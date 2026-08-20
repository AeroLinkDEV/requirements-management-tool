using AeroLink.Domain.Common;

namespace AeroLink.Domain.Verification;

/// <summary>Why a verification impact item was raised.</summary>
public enum VerificationImpactTrigger
{
    /// <summary>An approved change introduced a requirement that has no verification yet.</summary>
    RequirementIntroduced,
    /// <summary>An approved change modified a requirement, so any existing coverage needs reassessment.</summary>
    RequirementModified,
    /// <summary>An approved retirement left a procedure covering no remaining requirement.</summary>
    ProcedureOrphaned
}

public enum VerificationImpactState { Open, Assigned, Resolved, Superseded }

/// <summary>
/// The governed engineering content of one verification-impact decision, as it appears in the TCR review
/// snapshot. Assignment and timestamps are deliberately absent: they are operational, not the content an
/// approver judges.
/// </summary>
public sealed record VerificationImpactSnapshot(
    Guid ItemId,
    Guid ChangeRequestId,
    VerificationImpactTrigger Trigger,
    Guid? RequirementChangeId,
    Guid? RequirementRevisionId,
    Guid? ProcedureId,
    string SubjectDisplayNumber,
    VerificationImpactOutcome? Outcome,
    TestProcedureChangeAction? ProcedureChangeAction,
    string ResolutionRationale,
    Guid? ResolvedProcedureId,
    Guid? ResolvedProcedureRevisionId,
    Guid? RetargetedRequirementRevisionId,
    bool PreReleaseEvidenceRequired);
public enum VerificationImpactHistoryAction { Resolved, Reopened }
public enum TestProcedureChangeAction { LinkExisting, CreateNew, ModifyExisting, RetireExisting, NoTestRequired }

/// <summary>
/// What a verification engineer decided. Every value is an explicit judgement — there is no outcome that
/// means "nobody looked", because a requirement must never reach an approved baseline without one.
/// </summary>
public enum VerificationImpactOutcome
{
    /// <summary>An approved procedure covers the exact requirement revision.</summary>
    ProcedureCoverageConfirmed,
    /// <summary>Verification is satisfied without a test — for example by analysis or inspection.</summary>
    NoTestRequired,
    /// <summary>
    /// A test is required and no procedure exists yet, so one has to be written.
    ///
    /// For a newly introduced requirement this is the ordinary answer, and until now it could not be given.
    /// The only outcomes were "an approved procedure covers this" and "no test required", so an engineer whose
    /// honest answer was "a procedure must be written" had to leave the item unanswered, go and author the
    /// procedure, get it approved, and come back. Meanwhile nothing could tell the difference between an item
    /// nobody had looked at and one where somebody had looked and knew exactly what was needed.
    ///
    /// It is a recorded decision, so the item stops reading as untouched work and has a named owner. It is not
    /// coverage: the coverage and evidence gates keep holding the release until the procedure actually exists,
    /// which is the point — the answer is a plan, not a result.
    /// </summary>
    NewProcedureRequired,
    /// <summary>The orphaned procedure is no longer needed and has been retired.</summary>
    ProcedureRetired,
    /// <summary>The orphaned procedure is deliberately kept despite covering no current requirement.</summary>
    ProcedureRetained,
    /// <summary>
    /// The orphaned procedure still tests something real, and is moved onto the requirement it now covers.
    ///
    /// Retiring a requirement removes what a procedure was written against, and the two answers available
    /// until now were to retire the procedure with it or to keep it covering nothing. Neither fits the common
    /// case: the behaviour was not withdrawn, it moved to another requirement, and the procedure that
    /// exercises it is still the right procedure. Retiring it would throw away working verification and its
    /// history; retaining it leaves a procedure in the library that answers for nothing and quietly stops
    /// counting as coverage anywhere.
    /// </summary>
    ProcedureRetargeted
}

