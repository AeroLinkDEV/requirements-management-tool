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
/// A span of paragraph text carrying its emphasis.
///
/// Typed data, never markup. A run says "this text is bold"; it does not say "&lt;b&gt;". Nothing in this
/// model can produce a string a browser would execute, which is the property the whole content design
/// exists to keep — see <see cref="RichContent"/>.
///
/// The four marks are the ones an engineer reaches for when writing a requirement or an analysis, and the
/// set is closed for the same reason the block kinds are: every one has a defined rendering in the
/// workspace, and a mark that rendered in one place and not another would let a controlled document
/// disagree with the record it was generated from.
/// </summary>
public sealed record RichRun(string Text, bool Bold = false, bool Italic = false, bool Underline = false, bool Code = false)
{
    /// <summary>Whether this run carries any emphasis at all. A plain run is not written out.</summary>
    public bool IsPlain => !Bold && !Italic && !Underline && !Code;
}

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
    string Target = "",
    /// <summary>
    /// The emphasis within a paragraph, or null where there is none. <see cref="Text"/> is always the
    /// concatenation of the run texts and remains the single source for every reader that has no way to
    /// show emphasis — search, the plain projection, the generated Word document and PDF. That invariant
    /// is restored on read rather than trusted, so a hand-edited record cannot make the two disagree.
    /// </summary>
    IReadOnlyList<RichRun>? Runs = null,
    /// <summary>Optional bounded display width for an inline image, as a percentage of its narrative column.</summary>
    int? WidthPercent = null);

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
    public const int MinimumImageWidthPercent = 25;
    public const int MaximumImageWidthPercent = 100;

    /// <summary>
    /// A paragraph's emphasis is capped independently of its length. Runs are bounded by how often the
    /// emphasis changes rather than by how much was written, and a record whose every character is its own
    /// run is not authored content — it is a payload.
    /// </summary>
    public const int MaximumRunsPerParagraph = 500;

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
    /// Removes fields introduced after an evidence contract version without changing the spelling of older
    /// authored content that did not use them. This is intentionally narrower than <see cref="Canonicalize"/>:
    /// recomputing a historical snapshot must not normalize old whitespace or legacy plain-text values while
    /// it removes metadata that the older contract could not have committed.
    /// </summary>
    public static string ForEvidenceSchema(string? stored, int schemaVersion)
    {
        if (schemaVersion != 4) return stored ?? "";
        if (string.IsNullOrWhiteSpace(stored)) return stored ?? "";
        var trimmed = stored.Trim();
        if (!trimmed.StartsWith('{')) return stored;

        try
        {
            using var document = JsonDocument.Parse(trimmed);
            if (!document.RootElement.TryGetProperty("blocks", out var blocks)
                || blocks.ValueKind != JsonValueKind.Array
                || !blocks.EnumerateArray().Any(block => block.ValueKind == JsonValueKind.Object
                    && block.TryGetProperty("type", out var type)
                    && type.ValueKind == JsonValueKind.String
                    && string.Equals(type.GetString(), "image", StringComparison.OrdinalIgnoreCase)
                    && block.TryGetProperty("widthPercent", out _)))
                return stored;

            // Width metadata did not exist in schema 4. The current parser has already bounded it on every
            // write, so dropping it here is the only deliberate difference in a v4 recomputation.
            return Write(Read(trimmed).Select(block => block.Kind == RichBlockKind.Image
                ? block with { WidthPercent = null }
                : block).ToList());
        }
        catch (JsonException)
        {
            // The historical contract committed this value as authored text. Preserve it exactly rather
            // than making an old snapshot unrecomputable because a newer reader cannot parse it.
            return stored;
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

    /// <summary>
    /// True when the content carries anything a plain field could not have carried.
    ///
    /// Emphasis counts. A single formatted paragraph is still one paragraph, so without this a plain-text
    /// editor would claim the content and write the emphasis away on the next keystroke.
    /// </summary>
    public static bool HasStructure(string? stored)
    {
        var blocks = Read(stored);
        return blocks.Count > 1
            || blocks.Any(x => x.Kind is not RichBlockKind.Paragraph)
            || blocks.Any(x => x.Runs is { Count: > 0 });
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
                {
                    // Runs, where present, are authoritative for the text: the concatenation is recomputed
                    // rather than taken from the stored "text", so content whose two halves disagree — hand
                    // edited, or written by an older client — is repaired towards what the author actually
                    // marked up rather than silently rendering one thing and searching another.
                    var runs = ReadRuns(element, out var authoredText, out var runsError);
                    if (runsError.Length > 0) { error = runsError; return false; }
                    // Where a runs array was present at all, its concatenation is the text — even when
                    // every run turned out to be plain and the emphasis itself is dropped. Falling back to
                    // the stored "text" there would erase the paragraph of any client that sends runs as
                    // the authority and leaves the projection for the server to compute.
                    block = new RichBlock(kind, Cap(authoredText ?? Text(element, "text")), Runs: runs);
                    return true;
                }

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
                    int? width = null;
                    if (element.TryGetProperty("widthPercent", out var widthElement))
                    {
                        if (widthElement.ValueKind != JsonValueKind.Number || !widthElement.TryGetInt32(out var parsedWidth)
                            || parsedWidth is < MinimumImageWidthPercent or > MaximumImageWidthPercent)
                        {
                            error = $"An inline image width must be between {MinimumImageWidthPercent} and {MaximumImageWidthPercent} percent.";
                            return false;
                        }
                        width = parsedWidth;
                    }
                    block = new RichBlock(kind, AttachmentId: attachmentId,
                        Alt: Cap(Text(element, "alt")), Caption: Cap(Text(element, "caption")), WidthPercent: width);
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
                        // Omitted entirely when nothing is emphasised, so unformatted content is byte-for-byte
                        // what it was before runs existed and no stored record changes shape without cause.
                        if (block.Runs is { Count: > 0 })
                        {
                            writer.WriteStartArray("runs");
                            foreach (var run in block.Runs)
                            {
                                writer.WriteStartObject();
                                writer.WriteString("text", run.Text);
                                if (run.Bold) writer.WriteBoolean("bold", true);
                                if (run.Italic) writer.WriteBoolean("italic", true);
                                if (run.Underline) writer.WriteBoolean("underline", true);
                                if (run.Code) writer.WriteBoolean("code", true);
                                writer.WriteEndObject();
                            }
                            writer.WriteEndArray();
                        }
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
                        if (block.WidthPercent is { } width) writer.WriteNumber("widthPercent", width);
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

    /// <summary>
    /// Reads a paragraph's runs, or null when it has none. Plain runs are dropped and adjacent runs sharing
    /// the same marks are merged, so one canonical spelling exists for any given piece of formatted text
    /// and a record's hash does not depend on how the editor happened to split it.
    /// </summary>
    private static IReadOnlyList<RichRun>? ReadRuns(JsonElement element, out string? authoredText, out string error)
    {
        error = "";
        authoredText = null;
        if (!element.TryGetProperty("runs", out var runs)) return null;
        if (runs.ValueKind != JsonValueKind.Array) { error = "Paragraph emphasis must be a list of runs."; return null; }
        if (runs.GetArrayLength() > MaximumRunsPerParagraph)
        {
            error = $"A paragraph is limited to {MaximumRunsPerParagraph} runs of emphasis.";
            return null;
        }

        var parsed = new List<RichRun>();
        foreach (var run in runs.EnumerateArray())
        {
            if (run.ValueKind != JsonValueKind.Object) { error = "Each run of emphasis must be an object."; return null; }
            var text = Text(run, "text");
            if (text.Length == 0) continue;
            var value = new RichRun(text, Flag(run, "bold"), Flag(run, "italic"), Flag(run, "underline"), Flag(run, "code"));
            if (parsed.Count > 0 && SameMarks(parsed[^1], value))
                parsed[^1] = parsed[^1] with { Text = parsed[^1].Text + value.Text };
            else parsed.Add(value);
        }

        authoredText = string.Concat(parsed.Select(run => run.Text));
        // Nothing emphasised is the same as no runs at all, and is stored that way — but the text the runs
        // spelled out is still the paragraph, and is returned above.
        return parsed.Count == 0 || parsed.All(run => run.IsPlain) ? null : parsed;
    }

    private static bool SameMarks(RichRun left, RichRun right) =>
        left.Bold == right.Bold && left.Italic == right.Italic
        && left.Underline == right.Underline && left.Code == right.Code;

    private static bool Flag(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    private static string Text(JsonElement element, string name, string fallback = "") =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? fallback
            : fallback;

    private static string Cap(string value) =>
        value.Length <= MaximumTextLength ? value : value[..MaximumTextLength];
}
