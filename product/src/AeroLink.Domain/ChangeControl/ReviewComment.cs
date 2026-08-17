using AeroLink.Domain.Common;

namespace AeroLink.Domain.ChangeControl;

/// <summary>What a comment is attached to. A comment always concerns one identifiable thing.</summary>
public enum ReviewCommentAnchor { ChangeCase, RequirementRevision }

public enum ReviewCommentState { Draft, Published }

/// <summary>
/// A reviewer's remark to the author about one part of the package they are reviewing.
///
/// This is working communication, not evidence, and the distinction is deliberate: a comment carries no
/// signature, is not covered by the snapshot hash, and never appears in the generated DOCX or PDF. The
/// controlled field is the return reason on the step beside it, which is mandatory and permanent. Comments
/// exist so a reviewer can say <em>which</em> requirement is wrong instead of describing it in prose.
///
/// A comment stays private to its author while it is a draft, and is published by the act of its author
/// recording their decision. Nothing else publishes it — including another reviewer deciding first, which
/// is handled by the cycle rather than here.
/// </summary>
public sealed class ReviewComment
{
    private ReviewComment() { }

    internal ReviewComment(Guid reviewCycleId, string authorId, ReviewCommentAnchor anchor,
        Guid? requirementChangeId, string body, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(authorId)) throw new DomainException("A comment must have an author.");
        if (string.IsNullOrWhiteSpace(body)) throw new DomainException("A comment cannot be empty.");
        // The anchor and the identifier have to agree. A requirement comment with no requirement is
        // unreadable six weeks later, and a change-case comment carrying one implies a link that is not real.
        if (anchor == ReviewCommentAnchor.RequirementRevision && requirementChangeId is null)
            throw new DomainException("A comment on a requirement must name the requirement revision.");
        if (anchor == ReviewCommentAnchor.ChangeCase && requirementChangeId is not null)
            throw new DomainException("A comment on the change case cannot also name a requirement revision.");

        Id = Guid.NewGuid();
        ReviewCycleId = reviewCycleId;
        AuthorId = authorId;
        Anchor = anchor;
        RequirementChangeId = requirementChangeId;
        Body = body.Trim();
        State = ReviewCommentState.Draft;
        CreatedAt = now;
        UpdatedAt = now;
    }

    public Guid Id { get; private set; }
    public Guid ReviewCycleId { get; private set; }
    public string AuthorId { get; private set; } = string.Empty;
    public ReviewCommentAnchor Anchor { get; private set; }
    /// <summary>The requirement revision this concerns, when it concerns one. Null for the change case.</summary>
    public Guid? RequirementChangeId { get; private set; }
    public string Body { get; private set; } = string.Empty;
    public ReviewCommentState State { get; private set; }
    /// <summary>
    /// Whether the author of this comment had recorded a decision when it published.
    ///
    /// False means the cycle closed under them — another reviewer returned the package, or it was cancelled
    /// — and this was still a draft. It is published anyway, because throwing away a reviewer's written
    /// analysis because a colleague clicked first is pure waste, but it must be labelled: it asserts nothing
    /// about its author's position, since they never took one.
    /// </summary>
    public bool DecisionRecorded { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public DateTimeOffset? PublishedAt { get; private set; }

    internal void Revise(string body, DateTimeOffset now)
    {
        // Free to change until it is published, and frozen afterwards. Once the author is reading it and
        // acting on it, editing it underneath them would make the record of the exchange untrue.
        if (State != ReviewCommentState.Draft)
            throw new DomainException("A published comment cannot be edited.");
        if (string.IsNullOrWhiteSpace(body)) throw new DomainException("A comment cannot be empty.");
        Body = body.Trim();
        UpdatedAt = now;
    }

    internal void Publish(bool decisionRecorded, DateTimeOffset now)
    {
        if (State == ReviewCommentState.Published) return;
        State = ReviewCommentState.Published;
        DecisionRecorded = decisionRecorded;
        PublishedAt = now;
    }
}
