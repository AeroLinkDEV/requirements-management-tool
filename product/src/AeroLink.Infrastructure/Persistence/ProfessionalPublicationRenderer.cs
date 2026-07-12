using System.IO.Compression;
using System.Security;
using System.Text;

namespace AeroLink.Infrastructure.Persistence;

public sealed record PublicationApproval(string Role, string Name, string UserId, string State, DateTimeOffset? DecidedAt);
public sealed record PublicationRecord(string Number, string Classification, string Title, string Body, IReadOnlyList<(string Label, string Value)> Details);
public sealed record PublicationSection(string Heading, string Introduction, IReadOnlyList<PublicationRecord> Records);
public sealed record ProfessionalPublication(string Product, string Program, string Project, string DocumentType, string Title, string Subtitle,
    string DocumentNumber, string Revision, string Status, string Release, string Baseline, string PreparedBy, DateTimeOffset GeneratedAt,
    string ManifestHash, IReadOnlyList<(string Label, string Value)> Metadata, IReadOnlyList<PublicationApproval> Approvals,
    IReadOnlyList<(string Revision, string Status, string Date, string Author)> RevisionHistory, IReadOnlyList<PublicationSection> Sections);

public static class ProfessionalPublicationRenderer
{
    public static GeneratedOutput Render(ProfessionalPublication publication, string format, string stem) => format.Equals("pdf", StringComparison.OrdinalIgnoreCase)
        ? new(BuildPdf(publication), "application/pdf", stem + ".pdf")
        : new(BuildDocx(publication), "application/vnd.openxmlformats-officedocument.wordprocessingml.document", stem + ".docx");

