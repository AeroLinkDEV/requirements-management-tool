using System.Security.Cryptography;
using System.Text;
using AeroLink.Domain.Common;

namespace AeroLink.Domain.ChangeControl;

public enum ScrState { Draft, InReview, Approved, Deferred, SelectedForBaseline }
public enum ChangeRequestType { System, Software }

public sealed class SystemChangeRequest
{
    private readonly List<RequirementChange> _requirementChanges = [];
    private readonly List<ReviewCycle> _reviewCycles = [];
    private readonly List<AuditEvent> _auditEvents = [];
    private SystemChangeRequest() { }

    public SystemChangeRequest(string baseNumber, int revision, Guid projectId, Guid targetReleaseId,
        string title, string problem, string analysis, string solution, string authorId, DateTimeOffset now,
        ChangeRequestType type = ChangeRequestType.System)
    {
        Id = Guid.NewGuid();
        BaseNumber = ArtifactNumber.ValidateBase(baseNumber);
        Revision = revision;
        ProjectId = projectId;
        TargetReleaseId = targetReleaseId;
        Title = title.Trim();
        Problem = problem.Trim();
        Analysis = analysis.Trim();
        Solution = solution.Trim();
        AuthorId = authorId;
        Type = type;
        State = ScrState.Draft;
        CreatedAt = now;
        UpdatedAt = now;
        Audit("ScrCreated", authorId, $"Created {DisplayNumber}.", now);
    }

