using AeroLink.Domain.Common;

namespace AeroLink.Domain.Requirements;

public enum ControlledLibraryState { Draft, Approved, Retired }
public enum VariantReuseMode { Reference, SynchronizedCopy, IntentionallyDiverged }
public enum ReuseSynchronizationState { Current, UpdateAvailable, Deferred, Diverged, Rejected }

public sealed class ControlledLibrary
{
    private ControlledLibrary() { }
    public ControlledLibrary(Guid projectId,string libraryNumber,string name,string description,string actor,DateTimeOffset now)
    {Id=Guid.NewGuid();ProjectId=projectId;LibraryNumber=Required(libraryNumber,"A library number is required.").ToUpperInvariant();Name=Required(name,"A library name is required.");Description=description?.Trim()??"";State=ControlledLibraryState.Draft;CreatedBy=Required(actor,"An actor is required.");CreatedAt=UpdatedAt=now;Version=1;}
    public Guid Id {get;private set;} public Guid ProjectId {get;private set;} public string LibraryNumber {get;private set;}=""; public string Name {get;private set;}=""; public string Description {get;private set;}=""; public ControlledLibraryState State {get;private set;} public string CreatedBy {get;private set;}=""; public DateTimeOffset CreatedAt {get;private set;} public DateTimeOffset UpdatedAt {get;private set;} public long Version {get;private set;}
    public void Approve(string actor,DateTimeOffset now){if(State==ControlledLibraryState.Retired)throw new DomainException("A retired library cannot be approved.");_ = Required(actor,"An approving actor is required.");State=ControlledLibraryState.Approved;UpdatedAt=now;Version++;}
    private static string Required(string? value,string error)=>string.IsNullOrWhiteSpace(value)?throw new DomainException(error):value.Trim();
}

public sealed class ControlledLibraryRevision
{
    private ControlledLibraryRevision() { }
    public ControlledLibraryRevision(Guid libraryId,int revision,string contentJson,string manifestHash,string actor,DateTimeOffset now)
    {if(revision<1)throw new DomainException("Library revisions begin at one.");Id=Guid.NewGuid();LibraryId=libraryId;Revision=revision;ContentJson=Required(contentJson,"Controlled library content is required.");ManifestHash=Required(manifestHash,"A library manifest hash is required.").ToLowerInvariant();ApprovedBy=Required(actor,"An approving actor is required.");ApprovedAt=now;}
    public Guid Id {get;private set;} public Guid LibraryId {get;private set;} public int Revision {get;private set;} public string ContentJson {get;private set;}="{}"; public string ManifestHash {get;private set;}=""; public string ApprovedBy {get;private set;}=""; public DateTimeOffset ApprovedAt {get;private set;}
    private static string Required(string? value,string error)=>string.IsNullOrWhiteSpace(value)?throw new DomainException(error):value.Trim();
}

