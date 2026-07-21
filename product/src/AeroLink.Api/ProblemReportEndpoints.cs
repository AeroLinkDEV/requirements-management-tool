using System.Text.Json;
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
        group.MapPost("/{id:guid}/investigation", InvestigateAsync);
        group.MapPost("/{id:guid}/resolution", ProposeResolutionAsync);
        group.MapPost("/{id:guid}/verify", VerifyAsync);
        group.MapPost("/{id:guid}/closure/approve", ApproveClosureAsync);
        group.MapPost("/{id:guid}/disposition", DispositionAsync);
        group.MapPost("/{id:guid}/reopen", ReopenAsync);
        group.MapPost("/{id:guid}/blocker", BlockerAsync);
        group.MapPost("/{id:guid}/links", LinkAsync);
        group.MapGet("/{id:guid}/closure-package", ClosurePackageAsync);
        return app;
    }

    private static async Task<IResult> CreateAsync(CreateProblemReportRequest request, HttpContext http, AeroLinkDbContext db, IdentityService identity, CancellationToken ct)
    {
        if (!await http.HasProjectRoleAsync(db, identity, request.ProjectId, ct, ProgramRole.Engineer, ProgramRole.TestEngineer, ProgramRole.ConfigurationManager, ProgramRole.ProgramManager)) return Results.Forbid();
        await using var transaction = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, ct);
        try
        {
            var now = DateTimeOffset.UtcNow; var actor = http.UserAccount();
            var item = new ProblemReport(request.ProjectId, await IdentifierAllocator.NextProblemReportAsync(db, ct), request.Title, request.Problem, request.Analysis ?? "", actor.UserName, now,
                request.Classification ?? "Software anomaly", request.Severity ?? ProblemReportSeverity.Major, request.Priority ?? ProblemReportPriority.Normal, request.Origin ?? "Manual report", request.AffectedConfiguration ?? "");
            db.ProblemReports.Add(item); AddRevision(db, item, "ProblemReportCreated", actor.UserName, now);
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
            var item = new ProblemReport(execution.ProjectId, await IdentifierAllocator.NextProblemReportAsync(db, ct), request.Title ?? $"Failed execution {execution.Id.ToString()[..8]}", request.Problem ?? execution.Determination,
                request.Analysis ?? "Created from failed verification execution.", actor.UserName, now, request.Classification ?? "Verification failure", request.Severity ?? ProblemReportSeverity.Major,
                request.Priority ?? ProblemReportPriority.High, "Test execution", request.AffectedConfiguration ?? execution.Configuration);
            db.ProblemReports.Add(item); db.ProblemReportLinks.Add(new ProblemReportLink(item.Id, "TestExecution", execution.Id, "OriginatingFailure", actor.UserName, now));
            AddRevision(db, item, "ProblemReportCreatedFromFailedExecution", actor.UserName, now);
            await db.SaveChangesAsync(ct); await transaction.CommitAsync(ct);
            return Results.Created($"/api/problem-reports/{item.Id}", Detail(item, [new ProblemReportLinkView("TestExecution", execution.Id, "OriginatingFailure", actor.UserName, now)], []));
        }
        catch (DomainException ex) { return Results.BadRequest(new { error = ex.Message }); }
        catch (DbUpdateException) { return Results.Conflict(new { error = "A problem report number was allocated concurrently. Retry the create request.", code = "number_allocation_conflict" }); }
    }

    private static async Task<IResult> ListAsync(Guid projectId, string? search, ProblemReportState? state, ProblemReportSeverity? severity, bool? blockersOnly, int? page, int? pageSize, HttpContext http, AeroLinkDbContext db, CancellationToken ct)
    {
        if (!await http.HasProjectAccessAsync(db, projectId, ct)) return Results.Forbid();
        var query = db.ProblemReports.AsNoTracking().Where(x => x.ProjectId == projectId);
        if (state is not null) query = query.Where(x => x.State == state);
        if (severity is not null) query = query.Where(x => x.Severity == severity);
        if (blockersOnly == true) query = query.Where(x => x.IsReleaseBlocker && string.IsNullOrEmpty(x.WaiverRationale));
        if (!string.IsNullOrWhiteSpace(search)) { var term = search.Trim().ToLower(); query = query.Where(x => x.ReportNumber.ToLower().Contains(term) || x.Title.ToLower().Contains(term) || x.Problem.ToLower().Contains(term) || x.RootCause.ToLower().Contains(term)); }
        var size = Math.Clamp(pageSize ?? 50, 1, 200); var current = Math.Max(page ?? 1, 1); var matching = await query.ToListAsync(ct); var total = matching.Count;
        var items = matching.OrderByDescending(x => x.IsReleaseBlocker).ThenByDescending(x => x.Severity).ThenByDescending(x => x.UpdatedAt).Skip((current - 1) * size).Take(size).ToList();
        return Results.Ok(new { page = current, pageSize = size, totalCount = total, totalPages = (int)Math.Ceiling(total / (double)size), items = items.Select(Summary) });
    }

    private static async Task<IResult> DashboardAsync(Guid projectId, HttpContext http, AeroLinkDbContext db, CancellationToken ct)
    {
        if (!await http.HasProjectAccessAsync(db, projectId, ct)) return Results.Forbid();
        var reports = await db.ProblemReports.AsNoTracking().Where(x => x.ProjectId == projectId).ToListAsync(ct);
        var active = reports.Where(x => x.State is ProblemReportState.Open or ProblemReportState.Investigating or ProblemReportState.ResolutionProposed or ProblemReportState.AwaitingClosureApproval).ToList();
        return Results.Ok(new
        {
            generatedAt = DateTimeOffset.UtcNow,
            summary = new { total = reports.Count, active = active.Count, closureAwaitingApproval = reports.Count(x => x.State == ProblemReportState.AwaitingClosureApproval), closed = reports.Count(x => x.State == ProblemReportState.Closed), releaseBlockers = reports.Count(x => x.IsReleaseBlocker && string.IsNullOrEmpty(x.WaiverRationale)), waivedBlockers = reports.Count(x => x.IsReleaseBlocker && !string.IsNullOrEmpty(x.WaiverRationale)) },
            bySeverity = reports.GroupBy(x => x.Severity).OrderBy(x => x.Key).Select(x => new { severity = x.Key.ToString(), count = x.Count() }),
            byState = reports.GroupBy(x => x.State).OrderBy(x => x.Key).Select(x => new { state = x.Key.ToString(), count = x.Count() }),
            attention = active.OrderByDescending(x => x.IsReleaseBlocker).ThenByDescending(x => x.Severity).ThenBy(x => x.CreatedAt).Take(12).Select(Summary)
        });
    }

    private static async Task<IResult> LinkedAsync(string artifactType, Guid artifactId, HttpContext http, AeroLinkDbContext db, CancellationToken ct)
    {
        var canonicalType = CanonicalLinkType(artifactType);
        var links = await db.ProblemReportLinks.AsNoTracking().Where(x => x.ArtifactType == canonicalType && x.ArtifactId == artifactId).ToListAsync(ct);
        var ids = links.Select(x => x.ProblemReportId).Distinct().ToList(); var reports = await db.ProblemReports.AsNoTracking().Where(x => ids.Contains(x.Id)).ToListAsync(ct);
        var permitted = new List<ProblemReport>(); foreach (var report in reports) if (await http.HasProjectAccessAsync(db, report.ProjectId, ct)) permitted.Add(report);
        return Results.Ok(permitted.Select(Summary));
    }

    private static async Task<IResult> DetailAsync(Guid id, HttpContext http, AeroLinkDbContext db, CancellationToken ct)
    {
        var report = await db.ProblemReports.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct); if (report is null) return Results.NotFound();
        if (!await http.HasProjectAccessAsync(db, report.ProjectId, ct)) return Results.Forbid();
        var links = (await db.ProblemReportLinks.AsNoTracking().Where(x => x.ProblemReportId == id).ToListAsync(ct)).OrderBy(x => x.AddedAt).ToList();
        var revisions = (await db.ProblemReportRevisions.AsNoTracking().Where(x => x.ProblemReportId == id).ToListAsync(ct)).OrderByDescending(x => x.OccurredAt).ToList();
        return Results.Ok(Detail(report, links.Select(x => new ProblemReportLinkView(x.ArtifactType, x.ArtifactId, x.Relationship, x.AddedBy, x.AddedAt)), revisions));
    }

    private static async Task<IResult> InvestigateAsync(Guid id, InvestigationRequest request, HttpContext http, AeroLinkDbContext db, CancellationToken ct) => await ChangeAsync(id, request.ExpectedVersion, http, db, ct, "InvestigationRecorded", (report, actor, now) => report.BeginInvestigation(actor.UserName, request.Analysis, request.RootCause ?? "", request.Effects ?? "", request.Containment ?? "", now));
    private static async Task<IResult> ProposeResolutionAsync(Guid id, ResolutionRequest request, HttpContext http, AeroLinkDbContext db, CancellationToken ct) => await ChangeAsync(id, request.ExpectedVersion, http, db, ct, "ResolutionProposed", (report, actor, now) => report.ProposeResolution(actor.UserName, request.CorrectiveAction, now));

    private static async Task<IResult> VerifyAsync(Guid id, VerificationRequest request, HttpContext http, AeroLinkDbContext db, CancellationToken ct)
    {
        var report = await db.ProblemReports.SingleOrDefaultAsync(x => x.Id == id, ct); if (report is null) return Results.NotFound();
        if (!await http.HasProjectAccessAsync(db, report.ProjectId, ct)) return Results.Forbid();
        var execution = await db.TestExecutions.AsNoTracking().SingleOrDefaultAsync(x => x.Id == request.TestExecutionId && x.ProjectId == report.ProjectId, ct);
        if (execution is null || execution.Outcome != TestOutcome.Pass) return Results.BadRequest(new { error = "Closure verification requires a passing successor test execution in the same project." });
        return await ChangeAsync(report, request.ExpectedVersion, http, db, ct, "ResolutionVerified", (item, actor, now) => item.RecordResolutionVerification(actor.UserName, execution.Id, now), link: (actor, now) => new ProblemReportLink(report.Id, "TestExecution", execution.Id, "ResolutionVerification", actor.UserName, now));
    }

    private static async Task<IResult> ApproveClosureAsync(Guid id, ClosureApprovalRequest request, HttpContext http, AeroLinkDbContext db, IdentityService identity, CancellationToken ct)
    {
        var report = await db.ProblemReports.SingleOrDefaultAsync(x => x.Id == id, ct); if (report is null) return Results.NotFound();
        if (!await http.HasProjectRoleAsync(db, identity, report.ProjectId, ct, ProgramRole.ConfigurationManager, ProgramRole.ProgramManager, ProgramRole.Approver)) return Results.Forbid();
        var actor = http.UserAccount();
        return await ChangeAsync(report, request.ExpectedVersion, http, db, ct, "ClosureApproved", (item, user, now) => item.ApproveClosure(user.UserName, user.Id, now));
    }

    private static async Task<IResult> DispositionAsync(Guid id, DispositionRequest request, HttpContext http, AeroLinkDbContext db, CancellationToken ct)
    {
        var result = await ChangeAsync(id, request.ExpectedVersion, http, db, ct, "DispositionRecorded", (report, actor, now) => report.ApplyDisposition(actor.UserName, request.Disposition, request.Rationale, request.DuplicateOfId, now), link: request.DuplicateOfId is null ? null : (actor, now) => new ProblemReportLink(id, "ProblemReport", request.DuplicateOfId.Value, "DuplicateOf", actor.UserName, now));
        return result;
    }
    private static async Task<IResult> ReopenAsync(Guid id, ReopenRequest request, HttpContext http, AeroLinkDbContext db, CancellationToken ct) => await ChangeAsync(id, request.ExpectedVersion, http, db, ct, "ProblemReportReopened", (report, actor, now) => report.Reopen(actor.UserName, request.Rationale, now));
    private static async Task<IResult> BlockerAsync(Guid id, BlockerRequest request, HttpContext http, AeroLinkDbContext db, CancellationToken ct) => await ChangeAsync(id, request.ExpectedVersion, http, db, ct, request.IsReleaseBlocker ? "ReleaseBlockerRaised" : "ReleaseBlockerCleared", (report, actor, now) => report.SetReleaseBlocker(actor.UserName, request.IsReleaseBlocker, request.WaiverRationale ?? "", now));

    private static async Task<IResult> LinkAsync(Guid id, LinkRequest request, HttpContext http, AeroLinkDbContext db, CancellationToken ct)
    {
        var report = await db.ProblemReports.SingleOrDefaultAsync(x => x.Id == id, ct); if (report is null) return Results.NotFound();
        if (!await http.HasProjectAccessAsync(db, report.ProjectId, ct)) return Results.Forbid();
        var canonicalType = CanonicalLinkType(request.ArtifactType);
        if (!await LinkExistsInProjectAsync(canonicalType, request.ArtifactId, report.ProjectId, db, ct)) return Results.BadRequest(new { error = "The linked artifact does not exist in this problem report's project or is not a supported link target." });
        try
        {
            var now = DateTimeOffset.UtcNow; var actor = http.UserAccount(); db.ProblemReportLinks.Add(new ProblemReportLink(report.Id, canonicalType, request.ArtifactId, request.Relationship, actor.UserName, now)); AddRevision(db, report, "ArtifactLinked", actor.UserName, now);
            await db.SaveChangesAsync(ct); return Results.Created($"/api/problem-reports/{id}/links/{request.ArtifactId}", new { artifactType = canonicalType, request.ArtifactId, request.Relationship });
        }
        catch (DomainException ex) { return Results.BadRequest(new { error = ex.Message }); }
        catch (DbUpdateException) { return Results.Conflict(new { error = "That problem-report link already exists." }); }
    }

    private static async Task<IResult> ClosurePackageAsync(Guid id, HttpContext http, AeroLinkDbContext db, CancellationToken ct)
    {
        var report = await db.ProblemReports.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct); if (report is null) return Results.NotFound();
        if (!await http.HasProjectAccessAsync(db, report.ProjectId, ct)) return Results.Forbid();
        if (report.State != ProblemReportState.Closed) return Results.Conflict(new { error = "A controlled closure package is available only after independent closure approval." });
        var links = await db.ProblemReportLinks.AsNoTracking().Where(x => x.ProblemReportId == id).OrderBy(x => x.ArtifactType).ThenBy(x => x.ArtifactId).ToListAsync(ct);
        var revisions = (await db.ProblemReportRevisions.AsNoTracking().Where(x => x.ProblemReportId == id).ToListAsync(ct)).OrderBy(x => x.OccurredAt).ToList();
        return Results.Ok(new { packageType = "ProblemReportClosurePackage", generatedAt = DateTimeOffset.UtcNow, generatorVersion = "AeroLink-3.0", report = Detail(report, links.Select(x => new ProblemReportLinkView(x.ArtifactType, x.ArtifactId, x.Relationship, x.AddedBy, x.AddedAt)), revisions), sourceHash = report.CanonicalHash(), manifest = new { report.DisplayNumber, report.Version, revisionEvidenceCount = revisions.Count, linkCount = links.Count } });
    }

    private static async Task<IResult> ChangeAsync(Guid id, long? expectedVersion, HttpContext http, AeroLinkDbContext db, CancellationToken ct, string eventType, Action<ProblemReport, AuthenticatedUser, DateTimeOffset> action, Func<AuthenticatedUser, DateTimeOffset, ProblemReportLink>? link = null)
    {
        var report = await db.ProblemReports.SingleOrDefaultAsync(x => x.Id == id, ct); if (report is null) return Results.NotFound();
        return await ChangeAsync(report, expectedVersion, http, db, ct, eventType, action, link);
    }

    private static async Task<IResult> ChangeAsync(ProblemReport report, long? expectedVersion, HttpContext http, AeroLinkDbContext db, CancellationToken ct, string eventType, Action<ProblemReport, AuthenticatedUser, DateTimeOffset> action, Func<AuthenticatedUser, DateTimeOffset, ProblemReportLink>? link = null)
    {
        if (!await http.HasProjectAccessAsync(db, report.ProjectId, ct)) return Results.Forbid();
        if (expectedVersion is not null && expectedVersion != report.Version) return Results.Conflict(new { error = "This problem report changed after it was opened. Refresh before continuing.", code = "stale_version", currentVersion = report.Version });
        try
        {
            var now = DateTimeOffset.UtcNow; var actor = http.UserAccount(); action(report, actor, now); if (link is not null) db.ProblemReportLinks.Add(link(actor, now)); AddRevision(db, report, eventType, actor.UserName, now);
            await db.SaveChangesAsync(ct); return Results.Ok(new { id = report.Id, displayNumber = report.DisplayNumber, state = report.State.ToString(), version = report.Version, snapshotHash = report.CanonicalHash() });
        }
        catch (DomainException ex) { return Results.BadRequest(new { error = ex.Message }); }
        catch (DbUpdateConcurrencyException) { return Results.Conflict(new { error = "This problem report was updated concurrently. Refresh before continuing.", code = "stale_version" }); }
    }

    private static void AddRevision(AeroLinkDbContext db, ProblemReport report, string eventType, string actor, DateTimeOffset now)
    {
        var snapshot = JsonSerializer.Serialize(new { report.Id, report.ProjectId, report.ReportNumber, report.Revision, report.DisplayNumber, report.Title, report.Problem, report.Analysis, report.ReportedBy, report.Classification, severity = report.Severity.ToString(), priority = report.Priority.ToString(), report.Origin, report.AffectedConfiguration, report.RootCause, report.Effects, report.Containment, report.CorrectiveAction, disposition = report.Disposition?.ToString(), report.DispositionRationale, report.ResolutionVerificationExecutionId, report.ClosureApprovedByName, report.ClosureApprovedAt, report.IsReleaseBlocker, report.WaiverRationale, report.WaivedBy, state = report.State.ToString(), report.Version });
        db.ProblemReportRevisions.Add(new ProblemReportRevision(report.Id, report.Revision, eventType, actor, report.CanonicalHash(), snapshot, now));
    }

    private static async Task<bool> LinkExistsInProjectAsync(string artifactType, Guid artifactId, Guid projectId, AeroLinkDbContext db, CancellationToken ct) => artifactType.Trim().ToLowerInvariant() switch
    {
        "requirement" => await db.Requirements.AnyAsync(x => x.Id == artifactId && x.ProjectId == projectId, ct),
        "changerequest" or "scr" or "swcr" => await db.SystemChangeRequests.AnyAsync(x => x.Id == artifactId && x.ProjectId == projectId, ct),
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

    private static object Summary(ProblemReport x) => new { x.Id, x.ReportNumber, x.Revision, x.DisplayNumber, x.Title, state = x.State.ToString(), severity = x.Severity.ToString(), priority = x.Priority.ToString(), x.Classification, x.ReportedBy, x.IsReleaseBlocker, waived = !string.IsNullOrWhiteSpace(x.WaiverRationale), x.UpdatedAt, x.Version };
    private static object Detail(ProblemReport x, IEnumerable<ProblemReportLinkView> links, IEnumerable<ProblemReportRevision> revisions) => new { x.Id, x.ProjectId, x.ReportNumber, x.Revision, x.DisplayNumber, x.Title, x.Problem, x.Analysis, x.ReportedBy, x.Classification, severity = x.Severity.ToString(), priority = x.Priority.ToString(), x.Origin, x.AffectedConfiguration, x.RootCause, x.Effects, x.Containment, x.CorrectiveAction, disposition = x.Disposition?.ToString(), x.DispositionRationale, x.ResolutionVerificationExecutionId, x.ClosureApprovedByName, x.ClosureApprovedAt, x.IsReleaseBlocker, x.WaiverRationale, x.WaivedBy, x.WaivedAt, state = x.State.ToString(), x.CreatedAt, x.UpdatedAt, x.Version, snapshotHash = x.CanonicalHash(), links, revisions = revisions.Select(x => new { x.Id, x.Revision, x.EventType, x.Actor, x.SnapshotHash, x.OccurredAt }) };

    private sealed record ProblemReportLinkView(string ArtifactType, Guid ArtifactId, string Relationship, string AddedBy, DateTimeOffset AddedAt);
    private sealed record CreateProblemReportRequest(Guid ProjectId, string Title, string Problem, string? Analysis, string? Classification, ProblemReportSeverity? Severity, ProblemReportPriority? Priority, string? Origin, string? AffectedConfiguration);
    private sealed record CreateProblemReportFromExecutionRequest(string? Title, string? Problem, string? Analysis, string? Classification, ProblemReportSeverity? Severity, ProblemReportPriority? Priority, string? AffectedConfiguration);
    private sealed record InvestigationRequest(long? ExpectedVersion, string Analysis, string? RootCause, string? Effects, string? Containment);
    private sealed record ResolutionRequest(long? ExpectedVersion, string CorrectiveAction);
    private sealed record VerificationRequest(long? ExpectedVersion, Guid TestExecutionId);
    private sealed record ClosureApprovalRequest(long? ExpectedVersion);
    private sealed record DispositionRequest(long? ExpectedVersion, ProblemReportDisposition Disposition, string Rationale, Guid? DuplicateOfId);
    private sealed record ReopenRequest(long? ExpectedVersion, string Rationale);
    private sealed record BlockerRequest(long? ExpectedVersion, bool IsReleaseBlocker, string? WaiverRationale);
    private sealed record LinkRequest(string ArtifactType, Guid ArtifactId, string Relationship);
}
