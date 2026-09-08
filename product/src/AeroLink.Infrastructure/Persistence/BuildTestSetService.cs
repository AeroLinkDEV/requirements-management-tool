using AeroLink.Domain.Verification;
using AeroLink.Domain.Hierarchy;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Npgsql;

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
public sealed class BuildTestSetService(AeroLinkDbContext db, ILadderPolicy? policy = null,
    IProjectLadderPolicyResolver? policyResolver = null)
{
    private readonly ILadderPolicy fallbackPolicy = policy ?? LegacyLadderPolicy.Instance;

    /// <summary>
    /// Returns the build's sets, creating and seeding any that do not exist yet.
    ///
    /// Safe to call repeatedly and from more than one request: a set that another caller created in the
    /// meantime loses the exact ReleaseId/Discipline unique index race, and the loser re-reads only after
    /// proving that the winner persisted every configured discipline. Other persistence failures remain
    /// failures; a readiness read must not turn an incomplete write into an apparent success.
    /// </summary>
    public async Task<IReadOnlyList<BuildTestSet>> EnsureForReleaseAsync(Guid projectId, Guid releaseId, CancellationToken ct = default)
    {
        var existing = await db.BuildTestSets.Include(x => x.Entries)
            .Where(x => x.ReleaseId == releaseId).ToListAsync(ct);
        var ladderPolicy = policyResolver is null
            ? fallbackPolicy
            : await policyResolver.ResolveAsync(projectId, ct);
        var disciplines = ladderPolicy.OrderedLevels
            .Where(level => ladderPolicy.Definition(level).Has(LevelCapabilities.HasVerification))
            .Select(ladderPolicy.Discipline)
            .ToArray();
        var configuredDisciplines = disciplines.ToHashSet();
        existing = existing.Where(x => configuredDisciplines.Contains(x.Discipline)).ToList();
        var missing = disciplines.Where(d => existing.All(x => x.Discipline != d)).ToList();
        if (missing.Count == 0) return existing;

        var now = DateTimeOffset.UtcNow;
        var carried = (await CarriedForwardAsync(releaseId, ct))
            .Where(x => configuredDisciplines.Contains(x.Discipline)).ToList();
        var candidates = new List<BuildTestSet>(missing.Count);
        foreach (var discipline in missing)
        {
            var set = new BuildTestSet(projectId, releaseId, discipline, now);
            foreach (var entry in carried.Where(x => x.Discipline == discipline))
                set.Include(entry.DecidedBy, entry.ProcedureRevisionId, TestSelectionReason.ChangedRequirement,
                    $"Carried forward from {entry.SubjectDisplayNumber}, which required evidence before release.", now);
            db.BuildTestSets.Add(set);
            candidates.Add(set);
        }

        try { await db.SaveChangesAsync(ct); }
        catch (OperationCanceledException)
        {
            // Do not leave our candidate graph armed for an accidental later save. The caller's unrelated
            // tracked changes remain intact, and cancellation remains cancellation.
            DetachCandidateGraph(candidates);
            throw;
        }
        catch (DbUpdateException ex)
        {
            // EF leaves failed Added graphs tracked. Detach only the graph this initializer owns; clearing the
            // whole unit of work would discard an unrelated mutation made by the caller in the same request.
            DetachCandidateGraph(candidates);
            if (!IsExpectedSetUniquenessViolation(ex)) throw;

            // A matching constraint error is only a concurrency loser if the database now contains every
            // configured set. A fault injector, partial writer, or invalid reference can otherwise produce
            // the same broad EF exception shape; preserve the original diagnostic in those cases.
            var winner = await ReadConfiguredSetsAsync(releaseId, configuredDisciplines, ct);
            if (winner.Select(x => x.Discipline).ToHashSet().SetEquals(configuredDisciplines))
                return winner;
            throw;
        }
        return await ReadConfiguredSetsAsync(releaseId, configuredDisciplines, ct);
    }

    private async Task<IReadOnlyList<BuildTestSet>> ReadConfiguredSetsAsync(Guid releaseId,
        IReadOnlySet<TestChangeReviewDiscipline> configuredDisciplines, CancellationToken ct) =>
        await db.BuildTestSets.Include(x => x.Entries)
            .Where(x => x.ReleaseId == releaseId && configuredDisciplines.Contains(x.Discipline))
            .ToListAsync(ct);

    private void DetachCandidateGraph(IEnumerable<BuildTestSet> candidates)
    {
        var owned = candidates.SelectMany(set => set.Entries.Cast<object>().Append(set)).ToHashSet();
        foreach (var entry in db.ChangeTracker.Entries()
                     .Where(entry => owned.Contains(entry.Entity)).ToList())
            entry.State = EntityState.Detached;
    }

    /// <summary>
    /// The only recoverable write conflict is the named one-set-per-release/discipline unique index. Provider
    /// error codes alone are too broad: PostgreSQL 23505 and SQLite 19 also cover unrelated uniqueness rules.
    /// </summary>
    private static bool IsExpectedSetUniquenessViolation(DbUpdateException exception)
    {
        for (var inner = exception.InnerException; inner is not null; inner = inner.InnerException)
        {
            if (inner is PostgresException { SqlState: "23505" } postgres
                && string.Equals(postgres.ConstraintName,
                    "IX_build_test_sets_ReleaseId_Discipline", StringComparison.OrdinalIgnoreCase))
                return true;

            if (inner is SqliteException { SqliteErrorCode: 19, SqliteExtendedErrorCode: 2067 } sqlite)
            {
                var message = sqlite.Message ?? string.Empty;
                if (message.Contains("IX_build_test_sets_ReleaseId_Discipline", StringComparison.OrdinalIgnoreCase)
                    || (message.Contains("build_test_sets.ReleaseId", StringComparison.OrdinalIgnoreCase)
                        && message.Contains("build_test_sets.Discipline", StringComparison.OrdinalIgnoreCase)))
                    return true;
            }
        }
        return false;
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
