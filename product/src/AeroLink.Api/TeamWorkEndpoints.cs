using AeroLink.Infrastructure.Persistence;

namespace AeroLink.Api;

public static class TeamWorkEndpoints
{
    public static void MapTeamWorkEndpoints(this WebApplication app)
    {
        app.MapGet("/api/team-work", async (
            Guid projectId,
            HttpContext http,
            AeroLinkDbContext db,
            TeamWorkProjectionService projection,
            CancellationToken ct) =>
        {
            // Authorization is intentionally the first operation. The projection service must never be
            // allowed to load a project's records for an actor who cannot read that project.
            if (!await http.HasProjectAccessAsync(db, projectId, ct))
                return Results.Forbid();

            var result = await projection.ProjectAsync(projectId, ct);
            return result is null
                ? Results.NotFound(new { error = "The project does not exist." })
                : Results.Ok(result);
        });
    }
}
