using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AeroLink.Domain.Baselines;
using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Common;
using AeroLink.Domain.Hierarchy;
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
/// Runs the behavior-changing #722 migration against a disposable PostgreSQL database when the qualification
/// connection is supplied. SQLite and EnsureCreated do not execute the migration's identity-site SQL, so the
/// exact pre-rename upgrade is deliberately kept as a PostgreSQL gate and never points at the protected demo DB.
/// </summary>
[CollectionDefinition("Issue722Postgres", DisableParallelization = true)]
public sealed class Issue722PostgresCollection : ICollectionFixture<object>;

[Collection("Issue722Postgres")]
public sealed class SoftwareCaseRenamePostgresQualificationTests
{
    private const string PreRenameMigration = "20260822153030_AddNeutralVerificationIdentity";
    private const string DatabaseName = "aerolink_722_qualify";

    [DisposablePostgresFact]
    public async Task Exact_pre_rename_upgrade_relabels_software_preserves_system_and_uses_equal_safe_watermarks()
    {
        var connection = QualificationConnectionOrSkip();
        var evidenceRoot = Path.Combine(Path.GetTempPath(), $"aerolink-722-authority-{Guid.NewGuid():N}");
        try
        {
            await using var db = await MigrateToPreRenameAsync(connection);
            var now = DateTimeOffset.UtcNow;
            var program = new ProgramRecord("#722 migration program", "C722");
            var project = new ProjectRecord(program.Id, "#722 migration project", "#722 software");
            var release = new SoftwareRelease(project.Id, "1.0", true);
            var baseline = new CandidateBaseline("BL-000722", 0, project.Id, release.Id, null, "#722 baseline", "migration.test", now);
            var high = new TestProcedure(project.Id, "HLRTP-000007", "Legacy high-level case", "migration.test", now,
                TestProcedureLevel.HighLevel);
            var low = new TestProcedure(project.Id, "LLRTP-000019", "Legacy low-level case", "migration.test", now,
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
            var oldDocument = new ControlledDocument(project.Id, release.Id, baseline.Id,
                ControlledDocumentType.HighLevelTestProcedures, "HLRTD-000722", "Legacy HLR Test Procedures", 0,
                new string('a', 64), 1, now);
            var legacyCaseDocument = new TestProcedureDocument(project.Id, "HLRTD-000722",
                "High-Level Software Test Procedures Document", TestProcedureLevel.HighLevel,
                "Controlled high-level software test procedures document for this project.", "migration.test", now);
            var legacyCaseSection = new TestProcedureDocumentNode(legacyCaseDocument.Id, null, 0,
                TestProcedureDocumentNodeType.Section, TestProcedureDocumentBootstrap.DefaultSectionHeading,
                null, "migration.test", now);
            var customDescriptionDocument = new TestProcedureDocument(project.Id, "LLRTD-000722",
                "Low-Level Software Test Procedures Document", TestProcedureLevel.LowLevel,
                "Owner-authored wording must remain unchanged.", "migration.test", now);
            const string oldManifestHash = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
            var structuredOnlyHighIdentifier = new ArtifactEditSession(project.Id, "TestProcedure", high.Id,
                highRevision.Id, oldManifestHash, "{\"artifactIdentity\":\"HLRTP-000123\"}", "migration.test", now);
            var files = new EvidenceFileStore(evidenceRoot);
            var legacyBytes = Encoding.UTF8.GetBytes("legacy frozen software document bytes");
            await using var legacySource = new MemoryStream(legacyBytes, writable: false);
            var storedLegacy = await files.StoreAsync(legacySource, "legacy-hlr.docx",
                "application/vnd.openxmlformats-officedocument.wordprocessingml.document", CancellationToken.None);
            var oldArtifact = new ControlledDocumentArtifact(oldDocument.Id, "docx", storedLegacy.StorageKey,
                storedLegacy.OriginalFileName, storedLegacy.ContentType, storedLegacy.Size, storedLegacy.Sha256, now);
            var unreadCaseNotification = new UserNotification(project.Id, "verification.reviewer",
                "TestProcedureComment", "Discussion on HLRTP-000007", "Review HLRTP-000007 before release.",
                $"testProcedure:{high.Id}", high.Id, now);
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
            review.AddProcedureChange("verification.author", new TestProcedureChangeDraft(
                "HLRTP-000007", 0, TestProcedureLevel.HighLevel, TestProcedureChangeKind.Modify,
                "Legacy high-level case", legacyBody, "legacy preconditions", "legacy steps", "legacy expected",
                "Identity-only migration"), now);
            var reviewCycle = review.SubmitForReview("verification.author",
                [new ApproverSelection("verification.reviewer", "Verification Reviewer")], true, now);
            var originalReviewHash = reviewCycle.SnapshotHash;
            var signature = new ElectronicSignature(Guid.NewGuid(), "verification.reviewer", "Verification Reviewer",
                program.Id, "TestChangeRequest", review.Id, review.DisplayNumber, "Approve", "Approved exact package",
                originalReviewHash, "127.0.0.1", now, reviewStepId: reviewCycle.Steps.Single().Id,
                reviewCycle: reviewCycle.Sequence, reviewStepPosition: reviewCycle.Steps.Single().Position,
                rationale: "Legacy approval evidence");
            var artifactSignature = new ElectronicSignature(Guid.NewGuid(), "verification.reviewer", "Verification Reviewer",
                program.Id, "ControlledDocumentArtifact", oldArtifact.Id, "HLRTD-000722.00/docx", "Approve",
                "Approved exact rendered output", oldArtifact.Sha256, "127.0.0.1", now);
            var documentSignature = new ElectronicSignature(Guid.NewGuid(), "verification.reviewer", "Verification Reviewer",
                program.Id, "ControlledDocument", oldDocument.Id, "HLRTD-000722.00", "Approve",
                "Approved controlled document basis", oldDocument.ContentHash, "127.0.0.1", now);
            var legacyCaseComment = new ArtifactComment(project.Id, "TestProcedure", high.Id, highRevision.Id, null,
                "Legacy discussion remains attached to the software Case.", "[]", "verification.author", now);

            db.AddRange(program, project, release, baseline, high, low, system, highRevision, lowRevision, systemRevision,
                oldDocument, oldArtifact, legacyCaseDocument, legacyCaseSection, customDescriptionDocument,
                structuredOnlyHighIdentifier, sourceChange, review, signature,
                artifactSignature, documentSignature, legacyCaseComment,
                unreadCaseNotification,
                new BaselineTestProcedureSelection(baseline.Id, high.Id, highRevision.Id),
                new BaselineTestProcedureSelection(baseline.Id, low.Id, lowRevision.Id),
                new BaselineTestProcedureSelection(baseline.Id, system.Id, systemRevision.Id),
                new IdentifierSequence("HLRTP", 10), new IdentifierSequence("LLRTP", 20));
            await db.SaveChangesAsync();
            var highRevisionId = highRevision.Id;

            await db.CandidateBaselines.Where(x => x.Id == baseline.Id).ExecuteUpdateAsync(update => update
                .SetProperty(x => x.State, CandidateBaselineState.Frozen)
                .SetProperty(x => x.FrozenAt, now)
                .SetProperty(x => x.RequirementsMaterializedAt, now)
                .SetProperty(x => x.TestProceduresMaterializedAt, now)
                .SetProperty(x => x.TestProceduresHash, oldManifestHash));
            db.ChangeTracker.Clear();

            await db.Database.GetService<IMigrator>().MigrateAsync();
            db.ChangeTracker.Clear();

            var artifacts = await db.TestProcedures.AsNoTracking().OrderBy(x => x.BaseNumber).ToListAsync();
            Assert.Equal(["HLRTC-000007", "LLRTC-000019", "SYSTP-000003"], artifacts.Select(x => x.BaseNumber));
            Assert.Equal([VerificationArtifactKind.Case, VerificationArtifactKind.Case, VerificationArtifactKind.Procedure],
                artifacts.Select(x => x.ArtifactKind));
            var body = await db.TestProcedureRevisions.AsNoTracking().SingleAsync(x => x.Id == highRevisionId);
            Assert.Equal(legacyBody, body.Objective);
            using var provenance = JsonDocument.Parse(body.SourceChangeRequestsJson);
            Assert.Equal("HLRTC-000007", provenance.RootElement.GetProperty("baseNumber").GetString());
            Assert.Equal("HLRTP-000007", provenance.RootElement.GetProperty("prose").GetString());

            var documents = await db.ControlledDocuments.AsNoTracking().ToListAsync();
            var migratedDocument = Assert.Single(documents);
            Assert.Equal(ControlledDocumentType.HighLevelTestCases, migratedDocument.Type);
            Assert.Equal("Legacy HLR Test Cases", migratedDocument.Title);
            Assert.Equal(TestProcedureDocumentBootstrap.DefaultCaseSectionHeading,
                (await db.TestProcedureDocumentNodes.AsNoTracking()
                    .SingleAsync(x => x.Id == legacyCaseSection.Id)).Heading);
            Assert.Equal("High-Level Software Test Cases Document",
                (await db.TestProcedureDocuments.AsNoTracking()
                    .SingleAsync(x => x.Id == legacyCaseDocument.Id)).Title);
            var migratedHighDocument = await db.TestProcedureDocuments.AsNoTracking()
                .SingleAsync(x => x.Id == legacyCaseDocument.Id);
            Assert.Equal("Controlled high-level software test cases document for this project.", migratedHighDocument.Description);
            var preservedCustomDocument = await db.TestProcedureDocuments.AsNoTracking()
                .SingleAsync(x => x.Id == customDescriptionDocument.Id);
            Assert.Equal("Low-Level Software Test Cases Document", preservedCustomDocument.Title);
            Assert.Equal("Owner-authored wording must remain unchanged.", preservedCustomDocument.Description);
            var migratedStructuredSession = await db.ArtifactEditSessions.AsNoTracking()
                .SingleAsync(x => x.Id == structuredOnlyHighIdentifier.Id);
            using (var structuredDraft = JsonDocument.Parse(migratedStructuredSession.DraftJson))
                Assert.Equal("HLRTC-000123", structuredDraft.RootElement.GetProperty("artifactIdentity").GetString());
            Assert.Equal("TestCase", (await db.ArtifactComments.AsNoTracking()
                .SingleAsync(x => x.Id == legacyCaseComment.Id)).ArtifactType);
            var migratedNotification = await db.UserNotifications.AsNoTracking()
                .SingleAsync(x => x.Id == unreadCaseNotification.Id);
            Assert.Equal("TestCaseComment", migratedNotification.Type);
            Assert.Equal("Discussion on HLRTC-000007", migratedNotification.Title);
            Assert.Equal("Review HLRTC-000007 before release.", migratedNotification.Detail);
            Assert.Equal($"case:{high.Id}", migratedNotification.Route);
            Assert.Contains(await db.SecurityAuditEvents.AsNoTracking()
                .Where(x => x.Target == "software-verification-identities").Select(x => x.EventType).ToListAsync(),
                eventType => eventType.EndsWith(".Pending", StringComparison.Ordinal));

            var watermarks = await db.IdentifierSequences.AsNoTracking()
                .Where(x => x.Scope == "HLRTC" || x.Scope == "HLRTP" || x.Scope == "LLRTC" || x.Scope == "LLRTP")
                .OrderBy(x => x.Scope).ToListAsync();
            Assert.Equal(["HLRTC", "HLRTP", "LLRTC", "LLRTP"], watermarks.Select(x => x.Scope));
            Assert.Equal([124L, 124L, 20L, 20L], watermarks.Select(x => x.NextValue));
            Assert.Equal(3, await db.TestProcedureRevisions.CountAsync());
            Assert.Equal(3, await db.BaselineTestProcedures.CountAsync(x => x.BaselineId == baseline.Id));

            var generator = new ControlledOutputGenerator(db, new RichContentPublisher(db, files));
            var authority = new SoftwareVerificationCaseMigrationAuthority(db, generator, files);
            await authority.EnsureCompletedAsync();

            var migratedBaseline = await db.CandidateBaselines.AsNoTracking().SingleAsync(x => x.Id == baseline.Id);
            var expectedManifestHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                $"HLRTC-000007.00:{highRevision.Id};LLRTC-000019.00:{lowRevision.Id};SYSTP-000003.00:{systemRevision.Id}"))).ToLowerInvariant();
            Assert.Equal(expectedManifestHash, migratedBaseline.TestProceduresHash);
            Assert.NotEqual(oldManifestHash, migratedBaseline.TestProceduresHash);
            var expectedDocumentHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                $"{migratedBaseline.TestProceduresHash}|{ControlledDocumentType.HighLevelTestCases}|1|aerolink-migration"))).ToLowerInvariant();
            migratedDocument = await db.ControlledDocuments.AsNoTracking().SingleAsync(x => x.Id == migratedDocument.Id);
            Assert.Equal(expectedDocumentHash, migratedDocument.ContentHash);

            var regeneratedArtifact = await db.ControlledDocumentArtifacts.AsNoTracking()
                .SingleAsync(x => x.DocumentId == migratedDocument.Id && x.Format == "docx");
            Assert.NotEqual(storedLegacy.StorageKey, regeneratedArtifact.StorageKey);
            Assert.NotEqual(storedLegacy.Sha256, regeneratedArtifact.Sha256);
            Assert.True(files.Exists(storedLegacy.StorageKey));
            await using (var original = await files.OpenVerifiedReadAsync(storedLegacy.StorageKey, storedLegacy.Size, storedLegacy.Sha256,
                CancellationToken.None))
            using (var originalCopy = new MemoryStream())
            {
                await original.CopyToAsync(originalCopy);
                Assert.Equal(legacyBytes, originalCopy.ToArray());
            }

            await using var generatedStream = await files.OpenVerifiedReadAsync(regeneratedArtifact.StorageKey,
                regeneratedArtifact.Size, regeneratedArtifact.Sha256, CancellationToken.None);
            using var generatedBytes = new MemoryStream();
            await generatedStream.CopyToAsync(generatedBytes);
            var generated = generatedBytes.ToArray();
            Assert.Equal(regeneratedArtifact.Sha256,
                Convert.ToHexString(SHA256.HashData(generated)).ToLowerInvariant());
            using (var archive = new ZipArchive(new MemoryStream(generated), ZipArchiveMode.Read))
            using (var reader = new StreamReader(archive.GetEntry("word/document.xml")!.Open()))
            {
                var documentXml = await reader.ReadToEndAsync();
                Assert.Contains(migratedBaseline.TestProceduresHash!, documentXml);
                Assert.Contains(migratedDocument.ContentHash, documentXml);
                Assert.Contains("HLRTC-000007", documentXml);
                Assert.DoesNotContain("HLRTP-000007", documentXml);
            }

            var migrationEvents = await db.SecurityAuditEvents.AsNoTracking()
                .Where(x => x.Target == "software-verification-identities")
                .ToListAsync();
            var allSecurityEvents = await db.SecurityAuditEvents.AsNoTracking().ToListAsync();
            var baselineEvents = await db.BaselineEvents.AsNoTracking().Where(x => x.BaselineId == baseline.Id).ToListAsync();
            Assert.Contains(baselineEvents, x => x.EventType == "VerificationIdentityManifestMigrated");
            Assert.Contains(allSecurityEvents, x => x.EventType == "VerificationIdentityMigration.DocumentRenditionRewritten"
                && x.Target.StartsWith("ControlledDocumentArtifact:", StringComparison.Ordinal));
            Assert.Contains(migrationEvents, x => x.EventType == "VerificationIdentityMigration.SoftwareCases.v1.Completed");

            // The human signature remains exactly what was recorded over the old snapshot. The latest
            // reconstructible cycle receives the new canonical hash, and append-only migration evidence names
            // that real replacement hash without pretending the reviewer signed it.
            var migratedCycle = await db.ReviewCycles.AsNoTracking().SingleAsync(x => x.Id == reviewCycle.Id);
            var preservedSignature = await db.ElectronicSignatures.AsNoTracking().SingleAsync(x => x.Id == signature.Id);
            Assert.NotEqual(originalReviewHash, migratedCycle.SnapshotHash);
            Assert.Equal(originalReviewHash, preservedSignature.ContentHash);
            var pendingSignatureEvidence = Assert.Single(allSecurityEvents, x =>
                x.EventType == "VerificationIdentityMigration.SignatureSuperseded"
                && x.Target == $"ElectronicSignature:{signature.Id}");
            var completedSignatureEvidence = Assert.Single(allSecurityEvents, x =>
                x.EventType == "VerificationIdentityMigration.SignatureSupersessionCompleted"
                && x.Target == pendingSignatureEvidence.Target);
            using (var completion = JsonDocument.Parse(completedSignatureEvidence.Detail))
            {
                Assert.Equal(originalReviewHash, completion.RootElement.GetProperty("oldSignatureHash").GetString());
                Assert.Equal(migratedCycle.SnapshotHash, completion.RootElement.GetProperty("newContentHash").GetString());
            }
            foreach (var (signed, expectedReplacementHash) in new[]
                     {
                         (artifactSignature, regeneratedArtifact.Sha256),
                         (documentSignature, migratedDocument.ContentHash)
                     })
            {
                var preserved = await db.ElectronicSignatures.AsNoTracking().SingleAsync(x => x.Id == signed.Id);
                Assert.Equal(signed.ContentHash, preserved.ContentHash);
                var pending = Assert.Single(allSecurityEvents, x =>
                    x.EventType == "VerificationIdentityMigration.SignatureSuperseded"
                    && x.Target == $"ElectronicSignature:{signed.Id}");
                var completed = Assert.Single(allSecurityEvents, x =>
                    x.EventType == "VerificationIdentityMigration.SignatureSupersessionCompleted"
                    && x.Target == pending.Target);
                using var detail = JsonDocument.Parse(completed.Detail);
                Assert.Equal(expectedReplacementHash, detail.RootElement.GetProperty("newContentHash").GetString());
            }

            // Completion is durable: the authority must not create a second rendition, hash, or audit record
            // on a restart after all output and signature work has completed.
            var completedCount = migrationEvents.Count(x => x.EventType == "VerificationIdentityMigration.SoftwareCases.v1.Completed");
            await authority.EnsureCompletedAsync();
            var secondDocument = await db.ControlledDocuments.AsNoTracking().SingleAsync(x => x.Id == migratedDocument.Id);
            var secondArtifact = await db.ControlledDocumentArtifacts.AsNoTracking()
                .SingleAsync(x => x.DocumentId == migratedDocument.Id && x.Format == "docx");
            Assert.Equal(migratedDocument.ContentHash, secondDocument.ContentHash);
            Assert.Equal(regeneratedArtifact.StorageKey, secondArtifact.StorageKey);
            Assert.Equal(regeneratedArtifact.Sha256, secondArtifact.Sha256);
            Assert.Equal(completedCount, await db.SecurityAuditEvents.CountAsync(x =>
                x.EventType == "VerificationIdentityMigration.SoftwareCases.v1.Completed"));

            // A second latest-migrate call is a no-op: no second relabelling, sequence bump, or audit event is
            // allowed merely because an installation restarts after the schema migration has been applied.
            var pendingCount = await db.SecurityAuditEvents.CountAsync(x => x.Target == "software-verification-identities");
            await db.Database.GetService<IMigrator>().MigrateAsync();
            Assert.Equal(pendingCount, await db.SecurityAuditEvents.CountAsync(x => x.Target == "software-verification-identities"));
            Assert.Equal([124L, 124L, 20L, 20L], await db.IdentifierSequences.AsNoTracking()
                .Where(x => x.Scope == "HLRTC" || x.Scope == "HLRTP" || x.Scope == "LLRTC" || x.Scope == "LLRTP")
                .OrderBy(x => x.Scope).Select(x => x.NextValue).ToListAsync());
        }
        finally
        {
            if (Directory.Exists(evidenceRoot)) Directory.Delete(evidenceRoot, recursive: true);
        }
    }

    [DisposablePostgresFact]
    public async Task Clean_install_latest_migration_allows_case_authority_to_complete_idempotently()
    {
        var connection = QualificationConnectionOrSkip();
        var evidenceRoot = Path.Combine(Path.GetTempPath(), $"aerolink-722-clean-authority-{Guid.NewGuid():N}");
        try
        {
            await using var db = new AeroLinkDbContext(Options(connection));
            await db.Database.EnsureDeletedAsync();
            await db.Database.GetService<IMigrator>().MigrateAsync();
            var files = new EvidenceFileStore(evidenceRoot);
            var authority = new SoftwareVerificationCaseMigrationAuthority(db,
                new ControlledOutputGenerator(db, new RichContentPublisher(db, files)), files);
            await authority.EnsureCompletedAsync();
            await authority.EnsureCompletedAsync();
            Assert.Equal(1, await db.SecurityAuditEvents.CountAsync(x =>
                x.EventType == "VerificationIdentityMigration.SoftwareCases.v1.Completed"));
        }
        finally
        {
            if (Directory.Exists(evidenceRoot)) Directory.Delete(evidenceRoot, recursive: true);
        }
    }

    private static async Task<AeroLinkDbContext> MigrateToPreRenameAsync(string connection)
    {
        var db = new AeroLinkDbContext(Options(connection));
        await db.Database.EnsureDeletedAsync();
        await db.Database.GetService<IMigrator>().MigrateAsync(PreRenameMigration);
        return db;
    }

    private static DbContextOptions<AeroLinkDbContext> Options(string connection) =>
        new DbContextOptionsBuilder<AeroLinkDbContext>().UseNpgsql(connection).Options;

    private static string QualificationConnectionOrSkip() => ValidateQualificationConnection(
        Environment.GetEnvironmentVariable("AEROLINK_MIGRATIONS_CONNECTION"));

    private static string ValidateQualificationConnection(string? connection)
    {
        if (string.IsNullOrWhiteSpace(connection))
            throw new InvalidOperationException("Issue #722 PostgreSQL qualification requires AEROLINK_MIGRATIONS_CONNECTION.");
        var builder = new NpgsqlConnectionStringBuilder(connection);
        var host = (builder.Host ?? string.Empty).Trim().Trim('[', ']');
        if (!string.Equals(host, "127.0.0.1", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Issue #722 PostgreSQL qualification requires a loopback host.");
        if (builder.Port == 54329)
            throw new InvalidOperationException("Issue #722 qualification refuses the protected PostgreSQL port 54329.");
        if (!string.Equals(builder.Database, DatabaseName, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Issue #722 PostgreSQL qualification requires the dedicated database {DatabaseName}.");
        return connection;
    }

    private sealed class DisposablePostgresFactAttribute : FactAttribute
    {
        public DisposablePostgresFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("AEROLINK_MIGRATIONS_CONNECTION")))
                Skip = "Issue #722 PostgreSQL qualification skipped: set AEROLINK_MIGRATIONS_CONNECTION to the dedicated disposable database.";
        }
    }
}
