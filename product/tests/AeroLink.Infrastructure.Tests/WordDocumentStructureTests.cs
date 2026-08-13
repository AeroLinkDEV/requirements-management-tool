using System.IO.Compression;
using System.Text;
using AeroLink.Domain.Common;
using AeroLink.Infrastructure.Persistence;

namespace AeroLink.Infrastructure.Tests;

public sealed class WordDocumentStructureTests
{
    private const string W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    private const string Rel = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private const string PRel = "http://schemas.openxmlformats.org/package/2006/relationships";
    private const string HeaderContentType = "application/vnd.openxmlformats-officedocument.wordprocessingml.header+xml";

    [Fact]
    public void Orphan_header_marker_does_not_satisfy_the_rendered_draft_watermark_requirement()
    {
        var bytes = Package(
            ("[Content_Types].xml", ContentTypes("/word/header1.xml")),
            ("word/document.xml", Document("<w:p><w:r><w:t>Body</w:t></w:r></w:p>", Section())),
            ("word/_rels/document.xml.rels", Rels()),
            ("word/header1.xml", HeaderWithWatermark()));

        var resolution = WordDocumentStructure.ResolveHeaders(bytes);
        Assert.Contains("word/header1.xml", resolution.OrphanHeaderParts);
        var error = Assert.Throws<DomainException>(() => ManagedDocumentFileService.ValidateDocx(bytes, requireDraftWatermark: true));
        Assert.Contains("default", error.Message);
    }

    [Fact]
    public void Unreferenced_header_part_without_watermark_does_not_fail_a_valid_draft()
    {
        var bytes = Package(
            ("[Content_Types].xml", ContentTypes("/word/header1.xml", "/word/header2.xml")),
            ("word/document.xml", Document("<w:p><w:r><w:t>Body</w:t></w:r></w:p>", Section(("default", "rId1")))),
            ("word/_rels/document.xml.rels", Rels(("rId1", HeaderType, "header1.xml", false))),
            ("word/header1.xml", HeaderWithWatermark()),
            ("word/header2.xml", PlainHeader()));

        ManagedDocumentFileService.ValidateDocx(bytes, requireDraftWatermark: true);
        var resolution = WordDocumentStructure.ResolveHeaders(bytes);
        Assert.Contains("word/header2.xml", resolution.OrphanHeaderParts);
    }

    [Fact]
    public void Linked_to_previous_inherits_the_prior_section_header()
    {
        var body = "<w:p><w:pPr><w:sectPr><w:headerReference w:type=\"default\" r:id=\"rId1\"/></w:sectPr></w:pPr><w:r><w:t>Page one</w:t></w:r></w:p>"
            + "<w:p><w:r><w:t>Page two</w:t></w:r></w:p>";
        var bytes = Package(
            ("[Content_Types].xml", ContentTypes("/word/header1.xml")),
            ("word/document.xml", Document(body, "<w:sectPr></w:sectPr>")),
            ("word/_rels/document.xml.rels", Rels(("rId1", HeaderType, "header1.xml", false))),
            ("word/header1.xml", HeaderWithWatermark()));

        var resolution = WordDocumentStructure.ResolveHeaders(bytes);
        Assert.Equal(2, resolution.Sections.Count);
        Assert.Equal("word/header1.xml", resolution.Sections[1].Effective("default"));
        ManagedDocumentFileService.ValidateDocx(bytes, requireDraftWatermark: true);
    }

    [Fact]
    public void An_unlinked_section_with_an_unmarked_header_is_rejected()
    {
        var bytes = Package(
            ("[Content_Types].xml", ContentTypes("/word/header1.xml", "/word/header2.xml")),
            ("word/document.xml", Document(
                "<w:p><w:pPr><w:sectPr><w:headerReference w:type=\"default\" r:id=\"rId1\"/></w:sectPr></w:pPr><w:r><w:t>Body</w:t></w:r></w:p>",
                "<w:sectPr><w:headerReference w:type=\"default\" r:id=\"rId2\"/></w:sectPr>")),
            ("word/_rels/document.xml.rels", Rels(("rId1", HeaderType, "header1.xml", false), ("rId2", HeaderType, "header2.xml", false))),
            ("word/header1.xml", HeaderWithWatermark()),
            ("word/header2.xml", PlainHeader()));

        var error = Assert.Throws<DomainException>(() => ManagedDocumentFileService.ValidateDocx(bytes, requireDraftWatermark: true));
        Assert.Contains("Section 2 default", error.Message);
    }

