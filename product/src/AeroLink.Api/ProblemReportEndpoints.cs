using System.Data;
using System.Data.Common;
using System.Text.Json;
using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Common;
using AeroLink.Domain.Identity;
using AeroLink.Domain.Requirements;
using AeroLink.Domain.Verification;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

public static class ProblemReportEndpoints
{
    public static IEndpointRouteBuilder MapAeroLinkProblemReportEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/problem-reports");
        group.MapGet("", ListAsync);
        group.MapGet("/dashboard", DashboardAsync);
        group.MapGet("/linked/{artifactType}/{artifactId:guid}", LinkedAsync);
        group.MapPost("", CreateAsync);
        group.MapPost("/from-test-execution/{executionId:guid}", CreateFromFailureAsync);
        group.MapGet("/{id:guid}", DetailAsync);
        group.MapGet("/{id:guid}/corrective-action", CorrectiveActionAsync);
        // Details are edited under the universal controlled-editing lease, not here. A second write path to
        // the same fields was the whole defect: it let two people save over each other with nothing but an
        // expected version between them, while every other controlled record took an exclusive lease.
        // See /api/controlled-editing/checkout with artifactType=ProblemReport.
        group.MapPost("/{id:guid}/owner", ReassignAsync);
        group.MapPost("/{id:guid}/target-build", RetargetAsync);
        group.MapPost("/{id:guid}/ready-for-sccb", ReadyForSccbAsync);
        group.MapPost("/{id:guid}/sccb/open", OpenBySccbAsync);
        group.MapPost("/{id:guid}/implementation", BeginImplementationAsync);
        group.MapPost("/{id:guid}/resume", ResumeAsync);
        group.MapPost("/{id:guid}/investigation", InvestigateAsync);
        group.MapPost("/{id:guid}/resolution", ProposeResolutionAsync);
        group.MapPost("/{id:guid}/verify", VerifyAsync);
        group.MapPost("/{id:guid}/closure/approve", ApproveClosureAsync);
        group.MapPost("/{id:guid}/disposition", DispositionAsync);
        group.MapGet("/{id:guid}/duplicate-candidates", DuplicateCandidatesAsync);
        group.MapPost("/{id:guid}/reopen", ReopenAsync);
        group.MapPost("/{id:guid}/blocker", BlockerAsync);
        group.MapPost("/{id:guid}/release-waiver", ApproveReleaseWaiverAsync);
        group.MapPost("/{id:guid}/release-waiver/{waiverId:guid}/revoke", RevokeReleaseWaiverAsync);
        group.MapPost("/{id:guid}/links", LinkAsync);
        group.MapGet("/{id:guid}/closure-package", ClosurePackageAsync);
        return app;
    }

    private static async Task<IResult> CreateAsync(CreateProblemReportRequest request, HttpContext http, AeroLinkDbContext db, IdentityService identity, CancellationToken ct)
    {
        if (!await http.HasProjectRoleAsync(db, identity, request.ProjectId, ct, ProgramRole.Engineer, ProgramRole.TestEngineer, ProgramRole.ConfigurationManager, ProgramRole.ProgramManager)) return Results.Forbid();
        if (request.ReleaseId is not null && !await db.Releases.AnyAsync(x => x.Id == request.ReleaseId && x.ProjectId == request.ProjectId, ct))
            return Results.BadRequest(new { error = "The selected build does not belong to this project." });
        await using var transaction = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, ct);
        try
        {
            var now = DateTimeOffset.UtcNow; var actor = http.UserAccount();
            var item = new ProblemReport(request.ProjectId, await IdentifierAllocator.NextProblemReportAsync(db, ct), request.Title, request.Problem, request.Analysis ?? "", actor.UserName, now,
                request.Classification ?? "Software anomaly", request.Severity ?? ProblemReportSeverity.Major, request.Priority ?? ProblemReportPriority.Normal, request.Origin ?? "Manual report", request.AffectedConfiguration ?? "",
                request.ReleaseId, actor.UserName, request.ProblemRich ?? "", request.AdditionalInformation ?? "", request.AdditionalInformationRich ?? "", request.SystemAircraftImpact ?? "", request.ImpactAssessmentJson ?? "{}");
            db.ProblemReports.Add(item);
            if (request.ReleaseId is not null)
                db.ProblemReportLinks.Add(ProblemReportRelationshipPolicy.CreateControlled(item.Id, "Release", request.ReleaseId.Value,
                    ProblemReportRelationshipPolicy.BuildScope, ProblemReportRelationshipProducer.TargetBuildWorkflow, actor.UserName, now));
            AddRevision(db, item, "ProblemReportCreated", actor.UserName, now);
            await db.SaveChangesAsync(ct); await transaction.CommitAsync(ct);
            return Results.Created($"/api/problem-reports/{item.Id}", Detail(item, [], []));
        }
        catch (DomainException ex) { return Results.BadRequest(new { error = ex.Message }); }
        catch (DbUpdateException) { return Results.Conflict(new { error = "A problem report number was allocated concurrently. Retry the create request.", code = "number_allocation_conflict" }); }
    }

    private static async Task<IResult> CreateFromFailureAsync(Guid executionId, CreateProblemReportFromExecutionRequest request, HttpContext http, AeroLinkDbContext db, IdentityService identity, CancellationToken ct)
    {
        var execution = await db.TestExecutions.AsNoTracking().SingleOrDefaultAsync(x => x.Id == executionId, ct);
        if (execution is null) return Results.NotFound();
        if (execution.Outcome != TestOutcome.Fail) return Results.BadRequest(new { error = "A problem report can be created from a failed execution only." });
        if (!await http.HasProjectRoleAsync(db, identity, execution.ProjectId, ct, ProgramRole.Engineer, ProgramRole.TestEngineer, ProgramRole.ConfigurationManager, ProgramRole.ProgramManager)) return Results.Forbid();
        await using var transaction = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, ct);
        try
        {
            var now = DateTimeOffset.UtcNow; var actor = http.UserAccount();
            var executionReleaseId = await db.SoftwareBuilds.AsNoTracking().Where(x => x.Id == execution.SoftwareBuildId).Select(x => (Guid?)x.ReleaseId).SingleOrDefaultAsync(ct);
            // A current build may own corrective work raised from predecessor evidence. The execution link
            // retains the historical origin; an explicit selected build owns the new problem report.
            var releaseId = request.ReleaseId ?? executionReleaseId;
            if (releaseId is not null && !await db.Releases.AnyAsync(x => x.Id == releaseId && x.ProjectId == execution.ProjectId, ct))
                return Results.BadRequest(new { error = "The selected build does not belong to this project." });
            var item = new ProblemReport(execution.ProjectId, await IdentifierAllocator.NextProblemReportAsync(db, ct), request.Title ?? $"Failed execution {execution.Id.ToString()[..8]}", request.Problem ?? execution.Determination,
                request.Analysis ?? "Created from failed verification execution.", actor.UserName, now, request.Classification ?? "Verification failure", request.Severity ?? ProblemReportSeverity.Major,
                request.Priority ?? ProblemReportPriority.High, "Test execution", request.AffectedConfiguration ?? execution.Configuration, releaseId, actor.UserName);
            db.ProblemReports.Add(item); db.ProblemReportLinks.Add(ProblemReportRelationshipPolicy.CreateControlled(item.Id, "TestExecution", execution.Id,
                ProblemReportRelationshipPolicy.OriginatingFailure, ProblemReportRelationshipProducer.FailureCreationWorkflow, actor.UserName, now));
            if (releaseId is not null)
                db.ProblemReportLinks.Add(ProblemReportRelationshipPolicy.CreateControlled(item.Id, "Release", releaseId.Value,
                    ProblemReportRelationshipPolicy.BuildScope, ProblemReportRelationshipProducer.TargetBuildWorkflow, actor.UserName, now));
            AddRevision(db, item, "ProblemReportCreatedFromFailedExecution", actor.UserName, now);
            await db.SaveChangesAsync(ct); await transaction.CommitAsync(ct);
            var identifier = await ResolveLinkIdentifierAsync("TestExecution", execution.Id, db, ct);
            return Results.Created($"/api/problem-reports/{item.Id}", Detail(item, [new ProblemReportLinkView("TestExecution", execution.Id, identifier, ProblemReportRelationshipPolicy.OriginatingFailure, actor.UserName, now, false)], []));
        }
        catch (DomainException ex) { return Results.BadRequest(new { error = ex.Message }); }
        catch (DbUpdateException) { return Results.Conflict(new { error = "A problem report number was allocated concurrently. Retry the create request.", code = "number_allocation_conflict" }); }
    }

    private static async Task<IResult> ListAsync(Guid projectId, Guid? targetReleaseId, bool? targetUnassigned, string? search, ProblemReportState? state, ProblemReportSeverity? severity, ProblemReportPriority? priority, string? owner, bool? blockersOnly, ProblemReportType? type, int? page, int? pageSize, HttpContext http, AeroLinkDbContext db, CancellationToken ct)
    {
        if (!await http.HasProjectAccessAsync(db, projectId, ct)) return Results.Forbid();
        if (targetReleaseId is not null && targetUnassigned == true)
            return Results.BadRequest(new { error = "Choose either a target build or unassigned Problem Reports, not both." });
        // One Problem Report database, read the same from any build.
        //
        // A report points at a target build and may be closed during a particular one, but the database itself
        // is a Project-level record set: the list of what is open and in work does not change because the
        // reader happens to be standing in 1.5 rather than 1.6. Applying the active build as an implicit
        // filter made ten reports invisible from the other build, which is not a different view of one
        // database — it is a different database as far as the reader can tell.
        //
        // `targetReleaseId` still filters, but only when a user asks for it. The workspace no longer supplies
        // it silently. This deliberately reverses the build-scoping half of #298; see DEC-089.
        var query = db.ProblemReports.AsNoTracking().Where(x => x.ProjectId == projectId);
        query = ApplyTargetFilter(query, targetReleaseId, targetUnassigned, db);
        if (state is not null) query = query.Where(x => x.State == state);
        if (severity is not null) query = query.Where(x => x.Severity == severity);
        if (priority is not null) query = query.Where(x => x.Priority == priority);
        if (type is not null) query = query.Where(x => x.Type == type);
        if (!string.IsNullOrWhiteSpace(owner)) { var normalizedOwner = owner.Trim().ToLower(); query = query.Where(x => x.ResponsibleEngineerId.ToLower().Contains(normalizedOwner)); }
        if (blockersOnly == true) query = query.Where(x => x.IsReleaseBlocker);
        if (!string.IsNullOrWhiteSpace(search)) { var term = search.Trim().ToLower(); query = query.Where(x => x.ReportNumber.ToLower().Contains(term) || x.Title.ToLower().Contains(term) || x.Problem.ToLower().Contains(term) || x.RootCause.ToLower().Contains(term)); }
        var now = DateTimeOffset.UtcNow;
        // SQLite does not translate DateTimeOffset ordering/comparison. Resolve only the independent waiver
        // candidates first, then use their bounded ID set in the authoritative count/page predicate. This
        // never materializes the Project's Problem Report population before Skip/Take.
        var waiverRows = await db.ReadinessWaivers.AsNoTracking().Where(waiver => waiver.ProjectId == projectId
            && waiver.BlockerType == "ProblemReportReleaseBlocker" && waiver.Provenance == "ServerAuthorized"
            && waiver.RevokedAt == null).ToListAsync(ct);
        var activeWaiverRows = waiverRows.Where(waiver => waiver.IsActive(now)).ToList();
        var activeWaivedIds = Array.Empty<Guid>();
        if (activeWaiverRows.Count > 0)
        {
            var blockerIds = activeWaiverRows.Select(waiver => waiver.BlockerId).Distinct().ToArray();
            var waiverReports = await db.ProblemReports.AsNoTracking().Where(report =>
                report.ProjectId == projectId && blockerIds.Contains(report.Id)).ToListAsync(ct);
            activeWaivedIds = waiverReports.Where(report => activeWaiverRows.Any(waiver =>
                waiver.IsActiveFor(report, now))).Select(report => report.Id).ToArray();
        }
        var matching = query.Select(report => new
        {
            Report = report,
            Waived = activeWaivedIds.Contains(report.Id),
        });
        if (blockersOnly == true) matching = matching.Where(row => !row.Waived);

        var size = Math.Clamp(pageSize ?? 10, 1, 200);
        var requestedPage = Math.Max(page ?? 1, 1);
        var total = await matching.CountAsync(ct);
        var totalPages = (int)Math.Ceiling(total / (double)size);
        var current = Math.Min(requestedPage, Math.Max(totalPages, 1));
        var items = await matching.OrderBy(row => row.Report.NumberSequence)
            .ThenBy(row => row.Report.Revision).ThenBy(row => row.Report.Id)
            .Skip((current - 1) * size).Take(size).ToListAsync(ct);
        return Results.Ok(new { page = current, pageSize = size, totalCount = total, totalPages,
            items = items.Select(row => Summary(row.Report, row.Waived)) });
    }

    private static async Task<IResult> DashboardAsync(Guid projectId, Guid? targetReleaseId, bool? targetUnassigned, HttpContext http, AeroLinkDbContext db, CancellationToken ct)
    {
        if (!await http.HasProjectAccessAsync(db, projectId, ct)) return Results.Forbid();
        if (targetReleaseId is not null && targetUnassigned == true)
            return Results.BadRequest(new { error = "Choose either a target build or unassigned Problem Reports, not both." });
        var query = db.ProblemReports.AsNoTracking().Where(x => x.ProjectId == projectId);
        // The dashboard counts the same database the list shows. Filtering it by the active build while the
        // list is Project-scoped would give two different answers about one record set.
        query = ApplyTargetFilter(query, targetReleaseId, targetUnassigned, db);
        var reports = await query.ToListAsync(ct);
        var now = DateTimeOffset.UtcNow;
        var waiverRows = await db.ReadinessWaivers.AsNoTracking().Where(x => x.ProjectId == projectId
            && x.BlockerType == "ProblemReportReleaseBlocker").ToListAsync(ct);
        bool IsWaived(ProblemReport report) => waiverRows.Any(waiver => waiver.IsActiveFor(report, now));
        var active = reports.Where(x => ProblemReportLifecycle.IsActiveWork(x.State)).ToList();
        return Results.Ok(new
        {
            generatedAt = DateTimeOffset.UtcNow,
            summary = new { total = reports.Count, active = active.Count, closureAwaitingApproval = reports.Count(x => x.State == ProblemReportState.AwaitingSqaClosure), closed = reports.Count(x => x.State == ProblemReportState.Closed), releaseBlockers = reports.Count(x => x.IsReleaseBlocker && !IsWaived(x)), waivedBlockers = reports.Count(x => x.IsReleaseBlocker && IsWaived(x)) },
            bySeverity = reports.GroupBy(x => x.Severity).OrderBy(x => x.Key).Select(x => new { severity = x.Key.ToString(), count = x.Count() }),
            byState = reports.GroupBy(x => x.State).OrderBy(x => x.Key).Select(x => new { state = x.Key.ToString(), count = x.Count() }),
            attention = active.OrderByDescending(x => x.IsReleaseBlocker).ThenByDescending(x => x.Severity).ThenBy(x => x.CreatedAt).Take(12).Select(x => Summary(x, IsWaived(x)))
        });
    }

    private static IQueryable<ProblemReport> ApplyTargetFilter(IQueryable<ProblemReport> query,
        Guid? targetReleaseId, bool? targetUnassigned, AeroLinkDbContext db)
    {
        if (targetReleaseId is not null)
            return query.Where(report => db.ProblemReportLinks.Any(link => link.ProblemReportId == report.Id
                && link.ArtifactType == "Release" && link.ArtifactId == targetReleaseId));
        return targetUnassigned == true ? query.Where(report => report.TargetReleaseId == null) : query;
    }

    private static async Task<IResult> LinkedAsync(string artifactType, Guid artifactId, HttpContext http, AeroLinkDbContext db, CancellationToken ct)
    {
        var canonicalType = CanonicalLinkType(artifactType);
        var links = await db.ProblemReportLinks.AsNoTracking().Where(x => x.ArtifactType == canonicalType && x.ArtifactId == artifactId).ToListAsync(ct);
        var ids = links.Select(x => x.ProblemReportId).Distinct().ToList(); var reports = await db.ProblemReports.AsNoTracking().Where(x => ids.Contains(x.Id)).ToListAsync(ct);
        var permitted = new List<ProblemReport>(); foreach (var report in reports) if (await http.HasProjectAccessAsync(db, report.ProjectId, ct)) permitted.Add(report);
        return Results.Ok(permitted.Select(x => Summary(x)));
    }

    private static async Task<IResult> DetailAsync(Guid id, HttpContext http, AeroLinkDbContext db,
        IdentityService identity, CancellationToken ct)
    {
        var report = await db.ProblemReports.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct); if (report is null) return Results.NotFound();
        if (!await http.HasProjectAccessAsync(db, report.ProjectId, ct)) return Results.Forbid();
        var links = (await db.ProblemReportLinks.AsNoTracking().Where(x => x.ProblemReportId == id).ToListAsync(ct)).OrderBy(x => x.AddedAt).ToList();
        var revisions = (await db.ProblemReportRevisions.AsNoTracking().Where(x => x.ProblemReportId == id).ToListAsync(ct)).OrderByDescending(x => x.OccurredAt).ToList();
        var candidates = await db.ProblemReportClosureCandidates.AsNoTracking()
            .Where(x => x.ProblemReportId == id).ToListAsync(ct);
        var waivers = (await db.ReadinessWaivers.AsNoTracking().Where(x => x.ProjectId == report.ProjectId
            && x.BlockerType == "ProblemReportReleaseBlocker" && x.BlockerId == report.Id).ToListAsync(ct))
            .OrderByDescending(x => x.CreatedAt).ToList();
        var canApproveSqaClosure = report.State == ProblemReportState.AwaitingSqaClosure
            && await HasCurrentSqaClosureAuthorityAsync(report, http, db, identity, ct)
            && !string.Equals(http.UserAccount().UserName, report.ReportedBy, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(http.UserAccount().UserName, report.ResponsibleEngineerId, StringComparison.OrdinalIgnoreCase);
        var waiverAuthority = await CurrentReleaseWaiverAuthorityAsync(report, http.UserAccount(), db, identity,
            DateTimeOffset.UtcNow, ct);
        var canApproveReleaseWaiver = report.IsReleaseBlocker
            && report.State is not (ProblemReportState.Closed or ProblemReportState.Duplicate
                or ProblemReportState.CannotReproduce or ProblemReportState.NoFaultFound
                or ProblemReportState.AcceptedRisk or ProblemReportState.Rejected)
            && waiverAuthority is not null
            && !string.Equals(http.UserAccount().UserName, report.ReportedBy, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(http.UserAccount().UserName, report.ResponsibleEngineerId, StringComparison.OrdinalIgnoreCase)
            && !waivers.Any(item => item.IsActiveFor(report, DateTimeOffset.UtcNow));
        var programId = await db.Projects.AsNoTracking().Where(item => item.Id == report.ProjectId)
            .Select(item => item.ProgramId).SingleAsync(ct);
        var ownerAuthority = await ProblemReportOwnerStatusAsync(report.ResponsibleEngineerId, programId, db, ct);
        var actor = http.UserAccount();
        var canRecoverOwner = !ownerAuthority.Eligible && !actor.IsAdministrator
            && await HasProblemReportOwnerRecoveryAuthorityAsync(actor.Id, programId, db, ct);
        var duplicateDiagnostic = await new ProblemReportDuplicateDispositionPolicy(db).DiagnoseAsync(report, ct);
        return Results.Ok(Detail(report, await LinkViewsAsync(report, links, db, ct), revisions,
            candidates.OrderByDescending(x => x.ReportRevision).ThenByDescending(x => x.Sequence),
            new
            {
                canApproveSqaClosure,
                canApproveReleaseWaiver,
                releaseWaiverAuthority = waiverAuthority,
                ownerEligible = ownerAuthority.Eligible,
                ownerAuthorityException = ownerAuthority.Eligible ? null : "The assigned owner no longer has accountable Problem Report authority in this Program.",
                canReassignOwner = string.Equals(actor.UserName, report.ResponsibleEngineerId, StringComparison.OrdinalIgnoreCase) || canRecoverOwner,
                canRecoverOwner,
            }, waivers, duplicateDiagnostic));
    }

    private static async Task<IResult> ReassignAsync(Guid id, ReassignRequest request, HttpContext http,
        AeroLinkDbContext db, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        var report = await db.ProblemReports.SingleOrDefaultAsync(x => x.Id == id, ct);
        if (report is null) return Results.NotFound();
        if (!await http.HasProjectAccessAsync(db, report.ProjectId, ct)) return Results.Forbid();
        if (request.ExpectedVersion is not null && request.ExpectedVersion != report.Version)
            return Results.Conflict(new { error = "This problem report changed after it was opened. Refresh before continuing.", code = "stale_version", currentVersion = report.Version });

        var programId = await db.Projects.AsNoTracking().Where(item => item.Id == report.ProjectId)
            .Select(item => item.ProgramId).SingleAsync(ct);
        var actor = http.UserAccount();
        var ownerAuthority = await ProblemReportOwnerStatusAsync(report.ResponsibleEngineerId, programId, db, ct);
        var supervisoryRecovery = !ownerAuthority.Eligible
            && !actor.IsAdministrator
            && await HasProblemReportOwnerRecoveryAuthorityAsync(actor.Id, programId, db, ct);
        if (!string.Equals(actor.UserName, report.ResponsibleEngineerId, StringComparison.OrdinalIgnoreCase)
            && !supervisoryRecovery)
            return Results.Json(new { error = "Only the current responsible engineer, or authorized supervision when that owner is no longer eligible, can reassign this Problem Report.", code = "pr_owner_reassignment_forbidden" }, statusCode: StatusCodes.Status403Forbidden);

        var requestedOwner = request.ResponsibleEngineerId.Trim().ToLowerInvariant();
        var account = await db.UserAccounts.AsNoTracking().SingleOrDefaultAsync(x => x.UserName == requestedOwner, ct);
        if (account is null || account.State != AccountState.Active)
            return Results.BadRequest(new { error = "The selected responsible engineer is not an available active account.", code = "pr_owner_account_unavailable" });
        var targetAuthority = await ProblemReportOwnerStatusAsync(account.UserName, programId, db, ct);
        if (!targetAuthority.ProgramMember)
            return Results.BadRequest(new { error = "The selected responsible engineer is not a member of this Program.", code = "pr_owner_program_membership_required" });
        if (!targetAuthority.Eligible)
            return Results.BadRequest(new { error = "The selected Program member does not have accountable Problem Report owner authority.", code = "pr_owner_authority_required" });

        try
        {
            var result = await ChangeAsync(report, request.ExpectedVersion, http, db, ct, "ResponsibleEngineerReassigned",
                (item, currentActor, now) => item.Reassign(currentActor.UserName, account.UserName, now, supervisoryRecovery));
            await transaction.CommitAsync(ct);
            return result;
        }
        catch (Exception exception) when (exception is DbUpdateException or DbException)
        {
            return Results.Conflict(new { error = "Problem Report ownership authority changed concurrently. Refresh before reassigning.", code = "pr_owner_authority_changed" });
        }
    }

    private static async Task<IResult> RetargetAsync(Guid id, RetargetRequest request, HttpContext http, AeroLinkDbContext db, CancellationToken ct)
    {
        var report = await db.ProblemReports.SingleOrDefaultAsync(x => x.Id == id, ct); if (report is null) return Results.NotFound();
        if (!await http.HasProjectAccessAsync(db, report.ProjectId, ct)) return Results.Forbid();
        if (request.ExpectedVersion is not null && request.ExpectedVersion != report.Version) return Results.Conflict(new { error = "This problem report changed after it was opened. Refresh before continuing.", code = "stale_version", currentVersion = report.Version });
        if (!await db.Releases.AnyAsync(x => x.Id == request.TargetReleaseId && x.ProjectId == report.ProjectId, ct)) return Results.BadRequest(new { error = "The selected target build does not belong to this project." });
        try
        {
            var actor = http.UserAccount(); var now = DateTimeOffset.UtcNow;
            var wasAwaitingClosure = report.State == ProblemReportState.AwaitingSqaClosure;
            report.Retarget(actor.UserName, request.TargetReleaseId, now);
            var existing = await db.ProblemReportLinks.Where(x => x.ProblemReportId == id && x.ArtifactType == "Release" && x.Relationship == ProblemReportRelationshipPolicy.BuildScope).ToListAsync(ct);
            db.ProblemReportLinks.RemoveRange(existing);
            db.ProblemReportLinks.Add(ProblemReportRelationshipPolicy.CreateControlled(id, "Release", request.TargetReleaseId,
                ProblemReportRelationshipPolicy.BuildScope, ProblemReportRelationshipProducer.TargetBuildWorkflow, actor.UserName, now));
            AddRevision(db, report, "TargetBuildChanged", actor.UserName, now);
            if (wasAwaitingClosure)
                await new ProblemReportClosureCandidateService(db).InvalidatePendingAsync(report, actor.UserName,
                    "TargetBuildChanged", now, ct);
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { id = report.Id, displayNumber = report.DisplayNumber, state = report.State.ToString(), version = report.Version, snapshotHash = report.CanonicalHash() });
        }
        catch (DomainException ex) { return Results.BadRequest(new { error = ex.Message }); }
        catch (DbUpdateConcurrencyException) { return Results.Conflict(new { error = "This problem report was updated concurrently. Refresh before continuing.", code = "stale_version" }); }
    }

    private static Task<IResult> ReadyForSccbAsync(Guid id, VersionRequest request, HttpContext http, AeroLinkDbContext db, CancellationToken ct) =>
        ChangeAsync(id, request.ExpectedVersion, http, db, ct, "ReadyForSccb", (report, actor, now) => report.ReadyForSccb(actor.UserName, now));

    private static async Task<IResult> OpenBySccbAsync(Guid id, VersionRequest request, HttpContext http, AeroLinkDbContext db, IdentityService identity, CancellationToken ct)
    {
        var report = await db.ProblemReports.SingleOrDefaultAsync(x => x.Id == id, ct); if (report is null) return Results.NotFound();
        if (!await http.HasProjectRoleAsync(db, identity, report.ProjectId, ct, ProgramRole.Approver, ProgramRole.ConfigurationManager, ProgramRole.ProgramManager)) return Results.Forbid();
        return await ChangeAsync(report, request.ExpectedVersion, http, db, ct, "OpenedBySccb", (item, actor, now) => item.OpenBySccb(actor.UserName, now));
    }

    private static Task<IResult> BeginImplementationAsync(Guid id, VersionRequest request, HttpContext http, AeroLinkDbContext db, CancellationToken ct) =>
        ChangeAsync(id, request.ExpectedVersion, http, db, ct, "ImplementationStarted", (report, actor, now) => report.BeginImplementation(actor.UserName, now));

    private static Task<IResult> ResumeAsync(Guid id, VersionRequest request, HttpContext http, AeroLinkDbContext db, CancellationToken ct) =>
        ChangeAsync(id, request.ExpectedVersion, http, db, ct, "ProblemReportResumed", (report, actor, now) => report.ResumeDeferred(actor.UserName, now));

    private static async Task<IResult> InvestigateAsync(Guid id, InvestigationRequest request, HttpContext http, AeroLinkDbContext db, CancellationToken ct) => await ChangeAsync(id, request.ExpectedVersion, http, db, ct, "InvestigationRecorded", (report, actor, now) => report.BeginInvestigation(actor.UserName, request.Analysis, request.RootCause ?? "", request.Effects ?? "", "", now));
    private static async Task<IResult> ProposeResolutionAsync(Guid id, ResolutionRequest request, HttpContext http, AeroLinkDbContext db, CancellationToken ct) => await ChangeAsync(id, request.ExpectedVersion, http, db, ct, "ResolutionProposed", (report, actor, now) => report.ProposeResolution(actor.UserName, request.CorrectiveAction, now));

    private static async Task<IResult> VerifyAsync(Guid id, VerificationRequest request, HttpContext http, AeroLinkDbContext db, CancellationToken ct)
    {
        var report = await db.ProblemReports.SingleOrDefaultAsync(x => x.Id == id, ct); if (report is null) return Results.NotFound();
        if (!await http.HasProjectAccessAsync(db, report.ProjectId, ct)) return Results.Forbid();
        var execution = await db.TestExecutions.AsNoTracking().SingleOrDefaultAsync(x => x.Id == request.TestExecutionId, ct);
        if (execution is null)
            return Results.BadRequest(new { error = "The selected closure execution does not exist in this Problem Report Project.", code = "pr_verification_wrong_project" });
        var decision = await new ProblemReportClosureVerificationPolicy(db).ValidateAsync(report, execution, ct);
        if (!decision.Accepted)
            return Results.Conflict(new { error = decision.Error, code = decision.Code });
        return await ChangeAsync(report, request.ExpectedVersion, http, db, ct, "ResolutionVerified", (item, actor, now) => item.RecordResolutionVerification(actor.UserName, execution.Id, now),
            link: (actor, now) => ProblemReportRelationshipPolicy.CreateControlled(report.Id, "TestExecution", execution.Id,
                ProblemReportRelationshipPolicy.ResolutionVerification, ProblemReportRelationshipProducer.ResolutionVerificationWorkflow, actor.UserName, now),
            afterMutation: async (item, actor, now, resolutionLink, _, token) =>
                await new ProblemReportClosureCandidateService(db).CreateAsync(item, execution,
                    resolutionLink!, actor.UserName, now, token));
    }

    private static async Task<IResult> ApproveClosureAsync(Guid id, ClosureApprovalRequest request, HttpContext http, AeroLinkDbContext db, IdentityService identity, CancellationToken ct)
    {
        var report = await db.ProblemReports.SingleOrDefaultAsync(x => x.Id == id, ct); if (report is null) return Results.NotFound();
        if (!await HasCurrentSqaClosureAuthorityAsync(report, http, db, identity, ct)) return Results.Forbid();
        if (report.State == ProblemReportState.AwaitingSqaClosure
            && report.ResolutionVerificationExecutionId is { } currentExecutionId)
        {
            var currentExecution = await db.TestExecutions.AsNoTracking()
                .SingleOrDefaultAsync(item => item.Id == currentExecutionId, ct);
            if (currentExecution is null)
                return Results.Conflict(new { error = "The recorded closure execution is unavailable.", code = "pr_verification_missing_evidence" });
            var currentVerification = await new ProblemReportClosureVerificationPolicy(db)
                .ValidateAsync(report, currentExecution, ct);
            if (!currentVerification.Accepted)
                return Results.Conflict(new { error = currentVerification.Error, code = currentVerification.Code });
        }
        var candidateDecision = await new ProblemReportClosureCandidateService(db).ValidateForApprovalAsync(report, ct);
        if (!candidateDecision.Accepted)
            return Results.Conflict(new { error = candidateDecision.Error, code = candidateDecision.Code });
        var execution = await db.TestExecutions.AsNoTracking()
            .SingleAsync(item => item.Id == candidateDecision.Candidate!.VerificationExecutionId, ct);
        var verificationDecision = await new ProblemReportClosureVerificationPolicy(db).ValidateAsync(report, execution, ct);
        if (!verificationDecision.Accepted)
            return Results.Conflict(new { error = verificationDecision.Error, code = verificationDecision.Code });
        return await ChangeAsync(report, request.ExpectedVersion, http, db, ct, "ClosureApproved",
            (item, user, now) => item.ApproveClosure(user.UserName, user.Id, now),
            afterMutation: async (item, user, now, _, closureRevision, token) =>
                await new ProblemReportClosureCandidateService(db).FreezeForApprovalAsync(item,
                    candidateDecision.Candidate!, closureRevision, user.UserName, user.Id,
                    ProgramRole.SoftwareQualityAnalyst.ToString(), now, token));
    }

    private static async Task<IResult> DispositionAsync(Guid id, DispositionRequest request, HttpContext http, AeroLinkDbContext db, CancellationToken ct)
    {
        if (request.Disposition != ProblemReportDisposition.Duplicate)
        {
            var ordinaryReport = await db.ProblemReports.SingleOrDefaultAsync(item => item.Id == id, ct);
            if (ordinaryReport is null) return Results.NotFound();
            if (!await http.HasProjectAccessAsync(db, ordinaryReport.ProjectId, ct)) return Results.Forbid();
            if (request.ExpectedVersion is not null && request.ExpectedVersion != ordinaryReport.Version)
                return Results.Conflict(new { error = "This problem report changed after it was opened. Refresh before continuing.", code = "stale_version", currentVersion = ordinaryReport.Version });
            if (request.DuplicateOfId is not null)
                return Results.BadRequest(new { error = "Only a Duplicate disposition may name another Problem Report.", code = "pr_duplicate_target_unexpected" });
            return await ChangeAsync(ordinaryReport, request.ExpectedVersion, http, db, ct, "DispositionRecorded",
                (item, actor, now) => item.ApplyDisposition(actor.UserName, request.Disposition,
                    request.Rationale, null, now));
        }

        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        var report = await db.ProblemReports.SingleOrDefaultAsync(item => item.Id == id, ct);
        if (report is null) return Results.NotFound();
        if (!await http.HasProjectAccessAsync(db, report.ProjectId, ct)) return Results.Forbid();
        if (request.ExpectedVersion is not null && request.ExpectedVersion != report.Version)
            return Results.Conflict(new { error = "This problem report changed after it was opened. Refresh before continuing.", code = "stale_version", currentVersion = report.Version });

        if (request.DuplicateOfId is null || request.DuplicateOfId == Guid.Empty)
            return Results.BadRequest(new { error = "A Duplicate disposition requires a canonical Problem Report target.", code = "pr_duplicate_target_required" });
        var decision = await new ProblemReportDuplicateDispositionPolicy(db)
            .ValidateAsync(report, request.DuplicateOfId.Value, ct);
        if (!decision.Accepted)
            return Results.BadRequest(new { error = decision.Error, code = decision.Code });

        try
        {
            var result = await ChangeAsync(report, request.ExpectedVersion, http, db, ct, "DispositionRecorded",
                (item, actor, now) => item.ApplyDisposition(actor.UserName, request.Disposition, request.Rationale,
                    request.DuplicateOfId, now),
                link: request.DuplicateOfId is null ? null : (actor, now) =>
                    ProblemReportRelationshipPolicy.CreateControlled(id, "ProblemReport", request.DuplicateOfId.Value,
                        ProblemReportRelationshipPolicy.DuplicateOf,
                        ProblemReportRelationshipProducer.DispositionWorkflow, actor.UserName, now));
            if (result is IStatusCodeHttpResult { StatusCode: >= 400 }) return result;
            await transaction.CommitAsync(ct);
            return result;
        }
        catch (Exception exception) when (exception is DbUpdateException or DbException)
        {
            return Results.Conflict(new
            {
                error = "The Problem Report duplicate graph changed concurrently. Refresh before recording the disposition.",
                code = "pr_duplicate_concurrent_change",
            });
        }
    }
    private static async Task<IResult> DuplicateCandidatesAsync(Guid id, string? search, HttpContext http,
        AeroLinkDbContext db, CancellationToken ct)
    {
        var report = await db.ProblemReports.AsNoTracking().SingleOrDefaultAsync(item => item.Id == id, ct);
        if (report is null) return Results.NotFound();
        if (!await http.HasProjectAccessAsync(db, report.ProjectId, ct)) return Results.Forbid();
        var candidates = await new ProblemReportDuplicateDispositionPolicy(db)
            .EligibleTargetsAsync(report, search, 50, ct);
        return Results.Ok(new { items = candidates.Select(item => Summary(item)), totalCount = candidates.Count });
    }
    private static async Task<IResult> ReopenAsync(Guid id, ReopenRequest request, HttpContext http, AeroLinkDbContext db, CancellationToken ct) => await ChangeAsync(id, request.ExpectedVersion, http, db, ct, "ProblemReportReopened", (report, actor, now) => report.Reopen(actor.UserName, request.Rationale, now), detail: request.Rationale);
    private static async Task<IResult> BlockerAsync(Guid id, BlockerRequest request, HttpContext http, AeroLinkDbContext db, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(request.WaiverRationale))
            return Results.BadRequest(new { error = "A release-blocker waiver requires a separate independent approval.", code = "pr_waiver_separate_approval_required" });
        return await ChangeAsync(id, request.ExpectedVersion, http, db, ct,
            request.IsReleaseBlocker ? "ReleaseBlockerRaised" : "ReleaseBlockerCleared",
            (report, actor, now) => report.SetReleaseBlocker(actor.UserName, request.IsReleaseBlocker, now));
    }

    private static async Task<IResult> ApproveReleaseWaiverAsync(Guid id, ReleaseWaiverRequest request,
        HttpContext http, AeroLinkDbContext db, IdentityService identity, CancellationToken ct)
    {
        var report = await db.ProblemReports.SingleOrDefaultAsync(x => x.Id == id, ct); if (report is null) return Results.NotFound();
        var actor = http.UserAccount(); var now = DateTimeOffset.UtcNow;
        var authority = await CurrentReleaseWaiverAuthorityAsync(report, actor, db, identity, now, ct);
        if (authority is null) return Results.Forbid();
        if (string.Equals(actor.UserName, report.ReportedBy, StringComparison.OrdinalIgnoreCase)
            || string.Equals(actor.UserName, report.ResponsibleEngineerId, StringComparison.OrdinalIgnoreCase))
            return Results.BadRequest(new { error = "The reporter or responsible engineer cannot independently approve this release waiver.", code = "pr_waiver_independence_required" });
        var existingWaivers = await db.ReadinessWaivers.AsNoTracking().Where(x => x.ProjectId == report.ProjectId
            && x.BlockerType == "ProblemReportReleaseBlocker" && x.BlockerId == report.Id).ToListAsync(ct);
        if (existingWaivers.Any(item => item.IsActiveFor(report, now)))
            return Results.Conflict(new { error = "This release blocker already has an active controlled waiver.", code = "pr_waiver_already_active" });
        return await ChangeAsync(report, request.ExpectedVersion, http, db, ct, "ReleaseBlockerWaiverApproved",
            (item, user, decisionAt) => item.RecordReleaseWaiverDecision(user.UserName, decisionAt),
            afterMutation: (item, user, decisionAt, _, _, _) =>
            {
                db.ReadinessWaivers.Add(new ReadinessWaiver(item.ProjectId, "ProblemReportReleaseBlocker",
                    item.Id, item.Revision, item.ReleaseBlockerVersion, request.Rationale, user.Id, user.UserName, authority,
                    "IndependentProblemReportReleaseWaiver", request.ExpiresAt, user.UserName, decisionAt));
                return Task.CompletedTask;
            });
    }

    private static async Task<IResult> RevokeReleaseWaiverAsync(Guid id, Guid waiverId,
        RevokeReleaseWaiverRequest request, HttpContext http, AeroLinkDbContext db, IdentityService identity,
        CancellationToken ct)
    {
        var report = await db.ProblemReports.SingleOrDefaultAsync(x => x.Id == id, ct); if (report is null) return Results.NotFound();
        var waiver = await db.ReadinessWaivers.SingleOrDefaultAsync(x => x.Id == waiverId
            && x.ProjectId == report.ProjectId && x.BlockerType == "ProblemReportReleaseBlocker"
            && x.BlockerId == report.Id, ct); if (waiver is null) return Results.NotFound();
        var actor = http.UserAccount(); var now = DateTimeOffset.UtcNow;
        if (await CurrentReleaseWaiverAuthorityAsync(report, actor, db, identity, now, ct) is null) return Results.Forbid();
        if (!waiver.IsActiveFor(report, now)) return Results.Conflict(new { error = "Only the current active waiver can be revoked.", code = "pr_waiver_not_active" });
        return await ChangeAsync(report, request.ExpectedVersion, http, db, ct, "ReleaseBlockerWaiverRevoked",
            (item, user, decisionAt) => item.RecordReleaseWaiverDecision(user.UserName, decisionAt),
            afterMutation: (item, user, decisionAt, _, _, _) =>
            { waiver.Revoke(user.UserName, request.Reason, decisionAt); return Task.CompletedTask; });
    }

    private static async Task<IResult> LinkAsync(Guid id, LinkRequest request, HttpContext http, AeroLinkDbContext db, CancellationToken ct)
    {
        var report = await db.ProblemReports.SingleOrDefaultAsync(x => x.Id == id, ct); if (report is null) return Results.NotFound();
        if (!await http.HasProjectAccessAsync(db, report.ProjectId, ct)) return Results.Forbid();
        var canonicalType = CanonicalLinkType(request.ArtifactType);
        var relationship = request.Relationship?.Trim();
        if (!ProblemReportRelationshipPolicy.IsGenericContextPair(canonicalType, relationship))
            return Results.BadRequest(new { error = "That relationship is controlled by a dedicated Problem Report workflow or is not supported.", code = "problem_report_relationship_not_generic" });
        if (request.ExpectedVersion is null)
            return Results.BadRequest(new { error = "The current Problem Report version is required to add contextual links.", code = "expected_version_required" });
        if (request.ExpectedVersion != report.Version)
            return Results.Conflict(new { error = "This problem report changed after it was opened. Refresh before continuing.", code = "stale_version", currentVersion = report.Version });
        if (!await LinkExistsInProjectAsync(canonicalType, request.ArtifactId, report.ProjectId, db, ct)) return Results.BadRequest(new { error = "The linked artifact does not exist in this problem report's project or is not a supported link target." });
        try
        {
            var now = DateTimeOffset.UtcNow; var actor = http.UserAccount();
            var wasAwaitingClosure = report.State == ProblemReportState.AwaitingSqaClosure;
            report.RecordContextLink(actor.UserName, now);
            db.ProblemReportLinks.Add(ProblemReportRelationshipPolicy.CreateGenericContext(report.Id, canonicalType, request.ArtifactId, relationship!, actor.UserName, now)); AddRevision(db, report, "ContextArtifactLinked", actor.UserName, now);
            if (wasAwaitingClosure)
                await new ProblemReportClosureCandidateService(db).InvalidatePendingAsync(report, actor.UserName,
                    "ContextArtifactLinked", now, ct);
            await db.SaveChangesAsync(ct); return Results.Created($"/api/problem-reports/{id}/links/{request.ArtifactId}", new { artifactType = canonicalType, request.ArtifactId, relationship, version = report.Version });
        }
        catch (DomainException ex) { return Results.BadRequest(new { error = ex.Message }); }
        catch (DbUpdateException) { return Results.Conflict(new { error = "That problem-report link already exists." }); }
    }

    private static async Task<IResult> ClosurePackageAsync(Guid id, Guid? candidateId, HttpContext http,
        AeroLinkDbContext db, CancellationToken ct)
    {
        var report = await db.ProblemReports.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct); if (report is null) return Results.NotFound();
        if (!await http.HasProjectAccessAsync(db, report.ProjectId, ct)) return Results.Forbid();
        var candidates = await db.ProblemReportClosureCandidates.AsNoTracking()
            .Where(item => item.ProblemReportId == id
                && (item.State == ProblemReportClosureCandidateState.Approved
                    || item.State == ProblemReportClosureCandidateState.LegacyUnavailable)).ToListAsync(ct);
        var candidate = candidateId is { } selectedId
            ? candidates.SingleOrDefault(item => item.Id == selectedId)
            : candidates.OrderByDescending(item => item.ReportRevision).ThenByDescending(item => item.Sequence).FirstOrDefault();
        if (candidate is null)
        {
            if (report.State == ProblemReportState.Closed)
                return Results.Conflict(new { error = "This legacy closure predates frozen package evidence; an exact historical package cannot be fabricated.", code = "pr_closure_package_legacy_unfrozen", provenance = "LegacyClosureNotFrozen", report.Id, report.Revision, report.ClosureApprovedByName, report.ClosureApprovedAt });
            return Results.Conflict(new { error = "No independently approved closure package exists for this Problem Report.", code = "pr_closure_package_missing" });
        }
        if (candidate.State == ProblemReportClosureCandidateState.LegacyUnavailable
            || string.IsNullOrWhiteSpace(candidate.ClosurePackageJson))
            return Results.Conflict(new { error = "This legacy closure predates frozen package evidence; an exact historical package cannot be fabricated.", code = "pr_closure_package_legacy_unfrozen", provenance = "LegacyClosureNotFrozen", candidate.Id, candidate.ReportRevision, candidate.ApprovedBy, candidate.ApprovedAt });
        return Results.Ok(new
        {
            packageType = "ProblemReportClosurePackage",
            generatedAt = DateTimeOffset.UtcNow,
            generatorVersion = "AeroLink-3.0",
            snapshot = new { candidate.Id, candidate.ReportRevision, candidate.SchemaVersion,
                candidate.ReportSnapshotSchemaVersion,
                candidate.PackageProvenance, candidate.ClosurePackageHash, candidate.ApprovedByAccountId,
                candidate.ApprovedBy, candidate.ApprovedAt,
                approvalAuthority = ClosureApprovalAuthority(candidate.ClosurePackageJson) },
            package = JsonSerializer.Deserialize<JsonElement>(candidate.ClosurePackageJson),
        });
    }

    private static async Task<IResult> ChangeAsync(Guid id, long? expectedVersion, HttpContext http, AeroLinkDbContext db, CancellationToken ct, string eventType, Action<ProblemReport, AuthenticatedUser, DateTimeOffset> action, Func<AuthenticatedUser, DateTimeOffset, ProblemReportLink>? link = null,
        Func<ProblemReport, AuthenticatedUser, DateTimeOffset, ProblemReportLink?, ProblemReportRevision, CancellationToken, Task>? afterMutation = null,
        string? detail = null)
    {
        var report = await db.ProblemReports.SingleOrDefaultAsync(x => x.Id == id, ct); if (report is null) return Results.NotFound();
        return await ChangeAsync(report, expectedVersion, http, db, ct, eventType, action, link, afterMutation, detail);
    }

    private static async Task<IResult> ChangeAsync(ProblemReport report, long? expectedVersion, HttpContext http, AeroLinkDbContext db, CancellationToken ct, string eventType, Action<ProblemReport, AuthenticatedUser, DateTimeOffset> action, Func<AuthenticatedUser, DateTimeOffset, ProblemReportLink>? link = null,
        Func<ProblemReport, AuthenticatedUser, DateTimeOffset, ProblemReportLink?, ProblemReportRevision, CancellationToken, Task>? afterMutation = null,
        string? detail = null)
    {
        if (!await http.HasProjectAccessAsync(db, report.ProjectId, ct)) return Results.Forbid();
        if (expectedVersion is not null && expectedVersion != report.Version) return Results.Conflict(new { error = "This problem report changed after it was opened. Refresh before continuing.", code = "stale_version", currentVersion = report.Version });
        try
        {
            var now = DateTimeOffset.UtcNow; var actor = http.UserAccount();
            var wasAwaitingClosure = report.State == ProblemReportState.AwaitingSqaClosure;
            action(report, actor, now);
            ProblemReportLink? createdLink = null;
            if (link is not null) { createdLink = link(actor, now); db.ProblemReportLinks.Add(createdLink); }
            var revision = AddRevision(db, report, eventType, actor.UserName, now, detail);
            if (wasAwaitingClosure && report.ResolutionVerificationExecutionId is null)
                await new ProblemReportClosureCandidateService(db).InvalidatePendingAsync(report, actor.UserName,
                    eventType, now, ct);
            if (afterMutation is not null) await afterMutation(report, actor, now, createdLink, revision, ct);
            await db.SaveChangesAsync(ct); return Results.Ok(new { id = report.Id, displayNumber = report.DisplayNumber, state = report.State.ToString(), version = report.Version, snapshotHash = report.CanonicalHash() });
        }
        catch (DomainException ex) { return Results.BadRequest(new { error = ex.Message }); }
        catch (DbUpdateConcurrencyException) { return Results.Conflict(new { error = "This problem report was updated concurrently. Refresh before continuing.", code = "stale_version" }); }
    }

    private static ProblemReportRevision AddRevision(AeroLinkDbContext db, ProblemReport report, string eventType, string actor, DateTimeOffset now, string? detail = null)
    {
        // One evidence shape for every change, whether it arrives here or through a controlled checkout.
        var snapshot = ProblemReportControlledEditingAdapter.EvidenceSnapshot(report);
        var revision = new ProblemReportRevision(report.Id, report.Revision, eventType, actor, report.CanonicalHash(), snapshot, now, detail: detail);
        db.ProblemReportRevisions.Add(revision); return revision;
    }

    private static async Task<bool> LinkExistsInProjectAsync(string artifactType, Guid artifactId, Guid projectId, AeroLinkDbContext db, CancellationToken ct) => artifactType.Trim().ToLowerInvariant() switch
    {
        "requirement" => await db.Requirements.AnyAsync(x => x.Id == artifactId && x.ProjectId == projectId, ct),
        "changerequest" or "scr" or "swcr" => await db.SystemChangeRequests.AnyAsync(x => x.Id == artifactId && x.ProjectId == projectId, ct),
        "testchangerequest" or "tcr" => await db.TestChangeReviews.AnyAsync(x => x.Id == artifactId && x.ProjectId == projectId, ct),
        "testexecution" => await db.TestExecutions.AnyAsync(x => x.Id == artifactId && x.ProjectId == projectId, ct),
        "softwarebuild" or "build" => await db.SoftwareBuilds.AnyAsync(x => x.Id == artifactId && x.ProjectId == projectId, ct),
        "baseline" => await db.CandidateBaselines.AnyAsync(x => x.Id == artifactId && x.ProjectId == projectId, ct),
        "document" => await db.ControlledDocuments.AnyAsync(x => x.Id == artifactId && x.ProjectId == projectId, ct),
        "evidence" => await db.EvidenceRecords.AnyAsync(x => x.Id == artifactId && x.ProjectId == projectId, ct),
        "release" => await db.Releases.AnyAsync(x => x.Id == artifactId && x.ProjectId == projectId, ct),
        "problemreport" or "pr" => await db.ProblemReports.AnyAsync(x => x.Id == artifactId && x.ProjectId == projectId, ct),
        _ => false
    };

    private static string CanonicalLinkType(string artifactType) => artifactType.Trim().ToLowerInvariant() switch
    {
        "changerequest" or "change-request" or "scr" or "swcr" => "ChangeRequest",
        "testchangerequest" or "test-change-request" or "tcr" => "TestChangeRequest",
        "testexecution" or "test-execution" => "TestExecution",
        "softwarebuild" or "software-build" or "build" => "SoftwareBuild",
        "problemreport" or "problem-report" or "pr" => "ProblemReport",
        "requirement" => "Requirement",
        "baseline" => "Baseline",
        "document" => "Document",
        "evidence" => "Evidence",
        "release" => "Release",
        _ => artifactType.Trim()
    };

    private static object Summary(ProblemReport x, bool waived = false) => new { x.Id, x.ReportNumber, x.Revision, x.DisplayNumber, x.Title, state = x.State.ToString(), severity = x.Severity.ToString(), priority = x.Priority.ToString(), type = x.Type.ToString(), x.Classification, x.ReportedBy, x.ResponsibleEngineerId, x.TargetReleaseId, x.IsReleaseBlocker, waived, x.UpdatedAt, x.Version };
    private static object Detail(ProblemReport x, IEnumerable<ProblemReportLinkView> links,
        IEnumerable<ProblemReportRevision> revisions,
        IEnumerable<ProblemReportClosureCandidate>? closureCandidates = null,
        object? capabilities = null,
        IEnumerable<ReadinessWaiver>? releaseWaivers = null,
        ProblemReportDuplicateDiagnostic? duplicateDiagnostic = null)
    {
        var materializedLinks = links.ToList();
        var approvedCorrectiveActions = materializedLinks.Where(link => link.TrustedControlledEvidence && link.Relationship == ProblemReportRelationshipPolicy.ApprovedCorrectiveAction).Select(LinkResponse).ToList();
        var testEvidence = materializedLinks.Where(link => link.TrustedControlledEvidence
            && link.Relationship == ProblemReportRelationshipPolicy.ResolutionVerification
            && link.ArtifactId == x.ResolutionVerificationExecutionId).Select(LinkResponse).ToList();
        var now = DateTimeOffset.UtcNow; var waiverHistory = (releaseWaivers ?? []).ToList();
        var activeWaiver = waiverHistory.FirstOrDefault(item => item.IsActiveFor(x, now));
        return new { x.Id, x.ProjectId, x.ReportNumber, x.Revision, x.DisplayNumber, x.Title, x.Problem, x.ProblemRich, x.AdditionalInformation, x.AdditionalInformationRich, x.Analysis, x.ReportedBy, x.ResponsibleEngineerId, x.TargetReleaseId, x.Classification, severity = x.Severity.ToString(), priority = x.Priority.ToString(), x.Origin, x.AffectedConfiguration, x.RootCause, x.Effects, x.CorrectiveAction, x.Workaround, type = x.Type.ToString(), x.SystemAircraftImpact, x.ImpactAssessmentJson, disposition = x.Disposition?.ToString(), x.DispositionRationale, x.ResolutionVerificationExecutionId, x.ClosureApprovedByName, x.ClosureApprovedAt, x.IsReleaseBlocker, x.ReleaseBlockerVersion, waived = activeWaiver is not null, activeReleaseWaiver = activeWaiver is null ? null : WaiverResponse(activeWaiver, x, now), releaseWaivers = waiverHistory.Select(item => WaiverResponse(item, x, now)), legacyWaiver = string.IsNullOrWhiteSpace(x.WaiverRationale) ? null : new { provenance = "LegacyUnverified", rationale = x.WaiverRationale, x.WaivedBy, x.WaivedAt }, state = x.State.ToString(), x.CreatedAt, x.UpdatedAt, x.Version, snapshotHash = x.CanonicalHash(), snapshotSchemaVersion = ProblemReportEvidenceContract.SchemaVersion, capabilities, duplicateDiagnostic, links = materializedLinks.Select(LinkResponse), approvedCorrectiveActions, testEvidence, closureCandidates = (closureCandidates ?? []).Select(CandidateResponse), revisions = revisions.Select(x => new { x.Id, x.Revision, x.EventType, x.Actor, x.Detail, x.EvidenceJson, x.EventSchemaVersion, x.SnapshotSchemaVersion, x.SnapshotHash, x.SnapshotJson, x.OccurredAt }) };
    }

    private static object WaiverResponse(ReadinessWaiver item, ProblemReport report, DateTimeOffset now) => new
    {
        item.Id, item.BlockerRevision, item.BlockerVersion, item.Rationale, item.ApprovedByAccountId,
        item.ApprovedBy, item.ApprovalAuthority, item.SignatureMeaning, item.Provenance, item.CreatedAt,
        item.ExpiresAt, item.RevokedAt, item.RevokedBy, item.RevocationReason, active = item.IsActiveFor(report, now),
    };

    private static object CandidateResponse(ProblemReportClosureCandidate candidate) => new
    {
        candidate.Id,
        candidate.ReportRevision,
        candidate.Sequence,
        candidate.SchemaVersion,
        candidate.ReportSnapshotSchemaVersion,
        candidate.ReportVersion,
        candidate.VerificationExecutionId,
        candidate.ManifestHash,
        candidate.SelectedBy,
        candidate.SelectedAt,
        state = candidate.State.ToString(),
        candidate.InvalidatedBy,
        candidate.InvalidatedAt,
        candidate.InvalidationReason,
        candidate.ApprovedBy,
        candidate.ApprovedAt,
        candidate.PackageProvenance,
        candidate.ClosurePackageHash,
        approvalAuthority = ClosureApprovalAuthority(candidate.ClosurePackageJson),
    };

    private static async Task<bool> HasCurrentSqaClosureAuthorityAsync(ProblemReport report, HttpContext http,
        AeroLinkDbContext db, IdentityService identity, CancellationToken ct)
    {
        var actor = http.UserAccount();
        if (actor.IsAdministrator) return false;
        var programId = await db.Projects.AsNoTracking().Where(item => item.Id == report.ProjectId)
            .Select(item => (Guid?)item.ProgramId).SingleOrDefaultAsync(ct);
        return programId is not null && await identity.HasRoleAsync(actor.Id, programId.Value,
            ProgramRole.SoftwareQualityAnalyst, DateTimeOffset.UtcNow, ct);
    }

    private static async Task<ProblemReportOwnerStatus> ProblemReportOwnerStatusAsync(string userName,
        Guid programId, AeroLinkDbContext db, CancellationToken ct)
    {
        var account = await db.UserAccounts.AsNoTracking()
            .SingleOrDefaultAsync(item => item.UserName == userName, ct);
        if (account is null || account.State != AccountState.Active)
            return new(false, false);
        var roles = await db.ProgramMemberships.AsNoTracking()
            .Where(item => item.UserId == account.Id && item.ProgramId == programId && item.EndedAt == null)
            .Select(item => item.Role).ToListAsync(ct);
        return new(roles.Count > 0, ProblemReportOwnerAuthority.IsEligible(roles));
    }

    private static async Task<bool> HasProblemReportOwnerRecoveryAuthorityAsync(Guid userId, Guid programId,
        AeroLinkDbContext db, CancellationToken ct)
    {
        var roles = await db.ProgramMemberships.AsNoTracking()
            .Where(item => item.UserId == userId && item.ProgramId == programId && item.EndedAt == null)
            .Select(item => item.Role).ToListAsync(ct);
        return ProblemReportOwnerAuthority.CanRecover(roles);
    }

    private static async Task<string?> CurrentReleaseWaiverAuthorityAsync(ProblemReport report,
        AuthenticatedUser actor, AeroLinkDbContext db, IdentityService identity, DateTimeOffset now,
        CancellationToken ct)
    {
        if (actor.IsAdministrator) return null;
        var programId = await db.Projects.AsNoTracking().Where(item => item.Id == report.ProjectId)
            .Select(item => (Guid?)item.ProgramId).SingleOrDefaultAsync(ct);
        if (programId is null) return null;
        if (await identity.HasRoleAsync(actor.Id, programId.Value, ProgramRole.SoftwareQualityAnalyst, now, ct))
            return ProgramRole.SoftwareQualityAnalyst.ToString();
        if (await identity.HasRoleAsync(actor.Id, programId.Value, ProgramRole.ConfigurationManager, now, ct))
            return ProgramRole.ConfigurationManager.ToString();
        if (await identity.HasRoleAsync(actor.Id, programId.Value, ProgramRole.ProgramManager, now, ct))
            return ProgramRole.ProgramManager.ToString();
        return null;
    }

    private static string? ClosureApprovalAuthority(string packageJson)
    {
        if (string.IsNullOrWhiteSpace(packageJson)) return null;
        try
        {
            using var document = JsonDocument.Parse(packageJson);
            return document.RootElement.TryGetProperty("closure", out var closure)
                && closure.TryGetProperty("authority", out var authority)
                ? authority.GetString() : null;
        }
        catch (JsonException) { return null; }
    }

    private static object LinkResponse(ProblemReportLinkView link) => new { link.ArtifactType, link.ArtifactId, link.Identifier, link.Relationship, link.AddedBy, link.AddedAt };

    /// <summary>
    /// Where "record a passing successor execution" should actually take the reader.
    ///
    /// The button navigated to a generic System Verification workspace carrying nothing, so a software
    /// author arrived in the wrong discipline, on a tab about change impact, with no procedure, execution or
    /// report selected — the primary remediation call to action could not guide anyone to the evidence it
    /// was asking for.
    ///
    /// Resolved here rather than in the browser. A problem report raised from a failure links the execution
    /// that produced it, and that execution names the exact procedure revision; the discipline follows from
    /// that procedure's level rather than from a field somebody has to remember to set. One place computes
    /// it, so the button, the destination and any future caller cannot disagree.
    /// </summary>
    private static async Task<IResult> CorrectiveActionAsync(Guid id, HttpContext http, AeroLinkDbContext db, CancellationToken ct)
    {
        var report = await db.ProblemReports.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
        if (report is null) return Results.NotFound();
        if (!await http.HasProjectAccessAsync(db, report.ProjectId, ct)) return Results.Forbid();
        var scope = await new ProblemReportClosureVerificationPolicy(db).ResolveAsync(report, ct);
        var targetRevisionId = scope.PermittedProcedureRevisionIds.Count == 1
            ? scope.PermittedProcedureRevisionIds.Single()
            : (Guid?)null;
        var revision = targetRevisionId is null ? null : await db.TestProcedureRevisions.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == targetRevisionId, ct);
        var procedure = revision is null ? null : await db.TestProcedures.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == revision.ProcedureId, ct);
        var levels = await (from candidateRevision in db.TestProcedureRevisions.AsNoTracking()
                            join candidateProcedure in db.TestProcedures.AsNoTracking()
                                on candidateRevision.ProcedureId equals candidateProcedure.Id
                            where scope.PermittedProcedureRevisionIds.Contains(candidateRevision.Id)
                            select candidateProcedure.Level).Distinct().ToListAsync(ct);
        var procedureLevel = levels.Count == 1 ? levels[0] : (TestProcedureLevel?)null;
        var discipline = procedureLevel is null ? null
            : procedureLevel == TestProcedureLevel.System ? "system" : "software";
        string? procedureTitle = null;
        if (revision is not null)
            procedureTitle = (await TestProcedureRevisionTitleProjection.ForRevisionsAsync(db,
                [revision.Id], ct))[revision.Id].Title;
        var reason = scope.IsResolved
            ? procedure is not null
                ? $"Record a passing successor execution against {procedure.BaseNumber}, using the exact revision carried by the target build."
                : "Choose one of the controlled corrective procedures carried by the target build."
            : scope.Error!;

        return Results.Ok(new
        {
            problemReportId = report.Id,
            problemReportNumber = report.DisplayNumber,
            available = scope.IsResolved && discipline is not null,
            discipline,
            reason,
            verificationCode = scope.ErrorCode,
            executionId = scope.OriginExecutionId,
            procedureId = procedure?.Id ?? scope.ProcedureId,
            procedureRevisionId = revision?.Id,
            procedureNumber = procedure?.BaseNumber,
            procedureTitle,
            // Naming the authority a handoff needs, rather than only refusing.
            requiredRole = ProgramRole.TestEngineer.ToString(),
        });
    }

    private static async Task<IReadOnlyList<ProblemReportLinkView>> LinkViewsAsync(
        ProblemReport report, IEnumerable<ProblemReportLink> links, AeroLinkDbContext db, CancellationToken ct)
    {
        var materialized = links.ToList();
        var approvedIds = materialized.Where(link => ProblemReportRelationshipPolicy.Matches(link.Relationship, link.ArtifactType)
                && link.Relationship == ProblemReportRelationshipPolicy.ApprovedCorrectiveAction)
            .Select(link => link.ArtifactId).Distinct().ToList();
        var approved = (await db.SystemChangeRequests.AsNoTracking()
            .Where(item => approvedIds.Contains(item.Id) && item.ProjectId == report.ProjectId
                && (item.State == ChangeRequestState.Approved || item.State == ChangeRequestState.SelectedForBaseline))
            .Select(item => item.Id).ToListAsync(ct)).ToHashSet();
        var result = new List<ProblemReportLinkView>();
        foreach (var link in materialized)
        {
            var trusted = link.Relationship switch
            {
                ProblemReportRelationshipPolicy.ApprovedCorrectiveAction => approved.Contains(link.ArtifactId),
                ProblemReportRelationshipPolicy.ResolutionVerification => report.ResolutionVerificationExecutionId == link.ArtifactId
                    && ProblemReportRelationshipPolicy.Matches(link.Relationship, link.ArtifactType),
                _ => false,
            };
            result.Add(new(link.ArtifactType, link.ArtifactId,
                await ResolveLinkIdentifierAsync(link.ArtifactType, link.ArtifactId, db, ct),
                link.Relationship, link.AddedBy, link.AddedAt, trusted));
        }
        return result;
    }

    private static async Task<string?> ResolveLinkIdentifierAsync(
        string artifactType, Guid artifactId, AeroLinkDbContext db, CancellationToken ct)
    {
        switch (artifactType.Trim().ToLowerInvariant())
        {
            case "requirement":
                return await db.Requirements.AsNoTracking().Where(x => x.Id == artifactId)
                    .Select(x => x.BaseNumber).SingleOrDefaultAsync(ct);
            case "changerequest" or "scr" or "swcr":
            {
                var item = await db.SystemChangeRequests.AsNoTracking().Where(x => x.Id == artifactId)
                    .Select(x => new { x.BaseNumber, x.Revision }).SingleOrDefaultAsync(ct);
                return item is null ? null : $"{item.BaseNumber}.{item.Revision:D2}";
            }
            case "testchangerequest" or "tcr":
            {
                var item = await db.TestChangeReviews.AsNoTracking().Where(x => x.Id == artifactId)
                    .Select(x => new { x.BaseNumber, x.Revision }).SingleOrDefaultAsync(ct);
                return item is null ? null : $"{item.BaseNumber}.{item.Revision:D2}";
            }
            case "testexecution":
            {
                var item = await (from execution in db.TestExecutions.AsNoTracking().Where(x => x.Id == artifactId)
                                  join revision in db.TestProcedureRevisions.AsNoTracking() on execution.ProcedureRevisionId equals revision.Id
                                  join procedure in db.TestProcedures.AsNoTracking() on revision.ProcedureId equals procedure.Id
                                  select new { procedure.BaseNumber, revision.Revision }).SingleOrDefaultAsync(ct);
                return item is null ? null : $"{item.BaseNumber}.{item.Revision:D2}";
            }
            case "softwarebuild" or "build":
                return await db.SoftwareBuilds.AsNoTracking().Where(x => x.Id == artifactId)
                    .Select(x => x.BuildNumber).SingleOrDefaultAsync(ct);
            case "baseline":
            {
                var item = await db.CandidateBaselines.AsNoTracking().Where(x => x.Id == artifactId)
                    .Select(x => new { x.BaseNumber, x.Revision }).SingleOrDefaultAsync(ct);
                return item is null ? null : $"{item.BaseNumber}.{item.Revision:D2}";
            }
            case "document":
            {
                var item = await db.ControlledDocuments.AsNoTracking().Where(x => x.Id == artifactId)
                    .Select(x => new { x.DocumentNumber, x.Revision }).SingleOrDefaultAsync(ct);
                return item is null ? null : $"{item.DocumentNumber}.{item.Revision:D2}";
            }
            case "evidence":
                return await db.EvidenceRecords.AsNoTracking().Where(x => x.Id == artifactId)
                    .Select(x => x.OriginalFileName).SingleOrDefaultAsync(ct);
            case "release":
                var version = await db.Releases.AsNoTracking().Where(x => x.Id == artifactId)
                    .Select(x => x.Version).SingleOrDefaultAsync(ct);
                return version is null ? null : SoftwareBuildIdentifier.FromVersion(version);
            case "problemreport" or "pr":
            {
                var item = await db.ProblemReports.AsNoTracking().Where(x => x.Id == artifactId)
                    .Select(x => new { x.ReportNumber, x.Revision }).SingleOrDefaultAsync(ct);
                return item is null ? null : $"{item.ReportNumber}.{item.Revision:D2}";
            }
            default:
                return null;
        }
    }

    private sealed record ProblemReportLinkView(string ArtifactType, Guid ArtifactId, string? Identifier, string Relationship, string AddedBy, DateTimeOffset AddedAt, bool TrustedControlledEvidence);
    private sealed record CreateProblemReportRequest(Guid ProjectId, Guid? ReleaseId, string Title, string Problem, string? ProblemRich, string? AdditionalInformation, string? AdditionalInformationRich, string? Analysis, string? Classification, ProblemReportSeverity? Severity, ProblemReportPriority? Priority, string? Origin, string? AffectedConfiguration, string? SystemAircraftImpact, string? ImpactAssessmentJson);
    private sealed record CreateProblemReportFromExecutionRequest(Guid? ReleaseId, string? Title, string? Problem, string? Analysis, string? Classification, ProblemReportSeverity? Severity, ProblemReportPriority? Priority, string? AffectedConfiguration);
    private sealed record InvestigationRequest(long? ExpectedVersion, string Analysis, string? RootCause, string? Effects, string? Containment);
    private sealed record ResolutionRequest(long? ExpectedVersion, string CorrectiveAction);
    private sealed record VerificationRequest(long? ExpectedVersion, Guid TestExecutionId);
    private sealed record ClosureApprovalRequest(long? ExpectedVersion);
    private sealed record DispositionRequest(long? ExpectedVersion, ProblemReportDisposition Disposition, string Rationale, Guid? DuplicateOfId);
    private sealed record ReopenRequest(long? ExpectedVersion, string Rationale);
    private sealed record BlockerRequest(long? ExpectedVersion, bool IsReleaseBlocker, string? WaiverRationale);
    private sealed record ReleaseWaiverRequest(long? ExpectedVersion, string Rationale, DateTimeOffset ExpiresAt);
    private sealed record RevokeReleaseWaiverRequest(long? ExpectedVersion, string Reason);
    private sealed record LinkRequest(long? ExpectedVersion, string ArtifactType, Guid ArtifactId, string Relationship);
    private sealed record ReassignRequest(long? ExpectedVersion, string ResponsibleEngineerId);
    private sealed record ProblemReportOwnerStatus(bool ProgramMember, bool Eligible);
    private sealed record RetargetRequest(long? ExpectedVersion, Guid TargetReleaseId);
    private sealed record VersionRequest(long? ExpectedVersion);
}
