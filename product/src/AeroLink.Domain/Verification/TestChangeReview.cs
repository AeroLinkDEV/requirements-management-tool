using AeroLink.Domain.Common;

namespace AeroLink.Domain.Verification;

/// <summary>The independently governed test-procedure discipline affected by an approved engineering change.</summary>
public enum TestChangeReviewDiscipline { System, HighLevelSoftware, LowLevelSoftware }
public enum TestChangeReviewState { Open, InReview, Approved, Superseded }

/// <summary>
/// What the test assessment of an approved change concluded.
///
/// The same three answers the requirements disciplines give, because it is the same question asked of a
/// different discipline: an approved change either needs test-procedure work or it does not, and until
/// somebody says which, it needs assessing.
/// </summary>
public enum TestChangeReviewOutcome { Pending, ChangeRequired, NoChangeRequired }

/// <summary>
/// A controlled package of test-procedure decisions raised from one approved change request.
///
/// Software HLR and LLR work is deliberately separated. A software change touching both levels therefore
/// creates two reviews, allowing different engineers and approvers to finish them independently.
/// </summary>
public sealed class TestChangeReview
{
    private readonly List<TestChangeRequestClaim> _additionalSources = [];
    private readonly List<TestProcedureChange> _procedureChanges = [];

    private TestChangeReview() { }

    public TestChangeReview(Guid projectId, Guid releaseId, Guid changeRequestId,
        TestChangeReviewDiscipline discipline, string sourceChangeRequestNumber, DateTimeOffset now,
        string baseNumber = "", int revision = 0)
    {
        Revision = revision;
        if (projectId == Guid.Empty) throw new DomainException("A test change review requires its Project.");
        if (releaseId == Guid.Empty) throw new DomainException("A test change review requires its software build.");
        if (changeRequestId == Guid.Empty) throw new DomainException("A test change review requires its originating change request.");
        if (!Enum.IsDefined(discipline)) throw new DomainException("A test change review requires a known discipline.");
        Id = Guid.NewGuid();
        ProjectId = projectId;
        ReleaseId = releaseId;
        ChangeRequestId = changeRequestId;
        Discipline = discipline;
        SourceChangeRequestNumber = Required(sourceChangeRequestNumber, "source change request number");
        // Empty remains readable for databases created before controlled TCR numbering. The showcase
        // upgrade assigns those rows a real number without changing their identity or evidence.
        BaseNumber = baseNumber.Trim();
        State = TestChangeReviewState.Open;
        CreatedAt = now;
        UpdatedAt = now;
    }

    public Guid Id { get; private set; }
    public Guid ProjectId { get; private set; }
    public Guid ReleaseId { get; private set; }
    public Guid ChangeRequestId { get; private set; }
    public TestChangeReviewDiscipline Discipline { get; private set; }
    public string SourceChangeRequestNumber { get; private set; } = "";
    /// <summary>Its controlled number — SYSTCR, HLRTCR or LLRTCR — empty only for rows raised before it had one.</summary>
    public string BaseNumber { get; private set; } = "";
    /// <summary>Advances when an approved package is reopened for further test work against the same change.</summary>
    public int Revision { get; private set; }
    public string DisplayNumber => string.IsNullOrEmpty(BaseNumber) ? SourceChangeRequestNumber : $"{BaseNumber}.{Revision:D2}";
    /// <summary>
    /// Change requests folded into this one beyond the change request it was raised from.
    ///
    /// The default is one change request to one test change request. Sometimes two changes are sensibly
    /// tested as a single package, and the engineer building it says so here rather than raising a second
    /// package that duplicates the first one's procedures.
    ///
    /// A change request belongs to at most one test change request — enforced by a unique index, not only by
    /// this type — because "is the test work for this change covered?" has to have exactly one answer. Two
    /// packages both claiming a change could be approved with contradictory procedure decisions and nothing
    /// would notice.
    /// </summary>
    public IReadOnlyCollection<TestChangeRequestClaim> AdditionalSources => _additionalSources.AsReadOnly();
    public TestChangeReviewState State { get; private set; }
    /// <summary>Whether the assessment has been performed, and what it found.</summary>
    public TestChangeReviewOutcome Outcome { get; private set; }
    /// <summary>Why no test-procedure work is required. Recorded only with that conclusion.</summary>
    public string NoChangeRationale { get; private set; } = "";
    public string? DecidedBy { get; private set; }
    public DateTimeOffset? DecidedAt { get; private set; }
    public string? AssignedEngineerId { get; private set; }
    public string? SubmittedBy { get; private set; }
    public string? SelectedApproverId { get; private set; }
    public DateTimeOffset? SubmittedAt { get; private set; }
    public string? ApprovedBy { get; private set; }
    public DateTimeOffset? ApprovedAt { get; private set; }
    public string ApprovalRationale { get; private set; } = "";
    public Guid? SupersededByTestChangeRequestId { get; private set; }
    public string SupersededReason { get; private set; } = "";
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public long Version { get; private set; } = 1;

