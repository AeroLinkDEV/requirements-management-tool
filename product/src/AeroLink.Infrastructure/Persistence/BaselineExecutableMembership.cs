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
    /// Recovers ALL exact source Case revisions for a baseline's executable Procedure selections through the
    /// immutable Case→Procedure links. A Procedure selection with no link is a standalone/Derived Procedure
    /// and contributes no Case obligation. Every parent is preserved — one-to-many and many-to-many exact
    /// Case-to-Procedure relationships are never collapsed.
    /// </summary>
    public static async Task<IReadOnlyList<Guid>> SourceCaseRevisionIdsAsync(
        AeroLinkDbContext db, IReadOnlyCollection<BaselineExecutableSelection> selections, CancellationToken ct)
    {
        var procedureRevisionIds = selections.Where(x => x.Kind == VerificationArtifactKind.Procedure)
            .Select(x => x.RevisionId).ToList();
        if (procedureRevisionIds.Count == 0) return [];
        return await (from link in db.TestCaseProcedureLinks.AsNoTracking()
                      where procedureRevisionIds.Contains(link.ProcedureRevisionId)
                      select link.CaseRevisionId).Distinct().ToListAsync(ct);
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
        IReadOnlyList<Guid> sourceCaseRevisionIds,
        Guid baselineId,
        IReadOnlySet<TestProcedureLevel> procedureEnabledLevels,
        CancellationToken ct)
    {
        return selections.Where(x => x.Kind == VerificationArtifactKind.Case)
            .Select(x => x.RevisionId)
            .Concat(sourceCaseRevisionIds)
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

    /// <summary>
    /// The two sides of one typed effectivity population for a baseline:
    /// <list type="bullet">
    /// <item><see cref="CoverageRevisionIds"/> — exact revisions whose TestCoverage rows count for
    /// requirement coverage (System Procedure revisions plus the effective software Case population).</item>
    /// <item><see cref="ExecutableRevisionIds"/> — exact effective executable revisions (System Procedures,
    /// software Procedures under the full profile, software Cases under a Case-only profile).</item>
    /// </list>
    /// Consumers that answer "what covers this requirement" use CoverageRevisionIds; consumers that answer
    /// "what does this build execute" use ExecutableRevisionIds. Never the same set.
    /// </summary>
    public static async Task<EffectiveExecutablePopulation> ForPopulationAsync(
        AeroLinkDbContext db,
        Guid baselineId,
        IReadOnlySet<TestProcedureLevel> procedureEnabledLevels,
        CancellationToken ct)
    {
        var selections = await ForBaselineAsync(db, baselineId, ct);
        var sourceCaseRevisionIds = await SourceCaseRevisionIdsAsync(db, selections, ct);
        var coverageRevisionIds = selections
            .Where(x => x.Level == TestProcedureLevel.System)
            .Select(x => x.RevisionId)
            .Concat(await EffectiveCaseRevisionIdsAsync(db, selections, sourceCaseRevisionIds,
                baselineId, procedureEnabledLevels, ct))
            .Distinct().ToList();
        var executableRevisionIds = selections
            .Where(x => !(procedureEnabledLevels.Contains(x.Level)
                && x.Kind == VerificationArtifactKind.Case))
            .Select(x => x.RevisionId).Distinct().ToList();
        return new EffectiveExecutablePopulation(coverageRevisionIds, executableRevisionIds);
    }
}

/// <summary>One typed effectivity population: coverage-side revisions and executable-side revisions.</summary>
public sealed record EffectiveExecutablePopulation(
    IReadOnlyList<Guid> CoverageRevisionIds,
    IReadOnlyList<Guid> ExecutableRevisionIds);
