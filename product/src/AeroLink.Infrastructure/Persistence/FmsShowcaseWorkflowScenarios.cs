using System.Text.Json;
using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Hierarchy;
using AeroLink.Domain.Identity;
using AeroLink.Domain.Programs;
using AeroLink.Domain.Requirements;
using AeroLink.Domain.Releases;
using AeroLink.Domain.Verification;
using Microsoft.EntityFrameworkCore;

namespace AeroLink.Infrastructure.Persistence;

public sealed partial class FmsShowcaseSeeder
{
    public const string WorkflowScenarioPrefix = "workflow-913/";
    private sealed record WorkflowScenarioIdentity(Guid RequestId, Guid BaselineId,
        Guid RequirementRevisionId, Guid? AssessmentId);

    private async Task<HashSet<Guid>> ValidateWorkflowScenariosAsync(Guid programId, Guid projectId, Guid releaseId,
        List<string> problems, CancellationToken ct)
    {
        var markers = await db.ShowcaseUpgradeSteps.AsNoTracking().Where(x => x.ProgramId == programId
            && x.StepKey.StartsWith(WorkflowScenarioPrefix)).ToListAsync(ct);
        var completed = await db.ShowcaseUpgradeSteps.AnyAsync(x => x.ProgramId == programId
            && x.StepKey == "workflow-holder-scenarios", ct);
        var named = new HashSet<Guid>();
        if (markers.Count == 0 && !completed) return named;
        if (markers.Count != 2) problems.Add("Expected both owned workflow-holder scenarios; an exact identity is missing.");
        foreach (var marker in markers)
        {
            WorkflowScenarioIdentity? identity;
            try { identity = JsonSerializer.Deserialize<WorkflowScenarioIdentity>(marker.Detail); }
            catch (JsonException) { identity = null; }
            if (identity is null) { problems.Add($"Invalid identity for {marker.StepKey}."); continue; }
            var request = await db.SystemChangeRequests.AsNoTracking().Include(x => x.RequirementChanges)
                .Include(x => x.ReviewCycles).ThenInclude(x => x.Steps).SingleOrDefaultAsync(x => x.Id == identity.RequestId, ct);
            var member = await (from inclusion in db.BaselineRequirements.AsNoTracking()
                join revision in db.RequirementRevisions.AsNoTracking() on inclusion.RevisionId equals revision.Id
                join artifact in db.Requirements.AsNoTracking() on revision.ArtifactId equals artifact.Id
                where inclusion.BaselineId == identity.BaselineId && revision.Id == identity.RequirementRevisionId
                    && artifact.ProjectId == projectId
                select new { artifact.BaseNumber, revision.Statement, revision.Revision, revision.VerificationMethod }).SingleOrDefaultAsync(ct);
            var proposal = request?.RequirementChanges.Count == 1 ? request.RequirementChanges.Single() : null;
            var assessmentScenario = marker.StepKey == WorkflowScenarioPrefix + "assessment-approval";
            var valid = marker.StepKey == WorkflowScenarioPrefix + "awaiting-approval" || assessmentScenario;
            valid &= request is { Type: ChangeRequestType.System, SoftwareLevel: null, Revision: 0, AuthorId: "systems.author" }
                && request.ProjectId == projectId && request.TargetReleaseId == releaseId
                && request.Title.StartsWith("[Synthetic showcase] Clarify rationale for ", StringComparison.Ordinal)
                && member is not null && proposal is not null && proposal.BaseNumber == member.BaseNumber
                && proposal.Statement == member.Statement && proposal.Revision == member.Revision + 1
                && proposal.VerificationMethod == member.VerificationMethod
                && proposal.Level == RequirementLevel.System && proposal.Kind == RequirementChangeKind.Modify;
            var cycle = request?.ReviewCycles.Count == 1 ? request.ReviewCycles.Single() : null;
            valid &= cycle is not null && cycle.Mode == ReviewMode.Sequential && cycle.WorkflowId is not null
                && cycle.Steps.Count == 3 && cycle.SnapshotContractVersion == SystemChangeRequest.CurrentSnapshotContractVersion;
            if (cycle is not null)
            {
                var steps = cycle.Steps.OrderBy(x => x.Position).ToList();
                valid &= steps.Count == 3 && steps[0].StageKind == ReviewStageKind.Review
                    && steps[1].StageKind == ReviewStageKind.Review && steps[2].StageKind == ReviewStageKind.Approval
                    && steps.All(x => x.AuthoritySourceId is not null && x.AuthoritySource is not (null or ProjectAuthoritySource.AdministratorSubstitution));
                var signatures = await db.ElectronicSignatures.AsNoTracking().Where(x => x.ArtifactId == identity.RequestId).ToListAsync(ct);
                valid &= signatures.Count == (assessmentScenario ? 3 : 2)
                    && signatures.All(x => x.ContentHash == cycle.SnapshotHash && x.WorkflowId == cycle.WorkflowId
                        && x.Meaning.StartsWith("Synthetic showcase fixture", StringComparison.Ordinal)
                        && steps.Any(step => step.Id == x.ReviewStepId && step.ApproverId == x.UserName
                            && step.AuthoritySourceId == x.AuthoritySourceId && step.State == ApprovalStepState.Approved));
                valid &= assessmentScenario
                    ? request?.State == ChangeRequestState.Approved && steps.All(x => x.State == ApprovalStepState.Approved)
                    : request?.State == ChangeRequestState.InReview && steps.Count(x => x.State == ApprovalStepState.Active
                        && x.StageKind == ReviewStageKind.Approval) == 1;
            }
            if (assessmentScenario)
            {
                var assessment = await db.DownstreamChangeAssessments.AsNoTracking().SingleOrDefaultAsync(x => x.Id == identity.AssessmentId, ct);
                valid &= assessment is { State: DownstreamAssessmentState.InReview, Outcome: DownstreamAssessmentOutcome.NoChangeRequired,
                    TargetLevel: RequirementLevel.HighLevel, AssignedEngineerId: "software.author", SelectedApproverId: "software.lead" }
                    && assessment.SourceChangeRequestId == identity.RequestId && assessment.ProjectId == projectId && assessment.ReleaseId == releaseId
                    && assessment.Rationale.StartsWith("Synthetic assessment awaiting independent approval:", StringComparison.Ordinal);
                if (valid) named.Add(identity.RequestId);
            }
            if (!valid) problems.Add($"The exact native state, proposal, review evidence or selected approver of {marker.StepKey} has drifted.");
        }
        return named;
    }

