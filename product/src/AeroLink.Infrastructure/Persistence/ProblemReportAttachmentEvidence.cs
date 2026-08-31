using AeroLink.Domain.Requirements;
using Microsoft.EntityFrameworkCore;

namespace AeroLink.Infrastructure.Persistence;

/// <summary>Builds the exact active supporting-file manifest committed into Problem Report evidence.</summary>
public static class ProblemReportAttachmentEvidence
{
    public static async Task<IReadOnlyList<ProblemReportSupportingAttachmentSnapshot>> ActiveAsync(
        AeroLinkDbContext db, Guid projectId, Guid problemReportId, CancellationToken ct)
    {
        var rows = await db.ControlledAttachments.AsNoTracking()
            .Where(x => x.ProjectId == projectId && x.ArtifactType == "ProblemReport"
                && x.ArtifactId == problemReportId && x.State == ControlledAttachmentState.Active)
            .OrderBy(x => x.LogicalId).ThenBy(x => x.Version)
            .Select(x => new ProblemReportSupportingAttachmentSnapshot
            {
                AttachmentId = x.Id,
                LogicalId = x.LogicalId,
                Version = x.Version,
                FileName = x.OriginalFileName,
                ContentType = x.ContentType,
                Size = x.Size,
                Sha256 = x.Sha256,
                UploadedBy = x.UploadedBy,
                UploadedAt = x.UploadedAt,
            }).ToListAsync(ct);
        return rows;
    }

    public static async Task<(string Json, string Hash)> SnapshotAsync(AeroLinkDbContext db,
        ProblemReport report, CancellationToken ct)
    {
        var manifest = await ActiveAsync(db, report.ProjectId, report.Id, ct);
        var json = ProblemReportEvidenceContract.Serialize(report, supportingAttachments: manifest);
        return (json, ProblemReportEvidenceContract.Hash(json));
    }
}
