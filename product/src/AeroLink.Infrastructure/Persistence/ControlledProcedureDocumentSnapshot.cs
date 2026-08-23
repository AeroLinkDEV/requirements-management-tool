using AeroLink.Domain.Verification;
using Microsoft.EntityFrameworkCore;

namespace AeroLink.Infrastructure.Persistence;

public sealed record ControlledProcedureDocumentRow(
    Guid RevisionId,
    string BaseNumber,
    string Title,
    int Revision,
    TestProcedureState State,
    string Objective,
    string Preconditions,
    string Steps,
    string ExpectedResult,
    string AuthorId,
    Guid? SourceTestChangeRequestId);

public sealed record ControlledProcedureDocumentSnapshot(
    bool IsExactManifest,
    IReadOnlyList<ControlledProcedureDocumentRow> Rows);

/// <summary>
/// The exact data population represented by one controlled test-procedure document record.
///
/// New documents are created only after procedure materialization and therefore render exact immutable
/// BaselineTestProcedures membership. Records created by older AeroLink versions before exact manifests existed
/// cannot acquire that precision retroactively; they are reconstructed from the latest approved, non-retired
/// revision that existed at the document's own GeneratedAt timestamp. Later revisions or later materialization
/// can never change that compatibility snapshot.
/// </summary>
public static class ControlledProcedureDocumentSnapshotProjection
{
    public static async Task<ControlledProcedureDocumentSnapshot> ForDocumentAsync(
        AeroLinkDbContext db,
        Guid baselineId,
        TestProcedureLevel level,
        DateTimeOffset generatedAt,
        CancellationToken ct)
    {
        var baseline = await db.CandidateBaselines.AsNoTracking()
            .Where(x => x.Id == baselineId)
            .Select(x => new { x.ProjectId, x.TestProceduresMaterializedAt })
            .SingleAsync(ct);
        var exact = baseline.TestProceduresMaterializedAt is not null
            && baseline.TestProceduresMaterializedAt <= generatedAt;
        List<ControlledProcedureDocumentRow> rows;
        if (exact)
        {
            rows = await (from member in db.BaselineTestProcedures.AsNoTracking()
                          where member.BaselineId == baselineId
                          join revision in db.TestProcedureRevisions.AsNoTracking()
                              on member.RevisionId equals revision.Id
                          join procedure in db.TestProcedures.AsNoTracking().Where(x => x.Level == level
                              && (x.Level == TestProcedureLevel.System || x.ArtifactKind == VerificationArtifactKind.Case))
                              on member.ProcedureId equals procedure.Id
                          orderby procedure.BaseNumber
                          select new ControlledProcedureDocumentRow(
                              revision.Id,
                              procedure.BaseNumber,
                              procedure.Title,
                              revision.Revision,
                              revision.State,
                              revision.Objective,
                              revision.Preconditions,
                              revision.Steps,
                              revision.ExpectedResult,
                              revision.AuthorId,
                              revision.SourceTestChangeRequestId)).ToListAsync(ct);
        }
        else
        {
            // DateTimeOffset ordering is deliberately done in memory for SQLite parity. This path only applies
            // to legacy records and is bounded to one Project/discipline's controlled procedure inventory.
            var candidates = await (from revision in db.TestProcedureRevisions.AsNoTracking()
                                    join procedure in db.TestProcedures.AsNoTracking()
                                        on revision.ProcedureId equals procedure.Id
                                    where procedure.ProjectId == baseline.ProjectId && procedure.Level == level
                                        && (procedure.Level == TestProcedureLevel.System || procedure.ArtifactKind == VerificationArtifactKind.Case)
                                    select new
                                    {
                                        ProcedureId = procedure.Id,
                                        revision.Id,
                                        procedure.BaseNumber,
                                        procedure.Title,
                                        revision.Revision,
                                        revision.State,
                                        revision.Objective,
                                        revision.Preconditions,
                                        revision.Steps,
                                        revision.ExpectedResult,
                                        revision.AuthorId,
                                        revision.SourceTestChangeRequestId,
                                        revision.CreatedAt,
                                    }).ToListAsync(ct);
            rows = candidates
                // A newer Draft is proposed work, not controlled effectivity. Ignore it while selecting the
                // generation-time revision; a later Retired revision remains eligible and therefore suppresses
                // the procedure when the final Approved-only filter is applied.
                .Where(x => x.CreatedAt <= generatedAt && x.State != TestProcedureState.Draft)
                .GroupBy(x => x.ProcedureId)
                .Select(group => group.OrderByDescending(x => x.Revision).First())
                .Where(x => x.State == TestProcedureState.Approved)
                .OrderBy(x => x.BaseNumber)
                .Select(x => new ControlledProcedureDocumentRow(
                    x.Id, x.BaseNumber, x.Title, x.Revision, x.State, x.Objective, x.Preconditions,
                    x.Steps, x.ExpectedResult, x.AuthorId, x.SourceTestChangeRequestId))
                .ToList();
        }

        // Exact controlled revisions use the title in their immutable source-TCR snapshot. A revision whose
        // title predates that provenance receives a deterministic, revision-specific compatibility label; the
        // mutable catalog title is never allowed to rewrite an already-created controlled document.
        var titles = await TestProcedureRevisionTitleProjection.ForRevisionsAsync(db,
            rows.Select(x => x.RevisionId).ToList(), ct);
        rows = rows.Select(row => row with { Title = titles[row.RevisionId].Title }).ToList();
        return new(exact, rows);
    }
}
