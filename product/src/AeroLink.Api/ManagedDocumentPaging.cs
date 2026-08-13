using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AeroLink.Domain.Documents;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

internal static class ManagedDocumentPaging
{
    internal const int DefaultPageSize = 50;
    internal const int MaximumPageSize = 100;

    internal sealed record CursorToken(int Version, string Scope, string FilterKey, DateTimeOffset SnapshotAt,
        string Value, string TieBreaker);
    internal sealed record PageSizeResult(int Value, IResult? Error);
    internal sealed record CursorResult(CursorToken? Cursor, IResult? Error);

    internal static PageSizeResult PageSize(int? requested) => requested is < 1 or > MaximumPageSize
        ? new(0, Results.BadRequest(new { error = $"Page size must be between 1 and {MaximumPageSize}.", code = "invalid_page_size" }))
        : new(requested ?? DefaultPageSize, null);

    internal static string FilterKey(params object?[] values)
    {
        var canonical = string.Join('|', values.Select(value => value?.ToString()?.Trim().ToLowerInvariant() ?? ""));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    internal static string Encode(string scope, string filterKey, DateTimeOffset snapshotAt, string value, string tieBreaker)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new CursorToken(1, scope, filterKey, snapshotAt, value, tieBreaker));
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    internal static CursorResult Decode(string? value, string scope, string filterKey)
    {
        if (string.IsNullOrWhiteSpace(value)) return new(null, null);
        try
        {
            var encoded = value.Trim().Replace('-', '+').Replace('_', '/');
            encoded += new string('=', (4 - encoded.Length % 4) % 4);
            var cursor = JsonSerializer.Deserialize<CursorToken>(Convert.FromBase64String(encoded));
            if (cursor is null || cursor.Version != 1 || cursor.Scope != scope || cursor.FilterKey != filterKey
                || cursor.SnapshotAt > DateTimeOffset.UtcNow.AddMinutes(1)) return new(null, InvalidCursor());
            return new(cursor, null);
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            return new(null, InvalidCursor());
        }
    }

    internal static IResult InvalidCursor() => Results.BadRequest(new
    {
        error = "The page cursor is invalid or does not belong to these filters. Start again from the first page.",
        code = "invalid_cursor"
    });
}

