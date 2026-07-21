using AeroLink.Domain.Common;

namespace AeroLink.Domain.Traceability;

public enum RequirementTraceType { DerivedFrom, AllocatedFrom }
public enum ControlledDocumentType { Sysrd, SwrdHighLevel, SwrdLowLevel, SystemTestProcedures, HighLevelTestProcedures, LowLevelTestProcedures }

public sealed class RequirementTraceLink
{
    private RequirementTraceLink() { }
    public RequirementTraceLink(Guid projectId, Guid sourceRevisionId, Guid targetRevisionId, RequirementTraceType type, string rationale, DateTimeOffset createdAt)
    {
        if (sourceRevisionId == targetRevisionId) throw new DomainException("A requirement revision cannot trace to itself.");
        Id = Guid.NewGuid(); ProjectId = projectId; SourceRevisionId = sourceRevisionId; TargetRevisionId = targetRevisionId;
        Type = type; Rationale = rationale.Trim(); CreatedAt = createdAt; UpdatedAt = createdAt;
    }
    public Guid Id { get; private set; }
    public Guid ProjectId { get; private set; }
    public Guid SourceRevisionId { get; private set; }
    public Guid TargetRevisionId { get; private set; }
    public RequirementTraceType Type { get; private set; }
    public string Rationale { get; private set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public long Version { get; private set; } = 1;

    public void UpdateProposal(RequirementTraceType type, string rationale, DateTimeOffset now)
    {
        Type = type; Rationale = rationale.Trim(); UpdatedAt = now;
    }
}

public sealed class ControlledDocument
{
    private ControlledDocument() { }
    public ControlledDocument(Guid projectId, Guid releaseId, Guid baselineId, ControlledDocumentType type,
        string documentNumber, string title, int revision, string contentHash, int artifactCount, DateTimeOffset generatedAt)
    {
        Id = Guid.NewGuid(); ProjectId = projectId; ReleaseId = releaseId; BaselineId = baselineId; Type = type;
        DocumentNumber = ArtifactNumber.ValidateBase(documentNumber); Title = title.Trim(); Revision = revision;
        ContentHash = contentHash; ArtifactCount = artifactCount; GeneratedAt = generatedAt;
    }
    public Guid Id { get; private set; }
    public Guid ProjectId { get; private set; }
    public Guid ReleaseId { get; private set; }
    public Guid BaselineId { get; private set; }
    public ControlledDocumentType Type { get; private set; }
    public string DocumentNumber { get; private set; } = string.Empty;
    public string Title { get; private set; } = string.Empty;
    public int Revision { get; private set; }
    public string ContentHash { get; private set; } = string.Empty;
    public int ArtifactCount { get; private set; }
    public DateTimeOffset GeneratedAt { get; private set; }
}
