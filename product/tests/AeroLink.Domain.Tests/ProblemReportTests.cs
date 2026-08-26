using AeroLink.Domain.Common;
using AeroLink.Domain.Requirements;

namespace AeroLink.Domain.Tests;

public sealed class ProblemReportTests
{
    private static readonly Guid ProjectId = Guid.NewGuid();
    private static readonly DateTimeOffset Now = new(2026, 7, 21, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Failed_execution_problem_can_be_investigated_verified_and_independently_closed()
    {
        var report = NewReport();

        report.ReadyForSccb("verification.engineer", Now);
        report.OpenBySccb("change.board", Now);
        report.BeginInvestigation("verification.engineer", "Reset sequence reproduces under load.", "Timeout race", "Navigation reset", "Disable automatic retry", Now.AddMinutes(1));
        report.ProposeResolution("verification.engineer", "Serialize the reset command and add a guard.", Now.AddMinutes(2));
        var executionId = Guid.NewGuid();
        report.RecordResolutionVerification("verification.engineer", executionId, Now.AddMinutes(3));
        report.ApproveClosure("configuration.manager", Guid.NewGuid(), Now.AddMinutes(4));

        Assert.Equal(ProblemReportState.Closed, report.State);
        Assert.Null(report.Disposition);
        Assert.Equal(executionId, report.ResolutionVerificationExecutionId);
        Assert.Equal("configuration.manager", report.ClosureApprovedByName);
        Assert.Equal("PR-00001.00", report.DisplayNumber);
    }

    [Fact]
    public void Owner_cannot_independently_close_own_problem_report()
    {
        var report = ReadyForClosure();
        var ex = Assert.Throws<DomainException>(() => report.ApproveClosure("verification.engineer", Guid.NewGuid(), Now));
        Assert.Contains("cannot independently approve", ex.Message);
        Assert.Equal(ProblemReportState.WaitingForSqaToClose, report.State);
    }

    [Fact]
    public void Closure_significant_change_returns_the_report_to_verification_without_erasing_history_identity()
    {
        var report = ReadyForClosure();
        var selectedExecution = report.ResolutionVerificationExecutionId;

        report.UpdateDetails("verification.engineer", report.Title, report.Problem, "", "", "", "Revised analysis",
            "Revised root cause", "Revised corrective action", "Revised aircraft impact", "{}",
            ProblemReportSeverity.Critical, ProblemReportPriority.Urgent, Now.AddMinutes(1),
            ProblemReportCategory.CodeFunctional, "Use the guarded operating mode.");

        Assert.Equal(ProblemReportState.Verifying, report.State);
        Assert.Null(report.ResolutionVerificationExecutionId);
        Assert.NotEqual(Guid.Empty, selectedExecution);
        Assert.Equal("Revised corrective action", report.CorrectiveAction);
    }

    [Fact]
    public void Closure_candidate_is_immutable_and_records_invalidation_or_approval_as_attributable_state()
    {
        var candidate = new ProblemReportClosureCandidate(Guid.NewGuid(), 0, 1, 1, 7,
            "{\"report\":true}", new string('a', 64), Guid.NewGuid(), "{\"execution\":true}",
            new string('b', 64), "{\"links\":[]}", new string('c', 64), new string('d', 64),
            "verification.engineer", Now, reportSnapshotSchemaVersion: 1);

        candidate.Invalidate("verification.engineer", "DetailsCheckedIn", Now.AddMinutes(1));
        Assert.Throws<DomainException>(() =>
            candidate.Approve("quality.engineer", Guid.NewGuid(), Now.AddMinutes(2),
                "{\"package\":true}", new string('e', 64)));

        Assert.Equal(ProblemReportClosureCandidateState.Invalidated, candidate.State);
        Assert.Equal("DetailsCheckedIn", candidate.InvalidationReason);
        Assert.Null(candidate.ApprovedAt);
    }

    [Fact]
    public void Reopen_retains_history_by_advancing_the_controlled_revision()
    {
        var report = ReadyForClosure(); report.ApproveClosure("configuration.manager", Guid.NewGuid(), Now);
        report.Reopen("verification.engineer", "A field failure shows the fix is incomplete.", Now.AddMinutes(1));

        Assert.Equal(1, report.Revision);
        Assert.Equal("PR-00001.01", report.DisplayNumber);
        Assert.Equal(ProblemReportState.Verifying, report.State);
        Assert.Null(report.Disposition);
        Assert.Null(report.ResolutionVerificationExecutionId);
    }

    [Fact]
    public void Terminal_dispositions_require_reason_and_duplicate_target()
    {
        var report = NewReport();
        report.ReadyForSccb("verification.engineer", Now);
        report.OpenBySccb("change.board", Now);
        Assert.Throws<DomainException>(() => report.ApplyDisposition("verification.engineer", ProblemReportDisposition.CannotReproduce, " ", null, Now));
        Assert.Throws<DomainException>(() => report.ApplyDisposition("verification.engineer", ProblemReportDisposition.Fixed, "Generic fixed result", null, Now));
        Assert.Throws<DomainException>(() => report.ApplyDisposition("verification.engineer", ProblemReportDisposition.Duplicate, "Same failure", null, Now));
        report.ApplyDisposition("verification.engineer", ProblemReportDisposition.Duplicate, "Same failure", Guid.NewGuid(), Now);
        Assert.Equal(ProblemReportState.Rejected, report.State);
        Assert.Throws<DomainException>(() => report.BeginInvestigation("verification.engineer", "again", "", "", "", Now));
    }

    [Fact]
    public void A_responsible_engineer_can_mark_a_blocker_but_cannot_create_waiver_evidence()
    {
        var report = NewReport();
        report.SetReleaseBlocker("verification.engineer", true, Now);
        Assert.True(report.IsReleaseBlocker);
        Assert.True(report.ReleaseBlockerVersion > 0);
        Assert.Empty(report.WaivedBy);
        Assert.Empty(report.WaiverRationale);
    }

    [Fact]
    public void A_controlled_waiver_expires_and_cannot_carry_into_a_reopened_revision()
    {
        var report = NewReport();
        report.SetReleaseBlocker("verification.engineer", true, Now);
        report.RecordReleaseWaiverDecision("independent.quality", Now.AddMinutes(1));
        var waiver = new ReadinessWaiver(report.ProjectId, "ProblemReportReleaseBlocker", report.Id,
            report.Revision, report.ReleaseBlockerVersion, "Temporary bounded release decision.", Guid.NewGuid(),
            "independent.quality", "SoftwareQualityAnalyst", "IndependentProblemReportReleaseWaiver",
            Now.AddHours(1), "independent.quality", Now.AddMinutes(1));
        Assert.True(waiver.IsActiveFor(report, Now.AddMinutes(2)));
        Assert.False(waiver.IsActiveFor(report, Now.AddHours(2)));

        report.ReadyForSccb("verification.engineer", Now.AddMinutes(2));
        report.OpenBySccb("change.board", Now.AddMinutes(3));
        report.ApplyDisposition("verification.engineer", ProblemReportDisposition.AcceptedRisk,
            "Closed under the original bounded decision.", null, Now.AddMinutes(4));
        report.Reopen("verification.engineer", "The known problem recurred.", Now.AddMinutes(5));
        Assert.False(waiver.IsActiveFor(report, Now.AddMinutes(6)));
        Assert.True(report.IsReleaseBlocker);
        Assert.NotEqual(waiver.BlockerVersion, report.ReleaseBlockerVersion);
    }

    [Fact]
    public void Agreed_lifecycle_requires_SCCB_open_and_supports_defer_and_resume()
    {
        var report = NewReport();
        Assert.Equal(ProblemReportState.Draft, report.State);
        Assert.Throws<DomainException>(() => report.BeginImplementation("verification.engineer", Now));

        report.ReadyForSccb("verification.engineer", Now);
        report.OpenBySccb("change.board", Now);
        report.ApplyDisposition("verification.engineer", ProblemReportDisposition.Deferred, "Waiting for the supplier qualification build.", null, Now);
        Assert.Equal(ProblemReportState.Open, report.State);

        report.ResumeDeferred("verification.engineer", Now.AddDays(1));
        Assert.Equal(ProblemReportState.Open, report.State);
    }

    [Fact]
    public void Responsibility_target_build_and_structured_impact_are_controlled_data()
    {
        var release = Guid.NewGuid();
        var report = NewReport();
        report.UpdateDetails("verification.engineer", report.Title, report.Problem, "{\"blocks\":[]}", "Observed twice.", "{\"blocks\":[]}", "", "", "", "Aircraft effect under assessment", "{\"SystemRequirements\":\"Yes\",\"Code\":\"Unknown\"}", ProblemReportSeverity.High, ProblemReportPriority.Urgent, Now);
        report.Retarget("verification.engineer", release, Now);
        report.Reassign("verification.engineer", "software.lead", Now);

        Assert.Equal(release, report.TargetReleaseId);
        Assert.Equal("software.lead", report.ResponsibleEngineerId);
        Assert.Contains("SystemRequirements", report.ImpactAssessmentJson);
        Assert.Throws<DomainException>(() => report.SetReleaseBlocker("verification.engineer", true, Now));
    }

    [Fact]
    public void A_report_is_unclassified_until_somebody_says_what_kind_of_problem_it_is()
    {
        // Raised without one, unlike the shared fixture, because that is the state under test here.
        var report = new ProblemReport(ProjectId, "PR-00002", "Unexpected navigation reset",
            "Unit reset while airborne.", "", "verification.engineer", Now);

        // A Draft nobody has classified says so, rather than defaulting to a category that would read as
        // an answer somebody gave.
        Assert.Null(report.Category);
        Assert.Null(report.CategoryProvenance);
        Assert.Equal("", report.Workaround);

        report.UpdateDetails("verification.engineer", report.Title, report.Problem, "", "", "", "", "", "", "", "{}",
            report.Severity, report.Priority, Now.AddMinutes(1),
            ProblemReportCategory.RequirementsDocumentation, "Fly the approach manually until the database is reissued.");

        Assert.Equal(ProblemReportCategory.RequirementsDocumentation, report.Category);
        // Choosing one on the form is a person's judgement, and the record says so from then on.
        Assert.Equal(ProblemReportCategoryProvenance.Selected, report.CategoryProvenance);
        Assert.Equal("Fly the approach manually until the database is reissued.", report.Workaround);
    }

    [Fact]
    public void An_impact_recorded_against_Safety_is_kept_under_Airworthiness()
    {
        var report = NewReport();

        // The area was renamed for what is actually being judged. A record written under the old name — or a
        // client that has not been reloaded — keeps its answer instead of losing it to a key nothing reads.
        report.UpdateDetails("verification.engineer", report.Title, report.Problem, "", "", "", "", "", "", "",
            """{"Safety":"Yes","Code":"No"}""", report.Severity, report.Priority, Now.AddMinutes(1));

        Assert.Contains("\"Airworthiness\":\"Yes\"", report.ImpactAssessmentJson);
        Assert.DoesNotContain("\"Safety\"", report.ImpactAssessmentJson);
        Assert.Contains("\"Code\":\"No\"", report.ImpactAssessmentJson);
    }

    // Classified where it is raised, because almost every test here walks the report past Ready for SCCB
    // and that transition refuses an unclassified Draft. ProblemReportCategoryTests covers the unclassified
    // case on its own fixture.
    private static ProblemReport NewReport() => new(ProjectId, "PR-00001", "Unexpected navigation reset", "Unit reset while airborne.", "", "verification.engineer", Now, "Verification failure", ProblemReportSeverity.High, ProblemReportPriority.Urgent, "Test execution", "Build 1.6.0", category: ProblemReportCategory.CodeFunctional);
    private static ProblemReport ReadyForClosure()
    {
        var report = NewReport(); report.ReadyForSccb("verification.engineer", Now); report.OpenBySccb("change.board", Now); report.BeginInvestigation("verification.engineer", "Reproduced", "Timeout race", "Reset", "Guard", Now); report.ProposeResolution("verification.engineer", "Serialize command", Now); report.RecordResolutionVerification("verification.engineer", Guid.NewGuid(), Now); return report;
    }
}
