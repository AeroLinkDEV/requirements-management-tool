using AeroLink.Domain.Common;
using AeroLink.Domain.Hierarchy;

namespace AeroLink.Domain.Verification;

public enum TestProcedureState { Draft, Approved, Retired }
public enum TestOutcome { Pass, Fail, Blocked }
public enum TestProcedureLevel { System, HighLevel, LowLevel }

public sealed class TestProcedure
{
    private TestProcedure() { }
    public TestProcedure(Guid projectId, string baseNumber, string title, string ownerId, DateTimeOffset now,
        TestProcedureLevel level = TestProcedureLevel.HighLevel, ILadderPolicy? policy = null)
    {
        if (string.IsNullOrWhiteSpace(title)) throw new DomainException("A test procedure title is required.");
        Id = Guid.NewGuid(); ProjectId = projectId; BaseNumber = ArtifactNumber.ValidateBase(baseNumber);
        EnsurePrefixMatchesLevel(BaseNumber, level, policy);
        Title = title.Trim(); OwnerId = ownerId.Trim(); CreatedAt = now; UpdatedAt = now; Level = level;
    }

    /// <summary>
    /// A procedure's number and its level are one fact, so they are not allowed to disagree.
    ///
    /// The allocator picks SYSTP, HLRTP or LLRTP <em>from</em> the level, so a SYSTP numbered procedure that
    /// says it is HighLevel did not come from there — it came from a caller that left the level to its default.
    /// That is not a cosmetic mislabelling: the level decides which requirements the procedure may verify and
    /// which discipline answers for it when a retirement strands it, so a wrong one puts real work in the wrong
    /// team's queue. Checked here because this is the only place a procedure comes into existence.
    /// </summary>
    private static void EnsurePrefixMatchesLevel(string baseNumber, TestProcedureLevel level, ILadderPolicy? policy = null)
    {
        var ladderPolicy = policy ?? LegacyLadderPolicy.Instance;
        var expected = ladderPolicy.TestProcedurePrefix(level) + "-";
        if (baseNumber.StartsWith(expected, StringComparison.OrdinalIgnoreCase)) return;
        // Only a number claiming to be a test procedure is judged. A project numbering its procedures some
        // other way is not making this mistake, and is not this rule's business.
        if (!ladderPolicy.IsKnownTestProcedurePrefix(baseNumber)) return;
        throw new DomainException(
            $"{baseNumber} is numbered for a different level than {level}. A test procedure's number and its level have to agree.");
    }
    public Guid Id { get; private set; }
    public Guid ProjectId { get; private set; }
    public string BaseNumber { get; private set; } = string.Empty;
    public string Title { get; private set; } = string.Empty;
    public string OwnerId { get; private set; } = string.Empty;
    public TestProcedureLevel Level { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public long Version { get; private set; } = 1;

    public void UpdateDraft(string title, string ownerId, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(title)) throw new DomainException("A test procedure title is required.");
        if (string.IsNullOrWhiteSpace(ownerId)) throw new DomainException("A test procedure owner is required.");
        Title = title.Trim(); OwnerId = ownerId.Trim(); UpdatedAt = now;
    }
}

