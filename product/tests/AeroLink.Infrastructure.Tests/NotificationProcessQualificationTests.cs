using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using AeroLink.Domain.Common;
using AeroLink.Domain.Requirements;
using AeroLink.Domain.Identity;
using AeroLink.Domain.Notifications;
using AeroLink.Domain.Programs;
using AeroLink.Infrastructure.Notifications;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace AeroLink.Infrastructure.Tests;

// Primary process/provider owners: a real paused worker, real TLS peer and real database outage.
// Lease-field assertions alone cannot prove socket exclusion, accepted-receipt recovery or process death.
[Trait("Category", "PostgresQualification")]
public sealed class NotificationProcessQualificationTests
{
    [DisposablePostgresFact]
    public async Task Live_wire_contains_original_identifiers_and_no_private_assignment_prose()
    {
        await using var fixture = await Fixture.CreateAsync("AcceptThenDropQuit");
        var notice = await fixture.QueueAssignmentAsync(); await fixture.RunWorkerAsync();
        Assert.Equal(NotificationGenerationState.SmtpAccepted, (await fixture.GenerationAsync(notice)).State);
        var wire = fixture.DataFacts().Single().GetProperty("wire").GetString()!;
        Assert.Contains("SYSR-00000001", wire); Assert.Contains("https://fixture.invalid/notifications/" + notice.ToString("D"), wire);
        Assert.DoesNotContain("PRIVATE assignment prose", wire); Assert.DoesNotContain("PRIVATE requirement statement", wire);
        Assert.DoesNotContain("PRIVATE personal title", wire); Assert.DoesNotContain("five-day", wire);
        Assert.Contains("RCPT TO:<admin@example.test>", fixture.DataFacts().Single().GetProperty("envelope").GetString());
    }
    [DisposablePostgresFact]
    public async Task Paused_started_process_excludes_linked_and_distinct_roots_after_lease_expiry()
    {
        await using var fixture = await Fixture.CreateAsync("PauseAfterData");
        var first = await fixture.QueueAsync();
        using var worker = fixture.Worker();
        await fixture.WaitFactAsync("DataReceived");
        Suspend(worker);
        try
        {
            await using var db = fixture.Db();
            var started = await db.NotificationPhysicalAttempts.SingleAsync();
            Assert.NotNull(started.TransmissionStartedAt);
            Assert.Equal(worker.Id, started.ProcessId);
            Assert.Equal(worker.StartTime.ToUniversalTime().Ticks, started.ProcessStartTicks);
            await db.NotificationDeliveryGenerations.ExecuteUpdateAsync(x => x.SetProperty(p => p.LeaseUntilTicks, DateTimeOffset.UtcNow.AddMinutes(-1).UtcTicks));
            await fixture.QueueAsync(); // A distinct legitimate source root must also respect the relay bound.
            await fixture.RunWorkerAsync();
            var generation = await fixture.GenerationAsync(first);
            Assert.Equal(NotificationGenerationState.AcceptanceUnknown, generation.State);
            await Assert.ThrowsAsync<DomainException>(() => fixture.ControlAsync("Reissue", generation, true));
            await Assert.ThrowsAsync<DomainException>(() => fixture.ControlAsync("Replay", generation, true));
            Assert.Equal(1, fixture.FactCount("TcpAccepted"));
            Assert.Equal(1, fixture.FactCount("DataReceived"));
        }
        finally { Resume(worker); }
        fixture.ReleaseData();
        await Fixture.ExitedAsync(worker);
        var accepted = await fixture.GenerationAsync(first);
        Assert.Equal(NotificationGenerationState.SmtpAccepted, accepted.State);
        Assert.Equal(1, fixture.FactCount("FinalDataAccepted"));
        // The late factual receipt wins over the earlier lease attention without a second DATA.
        Assert.Equal(1, fixture.FactCount("TcpAccepted"));
    }

