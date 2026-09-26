using Microsoft.Data.Sqlite;

namespace AeroLink.Infrastructure.Diagnostics;

/// <summary>
/// What the SQLite file itself says at the moment a request stalls (#1163).
///
/// The browser host's stalled login was inside native <c>sqlite3_step</c> for a <c>COMMIT</c> while no other
/// thread in the process was in SQLite, and every waiter was released in the same millisecond. That fits a
/// commit waiting for a lock held outside any running request, such as an idle pooled connection still holding
/// an old read snapshot while the writer tries to restart the WAL, or for the disk. The request log cannot say
/// which. This asks SQLite, from a separate unpooled connection that never waits:
///
/// - the sizes of the database and its WAL;
/// - a PASSIVE checkpoint's answer. <c>busy=1</c> means another checkpointer or an exclusive lock. A
///   <c>checkpointedFrames</c> well below <c>walFrames</c> means a reader is pinning an old snapshot. An
///   error means the file could not even be opened or locked without waiting.
///
/// A PASSIVE checkpoint is what SQLite already runs after every large enough commit, so asking it changes
/// nothing a commit would not. The probe runs on its own thread with a budget, so an I/O-bound file shows up
/// as "did not return" instead of hanging the watchdog that reports the stall.
/// </summary>
public static class SqliteStallProbe
{
    public const string Marker = "AEROLINK-SQLITE";

    public static string Describe(string connectionString, TimeSpan budget)
    {
        var builder = new SqliteConnectionStringBuilder(connectionString)
        {
            Pooling = false,
            Mode = SqliteOpenMode.ReadWrite,
            DefaultTimeout = 1,
        };
        var path = builder.DataSource;
        if (string.IsNullOrWhiteSpace(path) || path == ":memory:") return "file=none (in-memory database)";

        // Everything that touches the file runs inside the budget. The first recurrence (#1163, run 36214468229)
        // showed why: a plain File.Exists on the database waited ~28 s while the stalled commit ran, and it held
        // the watchdog thread with it. The stage that did not return is now the finding, and statMs says how long
        // the operating system took to answer a metadata question about the file.
        var stage = "stat";
        string? files = null;
        string? outcome = null;
        var probe = new Thread(() =>
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var sizes = $"dbBytes={Size(path)} walBytes={Size(path + "-wal")}";
            files = $"{sizes} statMs={clock.ElapsedMilliseconds}";
            stage = "checkpoint";
            outcome = Checkpoint(builder.ToString());
        })
        {
            IsBackground = true,
            Name = "AeroLink SQLite stall probe",
        };
        probe.Start();
        return probe.Join(budget)
            ? $"{files} {outcome}"
            : $"{files ?? "dbBytes=? walBytes=?"} {stage}=did-not-return-within-{(long)budget.TotalMilliseconds}ms";
    }

    private static string Checkpoint(string connectionString)
    {
        try
        {
            using var connection = new SqliteConnection(connectionString);
            connection.Open();
            using (var noWait = connection.CreateCommand())
            {
                noWait.CommandText = "PRAGMA busy_timeout = 0";
                noWait.ExecuteNonQuery();
            }
            using var checkpoint = connection.CreateCommand();
            checkpoint.CommandText = "PRAGMA wal_checkpoint(PASSIVE)";
            using var reader = checkpoint.ExecuteReader();
            reader.Read();
            return $"checkpoint busy={reader.GetInt64(0)} walFrames={reader.GetInt64(1)} checkpointedFrames={reader.GetInt64(2)}";
        }
        catch (SqliteException ex)
        {
            return $"checkpoint=error sqliteCode={ex.SqliteErrorCode} extended={ex.SqliteExtendedErrorCode}";
        }
        catch (Exception ex)
        {
            return $"checkpoint=error {ex.GetType().Name}";
        }
    }

    private static string Size(string file)
    {
        try { return File.Exists(file) ? new FileInfo(file).Length.ToString() : "absent"; }
        catch (IOException) { return "unreadable"; }
        catch (UnauthorizedAccessException) { return "unreadable"; }
    }
}
