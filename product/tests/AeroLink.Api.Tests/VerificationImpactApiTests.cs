using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AeroLink.Domain.Baselines;
using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Identity;
using AeroLink.Domain.Programs;
using AeroLink.Domain.Releases;
using AeroLink.Domain.Requirements;
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
            if (role == ProgramRole.ConfigurationManager)
                db.Add(new ProjectLeadershipAssignment(program.Id, ProjectLeadershipPosition.ConfigurationManager,
                    account.Id, "test.setup", now));
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
        Assert.Equal("Draft", review.GetProperty("state").GetString());
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
    public async Task A_missing_retarget_target_requires_a_controlled_successor_before_link_existing_can_be_recorded()
    {
        using var factory = new AeroLinkApiFactory();
        var fixture = await SeedAsync(factory, ("cm.user", ProgramRole.ConfigurationManager), ("eng.user", ProgramRole.TestEngineer));
        using (var configurationManager = factory.CreateClient())
        {
            await LoginAsync(configurationManager, "cm.user");
            Assert.Equal(HttpStatusCode.OK,
                (await configurationManager.PostAsJsonAsync($"/api/baselines/{fixture.BaselineId}/freeze", new { })).StatusCode);
            Assert.Equal(HttpStatusCode.OK,
                (await configurationManager.PostAsJsonAsync($"/api/baselines/{fixture.BaselineId}/materialize-requirements", new { })).StatusCode);
        }
        Guid itemId;
        Guid targetRevisionId;
        Guid siblingRevisionId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var now = DateTimeOffset.UtcNow;
            var source = await db.SystemChangeRequests.SingleAsync();
            var review = await db.TestChangeReviews.SingleAsync();
            var original = await db.RequirementRevisions.SingleAsync();
            var targetArtifact = new RequirementArtifact(fixture.ProjectId, "SYSR-00000902", RequirementLevel.System, now);
            var target = new RequirementRevision(targetArtifact.Id, 0,
                "The FMS shall retain the retargeted waypoint.", "Retarget target", "Test",
                RequirementRevisionState.Active, source.Id, fixture.BaselineId, now);
            var siblingArtifact = new RequirementArtifact(fixture.ProjectId, "SYSR-00000903",
                RequirementLevel.System, now);
            var sibling = new RequirementRevision(siblingArtifact.Id, 0,
                "An active sibling-build requirement", "Not selected by this target build", "Test",
                RequirementRevisionState.Active, source.Id, fixture.BaselineId, now);
            db.AddRange(targetArtifact, target,
                new BaselineRequirementSelection(fixture.BaselineId, targetArtifact.Id, target.Id),
                siblingArtifact, sibling);
            var procedure = new TestProcedure(fixture.ProjectId, "SYSTP-00000998", "Retarget endpoint procedure",
                "procedure.author", now, TestProcedureLevel.System);
            var revision = new TestProcedureRevision(procedure.Id, 0, "Objective", "Preconditions",
                "Steps", "Expected", TestProcedureState.Approved, "procedure.author", now,
                effectiveBaselineId: fixture.BaselineId, parentKind: VerificationProcedureParentKind.Allocated);
            db.AddRange(procedure, revision,
                new BaselineTestProcedureSelection(fixture.BaselineId, procedure.Id, revision.Id),
                new TestRequirementCoverage(revision.Id, original.Id));
            var item = VerificationImpactItem.ForOrphanedProcedure(fixture.ProjectId, fixture.ReleaseId,
                source.Id, review.Id, procedure.Id, procedure.BaseNumber, now);
            db.Add(item);
            await db.SaveChangesAsync();
            itemId = item.Id;
            targetRevisionId = target.Id;
            siblingRevisionId = sibling.Id;
        }

        using var client = factory.CreateClient();
        await LoginAsync(client, "eng.user");
        using (var linkExisting = await client.PostAsJsonAsync($"/api/verification-impact/{itemId}/resolve",
                   new
                   {
                       outcome = "ProcedureRetargeted",
                       rationale = "The existing target was expected to be present.",
                       procedureChangeAction = "LinkExisting",
                       retargetedRequirementRevisionId = targetRevisionId
                   }))
        {
            Assert.Equal(HttpStatusCode.BadRequest, linkExisting.StatusCode);
            var body = await linkExisting.Content.ReadAsStringAsync();
            Assert.Contains("ModifyExisting", body, StringComparison.OrdinalIgnoreCase);
        }

        using (var staleLinkExisting = await client.PostAsJsonAsync($"/api/verification-impact/{itemId}/resolve",
                   new
                   {
                       outcome = "ProcedureRetargeted",
                       rationale = "An active sibling revision must not be accepted.",
                       procedureChangeAction = "LinkExisting",
                       retargetedRequirementRevisionId = siblingRevisionId
                   }))
        {
            Assert.Equal(HttpStatusCode.BadRequest, staleLinkExisting.StatusCode);
            Assert.Contains("target build", await staleLinkExisting.Content.ReadAsStringAsync(),
                StringComparison.OrdinalIgnoreCase);
        }

        using (var deferred = await client.PostAsJsonAsync($"/api/verification-impact/{itemId}/resolve",
                   new
                   {
                       outcome = "ProcedureRetargeted",
                       rationale = "The target will be created by the controlled successor.",
                       retargetedRequirementRevisionId = targetRevisionId
                   }))
        {
            Assert.Equal(HttpStatusCode.OK, deferred.StatusCode);
            var body = await deferred.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("Resolved", body.GetProperty("state").GetString());
            Assert.Equal("ModifyExisting", body.GetProperty("procedureChangeAction").GetString());
        }

        using var verify = factory.Services.CreateScope();
        var verifyDb = verify.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        Assert.DoesNotContain(await verifyDb.TestCoverage.AsNoTracking().ToListAsync(),
            x => x.RequirementRevisionId == targetRevisionId);
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
                "Steps", "Expected", TestProcedureState.Approved, "procedure.author", now,
                effectiveBaselineId: fixture.BaselineId,
                parentKind: VerificationProcedureParentKind.Allocated);
            db.AddRange(procedure, revision);
            db.TestCoverage.Add(new TestRequirementCoverage(revision.Id, requirementRevisionId));
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

    /// <summary>
    /// #726 blocker 3, API path: a software Procedure can carry multiple exact Case parents, and coverage
    /// work (resolve, reopen, re-confirm) must apply to ALL of them through the authoritative links — never
    /// one arbitrary Case chosen by provider or insertion order, and never a TestCoverage row against the
    /// software Procedure revision itself.
    /// </summary>
    [Fact]
    public async Task Resolving_a_software_procedure_with_two_case_parents_applies_coverage_to_both_through_the_api()
    {
        using var factory = new AeroLinkApiFactory(testLadderPolicy: ProcedureEnabledTestPolicy.Create());
        var now = DateTimeOffset.UtcNow;
        Guid projectId, baselineId, requirementRevisionId, itemId, procedureId,
            caseARevisionId, caseBRevisionId, procedureRevisionId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var program = new ProgramRecord("All Parent API Program", "APA");
            var project = new ProjectRecord(program.Id, "All Parent API Software", "All Parent API Product");
            var release = new SoftwareRelease(project.Id, "1.0", false);
            var baseline = new CandidateBaseline("SW-01.00", 0, project.Id, release.Id, null,
                "Candidate", "cm.test", now);
            var scr = new SystemChangeRequest("HLRCR-00933", 0, project.Id, release.Id,
                "All-parent authority", "P", "A", "S", "author", now,
                ChangeRequestType.Software, softwareLevel: RequirementLevel.HighLevel);
            scr.AddRequirementChange("author", "HLR-00000933", 0, RequirementLevel.HighLevel,
                RequirementChangeKind.Introduce, "The software shall sequence in all-parent builds.",
                "All-parent fixture authority.", "Analysis", now,
                attributesJson: "{\"derived\":true}");
            scr.SubmitForReview("author", [new ApproverSelection("reviewer", "Reviewer")], now);
            scr.ApproveActiveStage("reviewer", now);
            baseline.Select(scr, "cm.test", now);
            var engineer = new UserAccount("eng.user", "Engineer", "eng.user@example.test",
                IdentityService.HashPassword(AeroLinkApiFactory.MemberPassword), now);
            var cm = new UserAccount("cm.user", "Configuration Manager", "cm.user@example.test",
                IdentityService.HashPassword(AeroLinkApiFactory.MemberPassword), now);
            db.AddRange(program, project, release, baseline, scr, engineer, cm);
            db.Add(new ProgramMembership(engineer.Id, program.Id, ProgramRole.TestEngineer,
                "all-parent-api", now));
            db.Add(new ProgramMembership(cm.Id, program.Id, ProgramRole.ConfigurationManager,
                "all-parent-api", now));
            db.Add(new ProjectLeadershipAssignment(program.Id, ProjectLeadershipPosition.ConfigurationManager,
                cm.Id, "all-parent-api", now));
            await db.SaveChangesAsync();
            await scope.ServiceProvider.GetRequiredService<VerificationImpactService>()
                .RaiseForApprovedChangeRequestAsync(scr, now, default);
            await db.SaveChangesAsync();
            projectId = project.Id;
            baselineId = baseline.Id;
        }

        using (var cmClient = factory.CreateClient())
        {
            await LoginAsync(cmClient, "cm.user");
            Assert.Equal(HttpStatusCode.OK,
                (await cmClient.PostAsJsonAsync($"/api/baselines/{baselineId}/freeze", new { })).StatusCode);
            Assert.Equal(HttpStatusCode.OK,
                (await cmClient.PostAsJsonAsync($"/api/baselines/{baselineId}/materialize-requirements", new { })).StatusCode);
        }

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            requirementRevisionId = await db.RequirementRevisions.Select(x => x.Id).SingleAsync();
            itemId = (await db.VerificationImpactItems.SingleAsync()).Id;
            var caseA = new TestProcedure(projectId, "HLRTC-000933", "Parent case A",
                "test.engineer", now, TestProcedureLevel.HighLevel);
            var caseARevision = new TestProcedureRevision(caseA.Id, 0,
                "Verify A", "Preconditions", "Steps", "Expected",
                TestProcedureState.Approved, "test.engineer", now, effectiveBaselineId: baselineId,
                parentKind: VerificationProcedureParentKind.Allocated);
            var caseB = new TestProcedure(projectId, "HLRTC-000934", "Parent case B",
                "test.engineer", now, TestProcedureLevel.HighLevel);
            var caseBRevision = new TestProcedureRevision(caseB.Id, 0,
                "Verify B", "Preconditions", "Steps", "Expected",
                TestProcedureState.Approved, "test.engineer", now, effectiveBaselineId: baselineId,
                parentKind: VerificationProcedureParentKind.Allocated);
            var procedure = new TestProcedure(projectId, "HLRTP-000933", "Two-parent procedure",
                "test.engineer", now, TestProcedureLevel.HighLevel,
                artifactKind: VerificationArtifactKind.Procedure,
                parentKind: VerificationProcedureParentKind.Allocated);
            var procedureRevision = new TestProcedureRevision(procedure.Id, 0,
                "Execute both parents", "Procedure setup", "Procedure steps", "Expected observation",
                TestProcedureState.Draft, "test.engineer", now,
                environmentSetup: "Setup", testData: "Data", orderedSteps: "Steps",
                expectedObservations: "Expected", cleanup: "Cleanup", toolingAutomation: "Tooling",
                parentKind: VerificationProcedureParentKind.Allocated);
            db.AddRange(caseA, caseARevision, caseB, caseBRevision, procedure, procedureRevision,
                new TestRequirementCoverage(caseARevision.Id, requirementRevisionId),
                new TestRequirementCoverage(caseBRevision.Id, requirementRevisionId),
                // Reversed insertion order: B is linked before A. Coverage work must never depend on it.
                new TestCaseProcedureLink(caseBRevision.Id, procedureRevision.Id),
                new TestCaseProcedureLink(caseARevision.Id, procedureRevision.Id),
                new BaselineTestProcedureSelection(baselineId, caseA.Id, caseARevision.Id),
                new BaselineTestProcedureSelection(baselineId, caseB.Id, caseBRevision.Id),
                new BaselineTestProcedureSelection(baselineId, procedure.Id, procedureRevision.Id));
            await db.SaveChangesAsync();
            db.Entry(procedureRevision).Property(x => x.State).CurrentValue = TestProcedureState.Approved;
            await db.SaveChangesAsync();
            procedureId = procedure.Id;
            caseARevisionId = caseARevision.Id;
            caseBRevisionId = caseBRevision.Id;
            procedureRevisionId = procedureRevision.Id;
        }

        using (var client = factory.CreateClient())
        {
            await LoginAsync(client, "eng.user");
            using var resolved = await client.PostAsJsonAsync($"/api/verification-impact/{itemId}/resolve", new
            {
                outcome = "ProcedureCoverageConfirmed",
                rationale = "The two-parent procedure executes both exact source Cases.",
                procedureId
            });
            Assert.Equal(HttpStatusCode.OK, resolved.StatusCode);
        }

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            Assert.Equal(2, await db.TestCoverage.AsNoTracking().CountAsync(x =>
                x.RequirementRevisionId == requirementRevisionId && !x.IsSuspect));
            Assert.Equal(2, await db.TestCoverage.AsNoTracking().CountAsync(x =>
                x.ProcedureRevisionId == caseARevisionId || x.ProcedureRevisionId == caseBRevisionId));
            Assert.Equal(0, await db.TestCoverage.AsNoTracking().CountAsync(x =>
                x.ProcedureRevisionId == procedureRevisionId));
        }

        using (var client = factory.CreateClient())
        {
            await LoginAsync(client, "eng.user");
            using var reopened = await client.PostAsJsonAsync($"/api/verification-impact/{itemId}/reopen",
                new { rationale = "Reopen both exact parent Cases." });
            Assert.Equal(HttpStatusCode.OK, reopened.StatusCode);
            using var resolvedAgain = await client.PostAsJsonAsync($"/api/verification-impact/{itemId}/resolve", new
            {
                outcome = "ProcedureCoverageConfirmed",
                rationale = "Re-confirmed against both exact source Cases.",
                procedureId
            });
            Assert.Equal(HttpStatusCode.OK, resolvedAgain.StatusCode);
        }

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            Assert.Equal(2, await db.TestCoverage.AsNoTracking().CountAsync(x =>
                x.RequirementRevisionId == requirementRevisionId && !x.IsSuspect));
            Assert.Equal(2, await db.TestCoverage.AsNoTracking().CountAsync(x =>
                x.ProcedureRevisionId == caseARevisionId || x.ProcedureRevisionId == caseBRevisionId));
            Assert.Equal(0, await db.TestCoverage.AsNoTracking().CountAsync(x =>
                x.ProcedureRevisionId == procedureRevisionId));
        }
    }

    /// <summary>
    /// #726 blocker 4, API path with a missing parent: when one exact source Case has no TestCoverage row,
    /// resolve/reopen/re-resolve confirm the parent that exists and deterministically defer the missing one
    /// to a controlled successor — never creating a row against the software Procedure and never depending on
    /// link insertion order.
    /// </summary>
    [Fact]
    public async Task Missing_case_parent_is_deferred_and_never_becomes_a_procedure_row_through_the_api()
    {
        using var factory = new AeroLinkApiFactory(testLadderPolicy: ProcedureEnabledTestPolicy.Create());
        var now = DateTimeOffset.UtcNow;
        Guid projectId, baselineId, requirementRevisionId, itemId, procedureId,
            caseARevisionId, caseBRevisionId, procedureRevisionId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var program = new ProgramRecord("Missing Parent API Program", "MPA");
            var project = new ProjectRecord(program.Id, "Missing Parent API Software", "Missing Parent API Product");
            var release = new SoftwareRelease(project.Id, "1.0", false);
            var baseline = new CandidateBaseline("SW-01.00", 0, project.Id, release.Id, null,
                "Candidate", "cm.test", now);
            var scr = new SystemChangeRequest("HLRCR-00934", 0, project.Id, release.Id,
                "Missing-parent authority", "P", "A", "S", "author", now,
                ChangeRequestType.Software, softwareLevel: RequirementLevel.HighLevel);
            scr.AddRequirementChange("author", "HLR-00000934", 0, RequirementLevel.HighLevel,
                RequirementChangeKind.Introduce, "The software shall sequence in missing-parent builds.",
                "Missing-parent fixture authority.", "Analysis", now,
                attributesJson: "{\"derived\":true}");
            scr.SubmitForReview("author", [new ApproverSelection("reviewer", "Reviewer")], now);
            scr.ApproveActiveStage("reviewer", now);
            baseline.Select(scr, "cm.test", now);
            var engineer = new UserAccount("eng.user", "Engineer", "eng.user@example.test",
                IdentityService.HashPassword(AeroLinkApiFactory.MemberPassword), now);
            var cm = new UserAccount("cm.user", "Configuration Manager", "cm.user@example.test",
                IdentityService.HashPassword(AeroLinkApiFactory.MemberPassword), now);
            db.AddRange(program, project, release, baseline, scr, engineer, cm);
            db.Add(new ProgramMembership(engineer.Id, program.Id, ProgramRole.TestEngineer,
                "missing-parent-api", now));
            db.Add(new ProgramMembership(cm.Id, program.Id, ProgramRole.ConfigurationManager,
                "missing-parent-api", now));
            db.Add(new ProjectLeadershipAssignment(program.Id, ProjectLeadershipPosition.ConfigurationManager,
                cm.Id, "missing-parent-api", now));
            await db.SaveChangesAsync();
            await scope.ServiceProvider.GetRequiredService<VerificationImpactService>()
                .RaiseForApprovedChangeRequestAsync(scr, now, default);
            await db.SaveChangesAsync();
            projectId = project.Id;
            baselineId = baseline.Id;
        }

        using (var cmClient = factory.CreateClient())
        {
            await LoginAsync(cmClient, "cm.user");
            Assert.Equal(HttpStatusCode.OK,
                (await cmClient.PostAsJsonAsync($"/api/baselines/{baselineId}/freeze", new { })).StatusCode);
            Assert.Equal(HttpStatusCode.OK,
                (await cmClient.PostAsJsonAsync($"/api/baselines/{baselineId}/materialize-requirements", new { })).StatusCode);
        }

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            requirementRevisionId = await db.RequirementRevisions.Select(x => x.Id).SingleAsync();
            itemId = (await db.VerificationImpactItems.SingleAsync()).Id;
            var caseA = new TestProcedure(projectId, "HLRTC-000934", "Parent case A",
                "test.engineer", now, TestProcedureLevel.HighLevel);
            var caseARevision = new TestProcedureRevision(caseA.Id, 0,
                "Verify A", "Preconditions", "Steps", "Expected",
                TestProcedureState.Approved, "test.engineer", now, effectiveBaselineId: baselineId,
                parentKind: VerificationProcedureParentKind.Allocated);
            var caseB = new TestProcedure(projectId, "HLRTC-000935", "Parent case B",
                "test.engineer", now, TestProcedureLevel.HighLevel);
            var caseBRevision = new TestProcedureRevision(caseB.Id, 0,
                "Verify B", "Preconditions", "Steps", "Expected",
                TestProcedureState.Approved, "test.engineer", now, effectiveBaselineId: baselineId,
                parentKind: VerificationProcedureParentKind.Derived,
                derivedRationale: "Missing-parent fixture Case without coverage.");
            var procedure = new TestProcedure(projectId, "HLRTP-000934", "Two-parent procedure",
                "test.engineer", now, TestProcedureLevel.HighLevel,
                artifactKind: VerificationArtifactKind.Procedure,
                parentKind: VerificationProcedureParentKind.Allocated);
            var procedureRevision = new TestProcedureRevision(procedure.Id, 0,
                "Execute both parents", "Procedure setup", "Procedure steps", "Expected observation",
                TestProcedureState.Draft, "test.engineer", now,
                environmentSetup: "Setup", testData: "Data", orderedSteps: "Steps",
                expectedObservations: "Expected", cleanup: "Cleanup", toolingAutomation: "Tooling",
                parentKind: VerificationProcedureParentKind.Allocated);
            db.AddRange(caseA, caseARevision, caseB, caseBRevision, procedure, procedureRevision,
                new TestRequirementCoverage(caseARevision.Id, requirementRevisionId),
                // Reversed insertion order: B is linked before A.
                new TestCaseProcedureLink(caseBRevision.Id, procedureRevision.Id),
                new TestCaseProcedureLink(caseARevision.Id, procedureRevision.Id),
                new BaselineTestProcedureSelection(baselineId, caseA.Id, caseARevision.Id),
                new BaselineTestProcedureSelection(baselineId, caseB.Id, caseBRevision.Id),
                new BaselineTestProcedureSelection(baselineId, procedure.Id, procedureRevision.Id));
            await db.SaveChangesAsync();
            db.Entry(procedureRevision).Property(x => x.State).CurrentValue = TestProcedureState.Approved;
            await db.SaveChangesAsync();
            procedureId = procedure.Id;
            caseARevisionId = caseARevision.Id;
            caseBRevisionId = caseBRevision.Id;
            procedureRevisionId = procedureRevision.Id;
        }

        using (var client = factory.CreateClient())
        {
            await LoginAsync(client, "eng.user");
            using var resolved = await client.PostAsJsonAsync($"/api/verification-impact/{itemId}/resolve", new
            {
                outcome = "ProcedureCoverageConfirmed",
                rationale = "Only parent A has an exact link; B is deferred to a controlled successor.",
                procedureId
            });
            Assert.Equal(HttpStatusCode.OK, resolved.StatusCode);
        }

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var aRow = await db.TestCoverage.AsNoTracking().SingleAsync(x =>
                x.ProcedureRevisionId == caseARevisionId
                && x.RequirementRevisionId == requirementRevisionId);
            Assert.False(aRow.IsSuspect);
            Assert.Equal(0, await db.TestCoverage.AsNoTracking().CountAsync(x =>
                x.ProcedureRevisionId == caseBRevisionId
                && x.RequirementRevisionId == requirementRevisionId));
            Assert.Equal(0, await db.TestCoverage.AsNoTracking().CountAsync(x =>
                x.ProcedureRevisionId == procedureRevisionId));
        }

        using (var client = factory.CreateClient())
        {
            await LoginAsync(client, "eng.user");
            using var reopened = await client.PostAsJsonAsync($"/api/verification-impact/{itemId}/reopen",
                new { rationale = "Reopen the effective parent only." });
            Assert.Equal(HttpStatusCode.OK, reopened.StatusCode);
            using var resolvedAgain = await client.PostAsJsonAsync($"/api/verification-impact/{itemId}/resolve", new
            {
                outcome = "ProcedureCoverageConfirmed",
                rationale = "Re-confirmed for the exact source Case that has a row.",
                procedureId
            });
            Assert.Equal(HttpStatusCode.OK, resolvedAgain.StatusCode);
        }

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var aRow = await db.TestCoverage.AsNoTracking().SingleAsync(x =>
                x.ProcedureRevisionId == caseARevisionId
                && x.RequirementRevisionId == requirementRevisionId);
            Assert.False(aRow.IsSuspect);
            Assert.Equal("eng.user", aRow.ConfirmedBy);
            Assert.Equal(0, await db.TestCoverage.AsNoTracking().CountAsync(x =>
                x.ProcedureRevisionId == caseBRevisionId
                && x.RequirementRevisionId == requirementRevisionId));
            Assert.Equal(0, await db.TestCoverage.AsNoTracking().CountAsync(x =>
                x.ProcedureRevisionId == procedureRevisionId));
        }
    }

    /// <summary>
    /// #726 Blocker 3, real HTTP seams: a historical migration mirror preserved in the Retired state (the
    /// post-cutover shape for an Approved Case revision that retains an old EffectiveBaselineId but is no
    /// longer selected) must be refused by verification-impact selection, direct BuildTestSet inclusion,
    /// execution, and retest — while the current effective revision of the same project still works.
    /// </summary>
    [Fact]
    public async Task Dormant_historical_procedure_revisions_are_refused_at_every_runtime_seam_through_the_api()
    {
        using var factory = new AeroLinkApiFactory(testLadderPolicy: ProcedureEnabledTestPolicy.Create());
        var now = DateTimeOffset.UtcNow;
        Guid projectId, releaseId, baselineId, requirementRevisionId, itemId, buildId,
            dormantArtifactId, dormantRevisionId, currentArtifactId, currentRevisionId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var program = new ProgramRecord("Dormant HTTP Program", "DHP");
            var project = new ProjectRecord(program.Id, "Dormant HTTP Software", "Dormant HTTP Product");
            var release = new SoftwareRelease(project.Id, "1.0", false);
            var baseline = new CandidateBaseline("SW-01.00", 0, project.Id, release.Id, null,
                "Candidate", "cm.test", now);
            var scr = new SystemChangeRequest("HLRCR-00940", 0, project.Id, release.Id,
                "Dormant HTTP authority", "P", "A", "S", "author", now,
                ChangeRequestType.Software, softwareLevel: RequirementLevel.HighLevel);
            scr.AddRequirementChange("author", "HLR-00000940", 0, RequirementLevel.HighLevel,
                RequirementChangeKind.Introduce, "The software shall sequence in dormant-http builds.",
                "Dormant HTTP fixture authority.", "Analysis", now,
                attributesJson: "{\"derived\":true}");
            scr.SubmitForReview("author", [new ApproverSelection("reviewer", "Reviewer")], now);
            scr.ApproveActiveStage("reviewer", now);
            baseline.Select(scr, "cm.test", now);
            var engineer = new UserAccount("eng.user", "Engineer", "eng.user@example.test",
                IdentityService.HashPassword(AeroLinkApiFactory.MemberPassword), now);
            var cm = new UserAccount("cm.user", "Configuration Manager", "cm.user@example.test",
                IdentityService.HashPassword(AeroLinkApiFactory.MemberPassword), now);
            db.AddRange(program, project, release, baseline, scr, engineer, cm);
            db.Add(new ProgramMembership(engineer.Id, program.Id, ProgramRole.TestEngineer,
                "dormant-http", now));
            db.Add(new ProgramMembership(engineer.Id, program.Id, ProgramRole.TestLead,
                "dormant-http", now));
            db.Add(new ProgramMembership(cm.Id, program.Id, ProgramRole.ConfigurationManager,
                "dormant-http", now));
            db.Add(new ProjectLeadershipAssignment(program.Id, ProjectLeadershipPosition.ConfigurationManager,
                cm.Id, "dormant-http", now));
            await db.SaveChangesAsync();
            await scope.ServiceProvider.GetRequiredService<VerificationImpactService>()
                .RaiseForApprovedChangeRequestAsync(scr, now, default);
            await db.SaveChangesAsync();
            projectId = project.Id;
            releaseId = release.Id;
            baselineId = baseline.Id;
        }

        using (var cmClient = factory.CreateClient())
        {
            await LoginAsync(cmClient, "cm.user");
            Assert.Equal(HttpStatusCode.OK,
                (await cmClient.PostAsJsonAsync($"/api/baselines/{baselineId}/freeze", new { })).StatusCode);
            Assert.Equal(HttpStatusCode.OK,
                (await cmClient.PostAsJsonAsync($"/api/baselines/{baselineId}/materialize-requirements", new { })).StatusCode);
        }

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            requirementRevisionId = await db.RequirementRevisions.Select(x => x.Id).SingleAsync();
            itemId = (await db.VerificationImpactItems.SingleAsync()).Id;
            var build = new SoftwareBuild(projectId, releaseId, baselineId, "B-DRM",
                "Dormant HTTP build", "cm.test", now);
            // Case A: Approved .00 with historical EffectiveBaselineId, selected in NO current baseline —
            // fully dormant, so its post-cutover mirror is Retired with no link and no selection.
            var caseA = new TestProcedure(projectId, "HLRTC-000940", "Dormant case A",
                "test.engineer", now, TestProcedureLevel.HighLevel);
            var caseARevision = new TestProcedureRevision(caseA.Id, 0,
                "Verify A", "Preconditions", "Steps", "Expected",
                TestProcedureState.Approved, "test.engineer", now, effectiveBaselineId: baselineId,
                parentKind: VerificationProcedureParentKind.Allocated);
            // Case B: .00 selected historically, .01 is the current selection — .00 becomes a Retired
            // mirror, .01 becomes the Approved executable mirror.
            var caseB = new TestProcedure(projectId, "HLRTC-000941", "Current case B",
                "test.engineer", now, TestProcedureLevel.HighLevel);
            var caseB00 = new TestProcedureRevision(caseB.Id, 0,
                "Verify B v1", "Preconditions", "Steps", "Expected",
                TestProcedureState.Approved, "test.engineer", now, effectiveBaselineId: baselineId,
                parentKind: VerificationProcedureParentKind.Allocated);
            var caseB01 = new TestProcedureRevision(caseB.Id, 1,
                "Verify B v2", "Preconditions v2", "Steps v2", "Expected v2",
                TestProcedureState.Approved, "test.engineer", now.AddDays(1),
                effectiveBaselineId: baselineId,
                parentKind: VerificationProcedureParentKind.Allocated);
            var dormantArtifact = new TestProcedure(projectId, "HLRTP-000940", "Dormant mirror A",
                "aerolink-migration", now, TestProcedureLevel.HighLevel,
                artifactKind: VerificationArtifactKind.Procedure,
                parentKind: VerificationProcedureParentKind.Allocated);
            var dormantRevision = new TestProcedureRevision(dormantArtifact.Id, 0,
                "Historical mirror", "", "", "",
                TestProcedureState.Retired, "aerolink-migration", now,
                parentKind: VerificationProcedureParentKind.Allocated,
                retirementRationale: "Superseded historical mirror; no executable claim.");
            var currentArtifact = new TestProcedure(projectId, "HLRTP-000941", "Current mirror B",
                "aerolink-migration", now, TestProcedureLevel.HighLevel,
                artifactKind: VerificationArtifactKind.Procedure,
                parentKind: VerificationProcedureParentKind.Allocated);
            var retiredMirror = new TestProcedureRevision(currentArtifact.Id, 0,
                "Historical mirror", "", "", "",
                TestProcedureState.Retired, "aerolink-migration", now,
                parentKind: VerificationProcedureParentKind.Allocated,
                retirementRationale: "Superseded by .01 before the cutover.");
            var currentRevision = new TestProcedureRevision(currentArtifact.Id, 1,
                "Execute current case", "Procedure setup", "Procedure steps", "Expected observation",
                TestProcedureState.Approved, "aerolink-migration", now.AddDays(1),
                effectiveBaselineId: baselineId,
                environmentSetup: "Setup", testData: "Data", orderedSteps: "Steps",
                expectedObservations: "Expected", cleanup: "Cleanup", toolingAutomation: "Tooling",
                parentKind: VerificationProcedureParentKind.Allocated);
            db.AddRange(caseA, caseARevision, caseB, caseB00, caseB01,
                dormantArtifact, dormantRevision, currentArtifact, retiredMirror, currentRevision, build,
                new TestRequirementCoverage(caseARevision.Id, requirementRevisionId),
                new TestRequirementCoverage(caseB00.Id, requirementRevisionId),
                new TestRequirementCoverage(caseB01.Id, requirementRevisionId),
                new TestCaseProcedureLink(caseB01.Id, currentRevision.Id),
                new BaselineTestProcedureSelection(baselineId, currentArtifact.Id, currentRevision.Id),
                new TestProcedureMigrationSource(projectId, caseARevision.Id,
                    dormantArtifact.Id, dormantRevision.Id),
                new TestProcedureMigrationSource(projectId, caseB00.Id,
                    currentArtifact.Id, retiredMirror.Id),
                new TestProcedureMigrationSource(projectId, caseB01.Id,
                    currentArtifact.Id, currentRevision.Id));
            await db.SaveChangesAsync();
            var baseline = await db.CandidateBaselines.SingleAsync(x => x.Id == baselineId);
            baseline.MarkTestProceduresMaterialized("cm.test", new string('b', 64), 1, now);
            await db.SaveChangesAsync();
            buildId = build.Id;
            dormantArtifactId = dormantArtifact.Id;
            dormantRevisionId = dormantRevision.Id;
            currentArtifactId = currentArtifact.Id;
            currentRevisionId = currentRevision.Id;
        }

        using (var client = factory.CreateClient())
        {
            await LoginAsync(client, "eng.user");

            // Verification-impact selection: the fully dormant artifact has no Approved revision.
            using var dormantResolve = await client.PostAsJsonAsync(
                $"/api/verification-impact/{itemId}/resolve", new
                {
                    outcome = "ProcedureCoverageConfirmed",
                    rationale = "The dormant mirror must not be selectable.",
                    procedureId = dormantArtifactId
                });
            Assert.Equal(HttpStatusCode.BadRequest, dormantResolve.StatusCode);
            // The current artifact resolves to its latest Approved revision and works.
            using var currentResolve = await client.PostAsJsonAsync(
                $"/api/verification-impact/{itemId}/resolve", new
                {
                    outcome = "ProcedureCoverageConfirmed",
                    rationale = "The current effective revision is selectable.",
                    procedureId = currentArtifactId
                });
            Assert.Equal(HttpStatusCode.OK, currentResolve.StatusCode);

            // Execution: the dormant revision is refused; the current revision executes.
            using var dormantExecution = await client.PostAsJsonAsync("/api/test-executions", new
            {
                projectId,
                artifactRevisionId = dormantRevisionId,
                softwareBuildId = buildId,
                retestOfExecutionId = (Guid?)null,
                outcome = "Pass",
                configuration = "Controlled rig",
                determination = "Must be refused.",
                evidenceReference = "evidence/dormant-refused.json",
                executedAt = now,
            });
            Assert.Equal(HttpStatusCode.BadRequest, dormantExecution.StatusCode);
            using var currentExecution = await client.PostAsJsonAsync("/api/test-executions", new
            {
                projectId,
                artifactRevisionId = currentRevisionId,
                softwareBuildId = buildId,
                retestOfExecutionId = (Guid?)null,
                outcome = "Pass",
                configuration = "Controlled rig",
                determination = "The current revision executes.",
                evidenceReference = "evidence/current-pass.json",
                executedAt = now,
            });
            Assert.Equal(HttpStatusCode.Created, currentExecution.StatusCode);
            var currentExecutionId = (await currentExecution.Content.ReadFromJsonAsync<JsonElement>())
                .GetProperty("id").GetGuid();

            // Retest: the dormant revision can never be the subject of a retest.
            using var dormantRetest = await client.PostAsJsonAsync("/api/test-executions", new
            {
                projectId,
                artifactRevisionId = dormantRevisionId,
                softwareBuildId = buildId,
                retestOfExecutionId = currentExecutionId,
                outcome = "Pass",
                configuration = "Controlled rig",
                determination = "Must be refused.",
                evidenceReference = "evidence/dormant-retest-refused.json",
                executedAt = now,
            });
            Assert.Equal(HttpStatusCode.BadRequest, dormantRetest.StatusCode);

            // Direct BuildTestSet inclusion: the dormant revision is refused; the current one is accepted.
            using var dormantInclude = await client.PostAsJsonAsync(
                $"/api/releases/{releaseId}/test-sets/HighLevelSoftware/procedures", new
                {
                    artifactRevisionIds = new[] { dormantRevisionId },
                    reason = "Chosen",
                    note = "Must be refused."
                });
            Assert.Equal(HttpStatusCode.BadRequest, dormantInclude.StatusCode);
            using var currentInclude = await client.PostAsJsonAsync(
                $"/api/releases/{releaseId}/test-sets/HighLevelSoftware/procedures", new
                {
                    artifactRevisionIds = new[] { currentRevisionId },
                    reason = "Chosen",
                    note = "The current revision belongs in the build."
                });
            Assert.Equal(HttpStatusCode.OK, currentInclude.StatusCode);
        }

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            // Effectivity is the readiness boundary: the dormant artifact/revision is absent, the current
            // artifact resolves to the current revision. Readiness and satisfaction therefore cannot count
            // the historical mirror.
            var effectivity = await TestProcedureEffectivity.ForReleaseAsync(
                db, projectId, releaseId, default);
            Assert.NotNull(effectivity);
            Assert.True(effectivity.RevisionByProcedure.TryGetValue(currentArtifactId, out var effective));
            Assert.Equal(currentRevisionId, effective);
            Assert.False(effectivity.RevisionByProcedure.ContainsKey(dormantArtifactId));
            Assert.Equal(0, await db.BuildTestSetEntries.AsNoTracking().CountAsync(x =>
                x.ProcedureRevisionId == dormantRevisionId));
            Assert.Equal(1, await db.BuildTestSetEntries.AsNoTracking().CountAsync(x =>
                x.ProcedureRevisionId == currentRevisionId));
            Assert.Equal(3, await db.TestProcedureMigrationSources.AsNoTracking().CountAsync(x =>
                x.ProjectId == projectId));
            var dormantSource = await db.TestProcedureMigrationSources.AsNoTracking()
                .SingleAsync(x => x.GeneratedProcedureRevisionId == dormantRevisionId);
            Assert.Equal(dormantArtifactId, dormantSource.GeneratedProcedureArtifactId);
        }
    }
}
