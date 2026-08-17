using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Common;
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
        var facts = await ReviewFactsAsync(notifications.Values, ct);

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
                await sender.SendAsync(
                    Compose(notification, delivery, links, tokens, facts.GetValueOrDefault(notification.Id)), ct);
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

    /// <summary>
    /// Gathers what a review request has to say beyond its own title, for the notifications in this batch
    /// that are one. Everything else — and everything this cannot resolve — composes exactly as before, so a
    /// notification kind with no template is a plainer message rather than a missing one.
    /// </summary>
    private async Task<Dictionary<Guid, ReviewEmailFacts>> ReviewFactsAsync(
        IReadOnlyCollection<UserNotification> notifications, CancellationToken ct)
    {
        var candidates = notifications.Where(x => x.Type == "ReviewActivated" && x.ArtifactId is not null
            && (x.Route.StartsWith("scr:", StringComparison.OrdinalIgnoreCase)
                || x.Route.StartsWith("swcr:", StringComparison.OrdinalIgnoreCase))).ToList();
        if (candidates.Count == 0) return [];

        var artifactIds = candidates.Select(x => x.ArtifactId!.Value).Distinct().ToList();
        // Every step of every open cycle on these packages, not just the recipient's: "stage 2 of 3" cannot
        // be answered without the other two.
        var steps = await (from step in db.ApprovalSteps.AsNoTracking()
                           join cycle in db.ReviewCycles.AsNoTracking() on step.ReviewCycleId equals cycle.Id
                           join scr in db.SystemChangeRequests.AsNoTracking() on cycle.ChangeRequestId equals scr.Id
                           where artifactIds.Contains(scr.Id) && cycle.State == ReviewCycleState.Active
                           select new
                           {
                               ArtifactId = scr.Id, scr.BaseNumber, scr.Revision, scr.Title, scr.AuthorId,
                               step.ApproverId, step.StageName, step.Authority, step.Position,
                               cycle.Mode, cycle.Sequence, cycle.StartedAt,
                           }).ToListAsync(ct);
        if (steps.Count == 0) return [];

        var counts = (await db.RequirementChanges.AsNoTracking()
                .Where(x => artifactIds.Contains(x.ChangeRequestId))
                .GroupBy(x => new { x.ChangeRequestId, x.Kind })
                .Select(g => new { g.Key.ChangeRequestId, g.Key.Kind, Count = g.Count() })
                .ToListAsync(ct))
            .ToLookup(x => x.ChangeRequestId);

        var authorIds = steps.Select(x => x.AuthorId).Distinct().ToList();
        var authors = (await db.UserAccounts.AsNoTracking()
                .Where(x => authorIds.Contains(x.UserName))
                .Select(x => new { x.UserName, x.DisplayName }).ToListAsync(ct))
            .ToDictionary(x => x.UserName, x => x.DisplayName, StringComparer.OrdinalIgnoreCase);

        var stageCounts = steps.GroupBy(x => x.ArtifactId).ToDictionary(g => g.Key, g => g.Count());
        var resolved = new Dictionary<Guid, ReviewEmailFacts>();
        foreach (var notification in candidates)
        {
            var mine = steps.SingleOrDefault(x => x.ArtifactId == notification.ArtifactId!.Value
                && string.Equals(x.ApproverId, notification.Recipient, StringComparison.OrdinalIgnoreCase));
            // The cycle closed, or the approver was replaced, between raising this and draining the queue.
            // There is no longer a stage to describe, so the message falls back to its plain form.
            if (mine is null) continue;

            var package = counts[notification.ArtifactId!.Value];
            var total = stageCounts.GetValueOrDefault(mine.ArtifactId, 1);
            var stage = string.IsNullOrWhiteSpace(mine.StageName) ? "Review" : mine.StageName;
            resolved[notification.Id] = new ReviewEmailFacts(
                ArtifactNumber.Display(mine.BaseNumber, mine.Revision),
                mine.Title,
                $"{stage} · stage {mine.Position + 1} of {total}",
                string.IsNullOrWhiteSpace(mine.Authority) ? "Reviewer" : mine.Authority,
                $"{mine.Mode} · cycle {mine.Sequence}",
                authors.GetValueOrDefault(mine.AuthorId, mine.AuthorId),
                mine.StartedAt,
                package.Where(x => x.Kind == RequirementChangeKind.Introduce).Sum(x => x.Count),
                package.Where(x => x.Kind == RequirementChangeKind.Modify).Sum(x => x.Count),
                package.Where(x => x.Kind == RequirementChangeKind.Retire).Sum(x => x.Count),
                // The same five days My Work counts an approval overdue against. One convention, so the
                // email and the queue never disagree about when a decision was wanted.
                mine.StartedAt.AddDays(5));
        }
        return resolved;
    }

    internal static EmailMessage Compose(UserNotification notification, NotificationDelivery delivery,
        NotificationLinkBuilder links, UnsubscribeTokenService tokens, ReviewEmailFacts? facts = null)
    {
        var link = links.LinkFor(notification.Route);
        var unsubscribeToken = tokens.Issue(delivery.Recipient);
        var unsubscribeLink = unsubscribeToken is null
            ? null
            : links.UnsubscribeLinkFor(delivery.Recipient, unsubscribeToken);

        if (facts is not null)
            return new EmailMessage(delivery.Address, ReviewEmailTemplate.Subject(facts),
                ReviewEmailTemplate.PlainText(facts, link, unsubscribeLink),
                ReviewEmailTemplate.Html(facts, link, unsubscribeLink));

        return ComposePlain(notification, delivery, link, unsubscribeLink);
    }

    private static EmailMessage ComposePlain(UserNotification notification, NotificationDelivery delivery,
        string? link, string? unsubscribe)
    {
        var body = new System.Text.StringBuilder();
        body.AppendLine(notification.Detail);
        body.AppendLine();

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
