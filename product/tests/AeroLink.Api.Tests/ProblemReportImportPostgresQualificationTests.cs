using System.Data;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AeroLink.Infrastructure.Persistence;
using AeroLink.Infrastructure.Tests;
using AeroLink.Tests;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace AeroLink.Api.Tests;

// Authoring gate: PostgreSQL owns real overlapping row-lock schedules, wrapped statement failures and
// raw COMMIT failures. SQLite transaction tests cannot protect these provider boundaries. Every database
// is owned and disposable; faults are database triggers rather than production-only test hooks.
[Trait("Category", "PostgresQualification")]
public sealed class ProblemReportImportPostgresQualificationTests
{
    [DisposablePostgresFact]
    public async Task Real_overlapping_imports_reconcile_source_rows_and_same_intent_replays_one_receipt()
    {
        await using var database = await DisposablePostgresDatabase.CreateAsync("aerolink_pr_import_overlap");
        using var factory = new AeroLinkApiFactory(postgresConnection: database.ConnectionString);
        using var client = factory.CreateClient();
        await ProblemReportImportApiTests.BootstrapAsync(client);
        await using var holder = new NpgsqlConnection(database.ConnectionString); await holder.OpenAsync();
        await using var observer = new NpgsqlConnection(database.ConnectionString); await observer.OpenAsync();
        foreach (var sameIntent in new[] { false, true })
        {
            var (project, build) = await ProblemReportImportRecoveryApiTests.SeedProject(factory.Services);
            var aCsv = ProblemReportImportRecoveryApiTests.Csv;
            var bCsv = sameIntent ? aCsv : aCsv.Replace("PR-1", "PR-3");
            var aHash = await ProblemReportImportRecoveryApiTests.Preview(client, project, build, aCsv);
            var bHash = await ProblemReportImportRecoveryApiTests.Preview(client, project, build, bCsv);
            var operation = Guid.NewGuid();
            await using var held = await holder.BeginTransactionAsync(IsolationLevel.ReadCommitted);
            await LockProject(holder, held, project);
            var a = client.PostAsync(ProblemReportImportRecoveryApiTests.Root + "/commit",
                ProblemReportImportRecoveryApiTests.Form(project, build, operation, aHash, csv: aCsv));
            var b = client.PostAsync(ProblemReportImportRecoveryApiTests.Root + "/commit",
                ProblemReportImportRecoveryApiTests.Form(project, build, sameIntent ? operation : Guid.NewGuid(), bHash, csv: bCsv));
            var waiters = await WaitForLockWaiters(observer, database.Name, 2);
            await held.CommitAsync();
            using var aResult = await a; using var bResult = await b;
            Assert.True(waiters >= 2, "Both HTTP imports must actually overlap at the provider lock.");
            Assert.Equal(HttpStatusCode.OK, aResult.StatusCode); Assert.Equal(HttpStatusCode.OK, bResult.StatusCode);
            var aJson = await aResult.Content.ReadAsStringAsync(); var bJson = await bResult.Content.ReadAsStringAsync();
            if (sameIntent) Assert.Equal(aJson, bJson);
            else Assert.Equal(new[] { 1, 2 }, new[] { JsonDocument.Parse(aJson).RootElement.GetProperty("created").GetInt32(),
                JsonDocument.Parse(bJson).RootElement.GetProperty("created").GetInt32() }.Order());
            using var scope = factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            Assert.False(db.Database.HasPendingModelChanges());
            Assert.Equal(sameIntent ? 2 : 3, await db.ProblemReports.CountAsync(x => x.ProjectId == project));
            var batches = await db.ProblemReportImportBatches.Where(x => x.ProjectId == project).ToListAsync();
            Assert.Equal(sameIntent ? 1 : 2, batches.Count);
            if (sameIntent)
            {
                await using var replayHeld = await holder.BeginTransactionAsync(IsolationLevel.ReadCommitted);
                await LockProject(holder, replayHeld, project);
                var replay = client.PostAsync(ProblemReportImportRecoveryApiTests.Root + "/commit",
                    ProblemReportImportRecoveryApiTests.Form(project, build, operation, aHash, csv: aCsv));
                Assert.True(await WaitForLockWaiters(observer, database.Name, 1) >= 1);
                await Execute(observer, "UPDATE user_sessions SET \"RevokedAt\" = now() WHERE \"RevokedAt\" IS NULL");
                await replayHeld.CommitAsync();
                using var refused = await replay;
                Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
                Assert.DoesNotContain("batchId", await refused.Content.ReadAsStringAsync());
                Assert.Equal(2, await db.ProblemReports.CountAsync(x => x.ProjectId == project));
                Assert.Single(await db.ProblemReportImportBatches.Where(x => x.ProjectId == project).ToListAsync());
            }
            foreach (var batch in batches)
            {
                Assert.NotNull(batch.ReceiptJson);
                Assert.Single(await db.ElectronicSignatures.Where(x => x.ArtifactType == "ProblemReportImportBatch"
                    && x.ArtifactId == batch.Id && x.Action == "ImportProblemReports").ToListAsync());
            }
        }
    }

