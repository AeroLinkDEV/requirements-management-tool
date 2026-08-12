using AeroLink.Domain.Common;
using System.Security.Cryptography;
using System.Text;

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
    public ManagedDocumentRevision(Guid documentId, int revision, string ownerId,
        string changeSummary, DateTimeOffset now, Guid? parentRevisionId = null,
        Guid? parentReleasedDocxAttachmentId = null, string? parentReleasedDocxSha256 = null,
        string? transformationProfile = null)
    {
        if (revision < 0) throw new DomainException("Document revisions cannot be negative.");
        if (revision == 0 && (parentRevisionId is not null || parentReleasedDocxAttachmentId is not null || parentReleasedDocxSha256 is not null))
            throw new DomainException("The initial document revision cannot have a parent revision.");
        if (revision > 0 && (parentRevisionId is null || parentReleasedDocxAttachmentId is null || string.IsNullOrWhiteSpace(parentReleasedDocxSha256)))
            throw new DomainException("A successor revision requires the exact released parent DOCX evidence.");
        Id = Guid.NewGuid(); DocumentId = documentId; Revision = revision;
        OwnerId = Required(ownerId, "A document-revision owner is required.").ToLowerInvariant();
        FormalChangeSummary = Bounded(changeSummary, 4000, "A document revision requires a formal change summary.", "A formal change summary cannot exceed 4000 characters.");
        FormalSummaryHash = Hash(FormalChangeSummary); FormalSummaryVersion = 1; FormalSummaryProvenance = "Authoritative";
        ParentRevisionId = parentRevisionId; ParentReleasedDocxAttachmentId = parentReleasedDocxAttachmentId;
        ParentReleasedDocxSha256 = parentReleasedDocxSha256?.Trim().ToLowerInvariant();
        TransformationProfile = transformationProfile?.Trim() ?? "";
        State = ManagedDocumentState.Draft; CreatedAt = UpdatedAt = now; Version = 1;
    }

    public Guid Id { get; private set; }
    public Guid DocumentId { get; private set; }
    public int Revision { get; private set; }
    public Guid? ParentRevisionId { get; private set; }
    public Guid? ParentReleasedDocxAttachmentId { get; private set; }
    public string? ParentReleasedDocxSha256 { get; private set; }
    public string TransformationProfile { get; private set; } = "";
    public string OwnerId { get; private set; } = "";
    public string FormalChangeSummary { get; private set; } = "";
    public string FormalSummaryHash { get; private set; } = "";
    public long FormalSummaryVersion { get; private set; }
    public string FormalSummaryProvenance { get; private set; } = "Authoritative";
    public ManagedDocumentState State { get; private set; }
    public Guid? CurrentWorkingAttachmentId { get; private set; }
    public Guid? ReleaseCandidateDocxAttachmentId { get; private set; }
    public Guid? ReleaseCandidatePdfAttachmentId { get; private set; }
    public Guid? ReleasedDocxAttachmentId { get; private set; }
    public Guid? ReleasedPdfAttachmentId { get; private set; }
    public string SnapshotHash { get; private set; } = "";
    public string SubmittedFormalSummaryHash { get; private set; } = "";
    public long? SubmittedFormalSummaryVersion { get; private set; }
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

    public void RecordCheckIn(Guid attachmentId, DateTimeOffset now)
    {
        EnsureEditable();
        CurrentWorkingAttachmentId = attachmentId;
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
        SubmittedFormalSummaryHash = FormalSummaryHash; SubmittedFormalSummaryVersion = FormalSummaryVersion;
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

    public void ReviseFormalSummary(string formalChangeSummary, string reason, long expectedVersion, DateTimeOffset now)
    {
        EnsureEditable();
        if (Version != expectedVersion) throw new DomainException("The document revision changed after this page loaded. Refresh and try again.");
        _ = Bounded(reason, 1000, "A formal-summary correction reason is required.", "A formal-summary correction reason cannot exceed 1000 characters.");
        FormalChangeSummary = Bounded(formalChangeSummary, 4000, "A formal change summary is required.", "A formal change summary cannot exceed 4000 characters.");
        FormalSummaryHash = Hash(FormalChangeSummary); FormalSummaryVersion++; FormalSummaryProvenance = "Authoritative";
        SnapshotHash = ""; SubmittedFormalSummaryHash = ""; SubmittedFormalSummaryVersion = null;
        UpdatedAt = now; Version++;
    }

    private ManagedDocumentReviewStep ActiveStep() => _reviewSteps.SingleOrDefault(x => x.Cycle == CurrentReviewCycle && x.State == ManagedDocumentReviewStepState.Active)
        ?? throw new DomainException("This document review has no active stage.");
    private void EnsureEditable() { if (State is not (ManagedDocumentState.Draft or ManagedDocumentState.Returned)) throw new DomainException("Only a Draft or returned document revision can be edited."); }
    private void EnsureInReview() { if (State != ManagedDocumentState.InReview) throw new DomainException("This document revision is not in review."); }
    private static string Required(string? value, string error) => string.IsNullOrWhiteSpace(value) ? throw new DomainException(error) : value.Trim();
    private static string Bounded(string? value, int maximum, string requiredError, string lengthError)
    { var result = Required(value, requiredError); return result.Length > maximum ? throw new DomainException(lengthError) : result; }
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}

