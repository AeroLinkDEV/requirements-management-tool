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
        Guid? verificationExecutionId, string verificationEvidenceJson, string verificationEvidenceHash,
        string linksManifestJson, string linksManifestHash, string manifestHash,
        string selectedBy, DateTimeOffset selectedAt, int reportSnapshotSchemaVersion)
    {
        // A null execution means the report was sent on an attested statement (#1113); the evidence JSON then
        // carries that statement, and is still required below.
        if (problemReportId == Guid.Empty || verificationExecutionId == Guid.Empty)
            throw new DomainException("A closure candidate requires its Problem Report and a verification execution or attested statement.");
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
    public Guid? VerificationExecutionId { get; private set; }
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
        string? fromState = null, string? toState = null, string? rationale = null,
        string? actorDisplayName = null)
    {
        Id = Guid.NewGuid(); ProblemReportId = problemReportId; Revision = revision; EventType = Required(eventType);
        if (snapshotSchemaVersion < 0) throw new DomainException("A Problem Report snapshot schema cannot be negative.");
        Actor = Required(actor); SnapshotHash = Required(snapshotHash); SnapshotJson = Required(snapshotJson);
        // Captured at the moment the event happened, never resolved later. See the property's own note.
        ActorDisplayName = string.IsNullOrWhiteSpace(actorDisplayName) ? null : actorDisplayName.Trim();
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
    /// <summary>
    /// The actor's human-readable name as it stood when this event occurred, or null when none was captured.
    ///
    /// Deliberately frozen rather than resolved from <c>UserAccount.DisplayName</c> at read time. An audit
    /// entry that says who did something must not change its answer because the directory was edited
    /// afterwards: if <c>jane.smith</c> approved a report in 2026 and the account is renamed in 2028, the
    /// 2026 entry still records the name that was true when she signed it. Live resolution would silently
    /// rewrite a frozen fact with no controlled revision to explain it.
    ///
    /// Null on every event recorded before this was captured, and on any event raised without an
    /// authenticated person behind it. Null means "no name was captured", not "look it up" — the honest
    /// rendering is then <see cref="Actor"/>, the login handle, which is what an auditor reconciles against
    /// the identity provider anyway. It is never backfilled from today's directory.
    ///
    /// Not part of <see cref="ProblemReportEvidenceContract"/>: the evidence snapshot hashes the report's
    /// content, and this sits beside it on the event. Adding it therefore leaves every historical
    /// <see cref="SnapshotHash"/> recomputing exactly as it was written.
    /// </summary>
    public string? ActorDisplayName { get; private set; }
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

/// <summary>
/// The authored companions to a Problem Report's narrative fields, and the two investigation fields the
/// editor can now reach.
///
/// A record rather than nine more parameters on UpdateDetails: they are all strings, and nine adjacent
/// string parameters is a transposition the compiler cannot see. Null means "not supplied" for Effects
/// and Containment, which lets a caller that does not show them leave them alone.
/// </summary>
public sealed record ProblemReportNarrative(
    string? AnalysisRich = null,
    string? RootCauseRich = null,
    string? WorkaroundRich = null,
    string? CorrectiveActionRich = null,
    string? SystemAircraftImpactRich = null,
    string? Effects = null,
    string? EffectsRich = null,
    string? Containment = null,
    string? ContainmentRich = null);

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
        Title = Required(title, "A problem-report title is required.");
        ProblemRich = CanonicalRich(problemRich);
        Problem = Required(ProjectionOrPlain(ProblemRich, problem), "A problem statement is required.");
        Analysis = analysis?.Trim() ?? ""; ReportedBy = Required(reportedBy, "A problem-report owner is required.");
        Classification = Required(classification, "A problem-report classification is required."); Severity = severity; Priority = priority;
        Origin = Required(origin, "A problem-report origin is required."); AffectedConfiguration = affectedConfiguration?.Trim() ?? "";
        TargetReleaseId = targetReleaseId; ResponsibleEngineerId = Required(responsibleEngineerId ?? reportedBy, "A responsible engineer is required.");
        AdditionalInformationRich = CanonicalRich(additionalInformationRich);
        AdditionalInformation = ProjectionOrPlain(AdditionalInformationRich, additionalInformation);
        SystemAircraftImpact = systemAircraftImpact?.Trim() ?? "";
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
    /// <summary>
    /// The authored form of <see cref="Analysis"/>. Every narrative field on this record carries a plain
    /// column and a rich one, exactly as Problem and ProblemRich always have: the plain value stays the
    /// single source for search, the generated documents and any reader that cannot render structure, and
    /// the rich one holds what the author actually wrote. Empty means nothing structured was authored —
    /// the plain value is then the whole truth, and is what the record shows.
    /// </summary>
    public string AnalysisRich { get; private set; } = "";
    public string ReportedBy { get; private set; } = "";
    public string ResponsibleEngineerId { get; private set; } = "";
    public Guid? TargetReleaseId { get; private set; }
    public string ProblemRich { get; private set; } = "";
    public string AdditionalInformation { get; private set; } = "";
    public string AdditionalInformationRich { get; private set; } = "";
    public string SystemAircraftImpact { get; private set; } = "";
    public string SystemAircraftImpactRich { get; private set; } = "";
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
    public string WorkaroundRich { get; private set; } = "";
    public string ImpactAssessmentJson { get; private set; } = "{}";
    public string Classification { get; private set; } = "";
    public ProblemReportSeverity Severity { get; private set; }
    public ProblemReportPriority Priority { get; private set; }
    public string Origin { get; private set; } = "";
    public string AffectedConfiguration { get; private set; } = "";
    public string RootCause { get; private set; } = "";
    public string RootCauseRich { get; private set; } = "";
    public string Effects { get; private set; } = "";
    public string EffectsRich { get; private set; } = "";
    public string Containment { get; private set; } = "";
    public string ContainmentRich { get; private set; } = "";
    public string CorrectiveAction { get; private set; } = "";
    public string CorrectiveActionRich { get; private set; } = "";
    public ProblemReportDisposition? Disposition { get; private set; }
    public string DispositionRationale { get; private set; } = "";
    public Guid? ResolutionVerificationExecutionId { get; private set; }
    /// <summary>
    /// The engineer's attested account of how the correction was verified, sent to SQA in place of a test
    /// execution when the project does not use Verification (#1113, DEC-137). Null whenever a test execution
    /// is the basis, and withdrawn exactly like one.
    /// </summary>
    public string? ResolutionAttestation { get; private set; }
    public Guid? ClosureApprovedBy { get; private set; }
    public string ClosureApprovedByName { get; private set; } = "";
    public DateTimeOffset? ClosureApprovedAt { get; private set; }
    public bool IsReleaseBlocker { get; private set; }
    public long ReleaseBlockerVersion { get; private set; }
    public string WaiverRationale { get; private set; } = "";
    public string WaivedBy { get; private set; } = "";
    public DateTimeOffset? WaivedAt { get; private set; }
    public ProblemReportState State { get; private set; }

    // Source provenance (#1114). All null on a report raised in AeroLink, so native snapshots are unchanged.
    /// <summary>The tool or export the report was imported from.</summary>
    public string? SourceSystem { get; private set; }
    /// <summary>The report's identity in its source, kept permanently and searchable.</summary>
    public string? SourceKey { get; private set; }
    /// <summary>Who raised it in the source, as the source recorded them. Never an AeroLink actor.</summary>
    public string? SourceReportedBy { get; private set; }
    public DateTimeOffset? SourceCreatedAt { get; private set; }
    /// <summary>The source's own status text at import.</summary>
    public string? SourceState { get; private set; }
    /// <summary>True when the source had already closed it: a read-only historical record with no AeroLink SQA closure.</summary>
    public bool? ClosedInSource { get; private set; }
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
        ProblemReportCategory? category = null, string? workaround = null,
        ProblemReportNarrative? narrative = null)
    {
        Required(actor, "A problem-report correction actor is required."); EnsureEditable(); InvalidateClosureVerificationForChange();
        // Choosing a category on the form is a person's judgement, which is what the migration's was not.
        // Once somebody has answered, the record stops describing the value as derived and never goes back.
        if (category is not null) { Category = category.Value; CategoryProvenance = ProblemReportCategoryProvenance.Selected; }
        if (workaround is not null) Workaround = workaround.Trim();
        Title = Required(title, "A problem-report title is required.");
        ProblemRich = CanonicalRich(problemRich);
        Problem = Required(ProjectionOrPlain(ProblemRich, problem), "A problem statement is required.");
        AdditionalInformationRich = CanonicalRich(additionalInformationRich);
        AdditionalInformation = ProjectionOrPlain(AdditionalInformationRich, additionalInformation);
        Analysis = analysis?.Trim() ?? "";
        RootCause = rootCause?.Trim() ?? ""; CorrectiveAction = correctiveAction?.Trim() ?? "";
        SystemAircraftImpact = systemAircraftImpact?.Trim() ?? ""; ImpactAssessmentJson = ValidImpactJson(impactAssessmentJson);
        // Effects and Containment are authored here as well as through BeginInvestigation. They are part
        // of the record a person is looking at, and a field the editor shows but cannot save would be
        // worse than one it hides.
        if (narrative is { } authored)
        {
            AnalysisRich = CanonicalRich(authored.AnalysisRich);
            Analysis = ProjectionOrPlain(AnalysisRich, Analysis);
            RootCauseRich = CanonicalRich(authored.RootCauseRich);
            RootCause = ProjectionOrPlain(RootCauseRich, RootCause);
            WorkaroundRich = CanonicalRich(authored.WorkaroundRich);
            Workaround = ProjectionOrPlain(WorkaroundRich, Workaround);
            CorrectiveActionRich = CanonicalRich(authored.CorrectiveActionRich);
            CorrectiveAction = ProjectionOrPlain(CorrectiveActionRich, CorrectiveAction);
            SystemAircraftImpactRich = CanonicalRich(authored.SystemAircraftImpactRich);
            SystemAircraftImpact = ProjectionOrPlain(SystemAircraftImpactRich, SystemAircraftImpact);
            if (authored.Effects is not null) Effects = authored.Effects.Trim();
            if (authored.EffectsRich is not null)
            {
                EffectsRich = CanonicalRich(authored.EffectsRich);
                Effects = ProjectionOrPlain(EffectsRich, Effects);
            }
            if (authored.Containment is not null) Containment = authored.Containment.Trim();
            if (authored.ContainmentRich is not null)
            {
                ContainmentRich = CanonicalRich(authored.ContainmentRich);
                Containment = ProjectionOrPlain(ContainmentRich, Containment);
            }
        }
        Touch(now);
        Severity = severity; Priority = priority;
    }

    /// <summary>
    /// Applies the authored fields supplied when the report was raised.
    ///
    /// Separate from the constructor because the constructor's job is identity and lifecycle, and separate
    /// from UpdateDetails because there is no editor lease here and nothing to invalidate — the record is
    /// one instant old. It writes the same fields UpdateDetails writes, so a report raised whole and one
    /// corrected later obey one set of rules.
    /// </summary>
    public void AuthorOnCreate(ProblemReportNarrative narrative, string? rootCause, string? correctiveAction,
        string? workaround, DateTimeOffset now)
    {
        if (rootCause is not null) RootCause = rootCause.Trim();
        if (correctiveAction is not null) CorrectiveAction = correctiveAction.Trim();
        if (workaround is not null) Workaround = workaround.Trim();
        AnalysisRich = CanonicalRich(narrative.AnalysisRich);
        Analysis = ProjectionOrPlain(AnalysisRich, Analysis);
        RootCauseRich = CanonicalRich(narrative.RootCauseRich);
        RootCause = ProjectionOrPlain(RootCauseRich, RootCause);
        WorkaroundRich = CanonicalRich(narrative.WorkaroundRich);
        Workaround = ProjectionOrPlain(WorkaroundRich, Workaround);
        CorrectiveActionRich = CanonicalRich(narrative.CorrectiveActionRich);
        CorrectiveAction = ProjectionOrPlain(CorrectiveActionRich, CorrectiveAction);
        SystemAircraftImpactRich = CanonicalRich(narrative.SystemAircraftImpactRich);
        SystemAircraftImpact = ProjectionOrPlain(SystemAircraftImpactRich, SystemAircraftImpact);
        if (narrative.Effects is not null) Effects = narrative.Effects.Trim();
        if (narrative.EffectsRich is not null)
        {
            EffectsRich = CanonicalRich(narrative.EffectsRich);
            Effects = ProjectionOrPlain(EffectsRich, Effects);
        }
        if (narrative.Containment is not null) Containment = narrative.Containment.Trim();
        if (narrative.ContainmentRich is not null)
        {
            ContainmentRich = CanonicalRich(narrative.ContainmentRich);
            Containment = ProjectionOrPlain(ContainmentRich, Containment);
        }
        Touch(now);
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

    public void BeginImplementation(string actor, DateTimeOffset now)
    {
        Required(actor, "An implementation actor is required.");
        TransitionTo(ProblemReportState.Implementing, actor, null, now);
    }

    /// <summary>
    /// Records investigation work. It never moves the report (#1088): an Open report stays Open until a person
    /// starts implementation, however much analysis has been written against it.
    /// </summary>
    public void BeginInvestigation(string actor, string analysis, string rootCause, string effects, string containment, DateTimeOffset now)
    {
        EnsureNotTerminal();
        if (State is not (ProblemReportState.Open or ProblemReportState.Implementing))
            throw new DomainException("Only an Open or Implementing problem report can record investigation work.");
        Analysis = Required(analysis, "Investigation analysis is required."); RootCause = rootCause?.Trim() ?? ""; Effects = effects?.Trim() ?? ""; Containment = containment?.Trim() ?? "";
        Touch(now);
    }

    public void ProposeResolution(string actor, string correctiveAction, DateTimeOffset now)
    {
        if (State != ProblemReportState.Implementing) throw new DomainException("Only an Implementing problem report can enter verification.");
        CorrectiveAction = Required(correctiveAction, "A corrective action is required."); Disposition = null;
        TransitionTo(ProblemReportState.Verifying, actor, null, now);
    }

    /// <summary>
    /// A person sends a Verifying report to SQA on a passing result they have chosen. This is the only way
    /// into WaitingForSqaToClose (#1088): the evidence and the decision to rely on it are one act, confirmed
    /// by that person. Recording a passing result elsewhere never calls this on the recorder's behalf.
    /// </summary>
    public void RecordResolutionVerification(string actor, Guid executionId, DateTimeOffset now, string? rationale = null)
    {
        if (State != ProblemReportState.Verifying) throw new DomainException("Only a Verifying problem report can record closure-supporting evidence.");
        if (executionId == Guid.Empty) throw new DomainException("A successor test execution is required for resolution verification.");
        ResolutionVerificationExecutionId = executionId; ResolutionAttestation = null; TransitionTo(ProblemReportState.WaitingForSqaToClose, actor, rationale, now);
    }

    /// <summary>
    /// The same act for a project that does not use Verification (#1113, DEC-137): the person sends the
    /// report to SQA on their attested account of how the correction was verified. The caller has already
    /// established that the project's Verification feature is off; SQA still closes independently.
    /// </summary>
    public void RecordResolutionAttestation(string actor, string statement, DateTimeOffset now, string? rationale = null)
    {
        if (State != ProblemReportState.Verifying) throw new DomainException("Only a Verifying problem report can record closure-supporting evidence.");
        var text = statement?.Trim() ?? "";
        if (text.Length < 20) throw new DomainException("Describe how the correction was verified (at least 20 characters) before sending this report to SQA.");
        if (text.Length > 8000) throw new DomainException("The verification statement is limited to 8000 characters.");
        ResolutionVerificationExecutionId = null; ResolutionAttestation = text; TransitionTo(ProblemReportState.WaitingForSqaToClose, actor, rationale, now);
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

    /// <summary>
    /// A report brought in from another tool (#1114). It lands in the state the importer mapped the source
    /// status to, which is a source fact, not an AeroLink lifecycle event; a report the source had closed
    /// lands Closed-in-source with no disposition and no SQA closure. The source identity, reporter, date
    /// and status are kept as source facts beside the AeroLink ones.
    /// </summary>
    public static ProblemReport Import(Guid projectId, string reportNumber, string title, string problem,
        string analysis, string raisedBy, string responsibleEngineerId, DateTimeOffset now,
        ProblemReportSeverity severity, ProblemReportPriority priority, ProblemReportCategory? category,
        Guid? targetReleaseId, string sourceSystem, string sourceKey, string? sourceReportedBy,
        DateTimeOffset? sourceCreatedAt, string sourceState, ProblemReportState landingState, bool closedInSource,
        string rootCause = "", string correctiveAction = "")
    {
        var allowed = closedInSource
            ? landingState == ProblemReportState.Closed
            : landingState is ProblemReportState.Draft or ProblemReportState.ReadyForSccb or ProblemReportState.Open
                or ProblemReportState.Implementing or ProblemReportState.Verifying;
        if (!allowed) throw new DomainException($"An imported report cannot land in {landingState}.");
        if (landingState != ProblemReportState.Draft && category is null)
            throw new DomainException("A category is required for an imported report beyond Draft.");
        var report = new ProblemReport(projectId, reportNumber, title, problem, analysis, raisedBy, now,
            severity: severity, priority: priority, origin: $"Imported from {sourceSystem.Trim()}",
            targetReleaseId: targetReleaseId, responsibleEngineerId: responsibleEngineerId, category: category);
        if (category is not null) report.CategoryProvenance = ProblemReportCategoryProvenance.ImportMapped;
        report.SourceSystem = Required(sourceSystem, "An imported report requires its source system.");
        report.SourceKey = Required(sourceKey, "An imported report requires its source key.");
        report.SourceReportedBy = string.IsNullOrWhiteSpace(sourceReportedBy) ? null : sourceReportedBy.Trim();
        report.SourceCreatedAt = sourceCreatedAt;
        report.SourceState = string.IsNullOrWhiteSpace(sourceState) ? null : sourceState.Trim();
        report.ClosedInSource = closedInSource ? true : null;
        report.RootCause = rootCause.Trim(); report.CorrectiveAction = correctiveAction.Trim();
        report.State = landingState;
        return report;
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
        if (ClosedInSource == true)
            throw new DomainException("This report was closed in its source tool and is kept as a read-only historical record.");
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
        // SQA is asked to close on evidence, so neither the way in nor the way out may be taken without it
        // (#1088). A report whose closure basis was withdrawn by a later change keeps its state and cannot
        // be closed until a person returns it to Verifying and sends it again on a fresh passing result.
        if (source == ProblemReportState.Verifying && target == ProblemReportState.WaitingForSqaToClose
            && ResolutionVerificationExecutionId is null && ResolutionAttestation is null)
            throw new DomainException("Choose the passing closure-supporting result before sending this Problem Report to SQA.");
        if (source == ProblemReportState.WaitingForSqaToClose && target == ProblemReportState.Closed
            && ResolutionVerificationExecutionId is null && ResolutionAttestation is null)
            throw new DomainException("The closure basis for this Problem Report was withdrawn by a later change. Return it to Verifying and send it to SQA on a fresh passing result.");

        if (target == ProblemReportState.Rejected)
        {
            Disposition = ProblemReportDisposition.Rejected;
            DispositionRationale = rationale!;
            ResolutionVerificationExecutionId = null; ResolutionAttestation = null;
            ClosureApprovedBy = null; ClosureApprovedByName = ""; ClosureApprovedAt = null;
        }
        else if (source == ProblemReportState.Rejected)
        {
            Revision++; Disposition = null; DispositionRationale = "";
            ResolutionVerificationExecutionId = null; ResolutionAttestation = null;
            ClosureApprovedBy = null; ClosureApprovedByName = ""; ClosureApprovedAt = null;
        }
        else if (source == ProblemReportState.Closed && target == ProblemReportState.Verifying)
        {
            Revision++; Disposition = null; DispositionRationale = "";
            ResolutionVerificationExecutionId = null; ResolutionAttestation = null;
            ClosureApprovedBy = null; ClosureApprovedByName = ""; ClosureApprovedAt = null;
        }
        else if (source == ProblemReportState.WaitingForSqaToClose && target != ProblemReportState.Closed)
        {
            ResolutionVerificationExecutionId = null; ResolutionAttestation = null;
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
        if (!HasClosureBasis()) return false;
        InvalidateClosureVerificationForChange(); Touch(now); return true;
    }
    /// <summary>
    /// Waiting on SQA with the passing result it was sent on still standing. False once a later change has
    /// withdrawn that basis, which leaves the state where it was and the report unclosable (#1088).
    /// </summary>
    public bool HasClosureBasis() =>
        State == ProblemReportState.WaitingForSqaToClose && (ResolutionVerificationExecutionId is not null || ResolutionAttestation is not null);
    public bool PrepareControlledRelationshipChange(string actor, DateTimeOffset now)
    {
        Required(actor, "A controlled relationship actor is required."); EnsureNotTerminal();
        return InvalidateClosureVerification(actor, now);
    }
    private void Touch(DateTimeOffset now) { UpdatedAt = now; Version++; }
    /// <summary>
    /// A change to what SQA was asked to close on withdraws the closure basis. It does not move the report:
    /// the lifecycle changes only by a person's explicit transition (#1088), so the report stays waiting on
    /// SQA, and closure is refused until someone returns it to Verifying and sends it on fresh evidence.
    /// </summary>
    private void InvalidateClosureVerificationForChange()
    {
        if (State != ProblemReportState.WaitingForSqaToClose) return;
        ResolutionVerificationExecutionId = null; ResolutionAttestation = null;
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
    private static string CanonicalRich(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        var canonical = AeroLink.Domain.Content.RichContent.Canonicalize(value);
        if (AeroLink.Domain.Content.RichContent.Read(canonical).Any(block =>
                block.Kind == AeroLink.Domain.Content.RichBlockKind.Image
                && string.IsNullOrWhiteSpace(block.Alt)))
            throw new DomainException("Every Problem Report figure needs descriptive alternative text.");
        return canonical;
    }

    private static string ProjectionOrPlain(string? canonicalRich, string? plain)
    {
        var blocks = AeroLink.Domain.Content.RichContent.Read(canonicalRich);
        // Plain-only and legacy clients remain valid, including the old spelling {"blocks":[]} alongside
        // a populated plain field. Once any typed block exists, however, it is the authored record and the
        // server derives the companion projection itself. A raw client must never make search, generated
        // output and an approver's rich reader describe different controlled facts in one signed revision.
        return blocks.Count == 0
            ? plain?.Trim() ?? ""
            : AeroLink.Domain.Content.RichContent.ToPlainText(blocks);
    }
    private static string Required(string? value, string error) => string.IsNullOrWhiteSpace(value) ? throw new DomainException(error) : value.Trim();
}
