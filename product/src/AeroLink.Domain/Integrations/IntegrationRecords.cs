using AeroLink.Domain.Common;
using System.Text.Json;

namespace AeroLink.Domain.Integrations;

public enum ServiceIdentityState { Active, Revoked }
public enum IntegrationEventState { Pending, Dispatched, Failed }
public enum WebhookDeliveryState { Pending, Delivering, Delivered, RetryScheduled, DeadLettered }

public sealed record WebhookDeliveryAttempt(
    int Attempt,
    Guid? ClaimToken,
    string? Worker,
    DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt,
    string Outcome,
    int? ResponseStatusCode,
    string? Error);

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
    public const int MaximumAttempts = 5;
    public const int MaximumAttemptHistory = 20;

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
    public Guid? ClaimToken { get; private set; }
    public string? ClaimedBy { get; private set; }
    public DateTimeOffset? ClaimedAt { get; private set; }
    public DateTimeOffset? ClaimExpiresAt { get; private set; }
    public string AttemptHistoryJson { get; private set; } = "[]";
    public long Version { get; private set; }

    public void BeginAttempt(string worker, Guid claimToken, DateTimeOffset now, TimeSpan lease)
    {
        if (State == WebhookDeliveryState.Delivered) throw new DomainException("A delivered webhook cannot be redelivered without replay.");
        if (State == WebhookDeliveryState.Delivering) throw new DomainException("A webhook delivery is already claimed.");
        if (claimToken == Guid.Empty) throw new DomainException("A webhook claim token is required.");
        if (string.IsNullOrWhiteSpace(worker)) throw new DomainException("A claiming webhook worker is required.");
        if (lease <= TimeSpan.Zero) throw new DomainException("A webhook claim lease must be positive.");
        State = WebhookDeliveryState.Delivering; AttemptCount++; UpdatedAt = now;
        ClaimToken = claimToken; ClaimedBy = worker.Trim(); ClaimedAt = now; ClaimExpiresAt = now + lease;
        ResponseStatusCode = null; LastError = null;
        RecordAttempt(new WebhookDeliveryAttempt(AttemptCount, claimToken, ClaimedBy, now, null, "Delivering", null, null));
        Touch();
    }

    public bool ClaimExpired(DateTimeOffset now) => State == WebhookDeliveryState.Delivering && ClaimExpiresAt is not null && ClaimExpiresAt <= now;

    public bool RecoverExpired(DateTimeOffset now)
    {
        if (!ClaimExpired(now)) throw new DomainException("Only an expired webhook claim can be recovered.");
        var token = ClaimToken ?? Guid.Empty;
        var error = $"Worker claim expired after attempt {AttemptCount}.";
        CompleteAttempt(token, now, AttemptCount >= MaximumAttempts ? "DeadLettered" : "RetryScheduled", null, error);
        ReleaseClaim();
        LastError = error; UpdatedAt = now;
        if (AttemptCount >= MaximumAttempts)
        {
            State = WebhookDeliveryState.DeadLettered;
        }
        else
        {
            State = WebhookDeliveryState.RetryScheduled;
            NextAttemptAt = now.AddMinutes(Math.Min(60, Math.Pow(2, Math.Max(0, AttemptCount - 1))));
        }
        Touch();
        return true;
    }

    public bool Complete(Guid claimToken, int statusCode, DateTimeOffset now)
    {
        if (!OwnsClaim(claimToken)) return false;
        State = WebhookDeliveryState.Delivered; ResponseStatusCode = statusCode; LastError = null; DeliveredAt = now; UpdatedAt = now;
        CompleteAttempt(claimToken, now, "Delivered", statusCode, null);
        ReleaseClaim();
        Touch();
        return true;
    }

    public bool Fail(Guid claimToken, int? statusCode, string error, DateTimeOffset now)
    {
        if (!OwnsClaim(claimToken)) return false;
        ResponseStatusCode = statusCode; LastError = error.Length > 2000 ? error[..2000] : error; UpdatedAt = now;
        var deadLetter = AttemptCount >= MaximumAttempts;
        CompleteAttempt(claimToken, now, deadLetter ? "DeadLettered" : "RetryScheduled", statusCode, LastError);
        ReleaseClaim();
        if (deadLetter) State = WebhookDeliveryState.DeadLettered;
        else { State = WebhookDeliveryState.RetryScheduled; NextAttemptAt = now.AddMinutes(Math.Min(60, Math.Pow(2, Math.Max(0, AttemptCount - 1)))); }
        Touch();
        return true;
    }

    public bool ReleaseForShutdown(Guid claimToken, DateTimeOffset now, string reason = "Worker shut down before finishing; returned to the queue.")
    {
        if (!OwnsClaim(claimToken)) return false;
        CompleteAttempt(claimToken, now, "Cancelled", null, reason);
        // A claim is an ownership lease, not a failed receiver attempt. Returning it before send must
        // leave the ordinary five-failure budget available while retaining the physical cancelled attempt.
        AttemptCount = Math.Max(0, AttemptCount - 1);
        ReleaseClaim(); State = WebhookDeliveryState.RetryScheduled; LastError = reason; NextAttemptAt = now; UpdatedAt = now;
        Touch();
        return true;
    }

    public void Replay(DateTimeOffset now)
    {
        if (State == WebhookDeliveryState.Delivering && !ClaimExpired(now)) throw new DomainException("A live webhook delivery claim cannot be replayed.");
        if (State == WebhookDeliveryState.Delivering && ClaimToken is Guid token)
            CompleteAttempt(token, now, "Replayed", null, "Expired webhook claim replayed by an operator.");
        State = WebhookDeliveryState.Pending; AttemptCount = 0; ResponseStatusCode = null; LastError = null; DeliveredAt = null; NextAttemptAt = now; UpdatedAt = now;
        ReleaseClaim();
        Touch();
    }

    public IReadOnlyList<WebhookDeliveryAttempt> AttemptHistory() =>
        JsonSerializer.Deserialize<WebhookDeliveryAttempt[]>(AttemptHistoryJson) ?? [];

    private bool OwnsClaim(Guid claimToken) => State == WebhookDeliveryState.Delivering && ClaimToken == claimToken;
    private void ReleaseClaim() { ClaimToken = null; ClaimedBy = null; ClaimedAt = null; ClaimExpiresAt = null; }
    private void CompleteAttempt(Guid claimToken, DateTimeOffset now, string outcome, int? statusCode, string? error)
    {
        var entries = AttemptHistory().ToList();
        var index = entries.FindLastIndex(x => x.ClaimToken == claimToken);
        if (index >= 0) entries[index] = entries[index] with { FinishedAt = now, Outcome = outcome, ResponseStatusCode = statusCode, Error = error };
        AttemptHistoryJson = JsonSerializer.Serialize(entries.TakeLast(MaximumAttemptHistory));
    }
    private void RecordAttempt(WebhookDeliveryAttempt attempt)
    {
        var entries = AttemptHistory().ToList(); entries.Add(attempt);
        AttemptHistoryJson = JsonSerializer.Serialize(entries.TakeLast(MaximumAttemptHistory));
    }
    private void Touch() => Version++;
}
