using System.Text.Json;
using System.Text.Json.Serialization;
using AeroLink.Domain.Identity;
using AeroLink.Domain.Hierarchy;
using AeroLink.Domain.Verification;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AeroLink.Api;

/// <summary>Project-scoped ladder authoring and the one public activation attempt.</summary>
public static class ProjectConfigurationEndpoints
{
    public static void MapProjectConfigurationEndpoints(this WebApplication app)
    {
        async Task<IResult> Read(Guid projectId, HttpContext http, AeroLinkDbContext db, IdentityService identity,
            ProjectLadderAuthoringService service, CancellationToken ct)
        {
            if (!await http.HasProjectAccessAsync(db, projectId, ct)) return Results.Forbid();
            var canManage = await http.HasProjectRoleAsync(db, identity, projectId, ct,
                ProgramRole.ConfigurationManager, ProgramRole.ProgramManager, ProgramRole.Administrator);
            var configuration = await service.ReadAsync(projectId, ct, canManage);
            return configuration is null ? Results.NotFound(new { error = "The project has no ladder configuration." }) : Results.Ok(configuration);
        }

        async Task<IResult> Edit(Guid projectId, ProjectConfigurationEditRequest request, HttpContext http,
            AeroLinkDbContext db, IdentityService identity, ProjectLadderAuthoringService service, CancellationToken ct)
        {
            if (!await db.Projects.AsNoTracking().AnyAsync(x => x.Id == projectId, ct)) return Results.NotFound();
            if (!await http.HasProjectRoleAsync(db, identity, projectId, ct,
                    ProgramRole.ConfigurationManager, ProgramRole.ProgramManager, ProgramRole.Administrator))
                return Results.Forbid();
            if (request.ExtensionData?.Keys.Any(IsLifecycleField) == true)
                return Results.BadRequest(new { error = "Ladder edits cannot set lifecycle, activation, or manifest fields." });
            try
            {
                var steps = (request.Steps ?? []).Select(x => new LadderStepDraft(x.CatalogueEntry ?? "", x.Position,
                    x.Capabilities, x.EnabledArtifactKinds)).ToArray();
                var relationships = (request.Relationships ?? []).Select(x => new LadderRelationshipDraft(x.Parent ?? "", x.Child ?? "")).ToArray();
                var result = await service.EditAsync(projectId,
                    new(request.ExpectedVersion, request.Reason ?? "", steps, relationships),
                    http.UserAccount().UserName, DateTimeOffset.UtcNow, ct);
                return result.Kind switch
                {
                    ProjectLadderEditResultKind.NotFound => Results.NotFound(new { error = result.Error }),
                    ProjectLadderEditResultKind.Conflict => Results.Conflict(new { error = result.Error }),
                    ProjectLadderEditResultKind.Invalid => Results.BadRequest(new { error = result.Error }),
                    _ => Results.Ok(result.Configuration),
                };
            }
            catch (Domain.Common.DomainException ex) { return Results.BadRequest(new { error = ex.Message }); }
        }

        async Task<IResult> Activate(Guid projectId, ProjectConfigurationActivateRequest request, HttpContext http,
            AeroLinkDbContext db, IdentityService identity, ProjectLadderAuthoringService service, CancellationToken ct)
        {
            if (!await db.Projects.AsNoTracking().AnyAsync(x => x.Id == projectId, ct)) return Results.NotFound();
            if (!await http.HasProjectRoleAsync(db, identity, projectId, ct,
                    ProgramRole.ConfigurationManager, ProgramRole.ProgramManager, ProgramRole.Administrator))
                return Results.Forbid();
            var result = await service.ActivateAsync(projectId,
                new(request.ExpectedVersion, request.Reason ?? ""), http.UserAccount().UserName,
                DateTimeOffset.UtcNow, ct);
            if (result.Kind == ProjectLadderActivationResultKind.NotFound) return Results.NotFound(new { error = result.Error });
            if (result.Kind == ProjectLadderActivationResultKind.Conflict) return Results.Conflict(new { error = result.Error });
            if (result.Kind == ProjectLadderActivationResultKind.Invalid) return Results.BadRequest(new { error = result.Error });
            if (result.Kind == ProjectLadderActivationResultKind.Success) return Results.Ok(result.Configuration);
            return Results.Conflict(new
            {
                error = result.Error,
                readiness = result.Readiness,
                blockers = result.Readiness?.MissingOrUnrouted.Select(x => new { x.Id, x.Description }),
            });
        }

        app.MapGet("/api/projects/{projectId:guid}/configuration", Read);
        app.MapPut("/api/projects/{projectId:guid}/configuration", Edit);
        app.MapPost("/api/projects/{projectId:guid}/configuration/activate", Activate);
    }

    private static bool IsLifecycleField(string key) => key.ToLowerInvariant() is
        "state" or "classification" or "activatedat" or "activatedby" or "retiredat" or "retiredby"
        or "activationmanifestversion" or "activationmanifesthash";
}

public sealed class ProjectConfigurationEditRequest
{
    public long ExpectedVersion { get; set; }
    public string? Reason { get; set; }
    public List<ProjectConfigurationStepRequest>? Steps { get; set; }
    public List<ProjectConfigurationRelationshipRequest>? Relationships { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

public sealed class ProjectConfigurationStepRequest
{
    public string? CatalogueEntry { get; set; }
    public int Position { get; set; }
    public LevelCapabilities Capabilities { get; set; }
    public List<VerificationArtifactKind>? EnabledArtifactKinds { get; set; }
}

public sealed class ProjectConfigurationRelationshipRequest
{
    public string? Parent { get; set; }
    public string? Child { get; set; }
}

public sealed class ProjectConfigurationActivateRequest
{
    public long ExpectedVersion { get; set; }
    public string? Reason { get; set; }
}
