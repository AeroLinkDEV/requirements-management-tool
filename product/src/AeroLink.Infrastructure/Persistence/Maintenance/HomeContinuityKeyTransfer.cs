using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Globalization;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Microsoft.AspNetCore.DataProtection.XmlEncryption;
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
    private static readonly XNamespace DataProtectionNamespace =
        XNamespace.Get("http://schemas.asp.net/2015/03/dataProtection");
    private static readonly XNamespace XmlEncryptionNamespace =
        XNamespace.Get("http://www.w3.org/2001/04/xmlenc#");
    private const string DataProtectionAssemblyPrefix = "Microsoft.AspNetCore.DataProtection,";
    private const string EncryptedXmlDecryptorPrefix =
        "Microsoft.AspNetCore.DataProtection.XmlEncryption.EncryptedXmlDecryptor, Microsoft.AspNetCore.DataProtection,";

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
                ValidateRevocation(revocationElements[index]);
                var relativeName = $"revocation-{index:D4}.xml";
                var path = Path.Combine(stagingDirectory, relativeName);
                WriteXmlFile(path, revocationElements[index]);
                revocations.Add(new HomeContinuityRevocationManifestEntry(relativeName, Sha256File(path)));
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

        var manifestPath = Path.Combine(packageDirectory, "manifest.json");
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
            ValidateRevocation(element);
            sourceRepository.StoreElement(element, revocation.FileName);
        }

        using var sourceServices = BuildCertificateUnwrapServices(
            sourceRepository, manifest.SourceApplicationDiscriminator, destinationTransportCertificate);
        var sourceManager = sourceServices.GetRequiredService<IKeyManager>();
        var sourceKeys = sourceManager.GetAllKeys().ToDictionary(x => x.KeyId);
        if (sourceKeys.Count != manifest.Keys.Count)
            throw new InvalidOperationException("The portable package did not load the expected key count.");

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
            || !string.Equals(manifest.Binding.ConnectorKeyId, expectedBinding.ConnectorKeyId, StringComparison.Ordinal))
            throw new InvalidOperationException("The continuity package is bound to a different source installation, backup, or connector identity.");

        var allNames = manifest.Keys.Select(x => x.FileName).Concat(manifest.Revocations.Select(x => x.FileName)).ToArray();
        if (allNames.Any(x => Path.IsPathRooted(x) || x.Contains("..", StringComparison.Ordinal) || Path.GetFileName(x) != x)
            || allNames.Distinct(StringComparer.OrdinalIgnoreCase).Count() != allNames.Length)
            throw new InvalidOperationException("The continuity package contains an unsafe or duplicate file name.");
        if (manifest.Keys.Any(x => x.KeyId == Guid.Empty)
            || manifest.Keys.Select(x => x.KeyId).Distinct().Count() != manifest.Keys.Count)
            throw new InvalidOperationException("The continuity package contains an empty or duplicate key identity.");
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

    private static void ValidateRevocation(XElement element)
    {
        if (!IsRevocationElement(element) || element.Attributes().Count() != 1
            || element.Attribute("version") is null || element.Attribute("version")!.Value != "1"
            || element.Elements().Any(x => x.Name.LocalName is not ("revocationDate" or "reason" or "key"))
            || element.Element("revocationDate") is null || element.Element("key") is null
            || element.Elements("key").Count() != 1)
            throw new InvalidOperationException("The source Data Protection repository contains a malformed revocation.");
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
        if (File.Exists(path)) throw new InvalidOperationException("The destination Data Protection ring path is a file.");
        if (Directory.Exists(path))
            throw new InvalidOperationException("The destination Data Protection ring already exists; continuity refuses to merge or overwrite it.");
    }

    private static void WriteXmlFile(string path, XElement element)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
        element.Save(temp, SaveOptions.DisableFormatting);
        File.Move(temp, path);
    }

    private static XElement ReadXmlFile(string path) =>
        XElement.Load(path, LoadOptions.PreserveWhitespace);

    private static void WriteJsonFile(string path, HomeContinuityPackageManifest manifest)
    {
        var temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
        File.WriteAllText(temp, JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
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
            || string.IsNullOrWhiteSpace(binding.ConnectorKeyId))
            throw new ArgumentException("A HOME continuity identity binding is incomplete.", nameof(binding));
    }

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
    string ConnectorKeyId);

public sealed record HomeContinuityKeyManifestEntry(
    Guid KeyId,
    DateTimeOffset CreationDate,
    DateTimeOffset ActivationDate,
    DateTimeOffset ExpirationDate,
    string FileName,
    string Sha256);

public sealed record HomeContinuityRevocationManifestEntry(string FileName, string Sha256);

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
