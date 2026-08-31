using AeroLink.Domain.Baselines;
using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Common;
using AeroLink.Domain.Identity;
using AeroLink.Domain.Hierarchy;
using AeroLink.Domain.Programs;
using AeroLink.Domain.Requirements;
using AeroLink.Domain.TeamWork;
using AeroLink.Domain.Verification;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AeroLink.Api;

/// <summary>
/// Read-only, project-wide Team Work projection. This component deliberately materializes each bounded
/// aggregate family before applying the domain policy. It keeps lifecycle truth in Domain while allowing the
/// API to characterize query count, materialization, and payload size against a real provider later.
/// </summary>
public sealed class TeamWorkProjectionService(AeroLinkDbContext db, IProjectLadderPolicyResolver? ladderResolver = null)
{
    public async Task<TeamWorkProjectionResponse?> ProjectAsync(Guid projectId, CancellationToken ct)
    {
        var programId = await db.Projects.AsNoTracking()
            .Where(project => project.Id == projectId)
            .Select(project => (Guid?)project.ProgramId)
            .SingleOrDefaultAsync(ct);
        if (programId is null) return null;

        // Team Work consumes the same effective ladder policy as the rest of the product. Older isolated
        // fixtures may predate persisted ladder rows, so leave the policy absent in that compatibility case;
        // observed controlled records still provide an honest fallback facet rather than inventing levels.
        ILadderPolicy? effectiveLadder = null;
        if (ladderResolver is not null
            && await db.ProjectLadderConfigurations.AsNoTracking().AnyAsync(x => x.ProjectId == projectId, ct))
            effectiveLadder = await ladderResolver.ResolveAsync(projectId, ct);

        // These are intentionally project-wide. A selected build in the browser must not silently narrow the
        // management view to one release, because an item can target a predecessor or successor release.
        var releases = await db.Releases.AsNoTracking()
            .Where(release => release.ProjectId == projectId)
            .ToListAsync(ct);
        var releaseById = releases.ToDictionary(release => release.Id);

        var changeRequests = await db.SystemChangeRequests.AsNoTracking()
            .Where(change => change.ProjectId == projectId)
            .ToListAsync(ct);
        var currentChangeRequests = changeRequests
            .GroupBy(change => change.BaseNumber, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(change => change.Revision).ThenByDescending(change => change.UpdatedAt).First())
            .ToList();

        // Empty BaseNumber is historical/uncontrolled TCR identity. It is loaded out of the projection by
        // query predicate rather than borrowing SourceDisplayNumber as a controlled number.
        var numberedTestChangeReviews = await db.TestChangeReviews.AsNoTracking()
            .Where(review => review.ProjectId == projectId && review.BaseNumber != "")
            .ToListAsync(ct);
        var currentTestChangeReviews = numberedTestChangeReviews
            .Where(review => !string.IsNullOrWhiteSpace(review.BaseNumber))
            .GroupBy(review => review.BaseNumber, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(review => review.Revision).ThenByDescending(review => review.UpdatedAt).First())
            .ToList();

        var problemReports = await db.ProblemReports.AsNoTracking()
            .Where(report => report.ProjectId == projectId)
            .ToListAsync(ct);
        var currentProblemReports = problemReports
            .GroupBy(report => report.ReportNumber, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(report => report.Revision).ThenByDescending(report => report.UpdatedAt).First())
            .ToList();

        var assessments = await db.DownstreamChangeAssessments.AsNoTracking()
            .Where(assessment => assessment.ProjectId == projectId)
            .ToListAsync(ct);

        var changeRequestIds = changeRequests.Select(change => change.Id).ToArray();
        var testChangeReviewIds = numberedTestChangeReviews.Select(review => review.Id).ToArray();
        var changeReviewCycles = changeRequestIds.Length == 0
            ? []
            : await db.ReviewCycles.AsNoTracking()
                .Where(cycle => cycle.ChangeRequestId.HasValue && changeRequestIds.Contains(cycle.ChangeRequestId.Value))
                .ToListAsync(ct);
        var testReviewCycles = testChangeReviewIds.Length == 0
            ? []
            : await db.ReviewCycles.AsNoTracking()
                .Where(cycle => cycle.TestChangeReviewId.HasValue && testChangeReviewIds.Contains(cycle.TestChangeReviewId.Value))
                .ToListAsync(ct);
        var reviewCycles = changeReviewCycles.Concat(testReviewCycles).ToList();
        var cycleIds = reviewCycles.Select(cycle => cycle.Id).ToArray();
        var reviewSteps = cycleIds.Length == 0
            ? []
            : await db.ApprovalSteps.AsNoTracking()
                .Where(step => cycleIds.Contains(step.ReviewCycleId))
                .ToListAsync(ct);
        var stepsByCycle = reviewSteps
            .GroupBy(step => step.ReviewCycleId)
            .ToDictionary(group => group.Key, group => group.OrderBy(step => step.Position).ToArray());

        // A release is authoritative for incorporation, not the selected shell build. Keep all baselines so
        // allocation provenance can still be shown for an unreleased candidate.
        var baselines = await db.CandidateBaselines.AsNoTracking()
            .Where(baseline => baseline.ProjectId == projectId)
            .ToListAsync(ct);
        var baselineById = baselines.ToDictionary(baseline => baseline.Id);
        var baselineIds = baselines.Select(baseline => baseline.Id).ToArray();
        var changeSelections = baselineIds.Length == 0
            ? []
            : await db.BaselineSelections.AsNoTracking()
                .Where(selection => baselineIds.Contains(selection.BaselineId))
                .ToListAsync(ct);
        var testChangeSelections = baselineIds.Length == 0
            ? []
            : await db.BaselineTestChangeSelections.AsNoTracking()
                .Where(selection => baselineIds.Contains(selection.BaselineId))
                .ToListAsync(ct);

        var latestChangeCycles = changeReviewCycles
            .GroupBy(cycle => cycle.ChangeRequestId!.Value)
            .ToDictionary(group => group.Key, group => group.OrderByDescending(cycle => cycle.Sequence).First());
        var latestTestCycles = testReviewCycles
            .GroupBy(cycle => cycle.TestChangeReviewId!.Value)
            .ToDictionary(group => group.Key, group => group.OrderByDescending(cycle => cycle.Sequence).First());

        var items = new List<TeamWorkItemDraft>(
            currentChangeRequests.Count + currentTestChangeReviews.Count + currentProblemReports.Count + assessments.Count);
        foreach (var change in currentChangeRequests)
        {
            var cycle = latestChangeCycles.GetValueOrDefault(change.Id);
            var allSteps = StepsFor(cycle, stepsByCycle);
            var activeSteps = cycle?.State == ReviewCycleState.Active ? allSteps : [];
            _ = TeamWorkReviewOverlay.Resolve(allSteps.Select(TeamWorkReviewStep.From));
            var reviewOverlay = TeamWorkReviewOverlay.Resolve(activeSteps.Select(TeamWorkReviewStep.From));
            var lane = TeamWorkLanePolicy.ForChangeRequest(change.State, change.DeferredFromState);
            var holders = TeamWorkHolderPolicy.ForChangeRequest(change.State, change.AuthorId, activeSteps
                .Select(TeamWorkReviewStep.From));

            // A deferred InReview item has a cancelled but frozen cycle. Its remembered lane still comes from
            // those persisted stage kinds, while deferral itself always removes current-holder obligations.
            if (change.State == ChangeRequestState.Deferred && change.DeferredFromState == ChangeRequestState.InReview)
            {
                var overlay = TeamWorkReviewOverlay.Resolve(allSteps.Select(TeamWorkReviewStep.From));
                lane = TeamWorkLaneDecision.OnBoard(overlay.LaneDecision.Lane!.Value, isDeferred: true);
            }
            else if (change.State == ChangeRequestState.InReview)
            {
                lane = reviewOverlay.LaneDecision;
            }

            if (change.State == ChangeRequestState.SelectedForBaseline
                && releaseById.TryGetValue(change.TargetReleaseId, out var targetRelease)
                && targetRelease.IsReleased)
                lane = TeamWorkLaneDecision.OffBoard;

            var allocation = LatestAllocation(
                changeSelections.Where(selection => selection.ChangeRequestId == change.Id)
                    .Select(selection => baselineById.GetValueOrDefault(selection.BaselineId)), releaseById);
            if (lane.IsOffBoard) continue;
            items.Add(new TeamWorkItemDraft(
                change.Id,
                WireFamily(change.Type),
                ChangeRequestLayer(change),
                ArtifactTypeForNumber(change.BaseNumber) ?? WireFamily(change.Type).ToUpperInvariant(),
                ChangeRequestCategory(change),
                Prefix(change.BaseNumber),
                change.DisplayNumber,
                change.Title,
                WireLane(lane.Lane!.Value),
                change.State.ToString(),
                null,
                CanonicalHolderIds(holders.CurrentHolderIds),
                WireHolderBasis(holders.HolderBasis),
                WireStageObligations(change.State == ChangeRequestState.InReview ? reviewOverlay.ActiveStageObligations : []),
                CanonicalUserName(change.AuthorId),
                string.IsNullOrWhiteSpace(change.AuthorId) ? null : "author",
                ReleaseFor(change.TargetReleaseId, releaseById),
                lane.IsDeferred,
                allocation,
                change.DeferredFromState?.ToString(),
                change.UpdatedAt,
                $"/open/change-request/{change.Id}"));
        }

        foreach (var review in currentTestChangeReviews)
        {
            var cycle = latestTestCycles.GetValueOrDefault(review.Id);
            var allSteps = StepsFor(cycle, stepsByCycle);
            var activeSteps = cycle?.State == ReviewCycleState.Active ? allSteps : [];
            _ = TeamWorkReviewOverlay.Resolve(allSteps.Select(TeamWorkReviewStep.From));
            var reviewOverlay = TeamWorkReviewOverlay.Resolve(activeSteps.Select(TeamWorkReviewStep.From));
            var lane = TeamWorkLanePolicy.ForTestChangeReview(review.State, review.DeferredFromState);
            var holders = TeamWorkHolderPolicy.ForTestChangeReview(review.State, review.AssignedEngineerId,
                activeSteps.Select(TeamWorkReviewStep.From));
            if (review.State == TestChangeReviewState.Deferred && review.DeferredFromState == TestChangeReviewState.InReview)
            {
                var overlay = TeamWorkReviewOverlay.Resolve(allSteps.Select(TeamWorkReviewStep.From));
                lane = TeamWorkLaneDecision.OnBoard(overlay.LaneDecision.Lane!.Value, isDeferred: true);
            }
            else if (review.State == TestChangeReviewState.InReview)
            {
                lane = reviewOverlay.LaneDecision;
            }

            var allocation = LatestAllocation(
                testChangeSelections.Where(selection => selection.TestChangeRequestId == review.Id)
                    .Select(selection => baselineById.GetValueOrDefault(selection.BaselineId)), releaseById);
            var incorporated = testChangeSelections
                .Where(selection => selection.TestChangeRequestId == review.Id)
                .Select(selection => baselineById.GetValueOrDefault(selection.BaselineId))
                .Any(baseline => baseline is not null && releaseById.TryGetValue(baseline.ReleaseId, out var release) && release.IsReleased);
            if (incorporated && holders.CurrentHolderIds.Count == 0) lane = TeamWorkLaneDecision.OffBoard;
            if (lane.IsOffBoard) continue;

            var raisedById = CanonicalUserName(review.AuthorId);
            var raisedByKind = string.IsNullOrWhiteSpace(review.AuthorId)
                ? WireOriginKind(review.OriginKind)
                : "author";
            items.Add(new TeamWorkItemDraft(
                review.Id,
                "verification",
                VerificationLayer(review.Discipline),
                ArtifactTypeForNumber(review.BaseNumber) ?? "Verification",
                TestChangeReviewCategory(review),
                Prefix(review.BaseNumber),
                string.IsNullOrWhiteSpace(review.BaseNumber) ? null : review.DisplayNumber,
                review.Title,
                WireLane(lane.Lane!.Value),
                review.State.ToString(),
                WireTestChangeReviewOutcome(review.Outcome),
                CanonicalHolderIds(holders.CurrentHolderIds),
                WireHolderBasis(holders.HolderBasis),
                WireStageObligations(review.State == TestChangeReviewState.InReview ? reviewOverlay.ActiveStageObligations : []),
                raisedById,
                raisedByKind,
                ReleaseFor(review.ReleaseId, releaseById),
                lane.IsDeferred,
                allocation,
                review.DeferredFromState?.ToString(),
                review.UpdatedAt,
                $"/open/test-change-request/{review.Id}"));
        }

        foreach (var report in currentProblemReports)
        {
            var lane = TeamWorkLanePolicy.ForProblemReport(report.State);
            if (lane.IsOffBoard) continue;
            var holders = TeamWorkHolderPolicy.ForProblemReport(report.State, report.ResponsibleEngineerId);
            items.Add(new TeamWorkItemDraft(
                report.Id,
                "problemReport",
                null,
                ArtifactTypeForNumber(report.ReportNumber) ?? "ProblemReport",
                report.Category?.ToString(),
                Prefix(report.ReportNumber),
                report.DisplayNumber,
                report.Title,
                WireLane(lane.Lane!.Value),
                report.State.ToString(),
                report.Disposition?.ToString(),
                CanonicalHolderIds(holders.CurrentHolderIds),
                WireHolderBasis(holders.HolderBasis),
                [],
                CanonicalUserName(report.ReportedBy),
                string.IsNullOrWhiteSpace(report.ReportedBy) ? null : "reportedBy",
                ReleaseFor(report.TargetReleaseId, releaseById),
                report.Disposition == ProblemReportDisposition.Deferred,
                null,
                null,
                report.UpdatedAt,
                $"/open/problem-report/{report.Id}"));
        }

        foreach (var assessment in assessments)
        {
            var lane = TeamWorkLanePolicy.ForAssessment(assessment.State, assessment.Outcome);
            if (lane.IsOffBoard) continue;
            var holders = TeamWorkHolderPolicy.ForAssessment(assessment.State, assessment.Outcome,
                assessment.AssignedEngineerId, assessment.SelectedApproverId);
            items.Add(new TeamWorkItemDraft(
                assessment.Id,
                "assessment",
                AssessmentLayer(assessment.TargetLevel),
                "Assessment",
                AssessmentCategory(assessment.TargetLevel),
                null,
                null,
                $"Assessment of {assessment.SourceChangeRequestNumber}",
                WireLane(lane.Lane!.Value),
                assessment.State.ToString(),
                assessment.Outcome.ToString(),
                CanonicalHolderIds(holders.CurrentHolderIds),
                WireHolderBasis(holders.HolderBasis),
                [],
                assessment.SourceChangeRequestId.ToString("D"),
                "changeRequest",
                ReleaseFor(assessment.ReleaseId, releaseById),
                false,
                null,
                null,
                assessment.UpdatedAt,
                $"/open/downstream-assessment/{assessment.Id}"));
        }

        var orderedItems = items
            .OrderByDescending(item => item.UpdatedAt)
            .ThenBy(item => item.Family, StringComparer.Ordinal)
            .ThenBy(item => item.Id)
            .Select(ToResponseItem)
            .ToList();

        var facets = BuildFacets(orderedItems, effectiveLadder);

        var holderIds = orderedItems.SelectMany(item => item.CurrentHolderIds)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var activeMemberships = await db.ProgramMemberships.AsNoTracking()
            .Where(membership => membership.ProgramId == programId.Value && membership.EndedAt == null)
            .ToListAsync(ct);
        var activeMemberIds = activeMemberships.Select(membership => membership.UserId).Distinct().ToArray();
        var users = activeMemberIds.Length == 0 && holderIds.Length == 0
            ? []
            : await db.UserAccounts.AsNoTracking()
                .Where(user => activeMemberIds.Contains(user.Id)
                    || holderIds.Contains(user.UserName))
                .ToListAsync(ct);
        var usersById = users.ToDictionary(user => user.Id);
        var usersByName = users.ToDictionary(user => user.UserName, StringComparer.OrdinalIgnoreCase);
        var rolesByUserId = activeMemberships
            .GroupBy(membership => membership.UserId)
            .ToDictionary(group => group.Key, group => group.Select(membership => membership.Role).ToArray());

        var peopleByName = new Dictionary<string, TeamWorkPersonAccumulator>(StringComparer.OrdinalIgnoreCase);
        foreach (var memberId in activeMemberIds)
        {
            if (!usersById.TryGetValue(memberId, out var user)) continue;
            peopleByName.TryAdd(user.UserName, new(user.UserName, user.DisplayName, user.Id, true,
                WireAccountState(user.State), ModernBaseRoles(rolesByUserId.GetValueOrDefault(user.Id) ?? [])));
        }
        foreach (var holderId in holderIds)
        {
            if (usersByName.TryGetValue(holderId, out var user))
                peopleByName.TryAdd(user.UserName, new(user.UserName, user.DisplayName, user.Id,
                    activeMemberIds.Contains(user.Id), WireAccountState(user.State),
                    ModernBaseRoles(rolesByUserId.GetValueOrDefault(user.Id) ?? [])));
            else
                peopleByName.TryAdd(holderId, new(holderId, holderId, null, false, null, []));
        }
        foreach (var item in orderedItems)
        foreach (var holderId in item.CurrentHolderIds)
        {
            if (!peopleByName.TryGetValue(holderId, out var person))
            {
                var holder = usersByName.GetValueOrDefault(holderId);
                person = holder is null
                    ? new(holderId, holderId, null, false, null, [])
                    : new(holder.UserName, holder.DisplayName, holder.Id, activeMemberIds.Contains(holder.Id),
                        WireAccountState(holder.State), ModernBaseRoles(rolesByUserId.GetValueOrDefault(holder.Id) ?? []));
                peopleByName[holderId] = person;
            }
            person.Add(item.Lane);
        }

        var responseItems = orderedItems;
        return new TeamWorkProjectionResponse(
            DateTimeOffset.UtcNow,
            new TeamWorkTotals(responseItems.Count, responseItems.Count, responseItems.Count(item => item.CurrentHolderIds.Count == 0)),
            peopleByName.Values
                .OrderBy(person => person.UserName, StringComparer.OrdinalIgnoreCase)
                .Select(person => person.ToResponse())
                .ToList(),
            responseItems,
            facets.Layers,
            facets.ArtifactTypes);
    }

