using Microsoft.Extensions.Configuration;

namespace AeroLink.Infrastructure.Notifications;

/// <summary>
/// Turns a notification's stored route into a link a person can click from their mail client.
///
/// The server cannot discover its own external address: it sits behind whatever reverse proxy and host
/// name the deployment chose, and a link built from the inbound request would be wrong for anyone reached
/// through a different path. The public base address is therefore configuration, and when it is absent no
/// link is produced rather than a broken one.
/// </summary>
public sealed class NotificationLinkBuilder(IConfiguration configuration)
{
    public string? BaseUrl
    {
        get
        {
            var configured = configuration["Notifications:BaseUrl"];
            return string.IsNullOrWhiteSpace(configured) ? null : configured.TrimEnd('/');
        }
    }

    /// <summary>
    /// Maps a stored route such as <c>scr:{id}</c> to a client path. An unrecognised route resolves to the
    /// workspace root rather than to nothing, so a new notification type is a slightly vague link rather
    /// than a dead one.
    /// </summary>
    public static string PathFor(string route)
    {
        if (string.IsNullOrWhiteSpace(route)) return "/";
        var separator = route.IndexOf(':');
        if (separator <= 0) return "/";
        var kind = route[..separator].Trim().ToLowerInvariant();
        var id = route[(separator + 1)..].Trim();
        if (string.IsNullOrWhiteSpace(id)) return "/";
        return kind switch
        {
            "scr" => $"/open/scr/{id}",
            "swcr" => $"/open/swcr/{id}",
            "requirement" => $"/open/requirement/{id}",
            "procedure" => $"/open/procedure/{id}",
            "verification-impact" => "/system-verification",
            "baseline" => $"/open/baseline/{id}",
            "release" or "campaign" => "/release-readiness",
            "document" => $"/open/document/{id}",
            "problem-report" => $"/open/problem-report/{id}",
            "managed-document" => $"/open/managed-document/{id}",
            _ => "/",
        };
    }

    /// <summary>The absolute link, or null when no public address is configured.</summary>
    public string? LinkFor(string route)
    {
        var baseUrl = BaseUrl;
        return baseUrl is null ? null : baseUrl + PathFor(route);
    }

    public string? UnsubscribeLinkFor(string recipient, string token)
    {
        var baseUrl = BaseUrl;
        return baseUrl is null
            ? null
            : $"{baseUrl}/api/notifications/unsubscribe?recipient={Uri.EscapeDataString(recipient)}&token={Uri.EscapeDataString(token)}";
    }
}
