using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Common;
using AeroLink.Domain.Identity;
using AeroLink.Domain.Hierarchy;
using AeroLink.Domain.Verification;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AeroLink.Api;

public sealed record TestChangeReviewApproverChoice(string UserId);

public sealed record SubmitTestChangeReviewCommand(string? ApproverId,
    IReadOnlyList<TestChangeReviewApproverChoice> Approvers);

public sealed record ApproveTestChangeReviewCommand(string Rationale, string Password, string Meaning);

public sealed record TestChangeReviewSubmitted(Guid ReviewId, TestChangeReviewState State, Guid CycleId,
    int Sequence, int StageCount);

public sealed record TestChangeReviewApproved(Guid ReviewId, TestChangeReviewState State,
    ReviewCycleState? CycleState);

/// <summary>A user-facing validation refusal from a workflow use case, kept independent of HTTP.</summary>
public sealed class TestChangeReviewWorkflowException(string message, string? code = null,
    IReadOnlyList<string>? fields = null) : InvalidOperationException(message)
{
    public string? Code { get; } = code;
    public IReadOnlyList<string>? Fields { get; } = fields;
}

public sealed class TestChangeReviewWorkflowForbiddenException(string message)
    : InvalidOperationException(message);

public sealed class ElectronicSignatureConfirmationException()
    : InvalidOperationException("Electronic signature confirmation failed.");

