using AeroLink.Domain.Common;
using System.Security.Cryptography;
using System.Text;

namespace AeroLink.Domain.Imports;

/// <summary>
/// One immutable Customer requirement staged from an accepted external baseline import.
/// Staging is deliberately not effectivity: no controlled requirement revision exists until the package is
/// selected into a draft candidate baseline and that baseline is frozen and materialized.
/// </summary>
public sealed class BaselineImportPackageItem
{
    private BaselineImportPackageItem() { }

    public BaselineImportPackageItem(Guid projectId, Guid baselineImportId, Guid sourceIdentityId,
        string baseNumber, int revision, string statement, string rationale, string sourceIdentifier,
        DateTimeOffset stagedAt)
    {
        if (projectId == Guid.Empty) throw new DomainException("A package item requires its Project.");
        if (baselineImportId == Guid.Empty) throw new DomainException("A package item requires its BaselineImport.");
        if (sourceIdentityId == Guid.Empty) throw new DomainException("A package item requires its SourceIdentity.");
        if (revision < 0) throw new DomainException("A package item revision cannot be negative.");
        Id = Guid.NewGuid(); ProjectId = projectId; BaselineImportId = baselineImportId;
        SourceIdentityId = sourceIdentityId; BaseNumber = ArtifactNumber.ValidateBase(baseNumber);
        if (!BaseNumber.StartsWith("CUSR-", StringComparison.Ordinal))
            throw new DomainException("An external package item must use the CUSR- prefix.");
        Revision = revision;
        Statement = Required(statement, "package item statement");
        Rationale = (rationale ?? "").Trim();
        SourceIdentifier = Required(sourceIdentifier, "source identifier");
        StagedAt = stagedAt;
    }

    public Guid Id { get; private set; }
    public Guid ProjectId { get; private set; }
    public Guid BaselineImportId { get; private set; }
    public Guid SourceIdentityId { get; private set; }
    public string BaseNumber { get; private set; } = "";
    public int Revision { get; private set; }
    public string Statement { get; private set; } = "";
    public string Rationale { get; private set; } = "";
    /// <summary>The quoted identifier supplied by the customer, retained alongside SourceIdentity.</summary>
    public string SourceIdentifier { get; private set; } = "";
    public DateTimeOffset StagedAt { get; private set; }

    private static string Required(string value, string name) =>
        string.IsNullOrWhiteSpace(value) ? throw new DomainException($"A {name} is required.") : value.Trim();
}

public static class BaselineImportPackageManifest
{
    public static string Hash(IEnumerable<BaselineImportPackageItem> items)
    {
        var manifest = string.Join(";", items.OrderBy(x => x.BaseNumber, StringComparer.Ordinal)
            .ThenBy(x => x.Revision).ThenBy(x => x.SourceIdentityId)
            .Select(x => string.Join("|", x.Id, x.SourceIdentityId, x.BaseNumber, x.Revision,
                x.SourceIdentifier, x.Statement, x.Rationale)));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(manifest))).ToLowerInvariant();
    }
}

/// <summary>Immutable membership of one accepted external package in one candidate baseline.</summary>
public sealed class BaselineExternalPackageSelection
{
    private BaselineExternalPackageSelection() { }

    internal BaselineExternalPackageSelection(Guid baselineId, Guid baselineImportId, string packageContentHash,
        DateTimeOffset selectedAt, string selectedBy)
    {
        if (baselineId == Guid.Empty || baselineImportId == Guid.Empty)
            throw new DomainException("An external package selection requires a baseline and package.");
        if (string.IsNullOrWhiteSpace(selectedBy)) throw new DomainException("An external package selection requires an actor.");
        if (string.IsNullOrWhiteSpace(packageContentHash) || packageContentHash.Length != 64)
            throw new DomainException("A valid external package content hash is required.");
        Id = Guid.NewGuid(); BaselineId = baselineId; BaselineImportId = baselineImportId;
        PackageContentHash = packageContentHash.ToLowerInvariant();
        SelectedAt = selectedAt; SelectedBy = selectedBy.Trim();
    }

    public Guid Id { get; private set; }
    public Guid BaselineId { get; private set; }
    public Guid BaselineImportId { get; private set; }
    public string PackageContentHash { get; private set; } = "";
    public DateTimeOffset SelectedAt { get; private set; }
    public string SelectedBy { get; private set; } = "";
}
