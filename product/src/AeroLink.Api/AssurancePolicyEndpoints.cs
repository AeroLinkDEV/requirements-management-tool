using System.Text.Json;
using System.Text.Json.Serialization;
using AeroLink.Domain.Assurance;
using AeroLink.Domain.Common;
using AeroLink.Domain.Identity;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AeroLink.Api;

/// <summary>
/// The project's declared assurance posture: its assurance level, its setting for each enforceable policy
/// lever, and the governed deviations that justify any setting looser than AeroLink recommends.
///
/// Reading is open to anybody with project access, because what the project has declared is exactly what a
/// reviewer needs to see. Recording follows the same authority as the rest of Project Configuration —
/// deciding project policy is a configuration-management act. Approving a *relaxation* is a different and
/// stricter question, and it is answered by the shared assurance authority resolver rather than here.
/// </summary>
public static class AssurancePolicyEndpoints
{
    public static void MapAssurancePolicyEndpoints(this WebApplication app)
    {
        async Task<IResult> Read(Guid projectId, HttpContext http, AeroLinkDbContext db,
            ProjectAuthorityResolver authority,
            ProjectAssurancePolicyService service, CancellationToken ct)
        {
            if (!await http.HasProjectAccessAsync(db, projectId, ct)) return Results.Forbid();
            var canManage = await http.HasApprovalConfigurationAuthorityAsync(db, authority, projectId, ct);
            var policy = await service.ReadAsync(projectId, canManage, ct);
            return policy is null ? Results.NotFound(new { error = "The project does not exist." }) : Results.Ok(policy);
        }

        async Task<IResult> Record(Guid projectId, AssurancePolicyEditRequest request, HttpContext http,
            AeroLinkDbContext db, ProjectAuthorityResolver authority,
            ProjectAssurancePolicyService service, CancellationToken ct)
        {
            if (!await db.Projects.AsNoTracking().AnyAsync(x => x.Id == projectId, ct)) return Results.NotFound();
            if (!await http.HasApprovalConfigurationAuthorityAsync(db, authority, projectId, ct))
                return Results.Forbid();
            // Assurance policy operates within the structure the ladder allows; it can require more of a step
            // that has verification, and it cannot give verification to a step that has none or take it from
            // one that has it. Refusing structural keys outright means a client cannot even attempt to reach
            // the sealed ladder through this route, rather than relying on the handler simply ignoring them.
            if (request.ExtensionData?.Keys.FirstOrDefault(IsStructuralField) is { } structural)
                return Results.BadRequest(new
                {
                    error = $"'{structural}' is structural project configuration and cannot be set through assurance policy. "
                        + "Assurance policy operates within what the sealed ladder allows.",
                });

            if (!Enum.TryParse<AssuranceLevel>(request.DeclaredLevel ?? nameof(AssuranceLevel.NotDeclared), false, out var declaredLevel))
                return Results.BadRequest(new { error = $"'{request.DeclaredLevel}' is not a supported assurance level." });

            var selections = new List<AssuranceSelectionDraft>();
            foreach (var selection in request.Selections ?? [])
            {
                if (!Enum.TryParse<AssurancePolicyLever>(selection.Lever ?? "", false, out var lever))
                    return Results.BadRequest(new { error = $"'{selection.Lever}' is not a supported assurance policy lever." });
                if (!Enum.TryParse<AssuranceLeverValue>(selection.Value ?? "", false, out var value))
                    return Results.BadRequest(new { error = $"'{selection.Value}' is not a supported assurance policy setting." });
                selections.Add(new(lever, value));
            }

            var deviations = new List<AssuranceDeviationDraft>();
            foreach (var deviation in request.Deviations ?? [])
            {
                if (!Enum.TryParse<AssurancePolicyLever>(deviation.Lever ?? "", false, out var lever))
                    return Results.BadRequest(new { error = $"'{deviation.Lever}' is not a supported assurance policy lever." });
                deviations.Add(new(lever, deviation.Scope ?? "Project", deviation.Rationale ?? "",
                    deviation.AirworthinessDesignated, deviation.ApproverUserName ?? ""));
            }

            var actor = http.UserAccount();
            try
            {
                var result = await service.RecordAsync(projectId,
                    new(request.ExpectedVersion, declaredLevel, request.Reason ?? "", selections, deviations),
                    actor.Id, actor.UserName, DateTimeOffset.UtcNow, ct);
                return result.Kind switch
                {
                    AssurancePolicyResultKind.NotFound => Results.NotFound(new { error = result.Error }),
                    AssurancePolicyResultKind.Conflict => Results.Conflict(new { error = result.Error }),
                    AssurancePolicyResultKind.Invalid => Results.BadRequest(new { error = result.Error }),
                    // A refused approval is an authority decision about the named approver, not about the
                    // caller's own access, so it is a 400 carrying the resolver's reason rather than a 403
                    // that would read as "you cannot configure this project".
                    AssurancePolicyResultKind.Refused => Results.BadRequest(new { error = result.Error, code = "deviation_approval_refused" }),
                    _ => Results.Ok(result.Policy),
                };
            }
            catch (DomainException ex) { return Results.BadRequest(new { error = ex.Message }); }
        }

        app.MapGet("/api/projects/{projectId:guid}/assurance-policy", Read);
        app.MapPut("/api/projects/{projectId:guid}/assurance-policy", Record);
    }

    private static bool IsStructuralField(string key) => key.ToLowerInvariant() is
        "steps" or "relationships" or "capabilities" or "hasverification" or "verificationprofile"
        or "enabledartifactkinds" or "catalogueentry" or "classification" or "state";
}

public sealed class AssurancePolicyEditRequest
{
    public int ExpectedVersion { get; set; }
    public string? DeclaredLevel { get; set; }
    public string? Reason { get; set; }
    public List<AssurancePolicySelectionRequest>? Selections { get; set; }
    public List<AssurancePolicyDeviationRequest>? Deviations { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

public sealed class AssurancePolicySelectionRequest
{
    public string? Lever { get; set; }
    public string? Value { get; set; }
}

public sealed class AssurancePolicyDeviationRequest
{
    public string? Lever { get; set; }
    public string? Scope { get; set; }
    public string? Rationale { get; set; }
    public bool AirworthinessDesignated { get; set; }
    public string? ApproverUserName { get; set; }
}
