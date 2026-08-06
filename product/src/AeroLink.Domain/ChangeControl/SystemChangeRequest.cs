using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AeroLink.Domain.Common;

namespace AeroLink.Domain.ChangeControl;

public enum ChangeRequestState { Draft, InReview, Approved, Deferred, SelectedForBaseline }
public enum ChangeRequestType { System, Software }

/// <summary>
/// Which identifier prefix a change request carries, decided by what it is allowed to change.
///
/// This is the single authority: the allocator asks it what to number a new record, and the data migration
/// that renamed the existing ones asked it the same question. A change request that could not answer it
/// would be a controlled record with no name, which is why a software change request must declare its level
/// before it exists rather than acquiring one later.
/// </summary>
public static class ChangeRequestNumbering
{
    public const string SystemPrefix = "SRCR";
    public const string HighLevelPrefix = "HLRCR";
    public const string LowLevelPrefix = "LLRCR";

    public static string Prefix(ChangeRequestType type, RequirementLevel? softwareLevel) => type switch
    {
        ChangeRequestType.System => SystemPrefix,
        _ => softwareLevel switch
        {
            RequirementLevel.HighLevel => HighLevelPrefix,
            RequirementLevel.LowLevel => LowLevelPrefix,
            _ => throw new DomainException("A software change request must declare HLR or LLR scope before it can be numbered."),
        },
    };
}

public sealed class SystemChangeRequest
{
    private readonly List<RequirementChange> _requirementChanges = [];
    private readonly List<ReviewCycle> _reviewCycles = [];
    private readonly List<AuditEvent> _auditEvents = [];
    private SystemChangeRequest() { }

