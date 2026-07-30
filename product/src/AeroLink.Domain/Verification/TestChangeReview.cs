using AeroLink.Domain.Common;

namespace AeroLink.Domain.Verification;

/// <summary>The independently governed test-procedure discipline affected by an approved engineering change.</summary>
public enum TestChangeReviewDiscipline { System, HighLevelSoftware, LowLevelSoftware }
public enum TestChangeReviewState { Open, InReview, Approved }

/// <summary>
/// A controlled package of test-procedure decisions raised from one approved change request.
///
/// Software HLR and LLR work is deliberately separated. A software change touching both levels therefore
/// creates two reviews, allowing different engineers and approvers to finish them independently.
/// </summary>
public sealed class TestChangeReview
{
    private readonly List<TestChangeRequestClaim> _additionalSources = [];

    private TestChangeReview() { }

    public TestChangeReview(Guid projectId, Guid releaseId, Guid changeRequestId,
        TestChangeReviewDiscipline discipline, string sourceChangeRequestNumber, DateTimeOffset now,
        string baseNumber = "")
    {
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
        // Empty is allowed: rows raised before this had a controlled number keep answering by the change
        // request they came from, rather than being retrospectively given a number they never had.
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
    public string? AssignedEngineerId { get; private set; }
    public string? SubmittedBy { get; private set; }
    public DateTimeOffset? SubmittedAt { get; private set; }
    public string? ApprovedBy { get; private set; }
    public DateTimeOffset? ApprovedAt { get; private set; }
    public string ApprovalRationale { get; private set; } = "";
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public long Version { get; private set; } = 1;

    public void Assign(string leadActorId, string engineerId, DateTimeOffset now)
    {
        EnsureOpen();
        Required(leadActorId, "assigning test lead");
        AssignedEngineerId = Required(engineerId, "assigned verification engineer");
        Touch(now);
    }

    public void Submit(string actorId, bool everyItemResolved, DateTimeOffset now)
    {
        EnsureOpen();
        if (!everyItemResolved)
            throw new DomainException("Every test-procedure decision must be completed before review.");
        SubmittedBy = Required(actorId, "submitting verification engineer");
        SubmittedAt = now;
        State = TestChangeReviewState.InReview;
        Touch(now);
    }

    public void Approve(string actorId, string rationale, DateTimeOffset now)
    {
        if (State != TestChangeReviewState.InReview)
            throw new DomainException("Only a submitted test change review can be approved.");
        var approver = Required(actorId, "approver");
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

    public void Retarget(Guid releaseId, DateTimeOffset now)
    {
        EnsureOpen();
        if (releaseId == Guid.Empty) throw new DomainException("A test change review requires its software build.");
        if (releaseId == ReleaseId) return;
        ReleaseId = releaseId;
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
