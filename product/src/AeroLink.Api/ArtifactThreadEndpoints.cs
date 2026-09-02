using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AeroLink.Api;

/// <summary>
/// The artifact thread of #880 §5.3: one focal artifact's exact-revision chain across six lanes, inside one
/// exact configuration context.
///
/// <para>
/// Read-only and Digital-Thread-specific. It does not replace <c>/api/traceability/path</c>, which still backs
/// the compact assurance path, and it does not widen the change-request register's one-hop inspector, whose
/// behaviour #866 decision 4 fixed deliberately.
/// </para>
/// </summary>
public static class ArtifactThreadEndpoints
{
    public static void MapArtifactThreadEndpoints(this WebApplication app)
    {
        // baselineId is required because §8.2 requires these views to be build-scoped: it is the governed
        // configuration the page already holds. buildId narrows that to one exact build; without either, a
        // procedure revision run in two builds would return both histories merged, with nothing in the request
        // able to choose between them.
        app.MapGet("/api/artifact-thread", async (Guid projectId, Guid baselineId, Guid? buildId,
            string focalKind, Guid focalId, HttpContext http, AeroLinkDbContext db, CancellationToken ct) =>
        {
            if (!Enum.TryParse<ArtifactThreadFocalKind>(focalKind, ignoreCase: true, out var kind))
                return Results.BadRequest(new { error = "focalKind must be Requirement, Case, Procedure, Execution or Build." });

            // Authorization precedes existence: a caller outside the Project is refused before the projection
            // runs, so a 404 from here never doubles as a probe for what exists in another Project.
            if (!await http.HasProjectAccessAsync(db, projectId, ct)) return Results.Forbid();

            var thread = await ArtifactThreadProjection.BuildAsync(db, projectId, baselineId, buildId, kind, focalId, ct);
            return thread is null ? Results.NotFound() : Results.Ok(thread);
        });
    }
}
