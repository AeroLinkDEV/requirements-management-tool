using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using AeroLink.Domain.Common;

namespace AeroLink.Infrastructure.Persistence;

/// <summary>
/// The effective Word section/header/watermark and controlled-field model shared by
/// the managed-document release-assurance work (#489, #497).
///
/// AeroLink-controlled document metadata is represented by named Word content controls,
/// never by searching arbitrary visible text:
/// - <c>AeroLink.DocumentNumber</c> - the controlled document number;
/// - <c>AeroLink.Revision</c> - the controlled formal revision;
/// - <c>AeroLink.Status</c> - the controlled Draft/Released status;
/// - <c>AeroLink.Watermark</c> - a content control whose VML shape has id
///   <c>AeroLinkWatermark</c> and carries the normalized DRAFT marking.
///
/// Ordinary author text (including the legitimate words "Draft"/"DRAFT") is technical
/// content and must never be rewritten to determine document state.
/// </summary>
public static class WordDocumentStructure
{
    public const string DocumentNumberTag = "AeroLink.DocumentNumber";
    public const string RevisionTag = "AeroLink.Revision";
    public const string StatusTag = "AeroLink.Status";
    public const string WatermarkTag = "AeroLink.Watermark";
    public const string WatermarkShapeId = "AeroLinkWatermark";

    private static readonly XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    private static readonly XNamespace V = "urn:schemas-microsoft-com:vml";
    private static readonly XNamespace O = "urn:schemas-microsoft-com:office:office";
    private static readonly XNamespace R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private static readonly XNamespace PR = "http://schemas.openxmlformats.org/package/2006/relationships";

    /// <summary>The ZIP parts that can carry rendered story text.</summary>
    private static readonly string[] StoryParts =
    [
        "word/document.xml", "word/footnotes.xml", "word/endnotes.xml", "word/comments.xml", "word/glossary/document.xml"
    ];

