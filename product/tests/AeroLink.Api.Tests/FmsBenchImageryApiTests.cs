using System.Net;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace AeroLink.Api.Tests;

/// <summary>
/// The FMS Test Bench's imagery relay (<see cref="FmsBenchImageryEndpoints"/>): the out-the-window view's aerial
/// imagery reaches the browser only through this server. It must reach exactly its one upstream, the USGS National Map
/// tile cache addressed level/row/column, refuse tile addresses that cannot exist without calling out, pass the
/// service's "no imagery here" through as a 404 for the view to draw relief instead, and never pass an upstream
/// failure off as a tile. It is an installation's choice separately from the terrain relay.
/// </summary>
public sealed class FmsBenchImageryApiTests
{
    private static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3];

    [Fact]
    public async Task A_signed_in_user_gets_the_tile_from_the_fixed_upstream_addressed_level_row_column()
    {
        using var upstream = new FmsBenchTerrainApiTests.Upstream(_ => Image(Jpeg, "image/jpeg"));
        using var harness = await SignedInAsync(upstream);

        using var response = await harness.Client.GetAsync("/api/fms-bench/imagery/15/9725/11855");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("image/jpeg", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(Jpeg, await response.Content.ReadAsByteArrayAsync());
        Assert.Contains("max-age=604800", response.Headers.CacheControl?.ToString());
        // x = 9725, y = 11855 is asked for as row 11855, column 9725.
        Assert.Equal("https://basemap.nationalmap.gov/arcgis/rest/services/USGSImageryOnly/MapServer/tile/15/11855/9725",
            Assert.Single(upstream.Requested).ToString());
    }

    [Theory]
    [InlineData("17/0/0")]      // deeper than the cache publishes
    [InlineData("2/4/0")]       // x beyond 2^z - 1
    [InlineData("2/0/4")]       // y beyond 2^z - 1
    [InlineData("3/-1/0")]
    public async Task A_tile_that_cannot_exist_is_refused_without_calling_out(string address)
    {
        using var upstream = new FmsBenchTerrainApiTests.Upstream(_ => Image(Jpeg, "image/jpeg"));
        using var harness = await SignedInAsync(upstream);

        using var response = await harness.Client.GetAsync($"/api/fms-bench/imagery/{address}");

        Assert.True(response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.NotFound, $"{address}: {response.StatusCode}");
        Assert.Empty(upstream.Requested);
    }

    [Fact]
    public async Task No_imagery_upstream_is_a_404_and_a_failure_or_a_body_that_is_not_a_jpeg_is_a_bad_gateway()
    {
        using var missing = new FmsBenchTerrainApiTests.Upstream(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        using (var harness = await SignedInAsync(missing))
            Assert.Equal(HttpStatusCode.NotFound, (await harness.Client.GetAsync("/api/fms-bench/imagery/12/2046/1362")).StatusCode);

        using var failing = new FmsBenchTerrainApiTests.Upstream(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        using (var harness = await SignedInAsync(failing))
            Assert.Equal(HttpStatusCode.BadGateway, (await harness.Client.GetAsync("/api/fms-bench/imagery/5/9/11")).StatusCode);

        using var html = new FmsBenchTerrainApiTests.Upstream(_ => Image("<html>captive portal</html>"u8.ToArray(), "text/html"));
        using (var harness = await SignedInAsync(html))
            Assert.Equal(HttpStatusCode.BadGateway, (await harness.Client.GetAsync("/api/fms-bench/imagery/5/9/11")).StatusCode);
    }

    [Fact]
    public async Task After_an_upstream_failure_the_relay_answers_at_once_instead_of_calling_out_again()
    {
        using var failing = new FmsBenchTerrainApiTests.Upstream(_ => throw new HttpRequestException("dropped by a firewall"));
        using var harness = await SignedInAsync(failing);

        Assert.Equal(HttpStatusCode.BadGateway, (await harness.Client.GetAsync("/api/fms-bench/imagery/5/9/11")).StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await harness.Client.GetAsync("/api/fms-bench/imagery/5/9/12")).StatusCode);
        Assert.Single(failing.Requested);
    }

    [Fact]
    public async Task Outside_development_it_follows_the_terrain_relay_setting_unless_set_itself()
    {
        // Neither set: off, as the terrain relay is.
        using (var upstream = new FmsBenchTerrainApiTests.Upstream(_ => Image(Jpeg, "image/jpeg")))
        using (var harness = new Harness(upstream, imagery: null, terrain: null))
        {
            await SecurityBoundaryTests.BootstrapAndLoginAdministratorAsync(harness.Client);
            using var response = await harness.Client.GetAsync("/api/fms-bench/imagery/5/9/11");
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.Contains("imagery_relay_disabled", await response.Content.ReadAsStringAsync());
            Assert.Empty(upstream.Requested);
        }
        // The terrain relay on: imagery follows it.
        using (var upstream = new FmsBenchTerrainApiTests.Upstream(_ => Image(Jpeg, "image/jpeg")))
        using (var harness = new Harness(upstream, imagery: null, terrain: true))
        {
            await SecurityBoundaryTests.BootstrapAndLoginAdministratorAsync(harness.Client);
            Assert.Equal(HttpStatusCode.OK, (await harness.Client.GetAsync("/api/fms-bench/imagery/5/9/11")).StatusCode);
        }
        // Terrain on, imagery turned off by the installation: off.
        using (var upstream = new FmsBenchTerrainApiTests.Upstream(_ => Image(Jpeg, "image/jpeg")))
        using (var harness = new Harness(upstream, imagery: false, terrain: true))
        {
            await SecurityBoundaryTests.BootstrapAndLoginAdministratorAsync(harness.Client);
            Assert.Equal(HttpStatusCode.NotFound, (await harness.Client.GetAsync("/api/fms-bench/imagery/5/9/11")).StatusCode);
            Assert.Empty(upstream.Requested);
        }
    }

    [Fact]
    public async Task The_relay_requires_a_session()
    {
        using var upstream = new FmsBenchTerrainApiTests.Upstream(_ => Image(Jpeg, "image/jpeg"));
        using var harness = new Harness(upstream);

        Assert.Equal(HttpStatusCode.Unauthorized, (await harness.Client.GetAsync("/api/fms-bench/imagery/0/0/0")).StatusCode);
        Assert.Empty(upstream.Requested);
    }

    private static HttpResponseMessage Image(byte[] body, string type)
    {
        var content = new ByteArrayContent(body);
        content.Headers.ContentType = new MediaTypeHeaderValue(type);
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }

    /// <summary>
    /// A host whose imagery client talks to <paramref name="upstream"/>. The test host runs as Production, where both
    /// relays are off unless configured; a null setting leaves it unset.
    /// </summary>
    private sealed class Harness : IDisposable
    {
        private readonly AeroLinkApiFactory factory = new();
        private readonly Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> host;
        public HttpClient Client { get; }

        public Harness(FmsBenchTerrainApiTests.Upstream upstream, bool? imagery = true, bool? terrain = null)
        {
            host = factory.WithWebHostBuilder(builder =>
            {
                if (imagery is { } on) builder.UseSetting(FmsBenchImageryEndpoints.EnabledKey, on.ToString());
                if (terrain is { } terrainOn) builder.UseSetting(FmsBenchTerrainEndpoints.EnabledKey, terrainOn.ToString());
                builder.ConfigureTestServices(services =>
                    services.AddHttpClient(FmsBenchImageryEndpoints.ClientName).ConfigurePrimaryHttpMessageHandler(() => upstream));
            });
            Client = host.CreateClient();
        }

        public void Dispose() { Client.Dispose(); host.Dispose(); factory.Dispose(); }
    }

    private static async Task<Harness> SignedInAsync(FmsBenchTerrainApiTests.Upstream upstream)
    {
        var harness = new Harness(upstream);
        await SecurityBoundaryTests.BootstrapAndLoginAdministratorAsync(harness.Client);
        return harness;
    }
}
