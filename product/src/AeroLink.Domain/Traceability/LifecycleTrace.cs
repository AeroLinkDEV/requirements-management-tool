using AeroLink.Domain.Common;
using AeroLink.Domain.Hierarchy;
using AeroLink.Domain.Requirements;
using AeroLink.Domain.ChangeControl;

namespace AeroLink.Domain.Traceability;

public enum RequirementTraceType { DerivedFrom, AllocatedFrom }
public enum ControlledDocumentType { Sysrd, SwrdHighLevel, SwrdLowLevel, SystemTestProcedures, HighLevelTestProcedures, LowLevelTestProcedures }

/// <summary>Central validation for hierarchy-aware trace mutations.</summary>
public static class RequirementTracePolicy
{
    public static void Validate(ILadderPolicy policy, RequirementLevel source, RequirementLevel target,
        RequirementTraceType type)
    {
        ArgumentNullException.ThrowIfNull(policy);
        _ = policy.Definition(source);
        _ = policy.Definition(target);
        // The legacy/default policy is intentionally permissive for generic trace creation (#702). Its
        // characterization allows any two non-self same-project revisions. Configured policies, however,
        // have explicit direct edges and must honor their stored orientation.
        if (policy is ILegacyLadderCompatibilityPolicy) return;
        var valid = type switch
        {
            RequirementTraceType.DerivedFrom or RequirementTraceType.AllocatedFrom =>
                policy.ParentRelationships.Any(x => x.Child == source && x.Parent == target),
            _ => false,
        };
        if (!valid)
            throw new DomainException(
                $"A {type} trace must point from configured child {source} to its direct parent {target}.");
    }
}

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
        string documentNumber, string title, int revision, string contentHash, int artifactCount, DateTimeOffset generatedAt,
        Guid? templateRevisionId = null)
    {
        Id = Guid.NewGuid(); ProjectId = projectId; ReleaseId = releaseId; BaselineId = baselineId; Type = type;
        DocumentNumber = ArtifactNumber.ValidateBase(documentNumber); Title = title.Trim(); Revision = revision;
        ContentHash = contentHash; ArtifactCount = artifactCount; GeneratedAt = generatedAt;
        // The exact approved template revision that produced this document. Without it, revising a template
        // would silently change every document generated afterwards, a document regenerated next year would
        // no longer match the one somebody signed, and the hash recorded here would be evidence of nothing.
        TemplateRevisionId = templateRevisionId;
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
    public Guid? TemplateRevisionId { get; private set; }
}

/// <summary>
/// One frozen rendition of a controlled document.
///
/// A controlled publication is a record of what was approved at a moment, not a recipe re-evaluated against
/// whatever happens to be live when somebody asks to download it years later. When a document is created the
/// exact bytes of each supported format are rendered once and stored here; downloads serve those bytes and the
/// manifest reports the stored SHA-256. Records created before artifact freezing carry no artifact rows and are
/// explicitly reported as legacy on-demand regeneration rather than pretending to be deterministic.
/// </summary>
public sealed class ControlledDocumentArtifact
{
    private ControlledDocumentArtifact() { }
    public ControlledDocumentArtifact(Guid documentId, string format, string storageKey, string originalFileName,
        string contentType, long size, string sha256, DateTimeOffset renderedAt)
    {
        Id = Guid.NewGuid(); DocumentId = documentId; Format = format.Trim().ToLowerInvariant();
        StorageKey = storageKey; OriginalFileName = originalFileName.Trim(); ContentType = contentType.Trim();
        Size = size; Sha256 = sha256.Trim().ToLowerInvariant(); RenderedAt = renderedAt;
    }
    public Guid Id { get; private set; }
    public Guid DocumentId { get; private set; }
    public string Format { get; private set; } = string.Empty;
    public string StorageKey { get; private set; } = string.Empty;
    public string OriginalFileName { get; private set; } = string.Empty;
    public string ContentType { get; private set; } = string.Empty;
    public long Size { get; private set; }
    public string Sha256 { get; private set; } = string.Empty;
    public DateTimeOffset RenderedAt { get; private set; }
}