    /// <summary>
    /// Records that the change needs test-procedure work — and only then is there a test change request.
    ///
    /// The number is what makes this a controlled SYSTCR, HLRTCR or LLRTCR, so it is not allocated until an
    /// assessment says one is needed. Numbering at the moment the change was approved produced a controlled
    /// record for every change before anybody had looked at whether it touched a single procedure.
    /// </summary>
    public void RecordTestChangeRequired(string actorId, DateTimeOffset now)
    {
        EnsureOpen();
        Outcome = TestChangeReviewOutcome.ChangeRequired;
        NoChangeRationale = "";
        DecidedBy = Required(actorId, "assessing verification engineer");
        DecidedAt = now;
        Touch(now);
    }

    /// <summary>
    /// Records that the change needs no test-procedure work.
    ///
    /// This conclusion produces nothing, so nothing downstream would ever examine it — which is why it is the
    /// one that goes for approval. Its counterpart becomes a test change request whose procedure decisions
    /// are reviewed on their own terms.
    /// </summary>
    public void RecordNoTestChangeRequired(string actorId, string rationale, DateTimeOffset now)
    {
        EnsureOpen();
        if (!string.IsNullOrEmpty(BaseNumber))
            throw new DomainException("This is already a controlled test change request. Withdraw its decisions before concluding that no test work is required.");
        Outcome = TestChangeReviewOutcome.NoChangeRequired;
        NoChangeRationale = Required(rationale, "no-change rationale");
        DecidedBy = Required(actorId, "assessing verification engineer");
        DecidedAt = now;
        Touch(now);
    }

    /// <summary>
    /// What this test change request proposes to do to the procedures, as a requirement change request
    /// proposes changes to requirements.
    /// </summary>
    public IReadOnlyCollection<TestProcedureChange> ProcedureChanges => _procedureChanges.AsReadOnly();

    /// <summary>
    /// Adds a proposed procedure change.
    ///
    /// Only while the package is open and only once it is a controlled test change request — an assessment
    /// that has not concluded test work is required has nothing to propose, and an in-review package must not
    /// grow underneath the person approving it. Both rules are the requirement side's, unchanged.
    /// </summary>
    public TestProcedureChange AddProcedureChange(string actorId, TestProcedureChangeDraft draft, DateTimeOffset now)
    {
        EnsureOpen();
        Required(actorId, "authoring verification engineer");
        if (Outcome != TestChangeReviewOutcome.ChangeRequired)
            throw new DomainException("Record that test-procedure work is required before proposing changes to procedures.");
        if (draft.Level != ProcedureLevel())
            throw new DomainException($"A {Discipline} test change request can contain {ProcedureLevel()} procedures only.");
        if (_procedureChanges.Any(x => x.BaseNumber == draft.BaseNumber))
            throw new DomainException($"{draft.BaseNumber} already has a proposed change in this test change request.");
        var change = new TestProcedureChange(Id, draft.BaseNumber, draft.Revision, draft.Level, draft.Kind,
            draft.Title, draft.Objective, draft.Preconditions, draft.Steps, draft.ExpectedResult, draft.Rationale,
            draft.DrivingRequirementRevisionIdsJson);
        _procedureChanges.Add(change);
        Touch(now);
        return change;
    }

