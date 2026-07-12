using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Common;

namespace AeroLink.Domain.Requirements;

public enum RequirementRevisionState { Active, Retired }

/// <summary>Stable identity for a requirement across all of its immutable revisions.</summary>
public sealed class RequirementArtifact
{
    private RequirementArtifact() { }
    public RequirementArtifact(Guid projectId, string baseNumber, RequirementLevel level, DateTimeOffset createdAt)
    {
        Id = Guid.NewGuid(); ProjectId = projectId; BaseNumber = ArtifactNumber.ValidateBase(baseNumber);
        Level = level; CreatedAt = createdAt;
    }
    public Guid Id { get; private set; }
    public Guid ProjectId { get; private set; }
    public string BaseNumber { get; private set; } = string.Empty;
    public RequirementLevel Level { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
}

/// <summary>An immutable, attributable requirement revision produced by one approved SCR.</summary>
public sealed class RequirementRevision
{
    private RequirementRevision() { }
    public RequirementRevision(Guid artifactId, int revision, string statement, string rationale,
        string verificationMethod, RequirementRevisionState state, Guid sourceScrId, Guid effectiveBaselineId,
        DateTimeOffset createdAt)
    {
        if (revision < 0) throw new DomainException("Requirement revision cannot be negative.");
        if (state == RequirementRevisionState.Active && string.IsNullOrWhiteSpace(statement))
            throw new DomainException("An active requirement revision needs a statement.");
        Id = Guid.NewGuid(); ArtifactId = artifactId; Revision = revision; Statement = statement.Trim();
        Rationale = rationale.Trim(); VerificationMethod = verificationMethod.Trim(); State = state;
        SourceScrId = sourceScrId; EffectiveBaselineId = effectiveBaselineId; CreatedAt = createdAt;
    }
    public Guid Id { get; private set; }
    public Guid ArtifactId { get; private set; }
    public int Revision { get; private set; }
    public string Statement { get; private set; } = string.Empty;
    public string Rationale { get; private set; } = string.Empty;
    public string VerificationMethod { get; private set; } = string.Empty;
    public RequirementRevisionState State { get; private set; }
    public Guid SourceScrId { get; private set; }
    public Guid EffectiveBaselineId { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
}

/// <summary>Exact active requirement-revision membership of one materialized baseline.</summary>
public sealed class BaselineRequirementSelection
{
    private BaselineRequirementSelection() { }
    public BaselineRequirementSelection(Guid baselineId, Guid artifactId, Guid revisionId)
    { Id = Guid.NewGuid(); BaselineId = baselineId; ArtifactId = artifactId; RevisionId = revisionId; }
    public Guid Id { get; private set; }
    public Guid BaselineId { get; private set; }
    public Guid ArtifactId { get; private set; }
    public Guid RevisionId { get; private set; }
}