public sealed class VariantLibraryReuse
{
    private VariantLibraryReuse() { }
    public VariantLibraryReuse(Guid variantId,Guid libraryId,Guid selectedRevisionId,VariantReuseMode mode,string applicabilityJson,string actor,DateTimeOffset now)
    {Id=Guid.NewGuid();VariantId=variantId;LibraryId=libraryId;SelectedRevisionId=LatestUpstreamRevisionId=selectedRevisionId;Mode=mode;ApplicabilityJson=string.IsNullOrWhiteSpace(applicabilityJson)?"{}":applicabilityJson;SynchronizationState=mode==VariantReuseMode.IntentionallyDiverged?ReuseSynchronizationState.Diverged:ReuseSynchronizationState.Current;DecidedBy=Required(actor,"An actor is required.");DecisionRationale="Initial controlled reuse selection.";UpdatedAt=now;Version=1;}
    public Guid Id {get;private set;} public Guid VariantId {get;private set;} public Guid LibraryId {get;private set;} public Guid SelectedRevisionId {get;private set;} public Guid LatestUpstreamRevisionId {get;private set;} public VariantReuseMode Mode {get;private set;} public ReuseSynchronizationState SynchronizationState {get;private set;} public string ApplicabilityJson {get;private set;}="{}"; public string DecisionRationale {get;private set;}=""; public string DecidedBy {get;private set;}=""; public DateTimeOffset UpdatedAt {get;private set;} public long Version {get;private set;}
    public void NotifyUpstream(Guid revisionId,DateTimeOffset now){LatestUpstreamRevisionId=revisionId;if(Mode!=VariantReuseMode.IntentionallyDiverged&&SelectedRevisionId!=revisionId)SynchronizationState=ReuseSynchronizationState.UpdateAvailable;UpdatedAt=now;Version++;}
    public void Decide(PropagationDecisionKind decision,Guid upstreamRevisionId,string rationale,string actor,DateTimeOffset now)
    {if(LatestUpstreamRevisionId!=upstreamRevisionId)throw new DomainException("The reuse decision does not target the latest upstream revision.");DecisionRationale=Required(rationale,"A reuse decision rationale is required.");DecidedBy=Required(actor,"An actor is required.");switch(decision){case PropagationDecisionKind.Accept:SelectedRevisionId=upstreamRevisionId;Mode=VariantReuseMode.SynchronizedCopy;SynchronizationState=ReuseSynchronizationState.Current;break;case PropagationDecisionKind.Defer:SynchronizationState=ReuseSynchronizationState.Deferred;break;case PropagationDecisionKind.Reject:SynchronizationState=ReuseSynchronizationState.Rejected;break;case PropagationDecisionKind.Diverge:Mode=VariantReuseMode.IntentionallyDiverged;SynchronizationState=ReuseSynchronizationState.Diverged;break;}UpdatedAt=now;Version++;}
    private static string Required(string? value,string error)=>string.IsNullOrWhiteSpace(value)?throw new DomainException(error):value.Trim();
}

public sealed class LibraryPropagationDecision
{
    private LibraryPropagationDecision() { }
    public LibraryPropagationDecision(Guid reuseId,Guid variantId,Guid libraryId,Guid previousRevisionId,Guid upstreamRevisionId,PropagationDecisionKind decision,string rationale,string actor,DateTimeOffset now)
    {Id=Guid.NewGuid();ReuseId=reuseId;VariantId=variantId;LibraryId=libraryId;PreviousRevisionId=previousRevisionId;UpstreamRevisionId=upstreamRevisionId;Decision=decision;Rationale=Required(rationale,"A propagation decision rationale is required.");DecidedBy=Required(actor,"A deciding actor is required.");DecidedAt=now;}
    public Guid Id {get;private set;} public Guid ReuseId {get;private set;} public Guid VariantId {get;private set;} public Guid LibraryId {get;private set;} public Guid PreviousRevisionId {get;private set;} public Guid UpstreamRevisionId {get;private set;} public PropagationDecisionKind Decision {get;private set;} public string Rationale {get;private set;}=""; public string DecidedBy {get;private set;}=""; public DateTimeOffset DecidedAt {get;private set;}
    private static string Required(string? value,string error)=>string.IsNullOrWhiteSpace(value)?throw new DomainException(error):value.Trim();
}

public sealed class DocumentTemplateRevision
{
    private DocumentTemplateRevision() { }
    public DocumentTemplateRevision(Guid templateId,int revision,string templateKind,string organization,string bodyJson,string manifestHash,string actor,DateTimeOffset now)
    {if(revision<1)throw new DomainException("Template revisions begin at one.");Id=Guid.NewGuid();TemplateId=templateId;Revision=revision;TemplateKind=Required(templateKind,"A template kind is required.");Organization=Required(organization,"A template organization is required.");BodyJson=Required(bodyJson,"A template body is required.");ManifestHash=Required(manifestHash,"A template manifest hash is required.").ToLowerInvariant();ApprovedBy=Required(actor,"An approving actor is required.");ApprovedAt=now;}
    public Guid Id {get;private set;} public Guid TemplateId {get;private set;} public int Revision {get;private set;} public string TemplateKind {get;private set;}="Generic"; public string Organization {get;private set;}=""; public string BodyJson {get;private set;}="{}"; public string ManifestHash {get;private set;}=""; public string ApprovedBy {get;private set;}=""; public DateTimeOffset ApprovedAt {get;private set;}
    private static string Required(string? value,string error)=>string.IsNullOrWhiteSpace(value)?throw new DomainException(error):value.Trim();
}
