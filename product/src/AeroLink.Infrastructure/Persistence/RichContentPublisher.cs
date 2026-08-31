using System.Text;
using System.Text.Json;
using AeroLink.Domain.Content;
using AeroLink.Domain.Requirements;
using Microsoft.EntityFrameworkCore;

namespace AeroLink.Infrastructure.Persistence;

/// <summary>
/// Prepares authored content for a generated document.
///
/// Authored content references an image by the attachment that holds it, so the bytes live in one place, are
/// hashed once, and carry the record of who uploaded them. A generated Word or PDF file cannot follow a
/// reference — it has to contain the picture — so the bytes are read out of the content-addressed store and
/// inlined here, at the moment of generation, rather than being duplicated into every record that mentions
/// them.
///
/// This is also the only place a document can quietly lose content, so it does not: an image whose file is
/// missing becomes a line of text naming what should have been there. A document with a visible gap is
/// recoverable; a document with an invisible one is not.
/// </summary>
public sealed class RichContentPublisher(AeroLinkDbContext db, EvidenceFileStore store)
{
    /// <summary>A single inline image beyond this size is a scan, not a diagram, and would bloat every copy.</summary>
    private const long MaximumInlineBytes = 12 * 1024 * 1024;
    // A record can contain several narrative fields, each of which permits several figures. Bound the
    // publication aggregate as well as each file so a deliberately dense but otherwise valid record cannot
    // allocate gigabytes while its DOCX/PDF is generated. Unresolved figures remain visible placeholders.
    private const int MaximumResolvedImages = 64;
    private const long MaximumResolvedBytes = 48 * 1024 * 1024;

    public async Task<IReadOnlyDictionary<Guid, string>> ResolveImagesAsync(
        IEnumerable<string?> contents, Guid projectId, CancellationToken ct, bool includeWithdrawn = false)
    {
        var wanted = contents.SelectMany(RichContent.ReferencedAttachments).Distinct().ToList();
        if (wanted.Count == 0) return new Dictionary<Guid, string>();

        var attachments = await db.ControlledAttachments.AsNoTracking()
            .Where(x => wanted.Contains(x.Id) && x.ProjectId == projectId
                && x.ArtifactType == "InlineImage"
                && (includeWithdrawn || x.State != ControlledAttachmentState.Withdrawn))
            .ToListAsync(ct);

        var resolved = new Dictionary<Guid, string>();
        long resolvedBytes = 0;
        foreach (var attachment in attachments)
        {
            var mediaType = attachment.ContentType.ToLowerInvariant();
            if (mediaType is not ("image/png" or "image/jpeg")) continue;
            if (attachment.Size > MaximumInlineBytes || attachment.Size <= 0
                || resolved.Count >= MaximumResolvedImages
                || attachment.Size > MaximumResolvedBytes - resolvedBytes) continue;
            try
            {
                await using var source = await store.OpenVerifiedReadAsync(
                    attachment.StorageKey, attachment.Size, attachment.Sha256, ct);
                using var buffer = new MemoryStream();
                await source.CopyToAsync(buffer, ct);
                resolved[attachment.Id] = $"data:{mediaType};base64,{Convert.ToBase64String(buffer.ToArray())}";
                resolvedBytes += attachment.Size;
            }
            catch (EvidenceIntegrityException)
            {
                // A missing or altered file is reported by its absence from this map, which the rewrite below
                // turns into visible text rather than silently publishing bytes that no longer match evidence.
            }
        }
        return resolved;
    }

    /// <summary>
    /// Rewrites stored content into the shape the publication renderer reads: images carry their bytes, and
    /// an image that could not be resolved is replaced by what it was described as.
    /// </summary>
    public static string ForPublication(string? stored, IReadOnlyDictionary<Guid, string> images)
    {
        var blocks = RichContent.Read(stored);
        if (blocks.Count == 0) return RichContent.Empty;

        var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteStartArray("blocks");
            foreach (var block in blocks)
            {
                writer.WriteStartObject();
                switch (block.Kind)
                {
                    case RichBlockKind.Image when block.AttachmentId is { } id && images.TryGetValue(id, out var uri):
                        writer.WriteString("type", "image");
                        writer.WriteString("dataUri", uri);
                        writer.WriteString("alt", block.Alt);
                        writer.WriteString("caption", block.Caption);
                        if (block.WidthPercent is { } width) writer.WriteNumber("widthPercent", width);
                        break;
                    case RichBlockKind.Image:
                        {
                            var described = string.IsNullOrWhiteSpace(block.Caption) ? block.Alt : block.Caption;
                            writer.WriteString("type", "paragraph");
                            writer.WriteString("text", string.IsNullOrWhiteSpace(described)
                                ? "[An inline image referenced here could not be retrieved.]"
                                : $"[Image not retrieved: {described}]");
                            break;
                        }
                    case RichBlockKind.Table:
                        writer.WriteString("type", "table");
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
                    case RichBlockKind.Symbol:
                        writer.WriteString("type", "symbol");
                        writer.WriteString("value", block.Text);
                        break;
                    case RichBlockKind.Reference:
                        writer.WriteString("type", "reference");
                        writer.WriteString("label", block.Text);
                        writer.WriteString("target", block.Target);
                        break;
                    default:
                        // Deliberately the plain projection, without the paragraph's emphasis. A generated
                        // Word document and PDF cannot render runs yet, and Text is their exact
                        // concatenation — so a published document says everything the record says, just
                        // without the bold. Teaching the renderers about runs is a separate change; until
                        // then this omission is stated rather than silent.
                        writer.WriteString("type", "paragraph");
                        writer.WriteString("text", block.Text);
                        break;
                }
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }
}
