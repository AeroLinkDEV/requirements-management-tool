using AeroLink.Domain.Common;

namespace AeroLink.Domain.Traceability;

/// <summary>
/// Registration key for the reusable exact-link lifecycle. #709 registers requirement traces; the dormant
/// Case-to-Procedure relation reserves a stable key for a future suspect projection without raising events in
/// this slice.
/// </summary>
public enum ExactLinkKind
{
    RequirementTrace,
    CaseProcedure,
}

public enum ExactLinkLifecycleState { Suspect, Acknowledged, ChangeRequired, Closed }
public enum ExactLinkLifecycleCauseKind { InternalRequirementRevision, ExternalBaselineImport }
public enum ExactLinkResolutionOutcome
{
    NoDownstreamChangeRequired,
    ExistingDownstreamRevisionRemainsValid,
    DownstreamChangeRequiredNotYetApproved
}
public enum ExactLinkLifecycleEventType { Raised, Acknowledged, ResolutionRecorded }

/// <summary>
/// Current projection for one immutable exact link. The stable LinkId/LinkKind pair is the reusable seam for
/// future exact-link kinds; unsupported registrations fail closed rather than silently sharing a table.
/// </summary>
public sealed class ExactLinkSuspectLifecycle
{
    private ExactLinkSuspectLifecycle() { }

    private ExactLinkSuspectLifecycle(Guid projectId, ExactLinkKind linkKind, Guid linkId,
        ExactLinkLifecycleCauseKind causeKind, Guid? causeRequirementRevisionId, Guid? causeBaselineImportId,
        string actorId, string rationale, DateTimeOffset now)
    {
        if (projectId == Guid.Empty) throw new DomainException("A suspect exact link requires a Project.");
        if (linkId == Guid.Empty) throw new DomainException("A suspect exact link requires a stable link id.");
        Validate(linkKind, causeKind, causeRequirementRevisionId, causeBaselineImportId);
        Id = Guid.NewGuid(); ProjectId = projectId; LinkKind = linkKind; LinkId = linkId;
        State = ExactLinkLifecycleState.Suspect; CauseKind = causeKind;
        CauseRequirementRevisionId = causeRequirementRevisionId; CauseBaselineImportId = causeBaselineImportId;
        RaisedBy = Required(actorId, "A suspect-link actor"); RaisedAt = now;
        RaisedRationale = Required(rationale, "A suspect-link rationale"); UpdatedAt = now;
    }

    public static ExactLinkSuspectLifecycle Raise(Guid projectId, ExactLinkKind linkKind, Guid linkId,
        ExactLinkLifecycleCauseKind causeKind, Guid? causeRequirementRevisionId, Guid? causeBaselineImportId,
        string actorId, string rationale, DateTimeOffset now)
    {
        var lifecycle = new ExactLinkSuspectLifecycle(projectId, linkKind, linkId, causeKind,
            causeRequirementRevisionId, causeBaselineImportId, actorId, rationale, now);
        lifecycle._events.Add(ExactLinkSuspectEvent.Raised(lifecycle, actorId, rationale, now));
        return lifecycle;
    }

