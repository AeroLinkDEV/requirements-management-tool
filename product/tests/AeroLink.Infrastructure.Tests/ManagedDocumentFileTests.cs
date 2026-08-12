using AeroLink.Domain.Common;
using AeroLink.Infrastructure.Persistence;

namespace AeroLink.Infrastructure.Tests;

public sealed class ManagedDocumentFileTests
{
    [Fact]
    public void Draft_renderer_places_the_named_watermark_in_the_word_header()
    {
        var bytes = ProfessionalPublicationRenderer.Render(Publication("Draft", "DRAFT"), "docx", "SDP-000001.01").Content;
        ManagedDocumentFileService.ValidateDocx(bytes, requireDraftWatermark: true);
        Assert.True(ManagedDocumentFileService.ContainsDraftWatermark(bytes));
    }

    [Fact]
    public void Released_word_source_is_clean_and_is_rejected_as_a_draft_check_in()
    {
        var bytes = ProfessionalPublicationRenderer.Render(Publication("Released", null), "docx", "SDP-000001.00").Content;
        ManagedDocumentFileService.ValidateDocx(bytes, requireDraftWatermark: false);
        ManagedDocumentFileService.ValidateReleaseDocx(bytes);
        Assert.False(ManagedDocumentFileService.ContainsDraftWatermark(bytes));
        Assert.Contains("DRAFT watermark", Assert.Throws<DomainException>(() => ManagedDocumentFileService.ValidateDocx(bytes, true)).Message);
    }

    [Fact]
    public void Release_candidate_rejects_a_controlled_draft_status_even_without_a_watermark()
    {
        var bytes = ProfessionalPublicationRenderer.Render(ControlledPublication("Draft", null, "01"), "docx", "SDP-000001.01").Content;

        var validation = ManagedDocumentFileService.ValidateReleaseTransformation(bytes, bytes, "SDP-000001", 1);

        Assert.False(validation.IsValid);
        Assert.Equal("invalid_released_status", validation.Code);
    }

    [Fact]
    public void Starting_the_next_revision_updates_controlled_metadata_but_preserves_history()
    {
        var original = ProfessionalPublicationRenderer.Render(Publication("Released", null, "00"), "docx", "SDP-000001.00").Content;

        var updated = ManagedDocumentFileService.PrepareNextRevisionDraft(original, "SDP-000001", 0, 1);
        var fields = WordDocumentStructure.ControlledFields(updated);
        Assert.NotEmpty(fields.DocumentNumbers);
        Assert.All(fields.DocumentNumbers, number => Assert.Equal("SDP-000001", number));
        Assert.NotEmpty(fields.Revisions);
        Assert.All(fields.Revisions, value => Assert.Equal("01", value));
        Assert.NotEmpty(fields.Statuses);
        Assert.All(fields.Statuses, status => Assert.Contains("Draft", status, StringComparison.OrdinalIgnoreCase));
        using var archive = new System.IO.Compression.ZipArchive(new MemoryStream(updated), System.IO.Compression.ZipArchiveMode.Read);
        var documentText = WordDocumentStructure.NormalizedPartText(Read(archive, "word/document.xml"));
        var footerText = WordDocumentStructure.NormalizedPartText(Read(archive, "word/footer1.xml"));

        Assert.Contains("SDP-000001  |  REVISION 01", documentText);
        Assert.Contains("SDP-000001 Rev 01 | Draft |", footerText);
        Assert.Contains("00", documentText);
        Assert.True(ManagedDocumentFileService.ContainsDraftWatermark(updated));
        ManagedDocumentFileService.ValidateDocx(updated, requireDraftWatermark: true);
    }

    [Fact]
    public void Starting_a_successor_adds_valid_watermark_namespaces_to_a_clean_word_header()
    {
        var original = ProfessionalPublicationRenderer.Render(Publication("Released", null, "00"), "docx", "SDP-000001.00").Content;
        using var buffer = new MemoryStream(); buffer.Write(original); buffer.Position = 0;
        using (var archive = new System.IO.Compression.ZipArchive(buffer, System.IO.Compression.ZipArchiveMode.Update, true))
        {
            var header = archive.GetEntry("word/header1.xml")!; string xml; using (var reader = new StreamReader(header.Open())) xml = reader.ReadToEnd();
            header.Delete(); var replacement = archive.CreateEntry("word/header1.xml"); using var writer = new StreamWriter(replacement.Open()); writer.Write(xml.Replace(" xmlns:v=\"urn:schemas-microsoft-com:vml\"", "").Replace(" xmlns:o=\"urn:schemas-microsoft-com:office:office\"", ""));
        }
        var updated = ManagedDocumentFileService.PrepareNextRevisionDraft(buffer.ToArray(), "SDP-000001", 0, 1);
        using var result = new System.IO.Compression.ZipArchive(new MemoryStream(updated), System.IO.Compression.ZipArchiveMode.Read);
        _ = System.Xml.Linq.XDocument.Parse(Read(result, "word/header1.xml"));
    }

