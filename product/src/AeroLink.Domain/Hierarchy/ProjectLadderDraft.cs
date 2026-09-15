using System.Security.Cryptography;
using System.Text;
using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Common;
using AeroLink.Domain.Verification;

namespace AeroLink.Domain.Hierarchy;

public sealed record LadderStepDraft(string CatalogueEntry, int Position, LevelCapabilities Capabilities,
    IReadOnlyList<VerificationArtifactKind>? EnabledArtifactKinds = null,
    IReadOnlyList<string>? UnrecognizedArtifactKinds = null)
{
    public IReadOnlyList<VerificationArtifactKind>? EnabledKinds => EnabledArtifactKinds;

    /// <summary>
    /// The artifact kinds this step enables once a policy is applied.
    ///
    /// Three inputs have to stay distinguishable, because collapsing them is how a disabled level acquires
    /// artifacts it never selected. An absent/null profile keeps the maintained capability-dependent
    /// fallback (the catalogue profile when the level verifies, nothing when it does not); an explicitly
    /// supplied profile — including an explicitly empty one — is never replaced. A newly initialized
    /// project default is a fourth case: it is supplied explicitly by the creation factory, not derived here.
    /// </summary>
    public IReadOnlyList<VerificationArtifactKind> EffectiveKinds(LevelDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        return EnabledArtifactKinds ?? (Capabilities.HasFlag(LevelCapabilities.HasVerification)
            ? definition.VerificationProfile?.EnabledKinds : null) ?? [];
    }
}
public sealed record LadderRelationshipDraft(string Parent, string Child);

/// <summary>
/// One machine-readable problem with a supplied ladder, identified by code and by the level and field it
/// belongs to. The browser is expected to act on <see cref="Code"/>/<see cref="Level"/>/<see cref="Field"/>
/// and to present <see cref="Message"/>; it must never parse the message to discover the subject.
/// </summary>
public sealed record LadderFinding(string Code, string? Level, string? Field, string Message, string? Token = null)
{
    public static LadderFinding Ladder(string code, string message) => new(code, null, "ladder", message);
}

/// <summary>Canonicalizes and hashes an edited ladder without including database-generated identities.</summary>
public static class ProjectLadderSnapshot
{
    public const int LegacySchemaVersion = VerificationArtifactProfileSchema.Legacy;
    public const int CurrentSchemaVersion = VerificationArtifactProfileSchema.Current;

    /// <summary>
    /// The original canonical form.  Keep this method byte-for-byte stable: stored v1 histories and hashes are
    /// evidence and must remain verifiable without being recomputed in the v2 shape.
    /// </summary>
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

    /// <summary>Canonical v2 adds the profile shape while retaining the same deterministic ordering rules.</summary>
    public static string CanonicalizeV2(IEnumerable<LadderStepDraft> steps,
        IEnumerable<LadderRelationshipDraft> relationships, ILadderPolicy? policy = null)
    {
        policy ??= LegacyLadderPolicy.Instance;
        var canonicalSteps = steps.OrderBy(x => x.Position).ThenBy(x => x.CatalogueEntry, StringComparer.Ordinal)
            .Select(x =>
            {
                var level = Enum.Parse<RequirementLevel>(x.CatalogueEntry, false);
                var definition = policy.Definition(level);
                var hasVerification = x.Capabilities.HasFlag(LevelCapabilities.HasVerification);
                var kinds = x.EffectiveKinds(definition).ToArray();
                var profile = definition.VerificationProfile;
                if (!hasVerification)
                {
                    if (kinds.Length != 0)
                        throw new DomainException($"A level without verification capability cannot enable verification artifacts.");
                }
                else
                    VerificationArtifactProfile.ValidateEnabledKinds(profile?.Discipline
                        ?? throw new DomainException($"The {level} definition has no verification profile."), kinds);
                return $"{x.Position}:{x.CatalogueEntry}:{(int)x.Capabilities}:{VerificationArtifactProfile.SerializeKinds(kinds)}";
            });
        var canonicalEdges = relationships.OrderBy(x => x.Parent, StringComparer.Ordinal)
            .ThenBy(x => x.Child, StringComparer.Ordinal)
            .Select(x => $"{x.Parent}>{x.Child}");
        return $"schema[{CurrentSchemaVersion}]|steps[{string.Join(';', canonicalSteps)}]|edges[{string.Join(';', canonicalEdges)}]";
    }

    public static string HashV2(IEnumerable<LadderStepDraft> steps, IEnumerable<LadderRelationshipDraft> relationships,
        ILadderPolicy? policy = null) => Hash(CanonicalizeV2(steps, relationships, policy));

