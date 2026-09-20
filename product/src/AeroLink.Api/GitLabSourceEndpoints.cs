using AeroLink.Domain.Common;
using AeroLink.Domain.Identity;
using AeroLink.Domain.Integrations;
using AeroLink.Domain.Releases;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AeroLink.Api;

public static class GitLabSourceEndpoints
{
    public static void MapGitLabSourceEndpoints(this WebApplication app)
    {
        app.MapGet("/api/projects/{projectId:guid}/code/source", ReadAsync);
        app.MapGet("/api/projects/{projectId:guid}/code/source/history", ReadHistoryAsync);
        app.MapPost("/api/projects/{projectId:guid}/code/source", SelectAsync);
    }

    private static async Task<IResult> ReadAsync(Guid projectId, Guid releaseId,
        HttpContext http, AeroLinkDbContext db, IdentityService identity,
        IOptions<ProjectGitLabOptions> settings, CancellationToken ct)
    {
        if (!await http.HasProjectAccessAsync(db, projectId, ct)) return Results.Forbid();
        var denied = await GitLabMetadataEndpoints.CurrentAccessFailureAsync(projectId, http, db, ct);
        if (denied is not null) return denied;
        if (!await db.Releases.AsNoTracking().AnyAsync(x => x.Id == releaseId && x.ProjectId == projectId, ct))
            return Results.NotFound();
        var current = await db.GitLabCurrentSourceSelections.AsNoTracking()
            .SingleOrDefaultAsync(x => x.ProjectId == projectId && x.ReleaseId == releaseId, ct);
        var snapshot = current is null ? null : await db.GitLabSourceSnapshots.AsNoTracking()
            .SingleAsync(x => x.Id == current.SourceSnapshotId && x.ProjectId == projectId, ct);
        var release = await db.Releases.AsNoTracking().SingleAsync(x => x.Id == releaseId && x.ProjectId == projectId, ct);
        var frozen = release.IsReleased || await db.ReleaseCampaigns.AsNoTracking().AnyAsync(x => x.ProjectId == projectId
            && x.ReleaseId == releaseId && (x.State == ReleaseCampaignState.InReview || x.State == ReleaseCampaignState.Released), ct);
        var canSelect = !frozen && await http.HasProjectRoleAsync(db, identity, projectId, ct,
            ProgramRole.Engineer, ProgramRole.ConfigurationManager, ProgramRole.ProgramManager);
        http.Response.Headers.CacheControl = "no-store";
        var demonstration = await GitLabSyntheticDemonstration.ReadAsync(db, projectId, settings.Value, ct);
        return Results.Ok(new { projectId, releaseId, version = current?.Version ?? 0,
            selectionEventId = current?.SelectionEventId, snapshot, demonstration,
            capabilities = new { canSelect, sourceSelectionFrozen = frozen } });
    }

    private static async Task<IResult> ReadHistoryAsync(Guid projectId, Guid releaseId, int? page,
        int? pageSize, HttpContext http, AeroLinkDbContext db, CancellationToken ct)
    {
        if (!await http.HasProjectAccessAsync(db, projectId, ct)) return Results.Forbid();
        var denied = await GitLabMetadataEndpoints.CurrentAccessFailureAsync(projectId, http, db, ct);
        if (denied is not null) return denied;
        if (releaseId == Guid.Empty || page is < 1 or > 100_000 || pageSize is < 1 or > 100)
            return Results.BadRequest(new { code = "invalid_request", error = "Supply a valid release and bounded history page." });
        if (!await db.Releases.AsNoTracking().AnyAsync(x => x.ProjectId == projectId && x.Id == releaseId, ct)) return Results.NotFound();
        var events = db.GitLabSourceSelectionEvents.AsNoTracking()
            .Where(x => x.ProjectId == projectId && x.ReleaseId == releaseId);
        var total = await events.CountAsync(ct);
        var rows = await (from selection in events
                          join snapshot in db.GitLabSourceSnapshots.AsNoTracking()
                              on new { selection.ProjectId, SnapshotId = selection.SourceSnapshotId }
                              equals new { snapshot.ProjectId, SnapshotId = snapshot.Id }
                          orderby selection.ResultingVersion descending
                          select new { selection.Id, selection.ExpectedCurrentVersion, selection.ResultingVersion,
                              selection.SelectedBy, selection.SelectedAt, snapshot }).Skip(((page ?? 1) - 1) * (pageSize ?? 25))
            .Take(pageSize ?? 25).ToListAsync(ct);
        http.Response.Headers.CacheControl = "no-store";
        return Results.Ok(new { page = page ?? 1, pageSize = pageSize ?? 25, total, items = rows });
    }

