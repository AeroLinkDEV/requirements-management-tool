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
        review.WriteCase("verification.engineer", "Verification case", "Problem", "Analysis", "Solution", Now);
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

    private static TestChangeReview RaisedLegacy(TestChangeReviewDiscipline discipline = TestChangeReviewDiscipline.System) =>
        new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), discipline, "SRCR-00039.00", Now,
            caseContractVersion: 0);

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
    public void Current_packages_require_a_complete_case_while_legacy_history_can_be_reconstructed_unchanged()
    {
        var current = Raised();
        current.RecordTestChangeRequired("verification.engineer", Now);
        var refusal = Assert.Throws<DomainException>(() =>
            current.Submit("verification.engineer", "test.lead", true, Now.AddMinutes(1)));
        Assert.Contains("Title, Problem, Analysis, Solution", refusal.Message);

        var legacy = new TestChangeReview(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            TestChangeReviewDiscipline.System, "SRCR-00001.00", Now, caseContractVersion: 0);
        legacy.RecordTestChangeRequired("historical.import", Now);
        legacy.Submit("historical.import", "historical.approver", true, Now.AddMinutes(1));

        Assert.Equal(TestChangeReviewState.InReview, legacy.State);
        Assert.Equal(0, legacy.CaseContractVersion);
        Assert.Equal(["Title", "Problem", "Analysis", "Solution"], legacy.MissingCaseFields());
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

    [Fact]
    public void Every_governed_procedure_change_field_changes_the_review_snapshot()
    {
        var driving = Guid.NewGuid();
        var baseline = SnapshotFor(ProcedureDraftWith(drivingJson: $"[\"{driving}\"]"));

        Assert.NotEqual(baseline, SnapshotFor(ProcedureDraftWith(
            preconditions: "A different precondition", drivingJson: $"[\"{driving}\"]")));
        Assert.NotEqual(baseline, SnapshotFor(ProcedureDraftWith(
            rationale: "A different rationale", drivingJson: $"[\"{driving}\"]")));
        Assert.NotEqual(baseline, SnapshotFor(ProcedureDraftWith(
            drivingJson: $"[\"{Guid.NewGuid()}\"]")));
        Assert.NotEqual(baseline, SnapshotFor(ProcedureDraftWith(
            drivingJson: $"[\"{driving}\",\"{Guid.NewGuid()}\"]")));
    }

    [Fact]
    public void The_snapshot_is_deterministic_and_ignores_runtime_fields()
    {
        var driving = Guid.NewGuid();
        var draft = ProcedureDraftWith(drivingJson: $"[\"{driving}\"]");
        var fixedChange = Guid.NewGuid();
        Assert.Equal(SnapshotFor(draft, fixedChange), SnapshotFor(draft, fixedChange));

        var changeRequestId = Guid.NewGuid();
        var assignedA = CreateWith(changeRequestId);
        assignedA.Assign("lead.user", "engineer.a", Now.AddMinutes(1));
        assignedA.AssignControlledNumber("SYSTCR-000056", Now.AddMinutes(1));
        assignedA.AddProcedureChange("verification.engineer", draft, Now.AddMinutes(2));
        assignedA.Submit("verification.engineer", "test.lead", true, Now.AddMinutes(3));

        var assignedB = CreateWith(changeRequestId);
        assignedB.Assign("lead.user", "engineer.b", Now.AddMinutes(1));
        assignedB.AssignControlledNumber("SYSTCR-000056", Now.AddMinutes(1));
        assignedB.AddProcedureChange("verification.engineer", draft, Now.AddMinutes(2));
        assignedB.Submit("verification.engineer", "test.lead", true, Now.AddMinutes(3));

        Assert.Equal(assignedA.ReviewCycles.Single().SnapshotHash,
            assignedB.ReviewCycles.Single().SnapshotHash);
    }

    [Fact]
    public void The_snapshot_covers_the_source_change_set()
    {
        var draft = ProcedureDraftWith(drivingJson: $"[\"{Guid.NewGuid()}\"]");
        var solo = Create();
        solo.AssignControlledNumber("SYSTCR-000057", Now.AddMinutes(1));
        solo.AddProcedureChange("verification.engineer", draft, Now.AddMinutes(2));
        solo.Submit("verification.engineer", "test.lead", true, Now.AddMinutes(3));

        var folded = Create();
        folded.AssignControlledNumber("SYSTCR-000057", Now.AddMinutes(1));
        folded.IncludeChangeRequest("verification.engineer", Guid.NewGuid(), "SRCR-00060.00", Now.AddMinutes(2));
        folded.AddProcedureChange("verification.engineer", draft, Now.AddMinutes(3));
        folded.Submit("verification.engineer", "test.lead", true, Now.AddMinutes(4));

        Assert.NotEqual(solo.ReviewCycles.Single().SnapshotHash,
            folded.ReviewCycles.Single().SnapshotHash);
    }

    [Fact]
    public void Outcome_alone_changes_the_review_snapshot()
    {
        var required = RaisedLegacy();
        required.RecordTestChangeRequired("verification.engineer", Now.AddMinutes(1));
        required.Submit("verification.engineer", "test.lead", true, Now.AddMinutes(2));

        var notRequired = RaisedLegacy();
        notRequired.RecordNoTestChangeRequired("verification.engineer", "Already covered.", Now.AddMinutes(1));
        notRequired.Submit("verification.engineer", "test.lead", true, Now.AddMinutes(2));

        Assert.NotEqual(required.ReviewCycles.Single().SnapshotHash,
            notRequired.ReviewCycles.Single().SnapshotHash);
    }

    [Fact]
    public void No_change_rationale_alone_changes_the_review_snapshot()
    {
        var first = Raised();
        first.RecordNoTestChangeRequired("verification.engineer", "Rationale A.", Now.AddMinutes(1));
        first.Submit("verification.engineer", "test.lead", true, Now.AddMinutes(2));

        var second = Raised();
        second.RecordNoTestChangeRequired("verification.engineer", "Rationale B.", Now.AddMinutes(1));
        second.Submit("verification.engineer", "test.lead", true, Now.AddMinutes(2));

        Assert.NotEqual(first.ReviewCycles.Single().SnapshotHash,
            second.ReviewCycles.Single().SnapshotHash);
    }

    [Fact]
    public void Resubmission_after_rationale_edit_hashes_differently()
    {
        var review = Raised();
        review.RecordNoTestChangeRequired("verification.engineer", "Rationale A.", Now.AddMinutes(1));
        review.Submit("verification.engineer", "test.lead", true, Now.AddMinutes(2));
        var firstHash = review.ReviewCycles.Single().SnapshotHash;

        review.RequestChanges("test.lead", "Rework the rationale.", Now.AddMinutes(3));
        review.RecordNoTestChangeRequired("verification.engineer", "Rationale B.", Now.AddMinutes(4));
        review.Submit("verification.engineer", "test.lead", true, Now.AddMinutes(5));

        Assert.Equal(2, review.ReviewCycles.Count);
        Assert.NotEqual(firstHash, review.ReviewCycles.Last().SnapshotHash);
        Assert.Equal(firstHash, review.ReviewCycles.First().SnapshotHash);
    }

    [Fact]
    public void Problem_report_identities_are_governed_snapshot_content()
    {
        var driving = Guid.NewGuid();
        var draft = ProcedureDraftWith(drivingJson: $"[\"{driving}\"]");

        var linked = Create();
        linked.AssignControlledNumber("SYSTCR-000058", Now.AddMinutes(1));
        linked.AddProcedureChange("verification.engineer", draft, Now.AddMinutes(2));
        linked.Submit("verification.engineer", "test.lead", true, Now.AddMinutes(3),
            problemReportIds: new[] { Guid.NewGuid(), Guid.NewGuid() });

        var plain = Create();
        plain.AssignControlledNumber("SYSTCR-000058", Now.AddMinutes(1));
        plain.AddProcedureChange("verification.engineer", draft, Now.AddMinutes(2));
        plain.Submit("verification.engineer", "test.lead", true, Now.AddMinutes(3));

        Assert.NotEqual(linked.ReviewCycles.Single().SnapshotHash,
            plain.ReviewCycles.Single().SnapshotHash);
    }

    [Fact]
    public void Delimiter_collision_produces_different_hashes()
    {
        var driving = Guid.NewGuid();
        var a = Create();
        a.AssignControlledNumber("SYSTCR-000059", Now.AddMinutes(1));
        a.AddProcedureChange("verification.engineer",
            new TestProcedureChangeDraft("SYSTP-000200", 0, TestProcedureLevel.System,
                TestProcedureChangeKind.Introduce, "A|B", "C", "", "Steps", "Expected", "Why",
                $"[\"{driving}\"]"), Now.AddMinutes(2));
        a.Submit("verification.engineer", "test.lead", true, Now.AddMinutes(3));

        var b = Create();
        b.AssignControlledNumber("SYSTCR-000059", Now.AddMinutes(1));
        b.AddProcedureChange("verification.engineer",
            new TestProcedureChangeDraft("SYSTP-000200", 0, TestProcedureLevel.System,
                TestProcedureChangeKind.Introduce, "A", "B|C", "", "Steps", "Expected", "Why",
                $"[\"{driving}\"]"), Now.AddMinutes(2));
        b.Submit("verification.engineer", "test.lead", true, Now.AddMinutes(3));

        Assert.NotEqual(a.ReviewCycles.Single().SnapshotHash, b.ReviewCycles.Single().SnapshotHash);
    }

    [Fact]
    public void Coverage_removals_rationale_and_author_are_frozen_into_the_review_snapshot()
    {
        var removed = Guid.NewGuid();
        TestProcedureChangeDraft Draft(Guid removal, string rationale) => new("SYSTP-000200", 1,
            TestProcedureLevel.System, TestProcedureChangeKind.Modify, "Revised procedure", "Objective", "",
            "Steps", "Expected", "Procedure rationale", "[]", $"[\"{removal}\"]", rationale);

        var first = Create();
        first.AssignControlledNumber("SYSTCR-000061", Now.AddMinutes(1));
        var change = first.AddProcedureChange("verification.engineer", Draft(removed, "Coverage no longer applies."),
            Now.AddMinutes(2));
        first.Submit("verification.engineer", "test.lead", true, Now.AddMinutes(3));

        var changedRationale = Create();
        changedRationale.AssignControlledNumber("SYSTCR-000061", Now.AddMinutes(1));
        changedRationale.AddProcedureChange("verification.engineer", Draft(removed, "A different removal decision."),
            Now.AddMinutes(2));
        changedRationale.Submit("verification.engineer", "test.lead", true, Now.AddMinutes(3));

        var changedRemoval = Create();
        changedRemoval.AssignControlledNumber("SYSTCR-000061", Now.AddMinutes(1));
        changedRemoval.AddProcedureChange("verification.engineer",
            Draft(Guid.NewGuid(), "Coverage no longer applies."), Now.AddMinutes(2));
        changedRemoval.Submit("verification.engineer", "test.lead", true, Now.AddMinutes(3));

        var changedAuthor = Create();
        changedAuthor.AssignControlledNumber("SYSTCR-000061", Now.AddMinutes(1));
        changedAuthor.AddProcedureChange("another.engineer", Draft(removed, "Coverage no longer applies."),
            Now.AddMinutes(2));
        changedAuthor.Submit("verification.engineer", "test.lead", true, Now.AddMinutes(3));

        Assert.Equal("verification.engineer", change.CoverageChangedBy);
        Assert.Equal("Coverage no longer applies.", change.CoverageChangeRationale);
        Assert.NotEqual(first.ReviewCycles.Single().SnapshotHash,
            changedRationale.ReviewCycles.Single().SnapshotHash);
        Assert.NotEqual(first.ReviewCycles.Single().SnapshotHash,
            changedRemoval.ReviewCycles.Single().SnapshotHash);
        Assert.NotEqual(first.ReviewCycles.Single().SnapshotHash,
            changedAuthor.ReviewCycles.Single().SnapshotHash);
    }

    [Fact]
    public void Malformed_driving_requirement_json_is_refused_at_submission()
    {
        var review = Create();
        review.AssignControlledNumber("SYSTCR-000060", Now.AddMinutes(1));
        review.AddProcedureChange("verification.engineer",
            ProcedureDraftWith(drivingJson: "{not-json"), Now.AddMinutes(2));
        Assert.Throws<DomainException>(() =>
            review.Submit("verification.engineer", "test.lead", true, Now.AddMinutes(3)));
    }

    [Fact]
    public void Reversing_input_orders_produces_the_same_hash()
    {
        var drivingA = Guid.NewGuid();
        var drivingB = Guid.NewGuid();
        var changeRequestId = Guid.NewGuid();
        var foldedSourceA = Guid.NewGuid();
        var foldedSourceB = Guid.NewGuid();

        TestChangeReview Build(bool reverseSources, bool reverseChanges, bool reverseDriving)
        {
            var review = CreateWith(changeRequestId);
            review.AssignControlledNumber("SYSTCR-000061", Now.AddMinutes(1));
            review.IncludeChangeRequest("verification.engineer", reverseSources ? foldedSourceB : foldedSourceA,
                reverseSources ? "SRCR-00071.00" : "SRCR-00070.00", Now.AddMinutes(2));
            review.IncludeChangeRequest("verification.engineer", reverseSources ? foldedSourceA : foldedSourceB,
                reverseSources ? "SRCR-00070.00" : "SRCR-00071.00", Now.AddMinutes(2));
            var driving = reverseDriving
                ? $"[\"{drivingB}\",\"{drivingA}\"]"
                : $"[\"{drivingA}\",\"{drivingB}\"]";
            var alpha = ProcedureDraftWith(baseNumber: "SYSTP-000301", drivingJson: driving);
            var beta = ProcedureDraftWith(baseNumber: "SYSTP-000302", drivingJson: driving);
            if (reverseChanges)
            {
                review.AddProcedureChange("verification.engineer", beta, Now.AddMinutes(3));
                review.AddProcedureChange("verification.engineer", alpha, Now.AddMinutes(4));
            }
            else
            {
                review.AddProcedureChange("verification.engineer", alpha, Now.AddMinutes(3));
                review.AddProcedureChange("verification.engineer", beta, Now.AddMinutes(4));
            }
            review.Submit("verification.engineer", "test.lead", true, Now.AddMinutes(5));
            return review;
        }

        var forward = Build(reverseSources: false, reverseChanges: false, reverseDriving: false);
        var reversed = Build(reverseSources: true, reverseChanges: true, reverseDriving: true);
        Assert.Equal(forward.ReviewCycles.Single().SnapshotHash,
            reversed.ReviewCycles.Single().SnapshotHash);
    }

    private static TestChangeReview SubmittedWithImpact(VerificationImpactSnapshot impact)
    {
        var review = Create();
        review.AssignControlledNumber("SYSTCR-000062", Now.AddMinutes(1));
        review.AddProcedureChange("verification.engineer",
            ProcedureDraftWith(drivingJson: $"[\"{Guid.NewGuid()}\"]"), Now.AddMinutes(2));
        review.Submit("verification.engineer", "test.lead", true, Now.AddMinutes(3),
            impactDecisions: [impact]);
        return review;
    }

    private static VerificationImpactSnapshot ImpactBase() =>
        new(Guid.NewGuid(), Guid.NewGuid(), VerificationImpactTrigger.RequirementIntroduced,
            Guid.NewGuid(), Guid.NewGuid(), null, "SYSR-00000100.00",
            VerificationImpactOutcome.NewProcedureRequired, TestProcedureChangeAction.CreateNew,
            "A new procedure is required.", null, null, null, false);

    [Fact]
    public void Every_governed_impact_decision_field_changes_the_snapshot()
    {
        var baseline = SubmittedWithImpact(ImpactBase()).ReviewCycles.Single().SnapshotHash;

        Assert.NotEqual(baseline, SubmittedWithImpact(ImpactBase() with
        { Outcome = VerificationImpactOutcome.NoTestRequired, ProcedureChangeAction = TestProcedureChangeAction.NoTestRequired })
            .ReviewCycles.Single().SnapshotHash);
        Assert.NotEqual(baseline, SubmittedWithImpact(ImpactBase() with
        { ResolutionRationale = "A different rationale." }).ReviewCycles.Single().SnapshotHash);
        Assert.NotEqual(baseline, SubmittedWithImpact(ImpactBase() with
        { PreReleaseEvidenceRequired = true }).ReviewCycles.Single().SnapshotHash);
        Assert.NotEqual(baseline, SubmittedWithImpact(ImpactBase() with
        { SubjectDisplayNumber = "SYSR-00000200.00" }).ReviewCycles.Single().SnapshotHash);
        Assert.NotEqual(baseline, SubmittedWithImpact(ImpactBase() with
        { Trigger = VerificationImpactTrigger.RequirementModified }).ReviewCycles.Single().SnapshotHash);
        Assert.NotEqual(baseline, SubmittedWithImpact(ImpactBase() with
        { ChangeRequestId = Guid.NewGuid() }).ReviewCycles.Single().SnapshotHash);
        Assert.NotEqual(baseline, SubmittedWithImpact(ImpactBase() with
        { RequirementChangeId = Guid.NewGuid() }).ReviewCycles.Single().SnapshotHash);
        Assert.NotEqual(baseline, SubmittedWithImpact(ImpactBase() with
        { RequirementRevisionId = Guid.NewGuid() }).ReviewCycles.Single().SnapshotHash);
        Assert.NotEqual(baseline, SubmittedWithImpact(ImpactBase() with
        { ProcedureId = Guid.NewGuid() }).ReviewCycles.Single().SnapshotHash);
        Assert.NotEqual(baseline, SubmittedWithImpact(ImpactBase() with
        { ResolvedProcedureId = Guid.NewGuid(), ResolvedProcedureRevisionId = Guid.NewGuid(),
            Outcome = VerificationImpactOutcome.ProcedureCoverageConfirmed,
            ProcedureChangeAction = TestProcedureChangeAction.LinkExisting })
            .ReviewCycles.Single().SnapshotHash);
        Assert.NotEqual(baseline, SubmittedWithImpact(ImpactBase() with
        { RetargetedRequirementRevisionId = Guid.NewGuid(), Outcome = VerificationImpactOutcome.ProcedureRetargeted,
            ProcedureChangeAction = TestProcedureChangeAction.LinkExisting })
            .ReviewCycles.Single().SnapshotHash);
    }

    [Fact]
    public void Impact_decision_order_is_canonical()
    {
        var first = ImpactBase();
        var second = ImpactBase() with { ItemId = Guid.NewGuid(), SubjectDisplayNumber = "SYSR-00000300.00" };
        var driving = Guid.NewGuid();
        var changeRequestId = Guid.NewGuid();

        var ordered = CreateWith(changeRequestId);
        ordered.AssignControlledNumber("SYSTCR-000063", Now.AddMinutes(1));
        ordered.AddProcedureChange("verification.engineer",
            ProcedureDraftWith(drivingJson: $"[\"{driving}\"]"), Now.AddMinutes(2));
        ordered.Submit("verification.engineer", "test.lead", true, Now.AddMinutes(3),
            impactDecisions: [first, second]);

        var reversed = CreateWith(changeRequestId);
        reversed.AssignControlledNumber("SYSTCR-000063", Now.AddMinutes(1));
        reversed.AddProcedureChange("verification.engineer",
            ProcedureDraftWith(drivingJson: $"[\"{driving}\"]"), Now.AddMinutes(2));
        reversed.Submit("verification.engineer", "test.lead", true, Now.AddMinutes(3),
            impactDecisions: [second, first]);

        Assert.Equal(ordered.ReviewCycles.Single().SnapshotHash,
            reversed.ReviewCycles.Single().SnapshotHash);
    }

    [Fact]
    public void Two_proposals_nobody_has_named_yet_can_sit_in_the_same_package()
    {
        // One proposal per procedure — but an unnamed proposal is not yet a procedure. Comparing empty base
        // numbers for equality refused the second with "already has a proposed change", naming a procedure
        // neither of them identified.
        var review = Create();
        review.AddProcedureChange("verification.engineer", Unfinished(), Now, allowIncomplete: true);
        review.AddProcedureChange("verification.engineer", Unfinished(), Now.AddMinutes(1), allowIncomplete: true);
        Assert.Equal(2, review.ProcedureChanges.Count);

        // Two proposals that do name the same procedure are still one too many.
        review.AddProcedureChange("verification.engineer", ProcedureDraft("SYSTP-000501"), Now.AddMinutes(2));
        Assert.Throws<DomainException>(() => review.AddProcedureChange("verification.engineer",
            ProcedureDraft("SYSTP-000501"), Now.AddMinutes(3)));
    }

    [Fact]
    public void An_unfinished_proposal_rests_in_a_package_and_review_refuses_it_by_name()
    {
        var review = Create();
        review.AddProcedureChange("verification.engineer",
            ProcedureDraftWith(rationale: "", baseNumber: "SYSTP-000502",
                drivingJson: $"[\"{Guid.NewGuid()}\"]"), Now);

        // Written down is fine. Asking somebody to sign it is not, and the refusal says which one.
        var error = Assert.Throws<DomainException>(() => review.SubmitForReview("verification.engineer",
            [new ChangeControl.ApproverSelection("approver", "Approver")], true, Now.AddMinutes(1)));
        Assert.Contains("SYSTP-000502", error.Message);
    }

    /// <summary>
    /// A driving requirement revision, so a submission fails on the thing under test rather than on the
    /// separate rule that an introduced procedure must name what it verifies.
    /// </summary>
    private static string DrivingJson() => $"[\"{Guid.NewGuid()}\"]";

    /// <summary>A proposal an engineer started and was interrupted in: a kind, and nothing else yet.</summary>
    private static TestProcedureChangeDraft Unfinished() =>
        new("", 0, TestProcedureLevel.System, TestProcedureChangeKind.Introduce,
            "", "", "", "", "", "", "[]");

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

    private static TestProcedureChangeDraft ProcedureDraftWith(string preconditions = "Preconditions",
        string rationale = "Why this procedure work is required.", string drivingJson = "[]",
        string baseNumber = "SYSTP-000123") =>
        new(baseNumber, 0, TestProcedureLevel.System, TestProcedureChangeKind.Introduce,
            "Oceanic waypoint sequencing", "Objective", preconditions, "Steps", "Expected", rationale, drivingJson);

    private static string SnapshotFor(TestProcedureChangeDraft draft, Guid? changeRequestId = null)
    {
        var review = changeRequestId is null ? Create() : CreateWith(changeRequestId.Value);
        review.AssignControlledNumber("SYSTCR-000055", Now.AddMinutes(1));
        review.AddProcedureChange("verification.engineer", draft, Now.AddMinutes(2));
        review.Submit("verification.engineer", "test.lead", true, Now.AddMinutes(3));
        return review.ReviewCycles.Single().SnapshotHash;
    }

    private static TestChangeReview CreateWith(Guid changeRequestId, string sourceNumber = "SRCR-00039.00")
    {
        var review = new TestChangeReview(Guid.NewGuid(), Guid.NewGuid(), changeRequestId,
            TestChangeReviewDiscipline.System, sourceNumber, Now);
        review.RecordTestChangeRequired("verification.engineer", Now);
        review.WriteCase("verification.engineer", "Verification case", "Problem", "Analysis", "Solution", Now);
        return review;
    }

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

        // Still refused by default: an API payload missing a procedure's objective is malformed, not
        // half-written, and the endpoint that builds a package from one must keep saying so.
        Assert.Throws<DomainException>(() => review.AddProcedureChange("verification.engineer",
            new TestProcedureChangeDraft("SYSTP-000010", 0, TestProcedureLevel.System,
                TestProcedureChangeKind.Introduce, "Title", "", "steps", "", "", "why", DrivingJson()),
            Now.AddMinutes(2)));

        // The check-in path asks for it explicitly, and then the same three rules are checked where they
        // mean something: an engineer stops mid-sentence, and an approver is never shown a procedure that
        // verifies nothing.
        review.AddProcedureChange("verification.engineer",
            new TestProcedureChangeDraft("SYSTP-000010", 0, TestProcedureLevel.System,
                TestProcedureChangeKind.Introduce, "Title", "", "steps", "", "", "why", DrivingJson()),
            Now.AddMinutes(2), allowIncomplete: true);
        var missingObjective = Assert.Throws<DomainException>(() => review.SubmitForReview("verification.engineer",
            [new ChangeControl.ApproverSelection("approver", "Approver")], true, Now.AddMinutes(5)));
        Assert.Contains("SYSTP-000010", missingObjective.Message);

        var noSteps = new TestChangeReview(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            TestChangeReviewDiscipline.System, "SRCR-00039.00", Now);
        noSteps.RecordTestChangeRequired("verification.engineer", Now);
        noSteps.WriteCase("verification.engineer", "Verification case", "Problem", "Analysis", "Solution", Now);
        noSteps.AddProcedureChange("verification.engineer",
            new TestProcedureChangeDraft("SYSTP-000011", 0, TestProcedureLevel.System,
                TestProcedureChangeKind.Introduce, "Title", "objective", "", "", "", "why", DrivingJson()),
            Now.AddMinutes(3), allowIncomplete: true);
        Assert.Throws<DomainException>(() => noSteps.SubmitForReview("verification.engineer",
            [new ChangeControl.ApproverSelection("approver", "Approver")], true, Now.AddMinutes(5)));

        // A procedure the build will carry still has to be called something — at review.
        var noTitle = new TestChangeReview(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            TestChangeReviewDiscipline.System, "SRCR-00039.00", Now);
        noTitle.RecordTestChangeRequired("verification.engineer", Now);
        noTitle.WriteCase("verification.engineer", "Verification case", "Problem", "Analysis", "Solution", Now);
        noTitle.AddProcedureChange("verification.engineer",
            new TestProcedureChangeDraft("SYSTP-000012", 0, TestProcedureLevel.System,
                TestProcedureChangeKind.Introduce, "", "objective", "", "steps", "expected", "why", DrivingJson()), Now.AddMinutes(4), allowIncomplete: true);
        Assert.Throws<DomainException>(() => noTitle.SubmitForReview("verification.engineer",
            [new ChangeControl.ApproverSelection("approver", "Approver")], true, Now.AddMinutes(5)));
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
        Assert.Equal(TestChangeReviewState.Draft, next.State);
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

        Assert.Equal(TestChangeReviewState.Draft, review.State);
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
    public void Current_change_required_package_cannot_be_submitted_without_a_procedure_decision()
    {
        var review = Create();

        var error = Assert.Throws<DomainException>(() =>
            review.Submit("verification.engineer", "test.approver", true, Now.AddMinutes(1)));

        Assert.Contains("names no procedure decisions", error.Message);
        Assert.Equal(TestChangeReviewState.Draft, review.State);
        Assert.Empty(review.ReviewCycles);
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

        Assert.Equal(TestChangeReviewState.Draft, review.State);
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

    /// <summary>
    /// Deferral, which a package could not do before: work the programme had dropped either sat in review
    /// holding a gate that would never clear, or was rejected — throwing away a review that raised no
    /// engineering objection.
    /// </summary>
    [Fact]
    public void A_deferred_package_remembers_how_far_it_had_got()
    {
        var review = Submittable();
        review.Submit("verification.engineer", "approver", true, Now.AddMinutes(1));
        Assert.Equal(TestChangeReviewState.InReview, review.State);

        review.Defer("Dropped from this build.", Now.AddMinutes(2));

        Assert.Equal(TestChangeReviewState.Deferred, review.State);
        Assert.Equal(TestChangeReviewState.InReview, review.DeferredFromState);
        Assert.Equal("Dropped from this build.", review.DeferralReason);
    }

    /// <summary>
    /// A package deferred out of review comes back as a Draft. The approvers were asked about work that has
    /// since been put away, so restoring the review would restore signatures against a snapshot nobody has
    /// looked at since.
    /// </summary>
    [Fact]
    public void Reinstating_from_review_returns_a_draft_to_be_submitted_again()
    {
        var review = Submittable();
        review.Submit("verification.engineer", "approver", true, Now.AddMinutes(1));
        review.Defer("Dropped from this build.", Now.AddMinutes(2));

        review.Reinstate(Now.AddMinutes(3));

        Assert.Equal(TestChangeReviewState.Draft, review.State);
        Assert.Null(review.DeferredFromState);
        Assert.Equal("", review.DeferralReason);
    }

    [Fact]
    public void Reinstating_an_approved_package_puts_it_back_as_approved()
    {
        var review = Submittable();
        review.Submit("verification.engineer", "approver", true, Now.AddMinutes(1));
        review.Approve("approver", "Signed.", Now.AddMinutes(2));
        review.Defer("Held for the next build.", Now.AddMinutes(3));

        review.Reinstate(Now.AddMinutes(4));

        // Approved work put away is still approved work. Coming back as a Draft would quietly discard a
        // signature somebody actually gave.
        Assert.Equal(TestChangeReviewState.Approved, review.State);
    }

    [Fact]
    public void A_deferred_package_cannot_be_edited_or_deferred_twice()
    {
        var review = Create();
        review.Defer("Not this build.", Now.AddMinutes(1));

        Assert.Throws<DomainException>(() => review.Defer("Again.", Now.AddMinutes(2)));
        Assert.Throws<DomainException>(() =>
            review.WriteCase("verification.engineer", "T", "P", "A", "S", Now.AddMinutes(2)));
    }

    /// <summary>
    /// Who raised it, which the register shows and which a package had no way to answer.
    ///
    /// Empty is the honest answer for the automatic ones: a package that exists because an assessment
    /// concluded test work is required was raised by nobody, and naming whoever was assigned afterwards would
    /// answer a different question.
    /// </summary>
    [Fact]
    public void A_package_records_who_raised_it_and_leaves_it_empty_when_nobody_did()
    {
        var automatic = Raised();
        Assert.Equal("", automatic.AuthorId);

        var byHand = new TestChangeReview(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            TestChangeReviewDiscipline.System, "SRCR-00039.00", Now, authorId: "test.engineer");
        Assert.Equal("test.engineer", byHand.AuthorId);
    }

    /// <summary>Revising is raising the next revision, so the engineer who revised it is its author.</summary>
    [Fact]
    public void A_revision_is_authored_by_whoever_revised_it()
    {
        var review = Submittable();
        review.Submit("verification.engineer", "approver", true, Now.AddMinutes(1));
        review.Approve("approver", "Signed.", Now.AddMinutes(2));

        var next = review.StartNextRevision("second.engineer", Now.AddMinutes(3), targetReleaseIsReleased: false);

        Assert.Equal("second.engineer", next.AuthorId);
        Assert.Equal(review.Revision + 1, next.Revision);
    }

    [Fact]
    public void Deferring_requires_a_reason_and_only_a_deferred_package_can_be_reinstated()
    {
        var review = Create();

        Assert.Throws<DomainException>(() => review.Defer("   ", Now.AddMinutes(1)));
        Assert.Throws<DomainException>(() => review.Reinstate(Now.AddMinutes(1)));
    }
}
