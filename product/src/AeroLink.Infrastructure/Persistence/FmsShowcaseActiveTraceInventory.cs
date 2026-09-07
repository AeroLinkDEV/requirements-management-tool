using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Hierarchy;
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
        var positive = new Dictionary<string, Guid>(StringComparer.Ordinal);
        foreach (var marker in markers)
            if (!Guid.TryParse(marker.Detail, out var id)) problems.Add($"Invalid scenario identity: {marker.StepKey}.");
            else positive.Add(marker.StepKey, id);
        if (positive.Values.Distinct().Count() != ActiveTraceScenarioChains * 3)
            problems.Add($"Expected {ActiveTraceScenarioChains * 3} distinct connected authoring drafts; found {positive.Values.Distinct().Count()}.");
        var missing = positive.Values.Except(currentIds).ToList();
        if (missing.Count > 0) problems.Add("Named scenarios are missing from the exact current build population: " + string.Join(", ", missing));
        var positiveIds = positive.Values.ToHashSet();
        var links = await db.ChangeRequestUpstreamLinks.AsNoTracking().Where(x => positiveIds.Contains(x.ChangeRequestId)).ToListAsync(ct);
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
        }
        foreach (var request in requests.Where(x => positiveIds.Contains(x.Id) && x.RequirementChanges.Count != 1))
            problems.Add($"The exact proposal of {request.DisplayNumber} has drifted.");
        var changeGaps = requests.Where(x => states[x.Id].Overall == "ActionRequired")
            .Select(x => new ShowcaseTraceGap(x.Id, x.DisplayNumber, states[x.Id].Warnings, IsNamedChangeGap(x, states[x.Id]))).ToList();
        var populations = new List<ShowcaseTracePopulation> { new("Current change requests", requests.Count, changeGaps) };
        var baseline = await db.CandidateBaselines.AsNoTracking().SingleOrDefaultAsync(x => x.ReleaseId == releaseId, ct);
        var waiting = baseline?.RequirementsMaterializedAt is null;
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
                states[x.Id].Downstream, states[x.Id].Overall, IsNamedChangeGap(x, states[x.Id]), states[x.Id].Warnings)).ToList(), problems);
    }

    private async Task AddMaterializedTracePopulationsAsync(Guid programId, Guid projectId, Guid releaseId, Guid baselineId,
        ILadderPolicy policy, List<ShowcaseTracePopulation> populations, List<string> problems, CancellationToken ct)
    {
        var members = await (from member in db.BaselineRequirements.AsNoTracking()
            join revision in db.RequirementRevisions.AsNoTracking() on member.RevisionId equals revision.Id
            join artifact in db.Requirements.AsNoTracking() on member.ArtifactId equals artifact.Id
            where member.BaselineId == baselineId && artifact.ProjectId == projectId
            select new { revision.Id, artifact.BaseNumber, revision.Revision, artifact.Level }).ToListAsync(ct);
        var memberIds = members.Select(x => x.Id).ToList();
        var manifest = await TestProcedureEffectivity.ForBaselineAsync(db, baselineId, ct);
        IReadOnlyCollection<Guid> coverageIds = manifest is null ? [] : await CoveragePopulationAsync(manifest, policy, ct);
        var coverage = await VerificationCoverageProjection.StatesAsync(db, memberIds, ct, coverageIds, buildScoped: false);
        // Same exact source/target membership test as the release-readiness upstream gate.
        var traced = await db.RequirementTraces.AsNoTracking().Where(x => x.ProjectId == projectId
            && memberIds.Contains(x.SourceRevisionId) && memberIds.Contains(x.TargetRevisionId))
            .Select(x => x.SourceRevisionId).Distinct().ToListAsync(ct);
        var requirementGaps = new List<ShowcaseTraceGap>();
        foreach (var member in members)
        {
            var warnings = new List<string>();
            if (policy.Definition(member.Level).Verification is not null && coverage[member.Id] != RequirementCoverageState.Covered)
                warnings.Add(coverage[member.Id]);
            if (policy.ParentLevels(member.Level).Count > 0 && !traced.Contains(member.Id)) warnings.Add("UpstreamGap");
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
        foreach (var marker in resultMarkers)
            if (!Guid.TryParse(marker.Detail, out var resultId) || !executions.TryGetValue(resultId, out var execution)
                || !Guid.TryParse(marker.StepKey[resultPrefix.Length..], out var procedureId) || execution.ProcedureRevisionId != procedureId
                || execution.ProjectId != projectId || execution.ReleaseId != releaseId
                || !execution.Determination.Contains("Synthetic demonstration", StringComparison.Ordinal) || !evidenced.Contains(resultId))
                problems.Add($"Owned synthetic result or its evidence link is absent/mismatched: {marker.StepKey}.");
    }
}
