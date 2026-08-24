using System.IO.Compression;
using System.Text;
using System.Text.Json;
using AeroLink.Domain.Baselines;
using AeroLink.Domain.Identity;
using AeroLink.Domain.Programs;
using AeroLink.Domain.Traceability;
using AeroLink.Domain.Verification;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace AeroLink.Infrastructure.Tests;

[CollectionDefinition("Issue747Postgres", DisableParallelization = true)]
public sealed class Issue747PostgresCollection : ICollectionFixture<object>;

[Collection("Issue747Postgres")]
public sealed class SoftwareCaseLegacyDocumentMigrationPostgresQualificationTests
{
    private const string DatabaseName = "aerolink_747_qualify";

    [Issue747PostgresFact]
    public async Task Legacy_case_document_without_exact_manifest_uses_generation_time_compatibility_basis_without_fabricating_baseline_manifest()
    {
        var connection = QualificationConnectionOrSkip();
        var evidenceRoot = Path.Combine(Path.GetTempPath(), $"aerolink-747-authority-{Guid.NewGuid():N}");
        try
        {
            await using var db = new AeroLinkDbContext(Options(connection));
            await db.Database.EnsureDeletedAsync();
            await db.Database.MigrateAsync();

            var files = new EvidenceFileStore(evidenceRoot);
            var seeded = await SeedLegacyCaseDocumentAsync(db, files, artifactCount: 1);
            var originalDocumentHash = seeded.Document.ContentHash;
            var originalGeneratedAt = await db.ControlledDocuments.AsNoTracking()
                .Where(x => x.Id == seeded.Document.Id).Select(x => x.GeneratedAt).SingleAsync();
            var originalStorageKey = seeded.Artifact.StorageKey;
            var originalArtifactHash = seeded.Artifact.Sha256;

            var authority = new SoftwareVerificationCaseMigrationAuthority(db,
                new ControlledOutputGenerator(db, new RichContentPublisher(db, files)), files);
            await authority.EnsureCompletedAsync();
            db.ChangeTracker.Clear();

            var baseline = await db.CandidateBaselines.AsNoTracking()
                .SingleAsync(x => x.Id == seeded.Baseline.Id);
            Assert.Null(baseline.TestProceduresMaterializedAt);
            Assert.Null(baseline.TestProceduresHash);

            var document = await db.ControlledDocuments.AsNoTracking()
                .SingleAsync(x => x.Id == seeded.Document.Id);
            var artifact = await db.ControlledDocumentArtifacts.AsNoTracking()
                .SingleAsync(x => x.Id == seeded.Artifact.Id);
            Assert.NotEqual(originalDocumentHash, document.ContentHash);
            Assert.Equal(originalGeneratedAt, document.GeneratedAt);
            Assert.NotEqual(originalStorageKey, artifact.StorageKey);
            Assert.NotEqual(originalArtifactHash, artifact.Sha256);
            Assert.True(files.Exists(originalStorageKey));

            // Revision .01 was approved after the historical document existed. Preserving GeneratedAt is what
            // keeps the migration on the original .00 compatibility snapshot rather than silently publishing
            // later work into an older controlled record.
            var snapshot = await ControlledProcedureDocumentSnapshotProjection.ForDocumentAsync(db,
                seeded.Baseline.Id, TestProcedureLevel.HighLevel, document.GeneratedAt, CancellationToken.None);
            Assert.False(snapshot.IsExactManifest);
            var migratedRow = Assert.Single(snapshot.Rows);
            Assert.Equal(0, migratedRow.Revision);
            Assert.Equal("HLRTC-000747", migratedRow.BaseNumber);

            var events = await db.SecurityAuditEvents.AsNoTracking().ToListAsync();
            var basis = Assert.Single(events, x =>
                x.EventType == "VerificationIdentityMigration.LegacyDocumentBasisReconstructed"
                && x.Target == $"ControlledDocument:{seeded.Document.Id}");
            using (var detail = JsonDocument.Parse(basis.Detail))
            {
                Assert.Equal(seeded.Document.Id,
                    detail.RootElement.GetProperty("documentId").GetGuid());
                Assert.Equal(seeded.Baseline.Id,
                    detail.RootElement.GetProperty("baselineId").GetGuid());
                Assert.Equal(1, detail.RootElement.GetProperty("artifactCount").GetInt32());
                Assert.True(detail.RootElement.GetProperty("baselineManifestStatePreserved").GetBoolean());
                Assert.True(detail.RootElement.GetProperty("documentGeneratedAtPreserved").GetBoolean());
                Assert.Equal(64, detail.RootElement.GetProperty("compatibilitySnapshotHash").GetString()!.Length);
            }
            Assert.Single(events, x =>
                x.EventType == "VerificationIdentityMigration.SoftwareCases.v1.Completed");

            await using (var generated = await files.OpenVerifiedReadAsync(
                             artifact.StorageKey, artifact.Size, artifact.Sha256, CancellationToken.None))
            using (var copy = new MemoryStream())
            {
                await generated.CopyToAsync(copy);
                using var archive = new ZipArchive(new MemoryStream(copy.ToArray()), ZipArchiveMode.Read);
                using var reader = new StreamReader(archive.GetEntry("word/document.xml")!.Open());
                var xml = await reader.ReadToEndAsync();
                Assert.Contains("HLRTC-000747", xml);
                Assert.DoesNotContain("HLRTC-000747.01", xml);
            }

            var eventCount = events.Count;
            await authority.EnsureCompletedAsync();
            Assert.Equal(eventCount, await db.SecurityAuditEvents.CountAsync());
            var secondArtifact = await db.ControlledDocumentArtifacts.AsNoTracking()
                .SingleAsync(x => x.Id == seeded.Artifact.Id);
            Assert.Equal(artifact.StorageKey, secondArtifact.StorageKey);
            Assert.Equal(artifact.Sha256, secondArtifact.Sha256);
        }
        finally
        {
            if (Directory.Exists(evidenceRoot)) Directory.Delete(evidenceRoot, recursive: true);
        }
    }

