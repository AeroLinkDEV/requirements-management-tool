using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Xml.Linq;
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
    [WindowsFact]
    public void Certificate_wrapped_ring_preserves_identity_and_legacy_payload_reads_without_plaintext_files()
    {
        var root = Path.Combine(Path.GetTempPath(), $"aerolink-home-continuity-{Guid.NewGuid():N}");
        var package = Path.Combine(root, "package");
        var destination = Path.Combine(root, "destination-ring");
        Directory.CreateDirectory(root);
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
            var binding = new HomeContinuityIdentityBinding(
                "source-installation", sourceApplication, targetApplication, "DpapiCurrentUser",
                "verified-backup-hash", "connector-deployment", "connector-key");
            var manifest = HomeContinuityKeyTransfer.ExportPackage(
                sourceManager, repository, certificate, binding, package);
            var manifestHash = Sha256File(Path.Combine(package, "manifest.json"));

            var applied = HomeContinuityKeyTransfer.ApplyPackage(
                package, destination, certificate, targetApplication, binding, manifestHash);

            Assert.Equal(manifest.PackageId, applied.PackageId);
            Assert.Equal(2, applied.Keys.Count);
            Assert.Single(applied.Revocations);
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
