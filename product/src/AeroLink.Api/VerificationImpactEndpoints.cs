using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Common;
using AeroLink.Domain.Identity;
using AeroLink.Domain.Hierarchy;
using AeroLink.Domain.Requirements;
using AeroLink.Domain.Verification;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace AeroLink.Api;

public sealed record AssignVerificationImpactRequest(string EngineerId);
public sealed record ResolveVerificationImpactRequest(VerificationImpactOutcome Outcome, string Rationale, Guid? ProcedureId = null,
    TestProcedureChangeAction? ProcedureChangeAction = null, bool PreReleaseEvidenceRequired = false,
    Guid? RetargetedRequirementRevisionId = null)
{
    /// <summary>Canonical neutral Case/Procedure identity; ProcedureId remains a compatibility alias.</summary>
    public Guid? ArtifactId { get; init; }

    /// <summary>Canonical neutral change action; ProcedureChangeAction remains a compatibility alias.</summary>
    public TestProcedureChangeAction? ArtifactChangeAction { get; init; }
}
public sealed record ReopenVerificationImpactRequest(string Rationale);
public sealed record IncludeChangeRequestRequest(Guid ChangeRequestId, long? ExpectedVersion = null);
public sealed record CreateTestChangeRequestRequest(TestChangeReviewDiscipline Discipline, Guid[] ChangeRequestIds,
    Guid[]? ProblemReportIds = null, string Title = "", string Problem = "", string Analysis = "",
    string Solution = "", string? ProblemRich = null, string? AnalysisRich = null, string? SolutionRich = null,
    /// <summary>
    /// The procedure decisions authored alongside the case, saved with the package in one act.
    ///
    /// A change request is created together with the requirement changes it proposes; a package created
    /// without its procedure decisions would be the same proposal in two halves, the second of which somebody
    /// has to remember to write. Optional, because a package may still be raised and worked afterwards.
    /// </summary>
    CreateTestProcedureChangeRequest[]? ProcedureChanges = null,
    VerificationArtifactKind? ArtifactKind = null,
    Guid[]? CaseChangeIds = null,
    Guid[]? CaseAssessmentIds = null)
{
    /// <summary>Neutral Case/Procedure seam; ProcedureChanges remains the wire alias for older clients.</summary>
    public CreateTestProcedureChangeRequest[]? ArtifactChanges { get; init; }
    /// <summary>Explicit neutral package identity; omitted legacy requests remain Case except System Procedure.</summary>
    public VerificationArtifactKey? ArtifactKey { get; init; }
}

/// <summary>One proposed procedure decision, as the authoring page states it before the package exists.</summary>
public sealed record CreateTestProcedureChangeRequest(string BaseNumber, int Revision, TestProcedureLevel Level,
    TestProcedureChangeKind Kind, string Title, string Objective, string Preconditions, string Steps,
    string ExpectedResult, string Rationale, Guid[]? DrivingRequirementRevisionIds = null,
    VerificationProcedureParentKind ParentKind = VerificationProcedureParentKind.Unspecified,
    Guid[]? ParentRevisionIds = null, string? DerivedRationale = null,
    string? EnvironmentSetup = null, string? TestData = null, string? OrderedSteps = null,
    string? ExpectedObservations = null, string? Cleanup = null, string? ToolingAutomation = null);
public sealed record WriteTestChangeRequestCaseRequest(string Title, string Problem, string Analysis,
    string Solution, string? ProblemRich = null, string? AnalysisRich = null, string? SolutionRich = null,
    long? ExpectedVersion = null);
public sealed record LinkProblemReportsRequest(Guid[] ProblemReportIds, long? ExpectedVersion = null);
public sealed record SubmitTestChangeReviewRequest(string ApproverId,
    IReadOnlyList<TestChangeRequestApproverRequest>? Approvers = null, long? ExpectedVersion = null);
/// <summary>One person chosen for one configured review stage.</summary>
public sealed record TestChangeRequestApproverRequest(string UserId, string Name = "");
/// <param name="Rationale">Why no test work is needed. Required only when concluding that none is.</param>
public sealed record TestAssessmentConclusionRequest(bool TestChangeRequired, string? Rationale,
    long? ExpectedVersion = null);
public sealed record ApproveTestChangeReviewRequest(string Rationale, string Password, string Meaning);
/// <summary>
/// One proposed change to one procedure. <paramref name="BaseNumber"/> is omitted when introducing — the
/// number is allocated here so two engineers cannot pick the same one — and required otherwise, because a
/// modification or retirement has to name the procedure it acts on.
/// </summary>
public sealed record ProposeProcedureChangeRequest(TestProcedureChangeKind Kind, string? BaseNumber, int Revision,
    string Title, string Objective, string Preconditions, string Steps, string ExpectedResult, string Rationale,
    Guid[]? DrivingRequirementRevisionIds, long? ExpectedVersion = null,
    Guid[]? RemovedRequirementRevisionIds = null, string? CoverageChangeRationale = null,
    VerificationProcedureParentKind ParentKind = VerificationProcedureParentKind.Unspecified,
    Guid[]? ParentRevisionIds = null, string? DerivedRationale = null,
    string? EnvironmentSetup = null, string? TestData = null, string? OrderedSteps = null,
    string? ExpectedObservations = null, string? Cleanup = null, string? ToolingAutomation = null);
public sealed record ReturnTestChangeReviewRequest(string Rationale);
public sealed record DeferTestChangeReviewRequest(string Reason);

/// <summary>The one lifecycle contract shared by every route that offers or accepts a TCR source.</summary>
internal static class TestChangeRequestSourceEligibility
{
    internal static readonly ChangeRequestState[] EligibleStates =
        [ChangeRequestState.Approved, ChangeRequestState.SelectedForBaseline];

    internal static bool Allows(ChangeRequestState state) => EligibleStates.Contains(state);

    internal static IQueryable<SystemChangeRequest> Apply(IQueryable<SystemChangeRequest> changes) =>
        changes.Where(x => EligibleStates.Contains(x.State));

    internal static IResult Refusal(string displayNumber, ChangeRequestState state) =>
        Results.BadRequest(new
        {
            error = $"{displayNumber} is {state} and cannot be a test change request source. " +
                "Only approved change requests in Approved or SelectedForBaseline state are eligible.",
            code = "change_request_not_selectable"
        });

    /// <summary>
    /// Whether a change request is at the level whose test work this package controls.
    ///
    /// A procedure is written to verify the requirements one level above it, so an HLR test change request
    /// answers for HLR requirement changes and nothing else. Before this, the picker offered every approved
    /// change in the build — an engineer raising an HLRTCCR was shown SRCRs and LLRCRs, neither of which could
    /// drive an HLR procedure. It is a refusal rather than a sort order because selecting one produced a
    /// package that claimed to answer for work it cannot verify.
    /// </summary>
    internal static bool MatchesDiscipline(TestChangeReviewDiscipline discipline, ChangeRequestType type,
        RequirementLevel? softwareLevel, ILadderPolicy? policy = null)
    {
        var ladderPolicy = policy ?? LegacyLadderPolicy.Instance;
        var level = ladderPolicy.RequirementLevelFor(discipline);
        return ladderPolicy.ParentLevels(level).Count == 0
            ? type == ChangeRequestType.System
            : type == ChangeRequestType.Software && softwareLevel == level;
    }

    /// <summary>The same rule as a database predicate, so the picker and the server cannot disagree.</summary>
    internal static IQueryable<SystemChangeRequest> AtLevelOf(IQueryable<SystemChangeRequest> changes,
        TestChangeReviewDiscipline discipline, ILadderPolicy? policy = null)
    {
        var ladderPolicy = policy ?? LegacyLadderPolicy.Instance;
        var level = ladderPolicy.RequirementLevelFor(discipline);
        return ladderPolicy.ParentLevels(level).Count == 0
            ? changes.Where(x => x.Type == ChangeRequestType.System)
            : changes.Where(x => x.Type == ChangeRequestType.Software && x.SoftwareLevel == level);
    }

    internal static string LevelName(TestChangeReviewDiscipline discipline) => discipline switch
    {
        TestChangeReviewDiscipline.System => "system",
        TestChangeReviewDiscipline.HighLevelSoftware => "high-level software",
        _ => "low-level software",
    };

    internal static string ArtifactWord(TestChangeReviewDiscipline discipline) =>
        discipline == TestChangeReviewDiscipline.System ? "test procedure" : "test case";

    internal static string ArtifactPlural(TestChangeReviewDiscipline discipline) =>
        discipline == TestChangeReviewDiscipline.System ? "test procedures" : "test cases";

    internal static string ArtifactNoun(TestChangeReviewDiscipline discipline) =>
        discipline == TestChangeReviewDiscipline.System ? "procedure" : "case";

    internal static string ArtifactKind(TestChangeReviewDiscipline discipline) =>
        discipline == TestChangeReviewDiscipline.System ? "Procedure" : "Case";

    internal static string ArtifactKind(VerificationArtifactKey key) => key.Kind.ToString();

    internal static string ArtifactLabel(TestChangeReviewDiscipline discipline) => discipline switch
    {
        TestChangeReviewDiscipline.System => "System Test Procedure",
        TestChangeReviewDiscipline.HighLevelSoftware => "HLR Test Case",
        _ => "LLR Test Case",
    };

    internal static string ArtifactLabel(VerificationArtifactKey key) => key switch
    {
        { Discipline: VerificationDiscipline.System, Kind: VerificationArtifactKind.Procedure } => "System Test Procedure",
        { Discipline: VerificationDiscipline.HighLevelSoftware, Kind: VerificationArtifactKind.Case } => "HLR Test Case",
        { Discipline: VerificationDiscipline.LowLevelSoftware, Kind: VerificationArtifactKind.Case } => "LLR Test Case",
        { Discipline: VerificationDiscipline.HighLevelSoftware, Kind: VerificationArtifactKind.Procedure } => "HLR Test Procedure",
        { Discipline: VerificationDiscipline.LowLevelSoftware, Kind: VerificationArtifactKind.Procedure } => "LLR Test Procedure",
        _ => "Verification artifact",
    };

    internal static IResult LevelRefusal(string displayNumber, TestChangeReviewDiscipline discipline) =>
        Results.BadRequest(new
        {
            error = $"{displayNumber} is not a {LevelName(discipline)} requirement change, so it cannot drive " +
                $"{LevelName(discipline)} test work. A {ArtifactWord(discipline)} verifies the requirements one level above it.",
            code = "change_request_wrong_level"
        });

    internal static string ArtifactWord(VerificationArtifactKey key) =>
        key.Kind == VerificationArtifactKind.Procedure ? "test procedure" : "test case";

    internal static string ArtifactNoun(VerificationArtifactKey key) =>
        key.Kind == VerificationArtifactKind.Procedure ? "procedure" : "case";

    internal static string ArtifactPlural(VerificationArtifactKey key) =>
        key.Kind == VerificationArtifactKind.Procedure ? "test procedures" : "test cases";

    internal static string LevelName(VerificationArtifactKey key) => key.Discipline switch
    {
        VerificationDiscipline.System => "system",
        VerificationDiscipline.HighLevelSoftware => "high-level software",
        _ => "low-level software",
    };
}

public static class VerificationImpactEndpoints
{
    private sealed record OriginDisplay(string Label, string Identity, string Title);

