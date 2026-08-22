using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Common;

namespace AeroLink.Domain.Requirements;

public enum RequirementRevisionState { Active, Superseded, Retired }
public enum RequirementRevisionOriginKind { ChangeRequest, ExternalSourcePackage }

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

/// <summary>An immutable, attributable requirement revision produced by exactly one controlled origin.</summary>
public sealed class RequirementRevision
{
    private RequirementRevision() { }
    public RequirementRevision(Guid artifactId, int revision, string statement, string rationale,
        string verificationMethod, RequirementRevisionState state, Guid sourceChangeRequestId, Guid effectiveBaselineId,
        DateTimeOffset createdAt)
    {
        if (revision < 0) throw new DomainException("Requirement revision cannot be negative.");
        if (sourceChangeRequestId == Guid.Empty) throw new DomainException("A change-request revision requires its source change request.");
        if (state == RequirementRevisionState.Active && string.IsNullOrWhiteSpace(statement))
            throw new DomainException("An active requirement revision needs a statement.");
        Id = Guid.NewGuid(); ArtifactId = artifactId; Revision = revision; Statement = statement.Trim();
        Rationale = rationale.Trim(); VerificationMethod = verificationMethod.Trim(); State = state;
        OriginKind = RequirementRevisionOriginKind.ChangeRequest;
        SourceChangeRequestId = sourceChangeRequestId; EffectiveBaselineId = effectiveBaselineId; CreatedAt = createdAt;
    }

    private RequirementRevision(Guid artifactId, int revision, string statement, string rationale,
        RequirementRevisionState state, Guid sourceBaselineImportId, Guid effectiveBaselineId, DateTimeOffset createdAt)
    {
        if (revision < 0) throw new DomainException("Requirement revision cannot be negative.");
        if (sourceBaselineImportId == Guid.Empty) throw new DomainException("An external revision requires its source baseline import.");
        if (state == RequirementRevisionState.Active && string.IsNullOrWhiteSpace(statement))
            throw new DomainException("An active requirement revision needs a statement.");
        Id = Guid.NewGuid(); ArtifactId = artifactId; Revision = revision; Statement = statement.Trim();
        Rationale = (rationale ?? "").Trim(); VerificationMethod = ""; State = state;
        OriginKind = RequirementRevisionOriginKind.ExternalSourcePackage;
        SourceBaselineImportId = sourceBaselineImportId; EffectiveBaselineId = effectiveBaselineId; CreatedAt = createdAt;
    }

    public static RequirementRevision FromExternalSourcePackage(Guid artifactId, int revision, string statement,
        string rationale, RequirementRevisionState state, Guid sourceBaselineImportId, Guid effectiveBaselineId,
        DateTimeOffset createdAt) => new(artifactId, revision, statement, rationale, state, sourceBaselineImportId,
            effectiveBaselineId, createdAt);
    public Guid Id { get; private set; }
    public Guid ArtifactId { get; private set; }
    public int Revision { get; private set; }
    public string Statement { get; private set; } = string.Empty;
    public string Rationale { get; private set; } = string.Empty;
    public string VerificationMethod { get; private set; } = string.Empty;
    public RequirementRevisionState State { get; private set; }
    public RequirementRevisionOriginKind OriginKind { get; private set; }
    public RequirementRevisionOriginKind Origin => OriginKind;
    public Guid? SourceChangeRequestId { get; private set; }
    public Guid? SourceBaselineImportId { get; private set; }
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
