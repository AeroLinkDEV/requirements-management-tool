using System.Net;
using System.Net.Http;
using AeroLink.Domain.Integrations;
using AeroLink.Domain.Programs;
using AeroLink.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace AeroLink.Infrastructure.Tests;

public sealed class WebhookDeliveryWorkerTests
{
    [Fact]
    public async Task Disabled_and_future_backlog_cannot_hide_due_enabled_work_and_reenable_resumes_backlog()
    {
        await using var fixture = await WorkerFixture.CreateAsync();
        var now = DateTimeOffset.UtcNow;
        Guid activeId;
        var disabledIds = new List<Guid>();
        using (var scope = fixture.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var project = fixture.Project;
            var disabled = new WebhookSubscription(project.Id, "Disabled", "https://disabled.example/hook", "[\"quality.test\"]", "unused", "tester", now);
            disabled.SetEnabled(false, now);
            var enabled = new WebhookSubscription(project.Id, "Enabled", "https://enabled.example/hook", "[\"quality.test\"]", "unused", "tester", now);
            var futureSubscription = new WebhookSubscription(project.Id, "Future", "https://future.example/hook", "[\"quality.test\"]", "unused", "tester", now);
            db.WebhookSubscriptions.AddRange(disabled, enabled);
            db.WebhookSubscriptions.Add(futureSubscription);
            for (var i = 0; i < 51; i++)
            {
                var integrationEvent = new IntegrationEvent(project.Id, "quality.test", "Test", Guid.NewGuid(), "{}", "tester", now);
                var delivery = new WebhookDelivery(project.Id, integrationEvent.Id, disabled.Id, now);
                db.IntegrationEvents.Add(integrationEvent); db.WebhookDeliveries.Add(delivery); disabledIds.Add(delivery.Id);
            }
            // The due row intentionally uses a non-UTC offset. SQLite stores DateTimeOffset as text, so the
            // worker's julianday predicate must compare instants rather than lexical local-clock text.
            var dueWithOffset = now.AddMinutes(-1).ToOffset(TimeSpan.FromHours(-4));
            var activeEvent = new IntegrationEvent(project.Id, "quality.test", "Test", Guid.NewGuid(), "{}", "tester", dueWithOffset);
            var active = new WebhookDelivery(project.Id, activeEvent.Id, enabled.Id, dueWithOffset);
            db.IntegrationEvents.Add(activeEvent); db.WebhookDeliveries.Add(active); activeId = active.Id;
            var futureTime = now.AddMinutes(30).ToOffset(TimeSpan.FromHours(-4));
            var futureEvent = new IntegrationEvent(project.Id, "quality.test", "Test", Guid.NewGuid(), "{}", "tester", futureTime);
            db.IntegrationEvents.Add(futureEvent);
            db.WebhookDeliveries.Add(new WebhookDelivery(project.Id, futureEvent.Id, futureSubscription.Id, futureTime));
            await db.SaveChangesAsync();
        }

        var worker = fixture.Worker();
        await worker.DeliverBatchAsync(CancellationToken.None);
        using (var scope = fixture.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var active = await db.WebhookDeliveries.AsNoTracking().SingleAsync(x => x.Id == activeId);
            Assert.Equal(1, active.AttemptCount);
            Assert.Equal(WebhookDeliveryState.RetryScheduled, active.State);
            Assert.Equal(0, await db.WebhookDeliveries.AsNoTracking().CountAsync(x => x.SubscriptionId == db.WebhookSubscriptions.Where(s => s.Name == "Future").Select(s => s.Id).Single() && x.AttemptCount > 0));
            Assert.All(await db.WebhookDeliveries.AsNoTracking().Where(x => disabledIds.Contains(x.Id)).ToListAsync(), x => Assert.Equal(0, x.AttemptCount));
            var disabled = await db.WebhookSubscriptions.SingleAsync(x => x.Name == "Disabled");
            disabled.SetEnabled(true, DateTimeOffset.UtcNow);
            await db.SaveChangesAsync();
        }

        await worker.DeliverBatchAsync(CancellationToken.None);
        using var check = fixture.Services.CreateScope();
        var checkDb = check.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        var resumed = await checkDb.WebhookDeliveries.AsNoTracking().Where(x => disabledIds.Contains(x.Id)).ToListAsync();
        Assert.Equal(20, resumed.Count(x => x.AttemptCount == 1));
    }