    [DisposablePostgresFact]
    public async Task First_lost_data_reply_permits_exactly_one_deliberate_replay_with_identical_wire()
    {
        await using var fixture = await Fixture.CreateAsync("LoseFinalReply");
        var notice = await fixture.QueueAsync();
        await fixture.RunWorkerAsync();
        var unknown = await fixture.GenerationAsync(notice);
        Assert.Equal(NotificationGenerationState.AcceptanceUnknown, unknown.State); Assert.Equal(1, unknown.Attempts);
        var original = fixture.DataFacts().Single();
        await fixture.ControlAsync("Replay", unknown, true);
        await fixture.RestartRelayAsync("AcceptThenDropQuit");
        await Task.Delay(1100); // The real installation-wide connection spacing is part of the contract.
        await fixture.RunWorkerAsync();
        var accepted = await fixture.GenerationAsync(notice);
        Assert.Equal(NotificationGenerationState.SmtpAccepted, accepted.State); Assert.Equal(2, accepted.Attempts);
        var retry = fixture.DataFacts().Last();
        Assert.Equal(original.GetProperty("wire").GetString(), retry.GetProperty("wire").GetString());
        Assert.Equal(original.GetProperty("envelope").GetString(), retry.GetProperty("envelope").GetString());
        await fixture.RunWorkerAsync();
        Assert.Equal(2, fixture.FactCount("TcpAccepted")); Assert.Equal(2, fixture.FactCount("DataReceived"));
    }

    [DisposablePostgresFact]
    public async Task Linked_reissues_racing_workers_share_one_root_and_relay_start_gate()
    {
        await using var fixture = await Fixture.CreateAsync("AcceptThenDropQuit");
        var notice = await fixture.QueueAsync(); await fixture.RunWorkerAsync();
        var accepted = await fixture.GenerationAsync(notice);
        var first = await fixture.ControlAsync("Reissue", accepted);
        var second = await fixture.ControlAsync("Reissue", accepted);
        Assert.NotEqual(first.GenerationId, second.GenerationId);
        await fixture.RestartRelayAsync("PauseAfterData"); await Task.Delay(1100);
        using var workerA = fixture.Worker(); using var workerB = fixture.Worker();
        await fixture.WaitFactAsync("DataReceived", 2);
        await Task.Delay(750);
        Assert.Equal(2, fixture.FactCount("TcpAccepted")); // One original plus exactly one linked successor.
        fixture.ReleaseData(); await Fixture.ExitedAsync(workerA); await Fixture.ExitedAsync(workerB);
        await using var db = fixture.Db();
        var successors = await db.NotificationDeliveryGenerations.Where(x => x.PredecessorId == accepted.Id).ToListAsync();
        Assert.Single(successors.Where(x => x.State == NotificationGenerationState.SmtpAccepted));
        Assert.Single(successors.Where(x => x.State == NotificationGenerationState.Pending));
    }

    [DisposablePostgresFact]
    public async Task Refused_destination_has_shared_cooldown_across_distinct_roots_and_workers()
    {
        await using var fixture = await Fixture.CreateAsync("Refuse451");
        var first = await fixture.QueueAsync(); await fixture.RunWorkerAsync();
        Assert.Equal(NotificationGenerationState.RetryExhausted, (await fixture.GenerationAsync(first)).State); // deliberate diagnostic has one attempt
        await fixture.QueueAsync(); await fixture.RunWorkerAsync();
        Assert.Equal(1, fixture.FactCount("TcpAccepted"));
        await using var db = fixture.Db();
        Assert.Single(await db.NotificationPhysicalAttempts.Where(x => x.TransmissionStartedAt != null).ToListAsync());
        Assert.Contains(await db.NotificationDeliveryGenerations.ToListAsync(), x => x.State == NotificationGenerationState.Pending);
    }

    [DisposablePostgresFact]
    public Task Accepted_data_during_owned_database_outage_recovers_receipt_without_resubmission()
        => AcceptedDuringOutageAsync(false);

    [DisposablePostgresFact]
    public Task Death_before_accepted_receipt_persistence_remains_unknown_without_resubmission()
        => AcceptedDuringOutageAsync(true);

    private static async Task AcceptedDuringOutageAsync(bool killBeforePersistence)
    {
        // This theory deliberately requires the same disposable connection as every provider owner.
        // It never stops the shared server or touches databases belonging to another fixture.
        await using var fixture = await Fixture.CreateAsync("PauseAfterData");
        var notice = await fixture.QueueAsync(); using var worker = fixture.Worker();
        await fixture.WaitFactAsync("DataReceived");
        await fixture.AllowConnectionsAsync(false); fixture.ReleaseData();
        await fixture.WaitFactAsync("FinalDataAccepted"); await Task.Delay(300);
        Assert.False(worker.HasExited); Assert.Equal(1, fixture.FactCount("DataReceived"));
        if (killBeforePersistence) { worker.Kill(true); await worker.WaitForExitAsync(); }
        await fixture.AllowConnectionsAsync(true);
        if (!killBeforePersistence)
        {
            await Fixture.ExitedAsync(worker);
            Assert.Equal(NotificationGenerationState.SmtpAccepted, (await fixture.GenerationAsync(notice)).State);
        }
        else
        {
            await using var db = fixture.Db();
            await db.NotificationDeliveryGenerations.ExecuteUpdateAsync(x => x.SetProperty(p => p.LeaseUntilTicks, DateTimeOffset.UtcNow.AddMinutes(-1).UtcTicks));
            await fixture.RunWorkerAsync();
            Assert.Equal(NotificationGenerationState.AcceptanceUnknown, (await fixture.GenerationAsync(notice)).State);
        }
        await fixture.RunWorkerAsync();
        Assert.Equal(1, fixture.FactCount("DataReceived")); Assert.Equal(1, fixture.FactCount("TcpAccepted"));
    }

