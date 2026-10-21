using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.Extensions.Logging.Abstractions;

namespace AeroLink.Api;

/// <summary>
/// Observes the repository selected by the framework when KeyManagementOptions.XmlRepository is null.
/// The structured .NET 10 selection event is evidence; a reconstructed profile path is not.
/// </summary>
public sealed class DataProtectionRepositoryCapture : ILoggerProvider
{
    public const string Category = "Microsoft.AspNetCore.DataProtection.KeyManagement.XmlKeyManager";
    private readonly object gate = new();
    private DataProtectionRepositoryObservation? observation;
    private bool refused;

    public ILogger CreateLogger(string categoryName) => categoryName == Category
        ? new SelectionLogger(this) : NullLogger.Instance;

    public void Dispose() { }

    public DataProtectionRepositoryObservation? Read()
    {
        lock (gate) return refused ? null : observation;
    }

    private void Observe<TState>(LogLevel level, EventId eventId, TState state, Exception? exception)
    {
        // These are public structured events from XmlKeyManager in ASP.NET Core 10. No message formatting,
        // exception text, key payload or unrelated log state is retained. Unknown schemas fail closed.
        var expected = eventId.Id switch
        {
            62 => ("UsingProfileAsKeyRepository", "User profile is available. Using '{FullName}' as key repository; keys will not be encrypted at rest.", "Unprotected"),
            63 => ("UsingProfileAsKeyRepositoryWithDPAPI", "User profile is available. Using '{FullName}' as key repository and Windows DPAPI to encrypt keys at rest.", "WindowsDpapi"),
            64 => ("UsingAzureAsKeyRepository", "Azure Web Sites environment detected. Using '{FullName}' as key repository; keys will not be encrypted at rest.", "Unprotected"),
            _ => default,
        };
        if (expected.Item1 is null && eventId.Id is not (59 or 61)
            && !(eventId.Name?.Contains("KeyRepository", StringComparison.Ordinal) ?? false)) return;
        lock (gate)
        {
            if (refused) return;
            if (typeof(XmlKeyManager).Assembly.GetName().Version?.Major != 10
                || level != LogLevel.Information || exception is not null
                || expected.Item1 is null || eventId.Name != expected.Item1
                || state is not IEnumerable<KeyValuePair<string, object?>> values)
            {
                refused = true;
                return;
            }
            var fields = values.ToArray();
            var paths = fields.Where(x => x.Key == "FullName").ToArray();
            var formats = fields.Where(x => x.Key == "{OriginalFormat}").ToArray();
            if (paths.Length != 1 || paths[0].Value is not string path
                || formats.Length != 1 || formats[0].Value is not string format || format != expected.Item2
                || string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            {
                refused = true;
                return;
            }
            try { path = Path.GetFullPath(path); }
            catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
            {
                refused = true;
                return;
            }
            var next = new DataProtectionRepositoryObservation(eventId.Id, expected.Item1, path, expected.Item3);
            if (observation is not null && observation != next) refused = true;
            else observation = next;
        }
    }

    private sealed class SelectionLogger(DataProtectionRepositoryCapture owner) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information && logLevel < LogLevel.None;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => owner.Observe(logLevel, eventId, state, exception);
    }
}

public sealed record DataProtectionRepositoryObservation(int EventId, string EventName, string Directory,
    string ProviderSelection)
{
    public string Provenance => $"framework-fallback-event:{EventId}/{EventName}";
    // Selection describes the framework's write encryptor, not the scope of every persisted descriptor.
    // Event 63 cannot distinguish CurrentUser from LocalMachine; 62/64 explicitly select unprotected storage.
    public string ProtectionScopeEvidence => ProviderSelection == "Unprotected" ? "Unprotected" : "unknown";

    public bool HasExistingOrdinaryDirectory()
    {
        try
        {
            if (!System.IO.Directory.Exists(Directory)) return false;
            for (var item = new DirectoryInfo(Directory); item is not null; item = item.Parent)
                if ((item.Attributes & FileAttributes.ReparsePoint) != 0) return false;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }
}
