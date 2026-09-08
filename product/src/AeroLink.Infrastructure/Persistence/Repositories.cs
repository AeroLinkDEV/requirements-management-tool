using AeroLink.Domain.Baselines;
using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Contracts;
using AeroLink.Domain.Programs;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using System.Data;

namespace AeroLink.Infrastructure.Persistence;

public sealed class ChangeRequestRepository(AeroLinkDbContext db) : IChangeRequestRepository
{
    public async Task<PagedResult<ScrListItem>> QueryAsync(ScrQuery query, CancellationToken cancellationToken)
    {
        var page = Math.Max(1, query.Page);
        var pageSize = Math.Clamp(query.PageSize, 1, 200);
        var source = db.SystemChangeRequests.AsNoTracking().Where(x => x.ProjectId == query.ProjectId);
        if (query.TargetReleaseId is not null)
        {
            // A build lists the work it is taking and the work it raised, and nothing else. Deferred work from
            // an earlier build is deliberately absent: it is a backlog to be considered, not part of this build
            // until somebody decides it is, and mixing it in makes the plan for this build read as though it
            // already contained work nobody has committed to. It has its own listing, and bringing one in is
            // the explicit act that puts it here.
            //
            // This reverses #320, which surfaced a predecessor's deferred work inline. That answered the right
            // problem -- shelved work was unreachable from the build that followed -- with the wrong shape.
            source = source.Where(x => x.TargetReleaseId == query.TargetReleaseId
                || x.OriginReleaseId == query.TargetReleaseId);
        }
        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var search = query.Search.Trim();
            source = source.Where(x => EF.Functions.ILike(x.BaseNumber, $"%{search}%") || EF.Functions.ILike(x.Title, $"%{search}%"));
        }
        if (query.State is not null) source = source.Where(x => x.State == query.State);
        if (!string.IsNullOrWhiteSpace(query.BaseNumber))
            source = source.Where(x => x.BaseNumber == query.BaseNumber);
        // One row per change request, showing where it has got to, rather than one row per revision. A revision
        // that has been superseded is the same piece of work read at an earlier moment, and listing it beside its
        // successor puts the stale copy in the reader's way. Compared against the max revision of the same base
        // number rather than by grouping, so paging and counting still work on a plain queryable.
        if (query.LatestRevisionOnly && string.IsNullOrWhiteSpace(query.BaseNumber))
            source = source.Where(x => x.Revision == db.SystemChangeRequests
                .Where(other => other.ProjectId == x.ProjectId && other.BaseNumber == x.BaseNumber)
                .Max(other => other.Revision));
        var total = await source.CountAsync(cancellationToken);
        var ordered = db.Database.IsSqlite()
            ? source.OrderBy(x => x.BaseNumber).ThenByDescending(x => x.Revision)
            : source.OrderByDescending(x => x.UpdatedAt).ThenBy(x => x.BaseNumber);
        var items = await ordered
            .Skip((page - 1) * pageSize).Take(pageSize)
            .Select(x => new ScrListItem(x.Id, x.BaseNumber, x.Revision, x.Title, x.State, x.Type, x.AuthorId,
                x.TargetReleaseId, x.RequirementChanges.Count, x.UpdatedAt, x.DeferredFromState,
                // Counted here so a collapsed row can say there is history behind it without a request per row.
                db.SystemChangeRequests.Count(other => other.ProjectId == x.ProjectId && other.BaseNumber == x.BaseNumber),
                x.RebaseRequiredReason))
            .ToListAsync(cancellationToken);
        return new PagedResult<ScrListItem>(items, page, pageSize, total);
    }

    public Task<SystemChangeRequest?> GetAsync(Guid id, CancellationToken cancellationToken) =>
        GetAsync(id, ChangeRequestLoadShape.Complete, cancellationToken);

    public async Task<SystemChangeRequest?> GetAsync(Guid id, ChangeRequestLoadShape shape,
        CancellationToken cancellationToken)
    {
        var normalized = Normalize(shape);
        var query = db.SystemChangeRequests.AsQueryable();
        if (normalized.HasFlag(ChangeRequestLoadShape.RequirementChanges))
            query = query.Include(x => x.RequirementChanges);
        if (normalized.HasFlag(ChangeRequestLoadShape.ReviewCycles))
            query = query.Include(x => x.ReviewCycles);
        if (normalized.HasFlag(ChangeRequestLoadShape.ReviewSteps))
            query = query.Include(x => x.ReviewCycles).ThenInclude(x => x.Steps);
        if (normalized.HasFlag(ChangeRequestLoadShape.ReviewComments))
        {
            // Comments load with the cycle because closing one publishes whatever drafts are outstanding.
            // Left out, that loop would iterate an empty collection and silently discard them — the write
            // would succeed, nothing would error, and a reviewer's writing would simply never appear.
            query = query.Include(x => x.ReviewCycles).ThenInclude(x => x.Comments);
        }
        if (normalized.HasFlag(ChangeRequestLoadShape.AuditEvents))
            query = query.Include(x => x.AuditEvents);
        if (normalized.HasFlag(ChangeRequestLoadShape.UpstreamLinks))
            query = query.Include(x => x.UpstreamLinks);
        if (normalized.HasFlag(ChangeRequestLoadShape.UpstreamHistory))
            query = query.Include(x => x.UpstreamHistory);

        var independentCollections = normalized is ChangeRequestLoadShape.None
            ? 0
            : Enum.GetValues<ChangeRequestLoadShape>()
                .Where(flag => flag is ChangeRequestLoadShape.RequirementChanges
                    or ChangeRequestLoadShape.ReviewCycles
                    or ChangeRequestLoadShape.ReviewSteps
                    or ChangeRequestLoadShape.ReviewComments
                    or ChangeRequestLoadShape.AuditEvents
                    or ChangeRequestLoadShape.UpstreamLinks
                    or ChangeRequestLoadShape.UpstreamHistory)
                .Count(flag => normalized.HasFlag(flag));
        if (independentCollections < 2)
            return await query.SingleOrDefaultAsync(x => x.Id == id, cancellationToken);

        // Split queries avoid the cartesian multiplication of independent history collections. Every statement
        // must still observe one snapshot: without a transaction PostgreSQL's default ReadCommitted isolation can
        // observe a different state for each child query. If the caller already owns a transaction, split only when
        // that transaction has a snapshot-preserving isolation level. A weaker caller transaction is deliberately
        // kept as one statement so it cannot return a graph assembled from different committed states; its lifetime
        // remains untouched in either case.
        var currentTransaction = db.Database.CurrentTransaction;
        if (currentTransaction is not null && !HasConsistentReadIsolation(currentTransaction))
            return await query.SingleOrDefaultAsync(x => x.Id == id, cancellationToken);

        var transaction = currentTransaction is null
            ? await BeginConsistentReadAsync(cancellationToken)
            : null;
        try
        {
            var result = await query.AsSplitQuery().SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
            if (transaction is not null) await transaction.CommitAsync(cancellationToken);
            return result;
        }
        finally
        {
            if (transaction is not null) await transaction.DisposeAsync();
        }
    }

    private async Task<IDbContextTransaction> BeginConsistentReadAsync(CancellationToken cancellationToken)
    {
        var isolation = db.Database.ProviderName?.Contains("Npgsql", StringComparison.OrdinalIgnoreCase) == true
            ? IsolationLevel.RepeatableRead
            : IsolationLevel.Serializable;
        return await db.Database.BeginTransactionAsync(isolation, cancellationToken);
    }

    private static bool HasConsistentReadIsolation(IDbContextTransaction transaction) =>
        transaction.GetDbTransaction().IsolationLevel is IsolationLevel.RepeatableRead
            or IsolationLevel.Serializable
            or IsolationLevel.Snapshot;

    private static ChangeRequestLoadShape Normalize(ChangeRequestLoadShape shape)
    {
        const ChangeRequestLoadShape all = ChangeRequestLoadShape.RequirementChanges
            | ChangeRequestLoadShape.ReviewCycles | ChangeRequestLoadShape.ReviewSteps
            | ChangeRequestLoadShape.ReviewComments | ChangeRequestLoadShape.AuditEvents
            | ChangeRequestLoadShape.UpstreamLinks | ChangeRequestLoadShape.UpstreamHistory;
        if ((shape & ~all) != 0)
            throw new ArgumentOutOfRangeException(nameof(shape), shape, "Unknown change-request load-shape flags.");
        if (shape.HasFlag(ChangeRequestLoadShape.ReviewSteps)
            || shape.HasFlag(ChangeRequestLoadShape.ReviewComments))
            shape |= ChangeRequestLoadShape.ReviewCycles;
        return shape;
    }

    public Task AddAsync(SystemChangeRequest scr, CancellationToken cancellationToken) =>
        db.SystemChangeRequests.AddAsync(scr, cancellationToken).AsTask();
    public Task SaveAsync(CancellationToken cancellationToken) => db.SaveChangesAsync(cancellationToken);
}

