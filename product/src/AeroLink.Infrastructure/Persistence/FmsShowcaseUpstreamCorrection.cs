using System.Text.Json;
using AeroLink.Domain.ChangeControl;
using Microsoft.EntityFrameworkCore;

namespace AeroLink.Infrastructure.Persistence;

public sealed partial class FmsShowcaseSeeder
{
    private async Task<string?> CorrectUnapprovedUpstreamScenariosAsync(Guid programId, CancellationToken ct)
    {
        var project = await db.Projects.SingleAsync(x => x.ProgramId == programId, ct);
        var policy = await resolver.ResolveAsync(project.Id, ct);
        var markers = await db.ShowcaseUpgradeSteps.AsNoTracking().Where(x => x.ProgramId == programId
            && x.StepKey.StartsWith(ActiveTraceProposalPrefix)).ToListAsync(ct);
        var owned = markers.Select(x => JsonSerializer.Deserialize<ActiveTraceProposalIdentity>(x.Detail)
            ?? throw new InvalidOperationException("Invalid exact scenario proposal ownership."))
            .ToDictionary(x => x.RequestId);
        var invalidIds = await (from link in db.ChangeRequestUpstreamLinks.AsNoTracking()
            join child in db.SystemChangeRequests.AsNoTracking() on link.ChangeRequestId equals child.Id
            join parent in db.SystemChangeRequests.AsNoTracking() on link.UpstreamChangeRequestId equals parent.Id
            where child.ProjectId == project.Id && parent.State != ChangeRequestState.Approved
                && parent.State != ChangeRequestState.SelectedForBaseline
            select child.Id).Distinct().ToListAsync(ct);
        var requests = await db.SystemChangeRequests.Include(x => x.UpstreamLinks).Include(x => x.UpstreamHistory)
            .Include(x => x.RequirementChanges).Include(x => x.ReviewCycles).ThenInclude(x => x.Steps)
            .Where(x => invalidIds.Contains(x.Id)).ToListAsync(ct);
        // A signed child requires a separate controlled successor. Never rewrite it as a seed repair.
        if (requests.Any(x => x.State is not (ChangeRequestState.Draft or ChangeRequestState.InReview)))
            throw new InvalidOperationException("An unapproved upstream source is referenced by signed or shelved work; a controlled successor is required.");
        const string actor = "aerolink-maintenance";
        const string reason = "#1006: remove an upstream revision that was not approved. Preserve frozen reviews as historical evidence.";
        var now = PersistedTimestamp(DateTimeOffset.UtcNow);
        foreach (var request in requests)
        {
            var resumeReview = request.State == ChangeRequestState.InReview;
            if (resumeReview)
            {
                request.CancelReview(actor, reason, now);
                // PostgreSQL's upstream guard observes persisted Draft state.
                await db.SaveChangesAsync(ct);
            }
            foreach (var link in request.UpstreamLinks.ToList())
            {
                var source = await db.SystemChangeRequests.AsNoTracking().SingleAsync(x => x.Id == link.UpstreamChangeRequestId, ct);
                if (!ChangeRequestUpstreamEligibility.IsApproved(source.State))
                    request.RemoveUpstreamLink(actor, link.Id, reason, now, administratorAuthority: true);
            }
            if (owned.TryGetValue(request.Id, out var identity) && identity.UpstreamRevisionId is { } upstreamRevisionId)
            {
                var sourceId = await db.RequirementRevisions.AsNoTracking().Where(x => x.Id == upstreamRevisionId)
                    .Select(x => x.SourceChangeRequestId).SingleAsync(ct)
                    ?? throw new InvalidOperationException("The owned baseline parent has no exact source change request.");
                var source = await db.SystemChangeRequests.AsNoTracking().SingleAsync(x => x.Id == sourceId && x.ProjectId == project.Id, ct);
                if (!ChangeRequestUpstreamEligibility.IsApproved(source.State))
                    throw new InvalidOperationException("The owned baseline parent source is no longer approved.");
                var sourceBuild = await db.Releases.AsNoTracking().SingleAsync(x => x.Id == source.TargetReleaseId, ct);
                request.AddUpstreamLink(actor, source.Id, source.DisplayNumber, sourceBuild.Id, sourceBuild.Version,
                    $"#1006 correction: the exact approved source of baseline parent revision {upstreamRevisionId} remains controlling for this proposal.",
                    now, administratorAuthority: true);
            }
            // Unowned author work becomes truthfully incomplete; maintenance must not invent its answer.
            await db.SaveChangesAsync(ct);
            if (resumeReview && owned.ContainsKey(request.Id))
            {
                await SubmitActiveTraceParallelReviewAsync(request, programId, policy, now, ct);
                await db.SaveChangesAsync(ct);
            }
        }
        return $"Corrected {requests.Count} active upstream answers; retained prior review snapshots and recorded corrective history.";
    }
}
