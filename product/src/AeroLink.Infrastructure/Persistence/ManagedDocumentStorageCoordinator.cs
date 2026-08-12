using System.Text.Json;
using AeroLink.Domain.Common;
using AeroLink.Domain.Documents;
using AeroLink.Domain.Requirements;
using Microsoft.EntityFrameworkCore;

namespace AeroLink.Infrastructure.Persistence;

public sealed record ManagedDocumentStagedObject(string Slot, Guid AttachmentId, string StagingKey,
    string StorageKey, long Size, string Sha256);
public sealed record ManagedDocumentStorageStart(ManagedDocumentStorageOperation Operation, string? ExistingResult);
public sealed record ManagedDocumentStorageObjectEvidence(Guid OperationId, string Slot, string StorageKey,
    long Size, string Sha256, ManagedDocumentStorageOperationState State);
public sealed record ManagedDocumentStorageReconciliation(int Checked, int Completed, int RolledBack,
    int RepairRequired, IReadOnlyList<string> QuarantinedKeys, IReadOnlyList<Guid> OperationIds,
    IReadOnlyList<ManagedDocumentStorageObjectEvidence> Objects);

public sealed class ManagedDocumentStorageConflictException(string code, string message) : InvalidOperationException(message)
{ public string Code { get; } = code; }

public interface IManagedDocumentStorageFaultInjector
{ Task CheckpointAsync(ManagedDocumentStorageOperation operation, string phase, CancellationToken ct); }
public sealed class NoManagedDocumentStorageFaultInjector : IManagedDocumentStorageFaultInjector
{ public Task CheckpointAsync(ManagedDocumentStorageOperation operation, string phase, CancellationToken ct) => Task.CompletedTask; }

