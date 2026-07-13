using AeroLink.Domain.Common;

namespace AeroLink.Domain.Requirements;

public enum AssignmentState { Open, Completed, Cancelled }
public enum NotificationState { Unread, Read }

public sealed class ArtifactWatch
{
    private ArtifactWatch() { }
    public ArtifactWatch(Guid projectId,string artifactType,Guid artifactId,string userName,string actor,DateTimeOffset now)
    { Id=Guid.NewGuid();ProjectId=projectId;ArtifactType=Required(artifactType);ArtifactId=artifactId;UserName=Required(userName).ToLowerInvariant();CreatedBy=Required(actor);CreatedAt=now; }
    public Guid Id { get; private set; }
    public Guid ProjectId { get; private set; }
    public string ArtifactType { get; private set; }="";
    public Guid ArtifactId { get; private set; }
    public string UserName { get; private set; }="";
    public string CreatedBy { get; private set; }="";
    public DateTimeOffset CreatedAt { get; private set; }
    private static string Required(string value)=>string.IsNullOrWhiteSpace(value)?throw new DomainException("A required value is missing."):value.Trim();
}

public sealed class ArtifactAssignment
{
    private ArtifactAssignment() { }
    public ArtifactAssignment(Guid projectId,string artifactType,Guid artifactId,Guid? commentId,string assignedTo,string title,string description,DateTimeOffset? dueAt,string actor,DateTimeOffset now)
    { if(string.IsNullOrWhiteSpace(title))throw new DomainException("An assignment title is required.");Id=Guid.NewGuid();ProjectId=projectId;ArtifactType=artifactType.Trim();ArtifactId=artifactId;CommentId=commentId;AssignedTo=assignedTo.Trim().ToLowerInvariant();Title=title.Trim();Description=description.Trim();DueAt=dueAt;State=AssignmentState.Open;CreatedBy=actor;CreatedAt=now;UpdatedAt=now;Version=1; }
    public Guid Id { get; private set; }
    public Guid ProjectId { get; private set; }
    public string ArtifactType { get; private set; }="";
    public Guid ArtifactId { get; private set; }
    public Guid? CommentId { get; private set; }
    public string AssignedTo { get; private set; }="";
    public string Title { get; private set; }="";
    public string Description { get; private set; }="";
    public DateTimeOffset? DueAt { get; private set; }
    public AssignmentState State { get; private set; }
    public string CreatedBy { get; private set; }="";
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public long Version { get; private set; }
    public string? CompletedBy { get; private set; }
    public void Complete(string actor,long expectedVersion,DateTimeOffset now){if(Version!=expectedVersion)throw new DomainException("This assignment changed after it was opened.");if(State!=AssignmentState.Open)throw new DomainException("Only open assignments can be completed.");State=AssignmentState.Completed;CompletedBy=actor;UpdatedAt=now;Version++;}
}

public sealed class UserNotification
{
    private UserNotification() { }
    public UserNotification(Guid projectId,string recipient,string type,string title,string detail,string route,Guid? artifactId,DateTimeOffset now)
    { Id=Guid.NewGuid();ProjectId=projectId;Recipient=recipient.Trim().ToLowerInvariant();Type=type.Trim();Title=title.Trim();Detail=detail.Trim();Route=route.Trim();ArtifactId=artifactId;State=NotificationState.Unread;CreatedAt=now; }
    public Guid Id { get; private set; }
    public Guid ProjectId { get; private set; }
    public string Recipient { get; private set; }="";
    public string Type { get; private set; }="";
    public string Title { get; private set; }="";
    public string Detail { get; private set; }="";
    public string Route { get; private set; }="";
    public Guid? ArtifactId { get; private set; }
    public NotificationState State { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? ReadAt { get; private set; }
    public void MarkRead(DateTimeOffset now){if(State==NotificationState.Read)return;State=NotificationState.Read;ReadAt=now;}
}

public sealed class RequirementImportMapping
{
    private RequirementImportMapping() { }
    public RequirementImportMapping(Guid projectId,string name,string mappingJson,string actor,DateTimeOffset now)
    { if(string.IsNullOrWhiteSpace(name))throw new DomainException("A mapping name is required.");Id=Guid.NewGuid();ProjectId=projectId;Name=name.Trim();MappingJson=string.IsNullOrWhiteSpace(mappingJson)?"{}":mappingJson;CreatedBy=actor;CreatedAt=now;UpdatedAt=now;Version=1; }
    public Guid Id { get; private set; }
    public Guid ProjectId { get; private set; }
    public string Name { get; private set; }="";
    public string MappingJson { get; private set; }="{}";
    public string CreatedBy { get; private set; }="";
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public int Version { get; private set; }
}
