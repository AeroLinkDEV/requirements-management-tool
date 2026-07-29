using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Common;
using AeroLink.Domain.Content;

namespace AeroLink.Domain.Tests;

/// <summary>
/// Authored content is read by the approver who signs for it, so what is asserted here is that it cannot
/// carry anything executable, cannot silently lose what an author wrote, and cannot change underneath a
/// recorded signature.
/// </summary>
public sealed class RichContentTests
{
    private const string Table =
        """{"blocks":[{"type":"table","caption":"Modes","rows":[["Mode","Value"],["Cruise","250"]]}]}""";

    [Fact]
    public void Content_written_before_this_model_existed_is_still_readable()
    {
        // Refusing to display an approved requirement because its storage format predates the reader would
        // be a defect in the reader, not in the record.
        var blocks = RichContent.Read("The FMS shall sequence waypoints.");
        var block = Assert.Single(blocks);
        Assert.Equal(RichBlockKind.Paragraph, block.Kind);
        Assert.Equal("The FMS shall sequence waypoints.", block.Text);
    }

    [Fact]
    public void Unparseable_content_is_shown_rather_than_discarded()
    {
        var blocks = RichContent.Read("{not json at all");
        Assert.Equal("{not json at all", Assert.Single(blocks).Text);
    }

    [Fact]
    public void A_table_survives_the_round_trip_it_was_written_in()
    {
        var canonical = RichContent.Canonicalize(Table);
        var block = Assert.Single(RichContent.Read(canonical));
        Assert.Equal(RichBlockKind.Table, block.Kind);
        Assert.Equal("Modes", block.Caption);
        Assert.Equal(["Cruise", "250"], block.Rows![1]);
    }

    [Fact]
    public void A_ragged_table_is_squared_so_every_renderer_sees_one_shape()
    {
        // Word draws missing cells as a broken grid and the workspace draws them as a short row. Squaring
        // here is what stops the document and the screen disagreeing.
        var canonical = RichContent.Canonicalize("""{"blocks":[{"type":"table","rows":[["a","b","c"],["d"]]}]}""");
        var rows = Assert.Single(RichContent.Read(canonical)).Rows!;
        Assert.All(rows, row => Assert.Equal(3, row.Count));
        Assert.Equal("", rows[1][2]);
    }

    [Theory]
    [InlineData("""{"blocks":[{"type":"script","text":"alert(1)"}]}""")]
    [InlineData("""{"blocks":[{"type":"iframe","src":"https://elsewhere.example"}]}""")]
    public void A_kind_of_content_this_product_cannot_render_is_refused_not_dropped(string authored)
    {
        // Dropping it would leave the author believing an approver will see something they will not.
        var error = Assert.Throws<DomainException>(() => RichContent.Canonicalize(authored));
        Assert.Contains("is not a kind of content this product can render", error.Message);
    }

    [Theory]
    [InlineData("""{"blocks":[{"type":"image","src":"https://tracker.example/pixel.png"}]}""")]
    [InlineData("""{"blocks":[{"type":"image","dataUri":"data:text/html;base64,PHNjcmlwdD4="}]}""")]
    [InlineData("""{"blocks":[{"type":"image","attachmentId":"not-a-guid"}]}""")]
    public void An_image_that_is_not_a_file_this_deployment_holds_is_refused(string authored)
    {
        // A remote image is an outbound call from a controlled tool, a rendering that changes when somebody
        // else's server changes, and a record that stops reproducing.
        var error = Assert.Throws<DomainException>(() => RichContent.Canonicalize(authored));
        Assert.Contains("attachment", error.Message);
    }

    [Fact]
    public void An_image_block_names_the_attachment_it_depends_on()
    {
        var id = Guid.NewGuid();
        var canonical = RichContent.Canonicalize(
            $$"""{"blocks":[{"type":"image","attachmentId":"{{id}}","alt":"Bus timing","caption":"Figure 1"}]}""");
        Assert.Equal([id], RichContent.ReferencedAttachments(canonical));
        Assert.Equal("Figure 1", RichContent.ToPlainText(canonical));
    }

    [Fact]
    public void The_plain_projection_says_what_the_structure_says()
    {
        // This is what feeds search and every consumer that cannot render structure. A record whose plain
        // form omits a table reads as incomplete.
        Assert.Equal("Modes\nMode\tValue\nCruise\t250", RichContent.ToPlainText(Table));
    }

    [Fact]
    public void An_empty_table_is_refused_because_it_says_nothing()
    {
        Assert.Throws<DomainException>(() => RichContent.Canonicalize("""{"blocks":[{"type":"table","rows":[]}]}"""));
    }

    [Fact]
    public void A_change_case_written_as_structure_derives_its_own_readable_form()
    {
        var scr = new SystemChangeRequest("SCR-00001", 0, Guid.NewGuid(), Guid.NewGuid(), "Oceanic routing",
            "ignored", "ignored", "ignored", "author", DateTimeOffset.UtcNow, ChangeRequestType.System,
            problemRich: Table);

        // The author supplied structure, so the plain form is derived from it rather than from whatever the
        // caller happened to pass alongside. The two can never disagree about what the case says.
        Assert.Equal("Modes\nMode\tValue\nCruise\t250", scr.Problem);
        Assert.Contains("\"type\":\"table\"", scr.ProblemRich);
    }

    [Fact]
    public void A_change_case_written_as_plain_text_still_has_a_structural_form()
    {
        var scr = new SystemChangeRequest("SCR-00002", 0, Guid.NewGuid(), Guid.NewGuid(), "Routing",
            "A defect exists.", "It was analyzed.", "It will be fixed.", "author", DateTimeOffset.UtcNow);

        Assert.Equal("A defect exists.", scr.Problem);
        Assert.Equal("A defect exists.", RichContent.ToPlainText(scr.ProblemRich));
        Assert.False(RichContent.HasStructure(scr.ProblemRich));
    }

    [Fact]
    public void Restructuring_a_change_case_changes_its_review_snapshot()
    {
        var now = DateTimeOffset.UtcNow;
        var projectId = Guid.NewGuid();
        var releaseId = Guid.NewGuid();
        var dispositions = """{"trace":"Affected","verification":"Affected","documents":"Affected","baseline":"Affected","collaboration":"Affected"}""";

        // Two rows of a table and two lines of text read the same in the plain projection. If only the
        // projection were hashed, the thing an approver actually looked at could change underneath a
        // recorded signature.
        var asText = Submit("Modes\nMode\tValue\nCruise\t250", null);
        var asTable = Submit("ignored", Table);
        Assert.NotEqual(asText, asTable);

        string Submit(string plain, string? rich)
        {
            var scr = new SystemChangeRequest("SCR-00003", 0, projectId, releaseId, "Routing",
                plain, "Analysis", "Solution", "author", now, ChangeRequestType.System, problemRich: rich);
            scr.AddRequirementChange("author", "REQ-00000001", 1, RequirementLevel.System,
                RequirementChangeKind.Modify, "The FMS shall sequence waypoints.", "Because.", "Test", now,
                impactDispositionJson: dispositions);
            return scr.SubmitForReview("author", [new("reviewer", "Reviewer")], now).SnapshotHash;
        }
    }
}