/// <summary>
/// Work raised for the verification team when an approved change alters what must be tested.
///
/// Items are raised on change-request approval rather than on baseline inclusion, so verification can start
/// as soon as the engineering decision is settled. Each item inherits the change request's target release
/// and follows it if the change is deferred or retargeted, so the work is never stranded against a release
/// the requirement no longer belongs to.
///
/// The requirement author's declared verification method is carried on the item as context. It is never a
/// resolution: a requirement declared "verification by analysis" still requires a verification engineer to
/// confirm that no test is needed. This record concerns what must be verified, never test results.
/// </summary>
public sealed class VerificationImpactItem
{
    private VerificationImpactItem() { }

    private VerificationImpactItem(Guid projectId, Guid releaseId, Guid changeRequestId, Guid testChangeReviewId,
        VerificationImpactTrigger trigger, DateTimeOffset now)
    {
        if (projectId == Guid.Empty) throw new DomainException("A verification impact item requires its Project.");
        if (releaseId == Guid.Empty) throw new DomainException("A verification impact item requires its target release.");
        if (changeRequestId == Guid.Empty) throw new DomainException("A verification impact item requires its originating change request.");
        if (testChangeReviewId == Guid.Empty) throw new DomainException("A verification impact item requires its test change review.");
        Id = Guid.NewGuid();
        ProjectId = projectId;
        ReleaseId = releaseId;
        ChangeRequestId = changeRequestId;
        TestChangeReviewId = testChangeReviewId;
        Trigger = trigger;
        State = VerificationImpactState.Open;
        RaisedAt = now;
        UpdatedAt = now;
    }

    public static VerificationImpactItem ForIntroducedRequirement(Guid projectId, Guid releaseId, Guid changeRequestId, Guid testChangeReviewId,
        Guid requirementChangeId, string requirementDisplayNumber, string declaredVerificationMethod, DateTimeOffset now)
        => ForRequirement(projectId, releaseId, changeRequestId, VerificationImpactTrigger.RequirementIntroduced,
            testChangeReviewId, requirementChangeId, requirementDisplayNumber, declaredVerificationMethod, now);

    public static VerificationImpactItem ForModifiedRequirement(Guid projectId, Guid releaseId, Guid changeRequestId, Guid testChangeReviewId,
        Guid requirementChangeId, string requirementDisplayNumber, string declaredVerificationMethod, DateTimeOffset now)
        => ForRequirement(projectId, releaseId, changeRequestId, VerificationImpactTrigger.RequirementModified,
            testChangeReviewId, requirementChangeId, requirementDisplayNumber, declaredVerificationMethod, now);

    /// <summary>
    /// Work for a procedure left covering no requirement.
    ///
    /// <paramref name="causingBaselineId"/> is set only when a reopened baseline stranded it rather than a
    /// retirement. Both name the change request whose work removed the requirement, because that is where a
    /// reader following the trail has to end up either way -- but a retirement is a decision the change
    /// request made, while a reopen is a decision somebody made about the build, and an item that could not
    /// tell those apart would send the reader to a change request that was itself taken back with no
    /// indication of why.
    /// </summary>
    public static VerificationImpactItem ForOrphanedProcedure(Guid projectId, Guid releaseId, Guid changeRequestId, Guid testChangeReviewId,
        Guid procedureId, string procedureDisplayNumber, DateTimeOffset now, Guid? causingBaselineId = null)
    {
        if (procedureId == Guid.Empty) throw new DomainException("An orphaned-procedure item requires its procedure.");
        if (causingBaselineId == Guid.Empty) throw new DomainException("A causing baseline must be a real baseline or absent.");
        return new VerificationImpactItem(projectId, releaseId, changeRequestId, testChangeReviewId, VerificationImpactTrigger.ProcedureOrphaned, now)
        {
            ProcedureId = procedureId,
            CausingBaselineId = causingBaselineId,
            SubjectDisplayNumber = Required(procedureDisplayNumber, "procedure identifier")
        };
    }

