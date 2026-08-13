using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using AeroLink.DocumentSecurity;
using AeroLink.Domain.Common;
using AeroLink.Domain.Requirements;

namespace AeroLink.Infrastructure.Persistence;

public sealed class ManagedDocumentFileService(EvidenceFileStore files)
{
    public const string DocxContentType = "application/vnd.openxmlformats-officedocument.wordprocessingml.document";
    public const string PdfContentType = "application/pdf";
    public const int MaximumDocumentBytes = 100 * 1024 * 1024;
    public const int MaximumPdfBytes = 100 * 1024 * 1024;
    public const string SuccessorTransformationProfile = "aerolink-managed-document-successor-v1";
    public const string ReleaseTransformationProfile = "aerolink-managed-document-release-v1";
    public const string ReleaseTransformationVersion = "1";

    private static readonly XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    private static readonly XNamespace R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private static readonly XNamespace PR = "http://schemas.openxmlformats.org/package/2006/relationships";
    private static readonly XNamespace CT = "http://schemas.openxmlformats.org/package/2006/content-types";

    public async Task<byte[]> ReadDocxAsync(Stream input, string fileName, bool requireDraftWatermark, CancellationToken ct)
    {
        if (!string.Equals(Path.GetExtension(fileName), ".docx", StringComparison.OrdinalIgnoreCase))
            throw new DomainException("Documentation Center accepts macro-free Word .docx files only.");
        var bytes = await ReadLimitedAsync(input, MaximumDocumentBytes, "Word documents are limited to 100 MB.", ct);
        ValidateDocx(bytes, requireDraftWatermark);
        return bytes;
    }

    /// <summary>
    /// Package-safety validation plus, for Draft check-ins, proof that every header variant Word can
    /// actually render carries the named, presentation-controlled DRAFT watermark. Package-entry string
    /// matching is deliberately not used: orphan, hidden, alternate-text and unrelated shapes can never
    /// satisfy the requirement, and unused parts can never fail it.
    /// </summary>
    public static void ValidateDocx(byte[] bytes, bool requireDraftWatermark)
    {
        ValidateSafeOoxml(bytes);
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
        }
        catch (InvalidDataException)
        {
            throw new DomainException("The selected file is not a valid Word document.");
        }

