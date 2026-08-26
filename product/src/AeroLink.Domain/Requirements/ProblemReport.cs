using AeroLink.Domain.Common;

namespace AeroLink.Domain.Requirements;

public enum ProblemReportState
{
    Draft, ReadyForSccb, Open, Implementing, Verifying, WaitingForSqaToClose, Closed, Rejected,
}

public enum ProblemReportSeverity { Critical, High, Major, Minor, Trivial }

// ProblemReportType — Documentation, Code, Test, Other — was retired by the category vocabulary in
// ProblemReportCategory.cs. It is gone rather than deprecated in place: every retained record was mapped
// onto the nine categories by migration and carries the provenance of that mapping, so there is no reading
// of the old value left to preserve and nothing that should still be able to write one.
public enum ProblemReportPriority { Urgent, High, Normal, Low }
public enum ProblemReportDisposition { Fixed, Duplicate, CannotReproduce, NoFaultFound, Deferred, AcceptedRisk, Rejected }
public enum ProblemReportClosureCandidateState { Pending, Invalidated, Approved, LegacyUnavailable }

/// <summary>
/// The exact closure basis selected for independent SQA review. A candidate is never rewritten: a
/// closure-significant change invalidates it, and re-verification creates a new sequence.
/// </summary>
public sealed class ProblemReportClosureCandidate
{
    private ProblemReportClosureCandidate() { }
    public ProblemReportClosureCandidate(Guid problemReportId, int reportRevision, int sequence,
        int schemaVersion, long reportVersion, string reportSnapshotJson, string reportSnapshotHash,
        Guid verificationExecutionId, string verificationEvidenceJson, string verificationEvidenceHash,
        string linksManifestJson, string linksManifestHash, string manifestHash,
        string selectedBy, DateTimeOffset selectedAt, int reportSnapshotSchemaVersion)
    {
        if (problemReportId == Guid.Empty || verificationExecutionId == Guid.Empty)
            throw new DomainException("A closure candidate requires its Problem Report and verification execution.");
        if (sequence < 1 || schemaVersion < 1 || reportVersion < 1 || reportSnapshotSchemaVersion < 1)
            throw new DomainException("A closure candidate requires a valid sequence, schema, and Problem Report version.");
        Id = Guid.NewGuid(); ProblemReportId = problemReportId; ReportRevision = reportRevision;
        Sequence = sequence; SchemaVersion = schemaVersion; ReportVersion = reportVersion;
        ReportSnapshotSchemaVersion = reportSnapshotSchemaVersion;
        ReportSnapshotJson = Required(reportSnapshotJson); ReportSnapshotHash = Hash(reportSnapshotHash);
        VerificationExecutionId = verificationExecutionId;
        VerificationEvidenceJson = Required(verificationEvidenceJson); VerificationEvidenceHash = Hash(verificationEvidenceHash);
        LinksManifestJson = Required(linksManifestJson); LinksManifestHash = Hash(linksManifestHash);
        ManifestHash = Hash(manifestHash); SelectedBy = Required(selectedBy); SelectedAt = selectedAt;
        PackageProvenance = "Candidate";
        State = ProblemReportClosureCandidateState.Pending;
    }

    public Guid Id { get; private set; }
    public Guid ProblemReportId { get; private set; }
    public int ReportRevision { get; private set; }
    public int Sequence { get; private set; }
    public int SchemaVersion { get; private set; }
    public int ReportSnapshotSchemaVersion { get; private set; }
    public long ReportVersion { get; private set; }
    public string ReportSnapshotJson { get; private set; } = "";
    public string ReportSnapshotHash { get; private set; } = "";
    public Guid VerificationExecutionId { get; private set; }
    public string VerificationEvidenceJson { get; private set; } = "";
    public string VerificationEvidenceHash { get; private set; } = "";
    public string LinksManifestJson { get; private set; } = "";
    public string LinksManifestHash { get; private set; } = "";
    public string ManifestHash { get; private set; } = "";
    public string SelectedBy { get; private set; } = "";
    public DateTimeOffset SelectedAt { get; private set; }
    public ProblemReportClosureCandidateState State { get; private set; }
    public string InvalidatedBy { get; private set; } = "";
    public DateTimeOffset? InvalidatedAt { get; private set; }
    public string InvalidationReason { get; private set; } = "";
    public Guid? ApprovedByAccountId { get; private set; }
    public string ApprovedBy { get; private set; } = "";
    public DateTimeOffset? ApprovedAt { get; private set; }
    public string PackageProvenance { get; private set; } = "";
    public string ClosurePackageJson { get; private set; } = "";
    public string ClosurePackageHash { get; private set; } = "";

