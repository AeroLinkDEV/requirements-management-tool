using System.Text.RegularExpressions;

namespace AeroLink.Domain.Common;

public static partial class ArtifactNumber
{
    [GeneratedRegex("^(?:[A-Z]+-[0-9]{5,8}|SW-[0-9]{2}\\.[0-9]{2})$")]
    private static partial Regex BasePattern();
    [GeneratedRegex("^(?:SCR|SWCR)-[0-9]{5}$")]
    private static partial Regex ChangeRequestPattern();

    public static string ValidateBase(string value)
    {
        var normalized = value.Trim().ToUpperInvariant();
        if (!BasePattern().IsMatch(normalized)
            || ((normalized.StartsWith("SCR-", StringComparison.Ordinal)
                    || normalized.StartsWith("SWCR-", StringComparison.Ordinal))
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