    private static void Suspend(Process process)
    {
        if (OperatingSystem.IsWindows()) Assert.Equal(0, NtSuspendProcess(process.Handle));
        else Assert.Equal(0, Kill(process.Id, 19));
    }
    private static void Resume(Process process)
    {
        if (OperatingSystem.IsWindows()) Assert.Equal(0, NtResumeProcess(process.Handle));
        else Assert.Equal(0, Kill(process.Id, 18));
    }
    [DllImport("ntdll.dll")] private static extern int NtSuspendProcess(IntPtr handle);
    [DllImport("ntdll.dll")] private static extern int NtResumeProcess(IntPtr handle);
    [DllImport("libc", EntryPoint = "kill")] private static extern int Kill(int pid, int signal);

    private sealed class Fixture : IAsyncDisposable
    {
        private static readonly string AuthorityRoot = InitializeAuthorityRoot();
        private readonly string root = Path.Combine(Path.GetTempPath(), "aerolink-notification-process-" + Guid.NewGuid().ToString("N"));
        private readonly List<Process> workers = [];
        private DisposablePostgresDatabase database = null!;
        private ServiceProvider services = null!;
        private Process relay = null!;
        private Task relayOutput = Task.CompletedTask;
        private string installation = Guid.NewGuid().ToString("D");
        private int port;
        private Guid project;
        private string PolicyPath => Path.Combine(AuthorityRoot, installation + ".json");
        private string ConfigPath => Path.Combine(root, "worker.json");
        private string RelayRoot => Path.Combine(root, "relay");
        internal static async Task<Fixture> CreateAsync(string reply)
        {
            var value = new Fixture(); Directory.CreateDirectory(value.RelayRoot);
            value.database = await DisposablePostgresDatabase.CreateAsync("notification_process");
            using var key = RSA.Create(2048);
            var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            var san = new SubjectAlternativeNameBuilder(); san.AddDnsName("localhost"); request.CertificateExtensions.Add(san.Build());
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
            request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.1") }, true));
            using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
            File.WriteAllText(Path.Combine(value.RelayRoot, "certificate.pem"), certificate.ExportCertificatePem());
            File.WriteAllText(Path.Combine(value.RelayRoot, "key.pem"), key.ExportPkcs8PrivateKeyPem());
            File.Copy(Path.Combine(AppContext.BaseDirectory, "TestSupport", "NotificationTlsRelay.py"), Path.Combine(value.RelayRoot, "relay.py"));
            await value.StartRelayAsync(reply);
            var config = new Dictionary<string, string?>
            {
                ["Database:Provider"] = "PostgreSql", ["ConnectionStrings:AeroLink"] = value.database.ConnectionString,
                ["Instance:InstanceId"] = value.installation, ["DataProtection:KeyRingPath"] = Path.Combine(value.root, "keys"),
            };
            File.WriteAllText(value.ConfigPath, JsonSerializer.Serialize(config));
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(config).Build();
            var registrations = new ServiceCollection().AddLogging().AddSingleton<IConfiguration>(configuration);
            registrations.AddAeroLinkInfrastructure(configuration); value.services = registrations.BuildServiceProvider();
            var policy = new NotificationInstallationPolicy(value.installation, Environment.MachineName, Guid.NewGuid(), Guid.NewGuid(),
                NotificationMode.Live, ["localhost"], ["sender@example.test"], [], ["admin@example.test"], "diagnostic@example.test", "https://fixture.invalid",
                AllowManagedCredentials: true, TrustAnchorsPem: [certificate.ExportCertificatePem()]);
            File.WriteAllText(value.PolicyPath, JsonSerializer.Serialize(policy, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            File.WriteAllText(value.PolicyPath + ".generation", policy.SendGeneration.ToString("D"));
            await using var scope = value.services.CreateAsyncScope(); var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            await db.Database.MigrateAsync();
            var program = new ProgramRecord("Owned email qualification", "NQ"); var project = new ProjectRecord(program.Id, "Owned", "Owned");
            value.project = project.Id;
            db.AddRange(program, project, new UserAccount(IdentityService.SystemAdministratorUserName, "Administrator", "admin@example.test", IdentityService.HashPassword("Owned!Qualification2026"), DateTimeOffset.UtcNow));
            await db.SaveChangesAsync();
            var operations = scope.ServiceProvider.GetRequiredService<NotificationOperationsService>();
            var saved = await operations.SaveSettingsAsync(IdentityService.SystemAdministratorUserName,
                new(Guid.NewGuid(), 0, NotificationMode.Live, "localhost", value.port, "sender@example.test", "AeroLink", "https://fixture.invalid", ""), default);
            await operations.ControlAsync(IdentityService.SystemAdministratorUserName, new(Guid.NewGuid(), saved.Result.GetProperty("version").GetInt64(), "Activate", NotificationMode.Live), default);
            return value;
        }
        internal AeroLinkDbContext Db() => new(new DbContextOptionsBuilder<AeroLinkDbContext>().UseNpgsql(database.ConnectionString).Options);
        internal async Task<Guid> QueueAsync()
        {
            await using var scope = services.CreateAsyncScope(); var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var state = await db.NotificationInstallationStates.SingleAsync();
            var receipt = await scope.ServiceProvider.GetRequiredService<NotificationOperationsService>().QueueDiagnosticAsync(IdentityService.SystemAdministratorUserName,
                new(project, Guid.NewGuid(), state.Version), default);
            return receipt.Result.GetProperty("notificationId").GetGuid();
        }
        internal async Task<Guid> QueueAssignmentAsync()
        {
            await using var scope = services.CreateAsyncScope(); var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>(); var now = DateTimeOffset.UtcNow;
            var artifact = new AeroLink.Domain.Requirements.RequirementArtifact(project, "SYSR-00000001", AeroLink.Domain.ChangeControl.RequirementLevel.System, now);
            var assignment = new AeroLink.Domain.Requirements.ArtifactAssignment(project, "Requirement", artifact.Id, null,
                IdentityService.SystemAdministratorUserName, "PRIVATE personal title", "PRIVATE assignment prose", null, "author", now);
            var notice = new UserNotification(project, IdentityService.SystemAdministratorUserName, "RequirementAssignment", "PRIVATE requirement statement", "PRIVATE assignment prose", "", artifact.Id, now);
            notice.BindContext(NotificationContext.RequirementAssignment(notice, artifact, assignment)); db.AddRange(artifact, assignment, notice); await db.SaveChangesAsync(); return notice.Id;
        }
        internal async Task<NotificationDeliveryGeneration> GenerationAsync(Guid notice)
        { await using var db = Db(); return await (from root in db.NotificationDeliveries join generation in db.NotificationDeliveryGenerations on root.Id equals generation.DeliveryId where root.NotificationId == notice && generation.PredecessorId == null select generation).SingleAsync(); }
        internal async Task<NotificationOperation> ControlAsync(string family, NotificationDeliveryGeneration generation, bool ack = false)
        {
            await using var scope = services.CreateAsyncScope(); var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var state = await db.NotificationInstallationStates.SingleAsync();
            var receipt = await scope.ServiceProvider.GetRequiredService<NotificationOperationsService>().ControlAsync(IdentityService.SystemAdministratorUserName,
                new(Guid.NewGuid(), state.Version, family, GenerationId: generation.Id, ExpectedGenerationVersion: generation.Version, AcknowledgeDuplicateRisk: ack), default);
            return await db.NotificationOperations.SingleAsync(x => x.Id == receipt.Id);
        }
        internal Process Worker()
        {
            var current = new DirectoryInfo(AppContext.BaseDirectory); while (current is not null && !File.Exists(Path.Combine(current.FullName, "AeroLink.slnx"))) current = current.Parent;
            if (current is null) throw new InvalidOperationException("Qualification solution root missing.");
            var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
            var assembly = Path.Combine(current.FullName, "tests", "AeroLink.NotificationQualificationHost", "bin", configuration, "net10.0", "AeroLink.NotificationQualificationHost.dll");
            if (!File.Exists(assembly)) throw new InvalidOperationException("Required qualification host was not built by the infrastructure project.");
            var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            start.ArgumentList.Add(assembly); start.ArgumentList.Add(ConfigPath); start.Environment["AEROLINK_NOTIFICATION_AUTHORITY_ROOT"] = AuthorityRoot;
            var process = Process.Start(start)!; workers.Add(process); return process;
        }
        internal async Task RunWorkerAsync() { using var worker = Worker(); await ExitedAsync(worker); }
        internal static async Task ExitedAsync(Process process)
        { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(40)); Assert.Equal(0, process.ExitCode); }
        internal int FactCount(string name) => Facts().Count(x => x.GetProperty("event").GetString() == name);
        internal JsonElement[] DataFacts() => Facts().Where(x => x.GetProperty("event").GetString() == "DataReceived").ToArray();
        private JsonElement[] Facts()
        {
            var path = Path.Combine(RelayRoot, "events.jsonl"); if (!File.Exists(path)) return [];
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream); var content = reader.ReadToEnd();
            var complete = content.LastIndexOf('\n'); if (complete < 0) return [];
            return content[..complete].Split('\n').Where(x => x.Length > 0).Select(x => JsonDocument.Parse(x).RootElement.Clone()).ToArray();
        }
        internal async Task WaitFactAsync(string name, int count = 1)
        { var until = DateTimeOffset.UtcNow.AddSeconds(30); while (FactCount(name) < count && DateTimeOffset.UtcNow < until) await Task.Delay(50); Assert.True(FactCount(name) >= count, "Missing actual relay fact: " + name); }
        internal void ReleaseData() => File.WriteAllText(Path.Combine(RelayRoot, "release-data"), "release");
        private async Task StartRelayAsync(string reply)
        {
            var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("AEROLINK_TEST_PYTHON") ?? (OperatingSystem.IsWindows() ? "python" : "python3"))
            { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            foreach (var argument in new[] { Path.Combine(RelayRoot, "relay.py"), RelayRoot, reply, port.ToString(), "90", "16" }) start.ArgumentList.Add(argument);
            relay = Process.Start(start)!;
            port = int.Parse((await relay.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)))!);
            relayOutput = Task.WhenAll(relay.StandardOutput.ReadToEndAsync(), relay.StandardError.ReadToEndAsync());
        }
        internal async Task RestartRelayAsync(string reply)
        { if (!relay.HasExited) relay.Kill(true); await relay.WaitForExitAsync(); await relayOutput; relay.Dispose(); File.Delete(Path.Combine(RelayRoot, "release-data")); await StartRelayAsync(reply); }
        internal async Task AllowConnectionsAsync(bool allow)
        {
            await using var admin = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(database.ConnectionString) { Database = "postgres" }.ConnectionString); await admin.OpenAsync();
            await using var command = admin.CreateCommand(); command.CommandText = $"ALTER DATABASE \"{database.Name}\" ALLOW_CONNECTIONS {(allow ? "true" : "false")}"; await command.ExecuteNonQueryAsync();
            if (!allow) { command.CommandText = "SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname=@owned AND pid <> pg_backend_pid()"; command.Parameters.AddWithValue("owned", database.Name); await command.ExecuteNonQueryAsync(); }
            else { using var ownedPool = new NpgsqlConnection(database.ConnectionString); NpgsqlConnection.ClearPool(ownedPool); }
        }
        private static string InitializeAuthorityRoot()
        {
            var supplied = Environment.GetEnvironmentVariable("AEROLINK_NOTIFICATION_AUTHORITY_ROOT"); if (!string.IsNullOrWhiteSpace(supplied)) return supplied;
            var owned = Path.Combine(Path.GetTempPath(), "aerolink-process-authority-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(owned);
            Environment.SetEnvironmentVariable("AEROLINK_NOTIFICATION_AUTHORITY_ROOT", owned); return owned;
        }
        public async ValueTask DisposeAsync()
        {
            foreach (var worker in workers) { try { if (!worker.HasExited) { worker.Kill(true); await worker.WaitForExitAsync(); } } catch (InvalidOperationException) { } }
            if (!relay.HasExited) relay.Kill(true); await relay.WaitForExitAsync(); await relayOutput; relay.Dispose();
            await AllowConnectionsAsync(true); await services.DisposeAsync(); NpgsqlConnection.ClearAllPools(); await database.DisposeAsync();
            File.Delete(PolicyPath); File.Delete(PolicyPath + ".generation"); Directory.Delete(root, true);
        }
    }
}
