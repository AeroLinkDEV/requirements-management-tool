using AeroLink.Domain.Baselines;
using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Programs;

namespace AeroLink.Domain.Contracts;

public interface IScrRepository
{
    Task<IReadOnlyList<SystemChangeRequest>> ListAsync(CancellationToken cancellationToken);
    Task<SystemChangeRequest?> GetAsync(Guid id, CancellationToken cancellationToken);
    Task AddAsync(SystemChangeRequest scr, CancellationToken cancellationToken);
    Task SaveAsync(CancellationToken cancellationToken);
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