    [Fact]
    public void Title_page_and_even_odd_variants_are_each_enforced()
    {
        var missingFirst = Package(
            ("[Content_Types].xml", ContentTypes("/word/header1.xml", "/word/header2.xml", "/word/header3.xml")),
            ("word/settings.xml", Settings(evenAndOdd: true)),
            ("word/document.xml", Document("<w:p><w:r><w:t>Body</w:t></w:r></w:p>",
                Section(true, ("default", "rId1"), ("first", "rId2"), ("even", "rId3")))),
            ("word/_rels/document.xml.rels", Rels(("rId1", HeaderType, "header1.xml", false), ("rId2", HeaderType, "header2.xml", false), ("rId3", HeaderType, "header3.xml", false))),
            ("word/header1.xml", HeaderWithWatermark()),
            ("word/header2.xml", PlainHeader()),
            ("word/header3.xml", HeaderWithWatermark()));

        var error = Assert.Throws<DomainException>(() => ManagedDocumentFileService.ValidateDocx(missingFirst, requireDraftWatermark: true));
        Assert.Contains("Section 1 first header", error.Message);

        var allMarked = Package(
            ("[Content_Types].xml", ContentTypes("/word/header1.xml", "/word/header2.xml", "/word/header3.xml")),
            ("word/settings.xml", Settings(evenAndOdd: true)),
            ("word/document.xml", Document("<w:p><w:r><w:t>Body</w:t></w:r></w:p>",
                Section(true, ("default", "rId1"), ("first", "rId2"), ("even", "rId3")))),
            ("word/_rels/document.xml.rels", Rels(("rId1", HeaderType, "header1.xml", false), ("rId2", HeaderType, "header2.xml", false), ("rId3", HeaderType, "header3.xml", false))),
            ("word/header1.xml", HeaderWithWatermark()),
            ("word/header2.xml", HeaderWithWatermark()),
            ("word/header3.xml", HeaderWithWatermark()));
        ManagedDocumentFileService.ValidateDocx(allMarked, requireDraftWatermark: true);
    }

    [Fact]
    public void Broken_and_external_header_relationships_fail_closed_with_section_diagnostics()
    {
        var broken = Package(
            ("[Content_Types].xml", ContentTypes("/word/header1.xml")),
            ("word/document.xml", Document("<w:p><w:r><w:t>Body</w:t></w:r></w:p>", Section(("default", "rId1")))),
            ("word/_rels/document.xml.rels", Rels()),
            ("word/header1.xml", HeaderWithWatermark()));
        var brokenError = Assert.Throws<DomainException>(() => ManagedDocumentFileService.ValidateDocx(broken, requireDraftWatermark: true));
        Assert.Contains("Section 1 default header relationship is missing or external", brokenError.Message);

        var external = Package(
            ("[Content_Types].xml", ContentTypes("/word/header1.xml")),
            ("word/document.xml", Document("<w:p><w:r><w:t>Body</w:t></w:r></w:p>", Section(("default", "rId1")))),
            ("word/_rels/document.xml.rels", Rels(("rId1", HeaderType, "http://example.invalid/header.xml", true))),
            ("word/header1.xml", HeaderWithWatermark()));
        var externalError = Assert.Throws<DomainException>(() => ManagedDocumentFileService.ValidateDocx(external, requireDraftWatermark: true));
        Assert.Contains("missing or external", externalError.Message);
    }

    [Fact]
    public void Hidden_text_alt_text_and_off_page_shapes_cannot_satisfy_the_watermark_requirement()
    {
        var hidden = Package(
            ("[Content_Types].xml", ContentTypes("/word/header1.xml")),
            ("word/document.xml", Document("<w:p><w:r><w:t>Body</w:t></w:r></w:p>", Section(("default", "rId1")))),
            ("word/_rels/document.xml.rels", Rels(("rId1", HeaderType, "header1.xml", false))),
            ("word/header1.xml", FakeMarkerHeader()));
        Assert.Throws<DomainException>(() => ManagedDocumentFileService.ValidateDocx(hidden, requireDraftWatermark: true));

        var offPage = Package(
            ("[Content_Types].xml", ContentTypes("/word/header1.xml")),
            ("word/document.xml", Document("<w:p><w:r><w:t>Body</w:t></w:r></w:p>", Section(("default", "rId1")))),
            ("word/_rels/document.xml.rels", Rels(("rId1", HeaderType, "header1.xml", false))),
            ("word/header1.xml", OffPageShapeHeader()));
        Assert.Throws<DomainException>(() => ManagedDocumentFileService.ValidateDocx(offPage, requireDraftWatermark: true));
    }

