using AeroLink.Domain.Common;
using AeroLink.Domain.Traceability;

namespace AeroLink.Domain.Tests;

/// <summary>
/// A layout decides what a controlled document contains, so what is asserted here is that it cannot name a
/// section the generator will not fill, cannot produce an authoritative-looking file with no records in it,
/// and cannot make an existing template unreadable by arriving later than it did.
/// </summary>
public sealed class PublicationLayoutTests
{
    private const string Sysrd = """
        {
          "appliesTo": "Sysrd",
          "titlePattern": "{product} System Requirements",
          "subtitlePattern": "Baseline {baseline}, release {release}",
          "sections": [
            { "heading": "Requirements", "introduction": "{recordCount} controlled records.", "content": "ControlledRecords" },
            { "heading": "Annex A - Verification", "introduction": "", "content": "VerificationAnnex" },
            { "heading": "Annex B - Upward Traceability", "introduction": "", "content": "UpwardTraceAnnex" }
          ]
        }
        """;

    [Fact]
    public void A_layout_round_trips_through_the_form_it_is_stored_in()
    {
        var layout = PublicationLayout.TryRead(PublicationLayout.Canonicalize(Sysrd));
        Assert.NotNull(layout);
        Assert.Equal(ControlledDocumentType.Sysrd, layout.AppliesTo);
        Assert.Equal("{product} System Requirements", layout.TitlePattern);
        Assert.Equal(3, layout.Sections.Count);
        // Section order is the programme's decision. A standard that puts verification ahead of traceability
        // must produce a document in that order, not in the generator's preferred one.
        Assert.Equal(PublicationSectionContent.VerificationAnnex, layout.Sections[1].Content);
    }

    [Fact]
    public void A_template_body_that_is_not_a_layout_is_readable_as_nothing_rather_than_a_failure()
    {
        // Template bodies predate this schema and are legitimate stored content. Refusing to generate at all
        // would be a defect in the generator, not in the record; these fall back to the built-in layout.
        Assert.Null(PublicationLayout.TryRead("""{"organization":"ACME","notes":"free form"}"""));
        Assert.Null(PublicationLayout.TryRead("not json"));
        Assert.Null(PublicationLayout.TryRead(""));
    }

    [Fact]
    public void A_section_this_product_cannot_fill_is_refused_at_approval()
    {
        // Approving one the generator cannot render would let somebody sign a structure that produces a
        // heading with nothing under it, and the defect would surface in a controlled document.
        var error = Assert.Throws<DomainException>(() => PublicationLayout.Canonicalize("""
            {"appliesTo":"Sysrd","sections":[{"heading":"Appendix","content":"CostBreakdown"}]}
            """));
        Assert.Contains("not a kind of section this product can fill", error.Message);
    }

    [Fact]
    public void A_document_type_this_product_does_not_generate_is_refused()
    {
        var error = Assert.Throws<DomainException>(() => PublicationLayout.Canonicalize("""
            {"appliesTo":"SafetyAssessment","sections":[{"heading":"Records","content":"ControlledRecords"}]}
            """));
        Assert.Contains("not a kind of controlled document this product generates", error.Message);
    }

    [Fact]
    public void A_layout_that_never_renders_its_records_is_refused()
    {
        // Otherwise the product would generate an authoritative-looking file containing no requirements.
        var error = Assert.Throws<DomainException>(() => PublicationLayout.Canonicalize("""
            {"appliesTo":"Sysrd","sections":[{"heading":"Scope","introduction":"Prose only.","content":"Narrative"}]}
            """));
        Assert.Contains("must render its controlled records", error.Message);
    }

    [Theory]
    [InlineData("""{"appliesTo":"Sysrd","sections":[]}""", "at least one section")]
    [InlineData("""{"appliesTo":"Sysrd","sections":[{"heading":"","content":"ControlledRecords"}]}""", "needs a heading")]
    [InlineData("""{"appliesTo":"Sysrd"}""", "list of sections")]
    [InlineData("not json", "could not be read")]
    public void A_layout_that_could_not_produce_a_document_is_refused_with_the_reason(string body, string expected)
    {
        Assert.Contains(expected, Assert.Throws<DomainException>(() => PublicationLayout.Canonicalize(body)).Message);
    }

    [Fact]
    public void Placeholders_are_filled_from_the_documents_own_context()
    {
        var values = new Dictionary<string, string> { ["product"] = "FMS", ["baseline"] = "BL-004" };
        Assert.Equal("FMS System Requirements for BL-004",
            PublicationLayout.Fill("{product} System Requirements for {baseline}", values));
    }

    [Fact]
    public void An_unrecognised_placeholder_stays_visible_rather_than_vanishing()
    {
        // A typo that silently became an empty string would leave a section heading blank in a controlled
        // document, and nobody reading it would know a title had been lost.
        Assert.Equal("FMS {revsion} Requirements",
            PublicationLayout.Fill("{product} {revsion} Requirements", new Dictionary<string, string> { ["product"] = "FMS" }));
    }
}
