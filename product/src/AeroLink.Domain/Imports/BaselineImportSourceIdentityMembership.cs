using AeroLink.Domain.Common;

namespace AeroLink.Domain.Imports;

/// <summary>
/// The observation that one source identity belonged to one particular extract.
///
/// <see cref="SourceIdentity.BaselineImportId"/> remains the first import that introduced the stable
/// identity. This row is the per-import membership needed to distinguish that provenance fact from what a
/// later delta actually contained.
/// </summary>
public sealed class BaselineImportSourceIdentityMembership
{
    private BaselineImportSourceIdentityMembership() { }

    public BaselineImportSourceIdentityMembership(Guid baselineImportId, Guid sourceIdentityId,
        bool inImportedBaseline, DateTimeOffset recordedAt)
    {
        if (baselineImportId == Guid.Empty) throw new DomainException("An import membership requires its BaselineImport.");
        if (sourceIdentityId == Guid.Empty) throw new DomainException("An import membership requires its SourceIdentity.");
        Id = Guid.NewGuid();
        BaselineImportId = baselineImportId;
        SourceIdentityId = sourceIdentityId;
        InImportedBaseline = inImportedBaseline;
        RecordedAt = recordedAt;
    }

    public Guid Id { get; private set; }
    public Guid BaselineImportId { get; private set; }
    public Guid SourceIdentityId { get; private set; }
    public bool InImportedBaseline { get; private set; }
    public DateTimeOffset RecordedAt { get; private set; }
}
