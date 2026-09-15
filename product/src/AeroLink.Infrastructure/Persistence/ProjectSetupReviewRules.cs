using System.Text.Json;
using System.Text.Json.Serialization;
using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Common;
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

        // The standard must describe the ladder the finalizer will actually validate. Reading the same way
        // both do — including the capability-dependent fallback for an absent profile — is what keeps the
        // offered subjects equal to the applicable subjects.
        var reading = ProjectSetupLadderReader.Read(ladderJson);
        if (reading.Findings.Count > 0) throw new InvalidOperationException(reading.Findings[0].Message);
        if (reading.IsDefault) return SuggestedJson(NewProjectLadderFactory.Create(projectId, DateTimeOffset.UtcNow));
        var steps = reading.Steps;
        return JsonSerializer.Serialize(new ReviewRulesDocument(
            ApplicableRules(steps, LegacyLadderPolicy.Instance).Select(ToWire).ToArray()), WireJson);
    }

    /// <summary>Reads the persisted definition's shape and named subjects without rejecting its content.</summary>
    public static ReviewDefinitionReading InspectDefinition(string rulesJson)
    {
        try
        {
            using var document = JsonDocument.Parse(rulesJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("rules", out var rules)
                || rules.ValueKind != JsonValueKind.Array)
                return new ReviewDefinitionReading(false, []);
            var subjects = rules.EnumerateArray()
                .Select(rule => rule.ValueKind == JsonValueKind.Object
                    && rule.TryGetProperty("subject", out var subject)
                    && subject.ValueKind == JsonValueKind.String
                        ? subject.GetString() ?? string.Empty
                        : string.Empty)
                .Where(x => x.Length > 0)
                .ToArray();
            return new ReviewDefinitionReading(true, subjects, rules.GetArrayLength());
        }
        catch (JsonException)
        {
            return new ReviewDefinitionReading(false, []);
        }
    }

    public static IReadOnlyList<string> SubjectsOf(string rulesJson) => InspectDefinition(rulesJson).Subjects;

    /// <summary>
    /// The semantic validity of one concrete review definition, read without throwing.
    ///
    /// This is the same authority materializing the workflows uses: every rule names a supported subject,
    /// carries a name the workflow accepts, and has at least one named Review stage and one named Approval
    /// stage whose required authority is a configurable base project role or an accountable Project
    /// Leadership position. <see cref="ProjectSetupService"/> refuses finalization on exactly these findings,
    /// so a readiness verdict can never be more permissive than the final gate — the failure this method
    /// exists to prevent is a green "ready" screen that leads to a predictable rejection.
    /// </summary>
    public static IReadOnlyList<LadderFinding> InspectRules(string? rulesJson)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(rulesJson ?? string.Empty);
        }
        catch (JsonException)
        {
            return [LadderFinding.Review("review_rules_not_json", null, null,
                "The review rules payload is invalid JSON.")];
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return [LadderFinding.Review("review_rules_not_object", null, null,
                    "Review rules must be a typed object.")];
            if (!root.EnumerateObject().Any())
                return [LadderFinding.Review("review_rules_empty", null, null,
                    "Review and approval rules must retain a concrete accepted definition.")];
            if (!root.TryGetProperty("rules", out var rules) || rules.ValueKind != JsonValueKind.Array)
                return [LadderFinding.Review("review_rules_not_typed", null, null,
                    "Review rules must contain a typed 'rules' array or be empty for the standard.")];

            var findings = new List<LadderFinding>();
            var position = 0;
            foreach (var rule in rules.EnumerateArray())
            {
                position += 1;
                if (rule.ValueKind != JsonValueKind.Object)
                {
                    findings.Add(LadderFinding.Review("review_rule_not_object", null, null,
                        $"Review rule {position} must be a typed object."));
                    continue;
                }

                var subjectText = Text(rule, "subject");
                if (subjectText is null
                    || !Enum.TryParse<ReviewSubject>(subjectText, true, out var subject)
                    || !Enum.IsDefined(subject))
                {
                    findings.Add(LadderFinding.Review("review_rule_subject_unknown", null, null,
                        $"Review rule {position} names no supported review subject.", Bound(subjectText)));
                    continue;
                }
                var label = subject.ToString();

                // A null name falls back to the subject name when the workflow is created; an explicitly
                // blank one reaches the workflow authority and is refused there.
                if (rule.TryGetProperty("name", out var nameValue) && nameValue.ValueKind == JsonValueKind.String
                    && string.IsNullOrWhiteSpace(nameValue.GetString()))
                    findings.Add(LadderFinding.Review("review_rule_unnamed", label, null,
                        "A review workflow needs a name."));

                if (!rule.TryGetProperty("stages", out var stages) || stages.ValueKind != JsonValueKind.Array)
                {
                    findings.Add(LadderFinding.Review("review_rule_stage_missing", label, null,
                        $"Review rule {label} requires a stage."));
                    continue;
                }
                if (stages.GetArrayLength() == 0)
                {
                    findings.Add(LadderFinding.Review("review_rule_stage_missing", label, null,
                        $"Review rule {label} requires a stage."));
                    continue;
                }

                var hasReview = false;
                var hasApproval = false;
                var stagePosition = 0;
                foreach (var stage in stages.EnumerateArray())
                {
                    stagePosition += 1;
                    if (stage.ValueKind != JsonValueKind.Object)
                    {
                        findings.Add(LadderFinding.Review("review_stage_not_object", label, stagePosition,
                            $"Every stage of review rule {label} must be a typed object."));
                        continue;
                    }

                    if (string.IsNullOrWhiteSpace(Text(stage, "name")))
                        findings.Add(LadderFinding.Review("review_stage_unnamed", label, stagePosition,
                            "A review stage needs a name."));

                    var kindText = Text(stage, "kind");
                    ReviewStageKind? kind = Enum.TryParse<ReviewStageKind>(kindText ?? string.Empty, true,
                        out var parsedKind) && Enum.IsDefined(parsedKind)
                            ? parsedKind
                            : null;
                    if (kind is null)
                        findings.Add(LadderFinding.Review("review_stage_kind_invalid", label, stagePosition,
                            $"'{Bound(kindText)}' is not a supported signature meaning for review rule {label}."));
                    else if (kind == ReviewStageKind.Review) hasReview = true;
                    else hasApproval = true;

                    var roleText = Text(stage, "requiredRole");
                    var roleKnown = Enum.TryParse<ProgramRole>(roleText ?? string.Empty, true, out var role)
                        && Enum.IsDefined(role);
                    if (!roleKnown)
                    {
                        findings.Add(LadderFinding.Review("review_stage_authority_invalid", label, stagePosition,
                            $"'{Bound(roleText)}' is not a supported project authority for review rule {label}."));
                        continue;
                    }

                    ReviewStageAuthorityKind? authorityKind = null;
                    var authorityReadable = true;
                    if (stage.TryGetProperty("authorityKind", out var authorityValue)
                        && authorityValue.ValueKind != JsonValueKind.Null)
                    {
                        if (authorityValue.ValueKind == JsonValueKind.String
                            && Enum.TryParse<ReviewStageAuthorityKind>(authorityValue.GetString(), true,
                                out var parsedAuthority)
                            && Enum.IsDefined(parsedAuthority))
                            authorityKind = parsedAuthority;
                        else
                            authorityReadable = false;
                    }
                    if (!authorityReadable)
                    {
                        findings.Add(LadderFinding.Review("review_stage_authority_invalid", label, stagePosition,
                            $"The authority demanded by stage {stagePosition} of review rule {label} is not "
                            + "BaseRole or LeadershipPosition."));
                        continue;
                    }

                    try
                    {
                        // The maintained authority rule itself — a base role must be a configurable role and
                        // a leadership demand must name one of the accountable positions.
                        ReviewWorkflowStage.ValidateAuthority(role, authorityKind);
                    }
                    catch (DomainException ex)
                    {
                        findings.Add(LadderFinding.Review("review_stage_authority_invalid", label, stagePosition,
                            ex.Message));
                    }
                }

                if (!hasReview || !hasApproval)
                    findings.Add(LadderFinding.Review("review_rule_missing_signature_kind", label, null,
                        $"Review rule {label} requires explicit Review and Approval stages."));
            }
            return findings;
        }
    }

    public static bool IsEmptyDefinition(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.ValueKind == JsonValueKind.Object
            && !document.RootElement.EnumerateObject().Any();
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string Bound(string? value) =>
        value is null ? string.Empty : ProjectLadderDraftValidator.DiagnosticToken(value);

    private static IReadOnlyList<Rule> ApplicableRules(ProjectLadderConfiguration ladder) => ApplicableRules(
        ladder.Steps.Select(x => new LadderStepDraft(x.CatalogueEntry, x.Position, x.Capabilities,
            x.EnabledArtifactKinds.ToArray())).ToArray(), LegacyLadderPolicy.Instance);

    public static HashSet<ReviewSubject> ApplicableSubjects(ProjectLadderConfiguration ladder) =>
        ApplicableRules(ladder).Select(x => x.Subject).ToHashSet();

    /// <summary>
    /// The subjects a supplied ladder makes applicable, applying the maintained fallback for an absent
    /// profile so the offered standard and the finalizer agree for the same saved input.
    /// </summary>
    public static IReadOnlyList<ReviewSubject> ApplicableSubjects(IReadOnlyList<LadderStepDraft> steps,
        ILadderPolicy policy) => ApplicableRules(steps, policy).Select(x => x.Subject).ToArray();

    private static IReadOnlyList<Rule> ApplicableRules(IReadOnlyList<LadderStepDraft> steps, ILadderPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(steps);
        ArgumentNullException.ThrowIfNull(policy);
        var effective = steps
            .Where(x => Enum.TryParse<RequirementLevel>(x.CatalogueEntry, false, out var level) && Enum.IsDefined(level))
            .Select(x =>
            {
                var level = Enum.Parse<RequirementLevel>(x.CatalogueEntry, false);
                return new Step(level, x.Capabilities,
                    x.EffectiveKinds(policy.Definition(level)).ToHashSet());
            })
            .ToArray();
        return ApplicableRules(effective);
    }

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

    private sealed record Step(RequirementLevel Level, LevelCapabilities Capabilities,
        IReadOnlySet<VerificationArtifactKind> Artifacts);
    private sealed record Rule(ReviewSubject Subject, string Name, ProgramRole Role,
        ReviewStageAuthorityKind AuthorityKind);
    private sealed record ReviewRulesDocument(IReadOnlyList<RuleWire> Rules);
    private sealed record RuleWire(ReviewSubject Subject, string Name, IReadOnlyList<StageWire> Stages);
    private sealed record StageWire(string Name, ProgramRole RequiredRole, ReviewStageKind Kind,
        ReviewStageAuthorityKind AuthorityKind);
}
