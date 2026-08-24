using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Traceability;
using AeroLink.Domain.Verification;
using Microsoft.EntityFrameworkCore;

namespace AeroLink.Infrastructure.Persistence;

/// <summary>One exact Case→Procedure obligation and its build-applicable satisfaction state.</summary>
public sealed record CaseProcedureObligation(Guid CaseRevisionId,
    IReadOnlyList<Guid> RequiredProcedureRevisionIds,
    IReadOnlyList<Guid> SatisfiedProcedureRevisionIds,
    IReadOnlyList<Guid> UnsatisfiedProcedureRevisionIds,
    bool HasSuspectLink,
    bool Satisfied);

/// <summary>
/// The authoritative Case→Procedure satisfaction rule from #726 (blocker 3), used by release readiness and
/// reconciliation. For every effective exact software Case revision in a Procedure-enabled candidate
/// baseline: zero exact Procedure links is unsatisfied; every required linked Procedure must be satisfied,
/// where a linked Procedure counts only when its exact revision is effective in that baseline, is selected
/// in the same release and matching HLR/LLR discipline BuildTestSet, and its latest execution under the
/// existing release/build ExecutionScope is Pass. Suspect links, Failed/Blocked/missing, cross-release,
/// cross-build, cross-discipline, non-effective, Derived (unlinked), and superseded evidence never count.
/// Checksummed evidence remains a separate release gate.
/// </summary>
public static class CaseProcedureSatisfaction
{
    public static async Task<IReadOnlyList<CaseProcedureObligation>> ForBaselineAsync(
        AeroLinkDbContext db, Guid baselineId, Guid releaseId, Guid? softwareBuildId,
        IReadOnlySet<TestProcedureLevel> procedureEnabledLevels, CancellationToken ct)
    {
        var selections = await BaselineExecutableMembership.ForBaselineAsync(db, baselineId, ct);
        var sourceCaseRevisionIds = await BaselineExecutableMembership.SourceCaseRevisionIdsAsync(
            db, selections, ct);
        var procedureSelectionsByRevision = selections
            .Where(x => x.Kind == VerificationArtifactKind.Procedure)
            .GroupBy(x => x.RevisionId).ToDictionary(x => x.Key, x => x.First());
        var caseRevisionIds = await BaselineExecutableMembership.EffectiveCaseRevisionIdsAsync(
            db, selections, sourceCaseRevisionIds, baselineId, procedureEnabledLevels, ct);
        if (caseRevisionIds.Count == 0) return [];
        var caseLevels = await (from revision in db.TestProcedureRevisions.AsNoTracking()
                                where caseRevisionIds.Contains(revision.Id)
                                join procedure in db.TestProcedures.AsNoTracking()
                                    on revision.ProcedureId equals procedure.Id
                                select new { revision.Id, procedure.Level }).ToListAsync(ct);
        var levelByCaseRevision = caseLevels.ToDictionary(x => x.Id, x => x.Level);
        var casesInScope = caseRevisionIds
            .Where(id => levelByCaseRevision.TryGetValue(id, out var level)
                && procedureEnabledLevels.Contains(level)).ToList();
        if (casesInScope.Count == 0) return [];

        var links = await (from link in db.TestCaseProcedureLinks.AsNoTracking()
                           where casesInScope.Contains(link.CaseRevisionId)
                           select new { link.CaseRevisionId, link.ProcedureRevisionId, link.Id }).ToListAsync(ct);
        var linkIds = links.Select(x => x.Id).ToList();
        var suspectLifecycles = await (from lifecycle in db.ExactLinkSuspectLifecycles.AsNoTracking()
                                       where lifecycle.LinkKind == ExactLinkKind.CaseProcedure
                                           && linkIds.Contains(lifecycle.LinkId)
                                       select new { lifecycle.LinkId, lifecycle.State }).ToListAsync(ct);
        var suspectByLink = suspectLifecycles
            .Where(x => x.State != ExactLinkLifecycleState.Closed)
            .Select(x => x.LinkId).ToHashSet();
        var requiredByCase = links.GroupBy(x => x.CaseRevisionId)
            .ToDictionary(x => x.Key, x => x.Select(y => y.ProcedureRevisionId).Distinct().ToList());
        var allRequiredProcedureRevisionIds = requiredByCase.Values.SelectMany(x => x).Distinct().ToList();

        // BuildTestSet selections per discipline: an obligation is only build-applicable when the exact
        // Procedure revision is selected in the SAME release and the matching HLR/LLR discipline set.
        var selectedRevisionIds = await (from entry in db.BuildTestSetEntries.AsNoTracking()
                                         join set in db.BuildTestSets.AsNoTracking()
                                             on entry.BuildTestSetId equals set.Id
                                         where set.ReleaseId == releaseId
                                             && allRequiredProcedureRevisionIds.Contains(entry.ProcedureRevisionId)
                                         select new { set.Discipline, entry.ProcedureRevisionId }).ToListAsync(ct);
        var selectedByRevision = selectedRevisionIds.GroupBy(x => x.ProcedureRevisionId)
            .ToDictionary(x => x.Key, x => x.Select(y => y.Discipline).ToHashSet());
        var procedureLevels = await (from revision in db.TestProcedureRevisions.AsNoTracking()
                                     where allRequiredProcedureRevisionIds.Contains(revision.Id)
                                     join procedure in db.TestProcedures.AsNoTracking()
                                         on revision.ProcedureId equals procedure.Id
                                     select new { revision.Id, procedure.Level }).ToListAsync(ct);
        var disciplineByRevision = procedureLevels.ToDictionary(x => x.Id,
            x => x.Level == TestProcedureLevel.HighLevel
                ? TestChangeReviewDiscipline.HighLevelSoftware
                : TestChangeReviewDiscipline.LowLevelSoftware);
        var latest = await ExecutionScope.LatestByProcedureAsync(
            db, allRequiredProcedureRevisionIds, releaseId, softwareBuildId, ct);

        var obligations = new List<CaseProcedureObligation>();
        foreach (var caseRevisionId in casesInScope)
        {
            var required = requiredByCase.TryGetValue(caseRevisionId, out var ids) ? ids : [];
            if (required.Count == 0)
            {
                obligations.Add(new CaseProcedureObligation(caseRevisionId, [], [], [], false, false));
                continue;
            }
            var satisfied = new List<Guid>();
            var unsatisfied = new List<Guid>();
            var hasSuspect = false;
            foreach (var linkId in links.Where(x => x.CaseRevisionId == caseRevisionId).Select(x => x.Id))
            {
                if (suspectByLink.Contains(linkId)) { hasSuspect = true; continue; }
            }
            foreach (var procedureRevisionId in required)
            {
                var effective = procedureSelectionsByRevision.ContainsKey(procedureRevisionId);
                var discipline = disciplineByRevision.TryGetValue(procedureRevisionId, out var d) ? d : (TestChangeReviewDiscipline?)null;
                var selected = discipline is { } known
                    && selectedByRevision.TryGetValue(procedureRevisionId, out var disciplines)
                    && disciplines.Contains(known);
                var pass = latest.TryGetValue(procedureRevisionId, out var execution)
                    && execution.Outcome == TestOutcome.Pass;
                if (effective && selected && pass && !hasSuspect) satisfied.Add(procedureRevisionId);
                else unsatisfied.Add(procedureRevisionId);
            }
            obligations.Add(new CaseProcedureObligation(caseRevisionId, required, satisfied,
                unsatisfied, hasSuspect,
                !hasSuspect && required.Count > 0 && unsatisfied.Count == 0));
        }
        return obligations;
    }
}
