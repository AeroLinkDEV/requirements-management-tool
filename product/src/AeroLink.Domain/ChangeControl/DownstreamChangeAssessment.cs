using AeroLink.Domain.Common;

namespace AeroLink.Domain.ChangeControl;

public enum DownstreamAssessmentState { Open, InReview, Approved, Superseded }
public enum DownstreamAssessmentOutcome { Pending, ChangeRequired, NoChangeRequired, ChangeRequestsLinked }

/// <summary>
/// The consuming discipline's controlled answer to an approved upstream change.
///
/// A System change is assessed by HLR engineering; an HLR change is assessed by LLR engineering. The
/// assessment deliberately exists before a downstream SWCR: "no change required" is a valid answer, and
/// several upstream changes may be handled by one SWCR without allocating empty controlled records.
/// </summary>
public sealed class DownstreamChangeAssessment
{
    private readonly List<DownstreamAssessmentChangeRequestLink> _changeRequestLinks = [];
    private DownstreamChangeAssessment() { }

    public DownstreamChangeAssessment(Guid projectId, Guid releaseId, Guid sourceChangeRequestId,
        string sourceChangeRequestNumber, RequirementLevel targetLevel, DateTimeOffset now)
    {
        if (projectId == Guid.Empty) throw new DomainException("A downstream assessment requires its Project.");
        if (releaseId == Guid.Empty) throw new DomainException("A downstream assessment requires its software build.");
        if (sourceChangeRequestId == Guid.Empty) throw new DomainException("A downstream assessment requires its source change request.");
        if (targetLevel is not (RequirementLevel.HighLevel or RequirementLevel.LowLevel))
            throw new DomainException("A downstream assessment must target HLR or LLR engineering.");
        Id = Guid.NewGuid();
        ProjectId = projectId;
        ReleaseId = releaseId;
        SourceChangeRequestId = sourceChangeRequestId;
        SourceChangeRequestNumber = Required(sourceChangeRequestNumber, "source change request number");
        TargetLevel = targetLevel;
        State = DownstreamAssessmentState.Open;
        Outcome = DownstreamAssessmentOutcome.Pending;
        CreatedAt = UpdatedAt = now;
    }

    public Guid Id { get; private set; }
    public Guid ProjectId { get; private set; }
    public Guid ReleaseId { get; private set; }
    public Guid SourceChangeRequestId { get; private set; }
    public string SourceChangeRequestNumber { get; private set; } = "";
    public RequirementLevel TargetLevel { get; private set; }
    public DownstreamAssessmentState State { get; private set; }
    public DownstreamAssessmentOutcome Outcome { get; private set; }
    public string? AssignedEngineerId { get; private set; }
    public string Rationale { get; private set; } = "";
    public string? SubmittedBy { get; private set; }
    public string? SelectedApproverId { get; private set; }
    public DateTimeOffset? SubmittedAt { get; private set; }
    public string? ApprovedBy { get; private set; }
    public DateTimeOffset? ApprovedAt { get; private set; }
    public Guid? SupersededByAssessmentId { get; private set; }
    public string SupersededReason { get; private set; } = "";
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public long Version { get; private set; } = 1;
    public IReadOnlyCollection<DownstreamAssessmentChangeRequestLink> ChangeRequestLinks => _changeRequestLinks.AsReadOnly();

    public void Assign(string actorId, string engineerId, DateTimeOffset now)
    {
        EnsureOpen();
        Required(actorId, "assigning actor");
        AssignedEngineerId = Required(engineerId, "assigned engineer");
        Touch(now);
    }

    public void RecordNoChange(string actorId, string rationale, DateTimeOffset now)
    {
        EnsureOpen();
        EnsureAssignee(actorId);
        if (_changeRequestLinks.Count != 0)
            throw new DomainException("Remove linked downstream change requests before recording no downstream change.");
        Rationale = Required(rationale, "no-change rationale");
        Outcome = DownstreamAssessmentOutcome.NoChangeRequired;
        Touch(now);
    }

    public void RecordChangeRequired(string actorId, DateTimeOffset now)
    {
        EnsureOpen();
        EnsureAssignee(actorId);
        if (_changeRequestLinks.Count != 0)
            throw new DomainException("The downstream change is already controlled by a linked SWCR.");
        Outcome = DownstreamAssessmentOutcome.ChangeRequired;
        Rationale = "";
        Touch(now);
    }

