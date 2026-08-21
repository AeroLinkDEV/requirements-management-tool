using AeroLink.Domain.Hierarchy;
using Microsoft.EntityFrameworkCore;

namespace AeroLink.Infrastructure.Persistence;

/// <summary>
/// Resolves the effective project policy at one application seam.  #705 deliberately keeps authored
/// NonDefault/Draft rows out of runtime authority; #706 will change this resolver when activation is eligible.
/// The database dependency is retained here so that consumers do not each invent their own configuration lookup.
/// </summary>
public sealed class EffectiveProjectLadderPolicyResolver(
    AeroLinkDbContext db, ILadderPolicy? codePolicy = null) : IProjectLadderPolicyResolver
{
    private readonly ILadderPolicy fallback = codePolicy ?? LegacyLadderPolicy.Instance;

    public async Task<ILadderPolicy> ResolveAsync(Guid projectId, CancellationToken ct = default)
    {
        // Read the row once at the seam so malformed project data fails closed consistently.  The authored
        // graph itself is not runtime authority until #706; the policy returned here therefore remains the
        // code-owned legacy policy for LegacyDefault/Stored and NonDefault/Draft alike.
        var configuration = await db.ProjectLadderConfigurations.AsNoTracking()
            .Include(x => x.Steps).Include(x => x.AllowedUpstream)
            .SingleOrDefaultAsync(x => x.ProjectId == projectId, ct);
        if (configuration is not null)
            _ = ProjectLadderResolver.Resolve(configuration, fallback);
        return fallback;
    }
}

/// <summary>Explicit policy resolver used by focused tests and legitimate injected policy seams.</summary>
public sealed class FixedProjectLadderPolicyResolver(ILadderPolicy policy) : IProjectLadderPolicyResolver
{
    private readonly ILadderPolicy policy = policy ?? throw new ArgumentNullException(nameof(policy));

    public Task<ILadderPolicy> ResolveAsync(Guid projectId, CancellationToken ct = default) =>
        Task.FromResult(policy);
}
