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

public sealed class TestProcedureRevisionHistoryApiTests
{
    private sealed record Fixture(Guid ProjectId, Guid Release15Id, Guid Release16Id,
        Guid Baseline15Id, Guid Baseline16Id,
        Guid ProcedureId, Guid Revision00Id, Guid Revision01Id,
        string Tcr00, string Tcr01, string Cr00, string Cr01, string FoldedCr01);

    [Fact]
    public async Task List_history_and_trace_keep_exact_titles_and_the_same_package_provenance_across_builds()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        var fixture = await SeedAsync(factory);
        await LoginAsync(client, "history.engineer");

        using var oldListResponse = await client.GetAsync(
            $"/api/test-procedures?projectId={fixture.ProjectId}&releaseId={fixture.Release15Id}" +
            "&scope=System&search=legacy%20route%20sequencing&page=1&pageSize=25");
        Assert.Equal(HttpStatusCode.OK, oldListResponse.StatusCode);
        var oldList = JsonSerializer.Deserialize<JsonElement>(await oldListResponse.Content.ReadAsStringAsync());
        Assert.Equal(1, oldList.GetProperty("totalCount").GetInt32());
        var oldRow = oldList.GetProperty("items").EnumerateArray().Single();
        Assert.Equal("Verify legacy route sequencing", oldRow.GetProperty("title").GetString());
        Assert.True(oldRow.GetProperty("titleIsExact").GetBoolean());
        Assert.Equal(fixture.Revision00Id, oldRow.GetProperty("revisionId").GetGuid());

        using var oldWrongSearchResponse = await client.GetAsync(
            $"/api/test-procedures?projectId={fixture.ProjectId}&releaseId={fixture.Release15Id}" +
            "&scope=System&search=discontinuities&page=1&pageSize=25");
        var oldWrongSearch = JsonSerializer.Deserialize<JsonElement>(await oldWrongSearchResponse.Content.ReadAsStringAsync());
        Assert.Equal(0, oldWrongSearch.GetProperty("totalCount").GetInt32());

        using var newListResponse = await client.GetAsync(
            $"/api/test-procedures?projectId={fixture.ProjectId}&releaseId={fixture.Release16Id}" +
            "&scope=System&search=discontinuities&page=1&pageSize=25");
        Assert.Equal(HttpStatusCode.OK, newListResponse.StatusCode);
        var newList = JsonSerializer.Deserialize<JsonElement>(await newListResponse.Content.ReadAsStringAsync());
        Assert.Equal(1, newList.GetProperty("totalCount").GetInt32());
        var newRow = newList.GetProperty("items").EnumerateArray().Single();
        Assert.Equal("Verify route sequencing and discontinuities", newRow.GetProperty("title").GetString());
        Assert.True(newRow.GetProperty("titleIsExact").GetBoolean());
        Assert.Equal(fixture.Revision01Id, newRow.GetProperty("revisionId").GetGuid());

        var oldHistory = await JsonAsync(client,
            $"/api/test-procedures/{fixture.ProcedureId}/history?releaseId={fixture.Release15Id}" +
            $"&revisionId={fixture.Revision00Id}");
        var oldHistoryRevision = oldHistory.GetProperty("revisions").EnumerateArray()
            .Single(x => x.GetProperty("id").GetGuid() == fixture.Revision00Id);
        Assert.Equal("Verify legacy route sequencing", oldHistoryRevision.GetProperty("title").GetString());
        Assert.Equal(fixture.Tcr00, oldHistoryRevision.GetProperty("package").GetString());
        Assert.Contains(oldHistoryRevision.GetProperty("drivenBy").EnumerateArray(),
            row => row.GetProperty("changeRequest").GetString() == fixture.Cr00
                   && row.GetProperty("package").GetString() == fixture.Tcr00);

        var newHistory = await JsonAsync(client,
            $"/api/test-procedures/{fixture.ProcedureId}/history?releaseId={fixture.Release16Id}" +
            $"&revisionId={fixture.Revision01Id}");
        var newHistoryRevision = newHistory.GetProperty("revisions").EnumerateArray()
            .Single(x => x.GetProperty("id").GetGuid() == fixture.Revision01Id);
        Assert.Equal("Verify route sequencing and discontinuities",
            newHistoryRevision.GetProperty("title").GetString());
        Assert.Equal(fixture.Tcr01, newHistoryRevision.GetProperty("package").GetString());
        Assert.Contains(newHistoryRevision.GetProperty("drivenBy").EnumerateArray(),
            row => row.GetProperty("changeRequest").GetString() == fixture.Cr01
                   && row.GetProperty("package").GetString() == fixture.Tcr01);
        Assert.Contains(newHistoryRevision.GetProperty("drivenBy").EnumerateArray(),
            row => row.GetProperty("changeRequest").GetString() == fixture.FoldedCr01
                   && row.GetProperty("package").GetString() == fixture.Tcr01);