/// <summary>Immutable evidence for one accepted managed-document working version.</summary>
public sealed class ManagedDocumentCheckIn
{
    private ManagedDocumentCheckIn() { }
    public ManagedDocumentCheckIn(Guid revisionId, Guid workingAttachmentId, int workingVersion, string actorId,
        string comment, Guid? baseAttachmentId, string? baseSha256, string resultSha256,
        Guid? supersededAttachmentId, Guid? connectorSessionId, string operationId, DateTimeOffset occurredAt,
        string? returnResolutionNote = null)
    {
        if (workingVersion < 1) throw new DomainException("A managed-document working version must be positive.");
        Id = Guid.NewGuid(); RevisionId = revisionId; WorkingAttachmentId = workingAttachmentId; WorkingVersion = workingVersion;
        ActorId = Required(actorId, "A check-in actor is required.").ToLowerInvariant();
        Comment = Bounded(comment, 4000, "A check-in comment is required.", "A check-in comment cannot exceed 4000 characters."); BaseAttachmentId = baseAttachmentId;
        BaseSha256 = baseSha256?.Trim().ToLowerInvariant(); ResultSha256 = Required(resultSha256, "A check-in result hash is required.").ToLowerInvariant();
        SupersededAttachmentId = supersededAttachmentId; ConnectorSessionId = connectorSessionId;
        OperationId = Required(operationId, "A check-in operation identifier is required."); OccurredAt = occurredAt;
        ReturnResolutionNote = string.IsNullOrWhiteSpace(returnResolutionNote) ? null : Bounded(returnResolutionNote, 4000, "A return-resolution note is required.", "A return-resolution note cannot exceed 4000 characters.");
    }
    public Guid Id { get; private set; }
    public Guid RevisionId { get; private set; }
    public Guid WorkingAttachmentId { get; private set; }
    public int WorkingVersion { get; private set; }
    public string ActorId { get; private set; } = "";
    public string Comment { get; private set; } = "";
    public Guid? BaseAttachmentId { get; private set; }
    public string? BaseSha256 { get; private set; }
    public string ResultSha256 { get; private set; } = "";
    public Guid? SupersededAttachmentId { get; private set; }
    public Guid? ConnectorSessionId { get; private set; }
    public string OperationId { get; private set; } = "";
    public DateTimeOffset OccurredAt { get; private set; }
    public string? ReturnResolutionNote { get; private set; }
    private static string Required(string? value, string error) => string.IsNullOrWhiteSpace(value) ? throw new DomainException(error) : value.Trim();
    private static string Bounded(string? value, int maximum, string requiredError, string lengthError)
    { var result = Required(value, requiredError); return result.Length > maximum ? throw new DomainException(lengthError) : result; }
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

/// <summary>Historical provenance retained from the former build-scoped document model. It never selects document effectivity.</summary>
public sealed class ManagedDocumentBuildProvenance
{
    private ManagedDocumentBuildProvenance() { }
    public ManagedDocumentBuildProvenance(Guid projectId, Guid releaseId, Guid documentId, Guid revisionId, string source, string actor, DateTimeOffset now)
    { Id = Guid.NewGuid(); ProjectId = projectId; ReleaseId = releaseId; DocumentId = documentId; RevisionId = revisionId; Source = source; RecordedBy = actor.ToLowerInvariant(); RecordedAt = now; }
    public Guid Id { get; private set; }
    public Guid ProjectId { get; private set; }
    public Guid ReleaseId { get; private set; }
    public Guid DocumentId { get; private set; }
    public Guid RevisionId { get; private set; }
    public string Source { get; private set; } = "";
    public string RecordedBy { get; private set; } = "";
    public DateTimeOffset RecordedAt { get; private set; }
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
