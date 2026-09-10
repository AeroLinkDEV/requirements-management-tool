using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Hierarchy;
using AeroLink.Domain.Programs;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace AeroLink.Infrastructure.Tests;

[CollectionDefinition("Issue973Postgres", DisableParallelization = true)]
public sealed class Issue973PostgresCollection;

/// <summary>
/// Opt-in PostgreSQL proof for the CQ10 adjacency backfill. It always creates and drops a unique disposable
/// database; the protected developer/demo port and database are rejected by the connection guard.
/// </summary>
[Collection("Issue973Postgres")]
public sealed class FrozenReviewTraceAdjacencyPostgresQualificationTests
{
    [Issue973PostgresFact]
    public async Task Concurrent_backfill_is_unique_and_preserves_exact_snapshot_bytes()
    {
        var server = QualificationServerOrThrow();
        var databaseName = $"aerolink_973_backfill_{Guid.NewGuid():N}";
        var created = false;
        try
        {
            await CreateDatabaseAsync(server, databaseName);
            created = true;
            var database = new NpgsqlConnectionStringBuilder(server.ConnectionString)
            { Database = databaseName, Pooling = false }.ConnectionString;

            Dictionary<Guid, (string Json, string Hash)> expected;
            Guid parentId;
            await using (var db = CreateContext(database))
            {
                await db.Database.MigrateAsync();
                var now = new DateTimeOffset(2026, 9, 8, 12, 0, 0, TimeSpan.Zero);
                var program = new ProgramRecord("CQ10 PostgreSQL", $"Q{Guid.NewGuid():N}"[..8]);
                var project = new ProjectRecord(program.Id, "CQ10 backfill", "Disposable trace product");
                var release = new SoftwareRelease(project.Id, "1.0", false);
                var parent = new SystemChangeRequest("SRCR-97300", 0, project.Id, release.Id,
                    "Parent", "Problem", "Analysis", "Solution", "author", now);
                parentId = parent.Id;
                var cycles = new List<ReviewCycle>();
                for (var index = 0; index < 101; index++)
                {
                    var child = new SystemChangeRequest($"SRCR-{97301 + index:00000}", 0, project.Id,
                        release.Id, $"Child {index}", "Problem", "Analysis", "Solution", "author", now);
                    var snapshot = Snapshot(parent.Id, Guid.NewGuid(), Guid.NewGuid(), release.Id);
                    cycles.Add(new ReviewCycle(child.Id, 1, Hash(snapshot),
                        [new ApproverSelection("reviewer", "Reviewer")], now,
                        snapshotContractVersion: 3, snapshotJson: snapshot));
                    db.AddRange(child, cycles[^1]);
                }
                db.AddRange(program, project, release, parent);
                await db.SaveChangesAsync();
                expected = await db.ReviewCycles.AsNoTracking()
                    .Where(x => cycles.Select(y => y.Id).Contains(x.Id))
                    .ToDictionaryAsync(x => x.Id, x => (x.SnapshotJson, x.SnapshotHash));

                // The backfill is intentionally tested against legacy rows whose disposable lookup was lost.
                await db.Database.ExecuteSqlRawAsync("DELETE FROM frozen_review_trace_links");
            }

            async Task RunAuthorityAsync()
            {
                await using var db = CreateContext(database);
                await new FrozenReviewTraceAdjacencyMigrationAuthority(db).EnsureCompletedAsync();
            }

            await Task.WhenAll(RunAuthorityAsync(), RunAuthorityAsync());

            await using (var verify = CreateContext(database))
            {
                Assert.Equal(101, await verify.Set<FrozenReviewTraceLink>().CountAsync());
                Assert.All(await verify.Set<FrozenReviewTraceLink>().AsNoTracking().ToListAsync(),
                    row => Assert.Equal(parentId, row.UpstreamId));
                var rows = await verify.ReviewCycles.AsNoTracking()
                    .Where(x => expected.Keys.Contains(x.Id)).ToListAsync();
                Assert.Equal(expected.Count, rows.Count);
                foreach (var row in rows)
                {
                    Assert.Equal(expected[row.Id].Json, row.SnapshotJson);
                    Assert.Equal(expected[row.Id].Hash, row.SnapshotHash);
                }

                var completion = Assert.Single(await verify.GovernedMigrationCompletions.AsNoTracking()
                    .Where(x => x.Marker == FrozenReviewTraceAdjacencyMigrationAuthority.Marker).ToListAsync());
                var audit = Assert.Single(await verify.SecurityAuditEvents.AsNoTracking()
                    .Where(x => x.EventType == FrozenReviewTraceAdjacencyMigrationAuthority.Marker + ".Completed"
                        && x.Target == FrozenReviewTraceAdjacencyMigrationAuthority.AuditTarget).ToListAsync());
                Assert.Equal("Success", audit.Outcome);
                Assert.Equal(completion.TotalsJson, audit.Detail);
                using var totals = JsonDocument.Parse(completion.TotalsJson);
                Assert.Equal(101, totals.RootElement.GetProperty("CyclesExamined").GetInt32());
                Assert.Equal(101, totals.RootElement.GetProperty("AdjacencyRowsInserted").GetInt32());
            }
        }
        finally
        {
            if (created)
                await DropDatabaseAsync(server, databaseName);
        }
    }

