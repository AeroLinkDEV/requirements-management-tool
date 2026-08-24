using AeroLink.Domain.Common;
using AeroLink.Domain.Hierarchy;

namespace AeroLink.Domain.Verification;

public enum TestProcedureState { Draft, Approved, Retired }
public enum TestOutcome { Pass, Fail, Blocked }
public enum TestProcedureLevel { System, HighLevel, LowLevel }

public sealed class TestProcedure : IVerificationArtifactHeader
{
    private TestProcedure() { }
    public TestProcedure(Guid projectId, string baseNumber, string title, string ownerId, DateTimeOffset now,
        TestProcedureLevel level = TestProcedureLevel.HighLevel, ILadderPolicy? policy = null,
        VerificationArtifactKind? artifactKind = null,
        VerificationProcedureParentKind parentKind = VerificationProcedureParentKind.Unspecified)
    {
        var artifactWord = artifactKind == VerificationArtifactKind.Procedure ? "test procedure" : "test case";
        if (string.IsNullOrWhiteSpace(title)) throw new DomainException($"A {artifactWord} title is required.");
        Id = Guid.NewGuid(); ProjectId = projectId; BaseNumber = ArtifactNumber.ValidateBase(baseNumber);
        Title = title.Trim(); OwnerId = ownerId.Trim(); CreatedAt = now; UpdatedAt = now; Level = level;
        ArtifactDiscipline = level switch
        {
            TestProcedureLevel.System => VerificationDiscipline.System,
            TestProcedureLevel.HighLevel => VerificationDiscipline.HighLevelSoftware,
            TestProcedureLevel.LowLevel => VerificationDiscipline.LowLevelSoftware,
            _ => throw new DomainException($"Unknown verification artifact level: {level}.")
        };
        ArtifactKind = artifactKind ?? (level == TestProcedureLevel.System
            ? VerificationArtifactKind.Procedure
            : VerificationArtifactKind.Case);
        var expectedKind = level == TestProcedureLevel.System
            ? VerificationArtifactKind.Procedure
            : ArtifactKind;
        if (level == TestProcedureLevel.System && ArtifactKind != expectedKind)
            throw new DomainException($"{level} verification artifacts must use {expectedKind}.");
        EnsurePrefixMatchesIdentity(BaseNumber, level, ArtifactKind, policy);
        if (level != TestProcedureLevel.System && ArtifactKind == VerificationArtifactKind.Procedure
            && parentKind == VerificationProcedureParentKind.Unspecified)
            throw new DomainException("A software Procedure requires an explicit Allocated or Derived parent classification.");
        _ = VerificationArtifactVocabulary.Definition(ArtifactKey);
    }

    /// <summary>
    /// Seeds a genuinely pre-#722 software Case while an upgrade qualification is still on the predecessor
    /// schema. This is intentionally internal: new Case allocation must never reuse the retired Procedure
    /// spelling, but the migration fixture must be able to represent the row that #722 relabels.
    /// </summary>
    internal static TestProcedure LegacySoftwareCaseForMigration(Guid projectId, string legacyBaseNumber,
        string title, string ownerId, DateTimeOffset now, TestProcedureLevel level)
    {
        var current = new TestProcedure(projectId,
            level == TestProcedureLevel.HighLevel ? "HLRTC-999999" : "LLRTC-999999",
            title, ownerId, now, level);
        current.BaseNumber = ArtifactNumber.ValidateBase(legacyBaseNumber);
        return current;
    }

    /// <summary>
    /// A procedure's number and its level are one fact, so they are not allowed to disagree.
    ///
    /// The allocator picks SYSTP, HLRTC or LLRTC <em>from</em> the level, so a SYSTP numbered procedure that
    /// says it is HighLevel did not come from there — it came from a caller that left the level to its default.
    /// That is not a cosmetic mislabelling: the level decides which requirements the procedure may verify and
    /// which discipline answers for it when a retirement strands it, so a wrong one puts real work in the wrong
    /// team's queue. Checked here because this is the only place a procedure comes into existence.
    /// </summary>
    private static void EnsurePrefixMatchesIdentity(string baseNumber, TestProcedureLevel level,
        VerificationArtifactKind artifactKind, ILadderPolicy? policy = null)
    {
        var ladderPolicy = policy ?? LegacyLadderPolicy.Instance;
        var discipline = level switch
        {
            TestProcedureLevel.System => VerificationDiscipline.System,
            TestProcedureLevel.HighLevel => VerificationDiscipline.HighLevelSoftware,
            TestProcedureLevel.LowLevel => VerificationDiscipline.LowLevelSoftware,
            _ => throw new DomainException($"Unknown verification artifact level: {level}.")
        };
        // A software profile can enable both Case and Procedure. A Case uses its exact configured key rather
        // than the profile's executable key. Dormant Procedure identities remain valid before profile
        // activation, so their globally governed vocabulary prefix does not require the policy to enable them.
        var key = new VerificationArtifactKey(discipline, artifactKind);
        var expectedPrefix = artifactKind == VerificationArtifactKind.Procedure
            ? VerificationArtifactVocabulary.Definition(key).ArtifactPrefix
            : ladderPolicy.ArtifactPrefix(key);
        var expected = expectedPrefix + "-";
        if (baseNumber.StartsWith(expected, StringComparison.OrdinalIgnoreCase)) return;
        var artifactWord = artifactKind == VerificationArtifactKind.Procedure ? "test procedure" : "test case";
        throw new DomainException(
            $"{baseNumber} is not a valid {artifactWord} identifier for {level}; expected the {expectedPrefix} family.");
    }
    public Guid Id { get; private set; }
    public Guid ProjectId { get; private set; }
    public string BaseNumber { get; private set; } = string.Empty;
    public string Title { get; private set; } = string.Empty;
    public string OwnerId { get; private set; } = string.Empty;
    public TestProcedureLevel Level { get; private set; }
    /// <summary>Persisted neutral identity; legacy level and number remain unchanged compatibility projections.</summary>
    public VerificationDiscipline ArtifactDiscipline { get; private set; }
    public VerificationArtifactKind ArtifactKind { get; private set; }
    public VerificationArtifactKey ArtifactKey => new(ArtifactDiscipline, ArtifactKind);
    public Guid ArtifactId => Id;
    public string Identity => BaseNumber;
    public VerificationArtifactHeader Header => new(Id, ProjectId, ArtifactKey, BaseNumber, Title, OwnerId);
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public long Version { get; private set; } = 1;