    private static byte[] BuildDocx(ProfessionalPublication publication)
    {
        using var output = new MemoryStream(); using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true))
        {
            Entry(zip, "[Content_Types].xml", "<?xml version=\"1.0\" encoding=\"UTF-8\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/><Override PartName=\"/word/document.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml\"/><Override PartName=\"/word/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.styles+xml\"/><Override PartName=\"/word/header1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.header+xml\"/><Override PartName=\"/word/footer1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.footer+xml\"/></Types>");
            Entry(zip, "_rels/.rels", "<?xml version=\"1.0\" encoding=\"UTF-8\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"word/document.xml\"/></Relationships>");
            Entry(zip, "word/_rels/document.xml.rels", "<?xml version=\"1.0\" encoding=\"UTF-8\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/><Relationship Id=\"rId2\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/header\" Target=\"header1.xml\"/><Relationship Id=\"rId3\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/footer\" Target=\"footer1.xml\"/></Relationships>");
            Entry(zip, "word/styles.xml", Styles()); Entry(zip, "word/header1.xml", Header(publication)); Entry(zip, "word/footer1.xml", Footer(publication));
            var body = new StringBuilder();
            body.Append(P("CONTROLLED LIFECYCLE PUBLICATION", "CoverKicker")).Append(P(publication.Title, "CoverTitle")).Append(P(publication.Subtitle, "CoverSubtitle"));
            body.Append(P(publication.DocumentNumber + "  |  REVISION " + publication.Revision, "CoverNumber")).Append(P(publication.Status.ToUpperInvariant(), "CoverStatus"));
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
            body.Append(Table(Array.Empty<string>(), controlRows, new[] { 2700, 6660 }, true));
            body.Append(P("Approval Register", "Heading2"));
            var approvalRows = publication.Approvals.Select(x => (IReadOnlyList<string>)new[] { x.Role, x.Name + " (" + x.UserId + ")", ApprovalDecision(x) }).ToList();
            if (approvalRows.Count == 0) approvalRows.Add(new[] { "Approval", "Not yet recorded", "Pending" });
            body.Append(Table(new[] { "Authority", "Approver", "Decision" }, approvalRows, new[] { 2400, 3600, 3360 }, false));
            body.Append(P("Revision History", "Heading2"));
            body.Append(Table(new[] { "Revision", "Status", "Date", "Author / owner" }, publication.RevisionHistory.Select(x => (IReadOnlyList<string>)new[] { x.Revision, x.Status, x.Date, x.Author }).ToList(), new[] { 1400, 1900, 2200, 3860 }, false));
            body.Append(P("Authority and use", "Heading2")).Append(P("This publication is a deterministic rendering of authoritative lifecycle records. The database records, exact revision identities, approval decisions, baseline membership, and manifest hashes remain the source of truth. Printed or downloaded copies must be verified against the displayed manifest before use.", "Callout"));

            foreach (var section in publication.Sections)
            {
                body.Append(PageBreak()).Append(P(section.Heading, "Heading1")); if (!string.IsNullOrWhiteSpace(section.Introduction)) body.Append(P(section.Introduction, "Lead"));
                foreach (var record in section.Records)
                {
                    body.Append(P(record.Number + "  |  " + record.Classification, "Heading2", true)); if (!string.IsNullOrWhiteSpace(record.Title)) body.Append(P(record.Title, "RecordTitle", true));
                    body.Append(P(record.Body, "Normal", record.Details.Count > 0)); foreach (var detail in record.Details) body.Append(P(detail.Label + ": " + detail.Value, "RecordMeta"));
                }
            }
            body.Append("<w:sectPr><w:headerReference w:type=\"default\" r:id=\"rId2\"/><w:footerReference w:type=\"default\" r:id=\"rId3\"/><w:pgSz w:w=\"12240\" w:h=\"15840\"/><w:pgMar w:top=\"1440\" w:right=\"1440\" w:bottom=\"1440\" w:left=\"1440\" w:header=\"708\" w:footer=\"708\"/></w:sectPr>");
            Entry(zip, "word/document.xml", "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><w:body>" + body + "</w:body></w:document>");
        }
        return output.ToArray();
    }

    private static string Styles() => "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><w:styles xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\">" +
        Style("Normal", "Normal", 22, "25364D", false, 0, 120, 300) + Style("CoverKicker", "Cover Kicker", 20, "168578", true, 1500, 160, 280) + Style("CoverTitle", "Cover Title", 60, "102A43", true, 0, 120, 280) + Style("CoverSubtitle", "Cover Subtitle", 28, "526274", false, 0, 360, 300) + Style("CoverNumber", "Cover Number", 24, "2E74B5", true, 0, 80, 280) + Style("CoverStatus", "Cover Status", 20, "7A5A00", true, 0, 300, 280) + Style("CoverMeta", "Cover Meta", 20, "526274", false, 0, 60, 280) + Style("CoverApprovalHeading", "Cover Approval Heading", 18, "168578", true, 560, 100, 280) + Style("CoverApproval", "Cover Approval", 18, "25364D", false, 0, 80, 280) + Style("CoverNotice", "Cover Notice", 16, "718096", true, 620, 0, 280) + Style("Heading1", "Heading 1", 32, "2E74B5", true, 360, 200, 300) + Style("Heading2", "Heading 2", 26, "2E74B5", true, 280, 140, 300) + Style("Heading3", "Heading 3", 24, "1F4D78", true, 200, 100, 300) + Style("Lead", "Lead", 22, "526274", false, 0, 180, 300) + Style("RecordTitle", "Record Title", 22, "25364D", true, 0, 80, 300) + Style("RecordMeta", "Record Meta", 18, "718096", false, 0, 80, 280) + Style("TableText", "Table Text", 18, "25364D", false, 0, 40, 260) + Style("TableHeader", "Table Header", 18, "102A43", true, 0, 40, 260) + Style("Callout", "Callout", 20, "25364D", false, 120, 120, 300, "F4F6F9") + "</w:styles>";
    private static string Style(string id, string name, int size, string color, bool bold, int before, int after, int line, string? fill = null) => $"<w:style w:type=\"paragraph\" w:styleId=\"{id}\"><w:name w:val=\"{name}\"/>{(id == "Normal" ? "" : "<w:basedOn w:val=\"Normal\"/>")}<w:pPr><w:spacing w:before=\"{before}\" w:after=\"{after}\" w:line=\"{line}\" w:lineRule=\"auto\"/>{(fill is null ? "" : $"<w:shd w:val=\"clear\" w:fill=\"{fill}\"/>")}</w:pPr><w:rPr><w:rFonts w:ascii=\"Calibri\" w:hAnsi=\"Calibri\"/><w:color w:val=\"{color}\"/><w:sz w:val=\"{size}\"/>{(bold ? "<w:b/>" : "")}</w:rPr></w:style>";
    private static string P(string text, string style, bool keepNext = false) => $"<w:p><w:pPr><w:pStyle w:val=\"{style}\"/>{(keepNext ? "<w:keepNext/>" : "")}</w:pPr><w:r><w:t xml:space=\"preserve\">{SecurityElement.Escape(text)}</w:t></w:r></w:p>";
    private static string PageBreak() => "<w:p><w:r><w:br w:type=\"page\"/></w:r></w:p>";
    private static string Table(IReadOnlyList<string> headers, IReadOnlyList<IReadOnlyList<string>> rows, IReadOnlyList<int> widths, bool shadeFirstColumn)
    {
        var body = new StringBuilder($"<w:tbl><w:tblPr><w:tblW w:w=\"9360\" w:type=\"dxa\"/><w:tblInd w:w=\"120\" w:type=\"dxa\"/><w:tblLayout w:type=\"fixed\"/><w:tblBorders><w:top w:val=\"single\" w:sz=\"4\" w:color=\"D7DEE7\"/><w:left w:val=\"single\" w:sz=\"4\" w:color=\"D7DEE7\"/><w:bottom w:val=\"single\" w:sz=\"4\" w:color=\"D7DEE7\"/><w:right w:val=\"single\" w:sz=\"4\" w:color=\"D7DEE7\"/><w:insideH w:val=\"single\" w:sz=\"4\" w:color=\"E5EAF0\"/><w:insideV w:val=\"single\" w:sz=\"4\" w:color=\"E5EAF0\"/></w:tblBorders><w:tblCellMar><w:top w:w=\"100\" w:type=\"dxa\"/><w:start w:w=\"120\" w:type=\"dxa\"/><w:bottom w:w=\"100\" w:type=\"dxa\"/><w:end w:w=\"120\" w:type=\"dxa\"/></w:tblCellMar></w:tblPr><w:tblGrid>{string.Join("", widths.Select(x => $"<w:gridCol w:w=\"{x}\"/>"))}</w:tblGrid>");
        if (headers.Count > 0) body.Append(Row(headers, widths, true, false)); foreach (var row in rows) body.Append(Row(row, widths, false, shadeFirstColumn)); return body.Append("</w:tbl>").ToString();
    }
    private static string Row(IReadOnlyList<string> cells, IReadOnlyList<int> widths, bool header, bool shadeFirstColumn) => "<w:tr>" + string.Join("", cells.Select((x, i) => $"<w:tc><w:tcPr><w:tcW w:w=\"{widths[i]}\" w:type=\"dxa\"/>{(header || shadeFirstColumn && i == 0 ? "<w:shd w:val=\"clear\" w:fill=\"E8EEF5\"/>" : "")}<w:vAlign w:val=\"center\"/></w:tcPr>{P(x, header || shadeFirstColumn && i == 0 ? "TableHeader" : "TableText")}</w:tc>")) + "</w:tr>";
    private static string Header(ProfessionalPublication p) => $"<?xml version=\"1.0\" encoding=\"UTF-8\"?><w:hdr xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"><w:p><w:pPr><w:pBdr><w:bottom w:val=\"single\" w:sz=\"6\" w:color=\"168578\"/></w:pBdr></w:pPr><w:r><w:rPr><w:b/><w:color w:val=\"102A43\"/><w:sz w:val=\"18\"/></w:rPr><w:t>{SecurityElement.Escape(p.Product)}  |  {SecurityElement.Escape(p.DocumentType.ToUpperInvariant())}</w:t></w:r></w:p></w:hdr>";
    private static string Footer(ProfessionalPublication p) => $"<?xml version=\"1.0\" encoding=\"UTF-8\"?><w:ftr xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"><w:p><w:pPr><w:jc w:val=\"center\"/></w:pPr><w:r><w:rPr><w:color w:val=\"718096\"/><w:sz w:val=\"14\"/></w:rPr><w:t>{SecurityElement.Escape(p.DocumentNumber)} Rev {SecurityElement.Escape(p.Revision)} | {SecurityElement.Escape(p.Status)} | Manifest {p.ManifestHash[..Math.Min(12, p.ManifestHash.Length)]} | Page </w:t></w:r><w:fldSimple w:instr=\"PAGE\"><w:r><w:t>1</w:t></w:r></w:fldSimple></w:p></w:ftr>";
    private static string ApprovalDecision(PublicationApproval approval) => approval.State + (approval.DecidedAt is null ? "" : " - " + approval.DecidedAt.Value.UtcDateTime.ToString("yyyy-MM-dd"));
    private static void Entry(ZipArchive zip, string name, string content) { var entry = zip.CreateEntry(name, CompressionLevel.Optimal); using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false)); writer.Write(content); }

    private sealed record PdfLine(string Text, int Size, bool Bold, string Color = "25364D", int Indent = 0, int After = 4);
    private static byte[] BuildPdf(ProfessionalPublication p)
    {
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
        return AssemblePdf(pageStreams, p);
    }
    private static string PdfCover(ProfessionalPublication p)
    {
        var s = new StringBuilder("0.063 0.165 0.263 rg 0 0 612 792 re f\n0.086 0.522 0.471 rg 0 742 612 50 re f\nBT\n");
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
            block.AddRange(Wrap(record.Body, 105).Select(x => new PdfLine(x, 8, false, "25364D", 0, 2))); foreach (var detail in record.Details) block.AddRange(Wrap(detail.Label + ": " + detail.Value, 110).Select(x => new PdfLine(x, 7, false, "718096", 0, 2))); block.Add(new("", 5, false, "25364D", 0, 4)); var height = block.Sum(LineHeight);
            if (used + height > max && current.Count > 0) { pages.Add(PdfContentPage(current, p, false)); current = [new(section.Heading.ToUpperInvariant() + " - CONTINUED", 10, true, "718096", 0, 8)]; used = current.Sum(LineHeight); }
            current.AddRange(block); used += height;
        }
        if (current.Count > 0) pages.Add(PdfContentPage(current, p, false)); return pages;
    }
    private static int LineHeight(PdfLine line) => line.Size + line.After;
    private static string PdfContentPage(IReadOnlyList<PdfLine> lines, ProfessionalPublication p, bool control)
    {
        var s = new StringBuilder("0.086 0.522 0.471 RG 1.3 w 54 760 m 558 760 l S\nBT\n"); Text(s, p.Product + " | " + p.DocumentNumber + " Rev " + p.Revision, 54, 772, 8, true, "102A43"); var y = 738;
        foreach (var line in lines) { Text(s, line.Text, 54 + line.Indent, y, line.Size, line.Bold, line.Color); y -= line.Size + line.After; }
        Text(s, p.DocumentNumber + " | " + p.Status + " | Manifest " + p.ManifestHash[..Math.Min(12, p.ManifestHash.Length)], 54, 28, 7, false, "718096"); return s.Append("ET").ToString();
    }
    private static void Text(StringBuilder s, string value, int x, int y, int size, bool bold, string color) { var (r,g,b)=Rgb(color); s.Append($"{r:0.###} {g:0.###} {b:0.###} rg /{(bold ? "F2" : "F1")} {size} Tf 1 0 0 1 {x} {y} Tm ({PdfEscape(value)}) Tj\n"); }
    private static (double,double,double) Rgb(string hex) => (Convert.ToInt32(hex[..2],16)/255d,Convert.ToInt32(hex.Substring(2,2),16)/255d,Convert.ToInt32(hex.Substring(4,2),16)/255d);
    private static byte[] AssemblePdf(IReadOnlyList<string> streams, ProfessionalPublication p)
    {
        var objects = new List<string> { "<< /Type /Catalog /Pages 2 0 R >>" }; var pageNumbers = Enumerable.Range(0, streams.Count).Select(i => 5 + i * 2).ToList(); objects.Add($"<< /Type /Pages /Kids [{string.Join(" ", pageNumbers.Select(x => x + " 0 R"))}] /Count {streams.Count} >>"); objects.Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>"); objects.Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica-Bold >>");
        for (var i=0;i<streams.Count;i++) { var stream = streams[i] + $"\nBT /F1 7 Tf 0.443 0.502 0.565 rg 1 0 0 1 500 28 Tm (Page {i+1} of {streams.Count}) Tj ET"; objects.Add($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 3 0 R /F2 4 0 R >> >> /Contents {6+i*2} 0 R >>"); objects.Add($"<< /Length {Encoding.ASCII.GetByteCount(stream)} >>\nstream\n{stream}\nendstream"); }
        using var output = new MemoryStream(); using var writer = new StreamWriter(output, Encoding.ASCII, 1024, true) { NewLine = "\n" }; writer.Write("%PDF-1.4\n"); writer.Flush(); var offsets = new List<long>{0}; for(var i=0;i<objects.Count;i++){offsets.Add(output.Position);writer.Write($"{i+1} 0 obj\n{objects[i]}\nendobj\n");writer.Flush();}var xref=output.Position;writer.Write($"xref\n0 {objects.Count+1}\n0000000000 65535 f \n");foreach(var offset in offsets.Skip(1))writer.Write($"{offset:D10} 00000 n \n");writer.Write($"trailer\n<< /Size {objects.Count+1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF");writer.Flush();return output.ToArray();
    }
    private static IEnumerable<string> Wrap(string text, int width) { text = text ?? ""; if (text.Length == 0) { yield return ""; yield break; } for(var start=0;start<text.Length;){var length=Math.Min(width,text.Length-start);if(start+length<text.Length){var split=text.LastIndexOf(' ',start+length-1,length);if(split>start)length=split-start;}yield return text.Substring(start,length).Trim();start+=length;while(start<text.Length&&text[start]==' ')start++;} }
    private static string PdfEscape(string value) => new(value.Select(c => c is '(' or ')' or '\\' ? ' ' : c > 126 ? '-' : c).ToArray());
}
