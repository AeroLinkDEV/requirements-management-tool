using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Common;
using AeroLink.Domain.Requirements;
using AeroLink.Domain.TeamWork;
using AeroLink.Domain.Verification;

namespace AeroLink.Domain.Tests;

public sealed class TeamWorkProjectionPolicyTests
{
    [Fact]
    public void Record_family_vocabulary_is_exactly_the_phase_one_surface()
    {
        Assert.Equal(
            [
                TeamWorkRecordFamily.SystemChangeRequest,
                TeamWorkRecordFamily.SoftwareChangeRequest,
                TeamWorkRecordFamily.InterfaceChangeRequest,
                TeamWorkRecordFamily.TestChangeReview,
                TeamWorkRecordFamily.ProblemReport,
                TeamWorkRecordFamily.Assessment,
            ],
            Enum.GetValues<TeamWorkRecordFamily>());
    }

    [Fact]
    public void Every_change_request_state_has_an_explicit_lane()
    {
        var expected = new Dictionary<ChangeRequestState, TeamWorkLaneDecision>
        {
            [ChangeRequestState.Draft] = TeamWorkLaneDecision.OnBoard(TeamWorkLane.InWork),
            [ChangeRequestState.InReview] = TeamWorkLaneDecision.OnBoard(TeamWorkLane.InReview),
            [ChangeRequestState.Approved] = TeamWorkLaneDecision.OnBoard(TeamWorkLane.Approved),
            [ChangeRequestState.Deferred] = TeamWorkLaneDecision.OffBoard,
            [ChangeRequestState.SelectedForBaseline] = TeamWorkLaneDecision.OnBoard(TeamWorkLane.Approved),
            [ChangeRequestState.Withdrawn] = TeamWorkLaneDecision.OffBoard,
        };

        Assert.Equal(Enum.GetValues<ChangeRequestState>(), expected.Keys);
        foreach (var state in Enum.GetValues<ChangeRequestState>())
        {
            var actual = state == ChangeRequestState.Deferred
                ? TeamWorkLanePolicy.ForChangeRequest(state, null)
                : TeamWorkLanePolicy.ForChangeRequest(state);
            Assert.Equal(expected[state], actual);
        }

        Assert.Equal(TeamWorkLane.InWork, TeamWorkLanePolicy.ForChangeRequest(
            ChangeRequestState.Deferred, ChangeRequestState.Draft).Lane);
        Assert.Equal(TeamWorkLane.InReview, TeamWorkLanePolicy.ForChangeRequest(
            ChangeRequestState.Deferred, ChangeRequestState.InReview).Lane);
        Assert.Equal(TeamWorkLane.Approved, TeamWorkLanePolicy.ForChangeRequest(
            ChangeRequestState.Deferred, ChangeRequestState.Approved).Lane);
        Assert.True(TeamWorkLanePolicy.ForChangeRequest(ChangeRequestState.Deferred, null).IsOffBoard);
    }

    [Fact]
    public void Every_test_change_review_state_has_an_explicit_lane()
    {
        var expected = new Dictionary<TestChangeReviewState, TeamWorkLaneDecision>
        {
            [TestChangeReviewState.Draft] = TeamWorkLaneDecision.OnBoard(TeamWorkLane.InWork),
            [TestChangeReviewState.InReview] = TeamWorkLaneDecision.OnBoard(TeamWorkLane.InReview),
            [TestChangeReviewState.Approved] = TeamWorkLaneDecision.OnBoard(TeamWorkLane.Approved),
            [TestChangeReviewState.Deferred] = TeamWorkLaneDecision.OffBoard,
            [TestChangeReviewState.Superseded] = TeamWorkLaneDecision.OffBoard,
        };

        Assert.Equal(Enum.GetValues<TestChangeReviewState>(), expected.Keys);
        foreach (var state in Enum.GetValues<TestChangeReviewState>())
        {
            var actual = state == TestChangeReviewState.Deferred
                ? TeamWorkLanePolicy.ForTestChangeReview(state, null)
                : TeamWorkLanePolicy.ForTestChangeReview(state);
            Assert.Equal(expected[state], actual);
        }

        Assert.Equal(TeamWorkLane.InReview, TeamWorkLanePolicy.ForTestChangeReview(
            TestChangeReviewState.Deferred, TestChangeReviewState.InReview).Lane);
        Assert.Equal(TeamWorkLane.Approved, TeamWorkLanePolicy.ForTestChangeReview(
            TestChangeReviewState.Deferred, TestChangeReviewState.Approved).Lane);
    }

