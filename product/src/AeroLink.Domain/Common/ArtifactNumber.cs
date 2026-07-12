using System.Text.RegularExpressions;

namespace AeroLink.Domain.Common;

public static partial class ArtifactNumber
{
    [GeneratedRegex("^[A-Z]+-[0-9]{8}$")]
    private static partial Regex BasePattern();

    public static string ValidateBase(string value)
    {
        var normalized = value.Trim().ToUpperInvariant();
        if (!BasePattern().IsMatch(normalized))
        {
            throw new DomainException("Artifact identifiers must use PREFIX-00000000 format.");
        }

        return normalized;
    }

    public static string Display(string baseNumber, int revision) =>
        $"{ValidateBase(baseNumber)}.{revision:D2}";
}
