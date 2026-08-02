using AeroLink.Domain.Common;
using AeroLink.Domain.Verification;

namespace AeroLink.Domain.Tests;

/// <summary>
/// The procedures a build has to run before it can ship.
///
/// A build is rarely worth its whole test suite. Somebody decides which procedures this one needs, and that
/// decision is what the release is then measured against.
/// </summary>
public sealed class BuildTestSetTests
{
    private static BuildTestSet Set() =>
        new(Guid.NewGuid(), Guid.NewGuid(), TestChangeReviewDiscipline.System, DateTimeOffset.UtcNow);

    [Fact]
    public void A_new_set_runs_nothing_until_somebody_decides()
    {
        // Not "everything by default": a build that silently inherits the whole suite has had no decision
        // made about it, and the gate would then measure it against work nobody intended to do.
        Assert.Empty(Set().Entries);
    }

    [Fact]
    public void Including_a_procedure_records_who_chose_it_and_why()
    {
        var set = Set();
        var procedure = Guid.NewGuid();

        Assert.True(set.Include("test.lead", procedure, TestSelectionReason.CoverageArea,
            "Integrity and Monitoring", DateTimeOffset.UtcNow));

        var entry = set.Entries.Single();
        Assert.Equal(procedure, entry.ProcedureRevisionId);
        Assert.Equal(TestSelectionReason.CoverageArea, entry.Reason);
        Assert.Equal("Integrity and Monitoring", entry.Note);
        Assert.Equal("test.lead", entry.AddedBy);
    }

    /// <summary>
    /// Selection happens from several directions at once — what changed, an area worth exercising, a defect
    /// needing a retest — and those overlap by design.
    /// </summary>
    [Fact]
    public void Selecting_the_same_procedure_twice_is_not_an_error_and_keeps_the_first_reason()
    {
        var set = Set();
        var procedure = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        Assert.True(set.Include("test.lead", procedure, TestSelectionReason.ChangedRequirement, "SYSR-000151", now));
        // Arriving by a second route did not put it in the set, so it does not get to say why it is there.
        Assert.False(set.Include("other.engineer", procedure, TestSelectionReason.CoverageArea, "Area sweep", now));

        var entry = set.Entries.Single();
        Assert.Equal(TestSelectionReason.ChangedRequirement, entry.Reason);
        Assert.Equal("test.lead", entry.AddedBy);
    }

    [Fact]
    public void A_procedure_can_be_taken_back_out()
    {
        var set = Set();
        var procedure = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        set.Include("test.lead", procedure, TestSelectionReason.Chosen, "Worth running.", now);

        Assert.True(set.Exclude(procedure, now));
        Assert.Empty(set.Entries);
        // Removing something that is not there is a no-op rather than a fault: two people tidying the same
        // set should not produce an error for the slower one.
        Assert.False(set.Exclude(procedure, now));
    }

    [Fact]
    public void A_changed_requirement_procedure_is_mandatory()
    {
        var set = Set();
        var procedure = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        set.Include("test.lead", procedure, TestSelectionReason.ChangedRequirement, "SYSR-000151 changed.", now);

        var error = Assert.Throws<DomainException>(() => set.Exclude(procedure, now));
        Assert.Contains("mandatory before release", error.Message);
        Assert.Single(set.Entries);
    }

    [Fact]
    public void A_discretionary_selection_is_promoted_when_a_changed_requirement_makes_it_mandatory()
    {
        var set = Set();
        var procedure = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        set.Include("test.lead", procedure, TestSelectionReason.Chosen, "Area sweep.", now);

        Assert.False(set.Include("verification", procedure, TestSelectionReason.ChangedRequirement,
            "SYSR-000151 changed.", now));
        var entry = Assert.Single(set.Entries);
        Assert.Equal(TestSelectionReason.ChangedRequirement, entry.Reason);
        Assert.Contains("SYSR-000151", entry.Note);
        Assert.Throws<DomainException>(() => set.Exclude(procedure, now));
    }

    [Fact]
    public void Every_selection_says_who_made_it()
    {
        var set = Set();
        Assert.Throws<DomainException>(() =>
            set.Include("  ", Guid.NewGuid(), TestSelectionReason.Chosen, "", DateTimeOffset.UtcNow));
    }

    [Fact]
    public void A_set_belongs_to_a_build_and_a_discipline()
    {
        Assert.Throws<DomainException>(() =>
            new BuildTestSet(Guid.NewGuid(), Guid.Empty, TestChangeReviewDiscipline.System, DateTimeOffset.UtcNow));
        Assert.Throws<DomainException>(() =>
            new BuildTestSet(Guid.Empty, Guid.NewGuid(), TestChangeReviewDiscipline.System, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void Changing_the_set_advances_its_version()
    {
        var set = Set();
        var before = set.Version;
        set.Include("test.lead", Guid.NewGuid(), TestSelectionReason.Chosen, "", DateTimeOffset.UtcNow);
        Assert.True(set.Version > before);
    }
}
