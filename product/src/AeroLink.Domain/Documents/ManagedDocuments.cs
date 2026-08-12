using AeroLink.Domain.Common;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AeroLink.Domain.Documents;

public enum ManagedDocumentState { Draft, InReview, Returned, Released, Superseded, Withdrawn }
public enum ManagedDocumentReviewStepState { Pending, Active, Approved, Returned }

public sealed record ManagedDocumentReviewer(string UserId, string Name, string StageName,
    string RequiredAuthority = "LegacyUnspecified", string GrantedAuthority = "LegacyUnspecified",
    string AuthoritySource = "LegacyUnspecified", Guid? AuthoritySourceId = null, Guid? WorkflowId = null,
    string WorkflowName = "Legacy managed-document review", int WorkflowVersion = 0,
    string AuthorityPolicy = "LegacyUnspecified");

/// <summary>A stable project document such as SDP-000001, independent of any one formal revision.</summary>
public sealed class ManagedDocument
{
    private ManagedDocument() { }
    public ManagedDocument(Guid projectId, string documentNumber, string acronym, string documentType, string title,
        string ownerId, DateTimeOffset now, string? createdBy = null)
    {
        var normalizedAcronym = Required(acronym, "A document acronym is required.").ToUpperInvariant();
        var normalizedNumber = ArtifactNumber.ValidateBase(documentNumber);
        if (!normalizedNumber.StartsWith(normalizedAcronym + "-", StringComparison.Ordinal))
            throw new DomainException("The controlled document number must begin with its acronym.");
        Id = Guid.NewGuid(); ProjectId = projectId; DocumentNumber = normalizedNumber; Acronym = normalizedAcronym;
        DocumentType = Required(documentType, "A document type is required."); Title = Required(title, "A document title is required.");
        OwnerId = Required(ownerId, "A document steward is required.").ToLowerInvariant(); StewardId = OwnerId;
        CreatedBy = Required(createdBy ?? ownerId, "A document creator is required.").ToLowerInvariant(); CreatedAt = UpdatedAt = now; Version = 1;
    }

    public Guid Id { get; private set; }
    public Guid ProjectId { get; private set; }
    public string DocumentNumber { get; private set; } = "";
    public string Acronym { get; private set; } = "";
    public string DocumentType { get; private set; } = "";
    public string Title { get; private set; } = "";
    public string OwnerId { get; private set; } = "";
    public string StewardId { get; private set; } = "";
    public string CreatedBy { get; private set; } = "";
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public long Version { get; private set; }

    public void UpdateTitle(string title, DateTimeOffset now)
    { Title = Required(title, "A document title is required."); UpdatedAt = now; Version++; }