    /// <summary>
    /// Selects the canonical algorithm from the persisted configuration schema.  History rows are evidence of
    /// the algorithm that wrote them, so callers must not silently use the current default when re-emitting one.
    /// </summary>
    public static string CanonicalizeForSchema(int schemaVersion, IEnumerable<LadderStepDraft> steps,
        IEnumerable<LadderRelationshipDraft> relationships, ILadderPolicy? policy = null) => schemaVersion switch
        {
            LegacySchemaVersion => Canonicalize(steps, relationships),
            CurrentSchemaVersion => CanonicalizeV2(steps, relationships, policy),
            _ => throw new DomainException($"Unsupported ladder snapshot schema version {schemaVersion}.")
        };

    public static string HashForSchema(int schemaVersion, IEnumerable<LadderStepDraft> steps,
        IEnumerable<LadderRelationshipDraft> relationships, ILadderPolicy? policy = null) =>
        Hash(CanonicalizeForSchema(schemaVersion, steps, relationships, policy));

    public static bool Verify(string canonicalSnapshot, string snapshotHash, int schemaVersion = LegacySchemaVersion)
    {
        if (string.IsNullOrWhiteSpace(canonicalSnapshot) || string.IsNullOrWhiteSpace(snapshotHash)) return false;
        if (schemaVersion is not (LegacySchemaVersion or CurrentSchemaVersion)) return false;
        if (schemaVersion == CurrentSchemaVersion
            && !canonicalSnapshot.StartsWith($"schema[{CurrentSchemaVersion}]|", StringComparison.Ordinal)) return false;
        if (schemaVersion == LegacySchemaVersion
            && canonicalSnapshot.StartsWith("schema[", StringComparison.Ordinal)) return false;
        return string.Equals(Hash(canonicalSnapshot), snapshotHash.Trim(), StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>Shared domain validation for an authoring payload before it reaches persistence.</summary>
public static class ProjectLadderDraftValidator
{
    /// <summary>
    /// Diagnoses a supplied ladder without throwing, so a save, a resume, a readiness claim and the final
    /// gate can all explain the same input the same way. <see cref="Validate"/> is the throwing view of
    /// exactly these findings: the first finding is the message it raises, in the same order as before.
    /// </summary>
    public static IReadOnlyList<LadderFinding> Inspect(IReadOnlyList<LadderStepDraft> steps,
        IReadOnlyList<LadderRelationshipDraft> relationships, ILadderPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(steps);
        ArgumentNullException.ThrowIfNull(relationships);

        // Structure first: every later check needs a coherent step list, so the scan stops at the first
        // structural problem rather than inventing secondary complaints about the same input.
        if (steps.Count == 0)
            return [LadderFinding.Ladder("ladder_step_missing", "A ladder requires at least one step.")];
        if (steps.Any(x => string.IsNullOrWhiteSpace(x.CatalogueEntry)))
            return [LadderFinding.Ladder("ladder_step_unnamed", "Every ladder step must name a catalogue entry.")];
        if (steps.Select(x => x.CatalogueEntry).Distinct(StringComparer.Ordinal).Count() != steps.Count)
            return [LadderFinding.Ladder("ladder_step_duplicate", "A ladder cannot contain duplicate catalogue entries.")];
        if (steps.Select(x => x.Position).Distinct().Count() != steps.Count
            || !steps.Select(x => x.Position).OrderBy(x => x).SequenceEqual(Enumerable.Range(1, steps.Count)))
            return [LadderFinding.Ladder("ladder_position_invalid",
                "Ladder positions must be unique and contiguous starting at one.")];

        var findings = new List<LadderFinding>();
        var levels = new List<(LadderStepDraft Step, RequirementLevel Level)>();
        foreach (var step in steps)
        {
            if (!Enum.TryParse<RequirementLevel>(step.CatalogueEntry, false, out var level) || !Enum.IsDefined(level))
            {
                findings.Add(new LadderFinding("ladder_level_unknown", step.CatalogueEntry, "catalogueEntry",
                    $"Unknown ladder catalogue entry '{step.CatalogueEntry}'."));
                return findings;
            }
            levels.Add((step, level));
        }

        foreach (var (step, level) in levels)
        {
            var definition = policy.Definition(level);
            if ((step.Capabilities & ~definition.Capabilities) != 0)
                findings.Add(new LadderFinding("capability_unsupported", level.ToString(), "capabilities",
                    $"Capabilities for {level} exceed the supported catalogue bindings."));
            // An unrecognized token is reported as its own content problem. The profile is then still
            // judged on the kinds that were understood, so a contradictory mask is not hidden behind it.
            foreach (var token in step.UnrecognizedArtifactKinds ?? [])
                findings.Add(new LadderFinding("artifact_kind_unrecognized", level.ToString(),
                    "enabledArtifactKinds",
                    $"The {level} verification profile contains an unrecognized artifact kind '{token}'.",
                    token));

            var hasVerification = step.Capabilities.HasFlag(LevelCapabilities.HasVerification);
            var kinds = step.EffectiveKinds(definition);
            if (!hasVerification)
            {
                if (kinds.Count != 0)
                    findings.Add(new LadderFinding("verification_disabled_with_artifacts", level.ToString(),
                        "enabledArtifactKinds",
                        "A level without verification capability cannot enable verification artifacts."));
            }
            else if (definition.VerificationProfile is null)
            {
                findings.Add(new LadderFinding("verification_profile_missing", level.ToString(),
                    "enabledArtifactKinds", $"The {level} definition has no verification profile."));
            }
            else
            {
                try
                {
                    VerificationArtifactProfile.ValidateEnabledKinds(definition.VerificationProfile.Discipline, kinds);
                }
                catch (DomainException ex)
                {
                    findings.Add(new LadderFinding("verification_profile_invalid", level.ToString(),
                        "enabledArtifactKinds", ex.Message));
                }
            }
        }

        var known = steps.Select(x => x.CatalogueEntry).ToHashSet(StringComparer.Ordinal);
        var positions = steps.ToDictionary(x => x.CatalogueEntry, x => x.Position, StringComparer.Ordinal);
        if (relationships.Any(x => string.IsNullOrWhiteSpace(x.Parent) || string.IsNullOrWhiteSpace(x.Child)))
            findings.Add(LadderFinding.Ladder("ladder_relationship_unnamed",
                "Every ladder relationship must name both endpoints."));
        else if (relationships.Any(x => string.Equals(x.Parent, x.Child, StringComparison.Ordinal)))
            findings.Add(LadderFinding.Ladder("ladder_relationship_self",
                "A ladder relationship cannot point a step to itself."));
        else if (relationships.Select(x => (x.Parent, x.Child)).Distinct().Count() != relationships.Count)
            findings.Add(LadderFinding.Ladder("ladder_relationship_duplicate",
                "A ladder cannot contain duplicate relationship edges."));
        else if (relationships.Any(x => !known.Contains(x.Parent) || !known.Contains(x.Child)))
            findings.Add(LadderFinding.Ladder("ladder_relationship_unknown_endpoint",
                "Every ladder relationship endpoint must belong to this ladder."));
        else if (relationships.Any(x => positions[x.Parent] >= positions[x.Child]))
            findings.Add(LadderFinding.Ladder("ladder_relationship_direction",
                "A ladder relationship must point from an earlier position to a later position."));
        else if (HasCycle(relationships, steps.Select(x => x.CatalogueEntry)))
            findings.Add(LadderFinding.Ladder("ladder_relationship_cycle",
                "A ladder relationship graph cannot contain a cycle."));
        return findings;
    }

    private static bool HasCycle(IReadOnlyList<LadderRelationshipDraft> relationships, IEnumerable<string> nodes)
    {
        // The project graph is authored data, not a copy of the legacy policy adjacency. A configured
        // Active graph may connect any two selected catalogue entries (for example System -> LowLevel),
        // while Draft runtime behavior remains on the prior effective policy until the activation gate.
        var childrenByParent = relationships.GroupBy(x => x.Parent, StringComparer.Ordinal)
            .ToDictionary(x => x.Key, x => x.Select(edge => edge.Child).ToArray(), StringComparer.Ordinal);
        var visiting = new HashSet<string>(StringComparer.Ordinal);
        var visited = new HashSet<string>(StringComparer.Ordinal);
        bool Visit(string node)
        {
            if (!visiting.Add(node)) return true;
            if (visited.Contains(node)) { visiting.Remove(node); return false; }
            if (childrenByParent.TryGetValue(node, out var children) && children.Any(Visit)) return true;
            visiting.Remove(node); visited.Add(node); return false;
        }

        return nodes.Any(Visit);
    }

    public static (IReadOnlyList<LadderStepDraft> Steps, IReadOnlyList<LadderRelationshipDraft> Relationships)
        Validate(IEnumerable<LadderStepDraft> steps, IEnumerable<LadderRelationshipDraft> relationships, ILadderPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        var materialized = steps?.ToList() ?? throw new DomainException("A ladder requires at least one step.");
        var edgeList = relationships?.ToList() ?? [];
        var findings = Inspect(materialized, edgeList, policy);
        if (findings.Count > 0) throw new DomainException(findings[0].Message);
        return (materialized, edgeList);
    }

}
