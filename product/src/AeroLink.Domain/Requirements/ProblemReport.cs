using System.Security.Cryptography;
using System.Text;
using AeroLink.Domain.Common;

namespace AeroLink.Domain.Requirements;

public enum ProblemReportState
{
    Draft, ReadyForSccb, Open, Implementing, Verifying, AwaitingSqaClosure, Closed, Deferred,
    // Retained so existing controlled records remain readable after the MVP lifecycle migration.
    Investigating, ResolutionProposed, AwaitingClosureApproval,
    Duplicate, CannotReproduce, NoFaultFound, AcceptedRisk, Rejected
}

public enum ProblemReportSeverity { Critical, High, Major, Minor, Trivial }

/// <summary>
/// What kind of thing is wrong, so a queue can be filtered down to the work one discipline owns.
///
/// Stored by name, so adding a kind later is a code change and not a data migration. <c>Other</c> exists
/// because every report predating this field is genuinely unclassified, and because a report that fits none
/// of the named kinds has to go somewhere other than the nearest wrong answer.
/// </summary>
public enum ProblemReportType { Documentation, Code, Test, Other }
public enum ProblemReportPriority { Urgent, High, Normal, Low }
public enum ProblemReportDisposition { Fixed, Duplicate, CannotReproduce, NoFaultFound, Deferred, AcceptedRisk, Rejected }