    private static AeroLinkDbContext CreateContext(string connection) =>
        new(new DbContextOptionsBuilder<AeroLinkDbContext>().UseNpgsql(connection).Options);

    private static string Snapshot(Guid parentId, Guid assessmentId, Guid assessmentLinkId, Guid buildId) =>
        JsonSerializer.Serialize(new
        {
            contractVersion = 3,
            authoredLinks = Array.Empty<object>(),
            derivedLinks = new[] { new
            {
                upstreamChangeRequestId = parentId,
                assessmentId,
                assessmentLinkId,
                buildId
            }
        }});

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static string? ResolveServerConnection() =>
        Environment.GetEnvironmentVariable("AEROLINK_973_SERVER_CONNECTION");

    private static NpgsqlConnectionStringBuilder QualificationServerOrThrow()
    {
        var raw = ResolveServerConnection();
        if (string.IsNullOrWhiteSpace(raw))
            throw new InvalidOperationException(
                "Issue #973 PostgreSQL qualification requires AEROLINK_973_SERVER_CONNECTION.");
        var builder = new NpgsqlConnectionStringBuilder(raw);
        var host = (builder.Host ?? string.Empty).Trim().Trim('[', ']');
        if (!string.Equals(host, "127.0.0.1", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Issue #973 PostgreSQL qualification requires a loopback host.");
        if (builder.Port is < 55438 or > 55499)
            throw new InvalidOperationException("Issue #973 PostgreSQL qualification requires port 55438-55499.");
        builder.Pooling = false;
        return builder;
    }

    private static async Task CreateDatabaseAsync(NpgsqlConnectionStringBuilder server, string databaseName)
    {
        var admin = new NpgsqlConnectionStringBuilder(server.ConnectionString)
        { Database = "postgres", Pooling = false };
        await using var connection = new NpgsqlConnection(admin.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"CREATE DATABASE \"{databaseName}\";";
        await command.ExecuteNonQueryAsync();
    }

    private static async Task DropDatabaseAsync(NpgsqlConnectionStringBuilder server, string databaseName)
    {
        var admin = new NpgsqlConnectionStringBuilder(server.ConnectionString)
        { Database = "postgres", Pooling = false };
        await using var connection = new NpgsqlConnection(admin.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"DROP DATABASE IF EXISTS \"{databaseName}\";";
        await command.ExecuteNonQueryAsync();
    }

    private sealed class Issue973PostgresFactAttribute : FactAttribute
    {
        public Issue973PostgresFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(ResolveServerConnection()))
                Skip = "Issue #973 PostgreSQL qualification skipped: set AEROLINK_973_SERVER_CONNECTION.";
        }
    }
}
