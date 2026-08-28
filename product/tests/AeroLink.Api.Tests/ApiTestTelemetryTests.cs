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
            // The AeroLink factory owns cleanup and telemetry at the outer disposal entry, so the framework's
            // recursive callback cannot add a second dispose record.
            dispose = Assert.Single(records.Select(record => (dynamic)record), record => record.phase == "dispose");
            Assert.Equal((double)host.constructionMs, (double)dispose.constructionMs);
            Assert.True((double)host.ms > 0, "hostMs must be recorded");
        }
        finally
        {
            ApiTestTelemetry.ResetForTest();
        }
    }

    [Fact]
    public async Task Synchronous_dispose_records_cleanup_once_and_removes_disposable_wal_artifacts()
    {
        var records = new List<object>();
        var factory = new AeroLinkApiFactory(telemetryObserver: records.Add);
        var databasePath = new SqliteConnectionStringBuilder(factory.ConnectionString).DataSource;
        try
        {
            using (var client = factory.CreateClient())
            using (var response = await client.GetAsync("/api/setup/status"))
                Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);

            Assert.True(File.Exists(databasePath + "-shm"), "The warmed keep-alive must hold the WAL index while the factory is active.");

            factory.Dispose();

            dynamic dispose = Assert.Single(records.Select(record => (dynamic)record), record => record.phase == "dispose");
            Assert.True((double)dispose.ms > 0, "Dispose telemetry must include the full synchronous disposal interval.");
            AssertDisposableDatabaseArtifactsAreGone(databasePath);
        }
        finally
        {
            factory.Dispose();
        }
    }

    [Fact]
    public async Task Asynchronous_dispose_records_cleanup_once_and_removes_disposable_wal_artifacts()
    {
        var records = new List<object>();
        var factory = new AeroLinkApiFactory(telemetryObserver: records.Add);
        var databasePath = new SqliteConnectionStringBuilder(factory.ConnectionString).DataSource;
        try
        {
            using (var client = factory.CreateClient())
            using (var response = await client.GetAsync("/api/setup/status"))
                Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);

            Assert.True(File.Exists(databasePath + "-shm"), "The warmed keep-alive must hold the WAL index while the factory is active.");

            await factory.DisposeAsync();

            dynamic dispose = Assert.Single(records.Select(record => (dynamic)record), record => record.phase == "dispose");
            Assert.True((double)dispose.ms > 0, "Dispose telemetry must include the full asynchronous disposal interval.");
            AssertDisposableDatabaseArtifactsAreGone(databasePath);
        }
        finally
        {
            await factory.DisposeAsync();
        }
    }

    private static void AssertDisposableDatabaseArtifactsAreGone(string databasePath)
    {
        Assert.All(new[] { databasePath, databasePath + "-wal", databasePath + "-shm" }, path =>
            Assert.False(File.Exists(path), $"The disposable database artifact '{path}' remained after factory disposal."));
    }

    [Fact]
    public void Reset_for_test_clears_telemetry_state()
    {
        ApiTestTelemetry.ResetForTest();
        Assert.Null(ApiTestTelemetry.UnavailableReason);
    }
}
