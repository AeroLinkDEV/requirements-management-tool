using AeroLink.Domain.Documents;
using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Common;
using AeroLink.Domain.Identity;
using AeroLink.Domain.Notifications;
using AeroLink.Domain.Requirements;
using AeroLink.Domain.Verification;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AeroLink.Infrastructure.Notifications;

public sealed record NotificationDispatchResult(int Sent, int Suppressed, int Failed);

internal sealed record ReviewDispatchFacts(
    IReadOnlyDictionary<Guid, ReviewEmailFacts> Facts,
    IReadOnlySet<Guid> ActiveNotificationIds,
    IReadOnlyDictionary<Guid, string> SuppressionReasons);

internal sealed record ReviewStepEmailFact(
    Guid ArtifactId,
    Guid CycleId,
    string BaseNumber,
    int Revision,
    string Title,
    string AuthorId,
    string ApproverId,
    string StageName,
    ReviewStageKind StageKind,
    string Authority,
    int Position,
    ApprovalStepState State,
    ReviewMode Mode,
    int Sequence,
    DateTimeOffset StartedAt,
    DateTimeOffset? DecidedAt,
    bool IsChangeRequest);

internal sealed record DocumentReviewDispatchFacts(
    IReadOnlyDictionary<Guid, DocumentReviewEmailFacts> Facts,
    IReadOnlySet<Guid> ActiveNotificationIds,
    IReadOnlyDictionary<Guid, string> SuppressionReasons);

