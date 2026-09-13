using System.Text.Json;

namespace AeroLink.Api;

public sealed record MainCurrencyStatus(string State, DateTimeOffset? CheckedAtUtc, string? RemoteSha);

public static class MainCurrency
{
    private sealed record Observation(string? SourceRoot, string? SourceSha, string? RemoteSha,
        DateTimeOffset CheckedAtUtc, bool Verified);

    // The existing production reconciler checks every 30 minutes. An old observation is not a claim
    // about current main, even if the source and runtime still match each other.
    public static MainCurrencyStatus? Read(IConfiguration configuration, AeroLinkRuntimeIdentity runtime,
        DateTimeOffset now)
    {
        if (runtime.Mode != "HOME-PRODUCTION" || runtime.InstanceClassification != "HomeCanonical") return null;
        var unknown = new MainCurrencyStatus("Unverified", null, null);
        var path = configuration["Runtime:MainCurrencyPath"];
        var sourceRoot = configuration["Runtime:SourceRoot"];
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(sourceRoot)) return unknown;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length > 4096) return unknown;
            var observation = JsonSerializer.Deserialize<Observation>(stream,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (observation is null || string.IsNullOrWhiteSpace(observation.SourceRoot)
                || !string.Equals(Path.GetFullPath(sourceRoot).TrimEnd('\\', '/'),
                    Path.GetFullPath(observation.SourceRoot).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase)
                || observation.CheckedAtUtc > now || observation.CheckedAtUtc == default) return unknown;
            var remoteSha = ValidSha(observation.RemoteSha) ? observation.RemoteSha : null;
            var state = "Unverified";
            if (observation.Verified && now - observation.CheckedAtUtc <= TimeSpan.FromMinutes(30)
                && remoteSha is not null && ValidSha(runtime.SourceSha)
                && runtime.SourceIdentity == runtime.SourceSha && observation.SourceSha == runtime.SourceSha)
            {
                state = runtime.SourceSha == remoteSha ? "Current" : "UpdateAvailable";
            }
            return new MainCurrencyStatus(state, observation.CheckedAtUtc, remoteSha);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or JsonException or ArgumentException or NotSupportedException)
        {
            return unknown;
        }
    }

    private static bool ValidSha(string? value) => value is { Length: 40 }
        && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
}
