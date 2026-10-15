using System.Security.Claims;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using AeroLink.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Negotiate;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;

namespace AeroLink.Api;

/// <summary>
/// Windows sign-in settings (#1615), under <c>Identity:Windows</c>.
///
/// <c>AllowedDomainSids</c> lists the domains whose users may sign in, by domain SID (<c>S-1-5-21-a-b-c</c>,
/// the user's SID without its last part). <c>Scheme</c> names the authentication scheme that proves who the
/// browser is; it is Negotiate in every real deployment, and Production refuses to start with anything else.
/// </summary>
public sealed class WindowsSignInOptions
{
    public const string Section = "Identity:Windows";
    public bool Enabled { get; set; }
    public string[] AllowedDomainSids { get; set; } = [];
    public string Scheme { get; set; } = NegotiateDefaults.AuthenticationScheme;
}

/// <summary>
/// Signing in with the Windows logon the browser already holds (#1615): Negotiate (Kerberos, with NTLM
/// accepted as fallback) proves the person, the SID decides whether they may sign in at all, the directory
/// supplies their name and email, and an ordinary AeroLink session cookie is issued — the same one a password
/// sign-in issues, so nothing downstream of sign-in knows or cares which method was used.
///
/// Both routes are on the session gate's anonymous list in Program.cs; there is no session yet when they run.
/// </summary>
public static class WindowsSignInEndpoints
{
    public const string SignInPath = "/api/auth/windows";
    public const string LinkPath = "/api/auth/windows/link";
    private const string LinkCookie = "AeroLink.WindowsLink";
    private const string LinkPurpose = "AeroLink.WindowsLink.v1";
    private static readonly TimeSpan LinkLifetime = TimeSpan.FromMinutes(5);

