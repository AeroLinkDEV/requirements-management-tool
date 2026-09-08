using System.Text.Json;
using AeroLink.Domain.Identity;
using Microsoft.EntityFrameworkCore;

namespace AeroLink.Infrastructure.Persistence;

/// <summary>One-time derived-index backfill. No controlled snapshot, signature, hash or identifier is updated.</summary>
public sealed class FrozenReviewTraceAdjacencyMigrationAuthority(AeroLinkDbContext db)
{
    public const string Marker = "FrozenReviewTraceAdjacency.v1";
    public async Task EnsureCompletedAsync(CancellationToken ct = default)
    {
        await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            if (db.Database.IsNpgsql())
                await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(973, 1)", ct);
            if (await db.GovernedMigrationCompletions.AsNoTracking().AnyAsync(x => x.Marker == Marker, ct))
            { await transaction.CommitAsync(ct); return; }
            var offset = 0;
            var inserted = 0;
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var cycles = await (from cycle in db.ReviewCycles.AsNoTracking()
                    join owner in db.SystemChangeRequests.AsNoTracking() on cycle.ChangeRequestId equals owner.Id
                    where cycle.SnapshotContractVersion >= 3
                    orderby cycle.Id
                    select new { cycle.Id, OwnerId = owner.Id, owner.ProjectId, cycle.SnapshotJson })
                    .Skip(offset).Take(100).ToListAsync(ct);
                if (cycles.Count == 0) break;
                var ids = cycles.Select(x => x.Id).ToArray();
                var existing = (await db.Set<FrozenReviewTraceLink>().AsNoTracking().Where(x => ids.Contains(x.CycleId))
                    .Select(x => new { x.CycleId, x.UpstreamId }).ToListAsync(ct))
                    .Select(x => (x.CycleId, x.UpstreamId)).ToHashSet();
                var added = new List<FrozenReviewTraceLink>();
                foreach (var cycle in cycles)
                    foreach (var upstream in FrozenReviewTraceParser.UpstreamIds(cycle.SnapshotJson))
                        if (existing.Add((cycle.Id, upstream))) added.Add(new FrozenReviewTraceLink
                        { ProjectId = cycle.ProjectId, CycleId = cycle.Id, OwnerId = cycle.OwnerId, UpstreamId = upstream });
                db.AddRange(added);
                await db.SaveChangesAsync(ct);
                foreach (var row in added) db.Entry(row).State = EntityState.Detached;
                inserted += added.Count;
                offset += cycles.Count;
            }
            var completion = new GovernedMigrationCompletion(Marker, "platform-migration", DateTimeOffset.UtcNow,
                JsonSerializer.Serialize(new { CyclesExamined = offset, AdjacencyRowsInserted = inserted }));
            db.Add(completion);
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
        });
    }
}
