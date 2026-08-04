using System.Text.Json;
using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Common;
using AeroLink.Domain.Identity;
using AeroLink.Domain.Integrations;
using AeroLink.Domain.Requirements;
using AeroLink.Infrastructure.Persistence;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

public static class IntegrationEndpoints
{
    private static readonly HashSet<string> AllowedScopes = new(StringComparer.OrdinalIgnoreCase) { "requirements:read", "requirements:write", "oslc:read", "oslc:write", "events:write", "integrations:read", "*" };
    private const string ConnectorConfigured="aerolink.connector.configured";
    private const string ConnectorCheckpoint="aerolink.connector.checkpoint";
    private const string MappingRevision="aerolink.mapping.revision";

    public static IEndpointRouteBuilder MapAeroLinkIntegrationEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/integrations/overview", OverviewAsync);
        app.MapPost("/api/integrations/service-identities", CreateServiceIdentityAsync);
        app.MapPost("/api/integrations/service-identities/{id:guid}/revoke", RevokeServiceIdentityAsync);
        app.MapPost("/api/integrations/webhooks", CreateWebhookAsync);
        app.MapPut("/api/integrations/webhooks/{id:guid}/state", SetWebhookStateAsync);
        app.MapPost("/api/integrations/events/test", PublishTestEventAsync);
        app.MapPost("/api/integrations/deliveries/{id:guid}/replay", ReplayDeliveryAsync);
        app.MapPost("/api/integrations/connectors", ConfigureConnectorAsync);
        app.MapPost("/api/integrations/connectors/{id:guid}/checkpoints", RecordConnectorCheckpointAsync);
        app.MapPost("/api/integrations/checkpoints/{id:guid}/replay", ReplayConnectorCheckpointAsync);
        app.MapGet("/api/integrations/mappings", GetMappingsAsync);
        app.MapPost("/api/integrations/mappings", CreateMappingAsync);
        app.MapPut("/api/integrations/mappings/{id:guid}", UpdateMappingAsync);
        app.MapGet("/api/integrations/mappings/{id:guid}/history", GetMappingHistoryAsync);
        app.MapAeroLinkControlledEditingEndpoints();
        app.MapAeroLinkProblemReportEndpoints();

