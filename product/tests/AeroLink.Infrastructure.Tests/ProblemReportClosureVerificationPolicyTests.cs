using AeroLink.Domain.Baselines;
using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Programs;
using AeroLink.Domain.Releases;
using AeroLink.Domain.Requirements;
using AeroLink.Domain.Verification;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AeroLink.Infrastructure.Tests;

public sealed class ProblemReportClosureVerificationPolicyTests
{
    [Fact]
    public async Task Projection_uses_the_target_builds_effective_successor_revision_and_causal_retest()
    {
        var path = Path.Combine(Path.GetTempPath(), $"aerolink-pr-verification-{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<AeroLinkDbContext>()
            .UseSqlite($"Data Source={path};Pooling=False").Options;
        try
        {
            await using var db = new AeroLinkDbContext(options);
            await db.Database.EnsureCreatedAsync();
            var start = new DateTimeOffset(2026, 8, 10, 12, 0, 0, TimeSpan.Zero);
            var program = new ProgramRecord("Closure projection", "CLP");
            var project = new ProjectRecord(program.Id, "FMS", "Closure FMS");
            var originRelease = new SoftwareRelease(project.Id, "1.5", false);
            var targetRelease = new SoftwareRelease(project.Id, "1.6", false, originRelease.Id);
            var originBaseline = new CandidateBaseline("SW-09.50", 0, project.Id, originRelease.Id, null, "Origin", "cm", start);
            var targetBaseline = new CandidateBaseline("SW-09.60", 0, project.Id, targetRelease.Id, originBaseline.Id, "Target", "cm", start);
            var originBuild = new SoftwareBuild(project.Id, originRelease.Id, originBaseline.Id, "SW-09.50", "Origin", "cm", start);
            var targetBuild = new SoftwareBuild(project.Id, targetRelease.Id, targetBaseline.Id, "SW-09.60", "Target", "cm", start);
            var change = new SystemChangeRequest("SRCR-09500", 0, project.Id, targetRelease.Id,
                "Correct behavior", "Problem", "Analysis", "Solution", "engineer", start);
            var tcr = new TestChangeReview(project.Id, targetRelease.Id, change.Id,
                TestChangeReviewDiscipline.System, "SRCR-09500", start, "SYSTCR-09500");
            var procedure = new TestProcedure(project.Id, "SYSTP-009500", "Procedure", "test", start, TestProcedureLevel.System);
            var revision0 = new TestProcedureRevision(procedure.Id, 0, "Original", "Pre", "Steps", "Expected",
                TestProcedureState.Approved, "test", start, effectiveBaselineId: originBaseline.Id);
            var revision1 = new TestProcedureRevision(procedure.Id, 1, "Corrected", "Pre", "New steps", "Expected",
                TestProcedureState.Approved, "test", start.AddMinutes(2), sourceTestChangeRequestId: tcr.Id,
                effectiveBaselineId: targetBaseline.Id);
            var failure = new TestExecution(project.Id, revision0.Id, originBuild.Id, null, TestOutcome.Fail,
                "test", "Rig", "Failed", "controlled://failure", start, start, originRelease.Id);
            var report = new ProblemReport(project.Id, "PR-09500", "Failure", "Problem", "", "engineer", start.AddMinutes(1),
                targetReleaseId: targetRelease.Id, responsibleEngineerId: "engineer");
            report.ReadyForSccb("engineer", start.AddMinutes(2));
            report.OpenBySccb("sccb", start.AddMinutes(3));
            report.BeginInvestigation("engineer", "Analysis", "Cause", "Effect", "", start.AddMinutes(4));
            report.ProposeResolution("engineer", "Correct and retest", start.AddMinutes(5));
            var successor = new TestExecution(project.Id, revision1.Id, targetBuild.Id, failure.Id, TestOutcome.Pass,
                "test", "Rig", "Passed", "controlled://successor", start.AddMinutes(6), start.AddMinutes(6), targetRelease.Id);
            var manual = new ProblemReport(project.Id, "PR-09501", "Manual", "Problem", "", "engineer", start.AddMinutes(1),
                targetReleaseId: targetRelease.Id, responsibleEngineerId: "engineer");
            manual.ReadyForSccb("engineer", start.AddMinutes(2));
            manual.OpenBySccb("sccb", start.AddMinutes(3));
            manual.BeginInvestigation("engineer", "Analysis", "Cause", "Effect", "", start.AddMinutes(4));
            manual.ProposeResolution("engineer", "Correct and verify", start.AddMinutes(5));
            var manualPass = new TestExecution(project.Id, revision1.Id, targetBuild.Id, null, TestOutcome.Pass,
                "test", "Rig", "Passed", "controlled://manual", start.AddMinutes(6), start.AddMinutes(6), targetRelease.Id);
            db.AddRange(program, project, originRelease, targetRelease, originBaseline, targetBaseline,
                originBuild, targetBuild, change, tcr, procedure, revision0, revision1, failure, successor, report, manual, manualPass,
                new BaselineTestProcedureSelection(originBaseline.Id, procedure.Id, revision0.Id),
                new BaselineTestProcedureSelection(targetBaseline.Id, procedure.Id, revision1.Id),
                ProblemReportRelationshipPolicy.CreateControlled(report.Id, "TestExecution", failure.Id,
                    ProblemReportRelationshipPolicy.OriginatingFailure, ProblemReportRelationshipProducer.FailureCreationWorkflow, "engineer", start),
                ProblemReportRelationshipPolicy.CreateControlled(manual.Id, "TestChangeRequest", tcr.Id,
                    ProblemReportRelationshipPolicy.VerificationForProblem, ProblemReportRelationshipProducer.TestChangeRequestWorkflow, "engineer", start));
            await db.SaveChangesAsync();
            var baselineIds = new[] { originBaseline.Id, targetBaseline.Id };
            await db.CandidateBaselines.Where(item => baselineIds.Contains(item.Id)).ExecuteUpdateAsync(update => update
                .SetProperty(item => item.RequirementsMaterializedAt, start)
                .SetProperty(item => item.TestProceduresMaterializedAt, start)
                .SetProperty(item => item.TestProceduresHash, "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"));
            await db.TestChangeReviews.Where(item => item.Id == tcr.Id)
                .ExecuteUpdateAsync(update => update.SetProperty(item => item.State, TestChangeReviewState.Approved));

            var policy = new ProblemReportClosureVerificationPolicy(db);
            var projection = await policy.ResolveAsync(report, default);
            Assert.True(projection.IsResolved);
            Assert.Equal(failure.Id, projection.OriginExecutionId);
            Assert.Equal(procedure.Id, projection.ProcedureId);
            Assert.Equal(revision1.Id, Assert.Single(projection.PermittedProcedureRevisionIds));
            var decision = await policy.ValidateAsync(report, successor, default);
            Assert.True(decision.Accepted, decision.Error);
            var manualProjection = await policy.ResolveAsync(manual, default);
            Assert.True(manualProjection.IsResolved, manualProjection.Error);
            Assert.Equal(revision1.Id, Assert.Single(manualProjection.PermittedProcedureRevisionIds));
            var manualDecision = await policy.ValidateAsync(manual, manualPass, default);
            Assert.True(manualDecision.Accepted, manualDecision.Error);

            // The deterministic same-instant case: the retest's server recording instant equals the instant
            // the corrective action entered verification. Equality is not evidence the retest came first,
            // and the structural lineage proves it succeeded the failure, so it must be accepted.
            var equalInstant = new TestExecution(project.Id, revision1.Id, targetBuild.Id, failure.Id, TestOutcome.Pass,
                "test", "Rig", "Passed", "controlled://equal-instant", start.AddMinutes(5), start.AddMinutes(5), targetRelease.Id);
            db.TestExecutions.Add(equalInstant);
            await db.SaveChangesAsync();
            var equalDecision = await policy.ValidateAsync(report, equalInstant, default);
            Assert.True(equalDecision.Accepted, equalDecision.Error);
            var earlierRecording = new TestExecution(project.Id, revision1.Id, targetBuild.Id, failure.Id, TestOutcome.Pass,
                "test", "Rig", "Passed", "controlled://earlier-recording", start.AddMinutes(4), start.AddMinutes(4), targetRelease.Id);
            db.TestExecutions.Add(earlierRecording);
            await db.SaveChangesAsync();
            var earlierDecision = await policy.ValidateAsync(report, earlierRecording, default);
            Assert.False(earlierDecision.Accepted);
            Assert.Equal("pr_verification_not_successor", earlierDecision.Code);

            // Once verification has transitioned away, the verification-ready instant must come from the
            // structural ResolutionProposed event selected by its monotonic revision number - never from
            // wall-clock order. An older-revision event carrying a later clock value must not win.
            db.ProblemReportRevisions.AddRange(
                new ProblemReportRevision(report.Id, 3, "ResolutionProposed", "engineer", new string('a', 64), "{}", start.AddMinutes(9)),
                new ProblemReportRevision(report.Id, 5, "ResolutionProposed", "engineer", new string('a', 64), "{}", start.AddMinutes(5)));
            await db.SaveChangesAsync();
            report.RecordResolutionVerification("engineer", successor.Id, start.AddMinutes(10));
            await db.SaveChangesAsync();
            var afterTransition = await policy.ResolveAsync(report, default);
            Assert.Equal(start.AddMinutes(5), afterTransition.VerificationReadyAt);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
