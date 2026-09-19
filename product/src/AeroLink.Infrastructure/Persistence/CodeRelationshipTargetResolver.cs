using AeroLink.Domain.Common;
using AeroLink.Domain.Integrations;
using AeroLink.Domain.Requirements;
using Microsoft.EntityFrameworkCore;

namespace AeroLink.Infrastructure.Persistence;

/// <summary>
/// Resolves exact identities and display snapshots from project-owned records. Callers must separately
/// enforce current actor authority, mutation capabilities, and implementation-build effectivity.
/// Historical targets are never replaced with their latest revision or materialized successor.
/// </summary>
public static class CodeRelationshipTargetResolver
{
    public static async Task<CodeRelationshipTarget?> ResolveAsync(AeroLinkDbContext db, Guid projectId,
        CodeRelationshipTargetKind kind, Guid exactIdentityId, CancellationToken ct)
    {
        if (projectId == Guid.Empty || exactIdentityId == Guid.Empty) return null;
        switch (kind)
        {
            case CodeRelationshipTargetKind.RequirementRevision:
            {
                var row = await (from revision in db.RequirementRevisions.AsNoTracking()
                                 where revision.Id == exactIdentityId
                                 join artifact in db.Requirements.AsNoTracking().Where(x => x.ProjectId == projectId)
                                     on revision.ArtifactId equals artifact.Id
                                 select new { revision.Id, revision.ArtifactId, revision.Revision, artifact.BaseNumber })
                    .SingleOrDefaultAsync(ct);
                return row is null ? null : CodeRelationshipTarget.ForRequirementRevision(row.Id,
                    row.ArtifactId, row.Revision, ArtifactNumber.Display(row.BaseNumber, row.Revision));
            }
            case CodeRelationshipTargetKind.ChangeRequestRevision:
            {
                var row = await db.SystemChangeRequests.AsNoTracking()
                    .SingleOrDefaultAsync(x => x.ProjectId == projectId && x.Id == exactIdentityId, ct);
                return row is null ? null : CodeRelationshipTarget.ForChangeRequestRevision(row.Id,
                    row.Revision, row.DisplayNumber);
            }
            case CodeRelationshipTargetKind.RequirementProposal:
            {
                var row = await (from proposal in db.RequirementChanges.AsNoTracking()
                                 where proposal.Id == exactIdentityId
                                 join owner in db.SystemChangeRequests.AsNoTracking().Where(x => x.ProjectId == projectId)
                                     on proposal.ChangeRequestId equals owner.Id
                                 select new { Proposal = proposal, Owner = owner }).SingleOrDefaultAsync(ct);
                if (row is null) return null;
                var label = string.IsNullOrWhiteSpace(row.Proposal.DisplayNumber)
                    ? $"Unnamed requirement proposal in {row.Owner.DisplayNumber}"
                    : $"{row.Proposal.DisplayNumber} proposal in {row.Owner.DisplayNumber}";
                return CodeRelationshipTarget.ForRequirementProposal(row.Proposal.Id, row.Owner.Id, label);
            }
            case CodeRelationshipTargetKind.ProblemReportRevision:
            {
                var row = await (from snapshot in db.ProblemReportRevisions.AsNoTracking()
                                 where snapshot.Id == exactIdentityId
                                 join report in db.ProblemReports.AsNoTracking().Where(x => x.ProjectId == projectId)
                                     on snapshot.ProblemReportId equals report.Id
                                 select snapshot).SingleOrDefaultAsync(ct);
                if (row is null || string.IsNullOrWhiteSpace(row.SnapshotJson)
                    || row.SnapshotSchemaVersion is < 1 or > ProblemReportEvidenceContract.SchemaVersion
                    || !string.Equals(ProblemReportEvidenceContract.Hash(row.SnapshotJson), row.SnapshotHash,
                        StringComparison.OrdinalIgnoreCase)) return null;
                var parsed = ProblemReportOutputGenerator.ReadStoredSnapshot(row.SnapshotJson, row.SnapshotSchemaVersion);
                if (parsed is null || parsed.Value.Snapshot.Id != row.ProblemReportId
                    || parsed.Value.Snapshot.ProjectId != projectId || parsed.Value.Snapshot.Revision != row.Revision
                    || string.IsNullOrWhiteSpace(parsed.Value.Snapshot.DisplayNumber))
                    return null;
                return CodeRelationshipTarget.ForProblemReportSnapshot(row.Id, row.ProblemReportId,
                    row.Revision, parsed.Value.Snapshot.DisplayNumber);
            }
            default: return null;
        }
    }
}