    public void UpdateDraft(string title, string ownerId, DateTimeOffset now)
    {
        var artifactWord = ArtifactKind == VerificationArtifactKind.Case ? "test case" : "test procedure";
        if (string.IsNullOrWhiteSpace(title)) throw new DomainException($"A {artifactWord} title is required.");
        if (string.IsNullOrWhiteSpace(ownerId)) throw new DomainException($"A {artifactWord} owner is required.");
        Title = title.Trim(); OwnerId = ownerId.Trim(); UpdatedAt = now;
    }
}

public sealed class TestProcedureRevision
{
    private TestProcedureRevision() { }
    public TestProcedureRevision(Guid procedureId, int revision, string objective, string preconditions,
        string steps, string expectedResult, TestProcedureState state, string authorId, DateTimeOffset now,
        string? selectedApproverId = null, Guid? sourceTestChangeRequestId = null,
        Guid? effectiveBaselineId = null, string sourceChangeRequestsJson = "[]",
        string? environmentSetup = null, string? testData = null, string? orderedSteps = null,
        string? expectedObservations = null, string? cleanup = null, string? toolingAutomation = null,
        VerificationProcedureParentKind parentKind = VerificationProcedureParentKind.Unspecified,
        string? derivedRationale = null, string? retirementRationale = null)
    {
        if (revision < 0) throw new DomainException("Verification artifact revision cannot be negative.");
        // A retired procedure is being removed, not restated — the same exemption a retired requirement revision
        // gets, so a retirement does not have to repeat the body of the thing it is withdrawing.
        if (state != TestProcedureState.Retired
            && (string.IsNullOrWhiteSpace(objective) || string.IsNullOrWhiteSpace(steps) || string.IsNullOrWhiteSpace(expectedResult)))
            throw new DomainException("Objective, steps, and expected result are required.");
        Id = Guid.NewGuid(); ProcedureId = procedureId; Revision = revision; Objective = objective.Trim();
        Preconditions = preconditions.Trim(); Steps = steps.Trim(); ExpectedResult = expectedResult.Trim();
        EnvironmentSetup = environmentSetup?.Trim() ?? "";
        TestData = testData?.Trim() ?? "";
        OrderedSteps = orderedSteps?.Trim() ?? "";
        ExpectedObservations = expectedObservations?.Trim() ?? "";
        Cleanup = cleanup?.Trim() ?? "";
        ToolingAutomation = toolingAutomation?.Trim() ?? "";
        ParentKind = parentKind;
        DerivedRationale = derivedRationale?.Trim() ?? "";
        RetirementRationale = retirementRationale?.Trim() ?? "";
        State = state; AuthorId = authorId.Trim(); SelectedApproverId = selectedApproverId?.Trim(); CreatedAt = now;
        SourceTestChangeRequestId = sourceTestChangeRequestId; EffectiveBaselineId = effectiveBaselineId;
        var sourceSnapshot = string.IsNullOrWhiteSpace(sourceChangeRequestsJson)
            ? "[]"
            : sourceChangeRequestsJson.Trim();
        try { using var parsed = System.Text.Json.JsonDocument.Parse(sourceSnapshot); }
        catch (System.Text.Json.JsonException)
        { throw new DomainException("Verification artifact source-change provenance must be valid JSON."); }
        SourceChangeRequestsJson = sourceSnapshot;
    }
    public Guid Id { get; private set; }
    public Guid ProcedureId { get; private set; }
    public int Revision { get; private set; }
    public string Objective { get; private set; } = string.Empty;
    public string Preconditions { get; private set; } = string.Empty;
    public string Steps { get; private set; } = string.Empty;
    public string ExpectedResult { get; private set; } = string.Empty;
    /// <summary>Procedure-only content; Case revisions continue to use the four legacy body fields above.</summary>
    public string EnvironmentSetup { get; private set; } = string.Empty;
    public string TestData { get; private set; } = string.Empty;
    public string OrderedSteps { get; private set; } = string.Empty;
    public string ExpectedObservations { get; private set; } = string.Empty;
    public string Cleanup { get; private set; } = string.Empty;
    public string ToolingAutomation { get; private set; } = string.Empty;
    public string Setup => EnvironmentSetup;
    public string ExecutableSteps => OrderedSteps;
    public string ExpectedObservationsText => ExpectedObservations;
    public string Tooling => ToolingAutomation;
    /// <summary>Exact parent classification for software Procedures and Case/System verification revisions.</summary>
    public VerificationProcedureParentKind ParentKind { get; private set; }
    public string DerivedRationale { get; private set; } = string.Empty;
    public string RetirementRationale { get; private set; } = string.Empty;
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

