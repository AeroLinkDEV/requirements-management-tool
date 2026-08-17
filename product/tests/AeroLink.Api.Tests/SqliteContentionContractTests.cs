using System.Diagnostics;
using Microsoft.Data.Sqlite;

namespace AeroLink.Api.Tests;

public sealed class SqliteContentionContractTests
{
    [Fact]
    public void File_backed_sqlite_contention_uses_the_provider_lock_retry_budget_without_a_custom_busy_handler()
    {
        var path = Path.Combine(Path.GetTempPath(), $"aerolink-sqlite-contention-{Guid.NewGuid():N}.db");
        try
        {
            var holderConnectionString = new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Pooling = false,
                DefaultTimeout = AeroLinkApiFactory.CommandTimeoutSeconds,
            }.ToString();
            using var holder = new SqliteConnection(holderConnectionString);
            holder.Open();
            using (var journalMode = holder.CreateCommand())
            {
                journalMode.CommandText = "PRAGMA journal_mode=WAL;";
                Assert.Equal("wal", journalMode.ExecuteScalar()?.ToString()?.ToLowerInvariant());
            }
            using (var createTable = holder.CreateCommand())
            {
                createTable.CommandText = "CREATE TABLE lock_probe (id INTEGER PRIMARY KEY);";
                createTable.ExecuteNonQuery();
            }

            using var holderTransaction = holder.BeginTransaction();
            using (var holderWrite = holder.CreateCommand())
            {
                holderWrite.Transaction = holderTransaction;
                holderWrite.CommandText = "INSERT INTO lock_probe DEFAULT VALUES;";
                holderWrite.ExecuteNonQuery();
            }

            var contenderConnectionString = new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Pooling = false,
                DefaultTimeout = 1,
            }.ToString();
            using var contender = new SqliteConnection(contenderConnectionString);
            contender.Open();
            Assert.Equal(1, contender.DefaultTimeout);

            using var busyTimeout = contender.CreateCommand();
            busyTimeout.CommandText = "PRAGMA busy_timeout;";
            Assert.Equal(0L, Convert.ToInt64(busyTimeout.ExecuteScalar()));

            using var contenderWrite = contender.CreateCommand();
            Assert.Equal(1, contenderWrite.CommandTimeout);
            contenderWrite.CommandText = "INSERT INTO lock_probe DEFAULT VALUES;";
            var stopwatch = Stopwatch.StartNew();
            var error = Assert.Throws<SqliteException>(() => contenderWrite.ExecuteNonQuery());
            stopwatch.Stop();
            Assert.True(stopwatch.Elapsed >= TimeSpan.FromMilliseconds(750),
                $"The provider returned SQLITE_BUSY too early after {stopwatch.Elapsed.TotalMilliseconds:F0} ms.");
            Assert.Equal(5, error.SqliteErrorCode);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            AeroLinkApiFactory.DeleteDatabaseArtifacts(path);
        }
    }
}
