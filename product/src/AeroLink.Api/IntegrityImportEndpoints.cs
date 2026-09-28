using System.Text.Json;
using AeroLink.Domain.Common;
using AeroLink.Domain.Identity;
using AeroLink.Domain.Programs;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AeroLink.Api;

public static class IntegrityImportEndpoints
{
    private const string Root = "/api/problem-reports/integrity-import";
    public static void MapIntegrityImportEndpoints(this WebApplication app)
    {
        app.MapPost("/api/problem-reports/integrity-import/preview", async (HttpContext http, AeroLinkDbContext db, IdentityService identity,
            IntegrityImportService service, IConfiguration configuration, CancellationToken ct) =>
        {
            try
            {
                var form = await ReadAsync(http, db, identity, configuration, ct);
                if (form.Refusal is not null) return form.Refusal;
                return Results.Ok(await service.PreviewAsync(form.ProjectId, form.Package!, form.Mapping!, http.UserAccount().UserName, ct));
            }
            catch (Exception ex) when (ex is DomainException or JsonException or InvalidDataException)
            { return Results.BadRequest(new { error = ex.Message }); }
        }).DisableAntiforgery();
        app.MapPost("/api/problem-reports/integrity-import/commit", async (HttpContext http, AeroLinkDbContext db, IdentityService identity,
            IntegrityImportService service, IConfiguration configuration, CancellationToken ct) =>
        {
            try
            {
                var form = await ReadAsync(http, db, identity, configuration, ct);
                if (form.Refusal is not null) return form.Refusal;
                var actor = http.UserAccount();
                if (!await identity.ConfirmPasswordAsync(actor.Id, form.Form!["password"].ToString(), ct))
                    return Results.Json(new { error = "Signature confirmation failed." }, statusCode: 401);
                if (!Guid.TryParse(form.Form!["operationId"], out var operationId))
                    return Results.BadRequest(new { error = "An import operation identity is required." });
                return Results.Ok(await service.CommitAsync(form.ProjectId, operationId, form.Bytes!, form.Package!, form.Mapping!,
                    form.Form["previewHash"].ToString(), actor, http.Connection.RemoteIpAddress?.ToString() ?? "local",
                    () => AuthorizedAsync(http, db, identity, form.ProjectId, ct), ct));
            }
            catch (Exception ex) when (ex is IntegrityImportConflict or ManagedDocumentStorageConflictException)
            { return Results.Conflict(new { error = ex.Message, code = "integrity_import_conflict" }); }
            catch (Exception ex) when (IsConcurrency(ex))
            { return Results.Conflict(new { error = "Another import changed the destination. Retry the same operation or review again.", code = "integrity_import_concurrency" }); }
            catch (Exception ex) when (ex is DomainException or JsonException or InvalidDataException)
            { return Results.BadRequest(new { error = ex.Message }); }
        }).DisableAntiforgery();
        app.MapGet(Root + "/batches", async (Guid projectId, HttpContext http, AeroLinkDbContext db, IdentityService identity, CancellationToken ct) =>
        {
            if (!await AuthorizedAsync(http, db, identity, projectId, ct)) return Results.Forbid();
            var rows = await db.IntegrityImportBatches.AsNoTracking().Where(x => x.ProjectId == projectId).ToListAsync(ct);
            return Results.Ok(rows.OrderByDescending(x => x.ImportedAt).Select(x => new { x.Id, x.ManifestHash, x.ImportedBy, x.ImportedAt,
                receipt = JsonSerializer.Deserialize<JsonElement>(x.ReceiptJson) }));
        });
        app.MapGet(Root + "/batches/{id:guid}", async (Guid id, HttpContext http, AeroLinkDbContext db, IdentityService identity, CancellationToken ct) =>
        {
            var batch = await db.IntegrityImportBatches.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
            if (batch is null) return Results.NotFound();
            if (!await AuthorizedAsync(http, db, identity, batch.ProjectId, ct)) return Results.Forbid();
            return Results.Ok(new { batch.Id, batch.ManifestHash, batch.PreviewHash, batch.ImportedBy, batch.ImportedAt,
                reconciliation = JsonSerializer.Deserialize<JsonElement>(batch.ReconciliationJson), receipt = JsonSerializer.Deserialize<JsonElement>(batch.ReceiptJson) });
        });
        app.MapGet(Root + "/batches/{id:guid}/package", async (Guid id, HttpContext http, AeroLinkDbContext db,
            IdentityService identity, EvidenceFileStore files, CancellationToken ct) =>
        {
            var batch = await db.IntegrityImportBatches.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
            if (batch is null) return Results.NotFound();
            if (!await AuthorizedAsync(http, db, identity, batch.ProjectId, ct)) return Results.Forbid();
            var attachment = await db.ControlledAttachments.AsNoTracking().SingleAsync(x => x.Id == batch.PackageAttachmentId, ct);
            try { return Results.File(await files.OpenVerifiedReadAsync(attachment.StorageKey, attachment.Size, attachment.Sha256, ct),
                "application/zip", $"integrity-source-{id:N}.zip", enableRangeProcessing: true); }
            catch (EvidenceIntegrityException) { return Results.Conflict(new { error = "The preserved package failed its integrity check. Controlled recovery is required." }); }
        });
        app.MapGet(Root + "/batches/{id:guid}/member", async (Guid id, string path, HttpContext http, AeroLinkDbContext db,
            IdentityService identity, EvidenceFileStore files, IConfiguration configuration, CancellationToken ct) =>
        {
            var batch = await db.IntegrityImportBatches.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
            if (batch is null) return Results.NotFound();
            if (!await AuthorizedAsync(http, db, identity, batch.ProjectId, ct)) return Results.Forbid();
            var attachment = await db.ControlledAttachments.AsNoTracking().SingleAsync(x => x.Id == batch.PackageAttachmentId, ct);
            try
            {
                await using var stream = await files.OpenVerifiedReadAsync(attachment.StorageKey, attachment.Size, attachment.Sha256, ct);
                using var buffer = new MemoryStream(); await stream.CopyToAsync(buffer, ct);
                var package = IntegritySourcePackage.Read(buffer.ToArray(), batch.ManifestHash, configuration.GetValue<bool>("IntegrityImport:AllowFixtures"));
                return package.Members.TryGetValue(path, out var bytes)
                    ? Results.File(bytes, "application/octet-stream", Path.GetFileName(path)) : Results.NotFound();
            }
            catch (Exception ex) when (ex is EvidenceIntegrityException or DomainException)
            { return Results.Conflict(new { error = "The preserved package cannot be verified." }); }
        });
    }
    public static async Task<bool> AuthorizedAsync(HttpContext http, AeroLinkDbContext db, IdentityService identity, Guid projectId, CancellationToken ct) =>
        await http.HasProjectRoleAsync(db, identity, projectId, ct, ProgramRole.ConfigurationManager, ProgramRole.ProgramManager, ProgramRole.Administrator)
        && (await ProjectFeatureService.EffectiveAsync(db, projectId, ct)).HasFlag(ProjectFeature.ProblemReports);

