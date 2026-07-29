using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AeroLink.Api;

internal static class BuildScope
{
    internal static async Task<Guid?> EffectiveBaselineAsync(AeroLinkDbContext db, Guid projectId, Guid releaseId,
        CancellationToken ct)
    {
        var releases = await db.Releases.AsNoTracking().Where(x => x.ProjectId == projectId)
            .Select(x => new { x.Id, x.PredecessorReleaseId }).ToListAsync(ct);
        var baselines = await db.CandidateBaselines.AsNoTracking()
            .Where(x => x.ProjectId == projectId && x.RequirementsMaterializedAt != null)
            .Select(x => new { x.Id, x.ReleaseId, x.FrozenAt, x.CreatedAt }).ToListAsync(ct);
        var current = releases.SingleOrDefault(x => x.Id == releaseId);
        var visited = new HashSet<Guid>();
        while (current is not null && visited.Add(current.Id))
        {
            // DateTimeOffset ordering is deliberately in memory: SQLite cannot translate it reliably.
            var baseline = baselines.Where(x => x.ReleaseId == current.Id)
                .OrderByDescending(x => x.FrozenAt ?? x.CreatedAt).FirstOrDefault();
            if (baseline is not null) return baseline.Id;
            current = current.PredecessorReleaseId is null
                ? null
                : releases.SingleOrDefault(x => x.Id == current.PredecessorReleaseId.Value);
        }
        return null;
    }
}
