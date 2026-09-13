using System.IO.Compression;
using System.Text;
using AeroLink.Infrastructure.Persistence;

namespace AeroLink.Infrastructure.Tests;

public sealed class ProjectCreationSourceParserTests
{
    [Fact]
    public void CsvRetainsForeignIdentityUnknownLevelAndEveryAttributeWithoutClaimingReconciliation()
    {
        using var input = Utf8("Identifier,Level,Statement,Created By,Status\r\nFOREIGN/27,Subsystem,\"Two lines\nwith a comma, here\",source.author,Approved\r\n");
        var result = ProjectCreationSourceParser.Analyse(input, "source.csv");
        var item = Assert.Single(result.Objects);
        Assert.Equal("FOREIGN/27", item.SourceIdentifier);
        Assert.Equal("Subsystem", item.Attributes["Level"]);
        Assert.Equal("source.author", item.Attributes["Created By"]);
        Assert.Equal("Approved", item.Attributes["Status"]);
        Assert.Equal("Two lines\nwith a comma, here", item.Attributes["Statement"]);
        Assert.Equal(64, result.Sha256.Length);
        Assert.Empty(result.Relations);
    }

    [Theory]
    [InlineData("Identifier,Identifier\na,b")]
    [InlineData("Identifier,Statement\na,\"unfinished")]
    [InlineData("Identifier,Statement\na,text,unaccounted")]
    [InlineData("Identifier,Statement\na,\"done\"oops")]
    public void CsvRefusesUnaccountableColumnsAndMalformedQuoting(string csv)
    {
        using var input = Utf8(csv);
        Assert.Throws<InvalidOperationException>(() => ProjectCreationSourceParser.Analyse(input, "source.csv"));
    }

    [Fact]
    public void XlsxObservesAllSheetsAndPreservesSparseForeignValues()
    {
        using var workbook = Workbook(("sheet1.xml", Sheet("F-1")), ("sheet2.xml", Sheet("F-2")));
        var result = ProjectCreationSourceParser.Analyse(workbook, "source.xlsx");
        Assert.Equal(2, result.Objects.Count);
        Assert.Equal(new[] { "F-1", "F-2" }, result.Objects.Select(x => x.SourceIdentifier));
        Assert.Equal(2, result.Objects.Select(x => x.Module).Distinct().Count());
        Assert.All(result.Objects, x => Assert.Equal("", x.Attributes["Level"]));
    }

    [Theory]
    [InlineData("<c r=\"A2\"><f>1+1</f><v>2</v></c>")]
    [InlineData("<c r=\"A2\" t=\"s\"><v>-1</v></c>")]
    [InlineData("<c r=\"ZZZZZZ2\"><v>2</v></c>")]
    public void XlsxRefusesUnverifiedFormulaAndInvalidReferences(string cell)
    {
        using var workbook = Workbook(("sheet1.xml", $"<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><sheetData><row>{cell}</row></sheetData></worksheet>"));
        Assert.Throws<InvalidOperationException>(() => ProjectCreationSourceParser.Analyse(workbook, "source.xlsx"));
    }