    [Issue747PostgresFact]
    public async Task Document_generated_before_later_baseline_materialization_still_uses_legacy_generation_time_basis()
    {
        var connection = QualificationConnectionOrSkip();
        var evidenceRoot = Path.Combine(Path.GetTempPath(), $"aerolink-747-temporal-{Guid.NewGuid():N}");
        try
        {
            await using var db = new AeroLinkDbContext(Options(connection));
            await db.Database.EnsureDeletedAsync();
            await db.Database.MigrateAsync();

            var files = new EvidenceFileStore(evidenceRoot);
            var seeded = await SeedLegacyCaseDocumentAsync(db, files, artifactCount: 1);
            var originalGeneratedAt = await db.ControlledDocuments.AsNoTracking()
                .Where(x => x.Id == seeded.Document.Id).Select(x => x.GeneratedAt).SingleAsync();
            var materializedAt = originalGeneratedAt.AddMinutes(1);
            var currentCase = await db.TestProcedures.SingleAsync(x => x.BaseNumber == "HLRTC-000747");
            var laterRevision = await db.TestProcedureRevisions.SingleAsync(x =>
                x.ProcedureId == currentCase.Id && x.Revision == 1);
            db.BaselineTestProcedures.Add(new BaselineTestProcedureSelection(
                seeded.Baseline.Id, currentCase.Id, laterRevision.Id));
            await db.SaveChangesAsync();
            await db.CandidateBaselines.Where(x => x.Id == seeded.Baseline.Id)
                .ExecuteUpdateAsync(update => update
                    .SetProperty(x => x.TestProceduresMaterializedAt, materializedAt)
                    .SetProperty(x => x.TestProceduresHash, new string('b', 64)));
            db.ChangeTracker.Clear();

            var authority = new SoftwareVerificationCaseMigrationAuthority(db,
                new ControlledOutputGenerator(db, new RichContentPublisher(db, files)), files);
            await authority.EnsureCompletedAsync();
            db.ChangeTracker.Clear();

            var baseline = await db.CandidateBaselines.AsNoTracking()
                .SingleAsync(x => x.Id == seeded.Baseline.Id);
            Assert.Equal(materializedAt, baseline.TestProceduresMaterializedAt);
            Assert.NotEqual(new string('b', 64), baseline.TestProceduresHash);

            var basis = await db.SecurityAuditEvents.AsNoTracking().SingleAsync(x =>
                x.EventType == "VerificationIdentityMigration.LegacyDocumentBasisReconstructed"
                && x.Target == $"ControlledDocument:{seeded.Document.Id}");
            using (var detail = JsonDocument.Parse(basis.Detail))
            {
                Assert.True(detail.RootElement.GetProperty("baselineManifestStatePreserved").GetBoolean());
                Assert.False(detail.RootElement.GetProperty("baselineWasMaterializedWhenDocumentGenerated").GetBoolean());
                Assert.Equal(materializedAt,
                    detail.RootElement.GetProperty("baselineMaterializedAt").GetDateTimeOffset());
            }

            var document = await db.ControlledDocuments.AsNoTracking()
                .SingleAsync(x => x.Id == seeded.Document.Id);
            Assert.Equal(originalGeneratedAt, document.GeneratedAt);
            var currentSelection = await db.BaselineTestProcedures.AsNoTracking()
                .SingleAsync(x => x.BaselineId == seeded.Baseline.Id);
            Assert.Equal(laterRevision.Id, currentSelection.RevisionId);

            var migratedArtifact = await db.ControlledDocumentArtifacts.AsNoTracking()
                .SingleAsync(x => x.Id == seeded.Artifact.Id);
            await using var generated = await files.OpenVerifiedReadAsync(
                migratedArtifact.StorageKey, migratedArtifact.Size, migratedArtifact.Sha256,
                CancellationToken.None);
            using var copy = new MemoryStream();
            await generated.CopyToAsync(copy);
            using var archive = new ZipArchive(new MemoryStream(copy.ToArray()), ZipArchiveMode.Read);
            using var reader = new StreamReader(archive.GetEntry("word/document.xml")!.Open());
            var xml = await reader.ReadToEndAsync();
            Assert.Contains("HLRTC-000747", xml);
            Assert.DoesNotContain("HLRTC-000747.01", xml);
        }
        finally
        {
            if (Directory.Exists(evidenceRoot)) Directory.Delete(evidenceRoot, recursive: true);
        }
    }

