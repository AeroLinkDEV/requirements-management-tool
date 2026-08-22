using AeroLink.Domain.Common;
using AeroLink.Domain.Traceability;
using Microsoft.EntityFrameworkCore;

namespace AeroLink.Infrastructure.Persistence;

/// <summary>Application seam for attributed exact-link lifecycle mutations.</summary>
public sealed class ExactLinkLifecycleService(AeroLinkDbContext db)
{
    public Task<ExactLinkSuspectLifecycle> AcknowledgeAsync(Guid linkId, string actorId, string rationale,
        DateTimeOffset now, CancellationToken ct) => MutateAsync(linkId, lifecycle => lifecycle.Acknowledge(actorId, rationale, now), ct);

    public Task<ExactLinkSuspectLifecycle> ResolveAsync(Guid linkId, ExactLinkResolutionOutcome outcome,
        string actorId, string rationale, DateTimeOffset now, CancellationToken ct) =>
        MutateAsync(linkId, lifecycle => lifecycle.RecordResolution(outcome, actorId, rationale, now), ct);

    private async Task<ExactLinkSuspectLifecycle> MutateAsync(Guid linkId,
        Action<ExactLinkSuspectLifecycle> mutation, CancellationToken ct)
    {
        var lifecycle = await db.ExactLinkSuspectLifecycles
            .Include(x => x.Events)
            .SingleOrDefaultAsync(x => x.LinkKind == ExactLinkKind.RequirementTrace && x.LinkId == linkId, ct)
            ?? throw new DomainException("The exact trace link has no suspect lifecycle.");
        var link = await db.RequirementTraces.AsNoTracking().SingleOrDefaultAsync(x => x.Id == linkId, ct)
            ?? throw new DomainException("The exact trace link does not exist.");
        if (link.ProjectId != lifecycle.ProjectId)
            throw new DomainException("The exact trace lifecycle and link belong to different projects.");
        if (link.ExactLinkSuspectLifecycleId != lifecycle.Id)
            throw new DomainException("The exact trace link is not associated with the selected lifecycle projection.");
        var endpointIds = new[] { link.SourceRevisionId, link.TargetRevisionId };
        var baselineIds = await db.BaselineRequirements.AsNoTracking()
            .Where(x => endpointIds.Contains(x.RevisionId)).GroupBy(x => x.BaselineId)
            .Where(group => group.Select(x => x.RevisionId).Distinct().Count() == 2)
            .Select(group => group.Key).ToListAsync(ct);
        if (await db.CandidateBaselines.AsNoTracking().AnyAsync(x => baselineIds.Contains(x.Id)
            && x.State == AeroLink.Domain.Baselines.CandidateBaselineState.Released, ct)
            || await db.ReleaseCampaigns.AsNoTracking().AnyAsync(x => baselineIds.Contains(x.BaselineId)
                && (x.State == AeroLink.Domain.Releases.ReleaseCampaignState.InReview || x.State == AeroLink.Domain.Releases.ReleaseCampaignState.Released), ct))
            throw new DomainException("The release package is frozen or released; exact-link lifecycle history cannot be mutated.");

        var eventCount = lifecycle.Events.Count;
        mutation(lifecycle);
        // A transition appends exactly one event. The domain owns construction and never exposes an event setter.
        db.ExactLinkSuspectEvents.Add(lifecycle.Events.Skip(eventCount).Single());
        await db.SaveChangesAsync(ct);
        return lifecycle;
    }
}
