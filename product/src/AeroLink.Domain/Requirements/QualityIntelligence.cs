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
    public ReadinessWaiver(Guid projectId, string blockerType, Guid blockerId, int blockerRevision,
        long blockerVersion, string rationale, Guid approvedByAccountId, string approvedBy,
        string approvalAuthority, string signatureMeaning, DateTimeOffset expiresAt, string actor,
        DateTimeOffset now)
    {
        if (projectId == Guid.Empty || blockerId == Guid.Empty || approvedByAccountId == Guid.Empty)
            throw new DomainException("A readiness waiver requires its Project, blocker, and approving account.");
        if (blockerRevision < 0 || (blockerType == "ProblemReportReleaseBlocker" && blockerVersion < 1))
            throw new DomainException("A readiness waiver requires an exact controlled blocker revision and version.");
        if (expiresAt <= now) throw new DomainException("A readiness waiver must have a future expiry.");
        Id = Guid.NewGuid(); ProjectId = projectId; BlockerType = Required(blockerType); BlockerId = blockerId;
        BlockerRevision = blockerRevision; BlockerVersion = blockerVersion; Rationale = Required(rationale);
        ApprovedByAccountId = approvedByAccountId; ApprovedBy = Required(approvedBy);
        ApprovalAuthority = Required(approvalAuthority); SignatureMeaning = Required(signatureMeaning);
        ExpiresAt = expiresAt; CreatedBy = Required(actor); CreatedAt = now; Provenance = "ServerAuthorized";
    }
    public Guid Id { get; private set; }
    public Guid ProjectId { get; private set; }
    public string BlockerType { get; private set; } = "";
    public Guid BlockerId { get; private set; }
    public int BlockerRevision { get; private set; }
    public long BlockerVersion { get; private set; }
    public string Rationale { get; private set; } = "";
    public Guid? ApprovedByAccountId { get; private set; }
    public string ApprovedBy { get; private set; } = "";
    public string ApprovalAuthority { get; private set; } = "";
    public string SignatureMeaning { get; private set; } = "";
    public string Provenance { get; private set; } = "";
    public DateTimeOffset ExpiresAt { get; private set; }
    public string CreatedBy { get; private set; } = "";
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? RevokedAt { get; private set; }
    public string RevokedBy { get; private set; } = "";
    public string RevocationReason { get; private set; } = "";
    public void Revoke(string actor, string reason, DateTimeOffset now)
    {
        if (RevokedAt is not null) throw new DomainException("This readiness waiver is already revoked.");
        RevokedBy = Required(actor); RevocationReason = Required(reason); RevokedAt = now;
    }
    public bool IsActive(DateTimeOffset now) => Provenance == "ServerAuthorized" && RevokedAt is null && ExpiresAt > now;
    public bool IsActiveFor(ProblemReport report, DateTimeOffset now) => IsActive(now)
        && BlockerType == "ProblemReportReleaseBlocker" && BlockerId == report.Id
        && ProjectId == report.ProjectId && BlockerRevision == report.Revision
        && BlockerVersion == report.ReleaseBlockerVersion && report.IsReleaseBlocker
        && !string.Equals(ApprovedBy, report.ReportedBy, StringComparison.OrdinalIgnoreCase)
        && !string.Equals(ApprovedBy, report.ResponsibleEngineerId, StringComparison.OrdinalIgnoreCase);
    private static string Required(string? value) => string.IsNullOrWhiteSpace(value)
        ? throw new DomainException("Readiness waiver values are required.") : value.Trim();
}

public sealed class CertificationEvidenceIndexEntry
{
    private CertificationEvidenceIndexEntry() { }
    public CertificationEvidenceIndexEntry(Guid projectId,string objectiveCode,string artifactType,Guid artifactId,string evidenceHash,string claimBoundary,string actor,DateTimeOffset now){Id=Guid.NewGuid();ProjectId=projectId;ObjectiveCode=Required(objectiveCode).ToUpperInvariant();ArtifactType=Required(artifactType);ArtifactId=artifactId;EvidenceHash=Hash(evidenceHash);ClaimBoundary=Required(claimBoundary);IndexedBy=Required(actor);IndexedAt=now;}
    public Guid Id{get;private set;}public Guid ProjectId{get;private set;}public string ObjectiveCode{get;private set;}="";public string ArtifactType{get;private set;}="";public Guid ArtifactId{get;private set;}public string EvidenceHash{get;private set;}="";public string ClaimBoundary{get;private set;}="";public string IndexedBy{get;private set;}="";public DateTimeOffset IndexedAt{get;private set;}private static string Required(string? value)=>string.IsNullOrWhiteSpace(value)?throw new DomainException("Evidence index values are required."):value.Trim();private static string Hash(string value)=>Required(value).Length==64?value.ToLowerInvariant():throw new DomainException("Evidence hashes must be SHA-256 values.");
}
