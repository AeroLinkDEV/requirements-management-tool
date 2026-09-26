using AeroLink.Domain.Hierarchy;
using AeroLink.Domain.Verification;
using Microsoft.EntityFrameworkCore;

namespace AeroLink.Infrastructure.Persistence;

/// <summary>What a verification artifact's latest build-scoped determination says about it.</summary>
public enum CaseExecutionStatus { Passed, Failed, Blocked, NotRun }

/// <summary>One executable verification artifact in a baseline and its latest result for the build.</summary>
public sealed record CaseExecutionRow(Guid ArtifactId, Guid RevisionId, string DisplayNumber, string Title,
    TestProcedureLevel Level, VerificationArtifactKind ArtifactKind, CaseExecutionStatus Status,
    Guid? LatestExecutionId, DateTimeOffset? ExecutedAt);

/// <summary>
/// DEC-144: in a project without Requirements there is no requirement coverage to measure, so each case shows
/// its execution status instead: passed, failed, blocked or not run.
///
/// The population is the baseline's verification manifest, limited to the artifacts that execute under the
/// project's ladder. The result is the latest determination that belongs to the build, through
/// <see cref="ExecutionScope"/>, the same rule the release gates and the Test Results workspace use.
/// </summary>
public static class CaseExecutionStatusProjection
{
    public static async Task<IReadOnlyList<CaseExecutionRow>> ForBaselineAsync(AeroLinkDbContext db, Guid baselineId,
        Guid releaseId, Guid? softwareBuildId, ILadderPolicy ladderPolicy, CancellationToken ct)
    {
        var members = await (from member in db.BaselineTestProcedures.AsNoTracking().Where(x => x.BaselineId == baselineId)
                             join revision in db.TestProcedureRevisions.AsNoTracking() on member.RevisionId equals revision.Id
                             join artifact in db.TestProcedures.AsNoTracking()
                                     .Where(EffectiveExecutableArtifact.ExecutablePredicate(ladderPolicy))
                                 on member.ProcedureId equals artifact.Id
                             where revision.State != TestProcedureState.Retired
                             select new
                             {
                                 artifact.Id, RevisionId = revision.Id, artifact.BaseNumber, revision.Revision,
                                 artifact.Title, artifact.Level, artifact.ArtifactKind,
                             }).ToListAsync(ct);
        var latest = await ExecutionScope.LatestByProcedureAsync(db,
            members.Select(x => x.RevisionId).ToList(), releaseId, softwareBuildId, ct);
        return members
            .OrderBy(x => x.BaseNumber, StringComparer.Ordinal)
            .Select(x =>
            {
                var run = latest.GetValueOrDefault(x.RevisionId);
                var status = run?.Outcome switch
                {
                    TestOutcome.Pass => CaseExecutionStatus.Passed,
                    TestOutcome.Fail => CaseExecutionStatus.Failed,
                    TestOutcome.Blocked => CaseExecutionStatus.Blocked,
                    _ => CaseExecutionStatus.NotRun,
                };
                return new CaseExecutionRow(x.Id, x.RevisionId, $"{x.BaseNumber}.{x.Revision:D2}", x.Title, x.Level,
                    x.ArtifactKind, status, run?.Id, run?.ExecutedAt);
            })
            .ToList();
    }
}
