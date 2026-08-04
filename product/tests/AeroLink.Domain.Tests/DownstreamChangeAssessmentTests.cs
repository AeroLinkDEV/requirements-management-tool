using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Common;

namespace AeroLink.Domain.Tests;

public sealed class DownstreamChangeAssessmentTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 31, 12, 0, 0, TimeSpan.Zero);
    private static DownstreamChangeAssessment Create() => new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
        "SCR-00032.00", RequirementLevel.HighLevel, Now);

    [Fact]
    public void Assigned_engineer_can_conclude_that_no_downstream_change_is_required()
    {
        var assessment = Create();
        assessment.Assign("software.lead", "software.engineer", Now.AddMinutes(1));
        assessment.RecordNoChange("software.engineer", "The existing HLR behavior already satisfies the approved System change.", Now.AddMinutes(2));
        assessment.Submit("software.engineer", "assurance.reviewer", Now.AddMinutes(3));
        assessment.Approve("assurance.reviewer", Now.AddMinutes(4));

        Assert.Equal(DownstreamAssessmentOutcome.NoChangeRequired, assessment.Outcome);
        Assert.Equal(DownstreamAssessmentState.Approved, assessment.State);
    }

    [Fact]
    public void Assessment_supports_several_downstream_change_requests()
    {
        var assessment = Create();
        assessment.Assign("software.lead", "software.engineer", Now.AddMinutes(1));
        assessment.LinkChangeRequest("software.engineer", Guid.NewGuid(), "SWCR-00077.00", Now.AddMinutes(2));
        assessment.LinkChangeRequest("software.engineer", Guid.NewGuid(), "SWCR-00078.00", Now.AddMinutes(3));

        Assert.Equal(2, assessment.ChangeRequestLinks.Count);
        Assert.Equal(DownstreamAssessmentOutcome.ChangeRequestsLinked, assessment.Outcome);
    }

    [Fact]
    public void Change_required_is_honest_pending_work_and_cannot_be_submitted_without_an_SWCR()
    {
        var assessment = Create();
        assessment.Assign("software.lead", "software.engineer", Now.AddMinutes(1));
        assessment.RecordChangeRequired("software.engineer", Now.AddMinutes(2));

        Assert.Equal(DownstreamAssessmentOutcome.ChangeRequired, assessment.Outcome);
        Assert.Throws<DomainException>(() => assessment.Submit("software.engineer", "assurance.reviewer", Now.AddMinutes(3)));

        assessment.LinkChangeRequest("software.engineer", Guid.NewGuid(), "SWCR-00079.00", Now.AddMinutes(4));
        assessment.Submit("software.engineer", "assurance.reviewer", Now.AddMinutes(5));
        Assert.Equal(DownstreamAssessmentState.InReview, assessment.State);
    }

    [Fact]
    public void Superseded_assessment_retains_history_but_refuses_new_work()
    {
        var assessment = Create();
        assessment.Assign("software.lead", "software.engineer", Now.AddMinutes(1));
        assessment.RecordNoChange("software.engineer", "No HLR change required for the original wording.", Now.AddMinutes(2));
        assessment.Supersede(Guid.NewGuid(), "SCR-00032.01 revised the approved source; reassessment is required.", Now.AddMinutes(3));

        Assert.Equal(DownstreamAssessmentState.Superseded, assessment.State);
        Assert.Contains("reassessment", assessment.SupersededReason);
        Assert.Throws<DomainException>(() => assessment.RecordNoChange("software.engineer", "Still none.", Now.AddMinutes(4)));
    }

    [Fact]
    public void Only_the_named_independent_approver_can_approve()
    {
        var assessment = Create();
        assessment.Assign("software.lead", "software.engineer", Now.AddMinutes(1));
        assessment.RecordNoChange("software.engineer", "No HLR change required.", Now.AddMinutes(2));
        assessment.Submit("software.engineer", "assurance.reviewer", Now.AddMinutes(3));

        Assert.Throws<DomainException>(() => assessment.Approve("another.approver", Now.AddMinutes(4)));
        Assert.Throws<DomainException>(() => Create().Submit("software.engineer", "software.engineer", Now));
    }

    [Fact]
    public void Returned_review_rationale_survives_linking_additional_change_work()
    {
        var assessment = Create();
        assessment.Assign("software.lead", "software.engineer", Now.AddMinutes(1));
        assessment.RecordChangeRequired("software.engineer", Now.AddMinutes(2));
        assessment.LinkChangeRequest("software.engineer", Guid.NewGuid(), "SWCR-00079.00", Now.AddMinutes(3));
        assessment.Submit("software.engineer", "assurance.reviewer", Now.AddMinutes(4));
        assessment.ReturnToWork("assurance.reviewer", "Clarify the allocation before approval.", Now.AddMinutes(5));

        assessment.LinkChangeRequest("software.engineer", Guid.NewGuid(), "SWCR-00080.00", Now.AddMinutes(6));

        Assert.Equal("Clarify the allocation before approval.", assessment.Rationale);
        Assert.Equal(2, assessment.ChangeRequestLinks.Count);
    }

    [Fact]
    public void Recorded_conclusion_names_the_engineer_who_reached_it()
    {
        var assessment = Create();
        assessment.Assign("software.lead", "software.engineer", Now.AddMinutes(1));
        Assert.Null(assessment.DecidedBy);

        assessment.RecordNoChange("software.engineer", "The existing HLR behavior already satisfies the change.", Now.AddMinutes(2));

        Assert.Equal("software.engineer", assessment.DecidedBy);
        Assert.Equal(Now.AddMinutes(2), assessment.DecidedAt);
    }

    [Fact]
    public void Reopening_withdraws_the_conclusion_and_keeps_what_it_carried()
    {
        var assessment = Create();
        assessment.Assign("software.lead", "software.engineer", Now.AddMinutes(1));
        assessment.RecordChangeRequired("software.engineer", Now.AddMinutes(2));
        assessment.LinkChangeRequest("software.engineer", Guid.NewGuid(), "SWCR-00079.00", Now.AddMinutes(3));

        var reopening = assessment.Reopen("software.engineer", "The linked SWCR answers a different System change.", Now.AddMinutes(4));

        // The assessment is genuinely undecided again — not merely relabelled.
        Assert.Equal(DownstreamAssessmentOutcome.Pending, assessment.Outcome);
        Assert.Equal(DownstreamAssessmentState.Open, assessment.State);
        Assert.Empty(assessment.ChangeRequestLinks);
        Assert.Null(assessment.DecidedBy);
        Assert.Equal("software.engineer", assessment.AssignedEngineerId);
        // And what it used to hold survives the correction rather than being overwritten by it.
        Assert.Equal(DownstreamAssessmentOutcome.ChangeRequestsLinked, reopening.PreviousOutcome);
        Assert.Equal("software.engineer", reopening.PreviousDecidedBy);
        Assert.Equal("SWCR-00079.00", reopening.DetachedChangeRequestNumbers);
        Assert.Contains("different System change", reopening.Reason);
    }

    [Fact]
    public void An_approved_conclusion_can_be_withdrawn_and_remembers_that_it_was_approved()
    {
        var assessment = Create();
        assessment.Assign("software.lead", "software.engineer", Now.AddMinutes(1));
        assessment.RecordNoChange("software.engineer", "No HLR change required.", Now.AddMinutes(2));
        assessment.Submit("software.engineer", "assurance.reviewer", Now.AddMinutes(3));
        assessment.Approve("assurance.reviewer", Now.AddMinutes(4));

        var reopening = assessment.Reopen("assurance.reviewer", "A later reading of the System change shows an HLR gap.", Now.AddMinutes(5));

        Assert.Equal(DownstreamAssessmentState.Open, assessment.State);
        Assert.Null(assessment.ApprovedBy);
        Assert.Null(assessment.SelectedApproverId);
        Assert.Equal("", assessment.Rationale);
        Assert.Equal(DownstreamAssessmentState.Approved, reopening.PreviousState);
        Assert.Equal("assurance.reviewer", reopening.PreviousApprovedBy);
        Assert.Equal("No HLR change required.", reopening.PreviousRationale);
        Assert.Equal("", reopening.DetachedChangeRequestNumbers);
    }

    [Fact]
    public void Reopening_refuses_where_there_is_nothing_to_withdraw_or_the_work_is_not_yours()
    {
        var untouched = Create();
        untouched.Assign("software.lead", "software.engineer", Now.AddMinutes(1));
        // Nothing has been concluded, so there is no answer to withdraw.
        Assert.Throws<DomainException>(() => untouched.Reopen("software.engineer", "Changed my mind.", Now.AddMinutes(2)));

        var mine = Create();
        mine.Assign("software.lead", "software.engineer", Now.AddMinutes(1));
        mine.RecordChangeRequired("software.engineer", Now.AddMinutes(2));
        Assert.Throws<DomainException>(() => mine.Reopen("someone.else", "I disagree.", Now.AddMinutes(3)));
        Assert.Throws<DomainException>(() => mine.Reopen("software.engineer", "  ", Now.AddMinutes(3)));

        // An assessment sitting with its approver is returned, not reopened behind their back.
        var inReview = Create();
        inReview.Assign("software.lead", "software.engineer", Now.AddMinutes(1));
        inReview.RecordNoChange("software.engineer", "No HLR change required.", Now.AddMinutes(2));
        inReview.Submit("software.engineer", "assurance.reviewer", Now.AddMinutes(3));
        Assert.Throws<DomainException>(() => inReview.Reopen("software.engineer", "Withdrawing it.", Now.AddMinutes(4)));

        var superseded = Create();
        superseded.Assign("software.lead", "software.engineer", Now.AddMinutes(1));
        superseded.RecordNoChange("software.engineer", "No HLR change required.", Now.AddMinutes(2));
        superseded.Supersede(Guid.NewGuid(), "SCR-00032.01 replaced the source.", Now.AddMinutes(3));
        Assert.Throws<DomainException>(() => superseded.Reopen("software.engineer", "Reassessing.", Now.AddMinutes(4)));
    }

    [Fact]
    public void A_withdrawn_assessment_can_be_answered_again_from_scratch()
    {
        var assessment = Create();
        assessment.Assign("software.lead", "software.engineer", Now.AddMinutes(1));
        assessment.RecordNoChange("software.engineer", "No HLR change required.", Now.AddMinutes(2));
        assessment.Submit("software.engineer", "assurance.reviewer", Now.AddMinutes(3));
        assessment.Approve("assurance.reviewer", Now.AddMinutes(4));
        assessment.Reopen("assurance.reviewer", "An HLR gap was found after approval.", Now.AddMinutes(5));

        assessment.RecordChangeRequired("software.engineer", Now.AddMinutes(6));
        assessment.LinkChangeRequest("software.engineer", Guid.NewGuid(), "SWCR-00081.00", Now.AddMinutes(7));
        assessment.Submit("software.engineer", "assurance.reviewer", Now.AddMinutes(8));
        assessment.Approve("assurance.reviewer", Now.AddMinutes(9));

        Assert.Equal(DownstreamAssessmentState.Approved, assessment.State);
        Assert.Equal(DownstreamAssessmentOutcome.ChangeRequestsLinked, assessment.Outcome);
    }
}
