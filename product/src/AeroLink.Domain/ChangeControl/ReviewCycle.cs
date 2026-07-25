using AeroLink.Domain.Common;
using AeroLink.Domain.Identity;

namespace AeroLink.Domain.ChangeControl;

public enum ReviewCycleState { Active, ChangesRequested, Cancelled, Approved }
public enum ApprovalStepState { Pending, Active, Approved }
public enum ReviewMode { Sequential, Parallel }

public sealed class ApprovalStep
{
    private ApprovalStep() { }
    internal ApprovalStep(Guid reviewCycleId, int position, string approverId, string approverName, bool active,
        string stageName = "")
    {
        Id = Guid.NewGuid();
        ReviewCycleId = reviewCycleId;
        Position = position;
        ApproverId = approverId;
        ApproverName = approverName;
        // The stage this signature answers, when the review follows a recorded procedure. An approval that
        // records only a name and a position cannot later be read as "the verification lead signed".
        StageName = stageName.Trim();
        State = active ? ApprovalStepState.Active : ApprovalStepState.Pending;
    }

    public Guid Id { get; private set; }
    public Guid ReviewCycleId { get; private set; }
    public int Position { get; private set; }
    public string ApproverId { get; private set; } = string.Empty;
    public string ApproverName { get; private set; } = string.Empty;
    public string StageName { get; private set; } = string.Empty;
    public ApprovalStepState State { get; private set; }
    public DateTimeOffset? DecidedAt { get; private set; }

    internal void Approve(DateTimeOffset now) { State = ApprovalStepState.Approved; DecidedAt = now; }
    internal void Activate() => State = ApprovalStepState.Active;
    internal void Replace(string id, string name) { ApproverId = id; ApproverName = name; }
}

public sealed class ReviewCycle
{
    private readonly List<ApprovalStep> _steps = [];
    private ReviewCycle() { }

    internal ReviewCycle(Guid scrId, int sequence, string snapshotHash, IReadOnlyList<ApproverSelection> approvers,
        DateTimeOffset now, ReviewMode mode = ReviewMode.Sequential, ReviewWorkflowSpecification? workflow = null)
    {
        if (approvers.Count == 0) throw new DomainException("At least one approver is required.");
        if (approvers.Select(x => x.UserId).Distinct(StringComparer.OrdinalIgnoreCase).Count() != approvers.Count)
            throw new DomainException("An approver cannot appear twice in one sequence.");
        // When the project has recorded a procedure, the review must satisfy it. When it has not, this is
        // skipped entirely and approver choice stays free, so introducing workflows blocks nobody.
        workflow?.Validate(approvers);

        Id = Guid.NewGuid();
        ScrId = scrId;
        Sequence = sequence;
        SnapshotHash = snapshotHash;
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
            _steps.Add(new ApprovalStep(Id, index, approvers[index].UserId, approvers[index].Name,
                Mode == ReviewMode.Parallel || index == 0,
                workflow is null ? "" : workflow.Stages[index].Name));
    }

    public Guid Id { get; private set; }
    public Guid ScrId { get; private set; }
    public int Sequence { get; private set; }
    public string SnapshotHash { get; private set; } = string.Empty;
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
    public int ActivePosition => _steps.Where(x => x.State == ApprovalStepState.Active).OrderBy(x => x.Position).FirstOrDefault()?.Position ?? -1;

    internal bool Approve(string actorId, DateTimeOffset now)
    {
        EnsureActive();
        var active = _steps.SingleOrDefault(x => x.State == ApprovalStepState.Active && string.Equals(x.ApproverId, actorId, StringComparison.OrdinalIgnoreCase));
        if (active is null)
            throw new DomainException("Only the active approver can approve this review stage.");
        var position = active.Position;
        active.Approve(now);
        if (_steps.All(x => x.State == ApprovalStepState.Approved))
        {
            State = ReviewCycleState.Approved;
            CompletedAt = now;
            return true;
        }

        if (Mode == ReviewMode.Sequential) _steps.Single(x => x.Position == position + 1).Activate();
        return false;
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
        _steps[position].Replace(replacement.UserId, replacement.Name);
    }

    internal void RequestChanges(string reason, DateTimeOffset now)
    {
        EnsureActive();
        State = ReviewCycleState.ChangesRequested;
        ClosureReason = reason.Trim();
        CompletedAt = now;
    }

    internal void Cancel(string reason, DateTimeOffset now)
    {
        EnsureActive();
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
