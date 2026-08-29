using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Common;
using AeroLink.Domain.Requirements;
using AeroLink.Domain.Verification;

namespace AeroLink.Domain.TeamWork;

/// <summary>
/// The four semantic lanes exposed by the Team Work projection.
///
/// Build allocation is deliberately not a lane. It is separate metadata on a controlled item, and a
/// deferred item retains the lane that describes how far it had got before it was put away.
/// </summary>
public enum TeamWorkLane
{
    InWork,
    InReview,
    AwaitingSignature,
    Approved,
}

/// <summary>The controlled-record families that Team Work may project.</summary>
public enum TeamWorkRecordFamily
{
    SystemChangeRequest,
    SoftwareChangeRequest,
    InterfaceChangeRequest,
    TestChangeReview,
    ProblemReport,
    Assessment,
}

/// <summary>
/// Stable provenance for a current holder. This is an obligation explanation, not a person's job or project
/// position. In particular, Review and Approval are meanings frozen on a review step, not ProgramRole values.
/// </summary>
public enum TeamWorkHolderBasis
{
    None,
    Author,
    AssignedEngineer,
    ResponsibleEngineer,
    ActiveReviewStage,
    ActiveApprovalStage,
    ActiveReviewAndApprovalStages,
    SelectedAssessmentApprover,
}

/// <summary>
/// Result of mapping a native lifecycle state to Team Work. A null lane is an explicit off-board result; it
/// is not an unknown state and cannot be mistaken for one of the four lanes.
/// </summary>
public sealed record TeamWorkLaneDecision
{
    private TeamWorkLaneDecision(TeamWorkLane? lane, bool isDeferred)
    {
        if (lane is { } value)
        {
            _ = value switch
            {
                TeamWorkLane.InWork or TeamWorkLane.InReview or TeamWorkLane.AwaitingSignature
                    or TeamWorkLane.Approved => value,
                _ => throw new DomainException($"The Team Work lane '{value}' is not supported."),
            };
        }

        if (isDeferred && lane is null)
            throw new DomainException("An off-board Team Work item cannot carry deferred allocation metadata.");

        Lane = lane;
        IsDeferred = isDeferred;
    }

    public TeamWorkLane? Lane { get; }
    public bool IsOnBoard => Lane is not null;
    public bool IsOffBoard => Lane is null;
    public bool IsDeferred { get; }

    public static TeamWorkLaneDecision OnBoard(TeamWorkLane lane, bool isDeferred = false) =>
        new(lane, isDeferred);

    public static TeamWorkLaneDecision OffBoard { get; } = new(null, false);
}

/// <summary>The current-holder projection, retaining zero, one, or many obligations.</summary>
public sealed record TeamWorkHolderResolution
{
    public TeamWorkHolderResolution(IEnumerable<string> currentHolderIds, TeamWorkHolderBasis holderBasis)
    {
        ArgumentNullException.ThrowIfNull(currentHolderIds);

        CurrentHolderIds = currentHolderIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        HolderBasis = holderBasis switch
        {
            TeamWorkHolderBasis.None or TeamWorkHolderBasis.Author or TeamWorkHolderBasis.AssignedEngineer
                or TeamWorkHolderBasis.ResponsibleEngineer or TeamWorkHolderBasis.ActiveReviewStage
                or TeamWorkHolderBasis.ActiveApprovalStage or TeamWorkHolderBasis.ActiveReviewAndApprovalStages
                or TeamWorkHolderBasis.SelectedAssessmentApprover => holderBasis,
            _ => throw new DomainException($"The Team Work holder basis '{holderBasis}' is not supported."),
        };
    }

    public IReadOnlyList<string> CurrentHolderIds { get; }
    public TeamWorkHolderBasis HolderBasis { get; }

    public static TeamWorkHolderResolution None { get; } =
        new(Array.Empty<string>(), TeamWorkHolderBasis.None);
}