        if (!requireDraftWatermark) return;
        var parts = WordDocumentStructure.ReadWordParts(bytes);
        var resolution = WordDocumentStructure.ResolveHeaders(bytes);
        foreach (var section in resolution.Sections)
        {
            foreach (var (variant, part) in section.RequiredVariants())
            {
                if (part is null)
                    throw new DomainException($"Section {section.Index + 1} has no effective {variant} header, so its pages would render without the controlled DRAFT watermark.");
                if (!WordDocumentStructure.PartHasControlledDraftWatermark(parts[part]))
                    throw new DomainException($"Section {section.Index + 1} {variant} header is missing the controlled DRAFT watermark. Reopen the AeroLink working copy and check it in again.");
            }
        }
    }

    /// <summary>
    /// Streams an uploaded PDF to a bounded staging file while hashing and counting bytes, so an upload is
    /// never materialized in one large byte buffer. The caller owns the returned file and deletes it.
    /// </summary>
    public static Task<(string Path, string Sha256, long Size)> ReadPdfToStagedFileAsync(Stream input, CancellationToken ct) =>
        ReadPdfToStagedFileAsync(input, MaximumPdfBytes, ct);

    /// <summary>Streams an uploaded PDF to a bounded staging file under the given explicit limit.</summary>
    public static async Task<(string Path, string Sha256, long Size)> ReadPdfToStagedFileAsync(Stream input, int maximumBytes, CancellationToken ct)
    {
        var path = Path.Combine(Path.GetTempPath(), $"aerolink-pdf-candidate-{Guid.NewGuid():N}.upload");
        try
        {
            long size = 0;
            string hash;
            await using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = new byte[81920];
                int read;
                while ((read = await input.ReadAsync(buffer, ct)) > 0)
                {
                    if (size + read > maximumBytes) throw new PdfRenditionTooLargeException();
                    size += read;
                    sha.AppendData(buffer, 0, read);
                    await output.WriteAsync(buffer.AsMemory(0, read), ct);
                }
                if (size == 0) throw new DomainException("The PDF rendition cannot be empty.");
                hash = Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant();
            }
            return (path, hash, size);
        }
        catch
        {
            if (File.Exists(path)) File.Delete(path);
            throw;
        }
    }

    /// <summary>
    /// Requires a real .pdf file name, rejects path-like or control-character names, and normalizes the
    /// extension to lowercase so no release attachment is ever retained or downloaded under another extension.
    /// </summary>
    public static string NormalizePdfFileName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new DomainException("The PDF rendition must carry a .pdf file name.");
        var trimmed = name.Trim();
        if (trimmed.IndexOfAny(['\\', '/']) >= 0 || trimmed.Contains("..", StringComparison.Ordinal) || trimmed.Any(char.IsControl))
            throw new DomainException("The PDF rendition file name is not a safe single file name.");
        if (!string.Equals(Path.GetExtension(trimmed), ".pdf", StringComparison.OrdinalIgnoreCase))
            throw new DomainException("The release rendition must be uploaded with a .pdf file name.");
        return Path.GetFileName(Path.ChangeExtension(trimmed, ".pdf"));
    }

    /// <summary>True when any header variant Word actually renders carries the controlled DRAFT watermark.</summary>
    public static bool ContainsDraftWatermark(byte[] bytes)
    {
        var parts = WordDocumentStructure.ReadWordParts(bytes);
        var resolution = WordDocumentStructure.ResolveHeaders(bytes);
        return resolution.Sections.SelectMany(section => section.RequiredVariants())
            .Any(variant => variant.Part is not null && WordDocumentStructure.PartHasControlledDraftWatermark(parts[variant.Part]));
    }

    /// <summary>
    /// Basic released-state validation: no controlled DRAFT watermark in any effective header and no
    /// controlled status control that still reads Draft. This is the parent-source check used when a
    /// successor Draft is started; candidate uploads additionally pass the exact reviewed-source gate.
    /// </summary>
    public static void ValidateReleaseDocx(byte[] bytes)
    {
        ValidateDocx(bytes, requireDraftWatermark: false);
        var parts = WordDocumentStructure.ReadWordParts(bytes);
        var resolution = WordDocumentStructure.ResolveHeaders(bytes);
        foreach (var section in resolution.Sections)
        {
            foreach (var (variant, part) in section.RequiredVariants())
            {
                if (part is not null && WordDocumentStructure.PartHasControlledDraftWatermark(parts[part]))
                    throw new DomainException($"Section {section.Index + 1} {variant} header still contains the controlled DRAFT watermark. Prepare the release candidate again.");
            }
        }
        if (parts.Values.Any(WordDocumentStructure.PartHasControlledDraftStatus))
            throw new DomainException("The release DOCX still contains a visible controlled Draft status marking. Prepare the release candidate again.");
    }

    /// <summary>
    /// The exact reviewed-source gate for a connector release candidate. The candidate must carry the
    /// controlled Released status and the correct number/revision, contain no controlled watermark, and
    /// have technical content (story text outside the named controls, embedded binary resources, external
    /// hyperlink targets) identical to the reviewed snapshot. Any other difference fails closed.
    /// </summary>
    public static ReleaseTransformationValidation ValidateReleaseTransformation(byte[] reviewedSource, byte[] candidate, string documentNumber, int revision)
    {
        try
        {
            ValidateDocx(candidate, requireDraftWatermark: false);
        }
        catch (DomainException ex)
        {
            return ReleaseTransformationValidation.Reject("invalid_release_candidate", ex.Message);
        }

        var fields = WordDocumentStructure.ControlledFields(candidate);
        if (fields.Statuses.Count == 0 || fields.Statuses.Any(status => !status.Trim().Equals("Released", StringComparison.OrdinalIgnoreCase)))
            return ReleaseTransformationValidation.Reject("invalid_released_status",
                "Every controlled status field must read Released before the candidate can be signed.");
        var expectedRevision = revision.ToString("D2");
        if (fields.DocumentNumbers.Count == 0 || fields.DocumentNumbers.Any(number => !number.Trim().Equals(documentNumber, StringComparison.OrdinalIgnoreCase))
            || fields.Revisions.Count == 0 || fields.Revisions.Any(value => !value.Trim().Equals(expectedRevision, StringComparison.OrdinalIgnoreCase)))
            return ReleaseTransformationValidation.Reject("invalid_release_metadata",
                "The release candidate carries the wrong controlled document number or formal revision.");

        var parts = WordDocumentStructure.ReadWordParts(candidate);
        var resolution = WordDocumentStructure.ResolveHeaders(candidate);
        foreach (var section in resolution.Sections)
        {
            foreach (var (variant, part) in section.RequiredVariants())
            {
                if (part is null) continue;
                if (WordDocumentStructure.PartHasControlledDraftWatermark(parts[part]))
                    return ReleaseTransformationValidation.Reject("release_candidate_draft_watermark",
                        $"Section {section.Index + 1} {variant} header still carries the controlled DRAFT watermark.");
                if (WordDocumentStructure.PartHasControlledWatermarkShape(parts[part]))
                    return ReleaseTransformationValidation.Reject("release_candidate_watermark_present",
                        $"Section {section.Index + 1} {variant} header still contains the controlled watermark object.");
            }
        }

        var reviewedFingerprint = WordDocumentStructure.TechnicalContentFingerprint(reviewedSource);
        var candidateFingerprint = WordDocumentStructure.TechnicalContentFingerprint(candidate);
        if (!string.Equals(reviewedFingerprint, candidateFingerprint, StringComparison.OrdinalIgnoreCase))
            return ReleaseTransformationValidation.Reject("candidate_source_mismatch",
                "The release DOCX changed reviewed content outside the controlled status and watermark fields. Prepare it again from the exact reviewed snapshot.");
        return ReleaseTransformationValidation.Valid();
    }

    /// <summary>
    /// The reference release transformation: only the named status controls become Released and the named
    /// watermark objects are removed. Production releases use the Word connector, which performs the same
    /// tagged operations through the Word object model; this OOXML implementation exists so the server-side
    /// comparison can be tested against a deterministic, documented transform.
    /// </summary>
    public static byte[] ApplyReleaseMarking(byte[] reviewedDraft)
    {
        var fields = WordDocumentStructure.ControlledFields(reviewedDraft);
        var documentNumber = fields.DocumentNumbers.FirstOrDefault(number => number.Length > 0) ?? "";
        var revisionValue = fields.Revisions.FirstOrDefault(value => value.Length > 0) ?? "0";
        var revision = int.TryParse(revisionValue, out var parsed) ? parsed : 0;
        using var output = new MemoryStream();
        output.Write(reviewedDraft);
        output.Position = 0;
        using (var archive = new ZipArchive(output, ZipArchiveMode.Update, true))
        {
            foreach (var entry in archive.Entries.Where(entry => IsStoryXmlEntry(entry.FullName)).Select(entry => entry.FullName).ToList())
            {
                ReplaceEntryText(archive, entry, xml =>
                    WordDocumentStructure.RemoveControlledWatermarks(
                        WordDocumentStructure.SetControlValueInXml(xml, WordDocumentStructure.StatusTag, "Released")));
            }
        }
        var result = output.ToArray();
        var validation = ValidateReleaseTransformation(reviewedDraft, result, documentNumber, revision);
        if (!validation.IsValid) throw new DomainException(validation.Message);
        return result;
    }

    /// <summary>
    /// Turns a verified released DOCX into the next controlled Draft. New-model releases are updated through
    /// their named controls only; legacy released documents produced by the pre-control renderer are upgraded
    /// deterministically to the named-control structure. Historical evidence is never rewritten.
    /// </summary>
    public static byte[] PrepareNextRevisionDraft(byte[] bytes, string documentNumber, int previousRevision, int nextRevision)
    {
        ValidateReleaseDocx(bytes);
        var fields = WordDocumentStructure.ControlledFields(bytes);
        var legacy = fields.Statuses.Count == 0;
        using var output = new MemoryStream();
        output.Write(bytes);
        output.Position = 0;
        using (var archive = new ZipArchive(output, ZipArchiveMode.Update, true))
        {
            if (legacy)
                UpgradeLegacyManagedDocument(archive, bytes, documentNumber, previousRevision, nextRevision);
            else
                UpgradeNewModelDraft(archive, bytes, documentNumber, nextRevision);
        }
        var result = output.ToArray();
        ValidateDocx(result, requireDraftWatermark: true);
        return result;
    }

    private static void UpgradeNewModelDraft(ZipArchive archive, byte[] originalBytes, string documentNumber, int nextRevision)
    {
        foreach (var entry in archive.Entries.Where(entry => IsStoryXmlEntry(entry.FullName)).Select(entry => entry.FullName).ToList())
        {
            ReplaceEntryText(archive, entry, xml =>
                WordDocumentStructure.SetControlValueInXml(
                    WordDocumentStructure.SetControlValueInXml(
                        WordDocumentStructure.SetControlValueInXml(xml, WordDocumentStructure.StatusTag, "Draft"),
                        WordDocumentStructure.RevisionTag, nextRevision.ToString("D2")),
                    WordDocumentStructure.DocumentNumberTag, documentNumber));
        }
        EnsureWatermarksInEffectiveHeaders(archive, originalBytes);
    }

    private static void UpgradeLegacyManagedDocument(ZipArchive archive, byte[] originalBytes, string documentNumber, int previousRevision, int nextRevision)
    {
        UpdateXmlEntry(archive, "word/document.xml", document => UpgradeLegacyDocumentBody(document, documentNumber, previousRevision, nextRevision));
        foreach (var entry in archive.Entries.Where(entry => entry.FullName.StartsWith("word/footer", StringComparison.OrdinalIgnoreCase)
            && entry.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)).Select(entry => entry.FullName).ToList())
        {
            UpdateXmlEntry(archive, entry, footer => UpgradeLegacyFooter(footer, documentNumber, previousRevision, nextRevision));
        }
        EnsureWatermarksInEffectiveHeaders(archive, originalBytes);
    }

    private static void EnsureWatermarksInEffectiveHeaders(ZipArchive archive, byte[] originalBytes)
    {
        const string headerName = "word/headerAeroLink.xml";
        const string relationshipId = "rIdAeroLinkDraftHeader";
        var resolution = WordDocumentStructure.ResolveHeaders(originalBytes);
        var headerCreated = false;
        foreach (var section in resolution.Sections)
        {
            foreach (var (variant, part) in section.RequiredVariants())
            {
                if (part is not null)
                {
                    ReplaceEntryText(archive, part, WordDocumentStructure.EnsureControlledDraftWatermark);
                    continue;
                }
                if (!headerCreated)
                {
                    const string emptyHeader = "<?xml version=\"1.0\" encoding=\"UTF-8\"?><w:hdr xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\" xmlns:v=\"urn:schemas-microsoft-com:vml\" xmlns:o=\"urn:schemas-microsoft-com:office:office\"></w:hdr>";
                    WriteEntryText(archive, headerName, WordDocumentStructure.EnsureControlledDraftWatermark(emptyHeader));
                    UpdateXmlEntry(archive, "[Content_Types].xml", document =>
                    {
                        if (document.Root!.Elements(CT + "Override").Any(element =>
                            string.Equals((string?)element.Attribute("PartName"), "/" + headerName, StringComparison.OrdinalIgnoreCase))) return;
                        document.Root.Add(new XElement(CT + "Override",
                            new XAttribute("PartName", "/" + headerName),
                            new XAttribute("ContentType", "application/vnd.openxmlformats-officedocument.wordprocessingml.header+xml")));
                    });
                    var relationshipsName = "word/_rels/document.xml.rels";
                    if (archive.GetEntry(relationshipsName) is null)
                        WriteEntryText(archive, relationshipsName, $"<?xml version=\"1.0\" encoding=\"UTF-8\"?><Relationships xmlns=\"{PR}\"/>");
                    UpdateXmlEntry(archive, relationshipsName, document =>
                    {
                        if (document.Root!.Elements(PR + "Relationship").Any(element =>
                            string.Equals((string?)element.Attribute("Id"), relationshipId, StringComparison.Ordinal))) return;
                        document.Root.Add(new XElement(PR + "Relationship",
                            new XAttribute("Id", relationshipId),
                            new XAttribute("Type", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/header"),
                            new XAttribute("Target", "headerAeroLink.xml")));
                    });
                    headerCreated = true;
                }
                AddHeaderReference(archive, section.Index, variant, relationshipId);
            }
        }
    }

    private static void AddHeaderReference(ZipArchive archive, int sectionIndex, string variant, string relationshipId)
    {
        UpdateXmlEntry(archive, "word/document.xml", document =>
        {
            var body = document.Descendants(W + "body").SingleOrDefault() ?? throw new DomainException("The Word document is missing its body.");
            var sections = new List<XElement>();
            foreach (var paragraph in body.Descendants(W + "p"))
            {
                var nested = paragraph.Elements(W + "pPr").Elements(W + "sectPr").SingleOrDefault();
                if (nested is not null) sections.Add(nested);
            }
            var bodySection = body.Elements(W + "sectPr").LastOrDefault();
            if (bodySection is not null) sections.Add(bodySection);
            var target = sections[sectionIndex];
            if (!target.Elements(W + "headerReference").Any(reference =>
                string.Equals((string?)reference.Attribute(W + "type"), variant, StringComparison.OrdinalIgnoreCase)))
                target.AddFirst(new XElement(W + "headerReference",
                    new XAttribute(W + "type", variant),
                    new XAttribute(R + "id", relationshipId)));
        });
    }

    private static void UpgradeLegacyDocumentBody(XDocument document, string documentNumber, int previousRevision, int nextRevision)
    {
        var body = document.Descendants(W + "body").SingleOrDefault() ?? throw new DomainException("The Word document is missing its body.");
        foreach (var paragraph in body.Descendants(W + "p").ToList())
        {
            var style = (string?)paragraph.Descendants(W + "pStyle").FirstOrDefault()?.Attribute(W + "val");
            var text = WordDocumentStructure.NormalizedPartText(paragraph.ToString());
            if (style == "CoverNumber" && text == $"{documentNumber}  |  REVISION {previousRevision:D2}")
            {
                var nodes = new List<XNode>();
                nodes.AddRange(SdtRun(WordDocumentStructure.DocumentNumberTag, documentNumber));
                nodes.Add(Run("  |  REVISION "));
                nodes.AddRange(SdtRun(WordDocumentStructure.RevisionTag, nextRevision.ToString("D2")));
                paragraph.ReplaceNodes(nodes);
            }
            else if (style == "CoverStatus" && IsLegacyStatus(text))
            {
                paragraph.ReplaceNodes(SdtRun(WordDocumentStructure.StatusTag, "Draft", caps: true));
            }
        }

        var table = body.Descendants(W + "tbl").FirstOrDefault();
        if (table is null) return;
        foreach (var row in table.Elements(W + "tr").ToList())
        {
            var cells = row.Elements(W + "tc").ToList();
            if (cells.Count < 2) continue;
            var label = WordDocumentStructure.NormalizedPartText(cells[0].ToString()).Trim();
            var valueParagraph = cells[1].Descendants(W + "p").FirstOrDefault();
            if (valueParagraph is null) continue;
            switch (label)
            {
                case "Document number":
                    valueParagraph.ReplaceNodes(SdtRun(WordDocumentStructure.DocumentNumberTag, documentNumber));
                    break;
                case "Revision":
                    valueParagraph.ReplaceNodes(SdtRun(WordDocumentStructure.RevisionTag, nextRevision.ToString("D2")));
                    break;
                case "Status":
                    valueParagraph.ReplaceNodes(SdtRun(WordDocumentStructure.StatusTag, "Draft"));
                    break;
            }
        }
    }

    private static void UpgradeLegacyFooter(XDocument footer, string documentNumber, int previousRevision, int nextRevision)
    {
        foreach (var paragraph in footer.Descendants(W + "p").ToList())
        {
            var text = WordDocumentStructure.NormalizedPartText(paragraph.ToString());
            var marker = $"{documentNumber} Rev {previousRevision:D2} | ";
            if (!text.StartsWith(marker, StringComparison.Ordinal)) continue;
            var remainder = text[marker.Length..];
            var pipe = remainder.IndexOf(" | ", StringComparison.Ordinal);
            if (pipe < 0) continue;
            var tail = remainder[pipe..];
            var pageMarker = tail.LastIndexOf(" | Page", StringComparison.Ordinal);
            if (pageMarker >= 0) tail = tail[..pageMarker] + " | Page ";
            var field = paragraph.Elements(W + "fldSimple").FirstOrDefault();
            var nodes = new List<XNode>();
            nodes.AddRange(SdtRun(WordDocumentStructure.DocumentNumberTag, documentNumber));
            nodes.Add(Run(" Rev "));
            nodes.AddRange(SdtRun(WordDocumentStructure.RevisionTag, nextRevision.ToString("D2")));
            nodes.Add(Run(" | "));
            nodes.AddRange(SdtRun(WordDocumentStructure.StatusTag, "Draft"));
            nodes.Add(Run(tail));
            if (field is not null) nodes.Add(new XElement(field));
            paragraph.ReplaceNodes(nodes);
        }
    }

    private static bool IsLegacyStatus(string text) =>
        text.Equals("RELEASED", StringComparison.OrdinalIgnoreCase)
        || text.Equals("RELEASE CANDIDATE", StringComparison.OrdinalIgnoreCase)
        || text.Equals("RELEASED CANDIDATE", StringComparison.OrdinalIgnoreCase);

    private static IEnumerable<XNode> SdtRun(string tag, string value, bool caps = false) =>
        [new XElement(W + "sdt",
            new XElement(W + "sdtPr", new XElement(W + "tag", new XAttribute(W + "val", tag))),
            new XElement(W + "sdtContent", caps ? RunCaps(value) : Run(value)))];

    private static XElement Run(string text) =>
        new(W + "r", new XElement(W + "t", new XAttribute(XNamespace.Xml + "space", "preserve"), text));

    private static XElement RunCaps(string text) =>
        new(W + "r", new XElement(W + "rPr", new XElement(W + "caps")), new XElement(W + "t", new XAttribute(XNamespace.Xml + "space", "preserve"), text));

    private static bool IsStoryXmlEntry(string name) =>
        name.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)
        && (name.Equals("word/document.xml", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("word/header", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("word/footer", StringComparison.OrdinalIgnoreCase)
            || name.Equals("word/footnotes.xml", StringComparison.OrdinalIgnoreCase)
            || name.Equals("word/endnotes.xml", StringComparison.OrdinalIgnoreCase)
            || name.Equals("word/comments.xml", StringComparison.OrdinalIgnoreCase)
            || name.Equals("word/glossary/document.xml", StringComparison.OrdinalIgnoreCase));

    public async Task<ControlledAttachment> StoreAsync(Guid projectId, Guid documentId, Guid revisionId,
        Guid logicalId, int version, string label, string description, string fileName, string contentType,
        byte[] content, Guid? supersedesId, string actor, DateTimeOffset now, CancellationToken ct)
    {
        var validation = ValidateSafeOoxmlIfDocx(contentType, content);
        await using var source = new MemoryStream(content, false);
        var stored = await files.StoreAsync(source, fileName, contentType, ct);
        return new ControlledAttachment(projectId, "ManagedDocument", documentId, revisionId, logicalId, version,
            label, description, stored.OriginalFileName, stored.ContentType, stored.Size, stored.Sha256,
            stored.StorageKey, supersedesId, actor, now, validation?.Profile, validation?.Result);
    }

    public async Task<(ControlledAttachment Attachment, StagedEvidence Staged)> StageAsync(Guid operationId, string slot,
        Guid projectId, Guid documentId, Guid revisionId, Guid logicalId, int version, string label,
        string description, string fileName, string contentType, byte[] content, Guid? supersedesId,
        string actor, DateTimeOffset now, CancellationToken ct)
    {
        var validation = ValidateSafeOoxmlIfDocx(contentType, content);
        await using var source = new MemoryStream(content, false);
        return await StageAsync(operationId, slot, projectId, documentId, revisionId, logicalId, version, label,
            description, fileName, contentType, source, supersedesId, actor, now, ct, validation);
    }

    /// <summary>Stages evidence from a stream so the evidence store can bound, hash and write it without an in-memory copy.</summary>
    public async Task<(ControlledAttachment Attachment, StagedEvidence Staged)> StageAsync(Guid operationId, string slot,
        Guid projectId, Guid documentId, Guid revisionId, Guid logicalId, int version, string label,
        string description, string fileName, string contentType, Stream source, Guid? supersedesId,
        string actor, DateTimeOffset now, CancellationToken ct, OoxmlValidationResult? validation = null)
    {
        var staged = await files.StageAsync(source, operationId, slot, fileName, contentType, ct);
        var attachment = new ControlledAttachment(projectId, "ManagedDocument", documentId, revisionId, logicalId,
            version, label, description, staged.OriginalFileName, staged.ContentType, staged.Size, staged.Sha256,
            staged.StorageKey, supersedesId, actor, now, validation?.Profile, validation?.Result);
        return (attachment, staged);
    }

    public Task PromoteAsync(StagedEvidence staged, CancellationToken ct) => files.PromoteAsync(staged, ct);
    public string? Quarantine(string storageKey, Guid operationId, string reason) => files.Quarantine(storageKey, operationId, reason);

    public static string Sha256(byte[] content) => Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
    public void Delete(string storageKey) => files.Delete(storageKey);

    private static OoxmlValidationResult? ValidateSafeOoxmlIfDocx(string contentType, byte[] content) =>
        string.Equals(contentType, DocxContentType, StringComparison.OrdinalIgnoreCase)
            ? ValidateSafeOoxml(content)
            : null;

    private static OoxmlValidationResult ValidateSafeOoxml(byte[] content)
    {
        try { return AeroLinkOoxmlProfile.Validate(content); }
        catch (OoxmlValidationException ex) { throw new DomainException(ex.Message); }
    }

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

    private static void UpdateXmlEntry(ZipArchive archive, string entryName, Action<XDocument> transform)
    {
        var entry = archive.GetEntry(entryName) ?? throw new DomainException("The Word document is missing required package metadata.");
        var document = XDocument.Parse(ReadText(entry), LoadOptions.PreserveWhitespace);
        transform(document);
        WriteEntryText(archive, entryName, document.ToString(SaveOptions.DisableFormatting));
    }

    private static void WriteEntryText(ZipArchive archive, string entryName, string content)
    {
        archive.GetEntry(entryName)?.Delete();
        var replacement = archive.CreateEntry(entryName, CompressionLevel.Optimal);
        using var writer = new StreamWriter(replacement.Open(), new UTF8Encoding(false));
        writer.Write(content);
    }

    private static async Task<byte[]> ReadLimitedAsync(Stream input, int maximumBytes, string limitMessage, CancellationToken ct)
    {
        using var output = new MemoryStream();
        var buffer = new byte[81920];
        int read;
        while ((read = await input.ReadAsync(buffer, ct)) > 0)
        {
            if (output.Length + read > maximumBytes) throw new DomainException(limitMessage);
            await output.WriteAsync(buffer.AsMemory(0, read), ct);
        }
        if (output.Length == 0) throw new DomainException("The Word document cannot be empty.");
        return output.ToArray();
    }
}

public sealed class PdfRenditionTooLargeException : IOException
{
    public PdfRenditionTooLargeException() : base("The PDF rendition exceeds the 100 MB release limit.") { }
}

public sealed record ReleaseTransformationValidation(bool IsValid, string Code, string Message)
{
    public static ReleaseTransformationValidation Valid() =>
        new(true, "release_candidate_ok", "The release DOCX preserves the exact reviewed technical content and carries the controlled Released status.");
    public static ReleaseTransformationValidation Reject(string code, string message) => new(false, code, message);
}
