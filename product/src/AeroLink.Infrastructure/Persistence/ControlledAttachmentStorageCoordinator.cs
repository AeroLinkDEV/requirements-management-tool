using AeroLink.Domain.Requirements;
using AeroLink.Domain.Identity;
using Microsoft.EntityFrameworkCore;

namespace AeroLink.Infrastructure.Persistence;

/// <summary>
/// Reconciles inline-image filesystem work which outlives its request. The database operation is the durable
/// intent; an object is never adopted after a checkout has closed, and an object that cannot be verified is
/// held for repair rather than silently becoming controlled content.
/// </summary>
public sealed class ControlledAttachmentStorageCoordinator(AeroLinkDbContext db, EvidenceFileStore files)
{
    public static readonly TimeSpan PendingOperationLease = TimeSpan.FromMinutes(30);
    private static readonly ProgramRole[] InlineImageAuthorRoles =
        [ProgramRole.Engineer, ProgramRole.TestEngineer, ProgramRole.ConfigurationManager, ProgramRole.ProgramManager];

    /// <summary>
    /// Re-evaluates the actor against current database authority rather than the Program list captured when
    /// the request was authenticated. Long uploads cross a meaningful authorization window, and this helper
    /// is shared by both live commit transactions and crash recovery so neither can adopt bytes after the
    /// author left the Project or lost the role required for an unclaimed recovery image.
    /// </summary>
    public async Task<bool> HasCurrentUploadAuthorityAsync(Guid projectId, string userName,
        bool hasEditSession, DateTimeOffset now, CancellationToken ct)
    {
        var actor = await db.UserAccounts.AsNoTracking()
            .Where(x => x.UserName == userName && x.State == AccountState.Active)
            .Select(x => new { x.Id, x.UserName })
            .SingleOrDefaultAsync(ct);
        if (actor is null) return false;
        if (actor.UserName == IdentityService.SystemAdministratorUserName) return true;

        var programId = await db.Projects.AsNoTracking().Where(x => x.Id == projectId)
            .Select(x => (Guid?)x.ProgramId).SingleOrDefaultAsync(ct);
        if (programId is null) return false;
        if (hasEditSession)
            return await db.ProgramMemberships.AsNoTracking().AnyAsync(x => x.UserId == actor.Id
                && x.ProgramId == programId.Value && x.EndedAt == null, ct);

        var resolver = new ProjectAuthorityResolver(db);
        foreach (var role in InlineImageAuthorRoles)
            if (await resolver.IsSatisfiedAsync(actor.Id, programId.Value,
                    ProjectAuthorityRequirement.LegacyRoleDemand(role), now, ct))
                return true;
        return false;
    }

    public async Task RollBackAsync(ControlledAttachmentStorageOperation operation, string detail,
        DateTimeOffset now, CancellationToken ct)
    {
        Quarantine(operation);
        operation.RollBack(detail, now);
        await db.SaveChangesAsync(ct);
    }