    [Fact]
    public void Deferred_records_without_historical_lane_provenance_fail_closed()
    {
        Assert.True(TeamWorkLanePolicy.ForChangeRequest(ChangeRequestState.Deferred, null).IsOffBoard);
        Assert.True(TeamWorkLanePolicy.ForTestChangeReview(TestChangeReviewState.Deferred, null).IsOffBoard);
    }

    [Fact]
    public void Every_problem_report_state_has_an_explicit_lane()
    {
        var expected = new Dictionary<ProblemReportState, TeamWorkLaneDecision>
        {
            [ProblemReportState.Draft] = TeamWorkLaneDecision.OnBoard(TeamWorkLane.InWork),
            [ProblemReportState.ReadyForSccb] = TeamWorkLaneDecision.OnBoard(TeamWorkLane.InReview),
            [ProblemReportState.Open] = TeamWorkLaneDecision.OnBoard(TeamWorkLane.InWork),
            [ProblemReportState.Implementing] = TeamWorkLaneDecision.OnBoard(TeamWorkLane.InWork),
            [ProblemReportState.Verifying] = TeamWorkLaneDecision.OnBoard(TeamWorkLane.InWork),
            [ProblemReportState.WaitingForSqaToClose] = TeamWorkLaneDecision.OnBoard(TeamWorkLane.AwaitingSignature),
            [ProblemReportState.Closed] = TeamWorkLaneDecision.OffBoard,
            [ProblemReportState.Rejected] = TeamWorkLaneDecision.OffBoard,
        };

        Assert.Equal(Enum.GetValues<ProblemReportState>(), expected.Keys);
        foreach (var state in Enum.GetValues<ProblemReportState>())
            Assert.Equal(expected[state], TeamWorkLanePolicy.ForProblemReport(state));
    }

    public static TheoryData<DownstreamAssessmentState, DownstreamAssessmentOutcome, bool, TeamWorkLane?> AssessmentLanes =>
        new()
        {
            { DownstreamAssessmentState.Open, DownstreamAssessmentOutcome.Pending, false, TeamWorkLane.InWork },
            { DownstreamAssessmentState.Open, DownstreamAssessmentOutcome.ChangeRequired, false, TeamWorkLane.InWork },
            { DownstreamAssessmentState.Open, DownstreamAssessmentOutcome.NoChangeRequired, false, TeamWorkLane.InWork },
            { DownstreamAssessmentState.Open, DownstreamAssessmentOutcome.ChangeRequestsLinked, true, null },
            { DownstreamAssessmentState.InReview, DownstreamAssessmentOutcome.Pending, false, TeamWorkLane.InReview },
            { DownstreamAssessmentState.InReview, DownstreamAssessmentOutcome.ChangeRequired, false, TeamWorkLane.InReview },
            { DownstreamAssessmentState.InReview, DownstreamAssessmentOutcome.NoChangeRequired, false, TeamWorkLane.InReview },
            { DownstreamAssessmentState.InReview, DownstreamAssessmentOutcome.ChangeRequestsLinked, false, TeamWorkLane.InReview },
            { DownstreamAssessmentState.Approved, DownstreamAssessmentOutcome.Pending, false, TeamWorkLane.Approved },
            { DownstreamAssessmentState.Approved, DownstreamAssessmentOutcome.ChangeRequired, false, TeamWorkLane.Approved },
            { DownstreamAssessmentState.Approved, DownstreamAssessmentOutcome.NoChangeRequired, false, TeamWorkLane.Approved },
            { DownstreamAssessmentState.Approved, DownstreamAssessmentOutcome.ChangeRequestsLinked, true, null },
            { DownstreamAssessmentState.Superseded, DownstreamAssessmentOutcome.Pending, true, null },
            { DownstreamAssessmentState.Superseded, DownstreamAssessmentOutcome.ChangeRequired, true, null },
            { DownstreamAssessmentState.Superseded, DownstreamAssessmentOutcome.NoChangeRequired, true, null },
            { DownstreamAssessmentState.Superseded, DownstreamAssessmentOutcome.ChangeRequestsLinked, true, null },
        };

