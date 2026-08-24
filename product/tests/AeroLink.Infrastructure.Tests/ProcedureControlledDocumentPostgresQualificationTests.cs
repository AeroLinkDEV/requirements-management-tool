using System.Net;
using System.Text;
using AeroLink.Domain.Baselines;
using AeroLink.Domain.Identity;
using AeroLink.Domain.Programs;
using AeroLink.Domain.Traceability;
using AeroLink.Domain.Verification;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace AeroLink.Infrastructure.Tests;

[CollectionDefinition("Issue728Postgres", DisableParallelization = true)]
public sealed class Issue728PostgresCollection : ICollectionFixture<object>;

/// <summary>Exact predecessor and clean-install PostgreSQL qualification for the typed document register.</summary>
[Collection("Issue728Postgres")]
public sealed class ProcedureControlledDocumentPostgresQualificationTests
{
    private const string Predecessor = "20260823220128_AddProcedureTestChangeControlPackage";
    private const string Migration = "20260824025544_AddProcedureControlledDocuments";
    private const string DatabaseName = "aerolink_728_qualify";
    private const int Port = 55474;

    [DisposablePostgresFact]
    public async Task Exact_upgrade_preserves_register_identity_and_backfills_historical_kind()
    {
        var connection = QualificationConnectionOrThrow();
        var evidenceRoot = Path.Combine(Path.GetTempPath(), $"aerolink-728-preservation-{Guid.NewGuid():N}");
        try
        {
        await using var db = new AeroLinkDbContext(Options(connection));
        await db.Database.EnsureDeletedAsync();
        await db.Database.GetService<IMigrator>().MigrateAsync(Predecessor);

        var programId = Guid.NewGuid();
        var projectId = Guid.NewGuid();
        var systemId = Guid.NewGuid();
        var highId = Guid.NewGuid();
        var lowId = Guid.NewGuid();
        const string now = "2026-08-24 03:10:00+00";
        await Sql(db, $"INSERT INTO programs (\"Id\",\"Name\",\"Code\") VALUES ('{programId}','Issue 728','Q728');");
        await Sql(db, $"INSERT INTO projects (\"Id\",\"ProgramId\",\"Name\",\"SoftwareProduct\") VALUES ('{projectId}','{programId}','Issue 728 project','Qualification software');");
        await RegisterSql(db, systemId, projectId, "SYSTD-000728", "System Test Procedures", "System", now);
        await RegisterSql(db, highId, projectId, "HLRTD-000728", "HLR Test Cases", "HighLevel", now);
        await RegisterSql(db, lowId, projectId, "LLRTD-000728", "LLR Test Cases", "LowLevel", now);

        // #728 types the existing registers but deliberately does not regenerate unchanged Case/System
        // publications. Their exact rows, bytes, hashes and human signatures are evidence and must remain
        // byte-for-byte intact; any future content drift must use the governed supersession authority instead.
        var release = new SoftwareRelease(projectId, "7.28", false);
        var baseline = new CandidateBaseline("SW-72.80", 0, projectId, release.Id, null,
            "Historical publication baseline", "qualification", DateTimeOffset.Parse(now));
        db.AddRange(release, baseline);
        await db.SaveChangesAsync();
        var files = new EvidenceFileStore(evidenceRoot);
        var caseBytes = Encoding.UTF8.GetBytes("exact historical HLR Case controlled bytes for issue 728");
        var systemBytes = Encoding.UTF8.GetBytes("exact historical System Procedure controlled bytes for issue 728");
        var storedCase = await StoreAsync(files, caseBytes, "historical-hlr-case.docx");
        var storedSystem = await StoreAsync(files, systemBytes, "historical-system-procedure.docx");
        var caseDocument = new ControlledDocument(projectId, release.Id, baseline.Id,
            ControlledDocumentType.HighLevelTestCases, "HLRTD-000729", "Historical HLR Test Cases", 0,
            new string('c', 64), 1, DateTimeOffset.Parse(now));
        var systemDocument = new ControlledDocument(projectId, release.Id, baseline.Id,
            ControlledDocumentType.SystemTestProcedures, "SYSTD-000729", "Historical System Test Procedures", 0,
            new string('5', 64), 1, DateTimeOffset.Parse(now));
        var caseArtifact = new ControlledDocumentArtifact(caseDocument.Id, "docx", storedCase.StorageKey,
            storedCase.OriginalFileName, storedCase.ContentType, storedCase.Size, storedCase.Sha256,
            DateTimeOffset.Parse(now));
        var systemArtifact = new ControlledDocumentArtifact(systemDocument.Id, "docx", storedSystem.StorageKey,
            storedSystem.OriginalFileName, storedSystem.ContentType, storedSystem.Size, storedSystem.Sha256,
            DateTimeOffset.Parse(now));
        var caseDocumentSignature = Signature(programId, caseDocument.Id, "ControlledDocument",
            "HLRTD-000729.00", caseDocument.ContentHash, now);
        var caseArtifactSignature = Signature(programId, caseArtifact.Id, "ControlledDocumentArtifact",
            "HLRTD-000729.00/docx", caseArtifact.Sha256, now);
        var systemDocumentSignature = Signature(programId, systemDocument.Id, "ControlledDocument",
            "SYSTD-000729.00", systemDocument.ContentHash, now);
        var systemArtifactSignature = Signature(programId, systemArtifact.Id, "ControlledDocumentArtifact",
            "SYSTD-000729.00/docx", systemArtifact.Sha256, now);
        db.AddRange(caseDocument, systemDocument, caseArtifact, systemArtifact,
            caseDocumentSignature, caseArtifactSignature, systemDocumentSignature, systemArtifactSignature);
        await db.SaveChangesAsync();

        await db.Database.GetService<IMigrator>().MigrateAsync();
        db.ChangeTracker.Clear();

        var rows = await db.TestProcedureDocuments.AsNoTracking().OrderBy(x => x.DocumentNumber).ToListAsync();
        Assert.Equal(3, rows.Count);
        Assert.Equal((highId, "HLRTD-000728", VerificationArtifactKind.Case),
            Project(rows.Single(x => x.Id == highId)));
        Assert.Equal((lowId, "LLRTD-000728", VerificationArtifactKind.Case),
            Project(rows.Single(x => x.Id == lowId)));
        Assert.Equal((systemId, "SYSTD-000728", VerificationArtifactKind.Procedure),
            Project(rows.Single(x => x.Id == systemId)));

        var preservedCaseDocument = await db.ControlledDocuments.AsNoTracking()
            .SingleAsync(x => x.Id == caseDocument.Id);
        var preservedSystemDocument = await db.ControlledDocuments.AsNoTracking()
            .SingleAsync(x => x.Id == systemDocument.Id);
        Assert.Equal((ControlledDocumentType.HighLevelTestCases, caseDocument.ContentHash),
            (preservedCaseDocument.Type, preservedCaseDocument.ContentHash));
        Assert.Equal((ControlledDocumentType.SystemTestProcedures, systemDocument.ContentHash),
            (preservedSystemDocument.Type, preservedSystemDocument.ContentHash));
        var preservedCaseArtifact = await db.ControlledDocumentArtifacts.AsNoTracking()
            .SingleAsync(x => x.Id == caseArtifact.Id);
        var preservedSystemArtifact = await db.ControlledDocumentArtifacts.AsNoTracking()
            .SingleAsync(x => x.Id == systemArtifact.Id);
        Assert.Equal((caseArtifact.StorageKey, caseArtifact.Sha256),
            (preservedCaseArtifact.StorageKey, preservedCaseArtifact.Sha256));
        Assert.Equal((systemArtifact.StorageKey, systemArtifact.Sha256),
            (preservedSystemArtifact.StorageKey, preservedSystemArtifact.Sha256));
        await AssertStoredBytesAsync(files, preservedCaseArtifact, caseBytes);
        await AssertStoredBytesAsync(files, preservedSystemArtifact, systemBytes);
        foreach (var original in new[]
                 {
                     caseDocumentSignature, caseArtifactSignature,
                     systemDocumentSignature, systemArtifactSignature
                 })
        {
            var preserved = await db.ElectronicSignatures.AsNoTracking().SingleAsync(x => x.Id == original.Id);
            Assert.Equal((original.ArtifactType, original.ArtifactId, original.ArtifactRevision, original.ContentHash),
                (preserved.ArtifactType, preserved.ArtifactId, preserved.ArtifactRevision, preserved.ContentHash));
        }
        Assert.DoesNotContain(await db.SecurityAuditEvents.AsNoTracking().ToListAsync(), x =>
            x.EventType is "VerificationIdentityMigration.SignatureSuperseded"
                or "VerificationIdentityMigration.SignatureSupersessionCompleted");

        var highProcedure = new TestProcedureDocument(projectId, "HLRTPD-000728",
            "High-Level Software Test Procedures Document", TestProcedureLevel.HighLevel,
            "Distinct Procedure register", "qualification", DateTimeOffset.UtcNow,
            VerificationArtifactKind.Procedure);
        db.Add(highProcedure);
        await db.SaveChangesAsync();
        Assert.Equal(2, await db.TestProcedureDocuments.CountAsync(x => x.Level == TestProcedureLevel.HighLevel));

        await Assert.ThrowsAnyAsync<Exception>(() => Sql(db,
            $"INSERT INTO test_procedure_documents (\"Id\",\"ProjectId\",\"DocumentNumber\",\"Title\",\"Level\",\"ArtifactKind\",\"Description\",\"CreatedBy\",\"CreatedAt\",\"UpdatedAt\",\"Version\") VALUES ('{Guid.NewGuid()}','{projectId}','BAD-728','Bad System Case','System','Case','','qualification','{now}','{now}',1);"));
        await Assert.ThrowsAnyAsync<Exception>(() => Sql(db,
            $"INSERT INTO test_procedure_documents (\"Id\",\"ProjectId\",\"DocumentNumber\",\"Title\",\"Level\",\"ArtifactKind\",\"Description\",\"CreatedBy\",\"CreatedAt\",\"UpdatedAt\",\"Version\") VALUES ('{Guid.NewGuid()}','{projectId}','BAD-729','Bad kind','HighLevel','Other','','qualification','{now}','{now}',1);"));
        }
        finally
        {
            if (Directory.Exists(evidenceRoot)) Directory.Delete(evidenceRoot, recursive: true);
        }
    }

