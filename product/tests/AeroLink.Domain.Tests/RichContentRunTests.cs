using AeroLink.Domain.Common;
using AeroLink.Domain.Content;

namespace AeroLink.Domain.Tests;

/// <summary>
/// Emphasis inside a paragraph, and the invariant that makes it safe to add.
///
/// The whole content design exists so that nothing stored can become markup a browser executes. Runs are
/// typed data — a run says "this text is bold", never "&lt;b&gt;" — and these cover the two ways that
/// could quietly stop being true: content surviving a round trip with markup in it, and the plain
/// projection drifting away from what the runs actually say.
/// </summary>
public sealed class RichContentRunTests
{
    private static string Paragraph(string runs) => $"{{\"blocks\":[{{\"type\":\"paragraph\",{runs}}}]}}";

    [Fact]
    public void Emphasis_survives_the_canonical_round_trip()
    {
        var authored = Paragraph("\"text\":\"ignored\",\"runs\":[" +
            "{\"text\":\"The tone \"}," +
            "{\"text\":\"must\",\"bold\":true}," +
            "{\"text\":\" follow within \"}," +
            "{\"text\":\"200 ms\",\"code\":true}]");

        var stored = RichContent.Canonicalize(authored);
        var block = Assert.Single(RichContent.Read(stored));

        Assert.Equal(RichBlockKind.Paragraph, block.Kind);
        Assert.Collection(block.Runs!,
            run => { Assert.Equal("The tone ", run.Text); Assert.True(run.IsPlain); },
            run => { Assert.Equal("must", run.Text); Assert.True(run.Bold); },
            run => Assert.Equal(" follow within ", run.Text),
            run => { Assert.Equal("200 ms", run.Text); Assert.True(run.Code); });
    }

    /// <summary>
    /// The stored text is recomputed from the runs rather than trusted. A record whose two halves disagree
    /// would render one thing and search another, which is exactly the sort of quiet untruth this product
    /// exists to prevent — so the runs win and the projection is repaired.
    /// </summary>
    [Fact]
    public void The_plain_projection_is_recomputed_from_the_runs_rather_than_trusted()
    {
        var lying = Paragraph("\"text\":\"something else entirely\"," +
            "\"runs\":[{\"text\":\"The \"},{\"text\":\"real\",\"bold\":true},{\"text\":\" text\"}]");

        var block = Assert.Single(RichContent.Read(lying));

        Assert.Equal("The real text", block.Text);
        Assert.Equal("The real text", RichContent.ToPlainText(lying));
    }

    [Fact]
    public void Unformatted_content_is_stored_exactly_as_it_was_before_runs_existed()
    {
        var plain = "{\"blocks\":[{\"type\":\"paragraph\",\"text\":\"No emphasis here.\"}]}";

        var stored = RichContent.Canonicalize(plain);

        Assert.Equal(plain, stored);
        Assert.DoesNotContain("runs", stored);
        Assert.Null(Assert.Single(RichContent.Read(stored)).Runs);
    }

    /// <summary>Runs that mark nothing are the same as no runs, and are not written out.</summary>
    [Fact]
    public void Runs_carrying_no_emphasis_are_dropped()
    {
        var stored = RichContent.Canonicalize(
            Paragraph("\"text\":\"\",\"runs\":[{\"text\":\"All \"},{\"text\":\"plain\"}]"));

        Assert.DoesNotContain("runs", stored);
        var block = Assert.Single(RichContent.Read(stored));
        Assert.Equal("All plain", block.Text);
        Assert.Null(block.Runs);
    }