    public void RemoveProcedureChange(Guid changeId, DateTimeOffset now)
    {
        EnsureOpen();
        var change = _procedureChanges.SingleOrDefault(x => x.Id == changeId)
            ?? throw new DomainException("That procedure change is not part of this test change request.");
        _procedureChanges.Remove(change);
        Touch(now);
    }

    /// <summary>The procedure level this discipline governs. The discipline and the level are one fact.</summary>
    public TestProcedureLevel ProcedureLevel() => Discipline switch
    {
        TestChangeReviewDiscipline.System => TestProcedureLevel.System,
        TestChangeReviewDiscipline.HighLevelSoftware => TestProcedureLevel.HighLevel,
        _ => TestProcedureLevel.LowLevel,
    };

    public void AssignControlledNumber(string baseNumber, DateTimeOffset now)
    {
        if (!string.IsNullOrEmpty(BaseNumber)) return;
        if (Outcome != TestChangeReviewOutcome.ChangeRequired)
            throw new DomainException("Record that test-procedure work is required before raising the test change request that carries it.");
        var number = Required(baseNumber, "controlled test change request number");
        var expectedPrefix = Discipline switch
        {
            TestChangeReviewDiscipline.System => "SYSTCR-",
            TestChangeReviewDiscipline.HighLevelSoftware => "HLRTCR-",
            _ => "LLRTCR-",
        };
        if (!number.StartsWith(expectedPrefix, StringComparison.OrdinalIgnoreCase))
            throw new DomainException($"A {Discipline} test change request requires a {expectedPrefix.TrimEnd('-')} number.");
        BaseNumber = number;
        Touch(now);
    }

    public void Assign(string leadActorId, string engineerId, DateTimeOffset now)
    {
        EnsureOpen();
        Required(leadActorId, "assigning test lead");
        AssignedEngineerId = Required(engineerId, "assigned verification engineer");
        Touch(now);
    }

    public void Submit(string actorId, string approverId, bool everyItemResolved, DateTimeOffset now)
    {
        EnsureOpen();
        if (Outcome == TestChangeReviewOutcome.Pending)
            throw new DomainException("Assess the change before sending it for review.");
        if (!everyItemResolved)
            throw new DomainException("Every test-procedure decision must be completed before review.");
        // "Concluded work is required, names none" is refused at the endpoint rather than here, and that is a
        // deliberate split. Every route a person can take passes through the endpoint. What does not is the
        // reconstruction of history: Build 1.5's packages were approved before procedure decisions existed,
        // and the honest record of them is empty. Enforcing it here would force the showcase to invent the
        // decisions those approvals never carried, which is a worse falsehood than the gap.
        SubmittedBy = Required(actorId, "submitting verification engineer");
        SelectedApproverId = Required(approverId, "selected test change request approver");
        if (string.Equals(SelectedApproverId, SubmittedBy, StringComparison.OrdinalIgnoreCase))
            throw new DomainException("The test change request approver must be independent from its submitting engineer.");
        SubmittedAt = now;
        State = TestChangeReviewState.InReview;
        Touch(now);
    }

