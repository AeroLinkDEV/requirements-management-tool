using System.Diagnostics;
using System.Text;
using System.Text.Json;
using AeroLink.Domain.Notifications;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MimeKit;

namespace AeroLink.Infrastructure.Notifications;

/// <summary>SMTP-specific claims and receipts; never a business-save or general background-job boundary.</summary>
public sealed class NotificationDispatcher(AeroLinkDbContext db, NotificationSettingsResolver resolver,
    NotificationInstallationAuthority authority, NotificationContentProtection protection,
    NotificationEligibility eligibility, NotificationSmtpTransport transport, UnsubscribeTokenService tokens,
    IServiceScopeFactory scopes)
{
    public async Task<NotificationDispatchResult> DispatchAsync(int batchSize, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        await RecoverExpiredClaimsAsync(now, ct);
        var settings = await resolver.ResolveAsync(ct);
        // Revocation is visible even when external policy is now Disabled or unavailable. Historical
        // acceptance remains untouched; only work that has not crossed the durable gate is held.
        var externalGeneration = settings.Policy?.SendGeneration ?? Guid.Empty;
        await db.NotificationDeliveryGenerations.Where(x => (x.Mode == NotificationMode.Live || x.Mode == NotificationMode.ControlledTest)
            && x.SendGeneration != externalGeneration && (x.State == NotificationGenerationState.Pending
                || x.State == NotificationGenerationState.RetryDue || x.State == NotificationGenerationState.ConfigBlocked || x.State == NotificationGenerationState.Claimed))
            .ExecuteUpdateAsync(x => x.SetProperty(p => p.State, NotificationGenerationState.HeldAdmission)
                .SetProperty(p => p.SafeCode, "SendAuthorityRevoked").SetProperty(p => p.ClaimToken, (Guid?)null)
                .SetProperty(p => p.Version, p => p.Version + 1), ct);
        // Activation is future-only. A new epoch cannot silently resume older admitted work.
        if (settings.AdmissionEnabled && settings.AdmissionEpochId is Guid activeEpoch)
            await db.NotificationDeliveryGenerations.Where(x => x.AdmissionEpochId != activeEpoch
                && (x.State == NotificationGenerationState.Pending || x.State == NotificationGenerationState.RetryDue
                    || x.State == NotificationGenerationState.ConfigBlocked || x.State == NotificationGenerationState.Claimed))
                .ExecuteUpdateAsync(x => x.SetProperty(p => p.State, NotificationGenerationState.HeldAdmission)
                    .SetProperty(p => p.SafeCode, "AdmissionEpochChanged").SetProperty(p => p.ClaimToken, (Guid?)null)
                    .SetProperty(p => p.Version, p => p.Version + 1), ct);
        await CompleteFencedPreparationsAsync(ct);
        if (!settings.CanSend || !settings.AdmissionEnabled || settings.SettingsId == Guid.Empty) return new(0, 0, 0);
        await CreateAdmittedGenerationsAsync(settings, Math.Clamp(batchSize, 1, 50), ct);
        var effectiveHash = EffectiveSettingsHash(settings);
        var due = await db.NotificationDeliveryGenerations.AsNoTracking()
            .Where(x => (x.State == NotificationGenerationState.Pending || x.State == NotificationGenerationState.RetryDue || x.State == NotificationGenerationState.ConfigBlocked
                    && x.BlockedEffectiveSettingsHash != effectiveHash && x.SafeCode != "RecipientAddressChanged" && x.SafeCode != "MessageSettingsChangedReissueRequired")
                && x.AdmissionEpochId == settings.AdmissionEpochId && x.Mode == settings.Mode && x.DueTicks <= now.UtcTicks)
            .OrderBy(x => x.DueTicks).Take(Math.Clamp(batchSize, 1, 50)).ToListAsync(ct);
        var sent = 0; var suppressed = 0; var failed = 0;
        foreach (var candidate in due)
        {
            if (ct.IsCancellationRequested) break;
            now = DateTimeOffset.UtcNow;
            settings = await resolver.ResolveAsync(ct);
            if (!settings.CanSend || !settings.AdmissionEnabled || settings.Mode != candidate.Mode || settings.SendGeneration != candidate.SendGeneration || settings.AdmissionEpochId != candidate.AdmissionEpochId) continue;
            if (candidate.DeadlineTicks <= now.UtcTicks || (candidate.TransientFailures >= candidate.MaximumAttempts || candidate.Attempts >= candidate.MaximumPhysicalAttempts))
            { await SetHeldAsync(candidate.Id, NotificationGenerationState.RetryExhausted, "RetryWindowExhausted", ct, settings); continue; }
            if (candidate.State == NotificationGenerationState.ConfigBlocked && candidate.CurrentAttemptId is Guid blockedAttempt
                && await db.NotificationPhysicalAttempts.AnyAsync(x => x.Id == blockedAttempt && x.SettingsRevisionId == settings.SettingsId
                    && x.PolicyHash == settings.PolicyHash && x.EffectiveSettingsHash == EffectiveSettingsHash(settings) && x.Outcome == NotificationAttemptOutcome.ConfigBlocked, ct)) continue;
            if (!await PredecessorsQuiescentAsync(candidate, ct)) continue;
            var context = await (from root in db.NotificationDeliveries.AsNoTracking()
                                 join bound in db.NotificationContexts.AsNoTracking() on root.NotificationId equals bound.NotificationId
                                 where root.Id == candidate.DeliveryId select bound).SingleOrDefaultAsync(ct);
            if (context is null) { await SetHeldAsync(candidate.Id, NotificationGenerationState.HeldAdmission, "LegacyUnbound", ct, settings); continue; }
            if (context.SourceFamily != NotificationSourceFamily.DiagnosticOperation && !settings.EnabledEventTypes.Contains(context.EventType, StringComparer.Ordinal))
            { await SetHeldAsync(candidate.Id, NotificationGenerationState.Suppressed, "EventTypeDisabled", ct, settings); continue; }
            var allowed = await eligibility.EvaluateAsync(context, ct);
            if (!allowed.Eligible)
            { await SetHeldAsync(candidate.Id, NotificationGenerationState.Suppressed, allowed.SafeCode, ct, settings); suppressed++; continue; }
            var diagnostic = context.SourceFamily == NotificationSourceFamily.DiagnosticOperation;
            var address = protection.Unprotect(candidate.ProtectedAddress);
            if (settings.Mode == NotificationMode.Live && !diagnostic && NotificationContentProtection.AddressHash(allowed.Address) != candidate.AddressHash)
            { await SetHeldAsync(candidate.Id, NotificationGenerationState.ConfigBlocked, "RecipientAddressChanged", ct, settings); continue; }
            if (!settings.PermitsAddress(address, diagnostic))
            { await SetHeldAsync(candidate.Id, NotificationGenerationState.ConfigBlocked, "RecipientPolicyBlocked", ct, settings); continue; }
            var semanticHash = MessageConfigurationHash(settings);
            if (candidate.ProtectedMime.Length > 0 && candidate.MessageConfigurationHash != semanticHash)
            { await SetHeldAsync(candidate.Id, NotificationGenerationState.ConfigBlocked, "MessageSettingsChangedReissueRequired", ct, settings); continue; }
            using var process = Process.GetCurrentProcess();
            var claim = Guid.NewGuid();
            var attempt = new NotificationPhysicalAttempt(candidate.Id, claim, settings.SettingsId, settings.PolicyHash,
                authority.HostIdentity, process.Id, process.StartTime.ToUniversalTime().Ticks, now,
                protection.Protect(JsonSerializer.Serialize(settings)), EffectiveSettingsHash(settings));
            await using (var transaction = await db.Database.BeginTransactionAsync(ct))
            {
                var won = await db.NotificationDeliveryGenerations.Where(x => x.Id == candidate.Id && x.Version == candidate.Version
                    && (x.State == NotificationGenerationState.Pending || x.State == NotificationGenerationState.RetryDue || x.State == NotificationGenerationState.ConfigBlocked))
                    .ExecuteUpdateAsync(x => x.SetProperty(p => p.State, NotificationGenerationState.Claimed)
                        .SetProperty(p => p.ClaimToken, claim).SetProperty(p => p.CurrentAttemptId, attempt.Id)
                        .SetProperty(p => p.LeaseUntilTicks, now.AddMinutes(2).UtcTicks).SetProperty(p => p.Version, p => p.Version + 1), ct);
                if (won == 0) continue;
                db.NotificationPhysicalAttempts.Add(attempt); await db.SaveChangesAsync(ct); await transaction.CommitAsync(ct);
            }
            db.ChangeTracker.Clear();
            var generation = await db.NotificationDeliveryGenerations.SingleAsync(x => x.Id == candidate.Id, ct);
            if (generation.ProtectedMime.Length == 0)
            {
                var mime = Compose(settings, generation, context, address);
                using var content = new MemoryStream(); await mime.WriteToAsync(content, ct);
                var text = Encoding.UTF8.GetString(content.ToArray());
                if (content.Length > 32_768)
                { await SetHeldAsync(generation.Id, NotificationGenerationState.ConfigBlocked, "MessageSizeBlocked", ct, settings); continue; }
                generation.FreezeMime(protection.Protect(text), NotificationInstallationAuthority.Hash(text), semanticHash);
                await db.SaveChangesAsync(ct);
            }
            var effective = await resolver.ResolveAsync(ct);
            if (!effective.CanSend || !effective.AdmissionEnabled || EffectiveSettingsHash(effective) != attempt.EffectiveSettingsHash || effective.SettingsId != settings.SettingsId || effective.PolicyHash != settings.PolicyHash
                || effective.SendGeneration != generation.SendGeneration || effective.Mode != generation.Mode)
            { await SetHeldAsync(generation.Id, NotificationGenerationState.ConfigBlocked, "PreparedPolicyChanged", ct, settings); continue; }
            var started = await StartGateAsync(generation, attempt.Id, claim, effective, ct);
            if (!started) continue;
            using var sending = CancellationTokenSource.CreateLinkedTokenSource(ct);
            using var heartbeatStop = new CancellationTokenSource();
            var heartbeat = HeartbeatAsync(generation.Id, claim, effective, sending, heartbeatStop.Token);
            NotificationTransportResult receipt;
            try
            {
                using var raw = new MemoryStream(Encoding.UTF8.GetBytes(protection.Unprotect(generation.ProtectedMime)));
                var message = await MimeMessage.LoadAsync(raw, ct);
                receipt = await transport.SendAsync(effective, message, sending.Token, accepted => PersistReceiptAsync(generation.Id, attempt.Id, claim, accepted, DateTimeOffset.UtcNow));
            }
            catch { receipt = new(NotificationAttemptOutcome.AcceptanceUnknown, "Started", null, "AcceptanceUnproven", true); }
            finally { heartbeatStop.Cancel(); await heartbeat; }
            // Once Send has returned, only this factual receipt is retried. SMTP is never retried here.
            await PersistReceiptAsync(generation.Id, attempt.Id, claim, receipt, DateTimeOffset.UtcNow);
            if (receipt.Outcome is NotificationAttemptOutcome.SmtpAccepted or NotificationAttemptOutcome.Captured or NotificationAttemptOutcome.TestAccepted) sent++;
            else failed++;
            db.ChangeTracker.Clear();
            // Single-host V1 submission is bounded and serial, with one second between connections.
            if (due.Count > 1) await Task.Delay(TimeSpan.FromSeconds(1), ct);
        }
        return new(sent, suppressed, failed);
    }

    private async Task CreateAdmittedGenerationsAsync(ResolvedNotificationSettings settings, int batch, CancellationToken ct)
    {
        var roots = await (from root in db.NotificationDeliveries.AsNoTracking()
                           join admitted in db.NotificationAdmissionEpochs.AsNoTracking() on root.AdmissionEpochId equals admitted.Id
                           where admitted.Id == settings.AdmissionEpochId && admitted.InstallationId == settings.InstallationId && admitted.Mode == settings.Mode
                               && admitted.SendGeneration == settings.SendGeneration
                               && root.State != NotificationDeliveryState.Suppressed && root.BoundNotificationId != null
                               && !db.NotificationDeliveryGenerations.Any(g => g.InitialDeliveryId == root.Id)
                           orderby root.Sequence select root).Take(batch).ToListAsync(ct);
        foreach (var root in roots)
        {
            var epoch = await db.NotificationAdmissionEpochs.AsNoTracking().SingleAsync(x => x.Id == root.AdmissionEpochId, ct);
            if (epoch.InstallationId != settings.InstallationId || epoch.Mode != settings.Mode || epoch.SendGeneration != settings.SendGeneration) continue;
            var context = await db.NotificationContexts.AsNoTracking().SingleAsync(x => x.NotificationId == root.NotificationId, ct);
            var eligible = await eligibility.EvaluateAsync(context, ct);
            if (!eligible.Eligible)
            {
                var endedRoot = await db.NotificationDeliveries.SingleAsync(x => x.Id == root.Id, ct);
                endedRoot.Suppress(eligible.SafeCode, DateTimeOffset.UtcNow); await db.SaveChangesAsync(ct); continue;
            }
            var diagnostic = context.SourceFamily == NotificationSourceFamily.DiagnosticOperation;
            var address = settings.Mode is NotificationMode.Capture or NotificationMode.ControlledTest || diagnostic
                ? settings.Policy?.DiagnosticTarget ?? "capture@aerolink.invalid" : eligible.Address;

            var generation = new NotificationDeliveryGeneration(root.Id, epoch.Id, settings.SettingsId, settings.Mode,
                settings.SendGeneration, protection.Protect(address), NotificationContentProtection.AddressHash(address), root.CreatedAt,
                diagnostic: diagnostic);
            if (!settings.PermitsAddress(address, diagnostic)) generation.Hold("RecipientPolicyBlocked", blockedSettingsHash: EffectiveSettingsHash(settings));
            db.NotificationDeliveryGenerations.Add(generation);
            try { await db.SaveChangesAsync(ct); }
            catch (DbUpdateException) { db.ChangeTracker.Clear(); }
        }
    }
    private async Task<bool> StartGateAsync(NotificationDeliveryGeneration generation, Guid attemptId, Guid claim,
        ResolvedNotificationSettings settings, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        // Settings commands serialize on this installation row too. A stale preparation cannot cross it.
        var policyCurrent = await db.NotificationInstallationStates.Where(x => x.InstallationId == settings.InstallationId
            && x.SendingEnabled && x.AdmissionEpochId == generation.AdmissionEpochId && x.SettingsRevisionId == settings.SettingsId && x.Version == settings.Version)
            .ExecuteUpdateAsync(x => x.SetProperty(p => p.Version, p => p.Version), ct);
        if (policyCurrent != 1) return false;
        var gatedSettings = await resolver.ResolveAsync(ct);
        if (!gatedSettings.CanSend || !gatedSettings.AdmissionEnabled || EffectiveSettingsHash(gatedSettings) != EffectiveSettingsHash(settings)) return false;
        // The shared installation row serializes gates and administrative replacement commands. Check
        // every already-started attempt on this root again while holding that lock: preparation before
        // a sibling's gate is not permission to create a concurrent physical connection.
        var prior = await (from sibling in db.NotificationDeliveryGenerations.AsNoTracking()
                           join physical in db.NotificationPhysicalAttempts.AsNoTracking() on sibling.Id equals physical.GenerationId
                           where sibling.DeliveryId == generation.DeliveryId && physical.Id != attemptId
                               && physical.TransmissionStartedAt != null select physical).ToListAsync(ct);
        if (prior.Any(x => !NotificationQuiescence.IsConfirmed(x, authority.HostIdentity))) return false;
        if (prior.Any(x => x.Outcome is NotificationAttemptOutcome.InProgress or NotificationAttemptOutcome.AcceptanceUnknown)
            && !generation.DuplicateRiskAcknowledged) return false;
        var gateTicks = DateTimeOffset.UtcNow.UtcTicks;
        var gate = await db.NotificationDeliveryGenerations.Where(x => x.Id == generation.Id && x.State == NotificationGenerationState.Claimed
            && x.ClaimToken == claim && x.CurrentAttemptId == attemptId && x.LeaseUntilTicks > gateTicks)
            .ExecuteUpdateAsync(x => x.SetProperty(p => p.State, NotificationGenerationState.TransmissionStarted)
                .SetProperty(p => p.Attempts, p => p.Attempts + 1).SetProperty(p => p.Version, p => p.Version + 1), ct);
        if (gate != 1) return false;
        var attempt = await db.NotificationPhysicalAttempts.SingleAsync(x => x.Id == attemptId, ct);
        attempt.Start(DateTimeOffset.UtcNow); await db.SaveChangesAsync(ct); await transaction.CommitAsync(ct); return true;
    }
    private async Task HeartbeatAsync(Guid generationId, Guid claim, ResolvedNotificationSettings original,
        CancellationTokenSource sending, CancellationToken stop)
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(30), stop);
                using var scope = scopes.CreateScope(); var currentDb = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
                var current = await scope.ServiceProvider.GetRequiredService<NotificationSettingsResolver>().ResolveAsync(stop);
                if (!current.CanSend || !current.AdmissionEnabled || current.Mode != original.Mode || current.SendGeneration != original.SendGeneration || current.PolicyHash != original.PolicyHash || EffectiveSettingsHash(current) != EffectiveSettingsHash(original))
                { sending.Cancel(); return; }
                var renewedUntil = DateTimeOffset.UtcNow.AddMinutes(2).UtcTicks;
                var renewed = await currentDb.NotificationDeliveryGenerations.Where(x => x.Id == generationId && x.ClaimToken == claim && x.State == NotificationGenerationState.TransmissionStarted)
                    .ExecuteUpdateAsync(x => x.SetProperty(p => p.LeaseUntilTicks, renewedUntil), stop);
                if (renewed != 1) { sending.Cancel(); return; }
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        catch { sending.Cancel(); }
    }
    private async Task PersistReceiptAsync(Guid generationId, Guid attemptId, Guid claim, NotificationTransportResult receipt, DateTimeOffset now)
    {
        var deadline = now.AddMinutes(10);
        while (DateTimeOffset.UtcNow < deadline)
        {
            try
            {
                using var scope = scopes.CreateScope(); var currentDb = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
                await using var transaction = await currentDb.Database.BeginTransactionAsync();
                var attempt = await currentDb.NotificationPhysicalAttempts.SingleAsync(x => x.Id == attemptId && x.ClaimToken == claim);
                if (attempt.Outcome != NotificationAttemptOutcome.InProgress)
                {
                    if (receipt.TransportDisposed && !attempt.TransportDisposed)
                    {
                        attempt.AcknowledgeDisposal(now, receipt.CleanupWarning); await currentDb.SaveChangesAsync(); await transaction.CommitAsync();
                    }
                    return;
                }
                attempt.Complete(receipt.Outcome, receipt.Phase, receipt.Status, receipt.SafeCode, receipt.TransportDisposed, now, receipt.CleanupWarning);
                var state = receipt.Outcome switch
                {
                    NotificationAttemptOutcome.SmtpAccepted => NotificationGenerationState.SmtpAccepted,
                    NotificationAttemptOutcome.Captured => NotificationGenerationState.Captured,
                    NotificationAttemptOutcome.TestAccepted => NotificationGenerationState.TestAccepted,
                    NotificationAttemptOutcome.TransientRefused => NotificationGenerationState.RetryDue,
                    NotificationAttemptOutcome.PermanentRefused => NotificationGenerationState.PermanentFailed,
                    NotificationAttemptOutcome.ConfigBlocked => NotificationGenerationState.ConfigBlocked,
                    _ => NotificationGenerationState.AcceptanceUnknown,
                };
                var generation = await currentDb.NotificationDeliveryGenerations.AsNoTracking().SingleAsync(x => x.Id == generationId);
                if (state == NotificationGenerationState.RetryDue && (generation.TransientFailures + 1 >= generation.MaximumAttempts || generation.DeadlineTicks <= now.UtcTicks)) state = NotificationGenerationState.RetryExhausted;
                var delay = (generation.TransientFailures + 1) switch { 1 => 30, 2 => 60, _ => 120 };
                var jitter = (int)(generation.Id.ToByteArray()[0] % 60);
                await currentDb.NotificationDeliveryGenerations.Where(x => x.Id == generationId && x.CurrentAttemptId == attemptId && x.ClaimToken == claim)
                    .ExecuteUpdateAsync(x => x.SetProperty(p => p.State, state).SetProperty(p => p.SafeCode, receipt.SafeCode)
                        .SetProperty(p => p.BlockedEffectiveSettingsHash, receipt.Outcome == NotificationAttemptOutcome.ConfigBlocked ? attempt.EffectiveSettingsHash : "")
                        .SetProperty(p => p.TransientFailures, p => p.TransientFailures + (receipt.Outcome == NotificationAttemptOutcome.TransientRefused ? 1 : 0))
                        .SetProperty(p => p.DueTicks, now.AddMinutes(delay).AddSeconds(jitter).UtcTicks).SetProperty(p => p.ClaimToken, (Guid?)null)
                        .SetProperty(p => p.Version, p => p.Version + 1));
                await currentDb.SaveChangesAsync(); await transaction.CommitAsync(); return;
            }
            catch { await Task.Delay(TimeSpan.FromSeconds(1)); }
        }
        // The durable gate still says possibly transmitted. Restart/expiry holds it as unknown.
    }
    public async Task<bool> PredecessorsQuiescentAsync(NotificationDeliveryGeneration generation, CancellationToken ct)
    {
        var others = await db.NotificationDeliveryGenerations.AsNoTracking().Where(x => x.DeliveryId == generation.DeliveryId && x.Id != generation.Id)
            .Select(x => x.Id).ToListAsync(ct);
        var attempts = await db.NotificationPhysicalAttempts.AsNoTracking().Where(x => others.Contains(x.GenerationId) && x.TransmissionStartedAt != null).ToListAsync(ct);
        foreach (var attempt in attempts)
        {
            if (!NotificationQuiescence.IsConfirmed(attempt, authority.HostIdentity)) return false;
            if (attempt.Outcome is NotificationAttemptOutcome.InProgress or NotificationAttemptOutcome.AcceptanceUnknown && !generation.DuplicateRiskAcknowledged) return false;
        }
        return true;
    }
    private async Task RecoverExpiredClaimsAsync(DateTimeOffset now, CancellationToken ct)
    {
        var expired = await db.NotificationDeliveryGenerations.AsNoTracking().Where(x => x.LeaseUntilTicks < now.UtcTicks
            && (x.State == NotificationGenerationState.Claimed || x.State == NotificationGenerationState.TransmissionStarted)).Take(50).ToListAsync(ct);
        foreach (var generation in expired)
        {
            var state = generation.State == NotificationGenerationState.Claimed ? NotificationGenerationState.Pending : NotificationGenerationState.AcceptanceUnknown;
            await db.NotificationDeliveryGenerations.Where(x => x.Id == generation.Id && x.State == generation.State && x.ClaimToken == generation.ClaimToken)
                .ExecuteUpdateAsync(x => x.SetProperty(p => p.State, state).SetProperty(p => p.SafeCode, state == NotificationGenerationState.Pending ? "" : "WorkerTransmissionExpired")
                    .SetProperty(p => p.ClaimToken, state == NotificationGenerationState.Pending ? (Guid?)null : generation.ClaimToken)
                    .SetProperty(p => p.Version, p => p.Version + 1), ct);
        }
    }
    private async Task<int> SetHeldAsync(Guid id, NotificationGenerationState state, string code, CancellationToken ct, ResolvedNotificationSettings settings)
    {
        var changed = await db.NotificationDeliveryGenerations.Where(x => x.Id == id && (x.State == NotificationGenerationState.Pending || x.State == NotificationGenerationState.RetryDue || x.State == NotificationGenerationState.ConfigBlocked || x.State == NotificationGenerationState.Claimed))
            .ExecuteUpdateAsync(x => x.SetProperty(p => p.State, state).SetProperty(p => p.SafeCode, code).SetProperty(p => p.BlockedEffectiveSettingsHash, EffectiveSettingsHash(settings)).SetProperty(p => p.ClaimToken, (Guid?)null).SetProperty(p => p.Version, p => p.Version + 1), ct);
        if (changed > 0) await CompleteFencedPreparationsAsync(ct);
        return changed;
    }
    private async Task CompleteFencedPreparationsAsync(CancellationToken ct)
    {
        var abandoned = await (from attempt in db.NotificationPhysicalAttempts.AsNoTracking()
                               join generation in db.NotificationDeliveryGenerations.AsNoTracking() on attempt.GenerationId equals generation.Id
                               where attempt.TransmissionStartedAt == null && attempt.Outcome == NotificationAttemptOutcome.InProgress
                                   && generation.CurrentAttemptId == attempt.Id && generation.ClaimToken == null
                                   && generation.State != NotificationGenerationState.TransmissionStarted
                               select attempt).Take(50).ToListAsync(ct);
        foreach (var candidate in abandoned)
        {
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            var fenced = await db.NotificationDeliveryGenerations.Where(x => x.Id == candidate.GenerationId
                && x.CurrentAttemptId == candidate.Id && x.ClaimToken == null && x.State != NotificationGenerationState.TransmissionStarted)
                .ExecuteUpdateAsync(x => x.SetProperty(p => p.Version, p => p.Version), ct);
            if (fenced != 1) continue;
            var attempt = await db.NotificationPhysicalAttempts.SingleAsync(x => x.Id == candidate.Id, ct);
            if (attempt.TransmissionStartedAt is null && attempt.Outcome == NotificationAttemptOutcome.InProgress)
            {
                attempt.Complete(NotificationAttemptOutcome.AbandonedBeforeTransmission, "Prepared", null,
                    "PreparationFenced", true, DateTimeOffset.UtcNow);
                await db.SaveChangesAsync(ct);
            }
            await transaction.CommitAsync(ct); db.ChangeTracker.Clear();
        }
    }
    public static string EffectiveSettingsHash(ResolvedNotificationSettings settings)
        => NotificationInstallationAuthority.Hash(JsonSerializer.Serialize(settings));
    public static string MessageConfigurationHash(ResolvedNotificationSettings settings)
        => NotificationInstallationAuthority.Hash($"{settings.Mode}\n{settings.Sender}\n{settings.DisplayName}\n{settings.BaseUrl}\n1\n1");
    private MimeMessage Compose(ResolvedNotificationSettings settings, NotificationDeliveryGeneration generation, NotificationContext context, string address)
    {
        var test = settings.Mode is NotificationMode.Capture or NotificationMode.ControlledTest || context.SourceFamily == NotificationSourceFamily.DiagnosticOperation;
        var body = test ? "AeroLink synthetic notification transport diagnostic. No workflow authority or user preference capability is carried by this message."
            : $"AeroLink: {context.Identifier}\nAction: {context.Stage}\nOriginal cycle/round: {context.Cycle}\nOriginal task/step: {context.SourceId:D}\n\nSign in to view this exact request:\n{settings.BaseUrl}/notifications/{context.NotificationId:D}";
        string? unsubscribe = null;
        if (!test)
        {
            var token = tokens.Issue(context.Recipient);
            if (token is not null) unsubscribe = $"{settings.BaseUrl}/api/notifications/unsubscribe?recipient={Uri.EscapeDataString(context.Recipient)}&token={token}";
            if (token is not null) body += $"\n\nEmail preference confirmation:\n{settings.BaseUrl}/api/notifications/unsubscribe?recipient={Uri.EscapeDataString(context.Recipient)}&token={token}";
        }
        else body += "\n\nSign in to AeroLink for the protected operations receipt.";
        var message = new MimeMessage { MessageId = generation.MessageId, Date = generation.MessageDate,
            Subject = test ? "AeroLink transport diagnostic" : $"AeroLink: {context.Identifier} — {context.Stage}" };
        message.From.Add(new MailboxAddress(settings.DisplayName, settings.Sender)); message.To.Add(MailboxAddress.Parse(address));
        var html = ReviewEmailShell.Render(message.Subject, test ? "TRANSPORT DIAGNOSTIC" : "ORIGINAL REQUEST", "#326d8f",
            test ? "Synthetic diagnostic" : context.Identifier,
            test ? "No controlled workflow content or preference capability is included." : context.Stage,
            test ? [] : [("Original cycle or round", context.Cycle.ToString()), ("Original task or step", context.SourceId.ToString("D"))],
            "View original request", test ? null : $"{settings.BaseUrl}/notifications/{context.NotificationId:D}",
            "Authority is checked in AeroLink", "An email is not a signature or permission to act. Sign in and confirm the exact current context.",
            "#edf6f4", "#3c9989", "#22594f", "#45675f",
            test ? "This is an explicitly requested synthetic transport diagnostic." : "You received this because the original request identified you.", unsubscribe);
        message.Body = new BodyBuilder { TextBody = body, HtmlBody = html }.ToMessageBody();
        message.Prepare(EncodingConstraint.SevenBit); return message;
    }
}

/// <summary>Local process creation identity or durable socket-disposal receipt, never lease/heartbeat absence.</summary>
public static class NotificationQuiescence
{
    public static bool IsConfirmed(NotificationPhysicalAttempt attempt, string hostIdentity)
    {
        if (attempt.TransportDisposed && attempt.CompletedAt is not null) return true;
        if (attempt.HostIdentity != hostIdentity) return false;
        try
        {
            using var process = Process.GetProcessById(attempt.ProcessId);
            return process.HasExited || process.StartTime.ToUniversalTime().Ticks != attempt.ProcessStartTicks;
        }
        catch (ArgumentException) { return true; } // OS reports that this exact PID no longer exists.
        catch { return false; }
    }
}
