using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;

namespace AeroLink.Infrastructure.Persistence;

/// <summary>Source observations only. These records confer no reconciliation, approval or import authority.</summary>
public sealed record ProjectCreationSourceObject(string Key, string Module, string SourceIdentifier,
    string Kind, IReadOnlyDictionary<string, string> Attributes,
    IReadOnlyDictionary<string, string>? AttributeNames = null);
public sealed record ProjectCreationSourceRelation(string Key, string SourceKey, string TargetKey,
    string Type, IReadOnlyDictionary<string, string> Attributes,
    IReadOnlyDictionary<string, string>? AttributeNames = null);
public sealed record ProjectCreationSourceAnalysis(string Format, string Sha256, long Size,
    string SourceTool, IReadOnlyList<ProjectCreationSourceObject> Objects,
    IReadOnlyList<ProjectCreationSourceRelation> Relations, IReadOnlyList<string> Findings);

/// <summary>
/// Analyses inception uploads without inventing destination identifiers, levels, or source assertions.
/// The setup service must separately map every observed attribute/object and validate dependencies.
/// </summary>
public static class ProjectCreationSourceParser
{
    private const int MaxBytes = 50 * 1024 * 1024;
    private const int MaxObjects = 50_000;

    public static ProjectCreationSourceAnalysis Analyse(Stream source, string fileName)
    {
        using var input = new MemoryStream();
        var buffer = new byte[81920];
        int read;
        while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
        {
            if (input.Length + read > MaxBytes) throw new InvalidOperationException("Source files are limited to 50 MB.");
            input.Write(buffer, 0, read);
        }
        if (input.Length == 0) throw new InvalidOperationException("The source file is empty.");
        var hash = Convert.ToHexString(SHA256.HashData(input.GetBuffer().AsSpan(0, (int)input.Length))).ToLowerInvariant();
        input.Position = 0;
        var extension = Path.GetExtension(fileName).ToLowerInvariant();
        var result = extension switch
        {
            ".csv" => AnalyseTables("CSV", InceptionTableReader.Csv(new UTF8Encoding(false, true).GetString(input.ToArray()))),
            ".xlsx" => AnalyseTables("XLSX", InceptionTableReader.Xlsx(input)),
            ".reqif" => AnalyseReqIf(input),
            ".reqifz" => AnalyseReqIfArchive(input),
            _ => throw new InvalidOperationException("Select ReqIF (.reqif or .reqifz), CSV (.csv), or Excel (.xlsx) source material.")
        };
        if (result.Objects.Count > MaxObjects) throw new InvalidOperationException("Source files are limited to 50,000 objects.");
        return result with { Sha256 = hash, Size = input.Length };
    }

    private static ProjectCreationSourceAnalysis AnalyseTables(string format, IReadOnlyList<InceptionSourceTable> tables)
    {
        var objects = new List<ProjectCreationSourceObject>();
        var findings = new List<string>();
        foreach (var table in tables)
        {
            if (table.Rows.Count == 0) { findings.Add($"Module '{table.Name}' is empty."); continue; }
            var headers = table.Rows[0].Select(x => x.Trim().TrimStart('\uFEFF')).ToArray();
            if (headers.Any(string.IsNullOrWhiteSpace) || headers.Distinct(StringComparer.OrdinalIgnoreCase).Count() != headers.Length)
                throw new InvalidOperationException($"Module '{table.Name}' needs nonempty, distinct column names.");
            for (var index = 1; index < table.Rows.Count; index++)
            {
                var row = table.Rows[index];
                if (row.All(string.IsNullOrWhiteSpace)) continue;
                if (row.Length > headers.Length && row.Skip(headers.Length).Any(x => !string.IsNullOrWhiteSpace(x)))
                    throw new InvalidOperationException($"Module '{table.Name}', row {index + 1} contains values without column names.");
                var attributes = headers.Select((header, column) => (header, value: column < row.Length ? row[column] : ""))
                    .ToDictionary(x => x.header, x => x.value, StringComparer.Ordinal);
                // A row key identifies the observation. A missing foreign identifier remains missing until mapping.
                var sourceIdentifier = Pick(attributes, "Identifier", "ID", "ForeignID", "ReqIF.ForeignID");
                objects.Add(new($"{table.Key}:row:{index + 1}", table.Name, sourceIdentifier, "Requirement", attributes));
                if (objects.Count > MaxObjects) throw new InvalidOperationException("Source files are limited to 50,000 objects.");
            }
            findings.AddRange(table.Findings);
        }
        return new(format, "", 0, "", objects, [], findings);
    }

