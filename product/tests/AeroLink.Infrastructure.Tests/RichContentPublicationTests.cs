using System.IO.Compression;
using System.Text;
using AeroLink.Domain.Content;
using AeroLink.Infrastructure.Persistence;

namespace AeroLink.Infrastructure.Tests;

/// <summary>
/// A generated document is what an approver signs and what an auditor reads years later. What is asserted
/// here is that the tables and figures an author wrote reach that document, and that when one of them cannot
/// be retrieved the document says so rather than quietly omitting it.
/// </summary>
public sealed class RichContentPublicationTests
{
    /// <summary>A two-by-two PNG: red, green on the top row, blue, white below.</summary>
    private static byte[] Png()
    {
        var raw = new byte[] // one filter byte per scanline, then RGB triples
        {
            0, 255, 0, 0, 0, 255, 0,
            0, 0, 0, 255, 255, 255, 255,
        };
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, true)) zlib.Write(raw);

        using var output = new MemoryStream();
        output.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        Chunk(output, "IHDR", [0, 0, 0, 2, 0, 0, 0, 2, 8, 2, 0, 0, 0]);
        Chunk(output, "IDAT", compressed.ToArray());
        Chunk(output, "IEND", []);
        return output.ToArray();

        static void Chunk(Stream target, string type, byte[] data)
        {
            Span<byte> length = stackalloc byte[4];
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(length, (uint)data.Length);
            target.Write(length);
            var body = Encoding.ASCII.GetBytes(type).Concat(data).ToArray();
            target.Write(body);
            target.Write(new byte[4]); // The decoder does not verify CRCs; a wrong one would not be read.
        }
    }

    [Fact]
    public void A_png_decodes_to_the_pixels_a_pdf_needs()
    {
        // PDF has no PNG filter, so a controlled PDF has to carry the pixels. Getting this wrong produces a
        // document that opens and shows a scrambled diagram, which is worse than one that shows none.
        Assert.True(PngImage.TryDecodeRgb(Png(), out var width, out var height, out var rgb));
        Assert.Equal(2, width);
        Assert.Equal(2, height);
        Assert.Equal([255, 0, 0, 0, 255, 0, 0, 0, 255, 255, 255, 255], rgb);
    }

    [Fact]
    public void Dimensions_are_readable_without_decoding_the_image()
    {
        Assert.Equal((2, 2), PngImage.Size(Png()));
    }

    [Theory]
    [InlineData(new byte[] { 0xFF, 0xD8, 0xFF })]
    [InlineData(new byte[] { })]
    public void Something_that_is_not_a_png_is_reported_as_such_rather_than_throwing(byte[] bytes)
    {
        // One unreadable image must not stop a document that is otherwise complete from being produced.
        Assert.False(PngImage.IsPng(bytes));
        Assert.False(PngImage.TryDecodeRgb(bytes, out _, out _, out _));
    }

    [Fact]
    public void A_truncated_png_is_refused_rather_than_half_decoded()
    {
        var truncated = Png()[..30];
        Assert.True(PngImage.IsPng(truncated));
        Assert.False(PngImage.TryDecodeRgb(truncated, out _, out _, out _));
    }

    [Fact]
    public void An_authored_image_reaches_the_document_as_its_bytes()
    {
        var id = Guid.NewGuid();
        var stored = RichContent.Canonicalize(
            $$"""{"blocks":[{"type":"image","attachmentId":"{{id}}","alt":"Bus timing","caption":"Figure 1"}]}""");
        var prepared = RichContentPublisher.ForPublication(stored,
            new Dictionary<Guid, string> { [id] = "data:image/png;base64,AAAA" });

        Assert.Contains("\"dataUri\":\"data:image/png;base64,AAAA\"", prepared);
        Assert.Contains("\"caption\":\"Figure 1\"", prepared);
    }

    [Fact]
    public void An_authored_image_width_reaches_publication_without_becoming_markup()
    {
        var id = Guid.NewGuid();
        var stored = RichContent.Canonicalize(
            $$"""{"blocks":[{"type":"image","attachmentId":"{{id}}","alt":"Bus timing","widthPercent":50}]}""");
        var prepared = RichContentPublisher.ForPublication(stored,
            new Dictionary<Guid, string> { [id] = "data:image/png;base64,AAAA" });

        Assert.Contains("\"widthPercent\":50", prepared);
        Assert.DoesNotContain("<img", prepared);
    }

    [Fact]
    public void An_image_whose_file_is_gone_becomes_visible_text_not_a_silent_gap()
    {
        var stored = RichContent.Canonicalize(
            $$"""{"blocks":[{"type":"image","attachmentId":"{{Guid.NewGuid()}}","alt":"Bus timing","caption":"Figure 1"}]}""");

        // A document with a visible gap is recoverable. A document with an invisible one is not: nobody
        // reading it can tell that a figure the author wrote was ever meant to be there.
        var prepared = RichContentPublisher.ForPublication(stored, new Dictionary<Guid, string>());
        Assert.Contains("Image not retrieved: Figure 1", prepared);
        Assert.DoesNotContain("dataUri", prepared);
    }

    [Fact]
    public void A_table_reaches_the_document_with_its_rows_intact()
    {
        var stored = RichContent.Canonicalize(
            """{"blocks":[{"type":"table","caption":"Modes","rows":[["Mode","Value"],["Cruise","250"]]}]}""");
        var prepared = RichContentPublisher.ForPublication(stored, new Dictionary<Guid, string>());

        Assert.Contains("\"type\":\"table\"", prepared);
        Assert.Contains("[\"Cruise\",\"250\"]", prepared);
    }

    [Fact]
    public void Content_that_was_never_authored_prepares_to_nothing()
    {
        Assert.Equal(RichContent.Empty, RichContentPublisher.ForPublication(null, new Dictionary<Guid, string>()));
    }

    [Fact]
    public void Docx_preserves_each_adjacent_image_occurrence_while_deduplicating_bytes()
    {
        var uri = "data:image/png;base64," + Convert.ToBase64String(Png());
        var rich = "{\"blocks\":["
            + $"{{\"type\":\"image\",\"dataUri\":\"{uri}\",\"alt\":\"First alt\",\"caption\":\"First caption\",\"widthPercent\":40}},"
            + $"{{\"type\":\"image\",\"dataUri\":\"{uri}\",\"alt\":\"Second alt\",\"caption\":\"Second caption\",\"widthPercent\":80}}]}}";
        var output = ProfessionalPublicationRenderer.Render(Publication(rich), "docx", "inline-images");

        using var zip = new ZipArchive(new MemoryStream(output.Content), ZipArchiveMode.Read);
        var document = Read(zip, "word/document.xml");
        var relationships = Read(zip, "word/_rels/document.xml.rels");
        var media = zip.Entries.Count(entry => entry.FullName.StartsWith("word/media/", StringComparison.Ordinal));

        Assert.Equal(1, media); // identical bytes are one package asset, not one asset per occurrence
        Assert.Equal(1, relationships.Split("Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/image\"").Length - 1);
        Assert.Contains("cx=\"2160000\"", document); // 40% of the document width
        Assert.Contains("cx=\"4320000\"", document); // 80% of the document width
        Assert.True(document.IndexOf("descr=\"First alt\"", StringComparison.Ordinal) < document.IndexOf("First caption", StringComparison.Ordinal));
        Assert.True(document.IndexOf("First caption", StringComparison.Ordinal) < document.IndexOf("descr=\"Second alt\"", StringComparison.Ordinal));
        Assert.Contains("Second caption", document);

        // The package asset is intentionally shared, but each visual placement must still own a unique
        // wp:docPr and pic:cNvPr ID. Word treats those IDs as drawing identities, not media identities.
        var docPrIds = System.Text.RegularExpressions.Regex.Matches(document, "<wp:docPr id=\"(\\d+)\"")
            .Select(match => match.Groups[1].Value).ToArray();
        var cNvPrIds = System.Text.RegularExpressions.Regex.Matches(document, "<pic:cNvPr id=\"(\\d+)\"")
            .Select(match => match.Groups[1].Value).ToArray();
        Assert.Equal(2, docPrIds.Length);
        Assert.Equal(docPrIds.Length, docPrIds.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(2, cNvPrIds.Length);
        Assert.Equal(cNvPrIds.Length, cNvPrIds.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(docPrIds.Order(StringComparer.Ordinal), cNvPrIds.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Pdf_preserves_each_adjacent_image_occurrence_metadata_and_order()
    {
        var uri = "data:image/png;base64," + Convert.ToBase64String(Png());
        var rich = "{\"blocks\":["
            + $"{{\"type\":\"image\",\"dataUri\":\"{uri}\",\"alt\":\"First alt\",\"caption\":\"First caption\",\"widthPercent\":40}},"
            + $"{{\"type\":\"image\",\"dataUri\":\"{uri}\",\"alt\":\"Second alt\",\"caption\":\"Second caption\",\"widthPercent\":80}}]}}";
        var output = ProfessionalPublicationRenderer.Render(Publication(rich), "pdf", "inline-images");
        var pdf = Encoding.ASCII.GetString(output.Content);

        Assert.Equal(1, Count(pdf, "/Subtype /Image"));
        Assert.Equal(2, Count(pdf, "/Im1 Do"));
        Assert.Contains("192 0 0 192", pdf); // 40% occurrence
        Assert.Contains("384 0 0 384", pdf); // 80% occurrence
        Assert.True(pdf.IndexOf("First caption", StringComparison.Ordinal) < pdf.IndexOf("Second caption", StringComparison.Ordinal));
        Assert.True(pdf.IndexOf("First alt", StringComparison.Ordinal) < pdf.IndexOf("Second alt", StringComparison.Ordinal));
    }

    private static ProfessionalPublication Publication(string rich) => new(
        "FMS", "Flight Management System (FMS)", "FMS Showcase", "Problem Report", "Inline image test",
        "Controlled narrative", "PR-00001.00", "00", "Draft", "1.6", "Not yet baseline-effective", "test.engineer",
        new DateTimeOffset(2026, 8, 30, 12, 0, 0, TimeSpan.Zero), new string('a', 64), [], [], [],
        [new PublicationSection("Narrative", "", [new PublicationRecord("Problem", "Narrative", "Problem", "", [], rich)])]);

    private static string Read(ZipArchive zip, string name)
    {
        using var stream = zip.GetEntry(name)!.Open();
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private static int Count(string value, string token) =>
        value.Split(token, StringSplitOptions.None).Length - 1;
}
