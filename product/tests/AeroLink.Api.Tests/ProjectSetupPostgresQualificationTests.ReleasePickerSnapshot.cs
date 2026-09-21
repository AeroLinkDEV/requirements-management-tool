using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using AeroLink.Domain.Programs;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace AeroLink.Api.Tests;

// Required PostgreSQL qualification for the frozen Release picker membership (#1040). These tests run in
// the required setup-Postgres runner (filtered by FullyQualifiedName~ProjectSetupPostgresQualificationTests)
// and are never skipped when that runner executes. Raw connections drive the deterministic fence schedules;
// the API drives page-one/continuation so paging behavior is proven through the real endpoint.
public sealed partial class ProjectSetupPostgresQualificationTests
{
    private const string PrecedingMigrationId = "20260920114500_ProtectReleasedSyntheticSourceSupplement";

    private static async Task<Guid> SeedPickerProjectAsync(AeroLinkApiFactory factory, string code)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        var program = new ProgramRecord($"Picker program {code}", code);
        var project = new ProjectRecord(program.Id, $"Picker project {code}", "Software");
        var release = new SoftwareRelease(project.Id, "1.0", isReleased: true);
        db.AddRange(program, project, release);
        await db.SaveChangesAsync();
        return project.Id;
    }

    private static async Task<Guid> CreateReleaseViaApiAsync(HttpClient client, Guid projectId, string version)
    {
        using var response = await client.PostAsJsonAsync("/api/releases", new { projectId, version });
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("id").GetGuid();
    }

    private static async Task<JsonElement> LinkOptionsPageAsync(HttpClient client, Guid projectId, int pageSize, string? cursor = null)
    {
        var url = $"/api/managed-documents/link-options?projectId={projectId}&artifactType=Release&pageSize={pageSize}";
        if (cursor is not null) url += $"&cursor={Uri.EscapeDataString(cursor)}";
        using var response = await client.GetAsync(url);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static List<string> DisplayNumbers(JsonElement page)
        => page.GetProperty("items").EnumerateArray().Select(x => x.GetProperty("displayNumber").GetString()!).ToList();

    private static async Task WalkContinuationAsync(HttpClient client, Guid projectId, string cursor, List<string> into)
    {
        var walk = cursor;
        while (true)
        {
            var page = await LinkOptionsPageAsync(client, projectId, pageSize: 1, walk);
            into.AddRange(DisplayNumbers(page));
            if (!page.GetProperty("hasMore").GetBoolean()) break;
            walk = page.GetProperty("nextCursor").GetString()!;
        }
    }

    private static async Task InsertRawReleaseAsync(string connection, Guid projectId, string version)
    {
        await using var c = new NpgsqlConnection(connection);
        await c.OpenAsync();
        await using var command = c.CreateCommand();
        command.CommandText = "INSERT INTO software_releases (\"Id\", \"ProjectId\", \"Version\", \"IsReleased\") VALUES (@id, @project, @version, false)";
        command.Parameters.AddWithValue("id", Guid.NewGuid());
        command.Parameters.AddWithValue("project", projectId);
        command.Parameters.AddWithValue("version", version);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<string> ReadReleaseRowsAsync(string connection)
    {
        await using var verify = new NpgsqlConnection(connection);
        await verify.OpenAsync();
        await using var command = verify.CreateCommand();
        command.CommandText = """
            SELECT "Id", "ProjectId", "Version", "CanonicalIdentity", "PredecessorReleaseId", "IsReleased", "ReleasedAt"
            FROM software_releases ORDER BY "Id"
            """;
        await using var reader = await command.ExecuteReaderAsync();
        var rows = new StringBuilder();
        while (await reader.ReadAsync())
            rows.Append(string.Join('|', reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2),
                reader.IsDBNull(3) ? "<null>" : reader.GetString(3),
                reader.IsDBNull(4) ? "<null>" : reader.GetGuid(4).ToString(),
                reader.GetBoolean(5), reader.IsDBNull(6) ? "<null>" : reader.GetFieldValue<DateTimeOffset>(6).ToString("O")))
                .Append(';');
        return rows.ToString();
    }

    [RequiredSetupPostgresFact]
    public async Task Picker_upgrade_preserves_legacy_rows_and_freezes_continuations()
    {
        await WithDatabaseAsync(async connection =>
        {
            // Upgrade from the preceding main schema, where the membership column does not exist yet.
            await using (var preUpgrade = new AeroLinkDbContext(
                new DbContextOptionsBuilder<AeroLinkDbContext>().UseNpgsql(connection).Options))
            {
                await preUpgrade.Database.MigrateAsync(PrecedingMigrationId);
            }

            Guid projectId;
            using (var factory = new AeroLinkApiFactory(postgresConnection: connection))
            {
                using var scope = factory.Services.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
                var program = new ProgramRecord("Picker upgrade program", "PICKERUPG");
                var project = new ProjectRecord(program.Id, "Picker upgrade project", "Software");
                db.AddRange(program, project);
                await db.SaveChangesAsync();
                projectId = project.Id;
            }

            // Legacy seeds at the OLD schema via raw SQL: a null-canonical historical row and a predecessor
            // relationship that must survive the upgrade unchanged.
            Guid release10, release20;
            await using (var raw = new NpgsqlConnection(connection))
            {
                await raw.OpenAsync();
                release10 = Guid.NewGuid();
                release20 = Guid.NewGuid();
                await using var first = raw.CreateCommand();
                first.CommandText = "INSERT INTO software_releases (\"Id\", \"ProjectId\", \"Version\", \"IsReleased\") VALUES (@id, @p, '1.0', true)";
                first.Parameters.AddWithValue("id", release10);
                first.Parameters.AddWithValue("p", projectId);
                await first.ExecuteNonQueryAsync();
                await using var historical = raw.CreateCommand();
                historical.CommandText = "INSERT INTO software_releases (\"Id\", \"ProjectId\", \"Version\", \"IsReleased\") VALUES (@id, @p, '1.5', true)";
                historical.Parameters.AddWithValue("id", Guid.NewGuid());
                historical.Parameters.AddWithValue("p", projectId);
                await historical.ExecuteNonQueryAsync();
                await using var successor = raw.CreateCommand();
                successor.CommandText = "INSERT INTO software_releases (\"Id\", \"ProjectId\", \"Version\", \"IsReleased\", \"PredecessorReleaseId\") VALUES (@id, @p, '2.0', true, @pred)";
                successor.Parameters.AddWithValue("id", release20);
                successor.Parameters.AddWithValue("p", projectId);
                successor.Parameters.AddWithValue("pred", release10);
                await successor.ExecuteNonQueryAsync();
            }

            var before = await ReadReleaseRowsAsync(connection);

            // Apply the new migration; reapplication must be a no-op.
            await using (var upgrade = new AeroLinkDbContext(
                new DbContextOptionsBuilder<AeroLinkDbContext>().UseNpgsql(connection).Options))
            {
                await upgrade.Database.MigrateAsync();
                Assert.Empty(await upgrade.Database.GetPendingMigrationsAsync());
                await upgrade.Database.MigrateAsync();
                Assert.Empty(await upgrade.Database.GetPendingMigrationsAsync());
            }

            Assert.Equal(before, await ReadReleaseRowsAsync(connection));
            await using (var counts = new NpgsqlConnection(connection))
            {
                await counts.OpenAsync();
                await using var command = counts.CreateCommand();
                command.CommandText = "SELECT COUNT(*), COUNT(\"PickerInsertionOrdinal\") FROM software_releases";
                await using var reader = await command.ExecuteReaderAsync();
                await reader.ReadAsync();
                Assert.Equal(reader.GetInt32(0), reader.GetInt32(1)); // every row is the legacy cohort: ordinal NULL
            }

            // Legacy builds remain selectable in canonical order; the new build enters only a fresh traversal.
            using (var factory = new AeroLinkApiFactory(postgresConnection: connection))
            {
                var client = factory.CreateClient();
                await SecurityBoundaryTests.BootstrapAndLoginAdministratorAsync(client);
                var pageOne = await LinkOptionsPageAsync(client, projectId, pageSize: 1);
                Assert.Equal(["BUILD-1.0"], DisplayNumbers(pageOne));
                var continuation = new List<string> { "BUILD-1.0" };
                await WalkContinuationAsync(client, projectId, pageOne.GetProperty("nextCursor").GetString()!, continuation);
                Assert.Equal(["BUILD-1.0", "BUILD-1.5", "BUILD-2.0"], continuation);

                // A build committed after the boundary is invisible to the frozen traversal...
                await CreateReleaseViaApiAsync(client, projectId, "9.9");
                var refrozen = new List<string> { "BUILD-1.0" };
                await WalkContinuationAsync(client, projectId, pageOne.GetProperty("nextCursor").GetString()!, refrozen);
                Assert.Equal(["BUILD-1.0", "BUILD-1.5", "BUILD-2.0"], refrozen);

                // ...and a traversal started after the insert re-establishes the boundary and shows it.
                var fresh = await LinkOptionsPageAsync(client, projectId, pageSize: 50);
                Assert.Equal(["BUILD-1.0", "BUILD-1.5", "BUILD-2.0", "BUILD-9.9"], DisplayNumbers(fresh));
            }
        });
    }

    [RequiredSetupPostgresFact]
    public async Task Stale_snapshot_writers_cannot_enter_frozen_release_continuations()
    {
        foreach (var isolation in new[] { System.Data.IsolationLevel.RepeatableRead, System.Data.IsolationLevel.Serializable })
        {
            await WithDatabaseAsync(async connection =>
            {
                using var factory = new AeroLinkApiFactory(postgresConnection: connection);
                var client = factory.CreateClient();
                await SecurityBoundaryTests.BootstrapAndLoginAdministratorAsync(client);
                var projectId = await SeedPickerProjectAsync(factory,
                    $"PICKERSNAP{(isolation == System.Data.IsolationLevel.RepeatableRead ? "RR" : "SER")}");
                await CreateReleaseViaApiAsync(client, projectId, "1.5");   // in-work successor, ordinal 2

                // Writer A establishes an old MVCC snapshot (max committed ordinal = 2).
                await using var writerA = new NpgsqlConnection(connection);
                await writerA.OpenAsync();
                await using (var snapshot = writerA.CreateCommand())
                {
                    snapshot.CommandText = "SELECT COALESCE(MAX(\"PickerInsertionOrdinal\"), 0) FROM software_releases WHERE \"ProjectId\" = @p";
                    snapshot.Parameters.AddWithValue("p", projectId);
                    Assert.Equal(2L, await snapshot.ExecuteScalarAsync());
                }
                await using (var openSnapshot = writerA.CreateCommand())
                {
                    var level = isolation == System.Data.IsolationLevel.RepeatableRead ? "REPEATABLE READ" : "SERIALIZABLE";
                    openSnapshot.CommandText = $"BEGIN ISOLATION LEVEL {level}";
                    await openSnapshot.ExecuteNonQueryAsync();
                    openSnapshot.CommandText = "SELECT 1";
                    await openSnapshot.ExecuteScalarAsync();
                }

                // Writer B (Read Committed) commits another build; a page one taken now re-fences at the
                // advanced cutoff, while the earlier traversal keeps its original boundary.
                var early = await LinkOptionsPageAsync(client, projectId, pageSize: 1);
                Assert.Equal(["BUILD-1.0"], DisplayNumbers(early));
                Assert.True(early.GetProperty("hasMore").GetBoolean());
                await InsertRawReleaseAsync(connection, projectId, "2.0");
                var mid = await LinkOptionsPageAsync(client, projectId, pageSize: 1);
                Assert.Equal(["BUILD-1.0"], DisplayNumbers(mid));

                // Writer A's late INSERT allocates the global nextval (3), never a stale maximum.
                await using (var insert = writerA.CreateCommand())
                {
                    insert.CommandText = "INSERT INTO software_releases (\"Id\", \"ProjectId\", \"Version\", \"IsReleased\") VALUES (@id, @p, '9.9', false) RETURNING \"PickerInsertionOrdinal\"";
                    insert.Parameters.AddWithValue("id", Guid.NewGuid());
                    insert.Parameters.AddWithValue("p", projectId);
                    Assert.Equal(4L, await insert.ExecuteScalarAsync());
                }
                await using (var commit = writerA.CreateCommand())
                {
                    commit.CommandText = "COMMIT";
                    await commit.ExecuteNonQueryAsync();
                }

                // The early traversal (boundary before writer B) keeps its original members and excludes
                // both later builds (2.0 ordinal 3, 9.9 ordinal 4).
                var earlyWalked = new List<string> { "BUILD-1.0" };
                await WalkContinuationAsync(client, projectId, early.GetProperty("nextCursor").GetString()!, earlyWalked);
                Assert.Equal(["BUILD-1.0", "BUILD-1.5"], earlyWalked);

                // The mid traversal (boundary after B, before A) admits B and still excludes A's late build.
                var midWalked = new List<string> { "BUILD-1.0" };
                await WalkContinuationAsync(client, projectId, mid.GetProperty("nextCursor").GetString()!, midWalked);
                Assert.Equal(["BUILD-1.0", "BUILD-1.5", "BUILD-2.0"], midWalked);

                // A traversal started after the commits sees everything in canonical order.
                var fresh = await LinkOptionsPageAsync(client, projectId, pageSize: 50);
                Assert.Equal(["BUILD-1.0", "BUILD-1.5", "BUILD-2.0", "BUILD-9.9"], DisplayNumbers(fresh));
            });
        }
    }

    [RequiredSetupPostgresFact]
    public async Task Page_one_fence_waits_for_uncommitted_allocator_then_includes_the_committed_build()
    {
        await WithDatabaseAsync(async connection =>
        {
            using var factory = new AeroLinkApiFactory(postgresConnection: connection);
            var client = factory.CreateClient();
            await SecurityBoundaryTests.BootstrapAndLoginAdministratorAsync(client);
            var projectId = await SeedPickerProjectAsync(factory, "PICKERFENCE");

            // An uncommitted allocator holds the shared fence: the insert trigger acquires the advisory key.
            await using var writer = new NpgsqlConnection(connection);
            await writer.OpenAsync();
            await using (var begin = writer.CreateCommand())
            {
                begin.CommandText = "BEGIN";
                await begin.ExecuteNonQueryAsync();
            }
            await using (var insert = writer.CreateCommand())
            {
                insert.CommandText = "INSERT INTO software_releases (\"Id\", \"ProjectId\", \"Version\", \"IsReleased\") VALUES (@id, @p, '9.9', false)";
                insert.Parameters.AddWithValue("id", Guid.NewGuid());
                insert.Parameters.AddWithValue("p", projectId);
                await insert.ExecuteNonQueryAsync();
            }

            // Page one must block on the fence; observe the ungranted advisory lock at the backend.
            var pageOneTask = client.GetAsync($"/api/managed-documents/link-options?projectId={projectId}&artifactType=Release&pageSize=50");
            var observedWait = false;
            for (var attempt = 0; attempt < 100 && !observedWait; attempt++)
            {
                await Task.Delay(100);
                await using var probe = new NpgsqlConnection(connection);
                await probe.OpenAsync();
                await using var locks = probe.CreateCommand();
                locks.CommandText = "SELECT COUNT(*) FROM pg_locks WHERE locktype = 'advisory' AND NOT granted";
                observedWait = Convert.ToInt64(await locks.ExecuteScalarAsync()) > 0;
            }
            Assert.True(observedWait, "page one was never observed waiting on the project advisory fence");

            // The allocator commits; the waited fence captures the advanced cutoff and includes the build.
            await using (var commit = writer.CreateCommand()) { commit.CommandText = "COMMIT"; await commit.ExecuteNonQueryAsync(); }
            using var pageOne = await pageOneTask;
            Assert.True(pageOne.IsSuccessStatusCode, await pageOne.Content.ReadAsStringAsync());
            var page = await pageOne.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(["BUILD-1.0", "BUILD-9.9"], DisplayNumbers(page));

            await using var after = new NpgsqlConnection(connection);
            await after.OpenAsync();
            await using var remaining = after.CreateCommand();
            remaining.CommandText = "SELECT COUNT(*) FROM pg_locks WHERE locktype = 'advisory'";
            Assert.Equal(0L, await remaining.ExecuteScalarAsync());
        });
    }

    [RequiredSetupPostgresFact]
    public async Task Cancelled_page_one_releases_the_fence_without_stuck_writers()
    {
        await WithDatabaseAsync(async connection =>
        {
            using var factory = new AeroLinkApiFactory(postgresConnection: connection);
            var client = factory.CreateClient();
            await SecurityBoundaryTests.BootstrapAndLoginAdministratorAsync(client);
            var projectId = await SeedPickerProjectAsync(factory, "PICKERCANCEL");

            await using var writer = new NpgsqlConnection(connection);
            await writer.OpenAsync();
            await using (var begin = writer.CreateCommand())
            {
                begin.CommandText = "BEGIN";
                await begin.ExecuteNonQueryAsync();
            }
            await using (var insert = writer.CreateCommand())
            {
                insert.CommandText = "INSERT INTO software_releases (\"Id\", \"ProjectId\", \"Version\", \"IsReleased\") VALUES (@id, @p, '9.9', false)";
                insert.Parameters.AddWithValue("id", Guid.NewGuid());
                insert.Parameters.AddWithValue("p", projectId);
                await insert.ExecuteNonQueryAsync();
            }

            using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(800));
            var cancelledRequest = Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => client.GetAsync($"/api/managed-documents/link-options?projectId={projectId}&artifactType=Release&pageSize=50", cancellation.Token));
            await cancelledRequest.WaitAsync(TimeSpan.FromSeconds(20));

            // The canceled reader leaves no lock behind; the writer proceeds and no membership leaked.
            await using (var rollback = writer.CreateCommand()) { rollback.CommandText = "ROLLBACK"; await rollback.ExecuteNonQueryAsync(); }
            await using var check = new NpgsqlConnection(connection);
            await check.OpenAsync();
            await using var locks = check.CreateCommand();
            locks.CommandText = "SELECT COUNT(*) FROM pg_locks WHERE locktype = 'advisory'";
            Assert.Equal(0L, await locks.ExecuteScalarAsync());
            await using var leaked = check.CreateCommand();
            leaked.CommandText = "SELECT COUNT(*) FROM software_releases WHERE \"Version\" = '9.9'";
            Assert.Equal(0L, await leaked.ExecuteScalarAsync());

            var fresh = await LinkOptionsPageAsync(client, projectId, pageSize: 50);
            Assert.Equal(["BUILD-1.0"], DisplayNumbers(fresh));
        });
    }
}
