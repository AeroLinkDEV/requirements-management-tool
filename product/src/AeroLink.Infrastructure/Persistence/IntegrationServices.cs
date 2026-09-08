using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AeroLink.Domain.Integrations;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AeroLink.Infrastructure.Persistence;

public sealed record IssuedServiceCredential(IntegrationServiceIdentity Identity, string ApiKey);
public sealed record AuthenticatedServiceIdentity(Guid Id, Guid ProjectId, string Name, IReadOnlySet<string> Scopes)
{
    public bool HasScope(string scope) => Scopes.Contains("*") || Scopes.Contains(scope, StringComparer.OrdinalIgnoreCase);
}

public sealed class IntegrationSecurityService(AeroLinkDbContext db, IDataProtectionProvider dataProtection)
{
    private readonly IDataProtector _webhookProtector = dataProtection.CreateProtector("AeroLink.Webhooks.SigningSecret.v1");

    public async Task<IssuedServiceCredential> CreateIdentityAsync(Guid projectId, string name, IReadOnlyCollection<string> scopes, string actor, DateTimeOffset now, CancellationToken ct)
    {
        var normalized = scopes.Select(x => x.Trim().ToLowerInvariant()).Where(x => x.Length > 0).Distinct().Order().ToArray();
        if (normalized.Length == 0) throw new ArgumentException("At least one API scope is required.");
        var clientId = Convert.ToHexString(RandomNumberGenerator.GetBytes(12)).ToLowerInvariant();
        var secret = Base64Url(RandomNumberGenerator.GetBytes(32));
        var apiKey = $"alk_{clientId}.{secret}";
        var identity = new IntegrationServiceIdentity(projectId, name, clientId, Hash(apiKey), JsonSerializer.Serialize(normalized), actor, now);
        db.IntegrationServiceIdentities.Add(identity);
        await db.SaveChangesAsync(ct);
        return new(identity, apiKey);
    }

    public async Task<AuthenticatedServiceIdentity?> ResolveAsync(string? authorization, DateTimeOffset now, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(authorization) || !authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return null;
        var token = authorization[7..].Trim();
        if (!token.StartsWith("alk_", StringComparison.Ordinal) || token.IndexOf('.') is < 5 or > 80) return null;
        var clientId = token[4..token.IndexOf('.')].ToLowerInvariant();
        var identity = await db.IntegrationServiceIdentities.SingleOrDefaultAsync(x => x.ClientId == clientId && x.State == ServiceIdentityState.Active, ct);
        if (identity is null || !FixedEquals(identity.ApiKeyHash, Hash(token))) return null;
        identity.RecordUse(now);
        await db.SaveChangesAsync(ct);
        var scopes = JsonSerializer.Deserialize<string[]>(identity.ScopesJson) ?? [];
        return new(identity.Id, identity.ProjectId, identity.Name, scopes.ToHashSet(StringComparer.OrdinalIgnoreCase));
    }

