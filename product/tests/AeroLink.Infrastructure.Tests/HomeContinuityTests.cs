using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.AccessControl;
using System.Text.Json;
using System.Xml.Linq;
using AeroLink.ConnectorProtocol;
using AeroLink.Infrastructure.Persistence.Maintenance;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

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