    [Fact]
    public void Split_run_draft_status_is_detected_across_adjacent_runs()
    {
        var xml = $"<w:document xmlns:w=\"{W}\"><w:body><w:p><w:sdt><w:sdtPr><w:tag w:val=\"AeroLink.Status\"/></w:sdtPr>"
            + "<w:sdtContent><w:r><w:t>Dr</w:t></w:r><w:r><w:t>aft</w:t></w:r></w:sdtContent></w:sdt></w:p></w:body></w:document>";
        Assert.True(WordDocumentStructure.PartHasControlledDraftStatus(xml));
    }

    [Fact]
    public void Release_marking_changes_only_controlled_structures_and_preserves_technical_content()
    {
        var draft = ProfessionalPublicationRenderer.Render(ControlledPublication("Draft", "DRAFT", "01"), "docx", "SDP-000001.01").Content;
        var released = ManagedDocumentFileService.ApplyReleaseMarking(draft);

        Assert.Equal(WordDocumentStructure.TechnicalContentFingerprint(draft), WordDocumentStructure.TechnicalContentFingerprint(released));
        var fields = WordDocumentStructure.ControlledFields(released);
        Assert.NotEmpty(fields.Statuses);
        Assert.All(fields.Statuses, status => Assert.Equal("Released", status.Trim()));
        Assert.False(ManagedDocumentFileService.ContainsDraftWatermark(released));
        Assert.True(ManagedDocumentFileService.ValidateReleaseTransformation(draft, released, "SDP-000001", 1).IsValid);
    }

    [Fact]
    public void Legitimate_draft_words_survive_the_release_transformation_unchanged()
    {
        var draft = ProfessionalPublicationRenderer.Render(
            ControlledBodyPublication("Draft", "DRAFT", "01", "Draft interface data shall be retained for comparison.", true), "docx", "SDP-000001.01").Content;
        var released = ManagedDocumentFileService.ApplyReleaseMarking(draft);

        var text = WordDocumentStructure.NormalizedPartText(ReadPart(released, "word/document.xml"));
        Assert.Contains("Draft interface data shall be retained for comparison.", text);
        Assert.True(ManagedDocumentFileService.ValidateReleaseTransformation(draft, released, "SDP-000001", 1).IsValid);
    }

    [Fact]
    public void Unauthorized_technical_content_change_is_rejected()
    {
        var draft = ProfessionalPublicationRenderer.Render(ControlledPublication("Draft", "DRAFT", "01"), "docx", "SDP-000001.01").Content;
        var released = ManagedDocumentFileService.ApplyReleaseMarking(draft);
        var tampered = RewriteDocumentText(released, "Controlled content.", "Changed content.");

        var validation = ManagedDocumentFileService.ValidateReleaseTransformation(draft, tampered, "SDP-000001", 1);
        Assert.False(validation.IsValid);
        Assert.Equal("candidate_source_mismatch", validation.Code);
    }

    [Fact]
    public void Unrelated_clean_docx_is_rejected_even_with_matching_metadata()
    {
        var reviewed = ProfessionalPublicationRenderer.Render(
            ControlledBodyPublication("Draft", "DRAFT", "01", "Reviewed content.", true), "docx", "SDP-000001.01").Content;
        var unrelated = ManagedDocumentFileService.ApplyReleaseMarking(
            ProfessionalPublicationRenderer.Render(
                ControlledBodyPublication("Draft", "DRAFT", "01", "Different content.", true), "docx", "SDP-000001.01").Content);

        var validation = ManagedDocumentFileService.ValidateReleaseTransformation(reviewed, unrelated, "SDP-000001", 1);
        Assert.False(validation.IsValid);
        Assert.Equal("candidate_source_mismatch", validation.Code);
    }

    [Fact]
    public void Wrong_controlled_number_or_revision_is_rejected_before_content_comparison()
    {
        var draft = ProfessionalPublicationRenderer.Render(ControlledPublication("Draft", "DRAFT", "01"), "docx", "SDP-000001.01").Content;
        var released = ManagedDocumentFileService.ApplyReleaseMarking(draft);

        var wrongNumber = ManagedDocumentFileService.ValidateReleaseTransformation(draft, released, "SDP-999999", 1);
        Assert.Equal("invalid_release_metadata", wrongNumber.Code);
        var wrongRevision = ManagedDocumentFileService.ValidateReleaseTransformation(draft, released, "SDP-000001", 2);
        Assert.Equal("invalid_release_metadata", wrongRevision.Code);
    }

