using System.Text.Json;

namespace AeroLink.Infrastructure.Persistence;

/// <summary>Shared interpretation of immutable CR review trace evidence for lookup preparation and display.</summary>
internal static class FrozenReviewTraceParser
{
    internal sealed record FrozenTrace(Guid UpstreamId, string Kind, Guid? SourceId, Guid? AssessmentId,
        Guid? AssessmentLinkId, string? Rationale = null, string? ActorId = null,
        DateTimeOffset? StatedAt = null, Guid? UpstreamBuildId = null, string? UpstreamBuildVersion = null,
        Guid? BuildId = null);

    internal static IReadOnlyList<Guid> UpstreamIds(string json) => ParseFrozenTrace(json).Select(x => x.UpstreamId).Distinct().ToArray();

    internal static IReadOnlyList<FrozenTrace> ParseFrozenTrace(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return [];
            var result = new List<FrozenTrace>();
            if (document.RootElement.TryGetProperty("authoredLinks", out var authored)
                && authored.ValueKind == JsonValueKind.Array)
                foreach (var link in authored.EnumerateArray())
                {
                    if (link.ValueKind != JsonValueKind.Object) continue;
                    if (TryGuid(link, "upstreamChangeRequestId", out var id)
                        || TryGuid(link, "UpstreamChangeRequestId", out id))
                    {
                        result.Add(new(id, "FrozenAuthorStatedEvidence", TryGuid(link, "id") ?? TryGuid(link, "Id"), null, null,
                            TryString(link, "rationale") ?? TryString(link, "Rationale"),
                            TryString(link, "actorId") ?? TryString(link, "ActorId"),
                            TryDate(link, "statedAt") ?? TryDate(link, "StatedAt"),
                            TryGuid(link, "upstreamBuildId") ?? TryGuid(link, "UpstreamBuildId"),
                            TryString(link, "upstreamBuildVersion") ?? TryString(link, "UpstreamBuildVersion")));
                    }
                }
            if (document.RootElement.TryGetProperty("derivedLinks", out var derived)
                && derived.ValueKind == JsonValueKind.Array)
                foreach (var link in derived.EnumerateArray())
                {
                    if (link.ValueKind != JsonValueKind.Object) continue;
                    if (TryGuid(link, "upstreamChangeRequestId", out var id)
                        || TryGuid(link, "UpstreamChangeRequestId", out id))
                    {
                        var assessmentId = TryGuid(link, "assessmentId") ?? TryGuid(link, "AssessmentId");
                        var assessmentLinkId = TryGuid(link, "assessmentLinkId") ?? TryGuid(link, "AssessmentLinkId");
                        result.Add(new(id, "FrozenReviewEvidence", assessmentLinkId ?? assessmentId,
                            assessmentId, assessmentLinkId, BuildId: TryGuid(link, "buildId") ?? TryGuid(link, "BuildId")));
                    }
                }
            return result.Distinct().OrderBy(x => x.UpstreamId).ThenBy(x => x.Kind).ToList();
        }
        catch (JsonException) { return []; }
    }

    private static Guid? TryGuid(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String
            || !Guid.TryParse(value.GetString(), out var id) || id == Guid.Empty) return null;
        return id;
    }

    private static string? TryString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;

    private static DateTimeOffset? TryDate(JsonElement element, string name) =>
        TryString(element, name) is { } value && DateTimeOffset.TryParse(value, out var parsed) ? parsed : null;

    private static bool TryGuid(JsonElement element, string name, out Guid id)
    {
        id = TryGuid(element, name) ?? Guid.Empty;
        return id != Guid.Empty;
    }
}
