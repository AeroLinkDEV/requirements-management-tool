using AeroLink.Domain.Identity;
using AeroLink.Domain.Integrations;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AeroLink.Api;

/// <summary>Project-authorized metadata observations, never implementation acceptance.</summary>
public static class GitLabMetadataEndpoints
{
    public static void MapGitLabMetadataEndpoints(this WebApplication app)
    {
        app.MapGet("/api/projects/{projectId:guid}/repository/merge-requests", (Guid projectId,
            string? search, string? state, int? page, int? pageSize, HttpContext http, AeroLinkDbContext db,
            GitLabMetadataReader reader, CancellationToken ct) =>
            ObserveAsync(projectId, http, db, (configuration, token) => reader.DiscoverMergeRequestsAsync(
                configuration, new(search, state ?? "all", page ?? 1, pageSize ?? 20), token), ct));

        app.MapGet("/api/projects/{projectId:guid}/repository/merge-requests/{iid:int}", (Guid projectId,
            int iid, HttpContext http, AeroLinkDbContext db, GitLabMetadataReader reader, CancellationToken ct) =>
            ObserveAsync(projectId, http, db, (configuration, token) =>
                reader.GetMergeRequestAsync(configuration, iid, token), ct));

        app.MapGet("/api/projects/{projectId:guid}/repository/commit", (Guid projectId,
            string reference, HttpContext http, AeroLinkDbContext db, GitLabMetadataReader reader,
            CancellationToken ct) => ObserveAsync(projectId, http, db, (configuration, token) =>
                reader.ResolveCommitAsync(configuration, reference, token), ct));

        app.MapGet("/api/projects/{projectId:guid}/repository/tree", (Guid projectId, string commit,
            string? path, string? cursor, int? pageSize, HttpContext http, AeroLinkDbContext db,
            GitLabMetadataReader reader, CancellationToken ct) => ObserveAsync(projectId, http, db,
                (configuration, token) => reader.ReadTreePageAsync(configuration, commit, path, cursor,
                    pageSize ?? 20, token), ct));
    }

    private static async Task<IResult> ObserveAsync<T>(Guid projectId, HttpContext http,
        AeroLinkDbContext db, Func<ProjectRepositoryConfiguration, CancellationToken, Task<T>> observe,
        CancellationToken ct)
    {
        // The ordinary workspace boundary runs before configuration lookup or any remote/cache read.
        if (!await http.HasProjectAccessAsync(db, projectId, ct)) return Results.Forbid();
        var denied = await CurrentAccessFailureAsync(projectId, http, db, ct);
        if (denied is not null) return denied;
        var configuration = await db.ProjectRepositoryConfigurations.AsNoTracking()
            .SingleOrDefaultAsync(x => x.ProjectId == projectId, ct);
        if (configuration is null || configuration.Status != ProjectRepositorySetupStatus.Verified)
            return Results.Conflict(new { code = "repository_unverified",
                error = "Configure and verify this project's GitLab repository before reading remote metadata." });

        var result = await observe(configuration, ct);

        // A remote wait can outlive membership, session, account or repository authority. The request's
        // cached principal is not evidence of current access, and tracked entities may be stale too.
        denied = await CurrentAccessFailureAsync(projectId, http, db, ct);
        if (denied is not null) return denied;
        var current = await db.ProjectRepositoryConfigurations.AsNoTracking()
            .SingleOrDefaultAsync(x => x.ProjectId == projectId, ct);
        if (current is null || current.Id != configuration.Id || current.Version != configuration.Version
            || current.Status != ProjectRepositorySetupStatus.Verified)
            return Results.Conflict(new { code = "repository_changed",
                error = "Repository configuration changed while GitLab was responding. Refresh before retrying." });
        http.Response.Headers.CacheControl = "no-store";
        return Results.Ok(new { configurationId = configuration.Id, configurationVersion = configuration.Version,
            remoteProjectId = configuration.RemoteProjectId, checkedAt = DateTimeOffset.UtcNow, observation = result });
    }

    internal static async Task<IResult?> CurrentAccessFailureAsync(Guid projectId, HttpContext http,
        AeroLinkDbContext db, CancellationToken ct)
    {
        var actorId = http.UserAccount().Id;
        var tokenHash = IdentityService.TokenDigest(http.Request.Cookies[IdentityService.CookieName]);
        if (tokenHash is null) return Results.Unauthorized();
        var session = await db.UserSessions.AsNoTracking()
            .SingleOrDefaultAsync(x => x.UserId == actorId && x.TokenHash == tokenHash, ct);
        if (session is null || !session.IsValid(DateTimeOffset.UtcNow)) return Results.Unauthorized();
        var account = await db.UserAccounts.AsNoTracking().SingleOrDefaultAsync(x => x.Id == actorId, ct);
        if (account is null || account.State != AccountState.Active) return Results.Unauthorized();
        if (account.MustChangePassword) return Results.Forbid();
        var programId = await db.Projects.AsNoTracking().Where(x => x.Id == projectId)
            .Select(x => (Guid?)x.ProgramId).SingleOrDefaultAsync(ct);
        if (programId is null) return Results.Forbid();
        var allowed = account.UserName == IdentityService.SystemAdministratorUserName
            || await db.ProgramMemberships.AsNoTracking()
                .AnyAsync(x => x.UserId == actorId && x.ProgramId == programId.Value && x.EndedAt == null, ct);
        return allowed ? null : Results.Forbid();
    }
}
