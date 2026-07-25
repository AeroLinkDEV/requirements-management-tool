using AeroLink.Domain.Traceability;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AeroLink.Api;

/// <summary>
/// Finds the approved layout a project uses for each kind of controlled document.
///
/// Templates were already controlled artifacts — numbered, approved by a named person, versioned, each
/// approval recording a manifest hash. What they were not was *read*: the body was arbitrary JSON that no
/// generator opened, so approving a template changed nothing about any document. This is the lookup that
/// makes the existing control mean something.
/// </summary>
public static class ControlledLayouts
{
    /// <summary>
    /// The latest approved template revision per document type. Only approved revisions are considered: a
    /// draft layout has not been agreed, and generating a controlled document from an unapproved layout
    /// would put an unreviewed structure in front of an approver.
    /// </summary>
    public static async Task<Dictionary<ControlledDocumentType, Guid>> ApprovedAsync(
        AeroLinkDbContext db, Guid projectId, CancellationToken ct)
    {
        var templateIds = await db.DocumentTemplates.AsNoTracking()
            .Where(x => x.ProjectId == projectId).Select(x => x.Id).ToListAsync(ct);
        if (templateIds.Count == 0) return [];

        var revisions = await db.DocumentTemplateRevisions.AsNoTracking()
            .Where(x => templateIds.Contains(x.TemplateId)).ToListAsync(ct);

        var result = new Dictionary<ControlledDocumentType, Guid>();
        foreach (var revision in revisions.OrderByDescending(x => x.Revision))
        {
            var layout = PublicationLayout.TryRead(revision.BodyJson);
            // A template body that is not a layout is legitimate stored content from before this schema
            // existed. It is skipped rather than rejected, and the built-in layout is used.
            if (layout is null) continue;
            result.TryAdd(layout.AppliesTo, revision.Id);
        }
        return result;
    }
}