    [Fact]
    public void New_model_successor_keeps_controls_and_regains_the_draft_watermark()
    {
        var released = ManagedDocumentFileService.ApplyReleaseMarking(
            ProfessionalPublicationRenderer.Render(ControlledPublication("Draft", "DRAFT", "00"), "docx", "SDP-000001.00").Content);
        var successor = ManagedDocumentFileService.PrepareNextRevisionDraft(released, "SDP-000001", 0, 1);

        var fields = WordDocumentStructure.ControlledFields(successor);
        Assert.All(fields.Revisions, value => Assert.Equal("01", value));
        Assert.All(fields.Statuses, status => Assert.Contains("Draft", status, StringComparison.OrdinalIgnoreCase));
        Assert.True(ManagedDocumentFileService.ContainsDraftWatermark(successor));
        ManagedDocumentFileService.ValidateDocx(successor, requireDraftWatermark: true);
    }

    [Fact]
    public void Word_part_renames_do_not_change_the_technical_fingerprint_or_release_validation()
    {
        var draft = ProfessionalPublicationRenderer.Render(ControlledPublication("Draft", "DRAFT", "01"), "docx", "SDP-000001.01").Content;
        var released = ManagedDocumentFileService.ApplyReleaseMarking(draft);
        var renamed = RenameStoryPart(RenameStoryPart(released, "header1.xml", "header2.xml"), "footer1.xml", "footer2.xml");

        Assert.Equal(WordDocumentStructure.TechnicalContentFingerprint(released), WordDocumentStructure.TechnicalContentFingerprint(renamed));
        Assert.True(ManagedDocumentFileService.ValidateReleaseTransformation(draft, renamed, "SDP-000001", 1).IsValid);
    }

    private const string HeaderType = "http://schemas.openxmlformats.org/officeDocument/2006/relationships/header";

    private static ProfessionalPublication ControlledPublication(string status, string? watermark, string? revision = null) =>
        ControlledBodyPublication(status, watermark, revision ?? (status == "Draft" ? "01" : "00"), "Controlled content.", true);

    private static ProfessionalPublication ControlledBodyPublication(string status, string? watermark, string revision, string body, bool controlled) =>
        new("AeroLink", "FMS", "FMS Product Development", "Software Development Plan", "FMS Software Development Plan", "Controlled project document",
            "SDP-000001", revision, status, "1.6", "FMS-1.6", "software.author", DateTimeOffset.UnixEpoch, new string('a', 64), [], [],
            [("00", "Released", "2026-01-01", "software.lead")],
            [new("Purpose", "Scope", [new("1", "Plan", "Purpose", body, [])])])
        { Watermark = watermark, ControlledStatusControls = controlled };

