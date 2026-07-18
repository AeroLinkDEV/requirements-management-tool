using System.Text.Json;
using AeroLink.Domain.Common;
using AeroLink.Domain.Identity;
using AeroLink.Domain.Integrations;
using AeroLink.Infrastructure.Persistence;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

public static class IntegrationEndpoints
{
    private static readonly HashSet<string> AllowedScopes = new(StringComparer.OrdinalIgnoreCase) { "requirements:read", "events:write", "integrations:read", "*" };

    public static IEndpointRouteBuilder MapAeroLinkIntegrationEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/integrations/overview", OverviewAsync);
        app.MapPost("/api/integrations/service-identities", CreateServiceIdentityAsync);
        app.MapPost("/api/integrations/service-identities/{id:guid}/revoke", RevokeServiceIdentityAsync);
        app.MapPost("/api/integrations/webhooks", CreateWebhookAsync);
        app.MapPut("/api/integrations/webhooks/{id:guid}/state", SetWebhookStateAsync);
        app.MapPost("/api/integrations/events/test", PublishTestEventAsync);
        app.MapPost("/api/integrations/deliveries/{id:guid}/replay", ReplayDeliveryAsync);

        var publicApi = app.MapGroup("/api/v1").RequireRateLimiting("service-api");
        publicApi.MapGet("/requirements", GetRequirementsAsync);
        publicApi.MapGet("/requirements/{id:guid}", GetRequirementAsync);
        publicApi.MapGet("/integrations/health", GetIntegrationHealthAsync);
        publicApi.MapPost("/events", PublishServiceEventAsync);
        return app;
    }

    private static async Task<IResult> OverviewAsync(Guid projectId, HttpContext http, AeroLinkDbContext db, CancellationToken ct)
    {
        if (!await http.HasProjectAccessAsync(db, projectId, ct)) return Results.Forbid();
        var identities = (await db.IntegrationServiceIdentities.AsNoTracking().Where(x=>x.ProjectId==projectId).ToListAsync(ct)).OrderByDescending(x=>x.CreatedAt).ToList();
        var subscriptions = await db.WebhookSubscriptions.AsNoTracking().Where(x=>x.ProjectId==projectId).OrderBy(x=>x.Name).ToListAsync(ct);
        var events = (await db.IntegrationEvents.AsNoTracking().Where(x=>x.ProjectId==projectId).ToListAsync(ct)).OrderByDescending(x=>x.OccurredAt).Take(40).ToList();
        var eventIds=events.Select(x=>x.Id).ToList();
        var deliveries = (await db.WebhookDeliveries.AsNoTracking().Where(x=>x.ProjectId==projectId).ToListAsync(ct)).OrderByDescending(x=>x.CreatedAt).Take(80).ToList();
        var interchange = (await db.RequirementInterchangeJobs.AsNoTracking().Where(x=>x.ProjectId==projectId).ToListAsync(ct)).OrderByDescending(x=>x.CreatedAt).Take(12).ToList();
        return Results.Ok(new
        {
            generatedAt=DateTimeOffset.UtcNow,
            api=new{version="v1",basePath="/api/v1",status="Operational",capabilities=new[]{"Scoped service identities","Cursor pagination","Idempotent event ingestion","Lifecycle-wide events","ReqIF 1.2 exchange","Stable problem responses"}},
            metrics=new{activeIdentities=identities.Count(x=>x.State==ServiceIdentityState.Active),enabledWebhooks=subscriptions.Count(x=>x.IsEnabled),events24h=events.Count(x=>x.OccurredAt>=DateTimeOffset.UtcNow.AddHours(-24)),deliverySuccess=deliveries.Count==0?100:(int)Math.Round(deliveries.Count(x=>x.State==WebhookDeliveryState.Delivered)*100d/deliveries.Count),deadLetters=deliveries.Count(x=>x.State==WebhookDeliveryState.DeadLettered)},
            identities=identities.Select(x=>new{x.Id,x.Name,x.ClientId,scopes=JsonSerializer.Deserialize<string[]>(x.ScopesJson)??[],state=x.State.ToString(),x.CreatedAt,x.CreatedBy,x.LastUsedAt,x.RevokedAt}),
            webhooks=subscriptions.Select(x=>new{x.Id,x.Name,x.EndpointUrl,eventTypes=JsonSerializer.Deserialize<string[]>(x.EventTypesJson)??[],x.IsEnabled,x.CreatedAt,x.UpdatedAt}),
            events=events.Select(x=>new{x.Id,x.EventType,x.AggregateType,x.AggregateId,x.Actor,x.OccurredAt,state=x.State.ToString(),deliveryCount=deliveries.Count(d=>d.IntegrationEventId==x.Id)}),
            deliveries=deliveries.Select(x=>new{x.Id,x.IntegrationEventId,x.SubscriptionId,state=x.State.ToString(),x.AttemptCount,x.NextAttemptAt,x.ResponseStatusCode,x.LastError,x.CreatedAt,x.DeliveredAt}),
            interchange=interchange.Select(x=>new{x.Id,x.FileName,state=x.State.ToString(),x.ValidRows,x.InvalidRows,x.CreatedAt,x.CompletedAt})
        });
    }

    private static async Task<IResult> CreateServiceIdentityAsync(CreateServiceIdentityRequest request, HttpContext http, AeroLinkDbContext db, IdentityService identity, IntegrationSecurityService security, CancellationToken ct)
    {
        if (!await http.HasProjectRoleAsync(db,identity,request.ProjectId,ct,ProgramRole.ConfigurationManager,ProgramRole.ProgramManager,ProgramRole.Administrator)) return Results.Forbid();
        var scopes=request.Scopes.Select(x=>x.Trim().ToLowerInvariant()).Distinct().ToArray();
        if(scopes.Length==0||scopes.Any(x=>!AllowedScopes.Contains(x)))return Results.BadRequest(new{error="Select one or more supported API scopes.",code="invalid_scope"});
        try
        {
            var actor=http.UserAccount().UserName;var issued=await security.CreateIdentityAsync(request.ProjectId,request.Name,scopes,actor,DateTimeOffset.UtcNow,ct);
            db.SecurityAuditEvents.Add(new("ServiceIdentityCreated",actor,$"service-identity:{issued.Identity.Id}","Success",$"Created {issued.Identity.Name} with scopes {string.Join(',',scopes)}.",http.Connection.RemoteIpAddress?.ToString()??"local",DateTimeOffset.UtcNow));await db.SaveChangesAsync(ct);
            return Results.Created($"/api/integrations/service-identities/{issued.Identity.Id}",new{issued.Identity.Id,issued.Identity.Name,issued.Identity.ClientId,apiKey=issued.ApiKey,scopes,warning="Copy this API key now. AeroLink stores only its hash and cannot show it again."});
        }
        catch(Exception ex)when(ex is ArgumentException or DomainException or DbUpdateException){return Results.BadRequest(new{error=ex.Message});}
    }

    private static async Task<IResult> RevokeServiceIdentityAsync(Guid id,HttpContext http,AeroLinkDbContext db,IdentityService identity,CancellationToken ct)
    {
        var item=await db.IntegrationServiceIdentities.SingleOrDefaultAsync(x=>x.Id==id,ct);if(item is null)return Results.NotFound();
        if(!await http.HasProjectRoleAsync(db,identity,item.ProjectId,ct,ProgramRole.ConfigurationManager,ProgramRole.ProgramManager,ProgramRole.Administrator))return Results.Forbid();
        var actor=http.UserAccount().UserName;item.Revoke(actor,DateTimeOffset.UtcNow);db.SecurityAuditEvents.Add(new("ServiceIdentityRevoked",actor,$"service-identity:{item.Id}","Success",$"Revoked {item.Name}.",http.Connection.RemoteIpAddress?.ToString()??"local",DateTimeOffset.UtcNow));await db.SaveChangesAsync(ct);return Results.NoContent();
    }

    private static async Task<IResult> CreateWebhookAsync(CreateWebhookRequest request,HttpContext http,AeroLinkDbContext db,IdentityService identity,IntegrationSecurityService security,CancellationToken ct)
    {
        if(!await http.HasProjectRoleAsync(db,identity,request.ProjectId,ct,ProgramRole.ConfigurationManager,ProgramRole.ProgramManager,ProgramRole.Administrator))return Results.Forbid();
        var types=request.EventTypes.Select(x=>x.Trim()).Where(x=>x.Length>0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();if(types.Length==0)return Results.BadRequest(new{error="At least one event type is required."});
        try{var actor=http.UserAccount().UserName;var secret=IntegrationSecurityService.GenerateWebhookSecret();var item=new WebhookSubscription(request.ProjectId,request.Name,request.EndpointUrl,JsonSerializer.Serialize(types),security.ProtectWebhookSecret(secret),actor,DateTimeOffset.UtcNow);db.WebhookSubscriptions.Add(item);await db.SaveChangesAsync(ct);return Results.Created($"/api/integrations/webhooks/{item.Id}",new{item.Id,item.Name,item.EndpointUrl,eventTypes=types,signingSecret=secret,warning="Copy the signing secret now. It is encrypted at rest and is not displayed again."});}
        catch(Exception ex)when(ex is DomainException or DbUpdateException){return Results.BadRequest(new{error=ex.Message});}
    }

    private static async Task<IResult> SetWebhookStateAsync(Guid id,SetWebhookStateRequest request,HttpContext http,AeroLinkDbContext db,IdentityService identity,CancellationToken ct)
    {var item=await db.WebhookSubscriptions.SingleOrDefaultAsync(x=>x.Id==id,ct);if(item is null)return Results.NotFound();if(!await http.HasProjectRoleAsync(db,identity,item.ProjectId,ct,ProgramRole.ConfigurationManager,ProgramRole.ProgramManager,ProgramRole.Administrator))return Results.Forbid();item.SetEnabled(request.Enabled,DateTimeOffset.UtcNow);await db.SaveChangesAsync(ct);return Results.NoContent();}

    private static async Task<IResult> PublishTestEventAsync(PublishTestEventRequest request,HttpContext http,AeroLinkDbContext db,IdentityService identity,IntegrationEventPublisher publisher,CancellationToken ct)
    {if(!await http.HasProjectRoleAsync(db,identity,request.ProjectId,ct,ProgramRole.ConfigurationManager,ProgramRole.ProgramManager,ProgramRole.Administrator))return Results.Forbid();var id=Guid.NewGuid();var item=await publisher.EnqueueAsync(request.ProjectId,"aerolink.integration.test","IntegrationTest",id,new{message=request.Message??"AeroLink signed delivery test",sentBy=http.UserAccount().UserName},http.UserAccount().UserName,DateTimeOffset.UtcNow,ct);return Results.Accepted($"/api/integrations/events/{item.Id}",new{item.Id,item.EventType});}

    private static async Task<IResult> ReplayDeliveryAsync(Guid id,HttpContext http,AeroLinkDbContext db,IdentityService identity,CancellationToken ct)
    {var item=await db.WebhookDeliveries.SingleOrDefaultAsync(x=>x.Id==id,ct);if(item is null)return Results.NotFound();if(!await http.HasProjectRoleAsync(db,identity,item.ProjectId,ct,ProgramRole.ConfigurationManager,ProgramRole.ProgramManager,ProgramRole.Administrator))return Results.Forbid();item.Replay(DateTimeOffset.UtcNow);await db.SaveChangesAsync(ct);return Results.Accepted();}

    private static async Task<IResult> GetRequirementsAsync(Guid projectId,string? cursor,int? pageSize,HttpContext http,AeroLinkDbContext db,CancellationToken ct)
    {
        var service=http.ServiceIdentity();if(service.ProjectId!=projectId||!service.HasScope("requirements:read"))return Results.Forbid();var size=Math.Clamp(pageSize??50,1,200);
        var query=db.Requirements.AsNoTracking().Where(x=>x.ProjectId==projectId);if(!string.IsNullOrWhiteSpace(cursor))query=query.Where(x=>string.Compare(x.BaseNumber,cursor)>0);
        var artifacts=await query.OrderBy(x=>x.BaseNumber).Take(size+1).ToListAsync(ct);var page=artifacts.Take(size).ToList();var ids=page.Select(x=>x.Id).ToList();var revisions=await db.RequirementRevisions.AsNoTracking().Where(x=>ids.Contains(x.ArtifactId)).OrderByDescending(x=>x.Revision).ToListAsync(ct);var current=revisions.GroupBy(x=>x.ArtifactId).ToDictionary(x=>x.Key,x=>x.First());var next=artifacts.Count>size?page[^1].BaseNumber:null;
        http.Response.Headers.ETag=$"\"{IntegrationSecurityService.Hash(string.Join('|',page.Select(x=>$"{x.Id}:{current.GetValueOrDefault(x.Id)?.Revision}")))}\"";return Results.Ok(new{items=page.Select(x=>new{x.Id,identifier=x.BaseNumber,level=x.Level.ToString(),revision=current.GetValueOrDefault(x.Id)?.Revision,statement=current.GetValueOrDefault(x.Id)?.Statement,state=current.GetValueOrDefault(x.Id)?.State.ToString()}),nextCursor=next,pageSize=size});
    }

    private static async Task<IResult> GetRequirementAsync(Guid id,HttpContext http,AeroLinkDbContext db,CancellationToken ct)
    {var service=http.ServiceIdentity();if(!service.HasScope("requirements:read"))return Results.Forbid();var item=await db.Requirements.AsNoTracking().SingleOrDefaultAsync(x=>x.Id==id&&x.ProjectId==service.ProjectId,ct);if(item is null)return Results.NotFound();var revisions=await db.RequirementRevisions.AsNoTracking().Where(x=>x.ArtifactId==id).OrderByDescending(x=>x.Revision).ToListAsync(ct);http.Response.Headers.ETag=$"\"{IntegrationSecurityService.Hash($"{id}:{revisions.FirstOrDefault()?.Revision}")}\"";return Results.Ok(new{item.Id,identifier=item.BaseNumber,level=item.Level.ToString(),revisions=revisions.Select(x=>new{x.Id,x.Revision,x.Statement,x.Rationale,x.VerificationMethod,state=x.State.ToString(),x.SourceScrId,x.EffectiveBaselineId,x.CreatedAt})});}

    private static async Task<IResult> GetIntegrationHealthAsync(HttpContext http,AeroLinkDbContext db,CancellationToken ct)
    {var service=http.ServiceIdentity();if(!service.HasScope("integrations:read"))return Results.Forbid();var subscriptions=await db.WebhookSubscriptions.AsNoTracking().CountAsync(x=>x.ProjectId==service.ProjectId&&x.IsEnabled,ct);var deliveries=await db.WebhookDeliveries.AsNoTracking().Where(x=>x.ProjectId==service.ProjectId).ToListAsync(ct);return Results.Ok(new{status=deliveries.Any(x=>x.State==WebhookDeliveryState.DeadLettered)?"attention":"healthy",projectId=service.ProjectId,enabledSubscriptions=subscriptions,pendingDeliveries=deliveries.Count(x=>x.State is WebhookDeliveryState.Pending or WebhookDeliveryState.RetryScheduled),deadLetters=deliveries.Count(x=>x.State==WebhookDeliveryState.DeadLettered),checkedAt=DateTimeOffset.UtcNow});}

    private static async Task<IResult> PublishServiceEventAsync(PublishServiceEventRequest request,HttpContext http,AeroLinkDbContext db,IntegrationEventPublisher publisher,CancellationToken ct)
    {var service=http.ServiceIdentity();if(!service.HasScope("events:write"))return Results.Forbid();var key=http.Request.Headers["Idempotency-Key"].ToString().Trim();if(key.Length is < 8 or > 160)return Results.BadRequest(new{error="Idempotency-Key must contain between 8 and 160 characters.",code="idempotency_key_required"});var existing=await db.IntegrationEvents.AsNoTracking().SingleOrDefaultAsync(x=>x.ProjectId==service.ProjectId&&x.IdempotencyKey==key,ct);if(existing is not null)return Results.Ok(new{existing.Id,existing.EventType,idempotentReplay=true});try{var item=await publisher.EnqueueAsync(service.ProjectId,request.EventType,request.AggregateType,request.AggregateId,request.Data,service.Name,DateTimeOffset.UtcNow,ct,key);return Results.Accepted($"/api/v1/events/{item.Id}",new{item.Id,item.EventType,idempotentReplay=false});}catch(DbUpdateException){var concurrent=await db.IntegrationEvents.AsNoTracking().SingleAsync(x=>x.ProjectId==service.ProjectId&&x.IdempotencyKey==key,ct);return Results.Ok(new{concurrent.Id,concurrent.EventType,idempotentReplay=true});}}

    private static AuthenticatedServiceIdentity ServiceIdentity(this HttpContext context)=>context.Items.TryGetValue("AeroLink.ServiceIdentity",out var value)&&value is AuthenticatedServiceIdentity service?service:throw new InvalidOperationException("Service identity context is unavailable.");
}

public sealed record CreateServiceIdentityRequest(Guid ProjectId,string Name,List<string> Scopes);
public sealed record CreateWebhookRequest(Guid ProjectId,string Name,string EndpointUrl,List<string> EventTypes);
public sealed record SetWebhookStateRequest(bool Enabled);
public sealed record PublishTestEventRequest(Guid ProjectId,string? Message);
public sealed record PublishServiceEventRequest(string EventType,string AggregateType,Guid AggregateId,JsonElement Data);
