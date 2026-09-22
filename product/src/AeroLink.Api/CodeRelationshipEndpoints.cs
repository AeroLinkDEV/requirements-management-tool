using AeroLink.Domain.Common;
using AeroLink.Domain.Identity;
using AeroLink.Domain.Integrations;
using AeroLink.Domain.Releases;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AeroLink.Api;

/// <summary>Project-scoped, independently attributed Code relationship commands and readers.</summary>
public static class CodeRelationshipEndpoints
{
    public static void MapCodeRelationshipEndpoints(this WebApplication app)
    {
        app.MapGet("/api/projects/{projectId:guid}/code/relationships", ReadRelationshipsAsync);
        app.MapGet("/api/projects/{projectId:guid}/code/relationships/{relationshipKind}/{relationshipId:guid}/history", ReadHistoryAsync);
        app.MapPost("/api/projects/{projectId:guid}/code/relationships/merge-requests", AddMergeRequestAsync);
        app.MapPost("/api/projects/{projectId:guid}/code/relationships/files", AddFileAsync);
        app.MapPost("/api/projects/{projectId:guid}/code/relationships/{relationshipKind}/{relationshipId:guid}/withdraw", WithdrawAsync);
        app.MapPost("/api/projects/{projectId:guid}/code/relationships/{relationshipKind}/{relationshipId:guid}/re-add", ReAddAsync);
        app.MapGet("/api/projects/{projectId:guid}/code/merge-requests/register", ReadRegisterAsync);
        app.MapGet("/api/projects/{projectId:guid}/code/merge-requests/{iid:int}", InspectMergeRequestAsync);
    }

    private static async Task<IResult> ReadRelationshipsAsync(Guid projectId, Guid? releaseId,
        string? relationshipKind, string? targetKind, Guid? targetId, int? page, int? pageSize,
        bool? includeWithdrawn, Guid? sourceSnapshotId, string? path,
        HttpContext http, AeroLinkDbContext db, CancellationToken ct)
    {
        if (!await http.HasProjectAccessAsync(db, projectId, ct)) return Results.Forbid();
        var denied = await GitLabMetadataEndpoints.CurrentAccessFailureAsync(projectId, http, db, ct);
        if (denied is not null) return denied;
        if (!TryParseKind(relationshipKind, out CodeRelationshipKind? parsedKind)
            || !TryParseTargetKind(targetKind, out CodeRelationshipTargetKind? parsedTargetKind))
            return Results.BadRequest(new { code = "invalid_filter", error = "The relationship filter is not supported." });
        var hasSourceSnapshot = sourceSnapshotId.HasValue;
        var hasPath = !string.IsNullOrWhiteSpace(path);
        if (hasSourceSnapshot != hasPath || (hasSourceSnapshot && parsedKind != CodeRelationshipKind.File))
            return Results.BadRequest(new { code = "invalid_filter", error = "Exact file filters require relationshipKind=File, sourceSnapshotId and path together." });
        string? normalizedPath = null;
        if (hasPath && (!TryNormalizePath(path!, out normalizedPath) || normalizedPath is null))
            return Results.BadRequest(new { code = "invalid_filter", error = "The exact file path filter is unsafe or empty." });
        if (hasSourceSnapshot && !await db.GitLabSourceSnapshots.AsNoTracking()
                .AnyAsync(x => x.ProjectId == projectId && x.Id == sourceSnapshotId!.Value, ct))
            return Results.NotFound(new { code = "source_not_found", error = "The source snapshot is not owned by this project." });
        try
        {
            var result = await new CodeRelationshipService(db).ReadPageAsync(projectId, releaseId, parsedKind,
                parsedTargetKind, targetId, page ?? 1, pageSize ?? 25, includeWithdrawn == true, ct,
                sourceSnapshotId, normalizedPath);
            http.Response.Headers.CacheControl = "no-store";
            var capabilityState = await RelationshipCapabilityStateAsync(projectId, result.Items.Select(x => x.ReleaseId), http, db, ct);
            var items = result.Items.Select(x => ToReadItem(x,
                capabilityState.CanMutate && capabilityState.MutableReleaseIds.Contains(x.ReleaseId))).ToArray();
            return Results.Ok(new { result.Page, result.PageSize, result.Total,
                items });
        }
        catch (DomainException ex) { return Results.BadRequest(new { code = "invalid_filter", error = ex.Message }); }
    }

