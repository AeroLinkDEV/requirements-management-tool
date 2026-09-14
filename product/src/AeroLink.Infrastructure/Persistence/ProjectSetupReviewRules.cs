using System.Text.Json;
using System.Text.Json.Serialization;
using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Hierarchy;
using AeroLink.Domain.Identity;
using AeroLink.Domain.Requirements;
using AeroLink.Domain.Verification;

namespace AeroLink.Infrastructure.Persistence;

/// <summary>
/// The one standard review and approval definition offered by Create New Project.
///
/// The JSON is deliberately the same typed wire shape accepted for an adjusted definition. That means the
/// creator can preview the standard, make a compatible adjustment, explicitly accept the resulting concrete
/// definition, and the finalizer can materialize exactly what was accepted after a restart.
/// </summary>
public static class ProjectSetupReviewRules
{
    private static readonly JsonSerializerOptions WireJson = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public static string SuggestedJson(ProjectLadderConfiguration ladder)
    {
        ArgumentNullException.ThrowIfNull(ladder);
        return JsonSerializer.Serialize(new ReviewRulesDocument(
            ApplicableRules(ladder).Select(ToWire).ToArray()), WireJson);
    }

    /// <summary>Returns the concrete standard definition for a draft's current ladder.</summary>
    public static string SuggestedJson(string ladderJson, Guid projectId)
    {
        if (string.IsNullOrWhiteSpace(ladderJson))
            throw new InvalidOperationException("A setup ladder is required before review rules can be suggested.");

        using var document = JsonDocument.Parse(ladderJson);
        if (document.RootElement.ValueKind == JsonValueKind.Object
            && !document.RootElement.EnumerateObject().Any())
            return SuggestedJson(NewProjectLadderFactory.Create(projectId, DateTimeOffset.UtcNow));

        var steps = document.RootElement.TryGetProperty("steps", out var stepList)
            && stepList.ValueKind == JsonValueKind.Array
            ? stepList.EnumerateArray().Select(ParseStep).ToArray()
            : throw new InvalidOperationException("A reviewed ladder must provide typed steps.");

        return JsonSerializer.Serialize(new ReviewRulesDocument(
            ApplicableRules(steps).Select(ToWire).ToArray()), WireJson);
    }

    public static bool IsEmptyDefinition(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.ValueKind == JsonValueKind.Object
            && !document.RootElement.EnumerateObject().Any();
    }

    private static IReadOnlyList<Rule> ApplicableRules(ProjectLadderConfiguration ladder)
    {
        var steps = ladder.Steps.Select(x => new Step(
            Enum.Parse<RequirementLevel>(x.CatalogueEntry, false), x.Capabilities, x.EnabledArtifactKinds.ToHashSet())).ToArray();
        return ApplicableRules(steps);
    }

    public static HashSet<ReviewSubject> ApplicableSubjects(ProjectLadderConfiguration ladder) =>
        ApplicableRules(ladder).Select(x => x.Subject).ToHashSet();

    private static IReadOnlyList<Rule> ApplicableRules(IReadOnlyList<Step> steps)
    {
        var levels = steps.Where(x => x.Capabilities.HasFlag(LevelCapabilities.HasChangeControl))
            .Select(x => x.Level).ToHashSet();
        var rules = new List<Rule>();

        if (levels.Contains(RequirementLevel.System))
            rules.Add(new(ReviewSubject.System, "System requirements", ProgramRole.SystemEngineer,
                ReviewStageAuthorityKind.BaseRole));
        if (levels.Contains(RequirementLevel.HighLevel) || levels.Contains(RequirementLevel.LowLevel))
            rules.Add(new(ReviewSubject.Software, "Software requirements", ProgramRole.SoftwareEngineer,
                ReviewStageAuthorityKind.BaseRole));
        if (levels.Contains(RequirementLevel.Interface))
            rules.Add(new(ReviewSubject.Interface, "Interface requirements", ProgramRole.ConfigurationManager,
                ReviewStageAuthorityKind.BaseRole));
        if (steps.Any(x => x.Level == RequirementLevel.System && x.Capabilities.HasFlag(LevelCapabilities.HasVerification)
            && x.Artifacts.Contains(VerificationArtifactKind.Procedure)))
            rules.Add(new(ReviewSubject.SystemTest, "System test procedures", ProgramRole.SystemTestEngineer,
                ReviewStageAuthorityKind.BaseRole));

        AddSoftwareArtifactRules(rules, steps, RequirementLevel.HighLevel,
            ReviewSubject.HighLevelSoftwareCase, ReviewSubject.HighLevelSoftwareProcedure, "High-level software");
        AddSoftwareArtifactRules(rules, steps, RequirementLevel.LowLevel,
            ReviewSubject.LowLevelSoftwareCase, ReviewSubject.LowLevelSoftwareProcedure, "Low-level software");
        return rules;
    }