    public void Invalidate(string actor, string reason, DateTimeOffset now)
    {
        if (State != ProblemReportClosureCandidateState.Pending) return;
        State = ProblemReportClosureCandidateState.Invalidated;
        InvalidatedBy = Required(actor); InvalidationReason = Required(reason); InvalidatedAt = now;
    }

    public void Approve(string actor, Guid actorAccountId, DateTimeOffset now,
        string closurePackageJson, string closurePackageHash)
    {
        if (State != ProblemReportClosureCandidateState.Pending)
            throw new DomainException("Only the current pending closure candidate can be approved.");
        State = ProblemReportClosureCandidateState.Approved;
        ApprovedBy = Required(actor); ApprovedByAccountId = actorAccountId == Guid.Empty ? null : actorAccountId;
        ApprovedAt = now; PackageProvenance = "FrozenAtApproval";
        ClosurePackageJson = Required(closurePackageJson); ClosurePackageHash = Hash(closurePackageHash);
    }

    private static string Required(string? value) => string.IsNullOrWhiteSpace(value)
        ? throw new DomainException("Problem Report closure-candidate evidence is required.") : value.Trim();
    private static string Hash(string? value)
    {
        var hash = Required(value).ToLowerInvariant();
        if (hash.Length != 64 || hash.Any(character => !Uri.IsHexDigit(character)))
            throw new DomainException("A closure-candidate SHA-256 hash is required.");
        return hash;
    }
}

/// <summary>Immutable lifecycle record.  This is deliberately separate from edit-session snapshots so that
/// significant engineering decisions remain discoverable after a checkout has expired or been discarded.</summary>
public sealed class ProblemReportRevision
{
    private ProblemReportRevision() { }
    public ProblemReportRevision(Guid problemReportId, int revision, string eventType, string actor,
        string snapshotHash, string snapshotJson, DateTimeOffset occurredAt,
        int snapshotSchemaVersion = ProblemReportEvidenceContract.SchemaVersion,
        string? detail = null, string? evidenceJson = null, int? eventSchemaVersion = null,
        string? fromState = null, string? toState = null, string? rationale = null)
    {
        Id = Guid.NewGuid(); ProblemReportId = problemReportId; Revision = revision; EventType = Required(eventType);
        if (snapshotSchemaVersion < 0) throw new DomainException("A Problem Report snapshot schema cannot be negative.");
        Actor = Required(actor); SnapshotHash = Required(snapshotHash); SnapshotJson = Required(snapshotJson);
        SnapshotSchemaVersion = snapshotSchemaVersion; OccurredAt = occurredAt;
        Detail = detail?.Trim() ?? ""; EvidenceJson = evidenceJson;
        FromState = fromState?.Trim() ?? ""; ToState = toState?.Trim() ?? ""; Rationale = rationale?.Trim() ?? "";
        EventSchemaVersion = eventSchemaVersion ?? (evidenceJson is null ? 0 : 1);
        if (EventSchemaVersion < 0) throw new DomainException("A Problem Report event schema cannot be negative.");
    }
    public Guid Id { get; private set; }
    public Guid ProblemReportId { get; private set; }
    public int Revision { get; private set; }
    public string EventType { get; private set; } = "";
    public string Actor { get; private set; } = "";
    public string SnapshotHash { get; private set; } = "";
    public string SnapshotJson { get; private set; } = "";
    public int SnapshotSchemaVersion { get; private set; }
    public string Detail { get; private set; } = "";
    public string FromState { get; private set; } = "";
    public string ToState { get; private set; } = "";
    public string Rationale { get; private set; } = "";
    public string? EvidenceJson { get; private set; }
    public int EventSchemaVersion { get; private set; }
    public DateTimeOffset OccurredAt { get; private set; }
    private static string Required(string? value) => string.IsNullOrWhiteSpace(value) ? throw new DomainException("Problem-report evidence is required.") : value.Trim();
}