    private static VerificationImpactItem ForRequirement(Guid projectId, Guid releaseId, Guid changeRequestId,
        VerificationImpactTrigger trigger, Guid testChangeReviewId, Guid requirementChangeId, string requirementDisplayNumber,
        string declaredVerificationMethod, DateTimeOffset now)
    {
        if (requirementChangeId == Guid.Empty) throw new DomainException("A requirement item requires its approved requirement change.");
        return new VerificationImpactItem(projectId, releaseId, changeRequestId, testChangeReviewId, trigger, now)
        {
            RequirementChangeId = requirementChangeId,
            SubjectDisplayNumber = Required(requirementDisplayNumber, "requirement identifier"),
            DeclaredVerificationMethod = declaredVerificationMethod?.Trim() ?? ""
        };
    }

    public Guid Id { get; private set; }
    public Guid ProjectId { get; private set; }
    public Guid ReleaseId { get; private set; }
    public Guid ChangeRequestId { get; private set; }
    public Guid TestChangeReviewId { get; private set; }
    public VerificationImpactTrigger Trigger { get; private set; }
    public VerificationImpactState State { get; private set; }

    /// <summary>
    /// Set for requirement-driven items: the approved requirement change that raised the work. Items are
    /// raised at approval, when no requirement revision exists yet — revisions are created only when a
    /// baseline is materialised — so the change is the durable anchor.
    /// </summary>
    public Guid? RequirementChangeId { get; private set; }
    /// <summary>
    /// Bound once the target baseline is materialised and the exact revision exists, so coverage can be
    /// checked against the precise revision the release will carry.
    /// </summary>
    public Guid? RequirementRevisionId { get; private set; }
    /// <summary>Set for orphaned-procedure items.</summary>
    public Guid? ProcedureId { get; private set; }
    /// <summary>
    /// The baseline whose reopening stranded this procedure, or null when a retirement did.
    ///
    /// Reopening un-materializes the revisions a build created, so a requirement that build introduced ceases
    /// to exist and anything written against it is left covering nothing. That is the same state a retirement
    /// produces and it is the same work, but it was not the change request that decided it -- so the act that
    /// did is recorded here rather than left for somebody to infer from the fact that the change request it
    /// names has been withdrawn.
    /// </summary>
    public Guid? CausingBaselineId { get; private set; }
    /// <summary>Human-readable identifier of whichever subject the item concerns.</summary>
    public string SubjectDisplayNumber { get; private set; } = "";
    /// <summary>What the requirement author declared. Context for the decision, never the decision itself.</summary>
    public string DeclaredVerificationMethod { get; private set; } = "";

    public string? AssignedEngineerId { get; private set; }
    public string? AssignedByLeadId { get; private set; }
    public DateTimeOffset? AssignedAt { get; private set; }

    public VerificationImpactOutcome? Outcome { get; private set; }
    public TestProcedureChangeAction? ProcedureChangeAction { get; private set; }
    public bool PreReleaseEvidenceRequired { get; private set; }
    /// <summary>The procedure named when coverage was confirmed; the exact link is bound at materialisation.</summary>
    public Guid? ResolvedProcedureId { get; private set; }
    /// <summary>The immutable approved procedure revision selected by the decision.</summary>
    public Guid? ResolvedProcedureRevisionId { get; private set; }
    /// <summary>The requirement revision a retargeted procedure now covers.</summary>
    public Guid? RetargetedRequirementRevisionId { get; private set; }
    public string ResolutionRationale { get; private set; } = "";
    public string? ResolvedBy { get; private set; }
    public DateTimeOffset? ResolvedAt { get; private set; }

    public DateTimeOffset RaisedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public long Version { get; private set; } = 1;

    /// <summary>An item counts against the baseline-approval gate until it is resolved.</summary>
    public bool BlocksBaselineApproval => State is not (VerificationImpactState.Resolved or VerificationImpactState.Superseded);

    /// <summary>The test lead distributes work to an individual verification engineer.</summary>
    public void AssignToEngineer(string leadActorId, string engineerId, DateTimeOffset now)
    {
        EnsureUnresolved();
        AssignedByLeadId = Required(leadActorId, "assigning test lead");
        AssignedEngineerId = Required(engineerId, "assigned verification engineer");
        AssignedAt = now;
        State = VerificationImpactState.Assigned;
        Touch(now);
    }

