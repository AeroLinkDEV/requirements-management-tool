using AeroLink.Domain.Common;

namespace AeroLink.Domain.ChangeControl;

public enum RequirementLevel { System, HighLevel, LowLevel }
public enum RequirementChangeKind { Introduce, Modify, Retire }

public sealed record RequirementChangeDraft(string BaseNumber, int Revision, RequirementLevel Level,
    RequirementChangeKind Kind, string Statement, string Rationale, string VerificationMethod,
    string RichText = "", string AttributesJson = "{}",
    string ImpactDispositionJson = RequirementAuthoringJson.CompleteImpactDispositions,
    Guid? TargetSectionId = null,
    string ProposedUpstreamRevisionIdsJson = "[]");

public sealed class RequirementChange
{
    private RequirementChange() { }

    internal RequirementChange(Guid changeRequestId, string baseNumber, int revision, RequirementLevel level,
        RequirementChangeKind kind, string statement, string rationale, string verificationMethod,
        string richText = "", string attributesJson = "{}",
        string impactDispositionJson = RequirementAuthoringJson.CompleteImpactDispositions,
        Guid? targetSectionId = null, string proposedUpstreamRevisionIdsJson = "[]")
    {
        Id = Guid.NewGuid();
        ChangeRequestId = changeRequestId;
        BaseNumber = ArtifactNumber.ValidateBase(baseNumber);
        Revision = revision;
        Level = level;
        Kind = kind;
        Statement = statement.Trim();
        Rationale = rationale.Trim();
        VerificationMethod = verificationMethod.Trim();
        // Supporting content is stored as canonical structure, whatever form it arrived in. Content written
        // before this model existed, and content arriving from a ReqIF exchange, is plain text; it becomes a
        // single paragraph rather than being rejected. Anything the product cannot render is rejected here,
        // where the author can still be told, rather than silently dropped on the way to an approver.
        RichText = Content.RichContent.Canonicalize(
            string.IsNullOrWhiteSpace(richText) ? statement.Trim() : richText.Trim());
        AttributesJson = string.IsNullOrWhiteSpace(attributesJson) ? "{}" : attributesJson;
        ImpactDispositionJson = string.IsNullOrWhiteSpace(impactDispositionJson) ? "{}" : impactDispositionJson;
        TargetSectionId = targetSectionId;
        ProposedUpstreamRevisionIdsJson = string.IsNullOrWhiteSpace(proposedUpstreamRevisionIdsJson)
            ? "[]"
            : proposedUpstreamRevisionIdsJson;
    }

    public Guid Id { get; private set; }
    public Guid ChangeRequestId { get; private set; }
    public string BaseNumber { get; private set; } = string.Empty;
    public int Revision { get; private set; }
    public string DisplayNumber => ArtifactNumber.Display(BaseNumber, Revision);
    public RequirementLevel Level { get; private set; }
    public RequirementChangeKind Kind { get; private set; }
    public string Statement { get; private set; } = string.Empty;
    public string Rationale { get; private set; } = string.Empty;
    public string VerificationMethod { get; private set; } = string.Empty;
    public string RichText { get; private set; } = string.Empty;
    public string AttributesJson { get; private set; } = "{}";
    public string ImpactDispositionJson { get; private set; } = "{}";
    public string ProposedUpstreamRevisionIdsJson { get; private set; } = "[]";

    /// <summary>
    /// Which section of the specification this requirement belongs in, as the author chose it.
    ///
    /// A requirement's place in a document is part of what is being proposed, and until now nothing carried it:
    /// section membership existed only as a `SpecificationNode` row created after the fact, so an introduced
    /// requirement landed wherever a backfill put it and a modification could not move one. An author writing
    /// "the FMS shall sequence oceanic waypoints" knows it belongs under Navigation, and had nowhere to say so.
    ///
    /// Null means unchanged: for a modification, leave the requirement where it is; for an introduction, let the
    /// existing placement rule decide. That keeps it optional, which matters because a proposal is worth saving
    /// before every field is settled.
    /// </summary>
    public Guid? TargetSectionId { get; private set; }
}
