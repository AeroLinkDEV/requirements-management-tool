using AeroLink.Domain.Hierarchy;
using AeroLink.Domain.Common;
using Microsoft.EntityFrameworkCore;

namespace AeroLink.Infrastructure.Persistence;

/// <summary>
/// Resolves the effective project policy at one application seam. Stored legacy rows and activated authored
/// rows are runtime authority. An authored draft deliberately remains non-authoritative until the one activation
/// service records its manifest evidence.
/// </summary>
public sealed class EffectiveProjectLadderPolicyResolver(
    AeroLinkDbContext db, ILadderPolicy? codePolicy = null) : IProjectLadderPolicyResolver
{
    private readonly ILadderPolicy catalogue = codePolicy ?? LegacyLadderPolicy.Instance;

    public async Task<ILadderPolicy> ResolveAsync(Guid projectId, CancellationToken ct = default)
    {
        var configuration = await db.ProjectLadderConfigurations.AsNoTracking()
            .Include(x => x.Steps).Include(x => x.AllowedUpstream)
            .SingleOrDefaultAsync(x => x.ProjectId == projectId, ct);
        return ProjectLadderPolicyStorage.ResolvePersisted(configuration, projectId, catalogue);
    }
}

internal static class ProjectLadderPolicyStorage
{
    public static ILadderPolicy ResolvePersisted(ProjectLadderConfiguration? configuration, Guid projectId,
        ILadderPolicy? catalogue = null)
    {
        catalogue ??= LegacyLadderPolicy.Instance;
        if (configuration is null)
            throw new DomainException($"Project {projectId} has no persisted ladder configuration.");
        var resolved = ProjectLadderResolver.Resolve(configuration, catalogue);
        if (configuration.Classification == ProjectLadderConfigurationClassification.LegacyDefault
            && !resolved.AgreesWithLegacyDefault(catalogue))
            throw new DomainException("A LegacyDefault project ladder does not match the stored legacy catalogue.");
        return configuration.State == ProjectLadderConfigurationState.Draft
            ? catalogue
            : configuration.Classification == ProjectLadderConfigurationClassification.LegacyDefault
                ? new StoredLegacyProjectLadderPolicy(resolved, catalogue)
                : new ResolvedProjectLadderPolicy(resolved, catalogue);
    }
}

/// <summary>Explicit policy resolver used by focused tests and legitimate injected policy seams.</summary>
public sealed class FixedProjectLadderPolicyResolver(ILadderPolicy policy) : IProjectLadderPolicyResolver
{
    private readonly ILadderPolicy policy = policy ?? throw new ArgumentNullException(nameof(policy));

    public Task<ILadderPolicy> ResolveAsync(Guid projectId, CancellationToken ct = default) =>
        Task.FromResult(policy);
}
