using System.Text.RegularExpressions;
using AeroLink.Domain.Common;
using AeroLink.Domain.Hierarchy;
using AeroLink.Domain.Identity;
using AeroLink.Domain.Integrations;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AeroLink.Api;

/// <summary>Project-scoped, source-bound acceptance of immutable Code evidence decisions.</summary>
public static class CodeEvidenceEndpoints
{
    private const int MaxContributions = 50;
    private static readonly Regex FullSha = new("^(?:[0-9a-fA-F]{40}|[0-9a-fA-F]{64})$", RegexOptions.CultureInvariant);

    public static void MapCodeEvidenceEndpoints(this WebApplication app) =>
        app.MapPost("/api/projects/{projectId:guid}/code/evidence", AcceptAsync);

    private static async Task<IResult> AcceptAsync(Guid projectId, AcceptCodeEvidenceRequest request,
        HttpContext http, AeroLinkDbContext db, IdentityService identity,
        IProjectLadderPolicyResolver policyResolver, GitLabMetadataReader reader,
        IOptions<ProjectGitLabOptions> settings, CancellationToken ct)
    {
        if (!await http.HasProjectRoleAsync(db, identity, projectId, ct,
                ProgramRole.Engineer, ProgramRole.ConfigurationManager, ProgramRole.ProgramManager))
            return Results.Forbid();
        var denied = await GitLabMetadataEndpoints.CurrentAccessFailureAsync(projectId, http, db, ct);
        if (denied is not null) return denied;

        var validation = ValidateRequest(projectId, request);
        if (validation is not null) return validation;
        var suppliedContributions = request.Contributions ?? [];

        var observations = new Dictionary<Guid, CodeEvidenceMergeObservation>();
        ProjectRepositoryConfiguration? configuration = null;
        GitLabSourceSnapshot? sourceSnapshot = null;
        if (request.Disposition == CodeEvidenceDisposition.GitLabContributions)
        {
            configuration = await db.ProjectRepositoryConfigurations.AsNoTracking()
                .SingleOrDefaultAsync(x => x.ProjectId == projectId, ct);
            if (configuration is null || configuration.Status != ProjectRepositorySetupStatus.Verified
                || configuration.Version != request.ExpectedConfigurationVersion
                || configuration.RemoteProjectId is not > 0
                || string.IsNullOrWhiteSpace(configuration.RemotePathWithNamespace))
                return RepositoryChanged();

            sourceSnapshot = await db.GitLabSourceSnapshots.AsNoTracking()
                .SingleOrDefaultAsync(x => x.ProjectId == projectId && x.Id == request.ExpectedSourceSnapshotId, ct);
            if (sourceSnapshot is null || sourceSnapshot.RemoteProjectId != configuration.RemoteProjectId
                || !string.Equals(sourceSnapshot.PathWithNamespace, configuration.RemotePathWithNamespace, StringComparison.Ordinal)
                || !string.Equals(sourceSnapshot.InstanceBaseUrl, settings.Value.BaseUrl, StringComparison.OrdinalIgnoreCase))
                return Results.Conflict(new { code = "source_changed", error = "Refresh the verified source selection before accepting evidence." });

            var mergeRequests = suppliedContributions.Where(x => x.Kind == CodeEvidenceContributionKind.MergeRequest)
                .Select(x => x.RelationshipId).ToHashSet();
            var files = suppliedContributions.Where(x => x.Kind == CodeEvidenceContributionKind.File)
                .Select(x => x.RelationshipId).ToHashSet();
            var mergeRows = await db.GitLabMergeRequestRelationships.AsNoTracking()
                .Where(x => x.ProjectId == projectId && mergeRequests.Contains(x.Id)).ToListAsync(ct);
            var fileRows = await db.GitLabFileRelationships.AsNoTracking()
                .Where(x => x.ProjectId == projectId && files.Contains(x.Id)).ToListAsync(ct);
            if (mergeRows.Count != mergeRequests.Count || fileRows.Count != files.Count)
                return Results.NotFound(new { code = "relationship_not_found", error = "One or more Code contribution relationships were not found." });

            foreach (var item in suppliedContributions)
            {
                if (item.Kind == CodeEvidenceContributionKind.File)
                {
                    var row = fileRows.Single(x => x.Id == item.RelationshipId);
                    if (!row.IsActive || row.Version != item.ExpectedRelationshipVersion
                        || row.ReleaseId != request.ReleaseId
                        || row.InstanceBaseUrl != settings.Value.BaseUrl
                        || row.RemoteProjectId != configuration.RemoteProjectId)
                        return RelationshipChanged();
                    continue;
                }

                var merge = mergeRows.Single(x => x.Id == item.RelationshipId);
                if (!merge.IsActive || merge.Version != item.ExpectedRelationshipVersion
                    || merge.ReleaseId != request.ReleaseId
                    || !string.Equals(merge.InstanceBaseUrl, settings.Value.BaseUrl, StringComparison.OrdinalIgnoreCase)
                    || merge.RemoteProjectId != configuration.RemoteProjectId
                    || merge.MergeRequestIid <= 0)
                    return RelationshipChanged();

                var observed = await reader.GetMergeRequestAsync(configuration, merge.MergeRequestIid, ct);
                denied = await GitLabMetadataEndpoints.CurrentAccessFailureAsync(projectId, http, db, ct);
                if (denied is not null) return denied;
                if (!observed.Succeeded || observed.Value is null) return MetadataFailure(observed);
                var details = observed.Value;
                if (details.ProjectId != configuration.RemoteProjectId || details.Iid != merge.MergeRequestIid)
                    return Results.Conflict(new { code = "metadata_invalid", error = "GitLab returned a merge request from a different project or IID." });
                if (!string.Equals(details.State, "merged", StringComparison.OrdinalIgnoreCase) || details.MergedAt is null)
                    return Results.Conflict(new { code = "merge_not_incorporated", error = "The merge request is not confirmed merged by GitLab." });
                var (resultSha, resultKind) = MergeResult(details);
                if (resultSha is null || resultKind is null)
                    return Results.Conflict(new { code = "merge_result_unknown", error = "GitLab did not provide an exact merge or squash result SHA." });
                var ancestry = await reader.ReadCommitAncestryAsync(configuration, resultSha, sourceSnapshot.CommitSha, ct);
                denied = await GitLabMetadataEndpoints.CurrentAccessFailureAsync(projectId, http, db, ct);
                if (denied is not null) return denied;
                if (!ancestry.Succeeded || ancestry.Value is null) return MetadataFailure(ancestry);
                if (!ancestry.Value.IsAncestor)
                    return Results.Conflict(new { code = "merge_not_incorporated", error = "The exact merge result is not an ancestor of the selected source commit." });
                observations.Add(merge.Id, new CodeEvidenceMergeObservation(merge.Id, merge.Version,
                    resultSha, resultKind.Value, details.MergedAt.Value, DateTimeOffset.UtcNow));
            }
        }

        denied = await GitLabMetadataEndpoints.CurrentAccessFailureAsync(projectId, http, db, ct);
        if (denied is not null) return denied;
        await using var scope = await ProjectControlledWriteScope.AcquireAsync(db, projectId, ct);
        try
        {
            var actor = await http.FreshUserForScopeAsync(identity, scope, ct);
            if (actor is null) return Results.Unauthorized();
            if (actor.MustChangePassword || !await http.HasFreshProjectRoleAsync(db, identity, scope, ct,
                    ProgramRole.Engineer, ProgramRole.ConfigurationManager, ProgramRole.ProgramManager))
                return Results.Forbid();
            var policy = await policyResolver.ResolveAsync(projectId, ct);
            var command = new CodeEvidenceAcceptanceCommand(projectId, request.ReleaseId,
                request.RequirementArtifactId, request.RequirementRevisionId, request.Disposition,
                request.ExpectedSelectorVersion, request.ExpectedLegacyRecordId, request.ExpectedConfigurationVersion,
                request.ExpectedSourceSelectionEventId, request.ExpectedSourceSnapshotId,
                request.ExpectedSourceSelectionVersion,
                suppliedContributions.Select(x => new CodeEvidenceContributionRequest(x.Kind, x.RelationshipId,
                    x.ExpectedRelationshipVersion)).ToArray(), request.NoCodeChangeRationale);
            var result = await new CodeEvidenceAcceptanceService(db).AcceptAsync(scope, command, observations,
                policy, actor.UserName, DateTimeOffset.UtcNow, ct);
            await db.SaveChangesAsync(ct);
            await scope.CommitAsync(ct);
            http.Response.Headers.CacheControl = "no-store";
            return Results.Created($"/api/projects/{projectId}/code/evidence/{result.EvidenceSetId}", new
            {
                result.EvidenceSetId, result.SelectorId, result.SelectorVersion,
                disposition = result.Disposition.ToString()
            });
        }
        catch (DomainException ex) { return Results.Conflict(new { code = "evidence_conflict", error = ex.Message }); }
        catch (KeyNotFoundException) { return Results.NotFound(); }
        catch (DbUpdateConcurrencyException) { return Results.Conflict(new { code = "evidence_changed", error = "Controlled Code evidence changed before acceptance committed." }); }
        catch (DbUpdateException) { return Results.Conflict(new { code = "evidence_changed", error = "Controlled Code evidence could not be committed because it changed." }); }
    }

