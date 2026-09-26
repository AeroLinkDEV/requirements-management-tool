using System.Diagnostics;
using AeroLink.Infrastructure.Diagnostics;
using AeroLink.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace AeroLink.Infrastructure.Tests;

/// <summary>
/// #939: the opt-in diagnostics the browser harness turns on so the next stalled request says what it was
/// waiting on. They must report what they exist to report, and stay off unless asked.
/// </summary>
public sealed class StallDiagnosticsTests
{
    [Theory]
    [InlineData(null, null)]
    [InlineData("0", null)]
    [InlineData("10", 10)]
    public void Settings_are_off_unless_a_positive_value_is_given(string? configured, int? expectedSeconds)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [StallDiagnosticsSettings.StallReportSecondsKey] = configured,
            [StallDiagnosticsSettings.SlowDatabaseMillisecondsKey] = configured,
        }).Build();

        Assert.Equal(expectedSeconds is null ? null : TimeSpan.FromSeconds(expectedSeconds.Value),
            StallDiagnosticsSettings.StallReportAfter(configuration));
        Assert.Equal(expectedSeconds is null ? null : TimeSpan.FromMilliseconds(expectedSeconds.Value),
            StallDiagnosticsSettings.SlowDatabaseAfter(configuration));
    }

    [Fact]
    public void A_request_past_the_threshold_is_reported_once_with_this_process_id_and_the_pool_counters()
    {
        var requests = new InFlightRequests();
        var logger = new CapturingLogger<StallWatchdog>();
        using var watchdog = new StallWatchdog(requests, TimeSpan.FromSeconds(10), logger);
        var now = Stopwatch.GetTimestamp();
        var stalled = requests.Begin("POST", "/api/auth/login", now - 15 * Stopwatch.Frequency);
        requests.Begin("GET", "/api/workspaces", now - 2 * Stopwatch.Frequency);

        Assert.Equal(1, watchdog.ReportOnce(now));

        var report = Assert.Single(logger.Messages, message => message.Contains($"{StallWatchdog.Marker} pid="));
        // The harness reads the pid from this line: `dotnet run` makes the server a process it never sees.
        Assert.Contains($"pid={Environment.ProcessId} ", report);
        Assert.Contains("method=POST path=/api/auth/login", report);
        Assert.Contains("threadPoolThreads=", report);
        Assert.Contains("pendingWorkItems=", report);
        var inFlight = Assert.Single(logger.Messages, message => message.Contains($"{StallWatchdog.Marker}-INFLIGHT"));
        Assert.Contains("GET /api/workspaces", inFlight);

        // Once per stall: the harness takes one stack capture for it, not one per second it keeps waiting.
        Assert.Equal(0, watchdog.ReportOnce(now + Stopwatch.Frequency));
        requests.End(stalled);
        Assert.DoesNotContain(requests.All(), entry => entry.Path == "/api/auth/login");
    }

    [Fact]
    public async Task A_long_transaction_names_its_owner_and_first_statement()
    {
        var logger = new CapturingLogger<SlowDatabaseInterceptor>();
        var path = Path.Combine(Path.GetTempPath(), $"aerolink-slowdb-{Guid.NewGuid():N}.db");
        try
        {
            var options = new DbContextOptionsBuilder<AeroLinkDbContext>()
                .UseSqlite($"Data Source={path};Pooling=False")
                .AddInterceptors(new SlowDatabaseInterceptor(TimeSpan.FromMilliseconds(200), logger)).Options;
            await using (var create = new AeroLinkDbContext(options)) await create.Database.EnsureCreatedAsync();
            logger.Messages.Clear();

            DiagnosticsWorkContext.Set("POST /api/auth/login");
            await using (var db = new AeroLinkDbContext(options))
            {
                await using var transaction = await db.Database.BeginTransactionAsync();
                db.SecurityAuditEvents.Add(new("Login", "admin", "session", "Success", "Session created.", "127.0.0.1", DateTimeOffset.UtcNow));
                await db.SaveChangesAsync();
                await Task.Delay(400); // held open past the threshold, as the unknown holder in #939 would be
                await transaction.CommitAsync();
            }

            var held = Assert.Single(logger.Messages, message => message.Contains("AEROLINK-SLOWDB transaction committed"));
            Assert.Contains("owner=POST /api/auth/login", held);
            Assert.Contains("first=INSERT INTO", held);
            // Fast commands under the threshold stay out of the transcript.
            Assert.DoesNotContain(logger.Messages, message => message.Contains("AEROLINK-SLOWDB command"));
        }
        finally
        {
            DiagnosticsWorkContext.Set(null);
            SqliteConnection.ClearAllPools();
            foreach (var file in new[] { path, path + "-wal", path + "-shm" })
                try { File.Delete(file); } catch (IOException) { }
        }
    }

    [Fact]
    public void A_stall_report_names_the_WAL_frames_a_pinned_reader_keeps_the_checkpoint_from_reaching()
    {
        // #1163: the stalled COMMIT had no contender inside any running request. A reader holding an old
        // snapshot on an otherwise idle connection is one candidate the report must be able to show.
        var path = Path.Combine(Path.GetTempPath(), $"aerolink-stallprobe-{Guid.NewGuid():N}.db");
        try
        {
            using var writer = Open(path);
            Execute(writer, "PRAGMA journal_mode = WAL; CREATE TABLE t (x INTEGER); INSERT INTO t VALUES (1);");
            using var reader = Open(path);
            Execute(reader, "BEGIN; SELECT count(*) FROM t;");
            Execute(writer, "INSERT INTO t SELECT x FROM t; INSERT INTO t SELECT x FROM t; INSERT INTO t VALUES (2);");

            var requests = new InFlightRequests();
            var logger = new CapturingLogger<StallWatchdog>();
            using var watchdog = new StallWatchdog(requests, TimeSpan.FromSeconds(10), logger,
                () => SqliteStallProbe.Describe($"Data Source={path}", TimeSpan.FromSeconds(3)));
            var now = Stopwatch.GetTimestamp();
            requests.Begin("POST", "/api/auth/login", now - 15 * Stopwatch.Frequency);
            watchdog.ReportOnce(now);

            var pinned = Assert.Single(logger.Messages, message => message.StartsWith(SqliteStallProbe.Marker + " "));
            Assert.Contains("checkpoint busy=0", pinned);
            Assert.Matches(@"walBytes=\d+", pinned);
            var (frames, checkpointed) = Frames(pinned);
            Assert.True(checkpointed < frames, pinned);

            Execute(reader, "COMMIT;");
            var released = SqliteStallProbe.Describe($"Data Source={path}", TimeSpan.FromSeconds(3));
            var (framesAfter, checkpointedAfter) = Frames(released);
            Assert.Equal(framesAfter, checkpointedAfter);
        }
        finally { Delete(path); }
    }

    [Fact]
    public void The_stall_probe_answers_at_once_when_the_file_is_locked_instead_of_waiting()
    {
        // The probe runs on the watchdog's dedicated thread. If it waited out Microsoft.Data.Sqlite's default
        // 30 s busy retry, the one thread that reports stalls would itself stall.
        var path = Path.Combine(Path.GetTempPath(), $"aerolink-stallprobe-{Guid.NewGuid():N}.db");
        try
        {
            using var holder = Open(path);
            Execute(holder, "CREATE TABLE t (x INTEGER); BEGIN EXCLUSIVE; INSERT INTO t VALUES (1);");

            var clock = Stopwatch.StartNew();
            var report = SqliteStallProbe.Describe($"Data Source={path}", TimeSpan.FromSeconds(10));

            Assert.Contains("checkpoint=error sqliteCode=5", report);
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5), $"{clock.Elapsed}: {report}");
            Execute(holder, "ROLLBACK;");
        }
        finally { Delete(path); }
    }

    private static SqliteConnection Open(string path)
    {
        var connection = new SqliteConnection($"Data Source={path};Pooling=False");
        connection.Open();
        return connection;
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        while (reader.NextResult()) { }
    }

    private static (long Frames, long Checkpointed) Frames(string report)
    {
        var match = System.Text.RegularExpressions.Regex.Match(report, @"walFrames=(\d+) checkpointedFrames=(\d+)");
        Assert.True(match.Success, report);
        return (long.Parse(match.Groups[1].Value), long.Parse(match.Groups[2].Value));
    }

    private static void Delete(string path)
    {
        SqliteConnection.ClearAllPools();
        foreach (var file in new[] { path, path + "-wal", path + "-shm" })
            try { File.Delete(file); } catch (IOException) { }
    }

    [Fact]
    public void Work_outside_a_request_is_attributed_to_the_background()
    {
        DiagnosticsWorkContext.Set(null);
        Assert.Equal("background", DiagnosticsWorkContext.Current);
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (Messages) Messages.Add(formatter(state, exception));
        }
    }
}
