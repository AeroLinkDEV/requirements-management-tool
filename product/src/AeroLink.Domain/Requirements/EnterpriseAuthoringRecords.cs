using AeroLink.Domain.Common;

namespace AeroLink.Domain.Requirements;

public enum SchemaFieldType { ShortText, LongText, RichText, Integer, Decimal, Boolean, Date, Enumeration, User, ArtifactReference }
public enum SpecificationNodeType { Section, Requirement }
public enum CollaborationState { Open, Resolved, Dispositioned }
public enum EnterpriseJobState { Preview, Running, Completed, Failed, Cancelled }

public sealed class ArtifactSchemaDefinition
{
    private ArtifactSchemaDefinition() { }
    public ArtifactSchemaDefinition(Guid projectId, string key, string name, string appliesTo, string description, string createdBy, DateTimeOffset now)
    { Id=Guid.NewGuid(); ProjectId=projectId; Key=Required(key,nameof(key)).ToUpperInvariant(); Name=Required(name,nameof(name)); AppliesTo=Required(appliesTo,nameof(appliesTo)); Description=description.Trim(); Version=1; IsActive=true; CreatedBy=createdBy; CreatedAt=now; }
    public Guid Id { get; private set; }
    public Guid ProjectId { get; private set; }
    public string Key { get; private set; } = "";
    public string Name { get; private set; } = "";
    public string AppliesTo { get; private set; } = "";
    public string Description { get; private set; } = "";
    public int Version { get; private set; }
    public bool IsActive { get; private set; }
    public string CreatedBy { get; private set; } = "";
    public DateTimeOffset CreatedAt { get; private set; }
    private readonly List<ArtifactFieldDefinition> _fields=[];
    public IReadOnlyCollection<ArtifactFieldDefinition> Fields => _fields;
    public void AddField(string key,string label,SchemaFieldType type,bool required,int order,string optionsJson,string actor,DateTimeOffset now)
    { if(_fields.Any(x=>x.Key.Equals(key,StringComparison.OrdinalIgnoreCase)))throw new DomainException($"Field '{key}' already exists in this schema."); _fields.Add(new(Id,key,label,type,required,order,optionsJson,actor,now)); Version++; }
    private static string Required(string value,string name)=>string.IsNullOrWhiteSpace(value)?throw new DomainException($"{name} is required."):value.Trim();
}

public sealed class ArtifactFieldDefinition
{
    private ArtifactFieldDefinition() { }
    internal ArtifactFieldDefinition(Guid schemaId,string key,string label,SchemaFieldType type,bool required,int order,string optionsJson,string actor,DateTimeOffset now)
    { Id=Guid.NewGuid();SchemaId=schemaId;Key=key.Trim().ToLowerInvariant();Label=label.Trim();Type=type;IsRequired=required;SortOrder=order;OptionsJson=string.IsNullOrWhiteSpace(optionsJson)?"[]":optionsJson;CreatedBy=actor;CreatedAt=now; }
    public Guid Id { get; private set; }
    public Guid SchemaId { get; private set; }
    public string Key { get; private set; }="";
    public string Label { get; private set; }="";
    public SchemaFieldType Type { get; private set; }
    public bool IsRequired { get; private set; }
    public int SortOrder { get; private set; }
    public string OptionsJson { get; private set; }="[]";
    public string CreatedBy { get; private set; }="";
    public DateTimeOffset CreatedAt { get; private set; }
}

public sealed class RequirementSpecification
{
    private RequirementSpecification() { }
    public RequirementSpecification(Guid projectId,string documentNumber,string title,string level,string description,string actor,DateTimeOffset now)
    { Id=Guid.NewGuid();ProjectId=projectId;DocumentNumber=ArtifactNumber.ValidateBase(documentNumber);Title=title.Trim();Level=level;Description=description.Trim();CreatedBy=actor;CreatedAt=now; }
    public Guid Id { get; private set; }
    public Guid ProjectId { get; private set; }
    public string DocumentNumber { get; private set; }="";
    public string Title { get; private set; }="";
    public string Level { get; private set; }="";
    public string Description { get; private set; }="";
    public string CreatedBy { get; private set; }="";
    public DateTimeOffset CreatedAt { get; private set; }
}

