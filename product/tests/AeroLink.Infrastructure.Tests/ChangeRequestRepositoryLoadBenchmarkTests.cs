using System.Diagnostics;
using System.Data;
using System.Net;
using AeroLink.Domain.Common;
using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Contracts;
using AeroLink.Domain.Hierarchy;
using AeroLink.Domain.Programs;
using AeroLink.Domain.Requirements;
using AeroLink.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit.Abstractions;

namespace AeroLink.Infrastructure.Tests;

/// <summary>
/// Opt-in CQ09 measurements for the old complete sibling-join shape and the scoped repository shapes. The
/// fixture deliberately grows independent requirement, review, discussion, audit, and upstream histories so a
/// lower command count cannot hide a larger provider result set.
/// </summary>
public sealed class ChangeRequestRepositoryLoadBenchmarkTests(ITestOutputHelper output)
{
    [Cq09BenchmarkFact]
    public async Task Sqlite_reports_legacy_and_scoped_load_costs()
    {
        await using var scenario = await LoadScenario.CreateSqliteAsync();
        await MeasureAsync(scenario, "sqlite-baseline-single-full", LegacyFullAsync);
        await MeasureAsync(scenario, "sqlite-after-complete-split", CompleteAsync);
        await MeasureAsync(scenario, "sqlite-after-detail", DetailAsync);
        await MeasureAsync(scenario, "sqlite-after-review-comments", ReviewCommentsAsync);
        await MeasureAsync(scenario, "sqlite-after-upstream-links", UpstreamLinksAsync);
    }

    [Cq09PostgresFact]
    public async Task PostgreSql_reports_load_costs_and_query_plans()
    {
        var server = QualificationServerConnectionOrThrow();
        await using var scenario = await LoadScenario.CreatePostgresAsync(server);
        await MeasureAsync(scenario, "postgres-baseline-single-full", LegacyFullAsync, includePlans: true);
        await MeasureAsync(scenario, "postgres-after-complete-split", CompleteAsync, includePlans: true);
        await MeasureAsync(scenario, "postgres-after-detail", DetailAsync, includePlans: true);
        await MeasureAsync(scenario, "postgres-after-review-comments", ReviewCommentsAsync, includePlans: true);
        await MeasureAsync(scenario, "postgres-after-upstream-links", UpstreamLinksAsync, includePlans: true);
    }

    [Cq09PostgresFact]
    public async Task PostgreSql_reuses_only_snapshot_transactions_for_split_loads()
    {
        var server = QualificationServerConnectionOrThrow();
        await using var scenario = await LoadScenario.CreatePostgresAsync(server);

        var readCommittedMeasurement = new QueryReadMeasurement { Enabled = true };
        await using (var readCommitted = scenario.Open(readCommittedMeasurement))
        await using (var transaction = await readCommitted.Database.BeginTransactionAsync(
                         IsolationLevel.ReadCommitted))
        {
            var request = await new ChangeRequestRepository(readCommitted).GetAsync(scenario.RequestId,
                ChangeRequestLoadShape.Detail, CancellationToken.None);
            Assert.NotNull(request);
            // PostgreSQL ReadCommitted can observe a different committed state for each split statement. The
            // repository therefore keeps this caller-owned transaction as one SQL statement.
            Assert.Single(readCommittedMeasurement.Statements);
            Assert.NotNull(readCommitted.Database.CurrentTransaction);
            await transaction.RollbackAsync();
        }

        var snapshotMeasurement = new QueryReadMeasurement { Enabled = true };
        await using var snapshot = scenario.Open(snapshotMeasurement);
        await using var snapshotTransaction = await snapshot.Database.BeginTransactionAsync(
            IsolationLevel.RepeatableRead);
        var snapshotRequest = await new ChangeRequestRepository(snapshot).GetAsync(scenario.RequestId,
            ChangeRequestLoadShape.Detail, CancellationToken.None);
        Assert.NotNull(snapshotRequest);
        Assert.True(snapshotMeasurement.Statements.Count >= 2);
        Assert.NotNull(snapshot.Database.CurrentTransaction);
        await snapshotTransaction.RollbackAsync();
    }

