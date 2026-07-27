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
record CreateScrRequest(string BaseNumber, Guid ProjectId, Guid TargetReleaseId, string Title, string Problem, string Analysis, string Solution, string AuthorId, ChangeRequestType Type = ChangeRequestType.System, string? ProblemRich = null, string? AnalysisRich = null, string? SolutionRich = null);
record DraftRequirementRequest(string BaseNumber, int Revision, RequirementLevel Level, RequirementChangeKind Kind, string Statement, string Rationale, string VerificationMethod,string RichText="",string AttributesJson="{}",string ImpactDispositionJson="{}",bool IsDerived=false);
record CreateScrDraftRequest(string BaseNumber, Guid ProjectId, Guid TargetReleaseId, string Title, string Problem, string Analysis, string Solution, string AuthorId, List<DraftRequirementRequest> RequirementChanges, ChangeRequestType Type = ChangeRequestType.System, string? ProblemRich = null, string? AnalysisRich = null, string? SolutionRich = null);
record CreateWorkspaceRequest(string ProgramName, string ProgramCode, string ProjectName, string SoftwareProduct, string InitialRelease, bool InitialReleaseIsReleased);
record CreateReleaseRequest(Guid ProjectId, string Version, Guid? PredecessorReleaseId);
record RetargetScrRequest(Guid TargetReleaseId, string Reason);
/// <summary>A reason is required by the domain: a shelf whose entries do not say why is a shelf nobody trusts.</summary>
record DeferScrRequest(string? Reason);
record RequirementChangeRequest(string ActorId, string BaseNumber, int Revision, RequirementLevel Level, RequirementChangeKind Kind, string Statement, string Rationale, string VerificationMethod);
record ApproverRequest(string UserId, string Name);
record SubmitReviewRequest(string ActorId, long? ExpectedVersion, List<ApproverRequest> Approvers, ReviewMode Mode=ReviewMode.Sequential);
record RestartReviewRequest(long? ExpectedVersion, string Reason, List<ApproverRequest> Approvers, ReviewMode Mode=ReviewMode.Sequential);
record ActorRequest(string ActorId, long? ExpectedVersion);
record SignatureRequest(string Password, string Meaning, long? ExpectedVersion);
record RequestChangesRequest(string ActorId, long? ExpectedVersion, string Reason);
record CreateBaselineRequest(string BaseNumber, int Revision, Guid ProjectId, Guid ReleaseId, Guid? PredecessorBaselineId, string Name, string ActorId);
record CreateReleaseCampaignRequest(Guid ProjectId, Guid ReleaseId, Guid BaselineId, string Name);
record BaselineSelectionRequest(Guid ScrId, string ActorId);
record BaselineActorRequest(string ActorId);
record CreateBuildRequest(Guid ProjectId, Guid ReleaseId, Guid BaselineId, string BuildNumber, string Description, string RecordedBy);
record CreateTestProcedureRequest(Guid ProjectId, string BaseNumber, string Title, string OwnerId, string Objective, string Preconditions, string Steps, string ExpectedResult, List<Guid> RequirementRevisionIds, TestProcedureLevel Level = TestProcedureLevel.HighLevel);
record RecordTestExecutionRequest(Guid ProjectId, Guid ProcedureRevisionId, Guid? SoftwareBuildId, Guid? RetestOfExecutionId, TestOutcome Outcome, string ExecutedBy, string Configuration, string Determination, string EvidenceReference, DateTimeOffset ExecutedAt);
record DispositionImpactRequest(ImpactDispositionState State, string Rationale, string ActorId);
record BulkDispositionImpactRequest(Guid? ScrId, ImpactDispositionState State, string Rationale, string ActorId);
record SelectBuildRequest(Guid SoftwareBuildId, string ActorId);
record CampaignActorRequest(string ActorId);
record StartReleaseReviewRequest(string ActorId, List<ApproverRequest> Approvers);
record CompleteReleaseRequest(string ActorId);
record CreateTraceLinkRequest(Guid ProjectId, Guid SourceRevisionId, Guid TargetRevisionId, RequirementTraceType Type, string Rationale);
record GenerateDocumentsRequest(string ActorId);
record CreateUserRequest(string UserName, string DisplayName, string Email, string TemporaryPassword);
record GrantRoleRequest(Guid ProgramId, ProgramRole Role);
record SetAccountStateRequest(bool Enabled);
record CreateDelegationRequest(Guid ProgramId, Guid DelegatorUserId, Guid DelegateUserId, ProgramRole Role, DateTimeOffset StartsAt, DateTimeOffset EndsAt, string Reason);
record CreateArtifactSchemaRequest(Guid ProjectId,string Key,string Name,string AppliesTo,string Description);
record CreateSchemaFieldRequest(string Key,string Label,SchemaFieldType Type,bool IsRequired,int SortOrder,string OptionsJson);
record CreateSpecificationRequest(Guid ProjectId,string DocumentNumber,string Title,string Level,string Description);
record CreateSectionRequest(Guid? ParentId,int Position,string Heading);
record CreateCommentRequest(Guid? RevisionId,Guid? ParentCommentId,string Body,List<string>? Mentions);
record ResolveCommentRequest(string? Disposition);
record CreateSavedViewRequest(Guid ProjectId,string Name,string QueryJson,string ColumnsJson,bool IsShared);
record BulkRequirementRequest(Guid ProjectId,List<Guid> ArtifactIds,string Tag,Guid? SpecificationId,Guid? SectionId);
record BulkJobPayload(List<Guid> ArtifactIds,string Tag,Guid? SpecificationId,Guid? SectionId);
record CommitImportRequest(Guid TargetReleaseId,string BaseNumber,string Title,string Problem,string Analysis,string Solution,ChangeRequestType Type=ChangeRequestType.Software);
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
record SearchResultDto(Guid Id,string Kind,string Identifier,string Title,string State,string Discipline,DateTimeOffset? UpdatedAt);
record RelatedArtifactDto(string Kind,Guid Id,string Identifier,string Title);
