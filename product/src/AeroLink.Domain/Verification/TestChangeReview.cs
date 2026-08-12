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
    public const int CurrentCaseContractVersion = 1;
    private readonly List<TestChangeRequestClaim> _additionalSources = [];
    private readonly List<TestProcedureChange> _procedureChanges = [];
    private readonly List<ChangeControl.ReviewCycle> _reviewCycles = [];

    private TestChangeReview() { }

    public TestChangeReview(Guid projectId, Guid releaseId, Guid changeRequestId,
        TestChangeReviewDiscipline discipline, string sourceChangeRequestNumber, DateTimeOffset now,
        string baseNumber = "", int revision = 0, int caseContractVersion = CurrentCaseContractVersion)
        : this(projectId, releaseId, discipline, now, baseNumber, revision, caseContractVersion)
    {
        if (changeRequestId == Guid.Empty) throw new DomainException("A test change review requires its originating change request.");
        ChangeRequestId = changeRequestId;
        SourceChangeRequestNumber = Required(sourceChangeRequestNumber, "source change request number");
    }

    /// <summary>
    /// A package raised from a Problem Report rather than from an approved change request.
    ///
    /// Test work is not only ever caused by a requirement change: an anomaly found in the field is a
    /// legitimate reason to write, correct or withdraw a procedure, and there may be no change request at the
    /// package's own level to hang it on. The Problem Report takes the originating slot the change request
    /// would have occupied, so a package still has exactly one thing it was raised from — which is what its
    /// number, its covered-sources record and its case snapshot all depend on.
    ///
    /// Approved change requests can still be folded in afterwards through <see cref="IncludeChangeRequest"/>.
    /// </summary>
    public static TestChangeReview FromProblemReport(Guid projectId, Guid releaseId, Guid problemReportId,
        TestChangeReviewDiscipline discipline, string sourceProblemReportNumber, DateTimeOffset now,
        string baseNumber = "", int revision = 0, int caseContractVersion = CurrentCaseContractVersion)
    {
        if (problemReportId == Guid.Empty)
            throw new DomainException("A test change review raised from a Problem Report requires that report.");
        var review = new TestChangeReview(projectId, releaseId, discipline, now, baseNumber, revision, caseContractVersion)
        {
            OriginatingProblemReportId = problemReportId,
        };
        review.SourceProblemReportNumber = Required(sourceProblemReportNumber, "source Problem Report number");
        return review;
    }

    private TestChangeReview(Guid projectId, Guid releaseId, TestChangeReviewDiscipline discipline,
        DateTimeOffset now, string baseNumber, int revision, int caseContractVersion)
    {
        Revision = revision;
        if (caseContractVersion < 0 || caseContractVersion > CurrentCaseContractVersion)
            throw new DomainException("A test change request requires a supported engineering-case contract version.");
        if (projectId == Guid.Empty) throw new DomainException("A test change review requires its Project.");
        if (releaseId == Guid.Empty) throw new DomainException("A test change review requires its software build.");
        if (!Enum.IsDefined(discipline)) throw new DomainException("A test change review requires a known discipline.");
        Id = Guid.NewGuid();
        ProjectId = projectId;
        ReleaseId = releaseId;
        Discipline = discipline;
        // Empty remains readable for databases created before controlled TCR numbering. The showcase
        // upgrade assigns those rows a real number without changing their identity or evidence.
        BaseNumber = baseNumber.Trim();
        CaseContractVersion = caseContractVersion;
        State = TestChangeReviewState.Open;
        CreatedAt = now;
        UpdatedAt = now;
    }

    public Guid Id { get; private set; }
    public Guid ProjectId { get; private set; }
    public Guid ReleaseId { get; private set; }
    /// <summary>The approved change request this package was raised from, when it was raised from one.</summary>
    public Guid? ChangeRequestId { get; private set; }
    /// <summary>
    /// The Problem Report it was raised from instead. Exactly one of this and <see cref="ChangeRequestId"/>
    /// is set: a package always has one thing it was raised from, which is what its number, its
    /// covered-sources record and its case snapshot depend on.
    /// </summary>
    public Guid? OriginatingProblemReportId { get; private set; }
    public string SourceProblemReportNumber { get; private set; } = "";
    /// <summary>What this package was raised from, whichever kind that was.</summary>
    public string SourceDisplayNumber =>
        ChangeRequestId is not null ? SourceChangeRequestNumber : SourceProblemReportNumber;
    public TestChangeReviewDiscipline Discipline { get; private set; }
    public string SourceChangeRequestNumber { get; private set; } = "";
    /// <summary>Its controlled number — SYSTCR, HLRTCR or LLRTCR — empty only for rows raised before it had one.</summary>
    public string BaseNumber { get; private set; } = "";
    /// <summary>Advances when an approved package is reopened for further test work against the same change.</summary>
    public int Revision { get; private set; }
    public string DisplayNumber => string.IsNullOrEmpty(BaseNumber) ? SourceDisplayNumber : $"{BaseNumber}.{Revision:D2}";
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
    /// <summary>Zero identifies persisted history created before a complete engineering case was required.</summary>
    public int CaseContractVersion { get; private set; }

    public IReadOnlyList<string> MissingCaseFields()
    {
        var missing = new List<string>(4);
        if (string.IsNullOrWhiteSpace(Title)) missing.Add(nameof(Title));
        if (string.IsNullOrWhiteSpace(Problem)) missing.Add(nameof(Problem));
        if (string.IsNullOrWhiteSpace(Analysis)) missing.Add(nameof(Analysis));
        if (string.IsNullOrWhiteSpace(Solution)) missing.Add(nameof(Solution));
        return missing;
    }

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
            draft.DrivingRequirementRevisionIdsJson, draft.RemovedRequirementRevisionIdsJson,
            draft.CoverageChangeRationale, actorId);
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
        CaseContractVersion = CurrentCaseContractVersion;
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
        ChangeControl.ReviewWorkflowSpecification? workflow = null,
        IReadOnlyList<Guid>? problemReportIds = null,
        IReadOnlyList<VerificationImpactSnapshot>? impactDecisions = null)
    {
        EnsureOpen();
        if (Outcome == TestChangeReviewOutcome.Pending)
            throw new DomainException("Assess the change before sending it for review.");
        if (!everyItemResolved)
            throw new DomainException("Every test-procedure decision must be completed before review.");
        var missingCaseFields = MissingCaseFields();
        if (Outcome == TestChangeReviewOutcome.ChangeRequired
            && CaseContractVersion >= CurrentCaseContractVersion && missingCaseFields.Count > 0)
            throw new DomainException(
                $"Complete the test change request case before sending it for review. Missing: {string.Join(", ", missingCaseFields)}.");
        if (Outcome == TestChangeReviewOutcome.ChangeRequired
            && CaseContractVersion >= CurrentCaseContractVersion && _procedureChanges.Count == 0)
            throw new DomainException(
                "A test change request that requires test work names no procedure decisions. Add an Introduce, Modify, or Retire decision before review.");
        // A procedure being introduced has to say what it verifies, and submission is where that is checked —
        // not when it is written. A draft package is worked on incrementally, exactly as a change request is,
        // so the gate belongs at the point an approver is asked to sign rather than at the point an engineer
        // starts typing. What must never happen is an approver signing a procedure that verifies nothing.
        if (_procedureChanges.Any(x => x.Kind == TestProcedureChangeKind.Introduce
                && (string.IsNullOrWhiteSpace(x.DrivingRequirementRevisionIdsJson)
                    || x.DrivingRequirementRevisionIdsJson.Trim() is "[]" or "")))
            throw new DomainException(
                "Every procedure this package introduces must name the requirement revisions it verifies.");
        if (approvers.Any(x => string.Equals(x.UserId, actorId, StringComparison.OrdinalIgnoreCase)))
            throw new DomainException("The test change request approver must be independent from its submitting engineer.");
        // Version-zero history can still be reconstructed exactly as it was approved before procedure decisions
        // existed. Current packages cannot use that compatibility path to approve an empty work package.
        var cycle = ChangeControl.ReviewCycle.ForTestChangeRequest(Id, _reviewCycles.Count + 1,
            ComputeSnapshotHash(problemReportIds, impactDecisions), approvers, now, mode, workflow);
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
    private string ComputeSnapshotHash(IReadOnlyList<Guid>? problemReportIds,
        IReadOnlyList<VerificationImpactSnapshot>? impactDecisions)
    {
        // A versioned, canonical structured manifest: property order is deliberately controlled, delimiters
        // are impossible to confuse with engineering text, and every governed field is present exactly once.
        // Problem Report identities are governed package content: the reviewer sees them and they are locked
        // while the review is active. Runtime fields (assignment, timestamps) are deliberately absent.
        using var buffer = new MemoryStream();
        using (var writer = new System.Text.Json.Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("contract", "aerolink.tcr-review-snapshot");
            writer.WriteNumber("version", 1);

            writer.WriteStartObject("package");
            writer.WriteString("baseNumber", BaseNumber);
            writer.WriteNumber("revision", Revision);
            writer.WriteString("displayNumber", DisplayNumber);
            writer.WriteString("discipline", Discipline.ToString());
            writer.WriteString("outcome", Outcome.ToString());
            writer.WriteString("noChangeRationale", NoChangeRationale);
            writer.WriteEndObject();

            writer.WriteStartObject("case");
            writer.WriteString("title", Title);
            writer.WriteString("problem", Problem);
            writer.WriteString("problemRich", ProblemRich);
            writer.WriteString("analysis", Analysis);
            writer.WriteString("analysisRich", AnalysisRich);
            writer.WriteString("solution", Solution);
            writer.WriteString("solutionRich", SolutionRich);
            writer.WriteEndObject();

            writer.WriteStartArray("coveredSources");
            foreach (var source in CoveredSourcesOrdered())
            {
                writer.WriteStartObject();
                writer.WriteString("changeRequestId", source.ChangeRequestId.ToString("D"));
                writer.WriteString("displayNumber", source.DisplayNumber);
                writer.WriteBoolean("originating", source.Originating);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();

            // Written only when the package was raised from a Problem Report. A package raised from a change
            // request therefore produces byte-identical JSON to the one it produced before this existed, so
            // every hash already recorded against a signature still verifies.
            if (OriginatingProblemReportId is { } originatingProblemReport)
            {
                writer.WriteStartObject("originatingProblemReport");
                writer.WriteString("problemReportId", originatingProblemReport.ToString("D"));
                writer.WriteString("displayNumber", SourceProblemReportNumber);
                writer.WriteEndObject();
            }

            writer.WriteStartArray("procedureChanges");
            foreach (var change in _procedureChanges
                         .OrderBy(x => x.BaseNumber, StringComparer.Ordinal)
                         .ThenBy(x => x.Revision)
                         .ThenBy(x => x.Id))
            {
                writer.WriteStartObject();
                writer.WriteString("baseNumber", change.BaseNumber);
                writer.WriteNumber("revision", change.Revision);
                writer.WriteString("displayNumber", change.DisplayNumber);
                writer.WriteString("kind", change.Kind.ToString());
                writer.WriteString("level", change.Level.ToString());
                writer.WriteString("title", change.Title);
                writer.WriteString("objective", change.Objective);
                writer.WriteString("preconditions", change.Preconditions);
                writer.WriteString("steps", change.Steps);
                writer.WriteString("expectedResult", change.ExpectedResult);
                writer.WriteString("rationale", change.Rationale);
                writer.WriteStartArray("drivingRequirementRevisionIds");
                foreach (var id in DrivingRequirementIds(change.DrivingRequirementRevisionIdsJson)
                             .OrderBy(x => x.ToString("D"), StringComparer.Ordinal))
                    writer.WriteStringValue(id.ToString("D"));
                writer.WriteEndArray();
                writer.WriteStartArray("removedRequirementRevisionIds");
                foreach (var id in DrivingRequirementIds(change.RemovedRequirementRevisionIdsJson)
                             .OrderBy(x => x.ToString("D"), StringComparer.Ordinal))
                    writer.WriteStringValue(id.ToString("D"));
                writer.WriteEndArray();
                writer.WriteString("coverageChangeRationale", change.CoverageChangeRationale);
                writer.WriteString("coverageChangedBy", change.CoverageChangedBy);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();

            writer.WriteStartArray("problemReportIds");
            foreach (var id in (problemReportIds ?? []).OrderBy(x => x.ToString("D"), StringComparer.Ordinal))
                writer.WriteStringValue(id.ToString("D"));
            writer.WriteEndArray();

            writer.WriteStartArray("impactDecisions");
            foreach (var item in (impactDecisions ?? [])
                         .OrderBy(x => x.ItemId.ToString("D"), StringComparer.Ordinal))
            {
                writer.WriteStartObject();
                writer.WriteString("itemId", item.ItemId.ToString("D"));
                writer.WriteString("changeRequestId", item.ChangeRequestId.ToString("D"));
                writer.WriteString("trigger", item.Trigger.ToString());
                if (item.RequirementChangeId is { } requirementChangeId)
                    writer.WriteString("requirementChangeId", requirementChangeId.ToString("D"));
                if (item.RequirementRevisionId is { } requirementRevisionId)
                    writer.WriteString("requirementRevisionId", requirementRevisionId.ToString("D"));
                if (item.ProcedureId is { } procedureId)
                    writer.WriteString("procedureId", procedureId.ToString("D"));
                writer.WriteString("subjectDisplayNumber", item.SubjectDisplayNumber);
                if (item.Outcome is { } outcome)
                    writer.WriteString("outcome", outcome.ToString());
                if (item.ProcedureChangeAction is { } action)
                    writer.WriteString("procedureChangeAction", action.ToString());
                writer.WriteString("resolutionRationale", item.ResolutionRationale);
                if (item.ResolvedProcedureId is { } resolvedProcedureId)
                    writer.WriteString("resolvedProcedureId", resolvedProcedureId.ToString("D"));
                if (item.ResolvedProcedureRevisionId is { } resolvedProcedureRevisionId)
                    writer.WriteString("resolvedProcedureRevisionId", resolvedProcedureRevisionId.ToString("D"));
                if (item.RetargetedRequirementRevisionId is { } retargetedRequirementRevisionId)
                    writer.WriteString("retargetedRequirementRevisionId", retargetedRequirementRevisionId.ToString("D"));
                writer.WriteBoolean("preReleaseEvidenceRequired", item.PreReleaseEvidenceRequired);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();

            writer.WriteEndObject();
        }
        return Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(buffer.ToArray()))
            .ToLowerInvariant();
    }

    /// <summary>
    /// The source change requests this package answers for, in a deterministic order independent of the
    /// order the caller happened to fold them in.
    /// </summary>
    private IEnumerable<(Guid ChangeRequestId, string DisplayNumber, bool Originating)> CoveredSourcesOrdered() =>
        // The originating entry appears only when the package was raised from a change request. A package
        // raised from a Problem Report records that origin separately rather than fabricating a change
        // request entry, and a package raised from a change request serializes exactly as it always has —
        // which matters, because this snapshot is hashed and the hash is what a signature recorded.
        (ChangeRequestId is { } originating
            ? new (Guid ChangeRequestId, string DisplayNumber, bool Originating)[]
                { (originating, SourceChangeRequestNumber, true) }
            : [])
            .Concat(_additionalSources.Select(x =>
                (ChangeRequestId: x.ChangeRequestId, DisplayNumber: x.ChangeRequestNumber, Originating: false)))
            .OrderBy(x => x.ChangeRequestId.ToString("D"), StringComparer.Ordinal);

    private static IReadOnlyList<Guid> DrivingRequirementIds(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<List<Guid>>(json) ?? [];
        }
        catch (System.Text.Json.JsonException)
        {
            throw new DomainException(
                "A procedure change carries malformed driving requirement revisions. Correct it before sending the package for review.");
        }
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
    public void Submit(string actorId, string approverId, bool everyItemResolved, DateTimeOffset now,
        IReadOnlyList<Guid>? problemReportIds = null,
        IReadOnlyList<VerificationImpactSnapshot>? impactDecisions = null)
    {
        Required(approverId, "selected test change request approver");
        SubmitForReview(actorId, [new ChangeControl.ApproverSelection(approverId, approverId)],
            everyItemResolved, now, problemReportIds: problemReportIds, impactDecisions: impactDecisions);
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
    /// The active reviewer returns the package, closing the review cycle as ChangesRequested.
    ///
    /// The prior cycle stays as historical evidence; a resubmission starts the next cycle sequence with a
    /// fresh snapshot, so old approvals are never reused. Only the approver whose stage is currently active
    /// may return the package, mirroring the requirement-side review.
    /// </summary>
    public void RequestChanges(string actorId, string rationale, DateTimeOffset now)
    {
        if (State != TestChangeReviewState.InReview)
            throw new DomainException("Only a submitted test change request can be returned.");
        Required(actorId, "reviewer");
        Required(rationale, "return rationale");
        var cycle = ActiveReviewCycle ?? throw new DomainException("This test change request has no active review.");
        var active = cycle.Steps.SingleOrDefault(x => x.State == ChangeControl.ApprovalStepState.Active
            && string.Equals(x.ApproverId, actorId, StringComparison.OrdinalIgnoreCase));
        if (active is null)
            throw new DomainException("Only the active approver can request changes.");
        cycle.RequestChanges(rationale, now);
        SubmittedBy = null;
        SelectedApproverId = null;
        SubmittedAt = null;
        State = TestChangeReviewState.Open;
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
        (ChangeRequestId is { } originating ? new[] { originating } : [])
            .Concat(_additionalSources.Select(x => x.ChangeRequestId));

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
        // The successor keeps whatever this package was raised from. A revision continues the same work
        // against the same origin; it does not acquire a different one.
        var next = ChangeRequestId is { } originating
            ? new TestChangeReview(ProjectId, ReleaseId, originating, Discipline,
                SourceChangeRequestNumber, now, BaseNumber, Revision + 1)
            : FromProblemReport(ProjectId, ReleaseId, OriginatingProblemReportId!.Value, Discipline,
                SourceProblemReportNumber, now, BaseNumber, Revision + 1);
        next.RecordTestChangeRequired(actorId, now);
        // The case carries forward exactly as a change request's does, so the engineer corrects the rationale
        // rather than retyping it. Packages that predate case authoring carry no fabricated case.
        if (!string.IsNullOrWhiteSpace(Title))
            next.WriteCase(actorId, Title, Problem, Analysis, Solution, now,
                ProblemRich, AnalysisRich, SolutionRich);
        foreach (var change in _procedureChanges)
            next.AddProcedureChange(actorId, new TestProcedureChangeDraft(change.BaseNumber, change.Revision,
                change.Level, change.Kind, change.Title, change.Objective, change.Preconditions, change.Steps,
                change.ExpectedResult, change.Rationale, change.DrivingRequirementRevisionIdsJson,
                change.RemovedRequirementRevisionIdsJson, change.CoverageChangeRationale), now);

        // Folded-in claims move to the successor rather than staying behind or being dropped. A change
        // request is claimed by at most one package, enforced by a unique index, so the two revisions cannot
        // both hold one — and the successor is the package that will actually be approved and materialised.
        // Leaving the claim on the predecessor would mean a superseded package still answering for test work
        // nobody is doing, and dropping it would make the new revision cover less than the old one without
        // saying so.
        //
        // Each claim is moved rather than recreated: same row, same identifier, same claimant and time. Who
        // took this change's test work on, and when, is not something a revision should rewrite.
        foreach (var claim in _additionalSources) claim.MoveTo(next.Id);
        next._additionalSources.AddRange(_additionalSources);
        _additionalSources.Clear();
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

    /// <summary>
    /// Advances the concurrency/version token when governed content held outside this aggregate (Problem
    /// Report links, verification-impact decisions) changes in the same unit of work. This is what makes a
    /// link-versus-submit or decision-versus-submit race collapse to exactly one winner: whichever side
    /// saves second hits the EF concurrency token and receives the stale-write contract.
    /// </summary>
    public void RecordControlledContentChange(DateTimeOffset now) => Touch(now);

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

    /// <summary>
    /// Hands this claim to the package's next revision. Only <see cref="TestChangeReview.StartNextRevision"/>
    /// calls it, which is why it is internal: a claim moving for any other reason would be a change request
    /// changing hands without anybody deciding to.
    /// </summary>
    internal void MoveTo(Guid testChangeReviewId) => TestChangeReviewId = testChangeReviewId;
}
