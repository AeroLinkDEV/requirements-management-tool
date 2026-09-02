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
        var now = DateTimeOffset.UtcNow;
        var candidates = await db.WebhookDeliveries.Where(x => x.State == WebhookDeliveryState.Pending).Take(50).ToListAsync(ct);
        candidates.AddRange(await db.WebhookDeliveries.Where(x => x.State == WebhookDeliveryState.RetryScheduled).Take(50).ToListAsync(ct));
        var deliveries = candidates.Where(x=>x.NextAttemptAt<=now).OrderBy(x=>x.NextAttemptAt).Take(20).ToList();
        foreach (var delivery in deliveries)
        {
            var integrationEvent = await db.IntegrationEvents.AsNoTracking().SingleAsync(x => x.Id == delivery.IntegrationEventId, ct);
            var subscription = await db.WebhookSubscriptions.AsNoTracking().SingleAsync(x => x.Id == delivery.SubscriptionId, ct);
            if (!subscription.IsEnabled) continue;
            delivery.BeginAttempt(now); await db.SaveChangesAsync(ct);
            try
            {
                var approved = await destinationPolicy.ValidateAsync(new Uri(subscription.EndpointUrl), allowInsecure, allowPrivate, ct);
                var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
                var envelope = JsonSerializer.Serialize(new { id = integrationEvent.Id, type = integrationEvent.EventType, occurredAt = integrationEvent.OccurredAt, projectId = integrationEvent.ProjectId, aggregate = new { type = integrationEvent.AggregateType, id = integrationEvent.AggregateId }, data = JsonDocument.Parse(integrationEvent.PayloadJson).RootElement });
                var secret = security.UnprotectWebhookSecret(subscription.ProtectedSecret);
                var signature = Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes($"{timestamp}.{envelope}"))).ToLowerInvariant();
                using var request = new HttpRequestMessage(HttpMethod.Post, subscription.EndpointUrl) { Content = new StringContent(envelope, Encoding.UTF8, "application/json") };
                request.Headers.Add("X-AeroLink-Event", integrationEvent.EventType); request.Headers.Add("X-AeroLink-Delivery", delivery.Id.ToString()); request.Headers.Add("X-AeroLink-Timestamp", timestamp); request.Headers.Add("X-AeroLink-Signature", $"v1={signature}");
                request.Options.Set(WebhookConnectionTransport.ApprovedAddressesOption, approved.Addresses);
                using var response = await clientFactory.CreateClient("AeroLinkWebhooks").SendAsync(request, ct);
                if ((int)response.StatusCode is >= 200 and < 300) delivery.Complete((int)response.StatusCode, DateTimeOffset.UtcNow);
                else delivery.Fail((int)response.StatusCode, $"Endpoint returned HTTP {(int)response.StatusCode}.", 5, DateTimeOffset.UtcNow);
            }
            catch (Exception ex) { delivery.Fail(null, ex.Message, 5, DateTimeOffset.UtcNow); }
            await db.SaveChangesAsync(ct);
        }
    }
}