    private static ProjectCreationSourceAnalysis AnalyseReqIf(Stream stream)
    {
        // Reuse the exchange parser's closed XML boundary; inception retains raw attributes and levels.
        return AnalyseReqIf(ReqIfExchangeService.ReadSourceXml(stream));
    }

    private static ProjectCreationSourceAnalysis AnalyseReqIfArchive(Stream stream)
    {
        using var archive = new InceptionArchiveReader(stream);
        var sources = archive.Entries.Where(x => x.FullName.EndsWith(".reqif", StringComparison.OrdinalIgnoreCase)).ToList();
        if (sources.Count != 1) throw new InvalidOperationException("A ReqIF package must contain exactly one source .reqif document.");
        var result = AnalyseReqIf(archive.ReadXml(sources[0]));
        var extras = archive.Entries.Where(x => x != sources[0] && !x.FullName.EndsWith('/'));
        return result with { Findings = result.Findings.Concat(extras.Select(x => $"Package part '{x.FullName}' requires explicit exclusion; this profile imports requirements and mapped relations only.")).ToArray() };
    }

    private static ProjectCreationSourceAnalysis AnalyseReqIf(XDocument document)
    {
        if (document.Root?.Name.LocalName != "REQ-IF") throw new InvalidOperationException("The document root is not REQ-IF.");
        var objects = new List<ProjectCreationSourceObject>();
        var relations = new List<ProjectCreationSourceRelation>();
        var objectKeys = new HashSet<string>(StringComparer.Ordinal);
        var relationKeys = new HashSet<string>(StringComparer.Ordinal);
        var findings = new List<string>();
        var definitions = document.Descendants().Where(x => x.Name.LocalName.StartsWith("ATTRIBUTE-DEFINITION-", StringComparison.Ordinal))
            .Where(x => x.Attribute("IDENTIFIER") is not null).ToList();
        var duplicateDefinitions = definitions.GroupBy(x => Attribute(x, "IDENTIFIER")).FirstOrDefault(x => x.Count() > 1);
        if (duplicateDefinitions is not null) throw new InvalidOperationException($"Duplicate ReqIF attribute definition '{duplicateDefinitions.Key}'.");
        var definitionNames = definitions.ToDictionary(x => "attribute:" + Attribute(x, "IDENTIFIER"), x => Attribute(x, "LONG-NAME"));
        var modules = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var specification in document.Descendants().Where(x => x.Name.LocalName == "SPECIFICATION"))
        {
            var module = Attribute(specification, "LONG-NAME");
            if (module.Length == 0) module = Attribute(specification, "IDENTIFIER");
            foreach (var reference in specification.Descendants().Where(x => x.Name.LocalName == "SPEC-OBJECT-REF"))
            {
                if (!modules.TryGetValue(reference.Value.Trim(), out var names)) modules[reference.Value.Trim()] = names = [];
                if (!names.Contains(module)) names.Add(module);
            }
        }
        foreach (var item in document.Descendants().Where(x => x.Name.LocalName == "SPEC-OBJECT"))
        {
            var key = Attribute(item, "IDENTIFIER");
            if (key.Length == 0 || !objectKeys.Add(key)) throw new InvalidOperationException("ReqIF objects require unique source identifiers.");
            var values = Values(item);
            var sourceIdentifier = PickNamed(values, definitionNames, "AeroLink.Identifier", "Identifier", "ReqIF.ForeignID", "ID");
            if (sourceIdentifier.Length == 0) sourceIdentifier = key;
            var type = item.Descendants().FirstOrDefault(x => x.Name.LocalName == "SPEC-OBJECT-TYPE-REF")?.Value.Trim() ?? "";
            var kind = PickNamed(values, definitionNames, "AeroLink.Kind");
            if (kind.Length == 0) kind = type == "SOT-SECTION" ? "Section" : "Unmapped";
            var names = modules.GetValueOrDefault(key, []);
            var attributes = new Dictionary<string, string>(values, StringComparer.Ordinal);
            if (!attributes.TryAdd("Source.ObjectType", type)
                || !attributes.TryAdd("Source.LastChange", Attribute(item, "LAST-CHANGE"))
                || !attributes.TryAdd("Source.LongName", Attribute(item, "LONG-NAME"))
                || !attributes.TryAdd("Source.Modules", System.Text.Json.JsonSerializer.Serialize(names)))
                throw new InvalidOperationException("A ReqIF attribute conflicts with a reserved source-metadata name.");
            objects.Add(new(key, names.FirstOrDefault() ?? "", sourceIdentifier, kind, attributes,
                values.Keys.ToDictionary(x => x, x => definitionNames.GetValueOrDefault(x, x))));
            if (objects.Count > MaxObjects) throw new InvalidOperationException("Source files are limited to 50,000 objects.");
        }
        foreach (var relation in document.Descendants().Where(x => x.Name.LocalName == "SPEC-RELATION"))
        {
            var key = Attribute(relation, "IDENTIFIER");
            if (key.Length == 0 || !relationKeys.Add(key)) throw new InvalidOperationException("ReqIF relations require unique source identifiers.");
            string Reference(string parent) => relation.Elements().FirstOrDefault(x => x.Name.LocalName == parent)?.Value.Trim() ?? "";
            var values = Values(relation);
            relations.Add(new(key, Reference("SOURCE"), Reference("TARGET"), Reference("TYPE"), values,
                values.Keys.ToDictionary(x => x, x => definitionNames.GetValueOrDefault(x, x))));
            if (relations.Count > MaxObjects) throw new InvalidOperationException("Source files are limited to 50,000 relations.");
        }
        foreach (var relation in relations.Where(x => !objectKeys.Contains(x.SourceKey) || !objectKeys.Contains(x.TargetKey)))
            findings.Add($"Relation '{relation.Key}' has an endpoint outside the observed source objects; resolve it during reconciliation.");
        if (document.Descendants().Any(x => x.Name.LocalName == "object")) findings.Add("Embedded attachment references require explicit exclusion; attachment materialization is not supported by this source profile.");
        var tool = document.Descendants().FirstOrDefault(x => x.Name.LocalName == "SOURCE-TOOL-ID")?.Value ?? "";
        return new("ReqIF", "", 0, tool, objects, relations, findings);
    }

    private static Dictionary<string, string> Values(XElement item)
    {
        var output = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var value in item.Elements().Where(x => x.Name.LocalName == "VALUES").Elements())
        {
            var definition = value.Elements().FirstOrDefault(x => x.Name.LocalName == "DEFINITION")?.Value.Trim() ?? "";
            var name = "attribute:" + definition;
            if (definition.Length == 0 || output.ContainsKey(name)) throw new InvalidOperationException("ReqIF values need distinct attributable attribute definitions.");
            // Enumeration references and rich XHTML remain source data, to be mapped explicitly rather than guessed.
            var literal = value.Attribute("THE-VALUE");
            output[name] = literal?.Value ?? string.Concat(value.Elements().Where(x => x.Name.LocalName != "DEFINITION").Select(x => x.ToString(SaveOptions.DisableFormatting)));
        }
        return output;
    }

    private static string Attribute(XElement element, string name) => element.Attribute(name)?.Value ?? "";
    private static string PickNamed(IReadOnlyDictionary<string, string> values, IReadOnlyDictionary<string, string> names, params string[] candidates)
    {
        foreach (var candidate in candidates)
        {
            var keys = values.Keys.Where(x => string.Equals(names.GetValueOrDefault(x), candidate, StringComparison.OrdinalIgnoreCase)).ToArray();
            // Ambiguous display names are source facts; the mapping screen must select the exact definition.
            if (keys.Length == 1) return values[keys[0]];
        }
        return "";
    }
    private static string Pick(IReadOnlyDictionary<string, string> values, params string[] names)
    {
        foreach (var name in names)
            foreach (var pair in values)
                if (string.Equals(pair.Key, name, StringComparison.OrdinalIgnoreCase)) return pair.Value;
        return "";
    }
}
