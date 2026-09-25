using System.Text.Json;
using AeroLink.Domain.Common;
using AeroLink.Domain.Identity;
using AeroLink.Domain.Programs;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AeroLink.Api;

/// <summary>
/// Problem Report import from another tool's CSV/XLSX export (#1114). Preview reconciles every row against
/// the mapping; commit repeats it, refuses unless the file and mapping still hash to what was previewed, and
/// records the importer's password-confirmed signature over that hash. Import is Project Configuration
/// authority (Configuration Manager, Program Manager or Administrator) and needs Problem Reports switched on.
/// </summary>
public static class ProblemReportImportEndpoints
{
    private static readonly JsonSerializerOptions MappingJson = new(JsonSerializerDefaults.Web);

    public static void MapProblemReportImportEndpoints(this WebApplication app)
    {
        app.MapPost("/api/problem-reports/import/preview", async (HttpContext http, AeroLinkDbContext db,
            IdentityService identity, ProblemReportImportService service, CancellationToken ct) =>
        {
            var form = await ReadAsync(http, db, identity, ct);
            if (form.Refusal is not null) return form.Refusal;
            try
            {
                var preview = await service.PreviewAsync(form.ProjectId, form.Bytes, form.FileName, form.Mapping, http.UserAccount().UserName, ct);
                return Results.Ok(preview);
            }
            catch (Exception ex) when (ex is DomainException or InvalidOperationException) { return Results.BadRequest(new { error = ex.Message }); }
        }).DisableAntiforgery();

        app.MapPost("/api/problem-reports/import/commit", async (HttpContext http, AeroLinkDbContext db,
            IdentityService identity, ProblemReportImportService service, CancellationToken ct) =>
        {
            var form = await ReadAsync(http, db, identity, ct);
            if (form.Refusal is not null) return form.Refusal;
            var actor = http.UserAccount();
            if (!await identity.ConfirmPasswordAsync(actor.Id, form.Password, ct))
                return Results.Json(new { error = "Electronic signature confirmation failed." }, statusCode: 401);
            try
            {
                var now = DateTimeOffset.UtcNow;
                var (batch, reports) = await service.CommitAsync(form.ProjectId, form.Bytes, form.FileName, form.Mapping,
                    form.PreviewHash, actor.UserName, actor.DisplayName, now, ct);
                var programId = await db.Projects.Where(x => x.Id == form.ProjectId).Select(x => x.ProgramId).SingleAsync(ct);
                db.ElectronicSignatures.Add(new ElectronicSignature(actor.Id, actor.UserName, actor.DisplayName, programId,
                    "ProblemReportImportBatch", batch.Id, batch.FileName, "ImportProblemReports",
                    "Accepted the source provenance and mapping of this import", batch.PreviewHash,
                    http.Connection.RemoteIpAddress?.ToString() ?? "local", now));
                await db.SaveChangesAsync(ct);
                return Results.Ok(new { batchId = batch.Id, batch.Created, batch.Skipped, reports = reports.Select(x => new { x.Id, x.DisplayNumber, x.SourceKey }) });
            }
            catch (Exception ex) when (ex is DomainException or InvalidOperationException) { return Results.BadRequest(new { error = ex.Message }); }
            catch (DbUpdateException) { return Results.Conflict(new { error = "Problem Reports changed during the import. Preview again and retry." }); }
        }).DisableAntiforgery();

        app.MapGet("/api/problem-reports/import/batches", async (Guid projectId, HttpContext http, AeroLinkDbContext db, CancellationToken ct) =>
        {
            if (!await http.HasProjectAccessAsync(db, projectId, ct)) return Results.Forbid();
            var batches = (await db.ProblemReportImportBatches.AsNoTracking().Where(x => x.ProjectId == projectId).ToListAsync(ct))
                .OrderByDescending(x => x.ImportedAt).Select(x => new { x.Id, x.SourceSystem, x.FileName, x.SourceHash, x.Created, x.Skipped, x.ImportedBy, x.ImportedAt });
            return Results.Ok(batches);
        });
    }

    private sealed record ImportForm(Guid ProjectId, byte[] Bytes, string FileName, ProblemReportImportMapping Mapping,
        string PreviewHash, string Password, IResult? Refusal);

    private static async Task<ImportForm> ReadAsync(HttpContext http, AeroLinkDbContext db, IdentityService identity, CancellationToken ct)
    {
        static ImportForm Refuse(IResult result) => new(Guid.Empty, [], "", new(), "", "", result);
        if (!http.Request.HasFormContentType) return Refuse(Results.BadRequest(new { error = "Send the import as a form with a file." }));
        var form = await http.Request.ReadFormAsync(ct);
        if (!Guid.TryParse(form["projectId"], out var projectId)) return Refuse(Results.BadRequest(new { error = "Choose the project." }));
        if (!await http.HasProjectRoleAsync(db, identity, projectId, ct,
                ProgramRole.ConfigurationManager, ProgramRole.ProgramManager, ProgramRole.Administrator))
            return Refuse(Results.Forbid());
        if (!(await ProjectFeatureService.EffectiveAsync(db, projectId, ct)).HasFlag(ProjectFeature.ProblemReports))
            return Refuse(Results.Conflict(new { error = "Problem Reports are not enabled for this project.", code = "feature_disabled" }));
        var file = form.Files.GetFile("file");
        if (file is null || file.Length == 0) return Refuse(Results.BadRequest(new { error = "Choose a .csv or .xlsx file." }));
        if (file.Length > ProblemReportImportService.MaximumBytes) return Refuse(Results.BadRequest(new { error = "Problem Report imports are limited to 20 MB." }));
        ProblemReportImportMapping mapping;
        try { mapping = JsonSerializer.Deserialize<ProblemReportImportMapping>(form["mapping"].ToString(), MappingJson) ?? new(); }
        catch (JsonException) { return Refuse(Results.BadRequest(new { error = "The mapping is not valid." })); }
        using var buffer = new MemoryStream();
        await file.CopyToAsync(buffer, ct);
        return new(projectId, buffer.ToArray(), file.FileName, mapping, form["previewHash"].ToString(), form["password"].ToString(), null);
    }
}