public sealed class ProgramRepository(AeroLinkDbContext db) : IProgramRepository
{
    public async Task<IReadOnlyList<ProgramRecord>> ListProgramsAsync(CancellationToken cancellationToken) =>
        await db.Programs.AsNoTracking().OrderBy(x => x.Name).ToListAsync(cancellationToken);
    public async Task AddAsync(ProgramRecord program, ProjectRecord project, IReadOnlyList<SoftwareRelease> releases, CancellationToken cancellationToken)
    {
        await db.Programs.AddAsync(program, cancellationToken);
        await db.Projects.AddAsync(project, cancellationToken);
        await db.Releases.AddRangeAsync(releases, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
    }
}

public sealed class BaselineRepository(AeroLinkDbContext db) : IBaselineRepository
{
    public async Task<IReadOnlyList<CandidateBaseline>> ListAsync(CancellationToken cancellationToken) =>
        await db.CandidateBaselines.AsNoTracking().Include(x => x.Selections).Include(x => x.ExternalPackageSelections).Include(x => x.Events).ToListAsync(cancellationToken);
    public Task<CandidateBaseline?> GetAsync(Guid id, CancellationToken cancellationToken) =>
        db.CandidateBaselines.Include(x => x.Selections).Include(x => x.ExternalPackageSelections).Include(x => x.Events).SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
    public Task AddAsync(CandidateBaseline baseline, CancellationToken cancellationToken) =>
        db.CandidateBaselines.AddAsync(baseline, cancellationToken).AsTask();
    public Task SaveAsync(CancellationToken cancellationToken) => db.SaveChangesAsync(cancellationToken);
}
