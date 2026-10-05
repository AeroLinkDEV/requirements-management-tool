using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Documents;
using AeroLink.Domain.Notifications;
using AeroLink.Domain.Requirements;
using Microsoft.EntityFrameworkCore;

namespace AeroLink.Infrastructure.Tests;

// #1482 test-audit: old rich title/person/five-day template comparisons are retired with their unused
// production templates. Retain the separate document obligation boundary: immutable round/step identity
// and frozen approval kind must survive current workflow changes. Capture MIME privacy/no-attachment
// is owned by NotificationOutboxTests; actual external TLS transport is independently owned.
public sealed class DocumentReviewEmailTests
{
    [Fact]
    public async Task A_document_notice_preserves_the_exact_frozen_approval_step_and_identifier()
    {
        using var fixture = await NotificationInfrastructureFixture.CreateAsync();
        NotificationContext context;
        await using (var db = fixture.NewDb())
        {
            var document = new ManagedDocument(fixture.ProjectId, "SDP-000001", "SDP", "Plan",
                "Private delivery plan title", "owner.user", DateTimeOffset.UtcNow);
            var revision = new ManagedDocumentRevision(document.Id, 0, "owner.user", "Initial issue.", DateTimeOffset.UtcNow);
            revision.RecordCheckIn(Guid.NewGuid(), DateTimeOffset.UtcNow);
            revision.SubmitForReview("owner.user", new string('a', 64),
                [new("technical.user", "Technical User", "Technical review"),
                 new("approver.user", "Approver User", "Release approval", Kind: ReviewStageKind.Approval)], DateTimeOffset.UtcNow);
            revision.Approve("technical.user", "Technically complete.", DateTimeOffset.UtcNow);
            var step = Assert.Single(revision.ReviewSteps.Where(x => x.State == ManagedDocumentReviewStepState.Active));
            var notice = new UserNotification(fixture.ProjectId, "approver.user", "DocumentApprovalActivated",
                "Private document title", "Private release approval detail", $"managed-document:{document.Id}",
                document.Id, DateTimeOffset.UtcNow);
            notice.BindContext(NotificationContext.DocumentStep(notice, document, revision, step));
            context = notice.Context!;
            db.AddRange(document, revision, notice);
            await db.SaveChangesAsync();
        }
        Assert.Equal("SDP-000001.00", context.Identifier);
        Assert.Equal("Approval", context.Stage);
        Assert.Equal(NotificationSourceFamily.DocStep, context.SourceFamily);
        Assert.Equal(context.SourceId, context.DocumentStepId);
        Assert.Equal(1, context.Cycle);
        Assert.True((await fixture.EligibleAsync(context)).Eligible);
        await using var asserted = fixture.NewDb();
        Assert.Equal(context.SourceId, (await asserted.NotificationContexts.SingleAsync()).SourceId);
    }

    [Fact]
    public async Task A_returned_document_round_cannot_be_replaced_by_a_later_round_for_the_same_reviewer()
    {
        using var fixture = await NotificationInfrastructureFixture.CreateAsync();
        NotificationContext original;
        NotificationContext later;
        await using (var db = fixture.NewDb())
        {
            var document = new ManagedDocument(fixture.ProjectId, "SDP-000002", "SDP", "Plan",
                "Private returned plan", "owner.user", DateTimeOffset.UtcNow);
            var revision = new ManagedDocumentRevision(document.Id, 0, "owner.user", "Initial issue.", DateTimeOffset.UtcNow);
            revision.RecordCheckIn(Guid.NewGuid(), DateTimeOffset.UtcNow);
            revision.SubmitForReview("owner.user", new string('b', 64),
                [new("approver.user", "Approver User", "Technical review"),
                 new("final.user", "Final User", "Release approval", Kind: ReviewStageKind.Approval)], DateTimeOffset.UtcNow);
            var firstStep = Assert.Single(revision.ReviewSteps.Where(x => x.State == ManagedDocumentReviewStepState.Active));
            var notice = Notice(fixture.ProjectId, document, revision, firstStep);
            original = notice.Context!;
            db.AddRange(document, revision, notice);
            await db.SaveChangesAsync();
            Assert.True((await fixture.EligibleAsync(original)).Eligible);
            revision.Return("approver.user", "Revise the document.", DateTimeOffset.UtcNow);
            revision.RecordCheckIn(Guid.NewGuid(), DateTimeOffset.UtcNow);
            revision.SubmitForReview("owner.user", new string('c', 64),
                [new("approver.user", "Approver User", "Technical review"),
                 new("final.user", "Final User", "Release approval", Kind: ReviewStageKind.Approval)], DateTimeOffset.UtcNow);
            // Match the production submit endpoint: persisted revisions explicitly add their new round.
            db.ManagedDocumentReviewSteps.AddRange(revision.ReviewSteps.Where(x => x.Cycle == revision.CurrentReviewCycle));
            var nextStep = Assert.Single(revision.ReviewSteps.Where(x => x.Cycle == revision.CurrentReviewCycle && x.State == ManagedDocumentReviewStepState.Active));
            var nextNotice = Notice(fixture.ProjectId, document, revision, nextStep);
            later = nextNotice.Context!;
            db.UserNotifications.Add(nextNotice);
            await db.SaveChangesAsync();
        }
        var ended = await fixture.EligibleAsync(original);
        Assert.False(ended.Eligible);
        Assert.Equal("OriginalObligationEnded", ended.SafeCode);
        Assert.True((await fixture.EligibleAsync(later)).Eligible);
        Assert.NotEqual(original.DocumentStepId, later.DocumentStepId);
        Assert.Equal(1, original.Cycle);
        Assert.Equal(2, later.Cycle);
    }

    private static UserNotification Notice(Guid projectId, ManagedDocument document, ManagedDocumentRevision revision,
        ManagedDocumentReviewStep step)
    {
        var notice = new UserNotification(projectId, step.ApproverId, "DocumentReviewActivated",
            "Private document title", "Private review prose", $"managed-document:{document.Id}", document.Id, DateTimeOffset.UtcNow);
        notice.BindContext(NotificationContext.DocumentStep(notice, document, revision, step));
        return notice;
    }
}
