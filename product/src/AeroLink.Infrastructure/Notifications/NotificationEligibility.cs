using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Documents;
using AeroLink.Domain.Identity;
using AeroLink.Domain.Notifications;
using AeroLink.Domain.Requirements;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AeroLink.Infrastructure.Notifications;

public sealed record NotificationEligibilityResult(bool Eligible, string SafeCode, string Address = "");

public sealed class NotificationEligibility(AeroLinkDbContext db, IdentityService identity)
{
    public async Task<NotificationEligibilityResult> EvaluateAsync(NotificationContext context, CancellationToken ct, bool deliveryPolicy = true)
    {
        var account = await db.UserAccounts.AsNoTracking().SingleOrDefaultAsync(x => x.UserName == context.Recipient, ct);
        if (account?.State != AccountState.Active) return new(false, "RecipientInactive");
        if (context.SourceFamily == NotificationSourceFamily.DiagnosticOperation)
            return account.UserName == IdentityService.SystemAdministratorUserName
                ? new(true, "", account.Email) : new(false, "DiagnosticAuthorityRemoved");
        var program = await db.Projects.AsNoTracking().Where(x => x.Id == context.ProjectId).Select(x => (Guid?)x.ProgramId).SingleOrDefaultAsync(ct);
        if (program is null || account.UserName != IdentityService.SystemAdministratorUserName
            && !await db.ProgramMemberships.AsNoTracking().AnyAsync(x => x.UserId == account.Id && x.ProgramId == program && x.EndedAt == null, ct))
            return new(false, "ProjectAccessRemoved");
        if (deliveryPolicy && await db.NotificationPreferences.AsNoTracking().AnyAsync(x => x.Recipient == context.Recipient && !x.EmailEnabled, ct))
            return new(false, "RecipientOptedOut");
        if (deliveryPolicy && !MimeKit.MailboxAddress.TryParse(account.Email, out _)) return new(false, "RecipientAddressMissing");
        var active = context.SourceFamily switch
        {
            NotificationSourceFamily.CRStep => await ChangeStepActiveAsync(context, false, account.Id, program.Value, ct),
            NotificationSourceFamily.TCRStep => await ChangeStepActiveAsync(context, true, account.Id, program.Value, ct),
            NotificationSourceFamily.DocStep => await DocumentStepActiveAsync(context, ct),
            NotificationSourceFamily.RequirementAssignment => await db.ArtifactAssignments.AsNoTracking().AnyAsync(x => x.Id == context.ArtifactAssignmentId
                && x.ProjectId == context.ProjectId && x.ArtifactId == context.RecordId && x.State == AssignmentState.Open && x.AssignedTo == context.Recipient, ct),
            NotificationSourceFamily.DocumentAssignment => await DocumentAssignmentActiveAsync(context, account.Id, program.Value, ct),
            NotificationSourceFamily.CRAudit => await ChangeReturnActiveAsync(context, ct),
            NotificationSourceFamily.DocEvent => await DocumentReturnActiveAsync(context, ct),
            _ => false,
        };
        return active ? new(true, "", account.Email) : new(false, "OriginalObligationEnded");
    }

