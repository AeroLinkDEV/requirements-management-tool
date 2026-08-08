using AeroLink.Domain.Common;
using AeroLink.Domain.Verification;

namespace AeroLink.Domain.Tests;

public sealed class TestChangeReviewTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 29, 12, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// A review that has been assessed and found to need test work — which is what makes it a test change
    /// request at all, and the state most of these tests are about.
    /// </summary>
    private static TestChangeReview Create(TestChangeReviewDiscipline discipline = TestChangeReviewDiscipline.System)
    {
        var review = Raised(discipline);
        review.RecordTestChangeRequired("verification.engineer", Now);
        return review;
    }

    /// <summary>
    /// A package with procedure work in it, which is the only kind that may be sent for review.
    ///
    /// A package concluding that work is required and then naming none asks an approver to approve nothing,
    /// so most of these tests need one that says what it does before they can reach submission at all.
    /// </summary>
    private static TestChangeReview Submittable(TestChangeReviewDiscipline discipline = TestChangeReviewDiscipline.System)
    {
        var review = Create(discipline);
        review.AddProcedureChange("verification.engineer",
            ProcedureDraft(discipline == TestChangeReviewDiscipline.System ? "SYSTP-000500" : "HLRTP-000500",
                level: discipline == TestChangeReviewDiscipline.System ? TestProcedureLevel.System : TestProcedureLevel.HighLevel),
            Now);
        return review;
    }

    /// <summary>As an approved change leaves it: unassessed, unnumbered, and not yet anything controlled.</summary>
    private static TestChangeReview Raised(TestChangeReviewDiscipline discipline = TestChangeReviewDiscipline.System) =>
        new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), discipline, "SRCR-00039.00", Now);

    [Fact]
    public void An_approved_change_arrives_needing_assessment_and_carrying_no_controlled_number()
    {
        var raised = Raised();

        // Numbering on arrival gave every approved change a SYSTCR before anybody had looked at whether it
        // touched a single procedure. It is a question until it is answered.
        Assert.Equal(TestChangeReviewOutcome.Pending, raised.Outcome);
        Assert.Equal("", raised.BaseNumber);
        Assert.Throws<DomainException>(() => raised.AssignControlledNumber("SYSTCR-000042", Now));
        Assert.Throws<DomainException>(() => raised.Submit("verification.engineer", "test.lead", true, Now));

        raised.RecordTestChangeRequired("verification.engineer", Now.AddMinutes(1));
        raised.AssignControlledNumber("SYSTCR-000042", Now.AddMinutes(1));
        Assert.Equal("SYSTCR-000042", raised.BaseNumber);
        Assert.Equal("verification.engineer", raised.DecidedBy);
    }

    [Fact]
    public void The_review_snapshot_protects_the_rich_content_of_the_case()
    {
        // The review has to be provably of the exact content the approver read. Plain-text hashing alone
        // would let the same words be re-styled â€” bold, a table, a figure â€” without changing the evidence,
        // which makes the approval of something the reviewer never saw.
        var plain = Submittable();
        plain.AssignControlledNumber("SYSTCR-000050", Now.AddMinutes(1));
        plain.WriteCase("verification.engineer", "Title", "Problem", "Analysis", "Solution", Now.AddMinutes(2),
            problemRich: "{\"blocks\":[{\"type\":\"paragraph\",\"text\":\"Problem\"}]}",
            analysisRich: "{\"blocks\":[{\"type\":\"paragraph\",\"text\":\"Analysis\"}]}",
            solutionRich: "{\"blocks\":[{\"type\":\"paragraph\",\"text\":\"Solution\"}]}");
        plain.Submit("verification.engineer", "test.lead", true, Now.AddMinutes(3));
        var plainHash = plain.ReviewCycles.Single().SnapshotHash;

        var attachmentId = Guid.NewGuid();
        var formatted = Submittable();
        formatted.AssignControlledNumber("SYSTCR-000050", Now.AddMinutes(1));
        formatted.WriteCase("verification.engineer", "Title", "Problem", "Analysis", "Solution", Now.AddMinutes(2),
            // Same readable words, different rendered content: a diagram replaces the paragraph. The plain
            // projection is identical, so only hashing the rich structure can tell the two reviews apart.
            problemRich: $"{{\"blocks\":[{{\"type\":\"image\",\"attachmentId\":\"{attachmentId}\",\"alt\":\"Problem\",\"caption\":\"Problem\"}}]}}",
            analysisRich: "{\"blocks\":[{\"type\":\"paragraph\",\"text\":\"Analysis\"}]}",
            solutionRich: "{\"blocks\":[{\"type\":\"paragraph\",\"text\":\"Solution\"}]}");
        formatted.Submit("verification.engineer", "test.lead", true, Now.AddMinutes(3));

        Assert.NotEqual(plainHash, formatted.ReviewCycles.Single().SnapshotHash);
    }

    private static TestProcedureChangeDraft ProcedureDraft(string baseNumber = "SYSTP-000123",
        TestProcedureChangeKind kind = TestProcedureChangeKind.Introduce,
        TestProcedureLevel level = TestProcedureLevel.System) =>
        new(baseNumber, 0, level, kind, "Oceanic waypoint sequencing",
            "Verify oceanic waypoints are sequenced in the order the active flight plan holds.",
            "The aircraft is in cruise with an active oceanic flight plan.",
            "1. Load the plan. 2. Advance past the first waypoint. 3. Read the sequencer.",
            "The next eligible oceanic waypoint is sequenced.",
            "No procedure exercises oceanic sequencing after the approved change.",
            // A procedure being introduced names what it verifies, and submission refuses one that does not.
            // Supplying it here keeps every test that submits a package on the path a real one takes.
            $"[\"{Guid.NewGuid()}\"]");

    [Fact]
    public void A_test_change_request_carries_procedure_changes_the_way_a_change_request_carries_requirements()
    {
        var review = Create();
        review.AssignControlledNumber("SYSTCR-000042", Now.AddMinutes(1));

        var change = review.AddProcedureChange("verification.engineer", ProcedureDraft(), Now.AddMinutes(2));

        Assert.Equal("SYSTP-000123.00", change.DisplayNumber);
        Assert.Equal(TestProcedureChangeKind.Introduce, change.Kind);
        Assert.Single(review.ProcedureChanges);

        // One proposed change per procedure, as one requirement gets one change in a change request:
        // two proposals for the same procedure would leave "what is being done to it?" with two answers.
        Assert.Throws<DomainException>(() =>
            review.AddProcedureChange("verification.engineer", ProcedureDraft(), Now.AddMinutes(3)));

        review.RemoveProcedureChange(change.Id, Now.AddMinutes(4));
        Assert.Empty(review.ProcedureChanges);
    }

    [Fact]
    public void Nothing_can_be_proposed_until_the_assessment_has_called_for_test_work()
    {
        var raised = Raised();

        // The mirror of the requirements rule: a change request cannot be linked to an assessment that
        // concluded nothing, and procedure work cannot be proposed by an assessment that has not asked for it.
        Assert.Throws<DomainException>(() =>
            raised.AddProcedureChange("verification.engineer", ProcedureDraft(), Now.AddMinutes(1)));

        raised.RecordTestChangeRequired("verification.engineer", Now.AddMinutes(2));
        raised.AddProcedureChange("verification.engineer", ProcedureDraft(), Now.AddMinutes(3));
        Assert.Single(raised.ProcedureChanges);
    }

    [Fact]
    public void A_test_change_request_contains_only_its_own_disciplines_procedures()
    {
        var system = Create(TestChangeReviewDiscipline.System);
        var hlr = Create(TestChangeReviewDiscipline.HighLevelSoftware);

        Assert.Equal(TestProcedureLevel.System, system.ProcedureLevel());
        Assert.Equal(TestProcedureLevel.HighLevel, hlr.ProcedureLevel());

        // A System package holding an HLR procedure is the test-world twin of a System change request holding
        // an HLR requirement — which is exactly the legacy data that put a System change request in the HLR
        // coverage queue. The rule exists here so it cannot happen again on this side.
        Assert.Throws<DomainException>(() => system.AddProcedureChange("verification.engineer",
            ProcedureDraft("HLRTP-000001", level: TestProcedureLevel.HighLevel), Now.AddMinutes(1)));
    }

    [Fact]
    public void A_retirement_needs_no_body_but_everything_else_does()
    {
        var review = Create();

        // A retired procedure is being removed, not restated — the same exemption a retired requirement gets.
        var retire = review.AddProcedureChange("verification.engineer",
            new TestProcedureChangeDraft("SYSTP-000009", 1, TestProcedureLevel.System,
                TestProcedureChangeKind.Retire, "", "", "", "", "", "Its requirement was retired."),
            Now.AddMinutes(1));
        Assert.Equal(TestProcedureChangeKind.Retire, retire.Kind);

        Assert.Throws<DomainException>(() => review.AddProcedureChange("verification.engineer",
            new TestProcedureChangeDraft("SYSTP-000010", 0, TestProcedureLevel.System,
                TestProcedureChangeKind.Introduce, "Title", "", "steps", "", "", "why"), Now.AddMinutes(2)));
        Assert.Throws<DomainException>(() => review.AddProcedureChange("verification.engineer",
            new TestProcedureChangeDraft("SYSTP-000011", 0, TestProcedureLevel.System,
                TestProcedureChangeKind.Introduce, "Title", "objective", "", "", "", "why"), Now.AddMinutes(3)));
        // A procedure the build will carry has to be called something.
        Assert.Throws<DomainException>(() => review.AddProcedureChange("verification.engineer",
            new TestProcedureChangeDraft("SYSTP-000012", 0, TestProcedureLevel.System,
                TestProcedureChangeKind.Introduce, "", "objective", "", "steps", "expected", "why"), Now.AddMinutes(4)));
    }

    [Fact]
    public void An_approved_test_change_request_advances_to_its_next_revision_carrying_its_work()
    {
        var review = Create();
        review.AssignControlledNumber("SYSTCR-000042", Now.AddMinutes(1));
        review.AddProcedureChange("verification.engineer", ProcedureDraft(), Now.AddMinutes(2));
        review.Submit("verification.engineer", "test.lead", true, Now.AddMinutes(3));
        review.Approve("test.lead", "Procedure decisions are sound.", Now.AddMinutes(4));

        var next = review.StartNextRevision("verification.engineer", Now.AddMinutes(5), targetReleaseIsReleased: false);

        Assert.Equal("SYSTCR-000042.01", next.DisplayNumber);
        Assert.Equal(TestChangeReviewState.Open, next.State);
        // Reopening approved procedure work to correct it is not a reason to ask again whether any was needed.
        Assert.Equal(TestChangeReviewOutcome.ChangeRequired, next.Outcome);
        // The work carries forward so it is corrected rather than retyped.
        Assert.Single(next.ProcedureChanges);
        Assert.Equal("SYSTP-000123.00", next.ProcedureChanges.Single().DisplayNumber);
        // The predecessor is untouched; superseding it is the caller's act, as on the requirements side.
        Assert.Equal(TestChangeReviewState.Approved, review.State);
    }

    [Fact]
    public void Only_an_approved_test_change_request_revises_and_never_into_a_released_build()
    {
        var open = Submittable();
        open.AssignControlledNumber("SYSTCR-000043", Now.AddMinutes(1));
        Assert.Throws<DomainException>(() =>
            open.StartNextRevision("verification.engineer", Now.AddMinutes(2), targetReleaseIsReleased: false));

        open.Submit("verification.engineer", "test.lead", true, Now.AddMinutes(3));
        open.Approve("test.lead", "Sound.", Now.AddMinutes(4));

        // A released build is closed to change, and a revision is a change like any other.
        Assert.Throws<DomainException>(() =>
            open.StartNextRevision("verification.engineer", Now.AddMinutes(5), targetReleaseIsReleased: true));
    }

    [Fact]
    public void Revising_an_approved_package_carries_its_case_forward_for_correction()
    {
        var review = Submittable();
        review.AssignControlledNumber("SYSTCR-000046", Now.AddMinutes(1));
        review.WriteCase("verification.engineer", "Oceanic sequencing verification", "Problem", "Analysis",
            "Solution", Now.AddMinutes(2),
            problemRich: "{\"blocks\":[{\"type\":\"paragraph\",\"text\":\"Problem rich\"}]}",
            analysisRich: "{\"blocks\":[{\"type\":\"paragraph\",\"text\":\"Analysis rich\"}]}",
            solutionRich: "{\"blocks\":[{\"type\":\"paragraph\",\"text\":\"Solution rich\"}]}");
        review.Submit("verification.engineer", "test.lead", true, Now.AddMinutes(3));
        review.Approve("test.lead", "Sound.", Now.AddMinutes(4));

        var next = review.StartNextRevision("verification.engineer", Now.AddMinutes(5), targetReleaseIsReleased: false);

        // The successor corrects the case; it does not make the engineer retype it.
        Assert.Equal("Oceanic sequencing verification", next.Title);
        Assert.Equal("Problem rich", next.Problem);
        Assert.Equal("Analysis rich", next.Analysis);
        Assert.Equal("Solution rich", next.Solution);
        Assert.Contains("Problem rich", next.ProblemRich);
        Assert.Contains("Analysis rich", next.AnalysisRich);
        Assert.Contains("Solution rich", next.SolutionRich);
    }

    [Fact]
    public void Revising_a_package_hands_its_folded_in_claims_to_the_successor()
    {
        // Submittable rather than merely created: a package answering for nothing cannot be submitted, and
        // this one has to reach Approved before it can revise at all.
        var review = Submittable();
        var folded = Guid.NewGuid();
        review.AssignControlledNumber("SYSTCR-000044", Now.AddMinutes(1));
        review.IncludeChangeRequest("verification.engineer", folded, "SRCR-00040.00", Now.AddMinutes(2));
        review.Submit("verification.engineer", "test.lead", true, Now.AddMinutes(3));
        review.Approve("test.lead", "Sound.", Now.AddMinutes(4));
        var claimedAt = review.AdditionalSources.Single().ClaimedAt;
        var claimId = review.AdditionalSources.Single().Id;

        var next = review.StartNextRevision("verification.engineer", Now.AddMinutes(5), targetReleaseIsReleased: false);

        // A change request is claimed by at most one package, so exactly one of the two revisions may hold it.
        // The successor is the one that will be approved and materialised.
        Assert.Empty(review.AdditionalSources);
        var moved = Assert.Single(next.AdditionalSources);
        Assert.Equal(folded, moved.ChangeRequestId);
        Assert.Equal(next.Id, moved.TestChangeReviewId);
        Assert.Contains(folded, next.CoveredChangeRequestIds);
        Assert.DoesNotContain(folded, review.CoveredChangeRequestIds);

        // Moved, not recreated. Who took this change's test work on, and when, is not a revision's to rewrite.
        Assert.Equal(claimId, moved.Id);
        Assert.Equal(claimedAt, moved.ClaimedAt);
        Assert.Equal("verification.engineer", moved.ClaimedBy);
    }

    [Fact]
    public void A_submitted_package_cannot_grow_underneath_its_approver()
    {
        var review = Create();
        review.AssignControlledNumber("SYSTCR-000042", Now.AddMinutes(1));
        review.AddProcedureChange("verification.engineer", ProcedureDraft(), Now.AddMinutes(2));
        review.Submit("verification.engineer", "test.lead", true, Now.AddMinutes(3));

        // The reviewer is judging a fixed set of procedure changes. Quietly widening what they are approving
        // is the one thing an approval must not allow — the same rule the folded-in change requests follow.
        Assert.Throws<DomainException>(() => review.AddProcedureChange("verification.engineer",
            ProcedureDraft("SYSTP-000124"), Now.AddMinutes(4)));
    }

    [Fact]
    public void Concluding_that_no_test_work_is_required_states_why_and_raises_nothing()
    {
        var raised = Raised();

        Assert.Throws<DomainException>(() => raised.RecordNoTestChangeRequired("verification.engineer", "", Now));

        raised.RecordNoTestChangeRequired("verification.engineer",
            "The approved change alters wording the existing procedures already exercise.", Now.AddMinutes(1));

        Assert.Equal(TestChangeReviewOutcome.NoChangeRequired, raised.Outcome);
        // No number, because there is no test change request — that is the whole content of the conclusion.
        Assert.Equal("", raised.BaseNumber);
        Assert.Throws<DomainException>(() => raised.AssignControlledNumber("SYSTCR-000042", Now.AddMinutes(2)));
    }

    [Fact]
    public void A_controlled_test_change_request_cannot_later_claim_no_test_work_was_needed()
    {
        var review = Create();
        review.AssignControlledNumber("SYSTCR-000042", Now.AddMinutes(1));

        // Its procedure decisions exist under that number. Withdrawing the conclusion has to withdraw them
        // too, rather than leaving a numbered record asserting that nothing was ever required.
        Assert.Throws<DomainException>(() =>
            review.RecordNoTestChangeRequired("verification.engineer", "Reconsidered.", Now.AddMinutes(2)));
    }

    [Fact]
    public void Approved_change_creates_an_open_discipline_specific_review()
    {
        var review = Create(TestChangeReviewDiscipline.HighLevelSoftware);

        Assert.Equal(TestChangeReviewState.Open, review.State);
        Assert.Equal(TestChangeReviewDiscipline.HighLevelSoftware, review.Discipline);
        Assert.Equal("SRCR-00039.00", review.SourceChangeRequestNumber);
    }

    [Fact]
    public void Review_cannot_be_submitted_until_every_procedure_decision_is_complete()
    {
        var review = Submittable();

        Assert.Throws<DomainException>(() => review.Submit("test.engineer", "test.approver", false, Now.AddMinutes(1)));
        review.Submit("test.engineer", "test.approver", true, Now.AddMinutes(2));

        Assert.Equal(TestChangeReviewState.InReview, review.State);
        Assert.Equal("test.engineer", review.SubmittedBy);
    }

    [Fact]
    public void Independent_approval_records_rationale_and_closes_the_review()
    {
        var review = Submittable();
        review.Submit("test.engineer", "test.approver", true, Now.AddMinutes(1));
        review.Approve("test.approver", "Procedure decisions are complete and technically sound.", Now.AddMinutes(2));

        Assert.Equal(TestChangeReviewState.Approved, review.State);
        Assert.Equal("test.approver", review.ApprovedBy);
        Assert.Contains("technically sound", review.ApprovalRationale);
        Assert.Throws<DomainException>(() => review.Retarget(Guid.NewGuid(), Now.AddMinutes(3)));
    }

    [Fact]
    public void The_engineer_who_submitted_a_review_cannot_approve_it()
    {
        var review = Submittable();
        review.Submit("test.lead", "test.approver", true, Now.AddMinutes(1));

        // Casing differs because an actor name reaching the domain is whatever the caller passed; the rule is
        // about the person, not the spelling.
        Assert.Throws<DomainException>(() => review.Approve("Test.Lead", "Looks fine to me.", Now.AddMinutes(2)));
        Assert.Equal(TestChangeReviewState.InReview, review.State);
        Assert.Null(review.ApprovedBy);

        review.Approve("test.approver", "Independently reviewed the procedure decisions.", Now.AddMinutes(3));
        Assert.Equal(TestChangeReviewState.Approved, review.State);
    }

    [Fact]
    public void Reviewer_can_return_a_submitted_review_to_work()
    {
        var review = Submittable();
        review.Submit("test.engineer", "test.approver", true, Now.AddMinutes(1));

        review.ReturnToWork("test.approver", "Clarify the modified procedure.", Now.AddMinutes(2));

        Assert.Equal(TestChangeReviewState.Open, review.State);
        Assert.Null(review.SubmittedBy);
        Assert.Null(review.SubmittedAt);
    }

    [Fact]
    public void A_legacy_review_can_receive_only_its_disciplines_controlled_number()
    {
        var review = Create(TestChangeReviewDiscipline.HighLevelSoftware);

        Assert.Equal("SRCR-00039.00", review.DisplayNumber);
        Assert.Throws<DomainException>(() => review.AssignControlledNumber("LLRTCR-000001", Now.AddMinutes(1)));
        review.AssignControlledNumber("HLRTCR-000014", Now.AddMinutes(2));
        review.AssignControlledNumber("HLRTCR-999999", Now.AddMinutes(3));

        Assert.Equal("HLRTCR-000014.00", review.DisplayNumber);
    }
}
