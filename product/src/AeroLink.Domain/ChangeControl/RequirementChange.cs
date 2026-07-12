using AeroLink.Domain.Common;

namespace AeroLink.Domain.ChangeControl;

public enum RequirementLevel { System, HighLevel }
public enum RequirementChangeKind { Introduce, Modify, Retire }

public sealed class RequirementChange
{
    private RequirementChange() { }

    internal RequirementChange(Guid scrId, string baseNumber, int revision, RequirementLevel level,
        RequirementChangeKind kind, string statement, string rationale, string verificationMethod)
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
}
