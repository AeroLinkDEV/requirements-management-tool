using AeroLink.Domain.Common;

namespace AeroLink.Domain.Hierarchy;

/// <summary>
/// Immutable attributed evidence for one successful ladder edit.  The canonical snapshot excludes database ids,
/// so equivalent edits produce the same hash even when EF allocates different step identities.
/// </summary>
public sealed class ProjectLadderConfigurationHistory
{
    private ProjectLadderConfigurationHistory() { }

    public ProjectLadderConfigurationHistory(Guid configurationId, Guid projectId, long revision,
        string actor, DateTimeOffset occurredAt, string reason, string snapshot, string snapshotHash,
        int snapshotSchemaVersion = ProjectLadderSnapshot.LegacySchemaVersion)
    {
        if (configurationId == Guid.Empty || projectId == Guid.Empty)
            throw new DomainException("Ladder history requires a configuration and project.");
        if (revision < 1) throw new DomainException("Ladder history revision must be positive.");
        if (string.IsNullOrWhiteSpace(actor)) throw new DomainException("Ladder history requires an actor.");
        if (string.IsNullOrWhiteSpace(reason)) throw new DomainException("Ladder history requires a meaningful reason.");
        if (string.IsNullOrWhiteSpace(snapshot)) throw new DomainException("Ladder history requires a canonical snapshot.");
        if (string.IsNullOrWhiteSpace(snapshotHash)) throw new DomainException("Ladder history requires a snapshot hash.");
        if (snapshotSchemaVersion is not (ProjectLadderSnapshot.LegacySchemaVersion or ProjectLadderSnapshot.CurrentSchemaVersion))
            throw new DomainException("Ladder history requires a supported snapshot schema version.");
        var canonicalSnapshot = snapshot.Trim();
        var normalizedHash = snapshotHash.Trim().ToLowerInvariant();
        if (!ProjectLadderSnapshot.Verify(canonicalSnapshot, normalizedHash, snapshotSchemaVersion))
            throw new DomainException("Ladder history snapshot hash does not match its canonical snapshot.");
        Id = Guid.NewGuid(); ConfigurationId = configurationId; ProjectId = projectId; Revision = revision;
        Actor = actor.Trim(); OccurredAt = occurredAt; Reason = reason.Trim(); CanonicalSnapshot = canonicalSnapshot;
        SnapshotHash = normalizedHash;
        SnapshotSchemaVersion = snapshotSchemaVersion;
    }

    public Guid Id { get; private set; }
    public Guid ConfigurationId { get; private set; }
    public Guid ProjectId { get; private set; }
    public long Revision { get; private set; }
    public string Actor { get; private set; } = string.Empty;
    public DateTimeOffset OccurredAt { get; private set; }
    public string Reason { get; private set; } = string.Empty;
    public string CanonicalSnapshot { get; private set; } = string.Empty;
    public string SnapshotHash { get; private set; } = string.Empty;
    public int SnapshotSchemaVersion { get; private set; } = ProjectLadderSnapshot.LegacySchemaVersion;
}
