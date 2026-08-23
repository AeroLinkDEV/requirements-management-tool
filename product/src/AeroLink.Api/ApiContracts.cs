using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Identity;
using AeroLink.Domain.Releases;
using AeroLink.Domain.Requirements;
using AeroLink.Domain.Traceability;
using AeroLink.Domain.Verification;

// The shapes the browser sends.
//
// Records rather than classes, because a request body is a value that arrives once and is never mutated, and
// because a positional record turns a missing field into a compile error at the call site instead of a null
// discovered at runtime.
//
// Deliberately still in the global namespace, which is where they were declared alongside the endpoints that
// consume them. Moving them into AeroLink.Api would be tidier and would also rename every one of them for
// anything outside that namespace, for no gain.

record LoginRequest(string UserName, string Password,string? MfaCode=null);
record ChangeOwnPasswordRequest(string CurrentPassword,string NewPassword);
record ConfirmMfaRequest(string Code);
record DisableMfaRequest(string Password,string Code);
record ResetPasswordRequest(string TemporaryPassword,string Reason);
record BootstrapAdministratorRequest(string DisplayName, string Email, string Password);
record CreateChangeRequestRequest(string BaseNumber, Guid ProjectId, Guid TargetReleaseId, string Title, string Problem, string Analysis, string Solution, ChangeRequestType Type = ChangeRequestType.System, string? ProblemRich = null, string? AnalysisRich = null, string? SolutionRich = null, List<Guid>? ProblemReportIds = null, RequirementLevel? SoftwareLevel = null);
record DraftRequirementRequest(string BaseNumber, int Revision, RequirementLevel Level, RequirementChangeKind Kind, string Statement, string Rationale, string VerificationMethod,string RichText="",string AttributesJson="{}",string ImpactDispositionJson="{}",bool IsDerived=false,Guid? TargetSectionId=null,List<Guid>? UpstreamRevisionIds=null);
record CreateChangeRequestDraftRequest(string BaseNumber, Guid ProjectId, Guid TargetReleaseId, string Title, string Problem, string Analysis, string Solution, List<DraftRequirementRequest> RequirementChanges, ChangeRequestType Type = ChangeRequestType.System, string? ProblemRich = null, string? AnalysisRich = null, string? SolutionRich = null, List<Guid>? ProblemReportIds = null, RequirementLevel? SoftwareLevel = null);
record CreateWorkspaceRequest(string ProgramName, string ProgramCode, string ProjectName, string SoftwareProduct, string InitialRelease, bool InitialReleaseIsReleased);
record CreateReleaseRequest(Guid ProjectId, string Version, Guid? PredecessorReleaseId);
record RetargetChangeRequestRequest(Guid TargetReleaseId, string Reason);
// The author re-applies their intent against the new text; the tool does not merge on their behalf.
record RebaseRequirementChangeRequest(string Statement);
record WithdrawChangeRequestRequest(string? Reason);
record ReopenBaselineRequest(string? Reason);
/// <summary>A reason is required by the domain: a shelf whose entries do not say why is a shelf nobody trusts.</summary>
record DeferChangeRequestRequest(string? Reason);
record CancelReviewRequest(string? Reason, long? ExpectedVersion);
record RequirementChangeRequest(string BaseNumber, int Revision, RequirementLevel Level, RequirementChangeKind Kind, string Statement, string Rationale, string VerificationMethod);
record ApproverRequest(string UserId, string Name);
record SubmitReviewRequest(long? ExpectedVersion, List<ApproverRequest> Approvers, ReviewMode Mode=ReviewMode.Sequential);
record RestartReviewRequest(long? ExpectedVersion, string Reason, List<ApproverRequest> Approvers, ReviewMode Mode=ReviewMode.Sequential);
record ActorRequest(long? ExpectedVersion);
/// Optional target build for reinstatement. Absent means "back into the build that shelved it".
record ReinstateChangeRequest(Guid? IntoReleaseId);
record SignatureRequest(string Password, string Meaning, long? ExpectedVersion, string? Rationale = null);
record ReleaseSignatureRequest(string Password, string Meaning, string ExpectedManifestHash, string? Rationale);
record RequestChangesRequest(long? ExpectedVersion, string Reason);
/// <summary>
/// A reviewer's remark. <paramref name="Anchor"/> is "ChangeCase" or "RequirementRevision";
/// <paramref name="RequirementChangeId"/> is set for the second and omitted for the first.
/// </summary>
record ReviewCommentRequest(string Anchor, Guid? RequirementChangeId, string Body);
/// <summary>
/// Revising carries the new text and nothing else. Where a comment is anchored is settled when it is
/// written: a request that could restate the anchor is a request that could silently move a comment to a
/// different requirement, and no caller has any business doing that.
/// </summary>
record ReviseReviewCommentRequest(string Body);
record CreateBaselineRequest(string BaseNumber, int Revision, Guid ProjectId, Guid ReleaseId, Guid? PredecessorBaselineId, string Name);
record CreateReleaseCampaignRequest(Guid ProjectId, Guid ReleaseId, Guid BaselineId, string Name);
record BaselineSelectionRequest(Guid ChangeRequestId);
record BaselineExternalPackageSelectionRequest(Guid BaselineImportId);
/// <summary>An approved test change request whose procedure decisions a baseline is to carry.</summary>
record BaselineTestChangeSelectionRequest(Guid TestChangeRequestId);
record LegacyProcedureManifestBootstrapRequest(string ExpectedHash, bool ConfirmLegacySnapshot);
record EmptyMutationRequest();
record CreateBuildRequest(Guid ProjectId, Guid ReleaseId, Guid BaselineId, string BuildNumber, string Description);
record RecordTestExecutionRequest(Guid ProjectId, Guid ProcedureRevisionId, Guid? SoftwareBuildId, Guid? RetestOfExecutionId, TestOutcome Outcome, string Configuration, string Determination, string EvidenceReference, DateTimeOffset ExecutedAt)
{
    /// <summary>Primary Case/Procedure-neutral wire field; ProcedureRevisionId is the compatibility alias.</summary>
    public Guid? ArtifactRevisionId { get; init; }
}
record DispositionImpactRequest(ImpactDispositionState State, string Rationale);
record BulkDispositionImpactRequest(Guid? ChangeRequestId, ImpactDispositionState State, string Rationale);
record SelectBuildRequest(Guid SoftwareBuildId);
record StartReleaseReviewRequest(List<ApproverRequest> Approvers);
record CancelReleaseReviewRequest(string? Reason);
record CreateTraceLinkRequest(Guid ProjectId, Guid SourceRevisionId, Guid TargetRevisionId, RequirementTraceType Type, string Rationale);
record AcknowledgeExactLinkRequest(string Rationale);
record ResolveExactLinkRequest(ExactLinkResolutionOutcome Outcome, string Rationale);
record CreateUserRequest(string UserName, string DisplayName, string Email, string TemporaryPassword);
record GrantRoleRequest(Guid ProgramId, ProgramRole Role);
record SetAccountStateRequest(bool Enabled);
record CreateDelegationRequest(Guid ProgramId, Guid DelegatorUserId, Guid DelegateUserId, ProgramRole Role, DateTimeOffset StartsAt, DateTimeOffset EndsAt, string Reason);
record CreateArtifactSchemaRequest(Guid ProjectId,string Key,string Name,string AppliesTo,string Description);
record CreateSchemaFieldRequest(string Key,string Label,SchemaFieldType Type,bool IsRequired,int SortOrder,string OptionsJson);
record CreateSpecificationRequest(Guid ProjectId,string DocumentNumber,string Title,string Level,string Description);
record CreateSectionRequest(Guid? ParentId,int Position,string Heading);
record CreateCommentRequest(Guid? RevisionId,Guid? ParentCommentId,string Body,List<string>? Mentions);
/// <summary>A remark on a test procedure. No mention routing yet — the conversation exists first.</summary>
record CreateProcedureCommentRequest(Guid? RevisionId, Guid? ParentCommentId, string Body, List<string>? Mentions);
record CreateDormantProcedureRequest(Guid ProjectId, TestProcedureLevel Level, string Title,
    string EnvironmentSetup, string TestData, string OrderedSteps, string ExpectedObservations,
    string Cleanup, string ToolingAutomation, VerificationProcedureParentKind ParentKind,
    Guid[]? CaseRevisionIds = null, string? DerivedRationale = null, string Objective = "Procedure execution",
    string Preconditions = "");
