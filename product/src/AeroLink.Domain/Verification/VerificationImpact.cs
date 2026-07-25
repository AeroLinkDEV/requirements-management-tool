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

public enum VerificationImpactState { Open, Assigned, Resolved }

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
    /// <summary>The orphaned procedure is no longer needed and has been retired.</summary>
    ProcedureRetired,
    /// <summary>The orphaned procedure is deliberately kept despite covering no current requirement.</summary>
    ProcedureRetained
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

    private VerificationImpactItem(Guid projectId, Guid releaseId, Guid changeRequestId,
        VerificationImpactTrigger trigger, DateTimeOffset now)
    {
        if (projectId == Guid.Empty) throw new DomainException("A verification impact item requires its Project.");
        if (releaseId == Guid.Empty) throw new DomainException("A verification impact item requires its target release.");
        if (changeRequestId == Guid.Empty) throw new DomainException("A verification impact item requires its originating change request.");
        Id = Guid.NewGuid();
        ProjectId = projectId;
        ReleaseId = releaseId;
        ChangeRequestId = changeRequestId;
        Trigger = trigger;
        State = VerificationImpactState.Open;
        RaisedAt = now;
        UpdatedAt = now;
    }

    public static VerificationImpactItem ForIntroducedRequirement(Guid projectId, Guid releaseId, Guid changeRequestId,
        Guid requirementChangeId, string requirementDisplayNumber, string declaredVerificationMethod, DateTimeOffset now)
        => ForRequirement(projectId, releaseId, changeRequestId, VerificationImpactTrigger.RequirementIntroduced,
            requirementChangeId, requirementDisplayNumber, declaredVerificationMethod, now);

    public static VerificationImpactItem ForModifiedRequirement(Guid projectId, Guid releaseId, Guid changeRequestId,
        Guid requirementChangeId, string requirementDisplayNumber, string declaredVerificationMethod, DateTimeOffset now)
        => ForRequirement(projectId, releaseId, changeRequestId, VerificationImpactTrigger.RequirementModified,
            requirementChangeId, requirementDisplayNumber, declaredVerificationMethod, now);

    public static VerificationImpactItem ForOrphanedProcedure(Guid projectId, Guid releaseId, Guid changeRequestId,
        Guid procedureId, string procedureDisplayNumber, DateTimeOffset now)
    {
        if (procedureId == Guid.Empty) throw new DomainException("An orphaned-procedure item requires its procedure.");
        return new VerificationImpactItem(projectId, releaseId, changeRequestId, VerificationImpactTrigger.ProcedureOrphaned, now)
        {
            ProcedureId = procedureId,
            SubjectDisplayNumber = Required(procedureDisplayNumber, "procedure identifier")
        };
    }

    private static VerificationImpactItem ForRequirement(Guid projectId, Guid releaseId, Guid changeRequestId,
        VerificationImpactTrigger trigger, Guid requirementChangeId, string requirementDisplayNumber,
        string declaredVerificationMethod, DateTimeOffset now)
    {
        if (requirementChangeId == Guid.Empty) throw new DomainException("A requirement item requires its approved requirement change.");
        return new VerificationImpactItem(projectId, releaseId, changeRequestId, trigger, now)
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
    /// <summary>Human-readable identifier of whichever subject the item concerns.</summary>
    public string SubjectDisplayNumber { get; private set; } = "";
    /// <summary>What the requirement author declared. Context for the decision, never the decision itself.</summary>
    public string DeclaredVerificationMethod { get; private set; } = "";

    public string? AssignedEngineerId { get; private set; }
    public string? AssignedByLeadId { get; private set; }
    public DateTimeOffset? AssignedAt { get; private set; }

    public VerificationImpactOutcome? Outcome { get; private set; }
    /// <summary>The procedure named when coverage was confirmed; the exact link is bound at materialisation.</summary>
    public Guid? ResolvedProcedureId { get; private set; }
    public string ResolutionRationale { get; private set; } = "";
    public string? ResolvedBy { get; private set; }
    public DateTimeOffset? ResolvedAt { get; private set; }

    public DateTimeOffset RaisedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public long Version { get; private set; } = 1;

    /// <summary>An item counts against the baseline-approval gate until it is resolved.</summary>
    public bool BlocksBaselineApproval => State != VerificationImpactState.Resolved;

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
        Guid? procedureId = null)
    {
        EnsureUnresolved();
        if (!Enum.IsDefined(outcome)) throw new DomainException("An unknown verification outcome cannot be recorded.");
        if (!IsOutcomeValidForTrigger(outcome))
            throw new DomainException($"{outcome} does not apply to a {Trigger} item.");
        if (outcome == VerificationImpactOutcome.ProcedureCoverageConfirmed && (procedureId is null || procedureId == Guid.Empty))
            throw new DomainException("Confirming coverage requires the approved procedure that covers the requirement.");
        if (outcome != VerificationImpactOutcome.ProcedureCoverageConfirmed && procedureId is not null)
            throw new DomainException("Only confirmed coverage names a procedure.");
        ResolvedProcedureId = procedureId;
        Outcome = outcome;
        ResolutionRationale = Required(rationale, "resolution rationale");
        ResolvedBy = Required(actorId, "resolving verification engineer");
        ResolvedAt = now;
        State = VerificationImpactState.Resolved;
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

    private bool IsOutcomeValidForTrigger(VerificationImpactOutcome outcome) => Trigger switch
    {
        VerificationImpactTrigger.ProcedureOrphaned =>
            outcome is VerificationImpactOutcome.ProcedureRetired or VerificationImpactOutcome.ProcedureRetained,
        _ => outcome is VerificationImpactOutcome.ProcedureCoverageConfirmed or VerificationImpactOutcome.NoTestRequired
    };

    private void EnsureUnresolved()
    {
        if (State == VerificationImpactState.Resolved)
            throw new DomainException("A resolved verification impact item cannot be changed. Raise a new item instead.");
    }

    private void Touch(DateTimeOffset now) { UpdatedAt = now; Version++; }

    private static string Required(string value, string name) =>
        string.IsNullOrWhiteSpace(value) ? throw new DomainException($"A {name} is required.") : value.Trim();
}
