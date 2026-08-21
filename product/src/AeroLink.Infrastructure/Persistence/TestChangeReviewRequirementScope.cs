using AeroLink.Domain.Baselines;
using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Hierarchy;
using AeroLink.Domain.Requirements;
using AeroLink.Domain.Verification;
using Microsoft.EntityFrameworkCore;

namespace AeroLink.Infrastructure.Persistence;

/// <summary>
/// One exact requirement revision a test change request is allowed to govern through a procedure proposal.
///
/// Project and discipline are necessary boundaries, but they are not authority. Authority comes from the
/// package's own verification-impact work, and build membership comes from the exact requirement manifest.
/// Keeping both predicates here prevents the picker, mutation, and procedure materializer from disagreeing.
/// </summary>
public sealed record TestChangeReviewRequirementChoice(
    Guid Id, Guid RevisionId, string DisplayNumber, string Statement, RequirementLevel Level);

public static class TestChangeReviewRequirementScope
{
    /// <summary>
    /// The exact requirement revisions this package may govern, intersected with the build's requirement
    /// manifest. Used by both the picker projection and the mutation enforcement so they cannot disagree.
    /// </summary>
    public static async Task<IReadOnlyList<Guid>> CarriedImpactRevisionIdsAsync(
        AeroLinkDbContext db, TestChangeReview review, Guid? baselineId, CancellationToken ct)
    {
        var effectiveBaselineId = baselineId ?? await EffectiveRequirementBaselineIdAsync(
            db, review.ProjectId, review.ReleaseId, ct);
        if (effectiveBaselineId is null) return [];

        var baseline = await db.CandidateBaselines.AsNoTracking()
            .Where(x => x.Id == effectiveBaselineId && x.ProjectId == review.ProjectId
                && x.RequirementsMaterializedAt != null)
            .Select(x => new { x.Id }).SingleOrDefaultAsync(ct);
        if (baseline is null) return [];

        var items = await db.VerificationImpactItems.AsNoTracking()
            .Where(x => x.TestChangeReviewId == review.Id && x.ProjectId == review.ProjectId
                && x.ReleaseId == review.ReleaseId && x.State != VerificationImpactState.Superseded)
            .Select(x => new { x.RequirementRevisionId, x.RetargetedRequirementRevisionId })
            .ToListAsync(ct);
        var impactRevisionIds = items
            .SelectMany(x => new[] { x.RequirementRevisionId, x.RetargetedRequirementRevisionId })
            .Where(x => x is not null).Select(x => x!.Value).Distinct().ToList();
        if (impactRevisionIds.Count == 0) return [];

        var carriedRevisionIds = await db.BaselineRequirements.AsNoTracking()
            .Where(x => x.BaselineId == baseline.Id && impactRevisionIds.Contains(x.RevisionId))
            .Select(x => x.RevisionId).Distinct().ToListAsync(ct);
        return carriedRevisionIds;
    }

    public static IQueryable<TestChangeReviewRequirementChoice> ChoicesQuery(
        AeroLinkDbContext db, Guid projectId, IReadOnlyCollection<Guid> carriedRevisionIds,
        TestProcedureLevel procedureLevel, ILadderPolicy? policy = null)
    {
        var ids = carriedRevisionIds.Distinct().ToList();
        var wantedLevel = (policy ?? LegacyLadderPolicy.Instance).RequirementLevelFor(procedureLevel);
        return from revision in db.RequirementRevisions.AsNoTracking()
                   .Where(x => ids.Contains(x.Id))
               join artifact in db.Requirements.AsNoTracking()
                   .Where(x => x.ProjectId == projectId && x.Level == wantedLevel)
                      on revision.ArtifactId equals artifact.Id
               orderby artifact.BaseNumber, revision.Revision
               select new TestChangeReviewRequirementChoice(
                   artifact.Id,
                   revision.Id,
                   artifact.BaseNumber + "." + (revision.Revision < 10 ? "0" : "") + revision.Revision,
                   revision.Statement,
                   artifact.Level);
    }

    public static async Task<IReadOnlyList<TestChangeReviewRequirementChoice>> ForReviewAsync(
        AeroLinkDbContext db, TestChangeReview review, Guid? baselineId, CancellationToken ct,
        ILadderPolicy? policy = null) =>
        await ChoicesQuery(db, review.ProjectId,
            await CarriedImpactRevisionIdsAsync(db, review, baselineId, ct), review.ProcedureLevel(policy), policy)
            .ToListAsync(ct);

    public static async Task<(int Total, IReadOnlyList<TestChangeReviewRequirementChoice> Items)> ForReviewPageAsync(
        AeroLinkDbContext db, TestChangeReview review, string? search, int page, int pageSize,
        IReadOnlyCollection<Guid>? hydrateRevisionIds, CancellationToken ct, ILadderPolicy? policy = null)
    {
        var carried = await CarriedImpactRevisionIdsAsync(db, review, null, ct);
        // The governed candidate set is the package's own scope, so materializing it is bounded by the
        // change's actual reach, never the whole Project. Filtering and paging then run in memory because
        // DisplayNumber is a computed projection property EF cannot translate into SQL.
        var scoped = await ChoicesQuery(db, review.ProjectId, carried, review.ProcedureLevel(policy), policy).ToListAsync(ct);
        var query = scoped.AsEnumerable();
        if (!string.IsNullOrWhiteSpace(search))
        {
            var q = search.Trim().ToLower();
            query = query.Where(x => x.DisplayNumber.ToLower().Contains(q) || x.Statement.ToLower().Contains(q));
        }
        var total = query.Count();
        var paged = query.OrderBy(x => x.DisplayNumber).Skip((page - 1) * pageSize).Take(pageSize).ToList();
        var requested = (hydrateRevisionIds ?? []).Distinct().ToList();
        var hydrated = requested.Count == 0
            ? []
            : scoped.Where(x => requested.Contains(x.RevisionId)).ToList();
        var items = paged.Concat(hydrated).DistinctBy(x => x.RevisionId)
            .OrderBy(x => x.DisplayNumber).ToList();
        return (total, items);
    }

    public static async Task<Guid?> EffectiveRequirementBaselineIdAsync(AeroLinkDbContext db, Guid projectId,
        Guid releaseId, CancellationToken ct)
    {
        var releases = await db.Releases.AsNoTracking().Where(x => x.ProjectId == projectId)
            .Select(x => new { x.Id, x.PredecessorReleaseId }).ToListAsync(ct);
        var baselines = await db.CandidateBaselines.AsNoTracking()
            .Where(x => x.ProjectId == projectId && x.RequirementsMaterializedAt != null)
            .Select(x => new { x.Id, x.ReleaseId, x.FrozenAt, x.CreatedAt }).ToListAsync(ct);
        var current = releases.SingleOrDefault(x => x.Id == releaseId);
        var visited = new HashSet<Guid>();
        while (current is not null && visited.Add(current.Id))
        {
            // DateTimeOffset ordering stays in memory for SQLite compatibility.
            var baseline = baselines.Where(x => x.ReleaseId == current.Id)
                .OrderByDescending(x => x.FrozenAt ?? x.CreatedAt).FirstOrDefault();
            if (baseline is not null) return baseline.Id;
            current = current.PredecessorReleaseId is null
                ? null
                : releases.SingleOrDefault(x => x.Id == current.PredecessorReleaseId.Value);
        }
        return null;
    }
}
