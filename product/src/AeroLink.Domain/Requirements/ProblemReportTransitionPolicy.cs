using AeroLink.Domain.Identity;
using AeroLink.Domain.Common;

namespace AeroLink.Domain.Requirements;

/// <summary>
/// The Problem Report lifecycle is intentionally a small, local policy. It describes the state graph; the API
/// supplies project access and live Program authority before asking the aggregate to apply one edge.
/// </summary>
public static class ProblemReportTransitionPolicy
{
    public static IReadOnlyList<ProblemReportState> CanonicalStates { get; } =
    [
        ProblemReportState.Draft,
        ProblemReportState.ReadyForSccb,
        ProblemReportState.Open,
        ProblemReportState.Implementing,
        ProblemReportState.Verifying,
        ProblemReportState.WaitingForSqaToClose,
        ProblemReportState.Closed,
        ProblemReportState.Rejected,
    ];

    public static IReadOnlyList<ProgramRole> SccbOpeningRoles { get; } =
    [
        ProgramRole.ProjectEngineer,
        ProgramRole.SystemEngineeringLead,
        ProgramRole.SoftwareEngineeringLead,
        ProgramRole.SoftwareQualityAnalyst,
        ProgramRole.Airworthiness,
        ProgramRole.SystemTestLead,
        ProgramRole.SoftwareTestLead,
    ];

    public static ProblemReportState Canonical(ProblemReportState state) => state switch
    {
        ProblemReportState.Draft => ProblemReportState.Draft,
        ProblemReportState.ReadyForSccb => ProblemReportState.ReadyForSccb,
        ProblemReportState.Open => ProblemReportState.Open,
        ProblemReportState.Implementing => ProblemReportState.Implementing,
        ProblemReportState.Verifying => ProblemReportState.Verifying,
        ProblemReportState.WaitingForSqaToClose => ProblemReportState.WaitingForSqaToClose,
        ProblemReportState.Closed => ProblemReportState.Closed,
        ProblemReportState.Rejected => ProblemReportState.Rejected,
        _ => throw new DomainException($"The Problem Report state '{state}' is not supported."),
    };

    public static IReadOnlyList<ProblemReportState> AllowedTargets(ProblemReportState state) => Canonical(state) switch
    {
        ProblemReportState.Draft => [ProblemReportState.ReadyForSccb, ProblemReportState.Rejected],
        ProblemReportState.ReadyForSccb => [ProblemReportState.Open, ProblemReportState.Draft, ProblemReportState.Rejected],
        ProblemReportState.Open => [ProblemReportState.Implementing, ProblemReportState.ReadyForSccb, ProblemReportState.Draft, ProblemReportState.Rejected],
        ProblemReportState.Implementing => [ProblemReportState.Verifying, ProblemReportState.Open, ProblemReportState.ReadyForSccb, ProblemReportState.Draft, ProblemReportState.Rejected],
        ProblemReportState.Verifying => [ProblemReportState.WaitingForSqaToClose, ProblemReportState.Implementing, ProblemReportState.Open, ProblemReportState.ReadyForSccb, ProblemReportState.Draft, ProblemReportState.Rejected],
        ProblemReportState.WaitingForSqaToClose => [ProblemReportState.Closed, ProblemReportState.Verifying, ProblemReportState.Implementing, ProblemReportState.Open, ProblemReportState.ReadyForSccb, ProblemReportState.Draft, ProblemReportState.Rejected],
        ProblemReportState.Closed => [ProblemReportState.Verifying],
        ProblemReportState.Rejected => [ProblemReportState.Draft],
        _ => [],
    };

    public static bool IsAllowed(ProblemReportState from, ProblemReportState to) =>
        AllowedTargets(from).Contains(Canonical(to));

    public static bool RequiresRationale(ProblemReportState from, ProblemReportState to)
    {
        var source = Canonical(from); var target = Canonical(to);
        return target == ProblemReportState.Rejected ||
            (source, target) is
                (ProblemReportState.ReadyForSccb, ProblemReportState.Draft) or
                (ProblemReportState.Open, ProblemReportState.ReadyForSccb) or
                (ProblemReportState.Open, ProblemReportState.Draft) or
                (ProblemReportState.Implementing, ProblemReportState.Open) or
                (ProblemReportState.Implementing, ProblemReportState.ReadyForSccb) or
                (ProblemReportState.Implementing, ProblemReportState.Draft) or
                (ProblemReportState.Verifying, ProblemReportState.Implementing) or
                (ProblemReportState.Verifying, ProblemReportState.Open) or
                (ProblemReportState.Verifying, ProblemReportState.ReadyForSccb) or
                (ProblemReportState.Verifying, ProblemReportState.Draft) or
                (ProblemReportState.WaitingForSqaToClose, ProblemReportState.Verifying) or
                (ProblemReportState.WaitingForSqaToClose, ProblemReportState.Implementing) or
                (ProblemReportState.WaitingForSqaToClose, ProblemReportState.Open) or
                (ProblemReportState.WaitingForSqaToClose, ProblemReportState.ReadyForSccb) or
                (ProblemReportState.WaitingForSqaToClose, ProblemReportState.Draft) or
                (ProblemReportState.Closed, ProblemReportState.Verifying) or
                (ProblemReportState.Rejected, ProblemReportState.Draft);
    }

    public static bool IsSqaOnly(ProblemReportState from, ProblemReportState to) =>
        (Canonical(from), Canonical(to)) is
            (ProblemReportState.WaitingForSqaToClose, ProblemReportState.Closed) or
            (ProblemReportState.WaitingForSqaToClose, ProblemReportState.Rejected) or
            (ProblemReportState.Closed, ProblemReportState.Verifying) or
            (ProblemReportState.Rejected, ProblemReportState.Draft);

    public static bool IsSccbOpening(ProblemReportState from, ProblemReportState to) =>
        Canonical(from) == ProblemReportState.ReadyForSccb && Canonical(to) == ProblemReportState.Open;
}
