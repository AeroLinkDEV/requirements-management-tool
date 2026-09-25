using AeroLink.Domain.Common;

namespace AeroLink.Domain.Requirements;

/// <summary>
/// The ledger of one Problem Report import (#1114): what file, under which mapping, by whom, and what it
/// created and skipped. The importer's electronic signature is recorded over <see cref="PreviewHash"/>, which
/// binds the exact source bytes to the exact mapping that was reviewed.
/// </summary>
public sealed class ProblemReportImportBatch
{
    private ProblemReportImportBatch() { }

    public ProblemReportImportBatch(Guid projectId, string sourceSystem, string fileName, string sourceHash,
        string mappingJson, string previewHash, int created, int skipped, string importedBy, DateTimeOffset importedAt)
    {
        if (projectId == Guid.Empty) throw new DomainException("An import belongs to a project.");
        Id = Guid.NewGuid(); ProjectId = projectId;
        SourceSystem = Required(sourceSystem); FileName = Required(fileName); SourceHash = Required(sourceHash);
        MappingJson = Required(mappingJson); PreviewHash = Required(previewHash);
        Created = created; Skipped = skipped; ImportedBy = Required(importedBy); ImportedAt = importedAt;
    }

    public Guid Id { get; private set; }
    public Guid ProjectId { get; private set; }
    public string SourceSystem { get; private set; } = "";
    public string FileName { get; private set; } = "";
    public string SourceHash { get; private set; } = "";
    public string MappingJson { get; private set; } = "";
    public string PreviewHash { get; private set; } = "";
    public int Created { get; private set; }
    public int Skipped { get; private set; }
    public string ImportedBy { get; private set; } = "";
    public DateTimeOffset ImportedAt { get; private set; }

    private static string Required(string? value) =>
        string.IsNullOrWhiteSpace(value) ? throw new DomainException("Problem Report import evidence is required.") : value.Trim();
}
