using System.Text.Json;
using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Hierarchy;
using AeroLink.Domain.Identity;
using AeroLink.Domain.Programs;
using AeroLink.Domain.Requirements;
using AeroLink.Domain.Releases;
using Microsoft.EntityFrameworkCore;

namespace AeroLink.Infrastructure.Persistence;

public sealed partial class FmsShowcaseSeeder
{
    public const int ActiveTraceScenarioChains = 30;
    public const int ActiveTraceRequestCount = ActiveTraceScenarioChains * 3;
    public const string ActiveTraceScenarioPrefix = "active-trace-913/";
    private const string ActiveTraceProposalPrefix = "active-trace-proposal-913/";
    private sealed record ActiveTraceProposalIdentity(Guid RequestId, Guid BaselineId, Guid RequirementRevisionId, Guid? UpstreamRevisionId);

    /// <summary>
    /// Adds named, connected authoring work to the active build. The eight original packages retain their
    /// historical decisions and deliberate incomplete states. No released revision, review, signature or
    /// baseline is edited. Scenario identity is the durable ownership map, never a title or number guess.
    /// </summary>
    private async Task<string?> EnsureActiveTraceScenariosAsync(Guid programId, CancellationToken ct)
    {
        var project = await db.Projects.SingleAsync(x => x.ProgramId == programId, ct);
        var release = await db.Releases.SingleAsync(x => x.ProjectId == project.Id && x.Version == "1.6", ct);
        if (release.IsReleased)
            throw new InvalidOperationException("The #913 authoring scenarios require the exact in-work FMS 1.6 build.");
        var campaign = await db.ReleaseCampaigns.AsNoTracking().SingleAsync(x => x.ReleaseId == release.Id, ct);
        if (campaign.State is ReleaseCampaignState.InReview or ReleaseCampaignState.Released)
            throw new InvalidOperationException("A frozen release package cannot receive showcase authoring scenarios.");
        var policy = await resolver.ResolveAsync(project.Id, ct);
        RequirementLevel[] levels = [RequirementLevel.System, RequirementLevel.HighLevel, RequirementLevel.LowLevel];
        if (levels.Any(level => !policy.OrderedLevels.Contains(level)))
            throw new InvalidOperationException("The #913 FMS scenarios require the configured System/HLR/LLR ladder.");

        var baselineId = await TestChangeReviewRequirementScope.EffectiveRequirementBaselineIdAsync(db, project.Id, release.Id, ct)
            ?? throw new InvalidOperationException("The active showcase has no authoritative requirement source population.");
        var members = await (from member in db.BaselineRequirements.AsNoTracking()
            join revision in db.RequirementRevisions.AsNoTracking() on member.RevisionId equals revision.Id
            join artifact in db.Requirements.AsNoTracking() on revision.ArtifactId equals artifact.Id
            where member.BaselineId == baselineId && artifact.ProjectId == project.Id
            select new { artifact.BaseNumber, artifact.Level, revision.Id, revision.Revision, revision.Statement })
            .ToDictionaryAsync(x => x.BaseNumber, ct);
        var memberIds = members.Values.Select(x => x.Id).ToList();
        var memberById = members.Values.ToDictionary(x => x.Id);
        var traces = await db.RequirementTraces.AsNoTracking().Where(x => x.ProjectId == project.Id
            && memberIds.Contains(x.SourceRevisionId) && memberIds.Contains(x.TargetRevisionId)).ToListAsync(ct);
        var at = PersistedTimestamp(DateTimeOffset.UtcNow);
        string[] systemAuthors = ["systems.author", "systems.reviewer"];
        string[] softwareAuthors = ["software.author", "lead.reviewer", "software.lead"];
        foreach (var author in systemAuthors)
            await EnsureCurrentProgramAuthorityAsync(programId, author, ProgramRole.SystemEngineer, at, ct);
        foreach (var author in softwareAuthors)
            await EnsureCurrentProgramAuthorityAsync(programId, author, ProgramRole.SoftwareEngineer, at, ct);


        var scenarios = new (string Name, string Criterion)[]
        {
            ("invalid-input retention", "An invalid input shall leave the last validated state unchanged and produce an explicit rejection status before the next processing cycle."),
            ("interrupted-update recovery", "An interrupted update shall retain the last validated state and report recovery required before accepting another update.")
        };
        var added = 0;
        SystemChangeRequest? parallelReviewRequest = null;
        for (var index = 1; index <= ActiveTraceScenarioChains; index++)
        {
            var topic = Topics[(index - 1) % Topics.Length];
            var scenario = scenarios[(index - 1) / Topics.Length];
            var highMember = members[$"HLR-{index:D6}"];
            var systemMember = traces.Where(x => x.SourceRevisionId == highMember.Id
                    && memberById[x.TargetRevisionId].Level == RequirementLevel.System)
                .Select(x => memberById[x.TargetRevisionId]).OrderBy(x => x.BaseNumber, StringComparer.Ordinal).FirstOrDefault()
                ?? throw new InvalidOperationException($"{highMember.BaseNumber} has no exact System parent in the selected source baseline.");
            var lowMember = traces.Where(x => x.TargetRevisionId == highMember.Id
                    && memberById[x.SourceRevisionId].Level == RequirementLevel.LowLevel)
                .Select(x => memberById[x.SourceRevisionId]).OrderBy(x => x.BaseNumber, StringComparer.Ordinal).FirstOrDefault()
                ?? throw new InvalidOperationException($"{highMember.BaseNumber} has no exact LLR child in the selected source baseline.");
            SystemChangeRequest? parent = null;
            Guid? parentRequirement = null;
            foreach (var level in levels)
            {
                var key = $"{ActiveTraceScenarioPrefix}{index:D2}/{level}";
                var marker = await db.ShowcaseUpgradeSteps.AsNoTracking()
                    .SingleOrDefaultAsync(x => x.ProgramId == programId && x.StepKey == key, ct);
                var member = level == RequirementLevel.System ? systemMember : level == RequirementLevel.HighLevel ? highMember : lowMember;
                if (marker is not null)
                {
                    if (!Guid.TryParse(marker.Detail, out var id))
                        throw new InvalidOperationException($"The {key} ownership map has no exact artifact identity.");
                    parent = await db.SystemChangeRequests.SingleOrDefaultAsync(x => x.Id == id, ct)
                        ?? throw new InvalidOperationException($"The {key} ownership map names a missing change request.");
                    if (parent.ProjectId != project.Id || parent.TargetReleaseId != release.Id)
                        throw new InvalidOperationException($"The {key} ownership map is outside the exact FMS project/build.");
                    parentRequirement = member.Id;
                    continue;
                }
                var system = level == RequirementLevel.System;
                var type = system ? ChangeRequestType.System : ChangeRequestType.Software;
                var softwareLevel = system ? (RequirementLevel?)null : level;
                // Explicit package responsibility, independent of workload counts or iteration order.
                var owners = topic switch
                {
                    "flight plan" or "route sequencing" or "departure procedures" or "arrival procedures"
                        => (System: "systems.author", High: "software.author", Low: "software.author"),
                    "guidance" or "lateral navigation" or "vertical navigation" or "performance prediction"
                        => (System: "systems.reviewer", High: "lead.reviewer", Low: "software.lead"),
                    "navigation database" or "radio navigation" or "position estimation"
                        => (System: "systems.author", High: "software.lead", Low: "software.author"),
                    "fuel management" or "crew interface" or "approach management" or "airspace constraints"
                        => (System: "systems.author", High: "software.author", Low: "lead.reviewer"),
                    _ => throw new InvalidOperationException($"No engineering owner is configured for {topic}.")
                };
                var author = system ? owners.System : level == RequirementLevel.HighLevel ? owners.High : owners.Low;
                var number = await IdentifierAllocator.NextChangeRequestAsync(db, type, softwareLevel, ct, policy);
                var criterion = scenario.Criterion;
                var request = new SystemChangeRequest(number, 0, project.Id, release.Id,
                    $"{topic}: {scenario.Name} ({level})",
                    $"The FMS 1.6 {topic} refinement needs an explicit {scenario.Name} boundary.",
                    $"Compare against exact effective {member.BaseNumber}.{member.Revision:D2} from baseline {baselineId:D}; preserve that definition while authoring this proposal.",
                    criterion, author, at, type, softwareLevel: softwareLevel, ladderPolicy: policy);
                request.AddRequirementChange(author, member.BaseNumber, member.Revision + 1, level,
                    RequirementChangeKind.Modify, member.Statement + " " + criterion,
                    $"Make the {topic} {scenario.Name} acceptance boundary observable and testable.", "Test", at,
                    impactDispositionJson: RequirementAuthoringJson.PendingImpactDispositions,
                    proposedUpstreamRevisionIdsJson: parentRequirement is { } parentId
                        ? JsonSerializer.Serialize(new[] { parentId }) : "[]", ladderPolicy: policy);
                if (parent is not null)
                    request.AddUpstreamLink(author, parent.Id, parent.DisplayNumber, release.Id, release.Version,
                        $"This {level} proposal develops the same {topic} {scenario.Name} change at the next configured level.", at);
                if (index == 1 && level == RequirementLevel.HighLevel)
                    parallelReviewRequest = request;
                db.SystemChangeRequests.Add(request);
                // The campaign predates these authoring scenarios. Register their real pending work;
                // never let the older campaign snapshot imply these new impacts were dispositioned.
                db.ImpactDispositions.AddRange(
                    new ChangeImpactDisposition(campaign.Id, request.Id, ImpactKind.Requirement,
                        request.RequirementChanges.Single().DisplayNumber, "Review the proposed requirement revision and its allocation."),
                    new ChangeImpactDisposition(campaign.Id, request.Id, ImpactKind.Traceability,
                        request.DisplayNumber, "Review the upstream and downstream links affected by this proposal."),
                    new ChangeImpactDisposition(campaign.Id, request.Id, ImpactKind.Verification,
                        request.DisplayNumber, "Review affected verification and required execution on the selected 1.6 build."),
                    new ChangeImpactDisposition(campaign.Id, request.Id, ImpactKind.Document,
                        request.DisplayNumber, "Review the controlled outputs affected by this proposal."));
                db.ShowcaseUpgradeSteps.Add(new ShowcaseUpgradeStep(programId, key, request.Id.ToString("D"), at));
                db.ShowcaseUpgradeSteps.Add(new ShowcaseUpgradeStep(programId,
                    $"{ActiveTraceProposalPrefix}{index:D2}/{level}",
                    JsonSerializer.Serialize(new ActiveTraceProposalIdentity(request.Id, baselineId, member.Id, parentRequirement)), at));
                parent = request;
                parentRequirement = member.Id;
                added++;
            }
        }
        await db.SaveChangesAsync(ct);
        if (parallelReviewRequest is not null)
        {
            // PostgreSQL protects authored upstream history with the persisted Draft state. Store
            // the draft first, then cross the review boundary inside the same upgrade transaction.
            await SubmitActiveTraceParallelReviewAsync(parallelReviewRequest, programId, policy, at, ct);
            await db.SaveChangesAsync(ct);
        }
        return $"Added {added} current-build authoring requests: {ActiveTraceScenarioChains} named System/HLR/LLR chains, including one live parallel review. The existing named incomplete scenarios remain visible; released history is unchanged.";
    }

