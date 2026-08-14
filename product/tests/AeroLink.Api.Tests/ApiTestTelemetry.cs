// Bounded, machine-readable startup-floor telemetry for #563 phase 1.
//
// The API test factory emits one JSON line per measured phase (host build with construction latency,
// disposal), attributed by test class and method from the construction call site. Lines are appended to
// AEROLINK_API_TELEMETRY_JSONL when set (CI only); without the variable the collector is a no-op and
// never floods normal console output. Per-test wall time comes from the TRX report; a separate
// aggregator combines both structured sources into the startup-floor report.

using System.Runtime.CompilerServices;
using System.Text.Json;

namespace AeroLink.Api.Tests;

internal static class ApiTestTelemetry
{
    private static readonly object Gate = new();
    private static string? _jsonlPath;
    private static bool _configured;
    private static string? _unavailableReason;
    private static long _written;
    private const long MaxLines = 50_000;

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

    internal static void ResetForTest()
    {
        lock (Gate)
        {
            _configured = false;
            _jsonlPath = null;
            _unavailableReason = null;
            _written = 0;
        }
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

    public static void RecordFactoryPhase(string phase, double constructionMs, double phaseMs, string callerFile, string callerMember, long factoryId)
    {
        Write(new
        {
            type = "factory",
            factoryId,
            @class = ClassName(callerFile),
            method = callerMember,
            phase,
            constructionMs = Math.Round(constructionMs, 3),
            ms = Math.Round(phaseMs, 3),
        });
    }

    private static void Write(object record)
    {
        lock (Gate)
        {
            if (!_configured)
            {
                _configured = true;
                ConfigureJsonlPath(Environment.GetEnvironmentVariable("AEROLINK_API_TELEMETRY_JSONL"));
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
