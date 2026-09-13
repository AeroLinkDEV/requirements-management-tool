using AeroLink.Domain.Common;
using Microsoft.EntityFrameworkCore;

namespace AeroLink.Infrastructure.Persistence;

/// <summary>
/// The single release identity authority used by workspace creation, successor release creation, and import
/// acceptance. Raw Version stays as entered for historical evidence; CanonicalIdentity is the uniqueness key.
/// Historical rows with no derived key are parsed on demand and any invalid or colliding inventory fails closed.
/// </summary>
public sealed class SoftwareReleaseIdentityAuthority(AeroLinkDbContext db)
{
    public async Task<SoftwareBuildIdentifier.Parsed> ValidateNewAsync(Guid projectId, string rawVersion,
        CancellationToken ct)
    {
        if (projectId == Guid.Empty) throw new DomainException("A release requires a project.");
        SoftwareBuildIdentifier.Parsed candidate;
        try { candidate = SoftwareBuildIdentifier.Parse(rawVersion); }
        catch (DomainException ex) { throw new DomainException($"Invalid software build version: {ex.Message}"); }

        var rows = await db.Releases.AsNoTracking().Where(x => x.ProjectId == projectId).ToListAsync(ct);
        var seen = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            SoftwareBuildIdentifier.Parsed parsed;
            try { parsed = SoftwareBuildIdentifier.Parse(row.Version); }
            catch (DomainException ex)
            {
                throw new DomainException(
                    $"Release identity review is required before adding another build: existing raw version '{row.Version}' is invalid. {ex.Message}");
            }
            var key = parsed.OfficialName;
            if (seen.TryGetValue(key, out var prior))
                throw new DomainException(
                    $"Release identity review is required before adding another build: '{prior}' and '{row.Version}' share canonical identity {key}.");
            seen[key] = row.Version;
            if (row.CanonicalIdentity is not null && !string.Equals(row.CanonicalIdentity, key, StringComparison.Ordinal))
                throw new DomainException(
                    $"Release identity review is required: stored canonical identity for '{row.Version}' is inconsistent.");
            if (string.Equals(key, candidate.OfficialName, StringComparison.Ordinal))
                throw new DomainException(
                    $"Build '{rawVersion.Trim()}' conflicts with existing canonical identity {candidate.OfficialName}.");
        }
        // Include pending rows in the same DbContext so a transaction cannot accidentally stage two equivalent
        // values through different entry points before its final save.
        foreach (var row in db.Releases.Local.Where(x => x.ProjectId == projectId))
        {
            var key = row.CanonicalIdentity ?? SoftwareBuildIdentifier.FromVersion(row.Version);
            if (string.Equals(key, candidate.OfficialName, StringComparison.Ordinal))
                throw new DomainException($"Build '{rawVersion.Trim()}' conflicts with existing canonical identity {candidate.OfficialName}.");
        }
        return candidate;
    }
}
