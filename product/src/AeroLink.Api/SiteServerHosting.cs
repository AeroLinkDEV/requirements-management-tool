using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.EntityFrameworkCore;

namespace AeroLink.Api;

/// <summary>
/// A site server installation (#1614): AeroLink on a shared company host, run as Production, serving HTTPS itself.
///
/// Everything here is decided from configuration before the host is built, and every problem is a refusal to
/// start. A site server that came up anyway - on plain HTTP, with demonstration accounts, or with a certificate
/// for some other name - would look healthy to the operator and be wrong for every person who used it.
///
/// The <c>SiteServer</c> section is written by the site launcher (product/scripts/Start-AeroLinkSite.ps1) from the
/// installation's non-secret <c>site-server.json</c>:
/// <code>
/// SiteServer:Enabled               true
/// SiteServer:PublicHost            the full host name people type; never a short name or an address
/// SiteServer:BindAddress           the address Kestrel listens on, for example 0.0.0.0
/// SiteServer:Port                  this installation's own port
/// SiteServer:Http1Only             true to refuse HTTP/2 (Windows sign-in needs HTTP/1.1, #1615)
/// SiteServer:Certificate:Thumbprint | :Subject   from LocalMachine\My (or :Store), or
/// SiteServer:Certificate:Path + :KeyPath         a PEM certificate and its PEM private key
/// </code>
/// </summary>
public sealed class SiteServerSettings
{
    public required string PublicHost { get; init; }
    public required IPAddress BindAddress { get; init; }
    public required int Port { get; init; }
    public required bool Http1Only { get; init; }
    public required X509Certificate2 Certificate { get; init; }
    /// <summary>The HTTPS origin browsers use, e.g. <c>https://host.example.com:5443</c>.</summary>
    public required string PublicOrigin { get; init; }
}

public static class SiteServerHosting
{
    public const string Section = "SiteServer";

    public static bool IsEnabled(IConfiguration configuration) => configuration.GetValue<bool>($"{Section}:Enabled");

    /// <summary>
    /// Applies the site profile to the builder, or refuses. Returns null for every other kind of launch, which is
    /// then held to the Production plain-HTTP rule below and otherwise left exactly as it was.
    /// </summary>
    public static SiteServerSettings? Configure(WebApplicationBuilder builder)
    {
        var configuration = builder.Configuration;
        if (!IsEnabled(configuration))
        {
            RefuseNetworkHttpInProduction(builder.Environment, configuration);
            return null;
        }

        var problems = new List<string>();
        if (!builder.Environment.IsProduction())
            problems.Add($"a site server runs as Production; ASPNETCORE_ENVIRONMENT is '{builder.Environment.EnvironmentName}'");
        RequireProductionIdentity(configuration, builder.Environment, problems);

        var publicHost = configuration[$"{Section}:PublicHost"]?.Trim().TrimEnd('.').ToLowerInvariant() ?? "";
        if (publicHost.Length == 0)
            problems.Add("SiteServer:PublicHost is not set");
        else if (Uri.CheckHostName(publicHost) != UriHostNameType.Dns || !publicHost.Contains('.') || publicHost == "localhost")
            problems.Add($"SiteServer:PublicHost '{publicHost}' must be the full host name (never a short name, localhost or an address)");

        IPAddress? bindAddress = null;
        var rawBind = configuration[$"{Section}:BindAddress"]?.Trim();
        if (string.IsNullOrEmpty(rawBind)) problems.Add("SiteServer:BindAddress is not set");
        else if (!IPAddress.TryParse(rawBind, out bindAddress)) problems.Add($"SiteServer:BindAddress '{rawBind}' is not an IP address");

        var port = configuration.GetValue<int?>($"{Section}:Port");
        if (port is null or < 1 or > 65535) problems.Add("SiteServer:Port must be set to a port from 1 to 65535");

        X509Certificate2? certificate = null;
        if (publicHost.Length > 0)
        {
            try { certificate = LoadCertificate(configuration.GetSection($"{Section}:Certificate"), publicHost); }
            catch (InvalidOperationException ex) { problems.Add(ex.Message); }
        }

        var origin = port is null ? "" : port == 443 ? $"https://{publicHost}" : $"https://{publicHost}:{port}";
        var derived = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        if (problems.Count == 0)
        {
            // Links in notification mail and the Word connector's trusted origin are this HTTPS origin. Either
            // is derived when unset; set to anything else, it names a different server and is refused.
            foreach (var key in new[] { "Notifications:BaseUrl", "Connector:PublicOrigin" })
            {
                var configured = configuration[key]?.Trim().TrimEnd('/');
                if (string.IsNullOrEmpty(configured)) derived[key] = origin;
                else if (!string.Equals(configured, origin, StringComparison.OrdinalIgnoreCase))
                    problems.Add($"{key} is '{configured}', but this site server's HTTPS origin is '{origin}'");
            }
            // The public name, plus loopback for probes on the host itself. Never "*".
            derived["AllowedHosts"] = $"{publicHost};localhost;127.0.0.1;[::1]";
        }

        if (problems.Count > 0)
        {
            certificate?.Dispose();
            throw new InvalidOperationException("AeroLink site server refused to start:" + Environment.NewLine
                + string.Join(Environment.NewLine, problems.Select(x => "  - " + x)));
        }

        configuration.AddInMemoryCollection(derived);
        var settings = new SiteServerSettings
        {
            PublicHost = publicHost,
            BindAddress = bindAddress!,
            Port = port!.Value,
            Http1Only = configuration.GetValue<bool>($"{Section}:Http1Only"),
            Certificate = certificate!,
            PublicOrigin = origin,
        };
        builder.Services.AddSingleton(settings);
        // An explicit endpoint replaces ASPNETCORE_URLS and --urls: a site server listens on its HTTPS port only.
        builder.WebHost.ConfigureKestrel(kestrel => kestrel.Listen(settings.BindAddress, settings.Port, listen =>
        {
            listen.Protocols = settings.Http1Only ? HttpProtocols.Http1 : HttpProtocols.Http1AndHttp2;
            listen.UseHttps(settings.Certificate);
        }));
        return settings;
    }

