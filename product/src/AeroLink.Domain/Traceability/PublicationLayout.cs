using System.Text;
using System.Text.Json;
using AeroLink.Domain.Common;

namespace AeroLink.Domain.Traceability;

/// <summary>
/// What a layout section can be filled with.
///
/// Closed on purpose. A generator can only produce what it knows how to produce, and a layout naming a
/// section this product cannot fill would put a heading in a controlled document with nothing under it —
/// which reads as missing evidence rather than as a configuration mistake.
/// </summary>
public enum PublicationSectionContent
{
    /// <summary>The controlled records the document is about: requirements or procedures at its level.</summary>
    ControlledRecords,
    /// <summary>Upward requirement traceability for every published requirement.</summary>
    UpwardTraceAnnex,
    /// <summary>Verification coverage for every published requirement.</summary>
    VerificationAnnex,
    /// <summary>Fixed prose the programme wants in every document of this type.</summary>
    Narrative,
}

public sealed record PublicationLayoutSection(string Heading, string Introduction, PublicationSectionContent Content);

/// <summary>
/// A programme's layout for one kind of controlled document, stored as the body of a controlled document
/// template.
///
/// Generation used to be fixed-form: the headings, the section order, the introductions and the front matter
/// were written into the generator, so a programme whose standard puts a verification annex ahead of the
/// trace annex in its SYSRD simply could not produce its own document. Templates already existed as
/// controlled, numbered, approved, versioned artifacts — but their body was arbitrary JSON that nothing
/// read, so approving one changed nothing about any document.
///
/// This is the schema that body has to satisfy, and the generator reads it. Which makes the existing control
/// meaningful rather than decorative: a layout is approved by a named person with a recorded manifest hash,
/// it cannot be edited once approved without beginning a successor revision, and every generated document
/// records the exact template revision that produced it. Without that last part, revising a template would
/// silently change every document generated afterwards, and a document regenerated next year would no longer
/// match the one somebody signed.
/// </summary>
public static class PublicationLayout
{
    public const int MaximumSections = 20;

    /// <summary>Which document type the layout is for, and how the document is titled and divided.</summary>
    public sealed record Definition(
        ControlledDocumentType AppliesTo,
        string TitlePattern,
        string SubtitlePattern,
        IReadOnlyList<PublicationLayoutSection> Sections);

    /// <summary>
    /// Reads a template body as a layout, or returns null when it is not one.
    ///
    /// Null rather than an exception: a template body predating this schema is legitimate stored content,
    /// and a document that falls back to the built-in layout is a working document. Refusing to generate at
    /// all would be a defect in the generator, not in the record.
    /// </summary>
    public static Definition? TryRead(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            if (!root.TryGetProperty("sections", out var sections) || sections.ValueKind != JsonValueKind.Array) return null;
            if (!Enum.TryParse<ControlledDocumentType>(Text(root, "appliesTo"), ignoreCase: true, out var appliesTo)) return null;

            var parsed = new List<PublicationLayoutSection>();
            foreach (var section in sections.EnumerateArray())
            {
                if (section.ValueKind != JsonValueKind.Object) return null;
                if (!Enum.TryParse<PublicationSectionContent>(Text(section, "content"), ignoreCase: true, out var content)) return null;
                var heading = Text(section, "heading");
                if (string.IsNullOrWhiteSpace(heading)) return null;
                parsed.Add(new(heading.Trim(), Text(section, "introduction").Trim(), content));
            }
            if (parsed.Count == 0) return null;

            return new(appliesTo, Text(root, "titlePattern", "{documentTitle}").Trim(),
                Text(root, "subtitlePattern").Trim(), parsed);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Validates an authored layout and returns the canonical body to store. Throws rather than silently
    /// dropping what it does not understand: somebody who writes a section this product cannot render must
    /// be told, not left to discover it in a document an approver has already signed.
    /// </summary>
    public static string Canonicalize(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) throw new DomainException("A document layout is required.");
        JsonDocument document;
        try { document = JsonDocument.Parse(body); }
        catch (JsonException) { throw new DomainException("The document layout could not be read."); }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new DomainException("A document layout must be an object.");
            if (!Enum.TryParse<ControlledDocumentType>(Text(root, "appliesTo"), ignoreCase: true, out var appliesTo))
                throw new DomainException($"'{Text(root, "appliesTo")}' is not a kind of controlled document this product generates.");
            if (!root.TryGetProperty("sections", out var sections) || sections.ValueKind != JsonValueKind.Array)
                throw new DomainException("A document layout needs a list of sections.");

            var parsed = new List<PublicationLayoutSection>();
            foreach (var section in sections.EnumerateArray())
            {
                if (section.ValueKind != JsonValueKind.Object) throw new DomainException("Each layout section must be an object.");
                var kind = Text(section, "content");
                if (!Enum.TryParse<PublicationSectionContent>(kind, ignoreCase: true, out var content))
                    throw new DomainException($"'{kind}' is not a kind of section this product can fill.");
                var heading = Text(section, "heading").Trim();
                if (heading.Length == 0) throw new DomainException("Every layout section needs a heading.");
                parsed.Add(new(heading, Text(section, "introduction").Trim(), content));
            }

            if (parsed.Count == 0) throw new DomainException("A document layout needs at least one section.");
            if (parsed.Count > MaximumSections) throw new DomainException($"A document layout is limited to {MaximumSections} sections.");
            // A layout that never renders the records the document is about is not a layout for that
            // document — it is a cover sheet, and generating it would produce an authoritative-looking file
            // containing no requirements.
            if (parsed.All(x => x.Content == PublicationSectionContent.Narrative))
                throw new DomainException("A document layout must render its controlled records in at least one section.");

            return Write(new Definition(appliesTo, Text(root, "titlePattern", "{documentTitle}").Trim(),
                Text(root, "subtitlePattern").Trim(), parsed));
        }
    }

    /// <summary>
    /// Fills a pattern from the document's context. An unrecognised placeholder is left exactly as written,
    /// so a typo appears in the document as itself rather than vanishing into an empty string where nobody
    /// would notice a section had lost its heading.
    /// </summary>
    public static string Fill(string pattern, IReadOnlyDictionary<string, string> values)
    {
        if (string.IsNullOrWhiteSpace(pattern)) return "";
        var result = new StringBuilder(pattern);
        foreach (var (key, value) in values) result.Replace("{" + key + "}", value);
        return result.ToString().Trim();
    }

    /// <summary>The placeholders a layout may use, for anything that has to offer them to an author.</summary>
    public static IReadOnlyList<string> Placeholders =>
        ["product", "project", "program", "release", "baseline", "documentType", "documentTitle", "documentNumber", "recordCount"];

    private static string Write(Definition definition)
    {
        var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("appliesTo", definition.AppliesTo.ToString());
            writer.WriteString("titlePattern", definition.TitlePattern);
            writer.WriteString("subtitlePattern", definition.SubtitlePattern);
            writer.WriteStartArray("sections");
            foreach (var section in definition.Sections)
            {
                writer.WriteStartObject();
                writer.WriteString("heading", section.Heading);
                writer.WriteString("introduction", section.Introduction);
                writer.WriteString("content", section.Content.ToString());
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
}