    [Theory]
    [MemberData(nameof(AssessmentLanes))]
    public void Every_assessment_state_and_outcome_pair_has_a_defined_lane(
        DownstreamAssessmentState state, DownstreamAssessmentOutcome outcome, bool offBoard, TeamWorkLane? lane)
    {
        var decision = TeamWorkLanePolicy.ForAssessment(state, outcome);
        Assert.Equal(offBoard, decision.IsOffBoard);
        Assert.Equal(lane, decision.Lane);
    }

    [Fact]
    public void Review_overlay_keeps_two_parallel_review_holders()
    {
        var result = TeamWorkReviewOverlay.Resolve(
        [
            new("reviewer.one", ReviewStageKind.Review, ApprovalStepState.Active),
            new("reviewer.two", ReviewStageKind.Review, ApprovalStepState.Active),
        ]);

        Assert.Equal(TeamWorkLane.InReview, result.LaneDecision.Lane);
        Assert.Equal(["reviewer.one", "reviewer.two"], result.HolderResolution.CurrentHolderIds);
        Assert.Equal(TeamWorkHolderBasis.ActiveReviewStage, result.HolderResolution.HolderBasis);
        Assert.Equal(
            [("reviewer.one", ReviewStageKind.Review), ("reviewer.two", ReviewStageKind.Review)],
            result.ActiveStageObligations.Select(obligation => (obligation.HolderId, obligation.StageKind)));
    }

    [Fact]
    public void Approval_stage_has_lane_precedence_but_mixed_parallel_holders_are_retained()
    {
        var result = TeamWorkReviewOverlay.Resolve(
        [
            new("reviewer.one", ReviewStageKind.Review, ApprovalStepState.Active),
            new("approver.one", ReviewStageKind.Approval, ApprovalStepState.Active),
        ]);

        Assert.Equal(TeamWorkLane.AwaitingSignature, result.LaneDecision.Lane);
        Assert.Equal(["reviewer.one", "approver.one"], result.HolderResolution.CurrentHolderIds);
        Assert.Equal(TeamWorkHolderBasis.ActiveReviewAndApprovalStages, result.HolderResolution.HolderBasis);
        Assert.Equal(
            [("reviewer.one", ReviewStageKind.Review), ("approver.one", ReviewStageKind.Approval)],
            result.ActiveStageObligations.Select(obligation => (obligation.HolderId, obligation.StageKind)));
    }

    [Fact]
    public void An_active_cycle_with_no_active_steps_is_in_review_with_no_holders()
    {
        var result = TeamWorkReviewOverlay.Resolve(
        [
            new("pending.reviewer", ReviewStageKind.Review, ApprovalStepState.Pending),
            new("returned.reviewer", ReviewStageKind.Approval, ApprovalStepState.Returned),
            new("approved.reviewer", ReviewStageKind.Review, ApprovalStepState.Approved),
        ]);

        Assert.Equal(TeamWorkLane.InReview, result.LaneDecision.Lane);
        Assert.Empty(result.HolderResolution.CurrentHolderIds);
        Assert.Equal(TeamWorkHolderBasis.None, result.HolderResolution.HolderBasis);
        Assert.Empty(result.ActiveStageObligations);
    }

    [Fact]
    public void Parallel_approval_obligations_keep_each_signature_actor()
    {
        var result = TeamWorkReviewOverlay.Resolve(
        [
            new("approver.one", ReviewStageKind.Approval, ApprovalStepState.Active),
            new("approver.two", ReviewStageKind.Approval, ApprovalStepState.Active),
        ]);

        Assert.Equal(TeamWorkLane.AwaitingSignature, result.LaneDecision.Lane);
        Assert.Equal(["approver.one", "approver.two"], result.HolderResolution.CurrentHolderIds);
        Assert.All(result.ActiveStageObligations, obligation => Assert.Equal(ReviewStageKind.Approval, obligation.StageKind));
    }

    [Fact]
    public void Review_overlay_deduplicates_the_same_person_without_losing_multi_holder_semantics()
    {
        var result = TeamWorkReviewOverlay.Resolve(
        [
            new("reviewer.one", ReviewStageKind.Review, ApprovalStepState.Active),
            new("REVIEWER.ONE", ReviewStageKind.Review, ApprovalStepState.Active),
        ]);

        Assert.Equal(["reviewer.one"], result.HolderResolution.CurrentHolderIds);
        Assert.Single(result.ActiveStageObligations);
        Assert.Equal("reviewer.one", result.ActiveStageObligations[0].HolderId);
    }

