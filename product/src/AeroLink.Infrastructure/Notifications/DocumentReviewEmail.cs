using System.Text;
using AeroLink.Domain.ChangeControl;

namespace AeroLink.Infrastructure.Notifications;

/// <summary>
/// The facts a document reviewer needs before they open anything.
///
/// Composed at send time from the review step, for the same reason the change request facts are: a
/// notification row carrying its own copy of the stage and the steward is a snapshot of things that move.
/// </summary>
public sealed record DocumentReviewEmailFacts(
    string DisplayNumber,
    string Title,
    string StageName,
    ReviewStageKind StageKind,
    string Authority,
    string CheckedInBy,
    string Steward,
    DateTimeOffset SubmittedAt,
    DateTimeOffset RespondBy);

/// <summary>
/// The document review request, in the forms a mail client will show it.
///
/// Deliberately the same shell as <see cref="ReviewEmailTemplate"/> — 600px table, inline styles, no image
/// of any kind — so the two reviews read as one product rather than two. What differs is the eyebrow, the
/// fact rows, and one paragraph that exists only here: the document is not attached.
/// </summary>
internal static class DocumentReviewEmailTemplate
{
    internal static string Subject(DocumentReviewEmailFacts facts) =>
        $"{facts.DisplayNumber} is ready for your {Action(facts)} — AeroLink";

    internal static string PlainText(DocumentReviewEmailFacts facts, string? link, string? unsubscribe)
    {
        var body = new StringBuilder();
        body.AppendLine($"{facts.DisplayNumber} is ready for your {Action(facts)}.");
        body.AppendLine(facts.Title);
        body.AppendLine();
        body.AppendLine($"Your stage:            {facts.StageName}");
        body.AppendLine($"Your authority:        {facts.Authority}");
        body.AppendLine($"Checked in by:         {facts.CheckedInBy} · {Date(facts.SubmittedAt)}");
        body.AppendLine($"Steward:               {facts.Steward}");
        body.AppendLine($"Response requested by: {Date(facts.RespondBy)}");
        body.AppendLine();
        if (link is not null)
        {
            body.AppendLine($"Open the document {Action(facts)}:");
            body.AppendLine(link);
        }
        else
        {
            body.AppendLine("Sign in to AeroLink to act on this. (No public address is configured for this");
            body.AppendLine("deployment, so a direct link could not be included.)");
        }
        body.AppendLine();
        // The one thing this message says that the change request one does not.
        body.AppendLine("No attachment is sent. Open the record to read the exact checked-in document under");
        body.AppendLine("review and record your signature over its hash. A mailed copy is a second artifact");
        body.AppendLine("that can drift from the controlled one.");
        if (unsubscribe is not null)
        {
            body.AppendLine();
            body.AppendLine("---");
            body.AppendLine("These messages tell you when a decision is waiting on you. To stop receiving them:");
            body.AppendLine(unsubscribe);
        }
        return body.ToString();
    }

    internal static string Html(DocumentReviewEmailFacts facts, string? link, string? unsubscribe)
    {
        var rows = new (string Label, string Value)[]
        {
            ("Your stage", facts.StageName),
            ("Your authority", facts.Authority),
            ("Checked in by", $"{facts.CheckedInBy} · {Date(facts.SubmittedAt)}"),
            ("Steward", facts.Steward),
            ("Response requested by", Date(facts.RespondBy)),
        };
        return ReviewEmailShell.Render(
            subject: Subject(facts),
            // Green rather than the change request's purple: a reader with both in one inbox can tell which
            // kind of decision is waiting before reading a word.
            eyebrow: $"CONTROLLED DOCUMENT · {Action(facts).ToUpperInvariant()} REQUESTED",
            eyebrowColour: "#24735d",
            headline: $"{facts.DisplayNumber} is ready for your {Action(facts)}",
            standfirst: facts.Title,
            rows: rows,
            buttonLabel: $"Open the document {Action(facts)}",
            link: link,
            calloutTitle: "The file stays in AeroLink",
            calloutBody: "No attachment is sent. Open the record to read the exact checked-in document under review and record your signature over its hash.",
            calloutTint: "#eaf7f4",
            calloutRule: "#3b9d8e",
            calloutInk: "#315f5b",
            calloutBodyInk: "#68798b",
            receivingBecause: $"You are receiving this because you have an active {Action(facts)} obligation on {facts.DisplayNumber}.",
            unsubscribe: unsubscribe);
    }

    private static string Action(DocumentReviewEmailFacts facts) => facts.StageKind switch
    {
        ReviewStageKind.Review => "review",
        ReviewStageKind.Approval => "approval",
        _ => throw new InvalidOperationException($"Unsupported frozen document review stage kind '{facts.StageKind}'.")
    };

    private static string Date(DateTimeOffset when) => when.ToString("d MMM yyyy");
}
