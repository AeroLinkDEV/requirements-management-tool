using System.Net;
using Microsoft.Extensions.DependencyInjection.Extensions;

/// <summary>
/// Relays aerial imagery tiles for the FMS Test Bench's out-the-window view, as the terrain relay does elevation
/// (<see cref="FmsBenchTerrainEndpoints"/>): the client's Content Security Policy admits this server only, so the
/// browser asks here, and this first relays the USGS National Map "USGS Imagery Only" tile cache
/// (USDA NAIP and USGS orthoimagery, US federal public-domain data, 6 inches to 1 metre, covering the United States).
/// The upstream URL is fixed in code and built from three validated integers, so this can reach nothing else. Tiles
/// are JPEG, or PNG along the edge of the coverage (see <see cref="TileTypes"/>), bounded in size and time, and marked
/// cacheable.
///
/// Outside the United States the service answers 404 (and, at low zoom levels near its edge, a blank white tile); the
/// view asks for the optional Esri fallback after decoding a blank tile. This is a separate outbound destination from the terrain source, so
/// an installation chooses it separately: <c>FmsBench:ImageryRelay</c>, which follows <c>FmsBench:TerrainRelay</c>
/// (on in development) unless set.
///
/// Outside the USGS coverage, and only when the owner has stored an Esri API key (DEC-151,
/// <see cref="FmsBenchEsriImageryKey"/>), the relay asks a second fixed upstream, Esri World Imagery, instead of
/// answering 404. Every tile says which source it came from (<see cref="SourceHeader"/>), so the view credits Esri
/// whenever its imagery is on screen, as Esri's terms require.
/// </summary>
public static class FmsBenchImageryEndpoints
{
    public const string ClientName = "fms-bench-imagery";
    public const string EnabledKey = "FmsBench:ImageryRelay";
    public static readonly TimeSpan FailureBackoff = TimeSpan.FromMinutes(1);
    /// <summary>The deepest level the USGS Imagery Only cache publishes (17 and deeper answer 404).</summary>
    public const int MaxZoom = 16;
    public const long MaxTileBytes = 1_048_576;
    internal const string Upstream = "https://basemap.nationalmap.gov/arcgis/rest/services/USGSImageryOnly/MapServer/tile";
    /// <summary>
    /// What the upstream serves a tile as. Its cache is "mixed": JPEG where a tile is fully covered, PNG along the edge of
    /// the coverage (coastlines and the border). Measured on HOME on 2026-09-30, 66 of the 223 tiles it served over the
    /// bench's area were PNG; treating those as failures held every tile back for a minute at a time.
    /// </summary>
    private static readonly string[] TileTypes = ["image/jpeg", "image/png"];
    public const string EsriClientName = "fms-bench-imagery-esri";
    internal const string EsriUpstream = "https://ibasemaps-api.arcgis.com/arcgis/rest/services/World_Imagery/MapServer/tile";
    /// <summary>Which source a relayed tile came from: <c>usgs</c> or <c>esri</c>.</summary>
    public const string SourceHeader = "X-Imagery-Source";
    public static readonly TimeSpan EsriFailureBackoff = TimeSpan.FromMinutes(1);
    /// <summary>A refused key does not fix itself in a minute; asking again on every tile would only repeat the refusal.</summary>
    public static readonly TimeSpan EsriRefusedBackoff = TimeSpan.FromMinutes(15);