    /// <summary>
    /// Records the verification engineer's judgement. A rationale is always required: this record is the
    /// evidence that a qualified person decided what the change means for verification.
    ///
    /// Confirming procedure coverage must name the procedure. The exact coverage link cannot be created
    /// yet — it binds a requirement revision, and revisions exist only once the baseline is materialised —
    /// so naming the procedure keeps the claim checkable instead of leaving it as prose.
    /// </summary>
    public void Resolve(string actorId, VerificationImpactOutcome outcome, string rationale, DateTimeOffset now,
        Guid? procedureId = null, Guid? procedureRevisionId = null,
        TestProcedureChangeAction? procedureChangeAction = null, bool preReleaseEvidenceRequired = false,
        Guid? retargetedRequirementRevisionId = null)
    {
        EnsureUnresolved();
        if (!Enum.IsDefined(outcome)) throw new DomainException("An unknown verification outcome cannot be recorded.");
        if (!IsOutcomeValidForTrigger(outcome))
            throw new DomainException($"{outcome} does not apply to a {Trigger} item.");
        if (outcome == VerificationImpactOutcome.ProcedureCoverageConfirmed && (procedureId is null || procedureId == Guid.Empty))
            throw new DomainException("Confirming coverage requires the approved procedure that covers the requirement.");
        if (outcome == VerificationImpactOutcome.ProcedureCoverageConfirmed
            && (procedureRevisionId is null || procedureRevisionId == Guid.Empty))
            throw new DomainException("Confirming coverage requires the exact approved procedure revision.");
        if (outcome is not (VerificationImpactOutcome.ProcedureCoverageConfirmed or VerificationImpactOutcome.ProcedureRetargeted)
            && (procedureId is not null || procedureRevisionId is not null))
            throw new DomainException("Only confirmed coverage names a procedure.");
        // Retargeting is the one decision that names a requirement rather than a procedure: the procedure is
        // already known — it is the stranded one this item was raised about — and what the engineer is
        // deciding is which requirement it now answers for.
        if (outcome == VerificationImpactOutcome.ProcedureRetargeted
            && (retargetedRequirementRevisionId is null || retargetedRequirementRevisionId == Guid.Empty))
            throw new DomainException("Moving a procedure requires the requirement revision it now covers.");
        if (outcome != VerificationImpactOutcome.ProcedureRetargeted && retargetedRequirementRevisionId is not null)
            throw new DomainException("Only a retargeted procedure names the requirement it moves to.");
        var action = procedureChangeAction ?? (outcome switch
        {
            VerificationImpactOutcome.NoTestRequired => TestProcedureChangeAction.NoTestRequired,
            // Deciding that a procedure has to be written is the CreateNew action by definition; defaulting it
            // to LinkExisting would record the item as pointing at a procedure that does not exist.
            VerificationImpactOutcome.NewProcedureRequired => TestProcedureChangeAction.CreateNew,
            _ => TestProcedureChangeAction.LinkExisting,
        });
        if (outcome == VerificationImpactOutcome.NoTestRequired && action != TestProcedureChangeAction.NoTestRequired)
            throw new DomainException("A no-test decision must use the no-test-required action.");
        if (outcome == VerificationImpactOutcome.NewProcedureRequired && action != TestProcedureChangeAction.CreateNew)
            throw new DomainException("Deciding that a new procedure is required must use the create-new action.");
        if (outcome != VerificationImpactOutcome.ProcedureCoverageConfirmed && preReleaseEvidenceRequired)
            throw new DomainException("Pre-release evidence can only be required for a selected test procedure.");
        ResolvedProcedureId = procedureId;
        ResolvedProcedureRevisionId = procedureRevisionId;
        RetargetedRequirementRevisionId = retargetedRequirementRevisionId;
        Outcome = outcome;
        ProcedureChangeAction = action;
        // A procedure that covers a requirement introduced or modified by this build is not optional
        // regression scope. The caller may still require evidence for other decisions, but cannot turn it
        // off for changed-requirement coverage.
        PreReleaseEvidenceRequired = preReleaseEvidenceRequired
            || (Trigger is VerificationImpactTrigger.RequirementIntroduced or VerificationImpactTrigger.RequirementModified
                && outcome == VerificationImpactOutcome.ProcedureCoverageConfirmed);
        ResolutionRationale = Required(rationale, "resolution rationale");
        ResolvedBy = Required(actorId, "resolving verification engineer");
        ResolvedAt = now;
        State = VerificationImpactState.Resolved;
        Touch(now);
    }

