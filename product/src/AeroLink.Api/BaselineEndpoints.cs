using AeroLink.Domain.Baselines;
using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Common;
using AeroLink.Domain.Contracts;
using AeroLink.Domain.Identity;
using AeroLink.Domain.Programs;
using AeroLink.Domain.Releases;
using AeroLink.Domain.Traceability;
using AeroLink.Domain.Verification;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using System.Security.Cryptography;
using System.Text;

namespace AeroLink.Api;

/// <summary>
/// Baselines and software builds: freezing a set of approved changes, and proving exactly what
/// shipped.
///
/// The historical reads deliberately include every revision and lifecycle state, because the question they
/// answer is what was true at a moment, not what is true now.
/// </summary>
public static class BaselineEndpoints
{
    public static void MapBaselineEndpoints(this WebApplication app)
    {
        // Historical discovery endpoints deliberately include every revision and lifecycle state.
        // `baseNumber` asks for one change request's whole revision history; without it the listing collapses to
        // each change request's newest revision. A programme's history is a list of change requests and not of
        // revisions — .00 superseded by .01 is one piece of work read twice, and showing both puts the stale copy
        // in the reader's way. Nothing is hidden: every collapsed row carries its revision count and expands.
        app.MapGet("/api/history/change-requests", async (Guid projectId, string? search, Guid? releaseId, Guid? baselineId, Guid? buildId, ChangeRequestType? type, RequirementLevel? level, string? state,
            string? baseNumber, int page, int pageSize, AeroLinkDbContext db, CancellationToken ct) =>
        {
            page = Math.Max(1, page == 0 ? 1 : page); pageSize = Math.Clamp(pageSize == 0 ? 50 : pageSize, 1, 200);
            var source = db.SystemChangeRequests.AsNoTracking().Where(x => x.ProjectId == projectId);
            if (type is not null) source = source.Where(x => x.Type == type);
            if (level is not null) source = source.Where(x =>
                x.RequirementChanges.Any(change => change.Level == level) ||
                (!x.RequirementChanges.Any() && x.SoftwareLevel == level));
            if (!string.IsNullOrWhiteSpace(state))
            {
                if (state.Equals("ApprovedOrSelected", StringComparison.OrdinalIgnoreCase))
                    source = source.Where(x => x.State == ChangeRequestState.Approved || x.State == ChangeRequestState.SelectedForBaseline);
                else if (Enum.TryParse<ChangeRequestState>(state, true, out var parsedState)) source = source.Where(x => x.State == parsedState);
                else return Results.BadRequest(new { error = "The requested lifecycle state is not recognized." });
            }
            if (!string.IsNullOrWhiteSpace(search)) { var q = search.Trim().ToLower(); source = source.Where(x =>
                x.BaseNumber.ToLower().Contains(q) || x.Title.ToLower().Contains(q) || x.Problem.ToLower().Contains(q) ||
                x.Analysis.ToLower().Contains(q) || x.Solution.ToLower().Contains(q)); }
            // A build shows the work it was raised in as well as the work it is taking. Without the origin
            // side, a change request raised in 1.6 and reinstated into 1.7 is neither deferred nor targeting
            // 1.6, so it would vanish from 1.6 and a reader there would see work that simply disappeared.
            if (releaseId is not null)
                source = source.Where(x => x.TargetReleaseId == releaseId || x.OriginReleaseId == releaseId);
            var selectedBaselineId = baselineId;
            if (buildId is not null) selectedBaselineId = await db.SoftwareBuilds.Where(x => x.Id == buildId && x.ProjectId == projectId).Select(x => (Guid?)x.BaselineId).SingleOrDefaultAsync(ct);
            if (selectedBaselineId is not null) source = source.Where(x => db.BaselineSelections.Any(s => s.BaselineId == selectedBaselineId && s.ChangeRequestId == x.Id));
            if (!string.IsNullOrWhiteSpace(baseNumber))
                source = source.Where(x => x.BaseNumber == baseNumber);
            else
                source = source.Where(x => x.Revision == db.SystemChangeRequests
                    .Where(other => other.ProjectId == projectId && other.BaseNumber == x.BaseNumber)
                    .Max(other => other.Revision));
            var total = await source.CountAsync(ct);
            var ordered = db.Database.IsSqlite() ? source.OrderBy(x => x.BaseNumber).ThenByDescending(x => x.Revision) : source.OrderByDescending(x => x.UpdatedAt).ThenBy(x => x.BaseNumber).ThenByDescending(x => x.Revision);
            var items = await ordered
                .Skip((page - 1) * pageSize).Take(pageSize).Select(x => new { x.Id, displayNumber = x.BaseNumber + "." + (x.Revision < 10 ? "0" : "") + x.Revision,
                    x.BaseNumber, x.Revision, x.Title, state = x.State.ToString(), deferredFromState = x.DeferredFromState == null ? null : x.DeferredFromState.ToString(),
                    x.AuthorId, x.TargetReleaseId, softwareLevel = x.SoftwareLevel == null ? null : x.SoftwareLevel.ToString(), requirementCount = x.RequirementChanges.Count, x.CreatedAt, x.UpdatedAt,
                    hasHighLevelChanges = x.RequirementChanges.Any(change => change.Level == RequirementLevel.HighLevel),
                    hasLowLevelChanges = x.RequirementChanges.Any(change => change.Level == RequirementLevel.LowLevel),
                    revisionCount = db.SystemChangeRequests.Count(other => other.ProjectId == projectId && other.BaseNumber == x.BaseNumber) }).ToListAsync(ct);
            return Results.Ok(new { page, pageSize, totalCount = total, totalPages = (int)Math.Ceiling(total / (double)pageSize), items });
        });

        // The verification twin of the endpoint above, deliberately next to it: the two registers are meant to
        // be the same register over different artifacts, and a difference between them should be visible in a
        // diff rather than discovered on the page.
        //
        // Same query parameters, same page envelope, same row fields. Where a change request has a target
        // release a package has the build it belongs to, and where a change request counts requirement changes
        // a package counts procedure changes.
        app.MapGet("/api/history/test-change-requests", async (Guid projectId, string? search, Guid? releaseId,
            TestChangeReviewDiscipline? discipline, string? state, string? baseNumber, int page, int pageSize,
            HttpContext http, AeroLinkDbContext db, CancellationToken ct) =>
        {
            if (!await http.HasProjectAccessAsync(db, projectId, ct)) return Results.Forbid();
            page = Math.Max(1, page == 0 ? 1 : page); pageSize = Math.Clamp(pageSize == 0 ? 50 : pageSize, 1, 200);
            var source = db.TestChangeReviews.AsNoTracking().Where(x => x.ProjectId == projectId);
            if (discipline is not null) source = source.Where(x => x.Discipline == discipline);
            if (!string.IsNullOrWhiteSpace(state))
            {
                if (Enum.TryParse<TestChangeReviewState>(state, true, out var parsedState))
                    source = source.Where(x => x.State == parsedState);
                else return Results.BadRequest(new { error = "The requested lifecycle state is not recognized." });
            }
            if (!string.IsNullOrWhiteSpace(search))
            {
                var q = search.Trim().ToLower();
                source = source.Where(x => x.BaseNumber.ToLower().Contains(q) || x.Title.ToLower().Contains(q)
                    || x.Problem.ToLower().Contains(q) || x.Analysis.ToLower().Contains(q) || x.Solution.ToLower().Contains(q)
                    || x.SourceChangeRequestNumber.ToLower().Contains(q) || x.SourceProblemReportNumber.ToLower().Contains(q));
            }
            if (releaseId is not null) source = source.Where(x => x.ReleaseId == releaseId);
            if (!string.IsNullOrWhiteSpace(baseNumber))
                source = source.Where(x => x.BaseNumber == baseNumber);
            else
                // Latest revision per controlled number, as the requirements register does — but packages that
                // predate controlled numbering carry an empty one, and grouping those together would collapse
                // every unnumbered package in the Project into whichever happened to hold the highest revision.
                source = source.Where(x => x.BaseNumber == "" || x.Revision == db.TestChangeReviews
                    .Where(other => other.ProjectId == projectId && other.BaseNumber == x.BaseNumber)
                    .Max(other => other.Revision));
            var total = await source.CountAsync(ct);
            // SQLite can neither order nor aggregate a DateTimeOffset, so the newest-first ordering the
            // requirements register uses is available only on PostgreSQL. Same compromise, same reason.
            var ordered = db.Database.IsSqlite()
                ? source.OrderBy(x => x.BaseNumber).ThenByDescending(x => x.Revision)
                : source.OrderByDescending(x => x.UpdatedAt).ThenBy(x => x.BaseNumber).ThenByDescending(x => x.Revision);
            var items = await ordered.Skip((page - 1) * pageSize).Take(pageSize)
                .Select(x => new
                {
                    x.Id,
                    displayNumber = x.BaseNumber == ""
                        ? (x.ChangeRequestId != null ? x.SourceChangeRequestNumber : x.SourceProblemReportNumber)
                        : x.BaseNumber + "." + (x.Revision < 10 ? "0" : "") + x.Revision,
                    x.BaseNumber,
                    x.Revision,
                    x.Title,
                    state = x.State.ToString(),
                    deferredFromState = x.DeferredFromState == null ? null : x.DeferredFromState.ToString(),
                    x.AuthorId,
                    // A package belongs to the build it was raised against; that is its allocation.
                    targetReleaseId = x.ReleaseId,
                    discipline = x.Discipline.ToString(),
                    procedureCount = x.ProcedureChanges.Count,
                    x.CreatedAt,
                    x.UpdatedAt,
                    revisionCount = x.BaseNumber == "" ? 1 : db.TestChangeReviews
                        .Count(other => other.ProjectId == projectId && other.BaseNumber == x.BaseNumber),
                }).ToListAsync(ct);
            return Results.Ok(new { page, pageSize, totalCount = total, totalPages = (int)Math.Ceiling(total / (double)pageSize), items });
        });

        app.MapGet("/api/history/requirements", async (Guid projectId, string? search, Guid? releaseId, Guid? baselineId, Guid? buildId,
            int page, int pageSize, AeroLinkDbContext db, CancellationToken ct) =>
        {
            page = Math.Max(1, page == 0 ? 1 : page); pageSize = Math.Clamp(pageSize == 0 ? 50 : pageSize, 1, 200);
            var scrs = db.SystemChangeRequests.AsNoTracking().Where(x => x.ProjectId == projectId);
            if (releaseId is not null) scrs = scrs.Where(x => x.TargetReleaseId == releaseId);
            var selectedBaselineId = baselineId;
            if (buildId is not null) selectedBaselineId = await db.SoftwareBuilds.Where(x => x.Id == buildId && x.ProjectId == projectId).Select(x => (Guid?)x.BaselineId).SingleOrDefaultAsync(ct);
            if (selectedBaselineId is not null) scrs = scrs.Where(x => db.BaselineSelections.Any(s => s.BaselineId == selectedBaselineId && s.ChangeRequestId == x.Id));
            var source = from r in db.RequirementChanges.AsNoTracking() join s in scrs on r.ChangeRequestId equals s.Id select new { r, s };
            if (!string.IsNullOrWhiteSpace(search)) { var q = search.Trim().ToLower(); source = source.Where(x =>
                x.r.BaseNumber.ToLower().Contains(q) || x.r.Statement.ToLower().Contains(q) ||
                x.r.Rationale.ToLower().Contains(q) || x.s.Title.ToLower().Contains(q)); }
            var total = await source.CountAsync(ct);
            var ordered = db.Database.IsSqlite() ? source.OrderBy(x => x.r.BaseNumber).ThenByDescending(x => x.r.Revision) : source.OrderBy(x => x.r.BaseNumber).ThenByDescending(x => x.r.Revision).ThenByDescending(x => x.s.UpdatedAt);
            var items = await ordered
                .Skip((page - 1) * pageSize).Take(pageSize).Select(x => new { x.r.Id, displayNumber = x.r.BaseNumber + "." + (x.r.Revision < 10 ? "0" : "") + x.r.Revision,
                    x.r.BaseNumber, x.r.Revision, level = x.r.Level.ToString(), kind = x.r.Kind.ToString(), x.r.Statement, x.r.Rationale, x.r.VerificationMethod,
                    changeRequestId = x.s.Id, changeRequestDisplayNumber = x.s.BaseNumber + "." + (x.s.Revision < 10 ? "0" : "") + x.s.Revision, scrTitle = x.s.Title, scrState = x.s.State.ToString(), x.s.TargetReleaseId }).ToListAsync(ct);
            return Results.Ok(new { page, pageSize, totalCount = total, totalPages = (int)Math.Ceiling(total / (double)pageSize), items });
        });

        app.MapGet("/api/builds", async (Guid projectId, Guid? releaseId, string? search, AeroLinkDbContext db, CancellationToken ct) =>
        {
            var source = db.SoftwareBuilds.AsNoTracking().Where(x => x.ProjectId == projectId && (releaseId == null || x.ReleaseId == releaseId));
            if (!string.IsNullOrWhiteSpace(search)) { var q = search.Trim().ToLower(); source = source.Where(x => x.BuildNumber.ToLower().Contains(q) || x.Description.ToLower().Contains(q)); }
            var joined = from build in source join release in db.Releases.AsNoTracking() on build.ReleaseId equals release.Id join baseline in db.CandidateBaselines.AsNoTracking() on build.BaselineId equals baseline.Id
                select new { build.Id, build.BuildNumber, build.Description, state = build.State.ToString(), build.RecordedBy, build.RecordedAt, build.ReleasedAt,
                    releaseId = release.Id, release.Version, baselineId = baseline.Id, baselineDisplayNumber = ArtifactNumber.Display(baseline.BaseNumber, baseline.Revision),
                    baseline.ContentHash, scrCount = baseline.Selections.Count };
            var items = await (db.Database.IsSqlite() ? joined.OrderByDescending(x => x.BuildNumber) : joined.OrderByDescending(x => x.RecordedAt)).ToListAsync(ct);
            return Results.Ok(items);
        });

        app.MapPost("/api/builds", async (CreateBuildRequest request, HttpContext http, AeroLinkDbContext db, IdentityService identity, CancellationToken ct) =>
        {
            if (!await http.HasProjectRoleAsync(db, identity, request.ProjectId, ct, ProgramRole.ConfigurationManager)) return Results.Forbid();
            var baseline = await db.CandidateBaselines.AsNoTracking().SingleOrDefaultAsync(x => x.Id == request.BaselineId, ct);
            if (baseline is null) return Results.NotFound();
            if (baseline.State != CandidateBaselineState.Frozen) return Results.BadRequest(new { error = "A build can only reference a frozen baseline." });
            if (baseline.RequirementsMaterializedAt is null) return Results.BadRequest(new { error = "Materialize the authoritative requirement baseline before recording a software build." });
            if (baseline.ProjectId != request.ProjectId || baseline.ReleaseId != request.ReleaseId) return Results.BadRequest(new { error = "Build, release, and baseline must belong to the same project context." });
            if (await db.SoftwareBuilds.AnyAsync(x => x.BaselineId == baseline.Id, ct)) return Results.Conflict(new { error = "This software build has already been recorded." });
            try { var build = new SoftwareBuild(request.ProjectId, request.ReleaseId, request.BaselineId, baseline.DisplayNumber, request.Description, http.UserAccount().UserName, DateTimeOffset.UtcNow);
                db.SoftwareBuilds.Add(build); await db.SaveChangesAsync(ct); return Results.Created($"/api/builds/{build.Id}", new { build.Id, build.BuildNumber }); }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        app.MapGet("/api/builds/{id:guid}", async (Guid id, AeroLinkDbContext db, CancellationToken ct) =>
        {
            var build = await db.SoftwareBuilds.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct); if (build is null) return Results.NotFound();
            var baseline = await db.CandidateBaselines.AsNoTracking().SingleAsync(x => x.Id == build.BaselineId, ct);
            var scrIds = await db.BaselineSelections.AsNoTracking().Where(x => x.BaselineId == baseline.Id).Select(x => x.ChangeRequestId).ToListAsync(ct);
            var scrs = await db.SystemChangeRequests.AsNoTracking().Where(x => scrIds.Contains(x.Id)).Include(x => x.RequirementChanges).OrderBy(x => x.BaseNumber).ThenByDescending(x => x.Revision).ToListAsync(ct);
            var effectiveRequirements = await (from member in db.BaselineRequirements.AsNoTracking().Where(x => x.BaselineId == baseline.Id)
                                               join artifact in db.Requirements.AsNoTracking() on member.ArtifactId equals artifact.Id
                                               join revision in db.RequirementRevisions.AsNoTracking() on member.RevisionId equals revision.Id
                                               orderby artifact.BaseNumber select new { artifact.Id, displayNumber = artifact.BaseNumber + "." + (revision.Revision < 10 ? "0" : "") + revision.Revision,
                                                   level = artifact.Level.ToString(), revision.Statement, revision.VerificationMethod }).ToListAsync(ct);
            return Results.Ok(new { build.Id, build.BuildNumber, build.Description, state = build.State.ToString(), build.RecordedBy, build.RecordedAt, build.ReleasedAt,
                build.ProjectId, build.ReleaseId, baseline = new { baseline.Id, baseline.DisplayNumber, baseline.Name, baseline.ContentHash, baseline.FrozenAt },
                effectiveRequirements, scrs = scrs.Select(x => new { x.Id, x.DisplayNumber, x.Title, state = x.State.ToString(), requirements = x.RequirementChanges.OrderBy(r => r.BaseNumber).ThenByDescending(r => r.Revision).Select(r => new { r.Id, r.DisplayNumber, level = r.Level.ToString(), kind = r.Kind.ToString(), r.Statement }) }) });
        });

        app.MapGet("/api/baselines", async (Guid projectId, Guid releaseId, AeroLinkDbContext db, CancellationToken ct) =>
        {
            var items = await db.CandidateBaselines.AsNoTracking().Where(x => x.ProjectId == projectId && x.ReleaseId == releaseId)
                .OrderBy(x => x.BaseNumber).ThenByDescending(x => x.Revision).Select(x => new { x.Id, x.BaseNumber, x.Revision, x.Name, state = x.State.ToString(), x.ContentHash, x.RequirementsHash, x.RequirementsMaterializedAt, x.CreatedAt, x.FrozenAt, selectionCount = x.Selections.Count }).ToListAsync(ct);
            return Results.Ok(items.Select(x => new { x.Id, displayNumber = ArtifactNumber.Display(x.BaseNumber, x.Revision), x.Name, x.state, x.ContentHash, x.RequirementsHash, x.RequirementsMaterializedAt, x.CreatedAt, x.FrozenAt, x.selectionCount }));
        });

        app.MapGet("/api/baselines/predecessors", async (Guid projectId, Guid releaseId, AeroLinkDbContext db, CancellationToken ct) =>
        {
            var rows = await (from baseline in db.CandidateBaselines.AsNoTracking()
                              join release in db.Releases.AsNoTracking() on baseline.ReleaseId equals release.Id
                              where baseline.ProjectId == projectId && baseline.ReleaseId != releaseId
                                    && baseline.RequirementsMaterializedAt != null
                              select new
                              {
                                  baseline.Id,
                                  baseline.BaseNumber,
                                  baseline.Revision,
                                  baseline.Name,
                                  baseline.ReleaseId,
                                  release = release.Version,
                                  release.IsReleased,
                                  baseline.RequirementsHash,
                                  baseline.FrozenAt,
                                  requirementCount = db.BaselineRequirements.Count(x => x.BaselineId == baseline.Id)
                              }).ToListAsync(ct);
            var items = rows
                .OrderByDescending(x => x.IsReleased)
                .ThenByDescending(x => x.release, StringComparer.Ordinal)
                .ThenByDescending(x => x.FrozenAt)
                .Select(x => new
                {
                    x.Id,
                    displayNumber = ArtifactNumber.Display(x.BaseNumber, x.Revision),
                    x.Name,
                    x.ReleaseId,
                    x.release,
                    x.IsReleased,
                    x.RequirementsHash,
                    x.requirementCount
                });
            return Results.Ok(items);
        });

        app.MapPost("/api/baselines", async (CreateBaselineRequest request, HttpContext http, IBaselineRepository repository, AeroLinkDbContext db, IdentityService identity, CancellationToken ct) =>
        {
            if (!await http.HasProjectRoleAsync(db, identity, request.ProjectId, ct, ProgramRole.ConfigurationManager)) return Results.Forbid();
            try
            {
                var release = await db.Releases.SingleOrDefaultAsync(x => x.Id == request.ReleaseId && x.ProjectId == request.ProjectId && !x.IsReleased, ct);
                if (release is null)
                    return Results.BadRequest(new { error = "Candidate baselines can only be created for an unreleased version in this project." });
                if (await db.CandidateBaselines.AnyAsync(x => x.ProjectId == request.ProjectId && x.ReleaseId == request.ReleaseId, ct))
                    return Results.Conflict(new { error = "A software build already exists for this release." });
                var priorProductExists = await db.CandidateBaselines.AnyAsync(x => x.ProjectId == request.ProjectId && x.ReleaseId != request.ReleaseId && x.RequirementsMaterializedAt != null, ct);
                if (priorProductExists && request.PredecessorBaselineId is null)
                    return Results.BadRequest(new { error = "Select the exact predecessor product baseline that this candidate inherits." });
                if (request.PredecessorBaselineId is not null && !await db.CandidateBaselines.AnyAsync(x => x.Id == request.PredecessorBaselineId && x.ProjectId == request.ProjectId && x.ReleaseId != request.ReleaseId && x.RequirementsMaterializedAt != null, ct))
                    return Results.BadRequest(new { error = "The predecessor must be a materialized baseline from this project." });
                var baseline = new CandidateBaseline(SoftwareBuildIdentifier.FromVersion(release.Version), 0, request.ProjectId, request.ReleaseId,
                    request.PredecessorBaselineId, request.Name, http.UserAccount().UserName, DateTimeOffset.UtcNow);
                await repository.AddAsync(baseline, ct); await repository.SaveAsync(ct);
                return Results.Created($"/api/baselines/{baseline.Id}", ApiMap.Baseline(baseline));
            }
            catch (DomainException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        app.MapGet("/api/baselines/{id:guid}", async (Guid id, IBaselineRepository repository, AeroLinkDbContext db, CancellationToken ct) =>
        {
            var baseline = await repository.GetAsync(id, ct); if (baseline is null) return Results.NotFound();
            var scrIds = baseline.Selections.Select(x => x.ChangeRequestId).ToList();
            var selected = await db.SystemChangeRequests.AsNoTracking().Where(x => scrIds.Contains(x.Id))
                .Include(x => x.RequirementChanges).ToListAsync(ct);
            return Results.Ok(ApiMap.BaselineDetail(baseline, selected));
        });

        app.MapGet("/api/baselines/{id:guid}/eligible-scrs", async (Guid id, IBaselineRepository repository, AeroLinkDbContext db, CancellationToken ct) =>
        {
            var baseline = await repository.GetAsync(id, ct); if (baseline is null) return Results.NotFound();
            var items = await db.SystemChangeRequests.AsNoTracking()
                .Where(x => x.ProjectId == baseline.ProjectId && x.TargetReleaseId == baseline.ReleaseId && x.State == ChangeRequestState.Approved)
                .OrderBy(x => x.BaseNumber).Select(x => new { x.Id, displayNumber = x.BaseNumber + "." + (x.Revision < 10 ? "0" : "") + x.Revision, x.Title, requirementCount = x.RequirementChanges.Count, x.UpdatedAt }).ToListAsync(ct);
            return Results.Ok(items);
        });

        app.MapPost("/api/baselines/{id:guid}/selections", async (Guid id, BaselineSelectionRequest request, HttpContext http, IBaselineRepository baselines, IChangeRequestRepository scrs, AeroLinkDbContext db, IdentityService identity, CancellationToken ct) =>
        {
            var baseline = await baselines.GetAsync(id, ct); if (baseline is null) return Results.NotFound();
            if (!await http.HasProjectRoleAsync(db, identity, baseline.ProjectId, ct, ProgramRole.ConfigurationManager)) return Results.Forbid();
            var scr = await scrs.GetAsync(request.ChangeRequestId, ct); if (scr is null) return Results.NotFound();
            try { baseline.Select(scr, http.UserAccount().UserName, DateTimeOffset.UtcNow); await baselines.SaveAsync(ct); return Results.Ok(ApiMap.Baseline(baseline)); }
            catch (DomainException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        app.MapDelete("/api/baselines/{id:guid}/selections/{changeRequestId:guid}", async (Guid id, Guid changeRequestId, HttpContext http, IBaselineRepository baselines, IChangeRequestRepository scrs, AeroLinkDbContext db, IdentityService identity, CancellationToken ct) =>
        {
            var baseline = await baselines.GetAsync(id, ct); if (baseline is null) return Results.NotFound();
            if (!await http.HasProjectRoleAsync(db, identity, baseline.ProjectId, ct, ProgramRole.ConfigurationManager)) return Results.Forbid();
            var scr = await scrs.GetAsync(changeRequestId, ct); if (scr is null) return Results.NotFound();
            try { baseline.Remove(scr, http.UserAccount().UserName, DateTimeOffset.UtcNow); await baselines.SaveAsync(ct); return Results.NoContent(); }
            catch (DomainException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        // The procedure side of a baseline: which approved test change requests it carries, and fixing the
        // exact procedure revisions that follow from them.
        //
        // Selecting is allowed after the freeze, unlike selecting a change request, because a procedure is
        // written against a frozen requirement and so is finished later. What closes this manifest is
        // materialization, not the freeze.
        app.MapGet("/api/baselines/{id:guid}/test-change-requests", async (Guid id, HttpContext http, AeroLinkDbContext db, CancellationToken ct) =>
        {
            var baseline = await db.CandidateBaselines.AsNoTracking().Include(x => x.TestChangeSelections)
                .SingleOrDefaultAsync(x => x.Id == id, ct);
            if (baseline is null) return Results.NotFound();
            if (!await http.HasProjectAccessAsync(db, baseline.ProjectId, ct)) return Results.Forbid();
            var selectedIds = baseline.TestChangeSelections.Select(x => x.TestChangeRequestId).ToList();
            // Only approved packages that actually carry procedure work can be selected. One that concluded no
            // test work was needed has nothing to contribute, and no controlled number to contribute it under.
            // Nor does one approved before procedure decisions existed: it is real history, but a build cannot
            // carry work that was never stated. Approved already excludes a superseded revision.
            var available = await db.TestChangeReviews.AsNoTracking()
                .Where(x => x.ProjectId == baseline.ProjectId && x.ReleaseId == baseline.ReleaseId
                    && x.State == TestChangeReviewState.Approved
                    && x.Outcome == TestChangeReviewOutcome.ChangeRequired
                    && x.ProcedureChanges.Any()
                    && !selectedIds.Contains(x.Id))
                .Select(x => new { x.Id, x.DisplayNumber, discipline = x.Discipline.ToString(), x.SourceChangeRequestNumber })
                .ToListAsync(ct);
            return Results.Ok(new
            {
                baseline.Id, baseline.DisplayNumber, baseline.TestProceduresHash, baseline.TestProceduresMaterializedAt,
                selected = baseline.TestChangeSelections.OrderBy(x => x.TestChangeRequestDisplayNumber)
                    .Select(x => new { x.TestChangeRequestId, x.TestChangeRequestDisplayNumber }),
                available = available.OrderBy(x => x.DisplayNumber),
                procedureCount = await db.BaselineTestProcedures.CountAsync(x => x.BaselineId == id, ct)
            });
        });

        app.MapPost("/api/baselines/{id:guid}/test-change-requests", async (Guid id, BaselineTestChangeSelectionRequest request,
            HttpContext http, AeroLinkDbContext db, IdentityService identity, CancellationToken ct) =>
        {
            var baseline = await db.CandidateBaselines.Include(x => x.TestChangeSelections).Include(x => x.Events)
                .SingleOrDefaultAsync(x => x.Id == id, ct);
            if (baseline is null) return Results.NotFound();
            if (!await http.HasProjectRoleAsync(db, identity, baseline.ProjectId, ct, ProgramRole.ConfigurationManager)) return Results.Forbid();
            // Loaded with its procedure changes, because the aggregate refuses a package carrying none and an
            // unloaded collection is indistinguishable from an empty one.
            var review = await db.TestChangeReviews.Include(x => x.ProcedureChanges)
                .SingleOrDefaultAsync(x => x.Id == request.TestChangeRequestId, ct);
            if (review is null) return Results.NotFound();
            try
            {
                baseline.SelectTestChangeRequest(review, http.UserAccount().UserName, DateTimeOffset.UtcNow);
                await db.SaveChangesAsync(ct);
                return Results.Ok(new { baseline.Id, selected = baseline.TestChangeSelections.Count });
            }
            catch (DomainException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        app.MapDelete("/api/baselines/{id:guid}/test-change-requests/{testChangeRequestId:guid}", async (Guid id,
            Guid testChangeRequestId, HttpContext http, AeroLinkDbContext db, IdentityService identity, CancellationToken ct) =>
        {
            var baseline = await db.CandidateBaselines.Include(x => x.TestChangeSelections).Include(x => x.Events)
                .SingleOrDefaultAsync(x => x.Id == id, ct);
            if (baseline is null) return Results.NotFound();
            if (!await http.HasProjectRoleAsync(db, identity, baseline.ProjectId, ct, ProgramRole.ConfigurationManager)) return Results.Forbid();
            var review = await db.TestChangeReviews.SingleOrDefaultAsync(x => x.Id == testChangeRequestId, ct);
            if (review is null) return Results.NotFound();
            try
            {
                baseline.RemoveTestChangeRequest(review, http.UserAccount().UserName, DateTimeOffset.UtcNow);
                await db.SaveChangesAsync(ct);
                return Results.NoContent();
            }
            catch (DomainException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        // Explicit one-time migration from a pre-manifest controlled inventory. This is separate from normal
        // materialization so no successor can silently rewrite what its released predecessor is said to carry.
        app.MapGet("/api/baselines/{id:guid}/legacy-procedure-manifest-bootstrap", async (Guid id,
            HttpContext http, LegacyProcedureManifestBootstrapper bootstrapper, AeroLinkDbContext db,
            IdentityService identity, CancellationToken ct) =>
        {
            var projectId = await db.CandidateBaselines.Where(x => x.Id == id)
                .Select(x => (Guid?)x.ProjectId).SingleOrDefaultAsync(ct);
            if (projectId is null) return Results.NotFound();
            if (!await http.HasProjectRoleAsync(db, identity, projectId.Value, ct,
                    ProgramRole.ConfigurationManager)) return Results.Forbid();
            try
            {
                var preview = await bootstrapper.PreviewAsync(id, ct);
                return preview is null ? Results.NotFound() : Results.Ok(preview);
            }
            catch (DomainException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        app.MapPost("/api/baselines/{id:guid}/legacy-procedure-manifest-bootstrap", async (Guid id,
            LegacyProcedureManifestBootstrapRequest request, HttpContext http,
            LegacyProcedureManifestBootstrapper bootstrapper, AeroLinkDbContext db,
            IdentityService identity, CancellationToken ct) =>
        {
            var projectId = await db.CandidateBaselines.Where(x => x.Id == id)
                .Select(x => (Guid?)x.ProjectId).SingleOrDefaultAsync(ct);
            if (projectId is null) return Results.NotFound();
            if (!await http.HasProjectRoleAsync(db, identity, projectId.Value, ct,
                    ProgramRole.ConfigurationManager)) return Results.Forbid();
            if (!request.ConfirmLegacySnapshot)
                return Results.BadRequest(new { error =
                    "Confirm that this is a migration snapshot of the current legacy controlled inventory, not reconstructed historical release evidence." });
            try
            {
                var result = await bootstrapper.BootstrapAsync(id, http.UserAccount().UserName,
                    request.ExpectedHash, DateTimeOffset.UtcNow, ct);
                return result is null ? Results.NotFound() : Results.Ok(result);
            }
            catch (DomainException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        app.MapPost("/api/baselines/{id:guid}/materialize-test-procedures", async (Guid id, EmptyMutationRequest request,
            HttpContext http, TestProcedureBaselineMaterializer materializer, AeroLinkDbContext db, IdentityService identity, CancellationToken ct) =>
        {
            var projectId = await db.CandidateBaselines.Where(x => x.Id == id).Select(x => (Guid?)x.ProjectId).SingleOrDefaultAsync(ct);
            if (projectId is null) return Results.NotFound();
            if (!await http.HasProjectRoleAsync(db, identity, projectId.Value, ct, ProgramRole.ConfigurationManager)) return Results.Forbid();
            try { return Results.Ok(await materializer.MaterializeAsync(id, http.UserAccount().UserName, DateTimeOffset.UtcNow, ct)); }
            catch (DomainException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        // What reopening this build would disturb, before anybody commits to it.
        //
        // One withdrawal can strand several other records, so the confirmation has to say what it is about to
        // do rather than asking for a decision the reader cannot inform. Computed by the same pass that
        // performs the reopen, which is what stops the two describing different things: a preview assembled by
        // its own query drifts the first time either side is changed and nothing fails while it does.
        app.MapGet("/api/baselines/{id:guid}/reopen-preview", async (Guid id, HttpContext http,
            IBaselineRepository repository, IdentityService identity, AeroLinkDbContext db,
            VerificationImpactService verificationImpact, CancellationToken ct) =>
        {
            var baseline = await repository.GetAsync(id, ct); if (baseline is null) return Results.NotFound();
            if (!await http.HasProjectRoleAsync(db, identity, baseline.ProjectId, ct, ProgramRole.ConfigurationManager)) return Results.Forbid();

            var refusal = await ReopenRefusalAsync(db, baseline, ct);
            var consequences = refusal is not null
                ? ReopenConsequences.None
                : await new RequirementBaselineDematerializer(db, verificationImpact).PreviewAsync(baseline.Id, baseline.DisplayNumber, ct);
            return Results.Ok(new
            {
                available = refusal is null,
                refusal?.Error,
                refusal?.Code,
                consequences = ApiMap.ReopenConsequences(consequences),
            });
        });

        // The way back through the freeze, for a build that has not been released.
        //
        // Held to the same authority as freezing it. Reopening is not an author correcting their own work --
        // it unfixes what a build contains for everybody planning against it -- so the person who sealed it is
        // the person who unseals it.
        app.MapPost("/api/baselines/{id:guid}/reopen", async (Guid id, ReopenBaselineRequest request, HttpContext http,
            IBaselineRepository repository, IdentityService identity, AeroLinkDbContext db,
            VerificationImpactService verificationImpact, CancellationToken ct) =>
        {
            var baseline = await repository.GetAsync(id, ct); if (baseline is null) return Results.NotFound();
            if (!await http.HasProjectRoleAsync(db, identity, baseline.ProjectId, ct, ProgramRole.ConfigurationManager)) return Results.Forbid();

            var refusal = await ReopenRefusalAsync(db, baseline, ct);
            if (refusal is not null) return Results.BadRequest(new { error = refusal.Error, code = refusal.Code });
            try
            {
                // The revisions come back before the record says the build is open, so there is no moment at
                // which the baseline reads as open while the requirements still read as sealed.
                var consequences = await new RequirementBaselineDematerializer(db, verificationImpact).DematerializeAsync(
                    baseline.Id, http.UserAccount().UserName, baseline.DisplayNumber, DateTimeOffset.UtcNow, ct);
                baseline.Reopen(http.UserAccount().UserName, request.Reason ?? "", DateTimeOffset.UtcNow);
                await repository.SaveAsync(ct);
                return Results.Ok(new
                {
                    baseline = ApiMap.Baseline(baseline),
                    revisionsTakenBack = consequences.RevisionCount,
                    consequences = ApiMap.ReopenConsequences(consequences),
                });
            }
            catch (DomainException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        // Freezing is deliberately not gated on verification decisions. Freezing then materializing is what
        // creates the requirement revisions a test engineer needs in order to write a procedure at all, so
        // blocking the freeze would withhold the test team's own inputs and deadlock the release. The gate the
        // verification queue belongs to is release approval, where it appears as a named readiness gate.
        app.MapPost("/api/baselines/{id:guid}/freeze", async (Guid id, EmptyMutationRequest request, HttpContext http, IBaselineRepository repository, AeroLinkDbContext db, IdentityService identity, CancellationToken ct) =>
        {
            var baseline = await repository.GetAsync(id, ct); if (baseline is null) return Results.NotFound();
            if (!await http.HasProjectRoleAsync(db, identity, baseline.ProjectId, ct, ProgramRole.ConfigurationManager)) return Results.Forbid();
            try
            {
                baseline.Freeze(http.UserAccount().UserName, DateTimeOffset.UtcNow);
                await repository.SaveAsync(ct);
                return Results.Ok(ApiMap.Baseline(baseline));
            }
            catch (DomainException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        app.MapPost("/api/baselines/{id:guid}/materialize-requirements", async (Guid id, EmptyMutationRequest request, HttpContext http, RequirementBaselineMaterializer materializer, AeroLinkDbContext db, IdentityService identity, CancellationToken ct) =>
        {
            var projectId=await db.CandidateBaselines.Where(x=>x.Id==id).Select(x=>(Guid?)x.ProjectId).SingleOrDefaultAsync(ct); if(projectId is null)return Results.NotFound(); if(!await http.HasProjectRoleAsync(db,identity,projectId.Value,ct,ProgramRole.ConfigurationManager))return Results.Forbid();
            try { return Results.Ok(await materializer.MaterializeAsync(id, http.UserAccount().UserName, DateTimeOffset.UtcNow, ct)); }
            catch (DomainException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        app.MapGet("/api/baselines/{id:guid}/swrd", async (Guid id, AeroLinkDbContext db, CancellationToken ct) =>
        {
            var baseline = await db.CandidateBaselines.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct); if (baseline is null) return Results.NotFound();
            var release = await db.Releases.AsNoTracking().SingleAsync(x => x.Id == baseline.ReleaseId, ct);
            var project = await db.Projects.AsNoTracking().SingleAsync(x => x.Id == baseline.ProjectId, ct);
            var rows = await (from member in db.BaselineRequirements.AsNoTracking().Where(x => x.BaselineId == id)
                              join artifact in db.Requirements.AsNoTracking() on member.ArtifactId equals artifact.Id
                              join revision in db.RequirementRevisions.AsNoTracking() on member.RevisionId equals revision.Id
                              join scr in db.SystemChangeRequests.AsNoTracking() on revision.SourceChangeRequestId equals scr.Id
                              orderby artifact.BaseNumber
                              select new { artifact.Id, artifact.BaseNumber, revisionId = revision.Id, revision.Revision, displayNumber = artifact.BaseNumber + "." + (revision.Revision < 10 ? "0" : "") + revision.Revision,
                                  level = artifact.Level.ToString(), revision.Statement, revision.Rationale, revision.VerificationMethod, sourceChangeRequestId = scr.Id,
                                  sourceScr = scr.BaseNumber + "." + (scr.Revision < 10 ? "0" : "") + scr.Revision }).ToListAsync(ct);
            return Results.Ok(new { documentType = "SWRD", title = $"{project.SoftwareProduct} Software Requirements Document", release = release.Version,
                baseline = baseline.DisplayNumber, baseline.Name, baseline.RequirementsHash, baseline.RequirementsMaterializedAt, requirementCount = rows.Count, requirements = rows });
        });

        app.MapPost("/api/baselines/{id:guid}/generate-documents", async (Guid id, EmptyMutationRequest request, HttpContext http, AeroLinkDbContext db, IdentityService identity, ControlledOutputGenerator generator, EvidenceFileStore store, CancellationToken ct) =>
        {
            var baseline = await db.CandidateBaselines.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct); if (baseline is null) return Results.NotFound(); if (baseline.RequirementsMaterializedAt is null) return Results.BadRequest(new { error = "Materialize the requirement baseline before generating controlled outputs." });
            if(!await http.HasProjectRoleAsync(db,identity,baseline.ProjectId,ct,ProgramRole.ConfigurationManager))return Results.Forbid();
            if (await db.ReleaseCampaigns.AsNoTracking().AnyAsync(x => x.BaselineId == id && x.State == ReleaseCampaignState.InReview, ct))
                return Results.Conflict(new { error = "The release package is frozen while approval is in progress.", code = "release_package_frozen" });
            var release = await db.Releases.AsNoTracking().SingleAsync(x => x.Id == baseline.ReleaseId, ct); var project = await db.Projects.AsNoTracking().SingleAsync(x => x.Id == baseline.ProjectId, ct);
            var requirementCounts = await (from member in db.BaselineRequirements.AsNoTracking().Where(x => x.BaselineId == id) join artifact in db.Requirements.AsNoTracking() on member.ArtifactId equals artifact.Id group artifact by artifact.Level into g select new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, ct);
            var procedureEffectivity = await TestProcedureEffectivity.ForBaselineAsync(db, id, ct);
            var procedureRevisionIds = procedureEffectivity?.RevisionIds ?? [];
            var testCounts = await (from revision in db.TestProcedureRevisions.AsNoTracking().Where(x => procedureRevisionIds.Contains(x.Id))
                                    join procedure in db.TestProcedures.AsNoTracking() on revision.ProcedureId equals procedure.Id
                                    group procedure by procedure.Level into grouped
                                    select new { Key = grouped.Key, Count = grouped.Count() })
                .ToDictionaryAsync(x => x.Key, x => x.Count, ct);
            var suffix = release.Version.Replace(".", ""); var specs = new[] {
                (ControlledDocumentType.Sysrd,$"SYSRD-{int.Parse(suffix):D6}",$"{project.SoftwareProduct} System Requirements Document",requirementCounts.GetValueOrDefault(RequirementLevel.System)),
                (ControlledDocumentType.SwrdHighLevel,$"HLRD-{int.Parse(suffix):D6}",$"{project.SoftwareProduct} High-Level Software Requirements Document",requirementCounts.GetValueOrDefault(RequirementLevel.HighLevel)),
                (ControlledDocumentType.SwrdLowLevel,$"LLRD-{int.Parse(suffix):D6}",$"{project.SoftwareProduct} Low-Level Software Requirements Document",requirementCounts.GetValueOrDefault(RequirementLevel.LowLevel)),
                (ControlledDocumentType.SystemTestProcedures,$"SYSTD-{int.Parse(suffix):D6}",$"{project.SoftwareProduct} System Test Procedures",testCounts.GetValueOrDefault(TestProcedureLevel.System)),
                (ControlledDocumentType.HighLevelTestProcedures,$"HLRTD-{int.Parse(suffix):D6}",$"{project.SoftwareProduct} HLR Test Procedures",testCounts.GetValueOrDefault(TestProcedureLevel.HighLevel)),
                (ControlledDocumentType.LowLevelTestProcedures,$"LLRTD-{int.Parse(suffix):D6}",$"{project.SoftwareProduct} LLR Test Procedures",testCounts.GetValueOrDefault(TestProcedureLevel.LowLevel)) };
            // The approved layout for each document type, if the programme has recorded one. Bound to the document
            // at generation and never re-resolved: revising a template afterwards must not change a document that
            // has already been produced and possibly signed.
            var approvedTemplates = await ControlledLayouts.ApprovedAsync(db, project.Id, ct);
            // #419: a controlled test-procedure document is one exact, immutable procedure manifest. The
            // record must never be created against a compatibility projection that materialization later
            // changes, and its hash basis must never fall back to the requirement manifest.
            var procedureManifestReady = baseline.TestProceduresMaterializedAt is not null && baseline.TestProceduresHash is not null;
            var existing = await db.ControlledDocuments.Where(x => x.BaselineId == id).ToListAsync(ct);
            var skipped = new List<ControlledDocumentType>();
            var created = new List<ControlledDocument>();
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            foreach (var spec in specs.Where(s => existing.All(x => x.Type != s.Item1)))
            {
                var procedureDocument = spec.Item1 is ControlledDocumentType.SystemTestProcedures or ControlledDocumentType.HighLevelTestProcedures or ControlledDocumentType.LowLevelTestProcedures;
                if (procedureDocument && !procedureManifestReady)
                {
                    skipped.Add(spec.Item1);
                    continue;
                }
                var manifestHash = procedureDocument ? baseline.TestProceduresHash! : baseline.RequirementsHash!;
                var content = $"{manifestHash}|{spec.Item1}|{spec.Item4}|{http.UserAccount().UserName}";
                var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant();
                var document = new ControlledDocument(project.Id, release.Id, baseline.Id, spec.Item1, spec.Item2, spec.Item3, 0, hash, spec.Item4, DateTimeOffset.UtcNow, approvedTemplates.GetValueOrDefault(spec.Item1));
                db.ControlledDocuments.Add(document);
                created.Add(document);
            }
            await db.SaveChangesAsync(ct);
            // A controlled publication is frozen bytes, not a recipe. Render each format once at creation and
            // keep the exact artifact; downloads serve these bytes so later trace, coverage, metadata or
            // template activity can never silently rewrite what this document record represents.
            var artifacts = new List<ControlledDocumentArtifact>();
            foreach (var document in created)
            {
                foreach (var format in new[] { "docx", "pdf" })
                {
                    var output = await generator.GenerateAsync(document.Id, format, ct);
                    if (output is null) continue;
                    await using var content = new MemoryStream(output.Content);
                    var stored = await store.StoreAsync(content, output.FileName, output.ContentType, ct);
                    artifacts.Add(new ControlledDocumentArtifact(document.Id, format, stored.StorageKey,
                        stored.OriginalFileName, stored.ContentType, stored.Size, stored.Sha256, DateTimeOffset.UtcNow));
                }
            }
            db.ControlledDocumentArtifacts.AddRange(artifacts);
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return Results.Ok(new { generated = created.Count, skipped = skipped.Select(x => x.ToString()).ToArray(), artifacts = artifacts.Count });
        });

        app.MapGet("/api/release-comparison", async (Guid projectId, Guid fromReleaseId, Guid toReleaseId, AeroLinkDbContext db, CancellationToken ct) =>
        {
            var releases = await db.Releases.AsNoTracking().Where(x => x.ProjectId == projectId && (x.Id == fromReleaseId || x.Id == toReleaseId)).ToListAsync(ct); if (releases.Count != 2) return Results.BadRequest(new { error = "Select two releases from the same project." });
            var baselines = await db.CandidateBaselines.AsNoTracking().Where(x => x.ProjectId == projectId && (x.ReleaseId == fromReleaseId || x.ReleaseId == toReleaseId)).ToListAsync(ct); var fromBaseline = baselines.Where(x => x.ReleaseId == fromReleaseId).OrderByDescending(x => x.FrozenAt).FirstOrDefault(); var toBaseline = baselines.Where(x => x.ReleaseId == toReleaseId).OrderByDescending(x => x.FrozenAt).FirstOrDefault();
            async Task<Dictionary<string, (int Revision, string Statement, string Level)>> Set(Guid? baselineId) { if (baselineId is null) return []; var rows = await (from member in db.BaselineRequirements.AsNoTracking().Where(x => x.BaselineId == baselineId) join artifact in db.Requirements.AsNoTracking() on member.ArtifactId equals artifact.Id join revision in db.RequirementRevisions.AsNoTracking() on member.RevisionId equals revision.Id select new { artifact.BaseNumber, revision.Revision, revision.Statement, Level = artifact.Level.ToString() }).ToListAsync(ct); return rows.ToDictionary(x => x.BaseNumber, x => (x.Revision, x.Statement, x.Level)); }
            var effectiveAvailable = toBaseline?.RequirementsMaterializedAt is not null;
            var from = effectiveAvailable ? await Set(fromBaseline?.Id) : []; var to = effectiveAvailable ? await Set(toBaseline?.Id) : []; var keys = from.Keys.Union(to.Keys).OrderBy(x => x).ToList();
            var effective = keys.Select(key => { var hasFrom = from.TryGetValue(key, out var a); var hasTo = to.TryGetValue(key, out var b); var kind = !hasFrom ? "Added" : !hasTo ? "Retired" : a.Revision != b.Revision || a.Statement != b.Statement ? "Modified" : "Unchanged"; return new { baseNumber = key, kind, fromRevision = hasFrom ? a.Revision : (int?)null, toRevision = hasTo ? b.Revision : (int?)null, level = hasTo ? b.Level : a.Level }; }).ToList();
            var requests = await db.SystemChangeRequests.AsNoTracking().Where(x => x.TargetReleaseId == toReleaseId).Include(x => x.RequirementChanges).OrderBy(x => x.BaseNumber).ToListAsync(ct);
            var proposed = requests.SelectMany(scr => scr.RequirementChanges.Select(change => new { changeRequestId = scr.Id, scr = scr.DisplayNumber, scr.Title, state = scr.State.ToString(), type = scr.Type.ToString(), change.DisplayNumber, level = change.Level.ToString(), kind = change.Kind.ToString(), change.Statement })).ToList();
            return Results.Ok(new { fromRelease = releases.Single(x => x.Id == fromReleaseId).Version, toRelease = releases.Single(x => x.Id == toReleaseId).Version,
                fromBaseline = fromBaseline?.DisplayNumber, toBaseline = toBaseline?.DisplayNumber, toMaterialized = effectiveAvailable,
                summary = new { added = effective.Count(x => x.kind == "Added"), modified = effective.Count(x => x.kind == "Modified"), retired = effective.Count(x => x.kind == "Retired"), unchanged = effective.Count(x => x.kind == "Unchanged"), proposed = proposed.Count }, effective, proposed });
        });
    }

    private sealed record ReopenRefusal(string Error, string Code);

    /// <summary>
    /// Why this baseline cannot be reopened, or null when it can.
    ///
    /// Shared by the preview and the act so a reader is never offered a reopen that is going to be refused,
    /// and never refused one the preview said was available. The state rules are the aggregate's own and are
    /// asked here in the same order it enforces them, because a preview that threw would have to be caught and
    /// turned back into words -- and the words are the whole point.
    /// </summary>
    private static async Task<ReopenRefusal?> ReopenRefusalAsync(AeroLinkDbContext db, CandidateBaseline baseline,
        CancellationToken ct)
    {
        if (baseline.State == CandidateBaselineState.Released)
            return new("A released baseline cannot be reopened. It is what the world was told the build contains.",
                "baseline_released");
        if (baseline.State != CandidateBaselineState.Frozen)
            return new("Only a frozen baseline can be reopened.", "not_frozen");

        // A build sealed on top of this one derives from what this one contains. Taking these revisions back
        // would leave the successor sealed around requirement revisions that no longer exist, so the refusal
        // names it and the order of unsealing is the reverse of the order of sealing.
        var successor = await db.CandidateBaselines.AsNoTracking()
            .Where(x => x.PredecessorBaselineId == baseline.Id && x.State != CandidateBaselineState.Draft)
            .Select(x => new { x.DisplayNumber, x.State })
            .FirstOrDefaultAsync(ct);
        return successor is null
            ? null
            : new($"{successor.DisplayNumber} was {successor.State.ToString().ToLowerInvariant()} on top of this baseline and derives from what it contains. Deal with {successor.DisplayNumber} first.",
                "successor_baseline");
    }
}
