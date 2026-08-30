using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Identity;
using AeroLink.Domain.Programs;
using AeroLink.Domain.Releases;
using AeroLink.Domain.Requirements;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AeroLink.Api.Tests;

/// <summary>
/// A review sent to the wrong approver used to be unrecoverable through the product: the only route onward
/// was for that approver to act, which is precisely what cannot happen when they are the wrong person. The
/// domain has always supported cancelling and restarting; nothing exposed it.
/// </summary>
public sealed class RestartReviewApiTests
{
    private static async Task<(Guid ChangeRequestId, Guid ProjectId, Guid? WorkflowId)> SeedAsync(
        AeroLinkApiFactory factory, bool configured = false, bool modernConfigured = false)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        var now = DateTimeOffset.UtcNow;

        var program = new ProgramRecord("Restart Program", "RSP");
        var project = new ProjectRecord(program.Id, "Software", "Restart Software");
        var release = new SoftwareRelease(project.Id, "1.6", false);
        db.AddRange(program, project, release);
        await db.SaveChangesAsync();

        var approverRole = configured ? ProgramRole.SystemEngineer : ProgramRole.Approver;
        foreach (var (name, role) in new[] { ("author.user", ProgramRole.Engineer), ("wrong.user", approverRole), ("right.user", approverRole) })
        {
            var account = new UserAccount(name, name, $"{name}@example.test",
                IdentityService.HashPassword(AeroLinkApiFactory.MemberPassword), now);
            db.Add(account);
            db.Add(new ProgramMembership(account.Id, program.Id, role, "test.setup", now));
        }
        await db.SaveChangesAsync();

        ReviewWorkflow? workflow = null;
        if (configured)
        {
            workflow = new ReviewWorkflow(project.Id, "Frozen system board", ReviewSubject.System, ReviewMode.Parallel,
                [new("System engineering approval", ProgramRole.SystemEngineer, ReviewStageKind.Approval,
                    modernConfigured ? ReviewStageAuthorityKind.BaseRole : null)],
                "test.setup", now);
            workflow.Activate("test.setup", now);
            db.ReviewWorkflows.Add(workflow);
            await db.SaveChangesAsync();
        }