    public string ReassignSteward(string stewardId, long expectedVersion, DateTimeOffset now)
    {
        if (Version != expectedVersion) throw new DomainException("The managed document changed after this page loaded. Refresh and try again.");
        var next = Required(stewardId, "A document steward is required.").ToLowerInvariant(); var prior = StewardId;
        if (next == prior) throw new DomainException("Select a different document steward.");
        StewardId = next; UpdatedAt = now; Version++; return prior;
    }

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
        string? transformationProfile = null, string? initiatedBy = null)
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
        ResponsibleOwnerId = OwnerId; InitiatedBy = Required(initiatedBy ?? ownerId, "A revision initiator is required.").ToLowerInvariant();
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
    public string ResponsibleOwnerId { get; private set; } = "";
    public string InitiatedBy { get; private set; } = "";
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
    public string SubmittedRelationshipManifest { get; private set; } = "";
    public string SubmittedRelationshipManifestHash { get; private set; } = "";
    public int RelationshipManifestVersion { get; private set; }
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

    public int SubmitForReview(string actor, string snapshotHash, IReadOnlyList<ManagedDocumentReviewer> reviewers, DateTimeOffset now,
        string relationshipManifest = "[]", string relationshipManifestHash = "", int relationshipManifestVersion = 1)
    {
        EnsureEditable();
        if (CurrentWorkingAttachmentId is null) throw new DomainException("Check in a Word working copy before submitting this revision.");
        if (reviewers.Count < 2) throw new DomainException("Document release requires a technical reviewer and an independent final approver.");
        if (reviewers.Select(x => x.UserId).Distinct(StringComparer.OrdinalIgnoreCase).Count() != reviewers.Count)
            throw new DomainException("A document reviewer cannot appear twice in one review cycle.");
        if (reviewers.Any(x => string.Equals(x.UserId, actor, StringComparison.OrdinalIgnoreCase) || string.Equals(x.UserId, ResponsibleOwnerId, StringComparison.OrdinalIgnoreCase)))
            throw new DomainException("The document author cannot approve their own revision.");
        var cycle = CurrentReviewCycle + 1;
        for (var index = 0; index < reviewers.Count; index++)
            _reviewSteps.Add(new ManagedDocumentReviewStep(Id, cycle, index, reviewers[index], index == 0, now));
        SnapshotHash = Required(snapshotHash, "A review snapshot hash is required.").ToLowerInvariant();
        SubmittedFormalSummaryHash = FormalSummaryHash; SubmittedFormalSummaryVersion = FormalSummaryVersion;
        SubmittedRelationshipManifest = Required(relationshipManifest, "A relationship manifest is required.");
        SubmittedRelationshipManifestHash = string.IsNullOrWhiteSpace(relationshipManifestHash) && relationshipManifest == "[]"
            ? Hash(relationshipManifest) : Required(relationshipManifestHash, "A relationship manifest hash is required.").ToLowerInvariant();
        RelationshipManifestVersion = relationshipManifestVersion > 0 ? relationshipManifestVersion : throw new DomainException("A relationship manifest version is required.");
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

    public bool Approve(string actor, Guid expectedStepId, int expectedCycle, long expectedRevisionVersion,
        long expectedStepVersion, string expectedSnapshotHash, Guid? expectedCandidateDocxAttachmentId,
        Guid? expectedCandidatePdfAttachmentId, string? expectedCandidateManifestHash, string rationale, DateTimeOffset now)
    {
        EnsureInReview(); var step = ActiveStep();
        EnsureDecisionIntent(step, expectedStepId, expectedCycle, expectedRevisionVersion, expectedStepVersion, expectedSnapshotHash);
        if (!string.Equals(step.ApproverId, actor, StringComparison.OrdinalIgnoreCase))
            throw new DomainException("Only the active document reviewer can approve this stage.");
        var cycleSteps = _reviewSteps.Where(x => x.Cycle == CurrentReviewCycle).OrderBy(x => x.Position).ToList();
        var final = step.Position == cycleSteps[^1].Position;
        if (final && (ReleaseCandidateDocxAttachmentId is null || ReleaseCandidatePdfAttachmentId is null || string.IsNullOrWhiteSpace(ReleaseManifestHash)))
            throw new DomainException("Prepare the exact DOCX and PDF release candidate before final approval.");
        if (final && (expectedCandidateDocxAttachmentId != ReleaseCandidateDocxAttachmentId
            || expectedCandidatePdfAttachmentId != ReleaseCandidatePdfAttachmentId
            || !string.Equals(expectedCandidateManifestHash, ReleaseManifestHash, StringComparison.OrdinalIgnoreCase)))
            throw new DomainException("The release candidate changed after this page loaded. Refresh and review the exact candidate before signing.");
        step.Approve(Required(rationale, "An approval rationale is required."), now);
        if (!final) cycleSteps.Single(x => x.Position == step.Position + 1).Activate();
        else
        {
            ReleasedDocxAttachmentId = ReleaseCandidateDocxAttachmentId; ReleasedPdfAttachmentId = ReleaseCandidatePdfAttachmentId;
            State = ManagedDocumentState.Released; ReleasedBy = actor.ToLowerInvariant(); ReleasedAt = now;
        }
        UpdatedAt = now; Version++; return final;
    }

    /// <summary>Trusted domain-fixture path; production API decisions use the exact-intent overload above.</summary>
    public bool Approve(string actor, string rationale, DateTimeOffset now)
    {
        var step = ActiveStep();
        return Approve(actor, step.Id, CurrentReviewCycle, Version, step.Version, SnapshotHash,
            ReleaseCandidateDocxAttachmentId, ReleaseCandidatePdfAttachmentId, ReleaseManifestHash, rationale, now);
    }

    public void Return(string actor, Guid expectedStepId, int expectedCycle, long expectedRevisionVersion,
        long expectedStepVersion, string expectedSnapshotHash, string reason, DateTimeOffset now)
    {
        EnsureInReview(); var step = ActiveStep();
        EnsureDecisionIntent(step, expectedStepId, expectedCycle, expectedRevisionVersion, expectedStepVersion, expectedSnapshotHash);
        if (!string.Equals(step.ApproverId, actor, StringComparison.OrdinalIgnoreCase))
            throw new DomainException("Only the active document reviewer can return this revision.");
        var explanation = Required(reason, "A return rationale is required."); step.Return(explanation, now);
        State = ManagedDocumentState.Returned; ReturnReason = explanation; ReleaseCandidateDocxAttachmentId = null;
        ReleaseCandidatePdfAttachmentId = null; ReleaseManifestHash = ""; UpdatedAt = now; Version++;
    }

    /// <summary>Trusted domain-fixture path; production API decisions use the exact-intent overload above.</summary>
    public void Return(string actor, string reason, DateTimeOffset now)
    {
        var step = ActiveStep();
        Return(actor, step.Id, CurrentReviewCycle, Version, step.Version, SnapshotHash, reason, now);
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
        SubmittedRelationshipManifest = ""; SubmittedRelationshipManifestHash = ""; RelationshipManifestVersion = 0;
        UpdatedAt = now; Version++;
    }

    public void RecordRelationshipChange(long expectedVersion, DateTimeOffset now)
    {
        EnsureEditable();
        if (Version != expectedVersion) throw new DomainException("The document revision changed after this page loaded. Refresh and try again.");
        SnapshotHash = ""; SubmittedFormalSummaryHash = ""; SubmittedFormalSummaryVersion = null;
        SubmittedRelationshipManifest = ""; SubmittedRelationshipManifestHash = ""; RelationshipManifestVersion = 0;
        ReleaseCandidateDocxAttachmentId = null; ReleaseCandidatePdfAttachmentId = null; ReleaseManifestHash = "";
        UpdatedAt = now; Version++;
    }

    public string ReassignResponsibleOwner(string ownerId, long expectedVersion, DateTimeOffset now)
    {
        EnsureEditable();
        if (Version != expectedVersion) throw new DomainException("The document revision changed after this page loaded. Refresh and try again.");
        var next = Required(ownerId, "A responsible revision owner is required.").ToLowerInvariant(); var prior = ResponsibleOwnerId;
        if (next == prior) throw new DomainException("Select a different responsible revision owner.");
        ResponsibleOwnerId = next; UpdatedAt = now; Version++; return prior;
    }

    private ManagedDocumentReviewStep ActiveStep() => _reviewSteps.SingleOrDefault(x => x.Cycle == CurrentReviewCycle && x.State == ManagedDocumentReviewStepState.Active)
        ?? throw new DomainException("This document review has no active stage.");
    private void EnsureDecisionIntent(ManagedDocumentReviewStep step, Guid expectedStepId, int expectedCycle,
        long expectedRevisionVersion, long expectedStepVersion, string expectedSnapshotHash)
    {
        if (Version != expectedRevisionVersion || CurrentReviewCycle != expectedCycle || step.Id != expectedStepId
            || step.Version != expectedStepVersion || !string.Equals(SnapshotHash, expectedSnapshotHash?.Trim(), StringComparison.OrdinalIgnoreCase))
            throw new DomainException("The document review advanced after this page loaded. Refresh and review the current evidence before signing.");
    }
    private void EnsureEditable() { if (State is not (ManagedDocumentState.Draft or ManagedDocumentState.Returned)) throw new DomainException("Only a Draft or returned document revision can be edited."); }
    private void EnsureInReview() { if (State != ManagedDocumentState.InReview) throw new DomainException("This document revision is not in review."); }
    private static string Required(string? value, string error) => string.IsNullOrWhiteSpace(value) ? throw new DomainException(error) : value.Trim();
    private static string Bounded(string? value, int maximum, string requiredError, string lengthError)
    { var result = Required(value, requiredError); return result.Length > maximum ? throw new DomainException(lengthError) : result; }
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}

