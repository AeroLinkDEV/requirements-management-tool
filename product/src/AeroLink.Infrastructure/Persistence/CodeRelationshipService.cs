using AeroLink.Domain.Common;
using AeroLink.Domain.Identity;
using AeroLink.Domain.Integrations;
using AeroLink.Domain.Releases;
using Microsoft.EntityFrameworkCore;

namespace AeroLink.Infrastructure.Persistence;

/// <summary>Bounded persistence operations for independently attributed Code relationships.</summary>
public sealed class CodeRelationshipService(AeroLinkDbContext db)
{
    private static readonly ProgramRole[] MutationRoles =
        [ProgramRole.Engineer, ProgramRole.ConfigurationManager, ProgramRole.ProgramManager];

    public static IReadOnlyList<ProgramRole> AllowedMutationRoles => MutationRoles;

    public async Task<CodeRelationshipPage> ReadPageAsync(Guid projectId, Guid? releaseId,
        CodeRelationshipKind? kind, CodeRelationshipTargetKind? targetKind, Guid? targetId,
        int page, int pageSize, bool includeWithdrawn, CancellationToken ct)
    {
        if (projectId == Guid.Empty || page is < 1 or > 100_000 || pageSize is < 1 or > 100)
            throw new DomainException("Choose a valid relationship page between 1 and 100000 and a page size between 1 and 100.");
        var mergeQuery = db.GitLabMergeRequestRelationships.AsNoTracking()
            .Where(x => x.ProjectId == projectId && (!releaseId.HasValue || x.ReleaseId == releaseId.Value)
                && (includeWithdrawn || x.IsActive) && (!targetKind.HasValue || x.TargetKind == targetKind.Value)
                && (!targetId.HasValue || x.TargetIdentityId == targetId.Value));
        var fileQuery = db.GitLabFileRelationships.AsNoTracking()
            .Where(x => x.ProjectId == projectId && (!releaseId.HasValue || x.ReleaseId == releaseId.Value)
                && (includeWithdrawn || x.IsActive) && (!targetKind.HasValue || x.TargetKind == targetKind.Value)
                && (!targetId.HasValue || x.TargetIdentityId == targetId.Value));
        var mergeCount = kind is null or CodeRelationshipKind.MergeRequest ? await mergeQuery.CountAsync(ct) : 0;
        var fileCount = kind is null or CodeRelationshipKind.File ? await fileQuery.CountAsync(ct) : 0;
        var total = mergeCount + fileCount;
        if (total > 10_000)
            throw new DomainException("The relationship result exceeds the bounded read limit; narrow the release, target or kind filter.");

        // Keep the set operation ahead of the final record projection. EF Core can translate
        // this common scalar shape to UNION ALL on PostgreSQL; projecting each entity directly
        // to the record before Concat is rejected by the relational translator.
        var mergeRows = mergeQuery.Select(x => new
        {
            Id = x.Id, RelationshipKind = x.RelationshipKind, ProjectId = x.ProjectId, ReleaseId = x.ReleaseId,
            InstanceBaseUrl = x.InstanceBaseUrl, RemoteProjectId = x.RemoteProjectId, IsActive = x.IsActive,
            Version = x.Version, TargetKind = x.TargetKind, TargetIdentityId = x.TargetIdentityId,
            TargetStableIdentity = x.TargetStableIdentity, TargetDisplaySnapshot = x.TargetDisplaySnapshot,
            Meaning = x.Meaning, RecordedBy = x.RecordedBy, RecordedAt = x.RecordedAt,
            WithdrawnAt = x.WithdrawnAt, WithdrawnBy = x.WithdrawnBy, WithdrawalRationale = x.WithdrawalRationale,
            ReAddedBy = x.ReAddedBy, ReAddedAt = x.ReAddedAt, SourceSnapshotId = (Guid?)x.SourceSnapshotId,
            SourceSelectionEventId = x.SourceSelectionEventId, MergeRequestIid = (int?)x.MergeRequestIid,
            MergeRequestId = x.MergeRequestId, MergeRequestUrlSnapshot = (string?)x.MergeRequestUrlSnapshot,
            MergeRequestTitleSnapshot = (string?)x.MergeRequestTitleSnapshot, CommitSha = (string?)null,
            Path = (string?)null, StartLine = (int?)null, EndLine = (int?)null,
            FileMergeRequestIid = (int?)null, RepositoryPathSnapshot = (string?)x.RepositoryPathSnapshot
        });
        var fileRows = fileQuery.Select(x => new
        {
            Id = x.Id, RelationshipKind = x.RelationshipKind, ProjectId = x.ProjectId, ReleaseId = x.ReleaseId,
            InstanceBaseUrl = x.InstanceBaseUrl, RemoteProjectId = x.RemoteProjectId, IsActive = x.IsActive,
            Version = x.Version, TargetKind = x.TargetKind, TargetIdentityId = x.TargetIdentityId,
            TargetStableIdentity = x.TargetStableIdentity, TargetDisplaySnapshot = x.TargetDisplaySnapshot,
            Meaning = x.Meaning, RecordedBy = x.RecordedBy, RecordedAt = x.RecordedAt,
            WithdrawnAt = x.WithdrawnAt, WithdrawnBy = x.WithdrawnBy, WithdrawalRationale = x.WithdrawalRationale,
            ReAddedBy = x.ReAddedBy, ReAddedAt = x.ReAddedAt, SourceSnapshotId = (Guid?)x.SourceSnapshotId,
            SourceSelectionEventId = x.SourceSelectionEventId, MergeRequestIid = (int?)null,
            MergeRequestId = (long?)null, MergeRequestUrlSnapshot = (string?)null,
            MergeRequestTitleSnapshot = (string?)null, CommitSha = (string?)x.CommitSha, Path = (string?)x.Path,
            StartLine = x.StartLine, EndLine = x.EndLine, FileMergeRequestIid = x.MergeRequestIid,
            RepositoryPathSnapshot = (string?)null
        });
        var combined = kind switch
        {
            CodeRelationshipKind.MergeRequest => mergeRows,
            CodeRelationshipKind.File => fileRows,
            _ => mergeRows.Concat(fileRows)
        };
        CodeRelationshipReadRow[] items;
        if (db.Database.IsSqlite())
        {
            // SQLite stores DateTimeOffset values as text and intentionally rejects ordering by them.
            // The bounded load preserves the same global ordering used by PostgreSQL without silently
            // truncating a later page.
            var all = (await combined.ToListAsync(ct)).Select(x => new CodeRelationshipReadRow(
                x.Id, x.RelationshipKind, x.ProjectId, x.ReleaseId, x.InstanceBaseUrl, x.RemoteProjectId,
                x.IsActive, x.Version, x.TargetKind, x.TargetIdentityId, x.TargetStableIdentity,
                x.TargetDisplaySnapshot, x.Meaning, x.RecordedBy, x.RecordedAt, x.WithdrawnAt, x.WithdrawnBy,
                x.WithdrawalRationale, x.ReAddedBy, x.ReAddedAt, x.SourceSnapshotId, x.SourceSelectionEventId,
                x.MergeRequestIid, x.MergeRequestId, x.MergeRequestUrlSnapshot, x.MergeRequestTitleSnapshot,
                x.CommitSha, x.Path, x.StartLine, x.EndLine, x.FileMergeRequestIid, x.RepositoryPathSnapshot)).ToList();
            items = all.OrderByDescending(x => x.RecordedAt).ThenBy(x => x.Id)
                .Skip((page - 1) * pageSize).Take(pageSize).ToArray();
        }
        else
        {
            items = await combined.Select(x => new CodeRelationshipReadRow(
                x.Id, x.RelationshipKind, x.ProjectId, x.ReleaseId, x.InstanceBaseUrl, x.RemoteProjectId,
                x.IsActive, x.Version, x.TargetKind, x.TargetIdentityId, x.TargetStableIdentity,
                x.TargetDisplaySnapshot, x.Meaning, x.RecordedBy, x.RecordedAt, x.WithdrawnAt, x.WithdrawnBy,
                x.WithdrawalRationale, x.ReAddedBy, x.ReAddedAt, x.SourceSnapshotId, x.SourceSelectionEventId,
                x.MergeRequestIid, x.MergeRequestId, x.MergeRequestUrlSnapshot, x.MergeRequestTitleSnapshot,
                x.CommitSha, x.Path, x.StartLine, x.EndLine, x.FileMergeRequestIid, x.RepositoryPathSnapshot))
                .OrderByDescending(x => x.RecordedAt).ThenBy(x => x.Id)
                .Skip((page - 1) * pageSize).Take(pageSize).ToArrayAsync(ct);
        }
        return new(page, pageSize, total, items);
    }

