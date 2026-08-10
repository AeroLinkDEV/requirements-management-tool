using AeroLink.Domain.Requirements;
using AeroLink.Domain.Verification;
using AeroLink.Infrastructure.Persistence;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace AeroLink.Api;

/// <summary>
/// Exact revision-scoped read models for Test Procedure Explorer.
///
/// These handlers intentionally use the existing public URLs with a lower route order than the legacy
/// handlers in VerificationEndpoints. That keeps every client/deep link stable while the old implementations
/// remain available for rollback and code-history comparison. Routing selects these handlers deterministically.
/// </summary>
public static class TestProcedureReadEndpoints
{
    public static IEndpointRouteBuilder MapExactTestProcedureReadEndpoints(this IEndpointRouteBuilder app)
    {
        Prefer(app.MapGet("/api/test-procedures/{id:guid}/history", HistoryAsync));
        Prefer(app.MapGet("/api/test-procedures/{id:guid}/trace", TraceAsync));
        Prefer(app.MapGet("/api/test-procedures", ListAsync));
        return app;
    }

    private static void Prefer(RouteHandlerBuilder builder) =>
        builder.Add(endpointBuilder => ((RouteEndpointBuilder)endpointBuilder).Order = -100);

    private static async Task<IResult> HistoryAsync(Guid id, Guid? revisionId, Guid? releaseId,
        HttpContext http, AeroLinkDbContext db, CancellationToken ct)
    {
        var procedure = await db.TestProcedures.AsNoTracking()
            .Where(x => x.Id == id)
            .Select(x => new { x.Id, x.ProjectId, x.BaseNumber, x.OwnerId, x.Level, x.CreatedAt })
            .SingleOrDefaultAsync(ct);
        if (procedure is null) return Results.NotFound();
        if (!await http.HasProjectAccessAsync(db, procedure.ProjectId, ct)) return Results.Forbid();

        var revisions = (await db.TestProcedureRevisions.AsNoTracking()
                .Where(x => x.ProcedureId == id).ToListAsync(ct))
            .OrderByDescending(x => x.Revision).ToList();
        var revisionIds = revisions.Select(x => x.Id).ToList();
        if (revisionId is not null && !revisionIds.Contains(revisionId.Value)) return Results.NotFound();
        Guid? effectiveRevisionId = null;
        if (releaseId is not null)
        {
            var effectivity = await TestProcedureEffectivity.ForReleaseAsync(
                db, procedure.ProjectId, releaseId.Value, ct);
            if (effectivity is not null
                && effectivity.RevisionByProcedure.TryGetValue(id, out var carriedRevisionId))
                effectiveRevisionId = carriedRevisionId;
            if (revisionId is not null && revisionId != effectiveRevisionId) return Results.NotFound();
        }

        var titles = await TestProcedureRevisionTitleProjection.ForRevisionsAsync(db, revisionIds, ct);
        var provenance = await TestProcedureProvenanceProjection.ForRevisionsAsync(db, revisionIds, ct);
        var coverage = await (from link in db.TestCoverage.AsNoTracking()
                              join revision in db.RequirementRevisions.AsNoTracking()
                                  on link.RequirementRevisionId equals revision.Id
                              join artifact in db.Requirements.AsNoTracking()
                                  on revision.ArtifactId equals artifact.Id
                              where revisionIds.Contains(link.ProcedureRevisionId)
                              select new
                              {
                                  link.ProcedureRevisionId,
                                  artifact.BaseNumber,
                                  revision.Revision,
                              }).ToListAsync(ct);

        var selectedId = revisionId ?? effectiveRevisionId ?? revisions.FirstOrDefault()?.Id;
        var selectedTitle = selectedId is Guid selected && titles.TryGetValue(selected, out var exactTitle)
            ? exactTitle
            : null;
        return Results.Ok(new
        {
            procedure.Id,
            procedure.BaseNumber,
            title = selectedTitle?.Title ?? "",
            titleIsExact = selectedTitle?.IsExact ?? false,
            titleIsLegacy = selectedTitle?.IsLegacy ?? false,
            titleNote = selectedTitle?.Note,
            level = procedure.Level.ToString(),
            procedure.OwnerId,
            procedure.CreatedAt,
            selectedRevisionId = selectedId,
            revisions = revisions.Select(revision =>
            {
                var title = titles[revision.Id];
                var source = provenance[revision.Id];
                return new
                {
                    revision.Id,
                    displayNumber = $"{procedure.BaseNumber}.{revision.Revision:D2}",
                    revision.Revision,
                    title = title.Title,
                    titleIsExact = title.IsExact,
                    titleIsLegacy = title.IsLegacy,
                    titleNote = title.Note,
                    state = revision.State.ToString(),
                    revision.AuthorId,
                    revision.CreatedAt,
                    revision.Objective,
                    revision.Preconditions,
                    revision.Steps,
                    revision.ExpectedResult,
                    selected = revision.Id == selectedId,
                    revision.SourceTestChangeRequestId,
                    package = source.Package,
                    provenanceNote = source.Note,
                    drivenBy = source.Drivers.Select(driver => new
                    {
                        changeRequest = driver.ChangeRequest,
                        package = driver.Package,
                        subjectDisplayNumber = driver.SubjectDisplayNumber,
                        action = driver.Action,
                    }).ToList(),
                    covers = coverage.Where(x => x.ProcedureRevisionId == revision.Id)
                        .Select(x => $"{x.BaseNumber}.{x.Revision:D2}")
                        .Distinct().OrderBy(x => x).ToList(),
                };
            }).ToList(),
        });
    }