/// <summary>Immutable attribution of one contributor to the exact submitted review cycle.</summary>
public sealed class ManagedDocumentReviewContributor
{
    private ManagedDocumentReviewContributor() { }
    public ManagedDocumentReviewContributor(Guid revisionId, int reviewCycle, string contributorId, string evidenceHash, DateTimeOffset capturedAt, string provenance = "AuthoritativeSubmissionSnapshot")
    {
        Id = Guid.NewGuid(); RevisionId = revisionId; ReviewCycle = reviewCycle;
        ContributorId = Required(contributorId, "A contributor is required.").ToLowerInvariant();
        EvidenceHash = Required(evidenceHash, "Contributor evidence requires a hash.").ToLowerInvariant(); CapturedAt = capturedAt;
        Provenance = Required(provenance, "Contributor evidence provenance is required.");
    }
    public Guid Id { get; private set; }
    public Guid RevisionId { get; private set; }
    public int ReviewCycle { get; private set; }
    public string ContributorId { get; private set; } = "";
    public string EvidenceHash { get; private set; } = "";
    public DateTimeOffset CapturedAt { get; private set; }
    public string Provenance { get; private set; } = "";
    private static string Required(string? value, string error) => string.IsNullOrWhiteSpace(value) ? throw new DomainException(error) : value.Trim();
}

