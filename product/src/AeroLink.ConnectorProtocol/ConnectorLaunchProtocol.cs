using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AeroLink.ConnectorProtocol;

public sealed record ConnectorLaunchEnvelope(
    string ProtocolVersion,
    string ProfileVersion,
    string DeploymentId,
    string Origin,
    string KeyId,
    string Nonce,
    DateTimeOffset ExpiresAt,
    Guid ProjectId,
    Guid DocumentId,
    string DocumentNumber,
    Guid RevisionId,
    string RevisionNumber,
    string Mode,
    Guid SourceAttachmentId,
    long SourceSize,
    string SourceSha256);

public sealed record ConnectorEnrollmentManifest(
    string ProtocolVersion,
    string ProfileVersion,
    string DeploymentId,
    string Origin,
    string KeyId,
    string PublicKey,
    string PublicKeyFingerprint,
    bool AllowInsecureLoopback,
    DateTimeOffset IssuedAt);

public sealed record ConnectorRedemptionIdentity(string Mode, string DeploymentId, string Origin, Guid ProjectId,
    Guid DocumentId, string DocumentNumber, Guid RevisionId, string RevisionNumber, Guid SourceAttachmentId,
    long SourceSize, string SourceSha256);

public static class ConnectorLaunchProtocol
{
    public const string Version = "aerolink-connector-launch-v1";
    public const string ProfileVersion = "aerolink-ooxml-safe-v1";
    public const int MaximumEnvelopeCharacters = 16_384;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public static string Sign(ConnectorLaunchEnvelope envelope, ECDsa privateKey)
    {
        Validate(envelope, DateTimeOffset.MinValue, checkExpiry: false);
        var payload = JsonSerializer.SerializeToUtf8Bytes(envelope, Json);
        var encodedPayload = Base64Url(payload);
        var signature = privateKey.SignData(Encoding.ASCII.GetBytes(encodedPayload), HashAlgorithmName.SHA256);
        return $"{encodedPayload}.{Base64Url(signature)}";
    }