    private static async Task<IResult> TraceAsync(Guid id, Guid? releaseId, Guid? revisionId,
        HttpContext http, AeroLinkDbContext db, CancellationToken ct)
    {
        var procedure = await db.TestProcedures.AsNoTracking()
            .Where(x => x.Id == id)
            .Select(x => new { x.Id, x.ProjectId, x.BaseNumber, x.Level })
            .SingleOrDefaultAsync(ct);
        if (procedure is null) return Results.NotFound();
        if (!await http.HasProjectAccessAsync(db, procedure.ProjectId, ct)) return Results.Forbid();

        Guid? selectedRevisionId = revisionId;
        Guid? effectiveBaselineId = null;
        Guid? requirementBaselineId = null;
        var isExactManifest = false;
        if (releaseId is not null)
        {
            var effectivity = await TestProcedureEffectivity.ForReleaseAsync(
                db, procedure.ProjectId, releaseId.Value, ct);
            if (effectivity is null
                || !effectivity.RevisionByProcedure.TryGetValue(id, out var carriedRevisionId))
                return Results.NotFound(new
                {
                    error = "This procedure is not carried by the selected build.",
                    code = "procedure_not_carried_by_build"
                });
            if (revisionId is not null && revisionId != carriedRevisionId)
                return Results.NotFound(new
                {
                    error = "The requested procedure revision is not the revision the selected build carries.",
                    code = "cross_build_procedure_revision"
                });
            selectedRevisionId = carriedRevisionId;
            isExactManifest = effectivity.IsExactManifest;
            effectiveBaselineId = effectivity.BaselineId;
            requirementBaselineId = await BuildScope.EffectiveBaselineAsync(
                db, procedure.ProjectId, releaseId.Value, ct);
            if (requirementBaselineId is null)
                return Results.NotFound(new
                {
                    error = "The selected build has no controlled requirement baseline to intersect the trace against.",
                    code = "requirement_baseline_unavailable"
                });
        }
        selectedRevisionId ??= await db.TestProcedureRevisions.AsNoTracking()
            .Where(x => x.ProcedureId == id).OrderByDescending(x => x.Revision)
            .Select(x => (Guid?)x.Id).FirstOrDefaultAsync(ct);
        if (selectedRevisionId is null) return Results.NotFound();
        var selectedRevisionIdValue = selectedRevisionId.Value;

        var revision = await db.TestProcedureRevisions.AsNoTracking()
            .Where(x => x.Id == selectedRevisionIdValue && x.ProcedureId == id)
            .Select(x => new
            {
                x.Id,
                x.Revision,
                x.State,
                x.AuthorId,
                x.CreatedAt,
                x.SourceTestChangeRequestId,
            }).SingleOrDefaultAsync(ct);
        if (revision is null) return Results.NotFound();

        var covered = await (from link in db.TestCoverage.AsNoTracking()
                             where link.ProcedureRevisionId == selectedRevisionIdValue
                             join requirementRevision in db.RequirementRevisions.AsNoTracking()
                                 on link.RequirementRevisionId equals requirementRevision.Id
                             join artifact in db.Requirements.AsNoTracking()
                                 on requirementRevision.ArtifactId equals artifact.Id
                             select new
                             {
                                 artifact.Id,
                                 RequirementRevisionId = requirementRevision.Id,
                                 DisplayNumber = artifact.BaseNumber + "." + requirementRevision.Revision.ToString("D2"),
                                 Level = artifact.Level.ToString(),
                                 requirementRevision.Statement,
                                 link.IsSuspect,
                             }).ToListAsync(ct);
        if (requirementBaselineId is not null)
        {
            var manifest = (await db.BaselineRequirements.AsNoTracking()
                    .Where(x => x.BaselineId == requirementBaselineId.Value)
                    .Select(x => x.RevisionId).ToListAsync(ct)).ToHashSet();
            covered = covered.Where(x => manifest.Contains(x.RequirementRevisionId)).ToList();
        }

        var coverageStates = (await VerificationCoverageProjection.ForRequirementRevisionsAsync(db,
                covered.Select(x => x.RequirementRevisionId).Distinct().ToList(), ct,
                buildScoped: true, effectiveProcedureRevisionIds: [selectedRevisionIdValue]))
            .GroupBy(x => x.RequirementRevisionId)
            .ToDictionary(x => x.Key, x => x.First().CoverageState);
        var title = (await TestProcedureRevisionTitleProjection.ForRevisionsAsync(
            db, [selectedRevisionIdValue], ct))[selectedRevisionIdValue];
        var source = (await TestProcedureProvenanceProjection.ForRevisionsAsync(
            db, [selectedRevisionIdValue], ct))[selectedRevisionIdValue];

        return Results.Ok(new
        {
            procedureId = procedure.Id,
            procedure.BaseNumber,
            title = title.Title,
            titleIsExact = title.IsExact,
            titleIsLegacy = title.IsLegacy,
            titleNote = title.Note,
            level = procedure.Level.ToString(),
            revisionId = selectedRevisionIdValue,
            displayNumber = $"{procedure.BaseNumber}.{revision.Revision:D2}",
            revision.Revision,
            state = revision.State.ToString(),
            revision.AuthorId,
            revision.CreatedAt,
            revision.SourceTestChangeRequestId,
            package = source.Package,
            provenanceNote = source.Note,
            requirements = covered.Select(x => new
            {
                x.Id,
                revisionId = x.RequirementRevisionId,
                x.DisplayNumber,
                x.Level,
                x.Statement,
                coverageState = coverageStates.TryGetValue(x.RequirementRevisionId, out var state)
                    ? state
                    : RequirementCoverageState.Suspect,
                x.IsSuspect,
            }).OrderBy(x => x.DisplayNumber).ToList(),
            provenance = source.Drivers.Select(driver => new
            {
                changeRequest = driver.ChangeRequest,
                package = driver.Package,
                subjectDisplayNumber = driver.SubjectDisplayNumber,
                action = driver.Action,
            }).ToList(),
            build = releaseId is null ? null : new
            {
                releaseId = releaseId.Value,
                effectiveBaselineId,
                requirementBaselineId,
                isExactManifest,
            },
        });
    }