    [Fact]
    public void Holder_policy_is_family_specific_and_does_not_fallback_to_author_or_roles()
    {
        Assert.Equal(
            ["change.author"],
            TeamWorkHolderPolicy.ForChangeRequest(ChangeRequestState.Draft, "change.author").CurrentHolderIds);
        Assert.Empty(TeamWorkHolderPolicy.ForChangeRequest(ChangeRequestState.Approved, "change.author").CurrentHolderIds);
        Assert.Empty(TeamWorkHolderPolicy.ForChangeRequest(
            ChangeRequestState.Deferred, "change.author").CurrentHolderIds);

        Assert.Equal(
            ["assigned.engineer"],
            TeamWorkHolderPolicy.ForTestChangeReview(TestChangeReviewState.Draft, "assigned.engineer").CurrentHolderIds);
        Assert.Empty(TeamWorkHolderPolicy.ForTestChangeReview(TestChangeReviewState.Approved, "assigned.engineer").CurrentHolderIds);

        Assert.Empty(TeamWorkHolderPolicy.ForProblemReport(ProblemReportState.ReadyForSccb, "responsible.engineer").CurrentHolderIds);
        Assert.Empty(TeamWorkHolderPolicy.ForProblemReport(ProblemReportState.WaitingForSqaToClose, "responsible.engineer").CurrentHolderIds);

        Assert.Equal(
            ["selected.approver"],
            TeamWorkHolderPolicy.ForAssessment(
                DownstreamAssessmentState.InReview, DownstreamAssessmentOutcome.NoChangeRequired,
                "assigned.engineer", "selected.approver").CurrentHolderIds);
        Assert.Empty(TeamWorkHolderPolicy.ForAssessment(
            DownstreamAssessmentState.InReview, DownstreamAssessmentOutcome.NoChangeRequired,
            "assigned.engineer", null).CurrentHolderIds);
        Assert.Empty(TeamWorkHolderPolicy.ForAssessment(
            DownstreamAssessmentState.Approved, DownstreamAssessmentOutcome.NoChangeRequired,
            "assigned.engineer", "selected.approver").CurrentHolderIds);
    }

    [Fact]
    public void Unassigned_obligation_fields_retain_their_authoritative_basis_without_inventing_a_holder()
    {
        var draftTcr = TeamWorkHolderPolicy.ForTestChangeReview(TestChangeReviewState.Draft, null);
        Assert.Empty(draftTcr.CurrentHolderIds);
        Assert.Equal(TeamWorkHolderBasis.AssignedEngineer, draftTcr.HolderBasis);

        var openAssessment = TeamWorkHolderPolicy.ForAssessment(
            DownstreamAssessmentState.Open, DownstreamAssessmentOutcome.Pending, null, "ignored.approver");
        Assert.Empty(openAssessment.CurrentHolderIds);
        Assert.Equal(TeamWorkHolderBasis.AssignedEngineer, openAssessment.HolderBasis);

        var reviewAssessment = TeamWorkHolderPolicy.ForAssessment(
            DownstreamAssessmentState.InReview, DownstreamAssessmentOutcome.NoChangeRequired, "ignored.engineer", null);
        Assert.Empty(reviewAssessment.CurrentHolderIds);
        Assert.Equal(TeamWorkHolderBasis.SelectedAssessmentApprover, reviewAssessment.HolderBasis);
    }

