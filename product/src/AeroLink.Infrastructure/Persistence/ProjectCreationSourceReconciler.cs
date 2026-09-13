using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Hierarchy;
using AeroLink.Domain.Requirements;
using AeroLink.Domain.Traceability;

namespace AeroLink.Infrastructure.Persistence;

public enum InceptionAttributeDestination { SourceOnly, Exclude, Statement, Rationale, VerificationMethod, SourceIdentifier }
public sealed record InceptionAttributeMapping(string SourceAttribute, InceptionAttributeDestination Destination,
    string? Reason = null, IReadOnlyDictionary<string, string>? ValueMappings = null);
public sealed record InceptionObjectMapping(string SourceKey, bool Include, string? ExclusionReason,
    RequirementLevel? Level, IReadOnlyList<InceptionAttributeMapping> Attributes);
public sealed record InceptionRelationMapping(string SourceKey, bool Include, string? ExclusionReason,
    RequirementTraceType? Type, bool? SourceIsParent);
public sealed record InceptionMapping(string SourceSha256, IReadOnlyList<InceptionObjectMapping> Objects,
    IReadOnlyList<InceptionRelationMapping> Relations, IReadOnlyDictionary<string, string> FindingResolutions);
public sealed record ReconciledInceptionRequirement(string SourceKey, string SourceModule, string SourceIdentifier,
    RequirementLevel Level, string Statement, string Rationale, string VerificationMethod);
public sealed record ReconciledInceptionTrace(string SourceKey, string ParentSourceKey, string ChildSourceKey,
    RequirementTraceType Type);
public sealed record InceptionReconciliation(bool Ready, int ObservedObjects, int IncludedObjects,
    int ExcludedObjects, int ObservedRelations, int IncludedRelations, int ExcludedRelations,
    IReadOnlyList<string> Errors, IReadOnlyList<ReconciledInceptionRequirement> Requirements,
    IReadOnlyList<ReconciledInceptionTrace> Traces, string? ManifestHash);

