using AeroLink.Domain.Common;

namespace AeroLink.Domain.ChangeControl;

public enum RequirementLevel { System, HighLevel, LowLevel }
public enum RequirementChangeKind { Introduce, Modify, Retire }

public sealed record RequirementChangeDraft(string BaseNumber, int Revision, RequirementLevel Level,
    RequirementChangeKind Kind, string Statement, string Rationale, string VerificationMethod,
    string RichText = "", string AttributesJson = "{}", string ImpactDispositionJson = "{}");

public sealed class RequirementChange
{
    private RequirementChange() { }

    internal RequirementChange(Guid scrId, string baseNumber, int revision, RequirementLevel level,
        RequirementChangeKind kind, string statement, string rationale, string verificationMethod,
        string richText = "", string attributesJson = "{}", string impactDispositionJson = "{}")
    {
        Id = Guid.NewGuid();
        ScrId = scrId;
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
    }

    public Guid Id { get; private set; }
    public Guid ScrId { get; private set; }
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
}
