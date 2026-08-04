using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Common;

namespace AeroLink.Domain.Tests;

/// <summary>
/// A review that should not be running can be stopped, and says why it was.
///
/// `RequestChanges` was the only way back to Draft and only the reviewer whose turn it was could use it. An
/// author who submitted too early had to ask that reviewer to reject work everybody already knew was going
/// to change.
/// </summary>
public sealed class CancelReviewTests
{
    private static SystemChangeRequest InReview()
    {
        var now = DateTimeOffset.UtcNow;
        var scr = new SystemChangeRequest("SRCR-00001", 0, Guid.NewGuid(), Guid.NewGuid(), "Governed change",
            "Problem", "Analysis", "Solution", "change.author", now);
        scr.AddRequirementChange("change.author", "SYSR-00000001", 0, RequirementLevel.System,
            RequirementChangeKind.Introduce, "The FMS shall hold its course.", "Rationale.", "Test", now,
            targetSectionId: Guid.NewGuid());
        scr.SubmitForReview("change.author", [new ApproverSelection("first.reviewer", "First Reviewer"),
            new ApproverSelection("second.reviewer", "Second Reviewer")], now);
        return scr;
    }

    [Fact]
    public void Cancelling_returns_it_to_draft_at_the_same_revision()
    {
        var scr = InReview();
        var revision = scr.Revision;

        scr.CancelReview("change.author", "Superseded by a wider change.", DateTimeOffset.UtcNow);

        Assert.Equal(ScrState.Draft, scr.State);
        // The same revision, because cancelling a review is not a rejection of the record. A new revision
        // would strand every reference to this one for a decision nobody made about its content.
        Assert.Equal(revision, scr.Revision);
        Assert.Null(scr.ActiveReviewCycle);
    }

    [Fact]
    public void The_cancelled_cycle_keeps_its_steps_and_records_why_it_stopped()
    {
        var scr = InReview();
        scr.CancelReview("change.author", "Superseded by a wider change.", DateTimeOffset.UtcNow);

        var cycle = scr.ReviewCycles.Single();
        Assert.Equal(ReviewCycleState.Cancelled, cycle.State);
        Assert.Equal("Superseded by a wider change.", cycle.ClosureReason);
        // The approvers stay on the record. Who was being waited on when a review was stopped is part of
        // what happened, and a cancelled cycle that lists nobody cannot answer it.
        Assert.Equal(2, cycle.Steps.Count);
    }

    /// <summary>
    /// Cancelled and "changes requested" both land in Draft, and a reader has to be able to tell which
    /// happened: one is a reviewer's judgement on the content, the other is a decision to stop asking.
    /// </summary>
    [Fact]
    public void Cancelling_is_recorded_as_its_own_event_distinct_from_requesting_changes()
    {
        var scr = InReview();
        scr.CancelReview("change.author", "Superseded by a wider change.", DateTimeOffset.UtcNow);

        var audit = scr.AuditEvents.Last();
        Assert.Equal("ReviewCancelled", audit.EventType);
        Assert.Equal("change.author", audit.ActorId);
        Assert.Contains("Superseded by a wider change.", audit.Detail);
        Assert.Contains("same revision", audit.Detail);
        Assert.DoesNotContain(scr.AuditEvents, x => x.EventType == "ChangesRequested");
    }

    [Fact]
    public void A_review_cannot_be_cancelled_without_a_reason()
    {
        var scr = InReview();
        // Without one, the next reader cannot tell whether it was withdrawn, superseded, or stopped by
        // accident — and the person who knew is the one who just left.
        var error = Assert.Throws<DomainException>(() => scr.CancelReview("change.author", "   ", DateTimeOffset.UtcNow));
        Assert.Contains("why", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(ScrState.InReview, scr.State);
    }

    [Fact]
    public void Only_a_change_request_in_review_has_a_review_to_cancel()
    {
        var now = DateTimeOffset.UtcNow;
        var draft = new SystemChangeRequest("SRCR-00002", 0, Guid.NewGuid(), Guid.NewGuid(), "Draft change",
            "Problem", "Analysis", "Solution", "change.author", now);

        Assert.Throws<DomainException>(() => draft.CancelReview("change.author", "No longer needed.", now));
    }

    [Fact]
    public void Cancelling_leaves_the_change_request_submittable_again()
    {
        var scr = InReview();
        var now = DateTimeOffset.UtcNow;
        scr.CancelReview("change.author", "Submitted too early.", now);

        // The point of returning to Draft rather than to some terminal state: the work continues.
        scr.SubmitForReview("change.author", [new ApproverSelection("first.reviewer", "First Reviewer")], now);
        Assert.Equal(ScrState.InReview, scr.State);
        Assert.Equal(2, scr.ReviewCycles.Count);
    }
}