    /// <summary>
    /// The first administrator has to come from somewhere. With no account in the database and no bootstrap
    /// secret, nobody can ever sign in to a site server, and demonstration accounts are refused - so refuse now,
    /// rather than serve a sign-in page nobody can pass.
    /// </summary>
    public static async Task RequireAdministratorPathAsync(IConfiguration configuration, AeroLink.Infrastructure.Persistence.AeroLinkDbContext db)
    {
        if (AuthEndpoints.BootstrapSecret(configuration) is not null) return;
        if (await db.UserAccounts.AnyAsync()) return;
        throw new InvalidOperationException("AeroLink site server refused to start:" + Environment.NewLine
            + "  - the database has no accounts and Identity__BootstrapSecret is not set (32 characters or more), so no administrator could ever sign in");
    }

    private static void RequireProductionIdentity(IConfiguration configuration, IHostEnvironment environment, List<string> problems)
    {
        if (configuration.GetValue<bool>("DemoData:Enabled")) problems.Add("DemoData:Enabled is true");
        if (configuration.GetValue<bool>("Identity:SeedDemoAccounts")) problems.Add("Identity:SeedDemoAccounts is true");
        if (configuration.GetValue<bool>("Identity:AllowDemoAccounts")) problems.Add("Identity:AllowDemoAccounts is true");
        if (!SessionCookie.SecureFrom(configuration, environment)) problems.Add("Identity:CookieSecure is false");
        if (configuration.GetValue<bool>("Integrations:AllowInsecureWebhookTargets")) problems.Add("Integrations:AllowInsecureWebhookTargets is true");
        if (configuration.GetValue<bool>("RestoreValidation:ReadOnly")) problems.Add("RestoreValidation:ReadOnly is a qualification host, not a site server");
    }

    /// <summary>
    /// Production never serves the network over plain HTTP. Loopback stays allowed: CI, restore validation and the
    /// HOME acceptance run the Production profile on 127.0.0.1, and nothing off the machine can reach them.
    /// </summary>
    private static void RefuseNetworkHttpInProduction(IHostEnvironment environment, IConfiguration configuration)
    {
        if (!environment.IsProduction()) return;
        var urls = (configuration[WebHostDefaults.ServerUrlsKey] ?? "")
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Concat(configuration.GetSection("Kestrel:Endpoints").GetChildren().Select(x => x["Url"] ?? ""))
            .Where(x => x.StartsWith("http://", StringComparison.OrdinalIgnoreCase));
        var exposed = urls.Where(x => !IsLoopbackUrl(x)).ToArray();
        if (exposed.Length > 0)
            throw new InvalidOperationException(
                $"AeroLink refused to start: Production may listen on plain HTTP only on loopback, and it was asked to listen on {string.Join(", ", exposed)}. " +
                "Serve the network over HTTPS with the site server profile (SiteServer:Enabled).");
    }