public sealed class SpecificationNode
{
    private SpecificationNode() { }
    public SpecificationNode(Guid specificationId,Guid? parentId,int position,SpecificationNodeType type,string heading,Guid? requirementArtifactId,string actor,DateTimeOffset now)
    { if(type==SpecificationNodeType.Requirement&&requirementArtifactId is null)throw new DomainException("Requirement nodes need an artifact.");Id=Guid.NewGuid();SpecificationId=specificationId;ParentId=parentId;Position=position;Type=type;Heading=heading.Trim();RequirementArtifactId=requirementArtifactId;CreatedBy=actor;CreatedAt=now; }
    public Guid Id { get; private set; }
    public Guid SpecificationId { get; private set; }
    public Guid? ParentId { get; private set; }
    public int Position { get; private set; }
    public SpecificationNodeType Type { get; private set; }
    public string Heading { get; private set; }="";
    public Guid? RequirementArtifactId { get; private set; }
    public string CreatedBy { get; private set; }="";
    public DateTimeOffset CreatedAt { get; private set; }
}

public sealed class RequirementRevisionProfile
{
    private RequirementRevisionProfile() { }
    public RequirementRevisionProfile(Guid revisionId,Guid schemaId,string richText,string attributesJson,string tagsJson,string actor,DateTimeOffset now)
    { Id=Guid.NewGuid();RevisionId=revisionId;SchemaId=schemaId;RichText=richText;AttributesJson=attributesJson;TagsJson=tagsJson;UpdatedBy=actor;UpdatedAt=now; }
    public Guid Id { get; private set; }
    public Guid RevisionId { get; private set; }
    public Guid SchemaId { get; private set; }
    public string RichText { get; private set; }="";
    public string AttributesJson { get; private set; }="{}";
    public string TagsJson { get; private set; }="[]";
    public string UpdatedBy { get; private set; }="";
    public DateTimeOffset UpdatedAt { get; private set; }
    public void AddTag(string tag,string actor,DateTimeOffset now)
    { var tags=System.Text.Json.JsonSerializer.Deserialize<List<string>>(TagsJson)??[];if(!tags.Contains(tag,StringComparer.OrdinalIgnoreCase))tags.Add(tag.Trim());TagsJson=System.Text.Json.JsonSerializer.Serialize(tags.OrderBy(x=>x,StringComparer.OrdinalIgnoreCase));UpdatedBy=actor;UpdatedAt=now; }
}

public sealed class ArtifactComment
{
    private ArtifactComment() { }
    public ArtifactComment(Guid projectId,string artifactType,Guid artifactId,Guid? revisionId,Guid? parentCommentId,string body,string mentionsJson,string actor,DateTimeOffset now)
    { if(string.IsNullOrWhiteSpace(body))throw new DomainException("Comment text is required.");Id=Guid.NewGuid();ProjectId=projectId;ArtifactType=artifactType;ArtifactId=artifactId;RevisionId=revisionId;ParentCommentId=parentCommentId;Body=body.Trim();MentionsJson=mentionsJson;State=CollaborationState.Open;CreatedBy=actor;CreatedAt=now; }
    public Guid Id { get; private set; }
    public Guid ProjectId { get; private set; }
    public string ArtifactType { get; private set; }="";
    public Guid ArtifactId { get; private set; }
    public Guid? RevisionId { get; private set; }
    public Guid? ParentCommentId { get; private set; }
    public string Body { get; private set; }="";
    public string MentionsJson { get; private set; }="[]";
    public CollaborationState State { get; private set; }
    public string CreatedBy { get; private set; }="";
    public DateTimeOffset CreatedAt { get; private set; }
    public string? ResolvedBy { get; private set; }
    public DateTimeOffset? ResolvedAt { get; private set; }
    public string? Disposition { get; private set; }
    public void Resolve(string actor,string disposition,DateTimeOffset now){if(State!=CollaborationState.Open)throw new DomainException("Only open comments can be resolved.");State=string.IsNullOrWhiteSpace(disposition)?CollaborationState.Resolved:CollaborationState.Dispositioned;Disposition=disposition.Trim();ResolvedBy=actor;ResolvedAt=now;}
}

