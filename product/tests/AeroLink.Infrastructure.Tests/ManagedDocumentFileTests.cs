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
    public void Release_candidate_rejects_visible_draft_markings_even_without_a_watermark()
    {
        var bytes = ProfessionalPublicationRenderer.Render(Publication("Draft", null), "docx", "SDP-000001.01").Content;

        var error = Assert.Throws<DomainException>(() => ManagedDocumentFileService.ValidateReleaseDocx(bytes));

        Assert.Contains("visible Draft status", error.Message);
    }

    [Fact]
    public void Starting_the_next_revision_updates_current_control_metadata_but_preserves_history()
    {
        var original = ProfessionalPublicationRenderer.Render(Publication("Draft", "DRAFT", "00"), "docx", "SDP-000001.00").Content;

        var updated = ManagedDocumentFileService.PrepareNextRevisionDraft(original, "SDP-000001", 0, 1);
        using var archive = new System.IO.Compression.ZipArchive(new MemoryStream(updated), System.IO.Compression.ZipArchiveMode.Read);
        var documentXml = Read(archive, "word/document.xml");
        var footerXml = Read(archive, "word/footer1.xml");

        Assert.Contains("SDP-000001  |  REVISION 01", documentXml);
        Assert.Contains("<w:t xml:space=\"preserve\">Revision</w:t>", documentXml);
        Assert.Contains("<w:t xml:space=\"preserve\">01</w:t>", documentXml);
        Assert.Contains("SDP-000001 Rev 01 |", footerXml);
        Assert.Contains("<w:t xml:space=\"preserve\">00</w:t>", documentXml);
        Assert.True(ManagedDocumentFileService.ContainsDraftWatermark(updated));
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

    private static string Read(System.IO.Compression.ZipArchive archive, string name)
    {
        using var reader = new StreamReader(archive.GetEntry(name)!.Open());
        return reader.ReadToEnd();
    }
}
