using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AeroLink.Domain.Common;
using AeroLink.Domain.Identity;
using AeroLink.Domain.Requirements;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AeroLink.Api;

public static class ProductLineEndpoints
{
    public static IEndpointRouteBuilder MapProductLineConfigurationEndpoints(this IEndpointRouteBuilder app)
    {
        var group=app.MapGroup("/api/product-line");
        group.MapGet("/overview",OverviewAsync);
        group.MapPost("/components",CreateComponentAsync);
        group.MapPost("/components/{id:guid}/streams",CreateStreamAsync);
        group.MapPost("/streams/{id:guid}/revisions",PublishStreamRevisionAsync);
        group.MapPost("/variants",CreateVariantAsync);
        group.MapPost("/variants/{id:guid}/components",SelectComponentAsync);
        group.MapPost("/variants/{id:guid}/approve",ApproveVariantAsync);
        group.MapGet("/variants/{id:guid}/manifest",VariantManifestAsync);
        return app;
    }

    private static async Task<IResult> OverviewAsync(Guid projectId,HttpContext http,AeroLinkDbContext db,CancellationToken ct)
    {
        if(!await http.HasProjectAccessAsync(db,projectId,ct))return Results.Forbid();
        var components=await db.ProductLineComponents.AsNoTracking().Where(x=>x.ProjectId==projectId).OrderBy(x=>x.ComponentNumber).ToListAsync(ct);
        var variants=await db.ProductVariants.AsNoTracking().Where(x=>x.ProjectId==projectId).OrderBy(x=>x.VariantKey).ToListAsync(ct);
        var componentIds=components.Select(x=>x.Id).ToList();var streams=await db.ComponentStreams.AsNoTracking().Where(x=>componentIds.Contains(x.ComponentId)).ToListAsync(ct);var streamIds=streams.Select(x=>x.Id).ToList();var revisions=await db.ComponentStreamRevisions.AsNoTracking().Where(x=>streamIds.Contains(x.StreamId)).ToListAsync(ct);
        return Results.Ok(new{components=components.Select(x=>new{x.Id,x.ComponentNumber,x.Name,state=x.State.ToString(),x.Version,streamCount=streams.Count(s=>s.ComponentId==x.Id)}),variants=variants.Select(x=>new{x.Id,x.VariantKey,x.Name,state=x.State.ToString(),x.ApplicabilityJson,x.Version,componentCount=db.VariantComponentSelections.Count(s=>s.VariantId==x.Id)}),streams=streams.Select(x=>new{x.Id,x.ComponentId,x.StreamKey,x.Name,state=x.State.ToString(),revisionCount=revisions.Count(r=>r.StreamId==x.Id)})});
    }
    private static async Task<IResult> CreateComponentAsync(CreateComponentRequest request,HttpContext http,AeroLinkDbContext db,IdentityService identity,CancellationToken ct)
    { if(!await http.HasProjectRoleAsync(db,identity,request.ProjectId,ct,ProgramRole.Engineer,ProgramRole.ConfigurationManager))return Results.Forbid();try{var item=new ProductLineComponent(request.ProjectId,request.ComponentNumber,request.Name,request.Description??"",http.UserAccount().UserName,DateTimeOffset.UtcNow);db.ProductLineComponents.Add(item);await db.SaveChangesAsync(ct);return Results.Created($"/api/product-line/components/{item.Id}",new{item.Id,item.ComponentNumber});}catch(DomainException ex){return Results.BadRequest(new{error=ex.Message});}catch(DbUpdateException){return Results.Conflict(new{error="A component with this number already exists."});} }
    private static async Task<IResult> CreateStreamAsync(Guid id,CreateStreamRequest request,HttpContext http,AeroLinkDbContext db,IdentityService identity,CancellationToken ct)
    {var component=await db.ProductLineComponents.SingleOrDefaultAsync(x=>x.Id==id,ct);if(component is null)return Results.NotFound();if(!await http.HasProjectRoleAsync(db,identity,component.ProjectId,ct,ProgramRole.Engineer,ProgramRole.ConfigurationManager))return Results.Forbid();try{var item=new ComponentStream(id,request.StreamKey,request.Name,http.UserAccount().UserName,DateTimeOffset.UtcNow);db.ComponentStreams.Add(item);await db.SaveChangesAsync(ct);return Results.Created($"/api/product-line/streams/{item.Id}",new{item.Id,item.StreamKey});}catch(DomainException ex){return Results.BadRequest(new{error=ex.Message});}catch(DbUpdateException){return Results.Conflict(new{error="This component already has that stream key."});}}
    private static async Task<IResult> PublishStreamRevisionAsync(Guid id,PublishComponentRevisionRequest request,HttpContext http,AeroLinkDbContext db,IdentityService identity,CancellationToken ct)
    {var stream=await db.ComponentStreams.SingleOrDefaultAsync(x=>x.Id==id,ct);if(stream is null)return Results.NotFound();var component=await db.ProductLineComponents.SingleAsync(x=>x.Id==stream.ComponentId,ct);if(!await http.HasProjectRoleAsync(db,identity,component.ProjectId,ct,ProgramRole.Engineer,ProgramRole.ConfigurationManager))return Results.Forbid();try{using var _=JsonDocument.Parse(request.ContentJson);var next=(await db.ComponentStreamRevisions.Where(x=>x.StreamId==id).MaxAsync(x=>(int?)x.Revision,ct)??0)+1;var canonical=JsonSerializer.Serialize(JsonDocument.Parse(request.ContentJson).RootElement);var hash=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();var revision=new ComponentStreamRevision(id,next,canonical,hash,http.UserAccount().UserName,DateTimeOffset.UtcNow);db.ComponentStreamRevisions.Add(revision);component.Approve(http.UserAccount().UserName,DateTimeOffset.UtcNow);await db.SaveChangesAsync(ct);return Results.Created($"/api/product-line/streams/{id}/revisions/{revision.Id}",new{revision.Id,revision.Revision,revision.ManifestHash});}catch(JsonException){return Results.BadRequest(new{error="Component content must be valid JSON."});}catch(DomainException ex){return Results.BadRequest(new{error=ex.Message});}}
    private static async Task<IResult> CreateVariantAsync(CreateVariantRequest request,HttpContext http,AeroLinkDbContext db,IdentityService identity,CancellationToken ct)
    {if(!await http.HasProjectRoleAsync(db,identity,request.ProjectId,ct,ProgramRole.Engineer,ProgramRole.ConfigurationManager))return Results.Forbid();try{using var _=JsonDocument.Parse(string.IsNullOrWhiteSpace(request.ApplicabilityJson)?"{}":request.ApplicabilityJson);var item=new ProductVariant(request.ProjectId,request.VariantKey,request.Name,request.ApplicabilityJson??"{}",http.UserAccount().UserName,DateTimeOffset.UtcNow);db.ProductVariants.Add(item);await db.SaveChangesAsync(ct);return Results.Created($"/api/product-line/variants/{item.Id}",new{item.Id,item.VariantKey});}catch(JsonException){return Results.BadRequest(new{error="Applicability must be valid JSON."});}catch(DomainException ex){return Results.BadRequest(new{error=ex.Message});}catch(DbUpdateException){return Results.Conflict(new{error="A variant with this key already exists."});}}
    private static async Task<IResult> SelectComponentAsync(Guid id,SelectVariantComponentRequest request,HttpContext http,AeroLinkDbContext db,IdentityService identity,CancellationToken ct)
    {var variant=await db.ProductVariants.SingleOrDefaultAsync(x=>x.Id==id,ct);if(variant is null)return Results.NotFound();if(!await http.HasProjectRoleAsync(db,identity,variant.ProjectId,ct,ProgramRole.ConfigurationManager))return Results.Forbid();var revision=await (from r in db.ComponentStreamRevisions join s in db.ComponentStreams on r.StreamId equals s.Id join c in db.ProductLineComponents on s.ComponentId equals c.Id where r.Id==request.ComponentRevisionId select new{r,c.ProjectId,c.State}).SingleOrDefaultAsync(ct);if(revision is null||revision.ProjectId!=variant.ProjectId)return Results.BadRequest(new{error="The selected component revision is not in this Project."});if(revision.State!=ProductLineComponentState.Approved)return Results.Conflict(new{error="Only an approved reusable component can be selected."});try{using var _=JsonDocument.Parse(string.IsNullOrWhiteSpace(request.ApplicabilityJson)?"{}":request.ApplicabilityJson);var selection=new VariantComponentSelection(id,request.ComponentRevisionId,request.ApplicabilityJson??"{}",http.UserAccount().UserName,DateTimeOffset.UtcNow);db.VariantComponentSelections.Add(selection);await db.SaveChangesAsync(ct);return Results.Created($"/api/product-line/variants/{id}/components/{selection.Id}",new{selection.Id});}catch(JsonException){return Results.BadRequest(new{error="Selection applicability must be valid JSON."});}catch(DbUpdateException){return Results.Conflict(new{error="That immutable component revision is already selected for this variant."});}}
    private static async Task<IResult> ApproveVariantAsync(Guid id,HttpContext http,AeroLinkDbContext db,IdentityService identity,CancellationToken ct)
    {var item=await db.ProductVariants.SingleOrDefaultAsync(x=>x.Id==id,ct);if(item is null)return Results.NotFound();if(!await http.HasProjectRoleAsync(db,identity,item.ProjectId,ct,ProgramRole.ConfigurationManager))return Results.Forbid();if(!await db.VariantComponentSelections.AnyAsync(x=>x.VariantId==id,ct))return Results.BadRequest(new{error="Select at least one immutable component revision before approving a variant."});item.Approve(DateTimeOffset.UtcNow);await db.SaveChangesAsync(ct);return Results.Ok(new{item.Id,state=item.State.ToString(),item.Version});}
    private static async Task<IResult> VariantManifestAsync(Guid id,HttpContext http,AeroLinkDbContext db,CancellationToken ct)
    {var variant=await db.ProductVariants.AsNoTracking().SingleOrDefaultAsync(x=>x.Id==id,ct);if(variant is null)return Results.NotFound();if(!await http.HasProjectAccessAsync(db,variant.ProjectId,ct))return Results.Forbid();var parts=await(from pick in db.VariantComponentSelections where pick.VariantId==id join revision in db.ComponentStreamRevisions on pick.ComponentRevisionId equals revision.Id join stream in db.ComponentStreams on revision.StreamId equals stream.Id join component in db.ProductLineComponents on stream.ComponentId equals component.Id orderby component.ComponentNumber,stream.StreamKey,revision.Revision select new{component.ComponentNumber,component.Name,stream.StreamKey,revision.Revision,revision.ManifestHash,pick.ApplicabilityJson}).ToListAsync(ct);var material=JsonSerializer.Serialize(new{variant.VariantKey,variant.ApplicabilityJson,components=parts});var hash=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material))).ToLowerInvariant();return Results.Ok(new{variant=new{variant.Id,variant.VariantKey,variant.Name,state=variant.State.ToString(),variant.ApplicabilityJson},components=parts,manifestHash=hash});}
}

public sealed record CreateComponentRequest(Guid ProjectId,string ComponentNumber,string Name,string? Description);
public sealed record CreateStreamRequest(string StreamKey,string Name);
public sealed record PublishComponentRevisionRequest(string ContentJson);
public sealed record CreateVariantRequest(Guid ProjectId,string VariantKey,string Name,string? ApplicabilityJson);
public sealed record SelectVariantComponentRequest(Guid ComponentRevisionId,string? ApplicabilityJson);
