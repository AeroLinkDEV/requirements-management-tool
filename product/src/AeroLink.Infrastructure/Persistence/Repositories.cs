using AeroLink.Domain.Baselines;
using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Contracts;
using AeroLink.Domain.Programs;
using Microsoft.EntityFrameworkCore;

namespace AeroLink.Infrastructure.Persistence;

public sealed class ScrRepository(AeroLinkDbContext db) : IScrRepository
{
    public async Task<IReadOnlyList<SystemChangeRequest>> ListAsync(CancellationToken cancellationToken) =>
        await db.SystemChangeRequests.AsNoTracking().OrderBy(x => x.BaseNumber).ThenByDescending(x => x.Revision).ToListAsync(cancellationToken);

    public Task<SystemChangeRequest?> GetAsync(Guid id, CancellationToken cancellationToken) =>
        db.SystemChangeRequests
            .Include(x => x.RequirementChanges)
            .Include(x => x.ReviewCycles).ThenInclude(x => x.Steps)
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
        await db.CandidateBaselines.AsNoTracking().Include(x => x.Selections).ToListAsync(cancellationToken);
    public Task<CandidateBaseline?> GetAsync(Guid id, CancellationToken cancellationToken) =>
        db.CandidateBaselines.Include(x => x.Selections).SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
    public Task AddAsync(CandidateBaseline baseline, CancellationToken cancellationToken) =>
        db.CandidateBaselines.AddAsync(baseline, cancellationToken).AsTask();
    public Task SaveAsync(CancellationToken cancellationToken) => db.SaveChangesAsync(cancellationToken);
}
