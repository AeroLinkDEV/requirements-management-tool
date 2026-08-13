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
        var configuredDeployment = configuration["Connector:DeploymentId"]?.Trim();
        DeploymentId = string.IsNullOrWhiteSpace(configuredDeployment) ? DefaultDeploymentId() : configuredDeployment;
        if (DeploymentId.Length is < 3 or > 100 || DeploymentId.Any(c => !(char.IsLetterOrDigit(c) || c is '-' or '_' or '.')))
            throw new InvalidOperationException("Connector:DeploymentId must be a stable 3-100 character deployment identifier.");
        var keyPath = configuration["Connector:SigningKeyPath"];
        if (string.IsNullOrWhiteSpace(keyPath))
            keyPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AeroLink", "keys", "connector-signing-key.pem");
        keyPath = Path.GetFullPath(keyPath);
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
        var configuredOrigin = configuration["Connector:PublicOrigin"]?.Trim();
        _publicOrigin = string.IsNullOrWhiteSpace(configuredOrigin) ? null
            : ConnectorLaunchProtocol.NormalizeOrigin(configuredOrigin, allowInsecureLoopback: true);
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