        var oldTrace = await JsonAsync(client,
            $"/api/test-procedures/{fixture.ProcedureId}/trace?releaseId={fixture.Release15Id}" +
            $"&revisionId={fixture.Revision00Id}");
        Assert.Equal("Verify legacy route sequencing", oldTrace.GetProperty("title").GetString());
        Assert.Equal(fixture.Tcr00, oldTrace.GetProperty("package").GetString());
        Assert.Contains(oldTrace.GetProperty("provenance").EnumerateArray(),
            row => row.GetProperty("changeRequest").GetString() == fixture.Cr00
                   && row.GetProperty("package").GetString() == fixture.Tcr00);

        var newTrace = await JsonAsync(client,
            $"/api/test-procedures/{fixture.ProcedureId}/trace?releaseId={fixture.Release16Id}" +
            $"&revisionId={fixture.Revision01Id}");
        Assert.Equal("Verify route sequencing and discontinuities", newTrace.GetProperty("title").GetString());
        Assert.Equal(fixture.Tcr01, newTrace.GetProperty("package").GetString());
        Assert.Contains(newTrace.GetProperty("provenance").EnumerateArray(),
            row => row.GetProperty("changeRequest").GetString() == fixture.Cr01
                   && row.GetProperty("package").GetString() == fixture.Tcr01);
        Assert.Contains(newTrace.GetProperty("provenance").EnumerateArray(),
            row => row.GetProperty("changeRequest").GetString() == fixture.FoldedCr01
                   && row.GetProperty("package").GetString() == fixture.Tcr01);

        var oldCoverage = await JsonAsync(client,
            $"/api/verification-coverage?projectId={fixture.ProjectId}&baselineId={fixture.Baseline15Id}");
        var oldCoveredBy = oldCoverage.GetProperty("items").EnumerateArray()
            .Single(x => x.GetProperty("displayNumber").GetString() == "SYSR-42150.00")
            .GetProperty("coveredBy").EnumerateArray().Single();
        Assert.Equal("Verify legacy route sequencing", oldCoveredBy.GetProperty("title").GetString());

        var newCoverage = await JsonAsync(client,
            $"/api/verification-coverage?projectId={fixture.ProjectId}&baselineId={fixture.Baseline16Id}");
        Assert.All(newCoverage.GetProperty("items").EnumerateArray()
                .Where(x => x.GetProperty("displayNumber").GetString() is "SYSR-42160.00" or "SYSR-42170.00")
                .SelectMany(x => x.GetProperty("coveredBy").EnumerateArray()),
            row => Assert.Equal("Verify route sequencing and discontinuities",
                row.GetProperty("title").GetString()));

        var oldTestSets = await JsonAsync(client, $"/api/releases/{fixture.Release15Id}/test-sets");
        var oldTestSetProcedure = oldTestSets.EnumerateArray()
            .Single(x => x.GetProperty("discipline").GetString() == "System")
            .GetProperty("procedures").EnumerateArray().Single();
        Assert.Equal("Verify legacy route sequencing", oldTestSetProcedure.GetProperty("title").GetString());

        var newTestSets = await JsonAsync(client, $"/api/releases/{fixture.Release16Id}/test-sets");
        var newTestSetProcedure = newTestSets.EnumerateArray()
            .Single(x => x.GetProperty("discipline").GetString() == "System")
            .GetProperty("procedures").EnumerateArray().Single();
        Assert.Equal("Verify route sequencing and discontinuities",
            newTestSetProcedure.GetProperty("title").GetString());