    /// <summary>
    /// The procedure this item asked for now exists and is approved, so the decision becomes coverage.
    ///
    /// Without this an engineer would answer the item twice: once to say a procedure must be written, and
    /// again after writing it to say the procedure covers the requirement. The second answer carries no
    /// judgement the first did not — the decision was already made, and what changed is that the work it
    /// named got done. Recording it automatically is also what keeps the causal chain intact: the procedure
    /// is here *because* this item asked for it.
    ///
    /// Deliberately narrow. It only advances an item that is awaiting a new procedure, and it names the exact
    /// approved revision, so nothing else can be quietly converted into coverage.
    /// </summary>
    public bool SettleWithApprovedProcedure(Guid procedureId, Guid procedureRevisionId, DateTimeOffset now)
    {
        if (!AwaitsNewProcedure) return false;
        if (procedureId == Guid.Empty || procedureRevisionId == Guid.Empty)
            throw new DomainException("Settling a new-procedure decision requires the exact approved procedure revision.");
        Outcome = VerificationImpactOutcome.ProcedureCoverageConfirmed;
        ProcedureChangeAction = TestProcedureChangeAction.CreateNew;
        ResolvedProcedureId = procedureId;
        ResolvedProcedureRevisionId = procedureRevisionId;
        ResolutionRationale = $"{ResolutionRationale} Settled when the requested procedure was approved.".Trim();
        Touch(now);
        return true;
    }

    /// <summary>Withdraws the current decision without erasing its separately persisted history.</summary>
    public void Reopen(string actorId, string rationale, DateTimeOffset now)
    {
        if (State != VerificationImpactState.Resolved)
            throw new DomainException("Only a resolved verification impact item can be reopened.");
        Required(actorId, "verification engineer reopening the decision");
        Required(rationale, "reopen rationale");
        Outcome = null;
        ProcedureChangeAction = null;
        PreReleaseEvidenceRequired = false;
        ResolvedProcedureId = null;
        ResolvedProcedureRevisionId = null;
        ResolutionRationale = "";
        ResolvedBy = null;
        ResolvedAt = null;
        State = AssignedEngineerId is null ? VerificationImpactState.Open : VerificationImpactState.Assigned;
        Touch(now);
    }

    /// <summary>
    /// Binds the item to the exact requirement revision once the target baseline materialises it. Until
    /// then the item is anchored to its approved requirement change.
    /// </summary>
    public void LinkRequirementRevision(Guid requirementRevisionId, DateTimeOffset now)
    {
        if (Trigger == VerificationImpactTrigger.ProcedureOrphaned)
            throw new DomainException("An orphaned-procedure item does not describe a requirement revision.");
        if (requirementRevisionId == Guid.Empty) throw new DomainException("An exact requirement revision is required.");
        if (RequirementRevisionId == requirementRevisionId) return;
        RequirementRevisionId = requirementRevisionId;
        Touch(now);
    }

    /// <summary>
    /// Follows the change request when it is deferred or retargeted, so verification work stays attached to
    /// the release that will actually carry the change.
    /// </summary>
    public void Retarget(Guid releaseId, DateTimeOffset now)
    {
        if (releaseId == Guid.Empty) throw new DomainException("A verification impact item requires its target release.");
        if (releaseId == ReleaseId) return;
        ReleaseId = releaseId;
        Touch(now);
    }