/// <summary>
/// The frozen fields needed by Team Work to interpret one review obligation. The API creates these from the
/// persisted <see cref="ApprovalStep"/> rows; policy tests can construct them without an aggregate.
/// </summary>
public sealed record TeamWorkReviewStep
{
    public TeamWorkReviewStep(string holderId, ReviewStageKind stageKind, ApprovalStepState state)
    {
        if (string.IsNullOrWhiteSpace(holderId))
            throw new DomainException("An active Team Work review step requires a holder identity.");

        HolderId = holderId;
        StageKind = stageKind switch
        {
            ReviewStageKind.Review or ReviewStageKind.Approval => stageKind,
            _ => throw new DomainException($"The review stage kind '{stageKind}' is not supported."),
        };
        State = state switch
        {
            ApprovalStepState.Pending or ApprovalStepState.Active or ApprovalStepState.Approved
                or ApprovalStepState.Returned => state,
            _ => throw new DomainException($"The approval-step state '{state}' is not supported."),
        };
    }

    public string HolderId { get; }
    /// <summary>Alias for callers mapping the persisted ApprovalStep vocabulary.</summary>
    public string ApproverId => HolderId;
    public ReviewStageKind StageKind { get; }
    public ApprovalStepState State { get; }

    public static TeamWorkReviewStep From(ApprovalStep step)
    {
        ArgumentNullException.ThrowIfNull(step);
        return new(step.ApproverId, step.StageKind, step.State);
    }
}

/// <summary>The result of applying the frozen active-review-stage overlay.</summary>
public sealed record TeamWorkReviewOverlayResult(
    TeamWorkLaneDecision LaneDecision,
    TeamWorkHolderResolution HolderResolution);

/// <summary>
/// Applies review truth to a base InReview item. Every active step contributes a holder. An Approval stage
/// changes the lane to AwaitingSignature, but does not hide parallel Review holders.
/// </summary>
public static class TeamWorkReviewOverlay
{
    public static TeamWorkReviewOverlayResult Resolve(IEnumerable<TeamWorkReviewStep> steps)
    {
        ArgumentNullException.ThrowIfNull(steps);
        var all = steps.ToArray();

        // Validate even inactive rows. A persisted enum value that this policy does not understand must not
        // disappear merely because it is not active today.
        foreach (var step in all)
        {
            _ = step.StageKind switch
            {
                ReviewStageKind.Review or ReviewStageKind.Approval => step.StageKind,
                _ => throw new DomainException($"The review stage kind '{step.StageKind}' is not supported."),
            };
            _ = step.State switch
            {
                ApprovalStepState.Pending or ApprovalStepState.Active or ApprovalStepState.Approved
                    or ApprovalStepState.Returned => step.State,
                _ => throw new DomainException($"The approval-step state '{step.State}' is not supported."),
            };
        }

        var active = all.Where(step => step.State == ApprovalStepState.Active).ToArray();
        if (active.Length == 0)
            return new(
                TeamWorkLaneDecision.OnBoard(TeamWorkLane.InReview),
                TeamWorkHolderResolution.None);

        var hasReview = active.Any(step => step.StageKind == ReviewStageKind.Review);
        var hasApproval = active.Any(step => step.StageKind == ReviewStageKind.Approval);
        var lane = hasApproval ? TeamWorkLane.AwaitingSignature : TeamWorkLane.InReview;
        var basis = hasApproval && hasReview
            ? TeamWorkHolderBasis.ActiveReviewAndApprovalStages
            : hasApproval
                ? TeamWorkHolderBasis.ActiveApprovalStage
                : TeamWorkHolderBasis.ActiveReviewStage;

        return new(
            TeamWorkLaneDecision.OnBoard(lane),
            new TeamWorkHolderResolution(active.Select(step => step.HolderId), basis));
    }
}

/// <summary>
/// Explicit native-state to Team Work lane policy. Holder resolution is intentionally a separate policy.
/// </summary>
public static class TeamWorkLanePolicy
{
    public static TeamWorkLaneDecision ForChangeRequest(
        ChangeRequestState state, ChangeRequestState? deferredFromState = null) => state switch
        {
            ChangeRequestState.Draft => TeamWorkLaneDecision.OnBoard(TeamWorkLane.InWork),
            ChangeRequestState.InReview => TeamWorkLaneDecision.OnBoard(TeamWorkLane.InReview),
            ChangeRequestState.Approved => TeamWorkLaneDecision.OnBoard(TeamWorkLane.Approved),
            ChangeRequestState.SelectedForBaseline => TeamWorkLaneDecision.OnBoard(TeamWorkLane.Approved),
            ChangeRequestState.Deferred => DeferredChangeRequest(deferredFromState),
            ChangeRequestState.Withdrawn => TeamWorkLaneDecision.OffBoard,
            _ => throw new DomainException($"The change-request state '{state}' is not supported by Team Work."),
        };

