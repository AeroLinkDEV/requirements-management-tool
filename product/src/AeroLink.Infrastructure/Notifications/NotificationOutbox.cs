using AeroLink.Domain.Identity;
using AeroLink.Domain.Notifications;
using AeroLink.Domain.Requirements;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AeroLink.Infrastructure.Notifications;

public sealed record NotificationDispatchResult(int Sent, int Suppressed, int Failed);

/// <summary>
/// Queues deliveries for in-app notifications, and drains that queue.
///
/// Queueing happens inside the transaction that raised the notification, so a notice cannot be announced
/// for work that a rollback then erased, and cannot be lost because the process died between committing
/// the work and telling anyone. Draining happens afterwards and out of band, so an unreachable mail relay
/// slows nothing down and fails nobody's approval submission.
/// </summary>
public sealed class NotificationOutbox(AeroLinkDbContext db)
{
    /// <summary>
    /// Queues an email delivery for each notification given. Call this with the notifications added in the
    /// current unit of work, before saving; nothing is sent here.
    ///
    /// A recipient with no address, or one who has opted out, still gets a delivery row — suppressed, with
    /// the reason recorded. Writing nothing would leave no evidence that a person was meant to be told and
    /// deliberately was not, and that evidence is the point.
    /// </summary>
    public async Task<int> QueueEmailAsync(IReadOnlyCollection<UserNotification> notifications,
        DateTimeOffset now, CancellationToken ct)
    {
        if (notifications.Count == 0) return 0;
        var recipients = notifications.Select(x => x.Recipient).Distinct().ToList();

        var accounts = await db.UserAccounts.AsNoTracking()
            .Where(x => recipients.Contains(x.UserName))
            .Select(x => new { x.UserName, x.Email, x.State })
            .ToListAsync(ct);
        var addresses = accounts.ToDictionary(x => x.UserName, StringComparer.OrdinalIgnoreCase);

        var optedOut = (await db.NotificationPreferences.AsNoTracking()
                .Where(x => recipients.Contains(x.Recipient) && !x.EmailEnabled)
                .Select(x => x.Recipient).ToListAsync(ct))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var notification in notifications)
        {
            addresses.TryGetValue(notification.Recipient, out var account);
            var delivery = new NotificationDelivery(notification.Id, NotificationChannel.Email,
                notification.Recipient, account?.Email ?? "", now);

            if (account is null || string.IsNullOrWhiteSpace(account.Email))
                delivery.Suppress("The recipient has no email address on their account.", now);
            else if (account.State != AccountState.Active)
                delivery.Suppress($"The recipient's account is {account.State}.", now);
            else if (optedOut.Contains(notification.Recipient))
                delivery.Suppress("The recipient has turned off email notification.", now);

            db.NotificationDeliveries.Add(delivery);
        }
        return notifications.Count;
    }

    /// <summary>
    /// Sends what is waiting. Returns counts rather than throwing, because one bad address must not stop
    /// the rest of the queue, and the caller is a background loop that has nobody to report an exception to.
    /// </summary>
    public async Task<NotificationDispatchResult> DispatchPendingAsync(IEmailSender sender,
        NotificationLinkBuilder links, UnsubscribeTokenService tokens, int batchSize, int maximumAttempts,
        DateTimeOffset now, CancellationToken ct)
    {
        if (!sender.IsConfigured) return new(0, 0, 0);

        var pending = await db.NotificationDeliveries
            .Where(x => x.State == NotificationDeliveryState.Pending && x.Channel == NotificationChannel.Email)
            .OrderBy(x => x.Sequence)
            .Take(batchSize)
            .ToListAsync(ct);
        if (pending.Count == 0) return new(0, 0, 0);

        var notificationIds = pending.Select(x => x.NotificationId).Distinct().ToList();
        var notifications = (await db.UserNotifications.AsNoTracking()
                .Where(x => notificationIds.Contains(x.Id)).ToListAsync(ct))
            .ToDictionary(x => x.Id);

        var sent = 0; var failed = 0; var suppressed = 0;
        foreach (var delivery in pending)
        {
            if (!notifications.TryGetValue(delivery.NotificationId, out var notification))
            {
                // The notification it announced is gone. There is nothing to say, and retrying cannot help.
                delivery.Suppress("The notification this delivery referred to no longer exists.", now);
                suppressed++;
                continue;
            }

            try
            {
                await sender.SendAsync(Compose(notification, delivery, links, tokens), ct);
                delivery.MarkSent(now);
                sent++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                delivery.MarkFailed(ex.Message, maximumAttempts, now);
                failed++;
            }
        }

        await db.SaveChangesAsync(ct);
        return new(sent, suppressed, failed);
    }

    internal static EmailMessage Compose(UserNotification notification, NotificationDelivery delivery,
        NotificationLinkBuilder links, UnsubscribeTokenService tokens)
    {
        var body = new System.Text.StringBuilder();
        body.AppendLine(notification.Detail);
        body.AppendLine();

        var link = links.LinkFor(notification.Route);
        if (link is not null)
        {
            body.AppendLine("Open it here:");
            body.AppendLine(link);
        }
        else
        {
            // Better to say why there is no link than to print a broken one.
            body.AppendLine("Sign in to AeroLink to act on this. (No public address is configured for this");
            body.AppendLine("deployment, so a direct link could not be included.)");
        }

        var token = tokens.Issue(delivery.Recipient);
        var unsubscribe = token is null ? null : links.UnsubscribeLinkFor(delivery.Recipient, token);
        if (unsubscribe is not null)
        {
            body.AppendLine();
            body.AppendLine("---");
            body.AppendLine("These messages tell you when a decision is waiting on you. To stop receiving them:");
            body.AppendLine(unsubscribe);
        }

        return new EmailMessage(delivery.Address, $"AeroLink: {notification.Title}", body.ToString());
    }
}
