using System.Text;
using AeroLink.Domain.Common;
using AeroLink.Infrastructure.Persistence;

namespace AeroLink.Infrastructure.Tests;

public sealed class PdfReleaseProfileTests
{
    [Fact]
    public void A_normal_single_export_pdf_passes_the_profile()
    {
        var bytes = ProfessionalPublicationRenderer.Render(Publication(), "pdf", "SDP-000001.00").Content;
        var validation = PdfReleaseProfile.Validate(bytes);
        Assert.True(validation.IsValid, validation.Message);
    }

    [Fact]
    public void A_multi_page_pdf_passes_the_profile()
    {
        var publication = new ProfessionalPublication("AeroLink", "FMS", "FMS Product Development", "Software Development Plan",
            "FMS Software Development Plan", "Controlled project document", "SDP-000001", "00", "Released", "1.6", "FMS-1.6",
            "software.author", DateTimeOffset.UnixEpoch, new string('a', 64), [], [],
            [("00", "Released", "2026-01-01", "software.lead")],
            [new("Purpose", "Scope", [new("1", "Plan", "Purpose", "Page one content.", [])]),
             new("Design", "Scope", [new("2", "Design", "Purpose", "Page two content.", [])])]);
        var bytes = ProfessionalPublicationRenderer.Render(publication, "pdf", "SDP-000001.00").Content;
        var validation = PdfReleaseProfile.Validate(bytes);
        Assert.True(validation.IsValid, validation.Message);
        Assert.True(bytes.Length > 2000);
    }

    [Fact]
    public void Prefix_only_fake_pdf_is_rejected()
    {
        var validation = PdfReleaseProfile.Validate(Encoding.ASCII.GetBytes("%PDF-not-a-pdf"));
        Assert.False(validation.IsValid);
        Assert.Equal("pdf_structure_invalid", validation.Code);
    }

    [Fact]
    public void Truncated_pdf_is_rejected()
    {
        var bytes = ProfessionalPublicationRenderer.Render(Publication(), "pdf", "SDP-000001.00").Content;
        var truncated = bytes[..^120];
        var validation = PdfReleaseProfile.Validate(truncated);
        Assert.False(validation.IsValid);
    }

    [Fact]
    public void Missing_trailer_and_root_is_rejected()
    {
        var bytes = TinyPdf(catalogExtra: "", extraObjects: [], trailerExtra: "", rootObject: 99);
        var validation = PdfReleaseProfile.Validate(bytes);
        Assert.False(validation.IsValid);
        Assert.Equal("pdf_structure_invalid", validation.Code);
    }

    [Fact]
    public void Zero_page_pdf_is_rejected()
    {
        var bytes = TinyPdf(catalogExtra: "", extraObjects: [], trailerExtra: "", rootObject: 1, pageCount: 0);
        var validation = PdfReleaseProfile.Validate(bytes);
        Assert.False(validation.IsValid);
        Assert.Equal("pdf_no_pages", validation.Code);
    }

    [Fact]
    public void Encrypted_pdf_is_rejected()
    {
        var bytes = TinyPdf(catalogExtra: "", extraObjects: ["<< /Filter /Standard /V 1 /R 2 /O (owner) /U (user) /P -1 >>"], trailerExtra: "/Encrypt 6 0 R");
        var validation = PdfReleaseProfile.Validate(bytes);
        Assert.False(validation.IsValid);
        Assert.Contains(validation.Code, new[] { "pdf_encrypted", "pdf_structure_invalid" });
    }

    [Fact]
    public void Javascript_is_rejected_by_policy()
    {
        var bytes = TinyPdf(catalogExtra: "/Names << /JavaScript << /Names [(A) 6 0 R] >> >>",
            extraObjects: ["<< /S /JavaScript /JS (app.alert(1)) >>"], trailerExtra: "");
        var validation = PdfReleaseProfile.Validate(bytes);
        Assert.False(validation.IsValid);
        Assert.Equal("pdf_prohibited_feature", validation.Code);
    }

    [Fact]
    public void Launch_actions_are_rejected_by_policy()
    {
        var bytes = TinyPdf(catalogExtra: "/OpenAction 6 0 R", extraObjects: ["<< /S /Launch /F (evil.exe) >>"], trailerExtra: "");
        var validation = PdfReleaseProfile.Validate(bytes);
        Assert.False(validation.IsValid);
        Assert.Equal("pdf_prohibited_feature", validation.Code);
    }

    [Fact]
    public void Embedded_files_are_rejected_by_policy()
    {
        var bytes = TinyPdf(catalogExtra: "/Names << /EmbeddedFiles << /Names [(A) 6 0 R] >> >>",
            extraObjects: ["<< /Type /EmbeddedFile /Length 0 >>"], trailerExtra: "");
        var validation = PdfReleaseProfile.Validate(bytes);
        Assert.False(validation.IsValid);
        Assert.Equal("pdf_prohibited_feature", validation.Code);
    }

