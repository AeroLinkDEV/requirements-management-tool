using AeroLink.Domain.Identity;
using AeroLink.Domain.Integrations;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AeroLink.Api;

/// <summary>Project-authorized metadata observations, never implementation acceptance.</summary>
public static class GitLabMetadataEndpoints
{
    public static void MapGitLabMetadataEndpoints(this WebApplication app)
    {
        app.MapGet("/api/projects/{projectId:guid}/repository/merge-requests", (Guid projectId,
            string? search, string? state, int? page, int? pageSize, HttpContext http, AeroLinkDbContext db,
            GitLabMetadataReader reader, GitLabDisplayMetadataCache cache, IOptions<ProjectGitLabOptions> settings, CancellationToken ct) =>
            ObserveAsync(projectId, http, db, cache, settings.Value, "merge-request-discovery", [search, state ?? "all", page ?? 1, pageSize ?? 20], (configuration, token) => reader.DiscoverMergeRequestsAsync(
                configuration, new(search, state ?? "all", page ?? 1, pageSize ?? 20), token), ct));

        app.MapGet("/api/projects/{projectId:guid}/repository/merge-requests/{iid:int}", (Guid projectId,
            int iid, HttpContext http, AeroLinkDbContext db, GitLabMetadataReader reader,
            GitLabDisplayMetadataCache cache, IOptions<ProjectGitLabOptions> settings, CancellationToken ct) =>
            ObserveAsync(projectId, http, db, cache, settings.Value, "merge-request-detail", [iid], (configuration, token) =>
                reader.GetMergeRequestAsync(configuration, iid, token), ct));

        app.MapGet("/api/projects/{projectId:guid}/repository/commit", (Guid projectId,
            string reference, GitLabReferenceKind? referenceKind, HttpContext http, AeroLinkDbContext db, GitLabMetadataReader reader,
            GitLabDisplayMetadataCache cache, IOptions<ProjectGitLabOptions> settings,
            CancellationToken ct) => ObserveAsync(projectId, http, db, cache, settings.Value, "commit-preview", [reference, referenceKind ?? GitLabReferenceKind.Auto], (configuration, token) =>
                reader.ResolveCommitAsync(configuration, reference, referenceKind ?? GitLabReferenceKind.Auto, token), ct));

        app.MapGet("/api/projects/{projectId:guid}/repository/tree", (Guid projectId, string commit,
            string? path, string? cursor, int? pageSize, HttpContext http, AeroLinkDbContext db,
            GitLabMetadataReader reader, GitLabDisplayMetadataCache cache, IOptions<ProjectGitLabOptions> settings,
            CancellationToken ct) => ObserveAsync(projectId, http, db, cache, settings.Value, "tree", [commit, path, cursor, pageSize ?? 20],
                (configuration, token) => reader.ReadTreePageAsync(configuration, commit, path, cursor,
                    pageSize ?? 20, token), ct));

        // A Code Explorer read must name its stored source identity. Generic repository browsing above
        // makes no claim to be the selected build source and cannot substitute for this boundary.
        app.MapGet("/api/projects/{projectId:guid}/code/source/{sourceSnapshotId:guid}/tree", (Guid projectId,
            Guid sourceSnapshotId, string commit, string? path, string? cursor, int? pageSize,
            HttpContext http, AeroLinkDbContext db, GitLabMetadataReader reader,
            GitLabDisplayMetadataCache cache, IOptions<ProjectGitLabOptions> settings, CancellationToken ct) =>
            ObserveAsync(projectId, http, db, cache, settings.Value, "tree", [commit, path, cursor, pageSize ?? 20],
                (configuration, token) => reader.ReadTreePageAsync(configuration, commit, path, cursor, pageSize ?? 20, token),
                ct, sourceSnapshotId, commit));
    }

    private static async Task<IResult> ObserveAsync<T>(Guid projectId, HttpContext http,
        AeroLinkDbContext db, GitLabDisplayMetadataCache cache, ProjectGitLabOptions settings, string operation,
        object?[] arguments, Func<ProjectRepositoryConfiguration, CancellationToken, Task<GitLabMetadataResult<T>>> observe,
        CancellationToken ct, Guid? sourceSnapshotId = null, string? sourceCommit = null)
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

        if (sourceSnapshotId is not null)
        {
            var snapshot = await db.GitLabSourceSnapshots.AsNoTracking()
                .SingleOrDefaultAsync(x => x.Id == sourceSnapshotId && x.ProjectId == projectId, ct);
            if (snapshot is null) return Results.NotFound();
            if (!MatchesSnapshot(configuration, settings, snapshot, sourceCommit))
                return Results.Conflict(new { code = "repository_changed",
                    error = "The selected source belongs to a different repository configuration. Historical relationships remain readable; select source from the current repository to browse files." });
        }

        var result = await cache.ReadAsync(GitLabDisplayMetadataCache.Key(configuration, settings, operation, arguments),
            token => observe(configuration, token), ct);

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
            remoteProjectId = configuration.RemoteProjectId, checkedAt = result.ObservedAt,
            cache = new { reused = result.Reused, expiresAt = result.ExpiresAt }, observation = result.Observation });
    }

    private static bool MatchesSnapshot(ProjectRepositoryConfiguration configuration, ProjectGitLabOptions settings,
        GitLabSourceSnapshot snapshot, string? commit)
    {
        if (snapshot.RepositoryConfigurationId != configuration.Id || snapshot.RemoteProjectId != configuration.RemoteProjectId
            || !string.Equals(snapshot.PathWithNamespace, configuration.RemotePathWithNamespace, StringComparison.Ordinal)
            || !string.Equals(snapshot.CommitSha, commit, StringComparison.OrdinalIgnoreCase)
            || !Uri.TryCreate(settings.BaseUrl, UriKind.Absolute, out var instance)
            || !Uri.TryCreate(snapshot.InstanceBaseUrl, UriKind.Absolute, out var recordedInstance)
            || instance != recordedInstance || !Uri.TryCreate(configuration.Endpoint, UriKind.Absolute, out var endpoint)) return false;
        var repository = endpoint.AbsoluteUri.TrimEnd('/');
        if (repository.EndsWith(".git", StringComparison.Ordinal)) repository = repository[..^4];
        var expected = instance.AbsoluteUri.TrimEnd('/') + "/" + string.Join('/', snapshot.PathWithNamespace.Split('/').Select(Uri.EscapeDataString));
        return Uri.TryCreate(repository, UriKind.Absolute, out var actual) && actual == new Uri(expected);
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
