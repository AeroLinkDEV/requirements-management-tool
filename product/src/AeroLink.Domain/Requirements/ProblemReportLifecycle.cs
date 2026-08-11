using System.Linq.Expressions;

namespace AeroLink.Domain.Requirements;

/// <summary>
/// The authoritative classification for Problem Report work that is still active.
///
/// The retained legacy states are active predecessor-lifecycle stages, not dispositions. Keeping them here
/// makes old controlled records contribute consistently until they progress through an authorized transition.
/// Terminal dispositions remain discoverable history but do not represent open engineering work.
/// </summary>
public static class ProblemReportLifecycle
{
    private static readonly ProblemReportState[] ActiveWorkStateValues =
    [
        ProblemReportState.Draft,
        ProblemReportState.ReadyForSccb,
        ProblemReportState.Open,
        ProblemReportState.Implementing,
        ProblemReportState.Verifying,
        ProblemReportState.AwaitingSqaClosure,
        ProblemReportState.Deferred,
        ProblemReportState.Investigating,
        ProblemReportState.ResolutionProposed,
        ProblemReportState.AwaitingClosureApproval,
    ];

    private static readonly Expression<Func<ProblemReport, bool>> ActiveWorkPredicateValue =
        BuildActiveWorkPredicate();

    public static IReadOnlyList<ProblemReportState> ActiveWorkStates { get; } =
        Array.AsReadOnly(ActiveWorkStateValues);

    public const string ActiveWorkDefinition =
        "Problem reports in Draft, Ready for SCCB, Open, Implementing, Verifying, Awaiting SQA Closure, " +
        "Deferred, or the retained active legacy stages Investigating, Resolution Proposed, and Awaiting Closure Approval.";

    public static bool IsActiveWork(ProblemReportState state) => ActiveWorkStateValues.Contains(state);

    /// <summary>An EF-translatable predicate generated from the same state set as <see cref="IsActiveWork"/>.</summary>
    public static Expression<Func<ProblemReport, bool>> ActiveWorkPredicate => ActiveWorkPredicateValue;

    private static Expression<Func<ProblemReport, bool>> BuildActiveWorkPredicate()
    {
        var report = Expression.Parameter(typeof(ProblemReport), "report");
        var state = Expression.Property(report, nameof(ProblemReport.State));
        var body = ActiveWorkStateValues
            .Select(value => (Expression)Expression.Equal(state, Expression.Constant(value)))
            .Aggregate(Expression.OrElse);
        return Expression.Lambda<Func<ProblemReport, bool>>(body, report);
    }
}
