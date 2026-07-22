using AeroLink.Domain.Common;

namespace AeroLink.Domain.Requirements;

public enum ProductLineComponentState { Draft, Approved, Retired }
public enum ComponentStreamState { Active, Closed }
public enum ProductVariantState { Draft, Approved, Retired }
public enum PropagationDecisionKind { Accept, Defer, Diverge }

/// <summary>Reusable, controlled engineering content with independent release streams.</summary>
public sealed class ProductLineComponent
{
    private ProductLineComponent() { }
    public ProductLineComponent(Guid projectId, string componentNumber, string name, string description, string actor, DateTimeOffset now)
    { Id=Guid.NewGuid(); ProjectId=projectId; ComponentNumber=Required(componentNumber,"A component number is required.").ToUpperInvariant(); Name=Required(name,"A component name is required."); Description=description?.Trim()??""; State=ProductLineComponentState.Draft; CreatedBy=Required(actor,"An actor is required."); CreatedAt=UpdatedAt=now; Version=1; }
    public Guid Id { get; private set; }
    public Guid ProjectId { get; private set; }
    public string ComponentNumber { get; private set; }="";
    public string Name { get; private set; }="";
    public string Description { get; private set; }="";
    public ProductLineComponentState State { get; private set; }
    public string CreatedBy { get; private set; }="";
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public long Version { get; private set; }
    public void Approve(string actor,DateTimeOffset now){if(State==ProductLineComponentState.Retired)throw new DomainException("A retired component cannot be approved.");State=ProductLineComponentState.Approved;UpdatedAt=now;Version++;}
    private static string Required(string? value,string error)=>string.IsNullOrWhiteSpace(value)?throw new DomainException(error):value.Trim();
}

public sealed class ComponentStream
{
    private ComponentStream() { }
    public ComponentStream(Guid componentId,string streamKey,string name,string actor,DateTimeOffset now)
    { Id=Guid.NewGuid();ComponentId=componentId;StreamKey=Required(streamKey,"A stream key is required.").ToUpperInvariant();Name=Required(name,"A stream name is required.");State=ComponentStreamState.Active;CreatedBy=Required(actor,"An actor is required.");CreatedAt=now; }
    public Guid Id { get; private set; }
    public Guid ComponentId { get; private set; }
    public string StreamKey { get; private set; }="";
    public string Name { get; private set; }="";
    public ComponentStreamState State { get; private set; }
    public string CreatedBy { get; private set; }="";
    public DateTimeOffset CreatedAt { get; private set; }
    private static string Required(string? value,string error)=>string.IsNullOrWhiteSpace(value)?throw new DomainException(error):value.Trim();
}

/// <summary>Immutable stream content. A SHA-256 manifest makes reuse and configuration output deterministic.</summary>
public sealed class ComponentStreamRevision
{
    private ComponentStreamRevision() { }
    public ComponentStreamRevision(Guid streamId,int revision,string contentJson,string manifestHash,string actor,DateTimeOffset now)
    {if(revision<1)throw new DomainException("Component stream revisions begin at one.");Id=Guid.NewGuid();StreamId=streamId;Revision=revision;ContentJson=Required(contentJson,"Controlled component content is required.");ManifestHash=Required(manifestHash,"A component manifest hash is required.").ToLowerInvariant();CreatedBy=Required(actor,"An actor is required.");CreatedAt=now;}
    public Guid Id { get; private set; }
    public Guid StreamId { get; private set; }
    public int Revision { get; private set; }
    public string ContentJson { get; private set; }="{}";
    public string ManifestHash { get; private set; }="";
    public string CreatedBy { get; private set; }="";
    public DateTimeOffset CreatedAt { get; private set; }
    private static string Required(string? value,string error)=>string.IsNullOrWhiteSpace(value)?throw new DomainException(error):value.Trim();
}

