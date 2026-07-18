using System.Text.Json;
using System.Xml;
using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Common;
using AeroLink.Domain.Identity;
using AeroLink.Domain.Requirements;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

public static class ReqIfEndpoints
{
    public static IEndpointRouteBuilder MapAeroLinkReqIfEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/reqif/overview", OverviewAsync);
        app.MapPost("/api/reqif/exports", ExportAsync);
        app.MapPost("/api/reqif/imports/preview", PreviewAsync).DisableAntiforgery();
        app.MapGet("/api/reqif/jobs/{id:guid}", JobAsync);
        app.MapGet("/api/reqif/jobs/{id:guid}/download", DownloadAsync);
        app.MapPost("/api/reqif/jobs/{id:guid}/commit", CommitAsync);
        return app;
    }

    private static async Task<IResult> OverviewAsync(Guid projectId,HttpContext http,AeroLinkDbContext db,CancellationToken ct)
    {
        if(!await http.HasProjectAccessAsync(db,projectId,ct))return Results.Forbid();
        var jobs=(await db.ReqIfExchangeJobs.AsNoTracking().Where(x=>x.ProjectId==projectId).ToListAsync(ct)).OrderByDescending(x=>x.CreatedAt).Take(40).ToList();
        return Results.Ok(new{standard="ReqIF 1.2",profile="AeroLink governed round-trip subset",coverage=new[]{new{key="identity",label="Stable identities",state="Lossless"},new{key="content",label="Content + rich source",state="Lossless"},new{key="structure",label="Hierarchy",state="Lossless"},new{key="trace",label="Trace relations",state="Lossless"},new{key="schema",label="Attributes + tags",state="Lossless"},new{key="files",label="Attachment binaries",state="Lossless"}},metrics=new{exchanges=jobs.Count,exports=jobs.Count(x=>x.Direction==ReqIfExchangeDirection.Export),imports=jobs.Count(x=>x.Direction==ReqIfExchangeDirection.Import),ready=jobs.Count(x=>x.State==ReqIfExchangeState.Ready),attention=jobs.Count(x=>x.ErrorCount>0)},jobs=jobs.Select(Map)});
    }

    private static async Task<IResult> ExportAsync(ReqIfExportRequest request,HttpContext http,ReqIfExchangeService service,AeroLinkDbContext db,IdentityService identity,CancellationToken ct)
    {
        if(!await http.HasProjectRoleAsync(db,identity,request.ProjectId,ct,ProgramRole.Engineer,ProgramRole.ConfigurationManager))return Results.Forbid();
        var result=await service.ExportAsync(request.ProjectId,http.UserAccount().UserName,DateTimeOffset.UtcNow,ct);
        return Results.Created($"/api/reqif/jobs/{result.Job.Id}",new{job=Map(result.Job),downloadUrl=$"/api/reqif/jobs/{result.Job.Id}/download"});
    }

    private static async Task<IResult> PreviewAsync(Guid projectId,HttpContext http,ReqIfExchangeService service,AeroLinkDbContext db,IdentityService identity,CancellationToken ct)
    {
        if(!await http.HasProjectRoleAsync(db,identity,projectId,ct,ProgramRole.Engineer,ProgramRole.ConfigurationManager))return Results.Forbid();
        if(!http.Request.HasFormContentType)return Results.BadRequest(new{error="Use multipart form data with a .reqif or .reqifz package."});
        var form=await http.Request.ReadFormAsync(ct);var file=form.Files.GetFile("file");
        if(file is null||file.Length==0)return Results.BadRequest(new{error="Select a non-empty ReqIF package."});
        if(!file.FileName.EndsWith(".reqif",StringComparison.OrdinalIgnoreCase)&&!file.FileName.EndsWith(".reqifz",StringComparison.OrdinalIgnoreCase))return Results.BadRequest(new{error="Only .reqif and .reqifz packages are accepted."});
        try{await using var stream=file.OpenReadStream();var job=await service.PreviewImportAsync(projectId,stream,file.FileName,file.ContentType,http.UserAccount().UserName,DateTimeOffset.UtcNow,ct);return Results.Ok(new{job=Map(job),preview=ReqIfExchangeService.ReadManifest(job)});}catch(InvalidDataException ex){return Results.BadRequest(new{error=$"The ReqIF package is not a valid archive: {ex.Message}"});}catch(XmlException ex){return Results.BadRequest(new{error=$"The ReqIF XML is invalid: {ex.Message}"});}catch(InvalidOperationException ex){return Results.BadRequest(new{error=ex.Message});}
    }

    private static async Task<IResult> JobAsync(Guid id,HttpContext http,AeroLinkDbContext db,CancellationToken ct)
    {
        var job=await db.ReqIfExchangeJobs.AsNoTracking().SingleOrDefaultAsync(x=>x.Id==id,ct);if(job is null)return Results.NotFound();if(!await http.HasProjectAccessAsync(db,job.ProjectId,ct))return Results.Forbid();
        return Results.Ok(new{job=Map(job),preview=job.Direction==ReqIfExchangeDirection.Import?ReqIfExchangeService.ReadManifest(job):null});
    }

    private static async Task<IResult> DownloadAsync(Guid id,HttpContext http,AeroLinkDbContext db,ReqIfExchangeService service,CancellationToken ct)
    {
        var job=await db.ReqIfExchangeJobs.AsNoTracking().SingleOrDefaultAsync(x=>x.Id==id,ct);if(job is null)return Results.NotFound();if(!await http.HasProjectAccessAsync(db,job.ProjectId,ct))return Results.Forbid();
        return Results.File(service.OpenPackage(job),job.FileName.EndsWith(".reqifz",StringComparison.OrdinalIgnoreCase)?"application/zip":"application/xml",job.FileName,enableRangeProcessing:true);
    }

    private static async Task<IResult> CommitAsync(Guid id,CommitReqIfImportRequest request,HttpContext http,AeroLinkDbContext db,IdentityService identity,CancellationToken ct)
    {
        var job=await db.ReqIfExchangeJobs.SingleOrDefaultAsync(x=>x.Id==id,ct);if(job is null)return Results.NotFound();
        if(!await http.HasProjectRoleAsync(db,identity,job.ProjectId,ct,ProgramRole.Engineer))return Results.Forbid();
        if(job.Direction!=ReqIfExchangeDirection.Import||job.State!=ReqIfExchangeState.Ready||job.ErrorCount>0)return Results.BadRequest(new{error="This import has unresolved reconciliation errors or is no longer ready."});
        if(string.IsNullOrWhiteSpace(request.Title))return Results.BadRequest(new{error="A controlled change-package title is required."});
        if(!await db.Releases.AnyAsync(x=>x.Id==request.TargetReleaseId&&x.ProjectId==job.ProjectId,ct))return Results.BadRequest(new{error="The target release does not belong to this Project."});
        var manifest=ReqIfExchangeService.ReadManifest(job);var now=DateTimeOffset.UtcNow;var actor=http.UserAccount().UserName;
        try
        {
            var number=await IdentifierAllocator.NextChangeRequestAsync(db,request.Type,ct);var scr=new SystemChangeRequest(number,0,job.ProjectId,request.TargetReleaseId,request.Title,request.Problem,request.Analysis,request.Solution,actor,now,request.Type);
            foreach(var item in manifest.Items){if(!Enum.TryParse<RequirementLevel>(item.Level,true,out var level))level=RequirementLevel.System;scr.AddRequirementChange(actor,item.Identifier,0,level,RequirementChangeKind.Introduce,item.Statement,item.Rationale,item.VerificationMethod,now);}
            db.SystemChangeRequests.Add(scr);job.Commit(scr.Id,now);await db.SaveChangesAsync(ct);
            return Results.Created($"/api/scrs/{scr.Id}",new{scr.Id,scr.DisplayNumber,imported=manifest.Items.Count,governance="Draft change request created; approval and baseline selection remain required."});
        }
        catch(DomainException ex){return Results.BadRequest(new{error=ex.Message});}
    }

    private static object Map(ReqIfExchangeJob x)=>new{x.Id,direction=x.Direction.ToString(),state=x.State.ToString(),x.FileName,x.Sha256,x.RequirementCount,x.HierarchyCount,x.RelationCount,x.AttachmentCount,x.WarningCount,x.ErrorCount,x.CreatedBy,x.CreatedAt,x.CompletedAt,x.CreatedScrId,downloadUrl=$"/api/reqif/jobs/{x.Id}/download"};
}

public sealed record ReqIfExportRequest(Guid ProjectId);
public sealed record CommitReqIfImportRequest(Guid TargetReleaseId,string Title,string Problem,string Analysis,string Solution,ChangeRequestType Type);
