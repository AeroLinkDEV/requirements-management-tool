namespace AeroLink.Domain.Common;

/// <summary>
/// One append-only audit event: what happened, who did it, and — separately — the technical evidence.
///
/// `Detail` was the only field, so anything a consumer might later need was serialized into it. Check-in
/// wrote a JSON payload of session and evidence identifiers, adapter names, snapshot hashes and aggregate
/// versions, and the timeline faithfully rendered that as the audit narrative: a reader looking for who
/// changed what got a wall of GUIDs. Worse, a free-form prose field had quietly become a schema, so changing
/// an internal payload shape changed the audit contract, and a consumer wanting a value out of it had to
/// parse prose.
///
/// The two are now separate concerns. `Detail` is the sentence a person reads. `EvidenceJson` is the
/// structured record, present only where there is one. `SchemaVersion` says which shape the evidence is in,
/// so presentation is deterministic rather than inferred.
/// </summary>
public sealed class AuditEvent
{
    /// <summary>Events written before evidence was separated carry this and no <see cref="EvidenceJson"/>.</summary>
    public const int LegacySchemaVersion = 0;
    public const int CurrentSchemaVersion = 1;

    private AuditEvent() { }

    public AuditEvent(Guid aggregateId, string eventType, string actorId, string detail, DateTimeOffset occurredAt,
        string? evidenceJson = null, int? schemaVersion = null)
    {
        Id = Guid.NewGuid();
        AggregateId = aggregateId;
        EventType = eventType;
        ActorId = actorId;
        Detail = detail;
        OccurredAt = occurredAt;
        EvidenceJson = evidenceJson;
        // An event that carries structured evidence is by definition written against the current contract.
        SchemaVersion = schemaVersion ?? (evidenceJson is null ? LegacySchemaVersion : CurrentSchemaVersion);
    }

    public Guid Id { get; private set; }
    public Guid AggregateId { get; private set; }
    public string EventType { get; private set; } = string.Empty;
    public string ActorId { get; private set; } = string.Empty;
    /// <summary>The human summary. Never a serialized payload.</summary>
    public string Detail { get; private set; } = string.Empty;
    /// <summary>Structured technical evidence, or null when the event has none.</summary>
    public string? EvidenceJson { get; private set; }
    public int SchemaVersion { get; private set; }
    public DateTimeOffset OccurredAt { get; private set; }
}
