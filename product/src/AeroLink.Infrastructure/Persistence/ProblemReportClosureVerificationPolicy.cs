using AeroLink.Domain.Requirements;
using AeroLink.Domain.Verification;
using Microsoft.EntityFrameworkCore;

namespace AeroLink.Infrastructure.Persistence;

public sealed record ProblemReportVerificationScope(
    Guid ProblemReportId,
    Guid? TargetReleaseId,
    Guid? OriginExecutionId,
    DateTimeOffset? OriginExecutedAt,
    DateTimeOffset? VerificationReadyAt,
    Guid? ProcedureId,
    IReadOnlySet<Guid> PermittedProcedureRevisionIds,
    string? ErrorCode,
    string? Error)
{
    public bool IsResolved => ErrorCode is null;
}

public sealed record ProblemReportVerificationDecision(
    bool Accepted, string? Code, string? Error, ProblemReportVerificationScope Scope)
{
    public static ProblemReportVerificationDecision Accept(ProblemReportVerificationScope scope) =>
        new(true, null, null, scope);
    public static ProblemReportVerificationDecision Reject(ProblemReportVerificationScope scope, string code, string error) =>
        new(false, code, error, scope);
}

/// <summary>
/// Resolves the controlled test chain a Problem Report is allowed to use for closure. Both the corrective
/// action read and the closure mutation use this projection so browser guidance and server authority cannot
/// disagree about the target build, procedure lineage, revision effectivity, or causal retest.
/// </summary>
public sealed class ProblemReportClosureVerificationPolicy(AeroLinkDbContext db)
{
    public async Task<ProblemReportVerificationScope> ResolveAsync(ProblemReport report, CancellationToken ct)
    {
        DateTimeOffset? verificationReadyAt = report.State == ProblemReportState.Verifying
            ? report.UpdatedAt
            : await LatestResolutionProposedAsync(report.Id, ct);
        var originatingLinks = (await db.ProblemReportLinks.AsNoTracking()
            .Where(link => link.ProblemReportId == report.Id
                && link.ArtifactType == "TestExecution"
                && link.Relationship == ProblemReportRelationshipPolicy.OriginatingFailure)
            .ToListAsync(ct)).OrderBy(link => link.AddedAt).ToList();

        if (originatingLinks.Count > 0)
        {
            var origin = await db.TestExecutions.AsNoTracking()
                .SingleOrDefaultAsync(item => item.Id == originatingLinks[0].ArtifactId, ct);
            if (origin is null || origin.ProjectId != report.ProjectId)
                return Unknown(report, "The originating failed execution is no longer available in this Project.");
            var originRevision = await db.TestProcedureRevisions.AsNoTracking()
                .SingleOrDefaultAsync(item => item.Id == origin.ProcedureRevisionId, ct);
            if (originRevision is null)
                return Unknown(report, "The originating execution no longer resolves to a controlled verification artifact revision.");

            if (report.TargetReleaseId is null)
                return new(report.Id, null, origin.Id, origin.ExecutedAt, verificationReadyAt, originRevision.ProcedureId,
                    new HashSet<Guid> { originRevision.Id }, "pr_verification_scope_unknown",
                    "Select the Problem Report target build before recording closure verification.");

            var effectivity = await TestProcedureEffectivity.ForReleaseAsync(
                db, report.ProjectId, report.TargetReleaseId.Value, ct);
            if (effectivity is null
                || !effectivity.RevisionByProcedure.TryGetValue(originRevision.ProcedureId, out var targetRevisionId))
                return new(report.Id, report.TargetReleaseId, origin.Id, origin.ExecutedAt, verificationReadyAt, originRevision.ProcedureId,
                    new HashSet<Guid>(), "pr_verification_scope_unknown",
                    "The target build does not carry a controlled effective revision of the verification artifact whose failure raised this report.");

            return new(report.Id, report.TargetReleaseId, origin.Id, origin.ExecutedAt, verificationReadyAt,
                originRevision.ProcedureId, new HashSet<Guid> { targetRevisionId }, null, null);
        }

        if (report.TargetReleaseId is null)
            return Unknown(report, "Select the Problem Report target build and link its controlled corrective test work before recording closure verification.");

        // A manually raised report has no failed execution from which procedure scope can be inferred. Its
        // dedicated TCR relationship is the explicit controlled authority: only approved packages for the
        // current target build may contribute procedures, and current build effectivity chooses their exact
        // revisions. A neutral requirement context link alone is deliberately not promoted into evidence.
        var tcrIds = await db.ProblemReportLinks.AsNoTracking()
            .Where(link => link.ProblemReportId == report.Id
                && link.ArtifactType == "TestChangeRequest"
                && link.Relationship == ProblemReportRelationshipPolicy.VerificationForProblem)
            .Select(link => link.ArtifactId).Distinct().ToListAsync(ct);
        var approvedTcrIds = await db.TestChangeReviews.AsNoTracking()
            .Where(item => tcrIds.Contains(item.Id) && item.ProjectId == report.ProjectId
                && item.ReleaseId == report.TargetReleaseId && item.State == TestChangeReviewState.Approved)
            .Select(item => item.Id).ToListAsync(ct);
        if (approvedTcrIds.Count == 0)
            return Unknown(report, "Link an approved corrective Test Change Request for this target build before recording closure verification.");

        var effectivityForManual = await TestProcedureEffectivity.ForReleaseAsync(
            db, report.ProjectId, report.TargetReleaseId.Value, ct);
        if (effectivityForManual is null)
            return Unknown(report, "The target build has no controlled verification artifact manifest from which closure scope can be established.");

        var procedureIds = (await db.VerificationImpactItems.AsNoTracking()
                .Where(item => approvedTcrIds.Contains(item.TestChangeReviewId) && item.ResolvedProcedureId != null)
                .Select(item => item.ResolvedProcedureId!.Value).ToListAsync(ct)).ToHashSet();
        var materializedSourceProcedureIds = await db.TestProcedureRevisions.AsNoTracking()
            .Where(revision => revision.SourceTestChangeRequestId != null
                && approvedTcrIds.Contains(revision.SourceTestChangeRequestId.Value))
            .Select(revision => revision.ProcedureId).ToListAsync(ct);
        procedureIds.UnionWith(materializedSourceProcedureIds);

        var permitted = effectivityForManual.RevisionByProcedure
            .Where(pair => procedureIds.Contains(pair.Key)).Select(pair => pair.Value).ToHashSet();
        if (permitted.Count == 0)
            return Unknown(report, "The linked corrective Test Change Request does not establish an effective verification artifact in the target build.");

        var singleProcedure = effectivityForManual.RevisionByProcedure
            .Where(pair => permitted.Contains(pair.Value)).Select(pair => (Guid?)pair.Key).Distinct().ToList();
        return new(report.Id, report.TargetReleaseId, null, null, verificationReadyAt,
            singleProcedure.Count == 1 ? singleProcedure[0] : null, permitted, null, null);
    }