    /// <summary>
    /// Moves the item to another test change request without recreating it, so its identity, originating
    /// change request, assignment, decision and history survive the move.
    ///
    /// Used when a manual package or a fold takes over a source change's pending automatic assessment: the
    /// work must stay actionable from the package that now claims the change, never stranded behind a
    /// superseded assessment.
    /// </summary>
    public void MoveToReview(Guid testChangeReviewId, DateTimeOffset now)
    {
        if (testChangeReviewId == Guid.Empty)
            throw new DomainException("A verification impact item must move to a test change request.");
        if (testChangeReviewId == TestChangeReviewId) return;
        TestChangeReviewId = testChangeReviewId;
        Touch(now);
    }

    public void Supersede(DateTimeOffset now)
    {
        if (State == VerificationImpactState.Superseded) return;
        State = VerificationImpactState.Superseded;
        Touch(now);
    }

    private bool IsOutcomeValidForTrigger(VerificationImpactOutcome outcome) => Trigger switch
    {
        VerificationImpactTrigger.ProcedureOrphaned =>
            outcome is VerificationImpactOutcome.ProcedureRetired or VerificationImpactOutcome.ProcedureRetained
                or VerificationImpactOutcome.ProcedureRetargeted,
        // A new or modified requirement can also owe a procedure nobody has written yet. An orphaned procedure
        // cannot: that item exists because a procedure already exists and has lost what it covered.
        _ => outcome is VerificationImpactOutcome.ProcedureCoverageConfirmed or VerificationImpactOutcome.NoTestRequired
            or VerificationImpactOutcome.NewProcedureRequired
    };

    /// <summary>
    /// True when the decision is an answer but not yet coverage.
    ///
    /// The distinction the release gates need: the item has been decided, so it no longer reads as work nobody
    /// has looked at, but nothing verifies the requirement yet and the coverage gate must keep holding.
    /// </summary>
    public bool AwaitsNewProcedure =>
        State == VerificationImpactState.Resolved && Outcome == VerificationImpactOutcome.NewProcedureRequired;

    private void EnsureUnresolved()
    {
        if (State is VerificationImpactState.Resolved or VerificationImpactState.Superseded)
            throw new DomainException("A resolved or superseded verification impact item cannot be changed. Raise a new item instead.");
    }

    private void Touch(DateTimeOffset now) { UpdatedAt = now; Version++; }

    private static string Required(string value, string name) =>
        string.IsNullOrWhiteSpace(value) ? throw new DomainException($"A {name} is required.") : value.Trim();
}

/// <summary>An immutable audit entry for a verification-impact decision or its withdrawal.</summary>
public sealed class VerificationImpactDecisionHistory
{
    private VerificationImpactDecisionHistory() { }

    public VerificationImpactDecisionHistory(Guid itemId, VerificationImpactHistoryAction action,
        VerificationImpactOutcome? outcome, Guid? procedureId, Guid? procedureRevisionId,
        string rationale, string actorId, DateTimeOffset occurredAt)
    {
        if (itemId == Guid.Empty) throw new DomainException("Verification-impact history requires its item.");
        if (!Enum.IsDefined(action)) throw new DomainException("Verification-impact history requires a known action.");
        Id = Guid.NewGuid();
        VerificationImpactItemId = itemId;
        Action = action;
        Outcome = outcome;
        ProcedureId = procedureId;
        ProcedureRevisionId = procedureRevisionId;
        Rationale = string.IsNullOrWhiteSpace(rationale)
            ? throw new DomainException("Verification-impact history requires a rationale.")
            : rationale.Trim();
        ActorId = string.IsNullOrWhiteSpace(actorId)
            ? throw new DomainException("Verification-impact history requires an actor.")
            : actorId.Trim();
        OccurredAt = occurredAt;
    }

    public Guid Id { get; private set; }
    public Guid VerificationImpactItemId { get; private set; }
    public VerificationImpactHistoryAction Action { get; private set; }
    public VerificationImpactOutcome? Outcome { get; private set; }
    public Guid? ProcedureId { get; private set; }
    public Guid? ProcedureRevisionId { get; private set; }
    public string Rationale { get; private set; } = "";
    public string ActorId { get; private set; } = "";
    public DateTimeOffset OccurredAt { get; private set; }
}
