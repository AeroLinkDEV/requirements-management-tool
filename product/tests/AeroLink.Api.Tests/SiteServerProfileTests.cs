using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AeroLink.Api.Tests;

/// <summary>
/// #1614 1.1/1.2: the site server profile - Production, HTTPS from Kestrel, __Host- cookies - and what it refuses.
///
/// The hosted tests run on TestServer, so they prove the composition (refusals, host filtering, HSTS, cookie names
/// and every reader of them, derived origins); the Kestrel listener itself is proved by the manual run recorded on
/// the pull request.
/// </summary>
public sealed class SiteServerProfileTests : IDisposable
{
    private const string PublicHost = "aerolink-site.example.test";
    private const string Origin = "https://aerolink-site.example.test:5443";
    private readonly string _directory = Directory.CreateTempSubdirectory("aerolink-site-cert-").FullName;
    private readonly (string Cert, string Key) _pem;

    public SiteServerProfileTests() => _pem = WritePem(PublicHost, "site");

    public void Dispose()
    {
        try { Directory.Delete(_directory, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private Dictionary<string, string?> ValidSite() => new()
    {
        ["SiteServer:Enabled"] = "true",
        ["SiteServer:PublicHost"] = PublicHost,
        ["SiteServer:BindAddress"] = "127.0.0.1",
        ["SiteServer:Port"] = "5443",
        ["SiteServer:Certificate:Path"] = _pem.Cert,
        ["SiteServer:Certificate:KeyPath"] = _pem.Key,
        ["Identity:CookieSecure"] = "true",
    };

    private static WebApplicationFactoryWith Host(Dictionary<string, string?> settings, string? environment = null)
        => new(settings, environment);

    public static TheoryData<string, string?, string, string> Refusals => new()
    {
        // key, value, environment ("" = Production), expected fragment
        { "SiteServer:Certificate:Path", "", "", "HTTPS is not configured" },
        { "SiteServer:Certificate:Thumbprint", "00112233445566778899AABBCCDDEEFF00112233", "", @"LocalMachine\My" },
        { "Identity:AllowDemoAccounts", "true", "", "Identity:AllowDemoAccounts is true" },
        { "Identity:SeedDemoAccounts", "true", "", "Identity:SeedDemoAccounts is true" },
        { "DemoData:Enabled", "true", "", "DemoData:Enabled is true" },
        { "Identity:CookieSecure", "false", "", "Identity:CookieSecure is false" },
        { "Integrations:AllowInsecureWebhookTargets", "true", "", "AllowInsecureWebhookTargets is true" },
        { "SiteServer:PublicHost", "aerolink", "", "must be the full host name" },
        { "SiteServer:Port", "", "", "SiteServer:Port must be set" },
        { "SiteServer:BindAddress", "", "", "SiteServer:BindAddress is not set" },
        { "Notifications:BaseUrl", "https://elsewhere.example.test", "", "HTTPS origin is 'https://aerolink-site.example.test:5443'" },
        { "Connector:PublicOrigin", "http://aerolink-site.example.test:5443", "", "HTTPS origin" },
        { "Identity:BootstrapSecret", "", "", "no accounts and Identity__BootstrapSecret is not set" },
        { "SiteServer:Enabled", "true", "Development", "runs as Production" },
    };

    [Theory]
    [MemberData(nameof(Refusals))]
    public void A_site_server_refuses_to_start_unless_it_is_a_secure_production_server(string key, string? value, string environment, string expected)
    {
        var settings = ValidSite();
        // A certificate row replaces the valid PEM source rather than adding a second source beside it.
        if (key.StartsWith("SiteServer:Certificate:", StringComparison.Ordinal))
            settings["SiteServer:Certificate:Path"] = settings["SiteServer:Certificate:KeyPath"] = "";
        settings[key] = value;
        using var factory = Host(settings, environment.Length == 0 ? null : environment);
        AssertRefused(factory, expected);
    }

    [Fact]
    public void A_site_server_refuses_a_certificate_for_another_name()
    {
        var other = WritePem("another-app.example.test", "other");
        var settings = ValidSite();
        settings["SiteServer:Certificate:Path"] = other.Cert;
        settings["SiteServer:Certificate:KeyPath"] = other.Key;
        using var factory = Host(settings);
        AssertRefused(factory, $"does not cover '{PublicHost}'");
    }

    [Theory]
    [InlineData("http://0.0.0.0:5080", true)]
    [InlineData("http://*:5080", true)]
    [InlineData("http://127.0.0.1:5080;http://aerolink.example.test:5080", true)]
    [InlineData("http://127.0.0.1:5080", false)]
    [InlineData("http://[::1]:5080", false)]
    public void Production_never_serves_the_network_over_plain_http(string urls, bool refused)
    {
        using var factory = Host(new() { ["urls"] = urls });
        if (refused) AssertRefused(factory, "plain HTTP only on loopback");
        else factory.CreateClient().Dispose();
    }

    [Fact]
    public async Task A_site_server_serves_its_public_name_over_hsts_with_host_prefixed_cookies_every_reader_understands()
    {
        var settings = ValidSite();
        settings["Notifications:UnsubscribeSecret"] = new string('s', 48);
        using var factory = Host(settings);
        using var client = factory.CreateClient(new() { BaseAddress = new Uri(Origin), HandleCookies = true });

        using var health = await client.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        Assert.Equal("max-age=31536000", Assert.Single(health.Headers.GetValues("Strict-Transport-Security")));

        // AllowedHosts is the public name plus loopback: another name on the same address is refused.
        using (var stranger = factory.CreateClient(new() { BaseAddress = new Uri("https://another-app.example.test:5443") }))
            Assert.Equal(HttpStatusCode.BadRequest, (await stranger.GetAsync("/health")).StatusCode);

        // Mail links and the Word connector's trusted origin are the HTTPS origin, derived when unset.
        var configuration = factory.Services.GetRequiredService<IConfiguration>();
        Assert.Equal(Origin, configuration["Notifications:BaseUrl"]);
        Assert.Equal(Origin, configuration["Connector:PublicOrigin"]);

        using (var bootstrap = new HttpRequestMessage(HttpMethod.Post, "/api/setup/bootstrap")
        { Content = JsonContent.Create(new { displayName = "Administrator", email = "admin@example.test", password = AeroLinkApiFactory.AdministratorPassword }) })
        {
            bootstrap.Headers.Add("X-AeroLink-Bootstrap-Secret", AeroLinkApiFactory.BootstrapSecret);
            Assert.Equal(HttpStatusCode.Created, (await client.SendAsync(bootstrap)).StatusCode);
        }
        using var login = await client.PostAsJsonAsync("/api/auth/login", new { userName = "admin", password = AeroLinkApiFactory.AdministratorPassword });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var issued = Assert.Single(login.Headers.GetValues("Set-Cookie"));
        Assert.StartsWith("__Host-aerolink_session=", issued);
        Assert.Contains("secure", issued, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("path=/", issued, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("domain=", issued, StringComparison.OrdinalIgnoreCase);

        // The session gate, the session list and the CSRF issuer and validator all read the prefixed name.
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/auth/me")).StatusCode);
        var sessions = await client.GetFromJsonAsync<List<SessionRow>>("/api/auth/sessions");
        Assert.True(Assert.Single(sessions!).Current);
        var csrf = await client.GetFromJsonAsync<CsrfToken>("/api/auth/csrf");
        using var logout = new HttpRequestMessage(HttpMethod.Post, "/api/auth/logout");
        logout.Headers.Add("Origin", Origin);
        logout.Headers.Add("X-AeroLink-CSRF", csrf!.Token);
        using var signedOut = await client.SendAsync(logout);
        Assert.Equal(HttpStatusCode.NoContent, signedOut.StatusCode);

        // A browser ignores a __Host- deletion that lacks Secure and Path=/, which would leave the session behind.
        var cleared = Assert.Single(signedOut.Headers.GetValues("Set-Cookie"));
        Assert.StartsWith("__Host-aerolink_session=;", cleared);
        Assert.Contains("secure", cleared, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("path=/", cleared, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/auth/me")).StatusCode);

        // The other AeroLink cookie, the email-preference confirmation's antiforgery cookie, is prefixed as well.
        string token;
        using (var scope = factory.Services.CreateScope())
            token = scope.ServiceProvider.GetRequiredService<AeroLink.Infrastructure.Notifications.UnsubscribeTokenService>().Issue("admin")!;
        using var preference = await client.GetAsync($"/api/notifications/unsubscribe?recipient=admin&token={token}");
        Assert.Equal(HttpStatusCode.OK, preference.StatusCode);
        var antiforgery = Assert.Single(preference.Headers.GetValues("Set-Cookie"));
        Assert.StartsWith("__Host-AeroLink.PreferenceConfirmation=", antiforgery);
        Assert.Contains("secure", antiforgery, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("path=/", antiforgery, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Status_code_pages_never_rewrite_a_401()
    {
        // Windows sign-in (#1615) is an empty-bodied 401 challenge; error-page middleware must leave it alone.
        // Added outermost, where a startup filter or an early UseStatusCodePages would put it.
        using var factory = new AeroLinkApiFactory().WithWebHostBuilder(builder =>
            builder.ConfigureServices(services => services.AddTransient<IStartupFilter, StatusCodePagesFirst>()));
        using var client = factory.CreateClient();

        using var response = await client.PostAsync("/api/document-connector/redeem/not-a-launch-token", null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("", await response.Content.ReadAsStringAsync());
        Assert.Null(response.Content.Headers.ContentType);
    }

    private sealed class StatusCodePagesFirst : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.UseStatusCodePages();
            next(app);
        };
    }

    private sealed record SessionRow(bool Current);
    private sealed record CsrfToken(string Token);

    private static void AssertRefused(WebApplicationFactoryWith factory, string expected)
    {
        var problem = Record.Exception(() => factory.CreateClient().Dispose());
        Assert.NotNull(problem);
        var messages = string.Join(" | ", Chain(problem));
        Assert.Contains(expected, messages);
    }

    private static IEnumerable<string> Chain(Exception? exception)
    {
        for (; exception is not null; exception = exception.InnerException)
        {
            yield return exception.Message;
            if (exception is AggregateException aggregate)
                foreach (var inner in aggregate.InnerExceptions.SelectMany(Chain)) yield return inner;
        }
    }

    private (string Cert, string Key) WritePem(string dnsName, string name)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest($"CN={dnsName}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName(dnsName);
        request.CertificateExtensions.Add(san.Build());
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
        var cert = Path.Combine(_directory, name + ".crt");
        var keyPath = Path.Combine(_directory, name + ".key");
        File.WriteAllText(cert, certificate.ExportCertificatePem());
        File.WriteAllText(keyPath, key.ExportPkcs8PrivateKeyPem());
        return (cert, keyPath);
    }

    /// <summary>The ordinary API test host with extra settings layered on top, and optionally another environment.</summary>
    private sealed class WebApplicationFactoryWith : IDisposable
    {
        private readonly AeroLinkApiFactory _inner = new();
        private readonly Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> _factory;

        public WebApplicationFactoryWith(Dictionary<string, string?> settings, string? environment)
            => _factory = _inner.WithWebHostBuilder(builder =>
            {
                if (environment is not null) builder.UseEnvironment(environment);
                // Both layers. The site profile is decided before the host is built, which only host settings reach;
                // the base factory's own app configuration (its bootstrap secret, for one) lands after them, so the
                // same values are layered on top of it too.
                foreach (var (key, value) in settings) builder.UseSetting(key, value);
                builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(settings));
            });

        public IServiceProvider Services => _factory.Services;
        public HttpClient CreateClient() => _factory.CreateClient();
        public HttpClient CreateClient(Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions options) => _factory.CreateClient(options);

        public void Dispose()
        {
            _factory.Dispose();
            _inner.Dispose();
        }
    }
}