    [Fact]
    public async Task Cancellation_releases_the_claim_with_a_cancelled_attempt_history_entry()
    {
        var handler = new BlockingWebhookHandler();
        await using var fixture = await WorkerFixture.CreateAsync(new LoopbackResolver(), handler,
            allowInsecure: true, allowPrivate: true);
        var now = DateTimeOffset.UtcNow;
        Guid deliveryId;
        using (var scope = fixture.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var security = scope.ServiceProvider.GetRequiredService<IntegrationSecurityService>();
            var subscription = new WebhookSubscription(fixture.Project.Id, "Blocking", "http://127.0.0.1/hook", "[\"quality.test\"]", security.ProtectWebhookSecret("blocking-secret"), "tester", now);
            var integrationEvent = new IntegrationEvent(fixture.Project.Id, "quality.test", "Test", Guid.NewGuid(), "{}", "tester", now);
            var delivery = new WebhookDelivery(fixture.Project.Id, integrationEvent.Id, subscription.Id, now);
            db.AddRange(subscription, integrationEvent, delivery);
            await db.SaveChangesAsync();
            deliveryId = delivery.Id;
        }

        using var cancellation = new CancellationTokenSource();
        var task = fixture.Worker().DeliverBatchAsync(cancellation.Token);
        await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);