public sealed class TestProcedureRevision
{
    private TestProcedureRevision() { }
    public TestProcedureRevision(Guid procedureId, int revision, string objective, string preconditions,
        string steps, string expectedResult, TestProcedureState state, string authorId, DateTimeOffset now,
        string? selectedApproverId = null, Guid? sourceTestChangeRequestId = null,
        Guid? effectiveBaselineId = null, string sourceChangeRequestsJson = "[]")
    {
        if (revision < 0) throw new DomainException("Test procedure revision cannot be negative.");
        // A retired procedure is being removed, not restated — the same exemption a retired requirement revision
        // gets, so a retirement does not have to repeat the body of the thing it is withdrawing.
        if (state != TestProcedureState.Retired
            && (string.IsNullOrWhiteSpace(objective) || string.IsNullOrWhiteSpace(steps) || string.IsNullOrWhiteSpace(expectedResult)))
            throw new DomainException("Objective, steps, and expected result are required.");
        Id = Guid.NewGuid(); ProcedureId = procedureId; Revision = revision; Objective = objective.Trim();
        Preconditions = preconditions.Trim(); Steps = steps.Trim(); ExpectedResult = expectedResult.Trim();
        State = state; AuthorId = authorId.Trim(); SelectedApproverId = selectedApproverId?.Trim(); CreatedAt = now;
        SourceTestChangeRequestId = sourceTestChangeRequestId; EffectiveBaselineId = effectiveBaselineId;
        var sourceSnapshot = string.IsNullOrWhiteSpace(sourceChangeRequestsJson)
            ? "[]"
            : sourceChangeRequestsJson.Trim();
        try { using var parsed = System.Text.Json.JsonDocument.Parse(sourceSnapshot); }
        catch (System.Text.Json.JsonException)
        { throw new DomainException("Test procedure source-change provenance must be valid JSON."); }
        SourceChangeRequestsJson = sourceSnapshot;
    }
    public Guid Id { get; private set; }
    public Guid ProcedureId { get; private set; }
    public int Revision { get; private set; }
    public string Objective { get; private set; } = string.Empty;
    public string Preconditions { get; private set; } = string.Empty;
    public string Steps { get; private set; } = string.Empty;
    public string ExpectedResult { get; private set; } = string.Empty;
    public TestProcedureState State { get; private set; }
    public string AuthorId { get; private set; } = string.Empty;
    public string? SelectedApproverId { get; private set; }
    /// <summary>
    /// The test change request that produced this revision, as a requirement revision names its change request.
    ///
    /// Null for a revision authored before test-procedure change was controlled. Those revisions exist and are
    /// real, so the honest record is "nobody knows which package approved this" rather than an invented one.
    /// </summary>
    public Guid? SourceTestChangeRequestId { get; private set; }
    /// <summary>The baseline this revision first became effective in. Null for the same legacy reason.</summary>
    public Guid? EffectiveBaselineId { get; private set; }
    /// <summary>Exact source-CR identities captured from the producing TCR revision.</summary>
    public string SourceChangeRequestsJson { get; private set; } = "[]";
    public DateTimeOffset CreatedAt { get; private set; }

    public void UpdateDraft(string objective, string preconditions, string steps, string expectedResult, string actor)
    {
        if (State != TestProcedureState.Draft) throw new DomainException("Only a Draft test procedure revision can be edited.");
        if (string.IsNullOrWhiteSpace(objective) || string.IsNullOrWhiteSpace(steps) || string.IsNullOrWhiteSpace(expectedResult))
            throw new DomainException("Objective, steps, and expected result are required.");
        if (string.IsNullOrWhiteSpace(actor)) throw new DomainException("A test procedure update actor is required.");
        Objective = objective.Trim(); Preconditions = preconditions.Trim(); Steps = steps.Trim(); ExpectedResult = expectedResult.Trim();
    }

    // No Approve here. A procedure revision is approved by the test change request that carries it, and
    // materialisation writes it as Approved on that authority — signing the revision separately would be a
    // second approval of the same work. The method that did it had one caller, a route now deleted, and a
    // capability nothing calls is not a capability.
}

/// <summary>
/// Exact active procedure-revision membership of one materialized baseline.
///
/// The test-procedure twin of <c>BaselineRequirementSelection</c>: which revision of which procedure a build
/// carries. Without it a build could say precisely which requirements it holds and only approximately which
/// procedures verify them.
/// </summary>
public sealed class BaselineTestProcedureSelection
{
    private BaselineTestProcedureSelection() { }
    public BaselineTestProcedureSelection(Guid baselineId, Guid procedureId, Guid revisionId)
    { Id = Guid.NewGuid(); BaselineId = baselineId; ProcedureId = procedureId; RevisionId = revisionId; }
    public Guid Id { get; private set; }
    public Guid BaselineId { get; private set; }
    public Guid ProcedureId { get; private set; }
    public Guid RevisionId { get; private set; }
}

/// <summary>
/// Binds an exact procedure revision to an exact requirement revision.
///
/// A link may be <see cref="IsSuspect"/>: carried forward onto a revision whose requirement changed, so the
/// procedure was written against earlier wording and its continued validity is unproven. Suspect coverage
/// must never be counted as verified — it is the difference between "a procedure is attached" and "someone
/// competent confirmed the procedure still tests this requirement".
/// </summary>
public sealed class TestRequirementCoverage
{
    private TestRequirementCoverage() { }
    public TestRequirementCoverage(Guid procedureRevisionId, Guid requirementRevisionId)
    { Id = Guid.NewGuid(); ProcedureRevisionId = procedureRevisionId; RequirementRevisionId = requirementRevisionId; }

