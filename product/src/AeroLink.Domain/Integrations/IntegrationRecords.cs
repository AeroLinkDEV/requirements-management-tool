using AeroLink.Domain.Common;

namespace AeroLink.Domain.Integrations;

public enum ServiceIdentityState { Active, Revoked }
public enum IntegrationEventState { Pending, Dispatched, Failed }
public enum WebhookDeliveryState { Pending, Delivering, Delivered, RetryScheduled, DeadLettered }

public sealed class IntegrationServiceIdentity
{
    private IntegrationServiceIdentity() { }
    public IntegrationServiceIdentity(Guid projectId, string name, string clientId, string apiKeyHash, string scopesJson, string actor, DateTimeOffset now)
    {
        Id = Guid.NewGuid(); ProjectId = projectId; Name = Required(name); ClientId = Required(clientId).ToLowerInvariant();
        ApiKeyHash = Required(apiKeyHash).ToLowerInvariant(); ScopesJson = Required(scopesJson); State = ServiceIdentityState.Active;
        CreatedBy = Required(actor).ToLowerInvariant(); CreatedAt = now;
    }
    public Guid Id { get; private set; }
    public Guid ProjectId { get; private set; }
    public string Name { get; private set; } = "";
    public string ClientId { get; private set; } = "";
    public string ApiKeyHash { get; private set; } = "";
    public string ScopesJson { get; private set; } = "[]";
    public ServiceIdentityState State { get; private set; }
    public string CreatedBy { get; private set; } = "";
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? LastUsedAt { get; private set; }
    public DateTimeOffset? RevokedAt { get; private set; }
    public string? RevokedBy { get; private set; }
    public void RecordUse(DateTimeOffset now) { if (State == ServiceIdentityState.Active) LastUsedAt = now; }
    public void Revoke(string actor, DateTimeOffset now) { if (State == ServiceIdentityState.Revoked) return; State = ServiceIdentityState.Revoked; RevokedAt = now; RevokedBy = Required(actor).ToLowerInvariant(); }
    private static string Required(string value) => string.IsNullOrWhiteSpace(value) ? throw new DomainException("A required service-identity value is missing.") : value.Trim();
}

public sealed class WebhookSubscription
{
    private WebhookSubscription() { }
    public WebhookSubscription(Guid projectId, string name, string endpointUrl, string eventTypesJson, string protectedSecret, string actor, DateTimeOffset now)
    {
        if (!Uri.TryCreate(endpointUrl, UriKind.Absolute, out var endpoint) || endpoint.Scheme is not ("https" or "http"))
            throw new DomainException("A webhook endpoint must be an absolute HTTP or HTTPS URL.");
        Id = Guid.NewGuid(); ProjectId = projectId; Name = Required(name); EndpointUrl = endpoint.ToString(); EventTypesJson = Required(eventTypesJson);
        ProtectedSecret = Required(protectedSecret); IsEnabled = true; CreatedBy = Required(actor).ToLowerInvariant(); CreatedAt = now; UpdatedAt = now;
    }
    public Guid Id { get; private set; }
    public Guid ProjectId { get; private set; }
    public string Name { get; private set; } = "";
    public string EndpointUrl { get; private set; } = "";
    public string EventTypesJson { get; private set; } = "[]";
    public string ProtectedSecret { get; private set; } = "";
    public bool IsEnabled { get; private set; }
    public string CreatedBy { get; private set; } = "";
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public void SetEnabled(bool enabled, DateTimeOffset now) { IsEnabled = enabled; UpdatedAt = now; }
    private static string Required(string value) => string.IsNullOrWhiteSpace(value) ? throw new DomainException("A required webhook value is missing.") : value.Trim();
}

public sealed class IntegrationEvent
{
    private IntegrationEvent() { }
    public IntegrationEvent(Guid projectId, string eventType, string aggregateType, Guid aggregateId, string payloadJson, string actor, DateTimeOffset now, string? idempotencyKey = null)
    {
        Id = Guid.NewGuid(); ProjectId = projectId; EventType = Required(eventType); AggregateType = Required(aggregateType); AggregateId = aggregateId;
        PayloadJson = Required(payloadJson); Actor = Required(actor); OccurredAt = now; State = IntegrationEventState.Pending; IdempotencyKey = string.IsNullOrWhiteSpace(idempotencyKey) ? null : idempotencyKey.Trim();
    }
    public Guid Id { get; private set; }
    public Guid ProjectId { get; private set; }
    public string EventType { get; private set; } = "";
    public string AggregateType { get; private set; } = "";
    public Guid AggregateId { get; private set; }
    public string PayloadJson { get; private set; } = "{}";
    public string Actor { get; private set; } = "";
    public string? IdempotencyKey { get; private set; }
    public DateTimeOffset OccurredAt { get; private set; }
    public IntegrationEventState State { get; private set; }
    public DateTimeOffset? DispatchedAt { get; private set; }
    public string? LastError { get; private set; }
    public void MarkDispatched(DateTimeOffset now) { State = IntegrationEventState.Dispatched; DispatchedAt = now; LastError = null; }
    public void MarkFailed(string error) { State = IntegrationEventState.Failed; LastError = error.Length > 2000 ? error[..2000] : error; }
    private static string Required(string value) => string.IsNullOrWhiteSpace(value) ? throw new DomainException("A required integration-event value is missing.") : value.Trim();
}

public sealed class WebhookDelivery
{
    private WebhookDelivery() { }
    public WebhookDelivery(Guid projectId, Guid integrationEventId, Guid subscriptionId, DateTimeOffset now)
    {
        Id = Guid.NewGuid(); ProjectId = projectId; IntegrationEventId = integrationEventId; SubscriptionId = subscriptionId;
        State = WebhookDeliveryState.Pending; NextAttemptAt = now; CreatedAt = now; UpdatedAt = now;
    }
    public Guid Id { get; private set; }
    public Guid ProjectId { get; private set; }
    public Guid IntegrationEventId { get; private set; }
    public Guid SubscriptionId { get; private set; }
    public WebhookDeliveryState State { get; private set; }
    public int AttemptCount { get; private set; }
    public DateTimeOffset NextAttemptAt { get; private set; }
    public int? ResponseStatusCode { get; private set; }
    public string? LastError { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public DateTimeOffset? DeliveredAt { get; private set; }
    public void BeginAttempt(DateTimeOffset now) { if (State == WebhookDeliveryState.Delivered) throw new DomainException("A delivered webhook cannot be redelivered without replay."); State = WebhookDeliveryState.Delivering; AttemptCount++; UpdatedAt = now; }
    public void Complete(int statusCode, DateTimeOffset now) { State = WebhookDeliveryState.Delivered; ResponseStatusCode = statusCode; LastError = null; DeliveredAt = now; UpdatedAt = now; }
    public void Fail(int? statusCode, string error, int maximumAttempts, DateTimeOffset now)
    {
        ResponseStatusCode = statusCode; LastError = error.Length > 2000 ? error[..2000] : error; UpdatedAt = now;
        if (AttemptCount >= maximumAttempts) { State = WebhookDeliveryState.DeadLettered; return; }
        State = WebhookDeliveryState.RetryScheduled; NextAttemptAt = now.AddMinutes(Math.Min(60, Math.Pow(2, Math.Max(0, AttemptCount - 1))));
    }
    public void Replay(DateTimeOffset now) { State = WebhookDeliveryState.Pending; AttemptCount = 0; ResponseStatusCode = null; LastError = null; DeliveredAt = null; NextAttemptAt = now; UpdatedAt = now; }
}