    public Guid Id { get; private set; }
    public string BaseNumber { get; private set; } = string.Empty;
    public int Revision { get; private set; }
    public string DisplayNumber => ArtifactNumber.Display(BaseNumber, Revision);
    public Guid ProjectId { get; private set; }
    public Guid TargetReleaseId { get; private set; }
    public string Title { get; private set; } = string.Empty;
    public string Problem { get; private set; } = string.Empty;
    public string Analysis { get; private set; } = string.Empty;
    public string Solution { get; private set; } = string.Empty;
    public string AuthorId { get; private set; } = string.Empty;
    public ChangeRequestType Type { get; private set; }
    public ScrState State { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public long Version { get; private set; } = 1;
    public IReadOnlyCollection<RequirementChange> RequirementChanges => _requirementChanges.AsReadOnly();
    public IReadOnlyCollection<ReviewCycle> ReviewCycles => _reviewCycles.AsReadOnly();
    public IReadOnlyCollection<AuditEvent> AuditEvents => _auditEvents.AsReadOnly();
    public ReviewCycle? ActiveReviewCycle => _reviewCycles.LastOrDefault(x => x.State == ReviewCycleState.Active);

    public RequirementChange AddRequirementChange(string actorId, string baseNumber, int revision,
        RequirementLevel level, RequirementChangeKind kind, string statement, string rationale,
        string verificationMethod, DateTimeOffset now)
    {
        EnsureAuthor(actorId);
        EnsureDraft();
        if (string.IsNullOrWhiteSpace(statement) && kind != RequirementChangeKind.Retire)
            throw new DomainException("A requirement statement is required.");
        var change = new RequirementChange(Id, baseNumber, revision, level, kind, statement, rationale, verificationMethod);
        _requirementChanges.Add(change);
        UpdatedAt = now;
        Audit("RequirementChangeAdded", actorId, $"Added {change.Kind} {change.DisplayNumber}.", now);
        return change;
    }

    public void UpdateDraft(string actorId, string title, string problem, string analysis, string solution,
        IReadOnlyList<RequirementChangeDraft> changes, DateTimeOffset now)
    {
        EnsureAuthor(actorId);
        EnsureDraft();
        if (string.IsNullOrWhiteSpace(title)) throw new DomainException("An SCR title is required.");
        Title = title.Trim();
        Problem = problem.Trim();
        Analysis = analysis.Trim();
        Solution = solution.Trim();
        _requirementChanges.Clear();
        foreach (var item in changes)
        {
            if (string.IsNullOrWhiteSpace(item.Statement) && item.Kind != RequirementChangeKind.Retire)
                throw new DomainException("A requirement statement is required unless the requirement is being retired.");
            _requirementChanges.Add(new RequirementChange(Id, item.BaseNumber, item.Revision, item.Level, item.Kind,
                item.Statement, item.Rationale, item.VerificationMethod));
        }
        UpdatedAt = now;
        Audit("ScrDraftUpdated", actorId, $"Updated {DisplayNumber} Draft with {changes.Count} proposed requirement changes.", now);
    }

    public ReviewCycle SubmitForReview(string actorId, IReadOnlyList<ApproverSelection> approvers, DateTimeOffset now)
    {
        EnsureAuthor(actorId);
        EnsureDraft();
        ValidateReadyForReview();
        var cycle = new ReviewCycle(Id, _reviewCycles.Count + 1, ComputeSnapshotHash(), approvers, now);
        _reviewCycles.Add(cycle);
        State = ScrState.InReview;
        UpdatedAt = now;
        Audit("ReviewStarted", actorId, $"Started review cycle {cycle.Sequence} with {approvers.Count} ordered approvers.", now);
        return cycle;
    }

    public void ApproveActiveStage(string actorId, DateTimeOffset now)
    {
        EnsureInReview();
        var cycle = ActiveReviewCycle!;
        var fullyApproved = cycle.Approve(actorId, now);
        Audit("ApprovalRecorded", actorId, $"Approved review cycle {cycle.Sequence} stage.", now);
        if (fullyApproved)
        {
            State = ScrState.Approved;
            Audit("ScrApproved", actorId, $"Unanimously approved {DisplayNumber}.", now);
        }
        UpdatedAt = now;
    }

    public void RequestChanges(string actorId, string reason, DateTimeOffset now)
    {
        EnsureInReview();
        var cycle = ActiveReviewCycle!;
        var active = cycle.Steps.Single(x => x.State == ApprovalStepState.Active);
        if (!string.Equals(active.ApproverId, actorId, StringComparison.OrdinalIgnoreCase))
            throw new DomainException("Only the active approver can request changes.");
        cycle.RequestChanges(reason, now);
        State = ScrState.Draft;
        UpdatedAt = now;
        Audit("ChangesRequested", actorId, $"Returned {DisplayNumber} to Draft at the same revision: {reason}", now);
    }

    public void ReplaceFutureApprover(string actorId, int position, ApproverSelection replacement, DateTimeOffset now)
    {
        EnsureAuthor(actorId);
        EnsureInReview();
        var cycle = ActiveReviewCycle!;
        var previous = cycle.Steps.Single(x => x.Position == position).ApproverName;
        cycle.ReplaceFutureApprover(position, replacement);
        UpdatedAt = now;
        Audit("FutureApproverReplaced", actorId, $"Replaced position {position + 1}: {previous} -> {replacement.Name}.", now);
    }

    public ReviewCycle CancelAndRestartForWrongApprover(string actorId, string reason,
        IReadOnlyList<ApproverSelection> correctedApprovers, DateTimeOffset now)
    {
        EnsureAuthor(actorId);
        EnsureInReview();
        var prior = ActiveReviewCycle!;
        prior.Cancel(reason, now);
        var replacement = new ReviewCycle(Id, _reviewCycles.Count + 1, prior.SnapshotHash, correctedApprovers, now);
        _reviewCycles.Add(replacement);
        UpdatedAt = now;
        Audit("ReviewCancelledAndRestarted", actorId,
            $"Cancelled cycle {prior.Sequence} and restarted as cycle {replacement.Sequence}: {reason}", now);
        return replacement;
    }

    public SystemChangeRequest StartNextRevision(string actorId, DateTimeOffset now)
    {
        EnsureAuthor(actorId);
        if (State != ScrState.Approved) throw new DomainException("Only an approved SCR can advance to its next revision.");
        var next = new SystemChangeRequest(BaseNumber, Revision + 1, ProjectId, TargetReleaseId,
            Title, Problem, Analysis, Solution, AuthorId, now, Type);
        foreach (var item in _requirementChanges)
            next.AddRequirementChange(actorId, item.BaseNumber, item.Revision, item.Level, item.Kind,
                item.Statement, item.Rationale, item.VerificationMethod, now);
        return next;
    }

    public void MarkSelectedForBaseline(string actorId, DateTimeOffset now)
    {
        if (State != ScrState.Approved) throw new DomainException("Only an approved SCR can be selected for a baseline.");
        State = ScrState.SelectedForBaseline;
        UpdatedAt = now;
        Audit("SelectedForBaseline", actorId, $"Selected {DisplayNumber} for a candidate baseline.", now);
    }

    public void UnmarkSelectedForBaseline(string actorId, DateTimeOffset now)
    {
        if (State != ScrState.SelectedForBaseline) throw new DomainException("The SCR is not selected for a baseline.");
        State = ScrState.Approved;
        UpdatedAt = now;
        Audit("RemovedFromCandidateBaseline", actorId, $"Returned {DisplayNumber} to Approved eligibility.", now);
    }

    public void Defer(string actorId, string reason, DateTimeOffset now)
    {
        EnsureAuthor(actorId);
        if (State is not (ScrState.Draft or ScrState.Approved)) throw new DomainException("Only a Draft or Approved change request can be deferred.");
        if (string.IsNullOrWhiteSpace(reason)) throw new DomainException("A deferral reason is required.");
        State = ScrState.Deferred; UpdatedAt = now; Audit("ChangeRequestDeferred", actorId, reason.Trim(), now);
    }

    private void ValidateReadyForReview()
    {
        if (string.IsNullOrWhiteSpace(Problem) || string.IsNullOrWhiteSpace(Analysis) || string.IsNullOrWhiteSpace(Solution))
            throw new DomainException("Problem, Analysis, and Solution are required before review.");
        if (_requirementChanges.Count == 0)
            throw new DomainException("At least one requirement change is required before review.");
    }

    private string ComputeSnapshotHash()
    {
        var content = string.Join("|", DisplayNumber, Title, Problem, Analysis, Solution,
            string.Join(";", _requirementChanges.OrderBy(x => x.DisplayNumber).Select(x =>
                $"{x.DisplayNumber}:{x.Level}:{x.Kind}:{x.Statement}:{x.Rationale}:{x.VerificationMethod}")));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant();
    }

    private void Audit(string type, string actor, string detail, DateTimeOffset now) =>
        _auditEvents.Add(new AuditEvent(Id, type, actor, detail, now));
    private void EnsureAuthor(string actorId)
    {
        if (!string.Equals(AuthorId, actorId, StringComparison.OrdinalIgnoreCase))
            throw new DomainException("Only the SCR author can perform this action.");
    }
    private void EnsureDraft() { if (State != ScrState.Draft) throw new DomainException("The SCR must be in Draft."); }
    private void EnsureInReview() { if (State != ScrState.InReview || ActiveReviewCycle is null) throw new DomainException("The SCR is not in active review."); }
}
