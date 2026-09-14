using AeroLink.Domain.Programs;
using Microsoft.EntityFrameworkCore;

namespace AeroLink.Infrastructure.Persistence;

/// <summary>
/// Provider-translatable canonical ordering for bounded release pages. This is a read projection only:
/// historical identifiers and the nullable canonical identity are never rewritten. Keep its compatibility
/// grammar qualified against SoftwareBuildIdentifier and SoftwareReleaseOrdering on both database providers.
/// </summary>
public static class SoftwareReleaseOrderingQuery
{
    public sealed class Row
    {
        public SoftwareRelease Release { get; init; } = null!;
        public string SortKey { get; init; } = "";
    }

    public static IQueryable<Row> WithSortKey(IQueryable<SoftwareRelease> query, string ordinalCollation) => query
        .Select(release => new
        {
            Release = release,
            Raw = release.Version.Trim(),
            Stored = (release.CanonicalIdentity ?? "").Trim().ToUpper(),
        })
        .Select(x => new
        {
            x.Release, x.Raw, x.Stored,
            Dot = x.Raw.IndexOf("."),
            NonDigits = x.Raw.Replace("0", "").Replace("1", "").Replace("2", "").Replace("3", "")
                .Replace("4", "").Replace("5", "").Replace("6", "").Replace("7", "")
                .Replace("8", "").Replace("9", ""),
            StoredNonDigits = x.Stored.Replace("0", "").Replace("1", "").Replace("2", "").Replace("3", "")
                .Replace("4", "").Replace("5", "").Replace("6", "").Replace("7", "")
                .Replace("8", "").Replace("9", ""),
        })
        .Select(x => new
        {
            x.Release, x.Raw, x.Stored,
            Official = (x.Dot == 1 || x.Dot == 2) && x.NonDigits == "."
                && x.Raw.Length - x.Dot - 1 >= 1 && x.Raw.Length - x.Dot - 1 <= 2
                ? "SW-" + (x.Dot == 1 ? "0" : "") + x.Raw.Substring(0, x.Dot) + "."
                    + x.Raw.Substring(x.Dot + 1) + (x.Raw.Length - x.Dot - 1 == 1 ? "0" : "")
                : x.Stored.Length == 8 && x.Stored.StartsWith("SW-") && x.Stored.IndexOf(".") == 5
                    && x.StoredNonDigits == "SW-." ? x.Stored : null,
        })
        .Select(x => new Row { Release = x.Release, SortKey = x.Official != null
            ? "0|" + x.Official + "|" + x.Raw + "!" + x.Release.Id.ToString().ToLower()
            : "1|" + (x.Release.CanonicalIdentity == null ? x.Raw : x.Release.CanonicalIdentity.Trim())
                + "|" + x.Raw + "!" + x.Release.Id.ToString().ToLower() })
        .Select(x => new Row { Release = x.Release, SortKey = EF.Functions.Collate(x.SortKey, ordinalCollation) });
}
