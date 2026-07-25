using AeroLink.Domain.Common;
using AeroLink.Domain.Contracts;
using AeroLink.Domain.Identity;
using AeroLink.Domain.Integrations;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AeroLink.Api;

/// <summary>
/// The Jira connector's surface.
///
/// AeroLink pushes a change request to the tracker and reads back what the tracker says about it. It does
/// not become the tracker, and the tracker never becomes authoritative for the controlled record — which is
/// why nothing here lets Jira change an AeroLink state.
/// </summary>
public static class JiraEndpoints
{
    public static void MapJiraEndpoints(this WebApplication app)
    {
        app.MapGet("/api/jira/connection", async (Guid projectId, HttpContext http, AeroLinkDbContext db, CancellationToken ct) =>
        {
            if (!await http.HasProjectAccessAsync(db, projectId, ct)) return Results.Forbid();
            var connection = await db.JiraConnections.AsNoTracking().SingleOrDefaultAsync(x => x.ProjectId == projectId, ct);
            return Results.Ok(connection is null ? new { configured = false } : Map(connection));
        });

        app.MapPut("/api/jira/connection", async (SaveJiraConnectionRequest request, HttpContext http,
            AeroLinkDbContext db, IdentityService identity, JiraConnectorService connector, CancellationToken ct) =>
        {
            // Pointing the project at a tracker is a configuration-management act.
            if (!await http.HasProjectRoleAsync(db, identity, request.ProjectId, ct,
                    ProgramRole.ConfigurationManager, ProgramRole.ProgramManager, ProgramRole.Administrator))
                return Results.Forbid();
            try
            {
                var now = DateTimeOffset.UtcNow;
                var actor = http.UserAccount().UserName;
                var existing = await db.JiraConnections.SingleOrDefaultAsync(x => x.ProjectId == request.ProjectId, ct);
                if (existing is null)
                {
                    if (string.IsNullOrWhiteSpace(request.ApiToken))
                        return Results.BadRequest(new { error = "An API token or personal access token is required." });
                    var created = new JiraConnection(request.ProjectId, request.BaseUrl, request.ProjectKey,
                        request.IssueType, request.UserName ?? "", connector.Protect(request.ApiToken), actor, now);
                    db.JiraConnections.Add(created);
                    await db.SaveChangesAsync(ct);
                    return Results.Created($"/api/jira/connection?projectId={request.ProjectId}", Map(created));
                }
                // The stored token is kept when none is supplied. Requiring it to change an issue type would
                // make people paste credentials for a trivial edit, which is how credentials end up in chat.
                existing.Reconfigure(request.ProjectKey, request.IssueType, request.UserName ?? "",
                    string.IsNullOrWhiteSpace(request.ApiToken) ? null : connector.Protect(request.ApiToken), now);
                if (request.IsEnabled is { } enabled) existing.SetEnabled(enabled, now);
                await db.SaveChangesAsync(ct);
                return Results.Ok(Map(existing));
            }
            catch (DomainException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        app.MapPost("/api/jira/connection/verify", async (Guid projectId, HttpContext http, AeroLinkDbContext db,
            IdentityService identity, JiraConnectorService connector, CancellationToken ct) =>
        {
            if (!await http.HasProjectRoleAsync(db, identity, projectId, ct,
                    ProgramRole.ConfigurationManager, ProgramRole.ProgramManager, ProgramRole.Administrator))
                return Results.Forbid();
            var connection = await db.JiraConnections.SingleOrDefaultAsync(x => x.ProjectId == projectId, ct);
            if (connection is null) return Results.NotFound();
            // Checked here rather than discovered on somebody's first push. A broken connection should be
            // visible before it is needed.
            var result = await connector.VerifyAsync(connection, DateTimeOffset.UtcNow, ct);
            return Results.Ok(new { result.Reachable, result.Detail });
        });

        app.MapPost("/api/scrs/{id:guid}/jira", async (Guid id, HttpContext http, IScrRepository repository,
            AeroLinkDbContext db, JiraConnectorService connector, CancellationToken ct) =>
        {
            var scr = await repository.GetAsync(id, ct);
            if (scr is null) return Results.NotFound();
            if (!await http.HasProjectAccessAsync(db, scr.ProjectId, ct)) return Results.Forbid();
            try
            {
                // Deliberately an act somebody takes. Not every change request is programme-tracked work,
                // and creating an issue for every draft would fill a board with things nobody agreed to.
                var link = await connector.PushChangeRequestAsync(scr, http.UserAccount().UserName, DateTimeOffset.UtcNow, ct);
                return link.State == JiraLinkState.Failed
                    ? Results.BadRequest(new { error = link.LastError, link = Map(link) })
                    : Results.Ok(Map(link));
            }
            catch (DomainException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        app.MapGet("/api/scrs/{id:guid}/jira", async (Guid id, HttpContext http, AeroLinkDbContext db, CancellationToken ct) =>
        {
            var projectId = await db.SystemChangeRequests.Where(x => x.Id == id).Select(x => (Guid?)x.ProjectId).SingleOrDefaultAsync(ct);
            if (projectId is null) return Results.NotFound();
            if (!await http.HasProjectAccessAsync(db, projectId.Value, ct)) return Results.Forbid();
            var link = await db.JiraIssueLinks.AsNoTracking()
                .SingleOrDefaultAsync(x => x.ArtifactId == id && x.ArtifactType == "ChangeRequest", ct);
            var configured = await db.JiraConnections.AsNoTracking().AnyAsync(x => x.ProjectId == projectId && x.IsEnabled, ct);
            return Results.Ok(new { configured, link = link is null ? null : Map(link) });
        });

        app.MapGet("/api/jira/links", async (Guid projectId, HttpContext http, AeroLinkDbContext db, CancellationToken ct) =>
        {
            if (!await http.HasProjectAccessAsync(db, projectId, ct)) return Results.Forbid();
            var rows = await db.JiraIssueLinks.AsNoTracking().Where(x => x.ProjectId == projectId).ToListAsync(ct);
            return Results.Ok(rows.OrderByDescending(x => x.UpdatedAt).Select(Map));
        });

        app.MapPost("/api/jira/links/refresh", async (Guid projectId, HttpContext http, AeroLinkDbContext db,
            JiraConnectorService connector, CancellationToken ct) =>
        {
            if (!await http.HasProjectAccessAsync(db, projectId, ct)) return Results.Forbid();
            var refreshed = await connector.RefreshStatusesAsync(projectId, DateTimeOffset.UtcNow, ct);
            return Results.Ok(new { refreshed });
        });
    }

    // The stored token is never returned. A caller can replace it; nobody can read it back.
    private static object Map(JiraConnection x) => new
    {
        configured = true,
        x.Id,
        x.ProjectId,
        x.BaseUrl,
        x.ProjectKey,
        x.IssueType,
        x.UserName,
        x.IsEnabled,
        x.CreatedBy,
        x.CreatedAt,
        x.UpdatedAt,
        x.LastVerifiedAt,
        x.LastError,
    };

    private static object Map(JiraIssueLink x) => new
    {
        x.Id,
        x.ArtifactType,
        x.ArtifactId,
        x.ArtifactNumber,
        x.IssueKey,
        x.IssueUrl,
        x.IssueStatus,
        state = x.State.ToString(),
        x.LastError,
        x.CreatedBy,
        x.CreatedAt,
        x.UpdatedAt,
        x.StatusReadAt,
    };
}

public sealed record SaveJiraConnectionRequest(Guid ProjectId, string BaseUrl, string ProjectKey,
    string IssueType, string? UserName, string? ApiToken, bool? IsEnabled);
