using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AeroLink.Domain.Baselines;
using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Identity;
using AeroLink.Domain.Programs;
using AeroLink.Domain.Releases;
using AeroLink.Domain.Verification;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AeroLink.Api.Tests;

public sealed class VerificationImpactApiTests
{
    private sealed record Fixture(Guid ReleaseId, Guid BaselineId, Guid ProjectId);

    /// <summary>
    /// Builds a Program with one approved change request that introduces a requirement, plus a candidate
    /// baseline that selects it. The verification impact raised by that approval is what the tests exercise.
    /// </summary>
    private static async Task<Fixture> SeedAsync(AeroLinkApiFactory factory, params (string user, ProgramRole role)[] members)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        var now = DateTimeOffset.UtcNow;
        var program = new ProgramRecord("Verification Program", "VIP");
        var project = new ProjectRecord(program.Id, "Software", "Verification Software");
        var release = new SoftwareRelease(project.Id, "1.6", false);
        var scr = new SystemChangeRequest("SCR-00900", 0, project.Id, release.Id, "Oceanic", "P", "A", "S", "author", now);
        scr.AddRequirementChange("author", "SYSR-00000901", 0, RequirementLevel.System, RequirementChangeKind.Introduce,
            "The FMS shall sequence oceanic waypoints.", "New capability", "Analysis", now);
        scr.SubmitForReview("author", [new("reviewer", "Reviewer")], now);
        scr.ApproveActiveStage("reviewer", now);
        var baseline = new CandidateBaseline("SW-90.00", 0, project.Id, release.Id, null, "Candidate", "cm", now);
        baseline.Select(scr, "cm", now);
        db.AddRange(program, project, release, scr, baseline);

        foreach (var (user, role) in members)
        {
            var account = new UserAccount(user, user, $"{user}@example.test",
                IdentityService.HashPassword(AeroLinkApiFactory.MemberPassword), now);
            db.Add(account);
            db.Add(new ProgramMembership(account.Id, program.Id, role, "test.setup", now));
        }
        await db.SaveChangesAsync();

        // Raise the work exactly as change-request approval does.
        var impact = scope.ServiceProvider.GetRequiredService<VerificationImpactService>();
        var tracked = await db.SystemChangeRequests.Include(x => x.RequirementChanges).SingleAsync(x => x.Id == scr.Id);
        await impact.RaiseForApprovedChangeRequestAsync(tracked, now, default);
        await db.SaveChangesAsync();