/// <summary>
/// Builds a server-verifiable proposal from a persisted parser observation and explicit mapping. A ready
/// result is not source acceptance: the orchestration must re-read it, sign it, and materialize atomically.
/// </summary>
public static class ProjectCreationSourceReconciler
{
    public static InceptionReconciliation Reconcile(ProjectCreationSourceAnalysis source, InceptionMapping mapping,
        ILadderPolicy ladder, VerificationMethodPolicy methods, string acceptedLadderHash)
    {
        var errors = new List<string>();
        var requirements = new List<ReconciledInceptionRequirement>();
        var traces = new List<ReconciledInceptionTrace>();
        if (!string.Equals(source.Sha256, mapping.SourceSha256, StringComparison.Ordinal))
            errors.Add("The mapping belongs to a different source upload.");
        if (acceptedLadderHash.Length != 64 || !acceptedLadderHash.All(Uri.IsHexDigit))
            errors.Add("The accepted ladder has no valid source-binding hash.");
        if (source.Objects.Count == 0) errors.Add("The source has no objects to reconcile.");
        var objectMappings = Index(mapping.Objects, x => x.SourceKey, "object", errors);
        var relationMappings = Index(mapping.Relations, x => x.SourceKey, "relation", errors);
        var observedKeys = source.Objects.Select(x => x.Key).ToHashSet(StringComparer.Ordinal);
        var observedRelations = source.Relations.Select(x => x.Key).ToHashSet(StringComparer.Ordinal);
        foreach (var key in objectMappings.Keys.Where(x => !observedKeys.Contains(x))) errors.Add($"Mapping object '{key}' was not observed in the source.");
        foreach (var key in relationMappings.Keys.Where(x => !observedRelations.Contains(x))) errors.Add($"Mapping relation '{key}' was not observed in the source.");
        var excludedObjects = 0;
        var excludedRelations = 0;
        foreach (var item in source.Objects)
        {
            if (!objectMappings.TryGetValue(item.Key, out var selection)) { errors.Add($"Object '{item.Key}' has no mapping or exclusion."); continue; }
            if (!selection.Include)
            {
                if (string.IsNullOrWhiteSpace(selection.ExclusionReason)) errors.Add($"Excluded object '{item.Key}' needs a reason.");
                excludedObjects++; continue;
            }
            if (item.Kind is not ("Requirement" or "Unmapped" or ""))
            { errors.Add($"Source object '{item.Key}' has unsupported kind '{item.Kind}'; explicitly exclude it instead of recasting it as a requirement."); continue; }
            if (selection.Level is not { } level || !ladder.OrderedLevels.Contains(level))
            { errors.Add($"Object '{item.Key}' needs a supported level in the accepted ladder."); continue; }
            var attributes = Index(selection.Attributes, x => x.SourceAttribute, "attribute", errors);
            foreach (var key in attributes.Keys.Where(x => !item.Attributes.ContainsKey(x))) errors.Add($"Object '{item.Key}' maps unobserved attribute '{key}'.");
            var fields = new Dictionary<InceptionAttributeDestination, string>();
            foreach (var attribute in item.Attributes)
            {
                if (!attributes.TryGetValue(attribute.Key, out var field)) { errors.Add($"Object '{item.Key}' attribute '{attribute.Key}' is unmapped."); continue; }
                if (!Enum.IsDefined(field.Destination)) { errors.Add($"Object '{item.Key}' has an unsupported field destination."); continue; }
                if (field.Destination is InceptionAttributeDestination.SourceOnly or InceptionAttributeDestination.Exclude)
                {
                    if (string.IsNullOrWhiteSpace(field.Reason)) errors.Add($"Object '{item.Key}' source-only/excluded attribute '{attribute.Key}' needs a reason.");
                    continue;
                }
                var value = attribute.Value;
                if (field.ValueMappings is { } values)
                {
                    if (field.Destination == InceptionAttributeDestination.SourceIdentifier)
                    { errors.Add($"Object '{item.Key}' source identity must retain the exact source value."); continue; }
                    if (!values.TryGetValue(value, out var mapped)) { errors.Add($"Object '{item.Key}' value for '{attribute.Key}' has no explicit value mapping."); continue; }
                    value = mapped;
                }
                if (!fields.TryAdd(field.Destination, value)) errors.Add($"Object '{item.Key}' maps multiple attributes to {field.Destination}.");
            }
            var statement = fields.GetValueOrDefault(InceptionAttributeDestination.Statement, "");
            var sourceIdentifier = fields.GetValueOrDefault(InceptionAttributeDestination.SourceIdentifier, item.SourceIdentifier);
            if (string.IsNullOrWhiteSpace(sourceIdentifier)) errors.Add($"Object '{item.Key}' needs an exact source identifier attribute mapping.");
            var method = fields.GetValueOrDefault(InceptionAttributeDestination.VerificationMethod, "");
            if (string.IsNullOrWhiteSpace(statement)) errors.Add($"Object '{item.Key}' needs mapped requirement wording.");
            if (ladder.HasVerification(level) && !string.IsNullOrWhiteSpace(method) && !methods.IsPermitted(method))
                errors.Add($"Object '{item.Key}' verification method must map to the accepted vocabulary: {methods.DescribePermitted()}.");
            if (!ladder.HasVerification(level) && !string.IsNullOrWhiteSpace(method))
                errors.Add($"Object '{item.Key}' targets a non-verification level; retain the source method as source-only data.");
            requirements.Add(new(item.Key, item.Module, sourceIdentifier, level, statement,
                fields.GetValueOrDefault(InceptionAttributeDestination.Rationale, ""), method));
        }
        foreach (var duplicate in requirements.GroupBy(x => (x.SourceModule, x.SourceIdentifier)).Where(x => x.Count() > 1))
            errors.Add($"Source identity '{duplicate.Key.SourceModule}/{duplicate.Key.SourceIdentifier}' occurs more than once.");
        var included = requirements.ToDictionary(x => x.SourceKey, StringComparer.Ordinal);
        foreach (var relation in source.Relations)
        {
            if (!relationMappings.TryGetValue(relation.Key, out var selection)) { errors.Add($"Relation '{relation.Key}' has no mapping or exclusion."); continue; }
            if (!selection.Include)
            {
                if (string.IsNullOrWhiteSpace(selection.ExclusionReason)) errors.Add($"Excluded relation '{relation.Key}' needs a reason.");
                excludedRelations++; continue;
            }
            if (selection.Type is not { } type || !Enum.IsDefined(type) || selection.SourceIsParent is not { } sourceIsParent)
            { errors.Add($"Relation '{relation.Key}' needs an explicit supported trace type and direction."); continue; }
            var parentKey = sourceIsParent ? relation.SourceKey : relation.TargetKey;
            var childKey = sourceIsParent ? relation.TargetKey : relation.SourceKey;
            if (!included.TryGetValue(parentKey, out var parent) || !included.TryGetValue(childKey, out var child))
            { errors.Add($"Relation '{relation.Key}' requires both exact endpoint objects to be included."); continue; }
            if (parentKey == childKey || !ladder.ParentLevels(child.Level).Contains(parent.Level))
            { errors.Add($"Relation '{relation.Key}' direction or endpoint levels are incompatible with the accepted ladder."); continue; }
            if (relation.Attributes.Count > 0)
            { errors.Add($"Relation '{relation.Key}' carries attributes unsupported by this trace profile; explicitly exclude it with a reason."); continue; }
            traces.Add(new(relation.Key, parentKey, childKey, type));
        }
        foreach (var finding in source.Findings)
            if (!mapping.FindingResolutions.TryGetValue(finding, out var reason) || string.IsNullOrWhiteSpace(reason))
                errors.Add($"Source finding needs an explicit disposition: {finding}");
        foreach (var finding in mapping.FindingResolutions.Keys.Where(x => !source.Findings.Contains(x))) errors.Add($"Disposition does not match a current source finding: {finding}");
        foreach (var requirement in requirements.Where(x => ladder.ParentLevels(x.Level).Count > 0))
            if (!traces.Any(x => x.ChildSourceKey == requirement.SourceKey && x.Type == RequirementTraceType.AllocatedFrom))
                errors.Add($"Object '{requirement.SourceKey}' requires an included exact upstream allocation in the accepted ladder. Include and map its source parent relation, exclude this object, or review a compatible ladder mapping; an excluded reference cannot make it a root.");
        if (requirements.Count == 0) errors.Add("At least one source requirement must be included.");
        string? manifestHash = null;
        if (errors.Count == 0)
        {
            var canonical = JsonSerializer.Serialize(new
            {
                schema = 1, source.Sha256, ladderHash = acceptedLadderHash,
                requirements = requirements.OrderBy(x => x.SourceKey, StringComparer.Ordinal),
                traces = traces.OrderBy(x => x.SourceKey, StringComparer.Ordinal),
                excludedObjects = mapping.Objects.Where(x => !x.Include).OrderBy(x => x.SourceKey, StringComparer.Ordinal),
                excludedRelations = mapping.Relations.Where(x => !x.Include).OrderBy(x => x.SourceKey, StringComparer.Ordinal),
                attributes = mapping.Objects.OrderBy(x => x.SourceKey, StringComparer.Ordinal).Select(x => new
                {
                    x.SourceKey, attributes = x.Attributes.OrderBy(a => a.SourceAttribute, StringComparer.Ordinal).Select(a => new
                    { a.SourceAttribute, a.Destination, a.Reason, values = a.ValueMappings?.OrderBy(v => v.Key, StringComparer.Ordinal).ToArray() })
                }),
                findings = mapping.FindingResolutions.OrderBy(x => x.Key, StringComparer.Ordinal)
            });
            manifestHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
        }
        return new(errors.Count == 0, source.Objects.Count, requirements.Count, excludedObjects, source.Relations.Count,
            traces.Count, excludedRelations, errors, requirements, traces, manifestHash);
    }

    private static Dictionary<string, T> Index<T>(IEnumerable<T> rows, Func<T, string> key, string kind, List<string> errors)
    {
        var result = new Dictionary<string, T>(StringComparer.Ordinal);
        foreach (var row in rows)
            if (string.IsNullOrWhiteSpace(key(row)) || !result.TryAdd(key(row), row)) errors.Add($"A {kind} mapping has an empty or duplicate source key.");
        return result;
    }
}
