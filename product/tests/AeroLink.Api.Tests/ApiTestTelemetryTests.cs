using System.Net.Http.Json;
using Microsoft.Data.Sqlite;

namespace AeroLink.Api.Tests;

public sealed class ApiTestTelemetryTests
{
    [Fact]
    public async Task An_injected_telemetry_write_failure_never_fails_the_authoritative_test()
    {
        // The injected failure is scoped to this factory's factoryId: no environment variable is changed
        // and no process-global telemetry path is mutated, so parallel sibling factories keep writing to
        // the suite's real telemetry file undisturbed.
        ApiTestTelemetry.ResetForTest();
        var sawFailure = false;
        long factoryId = 0;
        try
        {
            using var factory = new AeroLinkApiFactory();
            factoryId = factory.TelemetryFactoryId;
            ApiTestTelemetry.InjectWriteFailure(factoryId, () =>
            {
                sawFailure = true;
                return new IOException("simulated telemetry write failure");
            });
            using var client = factory.CreateClient();
            using var response = await client.GetAsync("/api/setup/status");
            Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
            Assert.True(sawFailure, "the telemetry write path must have been attempted and contained");
            Assert.NotNull(ApiTestTelemetry.InjectedFailureReason(factoryId));
            Assert.Null(ApiTestTelemetry.UnavailableReason);
        }
        finally
        {
            if (factoryId != 0) ApiTestTelemetry.ClearInjectedFailures(factoryId);
            ApiTestTelemetry.ResetForTest();
        }
    }

    [Fact]
    public async Task Host_and_dispose_records_report_the_same_pre_host_construction_latency()
    {
        // Regression for the round-2 finding: constructionMs must be captured BEFORE base.CreateHost and
        // must not contain hostMs. The host and dispose records therefore carry the same pre-host value.
        // The old behavior read _construction.Elapsed after host completion, making host.constructionMs
        // include hostMs and differ from dispose.constructionMs by the whole test duration.
        ApiTestTelemetry.ResetForTest();
        var records = new List<object>();
        try
        {
            dynamic host;
            dynamic dispose;
            using (var factory = new AeroLinkApiFactory(telemetryObserver: records.Add))
            {
                using var client = factory.CreateClient();
                using var response = await client.GetAsync("/api/setup/status");
                Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
                host = records.Select(record => (dynamic)record).Single(record => record.phase == "host");
            }
            // WebApplicationFactory can invoke Dispose(bool) more than once; the aggregator deterministically
            // keeps the last dispose record, so the regression checks the same record it will count.
            dispose = records.Select(record => (dynamic)record).Last(record => record.phase == "dispose");
            Assert.Equal((double)host.constructionMs, (double)dispose.constructionMs);
            Assert.True((double)host.ms > 0, "hostMs must be recorded");
        }
        finally
        {
            ApiTestTelemetry.ResetForTest();
        }
    }
}
