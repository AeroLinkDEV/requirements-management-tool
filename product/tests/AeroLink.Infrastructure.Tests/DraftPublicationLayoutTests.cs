using System.Text;
using AeroLink.Infrastructure.Persistence;

namespace AeroLink.Infrastructure.Tests;

/// <summary>
/// #1109: the Build 1.6 draft PDFs printed "Document  SYSRD " and "Live Program  FMSLIVE ", cut the footer to
/// "Manifest not applicab", and hid the DRAFT watermark behind the cover's approvals panel.
/// </summary>
public sealed class DraftPublicationLayoutTests
{
    internal static ProfessionalPublication Draft() => new(
        "Flight Management System", "Flight Management System Live Program (FMSLIVE)", "FMS Product Development",
        "System Requirements Document (SYSRD)", "Flight Management System System Requirements Document (SYSRD)",
        "Draft for Build 1.6.", "SYSRD-000016", "01", "DRAFT - NOT APPROVED", "1.6", "SW-01.50", "AeroLink Administrator",
        DateTimeOffset.UnixEpoch, "not applicable to a draft", [("Requirements", "153")], [],
        [("01", "Draft", "2026-09-24", "AeroLink Administrator")],
        [new("Effective Requirements", "Released content plus approved changes.",
            [new("SYSR-000001.01", "System", "Changed in this release", "The FMS shall (in cruise) provide a flight plan.", [])])])
    {
        Watermark = "DRAFT",
    };

    private static string Pdf() => Encoding.Latin1.GetString(ProfessionalPublicationRenderer.Render(Draft(), "pdf", "draft").Content);

    [Fact]
    public void Parentheses_are_printed_rather_than_replaced_with_spaces()
    {
        var pdf = Pdf();
        Assert.Contains(@"System Requirements Document \(SYSRD\)", pdf);
        Assert.Contains(@"Live Program \(FMSLIVE\)", pdf);
        Assert.Contains(@"The FMS shall \(in cruise\) provide", pdf);
        Assert.True(PdfReleaseProfile.Validate(Encoding.Latin1.GetBytes(pdf)).IsValid);
    }

    [Fact]
    public void A_draft_footer_states_the_missing_manifest_in_full()
    {
        Assert.Contains("Manifest not applicable to a draft)", Pdf());
    }

    [Fact]
    public void The_cover_watermark_is_drawn_over_the_approvals_panel()
    {
        var cover = Pdf();
        Assert.True(cover.IndexOf("54 118 504 190 re f", StringComparison.Ordinal)
            < cover.IndexOf("(DRAFT) Tj", StringComparison.Ordinal));
    }

    [Fact]
    public void The_cover_names_the_build()
    {
        Assert.Contains("| Build 1.6)", Pdf());
    }
}
