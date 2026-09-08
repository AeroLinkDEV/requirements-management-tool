using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using AeroLink.Domain.Integrations;
using AeroLink.Domain.Programs;
using AeroLink.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace AeroLink.Infrastructure.Tests;

public sealed class WebhookDeliveryPostgresQualificationTests
{
    [DisposablePostgresFact]
    public async Task PostgreSQL_claims_only_enabled_due_rows_and_fences_concurrent_workers()
    {
        var serverConnection = QualificationConnectionOrThrow();
        var databaseName = $"aerolink_963_{Guid.NewGuid():N}";
        var connection = await CreateDatabaseAsync(serverConnection, databaseName);
        ServiceProvider? provider = null;
        try
        {
            await using (var migrated = new AeroLinkDbContext(Options(connection)))
                await migrated.Database.MigrateAsync();

            var handler = new RecordingSuccessHandler();
            var claimBarrier = new CompetingClaimBarrier();
            provider = BuildProvider(connection, handler, claimBarrier);
            var now = DateTimeOffset.UtcNow;
            var disabledIds = new List<Guid>();
            var eligibleIds = new List<Guid>();
            Guid futureId;
            using (var scope = provider.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
                var security = scope.ServiceProvider.GetRequiredService<IntegrationSecurityService>();
                var project = new ProjectRecord(Guid.NewGuid(), "Webhook PostgreSQL qualification", "W963");
                var disabled = new WebhookSubscription(project.Id, "Disabled", "https://disabled.example/hook", "[\"quality.test\"]",
                    security.ProtectWebhookSecret("disabled-secret"), "tester", now);
                disabled.SetEnabled(false, now);
                var enabled = new WebhookSubscription(project.Id, "Enabled", "https://enabled.example/hook", "[\"quality.test\"]",
                    security.ProtectWebhookSecret("enabled-secret"), "tester", now);
                var futureSubscription = new WebhookSubscription(project.Id, "Future", "https://future.example/hook", "[\"quality.test\"]",
                    security.ProtectWebhookSecret("future-secret"), "tester", now);
                db.AddRange(project, disabled, enabled, futureSubscription);
                for (var i = 0; i < 60; i++)
                {
                    var integrationEvent = new IntegrationEvent(project.Id, "quality.test", "Test", Guid.NewGuid(), "{}", "tester", now);
                    var delivery = new WebhookDelivery(project.Id, integrationEvent.Id, disabled.Id, now);
                    db.AddRange(integrationEvent, delivery);
                    disabledIds.Add(delivery.Id);
                }

                // Npgsql's timestamp-with-time-zone mapping requires UTC values; SQLite offset ordering is
                // covered separately by the worker fixture, while this lane proves the PostgreSQL claim SQL.
                var dueAt = now.AddMinutes(-1).ToUniversalTime();
                var dueEvent = new IntegrationEvent(project.Id, "quality.test", "Test", Guid.NewGuid(), "{}", "tester", dueAt);
                var dueDelivery = new WebhookDelivery(project.Id, dueEvent.Id, enabled.Id, dueAt);
                db.AddRange(dueEvent, dueDelivery);
                eligibleIds.Add(dueDelivery.Id);

                var futureTime = now.AddMinutes(30).ToUniversalTime();
                var futureEvent = new IntegrationEvent(project.Id, "quality.test", "Test", Guid.NewGuid(), "{}", "tester", futureTime);
                var future = new WebhookDelivery(project.Id, futureEvent.Id, futureSubscription.Id, futureTime);
                db.AddRange(futureEvent, future);
                futureId = future.Id;
                await db.SaveChangesAsync();
            }

            var workerOne = new WebhookDeliveryWorker(provider.GetRequiredService<IServiceScopeFactory>(),
                provider.GetRequiredService<IConfiguration>(), NullLogger<WebhookDeliveryWorker>.Instance);
            var workerTwo = new WebhookDeliveryWorker(provider.GetRequiredService<IServiceScopeFactory>(),
                provider.GetRequiredService<IConfiguration>(), NullLogger<WebhookDeliveryWorker>.Instance);
            await Task.WhenAll(workerOne.DeliverBatchAsync(CancellationToken.None), workerTwo.DeliverBatchAsync(CancellationToken.None));
            Assert.True(claimBarrier.BothWritersReached, "The qualification must force both workers to persist the same claim candidate.");

            using var checkScope = provider.CreateScope();
            var checkDb = checkScope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var disabledRows = await checkDb.WebhookDeliveries.AsNoTracking().Where(x => disabledIds.Contains(x.Id)).ToListAsync();
            Assert.All(disabledRows, row => Assert.Equal(0, row.AttemptCount));
            var eligibleRows = await checkDb.WebhookDeliveries.AsNoTracking().Where(x => eligibleIds.Contains(x.Id)).ToListAsync();
            Assert.All(eligibleRows, row =>
            {
                Assert.Equal(WebhookDeliveryState.Delivered, row.State);
                Assert.Equal(1, row.AttemptCount);
                Assert.Null(row.ClaimToken);
            });
            var futureRow = await checkDb.WebhookDeliveries.AsNoTracking().SingleAsync(x => x.Id == futureId);
            Assert.Equal(0, futureRow.AttemptCount);
            Assert.Single(handler.DeliveryIds);
        }
        finally
        {
            if (provider is not null) await provider.DisposeAsync();
            await DropDatabaseAsync(serverConnection, databaseName);
        }
    }

