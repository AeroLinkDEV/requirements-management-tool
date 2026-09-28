using AeroLink.Domain.Common;

namespace AeroLink.Domain.Requirements;

/// <summary>Immutable acceptance of an Integrity source package; source facts confer no native authority.</summary>
public sealed class IntegrityImportBatch
{
    private IntegrityImportBatch() { }
    public IntegrityImportBatch(Guid projectId, Guid operationId, Guid actorId, Guid sourceInstanceId,
        string manifestHash, string requestHash, string previewHash, string mappingJson, string reconciliationJson,
        Guid packageAttachmentId, string actor, DateTimeOffset now)
    {
        if (new[] { projectId, operationId, actorId, sourceInstanceId, packageAttachmentId }.Contains(Guid.Empty))
            throw new DomainException("An Integrity import requires complete operation and source identities.");
        Id = Guid.NewGuid(); ProjectId = projectId; OperationId = operationId; ActorId = actorId;
        SourceInstanceId = sourceInstanceId; ManifestHash = manifestHash; RequestHash = requestHash;
        PreviewHash = previewHash; MappingJson = mappingJson; ReconciliationJson = reconciliationJson;
        PackageAttachmentId = packageAttachmentId; ImportedBy = actor; ImportedAt = now;
    }
    public Guid Id { get; private set; }
    public Guid ProjectId { get; private set; }
    public Guid OperationId { get; private set; }
    public Guid ActorId { get; private set; }
    public Guid SourceInstanceId { get; private set; }
    public string ManifestHash { get; private set; } = "";
    public string RequestHash { get; private set; } = "";
    public string PreviewHash { get; private set; } = "";
    public string MappingJson { get; private set; } = "";
    public string ReconciliationJson { get; private set; } = "";
    public string ReceiptJson { get; private set; } = "";
    public Guid PackageAttachmentId { get; private set; }
    public string ImportedBy { get; private set; } = "";
    public DateTimeOffset ImportedAt { get; private set; }
    public void RecordReceipt(string receipt)
    {
        if (ReceiptJson.Length != 0 || string.IsNullOrWhiteSpace(receipt))
            throw new DomainException("An import receipt is recorded exactly once.");
        ReceiptJson = receipt;
    }
}

/// <summary>The original item and its preserved content remain in the immutable package.</summary>
public sealed class IntegrityReportSource
{
    private IntegrityReportSource() { }
    public IntegrityReportSource(Guid projectId, Guid batchId, Guid reportId, Guid sourceInstanceId,
        string sourceKey, string itemPath, string dateJson)
    {
        Id = Guid.NewGuid(); ProjectId = projectId; BatchId = batchId; ReportId = reportId;
        SourceInstanceId = sourceInstanceId; SourceKey = sourceKey; ItemPath = itemPath; DateJson = dateJson;
    }
    public Guid Id { get; private set; }
    public Guid ProjectId { get; private set; }
    public Guid BatchId { get; private set; }
    public Guid ReportId { get; private set; }
    public Guid SourceInstanceId { get; private set; }
    public string SourceKey { get; private set; } = "";
    public string ItemPath { get; private set; } = "";
    public string DateJson { get; private set; } = "";
}