/// <summary>Append-only evidence of an explicit stewardship or revision-responsibility transfer.</summary>
public sealed class ManagedDocumentAssignment
{
    private ManagedDocumentAssignment() { }
    public ManagedDocumentAssignment(Guid documentId, Guid? revisionId, string assignmentType, string priorAssigneeId,
        string newAssigneeId, string assignedBy, string reason, DateTimeOffset effectiveAt)
    {
        Id = Guid.NewGuid(); DocumentId = documentId; RevisionId = revisionId;
        AssignmentType = Required(assignmentType, "An assignment type is required.");
        PriorAssigneeId = Required(priorAssigneeId, "A prior assignee is required.").ToLowerInvariant();
        NewAssigneeId = Required(newAssigneeId, "A new assignee is required.").ToLowerInvariant();
        AssignedBy = Required(assignedBy, "An assigning actor is required.").ToLowerInvariant();
        Reason = Bounded(reason, 1000, "A reassignment reason is required.", "A reassignment reason cannot exceed 1000 characters."); EffectiveAt = effectiveAt;
    }
    public Guid Id { get; private set; }
    public Guid DocumentId { get; private set; }
    public Guid? RevisionId { get; private set; }
    public string AssignmentType { get; private set; } = "";
    public string PriorAssigneeId { get; private set; } = "";
    public string NewAssigneeId { get; private set; } = "";
    public string AssignedBy { get; private set; } = "";
    public string Reason { get; private set; } = "";
    public DateTimeOffset EffectiveAt { get; private set; }
    private static string Required(string? value, string error) => string.IsNullOrWhiteSpace(value) ? throw new DomainException(error) : value.Trim();
    private static string Bounded(string? value, int maximum, string requiredError, string lengthError)
    { var result = Required(value, requiredError); return result.Length > maximum ? throw new DomainException(lengthError) : result; }
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
    internal ManagedDocumentReviewStep(Guid revisionId, int cycle, int position, ManagedDocumentReviewer reviewer, bool active, DateTimeOffset assignedAt)
    {
        Id = Guid.NewGuid(); RevisionId = revisionId; Cycle = cycle; Position = position;
        ApproverId = Required(reviewer.UserId).ToLowerInvariant(); ApproverName = Required(reviewer.Name);
        StageName = Required(reviewer.StageName); State = active ? ManagedDocumentReviewStepState.Active : ManagedDocumentReviewStepState.Pending;
        RequiredAuthority = Required(reviewer.RequiredAuthority); GrantedAuthority = Required(reviewer.GrantedAuthority);
        AuthoritySource = Required(reviewer.AuthoritySource); AuthoritySourceId = reviewer.AuthoritySourceId; WorkflowId = reviewer.WorkflowId;
        WorkflowName = Required(reviewer.WorkflowName);
        WorkflowVersion = reviewer.WorkflowVersion > 0 || reviewer.AuthoritySource == "LegacyUnspecified"
            ? reviewer.WorkflowVersion
            : throw new DomainException("A document review workflow version is required.");
        AuthorityPolicy = Required(reviewer.AuthorityPolicy); AssignedAt = assignedAt; Version = 1;
    }
    public Guid Id { get; private set; }
    public Guid RevisionId { get; private set; }
    public int Cycle { get; private set; }
    public int Position { get; private set; }
    public string ApproverId { get; private set; } = "";
    public string ApproverName { get; private set; } = "";
    public string StageName { get; private set; } = "";
    public string RequiredAuthority { get; private set; } = "LegacyUnspecified";
    public string GrantedAuthority { get; private set; } = "LegacyUnspecified";
    public string AuthoritySource { get; private set; } = "LegacyUnspecified";
    public Guid? AuthoritySourceId { get; private set; }
    public Guid? WorkflowId { get; private set; }
    public string WorkflowName { get; private set; } = "Legacy managed-document review";
    public int WorkflowVersion { get; private set; }
    public string AuthorityPolicy { get; private set; } = "LegacyUnspecified";
    public DateTimeOffset? AssignedAt { get; private set; }
    public long Version { get; private set; }
    public ManagedDocumentReviewStepState State { get; private set; }
    public string Rationale { get; private set; } = "";
    public DateTimeOffset? DecidedAt { get; private set; }
    internal void Activate() { State = ManagedDocumentReviewStepState.Active; Version++; }
    internal void Approve(string rationale, DateTimeOffset now) { State = ManagedDocumentReviewStepState.Approved; Rationale = rationale; DecidedAt = now; Version++; }
    internal void Return(string rationale, DateTimeOffset now) { State = ManagedDocumentReviewStepState.Returned; Rationale = rationale; DecidedAt = now; Version++; }
    private static string Required(string? value) => string.IsNullOrWhiteSpace(value) ? throw new DomainException("A document review-step value is required.") : value.Trim();
}

