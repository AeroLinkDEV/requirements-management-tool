using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AeroLink.Domain.Baselines;
using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Identity;
using AeroLink.Domain.Programs;
using AeroLink.Domain.Requirements;
using AeroLink.Domain.Verification;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AeroLink.Api.Tests;

/// <summary>
/// Authoring the procedure decisions a test change request carries.
///
/// The test-side counterpart of authoring requirement changes inside a change request, and the surface the
/// workspace reads and writes.
/// </summary>
public sealed class TestProcedureAuthoringApiTests
{
    private sealed record Fixture(Guid ProjectId, Guid ReleaseId, Guid TcrId, Guid ReleasedTcrId,
        Guid RequirementRevisionId, Guid FoldedRequirementRevisionId, Guid UnrelatedRequirementRevisionId,
        Guid OtherBuildRequirementRevisionId, Guid WrongLevelRequirementRevisionId,
        Guid OtherProjectRequirementRevisionId);

    private static async Task<Fixture> SeedAsync(AeroLinkApiFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        var impact = scope.ServiceProvider.GetRequiredService<VerificationImpactService>();
        var now = DateTimeOffset.UtcNow;

        var program = new ProgramRecord("Procedure Program", "PRO");
        var project = new ProjectRecord(program.Id, "Software", "Procedure Software");
        var inWork = new SoftwareRelease(project.Id, "1.6", false);
        // Constructed unreleased and released afterwards: the constructor refuses a release that is born closed.
        var closed = new SoftwareRelease(project.Id, "1.5", false);
        db.AddRange(program, project, inWork, closed);

        SystemChangeRequest Approved(string number, string requirement, Guid releaseId)
        {
            var scr = new SystemChangeRequest(number, 0, project.Id, releaseId, "Oceanic", "P", "A", "S", "author", now);
            scr.AddRequirementChange("author", requirement, 0, RequirementLevel.System,
                RequirementChangeKind.Introduce, "The FMS shall sequence oceanic waypoints.", "New capability",
                "Test", now);
            scr.SubmitForReview("author", [new ApproverSelection("reviewer", "Reviewer")], now);
            scr.ApproveActiveStage("reviewer", now);
            return scr;
        }

        var open = Approved("SRCR-00920", "SYSR-00000921", inWork.Id);
        var folded = Approved("SRCR-00922", "SYSR-00000925", inWork.Id);
        var shipped = Approved("SRCR-00921", "SYSR-00000922", closed.Id);
        db.AddRange(open, folded, shipped);

        foreach (var (user, role) in new[]
                 {
                     ("procedure.engineer", ProgramRole.TestEngineer),
                     ("procedure.approver", ProgramRole.Approver),
                     ("procedure.outsider", ProgramRole.Engineer),
                 })
        {
            var account = new UserAccount(user, user, $"{user}@example.test",
                IdentityService.HashPassword(AeroLinkApiFactory.MemberPassword), now);
            db.Add(account);
            db.Add(new ProgramMembership(account.Id, program.Id, role, "test.setup", now));
        }
        await db.SaveChangesAsync();

        foreach (var id in new[] { open.Id, folded.Id, shipped.Id })
        {
            var tracked = await db.SystemChangeRequests.Include(x => x.RequirementChanges).SingleAsync(x => x.Id == id);
            await impact.RaiseForApprovedChangeRequestAsync(tracked, now, default);
        }
        await db.SaveChangesAsync();

        closed.MarkReleased(now);
        await db.SaveChangesAsync();

        var tcrId = await db.TestChangeReviews.Where(x => x.ChangeRequestId == open.Id).Select(x => x.Id).SingleAsync();
        var foldedTcrId = await db.TestChangeReviews.Where(x => x.ChangeRequestId == folded.Id).Select(x => x.Id).SingleAsync();
        var releasedTcrId = await db.TestChangeReviews.Where(x => x.ChangeRequestId == shipped.Id).Select(x => x.Id).SingleAsync();
        var primaryTcr = await db.TestChangeReviews.Include(x => x.AdditionalSources).SingleAsync(x => x.Id == tcrId);
        primaryTcr.IncludeChangeRequest("procedure.engineer", folded.Id, folded.DisplayNumber, now);
        var foldedImpact = await db.VerificationImpactItems.SingleAsync(x => x.TestChangeReviewId == foldedTcrId
            && x.RequirementChangeId != null);
        foldedImpact.MoveToReview(tcrId, now);
        // The requirement revision SRCR-00920 introduced, in this project and at System level — the approved
        // change whose impact raised this very package. A procedure introduced here verifies that, so the
        // seed carries the real thing rather than an invented identifier: the server checks a driving
        // revision exists, belongs to this project, and sits at the discipline's level, and refuses anything
        // that does not. An invented Guid is refused as nonexistent, which is exactly right of it.
        var baseline = new CandidateBaseline("SW-01.60", 0, project.Id, inWork.Id, null, "In work", "cm", now);
        var artifact = new RequirementArtifact(project.Id, "SYSR-00000921", RequirementLevel.System, now);
        var revision = new RequirementRevision(artifact.Id, 0, "The FMS shall sequence oceanic waypoints.",
            "New capability.", "Test", RequirementRevisionState.Active, open.Id, baseline.Id, now);
        var foldedArtifact = new RequirementArtifact(project.Id, "SYSR-00000925", RequirementLevel.System, now);
        var foldedRevision = new RequirementRevision(foldedArtifact.Id, 0,
            "The FMS shall sequence a second governed source.", "Folded source.", "Test",
            RequirementRevisionState.Active, folded.Id, baseline.Id, now);
        var unrelatedArtifact = new RequirementArtifact(project.Id, "SYSR-00000923", RequirementLevel.System, now);
        var unrelatedRevision = new RequirementRevision(unrelatedArtifact.Id, 0,
            "The FMS shall calculate an unrelated performance value.", "Different engineering scope.",
            "Test", RequirementRevisionState.Active, open.Id, baseline.Id, now);
        var wrongLevelArtifact = new RequirementArtifact(project.Id, "HLR-00000926", RequirementLevel.HighLevel, now);
        var wrongLevelRevision = new RequirementRevision(wrongLevelArtifact.Id, 0,
            "The software shall implement a lower-level detail.", "Wrong discipline.", "Test",
            RequirementRevisionState.Active, open.Id, baseline.Id, now);
        baseline.Select(open, "cm", now);
        baseline.Select(folded, "cm", now);
        baseline.Freeze("cm", now);
        baseline.MarkRequirementsMaterialized("cm", new string('a', 64), 4, now);
        var carriedProcedure = new TestProcedure(project.Id, "SYSTP-000900", "Carried oceanic procedure",
            "test.author", now, TestProcedureLevel.System);
        var carriedProcedureRevision = new TestProcedureRevision(carriedProcedure.Id, 0,
            "Verify existing oceanic behavior.", "Cruise.", "Execute the procedure.", "Behavior observed.",
            TestProcedureState.Approved, "test.author", now);
        baseline.MarkTestProceduresMaterialized("cm", new string('c', 64), 1, now);
        db.AddRange(baseline, artifact, revision, foldedArtifact, foldedRevision,
            unrelatedArtifact, unrelatedRevision, wrongLevelArtifact, wrongLevelRevision,
            carriedProcedure, carriedProcedureRevision,
            new BaselineRequirementSelection(baseline.Id, artifact.Id, revision.Id),
            new BaselineRequirementSelection(baseline.Id, foldedArtifact.Id, foldedRevision.Id),
            new BaselineRequirementSelection(baseline.Id, unrelatedArtifact.Id, unrelatedRevision.Id),
            new BaselineRequirementSelection(baseline.Id, wrongLevelArtifact.Id, wrongLevelRevision.Id),
            new BaselineTestProcedureSelection(baseline.Id, carriedProcedure.Id, carriedProcedureRevision.Id),
            new TestRequirementCoverage(carriedProcedureRevision.Id, revision.Id));
        var governedItem = await db.VerificationImpactItems.SingleAsync(x => x.TestChangeReviewId == tcrId
            && x.RequirementChangeId == open.RequirementChanges.Single().Id);
        governedItem.LinkRequirementRevision(revision.Id, now);
        foldedImpact.LinkRequirementRevision(foldedRevision.Id, now);

        // Same Project and level, but a distinct release/build. Project-and-level checks alone accept it.
        var otherBuildRelease = new SoftwareRelease(project.Id, "1.7", false, inWork.Id);
        var otherBuildBaseline = new CandidateBaseline("SW-01.70", 0, project.Id, otherBuildRelease.Id,
            baseline.Id, "Other build", "cm", now);
        var otherBuildArtifact = new RequirementArtifact(project.Id, "SYSR-00000924", RequirementLevel.System, now);
        var otherBuildRevision = new RequirementRevision(otherBuildArtifact.Id, 0,
            "The FMS shall implement a future-build-only capability.", "Other build.", "Test",
            RequirementRevisionState.Active, shipped.Id, otherBuildBaseline.Id, now);
        db.AddRange(otherBuildRelease, otherBuildBaseline, otherBuildArtifact, otherBuildRevision);

        // A requirement in a different project, so a cross-project link has something real to be refused for.
        var elsewhereProgram = new ProgramRecord("Other Program", "OTH");
        var elsewhereProject = new ProjectRecord(elsewhereProgram.Id, "Software", "Other Software");
        var elsewhereRelease = new SoftwareRelease(elsewhereProject.Id, "1.0", false);
        var elsewhereBaseline = new CandidateBaseline("SW-00.10", 0, elsewhereProject.Id, elsewhereRelease.Id, null, "Other", "cm", now);
        var elsewhereArtifact = new RequirementArtifact(elsewhereProject.Id, "SYSR-00000940", RequirementLevel.System, now);
        var elsewhereRevision = new RequirementRevision(elsewhereArtifact.Id, 0, "Another program shall do its own thing.",
            "Elsewhere.", "Test", RequirementRevisionState.Active, shipped.Id, elsewhereBaseline.Id, now);
        db.AddRange(elsewhereProgram, elsewhereProject, elsewhereRelease, elsewhereBaseline, elsewhereArtifact, elsewhereRevision);
        await db.SaveChangesAsync();

        return new(project.Id, inWork.Id, tcrId, releasedTcrId, revision.Id, foldedRevision.Id,
            unrelatedRevision.Id, otherBuildRevision.Id, wrongLevelRevision.Id, elsewhereRevision.Id);
    }

