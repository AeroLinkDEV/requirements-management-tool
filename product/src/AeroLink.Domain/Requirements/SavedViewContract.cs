using System.Text.Json;
using System.Text.Json.Nodes;

namespace AeroLink.Domain.Requirements;

/// <summary>
/// The stored shape of a saved requirements view.
///
/// `QueryJson` and `ColumnsJson` were persisted exactly as the browser sent them, so a field the workspace
/// does not understand, a sort mode it cannot apply, or a column that does not exist could all be written and
/// then read back by every future reader. A saved view is a controlled worklist somebody else opens; it has
/// to mean the same thing when they open it as when it was written.
///
/// The version lives inside the contract rather than beside it, because it describes the document and not the
/// row. Content written before this existed carries no version and is read as version 1, which is what it is.
/// </summary>
public static class SavedViewContract
{
    public const int CurrentVersion = 1;

    /// <summary>Query fields the workspace can actually apply. Anything else is rejected rather than kept.</summary>
    private static readonly HashSet<string> Fields = new(StringComparer.OrdinalIgnoreCase)
    { "version", "search", "level", "verification", "tag", "state", "owner", "sourceScr", "openComments", "coverageState", "sort", "specificationId" };

    private static readonly HashSet<string> Sorts = new(StringComparer.OrdinalIgnoreCase)
    { "identifier", "updated", "verification", "state" };

    private static readonly HashSet<string> Levels = new(StringComparer.OrdinalIgnoreCase)
    { "", "System", "Software", "HighLevel", "LowLevel" };

    private static readonly HashSet<string> CoverageStates = new(StringComparer.OrdinalIgnoreCase)
    { "", "covered", "suspect", "uncovered" };

    private static readonly HashSet<string> Columns = new(StringComparer.OrdinalIgnoreCase)
    { "identifier", "statement", "level", "verification", "state", "comments", "coverage" };

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
            if (string.Equals(key, "sort", StringComparison.OrdinalIgnoreCase) && text.Length > 0 && !Sorts.Contains(text))
                return Invalid($"'{text}' is not a sort this workspace can apply.");
            if (string.Equals(key, "level", StringComparison.OrdinalIgnoreCase) && !Levels.Contains(text))
                return Invalid($"'{text}' is not a requirement level.");
            if (string.Equals(key, "coverageState", StringComparison.OrdinalIgnoreCase) && !CoverageStates.Contains(text))
                return Invalid($"'{text}' is not a coverage state.");
            if (text.Length > 400) return Invalid($"'{key}' is longer than a saved view field may be.");
            normalized[key] = value.DeepClone();
        }

        var normalizedColumns = new JsonArray();
        foreach (var column in columnArray)
        {
            var name = column?.ToString() ?? "";
            if (!Columns.Contains(name)) return Invalid($"'{name}' is not a column this workspace can show.");
            normalizedColumns.Add(name);
        }

        return new Result(true, "", normalized.ToJsonString(), normalizedColumns.ToJsonString());
    }
}