    public async Task<IReadOnlyList<GitLabCodeRelationshipEvent>> ReadHistoryAsync(Guid projectId,
        CodeRelationshipKind kind, Guid relationshipId, CancellationToken ct)
    {
        var exists = kind == CodeRelationshipKind.MergeRequest
            ? await db.GitLabMergeRequestRelationships.AsNoTracking().AnyAsync(x => x.ProjectId == projectId && x.Id == relationshipId, ct)
            : await db.GitLabFileRelationships.AsNoTracking().AnyAsync(x => x.ProjectId == projectId && x.Id == relationshipId, ct);
        if (!exists) throw new KeyNotFoundException("The code relationship was not found.");
        var events = await db.GitLabCodeRelationshipEvents.AsNoTracking()
            .Where(x => x.RelationshipKind == kind && x.RelationshipId == relationshipId)
            .ToListAsync(ct);
        return events.OrderBy(x => x.OccurredAt).ThenBy(x => x.Id).ToArray();
    }

    public async Task<CodeRelationshipMutation> AddMergeRequestAsync(
        ProjectControlledWriteScope scope,
        ProjectRepositoryConfiguration configuration,
        string instanceBaseUrl,
        GitLabMergeRequestDetails observed,
        Guid releaseId,
        Guid? sourceSnapshotId,
        Guid? sourceSelectionEventId,
        CodeRelationshipTarget target,
        CodeRelationshipMeaning meaning,
        string actor,
        DateTimeOffset now,
        CancellationToken ct)
    {
        ProjectControlledWriteScope.Require(db, scope.ProjectId, scope);
        await EnsureMutableReleaseAsync(scope.ProjectId, releaseId, ct);
        ValidateConfiguration(configuration, scope.ProjectId, instanceBaseUrl, observed.ProjectId);
        if (observed.Iid <= 0 || observed.ProjectId != configuration.RemoteProjectId)
            throw new DomainException("The observed merge request does not belong to the verified repository.");

        await ValidateSourceContextAsync(scope.ProjectId, releaseId, configuration, instanceBaseUrl,
            sourceSnapshotId, sourceSelectionEventId, expectedCommitSha: null, selectionEventRequired: false, ct);

        var existing = await db.GitLabMergeRequestRelationships
            .FirstOrDefaultAsync(x => x.ProjectId == scope.ProjectId && x.ReleaseId == releaseId
                && x.InstanceBaseUrl == instanceBaseUrl && x.RemoteProjectId == observed.ProjectId
                && x.MergeRequestIid == observed.Iid && x.TargetKind == target.Kind
                && x.TargetIdentityId == target.ExactIdentityId && x.Meaning == meaning, ct);
        if (existing is not null)
        {
            if (existing.IsActive)
                return CodeRelationshipMutation.Unchanged(existing.RelationshipKind, existing.Id, existing.Version, true);
            throw new DomainException("This code relationship was withdrawn. Use the explicit re-add command with its expected version.");
        }

        var relationship = new GitLabMergeRequestRelationship(scope.ProjectId, releaseId, instanceBaseUrl,
            observed.ProjectId, observed.Iid, observed.Id, sourceSnapshotId, sourceSelectionEventId,
            configuration.RemotePathWithNamespace!, observed.WebUrl, observed.Title, target, meaning, actor, now);
        db.GitLabMergeRequestRelationships.Add(relationship);
        db.GitLabCodeRelationshipEvents.Add(new(CodeRelationshipKind.MergeRequest, relationship.Id,
            CodeRelationshipEventKind.Added, actor, now));
        return CodeRelationshipMutation.Applied(relationship.RelationshipKind, relationship.Id, relationship.Version, true);
    }

