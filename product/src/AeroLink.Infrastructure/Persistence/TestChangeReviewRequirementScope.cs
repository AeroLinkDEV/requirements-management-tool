using AeroLink.Domain.Baselines;
using AeroLink.Domain.ChangeControl;
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
    public static async Task<IReadOnlyList<TestChangeReviewRequirementChoice>> ForReviewAsync(
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
        if (carriedRevisionIds.Count == 0) return [];

        var wantedLevel = review.ProcedureLevel() switch
        {
            TestProcedureLevel.System => RequirementLevel.System,
            TestProcedureLevel.HighLevel => RequirementLevel.HighLevel,
            _ => RequirementLevel.LowLevel
        };
        return await (from revision in db.RequirementRevisions.AsNoTracking()
                          .Where(x => carriedRevisionIds.Contains(x.Id))
                      join artifact in db.Requirements.AsNoTracking()
                          .Where(x => x.ProjectId == review.ProjectId && x.Level == wantedLevel)
                          on revision.ArtifactId equals artifact.Id
                      orderby artifact.BaseNumber, revision.Revision
                      select new TestChangeReviewRequirementChoice(
                          artifact.Id,
                          revision.Id,
                          artifact.BaseNumber + "." + (revision.Revision < 10 ? "0" : "") + revision.Revision,
                          revision.Statement,
                          artifact.Level)).ToListAsync(ct);
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