    public async Task<ProblemReportVerificationDecision> ValidateAsync(
        ProblemReport report, TestExecution execution, CancellationToken ct)
    {
        var scope = await ResolveAsync(report, ct);
        if (execution.ProjectId != report.ProjectId)
            return ProblemReportVerificationDecision.Reject(scope, "pr_verification_wrong_project",
                "Closure verification must belong to the Problem Report Project.");
        if (execution.Outcome != TestOutcome.Pass)
            return ProblemReportVerificationDecision.Reject(scope, "pr_verification_not_pass",
                "Closure verification requires a passing execution.");
        if (!scope.IsResolved)
            return ProblemReportVerificationDecision.Reject(scope, scope.ErrorCode!, scope.Error!);

        var executionReleaseId = execution.ReleaseId;
        if (execution.SoftwareBuildId is { } buildId)
        {
            var build = await db.SoftwareBuilds.AsNoTracking()
                .Where(item => item.Id == buildId)
                .Select(item => new { item.ProjectId, item.ReleaseId }).SingleOrDefaultAsync(ct);
            if (build is null || build.ProjectId != report.ProjectId)
                return ProblemReportVerificationDecision.Reject(scope, "pr_verification_wrong_project",
                    "The closure execution's software build does not belong to the Problem Report Project.");
            if (executionReleaseId is not null && executionReleaseId != build.ReleaseId)
                return ProblemReportVerificationDecision.Reject(scope, "pr_verification_wrong_build",
                    "The closure execution carries contradictory build and release scope.");
            executionReleaseId ??= build.ReleaseId;
        }
        if (executionReleaseId != scope.TargetReleaseId)
            return ProblemReportVerificationDecision.Reject(scope, "pr_verification_wrong_build",
                "Closure verification must be recorded against the Problem Report's current target build.");
        if (!scope.PermittedProcedureRevisionIds.Contains(execution.ProcedureRevisionId))
            return ProblemReportVerificationDecision.Reject(scope, "pr_verification_wrong_procedure",
                "Closure verification must execute the effective corrective verification artifact revision carried by the target build.");
        if (string.IsNullOrWhiteSpace(execution.EvidenceReference))
            return ProblemReportVerificationDecision.Reject(scope, "pr_verification_missing_evidence",
                "Closure verification requires an attributable evidence reference.");
        // Causal contract. The retest lineage validated below is the structural proof that this execution
        // succeeded the originating failure; no wall-clock comparison with the origin is needed. Succession
        // against the corrective action is proved with server recording instants: RecordedAt is assigned by
        // this server when the execution row is created, so it is never subject to client clock skew or
        // second-precision truncation. Two sequential requests can complete inside one clock tick, and an
        // equal instant is not evidence that the execution came first - only a strictly earlier recording
        // instant is refused.
        if (scope.VerificationReadyAt is { } readyAt && execution.RecordedAt < readyAt)
            return ProblemReportVerificationDecision.Reject(scope, "pr_verification_not_successor",
                "The closure retest was recorded before the corrective action entered verification, or before the latest change to the verification scope. Record a new passing successor execution.");
        if (scope.OriginExecutionId is { } originId
            && !await RetestChainReachesAsync(execution, originId, scope.ProcedureId!.Value, ct))
            return ProblemReportVerificationDecision.Reject(scope, "pr_verification_not_successor",
                "Closure verification must be a recorded retest successor of the failure that raised this Problem Report.");

        return ProblemReportVerificationDecision.Accept(scope);
    }

