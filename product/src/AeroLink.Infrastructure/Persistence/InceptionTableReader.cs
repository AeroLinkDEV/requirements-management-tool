using System.Text;
using System.Xml.Linq;

namespace AeroLink.Infrastructure.Persistence;

internal sealed record InceptionSourceTable(string Key, string Name, IReadOnlyList<string[]> Rows, IReadOnlyList<string> Findings);

/// <summary>Table decoding shared with proposal interchange; inception separately retains every source column.</summary>
internal static class InceptionTableReader
{
    private const int MaximumRows = 50_001;
    private const int MaximumColumns = 1024;

    public static IReadOnlyList<InceptionSourceTable> Csv(string text) => [new("csv", "CSV", CsvRows(text), [])];

    internal static List<string[]> CsvRows(string text)
    {
        var rows = new List<string[]>();
        var row = new List<string>();
        var field = new StringBuilder();
        var quoted = false;
        var closedQuote = false;
        void Field()
        {
            row.Add(field.ToString()); field.Clear(); closedQuote = false;
            if (row.Count > MaximumColumns) throw new InvalidOperationException("Source tables are limited to 1,024 columns.");
        }
        void Row()
        {
            Field(); rows.Add(row.ToArray()); row.Clear();
            if (rows.Count > MaximumRows) throw new InvalidOperationException("Source tables are limited to 50,000 data rows.");
        }
        for (var i = 0; i < text.Length; i++)
        {
            var character = text[i];
            if (character == '"')
            {
                if (quoted && i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                else if (quoted) { quoted = false; closedQuote = true; }
                else if (field.Length == 0 && !closedQuote) quoted = true;
                else throw new InvalidOperationException("The CSV contains an unexpected quote.");
            }
            else if (character == ',' && !quoted) Field();
            else if ((character == '\r' || character == '\n') && !quoted)
            {
                if (character == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                Row();
            }
            else
            {
                if (closedQuote) throw new InvalidOperationException("The CSV contains text after a closing quote.");
                field.Append(character);
            }
        }
        if (quoted) throw new InvalidOperationException("The CSV has an unterminated quoted field.");
        if (field.Length > 0 || row.Count > 0 || closedQuote) Row();
        return rows;
    }

    public static IReadOnlyList<InceptionSourceTable> Xlsx(Stream stream)
    {
        using var archive = new InceptionArchiveReader(stream);
        XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        var shared = new List<string>();
        if (archive.Entry("xl/sharedStrings.xml") is { } sharedEntry)
        {
            shared = archive.ReadXml(sharedEntry).Descendants(ns + "si").Select(x => string.Concat(x.Descendants(ns + "t").Select(t => t.Value))).ToList();
        }
        var workbook = archive.ReadXml(archive.Entry("xl/workbook.xml") ?? throw new InvalidOperationException("The workbook metadata is missing."));
        var relationships = archive.ReadXml(archive.Entry("xl/_rels/workbook.xml.rels") ?? throw new InvalidOperationException("The workbook relationships are missing."));
        var relationRows = relationships.Root?.Elements().Where(x => x.Name.LocalName == "Relationship").ToList() ?? [];
        if (relationRows.Any(x => string.IsNullOrEmpty((string?)x.Attribute("Id"))) || relationRows.GroupBy(x => (string?)x.Attribute("Id")).Any(x => x.Count() > 1))
            throw new InvalidOperationException("The workbook contains invalid relationship identities.");
        var relations = relationRows.ToDictionary(x => (string)x.Attribute("Id")!);
        var sheets = workbook.Descendants(ns + "sheet").ToList();
        if (sheets.Count == 0) throw new InvalidOperationException("The workbook contains no worksheets.");
        var output = new List<InceptionSourceTable>();
        var observed = new HashSet<string>(StringComparer.Ordinal);
        var totalRows = 0;
        foreach (var sheet in sheets)
        {
            var relationshipId = (string?)sheet.Attribute(XName.Get("id", "http://schemas.openxmlformats.org/officeDocument/2006/relationships")) ?? "";
            if (!relations.TryGetValue(relationshipId, out var relationship)
                || (string?)relationship.Attribute("TargetMode") == "External"
                || !((string?)relationship.Attribute("Type") ?? "").EndsWith("/worksheet", StringComparison.Ordinal))
                throw new InvalidOperationException("A workbook sheet has an unsupported relationship.");
            var target = (string?)relationship.Attribute("Target") ?? "";
            var path = target.StartsWith('/') ? target.TrimStart('/') : "xl/" + target;
            if (path.Contains("..", StringComparison.Ordinal) || path.Contains('\\') || !path.StartsWith("xl/worksheets/", StringComparison.Ordinal) || !observed.Add(path))
                throw new InvalidOperationException("The workbook contains an invalid or repeated worksheet target.");
            var name = (string?)sheet.Attribute("name") ?? "";
            if (string.IsNullOrWhiteSpace(name)) throw new InvalidOperationException("Workbook sheets require source names.");
            var document = archive.ReadXml(archive.Entry(path) ?? throw new InvalidOperationException("A declared worksheet is missing."));
            var rows = new List<string[]>();
            var findings = new List<string>();
            var state = (string?)sheet.Attribute("state") ?? "visible";
            if (state != "visible") findings.Add($"Worksheet '{name}' is {state}; its source rows are included in analysis.");
            foreach (var row in document.Descendants(ns + "row"))
            {
                var cells = new SortedDictionary<int, string>();
                foreach (var cell in row.Elements(ns + "c"))
                {
                    var reference = (string?)cell.Attribute("r") ?? throw new InvalidOperationException("Workbook cells require source coordinates.");
                    var column = ColumnIndex(reference);
                    var raw = cell.Element(ns + "v")?.Value ?? string.Concat(cell.Descendants(ns + "t").Select(x => x.Value));
                    if (cell.Element(ns + "f") is not null)
                        throw new InvalidOperationException($"Workbook cell '{name}:{reference}' contains a formula. Export source values before importing.");
                    var type = (string?)cell.Attribute("t");
                    if (type == "s")
                    {
                        if (!int.TryParse(raw, out var index) || index < 0 || index >= shared.Count)
                            throw new InvalidOperationException("The workbook contains an invalid shared string reference.");
                        raw = shared[index];
                    }
                    if (type == "e") throw new InvalidOperationException($"Workbook cell '{reference}' contains an error.");
                    if (!cells.TryAdd(column, raw)) throw new InvalidOperationException("The workbook repeats a cell coordinate.");
                }
                if (cells.Count == 0) continue;
                var values = Enumerable.Repeat("", cells.Keys.Max() + 1).ToArray();
                foreach (var cell in cells) values[cell.Key] = cell.Value;
                rows.Add(values);
                totalRows++;
                if (totalRows > MaximumRows) throw new InvalidOperationException("The workbook is limited to 50,001 rows across all sheets, including headers.");
            }
            if (document.Descendants(ns + "hyperlink").Any()) findings.Add($"Worksheet '{name}' includes hyperlinks; link metadata requires explicit exclusion.");
            output.Add(new(path, name, rows, findings));
        }
        var orphans = archive.Entries.Where(x => x.FullName.StartsWith("xl/worksheets/", StringComparison.Ordinal)
            && x.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) && !x.FullName.Contains("/_rels/", StringComparison.Ordinal) && !observed.Contains(x.FullName)).ToList();
        if (orphans.Count > 0)
            output[0] = output[0] with { Findings = output[0].Findings.Concat(orphans.Select(x => $"Unreferenced worksheet part '{x.FullName}' is not a workbook sheet and was not imported.")).ToArray() };
        return output;
    }

    private static int ColumnIndex(string reference)
    {
        var value = 0;
        var letters = reference.TakeWhile(char.IsAsciiLetter).ToArray();
        if (letters.Length == 0 || letters.Length > 3 || !reference.Skip(letters.Length).All(char.IsAsciiDigit)
            || reference.Length == letters.Length) throw new InvalidOperationException("The workbook contains an invalid cell coordinate.");
        foreach (var letter in letters) value = value * 26 + char.ToUpperInvariant(letter) - 'A' + 1;
        if (value > MaximumColumns) throw new InvalidOperationException("Source tables are limited to 1,024 columns.");
        return value - 1;
    }

}
