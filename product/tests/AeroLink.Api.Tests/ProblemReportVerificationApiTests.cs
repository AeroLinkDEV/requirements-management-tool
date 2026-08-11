using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AeroLink.Domain.Baselines;
using AeroLink.Domain.Programs;
using AeroLink.Domain.Releases;
using AeroLink.Domain.Requirements;
using AeroLink.Domain.Verification;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AeroLink.Api.Tests;

public sealed class ProblemReportVerificationApiTests
{
    private sealed record Fixture(
        Guid ReportId, long ReportVersion, Guid ManualReportId, long ManualReportVersion,
        Guid ProjectId, Guid TargetBuildId, Guid TargetRevisionId, Guid OriginExecutionId,
        Guid WrongProcedureExecutionId, Guid NoRetestExecutionId, Guid HistoricalExecutionId,
        Guid EqualCorrectionTimeExecutionId,
        Guid WrongBuildExecutionId, Guid FailedExecutionId, Guid BlockedExecutionId, Guid WrongProjectExecutionId);

    private static async Task<Fixture> SeedAsync(AeroLinkApiFactory factory)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        var start = new DateTimeOffset(2026, 8, 10, 12, 0, 0, TimeSpan.Zero);
        var program = new ProgramRecord("PR closure chain", "PVC");
        var project = new ProjectRecord(program.Id, "FMS", "Closure-chain FMS");
        var originRelease = new SoftwareRelease(project.Id, "1.5", false);
        var targetRelease = new SoftwareRelease(project.Id, "1.6", false, originRelease.Id);
        var wrongRelease = new SoftwareRelease(project.Id, "1.7", false, targetRelease.Id);
        var originBaseline = new CandidateBaseline("SW-04.49", 0, project.Id, originRelease.Id, null, "Origin", "cm", start);
        var targetBaseline = new CandidateBaseline("SW-04.50", 0, project.Id, targetRelease.Id, originBaseline.Id, "Target", "cm", start);
        var wrongBaseline = new CandidateBaseline("SW-04.51", 0, project.Id, wrongRelease.Id, targetBaseline.Id, "Wrong", "cm", start);
        var originBuild = new SoftwareBuild(project.Id, originRelease.Id, originBaseline.Id, "SW-04.49", "Origin build", "cm", start);
        var targetBuild = new SoftwareBuild(project.Id, targetRelease.Id, targetBaseline.Id, "SW-04.50", "Corrective build", "cm", start);
        var wrongBuild = new SoftwareBuild(project.Id, wrongRelease.Id, wrongBaseline.Id, "SW-04.51", "Wrong build", "cm", start);

        var procedure = new TestProcedure(project.Id, "SYSTP-004490", "Correct source selection", "test", start, TestProcedureLevel.System);
        var failedRevision = new TestProcedureRevision(procedure.Id, 0, "Original objective", "Preconditions", "Steps", "Expected",
            TestProcedureState.Approved, "test", start, effectiveBaselineId: originBaseline.Id);
        var targetRevision = new TestProcedureRevision(procedure.Id, 1, "Corrective objective", "Preconditions", "Revised steps", "Expected",
            TestProcedureState.Approved, "test", start.AddMinutes(3), sourceTestChangeRequestId: Guid.NewGuid(), effectiveBaselineId: targetBaseline.Id);
        var otherProcedure = new TestProcedure(project.Id, "SYSTP-004491", "Unrelated function", "test", start, TestProcedureLevel.System);
        var otherRevision = new TestProcedureRevision(otherProcedure.Id, 0, "Other objective", "Preconditions", "Steps", "Expected",
            TestProcedureState.Approved, "test", start, effectiveBaselineId: targetBaseline.Id);

        var originFailure = Execution(project.Id, failedRevision.Id, originBuild.Id, null, TestOutcome.Fail, start, originRelease.Id);
        var report = Report(project.Id, targetRelease.Id, "PR-04490", start.AddMinutes(1));
        ProgressToVerifying(report, start.AddMinutes(5));
        var manual = Report(project.Id, targetRelease.Id, "PR-04491", start.AddMinutes(1));
        ProgressToVerifying(manual, start.AddMinutes(5));