/// <summary>
/// Application orchestration for the two critical TCR workflow mutations.
///
/// Endpoint modules retain route binding and resource-entry checks. This service owns the controlled
/// operation after those checks: authority selection, exact snapshot assembly, aggregate transition,
/// notification creation, signature provenance, and the approval transaction.
/// Callers must authorize Project/release access and author/assignment entry, and load the aggregate's
/// ProcedureChanges and ReviewCycles before invoking this boundary; it does not replace HTTP authorization.
/// </summary>
public sealed class TestChangeReviewWorkflowService(
    AeroLinkDbContext db,
    IdentityService identity,
    VerificationImpactService verificationImpact,
    WorkflowAuthorityService workflowAuthority)
{
    public async Task<TestChangeReviewSubmitted> SubmitAsync(TestChangeReview review,
        SubmitTestChangeReviewCommand command, AuthenticatedUser actor, ILadderPolicy ladderPolicy,
        CancellationToken ct)
    {
        var missingCaseFields = review.MissingCaseFields();
        if (review.Outcome == TestChangeReviewOutcome.ChangeRequired && missingCaseFields.Count > 0)
            throw new TestChangeReviewWorkflowException(
                $"Complete the test change request case before sending it for review. Missing: {string.Join(", ", missingCaseFields)}.",
                "test_change_request_case_incomplete", missingCaseFields);
        if (review.Outcome == TestChangeReviewOutcome.ChangeRequired && review.ProcedureChanges.Count == 0)
            throw new TestChangeReviewWorkflowException(
                $"{review.DisplayNumber} concluded that {TestChangeRequestSourceEligibility.ArtifactNoun(review.ArtifactKey)} work is required but names none. " +
                $"Add the {TestChangeRequestSourceEligibility.ArtifactNoun(review.ArtifactKey)} decisions it carries before sending it for review.");

        await TestChangeReviewRequirementScope.ValidateProcedureChangesForSubmissionAsync(
            db, review, ladderPolicy, ct);
        await TestChangeReviewRequirementScope.ValidateRetargetPlansForSubmissionAsync(db, review, ct, ladderPolicy);
        var allResolved = await db.VerificationImpactItems
            .Where(x => x.TestChangeReviewId == review.Id)
            .AllAsync(x => x.State == VerificationImpactState.Resolved, ct);
        var workflow = await workflowAuthority.ActiveSpecificationAsync(review.ProjectId, review.ArtifactKey,
            ct, ladderPolicy);
        var selections = await ResolveSelectionsAsync(review, workflow, command, ct);
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
        var contendedProcedures = review.ProcedureChanges
            .Where(x => x.Kind is TestProcedureChangeKind.Modify or TestProcedureChangeKind.Retire)
            .Select(x => x.BaseNumber).Distinct().ToList();
        var blockingProcedures = (await ArtifactClaims.ProcedureContendersAsync(db, review.ProjectId,
            contendedProcedures, review.Id, ct)).Where(x => x.Holds).ToList();
        if (blockingProcedures.Count > 0)
            throw new TestChangeReviewWorkflowException(
                ArtifactClaims.Refusal(blockingProcedures,
                    TestChangeRequestSourceEligibility.ArtifactPlural(review.ArtifactKey)), "procedure_claimed");

        var cycle = review.SubmitForReview(actor.UserName, selections, allResolved, now,
            workflow?.Mode ?? ReviewMode.Sequential, workflow, problemReportIds, impactDecisions);
        foreach (var step in cycle.Steps.Where(x => x.State == ApprovalStepState.Active))
            db.UserNotifications.Add(ReviewNotificationFactory.ForTestChangeRequest(review.ProjectId,
                step.ApproverId, step.StageKind, review.DisplayNumber, actor.DisplayName,
                $"test-change-request:{review.Id}", review.Id, now));
        await db.SaveChangesAsync(ct);
        return new(review.Id, review.State, cycle.Id, cycle.Sequence, cycle.Steps.Count);
    }

    public async Task<TestChangeReviewApproved> ApproveAsync(TestChangeReview review,
        ApproveTestChangeReviewCommand command, AuthenticatedUser actor, string remoteIp,
        CancellationToken ct)
    {
        var cycle = review.ActiveReviewCycle;
        if (cycle is null)
            throw new TestChangeReviewWorkflowException("This test change request has no active review.");
        var activeStep = cycle.Steps.SingleOrDefault(x => x.State == ApprovalStepState.Active
            && string.Equals(x.ApproverId, actor.UserName, StringComparison.OrdinalIgnoreCase));
        if (activeStep is null)
            throw new TestChangeReviewWorkflowException("Only the active approver can approve this review stage.");
        var programId = await db.Projects.AsNoTracking().Where(x => x.Id == review.ProjectId)
            .Select(x => x.ProgramId).SingleAsync(ct);
        if (cycle.WorkflowId is null
            && !await identity.HasRoleAsync(actor, programId, ProgramRole.Approver,
                DateTimeOffset.UtcNow, ct))
            throw new TestChangeReviewWorkflowForbiddenException(
                "The active approver does not hold current Approver authority.");
        if (!await identity.ConfirmPasswordAsync(actor.Id, command.Password ?? "", ct))
            throw new ElectronicSignatureConfirmationException();

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var now = DateTimeOffset.UtcNow;
        var snapshotHash = cycle.SnapshotHash;
        var activeBefore = cycle.Steps.Where(x => x.State == ApprovalStepState.Active)
            .Select(x => x.ApproverId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        review.ApproveActiveStage(actor.UserName, command.Rationale, now);
        var activated = review.ActiveReviewCycle?.Steps
            .Where(x => x.State == ApprovalStepState.Active && !activeBefore.Contains(x.ApproverId))
            .ToList() ?? [];
        foreach (var step in activated)
            db.UserNotifications.Add(ReviewNotificationFactory.ForTestChangeRequest(review.ProjectId,
                step.ApproverId, step.StageKind, review.DisplayNumber, actor.DisplayName,
                $"test-change-request:{review.Id}", review.Id, now, priorStageComplete: true));
        db.ElectronicSignatures.Add(new(actor.Id, actor.UserName, actor.DisplayName, programId,
            "TestChangeRequest", review.Id, review.DisplayNumber, activeStep.StageKind.ToString(),
            command.Meaning.Trim(), snapshotHash, remoteIp, now,
            authority: activeStep.Authority, reviewStepId: activeStep.Id,
            reviewCycle: cycle.Sequence, reviewStepPosition: activeStep.Position,
            rationale: command.Rationale.Trim(), authoritySource: activeStep.AuthoritySource?.ToString() ?? "",
            workflowId: cycle.WorkflowId, workflowVersion: cycle.WorkflowVersion,
            authoritySourceId: activeStep.AuthoritySourceId));
        await db.SaveChangesAsync(ct);
        if (review.State == TestChangeReviewState.Approved
            && review.ArtifactKind == VerificationArtifactKind.Case)
        {
            await verificationImpact.RaiseForApprovedCaseReviewAsync(review, now, ct);
            await db.SaveChangesAsync(ct);
        }
        await transaction.CommitAsync(ct);
        return new(review.Id, review.State, review.ActiveReviewCycle?.State);
    }

    private async Task<List<ApproverSelection>> ResolveSelectionsAsync(TestChangeReview review,
        ReviewWorkflowSpecification? workflow, SubmitTestChangeReviewCommand command, CancellationToken ct)
    {
        if (workflow is null)
        {
            if (string.IsNullOrWhiteSpace(command.ApproverId))
                throw new TestChangeReviewWorkflowException("Select an independent test change request approver.");
            var approver = await db.UserAccounts.AsNoTracking().SingleOrDefaultAsync(x =>
                x.UserName == command.ApproverId.Trim().ToLowerInvariant() && x.State == AccountState.Active, ct);
            if (approver is null)
                throw new TestChangeReviewWorkflowException("Select an active AeroLink test change request approver.");
            var programId = await db.Projects.AsNoTracking().Where(x => x.Id == review.ProjectId)
                .Select(x => x.ProgramId).SingleAsync(ct);
            if (!await identity.HasRoleAsync(approver.Id, programId, ProgramRole.Approver,
                    DateTimeOffset.UtcNow, ct))
                throw new TestChangeReviewWorkflowException(
                    $"{approver.DisplayName} does not hold Approver authority for this Program.");
            var resolved = await workflowAuthority.StageAuthorityWithDecisionAsync(review.ProjectId,
                approver.Id, ProgramRole.Approver, ct);
            return [new ApproverSelection(approver.UserName, approver.DisplayName, resolved.Role,
                resolved.Decision.Granted ? resolved.Decision.Source : ProjectAuthoritySource.None,
                resolved.Decision.SourceId)];
        }

        if (command.Approvers.Count < workflow.Stages.Count)
            throw new TestChangeReviewWorkflowException(
                $"{workflow.Name} v{workflow.Version} requires {workflow.Stages.Count} approver{(workflow.Stages.Count == 1 ? "" : "s")} minimum (at least {workflow.Stages.Count}), one for each stage: " +
                string.Join(", ", workflow.Stages.Select(x => x.Name)) + ".");
        var ids = command.Approvers.Select(x => x.UserId.Trim().ToLowerInvariant()).ToList();
        var accounts = await db.UserAccounts.AsNoTracking()
            .Where(x => ids.Contains(x.UserName) && x.State == AccountState.Active)
            .Select(x => new { x.Id, x.UserName, x.DisplayName }).ToListAsync(ct);
        if (accounts.Count != ids.Count)
            throw new TestChangeReviewWorkflowException("Every stage approver must be an active AeroLink user.");
        var directory = accounts.ToDictionary(x => x.UserName, StringComparer.OrdinalIgnoreCase);
        var selections = new List<ApproverSelection>();
        for (var index = 0; index < command.Approvers.Count; index++)
        {
            var chosen = command.Approvers[index];
            var account = directory[chosen.UserId.Trim().ToLowerInvariant()];
            var resolution = index < workflow.Stages.Count
                ? await workflowAuthority.StageAuthorityWithDecisionAsync(review.ProjectId, account.Id,
                    workflow.Stages[index], ct)
                : (await workflowAuthority.AuthoritiesWithDecisionsAsync(review.ProjectId, [account.Id], ct))
                    .GetValueOrDefault(account.Id);
            if (resolution.Role is null)
                throw new TestChangeReviewWorkflowException(
                    $"{account.DisplayName} does not hold authority to sign this review.");
            selections.Add(new ApproverSelection(account.UserName, account.DisplayName, resolution.Role,
                resolution.Decision.Source, resolution.Decision.SourceId));
        }
        return selections;
    }
}