    [Fact]
    public void ReqIfRetainsSectionsRawLevelSourceAuthorshipAndDirectedDanglingRelations()
    {
        using var input = Utf8("""
            <REQ-IF xmlns="http://www.omg.org/spec/ReqIF/20110401/reqif.xsd">
              <REQ-IF-HEADER><SOURCE-TOOL-ID>Source tool</SOURCE-TOOL-ID></REQ-IF-HEADER>
              <SPEC-TYPES><SPEC-OBJECT-TYPE IDENTIFIER="REQ">
                <ATTRIBUTE-DEFINITION-STRING IDENTIFIER="level" LONG-NAME="Level"/>
                <ATTRIBUTE-DEFINITION-STRING IDENTIFIER="author" LONG-NAME="Created By"/>
              </SPEC-OBJECT-TYPE></SPEC-TYPES>
              <SPEC-OBJECTS>
                <SPEC-OBJECT IDENTIFIER="foreign-1" LAST-CHANGE="2001-02-03T00:00:00Z"><TYPE><SPEC-OBJECT-TYPE-REF>REQ</SPEC-OBJECT-TYPE-REF></TYPE><VALUES>
                  <ATTRIBUTE-VALUE-STRING THE-VALUE="Subsystem"><DEFINITION><ATTRIBUTE-DEFINITION-STRING-REF>level</ATTRIBUTE-DEFINITION-STRING-REF></DEFINITION></ATTRIBUTE-VALUE-STRING>
                  <ATTRIBUTE-VALUE-STRING THE-VALUE="source.person"><DEFINITION><ATTRIBUTE-DEFINITION-STRING-REF>author</ATTRIBUTE-DEFINITION-STRING-REF></DEFINITION></ATTRIBUTE-VALUE-STRING>
                </VALUES></SPEC-OBJECT>
                <SPEC-OBJECT IDENTIFIER="section"><TYPE><SPEC-OBJECT-TYPE-REF>SOT-SECTION</SPEC-OBJECT-TYPE-REF></TYPE></SPEC-OBJECT>
              </SPEC-OBJECTS>
              <SPECIFICATIONS><SPECIFICATION IDENTIFIER="mod" LONG-NAME="Module"><CHILDREN><SPEC-HIERARCHY><OBJECT><SPEC-OBJECT-REF>foreign-1</SPEC-OBJECT-REF></OBJECT></SPEC-HIERARCHY></CHILDREN></SPECIFICATION></SPECIFICATIONS>
              <SPEC-RELATIONS><SPEC-RELATION IDENTIFIER="link"><SOURCE><SPEC-OBJECT-REF>foreign-1</SPEC-OBJECT-REF></SOURCE><TARGET><SPEC-OBJECT-REF>missing</SPEC-OBJECT-REF></TARGET><TYPE><SPEC-RELATION-TYPE-REF>derive</SPEC-RELATION-TYPE-REF></TYPE></SPEC-RELATION></SPEC-RELATIONS>
            </REQ-IF>
            """);
        var result = ProjectCreationSourceParser.Analyse(input, "source.reqif");
        Assert.Equal(2, result.Objects.Count);
        var item = result.Objects[0];
        Assert.Equal("Subsystem", item.Attributes["Level"]);
        Assert.Equal("source.person", item.Attributes["Created By"]);
        Assert.Equal("Unmapped", item.Kind);
        Assert.Equal("Module", item.Module);
        Assert.Equal("Section", result.Objects[1].Kind);
        var relation = Assert.Single(result.Relations);
        Assert.Equal("foreign-1", relation.SourceKey);
        Assert.Equal("missing", relation.TargetKey);
        Assert.Contains(result.Findings, x => x.Contains("endpoint outside"));
    }

    [Fact]
    public void ReqIfRejectsDtdAndDuplicateSourceObjects()
    {
        using var dtd = Utf8("<!DOCTYPE REQ-IF [<!ENTITY ex SYSTEM 'file:///not-read'>]><REQ-IF>&ex;</REQ-IF>");
        Assert.Throws<System.Xml.XmlException>(() => ProjectCreationSourceParser.Analyse(dtd, "source.reqif"));
        using var duplicate = Utf8("<REQ-IF><SPEC-OBJECT IDENTIFIER='dup'/><SPEC-OBJECT IDENTIFIER='dup'/></REQ-IF>");
        Assert.Throws<InvalidOperationException>(() => ProjectCreationSourceParser.Analyse(duplicate, "source.reqif"));
    }

    private static MemoryStream Utf8(string content) => new(Encoding.UTF8.GetBytes(content));
    private static string Sheet(string id) => $"""
        <worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><sheetData>
        <row r="1"><c r="A1" t="inlineStr"><is><t>Identifier</t></is></c><c r="B1" t="inlineStr"><is><t>Level</t></is></c><c r="C1" t="inlineStr"><is><t>Statement</t></is></c></row>
        <row r="2"><c r="A2" t="inlineStr"><is><t>{id}</t></is></c><c r="C2" t="inlineStr"><is><t>Source wording</t></is></c></row>
        </sheetData></worksheet>
        """;
    private static MemoryStream Workbook(params (string Name, string Xml)[] sheets)
    {
        var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, true))
            foreach (var (name, xml) in sheets)
            {
                using var writer = new StreamWriter(archive.CreateEntry($"xl/worksheets/{name}").Open());
                writer.Write(xml);
            }
        output.Position = 0;
        return output;
    }
}
