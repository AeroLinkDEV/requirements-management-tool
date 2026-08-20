using AeroLink.Domain.Requirements;
using Microsoft.EntityFrameworkCore;

namespace AeroLink.Infrastructure.Persistence;

/// <summary>
/// Takes back what materializing a baseline created, so a reopened build reads as open.
///
/// Freezing fixes what a build contains and materializing moves the requirements themselves. Reopening undoes
/// the first; without this it would leave the second standing, and the requirements would still read as though
/// the build were sealed while the build said otherwise.
///
/// Everything removed here is identified by the baseline that made it. A revision carries the baseline it was
/// materialized into, so "what did this baseline create" is a question the data answers rather than one that
/// has to be reconstructed. Nothing from an earlier baseline is touched: those revisions belong to builds that
/// have already been sealed, and in most cases released.
/// </summary>
public sealed class RequirementBaselineDematerializer(AeroLinkDbContext db)
{
    public async Task<int> DematerializeAsync(Guid baselineId, CancellationToken ct)
    {
        var revisions = await db.RequirementRevisions.Where(x => x.EffectiveBaselineId == baselineId).ToListAsync(ct);
        var selections = await db.BaselineRequirements.Where(x => x.BaselineId == baselineId).ToListAsync(ct);

        // Trace links are identified by the revisions they join. A link into a revision that is going away
        // would otherwise point at nothing, and a dangling trace is worse than an absent one because it reads
        // as coverage.
        var revisionIds = revisions.Select(x => x.Id).ToList();
        var traces = revisionIds.Count == 0
            ? []
            : await db.RequirementTraces
                .Where(x => revisionIds.Contains(x.SourceRevisionId) || revisionIds.Contains(x.TargetRevisionId))
                .ToListAsync(ct);

        db.RequirementTraces.RemoveRange(traces);
        db.BaselineRequirements.RemoveRange(selections);
        db.RequirementRevisions.RemoveRange(revisions);

        // A requirement introduced by this baseline has no revision left once its only one is taken back, so
        // the artifact itself goes with it. One that existed before keeps every earlier revision and simply
        // returns to the newest of them.
        var touched = revisions.Select(x => x.ArtifactId).Distinct().ToList();
        if (touched.Count > 0)
        {
            var surviving = await db.RequirementRevisions
                .Where(x => touched.Contains(x.ArtifactId) && x.EffectiveBaselineId != baselineId)
                .Select(x => x.ArtifactId).Distinct().ToListAsync(ct);
            var orphaned = touched.Except(surviving).ToList();
            if (orphaned.Count > 0)
                db.Requirements.RemoveRange(await db.Requirements.Where(x => orphaned.Contains(x.Id)).ToListAsync(ct));
        }

        return revisions.Count;
    }
}
