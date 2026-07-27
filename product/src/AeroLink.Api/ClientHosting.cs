using Microsoft.Extensions.FileProviders;

namespace AeroLink.Api;

/// <summary>
/// Serves the built client from the API process, so there is a way to run AeroLink that is not the Vite dev
/// server.
///
/// There had not been one. `START_AEROLINK.bat` ran `npm run dev`, the browser journeys ran `npm run dev`, and
/// nothing anywhere served <c>client/dist</c> — so the production bundle had been compiled by CI and never
/// once rendered in a browser, on any platform. The demonstration brief calls a dry run from a production
/// build the one preparation that cannot be skipped, and the environment it named did not exist.
///
/// Serving the client from the API is also the right shape for an on-premises deployment: one process, one
/// port, one origin. Same-origin means no CORS policy to get wrong, no second server to supervise, and one
/// place for a reverse proxy to terminate TLS.
///
/// This is additive. With no built client present nothing here activates and the API behaves exactly as it
/// did, which is what a deployment serving the client through its own proxy still relies on.
/// </summary>
public static class ClientHosting
{
    /// <summary>
    /// What the API's own responses may do: nothing. An API returns data, never a document.
    /// </summary>
    public const string ApiContentSecurityPolicy =
        "default-src 'none'; frame-ancestors 'none'; base-uri 'none'; sandbox";

    /// <summary>
    /// What the client document may do, which is the same list DEC-047 already committed to — everything
    /// comes from this origin and nothing comes from anywhere else. The client makes no external request at
    /// runtime, and stating that as a policy makes the browser enforce it rather than leaving it as a property
    /// somebody has to keep remembering: a CDN reference reintroduced by accident fails visibly here.
    ///
    /// <c>'unsafe-inline'</c> on styles is required and is the one concession. Eight places set a style
    /// attribute to carry a measured value into CSS — a progress width, a readiness angle — and an attribute
    /// cannot carry a nonce. It buys an attacker no script execution, and authored content never becomes
    /// markup in the first place: it is stored as structure and reaches the DOM as escaped text nodes, so
    /// there is no injection point to pair it with. Scripts stay <c>'self'</c> with no inline allowance.
    /// </summary>
    /// <remarks>
    /// <c>data:</c> is allowed for fonts and images because the build inlines assets below its size threshold
    /// straight into the stylesheet. A data URI is bytes already in the document and not a request to anywhere,
    /// so it is no part of what DEC-047 forbids — but omitting it is not a harmless omission: with
    /// <c>font-src 'self'</c> alone the browser blocked eight of the self-hosted typefaces and the client fell
    /// back to a system font. The production journey caught that on its first run.
    /// </remarks>
    public const string DocumentContentSecurityPolicy =
        "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; " +
        "font-src 'self' data:; connect-src 'self'; object-src 'none'; frame-src 'none'; worker-src 'self'; " +
        "form-action 'self'; frame-ancestors 'none'; base-uri 'none'";

    /// <summary>
    /// Paths this process answers as an API rather than as the client, and which therefore keep the strict
    /// policy above and must 404 as JSON rather than falling through to the single-page application.
    /// </summary>
    public static bool IsApiPath(PathString path)
    {
        var value = path.Value ?? "";
        return value.Equals("/api", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("/api/", StringComparison.OrdinalIgnoreCase)
            || value.Equals("/health", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("/health/", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Where the built client is, or null when this deployment does not serve one.
    ///
    /// An index.html is the test, never the directory: `wwwroot` exists in plenty of API projects that serve no
    /// client, and a `wwwroot` left empty by an interrupted build must not read as a deployment.
    ///
    /// `product/client/dist` is deliberately *not* searched for. Both launchers and the production journeys name
    /// it through <c>Client:StaticFiles</c>, so discovering it would buy nothing and cost something real: an
    /// ordinary `dotnet run` would then serve whatever build happened to be left in the working tree, and a
    /// stale bundle served silently is worse than no bundle served at all.
    ///
    /// Both roots are checked because the content root depends on how the process was started — the project
    /// directory under `dotnet run --project`, the working directory under `dotnet AeroLink.Api.dll`.
    /// </summary>
    public static string? ResolveClientRoot(IWebHostEnvironment environment, IConfiguration configuration)
    {
        var configured = configuration["Client:StaticFiles"];
        if (!string.IsNullOrWhiteSpace(configured))
        {
            var explicitRoot = Path.GetFullPath(configured);
            // Explicitly configured and absent is a misconfiguration, not an opt-out. Falling through to the
            // conventions here would silently serve a different client than the operator named.
            if (!File.Exists(Path.Combine(explicitRoot, "index.html")))
                throw new InvalidOperationException($"Client:StaticFiles is set to '{explicitRoot}', which contains no index.html.");
            return explicitRoot;
        }

        // A published deployment: `dotnet publish` puts wwwroot beside the assembly.
        foreach (var root in new[] { AppContext.BaseDirectory, environment.ContentRootPath })
        {
            var published = Path.Combine(root, "wwwroot");
            if (File.Exists(Path.Combine(published, "index.html"))) return published;
        }
        return null;
    }

    /// <summary>
    /// Serves the client's files, then falls back to its entry document so a deep link reloads.
    /// </summary>
    public static void UseAeroLinkClient(this WebApplication app, string clientRoot)
    {
        var files = new PhysicalFileProvider(clientRoot);
        app.UseStaticFiles(new StaticFileOptions
        {
            FileProvider = files,
            OnPrepareResponse = context =>
            {
                // Everything under /assets carries a content hash in its name, so it can be cached forever and
                // a new build is a new URL. Everything else — the entry document, the favicon, the two files
                // in public/ — is served under a stable name, so it must be revalidated or an upgraded
                // deployment would keep handing out the previous release's HTML.
                var immutable = context.Context.Request.Path.StartsWithSegments("/assets");
                context.Context.Response.Headers.CacheControl =
                    immutable ? "public, max-age=31536000, immutable" : "no-cache";
            },
        });

        app.MapFallback(async context =>
        {
            // A single-page application answers its own routes, so any path that matched no file and no
            // endpoint is a client route and gets the entry document. An unmatched API path is a mistake and
            // must say so: returning HTML with status 200 to a caller that asked for JSON turns a typo into a
            // parse error somewhere far away from the cause.
            if (IsApiPath(context.Request.Path))
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                await context.Response.WriteAsJsonAsync(new { error = "No such endpoint.", code = "endpoint_not_found" });
                return;
            }
            context.Response.ContentType = "text/html; charset=utf-8";
            context.Response.Headers.CacheControl = "no-cache";
            await context.Response.SendFileAsync(Path.Combine(clientRoot, "index.html"));
        });
    }
}
