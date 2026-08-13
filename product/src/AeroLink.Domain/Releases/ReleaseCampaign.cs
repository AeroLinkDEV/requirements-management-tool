using AeroLink.Domain.Common;

namespace AeroLink.Domain.Releases;

public enum ReleaseCampaignState { Planning, Verification, InReview, Released }
public enum ImpactKind { Requirement, Traceability, Verification, Document }
public enum ImpactDispositionState { Pending, Addressed, NotApplicable }
public enum ReleaseApprovalState { Pending, Active, Approved, Cancelled }

public sealed class ReleaseCampaign
{
    private readonly List<ReleaseCampaignEvent> _events = [];
    private readonly List<ReleaseApproval> _approvals = [];
    private ReleaseCampaign() { }
    public ReleaseCampaign(Guid projectId, Guid releaseId, Guid baselineId, string name, string ownerId, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(ownerId)) throw new DomainException("Campaign name and owner are required.");
        Id = Guid.NewGuid(); ProjectId = projectId; ReleaseId = releaseId; BaselineId = baselineId; Name = name.Trim(); OwnerId = ownerId.Trim(); State = ReleaseCampaignState.Planning; CreatedAt = now; UpdatedAt = now;
        Event("CampaignCreated", ownerId, $"Created release campaign {Name}.", now);
    }
    public Guid Id { get; private set; }
    public Guid ProjectId { get; private set; }
    public Guid ReleaseId { get; private set; }
    public Guid BaselineId { get; private set; }
    public Guid? SoftwareBuildId { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public string OwnerId { get; private set; } = string.Empty;
    public ReleaseCampaignState State { get; private set; }
    public long Version { get; private set; } = 1;
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public DateTimeOffset? ReleasedAt { get; private set; }
    public string? ReleaseHash { get; private set; }
    public IReadOnlyCollection<ReleaseCampaignEvent> Events => _events.AsReadOnly();
    public IReadOnlyCollection<ReleaseApproval> Approvals => _approvals.AsReadOnly();
    public void StartVerification(string actorId, DateTimeOffset now)
    {
        if (State != ReleaseCampaignState.Planning) throw new DomainException("Only a planning campaign can start verification.");
        State = ReleaseCampaignState.Verification; Event("VerificationStarted", actorId, "Started release verification campaign.", now); Touch(now);
    }
    public void SelectVerificationBuild(Guid softwareBuildId, string actorId, DateTimeOffset now)
    { EnsurePackageMutable(); SoftwareBuildId = softwareBuildId; Event("VerificationBuildSelected", actorId, $"Selected software build {softwareBuildId} for release verification.", now); Touch(now); }
    public void RecordExecutionProgress(string eventType, string detail, string actorId, DateTimeOffset now)
    { EnsurePackageMutable(); if (string.IsNullOrWhiteSpace(eventType) || string.IsNullOrWhiteSpace(detail)) throw new DomainException("Release progress type and detail are required."); Event(eventType.Trim(), actorId, detail.Trim(), now); Touch(now); }
    public void BeginReleaseReview(string actorId, IReadOnlyList<(string Id, string Name)> approvers, string manifestHash, DateTimeOffset now)
    {
        if (State != ReleaseCampaignState.Verification) throw new DomainException("Release review can begin only after verification is complete.");
        if (approvers.Count == 0) throw new DomainException("At least one release approver is required.");
        if (string.IsNullOrWhiteSpace(manifestHash) || manifestHash.Length != 64) throw new DomainException("A valid release review manifest hash is required.");
        if (_approvals.Any(x => x.State != ReleaseApprovalState.Cancelled)) throw new DomainException("Release review has already been configured.");
        var cycle = _approvals.Select(x => x.Cycle).DefaultIfEmpty(0).Max() + 1;
        for (var i = 0; i < approvers.Count; i++) _approvals.Add(new ReleaseApproval(Id, cycle, i, approvers[i].Id, approvers[i].Name, i == 0 ? ReleaseApprovalState.Active : ReleaseApprovalState.Pending));
        ReleaseHash = manifestHash; State = ReleaseCampaignState.InReview;
        Event("ReleaseReviewStarted", actorId, $"Started ordered release review with {approvers.Count} approvers against manifest SHA-256 {manifestHash}.", now); Touch(now);
    }
    public bool Approve(string actorId, DateTimeOffset now)
    {
        if (State != ReleaseCampaignState.InReview) throw new DomainException("The release campaign is not in review.");
        var active = _approvals.SingleOrDefault(x => x.State == ReleaseApprovalState.Active) ?? throw new DomainException("No release approval is active.");
        if (!string.Equals(active.ApproverId, actorId, StringComparison.OrdinalIgnoreCase)) throw new DomainException("Only the active release approver can approve.");
        active.Approve(now); var next = _approvals.Where(x => x.State == ReleaseApprovalState.Pending).OrderBy(x => x.Position).FirstOrDefault(); next?.Activate();
        Event("ReleaseApprovalRecorded", actorId, $"Approved release review position {active.Position + 1}.", now); Touch(now); return next is null;
    }
    public void CancelReleaseReview(string actorId, string? reason, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(actorId)) throw new DomainException("A review cancellation actor is required.");
        if (State != ReleaseCampaignState.InReview) throw new DomainException("Only a campaign in release review can be cancelled.");
        foreach (var approval in _approvals.Where(x => x.State != ReleaseApprovalState.Cancelled))
            approval.Cancel();
        ReleaseHash = null; State = ReleaseCampaignState.Verification;
        Event("ReleaseReviewCancelled", actorId, string.IsNullOrWhiteSpace(reason) ? "Release review cancelled; the package must be re-frozen and re-reviewed." : reason.Trim(), now);
        Touch(now);
    }
    public void Release(Guid softwareBuildId, string releaseHash, string actorId, DateTimeOffset now)
    {
        if (State != ReleaseCampaignState.InReview || _approvals.Any(x => x.State != ReleaseApprovalState.Approved)) throw new DomainException("Every configured release approver must approve before release.");
        if (string.IsNullOrWhiteSpace(releaseHash) || releaseHash.Length != 64) throw new DomainException("A valid release hash is required.");
        if (!string.Equals(ReleaseHash, releaseHash, StringComparison.OrdinalIgnoreCase)) throw new DomainException("The release package changed after review began. Cancel and restart release review against the current manifest.");
        if (SoftwareBuildId != softwareBuildId) throw new DomainException("The reviewed verification build does not match the release build.");
        ReleasedAt = now; State = ReleaseCampaignState.Released;
        Event("ReleaseCompleted", actorId, $"Released campaign with software build {softwareBuildId} and hash {releaseHash}.", now); Touch(now);
    }
    private void Touch(DateTimeOffset now) { Version++; UpdatedAt = now; }
    private void EnsureNotReleased() { if (State == ReleaseCampaignState.Released) throw new DomainException("A released campaign is immutable."); }
    private void EnsurePackageMutable()
    {
        if (State == ReleaseCampaignState.InReview) throw new DomainException("The release package is frozen while approval is in progress.");
        EnsureNotReleased();
    }
    private void Event(string type, string actor, string detail, DateTimeOffset now) => _events.Add(new ReleaseCampaignEvent(Id, type, actor, detail, now));
}

