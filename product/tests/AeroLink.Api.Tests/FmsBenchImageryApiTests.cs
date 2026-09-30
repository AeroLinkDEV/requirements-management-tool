using System.Net;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
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
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 4, 5, 6];

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

    [Fact]
    public async Task A_png_tile_is_relayed_as_png_and_the_next_tile_is_still_fetched()
    {
        // The upstream cache is "mixed": along the edge of its coverage (coastlines, the border) it serves PNG. On HOME
        // on 2026-09-30, 66 of the 223 tiles it served over the bench's area were PNG, and treating each as a failure
        // held every tile back for a minute: the view reported the imagery source unreachable while it answered.
        using var upstream = new FmsBenchTerrainApiTests.Upstream(request =>
            request.RequestUri!.AbsolutePath.EndsWith("/11855/9725") ? Image(Png, "image/png") : Image(Jpeg, "image/jpeg"));
        using var harness = await SignedInAsync(upstream);

        using var png = await harness.Client.GetAsync("/api/fms-bench/imagery/15/9725/11855");
        using var next = await harness.Client.GetAsync("/api/fms-bench/imagery/15/9726/11855");

        Assert.Equal(HttpStatusCode.OK, png.StatusCode);
        Assert.Equal("image/png", png.Content.Headers.ContentType?.MediaType);
        Assert.Equal(Png, await png.Content.ReadAsByteArrayAsync());
        Assert.Equal(HttpStatusCode.OK, next.StatusCode);
        Assert.Equal(2, upstream.Requested.Count);
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
    public async Task No_imagery_upstream_is_a_404_and_a_failure_or_a_body_that_is_not_a_tile_image_is_a_bad_gateway()
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

    [Fact]
    public async Task Outside_the_usgs_coverage_a_stored_key_fetches_esri_imagery_with_the_key_in_a_header_never_the_url()
    {
        using var usgs = new FmsBenchTerrainApiTests.Upstream(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        var esri = new EsriUpstream(_ => Image(Jpeg, "image/jpeg"));
        using var harness = new Harness(usgs, esri: esri, esriKey: "test-esri-key");
        await SecurityBoundaryTests.BootstrapAndLoginAdministratorAsync(harness.Client);

        using var response = await harness.Client.GetAsync("/api/fms-bench/imagery/12/1210/1465");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(Jpeg, await response.Content.ReadAsByteArrayAsync());
        Assert.Equal("esri", Assert.Single(response.Headers.GetValues(FmsBenchImageryEndpoints.SourceHeader)));
        var (uri, authorization) = Assert.Single(esri.Requested);
        Assert.Equal("https://ibasemaps-api.arcgis.com/arcgis/rest/services/World_Imagery/MapServer/tile/12/1465/1210", uri.ToString());
        Assert.Equal("Bearer test-esri-key", authorization);
        Assert.DoesNotContain("test-esri-key", uri.ToString());
    }

    [Fact]
    public async Task A_usgs_tile_is_credited_to_usgs_and_without_a_key_outside_its_coverage_is_none_without_asking_esri()
    {
        using var usgs = new FmsBenchTerrainApiTests.Upstream(request =>
            request.RequestUri!.AbsolutePath.EndsWith("/1465/1210") ? new HttpResponseMessage(HttpStatusCode.NotFound) : Image(Jpeg, "image/jpeg"));
        using var harness = await SignedInAsync(usgs);   // the harness's Esri handler throws if it is asked

        using var inside = await harness.Client.GetAsync("/api/fms-bench/imagery/15/9725/11855");
        using var outside = await harness.Client.GetAsync("/api/fms-bench/imagery/12/1210/1465");

        Assert.Equal("usgs", Assert.Single(inside.Headers.GetValues(FmsBenchImageryEndpoints.SourceHeader)));
        Assert.Equal(HttpStatusCode.NotFound, outside.StatusCode);
    }

    [Fact]
    public async Task A_refused_key_is_none_here_pauses_esri_alone_and_never_holds_usgs_back()
    {
        // ArcGIS refuses an expired or revoked token with a JSON error body under status 200.
        using var usgs = new FmsBenchTerrainApiTests.Upstream(request =>
            request.RequestUri!.AbsolutePath.Contains("/USGSImageryOnly/MapServer/tile/12/")
                ? new HttpResponseMessage(HttpStatusCode.NotFound) : Image(Jpeg, "image/jpeg"));
        var esri = new EsriUpstream(_ => Image("""{"error":{"code":498,"message":"Invalid token."}}"""u8.ToArray(), "application/json"));
        using var harness = new Harness(usgs, esri: esri, esriKey: "expired-key");
        await SecurityBoundaryTests.BootstrapAndLoginAdministratorAsync(harness.Client);

        Assert.Equal(HttpStatusCode.NotFound, (await harness.Client.GetAsync("/api/fms-bench/imagery/12/1210/1465")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await harness.Client.GetAsync("/api/fms-bench/imagery/12/1211/1465")).StatusCode);
        Assert.Single(esri.Requested);
        Assert.Equal(HttpStatusCode.OK, (await harness.Client.GetAsync("/api/fms-bench/imagery/15/9725/11855")).StatusCode);
    }

    [Fact]
    public void The_stored_key_is_read_from_its_protected_file_and_ignored_when_the_file_is_not_locked_down()
    {
        if (!OperatingSystem.IsWindows()) return;   // DPAPI and NTFS ACLs; the backend jobs run on Windows
        var directory = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"esri-key-{Guid.NewGuid():N}"));
        try
        {
            var owner = System.Security.Principal.WindowsIdentity.GetCurrent().User!.Value;
            var path = Path.Combine(directory.FullName, "esri-world-imagery.json");
            var ciphertext = System.Security.Cryptography.ProtectedData.Protect("stored-esri-key"u8.ToArray(),
                System.Text.Encoding.UTF8.GetBytes(FmsBenchEsriImageryKey.Entropy), System.Security.Cryptography.DataProtectionScope.LocalMachine);
            File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(new
            {
                schemaVersion = 1, purpose = FmsBenchEsriImageryKey.Purpose, ownerSid = owner, protectedKey = Convert.ToBase64String(ciphertext),
                fingerprint = new string('a', 64), updatedAtUtc = DateTime.UtcNow.ToString("o"),
            }));
            var reader = new FmsBenchEsriImageryKey(
                new Microsoft.Extensions.Configuration.ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { [FmsBenchEsriImageryKey.KeyFileSetting] = path }).Build(),
                Microsoft.Extensions.Logging.Abstractions.NullLogger<FmsBenchEsriImageryKey>.Instance);

            // As written into a temp directory: inherited permissions, so refused.
            Assert.Null(reader.Current());

            // Locked down as the store writes it: owner, SYSTEM and Administrators, inheritance off. Then read.
            var directorySecurity = directory.GetAccessControl();
            var fileSecurity = new FileInfo(path).GetAccessControl();
            foreach (System.Security.AccessControl.FileSystemSecurity security in new System.Security.AccessControl.FileSystemSecurity[] { directorySecurity, fileSecurity })
            {
                security.SetAccessRuleProtection(true, false);
                foreach (System.Security.AccessControl.FileSystemAccessRule rule in security.GetAccessRules(true, false, typeof(System.Security.Principal.SecurityIdentifier)))
                    security.RemoveAccessRule(rule);
                foreach (var sid in new[] { owner, "S-1-5-18", "S-1-5-32-544" })
                    security.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(new System.Security.Principal.SecurityIdentifier(sid),
                        System.Security.AccessControl.FileSystemRights.FullControl, System.Security.AccessControl.AccessControlType.Allow));
            }
            directory.SetAccessControl(directorySecurity);
            new FileInfo(path).SetAccessControl(fileSecurity);
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(5));   // a changed file is read again
            Assert.Equal("stored-esri-key", reader.Current());
        }
        finally { directory.Delete(recursive: true); }
    }

    /// <summary>The Esri upstream: records each request's address and its X-Esri-Authorization header.</summary>
    internal sealed class EsriUpstream(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<(Uri Uri, string? Authorization)> Requested { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requested.Add((request.RequestUri!, request.Headers.TryGetValues("X-Esri-Authorization", out var values) ? values.Single() : null));
            return Task.FromResult(respond(request));
        }
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

        public Harness(FmsBenchTerrainApiTests.Upstream upstream, bool? imagery = true, bool? terrain = null,
            EsriUpstream? esri = null, string? esriKey = null)
        {
            host = factory.WithWebHostBuilder(builder =>
            {
                if (imagery is { } on) builder.UseSetting(FmsBenchImageryEndpoints.EnabledKey, on.ToString());
                if (terrain is { } terrainOn) builder.UseSetting(FmsBenchTerrainEndpoints.EnabledKey, terrainOn.ToString());
                // Never the machine's real key: on HOME one is stored, and a test must not spend it or reach Esri.
                builder.UseSetting(FmsBenchEsriImageryKey.KeyFileSetting, Path.Combine(Path.GetTempPath(), $"no-esri-key-{Guid.NewGuid():N}.json"));
                if (esriKey is not null) builder.UseSetting(FmsBenchEsriImageryKey.DirectKeySetting, esriKey);
                builder.ConfigureTestServices(services =>
                {
                    services.AddHttpClient(FmsBenchImageryEndpoints.ClientName).ConfigurePrimaryHttpMessageHandler(() => upstream);
                    var esriHandler = esri ?? new EsriUpstream(_ => throw new InvalidOperationException("Esri must not be asked"));
                    services.AddHttpClient(FmsBenchImageryEndpoints.EsriClientName).ConfigurePrimaryHttpMessageHandler(() => esriHandler);
                });
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
