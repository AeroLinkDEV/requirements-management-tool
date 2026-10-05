using System.Text.Json;
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

/// <summary>
/// Records email roots alongside in-app notifications in the authoritative save.
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

        // Admission is captured at the business commit. A later resume cannot enroll disabled-era work.
        // These are plain persisted identities; no transport/configuration/keyring work belongs here.
        var admissions = await (from state in db.NotificationInstallationStates.AsNoTracking()
                                join settings in db.NotificationSettingsRevisions.AsNoTracking() on state.SettingsRevisionId equals settings.Id
                                join epoch in db.NotificationAdmissionEpochs.AsNoTracking() on state.AdmissionEpochId equals epoch.Id
                                where state.SendingEnabled && settings.Mode != NotificationMode.Disabled && settings.Mode == epoch.Mode
                                select new { Epoch = epoch, settings.AllowedEventTypesJson }).Take(2).ToListAsync(ct);
        var admission = admissions.Count == 1 ? admissions[0] : null;
        string[] admittedEvents = [];
        if (admission is not null)
        {
            try { admittedEvents = JsonSerializer.Deserialize<string[]>(admission.AllowedEventTypesJson) ?? []; }
            catch (JsonException) { } // Corrupt administrator settings fail closed without failing business work.
        }

        // SaveChangesAsync(false) and an uncertain save failure intentionally leave the same user
        // notification and its delivery in the change tracker for a caller retry. Do not append a second
        // delivery every time the authoritative save pipeline is re-entered. The database transaction still
        // owns atomicity; this set only prevents duplicate pending inserts for the same tracked unit of work.
        var trackedEmailDeliveries = db.ChangeTracker.Entries<NotificationDelivery>()
            .Where(x => x.State is EntityState.Added or EntityState.Modified
                        && x.Entity.Channel == NotificationChannel.Email)
            .Select(x => x.Entity.NotificationId)
            .ToHashSet();

        foreach (var notification in notifications)
        {
            if (!trackedEmailDeliveries.Add(notification.Id))
                continue;
            addresses.TryGetValue(notification.Recipient, out var account);
            var delivery = new NotificationDelivery(notification.Id, NotificationChannel.Email,
                notification.Recipient, "", now);

            if (notification.Context?.SourceFamily == NotificationSourceFamily.DiagnosticOperation) { }
            else if (account is null || string.IsNullOrWhiteSpace(account.Email))
                delivery.Suppress("The recipient has no email address on their account.", now);
            else if (account.State != AccountState.Active)
                delivery.Suppress($"The recipient's account is {account.State}.", now);
            else if (optedOut.Contains(notification.Recipient))
                delivery.Suppress("The recipient has turned off email notification.", now);

            delivery.HoldAdmission(notification.Context is not null, now);
            if (notification.Context is not null && admission is not null && delivery.Sequence > admission.Epoch.CutoffSequence
                && (notification.Context.SourceFamily == NotificationSourceFamily.DiagnosticOperation || admittedEvents.Contains(notification.Type, StringComparer.Ordinal))
                && delivery.State != NotificationDeliveryState.Suppressed)
                delivery.RecordAdmission(admission.Epoch.Id);
            db.NotificationDeliveries.Add(delivery);
        }
        return notifications.Count;
    }

}
