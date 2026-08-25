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
    // The pinned pre-rename migration and the dedicated qualification database identity live with the
    // seeder and the connection contract, so the fixture and the safety refusal cannot drift apart.

    [DisposablePostgresFact]
    public async Task Exact_pre_rename_upgrade_relabels_software_preserves_system_and_uses_equal_safe_watermarks()
    {
        var connection = QualificationConnectionOrSkip();
        var evidenceRoot = Path.Combine(Path.GetTempPath(), $"aerolink-722-authority-{Guid.NewGuid():N}");
        try
        {
            // The historical database is created honestly: migrate exactly to the pinned pre-rename schema,
            // seed it through raw parameterized SQL that names only pinned-schema columns, and run the real
            // remaining migration chain. Today's EF model is first used only after that chain completes —
            // the product never occupies the pinned-schema-plus-current-model state, so the fixture must
            // not either.
            await Issue722PinnedSchemaSeeder.PreparePinnedDatabaseAsync(connection);
            var seed = await Issue722PinnedSchemaSeeder.SeedAsync(connection, evidenceRoot);
            var files = new EvidenceFileStore(evidenceRoot);
            await using var db = new AeroLinkDbContext(Options(connection));
            await db.Database.GetService<IMigrator>().MigrateAsync();

            await db.Database.GetService<IMigrator>().MigrateAsync();
            db.ChangeTracker.Clear();

            var artifacts = await db.TestProcedures.AsNoTracking().OrderBy(x => x.BaseNumber).ToListAsync();
            Assert.Equal(["HLRTC-000007", "LLRTC-000019", "SYSTP-000003"], artifacts.Select(x => x.BaseNumber));
            Assert.Equal([VerificationArtifactKind.Case, VerificationArtifactKind.Case, VerificationArtifactKind.Procedure],
                artifacts.Select(x => x.ArtifactKind));
            var body = await db.TestProcedureRevisions.AsNoTracking().SingleAsync(x => x.Id == seed.HighRevisionId);
            Assert.Equal(seed.LegacyBody, body.Objective);
            using var provenance = JsonDocument.Parse(body.SourceChangeRequestsJson);
            Assert.Equal("HLRTC-000007", provenance.RootElement.GetProperty("baseNumber").GetString());
            Assert.Equal("HLRTP-000007", provenance.RootElement.GetProperty("prose").GetString());

            var documents = await db.ControlledDocuments.AsNoTracking().ToListAsync();
            var migratedDocument = Assert.Single(documents);
            Assert.Equal(ControlledDocumentType.HighLevelTestCases, migratedDocument.Type);
            Assert.Equal("Legacy HLR Test Cases", migratedDocument.Title);
            Assert.Equal(TestProcedureDocumentBootstrap.DefaultCaseSectionHeading,
                (await db.TestProcedureDocumentNodes.AsNoTracking()
                    .SingleAsync(x => x.Id == seed.LegacyCaseSectionId)).Heading);
            Assert.Equal("High-Level Software Test Cases Document",
                (await db.TestProcedureDocuments.AsNoTracking()
                    .SingleAsync(x => x.Id == seed.LegacyCaseDocumentId)).Title);
            var migratedHighDocument = await db.TestProcedureDocuments.AsNoTracking()
                .SingleAsync(x => x.Id == seed.LegacyCaseDocumentId);
            Assert.Equal("Controlled high-level software test cases document for this project.", migratedHighDocument.Description);
            var preservedCustomDocument = await db.TestProcedureDocuments.AsNoTracking()
                .SingleAsync(x => x.Id == seed.CustomDescriptionDocumentId);
            Assert.Equal("Low-Level Software Test Cases Document", preservedCustomDocument.Title);
            Assert.Equal("Owner-authored wording must remain unchanged.", preservedCustomDocument.Description);
            var migratedStructuredSession = await db.ArtifactEditSessions.AsNoTracking()
                .SingleAsync(x => x.Id == seed.StructuredSessionId);
            using (var structuredDraft = JsonDocument.Parse(migratedStructuredSession.DraftJson))
                Assert.Equal("HLRTC-000123", structuredDraft.RootElement.GetProperty("artifactIdentity").GetString());
            var preservedSignatureOnly = await db.ElectronicSignatures.AsNoTracking()
                .SingleAsync(x => x.Id == seed.SignatureOnlySignatureId);
            Assert.Equal("HLRTP-000321.00", preservedSignatureOnly.ArtifactRevision);
            Assert.Equal(seed.SignatureOnlyHash, preservedSignatureOnly.ContentHash);
            Assert.Equal("TestCase", (await db.ArtifactComments.AsNoTracking()
                .SingleAsync(x => x.Id == seed.LegacyCommentId)).ArtifactType);
            var migratedNotification = await db.UserNotifications.AsNoTracking()
                .SingleAsync(x => x.Id == seed.NotificationId);
            Assert.Equal("TestCaseComment", migratedNotification.Type);
            Assert.Equal("Discussion on HLRTC-000007", migratedNotification.Title);
            Assert.Equal("Review HLRTC-000007 before release.", migratedNotification.Detail);
            Assert.Equal($"case:{seed.HighCaseId}", migratedNotification.Route);
            Assert.Contains(await db.SecurityAuditEvents.AsNoTracking()
                .Where(x => x.Target == "software-verification-identities").Select(x => x.EventType).ToListAsync(),
                eventType => eventType.EndsWith(".Pending", StringComparison.Ordinal));

            var watermarks = await db.IdentifierSequences.AsNoTracking()
                .Where(x => x.Scope == "HLRTC" || x.Scope == "HLRTP" || x.Scope == "LLRTC" || x.Scope == "LLRTP")
                .OrderBy(x => x.Scope).ToListAsync();
            Assert.Equal(["HLRTC", "HLRTP", "LLRTC", "LLRTP"], watermarks.Select(x => x.Scope));
            Assert.Equal([322L, 322L, 20L, 20L], watermarks.Select(x => x.NextValue));
            Assert.Equal(3, await db.TestProcedureRevisions.CountAsync());
            Assert.Equal(3, await db.BaselineTestProcedures.CountAsync(x => x.BaselineId == seed.BaselineId));

            var generator = new ControlledOutputGenerator(db, new RichContentPublisher(db, files));
            var authority = new SoftwareVerificationCaseMigrationAuthority(db, generator, files);
            await authority.EnsureCompletedAsync();

            var migratedBaseline = await db.CandidateBaselines.AsNoTracking().SingleAsync(x => x.Id == seed.BaselineId);
            var expectedManifestHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                $"HLRTC-000007.00:{seed.HighRevisionId};LLRTC-000019.00:{seed.LowRevisionId};SYSTP-000003.00:{seed.SystemRevisionId}"))).ToLowerInvariant();
            Assert.Equal(expectedManifestHash, migratedBaseline.TestProceduresHash);
            Assert.NotEqual(seed.OldManifestHash, migratedBaseline.TestProceduresHash);
            var expectedDocumentHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                $"{migratedBaseline.TestProceduresHash}|{ControlledDocumentType.HighLevelTestCases}|1|aerolink-migration"))).ToLowerInvariant();
            migratedDocument = await db.ControlledDocuments.AsNoTracking().SingleAsync(x => x.Id == migratedDocument.Id);
            Assert.Equal(expectedDocumentHash, migratedDocument.ContentHash);

            var regeneratedArtifact = await db.ControlledDocumentArtifacts.AsNoTracking()
                .SingleAsync(x => x.DocumentId == migratedDocument.Id && x.Format == "docx");
            Assert.NotEqual(seed.StoredLegacyStorageKey, regeneratedArtifact.StorageKey);
            Assert.NotEqual(seed.StoredLegacySha256, regeneratedArtifact.Sha256);
            Assert.True(files.Exists(seed.StoredLegacyStorageKey));
            await using (var original = await files.OpenVerifiedReadAsync(seed.StoredLegacyStorageKey, seed.StoredLegacySize, seed.StoredLegacySha256,
                CancellationToken.None))
            using (var originalCopy = new MemoryStream())
            {
                await original.CopyToAsync(originalCopy);
                Assert.Equal(Encoding.UTF8.GetBytes("legacy frozen software document bytes"), originalCopy.ToArray());
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
            var baselineEvents = await db.BaselineEvents.AsNoTracking().Where(x => x.BaselineId == seed.BaselineId).ToListAsync();
            Assert.Contains(baselineEvents, x => x.EventType == "VerificationIdentityManifestMigrated");
            Assert.Contains(allSecurityEvents, x => x.EventType == "VerificationIdentityMigration.DocumentRenditionRewritten"
                && x.Target.StartsWith("ControlledDocumentArtifact:", StringComparison.Ordinal));
            Assert.Contains(migrationEvents, x => x.EventType == "VerificationIdentityMigration.SoftwareCases.v1.Completed");
            Assert.DoesNotContain(allSecurityEvents, x => x.Target == $"ElectronicSignature:{seed.SignatureOnlySignatureId}"
                && x.EventType == "VerificationIdentityMigration.SignatureSuperseded");

            // The human signature remains exactly what was recorded over the old snapshot. The latest
            // reconstructible cycle receives the new canonical hash, and append-only migration evidence names
            // that real replacement hash without pretending the reviewer signed it.
            var migratedCycle = await db.ReviewCycles.AsNoTracking().SingleAsync(x => x.Id == seed.ReviewCycleId);
            var preservedSignature = await db.ElectronicSignatures.AsNoTracking().SingleAsync(x => x.Id == seed.SignatureId);
            Assert.NotEqual(seed.OriginalReviewHash, migratedCycle.SnapshotHash);
            Assert.Equal(seed.OriginalReviewHash, preservedSignature.ContentHash);
            var pendingSignatureEvidence = Assert.Single(allSecurityEvents, x =>
                x.EventType == "VerificationIdentityMigration.SignatureSuperseded"
                && x.Target == $"ElectronicSignature:{seed.SignatureId}");
            var completedSignatureEvidence = Assert.Single(allSecurityEvents, x =>
                x.EventType == "VerificationIdentityMigration.SignatureSupersessionCompleted"
                && x.Target == pendingSignatureEvidence.Target);
            using (var completion = JsonDocument.Parse(completedSignatureEvidence.Detail))
            {
                Assert.Equal(seed.OriginalReviewHash, completion.RootElement.GetProperty("oldSignatureHash").GetString());
                Assert.Equal(migratedCycle.SnapshotHash, completion.RootElement.GetProperty("newContentHash").GetString());
            }
            foreach (var (signed, originalContentHash, expectedReplacementHash) in new[]
                     {
                         (seed.ArtifactSignatureId, seed.StoredLegacySha256, regeneratedArtifact.Sha256),
                         (seed.DocumentSignatureId, new string('a', 64), migratedDocument.ContentHash)
                     })
            {
                var preserved = await db.ElectronicSignatures.AsNoTracking().SingleAsync(x => x.Id == signed);
                Assert.Equal(originalContentHash, preserved.ContentHash);
                var pending = Assert.Single(allSecurityEvents, x =>
                    x.EventType == "VerificationIdentityMigration.SignatureSuperseded"
                    && x.Target == $"ElectronicSignature:{signed}");
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
            Assert.Equal([322L, 322L, 20L, 20L], await db.IdentifierSequences.AsNoTracking()
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

    private static DbContextOptions<AeroLinkDbContext> Options(string connection) =>
        new DbContextOptionsBuilder<AeroLinkDbContext>().UseNpgsql(connection).Options;

    private static string QualificationConnectionOrSkip() => Issue722QualificationConnection.Validate(
        Environment.GetEnvironmentVariable("AEROLINK_MIGRATIONS_CONNECTION"));

    private sealed class DisposablePostgresFactAttribute : FactAttribute
    {
        public DisposablePostgresFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("AEROLINK_MIGRATIONS_CONNECTION")))
                Skip = "Issue #722 PostgreSQL qualification skipped: set AEROLINK_MIGRATIONS_CONNECTION to the dedicated disposable database.";
        }
    }
}