    private static async Task<IResult> ReadHistoryAsync(Guid projectId, string relationshipKind,
        Guid relationshipId, HttpContext http, AeroLinkDbContext db, CancellationToken ct)
    {
        if (!await http.HasProjectAccessAsync(db, projectId, ct)) return Results.Forbid();
        var denied = await GitLabMetadataEndpoints.CurrentAccessFailureAsync(projectId, http, db, ct);
        if (denied is not null) return denied;
        if (!TryParseRequiredKind(relationshipKind, out var kind))
            return Results.BadRequest(new { code = "invalid_relationship_kind", error = "The relationship kind is not supported." });
        try
        {
            var events = await new CodeRelationshipService(db).ReadHistoryAsync(projectId, kind, relationshipId, ct);
            http.Response.Headers.CacheControl = "no-store";
            return Results.Ok(new { relationshipKind = kind, relationshipId,
                events = events.Select(x => new { x.Id, eventKind = x.EventKind.ToString(), x.Actor, x.OccurredAt, x.Rationale }) });
        }
        catch (KeyNotFoundException) { return Results.NotFound(); }
    }

    private static async Task<IResult> AddMergeRequestAsync(Guid projectId, AddMergeRequestRequest request,
        HttpContext http, AeroLinkDbContext db, IdentityService identity, GitLabMetadataReader reader,
        IOptions<ProjectGitLabOptions> settings, CancellationToken ct)
    {
        if (!await http.HasProjectRoleAsync(db, identity, projectId, ct, CodeRelationshipService.AllowedMutationRoles.ToArray()))
            return Results.Forbid();
        var denied = await GitLabMetadataEndpoints.CurrentAccessFailureAsync(projectId, http, db, ct);
        if (denied is not null) return denied;
        if (request.ReleaseId == Guid.Empty || request.MergeRequestIid <= 0 || request.ExpectedConfigurationVersion < 1
            || request.TargetId == Guid.Empty || !Enum.IsDefined(request.TargetKind) || !Enum.IsDefined(request.Meaning))
            return Results.BadRequest(new { code = "invalid_request", error = "Release, exact target, meaning, IID and configuration version are required." });
        var configuration = await db.ProjectRepositoryConfigurations.AsNoTracking().SingleOrDefaultAsync(x => x.ProjectId == projectId, ct);
        if (configuration is null || configuration.Status != ProjectRepositorySetupStatus.Verified
            || configuration.Version != request.ExpectedConfigurationVersion)
            return Results.Conflict(new { code = "repository_changed", error = "Refresh the verified repository configuration before recording a relationship." });
        if (!await db.Releases.AsNoTracking().AnyAsync(x => x.ProjectId == projectId && x.Id == request.ReleaseId, ct)) return Results.NotFound();

        var observed = await reader.GetMergeRequestAsync(configuration, request.MergeRequestIid, ct);
        denied = await GitLabMetadataEndpoints.CurrentAccessFailureAsync(projectId, http, db, ct);
        if (denied is not null) return denied;
        if (!observed.Succeeded || observed.Value is null) return MetadataFailure(observed);

        await using var scope = await ProjectControlledWriteScope.AcquireAsync(db, projectId, ct);
        try
        {
            var actor = await http.FreshUserForScopeAsync(identity, scope, ct);
            if (actor is null) return Results.Unauthorized();
            if (actor.MustChangePassword || !await http.HasFreshProjectRoleAsync(db, identity, scope, ct,
                    CodeRelationshipService.AllowedMutationRoles.ToArray())) return Results.Forbid();
            configuration = await db.ProjectRepositoryConfigurations.AsNoTracking().SingleOrDefaultAsync(x => x.ProjectId == projectId, ct);
            if (configuration is null || configuration.Status != ProjectRepositorySetupStatus.Verified
                || configuration.Version != request.ExpectedConfigurationVersion) return RepositoryChanged();
            var target = await CodeRelationshipTargetResolver.ResolveAsync(db, projectId, request.TargetKind, request.TargetId, ct);
            if (target is null) return Results.NotFound(new { code = "target_not_found", error = "The exact relationship target is not part of this project." });
            var result = await new CodeRelationshipService(db).AddMergeRequestAsync(scope, configuration,
                settings.Value.BaseUrl, observed.Value, request.ReleaseId, request.SourceSnapshotId,
                request.SourceSelectionEventId, target, request.Meaning, actor.UserName, DateTimeOffset.UtcNow, ct);
            await db.SaveChangesAsync(ct);
            await scope.CommitAsync(ct);
            http.Response.Headers.CacheControl = "no-store";
            return Results.Ok(MutationResponse(result));
        }
        catch (DomainException ex) { return Results.Conflict(new { code = "relationship_conflict", error = ex.Message }); }
        catch (KeyNotFoundException) { return Results.NotFound(); }
        catch (DbUpdateConcurrencyException) { return Results.Conflict(new { code = "relationship_changed", error = "Another relationship change was saved. Refresh before retrying." }); }
        catch (DbUpdateException) { return Results.Conflict(new { code = "relationship_exists", error = "An equivalent active relationship already exists." }); }
    }