    private static async Task<IResult> ListAsync(Guid projectId, Guid? releaseId, string? search,
        string? scope, string? state, string? owner, string? outcome, Guid? requirementRevisionId,
        string? sort, int? page, int? pageSize, string? ids,
        HttpContext http, AeroLinkDbContext db, CancellationToken ct)
    {
        if (!await http.HasProjectAccessAsync(db, projectId, ct)) return Results.Forbid();
        var currentPage = Math.Max(1, page ?? 1);
        var size = Math.Clamp(pageSize ?? 25, 1, 200);
        var source = db.TestProcedures.AsNoTracking().Where(x => x.ProjectId == projectId);
        Dictionary<Guid, Guid>? scopedRevisions = null;
        if (releaseId is not null)
        {
            var effectivity = await TestProcedureEffectivity.ForReleaseAsync(db, projectId, releaseId.Value, ct);
            if (effectivity is null)
                return Results.Ok(new
                {
                    page = currentPage,
                    pageSize = size,
                    totalCount = 0,
                    totalPages = 0,
                    items = Array.Empty<object>(),
                });
            scopedRevisions = effectivity.RevisionByProcedure.ToDictionary(x => x.Key, x => x.Value);
            var effectiveProcedureIds = scopedRevisions.Keys.ToList();
            source = source.Where(x => effectiveProcedureIds.Contains(x.Id));
        }
        if (string.Equals(scope, "System", StringComparison.OrdinalIgnoreCase))
            source = source.Where(x => x.Level == TestProcedureLevel.System);
        else if (string.Equals(scope, "Software", StringComparison.OrdinalIgnoreCase))
            source = source.Where(x => x.Level == TestProcedureLevel.HighLevel || x.Level == TestProcedureLevel.LowLevel);
        else if (string.Equals(scope, "HighLevelSoftware", StringComparison.OrdinalIgnoreCase))
            source = source.Where(x => x.Level == TestProcedureLevel.HighLevel);
        else if (string.Equals(scope, "LowLevelSoftware", StringComparison.OrdinalIgnoreCase))
            source = source.Where(x => x.Level == TestProcedureLevel.LowLevel);
        var eligibility = source;

        if (!string.IsNullOrWhiteSpace(search))
        {
            var q = search.Trim().ToLower();
            var requestedRevision = -1;
            var hasRevision = q.Length > 3 && q[^3] == '.' && int.TryParse(q[^2..], out requestedRevision);
            var baseQuery = hasRevision ? q[..^3] : q;
            var scopedRevisionIds = scopedRevisions?.Values.ToList();
            var titleMatches = await (from change in db.Set<TestProcedureChange>().AsNoTracking()
                                      join revision in db.TestProcedureRevisions.AsNoTracking()
                                          on new
                                          {
                                              ReviewId = (Guid?)change.TestChangeReviewId,
                                              change.Revision,
                                          }
                                          equals new
                                          {
                                              ReviewId = revision.SourceTestChangeRequestId,
                                              revision.Revision,
                                          }
                                      join procedure in db.TestProcedures.AsNoTracking()
                                          on revision.ProcedureId equals procedure.Id
                                      where procedure.ProjectId == projectId
                                            && procedure.BaseNumber == change.BaseNumber
                                            && change.Title.ToLower().Contains(q)
                                            && (scopedRevisionIds == null
                                                ? revision.Revision == db.TestProcedureRevisions
                                                    .Where(other => other.ProcedureId == procedure.Id)
                                                    .Max(other => other.Revision)
                                                : scopedRevisionIds.Contains(revision.Id))
                                      select procedure.Id).Distinct().ToListAsync(ct);
            source = source.Where(x => x.BaseNumber.ToLower().Contains(baseQuery)
                                       || x.Title.ToLower().Contains(q)
                                       || titleMatches.Contains(x.Id));
            if (hasRevision && scopedRevisions is not null)
            {
                var matchingProcedureIds = await db.TestProcedureRevisions.AsNoTracking()
                    .Where(x => scopedRevisionIds!.Contains(x.Id) && x.Revision == requestedRevision)
                    .Select(x => x.ProcedureId).ToListAsync(ct);
                source = source.Where(x => matchingProcedureIds.Contains(x.Id));
            }
        }
        if (!string.IsNullOrWhiteSpace(owner))
        {
            var ownerQuery = owner.Trim().ToLower();
            source = source.Where(x => x.OwnerId.ToLower() == ownerQuery);
        }
        if (!string.IsNullOrWhiteSpace(state)
            && Enum.TryParse<TestProcedureState>(state, true, out var parsedState))
        {
            var scopedRevisionIds = scopedRevisions?.Values.ToList();
            source = scopedRevisionIds is null
                ? source.Where(x => db.TestProcedureRevisions.Any(r => r.ProcedureId == x.Id
                    && r.Revision == db.TestProcedureRevisions.Where(o => o.ProcedureId == x.Id)
                        .Max(o => o.Revision)
                    && r.State == parsedState))
                : source.Where(x => db.TestProcedureRevisions.Any(r => r.ProcedureId == x.Id
                    && scopedRevisionIds.Contains(r.Id) && r.State == parsedState));
        }
        if (requirementRevisionId is not null)
            source = source.Where(x => db.TestCoverage.Any(c => c.RequirementRevisionId == requirementRevisionId
                && db.TestProcedureRevisions.Any(r => r.Id == c.ProcedureRevisionId && r.ProcedureId == x.Id)));
        if (!string.IsNullOrWhiteSpace(outcome)
            && Enum.TryParse<TestOutcome>(outcome, true, out var parsedOutcome))
        {
            var candidateIds = await source.Select(x => x.Id).ToListAsync(ct);
            var scopedRevisionIds = scopedRevisions?.Where(x => candidateIds.Contains(x.Key))
                .Select(x => x.Value).ToList();
            var runs = await (from execution in db.TestExecutions.AsNoTracking()
                              join revision in db.TestProcedureRevisions.AsNoTracking()
                                  on execution.ProcedureRevisionId equals revision.Id
                              where candidateIds.Contains(revision.ProcedureId)
                                    && (scopedRevisionIds == null || scopedRevisionIds.Contains(revision.Id))
                              select new
                              {
                                  revision.ProcedureId,
                                  execution.Outcome,
                                  execution.ExecutedAt,
                                  execution.RecordedAt,
                              }).ToListAsync(ct);
            var matching = runs.GroupBy(x => x.ProcedureId)
                .Where(group => group.OrderByDescending(x => x.ExecutedAt)
                    .ThenByDescending(x => x.RecordedAt).First().Outcome == parsedOutcome)
                .Select(group => group.Key).ToList();
            source = source.Where(x => matching.Contains(x.Id));
        }

        var totalCount = await source.CountAsync(ct);
        // Exact title is projected after the bounded page is selected. Base number remains the deterministic
        // database page boundary; title-sort requests use the stable catalog as a compatibility tie-breaker.
        var ordered = sort?.ToLowerInvariant() switch
        {
            "title" => source.OrderBy(x => x.Title).ThenBy(x => x.BaseNumber),
            "owner" => source.OrderBy(x => x.OwnerId).ThenBy(x => x.BaseNumber),
            "level" => source.OrderBy(x => x.Level).ThenBy(x => x.BaseNumber),
            _ => source.OrderBy(x => x.BaseNumber).ThenBy(x => x.BaseNumber),
        };
        var items = await ordered.Skip((currentPage - 1) * size).Take(size)
            .Select(x => new { x.Id, x.BaseNumber, x.OwnerId, x.Level, x.CreatedAt }).ToListAsync(ct);
        var requestedIds = (ids ?? "").Split(',',
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(x => Guid.TryParse(x, out var parsed) ? parsed : Guid.Empty)
            .Where(x => x != Guid.Empty).Distinct().ToList();
        var hydrated = requestedIds.Count == 0
            ? []
            : await eligibility.Where(x => requestedIds.Contains(x.Id))
                .Select(x => new { x.Id, x.BaseNumber, x.OwnerId, x.Level, x.CreatedAt })
                .ToListAsync(ct);
        var all = items.Concat(hydrated).DistinctBy(x => x.Id).ToList();
        var allIds = all.Select(x => x.Id).ToList();
        var revisions = await db.TestProcedureRevisions.AsNoTracking()
            .Where(x => allIds.Contains(x.ProcedureId)).ToListAsync(ct);
        var selectedRevisionIds = scopedRevisions is null
            ? revisions.GroupBy(x => x.ProcedureId)
                .Select(group => group.OrderByDescending(x => x.Revision).First().Id).ToList()
            : scopedRevisions.Where(x => allIds.Contains(x.Key)).Select(x => x.Value).ToList();
        var titles = await TestProcedureRevisionTitleProjection.ForRevisionsAsync(db, selectedRevisionIds, ct);
        var coverage = await db.TestCoverage.AsNoTracking()
            .Where(x => selectedRevisionIds.Contains(x.ProcedureRevisionId)).ToListAsync(ct);
        var executions = await db.TestExecutions.AsNoTracking()
            .Where(x => selectedRevisionIds.Contains(x.ProcedureRevisionId)).ToListAsync(ct);

        var projected = all.OrderBy(x => x.BaseNumber).Select(x =>
        {
            var latest = scopedRevisions is not null && scopedRevisions.TryGetValue(x.Id, out var selectedRevisionId)
                ? revisions.SingleOrDefault(r => r.Id == selectedRevisionId)
                : revisions.Where(r => r.ProcedureId == x.Id).OrderByDescending(r => r.Revision).FirstOrDefault();
            var title = latest is null || !titles.TryGetValue(latest.Id, out var projectedTitle)
                ? null
                : projectedTitle;
            var lastRun = latest is null ? null : executions.Where(e => e.ProcedureRevisionId == latest.Id)
                .OrderByDescending(e => e.ExecutedAt).ThenByDescending(e => e.RecordedAt).FirstOrDefault();
            return new
            {
                x.Id,
                displayNumber = latest is null ? x.BaseNumber : $"{x.BaseNumber}.{latest.Revision:D2}",
                title = title?.Title ?? "",
                titleIsExact = title?.IsExact ?? false,
                titleIsLegacy = title?.IsLegacy ?? false,
                titleNote = title?.Note,
                x.OwnerId,
                level = x.Level.ToString(),
                revisionId = latest?.Id,
                revision = latest?.Revision,
                state = latest?.State.ToString(),
                objective = latest?.Objective,
                requirementCount = latest is null ? 0 : coverage.Count(c => c.ProcedureRevisionId == latest.Id),
                lastOutcome = lastRun?.Outcome.ToString(),
                lastExecutedAt = lastRun?.ExecutedAt,
            };
        }).ToList();
        return Results.Ok(new
        {
            page = currentPage,
            pageSize = size,
            totalCount,
            totalPages = (int)Math.Ceiling(totalCount / (double)size),
            items = projected,
        });
    }
}