    public async Task<CodeRelationshipMutation> AddFileAsync(
        ProjectControlledWriteScope scope,
        ProjectRepositoryConfiguration configuration,
        string instanceBaseUrl,
        long remoteProjectId,
        Guid releaseId,
        Guid sourceSnapshotId,
        Guid? sourceSelectionEventId,
        string commitSha,
        string path,
        int? startLine,
        int? endLine,
        int? mergeRequestIid,
        CodeRelationshipTarget target,
        CodeRelationshipMeaning meaning,
        string actor,
        DateTimeOffset now,
        CancellationToken ct)
    {
        ProjectControlledWriteScope.Require(db, scope.ProjectId, scope);
        await EnsureMutableReleaseAsync(scope.ProjectId, releaseId, ct);
        ValidateConfiguration(configuration, scope.ProjectId, instanceBaseUrl, remoteProjectId);
        await ValidateSourceContextAsync(scope.ProjectId, releaseId, configuration, instanceBaseUrl,
            sourceSnapshotId, sourceSelectionEventId, commitSha, selectionEventRequired: false, ct);

        var existing = await db.GitLabFileRelationships
            .FirstOrDefaultAsync(x => x.ProjectId == scope.ProjectId && x.ReleaseId == releaseId
                && x.InstanceBaseUrl == instanceBaseUrl && x.RemoteProjectId == remoteProjectId
                && x.SourceSnapshotId == sourceSnapshotId && x.CommitSha == commitSha
                && x.Path == path && x.StartLine == startLine && x.EndLine == endLine
                && x.MergeRequestIid == mergeRequestIid && x.TargetKind == target.Kind
                && x.TargetIdentityId == target.ExactIdentityId && x.Meaning == meaning, ct);
        if (existing is not null)
        {
            if (existing.IsActive)
                return CodeRelationshipMutation.Unchanged(existing.RelationshipKind, existing.Id, existing.Version, true);
            throw new DomainException("This code relationship was withdrawn. Use the explicit re-add command with its expected version.");
        }

        var relationship = new GitLabFileRelationship(scope.ProjectId, releaseId, instanceBaseUrl,
            remoteProjectId, sourceSnapshotId, sourceSelectionEventId, commitSha, path, startLine, endLine,
            mergeRequestIid, target, meaning, actor, now);
        db.GitLabFileRelationships.Add(relationship);
        db.GitLabCodeRelationshipEvents.Add(new(CodeRelationshipKind.File, relationship.Id,
            CodeRelationshipEventKind.Added, actor, now));
        return CodeRelationshipMutation.Applied(relationship.RelationshipKind, relationship.Id, relationship.Version, true);
    }

