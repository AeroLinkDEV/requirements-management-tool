using AeroLink.Domain.Baselines;
using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Common;
using AeroLink.Domain.Requirements;
using AeroLink.Domain.Releases;
using AeroLink.Domain.Traceability;
using AeroLink.Domain.Verification;
using Microsoft.EntityFrameworkCore;

namespace AeroLink.Infrastructure.Persistence;

/// <summary>
/// Performs the state normalization that must happen immediately before persistence.
///
/// Application-assigned child identities make EF's initial state ambiguous: a newly discovered child can be
/// marked Modified even though no row exists yet. This component resolves that ambiguity in bounded family
/// queries, then applies the existing append-only, version, and orphan-comment rules. It deliberately does
/// not save, open a transaction, or clear the tracker; those decisions remain owned by the save boundary.
/// </summary>
internal sealed class SaveBoundaryStateRepair(AeroLinkDbContext db)
{
    private const int ExistenceQueryChunkSize = 500;

    public async Task ApplyAsync(CancellationToken cancellationToken)
    {
        // Aggregate children use application-assigned GUIDs. EF interprets newly discovered children with set
        // keys as existing unless their append-only state is made explicit.
        foreach (var entry in db.ChangeTracker.Entries<AuditEvent>().Where(x => x.State == EntityState.Modified))
            entry.State = EntityState.Added;

        var requirementChanges = db.ChangeTracker.Entries<RequirementChange>()
            .Where(x => x.State == EntityState.Modified).ToList();
        var upstreamLinks = db.ChangeTracker.Entries<ChangeRequestUpstreamLink>()
            .Where(x => x.State == EntityState.Modified).ToList();
        var upstreamHistory = db.ChangeTracker.Entries<ChangeRequestUpstreamHistory>()
            .Where(x => x.State == EntityState.Modified).ToList();
        var cycles = db.ChangeTracker.Entries<ReviewCycle>()
            .Where(x => x.State == EntityState.Modified
                && x.Entity.CompletedAt is null
                && x.Entity.Steps.All(s => s.State != ApprovalStepState.Approved)).ToList();

        // Resolve all application-assigned IDs before changing any state. This keeps the query count bounded
        // by entity family and avoids one GetDatabaseValuesAsync call per affected row.
        var existingRequirementChanges = await ExistingIdsAsync(
            requirementChanges.Select(x => x.Entity.Id),
            ids => db.RequirementChanges.AsNoTracking().Where(x => ids.Contains(x.Id)).Select(x => x.Id),
            cancellationToken);
        var existingUpstreamLinks = await ExistingIdsAsync(
            upstreamLinks.Select(x => x.Entity.Id),
            ids => db.ChangeRequestUpstreamLinks.AsNoTracking().Where(x => ids.Contains(x.Id)).Select(x => x.Id),
            cancellationToken);
        var existingUpstreamHistory = await ExistingIdsAsync(
            upstreamHistory.Select(x => x.Entity.Id),
            ids => db.ChangeRequestUpstreamHistory.AsNoTracking().Where(x => ids.Contains(x.Id)).Select(x => x.Id),
            cancellationToken);
        var existingCycles = await ExistingIdsAsync(
            cycles.Select(x => x.Entity.Id),
            ids => db.ReviewCycles.AsNoTracking().Where(x => ids.Contains(x.Id)).Select(x => x.Id),
            cancellationToken);

        // A requirement change can be genuinely modified (for example, a rebase) or can be a newly appended
        // child discovered through an aggregate. Preserve its identity in both cases.
        foreach (var entry in requirementChanges)
            if (!existingRequirementChanges.Contains(entry.Entity.Id))
                entry.State = EntityState.Added;

        // Existing upstream answers and history are controlled evidence and cannot be rewritten. A genuinely
        // new application-assigned row is still an append, as before.
        foreach (var entry in upstreamLinks)
        {
            if (!existingUpstreamLinks.Contains(entry.Entity.Id))
                entry.State = EntityState.Added;
            else
                throw new DomainException(
                    "A change-request upstream link is immutable; replace it through controlled authoring.");
        }

        foreach (var entry in upstreamHistory)
        {
            if (!existingUpstreamHistory.Contains(entry.Entity.Id))
                entry.State = EntityState.Added;
            else
                throw new DomainException("Change-request upstream history is immutable.");
        }

        foreach (var entry in db.ChangeTracker.Entries<ArtifactFieldDefinition>()
                     .Where(x => x.State == EntityState.Modified))
            entry.State = EntityState.Added;

        // Verification identity migration can update the snapshot hash of an existing active cycle. Only a
        // cycle whose row is absent is converted to Added, and its newly discovered steps follow it.
        foreach (var cycle in cycles)
        {
            if (existingCycles.Contains(cycle.Entity.Id))
                continue;

            cycle.State = EntityState.Added;
            foreach (var step in cycle.Entity.Steps)
                db.Entry(step).State = EntityState.Added;
        }

        foreach (var entry in db.ChangeTracker.Entries<BaselineChangeRequestSelection>()
                     .Where(x => x.State == EntityState.Modified))
            entry.State = EntityState.Added;
        foreach (var entry in db.ChangeTracker.Entries<BaselineEvent>()
                     .Where(x => x.State == EntityState.Modified))
            entry.State = EntityState.Added;
        foreach (var entry in db.ChangeTracker.Entries<ReleaseCampaignEvent>()
                     .Where(x => x.State == EntityState.Modified))
            entry.State = EntityState.Added;

        SetVersions<SystemChangeRequest>();
        SetVersions<RequirementSpecification>();
        SetVersions<TestProcedure>();
        SetVersions<RequirementTraceLink>();
        SetVersions<CandidateBaseline>();

        // Removing a comment from an optional owner collection nulls its FK by default. The database constraint
        // requires exactly one owner, so an ownerless modified comment is a deletion.
        foreach (var entry in db.ChangeTracker.Entries<ReviewComment>()
                     .Where(x => x.State == EntityState.Modified))
        {
            if (entry.Property(x => x.ReviewCycleId).CurrentValue is null
                && entry.Property(x => x.ManagedDocumentRevisionId).CurrentValue is null)
                entry.State = EntityState.Deleted;
        }
    }

    private async Task<HashSet<Guid>> ExistingIdsAsync(
        IEnumerable<Guid> candidateIds,
        Func<IReadOnlyCollection<Guid>, IQueryable<Guid>> queryFactory,
        CancellationToken cancellationToken)
    {
        var ids = candidateIds.Distinct().ToArray();
        if (ids.Length == 0)
            return [];

        var existing = new HashSet<Guid>();
        foreach (var chunk in ids.Chunk(ExistenceQueryChunkSize))
            foreach (var id in await queryFactory(chunk).ToListAsync(cancellationToken))
                existing.Add(id);
        return existing;
    }

    private void SetVersions<TEntity>() where TEntity : class
    {
        foreach (var entry in db.ChangeTracker.Entries<TEntity>())
        {
            var property = entry.Property<long>("Version");
            if (entry.State == EntityState.Added)
                property.CurrentValue = 1L;
            else if (entry.State == EntityState.Modified)
                property.CurrentValue = property.OriginalValue + 1L;
        }
    }
}
