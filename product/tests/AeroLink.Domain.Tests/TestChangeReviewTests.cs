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