    public static TeamWorkLaneDecision ForTestChangeReview(
        TestChangeReviewState state, TestChangeReviewState? deferredFromState = null) => state switch
        {
            TestChangeReviewState.Draft => TeamWorkLaneDecision.OnBoard(TeamWorkLane.InWork),
            TestChangeReviewState.InReview => TeamWorkLaneDecision.OnBoard(TeamWorkLane.InReview),
            TestChangeReviewState.Approved => TeamWorkLaneDecision.OnBoard(TeamWorkLane.Approved),
            TestChangeReviewState.Deferred => DeferredTestChangeReview(deferredFromState),
            TestChangeReviewState.Superseded => TeamWorkLaneDecision.OffBoard,
            _ => throw new DomainException($"The test-change-review state '{state}' is not supported by Team Work."),
        };

    public static TeamWorkLaneDecision ForProblemReport(ProblemReportState state) => state switch
    {
        ProblemReportState.Draft => TeamWorkLaneDecision.OnBoard(TeamWorkLane.InWork),
        ProblemReportState.ReadyForSccb => TeamWorkLaneDecision.OnBoard(TeamWorkLane.InReview),
        ProblemReportState.Open => TeamWorkLaneDecision.OnBoard(TeamWorkLane.InWork),
        ProblemReportState.Implementing => TeamWorkLaneDecision.OnBoard(TeamWorkLane.InWork),
        ProblemReportState.Verifying => TeamWorkLaneDecision.OnBoard(TeamWorkLane.InWork),
        ProblemReportState.WaitingForSqaToClose => TeamWorkLaneDecision.OnBoard(TeamWorkLane.AwaitingSignature),
        ProblemReportState.Closed or ProblemReportState.Rejected => TeamWorkLaneDecision.OffBoard,
        _ => throw new DomainException($"The Problem Report state '{state}' is not supported by Team Work."),
    };

    public static TeamWorkLaneDecision ForAssessment(
        DownstreamAssessmentState state, DownstreamAssessmentOutcome outcome) => state switch
        {
            DownstreamAssessmentState.Open => outcome switch
            {
                DownstreamAssessmentOutcome.Pending => TeamWorkLaneDecision.OnBoard(TeamWorkLane.InWork),
                DownstreamAssessmentOutcome.ChangeRequired => TeamWorkLaneDecision.OnBoard(TeamWorkLane.InWork),
                DownstreamAssessmentOutcome.NoChangeRequired => TeamWorkLaneDecision.OnBoard(TeamWorkLane.InWork),
                DownstreamAssessmentOutcome.ChangeRequestsLinked => TeamWorkLaneDecision.OffBoard,
                _ => throw UnsupportedAssessmentOutcome(outcome),
            },
            DownstreamAssessmentState.InReview => outcome switch
            {
                DownstreamAssessmentOutcome.Pending => TeamWorkLaneDecision.OnBoard(TeamWorkLane.InReview),
                DownstreamAssessmentOutcome.ChangeRequired => TeamWorkLaneDecision.OnBoard(TeamWorkLane.InReview),
                DownstreamAssessmentOutcome.NoChangeRequired => TeamWorkLaneDecision.OnBoard(TeamWorkLane.InReview),
                DownstreamAssessmentOutcome.ChangeRequestsLinked => TeamWorkLaneDecision.OnBoard(TeamWorkLane.InReview),
                _ => throw UnsupportedAssessmentOutcome(outcome),
            },
            DownstreamAssessmentState.Approved => outcome switch
            {
                DownstreamAssessmentOutcome.Pending => TeamWorkLaneDecision.OnBoard(TeamWorkLane.Approved),
                DownstreamAssessmentOutcome.ChangeRequired => TeamWorkLaneDecision.OnBoard(TeamWorkLane.Approved),
                DownstreamAssessmentOutcome.NoChangeRequired => TeamWorkLaneDecision.OnBoard(TeamWorkLane.Approved),
                DownstreamAssessmentOutcome.ChangeRequestsLinked => TeamWorkLaneDecision.OffBoard,
                _ => throw UnsupportedAssessmentOutcome(outcome),
            },
            DownstreamAssessmentState.Superseded => outcome switch
            {
                DownstreamAssessmentOutcome.Pending => TeamWorkLaneDecision.OffBoard,
                DownstreamAssessmentOutcome.ChangeRequired => TeamWorkLaneDecision.OffBoard,
                DownstreamAssessmentOutcome.NoChangeRequired => TeamWorkLaneDecision.OffBoard,
                DownstreamAssessmentOutcome.ChangeRequestsLinked => TeamWorkLaneDecision.OffBoard,
                _ => throw UnsupportedAssessmentOutcome(outcome),
            },
            _ => throw new DomainException($"The downstream-assessment state '{state}' is not supported by Team Work."),
        };

