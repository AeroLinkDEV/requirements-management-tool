using System.Text;
using System.Text.Json;
using AeroLink.Domain.Common;

namespace AeroLink.Domain.Content;

/// <summary>
/// The structural forms authored content may take. The set is closed on purpose: every one of these has a
/// defined rendering in the workspace, in generated Word documents, and in generated PDF, and a block that
/// renders in one of those and not the others would make a controlled document disagree with the record it
/// was generated from.
/// </summary>
public enum RichBlockKind { Paragraph, Table, Image, Symbol, Reference }

/// <summary>
/// One authored block. A record rather than a class because a block has no identity and no lifecycle — it
/// is content, and the artifact that holds it is what is controlled.
/// </summary>
public sealed record RichBlock(
    RichBlockKind Kind,
    string Text = "",
    IReadOnlyList<IReadOnlyList<string>>? Rows = null,
    Guid? AttachmentId = null,
    string Alt = "",
    string Caption = "",
    string Target = "");

/// <summary>
/// Authored rich content: an ordered list of blocks, stored as canonical JSON.
///
/// Content is stored as structure rather than as markup. Markup would mean storing something a browser
/// executes, written by one engineer and read by the approver who signs for it — the exact shape of a stored
/// scripting attack, and an approver whose session can be driven by the content they are approving is a
/// signature that means nothing. Structure has no executable form: text is text everywhere it is rendered,
/// escaped by the renderer, and there is nothing to sanitise because there was never any markup to begin
/// with.
///
/// Storing structure also keeps the record reproducible. A table is rows and cells, not a layout that
/// depends on a stylesheet that may not exist in ten years, and the same blocks drive the workspace, the
/// Word document, and the PDF, so the three cannot drift apart.
/// </summary>
public static class RichContent
{
    public const string Empty = "{\"blocks\":[]}";

    /// <summary>An authored artifact's content is capped so one record cannot exhaust a page or a request.</summary>
    public const int MaximumBlocks = 200;
    public const int MaximumTextLength = 20_000;
    public const int MaximumTableRows = 200;
    public const int MaximumTableColumns = 20;

    /// <summary>
    /// Reads stored content. Content written before this model existed is plain text; it is adopted as a
    /// single paragraph rather than rejected, because refusing to display an existing approved requirement
    /// because its storage format predates the reader would be a defect in the reader, not in the record.
    /// </summary>
    public static IReadOnlyList<RichBlock> Read(string? stored)
    {
        if (string.IsNullOrWhiteSpace(stored)) return [];
        var trimmed = stored.Trim();
        if (!trimmed.StartsWith('{')) return [new RichBlock(RichBlockKind.Paragraph, trimmed)];
        try
        {
            using var document = JsonDocument.Parse(trimmed);
            if (!document.RootElement.TryGetProperty("blocks", out var blocks) || blocks.ValueKind != JsonValueKind.Array)
                return [new RichBlock(RichBlockKind.Paragraph, trimmed)];
            var result = new List<RichBlock>();
            foreach (var block in blocks.EnumerateArray())
                if (TryReadBlock(block, out var parsed, out _)) result.Add(parsed);
            return result;
        }
        catch (JsonException)
        {
            // Unparseable content is still content somebody wrote. Showing it as text loses its formatting;
            // discarding it loses the requirement.
            return [new RichBlock(RichBlockKind.Paragraph, trimmed)];
        }
    }