    [DisposablePostgresFact]
    public async Task Clean_install_has_typed_constraints_and_no_fabricated_registers()
    {
        var connection = QualificationConnectionOrThrow();
        await using var db = new AeroLinkDbContext(Options(connection));
        await db.Database.EnsureDeletedAsync();
        await db.Database.GetService<IMigrator>().MigrateAsync();

        Assert.Empty(await db.TestProcedureDocuments.AsNoTracking().ToListAsync());
        Assert.Equal(1, await db.Database.SqlQueryRaw<int>(
                $"SELECT COUNT(*)::int AS \"Value\" FROM \"__EFMigrationsHistory\" WHERE \"MigrationId\" = '{Migration}'")
            .SingleAsync());
        Assert.Equal(2, await db.Database.SqlQueryRaw<int>(
                "SELECT COUNT(*)::int AS \"Value\" FROM pg_constraint WHERE conname IN ('CK_test_procedure_documents_ArtifactKind','CK_test_procedure_documents_SystemProcedureOnly')")
            .SingleAsync());
    }

    [Theory]
    [InlineData("Host=127.0.0.1;Port=54329;Database=aerolink_728_qualify")]
    [InlineData("Host=127.0.0.1;Port=55475;Database=aerolink_728_qualify")]
    [InlineData("Host=10.0.0.1;Port=55474;Database=aerolink_728_qualify")]
    [InlineData("Host=127.0.0.1;Port=55474;Database=other_database")]
    public void Qualification_connection_rejects_protected_non_loopback_or_wrong_database(string connection) =>
        Assert.Throws<InvalidOperationException>(() => ValidateQualificationConnection(connection));