    private static TeamWorkLaneDecision DeferredChangeRequest(ChangeRequestState? prior) => prior switch
    {
        // Legacy rows may predate DeferredFromState. In that case the only honest claim is that the item is
        // deferred work with no known prior lane; choosing InWork is conservative and carries no holder.
        null => TeamWorkLaneDecision.OnBoard(TeamWorkLane.InWork, isDeferred: true),
        ChangeRequestState.Draft => TeamWorkLaneDecision.OnBoard(TeamWorkLane.InWork, isDeferred: true),
        ChangeRequestState.InReview => TeamWorkLaneDecision.OnBoard(TeamWorkLane.InReview, isDeferred: true),
        ChangeRequestState.Approved => TeamWorkLaneDecision.OnBoard(TeamWorkLane.Approved, isDeferred: true),
        ChangeRequestState.Deferred or ChangeRequestState.SelectedForBaseline or ChangeRequestState.Withdrawn =>
            throw new DomainException($"The deferred change request prior state '{prior}' is invalid."),
        _ => throw new DomainException($"The deferred change request prior state '{prior}' is not supported."),
    };

    private static TeamWorkLaneDecision DeferredTestChangeReview(TestChangeReviewState? prior) => prior switch
    {
        // Same conservative legacy treatment as change requests: no missing provenance is promoted to a
        // fabricated approval/review state.
        null => TeamWorkLaneDecision.OnBoard(TeamWorkLane.InWork, isDeferred: true),
        TestChangeReviewState.Draft => TeamWorkLaneDecision.OnBoard(TeamWorkLane.InWork, isDeferred: true),
        TestChangeReviewState.InReview => TeamWorkLaneDecision.OnBoard(TeamWorkLane.InReview, isDeferred: true),
        TestChangeReviewState.Approved => TeamWorkLaneDecision.OnBoard(TeamWorkLane.Approved, isDeferred: true),
        TestChangeReviewState.Deferred or TestChangeReviewState.Superseded =>
            throw new DomainException($"The deferred test-change-review prior state '{prior}' is invalid."),
        _ => throw new DomainException($"The deferred test-change-review prior state '{prior}' is not supported."),
    };

    private static DomainException UnsupportedAssessmentOutcome(DownstreamAssessmentOutcome outcome) =>
        new($"The downstream-assessment outcome '{outcome}' is not supported by Team Work.");
}

/// <summary>Explicit current-holder policy. It never falls back across different domain meanings.</summary>
public static class TeamWorkHolderPolicy
{
    public static TeamWorkHolderResolution ForChangeRequest(
        ChangeRequestState state, string? authorId,
        IEnumerable<TeamWorkReviewStep>? activeReviewSteps = null) => state switch
        {
            ChangeRequestState.Draft => OneOrNone(authorId, TeamWorkHolderBasis.Author),
            ChangeRequestState.InReview => TeamWorkReviewOverlay.Resolve(activeReviewSteps ?? Array.Empty<TeamWorkReviewStep>()).HolderResolution,
            ChangeRequestState.Approved or ChangeRequestState.SelectedForBaseline
                or ChangeRequestState.Deferred or ChangeRequestState.Withdrawn => TeamWorkHolderResolution.None,
            _ => throw new DomainException($"The change-request state '{state}' is not supported by Team Work."),
        };

    public static TeamWorkHolderResolution ForTestChangeReview(
        TestChangeReviewState state, string? assignedEngineerId,
        IEnumerable<TeamWorkReviewStep>? activeReviewSteps = null) => state switch
        {
            TestChangeReviewState.Draft => OneOrNone(assignedEngineerId, TeamWorkHolderBasis.AssignedEngineer),
            TestChangeReviewState.InReview => TeamWorkReviewOverlay.Resolve(activeReviewSteps ?? Array.Empty<TeamWorkReviewStep>()).HolderResolution,
            TestChangeReviewState.Approved or TestChangeReviewState.Deferred or TestChangeReviewState.Superseded =>
                TeamWorkHolderResolution.None,
            _ => throw new DomainException($"The test-change-review state '{state}' is not supported by Team Work."),
        };

