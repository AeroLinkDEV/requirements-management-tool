namespace AeroLink.Domain.Requirements;

/// <summary>
/// One tag on one exact requirement revision, stored as a row rather than as a fragment of a JSON array.
///
/// Tag filtering matched `TagsJson.ToLower().Contains(tag)`, so the tag `safe` matched every requirement
/// tagged `failsafe`, and a case-folded leading-wildcard scan over raw JSON can use no index at all. Both
/// problems are the same problem: a serialized array is not a set the database can search.
///
/// The authored `TagsJson` remains what an author edits and what is displayed; this is the normalized index
/// the query reads, written alongside it.
/// </summary>
public sealed class RequirementRevisionTag
{
    private RequirementRevisionTag() { }

    public RequirementRevisionTag(Guid revisionId, string tag)
    {
        Id = Guid.NewGuid();
        RevisionId = revisionId;
        Tag = RequirementFilterValue.Normalize(tag);
        DisplayTag = (tag ?? string.Empty).Trim();
    }

    public Guid Id { get; private set; }
    public Guid RevisionId { get; private set; }
    /// <summary>Normalized for exact comparison.</summary>
    public string Tag { get; private set; } = "";
    /// <summary>As the author wrote it, so a reader is not shown a case-folded version of their own tag.</summary>
    public string DisplayTag { get; private set; } = "";
}
