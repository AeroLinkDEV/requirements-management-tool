using AeroLink.Domain.Verification;
using AeroLink.Domain.Hierarchy;
using Microsoft.EntityFrameworkCore;

namespace AeroLink.Infrastructure.Persistence;

/// <summary>The exact readable verification revisions of a build, including non-executable source Cases.</summary>
public sealed record VerificationReadEffectivityResult(Guid BaselineId, bool IsExactManifest,
    IReadOnlySet<Guid> RevisionIds, IReadOnlyDictionary<Guid, Guid> RevisionByProcedure);

public static class VerificationReadEffectivity
{
    /// <summary>
    /// Uses the existing release traversal and typed baseline membership. Reading a source Case must not
    /// require making it an executable selection. Exact links retain every carried source revision; a
    /// register without an exact revision selects the highest revision within this same scoped population.
    /// This projection must never be used to decide what a build executes.
    /// </summary>
    public static async Task<VerificationReadEffectivityResult?> ForReleaseAsync(
        AeroLinkDbContext db, Guid projectId, Guid releaseId, CancellationToken ct,
        IProjectLadderPolicyResolver? policyResolver = null)
    {
        var executable = await TestProcedureEffectivity.ForReleaseAsync(db, projectId, releaseId, ct);
        if (executable is null) return null;
        var revisionIds = executable.RevisionIds.ToHashSet();
        if (executable.IsExactManifest)
        {
            var policy = await (policyResolver ?? new EffectiveProjectLadderPolicyResolver(db)).ResolveAsync(projectId, ct);
            var levels = policy.OrderedLevels.Where(level =>
                    policy.Definition(level).VerificationProfile?.Enables(VerificationArtifactKind.Case) == true
                    && policy.Definition(level).VerificationProfile?.Enables(VerificationArtifactKind.Procedure) == true)
                .Select(policy.ProcedureLevel).ToHashSet();
            var population = await BaselineExecutableMembership.ForPopulationAsync(
                db, executable.BaselineId, levels, ct);
            revisionIds.UnionWith(population.CoverageRevisionIds);
        }
        var ids = revisionIds.ToList();
        var revisions = await db.TestProcedureRevisions.AsNoTracking().Where(x => ids.Contains(x.Id))
            .Select(x => new { x.Id, x.ProcedureId, x.Revision }).ToListAsync(ct);
        var current = revisions.GroupBy(x => x.ProcedureId)
            .ToDictionary(x => x.Key, x => x.OrderByDescending(r => r.Revision).First().Id);
        return new(executable.BaselineId, executable.IsExactManifest, revisionIds, current);
    }
}
