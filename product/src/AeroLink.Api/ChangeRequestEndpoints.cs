using AeroLink.Domain.Baselines;
using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Common;
using AeroLink.Domain.Contracts;
using AeroLink.Domain.Identity;
using AeroLink.Domain.Hierarchy;
using AeroLink.Domain.Releases;
using AeroLink.Domain.Requirements;
using AeroLink.Domain.Traceability;
using AeroLink.Domain.Verification;
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
    /// <summary>
    /// The default ceiling on nodes returned by the build change network. A build carries change requests in
    /// the tens, so this is a runaway guard rather than paging; passing it is a deliberate caller choice and
    /// the response declares whether the cut was applied.
    /// </summary>
    private const int DefaultNetworkNodeCeiling = 500;

    public static void MapChangeRequestEndpoints(this WebApplication app)
    {
        app.MapPost("/api/change-requests/{id:guid}/retarget", async (Guid id, RetargetChangeRequestRequest request, HttpContext http, IChangeRequestRepository repository, AeroLinkDbContext db, IdentityService identity, VerificationImpactService verificationImpact, CancellationToken ct) =>
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
                return Results.Ok(ApiMap.ChangeRequestDetail(scr));
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
        app.MapPost("/api/change-requests/{id:guid}/defer", async (Guid id, DeferChangeRequestRequest request, HttpContext http, IChangeRequestRepository repository, AeroLinkDbContext db, CancellationToken ct) =>
        {
            var scr = await repository.GetAsync(id, ct); if (scr is null) return Results.NotFound();
            if (!await http.HasProjectAccessAsync(db, scr.ProjectId, ct)) return Results.Forbid();
            var actor = http.UserAccount();
            if (!CanAdminister(scr, actor)) return Results.Forbid();
            try
            {
                scr.Defer(actor.UserName, request.Reason ?? "", DateTimeOffset.UtcNow, actor.IsAdministrator);
                await repository.SaveAsync(ct);
                return Results.Ok(ApiMap.ChangeRequestDetail(scr));
            }
            catch (DomainException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        /// Stopping a review that should not be running.
        ///
        /// Scoped to people with a stake in it: the author, anybody named as an approver on the active cycle,
        /// a Program manager, and an administrator. Deliberately not "anyone with access" — that would let
        /// somebody with no part in a change halt a review they have nothing to do with, and a controlled tool
        /// should not make that an accident anybody can have.
        app.MapPost("/api/change-requests/{id:guid}/cancel-review", async (Guid id, CancelReviewRequest request, HttpContext http,
            IChangeRequestRepository repository, AeroLinkDbContext db, IdentityService identity, CancellationToken ct) =>
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
                return Results.Ok(ApiMap.ChangeRequestDetail(scr));
            }
            catch (DomainException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        app.MapPost("/api/change-requests/{id:guid}/reinstate", async (Guid id, ReinstateChangeRequest? request, HttpContext http, IChangeRequestRepository repository, AeroLinkDbContext db, CancellationToken ct) =>
        {
            var scr = await repository.GetAsync(id, ct); if (scr is null) return Results.NotFound();
            if (!await http.HasProjectAccessAsync(db, scr.ProjectId, ct)) return Results.Forbid();
            var actor = http.UserAccount();
            if (!CanAdminister(scr, actor)) return Results.Forbid();
            try
            {
                var now = DateTimeOffset.UtcNow;
                // Reinstating into the build the reader is standing in, rather than the one that shelved it.
                //
                // Deferred work is offered to the successor build, so the build it comes back into is the
                // active one — otherwise reinstating from 1.7 would silently return the change request to 1.6
                // and it would vanish from the list the reader is looking at. Retarget first so both moves are
                // audited and the record names both builds.
                var into = request?.IntoReleaseId;
                if (into is not null && into != scr.TargetReleaseId)
                {
                    var target = await db.Releases.AsNoTracking()
                        .SingleOrDefaultAsync(x => x.Id == into && x.ProjectId == scr.ProjectId, ct);
                    if (target is null) return Results.BadRequest(new { error = "The selected build does not belong to this Project." });
                    if (target.IsReleased) return Results.Conflict(new { error = $"Build {target.Version} is released and read-only.", code = "released_build_read_only" });
                    scr.Retarget(actor.UserName, into.Value, "Reinstated into the active build.", now, actor.IsAdministrator);
                }
                scr.Reinstate(actor.UserName, now, actor.IsAdministrator);
                await repository.SaveAsync(ct);
                return Results.Ok(ApiMap.ChangeRequestDetail(scr));
            }
            catch (DomainException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        app.MapPost("/api/change-requests/{id:guid}/next-revision", async (Guid id, ActorRequest request, HttpContext http, IChangeRequestRepository repository, AeroLinkDbContext db, IProjectLadderPolicyResolver policyResolver, CancellationToken ct) =>
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
                var now = DateTimeOffset.UtcNow;
                var policy = await policyResolver.ResolveAsync(approved.ProjectId, ct);
                var next = approved.StartNextRevision(actor.UserName, now, released, actor.IsAdministrator, policy);
                var reportIds = await db.ProblemReportLinks.AsNoTracking().Where(link =>
                        link.ArtifactType == "ChangeRequest" && link.ArtifactId == approved.Id
                        && link.Relationship == ProblemReportRelationshipPolicy.ProposedCorrectiveAction)
                    .Select(link => link.ProblemReportId).ToListAsync(ct);
                await repository.AddAsync(next, ct);
                await new ProblemReportLinkService(db).LinkChangeRequestAsync(next.Id, next.DisplayNumber, reportIds,
                    actor.UserName, now, ct);
                await repository.SaveAsync(ct);
                return Results.Created($"/api/change-requests/{next.Id}", ApiMap.ChangeRequestDetail(next));
            }
            catch (DomainException ex) { return Results.BadRequest(new { error = ex.Message }); }
            catch (DbUpdateException) { return Results.Conflict(new { error = $"A later revision of {approved.BaseNumber} already exists." }); }
        });

        app.MapGet("/api/change-requests", async (Guid projectId, Guid? releaseId, int? page, int? pageSize, string? search, ChangeRequestState? state, HttpContext http, AeroLinkDbContext db, IChangeRequestRepository repository, CancellationToken ct) =>
        {
            if (!await http.HasProjectAccessAsync(db, projectId, ct)) return Results.Forbid();
            var result = await repository.QueryAsync(new ScrQuery(projectId, page is null or 0 ? 1 : page.Value, pageSize is null or 0 ? 50 : pageSize.Value, search, state, releaseId), ct);
            return Results.Ok(new { result.Page, result.PageSize, result.TotalCount, result.TotalPages, items = result.Items.Select(ApiMap.ChangeRequestSummary) });
        });

        app.MapGet("/api/change-requests/{id:guid}", async (Guid id, HttpContext http,
            IChangeRequestRepository repository, AeroLinkDbContext db, CancellationToken ct) =>
        {
            var scr = await repository.GetAsync(id, ct);
            if (scr is null) return Results.NotFound();
            if (!await http.HasProjectAccessAsync(db, scr.ProjectId, ct)) return Results.Forbid();
            return Results.Ok(ApiMap.ChangeRequestDetail(scr));
        });

        // Phase 2's composed trace is a server-owned read. Resolve the owning Project first, authorize it,
        // and only then ask the projection to materialize connected nodes; this prevents a forbidden root from
        // becoming a side channel for cross-Project graph data.
        app.MapGet("/api/change-requests/{id:guid}/trace", async (Guid id, HttpContext http,
            AeroLinkDbContext db, IProjectLadderPolicyResolver policyResolver, CancellationToken ct) =>
        {
            var projectId = await db.SystemChangeRequests.AsNoTracking()
                .Where(x => x.Id == id).Select(x => (Guid?)x.ProjectId).SingleOrDefaultAsync(ct);
            if (projectId is null) return Results.NotFound();
            if (!await http.HasProjectAccessAsync(db, projectId.Value, ct)) return Results.Forbid();
            var policy = await policyResolver.ResolveAsync(projectId.Value, ct);
            var trace = await ChangeRequestTraceProjection.ForChangeRequestAsync(db, projectId.Value, id, policy, ct);
            return trace is null ? Results.NotFound() : Results.Ok(trace);
        });

        // The build-scoped change network. The rooted trace above answers "what is this change connected to";
        // this answers "what is in this build, and how is it connected". The release is resolved inside the
        // authorized Project so a release identifier cannot pull a network out of a Project the caller cannot see.
        app.MapGet("/api/change-requests/network", async (Guid projectId, Guid releaseId, int? maxNodes,
            HttpContext http, AeroLinkDbContext db, IProjectLadderPolicyResolver policyResolver,
            CancellationToken ct) =>
        {
            if (!await http.HasProjectAccessAsync(db, projectId, ct)) return Results.Forbid();
            var releaseExists = await db.Releases.AsNoTracking()
                .AnyAsync(x => x.Id == releaseId && x.ProjectId == projectId, ct);
            if (!releaseExists) return Results.NotFound();
            var policy = await policyResolver.ResolveAsync(projectId, ct);
            var network = await ChangeRequestTraceProjection.ForBuildAsync(db, projectId, releaseId, policy,
                maxNodes is null or < 1 ? DefaultNetworkNodeCeiling : maxNodes.Value, ct);
            return Results.Ok(network);
        });

        app.MapGet("/api/change-requests/{id:guid}/upstream-candidates", async (Guid id, string? search,
            bool? includeEarlierBuilds, int? limit, HttpContext http, IChangeRequestRepository repository,
            AeroLinkDbContext db, IProjectLadderPolicyResolver policyResolver, CancellationToken ct) =>
        {
            var scr = await repository.GetAsync(id, ct); if (scr is null) return Results.NotFound();
            if (!await http.HasProjectAccessAsync(db, scr.ProjectId, ct)) return Results.Forbid();
            var policy = await policyResolver.ResolveAsync(scr.ProjectId, ct);
            var childLevel = ChangeRequestLevel(scr, policy);
            var parentLevels = policy.ParentLevels(childLevel);
            var derivedPairs = await DerivedEdgesAsync(db, scr, childLevel, ct);
            var derivedReleaseIds = derivedPairs.Select(x => x.BuildId).Distinct().ToArray();
            var derivedBuilds = await db.Releases.AsNoTracking().Where(x => derivedReleaseIds.Contains(x.Id))
                .ToDictionaryAsync(x => x.Id, x => x.Version, ct);
            var derivedEdges = derivedPairs.Select(x => new
            {
                upstreamChangeRequestId = x.UpstreamChangeRequestId,
                upstreamDisplayNumber = x.UpstreamDisplayNumber,
                upstreamBuildId = x.BuildId,
                upstreamBuildVersion = derivedBuilds.GetValueOrDefault(x.BuildId, ""),
                assessmentId = x.AssessmentId,
                assessmentLinkId = x.AssessmentLinkId,
            }).ToArray();
            var upstreamAnswerComplete = parentLevels.Count == 0 || derivedEdges.Length > 0
                || scr.UpstreamLinks.Count > 0 || !string.IsNullOrWhiteSpace(scr.NoUpstreamRationale)
                || (scr.InheritedUpstreamContextJson is not null && scr.UpstreamAnswerAffirmed);
            if (parentLevels.Count == 0)
                return Results.Ok(new { isTopOfLadder = true, upstreamAnswerComplete, candidates = Array.Empty<object>(), derivedEdges });
            var earlier = await EarlierReleaseIdsAsync(db, scr.ProjectId, scr.TargetReleaseId, ct);
            var releases = new[] { scr.TargetReleaseId }
                .Concat(includeEarlierBuilds == true ? earlier : [])
                .Distinct().ToHashSet();
            var candidateQuery = db.SystemChangeRequests.AsNoTracking()
                .Where(x => x.ProjectId == scr.ProjectId && x.Id != scr.Id && releases.Contains(x.TargetReleaseId)
                    && x.State != ChangeRequestState.Withdrawn
                    && (x.TargetReleaseId == scr.TargetReleaseId
                        || x.State == ChangeRequestState.Approved
                        || x.State == ChangeRequestState.SelectedForBaseline)
                    && ((x.Type == ChangeRequestType.System && parentLevels.Contains(RequirementLevel.System))
                        || (x.Type == ChangeRequestType.Interface && parentLevels.Contains(RequirementLevel.Interface))
                        || (x.Type == ChangeRequestType.Software && x.SoftwareLevel != null && parentLevels.Contains(x.SoftwareLevel.Value))));
            if (!string.IsNullOrWhiteSpace(search))
            {
                var term = search.Trim().ToLowerInvariant();
                candidateQuery = candidateQuery.Where(x => EF.Functions.Like(x.BaseNumber.ToLower(), $"%{term}%")
                    || EF.Functions.Like(x.Title.ToLower(), $"%{term}%"));
            }
            var candidates = await candidateQuery.OrderBy(x => x.BaseNumber).ThenByDescending(x => x.Revision)
                .Take(Math.Clamp(limit ?? 25, 1, 100)).ToListAsync(ct);
            var releaseRows = await db.Releases.AsNoTracking().Where(x => releases.Contains(x.Id))
                .ToDictionaryAsync(x => x.Id, x => x.Version, ct);
            return Results.Ok(new
            {
                isTopOfLadder = false,
                upstreamAnswerComplete,
                includeEarlierBuilds = includeEarlierBuilds == true,
                derivedEdges,
                candidates = candidates.Select(x => new
                {
                    x.Id, x.DisplayNumber, x.Title, state = x.State.ToString(), x.TargetReleaseId,
                    build = releaseRows.GetValueOrDefault(x.TargetReleaseId, ""),
                    earlierBuild = x.TargetReleaseId != scr.TargetReleaseId,
                    assessmentDerived = derivedPairs.Any(p => p.UpstreamChangeRequestId == x.Id),
                })
            });
        });

        // A software change request is numbered per level, so the preview needs to know which workspace is
        // asking. Without a level it can only answer for a System change request; the software authoring
        // surfaces always know their own level and send it.
        app.MapGet("/api/authoring/context", async (Guid projectId, ChangeRequestType type,
            RequirementLevel? softwareLevel, HttpContext http, AeroLinkDbContext db, ILadderPolicy ladderPolicy,
            IProjectLadderPolicyResolver policyResolver, CancellationToken ct) =>
        {
            if (!await http.HasProjectAccessAsync(db, projectId, ct)) return Results.Forbid();
            ladderPolicy = await policyResolver.ResolveAsync(projectId, ct);
            if (type == ChangeRequestType.Software && !ladderPolicy.IsChangeRequestScopeValid(type, softwareLevel))
                return Results.BadRequest(new { error = "Say whether this software change request is HLR or LLR before previewing its number." });
            if (type == ChangeRequestType.Interface && !ladderPolicy.IsChangeRequestScopeValid(type, softwareLevel))
                return Results.BadRequest(new { error = "The active project ladder does not configure Interface change control." });
            var prefixes = type switch
            {
                ChangeRequestType.System => new[] { ladderPolicy.RequirementPrefix(RequirementLevel.System) },
                ChangeRequestType.Interface => new[] { ladderPolicy.RequirementPrefix(RequirementLevel.Interface) },
                _ => ladderPolicy.OrderedLevels.Where(level => level is RequirementLevel.HighLevel or RequirementLevel.LowLevel)
                    .Select(ladderPolicy.RequirementPrefix).ToArray(),
            };
            var numbers = new Dictionary<string, string>();
            foreach (var prefix in prefixes) numbers[prefix] = await IdentifierAllocator.PreviewRequirementAsync(db, prefix, ct);
            return Results.Ok(new
            {
                type = type.ToString(),
                changeRequestNumber = await IdentifierAllocator.PreviewChangeRequestAsync(db, type, softwareLevel, ct, ladderPolicy),
                author = new { http.UserAccount().UserName, http.UserAccount().DisplayName },
                requirementNumbers = numbers
            });
        });

        app.MapGet("/api/authoring/requirements", async (Guid projectId, string scope, string? search, int? limit, HttpContext http, AeroLinkDbContext db, IProjectLadderPolicyResolver policyResolver, CancellationToken ct) =>
        {
            if (!await http.HasProjectAccessAsync(db, projectId, ct)) return Results.Forbid();
            var ladderPolicy = await policyResolver.ResolveAsync(projectId, ct);
            var artifacts = db.Requirements.AsNoTracking().Where(x => x.ProjectId == projectId);
            var allowedLevels = scope.Equals("System", StringComparison.OrdinalIgnoreCase)
                ? ladderPolicy.OrderedLevels.Where(x => x == RequirementLevel.System
                    && ladderPolicy.Definition(x).Has(LevelCapabilities.HasChangeControl)).ToArray()
                : ladderPolicy.OrderedLevels.Where(x => x != RequirementLevel.System
                    && ladderPolicy.Definition(x).Has(LevelCapabilities.HasChangeControl)).ToArray();
            artifacts = artifacts.Where(x => allowedLevels.Contains(x.Level));
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
                              select new { artifact.Id, artifact.BaseNumber, level = artifact.Level.ToString(), revisionId = revision.Id, revision.Revision, revision.Statement, revision.Rationale, revision.VerificationMethod, revision.ParentKind, revision.DerivedRationale, revision.ParentRevisionIdsJson, state = revision.State.ToString() })
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
            var currentRevisionIds = rows.Select(x => x.revisionId).ToList();
            var currentAllocations = await db.RequirementTraces.AsNoTracking()
                .Where(x => currentRevisionIds.Contains(x.SourceRevisionId)
                    && (x.Type == RequirementTraceType.AllocatedFrom || x.Type == RequirementTraceType.DerivedFrom))
                .Select(x => new { x.SourceRevisionId, x.TargetRevisionId }).ToListAsync(ct);
            var upstreamByRevision = currentAllocations.GroupBy(x => x.SourceRevisionId)
                .ToDictionary(group => group.Key, group => group.Select(x => x.TargetRevisionId).Distinct().ToArray());
            return Results.Ok(rows.Select(x => new { x.Id, x.BaseNumber, displayNumber = $"{x.BaseNumber}.{x.Revision:D2}", x.level, x.Revision, nextRevision = x.Revision + 1, x.Statement, x.Rationale, x.VerificationMethod, parentKind = x.ParentKind.ToString(), derivedRationale = x.DerivedRationale, x.state,
                currentSectionId = sectionByArtifact.TryGetValue(x.Id, out var sectionId) ? sectionId : (Guid?)null,
                currentUpstreamRevisionIds = x.ParentKind != RequirementParentKind.Unspecified
                    ? ProposedUpstreamRevisionIds(x.ParentRevisionIdsJson).ToArray()
                    : upstreamByRevision.TryGetValue(x.revisionId, out var parents) ? parents : [] }));
        });

        app.MapGet("/api/authoring/upstream-requirements", async (Guid projectId, Guid releaseId,
            RequirementLevel childLevel, string? search, string? selected, int? limit, HttpContext http, AeroLinkDbContext db,
            ILadderPolicy ladderPolicy, IProjectLadderPolicyResolver policyResolver, CancellationToken ct) =>
        {
            if (!await http.HasProjectAccessAsync(db, projectId, ct)) return Results.Forbid();
            ladderPolicy = await policyResolver.ResolveAsync(projectId, ct);
            IReadOnlyList<RequirementLevel> parentLevels;
            try { parentLevels = ladderPolicy.ParentLevels(childLevel); }
            catch (DomainException)
            {
                return Results.BadRequest(new { error = ladderPolicy is ILegacyLadderCompatibilityPolicy
                    ? "Only HLR and LLR proposals have an upward allocation."
                    : $"The configured project ladder does not contain {childLevel}." });
            }
            if (parentLevels.Count == 0)
                return Results.BadRequest(new { error = ladderPolicy is ILegacyLadderCompatibilityPolicy
                    ? "Only HLR and LLR proposals have an upward allocation."
                    : $"The configured {childLevel} level has no allowed upstream parent." });
            var baselineId = await BuildScope.EffectiveBaselineAsync(db, projectId, releaseId, ct);
            if (baselineId is null) return Results.Ok(Array.Empty<object>());
            var source = from member in db.BaselineRequirements.AsNoTracking().Where(x => x.BaselineId == baselineId)
                         join artifact in db.Requirements.AsNoTracking().Where(x => x.ProjectId == projectId && parentLevels.Contains(x.Level)) on member.ArtifactId equals artifact.Id
                         join revision in db.RequirementRevisions.AsNoTracking().Where(x => x.State == RequirementRevisionState.Active) on member.RevisionId equals revision.Id
                         select new { revisionId = revision.Id, artifactId = artifact.Id, artifact.BaseNumber, level = artifact.Level.ToString(), revision.Revision, revision.Statement };
            var selectedIds = (selected ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(x => Guid.TryParse(x, out var id) ? id : Guid.Empty).Where(x => x != Guid.Empty).ToList();
            if (!string.IsNullOrWhiteSpace(search))
            {
                var term = search.Trim().ToLowerInvariant();
                source = source.Where(x => selectedIds.Contains(x.revisionId) || x.BaseNumber.ToLower().Contains(term) || x.Statement.ToLower().Contains(term));
            }
            else if (selectedIds.Count > 0) source = source.Where(x => selectedIds.Contains(x.revisionId));
            var rows = await source.OrderBy(x => x.BaseNumber).Take(Math.Clamp(Math.Max(limit ?? 12, selectedIds.Count), 1, 50)).ToListAsync(ct);
            // A proposal must retain the exact revision it already traces to, even when that parent belongs to
            // an older baseline and has since been superseded. Keep normal search candidates build-scoped and
            // active, but explicitly hydrate already-selected immutable references from their owning Project.
            if (selectedIds.Count > 0)
            {
                var selectedRows = await (from revision in db.RequirementRevisions.AsNoTracking().Where(x => selectedIds.Contains(x.Id))
                                          join artifact in db.Requirements.AsNoTracking().Where(x => x.ProjectId == projectId && parentLevels.Contains(x.Level)) on revision.ArtifactId equals artifact.Id
                                          select new { revisionId = revision.Id, artifactId = artifact.Id, artifact.BaseNumber, level = artifact.Level.ToString(), revision.Revision, revision.Statement })
                    .ToListAsync(ct);
                rows = rows.Concat(selectedRows).DistinctBy(x => x.revisionId).OrderBy(x => x.BaseNumber).ToList();
            }
            return Results.Ok(rows.Select(x => new { x.revisionId, x.artifactId, displayNumber = $"{x.BaseNumber}.{x.Revision:D2}", x.level, x.Statement }));
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
            {
                // The old coveringProcedures field remains only for clients before the neutral artifact seam.
                return Results.Ok(new { baseNumber = normalized, known = false, derivedRequirements = Array.Empty<object>(), coveringArtifacts = Array.Empty<object>(), coveringProcedures = Array.Empty<object>() });
            }

            var current = await db.RequirementRevisions.AsNoTracking()
                .Where(x => x.ArtifactId == artifact.Id).OrderByDescending(x => x.Revision).FirstOrDefaultAsync(ct);
            if (current is null)
            {
                // The old coveringProcedures field remains only for clients before the neutral artifact seam.
                return Results.Ok(new { baseNumber = normalized, known = false, derivedRequirements = Array.Empty<object>(), coveringArtifacts = Array.Empty<object>(), coveringProcedures = Array.Empty<object>() });
            }

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
            var coveringArtifacts = procedures.Select(x => new
            {
                id = x.ProcedureId,
                revisionId = x.ProcedureRevisionId,
                x.DisplayNumber,
                x.Title,
                x.Level,
                state = x.ProcedureState,
                x.IsSuspect,
                x.CoverageState
            }).ToList();

            return Results.Ok(new
            {
                baseNumber = artifact.BaseNumber,
                known = true,
                displayNumber = artifact.BaseNumber + "." + (current.Revision < 10 ? "0" : "") + current.Revision,
                requirementRevisionId = current.Id,
                derivedRequirements = derived,
                coveringArtifacts,
                coveringProcedures = coveringArtifacts, // compatibility alias
            });
        });

        /// The sections a requirement of a given level can be placed in, for the picker on a proposal.
        app.MapGet("/api/authoring/sections", async (Guid projectId, RequirementLevel level, HttpContext http,
            AeroLinkDbContext db, EnterpriseRequirementsService requirements,
            IProjectLadderPolicyResolver policyResolver, CancellationToken ct) =>
        {
            if (!await http.HasProjectAccessAsync(db, projectId, ct)) return Results.Forbid();
            var ladderPolicy = await policyResolver.ResolveAsync(projectId, ct);
            LevelDefinition definition;
            try { definition = ladderPolicy.Definition(level); }
            catch (DomainException ex) { return Results.BadRequest(new { error = ex.Message }); }
            if (!definition.Has(LevelCapabilities.HasRequirementsDocument) || definition.RequirementsCatalogue is null)
                return Results.Ok(Array.Empty<object>());
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
                              where spec.ProjectId == projectId && spec.IsActive
                                 && spec.DocumentNumber == definition.RequirementsCatalogue.SpecificationNumber
                                 && spec.Level == level.ToString()
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
                              join scr in db.SystemChangeRequests.AsNoTracking() on change.ChangeRequestId equals scr.Id
                              where scr.ProjectId == projectId
                              select new
                              {
                                  scr.Id, changeRequestDisplayNumber = scr.BaseNumber + "." +
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
                return new { row.Id, displayNumber = row.changeRequestDisplayNumber, row.Title, row.AuthorId,
                    state = row.State.ToString(), row.changeId, requirement = row.requirementDisplayNumber,
                    level = row.Level.ToString(), missing,
                    reconciliation = row.State == ChangeRequestState.Draft ? $"scr:{row.Id}" : "Create a controlled successor revision; approved history is immutable." };
            }).Where(x => x.missing.Length > 0).OrderBy(x => x.displayNumber).ThenBy(x => x.requirement);
            return Results.Ok(gaps);
        });

        app.MapGet("/api/change-requests/{id:guid}/download", async (Guid id, string? format, ChangeRequestOutputGenerator generator, CancellationToken ct) =>
        {
            var output = await generator.GenerateAsync(id, format ?? "docx", ct); return output is null ? Results.NotFound() : Results.File(output.Content, output.ContentType, output.FileName);
        });

        app.MapPut("/api/change-requests/{id:guid}/draft", (Guid id) => Results.Json(new
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
                .Where(x => db.SystemChangeRequests.Any(scr => scr.Id == x.ChangeRequestId && scr.ProjectId == projectId));
            if (!string.IsNullOrWhiteSpace(search))
            {
                var term = search.Trim();
                source = source.Where(x => EF.Functions.ILike(x.BaseNumber, $"%{term}%") || EF.Functions.ILike(x.Statement, $"%{term}%"));
            }
            var totalCount = await source.CountAsync(ct);
            var items = await source.OrderBy(x => x.BaseNumber).ThenByDescending(x => x.Revision)
                .Skip((page - 1) * pageSize).Take(pageSize)
                .Select(x => new { x.Id, displayNumber = x.BaseNumber + "." + x.Revision, level = x.Level.ToString(), kind = x.Kind.ToString(), x.Statement, x.VerificationMethod, x.ChangeRequestId })
                .ToListAsync(ct);
            return Results.Ok(new { page, pageSize, totalCount, totalPages = (int)Math.Ceiling(totalCount / (double)pageSize), items });
        });

        // Historical discovery endpoints deliberately include every revision and lifecycle state.

        app.MapPost("/api/change-requests", async (CreateChangeRequestRequest request, HttpContext http, IChangeRequestRepository repository, AeroLinkDbContext db, IdentityService identity, ILadderPolicy ladderPolicy, IProjectLadderPolicyResolver policyResolver, ProblemReportLinkService problemReports, CancellationToken ct) =>
        {
            if (!await http.HasProjectRoleAsync(db, identity, request.ProjectId, ct, ProgramRole.Engineer)) return Results.Forbid();
            ladderPolicy = await policyResolver.ResolveAsync(request.ProjectId, ct);
            var closed = await ReleasedBuildRefusalAsync(db, request.TargetReleaseId, ct);
            if (closed is not null) return Results.BadRequest(new { error = closed, code = "release_is_closed" });
            if (string.IsNullOrWhiteSpace(request.Title))
                return Results.BadRequest(new { error = "Title of change request must be filled out before save is available." });
            if (request.Type == ChangeRequestType.Software && !ladderPolicy.IsChangeRequestScopeValid(request.Type, request.SoftwareLevel))
                return Results.BadRequest(new { error = "Choose whether this Software Draft belongs to the HLR or LLR workspace." });
            var problemReportError = await problemReports.ValidateSelectionAsync(request.ProjectId,
                request.TargetReleaseId, request.ProblemReportIds, ct);
            if (problemReportError is not null) return Results.BadRequest(new { error = problemReportError });
            try
            {
                var baseNumber = await IdentifierAllocator.NextChangeRequestAsync(db, request.Type, request.SoftwareLevel, ct, ladderPolicy);
                var scr = new SystemChangeRequest(baseNumber, 0, request.ProjectId, request.TargetReleaseId,
                    request.Title, request.Problem, request.Analysis, request.Solution, http.UserAccount().UserName, DateTimeOffset.UtcNow, request.Type,
                    request.ProblemRich, request.AnalysisRich, request.SolutionRich, request.SoftwareLevel, ladderPolicy);
                await problemReports.LinkChangeRequestAsync(scr.Id, scr.DisplayNumber, request.ProblemReportIds,
                    http.UserAccount().UserName, DateTimeOffset.UtcNow, ct);
                await repository.AddAsync(scr, ct); await repository.SaveAsync(ct);
                return Results.Created($"/api/change-requests/{scr.Id}", ApiMap.ChangeRequestDetail(scr));
            }
            catch (DomainException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        app.MapPost("/api/change-request-drafts", async (CreateChangeRequestDraftRequest request, HttpContext http, IChangeRequestRepository repository, AeroLinkDbContext db, IdentityService identity, ILadderPolicy ladderPolicy, IProjectLadderPolicyResolver policyResolver, EnterpriseRequirementsService enterpriseRequirements, ProblemReportLinkService problemReports, CancellationToken ct) =>
        {
            if (!await http.HasProjectRoleAsync(db, identity, request.ProjectId, ct, ProgramRole.Engineer)) return Results.Forbid();
            ladderPolicy = await policyResolver.ResolveAsync(request.ProjectId, ct);
            var closed = await ReleasedBuildRefusalAsync(db, request.TargetReleaseId, ct);
            if (closed is not null) return Results.BadRequest(new { error = closed, code = "release_is_closed" });
            // Reject before synchronization, transaction creation, or identifier allocation: an untouched
            // form is not a controlled record and must not consume the next SCR/SWCR number.
            if (string.IsNullOrWhiteSpace(request.Title))
                return Results.BadRequest(new { error = "Title of change request must be filled out before save is available." });
            var softwareLevel = request.SoftwareLevel;
            if (request.Type == ChangeRequestType.Software && softwareLevel is null)
            {
                var authoredLevels = request.RequirementChanges.Select(x => x.Level).Distinct().ToArray();
                if (authoredLevels.Length == 0)
                    return Results.BadRequest(new { error = "Choose whether this Software Draft belongs to the HLR or LLR workspace." });
                if (authoredLevels.Length == 1 && ladderPolicy.IsChangeRequestScopeValid(ChangeRequestType.Software, authoredLevels[0]))
                    softwareLevel = authoredLevels[0];
                else if (!authoredLevels.Any(level =>
                {
                    try
                    {
                        _ = ladderPolicy.Definition(level);
                        return ladderPolicy.ParentLevels(level).Count == 0;
                    }
                    catch (Exception ex) when (ex is DomainException or InvalidOperationException or KeyNotFoundException)
                    { return false; }
                }))
                    return Results.BadRequest(new { error = "A Software Draft belongs to one HLR or LLR workspace and cannot mix both levels." });
            }
            var problemReportError = await problemReports.ValidateSelectionAsync(request.ProjectId,
                request.TargetReleaseId, request.ProblemReportIds, ct);
            if (problemReportError is not null) return Results.BadRequest(new { error = problemReportError });
            await enterpriseRequirements.SynchronizeProjectAsync(request.ProjectId, http.UserAccount().UserName, ct);
            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
            try
            {
                var now = DateTimeOffset.UtcNow;
                var actor = http.UserAccount().UserName;
                // The resolved level, not the requested one: this path infers HLR or LLR from the authored
                // changes when the caller did not state it, and the number has to name the same level the
                // record will carry.
                var baseNumber = await IdentifierAllocator.NextChangeRequestAsync(db, request.Type, softwareLevel, ct, ladderPolicy);
                var schemas = await db.ArtifactSchemas.Include(x => x.Fields)
                    .Where(x => x.ProjectId == request.ProjectId && x.IsActive)
                    .ToDictionaryAsync(x => x.AppliesTo, StringComparer.OrdinalIgnoreCase, ct);
                var scr = new SystemChangeRequest(baseNumber, 0, request.ProjectId, request.TargetReleaseId,
                    request.Title, request.Problem, request.Analysis, request.Solution, http.UserAccount().UserName, now, request.Type,
                    request.ProblemRich, request.AnalysisRich, request.SolutionRich, softwareLevel, ladderPolicy);
                var nextNumbers = new Dictionary<string, int>();
                foreach (var change in request.RequirementChanges)
                {
                    if (!ladderPolicy.AcceptsChangeRequest(request.Type, change.Level))
                        return Results.BadRequest(new
                        {
                            error = request.Type == ChangeRequestType.System
                                ? "A System change request can contain only System requirement changes."
                                : "A Software change request can contain only HLR and LLR changes."
                        });
                    var upstreamError = await UpstreamAllocationRefusalAsync(db, ladderPolicy, request.ProjectId,
                        request.TargetReleaseId, change.Level, change.IsDerived,
                        change.UpstreamRevisionIds ?? [], false, change.Rationale, ct, change.Kind);
                    if (upstreamError is not null) return Results.BadRequest(new { error = upstreamError });
                    string requirementNumber; int revision;
                    if (change.Kind == RequirementChangeKind.Introduce)
                    {
                        var prefix = ladderPolicy.RequirementPrefix(change.Level);
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
                    var definition = ladderPolicy.Definition(change.Level);
                    var attributes = definition.Has(LevelCapabilities.HasRequirementsDocument)
                        && definition.RequirementsCatalogue is not null
                        ? schemas.TryGetValue(change.Level.ToString(), out var schema)
                            ? RequirementAuthoringJson.ValidateAndMergeAttributes(
                                change.AttributesJson, schema, ladderPolicy.IsDownstreamTarget(change.Level) && change.IsDerived)
                            : throw new DomainException($"No active requirement schema is configured for {change.Level}.")
                        : "{}";
                    var sectionError = await TargetSectionRefusalAsync(db, request.ProjectId, ladderPolicy, change.Level,
                        change.TargetSectionId, ct);
                    if (sectionError is not null) return Results.BadRequest(new { error = sectionError });
                    scr.AddRequirementChange(actor, requirementNumber, revision, change.Level, change.Kind,
                        change.Statement, change.Rationale, change.VerificationMethod, now, change.RichText, attributes, change.ImpactDispositionJson,
                        change.TargetSectionId, proposedUpstreamRevisionIdsJson: JsonSerializer.Serialize(change.UpstreamRevisionIds ?? []),
                        ladderPolicy: ladderPolicy);
                }
                await repository.AddAsync(scr, ct);
                await problemReports.LinkChangeRequestAsync(scr.Id, scr.DisplayNumber, request.ProblemReportIds, actor, now, ct);
                await repository.SaveAsync(ct);
                await transaction.CommitAsync(ct);
                return Results.Created($"/api/change-requests/{scr.Id}", ApiMap.ChangeRequestDetail(scr));
            }
            catch (DomainException ex) { return Results.BadRequest(new { error = ex.Message }); }
            catch (DbUpdateException) { return Results.Conflict(new { error = "Another author created an artifact at the same instant. No duplicate was saved; submit again to receive the next available numbers." }); }
        });

        app.MapPost("/api/change-requests/{id:guid}/requirements", async (Guid id, RequirementChangeRequest request, HttpContext http, IChangeRequestRepository repository, AeroLinkDbContext db, IProjectLadderPolicyResolver policyResolver, CancellationToken ct) =>
        {
            var scr = await repository.GetAsync(id, ct); if (scr is null) return Results.NotFound();
            if (!await http.HasProjectAccessAsync(db, scr.ProjectId, ct)) return Results.Forbid();
            var actor = http.UserAccount();
            if (!CanAdminister(scr, actor)) return Results.Forbid();
            var ladderPolicy = await policyResolver.ResolveAsync(scr.ProjectId, ct);
            try
            {
                scr.AddRequirementChange(actor.UserName, request.BaseNumber, request.Revision, request.Level, request.Kind,
                    request.Statement, request.Rationale, request.VerificationMethod, DateTimeOffset.UtcNow,
                    impactDispositionJson: RequirementAuthoringJson.PendingImpactDispositions,
                    administratorAuthority: actor.IsAdministrator, ladderPolicy: ladderPolicy);
                await repository.SaveAsync(ct);
                // The author is told who else is writing against this requirement, and never stopped by it.
                return Results.Ok(ApiMap.ChangeRequestDetail(scr, await ArtifactClaims.NoticesAsync(db, scr, ct)));
            }
            catch (DomainException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        // The deferred backlog, which is not a build's work and so is not in a build's list.
        //
        // Everything still on the shelf, however long ago it was put there — not only what the immediately
        // preceding build deferred. Work shelved in 1.4 and never taken up is exactly what somebody planning
        // 1.7 needs to see, and a chain that only looked one build back would have quietly lost it.
        //
        // Scoped to the register it is read from: a reader looking at SRCRs is offered deferred SRCRs, and
        // mixing HLRCRs into that list would offer them work they cannot bring into the view they are in.
        app.MapGet("/api/change-requests/deferred", async (Guid projectId, ChangeRequestType? type,
            RequirementLevel? softwareLevel, HttpContext http, AeroLinkDbContext db, CancellationToken ct) =>
        {
            if (!await http.HasProjectAccessAsync(db, projectId, ct)) return Results.Forbid();
            var source = db.SystemChangeRequests.AsNoTracking()
                .Where(x => x.ProjectId == projectId && x.State == ChangeRequestState.Deferred);
            if (type is not null) source = source.Where(x => x.Type == type);
            if (softwareLevel is not null) source = source.Where(x => x.SoftwareLevel == softwareLevel);

            var items = await source
                .OrderBy(x => x.BaseNumber).ThenByDescending(x => x.Revision)
                .Select(x => new
                {
                    x.Id, x.BaseNumber, x.Revision,
                    displayNumber = x.BaseNumber + "." + (x.Revision < 10 ? "0" : "") + x.Revision,
                    x.Title, x.AuthorId, x.UpdatedAt,
                    type = x.Type.ToString(),
                    softwareLevel = x.SoftwareLevel == null ? null : x.SoftwareLevel.ToString(),
                    // Where it was shelved from, and how far it had got. A reader deciding whether to take work
                    // on wants both: which build put it away, and whether it was written, reviewed or approved.
                    x.OriginReleaseId, shelvedFromReleaseId = x.TargetReleaseId,
                    deferredFromState = x.DeferredFromState == null ? null : x.DeferredFromState.ToString(),
                    requirementCount = x.RequirementChanges.Count,
                })
                .ToListAsync(ct);
            return Results.Ok(new { items });
        });

 // What a rebase would be against, and whether one is offered at all.
        //
        // Two rules live here rather than in the aggregate, because both are facts about a different change
        // request. Rebase is offered only onto an Approved result: a change still in review can be returned,
        // deferred or withdrawn, and a change request baselined on a revision that never comes to exist is
        // worse off than one that waited. And never onto a retirement, because a retired requirement cannot be
        // modified -- there is nothing to re-apply a statement against.
        app.MapGet("/api/change-requests/{id:guid}/requirements/{requirementChangeId:guid}/rebase",
            async (Guid id, Guid requirementChangeId, HttpContext http, IChangeRequestRepository repository,
                AeroLinkDbContext db, CancellationToken ct) =>
        {
            var scr = await repository.GetAsync(id, ct); if (scr is null) return Results.NotFound();
            if (!await http.HasProjectAccessAsync(db, scr.ProjectId, ct)) return Results.Forbid();
            var mine = scr.RequirementChanges.SingleOrDefault(x => x.Id == requirementChangeId);
            if (mine is null) return Results.BadRequest(new { error = "That requirement change is not part of this change request." });

            var holders = (await ArtifactClaims.ContendersAsync(db, scr.ProjectId, [mine.BaseNumber], scr.Id, ct))
                .Where(x => x.Holds).ToList();
            var approved = holders.FirstOrDefault(x => x.State == ChangeRequestState.Approved);
            if (approved is null)
                return Results.Ok(new { available = false, reason = holders.Count == 0
                    ? "Nothing holds this requirement, so there is nothing to rebase onto."
                    : "The change request holding this requirement is still in review. Rebasing onto a result that may still be returned would leave this baselined on a revision that never existed." });

            var winner = await db.RequirementChanges.AsNoTracking()
                .Where(x => x.ChangeRequestId == approved.ChangeRequestId && x.BaseNumber == mine.BaseNumber)
                .OrderByDescending(x => x.Revision).FirstOrDefaultAsync(ct);
            if (winner is null)
                return Results.Ok(new { available = false, reason = "The holding change request no longer changes this requirement." });
            if (winner.Kind == RequirementChangeKind.Retire)
                return Results.Ok(new { available = false, reason = $"{approved.DisplayNumber} retires {mine.BaseNumber}. A retired requirement cannot be modified, so remove it from this change request or contest the retirement." });

            return Results.Ok(new
            {
                available = true,
                onto = new { changeRequestId = approved.ChangeRequestId, approved.DisplayNumber, revision = winner.Revision, statement = winner.Statement },
                // Their own words, and the revision they were written against, so the panel can show both
                // beside the difference rather than making the author remember what they proposed.
                mine = new { mine.Id, mine.BaseNumber, mine.Revision, mine.Statement },
            });
        });

        app.MapPost("/api/change-requests/{id:guid}/requirements/{requirementChangeId:guid}/rebase",
            async (Guid id, Guid requirementChangeId, RebaseRequirementChangeRequest request, HttpContext http,
                IChangeRequestRepository repository, AeroLinkDbContext db, CancellationToken ct) =>
        {
            var scr = await repository.GetAsync(id, ct); if (scr is null) return Results.NotFound();
            if (!await http.HasProjectAccessAsync(db, scr.ProjectId, ct)) return Results.Forbid();
            var actor = http.UserAccount();
            if (!CanAdminister(scr, actor)) return Results.Forbid();
            var mine = scr.RequirementChanges.SingleOrDefault(x => x.Id == requirementChangeId);
            if (mine is null) return Results.BadRequest(new { error = "That requirement change is not part of this change request." });

            var approved = (await ArtifactClaims.ContendersAsync(db, scr.ProjectId, [mine.BaseNumber], scr.Id, ct))
                .Where(x => x.Holds).FirstOrDefault(x => x.State == ChangeRequestState.Approved);
            if (approved is null)
                return Results.BadRequest(new { error = "Rebasing is offered only onto an approved result.", code = "no_approved_result" });

            var winner = await db.RequirementChanges.AsNoTracking()
                .Where(x => x.ChangeRequestId == approved.ChangeRequestId && x.BaseNumber == mine.BaseNumber)
                .OrderByDescending(x => x.Revision).FirstOrDefaultAsync(ct);
            if (winner is null || winner.Kind == RequirementChangeKind.Retire)
                return Results.BadRequest(new { error = "A retired requirement cannot be rebased onto.", code = "retired" });

            try
            {
                scr.RebaseRequirementChange(actor.UserName, requirementChangeId, winner.Revision,
                    request.Statement, approved.DisplayNumber, DateTimeOffset.UtcNow, actor.IsAdministrator);
                await repository.SaveAsync(ct);
                return Results.Ok(ApiMap.ChangeRequestDetail(scr, await ArtifactClaims.NoticesAsync(db, scr, ct)));
            }
            catch (DomainException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        // Taking a change request back, and deleting one nobody ever reviewed.
        //
        // The refusal when its baseline is frozen names the way out rather than leaving the reader stuck: a
        // frozen baseline is the strongest statement this system makes about what a build contains, and it
        // stops being true by somebody deciding so, not as a side effect of an author withdrawing their work.
        app.MapPost("/api/change-requests/{id:guid}/withdraw", async (Guid id, WithdrawChangeRequestRequest request,
            HttpContext http, IChangeRequestRepository repository, AeroLinkDbContext db, CancellationToken ct) =>
        {
            var scr = await repository.GetAsync(id, ct); if (scr is null) return Results.NotFound();
            if (!await http.HasProjectAccessAsync(db, scr.ProjectId, ct)) return Results.Forbid();
            var actor = http.UserAccount();
            if (!CanAdminister(scr, actor)) return Results.Forbid();

            var frozen = await db.CandidateBaselines.AsNoTracking()
                .Where(x => x.Selections.Any(s => s.ChangeRequestId == scr.Id) && x.State != CandidateBaselineState.Draft)
                .Select(x => new { x.DisplayNumber, x.State })
                .FirstOrDefaultAsync(ct);
            if (frozen is not null)
                return Results.BadRequest(new
                {
                    error = frozen.State == CandidateBaselineState.Released
                        ? $"{frozen.DisplayNumber} has been released. What it contains is what the world was told, and cannot be taken back."
                        : $"{frozen.DisplayNumber} is frozen. Reopen it before withdrawing work from it.",
                    code = frozen.State == CandidateBaselineState.Released ? "baseline_released" : "baseline_frozen",
                });

            try
            {
                var now = DateTimeOffset.UtcNow;
                // Selection into a still-open baseline is a plan, not a commitment, so taking the work back
                // takes it out of the plan rather than making the author do that first. Both halves are
                // recorded -- the baseline says it removed the change request, the change request says it was
                // returned -- so nothing about this is silent. The frozen case is refused above precisely
                // because there the selection is a commitment.
                var open = await db.CandidateBaselines
                    .Include(x => x.Selections)
                    .Where(x => x.Selections.Any(s => s.ChangeRequestId == scr.Id))
                    .ToListAsync(ct);
                foreach (var baseline in open) baseline.Remove(scr, actor.UserName, now);

                scr.Withdraw(actor.UserName, request.Reason ?? "", now, actor.IsAdministrator);
                await repository.SaveAsync(ct);
                return Results.Ok(ApiMap.ChangeRequestDetail(scr));
            }
            catch (DomainException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        // Deleting outright, which is only honest for a draft nobody has ever been asked about. Anything that
        // reached a reviewer has signatures, and removing the evidence that an approval happened is worse than
        // the problem it solves.
        app.MapDelete("/api/change-requests/{id:guid}", async (Guid id, HttpContext http,
            IChangeRequestRepository repository, AeroLinkDbContext db, CancellationToken ct) =>
        {
            var scr = await repository.GetAsync(id, ct); if (scr is null) return Results.NotFound();
            if (!await http.HasProjectAccessAsync(db, scr.ProjectId, ct)) return Results.Forbid();
            var actor = http.UserAccount();
            if (!CanAdminister(scr, actor)) return Results.Forbid();
            if (scr.State != ChangeRequestState.Draft || scr.ReviewCycles.Count > 0)
                return Results.BadRequest(new
                {
                    error = "This has been in front of reviewers. Withdraw it instead, so the record of what was decided survives.",
                    code = "withdraw_instead",
                });

            // A Draft can still be the authored upstream answer of another Draft. The restrictive upstream
            // foreign key would otherwise surface as an opaque provider failure, so report the exact
            // same-project dependants and give the author a controlled unlink/replace route first.
            var downstream = await (from link in db.ChangeRequestUpstreamLinks.AsNoTracking()
                                    join child in db.SystemChangeRequests.AsNoTracking()
                                        on link.ChangeRequestId equals child.Id
                                    where link.UpstreamChangeRequestId == scr.Id
                                        && child.ProjectId == scr.ProjectId
                                    select new { child.Id, child.BaseNumber, child.Revision })
                .OrderBy(x => x.BaseNumber).ThenBy(x => x.Revision).ToListAsync(ct);
            if (downstream.Count > 0)
                return Results.Conflict(new
                {
                    error = $"{scr.DisplayNumber} is referenced by {downstream.Count} downstream change request(s). Remove or replace that upstream answer through controlled checkout and check-in before deleting this Draft.",
                    code = "upstream_change_request_in_use",
                    guidance = "Check out each referenced downstream Draft, remove or replace this upstream answer, check it in, then retry deletion.",
                    referencedCount = downstream.Count,
                    referencedDownstreamChangeRequests = downstream.Take(10).Select(x => new
                    {
                        id = x.Id,
                        displayNumber = $"{x.BaseNumber}.{x.Revision:D2}",
                    }).ToArray(),
                });

            // Keep the hard-delete as one provider-side parent operation. Upstream answer history is
            // immutable to ordinary child-row writes; the database permits its removal only as part of
            // cascading an authorized Draft deletion.
            await db.SystemChangeRequests.Where(x => x.Id == scr.Id).ExecuteDeleteAsync(ct);
            return Results.NoContent();
        });

        // The other half of adding one. Its absence is why a change request refused at submission for a
        // contested requirement had no remedy but waiting.
        app.MapDelete("/api/change-requests/{id:guid}/requirements/{requirementChangeId:guid}", async (Guid id,
            Guid requirementChangeId, HttpContext http, IChangeRequestRepository repository, AeroLinkDbContext db,
            CancellationToken ct) =>
        {
            var scr = await repository.GetAsync(id, ct); if (scr is null) return Results.NotFound();
            if (!await http.HasProjectAccessAsync(db, scr.ProjectId, ct)) return Results.Forbid();
            var actor = http.UserAccount();
            if (!CanAdminister(scr, actor)) return Results.Forbid();
            try
            {
                scr.RemoveRequirementChange(actor.UserName, requirementChangeId, DateTimeOffset.UtcNow,
                    actor.IsAdministrator);
                await repository.SaveAsync(ct);
                return Results.Ok(ApiMap.ChangeRequestDetail(scr, await ArtifactClaims.NoticesAsync(db, scr, ct)));
            }
            catch (DomainException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        app.MapPost("/api/change-requests/{id:guid}/submit", async (Guid id, SubmitReviewRequest request, HttpContext http, IChangeRequestRepository repository, AeroLinkDbContext db, IdentityService identity, ILadderPolicy ladderPolicy, IProjectLadderPolicyResolver policyResolver, ProjectVerificationVocabularyService verificationVocabulary, CancellationToken ct) =>
        {
            var scr = await repository.GetAsync(id, ct); if (scr is null) return Results.NotFound();
            if (request.ExpectedVersion is not null && scr.Version != request.ExpectedVersion) return Results.Conflict(new { error = "This change request changed after it was opened. Refresh it before submitting.", code = "stale_version" });
            var now=DateTimeOffset.UtcNow;var editSessions=await db.ArtifactEditSessions.Where(x=>x.ArtifactId==id&&x.ArtifactType=="SCR"&&x.IsExclusive&&x.State==EditSessionState.Active).ToListAsync(ct);foreach(var expired in editSessions.Where(x=>x.ExpiresAt<=now))expired.Expire(now);if(db.ChangeTracker.HasChanges())await db.SaveChangesAsync(ct);var activeEdit=editSessions.FirstOrDefault(x=>x.State==EditSessionState.Active);if(activeEdit is not null)return Results.Conflict(new{error=$"Review cannot begin while {activeEdit.UserName} has the Draft checked out.",code="active_edit_session",activeEdit.ExpiresAt});
            try
            {
                var actor = http.UserAccount();
                if (!CanAdminister(scr, actor)) return Results.Forbid();
                ladderPolicy = await policyResolver.ResolveAsync(scr.ProjectId, ct);
                var traceLevel = ChangeRequestLevel(scr, ladderPolicy);
                var traceEvidence = new ChangeRequestTraceReviewEvidence(
                    ladderPolicy.ParentLevels(traceLevel).Count == 0,
                    await DerivedEdgesAsync(db, scr, traceLevel, ct));
                var traceError = await UpstreamChangeRequestRefusalAsync(db, scr, ladderPolicy, traceEvidence, ct);
                if (traceError is not null) return Results.BadRequest(new { error = traceError });
                // #701: the project's own vocabulary is the submission authority, resolved here where the server
                // already knows which project this change request belongs to. Nothing a client sends can widen
                // what review accepts. A project that carries no persisted vocabulary has the founding one
                // materialized into this unit of work, so it commits only if the submission itself does.
                var verificationPolicy = await verificationVocabulary.ResolveForSubmissionAsync(scr.ProjectId,
                    actor.UserName, http.Connection.RemoteIpAddress?.ToString() ?? "local", now, ct);
                foreach (var change in scr.RequirementChanges)
                {
                    var sectionError = await TargetSectionRefusalAsync(db, scr.ProjectId, ladderPolicy, change.Level,
                        change.TargetSectionId, ct, change.Kind);
                    if (sectionError is not null) return Results.BadRequest(new { error = sectionError });
                    var upstreamError = await UpstreamAllocationRefusalAsync(db, ladderPolicy, scr.ProjectId,
                        scr.TargetReleaseId, change.Level, RequirementAuthoringJson.IsDerived(change.AttributesJson),
                        ProposedUpstreamRevisionIds(change.ProposedUpstreamRevisionIdsJson), true,
                        change.Rationale, ct, change.Kind);
                    if (upstreamError is not null) return Results.BadRequest(new { error = upstreamError });
                }
                var known = await db.UserAccounts.AsNoTracking().Where(x => request.Approvers.Select(a => a.UserId.ToLower()).Contains(x.UserName) && x.State == AccountState.Active).Select(x => new { x.Id, x.UserName, x.DisplayName }).ToListAsync(ct);
                if (known.Count != request.Approvers.Count) return Results.BadRequest(new { error = "Every approver must be an active AeroLink user." });
                var directory = known.ToDictionary(x => x.UserName, StringComparer.OrdinalIgnoreCase);
                var workflow = await WorkflowEndpoints.ActiveSpecificationAsync(db, scr.ProjectId, scr.Type, ct, ladderPolicy);
                var programId = await db.Projects.AsNoTracking().Where(x => x.Id == scr.ProjectId)
                    .Select(x => x.ProgramId).SingleAsync(ct);
                if (workflow is not null && request.Approvers.Count < workflow.Stages.Count)
                    return Results.BadRequest(new
                    {
                        error = $"{workflow.Name} v{workflow.Version} requires {workflow.Stages.Count} approver{(workflow.Stages.Count == 1 ? "" : "s")} minimum (at least {workflow.Stages.Count}), one for each stage: " +
                            string.Join(", ", workflow.Stages.Select(x => x.Name)) + "."
                    });
                // The authority each approver actually uses for their stage is resolved here, where program
                // membership lives, and travels with the selection so the domain can enforce the recorded
                // procedure without reaching for it. A multi-role user signs the stage they hold, not the
                // strongest role they happen to have.
                var selections = new List<ApproverSelection>();
                for (var index = 0; index < request.Approvers.Count; index++)
                {
                    var chosen = request.Approvers[index];
                    var account = directory[chosen.UserId];
                    ProgramRole? role;
                    ProjectAuthorityDecision authorityDecision;
                    if (workflow is null)
                    {
                        // No configured workflow means the legacy single-independent-Approver contract:
                        // every reviewer must hold Approver authority today, with Administrator
                        // substitution exactly as the legacy approval gate allowed it (system
                        // administrator and Approver delegations included). Refusing at selection keeps
                        // an uncompletable review from ever being created.
                        if (!await identity.HasRoleAsync(account.Id, programId, ProgramRole.Approver,
                                DateTimeOffset.UtcNow, ct))
                            return Results.BadRequest(new
                            {
                                error = $"{account.DisplayName} does not hold Approver authority. With no review workflow configured, the reviewer must be an Approver."
                            });
                        var resolved = await WorkflowEndpoints.StageAuthorityWithDecisionAsync(db, scr.ProjectId,
                            account.Id, ProgramRole.Approver, ct);
                        role = resolved.Role;
                        authorityDecision = resolved.Decision;
                    }
                    else if (index < workflow.Stages.Count)
                    {
                        var resolved = await WorkflowEndpoints.StageAuthorityWithDecisionAsync(db, scr.ProjectId,
                            account.Id, workflow.Stages[index], ct);
                        role = resolved.Role;
                        authorityDecision = resolved.Decision;
                    }
                    else
                        // Additional signers are allowed beyond the configured minimum, but they must still
                        // be active participants in this Program. A role is resolved from the server roster;
                        // the client cannot turn an unrelated account into an extra reviewer.
                    {
                        var resolved = (await WorkflowEndpoints.AuthoritiesWithDecisionsAsync(db, scr.ProjectId,
                            [account.Id], ct)).GetValueOrDefault(account.Id);
                        role = resolved.Role;
                        authorityDecision = resolved.Decision;
                    }
                    if (workflow is not null && role is null)
                        return Results.BadRequest(new { error = $"{account.DisplayName} does not hold authority to sign this review." });
                    selections.Add(new ApproverSelection(account.UserName, account.DisplayName, role,
                        authorityDecision.Granted ? authorityDecision.Source : ProjectAuthoritySource.None,
                        authorityDecision.SourceId));
                }
                // Contention is settled here rather than while the author was writing, because until now
                // nothing was in front of reviewers and there was nothing to be second to. Whoever submits
                // first takes the requirement; the second is told which change request has it and on what.
                var contendedNumbers = scr.RequirementChanges
                    .Where(x => x.Kind is RequirementChangeKind.Modify or RequirementChangeKind.Retire)
                    .Select(x => x.BaseNumber).Distinct().ToList();
                var blocking = (await ArtifactClaims.ContendersAsync(db, scr.ProjectId, contendedNumbers, scr.Id, ct))
                    .Where(x => x.Holds).ToList();
                if (blocking.Count > 0)
                    return Results.BadRequest(new { error = ArtifactClaims.Refusal(blocking), code = "requirement_claimed" });

                var cycle = scr.SubmitForReviewWithResolvedTrace(actor.UserName, selections, now, request.Mode, workflow,
                    actor.IsAdministrator, ladderPolicy, verificationPolicy, traceEvidence);
                foreach (var step in cycle.Steps.Where(x => x.State == ApprovalStepState.Active))
                    db.UserNotifications.Add(ReviewNotificationFactory.ForChangeRequest(scr.ProjectId,
                        step.ApproverId, step.StageKind, scr.DisplayNumber, scr.Title,
                        $"{(scr.Type == ChangeRequestType.Software ? "swcr" : "scr")}:{scr.Id}", scr.Id, now));
                await repository.SaveAsync(ct); return Results.Ok(ApiMap.ChangeRequestDetail(scr));
            }
            catch (DomainException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        // Recovering from a misrouted review. Without this the only way out of a review sent to the wrong approver
        // was for that approver to act, which is exactly what cannot happen when they are the wrong person, on leave,
        // or no longer with the organization. The domain has always supported it; nothing exposed it.

        // Recovering from a misrouted review. Without this the only way out of a review sent to the wrong approver
        // was for that approver to act, which is exactly what cannot happen when they are the wrong person, on leave,
        // or no longer with the organization. The domain has always supported it; nothing exposed it.
        app.MapPost("/api/change-requests/{id:guid}/restart-review", async (Guid id, RestartReviewRequest request, HttpContext http, IChangeRequestRepository repository, AeroLinkDbContext db, IdentityService identity, CancellationToken ct) =>
        {
            var scr = await repository.GetAsync(id, ct); if (scr is null) return Results.NotFound();
            if (request.ExpectedVersion is not null && scr.Version != request.ExpectedVersion) return Results.Conflict(new { error = "This change request changed after it was opened. Refresh it before restarting the review.", code = "stale_version" });
            try
            {
                var actor = http.UserAccount();
                // The domain restricts this to the author; an administrator may also act, matching submission.
                if (!CanAdminister(scr, actor)) return Results.Forbid();
                var now = DateTimeOffset.UtcNow;
                var known = await db.UserAccounts.AsNoTracking().Where(x => request.Approvers.Select(a => a.UserId.ToLower()).Contains(x.UserName) && x.State == AccountState.Active).Select(x => new { x.Id, x.UserName, x.DisplayName }).ToListAsync(ct);
                if (known.Count != request.Approvers.Count) return Results.BadRequest(new { error = "Every corrected approver must be an active AeroLink user." });
                var directory = known.ToDictionary(x => x.UserName, StringComparer.OrdinalIgnoreCase);
                // A correction within an already active cycle is still governed by that cycle's frozen
                // workflow. A Draft returned/cancelled and normally submitted above resolves the latest
                // active version, but restart must not reinterpret the review that already began.
                var workflow = await WorkflowEndpoints.HistoricalSpecificationAsync(db, scr.ProjectId,
                    scr.ActiveReviewCycle?.WorkflowId, ct);
                var programId = await db.Projects.AsNoTracking().Where(x => x.Id == scr.ProjectId)
                    .Select(x => x.ProgramId).SingleAsync(ct);
                if (workflow is not null && request.Approvers.Count < workflow.Stages.Count)
                    return Results.BadRequest(new
                    {
                        error = $"{workflow.Name} v{workflow.Version} requires {workflow.Stages.Count} approver{(workflow.Stages.Count == 1 ? "" : "s")} minimum (at least {workflow.Stages.Count}), one for each stage: " +
                            string.Join(", ", workflow.Stages.Select(x => x.Name)) + "."
                    });
                var corrected = new List<ApproverSelection>();
                for (var index = 0; index < request.Approvers.Count; index++)
                {
                    var chosen = request.Approvers[index];
                    var account = directory[chosen.UserId];
                    ProgramRole? role;
                    ProjectAuthorityDecision authorityDecision;
                    if (workflow is null)
                    {
                        if (!await identity.HasRoleAsync(account.Id, programId, ProgramRole.Approver,
                                DateTimeOffset.UtcNow, ct))
                            return Results.BadRequest(new
                            {
                                error = $"{account.DisplayName} does not hold Approver authority. With no review workflow configured, the reviewer must be an Approver."
                            });
                        var resolved = await WorkflowEndpoints.StageAuthorityWithDecisionAsync(db, scr.ProjectId,
                            account.Id, ProgramRole.Approver, ct);
                        role = resolved.Role;
                        authorityDecision = resolved.Decision;
                    }
                    else if (index < workflow.Stages.Count)
                    {
                        var resolved = await WorkflowEndpoints.StageAuthorityWithDecisionAsync(db, scr.ProjectId,
                            account.Id, workflow.Stages[index], ct);
                        role = resolved.Role;
                        authorityDecision = resolved.Decision;
                    }
                    else
                    {
                        var resolved = (await WorkflowEndpoints.AuthoritiesWithDecisionsAsync(db, scr.ProjectId,
                            [account.Id], ct)).GetValueOrDefault(account.Id);
                        role = resolved.Role;
                        authorityDecision = resolved.Decision;
                    }
                    if (workflow is not null && role is null)
                        return Results.BadRequest(new { error = $"{account.DisplayName} does not hold authority to sign this review." });
                    corrected.Add(new ApproverSelection(account.UserName, account.DisplayName, role,
                        authorityDecision.Granted ? authorityDecision.Source : ProjectAuthoritySource.None,
                        authorityDecision.SourceId));
                }
                var cycle = scr.CancelAndRestartForWrongApprover(actor.UserName, request.Reason, corrected, now,
                    workflow: workflow, administratorAuthority: actor.IsAdministrator);
                foreach (var step in cycle.Steps.Where(x => x.State == ApprovalStepState.Active))
                    db.UserNotifications.Add(ReviewNotificationFactory.ForChangeRequest(scr.ProjectId,
                        step.ApproverId, step.StageKind, scr.DisplayNumber, scr.Title,
                        $"{(scr.Type == ChangeRequestType.Software ? "swcr" : "scr")}:{scr.Id}", scr.Id, now));
                await repository.SaveAsync(ct); return Results.Ok(ApiMap.ChangeRequestDetail(scr));
            }
            catch (DomainException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        // Reviewer comments are their own resource rather than a field on the change request, because who
        // may read them is not who may read the package: a reviewer still deciding is deliberately shown
        // less than the author is. Folding them into ChangeRequestDetail would make all eight of its callers
        // responsible for a rule that belongs in one place.
        app.MapGet("/api/change-requests/{id:guid}/review-comments", async (Guid id, HttpContext http,
            IChangeRequestRepository repository, CancellationToken ct) =>
        {
            var scr = await repository.GetAsync(id, ct); if (scr is null) return Results.NotFound();
            var viewer = http.UserAccount().UserName;
            var cycles = scr.ReviewCycles.OrderByDescending(x => x.Sequence)
                .Select(cycle => new
                {
                    cycle.Id,
                    cycle.Sequence,
                    state = cycle.State.ToString(),
                    comments = cycle.CommentsVisibleTo(viewer)
                        .OrderBy(x => x.CreatedAt)
                        .Select(x => ApiMap.ReviewComment(x, viewer)),
                });
            return Results.Ok(new { cycles });
        });

        app.MapPost("/api/change-requests/{id:guid}/review-comments", async (Guid id, ReviewCommentRequest request,
            HttpContext http, IChangeRequestRepository repository, AeroLinkDbContext db, CancellationToken ct) =>
        {
            var scr = await repository.GetAsync(id, ct); if (scr is null) return Results.NotFound();
            if (!Enum.TryParse<ReviewCommentAnchor>(request.Anchor, ignoreCase: true, out var anchor))
                return Results.BadRequest(new { error = "A comment must be anchored to the change case or a requirement revision." });
            try
            {
                var actor = http.UserAccount().UserName;
                var comment = scr.AddReviewComment(actor, anchor, request.RequirementChangeId, request.Body, DateTimeOffset.UtcNow);
                // Said explicitly rather than left to change detection. Aggregate children carry
                // application-assigned GUIDs, so EF reads a newly discovered one as a row that already
                // exists and issues an UPDATE that matches nothing — the same hazard the append-only
                // children in AeroLinkDbContext.SaveChangesAsync are corrected for. A comment is not
                // append-only, so it cannot be corrected there without breaking revision.
                db.ReviewComments.Add(comment);
                await repository.SaveAsync(ct);
                return Results.Created($"/api/change-requests/{id}/review-comments/{comment.Id}",
                    ApiMap.ReviewComment(comment, actor));
            }
            catch (DomainException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        app.MapPut("/api/change-requests/{id:guid}/review-comments/{commentId:guid}", async (Guid id, Guid commentId,
            ReviseReviewCommentRequest request, HttpContext http, IChangeRequestRepository repository, CancellationToken ct) =>
        {
            var scr = await repository.GetAsync(id, ct); if (scr is null) return Results.NotFound();
            try
            {
                var actor = http.UserAccount().UserName;
                scr.ReviseReviewComment(actor, commentId, request.Body, DateTimeOffset.UtcNow);
                await repository.SaveAsync(ct);
                var comment = scr.ActiveReviewCycle!.Comments.Single(x => x.Id == commentId);
                return Results.Ok(ApiMap.ReviewComment(comment, actor));
            }
            catch (DomainException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        app.MapDelete("/api/change-requests/{id:guid}/review-comments/{commentId:guid}", async (Guid id, Guid commentId,
            HttpContext http, IChangeRequestRepository repository, CancellationToken ct) =>
        {
            var scr = await repository.GetAsync(id, ct); if (scr is null) return Results.NotFound();
            try
            {
                scr.RemoveReviewComment(http.UserAccount().UserName, commentId);
                await repository.SaveAsync(ct);
                return Results.NoContent();
            }
            catch (DomainException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        app.MapPost("/api/change-requests/{id:guid}/approve", async (Guid id, SignatureRequest request, HttpContext http, IChangeRequestRepository repository, AeroLinkDbContext db, IdentityService identity, VerificationImpactService verificationImpact, DownstreamImpactService downstreamImpact, ProblemReportLinkService problemReports, CancellationToken ct) =>
        {
            var scr = await repository.GetAsync(id, ct); if (scr is null) return Results.NotFound();
            if (request.ExpectedVersion is not null && scr.Version != request.ExpectedVersion) return Results.Conflict(new { error = "The review advanced after this page was loaded. Refresh before acting.", code = "stale_version" });
            var actor = http.UserAccount(); if (!await identity.ConfirmPasswordAsync(actor.Id, request.Password, ct)) return Results.Json(new { error = "Electronic signature confirmation failed." }, statusCode: 401);
            var programId = await db.Projects.Where(x => x.Id == scr.ProjectId).Join(db.Programs, x => x.ProgramId, x => x.Id, (_, p) => p.Id).SingleAsync(ct);
            // A configured workflow freezes stage authority on the active step, so no generic Approver gate
            // applies there. A cycle without a recorded workflow is the legacy single-independent-Approver
            // contract: the person signing must still hold Approver authority today, so an ineligible
            // selection made before this rule existed cannot be completed through the live route.
            if (scr.ActiveReviewCycle?.WorkflowId is null &&
                !await identity.HasRoleAsync(actor, programId, ProgramRole.Approver, DateTimeOffset.UtcNow, ct))
                return Results.Forbid();
            var cycle = scr.ActiveReviewCycle;
            if (cycle is null)
                return Results.BadRequest(new { error = "This change request has no active review." });
            var activeStep = cycle.Steps.SingleOrDefault(x => x.State == ApprovalStepState.Active
                && string.Equals(x.ApproverId, actor.UserName, StringComparison.OrdinalIgnoreCase));
            if (activeStep is null)
                return Results.BadRequest(new { error = "Only the active approver can approve this review stage." });
            try
            {
                var now = DateTimeOffset.UtcNow;
                // Capture the exact active step before the domain advances it. The signature is evidence of
                // this frozen obligation, never a re-resolution against today's workflow or roster.
                var snapshotHash = cycle.SnapshotHash;
                var activeBefore = cycle.Steps.Where(x => x.State == ApprovalStepState.Active)
                    .Select(x => x.ApproverId).ToHashSet(StringComparer.OrdinalIgnoreCase);
                scr.ApproveActiveStage(actor.UserName, now, request.Rationale);
                var activated = scr.ActiveReviewCycle?.Steps
                    .Where(x => x.State == ApprovalStepState.Active && !activeBefore.Contains(x.ApproverId)).ToList() ?? [];
                foreach (var step in activated)
                    db.UserNotifications.Add(ReviewNotificationFactory.ForChangeRequest(scr.ProjectId,
                        step.ApproverId, step.StageKind, scr.DisplayNumber, scr.Title,
                        $"{(scr.Type == ChangeRequestType.Software ? "swcr" : "scr")}:{scr.Id}", scr.Id, now,
                        priorStageComplete: true));
                db.ElectronicSignatures.Add(new(actor.Id, actor.UserName, actor.DisplayName, programId,
                    "SCR", scr.Id, scr.DisplayNumber, activeStep.StageKind.ToString(), request.Meaning,
                    snapshotHash, http.Connection.RemoteIpAddress?.ToString() ?? "local", now,
                    authority: activeStep.Authority, reviewStepId: activeStep.Id, reviewCycle: cycle.Sequence,
                    reviewStepPosition: activeStep.Position, rationale: request.Rationale ?? "",
                    authoritySource: activeStep.AuthoritySource?.ToString() ?? "",
                    workflowId: cycle.WorkflowId, workflowVersion: cycle.WorkflowVersion,
                    authoritySourceId: activeStep.AuthoritySourceId));
                // Approval is what settles the engineering decision, so verification work is raised here rather than
                // waiting for baseline inclusion. Saved in the same unit of work as the approval itself.
                await verificationImpact.RaiseForApprovedChangeRequestAsync(scr, now, ct, actor.UserName);
                await downstreamImpact.RaiseForApprovedChangeRequestAsync(scr, now, ct);
                await problemReports.RecordApprovedCorrectiveActionsAsync(scr, actor.UserName, now, ct);
                await repository.SaveAsync(ct);
                return Results.Ok(ApiMap.ChangeRequestDetail(scr));
            }
            catch (DomainException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        app.MapPost("/api/change-requests/{id:guid}/request-changes", async (Guid id, RequestChangesRequest request, HttpContext http, IChangeRequestRepository repository, AeroLinkDbContext db, IdentityService identity, CancellationToken ct) =>
        {
            var scr = await repository.GetAsync(id, ct); if (scr is null) return Results.NotFound();
            if (request.ExpectedVersion is not null && scr.Version != request.ExpectedVersion) return Results.Conflict(new { error = "The review advanced after this page was loaded. Refresh before acting.", code = "stale_version" });
            var actor = http.UserAccount();
            // Returning work is review authority. Under the no-workflow fallback the same Approver contract
            // applies, so an ineligible person cannot exercise it through request-changes either.
            if (scr.ActiveReviewCycle?.WorkflowId is null)
            {
                var programId = await db.Projects.AsNoTracking().Where(x => x.Id == scr.ProjectId)
                    .Select(x => x.ProgramId).SingleAsync(ct);
                if (!await identity.HasRoleAsync(actor, programId, ProgramRole.Approver, DateTimeOffset.UtcNow, ct))
                    return Results.Forbid();
            }
            try { var now=DateTimeOffset.UtcNow;scr.RequestChanges(actor.UserName, request.Reason, now);db.UserNotifications.Add(new(scr.ProjectId,scr.AuthorId,"ReviewChangesRequested",$"Changes requested for {scr.DisplayNumber}",request.Reason,$"{(scr.Type == ChangeRequestType.Software ? "swcr" : "scr")}:{scr.Id}",scr.Id,now)); await repository.SaveAsync(ct); return Results.Ok(ApiMap.ChangeRequestDetail(scr)); }
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
            var signatures = db.Database.IsSqlite()
                ? (await query.ToListAsync(ct)).OrderByDescending(x => x.SignedAt).Take(500).ToList()
                : await query.OrderByDescending(x => x.SignedAt).Take(500).ToListAsync(ct);
            var migration = await SignatureMigrationProjector.ForAsync(db, signatures.Select(x => x.Id), ct);
            return Results.Ok(signatures.Select(x =>
            {
                var status = migration.GetValueOrDefault(x.Id) ?? SignatureMigrationProjection.Current;
                return new
                {
                    x.Id, x.ArtifactType, x.ArtifactId, x.ArtifactRevision, x.Action, x.Authority,
                    x.AuthoritySource, x.AuthoritySourceId, x.WorkflowId, x.WorkflowVersion,
                    x.ReviewStepId, x.ReviewCycle, x.ReviewStepPosition, x.Rationale,
                    isLegacyAuthoritySource = string.IsNullOrWhiteSpace(x.AuthoritySource),
                    x.Meaning, x.ContentHash, x.UserName, x.DisplayName, x.SignedAt,
                    signatureStatus = status.Status,
                    isSuperseded = status.IsSuperseded,
                    supersession = status.Supersession
                };
            }));
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
        ILadderPolicy ladderPolicy, RequirementLevel level, Guid? targetSectionId, CancellationToken ct,
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
        LevelDefinition definition;
        try { definition = ladderPolicy.Definition(level); }
        catch (DomainException) { return $"The configured project ladder does not contain {level}."; }
        if (!definition.Has(LevelCapabilities.HasRequirementsDocument) || definition.RequirementsCatalogue is null)
            return targetSectionId is null
                ? null
                : $"The configured {level} level has no requirements document section to receive a change.";
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
                                       specification.ProjectId == projectId && specification.IsActive
                                       && specification.DocumentNumber == definition.RequirementsCatalogue.SpecificationNumber
                                       && specification.Level == level.ToString()
                                 select node.Id).AnyAsync(ct);
            return choices
                ? $"Choose the {level} requirements document section this new requirement belongs in."
                : null;
        }
        var exists = await (from node in db.SpecificationNodes.AsNoTracking()
                            join specification in db.RequirementSpecifications.AsNoTracking()
                                on node.SpecificationId equals specification.Id
                            where node.Id == targetSectionId && node.Type == SpecificationNodeType.Section &&
                                  specification.ProjectId == projectId && specification.IsActive
                                  && specification.DocumentNumber == definition.RequirementsCatalogue.SpecificationNumber
                                  && specification.Level == level.ToString()
                            select node.Id).AnyAsync(ct);
        return exists ? null :
            $"The selected {level} specification section is no longer available. Reopen the Draft and choose another section.";
    }

    private static IReadOnlyList<Guid> ProposedUpstreamRevisionIds(string json)
    {
        try
        {
            return ExactParentSelectionPolicy.NormalizeIds(
                JsonSerializer.Deserialize<List<Guid>>(string.IsNullOrWhiteSpace(json) ? "[]" : json) ?? [],
                "requirement revision");
        }
        catch (JsonException)
        {
            throw new DomainException("A requirement change carries malformed exact upstream revisions.");
        }
    }

    private static async Task<string?> UpstreamAllocationRefusalAsync(AeroLinkDbContext db, ILadderPolicy ladderPolicy, Guid projectId,
        Guid releaseId, RequirementLevel childLevel, bool derived, IReadOnlyCollection<Guid> selected,
        bool requireComplete, string? derivedRationale, CancellationToken ct, RequirementChangeKind? kind = null)
    {
        if (kind == RequirementChangeKind.Retire)
            return null;
        IReadOnlyList<RequirementLevel> parentLevels;
        try
        {
            // Resolve the level before interpreting an empty relationship list
            // as the configured root exemption. Unknown topology must fail
            // closed rather than becoming an accidental root.
            _ = ladderPolicy.Definition(childLevel);
            parentLevels = ladderPolicy.ParentLevels(childLevel);
        }
        catch (Exception ex) when (ex is DomainException or InvalidOperationException or KeyNotFoundException)
        {
            return $"The configured project ladder cannot resolve the {childLevel} parent topology.";
        }
        if (parentLevels.Count == 0)
            return selected.Count == 0 ? null : ladderPolicy is ILegacyLadderCompatibilityPolicy
                ? "System requirements cannot carry a software upward allocation."
                : $"The configured {childLevel} level has no allowed upstream parent.";
        if (derived)
        {
            // An explicit Derived choice is already a substantive engineering decision, even while the
            // surrounding Draft is allowed to remain incomplete.  Do not let the draft-mode relaxation
            // turn that choice into a blank, unreviewable exception; ordinary Unspecified drafts still
            // remain saveable until review submission.
            if (string.IsNullOrWhiteSpace(derivedRationale))
                return "A derived requirement requires an explicit engineering rationale.";
            return selected.Count == 0 ? null : "A derived requirement uses its documented rationale instead of an upstream allocation.";
        }
        if (selected.Count == 0)
            return requireComplete ? $"Allocate the proposed {ladderPolicy.RequirementPrefix(childLevel)} to at least one current upstream requirement before review." : null;
        if (selected.Any(x => x == Guid.Empty) || selected.Distinct().Count() != selected.Count)
            return "Every proposed upstream allocation must name a distinct controlled revision.";
        if (!await db.Releases.AsNoTracking().AnyAsync(x => x.Id == releaseId && x.ProjectId == projectId, ct))
            return "The selected build does not belong to this Project.";
        var baselineId = await BuildScope.EffectiveBaselineAsync(db, projectId, releaseId, ct);
        if (baselineId is null) return "The selected build has no controlled baseline for upward allocation.";
        var valid = await (from member in db.BaselineRequirements.AsNoTracking().Where(x => x.BaselineId == baselineId && selected.Contains(x.RevisionId))
                           join revision in db.RequirementRevisions.AsNoTracking().Where(x => x.State == RequirementRevisionState.Active) on member.RevisionId equals revision.Id
                           join artifact in db.Requirements.AsNoTracking().Where(x => x.ProjectId == projectId && parentLevels.Contains(x.Level)) on member.ArtifactId equals artifact.Id
                           select revision.Id).Distinct().ToListAsync(ct);
        if (valid.Count == selected.Count) return null;
        if (ladderPolicy is ILegacyLadderCompatibilityPolicy && parentLevels.Count == 1)
            return $"Every proposed upstream allocation must be a current {parentLevels[0]} revision from this Project and build.";
        return "Every proposed upstream allocation must be a current configured parent revision from this Project and build.";
    }

    private static RequirementLevel ChangeRequestLevel(SystemChangeRequest scr, ILadderPolicy policy) =>
        scr.Type switch
        {
            ChangeRequestType.System => RequirementLevel.System,
            ChangeRequestType.Interface => RequirementLevel.Interface,
            ChangeRequestType.Software when scr.SoftwareLevel is { } level => level,
            _ => throw new DomainException("A Software change request must declare its effective ladder level."),
        };

    private static async Task<HashSet<Guid>> EarlierReleaseIdsAsync(AeroLinkDbContext db, Guid projectId,
        Guid releaseId, CancellationToken ct)
    {
        var releases = await db.Releases.AsNoTracking().Where(x => x.ProjectId == projectId)
            .Select(x => new { x.Id, x.PredecessorReleaseId }).ToDictionaryAsync(x => x.Id, ct);
        var result = new HashSet<Guid>(); var cursor = releaseId;
        while (releases.TryGetValue(cursor, out var release) && release.PredecessorReleaseId is { } predecessor
            && result.Add(predecessor)) cursor = predecessor;
        return result;
    }

    private static async Task<IReadOnlyList<DerivedChangeRequestUpstreamEvidence>> DerivedEdgesAsync(
        AeroLinkDbContext db, SystemChangeRequest child, RequirementLevel childLevel, CancellationToken ct) =>
        await (from link in db.DownstreamAssessmentChangeRequestLinks.AsNoTracking()
               join assessment in db.DownstreamChangeAssessments.AsNoTracking()
                   on link.AssessmentId equals assessment.Id
               where link.ChangeRequestId == child.Id
                   && assessment.ProjectId == child.ProjectId
                   && assessment.ReleaseId == child.TargetReleaseId
                   && assessment.TargetLevel == childLevel
                   && assessment.State != DownstreamAssessmentState.Superseded
               select new DerivedChangeRequestUpstreamEvidence(
                   assessment.SourceChangeRequestId, assessment.Id, link.Id, assessment.ReleaseId,
                   assessment.SourceChangeRequestNumber)).ToListAsync(ct);

    private static async Task<string?> UpstreamChangeRequestRefusalAsync(AeroLinkDbContext db,
        SystemChangeRequest child, ILadderPolicy policy, ChangeRequestTraceReviewEvidence evidence, CancellationToken ct)
    {
        var childLevel = ChangeRequestLevel(child, policy);
        var parentLevels = policy.ParentLevels(childLevel);
        if (parentLevels.Count == 0)
            return child.UpstreamLinks.Count == 0 && string.IsNullOrWhiteSpace(child.NoUpstreamRationale)
                ? null : "The top-of-ladder answer is derived and cannot be authored.";
        if (evidence.DerivedUpstreamLinks.Count > 0 && !string.IsNullOrWhiteSpace(child.NoUpstreamRationale))
            return "An assessment-derived upstream edge cannot be combined with a no-upstream answer.";
        var releases = await db.Releases.AsNoTracking().Where(x => x.ProjectId == child.ProjectId)
            .Select(x => new { x.Id, x.PredecessorReleaseId, x.Version }).ToDictionaryAsync(x => x.Id, ct);
        var earlier = new HashSet<Guid>(); var cursor = child.TargetReleaseId;
        while (releases.TryGetValue(cursor, out var release) && release.PredecessorReleaseId is { } predecessor
            && earlier.Add(predecessor)) cursor = predecessor;
        foreach (var derivedEvidence in evidence.DerivedUpstreamLinks)
        {
            var source = await db.SystemChangeRequests.AsNoTracking().SingleOrDefaultAsync(x =>
                x.Id == derivedEvidence.UpstreamChangeRequestId && x.ProjectId == child.ProjectId, ct);
            if (source is null || source.TargetReleaseId != child.TargetReleaseId
                || derivedEvidence.BuildId != child.TargetReleaseId)
                return "An assessment-derived upstream edge no longer matches this Project and build.";
            var sourceLevel = source.Type switch
            {
                ChangeRequestType.System => RequirementLevel.System,
                ChangeRequestType.Interface => RequirementLevel.Interface,
                ChangeRequestType.Software => source.SoftwareLevel,
                _ => null,
            };
            if (sourceLevel is null || !parentLevels.Contains(sourceLevel.Value))
                return "An assessment-derived upstream edge no longer matches the effective direct-parent ladder.";
            if (source.State == ChangeRequestState.Withdrawn)
                return "A withdrawn change request cannot satisfy an assessment-derived upstream dependency.";
            if (!string.Equals(source.DisplayNumber, derivedEvidence.UpstreamDisplayNumber, StringComparison.Ordinal))
                return "An assessment-derived upstream edge carries stale exact change-request identity.";
        }
        var derivedIds = evidence.DerivedUpstreamLinks.Select(x => x.UpstreamChangeRequestId).ToHashSet();
        foreach (var link in child.UpstreamLinks)
        {
            var source = await db.SystemChangeRequests.AsNoTracking().SingleOrDefaultAsync(x =>
                x.Id == link.UpstreamChangeRequestId && x.ProjectId == child.ProjectId, ct);
            if (source is null) return "An authored upstream link no longer points to a change request in this Project.";
            var sourceLevel = source.Type switch
            {
                ChangeRequestType.System => RequirementLevel.System,
                ChangeRequestType.Interface => RequirementLevel.Interface,
                ChangeRequestType.Software => source.SoftwareLevel,
                _ => null,
            };
            if (sourceLevel is null || !parentLevels.Contains(sourceLevel.Value))
                return "An authored upstream link no longer matches the effective direct-parent ladder.";
            if (source.TargetReleaseId != link.UpstreamBuildId
                || !releases.TryGetValue(source.TargetReleaseId, out var sourceRelease)
                || !string.Equals(sourceRelease.Version, link.UpstreamBuildVersion, StringComparison.Ordinal))
                return "An authored upstream link carries a stale upstream build identity; reauthor the link.";
            if (derivedIds.Contains(source.Id))
                return "An authored upstream link duplicates an assessment-derived upstream edge.";
            var crossBuild = source.TargetReleaseId != child.TargetReleaseId;
            if (!crossBuild && source.State == ChangeRequestState.Withdrawn)
                return "A withdrawn change request cannot be an upstream dependency.";
            if (crossBuild && (!earlier.Contains(source.TargetReleaseId)
                || source.State is not (ChangeRequestState.Approved or ChangeRequestState.SelectedForBaseline)
                || string.IsNullOrWhiteSpace(link.Rationale)))
                return "An earlier-build upstream link requires a signed earlier revision and a specific rationale.";
        }
        var links = await db.ChangeRequestUpstreamLinks.AsNoTracking()
            .Join(db.SystemChangeRequests.AsNoTracking().Where(x => x.ProjectId == child.ProjectId),
                x => x.ChangeRequestId, x => x.Id, (x, _) => new { x.ChangeRequestId, x.UpstreamChangeRequestId }).ToListAsync(ct);
        var derived = await (from link in db.DownstreamAssessmentChangeRequestLinks.AsNoTracking()
                             join assessment in db.DownstreamChangeAssessments.AsNoTracking() on link.AssessmentId equals assessment.Id
                             where assessment.State != DownstreamAssessmentState.Superseded
                             select new { link.ChangeRequestId, UpstreamChangeRequestId = assessment.SourceChangeRequestId }).ToListAsync(ct);
        var parents = links.Concat(derived).GroupBy(x => x.ChangeRequestId)
            .ToDictionary(x => x.Key, x => x.Select(v => v.UpstreamChangeRequestId).ToArray());
        foreach (var link in child.UpstreamLinks)
        {
            var seen = new HashSet<Guid>(); var stack = new Stack<Guid>(); stack.Push(link.UpstreamChangeRequestId);
            while (stack.Count != 0)
            {
                var current = stack.Pop();
                if (current == child.Id) return "The authored upstream answer would create a cycle.";
                if (!seen.Add(current) || !parents.TryGetValue(current, out var next)) continue;
                foreach (var parent in next) stack.Push(parent);
            }
        }
        return null;
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