    private static bool IsLoopbackUrl(string url)
    {
        // Kestrel's own wildcard forms are not URIs: http://*:5080 and http://+:5080 bind every address.
        var rest = url["http://".Length..];
        var host = rest.StartsWith('[') ? rest[..(rest.IndexOf(']') + 1)] : rest.Split(':', '/')[0];
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase)) return true;
        return IPAddress.TryParse(host.Trim('[', ']'), out var address) && IPAddress.IsLoopback(address);
    }

    private static X509Certificate2 LoadCertificate(IConfigurationSection section, string publicHost)
    {
        var thumbprint = section["Thumbprint"]?.Replace(" ", "").Trim();
        var subject = section["Subject"]?.Trim();
        var path = section["Path"]?.Trim();
        var keyPath = section["KeyPath"]?.Trim();
        var sources = new[] { !string.IsNullOrEmpty(thumbprint), !string.IsNullOrEmpty(subject), !string.IsNullOrEmpty(path) || !string.IsNullOrEmpty(keyPath) }.Count(x => x);
        if (sources != 1)
            throw new InvalidOperationException("HTTPS is not configured: set exactly one of SiteServer:Certificate:Thumbprint, SiteServer:Certificate:Subject, or SiteServer:Certificate:Path with :KeyPath");

        X509Certificate2 certificate;
        if (!string.IsNullOrEmpty(path) || !string.IsNullOrEmpty(keyPath))
            certificate = LoadPem(path, keyPath);
        else
            certificate = LoadFromMachineStore(section["Store"], thumbprint, subject, publicHost);

        var now = DateTime.Now;
        string? problem = null;
        if (!certificate.HasPrivateKey) problem = $"certificate {certificate.Thumbprint} has no private key this process can use";
        else if (now < certificate.NotBefore || now > certificate.NotAfter)
            problem = $"certificate {certificate.Thumbprint} is valid only from {certificate.NotBefore:u} to {certificate.NotAfter:u}";
        else if (!certificate.MatchesHostname(publicHost))
            problem = $"certificate {certificate.Thumbprint} does not cover '{publicHost}'";
        if (problem is null) return certificate;
        certificate.Dispose();
        throw new InvalidOperationException(problem);
    }

    private static X509Certificate2 LoadPem(string? path, string? keyPath)
    {
        if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(keyPath))
            throw new InvalidOperationException("a PEM certificate needs both SiteServer:Certificate:Path and SiteServer:Certificate:KeyPath");
        if (!File.Exists(path)) throw new InvalidOperationException($"SiteServer:Certificate:Path '{path}' does not exist");
        if (!File.Exists(keyPath)) throw new InvalidOperationException($"SiteServer:Certificate:KeyPath '{keyPath}' does not exist");
        try
        {
            using var pem = X509Certificate2.CreateFromPemFile(path, keyPath);
            // A PEM key is ephemeral, which Windows TLS (SChannel) cannot use. Re-import it into a machine key
            // set: NETWORK SERVICE has no loaded user profile, so a user key set would fail at the handshake.
            var flags = OperatingSystem.IsWindows() ? X509KeyStorageFlags.MachineKeySet : X509KeyStorageFlags.DefaultKeySet;
            return X509CertificateLoader.LoadPkcs12(pem.Export(X509ContentType.Pkcs12), null, flags);
        }
        catch (CryptographicException ex)
        {
            throw new InvalidOperationException($"the PEM certificate at '{path}' with key '{keyPath}' could not be loaded: {ex.Message}");
        }
    }

    private static X509Certificate2 LoadFromMachineStore(string? storeName, string? thumbprint, string? subject, string publicHost)
    {
        var name = string.IsNullOrWhiteSpace(storeName) ? "My" : storeName.Trim();
        using var store = new X509Store(name, StoreLocation.LocalMachine);
        try { store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly); }
        catch (CryptographicException ex) { throw new InvalidOperationException($"the LocalMachine\\{name} certificate store could not be opened: {ex.Message}"); }

        X509Certificate2? chosen;
        if (!string.IsNullOrEmpty(thumbprint))
        {
            var found = store.Certificates.Find(X509FindType.FindByThumbprint, thumbprint, validOnly: false);
            chosen = found.Count > 0 ? found[0] : null;
            foreach (var other in found.Where(x => !ReferenceEquals(x, chosen))) other.Dispose();
            if (chosen is null) throw new InvalidOperationException($"no certificate with thumbprint {thumbprint} is in LocalMachine\\{name}");
        }
        else
        {
            // By subject the newest usable certificate wins, so a renewal placed beside the old one is picked up
            // at the next restart without editing configuration.
            var found = store.Certificates.Find(X509FindType.FindBySubjectName, subject!, validOnly: false);
            var now = DateTime.Now;
            chosen = found.Where(x => x.HasPrivateKey && x.NotBefore <= now && now <= x.NotAfter && x.MatchesHostname(publicHost))
                .OrderByDescending(x => x.NotAfter).FirstOrDefault();
            foreach (var other in found.Where(x => !ReferenceEquals(x, chosen))) other.Dispose();
            if (chosen is null)
                throw new InvalidOperationException($"no current certificate with a private key covering '{publicHost}' matches subject '{subject}' in LocalMachine\\{name}");
        }
        return chosen;
    }
}
