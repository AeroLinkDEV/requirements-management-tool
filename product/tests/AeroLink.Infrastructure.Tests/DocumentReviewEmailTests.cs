using AeroLink.Infrastructure.Notifications;

namespace AeroLink.Infrastructure.Tests;

/// <summary>
/// The document review request. Both forms are asserted separately from the change request ones rather than
/// assumed to follow, because they share a shell but not a subject: the fact rows differ, and one paragraph
/// exists only here.
/// </summary>
public sealed class DocumentReviewEmailTests
{
    private static DocumentReviewEmailFacts Facts(string title = "System Requirements Document — FMS 1.6 candidate") =>
        new("SYSRD-0004.03", title, "Independent technical review · step 1 of 2", "Systems Engineering Lead",
            "Maya Patel", "Daniel Reyes", new DateTimeOffset(2026, 8, 14, 9, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 8, 20, 9, 0, 0, TimeSpan.Zero));

    [Fact]
    public void The_subject_leads_with_the_identifier_and_the_ask()
    {
        Assert.Equal("SYSRD-0004.03 is ready for your review — AeroLink", DocumentReviewEmailTemplate.Subject(Facts()));
    }

    [Fact]
    public void Both_forms_say_the_document_is_not_attached()
    {
        const string link = "https://aerolink.example.test/open/managed-document/4b2e10c9";
        var html = DocumentReviewEmailTemplate.Html(Facts(), link, null);
        var text = DocumentReviewEmailTemplate.PlainText(Facts(), link, null);

        // The reason a reviewer must open the record rather than read a copy: a mailed attachment is a
        // second artifact that can drift from the controlled one.
        Assert.Contains("No attachment is sent", html);
        Assert.Contains("No attachment is sent", text);
        Assert.Contains("The file stays in AeroLink", html);
    }

    [Fact]
    public void The_facts_a_document_reviewer_needs_reach_both_forms()
    {
        const string link = "https://aerolink.example.test/open/managed-document/4b2e10c9";
        var html = DocumentReviewEmailTemplate.Html(Facts(), link, null);
        var text = DocumentReviewEmailTemplate.PlainText(Facts(), link, null);

        foreach (var fact in new[] { "SYSRD-0004.03", "Independent technical review", "Systems Engineering Lead", "Daniel Reyes" })
        {
            Assert.Contains(fact, html);
            Assert.Contains(fact, text);
        }
        Assert.Contains(link, html);
        Assert.Contains(link, text);
    }

    [Fact]
    public void A_document_title_cannot_break_out_of_the_html_it_is_rendered_into()
    {
        var html = DocumentReviewEmailTemplate.Html(Facts("Requirements <script>alert(1)</script> & scope"), null, null);

        Assert.DoesNotContain("<script>", html);
        Assert.Contains("&lt;script&gt;", html);
        Assert.Contains("&amp; scope", html);
    }

    [Fact]
    public void With_no_public_address_neither_form_prints_a_broken_link()
    {
        var html = DocumentReviewEmailTemplate.Html(Facts(), null, null);
        var text = DocumentReviewEmailTemplate.PlainText(Facts(), null, null);

        Assert.DoesNotContain("<a href", html);
        Assert.Contains("Sign in to AeroLink", html);
        Assert.Contains("Sign in to AeroLink", text);
    }
}