    private sealed record Input(Guid ProjectId, IFormCollection? Form = null, byte[]? Bytes = null,
        IntegritySourcePackage? Package = null, ProblemReportImportMapping? Mapping = null, IResult? Refusal = null);
    private static async Task<Input> ReadAsync(HttpContext http, AeroLinkDbContext db, IdentityService identity,
        IConfiguration configuration, CancellationToken ct)
    {
        if (!http.Request.HasFormContentType) throw new DomainException("Upload an Integrity source package.");
        var bodyLimit = http.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>();
        if (bodyLimit is { IsReadOnly: false }) bodyLimit.MaxRequestBodySize = IntegritySourcePackage.MaximumBytes + 1024 * 1024;
        var form = await http.Request.ReadFormAsync(ct);
        if (!Guid.TryParse(form["projectId"], out var projectId)) throw new DomainException("Choose the destination project.");
        if (!await AuthorizedAsync(http, db, identity, projectId, ct)) return new(projectId, Refusal: Results.Forbid());
        var file = form.Files.GetFile("file");
        if (file is null || file.Length is 0 or > IntegritySourcePackage.MaximumBytes) throw new DomainException("Choose an Integrity package of at most 50 MB.");
        if (form["mapping"].ToString().Length > 500_000) throw new DomainException("The mapping is too large.");
        var mapping = JsonSerializer.Deserialize<ProblemReportImportMapping>(form["mapping"].ToString(), IntegritySourcePackage.Json)
            ?? throw new DomainException("Choose the field mapping.");
        using var buffer = new MemoryStream(); await file.CopyToAsync(buffer, ct);
        var bytes = buffer.ToArray();
        return new(projectId, form, bytes, IntegritySourcePackage.Read(bytes, form["manifestHash"].ToString(),
            configuration.GetValue<bool>("IntegrityImport:AllowFixtures")), mapping);
    }
    private static bool IsConcurrency(Exception exception) => exception.GetBaseException() switch
    {
        Npgsql.PostgresException { SqlState: "40001" or "40P01" } => true,
        Npgsql.PostgresException { SqlState: "23505", ConstraintName: { } name } when name.Contains("integrity_", StringComparison.Ordinal) => true,
        Microsoft.Data.Sqlite.SqliteException { SqliteErrorCode: 5 or 6 } => true,
        _ => false,
    };
}
