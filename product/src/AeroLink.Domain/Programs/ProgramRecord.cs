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
    public SoftwareRelease(Guid projectId, string version, bool isReleased)
    {
        if (projectId == Guid.Empty) throw new ArgumentException("Project is required.", nameof(projectId));
        if (string.IsNullOrWhiteSpace(version)) throw new ArgumentException("Release version is required.", nameof(version));
        Id = Guid.NewGuid();
        ProjectId = projectId;
        Version = version.Trim();
        IsReleased = isReleased;
    }

    public Guid Id { get; private set; }
    public Guid ProjectId { get; private set; }
    public string Version { get; private set; } = string.Empty;
    public bool IsReleased { get; private set; }
}