    private static async Task LoginAsync(HttpClient client, string user)
    {
        using var login = await client.PostAsJsonAsync("/api/auth/login",
            new { userName = user, password = AeroLinkApiFactory.MemberPassword });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        await SecurityBoundaryTests.AuthorizeMutationsAsync(client);
    }

    private static async Task ConcludeTestWorkRequiredAsync(HttpClient client, Guid tcrId)
    {
        using var response = await client.PostAsJsonAsync($"/api/test-change-reviews/{tcrId}/conclusion",
            new { testChangeRequired = true });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Introducing_a_procedure_allocates_its_number_rather_than_letting_the_caller_choose()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        var fixture = await SeedAsync(factory);
        await LoginAsync(client, "procedure.engineer");
        await ConcludeTestWorkRequiredAsync(client, fixture.TcrId);

        using var response = await client.PostAsJsonAsync(
            $"/api/test-change-reviews/{fixture.TcrId}/procedure-changes",
            new
            {
                kind = "Introduce", revision = 0, title = "Oceanic waypoint sequencing",
                objective = "Verify oceanic waypoints sequence in flight-plan order.",
                preconditions = "The aircraft is in cruise on an oceanic plan.",
                steps = "1. Load the plan. 2. Read the sequencer.",
                expectedResult = "The next eligible waypoint is sequenced.",
                rationale = "No procedure exercises oceanic sequencing after the approved change."
            });
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{(int)response.StatusCode}: {body}");

        // Allocated centrally, so two engineers proposing a new procedure at once cannot claim one number.
        var created = JsonSerializer.Deserialize<JsonElement>(body);
        Assert.Matches(@"^SYSTP-\d{6}\.00$", created.GetProperty("displayNumber").GetString()!);
        // The discipline fixes the level; the caller does not get to disagree with it.
        Assert.Equal("System", created.GetProperty("level").GetString());
    }