    /// <summary>Reads package parts as UTF-8 text, rejecting unsafe paths and excessive packages.</summary>
    public static IReadOnlyDictionary<string, string> ReadWordParts(byte[] bytes)
    {
        try
        {
            using var stream = new MemoryStream(bytes, false);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read, false);
            if (archive.Entries.Count > 4096)
                throw new DomainException("The Word package contains too many parts.");
            var parts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in archive.Entries)
            {
                var name = NormalizePartName(entry.FullName);
                if (IsUnsafePart(name))
                    throw new DomainException("The Word package contains an unsafe path.");
                if (name.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".rels", StringComparison.OrdinalIgnoreCase))
                {
                    if (entry.Length > 32 * 1024 * 1024)
                        throw new DomainException("A Word package part exceeds the supported size.");
                    using var reader = new StreamReader(entry.Open(), Encoding.UTF8, true, leaveOpen: false);
                    parts[name] = reader.ReadToEnd();
                }
            }
            return parts;
        }
        catch (InvalidDataException)
        {
            throw new DomainException("The selected file is not a valid Word document.");
        }
    }

    /// <summary>SHA-256 of every embedded binary resource a document renders (images, OLE/media embeddings).</summary>
    public static IReadOnlyDictionary<string, string> EmbeddedResourceHashes(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, false);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read, false);
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in archive.Entries)
        {
            var name = NormalizePartName(entry.FullName);
            if (!name.StartsWith("word/media/", StringComparison.OrdinalIgnoreCase)
                && !name.StartsWith("word/embeddings/", StringComparison.OrdinalIgnoreCase))
                continue;
            using var source = entry.Open();
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[81920];
            int read;
            while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
                sha.AppendData(buffer, 0, read);
            result[name] = Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant();
        }
        return result;
    }

    /// <summary>Resolves the effective header actually rendered for every section and page variant.</summary>
    public static WordHeaderResolution ResolveHeaders(byte[] bytes)
    {
        var parts = ReadWordParts(bytes);
        if (!parts.TryGetValue("word/document.xml", out var documentXml))
            throw new DomainException("The Word document is missing its body.");
        var relationships = ParseRelationships(parts.TryGetValue("word/_rels/document.xml.rels", out var rels) ? rels : "");
        var settings = parts.TryGetValue("word/settings.xml", out var settingsXml) ? XDocument.Parse(settingsXml) : null;
        var evenAndOdd = settings?.Descendants(W + "evenAndOddHeaders").Any() == true;

        var document = XDocument.Parse(documentXml, LoadOptions.PreserveWhitespace);
        var body = document.Descendants(W + "body").SingleOrDefault() ?? throw new DomainException("The Word document is missing its body.");
        var sections = new List<XElement>();
        foreach (var paragraph in body.Descendants(W + "p"))
        {
            var nested = paragraph.Elements(W + "pPr").Elements(W + "sectPr").SingleOrDefault();
            if (nested is not null) sections.Add(nested);
        }
        var bodySection = body.Elements(W + "sectPr").LastOrDefault();
        if (bodySection is not null) sections.Add(bodySection);
        if (sections.Count == 0) throw new DomainException("The Word document defines no sections.");

        var resolution = new WordHeaderResolution(evenAndOdd);
        WordSectionHeaders? previous = null;
        for (var index = 0; index < sections.Count; index++)
        {
            var section = sections[index];
            var references = section.Elements(W + "headerReference")
                .Select(reference => (Type: ((string?)reference.Attribute(W + "type"))?.ToLowerInvariant() ?? "default",
                    Id: (string?)reference.Attribute(R + "id")))
                .ToList();
            if (references.Any(reference => string.IsNullOrWhiteSpace(reference.Id)))
                throw new DomainException($"Section {index + 1} has a broken header reference.");
            var duplicate = references.GroupBy(reference => reference.Type).FirstOrDefault(group => group.Count() > 1);
            if (duplicate is not null)
                throw new DomainException($"Section {index + 1} defines the {duplicate.Key} header more than once.");
            var titlePage = section.Elements(W + "titlePg").Any();

            string? Resolve(string type)
            {
                var reference = references.SingleOrDefault(candidate => candidate.Type == type);
                if (reference.Id is null) return previous?.Effective(type);
                if (!relationships.TryGetValue(reference.Id, out var target) || target.External)
                    throw new DomainException($"Section {index + 1} {type} header relationship is missing or external.");
                var path = ResolvePartPath(target.Target);
                if (!parts.ContainsKey(path))
                    throw new DomainException($"Section {index + 1} {type} header target is missing.");
                return path;
            }

            var current = new WordSectionHeaders(index, titlePage, evenAndOdd,
                Resolve("default"), titlePage ? Resolve("first") : null, evenAndOdd ? Resolve("even") : null);
            resolution.Sections.Add(current);
            previous = current;
        }

        var referenced = resolution.Sections
            .SelectMany(section => section.VariantParts())
            .Where(part => part is not null)
            .ToHashSet(StringComparer.OrdinalIgnoreCase)!;
        foreach (var part in parts.Keys.Where(part => part.StartsWith("word/header", StringComparison.OrdinalIgnoreCase)))
            if (!referenced.Contains(part)) resolution.OrphanHeaderParts.Add(part);
        return resolution;
    }

    /// <summary>Normalized visible text of a part: adjacent w:t runs, tabs and breaks concatenated.</summary>
    public static string NormalizedPartText(string xml)
    {
        var document = XDocument.Parse(xml, LoadOptions.PreserveWhitespace);
        var builder = new StringBuilder();
        foreach (var node in document.Descendants())
        {
            if (node.Name == W + "t" && !node.Ancestors(W + "del").Any())
                builder.Append(node.Value);
            else if (node.Name == W + "tab") builder.Append('\t');
            else if (node.Name == W + "br" || node.Name == W + "cr") builder.Append('\n');
        }
        return builder.ToString();
    }

    /// <summary>
    /// Normalized story text of one part with controlled status/watermark content removed,
    /// while document-number and revision control text is retained so any change to them is detected.
    /// </summary>
    public static string StoryText(string xml)
    {
        var document = XDocument.Parse(xml, LoadOptions.PreserveWhitespace);
        var builder = new StringBuilder();
        foreach (var node in document.Descendants())
        {
            if (node.Name != W + "t" && node.Name != W + "tab" && node.Name != W + "br" && node.Name != W + "cr")
                continue;
            if (InsideSkippedRegion(node)) continue;
            if (node.Name == W + "tab" && node.Ancestors(W + "tabs").Any()) continue;
            if (node.Name == W + "t")
            {
                if (!node.Ancestors(W + "del").Any()) builder.Append(node.Value);
            }
            else if (node.Name == W + "tab") builder.Append('\t');
            else builder.Append('\n');
        }
        return builder.ToString();
    }

    private static bool InsideSkippedRegion(XElement node)
    {
        foreach (var ancestor in node.Ancestors())
        {
            if (ancestor.Name == W + "sdt")
            {
                var tag = (string?)ancestor.Elements(W + "sdtPr").Elements(W + "tag").Attributes(W + "val").SingleOrDefault();
                if (tag == StatusTag || tag == WatermarkTag) return true;
            }
            else if (ancestor.Name == V + "shape"
                && string.Equals((string?)ancestor.Attribute("id"), WatermarkShapeId, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// A stable fingerprint of the technical content of a document: story text outside the named
    /// status/watermark controls (document body, effective headers, footers, notes and comments),
    /// embedded binary resources, and external hyperlink targets. Word rewrites part names,
    /// relationships and package metadata (styles, settings, docProps, fonts, theme, custom XML) on
    /// every save without changing what the document renders, so those are deliberately not compared.
    /// </summary>
    public static string TechnicalContentFingerprint(byte[] bytes)
    {
        var parts = ReadWordParts(bytes);
        var binaries = EmbeddedResourceHashes(bytes);
        var builder = new StringBuilder();
        foreach (var name in StoryParts)
        {
            if (!parts.TryGetValue(name, out var xml)) continue;
            var text = StoryText(xml);
            if (text.Length == 0) continue;
            builder.Append("T|").Append(name).Append('|').Append(text).Append('\n');
        }
        var resolution = ResolveHeaders(bytes);
        foreach (var section in resolution.Sections)
        {
            foreach (var (variant, part) in section.RequiredVariants())
            {
                var text = part is null ? "" : StoryText(parts[part]);
                if (text.Length == 0) continue;
                builder.Append("H|").Append(section.Index).Append('|').Append(variant).Append('|').Append(text).Append('\n');
            }
        }
        var footerTexts = parts.Keys
            .Where(name => name.StartsWith("word/footer", StringComparison.OrdinalIgnoreCase))
            .Select(name => StoryText(parts[name]))
            .Where(text => text.Length > 0)
            .OrderBy(text => text, StringComparer.Ordinal)
            .ToList();
        if (footerTexts.Count > 0) builder.Append("F|").Append(string.Join(";", footerTexts)).Append('\n');
        foreach (var hash in binaries.Values.OrderBy(hash => hash, StringComparer.Ordinal))
            builder.Append("B|").Append(hash).Append('\n');
        foreach (var target in ExternalHyperlinkTargets(parts).OrderBy(target => target, StringComparer.Ordinal))
            builder.Append("X|").Append(target).Append('\n');
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()))).ToLowerInvariant();
    }

    /// <summary>All external hyperlink targets in story parts, resolved through their relationships.</summary>
    private static IEnumerable<string> ExternalHyperlinkTargets(IReadOnlyDictionary<string, string> parts)
    {
        var targets = new List<string>();
        foreach (var name in parts.Keys.Where(name => name.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)))
        {
            if (name.EndsWith(".rels", StringComparison.OrdinalIgnoreCase)) continue;
            var relationships = ParseRelationships(parts.TryGetValue(RelationshipsName(name), out var rels) ? rels : "");
            XDocument document;
            try { document = XDocument.Parse(parts[name], LoadOptions.PreserveWhitespace); }
            catch (System.Xml.XmlException) { continue; }
            foreach (var hyperlink in document.Descendants(W + "hyperlink"))
            {
                var id = (string?)hyperlink.Attribute(R + "id");
                if (id is null || !relationships.TryGetValue(id, out var target)) continue;
                if (target.External && !string.IsNullOrWhiteSpace(target.Target)) targets.Add(target.Target);
            }
        }
        return targets;
    }

    private static string RelationshipsName(string partName)
    {
        var separator = partName.LastIndexOf('/');
        var directory = separator < 0 ? "" : partName[..separator];
        var file = separator < 0 ? partName : partName[(separator + 1)..];
        return directory.Length == 0 ? $"_rels/{file}.rels" : $"{directory}/_rels/{file}.rels";
    }

    /// <summary>The controlled field values present in a DOCX.</summary>
    public static ControlledFieldValues ControlledFields(byte[] bytes)
    {
        var parts = ReadWordParts(bytes);
        var values = new ControlledFieldValues();
        foreach (var (name, xml) in parts.Where(part => part.Key.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)))
        {
            if (name.EndsWith(".rels", StringComparison.OrdinalIgnoreCase)) continue;
            var document = XDocument.Parse(xml, LoadOptions.PreserveWhitespace);
            foreach (var control in document.Descendants(W + "sdt"))
            {
                var tag = (string?)control.Elements(W + "sdtPr").Elements(W + "tag").Attributes(W + "val").SingleOrDefault();
                if (string.IsNullOrWhiteSpace(tag)) continue;
                var text = ControlText(control);
                switch (tag)
                {
                    case DocumentNumberTag: values.DocumentNumbers.Add(text); break;
                    case RevisionTag: values.Revisions.Add(text); break;
                    case StatusTag: values.Statuses.Add(text); break;
                }
            }
        }
        return values;
    }

    private static string ControlText(XElement control)
    {
        var builder = new StringBuilder();
        foreach (var text in control.Descendants(W + "t"))
            if (!text.Ancestors(W + "del").Any()) builder.Append(text.Value);
        return builder.ToString();
    }

    /// <summary>True when the named controlled watermark shape is present with normalized DRAFT text and the controlled presentation.</summary>
    public static bool PartHasControlledDraftWatermark(string xml)
    {
        var document = XDocument.Parse(xml, LoadOptions.PreserveWhitespace);
        return document.Descendants(V + "shape").Any(shape =>
            string.Equals((string?)shape.Attribute("id"), WatermarkShapeId, StringComparison.OrdinalIgnoreCase)
            && HasDraftTextPath(shape)
            && HasWatermarkPresentation(shape));
    }

    /// <summary>True when the named controlled watermark shape exists at all, regardless of its text.</summary>
    public static bool PartHasControlledWatermarkShape(string xml)
    {
        var document = XDocument.Parse(xml, LoadOptions.PreserveWhitespace);
        return document.Descendants(V + "shape").Any(shape =>
            string.Equals((string?)shape.Attribute("id"), WatermarkShapeId, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>True when a part contains a controlled status control whose normalized text says Draft.</summary>
    public static bool PartHasControlledDraftStatus(string xml)
    {
        var document = XDocument.Parse(xml, LoadOptions.PreserveWhitespace);
        return document.Descendants(W + "sdt").Any(control =>
        {
            var tag = (string?)control.Elements(W + "sdtPr").Elements(W + "tag").Attributes(W + "val").SingleOrDefault();
            return tag == StatusTag && ControlText(control).Contains("Draft", StringComparison.OrdinalIgnoreCase);
        });
    }

    /// <summary>Sets every content control with the given tag to one run carrying the given text, preserving run properties.</summary>
    public static string SetControlValueInXml(string xml, string tag, string value)
    {
        var document = XDocument.Parse(xml, LoadOptions.PreserveWhitespace);
        var changed = false;
        foreach (var control in document.Descendants(W + "sdt").ToList())
        {
            var controlTag = (string?)control.Elements(W + "sdtPr").Elements(W + "tag").Attributes(W + "val").SingleOrDefault();
            if (controlTag != tag) continue;
            var content = control.Element(W + "sdtContent");
            if (content is null) continue;
            var texts = content.Descendants(W + "t").Where(text => !text.Ancestors(W + "del").Any()).ToList();
            if (texts.Count == 0)
                content.Add(new XElement(W + "r", new XElement(W + "t", value)));
            else
            {
                texts[0].Value = value;
                for (var index = 1; index < texts.Count; index++) texts[index].Value = "";
            }
            changed = true;
        }
        return changed ? document.ToString(SaveOptions.DisableFormatting) : xml;
    }

    /// <summary>Removes the named watermark content controls and any bare named watermark shape.</summary>
    public static string RemoveControlledWatermarks(string xml)
    {
        var document = XDocument.Parse(xml, LoadOptions.PreserveWhitespace);
        var changed = false;
        foreach (var control in document.Descendants(W + "sdt").ToList())
        {
            var tag = (string?)control.Elements(W + "sdtPr").Elements(W + "tag").Attributes(W + "val").SingleOrDefault();
            if (tag != WatermarkTag) continue;
            control.Remove();
            changed = true;
        }
        foreach (var shape in document.Descendants(V + "shape").ToList())
        {
            if (!string.Equals((string?)shape.Attribute("id"), WatermarkShapeId, StringComparison.OrdinalIgnoreCase)) continue;
            shape.Remove();
            changed = true;
        }
        return changed ? document.ToString(SaveOptions.DisableFormatting) : xml;
    }

    /// <summary>
    /// Adds the named, presentation-controlled DRAFT watermark content control to a header part that
    /// lacks one. Idempotent: a header that already carries the controlled watermark is returned unchanged.
    /// </summary>
    public static string EnsureControlledDraftWatermark(string headerXml)
    {
        if (PartHasControlledDraftWatermark(headerXml)) return headerXml;
        var document = XDocument.Parse(headerXml, LoadOptions.PreserveWhitespace);
        var root = document.Root ?? throw new DomainException("The Word header is malformed.");
        var watermark = new XElement(W + "sdt",
            new XElement(W + "sdtPr", new XElement(W + "tag", new XAttribute(W + "val", WatermarkTag))),
            new XElement(W + "sdtContent",
                new XElement(W + "p", new XElement(W + "r",
                    new XElement(W + "rPr", new XElement(W + "noProof")),
                    WatermarkShape()))));
        root.AddFirst(new XElement(W + "p", watermark));
        return document.ToString(SaveOptions.DisableFormatting);
    }

    private static XElement WatermarkShape() => new(V + "shape",
        new XAttribute("id", WatermarkShapeId),
        new XAttribute(O + "spid", "_x0000_s2049"),
        new XAttribute("type", "#_x0000_t136"),
        new XAttribute("style",
            "position:absolute;margin-left:0;margin-top:0;width:468pt;height:117pt;rotation:315;z-index:-251658752;" +
            "mso-position-horizontal:center;mso-position-horizontal-relative:margin;" +
            "mso-position-vertical:center;mso-position-vertical-relative:margin"),
        new XAttribute(O + "allowincell", "f"),
        new XAttribute("fillcolor", "#c8d0d8"),
        new XAttribute("stroked", "f"),
        new XElement(V + "textpath",
            new XAttribute("style", "font-family:&quot;Calibri&quot;;font-size:1pt"),
            new XAttribute("string", "DRAFT")),
        new XElement(V + "fill", new XAttribute("opacity", ".45")));

    private static bool HasDraftTextPath(XElement shape) =>
        shape.Descendants(V + "textpath").Any(textpath =>
            ((string?)textpath.Attribute("string"))?.Contains("DRAFT", StringComparison.OrdinalIgnoreCase) == true);

    private static bool HasWatermarkPresentation(XElement shape)
    {
        var style = (string?)shape.Attribute("style") ?? "";
        var fill = (string?)shape.Attribute("fillcolor") ?? "";
        var opacity = (string?)shape.Descendants(V + "fill").Select(fillElement => fillElement.Attribute("opacity")?.Value)
            .FirstOrDefault(value => value is not null);
        return style.Contains("position:absolute", StringComparison.OrdinalIgnoreCase)
            && style.Contains("rotation:315", StringComparison.OrdinalIgnoreCase)
            && style.Contains("z-index:-251658752", StringComparison.OrdinalIgnoreCase)
            && style.Contains("mso-position-horizontal:center", StringComparison.OrdinalIgnoreCase)
            && style.Contains("mso-position-vertical:center", StringComparison.OrdinalIgnoreCase)
            && fill.Equals("#c8d0d8", StringComparison.OrdinalIgnoreCase)
            && (opacity == ".45" || opacity == "0.45");
    }

    private static IReadOnlyDictionary<string, (string Target, bool External)> ParseRelationships(string xml)
    {
        if (string.IsNullOrWhiteSpace(xml)) return new Dictionary<string, (string, bool)>();
        var document = XDocument.Parse(xml);
        return document.Root!.Elements(PR + "Relationship")
            .Where(x => (string?)x.Attribute("Id") is not null)
            .ToDictionary(
                x => (string)x.Attribute("Id")!,
                x => ((string)x.Attribute("Target")!, string.Equals((string?)x.Attribute("TargetMode"), "External", StringComparison.OrdinalIgnoreCase)),
                StringComparer.Ordinal);
    }

    private static string ResolvePartPath(string target)
    {
        var normalized = target.Replace('\\', '/').TrimStart('/');
        if (normalized.Contains("../", StringComparison.Ordinal))
            throw new DomainException("The Word document contains an unsafe relationship target.");
        return normalized.StartsWith("word/", StringComparison.OrdinalIgnoreCase) ? normalized : "word/" + normalized;
    }

    private static string NormalizePartName(string name) => name.Replace('\\', '/');
    private static bool IsUnsafePart(string name) =>
        name.StartsWith('/') || name.Contains("../", StringComparison.Ordinal);
}

public sealed class WordHeaderResolution
{
    public WordHeaderResolution(bool evenAndOdd) { EvenAndOdd = evenAndOdd; }
    public bool EvenAndOdd { get; }
    public List<WordSectionHeaders> Sections { get; } = [];
    public List<string> OrphanHeaderParts { get; } = [];
}

public sealed class WordSectionHeaders
{
    public WordSectionHeaders(int index, bool titlePage, bool evenAndOdd, string? defaultHeader, string? firstHeader, string? evenHeader)
    {
        Index = index; TitlePage = titlePage; EvenAndOdd = evenAndOdd;
        DefaultHeader = defaultHeader; FirstHeader = firstHeader; EvenHeader = evenHeader;
    }
    public int Index { get; }
    public bool TitlePage { get; }
    public bool EvenAndOdd { get; }
    public string? DefaultHeader { get; }
    public string? FirstHeader { get; }
    public string? EvenHeader { get; }

    /// <summary>
    /// A missing reference means linked-to-previous; when the chain has no such header,
    /// Word renders the section's default header for that page variant.
    /// </summary>
    public string? Effective(string type) => type switch
    {
        "first" => FirstHeader ?? DefaultHeader,
        "even" => EvenHeader ?? DefaultHeader,
        _ => DefaultHeader
    };

    public IEnumerable<string> VariantParts()
    {
        if (DefaultHeader is not null) yield return DefaultHeader;
        if (TitlePage && FirstHeader is not null) yield return FirstHeader;
        if (EvenAndOdd && EvenHeader is not null) yield return EvenHeader;
    }

    public IReadOnlyList<(string Variant, string? Part)> RequiredVariants()
    {
        var variants = new List<(string, string?)> { ("default", DefaultHeader) };
        if (TitlePage) variants.Add(("first", Effective("first")));
        if (EvenAndOdd) variants.Add(("even", Effective("even")));
        return variants;
    }
}

public sealed class ControlledFieldValues
{
    public List<string> DocumentNumbers { get; } = [];
    public List<string> Revisions { get; } = [];
    public List<string> Statuses { get; } = [];
}
