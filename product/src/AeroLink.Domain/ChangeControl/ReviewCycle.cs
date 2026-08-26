using AeroLink.Domain.Common;
using AeroLink.Domain.Identity;

namespace AeroLink.Domain.ChangeControl;

public enum ReviewCycleState { Active, ChangesRequested, Cancelled, Approved }
public enum ApprovalStepState { Pending, Active, Approved, Returned }
public enum ReviewMode { Sequential, Parallel }

public sealed class ApprovalStep
{
    private ApprovalStep() { }
    internal ApprovalStep(Guid reviewCycleId, int position, string approverId, string approverName, bool active,
        string stageName = "", ReviewStageKind stageKind = ReviewStageKind.Review)
    {
        Id = Guid.NewGuid();
        ReviewCycleId = reviewCycleId;
        Position = position;
        ApproverId = approverId;
        ApproverName = approverName;
        // The stage this signature answers, when the review follows a recorded procedure. An approval that
        // records only a name and a position cannot later be read as "the verification lead signed".
        StageName = stageName.Trim();
        StageKind = stageKind;
        Authority = "Reviewer";
        State = active ? ApprovalStepState.Active : ApprovalStepState.Pending;
    }

    public Guid Id { get; private set; }
    public Guid ReviewCycleId { get; private set; }
    public int Position { get; private set; }
    public string ApproverId { get; private set; } = string.Empty;
    public string ApproverName { get; private set; } = string.Empty;
    public string StageName { get; private set; } = string.Empty;
    /// <summary>
    /// Whether this signature examined the content or authorised the release. Frozen on the step, like the
    /// authority beside it, so the record stays readable after the procedure behind it is revised.
    /// </summary>
    public ReviewStageKind StageKind { get; private set; }
    public string Authority { get; private set; } = string.Empty;
    /// <summary>
    /// Why this reviewer decided as they did. Approval rationale is the reviewer's own reasoning about the
    /// exact content they examined; it is distinct from the engineering rationale carried by the artifact
    /// and from the electronic-signature meaning. Legacy records have no recorded reasoning and remain "".
    /// </summary>
    public string Rationale { get; private set; } = string.Empty;
    public ApprovalStepState State { get; private set; }
    public DateTimeOffset? DecidedAt { get; private set; }

    internal void Approve(DateTimeOffset now, string? rationale = null) { State = ApprovalStepState.Approved; DecidedAt = now; if (!string.IsNullOrWhiteSpace(rationale)) Rationale = rationale.Trim(); }
    internal void Return(string rationale, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(rationale)) throw new DomainException("A return reason is required.");
        State = ApprovalStepState.Returned; Rationale = rationale.Trim(); DecidedAt = now;
    }
    internal void Activate() => State = ApprovalStepState.Active;
    internal void Replace(string id, string name, ProgramRole? role)
    {
        ApproverId = id;
        ApproverName = name;
        Authority = role?.ToString() ?? "Reviewer";
    }
}

public sealed class ReviewCycle
{
    private readonly List<ApprovalStep> _steps = [];
    private readonly List<ReviewComment> _comments = [];
    private ReviewCycle() { }

    /// <summary>A review of a change request.</summary>
    internal ReviewCycle(Guid changeRequestId, int sequence, string snapshotHash, IReadOnlyList<ApproverSelection> approvers,
        DateTimeOffset now, ReviewMode mode = ReviewMode.Sequential, ReviewWorkflowSpecification? workflow = null,
        int snapshotContractVersion = 0, string snapshotJson = "")
        : this(changeRequestId, null, sequence, snapshotHash, approvers, now, mode, workflow,
            snapshotContractVersion, snapshotJson) { }

    /// <summary>A review of a test change request. Same mechanism, different subject.</summary>
    internal static ReviewCycle ForTestChangeRequest(Guid testChangeReviewId, int sequence, string snapshotHash,
        IReadOnlyList<ApproverSelection> approvers, DateTimeOffset now,
        ReviewMode mode = ReviewMode.Sequential, ReviewWorkflowSpecification? workflow = null,
        int snapshotContractVersion = 0, string snapshotJson = "") =>
        new(null, testChangeReviewId, sequence, snapshotHash, approvers, now, mode, workflow,
            snapshotContractVersion, snapshotJson);