        var wrongProcedure = Execution(project.Id, otherRevision.Id, targetBuild.Id, originFailure.Id, TestOutcome.Pass, start.AddMinutes(10), targetRelease.Id);
        var noRetest = Execution(project.Id, targetRevision.Id, targetBuild.Id, null, TestOutcome.Pass, start.AddMinutes(10), targetRelease.Id);
        // Simulates an Awaiting-SQA record produced by the historical weak endpoint. Closure must revalidate
        // it instead of trusting state alone.
        manual.RecordResolutionVerification("admin", noRetest.Id, start.AddMinutes(11));
        var historical = Execution(project.Id, targetRevision.Id, targetBuild.Id, originFailure.Id, TestOutcome.Pass, start.AddMinutes(4), targetRelease.Id);
        var equalCorrectionTime = Execution(project.Id, targetRevision.Id, targetBuild.Id, originFailure.Id, TestOutcome.Pass, start.AddMinutes(5), targetRelease.Id);
        var wrongBuildExecution = Execution(project.Id, targetRevision.Id, wrongBuild.Id, originFailure.Id, TestOutcome.Pass, start.AddMinutes(10), wrongRelease.Id);
        var failed = Execution(project.Id, targetRevision.Id, targetBuild.Id, originFailure.Id, TestOutcome.Fail, start.AddMinutes(10), targetRelease.Id);
        var blocked = Execution(project.Id, targetRevision.Id, targetBuild.Id, originFailure.Id, TestOutcome.Blocked, start.AddMinutes(10), targetRelease.Id);
        var otherProject = new ProjectRecord(program.Id, "Other", "Other Project");
        var wrongProject = Execution(otherProject.Id, targetRevision.Id, targetBuild.Id, originFailure.Id, TestOutcome.Pass, start.AddMinutes(10), targetRelease.Id);

