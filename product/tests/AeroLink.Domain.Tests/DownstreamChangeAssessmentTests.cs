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
}