    public void Approve(string actorId, string rationale, DateTimeOffset now)
    {
        if (State != TestChangeReviewState.InReview)
            throw new DomainException("Only a submitted test change review can be approved.");
        var approver = Required(actorId, "approver");
        if (!string.Equals(approver, SelectedApproverId, StringComparison.OrdinalIgnoreCase))
            throw new DomainException("Only the explicitly selected test change request approver can approve it.");
        // One person holding TestLead could otherwise submit a package of test-procedure decisions and approve
        // it themselves, which makes the approval a formality rather than an independent judgement.
        if (string.Equals(approver, SubmittedBy, StringComparison.OrdinalIgnoreCase))
            throw new DomainException("A test change review cannot be approved by the engineer who submitted it.");
        ApprovedBy = approver;
        ApprovalRationale = Required(rationale, "approval rationale");
        ApprovedAt = now;
        State = TestChangeReviewState.Approved;
        Touch(now);
    }

    public void ReturnToWork(string actorId, string rationale, DateTimeOffset now)
    {
        if (State != TestChangeReviewState.InReview)
            throw new DomainException("Only a submitted test change review can be returned.");
        Required(actorId, "reviewer");
        Required(rationale, "return rationale");
        State = TestChangeReviewState.Open;
        SubmittedBy = null;
        SelectedApproverId = null;
        SubmittedAt = null;
        Touch(now);
    }

    /// <summary>
    /// Folds another change request's test work into this package.
    ///
    /// Whole change requests, not individual requirement changes: an engineer takes on a change's test work
    /// or they do not, and splitting one across two packages would leave "is this change covered?" with a
    /// partial answer that neither package could give.
    ///
    /// Only while the package is still open. Once it has been submitted the reviewer is judging a fixed set
    /// of decisions, and quietly widening what they are approving is the one thing an approval must not allow.
    /// </summary>
    public void IncludeChangeRequest(string actorId, Guid changeRequestId, string changeRequestNumber, DateTimeOffset now)
    {
        EnsureOpen();
        Required(actorId, "including verification engineer");
        if (changeRequestId == Guid.Empty) throw new DomainException("A change request is required.");
        if (changeRequestId == ChangeRequestId)
            throw new DomainException("This package was raised from that change request and already covers it.");
        if (_additionalSources.Any(x => x.ChangeRequestId == changeRequestId))
            throw new DomainException("That change request is already part of this package.");
        _additionalSources.Add(new TestChangeRequestClaim(Id, changeRequestId,
            Required(changeRequestNumber, "change request number"), actorId, now));
        Touch(now);
    }

    /// <summary>Takes a folded-in change request back out, releasing it for another package to claim.</summary>
    public void ExcludeChangeRequest(Guid changeRequestId, DateTimeOffset now)
    {
        EnsureOpen();
        var claim = _additionalSources.SingleOrDefault(x => x.ChangeRequestId == changeRequestId)
            ?? throw new DomainException("That change request is not part of this package.");
        _additionalSources.Remove(claim);
        Touch(now);
    }

    /// <summary>Every change request this package answers for, including the one it was raised from.</summary>
    public IEnumerable<Guid> CoveredChangeRequestIds =>
        new[] { ChangeRequestId }.Concat(_additionalSources.Select(x => x.ChangeRequestId));

