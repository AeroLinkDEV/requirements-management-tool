using System.Linq.Expressions;

namespace AeroLink.Domain.Requirements;

/// <summary>
/// The authoritative classification for Problem Report work that is still active. Legacy database strings are
/// normalized by migration before they can be exposed through this API.
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
        ProblemReportState.WaitingForSqaToClose,
    ];

    private static readonly Expression<Func<ProblemReport, bool>> ActiveWorkPredicateValue =
        BuildActiveWorkPredicate();

    public static IReadOnlyList<ProblemReportState> ActiveWorkStates { get; } =
        Array.AsReadOnly(ActiveWorkStateValues);

    public const string ActiveWorkDefinition =
        "Problem reports in Draft, Ready for SCCB, Open, Implementing, Verifying, or Waiting for SQA to Close.";

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