/// <summary>One durable result for a caller supplied one-use review operation key.</summary>
public sealed class ManagedDocumentOperation
{
    private ManagedDocumentOperation() { }
    public ManagedDocumentOperation(Guid revisionId, string operationType, string operationKey,
        string payloadHash, string resultJson, DateTimeOffset completedAt)
    {
        Id = Guid.NewGuid(); RevisionId = revisionId; OperationType = Required(operationType);
        OperationKey = Required(operationKey); PayloadHash = Required(payloadHash).ToLowerInvariant();
        ResultJson = Required(resultJson); CompletedAt = completedAt;
    }
    public Guid Id { get; private set; }
    public Guid RevisionId { get; private set; }
    public string OperationType { get; private set; } = "";
    public string OperationKey { get; private set; } = "";
    public string PayloadHash { get; private set; } = "";
    public string ResultJson { get; private set; } = "";
    public DateTimeOffset CompletedAt { get; private set; }
    private static string Required(string? value) => string.IsNullOrWhiteSpace(value) ? throw new DomainException("A managed-document operation value is required.") : value.Trim();
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
        : this(revisionId, artifactType, artifactId, displayNumber, "", "", Guid.Empty, null, "", "", relationship, actor, now, 0, "LegacyClientSupplied") { }
    public ManagedDocumentLink(Guid revisionId, string artifactType, Guid artifactId, string displayNumber, string title,
        string targetState, Guid targetProjectId, Guid? targetReleaseId, string targetReleaseVersion, string deepLink,
        string relationship, string actor, DateTimeOffset now, int policyVersion = ManagedDocumentRelationshipPolicy.CurrentVersion,
        string provenance = "CanonicalServerResolved")
    {
        Id = Guid.NewGuid(); RevisionId = revisionId; ArtifactType = Required(artifactType); ArtifactId = artifactId;
        DisplayNumber = Bounded(displayNumber, 80, "A canonical target identifier is required."); CanonicalTitle = Bounded(title, 500, "A canonical target title is required.", policyVersion == 0);
        TargetState = Bounded(targetState, 80, "A canonical target state is required.", policyVersion == 0); TargetProjectId = targetProjectId;
        TargetReleaseId = targetReleaseId; TargetReleaseVersion = targetReleaseVersion?.Trim() ?? "";
        DeepLink = Bounded(deepLink, 1000, "A canonical target link is required.", policyVersion == 0);
        Relationship = ManagedDocumentRelationshipPolicy.Validate(ArtifactType, relationship, policyVersion);
        PolicyVersion = policyVersion; Provenance = Required(provenance); IsCurrent = true;
        CreatedBy = Required(actor).ToLowerInvariant(); CreatedAt = now;
    }
    public Guid Id { get; private set; }
    public Guid RevisionId { get; private set; }
    public string ArtifactType { get; private set; } = "";
    public Guid ArtifactId { get; private set; }
    public string DisplayNumber { get; private set; } = "";
    public string CanonicalTitle { get; private set; } = "";
    public string TargetState { get; private set; } = "";
    public Guid TargetProjectId { get; private set; }
    public Guid? TargetReleaseId { get; private set; }
    public string TargetReleaseVersion { get; private set; } = "";
    public string DeepLink { get; private set; } = "";
    public string Relationship { get; private set; } = "";
    public int PolicyVersion { get; private set; }
    public string Provenance { get; private set; } = "";
    public bool IsCurrent { get; private set; }
    public Guid? SupersededByLinkId { get; private set; }
    public string SupersedeReason { get; private set; } = "";
    public string? SupersededBy { get; private set; }
    public DateTimeOffset? SupersededAt { get; private set; }
    public string CreatedBy { get; private set; } = "";
    public DateTimeOffset CreatedAt { get; private set; }
    public void Supersede(string actor, string reason, DateTimeOffset now, Guid? replacementId = null)
    {
        if (!IsCurrent) throw new DomainException("This document relationship has already been superseded.");
        SupersedeReason = Bounded(reason, 1000, "A relationship correction reason is required.");
        IsCurrent = false; SupersededBy = Required(actor).ToLowerInvariant(); SupersededAt = now; SupersededByLinkId = replacementId;
    }
    private static string Required(string? value) => string.IsNullOrWhiteSpace(value) ? throw new DomainException("A document relationship value is required.") : value.Trim();
    private static string Bounded(string? value, int maximum, string error, bool allowEmpty = false)
    { var result = value?.Trim() ?? ""; if (!allowEmpty && result.Length == 0) throw new DomainException(error); return result.Length > maximum ? throw new DomainException(error) : result; }
}

