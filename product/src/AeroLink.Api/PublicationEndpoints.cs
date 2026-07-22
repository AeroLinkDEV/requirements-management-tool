using System.Text.Json;
using AeroLink.Domain.Identity;
using AeroLink.Domain.Requirements;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AeroLink.Api;

public static class PublicationEndpoints
{
    public static IEndpointRouteBuilder MapAeroLinkPublicationEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/publications/jobs",CreateJobAsync);
        app.MapGet("/api/publications/jobs/{id:guid}",GetJobAsync);
        return app;
    }

    private static async Task<IResult> CreateJobAsync(CreatePublicationJobRequest request,HttpContext http,AeroLinkDbContext db,IdentityService identity,CancellationToken ct)
    {
        var document=await db.ControlledDocuments.AsNoTracking().SingleOrDefaultAsync(x=>x.Id==request.DocumentId,ct);if(document is null)return Results.NotFound();if(!await http.HasProjectRoleAsync(db,identity,document.ProjectId,ct,ProgramRole.Engineer,ProgramRole.ConfigurationManager,ProgramRole.ProgramManager))return Results.Forbid();var format=request.Format.Trim().ToLowerInvariant();if(format is not ("pdf" or "docx"))return Results.BadRequest(new{error="Controlled publications support PDF or DOCX."});if(request.PreviousDocumentId is not null&&!await db.ControlledDocuments.AnyAsync(x=>x.Id==request.PreviousDocumentId&&x.ProjectId==document.ProjectId,ct))return Results.BadRequest(new{error="The redline predecessor is not in this Project."});if(request.TemplateId is not null&&!await db.DocumentTemplates.AnyAsync(x=>x.Id==request.TemplateId&&x.ProjectId==document.ProjectId&&x.State==DocumentTemplateState.Approved,ct))return Results.BadRequest(new{error="Only an approved Project template can govern a publication."});var key=string.IsNullOrWhiteSpace(request.IdempotencyKey)?$"publication:{request.DocumentId:N}:{format}:{request.PreviousDocumentId}:{request.TemplateId}":request.IdempotencyKey.Trim();var existing=await db.EnterpriseOperationJobs.AsNoTracking().SingleOrDefaultAsync(x=>x.ProjectId==document.ProjectId&&x.IdempotencyKey==key,ct);if(existing is not null)return Results.Ok(Map(existing,true));var payload=JsonSerializer.Serialize(new PublicationJobPayload(request.DocumentId,format,request.PreviousDocumentId,request.TemplateId));var job=new EnterpriseOperationJob(document.ProjectId,"BackgroundControlledPublication",payload,1,http.UserAccount().UserName,DateTimeOffset.UtcNow,key);db.EnterpriseOperationJobs.Add(job);await db.SaveChangesAsync(ct);return Results.Accepted($"/api/publications/jobs/{job.Id}",Map(job,false));
    }
    private static async Task<IResult> GetJobAsync(Guid id,HttpContext http,AeroLinkDbContext db,CancellationToken ct)
    {var job=await db.EnterpriseOperationJobs.AsNoTracking().SingleOrDefaultAsync(x=>x.Id==id&&x.JobType=="BackgroundControlledPublication",ct);if(job is null)return Results.NotFound();if(!await http.HasProjectAccessAsync(db,job.ProjectId,ct))return Results.Forbid();return Results.Ok(Map(job,false));}
    private static object Map(EnterpriseOperationJob job,bool reused)=>new{job.Id,state=job.State.ToString(),job.ProgressPercent,job.Attempt,job.LastError,job.ResultJson,job.CreatedAt,job.UpdatedAt,job.CompletedAt,reused,downloadUrl=job.State==EnterpriseJobState.Completed?$"/api/enterprise-hardening/jobs/{job.Id}/download":null};
}

public sealed record CreatePublicationJobRequest(Guid DocumentId,string Format,Guid? PreviousDocumentId,Guid? TemplateId,string? IdempotencyKey);
