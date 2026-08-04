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
    /// <summary>
    /// Who recorded the conclusion the assessment currently carries, and when. Held separately from the
    /// approval fields because a conclusion is an engineering answer that exists long before anybody reviews
    /// it — a reader looking at a concluded assessment needs to know whose answer it is without having to
    /// infer it from the assignment.
    /// </summary>
    public string? DecidedBy { get; private set; }
    public DateTimeOffset? DecidedAt { get; private set; }
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
        Decide(actorId, now);
    }

    public void RecordChangeRequired(string actorId, DateTimeOffset now)
    {
        EnsureOpen();
        EnsureAssignee(actorId);
        if (_changeRequestLinks.Count != 0)
            throw new DomainException("The downstream change is already controlled by a linked SWCR.");
        if (Outcome == DownstreamAssessmentOutcome.NoChangeRequired)
            Rationale = "";
        Outcome = DownstreamAssessmentOutcome.ChangeRequired;
        Decide(actorId, now);
    }

    public void LinkChangeRequest(string actorId, Guid changeRequestId, string displayNumber, DateTimeOffset now)
    {
        EnsureOpen();
        EnsureAssignee(actorId);
        if (changeRequestId == Guid.Empty) throw new DomainException("A downstream change request is required.");
        if (_changeRequestLinks.Any(x => x.ChangeRequestId == changeRequestId)) return;
        _changeRequestLinks.Add(new(Id, changeRequestId, Required(displayNumber, "downstream change request number"), actorId, now));
        Outcome = DownstreamAssessmentOutcome.ChangeRequestsLinked;
        Decide(actorId, now);
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

    /// <summary>
    /// Withdraws the recorded conclusion and hands the assessment back to its engineer as undecided.
    ///
    /// An assessment can be answered wrongly, and until now the only way to say so was to press a conclusion
    /// button that looked exactly like a first-time answer — which left no trace that the question had ever
    /// been answered differently. Reopening is that statement made explicitly: it returns the assessment to
    /// the undecided state and hands back a record of precisely what was withdrawn, so the previous answer
    /// survives the correction instead of being overwritten by it.
    ///
    /// Links to Draft SWCRs are detached, because an undecided assessment cannot claim to be controlled by a
    /// change request. The SWCRs themselves are untouched, and their numbers are carried into the returned
    /// record so the detachment is visible rather than silent.
    /// </summary>
    public DownstreamAssessmentReopening Reopen(string actorId, string reason, DateTimeOffset now)
    {
        var actor = Required(actorId, "engineer reopening the assessment");
        Required(reason, "reopen reason");
        if (State == DownstreamAssessmentState.Superseded)
            throw new DomainException("A superseded downstream assessment cannot be reopened. Work the assessment that replaced it.");
        if (State == DownstreamAssessmentState.InReview)
            throw new DomainException("Return the assessment to its engineer instead of reopening it while it is in review.");
        if (State == DownstreamAssessmentState.Open && Outcome == DownstreamAssessmentOutcome.Pending)
            throw new DomainException("This downstream assessment has no recorded conclusion to withdraw.");
        // An unapproved conclusion belongs to the engineer who recorded it; an approved one has left their
        // hands, and the endpoint requires Approver authority to withdraw it.
        if (State == DownstreamAssessmentState.Open) EnsureAssignee(actor);
        var reopening = new DownstreamAssessmentReopening(Id, State, Outcome, Rationale, DecidedBy, DecidedAt,
            ApprovedBy, ApprovedAt, _changeRequestLinks.Select(x => x.ChangeRequestNumber), reason, actor, now);
        _changeRequestLinks.Clear();
        Outcome = DownstreamAssessmentOutcome.Pending;
        Rationale = "";
        DecidedBy = null;
        DecidedAt = null;
        SubmittedBy = null;
        SelectedApproverId = null;
        SubmittedAt = null;
        ApprovedBy = null;
        ApprovedAt = null;
        State = DownstreamAssessmentState.Open;
        Touch(now);
        return reopening;
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

    private void Decide(string actorId, DateTimeOffset now)
    {
        DecidedBy = actorId.Trim();
        DecidedAt = now;
        Touch(now);
    }

    private void Touch(DateTimeOffset now) { UpdatedAt = now; Version++; }
    private static string Required(string value, string name) =>
        string.IsNullOrWhiteSpace(value) ? throw new DomainException($"A {name} is required.") : value.Trim();
}

/// <summary>
/// An immutable record of a downstream conclusion that was withdrawn, and of everything it carried at the
/// moment it was withdrawn. Written only by <see cref="DownstreamChangeAssessment.Reopen"/>.
/// </summary>
public sealed class DownstreamAssessmentReopening
{
    private DownstreamAssessmentReopening() { }

    public DownstreamAssessmentReopening(Guid assessmentId, DownstreamAssessmentState previousState,
        DownstreamAssessmentOutcome previousOutcome, string previousRationale, string? previousDecidedBy,
        DateTimeOffset? previousDecidedAt, string? previousApprovedBy, DateTimeOffset? previousApprovedAt,
        IEnumerable<string> detachedChangeRequestNumbers, string reason, string actorId, DateTimeOffset occurredAt)
    {
        if (assessmentId == Guid.Empty) throw new DomainException("A reopening record requires its assessment.");
        Id = Guid.NewGuid();
        AssessmentId = assessmentId;
        PreviousState = previousState;
        PreviousOutcome = previousOutcome;
        PreviousRationale = previousRationale ?? "";
        PreviousDecidedBy = previousDecidedBy;
        PreviousDecidedAt = previousDecidedAt;
        PreviousApprovedBy = previousApprovedBy;
        PreviousApprovedAt = previousApprovedAt;
        DetachedChangeRequestNumbers = string.Join(", ", detachedChangeRequestNumbers.OrderBy(x => x));
        Reason = string.IsNullOrWhiteSpace(reason)
            ? throw new DomainException("A reopening record requires its reason.") : reason.Trim();
        ActorId = string.IsNullOrWhiteSpace(actorId)
            ? throw new DomainException("A reopening record requires its actor.") : actorId.Trim();
        OccurredAt = occurredAt;
    }

    public Guid Id { get; private set; }
    public Guid AssessmentId { get; private set; }
    public DownstreamAssessmentState PreviousState { get; private set; }
    public DownstreamAssessmentOutcome PreviousOutcome { get; private set; }
    public string PreviousRationale { get; private set; } = "";
    public string? PreviousDecidedBy { get; private set; }
    public DateTimeOffset? PreviousDecidedAt { get; private set; }
    public string? PreviousApprovedBy { get; private set; }
    public DateTimeOffset? PreviousApprovedAt { get; private set; }
    /// <summary>The Draft SWCR numbers this reopening detached, empty when there were none.</summary>
    public string DetachedChangeRequestNumbers { get; private set; } = "";
    public string Reason { get; private set; } = "";
    public string ActorId { get; private set; } = "";
    public DateTimeOffset OccurredAt { get; private set; }
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
