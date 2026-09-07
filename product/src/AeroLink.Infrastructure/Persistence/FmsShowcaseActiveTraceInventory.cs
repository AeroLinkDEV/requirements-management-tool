using System.Text.Json;
using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Hierarchy;
using AeroLink.Domain.Traceability;
using AeroLink.Domain.Verification;
using Microsoft.EntityFrameworkCore;

namespace AeroLink.Infrastructure.Persistence;

public sealed record ShowcaseActiveTraceRow(Guid Id, string Identifier, string NativeState,
    string Upstream, string Downstream, string Overall, bool NamedNegative, IReadOnlyList<string> Warnings);
public sealed record ShowcaseTraceGap(Guid Id, string Identifier, IReadOnlyList<string> Warnings, bool NamedNegative);
public sealed record ShowcaseTracePopulation(string Family, int Total, IReadOnlyList<ShowcaseTraceGap> Gaps);
public sealed record ShowcaseActiveTraceInventory(Guid ProjectId, Guid BuildId, int CurrentChanges,
    int IncompleteChanges, int EligibleArtifacts, int IncompleteArtifacts, double IncompletePercent,
    bool Holds, string Scope, bool WaitingForMaterialization, IReadOnlyList<ShowcaseTracePopulation> Populations,
    IReadOnlyList<ShowcaseActiveTraceRow> Records, IReadOnlyList<string> Problems);

public sealed partial class FmsShowcaseSeeder
{
    private async Task<List<SystemChangeRequest>> CurrentActiveTraceRequestsAsync(Guid projectId, Guid releaseId,
        ILadderPolicy policy, CancellationToken ct)
    {
        var all = await db.SystemChangeRequests.AsNoTracking().Include(x => x.RequirementChanges)
            .Where(x => x.ProjectId == projectId && x.TargetReleaseId == releaseId).ToListAsync(ct);
        return all.GroupBy(x => x.BaseNumber, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(x => x.Revision).First())
            .Where(x => x.State != ChangeRequestState.Withdrawn && (x.Type == ChangeRequestType.System
                ? policy.OrderedLevels.Contains(RequirementLevel.System)
                : x.Type == ChangeRequestType.Software && x.SoftwareLevel is { } level && policy.OrderedLevels.Contains(level)))
            .OrderBy(x => x.DisplayNumber, StringComparer.Ordinal).ToList();
    }