    private ReviewCycle(Guid? changeRequestId, Guid? testChangeReviewId, int sequence, string snapshotHash,
        IReadOnlyList<ApproverSelection> approvers, DateTimeOffset now, ReviewMode mode,
        ReviewWorkflowSpecification? workflow, int snapshotContractVersion, string snapshotJson)
    {
        // Exactly one owner. Both would make "what is this a review of" ambiguous; neither would make it
        // unanswerable, and the cycle would outlive anything that could explain it.
        if (changeRequestId is null == testChangeReviewId is null)
            throw new DomainException("A review cycle belongs to exactly one package.");
        TestChangeReviewId = testChangeReviewId;
        if (approvers.Count == 0) throw new DomainException("At least one approver is required.");
        if (approvers.Select(x => x.UserId).Distinct(StringComparer.OrdinalIgnoreCase).Count() != approvers.Count)
            throw new DomainException("An approver cannot appear twice in one sequence.");
        // When the project has recorded a procedure, the review must satisfy it. When it has not, this is
        // skipped entirely and approver choice stays free, so introducing workflows blocks nobody.
        workflow?.Validate(approvers);

        Id = Guid.NewGuid();
        ChangeRequestId = changeRequestId;
        Sequence = sequence;
        SnapshotHash = snapshotHash;
        SnapshotContractVersion = snapshotContractVersion;
        SnapshotJson = snapshotJson ?? string.Empty;
        // The procedure's own mode wins when there is one. A team that recorded a sequential board does not
        // want an author choosing parallel at submission.
        Mode = workflow?.Mode ?? mode;
        // Which procedure, at which version. Recorded on the cycle so the review stays explainable after the
        // procedure is revised.
        WorkflowId = workflow?.WorkflowId;
        WorkflowLogicalId = workflow?.LogicalId;
        WorkflowName = workflow?.Name ?? "";
        WorkflowVersion = workflow?.Version;
        State = ReviewCycleState.Active;
        StartedAt = now;
        for (var index = 0; index < approvers.Count; index++)
        {
            var configuredStage = workflow?.Stages.ElementAtOrDefault(index);
            var step = new ApprovalStep(Id, index, approvers[index].UserId, approvers[index].Name,
                Mode == ReviewMode.Parallel || index == 0,
                workflow is null ? "" : configuredStage?.Name ?? $"Additional reviewer {index - workflow.Stages.Count + 1}",
                workflow is null ? ReviewStageKind.Review : configuredStage?.Kind ?? ReviewStageKind.Review);
            step.Replace(approvers[index].UserId, approvers[index].Name, approvers[index].Role);
            _steps.Add(step);
        }
    }

    public Guid Id { get; private set; }
    /// <summary>
    /// The change request this review is of, when it is of one.
    ///
    /// A review cycle belongs to exactly one package, but there are now two kinds it can belong to. Rather
    /// than one loose owner column that could point anywhere, each kind keeps its own foreign key and the
    /// database enforces that exactly one is set — so an orphaned cycle stays impossible, which a single
    /// untyped identifier could not promise.
    /// </summary>
    public Guid? ChangeRequestId { get; private set; }
    /// <summary>The test change request this review is of, when it is of one. Never set together with the above.</summary>
    public Guid? TestChangeReviewId { get; private set; }
    /// <summary>Whichever package this review belongs to, for the code that does not care which kind it is.</summary>
    public Guid OwnerId => ChangeRequestId ?? TestChangeReviewId!.Value;
    public int Sequence { get; private set; }
    public string SnapshotHash { get; private set; } = string.Empty;
    public int SnapshotContractVersion { get; private set; }
    public string SnapshotJson { get; private set; } = string.Empty;
    public ReviewMode Mode { get; private set; }
    public Guid? WorkflowId { get; private set; }
    public Guid? WorkflowLogicalId { get; private set; }
    public string WorkflowName { get; private set; } = string.Empty;
    public int? WorkflowVersion { get; private set; }
    public ReviewCycleState State { get; private set; }
    public DateTimeOffset StartedAt { get; private set; }
    public DateTimeOffset? CompletedAt { get; private set; }
    public string? ClosureReason { get; private set; }
    public IReadOnlyCollection<ApprovalStep> Steps => _steps.AsReadOnly();
    public IReadOnlyCollection<ReviewComment> Comments => _comments.AsReadOnly();
    public int ActivePosition => _steps.Where(x => x.State == ApprovalStepState.Active).OrderBy(x => x.Position).FirstOrDefault()?.Position ?? -1;

