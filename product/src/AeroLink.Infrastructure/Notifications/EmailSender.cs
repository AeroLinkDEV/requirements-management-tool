using System.Net.Mail;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace AeroLink.Infrastructure.Notifications;

public sealed record EmailMessage(string To, string Subject, string PlainTextBody);

/// <summary>
/// Sending is behind an interface so the dispatcher can be tested without a mail server, and so a
/// deployment that has not configured one is a visible, inspectable state rather than a silent hole.
/// </summary>
public interface IEmailSender
{
    bool IsConfigured { get; }
    Task SendAsync(EmailMessage message, CancellationToken ct);
}

/// <summary>
/// SMTP relay delivery. AeroLink is on-premises software, so mail leaves through whatever relay the
/// organization already runs; there is no third-party mail service and no outbound call to one.
/// </summary>
public sealed class SmtpEmailSender(IConfiguration configuration, ILogger<SmtpEmailSender> logger) : IEmailSender
{
    private string? Host => Blank(configuration["Notifications:Smtp:Host"]);
    private string From => Blank(configuration["Notifications:Smtp:From"]) ?? "aerolink@localhost";
    private int Port => int.TryParse(configuration["Notifications:Smtp:Port"], out var port) ? port : 25;
    private bool UseStartTls => !string.Equals(configuration["Notifications:Smtp:UseStartTls"], "false", StringComparison.OrdinalIgnoreCase);

    public bool IsConfigured => Host is not null;

    public async Task SendAsync(EmailMessage message, CancellationToken ct)
    {
        var host = Host ?? throw new InvalidOperationException("No SMTP host is configured.");
        using var client = new SmtpClient(host, Port) { EnableSsl = UseStartTls };
        var user = Blank(configuration["Notifications:Smtp:UserName"]);
        var password = Blank(configuration["Notifications:Smtp:Password"]);
        if (user is not null && password is not null)
        {
            client.UseDefaultCredentials = false;
            client.Credentials = new System.Net.NetworkCredential(user, password);
        }
        using var mail = new MailMessage(From, message.To, message.Subject, message.PlainTextBody) { IsBodyHtml = false };
        // The address is logged; the body is not. A notification body names controlled artifacts, and the
        // mail log is not an access-controlled surface.
        logger.LogInformation("Sending AeroLink notification to {Address}", message.To);
        await client.SendMailAsync(mail, ct);
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

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
