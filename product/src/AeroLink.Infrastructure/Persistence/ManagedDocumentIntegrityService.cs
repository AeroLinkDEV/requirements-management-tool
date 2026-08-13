using AeroLink.Domain.Documents;
using AeroLink.Domain.Identity;
using AeroLink.Domain.Requirements;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace AeroLink.Infrastructure.Persistence;

public sealed class ManagedDocumentIntegrityFailure(
    Guid attachmentId,
    string code,
    string message,
    Exception? innerException = null) : Exception(message, innerException)
{
    public Guid AttachmentId { get; } = attachmentId;
    public string Code { get; } = code;
}

public sealed record ManagedDocumentIntegrityScanResult(int Checked, int Healthy, int Failed, IReadOnlyList<Guid> FailedAttachmentIds);

public sealed class ManagedDocumentIntegrityService(AeroLinkDbContext db, EvidenceFileStore store, IConfiguration? configuration = null)
{
    private readonly bool readOnlyRestoreValidation = configuration?.GetValue<bool>("RestoreValidation:ReadOnly") == true;

    public async Task<FileStream> OpenVerifiedAsync(ControlledAttachment attachment, string actor, CancellationToken ct)
    {
        var signal = Signal(attachment.Id);
        if (await db.OperationalAlerts.AnyAsync(x => x.ProjectId == attachment.ProjectId && x.Signal == signal && x.State != OperationalAlertState.Resolved, ct))
            throw new ManagedDocumentIntegrityFailure(attachment.Id, "unresolved_incident",
                "Controlled document evidence remains blocked pending an authorized exact-hash recovery.");
        try
        {
            var stream = await store.OpenVerifiedReadAsync(attachment.StorageKey, attachment.Size, attachment.Sha256, ct);
            if (!readOnlyRestoreValidation)
                await db.ControlledAttachments.Where(x => x.Id == attachment.Id)
                    .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.IntegrityVerifiedAt, DateTimeOffset.UtcNow), ct);
            return stream;
        }
        catch (EvidenceIntegrityException ex)
        {
            if (!readOnlyRestoreValidation) await RecordFailureAsync(attachment, actor, ex, ct);
            throw new ManagedDocumentIntegrityFailure(attachment.Id, ex.Code,
                "Controlled document evidence failed integrity verification. No bytes were returned or used.", ex);
        }
    }

    public async Task<string?> RestoreAsync(ControlledAttachment attachment, Stream source, string actor, string reason, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(reason) || reason.Trim().Length > 1000)
            throw new InvalidOperationException("Provide a controlled recovery reason of 1000 characters or fewer.");
        var signal = Signal(attachment.Id);
        var alert = await db.OperationalAlerts.SingleOrDefaultAsync(x => x.ProjectId == attachment.ProjectId
            && x.Signal == signal && x.State != OperationalAlertState.Resolved, ct)
            ?? throw new InvalidOperationException("This attachment has no open integrity incident to recover.");
        RestoredEvidence restored;
        try { restored = await store.RestoreExactAsync(source, attachment.StorageKey, attachment.Size, attachment.Sha256, ct); }
        catch (EvidenceIntegrityException ex)
        {
            throw new ManagedDocumentIntegrityFailure(attachment.Id, ex.Code, ex.Message, ex);
        }
        await using (var verified = await store.OpenVerifiedReadAsync(attachment.StorageKey, attachment.Size, attachment.Sha256, ct)) { }
        var now = DateTimeOffset.UtcNow;
        var tracked = await db.ControlledAttachments.SingleAsync(x => x.Id == attachment.Id, ct);
        tracked.RecordIntegrityVerification(now);
        alert.Resolve(actor, now);
        var detail = $"Recovered attachment {attachment.Id} to its immutable SHA-256 {attachment.Sha256}. Reason: {reason.Trim()}";
        if (restored.QuarantineKey is not null) detail += $" Prior bytes were quarantined as {restored.QuarantineKey}.";
        db.ManagedDocumentEvents.Add(new ManagedDocumentEvent(attachment.ArtifactId, "DocumentIntegrityRecovered", actor, detail, now));
        db.SecurityAuditEvents.Add(new SecurityAuditEvent("ManagedDocumentIntegrityRecovered", actor,
            attachment.Id.ToString(), "Success", detail, "local-storage", now));
        await db.SaveChangesAsync(ct);
        return restored.QuarantineKey;
    }

    public async Task VerifyAsync(ControlledAttachment attachment, string actor, CancellationToken ct)
    {
        await using var stream = await OpenVerifiedAsync(attachment, actor, ct);
    }

    public async Task<ManagedDocumentIntegrityScanResult> ScanProjectAsync(Guid projectId, string actor, CancellationToken ct)
    {
        var attachments = await db.ControlledAttachments.AsNoTracking()
            .Where(x => x.ProjectId == projectId && x.ArtifactType == "ManagedDocument")
            .OrderBy(x => x.Id).ToListAsync(ct);
        var failed = new List<Guid>();
        foreach (var attachment in attachments)
        {
            try { await VerifyAsync(attachment, actor, ct); }
            catch (ManagedDocumentIntegrityFailure) { failed.Add(attachment.Id); }
        }
        return new(attachments.Count, attachments.Count - failed.Count, failed.Count, failed);
    }

    private async Task RecordFailureAsync(ControlledAttachment attachment, string actor, EvidenceIntegrityException failure, CancellationToken ct)
    {
        var signal = Signal(attachment.Id);
        var alreadyOpen = await db.OperationalAlerts.AnyAsync(x => x.ProjectId == attachment.ProjectId
            && x.Signal == signal && x.State != OperationalAlertState.Resolved, ct);
        if (alreadyOpen) return;

        var now = DateTimeOffset.UtcNow;
        var detail = $"Attachment {attachment.Id} ({attachment.OriginalFileName}) failed {failure.Code}; expected size {attachment.Size} and SHA-256 {attachment.Sha256}. Historical metadata was not changed.";
        db.OperationalAlerts.Add(new OperationalAlert(attachment.ProjectId, "Critical", signal, detail,
            "/docs/MANAGED_DOCUMENTATION_CENTER.md#controlled-file-integrity", actor, now));
        db.ManagedDocumentEvents.Add(new ManagedDocumentEvent(attachment.ArtifactId, "DocumentIntegrityBlocked", actor,
            $"Blocked controlled use of attachment {attachment.Id} after {failure.Code}. Expected SHA-256 remains {attachment.Sha256}.", now));
        db.SecurityAuditEvents.Add(new SecurityAuditEvent("ManagedDocumentIntegrityFailure", actor,
            attachment.Id.ToString(), "Blocked", detail, "local-storage", now));
        await db.SaveChangesAsync(ct);
    }

    private static string Signal(Guid attachmentId) => $"managed-document-integrity:{attachmentId:N}";
}