    [Fact]
    public async Task Nothing_can_be_proposed_until_the_assessment_has_asked_for_test_work()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        var fixture = await SeedAsync(factory);
        await LoginAsync(client, "procedure.engineer");

        using var response = await client.PostAsJsonAsync(
            $"/api/test-change-reviews/{fixture.TcrId}/procedure-changes",
            new { kind = "Introduce", revision = 0, title = "Premature", objective = "o", steps = "s", expectedResult = "e", rationale = "r" });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task A_modification_has_to_name_the_procedure_it_acts_on()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        var fixture = await SeedAsync(factory);
        await LoginAsync(client, "procedure.engineer");
        await ConcludeTestWorkRequiredAsync(client, fixture.TcrId);

        // Allocating a fresh number for a modification would silently turn it into a different procedure.
        using var response = await client.PostAsJsonAsync(
            $"/api/test-change-reviews/{fixture.TcrId}/procedure-changes",
            new { kind = "Modify", revision = 1, title = "Nameless", objective = "o", steps = "s", expectedResult = "e", rationale = "r" });
        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("name the procedure", body);
    }

    [Fact]
    public async Task The_workspace_reads_back_what_was_proposed_and_can_withdraw_it()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        var fixture = await SeedAsync(factory);
        await LoginAsync(client, "procedure.engineer");
        await ConcludeTestWorkRequiredAsync(client, fixture.TcrId);

        using var created = await client.PostAsJsonAsync(
            $"/api/test-change-reviews/{fixture.TcrId}/procedure-changes",
            new
            {
                kind = "Introduce", revision = 0, title = "Oceanic waypoint sequencing",
                objective = "Verify oceanic sequencing.", preconditions = "Cruise.",
                steps = "1. Load. 2. Read.", expectedResult = "Sequenced.", rationale = "Nothing covers it."
            });
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        var changeId = JsonSerializer.Deserialize<JsonElement>(await created.Content.ReadAsStringAsync())
            .GetProperty("id").GetGuid();

        var package = await client.GetFromJsonAsync<JsonElement>(
            $"/api/test-change-reviews/{fixture.TcrId}/procedure-changes");
        Assert.Equal("System", package.GetProperty("procedureLevel").GetString());
        Assert.Equal("ChangeRequired", package.GetProperty("outcome").GetString());
        var change = Assert.Single(package.GetProperty("procedureChanges").EnumerateArray());
        Assert.Equal("Oceanic waypoint sequencing", change.GetProperty("title").GetString());
        Assert.Equal("Introduce", change.GetProperty("kind").GetString());

        using var removed = await client.DeleteAsync(
            $"/api/test-change-reviews/{fixture.TcrId}/procedure-changes/{changeId}");
        Assert.Equal(HttpStatusCode.OK, removed.StatusCode);
        var after = await client.GetFromJsonAsync<JsonElement>(
            $"/api/test-change-reviews/{fixture.TcrId}/procedure-changes");
        Assert.Empty(after.GetProperty("procedureChanges").EnumerateArray());
    }

    /// <summary>
    /// The four things the server now proves about what a decision may name, and the one it proves about
    /// sending a package for review. All were previously accepted and failed later, or not at all.
    /// </summary>
    [Fact]
    public async Task A_decision_can_only_name_this_project_and_this_level()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        var fixture = await SeedAsync(factory);
        await LoginAsync(client, "procedure.engineer");
        await ConcludeTestWorkRequiredAsync(client, fixture.TcrId);

        async Task<(HttpStatusCode Status, string Body)> Propose(object body)
        {
            using var response = await client.PostAsJsonAsync(
                $"/api/test-change-reviews/{fixture.TcrId}/procedure-changes", body);
            return (response.StatusCode, await response.Content.ReadAsStringAsync());
        }

        // A modification must name a procedure that exists and belongs here.
        var unknown = await Propose(new { kind = "Modify", baseNumber = "SYSTP-999999", revision = 1,
            title = "T", objective = "o", steps = "s", expectedResult = "e", rationale = "r" });
        Assert.Equal(HttpStatusCode.BadRequest, unknown.Status);
        Assert.Contains("not a controlled test procedure", unknown.Body);

        // A driving requirement must be this project's. A cross-project identifier would otherwise become
        // controlled coverage at materialization and claim verification belonging to another program.
        var foreign = await Propose(new { kind = "Introduce", revision = 0, title = "T", objective = "o",
            steps = "s", expectedResult = "e", rationale = "r",
            drivingRequirementRevisionIds = new[] { fixture.OtherProjectRequirementRevisionId } });
        Assert.Equal(HttpStatusCode.BadRequest, foreign.Status);
        Assert.Contains("another project", foreign.Body);
        Assert.Equal("requirement_revision_project_mismatch",
            JsonSerializer.Deserialize<JsonElement>(foreign.Body).GetProperty("code").GetString());

        // And one that does not exist at all is refused rather than silently dropped at materialization.
        var missing = await Propose(new { kind = "Introduce", revision = 0, title = "T", objective = "o",
            steps = "s", expectedResult = "e", rationale = "r",
            drivingRequirementRevisionIds = new[] { Guid.NewGuid() } });
        Assert.Equal(HttpStatusCode.BadRequest, missing.Status);
        Assert.Contains("does not exist", missing.Body);
        Assert.Equal("requirement_revision_not_found",
            JsonSerializer.Deserialize<JsonElement>(missing.Body).GetProperty("code").GetString());
    }

    [Fact]
    public async Task A_decision_can_only_name_requirements_governed_by_its_package_and_target_build()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        var fixture = await SeedAsync(factory);
        await LoginAsync(client, "procedure.engineer");
        await ConcludeTestWorkRequiredAsync(client, fixture.TcrId);

        var workspace = await client.GetFromJsonAsync<JsonElement>(
            $"/api/test-change-reviews/{fixture.TcrId}/procedure-changes");
        var choices = workspace.GetProperty("drivingRequirementChoices").EnumerateArray()
            .Select(x => x.GetProperty("revisionId").GetGuid()).ToHashSet();
        Assert.True(choices.SetEquals([fixture.RequirementRevisionId, fixture.FoldedRequirementRevisionId]));

        async Task AssertOutsideScope(Guid revisionId)
        {
            int before;
            using (var scope = factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
                before = await db.Set<TestProcedureChange>().CountAsync(
                    x => x.TestChangeReviewId == fixture.TcrId);
            }
            using var response = await client.PostAsJsonAsync(
                $"/api/test-change-reviews/{fixture.TcrId}/procedure-changes",
                new
                {
                    kind = "Introduce", revision = 0, title = "Out-of-scope coverage",
                    objective = "Verify unrelated behavior.", steps = "Execute.",
                    expectedResult = "Observed.", rationale = "Direct API attempt.",
                    drivingRequirementRevisionIds = new[] { revisionId }
                });
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            var body = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
            Assert.Equal("requirement_revision_outside_tcr_scope", body.GetProperty("code").GetString());
            using var assertScope = factory.Services.CreateScope();
            var assertDb = assertScope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            Assert.Equal(before, await assertDb.Set<TestProcedureChange>().CountAsync(
                x => x.TestChangeReviewId == fixture.TcrId));
        }

        await AssertOutsideScope(fixture.UnrelatedRequirementRevisionId);
        await AssertOutsideScope(fixture.OtherBuildRequirementRevisionId);

        using (var wrongLevel = await client.PostAsJsonAsync(
                   $"/api/test-change-reviews/{fixture.TcrId}/procedure-changes",
                   new
                   {
                       kind = "Introduce", revision = 0, title = "Wrong-level coverage",
                       objective = "Verify wrong-level behavior.", steps = "Execute.", expectedResult = "Observed.",
                       rationale = "Direct API attempt.",
                       drivingRequirementRevisionIds = new[] { fixture.WrongLevelRequirementRevisionId }
                   }))
        {
            Assert.Equal(HttpStatusCode.BadRequest, wrongLevel.StatusCode);
            var body = JsonSerializer.Deserialize<JsonElement>(await wrongLevel.Content.ReadAsStringAsync());
            Assert.Equal("requirement_revision_level_mismatch", body.GetProperty("code").GetString());
        }

        using var valid = await client.PostAsJsonAsync(
            $"/api/test-change-reviews/{fixture.TcrId}/procedure-changes",
            new
            {
                kind = "Introduce", revision = 0, title = "Governed coverage",
                objective = "Verify governed behavior.", steps = "Execute.",
                expectedResult = "Observed.", rationale = "Package scope.",
                drivingRequirementRevisionIds = new[]
                    { fixture.RequirementRevisionId, fixture.FoldedRequirementRevisionId }
            });
        Assert.Equal(HttpStatusCode.OK, valid.StatusCode);
    }

    [Fact]
    public async Task A_modification_exposes_current_coverage_and_requires_rationale_for_a_governed_addition()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        var fixture = await SeedAsync(factory);
        await LoginAsync(client, "procedure.engineer");
        await ConcludeTestWorkRequiredAsync(client, fixture.TcrId);

        var workspace = await client.GetFromJsonAsync<JsonElement>(
            $"/api/test-change-reviews/{fixture.TcrId}/procedure-changes");
        var target = Assert.Single(workspace.GetProperty("procedureTargets").EnumerateArray());
        Assert.Equal("SYSTP-000900", target.GetProperty("baseNumber").GetString());
        var current = Assert.Single(target.GetProperty("currentCoverage").EnumerateArray());
        Assert.Equal(fixture.RequirementRevisionId, current.GetProperty("revisionId").GetGuid());

        object Proposal(string? rationale) => new
        {
            kind = "Modify", baseNumber = "SYSTP-000900", revision = 1, title = "Expanded coverage",
            objective = "Verify both governed requirements.", steps = "Execute.", expectedResult = "Observed.",
            rationale = "The approved change expands the procedure.",
            drivingRequirementRevisionIds = new[] { fixture.FoldedRequirementRevisionId },
            coverageChangeRationale = rationale
        };
        using var missingRationale = await client.PostAsJsonAsync(
            $"/api/test-change-reviews/{fixture.TcrId}/procedure-changes", Proposal(null));
        Assert.Equal(HttpStatusCode.BadRequest, missingRationale.StatusCode);
        Assert.Equal("coverage_delta_rationale_required",
            JsonSerializer.Deserialize<JsonElement>(await missingRationale.Content.ReadAsStringAsync())
                .GetProperty("code").GetString());

        using var accepted = await client.PostAsJsonAsync(
            $"/api/test-change-reviews/{fixture.TcrId}/procedure-changes",
            Proposal("The folded source adds a second verification obligation."));
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        var after = await client.GetFromJsonAsync<JsonElement>(
            $"/api/test-change-reviews/{fixture.TcrId}/procedure-changes");
        var decision = Assert.Single(after.GetProperty("procedureChanges").EnumerateArray());
        Assert.Equal(fixture.FoldedRequirementRevisionId,
            Assert.Single(decision.GetProperty("drivingRequirementRevisionIds").EnumerateArray()).GetGuid());
        Assert.Equal("The folded source adds a second verification obligation.",
            decision.GetProperty("coverageChangeRationale").GetString());
        Assert.Equal("procedure.engineer", decision.GetProperty("coverageChangedBy").GetString());
    }

    [Fact]
    public async Task A_coverage_removal_must_be_current_governed_and_rationalized()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        var fixture = await SeedAsync(factory);
        await LoginAsync(client, "procedure.engineer");
        await ConcludeTestWorkRequiredAsync(client, fixture.TcrId);

        async Task<JsonElement> Refused(Guid removed, string? rationale)
        {
            using var response = await client.PostAsJsonAsync(
                $"/api/test-change-reviews/{fixture.TcrId}/procedure-changes", new
                {
                    kind = "Modify", baseNumber = "SYSTP-000900", revision = 1, title = "Narrowed coverage",
                    objective = "Verify retained behavior.", steps = "Execute.", expectedResult = "Observed.",
                    rationale = "The procedure scope changes.", removedRequirementRevisionIds = new[] { removed },
                    coverageChangeRationale = rationale
                });
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            return JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
        }

        Assert.Equal("requirement_revision_outside_tcr_scope",
            (await Refused(fixture.UnrelatedRequirementRevisionId, "Remove unrelated coverage."))
            .GetProperty("code").GetString());
        Assert.Equal("coverage_removal_not_current",
            (await Refused(fixture.FoldedRequirementRevisionId, "Remove coverage not currently present."))
            .GetProperty("code").GetString());
        Assert.Equal("coverage_delta_rationale_required",
            (await Refused(fixture.RequirementRevisionId, null)).GetProperty("code").GetString());

        using var accepted = await client.PostAsJsonAsync(
            $"/api/test-change-reviews/{fixture.TcrId}/procedure-changes", new
            {
                kind = "Modify", baseNumber = "SYSTP-000900", revision = 1, title = "Narrowed coverage",
                objective = "Verify retained behavior.", steps = "Execute.", expectedResult = "Observed.",
                rationale = "The procedure scope changes.",
                removedRequirementRevisionIds = new[] { fixture.RequirementRevisionId },
                coverageChangeRationale = "The approved design retired this verification obligation."
            });
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
    }

    [Fact]
    public async Task A_package_naming_no_procedure_work_cannot_be_sent_for_review()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        var fixture = await SeedAsync(factory);
        await LoginAsync(client, "procedure.engineer");
        await ConcludeTestWorkRequiredAsync(client, fixture.TcrId);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            foreach (var item in await db.VerificationImpactItems.Where(x => x.TestChangeReviewId == fixture.TcrId).ToListAsync())
                item.Resolve("procedure.engineer", VerificationImpactOutcome.NoTestRequired, "Covered by analysis.", DateTimeOffset.UtcNow);
            await db.SaveChangesAsync();
        }

        // It concluded work was required and named none. The workspace says as much; this is that enforced.
        using var refused = await client.PostAsJsonAsync($"/api/test-change-reviews/{fixture.TcrId}/submit",
            new { approverId = "procedure.approver" });
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Contains("names none", await refused.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Someone_without_test_authority_cannot_propose_procedure_work()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        var fixture = await SeedAsync(factory);
        await LoginAsync(client, "procedure.outsider");

        using var response = await client.PostAsJsonAsync(
            $"/api/test-change-reviews/{fixture.TcrId}/procedure-changes",
            new { kind = "Introduce", revision = 0, title = "T", objective = "o", steps = "s", expectedResult = "e", rationale = "r" });
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task A_released_build_refuses_procedure_work()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        var fixture = await SeedAsync(factory);
        await LoginAsync(client, "procedure.engineer");

        using var propose = await client.PostAsJsonAsync(
            $"/api/test-change-reviews/{fixture.ReleasedTcrId}/procedure-changes",
            new { kind = "Introduce", revision = 0, title = "T", objective = "o", steps = "s", expectedResult = "e", rationale = "r" });
        Assert.Equal(HttpStatusCode.Conflict, propose.StatusCode);

        // Revising a released package is refused first as read-only; the "never approved" refusal is covered
        // by the too-early assertion in the approved-package journey below.
        using var revise = await client.PostAsJsonAsync(
            $"/api/test-change-reviews/{fixture.ReleasedTcrId}/revise", new { });
        Assert.Equal(HttpStatusCode.Conflict, revise.StatusCode);
        Assert.Contains("read-only", await revise.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task An_approved_package_advances_to_its_next_revision_carrying_its_work()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        var fixture = await SeedAsync(factory);
        await LoginAsync(client, "procedure.engineer");
        await ConcludeTestWorkRequiredAsync(client, fixture.TcrId);

        using var created = await client.PostAsJsonAsync(
            $"/api/test-change-reviews/{fixture.TcrId}/procedure-changes",
            new
            {
                kind = "Introduce", revision = 0, title = "Oceanic waypoint sequencing",
                objective = "Verify oceanic sequencing.", preconditions = "Cruise.",
                steps = "1. Load. 2. Read.", expectedResult = "Sequenced.", rationale = "Nothing covers it.",
                // Named, because this package is submitted below and submission refuses an introduced
                // procedure that verifies nothing.
                drivingRequirementRevisionIds = new[] { fixture.RequirementRevisionId }
            });
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);

        // Only an approved package can be revised, so the unapproved one is refused first.
        using var tooEarly = await client.PostAsJsonAsync($"/api/test-change-reviews/{fixture.TcrId}/revise", new { });
        Assert.Equal(HttpStatusCode.BadRequest, tooEarly.StatusCode);

        // A package cannot be submitted while its assessment items are still open, so answer them first.
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            foreach (var item in await db.VerificationImpactItems.Where(x => x.TestChangeReviewId == fixture.TcrId).ToListAsync())
                item.Resolve("procedure.engineer", VerificationImpactOutcome.NewProcedureRequired,
                    "The procedure proposed in this package will cover it.", DateTimeOffset.UtcNow);
            await db.SaveChangesAsync();
        }

        using var submit = await client.PostAsJsonAsync($"/api/test-change-reviews/{fixture.TcrId}/submit",
            new { approverId = "procedure.approver" });
        var submitBody = await submit.Content.ReadAsStringAsync();
        Assert.True(submit.StatusCode == HttpStatusCode.OK, $"{(int)submit.StatusCode}: {submitBody}");
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var review = await db.TestChangeReviews.SingleAsync(x => x.Id == fixture.TcrId);
            review.Approve("procedure.approver", "Procedure decisions are complete.", DateTimeOffset.UtcNow);
            await db.SaveChangesAsync();
        }

        using var revise = await client.PostAsJsonAsync($"/api/test-change-reviews/{fixture.TcrId}/revise", new { });
        var body = await revise.Content.ReadAsStringAsync();
        Assert.True(revise.StatusCode == HttpStatusCode.OK, $"{(int)revise.StatusCode}: {body}");

        var next = JsonSerializer.Deserialize<JsonElement>(body);
        Assert.Equal(1, next.GetProperty("revision").GetInt32());
        Assert.EndsWith(".01", next.GetProperty("displayNumber").GetString()!);
        // Carries the work forward, and stays concluded that test work is required: reopening approved work to
        // correct it is not a reason to re-ask whether any was needed.
        Assert.Equal(1, next.GetProperty("procedureChanges").GetInt32());
        Assert.Equal("ChangeRequired", next.GetProperty("outcome").GetString());
    }
}
