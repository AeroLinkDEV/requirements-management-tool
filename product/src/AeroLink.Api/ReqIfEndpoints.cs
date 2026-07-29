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
        app.MapGet("/api/reqif/jobs/{id:guid}/attachments", AttachmentsAsync);
        app.MapPost("/api/reqif/jobs/{id:guid}/commit", CommitAsync);
        app.MapPost("/api/reqif/jobs/{id:guid}/process", ProcessAsync);
        app.MapPost("/api/reqif/jobs/{id:guid}/cancel", CancelAsync);
        app.MapPost("/api/reqif/jobs/{id:guid}/reject", RejectAsync);
        return app;
    }

    private static async Task<IResult> OverviewAsync(Guid projectId,HttpContext http,AeroLinkDbContext db,CancellationToken ct)
    {
        if(!await http.HasProjectAccessAsync(db,projectId,ct))return Results.Forbid();
        var jobs=(await db.ReqIfExchangeJobs.AsNoTracking().Where(x=>x.ProjectId==projectId).ToListAsync(ct)).OrderByDescending(x=>x.CreatedAt).Take(40).ToList();
        return Results.Ok(new{standard="ReqIF 1.2",profile="AeroLink governed round-trip subset",coverage=new[]{new{key="identity",label="Stable identities",state="Lossless"},new{key="content",label="Content + rich source",state="Lossless"},new{key="structure",label="Hierarchy",state="Lossless"},new{key="trace",label="Trace relations",state="Lossless"},new{key="schema",label="Attributes + tags",state="Lossless"},new{key="files",label="Attachment binaries",state="Hash verified"}},metrics=new{exchanges=jobs.Count,exports=jobs.Count(x=>x.Direction==ReqIfExchangeDirection.Export),imports=jobs.Count(x=>x.Direction==ReqIfExchangeDirection.Import),ready=jobs.Count(x=>x.State==ReqIfExchangeState.Ready),attention=jobs.Count(x=>x.ErrorCount>0)},jobs=jobs.Select(Map)});
    }

    private static async Task<IResult> ExportAsync(ReqIfExportRequest request,HttpContext http,ReqIfExchangeService service,AeroLinkDbContext db,IdentityService identity,CancellationToken ct)
    {
        if(!await http.HasProjectRoleAsync(db,identity,request.ProjectId,ct,ProgramRole.Engineer,ProgramRole.ConfigurationManager))return Results.Forbid();
        var baselineId=await AeroLink.Api.BuildScope.EffectiveBaselineAsync(db,request.ProjectId,request.ReleaseId,ct);
        if(baselineId is null&&await db.Requirements.AnyAsync(x=>x.ProjectId==request.ProjectId,ct))return Results.BadRequest(new{error="The selected build has no effective requirement baseline.",code="build_baseline_unavailable"});
        var result=await service.ExportAsync(request.ProjectId,baselineId,http.UserAccount().UserName,DateTimeOffset.UtcNow,ct);
        await using var package=service.OpenPackage(result.Job);var integrity=ReqIfPackageIntegrity.Inspect(package,result.Job.FileName,result.Job.Sha256);
        return Results.Created($"/api/reqif/jobs/{result.Job.Id}",new{job=Map(result.Job),downloadUrl=$"/api/reqif/jobs/{result.Job.Id}/download",binaryIntegrity=integrity});
    }

    private static async Task<IResult> PreviewAsync(Guid projectId,HttpContext http,ReqIfExchangeService service,AeroLinkDbContext db,IdentityService identity,CancellationToken ct)
    {
        if(!await http.HasProjectRoleAsync(db,identity,projectId,ct,ProgramRole.Engineer,ProgramRole.ConfigurationManager))return Results.Forbid();
        if(!http.Request.HasFormContentType)return Results.BadRequest(new{error="Use multipart form data with a .reqif or .reqifz package."});
        var form=await http.Request.ReadFormAsync(ct);var file=form.Files.GetFile("file");
        if(file is null||file.Length==0)return Results.BadRequest(new{error="Select a non-empty ReqIF package."});
        if(!file.FileName.EndsWith(".reqif",StringComparison.OrdinalIgnoreCase)&&!file.FileName.EndsWith(".reqifz",StringComparison.OrdinalIgnoreCase))return Results.BadRequest(new{error="Only .reqif and .reqifz packages are accepted."});
        try
        {
            await using var input=new MemoryStream();await using(var source=file.OpenReadStream())await source.CopyToAsync(input,ct);input.Position=0;
            var integrity=ReqIfPackageIntegrity.Inspect(input,file.FileName);if(integrity.Warnings.Any(x=>x.Contains("does not match",StringComparison.OrdinalIgnoreCase)||x.Contains("is missing",StringComparison.OrdinalIgnoreCase)))return Results.BadRequest(new{error="The ReqIF package failed embedded-binary integrity validation.",binaryIntegrity=integrity});
            input.Position=0;var job=await service.PreviewImportAsync(projectId,input,file.FileName,file.ContentType,http.UserAccount().UserName,DateTimeOffset.UtcNow,ct);
            return Results.Ok(new{job=Map(job),preview=ReqIfExchangeService.ReadManifest(job),binaryIntegrity=integrity});
        }
        catch(InvalidDataException ex){return Results.BadRequest(new{error=$"The ReqIF package is not a valid archive: {ex.Message}"});}
        catch(XmlException ex){return Results.BadRequest(new{error=$"The ReqIF XML is invalid: {ex.Message}"});}
        catch(InvalidOperationException ex){return Results.BadRequest(new{error=ex.Message});}
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

    private static async Task<IResult> AttachmentsAsync(Guid id,HttpContext http,AeroLinkDbContext db,ReqIfExchangeService service,CancellationToken ct)
    {
        var job=await db.ReqIfExchangeJobs.AsNoTracking().SingleOrDefaultAsync(x=>x.Id==id,ct);if(job is null)return Results.NotFound();if(!await http.HasProjectAccessAsync(db,job.ProjectId,ct))return Results.Forbid();
        await using var package=service.OpenPackage(job);var integrity=ReqIfPackageIntegrity.Inspect(package,job.FileName,job.Sha256);
        return Results.Ok(new{jobId=job.Id,job.AttachmentCount,integrity,provenance=new{job.Sha256,job.StorageKey,job.CreatedBy,job.CreatedAt}});
    }

    private static async Task<IResult> CommitAsync(Guid id,CommitReqIfImportRequest request,HttpContext http,AeroLinkDbContext db,IdentityService identity,ReqIfExchangeService service,CancellationToken ct)
    {
        var job=await db.ReqIfExchangeJobs.SingleOrDefaultAsync(x=>x.Id==id,ct);if(job is null)return Results.NotFound();
        if(!await http.HasProjectRoleAsync(db,identity,job.ProjectId,ct,ProgramRole.Engineer))return Results.Forbid();
        if(job.Direction!=ReqIfExchangeDirection.Import||job.State!=ReqIfExchangeState.Ready||job.ErrorCount>0)return Results.BadRequest(new{error="This import has unresolved reconciliation errors or is no longer ready."});
        if(string.IsNullOrWhiteSpace(request.Title))return Results.BadRequest(new{error="A controlled change-package title is required."});
        if(!await db.Releases.AnyAsync(x=>x.Id==request.TargetReleaseId&&x.ProjectId==job.ProjectId,ct))return Results.BadRequest(new{error="The target release does not belong to this Project."});
        await using(var package=service.OpenPackage(job)){var integrity=ReqIfPackageIntegrity.Inspect(package,job.FileName,job.Sha256);if(!integrity.PackageVerified||integrity.Warnings.Any(x=>x.Contains("does not match",StringComparison.OrdinalIgnoreCase)||x.Contains("is missing",StringComparison.OrdinalIgnoreCase)))return Results.Conflict(new{error="The staged package no longer passes binary-integrity verification.",binaryIntegrity=integrity});}
        var manifest=ReqIfExchangeService.ReadManifest(job);var now=DateTimeOffset.UtcNow;var actor=http.UserAccount().UserName;
        try
        {
            var number=await IdentifierAllocator.NextChangeRequestAsync(db,request.Type,ct);var scr=new SystemChangeRequest(number,0,job.ProjectId,request.TargetReleaseId,request.Title,request.Problem,request.Analysis,request.Solution,actor,now,request.Type);
            foreach(var item in manifest.Items){if(!Enum.TryParse<RequirementLevel>(item.Level,true,out var level))level=RequirementLevel.System;scr.AddRequirementChange(actor,item.Identifier,0,level,RequirementChangeKind.Introduce,item.Statement,item.Rationale,item.VerificationMethod,now,impactDispositionJson:RequirementAuthoringJson.PendingImpactDispositions);}
            db.SystemChangeRequests.Add(scr);job.Commit(scr.Id,now);db.IntegrationEvents.Add(new(job.ProjectId,"aerolink.reqif.import.committed","ReqIfExchange",job.Id,JsonSerializer.Serialize(new{jobId=job.Id,scrId=scr.Id,job.Sha256,job.AttachmentCount,job.CheckpointJson,mappingProvenance=manifest.SourceTool}),actor,now,$"reqif-commit:{job.Id:N}"));await db.SaveChangesAsync(ct);
            return Results.Created($"/api/scrs/{scr.Id}",new{scr.Id,scr.DisplayNumber,imported=manifest.Items.Count,attachments=job.AttachmentCount,packageHash=job.Sha256,governance="Draft change request created; approval and baseline selection remain required."});
        }
        catch(DomainException ex){return Results.BadRequest(new{error=ex.Message});}
    }

    private static async Task<IResult> RejectAsync(Guid id,HttpContext http,AeroLinkDbContext db,IdentityService identity,CancellationToken ct)
    {var job=await db.ReqIfExchangeJobs.SingleOrDefaultAsync(x=>x.Id==id,ct);if(job is null)return Results.NotFound();if(!await http.HasProjectRoleAsync(db,identity,job.ProjectId,ct,ProgramRole.Engineer,ProgramRole.ConfigurationManager))return Results.Forbid();try{job.Reject(DateTimeOffset.UtcNow);db.SecurityAuditEvents.Add(new("ReqIfExchangeRejected",http.UserAccount().UserName,$"reqif:{job.Id}","Success","Operator rejected the staged exchange; no controlled artifact was mutated.",http.Connection.RemoteIpAddress?.ToString()??"local",DateTimeOffset.UtcNow));await db.SaveChangesAsync(ct);return Results.Ok(new{job.Id,state=job.State.ToString()});}catch(DomainException ex){return Results.BadRequest(new{error=ex.Message});}}

    private static async Task<IResult> ProcessAsync(Guid id,ProcessReqIfJobRequest request,HttpContext http,AeroLinkDbContext db,IdentityService identity,CancellationToken ct)
    {var job=await db.ReqIfExchangeJobs.SingleOrDefaultAsync(x=>x.Id==id,ct);if(job is null)return Results.NotFound();if(!await http.HasProjectRoleAsync(db,identity,job.ProjectId,ct,ProgramRole.Engineer,ProgramRole.ConfigurationManager))return Results.Forbid();if(job.Direction!=ReqIfExchangeDirection.Import)return Results.BadRequest(new{error="Only imports require checkpointed validation."});try{if(job.State!=ReqIfExchangeState.Processing)job.BeginOrResume(DateTimeOffset.UtcNow);var manifest=ReqIfExchangeService.ReadManifest(job);var size=Math.Clamp(request.BatchSize??100,1,1000);var next=Math.Min(manifest.Items.Count,job.ProcessedCount+size);var batch=manifest.Items.Skip(job.ProcessedCount).Take(size).Select(x=>x.ExternalId).ToList();var checkpoint=JsonSerializer.Serialize(new{processed=next,total=manifest.Items.Count,lastExternalId=batch.LastOrDefault(),packageHash=job.Sha256,attachmentCount=job.AttachmentCount});job.Checkpoint(next,checkpoint,DateTimeOffset.UtcNow);await db.SaveChangesAsync(ct);return Results.Ok(new{job=Map(job),complete=job.State==ReqIfExchangeState.Ready,remaining=Math.Max(0,manifest.Items.Count-next)});}catch(DomainException ex){return Results.BadRequest(new{error=ex.Message});}}

    private static async Task<IResult> CancelAsync(Guid id,HttpContext http,AeroLinkDbContext db,IdentityService identity,CancellationToken ct)
    {var job=await db.ReqIfExchangeJobs.SingleOrDefaultAsync(x=>x.Id==id,ct);if(job is null)return Results.NotFound();if(!await http.HasProjectRoleAsync(db,identity,job.ProjectId,ct,ProgramRole.Engineer,ProgramRole.ConfigurationManager))return Results.Forbid();try{job.Cancel(DateTimeOffset.UtcNow);await db.SaveChangesAsync(ct);return Results.Ok(new{job=Map(job)});}catch(DomainException ex){return Results.BadRequest(new{error=ex.Message});}}

    private static object Map(ReqIfExchangeJob x)=>new{x.Id,direction=x.Direction.ToString(),state=x.State.ToString(),x.FileName,x.Sha256,x.RequirementCount,x.HierarchyCount,x.RelationCount,x.AttachmentCount,x.WarningCount,x.ErrorCount,x.ProcessedCount,x.CheckpointJson,x.Attempt,x.LastError,x.CreatedBy,x.CreatedAt,x.CompletedAt,x.CreatedScrId,downloadUrl=$"/api/reqif/jobs/{x.Id}/download",attachmentsUrl=$"/api/reqif/jobs/{x.Id}/attachments"};
}

public sealed record ReqIfExportRequest(Guid ProjectId,Guid ReleaseId);
public sealed record CommitReqIfImportRequest(Guid TargetReleaseId,string Title,string Problem,string Analysis,string Solution,ChangeRequestType Type);
public sealed record ProcessReqIfJobRequest(int? BatchSize);
