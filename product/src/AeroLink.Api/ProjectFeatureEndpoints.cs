using AeroLink.Domain.Identity;
using AeroLink.Domain.Programs;
using AeroLink.Infrastructure.Persistence;

namespace AeroLink.Api;

/// <summary>
/// A project's switched-on features (#1113). Every member reads them, because every surface depends on
/// them. Changing them is Project Configuration authority: Configuration Manager, Program Manager or
/// Administrator, with a reason, under the optimistic version.
/// </summary>
public static class ProjectFeatureEndpoints
{
    private static readonly ProgramRole[] ManagingRoles =
        [ProgramRole.ConfigurationManager, ProgramRole.ProgramManager, ProgramRole.Administrator];

    public static void MapProjectFeatureEndpoints(this WebApplication app)
    {
        app.MapGet("/api/projects/{projectId:guid}/features", async (Guid projectId, HttpContext http,
            AeroLinkDbContext db, IdentityService identity, ProjectFeatureService service, CancellationToken ct) =>
        {
            if (!await http.HasProjectAccessAsync(db, projectId, ct)) return Results.Forbid();
            var read = await service.ReadAsync(projectId, ct);
            if (read is null) return Results.NotFound(new { error = "That project does not exist." });
            return Results.Ok(Projection(read, await http.HasProjectRoleAsync(db, identity, projectId, ct, ManagingRoles)));
        });

        app.MapPut("/api/projects/{projectId:guid}/features", async (Guid projectId, ProjectFeatureEditRequest request,
            HttpContext http, AeroLinkDbContext db, IdentityService identity, ProjectFeatureService service,
            CancellationToken ct) =>
        {
            if (!await http.HasProjectAccessAsync(db, projectId, ct)) return Results.Forbid();
            if (!await http.HasProjectRoleAsync(db, identity, projectId, ct, ManagingRoles)) return Results.Forbid();
            var enabled = ProjectFeature.None;
            foreach (var name in request.Enabled ?? [])
            {
                if (!Enum.TryParse<ProjectFeature>(name, ignoreCase: false, out var feature) || feature == ProjectFeature.None
                    || !ProjectFeatures.Each.Contains(feature))
                    return Results.BadRequest(new { error = $"'{name}' is not a project feature." });
                enabled |= feature;
            }
            var result = await service.ChangeAsync(projectId, enabled, request.ExpectedVersion, request.Reason ?? "",
                http.UserAccount().UserName, http.Connection.RemoteIpAddress?.ToString() ?? "local", DateTimeOffset.UtcNow, ct);
            return result.Kind switch
            {
                ProjectFeatureChangeKind.NotFound => Results.NotFound(new { error = result.Error }),
                ProjectFeatureChangeKind.Invalid => Results.BadRequest(new { error = result.Error }),
                ProjectFeatureChangeKind.Conflict => Results.Conflict(new { error = result.Error }),
                _ => Results.Ok(Projection(result.Read!, canManage: true)),
            };
        });
    }

    private static object Projection(ProjectFeatureRead read, bool canManage) => new
    {
        read.Persisted,
        read.Version,
        canManage,
        enabled = ProjectFeatures.Each.Where(f => read.Enabled.HasFlag(f)).Select(x => x.ToString()),
        features = ProjectFeatures.Each.Select(x => new
        {
            id = x.ToString(),
            label = ProjectFeatures.Label(x),
            enabled = read.Enabled.HasFlag(x),
            hasRecords = read.HasRecords[x],
        }),
        history = read.History.Select(x => new
        {
            x.Version,
            previous = ProjectFeatures.Each.Where(f => x.Previous.HasFlag(f)).Select(f => f.ToString()),
            enabled = ProjectFeatures.Each.Where(f => x.Enabled.HasFlag(f)).Select(f => f.ToString()),
            x.Actor, x.Reason, x.OccurredAt, x.SnapshotHash,
        }),
    };
}

public sealed class ProjectFeatureEditRequest
{
    /// <summary>The feature-set version the caller read. Zero means the project has never changed its features.</summary>
    public long ExpectedVersion { get; set; }
    public string? Reason { get; set; }
    /// <summary>The complete set of features that should be on, by name.</summary>
    public List<string>? Enabled { get; set; }
}
