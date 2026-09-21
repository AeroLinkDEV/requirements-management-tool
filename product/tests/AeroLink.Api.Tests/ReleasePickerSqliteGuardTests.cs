using AeroLink.Domain.Programs;
using AeroLink.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace AeroLink.Api.Tests;

// SQLite guard installer (#1040): first-install classification and trigger creation are atomic; later
// startups never reclassify rows. PostgreSQL receives its guards through the migration and is qualified
// in the required setup-Postgres runner.
public sealed class ReleasePickerSqliteGuardTests : IDisposable
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"picker-guard-{Guid.NewGuid():N}.db");

    private AeroLinkDbContext CreateContext() => new(
        new DbContextOptionsBuilder<AeroLinkDbContext>().UseSqlite($"Data Source={_databasePath}").Options);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (File.Exists(_databasePath)) File.Delete(_databasePath);
    }

    private async Task InsertRawReleaseAsync(Guid projectId, string version)
    {
        await using var db = CreateContext();
        await db.Database.OpenConnectionAsync();
        var connection = (SqliteConnection)db.Database.GetDbConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO \"software_releases\" (\"Id\", \"ProjectId\", \"Version\", \"IsReleased\") VALUES ($id, $p, $v, 0)";
        command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString());
        command.Parameters.AddWithValue("$p", projectId.ToString());
        command.Parameters.AddWithValue("$v", version);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<string> ScalarTextAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (string)(await command.ExecuteScalarAsync())!;
    }

    [Fact]
    public async Task Installed_guards_reject_the_full_forbidden_membership_matrix()
    {
        var projectId = Guid.NewGuid();
        await using (var setup = CreateContext())
        {
            await setup.Database.EnsureCreatedAsync();
            await InsertRawReleaseAsync(projectId, "1.0");   // allocated by the first install? No: this row
                                                             // predates the guard, so it becomes the legacy row.
        }

        await using (var db = CreateContext())
        {
            await ReleasePickerSqliteGuard.EnsureInstalledAsync(db);
            var allocated = new SoftwareRelease(projectId, "1.5", false);
            db.Add(allocated);
            await db.SaveChangesAsync();
            var allocatedOrdinal = allocated.PickerInsertionOrdinal!.Value;

            var connection = (SqliteConnection)db.Database.GetDbConnection();
            if (connection.State != System.Data.ConnectionState.Open) await connection.OpenAsync();
            var legacyId = await ScalarTextAsync(connection, "SELECT CAST(\"Id\" AS TEXT) FROM \"software_releases\" WHERE \"Version\" = '1.0'");
            var allocatedId = await ScalarTextAsync(connection, "SELECT CAST(\"Id\" AS TEXT) FROM \"software_releases\" WHERE \"Version\" = '1.5'");
            await using var ordinalCheck = connection.CreateCommand();
            ordinalCheck.CommandText = $"SELECT \"PickerInsertionOrdinal\" FROM \"software_releases\" WHERE \"Id\" = '{allocatedId}'";
            Assert.Equal(allocatedOrdinal, await ordinalCheck.ExecuteScalarAsync());

            async Task AssertRejectedAsync(string sql)
            {
                await using var command = connection.CreateCommand();
                command.CommandText = sql;
                var rejected = false;
                try { await command.ExecuteNonQueryAsync(); }
                catch (SqliteException) { rejected = true; }
                Assert.True(rejected, "expected the installed guard to reject: " + sql);
            }

            await AssertRejectedAsync($"INSERT INTO \"software_releases\" (\"Id\", \"ProjectId\", \"Version\", \"IsReleased\", \"PickerInsertionOrdinal\") VALUES ('{Guid.NewGuid()}', '{projectId}', '7.7', 0, 42)");
            await AssertRejectedAsync($"INSERT INTO \"software_releases\" (\"Id\", \"ProjectId\", \"Version\", \"IsReleased\", \"PickerLegacyCohort\") VALUES ('{Guid.NewGuid()}', '{projectId}', '7.8', 0, 1)");
            await AssertRejectedAsync($"UPDATE \"software_releases\" SET \"PickerInsertionOrdinal\" = 999 WHERE \"Id\" = '{legacyId}'");
            await AssertRejectedAsync($"UPDATE \"software_releases\" SET \"PickerInsertionOrdinal\" = {allocatedOrdinal + 1} WHERE \"Id\" = '{allocatedId}'");
            await AssertRejectedAsync($"UPDATE \"software_releases\" SET \"PickerInsertionOrdinal\" = NULL WHERE \"Id\" = '{allocatedId}'");
            await AssertRejectedAsync($"UPDATE \"software_releases\" SET \"PickerLegacyCohort\" = 0 WHERE \"Id\" = '{legacyId}'");
            await AssertRejectedAsync($"UPDATE \"software_releases\" SET \"PickerLegacyCohort\" = 1 WHERE \"Id\" = '{allocatedId}'");

            // Ordinary lifecycle mutation remains valid on both rows.
            await using var allowed = connection.CreateCommand();
            allowed.CommandText = $"UPDATE \"software_releases\" SET \"Version\" = '1.0 (lifecycle)' WHERE \"Id\" = '{legacyId}'";
            await allowed.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    public async Task First_install_classifies_existing_rows_as_legacy_and_installs_triggers()
    {
        var projectId = Guid.NewGuid();
        await using (var setup = CreateContext())
        {
            await setup.Database.EnsureCreatedAsync();
            await InsertRawReleaseAsync(projectId, "1.0");
            await InsertRawReleaseAsync(projectId, "1.5");
        }

        await using (var db = CreateContext())
        {
            await ReleasePickerSqliteGuard.EnsureInstalledAsync(db);

            var connection = (SqliteConnection)db.Database.GetDbConnection();
            if (connection.State != System.Data.ConnectionState.Open) await connection.OpenAsync();
            long flagColumn = 0, triggers = 0, legacy = 0;
            await using (var check = connection.CreateCommand())
            {
                check.CommandText = "SELECT COUNT(*) FROM pragma_table_info('software_releases') WHERE name = 'PickerLegacyCohort'";
                flagColumn = (long)(await check.ExecuteScalarAsync())!;
                check.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'trigger' AND name LIKE 'aerolink_release_picker%'";
                triggers = (long)(await check.ExecuteScalarAsync())!;
                check.CommandText = "SELECT COUNT(*) FROM \"software_releases\" WHERE \"PickerLegacyCohort\" = 1 AND \"PickerInsertionOrdinal\" IS NULL";
                legacy = (long)(await check.ExecuteScalarAsync())!;
            }
            Assert.Equal(1, flagColumn);
            Assert.Equal(3, triggers);
            Assert.Equal(2, legacy);

            // A new insert through EF receives a database-allocated ordinal as a non-legacy row.
            var release = new SoftwareRelease(projectId, "2.0", false);
            db.Add(release);
            await db.SaveChangesAsync();
            Assert.Equal(1, release.PickerInsertionOrdinal);
        }
    }

    [Fact]
    public async Task Restart_install_does_not_reclassify_and_remains_idempotent()
    {
        var projectId = Guid.NewGuid();
        await using (var setup = CreateContext())
        {
            await setup.Database.EnsureCreatedAsync();
            await InsertRawReleaseAsync(projectId, "1.0");
        }

        await using (var db = CreateContext())
        {
            await ReleasePickerSqliteGuard.EnsureInstalledAsync(db);
            var release = new SoftwareRelease(projectId, "2.0", false);
            db.Add(release);
            await db.SaveChangesAsync();
            await ReleasePickerSqliteGuard.EnsureInstalledAsync(db);   // second startup

            var connection = (SqliteConnection)db.Database.GetDbConnection();
            if (connection.State != System.Data.ConnectionState.Open) await connection.OpenAsync();
            await using var check = connection.CreateCommand();
            check.CommandText = "SELECT \"Version\", \"PickerLegacyCohort\", \"PickerInsertionOrdinal\" FROM \"software_releases\" ORDER BY \"Version\"";
            await using var reader = await check.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal("1.0", reader.GetString(0));
            Assert.Equal(1L, reader.GetInt64(1));
            Assert.True(reader.IsDBNull(2));
            Assert.True(await reader.ReadAsync());
            Assert.Equal("2.0", reader.GetString(0));
            Assert.Equal(0L, reader.GetInt64(1));
            Assert.Equal(1L, reader.GetInt64(2));
            Assert.False(await reader.ReadAsync());
        }
    }

    [Fact]
    public async Task Failed_install_rolls_back_the_whole_first_installation()
    {
        var projectId = Guid.NewGuid();
        await using (var setup = CreateContext())
        {
            await setup.Database.EnsureCreatedAsync();
            await InsertRawReleaseAsync(projectId, "1.0");
            // A hostile update trigger aborts the installer's classification UPDATE after the column was
            // added, forcing a genuine mid-transaction failure.
            await setup.Database.OpenConnectionAsync();
            var connection = (SqliteConnection)setup.Database.GetDbConnection();
            await using var hostile = connection.CreateCommand();
            hostile.CommandText = "CREATE TRIGGER aerolink_hostile_upd BEFORE UPDATE ON \"software_releases\" FOR EACH ROW BEGIN SELECT RAISE(ABORT, 'hostile'); END";
            await hostile.ExecuteNonQueryAsync();
        }

        await using (var db = CreateContext())
        {
            await Assert.ThrowsAnyAsync<SqliteException>(() => ReleasePickerSqliteGuard.EnsureInstalledAsync(db));

            var connection = (SqliteConnection)db.Database.GetDbConnection();
            if (connection.State != System.Data.ConnectionState.Open) await connection.OpenAsync();
            await using var check = connection.CreateCommand();
            check.CommandText = "SELECT COUNT(*) FROM pragma_table_info('software_releases') WHERE name = 'PickerLegacyCohort'";
            Assert.Equal(0L, await check.ExecuteScalarAsync());          // the ADD COLUMN was rolled back
            check.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE name LIKE 'aerolink_release_picker%'";
            Assert.Equal(0L, await check.ExecuteScalarAsync());          // no partial trigger artifacts
        }

        // The installer can complete cleanly once the hostile trigger is gone.
        await using (var repair = CreateContext())
        {
            await repair.Database.OpenConnectionAsync();
            var connection = (SqliteConnection)repair.Database.GetDbConnection();
            await using var drop = connection.CreateCommand();
            drop.CommandText = "DROP TRIGGER aerolink_hostile_upd";
            await drop.ExecuteNonQueryAsync();
        }
        await using (var db = CreateContext())
        {
            await ReleasePickerSqliteGuard.EnsureInstalledAsync(db);
            var connection = (SqliteConnection)db.Database.GetDbConnection();
            if (connection.State != System.Data.ConnectionState.Open) await connection.OpenAsync();
            await using var check = connection.CreateCommand();
            check.CommandText = "SELECT COUNT(*) FROM pragma_table_info('software_releases') WHERE name = 'PickerLegacyCohort'";
            Assert.Equal(1L, await check.ExecuteScalarAsync());
        }
    }
}
