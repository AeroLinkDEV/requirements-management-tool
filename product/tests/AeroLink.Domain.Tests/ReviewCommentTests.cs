using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Common;
using AeroLink.Domain.Requirements;

namespace AeroLink.Domain.Tests;

/// <summary>
/// Comments are the one thing on a review that is not evidence, and the rules about who can see them are
/// what keeps that true. What is asserted here is that a draft reaches nobody, that deciding is the only
/// thing which publishes, and that a cycle ending under a reviewer does not throw their writing away.
/// </summary>
public sealed class ReviewCommentTests
{
    private static readonly Guid ProjectId = Guid.NewGuid();
    private static readonly Guid ReleaseId = Guid.NewGuid();
    private static readonly DateTimeOffset Now = new(2026, 8, 12, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_comment_is_a_draft_until_its_author_decides()
    {
        var scr = InReview();
        var comment = scr.AddReviewComment("systems", ReviewCommentAnchor.ChangeCase, null,
            "The analysis never rules out bus settling time.", Now);

        Assert.Equal(ReviewCommentState.Draft, comment.State);
        Assert.Null(comment.PublishedAt);

        scr.ApproveActiveStage("systems", Now.AddHours(1));

        Assert.Equal(ReviewCommentState.Published, comment.State);
        Assert.True(comment.DecisionRecorded);
        Assert.Equal(Now.AddHours(1), comment.PublishedAt);
    }

    [Fact]
    public void Approving_publishes_only_your_own_comments_and_leaves_the_cycle_running()
    {
        var scr = InReview(ReviewMode.Parallel);
        var mine = scr.AddReviewComment("systems", ReviewCommentAnchor.ChangeCase, null, "Mine.", Now);
        var theirs = scr.AddReviewComment("software", ReviewCommentAnchor.ChangeCase, null, "Theirs.", Now);

        scr.ApproveActiveStage("systems", Now.AddHours(1));

        Assert.Equal(ReviewCommentState.Published, mine.State);
        // The other reviewer has not decided, so their draft is still theirs alone — and nothing about one
        // reviewer signing may expose what another is still weighing up.
        Assert.Equal(ReviewCommentState.Draft, theirs.State);
        Assert.Equal(ChangeRequestState.InReview, scr.State);
    }

    [Fact]
    public void A_return_publishes_the_returning_reviewers_comments_with_their_decision()
    {
        var scr = InReview();
        var comment = scr.AddReviewComment("systems", ReviewCommentAnchor.ChangeCase, null,
            "1.5s is asserted, not derived.", Now);

        scr.RequestChanges("systems", "The budget is asserted rather than derived.", Now.AddHours(2));

        Assert.Equal(ReviewCommentState.Published, comment.State);
        Assert.True(comment.DecisionRecorded);
    }

    [Fact]
    public void A_reviewer_who_never_got_to_decide_does_not_lose_what_they_wrote()
    {
        var scr = InReview(ReviewMode.Parallel);
        var stranded = scr.AddReviewComment("verification", ReviewCommentAnchor.ChangeCase, null,
            "163 has no procedure, so the release gate will hold the whole build.", Now);

        // A colleague returns the package first. That closes the cycle for everybody, so this reviewer never
        // records a decision — but the author is about to revise, and this is the observation that predicts
        // the gate hold.
        scr.RequestChanges("systems", "Settle the tolerance first.", Now.AddHours(2));

        Assert.Equal(ReviewCommentState.Published, stranded.State);
        Assert.False(stranded.DecisionRecorded);
    }

    [Fact]
    public void A_cancelled_review_still_hands_over_what_was_written()
    {
        var scr = InReview();
        var comment = scr.AddReviewComment("systems", ReviewCommentAnchor.ChangeCase, null, "Worth a look.", Now);

        scr.CancelReview("author", "Submitted too early.", Now.AddHours(1));

        Assert.Equal(ReviewCommentState.Published, comment.State);
        Assert.False(comment.DecisionRecorded);
    }

    [Fact]
    public void Only_the_author_can_change_a_comment_and_only_while_it_is_a_draft()
    {
        var scr = InReview();
        var comment = scr.AddReviewComment("systems", ReviewCommentAnchor.ChangeCase, null, "First thought.", Now);

        Assert.Throws<DomainException>(() => scr.ReviseReviewComment("software", comment.Id, "Not yours.", Now));

        scr.ReviseReviewComment("systems", comment.Id, "Second thought.", Now.AddMinutes(5));
        Assert.Equal("Second thought.", comment.Body);

        scr.ApproveActiveStage("systems", Now.AddHours(1));
        // The author is reading it now. Editing underneath them would make the record of the exchange untrue.
        Assert.Throws<DomainException>(() => scr.ReviseReviewComment("systems", comment.Id, "Third.", Now.AddHours(2)));
    }

    [Fact]
    public void A_comment_must_name_something_that_is_actually_in_the_package()
    {
        var scr = InReview();

        Assert.Throws<DomainException>(() => scr.AddReviewComment("systems",
            ReviewCommentAnchor.RequirementRevision, Guid.NewGuid(), "About a revision from elsewhere.", Now));

        var revision = scr.RequirementChanges.First().Id;
        var good = scr.AddReviewComment("systems", ReviewCommentAnchor.RequirementRevision, revision,
            "Tolerance is not stated.", Now);
        Assert.Equal(revision, good.RequirementChangeId);
    }

    [Fact]
    public void The_anchor_and_its_identifier_have_to_agree()
    {
        var scr = InReview();
        var revision = scr.RequirementChanges.First().Id;

        Assert.Throws<DomainException>(() => scr.AddReviewComment("systems",
            ReviewCommentAnchor.RequirementRevision, null, "No revision named.", Now));
        Assert.Throws<DomainException>(() => scr.AddReviewComment("systems",
            ReviewCommentAnchor.ChangeCase, revision, "Change case, but carrying a revision.", Now));
    }

    [Fact]
    public void Somebody_who_is_not_reviewing_cannot_comment()
    {
        var scr = InReview();
        Assert.Throws<DomainException>(() => scr.AddReviewComment("author",
            ReviewCommentAnchor.ChangeCase, null, "The author's own note.", Now));
    }

    [Fact]
    public void A_later_reviewer_may_write_before_their_stage_is_reached()
    {
        // Sequential: only "systems" is active. Reading ahead is normal, and refusing the third reviewer a
        // place to record what they found only means it arrives by some other route, or not at all.
        var scr = InReview();
        var early = scr.AddReviewComment("verification", ReviewCommentAnchor.ChangeCase, null,
            "Noticed while reading ahead.", Now);

        Assert.Equal(ReviewCommentState.Draft, early.State);
        scr.ApproveActiveStage("systems", Now.AddHours(1));
        // Somebody else's approval is not this reviewer's decision, so it stays a draft.
        Assert.Equal(ReviewCommentState.Draft, early.State);
    }

    [Fact]
    public void A_reviewer_still_deciding_cannot_read_what_a_colleague_already_signed()
    {
        var scr = InReview(ReviewMode.Parallel);
        scr.AddReviewComment("systems", ReviewCommentAnchor.ChangeCase, null, "Signed with reservations.", Now);
        scr.AddReviewComment("software", ReviewCommentAnchor.ChangeCase, null, "Still thinking.", Now);
        scr.ApproveActiveStage("systems", Now.AddHours(1));
        var cycle = scr.ActiveReviewCycle!;

        // The author gets it the moment it publishes — that is the whole point of publishing on signature.
        Assert.Single(cycle.CommentsVisibleTo("author"));

        // The reviewer who has not decided sees only their own draft. Reading a colleague's objections
        // before signing would make their own signature a weaker thing.
        var software = cycle.CommentsVisibleTo("software");
        Assert.Equal("Still thinking.", Assert.Single(software).Body);

        // Having decided, they see it like anyone else.
        scr.ApproveActiveStage("software", Now.AddHours(2));
        Assert.Equal(2, cycle.CommentsVisibleTo("software").Count);
    }

    [Fact]
    public void A_draft_is_visible_to_nobody_but_the_person_writing_it()
    {
        var scr = InReview();
        scr.AddReviewComment("systems", ReviewCommentAnchor.ChangeCase, null, "Not sent yet.", Now);
        var cycle = scr.ActiveReviewCycle!;

        Assert.Single(cycle.CommentsVisibleTo("systems"));
        Assert.Empty(cycle.CommentsVisibleTo("author"));
        Assert.Empty(cycle.CommentsVisibleTo("software"));
        Assert.Empty(cycle.CommentsVisibleTo("somebody.else"));
    }

    [Fact]
    public void Once_the_cycle_closes_everyone_reads_the_same_set()
    {
        var scr = InReview(ReviewMode.Parallel);
        scr.AddReviewComment("systems", ReviewCommentAnchor.ChangeCase, null, "Decided.", Now);
        scr.AddReviewComment("software", ReviewCommentAnchor.ChangeCase, null, "Never decided.", Now);
        var cycle = scr.ActiveReviewCycle!;

        scr.RequestChanges("systems", "Rework it.", Now.AddHours(1));

        foreach (var viewer in new[] { "author", "systems", "software", "verification", "somebody.else" })
            Assert.Equal(2, cycle.CommentsVisibleTo(viewer).Count);
    }

    [Fact]
    public void Comments_do_not_move_the_snapshot_hash()
    {
        var withComments = InReview();
        var untouched = InReview();
        var first = withComments.ActiveReviewCycle!.SnapshotHash;
        Assert.Equal(untouched.ActiveReviewCycle!.SnapshotHash, first);

        withComments.AddReviewComment("systems", ReviewCommentAnchor.ChangeCase, null, "A remark.", Now);
        withComments.AddReviewComment("systems", ReviewCommentAnchor.RequirementRevision,
            withComments.RequirementChanges.First().Id, "Another.", Now);
        withComments.RequestChanges("systems", "Rework the tolerance.", Now.AddHours(1));
        untouched.RequestChanges("systems", "Rework the tolerance.", Now.AddHours(1));

        // Cycle 2 hashes the package again. One of these two has four published comments hanging off cycle 1
        // and the other has none; the thing an approver signs must not be able to tell the difference.
        withComments.SubmitForReview("author", Approvers(), Now.AddHours(2));
        untouched.SubmitForReview("author", Approvers(), Now.AddHours(2));

        Assert.Equal(untouched.ActiveReviewCycle!.SnapshotHash, withComments.ActiveReviewCycle!.SnapshotHash);
        Assert.Equal(first, withComments.ActiveReviewCycle!.SnapshotHash);
    }

    private static ApproverSelection[] Approvers() =>
        [new("systems", "Maya Chen"), new("software", "David Lee"), new("verification", "Sarah Rodriguez")];

    private static SystemChangeRequest InReview(ReviewMode mode = ReviewMode.Sequential)
    {
        var scr = new SystemChangeRequest("SRCR-01049", 1, ProjectId, ReleaseId, "Introduce Round Robin",
            "Round Robin is not available.", "The existing sequence is linear.",
            "Add selectable deterministic Round Robin sequencing.", "author", Now);
        scr.AddRequirementChange("author", "SYSR-00002375", 1, RequirementLevel.System,
            RequirementChangeKind.Introduce, "The FMS shall provide selectable Round Robin sequencing.",
            "Required for the new function.", "Test", Now);
        scr.SubmitForReview("author",
            [new("systems", "Maya Chen"), new("software", "David Lee"), new("verification", "Sarah Rodriguez")],
            Now, mode);
        return scr;
    }
}
