using System.Text.Json;
using System.Text.Json.Serialization;
using AeroLink.Domain.Common;
using AeroLink.Domain.Identity;
using AeroLink.Domain.Integrations;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AeroLink.Api;

/// <summary>
/// Dedicated operator boundary for the one DEC-131 source-only supplement. It never routes through the
/// ordinary source-selection command and exposes no relationship, evidence, or release mutation.
/// </summary>
public static class ReleasedSyntheticSourceSupplementEndpoints
{
    public static void MapReleasedSyntheticSourceSupplementEndpoints(this WebApplication app)
    {
        app.MapPost("/api/projects/{projectId:guid}/code/source/released-supplement/preview", PreviewAsync);
        app.MapPost("/api/projects/{projectId:guid}/code/source/released-supplement/apply", ApplyAsync);
        app.MapGet("/api/projects/{projectId:guid}/code/source/released-supplement", ReadAsync);
    }

    private static async Task<IResult> PreviewAsync(Guid projectId,
        ReleasedSyntheticSourceSupplementRequest request, HttpContext http, AeroLinkDbContext db,
        IdentityService identity, ReleasedSyntheticSourceSupplementService service, CancellationToken ct)
    {
        if (!await http.HasProjectRoleAsync(db, identity, projectId, ct,
                ProgramRole.ConfigurationManager, ProgramRole.ProgramManager)) return Results.Forbid();
        var denied = await GitLabMetadataEndpoints.CurrentAccessFailureAsync(projectId, http, db, ct);
        if (denied is not null) return denied;
        ReleasedSyntheticSourceSupplementManifest manifest;
        try { manifest = request.ToManifest(projectId); }
        catch (DomainException ex) { return Results.BadRequest(new { code = "invalid_manifest", error = ex.Message }); }
        try
        {
            var preflight = await service.PreviewAsync(manifest, ct);
            denied = await GitLabMetadataEndpoints.CurrentAccessFailureAsync(projectId, http, db, ct);
            if (denied is not null) return denied;
            if (!await http.HasProjectRoleAsync(db, identity, projectId, ct,
                    ProgramRole.ConfigurationManager, ProgramRole.ProgramManager)) return Results.Forbid();
            http.Response.Headers.CacheControl = "no-store";
            return Results.Ok(View(preflight, manifest));
        }
        catch (DomainException ex)
        { return Results.Conflict(new { code = "supplement_preflight_refused", error = ex.Message }); }
    }

    private static async Task<IResult> ApplyAsync(Guid projectId,
        ReleasedSyntheticSourceSupplementRequest request, HttpContext http, AeroLinkDbContext db,
        IdentityService identity, ReleasedSyntheticSourceSupplementService service, CancellationToken ct)
    {
        if (!await http.HasProjectRoleAsync(db, identity, projectId, ct,
                ProgramRole.ConfigurationManager, ProgramRole.ProgramManager)) return Results.Forbid();
        var denied = await GitLabMetadataEndpoints.CurrentAccessFailureAsync(projectId, http, db, ct);
        if (denied is not null) return denied;
        ReleasedSyntheticSourceSupplementManifest manifest;
        try { manifest = request.ToManifest(projectId); }
        catch (DomainException ex) { return Results.BadRequest(new { code = "invalid_manifest", error = ex.Message }); }
        try
        {
            // The remote wait is intentionally outside ProjectControlledWriteScope. The access check is repeated
            // after it and the fresh actor/role is resolved again after the project lock is acquired.
            var preflight = await service.PreviewAsync(manifest, ct);
            denied = await GitLabMetadataEndpoints.CurrentAccessFailureAsync(projectId, http, db, ct);
            if (denied is not null) return denied;
            await using var scope = await ProjectControlledWriteScope.AcquireAsync(db, projectId, ct);
            var actor = await http.FreshUserForScopeAsync(identity, scope, ct);
            if (actor is null) return Results.Unauthorized();
            if (actor.MustChangePassword || !await http.HasFreshProjectRoleAsync(db, identity, scope, ct,
                    ProgramRole.ConfigurationManager, ProgramRole.ProgramManager)) return Results.Forbid();
            var result = await service.ApplyAsync(scope, manifest, preflight, actor.UserName,
                DateTimeOffset.UtcNow, ct);
            await db.SaveChangesAsync(ct);
            await scope.CommitAsync(ct);
            http.Response.Headers.CacheControl = "no-store";
            return Results.Ok(View(result));
        }
        catch (DomainException ex)
        { return Results.Conflict(new { code = "supplement_apply_refused", error = ex.Message }); }
        catch (DbUpdateConcurrencyException)
        { return Results.Conflict(new { code = "supplement_changed", error = "The supplement target changed while the operation was running. Preview it again." }); }
    }