    private static IReadOnlyList<ApprovalStep> StepsFor(ReviewCycle? cycle,
        IReadOnlyDictionary<Guid, ApprovalStep[]> stepsByCycle) =>
        cycle is not null && stepsByCycle.TryGetValue(cycle.Id, out var steps) ? steps : [];

    private static TeamWorkAllocation? LatestAllocation(IEnumerable<CandidateBaseline?> candidates,
        IReadOnlyDictionary<Guid, SoftwareRelease> releaseById) => candidates
        .Where(baseline => baseline is not null)
        .Select(baseline => baseline!)
        .OrderByDescending(baseline => baseline.UpdatedAt)
        .ThenByDescending(baseline => baseline.Revision)
        .Select(baseline => releaseById.TryGetValue(baseline.ReleaseId, out var release)
            ? new TeamWorkAllocation(baseline.Id, release.Id, release.Version, baseline.BaseNumber, baseline.Revision, release.IsReleased)
            : null)
        .FirstOrDefault(allocation => allocation is not null);

    private static TeamWorkRelease? ReleaseFor(Guid? releaseId,
        IReadOnlyDictionary<Guid, SoftwareRelease> releaseById) =>
        releaseId is Guid id && releaseById.TryGetValue(id, out var release)
            ? new TeamWorkRelease(release.Id, release.Version, release.IsReleased)
            : null;

