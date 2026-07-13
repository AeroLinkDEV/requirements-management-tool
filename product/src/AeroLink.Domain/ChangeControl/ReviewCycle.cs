using AeroLink.Domain.Common;

namespace AeroLink.Domain.ChangeControl;

public enum ReviewCycleState { Active, ChangesRequested, Cancelled, Approved }
public enum ApprovalStepState { Pending, Active, Approved }
public enum ReviewMode { Sequential, Parallel }

public sealed class ApprovalStep
{
    private ApprovalStep() { }
    internal ApprovalStep(Guid reviewCycleId, int position, string approverId, string approverName, bool active)
    {
        Id = Guid.NewGuid();
        ReviewCycleId = reviewCycleId;
        Position = position;
        ApproverId = approverId;
        ApproverName = approverName;
        State = active ? ApprovalStepState.Active : ApprovalStepState.Pending;
    }

    public Guid Id { get; private set; }
    public Guid ReviewCycleId { get; private set; }
    public int Position { get; private set; }
    public string ApproverId { get; private set; } = string.Empty;
    public string ApproverName { get; private set; } = string.Empty;
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

    internal ReviewCycle(Guid scrId, int sequence, string snapshotHash, IReadOnlyList<ApproverSelection> approvers, DateTimeOffset now, ReviewMode mode = ReviewMode.Sequential)
    {
        if (approvers.Count == 0) throw new DomainException("At least one approver is required.");
        if (approvers.Select(x => x.UserId).Distinct(StringComparer.OrdinalIgnoreCase).Count() != approvers.Count)
            throw new DomainException("An approver cannot appear twice in one sequence.");

        Id = Guid.NewGuid();
        ScrId = scrId;
        Sequence = sequence;
        SnapshotHash = snapshotHash;
        Mode = mode;
        State = ReviewCycleState.Active;
        StartedAt = now;
        for (var index = 0; index < approvers.Count; index++)
            _steps.Add(new ApprovalStep(Id, index, approvers[index].UserId, approvers[index].Name, mode == ReviewMode.Parallel || index == 0));
    }

    public Guid Id { get; private set; }
    public Guid ScrId { get; private set; }
    public int Sequence { get; private set; }
    public string SnapshotHash { get; private set; } = string.Empty;
    public ReviewMode Mode { get; private set; }
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

    internal void ReplaceFutureApprover(int position, ApproverSelection replacement)
    {
        EnsureActive();
        if (Mode == ReviewMode.Parallel) throw new DomainException("Parallel review assignments are activated together; cancel and restart to change an approver.");
        if (position <= ActivePosition) throw new DomainException("Only not-yet-reached approvers can be replaced.");
        if (_steps.Any(x => x.Position != position && string.Equals(x.ApproverId, replacement.UserId, StringComparison.OrdinalIgnoreCase)))
            throw new DomainException("An approver cannot appear twice in one sequence.");
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

public sealed record ApproverSelection(string UserId, string Name);