public static class ManagedDocumentRelationshipPolicy
{
    public const int CurrentVersion = 1;
    private static readonly IReadOnlyDictionary<string, string[]> Allowed = new Dictionary<string, string[]>(StringComparer.Ordinal)
    {
        ["ChangeRequest"] = ["MotivatedBy", "ImplementsChange"],
        ["TestChangeRequest"] = ["VerificationImpact"],
        ["ProblemReport"] = ["AddressesProblem", "AffectedBy"],
        ["Release"] = ["RelatedBuild", "AppliesToMilestone"]
    };

    public static string CanonicalType(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "changerequest" or "change-request" or "srcr" or "hlrcr" or "llrcr" => "ChangeRequest",
        "testchangerequest" or "test-change-request" or "tcr" => "TestChangeRequest",
        "problemreport" or "problem-report" or "pr" => "ProblemReport",
        "release" or "build" => "Release",
        _ => throw new DomainException("Choose a supported lifecycle artifact type.")
    };

    public static IReadOnlyList<string> Relationships(string artifactType) => Allowed.TryGetValue(CanonicalType(artifactType), out var values)
        ? values : throw new DomainException("Choose a supported lifecycle artifact type.");

    public static string Validate(string artifactType, string? relationship, int policyVersion = CurrentVersion)
    {
        var value = relationship?.Trim() ?? "";
        if (policyVersion == 0) return Required(value);
        if (value.Length > 80 || !Allowed.TryGetValue(CanonicalType(artifactType), out var values) || !values.Contains(value, StringComparer.Ordinal))
            throw new DomainException("Choose a supported relationship for this lifecycle artifact type.");
        return value;
    }

    public static (string Json, string Hash) Manifest(IEnumerable<ManagedDocumentLink> links)
    {
        var entries = links.Where(x => x.IsCurrent).OrderBy(x => x.ArtifactType, StringComparer.Ordinal)
            .ThenBy(x => x.DisplayNumber, StringComparer.Ordinal).ThenBy(x => x.ArtifactId).ThenBy(x => x.Relationship, StringComparer.Ordinal)
            .Select(x => new { x.ArtifactType, x.ArtifactId, x.DisplayNumber, x.CanonicalTitle, x.TargetState, x.TargetProjectId,
                x.TargetReleaseId, x.TargetReleaseVersion, x.Relationship, x.PolicyVersion, x.Provenance, x.DeepLink }).ToArray();
        var json = JsonSerializer.Serialize(entries); return (json, Hash(json));
    }

    private static string Required(string? value) => string.IsNullOrWhiteSpace(value) ? throw new DomainException("A document relationship value is required.") : value.Trim();
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
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