public sealed class ProblemReportLink
{
    private ProblemReportLink() { }
    public ProblemReportLink(Guid problemReportId, string artifactType, Guid artifactId, string relationship, string actor, DateTimeOffset now)
    {
        if (artifactId == Guid.Empty) throw new DomainException("A linked artifact is required.");
        Id = Guid.NewGuid(); ProblemReportId = problemReportId; ArtifactType = Required(artifactType, "A linked artifact type is required.");
        ArtifactId = artifactId; Relationship = Required(relationship, "A link relationship is required."); AddedBy = Required(actor, "A link actor is required."); AddedAt = now;
    }
    public Guid Id { get; private set; }
    public Guid ProblemReportId { get; private set; }
    public string ArtifactType { get; private set; } = "";
    public Guid ArtifactId { get; private set; }
    public string Relationship { get; private set; } = "";
    public string AddedBy { get; private set; } = "";
    public DateTimeOffset AddedAt { get; private set; }
    private static string Required(string? value, string error) => string.IsNullOrWhiteSpace(value) ? throw new DomainException(error) : value.Trim();
}

public sealed class ProblemReport
{
    private ProblemReport() { }
    public ProblemReport(Guid projectId, string reportNumber, string title, string problem, string analysis, string reportedBy, DateTimeOffset now,
        string classification = "Software anomaly", ProblemReportSeverity severity = ProblemReportSeverity.Major, ProblemReportPriority priority = ProblemReportPriority.Normal,
        string origin = "Test execution", string affectedConfiguration = "", Guid? targetReleaseId = null,
        string? responsibleEngineerId = null, string problemRich = "", string additionalInformation = "",
        string additionalInformationRich = "", string systemAircraftImpact = "", string impactAssessmentJson = "{}",
        ProblemReportCategory? category = null)
    {
        if (projectId == Guid.Empty) throw new DomainException("A problem-report project is required.");
        Id = Guid.NewGuid(); ProjectId = projectId; ReportNumber = Required(reportNumber, "A problem-report number is required.");
        NumberSequence = ProblemReportNumber.Sequence(ReportNumber);
        Title = Required(title, "A problem-report title is required."); Problem = Required(problem, "A problem statement is required.");
        Analysis = analysis?.Trim() ?? ""; ReportedBy = Required(reportedBy, "A problem-report owner is required.");
        Classification = Required(classification, "A problem-report classification is required."); Severity = severity; Priority = priority;
        Origin = Required(origin, "A problem-report origin is required."); AffectedConfiguration = affectedConfiguration?.Trim() ?? "";
        TargetReleaseId = targetReleaseId; ResponsibleEngineerId = Required(responsibleEngineerId ?? reportedBy, "A responsible engineer is required.");
        ProblemRich = problemRich?.Trim() ?? ""; AdditionalInformation = additionalInformation?.Trim() ?? "";
        AdditionalInformationRich = additionalInformationRich?.Trim() ?? ""; SystemAircraftImpact = systemAircraftImpact?.Trim() ?? "";
        ImpactAssessmentJson = ValidImpactJson(impactAssessmentJson);
        // Optional here on purpose. A report is raised the moment somebody hits the problem, and demanding
        // the classification first is how a Task Driver never gets written down at all; the Draft to
        // Ready-for-SCCB transition is where it becomes mandatory.
        if (category is not null) { Category = category.Value; CategoryProvenance = ProblemReportCategoryProvenance.Selected; }
        State = ProblemReportState.Draft; CreatedAt = UpdatedAt = now; Version = 1;
    }

    public Guid Id { get; private set; }
    public Guid ProjectId { get; private set; }
    public string ReportNumber { get; private set; } = "";
    public int NumberSequence { get; private set; }
    public int Revision { get; private set; }
    public string DisplayNumber => $"{ReportNumber}.{Revision:D2}";
    public string Title { get; private set; } = "";
    public string Problem { get; private set; } = "";
    public string Analysis { get; private set; } = "";
    public string ReportedBy { get; private set; } = "";
    public string ResponsibleEngineerId { get; private set; } = "";
    public Guid? TargetReleaseId { get; private set; }
    public string ProblemRich { get; private set; } = "";
    public string AdditionalInformation { get; private set; } = "";
    public string AdditionalInformationRich { get; private set; } = "";
    public string SystemAircraftImpact { get; private set; } = "";
    /// <summary>
    /// What kind of problem this is. Null only while a Draft is still being written — the
    /// Draft to Ready-for-SCCB transition refuses until it is answered, so nothing reaches review
    /// unclassified. Every report retained from before the vocabulary existed was given one by migration.
    /// </summary>
    public ProblemReportCategory? Category { get; private set; }

