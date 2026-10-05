using AeroLink.Domain.Identity;
using AeroLink.Domain.Programs;
using AeroLink.Domain.Requirements;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace AeroLink.Infrastructure.Tests;

// Authoring gate: only a migrated PostgreSQL database can prove the frozen function, native generated
// columns, index arbitration and transactional predecessor refusal. SQLite/EnsureCreated is not migration proof.
[Trait("Category", "PostgresQualification")]
public sealed class ProblemReportSourceIdentityPostgresTests
{
    internal const string Predecessor = "20260928002122_IntegrityProblemReportSourcePackages";
    internal const string RecoveryMigration = "20261003032322_AddProblemReportImportRecovery";

    [DisposablePostgresFact]
    public async Task Upgrade_refuses_non_UTF8_server_encoding_before_publishing_the_frozen_function()
    {
        await using var database = await DisposablePostgresDatabase.CreateAsync("aerolink_pr_source_encoding");
        var server = DisposablePostgresDatabase.ValidateServer(Environment.GetEnvironmentVariable(DisposablePostgresFactAttribute.ConnectionVariable));
        await using (var admin = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(server) { Database = "postgres" }.ConnectionString))
        {
            await admin.OpenAsync();
            await using var recreate = admin.CreateCommand();
            // The helper owns this freshly created, empty unique database; it still owns cleanup.
            recreate.CommandText = $"DROP DATABASE \"{database.Name}\"";
            await recreate.ExecuteNonQueryAsync();
            recreate.CommandText = $"CREATE DATABASE \"{database.Name}\" TEMPLATE template0 ENCODING 'LATIN1' LC_COLLATE 'C' LC_CTYPE 'C'";
            await recreate.ExecuteNonQueryAsync();
        }
        await using var db = Context(database.ConnectionString);
        await db.GetService<IMigrator>().MigrateAsync(Predecessor);
        var original = await OriginalInventory(db);
        var refusal = await Assert.ThrowsAsync<PostgresException>(() => db.Database.MigrateAsync());
        Assert.Contains("requires UTF8", refusal.MessageText);
        Assert.Equal(original, await OriginalInventory(db));
        Assert.DoesNotContain(RecoveryMigration, await db.Database.GetAppliedMigrationsAsync());
        Assert.Equal(0, await db.Database.SqlQueryRaw<int>("SELECT count(*)::integer AS \"Value\" FROM pg_proc WHERE proname='aerolink_source_identity_v1'").SingleAsync());
    }

    [DisposablePostgresFact]
    public async Task Migrated_database_enforces_the_exact_source_pair_for_raw_writers_and_refuses_forged_keys()
    {
        await using var database = await DisposablePostgresDatabase.CreateAsync("aerolink_pr_source_keys");
        await using var db = Context(database.ConnectionString); await db.Database.MigrateAsync();
        Assert.False(db.Database.HasPendingModelChanges());
        var project = await ProblemReportSourceIdentityDatabaseTests.SeedProject(db);
        var sequence = 0;
        foreach (var pair in ProblemReportSourceIdentityDatabaseTests.Pairs.Where(x => !x.A.Contains('\0') && !x.B.Contains('\0')))
        {
            var system = $"Case{sequence}";
            await ProblemReportSourceIdentityDatabaseTests.InsertRaw(db, ProblemReportSourceIdentityDatabaseTests.Imported(project, $"PR-{++sequence}", system, pair.A));
            var report = ProblemReportSourceIdentityDatabaseTests.Imported(project, $"PR-{++sequence}", system, pair.B);
            var equivalent = StringComparer.OrdinalIgnoreCase.Equals(pair.A.Trim(), pair.B.Trim());
            if (equivalent)
            {
                var refused = await Assert.ThrowsAsync<PostgresException>(() => ProblemReportSourceIdentityDatabaseTests.InsertRaw(db, report));
                Assert.Equal("23505", refused.SqlState); Assert.Equal("ux_pr_source_identity", refused.ConstraintName);
            }
            else await ProblemReportSourceIdentityDatabaseTests.InsertRaw(db, report);
            Assert.Equal(equivalent ? 1 : 2, await db.ProblemReports.CountAsync(x => x.ProjectId == project && x.SourceSystem == system));
        }
        await ProblemReportSourceIdentityDatabaseTests.InsertRaw(db, ProblemReportSourceIdentityDatabaseTests.Imported(project, $"PR-{++sequence}", "Jira", "Key"));
        await ProblemReportSourceIdentityDatabaseTests.InsertRaw(db, ProblemReportSourceIdentityDatabaseTests.Imported(project, $"PR-{++sequence}", "jira", "key"));
        var forged = await Assert.ThrowsAsync<PostgresException>(() => ProblemReportSourceIdentityDatabaseTests.InsertRaw(db,
            ProblemReportSourceIdentityDatabaseTests.Imported(project, $"PR-{++sequence}", "Jira", "Forged"), suppliedKey: [1]));
        Assert.Equal("428C9", forged.SqlState);
        foreach (var malformed in new (object? System, object? Key)[] { (null, "Partial"), ("Jira", null), (" ", "Empty"), (" Jira", "Untrimmed"), ("Jira", "Key\u3000") })
        {
            var shape = await Assert.ThrowsAsync<PostgresException>(() => ProblemReportSourceIdentityDatabaseTests.InsertRaw(db,
                ProblemReportSourceIdentityDatabaseTests.Imported(project, $"PR-{++sequence}", "Valid", "Valid"), malformed.System, malformed.Key, overrideSource: true));
            Assert.Equal("23514", shape.SqlState); Assert.Equal("CK_pr_source_identity_shape", shape.ConstraintName);
        }
        var normal = new ProblemReport(project, $"PR-{++sequence}", "Native", "Native problem", "", "author", DateTimeOffset.UtcNow);
        await ProblemReportSourceIdentityDatabaseTests.InsertRaw(db, normal);
        Assert.Null(await db.ProblemReports.Where(x => x.Id == normal.Id).Select(x => EF.Property<byte[]>(x, "SourceKeyIdentityV1")).SingleAsync());
        // Bypass the service/project lock: the generated unique index must arbitrate competing raw writers.
        await using var first = Context(database.ConnectionString);
        await using var second = Context(database.ConnectionString);
        await using var transaction = await first.Database.BeginTransactionAsync();
        await ProblemReportSourceIdentityDatabaseTests.InsertRaw(first,
            ProblemReportSourceIdentityDatabaseTests.Imported(project, "PR-RACE-A", "Race", "Concurrent"));
        var competing = ProblemReportSourceIdentityDatabaseTests.InsertRaw(second,
            ProblemReportSourceIdentityDatabaseTests.Imported(project, "PR-RACE-B", "Race", "CONCURRENT"));
        var blocked = false;
        for (var attempt = 0; attempt < 100 && !blocked; attempt++)
        {
            blocked = await db.Database.SqlQueryRaw<int>("SELECT count(*)::integer AS \"Value\" FROM pg_stat_activity WHERE datname=current_database() AND wait_event='transactionid'").SingleAsync() > 0;
            if (!blocked) await Task.Delay(50);
        }
        await transaction.CommitAsync();
        var conflict = await Assert.ThrowsAsync<PostgresException>(() => competing);
        Assert.True(blocked, "The competing raw insert must reach the actual unique-index wait.");
        Assert.Equal("23505", conflict.SqlState); Assert.Equal("ux_pr_source_identity", conflict.ConstraintName);
        Assert.Equal(1, await db.ProblemReports.CountAsync(x => x.ProjectId == project && x.SourceSystem == "Race"));
    }

    [DisposablePostgresFact]
    public async Task Upgrade_preserves_original_source_history_and_unsigned_batches_and_refuses_bad_existing_identities_atomically()
    {
        foreach (var invalid in new[] { "none", "duplicate", "partial", "untrimmed" })
        {
            await using var database = await DisposablePostgresDatabase.CreateAsync("aerolink_pr_source_upgrade");
            await using var db = Context(database.ConnectionString);
            await db.GetService<IMigrator>().MigrateAsync(Predecessor);
            var now = DateTimeOffset.Parse("2024-01-02T03:04:05Z");
            var program = new ProgramRecord("Historical source", "HISTORY");
            var project = new ProjectRecord(program.Id, "Historical source", "Historical source");
            var actor = new UserAccount("historical", "Original name", "historical@example.test", "original-password-hash", now);
            var report = ProblemReportSourceIdentityDatabaseTests.Imported(project.Id, "PR-1", "Jira", "longſ");
            var snapshot = ProblemReportEvidenceContract.Create(report);
            var json = ProblemReportEvidenceContract.Serialize(snapshot); var hash = ProblemReportEvidenceContract.Hash(json);
            var revision = new ProblemReportRevision(report.Id, report.Revision, "ImportedFromSource", "historical", hash, json, now);
            var batch = new ProblemReportImportBatch(project.Id, "Jira", "original.csv", new string('a', 64), "{\"original\":true}", new string('b', 64), 1, 0, "historical", now);
            var signature = new ElectronicSignature(actor.Id, actor.UserName, actor.DisplayName, program.Id, "ProblemReport", report.Id,
                report.DisplayNumber, "OriginalHistory", "Original meaning", hash, "original-address", now);
            db.AddRange(program, project, actor, report, revision, batch, signature);
            await PredecessorSchemaRows.InsertTrackedAsync(db);
            Guid? conflict = null;
            if (invalid != "none")
            {
                var bad = ProblemReportSourceIdentityDatabaseTests.Imported(project.Id, "PR-2", "Jira", "LONGſ"); conflict = bad.Id;
                db.Add(bad); await PredecessorSchemaRows.InsertTrackedAsync(db);
                if (invalid == "partial") await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE problem_reports SET \"SourceSystem\"=NULL WHERE \"Id\"={bad.Id}");
                if (invalid == "untrimmed") await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE problem_reports SET \"SourceSystem\"=' Jira' WHERE \"Id\"={bad.Id}");
            }
            var original = await OriginalInventory(db);
            if (invalid == "none")
            {
                await db.Database.MigrateAsync();
                Assert.Equal(original, await OriginalInventory(db));
                var immutable = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync(
                    $"UPDATE problem_report_import_batches SET \"PreviewHash\"='rewritten' WHERE \"Id\"={batch.Id}"));
                Assert.Contains("receipts are immutable", immutable.MessageText);
                Assert.Equal(original, await OriginalInventory(db));
                var stored = await db.ProblemReportImportBatches.SingleAsync();
                Assert.Null(stored.OperationId); Assert.Null(stored.ActorId); Assert.Null(stored.RequestHash); Assert.Null(stored.ReceiptJson);
                // Keep the exact historical upgrade boundary; later unrelated migrations may follow it.
                var applied = (await db.Database.GetAppliedMigrationsAsync()).ToArray();
                var predecessorIndex = Array.IndexOf(applied, Predecessor);
                Assert.True(predecessorIndex >= 0, "The original source-history migration must remain applied.");
                Assert.Equal(new[] { Predecessor, RecoveryMigration }, applied.Skip(predecessorIndex).Take(2));
                Assert.Equal(new[] { signature.Id }, await db.ElectronicSignatures.Select(x => x.Id).ToArrayAsync());
            }
            else
            {
                var refusal = await Assert.ThrowsAsync<PostgresException>(() => db.Database.MigrateAsync());
                Assert.Contains(conflict!.Value.ToString(), refusal.MessageText);
                Assert.Equal(original, await OriginalInventory(db));
                Assert.DoesNotContain(RecoveryMigration, await db.Database.GetAppliedMigrationsAsync());
                var columns = await db.Database.SqlQueryRaw<int>("SELECT count(*)::integer AS \"Value\" FROM information_schema.columns WHERE table_name='problem_reports' AND column_name='SourceKeyIdentityV1'").SingleAsync();
                Assert.Equal(0, columns);
                var functions = await db.Database.SqlQueryRaw<int>("SELECT count(*)::integer AS \"Value\" FROM pg_proc WHERE proname='aerolink_source_identity_v1'").SingleAsync();
                Assert.Equal(0, functions);
            }
        }
    }

    private static async Task<string> OriginalInventory(AeroLinkDbContext db)
    {
        // Compare every original column, including exact source text, snapshot JSON/hash and signature bytes.
        return await db.Database.SqlQueryRaw<string>("""
            SELECT (SELECT coalesce(jsonb_agg(to_jsonb(r) - 'SourceSystemIdentityV1' - 'SourceKeyIdentityV1' ORDER BY "Id"),'[]') FROM problem_reports r)::text
              || (SELECT coalesce(jsonb_agg(to_jsonb(r) ORDER BY "Id"),'[]') FROM problem_report_revisions r)::text
              || (SELECT coalesce(jsonb_agg(to_jsonb(s) ORDER BY "Id"),'[]') FROM electronic_signatures s)::text
              || (SELECT coalesce(jsonb_agg(to_jsonb(b) - 'OperationId' - 'ActorId' - 'RequestHash' - 'ReceiptJson' ORDER BY "Id"),'[]') FROM problem_report_import_batches b)::text AS "Value"
            """).SingleAsync();
    }
    private static AeroLinkDbContext Context(string connection) => new(new DbContextOptionsBuilder<AeroLinkDbContext>().UseNpgsql(connection).Options);
}
