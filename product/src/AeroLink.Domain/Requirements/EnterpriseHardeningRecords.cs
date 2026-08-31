using AeroLink.Domain.Common;

namespace AeroLink.Domain.Requirements;

public enum ControlledAttachmentState { Active, Superseded, Withdrawn }
public enum ControlledAttachmentStorageOperationState { Pending, Available, RolledBack, RepairRequired, CleanedUp }
public enum EditSessionState { Active, Committed, Abandoned, Conflict, Expired, ForceUnlocked }
public enum IntegrityCheckpointState { Healthy, Attention, Failed }

public sealed class ControlledAttachment
{
    private ControlledAttachment() { }
    public ControlledAttachment(Guid projectId, string artifactType, Guid artifactId, Guid? revisionId, Guid logicalId, int version,
        string label, string description, string originalFileName, string contentType, long size, string sha256, string storageKey,
        Guid? supersedesId, string actor, DateTimeOffset now, string? validationProfile = null, string? validationResult = null)
    {
        if (size <= 0) throw new DomainException("An attachment cannot be empty.");
        if (version < 1) throw new DomainException("Attachment versions begin at one.");
        if (string.IsNullOrWhiteSpace(validationProfile) != string.IsNullOrWhiteSpace(validationResult))
            throw new DomainException("Attachment validation profile and result must be recorded together.");
        Id = Guid.NewGuid(); ProjectId = projectId; ArtifactType = Required(artifactType); ArtifactId = artifactId; RevisionId = revisionId;
        LogicalId = logicalId; Version = version; Label = Required(label); Description = description.Trim(); OriginalFileName = Required(originalFileName);
        ContentType = Required(contentType); Size = size; Sha256 = Required(sha256).ToLowerInvariant(); StorageKey = Required(storageKey);
        SupersedesId = supersedesId; State = ControlledAttachmentState.Active; UploadedBy = Required(actor); UploadedAt = now;
        ValidationProfile = validationProfile?.Trim(); ValidationResult = validationResult?.Trim();
    }
    public Guid Id { get; private set; }
    public Guid ProjectId { get; private set; }
    public string ArtifactType { get; private set; } = "";
    public Guid ArtifactId { get; private set; }
    public Guid? RevisionId { get; private set; }
    public Guid LogicalId { get; private set; }
    public int Version { get; private set; }
    public string Label { get; private set; } = "";
    public string Description { get; private set; } = "";
    public string OriginalFileName { get; private set; } = "";
    public string ContentType { get; private set; } = "";
    public long Size { get; private set; }
    public string Sha256 { get; private set; } = "";
    public string StorageKey { get; private set; } = "";
    public Guid? SupersedesId { get; private set; }
    public ControlledAttachmentState State { get; private set; }
    public string UploadedBy { get; private set; } = "";
    public DateTimeOffset UploadedAt { get; private set; }
    public DateTimeOffset? IntegrityVerifiedAt { get; private set; }
    public string? ValidationProfile { get; private set; }
    public string? ValidationResult { get; private set; }
    public void Supersede() { if (State == ControlledAttachmentState.Active) State = ControlledAttachmentState.Superseded; }
    public void Withdraw() { if (State != ControlledAttachmentState.Withdrawn) State = ControlledAttachmentState.Withdrawn; }
    /// <summary>
    /// Claims a browser-recovery image into the controlled Problem Report that now references it. A staged
    /// image is deliberately not historical evidence: until this transition it may expire and be reclaimed.
    /// Once claimed, its immutable file identity, uploader, timestamp, size and digest stay unchanged.
    /// </summary>
    public void ClaimInlineImage(Guid artifactId, Guid? revisionId)
    {
        if (State != ControlledAttachmentState.Active || ArtifactType != "InlineImageDraft" || ArtifactId != ProjectId)
            throw new DomainException("Only an active unclaimed inline image can be claimed.");
        if (artifactId == Guid.Empty || artifactId == ProjectId)
            throw new DomainException("A controlled artifact identifier is required to claim an inline image.");
        ArtifactType = "InlineImage"; ArtifactId = artifactId; RevisionId = revisionId;
    }
    public void RecordIntegrityVerification(DateTimeOffset now) => IntegrityVerifiedAt = now;
    private static string Required(string value) => string.IsNullOrWhiteSpace(value) ? throw new DomainException("A required attachment value is missing.") : value.Trim();
}

