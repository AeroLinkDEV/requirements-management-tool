using AeroLink.Domain.Hierarchy;
using AeroLink.Domain.Verification;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AeroLink.Api;

internal sealed record InspectorTrace(ArtifactThreadResult? Thread, int ExcludedRecords);

internal static class InspectorTraceProjection
{
    // The exploration graph retains historical reachability. An Explorer inspector additionally asserts
    // build applicability, so intersect it with existing exact effectivity rather than treating every
    // historically connected revision as carried. No stored edge or history is changed by this read.
    internal static async Task<InspectorTrace> ReadAsync(AeroLinkDbContext db, Guid projectId,
        Guid baselineId, Guid? releaseId, ArtifactThreadFocalKind kind, Guid revisionId,
        IProjectLadderPolicyResolver policies, CancellationToken ct)
    {
        var graph = await ArtifactThreadProjection.BuildAsync(db, projectId, baselineId, null, kind, revisionId, ct);
        if (graph is null) return new(null, 0);
        var requirements = (await db.BaselineRequirements.AsNoTracking().Where(x => x.BaselineId == baselineId)
            .Select(x => x.RevisionId).ToListAsync(ct)).ToHashSet();
        IReadOnlySet<Guid> verification;
        if (releaseId is Guid release)
            verification = (await VerificationReadEffectivity.ForReleaseAsync(db, projectId, release, ct, policies))?.RevisionIds
                ?? new HashSet<Guid>();
        else
        {
            var effectivity = await TestProcedureEffectivity.ForBaselineAsync(db, baselineId, ct);
            var ids = effectivity?.RevisionIds.ToHashSet() ?? [];
            if (effectivity?.IsExactManifest == true)
            {
                var policy = await policies.ResolveAsync(projectId, ct);
                var levels = policy.OrderedLevels.Where(level =>
                    policy.Definition(level).VerificationProfile?.Enables(VerificationArtifactKind.Case) == true
                    && policy.Definition(level).VerificationProfile?.Enables(VerificationArtifactKind.Procedure) == true)
                    .Select(policy.ProcedureLevel).ToHashSet();
                var population = await BaselineExecutableMembership.ForPopulationAsync(db, baselineId, levels, ct);
                ids.UnionWith(population.CoverageRevisionIds);
            }
            verification = ids;
        }
        var allowed = graph.Nodes.Where(node => node.Kind switch
        {
            "Requirement" => requirements.Contains(node.Id),
            "Case" or "Procedure" => verification.Contains(node.Id),
            _ => true,
        }).Select(node => node.Id).ToHashSet();
        if (!allowed.Contains(revisionId)) return new(null, graph.Nodes.Count);
        var edges = graph.Edges.Where(edge => allowed.Contains(edge.FromId) && allowed.Contains(edge.ToId)).ToList();
        // Prune components cut off by the exact-scope intersection; do not display orphaned provenance as
        // though a path to the selected record still exists.
        var adjacency = edges.SelectMany(edge => new[] { (edge.FromId, edge.ToId), (edge.ToId, edge.FromId) })
            .ToLookup(pair => pair.Item1, pair => pair.Item2);
        var connected = new HashSet<Guid> { revisionId };
        var queue = new Queue<Guid>();
        queue.Enqueue(revisionId);
        while (queue.TryDequeue(out var id))
            foreach (var next in adjacency[id]) if (connected.Add(next)) queue.Enqueue(next);
        var nodes = graph.Nodes.Where(node => connected.Contains(node.Id)).ToList();
        return new(graph with
        {
            Nodes = nodes,
            Edges = edges.Where(edge => connected.Contains(edge.FromId) && connected.Contains(edge.ToId)).ToList(),
        }, graph.Nodes.Count - nodes.Count);
    }
}
