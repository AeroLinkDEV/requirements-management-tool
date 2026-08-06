using AeroLink.Domain.Common;
using AeroLink.Domain.Content;

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
    private readonly List<ChangeControl.ReviewCycle> _reviewCycles = [];

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
    /// <summary>
    /// The reviews this package has been through, using the same mechanism a change request uses.
    ///
    /// Shared rather than mirrored: one implementation of snapshot hashing, staged approval, substitution and
    /// signature, so a correction to how review works reaches both disciplines. What differs between them is
    /// the workflow — how many stages, which authority signs each — and that is data, not code.
    /// </summary>
    public IReadOnlyCollection<ChangeControl.ReviewCycle> ReviewCycles => _reviewCycles.AsReadOnly();
    public TestChangeReviewState State { get; private set; }
    /// <summary>
    /// The case this package argues, in the same three parts a change request argues its own.
    ///
    /// A test change request is a controlled proposal, and a proposal that lists procedure edits without saying
    /// why is a work order rather than a case for review. An approver signing one is answering the same
    /// question their counterpart answers on the requirements side — is this the right change, for a reason
    /// that holds — so it is asked in the same words.
    ///
    /// Empty on packages raised before the fields existed, and on those raised automatically by an approved
    /// change request, which have not been written up by anybody yet.
    /// </summary>
    public string Title { get; private set; } = "";
    public string Problem { get; private set; } = "";
    public string Analysis { get; private set; } = "";
    public string Solution { get; private set; } = "";
    public string ProblemRich { get; private set; } = RichContent.Empty;
    public string AnalysisRich { get; private set; } = RichContent.Empty;
    public string SolutionRich { get; private set; } = RichContent.Empty;

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

    /// <summary>
    /// Writes the case this package argues. The counterpart of a change request's own draft edit, and open for
    /// the same window: while the package is still being worked, and not once an approver is holding it.
    /// </summary>
    public void WriteCase(string actorId, string title, string problem, string analysis, string solution,
        DateTimeOffset now, string? problemRich = null, string? analysisRich = null, string? solutionRich = null)
    {
        EnsureOpen();
        Required(actorId, "authoring verification engineer");
        if (string.IsNullOrWhiteSpace(title)) throw new DomainException("A test change request title is required.");
        Title = title.Trim();
        (Problem, ProblemRich) = Resolve(problem, problemRich);
        (Analysis, AnalysisRich) = Resolve(analysis, analysisRich);
        (Solution, SolutionRich) = Resolve(solution, solutionRich);
        Touch(now);

        static (string Plain, string Rich) Resolve(string plain, string? rich)
        {
            if (string.IsNullOrWhiteSpace(rich)) return (plain?.Trim() ?? "", RichContent.FromPlainText(plain ?? ""));
            var canonical = RichContent.Canonicalize(rich);
            return (RichContent.ToPlainText(canonical), canonical);
        }
    }

    /// <summary>The cycle currently deciding this package, if one is.</summary>
    public ChangeControl.ReviewCycle? ActiveReviewCycle =>
        _reviewCycles.LastOrDefault(x => x.State == ChangeControl.ReviewCycleState.Active);

    /// <summary>
    /// Sends the package to its review board, running the same staged review a change request runs.
    ///
    /// The single named approver this used to take is a review board of one that nobody could configure. Now
    /// the stages come from the project's recorded procedure for this discipline, so a program can require
    /// three signatures on a requirement change and one on the test work that follows it — and either way the
    /// review is snapshot-hashed, ordered and signed by the same code.
    /// </summary>
    public ChangeControl.ReviewCycle SubmitForReview(string actorId,
        IReadOnlyList<ChangeControl.ApproverSelection> approvers, bool everyItemResolved, DateTimeOffset now,
        ChangeControl.ReviewMode mode = ChangeControl.ReviewMode.Sequential,
        ChangeControl.ReviewWorkflowSpecification? workflow = null)
    {
        EnsureOpen();
        if (Outcome == TestChangeReviewOutcome.Pending)
            throw new DomainException("Assess the change before sending it for review.");
        if (!everyItemResolved)
            throw new DomainException("Every test-procedure decision must be completed before review.");
        if (approvers.Any(x => string.Equals(x.UserId, actorId, StringComparison.OrdinalIgnoreCase)))
            throw new DomainException("The test change request approver must be independent from its submitting engineer.");
        var cycle = ChangeControl.ReviewCycle.ForTestChangeRequest(Id, _reviewCycles.Count + 1,
            ComputeSnapshotHash(), approvers, now, mode, workflow);
        _reviewCycles.Add(cycle);
        SubmittedBy = Required(actorId, "submitting verification engineer");
        SelectedApproverId = approvers[0].UserId;
        SubmittedAt = now;
        State = TestChangeReviewState.InReview;
        Touch(now);
        return cycle;
    }

    /// <summary>Records one stage's approval. The package is approved when the last stage is.</summary>
    public void ApproveActiveStage(string actorId, string rationale, DateTimeOffset now)
    {
        if (State != TestChangeReviewState.InReview)
            throw new DomainException("Only a submitted test change request can be approved.");
        var cycle = ActiveReviewCycle ?? throw new DomainException("This test change request has no active review.");
        if (cycle.Approve(actorId, now))
        {
            ApprovedBy = Required(actorId, "approving reviewer");
            ApprovalRationale = Required(rationale, "approval rationale");
            ApprovedAt = now;
            State = TestChangeReviewState.Approved;
        }
        Touch(now);
    }

    /// <summary>
    /// What the review is of, fixed at submission.
    ///
    /// The same purpose the change request's own snapshot serves: an approval has to be provably of an exact
    /// set of decisions, so that editing the package afterwards cannot quietly reuse the signature.
    /// </summary>
    private string ComputeSnapshotHash()
    {
        var manifest = string.Join("|", DisplayNumber, Title, Problem, Analysis, Solution,
            string.Join(";", _procedureChanges.OrderBy(x => x.BaseNumber)
                .Select(x => $"{x.DisplayNumber}:{x.Kind}:{x.Title}:{x.Objective}:{x.Steps}:{x.ExpectedResult}")));
        return Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(manifest)))
            .ToLowerInvariant();
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

    /// <summary>
    /// Sends the package to a single named approver.
    ///
    /// Delegates rather than duplicating: this is <see cref="SubmitForReview"/> with a review board of one and
    /// no recorded procedure, which is what every submission was before boards could be configured. Two
    /// implementations of "submit" would be two things to keep in step, and the reason this one still exists is
    /// that it reads better at the call sites that genuinely have one approver and no workflow.
    /// </summary>
    public void Submit(string actorId, string approverId, bool everyItemResolved, DateTimeOffset now)
    {
        Required(approverId, "selected test change request approver");
        SubmitForReview(actorId, [new ChangeControl.ApproverSelection(approverId, approverId)],
            everyItemResolved, now);
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