    private static byte[] Package(params (string Name, string Content)[] parts)
    {
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true))
        {
            foreach (var (name, content) in parts)
            {
                var entry = zip.CreateEntry(name);
                using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
                writer.Write(content);
            }
        }
        return output.ToArray();
    }

    private static string ContentTypes(params string[] headerOverrides) =>
        "<?xml version=\"1.0\" encoding=\"UTF-8\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">"
        + "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>"
        + "<Default Extension=\"xml\" ContentType=\"application/xml\"/>"
        + "<Override PartName=\"/word/document.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml\"/>"
        + string.Join("", headerOverrides.Select(name => $"<Override PartName=\"{name}\" ContentType=\"{HeaderContentType}\"/>"))
        + "</Types>";

    private static string Rels(params (string Id, string Type, string Target, bool External)[] entries) =>
        $"<?xml version=\"1.0\" encoding=\"UTF-8\"?><Relationships xmlns=\"{PRel}\">"
        + string.Join("", entries.Select(entry => $"<Relationship Id=\"{entry.Id}\" Type=\"{entry.Type}\" Target=\"{entry.Target}\"{(entry.External ? " TargetMode=\"External\"" : "")}/>"))
        + "</Relationships>";

    private static string Document(string bodyXml, string sectPrXml) =>
        $"<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><w:document xmlns:w=\"{W}\" xmlns:r=\"{Rel}\"><w:body>{bodyXml}{sectPrXml}</w:body></w:document>";

    private static string Section(params (string Type, string Id)[] references) => Section(false, references);

    private static string Section(bool titlePage, params (string Type, string Id)[] references) =>
        "<w:sectPr>" + string.Join("", references.Select(reference => $"<w:headerReference w:type=\"{reference.Type}\" r:id=\"{reference.Id}\"/>"))
        + (titlePage ? "<w:titlePg/>" : "") + "</w:sectPr>";

    private static string Settings(bool evenAndOdd) =>
        $"<?xml version=\"1.0\" encoding=\"UTF-8\"?><w:settings xmlns:w=\"{W}\">{(evenAndOdd ? "<w:evenAndOddHeaders/>" : "")}</w:settings>";

    private static string HeaderXml(string inner) =>
        $"<?xml version=\"1.0\" encoding=\"UTF-8\"?><w:hdr xmlns:w=\"{W}\" xmlns:v=\"urn:schemas-microsoft-com:vml\" xmlns:o=\"urn:schemas-microsoft-com:office:office\">{inner}</w:hdr>";

    private static string HeaderWithWatermark() => HeaderXml(WatermarkSdt());

    private static string PlainHeader() => HeaderXml("<w:p><w:r><w:t>Product header</w:t></w:r></w:p>");

    private static string FakeMarkerHeader() => HeaderXml("<w:p><w:r><w:rPr><w:vanish/></w:rPr><w:t>AeroLinkWatermark DRAFT</w:t></w:r></w:p>");

    private static string OffPageShapeHeader() => HeaderXml("<w:p><w:r><w:pict><v:shape id=\"AeroLinkWatermark\" type=\"#_x0000_t136\" fillcolor=\"#c8d0d8\"><v:textpath string=\"DRAFT\"/></v:shape></w:pict></w:r></w:p>");

    private static string WatermarkSdt() =>
        "<w:p><w:sdt><w:sdtPr><w:tag w:val=\"AeroLink.Watermark\"/></w:sdtPr><w:sdtContent><w:p><w:r><w:rPr><w:noProof/></w:rPr><w:pict>"
        + "<v:shape id=\"AeroLinkWatermark\" o:spid=\"_x0000_s2049\" type=\"#_x0000_t136\" style=\"position:absolute;margin-left:0;margin-top:0;width:468pt;height:117pt;rotation:315;z-index:-251658752;mso-position-horizontal:center;mso-position-horizontal-relative:margin;mso-position-vertical:center;mso-position-vertical-relative:margin\" o:allowincell=\"f\" fillcolor=\"#c8d0d8\" stroked=\"f\">"
        + "<v:textpath style=\"font-family:&quot;Calibri&quot;;font-size:1pt\" string=\"DRAFT\"/><v:fill opacity=\".45\"/></v:shape></w:pict></w:r></w:p></w:sdtContent></w:sdt></w:p>";

    private static string ReadPart(byte[] bytes, string name)
    {
        using var archive = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
        using var reader = new StreamReader(archive.GetEntry(name)!.Open(), Encoding.UTF8, true);
        return reader.ReadToEnd();
    }

    private static byte[] RewriteDocumentText(byte[] bytes, string from, string to)
    {
        using var output = new MemoryStream();
        output.Write(bytes);
        output.Position = 0;
        using (var archive = new ZipArchive(output, ZipArchiveMode.Update, true))
        {
            var entry = archive.GetEntry("word/document.xml")!;
            string xml;
            using (var reader = new StreamReader(entry.Open())) xml = reader.ReadToEnd();
            entry.Delete();
            var replacement = archive.CreateEntry("word/document.xml");
            using var writer = new StreamWriter(replacement.Open(), new UTF8Encoding(false));
            writer.Write(xml.Replace(from, to, StringComparison.Ordinal));
        }
        return output.ToArray();
    }

    private static byte[] RenameStoryPart(byte[] bytes, string from, string to)
    {
        using var output = new MemoryStream();
        output.Write(bytes);
        output.Position = 0;
        using (var archive = new ZipArchive(output, ZipArchiveMode.Update, true))
        {
            ReplaceEntryText(archive, "word/_rels/document.xml.rels", xml => xml.Replace($"Target=\"{from}\"", $"Target=\"{to}\"", StringComparison.Ordinal));
            ReplaceEntryText(archive, "[Content_Types].xml", xml => xml.Replace($"/word/{from}", $"/word/{to}", StringComparison.Ordinal));
            var entry = archive.GetEntry($"word/{from}")!;
            string content;
            using (var reader = new StreamReader(entry.Open())) content = reader.ReadToEnd();
            entry.Delete();
            var replacement = archive.CreateEntry($"word/{to}");
            using var writer = new StreamWriter(replacement.Open(), new UTF8Encoding(false));
            writer.Write(content);
        }
        return output.ToArray();
    }

    private static void ReplaceEntryText(ZipArchive archive, string entryName, Func<string, string> transform)
    {
        var entry = archive.GetEntry(entryName)!;
        string content;
        using (var reader = new StreamReader(entry.Open())) content = reader.ReadToEnd();
        entry.Delete();
        var replacement = archive.CreateEntry(entryName);
        using var writer = new StreamWriter(replacement.Open(), new UTF8Encoding(false));
        writer.Write(transform(content));
    }
}
