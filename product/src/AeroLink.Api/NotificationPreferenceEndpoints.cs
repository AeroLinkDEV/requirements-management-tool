using System.Net;
using System.Security.Cryptography;
using AeroLink.Domain.Notifications;
using AeroLink.Infrastructure.Notifications;
using AeroLink.Infrastructure.Persistence;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace AeroLink.Api;

internal static class NotificationPreferenceEndpoints
{
    private const string Answer = "If that link and confirmation were valid, email notification is off for that account. Sign in to AeroLink to turn it back on.";
    private static ITimeLimitedDataProtector Protector(IDataProtectionProvider provider)
        => provider.CreateProtector("AeroLink.NotificationPreferenceConfirmation.v1").ToTimeLimitedDataProtector();
    private static string CapabilityHash(string recipient, string token)
        => NotificationInstallationAuthority.Hash(recipient.Trim().ToLowerInvariant() + "\n" + token.Trim().ToLowerInvariant());
    private static void Headers(HttpContext http)
    {
        http.Response.Headers.CacheControl = "no-store";
        http.Response.Headers["Referrer-Policy"] = "no-referrer";
        http.Response.Headers["Content-Security-Policy"] = "default-src 'none'; form-action 'self'; frame-ancestors 'none'; base-uri 'none'";
        http.Response.Headers["X-Content-Type-Options"] = "nosniff";
    }
    internal static void MapNotificationPreferenceEndpoints(this WebApplication app)
    {
        app.MapMethods("/api/notifications/unsubscribe", ["GET", "HEAD"], (HttpContext http,
            string? recipient, string? token, UnsubscribeTokenService capabilities, IAntiforgery antiforgery, IDataProtectionProvider provider) =>
        {
            Headers(http);
            // Mail scanners, previews and HEAD requests have no persistence dependency or mutation.
            var valid = recipient is { Length: <= 100 } && token is { Length: <= 128 } && capabilities.Validate(recipient, token);
            string hidden = "";
            if (valid)
            {
                var challenge = Protector(provider).Protect(Guid.NewGuid().ToString("N") + ":" + CapabilityHash(recipient!, token!), TimeSpan.FromMinutes(30));
                var anti = antiforgery.GetAndStoreTokens(http);
                string Encode(string value) => WebUtility.HtmlEncode(value);
                hidden = $"<input type=\"hidden\" name=\"recipient\" value=\"{Encode(recipient!)}\"><input type=\"hidden\" name=\"token\" value=\"{Encode(token!)}\"><input type=\"hidden\" name=\"challenge\" value=\"{Encode(challenge)}\"><input type=\"hidden\" name=\"{Encode(anti.FormFieldName)}\" value=\"{Encode(anti.RequestToken!)}\">";
            }
            return Results.Content($"<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><title>AeroLink email preference</title></head><body><h1>Confirm email preference</h1><p>Opening this page does not change any preference.</p><form method=\"post\" action=\"/api/notifications/unsubscribe\">{hidden}<button type=\"submit\">Turn off email notifications</button></form><p>Sign in to AeroLink to manage your email preferences.</p></body></html>", "text/html");
        }).AllowAnonymous();
        app.MapPost("/api/notifications/unsubscribe", async (HttpContext http, AeroLinkDbContext db,
            UnsubscribeTokenService capabilities, IAntiforgery antiforgery, IDataProtectionProvider provider, CancellationToken ct) =>
        {
            Headers(http);
            var origin = http.Request.Headers.Origin.ToString();
            if (origin != $"{http.Request.Scheme}://{http.Request.Host}" || !http.Request.HasFormContentType
                || !await antiforgery.IsRequestValidAsync(http)) return Results.Text(Answer);
            var form = await http.Request.ReadFormAsync(ct);
            var recipient = form["recipient"].ToString(); var token = form["token"].ToString(); var challenge = form["challenge"].ToString();
            if (recipient.Length is 0 or > 100 || token.Length is 0 or > 128 || challenge.Length is 0 or > 2048
                || !capabilities.Validate(recipient, token)) return Results.Text(Answer);
            try
            {
                var payload = Protector(provider).Unprotect(challenge);
                if (payload.Length != 97 || payload[32] != ':' || !Guid.TryParseExact(payload[..32], "N", out _)
                    || payload[33..] != CapabilityHash(recipient, token)) return Results.Text(Answer);
            }
            catch (CryptographicException) { return Results.Text(Answer); }
            var hash = NotificationInstallationAuthority.Hash(challenge);
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            if (await db.NotificationPreferenceConfirmations.AnyAsync(x => x.ChallengeHash == hash, ct)) return Results.Text(Answer);
            var name = recipient.Trim().ToLowerInvariant(); var now = DateTimeOffset.UtcNow;
            var preference = await db.NotificationPreferences.SingleOrDefaultAsync(x => x.Recipient == name, ct);
            if (preference is null) { preference = new NotificationPreference(name, now); db.NotificationPreferences.Add(preference); }
            preference.SetEmailEnabled(false, now);
            db.NotificationPreferenceConfirmations.Add(new(hash, now));
            db.SecurityAuditEvents.Add(new("NotificationEmailDisabled", name, name, "Success", "Email notification turned off by explicit preference confirmation.", "local", now));
            try { await db.SaveChangesAsync(ct); await transaction.CommitAsync(ct); }
            catch (DbUpdateException) { await transaction.RollbackAsync(ct); }
            return Results.Text(Answer);
        }).AllowAnonymous();
    }
}