    [DisposablePostgresFact]
    public async Task Statement_and_commit_serialization_failures_roll_back_and_only_named_constraints_offer_retry()
    {
        await using var database = await DisposablePostgresDatabase.CreateAsync("aerolink_pr_import_fault");
        using var factory = new AeroLinkApiFactory(postgresConnection: database.ConnectionString);
        using var client = factory.CreateClient();
        await ProblemReportImportApiTests.BootstrapAsync(client);
        await using var connection = new NpgsqlConnection(database.ConnectionString); await connection.OpenAsync();
        foreach (var fault in new[] { (Code: "40001", Constraint: "", Deferred: false, Retry: true),
            (Code: "40001", Constraint: "", Deferred: true, Retry: true),
            (Code: "23505", Constraint: "ux_pr_import_operation", Deferred: false, Retry: true),
            (Code: "23505", Constraint: "ux_pr_source_identity", Deferred: false, Retry: true),
            (Code: "23505", Constraint: "unrelated_unique_constraint", Deferred: false, Retry: false) })
        {
            var (project, build) = await ProblemReportImportRecoveryApiTests.SeedProject(factory.Services);
            var operation = Guid.NewGuid(); var hash = await ProblemReportImportRecoveryApiTests.Preview(client, project, build);
            var before = await PublicationCounts(factory.Services);
            await Execute(connection, $"""
                CREATE OR REPLACE FUNCTION pr_import_fault() RETURNS trigger LANGUAGE plpgsql AS $fault$
                BEGIN RAISE EXCEPTION 'Owned qualification fault' USING ERRCODE = '{fault.Code}', CONSTRAINT = '{fault.Constraint}'; END $fault$;
                CREATE {(fault.Deferred ? "CONSTRAINT" : "")} TRIGGER pr_import_fault AFTER INSERT ON electronic_signatures
                {(fault.Deferred ? "DEFERRABLE INITIALLY DEFERRED" : "")} FOR EACH ROW
                WHEN (NEW."Action" = 'ImportProblemReports') EXECUTE FUNCTION pr_import_fault();
                """);
            using var failed = await client.PostAsync(ProblemReportImportRecoveryApiTests.Root + "/commit",
                ProblemReportImportRecoveryApiTests.Form(project, build, operation, hash));
            Assert.Equal(fault.Retry ? HttpStatusCode.Conflict : HttpStatusCode.InternalServerError, failed.StatusCode);
            if (fault.Retry)
            {
                var body = await failed.Content.ReadFromJsonAsync<JsonElement>();
                Assert.Equal("problem_report_import_concurrency", body.GetProperty("code").GetString());
                Assert.True(body.GetProperty("retryable").GetBoolean());
            }
            using (var scope = factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
                Assert.False(await db.ProblemReports.AnyAsync(x => x.ProjectId == project));
                Assert.False(await db.ProblemReportImportBatches.AnyAsync(x => x.ProjectId == project));
                Assert.Equal(before, await PublicationCounts(factory.Services));
            }
            await Execute(connection, "DROP TRIGGER pr_import_fault ON electronic_signatures; DROP FUNCTION pr_import_fault();");
            using var retry = await client.PostAsync(ProblemReportImportRecoveryApiTests.Root + "/commit",
                ProblemReportImportRecoveryApiTests.Form(project, build, operation, hash));
            Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        }
    }

    private static async Task<(int Revisions, int Links, int Signatures)> PublicationCounts(IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        return (await db.ProblemReportRevisions.CountAsync(), await db.ProblemReportLinks.CountAsync(),
            await db.ElectronicSignatures.CountAsync(x => x.Action == "ImportProblemReports"));
    }

    private static async Task LockProject(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid project)
    {
        await using var command = new NpgsqlCommand("SELECT \"Id\" FROM projects WHERE \"Id\" = @id FOR UPDATE", connection, transaction);
        command.Parameters.AddWithValue("id", project); Assert.Equal(project, await command.ExecuteScalarAsync());
    }

    private static async Task<long> WaitForLockWaiters(NpgsqlConnection connection, string database, int expected)
    {
        long waiters = 0;
        for (var attempt = 0; attempt < 100 && waiters < expected; attempt++)
        {
            await using var command = new NpgsqlCommand("SELECT count(*) FROM pg_stat_activity WHERE datname = @db AND wait_event_type = 'Lock'", connection);
            command.Parameters.AddWithValue("db", database); waiters = (long)(await command.ExecuteScalarAsync())!;
            if (waiters < expected) await Task.Delay(100);
        }
        return waiters;
    }

    private static async Task Execute(NpgsqlConnection connection, string sql)
    { await using var command = new NpgsqlCommand(sql, connection); await command.ExecuteNonQueryAsync(); }
}