    public async Task<CodeRelationshipMutation> WithdrawAsync(ProjectControlledWriteScope scope,
        CodeRelationshipKind kind, Guid relationshipId, long expectedVersion, string rationale,
        string actor, DateTimeOffset now, CancellationToken ct)
    {
        ProjectControlledWriteScope.Require(db, scope.ProjectId, scope);
        await EnsureRelationshipReleaseMutableAsync(scope.ProjectId, kind, relationshipId, ct);
        if (kind == CodeRelationshipKind.MergeRequest)
        {
            var row = await db.GitLabMergeRequestRelationships.SingleOrDefaultAsync(x => x.Id == relationshipId && x.ProjectId == scope.ProjectId, ct)
                ?? throw new KeyNotFoundException("The code relationship was not found.");
            row.Withdraw(expectedVersion, actor, rationale, now);
            db.GitLabCodeRelationshipEvents.Add(new(kind, row.Id, CodeRelationshipEventKind.Withdrawn, actor, now, rationale));
            return CodeRelationshipMutation.Applied(kind, row.Id, row.Version, false);
        }

        var file = await db.GitLabFileRelationships.SingleOrDefaultAsync(x => x.Id == relationshipId && x.ProjectId == scope.ProjectId, ct)
            ?? throw new KeyNotFoundException("The code relationship was not found.");
        file.Withdraw(expectedVersion, actor, rationale, now);
        db.GitLabCodeRelationshipEvents.Add(new(kind, file.Id, CodeRelationshipEventKind.Withdrawn, actor, now, rationale));
        return CodeRelationshipMutation.Applied(kind, file.Id, file.Version, false);
    }