    private async Task SubmitActiveTraceParallelReviewAsync(SystemChangeRequest request, Guid programId,
        ILadderPolicy policy, DateTimeOffset at, CancellationToken ct)
    {
        // HOME has a configured System sequence, which must not be bypassed. This named software
        // scenario uses the existing unconfigured-workflow contract: independently eligible Approvers.
        var subject = policy.WorkflowSubject(request.Type);
        if (await db.ReviewWorkflows.AnyAsync(x => x.ProjectId == request.ProjectId && x.AppliesTo == subject
            && x.State == ReviewWorkflowState.Active, ct))
            throw new InvalidOperationException("The named FMS parallel software review cannot replace a configured approval workflow. Qualify that customized showcase separately.");
        var number = request.RequirementChanges.Single().BaseNumber;
        var claimed = await (from change in db.RequirementChanges.AsNoTracking()
            join other in db.SystemChangeRequests.AsNoTracking() on change.ChangeRequestId equals other.Id
            where other.ProjectId == request.ProjectId && other.Id != request.Id && change.BaseNumber == number
                && (change.Kind == RequirementChangeKind.Modify || change.Kind == RequirementChangeKind.Retire)
                && (other.State == ChangeRequestState.InReview || other.State == ChangeRequestState.Approved
                    || other.State == ChangeRequestState.SelectedForBaseline)
            select other.Id).AnyAsync(ct);
        if (claimed) throw new InvalidOperationException($"The named parallel review cannot claim {number}; another controlled change already holds it.");
        var approvers = new List<ApproverSelection>();
        var authority = new ProjectAuthorityResolver(db);
        // Older HOME carries direct Approver grants; fresh showcases use governed leadership.
        // Resolve the same demand as normal submission, preserving the actual source row and never
        // manufacturing a grant or falling back to an administrator to fill the review.
        foreach (var userName in new[] { "lead.reviewer", "manager.reviewer", "systems.lead", "software.lead" })
        {
            var account = await db.UserAccounts.AsNoTracking().SingleOrDefaultAsync(x => x.UserName == userName
                && x.State == AccountState.Active, ct);
            if (account is null || account.UserName == request.AuthorId) continue;
            var decision = await authority.ResolveAsync(account.Id, programId,
                ProjectAuthorityRequirement.LegacyRoleDemand(ProgramRole.Approver), at, ct);
            if (!decision.Granted || decision.Source is ProjectAuthoritySource.AdministratorSubstitution or ProjectAuthoritySource.None) continue;
            approvers.Add(new(account.UserName, account.DisplayName, ProgramRole.Approver,
                decision.Source, decision.SourceId));
            if (approvers.Count == 2) break;
        }
        if (approvers.Count != 2) throw new InvalidOperationException("The named parallel review requires two independently eligible current Approvers.");
        var vocabulary = await new ProjectVerificationVocabularyService(db, resolver)
            .ResolveForSubmissionAsync(request.ProjectId, request.AuthorId, "showcase-upgrade", at, ct);
        request.SubmitForReviewWithResolvedTrace(request.AuthorId, approvers, at, ReviewMode.Parallel,
            ladderPolicy: policy, verificationPolicy: vocabulary, traceEvidence: new(false, []));
    }
}
