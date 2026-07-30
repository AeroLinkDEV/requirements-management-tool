using AeroLink.Domain.Common;
using AeroLink.Domain.Identity;
using AeroLink.Domain.Requirements;
using AeroLink.Domain.Verification;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AeroLink.Api;

public sealed record AssignVerificationImpactRequest(string EngineerId);
public sealed record ResolveVerificationImpactRequest(VerificationImpactOutcome Outcome, string Rationale, Guid? ProcedureId,
    TestProcedureChangeAction? ProcedureChangeAction = null, bool PreReleaseEvidenceRequired = false,
    Guid? RetargetedRequirementRevisionId = null);
public sealed record ReopenVerificationImpactRequest(string Rationale);
public sealed record IncludeChangeRequestRequest(Guid ChangeRequestId);
public sealed record SubmitTestChangeReviewRequest(string? Rationale = null);
public sealed record ApproveTestChangeReviewRequest(string Rationale);
public sealed record ReturnTestChangeReviewRequest(string Rationale);

public static class VerificationImpactEndpoints
{
    public static IEndpointRouteBuilder MapAeroLinkVerificationImpactEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/releases/{releaseId:guid}/verification-impact", async (Guid releaseId, bool? outstandingOnly,
            HttpContext http, AeroLinkDbContext db, IdentityService identity, VerificationImpactService service, CancellationToken ct) =>
        {
            var projectId = await db.Releases.AsNoTracking().Where(x => x.Id == releaseId)
                .Select(x => x.ProjectId).SingleOrDefaultAsync(ct);
            if (projectId == Guid.Empty) return Results.NotFound();
            if (!await http.HasProjectAccessAsync(db, projectId, ct)) return Results.Forbid();
            var items = outstandingOnly == true
                ? await service.OutstandingForReleaseAsync(releaseId, ct)
                : await service.ForReleaseAsync(releaseId, ct);
            return Results.Ok(await MapAsync(items, db, ct));
        });

        app.MapGet("/api/releases/{releaseId:guid}/test-change-reviews", async (Guid releaseId,
            HttpContext http, AeroLinkDbContext db, CancellationToken ct) =>
        {
            var projectId = await db.Releases.AsNoTracking().Where(x => x.Id == releaseId)
                .Select(x => x.ProjectId).SingleOrDefaultAsync(ct);
            if (projectId == Guid.Empty) return Results.NotFound();
            if (!await http.HasProjectAccessAsync(db, projectId, ct)) return Results.Forbid();
            // Ordered in memory on purpose: SQLite cannot translate an ORDER BY over a DateTimeOffset and
            // throws, which took this whole endpoint to a 500 and left the workspace looking simply empty.
            var reviews = (await db.TestChangeReviews.AsNoTracking()
                    .Include(x => x.AdditionalSources)
                    .Where(x => x.ReleaseId == releaseId)
                    .ToListAsync(ct))
                .OrderBy(x => x.State).ThenBy(x => x.Discipline).ThenBy(x => x.CreatedAt)
                .ToList();
            var reviewIds = reviews.Select(x => x.Id).ToList();
            var items = await db.VerificationImpactItems.AsNoTracking()
                .Where(x => reviewIds.Contains(x.TestChangeReviewId)).ToListAsync(ct);
            return Results.Ok(reviews.Select(review => new
            {
                review.Id,
                review.ProjectId,
                review.ReleaseId,
                review.ChangeRequestId,
                discipline = review.Discipline.ToString(),
                state = review.State.ToString(),
                review.SourceChangeRequestNumber,
                review.DisplayNumber,
                // Every change request this package answers for, the one it was raised from first. A reader
                // scanning the list needs to see that two changes are being tested together without opening it.
                coveredChangeRequests = new[] { new { id = review.ChangeRequestId, number = review.SourceChangeRequestNumber, originating = true } }
                    .Concat(review.AdditionalSources.OrderBy(x => x.ChangeRequestNumber)
                        .Select(x => new { id = x.ChangeRequestId, number = x.ChangeRequestNumber, originating = false })),
                review.AssignedEngineerId,
                review.SubmittedBy,
                review.SubmittedAt,
                review.ApprovedBy,
                review.ApprovedAt,
                review.ApprovalRationale,
                totalItems = items.Count(x => x.TestChangeReviewId == review.Id),
                resolvedItems = items.Count(x => x.TestChangeReviewId == review.Id && x.State == VerificationImpactState.Resolved),
                preReleaseEvidenceItems = items.Count(x => x.TestChangeReviewId == review.Id && x.PreReleaseEvidenceRequired)
            }));
        });

        app.MapPost("/api/verification-impact/{id:guid}/assign", async (Guid id, AssignVerificationImpactRequest request,
            HttpContext http, AeroLinkDbContext db, IdentityService identity, CancellationToken ct) =>
        {
            var item = await db.VerificationImpactItems.SingleOrDefaultAsync(x => x.Id == id, ct);
            if (item is null) return Results.NotFound();
            if (await db.Releases.AnyAsync(x => x.Id == item.ReleaseId && x.IsReleased, ct))
                return Results.Conflict(new { error = "Released software-build verification records are read-only." });
            if (!await http.HasProjectRoleAsync(db, identity, item.ProjectId, ct, ProgramRole.TestLead))
                return Results.Forbid();
            try
            {
                item.AssignToEngineer(http.UserAccount().UserName, request.EngineerId, DateTimeOffset.UtcNow);
                await db.SaveChangesAsync(ct);
                return Results.Ok((await MapAsync([item], db, ct)).Single());
            }
            catch (DomainException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        app.MapPost("/api/verification-impact/{id:guid}/resolve", async (Guid id, ResolveVerificationImpactRequest request,
            HttpContext http, AeroLinkDbContext db, IdentityService identity, VerificationImpactService service, BuildTestSetService testSets, CancellationToken ct) =>
        {
            var item = await db.VerificationImpactItems.SingleOrDefaultAsync(x => x.Id == id, ct);
            if (item is null) return Results.NotFound();
            if (await db.Releases.AnyAsync(x => x.Id == item.ReleaseId && x.IsReleased, ct))
                return Results.Conflict(new { error = "Released software-build verification records are read-only." });
            if (!await http.HasProjectRoleAsync(db, identity, item.ProjectId, ct,
                    ProgramRole.TestEngineer, ProgramRole.TestLead))
                return Results.Forbid();

            ApprovedProcedureSelection? selectedProcedure = null;
            if (request.Outcome == VerificationImpactOutcome.ProcedureCoverageConfirmed && request.ProcedureId is not null)
                selectedProcedure = await service.FindApprovedProcedureAsync(item.ProjectId, request.ProcedureId.Value, ct);
            if (request.Outcome == VerificationImpactOutcome.ProcedureCoverageConfirmed && selectedProcedure is null)
                return Results.BadRequest(new
                {
                    error = "Coverage can only be confirmed against an approved procedure in this Project."
                });

            // A procedure can only be moved onto a requirement that is actually in this Project and still
            // active. Without this check a stale identifier from a reloaded page would attach verification to
            // a requirement that had itself been retired, which is the fault this decision exists to avoid.
            if (request.Outcome == VerificationImpactOutcome.ProcedureRetargeted)
            {
                if (request.RetargetedRequirementRevisionId is null)
                    return Results.BadRequest(new { error = "Moving a procedure requires the requirement revision it now covers." });
                var reachable = await (from revision in db.RequirementRevisions.AsNoTracking()
                                       join artifact in db.Requirements.AsNoTracking() on revision.ArtifactId equals artifact.Id
                                       where revision.Id == request.RetargetedRequirementRevisionId
                                             && artifact.ProjectId == item.ProjectId
                                             && revision.State == RequirementRevisionState.Active
                                       select revision.Id).AnyAsync(ct);
                if (!reachable)
                    return Results.BadRequest(new
                    {
                        error = "A procedure can only be moved onto an active requirement revision in this Project."
                    });
            }

            try
            {
                var now = DateTimeOffset.UtcNow;
                var actor = http.UserAccount().UserName;
                item.Resolve(actor, request.Outcome, request.Rationale, now,
                    selectedProcedure?.ProcedureId, selectedProcedure?.RevisionId,
                    request.ProcedureChangeAction, request.PreReleaseEvidenceRequired,
                    request.RetargetedRequirementRevisionId);
                db.VerificationImpactDecisionHistory.Add(new VerificationImpactDecisionHistory(
                    item.Id, VerificationImpactHistoryAction.Resolved, item.Outcome,
                    item.ResolvedProcedureId, item.ResolvedProcedureRevisionId,
                    item.ResolutionRationale, actor, now));
                await service.ApplyResolvedCoverageAsync(item, now, ct);
                await service.ApplyRetargetedCoverageAsync(item, now, ct);
                // Asking for evidence before release is saying this build must run that procedure, so it goes
                // into the build's test set — which is what the release gate now measures. Setting only the
                // flag would leave the decision recorded and unenforced, because the gate stopped reading it.
                if (item.PreReleaseEvidenceRequired && item.ResolvedProcedureRevisionId is not null)
                {
                    var discipline = await db.TestChangeReviews.AsNoTracking()
                        .Where(x => x.Id == item.TestChangeReviewId)
                        .Select(x => (TestChangeReviewDiscipline?)x.Discipline).SingleOrDefaultAsync(ct);
                    if (discipline is not null)
                    {
                        var sets = await testSets.EnsureForReleaseAsync(item.ProjectId, item.ReleaseId, ct);
                        sets.SingleOrDefault(x => x.Discipline == discipline)?.Include(actor,
                            item.ResolvedProcedureRevisionId.Value, TestSelectionReason.ChangedRequirement,
                            $"Required before release by {item.SubjectDisplayNumber}.", now);
                    }
                }
                await db.SaveChangesAsync(ct);
                return Results.Ok((await MapAsync([item], db, ct)).Single());
            }
            catch (DomainException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        /// Folding another change request's test work into this package, and taking it back out.
        ///
        /// Whole change requests, because an engineer takes on a change's test work or they do not; splitting
        /// one across two packages would leave "is this change covered?" with a partial answer that neither
        /// package could give. A change already claimed elsewhere is refused by name rather than by a unique
        /// index violation, so the engineer is told which package has it instead of being told to try again.
        app.MapPost("/api/test-change-reviews/{id:guid}/change-requests", async (Guid id, IncludeChangeRequestRequest request,
            HttpContext http, AeroLinkDbContext db, IdentityService identity, CancellationToken ct) =>
        {
            var review = await db.TestChangeReviews.Include(x => x.AdditionalSources).SingleOrDefaultAsync(x => x.Id == id, ct);
            if (review is null) return Results.NotFound();
            if (await db.Releases.AnyAsync(x => x.Id == review.ReleaseId && x.IsReleased, ct))
                return Results.Conflict(new { error = "Released software-build test change requests are read-only." });
            if (!await http.HasProjectRoleAsync(db, identity, review.ProjectId, ct, ProgramRole.TestEngineer, ProgramRole.TestLead))
                return Results.Forbid();

            var change = await db.SystemChangeRequests.AsNoTracking()
                .Where(x => x.Id == request.ChangeRequestId && x.ProjectId == review.ProjectId)
                .Select(x => new { x.Id, x.DisplayNumber, x.TargetReleaseId }).SingleOrDefaultAsync(ct);
            if (change is null) return Results.NotFound(new { error = "That change request is not in this Project." });
            // A package governs one build's test work. Folding in a change allocated to a different build
            // would put its procedures behind the wrong release gate.
            if (change.TargetReleaseId != review.ReleaseId)
                return Results.BadRequest(new { error = $"{change.DisplayNumber} is allocated to a different build." });

            var claimedBy = await db.TestChangeRequestClaims.AsNoTracking()
                .Where(x => x.ChangeRequestId == request.ChangeRequestId)
                .Select(x => x.TestChangeReviewId).FirstOrDefaultAsync(ct);
            if (claimedBy != Guid.Empty && claimedBy != id)
            {
                var holder = await db.TestChangeReviews.AsNoTracking().Where(x => x.Id == claimedBy)
                    .Select(x => x.BaseNumber).SingleOrDefaultAsync(ct);
                return Results.Conflict(new { error = $"{change.DisplayNumber} is already covered by {holder}." });
            }

            try
            {
                review.IncludeChangeRequest(http.UserAccount().UserName, change.Id, change.DisplayNumber, DateTimeOffset.UtcNow);
                await db.SaveChangesAsync(ct);
                return Results.Ok(new { review.Id, review.DisplayNumber, covered = review.CoveredChangeRequestIds });
            }
            catch (DomainException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        app.MapDelete("/api/test-change-reviews/{id:guid}/change-requests/{changeRequestId:guid}", async (Guid id,
            Guid changeRequestId, HttpContext http, AeroLinkDbContext db, IdentityService identity, CancellationToken ct) =>
        {
            var review = await db.TestChangeReviews.Include(x => x.AdditionalSources).SingleOrDefaultAsync(x => x.Id == id, ct);
            if (review is null) return Results.NotFound();
            if (await db.Releases.AnyAsync(x => x.Id == review.ReleaseId && x.IsReleased, ct))
                return Results.Conflict(new { error = "Released software-build test change requests are read-only." });
            if (!await http.HasProjectRoleAsync(db, identity, review.ProjectId, ct, ProgramRole.TestEngineer, ProgramRole.TestLead))
                return Results.Forbid();
            try
            {
                review.ExcludeChangeRequest(changeRequestId, DateTimeOffset.UtcNow);
                await db.SaveChangesAsync(ct);
                return Results.Ok(new { review.Id, review.DisplayNumber, covered = review.CoveredChangeRequestIds });
            }
            catch (DomainException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        app.MapPost("/api/test-change-reviews/{id:guid}/submit", async (Guid id, SubmitTestChangeReviewRequest request,
            HttpContext http, AeroLinkDbContext db, IdentityService identity, CancellationToken ct) =>
        {
            var review = await db.TestChangeReviews.SingleOrDefaultAsync(x => x.Id == id, ct);
            if (review is null) return Results.NotFound();
            if (await db.Releases.AnyAsync(x => x.Id == review.ReleaseId && x.IsReleased, ct))
                return Results.Conflict(new { error = "Released software-build test change reviews are read-only." });
            if (!await http.HasProjectRoleAsync(db, identity, review.ProjectId, ct, ProgramRole.TestEngineer, ProgramRole.TestLead))
                return Results.Forbid();
            try
            {
                var allResolved = await db.VerificationImpactItems
                    .Where(x => x.TestChangeReviewId == id)
                    .AllAsync(x => x.State == VerificationImpactState.Resolved, ct);
                review.Submit(http.UserAccount().UserName, allResolved, DateTimeOffset.UtcNow);
                await db.SaveChangesAsync(ct);
                return Results.Ok(new { review.Id, state = review.State.ToString() });
            }
            catch (DomainException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        app.MapPost("/api/test-change-reviews/{id:guid}/approve", async (Guid id, ApproveTestChangeReviewRequest request,
            HttpContext http, AeroLinkDbContext db, IdentityService identity, CancellationToken ct) =>
        {
            var review = await db.TestChangeReviews.SingleOrDefaultAsync(x => x.Id == id, ct);
            if (review is null) return Results.NotFound();
            if (await db.Releases.AnyAsync(x => x.Id == review.ReleaseId && x.IsReleased, ct))
                return Results.Conflict(new { error = "Released software-build test change reviews are read-only." });
            if (!await http.HasProjectRoleAsync(db, identity, review.ProjectId, ct, ProgramRole.TestLead, ProgramRole.Approver))
                return Results.Forbid();
            try
            {
                review.Approve(http.UserAccount().UserName, request.Rationale, DateTimeOffset.UtcNow);
                await db.SaveChangesAsync(ct);
                return Results.Ok(new { review.Id, state = review.State.ToString() });
            }
            catch (DomainException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        app.MapPost("/api/test-change-reviews/{id:guid}/return", async (Guid id, ReturnTestChangeReviewRequest request,
            HttpContext http, AeroLinkDbContext db, IdentityService identity, CancellationToken ct) =>
        {
            var review = await db.TestChangeReviews.SingleOrDefaultAsync(x => x.Id == id, ct);
            if (review is null) return Results.NotFound();
            if (await db.Releases.AnyAsync(x => x.Id == review.ReleaseId && x.IsReleased, ct))
                return Results.Conflict(new { error = "Released software-build test change reviews are read-only." });
            if (!await http.HasProjectRoleAsync(db, identity, review.ProjectId, ct, ProgramRole.TestLead, ProgramRole.Approver))
                return Results.Forbid();
            try
            {
                review.ReturnToWork(http.UserAccount().UserName, request.Rationale, DateTimeOffset.UtcNow);
                await db.SaveChangesAsync(ct);
                return Results.Ok(new { review.Id, state = review.State.ToString() });
            }
            catch (DomainException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        app.MapPost("/api/verification-impact/{id:guid}/reopen", async (Guid id, ReopenVerificationImpactRequest request,
            HttpContext http, AeroLinkDbContext db, IdentityService identity, VerificationImpactService service, CancellationToken ct) =>
        {
            var item = await db.VerificationImpactItems.SingleOrDefaultAsync(x => x.Id == id, ct);
            if (item is null) return Results.NotFound();
            if (await db.Releases.AnyAsync(x => x.Id == item.ReleaseId && x.IsReleased, ct))
                return Results.Conflict(new { error = "Released software-build verification records are read-only." });
            if (!await http.HasProjectRoleAsync(db, identity, item.ProjectId, ct,
                    ProgramRole.TestEngineer, ProgramRole.TestLead))
                return Results.Forbid();
            try
            {
                var actor = http.UserAccount().UserName;
                var now = DateTimeOffset.UtcNow;
                db.VerificationImpactDecisionHistory.Add(new VerificationImpactDecisionHistory(
                    item.Id, VerificationImpactHistoryAction.Reopened, item.Outcome,
                    item.ResolvedProcedureId, item.ResolvedProcedureRevisionId,
                    request.Rationale, actor, now));
                await service.ReopenResolvedCoverageAsync(item, request.Rationale, now, ct);
                item.Reopen(actor, request.Rationale, now);
                await db.SaveChangesAsync(ct);
                return Results.Ok((await MapAsync([item], db, ct)).Single());
            }
            catch (DomainException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        return app;
    }

    private static async Task<IReadOnlyList<object>> MapAsync(
        IReadOnlyCollection<VerificationImpactItem> items, AeroLinkDbContext db, CancellationToken ct)
    {
        var exactIds = items.Where(x => x.ResolvedProcedureRevisionId is not null)
            .Select(x => x.ResolvedProcedureRevisionId!.Value).Distinct().ToList();

        // Which resolved items still owe the evidence they designated as a prerequisite to releasing.
        //
        // `BlocksBaselineApproval` answers one question — is this decision still outstanding — and a reader
        // took it for the whole answer. An item resolved with "evidence required before release" reported
        // that it blocked nothing, and the workspace queue, which filters on exactly that, stopped showing
        // it. The release could not ship until its evidence arrived, and the one place a verification
        // engineer looks for outstanding work had quietly dropped it.
        //
        // Computed the same way the release readiness gate computes it, against the latest run for the
        // procedure revision the item resolved to, so the queue and the gate cannot disagree.
        var evidenceOwedIds = new HashSet<Guid>();
        var pendingEvidence = items
            .Where(x => x.PreReleaseEvidenceRequired && x.ResolvedProcedureRevisionId is not null)
            .ToList();
        if (pendingEvidence.Count != 0)
        {
            var revisionIds = pendingEvidence.Select(x => x.ResolvedProcedureRevisionId!.Value).Distinct().ToList();
            // Materialized before ordering: SQLite cannot translate an ORDER BY over a DateTimeOffset.
            var runs = (await db.TestExecutions.AsNoTracking()
                    .Where(x => revisionIds.Contains(x.ProcedureRevisionId)).ToListAsync(ct))
                .GroupBy(x => x.ProcedureRevisionId)
                .ToDictionary(group => group.Key,
                    group => group.OrderByDescending(x => x.ExecutedAt).ThenByDescending(x => x.RecordedAt).First().Id);
            var runIds = runs.Values.ToList();
            var evidenced = (await db.TestExecutionEvidence.AsNoTracking()
                .Where(x => runIds.Contains(x.TestExecutionId)).Select(x => x.TestExecutionId).Distinct().ToListAsync(ct))
                .ToHashSet();
            foreach (var item in pendingEvidence)
                if (!runs.TryGetValue(item.ResolvedProcedureRevisionId!.Value, out var runId) || !evidenced.Contains(runId))
                    evidenceOwedIds.Add(item.Id);
        }
        var exactRows = exactIds.Count == 0
            ? []
            : await LoadProcedureRowsAsync(db, exactIds, [], ct);
        var exact = exactRows.ToDictionary(x => x.RevisionId, ToSelection);

        var legacyIds = items.Where(x => x.ResolvedProcedureRevisionId is null && x.ResolvedProcedureId is not null)
            .Select(x => x.ResolvedProcedureId!.Value).Distinct().ToList();
        var legacyRows = legacyIds.Count == 0
            ? []
            : (await LoadProcedureRowsAsync(db, [], legacyIds, ct))
                .Where(x => x.State == TestProcedureState.Approved).ToList();
        var legacy = legacyRows.GroupBy(x => x.ProcedureId).ToDictionary(x => x.Key,
            x => ToSelection(x.OrderByDescending(y => y.Revision).First()));

        var itemIds = items.Select(x => x.Id).ToList();
        var historyRows = await db.VerificationImpactDecisionHistory.AsNoTracking()
            .Where(x => itemIds.Contains(x.VerificationImpactItemId))
            .ToListAsync(ct);
        var history = historyRows.OrderBy(x => x.OccurredAt).ToList();

        return items.Select(x =>
        {
            ApprovedProcedureSelection? procedure = null;
            if (x.ResolvedProcedureRevisionId is not null)
                exact.TryGetValue(x.ResolvedProcedureRevisionId.Value, out procedure);
            else if (x.ResolvedProcedureId is not null)
                legacy.TryGetValue(x.ResolvedProcedureId.Value, out procedure);
            return (object)new
            {
                x.Id,
                x.ReleaseId,
                x.ChangeRequestId,
                x.TestChangeReviewId,
                trigger = x.Trigger.ToString(),
                state = x.State.ToString(),
                x.SubjectDisplayNumber,
                x.DeclaredVerificationMethod,
                x.RequirementChangeId,
                x.RequirementRevisionId,
                x.ProcedureId,
                x.AssignedEngineerId,
                x.AssignedByLeadId,
                x.AssignedAt,
                outcome = x.Outcome?.ToString(),
                procedureChangeAction = x.ProcedureChangeAction?.ToString(),
                x.PreReleaseEvidenceRequired,
                x.ResolvedProcedureId,
                x.ResolvedProcedureRevisionId,
                resolvedProcedure = procedure is null ? null : new
                {
                    id = procedure.ProcedureId,
                    revisionId = procedure.RevisionId,
                    procedure.DisplayNumber,
                    procedure.Title,
                    procedure.Level,
                    procedure.State,
                    configuration = new { x.RequirementRevisionId, procedureRevisionId = procedure.RevisionId }
                },
                x.ResolutionRationale,
                x.ResolvedBy,
                x.ResolvedAt,
                x.RaisedAt,
                x.RetargetedRequirementRevisionId,
                x.BlocksBaselineApproval,
                // Resolved, and still holding the release until its designated evidence is captured.
                awaitsPreReleaseEvidence = evidenceOwedIds.Contains(x.Id),
                // What a reader actually wants to know: is this item holding the build, for any reason.
                holdsRelease = x.BlocksBaselineApproval || evidenceOwedIds.Contains(x.Id),
                decisionHistory = history.Where(h => h.VerificationImpactItemId == x.Id).Select(h => new
                {
                    h.Id,
                    action = h.Action.ToString(),
                    outcome = h.Outcome?.ToString(),
                    h.ProcedureId,
                    h.ProcedureRevisionId,
                    h.Rationale,
                    actor = h.ActorId,
                    h.OccurredAt
                })
            };
        }).ToList();
    }

    private static async Task<List<ProcedureRow>> LoadProcedureRowsAsync(
        AeroLinkDbContext db, IReadOnlyCollection<Guid> revisionIds,
        IReadOnlyCollection<Guid> procedureIds, CancellationToken ct)
    {
        var rows = await (
            from revision in db.TestProcedureRevisions.AsNoTracking()
            join procedure in db.TestProcedures.AsNoTracking() on revision.ProcedureId equals procedure.Id
            where revisionIds.Contains(revision.Id) || procedureIds.Contains(procedure.Id)
            select new
            {
                ProcedureId = procedure.Id,
                RevisionId = revision.Id,
                revision.Revision,
                procedure.BaseNumber,
                procedure.Title,
                procedure.Level,
                revision.State
            }).ToListAsync(ct);
        return rows.Select(x => new ProcedureRow(x.ProcedureId, x.RevisionId, x.Revision, x.BaseNumber,
            x.Title, x.Level.ToString(), x.State)).ToList();
    }

    private static ApprovedProcedureSelection ToSelection(ProcedureRow row) =>
        new(row.ProcedureId, row.RevisionId, row.Revision, $"{row.BaseNumber}.{row.Revision:D2}",
            row.Title, row.Level, row.State.ToString());

    private sealed record ProcedureRow(Guid ProcedureId, Guid RevisionId, int Revision, string BaseNumber,
        string Title, string Level, TestProcedureState State);
}