        db.AddRange(program, project, otherProject, originRelease, targetRelease, wrongRelease,
            originBaseline, targetBaseline, wrongBaseline, originBuild, targetBuild, wrongBuild,
            procedure, failedRevision, targetRevision, otherProcedure, otherRevision,
            originFailure, report, manual, wrongProcedure, noRetest, historical, equalCorrectionTime, wrongBuildExecution, failed, blocked, wrongProject,
            new BaselineTestProcedureSelection(originBaseline.Id, procedure.Id, failedRevision.Id),
            new BaselineTestProcedureSelection(targetBaseline.Id, procedure.Id, targetRevision.Id),
            new BaselineTestProcedureSelection(targetBaseline.Id, otherProcedure.Id, otherRevision.Id),
            new BaselineTestProcedureSelection(wrongBaseline.Id, procedure.Id, targetRevision.Id),
            ProblemReportRelationshipPolicy.CreateControlled(report.Id, "TestExecution", originFailure.Id,
                ProblemReportRelationshipPolicy.OriginatingFailure, ProblemReportRelationshipProducer.FailureCreationWorkflow, "admin", start),
            ProblemReportRelationshipPolicy.CreateControlled(report.Id, "Release", targetRelease.Id,
                ProblemReportRelationshipPolicy.BuildScope, ProblemReportRelationshipProducer.TargetBuildWorkflow, "admin", start));
        await db.SaveChangesAsync();
        var baselineIds = new[] { originBaseline.Id, targetBaseline.Id, wrongBaseline.Id };
        await db.CandidateBaselines.Where(item => baselineIds.Contains(item.Id)).ExecuteUpdateAsync(update => update
            .SetProperty(item => item.RequirementsMaterializedAt, start)
            .SetProperty(item => item.TestProceduresMaterializedAt, start)
            .SetProperty(item => item.TestProceduresHash, "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"));
        return new(report.Id, report.Version, manual.Id, manual.Version, project.Id, targetBuild.Id,
            targetRevision.Id, originFailure.Id, wrongProcedure.Id, noRetest.Id, historical.Id,
            equalCorrectionTime.Id, wrongBuildExecution.Id, failed.Id, blocked.Id, wrongProject.Id);
    }

    [Fact]
    public async Task Verify_rejects_unrelated_stale_wrong_build_and_unscoped_evidence_then_accepts_the_effective_retest_once()
    {
        using var factory = new AeroLinkApiFactory();
        var fixture = await SeedAsync(factory);
        using var client = factory.CreateClient();
        await ProblemReportApiTests.BootstrapAndLoginAsync(client);

        await RejectAsync(client, fixture.ReportId, fixture.ReportVersion, fixture.WrongProjectExecutionId, "pr_verification_wrong_project");
        await RejectAsync(client, fixture.ReportId, fixture.ReportVersion, fixture.WrongProcedureExecutionId, "pr_verification_wrong_procedure");
        await RejectAsync(client, fixture.ReportId, fixture.ReportVersion, fixture.NoRetestExecutionId, "pr_verification_not_successor");
        await RejectAsync(client, fixture.ReportId, fixture.ReportVersion, fixture.HistoricalExecutionId, "pr_verification_not_successor");
        await RejectAsync(client, fixture.ReportId, fixture.ReportVersion, fixture.EqualCorrectionTimeExecutionId, "pr_verification_not_successor");
        await RejectAsync(client, fixture.ReportId, fixture.ReportVersion, fixture.WrongBuildExecutionId, "pr_verification_wrong_build");
        await RejectAsync(client, fixture.ReportId, fixture.ReportVersion, fixture.FailedExecutionId, "pr_verification_not_pass");
        await RejectAsync(client, fixture.ReportId, fixture.ReportVersion, fixture.BlockedExecutionId, "pr_verification_not_pass");
        await RejectAsync(client, fixture.ManualReportId, fixture.ManualReportVersion, fixture.NoRetestExecutionId, "pr_verification_scope_unknown");
        using (var closure = await client.PostAsJsonAsync($"/api/problem-reports/{fixture.ManualReportId}/closure/approve", new
        {
            expectedVersion = fixture.ManualReportVersion
        }))
        {
            Assert.Equal(HttpStatusCode.Conflict, closure.StatusCode);
            Assert.Equal("pr_verification_scope_unknown",
                (await closure.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        }

        var corrective = await client.GetFromJsonAsync<JsonElement>($"/api/problem-reports/{fixture.ReportId}/corrective-action");
        Assert.True(corrective.GetProperty("available").GetBoolean());
        Assert.Equal(fixture.TargetRevisionId, corrective.GetProperty("procedureRevisionId").GetGuid());
        Assert.Equal(fixture.OriginExecutionId, corrective.GetProperty("executionId").GetGuid());

        using (var notLater = await client.PostAsJsonAsync("/api/test-executions", new
        {
            projectId = fixture.ProjectId,
            procedureRevisionId = fixture.TargetRevisionId,
            softwareBuildId = fixture.TargetBuildId,
            retestOfExecutionId = fixture.OriginExecutionId,
            outcome = "Pass",
            configuration = "Corrective rig",
            determination = "A simultaneous result is not a successor.",
            evidenceReference = "controlled://pr-449/not-later",
            executedAt = new DateTimeOffset(2026, 8, 10, 12, 0, 0, TimeSpan.Zero),
        }))
        {
            Assert.Equal(HttpStatusCode.BadRequest, notLater.StatusCode);
            Assert.Equal("retest_not_successor",
                (await notLater.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        }

        // The target build carries revision 1 while the failure executed revision 0. Recording the retest
        // through the public API proves stable procedure lineage is accepted without reviving the obsolete
        // exact-revision restriction.
        using var recorded = await client.PostAsJsonAsync("/api/test-executions", new
        {
            projectId = fixture.ProjectId,
            procedureRevisionId = fixture.TargetRevisionId,
            softwareBuildId = fixture.TargetBuildId,
            retestOfExecutionId = fixture.OriginExecutionId,
            outcome = "Pass",
            configuration = "Corrective rig",
            determination = "The corrected behavior satisfies the effective successor procedure.",
            evidenceReference = "controlled://pr-449/successor",
            executedAt = new DateTimeOffset(2026, 8, 10, 12, 10, 0, TimeSpan.Zero),
        });
        Assert.Equal(HttpStatusCode.Created, recorded.StatusCode);
        var executionId = (await recorded.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        using var accepted = await client.PostAsJsonAsync($"/api/problem-reports/{fixture.ReportId}/verify", new
        {
            expectedVersion = fixture.ReportVersion, testExecutionId = executionId
        });
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        Assert.Equal("AwaitingSqaClosure", (await accepted.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("state").GetString());

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        var links = await db.ProblemReportLinks.AsNoTracking().Where(item => item.ProblemReportId == fixture.ReportId
            && item.Relationship == ProblemReportRelationshipPolicy.ResolutionVerification).ToListAsync();
        var link = Assert.Single(links);
        Assert.Equal(executionId, link.ArtifactId);
        Assert.Equal("admin", link.AddedBy);
    }

    private static async Task RejectAsync(HttpClient client, Guid reportId, long version, Guid executionId, string code)
    {
        using var response = await client.PostAsJsonAsync($"/api/problem-reports/{reportId}/verify", new
        {
            expectedVersion = version, testExecutionId = executionId
        });
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(code, (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
    }

    private static ProblemReport Report(Guid projectId, Guid releaseId, string number, DateTimeOffset now) =>
        new(projectId, number, "Verification chain", "Closure must use corrective evidence.", "", "admin", now,
            targetReleaseId: releaseId, responsibleEngineerId: "admin");

    private static void ProgressToVerifying(ProblemReport report, DateTimeOffset now)
    {
        report.ReadyForSccb("admin", now.AddMinutes(-3));
        report.OpenBySccb("sccb", now.AddMinutes(-2));
        report.BeginInvestigation("admin", "Root cause", "Cause", "Effect", "", now.AddMinutes(-1));
        report.ProposeResolution("admin", "Correct and retest.", now);
    }

    private static TestExecution Execution(Guid projectId, Guid revisionId, Guid? buildId, Guid? retestOf,
        TestOutcome outcome, DateTimeOffset executedAt, Guid releaseId) =>
        new(projectId, revisionId, buildId, retestOf, outcome, "tester", "Rig", "Determination",
            outcome == TestOutcome.Blocked ? "" : "controlled://evidence", executedAt, executedAt, releaseId);
}