public sealed class ReleaseApproval
{
    private ReleaseApproval() { }
    internal ReleaseApproval(Guid campaignId, int cycle, int position, string approverId, string approverName, ReleaseApprovalState state)
    { Id = Guid.NewGuid(); CampaignId = campaignId; Cycle = cycle; Position = position; ApproverId = approverId; ApproverName = approverName; State = state; }
    public Guid Id { get; private set; } public Guid CampaignId { get; private set; } public int Cycle { get; private set; } public int Position { get; private set; }
    public string ApproverId { get; private set; } = string.Empty; public string ApproverName { get; private set; } = string.Empty;
    public ReleaseApprovalState State { get; private set; } public DateTimeOffset? ApprovedAt { get; private set; }
    internal void Activate() => State = ReleaseApprovalState.Active;
    internal void Approve(DateTimeOffset now) { State = ReleaseApprovalState.Approved; ApprovedAt = now; }
    internal void Cancel() => State = ReleaseApprovalState.Cancelled;
}

public sealed class ReleaseCampaignEvent
{
    private ReleaseCampaignEvent() { }
    internal ReleaseCampaignEvent(Guid campaignId, string eventType, string actorId, string detail, DateTimeOffset occurredAt)
    { Id = Guid.NewGuid(); CampaignId = campaignId; EventType = eventType; ActorId = actorId; Detail = detail; OccurredAt = occurredAt; }
    public Guid Id { get; private set; } public Guid CampaignId { get; private set; } public string EventType { get; private set; } = string.Empty;
    public string ActorId { get; private set; } = string.Empty; public string Detail { get; private set; } = string.Empty; public DateTimeOffset OccurredAt { get; private set; }
}

