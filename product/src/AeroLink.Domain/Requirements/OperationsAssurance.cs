using AeroLink.Domain.Common;

namespace AeroLink.Domain.Requirements;

public enum OperationalEvidenceState { Passed, Failed }
public enum OperationalAlertState { Open, Acknowledged, Resolved }

public sealed class WorkloadQualificationEvidence
{
    private WorkloadQualificationEvidence() { }
    public WorkloadQualificationEvidence(Guid projectId,string environment,int requirementCount,int concurrentUsers,int durationMinutes,string resultsJson,string reportHash,bool allPassed,string actor,DateTimeOffset now)
    {if(requirementCount<1||concurrentUsers<1||durationMinutes<1)throw new DomainException("Qualification workload, concurrency, and duration must be positive.");Id=Guid.NewGuid();ProjectId=projectId;Environment=Required(environment);RequirementCount=requirementCount;ConcurrentUsers=concurrentUsers;DurationMinutes=durationMinutes;ResultsJson=Required(resultsJson);ReportHash=Hash(reportHash);State=allPassed?OperationalEvidenceState.Passed:OperationalEvidenceState.Failed;ExecutedBy=Required(actor);ExecutedAt=now;}
    public Guid Id{get;private set;}public Guid ProjectId{get;private set;}public string Environment{get;private set;}="";public int RequirementCount{get;private set;}public int ConcurrentUsers{get;private set;}public int DurationMinutes{get;private set;}public string ResultsJson{get;private set;}="{}";public string ReportHash{get;private set;}="";public OperationalEvidenceState State{get;private set;}public string ExecutedBy{get;private set;}="";public DateTimeOffset ExecutedAt{get;private set;}
    public bool MeetsProductionTarget=>State==OperationalEvidenceState.Passed&&RequirementCount>=50_000&&ConcurrentUsers>=150;
    private static string Required(string? value)=>string.IsNullOrWhiteSpace(value)?throw new DomainException("Operational evidence values are required."):value.Trim();private static string Hash(string value)=>Required(value).Length==64?value.ToLowerInvariant():throw new DomainException("Evidence hashes must be SHA-256 values.");
}

public sealed class BackupRestoreDrillEvidence
{
    private BackupRestoreDrillEvidence() { }
    public BackupRestoreDrillEvidence(Guid projectId,string backupLocation,string backupHash,DateTimeOffset backupCreatedAt,DateTimeOffset offsiteVerifiedAt,int targetRpoMinutes,int targetRtoMinutes,int actualRpoMinutes,int actualRtoMinutes,string restoreEnvironment,string evidenceHash,string actor,DateTimeOffset now)
    {if(targetRpoMinutes<1||targetRtoMinutes<1||actualRpoMinutes<0||actualRtoMinutes<0)throw new DomainException("RPO and RTO evidence values are invalid.");Id=Guid.NewGuid();ProjectId=projectId;BackupLocation=Required(backupLocation);BackupHash=Hash(backupHash);BackupCreatedAt=backupCreatedAt;OffsiteVerifiedAt=offsiteVerifiedAt;TargetRpoMinutes=targetRpoMinutes;TargetRtoMinutes=targetRtoMinutes;ActualRpoMinutes=actualRpoMinutes;ActualRtoMinutes=actualRtoMinutes;RestoreEnvironment=Required(restoreEnvironment);EvidenceHash=Hash(evidenceHash);State=actualRpoMinutes<=targetRpoMinutes&&actualRtoMinutes<=targetRtoMinutes?OperationalEvidenceState.Passed:OperationalEvidenceState.Failed;ExecutedBy=Required(actor);ExecutedAt=now;}
    public Guid Id{get;private set;}public Guid ProjectId{get;private set;}public string BackupLocation{get;private set;}="";public string BackupHash{get;private set;}="";public DateTimeOffset BackupCreatedAt{get;private set;}public DateTimeOffset OffsiteVerifiedAt{get;private set;}public int TargetRpoMinutes{get;private set;}public int TargetRtoMinutes{get;private set;}public int ActualRpoMinutes{get;private set;}public int ActualRtoMinutes{get;private set;}public string RestoreEnvironment{get;private set;}="";public string EvidenceHash{get;private set;}="";public OperationalEvidenceState State{get;private set;}public string ExecutedBy{get;private set;}="";public DateTimeOffset ExecutedAt{get;private set;}
    private static string Required(string? value)=>string.IsNullOrWhiteSpace(value)?throw new DomainException("Backup and restore evidence values are required."):value.Trim();private static string Hash(string value)=>Required(value).Length==64?value.ToLowerInvariant():throw new DomainException("Evidence hashes must be SHA-256 values.");
}

