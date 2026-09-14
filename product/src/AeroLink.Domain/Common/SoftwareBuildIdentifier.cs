namespace AeroLink.Domain.Common;

/// <summary>The one official software-build name used for the release and its controlled baseline.</summary>
public static class SoftwareBuildIdentifier
{
    public readonly record struct Parsed(int Major, int Minor)
    {
        public string CanonicalKey => $"{Major:D2}.{Minor:D2}";
        public string OfficialName => $"SW-{CanonicalKey}";
    }

    /// <summary>
    /// Parses the controlled major.minor syntax and returns the numeric identity used by every release
    /// creation path. The raw version entered by an operator remains separately preserved on the release.
    /// </summary>
    public static Parsed Parse(string version)
    {
        var parts = (version ?? "").Trim().Split('.', StringSplitOptions.None);
        if (parts.Length != 2 || parts[0].Length is < 1 or > 2 || parts[1].Length is < 1 or > 2
            || !IsAsciiDigits(parts[0]) || !IsAsciiDigits(parts[1])
            || !int.TryParse(parts[0], out var major) || !int.TryParse(parts[1], out var minor)
            || major is < 0 or > 99)
            throw new DomainException("Software build versions must use major.minor format, for example 1.6.");
        return new(major, parts[1].Trim().Length == 1 ? minor * 10 : minor);
    }

    private static bool IsAsciiDigits(string value) => value.All(c => c is >= '0' and <= '9');

    /// <summary>
    /// The minor part is read as the decimal it is written as, not as a count: `1.6` is six tenths and becomes
    /// `SW-01.60`, while `1.10` is ten hundredths and becomes `SW-01.10`. Treating a single digit as a count
    /// and scaling it by ten made `1.10` — the ordinary successor to `1.9` — throw, and this is reached from
    /// baseline creation and document generation, where a release version is whatever somebody typed.
    /// </summary>
    public static string FromVersion(string version)
        => Parse(version).OfficialName;
}
