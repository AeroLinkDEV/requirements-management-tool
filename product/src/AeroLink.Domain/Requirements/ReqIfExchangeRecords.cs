using AeroLink.Domain.Common;

namespace AeroLink.Domain.Requirements;

public enum ReqIfExchangeDirection { Import, Export }
public enum ReqIfExchangeState { Preview, Ready, Committed, Rejected }

/// <summary>Durable audit record for one governed ReqIF package operation.</summary>
public sealed class ReqIfExchangeJob
{
    private ReqIfExchangeJob() { }
    public ReqIfExchangeJob(Guid projectId, ReqIfExchangeDirection direction, string fileName, string sha256,
        string storageKey, string manifestJson, int requirementCount, int hierarchyCount, int relationCount,
        int attachmentCount, int warningCount, int errorCount, string actor, DateTimeOffset now)
    {
        if (projectId == Guid.Empty) throw new DomainException("A ReqIF exchange needs a Project.");
        Id = Guid.NewGuid(); ProjectId = projectId; Direction = direction; FileName = Required(fileName);
        Sha256 = Required(sha256); StorageKey = Required(storageKey); ManifestJson = Required(manifestJson);
        RequirementCount = requirementCount; HierarchyCount = hierarchyCount; RelationCount = relationCount;
        AttachmentCount = attachmentCount; WarningCount = warningCount; ErrorCount = errorCount;
        State = errorCount == 0 ? ReqIfExchangeState.Ready : ReqIfExchangeState.Preview;
        CreatedBy = Required(actor); CreatedAt = now;
    }
    public Guid Id { get; private set; }
    public Guid ProjectId { get; private set; }
    public ReqIfExchangeDirection Direction { get; private set; }
    public string FileName { get; private set; } = "";
    public string Sha256 { get; private set; } = "";
    public string StorageKey { get; private set; } = "";
    public string ManifestJson { get; private set; } = "{}";
    public int RequirementCount { get; private set; }
    public int HierarchyCount { get; private set; }
    public int RelationCount { get; private set; }
    public int AttachmentCount { get; private set; }
    public int WarningCount { get; private set; }
    public int ErrorCount { get; private set; }
    public ReqIfExchangeState State { get; private set; }
    public string CreatedBy { get; private set; } = "";
    public DateTimeOffset CreatedAt { get; private set; }
    public Guid? CreatedScrId { get; private set; }
    public DateTimeOffset? CompletedAt { get; private set; }
    public void Commit(Guid scrId, DateTimeOffset now)
    {
        if (Direction != ReqIfExchangeDirection.Import || State != ReqIfExchangeState.Ready)
            throw new DomainException("Only a validated ReqIF import can be committed.");
        CreatedScrId = scrId; State = ReqIfExchangeState.Committed; CompletedAt = now;
    }
    public void Reject(DateTimeOffset now) { if (State == ReqIfExchangeState.Committed) throw new DomainException("A committed exchange is immutable."); State = ReqIfExchangeState.Rejected; CompletedAt = now; }
    private static string Required(string value) => string.IsNullOrWhiteSpace(value) ? throw new DomainException("A required ReqIF exchange value is missing.") : value.Trim();
}
