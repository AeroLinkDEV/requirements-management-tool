using System.Text.Json;
using AeroLink.Domain.Common;
using AeroLink.Domain.Notifications;
using AeroLink.Domain.Requirements;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AeroLink.Infrastructure.Notifications;

public sealed record NotificationSettingsCommand(Guid OperationKey, long ExpectedVersion, NotificationMode Mode,
    string? Host, int Port, string? Sender, string? DisplayName, string? BaseUrl, string? UserName,
    string? Credential = null, bool ClearCredential = false, string[]? EventTypes = null);
public sealed record NotificationControlCommand(Guid OperationKey, long ExpectedVersion, string Family,
    NotificationMode? Mode = null, Guid? GenerationId = null, long? ExpectedGenerationVersion = null,
    bool AcknowledgeDuplicateRisk = false, Guid? DeliveryId = null);
public sealed record NotificationDiagnosticCommand(Guid ProjectId, Guid OperationKey, long ExpectedVersion);
public sealed record NotificationOperationReceipt(Guid Id, Guid OperationKey, string Family, DateTimeOffset CreatedAt, JsonElement Result);
public sealed class NotificationOperationConflictException(string message) : Exception(message);

public sealed class NotificationOperationsService(AeroLinkDbContext db, NotificationSettingsResolver resolver,
    NotificationInstallationAuthority authority, NotificationContentProtection protection, NotificationEligibility eligibility)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public static readonly string[] InitialEventTypes = ["ReviewActivated", "ApprovalActivated", "TestChangeRequestApprovalRequested", "DocumentReviewActivated",
        "DocumentApprovalActivated", "ReviewChangesRequested", "DocumentReturned", "RequirementAssignment", "ManagedDocumentStewardAssigned", "ManagedDocumentRevisionAssigned", "NotificationTransportTest"];

    public async Task<NotificationOperationReceipt?> LookupAsync(string actor, string family, Guid key, CancellationToken ct)
    {
        var operation = await db.NotificationOperations.AsNoTracking().SingleOrDefaultAsync(x => x.InstallationId == authority.InstallationId
            && x.Actor == actor && x.Family == family && x.OperationKey == key, ct);
        return operation is null ? null : Receipt(operation);
    }
    public Task<NotificationOperationReceipt> SaveSettingsAsync(string actor, NotificationSettingsCommand request, CancellationToken ct)
        => RunAsync(actor, "Settings", request.OperationKey, request with
        { Host = request.Host?.Trim().ToLowerInvariant(), Sender = request.Sender?.Trim().ToLowerInvariant(),
            BaseUrl = request.BaseUrl?.Trim().TrimEnd('/'), UserName = request.UserName?.Trim(),
            EventTypes = request.EventTypes?.Distinct().Order().ToArray() }, async operation =>
        {
            var state = await StateAsync(request.ExpectedVersion, ct);
            var current = await resolver.ResolveAsync(ct);
            var previous = state.SettingsRevisionId is Guid settingsId ? await db.NotificationSettingsRevisions.SingleAsync(x => x.Id == settingsId, ct) : null;
            var events = request.EventTypes ?? (previous is null ? InitialEventTypes : JsonSerializer.Deserialize<string[]>(previous.AllowedEventTypesJson) ?? []);
            if (events.Any(x => !InitialEventTypes.Contains(x, StringComparer.Ordinal))) throw new DomainException("Only initial-scope event types may be enabled.");
            if (!Enum.IsDefined(request.Mode)) throw new DomainException("Unknown notification mode.");
            if (request.Mode is NotificationMode.ControlledTest or NotificationMode.Live
                && (current.Policy is null || request.Mode > current.Policy.MaximumMode)) throw new DomainException("Installation policy does not permit external delivery.");
            if (request.Port is < 1 or > 65535) throw new DomainException("SMTP port must be between 1 and 65535.");
            string Stored(bool locked, string? replacement, string? previousValue)
                => locked || replacement is null ? previousValue ?? "" : protection.Protect(replacement.Trim());
            var credential = previous?.ProtectedCredential ?? "";
            if (!current.CredentialsLocked)
            {
                if (request.ClearCredential) credential = "";
                else if (request.Credential is not null) credential = protection.Protect(request.Credential);
            }
            var saved = new NotificationSettingsRevision(authority.InstallationId, (previous?.Version ?? 0) + 1,
                request.Mode, Stored(current.RelayLocked, request.Host, previous?.Host), current.RelayLocked ? current.Port : request.Port,
                Stored(current.SenderLocked, request.Sender, previous?.Sender), current.SenderLocked ? previous?.DisplayName ?? "AeroLink" : request.DisplayName ?? previous?.DisplayName ?? "AeroLink",
                Stored(current.BaseUrlLocked, request.BaseUrl, previous?.BaseUrl), Stored(current.CredentialsLocked, request.UserName, previous?.UserName),
                credential, actor, DateTimeOffset.UtcNow);
            saved.NarrowEvents(JsonSerializer.Serialize(events.Distinct().Order(), Json));
            db.NotificationSettingsRevisions.Add(saved); state.SetSettings(saved.Id, state.Version, request.Mode == NotificationMode.Disabled || request.Mode != current.Mode);
            operation.Record(JsonSerializer.Serialize(new { state = "SettingsSaved", version = state.Version, settingsRevisionId = saved.Id }, Json));
        }, ct);

    public Task<NotificationOperationReceipt> QueueDiagnosticAsync(string actor, NotificationDiagnosticCommand request, CancellationToken ct)
        => RunAsync(actor, "TransportTest", request.OperationKey, request, async operation =>
        {
            var state = await StateAsync(request.ExpectedVersion, ct);
            if (!await db.Projects.AsNoTracking().AnyAsync(x => x.Id == request.ProjectId, ct)) throw new DomainException("The diagnostic project is unavailable.");
            var settings = await resolver.ResolveAsync(ct);
            var now = DateTimeOffset.UtcNow;
            var notice = new UserNotification(request.ProjectId, actor, "NotificationTransportTest", "AeroLink email transport diagnostic",
                "A deliberate synthetic administrator diagnostic.", "", null, now);
            notice.BindContext(NotificationContext.Diagnostic(notice, operation)); db.UserNotifications.Add(notice);
            operation.Record(JsonSerializer.Serialize(new { state = settings.CanSend && state.SendingEnabled && state.AdmissionEpochId is not null ? "Queued" : "HeldAdmission",
                notificationId = notice.Id, version = state.Version }, Json), notice.Id);
        }, ct);

    public Task<NotificationOperationReceipt> ControlAsync(string actor, NotificationControlCommand request, CancellationToken ct)
        => RunAsync(actor, request.Family, request.OperationKey, request, async operation =>
        {
            if (request.Family is not ("Activate" or "Resume" or "Readmit" or "Replay" or "Reissue" or "Suppress")) throw new DomainException("Unknown notification command family.");
            var state = await StateAsync(request.ExpectedVersion, ct);
            var settings = await resolver.ResolveAsync(ct); var now = DateTimeOffset.UtcNow;
            if (request.Family is "Activate" or "Resume")
            {
                if (!settings.CanSend || request.Mode != settings.Mode) throw new DomainException("Save valid permitted settings before explicit admission.");
                if (request.Family == "Resume")
                {
                    if (state.AdmissionEpochId is not Guid existingId) throw new DomainException("There is no admission epoch to resume.");
                    var existing = await db.NotificationAdmissionEpochs.SingleAsync(x => x.Id == existingId, ct);
                    if (existing.Mode != settings.Mode || existing.SendGeneration != settings.SendGeneration) throw new DomainException("Restored or different-mode admission cannot be resumed.");
                    state.Admit(existing.Id, state.Version);
                    operation.Record(JsonSerializer.Serialize(new { state = "Resumed", version = state.Version, admissionEpochId = existing.Id }, Json)); return;
                }
                var cutoff = await db.NotificationDeliveries.AsNoTracking().Select(x => (long?)x.Sequence).MaxAsync(ct) ?? now.UtcTicks;
                var epoch = new NotificationAdmissionEpoch(authority.InstallationId, settings.Mode, settings.SettingsId, settings.SendGeneration, cutoff, actor, now);
                db.NotificationAdmissionEpochs.Add(epoch); state.Admit(epoch.Id, state.Version);
                operation.Record(JsonSerializer.Serialize(new { state = "ActivatedFutureEvents", version = state.Version, admissionEpochId = epoch.Id }, Json)); return;
            }
            if (request.Family == "Readmit" && request.DeliveryId is Guid rootId && request.GenerationId is null)
            {
                var root = await db.NotificationDeliveries.SingleOrDefaultAsync(x => x.Id == rootId, ct)
                    ?? throw new DomainException("The selected delivery is unavailable.");
                if (root.BoundNotificationId is null || root.State == NotificationDeliveryState.Suppressed
                    || await db.NotificationDeliveryGenerations.AnyAsync(x => x.DeliveryId == rootId, ct)) throw new DomainException("This root is legacy, suppressed or already has a concrete generation.");
                var epoch = await CurrentEpochAsync(state, settings, ct);
                var context = await db.NotificationContexts.AsNoTracking().SingleAsync(x => x.NotificationId == root.NotificationId, ct);
                var allowed = await eligibility.EvaluateAsync(context, ct); if (!allowed.Eligible) throw new DomainException("The original request is no longer eligible.");
                var generation = NewGeneration(root.Id, epoch, settings, context, allowed.Address, now);
                if (root.AdmissionEpochId is null) root.RecordAdmission(epoch.Id); db.NotificationDeliveryGenerations.Add(generation);
                operation.Record(JsonSerializer.Serialize(new { state = "Readmitted", generationId = generation.Id, version = state.Version }, Json), root.NotificationId, generation.Id); return;
            }
            var old = await db.NotificationDeliveryGenerations.SingleOrDefaultAsync(x => x.Id == request.GenerationId, ct)
                ?? throw new DomainException("The selected generation is unavailable.");
            if (old.Version != request.ExpectedGenerationVersion) throw new NotificationOperationConflictException("The selected generation changed; recover your original operation or refresh.");
            var possible = await (from generation in db.NotificationDeliveryGenerations.AsNoTracking()
                                  join attempt in db.NotificationPhysicalAttempts.AsNoTracking() on generation.Id equals attempt.GenerationId
                                  where generation.DeliveryId == old.DeliveryId && attempt.TransmissionStartedAt != null select attempt).ToListAsync(ct);
            if (possible.Any(x => !NotificationQuiescence.IsConfirmed(x, authority.HostIdentity)))
                throw new DomainException("The original worker process or socket is not proven quiescent. Expired leases do not permit replay or replacement.");
            var unknown = possible.Any(x => x.Outcome is NotificationAttemptOutcome.InProgress or NotificationAttemptOutcome.AcceptanceUnknown);
            if (unknown && !request.AcknowledgeDuplicateRisk) throw new DomainException("Acceptance is unknown; explicitly acknowledge possible duplicate mail after quiescence is proven.");
            if (request.Family == "Suppress")
            {
                if (old.State is not (NotificationGenerationState.SmtpAccepted or NotificationGenerationState.Captured or NotificationGenerationState.TestAccepted or NotificationGenerationState.PermanentFailed))
                    old.Hold("OperatorDisposition", NotificationGenerationState.Suppressed);
                operation.Record(JsonSerializer.Serialize(new { state = "Suppressed", generationId = old.Id, version = state.Version }, Json), generationId: old.Id); return;
            }
            if (old.State is NotificationGenerationState.Captured or NotificationGenerationState.TestAccepted)
                throw new DomainException("Terminal capture and controlled-test generations cannot be promoted or replayed.");
            var currentEpoch = await CurrentEpochAsync(state, settings, ct);
            var bound = await (from root in db.NotificationDeliveries.AsNoTracking() join context in db.NotificationContexts.AsNoTracking()
                               on root.NotificationId equals context.NotificationId where root.Id == old.DeliveryId select context).SingleAsync(ct);
            var eligible = await eligibility.EvaluateAsync(bound, ct); if (!eligible.Eligible) throw new DomainException("The original request is no longer eligible.");
            if (request.Family == "Replay")
            {
                if (old.Mode != settings.Mode || old.SendGeneration != settings.SendGeneration || old.MessageConfigurationHash != NotificationDispatcher.MessageConfigurationHash(settings))
                    throw new DomainException("Changed recipient, mode or semantic settings require linked reissue, not replay.");
                if (settings.Mode == NotificationMode.Live && bound.SourceFamily != NotificationSourceFamily.DiagnosticOperation
                    && NotificationContentProtection.AddressHash(eligible.Address) != old.AddressHash) throw new DomainException("The original address changed; linked reissue is required.");
                if (!request.AcknowledgeDuplicateRisk) throw new DomainException("Deliberate replay requires duplicate-risk acknowledgement.");
                old.QueueUnknownReplay(currentEpoch.Id, now);
            }
            else if (request.Family == "Readmit")
            {
                if (old.Mode != settings.Mode || old.MessageConfigurationHash != NotificationDispatcher.MessageConfigurationHash(settings)) throw new DomainException("Changed concrete mail requires linked reissue.");
                old.Readmit(currentEpoch.Id, settings.SendGeneration, now, bound.SourceFamily == NotificationSourceFamily.DiagnosticOperation);
            }
            else
            {
                // Revoke a known pre-gate claim before its replacement can exist. Started sockets require
                // the trusted quiescence proof above, including across every linked predecessor.
                if (old.State is NotificationGenerationState.Claimed or NotificationGenerationState.Pending or NotificationGenerationState.RetryDue or NotificationGenerationState.ConfigBlocked)
                    old.Hold("Reissued", NotificationGenerationState.Suppressed);
                var replacement = NewGeneration(old.DeliveryId, currentEpoch, settings, bound, eligible.Address, now, old.Id, request.AcknowledgeDuplicateRisk);
                db.NotificationDeliveryGenerations.Add(replacement);
                operation.Record(JsonSerializer.Serialize(new { state = "Reissued", generationId = replacement.Id, version = state.Version }, Json), generationId: replacement.Id); return;
            }
            operation.Record(JsonSerializer.Serialize(new { state = request.Family == "Replay" ? "ReplayQueuedDuplicateRisk" : "Readmitted", generationId = old.Id, version = state.Version }, Json), generationId: old.Id);
        }, ct);

    private NotificationDeliveryGeneration NewGeneration(Guid root, NotificationAdmissionEpoch epoch, ResolvedNotificationSettings settings,
        NotificationContext context, string accountAddress, DateTimeOffset now, Guid? predecessor = null, bool duplicateRisk = false)
    {
        var diagnostic = context.SourceFamily == NotificationSourceFamily.DiagnosticOperation;
        var address = settings.Mode is NotificationMode.Capture or NotificationMode.ControlledTest || diagnostic
            ? settings.Policy?.DiagnosticTarget ?? "capture@aerolink.invalid" : accountAddress;
        if (!settings.PermitsAddress(address, diagnostic)) throw new DomainException("Installation recipient policy blocks this target.");
        return new(root, epoch.Id, settings.SettingsId, settings.Mode, settings.SendGeneration,
            protection.Protect(address), NotificationContentProtection.AddressHash(address), now, predecessor, diagnostic, duplicateRisk);
    }
    private async Task<NotificationAdmissionEpoch> CurrentEpochAsync(NotificationInstallationState state, ResolvedNotificationSettings settings, CancellationToken ct)
    {
        if (!settings.CanSend || !state.SendingEnabled || state.AdmissionEpochId is not Guid epochId) throw new DomainException("Explicit permitted admission is required.");
        var epoch = await db.NotificationAdmissionEpochs.SingleAsync(x => x.Id == epochId, ct);
        if (epoch.Mode != settings.Mode || epoch.SendGeneration != settings.SendGeneration) throw new DomainException("Restored or different-mode admission requires explicit new activation.");
        return epoch;
    }
    private async Task<NotificationInstallationState> StateAsync(long expectedVersion, CancellationToken ct)
    {
        var state = await db.NotificationInstallationStates.SingleOrDefaultAsync(x => x.InstallationId == authority.InstallationId, ct);
        if (state is null)
        {
            if (expectedVersion != 0) throw new NotificationOperationConflictException("Installation version is stale.");
            state = new(authority.InstallationId); db.NotificationInstallationStates.Add(state); await db.SaveChangesAsync(ct);
        }
        else if (state.Version != expectedVersion) throw new NotificationOperationConflictException("Installation version is stale.");
        // Locks before mutable validation. Every settings/admission/generation command joins this row.
        await db.NotificationInstallationStates.Where(x => x.InstallationId == state.InstallationId && x.Version == state.Version)
            .ExecuteUpdateAsync(x => x.SetProperty(p => p.Version, p => p.Version), ct);
        return state;
    }
    private async Task<NotificationOperationReceipt> RunAsync<T>(string actor, string family, Guid key, T payload,
        Func<NotificationOperation, Task> command, CancellationToken ct)
    {
        var hash = NotificationInstallationAuthority.Hash(JsonSerializer.Serialize(payload, Json));
        // This recovery precedes configuration, target, version, eligibility and delivery-state checks.
        var existing = await db.NotificationOperations.AsNoTracking().SingleOrDefaultAsync(x => x.InstallationId == authority.InstallationId
            && x.Actor == actor && x.Family == family && x.OperationKey == key, ct);
        if (existing is not null) return Existing(existing, hash);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        try
        {
            // Concurrent identical commands wait here, then recover the committed receipt before stale
            // expected versions or any other mutable validation can reject their original intent.
            await db.NotificationInstallationStates.Where(x => x.InstallationId == authority.InstallationId)
                .ExecuteUpdateAsync(x => x.SetProperty(p => p.Version, p => p.Version), ct);
            existing = await db.NotificationOperations.AsNoTracking().SingleOrDefaultAsync(x => x.InstallationId == authority.InstallationId
                && x.Actor == actor && x.Family == family && x.OperationKey == key, ct);
            if (existing is not null) { await transaction.CommitAsync(ct); return Existing(existing, hash); }
            var operation = new NotificationOperation(authority.InstallationId, actor, family, key, hash, DateTimeOffset.UtcNow);
            await command(operation); db.NotificationOperations.Add(operation); await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct); return Receipt(operation);
        }
        catch (DbUpdateException)
        {
            await transaction.RollbackAsync(ct); db.ChangeTracker.Clear();
            existing = await db.NotificationOperations.AsNoTracking().SingleOrDefaultAsync(x => x.InstallationId == authority.InstallationId
                && x.Actor == actor && x.Family == family && x.OperationKey == key, ct);
            if (existing is not null) return Existing(existing, hash); throw;
        }
    }
    private static NotificationOperationReceipt Existing(NotificationOperation operation, string hash)
    {
        if (operation.PayloadHash != hash) throw new NotificationOperationConflictException("The operation key is already bound to a different semantic request.");
        return Receipt(operation);
    }
    private static NotificationOperationReceipt Receipt(NotificationOperation operation)
        => new(operation.Id, operation.OperationKey, operation.Family, operation.CreatedAt, JsonSerializer.Deserialize<JsonElement>(operation.ResultJson));
}