        var oldSearch = await JsonAsync(client,
            $"/api/search?projectId={fixture.ProjectId}&releaseId={fixture.Release15Id}&query=legacy%20route");
        Assert.Contains(oldSearch.GetProperty("items").EnumerateArray(),
            x => x.GetProperty("kind").GetString() == "test-procedure"
                 && x.GetProperty("title").GetString() == "Verify legacy route sequencing");
        var oldSuccessorTitleSearch = await JsonAsync(client,
            $"/api/search?projectId={fixture.ProjectId}&releaseId={fixture.Release15Id}&query=discontinuities");
        Assert.DoesNotContain(oldSuccessorTitleSearch.GetProperty("items").EnumerateArray(),
            x => x.GetProperty("kind").GetString() == "test-procedure");
        var newSearch = await JsonAsync(client,
            $"/api/search?projectId={fixture.ProjectId}&releaseId={fixture.Release16Id}&query=discontinuities");
        Assert.Contains(newSearch.GetProperty("items").EnumerateArray(),
            x => x.GetProperty("kind").GetString() == "test-procedure"
                 && x.GetProperty("title").GetString() == "Verify route sequencing and discontinuities");

var oldArtifact = await JsonAsync(client,
    $"/api/artifacts/test-procedure/{fixture.ProcedureId}?releaseId={fixture.Release15Id}");
Assert.Equal("SYSTP-42150.00", oldArtifact.GetProperty("identifier").GetString());
Assert.Equal("Verify legacy route sequencing", oldArtifact.GetProperty("title").GetString());
Assert.Equal(fixture.Revision00Id,
    oldArtifact.GetProperty("details").GetProperty("revisionId").GetGuid());

var newArtifact = await JsonAsync(client,
    $"/api/artifacts/test-procedure/{fixture.ProcedureId}?releaseId={fixture.Release16Id}");
Assert.Equal("SYSTP-42150.01", newArtifact.GetProperty("identifier").GetString());
Assert.Equal("Verify route sequencing and discontinuities",
    newArtifact.GetProperty("title").GetString());
Assert.Equal(fixture.Revision01Id,
    newArtifact.GetProperty("details").GetProperty("revisionId").GetGuid());
    }

    [Fact]
    public async Task Manual_package_without_impacts_and_legacy_revision_report_only_the_provenance_recorded()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        Guid manualProcedureId, manualRevisionId, legacyProcedureId, legacyRevisionId;
        string packageNumber, primaryNumber, foldedNumber;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var now = new DateTimeOffset(2026, 8, 10, 12, 0, 0, TimeSpan.Zero);
            var program = new ProgramRecord("Manual procedure provenance", "MPP");
            var project = new ProjectRecord(program.Id, "Manual project", "Manual product");
            var release = new SoftwareRelease(project.Id, "2.0", false);
            var primary = ApprovedChange("SRCR-42410", project.Id, release.Id,
                "SYSR-42410", "Primary manual-package requirement", now);
            var folded = ApprovedChange("SRCR-42420", project.Id, release.Id,
                "SYSR-42420", "Folded manual-package requirement", now.AddMinutes(1));
            var review = Review(project.Id, release.Id, primary.Id, primary.DisplayNumber,
                "SYSTCR-42410", 1, TestProcedureChangeKind.Introduce,
                "Verify the manually packaged behavior", Guid.NewGuid(), now.AddMinutes(2));
            review.IncludeChangeRequest("verification.engineer", folded.Id, folded.DisplayNumber,
                now.AddMinutes(3));
            var manualProcedure = new TestProcedure(project.Id, "SYSTP-42410", "Mutable manual catalog title",
                "verification.engineer", now, TestProcedureLevel.System);
            var manualRevision = Revision(manualProcedure.Id, 0, review.Id, null,
                "Manual objective", now.AddMinutes(4));
            var legacyProcedure = new TestProcedure(project.Id, "SYSTP-42420", "Mutable legacy catalog title",
                "legacy.author", now, TestProcedureLevel.System);
            var legacyRevision = Revision(legacyProcedure.Id, 0, null, null,
                "Legacy objective", now.AddMinutes(5));
            var engineer = new UserAccount("history.engineer", "History Engineer", "history@example.test",
                IdentityService.HashPassword(AeroLinkApiFactory.MemberPassword), now);
            db.AddRange(program, project, release, primary, folded, review, manualProcedure, manualRevision,
                legacyProcedure, legacyRevision, engineer,
                new ProgramMembership(engineer.Id, program.Id, ProgramRole.TestEngineer, "test.setup", now));
            await db.SaveChangesAsync();
            manualProcedureId = manualProcedure.Id;
            manualRevisionId = manualRevision.Id;
            legacyProcedureId = legacyProcedure.Id;
            legacyRevisionId = legacyRevision.Id;
            packageNumber = review.DisplayNumber;
            primaryNumber = primary.DisplayNumber;
            foldedNumber = folded.DisplayNumber;
        }
        await LoginAsync(client, "history.engineer");

        var manualHistory = await JsonAsync(client,
            $"/api/test-procedures/{manualProcedureId}/history?revisionId={manualRevisionId}");
        var manualHistoryRevision = manualHistory.GetProperty("revisions").EnumerateArray().Single();
        Assert.Equal(packageNumber, manualHistoryRevision.GetProperty("package").GetString());
        Assert.Contains(manualHistoryRevision.GetProperty("drivenBy").EnumerateArray(),
            x => x.GetProperty("changeRequest").GetString() == primaryNumber);
        Assert.Contains(manualHistoryRevision.GetProperty("drivenBy").EnumerateArray(),
            x => x.GetProperty("changeRequest").GetString() == foldedNumber);

        var manualTrace = await JsonAsync(client,
            $"/api/test-procedures/{manualProcedureId}/trace?revisionId={manualRevisionId}");
        Assert.Equal(packageNumber, manualTrace.GetProperty("package").GetString());
        Assert.Contains(manualTrace.GetProperty("provenance").EnumerateArray(),
            x => x.GetProperty("changeRequest").GetString() == primaryNumber);
        Assert.Contains(manualTrace.GetProperty("provenance").EnumerateArray(),
            x => x.GetProperty("changeRequest").GetString() == foldedNumber);

        var legacyHistory = await JsonAsync(client,
            $"/api/test-procedures/{legacyProcedureId}/history?revisionId={legacyRevisionId}");
        var legacyHistoryRevision = legacyHistory.GetProperty("revisions").EnumerateArray().Single();
        Assert.Equal(JsonValueKind.Null, legacyHistoryRevision.GetProperty("package").ValueKind);
        Assert.False(legacyHistoryRevision.GetProperty("titleIsExact").GetBoolean());
        Assert.Contains("exact historical title was not recorded",
            legacyHistoryRevision.GetProperty("title").GetString());
        Assert.Contains("Legacy revision",
            legacyHistoryRevision.GetProperty("provenanceNote").GetString());

        var legacyTrace = await JsonAsync(client,
            $"/api/test-procedures/{legacyProcedureId}/trace?revisionId={legacyRevisionId}");
        Assert.Equal(JsonValueKind.Null, legacyTrace.GetProperty("package").ValueKind);
        Assert.False(legacyTrace.GetProperty("titleIsExact").GetBoolean());
        Assert.Empty(legacyTrace.GetProperty("provenance").EnumerateArray());
        Assert.Contains("Legacy revision", legacyTrace.GetProperty("provenanceNote").GetString());
    }

    private static async Task<Fixture> SeedAsync(AeroLinkApiFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        var now = new DateTimeOffset(2026, 8, 10, 10, 0, 0, TimeSpan.Zero);
        var program = new ProgramRecord("Procedure revision history", "PRH");
        var project = new ProjectRecord(program.Id, "History project", "History product");
        var release15 = new SoftwareRelease(project.Id, "1.5", true);
        var release16 = new SoftwareRelease(project.Id, "1.6", false, release15.Id);
        var scr15 = ApprovedChange("SRCR-42150", project.Id, release15.Id,
            "SYSR-42150", "Legacy sequencing requirement", now);
        var scr16 = ApprovedChange("SRCR-42160", project.Id, release16.Id,
            "SYSR-42160", "Sequencing discontinuities requirement", now.AddMinutes(1));
        var foldedScr16 = ApprovedChange("SRCR-42170", project.Id, release16.Id,
            "SYSR-42170", "Folded route-monitoring requirement", now.AddMinutes(2));
        var baseline15 = Baseline("SW-42.15", project.Id, release15.Id, null, scr15, now);
        var baseline16 = Baseline("SW-42.16", project.Id, release16.Id, baseline15.Id, scr16, now.AddMinutes(1));

        var requirement15 = new RequirementArtifact(project.Id, "SYSR-42150", RequirementLevel.System, now);
        var requirement16 = new RequirementArtifact(project.Id, "SYSR-42160", RequirementLevel.System, now);
        var foldedRequirement16 = new RequirementArtifact(project.Id, "SYSR-42170", RequirementLevel.System, now);
        var requirement15Revision = new RequirementRevision(requirement15.Id, 0,
            "Legacy sequencing requirement", "History fixture", "Test",
            RequirementRevisionState.Active, scr15.Id, baseline15.Id, now);
        var requirement16Revision = new RequirementRevision(requirement16.Id, 0,
            "Sequencing discontinuities requirement", "History fixture", "Test",
            RequirementRevisionState.Active, scr16.Id, baseline16.Id, now.AddMinutes(1));
        var foldedRequirement16Revision = new RequirementRevision(foldedRequirement16.Id, 0,
            "Folded route-monitoring requirement", "History fixture", "Test",
            RequirementRevisionState.Active, foldedScr16.Id, baseline16.Id, now.AddMinutes(2));
        db.BaselineRequirements.AddRange(
            new BaselineRequirementSelection(baseline15.Id, requirement15.Id, requirement15Revision.Id),
            new BaselineRequirementSelection(baseline16.Id, requirement15.Id, requirement15Revision.Id),
            new BaselineRequirementSelection(baseline16.Id, requirement16.Id, requirement16Revision.Id),
            new BaselineRequirementSelection(baseline16.Id, foldedRequirement16.Id, foldedRequirement16Revision.Id));

        var tcr15 = Review(project.Id, release15.Id, scr15.Id, scr15.DisplayNumber,
            "SYSTCR-42150", 0, TestProcedureChangeKind.Introduce,
            "Verify legacy route sequencing", requirement15Revision.Id, now);
        var tcr16 = Review(project.Id, release16.Id, scr16.Id, scr16.DisplayNumber,
            "SYSTCR-42150", 1, TestProcedureChangeKind.Modify,
            "Verify route sequencing and discontinuities", requirement16Revision.Id, now.AddMinutes(1),
            procedureRevision: 1);
        tcr16.IncludeChangeRequest("verification.engineer", foldedScr16.Id, foldedScr16.DisplayNumber,
            now.AddMinutes(2));
        var procedure = new TestProcedure(project.Id, "SYSTP-42150", "Mutable catalog title",
            "verification.engineer", now, TestProcedureLevel.System);
        var revision00 = Revision(procedure.Id, 0, tcr15.Id, baseline15.Id,
            "Legacy route objective", now);
        var revision01 = Revision(procedure.Id, 1, tcr16.Id, baseline16.Id,
            "Discontinuity objective", now.AddMinutes(1));
        procedure.UpdateDraft("Catalog title changed after 1.6", procedure.OwnerId, now.AddMinutes(2));
        db.BaselineTestProcedures.AddRange(
            new BaselineTestProcedureSelection(baseline15.Id, procedure.Id, revision00.Id),
            new BaselineTestProcedureSelection(baseline16.Id, procedure.Id, revision01.Id));
        baseline15.MarkTestProceduresMaterialized("cm", new string('b', 64), 1, now);
        baseline16.MarkTestProceduresMaterialized("cm", new string('c', 64), 1, now.AddMinutes(1));

        var impact15 = Impact(project.Id, release15.Id, scr15, tcr15, requirement15Revision,
            procedure, revision00, now);
        var impact16 = Impact(project.Id, release16.Id, scr16, tcr16, requirement16Revision,
            procedure, revision01, now.AddMinutes(1));
        var foldedImpact16 = Impact(project.Id, release16.Id, foldedScr16, tcr16,
            foldedRequirement16Revision, procedure, revision01, now.AddMinutes(2));
        var coverage15 = new TestRequirementCoverage(revision00.Id, requirement15Revision.Id);
        var coverage16 = new TestRequirementCoverage(revision01.Id, requirement16Revision.Id);
        var foldedCoverage16 = new TestRequirementCoverage(revision01.Id, foldedRequirement16Revision.Id);
        var engineer = new UserAccount("history.engineer", "History Engineer", "history@example.test",
            IdentityService.HashPassword(AeroLinkApiFactory.MemberPassword), now);
        db.AddRange(program, project, release15, release16, scr15, scr16, foldedScr16, baseline15, baseline16,
            requirement15, requirement16, foldedRequirement16, requirement15Revision, requirement16Revision,
            foldedRequirement16Revision, tcr15, tcr16, procedure, revision00, revision01, impact15, impact16,
            foldedImpact16, coverage15, coverage16, foldedCoverage16,
            engineer, new ProgramMembership(engineer.Id, program.Id, ProgramRole.TestEngineer, "test.setup", now));
        await db.SaveChangesAsync();

        return new(project.Id, release15.Id, release16.Id, baseline15.Id, baseline16.Id,
            procedure.Id, revision00.Id, revision01.Id,
            tcr15.DisplayNumber, tcr16.DisplayNumber, scr15.DisplayNumber, scr16.DisplayNumber,
            foldedScr16.DisplayNumber);
    }

    private static SystemChangeRequest ApprovedChange(string number, Guid projectId, Guid releaseId,
        string requirementNumber, string statement, DateTimeOffset now)
    {
        var request = new SystemChangeRequest(number, 0, projectId, releaseId,
            "History change", "Problem", "Analysis", "Solution", "author", now);
        request.AddRequirementChange("author", requirementNumber, 0, RequirementLevel.System,
            RequirementChangeKind.Introduce, statement, "History fixture", "Test", now);
        request.SubmitForReview("author", [new ApproverSelection("reviewer", "Reviewer")], now);
        request.ApproveActiveStage("reviewer", now);
        return request;
    }

    private static CandidateBaseline Baseline(string number, Guid projectId, Guid releaseId,
        Guid? predecessorId, SystemChangeRequest request, DateTimeOffset now)
    {
        var baseline = new CandidateBaseline(number, 0, projectId, releaseId, predecessorId,
            number, "cm", now);
        baseline.Select(request, "cm", now);
        baseline.Freeze("cm", now);
        baseline.MarkRequirementsMaterialized("cm", new string('a', 64), 1, now);
        return baseline;
    }

    private static TestChangeReview Review(Guid projectId, Guid releaseId, Guid changeRequestId,
        string changeRequestNumber, string tcrNumber, int tcrRevision, TestProcedureChangeKind kind,
        string title, Guid requirementRevisionId, DateTimeOffset now, int procedureRevision = 0)
    {
        var review = new TestChangeReview(projectId, releaseId, changeRequestId,
            TestChangeReviewDiscipline.System, changeRequestNumber, now, tcrNumber, tcrRevision);
        review.RecordTestChangeRequired("verification.engineer", now);
        review.AddProcedureChange("verification.engineer", new TestProcedureChangeDraft(
            "SYSTP-42150", procedureRevision, TestProcedureLevel.System, kind, title,
            "Verify exact title.", "Configured system.", "Exercise route sequencing.",
            "Expected sequencing is observed.", "History fixture.",
            JsonSerializer.Serialize(new[] { requirementRevisionId })), now);
        return review;
    }

    private static TestProcedureRevision Revision(Guid procedureId, int revision, Guid? tcrId,
        Guid? baselineId, string objective, DateTimeOffset now) =>
        new(procedureId, revision, objective, "Configured system.", "Exercise route sequencing.",
            "Expected sequencing is observed.", TestProcedureState.Approved, "verification.engineer", now,
            sourceTestChangeRequestId: tcrId, effectiveBaselineId: baselineId);

    private static VerificationImpactItem Impact(Guid projectId, Guid releaseId,
        SystemChangeRequest request, TestChangeReview review, RequirementRevision requirementRevision,
        TestProcedure procedure, TestProcedureRevision procedureRevision, DateTimeOffset now)
    {
        var change = request.RequirementChanges.Single();
        var item = VerificationImpactItem.ForIntroducedRequirement(projectId, releaseId, request.Id,
            review.Id, change.Id, change.DisplayNumber, "Test", now);
        item.LinkRequirementRevision(requirementRevision.Id, now);
        item.Resolve("verification.engineer", VerificationImpactOutcome.ProcedureCoverageConfirmed,
            "Exact procedure revision covers this requirement.", now,
            procedure.Id, procedureRevision.Id, TestProcedureChangeAction.ModifyExisting);
        return item;
    }

    private static async Task LoginAsync(HttpClient client, string user)
    {
        using var login = await client.PostAsJsonAsync("/api/auth/login",
            new { userName = user, password = AeroLinkApiFactory.MemberPassword });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        await SecurityBoundaryTests.AuthorizeMutationsAsync(client);
    }

    private static async Task<JsonElement> JsonAsync(HttpClient client, string path)
    {
        using var response = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
    }
}
