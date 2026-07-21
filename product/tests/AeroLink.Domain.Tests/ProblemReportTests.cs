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
        Assert.Equal(ProblemReportState.AwaitingClosureApproval, report.State);
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

    private static ProblemReport NewReport() => new(ProjectId, "PR-00001", "Unexpected navigation reset", "Unit reset while airborne.", "", "verification.engineer", Now, "Verification failure", ProblemReportSeverity.High, ProblemReportPriority.Urgent, "Test execution", "Build 1.6.0");
    private static ProblemReport ReadyForClosure()
    {
        var report = NewReport(); report.BeginInvestigation("verification.engineer", "Reproduced", "Timeout race", "Reset", "Guard", Now); report.ProposeResolution("verification.engineer", "Serialize command", Now); report.RecordResolutionVerification("verification.engineer", Guid.NewGuid(), Now); return report;
    }
}
