using AeroLink.Domain.Common;
using AeroLink.Domain.Notifications;
using AeroLink.Infrastructure.Notifications;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AeroLink.Api;

internal static class NotificationOperationsEndpoints
{
    internal static void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/operations");
        group.MapGet("/notifications", OverviewAsync);
        group.MapPost("/notifications/settings", async (NotificationSettingsCommand request, HttpContext http, NotificationOperationsService operations, CancellationToken ct)
            => await CommandAsync(http, () => operations.SaveSettingsAsync(http.UserAccount().UserName, request, ct)));
        group.MapPost("/notifications/transport-test", async (NotificationDiagnosticCommand request, HttpContext http, NotificationOperationsService operations, CancellationToken ct)
            => await CommandAsync(http, () => operations.QueueDiagnosticAsync(http.UserAccount().UserName, request, ct)));
        group.MapPost("/notifications/commands", async (NotificationControlCommand request, HttpContext http, NotificationOperationsService operations, CancellationToken ct)
            => await CommandAsync(http, () => operations.ControlAsync(http.UserAccount().UserName, request, ct)));
        group.MapGet("/notifications/operations/{operationKey:guid}", async (Guid operationKey, string family, HttpContext http, NotificationOperationsService operations, CancellationToken ct) =>
        {
            if (!http.UserAccount().IsAdministrator) return Results.Forbid();
            var receipt = await operations.LookupAsync(http.UserAccount().UserName, family, operationKey, ct);
            return receipt is null ? Results.NotFound() : Results.Ok(receipt);
        });
        group.MapGet("/notifications/generations/{id:guid}/attempts", async (Guid id, HttpContext http, AeroLinkDbContext db, NotificationInstallationAuthority authority, int page = 1, int pageSize = 25, CancellationToken ct = default) =>
        {
            if (!http.UserAccount().IsAdministrator) return Results.Forbid();
            if (!await db.NotificationDeliveryGenerations.AnyAsync(x => x.Id == id, ct)) return Results.NotFound();
            page = Math.Max(1, page); pageSize = Math.Clamp(pageSize, 1, 100);
            var total = await db.NotificationPhysicalAttempts.CountAsync(x => x.GenerationId == id, ct);
            var attempts = await db.NotificationPhysicalAttempts.AsNoTracking().Where(x => x.GenerationId == id).OrderByDescending(x => x.Id).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
            return Results.Ok(new { page, pageSize, total, attempts = attempts.Select(x => new { x.Id, startedAt = x.TransmissionStartedAt, x.CompletedAt,
                outcome = x.Outcome.ToString(), x.Phase, x.SmtpStatus, x.SafeCode, x.TransportDisposed, x.CleanupWarning,
                quiescence = NotificationQuiescence.IsConfirmed(x, authority.HostIdentity) ? "confirmed" : "unproven" }) });
        });
    }
    private static async Task<IResult> CommandAsync(HttpContext http, Func<Task<NotificationOperationReceipt>> command)
    {
        if (!http.UserAccount().IsAdministrator) return Results.Forbid();
        try { return Results.Ok(await command()); }
        catch (NotificationOperationConflictException ex) { return Results.Conflict(new { error = ex.Message, code = "operation_conflict" }); }
        catch (DbUpdateConcurrencyException) { return Results.Conflict(new { error = "Notification state changed; recover the original operation or refresh.", code = "stale_version" }); }
        catch (DomainException ex) { return Results.BadRequest(new { error = ex.Message, code = "notification_policy_refusal" }); }
    }
    private static async Task<IResult> OverviewAsync(HttpContext http, AeroLinkDbContext db, IConfiguration configuration,
        NotificationSettingsResolver resolver, NotificationInstallationAuthority authority, int page = 1, int pageSize = 25,
        string? state = null, int heldPage = 1, int heldPageSize = 25, CancellationToken ct = default)
    {
        if (!http.UserAccount().IsAdministrator) return Results.Forbid();
        page = Math.Clamp(page, 1, 1_000_000); pageSize = Math.Clamp(pageSize, 1, 100);
        heldPage = Math.Clamp(heldPage, 1, 1_000_000); heldPageSize = Math.Clamp(heldPageSize, 1, 100);
        var effective = await resolver.ResolveAsync(ct);
        var query = db.NotificationDeliveryGenerations.AsNoTracking();
        if (state is not null)
        {
            if (!Enum.TryParse<NotificationGenerationState>(state, out var selected) || !Enum.IsDefined(selected)) return Results.BadRequest(new { error = "Unknown notification state." });
            query = query.Where(x => x.State == selected);
        }
        var total = await query.CountAsync(ct);
        var generations = await query.OrderByDescending(x => x.DueTicks).ThenBy(x => x.Id).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        var rootIds = generations.Select(x => x.DeliveryId).ToList();
        var uncertainRoots = await (from linked in db.NotificationDeliveryGenerations.AsNoTracking()
                                    join attempt in db.NotificationPhysicalAttempts.AsNoTracking() on linked.Id equals attempt.GenerationId
                                    where rootIds.Contains(linked.DeliveryId) && attempt.TransmissionStartedAt != null
                                        && (attempt.Outcome == NotificationAttemptOutcome.InProgress || attempt.Outcome == NotificationAttemptOutcome.AcceptanceUnknown)
                                    select linked.DeliveryId).Distinct().ToListAsync(ct);
        var roots = (await db.NotificationDeliveries.AsNoTracking().Where(x => rootIds.Contains(x.Id)).ToListAsync(ct)).ToDictionary(x => x.Id);
        var noticeIds = roots.Values.Select(x => x.NotificationId).ToList();
        var contexts = (await db.NotificationContexts.AsNoTracking().Where(x => noticeIds.Contains(x.NotificationId)).ToListAsync(ct)).ToDictionary(x => x.NotificationId);
        var counts = await db.NotificationDeliveryGenerations.AsNoTracking().GroupBy(x => x.State)
            .Select(x => new { State = x.Key, Count = x.Count(), OldestTicks = x.Min(g => g.CreatedTicks) }).ToListAsync(ct);
        var historical = await db.NotificationDeliveries.AsNoTracking().GroupBy(x => x.State).Select(x => new { State = x.Key, Count = x.Count(), OldestTicks = x.Min(g => g.Sequence) }).ToListAsync(ct);
        var deliveryRows = await db.NotificationDeliveries.AsNoTracking().OrderByDescending(x => x.Sequence).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        var heldQuery = db.NotificationDeliveries.AsNoTracking().Where(x => x.State == NotificationDeliveryState.HeldAdmission
            && x.BoundNotificationId != null && !db.NotificationDeliveryGenerations.Any(g => g.DeliveryId == x.Id));
        var heldTotal = await heldQuery.CountAsync(ct);
        var heldRows = await heldQuery.OrderBy(x => x.Sequence).ThenBy(x => x.Id)
            .Skip((heldPage - 1) * heldPageSize).Take(heldPageSize).ToListAsync(ct);
        var heldNoticeIds = heldRows.Select(x => x.NotificationId).ToList();
        var heldContexts = await db.NotificationContexts.AsNoTracking().Where(x => heldNoticeIds.Contains(x.NotificationId))
            .ToDictionaryAsync(x => x.NotificationId, x => x.Identifier, ct);
        var heldRoots = await db.NotificationDeliveries.AsNoTracking().CountAsync(x => x.State == NotificationDeliveryState.HeldAdmission
            && !db.NotificationDeliveryGenerations.Any(g => g.DeliveryId == x.Id), ct);
        var heldRootOldest = await db.NotificationDeliveries.AsNoTracking().Where(x => x.State == NotificationDeliveryState.HeldAdmission
            && !db.NotificationDeliveryGenerations.Any(g => g.DeliveryId == x.Id)).Select(x => (long?)x.Sequence).MinAsync(ct);
        var health = counts.Select(x => new { state = x.State.ToString(), count = x.Count, oldestTicks = x.OldestTicks, action = Guidance(x.State) })
            .Concat([new { state = "HeldAdmission", count = heldRoots, oldestTicks = heldRootOldest ?? DateTimeOffset.UtcNow.UtcTicks, action = "Select eligible exact-context work for explicit readmission." },
                new { state = "LegacyUnbound", count = Historical(NotificationDeliveryState.LegacyUnbound), oldestTicks = historical.SingleOrDefault(x => x.State == NotificationDeliveryState.LegacyUnbound)?.OldestTicks ?? DateTimeOffset.UtcNow.UtcTicks, action = "Historical source identity is unresolved; legacy backlog cannot enter Live admission." }])
            .Where(x => x.count > 0).GroupBy(x => x.state).Select(x => new { state = x.Key, count = x.Sum(v => v.count), oldestAt = new DateTimeOffset(x.Min(v => v.oldestTicks), TimeSpan.Zero), action = x.First().action });
        var database = db.Database.GetDbConnection().Database;
        return Results.Ok(new
        {
            generatedAt = DateTimeOffset.UtcNow,
            installation = new { id = authority.InstallationId, label = configuration["Instance:Label"] ?? "Unconfigured installation", database, version = effective.Version },
            settings = new { version = effective.Version, mode = effective.Mode.ToString(), host = effective.RelayLocked ? "[installation-owned]" : effective.Host,
                effective.Port, sender = effective.SenderLocked ? "[installation-owned]" : effective.Sender, effective.DisplayName,
                baseUrl = effective.BaseUrlLocked ? "[installation-owned]" : effective.BaseUrl,
                userNameConfigured = effective.UserName.Length > 0, credentialConfigured = effective.Credential.Length > 0 },
            locks = new { relay = effective.RelayLocked, sender = effective.SenderLocked, baseUrl = effective.BaseUrlLocked,
                credentials = effective.CredentialsLocked, externalModes = effective.Policy is null || effective.Policy.MaximumMode < NotificationMode.ControlledTest, diagnosticTarget = true },
            policy = new { maximumMode = (effective.Policy?.MaximumMode ?? NotificationMode.Capture).ToString(),
                diagnosticTarget = string.IsNullOrWhiteSpace(effective.Policy?.DiagnosticTarget) ? "not-configured" : "protected",
                sendAuthority = effective.Policy is null ? "blocked" : "current", safeCode = effective.SafeCode },
            smtp = new { configured = effective.CanSend, hostConfigured = effective.Host.Length > 0, port = effective.Port,
                portValid = effective.Port is > 0 and <= 65535, useStartTls = effective.IsExternal,
                credentialsConfigured = effective.Credential.Length > 0, fromConfigured = effective.Sender.Length > 0 },
            links = new { configured = effective.BaseUrl.Length > 0, valid = Uri.TryCreate(effective.BaseUrl, UriKind.Absolute, out _), baseUrl = effective.BaseUrlLocked ? null : effective.BaseUrl },
            totals = new { pending = Count(NotificationGenerationState.Pending) + Count(NotificationGenerationState.RetryDue),
                sent = Historical(NotificationDeliveryState.Sent) + Count(NotificationGenerationState.SmtpAccepted) + Count(NotificationGenerationState.Captured) + Count(NotificationGenerationState.TestAccepted),
                failed = Historical(NotificationDeliveryState.Failed) + Count(NotificationGenerationState.PermanentFailed),
                suppressed = Historical(NotificationDeliveryState.Suppressed) + Count(NotificationGenerationState.Suppressed) },
            health,
            deliveries = deliveryRows.Select(x => new { x.Id, x.NotificationId, recipient = "[protected]", address = "[protected]", channel = x.Channel.ToString(),
                state = x.State.ToString(), x.Attempts, detail = x.State == NotificationDeliveryState.Sent ? "Historical SMTP submission evidence." : x.State.ToString(), x.CreatedAt, x.UpdatedAt, x.CompletedAt }),
            heldDeliveries = heldRows.Select(x => new { x.Id, x.NotificationId, contextIdentifier = heldContexts.GetValueOrDefault(x.NotificationId) ?? "Original request unavailable", state = x.State.ToString(), x.CreatedAt }),
            heldTotal, heldPage, heldPageSize,
            generations = generations.Select(x => new { x.Id, x.DeliveryId, notificationId = roots[x.DeliveryId].NotificationId,
                contextIdentifier = contexts.GetValueOrDefault(roots[x.DeliveryId].NotificationId)?.Identifier ?? "Legacy unresolved", mode = x.Mode.ToString(), state = x.State.ToString(),
                x.Version, x.MessageId, x.BodyHash, intendedDestination = "[protected]", effectiveDestination = "[protected]", x.Attempts,
                requiresDuplicateRiskAcknowledgement = uncertainRoots.Contains(x.DeliveryId),
                dueAt = new DateTimeOffset(x.DueTicks, TimeSpan.Zero), deadlineAt = new DateTimeOffset(x.DeadlineTicks, TimeSpan.Zero), x.SafeCode, x.CreatedAt }), page, pageSize, total,
        });
        int Count(NotificationGenerationState value) => counts.SingleOrDefault(x => x.State == value)?.Count ?? 0;
        int Historical(NotificationDeliveryState value) => historical.SingleOrDefault(x => x.State == value)?.Count ?? 0;
    }
    private static string Guidance(NotificationGenerationState state) => state switch
    {
        NotificationGenerationState.ConfigBlocked => "Correct permitted configuration or reissue changed concrete mail; never bypass TLS certificates.",
        NotificationGenerationState.AcceptanceUnknown or NotificationGenerationState.TransmissionStarted => "Prove exact worker/socket quiescence and reconcile relay evidence before deliberate duplicate-risk replay.",
        NotificationGenerationState.RetryExhausted => "Select eligible work for explicit bounded readmission.",
        NotificationGenerationState.SmtpAccepted => "SMTP accepted submission; mailbox arrival is separate evidence.",
        NotificationGenerationState.Captured or NotificationGenerationState.TestAccepted => "Immutable test evidence; eligible work needs an explicit distinct linked generation for Live delivery.",
        _ => "Inspect the original request and attempt history.",
    };
}
