using AeroLink.Domain.Verification;
using Microsoft.EntityFrameworkCore;

namespace AeroLink.Infrastructure.Persistence;

/// <summary>
/// Which recorded determinations belong to one build.
///
/// There is exactly one answer to this question, and it is asked from two places: the release gates, which
/// decide whether a build may ship, and the Test Results workspace, which shows a lead what the gates will
/// see. Written twice, the two drifted — both relaxed to "any execution at all" when the build had not yet
/// selected an immutable software build, because the predicate said
/// <c>campaign.SoftwareBuildId == null || execution.SoftwareBuildId == campaign.SoftwareBuildId</c> and the
/// left side is simply true for every row once the campaign has no build. Nothing constrained the release,
/// so a determination recorded against released Build 1.5 could satisfy Build 1.6's verification and evidence
/// gates and appear as Build 1.6's latest result.
///
/// The rule has two cases and no third:
///
/// * the build has recorded an immutable software build — only executions attributed to that exact build
///   count, because that is the configuration the release is being decided about;
/// * it has not — only executions belonging to this release and attributed to no software build count.
///   Work in progress legitimately has no build identity yet, but it still belongs to exactly one release.
///
/// An execution carrying another release's identity is never in scope, however recent it is. A newer
/// historical run must not outrank an older active one.
/// </summary>
public static class ExecutionScope
{
    /// <summary>
    /// True when this execution counts toward the given release's readiness and test-set presentation.
    /// Expression-bodied so EF can translate it inside a query as well as evaluate it in memory.
    /// </summary>
    public static bool Belongs(TestExecution execution, Guid releaseId, Guid? softwareBuildId) =>
        softwareBuildId == null
            ? execution.ReleaseId == releaseId && execution.SoftwareBuildId == null
            : execution.SoftwareBuildId == softwareBuildId;

    /// <summary>
    /// The executions of the named procedure revisions that belong to this release, newest determination per
    /// procedure revision. Materialized before ordering because SQLite cannot translate an ORDER BY over a
    /// DateTimeOffset, which is also why the release filter is applied in the query rather than after it.
    /// </summary>
    public static async Task<IReadOnlyDictionary<Guid, TestExecution>> LatestByProcedureAsync(
        AeroLinkDbContext db,
        IReadOnlyCollection<Guid> procedureRevisionIds,
        Guid releaseId,
        Guid? softwareBuildId,
        CancellationToken ct)
    {
        if (procedureRevisionIds.Count == 0) return new Dictionary<Guid, TestExecution>();
        var scoped = softwareBuildId is null
            ? db.TestExecutions.AsNoTracking().Where(x => procedureRevisionIds.Contains(x.ProcedureRevisionId)
                && x.ReleaseId == releaseId && x.SoftwareBuildId == null)
            : db.TestExecutions.AsNoTracking().Where(x => procedureRevisionIds.Contains(x.ProcedureRevisionId)
                && x.SoftwareBuildId == softwareBuildId);
        return (await scoped.ToListAsync(ct))
            .GroupBy(x => x.ProcedureRevisionId)
            .ToDictionary(group => group.Key,
                group => group.OrderByDescending(x => x.ExecutedAt).ThenByDescending(x => x.RecordedAt).First());
    }
}
