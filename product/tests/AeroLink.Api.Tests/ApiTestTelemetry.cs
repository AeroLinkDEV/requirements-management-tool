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
    private static long _written;
    private const long MaxLines = 50_000;

    public static void ConfigureJsonlPath(string? path)
    {
        lock (Gate)
        {
            _jsonlPath = string.IsNullOrWhiteSpace(path) ? null : path;
            if (_jsonlPath is not null)
            {
                var directory = Path.GetDirectoryName(_jsonlPath);
                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
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
            File.AppendAllText(_jsonlPath, JsonSerializer.Serialize(record) + Environment.NewLine);
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
