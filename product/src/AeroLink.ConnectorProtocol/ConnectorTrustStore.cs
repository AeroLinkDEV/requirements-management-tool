using System.Text.Json;
using System.Security.Cryptography;
using System.Text;

namespace AeroLink.ConnectorProtocol;

public sealed record TrustedConnectorDeployment(
    string DeploymentId,
    string Origin,
    string KeyId,
    string PublicKey,
    string PublicKeyFingerprint,
    bool AllowInsecureLoopback,
    DateTimeOffset EnrolledAt,
    string EnrolledBy,
    DateTimeOffset? RevokedAt = null);

public sealed record ConnectorTrustDocument(int Version, IReadOnlyList<TrustedConnectorDeployment> Deployments);

public sealed class ConnectorTrustStore(string rootPath)
{
    private const int MaximumTrustFileBytes = 1024 * 1024;
    private readonly string _root = Path.GetFullPath(rootPath);
    private string TrustPath => Path.Combine(_root, "trusted-deployments.json");
    private string ReplayPath => Path.Combine(_root, "consumed-launches.json");
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public IReadOnlyList<TrustedConnectorDeployment> Load()
    {
        if (!File.Exists(TrustPath)) return [];
        if (new FileInfo(TrustPath).Length > MaximumTrustFileBytes) throw new ConnectorProtocolException("connector_trust_invalid", "The connector trust store is oversized.");
        var document = JsonSerializer.Deserialize<ConnectorTrustDocument>(File.ReadAllBytes(TrustPath), Json)
            ?? throw new ConnectorProtocolException("connector_trust_invalid", "The connector trust store is empty.");
        if (document.Version != 1 || document.Deployments.Count > 100) throw new ConnectorProtocolException("connector_trust_invalid", "The connector trust store version or entry count is invalid.");
        return document.Deployments;
    }

    public TrustedConnectorDeployment Require(ConnectorLaunchEnvelope envelope)
    {
        var deployment = Require(envelope.DeploymentId, envelope.KeyId);
        var signedOrigin = ConnectorLaunchProtocol.NormalizeOrigin(envelope.Origin, deployment.AllowInsecureLoopback);
        var enrolledOrigin = ConnectorLaunchProtocol.NormalizeOrigin(deployment.Origin, deployment.AllowInsecureLoopback);
        if (!string.Equals(signedOrigin, enrolledOrigin, StringComparison.Ordinal))
            throw new ConnectorProtocolException("connector_origin_mismatch", "The signed connector origin does not match its enrolled deployment.");
        if (!string.Equals(deployment.PublicKeyFingerprint, ConnectorLaunchProtocol.PublicKeyFingerprint(deployment.PublicKey), StringComparison.Ordinal))
            throw new ConnectorProtocolException("connector_trust_invalid", "The enrolled connector public key does not match its fingerprint.");
        return deployment;
    }

    public TrustedConnectorDeployment Require(string deploymentId, string keyId)
    {
        var matches = Load().Where(x => x.RevokedAt is null
            && string.Equals(x.DeploymentId, deploymentId, StringComparison.Ordinal)
            && string.Equals(x.KeyId, keyId, StringComparison.Ordinal)).ToList();
        if (matches.Count > 1) throw new ConnectorProtocolException("connector_trust_invalid", "The connector trust store contains an ambiguous active enrollment.");
        var deployment = matches.SingleOrDefault();
        return deployment ?? throw new ConnectorProtocolException("connector_deployment_untrusted", "This AeroLink deployment or signing key is not enrolled.");
    }

