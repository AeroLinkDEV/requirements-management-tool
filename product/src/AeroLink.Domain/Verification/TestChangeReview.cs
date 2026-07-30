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
    private TestChangeReview() { }

    public TestChangeReview(Guid projectId, Guid releaseId, Guid changeRequestId,
        TestChangeReviewDiscipline discipline, string sourceChangeRequestNumber, DateTimeOffset now)
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
