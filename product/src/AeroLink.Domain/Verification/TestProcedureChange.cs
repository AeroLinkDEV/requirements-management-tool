using AeroLink.Domain.Common;

namespace AeroLink.Domain.Verification;

/// <summary>
/// What is being done to a procedure. The same three acts a requirement change proposes.
/// </summary>
public enum TestProcedureChangeKind { Introduce, Modify, Retire }

public sealed record TestProcedureChangeDraft(string BaseNumber, int Revision, TestProcedureLevel Level,
    TestProcedureChangeKind Kind, string Objective, string Preconditions, string Steps, string ExpectedResult,
    string Rationale, string DrivingRequirementRevisionIdsJson = "[]");

/// <summary>
/// One proposed change to one test procedure, carried by a test change request.
///
/// The counterpart of <see cref="AeroLink.Domain.ChangeControl.RequirementChange"/>, and deliberately its
/// mirror: test procedures are authored, reviewed, approved and aligned to a build exactly as requirements
/// are, so the record that proposes a change to one is shaped like the record that proposes a change to the
/// other. Where the requirement change carries a statement, this carries the procedure body; where that one
/// names the upstream revisions a requirement is proposed to satisfy, this names the requirement revisions
/// that drive the procedure.
///
/// A proposal, not a revision. Nothing here is a controlled procedure revision until the test change request
/// carrying it is approved and materialised into a build — which is the same rule, and the same reason, that
/// governs a requirement change.
/// </summary>
public sealed class TestProcedureChange
{
    private TestProcedureChange() { }

    internal TestProcedureChange(Guid testChangeReviewId, string baseNumber, int revision,
        TestProcedureLevel level, TestProcedureChangeKind kind, string objective, string preconditions,
        string steps, string expectedResult, string rationale,
        string drivingRequirementRevisionIdsJson = "[]")
    {
        Id = Guid.NewGuid();
        TestChangeReviewId = testChangeReviewId;
        BaseNumber = ArtifactNumber.ValidateBase(baseNumber);
        Revision = revision;
        Level = level;
        Kind = kind;
        // A retirement removes a procedure rather than restating it, so it is the one kind that needs no
        // body — exactly as a retired requirement needs no statement.
        if (kind != TestProcedureChangeKind.Retire && string.IsNullOrWhiteSpace(objective))
            throw new DomainException("A test procedure objective is required.");
        if (kind != TestProcedureChangeKind.Retire && string.IsNullOrWhiteSpace(steps))
            throw new DomainException("A test procedure must state its steps.");
        Objective = objective?.Trim() ?? "";
        Preconditions = preconditions?.Trim() ?? "";
        Steps = steps?.Trim() ?? "";
        ExpectedResult = expectedResult?.Trim() ?? "";
        Rationale = rationale?.Trim() ?? "";
        DrivingRequirementRevisionIdsJson = string.IsNullOrWhiteSpace(drivingRequirementRevisionIdsJson)
            ? "[]"
            : drivingRequirementRevisionIdsJson;
    }

    public Guid Id { get; private set; }
    public Guid TestChangeReviewId { get; private set; }
    public string BaseNumber { get; private set; } = string.Empty;
    public int Revision { get; private set; }
    public string DisplayNumber => ArtifactNumber.Display(BaseNumber, Revision);
    public TestProcedureLevel Level { get; private set; }
    public TestProcedureChangeKind Kind { get; private set; }

    public string Objective { get; private set; } = string.Empty;
    public string Preconditions { get; private set; } = string.Empty;
    public string Steps { get; private set; } = string.Empty;
    public string ExpectedResult { get; private set; } = string.Empty;
    /// <summary>Why this procedure work is required, in the author's words.</summary>
    public string Rationale { get; private set; } = string.Empty;

    /// <summary>
    /// The requirement revisions that drive this procedure.
    ///
    /// A requirement drives a procedure; the procedure does not, by existing, verify the requirement.
    /// Verification is what an execution of the procedure establishes, and only for the run that happened —
    /// so this link is named for what it actually is. Calling it "verifies" would let a requirement read as
    /// verified the moment somebody wrote a procedure against it, which is the same class of unearned claim
    /// as an imported baseline appearing to have been approved here.
    ///
    /// Held as proposed revision identifiers rather than as links, because a proposal is not a link: it
    /// becomes a real <see cref="TestRequirementCoverage"/> only when the test change request is approved and
    /// materialised. That is the distinction the requirement side draws between a proposed upstream revision
    /// and an approved trace.
    /// </summary>
    public string DrivingRequirementRevisionIdsJson { get; private set; } = "[]";
}