    public static ConnectorLaunchEnvelope Verify(string compactEnvelope, string publicKeyPem, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(compactEnvelope) || compactEnvelope.Length > MaximumEnvelopeCharacters)
            throw new ConnectorProtocolException("connector_envelope_invalid", "The connector launch envelope is missing or oversized.");
        var pieces = compactEnvelope.Split('.');
        if (pieces.Length != 2 || pieces.Any(string.IsNullOrWhiteSpace))
            throw new ConnectorProtocolException("connector_envelope_invalid", "The connector launch envelope format is invalid.");
        try
        {
            using var key = ECDsa.Create();
            key.ImportFromPem(publicKeyPem);
            if (key.KeySize != 256) throw new ConnectorProtocolException("connector_key_unsupported", "The connector signing key is not an approved P-256 key.");
            var signature = FromBase64Url(pieces[1]);
            if (!key.VerifyData(Encoding.ASCII.GetBytes(pieces[0]), signature, HashAlgorithmName.SHA256))
                throw new ConnectorProtocolException("connector_envelope_signature_invalid", "The connector launch signature is invalid.");
            var envelope = JsonSerializer.Deserialize<ConnectorLaunchEnvelope>(FromBase64Url(pieces[0]), Json)
                ?? throw new ConnectorProtocolException("connector_envelope_invalid", "The connector launch envelope is empty.");
            Validate(envelope, now, checkExpiry: true);
            return envelope;
        }
        catch (ConnectorProtocolException) { throw; }
        catch (Exception ex) when (ex is ArgumentException or CryptographicException or FormatException or JsonException)
        {
            throw new ConnectorProtocolException("connector_envelope_invalid", "The connector launch envelope could not be verified.", ex);
        }
    }

    public static (string DeploymentId, string KeyId) ReadUnverifiedIdentity(string compactEnvelope)
    {
        if (string.IsNullOrWhiteSpace(compactEnvelope) || compactEnvelope.Length > MaximumEnvelopeCharacters)
            throw new ConnectorProtocolException("connector_envelope_invalid", "The connector launch envelope is missing or oversized.");
        var pieces = compactEnvelope.Split('.');
        if (pieces.Length != 2) throw new ConnectorProtocolException("connector_envelope_invalid", "The connector launch envelope format is invalid.");
        try
        {
            using var document = JsonDocument.Parse(FromBase64Url(pieces[0]), new JsonDocumentOptions { MaxDepth = 8 });
            var root = document.RootElement;
            var deploymentId = root.GetProperty("deploymentId").GetString();
            var keyId = root.GetProperty("keyId").GetString();
            if (!ValidToken(deploymentId, 100) || !ValidToken(keyId, 100)) throw new JsonException();
            return (deploymentId!, keyId!);
        }
        catch (Exception ex) when (ex is FormatException or JsonException or InvalidOperationException)
        {
            throw new ConnectorProtocolException("connector_envelope_invalid", "The connector launch identity is invalid.", ex);
        }
    }

    public static string NormalizeOrigin(string value, bool allowInsecureLoopback)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || !string.IsNullOrEmpty(uri.UserInfo)
            || uri.AbsolutePath != "/" || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            throw new ConnectorProtocolException("connector_origin_invalid", "A trusted deployment must use an exact origin without credentials, path, query, or fragment.");
        if (uri.Scheme != Uri.UriSchemeHttps && !(allowInsecureLoopback && uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback))
            throw new ConnectorProtocolException("connector_origin_invalid", "A trusted deployment requires HTTPS; explicit loopback development enrollment may use HTTP.");
        var builder = new UriBuilder(uri.Scheme.ToLowerInvariant(), uri.IdnHost.ToLowerInvariant(), uri.IsDefaultPort ? -1 : uri.Port);
        return builder.Uri.GetLeftPart(UriPartial.Authority);
    }

    public static string PublicKeyFingerprint(string publicKeyPem)
    {
        using var key = ECDsa.Create();
        key.ImportFromPem(publicKeyPem);
        if (key.KeySize != 256) throw new ConnectorProtocolException("connector_key_unsupported", "The connector signing key is not an approved P-256 key.");
        return Convert.ToHexString(SHA256.HashData(key.ExportSubjectPublicKeyInfo())).ToLowerInvariant();
    }

    public static string ExportPublicKey(ECDsa key) => key.ExportSubjectPublicKeyInfoPem();

    public static void ValidateRedemption(ConnectorLaunchEnvelope envelope, ConnectorRedemptionIdentity redemption)
    {
        var exact = redemption.Mode == envelope.Mode && redemption.DeploymentId == envelope.DeploymentId
            && NormalizeOrigin(redemption.Origin, allowInsecureLoopback: true) == envelope.Origin
            && redemption.ProjectId == envelope.ProjectId && redemption.DocumentId == envelope.DocumentId
            && redemption.DocumentNumber == envelope.DocumentNumber && redemption.RevisionId == envelope.RevisionId
            && redemption.RevisionNumber == envelope.RevisionNumber && redemption.SourceAttachmentId == envelope.SourceAttachmentId
            && redemption.SourceSize == envelope.SourceSize && string.Equals(redemption.SourceSha256, envelope.SourceSha256, StringComparison.OrdinalIgnoreCase);
        if (!exact) throw new ConnectorProtocolException("connector_redemption_mismatch", "The server redemption response does not match the signed Project, document, revision, mode, or source evidence.");
    }

    public static async Task CopyExactlyAsync(Stream source, Stream destination, long expectedSize, CancellationToken cancellationToken = default)
    {
        if (expectedSize <= 0 || expectedSize > 100L * 1024 * 1024)
            throw new ConnectorProtocolException("connector_download_oversized", "The signed controlled download size is outside the connector limit.");
        var buffer = new byte[81920]; long total = 0;
        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken); if (read == 0) break;
            total += read; if (total > expectedSize) throw new ConnectorProtocolException("connector_download_oversized", "The controlled download exceeded the signed size before Word could open.");
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
        if (total != expectedSize) throw new ConnectorProtocolException("connector_download_size_mismatch", "The controlled download length does not match the signed envelope.");
    }

    private static void Validate(ConnectorLaunchEnvelope value, DateTimeOffset now, bool checkExpiry)
    {
        if (value.ProtocolVersion != Version || value.ProfileVersion != ProfileVersion)
            throw new ConnectorProtocolException("connector_version_unsupported", "The connector launch protocol or document profile is not supported.");
        if (!ValidToken(value.DeploymentId, 100) || !ValidToken(value.KeyId, 100) || !ValidToken(value.Nonce, 256))
            throw new ConnectorProtocolException("connector_envelope_invalid", "The connector deployment, key, or nonce is invalid.");
        if (value.ProjectId == Guid.Empty || value.DocumentId == Guid.Empty || value.RevisionId == Guid.Empty || value.SourceAttachmentId == Guid.Empty)
            throw new ConnectorProtocolException("connector_envelope_invalid", "The connector target identity is incomplete.");
        if (!ValidText(value.DocumentNumber, 100) || !ValidText(value.RevisionNumber, 100) || value.Mode is not ("edit" or "release"))
            throw new ConnectorProtocolException("connector_envelope_invalid", "The connector document, revision, or mode is invalid.");
        if (value.SourceSize <= 0 || value.SourceSize > 100L * 1024 * 1024 || value.SourceSha256.Length != 64
            || !value.SourceSha256.All(Uri.IsHexDigit))
            throw new ConnectorProtocolException("connector_envelope_invalid", "The connector source evidence is invalid.");
        _ = NormalizeOrigin(value.Origin, allowInsecureLoopback: true);
        if (checkExpiry && (value.ExpiresAt <= now || value.ExpiresAt > now.AddMinutes(10)))
            throw new ConnectorProtocolException("connector_envelope_expired", "The connector launch envelope is expired or has an invalid lifetime.");
    }

    private static bool ValidToken(string? value, int maximum) => ValidText(value, maximum) && value!.All(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.');
    private static bool ValidText(string? value, int maximum) => !string.IsNullOrWhiteSpace(value) && value.Length <= maximum && value.All(c => !char.IsControl(c));
    private static string Base64Url(ReadOnlySpan<byte> value) => Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static byte[] FromBase64Url(string value)
    {
        if (value.Any(c => !(char.IsLetterOrDigit(c) || c is '-' or '_'))) throw new FormatException();
        var padded = value.Replace('-', '+').Replace('_', '/');
        padded += (padded.Length % 4) switch { 2 => "==", 3 => "=", 0 => "", _ => throw new FormatException() };
        return Convert.FromBase64String(padded);
    }
}

public sealed class ConnectorProtocolException(string code, string message, Exception? inner = null) : Exception(message, inner)
{
    public string Code { get; } = code;
}
