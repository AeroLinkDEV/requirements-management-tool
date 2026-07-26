using System.IO.Compression;
using System.Text;
using AeroLink.Infrastructure.Persistence;

namespace AeroLink.Infrastructure.Tests;

/// <summary>
/// The import parser is the one place the product reads a file an engineer downloaded from somewhere else.
///
/// Two things are asserted: an ordinary export from a spreadsheet tool is read correctly, and a workbook
/// carrying a document type definition is refused rather than resolved. The second matters because a
/// requirements file circulates by email between organizations, so its provenance is weaker than the person
/// importing it assumes.
/// </summary>
public sealed class RequirementImportParsingTests
{
    [Fact]
    public void A_workbook_exported_by_a_spreadsheet_tool_is_read_row_by_row()
    {
        var workbook = Workbook(Sheet("""
            <row r="1"><c r="A1" t="s"><v>0</v></c><c r="B1" t="s"><v>1</v></c><c r="C1" t="s"><v>2</v></c><c r="D1" t="s"><v>3</v></c></row>
            <row r="2"><c r="A2" t="s"><v>4</v></c><c r="B2" t="s"><v>5</v></c><c r="C2" t="s"><v>6</v></c><c r="D2" t="s"><v>7</v></c></row>
            """), SharedStrings(
                "Identifier", "Level", "Statement", "VerificationMethod",
                "SYSR-00004100", "System", "The system shall sequence oceanic waypoints.", "Test"));

        using var stream = new MemoryStream(workbook);
        var rows = EnterpriseRequirementsService.ParseImport(stream, "requirements.xlsx");

        var row = Assert.Single(rows);
        Assert.True(row.Valid, string.Join("; ", row.Errors));
        Assert.Equal("SYSR-00004100", row.Identifier);
        Assert.Equal("The system shall sequence oceanic waypoints.", row.Statement);
    }

    /// <summary>
    /// A declared entry size is a number the sender chose, so it cannot be the only limit. What is asserted
    /// here is the reader's own posture: a document type definition is refused outright, which is what closes
    /// off both external resolution and entity expansion regardless of what the archive claims about itself.
    /// </summary>
    [Fact]
    public void A_workbook_that_declares_a_document_type_is_refused()
    {
        var workbook = Workbook(
            """
            <?xml version="1.0"?>
            <!DOCTYPE worksheet [ <!ENTITY expand "aaaaaaaaaa"> ]>
            <worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><sheetData>
            <row r="1"><c r="A1" t="inlineStr"><is>&expand;</is></c></row>
            </sheetData></worksheet>
            """, sharedStrings: null);

        using var stream = new MemoryStream(workbook);

        // Asserted on the reason, not merely on failure: a hand-built workbook could throw for a dozen
        // uninteresting reasons, and only one of them is the reader refusing the document type.
        var refusal = Assert.Throws<System.Xml.XmlException>(
            () => EnterpriseRequirementsService.ParseImport(stream, "hostile.xlsx"));
        Assert.Contains("DTD", refusal.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_comma_separated_export_is_read_the_same_way()
    {
        var csv = "Identifier,Level,Statement,VerificationMethod\nSYSR-00004200,System,\"A statement, with a comma.\",Analysis\n";
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(csv));

        var row = Assert.Single(EnterpriseRequirementsService.ParseImport(stream, "requirements.csv"));

        Assert.True(row.Valid, string.Join("; ", row.Errors));
        Assert.Equal("A statement, with a comma.", row.Statement);
    }

    private static string Sheet(string rows) =>
        $"""
        <?xml version="1.0"?>
        <worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><sheetData>{rows}</sheetData></worksheet>
        """;

    private static string SharedStrings(params string[] values) =>
        $"""
        <?xml version="1.0"?>
        <sst xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">{
            string.Concat(values.Select(x => $"<si><t>{x}</t></si>"))}</sst>
        """;

    private static byte[] Workbook(string sheet, string? sharedStrings)
    {
        using var memory = new MemoryStream();
        using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, true))
        {
            Write(zip, "xl/worksheets/sheet1.xml", sheet);
            if (sharedStrings is not null) Write(zip, "xl/sharedStrings.xml", sharedStrings);
        }
        return memory.ToArray();

        static void Write(ZipArchive zip, string name, string content)
        {
            using var writer = new StreamWriter(zip.CreateEntry(name).Open());
            writer.Write(content);
        }
    }
}