    private static ServiceProvider BuildProvider(string connection, RecordingSuccessHandler handler, CompetingClaimBarrier claimBarrier)
    {
        var services = new ServiceCollection();
        services.AddDbContext<AeroLinkDbContext>(options => options.UseNpgsql(connection).AddInterceptors(claimBarrier));
        services.AddDataProtection();
        services.AddScoped<IntegrationSecurityService>();
        services.AddSingleton<IWebhookDnsResolver, LoopbackResolver>();
        services.AddSingleton<WebhookDestinationPolicy>();
        services.AddSingleton(handler);
        services.AddHttpClient("AeroLinkWebhooks")
            .ConfigurePrimaryHttpMessageHandler((serviceProvider) => serviceProvider.GetRequiredService<RecordingSuccessHandler>());
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Integrations:AllowInsecureWebhookTargets"] = "true",
            ["Integrations:AllowPrivateWebhookTargets"] = "true",
        }).Build());
        return services.BuildServiceProvider();
    }

    private static DbContextOptions<AeroLinkDbContext> Options(string connection) =>
        new DbContextOptionsBuilder<AeroLinkDbContext>().UseNpgsql(connection).Options;

    private static string QualificationConnectionOrThrow()
    {
        var connection = Environment.GetEnvironmentVariable("AEROLINK_MIGRATIONS_CONNECTION");
        if (string.IsNullOrWhiteSpace(connection))
            throw new InvalidOperationException("Webhook PostgreSQL qualification requires AEROLINK_MIGRATIONS_CONNECTION.");
        var builder = new NpgsqlConnectionStringBuilder(connection);
        var host = (builder.Host ?? string.Empty).Trim().Trim('[', ']');
        if (!(string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
            || IPAddress.TryParse(host, out var address) && IPAddress.IsLoopback(address)))
            throw new InvalidOperationException("Webhook PostgreSQL qualification requires a loopback host.");
        if (builder.Port == 54329)
            throw new InvalidOperationException("Webhook PostgreSQL qualification refuses persistent port 54329.");
        return connection;
    }

    private static async Task<string> CreateDatabaseAsync(string serverConnection, string databaseName)
    {
        var builder = new NpgsqlConnectionStringBuilder(serverConnection) { Database = "postgres" };
        await using var server = new NpgsqlConnection(builder.ConnectionString);
        await server.OpenAsync();
        await using var command = server.CreateCommand();
        command.CommandText = $"CREATE DATABASE \"{databaseName}\"";
        await command.ExecuteNonQueryAsync();
        return new NpgsqlConnectionStringBuilder(serverConnection) { Database = databaseName }.ConnectionString;
    }

    private static async Task DropDatabaseAsync(string serverConnection, string databaseName)
    {
        var builder = new NpgsqlConnectionStringBuilder(serverConnection) { Database = "postgres" };
        await using var server = new NpgsqlConnection(builder.ConnectionString);
        await server.OpenAsync();
        await using var command = server.CreateCommand();
        command.CommandText = $"DROP DATABASE IF EXISTS \"{databaseName}\" WITH (FORCE)";
        await command.ExecuteNonQueryAsync();
    }

    private sealed class DisposablePostgresFactAttribute : FactAttribute
    {
        public DisposablePostgresFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("AEROLINK_MIGRATIONS_CONNECTION")))
                Skip = "Webhook PostgreSQL qualification skipped: set AEROLINK_MIGRATIONS_CONNECTION to the dedicated disposable database.";
        }
    }

    private sealed class LoopbackResolver : IWebhookDnsResolver
    {
        public Task<IReadOnlyList<IPAddress>> ResolveAsync(string host, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<IPAddress>>([IPAddress.Loopback]);
    }

    private sealed class RecordingSuccessHandler : HttpMessageHandler
    {
        public ConcurrentBag<string> DeliveryIds { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            DeliveryIds.Add(request.Headers.GetValues("X-AeroLink-Delivery").Single());
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }

    private sealed class CompetingClaimBarrier : SaveChangesInterceptor
    {
        private readonly TaskCompletionSource<bool> _bothWriters = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _writers;

        public bool BothWritersReached => _bothWriters.Task.IsCompletedSuccessfully;

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            var context = eventData.Context;
            var claiming = context?.ChangeTracker.Entries<WebhookDelivery>().Any(x =>
                x.State == EntityState.Modified && x.Entity.State == WebhookDeliveryState.Delivering) == true;
            if (!claiming) return result;
            if (Interlocked.Increment(ref _writers) == 2) _bothWriters.TrySetResult(true);
            await _bothWriters.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            return result;
        }
    }
}
