using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration;

namespace AeroLink.Infrastructure.Notifications;

/// <summary>
/// Signs the unsubscribe links carried in message bodies.
///
/// The opt-out endpoint has to work from a mail client, where the reader is not authenticated, so the link
/// itself must prove it was issued by this deployment. The token is an HMAC over the recipient using a
/// configured secret; without a secret no unsubscribe link is offered at all, because a guessable one would
/// let anybody silence anybody else's approval notices.
/// </summary>
public sealed class UnsubscribeTokenService(IConfiguration configuration)
{
    private string? Secret
    {
        get
        {
            var configured = configuration["Notifications:UnsubscribeSecret"];
            return string.IsNullOrWhiteSpace(configured) || configured.Trim().Length < 32 ? null : configured.Trim();
        }
    }

    public bool IsConfigured => Secret is not null;

    public string? Issue(string recipient)
    {
        var secret = Secret;
        if (secret is null || string.IsNullOrWhiteSpace(recipient)) return null;
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var signature = hmac.ComputeHash(Encoding.UTF8.GetBytes(recipient.Trim().ToLowerInvariant()));
        return Convert.ToHexStringLower(signature);
    }

    public bool Validate(string recipient, string token)
    {
        var expected = Issue(recipient);
        if (expected is null || string.IsNullOrWhiteSpace(token)) return false;
        // Constant-time comparison: a token check that leaks timing is a token check that can be guessed.
        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(token.Trim().ToLowerInvariant()));
    }
}
