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
