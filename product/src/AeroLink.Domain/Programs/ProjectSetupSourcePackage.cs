using AeroLink.Domain.Common;

namespace AeroLink.Domain.Programs;

/// <summary>Which source is being staged while a new project still has no Project row.</summary>
public enum ProjectSetupSourceKind { AeroLinkBaseline, ExternalBaseline }

/// <summary>
/// Durable source staging owned by a setup draft. It deliberately has no Project foreign key: an upload must
/// survive interruption and resume before finalization creates the destination project.
/// </summary>
public sealed class ProjectSetupSourcePackage
{
    private ProjectSetupSourcePackage() { }

    public ProjectSetupSourcePackage(Guid draftId, ProjectSetupSourceKind kind, string fileName,
        string format, string sha256, long sizeBytes, byte[] payload, string capturedBy, DateTimeOffset now,
        Guid? sourceBaselineId = null)
    {
        if (draftId == Guid.Empty) throw new DomainException("A staged source requires its setup draft.");
        if (kind == ProjectSetupSourceKind.AeroLinkBaseline && sourceBaselineId is null)
            throw new DomainException("A native source requires its exact baseline.");
        if (kind == ProjectSetupSourceKind.ExternalBaseline && sourceBaselineId is not null)
            throw new DomainException("An external source cannot carry an AeroLink baseline identity.");
        payload ??= [];
        if (kind == ProjectSetupSourceKind.ExternalBaseline
            ? sizeBytes <= 0 || payload.Length != sizeBytes
            : sizeBytes != 0 || payload.Length != 0)
            throw new DomainException("A staged source has an invalid size.");
        Id = Guid.NewGuid(); DraftId = draftId; Kind = kind; SourceBaselineId = sourceBaselineId;
        FileName = Required(fileName, "source file name"); Format = Required(format, "source format");
        Sha256 = Hash(sha256); SizeBytes = sizeBytes; Payload = payload.ToArray(); CapturedBy = Required(capturedBy, "source actor");
        CapturedAt = now; UpdatedAt = now; Stage = ProjectSetupSourceStage.Captured;
        AnalysisJson = "{}"; MetadataJson = "{}"; MappingJson = "{}"; ReconciliationJson = "";
        SelectedCategoriesJson = "[]";
    }

    public Guid Id { get; private set; }
    public Guid DraftId { get; private set; }
    public ProjectSetupSourceKind Kind { get; private set; }
    public Guid? SourceBaselineId { get; private set; }
    public Guid? SourceProjectId { get; private set; }
    public string? SourceState { get; private set; }
    public string FileName { get; private set; } = "";
    public string Format { get; private set; } = "";
    public string Sha256 { get; private set; } = "";
    public long SizeBytes { get; private set; }
    public byte[] Payload { get; private set; } = [];
    public string SourceTool { get; private set; } = "";
    public string MetadataJson { get; private set; } = "{}";
    public string AnalysisJson { get; private set; } = "{}";
    public string SelectedCategoriesJson { get; private set; } = "[]";
    public string MappingJson { get; private set; } = "{}";
    public string ReconciliationJson { get; private set; } = "";
    public string? ManifestHash { get; private set; }
    public ProjectSetupSourceStage Stage { get; private set; }
    public string CapturedBy { get; private set; } = "";
    public DateTimeOffset CapturedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public long Version { get; private set; } = 1;
    public Guid? MaterializedProjectId { get; private set; }
    public Guid? MaterializedBaselineId { get; private set; }
    public string? AssertionHash { get; private set; }

    public void RecordNativeSnapshot(Guid sourceProjectId, string sourceState, string metadataJson,
        string analysisJson, DateTimeOffset now)
    {
        if (Kind != ProjectSetupSourceKind.AeroLinkBaseline) throw new DomainException("Only a native source can record a native snapshot.");
        if (sourceProjectId == Guid.Empty) throw new DomainException("A native source requires its source project.");
        SourceProjectId = sourceProjectId;
        SourceState = Required(sourceState, "source baseline state");
        MetadataJson = Json(metadataJson, "source metadata");
        AnalysisJson = Json(analysisJson, "source snapshot");
        Stage = ProjectSetupSourceStage.Analysed;
        Touch(now);
    }

    public void RecordAnalysis(string sourceTool, string metadataJson, string analysisJson, DateTimeOffset now)
    {
        if (Kind != ProjectSetupSourceKind.ExternalBaseline) throw new DomainException("Only an external source can record parser analysis.");
        SourceTool = sourceTool?.Trim() ?? "";
        MetadataJson = Json(metadataJson, "source metadata");
        AnalysisJson = Json(analysisJson, "source analysis");
        Stage = ProjectSetupSourceStage.Analysed;
        Touch(now);
    }