    public static IEndpointRouteBuilder MapFmsBenchImageryEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/fms-bench/imagery/{z:int}/{x:int}/{y:int}", RelayAsync);
        return app;
    }

    public static void AddFmsBenchImageryClient(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<ImageryUpstreamHealth>();
        services.AddSingleton<EsriImageryHealth>();
        services.AddSingleton<FmsBenchEsriImageryKey>();
        foreach (var name in new[] { ClientName, EsriClientName })
        {
            var builder = services.AddHttpClient(name, client =>
            {
                client.Timeout = TimeSpan.FromSeconds(10);
                client.MaxResponseContentBufferSize = MaxTileBytes;
                client.DefaultRequestHeaders.UserAgent.ParseAdd("AeroLink-FMS-Test-Bench/1.0");
            });
            // DEC-152: this fixed HTTPS request contains the credential in its query. Do not log it or
            // follow a redirect carrying it to another host. Neither cookies nor browser credentials are used.
            if (name == EsriClientName) builder.RemoveAllLoggers().ConfigurePrimaryHttpMessageHandler(() =>
                new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false });
        }
    }

    /// <summary>A Web Mercator tile address that exists: 0 ≤ z ≤ 16 and x, y within 0 … 2^z − 1.</summary>
    public static bool IsValidTile(int z, int x, int y) =>
        z is >= 0 and <= MaxZoom && x >= 0 && y >= 0 && x < 1 << z && y < 1 << z;

    public static bool IsEnabled(IConfiguration configuration, IHostEnvironment environment) =>
        configuration.GetValue<bool?>(EnabledKey) ?? FmsBenchTerrainEndpoints.IsEnabled(configuration, environment);

    private static async Task<IResult> RelayAsync(int z, int x, int y, HttpContext http, IHttpClientFactory clients,
        IConfiguration configuration, IHostEnvironment environment, ImageryUpstreamHealth health, EsriImageryHealth esriHealth,
        FmsBenchEsriImageryKey esriKey, CancellationToken ct, bool? fallback)
    {
        if (!IsEnabled(configuration, environment))
            return Results.Json(new { error = $"Imagery is off on this installation ({EnabledKey}).", code = "imagery_relay_disabled" },
                statusCode: StatusCodes.Status404NotFound);
        if (!IsValidTile(z, x, y))
            return Results.BadRequest(new { error = $"Imagery tile {z}/{x}/{y} does not exist: zoom is 0 to {MaxZoom}, and x and y are 0 to 2^zoom - 1." });
        if (fallback == true) return await EsriOrNoneAsync(z, x, y, http, clients, esriHealth, esriKey, ct);
        if (!health.Available)
            return Results.Json(new { error = "The imagery source did not answer recently; retrying shortly.", code = "imagery_source_unavailable" },
                statusCode: StatusCodes.Status503ServiceUnavailable);
        try
        {
            // The ArcGIS tile cache is addressed level/row/column: z, then y, then x.
            using var response = await clients.CreateClient(ClientName).GetAsync($"{Upstream}/{z}/{y}/{x}", ct);
            if (response.StatusCode == HttpStatusCode.NotFound) return await EsriOrNoneAsync(z, x, y, http, clients, esriHealth, esriKey, ct);
            var type = response.Content.Headers.ContentType?.MediaType;
            if (!response.IsSuccessStatusCode || type is null || !TileTypes.Contains(type))
                return health.Failed(StatusCodes.Status502BadGateway);
            var bytes = await response.Content.ReadAsByteArrayAsync(ct);
            http.Response.Headers.CacheControl = "private, max-age=604800";
            http.Response.Headers[SourceHeader] = "usgs";
            return Results.File(bytes, type);
        }
        catch (HttpRequestException) { return health.Failed(StatusCodes.Status502BadGateway); }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested) { return health.Failed(StatusCodes.Status504GatewayTimeout); }
    }

    /// <summary>
    /// Where USGS has no imagery (outside the United States), Esri World Imagery when the owner has stored a key (DEC-151);
    /// otherwise, and whenever Esri does not give an image, the same 404 as before, so the view draws relief there. Esri
    /// is optional, so its failures never report the imagery source unreachable and never hold USGS back: they pause
    /// Esri alone, for longer when the key itself is refused (expired, revoked, or the month's allowance used up).
    /// </summary>
    private static async Task<IResult> EsriOrNoneAsync(int z, int x, int y, HttpContext http, IHttpClientFactory clients,
        EsriImageryHealth esriHealth, FmsBenchEsriImageryKey esriKey, CancellationToken ct)
    {
        if (esriKey.Current() is not { } key || !esriHealth.AvailableFor(key)) return Results.NotFound();
        try
        {
            // World Imagery tiles ignore header authentication (live error 499). DEC-152 permits a
            // token only in this fixed backend HTTPS URL; this client's logging and redirects are disabled.
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{EsriUpstream}/{z}/{y}/{x}?token={Uri.EscapeDataString(key)}");
            using var response = await clients.CreateClient(EsriClientName).SendAsync(request, ct);
            if (response.StatusCode == HttpStatusCode.NotFound) return Results.NotFound();
            var type = response.Content.Headers.ContentType?.MediaType;
            // ArcGIS refuses a token as 401/403, or as a JSON error body with status 200 (codes 498/499).
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests
                || (response.IsSuccessStatusCode && type is not null && !TileTypes.Contains(type)))
                return esriHealth.Refused(key);
            if (!response.IsSuccessStatusCode || type is null) return esriHealth.Failed(key);
            var bytes = await response.Content.ReadAsByteArrayAsync(ct);
            // Esri's own caching instruction, not ours: its terms govern how long its tiles may be kept.
            http.Response.Headers.CacheControl = response.Headers.CacheControl?.ToString() is { Length: > 0 } cache ? cache : "private, max-age=86400";
            http.Response.Headers[SourceHeader] = "esri";
            return Results.File(bytes, type);
        }
        catch (HttpRequestException) { return esriHealth.Failed(key); }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested) { return esriHealth.Failed(key); }
    }

    /// <summary>When the imagery upstream last failed, so a dead route is not retried on every tile.</summary>
    public sealed class ImageryUpstreamHealth(TimeProvider clock)
    {
        private long unavailableUntilTicks;
        public bool Available => clock.GetUtcNow().UtcTicks >= Interlocked.Read(ref unavailableUntilTicks);

        public IResult Failed(int status)
        {
            Interlocked.Exchange(ref unavailableUntilTicks, (clock.GetUtcNow() + FailureBackoff).UtcTicks);
            return Results.StatusCode(status);
        }
    }

    /// <summary>When Esri last failed or refused the key. Its answer to the browser is always "none here" (404).</summary>
    public sealed class EsriImageryHealth(TimeProvider clock)
    {
        private readonly Lock gate = new();
        private string? currentKey;
        private long unavailableUntilTicks;
        public bool AvailableFor(string key)
        {
            lock (gate)
            {
                if (currentKey != key) { currentKey = key; unavailableUntilTicks = 0; }
                return clock.GetUtcNow().UtcTicks >= unavailableUntilTicks;
            }
        }
        public IResult Failed(string key) => Pause(key, EsriFailureBackoff);
        public IResult Refused(string key) => Pause(key, EsriRefusedBackoff);

        private IResult Pause(string key, TimeSpan span)
        {
            lock (gate)
                // An in-flight refusal for the old credential must not pause its replacement.
                if (currentKey == key) unavailableUntilTicks = (clock.GetUtcNow() + span).UtcTicks;
            return Results.NotFound();
        }
    }
}
