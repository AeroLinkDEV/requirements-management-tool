using System.Text.RegularExpressions;

namespace AeroLink.Domain.Common;

public static partial class ArtifactNumber
{
    [GeneratedRegex("^(?:[A-Z]+-[0-9]{5,8}|SW-[0-9]{2}\\.[0-9]{2})$")]
    private static partial Regex BasePattern();
    // Five digits is the padded form, not a ceiling. The allocator counts without a bound, so once a
    // repository passes 99,999 change requests it hands out a six-digit number; requiring exactly five made
    // every create past that point fail validation permanently. Wider numbers are accepted only without
    // leading zeros, which keeps retired eight-digit identifiers like SCR-00000001 rejected.
    [GeneratedRegex("^(?:SRCR|HLRCR|LLRCR)-(?:[0-9]{5}|[1-9][0-9]{5,})$")]
    private static partial Regex ChangeRequestPattern();

    /// <summary>
    /// The change-request prefixes, which name the level of requirement a change request may carry.
    ///
    /// A System change request is an SRCR. A software one is an HLRCR or an LLRCR — never a single software
    /// prefix — because HLR and LLR change control are worked, reviewed and approved separately, and a
    /// reader who sees the identifier should already know which of the two they are looking at.
    /// </summary>
    private static readonly string[] ChangeRequestPrefixes = ["SRCR-", "HLRCR-", "LLRCR-"];

    public static string ValidateBase(string value)
    {
        var normalized = value.Trim().ToUpperInvariant();
        if (!BasePattern().IsMatch(normalized)
            || (ChangeRequestPrefixes.Any(prefix => normalized.StartsWith(prefix, StringComparison.Ordinal))
                && !ChangeRequestPattern().IsMatch(normalized)))
        {
            throw new DomainException("Artifact identifiers must use PREFIX-00001 format, or SW-01.60 for a software build.");
        }

        return normalized;
    }

    public static string Display(string baseNumber, int revision)
    {
        var normalized = ValidateBase(baseNumber);
        return normalized.StartsWith("SW-", StringComparison.Ordinal) ? normalized : $"{normalized}.{revision:D2}";
    }
}
