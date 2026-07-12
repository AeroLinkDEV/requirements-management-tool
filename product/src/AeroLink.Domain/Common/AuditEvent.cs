namespace AeroLink.Domain.Common;

public sealed class AuditEvent
{
    private AuditEvent() { }

    public AuditEvent(Guid aggregateId, string eventType, string actorId, string detail, DateTimeOffset occurredAt)
    {
        Id = Guid.NewGuid();
        AggregateId = aggregateId;
        EventType = eventType;
        ActorId = actorId;
        Detail = detail;
        OccurredAt = occurredAt;
    }

    public Guid Id { get; private set; }
    public Guid AggregateId { get; private set; }
    public string EventType { get; private set; } = string.Empty;
    public string ActorId { get; private set; } = string.Empty;
    public string Detail { get; private set; } = string.Empty;
    public DateTimeOffset OccurredAt { get; private set; }
}