    public async Task<ShowcaseActiveTraceInventory> ActiveTraceInventoryAsync(Guid programId, CancellationToken ct = default)
    {
        var projectId = await db.Projects.Where(x => x.ProgramId == programId).Select(x => x.Id).SingleAsync(ct);
        var releaseId = await db.Releases.Where(x => x.ProjectId == projectId && x.Version == "1.6").Select(x => x.Id).SingleAsync(ct);
        var policy = await resolver.ResolveAsync(projectId, ct);
        var requests = await CurrentActiveTraceRequestsAsync(projectId, releaseId, policy, ct);
        var currentIds = requests.Select(x => x.Id).ToHashSet();
        var states = await ChangeRequestTraceProjection.StatesAsync(db, projectId, currentIds, policy, ct);
        var markers = await db.ShowcaseUpgradeSteps.AsNoTracking()
            .Where(x => x.ProgramId == programId && x.StepKey.StartsWith(ActiveTraceScenarioPrefix)).ToListAsync(ct);
        var problems = new List<string>();
        var workflowGaps = await ValidateWorkflowScenariosAsync(programId, projectId, releaseId, problems, ct);
        bool NamedGap(SystemChangeRequest request) => IsNamedChangeGap(request, states[request.Id])
            || (workflowGaps.Contains(request.Id) && states[request.Id].Upstream == "Root"
                && states[request.Id].Downstream == "ApprovalPending");
        var positive = new Dictionary<string, Guid>(StringComparer.Ordinal);
        foreach (var marker in markers)
            if (!Guid.TryParse(marker.Detail, out var id)) problems.Add($"Invalid scenario identity: {marker.StepKey}.");
            else positive.Add(marker.StepKey, id);
        if (positive.Values.Distinct().Count() != ActiveTraceScenarioChains * 3)
            problems.Add($"Expected {ActiveTraceScenarioChains * 3} distinct connected authoring requests; found {positive.Values.Distinct().Count()}.");
        var missing = positive.Values.Except(currentIds).ToList();
        if (missing.Count > 0) problems.Add("Named scenarios are missing from the exact current build population: " + string.Join(", ", missing));
        if (positive.TryGetValue(ActiveTraceScenarioPrefix + "01/HighLevel", out var parallelId))
        {
            var parallel = await db.SystemChangeRequests.AsNoTracking().Include(x => x.ReviewCycles)
                .ThenInclude(x => x.Steps).SingleOrDefaultAsync(x => x.Id == parallelId, ct);
            var cycle = parallel?.ReviewCycles.Count == 1 ? parallel.ReviewCycles.Single() : null;
            var notices = await db.UserNotifications.AsNoTracking().Where(x => x.ArtifactId == parallelId).ToListAsync(ct);
            if (parallel?.State != ChangeRequestState.InReview || cycle is null
                || cycle.State != ReviewCycleState.Active || cycle.Mode != ReviewMode.Parallel
                || cycle.SnapshotContractVersion != SystemChangeRequest.CurrentSnapshotContractVersion
                || cycle.Steps.Count != 2 || cycle.Steps.Select(x => x.ApproverId).Distinct().Count() != 2
                || cycle.Steps.Any(x => x.State != ApprovalStepState.Active || x.StageKind != ReviewStageKind.Review)
                || notices.Count != 2 || cycle.Steps.Any(step => notices.Count(x => x.Recipient == step.ApproverId
                    && x.ProjectId == projectId && x.Type == "ReviewActivated" && x.Route == $"swcr:{parallelId}") != 1))
                problems.Add("The owned active-trace-913/01/HighLevel parallel review or its two active reviewer notifications has drifted.");
        }
        var positiveIds = positive.Values.ToHashSet();
        var links = await db.ChangeRequestUpstreamLinks.AsNoTracking().Where(x => positiveIds.Contains(x.ChangeRequestId)).ToListAsync(ct);
        var proposalIdentities = await ValidateActiveTraceProposalsAsync(programId, projectId, requests, positive, problems, ct);
        for (var index = 1; index <= ActiveTraceScenarioChains; index++)
        {
            var prefix = $"{ActiveTraceScenarioPrefix}{index:D2}/";
            if (!positive.TryGetValue(prefix + RequirementLevel.System, out var system)
                || !positive.TryGetValue(prefix + RequirementLevel.HighLevel, out var high)
                || !positive.TryGetValue(prefix + RequirementLevel.LowLevel, out var low))
            { problems.Add($"Missing System/HLR/LLR ownership map for scenario {index:D2}."); continue; }
            if (links.Any(x => x.ChangeRequestId == system)
                || links.Count(x => x.ChangeRequestId == high) != 1
                || !links.Any(x => x.ChangeRequestId == high && x.UpstreamChangeRequestId == system)
                || links.Count(x => x.ChangeRequestId == low) != 1
                || !links.Any(x => x.ChangeRequestId == low && x.UpstreamChangeRequestId == high))
                problems.Add($"The exact System/HLR/LLR links of scenario {index:D2} have drifted.");
            if (!proposalIdentities.TryGetValue(system, out var systemProposal)
                || !proposalIdentities.TryGetValue(high, out var highProposal)
                || !proposalIdentities.TryGetValue(low, out var lowProposal)
                || systemProposal.UpstreamRevisionId is not null
                || highProposal.UpstreamRevisionId != systemProposal.RequirementRevisionId
                || lowProposal.UpstreamRevisionId != highProposal.RequirementRevisionId
                || systemProposal.BaselineId != highProposal.BaselineId || highProposal.BaselineId != lowProposal.BaselineId)
                problems.Add($"The exact requirement proposal chain of scenario {index:D2} has drifted.");
        }
        foreach (var request in requests.Where(x => positiveIds.Contains(x.Id) && x.RequirementChanges.Count != 1))
            problems.Add($"The exact proposal of {request.DisplayNumber} has drifted.");
        var changeGaps = requests.Where(x => states[x.Id].Overall == "ActionRequired")
            .Select(x => new ShowcaseTraceGap(x.Id, x.DisplayNumber, states[x.Id].Warnings, NamedGap(x))).ToList();
        var populations = new List<ShowcaseTracePopulation> { new("Current change requests", requests.Count, changeGaps) };
        var baseline = await db.CandidateBaselines.AsNoTracking().SingleOrDefaultAsync(x => x.ReleaseId == releaseId, ct);
        var waiting = baseline?.RequirementsMaterializedAt is null || baseline.TestProceduresMaterializedAt is null;
        if (!waiting)
            await AddMaterializedTracePopulationsAsync(programId, projectId, releaseId, baseline!.Id, policy, populations, problems, ct);
        else
        {
            populations.Add(new("Exact active-baseline requirements (waiting for materialization)", 0, []));
            populations.Add(new("Exact software Case-to-Procedure obligations (waiting for materialization)", 0, []));
        }
        foreach (var population in populations)
            foreach (var gap in population.Gaps.Where(x => !x.NamedNegative))
                problems.Add($"Unplanned {population.Family} gap: {gap.Identifier}: {string.Join(" ", gap.Warnings)}");
        var total = populations.Sum(x => x.Total);
        var gaps = populations.Sum(x => x.Gaps.Count);
        var percentage = total == 0 ? 0 : Math.Round(gaps * 100d / total, 2);
        if (percentage is < 5 or > 10) problems.Add($"Incomplete active trace share is {percentage}% ({gaps}/{total}); expected 5–10%.");
        return new(projectId, releaseId, requests.Count, changeGaps.Count, total, gaps, percentage, problems.Count == 0,
            "Native completeness populations in exact FMS Build 1.6: current on-ladder CR revisions (including operator work, one per controlled number, excluding withdrawn current revisions), this build's own materialized requirement revisions, and its exact software Case-to-Procedure obligations. Each artifact is counted once within its distinct family; a requirement with two gaps is one incomplete requirement. Pending assessments are incomplete native CR states. No released predecessor is substituted. Other artifact-family relationships, execution evidence and release readiness remain separately inventoried and do not inflate this denominator.",
            waiting, populations, requests.Select(x => new ShowcaseActiveTraceRow(x.Id, x.DisplayNumber, x.State.ToString(), states[x.Id].Upstream,
                states[x.Id].Downstream, states[x.Id].Overall, NamedGap(x), states[x.Id].Warnings)).ToList(), problems);
    }