    private async Task<bool> ChangeStepActiveAsync(NotificationContext context, bool test, Guid userId, Guid programId, CancellationToken ct)
    {
        var cycle = await db.ReviewCycles.AsNoTracking().SingleOrDefaultAsync(x => x.Id == context.ReviewCycleId, ct);
        var step = await db.ApprovalSteps.AsNoTracking().SingleOrDefaultAsync(x => x.Id == context.ApprovalStepId, ct);
        if (cycle is null || step is null || cycle.State != ReviewCycleState.Active || step.ReviewCycleId != cycle.Id
            || step.State != ApprovalStepState.Active || step.ApproverId != context.Recipient || cycle.SnapshotHash != context.SnapshotHash) return false;
        // Match the live signature gate: configured workflows carry frozen step authority; legacy
        // cycles additionally require current Approver authority through the established resolver.
        if (cycle.WorkflowId is null && !await identity.HasRoleAsync(userId, programId, ProgramRole.Approver, DateTimeOffset.UtcNow, ct)) return false;
        return test
            ? cycle.TestChangeReviewId == context.RecordId && await db.TestChangeReviews.AsNoTracking().AnyAsync(x => x.Id == context.RecordId && x.ProjectId == context.ProjectId && x.Revision == context.Revision, ct)
            : cycle.ChangeRequestId == context.RecordId && await db.SystemChangeRequests.AsNoTracking().AnyAsync(x => x.Id == context.RecordId && x.ProjectId == context.ProjectId && x.Revision == context.Revision, ct);
    }
    private async Task<bool> DocumentStepActiveAsync(NotificationContext context, CancellationToken ct)
    {
        var revision = await db.ManagedDocumentRevisions.AsNoTracking().SingleOrDefaultAsync(x => x.Id == context.DocumentRevisionId, ct);
        var step = await db.ManagedDocumentReviewSteps.AsNoTracking().SingleOrDefaultAsync(x => x.Id == context.DocumentStepId, ct);
        if (revision is null || step is null || revision.DocumentId != context.DocumentId || revision.State != ManagedDocumentState.InReview
            || revision.SnapshotHash != context.SnapshotHash || step.RevisionId != revision.Id || step.Cycle != context.Cycle
            || step.State != ManagedDocumentReviewStepState.Active || step.ApproverId != context.Recipient) return false;
        return await db.ManagedDocumentReviewSteps.AsNoTracking().Where(x => x.RevisionId == revision.Id).MaxAsync(x => x.Cycle, ct) == context.Cycle;
    }
    private async Task<bool> ChangeReturnActiveAsync(NotificationContext context, CancellationToken ct)
    {
        var record = await db.SystemChangeRequests.AsNoTracking().SingleOrDefaultAsync(x => x.Id == context.ChangeRequestId, ct);
        if (record is null || record.AuthorId != context.Recipient || record.State != ChangeRequestState.Draft || record.Revision != context.Revision) return false;
        if (!await db.ReviewCycles.AsNoTracking().AnyAsync(x => x.Id == context.ReviewCycleId && x.ChangeRequestId == record.Id && x.State == ReviewCycleState.ChangesRequested, ct)) return false;
        return await db.ReviewCycles.AsNoTracking().Where(x => x.ChangeRequestId == record.Id).MaxAsync(x => x.Sequence, ct) == context.Cycle;
    }
    private async Task<bool> DocumentReturnActiveAsync(NotificationContext context, CancellationToken ct)
    {
        var revision = await db.ManagedDocumentRevisions.AsNoTracking().SingleOrDefaultAsync(x => x.Id == context.DocumentRevisionId, ct);
        if (revision is null || revision.DocumentId != context.DocumentId || revision.ResponsibleOwnerId != context.Recipient || revision.State != ManagedDocumentState.Returned) return false;
        return await db.ManagedDocumentReviewSteps.AsNoTracking().Where(x => x.RevisionId == revision.Id).MaxAsync(x => x.Cycle, ct) == context.Cycle;
    }
    private async Task<bool> DocumentAssignmentActiveAsync(NotificationContext context, Guid userId, Guid programId, CancellationToken ct)
    {
        // Ownership is an exact event selector. Matching a name alone would revive an old A→B→A notice.
        var assignment = await db.ManagedDocumentAssignments.AsNoTracking().SingleOrDefaultAsync(x => x.Id == context.DocumentAssignmentId, ct);
        if (assignment is null || assignment.NewAssigneeId != context.Recipient) return false;
        var current = context.DocumentRevisionId is Guid revisionId
            ? await db.ManagedDocumentRevisions.AsNoTracking().AnyAsync(x => x.Id == revisionId && x.CurrentResponsibleAssignmentId == assignment.Id && x.ResponsibleOwnerId == context.Recipient, ct)
            : await db.ManagedDocuments.AsNoTracking().AnyAsync(x => x.Id == context.DocumentId && x.CurrentStewardAssignmentId == assignment.Id && x.StewardId == context.Recipient, ct);
        return current && await identity.HasRoleAsync(userId, programId, ProgramRole.Engineer, DateTimeOffset.UtcNow, ct);
    }
}