    public TrustedConnectorDeployment Enroll(ConnectorEnrollmentManifest manifest, string actor, DateTimeOffset now)
    {
        if (manifest.ProtocolVersion != ConnectorLaunchProtocol.Version || manifest.ProfileVersion != ConnectorLaunchProtocol.ProfileVersion)
            throw new ConnectorProtocolException("connector_version_unsupported", "The deployment requires an unsupported connector protocol or document profile.");
        var origin = ConnectorLaunchProtocol.NormalizeOrigin(manifest.Origin, manifest.AllowInsecureLoopback);
        if (manifest.AllowInsecureLoopback && (!new Uri(origin).IsLoopback || new Uri(origin).Scheme != Uri.UriSchemeHttp))
            throw new ConnectorProtocolException("connector_origin_invalid", "Insecure enrollment is permitted only for an explicit HTTP loopback origin.");
        var fingerprint = ConnectorLaunchProtocol.PublicKeyFingerprint(manifest.PublicKey);
        if (!string.Equals(fingerprint, manifest.PublicKeyFingerprint, StringComparison.Ordinal)
            || !string.Equals(manifest.KeyId, fingerprint[..24], StringComparison.Ordinal))
            throw new ConnectorProtocolException("connector_trust_invalid", "The deployment public-key identity is invalid.");
        var existing = Load();
        if (existing.Any(x => x.DeploymentId == manifest.DeploymentId && x.KeyId == manifest.KeyId && x.RevokedAt is not null))
            throw new ConnectorProtocolException("connector_key_retired", "A retired connector signing key cannot be re-enrolled.");
        var entries = existing.Select(x => x.DeploymentId == manifest.DeploymentId && x.RevokedAt is null ? x with { RevokedAt = now } : x).ToList();
        var trusted = new TrustedConnectorDeployment(manifest.DeploymentId, origin, manifest.KeyId, manifest.PublicKey,
            fingerprint, manifest.AllowInsecureLoopback, now, string.IsNullOrWhiteSpace(actor) ? Environment.UserName : actor.Trim());
        entries.Add(trusted);
        Save(TrustPath, new ConnectorTrustDocument(1, entries));
        AppendAudit($"{now:O}\tenroll\t{trusted.DeploymentId}\t{trusted.Origin}\t{trusted.KeyId}\t{trusted.EnrolledBy}");
        return trusted;
    }

    public void Revoke(string deploymentId, string keyId, string actor, DateTimeOffset now)
    {
        var entries = Load().ToList(); var index = entries.FindIndex(x => x.DeploymentId == deploymentId && x.KeyId == keyId && x.RevokedAt is null);
        if (index < 0) throw new ConnectorProtocolException("connector_deployment_untrusted", "The active connector enrollment was not found.");
        entries[index] = entries[index] with { RevokedAt = now };
        Save(TrustPath, new ConnectorTrustDocument(1, entries));
        AppendAudit($"{now:O}\trevoke\t{deploymentId}\t{keyId}\t{actor.Trim()}");
    }

    public void ConsumeNonce(string deploymentId, string nonce, DateTimeOffset expiresAt, DateTimeOffset now)
    {
        Directory.CreateDirectory(_root);
        using var replayLock = new FileStream(Path.Combine(_root, "consumed-launches.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var nonceHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(nonce))).ToLowerInvariant();
        var entries = File.Exists(ReplayPath)
            ? JsonSerializer.Deserialize<List<ConsumedLaunch>>(File.ReadAllBytes(ReplayPath), Json) ?? [] : [];
        entries.RemoveAll(x => x.ExpiresAt <= now.AddMinutes(-10));
        if (entries.Any(x => x.DeploymentId == deploymentId && x.NonceHash == nonceHash))
            throw new ConnectorProtocolException("connector_envelope_replayed", "This connector launch envelope has already been used.");
        entries.Add(new(deploymentId, nonceHash, expiresAt));
        Save(ReplayPath, entries);
    }

    private void Save<T>(string path, T value)
    {
        Directory.CreateDirectory(_root);
        var temporary = path + $".{Guid.NewGuid():N}.tmp";
        File.WriteAllBytes(temporary, JsonSerializer.SerializeToUtf8Bytes(value, Json));
        File.Move(temporary, path, true);
    }

    private void AppendAudit(string line)
    {
        Directory.CreateDirectory(_root);
        File.AppendAllLines(Path.Combine(_root, "trust-audit.log"), [line]);
    }

    private sealed record ConsumedLaunch(string DeploymentId, string NonceHash, DateTimeOffset ExpiresAt);
}
