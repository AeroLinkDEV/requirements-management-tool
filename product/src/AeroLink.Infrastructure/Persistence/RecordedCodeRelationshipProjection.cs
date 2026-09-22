using AeroLink.Domain.Integrations;
using Microsoft.EntityFrameworkCore;

namespace AeroLink.Infrastructure.Persistence;

/// <summary>The exact persisted address of a Code relationship target in a selected read frontier.</summary>
internal sealed record CodeRelationshipExactTargetAddress(
    CodeRelationshipTargetKind Kind,
    Guid IdentityId,
    Guid? OwnerIdentityId,
    int? RevisionNumber);

/// <summary>The bounded result of one batched active Code relationship read.</summary>
internal sealed record ActiveCodeRelationshipTargetRead(
    IReadOnlyList<CodeRelationshipReadRow> Rows,
    bool LimitExceeded);

/// <summary>
/// One active, independently recorded GitLab relationship. Its target and source fields are immutable stored
/// snapshots; this record is contextual reference data and never represents accepted implementation evidence.
/// </summary>
public sealed record RecordedCodeRelationship(
    Guid Id,
    CodeRelationshipKind RelationshipKind,
    long Version,
    bool IsActive,
    Guid ReleaseId,
    string ReleaseVersion,
    CodeRelationshipMeaning Meaning,
    string RecordedBy,
    DateTimeOffset RecordedAt,
    string? ReAddedBy,
    DateTimeOffset? ReAddedAt,
    CodeRelationshipTargetKind TargetKind,
    Guid TargetIdentityId,
    Guid? TargetOwnerIdentityId,
    int? TargetRevisionNumber,
    string TargetStableIdentity,
    string TargetDisplaySnapshot,
    string InstanceBaseUrl,
    long RemoteProjectId,
    string RepositoryPathSnapshot,
    Guid? SourceSnapshotId,
    Guid? SourceSelectionEventId,
    int? MergeRequestIid,
    long? MergeRequestId,
    string? MergeRequestUrlSnapshot,
    string? MergeRequestTitleSnapshot,
    string? CommitSha,
    string? Path,
    int? StartLine,
    int? EndLine,
    int? FileMergeRequestIid);

/// <summary>
/// Reads the one shared active Code relationship store for already-selected exact targets. Callers own target
/// admission and release context; this projection does not broaden either frontier or resolve today's labels.
/// </summary>
internal static class RecordedCodeRelationshipProjection
{
    public static async Task<RecordedCodeRelationshipProjectionResult> ReadAsync(
        AeroLinkDbContext db,
        Guid projectId,
        IReadOnlyCollection<CodeRelationshipExactTargetAddress> targets,
        IReadOnlyCollection<Guid>? releaseIds,
        int maximumRows,
        TraceReadBudget? budget,
        CancellationToken ct)
    {
        if (projectId == Guid.Empty || maximumRows < 0) throw new ArgumentOutOfRangeException(nameof(maximumRows));
        if (targets.Count == 0 || releaseIds is { Count: 0 }) return new([], false);

        var read = await new CodeRelationshipService(db).ReadActiveForTargetsAsync(
            projectId, targets, releaseIds, maximumRows, budget, ct);
        if (read.Rows.Count == 0) return new([], read.LimitExceeded);

        var releaseIdsInRows = read.Rows.Select(x => x.ReleaseId).Distinct().ToArray();
        var releaseVersions = await db.Releases.AsNoTracking()
            .Where(x => x.ProjectId == projectId && releaseIdsInRows.Contains(x.Id))
            .Select(x => new { x.Id, x.Version })
            .ReadTraceAsync(budget, ct);
        var byRelease = releaseVersions.ToDictionary(x => x.Id, x => x.Version);
        var references = read.Rows.Select(row => new RecordedCodeRelationship(
                row.Id,
                row.RelationshipKind,
                row.Version,
                row.IsActive,
                row.ReleaseId,
                byRelease.GetValueOrDefault(row.ReleaseId, string.Empty),
                row.Meaning,
                row.RecordedBy,
                row.RecordedAt,
                row.ReAddedBy,
                row.ReAddedAt,
                row.TargetKind,
                row.TargetIdentityId,
                row.TargetOwnerIdentityId,
                row.TargetRevisionNumber,
                row.TargetStableIdentity,
                row.TargetDisplaySnapshot,
                row.InstanceBaseUrl,
                row.RemoteProjectId,
                row.RepositoryPathSnapshot ?? string.Empty,
                row.SourceSnapshotId,
                row.SourceSelectionEventId,
                row.MergeRequestIid,
                row.MergeRequestId,
                row.MergeRequestUrlSnapshot,
                row.MergeRequestTitleSnapshot,
                row.CommitSha,
                row.Path,
                row.StartLine,
                row.EndLine,
                row.FileMergeRequestIid))
            .OrderBy(x => x.ReleaseId).ThenByDescending(x => x.RecordedAt).ThenBy(x => x.Id)
            .ToArray();
        return new(references, read.LimitExceeded);
    }
}

internal sealed record RecordedCodeRelationshipProjectionResult(
    IReadOnlyList<RecordedCodeRelationship> Relationships,
    bool LimitExceeded);
