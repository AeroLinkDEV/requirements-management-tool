using System.Net;
using System.Text;
using AeroLink.Domain.ChangeControl;

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
    ReviewStageKind StageKind,
    string Authority,
    string Order,
    string SubmittedBy,
    DateTimeOffset SubmittedAt,
    int Introduced,
    int Modified,
    int Retired,
    DateTimeOffset RespondBy,
    string PackageSummary = "");

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
        $"{facts.DisplayNumber} is ready for your {Action(facts)} — AeroLink";

    internal static string PlainText(ReviewEmailFacts facts, string? link, string? unsubscribe)
    {
        var body = new StringBuilder();
        body.AppendLine($"{facts.DisplayNumber} is ready for your {Action(facts)}.");
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
            body.AppendLine($"Open the {Action(facts)} page:");
            body.AppendLine(link);
        }
        else
        {
            body.AppendLine("Sign in to AeroLink to act on this. (No public address is configured for this");
            body.AppendLine("deployment, so a direct link could not be included.)");
        }
        body.AppendLine();
        body.AppendLine("Your decision is recorded on that page with your electronic signature. Nothing in this");
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

    internal static string Html(ReviewEmailFacts facts, string? link, string? unsubscribe) =>
        ReviewEmailShell.Render(
            subject: Subject(facts),
            eyebrow: $"CONTROLLED CHANGE · {Action(facts).ToUpperInvariant()} REQUESTED",
            eyebrowColour: "#6748a8",
            headline: $"{facts.DisplayNumber} is ready for your {Action(facts)}",
            standfirst: facts.Title,
            rows:
            [
                ("Your stage", facts.StageName),
                ("Your authority", facts.Authority),
                ("Order", facts.Order),
                ("Submitted by", $"{facts.SubmittedBy} · {Date(facts.SubmittedAt)}"),
                ("Package", Package(facts)),
                ("Response requested by", Date(facts.RespondBy)),
            ],
            buttonLabel: $"Open the {Action(facts)} page",
            link: link,
            calloutTitle: "You are signing for the whole submitted package",
            calloutBody: "Your decision is recorded on the page with your electronic signature. Nothing in this message approves anything, and nothing here can be changed by replying to it.",
            calloutTint: "#eef6ff",
            calloutRule: "#4b89cf",
            calloutInk: "#345979",
            calloutBodyInk: "#5e7489",
            receivingBecause: $"You are receiving this because you have an active {Action(facts)} obligation on {facts.DisplayNumber}.",
            unsubscribe: unsubscribe);

    private static void Row(StringBuilder html, string label, string value, bool last = false)
    {
        var border = last ? "" : "border-bottom:1px solid #edf1f4;";
        html.Append($"<tr><td style=\"padding:10px 0;{border}font:400 12px Arial,sans-serif;color:#748396;\">{E(label)}</td>")
            .Append($"<td align=\"right\" style=\"padding:10px 0;{border}font:700 12px Arial,sans-serif;color:#2e4357;\">{E(value)}</td></tr>");
    }

    private static string Package(ReviewEmailFacts facts)
    {
        if (!string.IsNullOrWhiteSpace(facts.PackageSummary)) return facts.PackageSummary;
        var parts = new List<string>();
        if (facts.Introduced > 0) parts.Add($"{facts.Introduced} introduced");
        if (facts.Modified > 0) parts.Add($"{facts.Modified} modified");
        if (facts.Retired > 0) parts.Add($"{facts.Retired} retired");
        // A package with no requirement changes is a real and legitimate submission — saying "0 introduced,
        // 0 modified, 0 retired" reads as a fault where it is not one.
        return parts.Count == 0 ? "No requirement changes" : string.Join(" · ", parts);
    }

    private static string Date(DateTimeOffset when) => when.ToString("d MMM yyyy");

    private static string Action(ReviewEmailFacts facts) => facts.StageKind switch
    {
        ReviewStageKind.Review => "review",
        ReviewStageKind.Approval => "approval",
        _ => throw new InvalidOperationException($"Unsupported frozen review stage kind '{facts.StageKind}'.")
    };

    private static string E(string value) => WebUtility.HtmlEncode(value);
}