    /// <summary>Typed compatibility content; kind is always derived from the owning artifact identity.</summary>
    public VerificationArtifactRevisionHeader RevisionHeader(TestProcedure owner)
    {
        var artifactKey = EnsureOwner(owner).ArtifactKey;
        return new(Id, ProcedureId, artifactKey.Kind, Revision,
            State switch
            {
                TestProcedureState.Draft => VerificationArtifactLifecycleState.Draft,
                TestProcedureState.Approved => VerificationArtifactLifecycleState.Active,
                TestProcedureState.Retired => VerificationArtifactLifecycleState.Retired,
                _ => throw new DomainException($"Unknown verification artifact state: {State}.")
            }, AuthorId, SourceTestChangeRequestId, EffectiveBaselineId, CreatedAt);
    }
    public VerificationArtifactRevisionProvenance RevisionProvenance =>
        new(SourceTestChangeRequestId, EffectiveBaselineId, SourceChangeRequestsJson);
    public IVerificationArtifactRevisionContent Content(TestProcedure owner) => EnsureOwner(owner).ArtifactKind == VerificationArtifactKind.Case
            ? new VerificationCaseRevisionContent(Objective, Preconditions, Steps, ExpectedResult)
            : new VerificationProcedureRevisionContent(Objective, Preconditions, Steps, ExpectedResult,
                EnvironmentSetup, TestData, OrderedSteps, ExpectedObservations, Cleanup, ToolingAutomation);

    public void ValidateProcedureParents(TestProcedure owner, IEnumerable<Guid>? caseRevisionIds = null) {
        if (owner.ArtifactKind != VerificationArtifactKind.Procedure || owner.Level == TestProcedureLevel.System
            || State == TestProcedureState.Retired) return;
        VerificationProcedureParentPolicy.Validate(ParentKind, caseRevisionIds, DerivedRationale);
    }

    private TestProcedure EnsureOwner(TestProcedure owner) => owner is not null && owner.Id == ProcedureId
        ? owner
        : throw new DomainException("A verification revision must be projected through its owning artifact.");

    public void UpdateDraft(string objective, string preconditions, string steps, string expectedResult, string actor)
    {
        if (State != TestProcedureState.Draft) throw new DomainException("Only a Draft verification artifact revision can be edited.");
        if (string.IsNullOrWhiteSpace(objective) || string.IsNullOrWhiteSpace(steps) || string.IsNullOrWhiteSpace(expectedResult))
            throw new DomainException("Objective, steps, and expected result are required.");
        if (string.IsNullOrWhiteSpace(actor)) throw new DomainException("A verification artifact update actor is required.");
        Objective = objective.Trim(); Preconditions = preconditions.Trim(); Steps = steps.Trim(); ExpectedResult = expectedResult.Trim();
    }

    // No Approve here. A procedure revision is approved by the test change request that carries it, and
    // materialisation writes it as Approved on that authority — signing the revision separately would be a
    // second approval of the same work. The method that did it had one caller, a route now deleted, and a
    // capability nothing calls is not a capability.
}

/// <summary>
/// Exact many-to-many parent relation from a software Procedure revision to a Case revision.  Both sides are
/// revision identities, never mutable artifact ids, so a later Case or Procedure revision cannot silently
/// change the relationship represented by an earlier controlled record.
/// </summary>
public sealed class TestCaseProcedureLink
{
    private TestCaseProcedureLink() { }
    public TestCaseProcedureLink(Guid caseRevisionId, Guid procedureRevisionId)
    {
        if (caseRevisionId == Guid.Empty || procedureRevisionId == Guid.Empty)
            throw new DomainException("An exact Case-to-Procedure link requires both revisions.");
        if (caseRevisionId == procedureRevisionId)
            throw new DomainException("An exact Case-to-Procedure link cannot point to the same revision.");
        Id = Guid.NewGuid(); CaseRevisionId = caseRevisionId; ProcedureRevisionId = procedureRevisionId;
    }
    public Guid Id { get; private set; }
    public Guid CaseRevisionId { get; private set; }
    public Guid ProcedureRevisionId { get; private set; }
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
