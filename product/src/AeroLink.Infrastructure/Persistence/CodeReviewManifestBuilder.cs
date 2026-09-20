using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AeroLink.Domain.Common;
using AeroLink.Domain.Hierarchy;
using AeroLink.Domain.Integrations;
using Microsoft.EntityFrameworkCore;

namespace AeroLink.Infrastructure.Persistence;

public sealed record CodeReviewManifestMaterial(string Hash, Guid? SourceSelectionEventId,
    Guid? SourceSnapshotId, IReadOnlyList<Guid> EvidenceReferenceIds);

/// <summary>
/// A v2 commitment wraps the unchanged v1 hash with exact new Code material. It does not select a review
/// format or freeze a cycle: the transaction owner must persist the chosen format with the actual cycle.
/// </summary>
public sealed class CodeReviewManifestBuilder(AeroLinkDbContext db)
{
    public const string Format = "aerolink.release-review-manifest.v2";

    public async Task<CodeReviewManifestMaterial> BuildV2Async(Guid campaignId, string legacyManifestHash,
        ILadderPolicy policy, CancellationToken ct)
    {
        if (legacyManifestHash.Length != 64 || legacyManifestHash.Any(x => !Uri.IsHexDigit(x)))
            throw new DomainException("The v2 manifest requires the exact v1 SHA-256 commitment.");
        var campaign = await db.ReleaseCampaigns.AsNoTracking().SingleAsync(x => x.Id == campaignId, ct);
        if (campaign.SoftwareBuildId is null) throw new DomainException("Select the exact verification build before freezing the release package.");
        var members = await (from member in db.BaselineRequirements.AsNoTracking()
                             where member.BaselineId == campaign.BaselineId
                             join artifact in db.Requirements.AsNoTracking().Where(x => x.ProjectId == campaign.ProjectId)
                                 on member.ArtifactId equals artifact.Id
                             select new { member.ArtifactId, member.RevisionId, artifact.Level }).ToListAsync(ct);
        var scope = members.Where(x => policy.OrderedLevels.Contains(x.Level)
                && (policy is ILegacyLadderCompatibilityPolicy || policy.HasCodeTraceability(x.Level)))
            .Select(x => (x.ArtifactId, x.RevisionId)).ToHashSet();
        var pointer = await db.GitLabCurrentSourceSelections.AsNoTracking()
            .SingleOrDefaultAsync(x => x.ProjectId == campaign.ProjectId && x.ReleaseId == campaign.ReleaseId, ct);
        GitLabSourceSnapshot? snapshot = null;
        GitLabSourceSelectionEvent? selection = null;
        if (pointer is not null)
        {
            snapshot = await db.GitLabSourceSnapshots.AsNoTracking().SingleOrDefaultAsync(x =>
                x.ProjectId == campaign.ProjectId && x.Id == pointer.SourceSnapshotId, ct);
            selection = await db.GitLabSourceSelectionEvents.AsNoTracking().SingleOrDefaultAsync(x =>
                x.ProjectId == campaign.ProjectId && x.ReleaseId == campaign.ReleaseId && x.Id == pointer.SelectionEventId, ct);
            if (snapshot is null || selection is null || selection.SourceSnapshotId != snapshot.Id
                || selection.ResultingVersion != pointer.Version)
                throw new DomainException("The selected Code source has inconsistent snapshot/event identity.");
        }
        var current = (await CurrentCodeEvidenceProjection.ForReleaseAsync(db, campaign.ProjectId, campaign.ReleaseId, ct))
            .Where(x => scope.Contains((x.RequirementArtifactId, x.RequirementRevisionId)) && x.Selector is not null)
            .OrderBy(x => x.RequirementArtifactId).ThenBy(x => x.RequirementRevisionId).ToArray();
        var canonical = JsonSerializer.Serialize(new
        {
            schema = Format,
            hashAlgorithm = "SHA-256",
            encoding = "UTF-8",
            legacySchema = "aerolink.release-review-manifest.v1",
            legacyManifestHash = legacyManifestHash.ToLowerInvariant(),
            campaign = new { campaign.Id, campaign.ProjectId, campaign.ReleaseId, campaign.BaselineId, campaign.SoftwareBuildId },
            source = snapshot is null ? null : new
            {
                snapshot.Id, snapshot.ProjectId, snapshot.RepositoryConfigurationId, snapshot.ConfigurationVersion,
                snapshot.InstanceBaseUrl, snapshot.RemoteProjectId, snapshot.PathWithNamespace, snapshot.CommitSha,
                snapshot.FriendlyRef, snapshot.RecordedBy, snapshot.RecordedAt,
                selection = new { selection!.Id, selection.ProjectId, selection.ReleaseId, selection.SourceSnapshotId,
                    selection.ExpectedCurrentVersion, selection.ResultingVersion, selection.SelectedBy, selection.SelectedAt },
            },
            evidence = current.Select(x => new
            {
                x.RequirementArtifactId, x.RequirementRevisionId, state = x.State.ToString(),
                selector = new { x.Selector!.Id, x.Selector.EvidenceSetId, x.Selector.Version,
                    x.Selector.SelectedBy, x.Selector.SelectedAt },
                disposition = x.EvidenceSet is null ? null : new
                {
                    x.EvidenceSet.Id, disposition = x.EvidenceSet.Disposition.ToString(), x.EvidenceSet.NoCodeChangeRationale,
                    x.EvidenceSet.SourceSelectionEventId, x.EvidenceSet.SourceSnapshotId, x.EvidenceSet.SupersededLegacyRecordId,
                    x.EvidenceSet.RecordedBy, x.EvidenceSet.RecordedAt,
                },
                contributions = x.Contributions.OrderBy(c => c.Id).Select(c => new
                {
                    c.Id, c.EvidenceSetId, c.ProjectId, c.ReleaseId, c.RequirementArtifactId, c.RequirementRevisionId,
                    c.SourceSnapshotId, kind = c.ContributionKind.ToString(), c.RelationshipId,
                    c.InstanceBaseUrl, c.RemoteProjectId, c.RepositoryPathSnapshot, c.MergeRequestIid, c.MergeRequestId,
                    c.MergeRequestUrlSnapshot, c.MergeRequestTitleSnapshot, c.CommitSha, c.FilePath,
                    c.StartLine, c.EndLine, c.MergeResultSha, mergeResultKind = c.MergeResultKind?.ToString(),
                    c.MergedAt, c.ProviderObservedAt, targetKind = c.TargetKind.ToString(), c.TargetIdentityId,
                    c.TargetOwnerIdentityId, c.TargetRevisionNumber, c.TargetStableIdentity, c.TargetDisplaySnapshot,
                    c.RecordedBy, c.RecordedAt,
                }),
            }),
        });
        return new(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant(),
            selection?.Id, snapshot?.Id, current.Select(x => x.Selector!.EvidenceSetId).Distinct().OrderBy(x => x).ToArray());
    }
}