/// <summary>
/// Durable intent for an inline-image filesystem operation. The attachment row and promoted object are
/// committed separately because the filesystem cannot participate in the database transaction; this row is
/// the recovery fact that lets reconciliation distinguish a crash window from an unowned object.
/// </summary>
public sealed class ControlledAttachmentStorageOperation
{
    private ControlledAttachmentStorageOperation() { }

    public ControlledAttachmentStorageOperation(Guid id, Guid projectId, string artifactType, Guid artifactId,
        Guid? revisionId, Guid logicalId, int version, string label, string originalFileName, string contentType,
        long size, string sha256, string stagingKey, string storageKey, string actor, DateTimeOffset now,
        Guid? editSessionId = null)
    {
        if (id == Guid.Empty || projectId == Guid.Empty || logicalId == Guid.Empty)
            throw new DomainException("An inline-image storage operation requires stable identifiers.");
        if (size <= 0 || version < 1) throw new DomainException("An inline-image storage operation requires valid file metadata.");
        Id = id; ProjectId = projectId; ArtifactType = Required(artifactType); ArtifactId = artifactId;
        RevisionId = revisionId; EditSessionId = editSessionId; LogicalId = logicalId; Version = version; Label = Required(label);
        OriginalFileName = Required(originalFileName); ContentType = Required(contentType); Size = size;
        Sha256 = Required(sha256).ToLowerInvariant(); StagingKey = Required(stagingKey); StorageKey = Required(storageKey);
        Actor = Required(actor).ToLowerInvariant(); State = ControlledAttachmentStorageOperationState.Pending;
        CreatedAt = UpdatedAt = now; Detail = "Inline-image object is staged and awaiting controlled metadata commit.";
    }

    public Guid Id { get; private set; }
    public Guid ProjectId { get; private set; }
    public string ArtifactType { get; private set; } = "";
    public Guid ArtifactId { get; private set; }
    public Guid? RevisionId { get; private set; }
    public Guid? EditSessionId { get; private set; }
    public Guid LogicalId { get; private set; }
    public int Version { get; private set; }
    public string Label { get; private set; } = "";
    public string OriginalFileName { get; private set; } = "";
    public string ContentType { get; private set; } = "";
    public long Size { get; private set; }
    public string Sha256 { get; private set; } = "";
    public string StagingKey { get; private set; } = "";
    public string StorageKey { get; private set; } = "";
    public string Actor { get; private set; } = "";
    public ControlledAttachmentStorageOperationState State { get; private set; }
    public Guid? AttachmentId { get; private set; }
    public string Detail { get; private set; } = "";
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public DateTimeOffset? CompletedAt { get; private set; }

    public void Complete(Guid attachmentId, DateTimeOffset now)
    {
        if (attachmentId == Guid.Empty) throw new DomainException("A completed inline-image operation requires its attachment.");
        if (State is ControlledAttachmentStorageOperationState.RolledBack)
            throw new DomainException("A rolled-back inline-image operation cannot be completed.");
        AttachmentId = attachmentId; State = ControlledAttachmentStorageOperationState.Available;
        Detail = "The inline-image object and its controlled metadata are available.";
        UpdatedAt = now; CompletedAt = now;
    }

    public void RequireRepair(string detail, DateTimeOffset now)
    {
        if (State == ControlledAttachmentStorageOperationState.Available) return;
        State = ControlledAttachmentStorageOperationState.RepairRequired; Detail = Required(detail);
        UpdatedAt = now; CompletedAt = null;
    }

    public void RollBack(string detail, DateTimeOffset now)
    {
        if (State == ControlledAttachmentStorageOperationState.Available) return;
        State = ControlledAttachmentStorageOperationState.RolledBack; Detail = Required(detail);
        UpdatedAt = now; CompletedAt = now;
    }

    /// <summary>
    /// Completes a durable reclamation intent after the unreferenced filesystem object has been removed.
    /// Cleanup is deliberately a distinct terminal state: it must never be interpreted as an available
    /// controlled attachment by reconciliation after the database row has already been reclaimed.
    /// </summary>
    public void CompleteCleanup(DateTimeOffset now)
    {
        if (State is ControlledAttachmentStorageOperationState.Available
            or ControlledAttachmentStorageOperationState.RolledBack)
            return;
        State = ControlledAttachmentStorageOperationState.CleanedUp;
        Detail = "The expired recovery image and its database row were reclaimed.";
        UpdatedAt = now; CompletedAt = now;
    }