public sealed class ManagedDocumentStorageCoordinator(AeroLinkDbContext db, EvidenceFileStore files,
    ManagedDocumentIntegrityService integrity, IManagedDocumentStorageFaultInjector faultInjector)
{
    // A pending request is deliberately fenced from reconciliation while it can still be
    // serving its caller. Requests that survive this conservative lease are treated as
    // interrupted; known failure paths explicitly surrender the lease below.
    public static readonly TimeSpan PendingOperationLease = TimeSpan.FromMinutes(30);
    public static string Manifest(IEnumerable<ManagedDocumentStagedObject> objects) => JsonSerializer.Serialize(objects);

    public async Task<ManagedDocumentStorageStart> BeginAsync(Guid projectId, Guid documentId, Guid revisionId,
        string operationType, string operationKey, string payloadHash, string actor, DateTimeOffset now, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(operationKey) || operationKey.Trim().Length > 100)
            throw new ManagedDocumentStorageConflictException("operation_key_required", "A one-use storage operation key of 100 characters or fewer is required.");
        var key = operationKey.Trim();
        var existing = await db.ManagedDocumentStorageOperations.SingleOrDefaultAsync(x => x.ProjectId == projectId
            && x.OperationType == operationType && x.OperationKey == key, ct);
        if (existing is not null)
        {
            if (!string.Equals(existing.PayloadHash, payloadHash, StringComparison.OrdinalIgnoreCase))
                throw new ManagedDocumentStorageConflictException("operation_key_reused", "That storage operation key was already used for different content or intent.");
            if (existing.State == ManagedDocumentStorageOperationState.Available)
                return new(existing, existing.ResultJson);
            if (existing.State == ManagedDocumentStorageOperationState.Pending
                && existing.UpdatedAt > now - PendingOperationLease)
                throw new ManagedDocumentStorageConflictException("storage_operation_pending", "The same storage operation is still pending. Retry after its lease expires or controlled reconciliation completes.");
            await ReconcileOperationAsync(existing, actor, now, ct);
            if (existing.State == ManagedDocumentStorageOperationState.Available) return new(existing, existing.ResultJson);
            if (existing.State == ManagedDocumentStorageOperationState.RepairRequired)
                throw new ManagedDocumentStorageConflictException("storage_repair_required", "The earlier storage operation requires controlled reconciliation before it can be retried.");
            if (existing.State == ManagedDocumentStorageOperationState.Pending)
                throw new ManagedDocumentStorageConflictException("storage_operation_pending", "The same storage operation is still pending. Retry after reconciliation completes.");
            throw new ManagedDocumentStorageConflictException("operation_key_reused", "That storage operation key belongs to a rolled-back attempt. Use a new operation key after reviewing its cleanup evidence.");
        }
        var operation = new ManagedDocumentStorageOperation(projectId, documentId, revisionId, operationType, key, payloadHash, actor, now);
        db.ManagedDocumentStorageOperations.Add(operation);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Clear();
            existing = await db.ManagedDocumentStorageOperations.SingleAsync(x => x.ProjectId == projectId
                && x.OperationType == operationType && x.OperationKey == key, ct);
            if (!string.Equals(existing.PayloadHash, payloadHash, StringComparison.OrdinalIgnoreCase))
                throw new ManagedDocumentStorageConflictException("operation_key_reused", "That storage operation key was concurrently used for different content or intent.");
            return existing.State == ManagedDocumentStorageOperationState.Available
                ? new(existing, existing.ResultJson)
                : throw new ManagedDocumentStorageConflictException("storage_operation_pending", "The same storage operation is already pending.");
        }
        try { await CheckpointAsync(operation, "pending-recorded", ct); }
        catch
        {
            // BeginAsync has not returned the durable operation to its caller yet, so it must
            // explicitly surrender and reconcile its own lease on this failure boundary.
            operation.RecordFailure("The storage request failed after recording its pending operation.", now);
            await db.SaveChangesAsync(CancellationToken.None);
            await ReconcileOperationAsync(operation, actor, DateTimeOffset.UtcNow, CancellationToken.None);
            throw;
        }
        return new(operation, null);
    }

    public async Task RecordPlanAsync(ManagedDocumentStorageOperation operation,
        IReadOnlyCollection<ManagedDocumentStagedObject> objects, string resultJson, DateTimeOffset now, CancellationToken ct)
    {
        operation.RecordManifest(Manifest(objects), resultJson, now); await db.SaveChangesAsync(ct);
        await CheckpointAsync(operation, "manifest-recorded", ct);
    }

    public async Task PromoteAsync(ManagedDocumentStorageOperation operation, IEnumerable<StagedEvidence> staged, CancellationToken ct)
    {
        await CheckpointAsync(operation, "before-promote", ct); var index = 0;
        foreach (var item in staged) { await files.PromoteAsync(item, ct); await CheckpointAsync(operation, $"object-promoted-{++index}", ct); }
    }

    public async Task CompleteAsync(ManagedDocumentStorageOperation operation, DateTimeOffset now, CancellationToken ct)
    {
        await CheckpointAsync(operation, "before-available-recorded", ct);
        operation.Complete(now);
        await db.SaveChangesAsync(ct);
        await CheckpointAsync(operation, "available-recorded", ct);
        await ResolveAlertAsync(operation, "system.storage", now, ct);
    }

    public Task CheckpointAsync(ManagedDocumentStorageOperation operation, string phase, CancellationToken ct) =>
        faultInjector.CheckpointAsync(operation, phase, ct);

    public async Task RollBackAsync(ManagedDocumentStorageOperation operation, string detail, string actor,
        DateTimeOffset now, CancellationToken ct)
    {
        var quarantined = new List<string>();
        foreach (var item in Objects(operation))
        {
            var key = files.Quarantine(item.StagingKey, operation.Id, "rollback-stage"); if (key is not null) quarantined.Add(key);
            key = files.Quarantine(item.StorageKey, operation.Id, "rollback-object"); if (key is not null) quarantined.Add(key);
        }
        foreach (var stage in OperationStages(operation.Id))
        { var key = files.Quarantine(stage, operation.Id, "rollback-unmanifested-stage"); if (key is not null) quarantined.Add(key); }
        operation.RollBack($"{detail} Quarantined {quarantined.Count} object(s): {string.Join(", ", quarantined)}", now);
        await AddEvidenceAsync(operation, "ManagedDocumentStorageRolledBack", actor, operation.Detail, now, false, ct);
    }

    public async Task<ManagedDocumentStorageReconciliation> ReconcileProjectAsync(Guid projectId, string actor,
        DateTimeOffset now, CancellationToken ct)
    {
        var openOperations = await db.ManagedDocumentStorageOperations.Where(x => x.ProjectId == projectId
            && (x.State == ManagedDocumentStorageOperationState.Pending || x.State == ManagedDocumentStorageOperationState.RepairRequired)).ToListAsync(ct);
        // Filter DateTimeOffset in memory so SQLite qualification and PostgreSQL production
        // use the same lease rule without provider-specific translation.
        var operations = openOperations.Where(x => x.State == ManagedDocumentStorageOperationState.RepairRequired
            || x.UpdatedAt <= now - PendingOperationLease).ToList();
        var completed = 0; var rolledBack = 0; var repair = 0; var quarantined = new List<string>();
        foreach (var operation in operations)
        {
            var before = operation.State; var keys = await ReconcileOperationAsync(operation, actor, now, ct); quarantined.AddRange(keys);
            if (operation.State == ManagedDocumentStorageOperationState.Available && before != operation.State) completed++;
            else if (operation.State == ManagedDocumentStorageOperationState.RolledBack && before != operation.State) rolledBack++;
            else if (operation.State == ManagedDocumentStorageOperationState.RepairRequired) repair++;
        }
        var allOpenOperations = await db.ManagedDocumentStorageOperations.AsNoTracking().Where(x => x.State == ManagedDocumentStorageOperationState.Pending
            || x.State == ManagedDocumentStorageOperationState.RepairRequired).ToListAsync(ct);
        var knownStages = allOpenOperations.SelectMany(Objects).Select(x => x.StagingKey).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var open in allOpenOperations) foreach (var stage in OperationStages(open.Id)) knownStages.Add(stage);
        foreach (var unknown in files.EnumerateStagedKeys().Where(x => !knownStages.Contains(x)))
        { var key = files.Quarantine(unknown, Guid.Empty, "unregistered-stage"); if (key is not null) quarantined.Add(key); }
        var integrityResult = await integrity.ScanProjectAsync(projectId, actor, ct); repair += integrityResult.Failed;
        var documentIds = await db.ManagedDocuments.AsNoTracking().Where(x => x.ProjectId == projectId).Select(x => x.Id).ToListAsync(ct);
        var revisions = await db.ManagedDocumentRevisions.AsNoTracking().Where(x => documentIds.Contains(x.DocumentId)).ToListAsync(ct);
        foreach (var revision in revisions)
        {
            string? detail = null;
            if (revision.State is ManagedDocumentState.Draft or ManagedDocumentState.Returned or ManagedDocumentState.InReview
                && revision.CurrentWorkingAttachmentId is null)
                detail = $"Managed document revision {revision.Id} is {revision.State} without a working attachment.";
            else if ((revision.ReleaseCandidateDocxAttachmentId is null) != (revision.ReleaseCandidatePdfAttachmentId is null))
                detail = $"Managed document revision {revision.Id} has a partial DOCX/PDF candidate set.";
            var signal = $"managed-document-revision-storage:{revision.Id:N}";
            var alerts = await db.OperationalAlerts.Where(x => x.ProjectId == projectId && x.Signal == signal
                && x.State != OperationalAlertState.Resolved).ToListAsync(ct);
            if (detail is null)
            { foreach (var alert in alerts) alert.Resolve(actor, now); if (alerts.Count > 0) await db.SaveChangesAsync(ct); continue; }
            repair++;
            if (alerts.Count == 0)
            {
                db.OperationalAlerts.Add(new(projectId, "critical", signal, detail,
                    "/docs/managed-documentation-center#failure-atomic-storage", actor, now));
                db.ManagedDocumentEvents.Add(new(revision.DocumentId, "ManagedDocumentStorageRepairRequired", actor, detail, now));
                await db.SaveChangesAsync(ct);
            }
        }
        var objectEvidence = operations.SelectMany(operation => Objects(operation).Select(item =>
            new ManagedDocumentStorageObjectEvidence(operation.Id, item.Slot, item.StorageKey,
                item.Size, item.Sha256, operation.State))).ToList();
        return new(operations.Count + integrityResult.Checked + revisions.Count, completed, rolledBack, repair,
            quarantined, operations.Select(x => x.Id).ToList(), objectEvidence);
    }

    public Task<IReadOnlyList<string>> ReconcileAbandonedOperationAsync(ManagedDocumentStorageOperation operation,
        string actor, DateTimeOffset now, CancellationToken ct) =>
        ReconcileOperationAsync(operation, actor, now, ct);

    private async Task<IReadOnlyList<string>> ReconcileOperationAsync(ManagedDocumentStorageOperation operation,
        string actor, DateTimeOffset now, CancellationToken ct)
    {
        var objects = Objects(operation); var quarantined = new List<string>();
        if (objects.Count == 0)
        {
            foreach (var stage in OperationStages(operation.Id))
            { var key = files.Quarantine(stage, operation.Id, "unmanifested-stage"); if (key is not null) quarantined.Add(key); }
            operation.RollBack($"The pending operation had no staged-object manifest and was deterministically rolled back. Quarantined {quarantined.Count} operation stage(s).", now);
            await AddEvidenceAsync(operation, "ManagedDocumentStorageRolledBack", actor, operation.Detail, now, false, ct); return quarantined;
        }
        var keys = objects.Select(x => x.StorageKey).ToList();
        var attachments = await db.ControlledAttachments.AsNoTracking().Where(x => keys.Contains(x.StorageKey)).ToListAsync(ct);
        if (attachments.Count == objects.Count)
        {
            try
            {
                foreach (var item in objects)
                {
                    await using var verified = await files.OpenVerifiedReadAsync(item.StorageKey, item.Size, item.Sha256, ct);
                    var stage = files.Quarantine(item.StagingKey, operation.Id, "duplicate-stage"); if (stage is not null) quarantined.Add(stage);
                }
                operation.Complete(now); await AddEvidenceAsync(operation, "ManagedDocumentStorageReconciled", actor,
                    $"Completed interrupted {operation.OperationType} after verifying {objects.Count} referenced object(s).", now, false, ct);
                await ResolveAlertAsync(operation, actor, now, ct); return quarantined;
            }
            catch (EvidenceIntegrityException ex)
            {
                operation.RequireRepair($"Referenced controlled object verification failed during reconciliation: {ex.Code}.", now);
                await AddEvidenceAsync(operation, "ManagedDocumentStorageRepairRequired", actor, operation.Detail, now, true, ct); return quarantined;
            }
        }
        if (attachments.Count != 0)
        {
            operation.RequireRepair($"The candidate/attachment set is partial: {attachments.Count} of {objects.Count} metadata rows exist.", now);
            await AddEvidenceAsync(operation, "ManagedDocumentStorageRepairRequired", actor, operation.Detail, now, true, ct); return quarantined;
        }
        foreach (var item in objects)
        {
            var key = files.Quarantine(item.StagingKey, operation.Id, "orphan-stage"); if (key is not null) quarantined.Add(key);
            key = files.Quarantine(item.StorageKey, operation.Id, "orphan-object"); if (key is not null) quarantined.Add(key);
        }
        operation.RollBack($"No committed attachment metadata referenced this interrupted operation. Quarantined {quarantined.Count} object(s).", now);
        await AddEvidenceAsync(operation, "ManagedDocumentStorageRolledBack", actor, operation.Detail, now, false, ct); return quarantined;
    }

    private async Task AddEvidenceAsync(ManagedDocumentStorageOperation operation, string eventType, string actor,
        string detail, DateTimeOffset now, bool alert, CancellationToken ct)
    {
        if (await db.ManagedDocuments.AnyAsync(x => x.Id == operation.DocumentId, ct))
            db.ManagedDocumentEvents.Add(new(operation.DocumentId, eventType, actor, detail, now));
        if (alert && !await db.OperationalAlerts.AnyAsync(x => x.ProjectId == operation.ProjectId
            && x.Signal == Signal(operation.Id) && x.State != OperationalAlertState.Resolved, ct))
            db.OperationalAlerts.Add(new(operation.ProjectId, "critical", Signal(operation.Id), detail,
                "/docs/managed-documentation-center#failure-atomic-storage", actor, now));
        await db.SaveChangesAsync(ct);
    }

    private async Task ResolveAlertAsync(ManagedDocumentStorageOperation operation, string actor, DateTimeOffset now, CancellationToken ct)
    {
        var alerts = await db.OperationalAlerts.Where(x => x.ProjectId == operation.ProjectId && x.Signal == Signal(operation.Id)
            && x.State != OperationalAlertState.Resolved).ToListAsync(ct);
        foreach (var alert in alerts) alert.Resolve(actor, now); if (alerts.Count > 0) await db.SaveChangesAsync(ct);
    }
    private static string Signal(Guid id) => $"managed-document-storage:{id:N}";
    private IReadOnlyList<string> OperationStages(Guid id) => files.EnumerateStagedKeys()
        .Where(x => x.StartsWith($"_staging/{id:N}/", StringComparison.OrdinalIgnoreCase)).ToList();
    private static IReadOnlyList<ManagedDocumentStagedObject> Objects(ManagedDocumentStorageOperation operation)
    { try { return JsonSerializer.Deserialize<List<ManagedDocumentStagedObject>>(operation.ObjectManifestJson) ?? []; } catch (JsonException) { return []; } }
}
