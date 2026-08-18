using AeroLink.Domain.Common;

namespace AeroLink.Domain.ChangeControl;

/// <summary>
/// What a comment is attached to. A comment always concerns one identifiable thing.
///
/// <see cref="DocumentRevision"/> is deliberately coarser than the others. A managed document is a checked-in
/// DOCX with a hash, and this system holds no addressable structure inside it — there are no sections to point
/// at. So a document comment names the revision and carries the reviewer's own section label as a hint. That
/// label is prose and can go stale, which is exactly the weakness that ruled out prose anchors for
/// requirements; it is accepted here only because the alternative is no anchor at all.
/// </summary>
public enum ReviewCommentAnchor { ChangeCase, RequirementRevision, DocumentRevision }

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
/// is handled by whichever review owns it rather than here.
///
/// Two kinds of review own comments, and each keeps its own foreign key rather than sharing one untyped
/// column, exactly as <see cref="ReviewCycle"/> does for its two owners: a change request review cycle, or a
/// managed document revision under review. The database enforces that exactly one is set, so a comment that
/// belongs to nothing stays impossible.
/// </summary>
public sealed class ReviewComment
{
    private ReviewComment() { }

    /// <summary>A remark on a change request review cycle.</summary>
    internal ReviewComment(Guid reviewCycleId, string authorId, ReviewCommentAnchor anchor,
        Guid? requirementChangeId, string body, DateTimeOffset now)
        : this(authorId, body, now)
    {
        if (anchor == ReviewCommentAnchor.DocumentRevision)
            throw new DomainException("A change request comment cannot be anchored to a document revision.");
        // The anchor and the identifier have to agree. A requirement comment with no requirement is
        // unreadable six weeks later, and a change-case comment carrying one implies a link that is not real.
        if (anchor == ReviewCommentAnchor.RequirementRevision && requirementChangeId is null)
            throw new DomainException("A comment on a requirement must name the requirement revision.");
        if (anchor == ReviewCommentAnchor.ChangeCase && requirementChangeId is not null)
            throw new DomainException("A comment on the change case cannot also name a requirement revision.");

        ReviewCycleId = reviewCycleId;
        Anchor = anchor;
        RequirementChangeId = requirementChangeId;
    }

    /// <summary>A remark on a managed document revision under review.</summary>
    internal ReviewComment(Guid managedDocumentRevisionId, int documentCycle, string authorId,
        string sectionLabel, string body, DateTimeOffset now)
        : this(authorId, body, now)
    {
        ManagedDocumentRevisionId = managedDocumentRevisionId;
        DocumentCycle = documentCycle;
        Anchor = ReviewCommentAnchor.DocumentRevision;
        // Optional, and free text on purpose: there is nothing structured to point at, so this is the
        // reviewer's own words about where in the document they are looking.
        SectionLabel = sectionLabel.Trim();
    }

    private ReviewComment(string authorId, string body, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(authorId)) throw new DomainException("A comment must have an author.");
        if (string.IsNullOrWhiteSpace(body)) throw new DomainException("A comment cannot be empty.");
        Id = Guid.NewGuid();
        AuthorId = authorId;
        Body = body.Trim();
        State = ReviewCommentState.Draft;
        CreatedAt = now;
        UpdatedAt = now;
    }

    public Guid Id { get; private set; }
    /// <summary>The change request review cycle this belongs to, when it belongs to one.</summary>
    public Guid? ReviewCycleId { get; private set; }
    /// <summary>The managed document revision this belongs to. Never set together with the above.</summary>
    public Guid? ManagedDocumentRevisionId { get; private set; }
    /// <summary>
    /// Which review round of that document revision. A revision keeps its steps across rounds on one
    /// aggregate rather than starting a fresh cycle entity, so the round has to be recorded here for a
    /// comment to be scoped to the review it was written during.
    /// </summary>
    public int? DocumentCycle { get; private set; }
    public string AuthorId { get; private set; } = string.Empty;
    public ReviewCommentAnchor Anchor { get; private set; }
    /// <summary>The requirement revision this concerns, when it concerns one. Null otherwise.</summary>
    public Guid? RequirementChangeId { get; private set; }
    /// <summary>Where in the document the reviewer was reading. Free text, and empty when not a document.</summary>
    public string SectionLabel { get; private set; } = string.Empty;
    public string Body { get; private set; } = string.Empty;
    public ReviewCommentState State { get; private set; }
    /// <summary>
    /// Whether the author of this comment had recorded a decision when it published.
    ///
    /// False means the review closed under them — another reviewer returned the package, or it was cancelled
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