    [Fact]
    public void Holder_policy_exhaustively_handles_every_native_state()
    {
        foreach (var state in Enum.GetValues<ChangeRequestState>())
        {
            var holders = TeamWorkHolderPolicy.ForChangeRequest(
                state, "author", [new TeamWorkReviewStep("reviewer", ReviewStageKind.Review, ApprovalStepState.Active)]);
            if (state == ChangeRequestState.Draft)
                Assert.Equal(["author"], holders.CurrentHolderIds);
            else if (state == ChangeRequestState.InReview)
                Assert.Equal(["reviewer"], holders.CurrentHolderIds);
            else
                Assert.Empty(holders.CurrentHolderIds);
        }

        foreach (var state in Enum.GetValues<TestChangeReviewState>())
        {
            var holders = TeamWorkHolderPolicy.ForTestChangeReview(
                state, "assigned", [new TeamWorkReviewStep("reviewer", ReviewStageKind.Review, ApprovalStepState.Active)]);
            if (state == TestChangeReviewState.Draft)
                Assert.Equal(["assigned"], holders.CurrentHolderIds);
            else if (state == TestChangeReviewState.InReview)
                Assert.Equal(["reviewer"], holders.CurrentHolderIds);
            else
                Assert.Empty(holders.CurrentHolderIds);
        }

        foreach (var state in Enum.GetValues<ProblemReportState>())
        {
            var holders = TeamWorkHolderPolicy.ForProblemReport(state, "responsible");
            if (state is ProblemReportState.Draft or ProblemReportState.Open
                or ProblemReportState.Implementing or ProblemReportState.Verifying)
                Assert.Equal(["responsible"], holders.CurrentHolderIds);
            else
                Assert.Empty(holders.CurrentHolderIds);
        }

        foreach (var state in Enum.GetValues<DownstreamAssessmentState>())
        foreach (var outcome in Enum.GetValues<DownstreamAssessmentOutcome>())
        {
            var holders = TeamWorkHolderPolicy.ForAssessment(state, outcome, "assigned", "selected");
            if (state == DownstreamAssessmentState.Open && outcome != DownstreamAssessmentOutcome.ChangeRequestsLinked)
                Assert.Equal(["assigned"], holders.CurrentHolderIds);
            else if (state == DownstreamAssessmentState.InReview)
                Assert.Equal(["selected"], holders.CurrentHolderIds);
            else
                Assert.Empty(holders.CurrentHolderIds);
        }
    }

    [Fact]
    public void Deferred_rows_have_no_current_holder_even_when_the_prior_lane_is_approved()
    {
        var lane = TeamWorkLanePolicy.ForChangeRequest(ChangeRequestState.Deferred, ChangeRequestState.Approved);
        Assert.Equal(TeamWorkLane.Approved, lane.Lane);
        Assert.True(lane.IsDeferred);
        Assert.Empty(TeamWorkHolderPolicy.ForChangeRequest(ChangeRequestState.Deferred, "author").CurrentHolderIds);

        var tcrLane = TeamWorkLanePolicy.ForTestChangeReview(TestChangeReviewState.Deferred, TestChangeReviewState.Approved);
        Assert.Equal(TeamWorkLane.Approved, tcrLane.Lane);
        Assert.True(tcrLane.IsDeferred);
        Assert.Empty(TeamWorkHolderPolicy.ForTestChangeReview(TestChangeReviewState.Deferred, "engineer").CurrentHolderIds);
    }

    [Fact]
    public void Invalid_native_and_deferred_states_fail_loudly()
    {
        Assert.Throws<DomainException>(() => TeamWorkLanePolicy.ForChangeRequest((ChangeRequestState)999));
        Assert.Throws<DomainException>(() => TeamWorkLanePolicy.ForTestChangeReview((TestChangeReviewState)999));
        Assert.Throws<DomainException>(() => TeamWorkLanePolicy.ForProblemReport((ProblemReportState)999));
        Assert.Throws<DomainException>(() => TeamWorkLanePolicy.ForAssessment(
            (DownstreamAssessmentState)999, DownstreamAssessmentOutcome.Pending));
        Assert.Throws<DomainException>(() => TeamWorkLanePolicy.ForAssessment(
            DownstreamAssessmentState.Open, (DownstreamAssessmentOutcome)999));
        Assert.Throws<DomainException>(() => TeamWorkLanePolicy.ForChangeRequest(
            ChangeRequestState.Deferred, ChangeRequestState.Withdrawn));
        Assert.Throws<DomainException>(() => TeamWorkLanePolicy.ForChangeRequest(
            ChangeRequestState.Deferred, ChangeRequestState.SelectedForBaseline));
        Assert.Throws<DomainException>(() => TeamWorkLanePolicy.ForTestChangeReview(
            TestChangeReviewState.Deferred, TestChangeReviewState.Superseded));
        Assert.Throws<DomainException>(() => TeamWorkReviewOverlay.Resolve(
            [new("holder", (ReviewStageKind)999, ApprovalStepState.Active)]));
        Assert.Throws<DomainException>(() => TeamWorkReviewOverlay.Resolve(
            [new("holder", ReviewStageKind.Review, (ApprovalStepState)999)]));
    }
}
