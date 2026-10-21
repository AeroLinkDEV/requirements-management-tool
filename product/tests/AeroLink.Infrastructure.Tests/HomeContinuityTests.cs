using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.AccessControl;
using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;
using System.Xml.Linq;
using AeroLink.Domain.Integrations;
using AeroLink.Domain.Identity;
using AeroLink.Domain.Programs;
using AeroLink.ConnectorProtocol;
using AeroLink.Infrastructure.Persistence;
using AeroLink.Infrastructure.Persistence.Maintenance;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace AeroLink.Infrastructure.Tests;

/// <summary>
/// Authoring gate for HOME continuity:
/// the observable contract is preservation of key identity, revocations and old-discriminator payloads;
/// a credible regression is an exporter that writes plaintext, changes key metadata, drops revocations, or
/// makes the canonical provider silently accept old ciphertext; existing installation tests do not cross a
/// certificate envelope, a destination DPAPI ring and a source-discriminator fallback together.  This test
/// exercises those production boundaries and needs no test-only production seam.
/// </summary>
public sealed class HomeContinuityTests
{
    // Authoring gate (#1654): HOME's genuine connector key can live outside its persistent installation.
    // The existing roundtrip uses a Site-shaped key beneath that root and cannot catch the refusal.
    // This owns admission/encryption at the actual export boundary, without a test-only production seam.
    // A broad containment removal would incorrectly admit the Site and mismatched-instance rows.
    [WindowsFact]
    public void Export_binds_an_external_HOME_connector_to_its_declared_instance_and_user()
    {
        var root = Path.Combine(Path.GetTempPath(), $"aerolink-home-layout-{Guid.NewGuid():N}");
        var installation = Path.Combine(root, "persistent");
        var connectorPath = Path.Combine(root, "profile", "connector.pem");
        var package = Path.Combine(root, "package");
        Directory.CreateDirectory(installation);
        Directory.CreateDirectory(Path.GetDirectoryName(connectorPath)!);
        try
        {
            var instanceId = Guid.NewGuid().ToString("D");
            File.WriteAllText(Path.Combine(installation, "instance.json"), JsonSerializer.Serialize(new
            {
                classification = "HomeCanonical", instanceId,
            }));
            using var signingKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var pem = signingKey.ExportPkcs8PrivateKeyPem();
            File.WriteAllText(connectorPath, pem);
            var fingerprint = ConnectorLaunchProtocol.PublicKeyFingerprint(signingKey.ExportSubjectPublicKeyInfoPem());
            var repository = new MemoryXmlRepository();
            using var services = BuildServices(repository, "actual-source-discriminator\\");
            var manager = services.GetRequiredService<IKeyManager>();
            var sourceKey = manager.CreateNewKey(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));
            using var transportKey = RSA.Create(2048);
            using var certificate = new CertificateRequest("CN=layout fixture", transportKey,
                HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
                .CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
            var binding = new HomeContinuityIdentityBinding(instanceId, "actual-source-discriminator\\", "AeroLink",
                "Unprotected", new string('a', 64), "deployment", fingerprint[..24], fingerprint, new string('a', 64));
            var material = new HomeContinuityConnectorMaterial("deployment", fingerprint[..24], fingerprint,
                pem, installation, connectorPath, CurrentSid());
            var manifest = HomeContinuityKeyTransfer.ExportPackage(manager, repository, certificate, binding, material, package);
            Assert.Equal(sourceKey.KeyId, Assert.Single(manifest.Keys).KeyId);
            Assert.Equal(installation, manifest.Connector.SourceInstallationRoot);
            Assert.Equal(connectorPath, manifest.Connector.SourceConnectorPath);
            Assert.Equal(CurrentSid(), manifest.Connector.SourceServiceSid);
            Assert.Equal("actual-source-discriminator\\", manifest.SourceApplicationDiscriminator);
            Assert.DoesNotContain(pem, File.ReadAllText(Path.Combine(package, "manifest.json")));
            Assert.DoesNotContain(pem, System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(package, manifest.Connector.FileName))));
            foreach (var declaration in new[] {
                new { classification = "HomeCanonical", instanceId = Guid.NewGuid().ToString("D") },
                new { classification = "SiteProduction", instanceId },
            })
            {
                File.WriteAllText(Path.Combine(installation, "instance.json"), JsonSerializer.Serialize(declaration));
                var refusedPackage = Path.Combine(root, "refused");
                Assert.Throws<InvalidOperationException>(() => HomeContinuityKeyTransfer.ExportPackage(
                    manager, repository, certificate, binding, material, refusedPackage));
                Assert.False(Directory.Exists(refusedPackage));
            }
            File.WriteAllText(Path.Combine(installation, "instance.json"), JsonSerializer.Serialize(new
                { classification = "HomeCanonical", instanceId }));
            Assert.Throws<InvalidOperationException>(() => HomeContinuityKeyTransfer.ExportPackage(
                manager, repository, certificate, binding, material with { SourceServiceSid = "S-1-5-18" },
                Path.Combine(root, "wrong-user")));
            Assert.False(Directory.Exists(Path.Combine(root, "wrong-user")));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Source_provider_is_bound_to_the_captured_ring_and_discriminator()
    {
        var root = Path.Combine(Path.GetTempPath(), $"aerolink-home-source-ring-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            const string application = "captured-source-discriminator";
            using (var original = BuildFileServices(root, application))
            {
                var created = original.GetRequiredService<IKeyManager>()
                    .CreateNewKey(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));
                Assert.NotEqual(Guid.Empty, created.KeyId);
            }

            using var captured = HomeContinuityKeyTransfer.BuildSourceServices(root, application, "Unprotected");
            var keys = captured.GetRequiredService<IKeyManager>().GetAllKeys();
            Assert.Single(keys);
            Assert.Equal(application, captured.GetRequiredService<Microsoft.Extensions.Options.IOptions<Microsoft.AspNetCore.DataProtection.DataProtectionOptions>>().Value.ApplicationDiscriminator);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// Authoring gate: this owns the Windows source-ring compatibility boundary when the serialized key
    /// descriptor is the only honest evidence of DPAPI scope. A regression that selects CurrentUser or
    /// LocalMachine while reading an opposite-scope descriptor fails to load the captured key; existing source
    /// provider tests use an unprotected ring and cannot see that mistake. The fixture writes one non-default
    /// temporary ring through the real Data Protection builder and reads it through the production source reader;
    /// no test-only production seam or implicit profile path is involved.
    /// </summary>
    [WindowsFact]
    public void Source_provider_uses_the_serialized_windows_dpapi_decryptor_without_reprotecting_the_ring()
    {
        var root = Path.Combine(Path.GetTempPath(), $"aerolink-home-windows-dpapi-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            const string application = "captured-windows-dpapi-discriminator";
            var seedServices = new ServiceCollection();
            seedServices.AddDataProtection()
                .SetApplicationName(application)
                .PersistKeysToFileSystem(new DirectoryInfo(root))
                .ProtectKeysWithDpapi();
            using (var original = seedServices.BuildServiceProvider())
            {
                var created = original.GetRequiredService<IKeyManager>()
                    .CreateNewKey(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));
                Assert.NotEqual(Guid.Empty, created.KeyId);
            }

            var before = HomeContinuityCompatibilityReader.ComputeRingManifestSha256(root);
            using var captured = HomeContinuityKeyTransfer.BuildSourceServices(
                root, application, HomeContinuityKeyTransfer.SerializedWindowsDpapiProvider);
            var keys = captured.GetRequiredService<IKeyManager>().GetAllKeys();
            Assert.Single(keys);
            Assert.Equal(before, HomeContinuityCompatibilityReader.ComputeRingManifestSha256(root));
            var payload = captured.GetRequiredService<IDataProtectionProvider>()
                .CreateProtector("synthetic-windows-dpapi")
                .Protect("serialized-descriptor-canary");
            Assert.Equal("serialized-descriptor-canary", captured.GetRequiredService<IDataProtectionProvider>()
                .CreateProtector("synthetic-windows-dpapi").Unprotect(payload));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Destination_boundary_requires_canonical_application_ring_and_protector()
    {
        var root = Path.Combine(Path.GetTempPath(), $"aerolink-home-destination-{Guid.NewGuid():N}");
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["DataProtection:ApplicationName"] = "WrongApplication",
            ["DataProtection:KeyRingPath"] = root,
            ["DataProtection:ProtectKeysWith"] = "DpapiLocalMachine",
        }).Build();

        var error = Assert.Throws<InvalidOperationException>(() =>
            HomeContinuityKeyTransfer.ValidateDestinationConfiguration(configuration, root));
        Assert.Contains("AeroLink", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Authoring gate: this owns the complete source-compatible catalog contract. The regression is a reader
    /// that accepts a caller-selected subset or emits a successful result without unprotecting every durable
    /// field. Existing transfer tests only exercise a synthetic purpose and do not query the restored schema;
    /// this owner crosses the real EF entities, exact purposes, prefix classification, and no-write boundary.
    /// </summary>
    [Fact]
    public async Task Compatibility_reader_unprotects_complete_catalog_without_writing_ring_or_secrets()
    {
        var root = Path.Combine(Path.GetTempPath(), $"aerolink-home-compatibility-reader-{Guid.NewGuid():N}");
        var ring = Path.Combine(root, "ring");
        var contentRoot = Path.Combine(root, "content");
        Directory.CreateDirectory(contentRoot);
        var executable = Environment.ProcessPath!;
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        try
        {
            Directory.CreateDirectory(ring);
            const string discriminator = "captured-source-content-root";
            using (var seedServices = BuildFileServices(ring, discriminator))
            {
                var seedManager = seedServices.GetRequiredService<IKeyManager>();
                var created = seedManager.CreateNewKey(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));
                Assert.NotEqual(Guid.Empty, created.KeyId);
            }

            using var sourceServices = HomeContinuityKeyTransfer.BuildSourceServices(ring, discriminator, "Unprotected");
            var sourceProvider = sourceServices.GetRequiredService<IDataProtectionProvider>();
            var mfa = "dp:v1:" + sourceProvider.CreateProtector("AeroLink.Identity.MfaSecret.v1").Protect("synthetic-mfa-secret");
            var webhook = sourceProvider.CreateProtector("AeroLink.Webhooks.SigningSecret.v1").Protect("synthetic-webhook-secret");
            var options = new DbContextOptionsBuilder<AeroLinkDbContext>().UseSqlite(connection).Options;
            await using var db = new AeroLinkDbContext(options);
            await db.Database.EnsureCreatedAsync();
            var user = new UserAccount("compatibility", "Compatibility", "compatibility@example.test", "password-hash", DateTimeOffset.UtcNow);
            db.UserAccounts.Add(user);
            db.UserMfaEnrollments.Add(new UserMfaEnrollment(user.Id, mfa, "test", DateTimeOffset.UtcNow));
            var program = new ProgramRecord("Compatibility", "COMPAT");
            var project = new ProjectRecord(program.Id, "Compatibility project", "AeroLink");
            db.Programs.Add(program);
            db.Projects.Add(project);
            db.WebhookSubscriptions.Add(new WebhookSubscription(project.Id, "test", "https://example.test/hook", "[]", webhook, "test", DateTimeOffset.UtcNow));
            await db.SaveChangesAsync();

            var sourceInstanceId = Guid.NewGuid().ToString("D");
            var continuityFiles = CreateSyntheticContinuityEvidence(root, sourceInstanceId);
            var binding = CompatibilityBinding(root, ring, contentRoot, executable,
                HomeContinuityCompatibilityReader.ComputeRingManifestSha256(ring), discriminator, continuityFiles, sourceInstanceId);
            var before = HomeContinuityCompatibilityReader.ComputeRingManifestSha256(ring);
            var receipt = await HomeContinuityCompatibilityReader.ProbeAsync(db, sourceServices, binding);
            var after = HomeContinuityCompatibilityReader.ComputeRingManifestSha256(ring);

            Assert.Equal(HomeContinuityCompatibilityReader.MetadataCaptured, receipt.Status);
            Assert.Equal(HomeContinuityCompatibilityReader.ProtectedFieldCatalog.Count, receipt.ProtectedCategories.Count);
            Assert.Equal(before, receipt.RingManifestSha256Before);
            Assert.Equal(after, receipt.RingManifestSha256After);
            Assert.Equal("passed", receipt.ProtectedCategories.Single(x => x.Definition.Category == "mfa-secret").Status);
            Assert.Equal("passed", receipt.ProtectedCategories.Single(x => x.Definition.Category == "webhook-secret").Status);
            Assert.All(receipt.ProtectedCategories.Where(x => x.Definition.Category is not "mfa-secret" and not "webhook-secret"),
                category => Assert.Equal(HomeContinuityCompatibilityReader.NotApplicable, category.Status));
            Assert.DoesNotContain("synthetic-mfa-secret", JsonSerializer.Serialize(receipt), StringComparison.Ordinal);
            Assert.Contains(receipt.ReissueCategories, x => x.StartsWith("user_sessions.TokenHash", StringComparison.Ordinal));

            var expectedCatalog = new[]
            {
                ("mfa-secret", "user_mfa_enrollments", "Secret", "AeroLink.Identity.MfaSecret.v1", "dp:v1:"),
                ("webhook-secret", "webhook_subscriptions", "ProtectedSecret", "AeroLink.Webhooks.SigningSecret.v1", (string?)null),
                ("jira-token", "jira_connections", "ProtectedApiToken", "AeroLink.Jira.ApiToken.v1", (string?)null),
                ("notification-credential", "notification_settings_revisions", "ProtectedCredential", HomeContinuityCompatibilityReader.NotificationPurpose, (string?)null),
                ("notification-address", "notification_delivery_generations", "ProtectedAddress", HomeContinuityCompatibilityReader.NotificationPurpose, (string?)null),
                ("notification-mime", "notification_delivery_generations", "ProtectedMime", HomeContinuityCompatibilityReader.NotificationPurpose, (string?)null),
                ("notification-blocked-settings", "notification_delivery_generations", "BlockedEffectiveSettingsHash", HomeContinuityCompatibilityReader.NotificationPurpose, (string?)null),
                ("notification-settings-snapshot", "notification_physical_attempts", "ProtectedSettingsSnapshot", HomeContinuityCompatibilityReader.NotificationPurpose, (string?)null),
                ("notification-effective-settings", "notification_physical_attempts", "EffectiveSettingsHash", HomeContinuityCompatibilityReader.NotificationPurpose, (string?)null),
                ("notification-operation-payload", "notification_operations", "PayloadHash", HomeContinuityCompatibilityReader.NotificationPurpose, (string?)null),
            };
            Assert.Equal(expectedCatalog.Length, receipt.ProtectedCategories.Count);
            Assert.Equal(expectedCatalog,
                receipt.ProtectedCategories.Select(x =>
                    (x.Definition.Category, x.Definition.Table, x.Definition.Column, x.Definition.Purpose, x.Definition.RequiredPrefix)));
            HomeContinuityCompatibilityReader.ValidateReceipt(receipt);
            var incomplete = receipt with { ProtectedCategories = receipt.ProtectedCategories.Skip(1).ToArray() };
            Assert.Throws<ArgumentException>(() => HomeContinuityCompatibilityReader.ValidateReceipt(incomplete));
            var mismatchedInstance = receipt with
            {
                Binding = receipt.Binding with { SourceInstanceId = Guid.NewGuid() },
            };
            Assert.Throws<ArgumentException>(() => HomeContinuityCompatibilityReader.ValidateReceipt(mismatchedInstance));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Compatibility_reader_reports_empty_catalog_without_claiming_proof()
    {
        var receipt = await ProbeSyntheticAsync(mfaValue: null);
        Assert.Equal(HomeContinuityCompatibilityReader.NoApplicableValues, receipt.Status);
        Assert.All(receipt.ProtectedCategories,
            category => Assert.Equal(HomeContinuityCompatibilityReader.NotApplicable, category.Status));
    }

    [Fact]
    public async Task Compatibility_reader_classifies_legacy_plaintext_without_claiming_proof()
    {
        var receipt = await ProbeSyntheticAsync("legacy-plaintext-secret");
        Assert.Equal(HomeContinuityCompatibilityReader.LegacyPlaintextPresent, receipt.Status);
        var mfa = receipt.ProtectedCategories.Single(x => x.Definition.Category == "mfa-secret");
        Assert.Equal("legacy-plaintext", mfa.Status);
        Assert.Equal(1, mfa.LegacyPlaintextCount);
    }

    [Fact]
    public void Compatibility_reader_refuses_persistent_database_port_before_reads()
    {
        var options = new DbContextOptionsBuilder<AeroLinkDbContext>().UseSqlite("Data Source=:memory:").Options;
        using var db = new AeroLinkDbContext(options);
        var error = Assert.Throws<InvalidOperationException>(() =>
            HomeContinuityCompatibilityReader.AssertDisposableRestoredDatabase(db, "aerolink_restore_validation", 54329));
        Assert.Contains("54329", error.Message, StringComparison.Ordinal);
    }

    private static HomeContinuityCompatibilityBinding CompatibilityBinding(
        string root, string ring, string contentRoot, string executable, string ringDigest, string discriminator,
        (string Archive, string Proof, string Script, string Configuration) evidence, string sourceInstanceId) =>
        new(
            sourceInstanceId,
            Guid.Parse(sourceInstanceId),
            Process.GetCurrentProcess().Id,
            Process.GetCurrentProcess().StartTime.ToUniversalTime(),
            executable,
            Sha256File(executable),
            contentRoot,
            evidence.Configuration,
            CurrentSid(),
            CurrentSid(),
            Sha256File(evidence.Configuration),
            ring,
            ringDigest,
            discriminator,
            HomeContinuityCompatibilityReader.UnknownProtectionScope,
            "Unprotected",
            "verified-backup-1",
            evidence.Archive,
            Sha256File(evidence.Archive),
            "aerolink_restore_validation",
            55928,
            evidence.Proof,
            evidence.Script,
            Sha256File(Process.GetCurrentProcess().MainModule!.FileName!),
            Sha256File(typeof(HomeContinuityCompatibilityReader).Assembly.Location),
            HomeContinuityCompatibilityReader.ExplicitRingRead,
            "reader-process",
            HomeContinuityCompatibilityReader.ExternalProtectedCategories.ToDictionary(x => x, _ => "absent"));

    private static (string Archive, string Proof, string Script, string Configuration) CreateSyntheticContinuityEvidence(
        string root, string sourceInstanceId)
    {
        var archive = Path.Combine(root, "aerolink-home.zip");
        var configuration = Path.Combine(root, "source-config.json");
        File.WriteAllText(configuration, "{\"source\":\"synthetic\"}");
        var manifest = new
        {
            FormatVersion = 2,
            Instance = new { Classification = "HomeCanonical", InstanceId = sourceInstanceId },
            Application = new { SourceSha = new string('d', 64) },
        };
        using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create))
        {
            using (var writer = new StreamWriter(zip.CreateEntry("manifest.json").Open()))
                writer.Write(JsonSerializer.Serialize(manifest));
            using (var dump = zip.CreateEntry("aerolink-postgresql.dump").Open())
                dump.Write([1, 2, 3]);
        }
        File.WriteAllText(archive + ".sha256", $"{Sha256File(archive)}  {Path.GetFileName(archive)}");
        var script = RepositoryFile("product/scripts/Restore-AeroLink.ps1");
        var proof = Path.Combine(root, "restore-proof.json");
        File.WriteAllText(proof, JsonSerializer.Serialize(new
        {
            FormatVersion = 1,
            ArchiveSha256 = Sha256File(archive),
            DatabaseName = "aerolink_restore_validation",
            PostgresPort = 55928,
            RestoreValidationRoot = root,
            RestoreScriptSha256 = Sha256File(script),
            AttachmentInventoryVerified = true,
        }));
        return (archive, proof, script, configuration);
    }

    private static string RepositoryFile(string relative)
    {
        var directory = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, relative);
            if (File.Exists(candidate)) return candidate;
            directory = directory.Parent;
        }
        throw new FileNotFoundException($"Repository file not found: {relative}");
    }

    private static string CurrentSid() => OperatingSystem.IsWindows()
        ? System.Security.Principal.WindowsIdentity.GetCurrent().User!.Value
        : "S-1-5-21-123-456-789-1001";

    private static async Task<HomeContinuityCompatibilityReceipt> ProbeSyntheticAsync(string? mfaValue)
    {
        var root = Path.Combine(Path.GetTempPath(), $"aerolink-home-compatibility-status-{Guid.NewGuid():N}");
        var ring = Path.Combine(root, "ring");
        var contentRoot = Path.Combine(root, "content");
        Directory.CreateDirectory(contentRoot);
        var executable = Environment.ProcessPath!;
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        try
        {
            Directory.CreateDirectory(ring);
            const string discriminator = "captured-source-content-root";
            using (var seedServices = BuildFileServices(ring, discriminator))
            {
                var created = seedServices.GetRequiredService<IKeyManager>()
                    .CreateNewKey(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));
                Assert.NotEqual(Guid.Empty, created.KeyId);
            }

            using var sourceServices = HomeContinuityKeyTransfer.BuildSourceServices(ring, discriminator, "Unprotected");
            var options = new DbContextOptionsBuilder<AeroLinkDbContext>().UseSqlite(connection).Options;
            await using var db = new AeroLinkDbContext(options);
            await db.Database.EnsureCreatedAsync();
            if (mfaValue is not null)
            {
                var user = new UserAccount("compatibility-status", "Compatibility", "compatibility-status@example.test", "password-hash", DateTimeOffset.UtcNow);
                db.UserAccounts.Add(user);
                db.UserMfaEnrollments.Add(new UserMfaEnrollment(user.Id, mfaValue, "test", DateTimeOffset.UtcNow));
                await db.SaveChangesAsync();
            }

            var sourceInstanceId = Guid.NewGuid().ToString("D");
            var evidence = CreateSyntheticContinuityEvidence(root, sourceInstanceId);
            var binding = CompatibilityBinding(root, ring, contentRoot, executable,
                HomeContinuityCompatibilityReader.ComputeRingManifestSha256(ring), discriminator, evidence, sourceInstanceId);
            return await HomeContinuityCompatibilityReader.ProbeAsync(db, sourceServices, binding);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [WindowsFact]
    public void Receipt_bound_abort_refuses_mismatch_and_cleans_only_the_owned_empty_stage()
    {
        var root = Path.Combine(Path.GetTempPath(), $"aerolink-home-recovery-{Guid.NewGuid():N}");
        var package = Path.Combine(root, "package");
        var destinationInstallation = Path.Combine(root, "destination-installation");
        var destination = Path.Combine(destinationInstallation, "keys", "data-protection");
        var destinationConnector = Path.Combine(destinationInstallation, "keys", "connector", "connector-signing-key.pem");
        var sourceInstallation = Path.Combine(root, "source-installation");
        var destinationServiceSid = System.Security.Principal.WindowsIdentity.GetCurrent().User!.Value;
        var packageId = Guid.NewGuid();
        var keyId = Guid.NewGuid();
        var connectorDeployment = "connector-deployment";
        var connectorFingerprint = new string('b', 64);
        var binding = new HomeContinuityIdentityBinding(
            "source-installation", "captured-source", "AeroLink", "Unprotected", new string('a', 64),
            connectorDeployment, connectorFingerprint[..24], connectorFingerprint, new string('a', 64));
        Directory.CreateDirectory(package);
        Directory.CreateDirectory(destinationInstallation);
        Directory.CreateDirectory(destination);
        SetCanonicalRingAcl(destination, destinationServiceSid);
        try
        {
            var keyPath = Path.Combine(package, "key.xml");
            var connectorPath = Path.Combine(package, "connector-key.cms");
            File.WriteAllText(keyPath, "<key />");
            File.WriteAllText(connectorPath, "synthetic encrypted connector envelope");
            var creation = DateTimeOffset.UtcNow.AddMinutes(-2);
            var key = new HomeContinuityKeyManifestEntry(keyId, creation, creation, creation.AddDays(1),
                "key.xml", Sha256File(keyPath));
            var connector = new HomeContinuityConnectorManifest(
                "connector-key.cms", Sha256File(connectorPath), connectorDeployment, connectorFingerprint[..24],
                connectorFingerprint, sourceInstallation, Path.Combine(sourceInstallation, "connector.pem"),
                "S-1-5-80-1234");
            var manifest = new HomeContinuityPackageManifest(
                2, packageId, DateTimeOffset.UtcNow, binding.SourceApplicationDiscriminator,
                "AeroLink", binding.SourceProtectionProvider, new string('d', 64), binding,
                [key], [], connector);
            File.WriteAllText(Path.Combine(package, "manifest.json"),
                JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
            var manifestHash = Sha256File(Path.Combine(package, "manifest.json"));
            var staged = destination.TrimEnd(Path.DirectorySeparatorChar) + $".continuity-stage-{packageId:N}";
            Directory.CreateDirectory(staged);
            var pending = new HomeContinuityPendingReceipt(
                1, packageId, manifestHash, destinationInstallation, destination, destinationConnector,
                destinationServiceSid, "staged", DateTimeOffset.UtcNow, package, staged,
                Array.Empty<HomeContinuityPendingArtifact>(), new string('c', 64));
            var pendingPath = destination + ".continuity-pending.json";
            File.WriteAllText(pendingPath, JsonSerializer.Serialize(pending, new JsonSerializerOptions { WriteIndented = true }));

            var mismatch = Assert.Throws<InvalidOperationException>(() => HomeContinuityKeyTransfer.RecoverPending(
                package, destination, binding, manifestHash, Guid.NewGuid(), destinationInstallation,
                destinationConnector, destinationServiceSid, abort: true));
            Assert.Contains("do not match", mismatch.Message, StringComparison.Ordinal);
            Assert.True(File.Exists(pendingPath));

            var aborted = HomeContinuityKeyTransfer.RecoverPending(
                package, destination, binding, manifestHash, packageId, destinationInstallation,
                destinationConnector, destinationServiceSid, abort: true);
            Assert.Equal("aborted", aborted.Phase);
            Assert.False(File.Exists(pendingPath));
            Assert.True(File.Exists(destination + ".continuity-terminal.json"));
            Assert.False(Directory.Exists(staged));
            Assert.Empty(Directory.EnumerateFileSystemEntries(destination));
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DataProtection:KeyRingPath"] = destination,
                ["SiteServer:InstallationRoot"] = destinationInstallation,
                ["SiteServer:ServiceSid"] = destinationServiceSid,
            }).Build();
            var blocked = Assert.Throws<InvalidOperationException>(() => HomeContinuityKeyTransfer.RequireNoPendingContinuity(configuration));
            Assert.Contains("aborted", blocked.Message, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [WindowsFact]
    public void Certificate_wrapped_ring_preserves_identity_and_legacy_payload_reads_without_plaintext_files()
    {
        var root = Path.Combine(Path.GetTempPath(), $"aerolink-home-continuity-{Guid.NewGuid():N}");
        var package = Path.Combine(root, "package");
        var sourceInstallation = Path.Combine(root, "source-installation");
        var destinationInstallation = Path.Combine(root, "destination-installation");
        var destination = Path.Combine(destinationInstallation, "keys", "data-protection");
        var destinationConnectorDirectory = Path.Combine(destinationInstallation, "keys", "connector");
        var destinationConnector = Path.Combine(destinationConnectorDirectory, "connector-signing-key.pem");
        var sourceConnectorDirectory = Path.Combine(sourceInstallation, "keys", "connector");
        var sourceConnector = Path.Combine(sourceConnectorDirectory, "connector-signing-key.pem");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(sourceConnectorDirectory);
        Directory.CreateDirectory(destinationConnectorDirectory);
        try
        {
            const string sourceApplication = "source-content-root-discriminator";
            const string targetApplication = "AeroLink";
            var repository = new MemoryXmlRepository();
            using var sourceServices = BuildServices(repository, sourceApplication);
            var sourceProvider = sourceServices.GetRequiredService<IDataProtectionProvider>();
            var sourceManager = sourceServices.GetRequiredService<IKeyManager>();
            var createdAt = DateTimeOffset.UtcNow.AddMinutes(-2);
            var revoked = sourceManager.CreateNewKey(createdAt, createdAt.AddDays(30));
            var current = sourceManager.CreateNewKey(createdAt.AddMinutes(1), createdAt.AddDays(31));
            var legacyPayload = sourceProvider.CreateProtector("synthetic", "legacy").Protect("legacy-canary");
            sourceManager.RevokeKey(revoked.KeyId, "continuity test revocation");

            using var transportKey = RSA.Create(2048);
            var request = new CertificateRequest("CN=AeroLink continuity test", transportKey,
                HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
            using var connectorSigningKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var connectorPem = connectorSigningKey.ExportPkcs8PrivateKeyPem();
            File.WriteAllText(sourceConnector, connectorPem);
            var connectorFingerprint = ConnectorLaunchProtocol.PublicKeyFingerprint(
                connectorSigningKey.ExportSubjectPublicKeyInfoPem());
            var binding = new HomeContinuityIdentityBinding(
                "source-installation", sourceApplication, targetApplication, "DpapiCurrentUser",
                new string('a', 64), "connector-deployment", connectorFingerprint[..24], connectorFingerprint, new string('a', 64));
            var connector = new HomeContinuityConnectorMaterial(
                "connector-deployment", connectorFingerprint[..24], connectorFingerprint, connectorPem,
                sourceInstallation, sourceConnector, "S-1-5-80-1234");
            var manifest = HomeContinuityKeyTransfer.ExportPackage(
                sourceManager, repository, certificate, binding, connector, package);
            var manifestHash = Sha256File(Path.Combine(package, "manifest.json"));
            ProtectDirectoryInheritance(destinationConnectorDirectory);
            var destinationServiceSid = System.Security.Principal.WindowsIdentity.GetCurrent().User!.Value;
            Directory.CreateDirectory(destination);
            SetCanonicalRingAcl(destination, destinationServiceSid);

            var applied = HomeContinuityKeyTransfer.ApplyPackage(
                package, destination, certificate, targetApplication, binding, manifestHash,
                destinationInstallation, destinationConnector, destinationServiceSid);

            Assert.Equal(manifest.PackageId, applied.PackageId);
            Assert.Equal(2, applied.Keys.Count);
            Assert.Single(applied.Revocations);
            Assert.Equal(revoked.KeyId, Assert.Single(manifest.Revocations).KeyId);
            Assert.True(File.Exists(destinationConnector));
            var destinationAcl = new DirectoryInfo(destination).GetAccessControl();
            var destinationRules = destinationAcl.GetAccessRules(true, true,
                typeof(System.Security.Principal.SecurityIdentifier)).Cast<FileSystemAccessRule>().ToArray();
            Assert.True(destinationAcl.AreAccessRulesProtected);
            Assert.Equal(3, destinationRules.Length);
            Assert.Contains(destinationRules, rule => rule.IdentityReference.Value == destinationServiceSid
                && rule.AccessControlType == AccessControlType.Allow
                && (rule.FileSystemRights & (FileSystemRights.Modify | FileSystemRights.Synchronize))
                    == (FileSystemRights.Modify | FileSystemRights.Synchronize)
                && !rule.IsInherited);
            Assert.Contains(destinationRules, rule => rule.IdentityReference.Value == "S-1-5-18"
                && rule.FileSystemRights == FileSystemRights.FullControl
                && rule.AccessControlType == AccessControlType.Allow && !rule.IsInherited);
            Assert.Contains(destinationRules, rule => rule.IdentityReference.Value == "S-1-5-32-544"
                && rule.FileSystemRights == FileSystemRights.FullControl
                && rule.AccessControlType == AccessControlType.Allow && !rule.IsInherited);
            using (var restoredConnector = ECDsa.Create())
            {
                restoredConnector.ImportFromPem(File.ReadAllText(destinationConnector));
                Assert.Equal(connectorFingerprint,
                    ConnectorLaunchProtocol.PublicKeyFingerprint(
                        restoredConnector.ExportSubjectPublicKeyInfoPem()));
            }
            Assert.DoesNotContain(File.ReadAllText(Path.Combine(package, manifest.Connector.FileName)),
                "BEGIN PRIVATE KEY", StringComparison.Ordinal);
            Assert.DoesNotContain(Directory.EnumerateFiles(package, "*.xml"),
                path => File.ReadAllText(path).Contains("<masterKey", StringComparison.Ordinal));
            Assert.DoesNotContain(Directory.EnumerateFiles(destination, "*.xml"),
                path => File.ReadAllText(path).Contains("<masterKey", StringComparison.Ordinal));

            using var targetServices = BuildFileServices(destination, targetApplication);
            var targetProvider = targetServices.GetRequiredService<IDataProtectionProvider>();
            var targetKeys = targetServices.GetRequiredService<IKeyManager>().GetAllKeys();
            Assert.Contains(targetKeys, key => key.KeyId == current.KeyId
                && key.CreationDate == current.CreationDate
                && key.ActivationDate == current.ActivationDate
                && key.ExpirationDate == current.ExpirationDate);
            Assert.True(Assert.Single(targetKeys, key => key.KeyId == revoked.KeyId).IsRevoked);

            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DataProtection:KeyRingPath"] = destination,
                ["DataProtection:LegacyApplicationName"] = sourceApplication,
            }).Build();
            using var compatibility = HomeContinuityDataProtection.Create(targetProvider, configuration);
            Assert.Equal("legacy-canary", compatibility.CreateProtector("synthetic", "legacy").Unprotect(legacyPayload));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static ServiceProvider BuildServices(IXmlRepository repository, string applicationName)
    {
        var services = new ServiceCollection();
        services.AddDataProtection()
            .SetApplicationName(applicationName)
            .DisableAutomaticKeyGeneration()
            .AddKeyManagementOptions(options => options.XmlRepository = repository);
        return services.BuildServiceProvider();
    }

    private static ServiceProvider BuildFileServices(string directory, string applicationName)
    {
        var services = new ServiceCollection();
        services.AddDataProtection()
            .SetApplicationName(applicationName)
            .DisableAutomaticKeyGeneration()
            .PersistKeysToFileSystem(new DirectoryInfo(directory));
        return services.BuildServiceProvider();
    }

    private static string Sha256File(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static void ProtectDirectoryInheritance(string path)
    {
        var security = new DirectoryInfo(path).GetAccessControl();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: true);
        new DirectoryInfo(path).SetAccessControl(security);
    }

    private static void SetCanonicalRingAcl(string path, string serviceSid)
    {
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (var grant in new[]
        {
            (Sid: serviceSid, Rights: FileSystemRights.Modify),
            (Sid: "S-1-5-18", Rights: FileSystemRights.FullControl),
            (Sid: "S-1-5-32-544", Rights: FileSystemRights.FullControl),
        })
        {
            security.AddAccessRule(new FileSystemAccessRule(
                new System.Security.Principal.SecurityIdentifier(grant.Sid), grant.Rights,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None, AccessControlType.Allow));
        }
        new DirectoryInfo(path).SetAccessControl(security);
    }

    private sealed class MemoryXmlRepository : IXmlRepository
    {
        private readonly List<XElement> elements = [];
        public IReadOnlyCollection<XElement> GetAllElements() => elements.Select(x => new XElement(x)).ToArray();
        public void StoreElement(XElement element, string friendlyName) => elements.Add(new XElement(element));
    }

    private sealed class WindowsFactAttribute : FactAttribute
    {
        public WindowsFactAttribute()
        {
            if (!OperatingSystem.IsWindows()) Skip = "Requires Windows LocalMachine DPAPI.";
        }
    }
}
