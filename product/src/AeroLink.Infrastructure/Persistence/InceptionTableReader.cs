using System.IO.Compression;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace AeroLink.Infrastructure.Persistence;

internal sealed record InceptionSourceTable(string Key, string Name, IReadOnlyList<string[]> Rows, IReadOnlyList<string> Findings);

/// <summary>Table decoding shared with proposal interchange; inception separately retains every source column.</summary>
internal static class InceptionTableReader
{
    private const long ExpandedLimit = 100L * 1024 * 1024;
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
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read, true);
        if (archive.Entries.Count > 4096 || archive.Entries.Sum(x => x.Length) > ExpandedLimit)
            throw new InvalidOperationException("The workbook exceeds its expanded size or entry limit.");
        var entries = archive.Entries.GroupBy(x => x.FullName, StringComparer.Ordinal).ToList();
        if (entries.Any(x => x.Count() > 1)) throw new InvalidOperationException("The workbook contains duplicate ZIP entries.");
        XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        var shared = new List<string>();
        if (archive.GetEntry("xl/sharedStrings.xml") is { } sharedEntry)
        {
            using var part = sharedEntry.Open();
            shared = ReadXml(part).Descendants(ns + "si").Select(x => string.Concat(x.Descendants(ns + "t").Select(t => t.Value))).ToList();
        }
        var sheets = archive.Entries.Where(x => x.FullName.StartsWith("xl/worksheets/", StringComparison.Ordinal)
            && x.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) && !x.FullName.Contains("/_rels/", StringComparison.Ordinal)).OrderBy(x => x.FullName).ToList();
        if (sheets.Count == 0) throw new InvalidOperationException("The workbook contains no worksheets.");
        var output = new List<InceptionSourceTable>();
        foreach (var sheet in sheets)
        {
            using var part = sheet.Open();
            var document = ReadXml(part);
            var rows = new List<string[]>();
            var findings = new List<string>();
            foreach (var row in document.Descendants(ns + "row"))
            {
                var cells = new SortedDictionary<int, string>();
                foreach (var cell in row.Elements(ns + "c"))
                {
                    var reference = (string?)cell.Attribute("r") ?? throw new InvalidOperationException("Workbook cells require source coordinates.");
                    var column = ColumnIndex(reference);
                    var raw = cell.Element(ns + "v")?.Value ?? string.Concat(cell.Descendants(ns + "t").Select(x => x.Value));
                    if (cell.Element(ns + "f") is not null)
                        throw new InvalidOperationException($"Workbook cell '{sheet.FullName}:{reference}' contains a formula. Export source values before importing.");
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
                if (rows.Count > MaximumRows) throw new InvalidOperationException("Source tables are limited to 50,000 data rows.");
            }
            if (document.Descendants(ns + "hyperlink").Any()) findings.Add($"Worksheet '{sheet.FullName}' includes hyperlinks; link metadata requires explicit exclusion.");
            output.Add(new(sheet.FullName, sheet.FullName, rows, findings));
        }
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

    private static XDocument ReadXml(Stream stream)
    {
        using var reader = XmlReader.Create(stream, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null,
            MaxCharactersInDocument = ExpandedLimit, MaxCharactersFromEntities = 0
        });
        return XDocument.Load(reader, LoadOptions.None);
    }
}