    private static (Guid Id, string Number, VerificationArtifactKind Kind) Project(TestProcedureDocument value) =>
        (value.Id, value.DocumentNumber, value.ArtifactKind);

    private static async Task<StoredEvidence> StoreAsync(EvidenceFileStore files, byte[] bytes, string name)
    {
        await using var source = new MemoryStream(bytes, writable: false);
        return await files.StoreAsync(source, name,
            "application/vnd.openxmlformats-officedocument.wordprocessingml.document", default);
    }

    private static ElectronicSignature Signature(Guid programId, Guid artifactId, string artifactType,
        string artifactRevision, string contentHash, string now) => new(Guid.NewGuid(), "qualification.reviewer",
        "Qualification Reviewer", programId, artifactType, artifactId, artifactRevision, "Approve",
        "Historical exact publication approval", contentHash, "127.0.0.1", DateTimeOffset.Parse(now));

    private static async Task AssertStoredBytesAsync(EvidenceFileStore files, ControlledDocumentArtifact artifact,
        byte[] expected)
    {
        await using var source = await files.OpenVerifiedReadAsync(artifact.StorageKey, artifact.Size,
            artifact.Sha256, default);
        using var copy = new MemoryStream();
        await source.CopyToAsync(copy);
        Assert.Equal(expected, copy.ToArray());
    }

