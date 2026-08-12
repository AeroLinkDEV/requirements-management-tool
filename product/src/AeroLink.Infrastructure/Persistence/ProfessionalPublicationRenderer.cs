using System.IO.Compression;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AeroLink.Infrastructure.Persistence;

public sealed record PublicationApproval(string Role, string Name, string UserId, string State, DateTimeOffset? DecidedAt);
public sealed record PublicationRecord(string Number, string Classification, string Title, string Body, IReadOnlyList<(string Label, string Value)> Details,string RichContentJson="");
public sealed record PublicationSection(string Heading, string Introduction, IReadOnlyList<PublicationRecord> Records);
public sealed record ProfessionalPublication(string Product, string Program, string Project, string DocumentType, string Title, string Subtitle,
    string DocumentNumber, string Revision, string Status, string Release, string Baseline, string PreparedBy, DateTimeOffset GeneratedAt,
    string ManifestHash, IReadOnlyList<(string Label, string Value)> Metadata, IReadOnlyList<PublicationApproval> Approvals,
    IReadOnlyList<(string Revision, string Status, string Date, string Author)> RevisionHistory, IReadOnlyList<PublicationSection> Sections)
{
    /// <summary>
    /// Text stamped across every page, or null for a document that stands on its own.
    ///
    /// Init-only rather than positional so that a publication which carries no watermark — every approved
    /// document — needs no change at its construction site, and so that adding one later cannot be done by
    /// accident at a call that meant something else.
    /// </summary>
    public string? Watermark { get; init; }

    /// <summary>
    /// True for Documentation Center managed documents: document number, revision, status and the page
    /// watermark are emitted as named AeroLink content controls so the controlled release transformation
    /// can change exactly those structures. Generated build/baseline publications leave this off.
    /// </summary>
    public bool ControlledStatusControls { get; init; }
}

public static class ProfessionalPublicationRenderer
{
    public static GeneratedOutput Render(ProfessionalPublication publication, string format, string stem) => format.Equals("pdf", StringComparison.OrdinalIgnoreCase)
        ? new(BuildPdf(publication), "application/pdf", stem + ".pdf")
        : new(BuildDocx(publication), "application/vnd.openxmlformats-officedocument.wordprocessingml.document", stem + ".docx");

