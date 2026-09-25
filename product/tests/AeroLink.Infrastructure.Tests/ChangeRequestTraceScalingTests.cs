using System.Diagnostics;
using System.Data.Common;
using Npgsql;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Hierarchy;
using AeroLink.Domain.Programs;
using AeroLink.Domain.Verification;
using AeroLink.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit.Abstractions;

namespace AeroLink.Infrastructure.Tests;

[Trait("Category", "PostgresQualification")]
public sealed class ChangeRequestTraceScalingTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(100)]
    [InlineData(1000)]
    public async Task Measures_fixed_graph_with_unrelated_project_history(int unrelated)
        => await MeasureAsync(unrelated, new SqliteConnection("Data Source=:memory:"));

    [DisposablePostgresFact]
    public async Task PostgreSQL_fixed_graph_scaling_and_query_plans()
    {
        foreach (var population in new[] { 100, 1000 })
        {
            await using var database = await DisposablePostgresDatabase.CreateAsync("aerolink_973_scaling");
            await MeasureAsync(population, new NpgsqlConnection(database.ConnectionString));
        }
    }

    private async Task MeasureAsync(int unrelated, DbConnection connection)
    {
        await using var ownedConnection = connection;
        await connection.OpenAsync();
        var measurement = new QueryReadMeasurement();
        await using var db = new AeroLinkDbContext(new DbContextOptionsBuilder<AeroLinkDbContext>()
            .ConfigureProvider(connection).AddInterceptors(measurement).Options);
        if (connection is NpgsqlConnection) await db.Database.MigrateAsync();
        else await db.Database.EnsureCreatedAsync();
        var now = new DateTimeOffset(2026, 9, 8, 12, 0, 0, TimeSpan.Zero);
        var program = new ProgramRecord("Scaling", "SCL");
        var project = new ProjectRecord(program.Id, "Scaling", "Product");
        var requestedRelease = new SoftwareRelease(project.Id, "1.0", false);
        var otherRelease = new SoftwareRelease(project.Id, "2.0", false);
        db.AddRange(program, project, requestedRelease, otherRelease);
        var root = CreateCr(0, requestedRelease.Id);
        var rootTcr = CreateTcr(root, 0);
        db.AddRange(root, rootTcr);
        Guid? previousId = null;
        for (var i = 1; i <= unrelated; i++)
        {
            var cr = CreateCr(i, otherRelease.Id);
            db.AddRange(cr, CreateTcr(cr, i));
            var json = JsonSerializer.Serialize(new { authoredLinks = previousId is Guid parent
                ? new[] { new { upstreamChangeRequestId = parent } } : [], derivedLinks = Array.Empty<object>(), padding = new string('x', 4096) });
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
            previousId = cr.Id;
            db.Add(new ReviewCycle(cr.Id, 1, hash, [new ApproverSelection("reviewer", "Reviewer")], now,
                snapshotContractVersion: 3, snapshotJson: json));
        }
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        // Warm provider translation/JIT before recording an observed run.
        await ChangeRequestTraceProjection.ForChangeRequestAsync(db, project.Id, root.Id, LegacyLadderPolicy.Instance, CancellationToken.None);
        await ChangeRequestTraceProjection.ForBuildAsync(db, project.Id, requestedRelease.Id, LegacyLadderPolicy.Instance, 100, CancellationToken.None);
        if (connection is NpgsqlConnection) await db.Database.ExecuteSqlRawAsync("ANALYZE");
        await Observe("root", async () =>
        {
            var result = await ChangeRequestTraceProjection.ForChangeRequestAsync(db, project.Id, root.Id, LegacyLadderPolicy.Instance, CancellationToken.None);
            Assert.NotNull(result);
            Assert.Equal(2, result.Nodes.Count);
            Assert.Single(result.Edges);
        });
        await Observe("build", async () =>
        {
            var result = await ChangeRequestTraceProjection.ForBuildAsync(db, project.Id, requestedRelease.Id, LegacyLadderPolicy.Instance, 100, CancellationToken.None);
            Assert.Equal(2, result.Nodes.Count);
            Assert.Single(result.Edges);
            Assert.False(result.Truncated);
        });
        SystemChangeRequest CreateCr(int i, Guid releaseId) => new($"SRCR-{i:00000}", 0, project.Id, releaseId,
            $"Change {i}", "Problem", "Analysis", "Solution", "author", now);
        TestChangeReview CreateTcr(SystemChangeRequest cr, int i) => new(project.Id, cr.TargetReleaseId, cr.Id,
            TestChangeReviewDiscipline.System, cr.DisplayNumber, now, baseNumber: $"SYSTPCR-{i:00000}", revision: 0);
        async Task Observe(string surface, Func<Task> read)
        {
            measurement.Reset();
            measurement.Enabled = true;
            var allocated = GC.GetTotalAllocatedBytes(true);
            var watch = Stopwatch.StartNew();
            await read();
            watch.Stop();
            allocated = GC.GetTotalAllocatedBytes(true) - allocated;
            measurement.Enabled = false;
            Assert.InRange(measurement.Rows, 0, 20);
            Assert.Equal(0, measurement.JsonBytes);
            output.WriteLine($"CQ10 {connection.GetType().Name} {surface}: unrelated={unrelated}; rows={measurement.Rows}; jsonBytes={measurement.JsonBytes}; commands={measurement.Statements.Count}; allocatedBytes={allocated}; elapsedMs={watch.Elapsed.TotalMilliseconds:F2}");
            if (connection is NpgsqlConnection postgres)
                foreach (var statement in measurement.Statements)
                {
                    await using var explain = new NpgsqlCommand("EXPLAIN (ANALYZE, BUFFERS, FORMAT JSON) " + statement.Sql, postgres);
                    foreach (var parameter in statement.Parameters)
                        explain.Parameters.AddWithValue(parameter.Name, parameter.Value ?? DBNull.Value);
                    output.WriteLine("SQL: " + statement.Sql);
                    var plan = (string)(await explain.ExecuteScalarAsync())!;
                    output.WriteLine("PLAN: " + plan);
                    if (unrelated >= 1000)
                    {
                        using var document = JsonDocument.Parse(plan);
                        AssertBoundedPlan(document.RootElement[0].GetProperty("Plan"));
                    }
                }
        }
    }
    private static void AssertBoundedPlan(JsonElement node)
    {
        if (node.TryGetProperty("Relation Name", out var relation)
            && relation.GetString() is "system_change_requests" or "test_change_reviews" or "review_cycles" or "frozen_review_trace_links")
        {
            var delivered = node.GetProperty("Actual Rows").GetDouble();
            var removed = node.TryGetProperty("Rows Removed by Filter", out var filter) ? filter.GetDouble() : 0;
            var loops = node.GetProperty("Actual Loops").GetDouble();
            Assert.True((delivered + removed) * loops < 1000,
                $"Fixed two-node graph scanned unrelated history in {relation.GetString()}: {node}");
        }
        if (node.TryGetProperty("Plans", out var children))
            foreach (var child in children.EnumerateArray()) AssertBoundedPlan(child);
    }
}

internal static class TraceTestProvider
{
    internal static DbContextOptionsBuilder<AeroLinkDbContext> ConfigureProvider(this DbContextOptionsBuilder<AeroLinkDbContext> options, DbConnection connection)
        => connection is NpgsqlConnection ? options.UseNpgsql(connection) : options.UseSqlite(connection);
}
