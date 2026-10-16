using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.AccessControl;
using System.Globalization;
using System.Security.Cryptography.Pkcs;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Microsoft.AspNetCore.DataProtection.XmlEncryption;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace AeroLink.Infrastructure.Persistence.Maintenance;

/// <summary>
/// Transfers a Data Protection ring without ever writing an unencrypted key descriptor.  The source
/// process exports descriptors in memory, wraps each one to a certificate whose private key stays on the
/// destination, and the destination process rewraps the descriptor with LocalMachine DPAPI before the
/// staged ring is committed.  This is deliberately separate from the database payload migration: a ring
/// transfer preserves key identity, while a discriminator change requires purpose-specific re-protection.
/// </summary>
public static class HomeContinuityKeyTransfer
{
    public const string CanonicalTargetApplicationName = "AeroLink";
    public const string CanonicalTargetProtectionProvider = "DpapiLocalMachine";
    /// <summary>
    /// Reads a Windows DPAPI-protected ring by using the decryptor type serialized in each key descriptor.
    /// The scope is deliberately not selected by the reader: <see cref="Microsoft.AspNetCore.DataProtection.XmlEncryption.DpapiXmlDecryptor"/>
    /// delegates to Windows DPAPI, whose protected blob carries the applicable user or machine binding.
    /// </summary>
    public const string SerializedWindowsDpapiProvider = "WindowsDpapi";
    private static readonly XNamespace DataProtectionNamespace =
        XNamespace.Get("http://schemas.asp.net/2015/03/dataProtection");
    private static readonly XNamespace XmlEncryptionNamespace =
        XNamespace.Get("http://www.w3.org/2001/04/xmlenc#");
    private const string DataProtectionAssemblyPrefix = "Microsoft.AspNetCore.DataProtection,";
    private const string EncryptedXmlDecryptorPrefix =
        "Microsoft.AspNetCore.DataProtection.XmlEncryption.EncryptedXmlDecryptor, Microsoft.AspNetCore.DataProtection,";

    /// <summary>
    /// Builds a source provider against the operator-captured ring.  The maintenance host must not use its
    /// already-composed provider here: HOME's implicit provider may be bound to a different per-user ring.
    /// </summary>
    public static ServiceProvider BuildSourceServices(
        string sourceRingDirectory, string sourceApplicationDiscriminator, string sourceProtectionProvider)
    {
        if (string.IsNullOrWhiteSpace(sourceRingDirectory) || !Directory.Exists(sourceRingDirectory))
            throw new DirectoryNotFoundException("The captured source Data Protection ring directory does not exist.");
        RequireNoReparsePoints(sourceRingDirectory, "The captured source Data Protection ring");
        if (string.IsNullOrWhiteSpace(sourceApplicationDiscriminator))
            throw new ArgumentException("The captured source application discriminator is required.", nameof(sourceApplicationDiscriminator));

        var services = new ServiceCollection();
        var builder = services
            .AddDataProtection()
            .SetApplicationName(sourceApplicationDiscriminator)
            .DisableAutomaticKeyGeneration()
            .PersistKeysToFileSystem(new DirectoryInfo(sourceRingDirectory));
        switch (sourceProtectionProvider.Trim())
        {
            case "DpapiCurrentUser":
                if (!OperatingSystem.IsWindows())
                    throw new PlatformNotSupportedException("DpapiCurrentUser source rings require Windows.");
                builder.ProtectKeysWithDpapi();
                break;
            case CanonicalTargetProtectionProvider:
                if (!OperatingSystem.IsWindows())
                    throw new PlatformNotSupportedException("DpapiLocalMachine source rings require Windows.");
                builder.ProtectKeysWithDpapi(protectToLocalMachine: true);
                break;
            case SerializedWindowsDpapiProvider:
                if (!OperatingSystem.IsWindows())
                    throw new PlatformNotSupportedException("Windows DPAPI source rings require Windows.");
                // Do not call ProtectKeysWithDpapi here. The source scope is intentionally unknown. With
                // automatic key generation disabled, the persisted descriptor's serialized DpapiXmlDecryptor
                // is authoritative and Windows DPAPI attempts the protected blob without selecting a new scope
                // or writing a replacement key.
                break;
            case "Unprotected":
                break;
            default:
                throw new InvalidOperationException(
                    $"The captured source protection provider '{sourceProtectionProvider}' is unsupported. " +
                    "Use WindowsDpapi, DpapiCurrentUser, DpapiLocalMachine, or Unprotected.");
        }
        return services.BuildServiceProvider();
    }

    public static void ValidateDestinationConfiguration(IConfiguration configuration, string destinationRingDirectory)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var applicationName = configuration["DataProtection:ApplicationName"]?.Trim();
        if (!string.Equals(applicationName, CanonicalTargetApplicationName, StringComparison.Ordinal))
            throw new InvalidOperationException(
                "DataProtection:ApplicationName must be exactly 'AeroLink' before continuity apply.");
        var configuredRing = configuration["DataProtection:KeyRingPath"]?.Trim();
        if (string.IsNullOrWhiteSpace(configuredRing)
            || !string.Equals(Path.GetFullPath(configuredRing), Path.GetFullPath(destinationRingDirectory), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                "DataProtection:KeyRingPath must explicitly match the destination continuity ring before apply.");
        var protector = configuration["DataProtection:ProtectKeysWith"]?.Trim();
        if (!string.Equals(protector, CanonicalTargetProtectionProvider, StringComparison.Ordinal))
            throw new InvalidOperationException(
                "DataProtection:ProtectKeysWith must be exactly 'DpapiLocalMachine' before continuity apply.");
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("The canonical destination Data Protection boundary requires Windows LocalMachine DPAPI.");
        var destinationParent = Path.GetDirectoryName(Path.GetFullPath(destinationRingDirectory))
            ?? throw new InvalidOperationException("The destination ring has no parent directory.");
        if (!Directory.Exists(destinationParent))
            throw new DirectoryNotFoundException("The destination Data Protection ring parent is not provisioned.");
        RequireNoReparsePoints(destinationParent, "The destination Data Protection ring parent");
        RequireProtectedDirectoryAcl(destinationParent, "The destination Data Protection ring parent");
    }

    /// <summary>
    /// A ring and connector publish cannot be one filesystem transaction.  The pending receipt therefore
    /// remains outside the ring and is a startup/commissioning hold, not merely a second-apply guard.
    /// </summary>
    public static void RequireNoPendingContinuity(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var ring = configuration["DataProtection:KeyRingPath"]?.Trim();
        if (string.IsNullOrWhiteSpace(ring)) return;
        var pending = PendingPath(ring);
        if (File.Exists(pending) || Directory.Exists(pending))
        {
            var receipt = ReadPendingReceipt(pending);
            if (string.Equals(receipt.Phase, "completed", StringComparison.Ordinal))
            {
                ValidateCompletedReceipt(receipt, configuration);
                return;
            }
            throw new InvalidOperationException(
                $"A HOME continuity transfer is pending at '{pending}'. Site commissioning is refused until an attended recovery completes.");
        }

        var terminal = TerminalPath(ring);
        if (File.Exists(terminal) || Directory.Exists(terminal))
        {
            var receipt = ReadPendingReceipt(terminal);
            if (string.Equals(receipt.Phase, "completed", StringComparison.Ordinal))
            {
                ValidateCompletedReceipt(receipt, configuration);
                return;
            }
            throw new InvalidOperationException(
                $"A HOME continuity transfer was aborted at '{terminal}'. Site commissioning remains refused until a new attended continuity transfer completes.");
        }
    }

    public static HomeContinuityPendingReceipt RecoverPending(
        string packageDirectory,
        string destinationRingDirectory,
        HomeContinuityIdentityBinding expectedBinding,
        string expectedManifestSha256,
        Guid expectedPackageId,
        string destinationInstallationRoot,
        string destinationConnectorPath,
        string destinationServiceSid,
        bool abort)
    {
        var pendingPath = PendingPath(destinationRingDirectory);
        var receipt = ReadPendingReceipt(pendingPath);
        _ = ValidateRecoveryReceipt(receipt, packageDirectory, destinationRingDirectory,
            expectedBinding, expectedManifestSha256, expectedPackageId, destinationInstallationRoot,
            destinationConnectorPath, destinationServiceSid);

        if (string.Equals(receipt.Phase, "completed", StringComparison.Ordinal))
            throw new InvalidOperationException("The continuity receipt is already terminal; no recovery action is required.");

        if (abort)
        {
            if (string.Equals(receipt.Phase, "ring-published", StringComparison.Ordinal)
                || Directory.Exists(destinationRingDirectory) && Directory.EnumerateFileSystemEntries(destinationRingDirectory).Any())
            {
                if (!string.Equals(receipt.Phase, "ring-publishing", StringComparison.Ordinal))
                    throw new InvalidOperationException("The destination ring is already published; complete recovery instead of aborting it.");
            }
            ValidateRecoveryArtifactsForAbort(receipt, destinationRingDirectory);
            if (string.Equals(receipt.Phase, "ring-publishing", StringComparison.Ordinal)
                && Directory.Exists(destinationRingDirectory))
            {
                foreach (var artifact in ReadArtifactManifest(destinationRingDirectory))
                    File.Delete(Path.Combine(destinationRingDirectory, artifact.RelativePath));
            }
            if (Directory.Exists(receipt.StagedDirectory)) Directory.Delete(receipt.StagedDirectory, recursive: true);
            if (File.Exists(destinationConnectorPath)) File.Delete(destinationConnectorPath);
            RequireDestinationRingEmpty(destinationRingDirectory, destinationServiceSid);
            var terminal = receipt with { Phase = "aborted", RecordedAt = DateTimeOffset.UtcNow };
            WriteJsonFile(TerminalPath(destinationRingDirectory), terminal);
            File.Delete(pendingPath);
            return terminal;
        }

        if (!string.Equals(receipt.Phase, "artifacts-ready", StringComparison.Ordinal)
            && !string.Equals(receipt.Phase, "connector-published", StringComparison.Ordinal)
            && !string.Equals(receipt.Phase, "ready-to-publish", StringComparison.Ordinal)
            && !string.Equals(receipt.Phase, "ring-publishing", StringComparison.Ordinal)
            && !string.Equals(receipt.Phase, "ring-published", StringComparison.Ordinal))
            throw new InvalidOperationException("The pending receipt has no fully staged connector publish to complete; abort it after reviewing the exact receipt.");
        ValidateRecoveryArtifactsForComplete(receipt, destinationRingDirectory, destinationConnectorPath);
        if (!string.Equals(receipt.Phase, "ring-published", StringComparison.Ordinal))
            PublishStagedArtifacts(receipt.StagedDirectory, destinationRingDirectory, receipt.Artifacts!, receipt.DestinationServiceSid,
                allowPartialExisting: string.Equals(receipt.Phase, "ring-publishing", StringComparison.Ordinal));
        var completed = receipt with
        {
            Phase = "completed",
            StagedDirectory = Path.GetFullPath(destinationRingDirectory),
            Artifacts = ReadArtifactManifest(destinationRingDirectory),
            RecordedAt = DateTimeOffset.UtcNow,
        };
        ReplaceJsonFile(pendingPath, completed);
        return completed;
    }