    public string ProtectWebhookSecret(string secret) => _webhookProtector.Protect(secret);
    public string UnprotectWebhookSecret(string protectedSecret) => _webhookProtector.Unprotect(protectedSecret);
    public static string GenerateWebhookSecret() => $"whsec_{Base64Url(RandomNumberGenerator.GetBytes(32))}";
    public static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    private static bool FixedEquals(string left, string right) => CryptographicOperations.FixedTimeEquals(Convert.FromHexString(left), Convert.FromHexString(right));
    private static string Base64Url(byte[] value) => Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

public sealed class IntegrationEventPublisher(AeroLinkDbContext db)
{
    public async Task<IntegrationEvent> EnqueueAsync(Guid projectId, string eventType, string aggregateType, Guid aggregateId, object payload, string actor, DateTimeOffset now, CancellationToken ct, string? idempotencyKey = null)
    {
        var payloadJson = JsonSerializer.Serialize(payload, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        using var _ = JsonDocument.Parse(payloadJson);
        var integrationEvent = new IntegrationEvent(projectId, eventType, aggregateType, aggregateId, payloadJson, actor, now, idempotencyKey);
        db.IntegrationEvents.Add(integrationEvent);
        var subscriptions = await db.WebhookSubscriptions.AsNoTracking().Where(x => x.ProjectId == projectId && x.IsEnabled).ToListAsync(ct);
        foreach (var subscription in subscriptions)
        {
            var types = JsonSerializer.Deserialize<string[]>(subscription.EventTypesJson) ?? [];
            if (types.Any(x => x == "*" || x.Equals(eventType, StringComparison.OrdinalIgnoreCase)))
                db.WebhookDeliveries.Add(new WebhookDelivery(projectId, integrationEvent.Id, subscription.Id, now));
        }
        integrationEvent.MarkDispatched(now);
        await db.SaveChangesAsync(ct);
        return integrationEvent;
    }
}

public sealed class WebhookDeliveryWorker(IServiceScopeFactory scopeFactory, IConfiguration configuration, ILogger<WebhookDeliveryWorker> logger) : BackgroundService
{
    public static readonly TimeSpan ClaimLease = TimeSpan.FromMinutes(2);
    public const int BatchSize = 20;
    private readonly string _worker = $"{Environment.MachineName}/{Environment.ProcessId}";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Max(1, configuration.GetValue<int?>("Integrations:DeliveryPollSeconds") ?? 3)));
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await DeliverBatchAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogError(ex, "Webhook delivery cycle failed."); }
            if (!await timer.WaitForNextTickAsync(stoppingToken)) break;
        }
    }

    internal async Task DeliverBatchAsync(CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        var clientFactory = scope.ServiceProvider.GetRequiredService<IHttpClientFactory>();
        var security = scope.ServiceProvider.GetRequiredService<IntegrationSecurityService>();
        var destinationPolicy = scope.ServiceProvider.GetRequiredService<WebhookDestinationPolicy>();
        var allowInsecure = configuration.GetValue<bool>("Integrations:AllowInsecureWebhookTargets");
        var allowPrivate = configuration.GetValue<bool>("Integrations:AllowPrivateWebhookTargets");

        await RecoverExpiredAsync(db, ct);
        for (var i = 0; i < BatchSize; i++)
        {
            ct.ThrowIfCancellationRequested();
            var claim = await ClaimNextAsync(db, DateTimeOffset.UtcNow, ct);
            if (claim is null) return;
            var (deliveryId, claimToken, integrationEventId, subscriptionId) = claim.Value;
            try
            {
                db.ChangeTracker.Clear();
                var integrationEvent = await db.IntegrationEvents.AsNoTracking().SingleAsync(x => x.Id == integrationEventId, ct);
                var subscription = await db.WebhookSubscriptions.AsNoTracking().SingleAsync(x => x.Id == subscriptionId, ct);
                if (!subscription.IsEnabled)
                {
                    await ReleaseClaimAsync(db, deliveryId, claimToken, DateTimeOffset.UtcNow, "Subscription disabled before send.", ct);
                    continue;
                }

                var approved = await destinationPolicy.ValidateAsync(new Uri(subscription.EndpointUrl), allowInsecure, allowPrivate, ct);
                var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
                var envelope = JsonSerializer.Serialize(new { id = integrationEvent.Id, type = integrationEvent.EventType, occurredAt = integrationEvent.OccurredAt, projectId = integrationEvent.ProjectId, aggregate = new { type = integrationEvent.AggregateType, id = integrationEvent.AggregateId }, data = JsonDocument.Parse(integrationEvent.PayloadJson).RootElement });
                var secret = security.UnprotectWebhookSecret(subscription.ProtectedSecret);
                var signature = Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes($"{timestamp}.{envelope}"))).ToLowerInvariant();
                using var request = new HttpRequestMessage(HttpMethod.Post, subscription.EndpointUrl) { Content = new StringContent(envelope, Encoding.UTF8, "application/json") };
                request.Headers.Add("X-AeroLink-Event", integrationEvent.EventType); request.Headers.Add("X-AeroLink-Delivery", deliveryId.ToString()); request.Headers.Add("X-AeroLink-Timestamp", timestamp); request.Headers.Add("X-AeroLink-Signature", $"v1={signature}");
                request.Options.Set(WebhookConnectionTransport.ApprovedAddressesOption, approved.Addresses);
                using var response = await clientFactory.CreateClient("AeroLinkWebhooks").SendAsync(request, ct);
                var now = DateTimeOffset.UtcNow;
                if ((int)response.StatusCode is >= 200 and < 300)
                    await CompleteClaimAsync(db, deliveryId, claimToken, (int)response.StatusCode, now, ct);
                else
                    await FailClaimAsync(db, deliveryId, claimToken, (int)response.StatusCode, $"Endpoint returned HTTP {(int)response.StatusCode}.", now, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                await ReleaseClaimAsync(db, deliveryId, claimToken, DateTimeOffset.UtcNow, "Worker shut down before finishing; returned to the queue.", CancellationToken.None);
                throw;
            }
            catch (Exception ex)
            {
                await FailClaimAsync(db, deliveryId, claimToken, null, ex.Message, DateTimeOffset.UtcNow, ct);
            }
        }
    }

    private async Task RecoverExpiredAsync(AeroLinkDbContext db, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var expiredQuery = db.Database.IsSqlite()
            ? db.WebhookDeliveries.FromSqlInterpolated($"SELECT * FROM webhook_deliveries WHERE State = 'Delivering' AND ClaimExpiresAt IS NOT NULL AND julianday(ClaimExpiresAt) <= julianday({now}) ORDER BY julianday(ClaimExpiresAt), Id LIMIT {BatchSize}")
            : db.WebhookDeliveries.Where(x => x.State == WebhookDeliveryState.Delivering && x.ClaimExpiresAt != null && x.ClaimExpiresAt <= now).OrderBy(x => x.ClaimExpiresAt).ThenBy(x => x.Id).Take(BatchSize);
        var ids = await expiredQuery.AsNoTracking().Select(x => x.Id).ToListAsync(ct);
        foreach (var id in ids)
        {
            db.ChangeTracker.Clear();
            var delivery = await db.WebhookDeliveries.SingleOrDefaultAsync(x => x.Id == id, ct);
            if (delivery is null || !delivery.ClaimExpired(now)) continue;
            var previousWorker = delivery.ClaimedBy;
            try
            {
                delivery.RecoverExpired(now);
                await db.SaveChangesAsync(ct);
                logger.LogWarning("Recovered expired webhook delivery {DeliveryId} from {Worker}.", id, previousWorker);
            }
            catch (DbUpdateConcurrencyException) { db.ChangeTracker.Clear(); }
        }
    }

    private async Task<(Guid DeliveryId, Guid ClaimToken, Guid IntegrationEventId, Guid SubscriptionId)?> ClaimNextAsync(AeroLinkDbContext db, DateTimeOffset now, CancellationToken ct)
    {
        var dueQuery = db.Database.IsSqlite()
            ? db.WebhookDeliveries.FromSqlInterpolated($"SELECT d.* FROM webhook_deliveries AS d INNER JOIN webhook_subscriptions AS s ON s.Id = d.SubscriptionId WHERE d.State IN ('Pending', 'RetryScheduled') AND julianday(d.NextAttemptAt) <= julianday({now}) AND s.IsEnabled = 1 ORDER BY julianday(d.NextAttemptAt), d.Id LIMIT {BatchSize}")
            : db.WebhookDeliveries.Where(x => (x.State == WebhookDeliveryState.Pending || x.State == WebhookDeliveryState.RetryScheduled) && x.NextAttemptAt <= now).Where(x => db.WebhookSubscriptions.Any(s => s.Id == x.SubscriptionId && s.IsEnabled)).OrderBy(x => x.NextAttemptAt).ThenBy(x => x.Id).Take(BatchSize);
        var candidates = await dueQuery.AsNoTracking().Select(x => new { x.Id, x.IntegrationEventId, x.SubscriptionId }).ToListAsync(ct);
        foreach (var candidate in candidates)
        {
            db.ChangeTracker.Clear();
            var delivery = await db.WebhookDeliveries.SingleOrDefaultAsync(x => x.Id == candidate.Id, ct);
            if (delivery is null || delivery.State is not (WebhookDeliveryState.Pending or WebhookDeliveryState.RetryScheduled) || delivery.NextAttemptAt > now)
                continue;
            var enabled = await db.WebhookSubscriptions.AsNoTracking().AnyAsync(x => x.Id == delivery.SubscriptionId && x.IsEnabled, ct);
            if (!enabled) continue;
            var claimToken = Guid.NewGuid();
            try
            {
                delivery.BeginAttempt(_worker, claimToken, now, ClaimLease);
                await db.SaveChangesAsync(ct);
                return (delivery.Id, claimToken, delivery.IntegrationEventId, delivery.SubscriptionId);
            }
            catch (DbUpdateConcurrencyException) { db.ChangeTracker.Clear(); }
        }
        return null;
    }

    private static async Task CompleteClaimAsync(AeroLinkDbContext db, Guid id, Guid claimToken, int statusCode, DateTimeOffset now, CancellationToken ct)
    {
        db.ChangeTracker.Clear();
        var delivery = await db.WebhookDeliveries.SingleOrDefaultAsync(x => x.Id == id, ct);
        if (delivery is null || !delivery.Complete(claimToken, statusCode, now)) return;
        await db.SaveChangesAsync(ct);
    }

    private static async Task FailClaimAsync(AeroLinkDbContext db, Guid id, Guid claimToken, int? statusCode, string error, DateTimeOffset now, CancellationToken ct)
    {
        db.ChangeTracker.Clear();
        var delivery = await db.WebhookDeliveries.SingleOrDefaultAsync(x => x.Id == id, ct);
        if (delivery is null || !delivery.Fail(claimToken, statusCode, error, now)) return;
        await db.SaveChangesAsync(ct);
    }

    private static async Task ReleaseClaimAsync(AeroLinkDbContext db, Guid id, Guid claimToken, DateTimeOffset now, string reason, CancellationToken ct)
    {
        db.ChangeTracker.Clear();
        var delivery = await db.WebhookDeliveries.SingleOrDefaultAsync(x => x.Id == id, ct);
        if (delivery is null || !delivery.ReleaseForShutdown(claimToken, now, reason)) return;
        await db.SaveChangesAsync(ct);
    }
}