    private static Task RegisterSql(AeroLinkDbContext db, Guid id, Guid projectId, string number, string title,
        string level, string now) => Sql(db,
        $"INSERT INTO test_procedure_documents (\"Id\",\"ProjectId\",\"DocumentNumber\",\"Title\",\"Level\",\"Description\",\"CreatedBy\",\"CreatedAt\",\"UpdatedAt\",\"Version\") VALUES ('{id}','{projectId}','{number}','{title}','{level}','','qualification','{now}','{now}',1);");

    private static Task Sql(AeroLinkDbContext db, string sql) => db.Database.ExecuteSqlRawAsync(sql);
    private static DbContextOptions<AeroLinkDbContext> Options(string connection) =>
        new DbContextOptionsBuilder<AeroLinkDbContext>().UseNpgsql(connection).Options;

    private static string QualificationConnectionOrThrow() => ValidateQualificationConnection(
        Environment.GetEnvironmentVariable("AEROLINK_MIGRATIONS_CONNECTION"));

    private static string ValidateQualificationConnection(string? connection)
    {
        if (string.IsNullOrWhiteSpace(connection))
            throw new InvalidOperationException("Issue #728 PostgreSQL qualification requires AEROLINK_MIGRATIONS_CONNECTION.");
        var builder = new NpgsqlConnectionStringBuilder(connection);
        var host = (builder.Host ?? "").Trim().Trim('[', ']');
        var loopback = string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
            || IPAddress.TryParse(host, out var address) && IPAddress.IsLoopback(address);
        if (!loopback) throw new InvalidOperationException("Issue #728 qualification requires a loopback host.");
        if (builder.Port != Port)
            throw new InvalidOperationException($"Issue #728 qualification requires disposable port {Port} and refuses 54329.");
        if (!string.Equals(builder.Database, DatabaseName, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Issue #728 qualification requires database {DatabaseName}.");
        return connection;
    }

    private sealed class DisposablePostgresFactAttribute : FactAttribute
    {
        public DisposablePostgresFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("AEROLINK_MIGRATIONS_CONNECTION")))
                Skip = "Issue #728 PostgreSQL qualification skipped: set the dedicated disposable connection.";
        }
    }
}
