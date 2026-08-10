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
        var scr = new SystemChangeRequest("SRCR-00900", 0, project.Id, release.Id, "Oceanic", "P", "A", "S", "author", now);
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
    /// The reviews listing ordered by a DateTimeOffset in the database, which SQLite refuses to translate, so
    /// the endpoint returned 500 on every call. The client checked only `response.ok` before storing the
    /// result, so the workspace rendered "No test change reviews" — a broken queue that read as an empty one.
    /// Asserting the payload rather than the status, because a 200 carrying nothing is the failure this hides.
    /// </summary>
    [Fact]
    public async Task The_reviews_listing_returns_the_reviews_an_approved_change_created()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        var fixture = await SeedAsync(factory, ("lead.user", ProgramRole.TestLead));
        await LoginAsync(client, "lead.user");

        using var response = await client.GetAsync($"/api/releases/{fixture.ReleaseId}/test-change-reviews");
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {body}");

        var reviews = JsonDocument.Parse(body).RootElement;
        var items = reviews.GetProperty("items");
        Assert.NotEmpty(items.EnumerateArray());
        var review = items.EnumerateArray().First();
        Assert.Equal("System", review.GetProperty("discipline").GetString());
        Assert.Equal("Open", review.GetProperty("state").GetString());
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

    /// <summary>
    /// A procedure is introduced by a package, and by nothing else.
    ///
    /// The direct-create route is gone: a procedure written straight into the library had no change request
    /// behind it and no record of why it existed, which is not how a requirement is ever changed. Introducing
    /// one is a package's proposal now, exercised in full by the workspace journeys; what this holds is that
    /// no other door remains open, because a rule enforced in one place and bypassable in another is not a
    /// rule.
    /// </summary>
    [Fact]
    public async Task A_procedure_cannot_be_created_outside_a_package()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        var fixture = await SeedAsync(factory, ("eng.user", ProgramRole.TestEngineer));
        await LoginAsync(client, "eng.user");

        // Method not allowed rather than not found: the collection is still there to be read, and only the
        // verb that wrote to it is gone. Asserting the exact status is the point — a 404 here would mean the
        // route had been renamed rather than retired, and the door would still be open somewhere else.
        using var direct = await client.PostAsJsonAsync("/api/test-procedures", new { projectId = fixture.ProjectId });
        Assert.Equal(HttpStatusCode.MethodNotAllowed, direct.StatusCode);
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
            // Stated rather than defaulted: a SYSTP number is a System procedure, and it covers the System
            // requirement below. Left to the default it was a HighLevel procedure wearing a System number.
            var procedure = new TestProcedure(fixture.ProjectId, "SYSTP-00000999",
                "Exact retained decision evidence", "procedure.author", now, TestProcedureLevel.System);
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
            Assert.StartsWith("Legacy procedure SYSTP-00000999.02", selected.GetProperty("title").GetString());
            Assert.False(selected.GetProperty("titleIsExact").GetBoolean());
            Assert.True(selected.GetProperty("titleIsLegacy").GetBoolean());
            Assert.Contains("exact historical title was not recorded", selected.GetProperty("titleNote").GetString());
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

    [Fact]
    public async Task Test_engineer_self_claim_assigns_the_whole_test_change_request_atomically()
    {
        using var factory = new AeroLinkApiFactory();
        var fixture = await SeedAsync(factory,
            ("eng.user", ProgramRole.TestEngineer), ("reviewer.user", ProgramRole.Approver));

        Guid reviewId;
        using (var engineer = factory.CreateClient())
        {
            await LoginAsync(engineer, "eng.user");
            var reviews = await engineer.GetFromJsonAsync<JsonElement>(
                $"/api/releases/{fixture.ReleaseId}/test-change-reviews");
            var review = reviews.GetProperty("items")[0];
            reviewId = review.GetProperty("id").GetGuid();
            Assert.True(review.GetProperty("capabilities").GetProperty("canAssign").GetBoolean());

            using var claimed = await engineer.PostAsJsonAsync(
                $"/api/test-change-reviews/{reviewId}/assign", new { engineerId = "eng.user" });
            Assert.Equal(HttpStatusCode.OK, claimed.StatusCode);

            var refreshed = await engineer.GetFromJsonAsync<JsonElement>(
                $"/api/releases/{fixture.ReleaseId}/test-change-reviews");
            var refreshedItems = refreshed.GetProperty("items");
            Assert.Equal("eng.user", refreshedItems[0].GetProperty("assignedEngineerId").GetString());
            Assert.True(refreshedItems[0].GetProperty("capabilities").GetProperty("canDecide").GetBoolean());
            var work = await engineer.GetFromJsonAsync<JsonElement>(
                $"/api/my-work?projectId={fixture.ProjectId}&releaseId={fixture.ReleaseId}");
            Assert.Contains(work.GetProperty("tasks").EnumerateArray(), task =>
                task.GetProperty("route").GetString() == "testingCoverage" && task.GetProperty("id").GetGuid() == reviewId);
        }

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        Assert.Equal("eng.user", (await db.TestChangeReviews.SingleAsync(x => x.Id == reviewId)).AssignedEngineerId);
        var items = await db.VerificationImpactItems.Where(x => x.TestChangeReviewId == reviewId).ToListAsync();
        Assert.NotEmpty(items);
        Assert.All(items, item => Assert.Equal("eng.user", item.AssignedEngineerId));
        Assert.Contains(await db.SecurityAuditEvents.AsNoTracking().ToListAsync(), audit =>
            audit.EventType == "TestChangeReviewAssigned" && audit.Target == reviewId.ToString());
    }
}