/// <summary>Immutable lifecycle record.  This is deliberately separate from edit-session snapshots so that
/// significant engineering decisions remain discoverable after a checkout has expired or been discarded.</summary>
public sealed class ProblemReportRevision
{
    private ProblemReportRevision() { }
    public ProblemReportRevision(Guid problemReportId, int revision, string eventType, string actor,
        string snapshotHash, string snapshotJson, DateTimeOffset occurredAt)
    {
        Id = Guid.NewGuid(); ProblemReportId = problemReportId; Revision = revision; EventType = Required(eventType);
        Actor = Required(actor); SnapshotHash = Required(snapshotHash); SnapshotJson = Required(snapshotJson); OccurredAt = occurredAt;
    }
    public Guid Id { get; private set; }
    public Guid ProblemReportId { get; private set; }
    public int Revision { get; private set; }
    public string EventType { get; private set; } = "";
    public string Actor { get; private set; } = "";
    public string SnapshotHash { get; private set; } = "";
    public string SnapshotJson { get; private set; } = "";
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
        string additionalInformationRich = "", string systemAircraftImpact = "", string impactAssessmentJson = "{}")
    {
        if (projectId == Guid.Empty) throw new DomainException("A problem-report project is required.");
        Id = Guid.NewGuid(); ProjectId = projectId; ReportNumber = Required(reportNumber, "A problem-report number is required.");
        Title = Required(title, "A problem-report title is required."); Problem = Required(problem, "A problem statement is required.");
        Analysis = analysis?.Trim() ?? ""; ReportedBy = Required(reportedBy, "A problem-report owner is required.");
        Classification = Required(classification, "A problem-report classification is required."); Severity = severity; Priority = priority;
        Origin = Required(origin, "A problem-report origin is required."); AffectedConfiguration = affectedConfiguration?.Trim() ?? "";
        TargetReleaseId = targetReleaseId; ResponsibleEngineerId = Required(responsibleEngineerId ?? reportedBy, "A responsible engineer is required.");
        ProblemRich = problemRich?.Trim() ?? ""; AdditionalInformation = additionalInformation?.Trim() ?? "";
        AdditionalInformationRich = additionalInformationRich?.Trim() ?? ""; SystemAircraftImpact = systemAircraftImpact?.Trim() ?? "";
        ImpactAssessmentJson = ValidImpactJson(impactAssessmentJson);
        State = ProblemReportState.Draft; CreatedAt = UpdatedAt = now; Version = 1;
    }

    public Guid Id { get; private set; }
    public Guid ProjectId { get; private set; }
    public string ReportNumber { get; private set; } = "";
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
    /// <summary>Which discipline's problem this is, for filtering the queue.</summary>
    public ProblemReportType Type { get; private set; } = ProblemReportType.Other;
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
    public string WaiverRationale { get; private set; } = "";
    public string WaivedBy { get; private set; } = "";
    public DateTimeOffset? WaivedAt { get; private set; }
    public ProblemReportState State { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public long Version { get; private set; }

    public void UpdateDraft(string title, string problem, string analysis, DateTimeOffset now)
    {
        EnsureEditable(); Title = Required(title, "A problem-report title is required."); Problem = Required(problem, "A problem statement is required.");
        Analysis = analysis?.Trim() ?? ""; Touch(now);
    }

    public void UpdateDetails(string actor, string title, string problem, string problemRich,
        string additionalInformation, string additionalInformationRich, string analysis, string rootCause,
        string correctiveAction, string systemAircraftImpact, string impactAssessmentJson,
        ProblemReportSeverity severity, ProblemReportPriority priority, DateTimeOffset now,
        ProblemReportType? type = null, string? workaround = null)
    {
        EnsureResponsible(actor); EnsureEditable();
        if (type is not null) Type = type.Value;
        if (workaround is not null) Workaround = workaround.Trim();
        Title = Required(title, "A problem-report title is required."); Problem = Required(problem, "A problem statement is required.");
        ProblemRich = problemRich?.Trim() ?? ""; AdditionalInformation = additionalInformation?.Trim() ?? "";
        AdditionalInformationRich = additionalInformationRich?.Trim() ?? ""; Analysis = analysis?.Trim() ?? "";
        RootCause = rootCause?.Trim() ?? ""; CorrectiveAction = correctiveAction?.Trim() ?? "";
        SystemAircraftImpact = systemAircraftImpact?.Trim() ?? ""; ImpactAssessmentJson = ValidImpactJson(impactAssessmentJson); Touch(now);
        Severity = severity; Priority = priority;
    }

    public void Reassign(string actor, string responsibleEngineerId, DateTimeOffset now)
    {
        EnsureResponsible(actor); EnsureNotTerminal();
        ResponsibleEngineerId = Required(responsibleEngineerId, "A responsible engineer is required."); Touch(now);
    }

    public void Retarget(string actor, Guid targetReleaseId, DateTimeOffset now)
    {
        EnsureResponsible(actor); EnsureNotTerminal();
        if (targetReleaseId == Guid.Empty) throw new DomainException("A target build is required.");
        TargetReleaseId = targetReleaseId; Touch(now);
    }

    public void RecordContextLink(string actor, DateTimeOffset now)
    {
        EnsureResponsible(actor); EnsureNotTerminal(); Touch(now);
    }

    public void ReadyForSccb(string actor, DateTimeOffset now)
    {
        EnsureResponsible(actor);
        if (State != ProblemReportState.Draft) throw new DomainException("Only a Draft problem report can be made ready for SCCB.");
        State = ProblemReportState.ReadyForSccb; Touch(now);
    }

    public void OpenBySccb(string actor, DateTimeOffset now)
    {
        Required(actor, "An SCCB actor is required.");
        if (State != ProblemReportState.ReadyForSccb) throw new DomainException("Only a problem report ready for SCCB can be opened.");
        State = ProblemReportState.Open; Touch(now);
    }

    public void BeginImplementation(string actor, DateTimeOffset now, bool automatic = false)
    {
        if (!automatic) EnsureResponsible(actor); else Required(actor, "An implementation actor is required.");
        if (State != ProblemReportState.Open) throw new DomainException("Only an Open problem report can begin implementation.");
        State = ProblemReportState.Implementing; Touch(now);
    }

    public void BeginInvestigation(string actor, string analysis, string rootCause, string effects, string containment, DateTimeOffset now)
    {
        EnsureResponsible(actor); EnsureNotTerminal();
        Analysis = Required(analysis, "Investigation analysis is required."); RootCause = rootCause?.Trim() ?? ""; Effects = effects?.Trim() ?? ""; Containment = containment?.Trim() ?? "";
        if (State == ProblemReportState.Open) State = ProblemReportState.Implementing;
        else if (State != ProblemReportState.Implementing) throw new DomainException("Only an Open or Implementing problem report can record investigation work.");
        Touch(now);
    }

    public void ProposeResolution(string actor, string correctiveAction, DateTimeOffset now)
    {
        EnsureResponsible(actor); if (State != ProblemReportState.Implementing) throw new DomainException("Only an Implementing problem report can enter verification.");
        CorrectiveAction = Required(correctiveAction, "A corrective action is required."); Disposition = ProblemReportDisposition.Fixed; State = ProblemReportState.Verifying; Touch(now);
    }

    public void RecordResolutionVerification(string actor, Guid executionId, DateTimeOffset now)
    {
        EnsureResponsible(actor); if (State != ProblemReportState.Verifying) throw new DomainException("Only a Verifying problem report can record closure-supporting evidence.");
        if (executionId == Guid.Empty) throw new DomainException("A successor test execution is required for resolution verification.");
        ResolutionVerificationExecutionId = executionId; State = ProblemReportState.AwaitingSqaClosure; Touch(now);
    }

    public void ApproveClosure(string actor, Guid actorAccountId, DateTimeOffset now)
    {
        if (string.Equals(actor, ReportedBy, StringComparison.OrdinalIgnoreCase) || string.Equals(actor, ResponsibleEngineerId, StringComparison.OrdinalIgnoreCase)) throw new DomainException("The problem-report author or responsible engineer cannot independently approve SQA closure.");
        if (State != ProblemReportState.AwaitingSqaClosure) throw new DomainException("Verified closure evidence must be awaiting SQA closure.");
        ClosureApprovedBy = actorAccountId == Guid.Empty ? null : actorAccountId; ClosureApprovedByName = Required(actor, "A closure approver is required."); ClosureApprovedAt = now;
        State = ProblemReportState.Closed; Touch(now);
    }

    public void ApplyDisposition(string actor, ProblemReportDisposition disposition, string rationale, Guid? duplicateOfId, DateTimeOffset now)
    {
        EnsureResponsible(actor); EnsureNotTerminal(); DispositionRationale = Required(rationale, "A disposition rationale is required."); Disposition = disposition;
        State = disposition switch
        {
            ProblemReportDisposition.Fixed => throw new DomainException("Use proposed resolution and verified closure for a fixed problem report."),
            ProblemReportDisposition.Duplicate when duplicateOfId is null || duplicateOfId.Value == Guid.Empty => throw new DomainException("A duplicate problem report must identify its original record."),
            ProblemReportDisposition.Duplicate => ProblemReportState.Duplicate,
            ProblemReportDisposition.CannotReproduce => ProblemReportState.CannotReproduce,
            ProblemReportDisposition.NoFaultFound => ProblemReportState.NoFaultFound,
            ProblemReportDisposition.Deferred => ProblemReportState.Deferred,
            ProblemReportDisposition.AcceptedRisk => ProblemReportState.AcceptedRisk,
            _ => ProblemReportState.Rejected
        };
        Touch(now);
    }

    public void SetReleaseBlocker(string actor, bool isBlocker, string waiverRationale, DateTimeOffset now)
    {
        EnsureResponsible(actor); IsReleaseBlocker = isBlocker;
        if (!isBlocker) { WaiverRationale = ""; WaivedBy = ""; WaivedAt = null; }
        else if (!string.IsNullOrWhiteSpace(waiverRationale)) { WaiverRationale = waiverRationale.Trim(); WaivedBy = actor; WaivedAt = now; }
        Touch(now);
    }

    public void Reopen(string actor, string rationale, DateTimeOffset now)
    {
        EnsureResponsible(actor);
        if (string.IsNullOrWhiteSpace(rationale)) throw new DomainException("A reopen rationale is required.");
        if (State == ProblemReportState.Closed || IsTerminalDisposition())
        {
            Revision++; State = ProblemReportState.Open; Disposition = null; DispositionRationale = ""; ResolutionVerificationExecutionId = null;
            ClosureApprovedBy = null; ClosureApprovedByName = ""; ClosureApprovedAt = null; Touch(now); return;
        }
        throw new DomainException("Only a closed or dispositioned problem report can be reopened.");
    }

    public void ResumeDeferred(string actor, DateTimeOffset now)
    {
        EnsureResponsible(actor); if (State != ProblemReportState.Deferred) throw new DomainException("Only a Deferred problem report can be resumed.");
        Disposition = null; DispositionRationale = ""; State = ProblemReportState.Open; Touch(now);
    }

    public string CanonicalSnapshot() => string.Join("|", Id, ProjectId, DisplayNumber, Title, Problem, ProblemRich, AdditionalInformation,
        AdditionalInformationRich, Analysis, ReportedBy, ResponsibleEngineerId, TargetReleaseId, Classification, Severity, Priority, Origin,
        AffectedConfiguration, RootCause, Effects, Containment, CorrectiveAction, SystemAircraftImpact, ImpactAssessmentJson, Disposition,
        DispositionRationale, ResolutionVerificationExecutionId, State, IsReleaseBlocker, WaiverRationale, Version);
    public string CanonicalHash() => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(CanonicalSnapshot()))).ToLowerInvariant();
    private void Touch(DateTimeOffset now) { UpdatedAt = now; Version++; }
    private void EnsureResponsible(string actor) { if (!string.Equals(actor, ResponsibleEngineerId, StringComparison.OrdinalIgnoreCase)) throw new DomainException("Only the responsible engineer can perform this action."); }
    /// <summary>
    /// Editable unless the report is finished. A report is corrected while the work it describes is in
    /// flight, so waiting on SQA closure or sitting deferred is no reason to refuse a correction — only
    /// closure and the terminal dispositions are, and reopening is the route back from those.
    /// </summary>
    private void EnsureEditable() { if (State == ProblemReportState.Closed || IsTerminalDisposition()) throw new DomainException("The problem report is closed or dispositioned and is no longer editable. Reopen it first."); }
    private void EnsureNotTerminal() { if (State == ProblemReportState.Closed || IsTerminalDisposition()) throw new DomainException("The problem report is closed or dispositioned. Reopen it before changing lifecycle data."); }
    private bool IsTerminalDisposition() => State is ProblemReportState.Duplicate or ProblemReportState.CannotReproduce or ProblemReportState.NoFaultFound or ProblemReportState.AcceptedRisk or ProblemReportState.Rejected;
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