    private async Task<bool> RetestChainReachesAsync(
        TestExecution selected, Guid originExecutionId, Guid procedureId, CancellationToken ct)
    {
        var currentId = selected.RetestOfExecutionId;
        var visited = new HashSet<Guid> { selected.Id };
        while (currentId is { } id && visited.Add(id))
        {
            var predecessor = await (from execution in db.TestExecutions.AsNoTracking()
                                     join revision in db.TestProcedureRevisions.AsNoTracking()
                                         on execution.ProcedureRevisionId equals revision.Id
                                     where execution.Id == id
                                     select new { Execution = execution, revision.ProcedureId })
                .SingleOrDefaultAsync(ct);
            if (predecessor is null || predecessor.Execution.ProjectId != selected.ProjectId
                || predecessor.ProcedureId != procedureId)
                return false;
            if (id == originExecutionId) return true;
            currentId = predecessor.Execution.RetestOfExecutionId;
        }
        return false;
    }

    /// <summary>
    /// The structural "corrective action entered verification" event: the most recent ResolutionProposed
    /// lifecycle revision. Selected by the monotonic revision number, never by wall-clock order, so two
    /// same-instant events cannot swap. A legacy report without that event yields no bound; the remaining
    /// project/build/procedure/outcome/evidence/lineage checks still gate adoption.
    /// </summary>
    private async Task<DateTimeOffset?> LatestResolutionProposedAsync(Guid reportId, CancellationToken ct)
    {
        var proposed = await db.ProblemReportRevisions.AsNoTracking()
            .Where(item => item.ProblemReportId == reportId && item.EventType == "ResolutionProposed")
            .OrderByDescending(item => item.Revision)
            .Select(item => (DateTimeOffset?)item.OccurredAt)
            .FirstOrDefaultAsync(ct);
        return proposed;
    }

    private static ProblemReportVerificationScope Unknown(ProblemReport report, string error) =>
        new(report.Id, report.TargetReleaseId, null, null, null, null, new HashSet<Guid>(),
            "pr_verification_scope_unknown", $"The applicable closure verification scope cannot be determined. {error}");
}