    private static void AddSoftwareArtifactRules(List<Rule> rules, IReadOnlyList<Step> steps,
        RequirementLevel level, ReviewSubject caseSubject, ReviewSubject procedureSubject, string label)
    {
        var step = steps.FirstOrDefault(x => x.Level == level);
        if (step is null || !step.Capabilities.HasFlag(LevelCapabilities.HasVerification)) return;
        if (step.Artifacts.Contains(VerificationArtifactKind.Case))
            rules.Add(new(caseSubject, $"{label} test cases", ProgramRole.SoftwareTestEngineer,
                ReviewStageAuthorityKind.BaseRole));
        if (step.Artifacts.Contains(VerificationArtifactKind.Procedure))
            rules.Add(new(procedureSubject, $"{label} test procedures", ProgramRole.SoftwareTestEngineer,
                ReviewStageAuthorityKind.BaseRole));
    }

    private static RuleWire ToWire(Rule rule) => new(rule.Subject, rule.Name,
        [
            new("Engineering review", rule.Role, ReviewStageKind.Review, rule.AuthorityKind),
            new("Project acceptance", ProgramRole.ProjectEngineer, ReviewStageKind.Approval,
                ReviewStageAuthorityKind.LeadershipPosition),
        ]);

    private static Step ParseStep(JsonElement element)
    {
        if (!element.TryGetProperty("catalogueEntry", out var catalogue)
            || catalogue.ValueKind != JsonValueKind.String
            || !Enum.TryParse<RequirementLevel>(catalogue.GetString(), false, out var level)
            || !Enum.IsDefined(level))
            throw new InvalidOperationException("A reviewed ladder contains an unsupported catalogue entry.");

        var artifacts = new HashSet<VerificationArtifactKind>();
        if (element.TryGetProperty("enabledArtifactKinds", out var values)
            && values.ValueKind == JsonValueKind.Array)
        {
            foreach (var value in values.EnumerateArray())
                if (value.ValueKind == JsonValueKind.String
                    && Enum.TryParse<VerificationArtifactKind>(value.GetString(), false, out var kind)
                    && Enum.IsDefined(kind))
                    artifacts.Add(kind);
        }
        var capabilities = element.TryGetProperty("capabilities", out var capabilityValue)
            ? JsonSerializer.Deserialize<LevelCapabilities>(capabilityValue.GetRawText(), WireJson)
            : LevelCapabilities.None;
        return new(level, capabilities, artifacts);
    }

    private sealed record Step(RequirementLevel Level, LevelCapabilities Capabilities,
        IReadOnlySet<VerificationArtifactKind> Artifacts);
    private sealed record Rule(ReviewSubject Subject, string Name, ProgramRole Role,
        ReviewStageAuthorityKind AuthorityKind);
    private sealed record ReviewRulesDocument(IReadOnlyList<RuleWire> Rules);
    private sealed record RuleWire(ReviewSubject Subject, string Name, IReadOnlyList<StageWire> Stages);
    private sealed record StageWire(string Name, ProgramRole RequiredRole, ReviewStageKind Kind,
        ReviewStageAuthorityKind AuthorityKind);
}
