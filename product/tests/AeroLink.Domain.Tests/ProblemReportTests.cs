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
        Assert.Equal(ProblemReportDisposition.Fixed, report.Disposition);
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
        Assert.Equal(ProblemReportState.AwaitingSqaClosure, report.State);
    }

    [Fact]
    public void Reopen_retains_history_by_advancing_the_controlled_revision()
    {
        var report = ReadyForClosure(); report.ApproveClosure("configuration.manager", Guid.NewGuid(), Now);
        report.Reopen("verification.engineer", "A field failure shows the fix is incomplete.", Now.AddMinutes(1));

        Assert.Equal(1, report.Revision);
        Assert.Equal("PR-00001.01", report.DisplayNumber);
        Assert.Equal(ProblemReportState.Open, report.State);
        Assert.Null(report.Disposition);
        Assert.Null(report.ResolutionVerificationExecutionId);
    }

    [Fact]
    public void Terminal_dispositions_require_reason_and_duplicate_target()
    {
        var report = NewReport();
        report.ReadyForSccb("verification.engineer", Now);
        report.OpenBySccb("change.board", Now);
        Assert.Throws<DomainException>(() => report.ApplyDisposition("verification.engineer", ProblemReportDisposition.Duplicate, "Same failure", null, Now));
        report.ApplyDisposition("verification.engineer", ProblemReportDisposition.Duplicate, "Same failure", Guid.NewGuid(), Now);
        Assert.Equal(ProblemReportState.Duplicate, report.State);
        Assert.Throws<DomainException>(() => report.BeginInvestigation("verification.engineer", "again", "", "", "", Now));
    }

    [Fact]
    public void A_release_blocker_can_be_waived_with_attributable_rationale()
    {
        var report = NewReport();
        report.SetReleaseBlocker("verification.engineer", true, "Safety board accepted temporary operational limitation.", Now);
        Assert.True(report.IsReleaseBlocker);
        Assert.Equal("verification.engineer", report.WaivedBy);
        Assert.NotEmpty(report.WaiverRationale);
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
        Assert.Equal(ProblemReportState.Deferred, report.State);

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
        Assert.Throws<DomainException>(() => report.SetReleaseBlocker("verification.engineer", true, "", Now));
    }

    private static ProblemReport NewReport() => new(ProjectId, "PR-00001", "Unexpected navigation reset", "Unit reset while airborne.", "", "verification.engineer", Now, "Verification failure", ProblemReportSeverity.High, ProblemReportPriority.Urgent, "Test execution", "Build 1.6.0");
    private static ProblemReport ReadyForClosure()
    {
        var report = NewReport(); report.ReadyForSccb("verification.engineer", Now); report.OpenBySccb("change.board", Now); report.BeginInvestigation("verification.engineer", "Reproduced", "Timeout race", "Reset", "Guard", Now); report.ProposeResolution("verification.engineer", "Serialize command", Now); report.RecordResolutionVerification("verification.engineer", Guid.NewGuid(), Now); return report;
    }
}
