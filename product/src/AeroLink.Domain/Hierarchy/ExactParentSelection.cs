using AeroLink.Domain.Common;

namespace AeroLink.Domain.Hierarchy;

/// <summary>The shared classification for every configured exact-parent topology.</summary>
public enum ExactParentClassification
{
    Unspecified,
    Allocated,
    Derived,
}

/// <summary>
/// The one structural exact-parent-or-derived policy shared by Requirements, Cases, and Procedures.
/// Repository-backed callers additionally validate that the exact revisions belong to the configured
/// project/build topology.
/// </summary>
public static class ExactParentSelectionPolicy
{
    public static IReadOnlyList<Guid> NormalizeIds(IEnumerable<Guid>? parentRevisionIds,
        string artifactNoun = "controlled artifact")
    {
        var supplied = (parentRevisionIds ?? []).ToArray();
        if (supplied.Any(x => x == Guid.Empty))
            throw new DomainException($"A {artifactNoun} exact parent selection cannot contain an empty revision.");
        if (supplied.Distinct().Count() != supplied.Length)
            throw new DomainException($"A {artifactNoun} exact parent selection cannot contain duplicate revisions.");
        return supplied.OrderBy(x => x).ToArray();
    }

    public static void Validate(ExactParentClassification classification,
        IEnumerable<Guid>? parentRevisionIds, string? derivedRationale,
        string artifactNoun = "controlled artifact")
    {
        var ids = NormalizeIds(parentRevisionIds, artifactNoun);
        var rationale = derivedRationale?.Trim() ?? string.Empty;
        switch (classification)
        {
            case ExactParentClassification.Allocated when ids.Count > 0 && rationale.Length == 0:
                return;
            case ExactParentClassification.Derived when ids.Count == 0 && rationale.Length > 0:
                return;
            case ExactParentClassification.Unspecified:
                throw new DomainException(
                    $"A {artifactNoun} must be Allocated to an exact parent revision or explicitly Derived with an engineering rationale.");
            default:
                throw new DomainException(
                    $"A {artifactNoun} must have exact parents or a nonblank Derived rationale, but not both or neither.");
        }
    }
}
