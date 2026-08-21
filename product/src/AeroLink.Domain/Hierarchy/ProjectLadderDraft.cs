using System.Security.Cryptography;
using System.Text;
using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Common;

namespace AeroLink.Domain.Hierarchy;

public sealed record LadderStepDraft(string CatalogueEntry, int Position, LevelCapabilities Capabilities);
public sealed record LadderRelationshipDraft(string Parent, string Child);

/// <summary>Canonicalizes and hashes an edited ladder without including database-generated identities.</summary>
public static class ProjectLadderSnapshot
{
    public static string Canonicalize(IEnumerable<LadderStepDraft> steps, IEnumerable<LadderRelationshipDraft> relationships)
    {
        var canonicalSteps = steps.OrderBy(x => x.Position).ThenBy(x => x.CatalogueEntry, StringComparer.Ordinal)
            .Select(x => $"{x.Position}:{x.CatalogueEntry}:{(int)x.Capabilities}");
        var canonicalEdges = relationships.OrderBy(x => x.Parent, StringComparer.Ordinal)
            .ThenBy(x => x.Child, StringComparer.Ordinal)
            .Select(x => $"{x.Parent}>{x.Child}");
        return $"steps[{string.Join(";", canonicalSteps)}]|edges[{string.Join(";", canonicalEdges)}]";
    }

    public static string Hash(string canonicalSnapshot) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalSnapshot))).ToLowerInvariant();
}

/// <summary>Shared domain validation for an authoring payload before it reaches persistence.</summary>
public static class ProjectLadderDraftValidator
{
    public static (IReadOnlyList<LadderStepDraft> Steps, IReadOnlyList<LadderRelationshipDraft> Relationships)
        Validate(IEnumerable<LadderStepDraft> steps, IEnumerable<LadderRelationshipDraft> relationships, ILadderPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        var materialized = steps?.ToList() ?? throw new DomainException("A ladder requires at least one step.");
        if (materialized.Count == 0) throw new DomainException("A ladder requires at least one step.");
        if (materialized.Any(x => string.IsNullOrWhiteSpace(x.CatalogueEntry)))
            throw new DomainException("Every ladder step must name a catalogue entry.");
        if (materialized.Select(x => x.CatalogueEntry).Distinct(StringComparer.Ordinal).Count() != materialized.Count)
            throw new DomainException("A ladder cannot contain duplicate catalogue entries.");
        if (materialized.Select(x => x.Position).Distinct().Count() != materialized.Count
            || !materialized.Select(x => x.Position).OrderBy(x => x).SequenceEqual(Enumerable.Range(1, materialized.Count)))
            throw new DomainException("Ladder positions must be unique and contiguous starting at one.");

        foreach (var step in materialized)
        {
            if (!Enum.TryParse<RequirementLevel>(step.CatalogueEntry, false, out var level) || !Enum.IsDefined(level))
                throw new DomainException($"Unknown ladder catalogue entry '{step.CatalogueEntry}'.");
            var definition = policy.Definition(level);
            if ((step.Capabilities & ~definition.Capabilities) != 0)
                throw new DomainException($"Capabilities for {level} exceed the supported catalogue bindings.");
        }

        var edgeList = relationships?.ToList() ?? [];
        if (edgeList.Any(x => string.IsNullOrWhiteSpace(x.Parent) || string.IsNullOrWhiteSpace(x.Child)))
            throw new DomainException("Every ladder relationship must name both endpoints.");
        if (edgeList.Any(x => string.Equals(x.Parent, x.Child, StringComparison.Ordinal)))
            throw new DomainException("A ladder relationship cannot point a step to itself.");
        if (edgeList.Select(x => (x.Parent, x.Child)).Distinct().Count() != edgeList.Count)
            throw new DomainException("A ladder cannot contain duplicate relationship edges.");
        var known = materialized.Select(x => x.CatalogueEntry).ToHashSet(StringComparer.Ordinal);
        var positions = materialized.ToDictionary(x => x.CatalogueEntry, x => x.Position, StringComparer.Ordinal);
        if (edgeList.Any(x => !known.Contains(x.Parent) || !known.Contains(x.Child)))
            throw new DomainException("Every ladder relationship endpoint must belong to this ladder.");
        if (edgeList.Any(x => positions[x.Parent] >= positions[x.Child]))
            throw new DomainException("A ladder relationship must point from an earlier position to a later position.");
        // The project graph is authored data, not a copy of the legacy policy adjacency.  A later
        // configuration may connect any two selected catalogue entries (for example System -> LowLevel),
        // while still remaining a valid hierarchy.  Keep only graph invariants here; runtime consumers
        // continue to ask the code-owned policy until a later slice routes them to this graph.
        var childrenByParent = edgeList.GroupBy(x => x.Parent, StringComparer.Ordinal)
            .ToDictionary(x => x.Key, x => x.Select(edge => edge.Child).ToArray(), StringComparer.Ordinal);
        var visiting = new HashSet<string>(StringComparer.Ordinal);
        var visited = new HashSet<string>(StringComparer.Ordinal);
        bool HasCycle(string node)
        {
            if (!visiting.Add(node)) return true;
            if (visited.Contains(node)) { visiting.Remove(node); return false; }
            if (childrenByParent.TryGetValue(node, out var children) && children.Any(HasCycle)) return true;
            visiting.Remove(node); visited.Add(node); return false;
        }

        if (known.Any(HasCycle))
            throw new DomainException("A ladder relationship graph cannot contain a cycle.");
        return (materialized, edgeList);
    }
}