    private async Task MeasureAsync(LoadScenario scenario, string label,
        Func<AeroLinkDbContext, Guid, CancellationToken, Task<SystemChangeRequest?>> load,
        bool includePlans = false)
    {
        await using (var warm = scenario.Open())
            _ = await load(warm, scenario.RequestId, CancellationToken.None);

        var measurement = new QueryReadMeasurement();
        await using var db = scenario.Open(measurement);
        measurement.Enabled = true;
        var allocated = GC.GetTotalAllocatedBytes(true);
        var watch = Stopwatch.StartNew();
        var request = await load(db, scenario.RequestId, CancellationToken.None);
        watch.Stop();
        allocated = GC.GetTotalAllocatedBytes(true) - allocated;
        measurement.Enabled = false;

        Assert.NotNull(request);
        output.WriteLine($"CQ09 {label}: rows={measurement.Rows}; jsonBytes={measurement.JsonBytes}; "
            + $"commands={measurement.Statements.Count}; allocatedBytes={allocated}; "
            + $"elapsedMs={watch.Elapsed.TotalMilliseconds:F2}");
        for (var i = 0; i < measurement.Statements.Count; i++)
        {
            var statement = measurement.Statements[i];
            output.WriteLine($"CQ09 {label} SQL[{i}] parameters={statement.Parameters.Count}");
            output.WriteLine(statement.Sql);
        }

        if (includePlans)
            await ExplainAsync(scenario.ConnectionString!, label, measurement.Statements);
    }

    private static Task<SystemChangeRequest?> LegacyFullAsync(AeroLinkDbContext db, Guid id, CancellationToken ct) =>
        db.SystemChangeRequests
            .Include(x => x.RequirementChanges)
            .Include(x => x.ReviewCycles).ThenInclude(x => x.Steps)
            .Include(x => x.ReviewCycles).ThenInclude(x => x.Comments)
            .Include(x => x.AuditEvents)
            .Include(x => x.UpstreamLinks)
            .Include(x => x.UpstreamHistory)
            .SingleOrDefaultAsync(x => x.Id == id, ct);

    private static Task<SystemChangeRequest?> CompleteAsync(AeroLinkDbContext db, Guid id, CancellationToken ct) =>
        new ChangeRequestRepository(db).GetAsync(id, ChangeRequestLoadShape.Complete, ct);

    private static Task<SystemChangeRequest?> DetailAsync(AeroLinkDbContext db, Guid id, CancellationToken ct) =>
        new ChangeRequestRepository(db).GetAsync(id, ChangeRequestLoadShape.Detail, ct);

    private static Task<SystemChangeRequest?> ReviewCommentsAsync(AeroLinkDbContext db, Guid id, CancellationToken ct) =>
        new ChangeRequestRepository(db).GetAsync(id, ChangeRequestLoadShape.ReviewDiscussion, ct);

    private static Task<SystemChangeRequest?> UpstreamLinksAsync(AeroLinkDbContext db, Guid id, CancellationToken ct) =>
        new ChangeRequestRepository(db).GetAsync(id, ChangeRequestLoadShape.UpstreamLinks, ct);

