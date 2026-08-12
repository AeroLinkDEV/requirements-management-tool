using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using AeroLink.Domain.Common;
using AeroLink.Domain.Requirements;

namespace AeroLink.Infrastructure.Persistence;

public sealed class ManagedDocumentFileService(EvidenceFileStore files)
{
    public const string DocxContentType = "application/vnd.openxmlformats-officedocument.wordprocessingml.document";
    public const string PdfContentType = "application/pdf";
    public const int MaximumDocumentBytes = 100 * 1024 * 1024;
    public const string SuccessorTransformationProfile = "aerolink-managed-document-successor-v1";

    public async Task<byte[]> ReadDocxAsync(Stream input, string fileName, bool requireDraftWatermark, CancellationToken ct)
    {
        if (!string.Equals(Path.GetExtension(fileName), ".docx", StringComparison.OrdinalIgnoreCase))
            throw new DomainException("Documentation Center accepts macro-free Word .docx files only.");
        var bytes = await ReadLimitedAsync(input, ct);
        ValidateDocx(bytes, requireDraftWatermark);
        return bytes;
    }

    public static void ValidateDocx(byte[] bytes, bool requireDraftWatermark)
    {
        try
        {
            using var stream = new MemoryStream(bytes, false);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read, false);
            if (archive.GetEntry("[Content_Types].xml") is null || archive.GetEntry("word/document.xml") is null)
                throw new DomainException("The selected file is not a valid Word document.");
            if (archive.Entries.Any(entry => IsUnsafeEntry(entry.FullName)))
                throw new DomainException("The Word package contains an unsafe path or macro-enabled content.");

            var contentTypes = ReadText(archive.GetEntry("[Content_Types].xml")!);
            if (contentTypes.Contains("macroEnabled", StringComparison.OrdinalIgnoreCase) ||
                contentTypes.Contains("vbaProject", StringComparison.OrdinalIgnoreCase))
                throw new DomainException("Macro-enabled Word documents are not accepted.");

            var headers = archive.Entries.Where(entry => entry.FullName.StartsWith("word/header", StringComparison.OrdinalIgnoreCase)
                && entry.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)).ToList();
            if (requireDraftWatermark && (headers.Count == 0 || headers.Any(header =>
                    !ReadText(header).Contains("AeroLinkWatermark", StringComparison.Ordinal) ||
                    !ReadText(header).Contains("DRAFT", StringComparison.OrdinalIgnoreCase))))
                throw new DomainException("Every draft section must retain the faint DRAFT watermark. Reopen the AeroLink working copy and check it in again.");
        }
        catch (InvalidDataException)
        {
            throw new DomainException("The selected file is not a valid Word document.");
        }
    }

    public static void ValidatePdf(byte[] bytes)
    {
        if (bytes.Length < 5 || !Encoding.ASCII.GetString(bytes, 0, 5).Equals("%PDF-", StringComparison.Ordinal))
            throw new DomainException("The release rendition is not a valid PDF file.");
    }

    public static bool ContainsDraftWatermark(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, false);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read, false);
        return archive.Entries
            .Where(entry => entry.FullName.StartsWith("word/header", StringComparison.OrdinalIgnoreCase) && entry.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
            .Any(entry => { var text = ReadText(entry); return text.Contains("AeroLinkWatermark", StringComparison.Ordinal) && text.Contains("DRAFT", StringComparison.OrdinalIgnoreCase); });
    }

    public static void ValidateReleaseDocx(byte[] bytes)
    {
        ValidateDocx(bytes, requireDraftWatermark: false);
        if (ContainsDraftWatermark(bytes))
            throw new DomainException("The release DOCX still contains a DRAFT watermark.");

        using var stream = new MemoryStream(bytes, false);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read, false);
        var stillMarkedDraft = archive.Entries
            .Where(entry => entry.FullName.Equals("word/document.xml", StringComparison.OrdinalIgnoreCase)
                || entry.FullName.StartsWith("word/header", StringComparison.OrdinalIgnoreCase)
                || entry.FullName.StartsWith("word/footer", StringComparison.OrdinalIgnoreCase))
            .Where(entry => entry.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
            .SelectMany(entry => XDocument.Parse(ReadText(entry)).Descendants())
            .Any(element => element.Name.LocalName == "t" && element.Value.Trim().Equals("Draft", StringComparison.OrdinalIgnoreCase));
        if (stillMarkedDraft)
            throw new DomainException("The release DOCX still contains a visible Draft status marking. Prepare the release candidate again.");
    }

    public static byte[] PrepareNextRevisionDraft(byte[] bytes, string documentNumber, int previousRevision, int nextRevision)
    {
        ValidateReleaseDocx(bytes);
        using var output = new MemoryStream();
        output.Write(bytes);
        output.Position = 0;
        using (var archive = new ZipArchive(output, ZipArchiveMode.Update, true))
        {
            var previous = previousRevision.ToString("D2");
            var next = nextRevision.ToString("D2");
            ReplaceEntryText(archive, "word/document.xml", xml =>
            {
                xml = xml.Replace($"{documentNumber}  |  REVISION {previous}", $"{documentNumber}  |  REVISION {next}", StringComparison.Ordinal);
                xml = ReplaceLabeledValue(xml, "Revision", previous, next);
                return ReplaceLabeledValue(xml, "Status", "Released", "Draft");
            });
            foreach (var footer in archive.Entries.Where(x => x.FullName.StartsWith("word/footer", StringComparison.OrdinalIgnoreCase) && x.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)).Select(x => x.FullName).ToList())
                ReplaceEntryText(archive, footer, xml => xml.Replace($"{documentNumber} Rev {previous} | Released |", $"{documentNumber} Rev {next} | Draft |", StringComparison.Ordinal));
            foreach (var header in archive.Entries.Where(x => x.FullName.StartsWith("word/header", StringComparison.OrdinalIgnoreCase) && x.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)).Select(x => x.FullName).ToList())
                ReplaceEntryText(archive, header, AddDraftWatermark);
        }
        var result = output.ToArray();
        ValidateDocx(result, requireDraftWatermark: true);
        return result;
    }

    private const string DraftWatermarkXml = "<w:r><w:rPr><w:noProof/></w:rPr><w:pict><v:shape id=\"AeroLinkWatermark\" o:spid=\"_x0000_s2049\" type=\"#_x0000_t136\" style=\"position:absolute;margin-left:0;margin-top:0;width:468pt;height:117pt;rotation:315;z-index:-251658752;mso-position-horizontal:center;mso-position-horizontal-relative:margin;mso-position-vertical:center;mso-position-vertical-relative:margin\" o:allowincell=\"f\" fillcolor=\"#c8d0d8\" stroked=\"f\"><v:textpath style=\"font-family:&quot;Calibri&quot;;font-size:1pt\" string=\"DRAFT\"/><v:fill opacity=\".45\"/></v:shape></w:pict></w:r>";

    private static string AddDraftWatermark(string xml)
    {
        if (xml.Contains("AeroLinkWatermark", StringComparison.Ordinal)) return xml;
        var rootStart = xml.IndexOf("<w:hdr", StringComparison.Ordinal); var rootEnd = rootStart < 0 ? -1 : xml.IndexOf('>', rootStart);
        if (rootEnd < 0) return xml;
        if (!xml[rootStart..rootEnd].Contains("xmlns:v=", StringComparison.Ordinal)) { xml = xml.Insert(rootEnd, " xmlns:v=\"urn:schemas-microsoft-com:vml\""); rootEnd = xml.IndexOf('>', rootStart); }
        if (!xml[rootStart..rootEnd].Contains("xmlns:o=", StringComparison.Ordinal)) xml = xml.Insert(rootEnd, " xmlns:o=\"urn:schemas-microsoft-com:office:office\"");
        var paragraphProperties = xml.IndexOf("</w:pPr>", StringComparison.Ordinal);
        return paragraphProperties >= 0 ? xml.Insert(paragraphProperties + "</w:pPr>".Length, DraftWatermarkXml) : xml.Replace("<w:p>", "<w:p>" + DraftWatermarkXml, StringComparison.Ordinal);
    }

    private static string ReplaceLabeledValue(string xml, string label, string previous, string next)
    {
        var controlLabel = $"<w:t xml:space=\"preserve\">{label}</w:t>"; var labelIndex = xml.IndexOf(controlLabel, StringComparison.Ordinal);
        if (labelIndex < 0) return xml;
        var previousValue = $"<w:t xml:space=\"preserve\">{previous}</w:t>"; var nextValue = $"<w:t xml:space=\"preserve\">{next}</w:t>";
        var valueIndex = xml.IndexOf(previousValue, labelIndex + controlLabel.Length, StringComparison.Ordinal);
        return valueIndex < 0 ? xml : string.Concat(xml.AsSpan(0, valueIndex), nextValue, xml.AsSpan(valueIndex + previousValue.Length));
    }

    public async Task<ControlledAttachment> StoreAsync(Guid projectId, Guid documentId, Guid revisionId,
        Guid logicalId, int version, string label, string description, string fileName, string contentType,
        byte[] content, Guid? supersedesId, string actor, DateTimeOffset now, CancellationToken ct)
    {
        await using var source = new MemoryStream(content, false);
        var stored = await files.StoreAsync(source, fileName, contentType, ct);
        return new ControlledAttachment(projectId, "ManagedDocument", documentId, revisionId, logicalId, version,
            label, description, stored.OriginalFileName, stored.ContentType, stored.Size, stored.Sha256,
            stored.StorageKey, supersedesId, actor, now);
    }

    public static string Sha256(byte[] content) => Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();

    private static bool IsUnsafeEntry(string name)
    {
        var normalized = name.Replace('\\', '/');
        return normalized.StartsWith('/') || normalized.Contains("../", StringComparison.Ordinal) ||
            normalized.Contains("/vbaProject", StringComparison.OrdinalIgnoreCase) ||
            normalized.EndsWith("vbaData.xml", StringComparison.OrdinalIgnoreCase);
    }

    private static string ReadText(ZipArchiveEntry entry)
    {
        using var reader = new StreamReader(entry.Open(), Encoding.UTF8, true, leaveOpen: false);
        return reader.ReadToEnd();
    }

    private static void ReplaceEntryText(ZipArchive archive, string entryName, Func<string, string> transform)
    {
        var entry = archive.GetEntry(entryName) ?? throw new DomainException("The Word document is missing required control metadata.");
        var original = ReadText(entry);
        var updated = transform(original);
        entry.Delete();
        var replacement = archive.CreateEntry(entryName, CompressionLevel.Optimal);
        using var writer = new StreamWriter(replacement.Open(), new UTF8Encoding(false));
        writer.Write(updated);
    }

    private static async Task<byte[]> ReadLimitedAsync(Stream input, CancellationToken ct)
    {
        using var output = new MemoryStream();
        var buffer = new byte[81920];
        int read;
        while ((read = await input.ReadAsync(buffer, ct)) > 0)
        {
            if (output.Length + read > MaximumDocumentBytes)
                throw new DomainException("Word documents are limited to 100 MB.");
            await output.WriteAsync(buffer.AsMemory(0, read), ct);
        }
        if (output.Length == 0) throw new DomainException("The Word document cannot be empty.");
        return output.ToArray();
    }
}
