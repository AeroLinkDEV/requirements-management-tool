using AeroLink.Domain.Common;
using AeroLink.Domain.Imports;

namespace AeroLink.Domain.Tests;

/// <summary>
/// What a requirement was called before it came here, and what this product will and will not say about it.
///
/// Two rules carry most of the weight. Only objects present in the imported baseline join the traceability
/// network — an object retired before it is recorded so a reference to it can be answered, and joins nothing.
/// And source history is reported, never asserted: it is not a revision, nobody signs for it, and nothing
/// downstream reasons over it.
/// </summary>
public sealed class SourceIdentityTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 4, 9, 0, 0, TimeSpan.Zero);
    private static readonly Guid ProjectId = Guid.NewGuid();
    private static readonly Guid ImportId = Guid.NewGuid();

    private static SourceIdentity Identity(string identifier = "SYS-01234", string key = "1234") =>
        new(ProjectId, ImportId, "IBM Rational DOORS", "FMS_System_Requirements", key, identifier, Now);

    [Fact]
    public void A_source_identifier_survives_the_import_as_a_record_of_its_own()
    {
        var identity = Identity();

        Assert.Equal("SYS-01234", identity.SourceIdentifier);
        Assert.Equal("FMS_System_Requirements", identity.SourceModule);
        // The source's own stable key, which survives the identifier text being edited between extracts —
        // this is what makes a re-import a delta rather than a duplicate set.
        Assert.Equal("1234", identity.SourceObjectKey);
        Assert.True(identity.InImportedBaseline);
    }

    [Fact]
    public void An_object_retired_before_the_imported_baseline_is_recorded_but_joins_nothing()
    {
        // SYS-01233 existed at V0.9 and was gone by V1.0. Somebody holding a drawing that cites it should
        // get an answer rather than an empty result they read as the tool having lost it.
        var retired = SourceIdentity.FromHistoryOnly(ProjectId, ImportId, "IBM Rational DOORS",
            "FMS_System_Requirements", "1233", "SYS-01233", Now);

        Assert.Equal("SYS-01233", retired.SourceIdentifier);
        // False is what keeps history narrative rather than nodes: nothing links to it, so a retired
        // ancestor can never become a dangling reference in the traceability network.
        Assert.False(retired.InImportedBaseline);
    }

    [Fact]
    public void A_provenance_link_reads_in_one_direction_only()
    {
        var revisionId = Guid.NewGuid();
        var identityId = Guid.NewGuid();

        var link = new SourceIdentityLink(ProjectId, revisionId, identityId, ImportId, Now);

        // SYSR-000148.00 originates from SYS-01234. The controlled requirement is the subject; the source
        // object is what it came from. Reversed, it would produce a complete and entirely wrong lineage.
        Assert.Equal(revisionId, link.RequirementRevisionId);
        Assert.Equal(identityId, link.SourceIdentityId);
        // The link records the import as its origin, never a change request, so nothing here suggests a
        // build carried work it did not.
        Assert.Equal(ImportId, link.BaselineImportId);
    }

    [Fact]
    public void An_object_the_source_retired_cannot_have_anything_originate_from_it()
    {
        var retired = SourceIdentity.FromHistoryOnly(ProjectId, ImportId, "IBM Rational DOORS",
            "FMS_System_Requirements", "1233", "SYS-01233", Now);

        // The rule that keeps source history narrative rather than nodes, enforced where the identity is in
        // hand. Nothing in the imported baseline came from an object that was gone before it — a link saying
        // otherwise would be a lineage claim about a requirement nobody imported.
        var refused = Assert.Throws<DomainException>(() => retired.LinkTo(Guid.NewGuid(), Now));
        Assert.Contains("SYS-01233", refused.Message);

        var live = Identity();
        var link = live.LinkTo(Guid.NewGuid(), Now);
        Assert.Equal(live.Id, link.SourceIdentityId);
        Assert.Equal(ImportId, link.BaselineImportId);
    }

    [Fact]
    public void A_provenance_link_cannot_be_missing_either_end_or_its_origin()
    {
        Assert.Throws<DomainException>(() => new SourceIdentityLink(ProjectId, Guid.Empty, Guid.NewGuid(), ImportId, Now));
        Assert.Throws<DomainException>(() => new SourceIdentityLink(ProjectId, Guid.NewGuid(), Guid.Empty, ImportId, Now));
        Assert.Throws<DomainException>(() => new SourceIdentityLink(ProjectId, Guid.NewGuid(), Guid.NewGuid(), Guid.Empty, Now));
    }

    [Fact]
    public void Source_history_is_recorded_as_reported_even_where_the_source_recorded_little()
    {
        // DOORS history is attribute-level and messy: objects get moved, purged and renumbered, and an
        // author or a date is often simply absent. Because this product asserts nothing about any of it,
        // a thin entry can be recorded honestly instead of being filled in with something plausible.
        var sparse = new SourceHistoryEntry(ProjectId, Guid.NewGuid(), ImportId, "V0.8",
            statement: "", changedBy: "", changedAt: null, sourceChangeReference: "");

        Assert.Equal("V0.8", sparse.SourceBaselineName);
        Assert.Equal("", sparse.Statement);
        Assert.Equal("", sparse.ChangedBy);
        Assert.Null(sparse.ChangedAt);

        var full = new SourceHistoryEntry(ProjectId, Guid.NewGuid(), ImportId, "V0.9",
            "The FMS shall annunciate a navigation source disagreement.", "a.okafor",
            new DateTimeOffset(2025, 1, 22, 0, 0, 0, TimeSpan.Zero), "DOORS CR-1402");

        Assert.Equal("a.okafor", full.ChangedBy);
        Assert.Equal("DOORS CR-1402", full.SourceChangeReference);
    }

    [Fact]
    public void Source_history_still_has_to_say_which_source_baseline_it_describes()
    {
        // The one thing an entry cannot be vague about: an undated, unattributed statement with no baseline
        // is not a fact about anything.
        Assert.Throws<DomainException>(() => new SourceHistoryEntry(ProjectId, Guid.NewGuid(), ImportId, "  ",
            "A statement", "someone", Now, ""));
    }

    [Fact]
    public void A_later_extract_marks_an_identity_seen_without_disturbing_who_recorded_it()
    {
        var identity = Identity();
        var later = Now.AddMonths(4);

        identity.SeenAgain(later);

        Assert.Equal(Now, identity.FirstSeenAt);
        Assert.Equal(later, identity.LastSeenAt);
        Assert.Equal(ImportId, identity.BaselineImportId);
    }
}
