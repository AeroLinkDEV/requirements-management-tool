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

/// <param name="BaseNumber">
/// One change request's whole revision history, when asked for. Used to expand a collapsed row.
/// </param>
/// <param name="LatestRevisionOnly">
/// Collapses each change request to its newest revision. A programme's history is a list of change requests, not
/// of revisions: SCR-31.00 superseded by .01 is one piece of work read twice, and listing both puts the
/// superseded copy in the reader's way. Off when a specific BaseNumber is asked for, which is how the newest row
/// expands to show what came before it.
/// </param>
public sealed record ScrQuery(Guid ProjectId, int Page = 1, int PageSize = 50, string? Search = null,
    ScrState? State = null, Guid? TargetReleaseId = null, string? BaseNumber = null, bool LatestRevisionOnly = false);
/// <param name="DeferredFromState">How far it had got when it was shelved. Null unless State is Deferred.</param>
/// <param name="RevisionCount">
/// How many revisions of this change request exist, so a collapsed row can say there is more behind it without a
/// second request per row.
/// </param>
public sealed record ScrListItem(Guid Id, string BaseNumber, int Revision, string Title, ScrState State,
    ChangeRequestType Type, string AuthorId, Guid TargetReleaseId, int RequirementCount, DateTimeOffset UpdatedAt,
    ScrState? DeferredFromState = null, int RevisionCount = 1);
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