    private static async Task<IResult> ReadAsync(Guid projectId, Guid releaseId,
        HttpContext http, AeroLinkDbContext db, CancellationToken ct)
    {
        if (!await http.HasProjectAccessAsync(db, projectId, ct)) return Results.Forbid();
        var denied = await GitLabMetadataEndpoints.CurrentAccessFailureAsync(projectId, http, db, ct);
        if (denied is not null) return denied;
        var supplement = await db.ReleasedSyntheticSourceSupplements.AsNoTracking()
            .SingleOrDefaultAsync(x => x.ProjectId == projectId && x.ReleaseId == releaseId, ct);
        if (supplement is null) return Results.NotFound();
        var snapshot = await db.GitLabSourceSnapshots.AsNoTracking()
            .SingleOrDefaultAsync(x => x.ProjectId == projectId && x.Id == supplement.SourceSnapshotId, ct);
        if (snapshot is null) return Results.Problem("The released source supplement is missing its immutable source snapshot.", statusCode: 500);
        http.Response.Headers.CacheControl = "no-store";
        return Results.Ok(new
        {
            projectId,
            releaseId,
            provenance = new
            {
                kind = "ReleasedSyntheticSourceSupplement",
                recordedAfterRelease = true,
                syntheticHistoricalSupplement = true,
                partOfOriginalReleasePackage = false,
                provesDeliveredBinary = false,
                supplement.Id,
                supplement.OperationId,
                supplement.ManifestDigest,
                supplement.AuthorityScopeVersion,
                supplement.AuthorityScopeDigest,
                supplement.PolicyId,
                supplement.AuthorizationReference,
                supplement.Reason,
                supplement.RecordedBy,
                supplement.RecordedAt,
            },
            source = snapshot,
        });
    }

    private static object View(ReleasedSyntheticSourceSupplementPreflight preflight,
        ReleasedSyntheticSourceSupplementManifest manifest) => new
    {
        projectId = manifest.ProjectId,
        releaseId = manifest.ReleaseId,
        operationId = manifest.OperationId,
        manifestDigest = preflight.ManifestDigest,
        isReplay = preflight.IsReplay,
        proposed = preflight.IsReplay ? null : new
        {
            sourceOnly = true,
            createsCurrentSourceSelection = false,
            createsRelationships = false,
            commitSha = preflight.Observation!.Sha,
            remoteProjectId = preflight.Observation.ProjectId,
            requestedReference = preflight.Observation.RequestedReference,
            repositoryConfigurationId = preflight.RepositoryConfigurationId,
            configurationVersion = preflight.ConfigurationVersion,
        },
        existing = preflight.Existing is null ? null : View(preflight.Existing, true),
    };

    private static object View(ReleasedSyntheticSourceSupplementResult result) => new
    {
        result.ProjectId,
        result.ReleaseId,
        result.SupplementId,
        result.SourceSnapshotId,
        result.OperationId,
        result.ManifestDigest,
        result.CommitSha,
        result.RecordedAt,
        result.RecordedBy,
        result.AuthorityScopeVersion,
        result.AuthorityScopeDigest,
        isReplay = result.IsReplay,
        sourceOnly = true,
        createsCurrentSourceSelection = false,
    };

    private static object View(ReleasedSyntheticSourceSupplement row, bool replay) => new
    {
        row.Id,
        row.SourceSnapshotId,
        row.OperationId,
        row.ManifestDigest,
        row.CommitSha,
        row.RecordedAt,
        row.RecordedBy,
        row.AuthorityScopeVersion,
        row.AuthorityScopeDigest,
        isReplay = replay,
    };

    public sealed class ReleasedSyntheticSourceSupplementRequest
    {
        public int SchemaVersion { get; init; }
        public Guid ProjectId { get; init; }
        public Guid ReleaseId { get; init; }
        public Guid ReleaseCampaignId { get; init; }
        public Guid BaselineId { get; init; }
        public Guid RepositoryConfigurationId { get; init; }
        public long ExpectedConfigurationVersion { get; init; }
        public long ExpectedSourceSelectionVersion { get; init; }
        public string InstanceBaseUrl { get; init; } = string.Empty;
        public long RemoteProjectId { get; init; }
        public string RepositoryPath { get; init; } = string.Empty;
        public string CommitSha { get; init; } = string.Empty;
        public string RequestedReference { get; init; } = string.Empty;
        public GitLabReferenceKind ReferenceKind { get; init; }
        public Guid OperationId { get; init; }
        public string PolicyId { get; init; } = string.Empty;
        public string AuthorizationReference { get; init; } = string.Empty;
        public string Reason { get; init; } = string.Empty;

        [JsonExtensionData]
        public Dictionary<string, JsonElement>? ExtensionData { get; init; }

        public ReleasedSyntheticSourceSupplementManifest ToManifest(Guid routeProjectId)
        {
            if (ExtensionData is { Count: > 0 })
                throw new DomainException("The source-only supplement manifest cannot contain relationship, target, or other unknown fields.");
            if (ProjectId != routeProjectId)
                throw new DomainException("The manifest project identity must match the route project.");
            return new(SchemaVersion, ProjectId, ReleaseId, ReleaseCampaignId, BaselineId,
                RepositoryConfigurationId, ExpectedConfigurationVersion, ExpectedSourceSelectionVersion,
                InstanceBaseUrl, RemoteProjectId, RepositoryPath, CommitSha, RequestedReference,
                ReferenceKind, OperationId, PolicyId, AuthorizationReference, Reason);
        }
    }
}