    private static string Required(string? value) => string.IsNullOrWhiteSpace(value)
        ? throw new DomainException("An inline-image storage operation value is required.") : value.Trim();
}

public sealed class ArtifactEditSession
{
    private ArtifactEditSession() { }
    public ArtifactEditSession(Guid projectId, string artifactType, Guid artifactId, Guid? revisionId, string baseSnapshotHash, string draftJson, string actor, DateTimeOffset now, bool exclusive=false, int leaseMinutes=15)
    { if(leaseMinutes is < 2 or > 120)throw new DomainException("Edit-session leases must be between 2 and 120 minutes.");Id = Guid.NewGuid(); ProjectId = projectId; ArtifactType = artifactType.Trim(); ArtifactId = artifactId; RevisionId = revisionId; BaseSnapshotHash = baseSnapshotHash; DraftJson = draftJson; UserName = actor.ToLowerInvariant(); State = EditSessionState.Active; OpenedAt = now; UpdatedAt = now; ExpiresAt=now.AddMinutes(leaseMinutes);IsExclusive=exclusive;LockKey=exclusive?$"{ArtifactType.ToLowerInvariant()}:{artifactId:N}":null;Version = 1; }
    public Guid Id { get; private set; }
    public Guid ProjectId { get; private set; }
    public string ArtifactType { get; private set; } = "";
    public Guid ArtifactId { get; private set; }
    public Guid? RevisionId { get; private set; }
    public string BaseSnapshotHash { get; private set; } = "";
    public string DraftJson { get; private set; } = "{}";
    public string UserName { get; private set; } = "";
    public EditSessionState State { get; private set; }
    public DateTimeOffset OpenedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public DateTimeOffset ExpiresAt { get; private set; }
    public DateTimeOffset? ClosedAt { get; private set; }
    public bool IsExclusive { get; private set; }
    public string? LockKey { get; private set; }
    public string? ClosedBy { get; private set; }
    public string? ClosedReason { get; private set; }
    public long Version { get; private set; }
    public void Save(string draftJson, long expectedVersion, DateTimeOffset now,int leaseMinutes=15) { EnsureActive(expectedVersion,now); DraftJson = draftJson; UpdatedAt = now;ExpiresAt=now.AddMinutes(leaseMinutes); Version++; }
    // Lease renewal deliberately does not advance the finalize token. The EF concurrency check on Version
    // still prevents a heartbeat from updating a session after a concurrent close wins.
    public void Heartbeat(long expectedVersion,DateTimeOffset now,int leaseMinutes=15){EnsureActive(expectedVersion,now);UpdatedAt=now;ExpiresAt=now.AddMinutes(leaseMinutes);}
    public void Close(EditSessionState state, long expectedVersion, DateTimeOffset now,string? actor=null,string? reason=null) { EnsureActive(expectedVersion,now); State = state; UpdatedAt = now; ClosedAt = now;ClosedBy=actor;ClosedReason=reason?.Trim();LockKey=null; Version++; }
    public void Expire(DateTimeOffset now){if(State!=EditSessionState.Active||ExpiresAt>now)return;State=EditSessionState.Expired;UpdatedAt=now;ClosedAt=now;ClosedReason="Inactive lock lease expired.";LockKey=null;Version++;}
    public void ForceUnlock(string actor,string reason,DateTimeOffset now){if(State!=EditSessionState.Active)throw new DomainException("Only an active edit session can be force-unlocked.");if(string.IsNullOrWhiteSpace(reason))throw new DomainException("A forced-unlock reason is required.");State=EditSessionState.ForceUnlocked;UpdatedAt=now;ClosedAt=now;ClosedBy=actor;ClosedReason=reason.Trim();LockKey=null;Version++;}
    private void EnsureActive(long expectedVersion,DateTimeOffset now) { if (Version != expectedVersion) throw new DomainException("The editing session changed; refresh before continuing."); if (State != EditSessionState.Active) throw new DomainException("This editing session is no longer active.");if(ExpiresAt<=now)throw new DomainException("This editing session expired because it was inactive."); }
}

