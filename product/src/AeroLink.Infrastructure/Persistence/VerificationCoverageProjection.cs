using Microsoft.EntityFrameworkCore;

namespace AeroLink.Infrastructure.Persistence;

/// <summary>
/// One exact requirement-revision to procedure-revision applicability link.
///
/// Procedure lifecycle and applicability are deliberately separate. An approved procedure can still be
/// suspect for changed wording; calling that link "approved coverage" would hide the work while changing the
/// procedure's own controlled state would misstate its history.
/// </summary>
public sealed record VerificationCoverageLinkProjection(
    Guid RequirementRevisionId,
    Guid ProcedureId,
    Guid ProcedureRevisionId,
    string DisplayNumber,
    string Title,
    string Level,
    string ProcedureState,
    bool IsSuspect,
    string CoverageState);

public static class VerificationCoverageProjection
{
    public static async Task<List<VerificationCoverageLinkProjection>> ForRequirementRevisionsAsync(
        AeroLinkDbContext db,
        IReadOnlyCollection<Guid> requirementRevisionIds,
        CancellationToken ct)
    {
        if (requirementRevisionIds.Count == 0) return [];
        var ids = requirementRevisionIds.Distinct().ToList();
        return await (from coverage in db.TestCoverage.AsNoTracking()
                      where ids.Contains(coverage.RequirementRevisionId)
                      join revision in db.TestProcedureRevisions.AsNoTracking()
                          on coverage.ProcedureRevisionId equals revision.Id
                      join procedure in db.TestProcedures.AsNoTracking()
                          on revision.ProcedureId equals procedure.Id
                      orderby procedure.BaseNumber, revision.Revision
                      select new VerificationCoverageLinkProjection(
                          coverage.RequirementRevisionId,
                          procedure.Id,
                          revision.Id,
                          procedure.BaseNumber + "." + revision.Revision.ToString("D2"),
                          procedure.Title,
                          procedure.Level.ToString(),
                          revision.State.ToString(),
                          coverage.IsSuspect,
                          coverage.IsSuspect ? "Suspect" : "Confirmed"))
            .ToListAsync(ct);
    }
}