    /// <summary>
    /// Validates authored content and returns the canonical JSON to store. Throws rather than silently
    /// dropping what it does not understand: an author who writes something the product cannot render must
    /// be told, not left believing an approver will see it.
    /// </summary>
    public static string Canonicalize(string? authored)
    {
        if (string.IsNullOrWhiteSpace(authored)) return Empty;
        var trimmed = authored.Trim();
        if (!trimmed.StartsWith('{')) return Write([new RichBlock(RichBlockKind.Paragraph, Cap(trimmed))]);

        JsonDocument document;
        try { document = JsonDocument.Parse(trimmed); }
        catch (JsonException) { throw new DomainException("The authored content could not be read."); }

        using (document)
        {
            if (!document.RootElement.TryGetProperty("blocks", out var blocks) || blocks.ValueKind != JsonValueKind.Array)
                throw new DomainException("Authored content must be a list of blocks.");
            var parsed = new List<RichBlock>();
            foreach (var block in blocks.EnumerateArray())
            {
                if (!TryReadBlock(block, out var value, out var error)) throw new DomainException(error);
                parsed.Add(value);
            }
            if (parsed.Count > MaximumBlocks)
                throw new DomainException($"Authored content is limited to {MaximumBlocks} blocks.");
            return Write(parsed);
        }
    }

    /// <summary>
    /// The readable text of authored content. This is what feeds search, the plain projection stored beside
    /// the rich one, and every consumer that has no way to render structure. It lives beside the reader so
    /// the two cannot disagree about what the content says.
    /// </summary>
    public static string ToPlainText(string? stored) => ToPlainText(Read(stored));

    public static string ToPlainText(IReadOnlyList<RichBlock> blocks)
    {
        var text = new StringBuilder();
        foreach (var block in blocks)
        {
            switch (block.Kind)
            {
                case RichBlockKind.Paragraph:
                    Line(block.Text);
                    break;
                case RichBlockKind.Symbol:
                    Line(block.Text);
                    break;
                case RichBlockKind.Reference:
                    Line(string.IsNullOrWhiteSpace(block.Target) ? block.Text : $"{block.Text} ({block.Target})");
                    break;
                case RichBlockKind.Image:
                    // An image reduces to what it was described as. A record whose plain projection says
                    // nothing where a diagram was is a record that reads as incomplete.
                    Line(string.IsNullOrWhiteSpace(block.Caption) ? block.Alt : block.Caption);
                    break;
                case RichBlockKind.Table:
                    if (!string.IsNullOrWhiteSpace(block.Caption)) Line(block.Caption);
                    foreach (var row in block.Rows ?? []) Line(string.Join("\t", row));
                    break;
            }
        }
        return text.ToString().Trim();

        void Line(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            if (text.Length > 0) text.Append('\n');
            text.Append(value.Trim());
        }
    }

    /// <summary>Every attachment an authored block depends on, so a caller can resolve or verify them.</summary>
    public static IReadOnlyList<Guid> ReferencedAttachments(string? stored) =>
        Read(stored).Where(x => x.Kind == RichBlockKind.Image && x.AttachmentId is not null)
            .Select(x => x.AttachmentId!.Value).Distinct().ToList();

    /// <summary>True when the content carries anything a plain field could not have carried.</summary>
    public static bool HasStructure(string? stored)
    {
        var blocks = Read(stored);
        return blocks.Count > 1 || blocks.Any(x => x.Kind is not RichBlockKind.Paragraph);
    }

    public static string FromPlainText(string? text) =>
        string.IsNullOrWhiteSpace(text) ? Empty : Write([new RichBlock(RichBlockKind.Paragraph, Cap(text.Trim()))]);

