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
        var policy = await (policyResolver ?? new EffectiveProjectLadderPolicyResolver(db)).ResolveAsync(projectId, ct);
        var levels = policy.OrderedLevels.Where(level =>
                policy.Definition(level).VerificationProfile?.Enables(VerificationArtifactKind.Case) == true
                && policy.Definition(level).VerificationProfile?.Enables(VerificationArtifactKind.Procedure) == true)
            .Select(policy.ProcedureLevel).ToHashSet();
        if (executable.IsExactManifest)
        {
            var population = await BaselineExecutableMembership.ForPopulationAsync(
                db, executable.BaselineId, levels, ct);
            revisionIds.UnionWith(population.CoverageRevisionIds);
        }
        else
        {
            // Pre-manifest coverage still names Cases after the governed execution cutover. Its typed
            // migration source preserves the exact generated Procedure, including historical mirrors.
            // Read that recorded identity, never a later authored Procedure merely linked to the Case.
            // This does not turn the compatibility population into an executable manifest.
            var carriedIds = revisionIds.ToList();
            var generated = await (from source in db.TestProcedureMigrationSources.AsNoTracking()
                                   where source.ProjectId == projectId && carriedIds.Contains(source.SourceCaseRevisionId)
                                   join revision in db.TestProcedureRevisions.AsNoTracking()
                                       on source.GeneratedProcedureRevisionId equals revision.Id
                                   join procedure in db.TestProcedures.AsNoTracking()
                                       on revision.ProcedureId equals procedure.Id
                                   where procedure.Id == source.GeneratedProcedureArtifactId
                                       && procedure.ProjectId == projectId
                                       && procedure.ArtifactKind == VerificationArtifactKind.Procedure
                                       && levels.Contains(procedure.Level)
                                   select revision.Id).ToListAsync(ct);
            revisionIds.UnionWith(generated);
        }
        var ids = revisionIds.ToList();
        var revisions = await db.TestProcedureRevisions.AsNoTracking().Where(x => ids.Contains(x.Id))
            .Select(x => new { x.Id, x.ProcedureId, x.Revision }).ToListAsync(ct);
        var current = revisions.GroupBy(x => x.ProcedureId)
            .ToDictionary(x => x.Key, x => x.OrderByDescending(r => r.Revision).First().Id);
        return new(executable.BaselineId, executable.IsExactManifest, revisionIds, current);
    }
}
