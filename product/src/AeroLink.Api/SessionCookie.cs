namespace AeroLink.Api;

/// <summary>
/// The one place that knows what AeroLink's sign-in cookie is called and how it is written.
///
/// A site server shares its full host name with other intranet applications and is told apart from them only by
/// port (#1614 D3). Browsers scope cookies by host name, not port (RFC 6265 section 8.5), so those applications
/// receive this cookie with every request and, being HTTPS on the same host, can overwrite it; nothing a cookie
/// name or prefix does changes that. The owner accepted this risk on 15 Oct 2026 (#1614 D3); a dedicated DNS
/// alias for AeroLink would remove it. What a secure deployment's
/// <c>__Host-</c> prefix does give: the browser accepts the cookie only when it is Secure, has Path=/ and names no
/// Domain, so a plain-HTTP origin, a parent or sibling host's <c>Domain=</c> cookie, or a path-scoped cookie cannot
/// plant or shadow it. The distinct name avoids accidental collisions with the neighbours' own cookies.
///
/// A non-secure development launch (HOME, the work laptop, the API tests) keeps the historical name, because a
/// browser refuses a <c>__Host-</c> cookie that is not Secure, which is every cookie on plain HTTP.
///
/// Every reader goes through <see cref="Read"/>. A reader that kept the old literal would see no session on a
/// site server and answer a signed-in person with 401, which is why the old public constant is gone.
/// </summary>
public sealed class SessionCookie
{
    public const string BaseName = "aerolink_session";
    public const string SecurePrefix = "__Host-";

    public SessionCookie(bool secure)
    {
        Secure = secure;
        Name = secure ? SecurePrefix + BaseName : BaseName;
    }

    public bool Secure { get; }
    public string Name { get; }

    /// <summary>
    /// Whether cookies are written Secure: <c>Identity:CookieSecure</c> when set, otherwise everywhere except
    /// Development. The same rule the sign-in endpoint has always applied, now decided once for every cookie.
    /// </summary>
    public static bool SecureFrom(IConfiguration configuration, IHostEnvironment environment)
        => configuration.GetValue<bool?>("Identity:CookieSecure") ?? !environment.IsDevelopment();

    public string? Read(HttpContext context) => context.Request.Cookies[Name];

    public void Issue(HttpResponse response, string token, DateTimeOffset expiresAt)
        => response.Cookies.Append(Name, token, Options(expiresAt));

    /// <summary>
    /// Deleting a <c>__Host-</c> cookie needs the same Secure and Path attributes as setting it: a browser
    /// ignores a Set-Cookie for that prefix without them, and sign-out would leave the session cookie behind.
    /// </summary>
    public void Clear(HttpResponse response) => response.Cookies.Delete(Name, Options(null));

    private CookieOptions Options(DateTimeOffset? expiresAt) => new()
    {
        HttpOnly = true,
        Secure = Secure,
        SameSite = SameSiteMode.Lax,
        Expires = expiresAt,
        Path = "/",
    };
}

public static class SessionCookieExtensions
{
    /// <summary>The raw session token this request carries, or null.</summary>
    public static string? SessionToken(this HttpContext context)
        => context.RequestServices.GetRequiredService<SessionCookie>().Read(context);

    public static void IssueSessionCookie(this HttpContext context, string token, DateTimeOffset expiresAt)
        => context.RequestServices.GetRequiredService<SessionCookie>().Issue(context.Response, token, expiresAt);

    public static void ClearSessionCookie(this HttpContext context)
        => context.RequestServices.GetRequiredService<SessionCookie>().Clear(context.Response);
}
