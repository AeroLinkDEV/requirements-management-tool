using System.Text;

namespace AeroLink.Domain.Requirements;

/// <summary>
/// How an owner or a tag is normalized before it is stored or compared.
///
/// Filtering used to be a case-folded substring scan over serialized JSON, which made `safe` match `failsafe`
/// and let an owner fragment match an unrelated attribute's value. Exact membership needs both sides reduced
/// the same way once, at write time, so the comparison is an equality the database can index rather than a
/// pattern it must scan.
///
/// Invariant lowercase rather than the current culture: a Turkish server and an English one must agree about
/// what `SAFE` is, or the same query answers differently depending on where it runs.
/// </summary>
public static class RequirementFilterValue
{
    public static string Normalize(string? value) =>
        (value ?? string.Empty).Trim().Normalize(NormalizationForm.FormC).ToLowerInvariant();

    /// <summary>True when a caller supplied something worth filtering on.</summary>
    public static bool HasValue(string? value) => !string.IsNullOrWhiteSpace(value);
}
