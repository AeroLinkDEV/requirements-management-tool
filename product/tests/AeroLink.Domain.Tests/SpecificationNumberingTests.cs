using AeroLink.Domain.Requirements;

namespace AeroLink.Domain.Tests;

/// <summary>
/// Section numbers are a fact about where a section sits, so they are read from where it sits.
///
/// They used to be typed into the heading — "1. Functional Behavior" — which is right until somebody inserts
/// a section above it, and which cannot express 4.1.1 at all.
/// </summary>
public sealed class SpecificationNumberingTests
{
    private static (Guid Id, Guid? ParentId, int Position, string Heading) Node(Guid id, Guid? parent, int position, string heading)
        => (id, parent, position, heading);

    [Fact]
    public void Top_level_sections_are_numbered_in_position_order()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        // Deliberately supplied out of order, because the caller reads them from a database in whatever order
        // the query returns and the numbering is what imposes the document's order.
        var numbered = SpecificationNumbering.Number(
        [
            Node(second, null, 2000, "Navigation and Guidance"),
            Node(first, null, 1000, "Functional Behavior"),
        ]);

        Assert.Equal(["1", "2"], numbered.Select(x => x.Number));
        Assert.Equal(first, numbered[0].Id);
        Assert.Equal("Functional Behavior", numbered[0].Heading);
    }

    [Fact]
    public void A_sub_section_is_numbered_beneath_its_parent_and_reported_depth_first()
    {
        var integrity = Guid.NewGuid();
        var builtInTest = Guid.NewGuid();
        var annunciation = Guid.NewGuid();
        var deeper = Guid.NewGuid();
        var operational = Guid.NewGuid();

        var numbered = SpecificationNumbering.Number(
        [
            Node(integrity, null, 4000, "Integrity and Monitoring"),
            Node(operational, null, 5000, "Operational Constraints"),
            Node(builtInTest, integrity, 1000, "Built-In Test"),
            Node(annunciation, integrity, 2000, "Fault Annunciation"),
            Node(deeper, builtInTest, 1000, "Power-On Self Test"),
        ]);

        // Depth first: a reader meets 4, then everything inside 4, then 5.
        Assert.Equal(["1", "1.1", "1.1.1", "1.2", "2"], numbered.Select(x => x.Number));
        Assert.Equal([integrity, builtInTest, deeper, annunciation, operational], numbered.Select(x => x.Id));
        Assert.Equal([0, 1, 2, 1, 0], numbered.Select(x => x.Depth));
    }

    [Fact]
    public void Inserting_a_section_renumbers_everything_after_it_without_touching_a_heading()
    {
        var one = Guid.NewGuid();
        var inserted = Guid.NewGuid();
        var last = Guid.NewGuid();

        var before = SpecificationNumbering.Number([Node(one, null, 1000, "Functional Behavior"), Node(last, null, 2000, "Data and Interfaces")]);
        Assert.Equal("2", before.Single(x => x.Id == last).Number);

        var after = SpecificationNumbering.Number(
        [
            Node(one, null, 1000, "Functional Behavior"),
            Node(inserted, null, 1500, "Navigation and Guidance"),
            Node(last, null, 2000, "Data and Interfaces"),
        ]);
        // This is the whole point: nobody retyped "Data and Interfaces" to make it section 3.
        Assert.Equal("3", after.Single(x => x.Id == last).Number);
        Assert.Equal("Data and Interfaces", after.Single(x => x.Id == last).Heading);
    }

    [Theory]
    [InlineData("1. Functional Behavior", "Functional Behavior")]
    [InlineData("4.1.1 Power-On Self Test", "Power-On Self Test")]
    [InlineData("Integrity and Monitoring", "Integrity and Monitoring")]
    // A heading that legitimately opens with a figure keeps it. Stripping "3D" would be a worse fault than
    // the one being fixed, because nothing downstream could tell the heading had been damaged.
    [InlineData("3D Terrain Rendering", "3D Terrain Rendering")]
    public void A_number_written_into_a_heading_is_removed_and_nothing_else_is(string stored, string expected)
        => Assert.Equal(expected, SpecificationNumbering.WithoutLeadingNumber(stored));

    [Fact]
    public void A_section_whose_parent_is_missing_is_still_reported()
    {
        var present = Guid.NewGuid();
        var orphan = Guid.NewGuid();
        var numbered = SpecificationNumbering.Number(
        [
            Node(present, null, 1000, "Functional Behavior"),
            Node(orphan, Guid.NewGuid(), 1000, "Detached"),
        ]);

        // Unnumbered, because it has no place in the document, but visible — a section that silently vanished
        // would be indistinguishable from one that was never there.
        Assert.Equal(2, numbered.Count);
        Assert.Equal("", numbered.Single(x => x.Id == orphan).Number);
    }
}