    /// <summary>
    /// Whether <see cref="Category"/> was chosen by a person or assigned by the migration. Null exactly
    /// when the category is. See <see cref="ProblemReportCategoryProvenance"/> for why this is recorded.
    /// </summary>
    public ProblemReportCategoryProvenance? CategoryProvenance { get; private set; }
    /// <summary>What can be done in the meantime, if anything. Empty means none has been recorded.</summary>
    public string Workaround { get; private set; } = "";
    public string ImpactAssessmentJson { get; private set; } = "{}";
    public string Classification { get; private set; } = "";
    public ProblemReportSeverity Severity { get; private set; }
    public ProblemReportPriority Priority { get; private set; }
    public string Origin { get; private set; } = "";
    public string AffectedConfiguration { get; private set; } = "";
    public string RootCause { get; private set; } = "";
    public string Effects { get; private set; } = "";
    public string Containment { get; private set; } = "";
    public string CorrectiveAction { get; private set; } = "";
    public ProblemReportDisposition? Disposition { get; private set; }
    public string DispositionRationale { get; private set; } = "";
    public Guid? ResolutionVerificationExecutionId { get; private set; }
    public Guid? ClosureApprovedBy { get; private set; }
    public string ClosureApprovedByName { get; private set; } = "";
    public DateTimeOffset? ClosureApprovedAt { get; private set; }
    public bool IsReleaseBlocker { get; private set; }
    public long ReleaseBlockerVersion { get; private set; }
    public string WaiverRationale { get; private set; } = "";
    public string WaivedBy { get; private set; } = "";
    public DateTimeOffset? WaivedAt { get; private set; }
    public ProblemReportState State { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public long Version { get; private set; }

    public void UpdateDraft(string title, string problem, string analysis, DateTimeOffset now)
    {
        EnsureEditable(); InvalidateClosureVerificationForChange(); Title = Required(title, "A problem-report title is required."); Problem = Required(problem, "A problem statement is required.");
        Analysis = analysis?.Trim() ?? ""; Touch(now);
    }

    /// <summary>
    /// Corrects what the report says. Deliberately not restricted to the responsible engineer.
    ///
    /// Describing the problem and owning the problem are different things. The person who can correct
    /// a wrong root cause is whoever knows the right one, and requiring reassignment first meant the
    /// alternative was a second report contradicting the first — two records where the truth needed
    /// one. <see cref="Reassign"/> and <see cref="Retarget"/> keep the owner check, because who is
    /// accountable and which build this lands in are decisions rather than corrections.
    ///
    /// The actor is still required and still recorded: the API takes the exclusive lease, and the
    /// caller writes a ProblemReportRevision naming whoever checked in.
    /// </summary>
    public void UpdateDetails(string actor, string title, string problem, string problemRich,
        string additionalInformation, string additionalInformationRich, string analysis, string rootCause,
        string correctiveAction, string systemAircraftImpact, string impactAssessmentJson,
        ProblemReportSeverity severity, ProblemReportPriority priority, DateTimeOffset now,
        ProblemReportCategory? category = null, string? workaround = null)
    {
        Required(actor, "A problem-report correction actor is required."); EnsureEditable(); InvalidateClosureVerificationForChange();
        // Choosing a category on the form is a person's judgement, which is what the migration's was not.
        // Once somebody has answered, the record stops describing the value as derived and never goes back.
        if (category is not null) { Category = category.Value; CategoryProvenance = ProblemReportCategoryProvenance.Selected; }
        if (workaround is not null) Workaround = workaround.Trim();
        Title = Required(title, "A problem-report title is required."); Problem = Required(problem, "A problem statement is required.");
        ProblemRich = problemRich?.Trim() ?? ""; AdditionalInformation = additionalInformation?.Trim() ?? "";
        AdditionalInformationRich = additionalInformationRich?.Trim() ?? ""; Analysis = analysis?.Trim() ?? "";
        RootCause = rootCause?.Trim() ?? ""; CorrectiveAction = correctiveAction?.Trim() ?? "";
        SystemAircraftImpact = systemAircraftImpact?.Trim() ?? ""; ImpactAssessmentJson = ValidImpactJson(impactAssessmentJson); Touch(now);
        Severity = severity; Priority = priority;
    }

    public void Reassign(string actor, string responsibleEngineerId, DateTimeOffset now, bool supervisoryRecovery = false)
    {
        if (!supervisoryRecovery) EnsureResponsible(actor);
        else Required(actor, "A supervisory recovery actor is required.");
        EnsureNotTerminal(); InvalidateClosureVerificationForChange();
        ResponsibleEngineerId = Required(responsibleEngineerId, "A responsible engineer is required."); Touch(now);
    }

    public void Retarget(string actor, Guid targetReleaseId, DateTimeOffset now)
    {
        EnsureResponsible(actor); EnsureNotTerminal(); InvalidateClosureVerificationForChange();
        if (targetReleaseId == Guid.Empty) throw new DomainException("A target build is required.");
        TargetReleaseId = targetReleaseId; Touch(now);
    }

    public void RecordContextLink(string actor, DateTimeOffset now)
    {
        EnsureResponsible(actor); EnsureNotTerminal(); Touch(now);
    }

    public void ReadyForSccb(string actor, DateTimeOffset now)
    {
        TransitionTo(ProblemReportState.ReadyForSccb, actor, null, now);
    }

    public void OpenBySccb(string actor, DateTimeOffset now)
    {
        TransitionTo(ProblemReportState.Open, actor, null, now);
    }

    public void BeginImplementation(string actor, DateTimeOffset now, bool automatic = false)
    {
        Required(actor, "An implementation actor is required.");
        TransitionTo(ProblemReportState.Implementing, actor, null, now);
    }

    public void RevertAutomaticImplementation(string actor, DateTimeOffset now)
    {
        TransitionTo(ProblemReportState.Open, actor, "Implementation source was removed.", now);
    }

    public void BeginInvestigation(string actor, string analysis, string rootCause, string effects, string containment, DateTimeOffset now)
    {
        EnsureNotTerminal();
        Analysis = Required(analysis, "Investigation analysis is required."); RootCause = rootCause?.Trim() ?? ""; Effects = effects?.Trim() ?? ""; Containment = containment?.Trim() ?? "";
        if (State == ProblemReportState.Open) State = ProblemReportState.Implementing;
        else if (State != ProblemReportState.Implementing) throw new DomainException("Only an Open or Implementing problem report can record investigation work.");
        Touch(now);
    }

    public void ProposeResolution(string actor, string correctiveAction, DateTimeOffset now)
    {
        if (State != ProblemReportState.Implementing) throw new DomainException("Only an Implementing problem report can enter verification.");
        CorrectiveAction = Required(correctiveAction, "A corrective action is required."); Disposition = null;
        TransitionTo(ProblemReportState.Verifying, actor, null, now);
    }

    public void RecordResolutionVerification(string actor, Guid executionId, DateTimeOffset now)
    {
        if (State != ProblemReportState.Verifying) throw new DomainException("Only a Verifying problem report can record closure-supporting evidence.");
        if (executionId == Guid.Empty) throw new DomainException("A successor test execution is required for resolution verification.");
        ResolutionVerificationExecutionId = executionId; TransitionTo(ProblemReportState.WaitingForSqaToClose, actor, null, now);
    }

    public void ApproveClosure(string actor, Guid actorAccountId, DateTimeOffset now)
    {
        if (string.Equals(actor, ReportedBy, StringComparison.OrdinalIgnoreCase) || string.Equals(actor, ResponsibleEngineerId, StringComparison.OrdinalIgnoreCase)) throw new DomainException("The problem-report author or responsible engineer cannot independently approve SQA closure.");
        if (State != ProblemReportState.WaitingForSqaToClose) throw new DomainException("A Problem Report must be waiting for SQA to close.");
        ClosureApprovedBy = actorAccountId == Guid.Empty ? null : actorAccountId; ClosureApprovedByName = Required(actor, "A closure approver is required."); ClosureApprovedAt = now;
        TransitionTo(ProblemReportState.Closed, actor, null, now);
    }

    public void ApplyDisposition(string actor, ProblemReportDisposition disposition, string rationale, Guid? duplicateOfId, DateTimeOffset now)
    {
        Required(actor, "A disposition actor is required."); EnsureNotTerminal();
        var requiredRationale = Required(rationale, "A disposition rationale is required.");
        var target = disposition switch
        {
            ProblemReportDisposition.Fixed => throw new DomainException("Use proposed resolution and verified closure for a fixed problem report."),
            ProblemReportDisposition.Duplicate when duplicateOfId is null || duplicateOfId.Value == Guid.Empty => throw new DomainException("A duplicate problem report must identify its original record."),
            ProblemReportDisposition.Duplicate or ProblemReportDisposition.CannotReproduce
                or ProblemReportDisposition.NoFaultFound or ProblemReportDisposition.AcceptedRisk or ProblemReportDisposition.Rejected => ProblemReportState.Rejected,
            ProblemReportDisposition.Deferred => ProblemReportState.Open,
            _ => ProblemReportState.Rejected,
        };
        Disposition = target == ProblemReportState.Rejected ? ProblemReportDisposition.Rejected : disposition;
        DispositionRationale = requiredRationale;
        if (ProblemReportTransitionPolicy.Canonical(State) == target)
        {
            InvalidateClosureVerificationForChange();
            Touch(now);
            return;
        }
        TransitionTo(target, actor, requiredRationale, now);
    }

    public void SetReleaseBlocker(string actor, bool isBlocker, DateTimeOffset now)
    {
        EnsureResponsible(actor); InvalidateClosureVerificationForChange();
        var newlyRaised = isBlocker && !IsReleaseBlocker; IsReleaseBlocker = isBlocker; Touch(now);
        if (newlyRaised) ReleaseBlockerVersion = Version;
    }

    public void RecordReleaseWaiverDecision(string actor, DateTimeOffset now)
    {
        Required(actor, "A release-waiver actor is required."); EnsureNotTerminal();
        if (!IsReleaseBlocker) throw new DomainException("Only a current release blocker can be waived.");
        InvalidateClosureVerificationForChange(); Touch(now);
    }

    public void Reopen(string actor, string rationale, DateTimeOffset now)
    {
        var target = State == ProblemReportState.Closed ? ProblemReportState.Verifying
            : State == ProblemReportState.Rejected ? ProblemReportState.Draft
            : throw new DomainException("Only a Closed or Rejected problem report can be reopened.");
        TransitionTo(target, actor, rationale, now);
    }

    public void ResumeDeferred(string actor, DateTimeOffset now)
    {
        if (State != ProblemReportState.Open) throw new DomainException("Only an Open problem report can be resumed.");
        Disposition = null; DispositionRationale = "";
        Touch(now);
    }

    /// <summary>Applies one edge of the canonical eight-state graph. Live role checks belong to the API.</summary>
    public void TransitionTo(ProblemReportState target, string actor, string? rationale, DateTimeOffset now)
    {
        Required(actor, "A Problem Report transition actor is required.");
        var source = ProblemReportTransitionPolicy.Canonical(State);
        target = ProblemReportTransitionPolicy.Canonical(target);
        if (!ProblemReportTransitionPolicy.IsAllowed(source, target))
            throw new DomainException($"A Problem Report cannot transition from {source} to {target}.");
        if (ProblemReportTransitionPolicy.RequiresRationale(source, target))
            rationale = Required(rationale, "A rationale is required for rejection and backward Problem Report transitions.");
        else rationale = rationale?.Trim();
        // A Draft may be unclassified — that is what a Draft is for. Leaving one is where the category
        // becomes mandatory, because SCCB is being asked to decide what to do about a problem, and what
        // kind of problem it is changes the answer. Rejecting an unclassified Draft outright stays
        // available: refusing to let somebody close a report they have already judged worthless would
        // only strand it.
        if (source == ProblemReportState.Draft && target == ProblemReportState.ReadyForSccb && Category is null)
            throw new DomainException("Choose a category before sending this Problem Report to the SCCB.");

        if (target == ProblemReportState.Rejected)
        {
            Disposition = ProblemReportDisposition.Rejected;
            DispositionRationale = rationale!;
            ResolutionVerificationExecutionId = null;
            ClosureApprovedBy = null; ClosureApprovedByName = ""; ClosureApprovedAt = null;
        }
        else if (source == ProblemReportState.Rejected)
        {
            Revision++; Disposition = null; DispositionRationale = "";
            ResolutionVerificationExecutionId = null;
            ClosureApprovedBy = null; ClosureApprovedByName = ""; ClosureApprovedAt = null;
        }
        else if (source == ProblemReportState.Closed && target == ProblemReportState.Verifying)
        {
            Revision++; Disposition = null; DispositionRationale = "";
            ResolutionVerificationExecutionId = null;
            ClosureApprovedBy = null; ClosureApprovedByName = ""; ClosureApprovedAt = null;
        }
        else if (source == ProblemReportState.WaitingForSqaToClose && target != ProblemReportState.Closed)
        {
            ResolutionVerificationExecutionId = null;
            ClosureApprovedBy = null; ClosureApprovedByName = ""; ClosureApprovedAt = null;
        }
        State = target;
        Touch(now);
        if ((source == ProblemReportState.Rejected
                || source == ProblemReportState.Closed && target == ProblemReportState.Verifying)
            && IsReleaseBlocker)
            ReleaseBlockerVersion = Version;
    }

    public string CanonicalSnapshot() => ProblemReportEvidenceContract.Serialize(this);
    public string CanonicalHash() => ProblemReportEvidenceContract.Hash(this);
    public bool InvalidateClosureVerification(string actor, DateTimeOffset now)
    {
        Required(actor, "An invalidation actor is required.");
        if (State != ProblemReportState.WaitingForSqaToClose) return false;
        InvalidateClosureVerificationForChange(); Touch(now); return true;
    }
    public bool PrepareControlledRelationshipChange(string actor, DateTimeOffset now)
    {
        Required(actor, "A controlled relationship actor is required."); EnsureNotTerminal();
        return InvalidateClosureVerification(actor, now);
    }
    private void Touch(DateTimeOffset now) { UpdatedAt = now; Version++; }
    private void InvalidateClosureVerificationForChange()
    {
        if (State != ProblemReportState.WaitingForSqaToClose) return;
        ResolutionVerificationExecutionId = null;
        State = ProblemReportState.Verifying;
    }
    private void EnsureResponsible(string actor) { if (!string.Equals(actor, ResponsibleEngineerId, StringComparison.OrdinalIgnoreCase)) throw new DomainException("Only the responsible engineer can perform this action."); }
    /// <summary>
    /// Editable unless the report is finished. A report is corrected while the work it describes is in
    /// flight, so waiting on SQA closure or sitting deferred is no reason to refuse a correction — only
    /// closure and the terminal dispositions are, and reopening is the route back from those.
    /// </summary>
    private void EnsureEditable() { if (State == ProblemReportState.Closed || IsTerminalDisposition()) throw new DomainException("The problem report is closed or dispositioned and is no longer editable. Reopen it first."); }
    private void EnsureNotTerminal() { if (State == ProblemReportState.Closed || IsTerminalDisposition()) throw new DomainException("The problem report is closed or dispositioned. Reopen it before changing lifecycle data."); }
    private bool IsTerminalDisposition() => ProblemReportTransitionPolicy.Canonical(State) == ProblemReportState.Rejected;
    private static string ValidImpactJson(string? value)
    {
        var candidate = string.IsNullOrWhiteSpace(value) ? "{}" : value.Trim();
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(candidate);
            if (document.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object) throw new Exception();
            var allowed = new[] { "SystemRequirements", "Hlr", "Llr", "Code", "Tests", "Documents", "SystemAircraft", "Airworthiness" };
            var normalized = allowed.ToDictionary(key => key, key => "Unknown");
            foreach (var property in document.RootElement.EnumerateObject())
            {
                // "Safety" is what this area was called before it was named for what is actually being
                // judged. Records written under the old name keep their answer rather than losing it, and a
                // client that has not been reloaded yet is still understood.
                var area = property.Name == "Safety" ? "Airworthiness" : property.Name;
                if (!normalized.ContainsKey(area)) throw new Exception();
                var assessment = property.Value.GetString();
                if (assessment is not ("Unknown" or "No" or "Yes")) throw new Exception();
                normalized[area] = assessment;
            }
            return System.Text.Json.JsonSerializer.Serialize(normalized);
        }
        catch { throw new DomainException("The problem-report impact assessment must be a JSON object."); }
    }
    private static string Required(string? value, string error) => string.IsNullOrWhiteSpace(value) ? throw new DomainException(error) : value.Trim();
}
