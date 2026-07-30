using AeroLink.Domain.Verification;
using Microsoft.EntityFrameworkCore;

namespace AeroLink.Infrastructure.Persistence;

/// <summary>
/// The three test sets a build has — System, software HLR, software LLR — created when first asked for.
///
/// Built lazily rather than when a build is created, in the same shape as the requirements-document
/// synchronization: a build that nobody has opened needs no rows, and a build created before this existed
/// has to get them anyway, so the two cases collapse into one if creation happens on first read.
///
/// The first creation carries forward what the product already knew. "Must be run before release" used to be
/// a checkbox on individual verification decisions, and every procedure that checkbox pointed at is exactly a
/// procedure the build has to run — so those become the set's first entries. Without that, replacing the
/// checkbox would silently discard every decision anybody had already recorded with it.
/// </summary>
public sealed class BuildTestSetService(AeroLinkDbContext db)
{
    private static readonly TestChangeReviewDiscipline[] Disciplines =
        [TestChangeReviewDiscipline.System, TestChangeReviewDiscipline.HighLevelSoftware, TestChangeReviewDiscipline.LowLevelSoftware];

    /// <summary>
    /// Returns the build's sets, creating and seeding any that do not exist yet.
    ///
    /// Safe to call repeatedly and from more than one request: a set that another caller created in the
    /// meantime loses the unique index race, and the loser re-reads rather than failing, because two people
    /// opening the same build at once is ordinary and neither should see an error for it.
    /// </summary>
    public async Task<IReadOnlyList<BuildTestSet>> EnsureForReleaseAsync(Guid projectId, Guid releaseId, CancellationToken ct = default)
    {
        var existing = await db.BuildTestSets.Include(x => x.Entries)
            .Where(x => x.ReleaseId == releaseId).ToListAsync(ct);
        var missing = Disciplines.Where(d => existing.All(x => x.Discipline != d)).ToList();
        if (missing.Count == 0) return existing;

        var now = DateTimeOffset.UtcNow;
        var carried = await CarriedForwardAsync(releaseId, ct);
        foreach (var discipline in missing)
        {
            var set = new BuildTestSet(projectId, releaseId, discipline, now);
            foreach (var entry in carried.Where(x => x.Discipline == discipline))
                set.Include(entry.DecidedBy, entry.ProcedureRevisionId, TestSelectionReason.ChangedRequirement,
                    $"Carried forward from {entry.SubjectDisplayNumber}, which required evidence before release.", now);
            db.BuildTestSets.Add(set);
        }

        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException)
        {
            // Another request created them between the read and the write. Theirs is as good as ours.
            db.ChangeTracker.Clear();
        }
        return await db.BuildTestSets.Include(x => x.Entries).Where(x => x.ReleaseId == releaseId).ToListAsync(ct);
    }

    private sealed record CarriedEntry(TestChangeReviewDiscipline Discipline, Guid ProcedureRevisionId,
        string SubjectDisplayNumber, string DecidedBy);

    private async Task<IReadOnlyList<CarriedEntry>> CarriedForwardAsync(Guid releaseId, CancellationToken ct) =>
        await (from item in db.VerificationImpactItems.AsNoTracking()
               join review in db.TestChangeReviews.AsNoTracking() on item.TestChangeReviewId equals review.Id
               where item.ReleaseId == releaseId
                     && item.PreReleaseEvidenceRequired
                     && item.ResolvedProcedureRevisionId != null
               select new CarriedEntry(review.Discipline, item.ResolvedProcedureRevisionId!.Value,
                   item.SubjectDisplayNumber, item.ResolvedBy ?? "verification"))
            .Distinct()
            .ToListAsync(ct);
}
