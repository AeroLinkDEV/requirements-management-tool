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
    /// <summary>Removes a schema from the current project catalogue without deleting its controlled history.</summary>
    public void SetActive(bool active) => IsActive = active;
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
    { Id=Guid.NewGuid();ProjectId=projectId;DocumentNumber=ArtifactNumber.ValidateBase(documentNumber);Title=Required(title, "A specification title is required.");Level=Required(level, "A specification level is required.");Description=description.Trim();CreatedBy=actor;CreatedAt=now;UpdatedAt=now;IsActive=true; }
    public Guid Id { get; private set; }
    public Guid ProjectId { get; private set; }
    public string DocumentNumber { get; private set; }="";
    public string Title { get; private set; }="";
    public string Level { get; private set; }="";
    public string Description { get; private set; }="";
    public string CreatedBy { get; private set; }="";
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public long Version { get; private set; } = 1;
    /// <summary>Current catalogue membership; inactive specifications remain available as historical records.</summary>
    public bool IsActive { get; private set; }

    public void SetActive(bool active, string actor, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(actor)) throw new DomainException("A specification update actor is required.");
        IsActive = active;
        UpdatedAt = now;
    }

    public void UpdateDraft(string title, string level, string description, string actor, DateTimeOffset now)
    {
        Title = Required(title, "A specification title is required.");
        Level = Required(level, "A specification level is required.");
        Description = description.Trim();
        UpdatedAt = now;
    }

    public void RecordStructureUpdate(string actor, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(actor)) throw new DomainException("A specification update actor is required.");
        UpdatedAt = now;
    }

    private static string Required(string value, string message) => string.IsNullOrWhiteSpace(value)
        ? throw new DomainException(message) : value.Trim();
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

    public void UpdateDraft(Guid? parentId, int position, string heading, string actor, DateTimeOffset now)
    {
        if (position < 0) throw new DomainException("A specification node position cannot be negative.");
        if (Type == SpecificationNodeType.Section && string.IsNullOrWhiteSpace(heading))
            throw new DomainException("A specification section heading is required.");
        if (string.IsNullOrWhiteSpace(actor)) throw new DomainException("A specification update actor is required.");
        ParentId = parentId; Position = position; Heading = heading.Trim();
    }
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
    /// <summary>
    /// The owner as a queryable field rather than a fragment of serialized attributes.
    ///
    /// Owner filtering matched a substring of AttributesJson, so an owner fragment could match another
    /// attribute's value entirely — and a leading-wildcard substring scan over raw JSON cannot use an index.
    /// The authored attributes remain the source; this is the normalized copy the query reads.
    /// </summary>
    public string Owner { get; private set; }="";
    public void SetOwner(string owner)=>Owner=RequirementFilterValue.Normalize(owner);
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
    { Id=Guid.NewGuid();ProjectId=projectId;OwnerId=ownerId;Name=name.Trim();QueryJson=queryJson;ColumnsJson=columnsJson;IsShared=shared;CreatedAt=now;UpdatedAt=now; }
    public Guid Id { get; private set; }
    public Guid ProjectId { get; private set; }
    public Guid OwnerId { get; private set; }
    public string Name { get; private set; }="";
    public string QueryJson { get; private set; }="{}";
    public string ColumnsJson { get; private set; }="[]";
    public bool IsShared { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>
    /// Renaming, resharing and replacing are separate from creating, because a view somebody else has a link
    /// to must keep its identity when its owner tidies it up. Replacing the query in place is deliberate: the
    /// alternative was saving a second view with the same name, which is how the duplicates that could not be
    /// removed through the product came to exist.
    /// </summary>
    public void Rename(string name, DateTimeOffset now)
    {
        var trimmed = (name ?? "").Trim();
        if (trimmed.Length == 0) throw new DomainException("A saved view needs a name.");
        Name = trimmed; UpdatedAt = now;
    }

    public void SetShared(bool shared, DateTimeOffset now) { IsShared = shared; UpdatedAt = now; }

    public void Replace(string queryJson, string columnsJson, DateTimeOffset now)
    { QueryJson = queryJson; ColumnsJson = columnsJson; UpdatedAt = now; }
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
    /// <summary>Which worker holds this job, so an abandoned claim can be told from a live one.</summary>
    public string? ClaimedBy { get; private set; }
    public DateTimeOffset? ClaimedAt { get; private set; }
    /// <summary>When the claim stops being believed. A worker that dies stops renewing this.</summary>
    public DateTimeOffset? LeaseExpiresAt { get; private set; }
    /// <summary>
    /// Every error this job has ever reported, oldest first, one line each.
    ///
    /// `LastError` alone meant a retry erased why the previous attempt failed, which is the history an operator
    /// needs most: a job that fails the same way four times is a different problem from one that fails four
    /// different ways.
    /// </summary>
    public string ErrorHistoryJson { get; private set; }="[]";
    /// <summary>Incremented on every transition, so a stale worker cannot write over a newer decision.</summary>
    public long Version { get; private set; }

    /// <summary>
    /// Takes ownership for a bounded period. The caller is responsible for having won the claim — the atomic
    /// conditional update lives in the worker, because only the database can decide a race between processes.
    /// </summary>
    public void Claim(string worker,DateTimeOffset now,TimeSpan lease)
    {
        if(State is not (EnterpriseJobState.Preview or EnterpriseJobState.Failed))throw new DomainException("Only previewed or failed jobs can start.");
        if(lease<=TimeSpan.Zero)throw new DomainException("A job lease must be a positive duration.");
        State=EnterpriseJobState.Running;StartedAt=now;UpdatedAt=now;Attempt++;LastError=null;
        ClaimedBy=string.IsNullOrWhiteSpace(worker)?throw new DomainException("A claiming worker must identify itself."):worker.Trim();
        ClaimedAt=now;LeaseExpiresAt=now+lease;Touch();
    }

    /// <summary>
    /// Claims the job for the request that is about to do the work itself, rather than for a background worker.
    /// The lease still exists so that a request which dies mid-way leaves a recoverable job rather than one
    /// stuck Running for ever.
    /// </summary>
    public void RunInline(string actor,DateTimeOffset now)=>Claim(actor,now,TimeSpan.FromMinutes(5));

    /// <summary>Extends the lease while work continues, and reports progress in the same act.</summary>
    public void Heartbeat(int percent,DateTimeOffset now,TimeSpan lease)
    {
        ReportProgress(percent,now);
        LeaseExpiresAt=now+lease;
    }

    /// <summary>True once nobody has renewed the lease, which is the only evidence that a worker is gone.</summary>
    public bool LeaseExpired(DateTimeOffset now)=>State==EnterpriseJobState.Running&&LeaseExpiresAt is not null&&LeaseExpiresAt<=now;

    /// <summary>
    /// Returns an abandoned job to the queue so another worker can take it, or fails it for good once it has
    /// used up its attempts. A crash is otherwise indistinguishable from work still in progress.
    /// </summary>
    public void RecoverExpiredLease(DateTimeOffset now,int maximumAttempts)
    {
        if(!LeaseExpired(now))throw new DomainException("Only a job whose lease has expired can be recovered.");
        Release();
        var reason=$"Worker stopped responding; lease expired after attempt {Attempt}.";
        if(Attempt>=maximumAttempts){State=EnterpriseJobState.Failed;CompletedAt=now;LastError=reason;}
        else{State=EnterpriseJobState.Preview;ProgressPercent=0;CompletedAt=null;LastError=reason;}
        Record(reason,now);UpdatedAt=now;Touch();
    }

    public void ReportProgress(int percent,DateTimeOffset now){if(State!=EnterpriseJobState.Running)throw new DomainException("Only running jobs report progress.");ProgressPercent=Math.Clamp(percent,0,99);UpdatedAt=now;Touch();}

    /// <summary>
    /// Records the outcome, and only for a job still running.
    ///
    /// This used to accept any state, so a worker holding a stale entity could write Completed over a
    /// cancellation an operator had already made — the job reported success for work somebody stopped.
    /// </summary>
    public void Complete(int succeeded,int failed,string result,DateTimeOffset now)
    {
        if(State!=EnterpriseJobState.Running)throw new DomainException("Only a running job can record an outcome.");
        State=failed==0?EnterpriseJobState.Completed:EnterpriseJobState.Failed;SucceededCount=succeeded;FailedCount=failed;ResultJson=result;ProgressPercent=100;CompletedAt=now;UpdatedAt=now;
        LastError=failed==0?null:$"{failed} item(s) failed.";
        if(failed!=0)Record(LastError!,now);
        Release();Touch();
    }

    public void Fail(string error,DateTimeOffset now)
    {
        if(State is EnterpriseJobState.Completed or EnterpriseJobState.Cancelled)throw new DomainException("This job is already final.");
        State=EnterpriseJobState.Failed;LastError=error.Trim();UpdatedAt=now;CompletedAt=now;Record(error,now);Release();Touch();
    }

    /// <summary>
    /// Hands the job back for another attempt without recording an outcome, for a shutdown rather than a
    /// failure. A process stopping is not the job failing, and must not consume an attempt's evidence.
    /// </summary>
    public void ReleaseForShutdown(DateTimeOffset now)
    {
        if(State!=EnterpriseJobState.Running)return;
        State=EnterpriseJobState.Preview;ProgressPercent=0;CompletedAt=null;UpdatedAt=now;
        var reason="Worker shut down before finishing; returned to the queue.";
        LastError=reason;Record(reason,now);Release();Touch();
    }

    public void Retry(DateTimeOffset now){if(State!=EnterpriseJobState.Failed)throw new DomainException("Only failed jobs can be retried.");State=EnterpriseJobState.Preview;ProgressPercent=0;CompletedAt=null;UpdatedAt=now;LastError=null;Release();Touch();}
    public void Cancel(DateTimeOffset now){if(State is EnterpriseJobState.Completed or EnterpriseJobState.Cancelled)throw new DomainException("This job is already final.");State=EnterpriseJobState.Cancelled;UpdatedAt=now;CompletedAt=now;Release();Touch();}

    private void Release(){ClaimedBy=null;ClaimedAt=null;LeaseExpiresAt=null;}
    private void Touch()=>Version++;

    /// <summary>Appends to the error history, keeping the most recent twenty so it cannot grow without bound.</summary>
    private void Record(string error,DateTimeOffset now)
    {
        var trimmed=(error??"").Trim();
        if(trimmed.Length==0)return;
        var entries=ErrorHistory().ToList();
        entries.Add(new JobErrorRecord(Attempt,now,trimmed.Length>500?trimmed[..500]:trimmed));
        ErrorHistoryJson=System.Text.Json.JsonSerializer.Serialize(entries.TakeLast(20));
    }

    public IReadOnlyList<JobErrorRecord> ErrorHistory()
    {
        try { return System.Text.Json.JsonSerializer.Deserialize<List<JobErrorRecord>>(ErrorHistoryJson)??[]; }
        catch (System.Text.Json.JsonException) { return []; }
    }
}