    public static void MapWindowsSignInEndpoints(this WebApplication app)
    {
        app.MapGet(SignInPath, async (HttpContext http, string? returnTo, string? intent, IOptions<WindowsSignInOptions> options,
            IdentityService identity, IDataProtectionProvider dataProtection, CancellationToken ct) =>
        {
            var settings = options.Value;
            if (!settings.Enabled) return Results.NotFound();
            // The challenge is an empty 401 carrying WWW-Authenticate. Error-page middleware that rewrote it would
            // break the handshake, so it is switched off for this request before anything can produce a 401.
            if (http.Features.Get<Microsoft.AspNetCore.Diagnostics.IStatusCodePagesFeature>() is { } statusCodePages)
                statusCodePages.Enabled = false;

            var authenticated = await http.AuthenticateAsync(settings.Scheme);
            if (!authenticated.Succeeded || authenticated.Principal?.Identity?.IsAuthenticated != true)
            {
                await http.ChallengeAsync(settings.Scheme);
                return Results.Empty;
            }

            var ip = http.Connection.RemoteIpAddress?.ToString() ?? "local";
            var now = DateTimeOffset.UtcNow;
            var sid = authenticated.Principal.FindFirst(ClaimTypes.PrimarySid)?.Value ?? "";
            var principal = authenticated.Principal.Identity.Name ?? "";
            var claimed = new WindowsDirectoryIdentity(sid, principal, principal, "");
            if (sid.Length == 0 || principal.Length == 0)
                return Refused(await identity.RefuseWindowsAsync(claimed, "no_identity", "Windows did not supply a SID and account name.", ip, now, ct));
            if (WindowsAccountPolicy.Refusal(sid, settings.AllowedDomainSids) is { } policyRefusal)
                return Refused(await identity.RefuseWindowsAsync(claimed, policyRefusal,
                    policyRefusal == WindowsAccountPolicy.ForeignDomain ? "The account's domain is not allowed to sign in." : "Local, built-in and well-known accounts cannot sign in.", ip, now, ct));

            var directory = http.RequestServices.GetService<IWindowsDirectory>();
            var domain = principal.Contains('\\') ? principal[..principal.IndexOf('\\')] : "";
            WindowsDirectoryEntry? entry;
            try
            {
                entry = directory is null || domain.Length == 0 ? null : await directory.FindBySidAsync(domain, sid, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return Refused(await identity.RefuseWindowsAsync(claimed, "directory_unavailable", "The directory could not be reached.", ip, now, ct));
            }
            if (entry is null)
                return Refused(await identity.RefuseWindowsAsync(claimed, "not_in_directory", "The directory has no account for this SID.", ip, now, ct));
            if (string.IsNullOrWhiteSpace(entry.Email))
                return Refused(await identity.RefuseWindowsAsync(claimed, "no_email", "The directory account has no email address.", ip, now, ct));
            var person = new WindowsDirectoryIdentity(sid, principal, string.IsNullOrWhiteSpace(entry.DisplayName) ? principal : entry.DisplayName.Trim(), entry.Email.Trim());

            if (string.Equals(intent, "link", StringComparison.OrdinalIgnoreCase))
            {
                // Linking needs the password of the account being linked, which the next page asks for. Until then
                // the proven Windows identity is held in a short-lived, encrypted, this-site-only cookie.
                var ticket = dataProtection.CreateProtector(LinkPurpose).ToTimeLimitedDataProtector()
                    .Protect(JsonSerializer.Serialize(person), LinkLifetime);
                http.Response.Cookies.Append(LinkCookieName(http), ticket, LinkCookieOptions(http, now.Add(LinkLifetime)));
                return Results.Redirect("/?windows=link");
            }

            var outcome = await identity.SignInWindowsAsync(person, ip, http.Request.Headers.UserAgent.ToString(), now, ct);
            if (outcome.Session is null) return Refused(outcome);
            SessionCookies.Append(http, outcome.Session);
            return Results.Redirect(SafeReturnTo(returnTo));
        }).RequireRateLimiting("authentication");

        app.MapPost(LinkPath, async (WindowsLinkRequest request, HttpContext http, IOptions<WindowsSignInOptions> options,
            IdentityService identity, IDataProtectionProvider dataProtection, CancellationToken ct) =>
        {
            if (!options.Value.Enabled) return Results.NotFound();
            WindowsDirectoryIdentity? person = null;
            try
            {
                var ticket = http.Request.Cookies[LinkCookieName(http)];
                if (!string.IsNullOrEmpty(ticket))
                    person = JsonSerializer.Deserialize<WindowsDirectoryIdentity>(
                        dataProtection.CreateProtector(LinkPurpose).ToTimeLimitedDataProtector().Unprotect(ticket));
            }
            catch (Exception ex) when (ex is CryptographicException or JsonException) { person = null; }
            if (person is null)
                return Results.Json(new { error = "Sign in with Windows again to link this account; the Windows sign-in has expired.", code = "link_expired" }, statusCode: StatusCodes.Status400BadRequest);

            var outcome = await identity.LinkWindowsAsync(person, request.UserName ?? "", request.Password ?? "", request.MfaCode,
                http.Connection.RemoteIpAddress?.ToString() ?? "local", http.Request.Headers.UserAgent.ToString(), DateTimeOffset.UtcNow, ct);
            if (outcome.Session is null)
                return outcome.RefusalCode == "credentials"
                    ? Results.Json(new { error = "The credentials or second-factor code were not accepted.", code = "credentials" }, statusCode: StatusCodes.Status401Unauthorized)
                    : Results.Json(new { error = RefusalMessage(outcome.RefusalCode), code = outcome.RefusalCode }, statusCode: StatusCodes.Status409Conflict);
            http.Response.Cookies.Delete(LinkCookieName(http), LinkCookieOptions(http, null));
            SessionCookies.Append(http, outcome.Session);
            return Results.Ok(outcome.Session.User);
        }).RequireRateLimiting("authentication");
    }

    private static IResult Refused(WindowsSignInOutcome outcome) =>
        Results.Redirect($"/?windows=refused&code={Uri.EscapeDataString(outcome.RefusalCode ?? "refused")}");

    private static string RefusalMessage(string? code) => code switch
    {
        "account_already_linked" => "That AeroLink account is already linked to a different Windows logon.",
        "windows_already_linked" => "This Windows logon already signs in to another AeroLink account. An administrator must disable that account first.",
        _ => "The link was refused.",
    };

    /// <summary>Only a path on this site: never another origin, and never a protocol-relative address.</summary>
    internal static string SafeReturnTo(string? returnTo) =>
        returnTo is { Length: > 0 } value && value[0] == '/' && !value.StartsWith("//", StringComparison.Ordinal)
            && !value.StartsWith("/\\", StringComparison.Ordinal) && !value.Any(char.IsControl)
            ? value : "/";

    private static string LinkCookieName(HttpContext http) => SessionCookies.Secure(http) ? "__Host-" + LinkCookie : LinkCookie;

    private static CookieOptions LinkCookieOptions(HttpContext http, DateTimeOffset? expires) => new()
    {
        HttpOnly = true,
        Secure = SessionCookies.Secure(http),
        // Strict: a link form posted from another site cannot carry the proven Windows identity with it.
        SameSite = SameSiteMode.Strict,
        Path = "/",
        Expires = expires,
    };
}

public sealed record WindowsLinkRequest(string? UserName, string? Password, string? MfaCode);

/// <summary>
/// Refuses, at startup, a Production configuration under which Windows sign-in would be weaker than it
/// claims (#1615): an authentication scheme other than Negotiate (which is how a test scheme stays out of a
/// real deployment), no allowed domain, the service running as SYSTEM, or a listener that may speak HTTP/2,
/// which Negotiate cannot use.
/// </summary>
public static class WindowsSignInStartupGuard
{
    public static async Task EnforceAsync(IServiceProvider services, IConfiguration configuration, IHostEnvironment environment)
    {
        var settings = services.GetRequiredService<IOptions<WindowsSignInOptions>>().Value;
        if (!settings.Enabled || !environment.IsProduction()) return;
        if (!string.Equals(settings.Scheme, NegotiateDefaults.AuthenticationScheme, StringComparison.Ordinal))
            throw new InvalidOperationException($"Windows sign-in in Production must use the Negotiate scheme, not '{settings.Scheme}'.");
        foreach (var scheme in await services.GetRequiredService<IAuthenticationSchemeProvider>().GetAllSchemesAsync())
            if (scheme.Name != AeroLinkAuthorizationHandler.SchemeName && scheme.HandlerType != typeof(NegotiateHandler))
                throw new InvalidOperationException($"Production refuses the authentication scheme '{scheme.Name}' alongside Windows sign-in.");
        if (settings.AllowedDomainSids.Length == 0
            || settings.AllowedDomainSids.Any(x => WindowsAccountPolicy.DomainSid(x.Trim() + "-1000", out _) is null))
            throw new InvalidOperationException("Windows sign-in requires Identity:Windows:AllowedDomainSids to list at least one domain SID (S-1-5-21-a-b-c).");
        if (OperatingSystem.IsWindows() && RunsAsLocalSystem())
            throw new InvalidOperationException("Windows sign-in refuses to run as SYSTEM. Run AeroLink as NETWORK SERVICE (#1614).");
        // SiteServer:Http1Only is the site server profile's switch (#1614, parallel HTTPS work); the Kestrel key
        // covers a listener configured directly.
        var http1Only = configuration.GetValue<bool>("SiteServer:Http1Only")
            || string.Equals(configuration["Kestrel:EndpointDefaults:Protocols"], "Http1", StringComparison.OrdinalIgnoreCase);
        if (!http1Only)
            throw new InvalidOperationException("Windows sign-in requires an HTTP/1.1-only listener. Set SiteServer:Http1Only (or Kestrel:EndpointDefaults:Protocols=Http1).");
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static bool RunsAsLocalSystem()
    {
        using var current = WindowsIdentity.GetCurrent();
        return current.User?.IsWellKnown(WellKnownSidType.LocalSystemSid) == true;
    }
}
