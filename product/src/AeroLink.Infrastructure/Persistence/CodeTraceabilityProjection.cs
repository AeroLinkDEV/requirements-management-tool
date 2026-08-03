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

        var programCode = await (from project in db.Projects.AsNoTracking().Where(x => x.Id == projectId)
                                 join program in db.Programs.AsNoTracking() on project.ProgramId equals program.Id
                                 select program.Code).SingleAsync(ct);

        // Five records keep the FMS boundary understandable without inventing hundreds of demo merge requests.
        // Real Projects owe evidence only for exact LLR revisions introduced or modified in this build.
        return programCode == FmsShowcaseSeeder.ProgramCode
            ? candidates.OrderBy(x => x.BaseNumber).Take(5).ToList()
            : candidates.Where(x => x.ChangedInBuild).OrderBy(x => x.BaseNumber).ToList();
    }
}