    private static TeamWorkRelease? ReleaseFor(Guid releaseId,
        IReadOnlyDictionary<Guid, SoftwareRelease> releaseById) =>
        releaseById.TryGetValue(releaseId, out var release)
            ? new TeamWorkRelease(release.Id, release.Version, release.IsReleased)
            : null;

    private static TeamWorkItemResponse ToResponseItem(TeamWorkItemDraft item) => new(
        item.Id,
        item.Family,
        item.Layer,
        item.ArtifactType,
        item.Category,
        item.Prefix,
        item.Number,
        item.Title,
        item.Lane,
        item.NativeState,
        item.NativeOutcome,
        item.CurrentHolderIds,
        item.HolderBasis,
        item.ActiveStageObligations,
        item.RaisedById,
        item.RaisedByKind,
        item.Release,
        item.Deferred,
        item.Allocation,
        item.DeferredFromState,
        item.UpdatedAt,
        item.OpenUrl);

    private static IReadOnlyList<TeamWorkStageObligation> WireStageObligations(
        IEnumerable<TeamWorkReviewObligation> obligations) => obligations
        .Select(obligation => new TeamWorkStageObligation(
            CanonicalUserName(obligation.HolderId)!,
            obligation.StageKind switch
            {
                ReviewStageKind.Review => "review",
                ReviewStageKind.Approval => "approval",
                _ => throw new DomainException($"The review stage kind '{obligation.StageKind}' is not supported by Team Work."),
            }))
        .ToArray();

