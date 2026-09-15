using System.Text.Json;
using AeroLink.Domain.Hierarchy;
using AeroLink.Domain.Verification;

namespace AeroLink.Infrastructure.Persistence;

/// <summary>
/// A persisted setup ladder read without throwing.
///
/// The saved answers remain the creator's own text until they repair them, so every consumer that has to
/// explain a draft — the readiness verdict, the suggested review standard and the final gate — reads the
/// same interpretation here instead of re-deciding what an absent, empty, unknown or malformed profile
/// means. <see cref="ProjectLadderReading.IsDefault"/> marks the pre-populated <c>{}</c> ladder that means
/// "use the new-project default".
/// </summary>
internal sealed record ProjectSetupLadderReading(bool IsDefault, IReadOnlyList<LadderStepDraft> Steps,
    IReadOnlyList<LadderRelationshipDraft> Relationships, IReadOnlyList<LadderFinding> Findings)
{
    public static ProjectSetupLadderReading Default { get; } = new(true, [], [], []);
}

internal static class ProjectSetupLadderReader
{
    public static ProjectSetupLadderReading Read(string? ladderJson)
    {
        if (string.IsNullOrWhiteSpace(ladderJson)) return ProjectSetupLadderReading.Default;
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(ladderJson);
        }
        catch (JsonException)
        {
            return Broken("ladder_not_json", "The ladder payload is invalid JSON.");
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return Broken("ladder_not_object", "The reviewed ladder must be an object.");
            // The pre-populated empty object is the maintained "use the new-project default" marker.
            if (!root.EnumerateObject().Any()) return ProjectSetupLadderReading.Default;
            if (!root.TryGetProperty("steps", out var steps) || steps.ValueKind != JsonValueKind.Array)
                return Broken("ladder_steps_missing", "A reviewed ladder must provide typed steps.");

            var findings = new List<LadderFinding>();
            var parsedSteps = new List<LadderStepDraft>();
            var index = 0;
            foreach (var element in steps.EnumerateArray())
            {
                index += 1;
                if (element.ValueKind != JsonValueKind.Object)
                {
                    findings.Add(LadderFinding.Ladder("ladder_step_not_object",
                        "Every reviewed ladder step must be a typed object."));
                    continue;
                }
                parsedSteps.Add(ReadStep(element, index, findings));
            }

            var relationships = new List<LadderRelationshipDraft>();
            if (root.TryGetProperty("relationships", out var edges))
            {
                if (edges.ValueKind != JsonValueKind.Array)
                    findings.Add(LadderFinding.Ladder("ladder_relationships_not_array",
                        "The reviewed ladder relationships must be a typed list."));
                else
                    foreach (var edge in edges.EnumerateArray())
                    {
                        if (edge.ValueKind != JsonValueKind.Object)
                        {
                            findings.Add(LadderFinding.Ladder("ladder_relationship_not_object",
                                "Every reviewed ladder relationship must be a typed object."));
                            continue;
                        }
                        relationships.Add(new LadderRelationshipDraft(Text(edge, "parent"), Text(edge, "child")));
                    }
            }

            return new ProjectSetupLadderReading(false, parsedSteps, relationships, findings);
        }
    }

    private static LadderStepDraft ReadStep(JsonElement element, int index, List<LadderFinding> findings)
    {
        var catalogueEntry = Text(element, "catalogueEntry");
        // A step's position is authoring input, not a presentation detail. The prior typed contract read it as
        // an int, so a missing position became 0 — which the validator refused — and a null or fractional one
        // was a payload refusal. Both stayed fail-closed. This reader keeps that behaviour and explains it: a
        // position that is absent, null, fractional or unreadable is recorded as a finding and never replaced
        // with a valid-looking row index, so an incomplete draft stays editable while finalization still
        // refuses it.
        var position = index;
        var hasPosition = element.TryGetProperty("position", out var positionValue);
        if (!hasPosition || positionValue.ValueKind == JsonValueKind.Null)
        {
            findings.Add(new LadderFinding("ladder_position_missing", catalogueEntry, "position",
                $"The {catalogueEntry} ladder step does not record a position."));
            position = 0;
        }
        else if (positionValue.ValueKind != JsonValueKind.Number || !positionValue.TryGetInt32(out position))
        {
            findings.Add(new LadderFinding("ladder_position_unreadable", catalogueEntry, "position",
                $"The {catalogueEntry} ladder step position is not a whole number."));
            position = 0;
        }
        var capabilities = LevelCapabilities.None;
        if (element.TryGetProperty("capabilities", out var capabilityValue))
        {
            if (capabilityValue.ValueKind == JsonValueKind.Number && capabilityValue.TryGetInt32(out var mask))
                capabilities = (LevelCapabilities)mask;
            else if (capabilityValue.ValueKind == JsonValueKind.String
                && Enum.TryParse<LevelCapabilities>(capabilityValue.GetString(), false, out var named))
                capabilities = named;
            else
                findings.Add(new LadderFinding("capability_unreadable", catalogueEntry, "capabilities",
                    $"The {catalogueEntry} capability mask is not a supported value."));
        }

        // Absent, explicitly empty, valid and unrecognized profiles stay four different things. Only the
        // absent case may fall back to the catalogue profile, and only an array is read as a profile.
        IReadOnlyList<VerificationArtifactKind>? kinds = null;
        var unrecognized = new List<string>();
        if (element.TryGetProperty("enabledArtifactKinds", out var profile)
            && profile.ValueKind != JsonValueKind.Null)
        {
            if (profile.ValueKind != JsonValueKind.Array)
            {
                findings.Add(new LadderFinding("artifact_profile_not_array", catalogueEntry,
                    "enabledArtifactKinds",
                    $"The {catalogueEntry} verification profile must be a list of artifact kinds."));
            }
            else
            {
                var recognized = new List<VerificationArtifactKind>();
                foreach (var item in profile.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String
                        && Enum.TryParse<VerificationArtifactKind>(item.GetString(), false, out var kind)
                        && Enum.IsDefined(kind))
                        recognized.Add(kind);
                    else if (item.ValueKind == JsonValueKind.String)
                        unrecognized.Add(item.GetString() ?? string.Empty);
                    else
                        findings.Add(new LadderFinding("artifact_kind_unreadable", catalogueEntry,
                            "enabledArtifactKinds",
                            $"The {catalogueEntry} verification profile contains a value that is not an artifact kind."));
                }
                kinds = recognized;
            }
        }

        return new LadderStepDraft(catalogueEntry, position, capabilities, kinds, unrecognized);
    }

    private static string Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static ProjectSetupLadderReading Broken(string code, string message) =>
        new(false, [], [], [LadderFinding.Ladder(code, message)]);
}
