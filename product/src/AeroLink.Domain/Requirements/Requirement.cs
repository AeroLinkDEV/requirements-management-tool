using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Common;
using AeroLink.Domain.Hierarchy;

namespace AeroLink.Domain.Requirements;

public enum RequirementRevisionState { Active, Superseded, Retired }
public enum RequirementRevisionOriginKind { ChangeRequest, ExternalSourcePackage }
/// <summary>How a configured non-root requirement revision answers its exact upstream obligation.</summary>
public enum RequirementParentKind { Unspecified, Allocated, Derived }

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
        DateTimeOffset createdAt, RequirementParentKind parentKind = RequirementParentKind.Unspecified,
        string? derivedRationale = null, IEnumerable<Guid>? parentRevisionIds = null)
    {
        if (revision < 0) throw new DomainException("Requirement revision cannot be negative.");
        if (sourceChangeRequestId == Guid.Empty) throw new DomainException("A change-request revision requires its source change request.");
        if (state == RequirementRevisionState.Active && string.IsNullOrWhiteSpace(statement))
            throw new DomainException("An active requirement revision needs a statement.");
        Id = Guid.NewGuid(); ArtifactId = artifactId; Revision = revision; Statement = statement.Trim();
        Rationale = rationale.Trim(); VerificationMethod = verificationMethod.Trim(); State = state;
        OriginKind = RequirementRevisionOriginKind.ChangeRequest;
        SourceChangeRequestId = sourceChangeRequestId; EffectiveBaselineId = effectiveBaselineId; CreatedAt = createdAt;
        ParentKind = parentKind;
        DerivedRationale = derivedRationale?.Trim() ?? string.Empty;
        ParentRevisionIdsJson = CanonicalParentIds(parentRevisionIds, parentKind, DerivedRationale);
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
        ParentKind = RequirementParentKind.Unspecified;
        DerivedRationale = string.Empty;
        ParentRevisionIdsJson = "[]";
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
    /// <summary>Typed exact-parent classification captured on the immutable revision.</summary>
    public RequirementParentKind ParentKind { get; private set; }
    /// <summary>Engineering rationale when <see cref="ParentKind"/> is Derived.</summary>
    public string DerivedRationale { get; private set; } = string.Empty;
    /// <summary>Canonical sorted exact upstream revision identities captured on the revision.</summary>
    public string ParentRevisionIdsJson { get; private set; } = "[]";
    public string ExactParentRevisionIdsJson => ParentRevisionIdsJson;
    public IReadOnlyList<Guid> ParentRevisionIds => ParseParentIds(ParentRevisionIdsJson);
    public DateTimeOffset CreatedAt { get; private set; }

    private static string CanonicalParentIds(IEnumerable<Guid>? parentRevisionIds,
        RequirementParentKind parentKind, string rationale)
    {
        var ids = ExactParentSelectionPolicy.NormalizeIds(parentRevisionIds, "requirement revision");
        // Unspecified with no evidence is retained for legacy/history rows. The
        // persistence boundary resolves the owning level and refuses it for a
        // newly written configured non-root revision.
        if (parentKind == RequirementParentKind.Unspecified && ids.Count == 0 && rationale.Length == 0)
            return "[]";
        ExactParentSelectionPolicy.Validate(parentKind switch
        {
            RequirementParentKind.Allocated => ExactParentClassification.Allocated,
            RequirementParentKind.Derived => ExactParentClassification.Derived,
            _ => ExactParentClassification.Unspecified,
        }, ids, rationale, "requirement revision");
        return System.Text.Json.JsonSerializer.Serialize(ids);
    }

    private static IReadOnlyList<Guid> ParseParentIds(string json)
    {
        try
        {
            var ids = System.Text.Json.JsonSerializer.Deserialize<List<Guid>>(json) ?? [];
            return ExactParentSelectionPolicy.NormalizeIds(ids, "requirement revision");
        }
        catch (System.Text.Json.JsonException)
        {
            throw new DomainException("A requirement revision carries malformed exact parent identities.");
        }
    }
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
