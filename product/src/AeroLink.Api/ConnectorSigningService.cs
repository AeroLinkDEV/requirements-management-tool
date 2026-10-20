using System.Security.Cryptography;
using System.Text;
using AeroLink.ConnectorProtocol;

namespace AeroLink.Api;

public sealed class ConnectorSigningService : IDisposable
{
    private readonly ECDsa _key;
    private readonly Lock _keyLock = new();
    private readonly string? _publicOrigin;
    public string DeploymentId { get; }
    public string KeyId { get; }
    public string PublicKeyPem { get; }

    public ConnectorSigningService(IConfiguration configuration)
    {
        DeploymentId = ResolveDeploymentId(configuration);
        var keyPath = ResolveKeyPath(configuration);
        Directory.CreateDirectory(Path.GetDirectoryName(keyPath)!);
        _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        if (File.Exists(keyPath)) _key.ImportFromPem(File.ReadAllText(keyPath));
        else
        {
            var pem = _key.ExportPkcs8PrivateKeyPem();
            try
            {
                using var file = new FileStream(keyPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                using var writer = new StreamWriter(file, new UTF8Encoding(false));
                writer.Write(pem);
            }
            catch (IOException) when (File.Exists(keyPath))
            {
                _key.Dispose();
                _key = ECDsa.Create();
                _key.ImportFromPem(File.ReadAllText(keyPath));
            }
        }
        PublicKeyPem = ConnectorLaunchProtocol.ExportPublicKey(_key);
        KeyId = ConnectorLaunchProtocol.PublicKeyFingerprint(PublicKeyPem)[..24];
        _publicOrigin = ResolveConfiguredOrigin(configuration);
    }

    // Do not resolve/construct the signing service for a diagnostic: its normal first-use path creates a key.
    // This uses the identical configuration resolution but only reads an existing ordinary file in memory.
    public static ConnectorRuntimeIdentityCapture CaptureExistingIdentity(IConfiguration configuration)
    {
        string? deployment = null, path = null, origin = null;
        string deploymentSource = "unproven", pathSource = "unproven", originSource = "unproven";
        ConnectorRuntimeIdentityCapture Result(string status, string? keyId = null, string? fingerprint = null, string? fileHash = null)
            => new(status, deployment, path, origin, keyId, fingerprint, fileHash, deploymentSource, pathSource, originSource);
        try
        {
            deploymentSource = string.IsNullOrWhiteSpace(configuration["Connector:DeploymentId"])
                ? "resolved-default:current-process-machine-and-user-domain" : "IConfiguration:Connector:DeploymentId";
            pathSource = string.IsNullOrWhiteSpace(configuration["Connector:SigningKeyPath"])
                ? "resolved-default:current-process-local-application-data" : "IConfiguration:Connector:SigningKeyPath";
            originSource = string.IsNullOrWhiteSpace(configuration["Connector:PublicOrigin"])
                ? "request-derived" : "IConfiguration:Connector:PublicOrigin";
            deployment = ResolveDeploymentId(configuration);
            path = ResolveKeyPath(configuration);
            origin = ResolveConfiguredOrigin(configuration);
            if (!File.Exists(path)) return Result("absent");
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                return Result("unproven");
            for (var directory = new DirectoryInfo(Path.GetDirectoryName(path)!); directory is not null; directory = directory.Parent)
                if ((directory.Attributes & FileAttributes.ReparsePoint) != 0)
                    return Result("unproven");
            var bytes = File.ReadAllBytes(path);
            try
            {
                using var key = ECDsa.Create();
                key.ImportFromPem(Encoding.UTF8.GetString(bytes));
                if (key.KeySize != 256) return Result("unproven");
                var fingerprint = Convert.ToHexString(SHA256.HashData(key.ExportSubjectPublicKeyInfo())).ToLowerInvariant();
                return Result("existing-key-file", fingerprint[..24], fingerprint, Convert.ToHexString(SHA256.HashData(bytes)));
            }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
            or CryptographicException or InvalidOperationException)
        {
            return Result("unavailable");
        }
    }

    private static string ResolveDeploymentId(IConfiguration configuration)
    {
        var configured = configuration["Connector:DeploymentId"]?.Trim();
        var deployment = string.IsNullOrWhiteSpace(configured) ? DefaultDeploymentId() : configured;
        if (deployment.Length is < 3 or > 100 || deployment.Any(c => !(char.IsLetterOrDigit(c) || c is '-' or '_' or '.')))
            throw new InvalidOperationException("Connector:DeploymentId must be a stable 3-100 character deployment identifier.");
        return deployment;
    }

    private static string ResolveKeyPath(IConfiguration configuration)
    {
        var path = configuration["Connector:SigningKeyPath"];
        return Path.GetFullPath(string.IsNullOrWhiteSpace(path)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AeroLink", "keys", "connector-signing-key.pem")
            : path);
    }

    private static string? ResolveConfiguredOrigin(IConfiguration configuration)
    {
        var origin = configuration["Connector:PublicOrigin"]?.Trim();
        return string.IsNullOrWhiteSpace(origin) ? null : ConnectorLaunchProtocol.NormalizeOrigin(origin, allowInsecureLoopback: true);
    }

    public ConnectorEnrollmentManifest Enrollment(string origin, DateTimeOffset now)
    {
        var normalized = ConnectorLaunchProtocol.NormalizeOrigin(origin, allowInsecureLoopback: true);
        return new(ConnectorLaunchProtocol.Version, ConnectorLaunchProtocol.ProfileVersion, DeploymentId,
            normalized, KeyId, PublicKeyPem, ConnectorLaunchProtocol.PublicKeyFingerprint(PublicKeyPem),
            new Uri(normalized).Scheme == Uri.UriSchemeHttp, now);
    }

    public string Sign(ConnectorLaunchEnvelope envelope)
    {
        lock (_keyLock) return ConnectorLaunchProtocol.Sign(envelope, _key);
    }

    public string ResolveOrigin(HttpContext context) => _publicOrigin
        ?? ConnectorLaunchProtocol.NormalizeOrigin($"{context.Request.Scheme}://{context.Request.Host}", allowInsecureLoopback: true);

    public void Dispose()
    {
        lock (_keyLock) _key.Dispose();
    }

    private static string DefaultDeploymentId()
    {
        var machine = Encoding.UTF8.GetBytes($"{Environment.MachineName}|{Environment.UserDomainName}");
        return "aerolink-local-" + Convert.ToHexString(SHA256.HashData(machine))[..16].ToLowerInvariant();
    }
}

public sealed record ConnectorRuntimeIdentityCapture(string Status, string? DeploymentId, string? SigningKeyPath,
    string? ConfiguredPublicOrigin, string? KeyId, string? PublicFingerprintSha256, string? KeyFileSha256,
    string DeploymentIdSource, string SigningKeyPathSource, string PublicOriginSource);