    [Fact]
    public void Starting_a_successor_creates_and_relates_a_watermarked_header_when_the_release_has_none()
    {
        var original = ProfessionalPublicationRenderer.Render(Publication("Released", null, "00"), "docx", "SDP-000001.00").Content;
        using var buffer = new MemoryStream(); buffer.Write(original); buffer.Position = 0;
        using (var archive = new System.IO.Compression.ZipArchive(buffer, System.IO.Compression.ZipArchiveMode.Update, true))
        {
            archive.GetEntry("word/header1.xml")!.Delete();
            RemoveElements(archive, "[Content_Types].xml", x => x.Name.LocalName == "Override" && (string?)x.Attribute("PartName") == "/word/header1.xml");
            RemoveElements(archive, "word/_rels/document.xml.rels", x => x.Name.LocalName == "Relationship" && (string?)x.Attribute("Target") == "header1.xml");
            RemoveElements(archive, "word/document.xml", x => x.Name.LocalName == "headerReference");
        }

        var updated = ManagedDocumentFileService.PrepareNextRevisionDraft(buffer.ToArray(), "SDP-000001", 0, 1);
        using var result = new System.IO.Compression.ZipArchive(new MemoryStream(updated), System.IO.Compression.ZipArchiveMode.Read);

        Assert.Contains("AeroLinkWatermark", Read(result, "word/headerAeroLink.xml"));
        Assert.Contains("headerAeroLink.xml", Read(result, "word/_rels/document.xml.rels"));
        Assert.Contains("rIdAeroLinkDraftHeader", Read(result, "word/document.xml"));
        Assert.Contains("/word/headerAeroLink.xml", Read(result, "[Content_Types].xml"));
        ManagedDocumentFileService.ValidateDocx(updated, requireDraftWatermark: true);
    }

    [Theory]
    [InlineData("SDP-000001.01.docm")]
    [InlineData("SDP-000001.01.pdf")]
    public async Task Connector_accepts_only_macro_free_docx_extension(string fileName)
    {
        var root = Path.Combine(Path.GetTempPath(), $"aerolink-doc-files-{Guid.NewGuid():N}");
        try
        {
            var service = new ManagedDocumentFileService(new EvidenceFileStore(root));
            await Assert.ThrowsAsync<DomainException>(() => service.ReadDocxAsync(new MemoryStream([1, 2, 3]), fileName, true, default));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private static ProfessionalPublication Publication(string status, string? watermark, string? revision = null) => new("AeroLink", "FMS", "FMS Product Development", "Software Development Plan", "FMS Software Development Plan", "Controlled project document", "SDP-000001", revision ?? (status == "Draft" ? "01" : "00"), status, "1.6", "FMS-1.6", "software.author", DateTimeOffset.UnixEpoch, new string('a', 64), [], [], [("00", "Released", "2026-01-01", "software.lead")], [new("Purpose", "Scope", [new("1", "Plan", "Purpose", "Controlled content.", [])])]) { Watermark = watermark };

    private static ProfessionalPublication ControlledPublication(string status, string? watermark, string? revision = null) =>
        Publication(status, watermark, revision) with { ControlledStatusControls = true };

    private static string Read(System.IO.Compression.ZipArchive archive, string name)
    {
        using var reader = new StreamReader(archive.GetEntry(name)!.Open());
        return reader.ReadToEnd();
    }

    private static void RemoveElements(System.IO.Compression.ZipArchive archive, string name, Func<System.Xml.Linq.XElement, bool> predicate)
    {
        var entry = archive.GetEntry(name)!; string xml; using (var reader = new StreamReader(entry.Open())) xml = reader.ReadToEnd();
        var document = System.Xml.Linq.XDocument.Parse(xml); foreach (var element in document.Descendants().Where(predicate).ToList()) element.Remove();
        entry.Delete(); var replacement = archive.CreateEntry(name); using var writer = new StreamWriter(replacement.Open()); writer.Write(document.ToString(System.Xml.Linq.SaveOptions.DisableFormatting));
    }
}
