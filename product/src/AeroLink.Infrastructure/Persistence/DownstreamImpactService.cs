using AeroLink.Domain.ChangeControl;
using Microsoft.EntityFrameworkCore;

namespace AeroLink.Infrastructure.Persistence;

/// <summary>
/// Opens the consuming discipline's assessment when an upstream engineering change is approved.
/// This creates work, not an SWCR: the consuming engineer may conclude no change, create a one-to-one
/// SWCR, or group several assessments into one downstream change request.
/// </summary>
public sealed class DownstreamImpactService(AeroLinkDbContext db)
{
    public async Task<int> RaiseForApprovedChangeRequestAsync(SystemChangeRequest request,
        DateTimeOffset now, CancellationToken ct)
    {
        if (request.State is not (ScrState.Approved or ScrState.SelectedForBaseline)) return 0;
        // Old showcase data predates the aggregate invariant. Refuse to turn a mismatched CR into more
        // controlled work; reconciliation remediates that source explicitly and preserves its history.
        if (request.RequirementChanges.Any(x => !SystemChangeRequest.AcceptsRequirementLevel(request.Type, x.Level)))
            return 0;

        var targets = new HashSet<RequirementLevel>();
        if (request.RequirementChanges.Any(x => x.Level == RequirementLevel.System))
            targets.Add(RequirementLevel.HighLevel);
        if (request.RequirementChanges.Any(x => x.Level == RequirementLevel.HighLevel))
            targets.Add(RequirementLevel.LowLevel);

        var existing = await db.DownstreamChangeAssessments
            .Where(x => x.SourceChangeRequestId == request.Id)
            .ToDictionaryAsync(x => x.TargetLevel, ct);
        var priorRequestIds = await db.SystemChangeRequests.AsNoTracking()
            .Where(x => x.ProjectId == request.ProjectId && x.BaseNumber == request.BaseNumber && x.Revision < request.Revision)
            .Select(x => x.Id).ToListAsync(ct);
        var prior = await db.DownstreamChangeAssessments
            .Where(x => priorRequestIds.Contains(x.SourceChangeRequestId) && x.State != DownstreamAssessmentState.Superseded)
            .ToListAsync(ct);

        var raised = 0;
        foreach (var target in targets)
        {
            if (existing.ContainsKey(target)) continue;
            var assessment = new DownstreamChangeAssessment(request.ProjectId, request.TargetReleaseId,
                request.Id, request.DisplayNumber, target, now);
            db.DownstreamChangeAssessments.Add(assessment);
            foreach (var historical in prior.Where(x => x.TargetLevel == target))
                historical.Supersede(assessment.Id,
                    $"{request.DisplayNumber} supersedes the source revision. Reassess the downstream impact against the approved replacement.", now);
            await SupersedeLegacyMisclassifiedAssessmentsAsync(request, assessment, now, ct);
            raised++;
        }
        return raised;
    }

    private async Task SupersedeLegacyMisclassifiedAssessmentsAsync(SystemChangeRequest replacement,
        DownstreamChangeAssessment successor, DateTimeOffset now, CancellationToken ct)
    {
        var candidates = await db.DownstreamChangeAssessments
            .Where(x => x.ProjectId == replacement.ProjectId
                && x.ReleaseId == replacement.TargetReleaseId
                && x.TargetLevel == successor.TargetLevel
                && x.SourceChangeRequestId != replacement.Id
                && x.State != DownstreamAssessmentState.Superseded)
            .ToListAsync(ct);
        if (candidates.Count == 0) return;

        var sourceIds = candidates.Select(x => x.SourceChangeRequestId).Distinct().ToList();
        var legacySources = await db.SystemChangeRequests.AsNoTracking().Include(x => x.RequirementChanges)
            .Where(x => sourceIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, ct);

        foreach (var candidate in candidates)
        {
            if (!legacySources.TryGetValue(candidate.SourceChangeRequestId, out var legacy)
                || legacy.RequirementChanges.All(x => SystemChangeRequest.AcceptsRequirementLevel(legacy.Type, x.Level))
                || !SharesExactSourceWork(legacy, replacement, successor.TargetLevel)) continue;

            candidate.Supersede(successor.Id,
                $"{replacement.DisplayNumber} is the correctly classified replacement for the same requirement change. Reassess the downstream impact against that controlled source.", now);
        }
    }

    private static bool SharesExactSourceWork(SystemChangeRequest legacy, SystemChangeRequest replacement,
        RequirementLevel downstreamTarget)
    {
        var sourceLevel = downstreamTarget == RequirementLevel.HighLevel
            ? RequirementLevel.System
            : RequirementLevel.HighLevel;
        return legacy.RequirementChanges.Where(x => x.Level == sourceLevel).Any(oldChange =>
            replacement.RequirementChanges.Any(newChange =>
                newChange.Level == oldChange.Level
                && newChange.Kind == oldChange.Kind
                && newChange.Revision == oldChange.Revision
                && string.Equals(newChange.BaseNumber, oldChange.BaseNumber, StringComparison.OrdinalIgnoreCase)
                && string.Equals(newChange.Statement, oldChange.Statement, StringComparison.Ordinal)));
    }
}
