using AeroLink.Domain.ChangeControl;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AeroLink.Infrastructure.Persistence;

/// <summary>Disposable derived lookup only. The referenced immutable review snapshot remains the evidence authority.</summary>
internal sealed class FrozenReviewTraceLink
{
    public Guid ProjectId { get; set; }
    public Guid CycleId { get; set; }
    public Guid OwnerId { get; set; }
    public Guid UpstreamId { get; set; }
    internal static void Configure(EntityTypeBuilder<FrozenReviewTraceLink> b)
    {
        b.ToTable("frozen_review_trace_links");
        b.HasKey(x => new { x.CycleId, x.UpstreamId });
        b.HasIndex(x => new { x.ProjectId, x.UpstreamId });
        b.HasIndex(x => new { x.ProjectId, x.OwnerId });
        b.HasOne<ReviewCycle>().WithMany().HasForeignKey(x => x.CycleId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne<SystemChangeRequest>().WithMany().HasForeignKey(x => x.OwnerId).OnDelete(DeleteBehavior.Cascade);
        // No upstream FK: legacy snapshots can name absent identities; traversal validates both project owners.
    }
    internal static async Task PrepareAsync(AeroLinkDbContext db, CancellationToken ct)
    {
        var cycles = db.ChangeTracker.Entries<ReviewCycle>().Where(x => x.State == EntityState.Added
            && x.Entity.ChangeRequestId != null && x.Entity.SnapshotContractVersion >= 3).Select(x => x.Entity).ToList();
        if (cycles.Count == 0) return;
        var owners = cycles.Select(x => x.ChangeRequestId!.Value).Distinct().ToArray();
        var projects = db.ChangeTracker.Entries<SystemChangeRequest>().Where(x => owners.Contains(x.Entity.Id))
            .ToDictionary(x => x.Entity.Id, x => x.Entity.ProjectId);
        var missing = owners.Except(projects.Keys).ToArray();
        if (missing.Length > 0)
            foreach (var row in await db.SystemChangeRequests.AsNoTracking().Where(x => missing.Contains(x.Id))
                .Select(x => new { x.Id, x.ProjectId }).ToListAsync(ct)) projects[row.Id] = row.ProjectId;
        var tracked = db.ChangeTracker.Entries<FrozenReviewTraceLink>().Where(x => x.State != EntityState.Deleted)
            .Select(x => (x.Entity.CycleId, x.Entity.UpstreamId)).ToHashSet();
        foreach (var cycle in cycles)
        {
            ct.ThrowIfCancellationRequested();
            foreach (var upstream in FrozenReviewTraceParser.UpstreamIds(cycle.SnapshotJson))
                if (tracked.Add((cycle.Id, upstream))) db.Add(new FrozenReviewTraceLink
                { ProjectId = projects[cycle.ChangeRequestId!.Value], CycleId = cycle.Id,
                    OwnerId = cycle.ChangeRequestId.Value, UpstreamId = upstream });
        }
    }
}