internal sealed record DocumentReviewStepEmailFact(
    Guid DocumentId,
    Guid RevisionId,
    string DocumentNumber,
    string Title,
    string StewardId,
    int Revision,
    string OwnerId,
    DateTimeOffset? SubmittedAt,
    string ApproverId,
    string StageName,
    ReviewStageKind StageKind,
    string GrantedAuthority,
    int Position,
    int Cycle,
    DateTimeOffset? AssignedAt,
    ManagedDocumentReviewStepState State,
    DateTimeOffset? DecidedAt);

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
        var reviewFacts = await ReviewFactsAsync(notifications.Values, ct);
        var documentFacts = await DocumentReviewFactsAsync(notifications.Values, ct);

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

            if (IsReviewObligationNotification(notification)
                && !reviewFacts.ActiveNotificationIds.Contains(notification.Id))
            {
                // The named person was replaced, or the cycle ended, before the queued delivery reached a
                // relay. Sending the original "you are authorised" wording after that would be a false
                // assertion about a controlled obligation. Retain the deliberate non-send as evidence.
                delivery.Suppress(reviewFacts.SuppressionReasons.GetValueOrDefault(notification.Id,
                    "The review obligation was no longer active when email dispatch ran."), now);
                suppressed++;
                continue;
            }

            if (IsDocumentReviewNotification(notification)
                && !documentFacts.ActiveNotificationIds.Contains(notification.Id))
            {
                delivery.Suppress(documentFacts.SuppressionReasons.GetValueOrDefault(notification.Id,
                    "The document review obligation was no longer active when email dispatch ran."), now);
                suppressed++;
                continue;
            }

            try
            {
                await sender.SendAsync(
                    Compose(notification, delivery, links, tokens, reviewFacts.Facts.GetValueOrDefault(notification.Id),
                        documentFacts.Facts.GetValueOrDefault(notification.Id)), ct);
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
    /// that are one. A queued review notification whose selected, frozen obligation is no longer active is
    /// explicitly suppressed by the caller; it must never fall back to stale "you are authorised" prose.
    /// </summary>
    private async Task<ReviewDispatchFacts> ReviewFactsAsync(
        IReadOnlyCollection<UserNotification> notifications, CancellationToken ct)
    {
        var candidates = notifications.Where(IsReviewObligationNotification).ToList();
        if (candidates.Count == 0) return new(new Dictionary<Guid, ReviewEmailFacts>(), new HashSet<Guid>(),
            new Dictionary<Guid, string>());

        var changeRequestCandidates = candidates.Where(IsChangeRequestReviewNotification).ToList();
        var testChangeCandidates = candidates.Where(IsTestChangeReviewNotification).ToList();
        var changeRequestIds = changeRequestCandidates.Select(x => x.ArtifactId!.Value).Distinct().ToList();
        var testChangeIds = testChangeCandidates.Select(x => x.ArtifactId!.Value).Distinct().ToList();
        // Every step of every open cycle on these packages, not just the recipient's: "stage 2 of 3" cannot
        // be answered without the other two.
        var changeRequestSteps = await (from step in db.ApprovalSteps.AsNoTracking()
                           join cycle in db.ReviewCycles.AsNoTracking() on step.ReviewCycleId equals cycle.Id
                           join scr in db.SystemChangeRequests.AsNoTracking() on cycle.ChangeRequestId equals scr.Id
                           where changeRequestIds.Contains(scr.Id) && cycle.State == ReviewCycleState.Active
                           select new
                           {
                               ArtifactId = scr.Id, CycleId = cycle.Id, scr.BaseNumber, scr.Revision, scr.Title, scr.AuthorId,
                               step.ApproverId, step.StageName, step.StageKind, step.Authority, step.Position, step.State,
                               cycle.Mode, cycle.Sequence, cycle.StartedAt, step.DecidedAt,
                           }).ToListAsync(ct);
        var testChangeSteps = await (from step in db.ApprovalSteps.AsNoTracking()
                                     join cycle in db.ReviewCycles.AsNoTracking() on step.ReviewCycleId equals cycle.Id
                                     join review in db.TestChangeReviews.AsNoTracking() on cycle.TestChangeReviewId equals review.Id
                                     where testChangeIds.Contains(review.Id) && cycle.State == ReviewCycleState.Active
                                     select new
                                     {
                                         ArtifactId = review.Id, CycleId = cycle.Id, review.BaseNumber, review.Revision, review.Title, review.AuthorId,
                                         step.ApproverId, step.StageName, step.StageKind, step.Authority, step.Position, step.State,
                                         cycle.Mode, cycle.Sequence, cycle.StartedAt, step.DecidedAt,
                                     }).ToListAsync(ct);

        var steps = changeRequestSteps.Select(x => new ReviewStepEmailFact(x.ArtifactId, x.CycleId, x.BaseNumber, x.Revision,
                x.Title, x.AuthorId, x.ApproverId, x.StageName, x.StageKind, x.Authority, x.Position, x.State,
                x.Mode, x.Sequence, x.StartedAt, x.DecidedAt, true))
            .Concat(testChangeSteps.Select(x => new ReviewStepEmailFact(x.ArtifactId, x.CycleId, x.BaseNumber, x.Revision,
                x.Title, x.AuthorId, x.ApproverId, x.StageName, x.StageKind, x.Authority, x.Position, x.State,
                x.Mode, x.Sequence, x.StartedAt, x.DecidedAt, false)))
            .ToList();
        if (steps.Count == 0) return new(new Dictionary<Guid, ReviewEmailFacts>(), new HashSet<Guid>(),
            new Dictionary<Guid, string>());

        var counts = (await db.RequirementChanges.AsNoTracking()
                .Where(x => changeRequestIds.Contains(x.ChangeRequestId))
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
        var activeNotificationIds = new HashSet<Guid>();
        var suppressionReasons = new Dictionary<Guid, string>();
        foreach (var notification in candidates)
        {
            var activeSteps = steps.Where(x => x.ArtifactId == notification.ArtifactId!.Value
                    && string.Equals(x.ApproverId, notification.Recipient, StringComparison.OrdinalIgnoreCase)
                    && x.State == ApprovalStepState.Active
                    && ReviewStepActivatedAt(x, steps) == notification.CreatedAt)
                .ToList();
            if (activeSteps.Count == 0) continue;
            if (activeSteps.Count != 1)
            {
                // Notification history names an artifact and recipient, not an ApprovalStep. If a frozen
                // parallel workflow deliberately assigns the same person more than one active stage, guessing
                // which StageKind produced this queued notice would fabricate the email's review/approval ask.
                // Retain a deliberate non-send until a future notification identity can name the exact step.
                suppressionReasons[notification.Id] =
                    "More than one active frozen review obligation matched this notification; its exact stage could not be established.";
                continue;
            }
            var mine = activeSteps[0];

            activeNotificationIds.Add(notification.Id);
            var total = stageCounts.GetValueOrDefault(mine.ArtifactId, 1);
            var stage = string.IsNullOrWhiteSpace(mine.StageName) ? "Review" : mine.StageName;
            resolved[notification.Id] = new ReviewEmailFacts(
                mine.IsChangeRequest || !string.IsNullOrWhiteSpace(mine.BaseNumber)
                    ? ArtifactNumber.Display(mine.BaseNumber, mine.Revision)
                    : "Test change assessment",
                mine.Title,
                $"{stage} · stage {mine.Position + 1} of {total}",
                mine.StageKind,
                string.IsNullOrWhiteSpace(mine.Authority) ? "Reviewer" : mine.Authority,
                $"{mine.Mode} · cycle {mine.Sequence}",
                authors.GetValueOrDefault(mine.AuthorId, mine.AuthorId),
                mine.StartedAt,
                mine.IsChangeRequest ? counts[mine.ArtifactId].Where(x => x.Kind == RequirementChangeKind.Introduce).Sum(x => x.Count) : 0,
                mine.IsChangeRequest ? counts[mine.ArtifactId].Where(x => x.Kind == RequirementChangeKind.Modify).Sum(x => x.Count) : 0,
                mine.IsChangeRequest ? counts[mine.ArtifactId].Where(x => x.Kind == RequirementChangeKind.Retire).Sum(x => x.Count) : 0,
                // The same five days My Work counts an approval overdue against. One convention, so the
                // email and the queue never disagree about when a decision was wanted.
                mine.StartedAt.AddDays(5),
                mine.IsChangeRequest ? "" : "Test change package");
        }
        return new(resolved, activeNotificationIds, suppressionReasons);
    }

    private static bool IsReviewObligationNotification(UserNotification notification) =>
        IsChangeRequestReviewNotification(notification) || IsTestChangeReviewNotification(notification);

    private static bool IsChangeRequestReviewNotification(UserNotification notification) =>
        (notification.Type == "ReviewActivated" || notification.Type == "ApprovalActivated")
        && notification.ArtifactId is not null
        && (notification.Route.StartsWith("scr:", StringComparison.OrdinalIgnoreCase)
            || notification.Route.StartsWith("swcr:", StringComparison.OrdinalIgnoreCase));

    private static bool IsTestChangeReviewNotification(UserNotification notification) =>
        notification.ArtifactId is not null
        && notification.Route.StartsWith("test-change-request:", StringComparison.OrdinalIgnoreCase)
        && (notification.Type == "ReviewActivated" || notification.Type == "TestChangeRequestApprovalRequested");

    private static bool IsDocumentReviewNotification(UserNotification notification) =>
        (notification.Type == "DocumentReviewActivated" || notification.Type == "DocumentApprovalActivated")
        && notification.ArtifactId is not null
        && notification.Route.StartsWith("managed-document:", StringComparison.OrdinalIgnoreCase);

    private static DateTimeOffset? ReviewStepActivatedAt(ReviewStepEmailFact step,
        IReadOnlyCollection<ReviewStepEmailFact> allSteps)
    {
        if (step.Mode == ReviewMode.Parallel || step.Position == 0) return step.StartedAt;
        return allSteps.SingleOrDefault(x => x.CycleId == step.CycleId && x.Position == step.Position - 1)?.DecidedAt;
    }

    /// <summary>
    /// The same gathering for a document review. Kept separate from the change request one rather than
    /// generalised, because the two hang off different aggregates: a change request review lives on a
    /// ReviewCycle, and a document review lives on the revision itself with an integer round. Forcing one
    /// query to serve both would obscure that rather than remove it.
    /// </summary>
    private async Task<DocumentReviewDispatchFacts> DocumentReviewFactsAsync(
        IReadOnlyCollection<UserNotification> notifications, CancellationToken ct)
    {
        var candidates = notifications.Where(IsDocumentReviewNotification).ToList();
        if (candidates.Count == 0) return new(new Dictionary<Guid, DocumentReviewEmailFacts>(),
            new HashSet<Guid>(), new Dictionary<Guid, string>());

        var documentIds = candidates.Select(x => x.ArtifactId!.Value).Distinct().ToList();
        var rows = await (from step in db.ManagedDocumentReviewSteps.AsNoTracking()
                           join revision in db.ManagedDocumentRevisions.AsNoTracking() on step.RevisionId equals revision.Id
                           join document in db.ManagedDocuments.AsNoTracking() on revision.DocumentId equals document.Id
                           where documentIds.Contains(document.Id)
                               && revision.State == ManagedDocumentState.InReview
                           select new
                           {
                               DocumentId = document.Id, RevisionId = revision.Id,
                               document.DocumentNumber, document.Title, document.StewardId,
                               revision.Revision, revision.OwnerId, revision.SubmittedAt,
                               step.ApproverId, step.StageName, step.Kind, step.GrantedAuthority, step.Position, step.Cycle,
                               step.AssignedAt, step.State, step.DecidedAt,
                           }).ToListAsync(ct);
        var steps = rows.Select(x => new DocumentReviewStepEmailFact(x.DocumentId, x.RevisionId,
            x.DocumentNumber, x.Title, x.StewardId, x.Revision, x.OwnerId, x.SubmittedAt,
            x.ApproverId, x.StageName, x.Kind, x.GrantedAuthority, x.Position, x.Cycle,
            x.AssignedAt, x.State, x.DecidedAt)).ToList();
        if (steps.Count == 0) return new(new Dictionary<Guid, DocumentReviewEmailFacts>(),
            new HashSet<Guid>(), new Dictionary<Guid, string>());

        var people = steps.SelectMany(x => new[] { x.StewardId, x.OwnerId }).Distinct().ToList();
        var names = (await db.UserAccounts.AsNoTracking().Where(x => people.Contains(x.UserName))
                .Select(x => new { x.UserName, x.DisplayName }).ToListAsync(ct))
            .ToDictionary(x => x.UserName, x => x.DisplayName, StringComparer.OrdinalIgnoreCase);

        var resolved = new Dictionary<Guid, DocumentReviewEmailFacts>();
        var activeNotificationIds = new HashSet<Guid>();
        var suppressionReasons = new Dictionary<Guid, string>();
        var currentCycleByRevision = steps.GroupBy(x => x.RevisionId)
            .ToDictionary(x => x.Key, x => x.Max(y => y.Cycle));
        foreach (var notification in candidates)
        {
            var activeSteps = steps.Where(x => x.DocumentId == notification.ArtifactId!.Value
                    && x.Cycle == currentCycleByRevision[x.RevisionId]
                    && x.State == ManagedDocumentReviewStepState.Active
                    && string.Equals(x.ApproverId, notification.Recipient, StringComparison.OrdinalIgnoreCase)
                    && DocumentReviewStepActivatedAt(x, steps) == notification.CreatedAt)
                .ToList();
            if (activeSteps.Count == 0) continue;
            if (activeSteps.Count != 1)
            {
                suppressionReasons[notification.Id] =
                    "More than one active frozen document review obligation matched this notification; its exact stage could not be established.";
                continue;
            }
            var mine = activeSteps[0];
            activeNotificationIds.Add(notification.Id);

            var round = steps.Count(x => x.RevisionId == mine.RevisionId && x.Cycle == mine.Cycle);
            var submitted = mine.SubmittedAt ?? notification.CreatedAt;
            var stage = string.IsNullOrWhiteSpace(mine.StageName) ? "Review" : mine.StageName;
            resolved[notification.Id] = new DocumentReviewEmailFacts(
                $"{mine.DocumentNumber}.{mine.Revision:D2}",
                mine.Title,
                $"{stage} · step {mine.Position + 1} of {round}",
                mine.StageKind,
                string.IsNullOrWhiteSpace(mine.GrantedAuthority) ? "Reviewer" : mine.GrantedAuthority,
                names.GetValueOrDefault(mine.OwnerId, mine.OwnerId),
                names.GetValueOrDefault(mine.StewardId, mine.StewardId),
                submitted,
                // The same five-day convention the change request email and My Work already use, so nothing
                // in the product disagrees about when a decision was wanted.
                submitted.AddDays(5));
        }
        return new(resolved, activeNotificationIds, suppressionReasons);
    }

    private static DateTimeOffset? DocumentReviewStepActivatedAt(DocumentReviewStepEmailFact step,
        IReadOnlyCollection<DocumentReviewStepEmailFact> allSteps)
    {
        if (step.Position == 0) return step.AssignedAt;
        return allSteps.SingleOrDefault(x => x.RevisionId == step.RevisionId && x.Cycle == step.Cycle
            && x.Position == step.Position - 1)?.DecidedAt;
    }

    internal static EmailMessage Compose(UserNotification notification, NotificationDelivery delivery,
        NotificationLinkBuilder links, UnsubscribeTokenService tokens, ReviewEmailFacts? facts = null,
        DocumentReviewEmailFacts? documentFacts = null)
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

        if (documentFacts is not null)
            return new EmailMessage(delivery.Address, DocumentReviewEmailTemplate.Subject(documentFacts),
                DocumentReviewEmailTemplate.PlainText(documentFacts, link, unsubscribeLink),
                DocumentReviewEmailTemplate.Html(documentFacts, link, unsubscribeLink));

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
