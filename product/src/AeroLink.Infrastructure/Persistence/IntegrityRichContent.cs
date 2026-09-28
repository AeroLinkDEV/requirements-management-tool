using System.Net;
using System.Xml;
using System.Xml.Linq;
using AeroLink.Domain.Common;
using AeroLink.Domain.Content;

namespace AeroLink.Infrastructure.Persistence;

public sealed record IntegrityRenderedContent(string Content, IReadOnlyList<string> Findings);

/// <summary>Bounded XHTML to AeroLink's non-executable content model. All original bytes stay in the package.</summary>
public static class IntegrityRichContent
{
    public static void ValidateImage(byte[] bytes, string contentType)
    {
        if (!PngImage.IsDeclaredImage(bytes, contentType))
            throw new DomainException("An embedded image is not a supported, bounded PNG or JPEG.");
    }
    public static IntegrityRenderedContent Convert(string value, string representation,
        IntegritySourceItem item, Func<IntegrityAttachment, Guid> imageIdentity)
    {
        // Source text is never an AeroLink document, even when it happens to contain a "blocks" JSON key.
        if (representation == "text") return new(RichContent.FromBlocks(
            string.IsNullOrWhiteSpace(value) ? [] : [new(RichBlockKind.Paragraph, value)]), []);
        if (value.Length > 200_000) throw new DomainException("A rich-text field exceeds the supported conversion size.");
        var source = representation == "entity-protected-xhtml" ? WebUtility.HtmlDecode(value) : value;
        XElement root;
        try
        {
            using var reader = XmlReader.Create(new StringReader("<source>" + source + "</source>"), new XmlReaderSettings
            { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 250_000 });
            root = XElement.Load(reader, LoadOptions.PreserveWhitespace);
        }
        catch (XmlException) { throw new DomainException("A mapped rich-text field is not supported XHTML. Its raw content remains in the source package; choose a supported field before importing."); }
        if (root.Descendants().Any(x => x.Ancestors().Count() > 32)) throw new DomainException("Rich text is nested too deeply.");
        var blocks = new List<RichBlock>();
        var runs = new List<RichRun>();
        var findings = new HashSet<string>(StringComparer.Ordinal);
        void Flush()
        {
            if (runs.Count == 0) return;
            var text = string.Concat(runs.Select(x => x.Text));
            if (!string.IsNullOrWhiteSpace(text)) blocks.Add(new(RichBlockKind.Paragraph, text, Runs: runs.ToArray()));
            runs.Clear();
        }
        void Walk(XNode node, bool bold = false, bool italic = false, bool underline = false, bool code = false)
        {
            if (node is XText text) { runs.Add(new(text.Value, bold, italic, underline, code)); return; }
            if (node is not XElement element) return;
            var tag = element.Name.LocalName.ToLowerInvariant();
            if (element.Attributes().Any(x => x.Name.LocalName is "style" or "class" || x.Name.LocalName.StartsWith("on", StringComparison.OrdinalIgnoreCase)))
                findings.Add("Source styling or behavior is preserved in the package but is not executed by AeroLink.");
            if (tag is "script" or "style" or "iframe" or "object")
            { findings.Add($"The source {tag} element is preserved only in the package."); return; }
            if (tag == "br") { runs.Add(new("\n", bold, italic, underline, code)); return; }
            if (tag == "img")
            {
                Flush(); var uri = (string?)element.Attribute("src") ?? "";
                if (uri.StartsWith("mks:///", StringComparison.OrdinalIgnoreCase))
                {
                    var attachment = item.Attachments.SingleOrDefault(x => x.SourceUri == uri)
                        ?? throw new DomainException("A source image does not resolve to an attributed package attachment.");
                    if (attachment.ContentType is not ("image/png" or "image/jpeg"))
                        throw new DomainException("An embedded image uses an unsupported media type.");
                    blocks.Add(new(RichBlockKind.Image, AttachmentId: imageIdentity(attachment), Alt: (string?)element.Attribute("alt") ?? attachment.Name));
                }
                else
                {
                    blocks.Add(new(RichBlockKind.Reference, Text: (string?)element.Attribute("alt") ?? "External source image", Target: uri));
                    findings.Add("External image URL retained as a reference; no network request was made.");
                }
                return;
            }
            if (tag == "table")
            {
                Flush();
                if (element.Descendants().Any(x => x.Name.LocalName is not ("tr" or "td" or "th" or "tbody" or "thead" or "tfoot")))
                    findings.Add("Source table formatting is rendered as cell text; the original markup is preserved.");
                if (element.Descendants().Any(x => x.Name.LocalName is "img" or "table")
                    || element.Descendants().Attributes().Any(x => x.Name.LocalName is "rowspan" or "colspan"))
                    throw new DomainException("Nested, merged or image-containing source tables require an explicit conversion before import.");
                var rows = element.Descendants().Where(x => x.Name.LocalName == "tr")
                    .Select(x => (IReadOnlyList<string>)x.Elements().Where(y => y.Name.LocalName is "td" or "th").Select(y => y.Value).ToArray()).ToArray();
                blocks.Add(new(RichBlockKind.Table, Rows: rows)); return;
            }
            var paragraph = tag is "p" or "div" or "li" or "h1" or "h2" or "h3" or "pre";
            if (tag is "ul" or "ol" or "h1" or "h2" or "h3")
                findings.Add("Source list or heading layout is rendered as paragraphs; the original markup is preserved.");
            if (paragraph) Flush();
            if (tag is not ("p" or "div" or "li" or "h1" or "h2" or "h3" or "pre" or "span" or "b" or "strong"
                or "i" or "em" or "u" or "code" or "ul" or "ol" or "html" or "body" or "a"))
                findings.Add($"Source element {tag} is rendered as text; its original markup is preserved.");
            foreach (var child in element.Nodes()) Walk(child, bold || tag is "b" or "strong", italic || tag is "i" or "em",
                underline || tag == "u", code || tag is "code" or "pre");
            if (tag == "a" && element.Attribute("href") is { } href)
                runs.Add(new(" (" + href.Value + ")"));
            if (paragraph) Flush();
        }
        foreach (var child in root.Nodes()) Walk(child);
        Flush();
        return new(RichContent.FromBlocks(blocks), findings.Order(StringComparer.Ordinal).ToArray());
    }
}
