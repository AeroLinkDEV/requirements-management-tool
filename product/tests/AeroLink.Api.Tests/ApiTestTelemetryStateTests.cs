namespace AeroLink.Api.Tests;

public sealed class ApiTestTelemetryStateTests
{
    [Fact]
    public void Reset_for_test_clears_telemetry_state()
    {
        ApiTestTelemetry.ResetForTest();
        Assert.Null(ApiTestTelemetry.UnavailableReason);
    }
}