internal static class ManagedDocumentHistoryEndpoints
{
    internal static async Task<IResult> ListAsync(Guid id, string surface, Guid? revisionId, int? pageSize,
        string? cursor, HttpContext http, AeroLinkDbContext db, CancellationToken ct)
    {
        var document = await db.ManagedDocuments.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
        if (document is null) return Results.NotFound();
        if (!await http.HasProjectAccessAsync(db, document.ProjectId, ct)) return Results.Forbid();
        var size = ManagedDocumentPaging.PageSize(pageSize); if (size.Error is not null) return size.Error;
        var normalized = surface.Trim().ToLowerInvariant();
        if (normalized is not ("revisions" or "check-ins" or "reviews" or "signatures" or "relationships" or "contributors" or "assignments" or "audit"))
            return Results.BadRequest(new { error = "Choose revisions, check-ins, reviews, signatures, relationships, contributors, assignments, or audit.", code = "invalid_history_surface" });
        if (revisionId is not null && !await db.ManagedDocumentRevisions.AnyAsync(x => x.Id == revisionId && x.DocumentId == id, ct))
            return Results.BadRequest(new { error = "The selected revision does not belong to this document.", code = "invalid_revision" });
        var filterKey = ManagedDocumentPaging.FilterKey(id, normalized, revisionId);
        var decoded = ManagedDocumentPaging.Decode(cursor, $"history:{normalized}", filterKey); if (decoded.Error is not null) return decoded.Error;
        var snapshotAt = decoded.Cursor?.SnapshotAt ?? DateTimeOffset.UtcNow;
        var offset = 0;
        if (decoded.Cursor is not null && (!int.TryParse(decoded.Cursor.Value, out offset) || offset < 0 || offset > 1_000_000))
            return ManagedDocumentPaging.InvalidCursor();

        if (normalized == "revisions")
        {
            var source = db.ManagedDocumentRevisions.AsNoTracking().Where(x => x.DocumentId == id);
            if (!db.Database.IsNpgsql())
            {
                var rows = (await source.ToListAsync(ct)).Where(x => x.CreatedAt <= snapshotAt)
                    .OrderByDescending(x => x.Revision).ThenByDescending(x => x.Id)
                    .Select(x => new { x.Id, x.Revision, displayNumber = $"{document.DocumentNumber}.{x.Revision:D2}", state = x.State.ToString(), x.ParentRevisionId, x.ResponsibleOwnerId, x.InitiatedBy, x.FormalChangeSummary, x.FormalSummaryHash, x.FormalSummaryVersion, x.CreatedAt, x.UpdatedAt, x.ReleasedBy, x.ReleasedAt, x.Version });
                return PageInMemory(rows, normalized, filterKey, snapshotAt, offset, size.Value);
            }
            var query = source.Where(x => x.CreatedAt <= snapshotAt)
                .OrderByDescending(x => x.Revision).ThenByDescending(x => x.Id)
                .Select(x => new { x.Id, x.Revision, displayNumber = $"{document.DocumentNumber}.{x.Revision:D2}", state = x.State.ToString(), x.ParentRevisionId, x.ResponsibleOwnerId, x.InitiatedBy, x.FormalChangeSummary, x.FormalSummaryHash, x.FormalSummaryVersion, x.CreatedAt, x.UpdatedAt, x.ReleasedBy, x.ReleasedAt, x.Version });
            return await PageAsync(query, normalized, filterKey, snapshotAt, offset, size.Value, ct);
        }
        if (normalized == "check-ins")
        {
            var source = from checkIn in db.ManagedDocumentCheckIns.AsNoTracking()
                        join revision in db.ManagedDocumentRevisions.AsNoTracking() on checkIn.RevisionId equals revision.Id
                        join attachment in db.ControlledAttachments.AsNoTracking() on checkIn.WorkingAttachmentId equals attachment.Id
                        where revision.DocumentId == id && (revisionId == null || revision.Id == revisionId)
                        select new { checkIn, revision, attachment };
            if (!db.Database.IsNpgsql())
            {
                var rows = (await source.ToListAsync(ct)).Where(x => x.checkIn.OccurredAt <= snapshotAt)
                    .OrderByDescending(x => x.checkIn.OccurredAt).ThenByDescending(x => x.checkIn.Id)
                    .Select(x => new { x.checkIn.Id, x.checkIn.RevisionId, revision = x.revision.Revision, displayNumber = $"{document.DocumentNumber}.{x.revision.Revision:D2}", x.checkIn.WorkingAttachmentId, x.checkIn.WorkingVersion, x.checkIn.ActorId, x.checkIn.Comment, x.checkIn.BaseAttachmentId, x.checkIn.BaseSha256, x.checkIn.ResultSha256, x.checkIn.SupersededAttachmentId, x.checkIn.ConnectorSessionId, x.checkIn.OperationId, x.checkIn.OccurredAt, x.checkIn.ReturnResolutionNote, x.attachment.OriginalFileName, x.attachment.Size, x.attachment.Sha256, downloadUrl = $"/api/managed-documents/attachments/{x.attachment.Id}" });
                return PageInMemory(rows, normalized, filterKey, snapshotAt, offset, size.Value);
            }
            var query = from row in source
                        where row.checkIn.OccurredAt <= snapshotAt
                        let checkIn = row.checkIn
                        let revision = row.revision
                        let attachment = row.attachment
                        orderby checkIn.OccurredAt descending, checkIn.Id descending
                        select new { checkIn.Id, checkIn.RevisionId, revision = revision.Revision, displayNumber = $"{document.DocumentNumber}.{revision.Revision:D2}", checkIn.WorkingAttachmentId, checkIn.WorkingVersion, checkIn.ActorId, checkIn.Comment, checkIn.BaseAttachmentId, checkIn.BaseSha256, checkIn.ResultSha256, checkIn.SupersededAttachmentId, checkIn.ConnectorSessionId, checkIn.OperationId, checkIn.OccurredAt, checkIn.ReturnResolutionNote, attachment.OriginalFileName, attachment.Size, attachment.Sha256, downloadUrl = $"/api/managed-documents/attachments/{attachment.Id}" };
            return await PageAsync(query, normalized, filterKey, snapshotAt, offset, size.Value, ct);
        }
        if (normalized == "reviews")
        {
            var source = from step in db.ManagedDocumentReviewSteps.AsNoTracking()
                        join revision in db.ManagedDocumentRevisions.AsNoTracking() on step.RevisionId equals revision.Id
                        where revision.DocumentId == id && (revisionId == null || revision.Id == revisionId)
                        select new { step, revision };
            if (!db.Database.IsNpgsql())
            {
                var rows = (await source.ToListAsync(ct)).Where(x => x.step.AssignedAt == null || x.step.AssignedAt <= snapshotAt)
                    .OrderByDescending(x => x.revision.Revision).ThenByDescending(x => x.step.Cycle).ThenByDescending(x => x.step.Position)
                    .Select(x => new { x.step.Id, x.step.RevisionId, revision = x.revision.Revision, x.step.Cycle, x.step.Position, x.step.StageName, x.step.ApproverId, x.step.ApproverName, x.step.RequiredAuthority, x.step.GrantedAuthority, x.step.AuthoritySource, x.step.AuthoritySourceId, x.step.WorkflowId, x.step.WorkflowName, x.step.WorkflowVersion, x.step.AuthorityPolicy, x.step.AssignedAt, x.step.Version, state = x.step.State.ToString(), x.step.Rationale, x.step.DecidedAt });
                return PageInMemory(rows, normalized, filterKey, snapshotAt, offset, size.Value);
            }
            var query = from row in source
                        where row.step.AssignedAt == null || row.step.AssignedAt <= snapshotAt
                        let step = row.step
                        let revision = row.revision
                        orderby revision.Revision descending, step.Cycle descending, step.Position descending
                        select new { step.Id, step.RevisionId, revision = revision.Revision, step.Cycle, step.Position, step.StageName, step.ApproverId, step.ApproverName, step.RequiredAuthority, step.GrantedAuthority, step.AuthoritySource, step.AuthoritySourceId, step.WorkflowId, step.WorkflowName, step.WorkflowVersion, step.AuthorityPolicy, step.AssignedAt, step.Version, state = step.State.ToString(), step.Rationale, step.DecidedAt };
            return await PageAsync(query, normalized, filterKey, snapshotAt, offset, size.Value, ct);
        }
        if (normalized == "signatures")
        {
            var source = db.ElectronicSignatures.AsNoTracking().Where(x => x.ArtifactType == "ManagedDocument" && x.ArtifactId == id);
            if (!db.Database.IsNpgsql())
            {
                var rows = (await source.ToListAsync(ct)).Where(x => x.SignedAt <= snapshotAt)
                    .OrderByDescending(x => x.SignedAt).ThenByDescending(x => x.Id)
                    .Select(x => new { x.Id, x.UserName, x.DisplayName, x.ArtifactRevision, x.Action, x.Authority, x.AuthoritySource, x.AuthoritySourceId, x.WorkflowId, x.WorkflowVersion, x.ReviewStepId, x.ReviewCycle, x.ReviewStepPosition, x.Meaning, x.Rationale, x.ContentHash, x.SignedAt, isLegacyAuthority = string.IsNullOrWhiteSpace(x.Authority) });
                return PageInMemory(rows, normalized, filterKey, snapshotAt, offset, size.Value);
            }
            var query = source.Where(x => x.SignedAt <= snapshotAt)
                .OrderByDescending(x => x.SignedAt).ThenByDescending(x => x.Id)
                .Select(x => new { x.Id, x.UserName, x.DisplayName, x.ArtifactRevision, x.Action, x.Authority, x.AuthoritySource, x.AuthoritySourceId, x.WorkflowId, x.WorkflowVersion, x.ReviewStepId, x.ReviewCycle, x.ReviewStepPosition, x.Meaning, x.Rationale, x.ContentHash, x.SignedAt, isLegacyAuthority = string.IsNullOrWhiteSpace(x.Authority) });
            return await PageAsync(query, normalized, filterKey, snapshotAt, offset, size.Value, ct);
        }
        if (normalized == "relationships")
        {
            var source = from link in db.ManagedDocumentLinks.AsNoTracking()
                        join revision in db.ManagedDocumentRevisions.AsNoTracking() on link.RevisionId equals revision.Id
                        where revision.DocumentId == id && (revisionId == null || revision.Id == revisionId)
                        select new { link, revision };
            if (!db.Database.IsNpgsql())
            {
                var rows = (await source.ToListAsync(ct)).Where(x => x.link.CreatedAt <= snapshotAt)
                    .OrderByDescending(x => x.link.CreatedAt).ThenByDescending(x => x.link.Id)
                    .Select(x => new { x.link.Id, x.link.RevisionId, revision = x.revision.Revision, x.link.ArtifactType, x.link.ArtifactId, x.link.DisplayNumber, x.link.CanonicalTitle, x.link.TargetState, x.link.TargetProjectId, x.link.TargetReleaseId, x.link.TargetReleaseVersion, x.link.DeepLink, x.link.Relationship, x.link.PolicyVersion, x.link.Provenance, x.link.IsCurrent, x.link.SupersededByLinkId, x.link.SupersedeReason, x.link.SupersededBy, x.link.SupersededAt, x.link.CreatedBy, x.link.CreatedAt });
                return PageInMemory(rows, normalized, filterKey, snapshotAt, offset, size.Value);
            }
            var query = from row in source
                        where row.link.CreatedAt <= snapshotAt
                        let link = row.link
                        let revision = row.revision
                        orderby link.CreatedAt descending, link.Id descending
                        select new { link.Id, link.RevisionId, revision = revision.Revision, link.ArtifactType, link.ArtifactId, link.DisplayNumber, link.CanonicalTitle, link.TargetState, link.TargetProjectId, link.TargetReleaseId, link.TargetReleaseVersion, link.DeepLink, link.Relationship, link.PolicyVersion, link.Provenance, link.IsCurrent, link.SupersededByLinkId, link.SupersedeReason, link.SupersededBy, link.SupersededAt, link.CreatedBy, link.CreatedAt };
            return await PageAsync(query, normalized, filterKey, snapshotAt, offset, size.Value, ct);
        }
        if (normalized == "contributors")
        {
            var source = from contributor in db.ManagedDocumentReviewContributors.AsNoTracking()
                        join revision in db.ManagedDocumentRevisions.AsNoTracking() on contributor.RevisionId equals revision.Id
                        where revision.DocumentId == id && (revisionId == null || revision.Id == revisionId)
                        select new { contributor, revision };
            if (!db.Database.IsNpgsql())
            {
                var rows = (await source.ToListAsync(ct)).Where(x => x.contributor.CapturedAt <= snapshotAt)
                    .OrderByDescending(x => x.contributor.CapturedAt).ThenByDescending(x => x.contributor.Id)
                    .Select(x => new { x.contributor.Id, x.contributor.RevisionId, revision = x.revision.Revision, x.contributor.ReviewCycle, x.contributor.ContributorId, x.contributor.EvidenceHash, x.contributor.CapturedAt, x.contributor.Provenance });
                return PageInMemory(rows, normalized, filterKey, snapshotAt, offset, size.Value);
            }
            var query = from row in source
                        where row.contributor.CapturedAt <= snapshotAt
                        let contributor = row.contributor
                        let revision = row.revision
                        orderby contributor.CapturedAt descending, contributor.Id descending
                        select new { contributor.Id, contributor.RevisionId, revision = revision.Revision, contributor.ReviewCycle, contributor.ContributorId, contributor.EvidenceHash, contributor.CapturedAt, contributor.Provenance };
            return await PageAsync(query, normalized, filterKey, snapshotAt, offset, size.Value, ct);
        }
        if (normalized == "assignments")
        {
            var source = db.ManagedDocumentAssignments.AsNoTracking().Where(x => x.DocumentId == id && (revisionId == null || x.RevisionId == revisionId));
            if (!db.Database.IsNpgsql())
            {
                var rows = (await source.ToListAsync(ct)).Where(x => x.EffectiveAt <= snapshotAt)
                    .OrderByDescending(x => x.EffectiveAt).ThenByDescending(x => x.Id)
                    .Select(x => new { x.Id, x.RevisionId, x.AssignmentType, x.PriorAssigneeId, x.NewAssigneeId, x.AssignedBy, x.Reason, x.EffectiveAt });
                return PageInMemory(rows, normalized, filterKey, snapshotAt, offset, size.Value);
            }
            var query = source.Where(x => x.EffectiveAt <= snapshotAt)
                .OrderByDescending(x => x.EffectiveAt).ThenByDescending(x => x.Id)
                .Select(x => new { x.Id, x.RevisionId, x.AssignmentType, x.PriorAssigneeId, x.NewAssigneeId, x.AssignedBy, x.Reason, x.EffectiveAt });
            return await PageAsync(query, normalized, filterKey, snapshotAt, offset, size.Value, ct);
        }
        if (!db.Database.IsNpgsql())
        {
            var rows = (await db.ManagedDocumentEvents.AsNoTracking().Where(x => x.DocumentId == id).ToListAsync(ct))
                .Where(x => x.OccurredAt <= snapshotAt).OrderByDescending(x => x.OccurredAt).ThenByDescending(x => x.Id)
                .Select(x => new { x.Id, x.EventType, x.ActorId, x.Detail, x.OccurredAt });
            return PageInMemory(rows, normalized, filterKey, snapshotAt, offset, size.Value);
        }
        var audit = db.ManagedDocumentEvents.AsNoTracking().Where(x => x.DocumentId == id && x.OccurredAt <= snapshotAt)
            .OrderByDescending(x => x.OccurredAt).ThenByDescending(x => x.Id)
            .Select(x => new { x.Id, x.EventType, x.ActorId, x.Detail, x.OccurredAt });
        return await PageAsync(audit, normalized, filterKey, snapshotAt, offset, size.Value, ct);
    }