    public SystemChangeRequest(string baseNumber, int revision, Guid projectId, Guid targetReleaseId,
        string title, string problem, string analysis, string solution, string authorId, DateTimeOffset now,
        ChangeRequestType type = ChangeRequestType.System,
        string? problemRich = null, string? analysisRich = null, string? solutionRich = null,
        RequirementLevel? softwareLevel = null)
    {
        if (string.IsNullOrWhiteSpace(title)) throw new DomainException("A change request title is required.");
        Id = Guid.NewGuid();
        BaseNumber = ArtifactNumber.ValidateBase(baseNumber);
        Revision = revision;
        ProjectId = projectId;
        TargetReleaseId = targetReleaseId;
        Title = title.Trim();
        SetCase(problem, analysis, solution, problemRich, analysisRich, solutionRich);
        AuthorId = authorId;
        Type = type;
        if (type == ChangeRequestType.System && softwareLevel is not null)
            throw new DomainException("A System change request cannot declare a software requirement level.");
        // Required, not merely constrained: HLR and LLR change requests are numbered apart, so a software
        // change request without a level is a controlled record that cannot be named.
        if (type == ChangeRequestType.Software && softwareLevel is not (RequirementLevel.HighLevel or RequirementLevel.LowLevel))
            throw new DomainException("A software change request must declare HLR or LLR scope.");
        if (ChangeRequestNumbering.Prefix(type, softwareLevel) is var expected
            && !BaseNumber.StartsWith(expected + "-", StringComparison.Ordinal))
            throw new DomainException($"A {(type == ChangeRequestType.System ? "System" : expected == ChangeRequestNumbering.HighLevelPrefix ? "HLR" : "LLR")} change request must be numbered {expected}-.");
        SoftwareLevel = softwareLevel;
        State = ChangeRequestState.Draft;
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
    /// <summary>
    /// How far this change request had got when it was deferred, or null when it is not on the shelf.
    ///
    /// State and allocation are two different facts and `ChangeRequestState` was carrying both. "Which build is this going
    /// into" and "how far has it got" are answered separately now: Deferred is where the work sits, and this is
    /// how far it got before it went there.
    /// </summary>
    public ChangeRequestState? DeferredFromState { get; private set; }
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
    /// <summary>
    /// The HLR or LLR workspace in which an empty Software Draft was started. Once proposals exist their
    /// controlled levels remain authoritative, but this field keeps a case-only Draft discoverable instead
    /// of making it disappear from both engineering work lists.
    /// </summary>
    public RequirementLevel? SoftwareLevel { get; private set; }
    public ChangeRequestState State { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public long Version { get; private set; } = 1;
    public IReadOnlyCollection<RequirementChange> RequirementChanges => _requirementChanges.AsReadOnly();
    public IReadOnlyCollection<ReviewCycle> ReviewCycles => _reviewCycles.AsReadOnly();
    public IReadOnlyCollection<AuditEvent> AuditEvents => _auditEvents.AsReadOnly();
    public ReviewCycle? ActiveReviewCycle => _reviewCycles.LastOrDefault(x => x.State == ReviewCycleState.Active);

    /// <summary>
    /// System change requests govern System requirements. Software change requests govern HLRs and LLRs.
    /// Keeping the rule beside the aggregate prevents imports, integrations, seed reconciliation, and future
    /// endpoints from creating a request which looks valid until its downstream assessment is raised.
    /// </summary>
    public static bool AcceptsRequirementLevel(ChangeRequestType type, RequirementLevel level) =>
        type == ChangeRequestType.System ? level == RequirementLevel.System : level != RequirementLevel.System;

    public RequirementChange AddRequirementChange(string actorId, string baseNumber, int revision,
        RequirementLevel level, RequirementChangeKind kind, string statement, string rationale,
        string verificationMethod, DateTimeOffset now, string richText = "", string attributesJson = "{}",
        string impactDispositionJson = RequirementAuthoringJson.CompleteImpactDispositions,
        Guid? targetSectionId = null, bool administratorAuthority = false,
        string proposedUpstreamRevisionIdsJson = "[]")
    {
        EnsureAuthor(actorId, administratorAuthority);
        EnsureDraft();
        EnsureRequirementLevel(level);
        if (string.IsNullOrWhiteSpace(statement) && kind != RequirementChangeKind.Retire)
            throw new DomainException("A requirement statement is required.");
        var change = new RequirementChange(Id, baseNumber, revision, level, kind, statement, rationale, verificationMethod,
            richText, attributesJson, impactDispositionJson, targetSectionId, proposedUpstreamRevisionIdsJson);
        _requirementChanges.Add(change);
        UpdatedAt = now;
        Audit("RequirementChangeAdded", actorId,
            $"Added {change.Kind} {change.DisplayNumber}" +
            (RequirementAuthoringJson.IsDerived(change.AttributesJson) ? " as a derived requirement with a documented rationale." : "."), now);
        return change;
    }

    public void UpdateDraft(string actorId, string title, string problem, string analysis, string solution,
        IReadOnlyList<RequirementChangeDraft> changes, DateTimeOffset now,
        string? problemRich = null, string? analysisRich = null, string? solutionRich = null,
        bool administratorAuthority = false)
    {
        EnsureAuthor(actorId, administratorAuthority);
        EnsureDraft();
        if (string.IsNullOrWhiteSpace(title)) throw new DomainException("A change request title is required.");
        foreach (var item in changes) EnsureRequirementLevel(item.Level);
        Title = title.Trim();
        SetCase(problem, analysis, solution, problemRich, analysisRich, solutionRich);
        _requirementChanges.Clear();
        foreach (var item in changes)
        {
            if (string.IsNullOrWhiteSpace(item.Statement) && item.Kind != RequirementChangeKind.Retire)
                throw new DomainException("A requirement statement is required unless the requirement is being retired.");
            _requirementChanges.Add(new RequirementChange(Id, item.BaseNumber, item.Revision, item.Level, item.Kind,
                item.Statement, item.Rationale, item.VerificationMethod, item.RichText, item.AttributesJson, item.ImpactDispositionJson,
                item.TargetSectionId, item.ProposedUpstreamRevisionIdsJson));
        }
        UpdatedAt = now;
        var derivedCount = _requirementChanges.Count(x => RequirementAuthoringJson.IsDerived(x.AttributesJson));
        Audit("ScrDraftUpdated", actorId,
            $"Updated {DisplayNumber} Draft with {changes.Count} proposed requirement changes and {derivedCount} documented derived exception(s).", now);
    }

    public ReviewCycle SubmitForReview(string actorId, IReadOnlyList<ApproverSelection> approvers,
        DateTimeOffset now, ReviewMode mode = ReviewMode.Sequential, ReviewWorkflowSpecification? workflow = null,
        bool administratorAuthority = false)
    {
        EnsureAuthor(actorId, administratorAuthority);
        EnsureDraft();
        ValidateReadyForReview();
        var cycle = new ReviewCycle(Id, _reviewCycles.Count + 1, ComputeSnapshotHash(), approvers, now, mode, workflow);
        _reviewCycles.Add(cycle);
        State = ChangeRequestState.InReview;
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
            State = ChangeRequestState.Approved;
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
        State = ChangeRequestState.Draft;
        UpdatedAt = now;
        Audit("ChangesRequested", actorId, $"Returned {DisplayNumber} to Draft at the same revision: {reason}", now);
    }

    /// <summary>
    /// Takes a change request out of review and puts it back in Draft, at the same revision.
    ///
    /// `RequestChanges` already did something close to this, and only the reviewer whose turn it was could do
    /// it — which is the wrong shape for the common case. An author who submitted too early, or a lead who can
    /// see the review is pointless because the change is being reworked, had no way to stop it. Their only
    /// options were to wait for a reviewer to reject work everybody already knew was going to change, or to
    /// ask that reviewer to do it for them.
    ///
    /// The two are kept apart deliberately. Requesting changes is a reviewer's decision about the content and
    /// is recorded as one; cancelling is a decision to stop the review itself, and reads that way in the
    /// history. Both land in Draft at the same revision, because neither is a rejection of the record.
    ///
    /// Who may do it is decided by the caller, which knows the actor's Program roles. What is settled here is
    /// that a reason is required: a review that stopped for no recorded reason leaves the next reader unable
    /// to tell whether it was withdrawn, superseded, or abandoned by accident.
    /// </summary>
    public void CancelReview(string actorId, string reason, DateTimeOffset now)
    {
        EnsureInReview();
        if (string.IsNullOrWhiteSpace(actorId)) throw new DomainException("A cancelling actor is required.");
        if (string.IsNullOrWhiteSpace(reason)) throw new DomainException("Say why this review is being cancelled.");
        ActiveReviewCycle!.Cancel(reason, now);
        State = ChangeRequestState.Draft;
        UpdatedAt = now;
        Audit("ReviewCancelled", actorId, $"Cancelled the review of {DisplayNumber} and returned it to Draft at the same revision: {reason.Trim()}", now);
    }

    public void ReplaceFutureApprover(string actorId, int position, ApproverSelection replacement,
        DateTimeOffset now, ReviewWorkflowSpecification? workflow = null, bool administratorAuthority = false)
    {
        EnsureAuthor(actorId, administratorAuthority);
        EnsureInReview();
        var cycle = ActiveReviewCycle!;
        var previous = cycle.Steps.Single(x => x.Position == position).ApproverName;
        cycle.ReplaceFutureApprover(position, replacement, workflow);
        UpdatedAt = now;
        Audit("FutureApproverReplaced", actorId, $"Replaced position {position + 1}: {previous} -> {replacement.Name}.", now);
    }

    public ReviewCycle CancelAndRestartForWrongApprover(string actorId, string reason,
        IReadOnlyList<ApproverSelection> correctedApprovers, DateTimeOffset now,
        ReviewWorkflowSpecification? workflow = null, bool administratorAuthority = false)
    {
        EnsureAuthor(actorId, administratorAuthority);
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
    public SystemChangeRequest StartNextRevision(string actorId, DateTimeOffset now, bool targetReleaseIsReleased,
        bool administratorAuthority = false)
    {
        EnsureAuthor(actorId, administratorAuthority);
        if (State is not (ChangeRequestState.Approved or ChangeRequestState.SelectedForBaseline))
            throw new DomainException("Only an approved change request can advance to its next revision.");
        if (targetReleaseIsReleased)
            throw new DomainException(
                "This change request is incorporated in a released build and cannot be revised. Raise a new one against the in-work build.");
        var next = new SystemChangeRequest(BaseNumber, Revision + 1, ProjectId, TargetReleaseId,
            Title, Problem, Analysis, Solution, AuthorId, now, Type, ProblemRich, AnalysisRich, SolutionRich, SoftwareLevel);
        foreach (var item in _requirementChanges)
            next.AddRequirementChange(actorId, item.BaseNumber, item.Revision, item.Level, item.Kind,
                item.Statement, item.Rationale, item.VerificationMethod, now, item.RichText, item.AttributesJson, item.ImpactDispositionJson,
                item.TargetSectionId, administratorAuthority, item.ProposedUpstreamRevisionIdsJson);
        return next;
    }

    public void MarkSelectedForBaseline(string actorId, DateTimeOffset now)
    {
        if (State != ChangeRequestState.Approved) throw new DomainException("Only an approved change request can be selected for a baseline.");
        State = ChangeRequestState.SelectedForBaseline;
        UpdatedAt = now;
        // Says what happened to the change, not what happened to the baseline. Selection into a candidate
        // baseline is the mechanism; being allocated to a build is the fact a reader opened the history for.
        // The event type is unchanged, because it names an event that is already recorded thousands of times
        // and renaming it would make the old entries and the new ones look like different things.
        Audit("SelectedForBaseline", actorId, $"Allocated {DisplayNumber} to the build.", now);
    }

    public void UnmarkSelectedForBaseline(string actorId, DateTimeOffset now)
    {
        if (State != ChangeRequestState.SelectedForBaseline) throw new DomainException("The change request is not selected for a baseline.");
        State = ChangeRequestState.Approved;
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
    public void Defer(string actorId, string reason, DateTimeOffset now, bool administratorAuthority = false)
    {
        EnsureAuthor(actorId, administratorAuthority);
        if (State == ChangeRequestState.Deferred) throw new DomainException("The change request is already deferred.");
        if (State == ChangeRequestState.SelectedForBaseline)
            throw new DomainException("Remove the change request from its candidate baseline before deferring it.");
        if (string.IsNullOrWhiteSpace(reason)) throw new DomainException("A deferral reason is required.");
        if (State == ChangeRequestState.InReview) ActiveReviewCycle?.Cancel(reason.Trim(), now);
        // Remembered, because deferring changes where the work sits and not how far it got. A change request put
        // away while approved is still approved work; one put away as a Draft still needs writing. Storing only
        // "Deferred" loses that, and a shelf that cannot tell a signed-off change from an unwritten one is a
        // shelf nobody can plan from. Reinstate puts it back exactly where it was.
        DeferredFromState = State;
        State = ChangeRequestState.Deferred; UpdatedAt = now; Audit("ChangeRequestDeferred", actorId, reason.Trim(), now);
    }

    /// <summary>
    /// Takes a deferred change request off the shelf, back to the state it was in when it went on.
    ///
    /// The review cycle is not resumed. Deferring from InReview cancels the cycle, which is right — the
    /// approvers were asked about work that has since been put away — so a change request that was In Review
    /// comes back as a Draft and its author submits it again. Anything else would restore signatures against a
    /// snapshot nobody has looked at since.
    /// </summary>
    public void Reinstate(string actorId, DateTimeOffset now, bool administratorAuthority = false)
    {
        EnsureAuthor(actorId, administratorAuthority);
        if (State != ChangeRequestState.Deferred) throw new DomainException("Only a deferred change request can be reinstated.");
        var restored = DeferredFromState switch
        {
            ChangeRequestState.InReview => ChangeRequestState.Draft,
            // Deferred rows that predate the state being remembered come back as Drafts. That is the safe
            // direction: an author can resubmit a Draft, where claiming approval nobody gave cannot be undone.
            null => ChangeRequestState.Draft,
            var value => value.Value,
        };
        State = restored;
        DeferredFromState = null;
        UpdatedAt = now;
        Audit("ChangeRequestReinstated", actorId, $"Reinstated {DisplayNumber} as {restored}.", now);
    }

    public void Retarget(string actorId, Guid targetReleaseId, string reason, DateTimeOffset now,
        bool administratorAuthority = false)
    {
        EnsureAuthor(actorId, administratorAuthority);
        if (State is not (ChangeRequestState.Draft or ChangeRequestState.Approved or ChangeRequestState.Deferred))
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
        foreach (var item in _requirementChanges) EnsureRequirementLevel(item.Level);
    }

    private void EnsureRequirementLevel(RequirementLevel level)
    {
        if (!AcceptsRequirementLevel(Type, level))
            throw new DomainException(Type == ChangeRequestType.System
                ? "A System change request can contain System requirements only. Use an HLRCR or LLRCR for software work."
                : "A Software change request can contain HLR or LLR requirements only. Use an SRCR for System work.");
        if (Type == ChangeRequestType.Software && SoftwareLevel is not null && level != SoftwareLevel)
            throw new DomainException($"This Software Draft belongs to the {(SoftwareLevel == RequirementLevel.HighLevel ? "HLR" : "LLR")} workspace and cannot contain {(level == RequirementLevel.HighLevel ? "HLR" : "LLR")} changes.");
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
                $"{x.DisplayNumber}:{x.Level}:{x.Kind}:{x.Statement}:{x.Rationale}:{x.VerificationMethod}:{x.RichText}:{x.AttributesJson}:{x.ImpactDispositionJson}:{x.ProposedUpstreamRevisionIdsJson}")));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant();
    }

    private void Audit(string type, string actor, string detail, DateTimeOffset now) =>
        _auditEvents.Add(new AuditEvent(Id, type, actor, detail, now));
    private void EnsureAuthor(string actorId, bool administratorAuthority)
    {
        if (!administratorAuthority && !string.Equals(AuthorId, actorId, StringComparison.OrdinalIgnoreCase))
            throw new DomainException("Only the change request author can perform this action.");
    }
    private void EnsureDraft() { if (State != ChangeRequestState.Draft) throw new DomainException("The change request must be in Draft."); }
    private void EnsureInReview() { if (State != ChangeRequestState.InReview || ActiveReviewCycle is null) throw new DomainException("The change request is not in active review."); }
}