    private async Task<string?> EnsureWorkflowScenariosAsync(Guid programId, CancellationToken ct)
    {
        var project = await db.Projects.SingleAsync(x => x.ProgramId == programId, ct);
        var release = await db.Releases.SingleAsync(x => x.ProjectId == project.Id && x.Version == "1.6", ct);
        var campaign = await db.ReleaseCampaigns.SingleAsync(x => x.ReleaseId == release.Id, ct);
        if (release.IsReleased || campaign.State is ReleaseCampaignState.InReview or ReleaseCampaignState.Released)
            throw new InvalidOperationException("The named workflow scenarios require the unfrozen FMS 1.6 campaign.");
        var policy = await resolver.ResolveAsync(project.Id, ct);
        var at = PersistedTimestamp(DateTimeOffset.UtcNow);
        await EnsureCurrentProgramAuthorityAsync(programId, "systems.author", ProgramRole.SystemEngineer, at, ct);
        await EnsureCurrentProgramAuthorityAsync(programId, "software.author", ProgramRole.SoftwareEngineer, at, ct);
        var authority = new ProjectAuthorityResolver(db);
        var people = await db.UserAccounts.AsNoTracking().Where(x => x.State == AccountState.Active
            && new[] { "systems.reviewer", "systems.lead", "software.lead" }.Contains(x.UserName))
            .ToDictionaryAsync(x => x.UserName, ct);
        var workflow = await db.ReviewWorkflows.Include(x => x.Stages).SingleOrDefaultAsync(x =>
            x.ProjectId == project.Id && x.AppliesTo == policy.WorkflowSubject(ChangeRequestType.System)
            && x.State == ReviewWorkflowState.Active, ct);
        if (workflow is null)
        {
            // The fresh showcase gets the same three-stage arrangement already configured on HOME.
            // Existing configured workflows are always used as recorded, never replaced.
            await EnsureCurrentProgramAuthorityAsync(programId, "program.manager", ProgramRole.ProgramManager, at, ct);
            workflow = new(project.Id, "FMS system engineering review and approval",
                policy.WorkflowSubject(ChangeRequestType.System), ReviewMode.Sequential,
                [new("Peer system engineering review", ProgramRole.SystemEngineer, ReviewStageKind.Review, ReviewStageAuthorityKind.BaseRole),
                 new("System engineering lead review", ProgramRole.SystemEngineeringLead, ReviewStageKind.Review, ReviewStageAuthorityKind.LeadershipPosition),
                 new("Software engineering lead approval", ProgramRole.SoftwareEngineeringLead, ReviewStageKind.Approval, ReviewStageAuthorityKind.LeadershipPosition)],
                "program.manager", at);
            workflow.Activate("program.manager", at);
            db.ReviewWorkflows.Add(workflow);
            db.SecurityAuditEvents.Add(new("ShowcaseWorkflowConfigured", "program.manager", workflow.Id.ToString(), "Configured",
                "Synthetic FMS workflow fixture: peer review, system lead review, software lead approval.", "showcase-upgrade", at));
            await db.SaveChangesAsync(ct);
        }
        var stages = workflow.Stages.OrderBy(x => x.Position).ToList();
        if (workflow.Mode != ReviewMode.Sequential || stages.Count != 3
            || stages[0].Kind != ReviewStageKind.Review || stages[1].Kind != ReviewStageKind.Review
            || stages[2].Kind != ReviewStageKind.Approval)
            throw new InvalidOperationException("The configured FMS System workflow cannot support the named three-stage fixture; it was not changed.");
        var names = new[] { "systems.reviewer", "systems.lead", "software.lead" };
        var selections = new List<ApproverSelection>();
        for (var index = 0; index < stages.Count; index++)
        {
            if (!people.TryGetValue(names[index], out var person))
                throw new InvalidOperationException($"Missing active workflow fixture actor {names[index]}.");
            var granted = await authority.ResolveAsync(person.Id, programId, stages[index].RequiredAuthority, at, ct);
            if (!granted.Granted || granted.Source is ProjectAuthoritySource.AdministratorSubstitution or ProjectAuthoritySource.None)
                throw new InvalidOperationException($"{person.UserName} lacks the current authority required by {stages[index].Name}.");
            selections.Add(new(person.UserName, person.DisplayName, stages[index].RequiredRole, granted.Source, granted.SourceId));
        }
        var baselineId = await TestChangeReviewRequirementScope.EffectiveRequirementBaselineIdAsync(db, project.Id, release.Id, ct)
            ?? throw new InvalidOperationException("The workflow fixtures need the exact authoritative source baseline.");
        var vocabulary = await new ProjectVerificationVocabularyService(db, resolver)
            .ResolveForSubmissionAsync(project.Id, "systems.author", "showcase-upgrade", at, ct);
        for (var index = 0; index < 2; index++)
        {
            var key = WorkflowScenarioPrefix + (index == 0 ? "awaiting-approval" : "assessment-approval");
            if (await db.ShowcaseUpgradeSteps.AnyAsync(x => x.ProgramId == programId && x.StepKey == key, ct))
                throw new InvalidOperationException("A partial workflow fixture cannot be silently adopted; the atomic upgrade must start from its original state.");
            var number = $"SYSR-{149 + index:D6}";
            if (await (from change in db.RequirementChanges.AsNoTracking()
                join other in db.SystemChangeRequests.AsNoTracking() on change.ChangeRequestId equals other.Id
                where other.ProjectId == project.Id && change.BaseNumber == number
                    && (change.Kind == RequirementChangeKind.Modify || change.Kind == RequirementChangeKind.Retire)
                    && (other.State == ChangeRequestState.InReview || other.State == ChangeRequestState.Approved
                        || other.State == ChangeRequestState.SelectedForBaseline)
                select other.Id).AnyAsync(ct))
                throw new InvalidOperationException($"The editorial fixture cannot claim {number}; existing controlled work holds it.");
            var member = await (from inclusion in db.BaselineRequirements.AsNoTracking()
                join revision in db.RequirementRevisions.AsNoTracking() on inclusion.RevisionId equals revision.Id
                join artifact in db.Requirements.AsNoTracking() on revision.ArtifactId equals artifact.Id
                where inclusion.BaselineId == baselineId && artifact.BaseNumber == number && artifact.ProjectId == project.Id
                select new { artifact.BaseNumber, revision.Id, revision.Revision, revision.Statement, revision.VerificationMethod }).SingleAsync(ct);
            var memberDisplay = $"{member.BaseNumber}.{member.Revision:D2}";
            var requestNumber = await IdentifierAllocator.NextChangeRequestAsync(db, ChangeRequestType.System, null, ct, policy);
            var request = new SystemChangeRequest(requestNumber, 0, project.Id, release.Id,
                $"[Synthetic showcase] Clarify rationale for {memberDisplay}",
                "The existing requirement rationale needs an explicit statement of its unchanged operational intent.",
                $"The normative statement remains byte-for-byte identical to {memberDisplay} in baseline {baselineId:D}.",
                "Clarify engineering rationale only; do not change behavior, interfaces, thresholds or allocation.",
                "systems.author", at, ChangeRequestType.System, ladderPolicy: policy);
            request.SetNoUpstreamRationale(request.AuthorId,
                "This is a root System-level editorial clarification of existing FMS scope, not a response to an upstream change request.", at);
            request.AddRequirementChange(request.AuthorId, member.BaseNumber, member.Revision + 1, RequirementLevel.System,
                RequirementChangeKind.Modify, member.Statement,
                "Synthetic editorial example: this existing capability remains applicable in its stated operational modes; no functional or allocation change is proposed.",
                member.VerificationMethod, at, impactDispositionJson: RequirementAuthoringJson.PendingImpactDispositions,
                proposedUpstreamRevisionIdsJson: "[]", ladderPolicy: policy);
            db.SystemChangeRequests.Add(request);
            db.ImpactDispositions.AddRange(Enum.GetValues<ImpactKind>().Select(kind => new ChangeImpactDisposition(campaign.Id,
                request.Id, kind, request.DisplayNumber, "Pending disposition of the synthetic editorial proposal.")));
            await db.SaveChangesAsync(ct);
            var cycle = request.SubmitForReviewWithResolvedTrace(request.AuthorId, selections, at, ReviewMode.Sequential,
                workflow.Specification(), ladderPolicy: policy, verificationPolicy: vocabulary, traceEvidence: new(false, []));
            void NotifyActiveStage()
            {
                foreach (var step in cycle.Steps.Where(x => x.State == ApprovalStepState.Active))
                {
                    var approval = step.StageKind == ReviewStageKind.Approval;
                    db.UserNotifications.Add(new(project.Id, step.ApproverId, approval ? "ApprovalActivated" : "ReviewActivated",
                        $"{(approval ? "Approve" : "Review")} {request.DisplayNumber}",
                        $"The synthetic editorial proposal is ready for your {(approval ? "approval" : "review")}: {request.Title}",
                        $"scr:{request.Id}", request.Id, at));
                }
            }
            NotifyActiveStage();
            await db.SaveChangesAsync(ct);
            var decisions = index == 0 ? 2 : 3;
            for (var position = 0; position < decisions; position++)
            {
                var step = cycle.Steps.Single(x => x.State == ApprovalStepState.Active);
                var person = people[step.ApproverId];
                const string rationale = "Synthetic demonstration decision: the exact normative statement is unchanged; the proposal clarifies rationale only.";
                request.ApproveActiveStage(person.UserName, at, rationale);
                db.ElectronicSignatures.Add(new(person.Id, person.UserName, person.DisplayName, programId, "SCR",
                    request.Id, request.DisplayNumber, step.StageKind.ToString(),
                    "Synthetic showcase fixture generated by the supported upgrade; not a human engineering approval.",
                    cycle.SnapshotHash, "showcase-fixture", at, step.Authority, step.Id, cycle.Sequence, step.Position,
                    rationale, step.AuthoritySource?.ToString() ?? "", cycle.WorkflowId, cycle.WorkflowVersion, step.AuthoritySourceId));
                NotifyActiveStage();
                await db.SaveChangesAsync(ct);
            }
            Guid? assessmentId = null;
            if (index == 1)
            {
                await new VerificationImpactService(db).RaiseForApprovedChangeRequestAsync(request, at, ct, "software.lead");
                await new DownstreamImpactService(db, policy).RaiseForApprovedChangeRequestAsync(request, at, ct);
                await db.SaveChangesAsync(ct);
                var assessment = await db.DownstreamChangeAssessments.SingleAsync(x => x.SourceChangeRequestId == request.Id
                    && x.TargetLevel == RequirementLevel.HighLevel, ct);
                assessment.Assign("software.author", "software.author", at);
                assessment.RecordNoChange("software.author",
                    $"Synthetic assessment awaiting independent approval: {request.DisplayNumber} retains the exact normative statement of {memberDisplay}; only rationale changes, so no HLR behavior or allocation revision is proposed.", at);
                var approver = people["software.lead"];
                var granted = await authority.ResolveAsync(approver.Id, programId,
                    ProjectAuthorityRequirement.LegacyRoleDemand(ProgramRole.Approver), at, ct);
                if (!granted.Granted || granted.Source is ProjectAuthoritySource.AdministratorSubstitution or ProjectAuthoritySource.None)
                    throw new InvalidOperationException("The selected assessment approver lacks current independent Approver authority.");
                assessment.Submit("software.author", approver.UserName, at);
                assessmentId = assessment.Id;
            }
            db.ShowcaseUpgradeSteps.Add(new(programId, key,
                JsonSerializer.Serialize(new WorkflowScenarioIdentity(request.Id, baselineId, member.Id, assessmentId)), at));
            await db.SaveChangesAsync(ct);
        }
        return "Added two explicitly synthetic editorial proposals: a current native Approval stage and a no-change downstream assessment awaiting its independent selected approver. All original records remain intact.";
    }
}
