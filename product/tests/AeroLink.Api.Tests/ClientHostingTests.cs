using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace AeroLink.Api.Tests;

/// <summary>
/// Whether the API serves the built client, and from where.
///
/// These exist because the resolution was wrong on its first attempt in a way nothing would have reported: it
/// looked only under the content root, which is the project directory under `dotnet run --project` but the
/// working directory under `dotnet AeroLink.Api.dll`. Started the second way it found nothing, served nothing,
/// and answered /health perfectly — a launcher would have declared success over a site that returned 404.
/// </summary>
public sealed class ClientHostingTests
{
    [Fact]
    public void An_explicitly_configured_client_directory_is_used()
    {
        using var directory = new TemporaryDirectory();
        File.WriteAllText(Path.Combine(directory.Path, "index.html"), "<!doctype html>");

        var resolved = ClientHosting.ResolveClientRoot(Environment("no-such-content-root"), Configuration(directory.Path));

        Assert.Equal(Path.GetFullPath(directory.Path), resolved);
    }

    [Fact]
    public void An_explicitly_configured_directory_without_a_client_is_a_misconfiguration_not_an_opt_out()
    {
        using var directory = new TemporaryDirectory();

        // Falling back to the conventions here would serve a different client than the operator named, with
        // nothing said about it. An empty directory left by an interrupted build is the realistic case.
        var failure = Assert.Throws<InvalidOperationException>(
            () => ClientHosting.ResolveClientRoot(Environment("no-such-content-root"), Configuration(directory.Path)));
        Assert.Contains("index.html", failure.Message);
    }

    [Fact]
    public void A_published_wwwroot_beside_the_application_is_found()
    {
        using var directory = new TemporaryDirectory();
        var wwwroot = Directory.CreateDirectory(Path.Combine(directory.Path, "wwwroot"));
        File.WriteAllText(Path.Combine(wwwroot.FullName, "index.html"), "<!doctype html>");

        var resolved = ClientHosting.ResolveClientRoot(Environment(directory.Path), Configuration(null));

        Assert.Equal(wwwroot.FullName, resolved);
    }

    [Fact]
    public void A_wwwroot_holding_no_entry_document_is_not_a_client()
    {
        using var directory = new TemporaryDirectory();
        Directory.CreateDirectory(Path.Combine(directory.Path, "wwwroot"));

        // The directory is the wrong test. Plenty of API projects carry a wwwroot and serve no client, and a
        // build that failed halfway leaves one behind.
        Assert.Null(ClientHosting.ResolveClientRoot(Environment(directory.Path), Configuration(null)));
    }

    [Fact]
    public void Serving_no_client_is_the_default_and_leaves_the_API_as_it_was()
    {
        using var directory = new TemporaryDirectory();

        // A deployment that serves the client through its own reverse proxy depends on this staying true, which
        // is why the repository's own client/dist is deliberately never discovered.
        Assert.Null(ClientHosting.ResolveClientRoot(Environment(directory.Path), Configuration(null)));
    }

    [Theory]
    [InlineData("/api", true)]
    [InlineData("/api/change-requests", true)]
    [InlineData("/health", true)]
    [InlineData("/health/ready", true)]
    [InlineData("/API/AUTH/LOGIN", true)]
    [InlineData("/", false)]
    [InlineData("/command-center", false)]
    [InlineData("/assets/index-abc123.js", false)]
    // Not an API path: the prefix has to be a whole segment, or a client route that merely begins with those
    // letters would be served JSON errors and the strict policy that forbids a document from loading anything.
    [InlineData("/apidocs", false)]
    [InlineData("/healthcheck-summary", false)]
    public void Api_paths_are_distinguished_from_client_routes(string path, bool expected)
        => Assert.Equal(expected, ClientHosting.IsApiPath(path));

    [Fact]
    public void The_document_policy_permits_this_origin_and_nothing_beyond_it()
    {
        var policy = ClientHosting.DocumentContentSecurityPolicy;

        // DEC-047 says the client makes no external request at runtime. Stated here, the browser enforces it
        // instead of it being a property somebody has to keep remembering.
        Assert.Contains("default-src 'self'", policy);
        Assert.Contains("connect-src 'self'", policy);
        Assert.Contains("script-src 'self'", policy);
        Assert.DoesNotContain("script-src 'self' 'unsafe-inline'", policy);
        // The build inlines assets under its size threshold into the stylesheet, so omitting data: here looks
        // tighter and simply blocks the self-hosted typefaces.
        Assert.Contains("font-src 'self' data:", policy);
        Assert.Contains("img-src 'self' data:", policy);
        Assert.DoesNotContain("sandbox", policy);

        // And the API's policy must stay the strict one, or the two have been confused for each other.
        Assert.Contains("default-src 'none'", ClientHosting.ApiContentSecurityPolicy);
        Assert.Contains("sandbox", ClientHosting.ApiContentSecurityPolicy);
    }

    private static IConfiguration Configuration(string? staticFiles) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(staticFiles is null ? [] : new Dictionary<string, string?> { ["Client:StaticFiles"] = staticFiles })
            .Build();

    private static IWebHostEnvironment Environment(string contentRoot) => new StubEnvironment(contentRoot);

    private sealed class StubEnvironment(string contentRoot) : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "AeroLink.Api";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public string ContentRootPath { get; set; } = contentRoot;
        public string EnvironmentName { get; set; } = Environments.Production;
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; } = contentRoot;
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = Directory.CreateTempSubdirectory("aerolink-client-hosting").FullName;
        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); }
            catch (IOException) { /* A leftover temp directory is not worth failing a test over. */ }
        }
    }
}
