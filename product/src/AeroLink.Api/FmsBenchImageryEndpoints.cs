using System.Net;
using Microsoft.Extensions.DependencyInjection.Extensions;

/// <summary>
/// Relays aerial imagery tiles for the FMS Test Bench's out-the-window view, as the terrain relay does elevation
/// (<see cref="FmsBenchTerrainEndpoints"/>): the client's Content Security Policy admits this server only, so the
/// browser asks here, and this relays exactly one upstream, the USGS National Map "USGS Imagery Only" tile cache
/// (USDA NAIP and USGS orthoimagery, US federal public-domain data, 6 inches to 1 metre, covering the United States).
/// The upstream URL is fixed in code and built from three validated integers, so this can reach nothing else. Tiles
/// are JPEG, bounded in size and time, and marked cacheable.
///
/// Outside the United States the service answers 404 (and, at low zoom levels near its edge, a blank white tile); the
/// view then draws its elevation relief instead. This is a separate outbound destination from the terrain source, so
/// an installation chooses it separately: <c>FmsBench:ImageryRelay</c>, which follows <c>FmsBench:TerrainRelay</c>
/// (on in development) unless set.
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

    public static IEndpointRouteBuilder MapFmsBenchImageryEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/fms-bench/imagery/{z:int}/{x:int}/{y:int}", RelayAsync);
        return app;
    }

    public static void AddFmsBenchImageryClient(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<ImageryUpstreamHealth>();
        services.AddHttpClient(ClientName, client =>
        {
            client.Timeout = TimeSpan.FromSeconds(10);
            client.MaxResponseContentBufferSize = MaxTileBytes;
            client.DefaultRequestHeaders.UserAgent.ParseAdd("AeroLink-FMS-Test-Bench/1.0");
        });
    }

    /// <summary>A Web Mercator tile address that exists: 0 ≤ z ≤ 16 and x, y within 0 … 2^z − 1.</summary>
    public static bool IsValidTile(int z, int x, int y) =>
        z is >= 0 and <= MaxZoom && x >= 0 && y >= 0 && x < 1 << z && y < 1 << z;

    public static bool IsEnabled(IConfiguration configuration, IHostEnvironment environment) =>
        configuration.GetValue<bool?>(EnabledKey) ?? FmsBenchTerrainEndpoints.IsEnabled(configuration, environment);

    private static async Task<IResult> RelayAsync(int z, int x, int y, HttpContext http, IHttpClientFactory clients,
        IConfiguration configuration, IHostEnvironment environment, ImageryUpstreamHealth health, CancellationToken ct)
    {
        if (!IsEnabled(configuration, environment))
            return Results.Json(new { error = $"Imagery is off on this installation ({EnabledKey}).", code = "imagery_relay_disabled" },
                statusCode: StatusCodes.Status404NotFound);
        if (!IsValidTile(z, x, y))
            return Results.BadRequest(new { error = $"Imagery tile {z}/{x}/{y} does not exist: zoom is 0 to {MaxZoom}, and x and y are 0 to 2^zoom - 1." });
        if (!health.Available)
            return Results.Json(new { error = "The imagery source did not answer recently; retrying shortly.", code = "imagery_source_unavailable" },
                statusCode: StatusCodes.Status503ServiceUnavailable);
        try
        {
            // The ArcGIS tile cache is addressed level/row/column: z, then y, then x.
            using var response = await clients.CreateClient(ClientName).GetAsync($"{Upstream}/{z}/{y}/{x}", ct);
            if (response.StatusCode == HttpStatusCode.NotFound) return Results.NotFound();
            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentType?.MediaType != "image/jpeg")
                return health.Failed(StatusCodes.Status502BadGateway);
            var bytes = await response.Content.ReadAsByteArrayAsync(ct);
            http.Response.Headers.CacheControl = "private, max-age=604800";
            return Results.File(bytes, "image/jpeg");
        }
        catch (HttpRequestException) { return health.Failed(StatusCodes.Status502BadGateway); }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested) { return health.Failed(StatusCodes.Status504GatewayTimeout); }
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
}
