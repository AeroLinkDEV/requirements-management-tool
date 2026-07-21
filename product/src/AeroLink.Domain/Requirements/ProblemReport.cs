using System.Security.Cryptography;
using System.Text;
using AeroLink.Domain.Common;

namespace AeroLink.Domain.Requirements;

public enum ProblemReportState
{
    Draft, Open, Investigating, ResolutionProposed, AwaitingClosureApproval, Closed,
    Duplicate, CannotReproduce, NoFaultFound, Deferred, AcceptedRisk, Rejected
}

public enum ProblemReportSeverity { Critical, High, Major, Minor, Trivial }
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
        string origin = "Test execution", string affectedConfiguration = "")
    {
        if (projectId == Guid.Empty) throw new DomainException("A problem-report project is required.");
        Id = Guid.NewGuid(); ProjectId = projectId; ReportNumber = Required(reportNumber, "A problem-report number is required.");
        Title = Required(title, "A problem-report title is required."); Problem = Required(problem, "A problem statement is required.");
        Analysis = analysis?.Trim() ?? ""; ReportedBy = Required(reportedBy, "A problem-report owner is required.");
        Classification = Required(classification, "A problem-report classification is required."); Severity = severity; Priority = priority;
        Origin = Required(origin, "A problem-report origin is required."); AffectedConfiguration = affectedConfiguration?.Trim() ?? "";
        State = ProblemReportState.Open; CreatedAt = UpdatedAt = now; Version = 1;
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

    public void BeginInvestigation(string actor, string analysis, string rootCause, string effects, string containment, DateTimeOffset now)
    {
        EnsureReporter(actor); EnsureNotTerminal();
        Analysis = Required(analysis, "Investigation analysis is required."); RootCause = rootCause?.Trim() ?? ""; Effects = effects?.Trim() ?? ""; Containment = containment?.Trim() ?? "";
        State = ProblemReportState.Investigating; Touch(now);
    }

    public void ProposeResolution(string actor, string correctiveAction, DateTimeOffset now)
    {
        EnsureReporter(actor); if (State is not (ProblemReportState.Open or ProblemReportState.Investigating)) throw new DomainException("Only an open or investigating problem report can propose a resolution.");
        CorrectiveAction = Required(correctiveAction, "A corrective action is required."); Disposition = ProblemReportDisposition.Fixed; State = ProblemReportState.ResolutionProposed; Touch(now);
    }

    public void RecordResolutionVerification(string actor, Guid executionId, DateTimeOffset now)
    {
        EnsureReporter(actor); if (State != ProblemReportState.ResolutionProposed) throw new DomainException("Only a proposed resolution can be verified.");
        if (executionId == Guid.Empty) throw new DomainException("A successor test execution is required for resolution verification.");
        ResolutionVerificationExecutionId = executionId; State = ProblemReportState.AwaitingClosureApproval; Touch(now);
    }

    public void ApproveClosure(string actor, Guid actorAccountId, DateTimeOffset now)
    {
        if (string.Equals(actor, ReportedBy, StringComparison.OrdinalIgnoreCase)) throw new DomainException("The problem-report owner cannot independently approve closure.");
        if (State != ProblemReportState.AwaitingClosureApproval) throw new DomainException("A verified resolution must be awaiting closure approval.");
        ClosureApprovedBy = actorAccountId == Guid.Empty ? null : actorAccountId; ClosureApprovedByName = Required(actor, "A closure approver is required."); ClosureApprovedAt = now;
        State = ProblemReportState.Closed; Touch(now);
    }

    public void ApplyDisposition(string actor, ProblemReportDisposition disposition, string rationale, Guid? duplicateOfId, DateTimeOffset now)
    {
        EnsureReporter(actor); EnsureNotTerminal(); DispositionRationale = Required(rationale, "A disposition rationale is required."); Disposition = disposition;
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
        EnsureReporter(actor); IsReleaseBlocker = isBlocker;
        if (!isBlocker) { WaiverRationale = ""; WaivedBy = ""; WaivedAt = null; }
        else if (!string.IsNullOrWhiteSpace(waiverRationale)) { WaiverRationale = waiverRationale.Trim(); WaivedBy = actor; WaivedAt = now; }
        Touch(now);
    }

    public void Reopen(string actor, string rationale, DateTimeOffset now)
    {
        EnsureReporter(actor);
        if (string.IsNullOrWhiteSpace(rationale)) throw new DomainException("A reopen rationale is required.");
        if (State == ProblemReportState.Closed || IsTerminalDisposition())
        {
            Revision++; State = ProblemReportState.Open; Disposition = null; DispositionRationale = ""; ResolutionVerificationExecutionId = null;
            ClosureApprovedBy = null; ClosureApprovedByName = ""; ClosureApprovedAt = null; Touch(now); return;
        }
        throw new DomainException("Only a closed or dispositioned problem report can be reopened.");
    }

    public string CanonicalSnapshot() => string.Join("|", Id, ProjectId, DisplayNumber, Title, Problem, Analysis, ReportedBy, Classification, Severity, Priority, Origin,
        AffectedConfiguration, RootCause, Effects, Containment, CorrectiveAction, Disposition, DispositionRationale, ResolutionVerificationExecutionId, State, IsReleaseBlocker, WaiverRationale, Version);
    public string CanonicalHash() => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(CanonicalSnapshot()))).ToLowerInvariant();
    private void Touch(DateTimeOffset now) { UpdatedAt = now; Version++; }
    private void EnsureReporter(string actor) { if (!string.Equals(actor, ReportedBy, StringComparison.OrdinalIgnoreCase)) throw new DomainException("Only the problem-report owner can perform this action."); }
    private void EnsureEditable() { if (State is not (ProblemReportState.Draft or ProblemReportState.Open or ProblemReportState.Investigating or ProblemReportState.ResolutionProposed)) throw new DomainException("The problem report is no longer editable."); }
    private void EnsureNotTerminal() { if (State == ProblemReportState.Closed || IsTerminalDisposition()) throw new DomainException("The problem report is closed or dispositioned. Reopen it before changing lifecycle data."); }
    private bool IsTerminalDisposition() => State is ProblemReportState.Duplicate or ProblemReportState.CannotReproduce or ProblemReportState.NoFaultFound or ProblemReportState.Deferred or ProblemReportState.AcceptedRisk or ProblemReportState.Rejected;
    private static string Required(string? value, string error) => string.IsNullOrWhiteSpace(value) ? throw new DomainException(error) : value.Trim();
}
