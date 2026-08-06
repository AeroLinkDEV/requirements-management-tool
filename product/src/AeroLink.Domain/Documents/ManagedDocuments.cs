using AeroLink.Domain.Common;

namespace AeroLink.Domain.Documents;

public enum ManagedDocumentState { Draft, InReview, Returned, Released, Superseded, Withdrawn }
public enum ManagedDocumentReviewStepState { Pending, Active, Approved, Returned }

public sealed record ManagedDocumentReviewer(string UserId, string Name, string StageName);

/// <summary>A stable project document such as SDP-000001, independent of any one formal revision.</summary>
public sealed class ManagedDocument
{
    private ManagedDocument() { }
    public ManagedDocument(Guid projectId, string documentNumber, string acronym, string documentType, string title,
        string ownerId, DateTimeOffset now)
    {
        var normalizedAcronym = Required(acronym, "A document acronym is required.").ToUpperInvariant();
        var normalizedNumber = ArtifactNumber.ValidateBase(documentNumber);
        if (!normalizedNumber.StartsWith(normalizedAcronym + "-", StringComparison.Ordinal))
            throw new DomainException("The controlled document number must begin with its acronym.");
        Id = Guid.NewGuid(); ProjectId = projectId; DocumentNumber = normalizedNumber; Acronym = normalizedAcronym;
        DocumentType = Required(documentType, "A document type is required."); Title = Required(title, "A document title is required.");
        OwnerId = Required(ownerId, "A document owner is required.").ToLowerInvariant(); CreatedAt = UpdatedAt = now; Version = 1;
    }

    public Guid Id { get; private set; }
    public Guid ProjectId { get; private set; }
    public string DocumentNumber { get; private set; } = "";
    public string Acronym { get; private set; } = "";
    public string DocumentType { get; private set; } = "";
    public string Title { get; private set; } = "";
    public string OwnerId { get; private set; } = "";
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public long Version { get; private set; }

    public void Update(string title, string ownerId, DateTimeOffset now)
    { Title = Required(title, "A document title is required."); OwnerId = Required(ownerId, "A document owner is required.").ToLowerInvariant(); UpdatedAt = now; Version++; }

    private static string Required(string? value, string error) => string.IsNullOrWhiteSpace(value) ? throw new DomainException(error) : value.Trim();
}

/// <summary>One formal revision. Working check-ins belong inside it and do not change Revision.</summary>
public sealed class ManagedDocumentRevision
{
    private readonly List<ManagedDocumentReviewStep> _reviewSteps = [];
    private ManagedDocumentRevision() { }
    public ManagedDocumentRevision(Guid documentId, Guid targetReleaseId, int revision, string ownerId,
        string changeSummary, DateTimeOffset now)
    {
        if (revision < 0) throw new DomainException("Document revisions cannot be negative.");
        if (targetReleaseId == Guid.Empty) throw new DomainException("A document revision requires a target build.");
        Id = Guid.NewGuid(); DocumentId = documentId; TargetReleaseId = targetReleaseId; Revision = revision;
        OwnerId = Required(ownerId, "A document-revision owner is required.").ToLowerInvariant();
        ChangeSummary = Required(changeSummary, "A document revision requires a change summary.");
        State = ManagedDocumentState.Draft; CreatedAt = UpdatedAt = now; Version = 1;
    }