    [Issue747PostgresFact]
    public async Task Legacy_document_without_stored_artifact_supersedes_document_signature_from_content_basis()
    {
        var connection = QualificationConnectionOrSkip();
        var evidenceRoot = Path.Combine(Path.GetTempPath(), $"aerolink-747-no-artifact-{Guid.NewGuid():N}");
        try
        {
            await using var db = new AeroLinkDbContext(Options(connection));
            await db.Database.EnsureDeletedAsync();
            await db.Database.MigrateAsync();

            var files = new EvidenceFileStore(evidenceRoot);
            var now = DateTimeOffset.UtcNow;
            var program = new ProgramRecord("#747 no-artifact program", "C747N");
            var project = new ProjectRecord(program.Id, "#747 no-artifact project", "#747 software");
            var release = new SoftwareRelease(project.Id, "1.0", true);
            var baseline = new CandidateBaseline("BL-000748", 0, project.Id, release.Id, null,
                "#747 legacy baseline", "migration.test", now.AddMinutes(-20));
            var @case = new TestProcedure(project.Id, "HLRTC-000748", "Legacy high-level case",
                "migration.test", now.AddMinutes(-15), TestProcedureLevel.HighLevel);
            var revision = new TestProcedureRevision(@case.Id, 0,
                "Verify the legacy software behavior", "Legacy logical preconditions",
                "Exercise the legacy software behavior", "The behavior is observed",
                TestProcedureState.Approved, "migration.test", now.AddMinutes(-14));
            var document = new ControlledDocument(project.Id, release.Id, baseline.Id,
                ControlledDocumentType.HighLevelTestCases, "HLRTD-000748", "Legacy HLR Test Cases", 0,
                new string('c', 64), 1, now.AddMinutes(-10));
            var signature = new ElectronicSignature(Guid.NewGuid(), "migration.owner", "Migration Owner", program.Id,
                "ControlledDocument", document.Id, "HLRTD-000748.00", "Approve", "Controlled publication",
                document.ContentHash, "127.0.0.1", now.AddMinutes(-9));
            var pending = new SecurityAuditEvent(
                "VerificationIdentityMigration.SignatureSuperseded", "aerolink-migration",
                $"ElectronicSignature:{signature.Id}", "Superseded",
                JsonSerializer.Serialize(new
                {
                    migration = SoftwareVerificationCaseMigrationAuthority.MigrationMarker,
                    oldArtifactIdentity = signature.ArtifactRevision,
                    oldSignatureId = signature.Id,
                    oldSignatureHash = signature.ContentHash,
                    newArtifactIdentity = signature.ArtifactRevision,
                    newContentHash = (string?)null
                }), "", now.AddMinutes(-8));

            db.AddRange(program, project, release, baseline, @case, revision, document, signature, pending);
            await db.SaveChangesAsync();
            var originalDocumentHash = document.ContentHash;
            var originalGeneratedAt = await db.ControlledDocuments.AsNoTracking()
                .Where(x => x.Id == document.Id).Select(x => x.GeneratedAt).SingleAsync();
            var originalSignatureHash = signature.ContentHash;

            var authority = new SoftwareVerificationCaseMigrationAuthority(db,
                new ControlledOutputGenerator(db, new RichContentPublisher(db, files)), files);
            await authority.EnsureCompletedAsync();
            db.ChangeTracker.Clear();

            var migratedDocument = await db.ControlledDocuments.AsNoTracking()
                .SingleAsync(x => x.Id == document.Id);
            var migratedSignature = await db.ElectronicSignatures.AsNoTracking()
                .SingleAsync(x => x.Id == signature.Id);
            Assert.NotEqual(originalDocumentHash, migratedDocument.ContentHash);
            Assert.Equal(originalGeneratedAt, migratedDocument.GeneratedAt);
            Assert.Equal(originalSignatureHash, migratedSignature.ContentHash);
            Assert.Equal(0, await db.ControlledDocumentArtifacts.CountAsync(x => x.DocumentId == document.Id));

            var completed = await db.SecurityAuditEvents.AsNoTracking().SingleAsync(x =>
                x.EventType == "VerificationIdentityMigration.SignatureSupersessionCompleted"
                && x.Target == $"ElectronicSignature:{signature.Id}");
            using var completedDetail = JsonDocument.Parse(completed.Detail);
            Assert.Equal(migratedDocument.ContentHash,
                completedDetail.RootElement.GetProperty("newContentHash").GetString());
            Assert.Contains("without a stored rendition",
                completedDetail.RootElement.GetProperty("reason").GetString());
            Assert.Contains("only its exact on-demand content basis",
                (await db.SecurityAuditEvents.AsNoTracking().SingleAsync(x =>
                    x.EventType == "VerificationIdentityMigration.DocumentContentBasisRewritten"
                    && x.Target == $"ControlledDocument:{document.Id}")).Detail);
        }
        finally
        {
            if (Directory.Exists(evidenceRoot)) Directory.Delete(evidenceRoot, recursive: true);
        }
    }

