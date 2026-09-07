using System.Text.Json;
using AeroLink.Domain.Identity;
using AeroLink.Domain.Verification;
using Microsoft.EntityFrameworkCore;

namespace AeroLink.Infrastructure.Persistence;

public sealed partial class FmsShowcaseSeeder
{
    private const string LegacyGapArchiveEvent = "ShowcaseLegacyDraftArchived";

    /// <summary>
    /// Early HOME seed runs placed the unapproved coverage-warning fixture on SYSTP-000001. The
    /// maintained seed moved it to SYSTP-000040, but left the old draft behind. Archive only that exact,
    /// unreferenced seed fixture, retaining its entire record in the operator audit. Authored, approved,
    /// materialized or referenced revisions are never eligible. Runs inside the upgrade transaction.
    /// </summary>
    private async Task<string?> ArchiveObsoleteGapDraftAsync(Guid programId, CancellationToken ct)
    {
        var projectId = await db.Projects.Where(x => x.ProgramId == programId).Select(x => x.Id).SingleAsync(ct);
        var draft = await (from revision in db.TestProcedureRevisions
            join artifact in db.TestProcedures on revision.ProcedureId equals artifact.Id
            where artifact.ProjectId == projectId && artifact.BaseNumber == "SYSTP-000001"
                && artifact.Level == TestProcedureLevel.System && artifact.ArtifactKind == VerificationArtifactKind.Procedure
                && revision.Revision == 1
            select revision).SingleOrDefaultAsync(ct);
        if (draft is null) return "No obsolete SYSTP-000001.01 seed draft remains.";
        if (draft.State != TestProcedureState.Draft || draft.AuthorId != "test.author"
            || draft.CreatedAt != new DateTimeOffset(2024, 11, 18, 9, 30, 0, TimeSpan.Zero)
            || draft.SourceTestChangeRequestId is not null || draft.EffectiveBaselineId is not null
            || draft.SelectedApproverId is not null || draft.SourceChangeRequestsJson != "[]"
            || draft.ParentKind != VerificationProcedureParentKind.Unspecified
            || draft.Objective != "Verify oceanic round-robin waypoint sequencing against the revised FMS 1.6 behavior."
            || draft.Preconditions != "Load the FMS 1.6 candidate software and the approved navigation database."
            || draft.Steps != "Initialize oceanic mode, stimulate the revised sequencing inputs, and record each observable output."
            || draft.ExpectedResult != "Every observed output meets the linked requirement acceptance criteria."
            || new[] { draft.EnvironmentSetup, draft.TestData, draft.OrderedSteps, draft.ExpectedObservations,
                draft.Cleanup, draft.ToolingAutomation, draft.DerivedRationale, draft.RetirementRationale }.Any(x => x.Length != 0))
            return "Preserved SYSTP-000001.01: it does not match the obsolete unapproved seed fixture.";

        if (await db.BaselineTestProcedures.AnyAsync(x => x.RevisionId == draft.Id, ct)
            || await db.TestCoverage.AnyAsync(x => x.ProcedureRevisionId == draft.Id, ct)
            || await db.TestExecutions.AnyAsync(x => x.ProcedureRevisionId == draft.Id, ct)
            || await db.TestCaseProcedureLinks.AnyAsync(x => x.CaseRevisionId == draft.Id || x.ProcedureRevisionId == draft.Id, ct)
            // These polymorphic references deliberately have no FK to verification revisions. Keep
            // discussion, attached evidence and authoring-session history attached to a real revision.
            || await db.ArtifactComments.AnyAsync(x => x.RevisionId == draft.Id, ct)
            || await db.ControlledAttachments.AnyAsync(x => x.RevisionId == draft.Id, ct)
            || await db.ControlledAttachmentStorageOperations.AnyAsync(x => x.RevisionId == draft.Id, ct)
            || await db.ArtifactEditSessions.AnyAsync(x => x.RevisionId == draft.Id, ct)
            || await db.ManagedDocumentLinks.AnyAsync(x => x.ArtifactId == draft.Id, ct)
            || await db.ProblemReportLinks.AnyAsync(x => x.ArtifactId == draft.Id, ct))
            return "Preserved SYSTP-000001.01: the legacy draft has controlled references and needs operator disposition.";

        // Audit and removal commit atomically. Remaining restrictive foreign keys also fail closed if an
        // unexpected controlled reference exists. This is not a procedure retirement or an approval.
        var snapshot = JsonSerializer.Serialize(new
        {
            Format = "aerolink-showcase-draft-archive/v1", ProjectId = projectId,
            Identifier = "SYSTP-000001.01", Reason = "Obsolete duplicate of the maintained SYSTP-000040 coverage-warning fixture.",
            Revision = draft
        });
        if (snapshot.Length > 4000) throw new InvalidOperationException("Legacy draft archive exceeds the audit record capacity.");
        db.SecurityAuditEvents.Add(new SecurityAuditEvent(LegacyGapArchiveEvent, "showcase.upgrade",
            draft.Id.ToString("D"), "Archived", snapshot, "", DateTimeOffset.UtcNow));
        db.TestProcedureRevisions.Remove(draft);
        await db.SaveChangesAsync(ct);
        return $"Archived obsolete unapproved SYSTP-000001.01 ({draft.Id:D}) with its full original content and attribution; approved revisions and coverage are unchanged.";
    }
}
