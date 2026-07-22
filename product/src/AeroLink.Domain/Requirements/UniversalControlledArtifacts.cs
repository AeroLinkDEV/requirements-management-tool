using AeroLink.Domain.Common;

namespace AeroLink.Domain.Requirements;

public enum DocumentTemplateState { Draft, InWork, Approved }
public enum ConfigurationChangeSetState { Draft, InWork, Conflict, Closed }

public sealed class DocumentTemplate
{
    private DocumentTemplate() { }
    public DocumentTemplate(Guid projectId, string templateNumber, string title, string body, string ownerId, DateTimeOffset now)
    {
        Id = Guid.NewGuid(); ProjectId = projectId; TemplateNumber = Required(templateNumber, "A document-template number is required.");
        Title = Required(title, "A document-template title is required."); Body = body?.Trim() ?? "";
        OwnerId = Required(ownerId, "A document-template owner is required."); State = DocumentTemplateState.Draft;
        CreatedAt = UpdatedAt = now; Version = 1;
    }
    public Guid Id { get; private set; }
    public Guid ProjectId { get; private set; }
    public string TemplateNumber { get; private set; } = "";
    public string Title { get; private set; } = "";
    public string Body { get; private set; } = "";
    public string OwnerId { get; private set; } = "";
    public DocumentTemplateState State { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public long Version { get; private set; }
    public void UpdateDraft(string title, string body, string ownerId, DateTimeOffset now)
    { Title = Required(title, "A document-template title is required."); Body = body?.Trim() ?? ""; OwnerId = Required(ownerId, "A document-template owner is required."); UpdatedAt = now; Version++; }
    private static string Required(string? value, string error) => string.IsNullOrWhiteSpace(value) ? throw new DomainException(error) : value.Trim();
}

public sealed class ConfigurationChangeSet
{
    private ConfigurationChangeSet() { }
    public ConfigurationChangeSet(Guid projectId, string changeSetNumber, string title, string description, string ownerId, DateTimeOffset now)
    {
        Id = Guid.NewGuid(); ProjectId = projectId; ChangeSetNumber = Required(changeSetNumber, "A configuration change-set number is required.");
        Title = Required(title, "A configuration change-set title is required."); Description = description?.Trim() ?? "";
        OwnerId = Required(ownerId, "A configuration change-set owner is required."); State = ConfigurationChangeSetState.Draft;
        CreatedAt = UpdatedAt = now; Version = 1;
    }
    public Guid Id { get; private set; }
    public Guid ProjectId { get; private set; }
    public string ChangeSetNumber { get; private set; } = "";
    public string Title { get; private set; } = "";
    public string Description { get; private set; } = "";
    public string OwnerId { get; private set; } = "";
    public ConfigurationChangeSetState State { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public long Version { get; private set; }
    public Guid? ComponentId { get; private set; }
    public Guid? TargetStreamId { get; private set; }
    public Guid? BaseRevisionId { get; private set; }
    public Guid? SourceRevisionId { get; private set; }
    public Guid? TargetRevisionId { get; private set; }
    public string? MergeResultJson { get; private set; }
    public string? ConflictJson { get; private set; }
    public string? ResolutionRationale { get; private set; }
    public DateTimeOffset? ClosedAt { get; private set; }
    public void UpdateDraft(string title, string description, string ownerId, DateTimeOffset now)
    { Title = Required(title, "A configuration change-set title is required."); Description = description?.Trim() ?? ""; OwnerId = Required(ownerId, "A configuration change-set owner is required."); UpdatedAt = now; Version++; }
    public void ConfigureMerge(Guid componentId,Guid targetStreamId,Guid baseRevisionId,Guid sourceRevisionId,Guid targetRevisionId,string? mergedJson,string? conflictJson,DateTimeOffset now)
    {if(State!=ConfigurationChangeSetState.Draft)throw new DomainException("Only a draft change set can be configured.");ComponentId=componentId;TargetStreamId=targetStreamId;BaseRevisionId=baseRevisionId;SourceRevisionId=sourceRevisionId;TargetRevisionId=targetRevisionId;MergeResultJson=mergedJson;ConflictJson=conflictJson;State=string.IsNullOrWhiteSpace(conflictJson)?ConfigurationChangeSetState.InWork:ConfigurationChangeSetState.Conflict;UpdatedAt=now;Version++;}
    public void Resolve(string resolutionJson,string rationale,DateTimeOffset now)
    {if(State!=ConfigurationChangeSetState.Conflict)throw new DomainException("Only a conflicting change set can be resolved.");MergeResultJson=Required(resolutionJson,"A deterministic resolution is required.");ResolutionRationale=Required(rationale,"A conflict-resolution rationale is required.");ConflictJson=null;State=ConfigurationChangeSetState.InWork;UpdatedAt=now;Version++;}
    public void Close(DateTimeOffset now){if(State!=ConfigurationChangeSetState.InWork||string.IsNullOrWhiteSpace(MergeResultJson))throw new DomainException("Only a resolved in-work change set can be closed.");State=ConfigurationChangeSetState.Closed;ClosedAt=UpdatedAt=now;Version++;}
    private static string Required(string? value, string error) => string.IsNullOrWhiteSpace(value) ? throw new DomainException(error) : value.Trim();
}
