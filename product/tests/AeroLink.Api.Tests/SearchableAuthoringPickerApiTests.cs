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
using Microsoft.Extensions.DependencyInjection;

namespace AeroLink.Api.Tests;

/// <summary>
/// #402 — TCR authoring pickers silently truncate requirements and procedures.
///
/// The fixed first-page loads (200 requirements, 200 approved procedures, 500 procedure targets) must become
/// bounded, server-filtered picker surfaces with search, stable paging, totals, and exact-ID hydration, while
/// Project/build/discipline/governed-scope eligibility stays on the server.
/// </summary>
public sealed class SearchableAuthoringPickerApiTests
{
    private sealed record Fixture(
        Guid ProjectId,
        Guid ReleaseId,
        Guid BaselineId,
        Guid ReviewId,
        Guid Requirement1RevisionId,
        Guid Requirement2RevisionId,
        Guid Requirement250RevisionId,
        Guid Procedure500Id,
        Guid Procedure500RevisionId,
        Guid Procedure520Id,
        Guid Requirement260RevisionId);

    private static async Task<Fixture> SeedAsync(AeroLinkApiFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        var now = new DateTimeOffset(2026, 8, 9, 0, 0, 0, TimeSpan.Zero);

        var program = new ProgramRecord("Searchable Picker Program", "SPP");
        var project = new ProjectRecord(program.Id, "FMS", "Picker FMS");
        var release = new SoftwareRelease(project.Id, "1.6", false);
        db.AddRange(program, project, release);

        var scr = new SystemChangeRequest("SRCR-04050", 0, project.Id, release.Id,
            "Picker fixture", "P", "A", "S", "author", now);
        for (var i = 1; i <= 260; i++)
            scr.AddRequirementChange("author", $"SYSR-{i:D6}", 0, RequirementLevel.System,
                RequirementChangeKind.Introduce, $"The FMS shall satisfy picker requirement {i}.",
                "Rationale", "Test", now);
        scr.SubmitForReview("author", [new ApproverSelection("reviewer", "Reviewer")], now);
        scr.ApproveActiveStage("reviewer", now);
        db.Add(scr);

        var baseline = new CandidateBaseline("SW-01.60", 0, project.Id, release.Id, null,
            "Picker build", "cm", now);
        baseline.Select(scr, "cm", now);
        baseline.Freeze("cm", now);
        baseline.MarkRequirementsMaterialized("cm", new string('a', 64), 260, now);
        db.Add(baseline);

        var requirementRevisions = new List<RequirementRevision>();
        var artifactByNumber = new Dictionary<string, Guid>();
        for (var i = 1; i <= 260; i++)
        {
            var artifact = new RequirementArtifact(project.Id, $"SYSR-{i:D6}", RequirementLevel.System, now);
            var revision = new RequirementRevision(artifact.Id, 0,
                $"The FMS shall satisfy picker requirement {i}.", "Rationale", "Test",
                RequirementRevisionState.Active, scr.Id, baseline.Id, now);
            db.AddRange(artifact, revision);
            db.BaselineRequirements.Add(new BaselineRequirementSelection(baseline.Id, artifact.Id, revision.Id));
            requirementRevisions.Add(revision);
            artifactByNumber[artifact.BaseNumber] = artifact.Id;
        }
        await db.SaveChangesAsync();

        var procedureRevisions = new List<(TestProcedure Procedure, TestProcedureRevision Revision)>();
        for (var i = 1; i <= 520; i++)
        {
            var procedure = new TestProcedure(project.Id, $"SYSTP-{i:D6}", $"Picker procedure {i}",
                "test.author", now, TestProcedureLevel.System);
            var revision = new TestProcedureRevision(procedure.Id, 0, $"Objective {i}",
                "Preconditions", "Steps", "Expected", TestProcedureState.Approved, "test.author", now,
                effectiveBaselineId: baseline.Id);
            db.AddRange(procedure, revision);
            db.BaselineTestProcedures.Add(new BaselineTestProcedureSelection(baseline.Id, procedure.Id, revision.Id));
            procedureRevisions.Add((procedure, revision));
        }
        var hlr = new TestProcedure(project.Id, "HLRTC-000001", "Wrong-level case",
            "test.author", now, TestProcedureLevel.HighLevel);
        var hlrRevision = new TestProcedureRevision(hlr.Id, 0, "HLR objective", "Preconditions", "Steps",
            "Expected", TestProcedureState.Approved, "test.author", now, effectiveBaselineId: baseline.Id);
        db.AddRange(hlr, hlrRevision);
        db.BaselineTestProcedures.Add(new BaselineTestProcedureSelection(baseline.Id, hlr.Id, hlrRevision.Id));
        baseline.MarkTestProceduresMaterialized("cm", new string('b', 64), 521, now);

        var procedure500 = procedureRevisions.Single(x => x.Procedure.BaseNumber == "SYSTP-000500");
        var procedure520 = procedureRevisions.Single(x => x.Procedure.BaseNumber == "SYSTP-000520");
        db.TestCoverage.AddRange(
            new TestRequirementCoverage(procedure500.Revision.Id, requirementRevisions[0].Id),
            new TestRequirementCoverage(procedure500.Revision.Id, requirementRevisions[259].Id));

        var release17 = new SoftwareRelease(project.Id, "1.7", false, release.Id);
        db.Add(release17);
        var scr17 = new SystemChangeRequest("SRCR-04051", 0, project.Id, release17.Id,
            "Future-only", "P", "A", "S", "author", now);
        scr17.AddRequirementChange("author", "SYSR-000999", 0, RequirementLevel.System,
            RequirementChangeKind.Introduce, "Future requirement.", "Rationale", "Test", now);
        scr17.SubmitForReview("author", [new ApproverSelection("reviewer", "Reviewer")], now);
        scr17.ApproveActiveStage("reviewer", now);
        var baseline17 = new CandidateBaseline("SW-01.70", 0, project.Id, release17.Id, baseline.Id,
            "Future build", "cm", now);
        baseline17.Select(scr17, "cm", now);
        baseline17.Freeze("cm", now);
        baseline17.MarkRequirementsMaterialized("cm", new string('e', 64), 1, now);
        var futureArtifact = new RequirementArtifact(project.Id, "SYSR-000999", RequirementLevel.System, now);
        var futureRevision = new RequirementRevision(futureArtifact.Id, 0, "Future requirement.",
            "Rationale", "Test", RequirementRevisionState.Active, scr17.Id, baseline17.Id, now);
        var futureProcedure = new TestProcedure(project.Id, "SYSTP-000999", "Future-only procedure",
            "test.author", now, TestProcedureLevel.System);
        var futureProcedureRevision = new TestProcedureRevision(futureProcedure.Id, 0, "Future objective",
            "Preconditions", "Steps", "Expected", TestProcedureState.Approved, "test.author", now,
            effectiveBaselineId: baseline17.Id);
        db.AddRange(scr17, baseline17, futureArtifact, futureRevision, futureProcedure, futureProcedureRevision);
        db.BaselineRequirements.Add(new BaselineRequirementSelection(baseline17.Id, futureArtifact.Id, futureRevision.Id));
        db.BaselineTestProcedures.Add(new BaselineTestProcedureSelection(baseline17.Id, futureProcedure.Id, futureProcedureRevision.Id));
        baseline17.MarkTestProceduresMaterialized("cm", new string('f', 64), 1, now);

        var review = new TestChangeReview(project.Id, release.Id, scr.Id,
            TestChangeReviewDiscipline.System, scr.DisplayNumber, now);
        review.RecordTestChangeRequired("verification.engineer", now);
        review.AssignControlledNumber("SYSTPCR-000901", now);
        db.Add(review);

        foreach (var baseNumber in new[] { "SYSR-000001", "SYSR-000002" })
        {
            var change = scr.RequirementChanges.Single(x => x.BaseNumber == baseNumber);
            var revision = requirementRevisions.Single(x => x.ArtifactId == artifactByNumber[change.BaseNumber]);
            var item = VerificationImpactItem.ForIntroducedRequirement(project.Id, release.Id, scr.Id,
                review.Id, change.Id, change.DisplayNumber, "Test", now);
            item.LinkRequirementRevision(revision.Id, now);
            db.Add(item);
        }

        var account = new UserAccount("picker.engineer", "Picker Engineer", "picker@example.test",
            IdentityService.HashPassword(AeroLinkApiFactory.MemberPassword), now);
        db.AddRange(account,
            new ProgramMembership(account.Id, program.Id, ProgramRole.TestEngineer, "test.setup", now));
        await db.SaveChangesAsync();

        return new Fixture(project.Id, release.Id, baseline.Id, review.Id,
            requirementRevisions[0].Id, requirementRevisions[1].Id, requirementRevisions[249].Id,
            procedure500.Procedure.Id, procedure500.Revision.Id, procedure520.Procedure.Id,
            requirementRevisions[259].Id);
    }