    private static async Task<IResult> AddFileAsync(Guid projectId, AddFileRelationshipRequest request,
        HttpContext http, AeroLinkDbContext db, IdentityService identity, GitLabMetadataReader reader,
        IOptions<ProjectGitLabOptions> settings, CancellationToken ct)
    {
        if (!await http.HasProjectRoleAsync(db, identity, projectId, ct, CodeRelationshipService.AllowedMutationRoles.ToArray()))
            return Results.Forbid();
        var denied = await GitLabMetadataEndpoints.CurrentAccessFailureAsync(projectId, http, db, ct);
        if (denied is not null) return denied;
        if (request.ReleaseId == Guid.Empty || request.SourceSnapshotId == Guid.Empty || request.ExpectedConfigurationVersion < 1
            || request.TargetId == Guid.Empty || string.IsNullOrWhiteSpace(request.CommitSha) || string.IsNullOrWhiteSpace(request.Path)
            || !Enum.IsDefined(request.TargetKind) || !Enum.IsDefined(request.Meaning))
            return Results.BadRequest(new { code = "invalid_request", error = "Release, exact source/tree identity, target, meaning and configuration version are required." });
        var configuration = await db.ProjectRepositoryConfigurations.AsNoTracking().SingleOrDefaultAsync(x => x.ProjectId == projectId, ct);
        var snapshot = await db.GitLabSourceSnapshots.AsNoTracking().SingleOrDefaultAsync(x => x.ProjectId == projectId && x.Id == request.SourceSnapshotId, ct);
        if (configuration is null || snapshot is null || configuration.Status != ProjectRepositorySetupStatus.Verified
            || configuration.Version != request.ExpectedConfigurationVersion || configuration.RemoteProjectId != snapshot.RemoteProjectId)
            return RepositoryChanged();
        if (!string.Equals(snapshot.CommitSha, request.CommitSha.Trim(), StringComparison.OrdinalIgnoreCase))
            return Results.Conflict(new { code = "source_changed", error = "The file commit does not match the immutable source snapshot." });
        if (!TryNormalizePath(request.Path, out var normalizedPath) || !TryNormalizeParent(request.ParentPath, out var parentPath)
            || !IsImmediateChild(normalizedPath, parentPath))
            return Results.BadRequest(new { code = "invalid_path", error = "The file path must be a safe immediate child of the supplied parent path." });

        var tree = await reader.ReadTreePageAsync(configuration, request.CommitSha, parentPath, request.Cursor,
            request.PageSize ?? 50, ct);
        denied = await GitLabMetadataEndpoints.CurrentAccessFailureAsync(projectId, http, db, ct);
        if (denied is not null) return denied;
        if (!tree.Succeeded || tree.Value is null) return MetadataFailure(tree);
        var matchingEntries = tree.Value.Entries.Where(x => string.Equals(x.Path, normalizedPath, StringComparison.Ordinal)).ToArray();
        if (matchingEntries.Length == 0) return Results.Conflict(new { code = "file_unobserved", error = "The requested file was not present on the bounded repository-tree page." });
        if (matchingEntries.Length > 1) return Results.Conflict(new { code = "metadata_invalid", error = "GitLab returned duplicate entries for the requested repository-tree path." });
        var entry = matchingEntries[0];
        if (entry.Kind != GitLabTreeEntryKind.Blob)
            return Results.Conflict(new { code = "file_type_unsupported", error = "Only a regular GitLab blob can be recorded as a file relationship." });

        await using var scope = await ProjectControlledWriteScope.AcquireAsync(db, projectId, ct);
        try
        {
            var actor = await http.FreshUserForScopeAsync(identity, scope, ct);
            if (actor is null) return Results.Unauthorized();
            if (actor.MustChangePassword || !await http.HasFreshProjectRoleAsync(db, identity, scope, ct,
                    CodeRelationshipService.AllowedMutationRoles.ToArray())) return Results.Forbid();
            configuration = await db.ProjectRepositoryConfigurations.AsNoTracking().SingleOrDefaultAsync(x => x.ProjectId == projectId, ct);
            if (configuration is null || configuration.Status != ProjectRepositorySetupStatus.Verified
                || configuration.Version != request.ExpectedConfigurationVersion) return RepositoryChanged();
            var target = await CodeRelationshipTargetResolver.ResolveAsync(db, projectId, request.TargetKind, request.TargetId, ct);
            if (target is null) return Results.NotFound(new { code = "target_not_found", error = "The exact relationship target is not part of this project." });
            var result = await new CodeRelationshipService(db).AddFileAsync(scope, configuration,
                settings.Value.BaseUrl, snapshot.RemoteProjectId, request.ReleaseId, request.SourceSnapshotId,
                request.SourceSelectionEventId, request.CommitSha, normalizedPath, request.StartLine, request.EndLine,
                request.MergeRequestIid, target, request.Meaning, actor.UserName, DateTimeOffset.UtcNow, ct);
            await db.SaveChangesAsync(ct);
            await scope.CommitAsync(ct);
            http.Response.Headers.CacheControl = "no-store";
            return Results.Ok(MutationResponse(result));
        }
        catch (DomainException ex) { return Results.Conflict(new { code = "relationship_conflict", error = ex.Message }); }
        catch (KeyNotFoundException) { return Results.NotFound(); }
        catch (DbUpdateConcurrencyException) { return Results.Conflict(new { code = "relationship_changed", error = "Another relationship change was saved. Refresh before retrying." }); }
        catch (DbUpdateException) { return Results.Conflict(new { code = "relationship_exists", error = "An equivalent active relationship already exists." }); }
    }