        var publicApi = app.MapGroup("/api/v1").RequireRateLimiting("service-api");
        publicApi.MapGet("/requirements", GetRequirementsAsync);
        publicApi.MapGet("/requirements/{id:guid}", GetRequirementAsync);
        publicApi.MapPut("/requirements/{id:guid}", ProposeConditionalRequirementWriteAsync);
        publicApi.MapGet("/oslc/rm/catalog", GetOslcCatalogAsync);
        publicApi.MapGet("/oslc/rm/requirements/{id:guid}", GetOslcRequirementAsync);
        publicApi.MapPost("/oslc/rm/consume", ConsumeOslcRequirementAsync);
        publicApi.MapGet("/integrations/health", GetIntegrationHealthAsync);
        publicApi.MapPost("/events", PublishServiceEventAsync);
        return app;
    }

    private static async Task<IResult> OverviewAsync(Guid projectId, HttpContext http, AeroLinkDbContext db, CancellationToken ct)
    {
        if (!await http.HasProjectAccessAsync(db, projectId, ct)) return Results.Forbid();
        var identities = (await db.IntegrationServiceIdentities.AsNoTracking().Where(x=>x.ProjectId==projectId).ToListAsync(ct)).OrderByDescending(x=>x.CreatedAt).ToList();
        var subscriptions = await db.WebhookSubscriptions.AsNoTracking().Where(x=>x.ProjectId==projectId).OrderBy(x=>x.Name).ToListAsync(ct);
        var events = (await db.IntegrationEvents.AsNoTracking().Where(x=>x.ProjectId==projectId).ToListAsync(ct)).OrderByDescending(x=>x.OccurredAt).Take(400).ToList();
        var deliveries = (await db.WebhookDeliveries.AsNoTracking().Where(x=>x.ProjectId==projectId).ToListAsync(ct)).OrderByDescending(x=>x.CreatedAt).Take(80).ToList();
        var interchange = (await db.RequirementInterchangeJobs.AsNoTracking().Where(x=>x.ProjectId==projectId).ToListAsync(ct)).OrderByDescending(x=>x.CreatedAt).Take(12).ToList();
        var mappings=await db.RequirementImportMappings.AsNoTracking().Where(x=>x.ProjectId==projectId).OrderBy(x=>x.Name).ToListAsync(ct);
        var connectors=BuildConnectorHealth(events,DateTimeOffset.UtcNow);
        return Results.Ok(new
        {
            generatedAt=DateTimeOffset.UtcNow,
            api=new{version="v1",basePath="/api/v1",status="Operational",capabilities=new[]{"Scoped service identities","Cursor pagination","Idempotent event ingestion","Lifecycle-wide events","ReqIF 1.2 exchange","OSLC RM provider and consumer","Durable connector checkpoints","Replay with provenance","Versioned mappings","Stable problem responses"}},
            metrics=new{activeIdentities=identities.Count(x=>x.State==ServiceIdentityState.Active),enabledWebhooks=subscriptions.Count(x=>x.IsEnabled),events24h=events.Count(x=>x.OccurredAt>=DateTimeOffset.UtcNow.AddHours(-24)),deliverySuccess=deliveries.Count==0?100:(int)Math.Round(deliveries.Count(x=>x.State==WebhookDeliveryState.Delivered)*100d/deliveries.Count),deadLetters=deliveries.Count(x=>x.State==WebhookDeliveryState.DeadLettered),connectors=connectors.Count,connectorsAttention=connectors.Count(x=>x.Status!="healthy"),mappingVersions=events.Count(x=>x.EventType==MappingRevision)},
            identities=identities.Select(x=>new{x.Id,x.Name,x.ClientId,scopes=JsonSerializer.Deserialize<string[]>(x.ScopesJson)??[],state=x.State.ToString(),x.CreatedAt,x.CreatedBy,x.LastUsedAt,x.RevokedAt}),
            webhooks=subscriptions.Select(x=>new{x.Id,x.Name,x.EndpointUrl,eventTypes=JsonSerializer.Deserialize<string[]>(x.EventTypesJson)??[],x.IsEnabled,x.CreatedAt,x.UpdatedAt}),
            events=events.Take(40).Select(x=>new{x.Id,x.EventType,x.AggregateType,x.AggregateId,x.Actor,x.OccurredAt,state=x.State.ToString(),deliveryCount=deliveries.Count(d=>d.IntegrationEventId==x.Id)}),
            deliveries=deliveries.Select(x=>new{x.Id,x.IntegrationEventId,x.SubscriptionId,state=x.State.ToString(),x.AttemptCount,x.NextAttemptAt,x.ResponseStatusCode,x.LastError,x.CreatedAt,x.DeliveredAt}),
            interchange=interchange.Select(x=>new{x.Id,x.FileName,state=x.State.ToString(),x.ValidRows,x.InvalidRows,x.CreatedAt,x.CompletedAt}),
            connectors,
            mappings=mappings.Select(x=>new{x.Id,x.Name,x.Version,x.CreatedBy,x.CreatedAt,x.UpdatedAt,sha256=IntegrationSecurityService.Hash(x.MappingJson),historyUrl=$"/api/integrations/mappings/{x.Id}/history"})
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

    private static async Task<IResult> ConfigureConnectorAsync(ConfigureConnectorRequest request,HttpContext http,AeroLinkDbContext db,IdentityService identity,IntegrationEventPublisher publisher,CancellationToken ct)
    {
        if(!await http.HasProjectRoleAsync(db,identity,request.ProjectId,ct,ProgramRole.ConfigurationManager,ProgramRole.ProgramManager,ProgramRole.Administrator))return Results.Forbid();
        if(string.IsNullOrWhiteSpace(request.Name)||string.IsNullOrWhiteSpace(request.Kind))return Results.BadRequest(new{error="Connector name and kind are required."});
        if(!Uri.TryCreate(request.BaseUrl,UriKind.Absolute,out var baseUri)||baseUri.Scheme is not ("http" or "https"))return Results.BadRequest(new{error="Connector baseUrl must be an absolute HTTP or HTTPS URL."});
        var connectorId=request.ConnectorId??Guid.NewGuid();var staleAfter=Math.Clamp(request.StaleAfterMinutes??60,5,10080);var now=DateTimeOffset.UtcNow;var actor=http.UserAccount().UserName;
        var payload=new{connectorId,name=request.Name.Trim(),kind=request.Kind.Trim(),baseUrl=baseUri.ToString(),configurationContext=request.ConfigurationContext?.Trim()??"",mappingName=request.MappingName?.Trim()??"",staleAfterMinutes=staleAfter,configuredBy=actor,configuredAt=now};
        var item=await publisher.EnqueueAsync(request.ProjectId,ConnectorConfigured,"IntegrationConnector",connectorId,payload,actor,now,ct,$"connector-config:{connectorId:N}:{IntegrationSecurityService.Hash(JsonSerializer.Serialize(payload))}");
        return Results.Created($"/api/integrations/connectors/{connectorId}",new{connectorId,eventId=item.Id,payload});
    }

    private static async Task<IResult> RecordConnectorCheckpointAsync(Guid id,ConnectorCheckpointRequest request,HttpContext http,AeroLinkDbContext db,IdentityService identity,IntegrationEventPublisher publisher,CancellationToken ct)
    {
        if(!await http.HasProjectRoleAsync(db,identity,request.ProjectId,ct,ProgramRole.Engineer,ProgramRole.ConfigurationManager,ProgramRole.ProgramManager,ProgramRole.Administrator))return Results.Forbid();
        var configured=await db.IntegrationEvents.AsNoTracking().AnyAsync(x=>x.ProjectId==request.ProjectId&&x.AggregateId==id&&x.EventType==ConnectorConfigured,ct);if(!configured)return Results.NotFound(new{error="The connector is not configured for this Project."});
        if(request.Succeeded&&string.IsNullOrWhiteSpace(request.Cursor))return Results.BadRequest(new{error="A successful checkpoint requires a durable cursor."});
        var now=DateTimeOffset.UtcNow;var actor=http.UserAccount().UserName;var sourceHash=(request.SourceHash??"").Trim().ToLowerInvariant();var key=http.Request.Headers["Idempotency-Key"].ToString().Trim();if(key.Length==0)key=$"connector:{id:N}:{IntegrationSecurityService.Hash($"{request.Cursor}|{sourceHash}|{request.Succeeded}|{request.ItemCount}")}";
        var existing=await db.IntegrationEvents.AsNoTracking().SingleOrDefaultAsync(x=>x.ProjectId==request.ProjectId&&x.IdempotencyKey==key,ct);if(existing is not null)return Results.Ok(new{existing.Id,existing.AggregateId,idempotentReplay=true});
        var payload=new{connectorId=id,cursor=request.Cursor?.Trim()??"",operation=request.Operation?.Trim()??"synchronize",request.ItemCount,sourceHash,mappingName=request.MappingName?.Trim()??"",request.MappingVersion,configurationContext=request.ConfigurationContext?.Trim()??"",request.Succeeded,error=request.Error?.Trim(),checkpointedBy=actor,checkpointedAt=now,replayOf=(Guid?)null};
        var item=await publisher.EnqueueAsync(request.ProjectId,ConnectorCheckpoint,"IntegrationConnector",id,payload,actor,now,ct,key);
        return Results.Accepted($"/api/integrations/checkpoints/{item.Id}",new{item.Id,connectorId=id,idempotentReplay=false,payload});
    }

    private static async Task<IResult> ReplayConnectorCheckpointAsync(Guid id,HttpContext http,AeroLinkDbContext db,IdentityService identity,IntegrationEventPublisher publisher,CancellationToken ct)
    {
        var original=await db.IntegrationEvents.AsNoTracking().SingleOrDefaultAsync(x=>x.Id==id&&x.EventType==ConnectorCheckpoint,ct);if(original is null)return Results.NotFound();
        if(!await http.HasProjectRoleAsync(db,identity,original.ProjectId,ct,ProgramRole.Engineer,ProgramRole.ConfigurationManager,ProgramRole.ProgramManager,ProgramRole.Administrator))return Results.Forbid();
        using var document=JsonDocument.Parse(original.PayloadJson);var root=document.RootElement;if(root.TryGetProperty("succeeded",out var succeeded)&&succeeded.GetBoolean())return Results.BadRequest(new{error="Only a failed connector checkpoint needs replay."});
        var replayKey=$"connector-replay:{original.Id:N}";var existing=await db.IntegrationEvents.AsNoTracking().SingleOrDefaultAsync(x=>x.ProjectId==original.ProjectId&&x.IdempotencyKey==replayKey,ct);if(existing is not null)return Results.Ok(new{existing.Id,replayOf=original.Id,idempotentReplay=true});
        var payload=new{connectorId=original.AggregateId,cursor=StringValue(root,"cursor"),operation=StringValue(root,"operation"),itemCount=IntValue(root,"itemCount"),sourceHash=StringValue(root,"sourceHash"),mappingName=StringValue(root,"mappingName"),mappingVersion=IntValue(root,"mappingVersion"),configurationContext=StringValue(root,"configurationContext"),succeeded=false,error="Replay requested; connector worker may safely resume from the retained cursor.",replayOf=original.Id,replayedBy=http.UserAccount().UserName,replayedAt=DateTimeOffset.UtcNow};
        var replay=await publisher.EnqueueAsync(original.ProjectId,ConnectorCheckpoint,"IntegrationConnector",original.AggregateId,payload,http.UserAccount().UserName,DateTimeOffset.UtcNow,ct,replayKey);
        return Results.Accepted($"/api/integrations/checkpoints/{replay.Id}",new{replay.Id,replayOf=original.Id,idempotentReplay=false,payload});
    }

    private static async Task<IResult> GetMappingsAsync(Guid projectId,HttpContext http,AeroLinkDbContext db,CancellationToken ct)
    {
        if(!await http.HasProjectAccessAsync(db,projectId,ct))return Results.Forbid();var mappings=await db.RequirementImportMappings.AsNoTracking().Where(x=>x.ProjectId==projectId).OrderBy(x=>x.Name).ToListAsync(ct);
        return Results.Ok(new{items=mappings.Select(x=>new{x.Id,x.Name,x.MappingJson,x.Version,x.CreatedBy,x.CreatedAt,x.UpdatedAt,sha256=IntegrationSecurityService.Hash(x.MappingJson),historyUrl=$"/api/integrations/mappings/{x.Id}/history"})});
    }

    private static async Task<IResult> CreateMappingAsync(CreateMappingRequest request,HttpContext http,AeroLinkDbContext db,IdentityService identity,IntegrationEventPublisher publisher,CancellationToken ct)
    {
        if(!await http.HasProjectRoleAsync(db,identity,request.ProjectId,ct,ProgramRole.Engineer,ProgramRole.ConfigurationManager,ProgramRole.ProgramManager,ProgramRole.Administrator))return Results.Forbid();
        try
        {
            var now=DateTimeOffset.UtcNow;var actor=http.UserAccount().UserName;var mapping=new RequirementImportMapping(request.ProjectId,request.Name,request.Mapping.GetRawText(),actor,now);db.RequirementImportMappings.Add(mapping);await db.SaveChangesAsync(ct);
            var revision=await publisher.EnqueueAsync(request.ProjectId,MappingRevision,"RequirementImportMapping",mapping.Id,new{mappingId=mapping.Id,mapping.Name,mapping.Version,mapping.MappingJson,sha256=IntegrationSecurityService.Hash(mapping.MappingJson),actor,recordedAt=now},actor,now,ct,$"mapping:{mapping.Id:N}:{mapping.Version}");
            return Results.Created($"/api/integrations/mappings/{mapping.Id}",new{mapping.Id,mapping.Name,mapping.Version,mapping.MappingJson,revisionEventId=revision.Id});
        }
        catch(DomainException ex){return Results.BadRequest(new{error=ex.Message});}
    }

    private static async Task<IResult> UpdateMappingAsync(Guid id,UpdateMappingRequest request,HttpContext http,AeroLinkDbContext db,IdentityService identity,IntegrationEventPublisher publisher,CancellationToken ct)
    {
        var mapping=await db.RequirementImportMappings.SingleOrDefaultAsync(x=>x.Id==id,ct);if(mapping is null)return Results.NotFound();if(!await http.HasProjectRoleAsync(db,identity,mapping.ProjectId,ct,ProgramRole.Engineer,ProgramRole.ConfigurationManager,ProgramRole.ProgramManager,ProgramRole.Administrator))return Results.Forbid();
        try
        {
            var now=DateTimeOffset.UtcNow;var actor=http.UserAccount().UserName;mapping.Update(request.Name,request.Mapping.GetRawText(),actor,request.ExpectedVersion,now);await db.SaveChangesAsync(ct);
            var revision=await publisher.EnqueueAsync(mapping.ProjectId,MappingRevision,"RequirementImportMapping",mapping.Id,new{mappingId=mapping.Id,mapping.Name,mapping.Version,mapping.MappingJson,sha256=IntegrationSecurityService.Hash(mapping.MappingJson),actor,recordedAt=now},actor,now,ct,$"mapping:{mapping.Id:N}:{mapping.Version}");
            return Results.Ok(new{mapping.Id,mapping.Name,mapping.Version,mapping.MappingJson,revisionEventId=revision.Id});
        }
        catch(DomainException ex){return Results.Conflict(new{error=ex.Message});}
    }

    private static async Task<IResult> GetMappingHistoryAsync(Guid id,HttpContext http,AeroLinkDbContext db,CancellationToken ct)
    {
        var mapping=await db.RequirementImportMappings.AsNoTracking().SingleOrDefaultAsync(x=>x.Id==id,ct);if(mapping is null)return Results.NotFound();if(!await http.HasProjectAccessAsync(db,mapping.ProjectId,ct))return Results.Forbid();
        var revisions=await db.IntegrationEvents.AsNoTracking().Where(x=>x.ProjectId==mapping.ProjectId&&x.AggregateId==id&&x.EventType==MappingRevision).OrderBy(x=>x.OccurredAt).ToListAsync(ct);
        return Results.Ok(new{mappingId=id,currentVersion=mapping.Version,currentSha256=IntegrationSecurityService.Hash(mapping.MappingJson),revisions=revisions.Select(x=>new{x.Id,x.OccurredAt,x.Actor,payload=JsonDocument.Parse(x.PayloadJson).RootElement.Clone()})});
    }

    private static async Task<IResult> GetRequirementsAsync(Guid projectId,string? cursor,int? pageSize,HttpContext http,AeroLinkDbContext db,CancellationToken ct)
    {
        var service=http.ServiceIdentity();if(service.ProjectId!=projectId||!service.HasScope("requirements:read"))return Results.Forbid();var size=Math.Clamp(pageSize??50,1,200);
        var query=db.Requirements.AsNoTracking().Where(x=>x.ProjectId==projectId);if(!string.IsNullOrWhiteSpace(cursor))query=query.Where(x=>string.Compare(x.BaseNumber,cursor)>0);
        var artifacts=await query.OrderBy(x=>x.BaseNumber).Take(size+1).ToListAsync(ct);var page=artifacts.Take(size).ToList();var ids=page.Select(x=>x.Id).ToList();var revisions=await db.RequirementRevisions.AsNoTracking().Where(x=>ids.Contains(x.ArtifactId)).OrderByDescending(x=>x.Revision).ToListAsync(ct);var current=revisions.GroupBy(x=>x.ArtifactId).ToDictionary(x=>x.Key,x=>x.First());var next=artifacts.Count>size?page[^1].BaseNumber:null;
        http.Response.Headers.ETag=$"\"{IntegrationSecurityService.Hash(string.Join('|',page.Select(x=>$"{x.Id}:{current.GetValueOrDefault(x.Id)?.Revision}")))}\"";return Results.Ok(new{items=page.Select(x=>new{x.Id,identifier=x.BaseNumber,level=x.Level.ToString(),revision=current.GetValueOrDefault(x.Id)?.Revision,statement=current.GetValueOrDefault(x.Id)?.Statement,state=current.GetValueOrDefault(x.Id)?.State.ToString()}),nextCursor=next,pageSize=size});
    }

    private static async Task<IResult> GetRequirementAsync(Guid id,HttpContext http,AeroLinkDbContext db,CancellationToken ct)
    {var service=http.ServiceIdentity();if(!service.HasScope("requirements:read"))return Results.Forbid();var item=await db.Requirements.AsNoTracking().SingleOrDefaultAsync(x=>x.Id==id&&x.ProjectId==service.ProjectId,ct);if(item is null)return Results.NotFound();var revisions=await db.RequirementRevisions.AsNoTracking().Where(x=>x.ArtifactId==id).OrderByDescending(x=>x.Revision).ToListAsync(ct);http.Response.Headers.ETag=$"\"{IntegrationSecurityService.Hash($"{id}:{revisions.FirstOrDefault()?.Revision}")}\"";return Results.Ok(new{item.Id,identifier=item.BaseNumber,level=item.Level.ToString(),revisions=revisions.Select(x=>new{x.Id,x.Revision,x.Statement,x.Rationale,x.VerificationMethod,state=x.State.ToString(),x.SourceChangeRequestId,x.EffectiveBaselineId,x.CreatedAt})});}

    private static async Task<IResult> ProposeConditionalRequirementWriteAsync(Guid id,ConditionalRequirementWriteRequest request,HttpContext http,AeroLinkDbContext db,CancellationToken ct)
    {
        var service=http.ServiceIdentity();if(!service.HasScope("requirements:write"))return Results.Forbid();var artifact=await db.Requirements.AsNoTracking().SingleOrDefaultAsync(x=>x.Id==id&&x.ProjectId==service.ProjectId,ct);if(artifact is null)return Results.NotFound();var current=await db.RequirementRevisions.AsNoTracking().Where(x=>x.ArtifactId==id).OrderByDescending(x=>x.Revision).FirstOrDefaultAsync(ct);if(current is null)return Results.Conflict(new{error="The requirement has no controlled revision.",code="requirement_revision_missing"});var currentEtag=$"\"{IntegrationSecurityService.Hash($"{id}:{current.Revision}")}\"";if(!string.Equals(http.Request.Headers.IfMatch.ToString(),currentEtag,StringComparison.Ordinal))return Results.Json(new{error="The requirement changed after the caller read it.",code="precondition_failed",currentEtag},statusCode:StatusCodes.Status412PreconditionFailed);if(!await db.Releases.AnyAsync(x=>x.Id==request.TargetReleaseId&&x.ProjectId==service.ProjectId,ct))return Results.BadRequest(new{error="The target release is not part of the service identity Project."});
        try{var now=DateTimeOffset.UtcNow;var number=await IdentifierAllocator.NextChangeRequestAsync(db,request.Type,request.SoftwareLevel,ct);var scr=new SystemChangeRequest(number,0,service.ProjectId,request.TargetReleaseId,request.Title,$"Conditional API change proposed for {artifact.BaseNumber}.",request.Analysis,request.Solution,service.Name,now,request.Type);scr.AddRequirementChange(service.Name,artifact.BaseNumber,current.Revision,artifact.Level,RequirementChangeKind.Modify,request.Statement,request.Rationale,request.VerificationMethod,now,impactDispositionJson:RequirementAuthoringJson.PendingImpactDispositions);db.SystemChangeRequests.Add(scr);db.SecurityAuditEvents.Add(new("ConditionalRequirementWriteProposed",service.Name,artifact.BaseNumber,"Success",$"Created {scr.DisplayNumber} from If-Match {currentEtag}; the controlled requirement remains unchanged until approval and baseline selection.",http.Connection.RemoteIpAddress?.ToString()??"service-api",now));await db.SaveChangesAsync(ct);return Results.Accepted($"/api/change-requests/{scr.Id}",new{scr.Id,scr.DisplayNumber,governance="Controlled change proposal created; direct requirement mutation is prohibited.",sourceEtag=currentEtag});}catch(DomainException ex){return Results.BadRequest(new{error=ex.Message});}
    }

    private static async Task<IResult> GetOslcCatalogAsync(string? configurationContext,HttpContext http,AeroLinkDbContext db,CancellationToken ct)
    {
        var service=http.ServiceIdentity();if(!service.HasScope("oslc:read")&&!service.HasScope("requirements:read"))return Results.Forbid();var project=await db.Projects.AsNoTracking().SingleAsync(x=>x.Id==service.ProjectId,ct);var configuration=configurationContext?.Trim();var suffix=string.IsNullOrWhiteSpace(configuration)?"":$"&configurationContext={Uri.EscapeDataString(configuration)}";
        var queryCapability=new Dictionary<string,object?>{{"oslc:queryBase",$"/api/v1/requirements?projectId={project.Id}{suffix}"}};
        var rmService=new Dictionary<string,object?>{{"oslc:domain","http://open-services.net/ns/rm#"},{"oslc:queryCapability",queryCapability},{"aerolink:consumer","/api/v1/oslc/rm/consume"}};
        if(!string.IsNullOrWhiteSpace(configuration))rmService["oslc_config:configuration"]=configuration;
        var provider=new Dictionary<string,object?>{{"@id",$"/api/v1/oslc/rm/catalog#provider-{project.Id:N}"},{"dcterms:title",project.Name},{"oslc:service",rmService}};
        var catalog=new Dictionary<string,object?>{{"@context",new Dictionary<string,string>{{"oslc","http://open-services.net/ns/core#"},{"oslc_rm","http://open-services.net/ns/rm#"},{"oslc_config","http://open-services.net/ns/config#"},{"dcterms","http://purl.org/dc/terms/"},{"aerolink","urn:aerolink:"}}},{"@type","oslc:ServiceProviderCatalog"},{"dcterms:title",$"AeroLink {project.Name} requirements"},{"oslc:serviceProvider",new[]{provider}}};
        return Results.Json(catalog,contentType:"application/ld+json");
    }

    private static async Task<IResult> GetOslcRequirementAsync(Guid id,string? configurationContext,HttpContext http,AeroLinkDbContext db,CancellationToken ct)
    {
        var service=http.ServiceIdentity();if(!service.HasScope("oslc:read")&&!service.HasScope("requirements:read"))return Results.Forbid();var artifact=await db.Requirements.AsNoTracking().SingleOrDefaultAsync(x=>x.Id==id&&x.ProjectId==service.ProjectId,ct);if(artifact is null)return Results.NotFound();var revision=await db.RequirementRevisions.AsNoTracking().Where(x=>x.ArtifactId==id).OrderByDescending(x=>x.Revision).FirstOrDefaultAsync(ct);if(revision is null)return Results.NotFound();var etag=$"\"{IntegrationSecurityService.Hash($"{id}:{revision.Revision}:{configurationContext}")}\"";http.Response.Headers.ETag=etag;var resource=new Dictionary<string,object?>{{"@context",new Dictionary<string,string>{{"oslc_rm","http://open-services.net/ns/rm#"},{"oslc_config","http://open-services.net/ns/config#"},{"dcterms","http://purl.org/dc/terms/"},{"aerolink","urn:aerolink:"}}},{"@id",$"/api/v1/oslc/rm/requirements/{id}"},{"@type","oslc_rm:Requirement"},{"dcterms:identifier",artifact.BaseNumber},{"dcterms:title",artifact.BaseNumber},{"dcterms:description",revision.Statement},{"aerolink:revision",revision.Revision},{"aerolink:verificationMethod",revision.VerificationMethod},{"aerolink:sourceScr",revision.SourceChangeRequestId}};if(!string.IsNullOrWhiteSpace(configurationContext))resource["oslc_config:configuration"]=configurationContext.Trim();return Results.Json(resource,contentType:"application/ld+json");
    }

    private static async Task<IResult> ConsumeOslcRequirementAsync(OslcConsumeRequirementRequest request,HttpContext http,AeroLinkDbContext db,IntegrationEventPublisher publisher,CancellationToken ct)
    {
        var service=http.ServiceIdentity();if(!service.HasScope("oslc:write"))return Results.Forbid();if(!Uri.TryCreate(request.SourceUri,UriKind.Absolute,out var sourceUri))return Results.BadRequest(new{error="sourceUri must be absolute.",code="invalid_source_uri"});
        var mapping=await db.RequirementImportMappings.AsNoTracking().SingleOrDefaultAsync(x=>x.Id==request.MappingId&&x.ProjectId==service.ProjectId,ct);if(mapping is null)return Results.BadRequest(new{error="The selected mapping does not belong to the service identity Project.",code="mapping_not_found"});if(!await db.Releases.AnyAsync(x=>x.Id==request.TargetReleaseId&&x.ProjectId==service.ProjectId,ct))return Results.BadRequest(new{error="The target release does not belong to the service identity Project.",code="release_not_found"});
        var sourceHash=IntegrationSecurityService.Hash($"{sourceUri}|{request.SourceEtag}|{request.LocalIdentifier}|{request.Statement}|{request.ConfigurationContext}|{mapping.Version}|{mapping.MappingJson}");var key=http.Request.Headers["Idempotency-Key"].ToString().Trim();if(key.Length==0)key=$"oslc-consume:{sourceHash}";if(key.Length>160)return Results.BadRequest(new{error="Idempotency-Key cannot exceed 160 characters."});
        var existing=await db.IntegrationEvents.AsNoTracking().SingleOrDefaultAsync(x=>x.ProjectId==service.ProjectId&&x.IdempotencyKey==key,ct);if(existing is not null)return Results.Ok(new{changeRequestId=existing.AggregateId,eventId=existing.Id,idempotentReplay=true,sourceHash});
        if(!Enum.TryParse<RequirementLevel>(request.Level,true,out var level))return Results.BadRequest(new{error="level must be System, HighLevel, or LowLevel.",code="invalid_level"});
        try
        {
            var now=DateTimeOffset.UtcNow;var number=await IdentifierAllocator.NextChangeRequestAsync(db,request.Type,request.SoftwareLevel,ct);var scr=new SystemChangeRequest(number,0,service.ProjectId,request.TargetReleaseId,request.Title,$"OSLC RM requirement consumed from {sourceUri}.",request.Analysis,request.Solution,service.Name,now,request.Type);scr.AddRequirementChange(service.Name,request.LocalIdentifier,0,level,RequirementChangeKind.Introduce,request.Statement,request.Rationale,request.VerificationMethod,now,impactDispositionJson:RequirementAuthoringJson.PendingImpactDispositions);db.SystemChangeRequests.Add(scr);
            var receipt=await publisher.EnqueueAsync(service.ProjectId,"aerolink.oslc.requirement.consumed","SystemChangeRequest",scr.Id,new{changeRequestId=scr.Id,sourceUri=sourceUri.ToString(),request.SourceEtag,request.ExternalIdentifier,request.LocalIdentifier,request.ConfigurationContext,mappingId=mapping.Id,mappingVersion=mapping.Version,mappingSha256=IntegrationSecurityService.Hash(mapping.MappingJson),sourceHash,consumedBy=service.Name,consumedAt=now,governance="Controlled draft change request; no approved requirement was directly mutated."},service.Name,now,ct,key);
            return Results.Accepted($"/api/change-requests/{scr.Id}",new{scr.Id,scr.DisplayNumber,eventId=receipt.Id,idempotentReplay=false,sourceHash,mappingVersion=mapping.Version,governance="Controlled change proposal created; review, approval, and baseline selection remain required."});
        }
        catch(DomainException ex){return Results.BadRequest(new{error=ex.Message});}
    }

    private static async Task<IResult> GetIntegrationHealthAsync(HttpContext http,AeroLinkDbContext db,CancellationToken ct)
    {
        var service=http.ServiceIdentity();if(!service.HasScope("integrations:read"))return Results.Forbid();var subscriptions=await db.WebhookSubscriptions.AsNoTracking().CountAsync(x=>x.ProjectId==service.ProjectId&&x.IsEnabled,ct);var deliveries=await db.WebhookDeliveries.AsNoTracking().Where(x=>x.ProjectId==service.ProjectId).ToListAsync(ct);var events=await db.IntegrationEvents.AsNoTracking().Where(x=>x.ProjectId==service.ProjectId&&(x.EventType==ConnectorConfigured||x.EventType==ConnectorCheckpoint||x.EventType==MappingRevision)).OrderByDescending(x=>x.OccurredAt).Take(1000).ToListAsync(ct);var connectors=BuildConnectorHealth(events,DateTimeOffset.UtcNow);var status=deliveries.Any(x=>x.State==WebhookDeliveryState.DeadLettered)||connectors.Any(x=>x.Status!="healthy")?"attention":"healthy";return Results.Ok(new{status,projectId=service.ProjectId,enabledSubscriptions=subscriptions,pendingDeliveries=deliveries.Count(x=>x.State is WebhookDeliveryState.Pending or WebhookDeliveryState.RetryScheduled),deadLetters=deliveries.Count(x=>x.State==WebhookDeliveryState.DeadLettered),connectors,mappingRevisionEvents=events.Count(x=>x.EventType==MappingRevision),checkedAt=DateTimeOffset.UtcNow});
    }

    private static async Task<IResult> PublishServiceEventAsync(PublishServiceEventRequest request,HttpContext http,AeroLinkDbContext db,IntegrationEventPublisher publisher,CancellationToken ct)
    {var service=http.ServiceIdentity();if(!service.HasScope("events:write"))return Results.Forbid();var key=http.Request.Headers["Idempotency-Key"].ToString().Trim();if(key.Length is < 8 or > 160)return Results.BadRequest(new{error="Idempotency-Key must contain between 8 and 160 characters.",code="idempotency_key_required"});var existing=await db.IntegrationEvents.AsNoTracking().SingleOrDefaultAsync(x=>x.ProjectId==service.ProjectId&&x.IdempotencyKey==key,ct);if(existing is not null)return Results.Ok(new{existing.Id,existing.EventType,idempotentReplay=true});try{var item=await publisher.EnqueueAsync(service.ProjectId,request.EventType,request.AggregateType,request.AggregateId,request.Data,service.Name,DateTimeOffset.UtcNow,ct,key);return Results.Accepted($"/api/v1/events/{item.Id}",new{item.Id,item.EventType,idempotentReplay=false});}catch(DbUpdateException){var concurrent=await db.IntegrationEvents.AsNoTracking().SingleAsync(x=>x.ProjectId==service.ProjectId&&x.IdempotencyKey==key,ct);return Results.Ok(new{concurrent.Id,concurrent.EventType,idempotentReplay=true});}}

    private static List<ConnectorHealthView> BuildConnectorHealth(IReadOnlyList<IntegrationEvent> events,DateTimeOffset now)
    {
        var configurations=events.Where(x=>x.EventType==ConnectorConfigured).GroupBy(x=>x.AggregateId).Select(x=>x.OrderByDescending(y=>y.OccurredAt).First()).ToList();var checkpoints=events.Where(x=>x.EventType==ConnectorCheckpoint).GroupBy(x=>x.AggregateId).ToDictionary(x=>x.Key,x=>x.OrderByDescending(y=>y.OccurredAt).First());var result=new List<ConnectorHealthView>();
        foreach(var configuration in configurations)
        {
            using var configDocument=JsonDocument.Parse(configuration.PayloadJson);var config=configDocument.RootElement;checkpoints.TryGetValue(configuration.AggregateId,out var checkpoint);string? cursor=null,error=null;bool? succeeded=null;DateTimeOffset? checkpointedAt=null;int mappingVersion=0;string configurationContext=StringValue(config,"configurationContext");
            if(checkpoint is not null){using var checkpointDocument=JsonDocument.Parse(checkpoint.PayloadJson);var payload=checkpointDocument.RootElement;cursor=StringValue(payload,"cursor");error=StringValue(payload,"error");succeeded=BoolValue(payload,"succeeded");mappingVersion=IntValue(payload,"mappingVersion");checkpointedAt=checkpoint.OccurredAt;if(string.IsNullOrWhiteSpace(configurationContext))configurationContext=StringValue(payload,"configurationContext");}
            var staleAfter=Math.Max(5,IntValue(config,"staleAfterMinutes",60));var stale=checkpointedAt is null||checkpointedAt<now.AddMinutes(-staleAfter);var status=succeeded==false?"failed":stale?"stale":"healthy";
            result.Add(new(configuration.AggregateId,StringValue(config,"name"),StringValue(config,"kind"),StringValue(config,"baseUrl"),configurationContext,StringValue(config,"mappingName"),staleAfter,status,cursor,mappingVersion,error,configuration.OccurredAt,checkpointedAt,checkpoint?.Id));
        }
        return result.OrderBy(x=>x.Name).ToList();
    }

    private static string StringValue(JsonElement root,string name)=>root.TryGetProperty(name,out var value)&&value.ValueKind==JsonValueKind.String?value.GetString()??"":"";
    private static int IntValue(JsonElement root,string name,int fallback=0)=>root.TryGetProperty(name,out var value)&&value.TryGetInt32(out var number)?number:fallback;
    private static bool? BoolValue(JsonElement root,string name)=>root.TryGetProperty(name,out var value)&&value.ValueKind is JsonValueKind.True or JsonValueKind.False?value.GetBoolean():null;
    private static AuthenticatedServiceIdentity ServiceIdentity(this HttpContext context)=>context.Items.TryGetValue("AeroLink.ServiceIdentity",out var value)&&value is AuthenticatedServiceIdentity service?service:throw new InvalidOperationException("Service identity context is unavailable.");
}

public sealed record ConnectorHealthView(Guid Id,string Name,string Kind,string BaseUrl,string ConfigurationContext,string MappingName,int StaleAfterMinutes,string Status,string? Cursor,int MappingVersion,string? LastError,DateTimeOffset ConfiguredAt,DateTimeOffset? LastCheckpointAt,Guid? LastCheckpointEventId);
public sealed record CreateServiceIdentityRequest(Guid ProjectId,string Name,List<string> Scopes);
public sealed record CreateWebhookRequest(Guid ProjectId,string Name,string EndpointUrl,List<string> EventTypes);
public sealed record SetWebhookStateRequest(bool Enabled);
public sealed record PublishTestEventRequest(Guid ProjectId,string? Message);
public sealed record PublishServiceEventRequest(string EventType,string AggregateType,Guid AggregateId,JsonElement Data);
public sealed record ConditionalRequirementWriteRequest(Guid TargetReleaseId,string Title,string Analysis,string Solution,string Statement,string Rationale,string VerificationMethod,ChangeRequestType Type=ChangeRequestType.System,RequirementLevel? SoftwareLevel=null);
public sealed record ConfigureConnectorRequest(Guid ProjectId,Guid? ConnectorId,string Name,string Kind,string BaseUrl,string? ConfigurationContext,string? MappingName,int? StaleAfterMinutes);
public sealed record ConnectorCheckpointRequest(Guid ProjectId,string? Cursor,string? Operation,int ItemCount,string? SourceHash,string? MappingName,int MappingVersion,string? ConfigurationContext,bool Succeeded,string? Error);
public sealed record CreateMappingRequest(Guid ProjectId,string Name,JsonElement Mapping);
public sealed record UpdateMappingRequest(string Name,JsonElement Mapping,int ExpectedVersion);
public sealed record OslcConsumeRequirementRequest(Guid TargetReleaseId,Guid MappingId,string SourceUri,string? SourceEtag,string? ExternalIdentifier,string LocalIdentifier,string Level,string Statement,string Rationale,string VerificationMethod,string? ConfigurationContext,string Title,string Analysis,string Solution,ChangeRequestType Type=ChangeRequestType.System,RequirementLevel? SoftwareLevel=null);
