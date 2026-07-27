using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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
        ChangeRequestType type = ChangeRequestType.System,
        string? problemRich = null, string? analysisRich = null, string? solutionRich = null)
    {
        Id = Guid.NewGuid();
        BaseNumber = ArtifactNumber.ValidateBase(baseNumber);
        Revision = revision;
        ProjectId = projectId;
        TargetReleaseId = targetReleaseId;
        Title = title.Trim();
        SetCase(problem, analysis, solution, problemRich, analysisRich, solutionRich);
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

    // The rich forms are what the author wrote; the plain forms above are their readable projection, derived
    // here rather than supplied, so the two can never disagree about what the change case says. Everything
    // that predates rich authoring — and every consumer that cannot render structure — keeps reading the
    // plain form and is unaffected.
    public string ProblemRich { get; private set; } = Content.RichContent.Empty;
    public string AnalysisRich { get; private set; } = Content.RichContent.Empty;
    public string SolutionRich { get; private set; } = Content.RichContent.Empty;
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
        string verificationMethod, DateTimeOffset now, string richText = "", string attributesJson = "{}",
        string impactDispositionJson = "{}")
    {
        EnsureAuthor(actorId);
        EnsureDraft();
        if (string.IsNullOrWhiteSpace(statement) && kind != RequirementChangeKind.Retire)
            throw new DomainException("A requirement statement is required.");
        var change = new RequirementChange(Id, baseNumber, revision, level, kind, statement, rationale, verificationMethod,
            richText, attributesJson, impactDispositionJson);
        _requirementChanges.Add(change);
        UpdatedAt = now;
        Audit("RequirementChangeAdded", actorId, $"Added {change.Kind} {change.DisplayNumber}.", now);
        return change;
    }

    public void UpdateDraft(string actorId, string title, string problem, string analysis, string solution,
        IReadOnlyList<RequirementChangeDraft> changes, DateTimeOffset now,
        string? problemRich = null, string? analysisRich = null, string? solutionRich = null)
    {
        EnsureAuthor(actorId);
        EnsureDraft();
        if (string.IsNullOrWhiteSpace(title)) throw new DomainException("An SCR title is required.");
        Title = title.Trim();
        SetCase(problem, analysis, solution, problemRich, analysisRich, solutionRich);
        _requirementChanges.Clear();
        foreach (var item in changes)
        {
            if (string.IsNullOrWhiteSpace(item.Statement) && item.Kind != RequirementChangeKind.Retire)
                throw new DomainException("A requirement statement is required unless the requirement is being retired.");
            _requirementChanges.Add(new RequirementChange(Id, item.BaseNumber, item.Revision, item.Level, item.Kind,
                item.Statement, item.Rationale, item.VerificationMethod, item.RichText, item.AttributesJson, item.ImpactDispositionJson));
        }
        UpdatedAt = now;
        Audit("ScrDraftUpdated", actorId, $"Updated {DisplayNumber} Draft with {changes.Count} proposed requirement changes.", now);
    }

    public ReviewCycle SubmitForReview(string actorId, IReadOnlyList<ApproverSelection> approvers,
        DateTimeOffset now, ReviewMode mode = ReviewMode.Sequential, ReviewWorkflowSpecification? workflow = null)
    {
        EnsureAuthor(actorId);
        EnsureDraft();
        ValidateReadyForReview();
        var cycle = new ReviewCycle(Id, _reviewCycles.Count + 1, ComputeSnapshotHash(), approvers, now, mode, workflow);
        _reviewCycles.Add(cycle);
        State = ScrState.InReview;
        UpdatedAt = now;
        Audit("ReviewStarted", actorId,
            $"Started {cycle.Mode.ToString().ToLowerInvariant()} review cycle {cycle.Sequence} with {approvers.Count} approvers" +
            (workflow is null ? "." : $" following {workflow.Name} v{workflow.Version}."), now);
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
        var active = cycle.Steps.SingleOrDefault(x => x.State == ApprovalStepState.Active && string.Equals(x.ApproverId, actorId, StringComparison.OrdinalIgnoreCase));
        if (active is null)
            throw new DomainException("Only the active approver can request changes.");
        cycle.RequestChanges(reason, now);
        State = ScrState.Draft;
        UpdatedAt = now;
        Audit("ChangesRequested", actorId, $"Returned {DisplayNumber} to Draft at the same revision: {reason}", now);
    }

    public void ReplaceFutureApprover(string actorId, int position, ApproverSelection replacement,
        DateTimeOffset now, ReviewWorkflowSpecification? workflow = null)
    {
        EnsureAuthor(actorId);
        EnsureInReview();
        var cycle = ActiveReviewCycle!;
        var previous = cycle.Steps.Single(x => x.Position == position).ApproverName;
        cycle.ReplaceFutureApprover(position, replacement, workflow);
        UpdatedAt = now;
        Audit("FutureApproverReplaced", actorId, $"Replaced position {position + 1}: {previous} -> {replacement.Name}.", now);
    }

    public ReviewCycle CancelAndRestartForWrongApprover(string actorId, string reason,
        IReadOnlyList<ApproverSelection> correctedApprovers, DateTimeOffset now,
        ReviewWorkflowSpecification? workflow = null)
    {
        EnsureAuthor(actorId);
        EnsureInReview();
        // Cancelling an in-flight review discards recorded approval authority, so it is exactly the kind of
        // act that must carry an attributable reason. Every other controlled action here demands one.
        if (string.IsNullOrWhiteSpace(reason)) throw new DomainException("A reason is required to cancel and restart a review.");
        if (correctedApprovers is null || correctedApprovers.Count == 0)
            throw new DomainException("At least one corrected approver is required.");
        var prior = ActiveReviewCycle!;
        prior.Cancel(reason, now);
        var replacement = new ReviewCycle(Id, _reviewCycles.Count + 1, prior.SnapshotHash, correctedApprovers, now, prior.Mode, workflow);
        _reviewCycles.Add(replacement);
        UpdatedAt = now;
        Audit("ReviewCancelledAndRestarted", actorId,
            $"Cancelled cycle {prior.Sequence} and restarted as cycle {replacement.Sequence}: {reason}", now);
        return replacement;
    }

    /// <summary>
    /// Supersedes this revision with the next one, carrying the same content forward as a Draft.
    ///
    /// Approved and SelectedForBaseline are the same fact to the person asking: the engineering is signed for.
    /// They differ only in whether a candidate baseline has picked the row up yet, and in a working programme
    /// every approved change request gets picked up — which is why requiring exactly `Approved` made this
    /// unreachable across a 113-record programme where not one change request sat in that state.
    ///
    /// What must not happen is revising a change request already incorporated in a *released* build. That
    /// content is frozen history, and a `.01` of it would claim the release said something it never said. The
    /// answer there is a new change request against the in-work build, so the caller passes the release's
    /// state in rather than the rule living outside the aggregate where a second caller could forget it.
    /// </summary>
    public SystemChangeRequest StartNextRevision(string actorId, DateTimeOffset now, bool targetReleaseIsReleased)
    {
        EnsureAuthor(actorId);
        if (State is not (ScrState.Approved or ScrState.SelectedForBaseline))
            throw new DomainException("Only an approved SCR can advance to its next revision.");
        if (targetReleaseIsReleased)
            throw new DomainException(
                "This SCR is incorporated in a released build and cannot be revised. Raise a new SCR against the in-work build.");
        var next = new SystemChangeRequest(BaseNumber, Revision + 1, ProjectId, TargetReleaseId,
            Title, Problem, Analysis, Solution, AuthorId, now, Type, ProblemRich, AnalysisRich, SolutionRich);
        foreach (var item in _requirementChanges)
            next.AddRequirementChange(actorId, item.BaseNumber, item.Revision, item.Level, item.Kind,
                item.Statement, item.Rationale, item.VerificationMethod, now, item.RichText, item.AttributesJson, item.ImpactDispositionJson);
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

    /// <summary>
    /// Puts the change request away for another day, from wherever it currently is.
    ///
    /// Deferral used to be reachable only from Draft and Approved, which left the middle of the lifecycle
    /// with nowhere to go: a change request under review that the programme had decided to drop had to be
    /// rejected — throwing away a review that raised no engineering objection — or left in review forever,
    /// holding a release gate that would never clear. Neither is what happened, so neither should be what the
    /// record says.
    ///
    /// From review, the cycle in flight is cancelled rather than abandoned, carrying the deferral reason as
    /// its closure. Approvals already given keep their decisions and their attribution; what they lose is
    /// force, because the revision they were given against is no longer heading for this release.
    ///
    /// Not reachable from SelectedForBaseline, and that is not an omission: a change request already chosen
    /// into a candidate baseline has to be taken out of it first, which is an explicit, attributable act with
    /// its own audit event rather than a side effect of deferring.
    /// </summary>
    public void Defer(string actorId, string reason, DateTimeOffset now)
    {
        EnsureAuthor(actorId);
        if (State == ScrState.Deferred) throw new DomainException("The change request is already deferred.");
        if (State == ScrState.SelectedForBaseline)
            throw new DomainException("Remove the change request from its candidate baseline before deferring it.");
        if (string.IsNullOrWhiteSpace(reason)) throw new DomainException("A deferral reason is required.");
        if (State == ScrState.InReview) ActiveReviewCycle?.Cancel(reason.Trim(), now);
        State = ScrState.Deferred; UpdatedAt = now; Audit("ChangeRequestDeferred", actorId, reason.Trim(), now);
    }

    public void Retarget(string actorId, Guid targetReleaseId, string reason, DateTimeOffset now)
    {
        EnsureAuthor(actorId);
        if (State is not (ScrState.Draft or ScrState.Approved or ScrState.Deferred))
            throw new DomainException("Only a Draft, Approved, or Deferred change request can move to another release.");
        if (targetReleaseId == Guid.Empty || targetReleaseId == TargetReleaseId)
            throw new DomainException("Choose a different target release.");
        if (string.IsNullOrWhiteSpace(reason)) throw new DomainException("A retarget rationale is required.");
        var prior = TargetReleaseId;
        TargetReleaseId = targetReleaseId;
        UpdatedAt = now;
        Audit("TargetReleaseChanged", actorId, $"Moved {DisplayNumber} from release {prior} to {targetReleaseId}: {reason.Trim()}", now);
    }

    private void ValidateReadyForReview()
    {
        if (string.IsNullOrWhiteSpace(Problem) || string.IsNullOrWhiteSpace(Analysis) || string.IsNullOrWhiteSpace(Solution))
            throw new DomainException("Problem, Analysis, and Solution are required before review.");
        if (_requirementChanges.Count == 0)
            throw new DomainException("At least one requirement change is required before review.");
        var requiredImpacts=new[]{"trace","verification","documents","baseline","collaboration"};
        foreach(var change in _requirementChanges)
        {
            if(change.ImpactDispositionJson=="{}")continue;
            Dictionary<string,string> dispositions;try{dispositions=JsonSerializer.Deserialize<Dictionary<string,string>>(change.ImpactDispositionJson)??[];}catch(JsonException){throw new DomainException($"{change.DisplayNumber} contains invalid impact dispositions.");}
            if(requiredImpacts.Any(key=>!dispositions.TryGetValue(key,out var value)||string.IsNullOrWhiteSpace(value)||value.Equals("Pending",StringComparison.OrdinalIgnoreCase)))throw new DomainException($"Complete every impact disposition for {change.DisplayNumber} before review.");
        }
    }

    /// <summary>
    /// Sets the change case from whichever form the author supplied.
    ///
    /// When rich content is given it is authoritative and the plain text is derived from it; when it is not,
    /// the plain text is what was written and the rich form is that same text as a single paragraph. Either
    /// way both are populated and both say the same thing, so no reader has to know which one the author
    /// used.
    /// </summary>
    private void SetCase(string problem, string analysis, string solution,
        string? problemRich, string? analysisRich, string? solutionRich)
    {
        (Problem, ProblemRich) = Resolve(problem, problemRich);
        (Analysis, AnalysisRich) = Resolve(analysis, analysisRich);
        (Solution, SolutionRich) = Resolve(solution, solutionRich);

        static (string Plain, string Rich) Resolve(string plain, string? rich)
        {
            if (string.IsNullOrWhiteSpace(rich)) return (plain.Trim(), Content.RichContent.FromPlainText(plain));
            var canonical = Content.RichContent.Canonicalize(rich);
            return (Content.RichContent.ToPlainText(canonical), canonical);
        }
    }

    private string ComputeSnapshotHash()
    {
        // The rich forms are in the hash in their own right. Two different structures can reduce to the same
        // readable text — a table and a list of lines, say — and hashing only the projection would let the
        // thing an approver actually looked at change underneath a recorded signature.
        var content = string.Join("|", DisplayNumber, Title, Problem, Analysis, Solution,
            ProblemRich, AnalysisRich, SolutionRich,
            string.Join(";", _requirementChanges.OrderBy(x => x.DisplayNumber).Select(x =>
                $"{x.DisplayNumber}:{x.Level}:{x.Kind}:{x.Statement}:{x.Rationale}:{x.VerificationMethod}:{x.RichText}:{x.AttributesJson}:{x.ImpactDispositionJson}")));
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
