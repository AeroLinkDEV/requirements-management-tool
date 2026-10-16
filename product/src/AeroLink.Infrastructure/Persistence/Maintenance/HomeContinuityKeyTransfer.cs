using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.AccessControl;
using System.Globalization;
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
            case "Unprotected":
                break;
            default:
                throw new InvalidOperationException(
                    $"The captured source protection provider '{sourceProtectionProvider}' is unsupported. " +
                    "Use DpapiCurrentUser, DpapiLocalMachine, or Unprotected.");
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

    public static HomeContinuityPackageManifest ExportPackage(
        IKeyManager sourceKeyManager,
        IXmlRepository sourceRepository,
        X509Certificate2 destinationTransportCertificate,
        HomeContinuityIdentityBinding binding,
        string packageDirectory)
    {
        ArgumentNullException.ThrowIfNull(sourceKeyManager);
        ArgumentNullException.ThrowIfNull(sourceRepository);
        ArgumentNullException.ThrowIfNull(destinationTransportCertificate);
        ValidateBinding(binding);
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

            var manifest = new HomeContinuityPackageManifest(
                SchemaVersion: 1,
                PackageId: Guid.NewGuid(),
                CreatedAt: DateTimeOffset.UtcNow,
                SourceApplicationDiscriminator: binding.SourceApplicationDiscriminator,
                TargetApplicationName: binding.TargetApplicationName,
                SourceProtectionProvider: binding.SourceProtectionProvider,
                SourceRingDigest: DigestElements(sourceElements),
                Binding: binding,
                Keys: keys,
                Revocations: revocations);

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
        string expectedManifestSha256)
    {
        ArgumentNullException.ThrowIfNull(destinationTransportCertificate);
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("LocalMachine DPAPI ring rewrapping requires Windows.");
        if (string.IsNullOrWhiteSpace(expectedTargetApplicationName))
            throw new ArgumentException("A target application name is required.", nameof(expectedTargetApplicationName));
        ValidateBinding(expectedBinding);
        if (!string.Equals(expectedTargetApplicationName, CanonicalTargetApplicationName, StringComparison.Ordinal))
            throw new InvalidOperationException("Continuity can only install the canonical AeroLink application boundary.");

        var manifestPath = Path.Combine(packageDirectory, "manifest.json");
        RequireNoReparsePoints(packageDirectory, "The continuity package");
        if (!string.Equals(Sha256File(manifestPath), expectedManifestSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The continuity manifest hash does not match the operator-verified handoff; nothing was installed.");
        var manifest = ReadManifest(manifestPath);
        ValidateManifest(manifest, packageDirectory, expectedTargetApplicationName, expectedBinding);
        RequireDestinationRingEmpty(destinationRingDirectory);

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
        Directory.CreateDirectory(stagedDirectory);
        try
        {
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
            WriteJsonFile(Path.Combine(stagedDirectory, "continuity-receipt.json"), new HomeContinuityApplyReceipt(
                1, manifest.PackageId, expectedManifestSha256, manifest.SourceRingDigest,
                expectedTargetApplicationName, CanonicalTargetProtectionProvider, manifest.Keys.Count,
                manifest.Revocations.Count, DateTimeOffset.UtcNow));
            Directory.CreateDirectory(Path.GetDirectoryName(destinationRingDirectory)
                ?? throw new InvalidOperationException("The destination ring has no parent directory."));
            if (Directory.Exists(destinationRingDirectory) || File.Exists(destinationRingDirectory))
                throw new InvalidOperationException("The destination Data Protection ring appeared during preparation; nothing was installed.");
            Directory.Move(stagedDirectory, destinationRingDirectory);
        }
        catch
        {
            if (Directory.Exists(stagedDirectory)) Directory.Delete(stagedDirectory, recursive: true);
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
        if (manifest.SchemaVersion != 1 || manifest.PackageId == Guid.Empty || manifest.Keys.Count == 0)
            throw new InvalidOperationException("The HOME continuity package manifest is unsupported or incomplete.");
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

        var allNames = manifest.Keys.Select(x => x.FileName).Concat(manifest.Revocations.Select(x => x.FileName)).ToArray();
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

    private static void RequireDestinationRingEmpty(string path)
    {
        RequireNoReparsePoints(Path.GetDirectoryName(Path.GetFullPath(path))
            ?? throw new InvalidOperationException("The destination ring has no parent directory."),
            "The destination Data Protection ring parent");
        if (File.Exists(path)) throw new InvalidOperationException("The destination Data Protection ring path is a file.");
        if (Directory.Exists(path))
            throw new InvalidOperationException("The destination Data Protection ring already exists; continuity refuses to merge or overwrite it.");
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

    private static string Sha256File(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

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
    DateTimeOffset AppliedAt);

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
    IReadOnlyList<HomeContinuityRevocationManifestEntry> Revocations);
