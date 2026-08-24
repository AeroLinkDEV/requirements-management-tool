using AeroLink.Domain.Common;
using AeroLink.Domain.Traceability;
using Microsoft.EntityFrameworkCore;

namespace AeroLink.Infrastructure.Persistence;

/// <summary>Application seam for attributed exact-link lifecycle mutations.</summary>
public sealed class ExactLinkLifecycleService(AeroLinkDbContext db)
{
    public Task<ExactLinkSuspectLifecycle> AcknowledgeAsync(Guid linkId, string actorId, string rationale,
        DateTimeOffset now, CancellationToken ct) => AcknowledgeAsync(ExactLinkKind.RequirementTrace,
        linkId, actorId, rationale, now, ct);

    public Task<ExactLinkSuspectLifecycle> AcknowledgeAsync(ExactLinkKind linkKind, Guid linkId,
        string actorId, string rationale, DateTimeOffset now, CancellationToken ct) =>
        MutateAsync(linkKind, linkId, lifecycle => lifecycle.Acknowledge(actorId, rationale, now), ct);

    public Task<ExactLinkSuspectLifecycle> ResolveAsync(Guid linkId, ExactLinkResolutionOutcome outcome,
        string actorId, string rationale, DateTimeOffset now, CancellationToken ct) =>
        ResolveAsync(ExactLinkKind.RequirementTrace, linkId, outcome, actorId, rationale, now, ct);

    public Task<ExactLinkSuspectLifecycle> ResolveAsync(ExactLinkKind linkKind, Guid linkId,
        ExactLinkResolutionOutcome outcome, string actorId, string rationale, DateTimeOffset now,
        CancellationToken ct) => MutateAsync(linkKind, linkId,
        lifecycle => lifecycle.RecordResolution(outcome, actorId, rationale, now), ct);

    private async Task<ExactLinkSuspectLifecycle> MutateAsync(ExactLinkKind linkKind, Guid linkId,
        Action<ExactLinkSuspectLifecycle> mutation, CancellationToken ct)
    {
        var lifecycle = await db.ExactLinkSuspectLifecycles
            .Include(x => x.Events)
            .SingleOrDefaultAsync(x => x.LinkKind == linkKind && x.LinkId == linkId, ct)
            ?? throw new DomainException("The exact trace link has no suspect lifecycle.");
        List<Guid> baselineIds;
        if (linkKind == ExactLinkKind.RequirementTrace)
        {
            var link = await db.RequirementTraces.AsNoTracking().SingleOrDefaultAsync(x => x.Id == linkId, ct)
                ?? throw new DomainException("The exact trace link does not exist.");
            if (link.ProjectId != lifecycle.ProjectId)
                throw new DomainException("The exact trace lifecycle and link belong to different projects.");
            if (link.ExactLinkSuspectLifecycleId != lifecycle.Id)
                throw new DomainException("The exact trace link is not associated with the selected lifecycle projection.");
            var endpointIds = new[] { link.SourceRevisionId, link.TargetRevisionId };
            baselineIds = await db.BaselineRequirements.AsNoTracking()
                .Where(x => endpointIds.Contains(x.RevisionId)).GroupBy(x => x.BaselineId)
                .Where(group => group.Select(x => x.RevisionId).Distinct().Count() == 2)
                .Select(group => group.Key).ToListAsync(ct);
        }
        else if (linkKind == ExactLinkKind.CaseProcedure)
        {
            var link = await db.TestCaseProcedureLinks.AsNoTracking()
                .SingleOrDefaultAsync(x => x.Id == linkId, ct)
                ?? throw new DomainException("The exact Case-to-Procedure link does not exist.");
            if (link.ExactLinkSuspectLifecycleId != lifecycle.Id)
                throw new DomainException("The exact Case-to-Procedure link is not associated with the selected lifecycle projection.");
            var projectIds = await (from revision in db.TestProcedureRevisions.AsNoTracking()
                                    join artifact in db.TestProcedures.AsNoTracking()
                                        on revision.ProcedureId equals artifact.Id
                                    where revision.Id == link.CaseRevisionId || revision.Id == link.ProcedureRevisionId
                                    select artifact.ProjectId).Distinct().ToListAsync(ct);
            if (projectIds.Count != 1 || projectIds[0] != lifecycle.ProjectId)
                throw new DomainException("The exact Case-to-Procedure lifecycle and link belong to different projects.");
            // #726 will add Procedure effectivity. Until then the materialized Case revision is the exact
            // baseline applicability authority; no historical or unrelated baseline is backpatched.
            baselineIds = await db.BaselineTestProcedures.AsNoTracking()
                .Where(x => x.RevisionId == link.CaseRevisionId)
                .Select(x => x.BaselineId).Distinct().ToListAsync(ct);
        }
        else
        {
            throw new DomainException($"The exact-link kind '{linkKind}' is not registered.");
        }
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
