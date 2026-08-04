using AeroLink.Domain.Common;

namespace AeroLink.Domain.Imports;

/// <summary>Which controlled records an import claims to carry. An import declares this before it runs.</summary>
[Flags]
public enum ImportedArtifactKinds
{
    None = 0,
    Requirements = 1,
    TestProcedures = 2
}

public enum BaselineImportState { Draft, Analysed, Mapped, Reconciled, Accepted, Abandoned }

/// <summary>
/// A program that already exists in another requirements tool, brought in as it stood at one moment.
///
/// This is not a change request and never becomes one. Nobody here approved these requirements, so routing
/// them through review and approval would produce a real signature attesting to a fiction. What this record
/// holds instead is provenance: where the extract came from, who took it, what it hashed to, and what the
/// mapping did with it — so the resulting baseline can be told apart from one this product built, forever.
///
/// The prior decisions — review, approval, verification — are credited to the source's own release. This
/// product never claims to have made them. See DEC-093.
/// </summary>
public sealed class BaselineImport
{
    private BaselineImport() { }

    public BaselineImport(Guid projectId, string sourceSystem, string sourceSystemVersion,
        string sourceBaselineName, DateTimeOffset sourceBaselineDate, string extractFileName,
        string extractSha256, long extractSizeBytes, ImportedArtifactKinds carries,
        string extractedBy, DateTimeOffset extractedAt, string startedBy, DateTimeOffset now)
    {
        if (projectId == Guid.Empty) throw new DomainException("A baseline import requires its Project.");
        if (carries == ImportedArtifactKinds.None)
            throw new DomainException("A baseline import must declare which kinds of record it carries.");
        if (extractSizeBytes <= 0) throw new DomainException("The extract file is empty.");
        Id = Guid.NewGuid();
        ProjectId = projectId;
        SourceSystem = Required(sourceSystem, "source system");
        SourceSystemVersion = Required(sourceSystemVersion, "source system version");
        SourceBaselineName = Required(sourceBaselineName, "source baseline name");
        SourceBaselineDate = sourceBaselineDate;
        ExtractFileName = Required(extractFileName, "extract file name");
        ExtractSha256 = Sha256(extractSha256);
        ExtractSizeBytes = extractSizeBytes;
        Carries = carries;
        ExtractedBy = Required(extractedBy, "person who took the extract");
        ExtractedAt = extractedAt;
        StartedBy = Required(startedBy, "person starting the import");
        StartedAt = now;
        State = BaselineImportState.Draft;
    }

    public Guid Id { get; private set; }
    public Guid ProjectId { get; private set; }
    public BaselineImportState State { get; private set; }

    public string SourceSystem { get; private set; } = "";
    public string SourceSystemVersion { get; private set; } = "";
    public string SourceBaselineName { get; private set; } = "";
    public DateTimeOffset SourceBaselineDate { get; private set; }

    public string ExtractFileName { get; private set; } = "";
    /// <summary>What makes the claim checkable years later, rather than a story about a file nobody kept.</summary>
    public string ExtractSha256 { get; private set; } = "";
    public long ExtractSizeBytes { get; private set; }
    public ImportedArtifactKinds Carries { get; private set; }

    public string ExtractedBy { get; private set; } = "";
    public DateTimeOffset ExtractedAt { get; private set; }
    public string StartedBy { get; private set; } = "";
    public DateTimeOffset StartedAt { get; private set; }

    /// <summary>The mapping the operator settled at the Map gate, kept so a re-import can reuse it verbatim.</summary>
    public string MappingJson { get; private set; } = "";
    /// <summary>Counts in against counts out, and every source object not imported with the reason why.</summary>
    public string ReconciliationJson { get; private set; } = "";

    public string? AcceptedBy { get; private set; }
    public DateTimeOffset? AcceptedAt { get; private set; }
    /// <summary>The released baseline this import produced. Set once, at acceptance.</summary>
    public Guid? ReleaseId { get; private set; }
    public long Version { get; private set; } = 1;

    public void RecordAnalysis(DateTimeOffset now)
    {
        Ensure(BaselineImportState.Draft, "analysed");
        State = BaselineImportState.Analysed;
        Touch(now);
    }

    public void RecordMapping(string mappingJson, DateTimeOffset now)
    {
        if (State is not (BaselineImportState.Analysed or BaselineImportState.Mapped or BaselineImportState.Reconciled))
            throw new DomainException("An import must be analysed before it can be mapped.");
        MappingJson = Required(mappingJson, "mapping");
        // Re-mapping invalidates any reconciliation already computed: the counts described the old mapping.
        ReconciliationJson = "";
        State = BaselineImportState.Mapped;
        Touch(now);
    }

    public void RecordReconciliation(string reconciliationJson, DateTimeOffset now)
    {
        if (State is not (BaselineImportState.Mapped or BaselineImportState.Reconciled))
            throw new DomainException("An import must be mapped before it can be reconciled.");
        ReconciliationJson = Required(reconciliationJson, "reconciliation");
        State = BaselineImportState.Reconciled;
        Touch(now);
    }

    /// <summary>
    /// A named person takes responsibility for the import, and the baseline exists from here.
    ///
    /// The signature asserts three narrow things: that the extract is a true copy of the named source
    /// baseline, that the mapping is correct for this program, and that any recorded gaps are accepted. It
    /// asserts nothing about whether these requirements were reviewed or approved — they were not, here.
    /// </summary>
    public void Accept(string actorId, Guid releaseId, DateTimeOffset now)
    {
        if (State != BaselineImportState.Reconciled)
            throw new DomainException("An import must be reconciled before it can be accepted.");
        if (releaseId == Guid.Empty) throw new DomainException("Accepting an import requires the build it creates.");
        AcceptedBy = Required(actorId, "person accepting the import");
        AcceptedAt = now;
        ReleaseId = releaseId;
        State = BaselineImportState.Accepted;
        Touch(now);
    }

    public void Abandon(DateTimeOffset now)
    {
        if (State == BaselineImportState.Accepted)
            throw new DomainException("An accepted import is immutable. Its baseline exists.");
        State = BaselineImportState.Abandoned;
        Touch(now);
    }

    private void Ensure(BaselineImportState expected, string verb)
    {
        if (State != expected) throw new DomainException($"An import in {State} cannot be {verb}.");
    }

    private void Touch(DateTimeOffset now) { UpdatedAt = now; Version++; }
    public DateTimeOffset UpdatedAt { get; private set; }

    private static string Required(string value, string name) =>
        string.IsNullOrWhiteSpace(value) ? throw new DomainException($"A {name} is required.") : value.Trim();

    private static string Sha256(string value)
    {
        var normalized = Required(value, "extract SHA-256").ToLowerInvariant();
        if (normalized.Length != 64 || !normalized.All(Uri.IsHexDigit))
            throw new DomainException("The extract hash must be a SHA-256 digest.");
        return normalized;
    }
}
