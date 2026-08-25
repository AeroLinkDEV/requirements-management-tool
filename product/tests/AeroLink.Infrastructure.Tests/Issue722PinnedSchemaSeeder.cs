using System.Text;
using AeroLink.Domain.Baselines;
using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Identity;
using AeroLink.Domain.Programs;
using AeroLink.Domain.Requirements;
using AeroLink.Domain.Traceability;
using AeroLink.Domain.Verification;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace AeroLink.Infrastructure.Tests;

/// <summary>
/// Seeds the #722 exact-pre-rename qualification database while it is pinned to
/// 20260822153030_AddNeutralVerificationIdentity, using only raw parameterized SQL that names columns which
/// exist at that pinned schema.
///
/// Why raw SQL: today's EF model contains columns added after the pin (Cleanup and the #738 parent columns
/// among them), so any current-model save against the pinned database would ask for columns that do not
/// exist there. The product never occupies that state — startup runs the whole migration chain before any
/// model use — so the fixture must not either. The domain objects below are constructed in memory purely to
/// produce the exact persisted values (notably the review-cycle snapshot hash that the signature evidence is
/// later checked against); they never touch an EF context. Current-model EF is first used by the test only
/// after the full migration chain has run to head.
///
/// The ten watermark/reference sources the rename migration honours are represented: test_procedures,
/// test_procedure_changes, electronic_signatures (two shapes), test_procedure_revisions
/// SourceChangeRequestsJson, artifact_edit_sessions, identifier_sequences, plus the controlled-document
/// evidence chain; verification_impact_items, saved_procedure_views, artifact_draft_snapshots and
/// artifact_merge_conflicts carry no rows in this fixture and are exercised as empty sources — the
/// equal-safe-watermark assertion proves the identifier_sequences floors are honoured regardless.
/// </summary>
internal static partial class Issue722PinnedSchemaSeeder
{
    internal const string PreRenameMigration = "20260822153030_AddNeutralVerificationIdentity";

    internal sealed record PinnedSeed(
        DateTimeOffset Now,
        Guid ProgramId,
        Guid ProjectId,
        Guid ReleaseId,
        Guid BaselineId,
        Guid HighCaseId,
        Guid LowCaseId,
        Guid SystemProcedureId,
        Guid HighRevisionId,
        Guid LowRevisionId,
        Guid SystemRevisionId,
        Guid OldDocumentId,
        Guid LegacyCaseDocumentId,
        Guid LegacyCaseSectionId,
        Guid CustomDescriptionDocumentId,
        Guid StructuredSessionId,
        Guid SignatureOnlySignatureId,
        Guid SourceChangeRequestId,
        Guid ReviewId,
        Guid ReviewCycleId,
        Guid ReviewStepId,
        int ReviewStepPosition,
        Guid SignatureId,
        Guid ArtifactSignatureId,
        Guid DocumentSignatureId,
        Guid LegacyCommentId,
        Guid NotificationId,
        Guid OldArtifactId,
        string OriginalReviewHash,
        string OldManifestHash,
        string SignatureOnlyHash,
        string LegacyBody,
        string StoredLegacyStorageKey,
        string StoredLegacySha256,
        long StoredLegacySize);

    /// <summary>Deletes and recreates the qualification database at exactly the pinned pre-rename schema.</summary>
    public static async Task PreparePinnedDatabaseAsync(string connectionString)
    {
        await using var db = new AeroLinkDbContext(new DbContextOptionsBuilder<AeroLinkDbContext>()
            .UseNpgsql(connectionString).Options);
        await db.Database.EnsureDeletedAsync();
        await db.Database.GetService<IMigrator>().MigrateAsync(PreRenameMigration);
    }

