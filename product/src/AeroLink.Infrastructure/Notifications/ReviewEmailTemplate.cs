using System.Net;
using System.Text;

namespace AeroLink.Infrastructure.Notifications;

/// <summary>
/// The facts a reviewer needs before they open anything.
///
/// Composed at send time from the review step rather than stored on the notification. A notification row
/// that carried its own copy of the stage, the authority and the package counts would be a snapshot of
/// things that move, and it would go stale the first time an approver is replaced.
/// </summary>
public sealed record ReviewEmailFacts(
    string DisplayNumber,
    string Title,
    string StageName,
    string Authority,
    string Order,
    string SubmittedBy,
    DateTimeOffset SubmittedAt,
    int Introduced,
    int Modified,
    int Retired,
    DateTimeOffset RespondBy);

/// <summary>
/// Renders the review request as a mail client will actually show it.
///
/// Constraints come from where this is read rather than from taste: a 600px table layout because Outlook
/// desktop has no flexbox, inline styles because a stylesheet is stripped, no image of any kind because
/// remote content is blocked by default and an email whose meaning depends on a blocked image says
/// nothing. Every interpolated value is HTML-encoded — a requirement title is user-supplied text that
/// routinely contains angle brackets and ampersands.
/// </summary>
internal static class ReviewEmailTemplate
{
    /// <summary>The identifier and the ask first, the product last — a subject line is read in a list.</summary>
    internal static string Subject(ReviewEmailFacts facts) =>
        $"{facts.DisplayNumber} is ready for your review — AeroLink";

    internal static string PlainText(ReviewEmailFacts facts, string? link, string? unsubscribe)
    {
        var body = new StringBuilder();
        body.AppendLine($"{facts.DisplayNumber} is ready for your review.");
        body.AppendLine(facts.Title);
        body.AppendLine();
        body.AppendLine($"Your stage:            {facts.StageName}");
        body.AppendLine($"Your authority:        {facts.Authority}");
        body.AppendLine($"Order:                 {facts.Order}");
        body.AppendLine($"Submitted by:          {facts.SubmittedBy} · {Date(facts.SubmittedAt)}");
        body.AppendLine($"Package:               {Package(facts)}");
        body.AppendLine($"Response requested by: {Date(facts.RespondBy)}");
        body.AppendLine();
        if (link is not null)
        {
            body.AppendLine("Open the review page:");
            body.AppendLine(link);
        }
        else
        {
            body.AppendLine("Sign in to AeroLink to act on this. (No public address is configured for this");
            body.AppendLine("deployment, so a direct link could not be included.)");
        }
        body.AppendLine();
        body.AppendLine("Approval is recorded on that page with your electronic signature. Nothing in this");
        body.AppendLine("message approves anything, and replying to it changes nothing.");
        if (unsubscribe is not null)
        {
            body.AppendLine();
            body.AppendLine("---");
            body.AppendLine("These messages tell you when a decision is waiting on you. To stop receiving them:");
            body.AppendLine(unsubscribe);
        }
        return body.ToString();
    }