    private static byte[] BuildDocx(ProfessionalPublication publication)
    {
        var images=Images(publication);
        using var output = new MemoryStream(); using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true))
        {
            Entry(zip, "[Content_Types].xml", "<?xml version=\"1.0\" encoding=\"UTF-8\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/><Default Extension=\"jpg\" ContentType=\"image/jpeg\"/><Default Extension=\"png\" ContentType=\"image/png\"/><Override PartName=\"/word/document.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml\"/><Override PartName=\"/word/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.styles+xml\"/><Override PartName=\"/word/header1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.header+xml\"/><Override PartName=\"/word/footer1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.footer+xml\"/></Types>");
            Entry(zip, "_rels/.rels", "<?xml version=\"1.0\" encoding=\"UTF-8\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"word/document.xml\"/></Relationships>");
            Entry(zip, "word/_rels/document.xml.rels", "<?xml version=\"1.0\" encoding=\"UTF-8\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/><Relationship Id=\"rId2\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/header\" Target=\"header1.xml\"/><Relationship Id=\"rId3\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/footer\" Target=\"footer1.xml\"/>"+string.Join("",images.Select(x=>$"<Relationship Id=\"rId{x.Index+3}\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/image\" Target=\"media/image{x.Index}.{x.Extension}\"/>"))+"</Relationships>");
            foreach(var image in images)BinaryEntry(zip,$"word/media/image{image.Index}.{image.Extension}",image.Bytes);
            Entry(zip, "word/styles.xml", Styles()); Entry(zip, "word/header1.xml", Header(publication)); Entry(zip, "word/footer1.xml", Footer(publication));
            var body = new StringBuilder();
            body.Append(P("CONTROLLED LIFECYCLE PUBLICATION", "CoverKicker")).Append(P(publication.Title, "CoverTitle")).Append(P(publication.Subtitle, "CoverSubtitle"));
            if (publication.ControlledStatusControls)
            {
                body.Append(PRuns(Sdt(WordDocumentStructure.DocumentNumberTag, Run(publication.DocumentNumber))
                    + Run("  |  REVISION ") + Sdt(WordDocumentStructure.RevisionTag, Run(publication.Revision)), "CoverNumber"));
                body.Append(PRuns(Sdt(WordDocumentStructure.StatusTag, RunCaps(publication.Status)), "CoverStatus"));
            }
            else
            {
                body.Append(P(publication.DocumentNumber + "  |  REVISION " + publication.Revision, "CoverNumber")).Append(P(publication.Status.ToUpperInvariant(), "CoverStatus"));
            }
            body.Append(P($"{publication.Product}  |  Release {publication.Release}", "CoverMeta")).Append(P($"{publication.Program}  |  {publication.Project}", "CoverMeta"));
            body.Append(P("APPROVALS RECORDED FOR THIS PUBLICATION", "CoverApprovalHeading"));
            var coverApprovals = publication.Approvals.Take(5).ToList();
            if (coverApprovals.Count == 0) body.Append(P("Approval pending - no completed approval decision is recorded.", "CoverApproval"));
            foreach (var approval in coverApprovals) body.Append(P($"{approval.Name}  |  {approval.Role}  |  {ApprovalDecision(approval)}", "CoverApproval"));
            if (publication.Approvals.Count > coverApprovals.Count) body.Append(P($"+ {publication.Approvals.Count - coverApprovals.Count} additional approvals in the Document Control register", "CoverApproval"));
            body.Append(P("CONTROLLED COPY  |  Verify manifest hash before use", "CoverNotice")).Append(PageBreak());

            body.Append(P("Document Control", "Heading1"));
            var controlRows = new List<IReadOnlyList<string>> { new[] { "Document type", publication.DocumentType }, new[] { "Document number", publication.DocumentNumber }, new[] { "Revision", publication.Revision }, new[] { "Status", publication.Status }, new[] { "Release", publication.Release }, new[] { "Baseline", publication.Baseline }, new[] { "Prepared by", publication.PreparedBy }, new[] { "Generated", publication.GeneratedAt.UtcDateTime.ToString("yyyy-MM-dd HH:mm 'UTC'") }, new[] { "Manifest SHA-256", publication.ManifestHash } };
            controlRows.AddRange(publication.Metadata.Select(x => (IReadOnlyList<string>)new[] { x.Label, x.Value }));
            body.Append(publication.ControlledStatusControls
                ? ControlledDocumentControlTable(controlRows)
                : Table(Array.Empty<string>(), controlRows, new[] { 2700, 6660 }, true));
            body.Append(P("Approval Register", "Heading2"));
            var approvalRows = publication.Approvals.Select(x => (IReadOnlyList<string>)new[] { x.Role, x.Name + " (" + x.UserId + ")", ApprovalDecision(x) }).ToList();
            if (approvalRows.Count == 0) approvalRows.Add(new[] { "Approval", "Not yet recorded", "Pending" });
            body.Append(Table(new[] { "Authority", "Approver", "Decision" }, approvalRows, new[] { 2400, 3600, 3360 }, false));
            body.Append(P("Revision History", "Heading2"));
            body.Append(Table(new[] { "Revision", "Status", "Date", "Author / owner" }, publication.RevisionHistory.Select(x => (IReadOnlyList<string>)new[] { x.Revision, x.Status, x.Date, x.Author }).ToList(), new[] { 1400, 1900, 2200, 3860 }, false));
            body.Append(P("Authority and use", "Heading2")).Append(P("This publication is a deterministic rendering of authoritative lifecycle records. The database records, exact revision identities, approval decisions, baseline membership, and manifest hashes remain the source of truth. Printed or downloaded copies must be verified against the displayed manifest before use.", "Callout"));

            foreach (var section in publication.Sections)
            {
                body.Append(P(section.Heading, "Heading1", pageBreakBefore: true)); if (!string.IsNullOrWhiteSpace(section.Introduction)) body.Append(P(section.Introduction, "Lead"));
                foreach (var record in section.Records)
                {
                    body.Append(P(record.Number + "  |  " + record.Classification, "Heading2", true)); if (!string.IsNullOrWhiteSpace(record.Title)) body.Append(P(record.Title, "RecordTitle", true));
                    body.Append(P(record.Body, "Normal", record.Details.Count > 0));body.Append(RichDocx(record,images)); foreach (var detail in record.Details) body.Append(P(detail.Label + ": " + detail.Value, "RecordMeta"));
                }
            }
            body.Append("<w:sectPr><w:headerReference w:type=\"default\" r:id=\"rId2\"/><w:footerReference w:type=\"default\" r:id=\"rId3\"/><w:pgSz w:w=\"12240\" w:h=\"15840\"/><w:pgMar w:top=\"1440\" w:right=\"1440\" w:bottom=\"1440\" w:left=\"1440\" w:header=\"708\" w:footer=\"708\"/></w:sectPr>");
            Entry(zip, "word/document.xml", "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\" xmlns:wp=\"http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing\" xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\" xmlns:pic=\"http://schemas.openxmlformats.org/drawingml/2006/picture\"><w:body>" + body + "</w:body></w:document>");
        }
        return output.ToArray();
    }

    private static string Styles() => "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><w:styles xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\">" +
        Style("Normal", "Normal", 22, "25364D", false, 0, 120, 300) + Style("CoverKicker", "Cover Kicker", 20, "168578", true, 1500, 160, 280) + Style("CoverTitle", "Cover Title", 60, "102A43", true, 0, 120, 280) + Style("CoverSubtitle", "Cover Subtitle", 28, "526274", false, 0, 360, 300) + Style("CoverNumber", "Cover Number", 24, "2E74B5", true, 0, 80, 280) + Style("CoverStatus", "Cover Status", 20, "7A5A00", true, 0, 300, 280) + Style("CoverMeta", "Cover Meta", 20, "526274", false, 0, 60, 280) + Style("CoverApprovalHeading", "Cover Approval Heading", 18, "168578", true, 560, 100, 280) + Style("CoverApproval", "Cover Approval", 18, "25364D", false, 0, 80, 280) + Style("CoverNotice", "Cover Notice", 16, "718096", true, 620, 0, 280) + Style("Heading1", "Heading 1", 32, "2E74B5", true, 360, 200, 300) + Style("Heading2", "Heading 2", 26, "2E74B5", true, 280, 140, 300) + Style("Heading3", "Heading 3", 24, "1F4D78", true, 200, 100, 300) + Style("Lead", "Lead", 22, "526274", false, 0, 180, 300) + Style("RecordTitle", "Record Title", 22, "25364D", true, 0, 80, 300) + Style("RecordMeta", "Record Meta", 18, "718096", false, 0, 80, 280) + Style("TableText", "Table Text", 18, "25364D", false, 0, 40, 260) + Style("TableHeader", "Table Header", 18, "102A43", true, 0, 40, 260) + Style("Callout", "Callout", 20, "25364D", false, 120, 120, 300, "F4F6F9") + "</w:styles>";
    private static string Style(string id, string name, int size, string color, bool bold, int before, int after, int line, string? fill = null) => $"<w:style w:type=\"paragraph\" w:styleId=\"{id}\"><w:name w:val=\"{name}\"/>{(id == "Normal" ? "" : "<w:basedOn w:val=\"Normal\"/>")}<w:pPr><w:spacing w:before=\"{before}\" w:after=\"{after}\" w:line=\"{line}\" w:lineRule=\"auto\"/>{(fill is null ? "" : $"<w:shd w:val=\"clear\" w:fill=\"{fill}\"/>")}</w:pPr><w:rPr><w:rFonts w:ascii=\"Calibri\" w:hAnsi=\"Calibri\"/><w:color w:val=\"{color}\"/><w:sz w:val=\"{size}\"/>{(bold ? "<w:b/>" : "")}</w:rPr></w:style>";
    private static string P(string text, string style, bool keepNext = false, bool pageBreakBefore = false) => $"<w:p><w:pPr><w:pStyle w:val=\"{style}\"/>{(keepNext ? "<w:keepNext/>" : "")}{(pageBreakBefore ? "<w:pageBreakBefore/>" : "")}</w:pPr><w:r><w:t xml:space=\"preserve\">{SecurityElement.Escape(text)}</w:t></w:r></w:p>";
    private static string PRuns(string innerXml, string style, bool keepNext = false, bool pageBreakBefore = false) => $"<w:p><w:pPr><w:pStyle w:val=\"{style}\"/>{(keepNext ? "<w:keepNext/>" : "")}{(pageBreakBefore ? "<w:pageBreakBefore/>" : "")}</w:pPr>{innerXml}</w:p>";
    private static string Run(string text) => $"<w:r><w:t xml:space=\"preserve\">{SecurityElement.Escape(text)}</w:t></w:r>";
    private static string RunCaps(string text) => $"<w:r><w:rPr><w:caps/></w:rPr><w:t xml:space=\"preserve\">{SecurityElement.Escape(text)}</w:t></w:r>";
    private static string Sdt(string tag, string innerXml) => $"<w:sdt><w:sdtPr><w:tag w:val=\"{tag}\"/></w:sdtPr><w:sdtContent>{innerXml}</w:sdtContent></w:sdt>";
    private static string PageBreak() => "<w:p><w:r><w:br w:type=\"page\"/></w:r></w:p>";
    private static string Table(IReadOnlyList<string> headers, IReadOnlyList<IReadOnlyList<string>> rows, IReadOnlyList<int> widths, bool shadeFirstColumn)
    {
        var body = new StringBuilder(TableStart(widths));
        if (headers.Count > 0) body.Append(Row(headers, widths, true, false)); foreach (var row in rows) body.Append(Row(row, widths, false, shadeFirstColumn)); return body.Append("</w:tbl>").ToString();
    }
    private static string TableStart(IReadOnlyList<int> widths) => $"<w:tbl><w:tblPr><w:tblW w:w=\"9360\" w:type=\"dxa\"/><w:tblInd w:w=\"120\" w:type=\"dxa\"/><w:tblLayout w:type=\"fixed\"/><w:tblBorders><w:top w:val=\"single\" w:sz=\"4\" w:color=\"D7DEE7\"/><w:left w:val=\"single\" w:sz=\"4\" w:color=\"D7DEE7\"/><w:bottom w:val=\"single\" w:sz=\"4\" w:color=\"D7DEE7\"/><w:right w:val=\"single\" w:sz=\"4\" w:color=\"D7DEE7\"/><w:insideH w:val=\"single\" w:sz=\"4\" w:color=\"E5EAF0\"/><w:insideV w:val=\"single\" w:sz=\"4\" w:color=\"E5EAF0\"/></w:tblBorders><w:tblCellMar><w:top w:w=\"100\" w:type=\"dxa\"/><w:start w:w=\"120\" w:type=\"dxa\"/><w:bottom w:w=\"100\" w:type=\"dxa\"/><w:end w:w=\"120\" w:type=\"dxa\"/></w:tblCellMar></w:tblPr><w:tblGrid>{string.Join("", widths.Select(x => $"<w:gridCol w:w=\"{x}\"/>"))}</w:tblGrid>";
    private static string ControlledDocumentControlTable(IReadOnlyList<IReadOnlyList<string>> rows)
    {
        var body = new StringBuilder(TableStart([2700, 6660]));
        foreach (var row in rows) body.Append(ControlledRow(row));
        return body.Append("</w:tbl>").ToString();
    }
    private static string ControlledRow(IReadOnlyList<string> cells)
    {
        var label = cells[0]; var value = cells[1];
        var valueInner = label switch
        {
            "Document number" => Sdt(WordDocumentStructure.DocumentNumberTag, Run(value)),
            "Revision" => Sdt(WordDocumentStructure.RevisionTag, Run(value)),
            "Status" => Sdt(WordDocumentStructure.StatusTag, Run(value)),
            _ => Run(value)
        };
        return "<w:tr>"
            + $"<w:tc><w:tcPr><w:tcW w:w=\"2700\" w:type=\"dxa\"/><w:shd w:val=\"clear\" w:fill=\"E8EEF5\"/><w:vAlign w:val=\"center\"/></w:tcPr>{P(label, "TableHeader")}</w:tc>"
            + $"<w:tc><w:tcPr><w:tcW w:w=\"6660\" w:type=\"dxa\"/><w:vAlign w:val=\"center\"/></w:tcPr>{PRuns(valueInner, "TableText")}</w:tc>"
            + "</w:tr>";
    }
    private static string Row(IReadOnlyList<string> cells, IReadOnlyList<int> widths, bool header, bool shadeFirstColumn) => "<w:tr>" + string.Join("", cells.Select((x, i) => $"<w:tc><w:tcPr><w:tcW w:w=\"{widths[i]}\" w:type=\"dxa\"/>{(header || shadeFirstColumn && i == 0 ? "<w:shd w:val=\"clear\" w:fill=\"E8EEF5\"/>" : "")}<w:vAlign w:val=\"center\"/></w:tcPr>{P(x, header || shadeFirstColumn && i == 0 ? "TableHeader" : "TableText")}</w:tc>")) + "</w:tr>";
    private static string Header(ProfessionalPublication p)
    {
        var watermark = DocxWatermark(p);
        var watermarkBlock = watermark.Length == 0 ? ""
            : p.ControlledStatusControls
                ? $"<w:p><w:sdt><w:sdtPr><w:tag w:val=\"{WordDocumentStructure.WatermarkTag}\"/></w:sdtPr><w:sdtContent><w:p>{watermark}</w:p></w:sdtContent></w:sdt></w:p>"
                : watermark;
        return $"<?xml version=\"1.0\" encoding=\"UTF-8\"?><w:hdr xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\" xmlns:v=\"urn:schemas-microsoft-com:vml\" xmlns:o=\"urn:schemas-microsoft-com:office:office\">{watermarkBlock}<w:p><w:pPr><w:pBdr><w:bottom w:val=\"single\" w:sz=\"6\" w:color=\"168578\"/></w:pBdr></w:pPr><w:r><w:rPr><w:b/><w:color w:val=\"102A43\"/><w:sz w:val=\"18\"/></w:rPr><w:t>{SecurityElement.Escape(p.Product)}  |  {SecurityElement.Escape(p.DocumentType.ToUpperInvariant())}</w:t></w:r></w:p></w:hdr>";
    }

    /// <summary>
    /// The watermark, as Word expects one: a VML shape anchored in the header.
    ///
    /// The header is the only place that repeats on every page without becoming part of the text, so a
    /// watermark placed anywhere else either appears once or pushes the content it is supposed to sit behind.
    /// `behindDoc` and the low opacity are what make it a stamp rather than a heading.
    /// </summary>
    private static string DocxWatermark(ProfessionalPublication p) =>
        string.IsNullOrWhiteSpace(p.Watermark) ? "" :
        "<w:r><w:rPr><w:noProof/></w:rPr><w:pict><v:shape id=\"AeroLinkWatermark\" o:spid=\"_x0000_s2049\" type=\"#_x0000_t136\" " +
        "style=\"position:absolute;margin-left:0;margin-top:0;width:468pt;height:117pt;rotation:315;z-index:-251658752;" +
        "mso-position-horizontal:center;mso-position-horizontal-relative:margin;mso-position-vertical:center;" +
        "mso-position-vertical-relative:margin\" o:allowincell=\"f\" fillcolor=\"#c8d0d8\" stroked=\"f\">" +
        $"<v:textpath style=\"font-family:&quot;Calibri&quot;;font-size:1pt\" string=\"{SecurityElement.Escape(p.Watermark)}\"/>" +
        "<v:fill opacity=\".45\"/></v:shape></w:pict></w:r>";
    private static string Footer(ProfessionalPublication p)
    {
        var manifest = p.ManifestHash[..Math.Min(12, p.ManifestHash.Length)];
        var inner = p.ControlledStatusControls
            ? Sdt(WordDocumentStructure.DocumentNumberTag, FooterRun(p.DocumentNumber)) + FooterRun(" Rev ")
                + Sdt(WordDocumentStructure.RevisionTag, FooterRun(p.Revision)) + FooterRun(" | ")
                + Sdt(WordDocumentStructure.StatusTag, FooterRun(p.Status)) + FooterRun($" | Manifest {manifest} | Page ")
            : FooterRun($"{p.DocumentNumber} Rev {p.Revision} | {p.Status} | Manifest {manifest} | Page ");
        return $"<?xml version=\"1.0\" encoding=\"UTF-8\"?><w:ftr xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"><w:p><w:pPr><w:jc w:val=\"center\"/></w:pPr>{inner}<w:fldSimple w:instr=\"PAGE\"><w:r><w:t>1</w:t></w:r></w:fldSimple></w:p></w:ftr>";
    }
    private static string FooterRun(string text) => $"<w:r><w:rPr><w:color w:val=\"718096\"/><w:sz w:val=\"14\"/></w:rPr><w:t xml:space=\"preserve\">{SecurityElement.Escape(text)}</w:t></w:r>";
    private static string ApprovalDecision(PublicationApproval approval) => approval.State + (approval.DecidedAt is null ? "" : " - " + approval.DecidedAt.Value.UtcDateTime.ToString("yyyy-MM-dd"));
    private static readonly DateTimeOffset DeterministicArchiveTime=new(2000,1,1,0,0,0,TimeSpan.Zero);
    private static void Entry(ZipArchive zip, string name, string content) { var entry = zip.CreateEntry(name, CompressionLevel.Optimal);entry.LastWriteTime=DeterministicArchiveTime; using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false)); writer.Write(content); }
    private static void BinaryEntry(ZipArchive zip,string name,byte[] content){var entry=zip.CreateEntry(name,CompressionLevel.Optimal);entry.LastWriteTime=DeterministicArchiveTime;using var stream=entry.Open();stream.Write(content);}

    private sealed record PublicationImage(int Index,string Key,byte[] Bytes,string Alt,string Caption,int Width,int Height,bool IsPng)
    {
        public string Extension => IsPng ? "png" : "jpg";
    }

    /// <summary>
    /// Decodes the images an authored record carries, once each. The same diagram referenced from three
    /// requirements is embedded once and pointed at three times, so a document does not grow with how often
    /// somebody reused a picture.
    /// </summary>
    private static List<PublicationImage> Images(ProfessionalPublication publication)
    {var result=new List<PublicationImage>();foreach(var record in publication.Sections.SelectMany(x=>x.Records))foreach(var block in Blocks(record)){if(BlockType(block)!="image"||!block.TryGetProperty("dataUri",out var data)||data.ValueKind!=JsonValueKind.String)continue;var bytes=ImageBytes(data.GetString()??"");if(bytes is null)continue;var key=Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();if(result.Any(x=>x.Key==key))continue;var isPng=PngImage.IsPng(bytes);var size=isPng?PngImage.Size(bytes):JpegSize(bytes);result.Add(new(result.Count+1,key,bytes,Text(block,"alt","Controlled inline image"),Text(block,"caption",""),size.Width,size.Height,isPng));}return result;}

    /// <summary>
    /// Only the two formats every renderer here can produce, and only as data this deployment already holds.
    /// A remote reference would be an outbound call from a controlled tool and a document that renders
    /// differently once somebody else's server changes.
    /// </summary>
    private static byte[]? ImageBytes(string uri)
    {
        foreach (var prefix in new[] { "data:image/png;base64,", "data:image/jpeg;base64," })
        {
            if (!uri.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            // Invalid controlled image data is represented by its authored alt text rather than by nothing.
            try { return Convert.FromBase64String(uri[prefix.Length..]); } catch (FormatException) { return null; }
        }
        return null;
    }
    private static IEnumerable<JsonElement> Blocks(PublicationRecord record)
    {if(string.IsNullOrWhiteSpace(record.RichContentJson))yield break;JsonDocument? document=null;try{document=JsonDocument.Parse(record.RichContentJson);var root=document.RootElement;if(root.TryGetProperty("blocks",out var blocks)&&blocks.ValueKind==JsonValueKind.Array)foreach(var block in blocks.EnumerateArray())yield return block.Clone();}finally{document?.Dispose();}}
    private static string BlockType(JsonElement block)=>Text(block,"type","").ToLowerInvariant();
    private static string Text(JsonElement block,string name,string fallback)=>block.TryGetProperty(name,out var value)&&value.ValueKind==JsonValueKind.String?value.GetString()??fallback:fallback;
    private static string RichDocx(PublicationRecord record,IReadOnlyList<PublicationImage> images)
    {var body=new StringBuilder();foreach(var block in Blocks(record)){switch(BlockType(block)){case "paragraph":body.Append(P(Text(block,"text",""),"Normal"));break;case "symbol":body.Append(P("Controlled symbol: "+Text(block,"value",""),"Callout"));break;case "reference":body.Append(P("Reference: "+Text(block,"label","")+" — "+Text(block,"target",""),"RecordMeta"));break;case "table":if(block.TryGetProperty("rows",out var rows)&&rows.ValueKind==JsonValueKind.Array){var values=rows.EnumerateArray().Where(x=>x.ValueKind==JsonValueKind.Array).Select(x=>(IReadOnlyList<string>)x.EnumerateArray().Select(v=>v.ToString()).ToList()).ToList();if(values.Count>0){var count=values.Max(x=>x.Count);var widths=Enumerable.Repeat(9360/Math.Max(1,count),count).ToList();body.Append(Table(values[0],values.Skip(1).ToList(),widths,false));}}break;case "image":if(block.TryGetProperty("dataUri",out var data)){var uri=data.GetString()??"";var comma=uri.IndexOf(',');if(comma>0){try{var key=Convert.ToHexString(SHA256.HashData(Convert.FromBase64String(uri[(comma+1)..]))).ToLowerInvariant();var image=images.FirstOrDefault(x=>x.Key==key);if(image is not null)body.Append(ImageDrawing(image));}catch(FormatException){body.Append(P(Text(block,"alt","Invalid controlled image"),"Callout"));}}}break;}}return body.ToString();}
    private static string ImageDrawing(PublicationImage image)
    {var cx=5_400_000L;var cy=Math.Clamp((long)(cx*Math.Max(1,image.Height)/(double)Math.Max(1,image.Width)),1_800_000L,4_000_000L);var alt=SecurityElement.Escape(image.Alt);return $"<w:p><w:pPr><w:jc w:val=\"center\"/></w:pPr><w:r><w:drawing><wp:inline distT=\"0\" distB=\"0\" distL=\"0\" distR=\"0\"><wp:extent cx=\"{cx}\" cy=\"{cy}\"/><wp:docPr id=\"{100+image.Index}\" name=\"ControlledImage{image.Index}\" descr=\"{alt}\"/><a:graphic><a:graphicData uri=\"http://schemas.openxmlformats.org/drawingml/2006/picture\"><pic:pic><pic:nvPicPr><pic:cNvPr id=\"{image.Index}\" name=\"image{image.Index}.{image.Extension}\"/><pic:cNvPicPr/></pic:nvPicPr><pic:blipFill><a:blip r:embed=\"rId{image.Index+3}\"/><a:stretch><a:fillRect/></a:stretch></pic:blipFill><pic:spPr><a:xfrm><a:off x=\"0\" y=\"0\"/><a:ext cx=\"{cx}\" cy=\"{cy}\"/></a:xfrm><a:prstGeom prst=\"rect\"><a:avLst/></a:prstGeom></pic:spPr></pic:pic></a:graphicData></a:graphic></wp:inline></w:drawing></w:r></w:p>"+P(image.Caption.Length>0?image.Caption:image.Alt,"RecordMeta");}
    private static (int Width,int Height) JpegSize(byte[] bytes)
    {for(var i=2;i+9<bytes.Length;){if(bytes[i]!=0xFF){i++;continue;}var marker=bytes[i+1];if(marker is >=0xC0 and <=0xC3 or >=0xC5 and <=0xC7 or >=0xC9 and <=0xCB or >=0xCD and <=0xCF)return((bytes[i+7]<<8)+bytes[i+8],(bytes[i+5]<<8)+bytes[i+6]);if(i+3>=bytes.Length)break;var length=(bytes[i+2]<<8)+bytes[i+3];if(length<2)break;i+=2+length;}return(640,360);}

    private sealed record PdfLine(string Text, int Size, bool Bold, string Color = "25364D", int Indent = 0, int After = 4);
    /// <summary>
    /// The images a PDF can actually carry, renumbered to be contiguous.
    ///
    /// PDF has no PNG filter, so a PNG has to arrive as pixels. Word needs no such thing and embeds the
    /// original file, which is why the two formats are selected separately: a PNG this decoder cannot read
    /// still belongs in the Word document, and only the PDF has to fall back to the image's alt text.
    /// </summary>
    private static List<(PublicationImage Image, byte[] Payload, string Filter)> PdfImages(ProfessionalPublication publication)
    {
        var result = new List<(PublicationImage, byte[], string)>();
        foreach (var image in Images(publication))
        {
            if (!image.IsPng) { result.Add((image with { Index = result.Count + 1 }, image.Bytes, "DCTDecode")); continue; }
            if (!PngImage.TryDecodeRgb(image.Bytes, out var width, out var height, out var rgb)) continue;
            result.Add((image with { Index = result.Count + 1, Width = width, Height = height }, Deflate(rgb), "FlateDecode"));
        }
        return result;
    }

    private static byte[] Deflate(byte[] raw)
    {
        using var output = new MemoryStream();
        using (var zlib = new ZLibStream(output, CompressionLevel.Optimal, true)) zlib.Write(raw);
        return output.ToArray();
    }

    private static byte[] BuildPdf(ProfessionalPublication p)
    {
        var images = PdfImages(p);
        var pageStreams = new List<string> { PdfCover(p) };
        var control = new List<PdfLine> { new("DOCUMENT CONTROL", 18, true, "2E74B5", 0, 10) };
        var metadata = new List<(string Label, string Value)> { ("Document type",p.DocumentType),("Document number",p.DocumentNumber),("Revision",p.Revision),("Status",p.Status),("Release",p.Release),("Baseline",p.Baseline),("Prepared by",p.PreparedBy),("Generated",p.GeneratedAt.UtcDateTime.ToString("yyyy-MM-dd HH:mm UTC")),("Manifest SHA-256",p.ManifestHash) }; metadata.AddRange(p.Metadata);
        foreach (var item in metadata) { control.Add(new(item.Label.ToUpperInvariant(), 7, true, "718096", 0, 1)); control.AddRange(Wrap(item.Value, 92).Select(x => new PdfLine(x, 9, false, "25364D", 0, 5))); }
        control.Add(new("APPROVAL REGISTER", 14, true, "2E74B5", 0, 7)); if (p.Approvals.Count == 0) control.Add(new("Approval pending - no completed approval decision is recorded.", 9, false));
        foreach (var approval in p.Approvals) control.Add(new($"{approval.Role}: {approval.Name} ({approval.UserId}) - {ApprovalDecision(approval)}", 8, true, "25364D", 0, 5));
        control.Add(new("REVISION HISTORY", 14, true, "2E74B5", 0, 7)); foreach (var revision in p.RevisionHistory) control.Add(new($"Rev {revision.Revision} | {revision.Status} | {revision.Date} | {revision.Author}", 8, false, "25364D", 0, 5));
        pageStreams.AddRange(Paginate(control, p, true));
        foreach (var section in p.Sections)
        {
            pageStreams.AddRange(PaginateSection(section, p));
        }
        pageStreams.AddRange(images.Select(image => PdfImagePage(image.Image, p)));
        return AssemblePdf(pageStreams, images);
    }
    private static string PdfCover(ProfessionalPublication p)
    {
        var s = new StringBuilder("0.063 0.165 0.263 rg 0 0 612 792 re f\n0.086 0.522 0.471 rg 0 742 612 50 re f\n" + PdfWatermark(p, true) + "BT\n");
        Text(s, p.Product.ToUpperInvariant() + "  |  CONTROLLED LIFECYCLE PUBLICATION", 54, 760, 9, true, "FFFFFF"); Text(s, p.DocumentType.ToUpperInvariant(), 64, 650, 10, true, "65D3C3");
        var y = 610; foreach (var line in Wrap(p.Title, 38)) { Text(s, line, 64, y, 25, true, "FFFFFF"); y -= 32; } foreach (var line in Wrap(p.Subtitle, 65)) { Text(s, line, 64, y - 4, 11, false, "B7C5D4"); y -= 17; }
        y -= 20; Text(s, p.DocumentNumber + "  |  REVISION " + p.Revision, 64, y, 12, true, "65D3C3"); Text(s, p.Status.ToUpperInvariant(), 64, y - 24, 10, true, "F0C96A");
        s.Append("0.105 0.235 0.340 rg 54 118 504 190 re f\n"); Text(s, "APPROVALS RECORDED FOR THIS PUBLICATION", 72, 282, 8, true, "65D3C3"); var ay = 258;
        if (p.Approvals.Count == 0) Text(s, "Approval pending - no completed approval decision is recorded.", 72, ay, 9, false, "FFFFFF");
        foreach (var approval in p.Approvals.Take(5)) { Text(s, approval.Name, 72, ay, 10, true, "FFFFFF"); Text(s, approval.Role + " | " + ApprovalDecision(approval), 72, ay - 13, 7, false, "B7C5D4"); ay -= 31; }
        if (p.Approvals.Count > 5) Text(s, "+ additional approvals in Document Control", 72, ay, 8, false, "B7C5D4");
        Text(s, p.Program + " | " + p.Project + " | Release " + p.Release, 64, 84, 8, false, "B7C5D4"); Text(s, "CONTROLLED COPY - Verify manifest hash before use", 64, 58, 8, true, "F0C96A"); return s.Append("ET").ToString();
    }
    private static IEnumerable<string> Paginate(IReadOnlyList<PdfLine> lines, ProfessionalPublication p, bool control)
    {
        var pages = new List<string>(); var current = new List<PdfLine>(); var used = 0; const int max = 650;
        foreach (var line in lines) { var height = line.Size + line.After; if (used + height > max && current.Count > 0) { pages.Add(PdfContentPage(current, p, control)); current = []; used = 0; } current.Add(line); used += height; }
        if (current.Count > 0) pages.Add(PdfContentPage(current, p, control)); return pages;
    }
    private static IEnumerable<string> PaginateSection(PublicationSection section, ProfessionalPublication p)
    {
        const int max = 650; var pages = new List<string>(); var current = new List<PdfLine> { new(section.Heading.ToUpperInvariant(), 18, true, "2E74B5", 0, 8) }; if (!string.IsNullOrWhiteSpace(section.Introduction)) current.AddRange(Wrap(section.Introduction, 92).Select(x => new PdfLine(x, 9, false, "526274", 0, 6))); var used = current.Sum(LineHeight);
        foreach (var record in section.Records)
        {
            var block = new List<PdfLine> { new(record.Number + " | " + record.Classification, 11, true, "2E74B5", 0, 3) }; if (!string.IsNullOrWhiteSpace(record.Title)) block.AddRange(Wrap(record.Title, 92).Select(x => new PdfLine(x, 9, true, "25364D", 0, 3)));
            block.AddRange(Wrap(record.Body, 105).Select(x => new PdfLine(x, 8, false, "25364D", 0, 2)));
            foreach (var rich in RichPdfLines(record)) block.Add(rich);
            foreach (var detail in record.Details) block.AddRange(Wrap(detail.Label + ": " + detail.Value, 110).Select(x => new PdfLine(x, 7, false, "718096", 0, 2))); block.Add(new("", 5, false, "25364D", 0, 4)); var height = block.Sum(LineHeight);
            if (used + height > max && current.Count > 0) { pages.Add(PdfContentPage(current, p, false)); current = [new(section.Heading.ToUpperInvariant() + " - CONTINUED", 10, true, "718096", 0, 8)]; used = current.Sum(LineHeight); }
            current.AddRange(block); used += height;
        }
        if (current.Count > 0) pages.Add(PdfContentPage(current, p, false)); return pages;
    }
    private static int LineHeight(PdfLine line) => line.Size + line.After;
    private static string PdfContentPage(IReadOnlyList<PdfLine> lines, ProfessionalPublication p, bool control)
    {
        var s = new StringBuilder(PdfWatermark(p, false) + "0.086 0.522 0.471 RG 1.3 w 54 760 m 558 760 l S\nBT\n"); Text(s, p.Product + " | " + p.DocumentNumber + " Rev " + p.Revision, 54, 772, 8, true, "102A43"); var y = 738;
        foreach (var line in lines) { Text(s, line.Text, 54 + line.Indent, y, line.Size, line.Bold, line.Color); y -= line.Size + line.After; }
        Text(s, p.DocumentNumber + " | " + p.Status + " | Manifest " + p.ManifestHash[..Math.Min(12, p.ManifestHash.Length)], 54, 28, 7, false, "718096"); return s.Append("ET").ToString();
    }
    private static void Text(StringBuilder s, string value, int x, int y, int size, bool bold, string color) { var (r,g,b)=Rgb(color); s.Append($"{r:0.###} {g:0.###} {b:0.###} rg /{(bold ? "F2" : "F1")} {size} Tf 1 0 0 1 {x} {y} Tm ({PdfEscape(value)}) Tj\n"); }

    /// <summary>
    /// The watermark, drawn diagonally across a page.
    ///
    /// Emitted first so everything else is drawn over it — a stamp the reader looks through, not a banner
    /// covering the text. The two colours are not a decoration: the cover is dark navy and the content pages
    /// are white, and one grey cannot be faint on both. Faint on the wrong background is either invisible,
    /// which defeats the purpose, or a solid block over the words.
    ///
    /// The matrix is a 45-degree rotation — cos and sin of the angle in the first four operands — with the
    /// origin placed low-left so the text runs corner to corner.
    /// </summary>
    private static string PdfWatermark(ProfessionalPublication p, bool onDark)
    {
        if (string.IsNullOrWhiteSpace(p.Watermark)) return "";
        var (r, g, b) = onDark ? (0.42, 0.50, 0.58) : (0.88, 0.90, 0.92);
        var s = new StringBuilder("BT\n");
        s.Append($"{r:0.###} {g:0.###} {b:0.###} rg /F2 96 Tf 0.7071 0.7071 -0.7071 0.7071 96 210 Tm ({PdfEscape(p.Watermark)}) Tj\n");
        return s.Append("ET\n").ToString();
    }
    private static (double,double,double) Rgb(string hex) => (Convert.ToInt32(hex[..2],16)/255d,Convert.ToInt32(hex.Substring(2,2),16)/255d,Convert.ToInt32(hex.Substring(4,2),16)/255d);
    private static IEnumerable<PdfLine> RichPdfLines(PublicationRecord record)
    {
        foreach (var block in Blocks(record)) switch (BlockType(block))
        {
            case "paragraph": foreach(var line in Wrap(Text(block,"text",""),105)) yield return new(line,8,false); break;
            case "symbol": yield return new("Controlled symbol: "+Text(block,"value",""),9,true,"168578",12,3); break;
            case "reference": yield return new("Reference: "+Text(block,"label","")+" -> "+Text(block,"target",""),8,false,"526274",12,3); break;
            case "image": yield return new("Inline controlled image: "+Text(block,"caption",Text(block,"alt","image")),8,true,"168578",12,3); break;
            case "table" when block.TryGetProperty("rows",out var rows)&&rows.ValueKind==JsonValueKind.Array:
                foreach(var row in rows.EnumerateArray().Where(x=>x.ValueKind==JsonValueKind.Array)) yield return new(string.Join(" | ",row.EnumerateArray().Select(x=>x.ToString())),8,false,"25364D",12,2);
                break;
        }
    }
    private static string PdfImagePage(PublicationImage image,ProfessionalPublication p)
    {
        var width=480d;var height=Math.Clamp(width*Math.Max(1,image.Height)/Math.Max(1d,image.Width),180d,540d);var y=680-height;
        var s=new StringBuilder("0.086 0.522 0.471 RG 1.3 w 54 760 m 558 760 l S\nBT\n");Text(s,p.Product+" | CONTROLLED INLINE IMAGE",54,772,8,true,"102A43");Text(s,image.Caption.Length>0?image.Caption:image.Alt,66,730,13,true,"2E74B5");s.Append("ET\n").Append($"q {width:0.###} 0 0 {height:0.###} 66 {y:0.###} cm /Im{image.Index} Do Q\nBT\n");Text(s,image.Alt,66,(int)Math.Max(52,y-22),8,false,"526274");return s.Append("ET").ToString();
    }
    private static byte[] AssemblePdf(IReadOnlyList<string> streams,IReadOnlyList<(PublicationImage Image,byte[] Payload,string Filter)> images)
    {
        static byte[] A(string value)=>Encoding.ASCII.GetBytes(value);
        var imageStart=5;var pageStart=imageStart+images.Count;var pageNumbers=Enumerable.Range(0,streams.Count).Select(i=>pageStart+i*2).ToList();
        var objects=new List<byte[]>{A("<< /Type /Catalog /Pages 2 0 R >>"),A($"<< /Type /Pages /Kids [{string.Join(" ",pageNumbers.Select(x=>x+" 0 R"))}] /Count {streams.Count} >>"),A("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>"),A("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica-Bold >>")};
        foreach(var (image,payload,filter) in images){using var item=new MemoryStream();var head=A($"<< /Type /XObject /Subtype /Image /Width {image.Width} /Height {image.Height} /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /{filter} /Length {payload.Length} >>\nstream\n");item.Write(head);item.Write(payload);item.Write(A("\nendstream"));objects.Add(item.ToArray());}
        var xobjects=images.Count==0?"":$" /XObject << {string.Join(" ",images.Select(x=>$"/Im{x.Image.Index} {imageStart+x.Image.Index-1} 0 R"))} >>";
        for(var i=0;i<streams.Count;i++){var stream=streams[i]+$"\nBT /F1 7 Tf 0.443 0.502 0.565 rg 1 0 0 1 500 28 Tm (Page {i+1} of {streams.Count}) Tj ET";var contentNumber=pageNumbers[i]+1;objects.Add(A($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 3 0 R /F2 4 0 R >>{xobjects} >> /Contents {contentNumber} 0 R >>"));objects.Add(A($"<< /Length {Encoding.ASCII.GetByteCount(stream)} >>\nstream\n{stream}\nendstream"));}
        using var output=new MemoryStream();output.Write(A("%PDF-1.4\n%----\n"));var offsets=new List<long>{0};for(var i=0;i<objects.Count;i++){offsets.Add(output.Position);output.Write(A($"{i+1} 0 obj\n"));output.Write(objects[i]);output.Write(A("\nendobj\n"));}var xref=output.Position;output.Write(A($"xref\n0 {objects.Count+1}\n0000000000 65535 f \n"));foreach(var offset in offsets.Skip(1))output.Write(A($"{offset:D10} 00000 n \n"));output.Write(A($"trailer\n<< /Size {objects.Count+1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF"));return output.ToArray();
    }
    private static IEnumerable<string> Wrap(string text, int width) { text = text ?? ""; if (text.Length == 0) { yield return ""; yield break; } for(var start=0;start<text.Length;){var length=Math.Min(width,text.Length-start);if(start+length<text.Length){var split=text.LastIndexOf(' ',start+length-1,length);if(split>start)length=split-start;}yield return text.Substring(start,length).Trim();start+=length;while(start<text.Length&&text[start]==' ')start++;} }
    private static string PdfEscape(string value) => new(value.Select(c => c is '(' or ')' or '\\' ? ' ' : c > 126 ? '-' : c).ToArray());
}
