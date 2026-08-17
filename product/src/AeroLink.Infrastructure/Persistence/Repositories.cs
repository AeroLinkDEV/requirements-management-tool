using AeroLink.Domain.Baselines;
using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Contracts;
using AeroLink.Domain.Programs;
using Microsoft.EntityFrameworkCore;

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
            // Work shelved by a predecessor build comes with the build that follows it.
            //
            // Deferring means "put away for another day with the state it had reached remembered", and the
            // next build is exactly the day it should come back and be considered. Listing strictly by target
            // build meant a change request deferred in 1.6 vanished when 1.7 opened, and the only route to it
            // was to navigate back to the build that shelved it — so the shelf was somewhere work went to be
            // forgotten rather than to wait.
            //
            // Only Deferred records travel. Anything else belongs to the build that owns it.
            var predecessors = db.Releases.Where(x => x.Id == query.TargetReleaseId)
                .Select(x => x.PredecessorReleaseId);
            source = source.Where(x => x.TargetReleaseId == query.TargetReleaseId
                || (x.State == ChangeRequestState.Deferred && predecessors.Contains(x.TargetReleaseId)));
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
                db.SystemChangeRequests.Count(other => other.ProjectId == x.ProjectId && other.BaseNumber == x.BaseNumber)))
            .ToListAsync(cancellationToken);
        return new PagedResult<ScrListItem>(items, page, pageSize, total);
    }

    public Task<SystemChangeRequest?> GetAsync(Guid id, CancellationToken cancellationToken) =>
        db.SystemChangeRequests
            .Include(x => x.RequirementChanges)
            .Include(x => x.ReviewCycles).ThenInclude(x => x.Steps)
            // Comments load with the cycle because closing one publishes whatever drafts are outstanding.
            // Left out, that loop would iterate an empty collection and silently discard them — the write
            // would succeed, nothing would error, and a reviewer's writing would simply never appear.
            .Include(x => x.ReviewCycles).ThenInclude(x => x.Comments)
            .Include(x => x.AuditEvents)
            .SingleOrDefaultAsync(x => x.Id == id, cancellationToken);

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
        await db.CandidateBaselines.AsNoTracking().Include(x => x.Selections).Include(x => x.Events).ToListAsync(cancellationToken);
    public Task<CandidateBaseline?> GetAsync(Guid id, CancellationToken cancellationToken) =>
        db.CandidateBaselines.Include(x => x.Selections).Include(x => x.Events).SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
    public Task AddAsync(CandidateBaseline baseline, CancellationToken cancellationToken) =>
        db.CandidateBaselines.AddAsync(baseline, cancellationToken).AsTask();
    public Task SaveAsync(CancellationToken cancellationToken) => db.SaveChangesAsync(cancellationToken);
}