    private static async Task<IResult> WithdrawAsync(Guid projectId, string relationshipKind, Guid relationshipId,
        RelationshipVersionRequest request, HttpContext http, AeroLinkDbContext db, IdentityService identity, CancellationToken ct)
        => await TransitionAsync(projectId, relationshipKind, relationshipId, request, false, http, db, identity, ct);

    private static async Task<IResult> ReAddAsync(Guid projectId, string relationshipKind, Guid relationshipId,
        RelationshipVersionRequest request, HttpContext http, AeroLinkDbContext db, IdentityService identity, CancellationToken ct)
        => await TransitionAsync(projectId, relationshipKind, relationshipId, request, true, http, db, identity, ct);

    private static async Task<IResult> TransitionAsync(Guid projectId, string relationshipKind, Guid relationshipId,
        RelationshipVersionRequest request, bool reAdd, HttpContext http, AeroLinkDbContext db, IdentityService identity, CancellationToken ct)
    {
        if (!TryParseRequiredKind(relationshipKind, out var kind))
            return Results.BadRequest(new { code = "invalid_relationship_kind", error = "The relationship kind is not supported." });
        if (!await http.HasProjectRoleAsync(db, identity, projectId, ct, CodeRelationshipService.AllowedMutationRoles.ToArray())) return Results.Forbid();
        var denied = await GitLabMetadataEndpoints.CurrentAccessFailureAsync(projectId, http, db, ct);
        if (denied is not null) return denied;
        if (request.ExpectedVersion < 1 || !reAdd && string.IsNullOrWhiteSpace(request.Rationale))
            return Results.BadRequest(new { code = "invalid_request", error = "An expected version is required, and withdrawal requires a rationale." });
        await using var scope = await ProjectControlledWriteScope.AcquireAsync(db, projectId, ct);
        try
        {
            var actor = await http.FreshUserForScopeAsync(identity, scope, ct);
            if (actor is null) return Results.Unauthorized();
            if (actor.MustChangePassword || !await http.HasFreshProjectRoleAsync(db, identity, scope, ct,
                    CodeRelationshipService.AllowedMutationRoles.ToArray())) return Results.Forbid();
            var service = new CodeRelationshipService(db);
            var result = reAdd
                ? await service.ReAddAsync(scope, kind, relationshipId, request.ExpectedVersion, actor.UserName, DateTimeOffset.UtcNow, ct)
                : await service.WithdrawAsync(scope, kind, relationshipId, request.ExpectedVersion, request.Rationale!, actor.UserName, DateTimeOffset.UtcNow, ct);
            await db.SaveChangesAsync(ct);
            await scope.CommitAsync(ct);
            http.Response.Headers.CacheControl = "no-store";
            return Results.Ok(MutationResponse(result));
        }
        catch (DomainException ex) { return Results.Conflict(new { code = "relationship_conflict", error = ex.Message }); }
        catch (KeyNotFoundException) { return Results.NotFound(); }
        catch (DbUpdateConcurrencyException) { return Results.Conflict(new { code = "relationship_changed", error = "Another relationship change was saved. Refresh before retrying." }); }
        catch (DbUpdateException) { return Results.Conflict(new { code = "relationship_changed", error = "The relationship changed before this operation committed." }); }
    }