    /// <summary>Creates coverage carried forward from a predecessor revision, marked suspect until reviewed.</summary>
    public static TestRequirementCoverage CarriedForward(Guid procedureRevisionId, Guid requirementRevisionId,
        string reason, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(reason)) throw new DomainException("Carried-forward coverage requires a reason.");
        var coverage = new TestRequirementCoverage(procedureRevisionId, requirementRevisionId);
        coverage.IsSuspect = true;
        coverage.SuspectReason = reason.Trim();
        coverage.SuspectSince = now;
        return coverage;
    }

    /// <summary>Copies the exact decision state when a procedure revision retains an unchanged link.</summary>
    public static TestRequirementCoverage RetainedByProcedureRevision(Guid procedureRevisionId,
        TestRequirementCoverage predecessor)
    {
        var coverage = new TestRequirementCoverage(procedureRevisionId, predecessor.RequirementRevisionId)
        {
            IsSuspect = predecessor.IsSuspect,
            SuspectReason = predecessor.SuspectReason,
            SuspectSince = predecessor.SuspectSince,
            ConfirmedBy = predecessor.ConfirmedBy,
            ConfirmedAt = predecessor.ConfirmedAt
        };
        return coverage;
    }

    public Guid Id { get; private set; }
    public Guid ProcedureRevisionId { get; private set; }
    public Guid RequirementRevisionId { get; private set; }
    public bool IsSuspect { get; private set; }
    public string SuspectReason { get; private set; } = "";
    public DateTimeOffset? SuspectSince { get; private set; }
    public string? ConfirmedBy { get; private set; }
    public DateTimeOffset? ConfirmedAt { get; private set; }

    /// <summary>A verification engineer confirms the procedure still verifies the changed requirement.</summary>
    public void ConfirmStillValid(string actorId, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(actorId)) throw new DomainException("A confirming verification engineer is required.");
        if (!IsSuspect) return;
        IsSuspect = false;
        SuspectReason = "";
        SuspectSince = null;
        ConfirmedBy = actorId.Trim();
        ConfirmedAt = now;
    }

    /// <summary>Returns a withdrawn applicability decision to suspect without erasing the link or its history.</summary>
    public void MarkSuspect(string reason, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(reason)) throw new DomainException("Suspect coverage requires a reason.");
        IsSuspect = true;
        SuspectReason = reason.Trim();
        SuspectSince = now;
        ConfirmedBy = null;
        ConfirmedAt = null;
    }

    // There is deliberately no method for marking an existing link suspect. A requirement changing under a
    // procedure produces a new revision, and materialisation creates a fresh carried-forward link already
    // marked suspect rather than mutating the old one, which must stay exactly as it was approved. A
    // procedure changing under a requirement is caught by the coverage gate, which refuses to count a link
    // whose procedure has any revision still in draft or review. Nothing is left for this to do, and an
    // unreachable method is a claim nothing keeps.
}

public sealed class TestExecution
{
    private TestExecution() { }
    public TestExecution(Guid projectId, Guid procedureRevisionId, Guid? softwareBuildId, Guid? retestOfExecutionId,
        TestOutcome outcome, string executedBy, string configuration, string determination, string evidenceReference,
        DateTimeOffset executedAt, DateTimeOffset recordedAt, Guid? releaseId = null)
    {
        if (string.IsNullOrWhiteSpace(executedBy)) throw new DomainException("The person making the result determination is required.");
        if (string.IsNullOrWhiteSpace(determination)) throw new DomainException("A human result determination is required.");
        if (outcome != TestOutcome.Blocked && string.IsNullOrWhiteSpace(evidenceReference))
            throw new DomainException("Pass and Fail results require an evidence reference.");
        Id = Guid.NewGuid(); ProjectId = projectId; ReleaseId = releaseId; ProcedureRevisionId = procedureRevisionId;
        SoftwareBuildId = softwareBuildId; RetestOfExecutionId = retestOfExecutionId; Outcome = outcome;
        ExecutedBy = executedBy.Trim(); Configuration = configuration.Trim(); Determination = determination.Trim();
        EvidenceReference = evidenceReference.Trim(); ExecutedAt = executedAt; RecordedAt = recordedAt;
    }
    public Guid Id { get; private set; }
    public Guid ProjectId { get; private set; }
    public Guid? ReleaseId { get; private set; }
    public Guid ProcedureRevisionId { get; private set; }
    public Guid? SoftwareBuildId { get; private set; }
    public Guid? RetestOfExecutionId { get; private set; }
    public TestOutcome Outcome { get; private set; }
    public string ExecutedBy { get; private set; } = string.Empty;
    public string Configuration { get; private set; } = string.Empty;
    public string Determination { get; private set; } = string.Empty;
    public string EvidenceReference { get; private set; } = string.Empty;
    public DateTimeOffset ExecutedAt { get; private set; }
    public DateTimeOffset RecordedAt { get; private set; }
}