        return new Fixture(release.Id, baseline.Id, project.Id);
    }

    private static async Task LoginAsync(HttpClient client, string user)
    {
        using var login = await client.PostAsJsonAsync("/api/auth/login", new { userName = user, password = AeroLinkApiFactory.MemberPassword });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        await SecurityBoundaryTests.AuthorizeMutationsAsync(client);
    }

    /// <summary>
    /// Freezing is deliberately unguarded, and the queue holds back release approval instead. Freezing then
    /// materializing is what creates the requirement revisions a test engineer needs before a procedure can
    /// exist, so gating the freeze would have withheld the test team's own inputs.
    /// </summary>
    [Fact]
    public async Task An_unresolved_verification_impact_leaves_freezing_alone_and_holds_release_approval()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        var fixture = await SeedAsync(factory,
            ("cm.user", ProgramRole.ConfigurationManager), ("lead.user", ProgramRole.TestLead), ("eng.user", ProgramRole.TestEngineer));

        await LoginAsync(client, "cm.user");
        using var frozen = await client.PostAsJsonAsync($"/api/baselines/{fixture.BaselineId}/freeze", new { });
        Assert.Equal(HttpStatusCode.OK, frozen.StatusCode);

        using (var engineer = factory.CreateClient())
        {
            await LoginAsync(engineer, "eng.user");
            var items = await engineer.GetFromJsonAsync<JsonElement>($"/api/releases/{fixture.ReleaseId}/verification-impact?outstandingOnly=true");
            Assert.Equal(1, items.GetArrayLength());
            var item = items[0];
            Assert.Equal("Analysis", item.GetProperty("declaredVerificationMethod").GetString());
            // The declared method alone never sufficed: verification still owes a confirmation.
            Assert.True(item.GetProperty("blocksBaselineApproval").GetBoolean());

            using var resolved = await engineer.PostAsJsonAsync($"/api/verification-impact/{item.GetProperty("id").GetGuid()}/resolve",
                new { outcome = "NoTestRequired", rationale = "Verified by analysis report AR-114." });
            Assert.Equal(HttpStatusCode.OK, resolved.StatusCode);
        }

        using (var engineer = factory.CreateClient())
        {
            await LoginAsync(engineer, "eng.user");
            var remaining = await engineer.GetFromJsonAsync<JsonElement>($"/api/releases/{fixture.ReleaseId}/verification-impact?outstandingOnly=true");
            Assert.Equal(0, remaining.GetArrayLength());
        }
    }

    [Fact]
    public async Task Procedure_authoring_requires_an_exact_materialized_requirement_revision()
    {
        using var factory = new AeroLinkApiFactory();
        var fixture = await SeedAsync(factory,
            ("cm.user", ProgramRole.ConfigurationManager), ("eng.user", ProgramRole.TestEngineer));

        using (var engineer = factory.CreateClient())
        {
            await LoginAsync(engineer, "eng.user");
            using var premature = await engineer.PostAsJsonAsync("/api/test-procedures", new
            {
                projectId = fixture.ProjectId,
                title = "Premature procedure",
                objective = "Must not bind before materialization.",
                preconditions = "None",
                steps = "Attempt authoring.",
                expectedResult = "The prerequisite is explicit.",
                requirementRevisionIds = Array.Empty<Guid>(),
                level = "System"
            });
            Assert.Equal(HttpStatusCode.BadRequest, premature.StatusCode);
            var refusal = await premature.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("materialized_requirement_required", refusal.GetProperty("code").GetString());
        }

        using (var configurationManager = factory.CreateClient())
        {
            await LoginAsync(configurationManager, "cm.user");
            using var freeze = await configurationManager.PostAsJsonAsync(
                $"/api/baselines/{fixture.BaselineId}/freeze", new { });
            Assert.Equal(HttpStatusCode.OK, freeze.StatusCode);
            using var materialize = await configurationManager.PostAsJsonAsync(
                $"/api/baselines/{fixture.BaselineId}/materialize-requirements", new { });
            Assert.Equal(HttpStatusCode.OK, materialize.StatusCode);
        }

        using (var engineer = factory.CreateClient())
        {
            await LoginAsync(engineer, "eng.user");
            var requirements = await engineer.GetFromJsonAsync<JsonElement>(
                $"/api/requirements?projectId={fixture.ProjectId}&baselineId={fixture.BaselineId}&scope=System&includeRetired=false&page=1&pageSize=10");
            var revisionId = requirements.GetProperty("items")[0].GetProperty("revisionId").GetGuid();
            using var created = await engineer.PostAsJsonAsync("/api/test-procedures", new
            {
                projectId = fixture.ProjectId,
                title = "Exact post-materialization procedure",
                objective = "Verify the exact controlled requirement.",
                preconditions = "Materialized configuration loaded.",
                steps = "Exercise the requirement.",
                expectedResult = "The required behavior is observed.",
                requirementRevisionIds = new[] { revisionId },
                level = "System"
            });
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        }
    }

    [Fact]
    public async Task Confirming_coverage_requires_an_approved_procedure_not_prose()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        var fixture = await SeedAsync(factory, ("eng.user", ProgramRole.TestEngineer));
        await LoginAsync(client, "eng.user");

        var items = await client.GetFromJsonAsync<JsonElement>($"/api/releases/{fixture.ReleaseId}/verification-impact");
        var id = items[0].GetProperty("id").GetGuid();

        using var noProcedure = await client.PostAsJsonAsync($"/api/verification-impact/{id}/resolve",
            new { outcome = "ProcedureCoverageConfirmed", rationale = "Trust me, it is covered." });
        Assert.Equal(HttpStatusCode.BadRequest, noProcedure.StatusCode);

        using var unknownProcedure = await client.PostAsJsonAsync($"/api/verification-impact/{id}/resolve",
            new { outcome = "ProcedureCoverageConfirmed", rationale = "Covered.", procedureId = Guid.NewGuid() });
        Assert.Equal(HttpStatusCode.BadRequest, unknownProcedure.StatusCode);
    }

    [Fact]
    public async Task Exact_procedure_evidence_survives_reload_and_reopen_preserves_history()
    {
        using var factory = new AeroLinkApiFactory();
        var fixture = await SeedAsync(factory,
            ("cm.user", ProgramRole.ConfigurationManager),
            ("lead.user", ProgramRole.TestLead),
            ("eng.user", ProgramRole.TestEngineer));

        using (var cm = factory.CreateClient())
        {
            await LoginAsync(cm, "cm.user");
            Assert.Equal(HttpStatusCode.OK,
                (await cm.PostAsJsonAsync($"/api/baselines/{fixture.BaselineId}/freeze", new { })).StatusCode);
            Assert.Equal(HttpStatusCode.OK,
                (await cm.PostAsJsonAsync($"/api/baselines/{fixture.BaselineId}/materialize-requirements", new { })).StatusCode);
        }

        Guid procedureId;
        Guid procedureRevisionId;
        Guid requirementRevisionId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            requirementRevisionId = await db.RequirementRevisions.Select(x => x.Id).SingleAsync();
            var now = DateTimeOffset.UtcNow;
            var procedure = new TestProcedure(fixture.ProjectId, "SYSTP-00000999",
                "Exact retained decision evidence", "procedure.author", now);
            var revision = new TestProcedureRevision(procedure.Id, 2, "Objective", "Configuration",
                "Steps", "Expected", TestProcedureState.Approved, "procedure.author", now);
            db.AddRange(procedure, revision);
            await db.SaveChangesAsync();
            procedureId = procedure.Id;
            procedureRevisionId = revision.Id;
        }

        Guid itemId;
        using (var lead = factory.CreateClient())
        {
            await LoginAsync(lead, "lead.user");
            var items = await lead.GetFromJsonAsync<JsonElement>(
                $"/api/releases/{fixture.ReleaseId}/verification-impact");
            itemId = items[0].GetProperty("id").GetGuid();
            using var assigned = await lead.PostAsJsonAsync(
                $"/api/verification-impact/{itemId}/assign", new { engineerId = "eng.user" });
            Assert.Equal(HttpStatusCode.OK, assigned.StatusCode);
        }

        using (var engineer = factory.CreateClient())
        {
            await LoginAsync(engineer, "eng.user");
            using var resolved = await engineer.PostAsJsonAsync($"/api/verification-impact/{itemId}/resolve", new
            {
                outcome = "ProcedureCoverageConfirmed",
                rationale = "The exact approved revision covers this materialized configuration.",
                procedureId
            });
            Assert.Equal(HttpStatusCode.OK, resolved.StatusCode);
            var body = await resolved.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(procedureRevisionId, body.GetProperty("resolvedProcedureRevisionId").GetGuid());
            var selected = body.GetProperty("resolvedProcedure");
            Assert.Equal("SYSTP-00000999.02", selected.GetProperty("displayNumber").GetString());
            Assert.Equal("Exact retained decision evidence", selected.GetProperty("title").GetString());
            Assert.Equal(requirementRevisionId,
                selected.GetProperty("configuration").GetProperty("requirementRevisionId").GetGuid());
            Assert.Equal("eng.user", body.GetProperty("resolvedBy").GetString());
            Assert.Equal("eng.user", body.GetProperty("assignedEngineerId").GetString());
            Assert.Equal(1, body.GetProperty("decisionHistory").GetArrayLength());

            var reloaded = await engineer.GetFromJsonAsync<JsonElement>(
                $"/api/releases/{fixture.ReleaseId}/verification-impact");
            Assert.Equal(procedureRevisionId,
                reloaded[0].GetProperty("resolvedProcedure").GetProperty("revisionId").GetGuid());

            using var reopened = await engineer.PostAsJsonAsync($"/api/verification-impact/{itemId}/reopen",
                new { rationale = "A changed interpretation requires a new verification decision." });
            Assert.Equal(HttpStatusCode.OK, reopened.StatusCode);
            var open = await reopened.Content.ReadFromJsonAsync<JsonElement>();
            Assert.True(open.GetProperty("blocksBaselineApproval").GetBoolean());
            Assert.Equal("Assigned", open.GetProperty("state").GetString());
            Assert.Equal(JsonValueKind.Null, open.GetProperty("resolvedProcedure").ValueKind);
            Assert.Equal(2, open.GetProperty("decisionHistory").GetArrayLength());
        }

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var historyRows = await db.VerificationImpactDecisionHistory.AsNoTracking()
                .Where(x => x.VerificationImpactItemId == itemId).ToListAsync();
            var histories = historyRows.OrderBy(x => x.OccurredAt).ToList();
            Assert.Equal([VerificationImpactHistoryAction.Resolved, VerificationImpactHistoryAction.Reopened],
                histories.Select(x => x.Action));
            Assert.Equal(procedureRevisionId, histories[0].ProcedureRevisionId);
            var coverage = await db.TestCoverage.AsNoTracking().SingleAsync(
                x => x.RequirementRevisionId == requirementRevisionId && x.ProcedureRevisionId == procedureRevisionId);
            Assert.True(coverage.IsSuspect);
        }
    }

    [Fact]
    public async Task Only_the_test_lead_distributes_work_and_only_verification_engineers_resolve_it()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        var fixture = await SeedAsync(factory,
            ("author.user", ProgramRole.Engineer), ("lead.user", ProgramRole.TestLead), ("eng.user", ProgramRole.TestEngineer));

        Guid id;
        using (var lead = factory.CreateClient())
        {
            await LoginAsync(lead, "lead.user");
            var items = await lead.GetFromJsonAsync<JsonElement>($"/api/releases/{fixture.ReleaseId}/verification-impact");
            id = items[0].GetProperty("id").GetGuid();

            using var assigned = await lead.PostAsJsonAsync($"/api/verification-impact/{id}/assign", new { engineerId = "eng.user" });
            Assert.Equal(HttpStatusCode.OK, assigned.StatusCode);
            var body = await assigned.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("Assigned", body.GetProperty("state").GetString());
            Assert.Equal("eng.user", body.GetProperty("assignedEngineerId").GetString());
            Assert.Equal("lead.user", body.GetProperty("assignedByLeadId").GetString());
            Assert.True(body.GetProperty("blocksBaselineApproval").GetBoolean());
        }

        // The requirement author cannot distribute the work, nor decide what it means for verification.
        await LoginAsync(client, "author.user");
        using var cannotAssign = await client.PostAsJsonAsync($"/api/verification-impact/{id}/assign", new { engineerId = "eng.user" });
        Assert.Equal(HttpStatusCode.Forbidden, cannotAssign.StatusCode);
        using var cannotResolve = await client.PostAsJsonAsync($"/api/verification-impact/{id}/resolve",
            new { outcome = "NoTestRequired", rationale = "I wrote it, it is fine." });
        Assert.Equal(HttpStatusCode.Forbidden, cannotResolve.StatusCode);
    }
}
