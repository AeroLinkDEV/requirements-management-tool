using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AeroLink.Infrastructure.Diagnostics;

/// <summary>
/// Opt-in stall diagnostics, off unless configured (#939).
///
/// The browser journeys have twice recorded a request that reached its endpoint and then waited until the
/// client gave up, while other requests finished around it. The retained log says that it waited, never on
/// what. These settings make the process say more at the moment it happens. Only the browser harness sets
/// them; unset, nothing below is registered and a production process is unchanged.
/// </summary>
public static class StallDiagnosticsSettings
{
    public const string StallReportSecondsKey = "Diagnostics:StallReportSeconds";
    public const string SlowDatabaseMillisecondsKey = "Diagnostics:SlowDatabaseMilliseconds";

    public static TimeSpan? StallReportAfter(IConfiguration configuration) =>
        configuration.GetValue<int?>(StallReportSecondsKey) is int seconds and > 0 ? TimeSpan.FromSeconds(seconds) : null;

    public static TimeSpan? SlowDatabaseAfter(IConfiguration configuration) =>
        configuration.GetValue<int?>(SlowDatabaseMillisecondsKey) is int ms and > 0 ? TimeSpan.FromMilliseconds(ms) : null;
}

/// <summary>
/// What the current flow of work is doing, so a slow database operation can say whose it was: a request
/// names its method and path, and anything else (a background worker) reads as "background".
/// </summary>
public static class DiagnosticsWorkContext
{
    private static readonly AsyncLocal<string?> CurrentValue = new();
    public static string Current => CurrentValue.Value ?? "background";
    public static void Set(string? description) => CurrentValue.Value = description;
}

/// <summary>The requests this process is serving right now, and how long each has been at it.</summary>
public sealed class InFlightRequests
{
    private readonly ConcurrentDictionary<long, Entry> _entries = new();
    private long _next;

    public sealed class Entry(long id, string method, string path, long startedAt)
    {
        public long Id { get; } = id;
        public string Method { get; } = method;
        public string Path { get; } = path;
        public long StartedAt { get; } = startedAt;
        internal bool Reported { get; set; }
        public TimeSpan Elapsed(long now) => Stopwatch.GetElapsedTime(StartedAt, now);
    }

    public long Begin(string method, string path, long? startedAt = null)
    {
        var id = Interlocked.Increment(ref _next);
        _entries[id] = new Entry(id, method, path, startedAt ?? Stopwatch.GetTimestamp());
        return id;
    }

    public void End(long id) => _entries.TryRemove(id, out _);

    public IReadOnlyList<Entry> All() => [.. _entries.Values.OrderBy(entry => entry.StartedAt)];

    /// <summary>Requests past the threshold that have not been reported yet. Each is reported once.</summary>
    public IReadOnlyList<Entry> TakeNewlyStalled(TimeSpan threshold, long now)
    {
        var stalled = new List<Entry>();
        foreach (var entry in _entries.Values.OrderBy(entry => entry.StartedAt))
        {
            if (entry.Reported || entry.Elapsed(now) < threshold) continue;
            entry.Reported = true;
            stalled.Add(entry);
        }
        return stalled;
    }
}

/// <summary>
/// Reports a request that has been executing longer than the threshold, in a form the harness can act on.
///
/// It runs on a dedicated thread, deliberately not the thread pool. One explanation still open for the hang
/// is a starved pool, and a pool-driven timer would be starved with it and report nothing. The line carries
/// this process id, because the harness starts <c>dotnet run</c> and the server is a grandchild whose id only
/// the server knows. The harness captures every managed thread's stack when it sees the marker. The line also
/// carries the pool's own counters, so starvation shows up even where no stack can be taken.
/// </summary>
public sealed class StallWatchdog(InFlightRequests requests, TimeSpan threshold, ILogger<StallWatchdog> logger)
    : IHostedService, IDisposable
{
    public const string Marker = "AEROLINK-STALL";
    private readonly CancellationTokenSource _stop = new();
    private Thread? _thread;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _thread = new Thread(Run) { IsBackground = true, Name = "AeroLink stall watchdog" };
        _thread.Start();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _stop.Cancel();
        return Task.CompletedTask;
    }

    private void Run()
    {
        while (!_stop.Token.WaitHandle.WaitOne(TimeSpan.FromSeconds(1)))
        {
            try { ReportOnce(Stopwatch.GetTimestamp()); }
            catch (Exception ex) { logger.LogWarning(ex, "The stall watchdog could not complete a check."); }
        }
    }

    /// <summary>One check. Public so a test can drive it with a chosen clock instead of waiting.</summary>
    public int ReportOnce(long now)
    {
        var stalled = requests.TakeNewlyStalled(threshold, now);
        if (stalled.Count == 0) return 0;
        ThreadPool.GetAvailableThreads(out var availableWorkers, out var availableIo);
        ThreadPool.GetMaxThreads(out var maxWorkers, out _);
        var inFlight = requests.All();
        foreach (var entry in stalled)
            logger.LogWarning(
                "{Marker} pid={Pid} method={Method} path={Path} elapsedMs={ElapsedMs} inFlight={InFlight} "
                + "threadPoolThreads={PoolThreads} busyWorkers={BusyWorkers} availableIo={AvailableIo} pendingWorkItems={Pending}",
                Marker, Environment.ProcessId, entry.Method, entry.Path, (long)entry.Elapsed(now).TotalMilliseconds,
                inFlight.Count, ThreadPool.ThreadCount, maxWorkers - availableWorkers, availableIo,
                ThreadPool.PendingWorkItemCount);
        logger.LogWarning("{Marker}-INFLIGHT {Requests}", Marker, string.Join("; ", inFlight.Select(entry =>
            $"{entry.Method} {entry.Path} {(long)entry.Elapsed(now).TotalMilliseconds}ms")));
        return stalled.Count;
    }

    public void Dispose() => _stop.Dispose();
}