    public async Task<ControlledAttachment?> ReconcileAsync(ControlledAttachmentStorageOperation operation,
        string actor, DateTimeOffset now, CancellationToken ct)
    {
        if (operation.State == ControlledAttachmentStorageOperationState.Available)
            return operation.AttachmentId is Guid attachmentId
                ? await db.ControlledAttachments.SingleOrDefaultAsync(x => x.Id == attachmentId, ct)
                : null;

        // Expired browser-recovery rows use the same durable operation table, but their intent is
        // reclamation rather than adoption. Never run them through the normal pending-object path: doing so
        // would recreate a controlled attachment after its recovery row was intentionally removed.
        if (operation.ArtifactType == "InlineImageDraftCleanup")
        {
            await using var cleanupTransaction = await db.Database.BeginTransactionAsync(
                System.Data.IsolationLevel.Serializable, ct);
            try
            {
                if (files.Exists(operation.StorageKey))
                    files.Delete(operation.StorageKey);
                if (files.Exists(operation.StagingKey))
                    files.Delete(operation.StagingKey);
                operation.CompleteCleanup(now);
                await db.SaveChangesAsync(ct);
                await cleanupTransaction.CommitAsync(ct);
            }
            catch (Exception ex) when (ex is IOException or EvidenceIntegrityException or UnauthorizedAccessException)
            {
                operation.RequireRepair($"Expired inline-image cleanup could not remove its object: {ex.Message}", now);
                await db.SaveChangesAsync(ct);
                await cleanupTransaction.CommitAsync(ct);
            }
            return null;
        }

        await using var transaction = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, ct);
        if (!await HasCurrentUploadAuthorityAsync(operation.ProjectId, operation.Actor,
                operation.EditSessionId is not null, now, ct))
        {
            Quarantine(operation);
            operation.RollBack("The image author no longer has authority to commit content in this Project.", now);
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return null;
        }
        if (operation.EditSessionId is Guid sessionId)
        {
            var session = await ArtifactEditSessionLock.AcquireAsync(db, sessionId, ct);
            if (session is null || !session.IsExclusive || session.State != EditSessionState.Active
                || session.ProjectId != operation.ProjectId || !string.Equals(session.UserName, operation.Actor,
                    StringComparison.OrdinalIgnoreCase) || session.ExpiresAt <= now)
            {
                Quarantine(operation);
                operation.RollBack("The checkout closed or expired before the staged image could be committed.", now);
                await db.SaveChangesAsync(ct);
                await transaction.CommitAsync(ct);
                return null;
            }
        }

        var existing = await db.ControlledAttachments.SingleOrDefaultAsync(x => x.StorageKey == operation.StorageKey, ct);
        if (existing is not null)
        {
            operation.Complete(existing.Id, now);
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return existing;
        }

        try
        {
            if (!files.Exists(operation.StorageKey) && files.Exists(operation.StagingKey))
                await files.PromoteAsync(new StagedEvidence(operation.OriginalFileName, operation.ContentType,
                    operation.Size, operation.Sha256, operation.StagingKey, operation.StorageKey), ct);
            await using var verified = await files.OpenVerifiedReadAsync(operation.StorageKey, operation.Size,
                operation.Sha256, ct);
        }
        catch (EvidenceIntegrityException ex)
        {
            operation.RequireRepair($"Promoted inline-image object failed verification ({ex.Code}); operator reconciliation is required.", now);
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return null;
        }

        var attachment = new ControlledAttachment(operation.ProjectId, operation.ArtifactType, operation.ArtifactId,
            operation.RevisionId, operation.LogicalId, operation.Version, operation.Label, "",
            operation.OriginalFileName, operation.ContentType, operation.Size, operation.Sha256, operation.StorageKey,
            null, operation.Actor, operation.CreatedAt);
        db.ControlledAttachments.Add(attachment);
        operation.Complete(attachment.Id, now);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return attachment;
    }

    public async Task<int> ReconcileProjectAsync(Guid projectId, string actor, DateTimeOffset now,
        CancellationToken ct)
    {
        var operations = await db.ControlledAttachmentStorageOperations
            .Where(x => x.ProjectId == projectId
                && (x.State == ControlledAttachmentStorageOperationState.Pending
                    || x.State == ControlledAttachmentStorageOperationState.RepairRequired))
            .ToListAsync(ct);
        var count = 0;
        foreach (var operation in operations.Where(x => x.State == ControlledAttachmentStorageOperationState.RepairRequired
            || x.UpdatedAt <= now - PendingOperationLease))
        {
            await ReconcileAsync(operation, actor, now, ct);
            count++;
        }
        return count;
    }

    private void Quarantine(ControlledAttachmentStorageOperation operation)
    {
        TryQuarantine(operation.StagingKey, operation.Id, "rollback-stage");
        TryQuarantine(operation.StorageKey, operation.Id, "rollback-object");
    }

    private void TryQuarantine(string key, Guid operationId, string reason)
    {
        try { files.Quarantine(key, operationId, reason); }
        catch (EvidenceIntegrityException) { /* The operation remains auditable as rolled back/repair-required. */ }
    }
}
