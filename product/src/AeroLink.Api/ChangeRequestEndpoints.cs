using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Common;
using AeroLink.Domain.Contracts;
using AeroLink.Domain.Identity;
using AeroLink.Domain.Releases;
using AeroLink.Domain.Requirements;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using System.Data;
using System.Text.Json;

namespace AeroLink.Api;

/// <summary>
/// The controlled change request: the record that carries a proposed change through review to
/// approval.
///
/// Nothing here mutates a requirement. A change request states what should change, and the requirement only
/// moves when a baseline is materialized — which is what makes the decision reconstructable afterwards.
/// </summary>
public static class ChangeRequestEndpoints
{
    public static void MapChangeRequestEndpoints(this WebApplication app)
    {
        app.MapPost("/api/scrs/{id:guid}/retarget", async (Guid id, RetargetScrRequest request, HttpContext http, IScrRepository repository, AeroLinkDbContext db, IdentityService identity, VerificationImpactService verificationImpact, CancellationToken ct) =>
        {
            var scr = await repository.GetAsync(id, ct); if (scr is null) return Results.NotFound();
            if (!await http.HasProjectAccessAsync(db, scr.ProjectId, ct)) return Results.Forbid();
            var actor = http.UserAccount();
            if (!CanAdminister(scr, actor)) return Results.Forbid();
            if (!await db.Releases.AnyAsync(x => x.Id == request.TargetReleaseId && x.ProjectId == scr.ProjectId && !x.IsReleased, ct)) return Results.BadRequest(new { error = "Choose an unreleased target release in this project." });
            // Verification work follows its change request. Left behind, it would hold a release the change no longer
            // belongs to and go missing from the one it does.
            try
            {
                var now = DateTimeOffset.UtcNow;
                scr.Retarget(actor.UserName, request.TargetReleaseId, request.Reason, now, actor.IsAdministrator);
                await verificationImpact.RetargetAsync(scr.Id, request.TargetReleaseId, now, ct);
                await repository.SaveAsync(ct);
                return Results.Ok(ApiMap.ScrDetail(scr));
            }
            catch (DomainException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        // Putting a change request away for another day, and taking it back off the shelf.
        //
        // The domain has had `Defer` since the allocation states were reworked, and nothing exposed it — so the
        // dashboard counted deferred change requests, the history explorer filtered for them, and the only way
        // one could exist was for the demonstration seeder to create it. The shelf was visible and unreachable.
        //
        // Deferring is the author's decision about their own work, so it takes the same authority as editing it.
        app.MapPost("/api/scrs/{id:guid}/defer", async (Guid id, DeferScrRequest request, HttpContext http, IScrRepository repository, AeroLinkDbContext db, CancellationToken ct) =>
        {
            var scr = await repository.GetAsync(id, ct); if (scr is null) return Results.NotFound();
            if (!await http.HasProjectAccessAsync(db, scr.ProjectId, ct)) return Results.Forbid();
            var actor = http.UserAccount();
            if (!CanAdminister(scr, actor)) return Results.Forbid();
            try
            {
                scr.Defer(actor.UserName, request.Reason ?? "", DateTimeOffset.UtcNow, actor.IsAdministrator);
                await repository.SaveAsync(ct);
                return Results.Ok(ApiMap.ScrDetail(scr));
            }
            catch (DomainException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        /// Stopping a review that should not be running.
        ///
        /// Scoped to people with a stake in it: the author, anybody named as an approver on the active cycle,
        /// a Program manager, and an administrator. Deliberately not "anyone with access" — that would let
        /// somebody with no part in a change halt a review they have nothing to do with, and a controlled tool
        /// should not make that an accident anybody can have.
        app.MapPost("/api/scrs/{id:guid}/cancel-review", async (Guid id, CancelReviewRequest request, HttpContext http,
            IScrRepository repository, AeroLinkDbContext db, IdentityService identity, CancellationToken ct) =>
        {
            var scr = await repository.GetAsync(id, ct); if (scr is null) return Results.NotFound();
            if (!await http.HasProjectAccessAsync(db, scr.ProjectId, ct)) return Results.Forbid();
            var actor = http.UserAccount();
            var isApprover = scr.ActiveReviewCycle?.Steps
                .Any(step => string.Equals(step.ApproverId, actor.UserName, StringComparison.OrdinalIgnoreCase)) ?? false;
            var isLead = await http.HasProjectRoleAsync(db, identity, scr.ProjectId, ct, ProgramRole.ProgramManager);
            if (!CanAdminister(scr, actor) && !isApprover && !isLead) return Results.Forbid();
            if (request.ExpectedVersion is not null && scr.Version != request.ExpectedVersion)
                return Results.Conflict(new { error = "This change request changed after it was opened. Refresh before cancelling the review.", code = "stale_version" });
            try
            {
                scr.CancelReview(actor.UserName, request.Reason ?? "", DateTimeOffset.UtcNow);
                await repository.SaveAsync(ct);
                return Results.Ok(ApiMap.ScrDetail(scr));
            }
            catch (DomainException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        app.MapPost("/api/scrs/{id:guid}/reinstate", async (Guid id, HttpContext http, IScrRepository repository, AeroLinkDbContext db, CancellationToken ct) =>
        {
            var scr = await repository.GetAsync(id, ct); if (scr is null) return Results.NotFound();
            if (!await http.HasProjectAccessAsync(db, scr.ProjectId, ct)) return Results.Forbid();
            var actor = http.UserAccount();
            if (!CanAdminister(scr, actor)) return Results.Forbid();
            try
            {
                scr.Reinstate(actor.UserName, DateTimeOffset.UtcNow, actor.IsAdministrator);
                await repository.SaveAsync(ct);
                return Results.Ok(ApiMap.ScrDetail(scr));
            }
            catch (DomainException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        app.MapPost("/api/scrs/{id:guid}/next-revision", async (Guid id, ActorRequest request, HttpContext http, IScrRepository repository, AeroLinkDbContext db, CancellationToken ct) =>
        {
            var approved = await repository.GetAsync(id, ct); if (approved is null) return Results.NotFound();
            if (!await http.HasProjectAccessAsync(db, approved.ProjectId, ct)) return Results.Forbid();
            var actor = http.UserAccount();
            if (!CanAdminister(approved, actor)) return Results.Forbid();
            if (request.ExpectedVersion is not null && approved.Version != request.ExpectedVersion) return Results.Conflict(new { error = "This approved request changed after it was opened. Refresh before revising.", code = "stale_version" });
            try
            {
                // Whether the build has shipped is a fact about the release, so it is read here and handed to
                // the aggregate, which owns the rule about what that fact forbids.
                var released = await db.Releases.AsNoTracking()
                    .Where(x => x.Id == approved.TargetReleaseId).Select(x => x.IsReleased).SingleOrDefaultAsync(ct);
                var next = approved.StartNextRevision(actor.UserName, DateTimeOffset.UtcNow, released, actor.IsAdministrator);
                await repository.AddAsync(next, ct); await repository.SaveAsync(ct);
                return Results.Created($"/api/scrs/{next.Id}", ApiMap.ScrDetail(next));
            }
            catch (DomainException ex) { return Results.BadRequest(new { error = ex.Message }); }
            catch (DbUpdateException) { return Results.Conflict(new { error = $"A later revision of {approved.BaseNumber} already exists." }); }
        });

        app.MapGet("/api/scrs", async (Guid projectId, Guid? releaseId, int? page, int? pageSize, string? search, ScrState? state, IScrRepository repository, CancellationToken ct) =>
        {
            var result = await repository.QueryAsync(new ScrQuery(projectId, page is null or 0 ? 1 : page.Value, pageSize is null or 0 ? 50 : pageSize.Value, search, state, releaseId), ct);
            return Results.Ok(new { result.Page, result.PageSize, result.TotalCount, result.TotalPages, items = result.Items.Select(ApiMap.ScrSummary) });
        });

        app.MapGet("/api/scrs/{id:guid}", async (Guid id, IScrRepository repository, CancellationToken ct) =>
        {
            var scr = await repository.GetAsync(id, ct);
            return scr is null ? Results.NotFound() : Results.Ok(ApiMap.ScrDetail(scr));
        });

        app.MapGet("/api/authoring/context", async (Guid projectId, ChangeRequestType type, HttpContext http, AeroLinkDbContext db, CancellationToken ct) =>
        {
            if (!await http.HasProjectAccessAsync(db, projectId, ct)) return Results.Forbid();
            var prefixes = type == ChangeRequestType.System ? new[] { "SYSR" } : new[] { "HLR", "LLR" };
            var numbers = new Dictionary<string, string>();
            foreach (var prefix in prefixes) numbers[prefix] = await IdentifierAllocator.PreviewRequirementAsync(db, prefix, ct);
            return Results.Ok(new
            {
                type = type.ToString(),
                changeRequestNumber = await IdentifierAllocator.PreviewChangeRequestAsync(db, type, ct),
                author = new { http.UserAccount().UserName, http.UserAccount().DisplayName },
                requirementNumbers = numbers
            });
        });

        app.MapGet("/api/authoring/requirements", async (Guid projectId, string scope, string? search, int? limit, HttpContext http, AeroLinkDbContext db, CancellationToken ct) =>
        {
            if (!await http.HasProjectAccessAsync(db, projectId, ct)) return Results.Forbid();
            var artifacts = db.Requirements.AsNoTracking().Where(x => x.ProjectId == projectId);
            artifacts = scope.Equals("System", StringComparison.OrdinalIgnoreCase)
                ? artifacts.Where(x => x.Level == RequirementLevel.System)
                : artifacts.Where(x => x.Level == RequirementLevel.HighLevel || x.Level == RequirementLevel.LowLevel);
            if (!string.IsNullOrWhiteSpace(search))
            {
                var term = search.Trim().ToLower();
                artifacts = artifacts.Where(x => x.BaseNumber.ToLower().Contains(term)
                    || db.RequirementRevisions.Any(revision => revision.ArtifactId == x.Id
                        && (revision.Statement.ToLower().Contains(term)
                            || revision.Rationale.ToLower().Contains(term))));
            }
            var rows = await (from artifact in artifacts
                              join revision in db.RequirementRevisions.AsNoTracking() on artifact.Id equals revision.ArtifactId
                              where revision.Revision == db.RequirementRevisions.Where(x => x.ArtifactId == artifact.Id).Max(x => x.Revision)
                              orderby artifact.BaseNumber
                              select new { artifact.Id, artifact.BaseNumber, level = artifact.Level.ToString(), revision.Revision, revision.Statement, revision.Rationale, revision.VerificationMethod, state = revision.State.ToString() })
                .Take(Math.Clamp(limit ?? 12, 1, 50)).ToListAsync(ct);
            // The section each requirement is currently in, so a modification can offer to keep it. Without this
            // the author is asked to choose a section for a requirement that already has one, which invites
            // moving it by accident — the commonest way structure gets quietly rearranged.
            var found = rows.Select(x => x.Id).ToList();
            var placements = await (from node in db.SpecificationNodes.AsNoTracking()
                                    where node.RequirementArtifactId != null && found.Contains(node.RequirementArtifactId.Value)
                                       && node.ParentId != null
                                    select new { ArtifactId = node.RequirementArtifactId!.Value, SectionId = node.ParentId!.Value })
                .ToListAsync(ct);
            var sectionByArtifact = placements.GroupBy(x => x.ArtifactId)
                .ToDictionary(x => x.Key, x => x.First().SectionId);
            return Results.Ok(rows.Select(x => new { x.Id, x.BaseNumber, displayNumber = $"{x.BaseNumber}.{x.Revision:D2}", x.level, x.Revision, nextRevision = x.Revision + 1, x.Statement, x.Rationale, x.VerificationMethod, x.state,
                currentSectionId = sectionByArtifact.TryGetValue(x.Id, out var sectionId) ? sectionId : (Guid?)null }));
        });

        app.MapGet("/api/authoring/upstream-requirements", async (Guid projectId, Guid releaseId,
            RequirementLevel childLevel, string? search, string? selected, int? limit, HttpContext http, AeroLinkDbContext db,
            CancellationToken ct) =>
        {
            if (!await http.HasProjectAccessAsync(db, projectId, ct)) return Results.Forbid();
            var parentLevel = childLevel switch
            {
                RequirementLevel.HighLevel => RequirementLevel.System,
                RequirementLevel.LowLevel => RequirementLevel.HighLevel,
                _ => (RequirementLevel?)null
            };
            if (parentLevel is null)
                return Results.BadRequest(new { error = "Only HLR and LLR proposals have an upward allocation." });
            var baselineId = await BuildScope.EffectiveBaselineAsync(db, projectId, releaseId, ct);
            if (baselineId is null) return Results.Ok(Array.Empty<object>());
            var source = from member in db.BaselineRequirements.AsNoTracking().Where(x => x.BaselineId == baselineId)
                         join artifact in db.Requirements.AsNoTracking().Where(x => x.ProjectId == projectId && x.Level == parentLevel) on member.ArtifactId equals artifact.Id
                         join revision in db.RequirementRevisions.AsNoTracking().Where(x => x.State == RequirementRevisionState.Active) on member.RevisionId equals revision.Id
                         select new { revisionId = revision.Id, artifact.BaseNumber, revision.Revision, revision.Statement };
            var selectedIds = (selected ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(x => Guid.TryParse(x, out var id) ? id : Guid.Empty).Where(x => x != Guid.Empty).ToList();
            if (!string.IsNullOrWhiteSpace(search))
            {
                var term = search.Trim().ToLowerInvariant();
                source = source.Where(x => selectedIds.Contains(x.revisionId) || x.BaseNumber.ToLower().Contains(term) || x.Statement.ToLower().Contains(term));
            }
            else if (selectedIds.Count > 0) source = source.Where(x => selectedIds.Contains(x.revisionId));
            var rows = await source.OrderBy(x => x.BaseNumber).Take(Math.Clamp(Math.Max(limit ?? 12, selectedIds.Count), 1, 50)).ToListAsync(ct);
            return Results.Ok(rows.Select(x => new { x.revisionId, displayNumber = $"{x.BaseNumber}.{x.Revision:D2}", level = parentLevel.ToString(), x.Statement }));
        });

        // What the traceability graph says a proposed change touches.
        //
        // A change request already asks its author to close five impact decisions — trace, verification,
        // documents, baselines, collaboration — and asked them from memory. The links needed to answer two of
        // those are recorded: which requirements derive from this one, and which procedures verify it. They were
        // reachable from the requirements explorer and nowhere near the person actually deciding.
        //
        // This informs the decision and does not make it. Nothing here sets a disposition, and a change with
        // nothing downstream still requires its author to say so — "the tool found no links" and "an engineer
        // confirmed there is no impact" are different claims, and only the second is worth anything in a review.
        // Keyed by base number rather than artifact id, because that is the identity a proposal carries.
        app.MapGet("/api/authoring/impact", async (Guid projectId, string baseNumber, HttpContext http, AeroLinkDbContext db, CancellationToken ct) =>
        {
            if (!await http.HasProjectAccessAsync(db, projectId, ct)) return Results.Forbid();
            var normalized = (baseNumber ?? "").Trim().ToUpperInvariant();
            var artifact = await db.Requirements.AsNoTracking()
                .SingleOrDefaultAsync(x => x.ProjectId == projectId && x.BaseNumber == normalized, ct);
            // An introduced requirement has no history and nothing downstream yet. That is not an error; it is
            // the honest answer, and the caller renders it as "nothing recorded" rather than as a failure.
            if (artifact is null)
                return Results.Ok(new { baseNumber = normalized, known = false, derivedRequirements = Array.Empty<object>(), coveringProcedures = Array.Empty<object>() });

            var current = await db.RequirementRevisions.AsNoTracking()
                .Where(x => x.ArtifactId == artifact.Id).OrderByDescending(x => x.Revision).FirstOrDefaultAsync(ct);
            if (current is null)
                return Results.Ok(new { baseNumber = normalized, known = false, derivedRequirements = Array.Empty<object>(), coveringProcedures = Array.Empty<object>() });

            // Children: requirements that trace *to* this one, so a change here propagates down to them.
            var derived = await (from link in db.RequirementTraces.AsNoTracking().Where(x => x.TargetRevisionId == current.Id)
                                 join revision in db.RequirementRevisions.AsNoTracking() on link.SourceRevisionId equals revision.Id
                                 join related in db.Requirements.AsNoTracking() on revision.ArtifactId equals related.Id
                                 orderby related.BaseNumber
                                 select new
                                 {
                                     related.Id,
                                     displayNumber = related.BaseNumber + "." + (revision.Revision < 10 ? "0" : "") + revision.Revision,
                                     level = related.Level.ToString(),
                                     revision.Statement,
                                     linkType = link.Type.ToString(),
                                 }).ToListAsync(ct);

            var procedures = await VerificationCoverageProjection.ForRequirementRevisionsAsync(
                db, [current.Id], ct);

            return Results.Ok(new
            {
                baseNumber = artifact.BaseNumber,
                known = true,
                displayNumber = artifact.BaseNumber + "." + (current.Revision < 10 ? "0" : "") + current.Revision,
                requirementRevisionId = current.Id,
                derivedRequirements = derived,
                coveringProcedures = procedures.Select(x => new
                {
                    id = x.ProcedureId,
                    revisionId = x.ProcedureRevisionId,
                    x.DisplayNumber,
                    x.Title,
                    x.Level,
                    state = x.ProcedureState,
                    x.IsSuspect,
                    x.CoverageState
                }),
            });
        });

        /// The sections a requirement of a given level can be placed in, for the picker on a proposal.
        app.MapGet("/api/authoring/sections", async (Guid projectId, RequirementLevel level, HttpContext http,
            AeroLinkDbContext db, EnterpriseRequirementsService requirements, CancellationToken ct) =>
        {
            if (!await http.HasProjectAccessAsync(db, projectId, ct)) return Results.Forbid();
            // A Project's requirements documents are built the first time its requirements are synchronized,
            // which is whenever somebody first opens the explorer. An author who reached the change request
            // form before anyone had done that was offered no sections at all — and then refused at submission
            // for not choosing one, because by then something else had built them. Asked for here, so the
            // question and the answer are about the same document.
            await requirements.SynchronizeProjectAsync(projectId, http.UserAccount().UserName, ct);
            // Scoped by level, because a requirement's level fixes which specification it belongs to. Offering
            // every section in the project would let an author file a low-level requirement in the system
            // document, which nothing downstream would accept and nothing here would have refused.
            var rows = await (from node in db.SpecificationNodes.AsNoTracking()
                              join spec in db.RequirementSpecifications.AsNoTracking() on node.SpecificationId equals spec.Id
                              where spec.ProjectId == projectId && spec.Level == level.ToString()
                                 && node.Type == SpecificationNodeType.Section
                              select new { node.Id, node.ParentId, node.Heading, node.Position, specification = spec.DocumentNumber }).ToListAsync(ct);
            // Numbered and ordered here, depth first, so every caller meets the sections in the order the
            // document presents them and nobody has to reconstruct "4.1.1" from a flat list.
            var numbering = SpecificationNumbering.Number(rows.Select(x => (x.Id, x.ParentId, x.Position, x.Heading)));
            var specifications = rows.ToDictionary(x => x.Id, x => x.specification);
            var positions = rows.ToDictionary(x => x.Id, x => x.Position);
            return Results.Ok(numbering.Select(section => new
            {
                section.Id,
                section.ParentId,
                section.Number,
                section.Depth,
                section.Heading,
                Position = positions[section.Id],
                specification = specifications[section.Id],
            }));
        });

        // Detection only: missing authored metadata cannot be reconstructed honestly by a backfill. Returning
        // the exact Draft proposals lets an administrator reopen each through the controlled checkout/check-in
        // path and supply the missing values with attribution, rather than inventing an owner after the fact.
        app.MapGet("/api/authoring/attribute-gaps", async (Guid projectId, HttpContext http,
            AeroLinkDbContext db, CancellationToken ct) =>
        {
            if (!await http.HasProjectAccessAsync(db, projectId, ct)) return Results.Forbid();
            var rows = await (from change in db.RequirementChanges.AsNoTracking()
                              join scr in db.SystemChangeRequests.AsNoTracking() on change.ScrId equals scr.Id
                              where scr.ProjectId == projectId
                              select new
                              {
                                  scr.Id, scrDisplayNumber = scr.BaseNumber + "." +
                                      (scr.Revision < 10 ? "0" : "") + scr.Revision,
                                  scr.Title, scr.AuthorId, scr.State, changeId = change.Id,
                                  requirementDisplayNumber = change.BaseNumber + "." +
                                      (change.Revision < 10 ? "0" : "") + change.Revision,
                                  change.Level, change.AttributesJson
                              }).ToListAsync(ct);
            var gaps = rows.Select(row =>
            {
                var keys = AttributeKeys(row.AttributesJson);
                var missing = new[] { "criticality", "owner" }.Where(key => !keys.Contains(key)).ToArray();
                return new { row.Id, displayNumber = row.scrDisplayNumber, row.Title, row.AuthorId,
                    state = row.State.ToString(), row.changeId, requirement = row.requirementDisplayNumber,
                    level = row.Level.ToString(), missing,
                    reconciliation = row.State == ScrState.Draft ? $"scr:{row.Id}" : "Create a controlled successor revision; approved history is immutable." };
            }).Where(x => x.missing.Length > 0).OrderBy(x => x.displayNumber).ThenBy(x => x.requirement);
            return Results.Ok(gaps);
        });

        app.MapGet("/api/scrs/{id:guid}/download", async (Guid id, string? format, ChangeRequestOutputGenerator generator, CancellationToken ct) =>
        {
            var output = await generator.GenerateAsync(id, format ?? "docx", ct); return output is null ? Results.NotFound() : Results.File(output.Content, output.ContentType, output.FileName);
        });

        app.MapPut("/api/scrs/{id:guid}/draft", (Guid id) => Results.Json(new
        {
            error = "Direct controlled-content updates are retired. Autosave the edit session and use the universal check-in endpoint.",
            code = "universal_check_in_required",
            artifactId = id,
            checkInRoute = "/api/controlled-editing/sessions/{sessionId}/check-in"
        }, statusCode: StatusCodes.Status410Gone));

        app.MapGet("/api/requirement-changes", async (Guid projectId, int page, int pageSize, string? search, AeroLinkDbContext db, CancellationToken ct) =>
        {
            page = Math.Max(1, page == 0 ? 1 : page);
            pageSize = Math.Clamp(pageSize == 0 ? 50 : pageSize, 1, 200);
            var source = db.RequirementChanges.AsNoTracking()
                .Where(x => db.SystemChangeRequests.Any(scr => scr.Id == x.ScrId && scr.ProjectId == projectId));
            if (!string.IsNullOrWhiteSpace(search))
            {
                var term = search.Trim();
                source = source.Where(x => EF.Functions.ILike(x.BaseNumber, $"%{term}%") || EF.Functions.ILike(x.Statement, $"%{term}%"));
            }
            var totalCount = await source.CountAsync(ct);
            var items = await source.OrderBy(x => x.BaseNumber).ThenByDescending(x => x.Revision)
                .Skip((page - 1) * pageSize).Take(pageSize)
                .Select(x => new { x.Id, displayNumber = x.BaseNumber + "." + x.Revision, level = x.Level.ToString(), kind = x.Kind.ToString(), x.Statement, x.VerificationMethod, x.ScrId })
                .ToListAsync(ct);
            return Results.Ok(new { page, pageSize, totalCount, totalPages = (int)Math.Ceiling(totalCount / (double)pageSize), items });
        });

        // Historical discovery endpoints deliberately include every revision and lifecycle state.

        app.MapPost("/api/scrs", async (CreateScrRequest request, HttpContext http, IScrRepository repository, AeroLinkDbContext db, IdentityService identity, CancellationToken ct) =>
        {
            if (!await http.HasProjectRoleAsync(db, identity, request.ProjectId, ct, ProgramRole.Engineer)) return Results.Forbid();
            var closed = await ReleasedBuildRefusalAsync(db, request.TargetReleaseId, ct);
            if (closed is not null) return Results.BadRequest(new { error = closed, code = "release_is_closed" });
            if (string.IsNullOrWhiteSpace(request.Title))
                return Results.BadRequest(new { error = "Title of change request must be filled out before save is available." });
            try
            {
                var baseNumber = await IdentifierAllocator.NextChangeRequestAsync(db, request.Type, ct);
                var scr = new SystemChangeRequest(baseNumber, 0, request.ProjectId, request.TargetReleaseId,
                    request.Title, request.Problem, request.Analysis, request.Solution, http.UserAccount().UserName, DateTimeOffset.UtcNow, request.Type,
                    request.ProblemRich, request.AnalysisRich, request.SolutionRich);
                await repository.AddAsync(scr, ct); await repository.SaveAsync(ct);
                return Results.Created($"/api/scrs/{scr.Id}", ApiMap.ScrDetail(scr));
            }
            catch (DomainException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        app.MapPost("/api/scr-drafts", async (CreateScrDraftRequest request, HttpContext http, IScrRepository repository, AeroLinkDbContext db, IdentityService identity, EnterpriseRequirementsService enterpriseRequirements, CancellationToken ct) =>
        {
            if (!await http.HasProjectRoleAsync(db, identity, request.ProjectId, ct, ProgramRole.Engineer)) return Results.Forbid();
            var closed = await ReleasedBuildRefusalAsync(db, request.TargetReleaseId, ct);
            if (closed is not null) return Results.BadRequest(new { error = closed, code = "release_is_closed" });
            // Reject before synchronization, transaction creation, or identifier allocation: an untouched
            // form is not a controlled record and must not consume the next SCR/SWCR number.
            if (string.IsNullOrWhiteSpace(request.Title))
                return Results.BadRequest(new { error = "Title of change request must be filled out before save is available." });
            await enterpriseRequirements.SynchronizeProjectAsync(request.ProjectId, http.UserAccount().UserName, ct);
            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
            try
            {
                var now = DateTimeOffset.UtcNow;
                var actor = http.UserAccount().UserName;
                var baseNumber = await IdentifierAllocator.NextChangeRequestAsync(db, request.Type, ct);
                var schemas = await db.ArtifactSchemas.Include(x => x.Fields)
                    .Where(x => x.ProjectId == request.ProjectId && x.IsActive)
                    .ToDictionaryAsync(x => x.AppliesTo, StringComparer.OrdinalIgnoreCase, ct);
                var scr = new SystemChangeRequest(baseNumber, 0, request.ProjectId, request.TargetReleaseId,
                    request.Title, request.Problem, request.Analysis, request.Solution, http.UserAccount().UserName, now, request.Type,
                    request.ProblemRich, request.AnalysisRich, request.SolutionRich);
                var nextNumbers = new Dictionary<string, int>();
                foreach (var change in request.RequirementChanges)
                {
                    if (request.Type == ChangeRequestType.System && change.Level != RequirementLevel.System)
                        return Results.BadRequest(new { error = "A System SCR can contain only System requirement changes." });
                    if (request.Type == ChangeRequestType.Software && change.Level == RequirementLevel.System)
                        return Results.BadRequest(new { error = "A Software SWCR can contain only HLR and LLR changes." });
                    if (change.IsDerived && string.IsNullOrWhiteSpace(change.Rationale))
                        return Results.BadRequest(new { error = "Every derived software requirement requires an explicit engineering rationale." });
                    var upstreamError = await UpstreamAllocationRefusalAsync(db, request.ProjectId,
                        request.TargetReleaseId, change.Level, change.IsDerived,
                        change.UpstreamRevisionIds ?? [], false, ct);
                    if (upstreamError is not null) return Results.BadRequest(new { error = upstreamError });
                    string requirementNumber; int revision;
                    if (change.Kind == RequirementChangeKind.Introduce)
                    {
                        var prefix = change.Level switch { RequirementLevel.System => "SYSR", RequirementLevel.HighLevel => "HLR", _ => "LLR" };
                        if (!nextNumbers.TryGetValue(prefix, out var next))
                            next = IdentifierAllocator.Sequence(await IdentifierAllocator.NextRequirementAsync(db, prefix, ct));
                        requirementNumber = IdentifierAllocator.Format(prefix, next);
                        revision = 0;
                        nextNumbers[prefix] = next + 1;
                    }
                    else
                    {
                        var artifact = await db.Requirements.AsNoTracking().SingleOrDefaultAsync(x => x.ProjectId == request.ProjectId && x.BaseNumber == change.BaseNumber.Trim().ToUpper(), ct);
                        if (artifact is null) return Results.BadRequest(new { error = $"Select an existing controlled requirement before proposing a {change.Kind.ToString().ToLowerInvariant()}." });
                        if (artifact.Level != change.Level) return Results.BadRequest(new { error = $"{artifact.BaseNumber} is not a {change.Level} requirement." });
                        requirementNumber = artifact.BaseNumber;
                        revision = await db.RequirementRevisions.Where(x => x.ArtifactId == artifact.Id).MaxAsync(x => x.Revision, ct) + 1;
                    }
                    if (!schemas.TryGetValue(change.Level.ToString(), out var schema))
                        return Results.BadRequest(new { error = $"No active requirement schema is configured for {change.Level}." });
                    var attributes = RequirementAuthoringJson.ValidateAndMergeAttributes(
                        change.AttributesJson, schema, change.Level != RequirementLevel.System && change.IsDerived);
                    var sectionError = await TargetSectionRefusalAsync(db, request.ProjectId, change.Level,
                        change.TargetSectionId, ct);
                    if (sectionError is not null) return Results.BadRequest(new { error = sectionError });
                    scr.AddRequirementChange(actor, requirementNumber, revision, change.Level, change.Kind,
                        change.Statement, change.Rationale, change.VerificationMethod, now, change.RichText, attributes, change.ImpactDispositionJson,
                        change.TargetSectionId, proposedUpstreamRevisionIdsJson: JsonSerializer.Serialize(change.UpstreamRevisionIds ?? []));
                }
                await repository.AddAsync(scr, ct);
                await repository.SaveAsync(ct);
                await transaction.CommitAsync(ct);
                return Results.Created($"/api/scrs/{scr.Id}", ApiMap.ScrDetail(scr));
            }
            catch (DomainException ex) { return Results.BadRequest(new { error = ex.Message }); }
            catch (DbUpdateException) { return Results.Conflict(new { error = "Another author created an artifact at the same instant. No duplicate was saved; submit again to receive the next available numbers." }); }
        });

        app.MapPost("/api/scrs/{id:guid}/requirements", async (Guid id, RequirementChangeRequest request, HttpContext http, IScrRepository repository, AeroLinkDbContext db, CancellationToken ct) =>
        {
            var scr = await repository.GetAsync(id, ct); if (scr is null) return Results.NotFound();
            if (!await http.HasProjectAccessAsync(db, scr.ProjectId, ct)) return Results.Forbid();
            var actor = http.UserAccount();
            if (!CanAdminister(scr, actor)) return Results.Forbid();
            try
            {
                scr.AddRequirementChange(actor.UserName, request.BaseNumber, request.Revision, request.Level, request.Kind,
                    request.Statement, request.Rationale, request.VerificationMethod, DateTimeOffset.UtcNow,
                    impactDispositionJson: RequirementAuthoringJson.PendingImpactDispositions,
                    administratorAuthority: actor.IsAdministrator);
                await repository.SaveAsync(ct); return Results.Ok(ApiMap.ScrDetail(scr));
            }
            catch (DomainException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        app.MapPost("/api/scrs/{id:guid}/submit", async (Guid id, SubmitReviewRequest request, HttpContext http, IScrRepository repository, AeroLinkDbContext db, CancellationToken ct) =>
        {
            var scr = await repository.GetAsync(id, ct); if (scr is null) return Results.NotFound();
            if (request.ExpectedVersion is not null && scr.Version != request.ExpectedVersion) return Results.Conflict(new { error = "This SCR changed after it was opened. Refresh it before submitting.", code = "stale_version" });
            var now=DateTimeOffset.UtcNow;var editSessions=await db.ArtifactEditSessions.Where(x=>x.ArtifactId==id&&x.ArtifactType=="SCR"&&x.IsExclusive&&x.State==EditSessionState.Active).ToListAsync(ct);foreach(var expired in editSessions.Where(x=>x.ExpiresAt<=now))expired.Expire(now);if(db.ChangeTracker.HasChanges())await db.SaveChangesAsync(ct);var activeEdit=editSessions.FirstOrDefault(x=>x.State==EditSessionState.Active);if(activeEdit is not null)return Results.Conflict(new{error=$"Review cannot begin while {activeEdit.UserName} has the Draft checked out.",code="active_edit_session",activeEdit.ExpiresAt});
            try
            {
                var actor = http.UserAccount();
                if (!CanAdminister(scr, actor)) return Results.Forbid();
                foreach (var change in scr.RequirementChanges)
                {
                    var sectionError = await TargetSectionRefusalAsync(db, scr.ProjectId, change.Level,
                        change.TargetSectionId, ct, change.Kind);
                    if (sectionError is not null) return Results.BadRequest(new { error = sectionError });
                    var upstreamError = await UpstreamAllocationRefusalAsync(db, scr.ProjectId,
                        scr.TargetReleaseId, change.Level, RequirementAuthoringJson.IsDerived(change.AttributesJson),
                        ProposedUpstreamRevisionIds(change.ProposedUpstreamRevisionIdsJson), true, ct);
                    if (upstreamError is not null) return Results.BadRequest(new { error = upstreamError });
                }
                var known = await db.UserAccounts.AsNoTracking().Where(x => request.Approvers.Select(a => a.UserId.ToLower()).Contains(x.UserName) && x.State == AccountState.Active).Select(x => new { x.Id, x.UserName, x.DisplayName }).ToListAsync(ct);
                if (known.Count != request.Approvers.Count) return Results.BadRequest(new { error = "Every approver must be an active AeroLink user." });
                var directory = known.ToDictionary(x => x.UserName, StringComparer.OrdinalIgnoreCase);
                // The authority each approver holds is resolved here, where program membership lives, and travels
                // with the selection so the domain can enforce a recorded procedure without reaching for it.
                var authorities = await WorkflowEndpoints.AuthoritiesAsync(db, scr.ProjectId, known.Select(x => x.Id).ToList(), ct);
                var selections = request.Approvers.Select(x =>
                {
                    var account = directory[x.UserId];
                    authorities.TryGetValue(account.Id, out var role);
                    return new ApproverSelection(account.UserName, account.DisplayName, role);
                }).ToList();
                var workflow = await WorkflowEndpoints.ActiveSpecificationAsync(db, scr.ProjectId, scr.Type, ct);
                var cycle = scr.SubmitForReview(actor.UserName, selections, now, request.Mode, workflow,
                    actor.IsAdministrator);
                foreach (var step in cycle.Steps.Where(x => x.State == ApprovalStepState.Active))
                    db.UserNotifications.Add(new(scr.ProjectId, step.ApproverId, "ReviewActivated", $"Review {scr.DisplayNumber}", $"You are now authorized to review {scr.DisplayNumber}: {scr.Title}", $"{(scr.Type == ChangeRequestType.Software ? "swcr" : "scr")}:{scr.Id}", scr.Id, now));
                await repository.SaveAsync(ct); return Results.Ok(ApiMap.ScrDetail(scr));
            }
            catch (DomainException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        // Recovering from a misrouted review. Without this the only way out of a review sent to the wrong approver
        // was for that approver to act, which is exactly what cannot happen when they are the wrong person, on leave,
        // or no longer with the organization. The domain has always supported it; nothing exposed it.

        // Recovering from a misrouted review. Without this the only way out of a review sent to the wrong approver
        // was for that approver to act, which is exactly what cannot happen when they are the wrong person, on leave,
        // or no longer with the organization. The domain has always supported it; nothing exposed it.
        app.MapPost("/api/scrs/{id:guid}/restart-review", async (Guid id, RestartReviewRequest request, HttpContext http, IScrRepository repository, AeroLinkDbContext db, CancellationToken ct) =>
        {
            var scr = await repository.GetAsync(id, ct); if (scr is null) return Results.NotFound();
            if (request.ExpectedVersion is not null && scr.Version != request.ExpectedVersion) return Results.Conflict(new { error = "This SCR changed after it was opened. Refresh it before restarting the review.", code = "stale_version" });
            try
            {
                var actor = http.UserAccount();
                // The domain restricts this to the author; an administrator may also act, matching submission.
                if (!CanAdminister(scr, actor)) return Results.Forbid();
                var now = DateTimeOffset.UtcNow;
                var known = await db.UserAccounts.AsNoTracking().Where(x => request.Approvers.Select(a => a.UserId.ToLower()).Contains(x.UserName) && x.State == AccountState.Active).Select(x => new { x.Id, x.UserName, x.DisplayName }).ToListAsync(ct);
                if (known.Count != request.Approvers.Count) return Results.BadRequest(new { error = "Every corrected approver must be an active AeroLink user." });
                var directory = known.ToDictionary(x => x.UserName, StringComparer.OrdinalIgnoreCase);
                var authorities = await WorkflowEndpoints.AuthoritiesAsync(db, scr.ProjectId,
                    known.Select(x => x.Id).ToList(), ct);
                var corrected = request.Approvers.Select(x =>
                {
                    var account = directory[x.UserId];
                    authorities.TryGetValue(account.Id, out var role);
                    return new ApproverSelection(account.UserName, account.DisplayName, role);
                }).ToList();
                var cycle = scr.CancelAndRestartForWrongApprover(actor.UserName, request.Reason, corrected, now,
                    administratorAuthority: actor.IsAdministrator);
                foreach (var step in cycle.Steps.Where(x => x.State == ApprovalStepState.Active))
                    db.UserNotifications.Add(new(scr.ProjectId, step.ApproverId, "ReviewActivated", $"Review {scr.DisplayNumber}", $"You are now authorized to review {scr.DisplayNumber}: {scr.Title}", $"{(scr.Type == ChangeRequestType.Software ? "swcr" : "scr")}:{scr.Id}", scr.Id, now));
                await repository.SaveAsync(ct); return Results.Ok(ApiMap.ScrDetail(scr));
            }
            catch (DomainException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        app.MapPost("/api/scrs/{id:guid}/approve", async (Guid id, SignatureRequest request, HttpContext http, IScrRepository repository, AeroLinkDbContext db, IdentityService identity, VerificationImpactService verificationImpact, DownstreamImpactService downstreamImpact, CancellationToken ct) =>
        {
            var scr = await repository.GetAsync(id, ct); if (scr is null) return Results.NotFound();
            if (request.ExpectedVersion is not null && scr.Version != request.ExpectedVersion) return Results.Conflict(new { error = "The review advanced after this page was loaded. Refresh before acting.", code = "stale_version" });
            var actor = http.UserAccount(); if (!await identity.ConfirmPasswordAsync(actor.Id, request.Password, ct)) return Results.Json(new { error = "Electronic signature confirmation failed." }, statusCode: 401);
            var programId = await db.Projects.Where(x => x.Id == scr.ProjectId).Join(db.Programs, x => x.ProgramId, x => x.Id, (_, p) => p.Id).SingleAsync(ct);
            if (!await identity.HasRoleAsync(actor, programId, ProgramRole.Approver, DateTimeOffset.UtcNow, ct)) return Results.Forbid();
            try { var now = DateTimeOffset.UtcNow; var snapshotHash = scr.ActiveReviewCycle?.SnapshotHash ?? ""; var activeBefore=scr.ActiveReviewCycle!.Steps.Where(x=>x.State==ApprovalStepState.Active).Select(x=>x.ApproverId).ToHashSet(StringComparer.OrdinalIgnoreCase); scr.ApproveActiveStage(actor.UserName, now); var activated=scr.ActiveReviewCycle?.Steps.Where(x=>x.State==ApprovalStepState.Active&&!activeBefore.Contains(x.ApproverId)).ToList()??[];foreach(var step in activated)db.UserNotifications.Add(new(scr.ProjectId,step.ApproverId,"ReviewActivated",$"Review {scr.DisplayNumber}",$"The prior stage is complete. You are now authorized to review {scr.DisplayNumber}: {scr.Title}",$"{(scr.Type == ChangeRequestType.Software ? "swcr" : "scr")}:{scr.Id}",scr.Id,now)); db.ElectronicSignatures.Add(new(actor.Id, actor.UserName, actor.DisplayName, programId, "SCR", scr.Id, scr.DisplayNumber, "Approve", request.Meaning, snapshotHash, http.Connection.RemoteIpAddress?.ToString() ?? "local", now));
                // Approval is what settles the engineering decision, so verification work is raised here rather than
                // waiting for baseline inclusion. Saved in the same unit of work as the approval itself.
                await verificationImpact.RaiseForApprovedChangeRequestAsync(scr, now, ct);
                await downstreamImpact.RaiseForApprovedChangeRequestAsync(scr, now, ct);
                await repository.SaveAsync(ct); return Results.Ok(ApiMap.ScrDetail(scr)); }
            catch (DomainException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        app.MapPost("/api/scrs/{id:guid}/request-changes", async (Guid id, RequestChangesRequest request, HttpContext http, IScrRepository repository, AeroLinkDbContext db, CancellationToken ct) =>
        {
            var scr = await repository.GetAsync(id, ct); if (scr is null) return Results.NotFound();
            if (request.ExpectedVersion is not null && scr.Version != request.ExpectedVersion) return Results.Conflict(new { error = "The review advanced after this page was loaded. Refresh before acting.", code = "stale_version" });
            try { var now=DateTimeOffset.UtcNow;scr.RequestChanges(http.UserAccount().UserName, request.Reason, now);db.UserNotifications.Add(new(scr.ProjectId,scr.AuthorId,"ReviewChangesRequested",$"Changes requested for {scr.DisplayNumber}",request.Reason,$"{(scr.Type == ChangeRequestType.Software ? "swcr" : "scr")}:{scr.Id}",scr.Id,now)); await repository.SaveAsync(ct); return Results.Ok(ApiMap.ScrDetail(scr)); }
            catch (DomainException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        app.MapPut("/api/impact-dispositions/{id:guid}", async (Guid id, DispositionImpactRequest request, HttpContext http, AeroLinkDbContext db, CancellationToken ct) =>
        {
            var impact = await db.ImpactDispositions.SingleOrDefaultAsync(x => x.Id == id, ct); if (impact is null) return Results.NotFound();
            var campaign = await db.ReleaseCampaigns.AsNoTracking().SingleAsync(x => x.Id == impact.CampaignId, ct);
            if (!await http.HasProjectAccessAsync(db, campaign.ProjectId, ct)) return Results.Forbid();
            if (campaign.State == ReleaseCampaignState.InReview) return Results.Conflict(new { error = "The release package is frozen while approval is in progress.", code = "release_package_frozen" });
            if (campaign.State == ReleaseCampaignState.Released) return Results.BadRequest(new { error = "A released campaign is immutable." });
            try { impact.Disposition(request.State, request.Rationale, http.UserAccount().UserName, DateTimeOffset.UtcNow); await db.SaveChangesAsync(ct); return Results.NoContent(); }
            catch (DomainException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        app.MapGet("/api/signatures", async (Guid? artifactId, HttpContext http, AeroLinkDbContext db, CancellationToken ct) =>
        {
            var actor=http.UserAccount();var query = db.ElectronicSignatures.AsNoTracking().AsQueryable();
            if(!actor.IsAdministrator){var allowed=actor.Programs.Select(x=>x.ProgramId).ToList();query=query.Where(x=>allowed.Contains(x.ProgramId));}
            if (artifactId is not null) query = query.Where(x => x.ArtifactId == artifactId);
            var projected=query.Select(x => new { x.Id, x.ArtifactType, x.ArtifactId, x.ArtifactRevision, x.Action, x.Meaning, x.ContentHash, x.UserName, x.DisplayName, x.SignedAt });
            if(db.Database.IsSqlite()){var rows=await projected.ToListAsync(ct);return Results.Ok(rows.OrderByDescending(x=>x.SignedAt).Take(500));}
            return Results.Ok(await projected.OrderByDescending(x => x.SignedAt).Take(500).ToListAsync(ct));
        });
    }

    private static bool CanAdminister(SystemChangeRequest scr, AuthenticatedUser actor) =>
        actor.IsAdministrator || string.Equals(scr.AuthorId, actor.UserName, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Why a released build takes no new change requests, or null when it will.
    ///
    /// A released build is closed. Its content was fixed when it shipped, and a change request allocated to it
    /// afterwards belongs to nothing: it cannot reach a baseline, cannot be incorporated, and cannot be revised
    /// — it is a record filed against a decision already made. `retarget` has always refused to *move* a change
    /// request onto a released build; nothing stopped one being *created* there, so the product offered an
    /// action whose result was a change request with no future.
    ///
    /// It is also the likely mechanism behind a report of a saved draft that never appeared: created while the
    /// released build was selected, it was allocated to that build, and the list the author then looked at was
    /// filtered to the in-work one. Refusing at the point of creation removes the whole class.
    ///
    /// Checked here rather than in the aggregate because a change request cannot know what its target build has
    /// done since; the same reason `StartNextRevision` is told rather than asked.
    /// </summary>
    private static async Task<string?> ReleasedBuildRefusalAsync(AeroLinkDbContext db, Guid targetReleaseId,
        CancellationToken ct)
    {
        var release = await db.Releases.AsNoTracking().Where(x => x.Id == targetReleaseId)
            .Select(x => new { x.Version, x.IsReleased }).SingleOrDefaultAsync(ct);
        if (release is null) return "The target build does not exist.";
        if (!release.IsReleased) return null;
        return $"{release.Version} has been released and takes no new change requests. Switch to the in-work build and raise it there.";
    }

    private static async Task<string?> TargetSectionRefusalAsync(AeroLinkDbContext db, Guid projectId,
        RequirementLevel level, Guid? targetSectionId, CancellationToken ct,
        RequirementChangeKind? kind = null)
    {
        // A new requirement has to be given a section. The author could previously leave this alone and defer
        // it to whoever assembled the baseline, which sounds harmless and is not: the requirement lands
        // wherever a backfill puts it, and the person who knew where it belonged — the one writing it — has
        // by then moved on. A modification may still be left where it already is, because it already has an
        // answer; a retirement has no section to be in at all.
        //
        // Checked when the change request is submitted, not while it is being written. A draft is somebody's
        // unfinished work and refusing to save it because a later field is empty helps nobody; what must not
        // happen is a reviewer being asked to approve a requirement with no place in the document. `kind` is
        // supplied only by the submit path for that reason.
        if (targetSectionId is null)
        {
            if (kind != RequirementChangeKind.Introduce) return null;
            // Only when there is something to choose. A Project's requirements documents are built the first
            // time its requirements are synchronized, so a Project new enough to have none would otherwise be
            // unable to author its first requirement at all — refused for not picking from an empty list.
            var choices = await (from node in db.SpecificationNodes.AsNoTracking()
                                 join specification in db.RequirementSpecifications.AsNoTracking()
                                     on node.SpecificationId equals specification.Id
                                 where node.Type == SpecificationNodeType.Section &&
                                       specification.ProjectId == projectId && specification.Level == level.ToString()
                                 select node.Id).AnyAsync(ct);
            return choices
                ? $"Choose the {level} requirements document section this new requirement belongs in."
                : null;
        }
        var exists = await (from node in db.SpecificationNodes.AsNoTracking()
                            join specification in db.RequirementSpecifications.AsNoTracking()
                                on node.SpecificationId equals specification.Id
                            where node.Id == targetSectionId && node.Type == SpecificationNodeType.Section &&
                                  specification.ProjectId == projectId && specification.Level == level.ToString()
                            select node.Id).AnyAsync(ct);
        return exists ? null :
            $"The selected {level} specification section is no longer available. Reopen the Draft and choose another section.";
    }

    private static IReadOnlyList<Guid> ProposedUpstreamRevisionIds(string json)
    {
        try { return JsonSerializer.Deserialize<List<Guid>>(json) ?? []; }
        catch (JsonException) { return []; }
    }

    private static async Task<string?> UpstreamAllocationRefusalAsync(AeroLinkDbContext db, Guid projectId,
        Guid releaseId, RequirementLevel childLevel, bool derived, IReadOnlyCollection<Guid> selected,
        bool requireComplete, CancellationToken ct)
    {
        if (childLevel == RequirementLevel.System)
            return selected.Count == 0 ? null : "System requirements cannot carry a software upward allocation.";
        if (derived)
            return selected.Count == 0 ? null : "A derived requirement uses its documented rationale instead of an upstream allocation.";
        if (selected.Count == 0)
            return requireComplete ? $"Allocate the proposed {(childLevel == RequirementLevel.HighLevel ? "HLR" : "LLR")} to at least one current upstream requirement before review." : null;
        if (selected.Any(x => x == Guid.Empty) || selected.Distinct().Count() != selected.Count)
            return "Every proposed upstream allocation must name a distinct controlled revision.";
        if (!await db.Releases.AsNoTracking().AnyAsync(x => x.Id == releaseId && x.ProjectId == projectId, ct))
            return "The selected build does not belong to this Project.";
        var baselineId = await BuildScope.EffectiveBaselineAsync(db, projectId, releaseId, ct);
        if (baselineId is null) return "The selected build has no controlled baseline for upward allocation.";
        var expectedLevel = childLevel == RequirementLevel.HighLevel ? RequirementLevel.System : RequirementLevel.HighLevel;
        var valid = await (from member in db.BaselineRequirements.AsNoTracking().Where(x => x.BaselineId == baselineId && selected.Contains(x.RevisionId))
                           join revision in db.RequirementRevisions.AsNoTracking().Where(x => x.State == RequirementRevisionState.Active) on member.RevisionId equals revision.Id
                           join artifact in db.Requirements.AsNoTracking().Where(x => x.ProjectId == projectId && x.Level == expectedLevel) on member.ArtifactId equals artifact.Id
                           select revision.Id).Distinct().ToListAsync(ct);
        return valid.Count == selected.Count ? null :
            $"Every proposed upstream allocation must be a current {expectedLevel} revision from this Project and build.";
    }

    private static HashSet<string> AttributeKeys(string attributesJson)
    {
        try
        {
            using var document = JsonDocument.Parse(attributesJson);
            return document.RootElement.ValueKind == JsonValueKind.Object
                ? document.RootElement.EnumerateObject().Select(x => x.Name).ToHashSet(StringComparer.OrdinalIgnoreCase)
                : [];
        }
        catch (JsonException) { return []; }
    }
}
