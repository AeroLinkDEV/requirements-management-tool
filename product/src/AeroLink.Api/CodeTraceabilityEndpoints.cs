using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Common;
using AeroLink.Domain.Identity;
using AeroLink.Domain.Hierarchy;
using AeroLink.Domain.Integrations;
using AeroLink.Domain.Requirements;
using AeroLink.Domain.Releases;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

public static class CodeTraceabilityEndpoints
{
    public static IEndpointRouteBuilder MapCodeTraceabilityEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/code-traceability", ListAsync);
        app.MapPost("/api/code-traceability", CreateAsync);
        return app;
    }

    private static async Task<IResult> ListAsync(Guid projectId, Guid releaseId, HttpContext http, AeroLinkDbContext db,
        IProjectLadderPolicyResolver policyResolver, CancellationToken ct)
    {
        if (!await http.HasProjectAccessAsync(db, projectId, ct)) return Results.Forbid();
        var denied = await AeroLink.Api.GitLabMetadataEndpoints.CurrentAccessFailureAsync(projectId, http, db, ct);
        if (denied is not null) return denied;
        http.Response.Headers.CacheControl = "no-store";
        var ladderPolicy = await policyResolver.ResolveAsync(projectId, ct);
        var release = await db.Releases.AsNoTracking().SingleOrDefaultAsync(x => x.Id == releaseId && x.ProjectId == projectId, ct);
        if (release is null) return Results.NotFound();
        var repository = ProjectRepositoryEvidencePolicy.Readiness(await db.ProjectRepositoryConfigurations.AsNoTracking()
            .SingleOrDefaultAsync(x => x.ProjectId == projectId, ct));

        // The campaign's own baseline, not the one this build inherits.
        //
        // EffectiveBaselineAsync exists so a read-only workspace can show inherited content before a build
        // materializes anything, and reusing it here made this page answer a different question from the
        // release gate it claims to be. Build 1.6 was shown "RELEASE GATE — 80%" computed from Build 1.5's
        // requirement population while the release decision reported the same gate unevaluated, and mappings
        // could be recorded against predecessor revisions that the real gate would never count.
        var campaignBaselineId = await CampaignBaselineAsync(db, projectId, releaseId, ct);
        var materialized = campaignBaselineId is not null && await db.CandidateBaselines.AsNoTracking()
            .AnyAsync(x => x.Id == campaignBaselineId && x.RequirementsMaterializedAt != null, ct);

        // Whatever has already been recorded for this build stays readable even while the gate cannot be
        // evaluated. A mapping is an attributable controlled record; it does not stop existing because the
        // population it belongs to is not ready.
        var recorded = await db.CodeTraceabilityRecords.AsNoTracking()
            .Where(x => x.ProjectId == projectId && x.ReleaseId == releaseId).ToListAsync(ct);
        var frozen = release.IsReleased || await db.ReleaseCampaigns.AsNoTracking().AnyAsync(x => x.ProjectId == projectId
            && x.ReleaseId == releaseId && (x.State == ReleaseCampaignState.InReview || x.State == ReleaseCampaignState.Released), ct);

        if (!materialized)
            return Results.Ok(Waiting(release.Version, frozen, recorded, repository,
                await db.CodeEvidenceDispositionSets.AsNoTracking().CountAsync(x => x.ProjectId == projectId && x.ReleaseId == releaseId, ct)));

        var required = await CodeTraceabilityProjection.RequiredAsync(db, projectId, releaseId, campaignBaselineId!.Value, ladderPolicy, ct);
        var current = await CurrentCodeEvidenceProjection.ForReleaseAsync(db, projectId, releaseId, ct);
        return Results.Ok(Response(release.Version, frozen, campaignBaselineId.Value, required, current, repository));
    }

    /// The one baseline the release decision is made against. A release has at most one campaign.
    private static async Task<Guid?> CampaignBaselineAsync(AeroLinkDbContext db, Guid projectId, Guid releaseId, CancellationToken ct) =>
        await db.ReleaseCampaigns.AsNoTracking().Where(x => x.ProjectId == projectId && x.ReleaseId == releaseId)
            .Select(x => (Guid?)x.BaselineId).SingleOrDefaultAsync(ct);

    private static async Task<IResult> CreateAsync(CreateCodeTraceabilityRequest request, HttpContext http, AeroLinkDbContext db,
        IdentityService identity, IProjectLadderPolicyResolver policyResolver, CancellationToken ct)
    {
        if (!await http.HasProjectRoleAsync(db, identity, request.ProjectId, ct, ProgramRole.Engineer, ProgramRole.ConfigurationManager, ProgramRole.ProgramManager)) return Results.Forbid();
        if (request.Disposition == CodeTraceDisposition.GitLabMerge)
            return Results.Conflict(new
            {
                code = "legacy_gitlab_capture_retired",
                error = "Legacy GitLab evidence writes are retired. Use the source-bound Code evidence acceptance command."
            });
        var release = await db.Releases.AsNoTracking().SingleOrDefaultAsync(x => x.Id == request.ReleaseId && x.ProjectId == request.ProjectId, ct);
        if (release is null) return Results.BadRequest(new { error = "The selected build does not belong to this Project." });
        var ladderPolicy = await policyResolver.ResolveAsync(request.ProjectId, ct);
        if (release.IsReleased) return Results.Conflict(new { error = $"Build {release.Version} is released and read-only." });
        await using var writeScope = await ProjectControlledWriteScope.AcquireAsync(db, request.ProjectId, ct);
        try
        {
            var freshActor = await http.FreshUserForScopeAsync(identity, writeScope, ct);
            if (freshActor is null || !await http.HasFreshProjectRoleAsync(db, identity, writeScope, ct,
                    ProgramRole.Engineer, ProgramRole.ConfigurationManager, ProgramRole.ProgramManager))
                return Results.Forbid();

            release = await db.Releases.AsNoTracking().SingleOrDefaultAsync(x => x.Id == request.ReleaseId && x.ProjectId == request.ProjectId, ct);
            if (release is null) return Results.BadRequest(new { error = "The selected build does not belong to this Project." });
            if (release.IsReleased) return Results.Conflict(new { error = $"Build {release.Version} is released and read-only." });
            if (await db.ReleaseCampaigns.AsNoTracking().AnyAsync(x => x.ProjectId == request.ProjectId
                    && x.ReleaseId == request.ReleaseId
                    && (x.State == ReleaseCampaignState.InReview || x.State == ReleaseCampaignState.Released), ct))
                return Results.Conflict(new { error = "The release package is frozen or released and cannot accept new code traceability.", code = "release_package_frozen" });

            // Hold the observed repository row through evidence commit. A concurrent configuration edit or
            // failed verification must wait for this bounded write; no external call occurs under the lock.
            ProjectRepositoryConfiguration? repositoryConfiguration = null;
            if (request.Disposition == CodeTraceDisposition.GitLabMerge)
            {
                repositoryConfiguration = await db.ProjectRepositoryConfigurations.AsNoTracking()
                    .SingleOrDefaultAsync(x => x.ProjectId == request.ProjectId, ct);
                var refusal = ProjectRepositoryEvidencePolicy.ValidateMerge(repositoryConfiguration, request.RepositoryPath,
                    request.MergeRequestUrl, request.MergeRequestReference);
                if (refusal is not null) return Results.Conflict(new { code = refusal.Code, error = refusal.Error });
            }

            // Mapped against the population the release decision will actually read. Recording against an
            // inherited predecessor revision produced an attributable record that the gate could never count.
            var baselineId = await CampaignBaselineAsync(db, request.ProjectId, request.ReleaseId, ct);
            if (baselineId is null || !await db.CandidateBaselines.AsNoTracking()
                    .AnyAsync(x => x.Id == baselineId && x.RequirementsMaterializedAt != null, ct))
                return Results.Conflict(new
            {
                error = "This build has no materialized requirement population yet, so implementation evidence cannot be recorded against it. Freeze the candidate baseline and materialize its requirements first.",
                code = "waiting_for_materialized_baseline"
            });
            var requiredLevels = ladderPolicy.OrderedLevels.Where(ladderPolicy.HasCodeTraceability).ToArray();
            if (requiredLevels.Length == 0)
                return Results.BadRequest(new { error = "The effective project ladder declares no code-traceability capability." });
            var exactLlr = await (from selection in db.BaselineRequirements.AsNoTracking().Where(x => x.BaselineId == baselineId && x.RevisionId == request.RequirementRevisionId)
                                                       join artifact in db.Requirements.AsNoTracking().Where(x => x.Id == request.RequirementArtifactId && x.ProjectId == request.ProjectId && requiredLevels.Contains(x.Level)) on selection.ArtifactId equals artifact.Id
                                                       select artifact.Id).AnyAsync(ct);
            if (!exactLlr) return Results.BadRequest(new { error = "Code traceability must map an exact requirement revision with code-traceability capability in the selected build baseline." });
            var actor = freshActor; var now = DateTimeOffset.UtcNow;
            var record = new CodeTraceabilityRecord(request.ProjectId, request.ReleaseId, request.RequirementArtifactId, request.RequirementRevisionId,
                request.Disposition, request.RepositoryPath ?? "", request.MergeRequestReference ?? "", request.MergeRequestTitle ?? "",
                request.MergeRequestUrl ?? "", request.MergeCommitSha ?? "", request.MergedAt, request.NoCodeChangeRationale ?? "", false, actor.UserName, now,
                repositoryConfiguration);
            db.CodeTraceabilityRecords.Add(record);
            db.SecurityAuditEvents.Add(new("CodeTraceabilityRecorded", actor.UserName, $"CodeTraceability:{record.Id}", "Success",
                $"Mapped exact LLR revision {request.RequirementRevisionId} as {request.Disposition} for build {request.ReleaseId}.",
                http.Connection.RemoteIpAddress?.ToString() ?? "local", now));
            await db.SaveChangesAsync(ct);
            await writeScope.CommitAsync(ct);
            return Results.Created($"/api/code-traceability/{record.Id}", new { record.Id, disposition = record.Disposition.ToString() });
        }
        catch (DomainException ex) { return Results.BadRequest(new { error = ex.Message }); }
        catch (DbUpdateException) { return Results.Conflict(new { error = "That exact LLR revision already has code traceability for this build." }); }
    }

    /// <summary>
    /// The build has no exact requirement population yet, so the gate has not been evaluated — the same
    /// answer, in the same words, that release readiness gives. Deliberately carries no percentage: a number
    /// here is what let an inherited-baseline calculation read as this build's release gate.
    /// </summary>
    private static object Waiting(string version, bool readOnly, IReadOnlyList<CodeTraceabilityRecord> recorded,
        ProjectRepositoryEvidenceReadiness repository, int evidenceSetCount) => new
    {
        repository,
        build = new { version, readOnly },
        sourceOfTruth = SourceOfTruth,
        evaluationState = "WaitingForPrerequisite",
        demonstrationScope = recorded.Any(x => x.IsDemonstration),
        waiting = new
        {
            detail = "Waiting for a materialized baseline. The exact requirement-revision population does not exist yet, so this gate has not been evaluated.",
            action = "Complete the Requirement baseline materialized gate first: freeze the candidate baseline and materialize its requirements.",
            recordedCount = recorded.Count + evidenceSetCount,
        },
        summary = (object?)null,
        requirements = Array.Empty<object>(),
    };

    private const string SourceOfTruth = "GitLab is the source of truth for source code, merge-request review, and commit content. AeroLink stores immutable traceability pointers only.";

    private static object Response(string version, bool readOnly, Guid campaignBaselineId,
        IReadOnlyList<RequiredCodeTraceabilityRequirement> candidates,
        IReadOnlyList<CurrentCodeEvidence> current, ProjectRepositoryEvidenceReadiness repository)
    {
        var byRevision = current.ToDictionary(x => (x.RequirementArtifactId, x.RequirementRevisionId));
        var mapped = candidates.Count(candidate => byRevision.TryGetValue((candidate.ArtifactId, candidate.RevisionId), out var evidence)
            && evidence.CountsAsImplementation);
        return new
        {
            repository,
            campaignBaselineId,
            build = new { version, readOnly },
            sourceOfTruth = SourceOfTruth,
            evaluationState = "Evaluated",
            demonstrationScope = current.Any(x => x.LegacyRecord?.IsDemonstration == true),
            summary = new { required = candidates.Count, mapped, missing = candidates.Count - mapped, percent = candidates.Count == 0 ? 100 : mapped * 100 / candidates.Count, gateComplete = mapped == candidates.Count },
            requirements = candidates.Select(candidate => new
            {
                candidate.ArtifactId, candidate.RevisionId, displayNumber = $"{candidate.BaseNumber}.{candidate.Revision:D2}", candidate.Statement,
                mapping = byRevision.TryGetValue((candidate.ArtifactId, candidate.RevisionId), out var evidence)
                    ? LegacyMapping(evidence.LegacyRecord) : null,
                evidence = evidence?.Selector is null ? null : new
                {
                    state = evidence.State.ToString(), evidence.CountsAsImplementation,
                    selectorVersion = evidence.Selector.Version, evidenceSetId = evidence.Selector.EvidenceSetId,
                    disposition = evidence.EvidenceSet?.Disposition.ToString(),
                    evidence.EvidenceSet?.NoCodeChangeRationale, evidence.EvidenceSet?.RecordedBy, evidence.EvidenceSet?.RecordedAt,
                    evidence.EvidenceSet?.SourceSnapshotId, evidence.EvidenceSet?.SourceSelectionEventId,
                    evidence.EvidenceSet?.SupersededLegacyRecordId, evidence.InvalidationRationale,
                    contributions = evidence.Contributions.Select(x => new
                    {
                        x.Id, kind = x.ContributionKind.ToString(), x.RelationshipId, x.InstanceBaseUrl, x.RemoteProjectId,
                        repositoryPath = x.RepositoryPathSnapshot, x.MergeRequestIid,
                        mergeRequestTitle = x.MergeRequestTitleSnapshot, mergeRequestUrl = x.MergeRequestUrlSnapshot,
                        x.CommitSha, path = x.FilePath, x.StartLine, x.EndLine, x.MergeResultSha,
                        mergeResultKind = x.MergeResultKind?.ToString(), x.MergedAt, x.ProviderObservedAt, x.RecordedBy, x.RecordedAt
                    })
                }
            })
        };
    }
    private static object? LegacyMapping(CodeTraceabilityRecord? record) => record is null ? null : new
    {
        id = record.Id, disposition = record.Disposition.ToString(), record.RepositoryPath, record.MergeRequestReference,
        record.MergeRequestTitle, record.MergeRequestUrl, record.MergeCommitSha, record.MergedAt, record.NoCodeChangeRationale,
        record.VerifiedRemoteProjectId, record.VerifiedRepositoryEndpoint, record.VerifiedRepositoryPath,
        record.RepositoryConfigurationVersion, record.RepositoryVerifiedAt, record.RepositoryVerifiedBy,
        record.IsDemonstration, record.RecordedBy, record.RecordedAt
    };
    private sealed record CreateCodeTraceabilityRequest(Guid ProjectId, Guid ReleaseId, Guid RequirementArtifactId, Guid RequirementRevisionId,
        CodeTraceDisposition Disposition, string? RepositoryPath, string? MergeRequestReference, string? MergeRequestTitle,
        string? MergeRequestUrl, string? MergeCommitSha, DateTimeOffset? MergedAt, string? NoCodeChangeRationale);
}
