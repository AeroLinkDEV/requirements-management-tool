using AeroLink.Domain.Integrations;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AeroLink.Api;

public static class CodeTargetEndpoints
{
    public static void MapCodeTargetEndpoints(this WebApplication app)
    {
        app.MapGet("/api/projects/{projectId:guid}/code/targets", ReadAsync);
    }

    private static async Task<IResult> ReadAsync(Guid projectId, Guid releaseId, string targetKind,
        Guid? baselineId, Guid? reportId, string? search, int? page, int? pageSize,
        HttpContext http, AeroLinkDbContext db, CancellationToken ct)
    {
        if (!await http.HasProjectAccessAsync(db, projectId, ct)) return Results.Forbid();
        var denied = await GitLabMetadataEndpoints.CurrentAccessFailureAsync(projectId, http, db, ct);
        if (denied is not null) return denied;
        http.Response.Headers.CacheControl = "no-store";
        if (!Enum.TryParse<CodeRelationshipTargetKind>(targetKind, out var kind) || !Enum.IsDefined(kind))
            return Results.BadRequest(new { error = "Choose one supported target kind." });
        if (!await db.Releases.AsNoTracking().AnyAsync(x => x.ProjectId == projectId && x.Id == releaseId, ct))
            return Results.NotFound();
        if (page is < 1 or > 100000 || pageSize is < 1 or > 100 || search?.Length > 200)
            return Results.BadRequest(new { error = "Use a bounded page and search of at most 200 characters." });
        var number = page ?? 1;
        var size = pageSize ?? 25;
        var query = search?.Trim().ToLowerInvariant() ?? "";
        var offset = (number - 1) * size;

        if (kind == CodeRelationshipTargetKind.RequirementRevision)
        {
            var effectiveBaseline = baselineId ?? await BuildScope.EffectiveBaselineAsync(db, projectId, releaseId, ct);
            if (effectiveBaseline is not null && !await db.CandidateBaselines.AsNoTracking()
                    .AnyAsync(x => x.ProjectId == projectId && x.Id == effectiveBaseline, ct)) return Results.NotFound();
            var source = from revision in db.RequirementRevisions.AsNoTracking()
                         join artifact in db.Requirements.AsNoTracking() on revision.ArtifactId equals artifact.Id
                         where artifact.ProjectId == projectId && effectiveBaseline != null
                             && db.BaselineRequirements.Any(x => x.BaselineId == effectiveBaseline && x.RevisionId == revision.Id)
                             && (query == "" || artifact.BaseNumber.ToLower().Contains(query) || revision.Statement.ToLower().Contains(query))
                         select new { revision, artifact };
            var total = await source.CountAsync(ct);
            var rows = await source.OrderBy(x => x.artifact.BaseNumber).ThenByDescending(x => x.revision.Revision)
                .ThenBy(x => x.revision.Id).Skip(offset).Take(size).ToListAsync(ct);
            return Results.Ok(new { page = number, pageSize = size, total, baselineId = effectiveBaseline,
                items = rows.Select(x => new Target(x.revision.Id, x.artifact.Id, x.revision.Revision,
                    Display(x.artifact.BaseNumber, x.revision.Revision), x.revision.Statement,
                    x.revision.State.ToString(), null, null, null, true)) });
        }
        if (kind == CodeRelationshipTargetKind.ChangeRequestRevision)
        {
            var source = db.SystemChangeRequests.AsNoTracking().Where(x => x.ProjectId == projectId
                && x.TargetReleaseId == releaseId && (query == "" || x.BaseNumber.ToLower().Contains(query) || x.Title.ToLower().Contains(query)));
            var total = await source.CountAsync(ct);
            var rows = await source.OrderBy(x => x.BaseNumber).ThenByDescending(x => x.Revision).ThenBy(x => x.Id)
                .Skip(offset).Take(size).ToListAsync(ct);
            return Results.Ok(new { page = number, pageSize = size, total,
                items = rows.Select(x => new Target(x.Id, x.Id, x.Revision, x.DisplayNumber, x.Title,
                    x.State.ToString(), x.TargetReleaseId, null, null, true)) });
        }
        if (kind == CodeRelationshipTargetKind.RequirementProposal)
        {
            var source = from proposal in db.RequirementChanges.AsNoTracking()
                         join owner in db.SystemChangeRequests.AsNoTracking() on proposal.ChangeRequestId equals owner.Id
                         where owner.ProjectId == projectId && owner.TargetReleaseId == releaseId
                             && (query == "" || proposal.BaseNumber.ToLower().Contains(query) || proposal.Statement.ToLower().Contains(query)
                                 || owner.BaseNumber.ToLower().Contains(query))
                         select new { proposal, owner };
            var total = await source.CountAsync(ct);
            var rows = await source.OrderBy(x => x.owner.BaseNumber).ThenByDescending(x => x.owner.Revision)
                .ThenBy(x => x.proposal.BaseNumber).ThenBy(x => x.proposal.Id).Skip(offset).Take(size).ToListAsync(ct);
            return Results.Ok(new { page = number, pageSize = size, total,
                items = rows.Select(x => new Target(x.proposal.Id, x.owner.Id, null,
                    $"{(x.proposal.DisplayNumber.Length == 0 ? "Unnamed proposal" : x.proposal.DisplayNumber + " proposal")} in {x.owner.DisplayNumber}",
                    x.proposal.Statement, x.owner.State.ToString(), x.owner.TargetReleaseId, null, null, true)) });
        }

        // PR discovery deliberately lists immutable recorded events, not a guessed "current" snapshot.
        // Stable scalar ordering works on both providers; the event timestamp is displayed, not used
        // to silently replace a selected snapshot. Invalid stored evidence stays visible but disabled.
        var snapshots = from snapshot in db.ProblemReportRevisions.AsNoTracking()
                        join report in db.ProblemReports.AsNoTracking() on snapshot.ProblemReportId equals report.Id
                        where report.ProjectId == projectId && (reportId == null || report.Id == reportId)
                        select new { snapshot, report.ReportNumber };
        if (query != "") snapshots = snapshots.Where(x => x.ReportNumber.ToLower().Contains(query));
        var snapshotTotal = await snapshots.CountAsync(ct);
        var snapshotRows = await snapshots.OrderBy(x => x.ReportNumber).ThenByDescending(x => x.snapshot.Revision)
            .ThenBy(x => x.snapshot.Id).Skip(offset).Take(size).ToListAsync(ct);
        var items = new List<Target>();
        foreach (var row in snapshotRows)
        {
            var exact = await CodeRelationshipTargetResolver.ResolveAsync(db, projectId, kind, row.snapshot.Id, ct);
            items.Add(new Target(row.snapshot.Id, row.snapshot.ProblemReportId, row.snapshot.Revision,
                exact?.DisplaySnapshot ?? Display(row.ReportNumber, row.snapshot.Revision),
                "Recorded Problem Report snapshot", "Historical snapshot", null,
                row.snapshot.EventType, row.snapshot.OccurredAt, exact is not null));
        }
        return Results.Ok(new { page = number, pageSize = size, total = snapshotTotal, items });
    }

    private static string Display(string number, int revision) => $"{number}.{revision:00}";
    private sealed record Target(Guid ExactIdentityId, Guid OwnerId, int? Revision, string Display,
        string Title, string Lifecycle, Guid? TargetReleaseId, string? EventType, DateTimeOffset? OccurredAt, bool Available);
}
