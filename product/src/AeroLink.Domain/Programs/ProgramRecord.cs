using AeroLink.Domain.Common;

namespace AeroLink.Domain.Programs;

public sealed class ProgramRecord
{
    private ProgramRecord() { }
    public ProgramRecord(string name, string code)
        : this(Guid.NewGuid(), name, code) { }

    /// <summary>Rehydrates an identity allocated by a resumable project setup draft.</summary>
    internal ProgramRecord(Guid id, string name, string code)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Program name is required.", nameof(name));
        if (string.IsNullOrWhiteSpace(code)) throw new ArgumentException("Program code is required.", nameof(code));
        if (id == Guid.Empty) throw new ArgumentException("Program identity is required.", nameof(id));
        Id = id;
        Name = name.Trim();
        Code = code.Trim().ToUpperInvariant();
    }

    public Guid Id { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public string Code { get; private set; } = string.Empty;
}

public sealed class ProjectRecord
{
    private ProjectRecord() { }
    public ProjectRecord(Guid programId, string name, string softwareProduct)
        : this(Guid.NewGuid(), programId, name, softwareProduct) { }

    /// <summary>Rehydrates an identity allocated by a resumable project setup draft.</summary>
    internal ProjectRecord(Guid id, Guid programId, string name, string softwareProduct)
    {
        if (programId == Guid.Empty) throw new ArgumentException("Program is required.", nameof(programId));
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Project name is required.", nameof(name));
        if (string.IsNullOrWhiteSpace(softwareProduct)) throw new ArgumentException("Software product is required.", nameof(softwareProduct));
        if (id == Guid.Empty) throw new ArgumentException("Project identity is required.", nameof(id));
        Id = id;
        ProgramId = programId;
        Name = name.Trim();
        SoftwareProduct = softwareProduct.Trim();
    }

    public Guid Id { get; private set; }
    public Guid ProgramId { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public string SoftwareProduct { get; private set; } = string.Empty;
}

public sealed class SoftwareRelease
{
    private SoftwareRelease() { }
    public SoftwareRelease(Guid projectId, string version, bool isReleased, Guid? predecessorReleaseId = null)
        : this(Guid.NewGuid(), projectId, version, isReleased, predecessorReleaseId) { }

    /// <summary>Rehydrates an identity allocated by a resumable project setup draft.</summary>
    internal SoftwareRelease(Guid id, Guid projectId, string version, bool isReleased, Guid? predecessorReleaseId = null)
    {
        if (projectId == Guid.Empty) throw new ArgumentException("Project is required.", nameof(projectId));
        if (string.IsNullOrWhiteSpace(version)) throw new ArgumentException("Release version is required.", nameof(version));
        if (id == Guid.Empty) throw new ArgumentException("Release identity is required.", nameof(id));
        Id = id;
        ProjectId = projectId;
        Version = version.Trim();
        CanonicalIdentity = SoftwareBuildIdentifier.FromVersion(Version);
        IsReleased = isReleased;
        PredecessorReleaseId = predecessorReleaseId;
    }

    public Guid Id { get; private set; }
    public Guid ProjectId { get; private set; }
    public string Version { get; private set; } = string.Empty;
    /// <summary>
    /// The canonical controlled identity for releases created after the canonical build authority was added.
    /// Historical rows may be null until an explicit, audited migration backfills them; raw Version is never
    /// rewritten in that process.
    /// </summary>
    public string? CanonicalIdentity { get; private set; }
    public Guid? PredecessorReleaseId { get; private set; }
    public bool IsReleased { get; private set; }
    public DateTimeOffset? ReleasedAt { get; private set; }
    /// <summary>
    /// Operational link-options membership ordinal, allocated by the database inside the shared project
    /// fence. It freezes Release picker candidate membership across cursor pages; it is not a creation
    /// timestamp, not a controlled identity, and never orders or labels builds. Rows present at upgrade
    /// keep null as the documented legacy cohort.
    /// </summary>
    public long? PickerInsertionOrdinal { get; private set; }
    internal void SetCanonicalIdentity(string canonicalIdentity) => CanonicalIdentity = canonicalIdentity;
    public void MarkReleased(DateTimeOffset now) { if (IsReleased) throw new InvalidOperationException("The software release is already released."); IsReleased = true; ReleasedAt = now; }
}

public enum SoftwareBuildState { Recorded, Released }

/// <summary>A software build is an immutable provenance record for one exact frozen baseline.</summary>
public sealed class SoftwareBuild
{
    private SoftwareBuild() { }
    public SoftwareBuild(Guid projectId, Guid releaseId, Guid baselineId, string buildNumber,
        string description, string recordedBy, DateTimeOffset recordedAt)
    {
        if (projectId == Guid.Empty || releaseId == Guid.Empty || baselineId == Guid.Empty)
            throw new ArgumentException("Project, release, and frozen baseline are required.");
        if (string.IsNullOrWhiteSpace(buildNumber)) throw new ArgumentException("A build number is required.");
        Id = Guid.NewGuid(); ProjectId = projectId; ReleaseId = releaseId; BaselineId = baselineId;
        BuildNumber = buildNumber.Trim(); Description = description.Trim(); RecordedBy = recordedBy.Trim();
        RecordedAt = recordedAt; State = SoftwareBuildState.Recorded;
    }
    public Guid Id { get; private set; }
    public Guid ProjectId { get; private set; }
    public Guid ReleaseId { get; private set; }
    public Guid BaselineId { get; private set; }
    public string BuildNumber { get; private set; } = string.Empty;
    public string Description { get; private set; } = string.Empty;
    public string RecordedBy { get; private set; } = string.Empty;
    public DateTimeOffset RecordedAt { get; private set; }
    public SoftwareBuildState State { get; private set; }
    public DateTimeOffset? ReleasedAt { get; private set; }
    public void MarkReleased(DateTimeOffset now) { State = SoftwareBuildState.Released; ReleasedAt = now; }
}