public sealed class ProductVariant
{
    private ProductVariant() { }
    public ProductVariant(Guid projectId,string variantKey,string name,string applicabilityJson,string actor,DateTimeOffset now)
    {Id=Guid.NewGuid();ProjectId=projectId;VariantKey=Required(variantKey,"A variant key is required.").ToUpperInvariant();Name=Required(name,"A variant name is required.");ApplicabilityJson=string.IsNullOrWhiteSpace(applicabilityJson)?"{}":applicabilityJson;State=ProductVariantState.Draft;CreatedBy=Required(actor,"An actor is required.");CreatedAt=UpdatedAt=now;Version=1;}
    public Guid Id { get; private set; }
    public Guid ProjectId { get; private set; }
    public string VariantKey { get; private set; }="";
    public string Name { get; private set; }="";
    public string ApplicabilityJson { get; private set; }="{}";
    public ProductVariantState State { get; private set; }
    public string CreatedBy { get; private set; }="";
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public long Version { get; private set; }
    public void Approve(DateTimeOffset now){if(State==ProductVariantState.Retired)throw new DomainException("A retired variant cannot be approved.");State=ProductVariantState.Approved;UpdatedAt=now;Version++;}
    private static string Required(string? value,string error)=>string.IsNullOrWhiteSpace(value)?throw new DomainException(error):value.Trim();
}

/// <summary>Frozen component revision selected for a variant. Never follows a moving stream head.</summary>
public sealed class VariantComponentSelection
{
    private VariantComponentSelection() { }
    public VariantComponentSelection(Guid variantId,Guid componentRevisionId,string applicabilityJson,string actor,DateTimeOffset now)
    {Id=Guid.NewGuid();VariantId=variantId;ComponentRevisionId=componentRevisionId;ApplicabilityJson=string.IsNullOrWhiteSpace(applicabilityJson)?"{}":applicabilityJson;SelectedBy=actor.Trim();SelectedAt=now;}
    public Guid Id { get; private set; }
    public Guid VariantId { get; private set; }
    public Guid ComponentRevisionId { get; private set; }
    public string ApplicabilityJson { get; private set; }="{}";
    public string SelectedBy { get; private set; }="";
    public DateTimeOffset SelectedAt { get; private set; }
}

/// <summary>An immutable, reproducible configuration snapshot for one approved product variant.</summary>
public sealed class ProductVariantBaseline
{
    private ProductVariantBaseline() { }
    public ProductVariantBaseline(Guid variantId,int revision,string manifestJson,string manifestHash,string actor,DateTimeOffset now)
    {if(revision<1)throw new DomainException("Variant baseline revisions begin at one.");Id=Guid.NewGuid();VariantId=variantId;Revision=revision;ManifestJson=Required(manifestJson,"A variant manifest is required.");ManifestHash=Required(manifestHash,"A variant manifest hash is required.").ToLowerInvariant();CreatedBy=Required(actor,"An actor is required.");CreatedAt=now;}
    public Guid Id {get;private set;} public Guid VariantId {get;private set;} public int Revision {get;private set;} public string ManifestJson {get;private set;}="{}"; public string ManifestHash {get;private set;}=""; public string CreatedBy {get;private set;}=""; public DateTimeOffset CreatedAt {get;private set;}
    private static string Required(string? value,string error)=>string.IsNullOrWhiteSpace(value)?throw new DomainException(error):value.Trim();
}

/// <summary>An attributable decision describing how a reusable component revision affects a variant.</summary>
public sealed class ComponentPropagationDecision
{
    private ComponentPropagationDecision() { }
    public ComponentPropagationDecision(Guid variantId,Guid componentRevisionId,PropagationDecisionKind decision,string rationale,string actor,DateTimeOffset now)
    {Id=Guid.NewGuid();VariantId=variantId;ComponentRevisionId=componentRevisionId;Decision=decision;Rationale=Required(rationale,"A propagation rationale is required.");DecidedBy=Required(actor,"An actor is required.");DecidedAt=now;}
    public Guid Id {get;private set;} public Guid VariantId {get;private set;} public Guid ComponentRevisionId {get;private set;} public PropagationDecisionKind Decision {get;private set;} public string Rationale {get;private set;}=""; public string DecidedBy {get;private set;}=""; public DateTimeOffset DecidedAt {get;private set;}
    private static string Required(string? value,string error)=>string.IsNullOrWhiteSpace(value)?throw new DomainException(error):value.Trim();
}
