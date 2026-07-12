using AeroLink.Domain.Baselines;
using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Programs;

namespace AeroLink.Domain.Contracts;

public interface IScrRepository
{
    Task<PagedResult<ScrListItem>> QueryAsync(ScrQuery query, CancellationToken cancellationToken);
    Task<SystemChangeRequest?> GetAsync(Guid id, CancellationToken cancellationToken);
    Task AddAsync(SystemChangeRequest scr, CancellationToken cancellationToken);
    Task SaveAsync(CancellationToken cancellationToken);
}

public sealed record ScrQuery(Guid ProjectId, int Page = 1, int PageSize = 50, string? Search = null, ScrState? State = null, Guid? TargetReleaseId = null);
public sealed record ScrListItem(Guid Id, string BaseNumber, int Revision, string Title, ScrState State,
    ChangeRequestType Type, string AuthorId, Guid TargetReleaseId, int RequirementCount, DateTimeOffset UpdatedAt);
public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Page, int PageSize, int TotalCount)
{
    public int TotalPages => (int)Math.Ceiling(TotalCount / (double)PageSize);
}

public interface IProgramRepository
{
    Task<IReadOnlyList<ProgramRecord>> ListProgramsAsync(CancellationToken cancellationToken);
    Task AddAsync(ProgramRecord program, ProjectRecord project, IReadOnlyList<SoftwareRelease> releases,
        CancellationToken cancellationToken);
}

public interface IBaselineRepository
{
    Task<IReadOnlyList<CandidateBaseline>> ListAsync(CancellationToken cancellationToken);
    Task<CandidateBaseline?> GetAsync(Guid id, CancellationToken cancellationToken);
    Task AddAsync(CandidateBaseline baseline, CancellationToken cancellationToken);
    Task SaveAsync(CancellationToken cancellationToken);
}
