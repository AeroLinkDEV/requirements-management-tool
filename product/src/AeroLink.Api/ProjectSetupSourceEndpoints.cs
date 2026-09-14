using System.Text.Json;
using AeroLink.Domain.Common;
using AeroLink.Domain.Programs;
using AeroLink.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http.Features;

namespace AeroLink.Api;

/// <summary>Source selection, upload, mapping, and reconciliation routes for a recoverable setup draft.</summary>
public static class ProjectSetupSourceEndpoints
{
    private const long MaxUploadBytes = 50L * 1024 * 1024;

    public static void MapProjectSetupSourceEndpoints(this WebApplication app)
    {
        app.MapGet("/api/project-setups/{draftId:guid}/source", async (Guid draftId, HttpContext http,
            ProjectSetupInceptionService service, CancellationToken ct) =>
        {
            try { var view = await service.ReadSourceAsync(draftId, http.UserAccount(), ct); return view is null ? Results.NotFound() : Results.Ok(view); }
            catch (ProjectSetupAccessException) { return Results.Forbid(); }
            catch (ProjectSetupNotFoundException) { return Results.NotFound(); }
            catch (ProjectSetupInvalidException ex) { return Results.Conflict(new { code = "source_unavailable", error = ex.Message }); }
        });

        app.MapGet("/api/project-setups/source-options", async (HttpContext http, int? offset, int? limit,
            ProjectSetupInceptionService service, CancellationToken ct) =>
        {
            try { return Results.Ok(await service.ListNativeOptionsAsync(http.UserAccount(), offset ?? 0, limit ?? 50, ct)); }
            catch (ProjectSetupAccessException) { return Results.Forbid(); }
            catch (ProjectSetupInvalidException ex) { return Results.BadRequest(new { code = "invalid_source", error = ex.Message }); }
        });

        app.MapPost("/api/project-setups/{draftId:guid}/source/native", async (Guid draftId,
            CaptureNativeSourceRequest request, HttpContext http, ProjectSetupInceptionService service, CancellationToken ct) =>
        {
            try { var result = await service.CaptureNativeAsync(draftId, http.UserAccount(), request.ExpectedVersion, request.BaselineId, ct); var package = result.Package; return Results.Ok(new { package.Id, package.Stage, package.Sha256, draftVersion = result.DraftVersion }); }
            catch (ProjectSetupAccessException) { return Results.Forbid(); }
            catch (ProjectSetupNotFoundException) { return Results.NotFound(); }
            catch (ProjectSetupConcurrencyException ex) { return Results.Conflict(new { code = "draft_conflict", error = ex.Message }); }
            catch (ProjectSetupInvalidException ex) { return Results.BadRequest(new { code = "invalid_source", error = ex.Message }); }
            catch (DomainException ex) { return Results.BadRequest(new { code = "invalid_source", error = ex.Message }); }
        });

        app.MapPost("/api/project-setups/{draftId:guid}/source/upload", async (Guid draftId, HttpRequest request,
            HttpContext http, ProjectSetupInceptionService service, CancellationToken ct) =>
        {
            // Kestrel's default request limit is commonly below the product's 50 MiB source bound. Set the
            // endpoint feature before consuming the body, while the service still enforces max+1 for every
            // hosting surface (including chunked requests and TestServer).
            var sizeFeature = http.Features.Get<IHttpMaxRequestBodySizeFeature>();
            if (sizeFeature is { IsReadOnly: false }) sizeFeature.MaxRequestBodySize = MaxUploadBytes;
            if (request.ContentLength is > MaxUploadBytes)
                return Results.BadRequest(new { code = "invalid_source", error = "Source files must be between 1 byte and 50 MB." });
            if (!long.TryParse(request.Query["expectedVersion"], out var expectedVersion))
                return Results.BadRequest(new { code = "invalid_source", error = "expectedVersion is required." });
            var fileName = request.Query["fileName"].ToString();
            try { var result = await service.UploadAsync(draftId, http.UserAccount(), expectedVersion, fileName, request.Body, ct); var package = result.Package; return Results.Ok(new { package.Id, package.Stage, package.Sha256, package.SizeBytes, draftVersion = result.DraftVersion }); }
            catch (ProjectSetupAccessException) { return Results.Forbid(); }
            catch (ProjectSetupNotFoundException) { return Results.NotFound(); }
            catch (ProjectSetupConcurrencyException ex) { return Results.Conflict(new { code = "draft_conflict", error = ex.Message }); }
            catch (ProjectSetupInvalidException ex) { return Results.BadRequest(new { code = "invalid_source", error = ex.Message }); }
            catch (DomainException ex) { return Results.BadRequest(new { code = "invalid_source", error = ex.Message }); }
        });

        app.MapPut("/api/project-setups/{draftId:guid}/source/configuration", async (Guid draftId,
            ConfigureSourceRequest request, HttpContext http, ProjectSetupInceptionService service, CancellationToken ct) =>
        {
            try
            {
                var result = await service.SaveConfigurationAsync(draftId, http.UserAccount(), request.ToCommand(), ct);
                var package = result.Package;
                return Results.Ok(new { package.Id, package.Stage, package.ManifestHash, draftVersion = result.DraftVersion });
            }
            catch (ProjectSetupAccessException) { return Results.Forbid(); }
            catch (ProjectSetupNotFoundException) { return Results.NotFound(); }
            catch (ProjectSetupConcurrencyException ex) { return Results.Conflict(new { code = "draft_conflict", error = ex.Message }); }
            catch (ProjectSetupInvalidException ex) { return Results.BadRequest(new { code = "invalid_source", error = ex.Message }); }
            catch (DomainException ex) { return Results.BadRequest(new { code = "invalid_source", error = ex.Message }); }
        });

        app.MapPost("/api/project-setups/{draftId:guid}/source/reconcile", async (Guid draftId,
            ReconcileSourceRequest request, HttpContext http, ProjectSetupInceptionService service, CancellationToken ct) =>
        {
            try { var result = await service.ReconcileAsync(draftId, http.UserAccount(), request.ExpectedVersion, ct); return Results.Ok(new { reconciliation = result.Reconciliation, draftVersion = result.DraftVersion }); }
            catch (ProjectSetupAccessException) { return Results.Forbid(); }
            catch (ProjectSetupNotFoundException) { return Results.NotFound(); }
            catch (ProjectSetupConcurrencyException ex) { return Results.Conflict(new { code = "draft_conflict", error = ex.Message }); }
            catch (ProjectSetupInvalidException ex) { return Results.BadRequest(new { code = "invalid_source", error = ex.Message }); }
            catch (DomainException ex) { return Results.BadRequest(new { code = "invalid_source", error = ex.Message }); }
        });
    }
}

public sealed record CaptureNativeSourceRequest(long ExpectedVersion, Guid BaselineId);
public sealed record ReconcileSourceRequest(long ExpectedVersion);
public sealed class ConfigureSourceRequest
{
    public long ExpectedVersion { get; set; }
    public JsonElement SelectedCategories { get; set; }
    public JsonElement Mapping { get; set; }
    public JsonElement Metadata { get; set; }
    public InceptionConfigurationCommand ToCommand() => new(ExpectedVersion,
        SelectedCategories.ValueKind is JsonValueKind.Undefined ? "[]" : SelectedCategories.GetRawText(),
        Mapping.ValueKind is JsonValueKind.Undefined ? "{}" : Mapping.GetRawText(),
        Metadata.ValueKind is JsonValueKind.Undefined ? "{}" : Metadata.GetRawText());
}
