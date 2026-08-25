using AeroLink.Domain.Assurance;
using Microsoft.EntityFrameworkCore;

namespace AeroLink.Infrastructure.Persistence;

/// <summary>
/// Reads the stored assurance policy at the one seam every consumer goes through.
///
/// A project with no recorded policy resolves to the AeroLink recommendations rather than failing. That is
/// deliberate and different from the ladder resolver, which fails closed: a missing ladder means the product
/// does not know what the project's structure is, while a missing assurance policy means the project has not
/// departed from anything, and the recommendations are precisely what it has been running under all along.
/// </summary>
public sealed class EffectiveProjectAssurancePolicyResolver(AeroLinkDbContext db) : IProjectAssurancePolicyResolver
{
    public async Task<ResolvedAssurancePolicy> ResolveAsync(Guid projectId, CancellationToken ct = default)
    {
        var effective = await db.ProjectAssurancePolicies.AsNoTracking()
            .Where(x => x.ProjectId == projectId && x.SupersededAt == null)
            .OrderByDescending(x => x.Version)
            .FirstOrDefaultAsync(ct);
        return Project(effective);
    }

    public async Task<ResolvedAssurancePolicy> ResolveVersionAsync(Guid policyVersionId, CancellationToken ct = default)
    {
        var version = await db.ProjectAssurancePolicies.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == policyVersionId, ct);
        return Project(version);
    }

    internal static ResolvedAssurancePolicy Project(ProjectAssurancePolicy? version) => version is null
        ? ResolvedAssurancePolicy.Recommended
        : new(version.Id, version.Version, version.DeclaredLevel, version.Selections());
}

/// <summary>Explicit resolver for focused tests and legitimate injected policy seams.</summary>
public sealed class FixedProjectAssurancePolicyResolver(ResolvedAssurancePolicy policy) : IProjectAssurancePolicyResolver
{
    private readonly ResolvedAssurancePolicy policy = policy ?? throw new ArgumentNullException(nameof(policy));

    public Task<ResolvedAssurancePolicy> ResolveAsync(Guid projectId, CancellationToken ct = default) =>
        Task.FromResult(policy);

    public Task<ResolvedAssurancePolicy> ResolveVersionAsync(Guid policyVersionId, CancellationToken ct = default) =>
        Task.FromResult(policy);
}