public sealed class ChangeImpactDisposition
{
    private ChangeImpactDisposition() { }
    public ChangeImpactDisposition(Guid campaignId, Guid changeRequestId, ImpactKind kind, string artifactReference, string description)
    { Id = Guid.NewGuid(); CampaignId = campaignId; ChangeRequestId = changeRequestId; Kind = kind; ArtifactReference = artifactReference.Trim(); Description = description.Trim(); State = ImpactDispositionState.Pending; }
    public Guid Id { get; private set; } public Guid CampaignId { get; private set; } public Guid ChangeRequestId { get; private set; }
    public ImpactKind Kind { get; private set; } public string ArtifactReference { get; private set; } = string.Empty; public string Description { get; private set; } = string.Empty;
    public ImpactDispositionState State { get; private set; } public string Rationale { get; private set; } = string.Empty; public string? DispositionedBy { get; private set; } public DateTimeOffset? DispositionedAt { get; private set; }
    public void Disposition(ImpactDispositionState state, string rationale, string actorId, DateTimeOffset now)
    { if (state == ImpactDispositionState.Pending) throw new DomainException("Choose Addressed or Not Applicable."); if (string.IsNullOrWhiteSpace(rationale)) throw new DomainException("Disposition rationale is required."); State = state; Rationale = rationale.Trim(); DispositionedBy = actorId; DispositionedAt = now; }
}

public sealed class EvidenceRecord
{
    private EvidenceRecord() { }
    public EvidenceRecord(Guid projectId, string originalFileName, string contentType, long size, string sha256, string storageKey, string uploadedBy, DateTimeOffset uploadedAt)
    { Id = Guid.NewGuid(); ProjectId = projectId; OriginalFileName = originalFileName.Trim(); ContentType = contentType.Trim(); Size = size; Sha256 = sha256; StorageKey = storageKey; UploadedBy = uploadedBy.Trim(); UploadedAt = uploadedAt; }
    public Guid Id { get; private set; } public Guid ProjectId { get; private set; } public string OriginalFileName { get; private set; } = string.Empty;
    public string ContentType { get; private set; } = string.Empty; public long Size { get; private set; } public string Sha256 { get; private set; } = string.Empty;
    public string StorageKey { get; private set; } = string.Empty; public string UploadedBy { get; private set; } = string.Empty; public DateTimeOffset UploadedAt { get; private set; }
}

public sealed class TestExecutionEvidence
{
    private TestExecutionEvidence() { }
    public TestExecutionEvidence(Guid testExecutionId, Guid evidenceId) { Id = Guid.NewGuid(); TestExecutionId = testExecutionId; EvidenceId = evidenceId; }
    public Guid Id { get; private set; } public Guid TestExecutionId { get; private set; } public Guid EvidenceId { get; private set; }
}
