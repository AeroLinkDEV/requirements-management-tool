using AeroLink.Domain.Common;

namespace AeroLink.Domain.Requirements;

public sealed class QualityLifecycleObjective
{
    private QualityLifecycleObjective() { }
    public QualityLifecycleObjective(Guid projectId,string code,string title,string targetJson,string evidenceExpectation,string actor,DateTimeOffset now){Id=Guid.NewGuid();ProjectId=projectId;Code=Required(code).ToUpperInvariant();Title=Required(title);TargetJson=Required(targetJson);EvidenceExpectation=Required(evidenceExpectation);IsActive=true;CreatedBy=Required(actor);CreatedAt=now;}
    public Guid Id{get;private set;}public Guid ProjectId{get;private set;}public string Code{get;private set;}="";public string Title{get;private set;}="";public string TargetJson{get;private set;}="{}";public string EvidenceExpectation{get;private set;}="";public bool IsActive{get;private set;}public string CreatedBy{get;private set;}="";public DateTimeOffset CreatedAt{get;private set;}private static string Required(string? value)=>string.IsNullOrWhiteSpace(value)?throw new DomainException("Quality objective values are required."):value.Trim();
}

public sealed class ReadinessWaiver
{
    private ReadinessWaiver() { }
    public ReadinessWaiver(Guid projectId,string blockerType,Guid blockerId,string rationale,string approvedBy,DateTimeOffset expiresAt,string actor,DateTimeOffset now){if(expiresAt<=now)throw new DomainException("A readiness waiver must have a future expiry.");Id=Guid.NewGuid();ProjectId=projectId;BlockerType=Required(blockerType);BlockerId=blockerId;Rationale=Required(rationale);ApprovedBy=Required(approvedBy);ExpiresAt=expiresAt;CreatedBy=Required(actor);CreatedAt=now;}
    public Guid Id{get;private set;}public Guid ProjectId{get;private set;}public string BlockerType{get;private set;}="";public Guid BlockerId{get;private set;}public string Rationale{get;private set;}="";public string ApprovedBy{get;private set;}="";public DateTimeOffset ExpiresAt{get;private set;}public string CreatedBy{get;private set;}="";public DateTimeOffset CreatedAt{get;private set;}public DateTimeOffset? RevokedAt{get;private set;}public void Revoke(DateTimeOffset now)=>RevokedAt=now;public bool IsActive(DateTimeOffset now)=>RevokedAt is null&&ExpiresAt>now;private static string Required(string? value)=>string.IsNullOrWhiteSpace(value)?throw new DomainException("Readiness waiver values are required."):value.Trim();
}

public sealed class CertificationEvidenceIndexEntry
{
    private CertificationEvidenceIndexEntry() { }
    public CertificationEvidenceIndexEntry(Guid projectId,string objectiveCode,string artifactType,Guid artifactId,string evidenceHash,string claimBoundary,string actor,DateTimeOffset now){Id=Guid.NewGuid();ProjectId=projectId;ObjectiveCode=Required(objectiveCode).ToUpperInvariant();ArtifactType=Required(artifactType);ArtifactId=artifactId;EvidenceHash=Hash(evidenceHash);ClaimBoundary=Required(claimBoundary);IndexedBy=Required(actor);IndexedAt=now;}
    public Guid Id{get;private set;}public Guid ProjectId{get;private set;}public string ObjectiveCode{get;private set;}="";public string ArtifactType{get;private set;}="";public Guid ArtifactId{get;private set;}public string EvidenceHash{get;private set;}="";public string ClaimBoundary{get;private set;}="";public string IndexedBy{get;private set;}="";public DateTimeOffset IndexedAt{get;private set;}private static string Required(string? value)=>string.IsNullOrWhiteSpace(value)?throw new DomainException("Evidence index values are required."):value.Trim();private static string Hash(string value)=>Required(value).Length==64?value.ToLowerInvariant():throw new DomainException("Evidence hashes must be SHA-256 values.");
}
