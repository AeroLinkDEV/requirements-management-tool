// Bounded, machine-readable startup-floor telemetry for #563 phase 1 (schema
// "aerolink-api-telemetry/v2").
//
// The API test factory emits one JSON line per measured phase, attributed by test class and method from
// the construction call site. Phases are non-overlapping:
//   host  - constructionMs = factory construction to host-build start (captured BEFORE base.CreateHost);
//           ms = the host build itself.
//   dispose - ms = disposal; constructionMs is repeated for provenance and is never added again.
//   connectionOpen - ms = one SQLite connection open over the factory lifetime; informational only and
//           never added to the startup total (host build already contains startup connection opens).
// Lines are appended to AEROLINK_API_TELEMETRY_JSONL when set (CI only); without the variable the
// collector is a no-op and never floods normal console output. Per-test wall time comes from the TRX
// report; a separate aggregator combines both structured sources into the startup-floor report.
//
// Telemetry is best-effort by design: expected setup/write I/O failures are contained, disable further
// writes, and never change the authoritative test result. Tests can inject a per-factory write failure
// (InjectedFailures) that exercises the same containment without mutating the process-global path state
// used by the running suite.

using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace AeroLink.Api.Tests;

internal static class ApiTestTelemetry
{
    internal const string SchemaVersion = "aerolink-api-telemetry/v2";
    private static readonly object Gate = new();
    private static string? _jsonlPath;
    private static bool _configured;
    private static string? _unavailableReason;
    private static long _written;
    private const long MaxLines = 50_000;
    private static readonly ConcurrentDictionary<long, Func<Exception>> InjectedFailures = new();
    private static readonly ConcurrentDictionary<long, string> InjectedFailureReasons = new();

    public static string? UnavailableReason
    {
        get
        {
            lock (Gate)
            {
                return _unavailableReason;
            }
        }
    }

    internal static string? CurrentJsonlPath
    {
        get
        {
            lock (Gate)
            {
                return _jsonlPath;
            }
        }
    }

    internal static string? InjectedFailureReason(long factoryId)
    {
        return InjectedFailureReasons.TryGetValue(factoryId, out var reason) ? reason : null;
    }

    internal static void ResetForTest()
    {
        lock (Gate)
        {
            _configured = false;
            _jsonlPath = null;
            _unavailableReason = null;
            _written = 0;
            InjectedFailures.Clear();
            InjectedFailureReasons.Clear();
        }
    }

    internal static void InjectWriteFailure(long factoryId, Func<Exception> failure)
    {
        InjectedFailures[factoryId] = failure;
        InjectedFailureReasons.TryRemove(factoryId, out _);
    }

    internal static void ClearInjectedFailures(long factoryId)
    {
        InjectedFailures.TryRemove(factoryId, out _);
        InjectedFailureReasons.TryRemove(factoryId, out _);
    }

    public static void ConfigureJsonlPath(string? path)
    {
        lock (Gate)
        {
            try
            {
                _jsonlPath = string.IsNullOrWhiteSpace(path) ? null : path;
                if (_jsonlPath is not null)
                {
                    var directory = Path.GetDirectoryName(_jsonlPath);
                    if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
                }
            }
            catch (Exception problem) when (problem is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
            {
                _jsonlPath = null;
                _unavailableReason = problem.Message;
            }
        }
    }

    public static void RecordFactoryPhase(string phase, double constructionMs, double phaseMs, string callerFile, string callerMember, long factoryId, Action<object>? observer = null)
    {
        var record = new
        {
            schemaVersion = SchemaVersion,
            type = "factory",
            factoryId,
            @class = ClassName(callerFile),
            method = callerMember,
            phase,
            constructionMs = Math.Round(constructionMs, 3),
            ms = Math.Round(phaseMs, 3),
        };
        observer?.Invoke(record);
        Write(record, factoryId);
    }

    private static void Write(object record, long factoryId)
    {
        lock (Gate)
        {
            if (!_configured)
            {
                _configured = true;
                ConfigureJsonlPath(Environment.GetEnvironmentVariable("AEROLINK_API_TELEMETRY_JSONL"));
            }
            if (InjectedFailures.TryGetValue(factoryId, out var failure))
            {
                // Isolated test injection: exercise the containment path without mutating the
                // process-global path state that sibling factories in parallel tests rely on.
                try
                {
                    throw failure();
                }
                catch (Exception problem) when (problem is IOException or UnauthorizedAccessException or NotSupportedException)
                {
                    InjectedFailureReasons[factoryId] = problem.Message;
                }
                return;
            }
            if (_jsonlPath is null || _written >= MaxLines) return;
            _written += 1;
            try
            {
                File.AppendAllText(_jsonlPath, JsonSerializer.Serialize(record) + Environment.NewLine);
            }
            catch (Exception problem) when (problem is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                // Telemetry is best-effort by design: a locked or unwritable path must never change the
                // product-test result. Disable further writes and record the reason.
                _jsonlPath = null;
                _unavailableReason = problem.Message;
            }
        }
    }

    // Absolute source paths are never published: the aggregator's privacy contract accepts only the file
    // name, matching the JUnit path sanitization used elsewhere in CI metrics.
    private static string ClassName(string callerFile)
    {
        var name = Path.GetFileNameWithoutExtension(callerFile);
        return string.IsNullOrWhiteSpace(name) ? "unknown" : name;
    }
}