    private static string WireAccountState(AccountState state) => state switch
    {
        AccountState.Active => "active",
        AccountState.Disabled => "disabled",
        AccountState.Locked => "locked",
        _ => throw new DomainException($"The account state '{state}' is not supported by Team Work."),
    };

    private static IReadOnlyList<string> ModernBaseRoles(IEnumerable<ProgramRole> roles) => roles
        .Select(role => role switch
        {
            ProgramRole.SystemEngineer => "SystemEngineer",
            ProgramRole.SoftwareEngineer => "SoftwareEngineer",
            ProgramRole.SystemTestEngineer => "SystemTestEngineer",
            ProgramRole.SoftwareTestEngineer => "SoftwareTestEngineer",
            ProgramRole.ProjectEngineer => "ProjectEngineer",
            ProgramRole.EngineeringManager => "EngineeringManager",
            ProgramRole.ProgramManager => "ProgramManager",
            ProgramRole.ConfigurationManager => "ConfigurationManager",
            ProgramRole.SoftwareQualityAnalyst => "SoftwareQualityAnalyst",
            ProgramRole.Airworthiness => "Airworthiness",
            // These memberships remain readable for compatibility but are not modern base project roles or
            // discipline signals. Reviewer and Approver are workflow signature meanings, while the lead
            // values are now represented by Project Leadership metadata.
            ProgramRole.Engineer or ProgramRole.Reviewer or ProgramRole.Approver or ProgramRole.TestEngineer
                or ProgramRole.TestLead or ProgramRole.Administrator or ProgramRole.SystemEngineeringLead
                or ProgramRole.SoftwareEngineeringLead or ProgramRole.ProjectEngineeringLead
                or ProgramRole.SystemTestLead or ProgramRole.SoftwareTestLead => null,
            _ => throw new DomainException($"The program role '{role}' is not classified by Team Work."),
        })
        .Where(role => role is not null)
        .Select(role => role!)
        .Distinct(StringComparer.Ordinal)
        .ToArray();

