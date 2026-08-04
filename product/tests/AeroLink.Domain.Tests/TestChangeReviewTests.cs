using AeroLink.Domain.Common;
using AeroLink.Domain.Verification;

namespace AeroLink.Domain.Tests;

public sealed class TestChangeReviewTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 29, 12, 0, 0, TimeSpan.Zero);

    private static TestChangeReview Create(TestChangeReviewDiscipline discipline = TestChangeReviewDiscipline.System) =>
        new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), discipline, "SRCR-00039.00", Now);

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
        var review = Create();

        Assert.Throws<DomainException>(() => review.Submit("test.engineer", "test.approver", false, Now.AddMinutes(1)));
        review.Submit("test.engineer", "test.approver", true, Now.AddMinutes(2));

        Assert.Equal(TestChangeReviewState.InReview, review.State);
        Assert.Equal("test.engineer", review.SubmittedBy);
    }

    [Fact]
    public void Independent_approval_records_rationale_and_closes_the_review()
    {
        var review = Create();
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
        var review = Create();
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
        var review = Create();
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
