using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AeroLink.Api;

public static class CodeFileRegisterEndpoints
{
    public static void MapCodeFileRegisterEndpoints(this WebApplication app)
    {
        app.MapGet("/api/projects/{projectId:guid}/code/files", async (Guid projectId, Guid releaseId,
            Guid sourceSnapshotId, string? search, int? page, int? pageSize, HttpContext http,
            AeroLinkDbContext db, CancellationToken ct) =>
        {
            if (!await http.HasProjectAccessAsync(db, projectId, ct)) return Results.Forbid();
            var denied = await GitLabMetadataEndpoints.CurrentAccessFailureAsync(projectId, http, db, ct);
            if (denied is not null) return denied;
            http.Response.Headers.CacheControl = "no-store";
            if (page is < 1 or > 100000 || pageSize is < 1 or > 100 || search?.Length > 200)
                return Results.BadRequest(new { error = "Use a bounded page and search of at most 200 characters." });
            if (!await db.Releases.AsNoTracking().AnyAsync(x => x.Id == releaseId && x.ProjectId == projectId, ct)
                || !await db.GitLabSourceSnapshots.AsNoTracking().AnyAsync(x => x.Id == sourceSnapshotId && x.ProjectId == projectId, ct))
                return Results.NotFound();
            var number = page ?? 1;
            var size = pageSize ?? 25;
            var query = search?.Trim().ToLowerInvariant() ?? "";
            var files = db.GitLabFileRelationships.AsNoTracking().Where(x => x.ProjectId == projectId
                && x.ReleaseId == releaseId && x.SourceSnapshotId == sourceSnapshotId && x.IsActive
                && (query == "" || x.Path.ToLower().Contains(query)))
                .GroupBy(x => x.Path).Select(group => new { path = group.Key, relationshipCount = group.Count() });
            var total = await files.CountAsync(ct);
            var items = await files.OrderBy(x => x.path).Skip((number - 1) * size).Take(size).ToListAsync(ct);
            return Results.Ok(new { page = number, pageSize = size, total, items,
                scope = "Recorded active relationships at this source snapshot; not repository coverage." });
        });
    }
}