    private static async Task LoginAsync(HttpClient client)
    {
        using var login = await client.PostAsJsonAsync("/api/auth/login",
            new { userName = "picker.engineer", password = AeroLinkApiFactory.MemberPassword });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        await SecurityBoundaryTests.AuthorizeMutationsAsync(client);
    }

    [Fact]
    public async Task Requirement_picker_hydrates_an_exact_revision_beyond_the_first_page()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        var fixture = await SeedAsync(factory);
        await LoginAsync(client);

        var first = await client.GetFromJsonAsync<JsonElement>(
            $"/api/requirements?projectId={fixture.ProjectId}&baselineId={fixture.BaselineId}&scope=System&includeRetired=false&page=1&pageSize=200");
        Assert.Equal(260, first.GetProperty("totalCount").GetInt32());
        Assert.Equal(200, first.GetProperty("items").GetArrayLength());

        var hydrated = await client.GetFromJsonAsync<JsonElement>(
            $"/api/requirements?projectId={fixture.ProjectId}&baselineId={fixture.BaselineId}&scope=System&includeRetired=false&page=1&pageSize=200&ids={fixture.Requirement250RevisionId}");
        var items = hydrated.GetProperty("items").EnumerateArray().ToList();
        var row = Assert.Single(items, x => x.GetProperty("displayNumber").GetString() == "SYSR-000250.00");
        Assert.Equal(fixture.Requirement250RevisionId, row.GetProperty("revisionId").GetGuid());
    }

    [Fact]
    public async Task Approved_procedure_picker_hydrates_a_procedure_beyond_the_first_page()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        var fixture = await SeedAsync(factory);
        await LoginAsync(client);

        var first = await client.GetFromJsonAsync<JsonElement>(
            $"/api/test-procedures?projectId={fixture.ProjectId}&releaseId={fixture.ReleaseId}&scope=System&state=Approved&page=1&pageSize=200");
        Assert.Equal(520, first.GetProperty("totalCount").GetInt32());
        Assert.Equal(200, first.GetProperty("items").GetArrayLength());

        var exactDisplayedTitle = await client.GetFromJsonAsync<JsonElement>(
            $"/api/test-procedures?projectId={fixture.ProjectId}&releaseId={fixture.ReleaseId}" +
            "&scope=System&state=Approved&search=exact%20historical%20title%20was%20not%20recorded&page=1&pageSize=25");
        Assert.Equal(520, exactDisplayedTitle.GetProperty("totalCount").GetInt32());
        var mutableCatalogTitle = await client.GetFromJsonAsync<JsonElement>(
            $"/api/test-procedures?projectId={fixture.ProjectId}&releaseId={fixture.ReleaseId}" +
            "&scope=System&state=Approved&search=Picker%20procedure%20500&page=1&pageSize=25");
        Assert.Equal(0, mutableCatalogTitle.GetProperty("totalCount").GetInt32());

        var hydrated = await client.GetFromJsonAsync<JsonElement>(
            $"/api/test-procedures?projectId={fixture.ProjectId}&releaseId={fixture.ReleaseId}&scope=System&state=Approved&page=1&pageSize=200&ids={fixture.Procedure500Id}");
        var items = hydrated.GetProperty("items").EnumerateArray().ToList();
        var row = Assert.Single(items, x => x.GetProperty("id").GetGuid() == fixture.Procedure500Id);
        Assert.Equal("SYSTP-000500.00", row.GetProperty("displayNumber").GetString());
    }

    [Fact]
    public async Task Modify_retire_targets_are_searchable_beyond_500_and_manifest_scoped()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        var fixture = await SeedAsync(factory);
        await LoginAsync(client);

        // The old projection took the first 500 rows of the deterministic ordering, so the defect is proven
        // only by a candidate whose position is strictly greater than 500. The fixture carries 520 System
        // procedures; position 520 is the last row of the unfiltered universe.
        var universe = await client.GetFromJsonAsync<JsonElement>(
            $"/api/test-change-reviews/{fixture.ReviewId}/procedure-targets?page=1&pageSize=200");
        Assert.Equal(520, universe.GetProperty("totalCount").GetInt32());
        Assert.Equal(3, universe.GetProperty("totalPages").GetInt32());
        var lastPage = await client.GetFromJsonAsync<JsonElement>(
            $"/api/test-change-reviews/{fixture.ReviewId}/procedure-targets?page=3&pageSize=200");
        var lastPageItems = lastPage.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(120, lastPageItems.Count);
        Assert.Equal("SYSTP-000520", lastPageItems[^1].GetProperty("baseNumber").GetString());

        var beyond500 = await client.GetFromJsonAsync<JsonElement>(
            $"/api/test-change-reviews/{fixture.ReviewId}/procedure-targets?search=SYSTP-000520&page=1&pageSize=25");
        Assert.Equal(1, beyond500.GetProperty("totalCount").GetInt32());
        var target = Assert.Single(beyond500.GetProperty("items").EnumerateArray());
        Assert.Equal("SYSTP-000520", target.GetProperty("baseNumber").GetString());
        Assert.Equal(0, target.GetProperty("currentRevision").GetInt32());

        var byDisplayedTitle = await client.GetFromJsonAsync<JsonElement>(
            $"/api/test-change-reviews/{fixture.ReviewId}/procedure-targets" +
            "?search=exact%20historical%20title%20was%20not%20recorded&page=1&pageSize=25");
        Assert.Equal(520, byDisplayedTitle.GetProperty("totalCount").GetInt32());
        Assert.Equal(25, byDisplayedTitle.GetProperty("items").GetArrayLength());

        // Exact hydration also works for the >500 target, by controlled base number and by immutable
        // procedure ID.
        var byBaseNumber = await client.GetFromJsonAsync<JsonElement>(
            $"/api/test-change-reviews/{fixture.ReviewId}/procedure-targets?page=1&pageSize=25&baseNumbers=SYSTP-000520");
        Assert.Contains(byBaseNumber.GetProperty("items").EnumerateArray(),
            x => x.GetProperty("baseNumber").GetString() == "SYSTP-000520");
        var byId = await client.GetFromJsonAsync<JsonElement>(
            $"/api/test-change-reviews/{fixture.ReviewId}/procedure-targets?page=1&pageSize=25&ids={fixture.Procedure520Id}");
        Assert.Contains(byId.GetProperty("items").EnumerateArray(),
            x => x.GetProperty("baseNumber").GetString() == "SYSTP-000520");

        var future = await client.GetFromJsonAsync<JsonElement>(
            $"/api/test-change-reviews/{fixture.ReviewId}/procedure-targets?search=SYSTP-000999&page=1&pageSize=25");
        Assert.Equal(0, future.GetProperty("totalCount").GetInt32());

        var wrongLevel = await client.GetFromJsonAsync<JsonElement>(
            $"/api/test-change-reviews/{fixture.ReviewId}/procedure-targets?search=HLRTC-000001&page=1&pageSize=25");
        Assert.Equal(0, wrongLevel.GetProperty("totalCount").GetInt32());
    }

    [Fact]
    public async Task Requirement_candidates_are_searchable_and_governed()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        var fixture = await SeedAsync(factory);
        await LoginAsync(client);

        var all = await client.GetFromJsonAsync<JsonElement>(
            $"/api/test-change-reviews/{fixture.ReviewId}/requirement-candidates?page=1&pageSize=25");
        Assert.Equal(2, all.GetProperty("totalCount").GetInt32());
        Assert.Equal(2, all.GetProperty("items").GetArrayLength());
        // #413's complete requirement identity survives in the picker projection: artifact Id, exact
        // revisionId, controlled displayNumber, statement and level.
        Assert.All(all.GetProperty("items").EnumerateArray(), row =>
        {
            Assert.NotEqual(Guid.Empty, row.GetProperty("id").GetGuid());
            Assert.NotEqual(Guid.Empty, row.GetProperty("revisionId").GetGuid());
            Assert.Matches(@"^SYSR-\d{6}\.\d{2}$", row.GetProperty("displayNumber").GetString());
            Assert.False(string.IsNullOrWhiteSpace(row.GetProperty("statement").GetString()));
            Assert.Equal("System", row.GetProperty("level").GetString());
        });

        var searched = await client.GetFromJsonAsync<JsonElement>(
            $"/api/test-change-reviews/{fixture.ReviewId}/requirement-candidates?search=000002&page=1&pageSize=25");
        var row = Assert.Single(searched.GetProperty("items").EnumerateArray());
        Assert.Equal("SYSR-000002.00", row.GetProperty("displayNumber").GetString());

        // A requirement outside the package's governed scope is not part of the candidate universe, even
        // when its immutable ID is requested directly.
        var forged = await client.GetFromJsonAsync<JsonElement>(
            $"/api/test-change-reviews/{fixture.ReviewId}/requirement-candidates?page=1&pageSize=25&ids={fixture.Requirement250RevisionId}");
        Assert.DoesNotContain(forged.GetProperty("items").EnumerateArray(),
            x => x.GetProperty("revisionId").GetGuid() == fixture.Requirement250RevisionId);
    }

    [Fact]
    public async Task Forged_procedure_ids_are_not_hydrated_into_targets()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        var fixture = await SeedAsync(factory);
        await LoginAsync(client);

        // The future-only procedure belongs to another build and must never be hydrated, whatever ID the
        // caller asks for.
        var future = await client.GetFromJsonAsync<JsonElement>(
            $"/api/test-change-reviews/{fixture.ReviewId}/procedure-targets?page=1&pageSize=25&baseNumbers=SYSTP-000999");
        Assert.Equal(520, future.GetProperty("totalCount").GetInt32());
        Assert.DoesNotContain(future.GetProperty("items").EnumerateArray(),
            x => x.GetProperty("baseNumber").GetString() == "SYSTP-000999");
    }

    [Fact]
    public async Task Workspace_payload_hydrates_only_referenced_procedure_targets()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        var fixture = await SeedAsync(factory);
        await LoginAsync(client);

        var before = await client.GetFromJsonAsync<JsonElement>(
            $"/api/test-change-reviews/{fixture.ReviewId}/procedure-changes");
        Assert.Equal(0, before.GetProperty("procedureTargets").GetArrayLength());

        using var proposed = await client.PostAsJsonAsync(
            $"/api/test-change-reviews/{fixture.ReviewId}/procedure-changes",
            new
            {
                kind = "Modify", baseNumber = "SYSTP-000500", revision = 1, title = "Expanded coverage",
                objective = "Verify governed behavior.", steps = "Execute.", expectedResult = "Observed.",
                rationale = "The approved change expands the procedure.",
                drivingRequirementRevisionIds = new[] { fixture.Requirement1RevisionId },
                coverageChangeRationale = "Add the governed requirement to the carried procedure."
            });
        Assert.Equal(HttpStatusCode.OK, proposed.StatusCode);

        var after = await client.GetFromJsonAsync<JsonElement>(
            $"/api/test-change-reviews/{fixture.ReviewId}/procedure-changes");
        var target = Assert.Single(after.GetProperty("procedureTargets").EnumerateArray());
        Assert.Equal("SYSTP-000500", target.GetProperty("baseNumber").GetString());
        var coverage = target.GetProperty("currentCoverage").EnumerateArray().ToList();
        Assert.Contains(coverage, x => x.GetProperty("revisionId").GetGuid() == fixture.Requirement260RevisionId);
    }
}