        using var check = fixture.Services.CreateScope();
        var checkDb = check.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        var saved = await checkDb.WebhookDeliveries.AsNoTracking().SingleAsync(x => x.Id == deliveryId);
        Assert.Equal(WebhookDeliveryState.RetryScheduled, saved.State);
        Assert.Null(saved.ClaimToken);
        Assert.Contains("Cancelled", saved.AttemptHistoryJson);
        Assert.Contains("shut down", saved.LastError, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Receiver_success_with_uncertain_final_save_is_retried_with_the_same_delivery_identity()
    {
        var handler = new CapturingSuccessHandler();
        var interceptor = new FailFirstDeliveredSaveInterceptor();
        await using var fixture = await WorkerFixture.CreateAsync(new LoopbackResolver(), handler,
            allowInsecure: true, allowPrivate: true, interceptor);
        var now = DateTimeOffset.UtcNow;
        Guid deliveryId;
        using (var scope = fixture.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var security = scope.ServiceProvider.GetRequiredService<IntegrationSecurityService>();
            var subscription = new WebhookSubscription(fixture.Project.Id, "Uncertain", "http://127.0.0.1/hook", "[\"quality.test\"]", security.ProtectWebhookSecret("uncertain-secret"), "tester", now);
            var integrationEvent = new IntegrationEvent(fixture.Project.Id, "quality.test", "Test", Guid.NewGuid(), "{}", "tester", now);
            var delivery = new WebhookDelivery(fixture.Project.Id, integrationEvent.Id, subscription.Id, now);
            db.AddRange(subscription, integrationEvent, delivery);
            await db.SaveChangesAsync();
            deliveryId = delivery.Id;
        }

        var worker = fixture.Worker();
        await worker.DeliverBatchAsync(CancellationToken.None);
        using (var check = fixture.Services.CreateScope())
        {
            var db = check.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var uncertain = await db.WebhookDeliveries.AsNoTracking().SingleAsync(x => x.Id == deliveryId);
            Assert.Equal(WebhookDeliveryState.RetryScheduled, uncertain.State);
            Assert.Contains("RetryScheduled", uncertain.AttemptHistoryJson);
            await db.WebhookDeliveries.Where(x => x.Id == deliveryId)
                .ExecuteUpdateAsync(x => x.SetProperty(y => y.NextAttemptAt, DateTimeOffset.UtcNow.AddMinutes(-1)));
        }

        await worker.DeliverBatchAsync(CancellationToken.None);
        using var finalCheck = fixture.Services.CreateScope();
        var finalDb = finalCheck.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        var delivered = await finalDb.WebhookDeliveries.AsNoTracking().SingleAsync(x => x.Id == deliveryId);
        Assert.Equal(WebhookDeliveryState.Delivered, delivered.State);
        Assert.Equal(2, handler.DeliveryIds.Count);
        Assert.All(handler.DeliveryIds, id => Assert.Equal(deliveryId.ToString(), id));
    }

    [Fact]
    public async Task Expired_claim_is_recovered_then_can_retry_with_history_and_identity_intact()
    {
        await using var fixture = await WorkerFixture.CreateAsync();
        var now = DateTimeOffset.UtcNow;
        Guid deliveryId;
        using (var scope = fixture.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var subscription = new WebhookSubscription(fixture.Project.Id, "Expired", "https://expired.example/hook", "[\"quality.test\"]", "unused", "tester", now);
            var integrationEvent = new IntegrationEvent(fixture.Project.Id, "quality.test", "Test", Guid.NewGuid(), "{}", "tester", now);
            var delivery = new WebhookDelivery(fixture.Project.Id, integrationEvent.Id, subscription.Id, now);
            var token = Guid.NewGuid(); delivery.BeginAttempt("old-worker", token, now.AddMinutes(-10), TimeSpan.FromMinutes(2));
            db.AddRange(subscription, integrationEvent, delivery); await db.SaveChangesAsync(); deliveryId = delivery.Id;
        }

        var worker = fixture.Worker();
        await worker.DeliverBatchAsync(CancellationToken.None);
        using (var scope = fixture.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var recovered = await db.WebhookDeliveries.AsNoTracking().SingleAsync(x => x.Id == deliveryId);
            Assert.Equal(WebhookDeliveryState.RetryScheduled, recovered.State);
            Assert.Null(recovered.ClaimToken);
            Assert.Contains("RetryScheduled", recovered.AttemptHistoryJson);
            Assert.Equal(1, recovered.AttemptCount);
            await db.WebhookDeliveries.Where(x => x.Id == deliveryId).ExecuteUpdateAsync(x => x.SetProperty(y => y.NextAttemptAt, DateTimeOffset.UtcNow.AddMinutes(-1)));
        }

        await worker.DeliverBatchAsync(CancellationToken.None);
        using var check = fixture.Services.CreateScope();
        var checkDb = check.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        var retried = await checkDb.WebhookDeliveries.AsNoTracking().SingleAsync(x => x.Id == deliveryId);
        Assert.Equal(deliveryId, retried.Id);
        Assert.Equal(2, retried.AttemptCount);
        Assert.Equal(WebhookDeliveryState.RetryScheduled, retried.State);
        Assert.Contains("old-worker", retried.AttemptHistoryJson);
    }

    [Fact]
    public async Task Stale_completion_cannot_persist_after_a_second_context_recovers_the_expired_claim()
    {
        await using var fixture = await WorkerFixture.CreateAsync();
        var now = DateTimeOffset.UtcNow;
        Guid deliveryId;
        using (var scope = fixture.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var subscription = new WebhookSubscription(fixture.Project.Id, "Race", "https://race.example/hook", "[\"quality.test\"]", "unused", "tester", now);
            var integrationEvent = new IntegrationEvent(fixture.Project.Id, "quality.test", "Test", Guid.NewGuid(), "{}", "tester", now);
            var delivery = new WebhookDelivery(fixture.Project.Id, integrationEvent.Id, subscription.Id, now);
            db.AddRange(subscription, integrationEvent, delivery);
            await db.SaveChangesAsync();
            deliveryId = delivery.Id;
        }

        using var staleScope = fixture.Services.CreateScope();
        using var recoveryScope = fixture.Services.CreateScope();
        var staleDb = staleScope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        var recoveryDb = recoveryScope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        var stale = await staleDb.WebhookDeliveries.SingleAsync(x => x.Id == deliveryId);
        var originalToken = Guid.NewGuid();
        stale.BeginAttempt("worker-a", originalToken, now, WebhookDeliveryWorker.ClaimLease);
        await staleDb.SaveChangesAsync();

        var recovered = await recoveryDb.WebhookDeliveries.SingleAsync(x => x.Id == deliveryId);
        recovered.RecoverExpired(now.AddMinutes(3));
        await recoveryDb.SaveChangesAsync();

        stale.Complete(originalToken, 200, now.AddMinutes(4));
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => staleDb.SaveChangesAsync());

        using var check = fixture.Services.CreateScope();
        var checkDb = check.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        var saved = await checkDb.WebhookDeliveries.AsNoTracking().SingleAsync(x => x.Id == deliveryId);
        Assert.Equal(WebhookDeliveryState.RetryScheduled, saved.State);
        Assert.Null(saved.ResponseStatusCode);
        Assert.Contains("RetryScheduled", saved.AttemptHistoryJson);
    }

