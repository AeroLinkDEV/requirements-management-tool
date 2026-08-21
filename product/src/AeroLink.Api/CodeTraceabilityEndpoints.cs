using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Common;
using AeroLink.Domain.Identity;
using AeroLink.Domain.Hierarchy;
using AeroLink.Domain.Requirements;
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

    private static async Task<IResult> ListAsync(Guid projectId, Guid releaseId, HttpContext http, AeroLinkDbContext db, ILadderPolicy ladderPolicy, CancellationToken ct)
    {
        if (!await http.HasProjectAccessAsync(db, projectId, ct)) return Results.Forbid();
        var release = await db.Releases.AsNoTracking().SingleOrDefaultAsync(x => x.Id == releaseId && x.ProjectId == projectId, ct);
        if (release is null) return Results.NotFound();

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

        if (!materialized)
            return Results.Ok(Waiting(release.Version, release.IsReleased, recorded));

        var required = await CodeTraceabilityProjection.RequiredAsync(db, projectId, releaseId, campaignBaselineId!.Value, ladderPolicy, ct);
        var revisionIds = required.Select(x => x.RevisionId).ToHashSet();
        var mappings = recorded.Where(x => revisionIds.Contains(x.RequirementRevisionId)).ToList();
        return Results.Ok(Response(release.Version, release.IsReleased, required, mappings));
    }

    /// The one baseline the release decision is made against. A release has at most one campaign.
    private static async Task<Guid?> CampaignBaselineAsync(AeroLinkDbContext db, Guid projectId, Guid releaseId, CancellationToken ct) =>
        await db.ReleaseCampaigns.AsNoTracking().Where(x => x.ProjectId == projectId && x.ReleaseId == releaseId)
            .Select(x => (Guid?)x.BaselineId).SingleOrDefaultAsync(ct);

    private static async Task<IResult> CreateAsync(CreateCodeTraceabilityRequest request, HttpContext http, AeroLinkDbContext db, IdentityService identity, ILadderPolicy ladderPolicy, CancellationToken ct)
    {
        if (!await http.HasProjectRoleAsync(db, identity, request.ProjectId, ct, ProgramRole.Engineer, ProgramRole.ConfigurationManager, ProgramRole.ProgramManager)) return Results.Forbid();
        var release = await db.Releases.AsNoTracking().SingleOrDefaultAsync(x => x.Id == request.ReleaseId && x.ProjectId == request.ProjectId, ct);
        if (release is null) return Results.BadRequest(new { error = "The selected build does not belong to this Project." });
        if (release.IsReleased) return Results.Conflict(new { error = $"Build {release.Version} is released and read-only." });
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
        var requiredLevel = ladderPolicy.OrderedLevels.Single(level => ladderPolicy.HasCodeTraceability(level));
        var exactLlr = await (from selection in db.BaselineRequirements.AsNoTracking().Where(x => x.BaselineId == baselineId && x.RevisionId == request.RequirementRevisionId)
                                                       join artifact in db.Requirements.AsNoTracking().Where(x => x.Id == request.RequirementArtifactId && x.ProjectId == request.ProjectId && x.Level == requiredLevel) on selection.ArtifactId equals artifact.Id
                                                       select artifact.Id).AnyAsync(ct);
        if (!exactLlr) return Results.BadRequest(new { error = "Code traceability must map an exact LLR revision in the selected build baseline." });
        try
        {
            var actor = http.UserAccount(); var now = DateTimeOffset.UtcNow;
            var record = new CodeTraceabilityRecord(request.ProjectId, request.ReleaseId, request.RequirementArtifactId, request.RequirementRevisionId,
                request.Disposition, request.RepositoryPath ?? "", request.MergeRequestReference ?? "", request.MergeRequestTitle ?? "",
                request.MergeRequestUrl ?? "", request.MergeCommitSha ?? "", request.MergedAt, request.NoCodeChangeRationale ?? "", false, actor.UserName, now);
            db.CodeTraceabilityRecords.Add(record);
            db.SecurityAuditEvents.Add(new("CodeTraceabilityRecorded", actor.UserName, $"CodeTraceability:{record.Id}", "Success",
                $"Mapped exact LLR revision {request.RequirementRevisionId} as {request.Disposition} for build {request.ReleaseId}.",
                http.Connection.RemoteIpAddress?.ToString() ?? "local", now));
            await db.SaveChangesAsync(ct);
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
    private static object Waiting(string version, bool readOnly, IReadOnlyList<CodeTraceabilityRecord> recorded) => new
    {
        build = new { version, readOnly },
        sourceOfTruth = SourceOfTruth,
        evaluationState = "WaitingForPrerequisite",
        demonstrationScope = recorded.Any(x => x.IsDemonstration),
        waiting = new
        {
            detail = "Waiting for a materialized baseline. The exact requirement-revision population does not exist yet, so this gate has not been evaluated.",
            action = "Complete the Requirement baseline materialized gate first: freeze the candidate baseline and materialize its requirements.",
            recordedCount = recorded.Count,
        },
        summary = (object?)null,
        requirements = Array.Empty<object>(),
    };

    private const string SourceOfTruth = "GitLab is the source of truth for source code, merge-request review, and commit content. AeroLink stores immutable traceability pointers only.";

    private static object Response(string version, bool readOnly, IReadOnlyList<RequiredCodeTraceabilityRequirement> candidates, IReadOnlyList<CodeTraceabilityRecord> mappings)
    {
        var byRevision = mappings.ToDictionary(x => x.RequirementRevisionId);
        var mapped = candidates.Count(candidate => byRevision.ContainsKey(candidate.RevisionId));
        return new
        {
            build = new { version, readOnly },
            sourceOfTruth = SourceOfTruth,
            evaluationState = "Evaluated",
            demonstrationScope = mappings.Any(x => x.IsDemonstration),
            summary = new { required = candidates.Count, mapped, missing = candidates.Count - mapped, percent = candidates.Count == 0 ? 100 : mapped * 100 / candidates.Count, gateComplete = mapped == candidates.Count },
            requirements = candidates.Select(candidate => new
            {
                candidate.ArtifactId, candidate.RevisionId, displayNumber = $"{candidate.BaseNumber}.{candidate.Revision:D2}", candidate.Statement,
                mapping = byRevision.TryGetValue(candidate.RevisionId, out var record) ? new
                {
                    id = record.Id, disposition = record.Disposition.ToString(), record.RepositoryPath, record.MergeRequestReference,
                    record.MergeRequestTitle, record.MergeRequestUrl, record.MergeCommitSha, record.MergedAt, record.NoCodeChangeRationale,
                    record.IsDemonstration, record.RecordedBy, record.RecordedAt
                } : null
            })
        };
    }
    private sealed record CreateCodeTraceabilityRequest(Guid ProjectId, Guid ReleaseId, Guid RequirementArtifactId, Guid RequirementRevisionId,
        CodeTraceDisposition Disposition, string? RepositoryPath, string? MergeRequestReference, string? MergeRequestTitle,
        string? MergeRequestUrl, string? MergeCommitSha, DateTimeOffset? MergedAt, string? NoCodeChangeRationale);
}
