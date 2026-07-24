using AeroLink.Domain.Identity;
using AeroLink.Infrastructure.Persistence;

namespace AeroLink.Api;

public static class ExternalIdentityAdminEndpoints
{
    public static IEndpointRouteBuilder MapAeroLinkExternalIdentityAdminEndpoints(this IEndpointRouteBuilder app)
    {
        var group=app.MapGroup("/api/admin/external-identity");
        group.MapGet("/providers",ListProvidersAsync);
        group.MapPost("/providers",CreateProviderAsync);
        group.MapPost("/providers/{id:guid}/enabled",SetProviderEnabledAsync);
        group.MapGet("/mappings",ListMappingsAsync);
        group.MapPost("/mappings",CreateMappingAsync);
        group.MapPost("/mappings/{id:guid}/enabled",SetMappingEnabledAsync);
        group.MapPost("/resolve",ResolveAsync);
        return app;
    }

    private static IResult ForbidUnlessAdministrator(HttpContext http)=>http.UserAccount().IsAdministrator?Results.Ok():Results.Forbid();
    private static async Task<IResult> ListProvidersAsync(HttpContext http,ExternalIdentityAdministrationService service,CancellationToken ct)
        =>http.UserAccount().IsAdministrator?Results.Ok(await service.ListProvidersAsync(ct)):Results.Forbid();
    private static async Task<IResult> ListMappingsAsync(Guid? providerId,Guid? programId,HttpContext http,ExternalIdentityAdministrationService service,CancellationToken ct)
        =>http.UserAccount().IsAdministrator?Results.Ok(await service.ListMappingsAsync(providerId,programId,ct)):Results.Forbid();
    private static async Task<IResult> CreateProviderAsync(CreateExternalIdentityProviderRequest request,HttpContext http,ExternalIdentityAdministrationService service,CancellationToken ct)
    {
        var actor=http.UserAccount();if(!actor.IsAdministrator)return Results.Forbid();
        try{var created=await service.CreateProviderAsync(request.Key,request.DisplayName,request.Protocol,request.Issuer,request.SubjectClaim,request.GroupClaim,actor.UserName,http.Connection.RemoteIpAddress?.ToString()??"local",DateTimeOffset.UtcNow,ct);return Results.Created($"/api/admin/external-identity/providers/{created.Id}",created);}
        catch(ArgumentException ex){return Results.BadRequest(new{error=ex.Message});}
        catch(InvalidOperationException ex){return Results.Conflict(new{error=ex.Message,code="external_identity_provider_conflict"});}
    }
    private static async Task<IResult> CreateMappingAsync(CreateExternalGroupRoleMappingRequest request,HttpContext http,ExternalIdentityAdministrationService service,CancellationToken ct)
    {
        var actor=http.UserAccount();if(!actor.IsAdministrator)return Results.Forbid();
        try{var created=await service.CreateMappingAsync(request.ProviderId,request.ExternalGroup,request.ProgramId,request.Role,actor.UserName,http.Connection.RemoteIpAddress?.ToString()??"local",DateTimeOffset.UtcNow,ct);return Results.Created($"/api/admin/external-identity/mappings/{created.Id}",created);}
        catch(ArgumentException ex){return Results.BadRequest(new{error=ex.Message});}
        catch(KeyNotFoundException ex){return Results.NotFound(new{error=ex.Message});}
        catch(InvalidOperationException ex){return Results.Conflict(new{error=ex.Message,code="external_group_role_mapping_conflict"});}
    }
    private static async Task<IResult> SetProviderEnabledAsync(Guid id,SetExternalIdentityEnabledRequest request,HttpContext http,ExternalIdentityAdministrationService service,CancellationToken ct)
    {
        var actor=http.UserAccount();if(!actor.IsAdministrator)return Results.Forbid();var found=await service.SetProviderEnabledAsync(id,request.Enabled,actor.UserName,http.Connection.RemoteIpAddress?.ToString()??"local",DateTimeOffset.UtcNow,ct);return found?Results.NoContent():Results.NotFound();
    }
    private static async Task<IResult> SetMappingEnabledAsync(Guid id,SetExternalIdentityEnabledRequest request,HttpContext http,ExternalIdentityAdministrationService service,CancellationToken ct)
    {
        var actor=http.UserAccount();if(!actor.IsAdministrator)return Results.Forbid();var found=await service.SetMappingEnabledAsync(id,request.Enabled,actor.UserName,http.Connection.RemoteIpAddress?.ToString()??"local",DateTimeOffset.UtcNow,ct);return found?Results.NoContent():Results.NotFound();
    }
    private static async Task<IResult> ResolveAsync(ResolveExternalIdentityRolesRequest request,HttpContext http,ExternalIdentityAdministrationService service,CancellationToken ct)
    {
        if(!http.UserAccount().IsAdministrator)return Results.Forbid();var roles=await service.ResolveRolesAsync(request.ProviderId,request.Issuer,request.ExternalGroups,request.ProgramId,ct);return Results.Ok(new{roles});
    }
}

public sealed record CreateExternalIdentityProviderRequest(string Key,string DisplayName,ExternalIdentityProtocol Protocol,string Issuer,string SubjectClaim,string GroupClaim);
public sealed record CreateExternalGroupRoleMappingRequest(Guid ProviderId,string ExternalGroup,Guid ProgramId,ProgramRole Role);
public sealed record SetExternalIdentityEnabledRequest(bool Enabled);
public sealed record ResolveExternalIdentityRolesRequest(Guid ProviderId,string Issuer,IReadOnlyList<string> ExternalGroups,Guid ProgramId);