/// <summary>One attempt's failure, kept so a retry does not erase why the attempt before it failed.</summary>
public sealed record JobErrorRecord(int Attempt,DateTimeOffset OccurredAt,string Error);

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
    public Guid? CreatedChangeRequestId { get; private set; }
    public DateTimeOffset? CompletedAt { get; private set; }
    public void Commit(Guid changeRequestId,DateTimeOffset now){if(State!=EnterpriseJobState.Preview)throw new DomainException("Only previewed imports can be committed.");State=EnterpriseJobState.Completed;CreatedChangeRequestId=changeRequestId;CompletedAt=now;}
}

/// <summary>
/// How much of a project the enterprise workspace has already backfilled.
///
/// The workspace has to give every requirement a schema profile and a place in its specification. That is a
/// backfill — it converts records created before the workspace existed, or created by a path that does not
/// place them — and it is idempotent, so it was simply run on every read of the requirements explorer.
///
/// At fifty thousand requirements and a hundred and fifty people that cost nine seconds a page: every
/// request loaded every requirement, every revision, every profile and every specification node in the
/// project before returning the fifty rows somebody asked for. This watermark is what lets the read path
/// find out there is nothing to do without loading everything to discover it. Only two things can create
/// work — and new revisions already get their profile from baseline materialization — so recording how many
/// requirements existed when the backfill last completed answers the question with one indexed count.
/// </summary>
public sealed class ProjectWorkspaceSynchronization
{
    private ProjectWorkspaceSynchronization() { }

    public ProjectWorkspaceSynchronization(Guid projectId, int artifactCount, DateTimeOffset now)
    {
        Id = Guid.NewGuid();
        ProjectId = projectId;
        Record(artifactCount, now);
    }

    public Guid Id { get; private set; }
    public Guid ProjectId { get; private set; }
    public int ArtifactCount { get; private set; }
    public DateTimeOffset SynchronizedAt { get; private set; }

    public void Record(int artifactCount, DateTimeOffset now)
    {
        ArtifactCount = artifactCount;
        SynchronizedAt = now;
    }

    /// <summary>
    /// True when no requirement has been added since the backfill last ran. Deliberately conservative: any
    /// doubt resolves to running the backfill, because a requirement with no profile is invisible in the
    /// workspace, and a slow page is a far better failure than a missing requirement.
    /// </summary>
    public bool IsCurrent(int artifactCount) => ArtifactCount == artifactCount;
}