    private static string? CanonicalUserName(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim().ToLowerInvariant();

    private static string? ChangeRequestLayer(SystemChangeRequest change) => change.Type switch
    {
        ChangeRequestType.System => RequirementLevel.System.ToString(),
        ChangeRequestType.Interface => RequirementLevel.Interface.ToString(),
        ChangeRequestType.Software when change.SoftwareLevel is RequirementLevel.HighLevel
            or RequirementLevel.LowLevel => change.SoftwareLevel.Value.ToString(),
        _ => null,
    };

    private static string? VerificationLayer(TestChangeReviewDiscipline discipline) => discipline switch
    {
        TestChangeReviewDiscipline.System => RequirementLevel.System.ToString(),
        TestChangeReviewDiscipline.HighLevelSoftware => RequirementLevel.HighLevel.ToString(),
        TestChangeReviewDiscipline.LowLevelSoftware => RequirementLevel.LowLevel.ToString(),
        _ => null,
    };

    private static string? AssessmentLayer(RequirementLevel level) => level switch
    {
        RequirementLevel.System or RequirementLevel.HighLevel or RequirementLevel.LowLevel or RequirementLevel.Interface
            => level.ToString(),
        _ => null,
    };

    private static string? ArtifactTypeForNumber(string? number)
    {
        if (string.IsNullOrWhiteSpace(number)) return null;
        var separator = number.IndexOf('-');
        var value = separator > 0
            ? number[..separator]
            : new string(number.TakeWhile(char.IsLetter).ToArray());
        return string.IsNullOrWhiteSpace(value) ? null : value.ToUpperInvariant();
    }

    private static TeamWorkFacetProjection BuildFacets(
        IReadOnlyList<TeamWorkItemResponse> items, ILadderPolicy? effectiveLadder)
    {
        var allTypes = items
            .Where(item => !string.IsNullOrWhiteSpace(item.ArtifactType))
            .GroupBy(item => item.ArtifactType, StringComparer.OrdinalIgnoreCase)
            .Select(group => new TeamWorkArtifactTypeFacet(
                group.Key,
                ArtifactTypeLabel(group.Key),
                group.Count()))
            .OrderBy(type => type.Id, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        // The ladder supplies the allowed layer vocabulary. Observed rows are included as a compatibility
        // bridge for old projects (and make a persisted Interface change request visible even when the legacy
        // ladder deliberately omits Interface from its three-level requirement chain).
        var levels = new List<string>();
        if (effectiveLadder is not null)
            levels.AddRange(effectiveLadder.OrderedLevels
                .Where(level => level is not RequirementLevel.Customer)
                .Select(level => level.ToString()));
        levels.AddRange(items.Where(item => !string.IsNullOrWhiteSpace(item.Layer)).Select(item => item.Layer!));
        var orderedLevels = levels.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var layerOrder = effectiveLadder?.OrderedLevels
            .Select((level, index) => (Id: level.ToString(), index))
            .ToDictionary(value => value.Id, value => value.index, StringComparer.OrdinalIgnoreCase)
            ?? new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var layers = orderedLevels
            .Select(id =>
            {
                var layerItems = items.Where(item => string.Equals(item.Layer, id, StringComparison.OrdinalIgnoreCase)).ToArray();
                var types = layerItems
                    .GroupBy(item => item.ArtifactType, StringComparer.OrdinalIgnoreCase)
                    .Select(group => new TeamWorkArtifactTypeFacet(group.Key, ArtifactTypeLabel(group.Key), group.Count()))
                    .OrderBy(type => type.Id, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                return new TeamWorkLayerFacet(id, LayerLabel(id), layerItems.Length, types);
            })
            .OrderBy(layer => layerOrder.GetValueOrDefault(layer.Id, int.MaxValue))
            .ThenBy(layer => layer.Id, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return new TeamWorkFacetProjection(layers, allTypes);
    }

    private static string LayerLabel(string id) => id switch
    {
        nameof(RequirementLevel.HighLevel) => "HLR",
        nameof(RequirementLevel.LowLevel) => "LLR",
        _ => id,
    };

    private static string ArtifactTypeLabel(string id) => id switch
    {
        "ProblemReport" => "Problem Report",
        "Assessment" => "Assessment",
        _ => id,
    };

    private static IReadOnlyList<string> CanonicalHolderIds(IEnumerable<string> values) => values
        .Where(value => !string.IsNullOrWhiteSpace(value))
        .Select(value => value.Trim().ToLowerInvariant())
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();

    private static string? Prefix(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var separator = value.IndexOf('-');
        return separator > 0 ? value[..separator] : null;
    }

    private static string WireFamily(ChangeRequestType type) => type switch
    {
        ChangeRequestType.System => "system",
        ChangeRequestType.Software => "software",
        ChangeRequestType.Interface => "interface",
        _ => throw new DomainException($"The change-request type '{type}' is not supported by Team Work."),
    };

    private static string ChangeRequestCategory(SystemChangeRequest change) => change.Type switch
    {
        ChangeRequestType.System => "system",
        ChangeRequestType.Interface => "interface",
        ChangeRequestType.Software when change.SoftwareLevel == RequirementLevel.HighLevel => "HLR",
        ChangeRequestType.Software when change.SoftwareLevel == RequirementLevel.LowLevel => "LLR",
        ChangeRequestType.Software => throw new DomainException("A software change request has an unsupported level."),
        _ => throw new DomainException($"The change-request type '{change.Type}' is not supported by Team Work."),
    };

    private static string TestChangeReviewCategory(TestChangeReview review) => review.Discipline switch
    {
        TestChangeReviewDiscipline.System => "system",
        TestChangeReviewDiscipline.HighLevelSoftware => "HLR",
        TestChangeReviewDiscipline.LowLevelSoftware => "LLR",
        _ => throw new DomainException($"The test-change-review discipline '{review.Discipline}' is not supported by Team Work."),
    };

    private static string AssessmentCategory(RequirementLevel level) => level switch
    {
        RequirementLevel.System => "Assessment",
        RequirementLevel.HighLevel => "HLR assessment",
        RequirementLevel.LowLevel => "LLR assessment",
        RequirementLevel.Interface => "Interface assessment",
        _ => throw new DomainException($"The assessment target level '{level}' is not supported by Team Work."),
    };

    private static string WireTestChangeReviewOutcome(TestChangeReviewOutcome outcome) => outcome switch
    {
        TestChangeReviewOutcome.Pending => "Pending",
        TestChangeReviewOutcome.ChangeRequired => "ChangeRequired",
        TestChangeReviewOutcome.NoChangeRequired => "NoChangeRequired",
        _ => throw new DomainException($"The test-change-review outcome '{outcome}' is not supported by Team Work."),
    };

    private static string WireLane(TeamWorkLane lane) => lane switch
    {
        TeamWorkLane.InWork => "work",
        TeamWorkLane.InReview => "review",
        TeamWorkLane.AwaitingSignature => "sign",
        TeamWorkLane.Approved => "approved",
        _ => throw new DomainException($"The Team Work lane '{lane}' is not supported by the API."),
    };

    private static string WireHolderBasis(TeamWorkHolderBasis basis) => basis switch
    {
        TeamWorkHolderBasis.None => "none",
        TeamWorkHolderBasis.Author => "author",
        TeamWorkHolderBasis.AssignedEngineer => "assignedEngineer",
        TeamWorkHolderBasis.ResponsibleEngineer => "responsibleEngineer",
        TeamWorkHolderBasis.ActiveReviewStage => "activeReviewStage",
        TeamWorkHolderBasis.ActiveApprovalStage => "activeApprovalStage",
        TeamWorkHolderBasis.ActiveReviewAndApprovalStages => "activeReviewAndApprovalStages",
        TeamWorkHolderBasis.SelectedAssessmentApprover => "selectedAssessmentApprover",
        _ => throw new DomainException($"The Team Work holder basis '{basis}' is not supported by the API."),
    };

    private static string WireOriginKind(TestChangeReviewOriginKind originKind) => originKind switch
    {
        TestChangeReviewOriginKind.ChangeRequest => "changeRequest",
        TestChangeReviewOriginKind.ProblemReport => "problemReport",
        TestChangeReviewOriginKind.CaseChange => "caseChange",
        TestChangeReviewOriginKind.CaseAssessment => "caseAssessment",
        TestChangeReviewOriginKind.CaseReview => "caseReview",
        _ => throw new DomainException($"The test-change-review origin kind '{originKind}' is not supported by Team Work."),
    };

    private sealed record TeamWorkItemDraft(
        Guid Id,
        string Family,
        string? Layer,
        string ArtifactType,
        string? Category,
        string? Prefix,
        string? Number,
        string Title,
        string Lane,
        string NativeState,
        string? NativeOutcome,
        IReadOnlyList<string> CurrentHolderIds,
        string HolderBasis,
        IReadOnlyList<TeamWorkStageObligation> ActiveStageObligations,
        string? RaisedById,
        string? RaisedByKind,
        TeamWorkRelease? Release,
        bool Deferred,
        TeamWorkAllocation? Allocation,
        string? DeferredFromState,
        DateTimeOffset UpdatedAt,
        string OpenUrl);

    private sealed class TeamWorkPersonAccumulator(
        string userName,
        string displayName,
        Guid? userId,
        bool isCurrentProjectMember,
        string? accountState,
        IReadOnlyList<string> baseRoles)
    {
        private int holds;
        private int work;
        private int review;
        private int sign;
        private int approved;

        public string UserName { get; } = userName;
        public string DisplayName { get; } = displayName;
        public Guid? UserId { get; } = userId;
        public bool IsCurrentProjectMember { get; } = isCurrentProjectMember;
        public string? AccountState { get; } = accountState;
        public IReadOnlyList<string> BaseRoles { get; } = baseRoles;

        public void Add(string lane)
        {
            holds++;
            switch (lane)
            {
                case "work": work++; break;
                case "review": review++; break;
                case "sign": sign++; break;
                case "approved": approved++; break;
                default: throw new DomainException($"The Team Work wire lane '{lane}' is not supported.");
            }
        }

        public TeamWorkPerson ToResponse() => new(UserId, UserName, DisplayName, IsCurrentProjectMember,
            AccountState, BaseRoles, DisciplineAffinities(BaseRoles), holds,
            new TeamWorkPersonLanes(work, review, sign, approved));

        private static IReadOnlyList<string> DisciplineAffinities(IReadOnlyList<string> roles)
        {
            var affinities = new List<string>(2);
            if (roles.Any(role => role is "SystemEngineer" or "SystemTestEngineer")) affinities.Add("system");
            if (roles.Any(role => role is "SoftwareEngineer" or "SoftwareTestEngineer")) affinities.Add("software");
            return affinities;
        }
    }
}

public sealed record TeamWorkProjectionResponse(
    DateTimeOffset GeneratedAt,
    TeamWorkTotals Totals,
    IReadOnlyList<TeamWorkPerson> People,
    IReadOnlyList<TeamWorkItemResponse> Items,
    IReadOnlyList<TeamWorkLayerFacet> Layers,
    IReadOnlyList<TeamWorkArtifactTypeFacet> ArtifactTypes);

public sealed record TeamWorkFacetProjection(
    IReadOnlyList<TeamWorkLayerFacet> Layers,
    IReadOnlyList<TeamWorkArtifactTypeFacet> ArtifactTypes);

public sealed record TeamWorkLayerFacet(
    string Id,
    string Label,
    int Count,
    IReadOnlyList<TeamWorkArtifactTypeFacet> ArtifactTypes);

public sealed record TeamWorkArtifactTypeFacet(string Id, string Label, int Count);

public sealed record TeamWorkTotals(int Items, int Returned, int Unheld);

public sealed record TeamWorkPerson(
    Guid? UserId,
    string UserName,
    string DisplayName,
    bool IsCurrentProjectMember,
    string? AccountState,
    IReadOnlyList<string> BaseRoles,
    IReadOnlyList<string> DisciplineAffinities,
    int Holds,
    TeamWorkPersonLanes ByLane);

public sealed record TeamWorkPersonLanes(int Work, int Review, int Sign, int Approved);

public sealed record TeamWorkRelease(Guid Id, string Version, bool IsReleased);

public sealed record TeamWorkAllocation(
    Guid BaselineId,
    Guid ReleaseId,
    string ReleaseVersion,
    string BaselineNumber,
    int BaselineRevision,
    bool IsReleased);

public sealed record TeamWorkItemResponse(
    Guid Id,
    string Family,
    string? Layer,
    string ArtifactType,
    string? Category,
    string? Prefix,
    string? Number,
    string Title,
    string Lane,
    string NativeState,
    string? NativeOutcome,
    IReadOnlyList<string> CurrentHolderIds,
    string HolderBasis,
    IReadOnlyList<TeamWorkStageObligation> ActiveStageObligations,
    string? RaisedById,
    string? RaisedByKind,
    TeamWorkRelease? Release,
    bool Deferred,
    TeamWorkAllocation? Allocation,
    string? DeferredFromState,
    DateTimeOffset UpdatedAt,
    string OpenUrl);

public sealed record TeamWorkStageObligation(string HolderId, string StageKind);
