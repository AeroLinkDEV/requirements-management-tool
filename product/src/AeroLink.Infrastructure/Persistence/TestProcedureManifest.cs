using System.Security.Cryptography;
using System.Text;

namespace AeroLink.Infrastructure.Persistence;

/// <summary>One exact active procedure revision in a build-scoped manifest.</summary>
internal sealed record TestProcedureManifestEntry(
    Guid ProcedureId,
    Guid RevisionId,
    string BaseNumber,
    int Revision);

/// <summary>
/// Canonical serialization and hashing for every procedure manifest producer.
///
/// Normal materialization and the one-time legacy bootstrap must produce the same hash for the same exact
/// membership. Keeping the representation here prevents either path from becoming a subtly different notion
/// of an exact build.
/// </summary>
internal static class TestProcedureManifest
{
    public static string Content(IEnumerable<TestProcedureManifestEntry> entries) => string.Join("|",
        entries.OrderBy(x => x.BaseNumber, StringComparer.Ordinal)
            .ThenBy(x => x.Revision)
            .ThenBy(x => x.RevisionId)
            .Select(x => $"{x.BaseNumber}.{x.Revision:D2}:{x.RevisionId:D}"));

    public static string Hash(IEnumerable<TestProcedureManifestEntry> entries) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Content(entries)))).ToLowerInvariant();
}
