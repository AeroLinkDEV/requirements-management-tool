using AeroLink.Domain.Common;

namespace AeroLink.Domain.Programs;

/// <summary>Governed version ordering; this never establishes ancestry or selects a project for navigation.</summary>
public static class SoftwareReleaseOrdering
{
    private readonly record struct ReleaseOrderEntry(SoftwareRelease Release, bool HasNumericIdentity,
        int Major, int Minor, string OfficialIdentity, string RawVersion);

    /// <summary>
    /// Orders release projections through the governed software-build identity. The raw Version remains the
    /// displayed historical value, while its parsed numeric identity controls ordering (9.0 before 10.5).
    /// Historical rows without CanonicalIdentity derive the same official identity from raw Version; invalid
    /// legacy rows remain visible at the end using their retained identity/text as a deterministic fallback.
    /// </summary>
    public static List<SoftwareRelease> Ascending(IEnumerable<SoftwareRelease> releases) =>
        releases.OrderBy(Key, StringComparer.Ordinal).ToList();

    public static string Key(SoftwareRelease release)
    {
        var entry = ReleaseOrderEntries([release]).Single();
        return $"{(entry.HasNumericIdentity ? "0" : "1")}|{entry.OfficialIdentity}|{entry.RawVersion}!{release.Id:D}";
    }

    public static SoftwareRelease? PreferredContext(IEnumerable<SoftwareRelease> releases) =>
        ReleaseOrderEntries(releases)
            .OrderBy(x => x.Release.IsReleased)
            .ThenByDescending(x => x.HasNumericIdentity)
            .ThenByDescending(x => x.Major)
            .ThenByDescending(x => x.Minor)
            .ThenByDescending(x => x.OfficialIdentity, StringComparer.Ordinal)
            .ThenByDescending(x => x.RawVersion, StringComparer.Ordinal)
            .ThenBy(x => x.Release.Id)
            .Select(x => x.Release)
            .FirstOrDefault();

    private static IEnumerable<ReleaseOrderEntry> ReleaseOrderEntries(IEnumerable<SoftwareRelease> releases) =>
        releases.Select(release =>
        {
            try
            {
                var parsed = SoftwareBuildIdentifier.Parse(release.Version);
                return new ReleaseOrderEntry(release, true, parsed.Major, parsed.Minor,
                    parsed.OfficialName, release.Version.Trim());
            }
            catch (DomainException)
            {
                // A historical CanonicalIdentity is a preserved server fact. If it is itself an official
                // identity, it still supplies a numeric sort key without rewriting the invalid raw value.
                var identity = release.CanonicalIdentity?.Trim();
                if (identity is not null && identity.StartsWith("SW-", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        var parsed = SoftwareBuildIdentifier.Parse(identity[3..]);
                        if (string.Equals(parsed.OfficialName, identity, StringComparison.OrdinalIgnoreCase))
                            return new ReleaseOrderEntry(release, true, parsed.Major, parsed.Minor,
                                parsed.OfficialName, release.Version.Trim());
                    }
                    catch (DomainException) { /* Keep the invalid historical row visible at the end. */ }
                }
                return new ReleaseOrderEntry(release, false, int.MaxValue, int.MaxValue,
                    identity ?? release.Version.Trim(), release.Version.Trim());
            }
        });

}