    internal static string Html(ReviewEmailFacts facts, string? link, string? unsubscribe)
    {
        var html = new StringBuilder();
        html.Append("<!doctype html><html><head><meta charset=\"utf-8\">");
        html.Append("<meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">");
        html.Append($"<title>{E(Subject(facts))}</title></head>");
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
            .Append("<div style=\"font:700 12px Arial,sans-serif;letter-spacing:1.4px;color:#6748a8;\">SYSTEM CHANGE REQUEST &middot; REVIEW REQUESTED</div>")
            .Append($"<h1 style=\"margin:10px 0 6px;font:700 26px/1.2 Arial,sans-serif;color:#102a44;\">{E(facts.DisplayNumber)} is ready for your review</h1>")
            .Append($"<p style=\"margin:0;font:400 15px/1.5 Arial,sans-serif;color:#68798c;\">{E(facts.Title)}</p>")
            .Append("</td></tr>");

        html.Append("<tr><td style=\"padding:20px 28px 4px;\">")
            .Append("<table role=\"presentation\" cellpadding=\"0\" cellspacing=\"0\" border=\"0\" width=\"100%\" style=\"border-collapse:collapse;\">");
        Row(html, "Your stage", facts.StageName);
        Row(html, "Your authority", facts.Authority);
        Row(html, "Order", facts.Order);
        Row(html, "Submitted by", $"{facts.SubmittedBy} · {Date(facts.SubmittedAt)}");
        Row(html, "Package", Package(facts));
        Row(html, "Response requested by", Date(facts.RespondBy), last: true);
        html.Append("</table></td></tr>");

        if (link is not null)
        {
            html.Append("<tr><td style=\"padding:22px 28px 6px;\">")
                .Append("<table role=\"presentation\" cellpadding=\"0\" cellspacing=\"0\" border=\"0\" width=\"100%\"><tr>")
                .Append("<td align=\"center\" bgcolor=\"#153b5e\" style=\"background:#153b5e;\">")
                .Append($"<a href=\"{E(link)}\" style=\"display:block;padding:14px 20px;font:700 14px Arial,sans-serif;color:#ffffff;text-decoration:none;\">Open the review page</a>")
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
            .Append("<table role=\"presentation\" cellpadding=\"0\" cellspacing=\"0\" border=\"0\" width=\"100%\" style=\"background:#eef6ff;border-left:3px solid #4b89cf;border-collapse:collapse;\"><tr>")
            .Append("<td style=\"padding:13px 14px;\">")
            .Append("<b style=\"font:700 12px Arial,sans-serif;color:#345979;\">You are signing for the whole submitted package</b>")
            .Append("<p style=\"margin:4px 0 0;font:400 12px/1.45 Arial,sans-serif;color:#5e7489;\">Approval is recorded on the page with your electronic signature. Nothing in this message approves anything, and nothing here can be changed by replying to it.</p>")
            .Append("</td></tr></table></td></tr>");

        html.Append("<tr><td style=\"padding:16px 28px 22px;background:#f6f8fa;border-top:1px solid #e5ebef;\">")
            .Append($"<p style=\"margin:0 0 6px;font:400 12px/1.5 Arial,sans-serif;color:#7c8b9b;\">You are receiving this because you are a named approver on {E(facts.DisplayNumber)}.</p>");
        if (unsubscribe is not null)
            html.Append("<p style=\"margin:0;font:400 12px/1.5 Arial,sans-serif;color:#7c8b9b;\">")
                .Append($"<a href=\"{E(unsubscribe)}\" style=\"color:#326d8f;\">Turn these emails off</a></p>");
        html.Append("</td></tr>");

        html.Append("</table></td></tr></table></body></html>");
        return html.ToString();
    }

    private static void Row(StringBuilder html, string label, string value, bool last = false)
    {
        var border = last ? "" : "border-bottom:1px solid #edf1f4;";
        html.Append($"<tr><td style=\"padding:10px 0;{border}font:400 12px Arial,sans-serif;color:#748396;\">{E(label)}</td>")
            .Append($"<td align=\"right\" style=\"padding:10px 0;{border}font:700 12px Arial,sans-serif;color:#2e4357;\">{E(value)}</td></tr>");
    }

    private static string Package(ReviewEmailFacts facts)
    {
        var parts = new List<string>();
        if (facts.Introduced > 0) parts.Add($"{facts.Introduced} introduced");
        if (facts.Modified > 0) parts.Add($"{facts.Modified} modified");
        if (facts.Retired > 0) parts.Add($"{facts.Retired} retired");
        // A package with no requirement changes is a real and legitimate submission — saying "0 introduced,
        // 0 modified, 0 retired" reads as a fault where it is not one.
        return parts.Count == 0 ? "No requirement changes" : string.Join(" · ", parts);
    }

    private static string Date(DateTimeOffset when) => when.ToString("d MMM yyyy");

    private static string E(string value) => WebUtility.HtmlEncode(value);
}
