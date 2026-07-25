using AeroLink.Domain.Common;

namespace AeroLink.Domain.Notifications;

public enum NotificationChannel { Email }

/// <summary>
/// Pending is written in the same transaction as the work it announces; every other state is the
/// dispatcher's account of what happened afterwards. Suppressed means the recipient opted out or has no
/// address — a deliberate non-send, which is different from a failure and must not look like one.
/// </summary>
public enum NotificationDeliveryState { Pending, Sent, Failed, Suppressed }

/// <summary>
/// One attempt to carry an in-app notification to somebody outside the product.
///
/// Deliveries are written as part of the transaction that raised the notification and sent afterwards by a
/// background dispatcher. Sending inline would let a slow or unreachable mail relay fail an approval
/// submission, and sending before commit would announce work that a rollback then erased. Neither is
/// acceptable in a system whose claim is that the record and the notice agree.
/// </summary>
public sealed class NotificationDelivery
{
    private NotificationDelivery() { }

    public NotificationDelivery(Guid notificationId, NotificationChannel channel, string recipient,
        string address, DateTimeOffset now)
    {
        if (notificationId == Guid.Empty) throw new DomainException("A delivery must belong to a notification.");
        if (string.IsNullOrWhiteSpace(recipient)) throw new DomainException("A delivery recipient is required.");
        Id = Guid.NewGuid();
        NotificationId = notificationId;
        Channel = channel;
        Recipient = recipient.Trim().ToLowerInvariant();
        Address = address.Trim();
        State = NotificationDeliveryState.Pending;
        Sequence = now.UtcTicks;
        CreatedAt = now;
        UpdatedAt = now;
    }

    public Guid Id { get; private set; }
    /// <summary>
    /// Insertion order as plain ticks. A queue has to drain oldest-first or a backlog starves the notices
    /// that have waited longest, and the obvious ordering key cannot be used: SQLite refuses to ORDER BY a
    /// DateTimeOffset, which this repository has already been caught by once. Ticks sort identically on
    /// both providers and need no database-generated value, which SQLite only offers for integer keys.
    /// </summary>
    public long Sequence { get; private set; }
    public Guid NotificationId { get; private set; }
    public NotificationChannel Channel { get; private set; }
    /// <summary>The AeroLink user name, retained so a delivery is attributable even if the address changes.</summary>
    public string Recipient { get; private set; } = "";
    public string Address { get; private set; } = "";
    public NotificationDeliveryState State { get; private set; }
    public int Attempts { get; private set; }
    /// <summary>Why the last attempt failed, or why the send was deliberately not made.</summary>
    public string LastError { get; private set; } = "";
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public DateTimeOffset? CompletedAt { get; private set; }

    public void MarkSent(DateTimeOffset now)
    {
        Attempts++;
        State = NotificationDeliveryState.Sent;
        LastError = "";
        CompletedAt = now;
        UpdatedAt = now;
    }

    /// <summary>
    /// Records a failed attempt. A delivery is only abandoned after <paramref name="maximumAttempts"/>,
    /// because the common failure is a mail relay that is briefly unavailable rather than a wrong address,
    /// and abandoning on the first refusal would lose the notice.
    /// </summary>
    public void MarkFailed(string error, int maximumAttempts, DateTimeOffset now)
    {
        Attempts++;
        LastError = (error ?? "").Trim();
        UpdatedAt = now;
        if (Attempts < maximumAttempts) return;
        State = NotificationDeliveryState.Failed;
        CompletedAt = now;
    }

    /// <summary>A deliberate non-send: the recipient opted out, or has no usable address.</summary>
    public void Suppress(string reason, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(reason)) throw new DomainException("Suppressing a delivery requires a reason.");
        State = NotificationDeliveryState.Suppressed;
        LastError = reason.Trim();
        CompletedAt = now;
        UpdatedAt = now;
    }
}

/// <summary>
/// A person's choice about being emailed. Absent a record the answer is yes, because the product's value
/// depends on approvals reaching people; opting out is an explicit act that is recorded with its time.
/// </summary>
public sealed class NotificationPreference
{
    private NotificationPreference() { }

    public NotificationPreference(string recipient, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(recipient)) throw new DomainException("A notification preference needs a recipient.");
        Id = Guid.NewGuid();
        Recipient = recipient.Trim().ToLowerInvariant();
        EmailEnabled = true;
        UpdatedAt = now;
    }

    public Guid Id { get; private set; }
    public string Recipient { get; private set; } = "";
    public bool EmailEnabled { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public DateTimeOffset? OptedOutAt { get; private set; }

    public void SetEmailEnabled(bool enabled, DateTimeOffset now)
    {
        EmailEnabled = enabled;
        OptedOutAt = enabled ? null : now;
        UpdatedAt = now;
    }
}
