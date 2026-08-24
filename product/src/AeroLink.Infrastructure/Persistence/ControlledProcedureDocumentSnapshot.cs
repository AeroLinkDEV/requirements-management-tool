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
    string EnvironmentSetup,
    string TestData,
    string OrderedSteps,
    string ExpectedObservations,
    string Cleanup,
    string ToolingAutomation,
    string AuthorId,
    Guid? SourceTestChangeRequestId,
    VerificationProcedureParentKind ParentKind = VerificationProcedureParentKind.Unspecified,
    string DerivedRationale = "",
    IReadOnlyList<Guid>? ParentRevisionIds = null);

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
    /// <summary>Compatibility overload for historical System Procedure/software Case documents.</summary>
    public static Task<ControlledProcedureDocumentSnapshot> ForDocumentAsync(
        AeroLinkDbContext db, Guid baselineId, TestProcedureLevel level, DateTimeOffset generatedAt,
        CancellationToken ct) => ForDocumentAsync(db, baselineId, new VerificationArtifactKey(level switch
        {
            TestProcedureLevel.System => VerificationDiscipline.System,
            TestProcedureLevel.HighLevel => VerificationDiscipline.HighLevelSoftware,
            TestProcedureLevel.LowLevel => VerificationDiscipline.LowLevelSoftware,
            _ => throw new ArgumentOutOfRangeException(nameof(level), level, null),
        }, level == TestProcedureLevel.System
            ? VerificationArtifactKind.Procedure
            : VerificationArtifactKind.Case), generatedAt, ct);

    public static async Task<ControlledProcedureDocumentSnapshot> ForDocumentAsync(
        AeroLinkDbContext db,
        Guid baselineId,
        VerificationArtifactKey artifactKey,
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
                          join procedure in db.TestProcedures.AsNoTracking().Where(x =>
                              x.ArtifactDiscipline == artifactKey.Discipline
                              && x.ArtifactKind == artifactKey.Kind)
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
                              revision.EnvironmentSetup,
                              revision.TestData,
                              revision.OrderedSteps,
                              revision.ExpectedObservations,
                              revision.Cleanup,
                              revision.ToolingAutomation,
                              revision.AuthorId,
                              revision.SourceTestChangeRequestId,
                              revision.ParentKind,
                              revision.DerivedRationale)).ToListAsync(ct);
        }
        else
        {
            // DateTimeOffset ordering is deliberately done in memory for SQLite parity. This path only applies
            // to legacy records and is bounded to one Project/discipline's controlled procedure inventory.
            var candidates = await (from revision in db.TestProcedureRevisions.AsNoTracking()
                                    join procedure in db.TestProcedures.AsNoTracking()
                                        on revision.ProcedureId equals procedure.Id
                                    where procedure.ProjectId == baseline.ProjectId
                                        && procedure.ArtifactDiscipline == artifactKey.Discipline
                                        && procedure.ArtifactKind == artifactKey.Kind
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
                                        revision.EnvironmentSetup,
                                        revision.TestData,
                                        revision.OrderedSteps,
                                        revision.ExpectedObservations,
                                        revision.Cleanup,
                                        revision.ToolingAutomation,
                                        revision.AuthorId,
                                        revision.SourceTestChangeRequestId,
                                        revision.ParentKind,
                                        revision.DerivedRationale,
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
                    x.Steps, x.ExpectedResult, x.EnvironmentSetup, x.TestData, x.OrderedSteps,
                    x.ExpectedObservations, x.Cleanup, x.ToolingAutomation,
                    x.AuthorId, x.SourceTestChangeRequestId,
                    x.ParentKind, x.DerivedRationale))
                .ToList();
        }

        // Exact controlled revisions use the title in their immutable source-TCR snapshot. A revision whose
        // title predates that provenance receives a deterministic, revision-specific compatibility label; the
        // mutable catalog title is never allowed to rewrite an already-created controlled document.
        var titles = await TestProcedureRevisionTitleProjection.ForRevisionsAsync(db,
            rows.Select(x => x.RevisionId).ToList(), ct);
        rows = rows.Select(row => row with { Title = titles[row.RevisionId].Title }).ToList();
        var revisionIds = rows.Select(x => x.RevisionId).ToList();
        // Coverage is an exact parent selection only within the governed requirement membership of the
        // document baseline. A later retarget may add a link to a carried procedure revision, but that link
        // must not leak into regeneration of an older released baseline whose requirement set never contained
        // the new endpoint.
        var baselineRequirementRevisionIds = await db.BaselineRequirements.AsNoTracking()
            .Where(x => x.BaselineId == baselineId)
            .Select(x => x.RevisionId)
            .ToHashSetAsync(ct);
        // A Procedure's Case parent is controlled effectivity, not merely a live cross-reference. The link can
        // legitimately survive while a later Case revision replaces its predecessor, but an older/non-current
        // Case must not leak into a document whose typed manifest selected a different exact Case revision.
        var baselineCaseRevisionIds = artifactKey.Kind == VerificationArtifactKind.Procedure
                                      && artifactKey.Discipline != VerificationDiscipline.System
            ? await (from member in db.BaselineTestProcedures.AsNoTracking()
                     where member.BaselineId == baselineId
                     join @case in db.TestProcedures.AsNoTracking().Where(x =>
                             x.ArtifactDiscipline == artifactKey.Discipline
                             && x.ArtifactKind == VerificationArtifactKind.Case)
                         on member.ProcedureId equals @case.Id
                     select member.RevisionId).ToHashSetAsync(ct)
            : [];
        var coverage = artifactKey.Kind == VerificationArtifactKind.Procedure
                       && artifactKey.Discipline != VerificationDiscipline.System
            ? await db.TestCaseProcedureLinks.AsNoTracking()
                .Where(x => revisionIds.Contains(x.ProcedureRevisionId)
                    && baselineCaseRevisionIds.Contains(x.CaseRevisionId))
                .Select(x => new { x.ProcedureRevisionId, ParentRevisionId = x.CaseRevisionId })
                .ToListAsync(ct)
            : await db.TestCoverage.AsNoTracking()
                .Where(x => revisionIds.Contains(x.ProcedureRevisionId)
                    // #709 suspect carry-forward is lifecycle evidence, not an approved exact-parent decision.
                    // It may live on the same immutable revision until a verification engineer confirms it, but
                    // controlled documents must never present that unconfirmed endpoint as signed coverage.
                    && !x.IsSuspect
                    && baselineRequirementRevisionIds.Contains(x.RequirementRevisionId))
                .Select(x => new { x.ProcedureRevisionId, ParentRevisionId = x.RequirementRevisionId })
                .ToListAsync(ct);
        var parentIds = coverage.GroupBy(x => x.ProcedureRevisionId)
            .ToDictionary(x => x.Key, x => (IReadOnlyList<Guid>)x.Select(y => y.ParentRevisionId)
                .Distinct().OrderBy(y => y).ToArray());
        rows = rows.Select(row => row with
        {
            ParentRevisionIds = parentIds.GetValueOrDefault(row.RevisionId, Array.Empty<Guid>())
        }).ToList();
        return new(exact, rows);
    }
}