    /// <summary>
    /// One canonical spelling for a given piece of formatted text, so a record's hash does not depend on
    /// how the editor happened to split the selection.
    /// </summary>
    [Fact]
    public void Adjacent_runs_with_the_same_marks_are_merged()
    {
        var split = RichContent.Canonicalize(Paragraph("\"text\":\"\",\"runs\":[" +
            "{\"text\":\"cri\",\"bold\":true},{\"text\":\"tical\",\"bold\":true},{\"text\":\" failure\"}]"));
        var whole = RichContent.Canonicalize(Paragraph("\"text\":\"\",\"runs\":[" +
            "{\"text\":\"critical\",\"bold\":true},{\"text\":\" failure\"}]"));

        Assert.Equal(whole, split);
        Assert.Equal(2, Assert.Single(RichContent.Read(split)).Runs!.Count);
    }

    /// <summary>
    /// Emphasis is structure. A single formatted paragraph is still one paragraph, so without this the
    /// plain-text editor would claim the field and write the emphasis away on the next keystroke.
    /// </summary>
    [Fact]
    public void A_formatted_paragraph_counts_as_structure()
    {
        Assert.False(RichContent.HasStructure("{\"blocks\":[{\"type\":\"paragraph\",\"text\":\"Plain.\"}]}"));
        Assert.True(RichContent.HasStructure(
            Paragraph("\"text\":\"\",\"runs\":[{\"text\":\"Emphasised.\",\"bold\":true}]")));
    }

    /// <summary>
    /// Nothing in a run is ever interpreted. Text that looks like markup is text, before and after storage,
    /// and the marks are separate typed fields that cannot carry a tag name or an attribute.
    /// </summary>
    [Theory]
    [InlineData("<script>alert(1)</script>")]
    [InlineData("<img src=x onerror=alert(1)>")]
    [InlineData("<a href=\\\"javascript:alert(1)\\\">click</a>")]
    [InlineData("</strong><script>alert(1)</script><strong>")]
    public void Text_that_looks_like_markup_stays_text(string hostile)
    {
        var stored = RichContent.Canonicalize(
            Paragraph($"\"text\":\"\",\"runs\":[{{\"text\":\"{hostile}\",\"bold\":true}}]"));

        var run = Assert.Single(Assert.Single(RichContent.Read(stored)).Runs!);
        Assert.Equal(hostile.Replace("\\\"", "\""), run.Text);
        Assert.True(run.Bold);
        // Round-tripping produces the same record, so nothing was interpreted on the way through.
        Assert.Equal(stored, RichContent.Canonicalize(stored));
    }

    [Fact]
    public void A_run_list_that_is_not_a_list_is_refused_rather_than_ignored()
    {
        var refusal = Assert.Throws<DomainException>(() =>
            RichContent.Canonicalize(Paragraph("\"text\":\"x\",\"runs\":\"bold\"")));

        Assert.Contains("runs", refusal.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_paragraph_split_into_more_runs_than_anybody_would_author_is_refused()
    {
        var runs = string.Join(",", Enumerable.Range(0, RichContent.MaximumRunsPerParagraph + 1)
            .Select(index => $"{{\"text\":\"{index % 10}\",\"bold\":true}}"));

        var refusal = Assert.Throws<DomainException>(() =>
            RichContent.Canonicalize(Paragraph($"\"text\":\"\",\"runs\":[{runs}]")));

        Assert.Contains($"{RichContent.MaximumRunsPerParagraph}", refusal.Message);
    }

    /// <summary>
    /// Content written before emphasis existed reads unchanged. This is the compatibility guarantee that
    /// let runs be added without migrating a single stored record.
    /// </summary>
    [Fact]
    public void Content_written_before_emphasis_existed_reads_unchanged()
    {
        // No angle bracket in the symbol: Utf8JsonWriter escapes one to <, which predates runs and is
        // not what this test is about.
        const string legacy = "{\"blocks\":[{\"type\":\"paragraph\",\"text\":\"Written in 2026-07.\"}," +
            "{\"type\":\"symbol\",\"value\":\"P below 1E-5\"}]}";

        Assert.Equal(legacy, RichContent.Canonicalize(legacy));
        Assert.DoesNotContain("runs", RichContent.Canonicalize(legacy));
        Assert.Equal("Written in 2026-07.\nP below 1E-5", RichContent.ToPlainText(legacy));
    }
}