    /// <summary>
    /// Advances an approved test change request to its next revision, as an approved change request advances
    /// to its own.
    ///
    /// The successor is a new record at the same number and the next revision, carrying the same procedure
    /// changes so the engineer corrects them rather than retyping them. It starts Open and already concluded
    /// that test work is required — reopening approved procedure work to revise it is not a reason to ask
    /// again whether any was needed.
    ///
    /// The predecessor is left for the caller to supersede, exactly as the requirements side leaves the
    /// predecessor for its caller to persist: this method builds the successor and nothing else.
    /// </summary>
    public TestChangeReview StartNextRevision(string actorId, DateTimeOffset now, bool targetReleaseIsReleased)
    {
        Required(actorId, "engineer revising the test change request");
        if (State != TestChangeReviewState.Approved)
            throw new DomainException("Only an approved test change request can advance to its next revision.");
        if (targetReleaseIsReleased)
            throw new DomainException(
                "This test change request is incorporated in a released build and cannot be revised. Raise a new one against the in-work build.");
        // A change request is claimed by at most one package, enforced by a unique index, so a successor
        // cannot hold what its predecessor still holds — and silently dropping the folded-in changes would
        // make the new revision cover less than the old one without saying so. Which package owns a claim
        // across a revision is a real question and it is not answered yet, so this refuses instead of
        // guessing.
        if (_additionalSources.Count != 0)
            throw new DomainException(
                "This test change request has other change requests folded into it. Revising one of those is not supported yet.");
        var next = new TestChangeReview(ProjectId, ReleaseId, ChangeRequestId, Discipline,
            SourceChangeRequestNumber, now, BaseNumber, Revision + 1);
        next.RecordTestChangeRequired(actorId, now);
        foreach (var change in _procedureChanges)
            next.AddProcedureChange(actorId, new TestProcedureChangeDraft(change.BaseNumber, change.Revision,
                change.Level, change.Kind, change.Title, change.Objective, change.Preconditions, change.Steps,
                change.ExpectedResult, change.Rationale, change.DrivingRequirementRevisionIdsJson), now);
        return next;
    }

    public void Retarget(Guid releaseId, DateTimeOffset now)
    {
        EnsureOpen();
        if (releaseId == Guid.Empty) throw new DomainException("A test change review requires its software build.");
        if (releaseId == ReleaseId) return;
        ReleaseId = releaseId;
        Touch(now);
    }

    /// <summary>
    /// Keeps this package as historical evidence while making it impossible to mistake for current work.
    /// A successor engineering-change revision requires a fresh package and fresh decisions.
    /// </summary>
    public void Supersede(Guid successorTestChangeRequestId, string reason, DateTimeOffset now)
    {
        if (State == TestChangeReviewState.Superseded) return;
        if (successorTestChangeRequestId == Guid.Empty || successorTestChangeRequestId == Id)
            throw new DomainException("A different successor test change request is required.");
        SupersededByTestChangeRequestId = successorTestChangeRequestId;
        SupersededReason = Required(reason, "supersession reason");
        State = TestChangeReviewState.Superseded;
        Touch(now);
    }

    private void EnsureOpen()
    {
        if (State != TestChangeReviewState.Open)
            throw new DomainException("An in-review or approved test change review cannot be edited.");
    }

    private void Touch(DateTimeOffset now) { UpdatedAt = now; Version++; }
    private static string Required(string value, string name) =>
        string.IsNullOrWhiteSpace(value) ? throw new DomainException($"A {name} is required.") : value.Trim();
}

/// <summary>
/// One change request folded into a test change request beyond the one it was raised from.
///
/// Recorded as its own row rather than as a list on the package, so the database can hold the rule that
/// matters: a unique index on the change request means two packages cannot both claim it, whatever order
/// two engineers happen to act in.
///
/// It carries the change request's number as well as its identity because the number is what a reader sees,
/// and resolving it would otherwise mean a join on every render of a list that exists to be scanned quickly.
/// </summary>
public sealed class TestChangeRequestClaim
{
    private TestChangeRequestClaim() { }

    public TestChangeRequestClaim(Guid testChangeReviewId, Guid changeRequestId, string changeRequestNumber,
        string claimedBy, DateTimeOffset now)
    {
        Id = Guid.NewGuid();
        TestChangeReviewId = testChangeReviewId;
        ChangeRequestId = changeRequestId;
        ChangeRequestNumber = changeRequestNumber;
        ClaimedBy = claimedBy;
        ClaimedAt = now;
    }

    public Guid Id { get; private set; }
    public Guid TestChangeReviewId { get; private set; }
    public Guid ChangeRequestId { get; private set; }
    public string ChangeRequestNumber { get; private set; } = "";
    public string ClaimedBy { get; private set; } = "";
    public DateTimeOffset ClaimedAt { get; private set; }
}