public sealed class RetentionPolicyEvidence
{
    private RetentionPolicyEvidence() { }
    public RetentionPolicyEvidence(Guid projectId,string recordType,int retentionDays,bool legalHold,string rationale,string actor,DateTimeOffset now)
    {if(retentionDays<1)throw new DomainException("Retention must be at least one day.");Id=Guid.NewGuid();ProjectId=projectId;RecordType=Required(recordType);RetentionDays=retentionDays;LegalHold=legalHold;Rationale=Required(rationale);ConfiguredBy=Required(actor);ConfiguredAt=now;}
    public Guid Id{get;private set;}public Guid ProjectId{get;private set;}public string RecordType{get;private set;}="";public int RetentionDays{get;private set;}public bool LegalHold{get;private set;}public string Rationale{get;private set;}="";public string ConfiguredBy{get;private set;}="";public DateTimeOffset ConfiguredAt{get;private set;}
    private static string Required(string? value)=>string.IsNullOrWhiteSpace(value)?throw new DomainException("Retention policy values are required."):value.Trim();
}

public sealed class UpgradeAssuranceEvidence
{
    private UpgradeAssuranceEvidence() { }
    public UpgradeAssuranceEvidence(Guid projectId,string fromVersion,string toVersion,string preflightJson,string postCheckJson,string evidenceHash,bool passed,string actor,DateTimeOffset now)
    {Id=Guid.NewGuid();ProjectId=projectId;FromVersion=Required(fromVersion);ToVersion=Required(toVersion);PreflightJson=Required(preflightJson);PostCheckJson=Required(postCheckJson);EvidenceHash=Hash(evidenceHash);State=passed?OperationalEvidenceState.Passed:OperationalEvidenceState.Failed;ExecutedBy=Required(actor);ExecutedAt=now;}
    public Guid Id{get;private set;}public Guid ProjectId{get;private set;}public string FromVersion{get;private set;}="";public string ToVersion{get;private set;}="";public string PreflightJson{get;private set;}="{}";public string PostCheckJson{get;private set;}="{}";public string EvidenceHash{get;private set;}="";public OperationalEvidenceState State{get;private set;}public string ExecutedBy{get;private set;}="";public DateTimeOffset ExecutedAt{get;private set;}
    private static string Required(string? value)=>string.IsNullOrWhiteSpace(value)?throw new DomainException("Upgrade evidence values are required."):value.Trim();private static string Hash(string value)=>Required(value).Length==64?value.ToLowerInvariant():throw new DomainException("Evidence hashes must be SHA-256 values.");
}

public sealed class OperationalAlert
{
    private OperationalAlert() { }
    public OperationalAlert(Guid projectId,string severity,string signal,string detail,string runbookUrl,string actor,DateTimeOffset now){Id=Guid.NewGuid();ProjectId=projectId;Severity=Required(severity);Signal=Required(signal);Detail=Required(detail);RunbookUrl=Required(runbookUrl);State=OperationalAlertState.Open;OpenedBy=Required(actor);OpenedAt=now;}
    public Guid Id{get;private set;}public Guid ProjectId{get;private set;}public string Severity{get;private set;}="";public string Signal{get;private set;}="";public string Detail{get;private set;}="";public string RunbookUrl{get;private set;}="";public OperationalAlertState State{get;private set;}public string OpenedBy{get;private set;}="";public DateTimeOffset OpenedAt{get;private set;}public string? ResolvedBy{get;private set;}public DateTimeOffset? ResolvedAt{get;private set;}
    public void Resolve(string actor,DateTimeOffset now){if(State==OperationalAlertState.Resolved)throw new DomainException("The alert is already resolved.");State=OperationalAlertState.Resolved;ResolvedBy=Required(actor);ResolvedAt=now;}private static string Required(string? value)=>string.IsNullOrWhiteSpace(value)?throw new DomainException("Operational alert values are required."):value.Trim();
}