    /// <summary>
    /// Rebinds the stored canonical snapshot hash after the forward-only verification identity migration.
    /// The review steps, decisions, workflow version and comments remain immutable history; only the hash of
    /// the same package's structured identity is refreshed by the governed migration authority.
    /// </summary>
    public void RecordVerificationIdentityMigration(string snapshotHash)
    {
        if (string.IsNullOrWhiteSpace(snapshotHash) || snapshotHash.Length != 64
            || snapshotHash.Any(character => !Uri.IsHexDigit(character)))
            throw new DomainException("A review-cycle migration requires a valid SHA-256 snapshot hash.");
        SnapshotHash = snapshotHash.Trim().ToLowerInvariant();
    }

    /// <summary>
    /// Adds a reviewer's remark about one part of the package, as a draft only its author can see.
    ///
    /// Anyone holding a step in this cycle may comment, not only whoever is active. In a sequential review a
    /// later reviewer reads ahead, and refusing them a place to write it down only means the observation is
    /// lost or arrives by some other route.
    /// </summary>
    internal ReviewComment AddComment(string authorId, ReviewCommentAnchor anchor, Guid? requirementChangeId,
        string body, DateTimeOffset now)
    {
        EnsureActive();
        if (!_steps.Any(x => string.Equals(x.ApproverId, authorId, StringComparison.OrdinalIgnoreCase)))
            throw new DomainException("Only a reviewer on this cycle can comment on it.");
        var comment = new ReviewComment(Id, authorId, anchor, requirementChangeId, body, now);
        _comments.Add(comment);
        return comment;
    }

    internal void ReviseComment(Guid commentId, string actorId, string body, DateTimeOffset now) =>
        OwnDraft(commentId, actorId).Revise(body, now);

    internal void RemoveComment(Guid commentId, string actorId) => _comments.Remove(OwnDraft(commentId, actorId));

    private ReviewComment OwnDraft(Guid commentId, string actorId)
    {
        var comment = _comments.SingleOrDefault(x => x.Id == commentId)
            ?? throw new DomainException("That comment is not on this review cycle.");
        if (!string.Equals(comment.AuthorId, actorId, StringComparison.OrdinalIgnoreCase))
            throw new DomainException("Only the author of a comment can change it.");
        if (comment.State != ReviewCommentState.Draft)
            throw new DomainException("A published comment cannot be changed.");
        return comment;
    }

    /// <summary>
    /// The comments on this cycle that one person is entitled to read.
    ///
    /// Three audiences, not two. Your own drafts are yours. A published comment is readable by anyone who
    /// can read the package — except a reviewer on this same open cycle who has not yet decided, who sees
    /// only their own until they do.
    ///
    /// That exception is the point of the method. Without it, one reviewer signing would put their
    /// objections in front of everyone still deciding, and an independent assurance signature made after
    /// reading peer engineering's reservations is a weaker thing than one made without them. The author is
    /// deliberately not held to that: they get every comment the moment it publishes.
    /// </summary>
    public IReadOnlyList<ReviewComment> CommentsVisibleTo(string viewer)
    {
        bool Mine(ReviewComment comment) => string.Equals(comment.AuthorId, viewer, StringComparison.OrdinalIgnoreCase);

        var step = _steps.SingleOrDefault(x => string.Equals(x.ApproverId, viewer, StringComparison.OrdinalIgnoreCase));
        var stillDeciding = State == ReviewCycleState.Active && step is not null
            && step.State is ApprovalStepState.Pending or ApprovalStepState.Active;
        if (stillDeciding) return _comments.Where(Mine).ToList();

        return _comments.Where(x => x.State == ReviewCommentState.Published || Mine(x)).ToList();
    }

    /// <summary>
    /// Publishes the drafts one reviewer wrote. Called by the act of that reviewer deciding, which is the
    /// only thing that makes their remarks the author's to read.
    /// </summary>
    private void PublishOwn(string actorId, DateTimeOffset now)
    {
        foreach (var comment in _comments.Where(x => x.State == ReviewCommentState.Draft
                     && string.Equals(x.AuthorId, actorId, StringComparison.OrdinalIgnoreCase)))
            comment.Publish(decisionRecorded: true, now);
    }