public sealed class ArtifactDraftSnapshot
{
    private ArtifactDraftSnapshot() { }
    public ArtifactDraftSnapshot(Guid projectId,Guid sessionId,string artifactType,Guid artifactId,long sequence,string draftJson,string sha256,string actor,DateTimeOffset now)
    {if(sequence<1)throw new DomainException("Draft snapshot sequence must be positive.");Id=Guid.NewGuid();ProjectId=projectId;SessionId=sessionId;ArtifactType=artifactType.Trim();ArtifactId=artifactId;Sequence=sequence;DraftJson=draftJson;Sha256=sha256.ToLowerInvariant();CreatedBy=actor.ToLowerInvariant();CreatedAt=now;}
    public Guid Id { get; private set; }
    public Guid ProjectId { get; private set; }
    public Guid SessionId { get; private set; }
    public string ArtifactType { get; private set; }="";
    public Guid ArtifactId { get; private set; }
    public long Sequence { get; private set; }
    public string DraftJson { get; private set; }="{}";
    public string Sha256 { get; private set; }="";
    public string CreatedBy { get; private set; }="";
    public DateTimeOffset CreatedAt { get; private set; }
    public string? RestoredBy { get; private set; }
    public DateTimeOffset? RestoredAt { get; private set; }
    public void RecordRestore(string actor,DateTimeOffset now){RestoredBy=actor;RestoredAt=now;}
}

public sealed class ArtifactMergeConflict
{
    private ArtifactMergeConflict() { }
    public ArtifactMergeConflict(Guid projectId, Guid artifactId, Guid localSessionId, Guid competingSessionId, string baseJson, string localJson, string remoteJson, string actor, DateTimeOffset now)
    { Id = Guid.NewGuid(); ProjectId = projectId; ArtifactId = artifactId; LocalSessionId = localSessionId; CompetingSessionId = competingSessionId; BaseJson = baseJson; LocalJson = localJson; RemoteJson = remoteJson; CreatedBy = actor; CreatedAt = now; }
    public Guid Id { get; private set; }
    public Guid ProjectId { get; private set; }
    public Guid ArtifactId { get; private set; }
    public Guid LocalSessionId { get; private set; }
    public Guid CompetingSessionId { get; private set; }
    public string BaseJson { get; private set; } = "{}";
    public string LocalJson { get; private set; } = "{}";
    public string RemoteJson { get; private set; } = "{}";
    public string? ResolutionJson { get; private set; }
    public string CreatedBy { get; private set; } = "";
    public DateTimeOffset CreatedAt { get; private set; }
    public string? ResolvedBy { get; private set; }
    public DateTimeOffset? ResolvedAt { get; private set; }
    public void Resolve(string resolutionJson, string actor, DateTimeOffset now) { if (ResolvedAt is not null) throw new DomainException("This merge conflict is already resolved."); ResolutionJson = resolutionJson; ResolvedBy = actor; ResolvedAt = now; }
}

public sealed class EnterpriseIntegrityCheckpoint
{
    private EnterpriseIntegrityCheckpoint() { }
    public EnterpriseIntegrityCheckpoint(Guid projectId, int artifacts, int revisions, int attachments, long attachmentBytes, int failedJobs, int openConflicts, string manifestHash, IntegrityCheckpointState state, string detail, string actor, DateTimeOffset now)
    { Id = Guid.NewGuid(); ProjectId = projectId; ArtifactCount = artifacts; RevisionCount = revisions; AttachmentCount = attachments; AttachmentBytes = attachmentBytes; FailedJobCount = failedJobs; OpenConflictCount = openConflicts; ManifestHash = manifestHash; State = state; Detail = detail; CreatedBy = actor; CreatedAt = now; }
    public Guid Id { get; private set; }
    public Guid ProjectId { get; private set; }
    public int ArtifactCount { get; private set; }
    public int RevisionCount { get; private set; }
    public int AttachmentCount { get; private set; }
    public long AttachmentBytes { get; private set; }
    public int FailedJobCount { get; private set; }
    public int OpenConflictCount { get; private set; }
    public string ManifestHash { get; private set; } = "";
    public IntegrityCheckpointState State { get; private set; }
    public string Detail { get; private set; } = "";
    public string CreatedBy { get; private set; } = "";
    public DateTimeOffset CreatedAt { get; private set; }
}
