namespace AeroLink.Domain.Programs;

public sealed class ProgramRecord
{
    private ProgramRecord() { }
    public ProgramRecord(string name, string code)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Program name is required.", nameof(name));
        if (string.IsNullOrWhiteSpace(code)) throw new ArgumentException("Program code is required.", nameof(code));
        Id = Guid.NewGuid();
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
    {
        if (programId == Guid.Empty) throw new ArgumentException("Program is required.", nameof(programId));
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Project name is required.", nameof(name));
        if (string.IsNullOrWhiteSpace(softwareProduct)) throw new ArgumentException("Software product is required.", nameof(softwareProduct));
        Id = Guid.NewGuid();
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
    {
        if (projectId == Guid.Empty) throw new ArgumentException("Project is required.", nameof(projectId));
        if (string.IsNullOrWhiteSpace(version)) throw new ArgumentException("Release version is required.", nameof(version));
        Id = Guid.NewGuid();
        ProjectId = projectId;
        Version = version.Trim();
        IsReleased = isReleased;
        PredecessorReleaseId = predecessorReleaseId;
    }

    public Guid Id { get; private set; }
    public Guid ProjectId { get; private set; }
    public string Version { get; private set; } = string.Empty;
    public Guid? PredecessorReleaseId { get; private set; }
    public bool IsReleased { get; private set; }
    public DateTimeOffset? ReleasedAt { get; private set; }
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
