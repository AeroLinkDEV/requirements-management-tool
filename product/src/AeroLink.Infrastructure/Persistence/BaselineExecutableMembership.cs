using AeroLink.Domain.Verification;
using Microsoft.EntityFrameworkCore;

namespace AeroLink.Infrastructure.Persistence;

/// <summary>One executable baseline selection with its exact revision identity.</summary>
public sealed record BaselineExecutableSelection(Guid ProcedureId, Guid RevisionId,
    TestProcedureLevel Level, VerificationArtifactKind Kind);

/// <summary>
/// The one exact-membership contract for verification baselines (#726 blocker 4).
///
/// BaselineTestProcedures always holds the EFFECTIVE EXECUTABLE selections: System Procedures, software
/// Cases under a Case-only profile, and software Procedures under the full profile. Requirement coverage
/// remains on Cases (TestCoverage). The source Case population of a Procedure-enabled baseline is recovered
/// through the immutable exact Case→Procedure links, never by searching for a Case baseline row the cutover
/// intentionally rebound. Case-only baselines recover their source from the selection itself.
/// </summary>
public static class BaselineExecutableMembership
{
    public static async Task<IReadOnlyList<BaselineExecutableSelection>> ForBaselineAsync(
        AeroLinkDbContext db, Guid baselineId, CancellationToken ct)
    {
        return await (from selection in db.BaselineTestProcedures.AsNoTracking()
                      where selection.BaselineId == baselineId
                      join revision in db.TestProcedureRevisions.AsNoTracking()
                          on selection.RevisionId equals revision.Id
                      join procedure in db.TestProcedures.AsNoTracking()
                          on selection.ProcedureId equals procedure.Id
                      select new BaselineExecutableSelection(procedure.Id, revision.Id,
                          procedure.Level, procedure.ArtifactKind)).ToListAsync(ct);
    }

    /// <summary>
    /// Recovers the exact source Case revisions for a baseline's executable Procedure selections through the
    /// immutable Case→Procedure links. A Procedure selection with no link is a standalone/Derived Procedure
    /// and contributes no Case obligation.
    /// </summary>
    public static async Task<IReadOnlyDictionary<Guid, Guid>> SourceCaseRevisionsAsync(
        AeroLinkDbContext db, IReadOnlyCollection<BaselineExecutableSelection> selections, CancellationToken ct)
    {
        var procedureRevisionIds = selections.Where(x => x.Kind == VerificationArtifactKind.Procedure)
            .Select(x => x.RevisionId).ToList();
        if (procedureRevisionIds.Count == 0) return new Dictionary<Guid, Guid>();
        return await (from link in db.TestCaseProcedureLinks.AsNoTracking()
                      where procedureRevisionIds.Contains(link.ProcedureRevisionId)
                      select new { link.ProcedureRevisionId, link.CaseRevisionId })
            .GroupBy(x => x.ProcedureRevisionId)
            .ToDictionaryAsync(x => x.Key, x => x.First().CaseRevisionId, ct);
    }

    /// <summary>
    /// The effective exact software Case revisions of one baseline, used whenever Procedure-enabled execution
    /// still reads Case-scoped coverage or Case-to-Procedure obligations. A Case counts when it is still a
    /// Case selection in the baseline, when it is the source of a Procedure selection (the post-cutover
    /// rebind), or when its authoritative EffectiveBaselineId names this baseline (a linked Procedure that is
    /// missing from or wrongly rebound out of this baseline).
    /// </summary>
    public static async Task<IReadOnlyList<Guid>> EffectiveCaseRevisionIdsAsync(
        AeroLinkDbContext db,
        IReadOnlyList<BaselineExecutableSelection> selections,
        IReadOnlyDictionary<Guid, Guid> sourceCases,
        Guid baselineId,
        IReadOnlySet<TestProcedureLevel> procedureEnabledLevels,
        CancellationToken ct)
    {
        return selections.Where(x => x.Kind == VerificationArtifactKind.Case)
            .Select(x => x.RevisionId)
            .Concat(sourceCases.Values)
            .Concat(await (from revision in db.TestProcedureRevisions.AsNoTracking()
                           join procedure in db.TestProcedures.AsNoTracking()
                               on revision.ProcedureId equals procedure.Id
                           where revision.EffectiveBaselineId == baselineId
                               && procedure.ArtifactKind == VerificationArtifactKind.Case
                               && procedureEnabledLevels.Contains(procedure.Level)
                           select revision.Id).ToListAsync(ct))
            .Distinct()
            .ToList();
    }
}