    public void RecordConfiguration(string selectedCategoriesJson, string mappingJson, DateTimeOffset now,
        string? metadataJson = null)
    {
        SelectedCategoriesJson = Json(selectedCategoriesJson, "selected categories");
        MappingJson = Json(mappingJson, "source mapping");
        if (metadataJson is not null) MetadataJson = Json(metadataJson, "source metadata");
        ReconciliationJson = ""; ManifestHash = null; AssertionHash = null;
        Stage = ProjectSetupSourceStage.Analysed;
        Touch(now);
    }

    public void RecordReconciliation(string reconciliationJson, string manifestHash, DateTimeOffset now)
    {
        if (Stage is not (ProjectSetupSourceStage.Analysed or ProjectSetupSourceStage.Reconciled))
            throw new DomainException("A source must be analysed before reconciliation.");
        ReconciliationJson = Json(reconciliationJson, "source reconciliation");
        ManifestHash = Hash(manifestHash);
        AssertionHash = null;
        Stage = ProjectSetupSourceStage.Reconciled;
        Touch(now);
    }

    public void MarkMaterialized(Guid projectId, Guid baselineId, string assertionHash, DateTimeOffset now)
    {
        if (Stage != ProjectSetupSourceStage.Reconciled) throw new DomainException("Only a reconciled source can be materialized.");
        if (projectId == Guid.Empty || baselineId == Guid.Empty) throw new DomainException("Materialization requires exact destination identities.");
        MaterializedProjectId = projectId; MaterializedBaselineId = baselineId; AssertionHash = Hash(assertionHash); Touch(now);
    }

    private void Touch(DateTimeOffset now) { UpdatedAt = now; Version++; }
    private static string Required(string? value, string name) => string.IsNullOrWhiteSpace(value)
        ? throw new DomainException($"A {name} is required.") : value.Trim();
    private static string Json(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new DomainException($"The {name} payload is required.");
        try { using var document = System.Text.Json.JsonDocument.Parse(value); return document.RootElement.GetRawText(); }
        catch (System.Text.Json.JsonException) { throw new DomainException($"The {name} payload is invalid JSON."); }
    }
    private static string Hash(string value)
    {
        var normalized = Required(value, "SHA-256").ToLowerInvariant();
        if (normalized.Length != 64 || !normalized.All(Uri.IsHexDigit)) throw new DomainException("A source hash must be a SHA-256 digest.");
        return normalized;
    }
}

public enum ProjectSetupSourceStage { Captured, Analysed, Reconciled }

/// <summary>Exact source fact retained beside a newly materialized controlled target record.</summary>
public sealed class ProjectInceptionSourceRecord
{
    private ProjectInceptionSourceRecord() { }

    public ProjectInceptionSourceRecord(Guid packageId, Guid projectId, Guid baselineId, string targetKind,
        Guid targetId, Guid? targetRevisionId, string sourceKey, string sourceModule, string sourceIdentifier,
        string sourceRevision, string sourceState, string sourceSnapshotJson, DateTimeOffset now)
    {
        if (packageId == Guid.Empty || projectId == Guid.Empty || baselineId == Guid.Empty || targetId == Guid.Empty)
            throw new DomainException("A source record requires exact package, destination and target identities.");
        Id = Guid.NewGuid(); PackageId = packageId; ProjectId = projectId; BaselineId = baselineId;
        TargetKind = Required(targetKind, "target kind"); TargetId = targetId; TargetRevisionId = targetRevisionId;
        SourceKey = Required(sourceKey, "source key"); SourceModule = sourceModule?.Trim() ?? "";
        SourceIdentifier = sourceIdentifier?.Trim() ?? ""; SourceRevision = sourceRevision?.Trim() ?? "";
        SourceState = sourceState?.Trim() ?? ""; SourceSnapshotJson = Json(sourceSnapshotJson); CreatedAt = now;
    }

    public Guid Id { get; private set; }
    public Guid PackageId { get; private set; }
    public Guid ProjectId { get; private set; }
    public Guid BaselineId { get; private set; }
    public string TargetKind { get; private set; } = "";
    public Guid TargetId { get; private set; }
    public Guid? TargetRevisionId { get; private set; }
    public string SourceKey { get; private set; } = "";
    public string SourceModule { get; private set; } = "";
    public string SourceIdentifier { get; private set; } = "";
    public string SourceRevision { get; private set; } = "";
    public string SourceState { get; private set; } = "";
    public string SourceSnapshotJson { get; private set; } = "{}";
    public DateTimeOffset CreatedAt { get; private set; }

    private static string Required(string? value, string name) => string.IsNullOrWhiteSpace(value)
        ? throw new DomainException($"A {name} is required.") : value.Trim();
    private static string Json(string value)
    {
        try { using var document = System.Text.Json.JsonDocument.Parse(value); return document.RootElement.GetRawText(); }
        catch (System.Text.Json.JsonException) { throw new DomainException("A source snapshot must be valid JSON."); }
    }
}
