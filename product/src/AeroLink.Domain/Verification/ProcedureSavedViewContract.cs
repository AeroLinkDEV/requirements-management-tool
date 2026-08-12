using System.Text.Json;
using System.Text.Json.Nodes;

namespace AeroLink.Domain.Verification;

/// <summary>
/// The stored shape of a saved test procedure view.
///
/// The verification twin of <see cref="AeroLink.Domain.Requirements.SavedViewContract"/>, and deliberately its
/// mirror in behaviour: validate and normalize at the boundary, store exactly what was accepted, and reject
/// anything the Explorer cannot actually apply. A saved view is a controlled worklist somebody else opens, so
/// it has to mean the same thing when they open it as when it was written.
///
/// It is a separate contract rather than a shared one because the two lists are not the same list. A
/// requirements view carries `verification`, `tag` and `specificationId`; a procedure view carries `outcome`
/// and the document it is written into. Accepting either set on both sides would let a view be saved against
/// one list and silently do nothing on the other — which is the failure the requirements contract exists to
/// prevent, reintroduced by sharing it.
/// </summary>
public static class ProcedureSavedViewContract
{
    public const int CurrentVersion = 1;

    /// <summary>Query fields the Explorer can actually apply. Anything else is rejected rather than kept.</summary>
    private static readonly HashSet<string> Fields = new(StringComparer.OrdinalIgnoreCase)
    { "version", "search", "state", "outcome", "documentId", "sectionId" };

    /// <summary>
    /// Procedure states as the list endpoint names them, plus the empty string for "any". Kept here rather
    /// than derived from the enum so that renaming a state is a decision about stored views, not a side effect.
    /// </summary>
    private static readonly HashSet<string> States = new(StringComparer.OrdinalIgnoreCase)
    { "", "Draft", "InReview", "Approved", "Retired" };

    /// <summary>The outcomes an execution can record, plus the empty string for "any".</summary>
    private static readonly HashSet<string> Outcomes = new(StringComparer.OrdinalIgnoreCase)
    { "", "Pass", "Fail", "Blocked" };

    private static readonly HashSet<string> Columns = new(StringComparer.OrdinalIgnoreCase)
    { "identifier", "level", "verifies", "latestResult", "state" };

    public sealed record Result(bool Valid, string Error, string QueryJson, string ColumnsJson);

    private static Result Invalid(string error) => new(false, error, "", "");

    /// <summary>
    /// Validates and normalizes a submitted contract, returning the exact JSON to store. Rejecting at the
    /// boundary is the point: a malformed view that reaches storage is read by everyone it is shared with.
    /// </summary>
    public static Result Normalize(string? queryJson, string? columnsJson)
    {
        JsonNode? query, columns;
        try { query = JsonNode.Parse(string.IsNullOrWhiteSpace(queryJson) ? "{}" : queryJson); }
        catch (JsonException) { return Invalid("The saved view query is not valid JSON."); }
        try { columns = JsonNode.Parse(string.IsNullOrWhiteSpace(columnsJson) ? "[]" : columnsJson); }
        catch (JsonException) { return Invalid("The saved view column list is not valid JSON."); }

        if (query is not JsonObject queryObject) return Invalid("The saved view query must be an object.");
        if (columns is not JsonArray columnArray) return Invalid("The saved view column list must be an array.");

        var normalized = new JsonObject { ["version"] = CurrentVersion };
        foreach (var (key, value) in queryObject)
        {
            if (string.Equals(key, "version", StringComparison.OrdinalIgnoreCase)) continue;
            if (!Fields.Contains(key)) return Invalid($"'{key}' is not a saved view query field.");
            if (value is null) continue;
            if (value is not JsonValue) return Invalid($"'{key}' must be a single value.");

            var text = value.ToString();
            if (string.Equals(key, "state", StringComparison.OrdinalIgnoreCase) && !States.Contains(text))
                return Invalid($"'{text}' is not a procedure state.");
            if (string.Equals(key, "outcome", StringComparison.OrdinalIgnoreCase) && !Outcomes.Contains(text))
                return Invalid($"'{text}' is not an execution outcome.");
            // A document or section is addressed by id, and an id that is not one would filter to nothing
            // while looking like a worklist somebody could work through.
            if ((string.Equals(key, "documentId", StringComparison.OrdinalIgnoreCase)
                 || string.Equals(key, "sectionId", StringComparison.OrdinalIgnoreCase))
                && text.Length > 0 && !Guid.TryParse(text, out _))
                return Invalid($"'{key}' must identify a document or section.");
            if (text.Length > 400) return Invalid($"'{key}' is longer than a saved view field may be.");
            normalized[key] = value.DeepClone();
        }

        var normalizedColumns = new JsonArray();
        foreach (var column in columnArray)
        {
            var name = column?.ToString() ?? "";
            if (!Columns.Contains(name)) return Invalid($"'{name}' is not a column this Explorer can show.");
            normalizedColumns.Add(name);
        }

        return new Result(true, "", normalized.ToJsonString(), normalizedColumns.ToJsonString());
    }
}
