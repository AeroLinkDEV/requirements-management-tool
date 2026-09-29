using System.Net;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace AeroLink.Api.Tests;

/// <summary>
/// The FMS Test Bench's terrain relay. The browser may only reach this server (Content Security Policy), so the relay
/// is what lets the out-the-window view show open elevation data; it must reach exactly its one upstream, refuse tile
/// addresses that cannot exist without calling out, and never pass an upstream failure off as a tile.
/// </summary>
public sealed class FmsBenchTerrainApiTests
{
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3];

    [Fact]
    public async Task A_signed_in_user_gets_the_tile_from_the_fixed_upstream_marked_cacheable()
    {
        using var upstream = new Upstream(_ => Image(Png, "image/png"));
        using var harness = await SignedInAsync(upstream); var client = harness.Client;

        using var response = await client.GetAsync("/api/fms-bench/terrain/13/2410/2918");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("image/png", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(Png, await response.Content.ReadAsByteArrayAsync());
        Assert.Contains("max-age=604800", response.Headers.CacheControl?.ToString());
        Assert.Equal("https://s3.amazonaws.com/elevation-tiles-prod/terrarium/13/2410/2918.png", Assert.Single(upstream.Requested).ToString());
    }

    [Theory]
    [InlineData("16/0/0")]      // deeper than the set publishes
    [InlineData("2/4/0")]       // x beyond 2^z - 1
    [InlineData("2/0/4")]       // y beyond 2^z - 1
    [InlineData("3/-1/0")]
    public async Task A_tile_that_cannot_exist_is_refused_without_calling_out(string address)
    {
        using var upstream = new Upstream(_ => Image(Png, "image/png"));
        using var harness = await SignedInAsync(upstream); var client = harness.Client;

        using var response = await client.GetAsync($"/api/fms-bench/terrain/{address}");

        Assert.True(response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.NotFound, $"{address}: {response.StatusCode}");
        Assert.Empty(upstream.Requested);
    }

    [Fact]
    public async Task An_upstream_failure_or_a_body_that_is_not_a_png_is_a_bad_gateway_not_a_tile()
    {
        using var failing = new Upstream(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        using (var harness = await SignedInAsync(failing))
            Assert.Equal(HttpStatusCode.BadGateway, (await harness.Client.GetAsync("/api/fms-bench/terrain/5/9/11")).StatusCode);

        using var html = new Upstream(_ => Image("<html>captive portal</html>"u8.ToArray(), "text/html"));
        using (var harness = await SignedInAsync(html))
            Assert.Equal(HttpStatusCode.BadGateway, (await harness.Client.GetAsync("/api/fms-bench/terrain/5/9/11")).StatusCode);

        using var missing = new Upstream(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        using (var harness = await SignedInAsync(missing))
            Assert.Equal(HttpStatusCode.NotFound, (await harness.Client.GetAsync("/api/fms-bench/terrain/5/9/11")).StatusCode);
    }

    [Fact]
    public async Task Outside_development_the_relay_is_off_until_the_installation_turns_it_on()
    {
        using var upstream = new Upstream(_ => Image(Png, "image/png"));
        using var harness = new Harness(upstream, enabled: null);
        await SecurityBoundaryTests.BootstrapAndLoginAdministratorAsync(harness.Client);

        using var response = await harness.Client.GetAsync("/api/fms-bench/terrain/5/9/11");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains("terrain_relay_disabled", await response.Content.ReadAsStringAsync());
        Assert.Empty(upstream.Requested);
    }

    [Fact]
    public async Task After_an_upstream_failure_the_relay_answers_at_once_instead_of_calling_out_again()
    {
        using var failing = new Upstream(_ => throw new HttpRequestException("dropped by a firewall"));
        using var harness = await SignedInAsync(failing);

        Assert.Equal(HttpStatusCode.BadGateway, (await harness.Client.GetAsync("/api/fms-bench/terrain/5/9/11")).StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await harness.Client.GetAsync("/api/fms-bench/terrain/5/9/12")).StatusCode);
        Assert.Single(failing.Requested);
    }

    [Fact]
    public async Task The_relay_requires_a_session()
    {
        using var upstream = new Upstream(_ => Image(Png, "image/png"));
        using var harness = new Harness(upstream);
        var anonymous = harness.Client;

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/fms-bench/terrain/0/0/0")).StatusCode);
        Assert.Empty(upstream.Requested);
    }

    private static HttpResponseMessage Image(byte[] body, string type)
    {
        var content = new ByteArrayContent(body);
        content.Headers.ContentType = new MediaTypeHeaderValue(type);
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }

    /// <summary>
    /// A host whose terrain client talks to <paramref name="upstream"/>, with an anonymous client on it. The test host
    /// runs as Production, where the relay is off unless configured; <paramref name="enabled"/> null leaves it unset.
    /// </summary>
    private sealed class Harness : IDisposable
    {
        private readonly AeroLinkApiFactory factory = new();
        private readonly Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> host;
        public HttpClient Client { get; }

        public Harness(Upstream upstream, bool? enabled = true)
        {
            host = factory.WithWebHostBuilder(builder =>
            {
                if (enabled is { } on) builder.UseSetting(FmsBenchTerrainEndpoints.EnabledKey, on.ToString());
                builder.ConfigureTestServices(services =>
                    services.AddHttpClient(FmsBenchTerrainEndpoints.ClientName).ConfigurePrimaryHttpMessageHandler(() => upstream));
            });
            Client = host.CreateClient();
        }

        public void Dispose() { Client.Dispose(); host.Dispose(); factory.Dispose(); }
    }

    private static async Task<Harness> SignedInAsync(Upstream upstream)
    {
        var harness = new Harness(upstream);
        await SecurityBoundaryTests.BootstrapAndLoginAdministratorAsync(harness.Client);
        return harness;
    }

    internal sealed class Upstream(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<Uri> Requested { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requested.Add(request.RequestUri!);
            return Task.FromResult(respond(request));
        }
    }
}