    /// <summary>
    /// Publishes whatever is still outstanding when the cycle ends under everyone. Discarding it instead
    /// would lose a reviewer's written analysis because a colleague decided first, and send the author back
    /// to revise the package without it. Each one is marked as carrying no decision.
    /// </summary>
    private void PublishOrphans(DateTimeOffset now)
    {
        foreach (var comment in _comments.Where(x => x.State == ReviewCommentState.Draft))
            comment.Publish(decisionRecorded: false, now);
    }

    internal bool Approve(string actorId, string? rationale, DateTimeOffset now)
    {
        EnsureActive();
        var active = _steps.SingleOrDefault(x => x.State == ApprovalStepState.Active && string.Equals(x.ApproverId, actorId, StringComparison.OrdinalIgnoreCase));
        if (active is null)
            throw new DomainException("Only the active approver can approve this review stage.");
        var position = active.Position;
        active.Approve(now, rationale);
        // Approving publishes this reviewer's remarks and nothing else. The cycle carries on, and the author
        // can read them straight away rather than waiting for everyone.
        PublishOwn(actorId, now);
        if (_steps.All(x => x.State == ApprovalStepState.Approved))
        {
            State = ReviewCycleState.Approved;
            CompletedAt = now;
            return true;
        }

        if (Mode == ReviewMode.Sequential) _steps.Single(x => x.Position == position + 1).Activate();
        return false;
    }

    /// <summary>
    /// The active reviewer returns the package to the author with their own reason. The step is recorded as
    /// Returned so history shows exactly which reviewer asked for what; the cycle closes as ChangesRequested
    /// and the author's next submission starts a new cycle, retaining this one and any earlier signatures.
    /// </summary>
    internal void ReturnActiveStep(string actorId, string rationale, DateTimeOffset now)
    {
        EnsureActive();
        var active = _steps.SingleOrDefault(x => x.State == ApprovalStepState.Active && string.Equals(x.ApproverId, actorId, StringComparison.OrdinalIgnoreCase));
        if (active is null)
            throw new DomainException("Only the active approver can return the review to the author.");
        active.Return(rationale, now);
        // This closes the cycle for everyone, so the returning reviewer's remarks publish with their
        // decision and everyone else's publish without one.
        PublishOwn(actorId, now);
        PublishOrphans(now);
        State = ReviewCycleState.ChangesRequested;
        ClosureReason = rationale.Trim();
        CompletedAt = now;
    }

    internal void ReplaceFutureApprover(int position, ApproverSelection replacement,
        ReviewWorkflowSpecification? workflow = null)
    {
        EnsureActive();
        if (Mode == ReviewMode.Parallel) throw new DomainException("Parallel review assignments are activated together; cancel and restart to change an approver.");
        if (position <= ActivePosition) throw new DomainException("Only not-yet-reached approvers can be replaced.");
        if (_steps.Any(x => x.Position != position && string.Equals(x.ApproverId, replacement.UserId, StringComparison.OrdinalIgnoreCase)))
            throw new DomainException("An approver cannot appear twice in one sequence.");
        // Swapping in somebody who does not hold the stage's authority would satisfy the procedure at
        // submission and quietly break it before anybody signed.
        var stage = workflow?.Stages.SingleOrDefault(x => x.Position == position);
        if (stage is not null) workflow!.ValidateStage(stage, replacement);
        _steps[position].Replace(replacement.UserId, replacement.Name, replacement.Role);
    }

    internal void Cancel(string reason, DateTimeOffset now)
    {
        EnsureActive();
        // A cancelled review still produced reading. The author is about to rework the package and every
        // observation written against it applies just as much as if the cycle had run its course.
        PublishOrphans(now);
        State = ReviewCycleState.Cancelled;
        ClosureReason = reason.Trim();
        CompletedAt = now;
    }

    private void EnsureActive()
    {
        if (State != ReviewCycleState.Active) throw new DomainException("The review cycle is not active.");
    }
}

/// <summary>
/// A chosen approver. The authority is resolved outside the domain, because program membership lives in a
/// different aggregate; it rides along so a recorded procedure can be enforced without reaching for it.
/// </summary>
public sealed record ApproverSelection(string UserId, string Name, ProgramRole? Role = null);
