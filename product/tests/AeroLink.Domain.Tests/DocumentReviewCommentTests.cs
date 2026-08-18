using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Common;
using AeroLink.Domain.Documents;

namespace AeroLink.Domain.Tests;

/// <summary>
/// A document reviewer saying what is wrong with the revision they are reading.
///
/// The same grammar as a change request review over a different subject, so what is asserted here is that
/// the rules did not quietly change on the way across: a draft reaches nobody, its author deciding is what
/// publishes it, and a round ending under a reviewer does not throw their writing away.
/// </summary>
public sealed class DocumentReviewCommentTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 17, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_comment_is_a_draft_until_its_author_decides()
    {
        var revision = InReview();
        var comment = revision.AddComment("peer.reviewer", "3.2 Flight plan synchronisation",
            "3.2.4 still cites the retired full-reload statement.", Now);

        Assert.Equal(ReviewCommentState.Draft, comment.State);
        Assert.Equal(ReviewCommentAnchor.DocumentRevision, comment.Anchor);
        Assert.Equal("3.2 Flight plan synchronisation", comment.SectionLabel);
        Assert.Empty(revision.CommentsVisibleTo("software.author"));

        revision.Approve("peer.reviewer", "Reads correctly.", Now.AddHours(1));

        Assert.Equal(ReviewCommentState.Published, comment.State);
        Assert.True(comment.DecisionRecorded);
        Assert.Single(revision.CommentsVisibleTo("software.author"));
    }

    [Fact]
    public void A_reviewer_still_deciding_cannot_read_what_a_colleague_already_signed()
    {
        var revision = InReview();
        revision.AddComment("peer.reviewer", "3.2", "Signed with reservations.", Now);
        revision.Approve("peer.reviewer", "Reads correctly.", Now.AddHours(1));

        // The owner has it the moment it publishes.
        Assert.Single(revision.CommentsVisibleTo("software.author"));
        // The next reviewer has not decided, so reading it would weaken the signature they are about to give.
        Assert.Empty(revision.CommentsVisibleTo("quality.analyst"));
    }

    [Fact]
    public void A_reviewer_who_never_got_to_decide_does_not_lose_what_they_wrote()
    {
        var revision = InReview();
        // The second reviewer reads ahead and writes something down before their stage is reached.
        var stranded = revision.AddComment("quality.analyst", "3.3",
            "The new annunciation has no verification coverage.", Now);

        revision.Return("peer.reviewer", "Settle 3.2.4 first.", Now.AddHours(1));

        Assert.Equal(ReviewCommentState.Published, stranded.State);
        Assert.False(stranded.DecisionRecorded);
    }

    [Fact]
    public void Only_a_reviewer_on_this_round_can_comment_and_only_on_their_own()
    {
        var revision = InReview();
        Assert.Throws<DomainException>(() => revision.AddComment("software.author", "3.2", "The owner's note.", Now));

        var comment = revision.AddComment("peer.reviewer", "3.2", "First thought.", Now);
        Assert.Throws<DomainException>(() => revision.ReviseComment(comment.Id, "quality.analyst", "Not yours.", Now));

        revision.ReviseComment(comment.Id, "peer.reviewer", "Second thought.", Now.AddMinutes(5));
        Assert.Equal("Second thought.", comment.Body);

        revision.Approve("peer.reviewer", "Reads correctly.", Now.AddHours(1));
        Assert.Throws<DomainException>(() => revision.ReviseComment(comment.Id, "peer.reviewer", "Third.", Now.AddHours(2)));
    }
    private static ManagedDocumentRevision InReview()
    {
        var revision = new ManagedDocumentRevision(Guid.NewGuid(), 1, "software.author",
            "Project document update.", Now, Guid.NewGuid(), Guid.NewGuid(), new string('a', 64),
            "aerolink-managed-document-successor-v1");
        revision.RecordCheckIn(Guid.NewGuid(), Now);
        revision.SubmitForReview("software.author", new string('b', 64),
        [
            new("peer.reviewer", "Peer Reviewer", "Independent technical review"),
            new("quality.analyst", "Quality Analyst", "Quality release", Kind: ReviewStageKind.Approval),
        ], Now);
        return revision;
    }
}