    public Guid Id { get; private set; }
    public Guid ProjectId { get; private set; }
    public ExactLinkKind LinkKind { get; private set; }
    public Guid LinkId { get; private set; }
    public ExactLinkLifecycleState State { get; private set; }
    public ExactLinkLifecycleCauseKind CauseKind { get; private set; }
    public Guid? CauseRequirementRevisionId { get; private set; }
    public Guid? CauseBaselineImportId { get; private set; }
    public string RaisedBy { get; private set; } = string.Empty;
    public DateTimeOffset RaisedAt { get; private set; }
    public string RaisedRationale { get; private set; } = string.Empty;
    public string? AcknowledgedBy { get; private set; }
    public DateTimeOffset? AcknowledgedAt { get; private set; }
    public string? AcknowledgementRationale { get; private set; }
    public ExactLinkResolutionOutcome? Outcome { get; private set; }
    public string? ResolvedBy { get; private set; }
    public DateTimeOffset? ResolvedAt { get; private set; }
    public string? ResolutionRationale { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public long Version { get; private set; } = 1;

    private readonly List<ExactLinkSuspectEvent> _events = [];
    public IReadOnlyCollection<ExactLinkSuspectEvent> Events => _events;

    public void Acknowledge(string actorId, string rationale, DateTimeOffset now)
    {
        if (State != ExactLinkLifecycleState.Suspect)
            throw new DomainException("Only a suspect exact link can be acknowledged.");
        var actor = Required(actorId, "An acknowledgement actor");
        var reason = Required(rationale, "An acknowledgement rationale");
        State = ExactLinkLifecycleState.Acknowledged; AcknowledgedBy = actor; AcknowledgedAt = now;
        AcknowledgementRationale = reason; UpdatedAt = now; Version++;
        _events.Add(ExactLinkSuspectEvent.Acknowledged(this, actor, reason, now));
    }

    public void RecordResolution(ExactLinkResolutionOutcome outcome, string actorId, string rationale,
        DateTimeOffset now)
    {
        if (State is not (ExactLinkLifecycleState.Suspect or ExactLinkLifecycleState.Acknowledged
            or ExactLinkLifecycleState.ChangeRequired))
            throw new DomainException("Only an open suspect exact link can record a resolution.");
        var actor = Required(actorId, "A resolution actor");
        var reason = Required(rationale, "A resolution rationale");
        State = outcome == ExactLinkResolutionOutcome.DownstreamChangeRequiredNotYetApproved
            ? ExactLinkLifecycleState.ChangeRequired : ExactLinkLifecycleState.Closed;
        Outcome = outcome; ResolvedBy = actor; ResolvedAt = now; ResolutionRationale = reason;
        UpdatedAt = now; Version++;
        _events.Add(ExactLinkSuspectEvent.ResolutionRecorded(this, actor, reason, outcome, now));
    }

    internal static void Validate(ExactLinkKind linkKind, ExactLinkLifecycleCauseKind causeKind,
        Guid? revisionId, Guid? importId)
    {
        if (linkKind is not (ExactLinkKind.RequirementTrace or ExactLinkKind.CaseProcedure))
            throw new DomainException($"The exact-link kind '{linkKind}' is not registered.");
        switch (causeKind)
        {
            case ExactLinkLifecycleCauseKind.InternalRequirementRevision when revisionId is not null
                && revisionId != Guid.Empty && importId is null:
            case ExactLinkLifecycleCauseKind.ExternalBaselineImport when importId is not null
                && importId != Guid.Empty && revisionId is null:
                return;
            default:
                throw new DomainException("A suspect exact-link cause must identify exactly one internal revision or external package.");
        }
    }

    private static string Required(string? value, string label) => string.IsNullOrWhiteSpace(value)
        ? throw new DomainException($"{label} is required.") : value.Trim();
}

/// <summary>An append-only, attributed lifecycle fact. It has no update or delete operations.</summary>
public sealed class ExactLinkSuspectEvent
{
    private ExactLinkSuspectEvent() { }
    private ExactLinkSuspectEvent(Guid id, Guid lifecycleId, Guid projectId, ExactLinkKind linkKind, Guid linkId,
        ExactLinkLifecycleEventType eventType, ExactLinkLifecycleCauseKind causeKind,
        Guid? causeRequirementRevisionId, Guid? causeBaselineImportId, string actorId, string rationale,
        ExactLinkResolutionOutcome? outcome, DateTimeOffset occurredAt)
    {
        Id = id; LifecycleId = lifecycleId; ProjectId = projectId; LinkKind = linkKind; LinkId = linkId;
        EventType = eventType; CauseKind = causeKind; CauseRequirementRevisionId = causeRequirementRevisionId;
        CauseBaselineImportId = causeBaselineImportId; ActorId = actorId; Rationale = rationale;
        Outcome = outcome; OccurredAt = occurredAt;
    }

    internal static ExactLinkSuspectEvent Raised(ExactLinkSuspectLifecycle lifecycle, string actor,
        string rationale, DateTimeOffset now) => Create(lifecycle, ExactLinkLifecycleEventType.Raised,
        actor, rationale, null, now);
    internal static ExactLinkSuspectEvent Acknowledged(ExactLinkSuspectLifecycle lifecycle, string actor,
        string rationale, DateTimeOffset now) => Create(lifecycle, ExactLinkLifecycleEventType.Acknowledged,
        actor, rationale, null, now);
    internal static ExactLinkSuspectEvent ResolutionRecorded(ExactLinkSuspectLifecycle lifecycle, string actor,
        string rationale, ExactLinkResolutionOutcome outcome, DateTimeOffset now) => Create(lifecycle,
        ExactLinkLifecycleEventType.ResolutionRecorded, actor, rationale, outcome, now);

    private static ExactLinkSuspectEvent Create(ExactLinkSuspectLifecycle lifecycle,
        ExactLinkLifecycleEventType eventType, string actor, string rationale,
        ExactLinkResolutionOutcome? outcome, DateTimeOffset now) => new(Guid.NewGuid(), lifecycle.Id,
        lifecycle.ProjectId, lifecycle.LinkKind, lifecycle.LinkId, eventType, lifecycle.CauseKind,
        lifecycle.CauseRequirementRevisionId, lifecycle.CauseBaselineImportId, actor, rationale, outcome, now);

    public Guid Id { get; private set; }
    public Guid LifecycleId { get; private set; }
    public Guid ProjectId { get; private set; }
    public ExactLinkKind LinkKind { get; private set; }
    public Guid LinkId { get; private set; }
    public ExactLinkLifecycleEventType EventType { get; private set; }
    public ExactLinkLifecycleCauseKind CauseKind { get; private set; }
    public Guid? CauseRequirementRevisionId { get; private set; }
    public Guid? CauseBaselineImportId { get; private set; }
    public string ActorId { get; private set; } = string.Empty;
    public string Rationale { get; private set; } = string.Empty;
    public ExactLinkResolutionOutcome? Outcome { get; private set; }
    public DateTimeOffset OccurredAt { get; private set; }
}
