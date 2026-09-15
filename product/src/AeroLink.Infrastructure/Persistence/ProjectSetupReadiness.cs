using System.Security.Cryptography;
using System.Text;
using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Hierarchy;
using AeroLink.Domain.Programs;
using AeroLink.Domain.Verification;

namespace AeroLink.Infrastructure.Persistence;

public sealed record ProjectSetupReadinessStep(string Level, int Capabilities, IReadOnlyList<string>? Stored,
    IReadOnlyList<string> Effective, string ProfileSource);

public sealed record ProjectSetupReadinessReview(IReadOnlyList<string> ApplicableSubjects,
    IReadOnlyList<string> AcceptedSubjects, IReadOnlyList<string> MissingSubjects,
    IReadOnlyList<string> UnexpectedSubjects, IReadOnlyList<string> DuplicateSubjects, bool Accepted,
    bool DefinitionConcrete, bool AcceptanceMatchesConfiguration, bool Covers);

/// <summary>
/// The authoritative, side-effect-free verdict for one saved setup configuration.
///
/// Its scope is deliberately narrow: it answers whether the persisted ladder/profile and the persisted
/// review definition are mutually compatible and accepted. It is not a project-readiness claim on its own
/// — administrator authority, unsaved local edits, source reconciliation, the source assertion/signature
/// and the final transactional gate all remain separate facts the caller must still establish.
/// </summary>
public sealed record ProjectSetupReadinessView(Guid DraftId, long Version, string EvaluatedConfigurationHash,
    bool LadderValid, IReadOnlyList<ProjectSetupReadinessStep> Steps, IReadOnlyList<LadderFinding> Findings,
    ProjectSetupReadinessReview Review, bool ConfigurationReady);

public static class ProjectSetupReadiness
{
    public static ProjectSetupReadinessView Evaluate(ProjectSetupDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        var reading = ProjectSetupLadderReader.Read(draft.LadderJson);
        var (steps, relationships) = reading.IsDefault
            ? DefaultLadder(draft.ProjectId)
            : (reading.Steps, reading.Relationships);

        var findings = new List<LadderFinding>(reading.Findings);
        findings.AddRange(ProjectLadderDraftValidator.Inspect(steps, relationships, LegacyLadderPolicy.Instance));
        var ladderValid = findings.Count == 0;

        var definition = ProjectSetupReviewRules.InspectDefinition(draft.ReviewRulesJson);
        var applicable = ProjectSetupReviewRules.ApplicableSubjects(steps, LegacyLadderPolicy.Instance)
            .Select(x => x.ToString()).ToArray();
        var accepted = ProjectSetupReviewRules.SubjectsOf(draft.ReviewRulesJson);
        var duplicates = accepted.GroupBy(x => x, StringComparer.Ordinal).Where(x => x.Count() > 1)
            .Select(x => x.Key).ToArray();
        var missing = applicable.Where(x => !accepted.Contains(x)).ToArray();
        var unexpected = accepted.Where(x => !applicable.Contains(x)).ToArray();
        var covers = definition.HasRulesArray
            && duplicates.Length == 0 && missing.Length == 0 && unexpected.Length == 0
            && (applicable.Length > 0 || accepted.Count == 0);

        return new ProjectSetupReadinessView(draft.Id, draft.Version, ConfigurationHash(draft.LadderJson, draft.ReviewRulesJson),
            ladderValid, Steps(steps), findings,
            new ProjectSetupReadinessReview(
                applicable.OrderBy(x => x, StringComparer.Ordinal).ToArray(),
                accepted.OrderBy(x => x, StringComparer.Ordinal).ToArray(),
                missing.OrderBy(x => x, StringComparer.Ordinal).ToArray(),
                unexpected.OrderBy(x => x, StringComparer.Ordinal).ToArray(),
                duplicates.OrderBy(x => x, StringComparer.Ordinal).ToArray(),
                draft.ReviewRulesAccepted, definition.HasRulesArray,
                AcceptanceMatches(draft), covers),
            ladderValid && covers && draft.ReviewRulesAccepted && definition.HasRulesArray
                && AcceptanceMatches(draft));
    }

    /// <summary>Mirrors the persisted ladder the creation factory writes, and nothing more.</summary>
    private static (IReadOnlyList<LadderStepDraft> Steps, IReadOnlyList<LadderRelationshipDraft> Relationships)
        DefaultLadder(Guid projectId)
    {
        var configuration = NewProjectLadderFactory.Create(projectId, DateTimeOffset.UnixEpoch);
        var steps = configuration.Steps.OrderBy(x => x.Position)
            .Select(x => new LadderStepDraft(x.CatalogueEntry, x.Position, x.Capabilities,
                x.EnabledArtifactKinds.ToArray()))
            .ToArray();
        var byName = configuration.Steps.ToDictionary(x => x.CatalogueEntry, x => x, StringComparer.Ordinal);
        var relationships = configuration.AllowedUpstream
            .Select(edge => new LadderRelationshipDraft(
                byName.Single(x => x.Value.Id == edge.ParentStepId).Key,
                byName.Single(x => x.Value.Id == edge.ChildStepId).Key))
            .ToArray();
        return (steps, relationships);
    }

    private static IReadOnlyList<ProjectSetupReadinessStep> Steps(IReadOnlyList<LadderStepDraft> steps) => steps
        .OrderBy(x => x.Position)
        .Select(step =>
        {
            var definition = Enum.TryParse<RequirementLevel>(step.CatalogueEntry, false, out var level)
                && Enum.IsDefined(level)
                    ? LegacyLadderPolicy.Instance.Definition(level)
                    : null;
            var source = step.EnabledArtifactKinds is null
                ? "catalogue-fallback"
                : "explicit";
            return new ProjectSetupReadinessStep(step.CatalogueEntry, (int)step.Capabilities,
                step.EnabledArtifactKinds?.Select(x => x.ToString()).ToArray(),
                definition is null ? [] : step.EffectiveKinds(definition).Select(x => x.ToString()).ToArray(),
                source);
        })
        .ToArray();

    private static bool AcceptanceMatches(ProjectSetupDraft draft) => draft.ReviewRulesAccepted
        && string.Equals(draft.ReviewRulesAcceptanceHash, AcceptanceHash(draft.LadderJson, draft.ReviewRulesJson),
            StringComparison.Ordinal);

    private static string AcceptanceHash(string ladderJson, string reviewRulesJson) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{ladderJson}\n{reviewRulesJson}")))
            .ToLowerInvariant();

    private static string ConfigurationHash(string ladderJson, string reviewRulesJson) =>
        AcceptanceHash(ladderJson, reviewRulesJson);
}

/// <summary>What the persisted review definition actually contains, read without throwing.</summary>
public sealed record ReviewDefinitionReading(bool HasRulesArray, IReadOnlyList<string> Subjects);