        var scr = new SystemChangeRequest("SRCR-00050", 0, project.Id, release.Id, "Oceanic routing", "P", "A", "S", "author.user", now);
        scr.AddRequirementChange("author.user", "SYSR-00000501", 0, RequirementLevel.System, RequirementChangeKind.Introduce,
            "The FMS shall sequence oceanic waypoints.", "New capability", "Test", now);
        scr.SubmitForReview("author.user", [new("wrong.user", "Wrong Approver", configured ? ProgramRole.SystemEngineer : ProgramRole.Approver)], now,
            workflow: workflow?.Specification());
        db.SystemChangeRequests.Add(scr);
        await db.SaveChangesAsync();
        return (scr.Id, project.Id, workflow?.Id);
    }

    private static async Task LoginAsync(HttpClient client, string userName)
    {
        var login = await client.PostAsJsonAsync("/api/auth/login", new { userName, password = AeroLinkApiFactory.MemberPassword });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        await SecurityBoundaryTests.AuthorizeMutationsAsync(client);
    }

    [Fact]
    public async Task The_author_cancels_a_misrouted_review_and_restarts_it_with_the_right_approver()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        var fixture = await SeedAsync(factory);
        await LoginAsync(client, "author.user");

        using var response = await client.PostAsJsonAsync($"/api/change-requests/{fixture.ChangeRequestId}/restart-review",
            new { reason = "Routed to the wrong discipline approver.", approvers = new[] { new { userId = "right.user" } } });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var detail = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("InReview", detail.GetProperty("state").GetString());

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        var scr = await db.SystemChangeRequests.AsNoTracking()
            .Include(x => x.ReviewCycles).ThenInclude(x => x.Steps)
            .Include(x => x.AuditEvents)
            .SingleAsync(x => x.Id == fixture.ChangeRequestId);

        // The misrouted cycle is retained and cancelled rather than erased, and the new cycle names the
        // corrected approver. History is the product's whole claim, so nothing may be rewritten.
        Assert.Equal(2, scr.ReviewCycles.Count);
        var active = scr.ReviewCycles.Single(x => x.CompletedAt is null);
        Assert.Equal("right.user", active.Steps.Single().ApproverId);
        Assert.Contains(scr.AuditEvents, x => x.EventType == "ReviewCancelledAndRestarted");
    }

    [Fact]
    public async Task Restart_keeps_the_historical_workflow_identity_and_stage_kind()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        var fixture = await SeedAsync(factory, configured: true);
        await LoginAsync(client, "author.user");

        using var response = await client.PostAsJsonAsync($"/api/change-requests/{fixture.ChangeRequestId}/restart-review",
            new { reason = "Routed to the wrong systems approver.", approvers = new[] { new { userId = "right.user" } } });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var detail = await response.Content.ReadFromJsonAsync<JsonElement>();

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        var scr = await db.SystemChangeRequests.AsNoTracking()
            .Include(x => x.ReviewCycles).ThenInclude(x => x.Steps)
            .SingleAsync(x => x.Id == fixture.ChangeRequestId);
        var active = scr.ReviewCycles.Single(x => x.CompletedAt is null);
        var step = active.Steps.Single();

        Assert.Equal(fixture.WorkflowId, active.WorkflowId);
        Assert.Equal(1, active.WorkflowVersion);
        Assert.Equal(ReviewMode.Parallel, active.Mode);
        Assert.Equal("Frozen system board", active.WorkflowName);
        Assert.Equal("System engineering approval", step.StageName);
        Assert.Equal(ReviewStageKind.Approval, step.StageKind);
        Assert.Equal(nameof(ProgramRole.SystemEngineer), step.Authority);
        var rightUserId = await db.UserAccounts.AsNoTracking().Where(x => x.UserName == "right.user")
            .Select(x => x.Id).SingleAsync();
        var programId = await db.Projects.AsNoTracking().Where(p => p.Id == fixture.ProjectId)
            .Select(p => p.ProgramId).SingleAsync();
        var rightMembership = await db.ProgramMemberships.AsNoTracking()
            .Where(x => x.ProgramId == programId)
            .Where(x => x.UserId == rightUserId && x.Role == ProgramRole.SystemEngineer && x.EndedAt == null)
            .OrderBy(x => x.Id).SingleAsync();
        Assert.Equal(nameof(ProjectAuthoritySource.DirectBaseRole), step.AuthoritySource?.ToString());
        Assert.Equal(rightMembership.Id, step.AuthoritySourceId);
        var projectedCycle = detail.GetProperty("reviewCycles").EnumerateArray()
            .Single(x => x.GetProperty("state").GetString() == nameof(ReviewCycleState.Active));
        var projectedStep = projectedCycle.GetProperty("steps").EnumerateArray().Single();
        Assert.Equal(step.Id, projectedStep.GetProperty("id").GetGuid());
        Assert.Equal(nameof(ReviewStageKind.Approval), projectedStep.GetProperty("stageKind").GetString());
        Assert.Equal(nameof(ProjectAuthoritySource.DirectBaseRole),
            projectedStep.GetProperty("authoritySource").GetString());
        Assert.Equal(rightMembership.Id, projectedStep.GetProperty("authoritySourceId").GetGuid());
        Assert.Equal(active.WorkflowId, projectedCycle.GetProperty("workflowId").GetGuid());
        Assert.Equal(active.WorkflowVersion, projectedCycle.GetProperty("workflowVersion").GetInt32());
        var notification = await db.UserNotifications.SingleAsync(x =>
            x.ArtifactId == fixture.ChangeRequestId && x.Recipient == "right.user");
        Assert.Equal("ApprovalActivated", notification.Type);
        Assert.Equal("Approve SRCR-00050.00", notification.Title);
        Assert.Contains("authorized to approve", notification.Detail);
    }

    [Fact]
    public async Task Configured_change_request_approval_freezes_modern_authority_and_signature_provenance()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        var fixture = await SeedAsync(factory, configured: true, modernConfigured: true);
        await LoginAsync(client, "author.user");

        // Restart onto the right person so the approval exercises a newly selected, explicit BaseRole
        // obligation rather than the legacy row seeded as the misrouted historical cycle.
        using var restarted = await client.PostAsJsonAsync($"/api/change-requests/{fixture.ChangeRequestId}/restart-review",
            new { reason = "Routed to the wrong systems approver.", approvers = new[] { new { userId = "right.user" } } });
        Assert.Equal(HttpStatusCode.OK, restarted.StatusCode);

        await LoginAsync(client, "right.user");
        using var approved = await client.PostAsJsonAsync($"/api/change-requests/{fixture.ChangeRequestId}/approve",
            new
            {
                password = AeroLinkApiFactory.MemberPassword,
                meaning = "I approve the exact controlled change request.",
                rationale = "The modern systems-engineering authority reviewed the exact package."
            });
        Assert.Equal(HttpStatusCode.OK, approved.StatusCode);
        var detail = await approved.Content.ReadFromJsonAsync<JsonElement>();

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        var scr = await db.SystemChangeRequests.AsNoTracking()
            .Include(x => x.ReviewCycles).ThenInclude(x => x.Steps)
            .SingleAsync(x => x.Id == fixture.ChangeRequestId);
        var cycle = scr.ReviewCycles.Single(x => x.WorkflowId == fixture.WorkflowId && x.Sequence == 2);
        var step = cycle.Steps.Single();
        var programId = await db.Projects.AsNoTracking().Where(x => x.Id == fixture.ProjectId)
            .Select(x => x.ProgramId).SingleAsync();
        var rightUserId = await db.UserAccounts.AsNoTracking().Where(x => x.UserName == "right.user")
            .Select(x => x.Id).SingleAsync();
        var membership = await db.ProgramMemberships.AsNoTracking()
            .Where(x => x.ProgramId == programId && x.UserId == rightUserId
                && x.Role == ProgramRole.SystemEngineer && x.EndedAt == null)
            .OrderBy(x => x.Id).SingleAsync();
        var signature = await db.ElectronicSignatures.AsNoTracking()
            .SingleAsync(x => x.ArtifactId == fixture.ChangeRequestId && x.Action == "Approval");

        Assert.Equal(ReviewStageKind.Approval, step.StageKind);
        Assert.Equal(nameof(ProjectAuthoritySource.DirectBaseRole), step.AuthoritySource?.ToString());
        Assert.Equal(membership.Id, step.AuthoritySourceId);
        Assert.Equal(fixture.WorkflowId, cycle.WorkflowId);
        Assert.Equal(1, cycle.WorkflowVersion);
        Assert.Equal("Approval", signature.Action);
        Assert.Equal(step.Id, signature.ReviewStepId);
        Assert.Equal(cycle.Sequence, signature.ReviewCycle);
        Assert.Equal(step.Position, signature.ReviewStepPosition);
        Assert.Equal(step.Authority, signature.Authority);
        Assert.Equal(step.AuthoritySource?.ToString(), signature.AuthoritySource);
        Assert.Equal(step.AuthoritySourceId, signature.AuthoritySourceId);
        Assert.Equal(cycle.WorkflowId, signature.WorkflowId);
        Assert.Equal(cycle.WorkflowVersion, signature.WorkflowVersion);

        var projectedCycle = detail.GetProperty("reviewCycles").EnumerateArray()
            .Single(x => x.GetProperty("id").GetGuid() == cycle.Id);
        var projectedStep = projectedCycle.GetProperty("steps").EnumerateArray().Single();
        Assert.Equal(step.Id, projectedStep.GetProperty("id").GetGuid());
        Assert.Equal(nameof(ReviewStageKind.Approval), projectedStep.GetProperty("stageKind").GetString());
        Assert.Equal(nameof(ProjectAuthoritySource.DirectBaseRole),
            projectedStep.GetProperty("authoritySource").GetString());
        Assert.Equal(membership.Id, projectedStep.GetProperty("authoritySourceId").GetGuid());
        Assert.Equal(cycle.WorkflowId, projectedCycle.GetProperty("workflowId").GetGuid());
        Assert.Equal(cycle.WorkflowVersion, projectedCycle.GetProperty("workflowVersion").GetInt32());

        var signatureProjection = (await client.GetFromJsonAsync<JsonElement>(
            $"/api/signatures?artifactId={fixture.ChangeRequestId}")).EnumerateArray()
            .Single(x => x.GetProperty("id").GetGuid() == signature.Id);
        Assert.Equal("Approval", signatureProjection.GetProperty("action").GetString());
        Assert.Equal(signature.Id, signatureProjection.GetProperty("id").GetGuid());
        Assert.Equal(step.Id, signatureProjection.GetProperty("reviewStepId").GetGuid());
        Assert.Equal(cycle.Sequence, signatureProjection.GetProperty("reviewCycle").GetInt32());
        Assert.Equal(step.Position, signatureProjection.GetProperty("reviewStepPosition").GetInt32());
        Assert.Equal(nameof(ProjectAuthoritySource.DirectBaseRole),
            signatureProjection.GetProperty("authoritySource").GetString());
        Assert.Equal(membership.Id, signatureProjection.GetProperty("authoritySourceId").GetGuid());
        Assert.Equal(cycle.WorkflowId, signatureProjection.GetProperty("workflowId").GetGuid());
        Assert.Equal(cycle.WorkflowVersion, signatureProjection.GetProperty("workflowVersion").GetInt32());
        Assert.False(signatureProjection.GetProperty("isLegacyAuthoritySource").GetBoolean());
    }

    [Fact]
    public async Task Someone_who_did_not_author_the_change_cannot_restart_its_review()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        var fixture = await SeedAsync(factory);
        await LoginAsync(client, "wrong.user");

        using var response = await client.PostAsJsonAsync($"/api/change-requests/{fixture.ChangeRequestId}/restart-review",
            new { reason = "I would rather someone else reviewed this.", approvers = new[] { new { userId = "right.user" } } });
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task A_restart_requires_a_recorded_reason_and_active_approvers()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        var fixture = await SeedAsync(factory);
        await LoginAsync(client, "author.user");

        using var noReason = await client.PostAsJsonAsync($"/api/change-requests/{fixture.ChangeRequestId}/restart-review",
            new { reason = "  ", approvers = new[] { new { userId = "right.user" } } });
        Assert.Equal(HttpStatusCode.BadRequest, noReason.StatusCode);

        using var unknownApprover = await client.PostAsJsonAsync($"/api/change-requests/{fixture.ChangeRequestId}/restart-review",
            new { reason = "Routed to the wrong discipline approver.", approvers = new[] { new { userId = "nobody.here" } } });
        Assert.Equal(HttpStatusCode.BadRequest, unknownApprover.StatusCode);
    }
}