    private sealed class WorkerFixture : IAsyncDisposable
    {
        private WorkerFixture(ServiceProvider services, ProjectRecord project, string databasePath)
        { Services = services; Project = project; DatabasePath = databasePath; }
        public ServiceProvider Services { get; }
        public ProjectRecord Project { get; }
        private string DatabasePath { get; }

        public static async Task<WorkerFixture> CreateAsync(
            IWebhookDnsResolver? resolver = null,
            HttpMessageHandler? handler = null,
            bool allowInsecure = false,
            bool allowPrivate = false,
            SaveChangesInterceptor? interceptor = null)
        {
            var path = Path.Combine(Path.GetTempPath(), $"aerolink-webhook-{Guid.NewGuid():N}.db");
            var services = new ServiceCollection();
            services.AddDbContext<AeroLinkDbContext>(options =>
            {
                options.UseSqlite($"Data Source={path};Pooling=False");
                if (interceptor is not null) options.AddInterceptors(interceptor);
            });
            services.AddDataProtection();
            services.AddScoped<IntegrationSecurityService>();
            services.AddSingleton<IWebhookDnsResolver>(resolver ?? new NoNetworkResolver());
            services.AddSingleton<WebhookDestinationPolicy>();
            services.AddHttpClient("AeroLinkWebhooks")
                .ConfigurePrimaryHttpMessageHandler(() => handler ?? new HttpClientHandler());
            services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Integrations:AllowInsecureWebhookTargets"] = allowInsecure.ToString(),
                ["Integrations:AllowPrivateWebhookTargets"] = allowPrivate.ToString(),
            }).Build());
            var provider = services.BuildServiceProvider();
            using (var scope = provider.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
                await db.Database.EnsureCreatedAsync();
                var project = new ProjectRecord(Guid.NewGuid(), "Webhook worker test", $"W-{Guid.NewGuid():N}");
                db.Projects.Add(project); await db.SaveChangesAsync();
                return new(provider, project, path);
            }
        }

        public WebhookDeliveryWorker Worker() => new(Services.GetRequiredService<IServiceScopeFactory>(), Services.GetRequiredService<IConfiguration>(), NullLogger<WebhookDeliveryWorker>.Instance);

        public async ValueTask DisposeAsync()
        {
            await Services.DisposeAsync();
            if (File.Exists(DatabasePath)) File.Delete(DatabasePath);
            var wal = DatabasePath + "-wal"; if (File.Exists(wal)) File.Delete(wal);
            var shm = DatabasePath + "-shm"; if (File.Exists(shm)) File.Delete(shm);
        }
    }

    private sealed class NoNetworkResolver : IWebhookDnsResolver
    {
        public Task<IReadOnlyList<IPAddress>> ResolveAsync(string host, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The worker fairness fixture must not perform network I/O.");
    }

    private sealed class LoopbackResolver : IWebhookDnsResolver
    {
        public Task<IReadOnlyList<IPAddress>> ResolveAsync(string host, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<IPAddress>>([IPAddress.Loopback]);
    }

    private sealed class BlockingWebhookHandler : HttpMessageHandler
    {
        public TaskCompletionSource<bool> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Started.TrySetResult(true);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new OperationCanceledException(cancellationToken);
        }
    }

    private sealed class CapturingSuccessHandler : HttpMessageHandler
    {
        public List<string> DeliveryIds { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            DeliveryIds.Add(request.Headers.GetValues("X-AeroLink-Delivery").Single());
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }

    private sealed class FailFirstDeliveredSaveInterceptor : SaveChangesInterceptor
    {
        private int _failed;

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            var context = eventData.Context;
            if (context is not null &&
                context.ChangeTracker.Entries<WebhookDelivery>().Any(x =>
                    x.State == EntityState.Modified && x.Entity.State == WebhookDeliveryState.Delivered) &&
                Interlocked.CompareExchange(ref _failed, 1, 0) == 0)
            {
                throw new DbUpdateException("Injected uncertain final delivery save.", new InvalidOperationException("The receiver already returned success."));
            }

            return ValueTask.FromResult(result);
        }
    }
}