    private async Task<Dictionary<Guid, ActiveTraceProposalIdentity>> ValidateActiveTraceProposalsAsync(Guid programId,
        Guid projectId, IReadOnlyCollection<SystemChangeRequest> requests, Dictionary<string, Guid> positive,
        List<string> problems, CancellationToken ct)
    {
        var markers = await db.ShowcaseUpgradeSteps.AsNoTracking().Where(x => x.ProgramId == programId
            && x.StepKey.StartsWith(ActiveTraceProposalPrefix)).ToListAsync(ct);
        var identities = new Dictionary<Guid, ActiveTraceProposalIdentity>();
        foreach (var marker in markers)
        {
            ActiveTraceProposalIdentity? identity;
            try { identity = JsonSerializer.Deserialize<ActiveTraceProposalIdentity>(marker.Detail ?? "null"); }
            catch (JsonException) { identity = null; }
            var key = ActiveTraceScenarioPrefix + marker.StepKey[ActiveTraceProposalPrefix.Length..];
            if (identity is null || !positive.TryGetValue(key, out var requestId) || identity.RequestId != requestId
                || !identities.TryAdd(requestId, identity))
                problems.Add($"Invalid exact proposal identity: {marker.StepKey}.");
        }
        if (identities.Count != ActiveTraceRequestCount)
            problems.Add($"Expected {ActiveTraceRequestCount} exact proposal identities; found {identities.Count}.");
        var baselineIds = identities.Values.Select(x => x.BaselineId).Distinct().ToList();
        var members = await (from member in db.BaselineRequirements.AsNoTracking()
            join revision in db.RequirementRevisions.AsNoTracking() on member.RevisionId equals revision.Id
            join artifact in db.Requirements.AsNoTracking() on revision.ArtifactId equals artifact.Id
            where baselineIds.Contains(member.BaselineId) && artifact.ProjectId == projectId
            select new { member.BaselineId, revision.Id, artifact.BaseNumber, revision.Revision, artifact.Level }).ToListAsync(ct);
        var exactMembers = members.ToDictionary(x => (x.BaselineId, x.Id));
        var memberIds = members.Select(x => x.Id).ToList();
        var traces = await db.RequirementTraces.AsNoTracking().Where(x => x.ProjectId == projectId
            && memberIds.Contains(x.SourceRevisionId) && memberIds.Contains(x.TargetRevisionId)).ToListAsync(ct);
        foreach (var request in requests.Where(x => identities.ContainsKey(x.Id)))
        {
            var identity = identities[request.Id];
            var proposal = request.RequirementChanges.Count == 1 ? request.RequirementChanges.Single() : null;
            Guid[] upstream;
            try { upstream = JsonSerializer.Deserialize<Guid[]>(proposal?.ProposedUpstreamRevisionIdsJson ?? "[]") ?? []; }
            catch (JsonException) { upstream = [Guid.Empty]; }
            Guid[] expectedUpstream = identity.UpstreamRevisionId is { } parent ? [parent] : [];
            if (!exactMembers.TryGetValue((identity.BaselineId, identity.RequirementRevisionId), out var member)
                || proposal is null || proposal.BaseNumber != member.BaseNumber || proposal.Revision != member.Revision + 1
                || proposal.Level != member.Level || proposal.Kind != RequirementChangeKind.Modify
                || request.Type != (member.Level == RequirementLevel.System ? ChangeRequestType.System : ChangeRequestType.Software)
                || request.SoftwareLevel != (member.Level == RequirementLevel.System ? (RequirementLevel?)null : member.Level)
                || !upstream.SequenceEqual(expectedUpstream)
                || (identity.UpstreamRevisionId is { } parentId
                    && (!exactMembers.ContainsKey((identity.BaselineId, parentId))
                        || !traces.Any(x => x.SourceRevisionId == identity.RequirementRevisionId && x.TargetRevisionId == parentId))))
                problems.Add($"The exact proposal of {request.DisplayNumber} has drifted from its recorded source baseline/revisions.");
        }
        return identities;
    }

