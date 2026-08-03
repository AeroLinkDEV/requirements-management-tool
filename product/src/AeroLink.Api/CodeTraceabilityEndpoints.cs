using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Common;
using AeroLink.Domain.Identity;
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

    private static async Task<IResult> ListAsync(Guid projectId, Guid releaseId, HttpContext http, AeroLinkDbContext db, CancellationToken ct)
    {
        if (!await http.HasProjectAccessAsync(db, projectId, ct)) return Results.Forbid();
        var release = await db.Releases.AsNoTracking().SingleOrDefaultAsync(x => x.Id == releaseId && x.ProjectId == projectId, ct);
        if (release is null) return Results.NotFound();
        var baselineId = await AeroLink.Api.BuildScope.EffectiveBaselineAsync(db, projectId, releaseId, ct);
        if (baselineId is null) return Results.Ok(Response(release.Version, release.IsReleased, [], []));

        var candidates = await (from selection in db.BaselineRequirements.AsNoTracking().Where(x => x.BaselineId == baselineId)
                                join artifact in db.Requirements.AsNoTracking().Where(x => x.ProjectId == projectId && x.Level == RequirementLevel.LowLevel) on selection.ArtifactId equals artifact.Id
                                join revision in db.RequirementRevisions.AsNoTracking() on selection.RevisionId equals revision.Id
                                join change in db.SystemChangeRequests.AsNoTracking() on revision.SourceScrId equals change.Id into changes
                                from change in changes.DefaultIfEmpty()
                                select new Candidate(artifact.Id, revision.Id, artifact.BaseNumber, revision.Revision, revision.Statement,
                                    change != null && change.TargetReleaseId == releaseId)).ToListAsync(ct);

        var programCode = await (from project in db.Projects.AsNoTracking().Where(x => x.Id == projectId)
                                 join program in db.Programs.AsNoTracking() on project.ProgramId equals program.Id
                                 select program.Code).SingleAsync(ct);
        // The FMS showcase deliberately uses five exact LLRs so the GitLab boundary is understandable without
        // pretending that 700 demonstration merge requests exist. Real Projects use every LLR changed in-build.
        var required = programCode == FmsShowcaseSeeder.ProgramCode
            ? candidates.OrderBy(x => x.BaseNumber).Take(5).ToList()
            : candidates.Where(x => x.ChangedInBuild).OrderBy(x => x.BaseNumber).ToList();
        var revisionIds = required.Select(x => x.RevisionId).ToList();
        var mappings = await db.CodeTraceabilityRecords.AsNoTracking()
            .Where(x => x.ProjectId == projectId && x.ReleaseId == releaseId && revisionIds.Contains(x.RequirementRevisionId))
            .ToListAsync(ct);
        return Results.Ok(Response(release.Version, release.IsReleased, required, mappings));
    }

    private static async Task<IResult> CreateAsync(CreateCodeTraceabilityRequest request, HttpContext http, AeroLinkDbContext db, IdentityService identity, CancellationToken ct)
    {
        if (!await http.HasProjectRoleAsync(db, identity, request.ProjectId, ct, ProgramRole.Engineer, ProgramRole.ConfigurationManager, ProgramRole.ProgramManager)) return Results.Forbid();
        if (!await db.Releases.AnyAsync(x => x.Id == request.ReleaseId && x.ProjectId == request.ProjectId, ct)) return Results.BadRequest(new { error = "The selected build does not belong to this Project." });
        var baselineId = await AeroLink.Api.BuildScope.EffectiveBaselineAsync(db, request.ProjectId, request.ReleaseId, ct);
        var exactLlr = baselineId is not null && await (from selection in db.BaselineRequirements.AsNoTracking().Where(x => x.BaselineId == baselineId && x.RevisionId == request.RequirementRevisionId)
                                                       join artifact in db.Requirements.AsNoTracking().Where(x => x.Id == request.RequirementArtifactId && x.ProjectId == request.ProjectId && x.Level == RequirementLevel.LowLevel) on selection.ArtifactId equals artifact.Id
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

    private static object Response(string version, bool readOnly, IReadOnlyList<Candidate> candidates, IReadOnlyList<CodeTraceabilityRecord> mappings)
    {
        var byRevision = mappings.ToDictionary(x => x.RequirementRevisionId);
        var mapped = candidates.Count(candidate => byRevision.ContainsKey(candidate.RevisionId));
        return new
        {
            build = new { version, readOnly },
            sourceOfTruth = "GitLab is the source of truth for source code, merge-request review, and commit content. AeroLink stores immutable traceability pointers only.",
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

    private sealed record Candidate(Guid ArtifactId, Guid RevisionId, string BaseNumber, int Revision, string Statement, bool ChangedInBuild);
    private sealed record CreateCodeTraceabilityRequest(Guid ProjectId, Guid ReleaseId, Guid RequirementArtifactId, Guid RequirementRevisionId,
        CodeTraceDisposition Disposition, string? RepositoryPath, string? MergeRequestReference, string? MergeRequestTitle,
        string? MergeRequestUrl, string? MergeCommitSha, DateTimeOffset? MergedAt, string? NoCodeChangeRationale);
}