    private async Task ExplainAsync(string connectionString, string label,
        IReadOnlyList<QueryReadMeasurement.Statement> statements)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        for (var i = 0; i < statements.Count; i++)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "EXPLAIN (ANALYZE, BUFFERS, FORMAT JSON) " + statements[i].Sql;
            foreach (var parameter in statements[i].Parameters)
                command.Parameters.AddWithValue(parameter.Name, parameter.Value ?? DBNull.Value);
            var plan = (await command.ExecuteScalarAsync())?.ToString() ?? "";
            output.WriteLine($"CQ09 {label} PLAN[{i}] {plan}");
        }
    }

    private static string QualificationServerConnectionOrThrow()
    {
        var raw = Environment.GetEnvironmentVariable("AEROLINK_MIGRATIONS_CONNECTION");
        if (string.IsNullOrWhiteSpace(raw))
            throw new InvalidOperationException("CQ09 PostgreSQL qualification requires AEROLINK_MIGRATIONS_CONNECTION.");
        var builder = new NpgsqlConnectionStringBuilder(raw);
        var host = (builder.Host ?? string.Empty).Trim().Trim('[', ']');
        if (!string.Equals(host, "127.0.0.1", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("CQ09 PostgreSQL qualification requires a loopback host.");
        if (builder.Port != 55465)
            throw new InvalidOperationException("CQ09 PostgreSQL qualification requires disposable port 55465 and refuses 54329.");
        return raw;
    }

    private sealed class Cq09BenchmarkFactAttribute : FactAttribute
    {
        public Cq09BenchmarkFactAttribute()
        {
            if (Environment.GetEnvironmentVariable("AEROLINK_CQ09_BENCHMARK") != "1")
                Skip = "Set AEROLINK_CQ09_BENCHMARK=1 to run CQ09 load measurements.";
        }
    }

    private sealed class Cq09PostgresFactAttribute : FactAttribute
    {
        public Cq09PostgresFactAttribute()
        {
            var raw = Environment.GetEnvironmentVariable("AEROLINK_MIGRATIONS_CONNECTION");
            if (string.IsNullOrWhiteSpace(raw))
            {
                Skip = "Set AEROLINK_MIGRATIONS_CONNECTION to the disposable CQ09 PostgreSQL server.";
                return;
            }

            try
            {
                var builder = new NpgsqlConnectionStringBuilder(raw);
                var host = (builder.Host ?? string.Empty).Trim().Trim('[', ']');
                if (builder.Port != 55465
                    || (!string.Equals(host, "127.0.0.1", StringComparison.OrdinalIgnoreCase)
                        && !string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)))
                    Skip = "CQ09 PostgreSQL qualification requires disposable loopback port 55465.";
            }
            catch (ArgumentException)
            {
                Skip = "CQ09 PostgreSQL qualification requires a valid disposable connection string.";
            }
        }
    }

    private sealed class LoadScenario : IAsyncDisposable
    {
        private readonly SqliteConnection? _sqlite;
        private readonly bool _isSqlite;
        private readonly string? _database;
        private readonly string? _serverConnection;

        private LoadScenario(DbContextOptions<AeroLinkDbContext> options, Guid requestId,
            SqliteConnection? sqlite, string? connectionString, string? serverConnection, string? database,
            bool isSqlite)
        {
            Options = options;
            RequestId = requestId;
            _sqlite = sqlite;
            ConnectionString = connectionString;
            _serverConnection = serverConnection;
            _database = database;
            _isSqlite = isSqlite;
        }

        private DbContextOptions<AeroLinkDbContext> Options { get; }
        public Guid RequestId { get; }
        public string? ConnectionString { get; }

        public AeroLinkDbContext Open(QueryReadMeasurement? measurement = null)
        {
            var builder = new DbContextOptionsBuilder<AeroLinkDbContext>();
            if (_isSqlite)
                builder.UseSqlite(_sqlite!);
            else
                builder.UseNpgsql(ConnectionString!);
            if (measurement is not null) builder.AddInterceptors(measurement);
            return new AeroLinkDbContext(builder.Options);
        }

        public static async Task<LoadScenario> CreateSqliteAsync()
        {
            var sqlite = new SqliteConnection("Data Source=:memory:");
            await sqlite.OpenAsync();
            var options = new DbContextOptionsBuilder<AeroLinkDbContext>().UseSqlite(sqlite).Options;
            await using var db = new AeroLinkDbContext(options);
            await db.Database.EnsureCreatedAsync();
            var id = await SeedAsync(db);
            return new LoadScenario(options, id, sqlite, null, null, null, isSqlite: true);
        }

        public static async Task<LoadScenario> CreatePostgresAsync(string serverConnection)
        {
            var database = $"aerolink_972_{Guid.NewGuid():N}";
            await using (var admin = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(serverConnection)
            { Database = "postgres" }.ConnectionString))
            {
                await admin.OpenAsync();
                await using var command = admin.CreateCommand();
                command.CommandText = $"CREATE DATABASE \"{database}\"";
                await command.ExecuteNonQueryAsync();
            }

            var connectionString = new NpgsqlConnectionStringBuilder(serverConnection) { Database = database }.ConnectionString;
            try
            {
                var options = new DbContextOptionsBuilder<AeroLinkDbContext>().UseNpgsql(connectionString).Options;
                await using var db = new AeroLinkDbContext(options);
                await db.Database.MigrateAsync();
                var id = await SeedAsync(db);
                return new LoadScenario(options, id, null, connectionString, serverConnection, database, isSqlite: false);
            }
            catch
            {
                await DropDatabaseAsync(serverConnection, database);
                throw;
            }
        }

        private static async Task<Guid> SeedAsync(AeroLinkDbContext db)
        {
            var now = DateTimeOffset.UtcNow;
            var program = new ProgramRecord("CQ09 repository load benchmark", $"B{Guid.NewGuid():N}"[..7]);
            var project = new ProjectRecord(program.Id, "CQ09 repository load benchmark", "Performance qualification");
            var release = new SoftwareRelease(project.Id, "1.0", false);
            var source = new SystemChangeRequest("SRCR-97210", 0, project.Id, release.Id,
                "Benchmark source", "Problem", "Analysis", "Solution", "author", now);
            var request = new SystemChangeRequest("HLRCR-97211", 0, project.Id, release.Id,
                "Benchmark aggregate", "Problem", "Analysis", "Solution", "author", now,
                ChangeRequestType.Software, softwareLevel: RequirementLevel.HighLevel);
            for (var i = 1; i <= 24; i++)
                request.AddRequirementChange("author", $"HLR-{i + 97200:D6}", 0, RequirementLevel.HighLevel,
                    RequirementChangeKind.Introduce, $"Controlled benchmark statement {i}.",
                    "Benchmark rationale", "Test", now, attributesJson: "{\"derived\":true}");
            request.AddUpstreamLink("author", source.Id, source.DisplayNumber, release.Id, release.Version,
                "The benchmark source controls this aggregate.", now);
            for (var i = 1; i <= 5; i++)
                request.ChangeUpstreamLinkRationale("author", request.UpstreamLinks.Single().Id,
                    $"Benchmark upstream rationale revision {i}.", now.AddSeconds(i));

            // PostgreSQL enforces that upstream-history inserts belong to a draft owner. Persist the draft graph
            // before the review-cycle transitions change the aggregate state; SQLite does not have that trigger.
            db.AddRange(program, project);
            await db.SaveChangesAsync();
            db.Add(LegacyDefaultProjectLadderFactory.Create(project.Id, now));
            await db.SaveChangesAsync();
            db.AddRange(release, source, request);
            await db.SaveChangesAsync();

            // Start lifecycle qualification from a fresh tracked graph. The save boundary may update the
            // aggregate's concurrency/version values while persisting the draft and its upstream history.
            db.ChangeTracker.Clear();
            request = await db.SystemChangeRequests
                .Include(x => x.RequirementChanges)
                .Include(x => x.UpstreamLinks)
                .SingleAsync(x => x.Id == request.Id);

            for (var cycleNumber = 0; cycleNumber < 6; cycleNumber++)
            {
                var cycle = new ReviewCycle(request.Id, cycleNumber, new string('a', 64), [
                    new("reviewer", "Benchmark Reviewer"),
                    new("assurance", "Benchmark Assurance"),
                    new("quality", "Benchmark Quality")], now.AddMinutes(cycleNumber), ReviewMode.Parallel);
                cycle.AddComment("reviewer", ReviewCommentAnchor.ChangeCase, null,
                    $"Benchmark discussion comment {cycleNumber}.", now.AddMinutes(cycleNumber).AddSeconds(1));
                if (cycleNumber < 5)
                    cycle.ReturnActiveStep("reviewer", $"Benchmark review return {cycleNumber}.",
                        now.AddMinutes(cycleNumber).AddSeconds(2));
                db.ReviewCycles.Add(cycle);
                db.AuditEvents.Add(new AuditEvent(request.Id, "BenchmarkReview", "reviewer",
                    $"Benchmark review cycle {cycleNumber}.", now.AddMinutes(cycleNumber)));
            }
            // The review rows are the load-shape workload; the request itself stays at its persisted draft state.
            // The cycles, steps, comments, and audit events remain real EF rows and are saved together.
            await db.SaveChangesAsync();
            return request.Id;
        }

        public async ValueTask DisposeAsync()
        {
            if (_sqlite is not null) await _sqlite.DisposeAsync();
            if (_serverConnection is not null && _database is not null)
                await DropDatabaseAsync(_serverConnection, _database);
        }

        private static async Task DropDatabaseAsync(string serverConnection, string database)
        {
            await using var admin = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(serverConnection)
            { Database = "postgres" }.ConnectionString);
            await admin.OpenAsync();
            await using var command = admin.CreateCommand();
            command.CommandText = $"DROP DATABASE IF EXISTS \"{database}\" WITH (FORCE)";
            await command.ExecuteNonQueryAsync();
        }
    }
}