    public Guid Id { get; private set; }
    public Guid DocumentId { get; private set; }
    public Guid TargetReleaseId { get; private set; }
    public int Revision { get; private set; }
    public string OwnerId { get; private set; } = "";
    public string ChangeSummary { get; private set; } = "";
    public ManagedDocumentState State { get; private set; }
    public Guid? CurrentWorkingAttachmentId { get; private set; }
    public Guid? ReleaseCandidateDocxAttachmentId { get; private set; }
    public Guid? ReleaseCandidatePdfAttachmentId { get; private set; }
    public Guid? ReleasedDocxAttachmentId { get; private set; }
    public Guid? ReleasedPdfAttachmentId { get; private set; }
    public string SnapshotHash { get; private set; } = "";
    public string ReleaseManifestHash { get; private set; } = "";
    public string ReturnReason { get; private set; } = "";
    public string? SubmittedBy { get; private set; }
    public DateTimeOffset? SubmittedAt { get; private set; }
    public string? ReleasedBy { get; private set; }
    public DateTimeOffset? ReleasedAt { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public long Version { get; private set; }
    public IReadOnlyCollection<ManagedDocumentReviewStep> ReviewSteps => _reviewSteps.AsReadOnly();
    public int CurrentReviewCycle => _reviewSteps.Count == 0 ? 0 : _reviewSteps.Max(x => x.Cycle);

    public void RecordCheckIn(Guid attachmentId, string actor, string changeSummary, DateTimeOffset now)
    {
        EnsureEditable();
        CurrentWorkingAttachmentId = attachmentId;
        ChangeSummary = Required(changeSummary, "A check-in comment is required.");
        OwnerId = Required(actor, "A check-in actor is required.").ToLowerInvariant();
        if (State == ManagedDocumentState.Returned) State = ManagedDocumentState.Draft;
        ReturnReason = ""; ReleaseCandidateDocxAttachmentId = null; ReleaseCandidatePdfAttachmentId = null;
        ReleaseManifestHash = ""; UpdatedAt = now; Version++;
    }

    public int SubmitForReview(string actor, string snapshotHash, IReadOnlyList<ManagedDocumentReviewer> reviewers, DateTimeOffset now)
    {
        EnsureEditable();
        if (CurrentWorkingAttachmentId is null) throw new DomainException("Check in a Word working copy before submitting this revision.");
        if (reviewers.Count < 2) throw new DomainException("Document release requires a technical reviewer and an independent final approver.");
        if (reviewers.Select(x => x.UserId).Distinct(StringComparer.OrdinalIgnoreCase).Count() != reviewers.Count)
            throw new DomainException("A document reviewer cannot appear twice in one review cycle.");
        if (reviewers.Any(x => string.Equals(x.UserId, actor, StringComparison.OrdinalIgnoreCase) || string.Equals(x.UserId, OwnerId, StringComparison.OrdinalIgnoreCase)))
            throw new DomainException("The document author cannot approve their own revision.");
        var cycle = CurrentReviewCycle + 1;
        for (var index = 0; index < reviewers.Count; index++)
            _reviewSteps.Add(new ManagedDocumentReviewStep(Id, cycle, index, reviewers[index], index == 0));
        SnapshotHash = Required(snapshotHash, "A review snapshot hash is required.").ToLowerInvariant();
        SubmittedBy = Required(actor, "A submitting actor is required.").ToLowerInvariant(); SubmittedAt = now;
        State = ManagedDocumentState.InReview; ReturnReason = ""; UpdatedAt = now; Version++; return cycle;
    }

    public void RecordReleaseCandidate(Guid docxAttachmentId, Guid pdfAttachmentId, string manifestHash,
        string actor, DateTimeOffset now)
    {
        EnsureInReview();
        var active = ActiveStep();
        if (active.Position != _reviewSteps.Where(x => x.Cycle == CurrentReviewCycle).Max(x => x.Position))
            throw new DomainException("The release candidate is prepared only after technical review is complete.");
        if (!string.Equals(active.ApproverId, actor, StringComparison.OrdinalIgnoreCase))
            throw new DomainException("Only the active final approver can prepare this release candidate.");
        ReleaseCandidateDocxAttachmentId = docxAttachmentId; ReleaseCandidatePdfAttachmentId = pdfAttachmentId;
        ReleaseManifestHash = Required(manifestHash, "A release-candidate manifest hash is required.").ToLowerInvariant();
        UpdatedAt = now; Version++;
    }

    public bool Approve(string actor, string rationale, DateTimeOffset now)
    {
        EnsureInReview(); var step = ActiveStep();
        if (!string.Equals(step.ApproverId, actor, StringComparison.OrdinalIgnoreCase))
            throw new DomainException("Only the active document reviewer can approve this stage.");
        var cycleSteps = _reviewSteps.Where(x => x.Cycle == CurrentReviewCycle).OrderBy(x => x.Position).ToList();
        var final = step.Position == cycleSteps[^1].Position;
        if (final && (ReleaseCandidateDocxAttachmentId is null || ReleaseCandidatePdfAttachmentId is null || string.IsNullOrWhiteSpace(ReleaseManifestHash)))
            throw new DomainException("Prepare the exact DOCX and PDF release candidate before final approval.");
        step.Approve(Required(rationale, "An approval rationale is required."), now);
        if (!final) cycleSteps.Single(x => x.Position == step.Position + 1).Activate();
        else
        {
            ReleasedDocxAttachmentId = ReleaseCandidateDocxAttachmentId; ReleasedPdfAttachmentId = ReleaseCandidatePdfAttachmentId;
            State = ManagedDocumentState.Released; ReleasedBy = actor.ToLowerInvariant(); ReleasedAt = now;
        }
        UpdatedAt = now; Version++; return final;
    }

    public void Return(string actor, string reason, DateTimeOffset now)
    {
        EnsureInReview(); var step = ActiveStep();
        if (!string.Equals(step.ApproverId, actor, StringComparison.OrdinalIgnoreCase))
            throw new DomainException("Only the active document reviewer can return this revision.");
        var explanation = Required(reason, "A return rationale is required."); step.Return(explanation, now);
        State = ManagedDocumentState.Returned; ReturnReason = explanation; ReleaseCandidateDocxAttachmentId = null;
        ReleaseCandidatePdfAttachmentId = null; ReleaseManifestHash = ""; UpdatedAt = now; Version++;
    }

    public void Supersede(DateTimeOffset now)
    { if (State != ManagedDocumentState.Released) throw new DomainException("Only a released revision can be superseded."); State = ManagedDocumentState.Superseded; UpdatedAt = now; Version++; }

    private ManagedDocumentReviewStep ActiveStep() => _reviewSteps.SingleOrDefault(x => x.Cycle == CurrentReviewCycle && x.State == ManagedDocumentReviewStepState.Active)
        ?? throw new DomainException("This document review has no active stage.");
    private void EnsureEditable() { if (State is not (ManagedDocumentState.Draft or ManagedDocumentState.Returned)) throw new DomainException("Only a Draft or returned document revision can be edited."); }
    private void EnsureInReview() { if (State != ManagedDocumentState.InReview) throw new DomainException("This document revision is not in review."); }
    private static string Required(string? value, string error) => string.IsNullOrWhiteSpace(value) ? throw new DomainException(error) : value.Trim();
}

public sealed class ManagedDocumentReviewStep
{
    private ManagedDocumentReviewStep() { }
    internal ManagedDocumentReviewStep(Guid revisionId, int cycle, int position, ManagedDocumentReviewer reviewer, bool active)
    {
        Id = Guid.NewGuid(); RevisionId = revisionId; Cycle = cycle; Position = position;
        ApproverId = Required(reviewer.UserId).ToLowerInvariant(); ApproverName = Required(reviewer.Name);
        StageName = Required(reviewer.StageName); State = active ? ManagedDocumentReviewStepState.Active : ManagedDocumentReviewStepState.Pending;
    }
    public Guid Id { get; private set; }
    public Guid RevisionId { get; private set; }
    public int Cycle { get; private set; }
    public int Position { get; private set; }
    public string ApproverId { get; private set; } = "";
    public string ApproverName { get; private set; } = "";
    public string StageName { get; private set; } = "";
    public ManagedDocumentReviewStepState State { get; private set; }
    public string Rationale { get; private set; } = "";
    public DateTimeOffset? DecidedAt { get; private set; }
    internal void Activate() => State = ManagedDocumentReviewStepState.Active;
    internal void Approve(string rationale, DateTimeOffset now) { State = ManagedDocumentReviewStepState.Approved; Rationale = rationale; DecidedAt = now; }
    internal void Return(string rationale, DateTimeOffset now) { State = ManagedDocumentReviewStepState.Returned; Rationale = rationale; DecidedAt = now; }
    private static string Required(string? value) => string.IsNullOrWhiteSpace(value) ? throw new DomainException("A document review-step value is required.") : value.Trim();
}

/// <summary>The exact document revision selected for a software build.</summary>
public sealed class ManagedDocumentBuildSelection
{
    private ManagedDocumentBuildSelection() { }
    public ManagedDocumentBuildSelection(Guid projectId, Guid releaseId, Guid documentId, Guid revisionId, string actor, DateTimeOffset now)
    { Id = Guid.NewGuid(); ProjectId = projectId; ReleaseId = releaseId; DocumentId = documentId; RevisionId = revisionId; SelectedBy = actor.ToLowerInvariant(); SelectedAt = now; }
    public Guid Id { get; private set; }
    public Guid ProjectId { get; private set; }
    public Guid ReleaseId { get; private set; }
    public Guid DocumentId { get; private set; }
    public Guid RevisionId { get; private set; }
    public string SelectedBy { get; private set; } = "";
    public DateTimeOffset SelectedAt { get; private set; }
    public void Select(Guid revisionId, string actor, DateTimeOffset now) { RevisionId = revisionId; SelectedBy = actor.ToLowerInvariant(); SelectedAt = now; }
}

public sealed class ManagedDocumentLink
{
    private ManagedDocumentLink() { }
    public ManagedDocumentLink(Guid revisionId, string artifactType, Guid artifactId, string displayNumber, string relationship, string actor, DateTimeOffset now)
    { Id = Guid.NewGuid(); RevisionId = revisionId; ArtifactType = Required(artifactType); ArtifactId = artifactId; DisplayNumber = Required(displayNumber).ToUpperInvariant(); Relationship = Required(relationship); CreatedBy = actor.ToLowerInvariant(); CreatedAt = now; }
    public Guid Id { get; private set; }
    public Guid RevisionId { get; private set; }
    public string ArtifactType { get; private set; } = "";
    public Guid ArtifactId { get; private set; }
    public string DisplayNumber { get; private set; } = "";
    public string Relationship { get; private set; } = "";
    public string CreatedBy { get; private set; } = "";
    public DateTimeOffset CreatedAt { get; private set; }
    private static string Required(string? value) => string.IsNullOrWhiteSpace(value) ? throw new DomainException("A document relationship value is required.") : value.Trim();
}

public sealed class ManagedDocumentEvent
{
    private ManagedDocumentEvent() { }
    public ManagedDocumentEvent(Guid documentId, string eventType, string actorId, string detail, DateTimeOffset occurredAt)
    { Id = Guid.NewGuid(); DocumentId = documentId; EventType = Required(eventType); ActorId = Required(actorId).ToLowerInvariant(); Detail = Required(detail); OccurredAt = occurredAt; }
    public Guid Id { get; private set; }
    public Guid DocumentId { get; private set; }
    public string EventType { get; private set; } = "";
    public string ActorId { get; private set; } = "";
    public string Detail { get; private set; } = "";
    public DateTimeOffset OccurredAt { get; private set; }
    private static string Required(string? value) => string.IsNullOrWhiteSpace(value) ? throw new DomainException("A document history value is required.") : value.Trim();
}

/// <summary>A one-time browser handoff and the short-lived scoped credential used by the Windows connector.</summary>
public sealed class DocumentConnectorGrant
{
    private DocumentConnectorGrant() { }
    public DocumentConnectorGrant(Guid projectId, Guid documentId, Guid revisionId, Guid editSessionId,
        string userName, string mode, string launchTokenHash, DateTimeOffset now)
    { Id = Guid.NewGuid(); ProjectId = projectId; DocumentId = documentId; RevisionId = revisionId; EditSessionId = editSessionId; UserName = userName.ToLowerInvariant(); Mode = mode is "edit" or "release" ? mode : throw new DomainException("The connector mode is invalid."); LaunchTokenHash = launchTokenHash.ToLowerInvariant(); CreatedAt = now; ExpiresAt = now.AddMinutes(5); }
    public Guid Id { get; private set; }
    public Guid ProjectId { get; private set; }
    public Guid DocumentId { get; private set; }
    public Guid RevisionId { get; private set; }
    public Guid EditSessionId { get; private set; }
    public string UserName { get; private set; } = "";
    public string Mode { get; private set; } = "edit";
    public string LaunchTokenHash { get; private set; } = "";
    public string? AccessTokenHash { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset ExpiresAt { get; private set; }
    public DateTimeOffset? RedeemedAt { get; private set; }
    public DateTimeOffset? RevokedAt { get; private set; }
    public void Redeem(string accessTokenHash, DateTimeOffset now)
    { if (RedeemedAt is not null || RevokedAt is not null || ExpiresAt <= now) throw new DomainException("This connector launch ticket is expired or has already been used."); AccessTokenHash = accessTokenHash.ToLowerInvariant(); RedeemedAt = now; ExpiresAt = now.AddHours(8); }
    public bool IsAccessValid(DateTimeOffset now) => RedeemedAt is not null && RevokedAt is null && ExpiresAt > now && !string.IsNullOrWhiteSpace(AccessTokenHash);
    public void Extend(DateTimeOffset now) { if (!IsAccessValid(now)) throw new DomainException("This connector session is no longer active."); ExpiresAt = now.AddHours(8); }
    public void Revoke(DateTimeOffset now) => RevokedAt = now;
}
