using AeroLink.Domain.ChangeControl;

namespace AeroLink.Api.Tests;

public sealed class ReviewNotificationFactoryTests
{
    [Theory]
    [InlineData("ChangeRequest", ReviewStageKind.Review, "ReviewActivated", "Review SRCR-00050.00", "authorized to review")]
    [InlineData("ChangeRequest", ReviewStageKind.Approval, "ApprovalActivated", "Approve SRCR-00050.00", "authorized to approve")]
    [InlineData("TestChangeRequest", ReviewStageKind.Review, "ReviewActivated", "Review SYSTPCR-00010.00", "selected you to review")]
    [InlineData("TestChangeRequest", ReviewStageKind.Approval, "TestChangeRequestApprovalRequested", "Approve test change assessment", "selected you to approve")]
    [InlineData("ManagedDocument", ReviewStageKind.Review, "DocumentReviewActivated", "Review SDP-000001.00", "ready for your review")]
    [InlineData("ManagedDocument", ReviewStageKind.Approval, "DocumentApprovalActivated", "Approve SDP-000001.00", "ready for your approval")]
    public void Frozen_stage_kind_owns_in_product_request_type_and_wording(string family,
        ReviewStageKind stageKind, string expectedType, string expectedTitle, string expectedDetail)
    {
        var projectId = Guid.NewGuid();
        var artifactId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var notification = family switch
        {
            "ChangeRequest" => ReviewNotificationFactory.ForChangeRequest(projectId, "reviewer.user", stageKind,
                "SRCR-00050.00", "Oceanic routing", $"scr:{artifactId}", artifactId, now),
            "TestChangeRequest" => ReviewNotificationFactory.ForTestChangeRequest(projectId, "reviewer.user",
                stageKind, stageKind == ReviewStageKind.Approval ? "" : "SYSTPCR-00010.00", "Review Author",
                $"test-change-request:{artifactId}", artifactId, now),
            "ManagedDocument" => ReviewNotificationFactory.ForManagedDocument(projectId, "reviewer.user",
                stageKind, "SDP-000001.00", "Release authorization", $"managed-document:{artifactId}",
                artifactId, now),
            _ => throw new ArgumentOutOfRangeException(nameof(family), family, "Unknown test family."),
        };

        Assert.Equal(expectedType, notification.Type);
        Assert.Equal(expectedTitle, notification.Title);
        Assert.Contains(expectedDetail, notification.Detail);
        if (family == "TestChangeRequest" && stageKind == ReviewStageKind.Approval)
            Assert.DoesNotContain("SYSTPCR-", notification.Title);
    }
}
