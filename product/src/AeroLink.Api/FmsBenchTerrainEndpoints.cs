using System.Net;
using Microsoft.Extensions.DependencyInjection.Extensions;

/// <summary>
/// Relays open elevation tiles for the FMS Test Bench's out-the-window view.
///
/// The client runs under a Content Security Policy that admits images and connections from this server only, and that
/// policy is not widened for a test bench. So the browser asks here, and this relays exactly one upstream: the AWS Open
/// Data "Terrain Tiles" set (Terrarium PNG encoding; SRTM, GMTED, NED and other open elevation sources). The upstream
/// URL is fixed in code and built from three validated integers, so this can reach nothing else: it is not a proxy.
/// Responses are bounded in size and time, and marked cacheable because a tile never changes.
///
/// It is also an outbound call from the server, which DEC-047's reasoning asks an installation to choose: AeroLink
/// runs on restricted and disconnected networks, and egress to a third party is a security-review question. So it is
/// off unless <c>FmsBench:TerrainRelay</c> is true (development defaults it on), and after an upstream failure it
/// refuses at once for a minute rather than letting a firewall that drops packets hold every request for the timeout.
/// Without it the view still flies over flat ground.
/// </summary>
public static class FmsBenchTerrainEndpoints
{
    public const string ClientName = "fms-bench-terrain";
    public const string EnabledKey = "FmsBench:TerrainRelay";
    public static readonly TimeSpan FailureBackoff = TimeSpan.FromMinutes(1);
    /// <summary>The deepest level the Terrain Tiles set publishes.</summary>
    public const int MaxZoom = 15;
    public const long MaxTileBytes = 1_048_576;
    internal const string Upstream = "https://s3.amazonaws.com/elevation-tiles-prod/terrarium";

    public static IEndpointRouteBuilder MapFmsBenchTerrainEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/fms-bench/terrain/{z:int}/{x:int}/{y:int}", RelayAsync);
        return app;
    }

    public static void AddFmsBenchTerrainClient(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<UpstreamHealth>();
        services.AddHttpClient(ClientName, client =>
        {
            client.Timeout = TimeSpan.FromSeconds(10);
            // Buffered reads fail past this, so an unexpected upstream body cannot grow without limit.
            client.MaxResponseContentBufferSize = MaxTileBytes;
            client.DefaultRequestHeaders.UserAgent.ParseAdd("AeroLink-FMS-Test-Bench/1.0");
        });
    }

    /// <summary>A Web Mercator tile address that exists: 0 ≤ z ≤ 15 and x, y within 0 … 2^z − 1.</summary>
    public static bool IsValidTile(int z, int x, int y) =>
        z is >= 0 and <= MaxZoom && x >= 0 && y >= 0 && x < 1 << z && y < 1 << z;

    public static bool IsEnabled(IConfiguration configuration, IHostEnvironment environment) =>
        configuration.GetValue<bool?>(EnabledKey) ?? environment.IsDevelopment();

    private static async Task<IResult> RelayAsync(int z, int x, int y, HttpContext http, IHttpClientFactory clients,
        IConfiguration configuration, IHostEnvironment environment, UpstreamHealth health, CancellationToken ct)
    {
        if (!IsEnabled(configuration, environment))
            return Results.Json(new { error = $"Terrain data is off on this installation ({EnabledKey}).", code = "terrain_relay_disabled" },
                statusCode: StatusCodes.Status404NotFound);
        if (!IsValidTile(z, x, y))
            return Results.BadRequest(new { error = $"Terrain tile {z}/{x}/{y} does not exist: zoom is 0 to {MaxZoom}, and x and y are 0 to 2^zoom - 1." });
        if (!health.Available)
            return Results.Json(new { error = "The terrain source did not answer recently; retrying shortly.", code = "terrain_source_unavailable" },
                statusCode: StatusCodes.Status503ServiceUnavailable);
        try
        {
            using var response = await clients.CreateClient(ClientName).GetAsync($"{Upstream}/{z}/{x}/{y}.png", ct);
            if (response.StatusCode == HttpStatusCode.NotFound) return Results.NotFound();
            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentType?.MediaType != "image/png")
                return health.Failed(StatusCodes.Status502BadGateway);
            var bytes = await response.Content.ReadAsByteArrayAsync(ct);
            http.Response.Headers.CacheControl = "private, max-age=604800, immutable";
            return Results.File(bytes, "image/png");
        }
        catch (HttpRequestException) { return health.Failed(StatusCodes.Status502BadGateway); }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested) { return health.Failed(StatusCodes.Status504GatewayTimeout); }
    }

    /// <summary>When the upstream last failed, so a dead route is not retried on every tile.</summary>
    public sealed class UpstreamHealth(TimeProvider clock)
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
