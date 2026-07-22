using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
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
        group.MapPost("/components/{id:guid}/approve",ApproveComponentAsync);
        group.MapPost("/components/{id:guid}/streams",CreateStreamAsync);
        group.MapPost("/streams/{id:guid}/revisions",PublishStreamRevisionAsync);
        group.MapPost("/change-sets",CreateChangeSetAsync);
        group.MapGet("/change-sets/{id:guid}",GetChangeSetAsync);
        group.MapPost("/change-sets/{id:guid}/resolve",ResolveChangeSetAsync);
        group.MapPost("/change-sets/{id:guid}/apply",ApplyChangeSetAsync);
        group.MapPost("/variants",CreateVariantAsync);
        group.MapPost("/variants/{id:guid}/components",SelectComponentAsync);
        group.MapPost("/variants/{id:guid}/propagation-decisions",RecordPropagationDecisionAsync);
        group.MapPost("/variants/{id:guid}/approve",ApproveVariantAsync);
        group.MapPost("/variants/{id:guid}/baselines",BaselineVariantAsync);
        group.MapGet("/variants/{id:guid}/manifest",VariantManifestAsync);
        return app;
    }

    private static async Task<IResult> OverviewAsync(Guid projectId,HttpContext http,AeroLinkDbContext db,CancellationToken ct)
    {
        if(!await http.HasProjectAccessAsync(db,projectId,ct))return Results.Forbid();
        var components=await db.ProductLineComponents.AsNoTracking().Where(x=>x.ProjectId==projectId).OrderBy(x=>x.ComponentNumber).ToListAsync(ct);
        var variants=await db.ProductVariants.AsNoTracking().Where(x=>x.ProjectId==projectId).OrderBy(x=>x.VariantKey).ToListAsync(ct);
        var componentIds=components.Select(x=>x.Id).ToList();var variantIds=variants.Select(x=>x.Id).ToList();
        var streams=await db.ComponentStreams.AsNoTracking().Where(x=>componentIds.Contains(x.ComponentId)).ToListAsync(ct);var streamIds=streams.Select(x=>x.Id).ToList();
        var revisions=await db.ComponentStreamRevisions.AsNoTracking().Where(x=>streamIds.Contains(x.StreamId)).ToListAsync(ct);
        var selections=await db.VariantComponentSelections.AsNoTracking().Where(x=>variantIds.Contains(x.VariantId)).ToListAsync(ct);
        var baselines=await db.ProductVariantBaselines.AsNoTracking().Where(x=>variantIds.Contains(x.VariantId)).ToListAsync(ct);
        var changes=(await db.ConfigurationChangeSets.AsNoTracking().Where(x=>x.ProjectId==projectId&&x.ComponentId!=null).ToListAsync(ct)).OrderByDescending(x=>x.UpdatedAt).Take(100).ToList();
        return Results.Ok(new
        {
            components=components.Select(x=>new{x.Id,x.ComponentNumber,x.Name,x.Description,state=x.State.ToString(),x.Version,streamCount=streams.Count(s=>s.ComponentId==x.Id),revisionCount=streams.Where(s=>s.ComponentId==x.Id).Sum(s=>revisions.Count(r=>r.StreamId==s.Id))}),
            variants=variants.Select(x=>new{x.Id,x.VariantKey,x.Name,state=x.State.ToString(),x.ApplicabilityJson,x.Version,componentCount=selections.Count(s=>s.VariantId==x.Id),baselineCount=baselines.Count(b=>b.VariantId==x.Id)}),
            streams=streams.Select(x=>new{x.Id,x.ComponentId,x.StreamKey,x.Name,state=x.State.ToString(),revisionCount=revisions.Count(r=>r.StreamId==x.Id),head=revisions.Where(r=>r.StreamId==x.Id).OrderByDescending(r=>r.Revision).Select(r=>new{r.Id,r.Revision,r.ManifestHash,r.CreatedAt}).FirstOrDefault()}),
            changeSets=changes.Select(x=>new{x.Id,x.ChangeSetNumber,x.Title,state=x.State.ToString(),x.ComponentId,x.TargetStreamId,x.Version,x.UpdatedAt,hasConflicts=x.ConflictJson!=null})
        });
    }

    private static async Task<IResult> CreateComponentAsync(CreateComponentRequest request,HttpContext http,AeroLinkDbContext db,IdentityService identity,CancellationToken ct)
    {if(!await http.HasProjectRoleAsync(db,identity,request.ProjectId,ct,ProgramRole.Engineer,ProgramRole.ConfigurationManager))return Results.Forbid();try{var item=new ProductLineComponent(request.ProjectId,request.ComponentNumber,request.Name,request.Description??"",http.UserAccount().UserName,DateTimeOffset.UtcNow);db.ProductLineComponents.Add(item);await db.SaveChangesAsync(ct);return Results.Created($"/api/product-line/components/{item.Id}",new{item.Id,item.ComponentNumber});}catch(DomainException ex){return Results.BadRequest(new{error=ex.Message});}catch(DbUpdateException){return Results.Conflict(new{error="A component with this number already exists."});}}

    private static async Task<IResult> ApproveComponentAsync(Guid id,HttpContext http,AeroLinkDbContext db,IdentityService identity,CancellationToken ct)
    {var item=await db.ProductLineComponents.SingleOrDefaultAsync(x=>x.Id==id,ct);if(item is null)return Results.NotFound();if(!await http.HasProjectRoleAsync(db,identity,item.ProjectId,ct,ProgramRole.ConfigurationManager))return Results.Forbid();var hasRevision=await(from revision in db.ComponentStreamRevisions join stream in db.ComponentStreams on revision.StreamId equals stream.Id where stream.ComponentId==id select revision.Id).AnyAsync(ct);if(!hasRevision)return Results.BadRequest(new{error="Publish at least one controlled stream revision before approving this reusable component."});item.Approve(http.UserAccount().UserName,DateTimeOffset.UtcNow);await db.SaveChangesAsync(ct);return Results.Ok(new{item.Id,state=item.State.ToString(),item.Version});}

    private static async Task<IResult> CreateStreamAsync(Guid id,CreateStreamRequest request,HttpContext http,AeroLinkDbContext db,IdentityService identity,CancellationToken ct)
    {var component=await db.ProductLineComponents.SingleOrDefaultAsync(x=>x.Id==id,ct);if(component is null)return Results.NotFound();if(!await http.HasProjectRoleAsync(db,identity,component.ProjectId,ct,ProgramRole.Engineer,ProgramRole.ConfigurationManager))return Results.Forbid();try{var item=new ComponentStream(id,request.StreamKey,request.Name,http.UserAccount().UserName,DateTimeOffset.UtcNow);db.ComponentStreams.Add(item);await db.SaveChangesAsync(ct);return Results.Created($"/api/product-line/streams/{item.Id}",new{item.Id,item.StreamKey});}catch(DomainException ex){return Results.BadRequest(new{error=ex.Message});}catch(DbUpdateException){return Results.Conflict(new{error="This component already has that stream key."});}}

    private static async Task<IResult> PublishStreamRevisionAsync(Guid id,PublishComponentRevisionRequest request,HttpContext http,AeroLinkDbContext db,IdentityService identity,CancellationToken ct)
    {
        var stream=await db.ComponentStreams.SingleOrDefaultAsync(x=>x.Id==id,ct);if(stream is null)return Results.NotFound();var component=await db.ProductLineComponents.SingleAsync(x=>x.Id==stream.ComponentId,ct);if(!await http.HasProjectRoleAsync(db,identity,component.ProjectId,ct,ProgramRole.Engineer,ProgramRole.ConfigurationManager))return Results.Forbid();
        try{var canonical=Canonicalize(request.ContentJson);await using var transaction=await db.Database.BeginTransactionAsync(IsolationLevel.Serializable,ct);var next=(await db.ComponentStreamRevisions.Where(x=>x.StreamId==id).MaxAsync(x=>(int?)x.Revision,ct)??0)+1;var revision=new ComponentStreamRevision(id,next,canonical,Hash(canonical),http.UserAccount().UserName,DateTimeOffset.UtcNow);db.ComponentStreamRevisions.Add(revision);await db.SaveChangesAsync(ct);await transaction.CommitAsync(ct);return Results.Created($"/api/product-line/streams/{id}/revisions/{revision.Id}",new{revision.Id,revision.Revision,revision.ManifestHash});}
        catch(JsonException){return Results.BadRequest(new{error="Component content must be valid JSON."});}catch(DomainException ex){return Results.BadRequest(new{error=ex.Message});}catch(DbUpdateException){return Results.Conflict(new{error="A concurrent revision was published. Refresh the stream head and retry.",code="stream_head_changed"});}
    }

    private static async Task<IResult> CreateChangeSetAsync(CreateProductLineChangeSetRequest request,HttpContext http,AeroLinkDbContext db,IdentityService identity,CancellationToken ct)
    {
        var revisions=await(from revision in db.ComponentStreamRevisions join stream in db.ComponentStreams on revision.StreamId equals stream.Id join component in db.ProductLineComponents on stream.ComponentId equals component.Id where revision.Id==request.BaseRevisionId||revision.Id==request.SourceRevisionId||revision.Id==request.TargetRevisionId select new{Revision=revision,Stream=stream,Component=component}).ToListAsync(ct);
        if(revisions.Count!=3)return Results.BadRequest(new{error="Base, source, and target revisions must exist."});var componentId=revisions[0].Component.Id;if(revisions.Any(x=>x.Component.Id!=componentId))return Results.BadRequest(new{error="All compared revisions must belong to the same reusable component."});var projectId=revisions[0].Component.ProjectId;
        if(!await http.HasProjectRoleAsync(db,identity,projectId,ct,ProgramRole.Engineer,ProgramRole.ConfigurationManager))return Results.Forbid();var target=revisions.Single(x=>x.Revision.Id==request.TargetRevisionId);var merge=ThreeWayMerge(revisions.Single(x=>x.Revision.Id==request.BaseRevisionId).Revision.ContentJson,revisions.Single(x=>x.Revision.Id==request.SourceRevisionId).Revision.ContentJson,target.Revision.ContentJson);
        await using var transaction=await db.Database.BeginTransactionAsync(IsolationLevel.Serializable,ct);var next=(await db.ConfigurationChangeSets.Where(x=>x.ProjectId==projectId&&x.ComponentId!=null).CountAsync(ct))+1;var item=new ConfigurationChangeSet(projectId,$"CCS-{next:D5}",request.Title,request.Description??"",http.UserAccount().UserName,DateTimeOffset.UtcNow);item.ConfigureMerge(componentId,target.Stream.Id,request.BaseRevisionId,request.SourceRevisionId,request.TargetRevisionId,merge.MergedJson,merge.ConflictJson,DateTimeOffset.UtcNow);db.ConfigurationChangeSets.Add(item);await db.SaveChangesAsync(ct);await transaction.CommitAsync(ct);return Results.Created($"/api/product-line/change-sets/{item.Id}",ChangeSetResponse(item));
    }

    private static async Task<IResult> GetChangeSetAsync(Guid id,HttpContext http,AeroLinkDbContext db,CancellationToken ct)
    {var item=await db.ConfigurationChangeSets.AsNoTracking().SingleOrDefaultAsync(x=>x.Id==id&&x.ComponentId!=null,ct);if(item is null)return Results.NotFound();if(!await http.HasProjectAccessAsync(db,item.ProjectId,ct))return Results.Forbid();return Results.Ok(ChangeSetResponse(item));}

    private static async Task<IResult> ResolveChangeSetAsync(Guid id,ResolveProductLineChangeSetRequest request,HttpContext http,AeroLinkDbContext db,IdentityService identity,CancellationToken ct)
    {var item=await db.ConfigurationChangeSets.SingleOrDefaultAsync(x=>x.Id==id&&x.ComponentId!=null,ct);if(item is null)return Results.NotFound();if(!await http.HasProjectRoleAsync(db,identity,item.ProjectId,ct,ProgramRole.Engineer,ProgramRole.ConfigurationManager))return Results.Forbid();try{var canonical=Canonicalize(request.ResolutionJson);item.Resolve(canonical,request.Rationale,DateTimeOffset.UtcNow);await db.SaveChangesAsync(ct);return Results.Ok(ChangeSetResponse(item));}catch(JsonException){return Results.BadRequest(new{error="The conflict resolution must be valid JSON."});}catch(DomainException ex){return Results.BadRequest(new{error=ex.Message});}}

    private static async Task<IResult> ApplyChangeSetAsync(Guid id,HttpContext http,AeroLinkDbContext db,IdentityService identity,CancellationToken ct)
    {
        var item=await db.ConfigurationChangeSets.SingleOrDefaultAsync(x=>x.Id==id&&x.ComponentId!=null,ct);if(item is null)return Results.NotFound();if(!await http.HasProjectRoleAsync(db,identity,item.ProjectId,ct,ProgramRole.ConfigurationManager))return Results.Forbid();if(item.State!=ConfigurationChangeSetState.InWork||item.TargetStreamId is null||item.TargetRevisionId is null||item.MergeResultJson is null)return Results.Conflict(new{error="Resolve all conflicts before applying the change set.",code="change_set_not_ready"});
        await using var transaction=await db.Database.BeginTransactionAsync(IsolationLevel.Serializable,ct);var head=await db.ComponentStreamRevisions.Where(x=>x.StreamId==item.TargetStreamId).OrderByDescending(x=>x.Revision).FirstAsync(ct);if(head.Id!=item.TargetRevisionId)return Results.Conflict(new{error="The target stream changed after comparison. Create a new change set against the current head.",code="target_stream_changed"});var revision=new ComponentStreamRevision(head.StreamId,head.Revision+1,item.MergeResultJson,Hash(item.MergeResultJson),http.UserAccount().UserName,DateTimeOffset.UtcNow);db.ComponentStreamRevisions.Add(revision);item.Close(DateTimeOffset.UtcNow);db.SecurityAuditEvents.Add(new("ProductLineChangeSetApplied",http.UserAccount().UserName,item.ChangeSetNumber,"Success",$"Published immutable component revision {revision.Revision} with manifest {revision.ManifestHash}.",http.Connection.RemoteIpAddress?.ToString()??"local",DateTimeOffset.UtcNow));try{await db.SaveChangesAsync(ct);await transaction.CommitAsync(ct);return Results.Ok(new{changeSet=ChangeSetResponse(item),revision=new{revision.Id,revision.Revision,revision.ManifestHash}});}catch(DbUpdateException){return Results.Conflict(new{error="The target stream changed while the change set was being applied.",code="target_stream_changed"});}
    }

    private static async Task<IResult> CreateVariantAsync(CreateVariantRequest request,HttpContext http,AeroLinkDbContext db,IdentityService identity,CancellationToken ct)
    {if(!await http.HasProjectRoleAsync(db,identity,request.ProjectId,ct,ProgramRole.Engineer,ProgramRole.ConfigurationManager))return Results.Forbid();try{var applicability=Canonicalize(request.ApplicabilityJson??"{}");var item=new ProductVariant(request.ProjectId,request.VariantKey,request.Name,applicability,http.UserAccount().UserName,DateTimeOffset.UtcNow);db.ProductVariants.Add(item);await db.SaveChangesAsync(ct);return Results.Created($"/api/product-line/variants/{item.Id}",new{item.Id,item.VariantKey});}catch(JsonException){return Results.BadRequest(new{error="Applicability must be valid JSON."});}catch(DomainException ex){return Results.BadRequest(new{error=ex.Message});}catch(DbUpdateException){return Results.Conflict(new{error="A variant with this key already exists."});}}

    private static async Task<IResult> SelectComponentAsync(Guid id,SelectVariantComponentRequest request,HttpContext http,AeroLinkDbContext db,IdentityService identity,CancellationToken ct)
    {var variant=await db.ProductVariants.SingleOrDefaultAsync(x=>x.Id==id,ct);if(variant is null)return Results.NotFound();if(!await http.HasProjectRoleAsync(db,identity,variant.ProjectId,ct,ProgramRole.ConfigurationManager))return Results.Forbid();if(variant.State!=ProductVariantState.Draft)return Results.Conflict(new{error="Approved variants are immutable. Create a new draft variant revision to change its component selections."});var revision=await(from r in db.ComponentStreamRevisions join s in db.ComponentStreams on r.StreamId equals s.Id join c in db.ProductLineComponents on s.ComponentId equals c.Id where r.Id==request.ComponentRevisionId select new{r,c.ProjectId,c.State}).SingleOrDefaultAsync(ct);if(revision is null||revision.ProjectId!=variant.ProjectId)return Results.BadRequest(new{error="The selected component revision is not in this Project."});if(revision.State!=ProductLineComponentState.Approved)return Results.Conflict(new{error="Only an explicitly approved reusable component can be selected."});try{var applicability=Canonicalize(request.ApplicabilityJson??"{}");var selection=new VariantComponentSelection(id,request.ComponentRevisionId,applicability,http.UserAccount().UserName,DateTimeOffset.UtcNow);db.VariantComponentSelections.Add(selection);await db.SaveChangesAsync(ct);return Results.Created($"/api/product-line/variants/{id}/components/{selection.Id}",new{selection.Id});}catch(JsonException){return Results.BadRequest(new{error="Selection applicability must be valid JSON."});}catch(DbUpdateException){return Results.Conflict(new{error="That immutable component revision is already selected for this variant."});}}

    private static async Task<IResult> RecordPropagationDecisionAsync(Guid id,RecordPropagationDecisionRequest request,HttpContext http,AeroLinkDbContext db,IdentityService identity,CancellationToken ct)
    {var variant=await db.ProductVariants.SingleOrDefaultAsync(x=>x.Id==id,ct);if(variant is null)return Results.NotFound();if(!await http.HasProjectRoleAsync(db,identity,variant.ProjectId,ct,ProgramRole.ConfigurationManager))return Results.Forbid();var belongs=await(from revision in db.ComponentStreamRevisions join stream in db.ComponentStreams on revision.StreamId equals stream.Id join component in db.ProductLineComponents on stream.ComponentId equals component.Id where revision.Id==request.ComponentRevisionId&&component.ProjectId==variant.ProjectId select revision.Id).AnyAsync(ct);if(!belongs)return Results.BadRequest(new{error="The component revision is not part of this product line."});try{var decision=new ComponentPropagationDecision(id,request.ComponentRevisionId,request.Decision,request.Rationale,http.UserAccount().UserName,DateTimeOffset.UtcNow);db.ComponentPropagationDecisions.Add(decision);await db.SaveChangesAsync(ct);return Results.Created($"/api/product-line/variants/{id}/propagation-decisions/{decision.Id}",new{decision.Id,decision=request.Decision.ToString()});}catch(DomainException ex){return Results.BadRequest(new{error=ex.Message});}catch(DbUpdateException){return Results.Conflict(new{error="A propagation decision already exists for this variant and component revision."});}}

    private static async Task<IResult> ApproveVariantAsync(Guid id,HttpContext http,AeroLinkDbContext db,IdentityService identity,CancellationToken ct)
    {var item=await db.ProductVariants.SingleOrDefaultAsync(x=>x.Id==id,ct);if(item is null)return Results.NotFound();if(!await http.HasProjectRoleAsync(db,identity,item.ProjectId,ct,ProgramRole.ConfigurationManager))return Results.Forbid();if(!await db.VariantComponentSelections.AnyAsync(x=>x.VariantId==id,ct))return Results.BadRequest(new{error="Select at least one immutable component revision before approving a variant."});item.Approve(DateTimeOffset.UtcNow);await db.SaveChangesAsync(ct);return Results.Ok(new{item.Id,state=item.State.ToString(),item.Version});}

    private static async Task<IResult> BaselineVariantAsync(Guid id,HttpContext http,AeroLinkDbContext db,IdentityService identity,CancellationToken ct)
    {var variant=await db.ProductVariants.SingleOrDefaultAsync(x=>x.Id==id,ct);if(variant is null)return Results.NotFound();if(!await http.HasProjectRoleAsync(db,identity,variant.ProjectId,ct,ProgramRole.ConfigurationManager))return Results.Forbid();if(variant.State!=ProductVariantState.Approved)return Results.Conflict(new{error="Only an approved variant can be baselined."});var manifest=await BuildManifestAsync(id,db,ct);await using var transaction=await db.Database.BeginTransactionAsync(IsolationLevel.Serializable,ct);var next=(await db.ProductVariantBaselines.Where(x=>x.VariantId==id).MaxAsync(x=>(int?)x.Revision,ct)??0)+1;var baseline=new ProductVariantBaseline(id,next,manifest.Json,manifest.Hash,http.UserAccount().UserName,DateTimeOffset.UtcNow);db.ProductVariantBaselines.Add(baseline);try{await db.SaveChangesAsync(ct);await transaction.CommitAsync(ct);return Results.Created($"/api/product-line/variants/{id}/baselines/{baseline.Id}",new{baseline.Id,baseline.Revision,baseline.ManifestHash,baseline.CreatedAt});}catch(DbUpdateException){return Results.Conflict(new{error="A concurrent variant baseline was created. Refresh and retry.",code="variant_baseline_changed"});}}

    private static async Task<IResult> VariantManifestAsync(Guid id,HttpContext http,AeroLinkDbContext db,CancellationToken ct)
    {var variant=await db.ProductVariants.AsNoTracking().SingleOrDefaultAsync(x=>x.Id==id,ct);if(variant is null)return Results.NotFound();if(!await http.HasProjectAccessAsync(db,variant.ProjectId,ct))return Results.Forbid();var manifest=await BuildManifestAsync(id,db,ct);var baseline=await db.ProductVariantBaselines.AsNoTracking().Where(x=>x.VariantId==id).OrderByDescending(x=>x.Revision).Select(x=>new{x.Id,x.Revision,x.ManifestHash,x.CreatedBy,x.CreatedAt}).FirstOrDefaultAsync(ct);using var json=JsonDocument.Parse(manifest.Json);var components=json.RootElement.GetProperty("components").Clone();var decisions=json.RootElement.GetProperty("propagationDecisions").Clone();return Results.Ok(new{variant=new{variant.Id,variant.VariantKey,variant.Name,state=variant.State.ToString(),variant.ApplicabilityJson},components,propagationDecisions=decisions,manifestHash=manifest.Hash,latestBaseline=baseline});}

    private static async Task<(string Json,string Hash)> BuildManifestAsync(Guid variantId,AeroLinkDbContext db,CancellationToken ct)
    {var variant=await db.ProductVariants.AsNoTracking().SingleAsync(x=>x.Id==variantId,ct);var parts=await(from pick in db.VariantComponentSelections.AsNoTracking() where pick.VariantId==variantId join revision in db.ComponentStreamRevisions.AsNoTracking() on pick.ComponentRevisionId equals revision.Id join stream in db.ComponentStreams.AsNoTracking() on revision.StreamId equals stream.Id join component in db.ProductLineComponents.AsNoTracking() on stream.ComponentId equals component.Id orderby component.ComponentNumber,stream.StreamKey,revision.Revision select new{component.ComponentNumber,component.Name,stream.StreamKey,revision.Revision,revision.ManifestHash,pick.ApplicabilityJson}).ToListAsync(ct);var decisionRows=(await db.ComponentPropagationDecisions.AsNoTracking().Where(x=>x.VariantId==variantId).ToListAsync(ct)).OrderBy(x=>x.DecidedAt).ToList();var decisions=decisionRows.Select(x=>new{x.ComponentRevisionId,decision=x.Decision.ToString(),x.Rationale,x.DecidedBy,x.DecidedAt});var material=Canonicalize(JsonSerializer.Serialize(new{variant.VariantKey,variant.Name,variant.ApplicabilityJson,components=parts,propagationDecisions=decisions}));return(material,Hash(material));}

    private static object ChangeSetResponse(ConfigurationChangeSet x)=>new{x.Id,x.ChangeSetNumber,x.Title,x.Description,x.OwnerId,state=x.State.ToString(),x.ComponentId,x.TargetStreamId,x.BaseRevisionId,x.SourceRevisionId,x.TargetRevisionId,x.MergeResultJson,x.ConflictJson,x.ResolutionRationale,x.Version,x.CreatedAt,x.UpdatedAt,x.ClosedAt};
    private static string Hash(string material)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material))).ToLowerInvariant();
    private static string Canonicalize(string json){var node=JsonNode.Parse(json)??throw new JsonException("JSON content is required.");return CanonicalNode(node).ToJsonString(new JsonSerializerOptions{WriteIndented=false});}
    private static JsonNode CanonicalNode(JsonNode node)=>node switch{JsonObject obj=>new JsonObject(obj.OrderBy(x=>x.Key,StringComparer.Ordinal).Select(x=>KeyValuePair.Create<string,JsonNode?>(x.Key,x.Value is null?null:CanonicalNode(x.Value)))),JsonArray array=>new JsonArray(array.Select(x=>x is null?null:CanonicalNode(x)).ToArray()),_=>node.DeepClone()};
    private static MergeResult ThreeWayMerge(string baseJson,string sourceJson,string targetJson)
    {var baseNode=JsonNode.Parse(baseJson)!;var sourceNode=JsonNode.Parse(sourceJson)!;var targetNode=JsonNode.Parse(targetJson)!;var conflicts=new List<object>();var merged=MergeNode(baseNode,sourceNode,targetNode,"$",conflicts);return conflicts.Count==0?new(Canonicalize(merged!.ToJsonString()),null):new(null,Canonicalize(JsonSerializer.Serialize(new{conflicts,proposed=merged})));}
    private static JsonNode? MergeNode(JsonNode? baseNode,JsonNode? sourceNode,JsonNode? targetNode,string path,List<object> conflicts)
    {
        if(JsonNode.DeepEquals(sourceNode,targetNode))return sourceNode?.DeepClone();if(JsonNode.DeepEquals(sourceNode,baseNode))return targetNode?.DeepClone();if(JsonNode.DeepEquals(targetNode,baseNode))return sourceNode?.DeepClone();
        if(baseNode is JsonObject b&&sourceNode is JsonObject s&&targetNode is JsonObject t){var result=new JsonObject();foreach(var key in b.Select(x=>x.Key).Concat(s.Select(x=>x.Key)).Concat(t.Select(x=>x.Key)).Distinct().Order(StringComparer.Ordinal)){result[key]=MergeNode(b[key],s[key],t[key],$"{path}.{key}",conflicts);}return result;}
        conflicts.Add(new{path,baseValue=baseNode,sourceValue=sourceNode,targetValue=targetNode});return targetNode?.DeepClone();
    }
    private sealed record MergeResult(string? MergedJson,string? ConflictJson);
}

public sealed record CreateComponentRequest(Guid ProjectId,string ComponentNumber,string Name,string? Description);
public sealed record CreateStreamRequest(string StreamKey,string Name);
public sealed record PublishComponentRevisionRequest(string ContentJson);
public sealed record CreateProductLineChangeSetRequest(Guid BaseRevisionId,Guid SourceRevisionId,Guid TargetRevisionId,string Title,string? Description);
public sealed record ResolveProductLineChangeSetRequest(string ResolutionJson,string Rationale);
public sealed record CreateVariantRequest(Guid ProjectId,string VariantKey,string Name,string? ApplicabilityJson);
public sealed record SelectVariantComponentRequest(Guid ComponentRevisionId,string? ApplicabilityJson);
public sealed record RecordPropagationDecisionRequest(Guid ComponentRevisionId,PropagationDecisionKind Decision,string Rationale);