    private static async Task<IResult> ReadRegisterAsync(Guid projectId, Guid releaseId, int? page, int? pageSize,
        bool? includeWithdrawn, HttpContext http, AeroLinkDbContext db, GitLabMetadataReader reader, GitLabDisplayMetadataCache cache,
        IOptions<ProjectGitLabOptions> settings, CancellationToken ct)
    {
        if (!await http.HasProjectAccessAsync(db, projectId, ct)) return Results.Forbid();
        var denied = await GitLabMetadataEndpoints.CurrentAccessFailureAsync(projectId, http, db, ct);
        if (denied is not null) return denied;
        if (releaseId == Guid.Empty) return Results.BadRequest(new { code = "release_required", error = "A release is required." });
        if (!await db.Releases.AsNoTracking().AnyAsync(x => x.ProjectId == projectId && x.Id == releaseId, ct)) return Results.NotFound();
        var local = await CodeMergeRequestRegisterProjection.ReadPageAsync(db, projectId, releaseId, page ?? 1,
            pageSize ?? 25, includeWithdrawn == true, ct);
        var configuration = await db.ProjectRepositoryConfigurations.AsNoTracking().SingleOrDefaultAsync(x => x.ProjectId == projectId, ct);
        var current = configuration is { Status: ProjectRepositorySetupStatus.Verified, RemoteProjectId: > 0 }
            ? local.Items.Where(x => x.RemoteProjectId == configuration.RemoteProjectId
                && string.Equals(x.InstanceBaseUrl, settings.Value.BaseUrl, StringComparison.OrdinalIgnoreCase)).ToArray() : [];
        GitLabMetadataResult<IReadOnlyList<GitLabMergeRequestSummary>>? metadata = null;
        GitLabDisplayObservation<IReadOnlyList<GitLabMergeRequestSummary>>? display = null;
        var metadataIsCurrent = false;
        if (current.Length > 0)
        {
            var iids = current.Select(x => x.MergeRequestIid).Distinct().Order().ToArray();
            display = await cache.ReadAsync(GitLabDisplayMetadataCache.Key(configuration!, settings.Value,
                "merge-request-register", iids), token => reader.ReadMergeRequestSummariesAsync(configuration!, iids, token), ct);
            metadata = display.Observation;
        }
        denied = await GitLabMetadataEndpoints.CurrentAccessFailureAsync(projectId, http, db, ct);
        if (denied is not null) return denied;
        if (metadata?.Succeeded == true && configuration is not null)
        {
            var latest = await db.ProjectRepositoryConfigurations.AsNoTracking()
                .SingleOrDefaultAsync(x => x.ProjectId == projectId, ct);
            metadataIsCurrent = latest is not null && latest.Id == configuration.Id && latest.Version == configuration.Version
                && latest.Status == ProjectRepositorySetupStatus.Verified
                && latest.RemoteProjectId == configuration.RemoteProjectId
                && string.Equals(latest.RemotePathWithNamespace, configuration.RemotePathWithNamespace, StringComparison.Ordinal);
        }
        var observations = metadata?.Succeeded == true ? metadata.Value!.ToDictionary(x => x.Iid) : new Dictionary<int, GitLabMergeRequestSummary>();
        http.Response.Headers.CacheControl = "no-store";
        return Results.Ok(new { local.Page, local.PageSize, local.Total,
            metadataCheckedAt = metadataIsCurrent ? display?.ObservedAt : null,
            metadataReused = metadataIsCurrent && display?.Reused == true,
            items = local.Items.Select(x => new { x.InstanceBaseUrl, x.RemoteProjectId, x.MergeRequestIid, x.RelationshipCount,
                metadataKnown = metadataIsCurrent && x.RemoteProjectId == configuration?.RemoteProjectId
                    && string.Equals(x.InstanceBaseUrl, settings.Value.BaseUrl, StringComparison.OrdinalIgnoreCase)
                    && observations.ContainsKey(x.MergeRequestIid),
                metadata = metadataIsCurrent && x.RemoteProjectId == configuration?.RemoteProjectId
                    && string.Equals(x.InstanceBaseUrl, settings.Value.BaseUrl, StringComparison.OrdinalIgnoreCase)
                    && observations.TryGetValue(x.MergeRequestIid, out var row) ? row : null }) });
    }