record ReviseDormantProcedureRequest(string EnvironmentSetup, string TestData, string OrderedSteps,
    string ExpectedObservations, string Cleanup, string ToolingAutomation,
    VerificationProcedureParentKind ParentKind, Guid[]? CaseRevisionIds = null, string? DerivedRationale = null,
    string Objective = "Procedure execution", string Preconditions = "", long? ExpectedVersion = null);
record RetireDormantProcedureRequest(string Rationale, long? ExpectedVersion = null);
record ResolveCommentRequest(string? Disposition);
record CreateSavedViewRequest(Guid ProjectId,string Name,string QueryJson,string ColumnsJson,bool IsShared);
record UpdateSavedViewRequest(string? Name,string? QueryJson,string? ColumnsJson,bool? IsShared);
record BulkRequirementRequest(Guid ProjectId,List<Guid> ArtifactIds,string Tag,Guid? SpecificationId,Guid? SectionId);
record BulkJobPayload(List<Guid> ArtifactIds,string Tag,Guid? SpecificationId,Guid? SectionId);
record CommitImportRequest(Guid TargetReleaseId,string BaseNumber,string Title,string Problem,string Analysis,string Solution,ChangeRequestType Type=ChangeRequestType.Software,RequirementLevel? SoftwareLevel=null);
record ProposeRequirementChangeRequest(Guid TargetReleaseId,RequirementChangeKind Kind,string? Title,Guid? ExistingScrId);
record CreateAssignmentRequest(string AssignedTo,string Title,string Description,DateTimeOffset? DueAt,Guid? CommentId);
record CompleteAssignmentRequest(long ExpectedVersion);
record CreateImportMappingRequest(Guid ProjectId,string Name,string MappingJson);
record CreateEnterpriseJobRequest(Guid ProjectId,string JobType,string RequestJson,string? IdempotencyKey);
record OpenEditSessionRequest(Guid ProjectId,Guid ArtifactId);
record SaveEditSessionRequest(long ExpectedVersion,string DraftJson);
record ResolveMergeConflictRequest(string ResolutionJson);
record CheckoutEditSessionRequest(string ArtifactType,Guid ArtifactId,int? LeaseMinutes=null);
record AutosaveEditSessionRequest(long ExpectedVersion,string DraftJson,int? LeaseMinutes=null);
record HeartbeatEditSessionRequest(long ExpectedVersion,int? LeaseMinutes=null);
record CloseEditSessionRequest(long ExpectedVersion,string? Reason=null);
record ForceUnlockEditSessionRequest(string Reason);
record CreateIntegrityCheckpointRequest(Guid ProjectId);
record PerformanceSample(string Name,long TargetMs,long P95Ms,bool Passed,List<long> Timings);
record SearchResultDto(Guid Id,string Kind,string Identifier,string Title,string State,string Discipline,DateTimeOffset? UpdatedAt,string? Level = null);
record RelatedArtifactDto(string Kind,Guid Id,string Identifier,string Title);
