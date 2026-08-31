using System.Data;
using System.Data.Common;
using System.Text.Json;
using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Common;
using AeroLink.Domain.Content;
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
        // The vocabulary is served rather than duplicated in the browser. Nine meanings written out twice
        // is nine chances for the picker to explain a category differently from the record.
        group.MapGet("/categories", CategoriesAsync);
        group.MapGet("/linked/{artifactType}/{artifactId:guid}", LinkedAsync);
        group.MapPost("", CreateAsync);
        group.MapPost("/from-test-execution/{executionId:guid}", CreateFromFailureAsync);
        group.MapGet("/{id:guid}", DetailAsync);
        group.MapGet("/{id:guid}/history/{snapshotId:guid}", HistoricalAsync);
        group.MapGet("/{id:guid}/download", DownloadAsync);
        group.MapGet("/{id:guid}/corrective-action", CorrectiveActionAsync);
        // Details are edited under the universal controlled-editing lease, not here. A second write path to
        // the same fields was the whole defect: it let two people save over each other with nothing but an
        // expected version between them, while every other controlled record took an exclusive lease.
        // See /api/controlled-editing/checkout with artifactType=ProblemReport.
        group.MapPost("/{id:guid}/owner", ReassignAsync);
        group.MapPost("/{id:guid}/target-build", RetargetAsync);
        group.MapPost("/{id:guid}/ready-for-sccb", ReadyForSccbAsync);
        group.MapPost("/{id:guid}/sccb/open", OpenBySccbAsync);
        group.MapPost("/{id:guid}/transition", TransitionAsync);
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
        group.MapPost("/{id:guid}/related", LinkRelatedAsync);
        group.MapDelete("/{id:guid}/related/{relatedId:guid}", UnlinkRelatedAsync);
        group.MapGet("/{id:guid}/closure-package", ClosurePackageAsync);
        return app;
    }

    private static async Task<IResult> CreateAsync(CreateProblemReportRequest request, HttpContext http, AeroLinkDbContext db, IdentityService identity, CancellationToken ct)
    {
        if (!await http.HasProjectRoleAsync(db, identity, request.ProjectId, ct, ProgramRole.Engineer, ProgramRole.TestEngineer, ProgramRole.ConfigurationManager, ProgramRole.ProgramManager)) return Results.Forbid();
        if (request.ReleaseId is not null && !await db.Releases.AnyAsync(x => x.Id == request.ReleaseId && x.ProjectId == request.ProjectId, ct))
            return Results.BadRequest(new { error = "The selected build does not belong to this project." });
        var actor = http.UserAccount();
        var recoveryCutoff = DateTimeOffset.UtcNow.AddDays(-30);
        var referencedImages = new[] { request.ProblemRich, request.AdditionalInformationRich, request.AnalysisRich,
            request.RootCauseRich, request.WorkaroundRich, request.CorrectiveActionRich, request.SystemAircraftImpactRich,
            request.EffectsRich, request.ContainmentRich }
            .SelectMany(RichContent.ReferencedAttachments).Distinct().ToArray();
        if (referencedImages.Length > 0)
        {
            var available = await db.ControlledAttachments.AsNoTracking()
                .Where(image => referencedImages.Contains(image.Id) && image.ProjectId == request.ProjectId
                    && (image.ArtifactType == "InlineImage"
                        || (image.ArtifactType == "InlineImageDraft" && image.UploadedBy == actor.UserName))
                    && image.State != ControlledAttachmentState.Withdrawn)
                .Select(image => image.Id).ToListAsync(ct);
            if (referencedImages.Except(available).Any())
                return Results.BadRequest(new { error = "Every inline image must belong to this Project and remain available." });
        }
        await using var transaction = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, ct);
        try
        {
            var now = DateTimeOffset.UtcNow;
            var item = new ProblemReport(request.ProjectId, await IdentifierAllocator.NextProblemReportAsync(db, ct), request.Title, request.Problem, request.Analysis ?? "", actor.UserName, now,
                request.Classification ?? "Software anomaly", request.Severity ?? ProblemReportSeverity.Major, request.Priority ?? ProblemReportPriority.Normal, request.Origin ?? "Manual report", request.AffectedConfiguration ?? "",
                request.ReleaseId, actor.UserName, request.ProblemRich ?? "", request.AdditionalInformation ?? "", request.AdditionalInformationRich ?? "", request.SystemAircraftImpact ?? "", request.ImpactAssessmentJson ?? "{}",
                request.Category);
            // Applied through the same controlled path an edit takes, so a report raised whole and one
            // corrected afterwards cannot end up with different rules about what may be written.
            item.AuthorOnCreate(new ProblemReportNarrative(
                request.AnalysisRich, request.RootCauseRich, request.WorkaroundRich,
                request.CorrectiveActionRich, request.SystemAircraftImpactRich,
                request.Effects, request.EffectsRich, request.Containment, request.ContainmentRich),
                request.RootCause, request.CorrectiveAction, request.Workaround, now);
            if (referencedImages.Length > 0)
            {
                var commitImages = await db.ControlledAttachments
                    .Where(image => referencedImages.Contains(image.Id) && image.ProjectId == request.ProjectId
                        && (image.ArtifactType == "InlineImage" || image.ArtifactType == "InlineImageDraft")
                        && image.State != ControlledAttachmentState.Withdrawn)
                    .ToListAsync(ct);
                if (commitImages.Any(image => image.ArtifactType == "InlineImageDraft"
                        && image.UploadedAt < recoveryCutoff))
                    return Results.BadRequest(new
                    {
                        error = "One or more browser-recovery images expired after 30 days; upload them again before saving the Problem Report.",
                        code = "inline_image_recovery_expired",
                    });
                if (referencedImages.Except(commitImages.Select(image => image.Id)).Any()
                    || commitImages.Any(image => image.ArtifactType == "InlineImageDraft" && image.UploadedBy != actor.UserName))
                    return Results.BadRequest(new { error = "Every inline image must belong to this Project and remain available." });
                foreach (var image in commitImages.Where(image => image.ArtifactType == "InlineImageDraft"))
                    image.ClaimInlineImage(item.Id, null);
            }
            db.ProblemReports.Add(item);
            if (request.ReleaseId is not null)
                db.ProblemReportLinks.Add(ProblemReportRelationshipPolicy.CreateControlled(item.Id, "Release", request.ReleaseId.Value,
                    ProblemReportRelationshipPolicy.BuildScope, ProblemReportRelationshipProducer.TargetBuildWorkflow, actor.UserName, now));
            await AddRevisionAsync(db, item, "ProblemReportCreated", actor.UserName, now, ct, actorDisplayName: actor.DisplayName);
            await db.SaveChangesAsync(ct); await transaction.CommitAsync(ct);
            return Results.Created($"/api/problem-reports/{item.Id}", await DetailResponseAsync(item, [], [], db, ct));
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
                request.Priority ?? ProblemReportPriority.High, "Test execution", request.AffectedConfiguration ?? execution.Configuration, releaseId, actor.UserName,
                // Deliberately unclassified unless the caller says otherwise. A failed execution means the
                // code is wrong or the test is wrong, and which of those it is — 3x against 5x — is exactly
                // the judgement nobody has made yet at the moment the failure is recorded. The Draft to
                // Ready-for-SCCB gate asks for it once somebody has looked.
                category: request.Category);
            db.ProblemReports.Add(item); db.ProblemReportLinks.Add(ProblemReportRelationshipPolicy.CreateControlled(item.Id, "TestExecution", execution.Id,
                ProblemReportRelationshipPolicy.OriginatingFailure, ProblemReportRelationshipProducer.FailureCreationWorkflow, actor.UserName, now));
            if (releaseId is not null)
                db.ProblemReportLinks.Add(ProblemReportRelationshipPolicy.CreateControlled(item.Id, "Release", releaseId.Value,
                    ProblemReportRelationshipPolicy.BuildScope, ProblemReportRelationshipProducer.TargetBuildWorkflow, actor.UserName, now));
            await AddRevisionAsync(db, item, "ProblemReportCreatedFromFailedExecution", actor.UserName, now, ct, actorDisplayName: actor.DisplayName);
            await db.SaveChangesAsync(ct); await transaction.CommitAsync(ct);
            var identifier = await ResolveLinkIdentifierAsync("TestExecution", execution.Id, db, ct);
            return Results.Created($"/api/problem-reports/{item.Id}", await DetailResponseAsync(item,
                [new ProblemReportLinkView("TestExecution", execution.Id, identifier, ProblemReportRelationshipPolicy.OriginatingFailure, actor.UserName, now, false)], [], db, ct));
        }
        catch (DomainException ex) { return Results.BadRequest(new { error = ex.Message }); }
        catch (DbUpdateException) { return Results.Conflict(new { error = "A problem report number was allocated concurrently. Retry the create request.", code = "number_allocation_conflict" }); }
    }

    /// <summary>
    /// The controlled category vocabulary. Fixed and identical on every Project, so this needs no Project
    /// scope and no authority beyond being signed in — it describes what the words mean, not what any
    /// record says.
    /// </summary>
    private static IResult CategoriesAsync() => Results.Ok(new
    {
        families = ProblemReportCategoryVocabulary.Families,
        categories = ProblemReportCategoryVocabulary.Definitions.Select(definition => new
        {
            value = definition.Category.ToString(),
            definition.Code,
            definition.Family,
            definition.Label,
            definition.Meaning,
        }),
    });

    private static async Task<IResult> ListAsync(Guid projectId, Guid? targetReleaseId, bool? targetUnassigned, string? search, ProblemReportState? state, ProblemReportSeverity? severity, ProblemReportPriority? priority, string? owner, bool? blockersOnly, ProblemReportCategory? category, string? categoryFamily, int? page, int? pageSize, HttpContext http, AeroLinkDbContext db, CancellationToken ct)
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
        if (category is not null) query = query.Where(x => x.Category == category);
        // Narrowing to a family is one click for "every code defect", which is the question people
        // actually ask. The family lives in the vocabulary, not the database, so it is resolved to the
        // categories it covers and applied as a set — there is no family column to drift out of step.
        if (!string.IsNullOrWhiteSpace(categoryFamily))
        {
            var members = ProblemReportCategoryVocabulary.Definitions
                .Where(definition => string.Equals(definition.Family, categoryFamily.Trim(), StringComparison.OrdinalIgnoreCase))
                .Select(definition => (ProblemReportCategory?)definition.Category).ToList();
            if (members.Count == 0) return Results.BadRequest(new { error = $"'{categoryFamily}' is not a Problem Report category family.", code = "pr_category_family_unknown" });
            query = query.Where(x => members.Contains(x.Category));
        }
        if (!string.IsNullOrWhiteSpace(owner)) { var normalizedOwner = owner.Trim().ToLower(); query = query.Where(x => x.ResponsibleEngineerId.ToLower().Contains(normalizedOwner)); }
        if (blockersOnly == true) query = query.Where(x => x.IsReleaseBlocker);
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim(); var lowered = term.ToLower();
            // DisplayNumber ("PR-00013.00") is a computed identity, so an exact number.revision search is
            // translated onto the mapped columns. The general substring match remains an OR, so a title or
            // description containing a dot-terminated number keeps working.
            var separator = term.LastIndexOf('.');
            string? exactNumber = null; int? exactRevision = null;
            if (separator > 0 && int.TryParse(term[(separator + 1)..], out var parsedRevision))
            {
                exactNumber = term[..separator];
                exactRevision = parsedRevision;
            }
            query = query.Where(x => x.ReportNumber.ToLower().Contains(lowered) || x.Title.ToLower().Contains(lowered)
                || x.Problem.ToLower().Contains(lowered) || x.RootCause.ToLower().Contains(lowered)
                || (exactNumber != null && exactRevision != null
                    && x.ReportNumber.ToLower() == exactNumber.ToLower() && x.Revision == exactRevision.Value));
        }
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
        // One lookup for the page, not one per row: the register shows a person per row, and this list is
        // already bounded by the page size. Both fields here are live assignments, so the current directory
        // name is the right answer for them.
        var listNames = await DirectoryIdentityProjection.DisplayNamesAsync(db,
            items.SelectMany(row => new[] { row.Report.ReportedBy, row.Report.ResponsibleEngineerId }), ct);
        return Results.Ok(new { page = current, pageSize = size, totalCount = total, totalPages,
            items = items.Select(row => Summary(row.Report, row.Waived, listNames)) });
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
        var attentionRows = active.OrderByDescending(x => x.IsReleaseBlocker).ThenByDescending(x => x.Severity)
            .ThenBy(x => x.CreatedAt).Take(12).ToList();
        return Results.Ok(new
        {
            generatedAt = DateTimeOffset.UtcNow,
            summary = new { total = reports.Count, active = active.Count, closureAwaitingApproval = reports.Count(x => x.State == ProblemReportState.WaitingForSqaToClose), closed = reports.Count(x => x.State == ProblemReportState.Closed), releaseBlockers = reports.Count(x => x.IsReleaseBlocker && !IsWaived(x)), waivedBlockers = reports.Count(x => x.IsReleaseBlocker && IsWaived(x)) },
            bySeverity = reports.GroupBy(x => x.Severity).OrderBy(x => x.Key).Select(x => new { severity = x.Key.ToString(), count = x.Count() }),
            byState = reports.GroupBy(x => x.State).OrderBy(x => x.Key).Select(x => new { state = x.Key.ToString(), count = x.Count() }),
            attention = attentionRows.Select(x => Summary(x, IsWaived(x)))
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
        var snapshotIds = await CurrentSnapshotIdsAsync(permitted, db, ct);
        return Results.Ok(permitted.Select(x => Summary(x,
            snapshotId: snapshotIds.TryGetValue(x.Id, out var snapshotId) ? snapshotId : null)));
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
        var canApproveSqaClosure = report.State == ProblemReportState.WaitingForSqaToClose
            && await HasCurrentSqaClosureAuthorityAsync(report, http, db, identity, ct)
            && !string.Equals(http.UserAccount().UserName, report.ReportedBy, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(http.UserAccount().UserName, report.ResponsibleEngineerId, StringComparison.OrdinalIgnoreCase);
        var waiverAuthority = await CurrentReleaseWaiverAuthorityAsync(report, http.UserAccount(), db, identity,
            DateTimeOffset.UtcNow, ct);
        var canApproveReleaseWaiver = report.IsReleaseBlocker
            && report.State is not (ProblemReportState.Closed or ProblemReportState.Rejected)
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
        var impactAreas = await new ProblemReportImpactProjection(db).BuildAsync(report, links, ct);
        var relatedReports = await RelatedReportsAsync(report, links, db, ct);
        var transitions = await AvailableTransitionsAsync(report, actor, db, identity, ct);
        // Reviving a finished report is the existing reopen, not a new authority: Closed → Verifying and
        // Rejected → Draft are the only edges out of a terminal state, both are SQA-only, and both already
        // demand a rationale. So the answer to "may this person revive it" is whether that one edge came
        // back available. Deriving it here rather than in the browser keeps one authority for the question.
        var reviveTarget = ReviveTarget(report.State);
        var canRevive = reviveTarget is not null
            && transitions.Any(transition => string.Equals(transition.State, reviveTarget, StringComparison.Ordinal));
        // One set-wise directory lookup for the live assignment fields on this authorized record. The
        // immutable events below are NOT resolved here — each carries the name captured when it happened.
        var currentNames = await DirectoryIdentityProjection.DisplayNamesAsync(
            db, [report.ReportedBy, report.ResponsibleEngineerId], ct);
        return Results.Ok(await DetailResponseAsync(report, await LinkViewsAsync(report, links, db, ct), revisions, db, ct,
            candidates.OrderByDescending(x => x.ReportRevision).ThenByDescending(x => x.Sequence),
            impactAreas: impactAreas,
            relatedReports: relatedReports,
            new
            {
                canApproveSqaClosure,
                availableTransitions = transitions,
                canRevive,
                reviveTargetState = canRevive ? reviveTarget : null,
                canApproveReleaseWaiver,
                releaseWaiverAuthority = waiverAuthority,
                ownerEligible = ownerAuthority.Eligible,
                ownerAuthorityException = ownerAuthority.Eligible ? null : "The assigned owner no longer has accountable Problem Report authority in this Program.",
                canReassignOwner = string.Equals(actor.UserName, report.ResponsibleEngineerId, StringComparison.OrdinalIgnoreCase) || canRecoverOwner,
                canRecoverOwner,
            }, waivers, duplicateDiagnostic, currentNames));
    }

    private static async Task<IResult> HistoricalAsync(Guid id, Guid snapshotId, HttpContext http,
        AeroLinkDbContext db, CancellationToken ct)
    {
        // The parent report is used only to establish the Project authorization boundary. Every displayed
        // value below comes from the immutable revision row; in particular, never substitute the current
        // aggregate when a historical envelope is missing, malformed, or fails its stored digest check.
        var projectId = await db.ProblemReports.AsNoTracking().Where(x => x.Id == id)
            .Select(x => (Guid?)x.ProjectId).SingleOrDefaultAsync(ct);
        if (projectId is null) return Results.NotFound();
        if (!await http.HasProjectAccessAsync(db, projectId.Value, ct)) return Results.Forbid();

        var row = await db.ProblemReportRevisions.AsNoTracking()
            .Where(x => x.Id == snapshotId && x.ProblemReportId == id)
            .SingleOrDefaultAsync(ct);
        if (row is null || string.IsNullOrWhiteSpace(row.SnapshotJson)
            || row.SnapshotSchemaVersion is < 1 or > ProblemReportEvidenceContract.SchemaVersion
            || !string.Equals(ProblemReportEvidenceContract.Hash(row.SnapshotJson), row.SnapshotHash,
                StringComparison.OrdinalIgnoreCase)) return Results.NotFound();
        var parsed = ProblemReportOutputGenerator.ReadStoredSnapshot(row.SnapshotJson, row.SnapshotSchemaVersion);
        if (parsed is null || parsed.Value.Snapshot.Id != id || parsed.Value.Snapshot.ProjectId != projectId.Value
            || parsed.Value.Snapshot.Revision != row.Revision) return Results.NotFound();
        var snapshot = parsed.Value.Snapshot;
        var category = HistoricalCategoryResponse(snapshot.Category, snapshot.CategoryProvenance);
        var revision = new
        {
            row.Id, row.Revision, row.EventType, row.Actor, row.ActorDisplayName, row.Detail,
            rationale = string.IsNullOrWhiteSpace(row.Rationale) ? row.Detail : row.Rationale,
            row.FromState, row.ToState, row.EvidenceJson, row.EventSchemaVersion,
            row.SnapshotSchemaVersion, row.SnapshotHash, row.SnapshotJson, row.OccurredAt,
        };
        return Results.Ok(new
        {
            snapshot.Id, snapshot.ProjectId, snapshot.ReportNumber, snapshot.Revision, snapshot.DisplayNumber,
            snapshot.Title, snapshot.Problem, snapshot.ProblemRich, snapshot.AdditionalInformation,
            snapshot.AdditionalInformationRich, snapshot.Analysis, snapshot.AnalysisRich, snapshot.ReportedBy,
            reportedByDisplayName = snapshot.ReportedBy, snapshot.ResponsibleEngineerId,
            responsibleEngineerDisplayName = snapshot.ResponsibleEngineerId, snapshot.TargetReleaseId,
            snapshot.Classification, severity = snapshot.Severity, priority = snapshot.Priority, snapshot.Origin,
            snapshot.AffectedConfiguration, snapshot.RootCause, snapshot.RootCauseRich, snapshot.Effects,
            snapshot.EffectsRich, snapshot.Containment, snapshot.ContainmentRich, snapshot.CorrectiveAction,
            snapshot.CorrectiveActionRich, snapshot.Workaround, snapshot.WorkaroundRich,
            snapshot.SystemAircraftImpact, snapshot.SystemAircraftImpactRich, snapshot.ImpactAssessmentJson,
            snapshot.Disposition, snapshot.DispositionRationale, snapshot.ResolutionVerificationExecutionId,
            snapshot.ClosureApprovedByName, snapshot.ClosureApprovedAt, snapshot.IsReleaseBlocker,
            snapshot.ReleaseBlockerVersion, waived = false, activeReleaseWaiver = (object?)null,
            releaseWaivers = Array.Empty<object>(), legacyWaiver = (object?)null, state = snapshot.State,
            snapshot.CreatedAt, snapshot.UpdatedAt, snapshot.Version, category,
            snapshotHash = row.SnapshotHash, snapshotSchemaVersion = row.SnapshotSchemaVersion,
            snapshotId = row.Id, historicalReadOnly = true, historicalLegacyType = parsed.Value.LegacyType,
            capabilities = new
            {
                canApproveSqaClosure = false, canApproveReleaseWaiver = false, canReassignOwner = false,
                canRecoverOwner = false, canRevive = false, availableTransitions = Array.Empty<object>(),
            }, duplicateDiagnostic = (object?)null, impactAreas = Array.Empty<object>(),
            relatedReports = Array.Empty<object>(), links = Array.Empty<object>(),
            approvedCorrectiveActions = Array.Empty<object>(), testEvidence = Array.Empty<object>(),
            closureCandidates = Array.Empty<object>(), revisions = new[] { revision },
            supportingAttachments = snapshot.SupportingAttachments ?? [],
        });
    }

    private static async Task<IResult> DownloadAsync(Guid id, int? revision, Guid? snapshotId, string? format, HttpContext http,
        AeroLinkDbContext db, ProblemReportOutputGenerator generator, CancellationToken ct)
    {
        var projectId = await db.ProblemReports.AsNoTracking().Where(x => x.Id == id)
            .Select(x => (Guid?)x.ProjectId).SingleOrDefaultAsync(ct);
        if (projectId is null) return Results.NotFound();
        if (!await http.HasProjectAccessAsync(db, projectId.Value, ct)) return Results.Forbid();
        var requestedFormat = string.IsNullOrWhiteSpace(format) ? "docx" : format.Trim().ToLowerInvariant();
        if (requestedFormat is not ("docx" or "pdf"))
            return Results.BadRequest(new { error = "Problem Report output format must be docx or pdf." });
        var output = await generator.GenerateAsync(id, revision, snapshotId, requestedFormat, ct);
        return output is null ? Results.NotFound() : Results.File(output.Content, output.ContentType, output.FileName);
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
            var fromState = ProblemReportTransitionPolicy.Canonical(report.State);
            var wasAwaitingClosure = fromState == ProblemReportState.WaitingForSqaToClose;
            report.Retarget(actor.UserName, request.TargetReleaseId, now);
            var existing = await db.ProblemReportLinks.Where(x => x.ProblemReportId == id && x.ArtifactType == "Release" && x.Relationship == ProblemReportRelationshipPolicy.BuildScope).ToListAsync(ct);
            db.ProblemReportLinks.RemoveRange(existing);
            db.ProblemReportLinks.Add(ProblemReportRelationshipPolicy.CreateControlled(id, "Release", request.TargetReleaseId,
                ProblemReportRelationshipPolicy.BuildScope, ProblemReportRelationshipProducer.TargetBuildWorkflow, actor.UserName, now));
            var targetState = ProblemReportTransitionPolicy.Canonical(report.State);
            var relationshipRationale = wasAwaitingClosure
                ? "Target build correction invalidated the prior closure evidence."
                : null;
            await AddRevisionAsync(db, report, "TargetBuildChanged", actor.UserName, now, ct,
                fromState: fromState, toState: targetState, rationale: relationshipRationale, actorDisplayName: actor.DisplayName);
            if (wasAwaitingClosure)
                await new ProblemReportClosureCandidateService(db).InvalidatePendingAsync(report, actor.UserName,
                    "TargetBuildChanged", now, ct, fromState, targetState, relationshipRationale, actorDisplayName: actor.DisplayName);
            await db.SaveChangesAsync(ct);
            var snapshot = await ProblemReportAttachmentEvidence.SnapshotAsync(db, report, ct);
            return Results.Ok(new { id = report.Id, displayNumber = report.DisplayNumber, state = ProblemReportTransitionPolicy.Canonical(report.State).ToString(), version = report.Version, snapshotHash = snapshot.Hash });
        }
        catch (DomainException ex) { return Results.BadRequest(new { error = ex.Message }); }
        catch (DbUpdateConcurrencyException) { return Results.Conflict(new { error = "This problem report was updated concurrently. Refresh before continuing.", code = "stale_version" }); }
    }

    private static Task<IResult> ReadyForSccbAsync(Guid id, VersionRequest request, HttpContext http, AeroLinkDbContext db, CancellationToken ct) =>
        ChangeAsync(id, request.ExpectedVersion, http, db, ct, "ReadyForSccb", (report, actor, now) => report.ReadyForSccb(actor.UserName, now));

    private static async Task<IResult> OpenBySccbAsync(Guid id, VersionRequest request, HttpContext http, AeroLinkDbContext db, CancellationToken ct)
    {
        var report = await db.ProblemReports.SingleOrDefaultAsync(x => x.Id == id, ct); if (report is null) return Results.NotFound();
        if (!await http.HasProjectAccessAsync(db, report.ProjectId, ct)) return Results.Forbid();
        if (http.UserAccount().IsAdministrator || !await HasSccbOpeningAuthorityAsync(report, http.UserAccount(), db, ct)) return Results.Forbid();
        return await ChangeAsync(report, request.ExpectedVersion, http, db, ct, "OpenedBySccb", (item, actor, now) => item.OpenBySccb(actor.UserName, now));
    }

    private static async Task<IResult> TransitionAsync(Guid id, TransitionRequest request, HttpContext http,
        AeroLinkDbContext db, IdentityService identity, CancellationToken ct)
    {
        var report = await db.ProblemReports.SingleOrDefaultAsync(x => x.Id == id, ct);
        if (report is null) return Results.NotFound();
        if (!await http.HasProjectAccessAsync(db, report.ProjectId, ct)) return Results.Forbid();
        var requested = request.TargetState ?? request.State;
        if (!Enum.TryParse<ProblemReportState>(requested, true, out var parsed))
            return Results.BadRequest(new { error = "Choose one of the eight supported Problem Report states.", code = "pr_state_invalid" });
        var target = ProblemReportTransitionPolicy.Canonical(parsed);
        if (target == ProblemReportState.Closed)
            return await CloseWithCandidateAsync(id, request.ExpectedVersion, request.Rationale, http, db, identity,
                "ProblemReportTransitionedToClosed", ct);
        if (ProblemReportTransitionPolicy.IsSccbOpening(report.State, target))
        {
            if (http.UserAccount().IsAdministrator || !await HasSccbOpeningAuthorityAsync(report, http.UserAccount(), db, ct))
                return Results.Forbid();
        }
        if (ProblemReportTransitionPolicy.IsSqaOnly(report.State, target))
        {
            if (http.UserAccount().IsAdministrator || !await HasCurrentSqaClosureAuthorityAsync(report, http, db, identity, ct))
                return Results.Forbid();
            if (target == ProblemReportState.Closed
                && (string.Equals(http.UserAccount().UserName, report.ReportedBy, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(http.UserAccount().UserName, report.ResponsibleEngineerId, StringComparison.OrdinalIgnoreCase)))
                return Results.Forbid();
        }
        var acceptedRationale = ProblemReportTransitionPolicy.RequiresRationale(report.State, target)
            ? request.Rationale
            : null;
        return await ChangeAsync(report, request.ExpectedVersion, http, db, ct,
            $"ProblemReportTransitionedTo{target}",
            (item, actor, now) =>
            {
                item.TransitionTo(target, actor.UserName, acceptedRationale, now);
            },
            detail: acceptedRationale, rationale: acceptedRationale);
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

    private static Task<IResult> ApproveClosureAsync(Guid id, ClosureApprovalRequest request, HttpContext http,
        AeroLinkDbContext db, IdentityService identity, CancellationToken ct) =>
        CloseWithCandidateAsync(id, request.ExpectedVersion, rationale: null, http, db, identity, "ClosureApproved", ct);

    private static async Task<IResult> DispositionAsync(Guid id, DispositionRequest request, HttpContext http, AeroLinkDbContext db, IdentityService identity, CancellationToken ct)
    {
        var report = await db.ProblemReports.SingleOrDefaultAsync(item => item.Id == id, ct);
        if (report is null) return Results.NotFound();
        if (!await http.HasProjectAccessAsync(db, report.ProjectId, ct)) return Results.Forbid();
        if (request.ExpectedVersion is not null && request.ExpectedVersion != report.Version)
            return Results.Conflict(new { error = "This problem report changed after it was opened. Refresh before continuing.", code = "stale_version", currentVersion = report.Version });
        if (request.Disposition != ProblemReportDisposition.Rejected)
            return Results.BadRequest(new { error = "Only the Rejected Problem Report state is writable through this compatibility route.", code = "pr_legacy_disposition_read_only" });
        if (request.DuplicateOfId is not null)
            return Results.BadRequest(new { error = "Duplicate relationships are historical and read-only.", code = "pr_duplicate_target_read_only" });
        if (report.State == ProblemReportState.WaitingForSqaToClose
            && (http.UserAccount().IsAdministrator || !await HasCurrentSqaClosureAuthorityAsync(report, http, db, identity, ct)))
            return Results.Forbid();
        return await ChangeAsync(report, request.ExpectedVersion, http, db, ct, "DispositionRecorded",
            (item, actor, now) => item.ApplyDisposition(actor.UserName, ProblemReportDisposition.Rejected,
                request.Rationale, null, now));
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
    private static async Task<IResult> ReopenAsync(Guid id, ReopenRequest request, HttpContext http, AeroLinkDbContext db, IdentityService identity, CancellationToken ct)
    {
        var report = await db.ProblemReports.SingleOrDefaultAsync(x => x.Id == id, ct);
        if (report is null) return Results.NotFound();
        if (!await http.HasProjectAccessAsync(db, report.ProjectId, ct)) return Results.Forbid();
        var target = ProblemReportTransitionPolicy.Canonical(report.State) switch
        {
            ProblemReportState.Closed => ProblemReportState.Verifying,
            ProblemReportState.Rejected => ProblemReportState.Draft,
            _ => (ProblemReportState?)null,
        };
        if (target is null) return Results.BadRequest(new { error = "Only Closed reports can return to Verifying and only Rejected reports can return to Draft.", code = "pr_reopen_state_invalid" });
        if (http.UserAccount().IsAdministrator || !await HasCurrentSqaClosureAuthorityAsync(report, http, db, identity, ct)) return Results.Forbid();
        return await ChangeAsync(report, request.ExpectedVersion, http, db, ct, "ProblemReportReopened",
            (item, actor, now) => item.TransitionTo(target.Value, actor.UserName, request.Rationale, now), detail: request.Rationale, rationale: request.Rationale);
    }
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

    /// <summary>
    /// Links two Problem Reports that belong together.
    ///
    /// Written on both records in one transaction, because a relationship that only one side knows about
    /// is a relationship the other side's reader will never find — and the reason to record it at all is
    /// that somebody looking at either report should see the other.
    ///
    /// Deliberately not reachable through the generic links endpoint: this is a controlled relationship
    /// with its own producer, exactly like the duplicate disposition beside it.
    /// </summary>
    private static async Task<IResult> LinkRelatedAsync(Guid id, RelatedProblemReportRequest request,
        HttpContext http, AeroLinkDbContext db, CancellationToken ct)
    {
        if (id == request.RelatedProblemReportId)
            return Results.BadRequest(new { error = "A Problem Report cannot be related to itself.", code = "pr_related_self" });

        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        var report = await db.ProblemReports.SingleOrDefaultAsync(x => x.Id == id, ct);
        if (report is null) return Results.NotFound();
        if (!await http.HasProjectAccessAsync(db, report.ProjectId, ct)) return Results.Forbid();
        if (request.ExpectedVersion is not null && request.ExpectedVersion != report.Version)
            return Results.Conflict(new { error = "This problem report changed after it was opened. Refresh before continuing.", code = "stale_version", currentVersion = report.Version });

        var related = await db.ProblemReports.SingleOrDefaultAsync(x => x.Id == request.RelatedProblemReportId, ct);
        // Same Project, like the duplicate disposition. A relationship reaching across Projects would be
        // visible to people who cannot open half of it, and nobody has asked for one.
        if (related is null || related.ProjectId != report.ProjectId)
            return Results.BadRequest(new { error = "The related Problem Report must exist in this Project.", code = "pr_related_not_in_project" });

        if (await db.ProblemReportLinks.AnyAsync(link => link.ProblemReportId == report.Id
                && link.ArtifactType == "ProblemReport" && link.ArtifactId == related.Id
                && link.Relationship == ProblemReportRelationshipPolicy.RelatedProblemReport, ct))
            return Results.Conflict(new { error = "These Problem Reports are already related.", code = "pr_related_duplicate" });

        try
        {
            var now = DateTimeOffset.UtcNow;
            var actor = http.UserAccount();
            // Both sides are a controlled relationship change, so a closure candidate pending on either
            // report is invalidated: its reviewed basis no longer describes what the record links to.
            foreach (var (subject, other) in new[] { (report, related), (related, report) })
            {
                var fromState = ProblemReportTransitionPolicy.Canonical(subject.State);
                var invalidated = subject.PrepareControlledRelationshipChange(actor.UserName, now);
                db.ProblemReportLinks.Add(ProblemReportRelationshipPolicy.CreateControlled(subject.Id,
                    "ProblemReport", other.Id, ProblemReportRelationshipPolicy.RelatedProblemReport,
                    ProblemReportRelationshipProducer.RelatedProblemReportWorkflow, actor.UserName, now));
                var toState = ProblemReportTransitionPolicy.Canonical(subject.State);
                var rationale = invalidated
                    ? "Relating another Problem Report invalidated the prior closure evidence."
                    : null;
                await AddRevisionAsync(db, subject, "RelatedProblemReportLinked", actor.UserName, now, ct,
                    detail: $"Related to {other.DisplayNumber}.",
                    fromState: fromState, toState: toState, rationale: rationale, actorDisplayName: actor.DisplayName);
                if (invalidated)
                    await new ProblemReportClosureCandidateService(db).InvalidatePendingAsync(subject, actor.UserName,
                        "RelatedProblemReportLinked", now, ct, fromState, toState, rationale, actorDisplayName: actor.DisplayName);
            }
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return Results.Created($"/api/problem-reports/{id}/related/{related.Id}",
                new { relatedProblemReportId = related.Id, related.DisplayNumber, version = report.Version });
        }
        catch (DomainException ex) { return Results.BadRequest(new { error = ex.Message }); }
    }

    /// <summary>Removes both halves, because a one-sided removal would leave the other report asserting a
    /// relationship that no longer exists.</summary>
    private static async Task<IResult> UnlinkRelatedAsync(Guid id, Guid relatedId, HttpContext http,
        AeroLinkDbContext db, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        var report = await db.ProblemReports.SingleOrDefaultAsync(x => x.Id == id, ct);
        if (report is null) return Results.NotFound();
        if (!await http.HasProjectAccessAsync(db, report.ProjectId, ct)) return Results.Forbid();
        var related = await db.ProblemReports.SingleOrDefaultAsync(x => x.Id == relatedId, ct);
        if (related is null || related.ProjectId != report.ProjectId) return Results.NotFound();

        var links = await db.ProblemReportLinks.Where(link =>
            link.ArtifactType == "ProblemReport"
            && link.Relationship == ProblemReportRelationshipPolicy.RelatedProblemReport
            && ((link.ProblemReportId == id && link.ArtifactId == relatedId)
                || (link.ProblemReportId == relatedId && link.ArtifactId == id))).ToListAsync(ct);
        if (links.Count == 0) return Results.NotFound();

        try
        {
            var now = DateTimeOffset.UtcNow;
            var actor = http.UserAccount();
            db.ProblemReportLinks.RemoveRange(links);
            foreach (var (subject, other) in new[] { (report, related), (related, report) })
            {
                var fromState = ProblemReportTransitionPolicy.Canonical(subject.State);
                var invalidated = subject.PrepareControlledRelationshipChange(actor.UserName, now);
                var toState = ProblemReportTransitionPolicy.Canonical(subject.State);
                var rationale = invalidated
                    ? "Removing a related Problem Report invalidated the prior closure evidence."
                    : null;
                await AddRevisionAsync(db, subject, "RelatedProblemReportUnlinked", actor.UserName, now, ct,
                    detail: $"No longer related to {other.DisplayNumber}.",
                    fromState: fromState, toState: toState, rationale: rationale, actorDisplayName: actor.DisplayName);
                if (invalidated)
                    await new ProblemReportClosureCandidateService(db).InvalidatePendingAsync(subject, actor.UserName,
                        "RelatedProblemReportUnlinked", now, ct, fromState, toState, rationale, actorDisplayName: actor.DisplayName);
            }
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return Results.Ok(new { relatedProblemReportId = relatedId, version = report.Version });
        }
        catch (DomainException ex) { return Results.BadRequest(new { error = ex.Message }); }
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
            var fromState = ProblemReportTransitionPolicy.Canonical(report.State);
            var wasAwaitingClosure = fromState == ProblemReportState.WaitingForSqaToClose;
            report.RecordContextLink(actor.UserName, now);
            db.ProblemReportLinks.Add(ProblemReportRelationshipPolicy.CreateGenericContext(report.Id, canonicalType, request.ArtifactId, relationship!, actor.UserName, now));
            var targetState = ProblemReportTransitionPolicy.Canonical(report.State);
            var relationshipRationale = wasAwaitingClosure
                ? "Contextual relationship change invalidated the prior closure evidence."
                : null;
            await AddRevisionAsync(db, report, "ContextArtifactLinked", actor.UserName, now, ct,
                fromState: fromState, toState: targetState, rationale: relationshipRationale, actorDisplayName: actor.DisplayName);
            if (wasAwaitingClosure)
                await new ProblemReportClosureCandidateService(db).InvalidatePendingAsync(report, actor.UserName,
                    "ContextArtifactLinked", now, ct, fromState, targetState, relationshipRationale, actorDisplayName: actor.DisplayName);
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

    private static async Task<IResult> CloseWithCandidateAsync(Guid id, long? expectedVersion, string? rationale,
        HttpContext http, AeroLinkDbContext db, IdentityService identity, string eventType, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        try
        {
            var report = await ProblemReportLock.AcquireAsync(db, id, ct);
            if (report is null) return Results.NotFound();
            if (!await http.HasProjectAccessAsync(db, report.ProjectId, ct)) return Results.Forbid();
            var actor = http.UserAccount();
            if (actor.IsAdministrator || !await HasCurrentSqaClosureAuthorityAsync(report, actor, db, identity, ct))
                return Results.Forbid();
            if (string.Equals(actor.UserName, report.ReportedBy, StringComparison.OrdinalIgnoreCase)
                || string.Equals(actor.UserName, report.ResponsibleEngineerId, StringComparison.OrdinalIgnoreCase))
                return Results.Forbid();
            if (expectedVersion is not null && expectedVersion != report.Version)
                return Results.Conflict(new { error = "This problem report changed after it was opened. Refresh before continuing.", code = "stale_version", currentVersion = report.Version });

            // Candidate validation and the Closed mutation share the same serializable row lock as every
            // Problem Report supporting-file mutation. An upload either wins first and invalidates this
            // candidate, or approval wins first and the upload observes the now-finished report.
            var candidateDecision = await new ProblemReportClosureCandidateService(db).ValidateForApprovalAsync(report, ct);
            if (!candidateDecision.Accepted && candidateDecision.Candidate is not null)
                return Results.Conflict(new { error = candidateDecision.Error, code = candidateDecision.Code });
            var candidate = candidateDecision.Accepted ? candidateDecision.Candidate : null;
            var now = DateTimeOffset.UtcNow;
            var fromState = ProblemReportTransitionPolicy.Canonical(report.State);
            report.ApproveClosure(actor.UserName, actor.Id, now);
            var toState = ProblemReportTransitionPolicy.Canonical(report.State);
            var transitionRationale = LifecycleTransitionRationale(eventType, fromState, toState, rationale);
            var revision = await AddRevisionAsync(db, report, eventType, actor.UserName, now, ct,
                detail: transitionRationale, fromState: fromState, toState: toState,
                rationale: transitionRationale, actorDisplayName: actor.DisplayName);
            if (candidate is not null)
                await new ProblemReportClosureCandidateService(db).FreezeForApprovalAsync(report, candidate,
                    revision, actor.UserName, actor.Id, ProgramRole.SoftwareQualityAnalyst.ToString(), now, ct);
            await db.SaveChangesAsync(ct);
            var snapshot = await ProblemReportAttachmentEvidence.SnapshotAsync(db, report, ct);
            await transaction.CommitAsync(ct);
            return Results.Ok(new { id = report.Id, displayNumber = report.DisplayNumber,
                state = ProblemReportTransitionPolicy.Canonical(report.State).ToString(), version = report.Version,
                snapshotHash = snapshot.Hash });
        }
        catch (DomainException ex) { return Results.BadRequest(new { error = ex.Message }); }
        catch (InvalidOperationException ex)
        {
            return Results.Conflict(new { error = ex.Message, code = "pr_closure_candidate_stale" });
        }
        catch (DbUpdateConcurrencyException)
        {
            return Results.Conflict(new { error = "This Problem Report changed concurrently. Refresh before continuing.", code = "stale_version" });
        }
        catch (DbUpdateException)
        {
            return Results.Conflict(new { error = "This Problem Report changed concurrently. Refresh before continuing.", code = "stale_version" });
        }
        catch (Exception ex) when (ProblemReportLock.IsSerializationConflict(ex))
        {
            return Results.Conflict(new { error = "This Problem Report changed concurrently. Refresh before continuing.", code = "stale_version" });
        }
    }

    private static async Task<IResult> ChangeAsync(Guid id, long? expectedVersion, HttpContext http, AeroLinkDbContext db, CancellationToken ct, string eventType, Action<ProblemReport, AuthenticatedUser, DateTimeOffset> action, Func<AuthenticatedUser, DateTimeOffset, ProblemReportLink>? link = null,
         Func<ProblemReport, AuthenticatedUser, DateTimeOffset, ProblemReportLink?, ProblemReportRevision, CancellationToken, Task>? afterMutation = null,
         string? detail = null, string? rationale = null)
    {
        var report = await db.ProblemReports.SingleOrDefaultAsync(x => x.Id == id, ct); if (report is null) return Results.NotFound();
        return await ChangeAsync(report, expectedVersion, http, db, ct, eventType, action, link, afterMutation, detail, rationale);
    }

    private static async Task<IResult> ChangeAsync(ProblemReport report, long? expectedVersion, HttpContext http, AeroLinkDbContext db, CancellationToken ct, string eventType, Action<ProblemReport, AuthenticatedUser, DateTimeOffset> action, Func<AuthenticatedUser, DateTimeOffset, ProblemReportLink>? link = null,
         Func<ProblemReport, AuthenticatedUser, DateTimeOffset, ProblemReportLink?, ProblemReportRevision, CancellationToken, Task>? afterMutation = null,
         string? detail = null, string? rationale = null)
    {
        if (!await http.HasProjectAccessAsync(db, report.ProjectId, ct)) return Results.Forbid();
        if (expectedVersion is not null && expectedVersion != report.Version) return Results.Conflict(new { error = "This problem report changed after it was opened. Refresh before continuing.", code = "stale_version", currentVersion = report.Version });
        try
        {
            var now = DateTimeOffset.UtcNow; var actor = http.UserAccount();
            var fromState = ProblemReportTransitionPolicy.Canonical(report.State);
            var wasAwaitingClosure = report.State == ProblemReportState.WaitingForSqaToClose;
            action(report, actor, now);
            ProblemReportLink? createdLink = null;
            if (link is not null) { createdLink = link(actor, now); db.ProblemReportLinks.Add(createdLink); }
            var toState = ProblemReportTransitionPolicy.Canonical(report.State);
            var transitionRationale = LifecycleTransitionRationale(eventType, fromState, toState, rationale ?? detail);
            var revision = await AddRevisionAsync(db, report, eventType, actor.UserName, now, ct, detail,
                fromState, toState, transitionRationale, actorDisplayName: actor.DisplayName);
            if (wasAwaitingClosure && report.ResolutionVerificationExecutionId is null)
                await new ProblemReportClosureCandidateService(db).InvalidatePendingAsync(report, actor.UserName,
                    eventType, now, ct, fromState, toState, transitionRationale, actorDisplayName: actor.DisplayName);
            if (afterMutation is not null) await afterMutation(report, actor, now, createdLink, revision, ct);
            await db.SaveChangesAsync(ct);
            var snapshot = await ProblemReportAttachmentEvidence.SnapshotAsync(db, report, ct);
            return Results.Ok(new { id = report.Id, displayNumber = report.DisplayNumber, state = ProblemReportTransitionPolicy.Canonical(report.State).ToString(), version = report.Version, snapshotHash = snapshot.Hash });
        }
        catch (DomainException ex) { return Results.BadRequest(new { error = ex.Message }); }
        catch (DbUpdateConcurrencyException) { return Results.Conflict(new { error = "This problem report was updated concurrently. Refresh before continuing.", code = "stale_version" }); }
    }

    // `actorDisplayName` is resolved server-side from the authenticated session, never from the request body.
    // It is captured here so the event keeps the name that was true when it happened; see
    // ProblemReportRevision.ActorDisplayName for why it is not resolved at read time instead.
    private static async Task<ProblemReportRevision> AddRevisionAsync(AeroLinkDbContext db, ProblemReport report, string eventType, string actor, DateTimeOffset now, CancellationToken ct, string? detail = null,
        ProblemReportState? fromState = null, ProblemReportState? toState = null, string? rationale = null,
        string? actorDisplayName = null)
    {
        // One evidence shape for every change, whether it arrives here or through a controlled checkout.
        var evidence = await ProblemReportAttachmentEvidence.SnapshotAsync(db, report, ct);
        var revision = new ProblemReportRevision(report.Id, report.Revision, eventType, actor, evidence.Hash, evidence.Json, now, detail: detail,
            fromState: fromState?.ToString(), toState: toState?.ToString(), rationale: rationale,
            actorDisplayName: actorDisplayName);
        db.ProblemReportRevisions.Add(revision); return revision;
    }

    private static string? LifecycleTransitionRationale(string eventType, ProblemReportState from,
        ProblemReportState to, string? supplied)
    {
        if (from == to || !ProblemReportTransitionPolicy.RequiresRationale(from, to)) return supplied;
        if (!string.IsNullOrWhiteSpace(supplied)) return supplied.Trim();
        if (from == ProblemReportState.WaitingForSqaToClose && to == ProblemReportState.Verifying)
            return eventType switch
            {
                "ResponsibleEngineerReassigned" => "The responsible-engineer change invalidated the prior closure basis and returned the report to Verifying.",
                "ReleaseBlockerRaised" => "Raising the release blocker invalidated the prior closure basis and returned the report to Verifying.",
                "ReleaseBlockerCleared" => "Clearing the release blocker invalidated the prior closure basis and returned the report to Verifying.",
                "ReleaseBlockerWaiverApproved" => "The release-waiver decision invalidated the prior closure basis and returned the report to Verifying.",
                "ReleaseBlockerWaiverRevoked" => "Revoking the release waiver invalidated the prior closure basis and returned the report to Verifying.",
                _ => $"The {eventType} change invalidated the prior closure basis and returned the report to Verifying.",
            };
        return $"The {eventType} change moved the report from {from} to {to}.";
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

    private static object Summary(ProblemReport x, bool waived = false,
        IReadOnlyDictionary<string, string>? currentNames = null, Guid? snapshotId = null)
    {
        var liveNames = currentNames ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        return new { x.Id, x.ReportNumber, x.Revision, x.DisplayNumber, snapshotId, x.Title, state = ProblemReportTransitionPolicy.Canonical(x.State).ToString(), severity = x.Severity.ToString(), priority = x.Priority.ToString(), category = CategoryResponse(x), x.Classification, x.ReportedBy, reportedByDisplayName = liveNames.Current(x.ReportedBy), x.ResponsibleEngineerId, responsibleEngineerDisplayName = liveNames.Current(x.ResponsibleEngineerId), x.TargetReleaseId, x.IsReleaseBlocker, waived, x.UpdatedAt, x.Version };
    }

    /// <summary>
    /// Returns only immutable snapshots that can be served by the strict historical route. A Problem Report
    /// display number is not enough to select a row: multiple lifecycle events may share one aggregate
    /// revision, and a malformed or legacy row must never be advertised as an exact link.
    /// </summary>
    private static async Task<IReadOnlyDictionary<Guid, Guid>> CurrentSnapshotIdsAsync(
        IEnumerable<ProblemReport> reports, AeroLinkDbContext db, CancellationToken ct)
    {
        var reportRows = reports.ToList();
        if (reportRows.Count == 0) return new Dictionary<Guid, Guid>();
        var reportIds = reportRows.Select(report => report.Id).ToArray();
        var rows = await db.ProblemReportRevisions.AsNoTracking()
            .Where(row => reportIds.Contains(row.ProblemReportId))
            .Select(row => new
            {
                row.Id,
                row.ProblemReportId,
                row.Revision,
                row.SnapshotJson,
                row.SnapshotHash,
                row.SnapshotSchemaVersion,
                row.OccurredAt,
            }).ToListAsync(ct);
        var exact = new Dictionary<Guid, Guid>();
        foreach (var report in reportRows)
        {
            foreach (var row in rows.Where(row => row.ProblemReportId == report.Id && row.Revision == report.Revision)
                         .OrderByDescending(row => row.OccurredAt).ThenByDescending(row => row.Id))
            {
                if (string.IsNullOrWhiteSpace(row.SnapshotJson)
                    || row.SnapshotSchemaVersion is < 1 or > ProblemReportEvidenceContract.SchemaVersion
                    || !string.Equals(ProblemReportEvidenceContract.Hash(row.SnapshotJson), row.SnapshotHash,
                        StringComparison.OrdinalIgnoreCase)) continue;
                var parsed = ProblemReportOutputGenerator.ReadStoredSnapshot(row.SnapshotJson, row.SnapshotSchemaVersion);
                if (parsed is null || parsed.Value.Snapshot.Id != report.Id
                    || parsed.Value.Snapshot.ProjectId != report.ProjectId
                    || parsed.Value.Snapshot.Revision != report.Revision) continue;
                exact[report.Id] = row.Id;
                break;
            }
        }
        return exact;
    }
    private static async Task<object> DetailResponseAsync(ProblemReport x, IEnumerable<ProblemReportLinkView> links,
        IEnumerable<ProblemReportRevision> revisions, AeroLinkDbContext db, CancellationToken ct,
        IEnumerable<ProblemReportClosureCandidate>? closureCandidates = null,
        IReadOnlyList<ProblemReportImpactArea>? impactAreas = null,
        IReadOnlyList<object>? relatedReports = null,
        object? capabilities = null,
        IEnumerable<ReadinessWaiver>? releaseWaivers = null,
        ProblemReportDuplicateDiagnostic? duplicateDiagnostic = null,
        IReadOnlyDictionary<string, string>? currentNames = null)
    {
        var currentSnapshot = await ProblemReportAttachmentEvidence.SnapshotAsync(db, x, ct);
        // Current directory names for the live assignment fields only. Historical events below read their own
        // captured name instead, so a rename cannot rewrite them. See DirectoryIdentityProjection.
        var liveNames = currentNames ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var materializedLinks = links.ToList();
        var approvedCorrectiveActions = materializedLinks.Where(link => link.TrustedControlledEvidence && link.Relationship == ProblemReportRelationshipPolicy.ApprovedCorrectiveAction).Select(LinkResponse).ToList();
        var testEvidence = materializedLinks.Where(link => link.TrustedControlledEvidence
            && link.Relationship == ProblemReportRelationshipPolicy.ResolutionVerification
            && link.ArtifactId == x.ResolutionVerificationExecutionId).Select(LinkResponse).ToList();
        var now = DateTimeOffset.UtcNow; var waiverHistory = (releaseWaivers ?? []).ToList();
        var activeWaiver = waiverHistory.FirstOrDefault(item => item.IsActiveFor(x, now));
        return new { x.Id, x.ProjectId, x.ReportNumber, x.Revision, x.DisplayNumber, x.Title, x.Problem, x.ProblemRich, x.AdditionalInformation, x.AdditionalInformationRich, x.Analysis, x.ReportedBy, reportedByDisplayName = liveNames.Current(x.ReportedBy), x.ResponsibleEngineerId, responsibleEngineerDisplayName = liveNames.Current(x.ResponsibleEngineerId), x.TargetReleaseId, x.Classification, severity = x.Severity.ToString(), priority = x.Priority.ToString(), x.Origin, x.AffectedConfiguration, x.RootCause, x.RootCauseRich, x.Effects, x.EffectsRich, x.Containment, x.ContainmentRich, x.CorrectiveAction, x.CorrectiveActionRich, x.Workaround, x.WorkaroundRich, x.AnalysisRich, category = CategoryResponse(x), x.SystemAircraftImpact, x.SystemAircraftImpactRich, x.ImpactAssessmentJson, disposition = x.Disposition?.ToString(), x.DispositionRationale, x.ResolutionVerificationExecutionId, x.ClosureApprovedByName, x.ClosureApprovedAt, x.IsReleaseBlocker, x.ReleaseBlockerVersion, waived = activeWaiver is not null, activeReleaseWaiver = activeWaiver is null ? null : WaiverResponse(activeWaiver, x, now), releaseWaivers = waiverHistory.Select(item => WaiverResponse(item, x, now)), legacyWaiver = string.IsNullOrWhiteSpace(x.WaiverRationale) ? null : new { provenance = "LegacyUnverified", rationale = x.WaiverRationale, x.WaivedBy, x.WaivedAt }, state = ProblemReportTransitionPolicy.Canonical(x.State).ToString(), x.CreatedAt, x.UpdatedAt, x.Version, snapshotHash = currentSnapshot.Hash, snapshotSchemaVersion = ProblemReportEvidenceContract.SchemaVersion, capabilities, duplicateDiagnostic,
            // Each slot arrives complete — identifier, live state and target build. A response carrying only
            // ids would force the browser into a follow-up call per artifact, and the states it showed
            // would then be read at different instants from one another.
            impactAreas = impactAreas ?? [],
            relatedReports = relatedReports ?? [], links = materializedLinks.Select(LinkResponse), approvedCorrectiveActions, testEvidence, closureCandidates = (closureCandidates ?? []).Select(CandidateResponse), revisions = revisions.Select(x => new { x.Id, x.Revision, x.EventType, x.Actor, x.ActorDisplayName, x.Detail, rationale = string.IsNullOrWhiteSpace(x.Rationale) ? x.Detail : x.Rationale, x.FromState, x.ToState, x.EvidenceJson, x.EventSchemaVersion, x.SnapshotSchemaVersion, x.SnapshotHash, x.SnapshotJson, x.OccurredAt }) };
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
        candidate.ReportSnapshotHash,
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
        return await HasCurrentSqaClosureAuthorityAsync(report, http.UserAccount(), db, identity, ct);
    }

    private static async Task<bool> HasCurrentSqaClosureAuthorityAsync(ProblemReport report, AuthenticatedUser actor,
        AeroLinkDbContext db, IdentityService identity, CancellationToken ct)
    {
        if (actor.IsAdministrator) return false;
        var programId = await db.Projects.AsNoTracking().Where(item => item.Id == report.ProjectId)
            .Select(item => (Guid?)item.ProgramId).SingleOrDefaultAsync(ct);
        return programId is not null && await identity.HasRoleAsync(actor.Id, programId.Value,
            ProgramRole.SoftwareQualityAnalyst, DateTimeOffset.UtcNow, ct);
    }

    private static async Task<bool> HasSccbOpeningAuthorityAsync(ProblemReport report, AuthenticatedUser actor,
        AeroLinkDbContext db, CancellationToken ct)
    {
        if (actor.IsAdministrator) return false;
        var programId = await db.Projects.AsNoTracking().Where(item => item.Id == report.ProjectId)
            .Select(item => (Guid?)item.ProgramId).SingleOrDefaultAsync(ct);
        return programId is not null && await HasRoleAsync(programId.Value);

        async Task<bool> HasRoleAsync(Guid id)
        {
            var resolver = new ProjectAuthorityResolver(db);
            var now = DateTimeOffset.UtcNow;
            foreach (var role in ProblemReportTransitionPolicy.SccbOpeningRoles)
                if (await resolver.IsSatisfiedAsync(actor.Id, id,
                        ProjectAuthorityRequirement.LegacyRoleDemand(role), now, ct)) return true;
            return false;
        }
    }

    /// <summary>One available lifecycle edge. Typed rather than anonymous so the capability projection
    /// can ask which edges came back without reflecting over the response it is about to serialize.</summary>
    private sealed record TransitionOption(string State, bool RequiresRationale);

    private static async Task<IReadOnlyList<TransitionOption>> AvailableTransitionsAsync(ProblemReport report,
        AuthenticatedUser actor, AeroLinkDbContext db, IdentityService identity, CancellationToken ct)
    {
        var sccb = await HasSccbOpeningAuthorityAsync(report, actor, db, ct);
        var sqa = await HasCurrentSqaClosureAuthorityAsync(report, actor, db, identity, ct);
        var transitions = new List<TransitionOption>();
        foreach (var target in ProblemReportTransitionPolicy.AllowedTargets(report.State))
        {
            var permitted = ProblemReportTransitionPolicy.IsSccbOpening(report.State, target) ? sccb
                : ProblemReportTransitionPolicy.IsSqaOnly(report.State, target) ? sqa
                : true;
            if (target == ProblemReportState.Closed
                && (string.Equals(actor.UserName, report.ReportedBy, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(actor.UserName, report.ResponsibleEngineerId, StringComparison.OrdinalIgnoreCase)))
                permitted = false;
            if (permitted)
                transitions.Add(new TransitionOption(target.ToString(),
                    ProblemReportTransitionPolicy.RequiresRationale(report.State, target)));
        }
        return transitions;
    }

    /// <summary>
    /// The state a finished report is revived into, or null when it is not finished. Closed work resumes
    /// at Verifying because the correction it describes was already made and only its evidence is in
    /// question; a rejected report goes back to Draft because the judgement being undone is that there
    /// was anything to do at all. Both edges already exist in the transition policy — this only names
    /// them so one gesture can offer the reopen and the editor together.
    /// </summary>
    private static string? ReviveTarget(ProblemReportState state) =>
        ProblemReportTransitionPolicy.Canonical(state) switch
        {
            ProblemReportState.Closed => nameof(ProblemReportState.Verifying),
            ProblemReportState.Rejected => nameof(ProblemReportState.Draft),
            _ => null,
        };

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

    /// <summary>
    /// Whether this person may take back a Problem Report whose owner has gone.
    ///
    /// Recovery follows the accountable position, not the discipline. Before #816 an ordinary
    /// <c>EngineeringManager</c> or <c>ProgramManager</c> membership carried it, so anybody granted the job
    /// could reassign work they were not accountable for, and the retired <c>ProjectEngineeringLead</c> row
    /// carried it indefinitely. It now takes the Project Engineer, Engineering Manager or Program Manager
    /// leadership — primary or standing backup — resolved through the one resolver so this gate cannot
    /// drift from the rest of the model.
    /// </summary>
    private static async Task<bool> HasProblemReportOwnerRecoveryAuthorityAsync(Guid userId, Guid programId,
        AeroLinkDbContext db, CancellationToken ct)
    {
        var resolver = new ProjectAuthorityResolver(db);
        var now = DateTimeOffset.UtcNow;
        foreach (var position in ProblemReportOwnerAuthority.RecoveryPositions)
            if (await resolver.IsSatisfiedAsync(userId, programId, ProjectAuthorityRequirement.Leadership(position), now, ct))
                return true;
        return false;
    }

    private static async Task<string?> CurrentReleaseWaiverAuthorityAsync(ProblemReport report,
        AuthenticatedUser actor, AeroLinkDbContext db, IdentityService identity, DateTimeOffset now,
        CancellationToken ct)
    {
        if (actor.IsAdministrator) return null;
        var programId = await db.Projects.AsNoTracking().Where(item => item.Id == report.ProjectId)
            .Select(item => (Guid?)item.ProgramId).SingleOrDefaultAsync(ct);
        if (programId is null) return null;
        var resolver = new ProjectAuthorityResolver(db);
        if (await resolver.IsSatisfiedAsync(actor.Id, programId.Value,
                ProjectAuthorityRequirement.BaseRole(ProgramRole.SoftwareQualityAnalyst), now, ct))
            return ProgramRole.SoftwareQualityAnalyst.ToString();
        if (await resolver.IsSatisfiedAsync(actor.Id, programId.Value,
                ProjectAuthorityRequirement.Leadership(ProjectLeadershipPosition.ConfigurationManager), now, ct))
            return ProgramRole.ConfigurationManager.ToString();
        if (await resolver.IsSatisfiedAsync(actor.Id, programId.Value,
                ProjectAuthorityRequirement.Leadership(ProjectLeadershipPosition.ProgramManager), now, ct))
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

    /// <summary>
    /// The category as the browser needs it: the stored name, its two-digit code, its family, its label,
    /// and how the value got there. Null when a Draft has not been classified yet, which is a real answer
    /// and is rendered as one rather than as a blank.
    /// </summary>
    private static object? CategoryResponse(ProblemReport report)
    {
        if (report.Category is not { } category) return null;
        var definition = ProblemReportCategoryVocabulary.Of(category);
        return new
        {
            value = category.ToString(),
            definition.Code,
            definition.Family,
            definition.Label,
            definition.Meaning,
            provenance = report.CategoryProvenance?.ToString(),
        };
    }

    private static object? HistoricalCategoryResponse(string? value, string? provenance)
    {
        if (!ProblemReportCategoryVocabulary.TryParse(value, out var category)) return null;
        var definition = ProblemReportCategoryVocabulary.Of(category);
        return new
        {
            value = category.ToString(),
            definition.Code,
            definition.Family,
            definition.Label,
            definition.Meaning,
            provenance,
        };
    }

    /// <summary>
    /// The Problem Reports related to this one, each with the live state a reader needs to know whether
    /// it still matters. One query for the whole set rather than one per link.
    /// </summary>
    private static async Task<IReadOnlyList<object>> RelatedReportsAsync(ProblemReport report,
        IReadOnlyList<ProblemReportLink> links, AeroLinkDbContext db, CancellationToken ct)
    {
        var relatedIds = links
            .Where(link => link.ArtifactType == "ProblemReport"
                && link.Relationship == ProblemReportRelationshipPolicy.RelatedProblemReport)
            .Select(link => link.ArtifactId).Distinct().ToList();
        if (relatedIds.Count == 0) return [];
        var releases = await db.Releases.AsNoTracking().Where(item => item.ProjectId == report.ProjectId)
            .ToDictionaryAsync(item => item.Id, item => item.Version, ct);
        var related = await db.ProblemReports.AsNoTracking().Where(item => relatedIds.Contains(item.Id))
            .OrderBy(item => item.NumberSequence).ToListAsync(ct);
        // This panel sits on the same page as the record's own identity block and names a person per row, so
        // it has to be named the same way. One lookup for the set, which is already bounded by the links.
        var relatedNames = await DirectoryIdentityProjection.DisplayNamesAsync(db,
            related.Select(item => item.ReportedBy), ct);
        var snapshotIds = await CurrentSnapshotIdsAsync(related, db, ct);
        return related
            .Select(item => (object)new
            {
                item.Id,
                item.DisplayNumber,
                snapshotId = snapshotIds.TryGetValue(item.Id, out var exactSnapshotId) ? exactSnapshotId : (Guid?)null,
                item.Title,
                state = ProblemReportTransitionPolicy.Canonical(item.State).ToString(),
                severity = item.Severity.ToString(),
                item.ReportedBy,
                reportedByDisplayName = relatedNames.Current(item.ReportedBy),
                targetBuild = item.TargetReleaseId is { } releaseId && releases.TryGetValue(releaseId, out var version) ? version : "",
            }).ToList();
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
        var artifactNoun = procedureLevel == TestProcedureLevel.System ? "procedure" : "case";
        string? procedureTitle = null;
        if (revision is not null)
            procedureTitle = (await TestProcedureRevisionTitleProjection.ForRevisionsAsync(db,
                [revision.Id], ct))[revision.Id].Title;
        var reason = scope.IsResolved
            ? procedure is not null
                ? $"Record a passing successor execution against {procedure.BaseNumber}, using the exact revision carried by the target build."
                : $"Choose one of the controlled corrective {artifactNoun}s carried by the target build."
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
            artifactId = procedure?.Id ?? scope.ProcedureId,
            artifactRevisionId = revision?.Id,
            artifactNumber = procedure?.BaseNumber,
            artifactTitle = procedureTitle,
            artifactKind = procedureLevel == TestProcedureLevel.System ? "Procedure" : "Case",
            procedureId = procedure?.Id ?? scope.ProcedureId, // compatibility alias
            procedureRevisionId = revision?.Id, // compatibility alias
            procedureNumber = procedure?.BaseNumber, // compatibility alias
            procedureTitle, // compatibility alias
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
        var identifiers = await ResolveLinkIdentifiersAsync(materialized, db, ct);
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
            var identifierType = CanonicalLinkIdentifierType(link.ArtifactType);
            result.Add(new(link.ArtifactType, link.ArtifactId,
                identifiers.TryGetValue((identifierType, link.ArtifactId), out var identifier) ? identifier : null,
                link.Relationship, link.AddedBy, link.AddedAt, trusted));
        }
        return result;
    }

    /// <summary>
    /// Resolves all identifiers needed by one report detail projection in one query per canonical artifact
    /// type. The returned keys retain the resolver's existing accepted aliases, while the link list itself
    /// remains untouched so its original order, spelling and trust calculation are preserved.
    /// </summary>
    private static async Task<Dictionary<(string ArtifactType, Guid ArtifactId), string?>> ResolveLinkIdentifiersAsync(
        IReadOnlyList<ProblemReportLink> links, AeroLinkDbContext db, CancellationToken ct)
    {
        var identifiers = new Dictionary<(string ArtifactType, Guid ArtifactId), string?>();
        foreach (var group in links.GroupBy(link => CanonicalLinkIdentifierType(link.ArtifactType)))
        {
            var ids = group.Select(link => link.ArtifactId).Distinct().ToList();
            switch (group.Key)
            {
                case "requirement":
                    foreach (var item in await db.Requirements.AsNoTracking().Where(x => ids.Contains(x.Id))
                                 .Select(x => new { x.Id, Identifier = x.BaseNumber }).ToListAsync(ct))
                        identifiers[(group.Key, item.Id)] = item.Identifier;
                    break;
                case "changerequest":
                    foreach (var item in await db.SystemChangeRequests.AsNoTracking().Where(x => ids.Contains(x.Id))
                                 .Select(x => new { x.Id, x.BaseNumber, x.Revision }).ToListAsync(ct))
                        identifiers[(group.Key, item.Id)] = $"{item.BaseNumber}.{item.Revision:D2}";
                    break;
                case "testchangerequest":
                    foreach (var item in await db.TestChangeReviews.AsNoTracking().Where(x => ids.Contains(x.Id))
                                 .Select(x => new { x.Id, x.BaseNumber, x.Revision }).ToListAsync(ct))
                        identifiers[(group.Key, item.Id)] = $"{item.BaseNumber}.{item.Revision:D2}";
                    break;
                case "testexecution":
                    foreach (var item in await (from execution in db.TestExecutions.AsNoTracking()
                                                .Where(x => ids.Contains(x.Id))
                                                join revision in db.TestProcedureRevisions.AsNoTracking()
                                                    on execution.ProcedureRevisionId equals revision.Id
                                                join procedure in db.TestProcedures.AsNoTracking()
                                                    on revision.ProcedureId equals procedure.Id
                                                select new { execution.Id, procedure.BaseNumber, revision.Revision }).ToListAsync(ct))
                        identifiers[(group.Key, item.Id)] = $"{item.BaseNumber}.{item.Revision:D2}";
                    break;
                case "softwarebuild":
                    foreach (var item in await db.SoftwareBuilds.AsNoTracking().Where(x => ids.Contains(x.Id))
                                 .Select(x => new { x.Id, Identifier = x.BuildNumber }).ToListAsync(ct))
                        identifiers[(group.Key, item.Id)] = item.Identifier;
                    break;
                case "baseline":
                    foreach (var item in await db.CandidateBaselines.AsNoTracking().Where(x => ids.Contains(x.Id))
                                 .Select(x => new { x.Id, x.BaseNumber, x.Revision }).ToListAsync(ct))
                        identifiers[(group.Key, item.Id)] = $"{item.BaseNumber}.{item.Revision:D2}";
                    break;
                case "document":
                    foreach (var item in await db.ControlledDocuments.AsNoTracking().Where(x => ids.Contains(x.Id))
                                 .Select(x => new { x.Id, x.DocumentNumber, x.Revision }).ToListAsync(ct))
                        identifiers[(group.Key, item.Id)] = $"{item.DocumentNumber}.{item.Revision:D2}";
                    break;
                case "evidence":
                    foreach (var item in await db.EvidenceRecords.AsNoTracking().Where(x => ids.Contains(x.Id))
                                 .Select(x => new { x.Id, Identifier = x.OriginalFileName }).ToListAsync(ct))
                        identifiers[(group.Key, item.Id)] = item.Identifier;
                    break;
                case "release":
                    foreach (var item in await db.Releases.AsNoTracking().Where(x => ids.Contains(x.Id))
                                 .Select(x => new { x.Id, x.Version }).ToListAsync(ct))
                        identifiers[(group.Key, item.Id)] = item.Version is null ? null : SoftwareBuildIdentifier.FromVersion(item.Version);
                    break;
                case "problemreport":
                    foreach (var item in await db.ProblemReports.AsNoTracking().Where(x => ids.Contains(x.Id))
                                 .Select(x => new { x.Id, x.ReportNumber, x.Revision }).ToListAsync(ct))
                        identifiers[(group.Key, item.Id)] = $"{item.ReportNumber}.{item.Revision:D2}";
                    break;
            }
        }
        return identifiers;
    }

    private static string CanonicalLinkIdentifierType(string artifactType) => artifactType.Trim().ToLowerInvariant() switch
    {
        "changerequest" or "scr" or "swcr" => "changerequest",
        "testchangerequest" or "tcr" => "testchangerequest",
        "testexecution" => "testexecution",
        "softwarebuild" or "build" => "softwarebuild",
        "problemreport" or "pr" => "problemreport",
        "requirement" => "requirement",
        "baseline" => "baseline",
        "document" => "document",
        "evidence" => "evidence",
        "release" => "release",
        _ => artifactType.Trim().ToLowerInvariant()
    };

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
                                  join procedure in db.TestProcedures.AsNoTracking()
                                      on revision.ProcedureId equals procedure.Id
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
    private sealed record CreateProblemReportRequest(Guid ProjectId, Guid? ReleaseId, string Title, string Problem, string? ProblemRich, string? AdditionalInformation, string? AdditionalInformationRich, string? Analysis, string? Classification, ProblemReportSeverity? Severity, ProblemReportPriority? Priority, string? Origin, string? AffectedConfiguration, string? SystemAircraftImpact, string? ImpactAssessmentJson,
        ProblemReportCategory? Category = null,
        // Everything else a person can write. These were absent, so Workaround, Root cause, Analysis and
        // the rest could not be set at raise time at any width — the create form was not hiding them, it
        // had nowhere to send them.
        string? AnalysisRich = null, string? RootCause = null, string? RootCauseRich = null,
        string? Effects = null, string? EffectsRich = null,
        string? Containment = null, string? ContainmentRich = null,
        string? CorrectiveAction = null, string? CorrectiveActionRich = null,
        string? Workaround = null, string? WorkaroundRich = null,
        string? SystemAircraftImpactRich = null);
    private sealed record CreateProblemReportFromExecutionRequest(Guid? ReleaseId, string? Title, string? Problem, string? Analysis, string? Classification, ProblemReportSeverity? Severity, ProblemReportPriority? Priority, string? AffectedConfiguration, ProblemReportCategory? Category = null);
    private sealed record InvestigationRequest(long? ExpectedVersion, string Analysis, string? RootCause, string? Effects, string? Containment);
    private sealed record ResolutionRequest(long? ExpectedVersion, string CorrectiveAction);
    private sealed record VerificationRequest(long? ExpectedVersion, Guid TestExecutionId);
    private sealed record ClosureApprovalRequest(long? ExpectedVersion);
    private sealed record DispositionRequest(long? ExpectedVersion, ProblemReportDisposition Disposition, string Rationale, Guid? DuplicateOfId);
    private sealed record ReopenRequest(long? ExpectedVersion, string Rationale);
    private sealed record RelatedProblemReportRequest(Guid RelatedProblemReportId, long? ExpectedVersion = null);
    private sealed record BlockerRequest(long? ExpectedVersion, bool IsReleaseBlocker, string? WaiverRationale);
    private sealed record ReleaseWaiverRequest(long? ExpectedVersion, string Rationale, DateTimeOffset ExpiresAt);
    private sealed record RevokeReleaseWaiverRequest(long? ExpectedVersion, string Reason);
    private sealed record LinkRequest(long? ExpectedVersion, string ArtifactType, Guid ArtifactId, string Relationship);
    private sealed record ReassignRequest(long? ExpectedVersion, string ResponsibleEngineerId);
    private sealed record TransitionRequest(long? ExpectedVersion, string? TargetState, string? State, string? Rationale);
    private sealed record ProblemReportOwnerStatus(bool ProgramMember, bool Eligible);
    private sealed record RetargetRequest(long? ExpectedVersion, Guid TargetReleaseId);
    private sealed record VersionRequest(long? ExpectedVersion);
}