    [Issue747PostgresFact]
    public async Task Legacy_case_document_snapshot_count_mismatch_still_fails_closed_and_names_document_and_baseline()
    {
        var connection = QualificationConnectionOrSkip();
        var evidenceRoot = Path.Combine(Path.GetTempPath(), $"aerolink-747-failclosed-{Guid.NewGuid():N}");
        try
        {
            await using var db = new AeroLinkDbContext(Options(connection));
            await db.Database.EnsureDeletedAsync();
            await db.Database.MigrateAsync();

            var files = new EvidenceFileStore(evidenceRoot);
            var seeded = await SeedLegacyCaseDocumentAsync(db, files, artifactCount: 2);
            var originalDocumentHash = seeded.Document.ContentHash;
            var originalStorageKey = seeded.Artifact.StorageKey;
            var originalArtifactHash = seeded.Artifact.Sha256;
            var authority = new SoftwareVerificationCaseMigrationAuthority(db,
                new ControlledOutputGenerator(db, new RichContentPublisher(db, files)), files);

            var failure = await Assert.ThrowsAsync<InvalidOperationException>(
                () => authority.EnsureCompletedAsync());
            var detail = failure.ToString();
            Assert.Contains(seeded.Document.Id.ToString(), detail, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(seeded.Baseline.Id.ToString(), detail, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("compatibility snapshot contains 1 records", detail, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("controlled document records 2", detail, StringComparison.OrdinalIgnoreCase);
            db.ChangeTracker.Clear();
            var document = await db.ControlledDocuments.AsNoTracking()
                .SingleAsync(x => x.Id == seeded.Document.Id);
            var artifact = await db.ControlledDocumentArtifacts.AsNoTracking()
                .SingleAsync(x => x.Id == seeded.Artifact.Id);
            Assert.Equal(originalDocumentHash, document.ContentHash);
            Assert.Equal(originalStorageKey, artifact.StorageKey);
            Assert.Equal(originalArtifactHash, artifact.Sha256);
            Assert.Equal(0, await db.SecurityAuditEvents.CountAsync(x =>
                x.EventType == "VerificationIdentityMigration.SoftwareCases.v1.Completed"));
            Assert.Equal(0, await db.SecurityAuditEvents.CountAsync(x =>
                x.EventType == "VerificationIdentityMigration.DocumentContentBasisRewritten"));
        }
        finally
        {
            if (Directory.Exists(evidenceRoot)) Directory.Delete(evidenceRoot, recursive: true);
        }
    }

    private static async Task<SeededLegacyDocument> SeedLegacyCaseDocumentAsync(
        AeroLinkDbContext db,
        EvidenceFileStore files,
        int artifactCount)
    {
        var now = DateTimeOffset.UtcNow;
        var program = new ProgramRecord("#747 migration program", "C747");
        var project = new ProjectRecord(program.Id, "#747 migration project", "#747 software");
        var release = new SoftwareRelease(project.Id, "1.0", true);
        var baseline = new CandidateBaseline("BL-000747", 0, project.Id, release.Id, null,
            "#747 legacy baseline", "migration.test", now.AddMinutes(-20));
        var @case = new TestProcedure(project.Id, "HLRTC-000747", "Legacy high-level case",
            "migration.test", now.AddMinutes(-15), TestProcedureLevel.HighLevel);
        var revision = new TestProcedureRevision(@case.Id, 0,
            "Verify the legacy software behavior", "Legacy logical preconditions",
            "Exercise the legacy software behavior", "The behavior is observed",
            TestProcedureState.Approved, "migration.test", now.AddMinutes(-14));
        var document = new ControlledDocument(project.Id, release.Id, baseline.Id,
            ControlledDocumentType.HighLevelTestCases, "HLRTD-000747", "Legacy HLR Test Cases", 0,
            new string('a', 64), artifactCount, now.AddMinutes(-10));
        var laterRevision = new TestProcedureRevision(@case.Id, 1,
            "Later objective that must not leak into the old document", "Later logical preconditions",
            "Exercise the later behavior", "The later behavior is observed",
            TestProcedureState.Approved, "later.author", now.AddMinutes(-5));

        var legacyBytes = Encoding.UTF8.GetBytes("legacy #747 controlled bytes");
        await using var source = new MemoryStream(legacyBytes, writable: false);
        var stored = await files.StoreAsync(source, "legacy-747.docx",
            "application/vnd.openxmlformats-officedocument.wordprocessingml.document", CancellationToken.None);
        var artifact = new ControlledDocumentArtifact(document.Id, "docx", stored.StorageKey,
            stored.OriginalFileName, stored.ContentType, stored.Size, stored.Sha256, now.AddMinutes(-10));

        db.AddRange(program, project, release, baseline, @case, revision, laterRevision, document, artifact);
        await db.SaveChangesAsync();
        return new SeededLegacyDocument(baseline, document, artifact);
    }

    private static DbContextOptions<AeroLinkDbContext> Options(string connection) =>
        new DbContextOptionsBuilder<AeroLinkDbContext>().UseNpgsql(connection).Options;

    private static string QualificationConnectionOrSkip() => ValidateQualificationConnection(
        ResolveQualificationConnection());

    /// <summary>
    /// The connection this qualification runs against, or null when no PostgreSQL server was offered.
    ///
    /// Every other PostgreSQL qualification fixture in this repository is driven by
    /// AEROLINK_MIGRATIONS_CONNECTION. Honouring only a bespoke AEROLINK_747_CONNECTION meant a
    /// maintainer who set the conventional variable and ran the suite silently skipped these four
    /// tests and still saw a green run — precisely the failure this fixture exists to prevent.
    /// The shared variable names a <em>server</em>, not this fixture's database, so its database is
    /// replaced with the dedicated disposable one rather than trusted: the isolation guarantee stays
    /// with the fixture instead of depending on whatever the caller happened to point at. The
    /// dedicated variable still wins when both are set, so an explicit #747 target is never silently
    /// redirected somewhere else.
    /// </summary>
    internal static string? ResolveQualificationConnection()
    {
        var dedicated = Environment.GetEnvironmentVariable("AEROLINK_747_CONNECTION");
        if (!string.IsNullOrWhiteSpace(dedicated)) return dedicated;
        var shared = Environment.GetEnvironmentVariable("AEROLINK_MIGRATIONS_CONNECTION");
        if (string.IsNullOrWhiteSpace(shared)) return null;
        return new NpgsqlConnectionStringBuilder(shared) { Database = DatabaseName }.ConnectionString;
    }

    private static string ValidateQualificationConnection(string? connection)
    {
        if (string.IsNullOrWhiteSpace(connection))
            throw new InvalidOperationException(
                "Issue #747 PostgreSQL qualification requires AEROLINK_747_CONNECTION or AEROLINK_MIGRATIONS_CONNECTION.");
        var builder = new NpgsqlConnectionStringBuilder(connection);
        var host = (builder.Host ?? string.Empty).Trim().Trim('[', ']');
        if (!string.Equals(host, "127.0.0.1", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Issue #747 PostgreSQL qualification requires a loopback host.");
        if (builder.Port == 54329)
            throw new InvalidOperationException("Issue #747 qualification refuses the protected PostgreSQL port 54329.");
        if (!string.Equals(builder.Database, DatabaseName, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"Issue #747 PostgreSQL qualification requires the dedicated database {DatabaseName}.");
        return connection;
    }

    private sealed record SeededLegacyDocument(
        CandidateBaseline Baseline,
        ControlledDocument Document,
        ControlledDocumentArtifact Artifact);

    private sealed class Issue747PostgresFactAttribute : FactAttribute
    {
        public Issue747PostgresFactAttribute()
        {
            // Skip only when no PostgreSQL server was offered at all. A conventional suite run that
            // sets AEROLINK_MIGRATIONS_CONNECTION now executes these tests instead of skipping them.
            if (string.IsNullOrWhiteSpace(ResolveQualificationConnection()))
                Skip = "Issue #747 PostgreSQL qualification skipped: set AEROLINK_747_CONNECTION or AEROLINK_MIGRATIONS_CONNECTION.";
        }
    }
}
