using AeroLink.Domain.Common;

namespace AeroLink.Domain.ChangeControl;

/// <summary>One exact assessment-owned upstream edge captured when a review is submitted.</summary>
public sealed record DerivedChangeRequestUpstreamEvidence(
    Guid UpstreamChangeRequestId,
    Guid AssessmentId,
    Guid AssessmentLinkId,
    Guid BuildId,
    string UpstreamDisplayNumber);

/// <summary>
/// Persistence-independent evidence resolved by the application boundary before a change request enters
/// review. The aggregate never queries the database; it only validates and freezes the evidence supplied here.
/// </summary>
public sealed record ChangeRequestTraceReviewEvidence(
    bool IsTopOfLadder,
    IReadOnlyCollection<DerivedChangeRequestUpstreamEvidence> DerivedUpstreamLinks);

/// <summary>An authenticated author's exact upstream change-request assertion.</summary>
public sealed class ChangeRequestUpstreamLink
{
    private ChangeRequestUpstreamLink() { }

    public ChangeRequestUpstreamLink(Guid changeRequestId, Guid upstreamChangeRequestId,
        string upstreamDisplayNumber, Guid upstreamBuildId, string upstreamBuildVersion,
        string rationale, string actorId, DateTimeOffset statedAt)
    {
        if (changeRequestId == Guid.Empty || upstreamChangeRequestId == Guid.Empty)
            throw new DomainException("An upstream change-request link requires exact change-request identities.");
        if (changeRequestId == upstreamChangeRequestId)
            throw new DomainException("A change request cannot link to itself.");
        Id = Guid.NewGuid();
        ChangeRequestId = changeRequestId;
        UpstreamChangeRequestId = upstreamChangeRequestId;
        UpstreamDisplayNumber = Required(upstreamDisplayNumber, "upstream change-request number");
        UpstreamBuildId = upstreamBuildId == Guid.Empty
            ? throw new DomainException("An upstream change-request link requires its build.") : upstreamBuildId;
        UpstreamBuildVersion = Required(upstreamBuildVersion, "upstream build identity");
        Rationale = Required(rationale, "upstream rationale");
        ActorId = Required(actorId, "upstream author");
        StatedAt = statedAt;
    }

    public Guid Id { get; private set; }
    public Guid ChangeRequestId { get; private set; }
    public Guid UpstreamChangeRequestId { get; private set; }
    public string UpstreamDisplayNumber { get; private set; } = string.Empty;
    public Guid UpstreamBuildId { get; private set; }
    public string UpstreamBuildVersion { get; private set; } = string.Empty;
    public string Rationale { get; private set; } = string.Empty;
    public string ActorId { get; private set; } = string.Empty;
    public DateTimeOffset StatedAt { get; private set; }

    private static string Required(string value, string name) =>
        string.IsNullOrWhiteSpace(value) ? throw new DomainException($"A {name} is required.") : value.Trim();
}

/// <summary>Immutable history of an authored upstream answer being added, removed, or replaced.</summary>
public sealed class ChangeRequestUpstreamHistory
{
    private ChangeRequestUpstreamHistory() { }

    internal ChangeRequestUpstreamHistory(Guid changeRequestId, string action, Guid? upstreamLinkId,
        Guid? upstreamChangeRequestId,
        string upstreamDisplayNumber, Guid? upstreamBuildId, string upstreamBuildVersion, string rationale,
        string actorId, DateTimeOffset occurredAt)
    {
        Id = Guid.NewGuid();
        ChangeRequestId = changeRequestId;
        Action = action.Trim();
        UpstreamLinkId = upstreamLinkId;
        UpstreamChangeRequestId = upstreamChangeRequestId;
        UpstreamDisplayNumber = upstreamDisplayNumber ?? string.Empty;
        UpstreamBuildId = upstreamBuildId;
        UpstreamBuildVersion = upstreamBuildVersion ?? string.Empty;
        Rationale = rationale ?? string.Empty;
        ActorId = actorId.Trim();
        OccurredAt = occurredAt;
    }

    public Guid Id { get; private set; }
    public Guid ChangeRequestId { get; private set; }
    public string Action { get; private set; } = string.Empty;
    /// <summary>The exact active-link row affected by this event, retained after that row is removed.</summary>
    public Guid? UpstreamLinkId { get; private set; }
    public Guid? UpstreamChangeRequestId { get; private set; }
    public string UpstreamDisplayNumber { get; private set; } = string.Empty;
    public Guid? UpstreamBuildId { get; private set; }
    public string UpstreamBuildVersion { get; private set; } = string.Empty;
    public string Rationale { get; private set; } = string.Empty;
    public string ActorId { get; private set; } = string.Empty;
    public DateTimeOffset OccurredAt { get; private set; }
}