    private async Task AddMaterializedTracePopulationsAsync(Guid programId, Guid projectId, Guid releaseId, Guid baselineId,
        ILadderPolicy policy, List<ShowcaseTracePopulation> populations, List<string> problems, CancellationToken ct)
    {
        var configuredLevels = policy.OrderedLevels.ToArray();
        var members = await (from member in db.BaselineRequirements.AsNoTracking()
            join revision in db.RequirementRevisions.AsNoTracking() on member.RevisionId equals revision.Id
            join artifact in db.Requirements.AsNoTracking() on member.ArtifactId equals artifact.Id
            where member.BaselineId == baselineId && artifact.ProjectId == projectId && configuredLevels.Contains(artifact.Level)
            select new { revision.Id, artifact.BaseNumber, revision.Revision, artifact.Level }).ToListAsync(ct);
        var memberIds = members.Select(x => x.Id).ToList();
        var manifest = await TestProcedureEffectivity.ForBaselineAsync(db, baselineId, ct);
        if (manifest?.IsExactManifest != true)
        {
            problems.Add("The active build has no exact verification manifest; inferred verification populations were not scored.");
            return;
        }
        IReadOnlyCollection<Guid> coverageIds = manifest is null ? [] : await CoveragePopulationAsync(manifest, policy, ct);
        var coverage = await VerificationCoverageProjection.StatesAsync(db, memberIds, ct, coverageIds, buildScoped: false);
        // Same exact source/target membership test as the release-readiness upstream gate.
        var traced = await db.RequirementTraces.AsNoTracking().Where(x => x.ProjectId == projectId
            && memberIds.Contains(x.SourceRevisionId) && memberIds.Contains(x.TargetRevisionId))
            .Select(x => x.SourceRevisionId).Distinct().ToListAsync(ct);
        var suspectSources = await (from link in db.RequirementTraces.AsNoTracking()
            join lifecycle in db.ExactLinkSuspectLifecycles.AsNoTracking()
                on new { LinkKind = ExactLinkKind.RequirementTrace, LinkId = link.Id }
                equals new { lifecycle.LinkKind, lifecycle.LinkId }
            where link.ProjectId == projectId && memberIds.Contains(link.SourceRevisionId)
                && memberIds.Contains(link.TargetRevisionId) && lifecycle.State != ExactLinkLifecycleState.Closed
            select link.SourceRevisionId).Distinct().ToListAsync(ct);
        var requirementGaps = new List<ShowcaseTraceGap>();
        foreach (var member in members)
        {
            var warnings = new List<string>();
            if (policy.Definition(member.Level).Verification is not null && coverage[member.Id] != RequirementCoverageState.Covered)
                warnings.Add(coverage[member.Id]);
            if (policy.ParentLevels(member.Level).Count > 0 && !traced.Contains(member.Id)) warnings.Add("UpstreamGap");
            if (suspectSources.Contains(member.Id)) warnings.Add("SuspectUpstream");
            if (warnings.Count == 0) continue;
            var number = $"{member.BaseNumber}.{member.Revision:D2}";
            var named = HomeRequirementGaps.TryGetValue(member.Id, out var expected)
                && number == expected.Number && warnings.Count == 1 && warnings[0] == expected.Warning;
            requirementGaps.Add(new(member.Id, number, warnings, named));
        }
        populations.Add(new("Exact active-baseline requirements", members.Count, requirementGaps));
        var levels = policy.OrderedLevels.Where(level => policy.Definition(level).VerificationProfile?.Enables(VerificationArtifactKind.Case) == true
            && policy.Definition(level).VerificationProfile?.Enables(VerificationArtifactKind.Procedure) == true)
            .Select(policy.ProcedureLevel).ToHashSet();
        var campaign = await db.ReleaseCampaigns.AsNoTracking().SingleOrDefaultAsync(x => x.ReleaseId == releaseId && x.BaselineId == baselineId, ct);
        var obligations = await CaseProcedureSatisfaction.ForBaselineAsync(db, baselineId, releaseId, campaign?.SoftwareBuildId, levels, ct);
        var latestResults = await ExecutionScope.LatestByProcedureAsync(db,
            obligations.SelectMany(x => x.RequiredProcedureRevisionIds).Distinct().ToList(), releaseId, campaign?.SoftwareBuildId, ct);
        var caseIds = obligations.Select(x => x.CaseRevisionId).ToList();
        var cases = await (from revision in db.TestProcedureRevisions.AsNoTracking()
            join artifact in db.TestProcedures.AsNoTracking() on revision.ProcedureId equals artifact.Id
            where caseIds.Contains(revision.Id) select new { revision.Id, artifact.BaseNumber, revision.Revision }).ToListAsync(ct);
        var names = cases.ToDictionary(x => x.Id, x => $"{x.BaseNumber}.{x.Revision:D2}");
        var waitPrefix = ActiveVerificationPrefix + "waiting/";
        var verificationMarkers = await db.ShowcaseUpgradeSteps.AsNoTracking().Where(x => x.ProgramId == programId && x.StepKey.StartsWith(ActiveVerificationPrefix)).ToListAsync(ct);
        var waitingMarkers = verificationMarkers.Where(x => x.StepKey.StartsWith(waitPrefix, StringComparison.Ordinal)).ToList();
        var waitingIds = waitingMarkers.Select(x => Guid.TryParse(x.StepKey[waitPrefix.Length..], out var id) ? id : Guid.Empty).ToHashSet();
        var caseMarkers = verificationMarkers.Where(x => x.StepKey.StartsWith(waitPrefix, StringComparison.Ordinal)
            || x.StepKey.StartsWith(ActiveVerificationPrefix + "expected/", StringComparison.Ordinal)).ToList();
        if (caseMarkers.Count != caseIds.Count) problems.Add($"Exact Case ownership population drifted: {caseMarkers.Count} recorded versus {caseIds.Count} effective Cases.");
        foreach (var marker in caseMarkers)
            if (!Guid.TryParse(marker.StepKey[(marker.StepKey.LastIndexOf('/') + 1)..], out var id)
                || !names.TryGetValue(id, out var number) || marker.Detail != number)
                problems.Add($"An exact Case scenario is absent or mismatched: {marker.Detail} ({marker.StepKey}).");
        var selected = await (from entry in db.BuildTestSetEntries.AsNoTracking()
            join set in db.BuildTestSets.AsNoTracking() on entry.BuildTestSetId equals set.Id
            join revision in db.TestProcedureRevisions.AsNoTracking() on entry.ProcedureRevisionId equals revision.Id
            join artifact in db.TestProcedures.AsNoTracking() on revision.ProcedureId equals artifact.Id
            where set.ProjectId == projectId && set.ReleaseId == releaseId
                && ((artifact.Level == TestProcedureLevel.HighLevel && set.Discipline == TestChangeReviewDiscipline.HighLevelSoftware)
                    || (artifact.Level == TestProcedureLevel.LowLevel && set.Discipline == TestChangeReviewDiscipline.LowLevelSoftware))
            select entry.ProcedureRevisionId).ToListAsync(ct);
        var caseGaps = obligations.Where(x => !x.Satisfied).Select(x => new ShowcaseTraceGap(x.CaseRevisionId, names[x.CaseRevisionId],
            [x.HasSuspectLink ? "Suspect Case-to-Procedure link" : x.RequiredProcedureRevisionIds.Count == 0 ? "No required Procedure" : "No complete selected, effective, build-scoped Pass"],
            waitingIds.Contains(x.CaseRevisionId) && !x.HasSuspectLink && x.RequiredProcedureRevisionIds.Count > 0
            && x.RequiredProcedureRevisionIds.All(id => !latestResults.ContainsKey(id))
            && manifest?.IsExactManifest == true && x.RequiredProcedureRevisionIds.All(id => manifest.RevisionIds.Contains(id) && selected.Contains(id)))).ToList();
        populations.Add(new("Exact software Case-to-Procedure obligations", obligations.Count, caseGaps));
        var resultPrefix = ActiveVerificationPrefix + "result/";
        var resultMarkers = verificationMarkers.Where(x => x.StepKey.StartsWith(resultPrefix, StringComparison.Ordinal)).ToList();
        var resultIds = resultMarkers.Select(x => Guid.TryParse(x.Detail, out var id) ? id : Guid.Empty).ToList();
        var executions = await db.TestExecutions.AsNoTracking().Where(x => resultIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, ct);
        var evidenced = await (from link in db.TestExecutionEvidence.AsNoTracking()
            join evidence in db.EvidenceRecords.AsNoTracking() on link.EvidenceId equals evidence.Id
            where resultIds.Contains(link.TestExecutionId) && evidence.ProjectId == projectId
            select link.TestExecutionId).Distinct().ToListAsync(ct);
        if (evidenceStore is not null)
        {
            var ownedEvidence = await (from link in db.TestExecutionEvidence.AsNoTracking()
                join evidence in db.EvidenceRecords.AsNoTracking() on link.EvidenceId equals evidence.Id
                where resultIds.Contains(link.TestExecutionId) && evidence.ProjectId == projectId
                select evidence).Distinct().ToListAsync(ct);
            foreach (var evidence in ownedEvidence)
            {
                try
                {
                    await using var verified = await evidenceStore.OpenVerifiedReadAsync(evidence.StorageKey,
                        evidence.Size, evidence.Sha256, ct);
                }
                catch (EvidenceIntegrityException ex)
                {
                    problems.Add($"Owned synthetic evidence {evidence.Id:D} is unavailable or invalid ({ex.Code}); complete its supported storage recovery before accepting the showcase.");
                }
            }
        }
        foreach (var marker in resultMarkers)
            if (!Guid.TryParse(marker.Detail, out var resultId) || !executions.TryGetValue(resultId, out var execution)
                || !Guid.TryParse(marker.StepKey[resultPrefix.Length..], out var procedureId) || execution.ProcedureRevisionId != procedureId
                || execution.ProjectId != projectId || execution.ReleaseId != releaseId
                || !execution.Determination.Contains("Synthetic demonstration", StringComparison.Ordinal) || !evidenced.Contains(resultId))
                problems.Add($"Owned synthetic result or its evidence link is absent/mismatched: {marker.StepKey}.");
    }
}