    public async Task<CodeRelationshipMutation> ReAddAsync(ProjectControlledWriteScope scope,
        CodeRelationshipKind kind, Guid relationshipId, long expectedVersion, string actor,
        DateTimeOffset now, CancellationToken ct)
    {
        ProjectControlledWriteScope.Require(db, scope.ProjectId, scope);
        await EnsureRelationshipReleaseMutableAsync(scope.ProjectId, kind, relationshipId, ct);
        if (kind == CodeRelationshipKind.MergeRequest)
        {
            var row = await db.GitLabMergeRequestRelationships.SingleOrDefaultAsync(x => x.Id == relationshipId && x.ProjectId == scope.ProjectId, ct)
                ?? throw new KeyNotFoundException("The code relationship was not found.");
            row.ReAdd(expectedVersion, actor, now);
            db.GitLabCodeRelationshipEvents.Add(new(kind, row.Id, CodeRelationshipEventKind.ReAdded, actor, now));
            return CodeRelationshipMutation.Applied(kind, row.Id, row.Version, true);
        }

        var file = await db.GitLabFileRelationships.SingleOrDefaultAsync(x => x.Id == relationshipId && x.ProjectId == scope.ProjectId, ct)
            ?? throw new KeyNotFoundException("The code relationship was not found.");
        file.ReAdd(expectedVersion, actor, now);
        db.GitLabCodeRelationshipEvents.Add(new(kind, file.Id, CodeRelationshipEventKind.ReAdded, actor, now));
        return CodeRelationshipMutation.Applied(kind, file.Id, file.Version, true);
    }

    private async Task EnsureMutableReleaseAsync(Guid projectId, Guid releaseId, CancellationToken ct)
    {
        var release = await db.Releases.AsNoTracking().SingleOrDefaultAsync(x => x.Id == releaseId && x.ProjectId == projectId, ct)
            ?? throw new KeyNotFoundException("The implementation release was not found.");
        if (release.IsReleased) throw new DomainException("The implementation release is released and its code relationships are immutable.");
        if (await db.ReleaseCampaigns.AsNoTracking().AnyAsync(x => x.ProjectId == projectId && x.ReleaseId == releaseId
                && (x.State == ReleaseCampaignState.InReview || x.State == ReleaseCampaignState.Released), ct))
            throw new DomainException("The release package is frozen or released and cannot accept code relationship changes.");
    }

    private async Task EnsureRelationshipReleaseMutableAsync(Guid projectId, CodeRelationshipKind kind,
        Guid relationshipId, CancellationToken ct)
    {
        Guid releaseId = kind == CodeRelationshipKind.MergeRequest
            ? await db.GitLabMergeRequestRelationships.Where(x => x.Id == relationshipId && x.ProjectId == projectId).Select(x => x.ReleaseId).SingleOrDefaultAsync(ct)
            : await db.GitLabFileRelationships.Where(x => x.Id == relationshipId && x.ProjectId == projectId).Select(x => x.ReleaseId).SingleOrDefaultAsync(ct);
        if (releaseId == Guid.Empty) throw new KeyNotFoundException("The code relationship was not found.");
        await EnsureMutableReleaseAsync(projectId, releaseId, ct);
    }

    private static void ValidateConfiguration(ProjectRepositoryConfiguration configuration, Guid projectId,
        string instanceBaseUrl, long remoteProjectId)
    {
        if (configuration.ProjectId != projectId || configuration.Status != ProjectRepositorySetupStatus.Verified
            || configuration.RemoteProjectId != remoteProjectId || string.IsNullOrWhiteSpace(configuration.RemotePathWithNamespace))
            throw new DomainException("The verified repository configuration changed while the relationship was being recorded.");
        if (!string.Equals(configuration.Provider, "GitLab", StringComparison.OrdinalIgnoreCase))
            throw new DomainException("The verified repository provider is not GitLab.");
        if (string.IsNullOrWhiteSpace(instanceBaseUrl)) throw new DomainException("The GitLab instance identity is required.");
    }