    public void LinkChangeRequest(string actorId, Guid changeRequestId, string displayNumber, DateTimeOffset now)
    {
        EnsureOpen();
        EnsureAssignee(actorId);
        if (changeRequestId == Guid.Empty) throw new DomainException("A downstream change request is required.");
        if (_changeRequestLinks.Any(x => x.ChangeRequestId == changeRequestId)) return;
        _changeRequestLinks.Add(new(Id, changeRequestId, Required(displayNumber, "downstream change request number"), actorId, now));
        Outcome = DownstreamAssessmentOutcome.ChangeRequestsLinked;
        Rationale = "";
        Touch(now);
    }

    public void Submit(string actorId, string approverId, DateTimeOffset now)
    {
        EnsureOpen();
        EnsureAssignee(actorId);
        if (Outcome is DownstreamAssessmentOutcome.Pending or DownstreamAssessmentOutcome.ChangeRequired)
            throw new DomainException("Record the downstream conclusion and link required SWCR work before submitting the assessment.");
        var approver = Required(approverId, "selected approver");
        if (string.Equals(approver, actorId, StringComparison.OrdinalIgnoreCase))
            throw new DomainException("The downstream assessment approver must be independent from its submitting engineer.");
        SubmittedBy = actorId;
        SelectedApproverId = approver;
        SubmittedAt = now;
        State = DownstreamAssessmentState.InReview;
        Touch(now);
    }

    public void Approve(string actorId, DateTimeOffset now)
    {
        if (State != DownstreamAssessmentState.InReview)
            throw new DomainException("Only an in-review downstream assessment can be approved.");
        if (!string.Equals(actorId, SelectedApproverId, StringComparison.OrdinalIgnoreCase))
            throw new DomainException("Only the explicitly selected downstream assessment approver can approve it.");
        ApprovedBy = actorId;
        ApprovedAt = now;
        State = DownstreamAssessmentState.Approved;
        Touch(now);
    }

    public void ReturnToWork(string actorId, string rationale, DateTimeOffset now)
    {
        if (State != DownstreamAssessmentState.InReview)
            throw new DomainException("Only an in-review downstream assessment can be returned.");
        if (!string.Equals(actorId, SelectedApproverId, StringComparison.OrdinalIgnoreCase))
            throw new DomainException("Only the explicitly selected downstream assessment approver can return it.");
        Rationale = Required(rationale, "return rationale");
        SubmittedBy = null;
        SelectedApproverId = null;
        SubmittedAt = null;
        State = DownstreamAssessmentState.Open;
        Touch(now);
    }

    public void Supersede(Guid successorAssessmentId, string reason, DateTimeOffset now)
    {
        if (successorAssessmentId == Guid.Empty || successorAssessmentId == Id)
            throw new DomainException("A different successor assessment is required.");
        SupersededByAssessmentId = successorAssessmentId;
        SupersededReason = Required(reason, "supersession reason");
        State = DownstreamAssessmentState.Superseded;
        Touch(now);
    }

    private void EnsureOpen()
    {
        if (State != DownstreamAssessmentState.Open)
            throw new DomainException("Only an open downstream assessment can be changed.");
    }

    private void EnsureAssignee(string actorId)
    {
        var actor = Required(actorId, "engineer");
        if (!string.Equals(actor, AssignedEngineerId, StringComparison.OrdinalIgnoreCase))
            throw new DomainException("Only the assigned downstream engineer can record this assessment.");
    }

    private void Touch(DateTimeOffset now) { UpdatedAt = now; Version++; }
    private static string Required(string value, string name) =>
        string.IsNullOrWhiteSpace(value) ? throw new DomainException($"A {name} is required.") : value.Trim();
}

public sealed class DownstreamAssessmentChangeRequestLink
{
    private DownstreamAssessmentChangeRequestLink() { }
    public DownstreamAssessmentChangeRequestLink(Guid assessmentId, Guid changeRequestId,
        string changeRequestNumber, string linkedBy, DateTimeOffset now)
    {
        Id = Guid.NewGuid();
        AssessmentId = assessmentId;
        ChangeRequestId = changeRequestId;
        ChangeRequestNumber = changeRequestNumber;
        LinkedBy = linkedBy;
        LinkedAt = now;
    }

    public Guid Id { get; private set; }
    public Guid AssessmentId { get; private set; }
    public Guid ChangeRequestId { get; private set; }
    public string ChangeRequestNumber { get; private set; } = "";
    public string LinkedBy { get; private set; } = "";
    public DateTimeOffset LinkedAt { get; private set; }
}