    private static async Task<IResult> SelectAsync(Guid projectId, SelectGitLabSourceRequest request,
        HttpContext http, AeroLinkDbContext db, IdentityService identity, GitLabMetadataReader reader,
        IOptions<ProjectGitLabOptions> settings, CancellationToken ct)
    {
        if (!await http.HasProjectRoleAsync(db, identity, projectId, ct,
                ProgramRole.Engineer, ProgramRole.ConfigurationManager, ProgramRole.ProgramManager)) return Results.Forbid();
        var denied = await GitLabMetadataEndpoints.CurrentAccessFailureAsync(projectId, http, db, ct);
        if (denied is not null) return denied;
        if (request.ExpectedSelectionVersion < 0 || request.ExpectedConfigurationVersion < 1
            || string.IsNullOrWhiteSpace(request.Reference) || string.IsNullOrWhiteSpace(request.PreviewSha))
            return Results.BadRequest(new { error = "An exact preview and expected configuration/source versions are required." });
        var configuration = await db.ProjectRepositoryConfigurations.AsNoTracking()
            .SingleOrDefaultAsync(x => x.ProjectId == projectId, ct);
        if (configuration is null || configuration.Status != ProjectRepositorySetupStatus.Verified
            || configuration.Version != request.ExpectedConfigurationVersion)
            return Results.Conflict(new { code = "repository_changed", error = "Refresh the verified repository configuration before confirming source." });
        if (!await db.Releases.AsNoTracking().AnyAsync(x => x.Id == request.ReleaseId && x.ProjectId == projectId, ct))
            return Results.NotFound();

        // No project lock is held while GitLab responds. Only the installation's configured origin is
        // allowed to become snapshot identity; the command has no URL, actor or remote-project fields.
        var instanceBaseUrl = settings.Value.BaseUrl;
        var observation = await reader.ResolveCommitAsync(configuration, request.Reference, request.ReferenceKind, ct);
        denied = await GitLabMetadataEndpoints.CurrentAccessFailureAsync(projectId, http, db, ct);
        if (denied is not null) return denied;
        if (!observation.Succeeded || observation.Value is null)
            return Results.Conflict(new { code = "source_unobserved", error = "GitLab could not confirm this exact source reference.", observation });

        await using var scope = await ProjectControlledWriteScope.AcquireAsync(db, projectId, ct);
        var actor = await http.FreshUserForScopeAsync(identity, scope, ct);
        if (actor is null) return Results.Unauthorized();
        if (actor.MustChangePassword || !await http.HasFreshProjectRoleAsync(db, identity, scope, ct,
                ProgramRole.Engineer, ProgramRole.ConfigurationManager, ProgramRole.ProgramManager)) return Results.Forbid();
        try
        {
            var selection = await new GitLabSourceSelectionService(db).SelectAsync(scope, request.ReleaseId,
                configuration, instanceBaseUrl, observation.Value, request.PreviewSha,
                request.ExpectedSelectionVersion, actor.UserName, DateTimeOffset.UtcNow, ct);
            db.SecurityAuditEvents.Add(new("GitLabSourceSelected", actor.UserName, $"GitLabSourceSelection:{selection.Id}",
                "Success", $"Selected exact source {observation.Value.Sha} for build {request.ReleaseId}.",
                http.Connection.RemoteIpAddress?.ToString() ?? "local", selection.SelectedAt));
            await db.SaveChangesAsync(ct);
            await scope.CommitAsync(ct);
            http.Response.Headers.CacheControl = "no-store";
            return Results.Ok(new { selectionEventId = selection.Id, snapshotId = selection.SourceSnapshotId,
                version = selection.ResultingVersion, commitSha = observation.Value.Sha });
        }
        catch (DomainException ex) { return Results.Conflict(new { code = "source_confirmation_conflict", error = ex.Message }); }
        catch (DbUpdateConcurrencyException)
        { return Results.Conflict(new { code = "source_changed", error = "Another source selection was saved. Refresh before retrying." }); }
    }

    public sealed record SelectGitLabSourceRequest(Guid ReleaseId, string Reference,
        GitLabReferenceKind ReferenceKind, string PreviewSha, long ExpectedConfigurationVersion, long ExpectedSelectionVersion);
}