public sealed class SavedRequirementView
{
    private SavedRequirementView() { }
    public SavedRequirementView(Guid projectId,Guid ownerId,string name,string queryJson,string columnsJson,bool shared,DateTimeOffset now)
    { Id=Guid.NewGuid();ProjectId=projectId;OwnerId=ownerId;Name=name.Trim();QueryJson=queryJson;ColumnsJson=columnsJson;IsShared=shared;CreatedAt=now; }
    public Guid Id { get; private set; }
    public Guid ProjectId { get; private set; }
    public Guid OwnerId { get; private set; }
    public string Name { get; private set; }="";
    public string QueryJson { get; private set; }="{}";
    public string ColumnsJson { get; private set; }="[]";
    public bool IsShared { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
}

public sealed class EnterpriseOperationJob
{
    private EnterpriseOperationJob() { }
    public EnterpriseOperationJob(Guid projectId,string jobType,string requestJson,int itemCount,string actor,DateTimeOffset now,string? idempotencyKey=null)
    { Id=Guid.NewGuid();ProjectId=projectId;JobType=jobType;RequestJson=requestJson;State=EnterpriseJobState.Preview;ItemCount=itemCount;CreatedBy=actor;CreatedAt=now;UpdatedAt=now;IdempotencyKey=string.IsNullOrWhiteSpace(idempotencyKey)?Guid.NewGuid().ToString("N"):idempotencyKey.Trim(); }
    public Guid Id { get; private set; }
    public Guid ProjectId { get; private set; }
    public string JobType { get; private set; }="";
    public string RequestJson { get; private set; }="{}";
    public EnterpriseJobState State { get; private set; }
    public int ItemCount { get; private set; }
    public int SucceededCount { get; private set; }
    public int FailedCount { get; private set; }
    public string ResultJson { get; private set; }="{}";
    public string CreatedBy { get; private set; }="";
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public DateTimeOffset? CompletedAt { get; private set; }
    public DateTimeOffset? StartedAt { get; private set; }
    public string IdempotencyKey { get; private set; }="";
    public int Attempt { get; private set; }
    public int ProgressPercent { get; private set; }
    public string? LastError { get; private set; }
    public void Start(DateTimeOffset now){if(State is not (EnterpriseJobState.Preview or EnterpriseJobState.Failed))throw new DomainException("Only previewed or failed jobs can start.");State=EnterpriseJobState.Running;StartedAt=now;UpdatedAt=now;Attempt++;LastError=null;}
    public void ReportProgress(int percent,DateTimeOffset now){if(State!=EnterpriseJobState.Running)throw new DomainException("Only running jobs report progress.");ProgressPercent=Math.Clamp(percent,0,99);UpdatedAt=now;}
    public void Complete(int succeeded,int failed,string result,DateTimeOffset now){State=failed==0?EnterpriseJobState.Completed:EnterpriseJobState.Failed;SucceededCount=succeeded;FailedCount=failed;ResultJson=result;ProgressPercent=100;CompletedAt=now;UpdatedAt=now;LastError=failed==0?null:$"{failed} item(s) failed.";}
    public void Fail(string error,DateTimeOffset now){State=EnterpriseJobState.Failed;LastError=error.Trim();UpdatedAt=now;CompletedAt=now;}
    public void Retry(DateTimeOffset now){if(State!=EnterpriseJobState.Failed)throw new DomainException("Only failed jobs can be retried.");State=EnterpriseJobState.Preview;ProgressPercent=0;CompletedAt=null;UpdatedAt=now;LastError=null;}
    public void Cancel(DateTimeOffset now){if(State is EnterpriseJobState.Completed or EnterpriseJobState.Cancelled)throw new DomainException("This job is already final.");State=EnterpriseJobState.Cancelled;UpdatedAt=now;CompletedAt=now;}
}

public sealed class RequirementInterchangeJob
{
    private RequirementInterchangeJob() { }
    public RequirementInterchangeJob(Guid projectId,string fileName,string sha256,string mappingJson,string rowsJson,int valid,int invalid,string actor,DateTimeOffset now)
    { Id=Guid.NewGuid();ProjectId=projectId;FileName=fileName;Sha256=sha256;MappingJson=mappingJson;RowsJson=rowsJson;ValidRows=valid;InvalidRows=invalid;State=EnterpriseJobState.Preview;CreatedBy=actor;CreatedAt=now; }
    public Guid Id { get; private set; }
    public Guid ProjectId { get; private set; }
    public string FileName { get; private set; }="";
    public string Sha256 { get; private set; }="";
    public string MappingJson { get; private set; }="{}";
    public string RowsJson { get; private set; }="[]";
    public int ValidRows { get; private set; }
    public int InvalidRows { get; private set; }
    public EnterpriseJobState State { get; private set; }
    public string CreatedBy { get; private set; }="";
    public DateTimeOffset CreatedAt { get; private set; }
    public Guid? CreatedScrId { get; private set; }
    public DateTimeOffset? CompletedAt { get; private set; }
    public void Commit(Guid scrId,DateTimeOffset now){if(State!=EnterpriseJobState.Preview)throw new DomainException("Only previewed imports can be committed.");State=EnterpriseJobState.Completed;CreatedScrId=scrId;CompletedAt=now;}
}
