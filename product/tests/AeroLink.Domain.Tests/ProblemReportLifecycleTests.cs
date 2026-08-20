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
        { ProblemReportState.WaitingForSqaToClose, true },
        { ProblemReportState.Closed, false },
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
        Assert.Equal(ProblemReportTransitionPolicy.CanonicalStates.Order(), classified.Order());
        Assert.Equal(classified.Length, classified.Distinct().Count());
        Assert.Equal(ProblemReportLifecycle.ActiveWorkStates.Order(),
            classified.Where(ProblemReportLifecycle.IsActiveWork).Order());
    }

    [Fact]
    public void Rejection_and_backward_edges_require_rationale_but_forward_waiting_does_not()
    {
        Assert.True(ProblemReportTransitionPolicy.RequiresRationale(ProblemReportState.Open, ProblemReportState.Rejected));
        Assert.True(ProblemReportTransitionPolicy.RequiresRationale(ProblemReportState.Verifying, ProblemReportState.Implementing));
        Assert.False(ProblemReportTransitionPolicy.RequiresRationale(ProblemReportState.Verifying, ProblemReportState.WaitingForSqaToClose));
        Assert.True(ProblemReportTransitionPolicy.IsSqaOnly(ProblemReportState.WaitingForSqaToClose, ProblemReportState.Closed));
        Assert.True(ProblemReportTransitionPolicy.IsSccbOpening(ProblemReportState.ReadyForSccb, ProblemReportState.Open));
    }

    [Fact]
    public void Canonical_graph_exposes_only_the_agreed_eight_states()
    {
        Assert.Equal(new[]
        {
            ProblemReportState.Draft, ProblemReportState.ReadyForSccb, ProblemReportState.Open,
            ProblemReportState.Implementing, ProblemReportState.Verifying,
            ProblemReportState.WaitingForSqaToClose, ProblemReportState.Closed, ProblemReportState.Rejected
        }, ProblemReportTransitionPolicy.CanonicalStates);
        Assert.DoesNotContain(ProblemReportState.Closed, ProblemReportTransitionPolicy.AllowedTargets(ProblemReportState.Draft));
        Assert.Contains(ProblemReportState.Verifying, ProblemReportTransitionPolicy.AllowedTargets(ProblemReportState.Closed));
        Assert.Contains(ProblemReportState.Draft, ProblemReportTransitionPolicy.AllowedTargets(ProblemReportState.Rejected));
    }
}