    private static async Task<IResult> PageAsync<T>(IQueryable<T> query, string surface, string filterKey,
        DateTimeOffset snapshotAt, int offset, int pageSize, CancellationToken ct)
    {
        var items = await query.Skip(offset).Take(pageSize + 1).ToListAsync(ct);
        var hasMore = items.Count > pageSize; if (hasMore) items.RemoveAt(items.Count - 1);
        return Results.Ok(new
        {
            pageSize, snapshotAt, hasMore,
            nextCursor = hasMore ? ManagedDocumentPaging.Encode($"history:{surface}", filterKey, snapshotAt, (offset + items.Count).ToString(), "") : null,
            items
        });
    }

    private static IResult PageInMemory<T>(IEnumerable<T> query, string surface, string filterKey,
        DateTimeOffset snapshotAt, int offset, int pageSize)
    {
        var items = query.Skip(offset).Take(pageSize + 1).ToList();
        var hasMore = items.Count > pageSize; if (hasMore) items.RemoveAt(items.Count - 1);
        return Results.Ok(new
        {
            pageSize, snapshotAt, hasMore,
            nextCursor = hasMore ? ManagedDocumentPaging.Encode($"history:{surface}", filterKey, snapshotAt, (offset + items.Count).ToString(), "") : null,
            items
        });
    }
}
