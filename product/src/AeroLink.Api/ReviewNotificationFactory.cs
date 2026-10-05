using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Requirements;
using AeroLink.Domain.Notifications;
using AeroLink.Domain.Documents;
using AeroLink.Domain.Verification;

namespace AeroLink.Api;

/// <summary>
/// Creates the in-product request that accompanies a frozen review-workflow obligation.
/// The persisted step kind is the authority: notification type and prose must never infer
/// Review versus Approval from a role, position, family, or today's workflow configuration.
/// </summary>
internal static class ReviewNotificationFactory
{
    public static UserNotification ForChangeRequest(Guid projectId, string recipient, ReviewStageKind stageKind,
        string displayNumber, string title, string route, Guid artifactId, DateTimeOffset now,
        bool priorStageComplete = false, SystemChangeRequest? record = null, ReviewCycle? cycle = null, ApprovalStep? step = null)
    {
        var action = ActionFor(stageKind);
        var type = stageKind switch
        {
            ReviewStageKind.Review => "ReviewActivated",
            ReviewStageKind.Approval => "ApprovalActivated",
            _ => throw new ArgumentOutOfRangeException(nameof(stageKind), stageKind, "Unknown frozen review stage kind."),
        };
        var prefix = priorStageComplete ? "The prior stage is complete. " : "";
        var notice = new UserNotification(projectId, recipient, type, $"{action.Imperative} {displayNumber}",
            $"{prefix}You are now authorized to {action.Verb} {displayNumber}: {title}",
            route, artifactId, now);
        return record is null ? notice : notice.BindContext(NotificationContext.ChangeRequestStep(notice, record, cycle!, step!));
    }

    public static UserNotification ForTestChangeRequest(Guid projectId, string recipient,
        ReviewStageKind stageKind, string displayNumber, string selectedBy, string route, Guid artifactId,
        DateTimeOffset now, bool priorStageComplete = false, TestChangeReview? record = null, ReviewCycle? cycle = null, ApprovalStep? step = null)
    {
        var action = ActionFor(stageKind);
        var type = stageKind switch
        {
            ReviewStageKind.Review => "ReviewActivated",
            ReviewStageKind.Approval => "TestChangeRequestApprovalRequested",
            _ => throw new ArgumentOutOfRangeException(nameof(stageKind), stageKind, "Unknown frozen review stage kind."),
        };
        var identity = string.IsNullOrWhiteSpace(displayNumber) ? "test change assessment" : displayNumber;
        var detail = priorStageComplete
            ? $"The prior stage is complete. You are now authorized to {action.Verb} {identity}."
            : $"{selectedBy} selected you to {action.Verb} this test change request.";
        var notice = new UserNotification(projectId, recipient, type, $"{action.Imperative} {identity}", detail,
            route, artifactId, now);
        return record is null ? notice : notice.BindContext(NotificationContext.TestChangeStep(notice, record, cycle!, step!));
    }

    public static UserNotification ForManagedDocument(Guid projectId, string recipient,
        ReviewStageKind stageKind, string displayNumber, string stageName, string route, Guid artifactId,
        DateTimeOffset now, ManagedDocument? document = null, ManagedDocumentRevision? revision = null, ManagedDocumentReviewStep? step = null)
    {
        var action = ActionFor(stageKind);
        var type = stageKind switch
        {
            ReviewStageKind.Review => "DocumentReviewActivated",
            ReviewStageKind.Approval => "DocumentApprovalActivated",
            _ => throw new ArgumentOutOfRangeException(nameof(stageKind), stageKind, "Unknown frozen review stage kind."),
        };
        var notice = new UserNotification(projectId, recipient, type, $"{action.Imperative} {displayNumber}",
            $"{stageName} is ready for your {action.Noun}.", route, artifactId, now);
        return document is null ? notice : notice.BindContext(NotificationContext.DocumentStep(notice, document, revision!, step!));
    }

    private static ReviewAction ActionFor(ReviewStageKind stageKind) => stageKind switch
    {
        ReviewStageKind.Review => new("Review", "review", "review"),
        ReviewStageKind.Approval => new("Approve", "approve", "approval"),
        _ => throw new ArgumentOutOfRangeException(nameof(stageKind), stageKind, "Unknown frozen review stage kind."),
    };

    private readonly record struct ReviewAction(string Imperative, string Verb, string Noun);
}