    private static IResult? ValidateRequest(Guid projectId, AcceptCodeEvidenceRequest request)
    {
        if (projectId == Guid.Empty || request.ReleaseId == Guid.Empty || request.RequirementArtifactId == Guid.Empty
            || request.RequirementRevisionId == Guid.Empty || !Enum.IsDefined(request.Disposition))
            return Results.BadRequest(new { code = "invalid_request", error = "Project, release, exact requirement and disposition are required." });
        var contributions = request.Contributions ?? [];
        if (request.ExpectedSelectorVersion < 0 || contributions.Count > MaxContributions
            || contributions.Any(x => x.RelationshipId == Guid.Empty || x.ExpectedRelationshipVersion < 1
                || !Enum.IsDefined(x.Kind))
            || contributions.GroupBy(x => (x.Kind, x.RelationshipId)).Any(x => x.Count() != 1))
            return Results.BadRequest(new { code = "invalid_request", error = "Contributions must be bounded, typed, unique and carry expected relationship versions." });
        if (request.ExpectedLegacyRecordId == Guid.Empty)
            return Results.BadRequest(new { code = "invalid_request", error = "An expected legacy identity must be null or a real record identity." });

        if (request.Disposition == CodeEvidenceDisposition.NoCodeChangeRequired)
        {
            if (contributions.Count != 0 || string.IsNullOrWhiteSpace(request.NoCodeChangeRationale))
                return Results.BadRequest(new { code = "invalid_request", error = "A no-code disposition requires a rationale and zero contributions." });
            if (request.ExpectedConfigurationVersion.HasValue || request.ExpectedSourceSelectionEventId.HasValue
                || request.ExpectedSourceSnapshotId.HasValue || request.ExpectedSourceSelectionVersion.HasValue)
                return Results.BadRequest(new { code = "invalid_request", error = "A no-code disposition cannot include GitLab source expectations." });
        }
        else
        {
            if (contributions.Count == 0 || !string.IsNullOrWhiteSpace(request.NoCodeChangeRationale)
                || request.ExpectedConfigurationVersion is not > 0
                || !request.ExpectedSourceSelectionEventId.HasValue
                || !request.ExpectedSourceSnapshotId.HasValue
                || request.ExpectedSourceSelectionVersion is not > 0)
                return Results.BadRequest(new { code = "invalid_request", error = "A GitLab disposition requires source expectations, contributions and no mixed no-code rationale." });
        }
        return null;
    }

