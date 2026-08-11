using AeroLink.Domain.Requirements;

namespace AeroLink.Domain.Tests;

public sealed class ProblemReportLifecycleTests
{
    public static TheoryData<ProblemReportState, bool> EveryState => new()
    {
        { ProblemReportState.Draft, true },
        { ProblemReportState.ReadyForSccb, true },
        { ProblemReportState.Open, true },
        { ProblemReportState.Implementing, true },
        { ProblemReportState.Verifying, true },
        { ProblemReportState.AwaitingSqaClosure, true },
        { ProblemReportState.Deferred, true },
        { ProblemReportState.Investigating, true },
        { ProblemReportState.ResolutionProposed, true },
        { ProblemReportState.AwaitingClosureApproval, true },
        { ProblemReportState.Closed, false },
        { ProblemReportState.Duplicate, false },
        { ProblemReportState.CannotReproduce, false },
        { ProblemReportState.NoFaultFound, false },
        { ProblemReportState.AcceptedRisk, false },
        { ProblemReportState.Rejected, false },
    };

    [Theory]
    [MemberData(nameof(EveryState))]
    public void Every_current_and_retained_state_has_an_explicit_active_work_classification(
        ProblemReportState state, bool expected)
    {
        Assert.Equal(expected, ProblemReportLifecycle.IsActiveWork(state));
    }

    [Fact]
    public void Classification_covers_the_complete_enum_exactly_once()
    {
        var classified = EveryState.Select(row => (ProblemReportState)row[0]).ToArray();
        Assert.Equal(Enum.GetValues<ProblemReportState>().Order(), classified.Order());
        Assert.Equal(classified.Length, classified.Distinct().Count());
        Assert.Equal(ProblemReportLifecycle.ActiveWorkStates.Order(),
            classified.Where(ProblemReportLifecycle.IsActiveWork).Order());
    }
}