    [Fact]
    public void Acro_forms_are_rejected_by_policy()
    {
        var bytes = TinyPdf(catalogExtra: "/AcroForm 6 0 R", extraObjects: ["<< /Fields [] >>"], trailerExtra: "");
        var validation = PdfReleaseProfile.Validate(bytes);
        Assert.False(validation.IsValid);
        Assert.Equal("pdf_prohibited_feature", validation.Code);
    }

    [Fact]
    public void Trailing_polyglot_data_is_rejected()
    {
        var valid = ProfessionalPublicationRenderer.Render(Publication(), "pdf", "SDP-000001.00").Content;
        var polyglot = valid.Concat(Encoding.ASCII.GetBytes("MZ\r\nthis is executable data")).ToArray();
        var validation = PdfReleaseProfile.Validate(polyglot);
        Assert.False(validation.IsValid);
        Assert.Equal("pdf_structure_invalid", validation.Code);
    }

    [Fact]
    public void Corrupt_cross_reference_offsets_are_rejected()
    {
        var valid = ProfessionalPublicationRenderer.Render(Publication(), "pdf", "SDP-000001.00").Content;
        var text = Encoding.ASCII.GetString(valid);
        var corrupted = text.Replace("xref", "XREF", StringComparison.Ordinal);
        var validation = PdfReleaseProfile.Validate(Encoding.ASCII.GetBytes(corrupted));
        Assert.False(validation.IsValid);
        Assert.Equal("pdf_structure_invalid", validation.Code);
    }

    [Fact]
    public void Misleading_and_path_like_pdf_names_are_rejected_and_safe_names_normalized()
    {
        Assert.Throws<DomainException>(() => ManagedDocumentFileService.NormalizePdfFileName("approved-document.exe"));
        Assert.Throws<DomainException>(() => ManagedDocumentFileService.NormalizePdfFileName("approved-document.html"));
        Assert.Throws<DomainException>(() => ManagedDocumentFileService.NormalizePdfFileName("..\\approved.pdf"));
        Assert.Throws<DomainException>(() => ManagedDocumentFileService.NormalizePdfFileName("dir/approved.pdf"));
        Assert.Equal("approved.pdf", ManagedDocumentFileService.NormalizePdfFileName("approved.PDF"));
        Assert.Equal("r\u00e9v-\u00e9dition.pdf", ManagedDocumentFileService.NormalizePdfFileName("r\u00e9v-\u00e9dition.pdf"));
    }

    [Fact]
    public async Task Oversized_pdf_is_bounded_while_streaming()
    {
        var payload = new byte[64];
        await Assert.ThrowsAsync<PdfRenditionTooLargeException>(() =>
            ManagedDocumentFileService.ReadPdfAsync(new MemoryStream(payload), 32, default));
    }

    private static ProfessionalPublication Publication() =>
        new("AeroLink", "FMS", "FMS Product Development", "Software Development Plan", "FMS Software Development Plan",
            "Controlled project document", "SDP-000001", "00", "Released", "1.6", "FMS-1.6", "software.author",
            DateTimeOffset.UnixEpoch, new string('a', 64), [], [], [("00", "Released", "2026-01-01", "software.lead")],
            [new("Purpose", "Scope", [new("1", "Plan", "Purpose", "Controlled content.", [])])]);

    private static byte[] TinyPdf(string catalogExtra, IReadOnlyList<string> extraObjects, string trailerExtra,
        int rootObject = 1, int pageCount = 1)
    {
        var contentStart = 3 + pageCount;
        var fontNumber = 3 + pageCount * 2;
        var objects = new List<string>
        {
            $"<< /Type /Catalog /Pages 2 0 R {catalogExtra} >>",
            $"<< /Type /Pages /Kids [{string.Join(" ", Enumerable.Range(0, pageCount).Select(i => $"{3 + i} 0 R"))}] /Count {pageCount} >>"
        };
        for (var i = 0; i < pageCount; i++)
        {
            objects.Add($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 {fontNumber} 0 R >> >> /Contents {contentStart + i} 0 R >>");
        }
        for (var i = 0; i < pageCount; i++)
        {
            objects.Add("<< /Length 44 >>\nstream\nBT /F1 12 Tf 72 720 Td (Hello) Tj ET\nendstream");
        }
        objects.Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");
        objects.AddRange(extraObjects);

        using var output = new MemoryStream();
        void Write(string value) { var bytes = Encoding.ASCII.GetBytes(value); output.Write(bytes); }
        Write("%PDF-1.4\n%----\n");
        var offsets = new List<long>();
        for (var i = 0; i < objects.Count; i++)
        {
            offsets.Add(output.Position);
            Write($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }
        var xref = output.Position;
        Write($"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets) Write($"{offset:D10} 00000 n \n");
        Write($"trailer\n<< /Size {objects.Count + 1} /Root {rootObject} 0 R {trailerExtra} >>\nstartxref\n{xref}\n%%EOF");
        return output.ToArray();
    }
}
