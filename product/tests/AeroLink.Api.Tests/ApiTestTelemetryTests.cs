using System.Net.Http.Json;
using Microsoft.Data.Sqlite;

namespace AeroLink.Api.Tests;

public sealed class ApiTestTelemetryTests
{
    [Fact]
    public async Task An_unwritable_telemetry_path_never_fails_the_authoritative_test()
    {
        ApiTestTelemetry.ResetForTest();
        var previous = Environment.GetEnvironmentVariable("AEROLINK_API_TELEMETRY_JSONL");
        // A path whose directory is an existing file makes Directory.CreateDirectory throw.
        var blocker = Path.Combine(Path.GetTempPath(), $"telemetry-blocker-{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(blocker, "not a directory");
        Environment.SetEnvironmentVariable("AEROLINK_API_TELEMETRY_JSONL", Path.Combine(blocker, "telemetry.jsonl"));
        try
        {
            using var factory = new AeroLinkApiFactory();
            using var client = factory.CreateClient();
            using var response = await client.GetAsync("/api/setup/status");
            Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
            Assert.NotNull(ApiTestTelemetry.UnavailableReason);
        }
        finally
        {
            Environment.SetEnvironmentVariable("AEROLINK_API_TELEMETRY_JSONL", previous);
            File.Delete(blocker);
            ApiTestTelemetry.ResetForTest();
        }
    }

    [Fact]
    public void Reset_for_test_clears_telemetry_state()
    {
        ApiTestTelemetry.ResetForTest();
        Assert.Null(ApiTestTelemetry.UnavailableReason);
    }
}