    private static async Task<IResult> InspectMergeRequestAsync(Guid projectId, int iid, Guid releaseId,
        string? instanceBaseUrl, long? remoteProjectId, HttpContext http, AeroLinkDbContext db,
        GitLabMetadataReader reader, GitLabDisplayMetadataCache cache, IOptions<ProjectGitLabOptions> settings, CancellationToken ct)
    {
        if (!await http.HasProjectAccessAsync(db, projectId, ct)) return Results.Forbid();
        var denied = await GitLabMetadataEndpoints.CurrentAccessFailureAsync(projectId, http, db, ct);
        if (denied is not null) return denied;
        if (iid <= 0 || releaseId == Guid.Empty) return Results.BadRequest(new { code = "invalid_request", error = "Release and merge-request IID are required." });
        var direct = await db.GitLabMergeRequestRelationships.AsNoTracking().Where(x => x.ProjectId == projectId && x.ReleaseId == releaseId && x.MergeRequestIid == iid).ToListAsync(ct);
        var files = await db.GitLabFileRelationships.AsNoTracking().Where(x => x.ProjectId == projectId && x.ReleaseId == releaseId && x.MergeRequestIid == iid).ToListAsync(ct);
        var groups = direct.Select(x => new { x.InstanceBaseUrl, x.RemoteProjectId }).Concat(files.Select(x => new { x.InstanceBaseUrl, x.RemoteProjectId })).Distinct().ToArray();
        if (!string.IsNullOrWhiteSpace(instanceBaseUrl)) groups = groups.Where(x => string.Equals(x.InstanceBaseUrl, instanceBaseUrl, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (remoteProjectId.HasValue) groups = groups.Where(x => x.RemoteProjectId == remoteProjectId.Value).ToArray();
        var configuration = await db.ProjectRepositoryConfigurations.AsNoTracking().SingleOrDefaultAsync(x => x.ProjectId == projectId, ct);
        GitLabMetadataResult<GitLabMergeRequestDetails>? metadata = null;
        GitLabDisplayObservation<GitLabMergeRequestDetails>? display = null;
        if (groups.Length == 1 && configuration is { Status: ProjectRepositorySetupStatus.Verified, RemoteProjectId: > 0 }
            && groups[0].RemoteProjectId == configuration.RemoteProjectId
            && string.Equals(groups[0].InstanceBaseUrl, settings.Value.BaseUrl, StringComparison.OrdinalIgnoreCase))
        {
            display = await cache.ReadAsync(GitLabDisplayMetadataCache.Key(configuration, settings.Value,
                "merge-request-detail", iid), token => reader.GetMergeRequestAsync(configuration, iid, token), ct);
            metadata = display.Observation;
        }
        denied = await GitLabMetadataEndpoints.CurrentAccessFailureAsync(projectId, http, db, ct);
        if (denied is not null) return denied;
        var metadataIsCurrent = false;
        if (metadata?.Succeeded == true && configuration is not null)
        {
            var latest = await db.ProjectRepositoryConfigurations.AsNoTracking()
                .SingleOrDefaultAsync(x => x.ProjectId == projectId, ct);
            metadataIsCurrent = latest is not null && latest.Id == configuration.Id && latest.Version == configuration.Version
                && latest.Status == ProjectRepositorySetupStatus.Verified
                && latest.RemoteProjectId == configuration.RemoteProjectId
                && string.Equals(latest.RemotePathWithNamespace, configuration.RemotePathWithNamespace, StringComparison.Ordinal);
        }
        http.Response.Headers.CacheControl = "no-store";
        var capabilityState = await RelationshipCapabilityStateAsync(projectId,
            direct.Select(x => x.ReleaseId).Concat(files.Select(x => x.ReleaseId)), http, db, ct);
        return Results.Ok(new { projectId, releaseId, mergeRequestIid = iid, identities = groups,
            metadataKnown = metadataIsCurrent, metadata = metadataIsCurrent ? metadata?.Value : null,
            metadataCheckedAt = metadataIsCurrent ? display?.ObservedAt : null,
            metadataReused = metadataIsCurrent && display?.Reused == true,
            observation = metadata is null || metadata.Succeeded ? null : new { metadata.Status, metadata.Code, metadata.Detail },
            mergeRequests = direct.Where(x => groups.Any(g => g.InstanceBaseUrl == x.InstanceBaseUrl && g.RemoteProjectId == x.RemoteProjectId))
                .Select(x => ToReadItem(x, capabilityState.CanMutate && capabilityState.MutableReleaseIds.Contains(x.ReleaseId))),
            files = files.Where(x => groups.Any(g => g.InstanceBaseUrl == x.InstanceBaseUrl && g.RemoteProjectId == x.RemoteProjectId))
                .Select(x => ToReadItem(x, capabilityState.CanMutate && capabilityState.MutableReleaseIds.Contains(x.ReleaseId))) });
    }

    private static object MutationResponse(CodeRelationshipMutation result) => new
    {
        result.RelationshipKind, result.RelationshipId, result.Version, result.IsActive, result.Changed,
        capabilities = new { canWithdraw = result.IsActive, canReAdd = !result.IsActive }
    };

    private static object ToReadItem(CodeRelationshipReadRow x, bool canMutate) => new
    {
        x.Id, relationshipKind = x.RelationshipKind.ToString(), x.ProjectId, x.ReleaseId, x.InstanceBaseUrl,
        x.RemoteProjectId, x.IsActive, x.Version, targetKind = x.TargetKind.ToString(), x.TargetIdentityId,
        x.TargetOwnerIdentityId,
        x.TargetStableIdentity, x.TargetDisplaySnapshot, meaning = x.Meaning.ToString(), x.RecordedBy, x.RecordedAt,
        x.WithdrawnAt, x.WithdrawnBy, x.WithdrawalRationale, x.ReAddedBy, x.ReAddedAt, x.SourceSnapshotId,
        x.SourceSelectionEventId, mergeRequestIid = x.RelationshipKind == CodeRelationshipKind.File ? x.FileMergeRequestIid : x.MergeRequestIid,
        x.MergeRequestId, x.MergeRequestUrlSnapshot, x.MergeRequestTitleSnapshot, x.CommitSha, x.Path, x.StartLine,
        x.EndLine, repositoryPathSnapshot = x.RepositoryPathSnapshot,
        capabilities = new { canWithdraw = canMutate && x.IsActive, canReAdd = canMutate && !x.IsActive }
    };

    private static object ToReadItem(GitLabMergeRequestRelationship x, bool canMutate) => new
    {
        x.Id, relationshipKind = x.RelationshipKind.ToString(), x.ProjectId, x.ReleaseId, x.InstanceBaseUrl,
        x.RemoteProjectId, x.IsActive, x.Version, targetKind = x.TargetKind.ToString(), x.TargetIdentityId,
        x.TargetOwnerIdentityId,
        x.TargetStableIdentity, x.TargetDisplaySnapshot, meaning = x.Meaning.ToString(), x.RecordedBy, x.RecordedAt,
        x.WithdrawnAt, x.WithdrawnBy, x.WithdrawalRationale, x.ReAddedBy, x.ReAddedAt, x.SourceSnapshotId,
        x.SourceSelectionEventId, mergeRequestIid = x.MergeRequestIid, x.MergeRequestId, x.MergeRequestUrlSnapshot,
        x.MergeRequestTitleSnapshot, commitSha = (string?)null, path = (string?)null, startLine = (int?)null,
        endLine = (int?)null, repositoryPathSnapshot = x.RepositoryPathSnapshot,
        capabilities = new { canWithdraw = canMutate && x.IsActive, canReAdd = canMutate && !x.IsActive }
    };

    private static object ToReadItem(GitLabFileRelationship x, bool canMutate) => new
    {
        x.Id, relationshipKind = x.RelationshipKind.ToString(), x.ProjectId, x.ReleaseId, x.InstanceBaseUrl,
        x.RemoteProjectId, x.IsActive, x.Version, targetKind = x.TargetKind.ToString(), x.TargetIdentityId,
        x.TargetOwnerIdentityId,
        x.TargetStableIdentity, x.TargetDisplaySnapshot, meaning = x.Meaning.ToString(), x.RecordedBy, x.RecordedAt,
        x.WithdrawnAt, x.WithdrawnBy, x.WithdrawalRationale, x.ReAddedBy, x.ReAddedAt, x.SourceSnapshotId,
        x.SourceSelectionEventId, mergeRequestId = (long?)null,
        mergeRequestUrlSnapshot = (string?)null, mergeRequestTitleSnapshot = (string?)null, x.CommitSha, x.Path,
        x.StartLine, x.EndLine, mergeRequestIid = x.MergeRequestIid, repositoryPathSnapshot = (string?)null,
        capabilities = new { canWithdraw = canMutate && x.IsActive, canReAdd = canMutate && !x.IsActive }
    };

    private static async Task<RelationshipCapabilityState> RelationshipCapabilityStateAsync(Guid projectId,
        IEnumerable<Guid> releaseIds, HttpContext http, AeroLinkDbContext db, CancellationToken ct)
    {
        var ids = releaseIds.Where(x => x != Guid.Empty).ToHashSet();
        var mutable = ids.Count == 0
            ? new HashSet<Guid>()
            : await db.Releases.AsNoTracking()
                .Where(x => x.ProjectId == projectId && ids.Contains(x.Id) && !x.IsReleased
                    && !db.ReleaseCampaigns.Any(c => c.ProjectId == projectId && c.ReleaseId == x.Id
                        && (c.State == ReleaseCampaignState.InReview || c.State == ReleaseCampaignState.Released)))
                .Select(x => x.Id).ToHashSetAsync(ct);
        return new(await HasFreshMutationRoleAsync(projectId, http, db, ct), mutable);
    }

    private static async Task<bool> HasFreshMutationRoleAsync(Guid projectId, HttpContext http,
        AeroLinkDbContext db, CancellationToken ct)
    {
        var actor = http.UserAccount();
        if (actor.IsAdministrator) return true;
        var programId = await db.Projects.AsNoTracking().Where(x => x.Id == projectId)
            .Select(x => (Guid?)x.ProgramId).SingleOrDefaultAsync(ct);
        if (programId is null) return false;
        var authority = new ProjectAuthorityResolver(db);
        var now = DateTimeOffset.UtcNow;
        foreach (var role in CodeRelationshipService.AllowedMutationRoles)
            if (await authority.IsSatisfiedAsync(actor.Id, programId.Value,
                    ProjectAuthorityRequirement.LegacyRoleDemand(role), now, ct))
                return true;
        return false;
    }

    private sealed record RelationshipCapabilityState(bool CanMutate, HashSet<Guid> MutableReleaseIds);

    private static IResult MetadataFailure<T>(GitLabMetadataResult<T> result) => result.Status switch
    {
        GitLabMetadataStatus.Unauthorized => Results.Unauthorized(),
        GitLabMetadataStatus.Forbidden => Results.Forbid(),
        GitLabMetadataStatus.NotFound => Results.NotFound(new { code = result.Code, error = result.Detail }),
        GitLabMetadataStatus.RateLimited => Results.StatusCode(429),
        GitLabMetadataStatus.Timeout or GitLabMetadataStatus.ServiceUnavailable => Results.StatusCode(502),
        _ => Results.Conflict(new { code = result.Code, error = result.Detail })
    };

    private static IResult RepositoryChanged() => Results.Conflict(new { code = "repository_changed", error = "Refresh the verified repository configuration before retrying." });

    private static bool TryParseRequiredKind(string? value, out CodeRelationshipKind kind)
    {
        if (string.Equals(value, "merge-request", StringComparison.OrdinalIgnoreCase)) { kind = CodeRelationshipKind.MergeRequest; return true; }
        if (string.Equals(value, "file", StringComparison.OrdinalIgnoreCase)) { kind = CodeRelationshipKind.File; return true; }
        return Enum.TryParse(value, true, out kind) && Enum.IsDefined(kind);
    }

    private static bool TryParseKind(string? value, out CodeRelationshipKind? kind)
    {
        if (string.IsNullOrWhiteSpace(value) || string.Equals(value, "all", StringComparison.OrdinalIgnoreCase)) { kind = null; return true; }
        if (TryParseRequiredKind(value, out var parsed)) { kind = parsed; return true; }
        kind = null; return false;
    }

    private static bool TryParseTargetKind(string? value, out CodeRelationshipTargetKind? kind)
    {
        if (string.IsNullOrWhiteSpace(value) || string.Equals(value, "all", StringComparison.OrdinalIgnoreCase)) { kind = null; return true; }
        if (Enum.TryParse(value, true, out CodeRelationshipTargetKind parsed) && Enum.IsDefined(parsed)) { kind = parsed; return true; }
        kind = null; return false;
    }

    private static bool TryNormalizePath(string value, out string path)
    {
        path = value.Trim().Trim('/');
        return path.Length > 0 && !path.Contains("//", StringComparison.Ordinal) && path.Split('/').All(SafeSegment);
    }

    private static bool TryNormalizeParent(string value, out string path)
    {
        path = value.Trim().Trim('/');
        return path.Length == 0 || (!path.Contains("//", StringComparison.Ordinal) && path.Split('/').All(SafeSegment));
    }

    private static bool IsImmediateChild(string path, string parent) => parent.Length == 0
        ? !path.Contains('/') : path.StartsWith(parent + "/", StringComparison.Ordinal) && !path[(parent.Length + 1)..].Contains('/');

    private static bool SafeSegment(string segment) => segment.Length > 0 && segment is not "." and not ".."
        && segment.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-');

    public sealed record AddMergeRequestRequest(Guid ReleaseId, int MergeRequestIid,
        CodeRelationshipTargetKind TargetKind, Guid TargetId, CodeRelationshipMeaning Meaning,
        long ExpectedConfigurationVersion, Guid? SourceSnapshotId = null, Guid? SourceSelectionEventId = null);

    public sealed record AddFileRelationshipRequest(Guid ReleaseId, Guid SourceSnapshotId,
        Guid? SourceSelectionEventId, string CommitSha, string Path, string ParentPath, string? Cursor,
        int? PageSize, int? StartLine, int? EndLine, int? MergeRequestIid, CodeRelationshipTargetKind TargetKind,
        Guid TargetId, CodeRelationshipMeaning Meaning, long ExpectedConfigurationVersion);

    public sealed record RelationshipVersionRequest(long ExpectedVersion, string? Rationale = null);
}