    private static bool TryReadBlock(JsonElement element, out RichBlock block, out string error)
    {
        block = new RichBlock(RichBlockKind.Paragraph);
        error = "";
        if (element.ValueKind != JsonValueKind.Object) { error = "Each authored block must be an object."; return false; }

        var type = Text(element, "type");
        if (!Enum.TryParse<RichBlockKind>(type, ignoreCase: true, out var kind))
        {
            error = $"'{type}' is not a kind of content this product can render.";
            return false;
        }

        switch (kind)
        {
            case RichBlockKind.Paragraph:
                block = new RichBlock(kind, Cap(Text(element, "text")));
                return true;

            case RichBlockKind.Symbol:
                block = new RichBlock(kind, Cap(Text(element, "value", Text(element, "text"))));
                return true;

            case RichBlockKind.Reference:
                block = new RichBlock(kind, Cap(Text(element, "label", Text(element, "text"))),
                    Target: Cap(Text(element, "target")));
                return true;

            case RichBlockKind.Image:
                {
                    var raw = Text(element, "attachmentId");
                    if (!Guid.TryParse(raw, out var attachmentId) || attachmentId == Guid.Empty)
                    {
                        // The only images this product renders are files it holds. A block that pointed
                        // somewhere else would be an outbound call from a controlled tool, a rendering that
                        // changes when somebody else's server changes, and a record that stops reproducing.
                        error = "An image block must name an attachment held by this deployment.";
                        return false;
                    }
                    block = new RichBlock(kind, AttachmentId: attachmentId,
                        Alt: Cap(Text(element, "alt")), Caption: Cap(Text(element, "caption")));
                    return true;
                }

            case RichBlockKind.Table:
                {
                    if (!element.TryGetProperty("rows", out var rows) || rows.ValueKind != JsonValueKind.Array)
                    { error = "A table block needs rows."; return false; }
                    var parsed = new List<IReadOnlyList<string>>();
                    foreach (var row in rows.EnumerateArray())
                    {
                        if (row.ValueKind != JsonValueKind.Array) { error = "Each table row must be a list of cells."; return false; }
                        var cells = row.EnumerateArray()
                            .Select(x => Cap(x.ValueKind == JsonValueKind.String ? x.GetString() ?? "" : x.ToString()))
                            .ToList();
                        if (cells.Count > MaximumTableColumns)
                        { error = $"A table is limited to {MaximumTableColumns} columns."; return false; }
                        parsed.Add(cells);
                    }
                    if (parsed.Count > MaximumTableRows)
                    { error = $"A table is limited to {MaximumTableRows} rows."; return false; }
                    if (parsed.Count == 0) { error = "A table with no rows says nothing."; return false; }
                    // Ragged rows render as missing cells in Word and as a broken grid on screen. Squaring
                    // them here means every renderer sees the same shape.
                    var width = parsed.Max(x => x.Count);
                    var square = parsed
                        .Select(x => (IReadOnlyList<string>)x.Concat(Enumerable.Repeat("", width - x.Count)).ToList())
                        .ToList();
                    block = new RichBlock(kind, Rows: square, Caption: Cap(Text(element, "caption")));
                    return true;
                }
        }
        error = "Unsupported content.";
        return false;
    }

    private static string Write(IReadOnlyList<RichBlock> blocks)
    {
        var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteStartArray("blocks");
            foreach (var block in blocks)
            {
                writer.WriteStartObject();
                writer.WriteString("type", block.Kind.ToString().ToLowerInvariant());
                switch (block.Kind)
                {
                    case RichBlockKind.Paragraph:
                        writer.WriteString("text", block.Text);
                        break;
                    case RichBlockKind.Symbol:
                        writer.WriteString("value", block.Text);
                        break;
                    case RichBlockKind.Reference:
                        writer.WriteString("label", block.Text);
                        writer.WriteString("target", block.Target);
                        break;
                    case RichBlockKind.Image:
                        writer.WriteString("attachmentId", block.AttachmentId!.Value.ToString());
                        writer.WriteString("alt", block.Alt);
                        writer.WriteString("caption", block.Caption);
                        break;
                    case RichBlockKind.Table:
                        writer.WriteString("caption", block.Caption);
                        writer.WriteStartArray("rows");
                        foreach (var row in block.Rows ?? [])
                        {
                            writer.WriteStartArray();
                            foreach (var cell in row) writer.WriteStringValue(cell);
                            writer.WriteEndArray();
                        }
                        writer.WriteEndArray();
                        break;
                }
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static string Text(JsonElement element, string name, string fallback = "") =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? fallback
            : fallback;

    private static string Cap(string value) =>
        value.Length <= MaximumTextLength ? value : value[..MaximumTextLength];
}