    private static (string? Sha, GitLabMergeResultKind? Kind) MergeResult(GitLabMergeRequestDetails details)
    {
        if (!string.IsNullOrWhiteSpace(details.MergeCommitSha) && FullSha.IsMatch(details.MergeCommitSha))
            return (details.MergeCommitSha.Trim().ToLowerInvariant(), GitLabMergeResultKind.MergeCommit);
        if (!string.IsNullOrWhiteSpace(details.SquashMergeCommitSha) && FullSha.IsMatch(details.SquashMergeCommitSha))
            return (details.SquashMergeCommitSha.Trim().ToLowerInvariant(), GitLabMergeResultKind.SquashCommit);
        return (null, null);
    }

    private static IResult RelationshipChanged() => Results.Conflict(new
    {
        code = "relationship_changed",
        error = "A Code contribution relationship changed before provider observation completed. Refresh before retrying."
    });

    private static IResult RepositoryChanged() => Results.Conflict(new
    {
        code = "repository_changed",
        error = "Refresh the verified repository configuration before accepting evidence."
    });

    private static IResult MetadataFailure<T>(GitLabMetadataResult<T> result) => result.Status switch
    {
        GitLabMetadataStatus.Unauthorized => Results.Unauthorized(),
        GitLabMetadataStatus.Forbidden => Results.Forbid(),
        GitLabMetadataStatus.NotFound => Results.NotFound(new { code = result.Code, error = result.Detail }),
        GitLabMetadataStatus.RateLimited => Results.StatusCode(429),
        GitLabMetadataStatus.Timeout or GitLabMetadataStatus.ServiceUnavailable => Results.StatusCode(502),
        _ => Results.Conflict(new { code = result.Code, error = result.Detail })
    };
}

public sealed record AcceptCodeEvidenceRequest(
    Guid ReleaseId,
    Guid RequirementArtifactId,
    Guid RequirementRevisionId,
    CodeEvidenceDisposition Disposition,
    long ExpectedSelectorVersion,
    Guid? ExpectedLegacyRecordId,
    long? ExpectedConfigurationVersion,
    Guid? ExpectedSourceSelectionEventId,
    Guid? ExpectedSourceSnapshotId,
    long? ExpectedSourceSelectionVersion,
    IReadOnlyList<AcceptCodeEvidenceContribution>? Contributions,
    string? NoCodeChangeRationale);

public sealed record AcceptCodeEvidenceContribution(
    CodeEvidenceContributionKind Kind,
    Guid RelationshipId,
    long ExpectedRelationshipVersion);
