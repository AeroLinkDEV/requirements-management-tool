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
            raised++;
        }
        return raised;
    }
}