    private static OriginDisplay OriginFor(TestChangeReview review,
        IReadOnlyDictionary<Guid, (string Identity, string Title)> caseChanges,
        IReadOnlyDictionary<Guid, (string Identity, string Title)> assessments,
        IReadOnlyDictionary<Guid, string> changeRequests,
        IReadOnlyDictionary<Guid, (string Identity, string Title)> problemReports)
    {
        return review.OriginKind switch
        {
            TestChangeReviewOriginKind.CaseChange when caseChanges.TryGetValue(review.OriginReferenceId, out var change)
                => new("Case change", change.Identity, change.Title),
            TestChangeReviewOriginKind.CaseAssessment when assessments.TryGetValue(review.OriginReferenceId, out var assessment)
                => new("Case assessment", assessment.Identity, assessment.Title),
            TestChangeReviewOriginKind.CaseReview
                => new("Case TCR", review.SourceCaseOriginNumber, "Approved Case change-control package"),
            TestChangeReviewOriginKind.ChangeRequest
                => new("Change request", review.SourceChangeRequestNumber,
                    changeRequests.GetValueOrDefault(review.OriginReferenceId) ?? "Source change request"),
            TestChangeReviewOriginKind.ProblemReport when problemReports.TryGetValue(review.OriginReferenceId, out var report)
                => new("Problem Report", report.Identity, report.Title),
            TestChangeReviewOriginKind.ProblemReport
                => new("Problem Report", review.SourceProblemReportNumber, "Source Problem Report"),
            _ => new("Origin", review.SourceDisplayNumber, "")
        };
    }

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
            HttpContext http, AeroLinkDbContext db, IdentityService identity, CancellationToken ct) =>
        {
            var release = await db.Releases.AsNoTracking().Where(x => x.Id == releaseId)
                .Select(x => new { x.ProjectId, x.IsReleased }).SingleOrDefaultAsync(ct);
            if (release is null) return Results.NotFound();
            if (!await http.HasProjectAccessAsync(db, release.ProjectId, ct)) return Results.Forbid();
            var actor = http.UserAccount().UserName;
            var canTest = !release.IsReleased && await http.HasProjectRoleAsync(db, identity, release.ProjectId, ct,
                ProgramRole.TestEngineer, ProgramRole.TestLead);
            var isLead = !release.IsReleased && await http.HasProjectRoleAsync(db, identity, release.ProjectId, ct,
                ProgramRole.TestLead);
            // Ordered in memory on purpose: SQLite cannot translate an ORDER BY over a DateTimeOffset and
            // throws, which took this whole endpoint to a 500 and left the workspace looking simply empty.
            var reviews = (await db.TestChangeReviews.AsNoTracking()
                    .Include(x => x.AdditionalSources)
                    .Include(x => x.ProcedureChanges)
                    .Include(x => x.ReviewCycles).ThenInclude(x => x.Steps)
                    .Where(x => x.ReleaseId == releaseId)
                    .ToListAsync(ct))
                .OrderBy(x => x.State).ThenBy(x => x.Discipline).ThenBy(x => x.ArtifactKind).ThenBy(x => x.CreatedAt)
                .ToList();
            var reviewIds = reviews.Select(x => x.Id).ToList();
            var changeRequestIds = reviews.Where(x => x.ChangeRequestId != null).Select(x => x.ChangeRequestId!.Value)
                .Concat(reviews.SelectMany(x => x.AdditionalSources).Select(x => x.ChangeRequestId)).Distinct().ToList();
            var changeRequests = await db.SystemChangeRequests.AsNoTracking().Where(x => changeRequestIds.Contains(x.Id))
                .ToDictionaryAsync(x => x.Id, x => x.Title, ct);
            var items = await db.VerificationImpactItems.AsNoTracking()
                .Where(x => reviewIds.Contains(x.TestChangeReviewId)).ToListAsync(ct);
            var reportLinks = await db.ProblemReportLinks.AsNoTracking()
                .Where(x => x.ArtifactType == "TestChangeRequest" && reviewIds.Contains(x.ArtifactId))
                .ToListAsync(ct);
            var reportIds = reportLinks.Select(x => x.ProblemReportId).Distinct().ToList();
            var reportDirectory = await db.ProblemReports.AsNoTracking().Where(x => reportIds.Contains(x.Id))
                .ToDictionaryAsync(x => x.Id, x => new { x.Id, x.DisplayNumber, x.Title, state = x.State.ToString() }, ct);
            var caseChangeIds = reviews.Where(x => x.OriginKind == TestChangeReviewOriginKind.CaseChange)
                .Select(x => x.OriginReferenceId).Distinct().ToList();
            var caseChanges = await db.Set<TestProcedureChange>().AsNoTracking()
                .Where(x => caseChangeIds.Contains(x.Id))
                .ToDictionaryAsync(x => x.Id, x => (Identity: x.BaseNumber + "." + (x.Revision < 10 ? "0" : "") + x.Revision, x.Title), ct);
            var assessmentIds = reviews.Where(x => x.OriginKind == TestChangeReviewOriginKind.CaseAssessment)
                .Select(x => x.OriginReferenceId).Distinct().ToList();
            var assessments = await db.VerificationImpactItems.AsNoTracking()
                .Where(x => assessmentIds.Contains(x.Id))
                .ToDictionaryAsync(x => x.Id, x => (x.SubjectDisplayNumber, $"{x.Outcome} · {x.ResolutionRationale}"), ct);
            var originReports = reportDirectory.ToDictionary(x => x.Key,
                x => (x.Value.DisplayNumber, x.Value.Title));
            var originChanges = caseChanges.ToDictionary(x => x.Key,
                x => (x.Value.Identity, x.Value.Title));
            var originAssessments = assessments.ToDictionary(x => x.Key,
                x => (x.Value.SubjectDisplayNumber, x.Value.Item2));
            return Results.Ok(new
            {
                canCreate = canTest,
                items = reviews.Select(review => new
                {
                    origin = OriginFor(review, originChanges, originAssessments, changeRequests, originReports),
                    review.Id,
                    review.ProjectId,
                    review.ReleaseId,
                    review.ChangeRequestId,
                    discipline = review.Discipline.ToString(),
                    artifactKey = review.ArtifactKey.ToString(),
                    artifactKind = review.ArtifactKind.ToString(),
                    originKind = review.OriginKind.ToString(),
                    originReferenceId = review.OriginReferenceId,
                    sourceCaseOriginNumber = review.SourceCaseOriginNumber,
                    originKindLabel = OriginFor(review, originChanges, originAssessments, changeRequests, originReports).Label,
                    originDisplayIdentity = OriginFor(review, originChanges, originAssessments, changeRequests, originReports).Identity,
                    originDisplayTitle = OriginFor(review, originChanges, originAssessments, changeRequests, originReports).Title,
                    state = review.State.ToString(),
                    // Deferral reads the way it does on the requirements register: the allocation column says
                    // it is on the shelf, and the state column says how far it had got before it went there.
                    deferredFromState = review.DeferredFromState?.ToString(),
                    review.DeferralReason,
                    review.AuthorId,
                    review.SourceChangeRequestNumber,
                    review.DisplayNumber,
                    review.Title,
                    review.Problem,
                    review.Analysis,
                    review.Solution,
                    review.ProblemRich,
                    review.AnalysisRich,
                    review.SolutionRich,
                    review.CaseContractVersion,
                    artifactLabel = TestChangeRequestSourceEligibility.ArtifactLabel(review.ArtifactKey),
                    artifactDecisionCount = review.ProcedureChanges.Count,
                    // Compatibility alias retained for older clients; current Case clients consume the
                    // neutral artifact field above.
                    procedureDecisionCount = review.ProcedureChanges.Count,
                    // Every change request this package answers for, the one it was raised from first. A reader
                    // scanning the list needs to see that two changes are being tested together without opening it.
                    coveredChangeRequests = (review.ChangeRequestId is { } originatingChangeRequest
                            ? new[] { new { id = originatingChangeRequest, number = review.SourceChangeRequestNumber, title = changeRequests.GetValueOrDefault(originatingChangeRequest) ?? "Source change request", originating = true } }
                            : [])
                        .Concat(review.AdditionalSources.OrderBy(x => x.ChangeRequestNumber)
                            .Select(x => new { id = x.ChangeRequestId, number = x.ChangeRequestNumber, title = changeRequests.GetValueOrDefault(x.ChangeRequestId) ?? "Source change request", originating = false })),
                    review.AssignedEngineerId,
                    outcome = review.Outcome.ToString(),
                    review.NoChangeRationale,
                    review.DecidedBy,
                    review.DecidedAt,
                    review.SubmittedBy,
                    review.SelectedApproverId,
                    version = review.Version,
                    review.SubmittedAt,
                    review.ApprovedBy,
                    review.ApprovedAt,
                    review.ApprovalRationale,
                    review.SupersededByTestChangeRequestId,
                    review.SupersededReason,
                    totalItems = items.Count(x => x.TestChangeReviewId == review.Id),
                    resolvedItems = items.Count(x => x.TestChangeReviewId == review.Id && x.State == VerificationImpactState.Resolved),
                    preReleaseEvidenceItems = items.Count(x => x.TestChangeReviewId == review.Id && x.PreReleaseEvidenceRequired)
                    ,problemReports = reportLinks.Where(x => x.ArtifactId == review.Id)
                        .Select(x => reportDirectory.GetValueOrDefault(x.ProblemReportId)).Where(x => x is not null)
                        .DistinctBy(x => x!.Id)
                    ,reviewCycle = LatestNonCancelledCycle(review) is { } cycle
                        ? new
                        {
                            cycle.Id,
                            cycle.Sequence,
                            mode = cycle.Mode.ToString(),
                            state = cycle.State.ToString(),
                            cycle.WorkflowId,
                            cycle.WorkflowLogicalId,
                            cycle.WorkflowName,
                            cycle.WorkflowVersion,
                            steps = cycle.Steps.OrderBy(x => x.Position).Select(step => new
                            {
                                step.Position,
                                stageName = step.StageName,
                                authority = step.Authority,
                                approverId = step.ApproverId,
                                approverName = step.ApproverName,
                                rationale = step.Rationale,
                                state = step.State.ToString(),
                                step.DecidedAt
                            })
                        }
                        : null
                    ,capabilities = new
                    {
                        // Unheld or held by this reader. Taking a package on used to be a step of its own before
                        // any of its work was offered; answering it is what takes it now, so an unheld package is
                        // open to anybody with the authority and a held one stays with whoever answered first.
                        // Answering/claiming an unheld package is open to any test engineer; Test Leads can
                        // additionally assign it elsewhere through the assign endpoint.
                        canAssign = canTest && review.State == TestChangeReviewState.Draft && review.AssignedEngineerId == null,
                        canDecide = canTest && review.State == TestChangeReviewState.Draft
                            && (review.AssignedEngineerId == null
                                || string.Equals(review.AssignedEngineerId, actor, StringComparison.OrdinalIgnoreCase)
                                || isLead),
                        canSubmit = canTest && review.State == TestChangeReviewState.Draft
                            && (review.AssignedEngineerId == null
                                || string.Equals(review.AssignedEngineerId, actor, StringComparison.OrdinalIgnoreCase)
                                || isLead),
                        // Approval and return authority come from the active review-cycle step, not from the
                        // legacy single-approver fields. A configured workflow can have one active step
                        // (sequential) or several (parallel), and the stage may require any Program role.
                        canApprove = !release.IsReleased && review.State == TestChangeReviewState.InReview
                            && (LatestNonCancelledCycle(review) is { State: ReviewCycleState.Active } activeCycle
                                && activeCycle.Steps.Any(step => step.State == ApprovalStepState.Active
                                    && string.Equals(step.ApproverId, actor, StringComparison.OrdinalIgnoreCase))),
                        canReturn = !release.IsReleased && review.State == TestChangeReviewState.InReview
                            && (LatestNonCancelledCycle(review) is { State: ReviewCycleState.Active } activeCycleReturn
                                && activeCycleReturn.Steps.Any(step => step.State == ApprovalStepState.Active
                                    && string.Equals(step.ApproverId, actor, StringComparison.OrdinalIgnoreCase)))
                    }
                })
            });
        });

        /// Additively links Problem Reports to an Open test change request.
        ///
        /// Linking is additive: existing links are never deleted here, and the governed link set is part of
        /// the review snapshot, so any actually-added link advances the package Version in the same unit of
        /// work. A link-versus-submit race therefore collapses to one winner: whichever side saves second
        /// hits the concurrency token and receives 409 stale_version.
        app.MapPost("/api/test-change-reviews/{id:guid}/problem-reports", async (Guid id,
            LinkProblemReportsRequest request, HttpContext http, AeroLinkDbContext db, IdentityService identity,
            ProblemReportLinkService problemReports, CancellationToken ct) =>
        {
            var review = await db.TestChangeReviews.SingleOrDefaultAsync(x => x.Id == id, ct);
            if (review is null) return Results.NotFound();
            if (await db.Releases.AnyAsync(x => x.Id == review.ReleaseId && x.IsReleased, ct))
                return Results.Conflict(new { error = "Released software-build test change requests are read-only." });
            // Stale writes are refused before lifecycle checks: a caller who loaded an older version must be
            // told to refresh even when the package has also moved InReview.
            if (request.ExpectedVersion is not null && review.Version != request.ExpectedVersion)
                return Results.Conflict(new
                {
                    error = "This test change request changed after it was opened. Refresh before changing its Problem Report links.",
                    code = "stale_version"
                });
            if (review.State != TestChangeReviewState.Draft)
                return Results.Conflict(new { error = "Problem Report links can be changed only while the test change request is a Draft." });
            var refusal = await RefuseUnlessAuthoredBy(review, http, db, identity, ct);
            if (refusal is not null) return refusal;
            var error = await problemReports.ValidateSelectionAsync(review.ProjectId, review.ReleaseId,
                request.ProblemReportIds, ct);
            if (error is not null) return Results.BadRequest(new { error });
            try
            {
                var now = DateTimeOffset.UtcNow;
                var before = (await db.ProblemReportLinks.AsNoTracking()
                        .Where(x => x.ArtifactType == "TestChangeRequest" && x.ArtifactId == review.Id)
                        .Select(x => x.ProblemReportId).ToListAsync(ct))
                    .ToHashSet();
                await problemReports.LinkTestChangeRequestAsync(review.Id, request.ProblemReportIds,
                    http.UserAccount().UserName, now, ct);
                var added = (request.ProblemReportIds?.Distinct() ?? []).Any(id => !before.Contains(id));
                if (added) review.RecordControlledContentChange(now);
                await db.SaveChangesAsync(ct);
                return Results.Ok(new { review.Id, linkedProblemReports = (request.ProblemReportIds?.Distinct() ?? []) });
            }
            catch (DbUpdateConcurrencyException)
            {
                return Results.Conflict(new
                {
                    error = "This test change request changed after it was opened. Refresh and try again.",
                    code = "stale_version"
                });
            }
            catch (DomainException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        /// <summary>
        /// The test assessment's conclusion, and the point at which a test change request comes into being.
        ///
        /// Mirrors the requirements-side downstream assessment exactly, because it is the same question asked
        /// of the verification discipline: does this approved change need work here or not. Concluding that
        /// it does allocates the controlled SYSTPCR, HLRTCCR or LLRTCCR number; concluding that it does not
        /// produces nothing, and so is the conclusion that goes for approval.
        /// </summary>
        app.MapPost("/api/test-change-reviews/{id:guid}/conclusion", async (Guid id, TestAssessmentConclusionRequest request,
            HttpContext http, AeroLinkDbContext db, IdentityService identity,
            IProjectLadderPolicyResolver policyResolver, CancellationToken ct) =>
        {
            var review = await db.TestChangeReviews.SingleOrDefaultAsync(x => x.Id == id, ct);
            if (review is null) return Results.NotFound();
            if (await db.Releases.AnyAsync(x => x.Id == review.ReleaseId && x.IsReleased, ct))
                return Results.Conflict(new { error = "Released software-build test change requests are read-only." });
            var actor = http.UserAccount().UserName;
            if (!await http.HasProjectRoleAsync(db, identity, review.ProjectId, ct, ProgramRole.TestEngineer, ProgramRole.TestLead))
                return Results.Forbid();
            if (review.AssignedEngineerId is not null
                && !string.Equals(review.AssignedEngineerId, actor, StringComparison.OrdinalIgnoreCase)
                && !await http.HasProjectRoleAsync(db, identity, review.ProjectId, ct, ProgramRole.TestLead))
                return Results.Forbid();
            if (request.ExpectedVersion is not null && review.Version != request.ExpectedVersion)
                return Results.Conflict(new
                {
                    error = "This test change request changed after it was opened. Refresh before recording its conclusion.",
                    code = "stale_version"
                });
            try
            {
                var ladderPolicy = await policyResolver.ResolveAsync(review.ProjectId, ct);
                var now = DateTimeOffset.UtcNow;
                // Answering an unheld package is what takes it on. The claim is no longer a step of its own,
                // but the record of who holds it still has to be true — the next reader needs to see that
                // somebody is on it, and submission and approval both key on the holder.
                if (review.AssignedEngineerId is null) review.Assign(actor, actor, now);
                if (request.TestChangeRequired)
                {
                    review.RecordTestChangeRequired(actor, now);
                    review.AssignControlledNumber(
                    await IdentifierAllocator.NextTestChangeRequestAsync(db, review.ArtifactKey, ct, ladderPolicy), now,
                        ladderPolicy);
                }
                else review.RecordNoTestChangeRequired(actor, request.Rationale ?? "", now);
                await db.SaveChangesAsync(ct);
                return Results.Ok(new
                {
                    review.Id, outcome = review.Outcome.ToString(), review.BaseNumber, review.DisplayNumber,
                    review.NoChangeRationale, review.DecidedBy, review.DecidedAt, state = review.State.ToString()
                });
            }
            catch (DbUpdateConcurrencyException)
            {
                return Results.Conflict(new
                {
                    error = "This test change request changed after it was opened. Refresh and try again.",
                    code = "stale_version"
                });
            }
            catch (DomainException problem) { return Results.BadRequest(new { error = problem.Message }); }
        });

        // The register inspector uses the same server-owned projection for a TCR entry point. The projection
        // itself owns the typed root, so every exact source claim and Case/Procedure ancestry is walked from
        // the selected TCR rather than from an arbitrary originating CR.
        app.MapGet("/api/test-change-reviews/{id:guid}/trace", async (Guid id, HttpContext http,
            AeroLinkDbContext db, IProjectLadderPolicyResolver policyResolver, CancellationToken ct) =>
        {
            var review = await db.TestChangeReviews.AsNoTracking()
                .Where(x => x.Id == id)
                .Select(x => new { x.ProjectId })
                .SingleOrDefaultAsync(ct);
            if (review is null) return Results.NotFound();
            if (!await http.HasProjectAccessAsync(db, review.ProjectId, ct)) return Results.Forbid();
            var policy = await policyResolver.ResolveAsync(review.ProjectId, ct);
            var projection = await ChangeRequestTraceProjection.ForTestChangeReviewAsync(db, review.ProjectId,
                id, policy, ct);
            return projection is null ? Results.NotFound() : Results.Ok(projection);
        });

        // The procedure decisions a test change request carries — what the workspace reads and writes, and the
        // test-side counterpart of the requirement changes a change request carries.
        app.MapGet("/api/test-change-reviews/{id:guid}/{artifactRoute:regex(procedure-changes|case-changes)}", async (Guid id, string artifactRoute,
            HttpContext http, AeroLinkDbContext db, IdentityService identity,
            IProjectLadderPolicyResolver policyResolver, CancellationToken ct) =>
        {
            var review = await db.TestChangeReviews.AsNoTracking().Include(x => x.ProcedureChanges)
                .SingleOrDefaultAsync(x => x.Id == id, ct);
            if (review is null || !ArtifactRouteAllows(artifactRoute, review.ArtifactKey)) return Results.NotFound();
            var ladderPolicy = await policyResolver.ResolveAsync(review.ProjectId, ct);
            if (!await http.HasProjectAccessAsync(db, review.ProjectId, ct)) return Results.Forbid();
            // Derived here rather than inferred by the client from a broad role. The workspace was offering
            // authoring controls to anyone with test authority while these same rules refused them, which is
            // an invitation to an error message.
            var actor = http.UserAccount().UserName;
            var released = await db.Releases.AnyAsync(x => x.Id == review.ReleaseId && x.IsReleased, ct);
            var isTester = !released && await http.HasProjectRoleAsync(db, identity, review.ProjectId, ct, ProgramRole.TestEngineer, ProgramRole.TestLead);
            var isLead = !released && await http.HasProjectRoleAsync(db, identity, review.ProjectId, ct, ProgramRole.TestLead);
            var holdsIt = review.AssignedEngineerId is null
                || string.Equals(review.AssignedEngineerId, actor, StringComparison.OrdinalIgnoreCase)
                || isLead;
            var mayAuthor = isTester && holdsIt;
            // The picker and both server enforcement points use the same package/build-scoped set. Project and
            // discipline alone do not authorize a TCR to govern an unrelated requirement.
            var candidates = await TestChangeReviewRequirementScope.ForReviewAsync(db, review, null, ct, ladderPolicy);
            // Modify and Retire act on what this target build carries, not on the newest procedure revision
            // anywhere in the Project. Coverage is not membership and a later build is not an authoring menu.
            var effectivity = await TestProcedureEffectivity.ForReleaseAsync(
                db, review.ProjectId, review.ReleaseId, ct);
            var targetRevisionIds = effectivity?.RevisionIds ?? [];
            var requirementBaselineId = await TestChangeReviewRequirementScope
                .EffectiveRequirementBaselineIdAsync(db, review.ProjectId, review.ReleaseId, ct);
            var carriedRequirementIds = requirementBaselineId is null
                ? []
                : await db.BaselineRequirements.AsNoTracking()
                    .Where(x => x.BaselineId == requirementBaselineId.Value)
                    .Select(x => x.RevisionId).ToListAsync(ct);
            // The workspace payload hydrates only the targets its existing decisions reference; the picker
            // universe itself is served by the searchable, paged procedure-targets endpoint below. A fixed
            // Take(500) here silently truncated the authoring menu and presented it as complete.
            var referencedBaseNumbers = review.ProcedureChanges.Select(x => x.BaseNumber).Distinct().ToList();
            var targets = referencedBaseNumbers.Count == 0
                ? []
                : await (from revision in db.TestProcedureRevisions.AsNoTracking()
                             .Where(x => targetRevisionIds.Contains(x.Id))
                         join procedure in db.TestProcedures.AsNoTracking()
                             .Where(x => x.ProjectId == review.ProjectId && x.Level == review.ProcedureLevel(ladderPolicy)
                                 && (review.ArtifactKey.Kind == VerificationArtifactKind.Procedure
                                     ? x.ArtifactKind == VerificationArtifactKind.Procedure
                                     : x.Level == TestProcedureLevel.System || x.ArtifactKind == VerificationArtifactKind.Case)
                                 && referencedBaseNumbers.Contains(x.BaseNumber))
                             on revision.ProcedureId equals procedure.Id
                         orderby procedure.BaseNumber
                         select new
                         {
                             revision.Id,
                             procedure.BaseNumber,
                             CurrentRevision = revision.Revision,
                             State = revision.State,
                             revision.ParentKind,
                             revision.DerivedRationale
                         }).ToListAsync(ct);
            var targetTitles = await TestProcedureRevisionTitleProjection.ForRevisionsAsync(db,
                targets.Select(x => x.Id).Distinct().ToList(), ct);
            var targetCoverage = await (from coverage in db.TestCoverage.AsNoTracking()
                                            .Where(x => targetRevisionIds.Contains(x.ProcedureRevisionId)
                                                && carriedRequirementIds.Contains(x.RequirementRevisionId))
                                        join revision in db.RequirementRevisions.AsNoTracking()
                                            on coverage.RequirementRevisionId equals revision.Id
                                        join artifact in db.Requirements.AsNoTracking()
                                            on revision.ArtifactId equals artifact.Id
                                        select new
                                        {
                                            coverage.ProcedureRevisionId,
                                            id = artifact.Id,
                                            revisionId = revision.Id,
                                            displayNumber = artifact.BaseNumber + "." + (revision.Revision < 10 ? "0" : "") + revision.Revision,
                                            revision.Statement,
                                            level = artifact.Level.ToString(),
                                            coverage.IsSuspect
                                        }).ToListAsync(ct);
            var artifactTargets = targets.Select(x => new
            {
                x.BaseNumber, title = targetTitles[x.Id].Title,
                currentRevision = x.CurrentRevision,
                state = x.State.ToString(),
                parentKind = x.ParentKind.ToString(),
                derivedRationale = x.DerivedRationale,
                parentRevisionIds = targetCoverage.Where(c => c.ProcedureRevisionId == x.Id)
                    .Where(c => !c.IsSuspect)
                    .Select(c => c.revisionId).Distinct().OrderBy(c => c).ToArray(),
                currentCoverage = targetCoverage.Where(c => c.ProcedureRevisionId == x.Id)
                    .OrderBy(c => c.displayNumber).Select(c => new
                    {
                        c.id, c.revisionId, c.displayNumber, statement = c.Statement, c.level,
                        isSuspect = c.IsSuspect
                    }).ToList()
            }).ToList();
            var artifactChanges = review.ProcedureChanges
                .OrderBy(x => x.BaseNumber)
                .Select(x => new
                {
                    x.Id, x.DisplayNumber, x.BaseNumber, x.Revision, kind = x.Kind.ToString(),
                    level = x.Level.ToString(), x.Title, x.Objective, x.Preconditions, x.Steps,
                    x.ExpectedResult, x.Rationale,
                    drivingRequirementRevisionIds = DrivingRequirements(x.DrivingRequirementRevisionIdsJson),
                    parentKind = x.ParentKind.ToString(),
                    parentRevisionIds = DrivingRequirements(x.ParentRevisionIdsJson),
                    derivedRationale = x.DerivedRationale,
                    removedRequirementRevisionIds = DrivingRequirements(x.RemovedRequirementRevisionIdsJson),
                    x.CoverageChangeRationale, x.CoverageChangedBy
                }).ToList();
            OriginDisplay originDisplay;
            if (review.OriginKind == TestChangeReviewOriginKind.CaseChange)
            {
                var source = await db.Set<TestProcedureChange>().AsNoTracking()
                    .Where(x => x.Id == review.OriginReferenceId)
                    .Select(x => new { Identity = x.BaseNumber + "." + (x.Revision < 10 ? "0" : "") + x.Revision, x.Title }).SingleOrDefaultAsync(ct);
                originDisplay = source is null
                    ? new("Case change", review.SourceCaseOriginNumber, "")
                    : new("Case change", source.Identity, source.Title);
            }
            else if (review.OriginKind == TestChangeReviewOriginKind.CaseAssessment)
            {
                var source = await db.VerificationImpactItems.AsNoTracking()
                    .Where(x => x.Id == review.OriginReferenceId)
                    .Select(x => new { x.SubjectDisplayNumber, x.Outcome, x.ResolutionRationale })
                    .SingleOrDefaultAsync(ct);
                originDisplay = source is null
                    ? new("Case assessment", review.SourceCaseOriginNumber, "")
                    : new("Case assessment", source.SubjectDisplayNumber,
                        $"{source.Outcome} · {source.ResolutionRationale}");
            }
            else if (review.OriginKind == TestChangeReviewOriginKind.CaseReview)
            {
                var source = await db.TestChangeReviews.AsNoTracking()
                    .Where(x => x.Id == review.OriginReferenceId)
                    .Select(x => new { x.Title }).SingleOrDefaultAsync(ct);
                originDisplay = new("Case TCR", review.SourceCaseOriginNumber,
                    source?.Title ?? "Approved Case change-control package");
            }
            else if (review.OriginKind == TestChangeReviewOriginKind.ChangeRequest)
            {
                var title = await db.SystemChangeRequests.AsNoTracking()
                    .Where(x => x.Id == review.OriginReferenceId).Select(x => x.Title).SingleOrDefaultAsync(ct);
                originDisplay = new("Change request", review.SourceChangeRequestNumber, title ?? "Source change request");
            }
            else
            {
                var source = await db.ProblemReports.AsNoTracking()
                    .Where(x => x.Id == review.OriginReferenceId)
                    .Select(x => new { x.DisplayNumber, x.Title }).SingleOrDefaultAsync(ct);
                originDisplay = new("Problem Report", source?.DisplayNumber ?? review.SourceProblemReportNumber,
                    source?.Title ?? "Source Problem Report");
            }
            return Results.Ok(new
            {
                artifactKind = TestChangeRequestSourceEligibility.ArtifactKind(review.ArtifactKey),
                artifactLabel = TestChangeRequestSourceEligibility.ArtifactLabel(review.ArtifactKey),
                capabilities = new
                {
                    canProposeArtifactChange = mayAuthor && review.State == TestChangeReviewState.Draft
                        && review.Outcome == TestChangeReviewOutcome.ChangeRequired,
                    canWithdrawArtifactChange = mayAuthor && review.State == TestChangeReviewState.Draft,
                    canProposeProcedureChange = mayAuthor && review.State == TestChangeReviewState.Draft
                        && review.Outcome == TestChangeReviewOutcome.ChangeRequired, // compatibility alias
                    canWithdrawProcedureChange = mayAuthor && review.State == TestChangeReviewState.Draft, // compatibility alias
                    canRevise = mayAuthor && review.State == TestChangeReviewState.Approved,
                },
                drivingRequirementChoices = candidates,
                artifactTargets,
                procedureTargets = artifactTargets, // compatibility alias
                review.Id, review.DisplayNumber, review.BaseNumber, review.Revision,
                review.ProjectId, releaseId = review.ReleaseId,
                discipline = review.Discipline.ToString(), state = review.State.ToString(),
                originKind = review.OriginKind.ToString(), originReferenceId = review.OriginReferenceId,
                originDisplayLabel = originDisplay.Label, originDisplayIdentity = originDisplay.Identity,
                originDisplayTitle = originDisplay.Title,
                outcome = review.Outcome.ToString(),
                artifactLevel = review.ProcedureLevel(ladderPolicy).ToString(),
                procedureLevel = review.ProcedureLevel(ladderPolicy).ToString(), // compatibility alias
                review.SourceChangeRequestNumber, review.AssignedEngineerId,
                version = review.Version,
                review.Title, review.Problem, review.Analysis, review.Solution,
                review.ProblemRich, review.AnalysisRich, review.SolutionRich,
                review.CaseContractVersion,
                artifactDecisionCount = artifactChanges.Count,
                procedureDecisionCount = artifactChanges.Count, // compatibility alias
                artifactChanges,
                procedureChanges = artifactChanges // compatibility alias
            });
        });

        // The searchable Modify/Retire picker: the exact procedure universe the selected build's manifest
        // carries for this package's discipline, bounded by server-side search and paging with totals.
        // Hydration by immutable procedure ID or controlled base number keeps an exact current selection
        // visible even when it lies beyond the current result page — and forged out-of-scope IDs hydrate
        // nothing because the scoped source is the only thing ever queried.
        app.MapGet("/api/test-change-reviews/{id:guid}/{artifactRoute:regex(procedure-targets|case-targets)}", async (Guid id, string artifactRoute, string? search,
            string? ids, string? baseNumbers, int? page, int? pageSize,
            HttpContext http, AeroLinkDbContext db, IProjectLadderPolicyResolver policyResolver, CancellationToken ct) =>
        {
            var review = await db.TestChangeReviews.AsNoTracking()
                .SingleOrDefaultAsync(x => x.Id == id, ct);
            if (review is null || !ArtifactRouteAllows(artifactRoute, review.ArtifactKey)) return Results.NotFound();
            var ladderPolicy = await policyResolver.ResolveAsync(review.ProjectId, ct);
            if (!await http.HasProjectAccessAsync(db, review.ProjectId, ct)) return Results.Forbid();
            var currentPage = Math.Max(1, page ?? 1);
            var size = Math.Clamp(pageSize ?? 25, 1, 200);

            var effectivity = await TestProcedureEffectivity.ForReleaseAsync(
                db, review.ProjectId, review.ReleaseId, ct);
            var targetRevisionIds = effectivity?.RevisionIds ?? [];
            var requirementBaselineId = await TestChangeReviewRequirementScope
                .EffectiveRequirementBaselineIdAsync(db, review.ProjectId, review.ReleaseId, ct);
            var carriedRequirementIds = requirementBaselineId is null
                ? []
                : await db.BaselineRequirements.AsNoTracking()
                    .Where(x => x.BaselineId == requirementBaselineId.Value)
                    .Select(x => x.RevisionId).ToListAsync(ct);

            var eligibility = from revision in db.TestProcedureRevisions.AsNoTracking()
                                  .Where(x => targetRevisionIds.Contains(x.Id))
                              join procedure in db.TestProcedures.AsNoTracking()
                                  .Where(x => x.ProjectId == review.ProjectId && x.Level == review.ProcedureLevel(ladderPolicy)
                                      && (review.ArtifactKey.Kind == VerificationArtifactKind.Procedure
                                          ? x.ArtifactKind == VerificationArtifactKind.Procedure
                                          : x.Level == TestProcedureLevel.System || x.ArtifactKind == VerificationArtifactKind.Case))
                                  on revision.ProcedureId equals procedure.Id
                              select new
                              {
                                  revision.Id,
                                  ProcedureId = procedure.Id,
                                  procedure.BaseNumber,
                                  CurrentRevision = revision.Revision,
                             State = revision.State,
                             revision.ParentKind,
                             revision.DerivedRationale
                              };
            var query = eligibility;
            if (!string.IsNullOrWhiteSpace(search))
            {
                var q = search.Trim().ToLower();
                var titleRevisionIds = await TestProcedureRevisionTitleProjection.MatchingRevisionIdsAsync(
                    db, targetRevisionIds, q, ct);
                query = query.Where(x => x.BaseNumber.ToLower().Contains(q)
                    || titleRevisionIds.Contains(x.Id));
            }
            var total = await query.CountAsync(ct);
            var paged = await query.OrderBy(x => x.BaseNumber).ThenBy(x => x.ProcedureId)
                .Skip((currentPage - 1) * size).Take(size).ToListAsync(ct);
            var requestedIds = (ids ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(x => Guid.TryParse(x, out var value) ? value : Guid.Empty)
                .Where(x => x != Guid.Empty).Distinct().ToList();
            var requestedBaseNumbers = (baseNumbers ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var hydrated = requestedIds.Count == 0 && requestedBaseNumbers.Count == 0
                ? []
                : await eligibility
                    .Where(x => requestedIds.Contains(x.ProcedureId) || requestedBaseNumbers.Contains(x.BaseNumber!))
                    .ToListAsync(ct);
            var all = paged.Concat(hydrated).DistinctBy(x => x.ProcedureId)
                .OrderBy(x => x.BaseNumber).ThenBy(x => x.ProcedureId).ToList();
            var exactTitles = await TestProcedureRevisionTitleProjection.ForRevisionsAsync(db,
                all.Select(x => x.Id).Distinct().ToList(), ct);
            var coverageRows = await (from coverage in db.TestCoverage.AsNoTracking()
                                          .Where(x => all.Select(t => t.Id).Contains(x.ProcedureRevisionId)
                                              && carriedRequirementIds.Contains(x.RequirementRevisionId))
                                      join revision in db.RequirementRevisions.AsNoTracking()
                                          on coverage.RequirementRevisionId equals revision.Id
                                      join artifact in db.Requirements.AsNoTracking()
                                          on revision.ArtifactId equals artifact.Id
                                      select new
                                      {
                                          coverage.ProcedureRevisionId,
                                          id = artifact.Id,
                                          revisionId = revision.Id,
                                          displayNumber = artifact.BaseNumber + "." + (revision.Revision < 10 ? "0" : "") + revision.Revision,
                                          revision.Statement,
                                          level = artifact.Level.ToString(),
                                          coverage.IsSuspect
                                      }).ToListAsync(ct);
            var artifactItems = all.Select(x => new
            {
                artifactId = x.ProcedureId,
                procedureId = x.ProcedureId, // compatibility alias for clients before the neutral seam
                x.BaseNumber,
                title = exactTitles[x.Id].Title,
                currentRevision = x.CurrentRevision,
                state = x.State.ToString(),
                parentKind = x.ParentKind.ToString(),
                derivedRationale = x.DerivedRationale,
                parentRevisionIds = coverageRows.Where(c => c.ProcedureRevisionId == x.Id)
                    .Where(c => !c.IsSuspect)
                    .Select(c => c.revisionId).Distinct().OrderBy(c => c).ToArray(),
                currentCoverage = coverageRows.Where(c => c.ProcedureRevisionId == x.Id)
                    .OrderBy(c => c.displayNumber).Select(c => new
                    {
                        c.id, c.revisionId, c.displayNumber, statement = c.Statement, c.level,
                        isSuspect = c.IsSuspect
                    }).ToList()
            }).ToList();
            return Results.Ok(new
            {
                page = currentPage,
                pageSize = size,
                totalCount = total,
                totalPages = (int)Math.Ceiling(total / (double)size),
                artifactKind = TestChangeRequestSourceEligibility.ArtifactKind(review.ArtifactKey),
                artifactLabel = TestChangeRequestSourceEligibility.ArtifactLabel(review.ArtifactKey),
                artifactTargets = artifactItems,
                items = artifactItems,
                procedureTargets = artifactItems // compatibility alias
            });
        });

        // The searchable driving-requirement picker: the same governed, build-scoped candidate set the
        // mutation enforcement uses, with search, stable paging, totals and exact-ID hydration. The
        // projection retains the complete requirement identity (#413): artifact Id, revisionId,
        // displayNumber, statement and level.
        app.MapGet("/api/test-change-reviews/{id:guid}/requirement-candidates", async (Guid id, string? search,
            string? ids, int? page, int? pageSize,
            HttpContext http, AeroLinkDbContext db, IProjectLadderPolicyResolver policyResolver, CancellationToken ct) =>
        {
            var review = await db.TestChangeReviews.AsNoTracking()
                .SingleOrDefaultAsync(x => x.Id == id, ct);
            if (review is null) return Results.NotFound();
            if (!await http.HasProjectAccessAsync(db, review.ProjectId, ct)) return Results.Forbid();
            var ladderPolicy = await policyResolver.ResolveAsync(review.ProjectId, ct);
            var currentPage = Math.Max(1, page ?? 1);
            var size = Math.Clamp(pageSize ?? 25, 1, 200);
            if (review.ArtifactKey.Kind == VerificationArtifactKind.Procedure
                && review.ArtifactKey.Discipline != VerificationDiscipline.System)
            {
                var effectivity = await TestProcedureEffectivity.ForReleaseAsync(db, review.ProjectId, review.ReleaseId, ct);
                var carried = effectivity?.RevisionIds ?? [];
                var rows = await (from revision in db.TestProcedureRevisions.AsNoTracking()
                                  join procedure in db.TestProcedures.AsNoTracking()
                                      on revision.ProcedureId equals procedure.Id
                                  where carried.Contains(revision.Id)
                                      && procedure.ProjectId == review.ProjectId
                                      && procedure.ArtifactKind == VerificationArtifactKind.Case
                                      && procedure.Level == review.ProcedureLevel(ladderPolicy)
                                  select new { procedure.Id, RevisionId = revision.Id, procedure.BaseNumber, revision.Revision })
                    .ToListAsync(ct);
                var titles = await TestProcedureRevisionTitleProjection.ForRevisionsAsync(db,
                    rows.Select(x => x.RevisionId).ToList(), ct);
                var query = rows.Select(x => new TestChangeReviewRequirementChoice(x.Id, x.RevisionId,
                    $"{x.BaseNumber}.{x.Revision:D2}", titles[x.RevisionId].Title, ladderPolicy.RequirementLevelFor(review.ProcedureLevel(ladderPolicy))))
                    .OrderBy(x => x.DisplayNumber).AsEnumerable();
                if (!string.IsNullOrWhiteSpace(search))
                {
                    var q = search.Trim();
                    query = query.Where(x => x.DisplayNumber.Contains(q, StringComparison.OrdinalIgnoreCase)
                        || x.Statement.Contains(q, StringComparison.OrdinalIgnoreCase)).OrderBy(x => x.DisplayNumber);
                }
                var procedureTotal = query.Count();
                var selected = query.Skip((currentPage - 1) * size).Take(size).ToList();
                return Results.Ok(new
                {
                    page = currentPage,
                    pageSize = size,
                    totalCount = procedureTotal,
                    totalPages = (int)Math.Ceiling(procedureTotal / (double)size),
                    items = selected,
                    artifactKind = review.ArtifactKind.ToString(),
                    artifactLabel = TestChangeRequestSourceEligibility.ArtifactLabel(review.ArtifactKey)
                });
            }
            var requestedIds = (ids ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(x => Guid.TryParse(x, out var value) ? value : Guid.Empty)
                .Where(x => x != Guid.Empty).Distinct().ToList();
            var (total, items) = await TestChangeReviewRequirementScope.ForReviewPageAsync(
                db, review, search, currentPage, size, requestedIds, ct, ladderPolicy);
            return Results.Ok(new
            {
                page = currentPage,
                pageSize = size,
                totalCount = total,
                totalPages = (int)Math.Ceiling(total / (double)size),
                items
            });
        });

        app.MapPost("/api/test-change-reviews/{id:guid}/{artifactRoute:regex(procedure-changes|case-changes)}", async (Guid id, string artifactRoute,
            ProposeProcedureChangeRequest request, HttpContext http, AeroLinkDbContext db,
            IdentityService identity, IProjectLadderPolicyResolver policyResolver, CancellationToken ct) =>
        {
            var review = await db.TestChangeReviews.Include(x => x.ProcedureChanges).Include(x => x.ReviewCycles)
                .SingleOrDefaultAsync(x => x.Id == id, ct);
            if (review is null || !ArtifactRouteAllows(artifactRoute, review.ArtifactKey)) return Results.NotFound();
            var ladderPolicy = await policyResolver.ResolveAsync(review.ProjectId, ct);
            var refusal = await RefuseUnlessAuthoredBy(review, http, db, identity, ct);
            if (refusal is not null) return refusal;
            if (request.ExpectedVersion is not null && review.Version != request.ExpectedVersion)
                return Results.Conflict(new
                {
                    error = $"This test change request changed after it was opened. Refresh before proposing a {TestChangeRequestSourceEligibility.ArtifactNoun(review.ArtifactKey)} change.",
                    code = "stale_version"
                });
            try
            {
                var now = DateTimeOffset.UtcNow;
                var isProcedurePackage = review.ArtifactKey.Kind == VerificationArtifactKind.Procedure
                    && review.ArtifactKey.Discipline != VerificationDiscipline.System;
                var artifactWord = TestChangeRequestSourceEligibility.ArtifactWord(review.ArtifactKey);
                var artifactNoun = TestChangeRequestSourceEligibility.ArtifactNoun(review.ArtifactKey);
                var driving = request.DrivingRequirementRevisionIds ?? [];
                if (isProcedurePackage && driving.Length != 0)
                    return Results.BadRequest(new { error = "A software Procedure selects exact Case parents, not requirement revisions.", code = "procedure_parent_kind_mismatch" });
                var parentIds = request.ParentRevisionIds ?? driving;
                var parentKind = request.ParentKind != VerificationProcedureParentKind.Unspecified
                    ? request.ParentKind
                    : parentIds.Length > 0
                        ? VerificationProcedureParentKind.Allocated
                        : VerificationProcedureParentKind.Unspecified;
                // A derived artifact is intentionally standalone. Do not normalize
                // an explicit parent/driving selection away: that would turn the
                // invalid "derived with parents" combination into a valid draft and
                // make alternate clients disagree about the review contract. Empty
                // arrays are harmless; any supplied identity is an explicit XOR
                // violation and is refused before persistence.
                if (parentKind == VerificationProcedureParentKind.Derived
                    && (request.ParentRevisionIds?.Length > 0 || driving.Length > 0))
                    return Results.BadRequest(new
                    {
                        error = $"A derived {artifactNoun} cannot carry exact parent revisions.",
                        code = "derived_parent_conflict"
                    });
                var removed = request.RemovedRequirementRevisionIds ?? [];
                if (request.Kind != TestProcedureChangeKind.Modify && removed.Length != 0)
                    return Results.BadRequest(new
                    {
                        error = $"Only a {artifactWord} modification can remove existing coverage.",
                        code = "coverage_removal_requires_modify"
                    });
                if (driving.Intersect(removed).Any())
                    return Results.BadRequest(new
                    {
                        error = $"A requirement cannot be both added and removed by one {artifactNoun} change.",
                        code = "coverage_delta_conflict"
                    });
                // A Modify parent list is the full successor selection. Retained parents belong to the
                // carried revision and need not be in this package's fresh impact delta; only fresh driving
                // and removal identities are scoped eagerly. The final selection is checked below against
                // the package scope plus the carried coverage.
                var scopedRequirementIds = (request.Kind == TestProcedureChangeKind.Modify
                        ? driving.Concat(removed)
                        : parentIds.Concat(driving).Concat(removed))
                    .Distinct().ToArray();
                if (!isProcedurePackage && scopedRequirementIds.Length != 0)
                {
                    var known = await (from revision in db.RequirementRevisions.AsNoTracking()
                                       join artifact in db.Requirements.AsNoTracking() on revision.ArtifactId equals artifact.Id
                                       where scopedRequirementIds.Contains(revision.Id)
                                       select new { revision.Id, artifact.ProjectId, artifact.Level })
                        .ToDictionaryAsync(x => x.Id, ct);
                    var wanted = ApiMap.RequirementLevelFor(review.ProcedureLevel(ladderPolicy), ladderPolicy);
                    foreach (var drivingId in scopedRequirementIds)
                    {
                        if (!known.TryGetValue(drivingId, out var requirement))
                            return Results.BadRequest(new { error = $"Requirement revision {drivingId} does not exist.", code = "requirement_revision_not_found" });
                        if (requirement.ProjectId != review.ProjectId)
                            return Results.BadRequest(new { error = $"Requirement revision {drivingId} belongs to another project.", code = "requirement_revision_project_mismatch" });
                        if (requirement.Level != wanted)
                            return Results.BadRequest(new { error = $"Requirement revision {drivingId} is a {requirement.Level} requirement, which a {review.Discipline} {artifactNoun} does not verify.", code = "requirement_revision_level_mismatch" });
                    }
                    var governed = await TestChangeReviewRequirementScope.ForReviewAsync(db, review, null, ct, ladderPolicy);
                    var governedIds = governed.Select(x => x.RevisionId).ToHashSet();
                    var outside = scopedRequirementIds.FirstOrDefault(x => !governedIds.Contains(x));
                    if (outside != Guid.Empty)
                        return Results.BadRequest(new
                        {
                            error = $"Requirement revision {outside} is outside this test change request's governed package/build scope.",
                            code = "requirement_revision_outside_tcr_scope"
                        });
                }
                // Introducing allocates; modifying or retiring names what already exists. Letting the caller
                // choose a number for a new procedure would let two engineers pick the same one.
                var baseNumber = request.Kind == TestProcedureChangeKind.Introduce
                    ? await IdentifierAllocator.NextTestProcedureAsync(db, review.ProcedureLevel(ladderPolicy), ct, ladderPolicy,
                        review.ArtifactKey.Kind)
                    : (request.BaseNumber ?? "").Trim();
                if (request.Kind != TestProcedureChangeKind.Introduce && baseNumber.Length == 0)
                    return Results.BadRequest(new { error = $"A modification or retirement must name the {artifactNoun} it acts on." });
                var currentCoverageIds = new HashSet<Guid>();
                // Deliberately no "must name a requirement revision" rule here, though the direct-create route
                // that this replaced had one. That route wrote a controlled procedure immediately, so it
                // needed its coverage at that moment; a package only proposes, and a proposal's driving
                // revisions become real coverage at materialization. Whether a package may introduce a
                // procedure that names nothing is a product question, not a gap to be closed by reflex.
                // A modification or retirement names a controlled procedure, so the server proves it is one:
                // that it exists, belongs to this project, sits at this discipline's level, and that the
                // proposed revision advances the one it actually has. Left unchecked, a typo survived approval
                // and failed at materialization, which puts an authoring mistake in the release path.
                if (request.Kind != TestProcedureChangeKind.Introduce)
                {
                    var target = await db.TestProcedures.AsNoTracking()
                        .Where(x => x.BaseNumber == baseNumber
                            && (review.ArtifactKey.Kind == VerificationArtifactKind.Procedure
                                ? x.ArtifactKind == VerificationArtifactKind.Procedure
                                : x.Level == TestProcedureLevel.System || x.ArtifactKind == VerificationArtifactKind.Case))
                        .Select(x => new { x.Id, x.ProjectId, x.Level }).SingleOrDefaultAsync(ct);
                    if (target is null)
                        return Results.BadRequest(new { error = $"{baseNumber} is not a controlled {artifactWord}." });
                    if (target.ProjectId != review.ProjectId)
                        return Results.BadRequest(new { error = $"{baseNumber} belongs to another project." });
                    if (target.Level != review.ProcedureLevel(ladderPolicy))
                        return Results.BadRequest(new { error = $"{baseNumber} is a {target.Level} {artifactNoun} and cannot be changed by a {review.Discipline} test change request." });
                    var effectivity = await TestProcedureEffectivity.ForReleaseAsync(
                        db, review.ProjectId, review.ReleaseId, ct);
                    if (effectivity is null || !effectivity.RevisionByProcedure.TryGetValue(target.Id, out var carriedRevisionId))
                        return Results.Conflict(new
                        {
                            error = $"{baseNumber} is not carried by the target software build. Refresh the {artifactNoun} list and reselect a current target.",
                            code = "procedure_not_carried_by_build"
                        });
                    var current = await db.TestProcedureRevisions.AsNoTracking()
                        .Where(x => x.Id == carriedRevisionId).Select(x => (int?)x.Revision)
                        .SingleOrDefaultAsync(ct);
                    if (current is null)
                        return Results.Conflict(new
                        {
                            error = $"The target build's selected revision for {baseNumber} is no longer available. Refresh the {artifactNoun} list and reselect a current target.",
                            code = "procedure_manifest_revision_missing"
                        });
                    if (request.Revision != current.Value + 1)
                        return Results.Conflict(new
                        {
                            error = $"{baseNumber}.{current.Value:D2} is now carried by the target build. Refresh the {artifactNoun} list and reselect it before proposing revision {current.Value + 1:D2}.",
                            code = "procedure_revision_not_next_for_build"
                        });
                    var targetRequirementBaselineId = await TestChangeReviewRequirementScope
                        .EffectiveRequirementBaselineIdAsync(db, review.ProjectId, review.ReleaseId, ct);
                    if (targetRequirementBaselineId is Guid targetBaselineId)
                    {
                        var targetRequirementIds = (await db.BaselineRequirements.AsNoTracking()
                            .Where(x => x.BaselineId == targetBaselineId)
                            .Select(x => x.RevisionId)
                            .ToListAsync(ct)).ToHashSet();
                        currentCoverageIds = (await db.TestCoverage.AsNoTracking()
                                .Where(x => x.ProcedureRevisionId == carriedRevisionId
                                    && !x.IsSuspect
                                    && targetRequirementIds.Contains(x.RequirementRevisionId))
                                .Select(x => x.RequirementRevisionId).ToListAsync(ct)).ToHashSet();
                    }
                }

                if (request.Kind == TestProcedureChangeKind.Modify && !isProcedurePackage)
                {
                    var absent = removed.Distinct().FirstOrDefault(x => !currentCoverageIds.Contains(x));
                    if (absent != Guid.Empty)
                        return Results.BadRequest(new
                        {
                            error = $"Requirement revision {absent} is not currently covered by {baseNumber} and cannot be removed.",
                            code = "coverage_removal_not_current"
                        });
                    if (request.ParentRevisionIds is null
                        && parentKind != VerificationProcedureParentKind.Derived)
                        parentIds = currentCoverageIds.Except(removed).Concat(driving).Distinct().ToArray();
                    if (request.ParentKind == VerificationProcedureParentKind.Unspecified)
                        parentKind = parentIds.Length > 0
                            ? VerificationProcedureParentKind.Allocated
                            : VerificationProcedureParentKind.Unspecified;
                    var addsOrRemovesCoverage = !currentCoverageIds.SetEquals(parentIds.ToHashSet());
                    if (parentKind != VerificationProcedureParentKind.Derived
                        && addsOrRemovesCoverage
                        && string.IsNullOrWhiteSpace(request.CoverageChangeRationale))
                        return Results.BadRequest(new
                        {
                            error = "Explain why this modification adds or removes requirement coverage.",
                            code = "coverage_delta_rationale_required"
                        });
                    // The first scope check above covers the request's delta. A modify
                    // with no explicit parent list derives its full successor set from
                    // the carried revision, so validate that resolved set as well.
                    var finalParentIds = parentIds.Distinct().ToArray();
                    if (finalParentIds.Length != 0)
                    {
                        var finalKnown = await (from revision in db.RequirementRevisions.AsNoTracking()
                                                join artifact in db.Requirements.AsNoTracking() on revision.ArtifactId equals artifact.Id
                                                where finalParentIds.Contains(revision.Id)
                                                select new { revision.Id, artifact.ProjectId, artifact.Level })
                            .ToDictionaryAsync(x => x.Id, ct);
                        var wanted = ApiMap.RequirementLevelFor(review.ProcedureLevel(ladderPolicy), ladderPolicy);
                        foreach (var finalId in finalParentIds)
                        {
                            if (!finalKnown.TryGetValue(finalId, out var requirement))
                                return Results.BadRequest(new { error = $"Requirement revision {finalId} does not exist.", code = "requirement_revision_not_found" });
                            if (requirement.ProjectId != review.ProjectId)
                                return Results.BadRequest(new { error = $"Requirement revision {finalId} belongs to another project.", code = "requirement_revision_project_mismatch" });
                            if (requirement.Level != wanted)
                                return Results.BadRequest(new { error = $"Requirement revision {finalId} is a {requirement.Level} requirement, which a {review.Discipline} {artifactNoun} does not verify.", code = "requirement_revision_level_mismatch" });
                        }
                        var finalGoverned = await TestChangeReviewRequirementScope.ForReviewAsync(db, review, null, ct, ladderPolicy);
                        var finalGovernedIds = finalGoverned.Select(x => x.RevisionId).ToHashSet();
                        finalGovernedIds.UnionWith(currentCoverageIds);
                        var finalOutside = finalParentIds.FirstOrDefault(x => !finalGovernedIds.Contains(x));
                        if (finalOutside != Guid.Empty)
                            return Results.BadRequest(new
                            {
                                error = $"Requirement revision {finalOutside} is outside this test change request's governed package/build scope.",
                                code = "requirement_revision_outside_tcr_scope"
                            });
                    }
                    var finalCoverage = parentIds.Distinct().Count();
                    if (finalCoverage == 0 && parentKind != VerificationProcedureParentKind.Derived)
                        return Results.BadRequest(new
                        {
                            error = $"A modified {artifactNoun} must retain or add at least one exact requirement revision. Retire the {artifactNoun} instead if it verifies nothing in this build.",
                            code = "procedure_final_coverage_required"
                        });
                }

                if (isProcedurePackage && request.Kind != TestProcedureChangeKind.Retire)
                {
                    ExactParentSelectionPolicy.Validate(
                        VerificationProcedureParentPolicy.Classification(parentKind), parentIds,
                        request.DerivedRationale, "software Procedure");
                    if (parentKind == VerificationProcedureParentKind.Allocated)
                    {
                        var effectivity = await TestProcedureEffectivity.ForReleaseAsync(db, review.ProjectId, review.ReleaseId, ct);
                        var carriedCaseIds = effectivity?.RevisionIds.ToHashSet() ?? [];
                        var caseIds = await (from revision in db.TestProcedureRevisions.AsNoTracking()
                                             join procedure in db.TestProcedures.AsNoTracking()
                                                 on revision.ProcedureId equals procedure.Id
                                             where parentIds.Contains(revision.Id)
                                                 && procedure.ProjectId == review.ProjectId
                                                 && procedure.ArtifactKind == VerificationArtifactKind.Case
                                                 && procedure.Level == review.ProcedureLevel(ladderPolicy)
                                             select revision.Id).ToListAsync(ct);
                        var missing = parentIds.FirstOrDefault(x => !caseIds.Contains(x) || !carriedCaseIds.Contains(x));
                        if (missing != Guid.Empty)
                            return Results.BadRequest(new { error = $"Case revision {missing} is not an exact Case parent selected by this Project and build.", code = "case_parent_out_of_scope" });
                    }
                }

                // A test-change package is incrementally authored.  Keep scope
                // checks above eager for any IDs the draft supplies, but defer
                // the Allocated/Derived XOR (including a blank rationale) to
                // SubmitForReview.  The aggregate and materialization/save
                // boundaries still enforce it before controlled state changes.
                var change = review.AddProcedureChange(http.UserAccount().UserName, new TestProcedureChangeDraft(
                    baseNumber, request.Revision, review.ProcedureLevel(ladderPolicy), request.Kind, request.Title ?? "",
                    request.Objective ?? "", request.Preconditions ?? "", request.Steps ?? "",
                    request.ExpectedResult ?? "", request.Rationale ?? "",
                    JsonSerializer.Serialize(driving.Distinct()), JsonSerializer.Serialize(removed.Distinct()),
                    request.CoverageChangeRationale ?? "", parentKind,
                    JsonSerializer.Serialize(parentIds.Distinct()), request.DerivedRationale ?? "",
                    request.EnvironmentSetup ?? "", request.TestData ?? "", request.OrderedSteps ?? "",
                    request.ExpectedObservations ?? "", request.Cleanup ?? "", request.ToolingAutomation ?? ""), now,
                    policy: ladderPolicy);
                await db.SaveChangesAsync(ct);
                return Results.Ok(new
                {
                    change.Id, change.DisplayNumber, change.BaseNumber, change.Revision,
                    kind = change.Kind.ToString(), level = change.Level.ToString(), change.Title
                });
            }
            catch (DbUpdateConcurrencyException)
            {
                return Results.Conflict(new
                {
                    error = "This test change request changed after it was opened. Refresh and try again.",
                    code = "stale_version"
                });
            }
            catch (DomainException problem) { return Results.BadRequest(new { error = problem.Message }); }
        });

        app.MapDelete("/api/test-change-reviews/{id:guid}/{artifactRoute:regex(procedure-changes|case-changes)}/{changeId:guid}", async (Guid id, string artifactRoute,
            Guid changeId, long? expectedVersion, HttpContext http, AeroLinkDbContext db,
            IdentityService identity, CancellationToken ct) =>
        {
            var review = await db.TestChangeReviews.Include(x => x.ProcedureChanges)
                .SingleOrDefaultAsync(x => x.Id == id, ct);
            if (review is null || !ArtifactRouteAllows(artifactRoute, review.ArtifactKey)) return Results.NotFound();
            var refusal = await RefuseUnlessAuthoredBy(review, http, db, identity, ct);
            if (refusal is not null) return refusal;
            if (expectedVersion is not null && review.Version != expectedVersion)
                return Results.Conflict(new
                {
                    error = $"This test change request changed after it was opened. Refresh before withdrawing a {TestChangeRequestSourceEligibility.ArtifactNoun(review.ArtifactKey)} change.",
                    code = "stale_version"
                });
            try
            {
                review.RemoveProcedureChange(changeId, DateTimeOffset.UtcNow);
                await db.SaveChangesAsync(ct);
                return Results.Ok(new { review.Id, remaining = review.ProcedureChanges.Count });
            }
            catch (DbUpdateConcurrencyException)
            {
                return Results.Conflict(new
                {
                    error = "This test change request changed after it was opened. Refresh and try again.",
                    code = "stale_version"
                });
            }
            catch (DomainException problem) { return Results.BadRequest(new { error = problem.Message }); }
        });

        // Reopening approved test work to correct it, exactly as a change request advances to its next revision.
        app.MapPost("/api/test-change-reviews/{id:guid}/revise", async (Guid id, HttpContext http,
            AeroLinkDbContext db, IdentityService identity, IProjectLadderPolicyResolver policyResolver, CancellationToken ct) =>
        {
            // AdditionalSources as well as ProcedureChanges: the successor takes the folded-in claims with it,
            // and an unloaded collection is an empty one. The claims would stay on the predecessor and the new
            // revision would quietly cover less, which is the exact outcome moving them is meant to prevent.
            var review = await db.TestChangeReviews.Include(x => x.ProcedureChanges).Include(x => x.AdditionalSources)
                .SingleOrDefaultAsync(x => x.Id == id, ct);
            if (review is null) return Results.NotFound();
            // Correcting approved work belongs to the engineer who holds it. A lead may still step in, which is
            // supervision rather than a second author, but an unrelated engineer starting a successor revision
            // would change the lineage of somebody else's controlled package without anyone deciding it should.
            var reviseRefusal = await RefuseUnlessAuthoredBy(review, http, db, identity, ct);
            if (reviseRefusal is not null) return reviseRefusal;
            try
            {
                var ladderPolicy = await policyResolver.ResolveAsync(review.ProjectId, ct);
                var released = await db.Releases.AnyAsync(x => x.Id == review.ReleaseId && x.IsReleased, ct);
                var now = DateTimeOffset.UtcNow;
                var next = review.StartNextRevision(http.UserAccount().UserName, now, released, ladderPolicy);
                db.TestChangeReviews.Add(next);
                // Superseded in the same unit of work as its successor is created. Two revisions both reading
                // as current is worse than either state on its own: configuration management could carry the
                // obsolete one into a build while an engineer is correcting it.
                review.Supersede(next.Id, $"Superseded by controlled revision {next.DisplayNumber}.", now);
                await db.SaveChangesAsync(ct);
                return Results.Ok(new
                {
                    next.Id, next.DisplayNumber, next.Revision, state = next.State.ToString(),
                    outcome = next.Outcome.ToString(),
                    artifactChangeCount = next.ProcedureChanges.Count,
                    procedureChanges = next.ProcedureChanges.Count, // compatibility alias
                    coveredChangeRequests = next.CoveredChangeRequestIds.Count(),
                });
            }
            catch (DbUpdateException)
            {
                return Results.Conflict(new { error = "A later revision of this test change request already exists. Refresh to see the current record." });
            }
            catch (DomainException problem) { return Results.BadRequest(new { error = problem.Message }); }
        });

        app.MapPost("/api/test-change-reviews/{id:guid}/assign", async (Guid id, AssignVerificationImpactRequest request,
            HttpContext http, AeroLinkDbContext db, IdentityService identity, CancellationToken ct) =>
        {
            var review = await db.TestChangeReviews.SingleOrDefaultAsync(x => x.Id == id, ct);
            if (review is null) return Results.NotFound();
            if (await db.Releases.AnyAsync(x => x.Id == review.ReleaseId && x.IsReleased, ct))
                return Results.Conflict(new { error = "Released software-build test change requests are read-only." });

            var actor = http.UserAccount().UserName;
            var selfClaim = string.Equals(actor, request.EngineerId, StringComparison.OrdinalIgnoreCase);
            var mayClaim = selfClaim && await http.HasProjectRoleAsync(db, identity, review.ProjectId, ct, ProgramRole.TestEngineer);
            var mayAssign = await http.HasProjectRoleAsync(db, identity, review.ProjectId, ct, ProgramRole.TestLead);
            if (!mayClaim && !mayAssign) return Results.Forbid();

            var target = await db.UserAccounts.AsNoTracking().SingleOrDefaultAsync(x =>
                x.UserName == request.EngineerId.Trim().ToLowerInvariant() && x.State == AccountState.Active, ct);
            if (target is null) return Results.BadRequest(new { error = "Select an active AeroLink test engineer." });
            var programId = await db.Projects.AsNoTracking().Where(x => x.Id == review.ProjectId)
                .Select(x => x.ProgramId).SingleAsync(ct);
            if (!await identity.HasRoleAsync(target.Id, programId, ProgramRole.TestEngineer, DateTimeOffset.UtcNow, ct))
                return Results.BadRequest(new { error = $"{target.DisplayName} does not hold Test Engineer authority for this Program." });

            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            try
            {
                var now = DateTimeOffset.UtcNow;
                var items = await db.VerificationImpactItems.Where(x => x.TestChangeReviewId == id).ToListAsync(ct);
                if (items.Count == 0) return Results.BadRequest(new { error = "This test change request has no verification decisions to assign." });
                review.Assign(actor, target.UserName, now);
                var assignedItems = items.Where(x => x.State != VerificationImpactState.Resolved).ToList();
                foreach (var item in assignedItems)
                    item.AssignToEngineer(actor, target.UserName, now);
                db.SecurityAuditEvents.Add(new("TestChangeReviewAssigned", actor, review.Id.ToString(), "Success",
                    $"{review.DisplayNumber} assigned to {target.UserName}; {assignedItems.Count} open decisions assigned atomically.",
                    http.Connection.RemoteIpAddress?.ToString() ?? "unknown", now));
                await db.SaveChangesAsync(ct);
                await transaction.CommitAsync(ct);
                return Results.Ok(new { review.Id, review.AssignedEngineerId, assignedItems = assignedItems.Count });
            }
            catch (DbUpdateConcurrencyException)
            {
                return Results.Conflict(new
                {
                    error = "This test change request changed after it was opened. Refresh and try again.",
                    code = "stale_version"
                });
            }
            catch (DomainException ex) { return Results.BadRequest(new { error = ex.Message }); }
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
            var review = await db.TestChangeReviews.SingleOrDefaultAsync(x => x.Id == item.TestChangeReviewId, ct);
            if (review is null) return Results.NotFound();
            if (review.State != TestChangeReviewState.Draft)
                return Results.Conflict(new { error = "Verification decisions can be changed only while the test change request is a Draft." });
            try
            {
                var now = DateTimeOffset.UtcNow;
                item.AssignToEngineer(http.UserAccount().UserName, request.EngineerId, now);
                review.RecordControlledContentChange(now);
                await db.SaveChangesAsync(ct);
                return Results.Ok((await MapAsync([item], db, ct)).Single());
            }
            catch (DbUpdateConcurrencyException)
            {
                return Results.Conflict(new
                {
                    error = "This test change request changed after it was opened. Refresh and try again.",
                    code = "stale_version"
                });
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
            var review = await db.TestChangeReviews.SingleOrDefaultAsync(x => x.Id == item.TestChangeReviewId, ct);
            if (review is null) return Results.NotFound();
            if (review.State != TestChangeReviewState.Draft)
                return Results.Conflict(new { error = "Verification decisions can be changed only while the test change request is a Draft." });
            var decisionRefusal = await RefuseUnlessAuthoredBy(review, http, db, identity, ct);
            if (decisionRefusal is not null) return decisionRefusal;
            var artifactWord = TestChangeRequestSourceEligibility.ArtifactWord(review.ArtifactKey);
            var artifactNoun = TestChangeRequestSourceEligibility.ArtifactNoun(review.ArtifactKey);
            var requestedChangeAction = request.ArtifactChangeAction ?? request.ProcedureChangeAction;
            TestProcedureChangeAction? resolvedChangeAction = requestedChangeAction;

            ApprovedProcedureSelection? selectedProcedure = null;
            var requestedArtifactId = request.ArtifactId ?? request.ProcedureId;
            if (request.Outcome == VerificationImpactOutcome.ProcedureCoverageConfirmed && requestedArtifactId is not null)
                selectedProcedure = await service.FindApprovedProcedureAsync(item.ProjectId, requestedArtifactId.Value, ct);
            if (request.Outcome == VerificationImpactOutcome.ProcedureCoverageConfirmed && selectedProcedure is null)
                return Results.BadRequest(new
                {
                    error = $"Coverage can only be confirmed against an approved {artifactWord} in this Project."
                });

            // A procedure can only be moved onto a requirement that is actually in this Project and still
            // active. Without this check a stale identifier from a reloaded page would attach verification to
            // a requirement that had itself been retired, which is the fault this decision exists to avoid.
            if (request.Outcome == VerificationImpactOutcome.ProcedureRetargeted)
            {
                if (item.ProcedureId is not Guid strandedProcedureId || strandedProcedureId == Guid.Empty)
                    return Results.BadRequest(new { error = $"A retargeted {artifactNoun} must identify the stranded controlled artifact." });
                if (request.RetargetedRequirementRevisionId is null)
                    return Results.BadRequest(new { error = $"Moving a {artifactNoun} requires the requirement revision it now covers." });
                var reachable = await service.IsExactRetargetTargetInBuildAsync(item.ProjectId, item.ReleaseId,
                    strandedProcedureId, request.RetargetedRequirementRevisionId.Value, ct);
                if (!reachable)
                    return Results.BadRequest(new
                    {
                        error = $"A {artifactNoun} can only be moved onto an active requirement revision selected in the target build's exact requirement baseline."
                    });

                var targetAlreadyLinked = await service.HasEffectiveRetargetTargetAsync(item.ProjectId, item.ReleaseId,
                    strandedProcedureId, request.RetargetedRequirementRevisionId.Value, ct);
                if (requestedChangeAction is TestProcedureChangeAction.CreateNew or TestProcedureChangeAction.NoTestRequired)
                    return Results.BadRequest(new
                    {
                        error = $"A retargeted {artifactNoun} must use LinkExisting for an existing exact link or ModifyExisting for a controlled successor."
                    });
                if (requestedChangeAction == TestProcedureChangeAction.LinkExisting && !targetAlreadyLinked)
                    return Results.BadRequest(new
                    {
                        error = $"The retargeted {artifactNoun} has no existing exact link. Use ModifyExisting and include the target in the successor's full parent selection."
                    });
                // Preserve the compact LinkExisting decision for an already-present (possibly #709 suspect)
                // target, while making a missing target an explicit controlled-successor decision.
                resolvedChangeAction ??= targetAlreadyLinked
                    ? TestProcedureChangeAction.LinkExisting
                    : TestProcedureChangeAction.ModifyExisting;
            }

            try
            {
                var now = DateTimeOffset.UtcNow;
                var actor = http.UserAccount().UserName;
                // Answering a decision takes its package on, the same as concluding the assessment does. This
                // is the path a package that already has a number is worked through — its conclusion was
                // recorded when it was raised — so without this the commonest way of answering leaves the
                // package unheld, missing from My Work and unsubmittable.
                if (review.AssignedEngineerId is null) review.Assign(actor, actor, now);
                item.Resolve(actor, request.Outcome, request.Rationale, now,
                    request.Outcome == VerificationImpactOutcome.ProcedureRetargeted
                        ? item.ProcedureId
                        : selectedProcedure?.ProcedureId,
                    request.Outcome == VerificationImpactOutcome.ProcedureRetargeted
                        ? null
                        : selectedProcedure?.RevisionId,
                    resolvedChangeAction, request.PreReleaseEvidenceRequired,
                    request.RetargetedRequirementRevisionId);
                db.VerificationImpactDecisionHistory.Add(new VerificationImpactDecisionHistory(
                    item.Id, VerificationImpactHistoryAction.Resolved, item.Outcome,
                    item.ResolvedProcedureId, item.ResolvedProcedureRevisionId,
                    item.ResolutionRationale, actor, now));
                await service.ApplyResolvedCoverageAsync(item, now, ct);
                await service.ApplyRetargetedCoverageAsync(item, now, ct);
                review.RecordControlledContentChange(now);
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
            catch (DbUpdateConcurrencyException)
            {
                return Results.Conflict(new
                {
                    error = "This test change request changed after it was opened. Refresh and try again.",
                    code = "stale_version"
                });
            }
            catch (DomainException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        /// Raising a test change request deliberately.
        ///
        /// One is raised automatically whenever a change request is approved, so nothing ever goes unnoticed.
        /// That is not the only way work arrives: a verification engineer may decide a set of changes is best
        /// tested as one package of their own making, and until now the only way to express that was to let
        /// the automatic packages appear and then fold them together.
        ///
        /// It takes the change requests it answers for up front, because a package that covers nothing has
        /// nothing to decide and would sit in the queue looking like work.
        app.MapPost("/api/releases/{releaseId:guid}/test-change-requests", async (Guid releaseId,
            CreateTestChangeRequestRequest request, HttpContext http, AeroLinkDbContext db,
            IdentityService identity, IProjectLadderPolicyResolver policyResolver, ProblemReportLinkService problemReports, CancellationToken ct) =>
        {
            var release = await db.Releases.AsNoTracking().Where(x => x.Id == releaseId)
                .Select(x => new { x.ProjectId, x.IsReleased }).SingleOrDefaultAsync(ct);
            if (release is null) return Results.NotFound();
            var ladderPolicy = await policyResolver.ResolveAsync(release.ProjectId, ct);
            if (release.IsReleased) return Results.Conflict(new { error = "A released build takes no new test change requests." });
            if (!await http.HasProjectRoleAsync(db, identity, release.ProjectId, ct, ProgramRole.TestEngineer, ProgramRole.TestLead))
                return Results.Forbid();
            VerificationArtifactKey artifactKey;
            try
            {
                artifactKey = request.ArtifactKey ?? new VerificationArtifactKey(
                    VerificationArtifactProfile.ToNeutral(request.Discipline),
                    request.ArtifactKind ?? (request.Discipline == TestChangeReviewDiscipline.System
                        ? VerificationArtifactKind.Procedure : VerificationArtifactKind.Case));
                if (artifactKey.Discipline != VerificationArtifactProfile.ToNeutral(request.Discipline))
                    throw new DomainException("The artifact key discipline must match the test-change discipline.");
                _ = ladderPolicy.VerificationArtifact(artifactKey);
            }
            catch (DomainException) { return Results.BadRequest(new { error = "The test-change discipline is not supported." }); }
            // Test work is not only ever caused by a requirement change: an anomaly found in the field is a
            // legitimate reason to write, correct or withdraw a procedure, and a build may carry no approved
            // change at this package's own level to hang it on. What a package cannot be is raised from
            // nothing — it must say what concluded the work was required.
            // Absent and empty mean the same thing here — the property is optional on the request.
            var changeRequestIds = request.ChangeRequestIds ?? [];
            var namedProblemReports = request.ProblemReportIds ?? [];
            var caseChangeIds = request.CaseChangeIds ?? [];
            var caseAssessmentIds = request.CaseAssessmentIds ?? [];
            Guid caseOriginId = Guid.Empty;
            string caseOriginDisplay = "";
            var caseOriginKind = TestChangeReviewOriginKind.CaseChange;
            if (artifactKey.Kind == VerificationArtifactKind.Procedure
                && artifactKey.Discipline != VerificationDiscipline.System)
            {
                if (changeRequestIds.Length != 0 || namedProblemReports.Length != 0
                    || caseChangeIds.Length + caseAssessmentIds.Length != 1)
                    return Results.BadRequest(new
                    {
                        error = "A software Procedure package must name exactly one exact Case change or Case assessment origin.",
                        code = "procedure_origin_required"
                    });
                if (caseChangeIds.Length == 1)
                {
                    var source = await (from change in db.Set<TestProcedureChange>().AsNoTracking()
                                        join review in db.TestChangeReviews.AsNoTracking()
                                            on change.TestChangeReviewId equals review.Id
                                        where change.Id == caseChangeIds[0]
                                            && review.ProjectId == release.ProjectId && review.ReleaseId == releaseId
                                            && review.ArtifactKind == VerificationArtifactKind.Case
                                            && review.State == TestChangeReviewState.Approved
                                            && review.Outcome == TestChangeReviewOutcome.ChangeRequired
                                            && change.BaseNumber != ""
                                        select new { change.Id,
                                            DisplayNumber = change.BaseNumber + "." + (change.Revision < 10 ? "0" : "") + change.Revision,
                                            change.Title, review.Discipline }).SingleOrDefaultAsync(ct);
                    if (source is null || source.Discipline == TestChangeReviewDiscipline.System
                        || VerificationArtifactProfile.ToNeutral(source.Discipline) != artifactKey.Discipline)
                        return Results.BadRequest(new { error = "The Case change origin must be an exact software Case change in this Project and build." });
                    caseOriginId = source.Id;
                    caseOriginDisplay = source.DisplayNumber;
                    caseOriginKind = TestChangeReviewOriginKind.CaseChange;
                }
                else
                {
                    var source = await (from item in db.VerificationImpactItems.AsNoTracking()
                                        join review in db.TestChangeReviews.AsNoTracking()
                                            on item.TestChangeReviewId equals review.Id
                                        where item.Id == caseAssessmentIds[0]
                                            && item.ProjectId == release.ProjectId && item.ReleaseId == releaseId
                                            && review.ArtifactKind == VerificationArtifactKind.Case
                                            && review.State != TestChangeReviewState.Superseded
                                            && item.State == VerificationImpactState.Resolved
                                            && item.Outcome == VerificationImpactOutcome.NewProcedureRequired
                                            && item.ProcedureChangeAction == TestProcedureChangeAction.CreateNew
                                            && item.RequirementRevisionId != null
                                        select new { item.Id, item.SubjectDisplayNumber, item.ResolutionRationale, item.RequirementRevisionId, review.Discipline }).SingleOrDefaultAsync(ct);
                    if (source is null || source.Discipline == TestChangeReviewDiscipline.System
                        || VerificationArtifactProfile.ToNeutral(source.Discipline) != artifactKey.Discipline)
                        return Results.BadRequest(new { error = "The Case assessment origin must be an exact software Case assessment in this Project and build." });
                    var effectiveBaselineId = await TestChangeReviewRequirementScope.EffectiveRequirementBaselineIdAsync(
                        db, release.ProjectId, releaseId, ct);
                    if (effectiveBaselineId is null || source.RequirementRevisionId is null
                        || !await db.BaselineRequirements.AsNoTracking().AnyAsync(x => x.BaselineId == effectiveBaselineId
                            && x.RevisionId == source.RequirementRevisionId, ct))
                        return Results.BadRequest(new { error = "The Case assessment origin is not bound to this build's effective baseline." });
                    caseOriginId = source.Id;
                    caseOriginDisplay = source.SubjectDisplayNumber;
                    caseOriginKind = TestChangeReviewOriginKind.CaseAssessment;
                }
            }
            if (artifactKey.Kind == VerificationArtifactKind.Case
                && changeRequestIds.Length == 0 && namedProblemReports.Length == 0)
                return Results.BadRequest(new
                {
                    error = "Name what this package answers for: an approved change request at its own level, or a Problem Report.",
                    code = "test_change_request_needs_a_driver"
                });
            if (string.IsNullOrWhiteSpace(request.Title))
                return Results.BadRequest(new { error = "A manually raised test change request needs a title that says what it is for." });

            var changes = await db.SystemChangeRequests.AsNoTracking()
                .Where(x => changeRequestIds.Contains(x.Id) && x.ProjectId == release.ProjectId
                    && x.TargetReleaseId == releaseId)
                .Select(x => new { x.Id, x.DisplayNumber, x.State, x.Type, x.SoftwareLevel }).ToListAsync(ct);
            if (changes.Count != changeRequestIds.Length)
                return Results.BadRequest(new
                {
                    error = "A test change request can only answer for approved change requests allocated to this build.",
                    code = "change_request_not_selectable"
                });
            var ineligible = changes.FirstOrDefault(x => !TestChangeRequestSourceEligibility.Allows(x.State));
            if (ineligible is not null)
                return TestChangeRequestSourceEligibility.Refusal(ineligible.DisplayNumber, ineligible.State);
            // Enforced here as well as in the picker. A filtered browser list is a convenience; the refusal is
            // the rule, and a request that never opened the picker must meet it too.
            var wrongLevel = changes.FirstOrDefault(x =>
                !TestChangeRequestSourceEligibility.MatchesDiscipline(request.Discipline, x.Type, x.SoftwareLevel, ladderPolicy));
            if (wrongLevel is not null)
                return TestChangeRequestSourceEligibility.LevelRefusal(wrongLevel.DisplayNumber, request.Discipline);
            // The first change the caller names is the package's base; the rest are folded in. The database
            // row order is not the caller's order, so it is restored explicitly rather than trusted.
            changes = changeRequestIds.Select(id => changes.Single(x => x.Id == id)).ToList();
            var problemReportError = artifactKey.Kind == VerificationArtifactKind.Procedure
                && artifactKey.Discipline != VerificationDiscipline.System
                ? null
                : await problemReports.ValidateSelectionAsync(release.ProjectId, releaseId, request.ProblemReportIds, ct);
            if (problemReportError is not null) return Results.BadRequest(new { error = problemReportError });

            // Already covered, by the package it was raised from or by one it was folded into. The check names
            // the holder, so an engineer is told where the work went rather than told to try again.
            //
            // Originating cover is per discipline — one change request legitimately has System, HLR and LLR
            // packages — while a folded-in claim is exclusive outright.
            foreach (var change in changes)
            {
                var origin = await db.TestChangeReviews.AsNoTracking()
                    .Where(x => x.ChangeRequestId == change.Id && x.Discipline == request.Discipline
                        && x.ArtifactKind == artifactKey.Kind
                        && x.State != TestChangeReviewState.Superseded
                        && (x.State != TestChangeReviewState.Draft || x.Outcome != TestChangeReviewOutcome.Pending))
                    .Select(x => x.DisplayNumber).FirstOrDefaultAsync(ct);
                var claimed = await db.TestChangeRequestClaims.AsNoTracking()
                    .Where(x => x.ChangeRequestId == change.Id)
                    .Join(db.TestChangeReviews.AsNoTracking(), claim => claim.TestChangeReviewId, review => review.Id, (_, review) => review.DisplayNumber)
                    .FirstOrDefaultAsync(ct);
                // Two different situations, and one sentence could not tell them apart once an unassessed
                // package took its name from the change it was raised from: being covered by somebody else's
                // package is not the same as already having an assessment of your own, and the second read
                // as "X is already covered by X".
                if (!string.IsNullOrEmpty(origin))
                    return Results.Conflict(new { error = $"{change.DisplayNumber} already has a {request.Discipline} test assessment." });
                if (!string.IsNullOrEmpty(claimed))
                    return Results.Conflict(new { error = $"{change.DisplayNumber} is already covered by {claimed}." });
            }
            // Raising a package by hand is itself the conclusion that test work is required (DEC-095), so an
            // unassessed automatic review of one of the selected changes is not a rival: the first change's
            // review becomes this package, and the others are superseded rather than duplicated. History keeps
            // them; nothing is deleted.
            var pendingAutomatic = await db.TestChangeReviews
                .Where(x => x.ChangeRequestId != null && changeRequestIds.Contains(x.ChangeRequestId.Value)
                        && x.Discipline == request.Discipline && x.ArtifactKind == artifactKey.Kind
                    && x.State == TestChangeReviewState.Draft && x.Outcome == TestChangeReviewOutcome.Pending)
                .ToListAsync(ct);

            try
            {
                var now = DateTimeOffset.UtcNow;
                var actor = http.UserAccount().UserName;
                TestChangeReview review;
                if (changes.Count == 0)
                {
                    // Raised from a Problem Report. The report takes the originating slot a change request
                    // would have held, so the package still has exactly one thing it was raised from — which
                    // is what its number, its covered-sources record and its case snapshot depend on.
                    if (artifactKey.Kind == VerificationArtifactKind.Procedure
                        && artifactKey.Discipline != VerificationDiscipline.System)
                    {
                        review = caseOriginKind == TestChangeReviewOriginKind.CaseAssessment
                            ? TestChangeReview.FromCaseAssessment(release.ProjectId, releaseId, caseOriginId,
                                artifactKey, caseOriginDisplay, now, authorId: actor)
                            : TestChangeReview.FromCaseChange(release.ProjectId, releaseId, caseOriginId,
                                artifactKey, caseOriginDisplay, now, authorId: actor);
                    }
                    else
                    {
                        var originatingReport = await db.ProblemReports.AsNoTracking()
                            .Where(x => x.Id == namedProblemReports[0])
                            .Select(x => new { x.Id, x.ReportNumber, x.Revision }).SingleAsync(ct);
                        review = TestChangeReview.FromProblemReport(release.ProjectId, releaseId, originatingReport.Id,
                            artifactKey, $"{originatingReport.ReportNumber}.{originatingReport.Revision:D2}", now, authorId: actor);
                    }
                    db.TestChangeReviews.Add(review);
                }
                else
                {
                    var first = changes[0];
                    // Raising one by hand is itself the conclusion that test work is required, so it is numbered
                    // immediately rather than waiting to be assessed by the person who just decided it. When the
                    // change already carries an unassessed automatic review, that review is the package — one
                    // row, one ChangeRequestId, one Revision, one answer.
                    var existing = pendingAutomatic.SingleOrDefault(x => x.ChangeRequestId == first.Id);
                    if (existing is not null)
                    {
                        review = existing;
                    }
                    else
                    {
                        review = new TestChangeReview(release.ProjectId, releaseId, first.Id, artifactKey,
                            first.DisplayNumber, now, authorId: actor);
                        db.TestChangeReviews.Add(review);
                    }
                }
                review.RecordTestChangeRequired(actor, now);
                review.AssignControlledNumber(
                    await IdentifierAllocator.NextTestChangeRequestAsync(db, artifactKey, ct, ladderPolicy), now,
                    ladderPolicy);
                foreach (var extra in changes.Skip(1))
                    review.IncludeChangeRequest(actor, extra.Id, extra.DisplayNumber, now);
                review.WriteCase(actor, request.Title, request.Problem, request.Analysis, request.Solution, now,
                    request.ProblemRich, request.AnalysisRich, request.SolutionRich);
                // Authored with the case and saved with it, the way a change request is created together with
                // the requirement changes it proposes. The domain refuses a decision that is not well formed,
                // so a malformed one fails the whole create rather than leaving a half-written package behind.
                foreach (var change in request.ArtifactChanges ?? request.ProcedureChanges ?? [])
                {
                    var suppliedParentIds = (change.ParentRevisionIds ?? [])
                        .Concat(change.DrivingRequirementRevisionIds ?? [])
                        .Distinct().ToArray();
                    if (change.ParentKind == VerificationProcedureParentKind.Derived
                        && suppliedParentIds.Length != 0)
                        throw new DomainException(
                            $"A derived {TestChangeRequestSourceEligibility.ArtifactNoun(request.Discipline)} cannot carry exact parent revisions.");
                    var parentRevisionIds = change.ParentKind == VerificationProcedureParentKind.Derived
                        ? Array.Empty<Guid>()
                        : change.ParentRevisionIds ?? change.DrivingRequirementRevisionIds ?? [];
                    review.AddProcedureChange(actor, new TestProcedureChangeDraft(change.BaseNumber, change.Revision,
                        change.Level, change.Kind, change.Title, change.Objective, change.Preconditions,
                        change.Steps, change.ExpectedResult, change.Rationale,
                        JsonSerializer.Serialize(change.DrivingRequirementRevisionIds ?? []),
                        ParentKind: change.ParentKind,
                        ParentRevisionIdsJson: JsonSerializer.Serialize(parentRevisionIds),
                        DerivedRationale: change.DerivedRationale ?? "",
                        EnvironmentSetup: change.EnvironmentSetup ?? "",
                        TestData: change.TestData ?? "",
                        OrderedSteps: change.OrderedSteps ?? "",
                        ExpectedObservations: change.ExpectedObservations ?? "",
                        Cleanup: change.Cleanup ?? "",
                        ToolingAutomation: change.ToolingAutomation ?? ""), now, policy: ladderPolicy);
                }
                // DEC-102: raising the package is itself taking it on. The engineer who built it holds it,
                // so it appears in My Work and can be worked without a meaningless "Take it on" step.
                review.Assign(actor, actor, now);
                foreach (var automatic in pendingAutomatic.Where(x => x.Id != review.Id))
                    automatic.Supersede(review.Id,
                        $"{review.DisplayNumber} was raised manually and concludes that test work is required; it supersedes this unassessed automatic review.",
                        now);
                // The folded sources' verification work must stay actionable from the surviving package.
                // Moving the items — identity, attribution, decision and history preserved — is what keeps
                // "this change is covered by this TCR" true instead of stranding work behind a superseded
                // assessment that the queue no longer shows.
                var supersededIds = pendingAutomatic.Where(x => x.Id != review.Id).Select(x => x.Id).ToList();
                if (supersededIds.Count != 0)
                {
                    var moved = await db.VerificationImpactItems
                        .Where(x => supersededIds.Contains(x.TestChangeReviewId)).ToListAsync(ct);
                    foreach (var item in moved) item.MoveToReview(review.Id, now);
                }
                await problemReports.LinkTestChangeRequestAsync(review.Id, request.ProblemReportIds, actor, now, ct);
                await db.SaveChangesAsync(ct);
                return Results.Created($"/api/test-change-reviews/{review.Id}", new
                {
                    review.Id,
                    review.DisplayNumber,
                    discipline = review.Discipline.ToString(),
                    state = review.State.ToString(),
                    covered = review.CoveredChangeRequestIds,
                });
            }
            catch (DomainException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        /// Editing the engineering case of an open test change request.
        ///
        /// A deliberately raised package is authored with its case up front, and stays correctable while it
        /// is being worked — the same window a change request's own draft edit has. Once a reviewer is
        /// holding it, the case is fixed, because the approval has to be provably of the content the
        /// reviewer read.
        app.MapPost("/api/test-change-reviews/{id:guid}/case", async (Guid id,
            WriteTestChangeRequestCaseRequest request, HttpContext http, AeroLinkDbContext db,
            IdentityService identity, CancellationToken ct) =>
        {
            var review = await db.TestChangeReviews.SingleOrDefaultAsync(x => x.Id == id, ct);
            if (review is null) return Results.NotFound();
            var refusal = await RefuseUnlessAuthoredBy(review, http, db, identity, ct);
            if (refusal is not null) return refusal;
            if (request.ExpectedVersion is not null && review.Version != request.ExpectedVersion)
                return Results.Conflict(new
                {
                    error = "This test change request changed after it was opened. Refresh before editing its case.",
                    code = "stale_version"
                });
            try
            {
                review.WriteCase(http.UserAccount().UserName, request.Title, request.Problem, request.Analysis,
                    request.Solution, DateTimeOffset.UtcNow, request.ProblemRich, request.AnalysisRich,
                    request.SolutionRich);
                await db.SaveChangesAsync(ct);
                return Results.Ok(new
                {
                    review.Id,
                    review.Title,
                    review.Problem,
                    review.Analysis,
                    review.Solution,
                    review.ProblemRich,
                    review.AnalysisRich,
                    review.SolutionRich
                });
            }
            catch (DbUpdateConcurrencyException)
            {
                return Results.Conflict(new
                {
                    error = "This test change request changed after it was opened. Refresh and try again.",
                    code = "stale_version"
                });
            }
            catch (DomainException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        /// A truthful read model for the manual-raise source picker.
        ///
        /// The mutation endpoint remains authoritative; this projection exists so the client can explain
        /// before submission why an approved change cannot be added — already assessed, already claimed, or
        /// not eligible for this build. No cross-project information is exposed.
        app.MapGet("/api/releases/{releaseId:guid}/test-change-request-sources", async (Guid releaseId,
            TestChangeReviewDiscipline discipline, VerificationArtifactKind? artifactKind, HttpContext http, AeroLinkDbContext db,
            IProjectLadderPolicyResolver policyResolver, CancellationToken ct) =>
        {
            var release = await db.Releases.AsNoTracking().Where(x => x.Id == releaseId)
                .Select(x => new { x.ProjectId, x.IsReleased }).SingleOrDefaultAsync(ct);
            if (release is null) return Results.NotFound();
            var ladderPolicy = await policyResolver.ResolveAsync(release.ProjectId, ct);
            if (!await http.HasProjectAccessAsync(db, release.ProjectId, ct)) return Results.Forbid();
            try { _ = ladderPolicy.RequirementLevelFor(discipline); }
            catch (DomainException) { return Results.BadRequest(new { error = "The test-change discipline is not supported." }); }

            // A software Procedure package is raised from exactly one governed Case origin. Keep this
            // projection on the same release/project/discipline boundary as the mutation endpoint so the
            // picker never offers a historical or cross-level origin that the server will reject.
            if (artifactKind == VerificationArtifactKind.Procedure && discipline != TestChangeReviewDiscipline.System)
            {
                try
                {
                    _ = ladderPolicy.VerificationArtifact(new VerificationArtifactKey(
                        VerificationArtifactProfile.ToNeutral(discipline), VerificationArtifactKind.Procedure));
                }
                catch (DomainException)
                {
                    return Results.BadRequest(new
                    {
                        error = "The selected Procedure artifact is not enabled by the active project profile.",
                        code = "verification_artifact_disabled"
                    });
                }
                var consumedOrigins = db.TestChangeReviews.AsNoTracking()
                    .Where(x => x.ProjectId == release.ProjectId && x.ReleaseId == releaseId
                        && x.Discipline == discipline && x.ArtifactKind == VerificationArtifactKind.Procedure
                        && x.State != TestChangeReviewState.Superseded)
                    .Select(x => x.OriginReferenceId);
                var effectiveBaselineId = await TestChangeReviewRequirementScope.EffectiveRequirementBaselineIdAsync(
                    db, release.ProjectId, releaseId, ct);
                var caseChanges = await (from change in db.Set<TestProcedureChange>().AsNoTracking()
                                         join review in db.TestChangeReviews.AsNoTracking()
                                             on change.TestChangeReviewId equals review.Id
                                         where review.ProjectId == release.ProjectId && review.ReleaseId == releaseId
                                             && review.Discipline == discipline
                                             && review.ArtifactKind == VerificationArtifactKind.Case
                                             && review.State == TestChangeReviewState.Approved
                                             && review.Outcome == TestChangeReviewOutcome.ChangeRequired
                                             && change.BaseNumber != ""
                                             && !consumedOrigins.Contains(change.Id)
                                         select new
                                         {
                                             sourceKind = TestChangeReviewOriginKind.CaseChange.ToString(),
                                             sourceId = change.Id,
                                             displayNumber = change.BaseNumber + "." + (change.Revision < 10 ? "0" : "") + change.Revision,
                                             title = change.Title,
                                             state = review.State.ToString(),
                                             selectable = true,
                                             reason = (string?)null
                                         }).ToListAsync(ct);
                var caseAssessments = await (from item in db.VerificationImpactItems.AsNoTracking()
                                             join review in db.TestChangeReviews.AsNoTracking()
                                                 on item.TestChangeReviewId equals review.Id
                                             where item.ProjectId == release.ProjectId && item.ReleaseId == releaseId
                                                 && review.Discipline == discipline
                                                 && review.ArtifactKind == VerificationArtifactKind.Case
                                                 && review.State != TestChangeReviewState.Superseded
                                                 && item.State == VerificationImpactState.Resolved
                                                 && item.Outcome == VerificationImpactOutcome.NewProcedureRequired
                                                 && item.ProcedureChangeAction == TestProcedureChangeAction.CreateNew
                                                 && item.RequirementRevisionId != null
                                                 && effectiveBaselineId != null
                                                 && db.BaselineRequirements.AsNoTracking().Any(baselineRequirement =>
                                                     baselineRequirement.BaselineId == effectiveBaselineId
                                                     && baselineRequirement.RevisionId == item.RequirementRevisionId)
                                                 && !consumedOrigins.Contains(item.Id)
                                             select new
                                             {
                                                 sourceKind = TestChangeReviewOriginKind.CaseAssessment.ToString(),
                                                 sourceId = item.Id,
                                                 displayNumber = item.SubjectDisplayNumber,
                                                 title = review.Title,
                                                 state = item.State.ToString(),
                                                 selectable = true,
                                                 reason = (string?)null
                                             }).ToListAsync(ct);
                return Results.Ok(caseChanges.Concat(caseAssessments)
                    .OrderBy(x => x.displayNumber, StringComparer.OrdinalIgnoreCase));
            }

            // Level-filtered before anything else: offering a change the package could never answer for is
            // not a selectable option that happens to be wrong, it is a wrong answer presented as a choice.
            var changes = await TestChangeRequestSourceEligibility.AtLevelOf(
                    TestChangeRequestSourceEligibility.Apply(db.SystemChangeRequests.AsNoTracking()), discipline, ladderPolicy)
                .Where(x => x.ProjectId == release.ProjectId && x.TargetReleaseId == releaseId)
                .Select(x => new { x.Id, x.DisplayNumber, x.Title, x.State }).ToListAsync(ct);
            var ids = changes.Select(x => x.Id).ToList();

            var concluded = await db.TestChangeReviews.AsNoTracking()
                .Where(x => x.ChangeRequestId != null && ids.Contains(x.ChangeRequestId.Value)
                    && x.Discipline == discipline
                    && x.State != TestChangeReviewState.Superseded
                    && (x.State != TestChangeReviewState.Draft || x.Outcome != TestChangeReviewOutcome.Pending))
                .GroupBy(x => x.ChangeRequestId!.Value)
                .ToDictionaryAsync(group => group.Key, group => group.First().DisplayNumber, ct);
            var claims = await db.TestChangeRequestClaims.AsNoTracking()
                .Where(x => ids.Contains(x.ChangeRequestId))
                .Join(db.TestChangeReviews.AsNoTracking(), claim => claim.TestChangeReviewId,
                    review => review.Id, (claim, review) => new { claim.ChangeRequestId, review.DisplayNumber })
                .ToListAsync(ct);
            var claimedBy = claims.GroupBy(x => x.ChangeRequestId)
                .ToDictionary(group => group.Key, group => group.First().DisplayNumber);

            return Results.Ok(changes.OrderBy(x => x.DisplayNumber).Select(change =>
            {
                string? reason = concluded.TryGetValue(change.Id, out var origin)
                    ? $"Already has a {discipline} test assessment."
                    : claimedBy.TryGetValue(change.Id, out var holder)
                        ? $"Already covered by {holder}."
                        : null;
                return new
                {
                    changeRequestId = change.Id,
                    displayNumber = change.DisplayNumber,
                    title = change.Title,
                    state = change.State.ToString(),
                    selectable = reason is null,
                    reason
                };
            }));
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
            if (review.ArtifactKind == VerificationArtifactKind.Procedure
                && review.ArtifactKey.Discipline != VerificationDiscipline.System)
                return Results.BadRequest(new { error = "A software Procedure package can only retain its exact Case origin; it cannot fold in a requirement change request." });
            if (await db.Releases.AnyAsync(x => x.Id == review.ReleaseId && x.IsReleased, ct))
                return Results.Conflict(new { error = "Released software-build test change requests are read-only." });
            var foldRefusal = await RefuseUnlessAuthoredBy(review, http, db, identity, ct);
            if (foldRefusal is not null) return foldRefusal;

            var change = await db.SystemChangeRequests.AsNoTracking()
                .Where(x => x.Id == request.ChangeRequestId && x.ProjectId == review.ProjectId)
                .Select(x => new { x.Id, x.DisplayNumber, x.TargetReleaseId, x.State }).SingleOrDefaultAsync(ct);
            if (change is null) return Results.NotFound(new { error = "That change request is not in this Project." });
            // A package governs one build's test work. Folding in a change allocated to a different build
            // would put its procedures behind the wrong release gate.
            if (change.TargetReleaseId != review.ReleaseId)
                return Results.BadRequest(new { error = $"{change.DisplayNumber} is allocated to a different build." });
            if (!TestChangeRequestSourceEligibility.Allows(change.State))
                return TestChangeRequestSourceEligibility.Refusal(change.DisplayNumber, change.State);

            var claimedBy = await db.TestChangeRequestClaims.AsNoTracking()
                .Where(x => x.ChangeRequestId == request.ChangeRequestId)
                .Select(x => x.TestChangeReviewId).FirstOrDefaultAsync(ct);
            if (claimedBy != Guid.Empty && claimedBy != id)
            {
                var holder = await db.TestChangeReviews.AsNoTracking().Where(x => x.Id == claimedBy)
                    .Select(x => x.DisplayNumber).SingleOrDefaultAsync(ct);
                return Results.Conflict(new { error = $"{change.DisplayNumber} is already covered by {holder}." });
            }
            // A change whose assessment has already been concluded is a real package of decisions, not a
            // pending automatic review. Folding it in would strand those decisions; the engineer should work
            // them where they were made. Checked after claims so a holder is named when one exists.
            var concluded = await db.TestChangeReviews.AsNoTracking()
                .Where(x => x.ChangeRequestId == request.ChangeRequestId && x.Discipline == review.Discipline
                    && x.ArtifactKind == review.ArtifactKind
                    && x.State != TestChangeReviewState.Superseded
                    && (x.State != TestChangeReviewState.Draft || x.Outcome != TestChangeReviewOutcome.Pending))
                .Select(x => x.DisplayNumber).FirstOrDefaultAsync(ct);
            if (!string.IsNullOrEmpty(concluded))
                return Results.Conflict(new { error = $"{change.DisplayNumber} already has a {review.Discipline} test assessment." });
            if (request.ExpectedVersion is not null && review.Version != request.ExpectedVersion)
                return Results.Conflict(new
                {
                    error = "This test change request changed after it was opened. Refresh before changing its source set.",
                    code = "stale_version"
                });

            try
            {
                var now = DateTimeOffset.UtcNow;
                review.IncludeChangeRequest(http.UserAccount().UserName, change.Id, change.DisplayNumber, now);
                // Taking a change's test work into this package takes its pending automatic assessment with
                // it: the assessment is superseded as history and its items move here so the package's
                // workspace can see and settle them.
                var automatic = await db.TestChangeReviews
                    .Where(x => x.ChangeRequestId == request.ChangeRequestId && x.Discipline == review.Discipline
                        && x.ArtifactKind == review.ArtifactKind
                        && x.State == TestChangeReviewState.Draft && x.Outcome == TestChangeReviewOutcome.Pending)
                    .ToListAsync(ct);
                foreach (var pending in automatic)
                    pending.Supersede(review.Id,
                        $"{review.DisplayNumber} took over this change's test work when it was folded in; this unassessed automatic review is superseded as history.",
                        now);
                var automaticIds = automatic.Select(x => x.Id).ToList();
                if (automaticIds.Count != 0)
                {
                    var items = await db.VerificationImpactItems
                        .Where(x => automaticIds.Contains(x.TestChangeReviewId)).ToListAsync(ct);
                    foreach (var item in items) item.MoveToReview(review.Id, now);
                }
                await db.SaveChangesAsync(ct);
                return Results.Ok(new { review.Id, review.DisplayNumber, covered = review.CoveredChangeRequestIds });
            }
            catch (DbUpdateConcurrencyException)
            {
                return Results.Conflict(new
                {
                    error = "This test change request changed after it was opened. Refresh and try again.",
                    code = "stale_version"
                });
            }
            catch (DomainException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        app.MapDelete("/api/test-change-reviews/{id:guid}/change-requests/{changeRequestId:guid}", async (Guid id,
            Guid changeRequestId, long? expectedVersion, HttpContext http, AeroLinkDbContext db,
            IdentityService identity, CancellationToken ct) =>
        {
            var review = await db.TestChangeReviews.Include(x => x.AdditionalSources).SingleOrDefaultAsync(x => x.Id == id, ct);
            if (review is null) return Results.NotFound();
            if (review.ArtifactKind == VerificationArtifactKind.Procedure
                && review.ArtifactKey.Discipline != VerificationDiscipline.System)
                return Results.BadRequest(new { error = "A software Procedure package has an immutable Case origin and cannot change its requirement source set." });
            if (await db.Releases.AnyAsync(x => x.Id == review.ReleaseId && x.IsReleased, ct))
                return Results.Conflict(new { error = "Released software-build test change requests are read-only." });
            var unfoldRefusal = await RefuseUnlessAuthoredBy(review, http, db, identity, ct);
            if (unfoldRefusal is not null) return unfoldRefusal;
            if (expectedVersion is not null && review.Version != expectedVersion)
                return Results.Conflict(new
                {
                    error = "This test change request changed after it was opened. Refresh before changing its source set.",
                    code = "stale_version"
                });
            try
            {
                var now = DateTimeOffset.UtcNow;
                review.ExcludeChangeRequest(changeRequestId, now);
                // The change is no longer claimed by this package, so the work this package moved in from its
                // automatic assessment must go back to an actionable assessment of its own — a fresh Open
                // record at the next review revision. The superseded assessment stays as history.
                var change = await db.SystemChangeRequests.AsNoTracking()
                    .Where(x => x.Id == changeRequestId && x.ProjectId == review.ProjectId)
                    .Select(x => new { x.Id, x.DisplayNumber }).SingleOrDefaultAsync(ct);
                if (change is not null)
                {
                    var stranded = await db.VerificationImpactItems
                        .Where(x => x.ChangeRequestId == changeRequestId && x.TestChangeReviewId == id)
                        .ToListAsync(ct);
                    // A fresh current Draft/Pending assessment is restored unconditionally — even when the
                    // source has no impact items — so the change is never left without an actionable
                    // assessment and can be selected or folded again. The superseded assessment remains history.
                    var nextRevision = await db.TestChangeReviews
                        .Where(x => x.ChangeRequestId == changeRequestId && x.Discipline == review.Discipline
                            && x.ArtifactKind == review.ArtifactKind)
                        .Select(x => (int?)x.Revision).MaxAsync(ct) ?? -1;
                    var fresh = new TestChangeReview(review.ProjectId, review.ReleaseId, change.Id,
                        review.ArtifactKey, change.DisplayNumber, now, revision: nextRevision + 1);
                    db.TestChangeReviews.Add(fresh);
                    foreach (var item in stranded) item.MoveToReview(fresh.Id, now);
                }
                await db.SaveChangesAsync(ct);
                return Results.Ok(new { review.Id, review.DisplayNumber, covered = review.CoveredChangeRequestIds });
            }
            catch (DbUpdateConcurrencyException)
            {
                return Results.Conflict(new
                {
                    error = "This test change request changed after it was opened. Refresh and try again.",
                    code = "stale_version"
                });
            }
            catch (DomainException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        app.MapPost("/api/test-change-reviews/{id:guid}/submit", async (Guid id, SubmitTestChangeReviewRequest request,
            HttpContext http, AeroLinkDbContext db, IdentityService identity, IProjectLadderPolicyResolver policyResolver, CancellationToken ct) =>
        {
            var review = await db.TestChangeReviews.Include(x => x.ProcedureChanges).Include(x => x.ReviewCycles)
                .SingleOrDefaultAsync(x => x.Id == id, ct);
            if (review is null) return Results.NotFound();
            var ladderPolicy = await policyResolver.ResolveAsync(review.ProjectId, ct);
            if (await db.Releases.AnyAsync(x => x.Id == review.ReleaseId && x.IsReleased, ct))
                return Results.Conflict(new { error = "Released software-build test change reviews are read-only." });
            var submitRefusal = await RefuseUnlessAuthoredBy(review, http, db, identity, ct);
            if (submitRefusal is not null) return submitRefusal;
            if (request.ExpectedVersion is not null && review.Version != request.ExpectedVersion)
                return Results.Conflict(new
                {
                    error = "This test change request changed after it was opened. Refresh before submitting it for review.",
                    code = "stale_version"
                });
            try
            {
                // Historical blank packages remain readable; compatibility is not a submission bypass.
                // Every new review cycle requires the complete case its reviewer is being asked to approve.
                var missingCaseFields = review.MissingCaseFields();
                if (review.Outcome == TestChangeReviewOutcome.ChangeRequired && missingCaseFields.Count > 0)
                    return Results.BadRequest(new
                    {
                        error = $"Complete the test change request case before sending it for review. Missing: {string.Join(", ", missingCaseFields)}.",
                        code = "test_change_request_case_incomplete",
                        fields = missingCaseFields
                    });
                if (review.Outcome == TestChangeReviewOutcome.ChangeRequired && review.ProcedureChanges.Count == 0)
                    return Results.BadRequest(new
                    {
                        error = $"{review.DisplayNumber} concluded that {TestChangeRequestSourceEligibility.ArtifactNoun(review.ArtifactKey)} work is required but names none. " +
                            $"Add the {TestChangeRequestSourceEligibility.ArtifactNoun(review.ArtifactKey)} decisions it carries before sending it for review."
                    });
                await TestChangeReviewRequirementScope.ValidateProcedureChangesForSubmissionAsync(
                    db, review, ladderPolicy, ct);
                await TestChangeReviewRequirementScope.ValidateRetargetPlansForSubmissionAsync(db, review, ct, ladderPolicy);
                var allResolved = await db.VerificationImpactItems
                    .Where(x => x.TestChangeReviewId == id)
                    .AllAsync(x => x.State == VerificationImpactState.Resolved, ct);
                // The project's recorded procedure for this discipline decides the stages. Where none is
                // recorded the chosen approver stands alone, exactly as before — a rule nobody has written
                // down must not become a rule that blocks work.
                var workflow = await WorkflowEndpoints.ActiveSpecificationAsync(db, review.ProjectId, review.ArtifactKey, ct, ladderPolicy);
                List<ApproverSelection> selections;
                if (workflow is null)
                {
                    if (string.IsNullOrWhiteSpace(request.ApproverId))
                        return Results.BadRequest(new { error = "Select an independent test change request approver." });
                    var approver = await db.UserAccounts.AsNoTracking().SingleOrDefaultAsync(x =>
                        x.UserName == request.ApproverId.Trim().ToLowerInvariant() && x.State == AccountState.Active, ct);
                    if (approver is null)
                        return Results.BadRequest(new { error = "Select an active AeroLink test change request approver." });
                    var programId = await db.Projects.AsNoTracking().Where(x => x.Id == review.ProjectId)
                        .Select(x => x.ProgramId).SingleAsync(ct);
                    if (!await identity.HasRoleAsync(approver.Id, programId, ProgramRole.Approver, DateTimeOffset.UtcNow, ct))
                        return Results.BadRequest(new { error = $"{approver.DisplayName} does not hold Approver authority for this Program." });
                    selections = [new ApproverSelection(approver.UserName, approver.DisplayName, ProgramRole.Approver)];
                }
                else
                {
                    var requested = request.Approvers ?? [];
                    if (requested.Count < workflow.Stages.Count)
                        return Results.BadRequest(new
                        {
                            error = $"{workflow.Name} v{workflow.Version} requires {workflow.Stages.Count} approver{(workflow.Stages.Count == 1 ? "" : "s")} minimum (at least {workflow.Stages.Count}), one for each stage: " +
                                string.Join(", ", workflow.Stages.Select(x => x.Name)) + "."
                        });
                    var ids = requested.Select(x => x.UserId.Trim().ToLowerInvariant()).ToList();
                    var accounts = await db.UserAccounts.AsNoTracking()
                        .Where(x => ids.Contains(x.UserName) && x.State == AccountState.Active)
                        .Select(x => new { x.Id, x.UserName, x.DisplayName }).ToListAsync(ct);
                    if (accounts.Count != ids.Count)
                        return Results.BadRequest(new { error = "Every stage approver must be an active AeroLink user." });
                    var directory = accounts.ToDictionary(x => x.UserName, StringComparer.OrdinalIgnoreCase);
                    var programId = await db.Projects.AsNoTracking().Where(x => x.Id == review.ProjectId)
                        .Select(x => x.ProgramId).SingleAsync(ct);
                    selections = new List<ApproverSelection>();
                    for (var index = 0; index < requested.Count; index++)
                    {
                        var chosen = requested[index];
                        var account = directory[chosen.UserId.Trim().ToLowerInvariant()];
                        var role = index < workflow.Stages.Count
                            ? await WorkflowEndpoints.StageAuthorityAsync(db, review.ProjectId, account.Id,
                                workflow.Stages[index].RequiredRole, ct)
                            : (await WorkflowEndpoints.AuthoritiesAsync(db, review.ProjectId, [account.Id], ct))
                                .GetValueOrDefault(account.Id);
                        if (role is null && index < workflow.Stages.Count
                            && await identity.HasRoleAsync(account.Id, programId, workflow.Stages[index].RequiredRole,
                                DateTimeOffset.UtcNow, ct))
                            role = workflow.Stages[index].RequiredRole;
                        if (role is null)
                            return Results.BadRequest(new { error = $"{account.DisplayName} does not hold authority to sign this review." });
                        selections.Add(new ApproverSelection(account.UserName, account.DisplayName, role));
                    }
                }
                var now = DateTimeOffset.UtcNow;
                var problemReportIds = await db.ProblemReportLinks.AsNoTracking()
                    .Where(x => x.ArtifactType == "TestChangeRequest" && x.ArtifactId == review.Id)
                    .Select(x => x.ProblemReportId).ToListAsync(ct);
                var impactItems = await db.VerificationImpactItems.AsNoTracking()
                    .Where(x => x.TestChangeReviewId == review.Id).ToListAsync(ct);
                var impactDecisions = impactItems.Select(x => new VerificationImpactSnapshot(
                    x.Id, x.ChangeRequestId, x.Trigger, x.RequirementChangeId, x.RequirementRevisionId,
                    x.ProcedureId, x.SubjectDisplayNumber, x.Outcome, x.ProcedureChangeAction,
                    x.ResolutionRationale, x.ResolvedProcedureId, x.ResolvedProcedureRevisionId,
                    x.RetargetedRequirementRevisionId, x.PreReleaseEvidenceRequired)).ToList();
                // Same rule as a change request: whoever submits first takes the procedure, and the second is
                // told which test change request has it rather than discovering it at approval.
                var contendedProcedures = review.ProcedureChanges
                    .Where(x => x.Kind is TestProcedureChangeKind.Modify or TestProcedureChangeKind.Retire)
                    .Select(x => x.BaseNumber).Distinct().ToList();
                var blockingProcedures = (await ArtifactClaims.ProcedureContendersAsync(db, review.ProjectId,
                    contendedProcedures, review.Id, ct)).Where(x => x.Holds).ToList();
                if (blockingProcedures.Count > 0)
                    return Results.BadRequest(new
                    {
                        error = ArtifactClaims.Refusal(blockingProcedures,
                            TestChangeRequestSourceEligibility.ArtifactPlural(review.ArtifactKey)),
                        code = "procedure_claimed"
                    });

                var cycle = review.SubmitForReview(http.UserAccount().UserName, selections, allResolved, now,
                    workflow?.Mode ?? ReviewMode.Sequential, workflow, problemReportIds, impactDecisions);
                foreach (var step in cycle.Steps.Where(x => x.State == ApprovalStepState.Active))
                    db.UserNotifications.Add(new(review.ProjectId, step.ApproverId, "TestChangeRequestApprovalRequested",
                        $"Review {review.DisplayNumber}", $"{http.UserAccount().DisplayName} selected you to approve this test change request.",
                        $"test-change-request:{review.Id}", review.Id, now));
                await db.SaveChangesAsync(ct);
                return Results.Ok(new
                {
                    review.Id,
                    state = review.State.ToString(),
                    cycleId = cycle.Id,
                    sequence = cycle.Sequence,
                    stageCount = cycle.Steps.Count
                });
            }
            catch (DbUpdateConcurrencyException)
            {
                // A governed mutation committed between this request's load and save (for example a Problem
                // Report link or an impact decision). The whole unit of work is rolled back: no cycle, no
                // notification, no signature is persisted.
                return Results.Conflict(new
                {
                    error = "This test change request changed after it was opened. Refresh it before submitting it for review.",
                    code = "stale_version"
                });
            }
            catch (DomainException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        app.MapPost("/api/test-change-reviews/{id:guid}/approve", async (Guid id, ApproveTestChangeReviewRequest request,
            HttpContext http, AeroLinkDbContext db, IdentityService identity,
            VerificationImpactService verificationImpact, CancellationToken ct) =>
        {
            var review = await db.TestChangeReviews.Include(x => x.ReviewCycles).ThenInclude(x => x.Steps)
                .SingleOrDefaultAsync(x => x.Id == id, ct);
            if (review is null) return Results.NotFound();
            if (await db.Releases.AnyAsync(x => x.Id == review.ReleaseId && x.IsReleased, ct))
                return Results.Conflict(new { error = "Released software-build test change reviews are read-only." });
            if (!await http.HasProjectAccessAsync(db, review.ProjectId, ct))
                return Results.Forbid();
            if (string.IsNullOrWhiteSpace(request.Rationale))
                return Results.BadRequest(new { error = "An approval rationale is required.", code = "approval_rationale_required" });
            if (string.IsNullOrWhiteSpace(request.Meaning))
                return Results.BadRequest(new { error = "An explicit electronic signature meaning is required.", code = "signature_meaning_required" });

            var actor = http.UserAccount();
            var cycle = review.ActiveReviewCycle;
            if (cycle is null)
                return Results.BadRequest(new { error = "This test change request has no active review." });
            var activeStep = cycle.Steps.SingleOrDefault(x => x.State == ApprovalStepState.Active
                && string.Equals(x.ApproverId, actor.UserName, StringComparison.OrdinalIgnoreCase));
            if (activeStep is null)
                return Results.BadRequest(new { error = "Only the active approver can approve this review stage." });
            var programId = await db.Projects.AsNoTracking().Where(x => x.Id == review.ProjectId)
                .Select(x => x.ProgramId).SingleAsync(ct);
            // Configured workflows freeze the authority selected for each stage. Requiring a generic Approver
            // here would invalidate legitimate TestLead and ConfigurationManager stages. The no-workflow
            // fallback has no such governed stage, so its signer must still hold current Approver authority,
            // including administrator substitution and active delegation.
            if (cycle.WorkflowId is null
                && !await identity.HasRoleAsync(actor, programId, ProgramRole.Approver, DateTimeOffset.UtcNow, ct))
                return Results.Forbid();
            // Credential knowledge is reconfirmed only after every other authorization/input gate and
            // immediately before the controlled mutation. A refusal therefore cannot advance a step, create a
            // signature, or activate/notify the next stage.
            if (!await identity.ConfirmPasswordAsync(actor.Id, request.Password ?? "", ct))
                return Results.Json(new
                {
                    error = "Electronic signature confirmation failed.",
                    code = "electronic_signature_confirmation_failed"
                }, statusCode: StatusCodes.Status401Unauthorized);
            try
            {
                await using var transaction = await db.Database.BeginTransactionAsync(ct);
                var now = DateTimeOffset.UtcNow;
                var snapshotHash = cycle.SnapshotHash;
                var activeBefore = cycle.Steps.Where(x => x.State == ApprovalStepState.Active)
                    .Select(x => x.ApproverId).ToHashSet(StringComparer.OrdinalIgnoreCase);
                review.ApproveActiveStage(actor.UserName, request.Rationale, now);
                var activated = review.ActiveReviewCycle?.Steps
                    .Where(x => x.State == ApprovalStepState.Active && !activeBefore.Contains(x.ApproverId))
                    .ToList() ?? [];
                foreach (var step in activated)
                    db.UserNotifications.Add(new(review.ProjectId, step.ApproverId, "ReviewActivated",
                        $"Review {review.DisplayNumber}",
                        $"The prior stage is complete. You are now authorized to review {review.DisplayNumber}.",
                        $"test-change-request:{review.Id}", review.Id, now));
                db.ElectronicSignatures.Add(new(actor.Id, actor.UserName, actor.DisplayName, programId,
                    "TestChangeRequest", review.Id, review.DisplayNumber, "Approve", request.Meaning.Trim(),
                    snapshotHash, http.Connection.RemoteIpAddress?.ToString() ?? "local", now,
                    activeStep.Authority, rationale: request.Rationale.Trim()));
                await db.SaveChangesAsync(ct);
                // The exact Case origin must already be Approved when PostgreSQL validates the polymorphic
                // origin. Keep both saves in one transaction so approval and assessment raising are still one
                // atomic controlled act, while the direct database guard observes the truthful source state.
                if (review.State == TestChangeReviewState.Approved
                    && review.ArtifactKind == VerificationArtifactKind.Case)
                {
                    await verificationImpact.RaiseForApprovedCaseReviewAsync(review, now, ct);
                    await db.SaveChangesAsync(ct);
                }
                await transaction.CommitAsync(ct);
                return Results.Ok(new
                {
                    review.Id,
                    state = review.State.ToString(),
                    cycleState = review.ActiveReviewCycle?.State.ToString()
                });
            }
            catch (DomainException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        app.MapPost("/api/test-change-reviews/{id:guid}/return", async (Guid id, ReturnTestChangeReviewRequest request,
            HttpContext http, AeroLinkDbContext db, IdentityService identity, CancellationToken ct) =>
        {
            var review = await db.TestChangeReviews.Include(x => x.ReviewCycles).ThenInclude(x => x.Steps)
                .SingleOrDefaultAsync(x => x.Id == id, ct);
            if (review is null) return Results.NotFound();
            if (await db.Releases.AnyAsync(x => x.Id == review.ReleaseId && x.IsReleased, ct))
                return Results.Conflict(new { error = "Released software-build test change reviews are read-only." });
            if (!await http.HasProjectAccessAsync(db, review.ProjectId, ct))
                return Results.Forbid();
            try
            {
                review.RequestChanges(http.UserAccount().UserName, request.Rationale, DateTimeOffset.UtcNow);
                await db.SaveChangesAsync(ct);
                return Results.Ok(new { review.Id, state = review.State.ToString() });
            }
            catch (DomainException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        /// <summary>
        /// The procedures that already verify what these approved changes touched.
        ///
        /// When an assessment concludes that a change needs test work, the work is almost always to re-align
        /// the procedures that verify the changed requirement — and the engineer had to go and find them.
        /// This answers "what already covers this" so the package can be raised with those procedures already
        /// proposed for modification, which is the common case rather than a special one.
        ///
        /// Suggestions only. Nothing is proposed until the engineer saves the package, and every suggestion
        /// can be edited or removed first.
        /// </summary>
        app.MapGet("/api/releases/{releaseId:guid}/test-change-request-coverage", async (Guid releaseId,
            TestChangeReviewDiscipline discipline, string? changeRequestIds,
            HttpContext http, AeroLinkDbContext db, IProjectLadderPolicyResolver policyResolver, CancellationToken ct) =>
        {
            var release = await db.Releases.AsNoTracking().Where(x => x.Id == releaseId)
                .Select(x => new { x.ProjectId }).SingleOrDefaultAsync(ct);
            if (release is null) return Results.NotFound();
            var ladderPolicy = await policyResolver.ResolveAsync(release.ProjectId, ct);
            if (!await http.HasProjectAccessAsync(db, release.ProjectId, ct)) return Results.Forbid();

            var ids = (changeRequestIds ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(x => Guid.TryParse(x.Trim(), out var value) ? value : Guid.Empty)
                .Where(x => x != Guid.Empty).Distinct().ToList();
            if (ids.Count == 0) return Results.Ok(Array.Empty<object>());

            // The controlled identities those changes touched. Introductions are excluded deliberately: a
            // requirement being introduced has nothing verifying it yet, so there is nothing to re-align.
            var touched = await db.RequirementChanges.AsNoTracking()
                .Where(x => ids.Contains(x.ChangeRequestId) && x.Kind != RequirementChangeKind.Introduce)
                .Select(x => x.BaseNumber).Distinct().ToListAsync(ct);
            if (touched.Count == 0) return Results.Ok(Array.Empty<object>());

            var requirementRevisionIds = await (from artifact in db.Requirements.AsNoTracking()
                    where artifact.ProjectId == release.ProjectId && touched.Contains(artifact.BaseNumber)
                    join revision in db.RequirementRevisions.AsNoTracking() on artifact.Id equals revision.ArtifactId
                    select revision.Id).ToListAsync(ct);
            if (requirementRevisionIds.Count == 0) return Results.Ok(Array.Empty<object>());

            TestProcedureLevel level;
            try
            {
                level = ladderPolicy.ProcedureLevel(ladderPolicy.RequirementLevelFor(discipline));
            }
            catch (DomainException)
            {
                return Results.BadRequest(new { error = "The test-change discipline is not supported." });
            }
            var covering = await (from coverage in db.TestCoverage.AsNoTracking()
                    where requirementRevisionIds.Contains(coverage.RequirementRevisionId)
                    join procedureRevision in db.TestProcedureRevisions.AsNoTracking()
                        on coverage.ProcedureRevisionId equals procedureRevision.Id
                    join procedure in db.TestProcedures.AsNoTracking()
                        .Where(x => x.Level == TestProcedureLevel.System || x.ArtifactKind == VerificationArtifactKind.Case)
                        on procedureRevision.ProcedureId equals procedure.Id
                    where procedure.ProjectId == release.ProjectId && procedure.Level == level
                    // The title is the procedure's, not the revision's — a revision carries objective, steps
                    // and expected result, and what the procedure is called belongs to the procedure.
                    select new { procedure.BaseNumber, procedureRevision.Revision, procedure.Title })
                .ToListAsync(ct);

            // One suggestion per procedure, at its highest revision, because a procedure covering two changed
            // requirements is still one procedure to re-align.
            var suggestions = covering
                .GroupBy(x => x.BaseNumber)
                .Select(group => group.OrderByDescending(x => x.Revision).First())
                .OrderBy(x => x.BaseNumber)
                .Select(x => new { baseNumber = x.BaseNumber, currentRevision = x.Revision, title = x.Title })
                .ToList();
            return Results.Ok(suggestions);
        });

        // The controlled publication, as a change request has. An approver reading a package outside the
        // product needed the same document the requirements side has always produced.
        app.MapGet("/api/test-change-reviews/{id:guid}/download", async (Guid id, string? format,
            HttpContext http, AeroLinkDbContext db, TestChangeRequestOutputGenerator generator, CancellationToken ct) =>
        {
            var package = await db.TestChangeReviews.AsNoTracking()
                .Where(x => x.Id == id).Select(x => new { x.ProjectId }).SingleOrDefaultAsync(ct);
            if (package is null) return Results.NotFound();
            // Gated on Project access. The change request's own download predates the project-scoped guard and
            // is reachable to any authenticated caller; a new route inheriting that would be a new hole.
            if (!await http.HasProjectAccessAsync(db, package.ProjectId, ct)) return Results.Forbid();
            var output = await generator.GenerateAsync(id, format ?? "docx", ct);
            return output is null ? Results.NotFound() : Results.File(output.Content, output.ContentType, output.FileName);
        });

        // Deferral, the same capability a change request has. A package the programme has decided to drop had
        // nowhere to go: it sat in review holding a gate that would never clear.
        app.MapPost("/api/test-change-reviews/{id:guid}/defer", async (Guid id, DeferTestChangeReviewRequest request,
            HttpContext http, AeroLinkDbContext db, IdentityService identity, CancellationToken ct) =>
        {
            var review = await db.TestChangeReviews.Include(x => x.ReviewCycles).ThenInclude(x => x.Steps)
                .SingleOrDefaultAsync(x => x.Id == id, ct);
            if (review is null) return Results.NotFound();
            if (await db.Releases.AnyAsync(x => x.Id == review.ReleaseId && x.IsReleased, ct))
                return Results.Conflict(new { error = "Released software-build test change reviews are read-only." });
            if (!await http.HasProjectRoleAsync(db, identity, review.ProjectId, ct,
                    ProgramRole.TestEngineer, ProgramRole.TestLead))
                return Results.Forbid();
            try
            {
                review.Defer(request.Reason, DateTimeOffset.UtcNow);
                await db.SaveChangesAsync(ct);
                return Results.Ok(new { review.Id, state = review.State.ToString() });
            }
            catch (DomainException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        app.MapPost("/api/test-change-reviews/{id:guid}/reinstate", async (Guid id,
            HttpContext http, AeroLinkDbContext db, IdentityService identity, CancellationToken ct) =>
        {
            var review = await db.TestChangeReviews.SingleOrDefaultAsync(x => x.Id == id, ct);
            if (review is null) return Results.NotFound();
            if (await db.Releases.AnyAsync(x => x.Id == review.ReleaseId && x.IsReleased, ct))
                return Results.Conflict(new { error = "Released software-build test change reviews are read-only." });
            if (!await http.HasProjectRoleAsync(db, identity, review.ProjectId, ct,
                    ProgramRole.TestEngineer, ProgramRole.TestLead))
                return Results.Forbid();
            try
            {
                review.Reinstate(DateTimeOffset.UtcNow);
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
            var reopenReview = await db.TestChangeReviews.SingleOrDefaultAsync(x => x.Id == item.TestChangeReviewId, ct);
            if (reopenReview is null) return Results.NotFound();
            if (reopenReview.State != TestChangeReviewState.Draft)
                return Results.Conflict(new { error = "Verification decisions can be changed only while the test change request is a Draft." });
            var reopenRefusal = await RefuseUnlessAuthoredBy(reopenReview, http, db, identity, ct);
            if (reopenRefusal is not null) return reopenRefusal;
            if (await db.TestChangeReviews.AsNoTracking().AnyAsync(x =>
                    x.ArtifactKind == VerificationArtifactKind.Procedure
                    && x.OriginKind == TestChangeReviewOriginKind.CaseAssessment
                    && x.OriginReferenceId == item.Id, ct))
                return Results.Conflict(new
                {
                    error = "This Case assessment is the immutable origin of a Procedure package and cannot be reopened; raise a new assessment instead.",
                    code = "immutable_case_assessment_origin"
                });
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
                reopenReview.RecordControlledContentChange(now);
                await db.SaveChangesAsync(ct);
                return Results.Ok((await MapAsync([item], db, ct)).Single());
            }
            catch (DbUpdateConcurrencyException)
            {
                return Results.Conflict(new
                {
                    error = "This test change request changed after it was opened. Refresh and try again.",
                    code = "stale_version"
                });
            }
            catch (DomainException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        return app;
    }

    private static async Task<IReadOnlyList<object>> MapAsync(
        IReadOnlyCollection<VerificationImpactItem> items, AeroLinkDbContext db, CancellationToken ct)
    {
        var revisionIdsForSubjects = items.Where(x => x.RequirementRevisionId is not null)
            .Select(x => x.RequirementRevisionId!.Value).Distinct().ToList();
        var revisionSubjects = await db.RequirementRevisions.AsNoTracking()
            .Where(x => revisionIdsForSubjects.Contains(x.Id)).ToDictionaryAsync(x => x.Id, x => x.Statement, ct);
        var changeIdsForSubjects = items.Where(x => x.RequirementChangeId is not null)
            .Select(x => x.RequirementChangeId!.Value).Distinct().ToList();
        var changeSubjects = await db.RequirementChanges.AsNoTracking()
            .Where(x => changeIdsForSubjects.Contains(x.Id)).ToDictionaryAsync(x => x.Id, x => x.Statement, ct);
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
                subjectStatement = x.RequirementRevisionId is not null && revisionSubjects.TryGetValue(x.RequirementRevisionId.Value, out var revisionStatement)
                    ? revisionStatement
                    : x.RequirementChangeId is not null && changeSubjects.TryGetValue(x.RequirementChangeId.Value, out var changeStatement)
                        ? changeStatement : "",
                x.DeclaredVerificationMethod,
                x.RequirementChangeId,
                x.RequirementRevisionId,
                artifactId = x.ProcedureId,
                procedureId = x.ProcedureId, // compatibility alias
                x.AssignedEngineerId,
                x.AssignedByLeadId,
                x.AssignedAt,
                outcome = x.Outcome?.ToString(),
                artifactChangeAction = x.ProcedureChangeAction?.ToString(),
                procedureChangeAction = x.ProcedureChangeAction?.ToString(), // compatibility alias
                x.PreReleaseEvidenceRequired,
                resolvedArtifactId = x.ResolvedProcedureId,
                resolvedArtifactRevisionId = x.ResolvedProcedureRevisionId,
                resolvedProcedureId = x.ResolvedProcedureId, // compatibility alias
                resolvedProcedureRevisionId = x.ResolvedProcedureRevisionId, // compatibility alias
                resolvedArtifact = procedure is null ? null : new
                {
                    id = procedure.ProcedureId,
                    revisionId = procedure.RevisionId,
                    procedure.DisplayNumber,
                    procedure.Title,
                    procedure.TitleIsExact,
                    procedure.TitleIsLegacy,
                    procedure.TitleNote,
                    procedure.Level,
                    procedure.State,
                    configuration = new
                    {
                        x.RequirementRevisionId,
                        artifactRevisionId = procedure.RevisionId,
                        procedureRevisionId = procedure.RevisionId // compatibility alias
                    }
                },
                resolvedProcedure = procedure is null ? null : new // compatibility alias
                {
                    id = procedure.ProcedureId,
                    revisionId = procedure.RevisionId,
                    procedure.DisplayNumber,
                    procedure.Title,
                    procedure.TitleIsExact,
                    procedure.TitleIsLegacy,
                    procedure.TitleNote,
                    procedure.Level,
                    procedure.State,
                    configuration = new
                    {
                        x.RequirementRevisionId,
                        artifactRevisionId = procedure.RevisionId,
                        procedureRevisionId = procedure.RevisionId // compatibility alias
                    }
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
                    artifactId = h.ProcedureId,
                    artifactRevisionId = h.ProcedureRevisionId,
                    procedureId = h.ProcedureId, // compatibility alias
                    procedureRevisionId = h.ProcedureRevisionId, // compatibility alias
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
            join procedure in db.TestProcedures.AsNoTracking()
                on revision.ProcedureId equals procedure.Id
            where revisionIds.Contains(revision.Id) || procedureIds.Contains(procedure.Id)
            select new
            {
                ProcedureId = procedure.Id,
                RevisionId = revision.Id,
                revision.Revision,
                procedure.BaseNumber,
                procedure.Level,
                revision.State
            }).ToListAsync(ct);
        var titles = await TestProcedureRevisionTitleProjection.ForRevisionsAsync(db,
            rows.Select(x => x.RevisionId).Distinct().ToList(), ct);
        return rows.Select(x =>
        {
            var title = titles[x.RevisionId];
            return new ProcedureRow(x.ProcedureId, x.RevisionId, x.Revision, x.BaseNumber,
                title.Title, title.IsExact, title.IsLegacy, title.Note, x.Level.ToString(), x.State);
        }).ToList();
    }

    /// <summary>
    /// The gate every write to a test change request's procedure decisions passes.
    ///
    /// Extracted rather than repeated because the three rules travel together and are easy to get partly
    /// right: a released build is read-only, the actor holds test authority, and an assigned package is the
    /// assignee's to edit unless a lead is doing it.
    /// </summary>
    private static async Task<IResult?> RefuseUnlessAuthoredBy(TestChangeReview review, HttpContext http,
        AeroLinkDbContext db, IdentityService identity, CancellationToken ct)
    {
        if (await db.Releases.AnyAsync(x => x.Id == review.ReleaseId && x.IsReleased, ct))
            return Results.Conflict(new { error = "Released software-build test change requests are read-only." });
        if (!await http.HasProjectRoleAsync(db, identity, review.ProjectId, ct, ProgramRole.TestEngineer, ProgramRole.TestLead))
            return Results.Forbid();
        var actor = http.UserAccount().UserName;
        if (review.AssignedEngineerId is not null
            && !string.Equals(review.AssignedEngineerId, actor, StringComparison.OrdinalIgnoreCase)
            && !await http.HasProjectRoleAsync(db, identity, review.ProjectId, ct, ProgramRole.TestLead))
            return Results.Forbid();
        return null;
    }

    /// <summary>
    /// The newest review cycle by Sequence that was not cancelled. Explicit and deterministic: it never
    /// depends on EF navigation order, and a cancelled cycle is a dead end that must not drive capabilities.
    /// </summary>
    private static ReviewCycle? LatestNonCancelledCycle(TestChangeReview review) =>
        review.ReviewCycles
            .Where(x => x.State != ReviewCycleState.Cancelled)
            .OrderByDescending(x => x.Sequence)
            .FirstOrDefault();

    private static bool ArtifactRouteAllows(string artifactRoute, VerificationArtifactKey key)
    {
        if (artifactRoute.StartsWith("case-", StringComparison.OrdinalIgnoreCase))
            return key.Kind == VerificationArtifactKind.Case;
        // procedure-changes was the original shared route name and remains a compatibility alias for
        // historical software Case packages. New Procedure packages always use their Procedure key.
        return artifactRoute.StartsWith("procedure-", StringComparison.OrdinalIgnoreCase)
            && (key.Kind == VerificationArtifactKind.Procedure || key.Kind == VerificationArtifactKind.Case);
    }

    private static IReadOnlyList<Guid> DrivingRequirements(string json)
    {
        try
        {
            return ExactParentSelectionPolicy.NormalizeIds(
                JsonSerializer.Deserialize<List<Guid>>(string.IsNullOrWhiteSpace(json) ? "[]" : json) ?? [],
                "exact parent selection");
        }
        catch (JsonException)
        {
            throw new DomainException("A test artifact carries malformed exact parent revisions.");
        }
    }

    private static ApprovedProcedureSelection ToSelection(ProcedureRow row) =>
        new(row.ProcedureId, row.RevisionId, row.Revision, $"{row.BaseNumber}.{row.Revision:D2}",
            row.Title, row.TitleIsExact, row.TitleIsLegacy, row.TitleNote,
            row.Level, row.State.ToString());

    private sealed record ProcedureRow(Guid ProcedureId, Guid RevisionId, int Revision, string BaseNumber,
        string Title, bool TitleIsExact, bool TitleIsLegacy, string? TitleNote,
        string Level, TestProcedureState State);
}
