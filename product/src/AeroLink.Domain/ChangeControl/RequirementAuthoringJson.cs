using System.Globalization;
using System.Text.Json;
using AeroLink.Domain.Common;
using AeroLink.Domain.Requirements;

namespace AeroLink.Domain.ChangeControl;

/// <summary>
/// Canonical JSON contracts for the authored parts of a requirement proposal.
///
/// These values are persisted as JSON because schemas are program-defined, but that does not make them
/// untyped. Keeping the rules here gives browser creation, controlled check-in, review, and materialization
/// the same interpretation instead of letting each boundary accept a different shape.
/// </summary>
public static class RequirementAuthoringJson
{
    public const string PendingImpactDispositions =
        """{"trace":"Pending","verification":"Pending","documents":"Pending","baseline":"Pending","collaboration":"Pending"}""";
    public const string CompleteImpactDispositions =
        """{"trace":"Not Affected","verification":"Not Affected","documents":"Not Affected","baseline":"Not Affected","collaboration":"Not Affected"}""";

    public static readonly string[] ImpactKeys =
        ["trace", "verification", "documents", "baseline", "collaboration"];

    private static readonly HashSet<string> CompleteImpactValues =
        new(["Affected", "Not Affected", "Follow-up Assigned"], StringComparer.OrdinalIgnoreCase);

    public static string ValidateAndMergeAttributes(string? attributesJson, ArtifactSchemaDefinition schema,
        bool isDerived)
    {
        JsonDocument document;
        try { document = JsonDocument.Parse(string.IsNullOrWhiteSpace(attributesJson) ? "{}" : attributesJson); }
        catch (JsonException) { throw new DomainException("Requirement attributes must be a valid JSON object."); }
        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new DomainException("Requirement attributes must be a JSON object.");

            var fields = schema.Fields.ToDictionary(x => x.Key, StringComparer.OrdinalIgnoreCase);
            var values = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in document.RootElement.EnumerateObject())
            {
                var key = property.Name.Trim().ToLowerInvariant();
                if (key == "derived")
                {
                    if (values.ContainsKey(key))
                        throw new DomainException($"Attribute '{property.Name}' appears more than once.");
                    values[key] = property.Value.Clone();
                    continue;
                }
                if (!fields.TryGetValue(key, out var field))
                    throw new DomainException($"Attribute '{property.Name}' is not allowed by the {schema.Name} schema.");
                if (values.ContainsKey(key))
                    throw new DomainException($"Attribute '{property.Name}' appears more than once.");
                ValidateValue(field, property.Value);
                values[key] = property.Value.Clone();
            }

            // The request may carry this field so an older draft can round-trip, but only the server decides it.
            if (fields.TryGetValue("derived", out var derivedField))
            {
                using var derived = JsonDocument.Parse(isDerived ? "true" : "false");
                values[derivedField.Key] = derived.RootElement.Clone();
            }

            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream))
            {
                writer.WriteStartObject();
                foreach (var field in schema.Fields.OrderBy(x => x.SortOrder).ThenBy(x => x.Key))
                    if (values.TryGetValue(field.Key, out var value))
                    {
                        writer.WritePropertyName(field.Key);
                        value.WriteTo(writer);
                    }
                writer.WriteEndObject();
            }
            return System.Text.Encoding.UTF8.GetString(stream.ToArray());
        }
    }

    public static void EnsureCompleteImpactDispositions(string? impactDispositionJson, string displayNumber)
    {
        JsonDocument document;
        try { document = JsonDocument.Parse(string.IsNullOrWhiteSpace(impactDispositionJson) ? "{}" : impactDispositionJson); }
        catch (JsonException) { throw new DomainException($"{displayNumber} contains invalid impact dispositions."); }
        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new DomainException($"{displayNumber} contains invalid impact dispositions.");
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (!ImpactKeys.Contains(property.Name, StringComparer.OrdinalIgnoreCase))
                    throw new DomainException($"{displayNumber} contains unknown impact disposition '{property.Name}'.");
                if (property.Value.ValueKind != JsonValueKind.String || !values.TryAdd(property.Name, property.Value.GetString() ?? ""))
                    throw new DomainException($"{displayNumber} contains invalid impact dispositions.");
            }
            if (ImpactKeys.Any(key => !values.TryGetValue(key, out var value) || !CompleteImpactValues.Contains(value)))
                throw new DomainException($"Complete every impact disposition for {displayNumber} before review.");
        }
    }

    public static bool IsDerived(string? attributesJson)
    {
        try
        {
            using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(attributesJson) ? "{}" : attributesJson);
            return document.RootElement.ValueKind == JsonValueKind.Object &&
                   document.RootElement.TryGetProperty("derived", out var value) &&
                   value.ValueKind == JsonValueKind.True;
        }
        catch (JsonException) { return false; }
    }

    public static bool HasCompleteImpactDispositions(string? impactDispositionJson)
    {
        try
        {
            EnsureCompleteImpactDispositions(impactDispositionJson, "Requirement proposal");
            return true;
        }
        catch (DomainException) { return false; }
    }

    private static void ValidateValue(ArtifactFieldDefinition field, JsonElement value)
    {
        var valid = field.Type switch
        {
            SchemaFieldType.ShortText or SchemaFieldType.LongText or SchemaFieldType.RichText or
                SchemaFieldType.User or SchemaFieldType.ArtifactReference => value.ValueKind == JsonValueKind.String,
            SchemaFieldType.Integer => value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out _),
            SchemaFieldType.Decimal => value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out _),
            SchemaFieldType.Boolean => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
            SchemaFieldType.Date => value.ValueKind == JsonValueKind.String &&
                DateOnly.TryParse(value.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out _),
            SchemaFieldType.Enumeration => IsAllowedOption(field, value),
            _ => false,
        };
        if (!valid) throw new DomainException($"Attribute '{field.Key}' has an invalid value for {field.Type}.");
    }

    private static bool IsAllowedOption(ArtifactFieldDefinition field, JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.String) return false;
        try
        {
            var options = JsonSerializer.Deserialize<List<string>>(field.OptionsJson) ?? [];
            return options.Contains(value.GetString() ?? "", StringComparer.OrdinalIgnoreCase);
        }
        catch (JsonException)
        {
            throw new DomainException($"The schema options for attribute '{field.Key}' are invalid.");
        }
    }
}