    public static async Task<PinnedSeed> SeedAsync(string connectionString, string evidenceRoot)
    {
        var now = DateTimeOffset.UtcNow;

        // The domain graph is built in memory only: it produces the exact values the pre-rename rows must
        // carry — notably the review-cycle snapshot hash the signature evidence is checked against — without
        // any current-model EF use against the pinned schema.
        var program = new ProgramRecord("#722 migration program", "C722");
        var project = new ProjectRecord(program.Id, "#722 migration project", "#722 software");
        var release = new SoftwareRelease(project.Id, "1.0", true);
        var baseline = new CandidateBaseline("BL-000722", 0, project.Id, release.Id, null, "#722 baseline", "migration.test", now);
        var high = TestProcedure.LegacySoftwareCaseForMigration(project.Id, "HLRTP-000007", "Legacy high-level case", "migration.test", now,
            TestProcedureLevel.HighLevel);
        var low = TestProcedure.LegacySoftwareCaseForMigration(project.Id, "LLRTP-000019", "Legacy low-level case", "migration.test", now,
            TestProcedureLevel.LowLevel);
        var system = new TestProcedure(project.Id, "SYSTP-000003", "System procedure remains a procedure", "migration.test", now,
            TestProcedureLevel.System);
        const string legacyBody = "Preserve this exact body,\nincluding spacing.";
        const string legacyProvenance = "{\"baseNumber\":\"HLRTP-000007\",\"prose\":\"HLRTP-000007\"}";
        var highRevision = new TestProcedureRevision(high.Id, 0, legacyBody, "legacy preconditions", "legacy steps", "legacy expected",
            TestProcedureState.Approved, "migration.test", now, sourceChangeRequestsJson: legacyProvenance);
        var lowRevision = new TestProcedureRevision(low.Id, 0, "low objective", "low preconditions", "low steps", "low expected",
            TestProcedureState.Approved, "migration.test", now);
        var systemRevision = new TestProcedureRevision(system.Id, 0, "system objective", "system preconditions", "system steps", "system expected",
            TestProcedureState.Approved, "migration.test", now);
        const string oldManifestHash = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        const string signatureOnlyHash = "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc";
        var sourceChange = new SystemChangeRequest("HLRCR-00722", 0, project.Id, release.Id,
            "Legacy software verification source", "Problem", "Analysis", "Solution", "software.author", now,
            ChangeRequestType.Software, softwareLevel: RequirementLevel.HighLevel);
        var review = new TestChangeReview(project.Id, release.Id, sourceChange.Id,
            TestChangeReviewDiscipline.HighLevelSoftware, sourceChange.DisplayNumber, now,
            "HLRTCR-000722", authorId: "verification.author");
        review.RecordTestChangeRequired("verification.author", now);
        review.WriteCase("verification.author", "Rename the existing software verification artifact",
            "The legacy identity says Procedure.", "The software artifact is a Case.",
            "Relabel the exact governed identity without changing its body.", now);
        // The fixture seeds no upstream requirement revision: this legacy identity-only relabel is honestly
        // Derived, which is exactly what the #738 XOR contract requires — inventing an exact parent would
        // fabricate history the fixture never had.
        var procedureChange = review.AddProcedureChange("verification.author", new TestProcedureChangeDraft(
            "HLRTP-000007", 0, TestProcedureLevel.HighLevel, TestProcedureChangeKind.Modify,
            "Legacy high-level case", legacyBody, "legacy preconditions", "legacy steps", "legacy expected",
            "Identity-only migration",
            ParentKind: VerificationProcedureParentKind.Derived,
            DerivedRationale: "The legacy case predates exact-parent allocation and this fixture records no upstream requirement revision."), now);
        var reviewCycle = review.SubmitForReview("verification.author",
            [new ApproverSelection("verification.reviewer", "Verification Reviewer")], true, now);
        var step = reviewCycle.Steps.Single();
        var signature = new ElectronicSignature(Guid.NewGuid(), "verification.reviewer", "Verification Reviewer",
            program.Id, "TestChangeRequest", review.Id, review.DisplayNumber, "Approve", "Approved exact package",
            reviewCycle.SnapshotHash, "127.0.0.1", now, reviewStepId: step.Id,
            reviewCycle: reviewCycle.Sequence, reviewStepPosition: step.Position,
            rationale: "Legacy approval evidence");

        // The legacy frozen rendition lives in a dedicated qualification evidence root on disk: the
        // migration rewrites its identity and the assertions prove the original bytes survive next to the
        // regenerated artifact.
        var files = new EvidenceFileStore(evidenceRoot);
        var legacyBytes = Encoding.UTF8.GetBytes("legacy frozen software document bytes");
        await using var legacySource = new MemoryStream(legacyBytes, writable: false);
        var storedLegacy = await files.StoreAsync(legacySource, "legacy-hlr.docx",
            "application/vnd.openxmlformats-officedocument.wordprocessingml.document", CancellationToken.None);

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();

        await InsertProgramAsync(connection, program);
        await InsertProjectAsync(connection, project);
        await InsertReleaseAsync(connection, release);
        await InsertBaselineAsync(connection, baseline, now);
        await InsertProcedureAsync(connection, high);
        await InsertProcedureAsync(connection, low);
        await InsertProcedureAsync(connection, system);
        await InsertRevisionAsync(connection, highRevision);
        await InsertRevisionAsync(connection, lowRevision);
        await InsertRevisionAsync(connection, systemRevision);
        var oldDocument = new ControlledDocument(project.Id, release.Id, baseline.Id,
            ControlledDocumentType.HighLevelTestProcedures, "HLRTD-000722", "Legacy HLR Test Procedures", 0,
            new string('a', 64), 1, now);
        await InsertControlledDocumentAsync(connection, oldDocument);
        var legacyCaseDocument = new TestProcedureDocument(project.Id, "HLRTD-000722",
            "High-Level Software Test Procedures Document", TestProcedureLevel.HighLevel,
            "Controlled high-level software test procedures document for this project.", "migration.test", now);
        await InsertProcedureDocumentAsync(connection, legacyCaseDocument);
        var legacyCaseSection = new TestProcedureDocumentNode(legacyCaseDocument.Id, null, 0,
            TestProcedureDocumentNodeType.Section, TestProcedureDocumentBootstrap.DefaultSectionHeading,
            null, "migration.test", now);
        await InsertProcedureDocumentNodeAsync(connection, legacyCaseSection);
        var customDescriptionDocument = new TestProcedureDocument(project.Id, "LLRTD-000722",
            "Low-Level Software Test Procedures Document", TestProcedureLevel.LowLevel,
            "Owner-authored wording must remain unchanged.", "migration.test", now);
        await InsertProcedureDocumentAsync(connection, customDescriptionDocument);
        var structuredSession = new ArtifactEditSession(project.Id, "TestProcedure", high.Id,
            highRevision.Id, oldManifestHash, "{\"artifactIdentity\":\"HLRTP-000123\"}", "migration.test", now);
        await InsertEditSessionAsync(connection, structuredSession, now);
        var signatureOnly = new ElectronicSignature(Guid.NewGuid(), "historic.reviewer", "Historic Reviewer",
            program.Id, "LegacyHistoricalEvidence", Guid.NewGuid(), "HLRTP-000321.00", "Approve",
            "Historical identity evidence", signatureOnlyHash, "127.0.0.1", now);
        await InsertSignatureAsync(connection, signatureOnly);
        var oldArtifact = new ControlledDocumentArtifact(oldDocument.Id, "docx", storedLegacy.StorageKey,
            storedLegacy.OriginalFileName, storedLegacy.ContentType, storedLegacy.Size, storedLegacy.Sha256, now);
        await InsertDocumentArtifactAsync(connection, oldArtifact);
        var notification = new UserNotification(project.Id, "verification.reviewer",
            "TestProcedureComment", "Discussion on HLRTP-000007", "Review HLRTP-000007 before release.",
            $"testProcedure:{high.Id}", high.Id, now);
        await InsertNotificationAsync(connection, notification);
        await InsertSourceChangeRequestAsync(connection, sourceChange);
        await InsertReviewAsync(connection, review);
        await InsertProcedureChangeAsync(connection, procedureChange);
        await InsertReviewCycleAsync(connection, reviewCycle);
        await InsertApprovalStepAsync(connection, step);
        await InsertSignatureAsync(connection, signature);
        var artifactSignature = new ElectronicSignature(Guid.NewGuid(), "verification.reviewer", "Verification Reviewer",
            program.Id, "ControlledDocumentArtifact", oldArtifact.Id, "HLRTD-000722.00/docx", "Approve",
            "Approved exact rendered output", oldArtifact.Sha256, "127.0.0.1", now);
        await InsertSignatureAsync(connection, artifactSignature);
        var documentSignature = new ElectronicSignature(Guid.NewGuid(), "verification.reviewer", "Verification Reviewer",
            program.Id, "ControlledDocument", oldDocument.Id, "HLRTD-000722.00", "Approve",
            "Approved controlled document basis", oldDocument.ContentHash, "127.0.0.1", now);
        await InsertSignatureAsync(connection, documentSignature);
        var comment = new ArtifactComment(project.Id, "TestProcedure", high.Id, highRevision.Id, null,
            "Legacy discussion remains attached to the software Case.", "[]", "verification.author", now);
        await InsertCommentAsync(connection, comment);
        await InsertBaselineSelectionAsync(connection, baseline.Id, high.Id, highRevision.Id);
        await InsertBaselineSelectionAsync(connection, baseline.Id, low.Id, lowRevision.Id);
        await InsertBaselineSelectionAsync(connection, baseline.Id, system.Id, systemRevision.Id);
        await InsertIdentifierSequenceAsync(connection, "HLRTP", 10);
        await InsertIdentifierSequenceAsync(connection, "LLRTP", 20);
        // The rename migration reads its watermarks only from frozen, materialized baselines: the fixture
        // freezes through raw SQL because the current-model ExecuteUpdate path is exactly what must not run
        // against the pinned schema.
        await ExecuteAsync(connection,
            "UPDATE \"candidate_baselines\" SET \"State\" = @state, \"FrozenAt\" = @frozenAt, " +
            "\"RequirementsMaterializedAt\" = @materializedAt, \"TestProceduresMaterializedAt\" = @materializedAt, " +
            "\"TestProceduresHash\" = @hash, \"UpdatedAt\" = @updatedAt WHERE \"Id\" = @id",
            P("state", CandidateBaselineState.Frozen.ToString()), P("frozenAt", now),
            P("materializedAt", now), P("hash", oldManifestHash), P("updatedAt", now), P("id", baseline.Id));

        return new PinnedSeed(now, program.Id, project.Id, release.Id, baseline.Id,
            high.Id, low.Id, system.Id, highRevision.Id, lowRevision.Id, systemRevision.Id,
            oldDocument.Id, legacyCaseDocument.Id, legacyCaseSection.Id, customDescriptionDocument.Id,
            structuredSession.Id, signatureOnly.Id, sourceChange.Id, review.Id, reviewCycle.Id,
            step.Id, step.Position, signature.Id, artifactSignature.Id, documentSignature.Id,
            comment.Id, notification.Id, oldArtifact.Id,
            reviewCycle.SnapshotHash, oldManifestHash, signatureOnlyHash, legacyBody,
            storedLegacy.StorageKey, storedLegacy.Sha256, storedLegacy.Size);
    }
}
