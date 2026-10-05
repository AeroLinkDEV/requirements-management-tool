using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AeroLink.Domain.Identity;
using AeroLink.Domain.Notifications;
using AeroLink.Infrastructure.Notifications;
using AeroLink.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AeroLink.Api.Tests;

// Primary HTTP owners for #1482: the real host owns authorization, immutable command receipts,
// optimistic edits, and explicit admission. Existing worker/TLS/PostgreSQL fixtures own transport and
// provider concurrency. These tests add no production seam and make no SMTP or mailbox claim.
public sealed class NotificationOperationsApiTests
{
    private sealed record SettingsRequest(Guid OperationKey, long ExpectedVersion, string Mode = "Capture",
        string Host = "127.0.0.1", int Port = 2525, string Sender = "outbox@example.test", string DisplayName = "AeroLink Fixture",
        string BaseUrl = "https://fixture.invalid", string? UserName = null, string? Credential = null, bool ClearCredential = false);

    private sealed class Host : IDisposable
    {
        private readonly AeroLinkApiFactory source = new();
        private string? ownedPolicyPath;
        internal readonly string InstallationId = Guid.NewGuid().ToString("D");
        internal WebApplicationFactory<Program> Factory { get; }
        internal Host(bool locked = false, bool editableCredentials = false)
        {
            if (editableCredentials)
            {
                var root = Environment.GetEnvironmentVariable("AEROLINK_NOTIFICATION_AUTHORITY_ROOT");
                Assert.False(string.IsNullOrWhiteSpace(root)); // The test-process factory initializer is mandatory.
                Directory.CreateDirectory(root!);
                ownedPolicyPath = Path.Combine(root!, InstallationId + ".json");
                var policy = new NotificationInstallationPolicy(InstallationId, Environment.MachineName, Guid.NewGuid(), Guid.NewGuid(),
                    NotificationMode.Live, ["relay.example.test"], ["outbox@example.test"], ["example.test"], [],
                    "diagnostic@example.test", "https://fixture.invalid", AllowManagedCredentials: true);
                File.WriteAllText(ownedPolicyPath, JsonSerializer.Serialize(policy, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
                File.WriteAllText(ownedPolicyPath + ".generation", policy.SendGeneration.ToString("D"));
            }
            Factory = source.WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, config) =>
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Instance:InstanceId"] = InstallationId, ["Instance:Label"] = "Disposable notification API fixture",
                    ["Notifications:Smtp:Host"] = locked ? "127.0.0.1" : "", ["Notifications:Smtp:Port"] = locked ? "2525" : "",
                    ["Notifications:Smtp:From"] = locked ? "locked@example.test" : "", ["Notifications:BaseUrl"] = locked ? "https://locked.example.test" : "",
                    ["Notifications:Smtp:UserName"] = locked ? "locked-user-sentinel" : "", ["Notifications:Smtp:Password"] = locked ? "locked-credential-sentinel" : "",
                })));
        }
        public void Dispose()
        {
            Factory.Dispose(); source.Dispose();
            if (ownedPolicyPath is not null)
            {
                File.Delete(ownedPolicyPath); File.Delete(ownedPolicyPath + ".generation"); // Only this fixture's random GUID pair.
            }
        }
    }
    private static async Task<JsonElement> OkAsync(HttpResponseMessage response)
    {
        using (response)
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            return await response.Content.ReadFromJsonAsync<JsonElement>();
        }
    }
    private static Task<JsonElement> OverviewAsync(HttpClient admin)
        => ReadOverviewAsync(admin, "/api/operations/notifications");
    private static async Task<JsonElement> ReadOverviewAsync(HttpClient admin, string route)
        => await OkAsync(await admin.GetAsync(route));
    private static async Task<long> VersionAsync(HttpClient admin)
        => (await OverviewAsync(admin)).GetProperty("settings").GetProperty("version").GetInt64();
    private static async Task<(Guid ProjectId, Guid ProgramId)> WorkspaceAsync(HttpClient admin, string code = "NOP")
    {
        using var response = await admin.PostAsJsonAsync("/api/workspaces", new
        { programName = "Notification Operations " + code, programCode = code, projectName = "Notification Project", softwareProduct = "Notification Product", initialRelease = "1.0" });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        return (json.GetProperty("project").GetProperty("id").GetGuid(), json.GetProperty("program").GetProperty("id").GetGuid());
    }
    private static async Task CaptureAsync(HttpClient admin)
    {
        var saved = await OkAsync(await admin.PostAsJsonAsync("/api/operations/notifications/settings", new SettingsRequest(Guid.NewGuid(), await VersionAsync(admin))));
        Assert.Equal("SettingsSaved", saved.GetProperty("result").GetProperty("state").GetString());
        var admitted = await OkAsync(await admin.PostAsJsonAsync("/api/operations/notifications/commands", new
        { operationKey = Guid.NewGuid(), expectedVersion = await VersionAsync(admin), family = "Activate", mode = "Capture" }));
        Assert.Equal("ActivatedFutureEvents", admitted.GetProperty("result").GetProperty("state").GetString());
    }
    private static async Task<(Guid GenerationId, Guid DeliveryId)> DiagnosticGenerationAsync(Host host, HttpClient admin, Guid projectId)
    {
        var receipt = await OkAsync(await admin.PostAsJsonAsync("/api/operations/notifications/transport-test", new
        { projectId, operationKey = Guid.NewGuid(), expectedVersion = await VersionAsync(admin) }));
        var noticeId = receipt.GetProperty("result").GetProperty("notificationId").GetGuid();
        Guid deliveryId;
        await using (var scope = host.Factory.Services.CreateAsyncScope())
            deliveryId = await scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>().NotificationDeliveries
                .Where(x => x.NotificationId == noticeId).Select(x => x.Id).SingleAsync();
        var readmitted = await OkAsync(await admin.PostAsJsonAsync("/api/operations/notifications/commands", new
        { operationKey = Guid.NewGuid(), expectedVersion = await VersionAsync(admin), family = "Readmit", deliveryId }));
        return (readmitted.GetProperty("result").GetProperty("generationId").GetGuid(), deliveryId);
    }

    // A project administrator must fail for lack of GLOBAL authority while all requested records exist
    // on this same host. The old negative control used an unstaffed account and random target instead.
    [Fact]
    public async Task Notification_operations_require_global_authority_for_every_read_and_command()
    {
        using var host = new Host();
        using var admin = host.Factory.CreateClient();
        await SecurityBoundaryTests.BootstrapAndLoginAdministratorAsync(admin);
        var workspace = await WorkspaceAsync(admin);
        await CaptureAsync(admin);
        var generation = await DiagnosticGenerationAsync(host, admin, workspace.ProjectId);
        var overview = await OverviewAsync(admin);
        var version = overview.GetProperty("settings").GetProperty("version").GetInt64();
        var commandKey = Guid.NewGuid();
        var receipt = await OkAsync(await admin.PostAsJsonAsync("/api/operations/notifications/transport-test", new
        { projectId = workspace.ProjectId, operationKey = commandKey, expectedVersion = version }));
        Assert.Equal(commandKey, receipt.GetProperty("operationKey").GetGuid());
        await using (var scope = host.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            foreach (var (name, role) in new[] { ("notification.member", ProgramRole.Engineer), ("notification.projectadmin", ProgramRole.Administrator) })
            {
                var account = new UserAccount(name, name, name + "@example.test", IdentityService.HashPassword(AeroLinkApiFactory.MemberPassword), DateTimeOffset.UtcNow);
                db.AddRange(account, new ProgramMembership(account.Id, workspace.ProgramId, role, "test.setup", DateTimeOffset.UtcNow));
            }
            await db.SaveChangesAsync();
        }
        var reads = new[] { "/api/operations/notifications", $"/api/operations/notifications/operations/{commandKey}?family=TransportTest", $"/api/operations/notifications/generations/{generation.GenerationId}/attempts" };
        foreach (var route in reads) await OkAsync(await admin.GetAsync(route)); // Positive control for every existing row.
        foreach (var name in new string?[] { null, "notification.member", "notification.projectadmin" })
        {
            using var client = host.Factory.CreateClient();
            if (name is not null) await MemberSession.SignInAsync(client, name);
            var expected = name is null ? HttpStatusCode.Unauthorized : HttpStatusCode.Forbidden;
            foreach (var route in reads) { using var refusal = await client.GetAsync(route); Assert.Equal(expected, refusal.StatusCode); }
            using var settings = await client.PostAsJsonAsync("/api/operations/notifications/settings", new SettingsRequest(Guid.NewGuid(), version));
            using var diagnostic = await client.PostAsJsonAsync("/api/operations/notifications/transport-test", new { projectId = workspace.ProjectId, operationKey = Guid.NewGuid(), expectedVersion = version });
            using var control = await client.PostAsJsonAsync("/api/operations/notifications/commands", new { operationKey = Guid.NewGuid(), expectedVersion = version, family = "Suppress", generationId = generation.GenerationId, expectedGenerationVersion = 1 });
            Assert.Equal(expected, settings.StatusCode); Assert.Equal(expected, diagnostic.StatusCode); Assert.Equal(expected, control.StatusCode);
        }
    }

    // Browser and server can otherwise pass independently while an ordinary settings save silently
    // clears hidden credentials or activates old mail. This HTTP owner checks persisted independent rows.
    [Fact]
    public async Task Saving_settings_preserves_hidden_credentials_rejects_stale_edits_and_admits_no_work()
    {
        using var host = new Host(editableCredentials: true);
        using var admin = host.Factory.CreateClient();
        await SecurityBoundaryTests.BootstrapAndLoginAdministratorAsync(admin);
        await WorkspaceAsync(admin);
        const string credential = "credential-not-for-json-sentinel";
        const string username = "username-not-for-json-sentinel";
        var original = new SettingsRequest(Guid.NewGuid(), await VersionAsync(admin), UserName: username, Credential: credential);
        var originalReceipt = await OkAsync(await admin.PostAsJsonAsync("/api/operations/notifications/settings", original));
        var current = await OverviewAsync(admin);
        Assert.False(current.GetProperty("locks").GetProperty("credentials").GetBoolean());
        Assert.DoesNotContain(credential, current.GetRawText()); Assert.DoesNotContain(username, current.GetRawText());
        Assert.True(current.GetProperty("settings").GetProperty("credentialConfigured").GetBoolean());
        var next = new SettingsRequest(Guid.NewGuid(), await VersionAsync(admin), DisplayName: "Changed display name");
        await OkAsync(await admin.PostAsJsonAsync("/api/operations/notifications/settings", next));
        var recovered = await OkAsync(await admin.PostAsJsonAsync("/api/operations/notifications/settings", original));
        Assert.Equal(originalReceipt.GetRawText(), recovered.GetRawText());
        using var changedSecret = await admin.PostAsJsonAsync("/api/operations/notifications/settings", original with { Credential = "different-credential-sentinel" });
        Assert.Equal(HttpStatusCode.Conflict, changedSecret.StatusCode);
        using var stale = await admin.PostAsJsonAsync("/api/operations/notifications/settings", next with { OperationKey = Guid.NewGuid(), DisplayName = "Stale edit" });
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        await using (var scope = host.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var effective = await scope.ServiceProvider.GetRequiredService<NotificationSettingsResolver>().ResolveAsync(default);
            Assert.Equal(username, effective.UserName); Assert.Equal(credential, effective.Credential);
            Assert.Equal(2, await db.NotificationSettingsRevisions.CountAsync());
            Assert.Empty(await db.NotificationAdmissionEpochs.ToListAsync()); Assert.Empty(await db.NotificationDeliveryGenerations.ToListAsync());
            Assert.DoesNotContain(credential, JsonSerializer.Serialize(await db.NotificationSettingsRevisions.ToListAsync()));
            Assert.Empty(await db.NotificationOperations.Where(x => x.OperationKey == original.OperationKey && x.ResultJson.Contains(credential)).ToListAsync());
            var operation = await db.NotificationOperations.SingleAsync(x => x.OperationKey == original.OperationKey);
            var comparison = scope.ServiceProvider.GetRequiredService<NotificationContentProtection>().Unprotect(operation.PayloadHash);
            Assert.Matches("^[A-Fa-f0-9]{64}$", comparison);
            Assert.NotEqual(comparison, operation.PayloadHash);
            Assert.DoesNotContain(credential, comparison);
        }
        var clear = new SettingsRequest(Guid.NewGuid(), await VersionAsync(admin), ClearCredential: true);
        await OkAsync(await admin.PostAsJsonAsync("/api/operations/notifications/settings", clear));
        var cleared = await OverviewAsync(admin);
        Assert.False(cleared.GetProperty("settings").GetProperty("credentialConfigured").GetBoolean());
        Assert.True(cleared.GetProperty("settings").GetProperty("userNameConfigured").GetBoolean());
    }

    [Fact]
    public async Task Installation_locks_ignore_browser_replacements_and_never_echo_protected_values()
    {
        using var host = new Host(locked: true);
        using var admin = host.Factory.CreateClient();
        await SecurityBoundaryTests.BootstrapAndLoginAdministratorAsync(admin);
        var request = new SettingsRequest(Guid.NewGuid(), await VersionAsync(admin), Host: "attacker.invalid", Port: 2526,
            Sender: "attacker@example.test", BaseUrl: "https://attacker.invalid", UserName: "attacker", Credential: "replacement", ClearCredential: true);
        await OkAsync(await admin.PostAsJsonAsync("/api/operations/notifications/settings", request));
        var overview = await OverviewAsync(admin);
        foreach (var field in new[] { "relay", "sender", "baseUrl", "credentials", "externalModes", "diagnosticTarget" }) Assert.True(overview.GetProperty("locks").GetProperty(field).GetBoolean());
        Assert.DoesNotContain("locked-user-sentinel", overview.GetRawText()); Assert.DoesNotContain("locked-credential-sentinel", overview.GetRawText());
        Assert.DoesNotContain("attacker.invalid", overview.GetRawText());
        await using var scope = host.Factory.Services.CreateAsyncScope();
        var effective = await scope.ServiceProvider.GetRequiredService<NotificationSettingsResolver>().ResolveAsync(default);
        Assert.Equal("127.0.0.1", effective.Host); Assert.Equal(2525, effective.Port); Assert.Equal("locked@example.test", effective.Sender);
        Assert.Equal("https://locked.example.test", effective.BaseUrl); Assert.Equal("locked-user-sentinel", effective.UserName); Assert.Equal("locked-credential-sentinel", effective.Credential);
    }

    // Receipt recovery must be checked before CURRENT settings, recipient preference/address, or delivery
    // state. Dispose the committed HTTP response as a client that lost it, then mutate those facts independently.
    [Fact]
    public async Task Committed_diagnostic_receipt_survives_mutable_changes_and_does_not_mean_another_send()
    {
        using var host = new Host();
        using var admin = host.Factory.CreateClient();
        await SecurityBoundaryTests.BootstrapAndLoginAdministratorAsync(admin);
        var workspace = await WorkspaceAsync(admin);
        await CaptureAsync(admin);
        await using (var scope = host.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var preference = new NotificationPreference("admin", DateTimeOffset.UtcNow); preference.SetEmailEnabled(false, DateTimeOffset.UtcNow);
            db.NotificationPreferences.Add(preference); await db.SaveChangesAsync();
        }
        var key = Guid.NewGuid(); var version = await VersionAsync(admin);
        var request = new { projectId = workspace.ProjectId, operationKey = key, expectedVersion = version, recipient = "attacker@example.test" };
        using (var unconsumed = await admin.PostAsJsonAsync("/api/operations/notifications/transport-test", request)) Assert.Equal(HttpStatusCode.OK, unconsumed.StatusCode);
        var original = await ReadOverviewAsync(admin, $"/api/operations/notifications/operations/{key}?family=TransportTest");
        Guid noticeId = original.GetProperty("result").GetProperty("notificationId").GetGuid();
        Assert.Equal("Queued", original.GetProperty("result").GetProperty("state").GetString());
        Guid deliveryId;
        await using (var scope = host.Factory.Services.CreateAsyncScope())
            deliveryId = await scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>().NotificationDeliveries
                .Where(x => x.NotificationId == noticeId).Select(x => x.Id).SingleAsync();
        var readmitted = await OkAsync(await admin.PostAsJsonAsync("/api/operations/notifications/commands", new
        { operationKey = Guid.NewGuid(), expectedVersion = await VersionAsync(admin), family = "Readmit", deliveryId }));
        var generationId = readmitted.GetProperty("result").GetProperty("generationId").GetGuid();
        await using (var scope = host.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var account = await db.UserAccounts.SingleAsync(x => x.UserName == "admin"); account.RefreshDirectoryProfile(account.DisplayName, "changed@example.test");
            var delivery = await db.NotificationDeliveries.SingleAsync(x => x.NotificationId == noticeId);
            Assert.NotEqual(NotificationDeliveryState.Suppressed, delivery.State); // Explicit diagnostics bypass personal opt-out.
            var generation = await db.NotificationDeliveryGenerations.SingleAsync(x => x.Id == generationId);
            var destination = scope.ServiceProvider.GetRequiredService<NotificationContentProtection>().Unprotect(generation.ProtectedAddress);
            Assert.Equal("capture@aerolink.invalid", destination); // The free-form browser target was ignored.
            generation.Hold("Fixture reply unknown after commit", NotificationGenerationState.AcceptanceUnknown);
            delivery.Suppress("Fixture disposition after commit", DateTimeOffset.UtcNow);
            await db.SaveChangesAsync();
        }
        await OkAsync(await admin.PostAsJsonAsync("/api/operations/notifications/settings", new SettingsRequest(Guid.NewGuid(), await VersionAsync(admin), Mode: "Disabled")));
        var recovered = await OkAsync(await admin.PostAsJsonAsync("/api/operations/notifications/transport-test", request));
        Assert.Equal(original.GetRawText(), recovered.GetRawText());
        var lookup = await ReadOverviewAsync(admin, $"/api/operations/notifications/operations/{key}?family=TransportTest");
        Assert.Equal(original.GetRawText(), lookup.GetRawText());
        await using var after = host.Factory.Services.CreateAsyncScope();
        var check = after.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        Assert.Single(await check.UserNotifications.Where(x => x.Type == "NotificationTransportTest").ToListAsync());
        Assert.Single(await check.NotificationOperations.Where(x => x.Family == "TransportTest").ToListAsync());
        Assert.Equal(NotificationDeliveryState.Suppressed, (await check.NotificationDeliveries.SingleAsync(x => x.NotificationId == noticeId)).State);
        Assert.Equal(NotificationGenerationState.AcceptanceUnknown, (await check.NotificationDeliveryGenerations.SingleAsync()).State);
    }

    [Fact]
    public async Task Concurrent_identical_commands_bind_one_intent_and_a_different_valid_payload_conflicts()
    {
        using var host = new Host();
        using var admin = host.Factory.CreateClient();
        await SecurityBoundaryTests.BootstrapAndLoginAdministratorAsync(admin);
        var workspace = await WorkspaceAsync(admin); var other = await WorkspaceAsync(admin, "NOP2");
        await CaptureAsync(admin);
        var key = Guid.NewGuid(); var version = await VersionAsync(admin);
        var request = new { projectId = workspace.ProjectId, operationKey = key, expectedVersion = version };
        var responses = await Task.WhenAll(admin.PostAsJsonAsync("/api/operations/notifications/transport-test", request), admin.PostAsJsonAsync("/api/operations/notifications/transport-test", request));
        var first = await OkAsync(responses[0]); var second = await OkAsync(responses[1]);
        Assert.Equal(first.GetRawText(), second.GetRawText());
        using var conflict = await admin.PostAsJsonAsync("/api/operations/notifications/transport-test", new { projectId = other.ProjectId, operationKey = key, expectedVersion = version });
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        Assert.Equal("operation_conflict", (await conflict.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        await using var scope = host.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        Assert.Single(await db.NotificationOperations.Where(x => x.OperationKey == key).ToListAsync());
        Assert.Single(await db.UserNotifications.Where(x => x.Type == "NotificationTransportTest").ToListAsync());
        Assert.Single(await db.NotificationDeliveries.ToListAsync());
    }

    // HTTP gate owner: use an actual currently alive OS process with exact creation identity, a persisted
    // transmission-start record and an expired lease. This does not claim live SMTP socket qualification.
    [Fact]
    public async Task Replay_and_reissue_require_trusted_quiescence_instead_of_an_expired_lease()
    {
        using var host = new Host(); using var admin = host.Factory.CreateClient();
        await SecurityBoundaryTests.BootstrapAndLoginAdministratorAsync(admin);
        var workspace = await WorkspaceAsync(admin); await CaptureAsync(admin);
        var target = await DiagnosticGenerationAsync(host, admin, workspace.ProjectId);
        long generationVersion; string messageId; string bodyHash;
        await using (var scope = host.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var settings = await scope.ServiceProvider.GetRequiredService<NotificationSettingsResolver>().ResolveAsync(default);
            var protection = scope.ServiceProvider.GetRequiredService<NotificationContentProtection>();
            var generation = await db.NotificationDeliveryGenerations.SingleAsync(x => x.Id == target.GenerationId);
            generation.FreezeMime(protection.Protect("fixture MIME sentinel"), "fixture-body-hash", NotificationDispatcher.MessageConfigurationHash(settings));
            generation.Hold("ReplyLost", NotificationGenerationState.AcceptanceUnknown);
            db.Entry(generation).Property(x => x.LeaseUntilTicks).CurrentValue = DateTimeOffset.UtcNow.AddMinutes(-1).UtcTicks;
            using var worker = Process.GetCurrentProcess();
            var attempt = new NotificationPhysicalAttempt(generation.Id, Guid.NewGuid(), settings.SettingsId, settings.PolicyHash,
                Environment.MachineName, worker.Id, worker.StartTime.ToUniversalTime().Ticks, DateTimeOffset.UtcNow.AddMinutes(-2));
            attempt.Start(DateTimeOffset.UtcNow.AddMinutes(-2)); attempt.Complete(NotificationAttemptOutcome.AcceptanceUnknown, "Data", null, "ReplyLost", false, DateTimeOffset.UtcNow);
            db.NotificationPhysicalAttempts.Add(attempt); await db.SaveChangesAsync();
            generationVersion = generation.Version; messageId = generation.MessageId; bodyHash = generation.BodyHash;
        }
        var version = await VersionAsync(admin);
        foreach (var family in new[] { "Replay", "Reissue" })
        {
            using var refused = await admin.PostAsJsonAsync("/api/operations/notifications/commands", new
            { operationKey = Guid.NewGuid(), expectedVersion = version, family, generationId = target.GenerationId, expectedGenerationVersion = generationVersion, acknowledgeDuplicateRisk = true });
            Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
            Assert.Contains("quiescent", await refused.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
        }
        var attempts = await ReadOverviewAsync(admin, $"/api/operations/notifications/generations/{target.GenerationId}/attempts");
        var visible = (await OverviewAsync(admin)).GetRawText();
        Assert.DoesNotContain("fixture MIME sentinel", visible);
        Assert.DoesNotContain("capture@aerolink.invalid", visible);
        Assert.Equal("unproven", Assert.Single(attempts.GetProperty("attempts").EnumerateArray()).GetProperty("quiescence").GetString());
        await using (var scope = host.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var generation = await db.NotificationDeliveryGenerations.SingleAsync();
            Assert.Equal(NotificationGenerationState.AcceptanceUnknown, generation.State); Assert.Equal(messageId, generation.MessageId); Assert.Equal(bodyHash, generation.BodyHash);
            (await db.NotificationPhysicalAttempts.SingleAsync()).AcknowledgeDisposal(DateTimeOffset.UtcNow); await db.SaveChangesAsync();
        }
        var accepted = await OkAsync(await admin.PostAsJsonAsync("/api/operations/notifications/commands", new
        { operationKey = Guid.NewGuid(), expectedVersion = version, family = "Replay", generationId = target.GenerationId, expectedGenerationVersion = generationVersion, acknowledgeDuplicateRisk = true }));
        Assert.Equal("ReplayQueuedDuplicateRisk", accepted.GetProperty("result").GetProperty("state").GetString());
        await using var after = host.Factory.Services.CreateAsyncScope();
        var final = await after.ServiceProvider.GetRequiredService<AeroLinkDbContext>().NotificationDeliveryGenerations.SingleAsync();
        Assert.Equal(NotificationGenerationState.Pending, final.State); Assert.Equal(messageId, final.MessageId); Assert.Equal(bodyHash, final.BodyHash);
    }

    // Review regression: a generation-only total made the 26th ungenerated held root unreachable.
    // This primary HTTP owner proves real bounded pages and excludes roots already readmitted. Client
    // response fixtures separately own replacing each rendered page; they cannot prove this server query.
    [Fact]
    public async Task Held_roots_have_independent_bounded_pages_and_generated_roots_leave_the_selectable_backlog()
    {
        using var host = new Host(); using var admin = host.Factory.CreateClient();
        await SecurityBoundaryTests.BootstrapAndLoginAdministratorAsync(admin);
        var workspace = await WorkspaceAsync(admin); await CaptureAsync(admin);
        for (var index = 0; index < 26; index++)
            await OkAsync(await admin.PostAsJsonAsync("/api/operations/notifications/transport-test", new
            { operationKey = Guid.NewGuid(), expectedVersion = await VersionAsync(admin), projectId = workspace.ProjectId }));
        var first = await ReadOverviewAsync(admin, "/api/operations/notifications?page=1&pageSize=25&heldPage=1&heldPageSize=25");
        Assert.Equal(0, first.GetProperty("total").GetInt32()); Assert.Empty(first.GetProperty("generations").EnumerateArray());
        Assert.Equal(26, first.GetProperty("heldTotal").GetInt32()); Assert.Equal(1, first.GetProperty("heldPage").GetInt32());
        var firstIds = first.GetProperty("heldDeliveries").EnumerateArray().Select(x => x.GetProperty("id").GetGuid()).ToArray();
        Assert.Equal(25, firstIds.Length);
        var second = await ReadOverviewAsync(admin, "/api/operations/notifications?page=1&pageSize=25&heldPage=2&heldPageSize=25");
        Assert.Equal(2, second.GetProperty("heldPage").GetInt32()); Assert.Equal(26, second.GetProperty("heldTotal").GetInt32());
        var last = Assert.Single(second.GetProperty("heldDeliveries").EnumerateArray()).GetProperty("id").GetGuid();
        Assert.DoesNotContain(last, firstIds);
        await OkAsync(await admin.PostAsJsonAsync("/api/operations/notifications/commands", new
        { operationKey = Guid.NewGuid(), expectedVersion = await VersionAsync(admin), family = "Readmit", deliveryId = last }));
        var after = await ReadOverviewAsync(admin, "/api/operations/notifications?page=1&pageSize=25&heldPage=1&heldPageSize=25");
        Assert.Equal(25, after.GetProperty("heldTotal").GetInt32()); Assert.Equal(1, after.GetProperty("total").GetInt32());
        Assert.DoesNotContain(after.GetProperty("heldDeliveries").EnumerateArray(), x => x.GetProperty("id").GetGuid() == last);
        Assert.Equal(last, Assert.Single(after.GetProperty("generations").EnumerateArray()).GetProperty("deliveryId").GetGuid());
    }

    // HTTP command guard owners. Terminal/frozen input state is seeded through public domain methods;
    // these are no SMTP/quiescence claims. Existing Capture/TLS/process owners qualify physical evidence.
    // The former unconditional terminal guard refused an authorized distinct Live reissue.
    [Fact]
    public async Task A_terminal_capture_can_be_explicitly_reissued_into_a_valid_live_epoch_without_promoting_the_original()
    {
        using var host = new Host(editableCredentials: true); using var admin = host.Factory.CreateClient();
        await SecurityBoundaryTests.BootstrapAndLoginAdministratorAsync(admin);
        var workspace = await WorkspaceAsync(admin); await CaptureAsync(admin);
        var target = await DiagnosticGenerationAsync(host, admin, workspace.ProjectId);
        long generationVersion; string originalMessage;
        await using (var scope = host.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var generation = await db.NotificationDeliveryGenerations.SingleAsync(x => x.Id == target.GenerationId);
            generation.Hold("TerminalCaptureFixture", NotificationGenerationState.Captured);
            await db.SaveChangesAsync(); generationVersion = generation.Version; originalMessage = generation.MessageId;
        }
        await OkAsync(await admin.PostAsJsonAsync("/api/operations/notifications/settings", new SettingsRequest(Guid.NewGuid(),
            await VersionAsync(admin), Mode: "Live", Host: "relay.example.test")));
        await OkAsync(await admin.PostAsJsonAsync("/api/operations/notifications/commands", new
        { operationKey = Guid.NewGuid(), expectedVersion = await VersionAsync(admin), family = "Activate", mode = "Live" }));
        using var replay = await admin.PostAsJsonAsync("/api/operations/notifications/commands", new
        { operationKey = Guid.NewGuid(), expectedVersion = await VersionAsync(admin), family = "Replay", generationId = target.GenerationId, expectedGenerationVersion = generationVersion, acknowledgeDuplicateRisk = true });
        Assert.Equal(HttpStatusCode.BadRequest, replay.StatusCode);
        Assert.Contains("Terminal capture", await replay.Content.ReadAsStringAsync());
        var receipt = await OkAsync(await admin.PostAsJsonAsync("/api/operations/notifications/commands", new
        { operationKey = Guid.NewGuid(), expectedVersion = await VersionAsync(admin), family = "Reissue", generationId = target.GenerationId, expectedGenerationVersion = generationVersion }));
        Assert.Equal("Reissued", receipt.GetProperty("result").GetProperty("state").GetString());
        var replacementId = receipt.GetProperty("result").GetProperty("generationId").GetGuid();
        await using var after = host.Factory.Services.CreateAsyncScope();
        var rows = await after.ServiceProvider.GetRequiredService<AeroLinkDbContext>().NotificationDeliveryGenerations.AsNoTracking().ToListAsync();
        Assert.Equal(2, rows.Count);
        var original = Assert.Single(rows, x => x.Id == target.GenerationId); var replacement = Assert.Single(rows, x => x.Id == replacementId);
        Assert.Equal(NotificationGenerationState.Captured, original.State); Assert.Equal(originalMessage, original.MessageId);
        Assert.Equal(target.GenerationId, replacement.PredecessorId); Assert.Equal(NotificationMode.Live, replacement.Mode);
        Assert.Equal(NotificationGenerationState.Pending, replacement.State); Assert.NotEqual(originalMessage, replacement.MessageId);
        Assert.Equal(original.DeliveryId, replacement.DeliveryId);
    }

    // An empty pre-MIME hash is not evidence that immutable message semantics changed. Conversely,
    // once concrete MIME exists, a changed sender/display/base origin must require distinct reissue.
    [Fact]
    public async Task Pre_mime_blocked_work_can_be_readmitted_but_frozen_mail_with_changed_semantics_requires_reissue()
    {
        using var host = new Host(); using var admin = host.Factory.CreateClient();
        await SecurityBoundaryTests.BootstrapAndLoginAdministratorAsync(admin);
        var workspace = await WorkspaceAsync(admin); await CaptureAsync(admin);
        var target = await DiagnosticGenerationAsync(host, admin, workspace.ProjectId);
        await using (var scope = host.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            (await db.NotificationDeliveryGenerations.SingleAsync(x => x.Id == target.GenerationId)).Hold("FixtureBlockedBeforeMime");
            await db.SaveChangesAsync();
        }
        await OkAsync(await admin.PostAsJsonAsync("/api/operations/notifications/commands", new
        { operationKey = Guid.NewGuid(), expectedVersion = await VersionAsync(admin), family = "Activate", mode = "Capture" }));
        // Hosted fixtures disable background workers. Run the public worker boundary once so the
        // actual epoch fence persists older blocked work as HeldAdmission before any SMTP attempt.
        await using (var scope = host.Factory.Services.CreateAsyncScope())
        {
            var dispatched = await scope.ServiceProvider.GetRequiredService<NotificationDispatcher>().DispatchAsync(25, default);
            Assert.Equal(0, dispatched.Sent);
            Assert.Empty(await scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>().NotificationPhysicalAttempts.ToListAsync());
        }
        long generationVersion; string originalMessage;
        await using (var scope = host.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var generation = await db.NotificationDeliveryGenerations.AsNoTracking().SingleAsync(x => x.Id == target.GenerationId);
            Assert.Equal(NotificationGenerationState.HeldAdmission, generation.State); Assert.Equal("", generation.ProtectedMime);
            generationVersion = generation.Version; originalMessage = generation.MessageId;
        }
        var readmitted = await OkAsync(await admin.PostAsJsonAsync("/api/operations/notifications/commands", new
        { operationKey = Guid.NewGuid(), expectedVersion = await VersionAsync(admin), family = "Readmit", generationId = target.GenerationId, expectedGenerationVersion = generationVersion }));
        Assert.Equal("Readmitted", readmitted.GetProperty("result").GetProperty("state").GetString());
        await using (var scope = host.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var generation = await db.NotificationDeliveryGenerations.SingleAsync(x => x.Id == target.GenerationId);
            var settings = await scope.ServiceProvider.GetRequiredService<NotificationSettingsResolver>().ResolveAsync(default);
            Assert.Equal(NotificationGenerationState.Pending, generation.State); Assert.Equal(settings.AdmissionEpochId, generation.AdmissionEpochId);
            Assert.Equal(originalMessage, generation.MessageId);
            generation.FreezeMime(scope.ServiceProvider.GetRequiredService<NotificationContentProtection>().Protect("Frozen fixture MIME"),
                "frozen-fixture-hash", NotificationDispatcher.MessageConfigurationHash(settings));
            generation.Hold("FrozenFixtureBlocked"); await db.SaveChangesAsync();
        }
        await OkAsync(await admin.PostAsJsonAsync("/api/operations/notifications/settings", new SettingsRequest(Guid.NewGuid(),
            await VersionAsync(admin), DisplayName: "Changed concrete sender display")));
        await OkAsync(await admin.PostAsJsonAsync("/api/operations/notifications/commands", new
        { operationKey = Guid.NewGuid(), expectedVersion = await VersionAsync(admin), family = "Activate", mode = "Capture" }));
        await using (var scope = host.Factory.Services.CreateAsyncScope())
            generationVersion = (await scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>().NotificationDeliveryGenerations.AsNoTracking().SingleAsync(x => x.Id == target.GenerationId)).Version;
        using var refused = await admin.PostAsJsonAsync("/api/operations/notifications/commands", new
        { operationKey = Guid.NewGuid(), expectedVersion = await VersionAsync(admin), family = "Readmit", generationId = target.GenerationId, expectedGenerationVersion = generationVersion });
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode); Assert.Contains("linked reissue", await refused.Content.ReadAsStringAsync());
        var reissued = await OkAsync(await admin.PostAsJsonAsync("/api/operations/notifications/commands", new
        { operationKey = Guid.NewGuid(), expectedVersion = await VersionAsync(admin), family = "Reissue", generationId = target.GenerationId, expectedGenerationVersion = generationVersion }));
        var newId = reissued.GetProperty("result").GetProperty("generationId").GetGuid();
        await using var asserted = host.Factory.Services.CreateAsyncScope();
        var replacement = await asserted.ServiceProvider.GetRequiredService<AeroLinkDbContext>().NotificationDeliveryGenerations.AsNoTracking().SingleAsync(x => x.Id == newId);
        Assert.Equal(target.GenerationId, replacement.PredecessorId); Assert.NotEqual(originalMessage, replacement.MessageId);
    }
}
