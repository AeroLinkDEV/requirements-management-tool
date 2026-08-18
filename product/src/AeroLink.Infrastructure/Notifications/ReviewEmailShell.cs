using System.Net;
using System.Text;

namespace AeroLink.Infrastructure.Notifications;

/// <summary>
/// The shell every review request is rendered into, whatever it is a review of.
///
/// Extracted when document reviews arrived rather than copied, because two near-identical templates drift:
/// the first time somebody fixes an Outlook quirk in one of them, the product starts sending two different
/// emails that were meant to be one design. What varies between subjects is the eyebrow, the fact rows, the
/// button label and one call-out — everything structural lives here once.
///
/// The constraints come from where this is read rather than from taste: a 600px table because Outlook
/// desktop has no flexbox, inline styles because a stylesheet is stripped, and no image of any kind because
/// remote content is blocked by default and a message whose meaning depends on a blocked image says nothing.
/// Every interpolated value is HTML-encoded; titles are user-supplied text that routinely contains angle
/// brackets and ampersands.
/// </summary>
internal static class ReviewEmailShell
{
    internal static string Render(
        string subject, string eyebrow, string eyebrowColour, string headline, string standfirst,
        IReadOnlyList<(string Label, string Value)> rows, string buttonLabel, string? link,
        string calloutTitle, string calloutBody, string calloutTint, string calloutRule,
        string calloutInk, string calloutBodyInk, string receivingBecause, string? unsubscribe)
    {
        var html = new StringBuilder();
        html.Append("<!doctype html><html><head><meta charset=\"utf-8\">");
        html.Append("<meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">");
        html.Append($"<title>{E(subject)}</title></head>");
        html.Append("<body style=\"margin:0;padding:0;background:#f3f6f9;\">");
        html.Append("<table role=\"presentation\" cellpadding=\"0\" cellspacing=\"0\" border=\"0\" width=\"100%\" ")
            .Append("style=\"background:#f3f6f9;\"><tr><td align=\"center\" style=\"padding:24px 12px;\">");
        html.Append("<table role=\"presentation\" cellpadding=\"0\" cellspacing=\"0\" border=\"0\" width=\"600\" ")
            .Append("style=\"width:600px;max-width:100%;background:#ffffff;border:1px solid #dce4ea;border-collapse:collapse;\">");

        // A 4px rule rather than a logo block: it identifies the sender without depending on an image that
        // the client will not load.
        html.Append("<tr><td style=\"height:4px;background:#3c9989;font-size:0;line-height:4px;\">&nbsp;</td></tr>");
        html.Append("<tr><td style=\"padding:20px 28px 16px;border-bottom:1px solid #e5ebef;\">")
            .Append("<table role=\"presentation\" cellpadding=\"0\" cellspacing=\"0\" border=\"0\" width=\"100%\"><tr>")
            .Append("<td style=\"font:700 18px Arial,sans-serif;color:#102a44;\">Aero<span style=\"color:#23877d;\">Link</span></td>")
            .Append("</tr></table></td></tr>");

        html.Append("<tr><td style=\"padding:26px 28px 8px;\">")
            .Append($"<div style=\"font:700 12px Arial,sans-serif;letter-spacing:1.4px;color:{eyebrowColour};\">{E(eyebrow)}</div>")
            .Append($"<h1 style=\"margin:10px 0 6px;font:700 26px/1.2 Arial,sans-serif;color:#102a44;\">{E(headline)}</h1>")
            .Append($"<p style=\"margin:0;font:400 15px/1.5 Arial,sans-serif;color:#68798c;\">{E(standfirst)}</p>")
            .Append("</td></tr>");

        html.Append("<tr><td style=\"padding:20px 28px 4px;\">")
            .Append("<table role=\"presentation\" cellpadding=\"0\" cellspacing=\"0\" border=\"0\" width=\"100%\" style=\"border-collapse:collapse;\">");
        for (var index = 0; index < rows.Count; index++)
            Row(html, rows[index].Label, rows[index].Value, last: index == rows.Count - 1);
        html.Append("</table></td></tr>");

        if (link is not null)
        {
            html.Append("<tr><td style=\"padding:22px 28px 6px;\">")
                .Append("<table role=\"presentation\" cellpadding=\"0\" cellspacing=\"0\" border=\"0\" width=\"100%\"><tr>")
                .Append("<td align=\"center\" bgcolor=\"#153b5e\" style=\"background:#153b5e;\">")
                .Append($"<a href=\"{E(link)}\" style=\"display:block;padding:14px 20px;font:700 14px Arial,sans-serif;color:#ffffff;text-decoration:none;\">{E(buttonLabel)}</a>")
                .Append("</td></tr></table>")
                // A button is a link the client may refuse to style. The address is printed as well so the
                // message still works when it arrives as an unstyled block of text.
                .Append("<p style=\"margin:12px 0 0;font:400 12px/1.5 Arial,sans-serif;color:#8492a1;word-break:break-all;\">")
                .Append($"If the button does not work, paste this into your browser:<br>{E(link)}</p></td></tr>");
        }
        else
        {
            html.Append("<tr><td style=\"padding:22px 28px 6px;\">")
                .Append("<p style=\"margin:0;font:400 13px/1.5 Arial,sans-serif;color:#68798c;\">Sign in to AeroLink to act on this. No public address is configured for this deployment, so a direct link could not be included.</p>")
                .Append("</td></tr>");
        }

        html.Append("<tr><td style=\"padding:18px 28px 26px;\">")
            .Append($"<table role=\"presentation\" cellpadding=\"0\" cellspacing=\"0\" border=\"0\" width=\"100%\" style=\"background:{calloutTint};border-left:3px solid {calloutRule};border-collapse:collapse;\"><tr>")
            .Append("<td style=\"padding:13px 14px;\">")
            .Append($"<b style=\"font:700 12px Arial,sans-serif;color:{calloutInk};\">{E(calloutTitle)}</b>")
            .Append($"<p style=\"margin:4px 0 0;font:400 12px/1.45 Arial,sans-serif;color:{calloutBodyInk};\">{E(calloutBody)}</p>")
            .Append("</td></tr></table></td></tr>");

        html.Append("<tr><td style=\"padding:16px 28px 22px;background:#f6f8fa;border-top:1px solid #e5ebef;\">")
            .Append($"<p style=\"margin:0 0 6px;font:400 12px/1.5 Arial,sans-serif;color:#7c8b9b;\">{E(receivingBecause)}</p>");
        if (unsubscribe is not null)
            html.Append("<p style=\"margin:0;font:400 12px/1.5 Arial,sans-serif;color:#7c8b9b;\">")
                .Append($"<a href=\"{E(unsubscribe)}\" style=\"color:#326d8f;\">Turn these emails off</a></p>");
        html.Append("</td></tr>");

        html.Append("</table></td></tr></table></body></html>");
        return html.ToString();
    }

    private static void Row(StringBuilder html, string label, string value, bool last)
    {
        var border = last ? "" : "border-bottom:1px solid #edf1f4;";
        html.Append($"<tr><td style=\"padding:10px 0;{border}font:400 12px Arial,sans-serif;color:#748396;\">{E(label)}</td>")
            .Append($"<td align=\"right\" style=\"padding:10px 0;{border}font:700 12px Arial,sans-serif;color:#2e4357;\">{E(value)}</td></tr>");
    }

    private static string E(string value) => WebUtility.HtmlEncode(value);
}
