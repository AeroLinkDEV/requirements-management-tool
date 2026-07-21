using AeroLink.Domain.Common;

namespace AeroLink.Domain.Requirements;

public enum ControlledCheckInOutcome { Succeeded, Failed }

/// <summary>
/// Immutable configuration-management evidence for one universal controlled check-in attempt.
/// The record deliberately captures both successful and rejected attempts so the lifecycle
/// decision can be reconstructed without relying on mutable application logs.
/// </summary>
public sealed class ControlledArtifactCheckInEvidence
{
    private ControlledArtifactCheckInEvidence() { }

    public ControlledArtifactCheckInEvidence(Guid projectId, string artifactType, Guid artifactId,
        string adapter, string actor, DateTimeOffset occurredAt, Guid sessionId, long sessionVersion,
        string baseSnapshotHash, string? resultingSnapshotHash, long aggregateVersionBefore,
        long? aggregateVersionAfter, string? revisionBefore, string? revisionAfter,
        Guid? draftSnapshotId, long? draftSequence, ControlledCheckInOutcome outcome,
        string reason)
    {
        if (projectId == Guid.Empty || artifactId == Guid.Empty || sessionId == Guid.Empty)
            throw new DomainException("Project, artifact, and edit session are required for check-in evidence.");
        if (sessionVersion < 1 || aggregateVersionBefore < 0)
            throw new DomainException("Check-in evidence versions cannot be negative or zero.");
        Id = Guid.NewGuid(); ProjectId = projectId; ArtifactType = Required(artifactType);
        ArtifactId = artifactId; Adapter = Required(adapter); Actor = Required(actor).ToLowerInvariant();
        OccurredAt = occurredAt; SessionId = sessionId; SessionVersion = sessionVersion;
        BaseSnapshotHash = Required(baseSnapshotHash).ToLowerInvariant();
        ResultingSnapshotHash = resultingSnapshotHash?.Trim().ToLowerInvariant();
        AggregateVersionBefore = aggregateVersionBefore; AggregateVersionAfter = aggregateVersionAfter;
        RevisionBefore = revisionBefore?.Trim(); RevisionAfter = revisionAfter?.Trim();
        DraftSnapshotId = draftSnapshotId; DraftSequence = draftSequence; Outcome = outcome;
        Reason = Required(reason);
    }

    public Guid Id { get; private set; }
    public Guid ProjectId { get; private set; }
    public string ArtifactType { get; private set; } = "";
    public Guid ArtifactId { get; private set; }
    public string Adapter { get; private set; } = "";
    public string Actor { get; private set; } = "";
    public DateTimeOffset OccurredAt { get; private set; }
    public Guid SessionId { get; private set; }
    public long SessionVersion { get; private set; }
    public string BaseSnapshotHash { get; private set; } = "";
    public string? ResultingSnapshotHash { get; private set; }
    public long AggregateVersionBefore { get; private set; }
    public long? AggregateVersionAfter { get; private set; }
    public string? RevisionBefore { get; private set; }
    public string? RevisionAfter { get; private set; }
    public Guid? DraftSnapshotId { get; private set; }
    public long? DraftSequence { get; private set; }
    public ControlledCheckInOutcome Outcome { get; private set; }
    public string Reason { get; private set; } = "";

    private static string Required(string value) => string.IsNullOrWhiteSpace(value)
        ? throw new DomainException("A required check-in evidence value is missing.")
        : value.Trim();
}