    public static HomeContinuityPackageManifest ExportPackage(
        IKeyManager sourceKeyManager,
        IXmlRepository sourceRepository,
        X509Certificate2 destinationTransportCertificate,
        HomeContinuityIdentityBinding binding,
        HomeContinuityConnectorMaterial connector,
        string packageDirectory,
        string compatibilityStatus = HomeContinuityCompatibilityReader.CompatibilityProof,
        bool cryptographicCompatibilityProof = true)
    {
        ArgumentNullException.ThrowIfNull(sourceKeyManager);
        ArgumentNullException.ThrowIfNull(sourceRepository);
        ArgumentNullException.ThrowIfNull(destinationTransportCertificate);
        ValidateBinding(binding);
        ValidateConnectorMaterial(connector, binding);
        if (compatibilityStatus is not (HomeContinuityCompatibilityReader.CompatibilityProof
            or HomeContinuityCompatibilityReader.NoApplicableValues
            or HomeContinuityCompatibilityReader.LegacyPlaintextPresent)
            || cryptographicCompatibilityProof != string.Equals(compatibilityStatus, HomeContinuityCompatibilityReader.CompatibilityProof, StringComparison.Ordinal))
            throw new ArgumentException("The continuity compatibility status is unsupported or inconsistently labelled.", nameof(compatibilityStatus));
        RequireDirectoryDoesNotExist(packageDirectory, "The continuity package directory");

        var sourceElements = sourceRepository.GetAllElements().Select(x => new XElement(x)).ToArray();
        if (sourceElements.Length == 0)
            throw new InvalidOperationException("The source Data Protection repository is empty.");

        var keyElements = sourceElements.Where(IsKeyElement).ToArray();
        var revocationElements = sourceElements.Where(IsRevocationElement).ToArray();
        if (keyElements.Length == 0)
            throw new InvalidOperationException("The source Data Protection repository has no keys.");
        if (keyElements.Length + revocationElements.Length != sourceElements.Length)
            throw new InvalidOperationException("The source Data Protection repository contains an unsupported element.");

        var sourceKeys = sourceKeyManager.GetAllKeys().ToDictionary(x => x.KeyId);
        if (sourceKeys.Count != keyElements.Length)
            throw new InvalidOperationException("The source key manager and repository disagree about key count.");
        foreach (var sourceKeyElement in keyElements)
        {
            var keyId = ParseKeyId(sourceKeyElement);
            if (!sourceKeys.TryGetValue(keyId, out var sourceKey))
                throw new InvalidOperationException($"The source key manager did not expose key {keyId:D}.");
            EnsureRepositoryMetadata(sourceKeyElement, sourceKey);
        }
        var repositoryRevokedIds = revocationElements
            .SelectMany(x => x.Elements("key"))
            .Select(ParseKeyId)
            .ToHashSet();
        var managerRevokedIds = sourceKeys.Values.Where(x => x.IsRevoked).Select(x => x.KeyId).ToHashSet();
        if (!repositoryRevokedIds.SetEquals(managerRevokedIds))
            throw new InvalidOperationException("The captured source ring revocations do not match the live source key manager.");

        var stagingDirectory = packageDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + $".continuity-stage-{Guid.NewGuid():N}";
        RequireDirectoryDoesNotExist(stagingDirectory, "The continuity package staging directory");
        Directory.CreateDirectory(stagingDirectory);
        try
        {
            var certificateEncryptor = new CertificateXmlEncryptor(destinationTransportCertificate, NullLoggerFactory.Instance);
            var keys = new List<HomeContinuityKeyManifestEntry>();

            foreach (var sourceKeyElement in keyElements)
            {
                var keyId = ParseKeyId(sourceKeyElement);
                if (!sourceKeys.TryGetValue(keyId, out var sourceKey))
                    throw new InvalidOperationException($"The source key manager did not expose key {keyId:D}.");

                var descriptor = sourceKey.Descriptor.ExportToXml();
                var encrypted = certificateEncryptor.Encrypt(descriptor.SerializedDescriptorElement);
                var packageKey = BuildEncryptedKeyElement(sourceKey, encrypted);
                var relativeName = $"key-{keyId:D}.xml";
                var path = Path.Combine(stagingDirectory, relativeName);
                WriteXmlFile(path, packageKey);
                keys.Add(new HomeContinuityKeyManifestEntry(
                    keyId,
                    sourceKey.CreationDate,
                    sourceKey.ActivationDate,
                    sourceKey.ExpirationDate,
                    relativeName,
                    Sha256File(path)));
            }

            var revocations = new List<HomeContinuityRevocationManifestEntry>();
            for (var index = 0; index < revocationElements.Length; index++)
            {
                var revocation = ParseRevocation(revocationElements[index]);
                var relativeName = $"revocation-{index:D4}.xml";
                var path = Path.Combine(stagingDirectory, relativeName);
                WriteXmlFile(path, revocationElements[index]);
                revocations.Add(new HomeContinuityRevocationManifestEntry(
                    relativeName, Sha256File(path), revocation.KeyId, revocation.RevocationDate));
            }

            var connectorName = "connector-key.cms";
            var connectorPath = Path.Combine(stagingDirectory, connectorName);
            WriteConnectorEnvelope(connectorPath, connector.PrivateKeyPem, destinationTransportCertificate);
            var connectorManifest = new HomeContinuityConnectorManifest(
                connectorName, Sha256File(connectorPath), connector.DeploymentId, connector.KeyId,
                connector.PublicKeyFingerprint, connector.SourceInstallationRoot, connector.SourceConnectorPath,
                connector.SourceServiceSid);

            var manifest = new HomeContinuityPackageManifest(
                SchemaVersion: 2,
                PackageId: Guid.NewGuid(),
                CreatedAt: DateTimeOffset.UtcNow,
                SourceApplicationDiscriminator: binding.SourceApplicationDiscriminator,
                TargetApplicationName: binding.TargetApplicationName,
                SourceProtectionProvider: binding.SourceProtectionProvider,
                SourceRingDigest: DigestElements(sourceElements),
                Binding: binding,
                Keys: keys,
                Revocations: revocations,
                Connector: connectorManifest,
                CompatibilityStatus: compatibilityStatus,
                CryptographicCompatibilityProof: cryptographicCompatibilityProof);

            var manifestPath = Path.Combine(stagingDirectory, "manifest.json");
            WriteJsonFile(manifestPath, manifest);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(packageDirectory))
                ?? throw new InvalidOperationException("The continuity package has no parent directory."));
            if (Directory.Exists(packageDirectory) || File.Exists(packageDirectory))
                throw new InvalidOperationException("The continuity package appeared during preparation; nothing was published.");
            Directory.Move(stagingDirectory, packageDirectory);
            return manifest;
        }
        catch
        {
            if (Directory.Exists(stagingDirectory)) Directory.Delete(stagingDirectory, recursive: true);
            throw;
        }
    }

    /// <summary>
    /// Rewraps a package with LocalMachine DPAPI and atomically installs it into a new, empty destination
    /// ring.  Existing destination contents are always refused; recovery is to choose a new staging path,
    /// never to overwrite or merge an unknown ring.
    /// </summary>
    public static HomeContinuityPackageManifest ApplyPackage(
        string packageDirectory,
        string destinationRingDirectory,
        X509Certificate2 destinationTransportCertificate,
        string expectedTargetApplicationName,
        HomeContinuityIdentityBinding expectedBinding,
        string expectedManifestSha256,
        string destinationInstallationRoot,
        string destinationConnectorPath,
        string destinationServiceSid)
    {
        ArgumentNullException.ThrowIfNull(destinationTransportCertificate);
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("LocalMachine DPAPI ring rewrapping requires Windows.");
        if (string.IsNullOrWhiteSpace(expectedTargetApplicationName))
            throw new ArgumentException("A target application name is required.", nameof(expectedTargetApplicationName));
        ValidateBinding(expectedBinding);
        ValidateDestinationBoundary(destinationInstallationRoot, destinationConnectorPath, destinationServiceSid,
            destinationRingDirectory);
        if (!string.Equals(expectedTargetApplicationName, CanonicalTargetApplicationName, StringComparison.Ordinal))
            throw new InvalidOperationException("Continuity can only install the canonical AeroLink application boundary.");

        var manifestPath = Path.Combine(packageDirectory, "manifest.json");
        RequireNoReparsePoints(packageDirectory, "The continuity package");
        if (!string.Equals(Sha256File(manifestPath), expectedManifestSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The continuity manifest hash does not match the operator-verified handoff; nothing was installed.");
        var manifest = ReadManifest(manifestPath);
        ValidateManifest(manifest, packageDirectory, expectedTargetApplicationName, expectedBinding);
        ValidateConnectorManifest(manifest.Connector, expectedBinding);
        ConsumeValidatedAbortedTerminal(TerminalPath(destinationRingDirectory), destinationRingDirectory,
            destinationInstallationRoot, destinationConnectorPath, destinationServiceSid);
        RequireDestinationRingEmpty(destinationRingDirectory, destinationServiceSid);
        var pendingPath = PendingPath(destinationRingDirectory);
        RequireFileDoesNotExist(pendingPath, "The continuity pending receipt");

        var sourceRepository = new MemoryXmlRepository();
        foreach (var key in manifest.Keys)
        {
            var path = CheckedPackageFile(packageDirectory, key.FileName, key.Sha256);
            var element = ReadXmlFile(path);
            ValidateKeyEnvelope(element, key);
            sourceRepository.StoreElement(element, key.FileName);
        }
        foreach (var revocation in manifest.Revocations)
        {
            var path = CheckedPackageFile(packageDirectory, revocation.FileName, revocation.Sha256);
            var element = ReadXmlFile(path);
            var actual = ParseRevocation(element);
            if (actual.KeyId != revocation.KeyId || actual.RevocationDate != revocation.RevocationDate)
                throw new InvalidOperationException("The continuity revocation metadata changed after export.");
            sourceRepository.StoreElement(element, revocation.FileName);
        }

        var connectorEnvelopePath = CheckedPackageFile(packageDirectory, manifest.Connector.FileName,
            manifest.Connector.Sha256);
        var connectorPem = ReadConnectorEnvelope(connectorEnvelopePath, destinationTransportCertificate,
            manifest.Connector);
        ValidateConnectorPem(connectorPem, manifest.Connector, expectedBinding);

        using var sourceServices = BuildCertificateUnwrapServices(
            sourceRepository, manifest.SourceApplicationDiscriminator, destinationTransportCertificate);
        var sourceManager = sourceServices.GetRequiredService<IKeyManager>();
        var sourceKeys = sourceManager.GetAllKeys().ToDictionary(x => x.KeyId);
        if (sourceKeys.Count != manifest.Keys.Count)
            throw new InvalidOperationException("The portable package did not load the expected key count.");
        var sourceRevokedIds = sourceKeys.Values.Where(x => x.IsRevoked).Select(x => x.KeyId).ToHashSet();
        var manifestRevokedIds = manifest.Revocations.Select(x => x.KeyId).ToHashSet();
        if (!sourceRevokedIds.SetEquals(manifestRevokedIds))
            throw new InvalidOperationException("The portable package revocation set does not match its certificate-unwrapped source manager.");

        var stagedDirectory = destinationRingDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + $".continuity-stage-{manifest.PackageId:N}";
        RequireDirectoryDoesNotExist(stagedDirectory, "The destination staging directory");
        var pending = new HomeContinuityPendingReceipt(
            1, manifest.PackageId, expectedManifestSha256, destinationInstallationRoot,
            Path.GetFullPath(destinationRingDirectory), Path.GetFullPath(destinationConnectorPath),
            destinationServiceSid, "staged", DateTimeOffset.UtcNow,
            Path.GetFullPath(packageDirectory), Path.GetFullPath(stagedDirectory),
            Array.Empty<HomeContinuityPendingArtifact>(), Sha256Utf8(connectorPem));
        try
        {
            // The receipt is outside both the staging directory and the final ring.  It is written before any
            // artifact so every crash leaves a durable, identity-bound recovery boundary.
            WriteJsonFile(pendingPath, pending);
            Directory.CreateDirectory(stagedDirectory);
            var dpapiEncryptor = new DpapiXmlEncryptor(protectToLocalMachine: true, NullLoggerFactory.Instance);
            foreach (var keyEntry in manifest.Keys)
            {
                if (!sourceKeys.TryGetValue(keyEntry.KeyId, out var sourceKey))
                    throw new InvalidOperationException($"The portable package did not load key {keyEntry.KeyId:D}.");
                EnsureMetadata(sourceKey, keyEntry);
                var descriptor = sourceKey.Descriptor.ExportToXml();
                var encrypted = dpapiEncryptor.Encrypt(descriptor.SerializedDescriptorElement);
                var targetKey = BuildEncryptedKeyElement(sourceKey, encrypted);
                WriteXmlFile(Path.Combine(stagedDirectory, keyEntry.FileName), targetKey);
            }
            foreach (var revocation in manifest.Revocations)
            {
                var element = ReadXmlFile(CheckedPackageFile(packageDirectory, revocation.FileName, revocation.Sha256));
                WriteXmlFile(Path.Combine(stagedDirectory, revocation.FileName), element);
            }

            ValidateRewrappedRing(stagedDirectory, expectedTargetApplicationName, manifest);
            pending = pending with
            {
                Phase = "artifacts-ready",
                Artifacts = ReadArtifactManifest(stagedDirectory),
                RecordedAt = DateTimeOffset.UtcNow,
            };
            ReplaceJsonFile(pendingPath, pending);

            WriteConnectorFile(destinationConnectorPath, connectorPem);
            pending = pending with { Phase = "connector-published", RecordedAt = DateTimeOffset.UtcNow };
            ReplaceJsonFile(pendingPath, pending);

            WriteJsonFile(Path.Combine(stagedDirectory, "continuity-receipt.json"), new HomeContinuityApplyReceipt(
                2, manifest.PackageId, expectedManifestSha256, manifest.SourceRingDigest,
                expectedTargetApplicationName, CanonicalTargetProtectionProvider, manifest.Keys.Count,
                manifest.Revocations.Count, manifest.Connector.KeyId, manifest.Connector.PublicKeyFingerprint,
                manifest.Connector.DeploymentId, Path.GetFullPath(destinationConnectorPath), DateTimeOffset.UtcNow));
            pending = pending with
            {
                Phase = "ready-to-publish",
                Artifacts = ReadArtifactManifest(stagedDirectory),
                RecordedAt = DateTimeOffset.UtcNow,
            };
            ReplaceJsonFile(pendingPath, pending);

            Directory.CreateDirectory(Path.GetDirectoryName(destinationRingDirectory)
                ?? throw new InvalidOperationException("The destination ring has no parent directory."));
            pending = pending with { Phase = "ring-publishing", RecordedAt = DateTimeOffset.UtcNow };
            ReplaceJsonFile(pendingPath, pending);
            PublishStagedArtifacts(stagedDirectory, destinationRingDirectory, pending.Artifacts!, destinationServiceSid,
                allowPartialExisting: false);
            pending = pending with
            {
                Phase = "ring-published",
                StagedDirectory = Path.GetFullPath(destinationRingDirectory),
                Artifacts = ReadArtifactManifest(destinationRingDirectory),
                RecordedAt = DateTimeOffset.UtcNow,
            };
            ReplaceJsonFile(pendingPath, pending);
            pending = pending with { Phase = "completed", RecordedAt = DateTimeOffset.UtcNow };
            ReplaceJsonFile(pendingPath, pending);
        }
        catch
        {
            // Leave the exact staging boundary and receipt for the attended recovery command.  Deleting on the
            // catch path cannot authenticate a crash window and would destroy the only evidence of what was
            // published; recovery is deliberately receipt-bound below.
            throw;
        }

        return manifest;
    }

    private static ServiceProvider BuildCertificateUnwrapServices(
        IXmlRepository repository, string applicationName, X509Certificate2 certificate)
    {
        var services = new ServiceCollection();
        services.AddDataProtection()
            .SetApplicationName(applicationName)
            .DisableAutomaticKeyGeneration()
            .AddKeyManagementOptions(options => options.XmlRepository = repository)
            .UnprotectKeysWithAnyCertificate(certificate);
        return services.BuildServiceProvider();
    }

    private static void ValidateRewrappedRing(
        string stagedDirectory, string applicationName, HomeContinuityPackageManifest manifest)
    {
        var serviceCollection = new ServiceCollection();
        serviceCollection.AddDataProtection()
            .SetApplicationName(applicationName)
            .DisableAutomaticKeyGeneration()
            .PersistKeysToFileSystem(new DirectoryInfo(stagedDirectory));
        using var services = serviceCollection.BuildServiceProvider();
        var manager = services.GetRequiredService<IKeyManager>();
        var keys = manager.GetAllKeys().ToDictionary(x => x.KeyId);
        if (keys.Count != manifest.Keys.Count)
            throw new InvalidOperationException("The rewrapped ring did not load the expected key count.");
        foreach (var entry in manifest.Keys)
        {
            if (!keys.TryGetValue(entry.KeyId, out var key))
                throw new InvalidOperationException($"The rewrapped ring did not load key {entry.KeyId:D}.");
            EnsureMetadata(key, entry);
        }
        var revokedIds = keys.Values.Where(x => x.IsRevoked).Select(x => x.KeyId).ToHashSet();
        var expectedRevokedIds = manifest.Revocations.Select(x => x.KeyId).ToHashSet();
        if (!revokedIds.SetEquals(expectedRevokedIds))
            throw new InvalidOperationException("The rewrapped ring does not preserve the exact revocation set.");
    }

    private static XElement BuildEncryptedKeyElement(IKey sourceKey, EncryptedXmlInfo encrypted)
    {
        return new XElement("key",
            new XAttribute("id", sourceKey.KeyId),
            new XAttribute("version", 1),
            new XElement("creationDate", sourceKey.CreationDate),
            new XElement("activationDate", sourceKey.ActivationDate),
            new XElement("expirationDate", sourceKey.ExpirationDate),
            new XElement("descriptor",
                new XAttribute("deserializerType", sourceKey.Descriptor.ExportToXml().DeserializerType.AssemblyQualifiedName
                    ?? throw new InvalidOperationException("The key descriptor has no deserializer type.")),
                new XElement(DataProtectionNamespace + "encryptedSecret",
                    new XAttribute("decryptorType", encrypted.DecryptorType.AssemblyQualifiedName
                        ?? throw new InvalidOperationException("The key envelope has no decryptor type.")),
                    encrypted.EncryptedElement)));
    }

    private static void ValidateManifest(HomeContinuityPackageManifest manifest, string packageDirectory,
        string targetApplicationName, HomeContinuityIdentityBinding expectedBinding)
    {
        if (manifest.SchemaVersion != 2 || manifest.PackageId == Guid.Empty || manifest.Keys.Count == 0
            || manifest.Connector is null)
            throw new InvalidOperationException("The HOME continuity package manifest is unsupported or incomplete.");
        if (manifest.CompatibilityStatus is not (HomeContinuityCompatibilityReader.CompatibilityProof
            or HomeContinuityCompatibilityReader.NoApplicableValues
            or HomeContinuityCompatibilityReader.LegacyPlaintextPresent)
            || manifest.CryptographicCompatibilityProof != string.Equals(manifest.CompatibilityStatus, HomeContinuityCompatibilityReader.CompatibilityProof, StringComparison.Ordinal))
            throw new InvalidOperationException("The HOME continuity package compatibility status is unsupported or inconsistently labelled.");
        if (!IsSha256(manifest.SourceRingDigest))
            throw new InvalidOperationException("The HOME continuity package has no valid source ring digest.");
        if (!string.Equals(manifest.TargetApplicationName, targetApplicationName, StringComparison.Ordinal))
            throw new InvalidOperationException("The continuity package target application name does not match this installation.");
        if (!string.Equals(manifest.SourceApplicationDiscriminator, manifest.Binding.SourceApplicationDiscriminator, StringComparison.Ordinal)
            || !string.Equals(manifest.SourceProtectionProvider, manifest.Binding.SourceProtectionProvider, StringComparison.Ordinal)
            || !string.Equals(manifest.TargetApplicationName, manifest.Binding.TargetApplicationName, StringComparison.Ordinal))
            throw new InvalidOperationException("The continuity package repeats inconsistent source or target identity metadata.");
        if (!string.Equals(manifest.Binding.SourceInstallationId, expectedBinding.SourceInstallationId, StringComparison.Ordinal)
            || !string.Equals(manifest.Binding.SourceApplicationDiscriminator, expectedBinding.SourceApplicationDiscriminator, StringComparison.Ordinal)
            || !string.Equals(manifest.Binding.SourceProtectionProvider, expectedBinding.SourceProtectionProvider, StringComparison.Ordinal)
            || !string.Equals(manifest.Binding.VerifiedBackupId, expectedBinding.VerifiedBackupId, StringComparison.Ordinal)
            || !string.Equals(manifest.Binding.ConnectorDeploymentId, expectedBinding.ConnectorDeploymentId, StringComparison.Ordinal)
            || !string.Equals(manifest.Binding.ConnectorKeyId, expectedBinding.ConnectorKeyId, StringComparison.Ordinal)
            || !string.Equals(manifest.Binding.ConnectorPublicKeyFingerprint, expectedBinding.ConnectorPublicKeyFingerprint, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(manifest.Binding.VerifiedBackupArchiveSha256, expectedBinding.VerifiedBackupArchiveSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The continuity package is bound to a different source installation, backup, or connector identity.");

        var allNames = manifest.Keys.Select(x => x.FileName)
            .Concat(manifest.Revocations.Select(x => x.FileName))
            .Append(manifest.Connector.FileName).ToArray();
        if (allNames.Any(x => Path.IsPathRooted(x) || x.Contains("..", StringComparison.Ordinal) || Path.GetFileName(x) != x)
            || allNames.Distinct(StringComparer.OrdinalIgnoreCase).Count() != allNames.Length)
            throw new InvalidOperationException("The continuity package contains an unsafe or duplicate file name.");
        if (manifest.Keys.Any(x => x.KeyId == Guid.Empty)
            || manifest.Keys.Select(x => x.KeyId).Distinct().Count() != manifest.Keys.Count)
            throw new InvalidOperationException("The continuity package contains an empty or duplicate key identity.");
        if (manifest.Revocations.Any(x => x.KeyId == Guid.Empty)
            || manifest.Revocations.Select(x => x.KeyId).Distinct().Count() != manifest.Revocations.Count
            || manifest.Revocations.Any(x => !manifest.Keys.Any(key => key.KeyId == x.KeyId)))
            throw new InvalidOperationException("The continuity package contains an invalid or duplicate revocation identity.");
        foreach (var key in manifest.Keys) _ = CheckedPackageFile(packageDirectory, key.FileName, key.Sha256);
        foreach (var revocation in manifest.Revocations) _ = CheckedPackageFile(packageDirectory, revocation.FileName, revocation.Sha256);
        _ = CheckedPackageFile(packageDirectory, manifest.Connector.FileName, manifest.Connector.Sha256);
    }

    private static void ValidateConnectorManifest(HomeContinuityConnectorManifest connector,
        HomeContinuityIdentityBinding expectedBinding)
    {
        if (string.IsNullOrWhiteSpace(connector.FileName) || !IsSha256(connector.Sha256)
            || string.IsNullOrWhiteSpace(connector.DeploymentId) || string.IsNullOrWhiteSpace(connector.KeyId)
            || !IsSha256(connector.PublicKeyFingerprint)
            || string.IsNullOrWhiteSpace(connector.SourceInstallationRoot)
            || string.IsNullOrWhiteSpace(connector.SourceConnectorPath)
            || string.IsNullOrWhiteSpace(connector.SourceServiceSid))
            throw new InvalidOperationException("The HOME continuity connector manifest is incomplete.");
        if (!string.Equals(connector.DeploymentId, expectedBinding.ConnectorDeploymentId, StringComparison.Ordinal)
            || !string.Equals(connector.KeyId, expectedBinding.ConnectorKeyId, StringComparison.Ordinal)
            || !string.Equals(connector.PublicKeyFingerprint, expectedBinding.ConnectorPublicKeyFingerprint, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The continuity connector identity does not match the operator-verified source identity.");
        if (Path.IsPathRooted(connector.FileName) || connector.FileName.Contains("..", StringComparison.Ordinal)
            || Path.GetFileName(connector.FileName) != connector.FileName)
            throw new InvalidOperationException("The continuity connector envelope has an unsafe file name.");
    }

    private static void ValidateConnectorMaterial(HomeContinuityConnectorMaterial connector,
        HomeContinuityIdentityBinding binding)
    {
        if (string.IsNullOrWhiteSpace(connector.DeploymentId)
            || !string.Equals(connector.DeploymentId, binding.ConnectorDeploymentId, StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(connector.KeyId)
            || !string.Equals(connector.KeyId, binding.ConnectorKeyId, StringComparison.Ordinal)
            || !IsSha256(connector.PublicKeyFingerprint)
            || !string.Equals(connector.PublicKeyFingerprint, binding.ConnectorPublicKeyFingerprint, StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(connector.PrivateKeyPem)
            || string.IsNullOrWhiteSpace(connector.SourceInstallationRoot)
            || string.IsNullOrWhiteSpace(connector.SourceConnectorPath)
            || string.IsNullOrWhiteSpace(connector.SourceServiceSid))
            throw new ArgumentException("The source connector transfer material is incomplete.", nameof(connector));
        var root = RequireExistingAbsoluteDirectory(connector.SourceInstallationRoot, "The source connector installation root");
        var keyPath = RequireExistingFile(connector.SourceConnectorPath, "The source connector signing key");
        RequireUnderRoot(root, keyPath, "The source connector signing key");
        RequireNoReparsePoints(root, "The source connector installation root");
        RequireNoReparsePoints(keyPath, "The source connector signing key");
        ValidateServiceSid(connector.SourceServiceSid, "The source connector service SID");
        using var key = ECDsa.Create();
        try { key.ImportFromPem(connector.PrivateKeyPem); }
        catch (Exception ex) when (ex is CryptographicException or ArgumentException)
        { throw new InvalidOperationException("The source connector signing key is not a valid ECDSA PEM.", ex); }
        var fingerprint = ConnectorFingerprint(key);
        if (!string.Equals(fingerprint, connector.PublicKeyFingerprint, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(fingerprint[..24], connector.KeyId, StringComparison.Ordinal))
            throw new InvalidOperationException("The source connector signing key does not match its verified public identity.");
    }

    private static void ValidateDestinationBoundary(string installationRoot, string connectorPath,
        string serviceSid, string destinationRing)
    {
        var root = RequireExistingAbsoluteDirectory(installationRoot, "The destination installation root");
        var ring = Path.GetFullPath(destinationRing);
        RequireUnderRoot(root, ring, "The destination Data Protection ring");
        var connector = Path.GetFullPath(connectorPath);
        RequireUnderRoot(root, connector, "The destination connector signing key");
        if (File.Exists(connector) || Directory.Exists(connector))
            throw new InvalidOperationException("The destination connector signing key already exists; continuity refuses replacement.");
        var parent = Path.GetDirectoryName(connector) ?? throw new InvalidOperationException("The destination connector path has no parent.");
        if (!Directory.Exists(parent)) throw new DirectoryNotFoundException("The destination connector directory is not provisioned.");
        RequireNoReparsePoints(root, "The destination installation root");
        RequireNoReparsePoints(parent, "The destination connector directory");
        ValidateServiceSid(serviceSid, "The destination connector service SID");
        RequireConnectorDirectoryAcl(parent, serviceSid);
    }

    private static string ReadConnectorEnvelope(string path, X509Certificate2 certificate,
        HomeContinuityConnectorManifest connector)
    {
        var bytes = File.ReadAllBytes(path);
        var cms = new EnvelopedCms();
        try
        {
            cms.Decode(bytes);
            cms.Decrypt(new X509Certificate2Collection(certificate));
            var pem = Encoding.UTF8.GetString(cms.ContentInfo.Content);
            if (string.IsNullOrWhiteSpace(pem)) throw new InvalidOperationException("The connector envelope was empty.");
            return pem;
        }
        catch (CryptographicException ex)
        {
            throw new InvalidOperationException("The connector envelope could not be decrypted by the destination certificate.", ex);
        }
    }

    private static void WriteConnectorEnvelope(string path, string privateKeyPem, X509Certificate2 certificate)
    {
        if (certificate.GetRSAPublicKey() is null)
            throw new InvalidOperationException("The continuity transport certificate must contain an RSA public key for connector transfer.");
        var content = new ContentInfo(Encoding.UTF8.GetBytes(privateKeyPem));
        var cms = new EnvelopedCms(content);
        cms.Encrypt(new CmsRecipient(SubjectIdentifierType.IssuerAndSerialNumber, certificate));
        WriteBytesFile(path, cms.Encode());
    }

    private static void ValidateConnectorPem(string pem, HomeContinuityConnectorManifest connector,
        HomeContinuityIdentityBinding expectedBinding)
    {
        using var key = ECDsa.Create();
        try { key.ImportFromPem(pem); }
        catch (Exception ex) when (ex is CryptographicException or ArgumentException)
        { throw new InvalidOperationException("The connector envelope did not contain a valid ECDSA PEM.", ex); }
        var fingerprint = ConnectorFingerprint(key);
        if (!string.Equals(fingerprint, connector.PublicKeyFingerprint, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(fingerprint, expectedBinding.ConnectorPublicKeyFingerprint, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(fingerprint[..24], connector.KeyId, StringComparison.Ordinal)
            || !string.Equals(connector.DeploymentId, expectedBinding.ConnectorDeploymentId, StringComparison.Ordinal))
            throw new InvalidOperationException("The connector envelope's key or deployment identity did not verify.");
    }

    private static string ConnectorFingerprint(ECDsa key) =>
        Convert.ToHexString(SHA256.HashData(key.ExportSubjectPublicKeyInfo())).ToLowerInvariant();

    private static void WriteConnectorFile(string path, string pem)
    {
        if (File.Exists(path) || Directory.Exists(path))
            throw new InvalidOperationException("The destination connector signing key appeared during preparation; nothing was installed.");
        var parent = Path.GetDirectoryName(path) ?? throw new InvalidOperationException("The destination connector path has no parent.");
        Directory.CreateDirectory(parent);
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough);
        using var writer = new StreamWriter(stream, new UTF8Encoding(false), 4096, leaveOpen: true);
        writer.Write(pem);
        writer.Flush();
        stream.Flush(flushToDisk: true);
    }

    private static string RequireExistingAbsoluteDirectory(string path, string description)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path) || !Directory.Exists(path))
            throw new InvalidOperationException($"{description} must be an existing absolute directory.");
        return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static string RequireExistingFile(string path, string description)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path) || !File.Exists(path))
            throw new InvalidOperationException($"{description} must be an existing absolute file.");
        return Path.GetFullPath(path);
    }

    private static void RequireUnderRoot(string root, string path, string description)
    {
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        var fullPath = Path.GetFullPath(path);
        if (!fullPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"{description} must be under the declared installation root.");
    }

    private static void ValidateServiceSid(string sid, string description)
    {
        if (string.IsNullOrWhiteSpace(sid) || !sid.StartsWith("S-", StringComparison.OrdinalIgnoreCase)
            || sid.Count(c => c == '-') < 2)
            throw new InvalidOperationException($"{description} is missing or malformed.");
    }

    private static void RequireConnectorDirectoryAcl(string path, string serviceSid)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Continuity connector ACL validation requires Windows.");
        var security = new DirectoryInfo(path).GetAccessControl();
        if (!security.AreAccessRulesProtected)
            throw new InvalidOperationException("The destination connector directory inherits permissions; continuity refuses an unprotected key boundary.");
        var expected = new SecurityIdentifier(serviceSid);
        var hasModify = security.GetAccessRules(true, true, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>()
            .Any(rule => rule.AccessControlType == AccessControlType.Allow
                && rule.IdentityReference is SecurityIdentifier identity
                && identity == expected
                && (rule.FileSystemRights & FileSystemRights.Modify) == FileSystemRights.Modify);
        if (!hasModify)
            throw new InvalidOperationException("The destination connector directory does not grant Modify to the declared API service SID.");
    }

    private static void EnsureMetadata(IKey key, HomeContinuityKeyManifestEntry entry)
    {
        if (key.KeyId != entry.KeyId || key.CreationDate != entry.CreationDate
            || key.ActivationDate != entry.ActivationDate || key.ExpirationDate != entry.ExpirationDate)
            throw new InvalidOperationException($"Key metadata changed during HOME continuity transfer for {entry.KeyId:D}.");
    }

    private static void EnsureRepositoryMetadata(XElement element, IKey key)
    {
        if (!DateTimeOffset.TryParse(element.Element("creationDate")?.Value, CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind, out var creation)
            || !DateTimeOffset.TryParse(element.Element("activationDate")?.Value, CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind, out var activation)
            || !DateTimeOffset.TryParse(element.Element("expirationDate")?.Value, CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind, out var expiration)
            || creation != key.CreationDate || activation != key.ActivationDate || expiration != key.ExpirationDate)
            throw new InvalidOperationException($"The captured source ring metadata does not match key {key.KeyId:D}.");
    }

    private static Guid ParseKeyId(XElement element)
    {
        if (!Guid.TryParse((string?)element.Attribute("id"), out var keyId) || keyId == Guid.Empty)
            throw new InvalidOperationException("The source Data Protection repository contains a key with an invalid ID.");
        return keyId;
    }

    private static RevocationMetadata ParseRevocation(XElement element)
    {
        if (!IsRevocationElement(element) || element.Attributes().Count() != 1
            || element.Attribute("version") is null || element.Attribute("version")!.Value != "1"
            || element.Elements().Any(x => x.Name.LocalName is not ("revocationDate" or "reason" or "key"))
            || element.Element("revocationDate") is null || element.Element("key") is null
            || element.Elements("key").Count() != 1
            || element.Elements("revocationDate").Count() != 1
            || element.Elements("reason").Count() > 1
            || !DateTimeOffset.TryParse(element.Element("revocationDate")!.Value,
                CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var revocationDate))
            throw new InvalidOperationException("The source Data Protection repository contains a malformed revocation.");
        return new RevocationMetadata(ParseKeyId(element.Element("key")!), revocationDate);
    }

    private static void ValidateKeyEnvelope(XElement element, HomeContinuityKeyManifestEntry entry)
    {
        if (!IsKeyElement(element)
            || element.Attributes().Count() != 2
            || element.Attribute("id")?.Value != entry.KeyId.ToString()
            || element.Attribute("version")?.Value != "1"
            || element.Elements().Count() != 4
            || element.Element("creationDate") is null
            || element.Element("activationDate") is null
            || element.Element("expirationDate") is null)
            throw new InvalidOperationException($"The continuity package key envelope for {entry.KeyId:D} is malformed.");

        var descriptor = element.Element("descriptor");
        var encryptedSecret = descriptor?.Element(DataProtectionNamespace + "encryptedSecret");
        var encryptedData = encryptedSecret?.Elements().SingleOrDefault();
        var deserializerType = descriptor?.Attribute("deserializerType")?.Value;
        var decryptorType = encryptedSecret?.Attribute("decryptorType")?.Value;
        if (descriptor is null
            || descriptor.Attributes().Count() != 1
            || string.IsNullOrWhiteSpace(deserializerType)
            || !deserializerType.StartsWith("Microsoft.AspNetCore.DataProtection.", StringComparison.Ordinal)
            || !deserializerType.Contains(DataProtectionAssemblyPrefix, StringComparison.Ordinal)
            || descriptor.Elements().Count() != 1
            || encryptedSecret is null
            || encryptedSecret.Attributes().Count(x => !x.IsNamespaceDeclaration) != 1
            || string.IsNullOrWhiteSpace(decryptorType)
            || !decryptorType.StartsWith(EncryptedXmlDecryptorPrefix, StringComparison.Ordinal)
            || encryptedData is null
            || encryptedData.Name != XmlEncryptionNamespace + "EncryptedData"
            || encryptedSecret.Elements().Count() != 1)
            throw new InvalidOperationException($"The continuity package key envelope for {entry.KeyId:D} is missing its expected encrypted descriptor.");
    }

    private static bool IsKeyElement(XElement element) => element.Name.LocalName == "key";
    private static bool IsRevocationElement(XElement element) => element.Name.LocalName == "revocation";

    private static HomeContinuityPackageManifest ReadManifest(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("The HOME continuity package manifest is missing.", path);
        return JsonSerializer.Deserialize<HomeContinuityPackageManifest>(File.ReadAllText(path))
            ?? throw new InvalidOperationException("The HOME continuity package manifest is empty.");
    }

    private static string CheckedPackageFile(string packageDirectory, string relativeName, string expectedHash)
    {
        if (string.IsNullOrWhiteSpace(relativeName) || Path.IsPathRooted(relativeName)
            || relativeName.Contains("..", StringComparison.Ordinal) || Path.GetFileName(relativeName) != relativeName)
            throw new InvalidOperationException("The continuity package contains an unsafe file name.");
        var path = Path.Combine(packageDirectory, relativeName);
        if (!File.Exists(path) || !string.Equals(Sha256File(path), expectedHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"The continuity package file '{relativeName}' is missing or changed.");
        return path;
    }

    private static void RequireDirectoryDoesNotExist(string path, string description)
    {
        if (Directory.Exists(path) || File.Exists(path))
            throw new InvalidOperationException($"{description} already exists; continuity refuses to overwrite it.");
    }

    private static void RequireFileDoesNotExist(string path, string description)
    {
        if (File.Exists(path) || Directory.Exists(path))
            throw new InvalidOperationException($"{description} already exists; continuity refuses to resume or overwrite it.");
    }

    private static string PendingPath(string destinationRingDirectory) =>
        Path.GetFullPath(destinationRingDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
        + ".continuity-pending.json";

    private static string TerminalPath(string destinationRingDirectory) =>
        Path.GetFullPath(destinationRingDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
        + ".continuity-terminal.json";

    private static HomeContinuityPendingReceipt ReadPendingReceipt(string path)
    {
        if (!File.Exists(path) || Directory.Exists(path))
            throw new InvalidOperationException($"The continuity receipt '{path}' is missing or is not a file.");
        try
        {
            var receipt = JsonSerializer.Deserialize<HomeContinuityPendingReceipt>(File.ReadAllText(path));
            if (receipt is null || receipt.SchemaVersion != 1 || receipt.PackageId == Guid.Empty
                || string.IsNullOrWhiteSpace(receipt.ManifestSha256)
                || string.IsNullOrWhiteSpace(receipt.DestinationRingPath)
                || string.IsNullOrWhiteSpace(receipt.DestinationInstallationRoot)
                || string.IsNullOrWhiteSpace(receipt.DestinationConnectorPath)
                || string.IsNullOrWhiteSpace(receipt.DestinationServiceSid)
                || string.IsNullOrWhiteSpace(receipt.Phase))
                throw new InvalidOperationException("The continuity receipt is incomplete.");
            return receipt;
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("The continuity receipt is malformed; no recovery action was taken.", ex);
        }
    }

    private static HomeContinuityPackageManifest ValidateRecoveryReceipt(
        HomeContinuityPendingReceipt receipt,
        string packageDirectory,
        string destinationRingDirectory,
        HomeContinuityIdentityBinding expectedBinding,
        string expectedManifestSha256,
        Guid expectedPackageId,
        string destinationInstallationRoot,
        string destinationConnectorPath,
        string destinationServiceSid)
    {
        var fullPackage = Path.GetFullPath(packageDirectory);
        var fullRing = Path.GetFullPath(destinationRingDirectory);
        var fullRoot = Path.GetFullPath(destinationInstallationRoot);
        var fullConnector = Path.GetFullPath(destinationConnectorPath);
        if (!string.Equals(receipt.PackageDirectory, fullPackage, StringComparison.OrdinalIgnoreCase)
            || receipt.PackageId != expectedPackageId
            || !string.Equals(receipt.ManifestSha256, expectedManifestSha256, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(receipt.DestinationRingPath, fullRing, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(receipt.DestinationInstallationRoot, fullRoot, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(receipt.DestinationConnectorPath, fullConnector, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(receipt.DestinationServiceSid, destinationServiceSid, StringComparison.Ordinal))
            throw new InvalidOperationException("The recovery arguments do not match the pending continuity receipt; nothing was changed.");
        var expectedStagedDirectory = fullRing.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + $".continuity-stage-{expectedPackageId:N}";
        if (!string.Equals(receipt.Phase, "ring-published", StringComparison.Ordinal)
            && !string.Equals(receipt.StagedDirectory, expectedStagedDirectory, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The pending receipt does not bind the canonical owned staging directory.");
        if (string.Equals(receipt.Phase, "ring-published", StringComparison.Ordinal)
            && !string.Equals(receipt.StagedDirectory, fullRing, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The pending receipt does not bind the canonical owned staging directory.");
        if (receipt.Phase is not ("staged" or "artifacts-ready" or "connector-published" or "ready-to-publish" or "ring-publishing" or "ring-published"))
            throw new InvalidOperationException($"The pending receipt phase '{receipt.Phase}' is not recoverable.");
        ValidateBinding(expectedBinding);
        if (!IsSha256(receipt.ManifestSha256) || !IsSha256(receipt.ConnectorSha256))
            throw new InvalidOperationException("The pending receipt has no valid manifest or connector artifact hash.");
        RequireNoReparsePoints(fullRoot, "The destination installation root");
        RequireUnderRoot(fullRoot, fullRing, "The destination Data Protection ring");
        RequireUnderRoot(fullRoot, fullConnector, "The destination connector path");
        RequireNoReparsePoints(Path.GetDirectoryName(fullRing)
            ?? throw new InvalidOperationException("The destination ring has no parent directory."),
            "The destination Data Protection ring parent");

        RequireNoReparsePoints(fullPackage, "The continuity package");
        var manifestPath = Path.Combine(fullPackage, "manifest.json");
        if (!string.Equals(Sha256File(manifestPath), expectedManifestSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The recovery package manifest changed after the pending receipt was recorded; nothing was changed.");
        var manifest = ReadManifest(manifestPath);
        if (manifest.PackageId != expectedPackageId)
            throw new InvalidOperationException("The recovery package ID does not match the pending receipt.");
        ValidateManifest(manifest, fullPackage, CanonicalTargetApplicationName, expectedBinding);
        ValidateConnectorManifest(manifest.Connector, expectedBinding);
        ValidateReceiptArtifactNames(receipt.Artifacts, manifest);
        return manifest;
    }

    private static void ValidateRecoveryArtifactsForAbort(HomeContinuityPendingReceipt receipt,
        string destinationRingDirectory)
    {
        if (receipt.Artifacts is null)
            throw new InvalidOperationException("The pending receipt has no authenticated artifact hashes; refusing arbitrary cleanup.");
        var ringArtifacts = Directory.Exists(destinationRingDirectory)
            ? ReadArtifactManifest(destinationRingDirectory)
            : Array.Empty<HomeContinuityPendingArtifact>();
        if (ringArtifacts.Count != 0 && !string.Equals(receipt.Phase, "ring-publishing", StringComparison.Ordinal))
            throw new InvalidOperationException("The destination ring contains unexpected artifacts; refusing cleanup.");
        if (!ArtifactsSubset(ringArtifacts, receipt.Artifacts))
            throw new InvalidOperationException("The destination ring contains artifacts that are not authenticated by the receipt; refusing cleanup.");
        if (Directory.Exists(receipt.StagedDirectory))
        {
            var actual = ReadArtifactManifest(receipt.StagedDirectory);
            if (!ArtifactsSubset(actual, receipt.Artifacts)
                || !ArtifactsDisjoint(actual, ringArtifacts)
                || actual.Count + ringArtifacts.Count != receipt.Artifacts.Count)
                throw new InvalidOperationException("The staged continuity artifacts do not match the receipt; refusing cleanup.");
        }
        else if (receipt.Phase != "ring-publishing" || !ArtifactsEqual(ringArtifacts, receipt.Artifacts))
            throw new InvalidOperationException("The staged continuity artifacts disappeared after the receipt was recorded; refusing cleanup.");
        if (File.Exists(receipt.DestinationConnectorPath))
        {
            if (!string.Equals(Sha256File(receipt.DestinationConnectorPath), receipt.ConnectorSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The destination connector artifact does not match the receipt; refusing cleanup.");
        }
        else if (receipt.Phase is "connector-published" or "ready-to-publish" or "ring-publishing" or "ring-published")
            throw new InvalidOperationException("The receipt says the connector was published, but the connector artifact is missing; refusing cleanup.");
        if (Directory.Exists(destinationRingDirectory))
            RequireCanonicalRingAcl(destinationRingDirectory, receipt.DestinationServiceSid);
    }

    private static void ValidateRecoveryArtifactsForComplete(HomeContinuityPendingReceipt receipt,
        string destinationRingDirectory, string destinationConnectorPath)
    {
        if (receipt.Artifacts is null)
            throw new InvalidOperationException("The pending receipt does not identify a complete staged artifact set; abort it after review.");
        if (!File.Exists(destinationConnectorPath)
            || !string.Equals(Sha256File(destinationConnectorPath), receipt.ConnectorSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The destination connector artifact does not match the receipt; refusing completion.");
        if (File.Exists(destinationRingDirectory))
            throw new InvalidOperationException("The destination ring path is a file; refusing completion.");
        var ringArtifacts = Directory.Exists(destinationRingDirectory)
            ? ReadArtifactManifest(destinationRingDirectory)
            : Array.Empty<HomeContinuityPendingArtifact>();
        if (Directory.Exists(destinationRingDirectory))
            RequireCanonicalRingAcl(destinationRingDirectory, receipt.DestinationServiceSid);
        if (ringArtifacts.Count != 0 && !string.Equals(receipt.Phase, "ring-publishing", StringComparison.Ordinal))
            throw new InvalidOperationException("The destination ring already contains artifacts before recovery publication; refusing completion.");
        if (string.Equals(receipt.Phase, "ring-published", StringComparison.Ordinal))
        {
            if (!ArtifactsEqual(ringArtifacts, receipt.Artifacts))
                throw new InvalidOperationException("The published ring does not match the receipt; refusing completion.");
            return;
        }
        if (!Directory.Exists(receipt.StagedDirectory))
            throw new InvalidOperationException("The pending receipt does not identify a complete staged artifact set; abort it after review.");
        var stagedArtifacts = ReadArtifactManifest(receipt.StagedDirectory);
        if (!ArtifactsSubset(stagedArtifacts, receipt.Artifacts)
            || !ArtifactsSubset(ringArtifacts, receipt.Artifacts)
            || !ArtifactsDisjoint(stagedArtifacts, ringArtifacts)
            || stagedArtifacts.Count + ringArtifacts.Count != receipt.Artifacts.Count)
            throw new InvalidOperationException("The staged and destination continuity artifacts do not match the receipt; refusing completion.");
    }

    private static void ValidateCompletedReceipt(HomeContinuityPendingReceipt receipt, IConfiguration configuration)
    {
        var configuredRing = configuration["DataProtection:KeyRingPath"]?.Trim();
        var configuredRoot = configuration["SiteServer:InstallationRoot"]?.Trim();
        var configuredSid = configuration["SiteServer:ServiceSid"]?.Trim();
        if (string.IsNullOrWhiteSpace(configuredRing) || string.IsNullOrWhiteSpace(configuredRoot)
            || string.IsNullOrWhiteSpace(configuredSid)
            || !string.Equals(Path.GetFullPath(configuredRing), receipt.DestinationRingPath, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(Path.GetFullPath(configuredRoot), receipt.DestinationInstallationRoot, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(configuredSid, receipt.DestinationServiceSid, StringComparison.Ordinal))
            throw new InvalidOperationException("The terminal continuity receipt is not bound to this site's configured ring, installation root and service SID.");
        if (string.IsNullOrWhiteSpace(receipt.PackageDirectory) || !IsSha256(receipt.ManifestSha256)
            || !IsSha256(receipt.ConnectorSha256) || !Directory.Exists(receipt.PackageDirectory))
            throw new InvalidOperationException("The terminal continuity receipt has no authenticated package identity.");
        RequireNoReparsePoints(receipt.PackageDirectory, "The continuity package");
        var manifestPath = Path.Combine(receipt.PackageDirectory, "manifest.json");
        if (!string.Equals(Sha256File(manifestPath), receipt.ManifestSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The terminal continuity package manifest changed after publication.");
        var manifest = ReadManifest(manifestPath);
        if (manifest.PackageId != receipt.PackageId)
            throw new InvalidOperationException("The terminal continuity package ID does not match its receipt.");
        ValidateManifest(manifest, receipt.PackageDirectory, CanonicalTargetApplicationName, manifest.Binding);
        ValidateConnectorManifest(manifest.Connector, manifest.Binding);
        ValidateReceiptArtifactNames(receipt.Artifacts, manifest);
        if (!Directory.Exists(receipt.DestinationRingPath)
            || !File.Exists(receipt.DestinationConnectorPath)
            || receipt.Artifacts is null
            || !ArtifactsEqual(ReadArtifactManifest(receipt.DestinationRingPath), receipt.Artifacts)
            || !string.Equals(Sha256File(receipt.DestinationConnectorPath), receipt.ConnectorSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The terminal continuity receipt does not match the published ring and connector artifacts.");
        RequireCanonicalRingAcl(receipt.DestinationRingPath, receipt.DestinationServiceSid);
    }

    private static void ConsumeValidatedAbortedTerminal(string terminalPath, string destinationRingDirectory,
        string destinationInstallationRoot, string destinationConnectorPath, string destinationServiceSid)
    {
        if (!File.Exists(terminalPath) && !Directory.Exists(terminalPath)) return;
        var receipt = ReadPendingReceipt(terminalPath);
        if (!string.Equals(receipt.Phase, "aborted", StringComparison.Ordinal)
            || !string.Equals(receipt.DestinationRingPath, Path.GetFullPath(destinationRingDirectory), StringComparison.OrdinalIgnoreCase)
            || !string.Equals(receipt.DestinationInstallationRoot, Path.GetFullPath(destinationInstallationRoot), StringComparison.OrdinalIgnoreCase)
            || !string.Equals(receipt.DestinationConnectorPath, Path.GetFullPath(destinationConnectorPath), StringComparison.OrdinalIgnoreCase)
            || !string.Equals(receipt.DestinationServiceSid, destinationServiceSid, StringComparison.Ordinal)
            || receipt.Artifacts is null || receipt.Artifacts.Count != 0
            || Directory.Exists(receipt.StagedDirectory) || File.Exists(destinationConnectorPath)
            || Directory.Exists(destinationRingDirectory) && Directory.EnumerateFileSystemEntries(destinationRingDirectory).Any())
            throw new InvalidOperationException("The aborted continuity receipt does not authenticate an empty destination; refusing to remove it.");
        if (Directory.Exists(destinationRingDirectory))
            RequireCanonicalRingAcl(destinationRingDirectory, destinationServiceSid);
        File.Delete(terminalPath);
    }

    private static IReadOnlyList<HomeContinuityPendingArtifact> ReadArtifactManifest(string directory)
    {
        if (!Directory.Exists(directory)) return Array.Empty<HomeContinuityPendingArtifact>();
        RequireNoReparsePoints(directory, "The continuity artifact directory");
        if (Directory.EnumerateDirectories(directory).Any())
            throw new InvalidOperationException("The continuity artifact directory contains an unexpected subdirectory.");
        return Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly)
            .Select(path => new HomeContinuityPendingArtifact(Path.GetFileName(path), Sha256File(path)))
            .OrderBy(x => x.RelativePath, StringComparer.Ordinal)
            .ToArray();
    }

    private static bool ArtifactsEqual(IReadOnlyList<HomeContinuityPendingArtifact> actual,
        IReadOnlyList<HomeContinuityPendingArtifact> expected) =>
        actual.Count == expected.Count
        && actual.Zip(expected).All(pair => string.Equals(pair.First.RelativePath, pair.Second.RelativePath, StringComparison.Ordinal)
            && string.Equals(pair.First.Sha256, pair.Second.Sha256, StringComparison.OrdinalIgnoreCase));

    private static bool ArtifactsSubset(IReadOnlyList<HomeContinuityPendingArtifact> actual,
        IReadOnlyList<HomeContinuityPendingArtifact> expected) =>
        actual.All(candidate => expected.Any(reference =>
            string.Equals(candidate.RelativePath, reference.RelativePath, StringComparison.Ordinal)
            && string.Equals(candidate.Sha256, reference.Sha256, StringComparison.OrdinalIgnoreCase)));

    private static bool ArtifactsDisjoint(IReadOnlyList<HomeContinuityPendingArtifact> first,
        IReadOnlyList<HomeContinuityPendingArtifact> second) =>
        first.All(candidate => second.All(other =>
            !string.Equals(candidate.RelativePath, other.RelativePath, StringComparison.Ordinal)));

    private static void ValidateReceiptArtifactNames(
        IReadOnlyList<HomeContinuityPendingArtifact>? artifacts,
        HomeContinuityPackageManifest manifest)
    {
        if (artifacts is null)
            throw new InvalidOperationException("The continuity receipt has no authenticated artifact list.");
        var allowed = manifest.Keys.Select(x => x.FileName)
            .Concat(manifest.Revocations.Select(x => x.FileName))
            .Append("continuity-receipt.json")
            .ToHashSet(StringComparer.Ordinal);
        if (artifacts.Select(x => x.RelativePath).Distinct(StringComparer.Ordinal).Count() != artifacts.Count
            || artifacts.Any(x => string.IsNullOrWhiteSpace(x.RelativePath) || !allowed.Contains(x.RelativePath)
                || !IsSha256(x.Sha256)))
            throw new InvalidOperationException("The continuity receipt contains an unexpected or malformed artifact name.");
    }

    private static void PublishStagedArtifacts(string stagedDirectory, string destinationRingDirectory,
        IReadOnlyList<HomeContinuityPendingArtifact> expectedArtifacts, string destinationServiceSid,
        bool allowPartialExisting)
    {
        if (File.Exists(destinationRingDirectory))
            throw new InvalidOperationException("The destination ring path is a file; nothing was published.");
        if (Directory.Exists(destinationRingDirectory))
        {
            RequireCanonicalRingAcl(destinationRingDirectory, destinationServiceSid);
            var existing = ReadArtifactManifest(destinationRingDirectory);
            if (existing.Count != 0 && (!allowPartialExisting
                || !ArtifactsSubset(existing, expectedArtifacts)))
                throw new InvalidOperationException("The destination ring already contains artifacts; continuity refuses to merge or overwrite it.");
            foreach (var artifact in expectedArtifacts.OrderBy(x => x.RelativePath, StringComparer.Ordinal))
            {
                var source = Path.Combine(stagedDirectory, artifact.RelativePath);
                var target = Path.Combine(destinationRingDirectory, artifact.RelativePath);
                if (File.Exists(target))
                {
                    if (!allowPartialExisting
                        || !string.Equals(Sha256File(target), artifact.Sha256, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("The destination ring already contains an artifact; continuity refuses to overwrite it.");
                    continue;
                }
                if (!File.Exists(source) || Directory.Exists(target)
                    || !string.Equals(Sha256File(source), artifact.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("The staged continuity artifact changed before publication; nothing was published.");
                File.Move(source, target);
            }
            if (Directory.Exists(stagedDirectory)) Directory.Delete(stagedDirectory, recursive: true);
            if (!ArtifactsEqual(ReadArtifactManifest(destinationRingDirectory), expectedArtifacts))
                throw new InvalidOperationException("The published continuity ring did not match its authenticated receipt.");
            return;
        }
        Directory.Move(stagedDirectory, destinationRingDirectory);
        RequireCanonicalRingAcl(destinationRingDirectory, destinationServiceSid);
        if (!ArtifactsEqual(ReadArtifactManifest(destinationRingDirectory), expectedArtifacts))
            throw new InvalidOperationException("The published continuity ring did not match its authenticated receipt.");
    }

    private static void RequireDestinationRingEmpty(string path, string serviceSid)
    {
        RequireNoReparsePoints(Path.GetDirectoryName(Path.GetFullPath(path))
            ?? throw new InvalidOperationException("The destination ring has no parent directory."),
            "The destination Data Protection ring parent");
        if (File.Exists(path)) throw new InvalidOperationException("The destination Data Protection ring path is a file.");
        if (Directory.Exists(path))
        {
            RequireNoReparsePoints(path, "The destination Data Protection ring");
            if (Directory.EnumerateFileSystemEntries(path).Any())
                throw new InvalidOperationException("The destination Data Protection ring already contains artifacts; continuity refuses to merge or overwrite it.");
            RequireCanonicalRingAcl(path, serviceSid);
        }
    }

    private static void RequireCanonicalRingAcl(string path, string serviceSid)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Continuity ACL validation requires Windows.");
        ValidateServiceSid(serviceSid, "The destination connector service SID");
        var security = new DirectoryInfo(path).GetAccessControl();
        if (!security.AreAccessRulesProtected)
            throw new InvalidOperationException("The destination Data Protection ring inherits permissions; continuity refuses an unprotected staging boundary.");

        var expected = new Dictionary<string, FileSystemRights>(StringComparer.Ordinal)
        {
            // Windows materializes Modify with Synchronize on an explicit directory
            // grant.  Accept that normalized mask while keeping the identity,
            // inheritance, and three-rule boundary exact.
            [serviceSid] = FileSystemRights.Modify | FileSystemRights.Synchronize,
            ["S-1-5-18"] = FileSystemRights.FullControl,
            ["S-1-5-32-544"] = FileSystemRights.FullControl,
        };
        var rules = security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>().ToArray();
        if (rules.Length != expected.Count || rules.Any(rule => rule.AccessControlType != AccessControlType.Allow
            || rule.IsInherited || rule.InheritanceFlags != (InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit)
            || rule.PropagationFlags != PropagationFlags.None || rule.IdentityReference is not SecurityIdentifier identity
            || !expected.TryGetValue(identity.Value, out var rights) || rule.FileSystemRights != rights))
            throw new InvalidOperationException("The destination Data Protection ring must retain exactly API Modify plus SYSTEM and Administrators FullControl with protected inheritance.");
    }

    private static void RequireProtectedDirectoryAcl(string path, string description)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Continuity ACL validation requires Windows.");
        var security = new DirectoryInfo(path).GetAccessControl();
        if (!security.AreAccessRulesProtected)
            throw new InvalidOperationException($"{description} inherits permissions; continuity refuses an unprotected staging boundary.");
    }

    private static void WriteXmlFile(string path, XElement element)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
        using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        {
            element.Save(stream, SaveOptions.DisableFormatting);
            stream.Flush(flushToDisk: true);
        }
        File.Move(temp, path);
    }

    private static void WriteBytesFile(string path, byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
        using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        {
            stream.Write(bytes, 0, bytes.Length);
            stream.Flush(flushToDisk: true);
        }
        File.Move(temp, path);
    }

    private static XElement ReadXmlFile(string path) =>
        XElement.Load(path, LoadOptions.PreserveWhitespace);

    private static void WriteJsonFile<T>(string path, T value)
    {
        var temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
        using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        using (var writer = new StreamWriter(stream))
        {
            writer.Write(JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }));
            writer.Flush();
            stream.Flush(flushToDisk: true);
        }
        File.Move(temp, path);
    }

    private static void ReplaceJsonFile<T>(string path, T value)
    {
        var temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
        using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        using (var writer = new StreamWriter(stream))
        {
            writer.Write(JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }));
            writer.Flush();
            stream.Flush(flushToDisk: true);
        }
        File.Move(temp, path, overwrite: true);
    }

    private static string Sha256File(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static string Sha256Utf8(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static string DigestElements(IEnumerable<XElement> elements)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var element in elements.OrderBy(x => x.Name.LocalName).ThenBy(x => (string?)x.Attribute("id")))
            hash.AppendData(System.Text.Encoding.UTF8.GetBytes(element.ToString(SaveOptions.DisableFormatting)));
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static void ValidateBinding(HomeContinuityIdentityBinding binding)
    {
        if (string.IsNullOrWhiteSpace(binding.SourceInstallationId)
            || string.IsNullOrWhiteSpace(binding.SourceApplicationDiscriminator)
            || string.IsNullOrWhiteSpace(binding.TargetApplicationName)
            || string.IsNullOrWhiteSpace(binding.SourceProtectionProvider)
            || string.IsNullOrWhiteSpace(binding.VerifiedBackupId)
            || string.IsNullOrWhiteSpace(binding.ConnectorDeploymentId)
            || string.IsNullOrWhiteSpace(binding.ConnectorKeyId)
            || string.IsNullOrWhiteSpace(binding.ConnectorPublicKeyFingerprint)
            || string.IsNullOrWhiteSpace(binding.VerifiedBackupArchiveSha256)
            || !IsSha256(binding.ConnectorPublicKeyFingerprint)
            || !IsSha256(binding.VerifiedBackupArchiveSha256))
            throw new ArgumentException("A HOME continuity identity binding is incomplete.", nameof(binding));
    }

    private static bool IsSha256(string value) => value.Length == 64 && value.All(Uri.IsHexDigit);

    private static void RequireNoReparsePoints(string path, string description)
    {
        var full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var current = full;
        while (!string.IsNullOrEmpty(current))
        {
            if (Directory.Exists(current)
                && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException($"{description} uses a reparse point at '{current}'.");
            var parent = Directory.GetParent(current)?.FullName;
            if (string.Equals(parent, current, StringComparison.OrdinalIgnoreCase)) break;
            current = parent ?? string.Empty;
        }
    }

    private sealed record RevocationMetadata(Guid KeyId, DateTimeOffset RevocationDate);

    private sealed class MemoryXmlRepository : IXmlRepository
    {
        private readonly List<XElement> elements = [];
        public IReadOnlyCollection<XElement> GetAllElements() => elements.Select(x => new XElement(x)).ToArray();
        public void StoreElement(XElement element, string friendlyName) => elements.Add(new XElement(element));
    }
}

public sealed record HomeContinuityIdentityBinding(
    string SourceInstallationId,
    string SourceApplicationDiscriminator,
    string TargetApplicationName,
    string SourceProtectionProvider,
    string VerifiedBackupId,
    string ConnectorDeploymentId,
    string ConnectorKeyId,
    string ConnectorPublicKeyFingerprint = "",
    string VerifiedBackupArchiveSha256 = "");

public sealed record HomeContinuityKeyManifestEntry(
    Guid KeyId,
    DateTimeOffset CreationDate,
    DateTimeOffset ActivationDate,
    DateTimeOffset ExpirationDate,
    string FileName,
    string Sha256);

public sealed record HomeContinuityRevocationManifestEntry(
    string FileName, string Sha256, Guid KeyId, DateTimeOffset RevocationDate);

public sealed record HomeContinuityApplyReceipt(
    int SchemaVersion,
    Guid PackageId,
    string ManifestSha256,
    string SourceRingDigest,
    string TargetApplicationName,
    string TargetProtectionProvider,
    int KeyCount,
    int RevocationCount,
    string ConnectorKeyId,
    string ConnectorPublicKeyFingerprint,
    string ConnectorDeploymentId,
    string ConnectorPath,
    DateTimeOffset AppliedAt);

public sealed record HomeContinuityPendingReceipt(
    int SchemaVersion,
    Guid PackageId,
    string ManifestSha256,
    string DestinationInstallationRoot,
    string DestinationRingPath,
    string DestinationConnectorPath,
    string DestinationServiceSid,
    string Phase,
    DateTimeOffset RecordedAt,
    string PackageDirectory = "",
    string StagedDirectory = "",
    IReadOnlyList<HomeContinuityPendingArtifact>? Artifacts = null,
    string ConnectorSha256 = "");

public sealed record HomeContinuityPendingArtifact(string RelativePath, string Sha256);

public sealed record HomeContinuityConnectorMaterial(
    string DeploymentId,
    string KeyId,
    string PublicKeyFingerprint,
    string PrivateKeyPem,
    string SourceInstallationRoot,
    string SourceConnectorPath,
    string SourceServiceSid);

public sealed record HomeContinuityConnectorManifest(
    string FileName,
    string Sha256,
    string DeploymentId,
    string KeyId,
    string PublicKeyFingerprint,
    string SourceInstallationRoot,
    string SourceConnectorPath,
    string SourceServiceSid);

public sealed record HomeContinuityPackageManifest(
    int SchemaVersion,
    Guid PackageId,
    DateTimeOffset CreatedAt,
    string SourceApplicationDiscriminator,
    string TargetApplicationName,
    string SourceProtectionProvider,
    string SourceRingDigest,
    HomeContinuityIdentityBinding Binding,
    IReadOnlyList<HomeContinuityKeyManifestEntry> Keys,
    IReadOnlyList<HomeContinuityRevocationManifestEntry> Revocations,
    HomeContinuityConnectorManifest Connector,
    string CompatibilityStatus = HomeContinuityCompatibilityReader.CompatibilityProof,
    bool CryptographicCompatibilityProof = true);