    public static TeamWorkHolderResolution ForProblemReport(
        ProblemReportState state, string? responsibleEngineerId) => state switch
        {
            ProblemReportState.Draft or ProblemReportState.Open or ProblemReportState.Implementing
                or ProblemReportState.Verifying => OneOrNone(responsibleEngineerId, TeamWorkHolderBasis.ResponsibleEngineer),
            ProblemReportState.ReadyForSccb or ProblemReportState.WaitingForSqaToClose
                or ProblemReportState.Closed or ProblemReportState.Rejected => TeamWorkHolderResolution.None,
            _ => throw new DomainException($"The Problem Report state '{state}' is not supported by Team Work."),
        };

    public static TeamWorkHolderResolution ForAssessment(
        DownstreamAssessmentState state, DownstreamAssessmentOutcome outcome,
        string? assignedEngineerId, string? selectedApproverId) => state switch
        {
            DownstreamAssessmentState.Open => outcome switch
            {
                DownstreamAssessmentOutcome.Pending => OneOrNone(assignedEngineerId, TeamWorkHolderBasis.AssignedEngineer),
                DownstreamAssessmentOutcome.ChangeRequired => OneOrNone(assignedEngineerId, TeamWorkHolderBasis.AssignedEngineer),
                DownstreamAssessmentOutcome.NoChangeRequired => OneOrNone(assignedEngineerId, TeamWorkHolderBasis.AssignedEngineer),
                DownstreamAssessmentOutcome.ChangeRequestsLinked => TeamWorkHolderResolution.None,
                _ => throw UnsupportedAssessmentOutcome(outcome),
            },
            DownstreamAssessmentState.InReview => outcome switch
            {
                DownstreamAssessmentOutcome.Pending => OneOrNone(selectedApproverId, TeamWorkHolderBasis.SelectedAssessmentApprover),
                DownstreamAssessmentOutcome.ChangeRequired => OneOrNone(selectedApproverId, TeamWorkHolderBasis.SelectedAssessmentApprover),
                DownstreamAssessmentOutcome.NoChangeRequired => OneOrNone(selectedApproverId, TeamWorkHolderBasis.SelectedAssessmentApprover),
                DownstreamAssessmentOutcome.ChangeRequestsLinked => OneOrNone(selectedApproverId, TeamWorkHolderBasis.SelectedAssessmentApprover),
                _ => throw UnsupportedAssessmentOutcome(outcome),
            },
            DownstreamAssessmentState.Approved => outcome switch
            {
                DownstreamAssessmentOutcome.Pending => TeamWorkHolderResolution.None,
                DownstreamAssessmentOutcome.ChangeRequired => TeamWorkHolderResolution.None,
                DownstreamAssessmentOutcome.NoChangeRequired => TeamWorkHolderResolution.None,
                DownstreamAssessmentOutcome.ChangeRequestsLinked => TeamWorkHolderResolution.None,
                _ => throw UnsupportedAssessmentOutcome(outcome),
            },
            DownstreamAssessmentState.Superseded => outcome switch
            {
                DownstreamAssessmentOutcome.Pending => TeamWorkHolderResolution.None,
                DownstreamAssessmentOutcome.ChangeRequired => TeamWorkHolderResolution.None,
                DownstreamAssessmentOutcome.NoChangeRequired => TeamWorkHolderResolution.None,
                DownstreamAssessmentOutcome.ChangeRequestsLinked => TeamWorkHolderResolution.None,
                _ => throw UnsupportedAssessmentOutcome(outcome),
            },
            _ => throw new DomainException($"The downstream-assessment state '{state}' is not supported by Team Work."),
        };

    private static TeamWorkHolderResolution OneOrNone(string? holderId, TeamWorkHolderBasis basis) =>
        string.IsNullOrWhiteSpace(holderId)
            // The basis still explains the authoritative obligation field when that field is unassigned.
            // "None" is reserved for states whose policy says that no person obligation exists at all.
            ? new(Array.Empty<string>(), basis)
            : new([holderId], basis);

    private static DomainException UnsupportedAssessmentOutcome(DownstreamAssessmentOutcome outcome) =>
        new($"The downstream-assessment outcome '{outcome}' is not supported by Team Work.");
}