    private async Task ValidateSourceContextAsync(Guid projectId, Guid releaseId,
        ProjectRepositoryConfiguration configuration, string instanceBaseUrl, Guid? snapshotId,
        Guid? selectionEventId, string? expectedCommitSha, bool selectionEventRequired, CancellationToken ct)
    {
        if (!snapshotId.HasValue && selectionEventId.HasValue)
            throw new DomainException("A source context must include both its snapshot and selection event.");
        if (!snapshotId.HasValue)
        {
            if (expectedCommitSha is not null)
                throw new DomainException("A file relationship requires a source snapshot.");
            return;
        }

        var snapshot = await db.GitLabSourceSnapshots.AsNoTracking().SingleOrDefaultAsync(x => x.ProjectId == projectId && x.Id == snapshotId.Value, ct)
            ?? throw new DomainException("The source snapshot is not part of this project.");
        if (snapshot.RemoteProjectId != configuration.RemoteProjectId
            || !string.Equals(snapshot.InstanceBaseUrl, instanceBaseUrl, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(snapshot.PathWithNamespace, configuration.RemotePathWithNamespace, StringComparison.Ordinal))
            throw new DomainException("The source snapshot does not belong to the verified repository identity.");
        if (selectionEventRequired && !selectionEventId.HasValue)
            throw new DomainException("This relationship source context requires a source selection event.");
        if (selectionEventId.HasValue)
        {
            _ = await db.GitLabSourceSelectionEvents.AsNoTracking().SingleOrDefaultAsync(x => x.ProjectId == projectId
                && x.ReleaseId == releaseId && x.Id == selectionEventId.Value && x.SourceSnapshotId == snapshot.Id, ct)
                ?? throw new DomainException("The source selection event does not bind the requested source snapshot to this release.");
        }
        if (expectedCommitSha is not null && !string.Equals(snapshot.CommitSha, expectedCommitSha, StringComparison.OrdinalIgnoreCase))
            throw new DomainException("The file commit must match the immutable source snapshot.");
    }
}

public sealed record CodeRelationshipMutation(CodeRelationshipKind RelationshipKind, Guid RelationshipId,
    long Version, bool IsActive, bool Changed)
{
    public static CodeRelationshipMutation Applied(CodeRelationshipKind kind, Guid id, long version, bool active) =>
        new(kind, id, version, active, true);

    public static CodeRelationshipMutation Unchanged(CodeRelationshipKind kind, Guid id, long version, bool active) =>
        new(kind, id, version, active, false);
}

public sealed record CodeRelationshipPage(int Page, int PageSize, int Total,
    IReadOnlyList<CodeRelationshipReadRow> Items);

public sealed record CodeRelationshipReadRow(Guid Id, CodeRelationshipKind RelationshipKind,
    Guid ProjectId, Guid ReleaseId, string InstanceBaseUrl, long RemoteProjectId, bool IsActive, long Version,
    CodeRelationshipTargetKind TargetKind, Guid TargetIdentityId, string TargetStableIdentity,
    string TargetDisplaySnapshot, CodeRelationshipMeaning Meaning, string RecordedBy, DateTimeOffset RecordedAt,
    DateTimeOffset? WithdrawnAt, string? WithdrawnBy, string? WithdrawalRationale, string? ReAddedBy,
    DateTimeOffset? ReAddedAt, Guid? SourceSnapshotId, Guid? SourceSelectionEventId, int? MergeRequestIid,
    long? MergeRequestId, string? MergeRequestUrlSnapshot, string? MergeRequestTitleSnapshot, string? CommitSha,
    string? Path, int? StartLine, int? EndLine, int? FileMergeRequestIid, string? RepositoryPathSnapshot);
