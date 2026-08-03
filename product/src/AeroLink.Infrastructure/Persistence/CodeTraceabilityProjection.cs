using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Requirements;
using Microsoft.EntityFrameworkCore;

namespace AeroLink.Infrastructure.Persistence;

public sealed record RequiredCodeTraceabilityRequirement(
    Guid ArtifactId,
    Guid RevisionId,
    string BaseNumber,
    int Revision,
    string Statement,
    bool ChangedInBuild);

/// <summary>
/// Defines the exact LLR revisions that owe implementation evidence for one build. The Code workspace and
/// authoritative release readiness use this same projection so they cannot disagree about whether the gate is
/// complete.
/// </summary>
public static class CodeTraceabilityProjection
{
    public static async Task<IReadOnlyList<RequiredCodeTraceabilityRequirement>> RequiredAsync(
        AeroLinkDbContext db,
        Guid projectId,
        Guid releaseId,
        Guid baselineId,
        CancellationToken ct)
    {
        var candidates = await (from selection in db.BaselineRequirements.AsNoTracking().Where(x => x.BaselineId == baselineId)
                                join artifact in db.Requirements.AsNoTracking().Where(x => x.ProjectId == projectId && x.Level == RequirementLevel.LowLevel) on selection.ArtifactId equals artifact.Id
                                join revision in db.RequirementRevisions.AsNoTracking() on selection.RevisionId equals revision.Id
                                join change in db.SystemChangeRequests.AsNoTracking() on revision.SourceScrId equals change.Id into changes
                                from change in changes.DefaultIfEmpty()
                                select new RequiredCodeTraceabilityRequirement(
                                    artifact.Id,
                                    revision.Id,
                                    artifact.BaseNumber,
                                    revision.Revision,
                                    revision.Statement,
                                    change != null && change.TargetReleaseId == releaseId)).ToListAsync(ct);

        // One rule, every Project: a build owes implementation evidence for exactly the LLR revisions it
        // introduced or modified.
        //
        // The demonstration Program used to take the first five LLRs by number instead, to keep the seeded
        // dataset small without inventing hundreds of merge requests. That capped the sample evidence by
        // redefining the gate: on the only Program anybody can actually use, an authoritative release gate
        // measured requirements the build had never touched, and would have reported complete while every
        // genuinely changed LLR lacked code evidence. A demonstration boundary may limit what is seeded; it
        // may not decide which changes owe evidence.
        //
        // A build that changed no LLR owes nothing and passes at 0/0, which is the honest answer rather than
        // a convenient one.
        return candidates.Where(x => x.ChangedInBuild).OrderBy(x => x.BaseNumber).ToList();
    }
}
