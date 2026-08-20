using AeroLink.Domain.Baselines;
using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Common;
using AeroLink.Domain.Programs;
using AeroLink.Domain.Imports;
using AeroLink.Domain.Requirements;
using AeroLink.Domain.Integrations;
using AeroLink.Domain.Verification;
using AeroLink.Domain.Traceability;
using AeroLink.Domain.Releases;
using AeroLink.Domain.Identity;
using AeroLink.Domain.Notifications;
using AeroLink.Domain.Documents;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace AeroLink.Infrastructure.Persistence;

public sealed class AeroLinkDbContext(DbContextOptions<AeroLinkDbContext> options) : DbContext(options)
{
    public DbSet<ProgramRecord> Programs => Set<ProgramRecord>();
    public DbSet<IdentifierSequence> IdentifierSequences => Set<IdentifierSequence>();
    public DbSet<ShowcaseUpgradeStep> ShowcaseUpgradeSteps => Set<ShowcaseUpgradeStep>();
    public DbSet<ProjectRecord> Projects => Set<ProjectRecord>();
    public DbSet<SoftwareRelease> Releases => Set<SoftwareRelease>();
    public DbSet<SoftwareBuild> SoftwareBuilds => Set<SoftwareBuild>();
    public DbSet<SystemChangeRequest> SystemChangeRequests => Set<SystemChangeRequest>();
    public DbSet<RequirementChange> RequirementChanges => Set<RequirementChange>();
    public DbSet<DownstreamChangeAssessment> DownstreamChangeAssessments => Set<DownstreamChangeAssessment>();
    public DbSet<DownstreamAssessmentChangeRequestLink> DownstreamAssessmentChangeRequestLinks => Set<DownstreamAssessmentChangeRequestLink>();
    public DbSet<DownstreamAssessmentReopening> DownstreamAssessmentReopenings => Set<DownstreamAssessmentReopening>();
    public DbSet<BaselineImport> BaselineImports => Set<BaselineImport>();
    public DbSet<SourceIdentity> SourceIdentities => Set<SourceIdentity>();
    public DbSet<SourceIdentityLink> SourceIdentityLinks => Set<SourceIdentityLink>();
    public DbSet<SourceHistoryEntry> SourceHistoryEntries => Set<SourceHistoryEntry>();
    public DbSet<ReviewCycle> ReviewCycles => Set<ReviewCycle>();
    public DbSet<ApprovalStep> ApprovalSteps => Set<ApprovalStep>();
    public DbSet<ReviewComment> ReviewComments => Set<ReviewComment>();
    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();
    public DbSet<CandidateBaseline> CandidateBaselines => Set<CandidateBaseline>();
    public DbSet<BaselineChangeRequestSelection> BaselineSelections => Set<BaselineChangeRequestSelection>();
    public DbSet<BaselineTestChangeRequestSelection> BaselineTestChangeSelections => Set<BaselineTestChangeRequestSelection>();
    public DbSet<BaselineEvent> BaselineEvents => Set<BaselineEvent>();
    public DbSet<RequirementArtifact> Requirements => Set<RequirementArtifact>();
    public DbSet<RequirementRevision> RequirementRevisions => Set<RequirementRevision>();
    public DbSet<BaselineRequirementSelection> BaselineRequirements => Set<BaselineRequirementSelection>();
    public DbSet<TestProcedure> TestProcedures => Set<TestProcedure>();
    public DbSet<TestProcedureRevision> TestProcedureRevisions => Set<TestProcedureRevision>();
    public DbSet<BaselineTestProcedureSelection> BaselineTestProcedures => Set<BaselineTestProcedureSelection>();
    public DbSet<TestRequirementCoverage> TestCoverage => Set<TestRequirementCoverage>();
    public DbSet<TestExecution> TestExecutions => Set<TestExecution>();
    public DbSet<TestChangeReview> TestChangeReviews => Set<TestChangeReview>();
    public DbSet<TestChangeRequestClaim> TestChangeRequestClaims => Set<TestChangeRequestClaim>();
    public DbSet<BuildTestSet> BuildTestSets => Set<BuildTestSet>();
    public DbSet<BuildTestSetEntry> BuildTestSetEntries => Set<BuildTestSetEntry>();
    public DbSet<VerificationImpactItem> VerificationImpactItems => Set<VerificationImpactItem>();
    public DbSet<VerificationImpactDecisionHistory> VerificationImpactDecisionHistory => Set<VerificationImpactDecisionHistory>();
    public DbSet<RequirementTraceLink> RequirementTraces => Set<RequirementTraceLink>();
    public DbSet<ControlledDocument> ControlledDocuments => Set<ControlledDocument>();
    public DbSet<ControlledDocumentArtifact> ControlledDocumentArtifacts => Set<ControlledDocumentArtifact>();
    public DbSet<ReleaseCampaign> ReleaseCampaigns => Set<ReleaseCampaign>();
    public DbSet<ReleaseApproval> ReleaseApprovals => Set<ReleaseApproval>();
    public DbSet<ReleaseCampaignEvent> ReleaseCampaignEvents => Set<ReleaseCampaignEvent>();
    public DbSet<ChangeImpactDisposition> ImpactDispositions => Set<ChangeImpactDisposition>();
    public DbSet<EvidenceRecord> EvidenceRecords => Set<EvidenceRecord>();
    public DbSet<TestExecutionEvidence> TestExecutionEvidence => Set<TestExecutionEvidence>();
    public DbSet<UserAccount> UserAccounts => Set<UserAccount>();
    public DbSet<ProgramMembership> ProgramMemberships => Set<ProgramMembership>();
    public DbSet<UserSession> UserSessions => Set<UserSession>();
    public DbSet<UserMfaEnrollment> UserMfaEnrollments => Set<UserMfaEnrollment>();
    public DbSet<MfaRecoveryCode> MfaRecoveryCodes => Set<MfaRecoveryCode>();
    public DbSet<RoleDelegation> RoleDelegations => Set<RoleDelegation>();
    public DbSet<ProjectRoleBackup> ProjectRoleBackups => Set<ProjectRoleBackup>();
    public DbSet<ElectronicSignature> ElectronicSignatures => Set<ElectronicSignature>();
    public DbSet<SecurityAuditEvent> SecurityAuditEvents => Set<SecurityAuditEvent>();
    public DbSet<ExternalIdentityProvider> ExternalIdentityProviders => Set<ExternalIdentityProvider>();
    public DbSet<ExternalGroupRoleMapping> ExternalGroupRoleMappings => Set<ExternalGroupRoleMapping>();
    public DbSet<ArtifactSchemaDefinition> ArtifactSchemas => Set<ArtifactSchemaDefinition>();
    public DbSet<ArtifactFieldDefinition> ArtifactFieldDefinitions => Set<ArtifactFieldDefinition>();
    public DbSet<RequirementSpecification> RequirementSpecifications => Set<RequirementSpecification>();
    public DbSet<SpecificationNode> SpecificationNodes => Set<SpecificationNode>();
    public DbSet<TestProcedureDocument> TestProcedureDocuments => Set<TestProcedureDocument>();
    public DbSet<TestProcedureDocumentNode> TestProcedureDocumentNodes => Set<TestProcedureDocumentNode>();
    public DbSet<SavedProcedureView> SavedProcedureViews => Set<SavedProcedureView>();
    public DbSet<RequirementRevisionProfile> RequirementRevisionProfiles => Set<RequirementRevisionProfile>();
    public DbSet<RequirementRevisionTag> RequirementRevisionTags => Set<RequirementRevisionTag>();
    public DbSet<ArtifactComment> ArtifactComments => Set<ArtifactComment>();
    public DbSet<SavedRequirementView> SavedRequirementViews => Set<SavedRequirementView>();
    public DbSet<EnterpriseOperationJob> EnterpriseOperationJobs => Set<EnterpriseOperationJob>();
    public DbSet<RequirementInterchangeJob> RequirementInterchangeJobs => Set<RequirementInterchangeJob>();
    public DbSet<ArtifactWatch> ArtifactWatches => Set<ArtifactWatch>();
    public DbSet<ArtifactAssignment> ArtifactAssignments => Set<ArtifactAssignment>();
    public DbSet<UserNotification> UserNotifications => Set<UserNotification>();
    public DbSet<ReviewWorkflow> ReviewWorkflows => Set<ReviewWorkflow>();
    public DbSet<ProjectWorkspaceSynchronization> ProjectWorkspaceSynchronizations => Set<ProjectWorkspaceSynchronization>();
    public DbSet<JiraConnection> JiraConnections => Set<JiraConnection>();
    public DbSet<JiraIssueLink> JiraIssueLinks => Set<JiraIssueLink>();
    public DbSet<NotificationDelivery> NotificationDeliveries => Set<NotificationDelivery>();
    public DbSet<NotificationPreference> NotificationPreferences => Set<NotificationPreference>();
    public DbSet<RequirementImportMapping> RequirementImportMappings => Set<RequirementImportMapping>();
    public DbSet<DocumentTemplate> DocumentTemplates => Set<DocumentTemplate>();
    public DbSet<ProblemReport> ProblemReports => Set<ProblemReport>();
    public DbSet<ProblemReportRevision> ProblemReportRevisions => Set<ProblemReportRevision>();
    public DbSet<ProblemReportLink> ProblemReportLinks => Set<ProblemReportLink>();
    public DbSet<ProblemReportClosureCandidate> ProblemReportClosureCandidates => Set<ProblemReportClosureCandidate>();
    public DbSet<CodeTraceabilityRecord> CodeTraceabilityRecords => Set<CodeTraceabilityRecord>();
    public DbSet<ConfigurationChangeSet> ConfigurationChangeSets => Set<ConfigurationChangeSet>();
    public DbSet<ControlledAttachment> ControlledAttachments => Set<ControlledAttachment>();
    public DbSet<ArtifactEditSession> ArtifactEditSessions => Set<ArtifactEditSession>();
    public DbSet<ArtifactDraftSnapshot> ArtifactDraftSnapshots => Set<ArtifactDraftSnapshot>();
    public DbSet<ControlledArtifactCheckInEvidence> ControlledArtifactCheckInEvidence => Set<ControlledArtifactCheckInEvidence>();
    public DbSet<ArtifactMergeConflict> ArtifactMergeConflicts => Set<ArtifactMergeConflict>();
    public DbSet<EnterpriseIntegrityCheckpoint> EnterpriseIntegrityCheckpoints => Set<EnterpriseIntegrityCheckpoint>();
    public DbSet<IntegrationServiceIdentity> IntegrationServiceIdentities => Set<IntegrationServiceIdentity>();
    public DbSet<WebhookSubscription> WebhookSubscriptions => Set<WebhookSubscription>();
    public DbSet<IntegrationEvent> IntegrationEvents => Set<IntegrationEvent>();
    public DbSet<WebhookDelivery> WebhookDeliveries => Set<WebhookDelivery>();
    public DbSet<ReqIfExchangeJob> ReqIfExchangeJobs => Set<ReqIfExchangeJob>();
    public DbSet<ProductLineComponent> ProductLineComponents => Set<ProductLineComponent>();
    public DbSet<ComponentStream> ComponentStreams => Set<ComponentStream>();
    public DbSet<ComponentStreamRevision> ComponentStreamRevisions => Set<ComponentStreamRevision>();
    public DbSet<ProductVariant> ProductVariants => Set<ProductVariant>();
    public DbSet<VariantComponentSelection> VariantComponentSelections => Set<VariantComponentSelection>();
    public DbSet<ProductVariantBaseline> ProductVariantBaselines => Set<ProductVariantBaseline>();
    public DbSet<ComponentPropagationDecision> ComponentPropagationDecisions => Set<ComponentPropagationDecision>();
    public DbSet<ControlledLibrary> ControlledLibraries => Set<ControlledLibrary>();
    public DbSet<ControlledLibraryRevision> ControlledLibraryRevisions => Set<ControlledLibraryRevision>();
    public DbSet<VariantLibraryReuse> VariantLibraryReuses => Set<VariantLibraryReuse>();
    public DbSet<LibraryPropagationDecision> LibraryPropagationDecisions => Set<LibraryPropagationDecision>();
    public DbSet<DocumentTemplateRevision> DocumentTemplateRevisions => Set<DocumentTemplateRevision>();
    public DbSet<WorkloadQualificationEvidence> WorkloadQualificationEvidence => Set<WorkloadQualificationEvidence>();
    public DbSet<BackupRestoreDrillEvidence> BackupRestoreDrillEvidence => Set<BackupRestoreDrillEvidence>();
    public DbSet<RetentionPolicyEvidence> RetentionPolicyEvidence => Set<RetentionPolicyEvidence>();
    public DbSet<UpgradeAssuranceEvidence> UpgradeAssuranceEvidence => Set<UpgradeAssuranceEvidence>();
    public DbSet<OperationalAlert> OperationalAlerts => Set<OperationalAlert>();
    public DbSet<QualityLifecycleObjective> QualityLifecycleObjectives => Set<QualityLifecycleObjective>();
    public DbSet<ReadinessWaiver> ReadinessWaivers => Set<ReadinessWaiver>();
    public DbSet<CertificationEvidenceIndexEntry> CertificationEvidenceIndex => Set<CertificationEvidenceIndexEntry>();
    public DbSet<ManagedDocument> ManagedDocuments => Set<ManagedDocument>();
    public DbSet<ManagedDocumentRevision> ManagedDocumentRevisions => Set<ManagedDocumentRevision>();
    public DbSet<ManagedDocumentCheckIn> ManagedDocumentCheckIns => Set<ManagedDocumentCheckIn>();
    public DbSet<ManagedDocumentReviewContributor> ManagedDocumentReviewContributors => Set<ManagedDocumentReviewContributor>();
    public DbSet<ManagedDocumentAssignment> ManagedDocumentAssignments => Set<ManagedDocumentAssignment>();
    public DbSet<ManagedDocumentReviewStep> ManagedDocumentReviewSteps => Set<ManagedDocumentReviewStep>();
    public DbSet<ManagedDocumentOperation> ManagedDocumentOperations => Set<ManagedDocumentOperation>();
    public DbSet<ManagedDocumentStorageOperation> ManagedDocumentStorageOperations => Set<ManagedDocumentStorageOperation>();
    public DbSet<ManagedDocumentBuildProvenance> ManagedDocumentBuildProvenance => Set<ManagedDocumentBuildProvenance>();
    public DbSet<ManagedDocumentLink> ManagedDocumentLinks => Set<ManagedDocumentLink>();
    public DbSet<ManagedDocumentEvent> ManagedDocumentEvents => Set<ManagedDocumentEvent>();
    public DbSet<DocumentConnectorGrant> DocumentConnectorGrants => Set<DocumentConnectorGrant>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ShowcaseUpgradeStep>(b =>
        {
            b.ToTable("showcase_upgrade_steps"); b.HasKey(x => x.Id);
            b.Property(x => x.StepKey).HasMaxLength(100).IsRequired();
            b.Property(x => x.Detail).HasMaxLength(500).IsRequired();
            // One row per step per Program: the unique index is what makes re-running the upgrade a no-op
            // rather than a duplicate, even when two processes start at once.
            b.HasIndex(x => new { x.ProgramId, x.StepKey }).IsUnique();
            b.HasOne<ProgramRecord>().WithMany().HasForeignKey(x => x.ProgramId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ProgramRecord>(b =>
        {
            b.ToTable("programs"); b.HasKey(x => x.Id);
            b.Property(x => x.Name).HasMaxLength(200).IsRequired();
            b.Property(x => x.Code).HasMaxLength(30).IsRequired();
            b.HasIndex(x => x.Code).IsUnique();
        });
        modelBuilder.Entity<ProjectRecord>(b =>
        {
            b.ToTable("projects"); b.HasKey(x => x.Id);
            b.Property(x => x.Name).HasMaxLength(200).IsRequired();
            b.Property(x => x.SoftwareProduct).HasMaxLength(200).IsRequired();
            b.HasIndex(x => new { x.ProgramId, x.Name }).IsUnique();
        });
        modelBuilder.Entity<ProductLineComponent>(b =>
        {
            b.ToTable("product_line_components"); b.HasKey(x=>x.Id); b.Property(x=>x.ComponentNumber).HasMaxLength(80).IsRequired(); b.Property(x=>x.Name).HasMaxLength(300).IsRequired(); b.Property(x=>x.Description).HasMaxLength(8000); b.Property(x=>x.CreatedBy).HasMaxLength(100).IsRequired(); b.Property(x=>x.State).HasConversion<string>().HasMaxLength(30); b.Property(x=>x.Version).IsConcurrencyToken(); b.HasIndex(x=>new{x.ProjectId,x.ComponentNumber}).IsUnique(); b.HasOne<ProjectRecord>().WithMany().HasForeignKey(x=>x.ProjectId).OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<ComponentStream>(b =>
        {
            b.ToTable("component_streams"); b.HasKey(x=>x.Id); b.Property(x=>x.StreamKey).HasMaxLength(80).IsRequired(); b.Property(x=>x.Name).HasMaxLength(300).IsRequired(); b.Property(x=>x.CreatedBy).HasMaxLength(100).IsRequired(); b.Property(x=>x.State).HasConversion<string>().HasMaxLength(30); b.HasIndex(x=>new{x.ComponentId,x.StreamKey}).IsUnique(); b.HasOne<ProductLineComponent>().WithMany().HasForeignKey(x=>x.ComponentId).OnDelete(DeleteBehavior.Cascade);
        });
        modelBuilder.Entity<ComponentStreamRevision>(b =>
        {
            b.ToTable("component_stream_revisions"); b.HasKey(x=>x.Id); b.Property(x=>x.ContentJson).IsRequired(); b.Property(x=>x.ManifestHash).HasMaxLength(64).IsRequired(); b.Property(x=>x.CreatedBy).HasMaxLength(100).IsRequired(); b.HasIndex(x=>new{x.StreamId,x.Revision}).IsUnique(); b.HasIndex(x=>x.ManifestHash); b.HasOne<ComponentStream>().WithMany().HasForeignKey(x=>x.StreamId).OnDelete(DeleteBehavior.Cascade);
        });
        modelBuilder.Entity<ProductVariant>(b =>
        {
            b.ToTable("product_variants"); b.HasKey(x=>x.Id); b.Property(x=>x.VariantKey).HasMaxLength(80).IsRequired(); b.Property(x=>x.Name).HasMaxLength(300).IsRequired(); b.Property(x=>x.ApplicabilityJson).IsRequired(); b.Property(x=>x.State).HasConversion<string>().HasMaxLength(30); b.Property(x=>x.CreatedBy).HasMaxLength(100).IsRequired(); b.Property(x=>x.Version).IsConcurrencyToken(); b.HasIndex(x=>new{x.ProjectId,x.VariantKey}).IsUnique(); b.HasOne<ProjectRecord>().WithMany().HasForeignKey(x=>x.ProjectId).OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<VariantComponentSelection>(b =>
        {
            b.ToTable("variant_component_selections"); b.HasKey(x=>x.Id); b.Property(x=>x.ApplicabilityJson).IsRequired(); b.Property(x=>x.SelectedBy).HasMaxLength(100).IsRequired(); b.HasIndex(x=>new{x.VariantId,x.ComponentRevisionId}).IsUnique(); b.HasOne<ProductVariant>().WithMany().HasForeignKey(x=>x.VariantId).OnDelete(DeleteBehavior.Cascade); b.HasOne<ComponentStreamRevision>().WithMany().HasForeignKey(x=>x.ComponentRevisionId).OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<ProductVariantBaseline>(b=>{b.ToTable("product_variant_baselines");b.HasKey(x=>x.Id);b.Property(x=>x.ManifestJson).IsRequired();b.Property(x=>x.ManifestHash).HasMaxLength(64).IsRequired();b.Property(x=>x.CreatedBy).HasMaxLength(100).IsRequired();b.HasIndex(x=>new{x.VariantId,x.Revision}).IsUnique();b.HasIndex(x=>x.ManifestHash);b.HasOne<ProductVariant>().WithMany().HasForeignKey(x=>x.VariantId).OnDelete(DeleteBehavior.Restrict);});
        modelBuilder.Entity<ComponentPropagationDecision>(b=>{b.ToTable("component_propagation_decisions");b.HasKey(x=>x.Id);b.Property(x=>x.Decision).HasConversion<string>().HasMaxLength(30);b.Property(x=>x.Rationale).HasMaxLength(4000).IsRequired();b.Property(x=>x.DecidedBy).HasMaxLength(100).IsRequired();b.HasIndex(x=>new{x.VariantId,x.ComponentRevisionId}).IsUnique();b.HasOne<ProductVariant>().WithMany().HasForeignKey(x=>x.VariantId).OnDelete(DeleteBehavior.Restrict);b.HasOne<ComponentStreamRevision>().WithMany().HasForeignKey(x=>x.ComponentRevisionId).OnDelete(DeleteBehavior.Restrict);});
        modelBuilder.Entity<ControlledLibrary>(b=>{b.ToTable("controlled_libraries");b.HasKey(x=>x.Id);b.Property(x=>x.LibraryNumber).HasMaxLength(80).IsRequired();b.Property(x=>x.Name).HasMaxLength(300).IsRequired();b.Property(x=>x.Description).HasMaxLength(8000);b.Property(x=>x.State).HasConversion<string>().HasMaxLength(30);b.Property(x=>x.CreatedBy).HasMaxLength(100).IsRequired();b.Property(x=>x.Version).IsConcurrencyToken();b.HasIndex(x=>new{x.ProjectId,x.LibraryNumber}).IsUnique();b.HasOne<ProjectRecord>().WithMany().HasForeignKey(x=>x.ProjectId).OnDelete(DeleteBehavior.Restrict);});
        modelBuilder.Entity<ControlledLibraryRevision>(b=>{b.ToTable("controlled_library_revisions");b.HasKey(x=>x.Id);b.Property(x=>x.ContentJson).IsRequired();b.Property(x=>x.ManifestHash).HasMaxLength(64).IsRequired();b.Property(x=>x.ApprovedBy).HasMaxLength(100).IsRequired();b.HasIndex(x=>new{x.LibraryId,x.Revision}).IsUnique();b.HasIndex(x=>x.ManifestHash);b.HasOne<ControlledLibrary>().WithMany().HasForeignKey(x=>x.LibraryId).OnDelete(DeleteBehavior.Restrict);});
        modelBuilder.Entity<VariantLibraryReuse>(b=>{b.ToTable("variant_library_reuses");b.HasKey(x=>x.Id);b.Property(x=>x.Mode).HasConversion<string>().HasMaxLength(30);b.Property(x=>x.SynchronizationState).HasConversion<string>().HasMaxLength(30);b.Property(x=>x.ApplicabilityJson).IsRequired();b.Property(x=>x.DecisionRationale).HasMaxLength(4000).IsRequired();b.Property(x=>x.DecidedBy).HasMaxLength(100).IsRequired();b.Property(x=>x.Version).IsConcurrencyToken();b.HasIndex(x=>new{x.VariantId,x.LibraryId}).IsUnique();b.HasOne<ProductVariant>().WithMany().HasForeignKey(x=>x.VariantId).OnDelete(DeleteBehavior.Restrict);b.HasOne<ControlledLibrary>().WithMany().HasForeignKey(x=>x.LibraryId).OnDelete(DeleteBehavior.Restrict);b.HasOne<ControlledLibraryRevision>().WithMany().HasForeignKey(x=>x.SelectedRevisionId).OnDelete(DeleteBehavior.Restrict);b.HasOne<ControlledLibraryRevision>().WithMany().HasForeignKey(x=>x.LatestUpstreamRevisionId).OnDelete(DeleteBehavior.Restrict);});
        modelBuilder.Entity<LibraryPropagationDecision>(b=>{b.ToTable("library_propagation_decisions");b.HasKey(x=>x.Id);b.Property(x=>x.Decision).HasConversion<string>().HasMaxLength(30);b.Property(x=>x.Rationale).HasMaxLength(4000).IsRequired();b.Property(x=>x.DecidedBy).HasMaxLength(100).IsRequired();b.HasIndex(x=>new{x.ReuseId,x.DecidedAt});b.HasOne<VariantLibraryReuse>().WithMany().HasForeignKey(x=>x.ReuseId).OnDelete(DeleteBehavior.Restrict);b.HasOne<ProductVariant>().WithMany().HasForeignKey(x=>x.VariantId).OnDelete(DeleteBehavior.Restrict);b.HasOne<ControlledLibrary>().WithMany().HasForeignKey(x=>x.LibraryId).OnDelete(DeleteBehavior.Restrict);b.HasOne<ControlledLibraryRevision>().WithMany().HasForeignKey(x=>x.PreviousRevisionId).OnDelete(DeleteBehavior.Restrict);b.HasOne<ControlledLibraryRevision>().WithMany().HasForeignKey(x=>x.UpstreamRevisionId).OnDelete(DeleteBehavior.Restrict);});
        modelBuilder.Entity<DocumentTemplateRevision>(b=>{b.ToTable("document_template_revisions");b.HasKey(x=>x.Id);b.Property(x=>x.TemplateKind).HasMaxLength(80).IsRequired();b.Property(x=>x.Organization).HasMaxLength(200).IsRequired();b.Property(x=>x.BodyJson).IsRequired();b.Property(x=>x.ManifestHash).HasMaxLength(64).IsRequired();b.Property(x=>x.ApprovedBy).HasMaxLength(100).IsRequired();b.HasIndex(x=>new{x.TemplateId,x.Revision}).IsUnique();b.HasIndex(x=>x.ManifestHash);b.HasOne<DocumentTemplate>().WithMany().HasForeignKey(x=>x.TemplateId).OnDelete(DeleteBehavior.Restrict);});
        modelBuilder.Entity<WorkloadQualificationEvidence>(b=>{b.ToTable("workload_qualification_evidence");b.HasKey(x=>x.Id);b.Property(x=>x.Environment).HasMaxLength(200).IsRequired();b.Property(x=>x.ResultsJson).IsRequired();b.Property(x=>x.ReportHash).HasMaxLength(64).IsRequired();b.Property(x=>x.State).HasConversion<string>().HasMaxLength(30);b.Property(x=>x.ExecutedBy).HasMaxLength(100).IsRequired();b.HasIndex(x=>new{x.ProjectId,x.ExecutedAt});});
        modelBuilder.Entity<BackupRestoreDrillEvidence>(b=>{b.ToTable("backup_restore_drill_evidence");b.HasKey(x=>x.Id);b.Property(x=>x.BackupLocation).HasMaxLength(500).IsRequired();b.Property(x=>x.BackupHash).HasMaxLength(64).IsRequired();b.Property(x=>x.RestoreEnvironment).HasMaxLength(200).IsRequired();b.Property(x=>x.EvidenceHash).HasMaxLength(64).IsRequired();b.Property(x=>x.State).HasConversion<string>().HasMaxLength(30);b.Property(x=>x.ExecutedBy).HasMaxLength(100).IsRequired();b.HasIndex(x=>new{x.ProjectId,x.ExecutedAt});});
        modelBuilder.Entity<RetentionPolicyEvidence>(b=>{b.ToTable("retention_policy_evidence");b.HasKey(x=>x.Id);b.Property(x=>x.RecordType).HasMaxLength(100).IsRequired();b.Property(x=>x.Rationale).HasMaxLength(4000).IsRequired();b.Property(x=>x.ConfiguredBy).HasMaxLength(100).IsRequired();b.HasIndex(x=>new{x.ProjectId,x.RecordType,x.ConfiguredAt});});
        modelBuilder.Entity<UpgradeAssuranceEvidence>(b=>{b.ToTable("upgrade_assurance_evidence");b.HasKey(x=>x.Id);b.Property(x=>x.FromVersion).HasMaxLength(80).IsRequired();b.Property(x=>x.ToVersion).HasMaxLength(80).IsRequired();b.Property(x=>x.PreflightJson).IsRequired();b.Property(x=>x.PostCheckJson).IsRequired();b.Property(x=>x.EvidenceHash).HasMaxLength(64).IsRequired();b.Property(x=>x.State).HasConversion<string>().HasMaxLength(30);b.Property(x=>x.ExecutedBy).HasMaxLength(100).IsRequired();b.HasIndex(x=>new{x.ProjectId,x.ExecutedAt});});
        modelBuilder.Entity<OperationalAlert>(b=>{b.ToTable("operational_alerts");b.HasKey(x=>x.Id);b.Property(x=>x.Severity).HasMaxLength(30).IsRequired();b.Property(x=>x.Signal).HasMaxLength(160).IsRequired();b.Property(x=>x.Detail).HasMaxLength(4000).IsRequired();b.Property(x=>x.RunbookUrl).HasMaxLength(500).IsRequired();b.Property(x=>x.State).HasConversion<string>().HasMaxLength(30);b.Property(x=>x.OpenedBy).HasMaxLength(100).IsRequired();b.Property(x=>x.ResolvedBy).HasMaxLength(100);b.HasIndex(x=>new{x.ProjectId,x.State,x.OpenedAt});});
        modelBuilder.Entity<QualityLifecycleObjective>(b=>{b.ToTable("quality_lifecycle_objectives");b.HasKey(x=>x.Id);b.Property(x=>x.Code).HasMaxLength(80).IsRequired();b.Property(x=>x.Title).HasMaxLength(300).IsRequired();b.Property(x=>x.TargetJson).IsRequired();b.Property(x=>x.EvidenceExpectation).HasMaxLength(4000).IsRequired();b.Property(x=>x.CreatedBy).HasMaxLength(100).IsRequired();b.HasIndex(x=>new{x.ProjectId,x.Code}).IsUnique();});
        modelBuilder.Entity<ReadinessWaiver>(b=>{b.ToTable("readiness_waivers");b.HasKey(x=>x.Id);b.Property(x=>x.BlockerType).HasMaxLength(80).IsRequired();b.Property(x=>x.Rationale).HasMaxLength(4000).IsRequired();b.Property(x=>x.ApprovedBy).HasMaxLength(100).IsRequired();b.Property(x=>x.ApprovalAuthority).HasMaxLength(80).IsRequired();b.Property(x=>x.SignatureMeaning).HasMaxLength(120).IsRequired();b.Property(x=>x.Provenance).HasMaxLength(80).IsRequired();b.Property(x=>x.CreatedBy).HasMaxLength(100).IsRequired();b.Property(x=>x.RevokedBy).HasMaxLength(100);b.Property(x=>x.RevocationReason).HasMaxLength(4000);b.HasIndex(x=>new{x.ProjectId,x.BlockerType,x.BlockerId,x.BlockerRevision,x.BlockerVersion});b.HasIndex(x=>new{x.ProjectId,x.BlockerType,x.BlockerId,x.ExpiresAt});});
        modelBuilder.Entity<CertificationEvidenceIndexEntry>(b=>{b.ToTable("certification_evidence_index");b.HasKey(x=>x.Id);b.Property(x=>x.ObjectiveCode).HasMaxLength(80).IsRequired();b.Property(x=>x.ArtifactType).HasMaxLength(80).IsRequired();b.Property(x=>x.EvidenceHash).HasMaxLength(64).IsRequired();b.Property(x=>x.ClaimBoundary).HasMaxLength(2000).IsRequired();b.Property(x=>x.IndexedBy).HasMaxLength(100).IsRequired();b.HasIndex(x=>new{x.ProjectId,x.ObjectiveCode,x.ArtifactType,x.ArtifactId}).IsUnique();});
        modelBuilder.Entity<SoftwareRelease>(b =>
        {
            b.ToTable("software_releases"); b.HasKey(x => x.Id);
            b.Property(x => x.Version).HasMaxLength(40).IsRequired();
            b.HasIndex(x => new { x.ProjectId, x.Version }).IsUnique();
            b.HasIndex(x => x.PredecessorReleaseId);
            b.HasOne<SoftwareRelease>().WithMany().HasForeignKey(x => x.PredecessorReleaseId).OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<SoftwareBuild>(b =>
        {
            b.ToTable("software_builds"); b.HasKey(x => x.Id);
            b.Property(x => x.BuildNumber).HasMaxLength(80).IsRequired();
            b.Property(x => x.Description).HasMaxLength(2000);
            b.Property(x => x.RecordedBy).HasMaxLength(100).IsRequired();
            b.Property(x => x.State).HasConversion<string>().HasMaxLength(30);
            b.HasIndex(x => new { x.ProjectId, x.BuildNumber }).IsUnique();
            b.HasIndex(x => new { x.ProjectId, x.ReleaseId, x.RecordedAt });
            b.HasIndex(x => x.BaselineId);
            b.HasOne<ProjectRecord>().WithMany().HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<SoftwareRelease>().WithMany().HasForeignKey(x => x.ReleaseId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<CandidateBaseline>().WithMany().HasForeignKey(x => x.BaselineId).OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<SystemChangeRequest>(b =>
        {
            b.ToTable("system_change_requests"); b.HasKey(x => x.Id);
            b.Property(x => x.BaseNumber).HasMaxLength(30).IsRequired();
            b.Property(x => x.Title).HasMaxLength(300).IsRequired();
            b.Property(x => x.Problem).HasMaxLength(8000).IsRequired();
            b.Property(x => x.Analysis).HasMaxLength(8000).IsRequired();
            b.Property(x => x.Solution).HasMaxLength(8000).IsRequired();
            b.Property(x => x.ProblemRich).HasMaxLength(200000).IsRequired();
            b.Property(x => x.AnalysisRich).HasMaxLength(200000).IsRequired();
            b.Property(x => x.SolutionRich).HasMaxLength(200000).IsRequired();
            b.Property(x => x.AuthorId).HasMaxLength(100).IsRequired();
            b.Property(x => x.State).HasConversion<string>().HasMaxLength(40);
            // Nullable: only a deferred change request has one, and rows deferred before this existed have none.
            b.Property(x => x.DeferredFromState).HasConversion<string>().HasMaxLength(40);
            b.Property(x => x.Type).HasConversion<string>().HasMaxLength(30);
            b.Property(x => x.SoftwareLevel).HasConversion<string>().HasMaxLength(30);
            b.Property(x => x.Version).IsConcurrencyToken();
            b.Ignore(x => x.DisplayNumber); b.Ignore(x => x.ActiveReviewCycle);
            b.HasIndex(x => new { x.ProjectId, x.BaseNumber, x.Revision }).IsUnique();
            b.HasIndex(x => new { x.ProjectId, x.UpdatedAt });
            b.HasIndex(x => new { x.ProjectId, x.State });
            b.HasMany(x => x.RequirementChanges).WithOne().HasForeignKey(x => x.ChangeRequestId).OnDelete(DeleteBehavior.Cascade);
            b.HasMany(x => x.ReviewCycles).WithOne().HasForeignKey(x => x.ChangeRequestId).OnDelete(DeleteBehavior.Cascade);
            b.HasMany(x => x.AuditEvents).WithOne().HasForeignKey(x => x.AggregateId).OnDelete(DeleteBehavior.Cascade);
        });
        modelBuilder.Entity<RequirementChange>(b =>
        {
            b.ToTable("requirement_changes"); b.HasKey(x => x.Id);
            b.Property(x => x.BaseNumber).HasMaxLength(30).IsRequired();
            b.Property(x => x.Level).HasConversion<string>().HasMaxLength(30);
            b.Property(x => x.Kind).HasConversion<string>().HasMaxLength(30);
            b.Property(x => x.Statement).HasMaxLength(8000).IsRequired();
            b.Property(x => x.Rationale).HasMaxLength(4000);
            b.Property(x => x.VerificationMethod).HasMaxLength(100);
            b.Property(x => x.RichText).HasMaxLength(200000);
            b.Property(x => x.AttributesJson).IsRequired();
            b.Property(x => x.ImpactDispositionJson).IsRequired();
            b.Property(x => x.ProposedUpstreamRevisionIdsJson).IsRequired();
            b.Ignore(x => x.DisplayNumber);
            b.HasIndex(x => new { x.ChangeRequestId, x.BaseNumber, x.Revision }).IsUnique();
            b.HasIndex(x => x.BaseNumber);
        });
        modelBuilder.Entity<DownstreamChangeAssessment>(b =>
        {
            b.ToTable("downstream_change_assessments"); b.HasKey(x => x.Id);
            b.Property(x => x.SourceChangeRequestNumber).HasMaxLength(40).IsRequired();
            b.Property(x => x.TargetLevel).HasConversion<string>().HasMaxLength(30);
            b.Property(x => x.State).HasConversion<string>().HasMaxLength(30);
            b.Property(x => x.Outcome).HasConversion<string>().HasMaxLength(30);
            b.Property(x => x.AssignedEngineerId).HasMaxLength(100);
            b.Property(x => x.Rationale).HasMaxLength(4000).IsRequired();
            b.Property(x => x.DecidedBy).HasMaxLength(100);
            b.Property(x => x.SubmittedBy).HasMaxLength(100);
            b.Property(x => x.SelectedApproverId).HasMaxLength(100);
            b.Property(x => x.ApprovedBy).HasMaxLength(100);
            b.Property(x => x.SupersededReason).HasMaxLength(2000).IsRequired();
            b.Property(x => x.Version).IsConcurrencyToken();
            b.HasIndex(x => new { x.SourceChangeRequestId, x.TargetLevel }).IsUnique();
            b.HasIndex(x => new { x.ProjectId, x.ReleaseId, x.TargetLevel, x.State });
            b.HasOne<SystemChangeRequest>().WithMany().HasForeignKey(x => x.SourceChangeRequestId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<DownstreamChangeAssessment>().WithMany().HasForeignKey(x => x.SupersededByAssessmentId).OnDelete(DeleteBehavior.Restrict);
            b.HasMany(x => x.ChangeRequestLinks).WithOne().HasForeignKey(x => x.AssessmentId).OnDelete(DeleteBehavior.Cascade);
            b.Navigation(x => x.ChangeRequestLinks).UsePropertyAccessMode(PropertyAccessMode.Field);
        });
        modelBuilder.Entity<DownstreamAssessmentChangeRequestLink>(b =>
        {
            b.ToTable("downstream_assessment_change_request_links"); b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.ChangeRequestNumber).HasMaxLength(40).IsRequired();
            b.Property(x => x.LinkedBy).HasMaxLength(100).IsRequired();
            b.HasIndex(x => new { x.AssessmentId, x.ChangeRequestId }).IsUnique();
            // Not unique on ChangeRequestId: one SWCR may deliberately answer several upstream assessments.
            b.HasIndex(x => x.ChangeRequestId);
            b.HasOne<SystemChangeRequest>().WithMany().HasForeignKey(x => x.ChangeRequestId).OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<BaselineImport>(b =>
        {
            b.ToTable("baseline_imports"); b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.State).HasConversion<string>().HasMaxLength(30);
            b.Property(x => x.Carries).HasConversion<string>().HasMaxLength(60);
            b.Property(x => x.SourceSystem).HasMaxLength(120).IsRequired();
            b.Property(x => x.SourceSystemVersion).HasMaxLength(60).IsRequired();
            b.Property(x => x.SourceBaselineName).HasMaxLength(200).IsRequired();
            b.Property(x => x.ExtractFileName).HasMaxLength(400).IsRequired();
            b.Property(x => x.ExtractSha256).HasMaxLength(64).IsRequired();
            b.Property(x => x.ExtractedBy).HasMaxLength(100).IsRequired();
            b.Property(x => x.StartedBy).HasMaxLength(100).IsRequired();
            b.Property(x => x.AcceptedBy).HasMaxLength(100);
            b.Property(x => x.Version).IsConcurrencyToken();
            b.HasIndex(x => new { x.ProjectId, x.State });
            b.HasOne<ProjectRecord>().WithMany().HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<SoftwareRelease>().WithMany().HasForeignKey(x => x.ReleaseId).OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<SourceIdentity>(b =>
        {
            b.ToTable("source_identities"); b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.SourceSystem).HasMaxLength(120).IsRequired();
            b.Property(x => x.SourceModule).HasMaxLength(200).IsRequired();
            b.Property(x => x.SourceObjectKey).HasMaxLength(120).IsRequired();
            b.Property(x => x.SourceIdentifier).HasMaxLength(200).IsRequired();
            // The key that makes a re-import a delta: one object in one module of one source system is one
            // identity in this Project, however many extracts mention it.
            b.HasIndex(x => new { x.ProjectId, x.SourceSystem, x.SourceModule, x.SourceObjectKey }).IsUnique();
            // Somebody holding a drawing quotes the identifier, not the key, so that is what they search on.
            b.HasIndex(x => new { x.ProjectId, x.SourceIdentifier });
            b.HasOne<BaselineImport>().WithMany().HasForeignKey(x => x.BaselineImportId).OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<SourceIdentityLink>(b =>
        {
            b.ToTable("source_identity_links"); b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.HasIndex(x => new { x.RequirementRevisionId, x.SourceIdentityId }).IsUnique();
            b.HasIndex(x => x.SourceIdentityId);
            b.HasOne<RequirementRevision>().WithMany().HasForeignKey(x => x.RequirementRevisionId).OnDelete(DeleteBehavior.Cascade);
            b.HasOne<SourceIdentity>().WithMany().HasForeignKey(x => x.SourceIdentityId).OnDelete(DeleteBehavior.Cascade);
            b.HasOne<BaselineImport>().WithMany().HasForeignKey(x => x.BaselineImportId).OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<SourceHistoryEntry>(b =>
        {
            b.ToTable("source_history_entries"); b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.SourceBaselineName).HasMaxLength(200).IsRequired();
            b.Property(x => x.ChangedBy).HasMaxLength(200).IsRequired();
            b.Property(x => x.SourceChangeReference).HasMaxLength(200).IsRequired();
            b.HasIndex(x => new { x.SourceIdentityId, x.SourceBaselineName });
            b.HasOne<SourceIdentity>().WithMany().HasForeignKey(x => x.SourceIdentityId).OnDelete(DeleteBehavior.Cascade);
            b.HasOne<BaselineImport>().WithMany().HasForeignKey(x => x.BaselineImportId).OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<DownstreamAssessmentReopening>(b =>
        {
            b.ToTable("downstream_assessment_reopenings"); b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.PreviousState).HasConversion<string>().HasMaxLength(30);
            b.Property(x => x.PreviousOutcome).HasConversion<string>().HasMaxLength(30);
            b.Property(x => x.PreviousRationale).HasMaxLength(4000).IsRequired();
            b.Property(x => x.PreviousDecidedBy).HasMaxLength(100);
            b.Property(x => x.PreviousApprovedBy).HasMaxLength(100);
            b.Property(x => x.DetachedChangeRequestNumbers).HasMaxLength(2000).IsRequired();
            b.Property(x => x.Reason).HasMaxLength(4000).IsRequired();
            b.Property(x => x.ActorId).HasMaxLength(100).IsRequired();
            b.HasIndex(x => x.AssessmentId);
            b.HasOne<DownstreamChangeAssessment>().WithMany().HasForeignKey(x => x.AssessmentId).OnDelete(DeleteBehavior.Cascade);
        });
        modelBuilder.Entity<ReviewCycle>(b =>
        {
            b.ToTable("review_cycles"); b.HasKey(x => x.Id);
            b.Property(x => x.SnapshotHash).HasMaxLength(64).IsRequired();
            b.Property(x => x.State).HasConversion<string>().HasMaxLength(40);
            b.Property(x => x.Mode).HasConversion<string>().HasMaxLength(20);
            b.Property(x => x.ClosureReason).HasMaxLength(2000);
            b.Property(x => x.WorkflowName).HasMaxLength(200);
            b.Ignore(x => x.ActivePosition);
            b.Ignore(x => x.OwnerId);
            b.HasIndex(x => new { x.ChangeRequestId, x.Sequence }).IsUnique();
            b.HasIndex(x => new { x.TestChangeReviewId, x.Sequence }).IsUnique();
            // Both owners keep a real foreign key, and a check constraint enforces that exactly one is set.
            // A single untyped owner column would have been simpler and would have given up the database's
            // guarantee that a review cycle points at something that exists — which is not a trade this
            // product should make, since its whole claim is that its records are provable.
            b.HasOne<TestChangeReview>().WithMany(x => x.ReviewCycles).HasForeignKey(x => x.TestChangeReviewId)
                .OnDelete(DeleteBehavior.Cascade);
            b.ToTable(t => t.HasCheckConstraint("CK_review_cycles_one_owner",
                "(\"ChangeRequestId\" IS NULL) <> (\"TestChangeReviewId\" IS NULL)"));
            b.HasMany(x => x.Steps).WithOne().HasForeignKey(x => x.ReviewCycleId).OnDelete(DeleteBehavior.Cascade);
            b.HasMany(x => x.Comments).WithOne().HasForeignKey(x => x.ReviewCycleId).OnDelete(DeleteBehavior.Cascade);
        });
        modelBuilder.Entity<ReviewComment>(b =>
        {
            b.ToTable("review_comments"); b.HasKey(x => x.Id);
            b.Property(x => x.AuthorId).HasMaxLength(100).IsRequired();
            b.Property(x => x.Body).HasMaxLength(4000).IsRequired();
            b.Property(x => x.Anchor).HasConversion<string>().HasMaxLength(30);
            b.Property(x => x.State).HasConversion<string>().HasMaxLength(20);
            b.Property(x => x.SectionLabel).HasMaxLength(200);
            // Reading a review's comments, and reading one reviewer's own drafts within it, are the only two
            // access paths there are — one index per owner kind.
            b.HasIndex(x => new { x.ReviewCycleId, x.State });
            b.HasIndex(x => new { x.ReviewCycleId, x.AuthorId });
            b.HasIndex(x => new { x.ManagedDocumentRevisionId, x.DocumentCycle, x.State });
            // The anchor and its identifier must agree in the database too, not only in the constructor.
            // A row that says RequirementRevision with no revision is unreadable, and nothing downstream
            // could tell whether it had been written that way or corrupted later.
            b.ToTable(t => t.HasCheckConstraint("CK_review_comments_anchor",
                "(\"Anchor\" = 'RequirementRevision') = (\"RequirementChangeId\" IS NOT NULL)"));
            // Exactly one owner, enforced by the database rather than trusted from the constructors, for the
            // same reason ReviewCycle enforces it: a comment that belongs to nothing would outlive anything
            // that could explain it, and no reader could tell whether it was written that way or orphaned.
            b.ToTable(t => t.HasCheckConstraint("CK_review_comments_one_owner",
                "(\"ReviewCycleId\" IS NULL) <> (\"ManagedDocumentRevisionId\" IS NULL)"));
            // A document comment is scoped to the round it was written in; a change request comment gets that
            // from its cycle. Set exactly when the document owner is.
            b.ToTable(t => t.HasCheckConstraint("CK_review_comments_document_cycle",
                "(\"ManagedDocumentRevisionId\" IS NULL) = (\"DocumentCycle\" IS NULL)"));
        });
        modelBuilder.Entity<ApprovalStep>(b =>
        {
            b.ToTable("approval_steps"); b.HasKey(x => x.Id);
            b.Property(x => x.ApproverId).HasMaxLength(100).IsRequired();
            b.Property(x => x.ApproverName).HasMaxLength(200).IsRequired();
            b.Property(x => x.StageName).HasMaxLength(120);
            b.Property(x => x.StageKind).HasConversion<string>().HasMaxLength(20).HasDefaultValue(ReviewStageKind.Review);
            b.Property(x => x.Authority).HasMaxLength(40);
            b.Property(x => x.Rationale).HasMaxLength(4000);
            b.Property(x => x.State).HasConversion<string>().HasMaxLength(30);
            b.HasIndex(x => new { x.ReviewCycleId, x.Position }).IsUnique();
        });
        modelBuilder.Entity<IdentifierSequence>(b =>
        {
            b.ToTable("identifier_sequences"); b.HasKey(x => x.Id);
            b.Property(x => x.Scope).HasMaxLength(80).IsRequired();
            // The unique scope is what makes a claim a single-row contest, and the concurrency stamp is what
            // makes two simultaneous claims resolve to two different numbers rather than the same one.
            b.HasIndex(x => x.Scope).IsUnique();
            b.Property(x => x.ConcurrencyStamp).IsConcurrencyToken();
        });

        modelBuilder.Entity<AuditEvent>(b =>
        {
            b.ToTable("audit_events"); b.HasKey(x => x.Id);
            b.Property(x => x.EventType).HasMaxLength(100).IsRequired();
            b.Property(x => x.ActorId).HasMaxLength(100).IsRequired();
            // Evidence is unbounded JSON by nature; the summary is a sentence and is capped so it cannot
            // quietly become a payload again.
            b.Property(x => x.Detail).HasMaxLength(1000);
            b.Property(x => x.EvidenceJson);
            b.Property(x => x.Detail).HasMaxLength(4000).IsRequired();
            b.HasIndex(x => new { x.AggregateId, x.OccurredAt });
        });
        modelBuilder.Entity<CandidateBaseline>(b =>
        {
            b.ToTable("candidate_baselines"); b.HasKey(x => x.Id);
            b.Property(x => x.BaseNumber).HasMaxLength(30).IsRequired();
            b.Property(x => x.Name).HasMaxLength(300).IsRequired();
            b.Property(x => x.State).HasConversion<string>().HasMaxLength(30);
            b.Property(x => x.ContentHash).HasMaxLength(64);
            b.Property(x => x.RequirementsHash).HasMaxLength(64);
            b.Property(x => x.TestProceduresHash).HasMaxLength(64);
            b.Property(x => x.Version).IsConcurrencyToken();
            b.Ignore(x => x.DisplayNumber);
            b.HasIndex(x => new { x.ProjectId, x.BaseNumber, x.Revision }).IsUnique();
            b.HasIndex(x => new { x.ProjectId, x.ReleaseId, x.State });
            b.HasMany(x => x.Selections).WithOne().HasForeignKey(x => x.BaselineId).OnDelete(DeleteBehavior.Cascade);
            b.HasMany(x => x.TestChangeSelections).WithOne().HasForeignKey(x => x.BaselineId).OnDelete(DeleteBehavior.Cascade);
            b.HasMany(x => x.Events).WithOne().HasForeignKey(x => x.BaselineId).OnDelete(DeleteBehavior.Cascade);
        });
        modelBuilder.Entity<BaselineChangeRequestSelection>(b =>
        {
            b.ToTable("baseline_change_request_selections"); b.HasKey(x => x.Id);
            b.Property(x => x.ChangeRequestDisplayNumber).HasMaxLength(40).IsRequired();
            b.HasIndex(x => new { x.BaselineId, x.ChangeRequestId }).IsUnique();
        });
        modelBuilder.Entity<BaselineTestChangeRequestSelection>(b =>
        {
            b.ToTable("baseline_test_change_request_selections"); b.HasKey(x => x.Id);
            // Unlike a change request, a test change request is selected after the baseline is already saved, so
            // this collection grows on a tracked parent. The identifier is set in the constructor, and without
            // this EF reads the new row as an existing one to update — which affects nothing and throws.
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.TestChangeRequestDisplayNumber).HasMaxLength(40).IsRequired();
            b.HasIndex(x => new { x.BaselineId, x.TestChangeRequestId }).IsUnique();
        });
        modelBuilder.Entity<BaselineEvent>(b =>
        {
            b.ToTable("baseline_events"); b.HasKey(x => x.Id);
            b.Property(x => x.EventType).HasMaxLength(100).IsRequired();
            b.Property(x => x.ActorId).HasMaxLength(100).IsRequired();
            b.Property(x => x.Detail).HasMaxLength(4000).IsRequired();
            b.HasIndex(x => new { x.BaselineId, x.OccurredAt });
        });
        modelBuilder.Entity<RequirementArtifact>(b =>
        {
            b.ToTable("requirements"); b.HasKey(x => x.Id);
            b.Property(x => x.BaseNumber).HasMaxLength(30).IsRequired();
            b.Property(x => x.Level).HasConversion<string>().HasMaxLength(30);
            b.HasIndex(x => new { x.ProjectId, x.BaseNumber }).IsUnique();
            b.HasOne<ProjectRecord>().WithMany().HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<RequirementRevision>(b =>
        {
            b.ToTable("requirement_revisions"); b.HasKey(x => x.Id);
            b.Property(x => x.Statement).HasMaxLength(8000).IsRequired();
            b.Property(x => x.Rationale).HasMaxLength(4000);
            b.Property(x => x.VerificationMethod).HasMaxLength(100);
            b.Property(x => x.State).HasConversion<string>().HasMaxLength(30);
            b.HasIndex(x => new { x.ArtifactId, x.Revision }).IsUnique();
            b.HasIndex(x => x.SourceChangeRequestId); b.HasIndex(x => x.EffectiveBaselineId);
            b.HasOne<RequirementArtifact>().WithMany().HasForeignKey(x => x.ArtifactId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<SystemChangeRequest>().WithMany().HasForeignKey(x => x.SourceChangeRequestId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<CandidateBaseline>().WithMany().HasForeignKey(x => x.EffectiveBaselineId).OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<BaselineRequirementSelection>(b =>
        {
            b.ToTable("baseline_requirement_selections"); b.HasKey(x => x.Id);
            b.HasIndex(x => new { x.BaselineId, x.ArtifactId }).IsUnique();
            b.HasIndex(x => new { x.BaselineId, x.RevisionId }).IsUnique();
            b.HasOne<CandidateBaseline>().WithMany().HasForeignKey(x => x.BaselineId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<RequirementArtifact>().WithMany().HasForeignKey(x => x.ArtifactId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<RequirementRevision>().WithMany().HasForeignKey(x => x.RevisionId).OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<TestProcedure>(b =>
        {
            b.ToTable("test_procedures"); b.HasKey(x => x.Id); b.Property(x => x.BaseNumber).HasMaxLength(30).IsRequired();
            b.Property(x => x.Title).HasMaxLength(300).IsRequired(); b.Property(x => x.OwnerId).HasMaxLength(100).IsRequired();
            b.Property(x => x.Level).HasConversion<string>().HasMaxLength(30);
            b.Property(x => x.Version).IsConcurrencyToken();
            b.HasIndex(x => new { x.ProjectId, x.BaseNumber }).IsUnique();
            b.HasOne<ProjectRecord>().WithMany().HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<TestProcedureRevision>(b =>
        {
            b.ToTable("test_procedure_revisions"); b.HasKey(x => x.Id); b.Property(x => x.Objective).HasMaxLength(4000).IsRequired();
            b.Property(x => x.Preconditions).HasMaxLength(8000); b.Property(x => x.Steps).HasMaxLength(16000).IsRequired();
            b.Property(x => x.ExpectedResult).HasMaxLength(8000).IsRequired(); b.Property(x => x.State).HasConversion<string>().HasMaxLength(30);
            b.Property(x => x.AuthorId).HasMaxLength(100).IsRequired(); b.Property(x => x.SelectedApproverId).HasMaxLength(100); b.HasIndex(x => new { x.ProcedureId, x.Revision }).IsUnique();
            b.Property(x => x.SourceChangeRequestsJson).IsRequired().HasDefaultValue("[]");
            b.HasIndex(x => x.SourceTestChangeRequestId);
            b.HasOne<TestProcedure>().WithMany().HasForeignKey(x => x.ProcedureId).OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<BaselineTestProcedureSelection>(b =>
        {
            b.ToTable("baseline_test_procedures"); b.HasKey(x => x.Id);
            b.HasIndex(x => new { x.BaselineId, x.ProcedureId }).IsUnique();
            b.HasIndex(x => x.RevisionId);
        });
        modelBuilder.Entity<TestRequirementCoverage>(b =>
        {
            b.ToTable("test_requirement_coverage"); b.HasKey(x => x.Id);
            b.Property(x => x.SuspectReason).HasMaxLength(500).IsRequired();
            b.Property(x => x.ConfirmedBy).HasMaxLength(100);
            b.HasIndex(x => x.IsSuspect);
            b.HasIndex(x => new { x.ProcedureRevisionId, x.RequirementRevisionId }).IsUnique(); b.HasIndex(x => x.RequirementRevisionId);
            b.HasOne<TestProcedureRevision>().WithMany().HasForeignKey(x => x.ProcedureRevisionId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<RequirementRevision>().WithMany().HasForeignKey(x => x.RequirementRevisionId).OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<BuildTestSet>(b =>
        {
            b.ToTable("build_test_sets"); b.HasKey(x => x.Id);
            b.Property(x => x.Discipline).HasConversion<string>().HasMaxLength(40);
            b.Property(x => x.Version).IsConcurrencyToken();
            // One set per build per discipline. Two would mean two answers to "what is this build running",
            // and the release gate would have to guess which one it is measured against.
            b.HasIndex(x => new { x.ReleaseId, x.Discipline }).IsUnique();
            b.HasMany(x => x.Entries).WithOne().HasForeignKey(x => x.BuildTestSetId).OnDelete(DeleteBehavior.Cascade);
            b.Navigation(x => x.Entries).UsePropertyAccessMode(PropertyAccessMode.Field);
            b.HasOne<ProjectRecord>().WithMany().HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<SoftwareRelease>().WithMany().HasForeignKey(x => x.ReleaseId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<BuildTestSetEntry>(b =>
        {
            b.ToTable("build_test_set_entries"); b.HasKey(x => x.Id);
            // Set in the constructor, not by the store. Without this an entry added to a set that is already
            // tracked arrives through the navigation with its key set and EF reads it as a row to update.
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.Reason).HasConversion<string>().HasMaxLength(40);
            b.Property(x => x.Note).HasMaxLength(1000).IsRequired();
            b.Property(x => x.AddedBy).HasMaxLength(100).IsRequired();
            b.HasIndex(x => new { x.BuildTestSetId, x.ProcedureRevisionId }).IsUnique();
            b.HasOne<TestProcedureRevision>().WithMany().HasForeignKey(x => x.ProcedureRevisionId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<TestProcedureChange>(b =>
        {
            b.ToTable("test_procedure_changes"); b.HasKey(x => x.Id);
            // Same reason as the claim below: the identifier is set in the constructor, so without this EF
            // reads a change added to an already-tracked test change request as an existing row to update.
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.BaseNumber).HasMaxLength(40).IsRequired();
            b.Property(x => x.Title).HasMaxLength(300).IsRequired();
            b.Property(x => x.Level).HasConversion<string>().HasMaxLength(30);
            b.Property(x => x.Kind).HasConversion<string>().HasMaxLength(30);
            b.Property(x => x.RemovedRequirementRevisionIdsJson).IsRequired();
            b.Property(x => x.CoverageChangeRationale).HasMaxLength(2000).IsRequired();
            b.Property(x => x.CoverageChangedBy).HasMaxLength(100).IsRequired();
            b.HasIndex(x => new { x.TestChangeReviewId, x.BaseNumber }).IsUnique();
            b.Ignore(x => x.DisplayNumber);
        });
        modelBuilder.Entity<TestChangeRequestClaim>(b =>
        {
            b.ToTable("test_change_request_claims"); b.HasKey(x => x.Id);
            // The identifier comes from the constructor, not the store. Without this, a claim added to a
            // package that is already tracked is discovered through the navigation with its key already set,
            // and EF reads that as an existing row to update — which then updates nothing, because there is
            // no such row. Every other child collection here is only ever built alongside a brand new parent,
            // so this is the first place it could bite.
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.ChangeRequestNumber).HasMaxLength(40).IsRequired();
            b.Property(x => x.ClaimedBy).HasMaxLength(100).IsRequired();
            // The rule the database holds rather than the aggregate: a change request belongs to at most one
            // test change request. Two engineers claiming the same change from two packages would otherwise
            // both succeed, and "is this change's test work covered?" would have two answers that could be
            // approved with contradictory procedure decisions.
            b.HasIndex(x => x.ChangeRequestId).IsUnique();
            b.HasIndex(x => x.TestChangeReviewId);
        });

        modelBuilder.Entity<TestChangeReview>(b =>
        {
            b.ToTable("test_change_reviews"); b.HasKey(x => x.Id);
            b.Property(x => x.Discipline).HasConversion<string>().HasMaxLength(40);
            b.Property(x => x.Outcome).HasConversion<string>().HasMaxLength(30);
            // Deferral, the same shape the requirements side stores it in: where the work sits and how far it
            // had got are two different facts, so the origin state is kept rather than inferred on the way back.
            b.Property(x => x.DeferredFromState).HasConversion<string>().HasMaxLength(30);
            b.Property(x => x.DeferralReason).HasMaxLength(2000).IsRequired();
            // Empty where no person raised it — see TestChangeReview.AuthorId. Required as a column so the
            // register never has to distinguish "nobody" from "not stored".
            b.Property(x => x.AuthorId).HasMaxLength(100).IsRequired();
            // Owned the way a change request owns its requirement changes: a proposed procedure change has
            // no life outside the test change request proposing it.
            b.HasMany(x => x.ProcedureChanges).WithOne().HasForeignKey(x => x.TestChangeReviewId)
                .OnDelete(DeleteBehavior.Cascade);
            b.Navigation(x => x.ProcedureChanges).UsePropertyAccessMode(PropertyAccessMode.Field);
            // Required as a string but empty when the package was raised from a Problem Report instead —
            // the column keeps its shape, and which origin a package has is told by the two id columns.
            b.Property(x => x.SourceChangeRequestNumber).HasMaxLength(40).IsRequired();
            b.Property(x => x.SourceProblemReportNumber).HasMaxLength(40).IsRequired();
            b.HasIndex(x => x.OriginatingProblemReportId);
            b.Property(x => x.BaseNumber).HasMaxLength(40).IsRequired();
            // The case the package argues, sized as a change request's own. Empty on packages raised before
            // the fields existed and on those raised automatically, which nobody has written up yet.
            b.Property(x => x.Title).HasMaxLength(300).IsRequired();
            b.Property(x => x.Problem).HasMaxLength(8000).IsRequired();
            b.Property(x => x.Analysis).HasMaxLength(8000).IsRequired();
            b.Property(x => x.Solution).HasMaxLength(8000).IsRequired();
            b.Property(x => x.ProblemRich).IsRequired();
            b.Property(x => x.AnalysisRich).IsRequired();
            b.Property(x => x.SolutionRich).IsRequired();
            b.Property(x => x.CaseContractVersion).IsRequired();
            b.Navigation(x => x.ReviewCycles).UsePropertyAccessMode(PropertyAccessMode.Field);
            b.Property(x => x.SelectedApproverId).HasMaxLength(100);
            b.Ignore(x => x.DisplayNumber);
            b.HasMany(x => x.AdditionalSources).WithOne().HasForeignKey(x => x.TestChangeReviewId).OnDelete(DeleteBehavior.Cascade);
            b.Navigation(x => x.AdditionalSources).UsePropertyAccessMode(PropertyAccessMode.Field);
            b.Property(x => x.State).HasConversion<string>().HasMaxLength(30);
            b.Property(x => x.AssignedEngineerId).HasMaxLength(100);
            b.Property(x => x.SubmittedBy).HasMaxLength(100);
            b.Property(x => x.ApprovedBy).HasMaxLength(100);
            b.Property(x => x.ApprovalRationale).HasMaxLength(4000).IsRequired();
            b.Property(x => x.SupersededReason).HasMaxLength(2000).IsRequired();
            b.Property(x => x.Version).IsConcurrencyToken();
            // Revision belongs in the key for the same reason it does on a change request: .01 is a further
            // revision of one package, not a second package claiming the same change. Without it the exclusivity
            // rule and the revision chain contradict each other, and the revision simply cannot be stored.
            b.HasIndex(x => new { x.ChangeRequestId, x.Discipline, x.Revision }).IsUnique();
            b.HasIndex(x => new { x.ReleaseId, x.State, x.Discipline });
            b.HasOne<ProjectRecord>().WithMany().HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<SoftwareRelease>().WithMany().HasForeignKey(x => x.ReleaseId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<SystemChangeRequest>().WithMany().HasForeignKey(x => x.ChangeRequestId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<TestChangeReview>().WithMany().HasForeignKey(x => x.SupersededByTestChangeRequestId).OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<VerificationImpactItem>(b =>
        {
            b.ToTable("verification_impact_items"); b.HasKey(x => x.Id);
            b.Property(x => x.Trigger).HasConversion<string>().HasMaxLength(40);
            b.Property(x => x.State).HasConversion<string>().HasMaxLength(30);
            b.Property(x => x.Outcome).HasConversion<string>().HasMaxLength(40);
            b.Property(x => x.ProcedureChangeAction).HasConversion<string>().HasMaxLength(40);
            b.Property(x => x.SubjectDisplayNumber).HasMaxLength(80).IsRequired();
            b.Property(x => x.DeclaredVerificationMethod).HasMaxLength(120).IsRequired();
            b.Property(x => x.AssignedEngineerId).HasMaxLength(100);
            b.Property(x => x.AssignedByLeadId).HasMaxLength(100);
            b.Property(x => x.ResolutionRationale).HasMaxLength(4000).IsRequired();
            b.Property(x => x.ResolvedBy).HasMaxLength(100);
            b.Property(x => x.Version).IsConcurrencyToken();
            // The gate query is "unresolved items for this release", so it leads the index.
            b.HasIndex(x => new { x.ReleaseId, x.State });
            b.HasIndex(x => x.ChangeRequestId);
            b.HasIndex(x => x.TestChangeReviewId);
            b.HasIndex(x => x.RequirementChangeId);
            b.HasIndex(x => x.RequirementRevisionId);
            b.HasIndex(x => x.AssignedEngineerId);
            b.HasOne<ProjectRecord>().WithMany().HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<SystemChangeRequest>().WithMany().HasForeignKey(x => x.ChangeRequestId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<TestChangeReview>().WithMany().HasForeignKey(x => x.TestChangeReviewId).OnDelete(DeleteBehavior.Cascade);
            b.HasOne<TestProcedure>().WithMany().HasForeignKey(x => x.ResolvedProcedureId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<TestProcedureRevision>().WithMany().HasForeignKey(x => x.ResolvedProcedureRevisionId)
                .OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<VerificationImpactDecisionHistory>(b =>
        {
            b.ToTable("verification_impact_decision_history"); b.HasKey(x => x.Id);
            b.Property(x => x.Action).HasConversion<string>().HasMaxLength(30);
            b.Property(x => x.Outcome).HasConversion<string>().HasMaxLength(40);
            b.Property(x => x.Rationale).HasMaxLength(4000).IsRequired();
            b.Property(x => x.ActorId).HasMaxLength(100).IsRequired();
            b.HasIndex(x => new { x.VerificationImpactItemId, x.OccurredAt });
            b.HasOne<VerificationImpactItem>().WithMany().HasForeignKey(x => x.VerificationImpactItemId)
                .OnDelete(DeleteBehavior.Restrict);
            b.HasOne<TestProcedure>().WithMany().HasForeignKey(x => x.ProcedureId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<TestProcedureRevision>().WithMany().HasForeignKey(x => x.ProcedureRevisionId)
                .OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<TestExecution>(b =>
        {
            b.ToTable("test_executions"); b.HasKey(x => x.Id); b.Property(x => x.Outcome).HasConversion<string>().HasMaxLength(30);
            b.Property(x => x.ExecutedBy).HasMaxLength(100).IsRequired(); b.Property(x => x.Configuration).HasMaxLength(4000);
            b.Property(x => x.Determination).HasMaxLength(8000).IsRequired(); b.Property(x => x.EvidenceReference).HasMaxLength(2000);
            b.HasIndex(x => new { x.ProjectId, x.ReleaseId, x.ExecutedAt }); b.HasIndex(x => x.ProcedureRevisionId); b.HasIndex(x => x.SoftwareBuildId);
            b.HasOne<ProjectRecord>().WithMany().HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<SoftwareRelease>().WithMany().HasForeignKey(x => x.ReleaseId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<TestProcedureRevision>().WithMany().HasForeignKey(x => x.ProcedureRevisionId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<SoftwareBuild>().WithMany().HasForeignKey(x => x.SoftwareBuildId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<TestExecution>().WithMany().HasForeignKey(x => x.RetestOfExecutionId).OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<RequirementTraceLink>(b =>
        {
            b.ToTable("requirement_trace_links"); b.HasKey(x => x.Id); b.Property(x => x.Type).HasConversion<string>().HasMaxLength(30);
            b.Property(x => x.Rationale).HasMaxLength(2000); b.Property(x => x.Version).IsConcurrencyToken(); b.HasIndex(x => new { x.SourceRevisionId, x.TargetRevisionId, x.Type }).IsUnique();
            b.HasIndex(x => x.TargetRevisionId); b.HasIndex(x => x.ProjectId);
            b.HasOne<ProjectRecord>().WithMany().HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<RequirementRevision>().WithMany().HasForeignKey(x => x.SourceRevisionId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<RequirementRevision>().WithMany().HasForeignKey(x => x.TargetRevisionId).OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<ControlledDocument>(b =>
        {
            b.ToTable("controlled_documents"); b.HasKey(x => x.Id); b.Property(x => x.Type).HasConversion<string>().HasMaxLength(40);
            b.Property(x => x.DocumentNumber).HasMaxLength(30).IsRequired(); b.Property(x => x.Title).HasMaxLength(300).IsRequired();
            b.Property(x => x.ContentHash).HasMaxLength(64).IsRequired(); b.HasIndex(x => new { x.ProjectId, x.DocumentNumber, x.Revision }).IsUnique();
            b.HasIndex(x => new { x.BaselineId, x.Type });
            b.HasOne<ProjectRecord>().WithMany().HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<SoftwareRelease>().WithMany().HasForeignKey(x => x.ReleaseId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<CandidateBaseline>().WithMany().HasForeignKey(x => x.BaselineId).OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<ControlledDocumentArtifact>(b =>
        {
            b.ToTable("controlled_document_artifacts"); b.HasKey(x => x.Id);
            b.Property(x => x.Format).HasMaxLength(8).IsRequired(); b.Property(x => x.StorageKey).HasMaxLength(500).IsRequired();
            b.Property(x => x.OriginalFileName).HasMaxLength(260).IsRequired(); b.Property(x => x.ContentType).HasMaxLength(200);
            b.Property(x => x.Sha256).HasMaxLength(64).IsRequired(); b.HasIndex(x => new { x.DocumentId, x.Format }).IsUnique();
            b.HasOne<ControlledDocument>().WithMany().HasForeignKey(x => x.DocumentId).OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<ReleaseCampaign>(b =>
        {
            b.ToTable("release_campaigns"); b.HasKey(x => x.Id); b.Property(x => x.Name).HasMaxLength(300).IsRequired(); b.Property(x => x.OwnerId).HasMaxLength(100).IsRequired();
            b.Property(x => x.State).HasConversion<string>().HasMaxLength(30); b.Property(x => x.ReleaseHash).HasMaxLength(64); b.Property(x => x.Version).IsConcurrencyToken(); b.HasIndex(x => new { x.ProjectId, x.ReleaseId }).IsUnique();
            b.HasOne<ProjectRecord>().WithMany().HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Restrict); b.HasOne<SoftwareRelease>().WithMany().HasForeignKey(x => x.ReleaseId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<CandidateBaseline>().WithMany().HasForeignKey(x => x.BaselineId).OnDelete(DeleteBehavior.Restrict); b.HasOne<SoftwareBuild>().WithMany().HasForeignKey(x => x.SoftwareBuildId).OnDelete(DeleteBehavior.Restrict);
            b.HasMany(x => x.Approvals).WithOne().HasForeignKey(x => x.CampaignId).OnDelete(DeleteBehavior.Restrict); b.HasMany(x => x.Events).WithOne().HasForeignKey(x => x.CampaignId).OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<ReleaseApproval>(b =>
        {
            b.ToTable("release_approvals"); b.HasKey(x => x.Id); b.Property(x => x.ApproverId).HasMaxLength(100).IsRequired(); b.Property(x => x.ApproverName).HasMaxLength(200).IsRequired(); b.Property(x => x.State).HasConversion<string>().HasMaxLength(30); b.Property(x => x.Cycle); b.HasIndex(x => new { x.CampaignId, x.Cycle, x.Position }).IsUnique();
        });
        modelBuilder.Entity<ReleaseCampaignEvent>(b =>
        {
            b.ToTable("release_campaign_events"); b.HasKey(x => x.Id); b.Property(x => x.EventType).HasMaxLength(100).IsRequired(); b.Property(x => x.ActorId).HasMaxLength(100).IsRequired(); b.Property(x => x.Detail).HasMaxLength(4000).IsRequired(); b.HasIndex(x => new { x.CampaignId, x.OccurredAt });
        });
        modelBuilder.Entity<ChangeImpactDisposition>(b =>
        {
            b.ToTable("change_impact_dispositions"); b.HasKey(x => x.Id); b.Property(x => x.Kind).HasConversion<string>().HasMaxLength(30); b.Property(x => x.State).HasConversion<string>().HasMaxLength(30);
            b.Property(x => x.ArtifactReference).HasMaxLength(100).IsRequired(); b.Property(x => x.Description).HasMaxLength(2000).IsRequired(); b.Property(x => x.Rationale).HasMaxLength(2000); b.Property(x => x.DispositionedBy).HasMaxLength(100);
            b.HasIndex(x => new { x.CampaignId, x.ChangeRequestId, x.Kind, x.ArtifactReference }).IsUnique(); b.HasOne<ReleaseCampaign>().WithMany().HasForeignKey(x => x.CampaignId).OnDelete(DeleteBehavior.Restrict); b.HasOne<SystemChangeRequest>().WithMany().HasForeignKey(x => x.ChangeRequestId).OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<EvidenceRecord>(b =>
        {
            b.ToTable("evidence_records"); b.HasKey(x => x.Id); b.Property(x => x.OriginalFileName).HasMaxLength(260).IsRequired(); b.Property(x => x.ContentType).HasMaxLength(200); b.Property(x => x.Sha256).HasMaxLength(64).IsRequired(); b.Property(x => x.StorageKey).HasMaxLength(500).IsRequired(); b.Property(x => x.UploadedBy).HasMaxLength(100).IsRequired();
            b.HasIndex(x => new { x.ProjectId, x.Sha256 }); b.HasOne<ProjectRecord>().WithMany().HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<TestExecutionEvidence>(b =>
        {
            b.ToTable("test_execution_evidence"); b.HasKey(x => x.Id); b.HasIndex(x => new { x.TestExecutionId, x.EvidenceId }).IsUnique();
            b.HasOne<TestExecution>().WithMany().HasForeignKey(x => x.TestExecutionId).OnDelete(DeleteBehavior.Restrict); b.HasOne<EvidenceRecord>().WithMany().HasForeignKey(x => x.EvidenceId).OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<UserAccount>(b =>
        {
            b.ToTable("user_accounts"); b.HasKey(x => x.Id); b.Property(x => x.UserName).HasMaxLength(100).IsRequired(); b.Property(x => x.DisplayName).HasMaxLength(200).IsRequired();
            b.Property(x => x.Email).HasMaxLength(320); b.Property(x => x.PasswordHash).HasMaxLength(500).IsRequired(); b.Property(x => x.State).HasConversion<string>().HasMaxLength(30); b.HasIndex(x => x.UserName).IsUnique(); b.HasIndex(x => x.Email);
        });
        modelBuilder.Entity<ProgramMembership>(b =>
        {
            b.ToTable("program_memberships"); b.HasKey(x => x.Id); b.Property(x => x.Role).HasConversion<string>().HasMaxLength(40); b.Property(x => x.GrantedBy).HasMaxLength(100).IsRequired();
            b.Property(x => x.EndedBy).HasMaxLength(100).IsRequired();
            // Unique among current memberships only. Ended rows are kept as history, and somebody who left and
            // came back would collide with their own past on an unfiltered unique index.
            b.HasIndex(x => new { x.UserId, x.ProgramId, x.Role }).IsUnique().HasFilter("\"EndedAt\" IS NULL");
            b.HasIndex(x => new { x.ProgramId, x.EndedAt });
            b.HasOne<UserAccount>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict); b.HasOne<ProgramRecord>().WithMany().HasForeignKey(x => x.ProgramId).OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<ProjectRoleBackup>(b =>
        {
            b.ToTable("project_role_backups"); b.HasKey(x => x.Id);
            b.Property(x => x.Role).HasConversion<string>().HasMaxLength(40);
            b.Property(x => x.NamedBy).HasMaxLength(100).IsRequired();
            b.Property(x => x.RemovedBy).HasMaxLength(100).IsRequired();
            // One standing backup per role per project, among those still standing.
            b.HasIndex(x => new { x.ProgramId, x.Role }).IsUnique().HasFilter("\"RemovedAt\" IS NULL");
            b.HasIndex(x => new { x.ProgramId, x.BackupUserId, x.RemovedAt });
            b.HasOne<UserAccount>().WithMany().HasForeignKey(x => x.BackupUserId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<ProgramRecord>().WithMany().HasForeignKey(x => x.ProgramId).OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<UserMfaEnrollment>(b=>{b.ToTable("user_mfa_enrollments");b.HasKey(x=>x.Id);b.Property(x=>x.Secret).HasMaxLength(512).IsRequired();b.Property(x=>x.CreatedBy).HasMaxLength(100).IsRequired();b.HasIndex(x=>x.UserId).IsUnique();b.HasOne<UserAccount>().WithMany().HasForeignKey(x=>x.UserId).OnDelete(DeleteBehavior.Cascade);});
        modelBuilder.Entity<MfaRecoveryCode>(b=>{b.ToTable("mfa_recovery_codes");b.HasKey(x=>x.Id);b.Property(x=>x.CodeHash).HasMaxLength(64).IsRequired();b.HasIndex(x=>new{x.UserId,x.CodeHash}).IsUnique();b.HasOne<UserAccount>().WithMany().HasForeignKey(x=>x.UserId).OnDelete(DeleteBehavior.Cascade);});
        modelBuilder.Entity<UserSession>(b =>
        {
            b.ToTable("user_sessions"); b.HasKey(x => x.Id); b.Property(x => x.TokenHash).HasMaxLength(64).IsRequired(); b.Property(x => x.IpAddress).HasMaxLength(100); b.Property(x => x.UserAgent).HasMaxLength(500); b.HasIndex(x => x.TokenHash).IsUnique(); b.HasIndex(x => new { x.UserId, x.ExpiresAt });
            b.HasOne<UserAccount>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<RoleDelegation>(b =>
        {
            b.ToTable("role_delegations"); b.HasKey(x => x.Id); b.Property(x => x.Role).HasConversion<string>().HasMaxLength(40); b.Property(x => x.Reason).HasMaxLength(2000).IsRequired(); b.Property(x => x.CreatedBy).HasMaxLength(100).IsRequired(); b.HasIndex(x => new { x.ProgramId, x.DelegateUserId, x.EndsAt });
        });
        modelBuilder.Entity<ElectronicSignature>(b =>
        {
            b.ToTable("electronic_signatures"); b.HasKey(x => x.Id); b.Property(x => x.UserName).HasMaxLength(100).IsRequired(); b.Property(x => x.DisplayName).HasMaxLength(200).IsRequired(); b.Property(x => x.ArtifactType).HasMaxLength(60).IsRequired(); b.Property(x => x.ArtifactRevision).HasMaxLength(80).IsRequired(); b.Property(x => x.Action).HasMaxLength(80).IsRequired(); b.Property(x => x.Authority).HasMaxLength(80).IsRequired(); b.Property(x => x.AuthoritySource).HasMaxLength(80).IsRequired(); b.Property(x => x.Meaning).HasMaxLength(1000).IsRequired(); b.Property(x => x.Rationale).HasMaxLength(4000).IsRequired(); b.Property(x => x.ContentHash).HasMaxLength(64).IsRequired(); b.Property(x => x.IpAddress).HasMaxLength(100); b.HasIndex(x => new { x.ArtifactType, x.ArtifactId, x.SignedAt }); b.HasIndex(x => new { x.UserId, x.SignedAt });
        });
        modelBuilder.Entity<SecurityAuditEvent>(b =>
        {
            b.ToTable("security_audit_events"); b.HasKey(x => x.Id); b.Property(x => x.EventType).HasMaxLength(100).IsRequired(); b.Property(x => x.ActorId).HasMaxLength(100).IsRequired(); b.Property(x => x.Target).HasMaxLength(300).IsRequired(); b.Property(x => x.Outcome).HasMaxLength(30).IsRequired(); b.Property(x => x.Detail).HasMaxLength(4000).IsRequired(); b.Property(x => x.IpAddress).HasMaxLength(100); b.HasIndex(x => x.OccurredAt); b.HasIndex(x => new { x.ActorId, x.OccurredAt });
        });
        modelBuilder.Entity<ExternalIdentityProvider>(b =>
        {
            b.ToTable("external_identity_providers"); b.HasKey(x => x.Id);
            b.Property(x => x.Key).HasMaxLength(ExternalIdentityProvider.KeyMaxLength).IsRequired();
            b.Property(x => x.DisplayName).HasMaxLength(ExternalIdentityProvider.DisplayNameMaxLength).IsRequired();
            b.Property(x => x.Protocol).HasConversion<string>().HasMaxLength(30);
            b.Property(x => x.Issuer).HasMaxLength(ExternalIdentityProvider.IssuerMaxLength).IsRequired();
            b.Property(x => x.SubjectClaim).HasMaxLength(ExternalIdentityProvider.ClaimMaxLength).IsRequired();
            b.Property(x => x.GroupClaim).HasMaxLength(ExternalIdentityProvider.ClaimMaxLength).IsRequired();
            b.Property(x => x.CreatedBy).HasMaxLength(ExternalIdentityProvider.ActorMaxLength).IsRequired();
            b.HasIndex(x => x.Key).IsUnique(); b.HasIndex(x => x.Issuer).IsUnique();
        });
        modelBuilder.Entity<ExternalGroupRoleMapping>(b =>
        {
            b.ToTable("external_group_role_mappings"); b.HasKey(x => x.Id);
            b.Property(x => x.ExternalGroup).HasMaxLength(ExternalGroupRoleMapping.ExternalGroupMaxLength).IsRequired();
            b.Property(x => x.Role).HasConversion<string>().HasMaxLength(40);
            b.Property(x => x.CreatedBy).HasMaxLength(ExternalGroupRoleMapping.ActorMaxLength).IsRequired();
            b.HasIndex(x => new { x.ProviderId, x.ExternalGroup, x.ProgramId, x.Role }).IsUnique();
            b.HasIndex(x => x.ProgramId);
            b.HasOne<ExternalIdentityProvider>().WithMany().HasForeignKey(x => x.ProviderId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<ProgramRecord>().WithMany().HasForeignKey(x => x.ProgramId).OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<ArtifactSchemaDefinition>(b =>
        {
            b.ToTable("artifact_schema_definitions"); b.HasKey(x=>x.Id); b.Property(x=>x.Key).HasMaxLength(80).IsRequired(); b.Property(x=>x.Name).HasMaxLength(200).IsRequired(); b.Property(x=>x.AppliesTo).HasMaxLength(80).IsRequired(); b.Property(x=>x.Description).HasMaxLength(2000); b.Property(x=>x.CreatedBy).HasMaxLength(100).IsRequired(); b.HasIndex(x=>new{x.ProjectId,x.Key}).IsUnique(); b.HasMany(x=>x.Fields).WithOne().HasForeignKey(x=>x.SchemaId).OnDelete(DeleteBehavior.Cascade);
        });
        modelBuilder.Entity<ArtifactFieldDefinition>(b =>
        {
            b.ToTable("artifact_field_definitions"); b.HasKey(x=>x.Id); b.Property(x=>x.Key).HasMaxLength(80).IsRequired(); b.Property(x=>x.Label).HasMaxLength(200).IsRequired(); b.Property(x=>x.Type).HasConversion<string>().HasMaxLength(40); b.Property(x=>x.OptionsJson).IsRequired(); b.Property(x=>x.CreatedBy).HasMaxLength(100).IsRequired(); b.HasIndex(x=>new{x.SchemaId,x.Key}).IsUnique();
        });
        modelBuilder.Entity<RequirementSpecification>(b =>
        {
            b.ToTable("requirement_specifications"); b.HasKey(x=>x.Id); b.Property(x=>x.DocumentNumber).HasMaxLength(40).IsRequired(); b.Property(x=>x.Title).HasMaxLength(300).IsRequired(); b.Property(x=>x.Level).HasMaxLength(40).IsRequired(); b.Property(x=>x.Description).HasMaxLength(4000); b.Property(x=>x.CreatedBy).HasMaxLength(100).IsRequired(); b.Property(x=>x.Version).IsConcurrencyToken(); b.HasIndex(x=>new{x.ProjectId,x.DocumentNumber}).IsUnique(); b.HasOne<ProjectRecord>().WithMany().HasForeignKey(x=>x.ProjectId).OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<SpecificationNode>(b =>
        {
            b.ToTable("specification_nodes"); b.HasKey(x=>x.Id); b.Property(x=>x.Type).HasConversion<string>().HasMaxLength(30); b.Property(x=>x.Heading).HasMaxLength(500); b.Property(x=>x.CreatedBy).HasMaxLength(100).IsRequired(); b.HasIndex(x=>new{x.SpecificationId,x.ParentId,x.Position}).IsUnique(); b.HasIndex(x=>x.RequirementArtifactId); b.HasOne<RequirementSpecification>().WithMany().HasForeignKey(x=>x.SpecificationId).OnDelete(DeleteBehavior.Cascade); b.HasOne<SpecificationNode>().WithMany().HasForeignKey(x=>x.ParentId).OnDelete(DeleteBehavior.Restrict); b.HasOne<RequirementArtifact>().WithMany().HasForeignKey(x=>x.RequirementArtifactId).OnDelete(DeleteBehavior.Restrict);
        });
        // The verification counterparts, mapped the same way their requirements originals are so the two
        // sides of the product keep the same shape rather than drifting into two conventions.
        modelBuilder.Entity<TestProcedureDocument>(b =>
        {
            b.ToTable("test_procedure_documents"); b.HasKey(x => x.Id);
            b.Property(x => x.DocumentNumber).HasMaxLength(40).IsRequired();
            b.Property(x => x.Title).HasMaxLength(300).IsRequired();
            b.Property(x => x.Level).HasConversion<string>().HasMaxLength(40);
            b.Property(x => x.Description).HasMaxLength(4000);
            b.Property(x => x.CreatedBy).HasMaxLength(100).IsRequired();
            // One document per level per project, which is what makes "the HLRTD" a thing you can name.
            b.HasIndex(x => new { x.ProjectId, x.Level }).IsUnique();
            b.HasIndex(x => x.DocumentNumber).IsUnique();
            b.HasOne<ProjectRecord>().WithMany().HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<TestProcedureDocumentNode>(b =>
        {
            b.ToTable("test_procedure_document_nodes"); b.HasKey(x => x.Id);
            b.Property(x => x.Type).HasConversion<string>().HasMaxLength(30);
            b.Property(x => x.Heading).HasMaxLength(500);
            b.Property(x => x.CreatedBy).HasMaxLength(100).IsRequired();
            b.HasIndex(x => new { x.DocumentId, x.ParentId, x.Position }).IsUnique();
            // A procedure sits in one place in one document. Two nodes claiming the same procedure would make
            // "where is this procedure" have two answers.
            b.HasIndex(x => x.ProcedureId).IsUnique().HasFilter("\"ProcedureId\" IS NOT NULL");
            b.HasOne<TestProcedureDocument>().WithMany().HasForeignKey(x => x.DocumentId).OnDelete(DeleteBehavior.Cascade);
            b.HasOne<TestProcedure>().WithMany().HasForeignKey(x => x.ProcedureId).OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<RequirementRevisionProfile>(b =>
        {
            b.ToTable("requirement_revision_profiles"); b.HasKey(x=>x.Id); b.Property(x=>x.RichText); b.Property(x=>x.AttributesJson).IsRequired(); b.Property(x=>x.TagsJson).IsRequired(); b.Property(x=>x.UpdatedBy).HasMaxLength(100).IsRequired(); b.HasIndex(x=>x.RevisionId).IsUnique(); b.HasOne<RequirementRevision>().WithMany().HasForeignKey(x=>x.RevisionId).OnDelete(DeleteBehavior.Restrict); b.HasOne<ArtifactSchemaDefinition>().WithMany().HasForeignKey(x=>x.SchemaId).OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<RequirementRevisionTag>(b =>
        {
            b.ToTable("requirement_revision_tags"); b.HasKey(x => x.Id);
            b.Property(x => x.Tag).HasMaxLength(120).IsRequired();
            b.Property(x => x.DisplayTag).HasMaxLength(120).IsRequired();
            // The index the substring scan could never use, and the uniqueness that stops a tag being
            // written twice for the same revision.
            b.HasIndex(x => new { x.Tag, x.RevisionId });
            b.HasIndex(x => new { x.RevisionId, x.Tag }).IsUnique();
            b.HasOne<RequirementRevision>().WithMany().HasForeignKey(x => x.RevisionId).OnDelete(DeleteBehavior.Cascade);
        });
        modelBuilder.Entity<ArtifactComment>(b =>
        {
            b.ToTable("artifact_comments"); b.HasKey(x=>x.Id); b.Property(x=>x.ArtifactType).HasMaxLength(60).IsRequired(); b.Property(x=>x.Body).HasMaxLength(8000).IsRequired(); b.Property(x=>x.MentionsJson).IsRequired(); b.Property(x=>x.State).HasConversion<string>().HasMaxLength(30); b.Property(x=>x.CreatedBy).HasMaxLength(100).IsRequired(); b.Property(x=>x.ResolvedBy).HasMaxLength(100); b.Property(x=>x.Disposition).HasMaxLength(4000); b.HasIndex(x=>new{x.ProjectId,x.ArtifactType,x.ArtifactId,x.CreatedAt}); b.HasOne<ArtifactComment>().WithMany().HasForeignKey(x=>x.ParentCommentId).OnDelete(DeleteBehavior.Restrict);
        });
        // The same shape and the same constraints as its requirements counterpart below: one view per name
        // per owner per project, so a repeat name is refused rather than becoming the second of two views
        // nobody could tell apart.
        modelBuilder.Entity<SavedProcedureView>(b =>
        {
            b.ToTable("saved_procedure_views"); b.HasKey(x=>x.Id); b.Property(x=>x.Name).HasMaxLength(200).IsRequired(); b.Property(x=>x.QueryJson).IsRequired(); b.Property(x=>x.ColumnsJson).IsRequired(); b.HasIndex(x=>new{x.ProjectId,x.OwnerId,x.Name}).IsUnique(); b.HasOne<UserAccount>().WithMany().HasForeignKey(x=>x.OwnerId).OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<SavedRequirementView>(b =>
        {
            b.ToTable("saved_requirement_views"); b.HasKey(x=>x.Id); b.Property(x=>x.Name).HasMaxLength(200).IsRequired(); b.Property(x=>x.QueryJson).IsRequired(); b.Property(x=>x.ColumnsJson).IsRequired(); b.HasIndex(x=>new{x.ProjectId,x.OwnerId,x.Name}).IsUnique(); b.HasOne<UserAccount>().WithMany().HasForeignKey(x=>x.OwnerId).OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<EnterpriseOperationJob>(b =>
        {
            b.ToTable("enterprise_operation_jobs"); b.HasKey(x=>x.Id); b.Property(x=>x.JobType).HasMaxLength(80).IsRequired(); b.Property(x=>x.RequestJson).IsRequired(); b.Property(x=>x.ResultJson).IsRequired(); b.Property(x=>x.State).HasConversion<string>().HasMaxLength(30); b.Property(x=>x.CreatedBy).HasMaxLength(100).IsRequired(); b.Property(x=>x.IdempotencyKey).HasMaxLength(120).IsRequired();b.Property(x=>x.LastError).HasMaxLength(4000);b.HasIndex(x=>new{x.ProjectId,x.CreatedAt});b.Property(x=>x.ClaimedBy).HasMaxLength(150);b.Property(x=>x.ErrorHistoryJson).IsRequired();b.HasIndex(x=>new{x.ProjectId,x.IdempotencyKey}).IsUnique();
            // The claim reads by state and the recovery sweep reads by lease, so both are indexed rather than
            // scanned: queue selection has to stay bounded as the job table grows.
            b.HasIndex(x=>new{x.State,x.CreatedAt});b.HasIndex(x=>x.LeaseExpiresAt);
        });
        modelBuilder.Entity<RequirementInterchangeJob>(b =>
        {
            b.ToTable("requirement_interchange_jobs"); b.HasKey(x=>x.Id); b.Property(x=>x.FileName).HasMaxLength(260).IsRequired(); b.Property(x=>x.Sha256).HasMaxLength(64).IsRequired(); b.Property(x=>x.MappingJson).IsRequired(); b.Property(x=>x.RowsJson).IsRequired(); b.Property(x=>x.State).HasConversion<string>().HasMaxLength(30); b.Property(x=>x.CreatedBy).HasMaxLength(100).IsRequired(); b.HasIndex(x=>new{x.ProjectId,x.CreatedAt}); b.HasIndex(x=>new{x.ProjectId,x.Sha256});
        });
        modelBuilder.Entity<ArtifactWatch>(b =>
        {
            b.ToTable("artifact_watches");b.HasKey(x=>x.Id);b.Property(x=>x.ArtifactType).HasMaxLength(60).IsRequired();b.Property(x=>x.UserName).HasMaxLength(100).IsRequired();b.Property(x=>x.CreatedBy).HasMaxLength(100).IsRequired();b.HasIndex(x=>new{x.ProjectId,x.ArtifactType,x.ArtifactId,x.UserName}).IsUnique();
        });
        modelBuilder.Entity<ArtifactAssignment>(b =>
        {
            b.ToTable("artifact_assignments");b.HasKey(x=>x.Id);b.Property(x=>x.ArtifactType).HasMaxLength(60).IsRequired();b.Property(x=>x.AssignedTo).HasMaxLength(100).IsRequired();b.Property(x=>x.Title).HasMaxLength(300).IsRequired();b.Property(x=>x.Description).HasMaxLength(4000);b.Property(x=>x.State).HasConversion<string>().HasMaxLength(30);b.Property(x=>x.CreatedBy).HasMaxLength(100).IsRequired();b.Property(x=>x.CompletedBy).HasMaxLength(100);b.Property(x=>x.Version).IsConcurrencyToken();b.HasIndex(x=>new{x.ProjectId,x.AssignedTo,x.State,x.DueAt});b.HasIndex(x=>new{x.ArtifactType,x.ArtifactId});b.HasOne<ArtifactComment>().WithMany().HasForeignKey(x=>x.CommentId).OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<UserNotification>(b =>
        {
            b.ToTable("user_notifications");b.HasKey(x=>x.Id);b.Property(x=>x.Recipient).HasMaxLength(100).IsRequired();b.Property(x=>x.Type).HasMaxLength(60).IsRequired();b.Property(x=>x.Title).HasMaxLength(300).IsRequired();b.Property(x=>x.Detail).HasMaxLength(2000);b.Property(x=>x.Route).HasMaxLength(300);b.Property(x=>x.State).HasConversion<string>().HasMaxLength(30);b.HasIndex(x=>new{x.Recipient,x.State,x.CreatedAt});b.HasIndex(x=>x.ProjectId);
        });
        modelBuilder.Entity<ProjectWorkspaceSynchronization>(b =>
        {
            b.ToTable("project_workspace_synchronizations"); b.HasKey(x => x.Id);
            b.HasIndex(x => x.ProjectId).IsUnique();
        });
        modelBuilder.Entity<JiraConnection>(b =>
        {
            b.ToTable("jira_connections"); b.HasKey(x => x.Id);
            b.Property(x => x.BaseUrl).HasMaxLength(500).IsRequired();
            b.Property(x => x.ProjectKey).HasMaxLength(50).IsRequired();
            b.Property(x => x.IssueType).HasMaxLength(80).IsRequired();
            b.Property(x => x.UserName).HasMaxLength(320);
            b.Property(x => x.ProtectedApiToken).IsRequired();
            b.Property(x => x.CreatedBy).HasMaxLength(100).IsRequired();
            b.Property(x => x.LastError).HasMaxLength(1000);
            // One connection per project. Two would mean the product choosing which tracker a push went to.
            b.HasIndex(x => x.ProjectId).IsUnique();
        });
        modelBuilder.Entity<JiraIssueLink>(b =>
        {
            b.ToTable("jira_issue_links"); b.HasKey(x => x.Id);
            b.Property(x => x.ArtifactType).HasMaxLength(60).IsRequired();
            b.Property(x => x.ArtifactNumber).HasMaxLength(60);
            b.Property(x => x.IssueKey).HasMaxLength(60);
            b.Property(x => x.IssueUrl).HasMaxLength(600);
            b.Property(x => x.IssueStatus).HasMaxLength(120);
            b.Property(x => x.State).HasConversion<string>().HasMaxLength(20);
            b.Property(x => x.LastError).HasMaxLength(1000);
            b.Property(x => x.CreatedBy).HasMaxLength(100).IsRequired();
            // One issue per artifact: a second push must find the existing link, not create a duplicate.
            b.HasIndex(x => new { x.ArtifactType, x.ArtifactId }).IsUnique();
            b.HasIndex(x => new { x.ProjectId, x.State });
        });
        modelBuilder.Entity<ReviewWorkflow>(b =>
        {
            b.ToTable("review_workflows"); b.HasKey(x => x.Id);
            b.Property(x => x.Name).HasMaxLength(200).IsRequired();
            b.Property(x => x.AppliesTo).HasConversion<string>().HasMaxLength(30);
            b.Property(x => x.Mode).HasConversion<string>().HasMaxLength(20);
            b.Property(x => x.State).HasConversion<string>().HasMaxLength(20);
            b.Property(x => x.CreatedBy).HasMaxLength(100).IsRequired();
            // One active procedure per change-request type per project. Two would mean the product silently
            // choosing which rules a review was judged by.
            b.HasIndex(x => new { x.ProjectId, x.AppliesTo, x.State });
            b.HasIndex(x => new { x.LogicalId, x.Version }).IsUnique();
            b.HasMany(x => x.Stages).WithOne().HasForeignKey(x => x.WorkflowId).OnDelete(DeleteBehavior.Cascade);
        });
        modelBuilder.Entity<ReviewWorkflowStage>(b =>
        {
            b.ToTable("review_workflow_stages"); b.HasKey(x => x.Id);
            b.Property(x => x.Name).HasMaxLength(120).IsRequired();
            b.Property(x => x.RequiredRole).HasConversion<string>().HasMaxLength(40);
            // Stored by name, and defaulted to Review rather than the enum's zero value by coincidence: every
            // stage recorded before this existed was a content review, and the default has to say so on
            // purpose (LES-004).
            b.Property(x => x.Kind).HasConversion<string>().HasMaxLength(20).HasDefaultValue(ReviewStageKind.Review);
            b.HasIndex(x => new { x.WorkflowId, x.Position }).IsUnique();
        });
        modelBuilder.Entity<NotificationDelivery>(b =>
        {
            b.ToTable("notification_deliveries"); b.HasKey(x => x.Id);
            b.Property(x => x.Channel).HasConversion<string>().HasMaxLength(30);
            b.Property(x => x.State).HasConversion<string>().HasMaxLength(30);
            b.Property(x => x.Recipient).HasMaxLength(100).IsRequired();
            b.Property(x => x.Address).HasMaxLength(320);
            b.Property(x => x.LastError).HasMaxLength(1000);
            // The dispatcher reads exactly this: oldest pending first.
            b.HasIndex(x => new { x.State, x.Channel, x.Sequence });
            b.HasIndex(x => x.NotificationId);
            b.HasOne<UserNotification>().WithMany().HasForeignKey(x => x.NotificationId).OnDelete(DeleteBehavior.Cascade);
        });
        modelBuilder.Entity<NotificationPreference>(b =>
        {
            b.ToTable("notification_preferences"); b.HasKey(x => x.Id);
            b.Property(x => x.Recipient).HasMaxLength(100).IsRequired();
            b.HasIndex(x => x.Recipient).IsUnique();
        });
        modelBuilder.Entity<RequirementImportMapping>(b =>
        {
            b.ToTable("requirement_import_mappings");b.HasKey(x=>x.Id);b.Property(x=>x.Name).HasMaxLength(200).IsRequired();b.Property(x=>x.MappingJson).IsRequired();b.Property(x=>x.CreatedBy).HasMaxLength(100).IsRequired();b.HasIndex(x=>new{x.ProjectId,x.Name}).IsUnique();
        });
        modelBuilder.Entity<ControlledAttachment>(b =>
        {
            b.ToTable("controlled_attachments");b.HasKey(x=>x.Id);b.Property(x=>x.ArtifactType).HasMaxLength(60).IsRequired();b.Property(x=>x.Label).HasMaxLength(300).IsRequired();b.Property(x=>x.Description).HasMaxLength(4000);b.Property(x=>x.OriginalFileName).HasMaxLength(260).IsRequired();b.Property(x=>x.ContentType).HasMaxLength(200).IsRequired();b.Property(x=>x.Sha256).HasMaxLength(64).IsRequired();b.Property(x=>x.StorageKey).HasMaxLength(500).IsRequired();b.Property(x=>x.State).HasConversion<string>().HasMaxLength(30);b.Property(x=>x.UploadedBy).HasMaxLength(100).IsRequired();b.Property(x=>x.ValidationProfile).HasMaxLength(100);b.Property(x=>x.ValidationResult).HasMaxLength(100);b.HasIndex(x=>new{x.ProjectId,x.ArtifactType,x.ArtifactId,x.State});b.HasIndex(x=>new{x.RevisionId,x.LogicalId,x.Version});b.HasIndex(x=>new{x.LogicalId,x.Version}).IsUnique();b.HasIndex(x=>new{x.ProjectId,x.Sha256});b.HasOne<ControlledAttachment>().WithMany().HasForeignKey(x=>x.SupersedesId).OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<ArtifactEditSession>(b =>
        {
            b.ToTable("artifact_edit_sessions");b.HasKey(x=>x.Id);b.Property(x=>x.ArtifactType).HasMaxLength(60).IsRequired();b.Property(x=>x.BaseSnapshotHash).HasMaxLength(64).IsRequired();b.Property(x=>x.DraftJson).IsRequired();b.Property(x=>x.UserName).HasMaxLength(100).IsRequired();b.Property(x=>x.State).HasConversion<string>().HasMaxLength(30);b.Property(x=>x.LockKey).HasMaxLength(100);b.Property(x=>x.ClosedBy).HasMaxLength(100);b.Property(x=>x.ClosedReason).HasMaxLength(2000);b.Property(x=>x.Version).IsConcurrencyToken();b.HasIndex(x=>new{x.ProjectId,x.ArtifactType,x.ArtifactId,x.State});b.HasIndex(x=>new{x.UserName,x.UpdatedAt});b.HasIndex(x=>x.LockKey).IsUnique();
        });
        modelBuilder.Entity<ArtifactDraftSnapshot>(b=>
        {
            b.ToTable("artifact_draft_snapshots");b.HasKey(x=>x.Id);b.Property(x=>x.ArtifactType).HasMaxLength(60).IsRequired();b.Property(x=>x.DraftJson).IsRequired();b.Property(x=>x.Sha256).HasMaxLength(64).IsRequired();b.Property(x=>x.CreatedBy).HasMaxLength(100).IsRequired();b.Property(x=>x.RestoredBy).HasMaxLength(100);b.HasIndex(x=>new{x.SessionId,x.Sequence}).IsUnique();b.HasIndex(x=>new{x.ProjectId,x.ArtifactType,x.ArtifactId,x.CreatedAt});b.HasOne<ArtifactEditSession>().WithMany().HasForeignKey(x=>x.SessionId).OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<DocumentTemplate>(b =>
        {
            b.ToTable("document_templates"); b.HasKey(x => x.Id); b.Property(x => x.TemplateNumber).HasMaxLength(80).IsRequired();
            b.Property(x => x.Title).HasMaxLength(300).IsRequired(); b.Property(x => x.Body).HasMaxLength(32000); b.Property(x => x.OwnerId).HasMaxLength(100).IsRequired();
            b.Property(x => x.State).HasConversion<string>().HasMaxLength(30); b.Property(x => x.Version).IsConcurrencyToken(); b.HasIndex(x => new { x.ProjectId, x.TemplateNumber }).IsUnique();
            b.HasOne<ProjectRecord>().WithMany().HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<ProblemReport>(b =>
        {
            b.ToTable("problem_reports"); b.HasKey(x => x.Id); b.Property(x => x.ReportNumber).HasMaxLength(80).IsRequired();
            b.Property(x => x.Title).HasMaxLength(300).IsRequired(); b.Property(x => x.Problem).HasMaxLength(8000).IsRequired(); b.Property(x => x.ProblemRich).HasMaxLength(32000); b.Property(x => x.AdditionalInformation).HasMaxLength(8000); b.Property(x => x.AdditionalInformationRich).HasMaxLength(32000); b.Property(x => x.Analysis).HasMaxLength(8000); b.Property(x => x.ReportedBy).HasMaxLength(100).IsRequired(); b.Property(x => x.ResponsibleEngineerId).HasMaxLength(100).IsRequired();
            b.Property(x => x.Classification).HasMaxLength(100).IsRequired(); b.Property(x => x.Origin).HasMaxLength(200).IsRequired(); b.Property(x => x.AffectedConfiguration).HasMaxLength(1000);
            b.Property(x => x.RootCause).HasMaxLength(8000); b.Property(x => x.Effects).HasMaxLength(8000); b.Property(x => x.Containment).HasMaxLength(8000); b.Property(x => x.CorrectiveAction).HasMaxLength(8000); b.Property(x => x.SystemAircraftImpact).HasMaxLength(8000); b.Property(x => x.ImpactAssessmentJson).HasMaxLength(4000); b.Property(x => x.DispositionRationale).HasMaxLength(8000);
            b.Property(x => x.WaiverRationale).HasMaxLength(8000); b.Property(x => x.WaivedBy).HasMaxLength(100); b.Property(x => x.ClosureApprovedByName).HasMaxLength(100);
            b.Property(x => x.Severity).HasConversion<string>().HasMaxLength(30); b.Property(x => x.Priority).HasConversion<string>().HasMaxLength(30); b.Property(x => x.Disposition).HasConversion<string>().HasMaxLength(30); b.Property(x => x.State).HasConversion<string>().HasMaxLength(40);
            // By name, so adding a kind later costs nothing in the database.
            b.Property(x => x.Type).HasConversion<string>().HasMaxLength(40);
            b.Property(x => x.NumberSequence).IsRequired(); b.Property(x => x.Version).IsConcurrencyToken(); b.HasIndex(x => new { x.ProjectId, x.ReportNumber }).IsUnique(); b.HasIndex(x => new { x.ProjectId, x.NumberSequence, x.Revision, x.Id }); b.HasIndex(x => new { x.ProjectId, x.State, x.Severity }); b.HasIndex(x => new { x.ProjectId, x.IsReleaseBlocker });
            b.HasOne<ProjectRecord>().WithMany().HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<ProblemReportRevision>(b =>
        {
            b.ToTable("problem_report_revisions"); b.HasKey(x => x.Id); b.Property(x => x.EventType).HasMaxLength(80).IsRequired(); b.Property(x => x.Actor).HasMaxLength(100).IsRequired(); b.Property(x => x.SnapshotHash).HasMaxLength(64).IsRequired(); b.Property(x => x.SnapshotJson).IsRequired(); b.Property(x => x.SnapshotSchemaVersion).IsRequired(); b.Property(x => x.Detail).HasMaxLength(2000).IsRequired(); b.Property(x => x.EvidenceJson); b.Property(x => x.EventSchemaVersion).IsRequired();
            b.HasIndex(x => new { x.ProblemReportId, x.OccurredAt }); b.HasIndex(x => new { x.ProblemReportId, x.Revision, x.EventType }); b.HasOne<ProblemReport>().WithMany().HasForeignKey(x => x.ProblemReportId).OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<ProblemReportLink>(b =>
        {
            b.ToTable("problem_report_links"); b.HasKey(x => x.Id); b.Property(x => x.ArtifactType).HasMaxLength(80).IsRequired(); b.Property(x => x.Relationship).HasMaxLength(80).IsRequired(); b.Property(x => x.AddedBy).HasMaxLength(100).IsRequired();
            b.HasIndex(x => new { x.ProblemReportId, x.ArtifactType, x.ArtifactId, x.Relationship }).IsUnique(); b.HasIndex(x => new { x.ArtifactType, x.ArtifactId }); b.HasOne<ProblemReport>().WithMany().HasForeignKey(x => x.ProblemReportId).OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<ProblemReportClosureCandidate>(b =>
        {
            b.ToTable("problem_report_closure_candidates"); b.HasKey(x => x.Id);
            b.Property(x => x.ReportSnapshotSchemaVersion).IsRequired();
            b.Property(x => x.ReportSnapshotJson).IsRequired(); b.Property(x => x.ReportSnapshotHash).HasMaxLength(64).IsRequired();
            b.Property(x => x.VerificationEvidenceJson).IsRequired(); b.Property(x => x.VerificationEvidenceHash).HasMaxLength(64).IsRequired();
            b.Property(x => x.LinksManifestJson).IsRequired(); b.Property(x => x.LinksManifestHash).HasMaxLength(64).IsRequired();
            b.Property(x => x.ManifestHash).HasMaxLength(64).IsRequired(); b.Property(x => x.SelectedBy).HasMaxLength(100).IsRequired();
            b.Property(x => x.State).HasConversion<string>().HasMaxLength(30); b.Property(x => x.InvalidatedBy).HasMaxLength(100);
            b.Property(x => x.InvalidationReason).HasMaxLength(1000); b.Property(x => x.ApprovedBy).HasMaxLength(100);
            b.Property(x => x.PackageProvenance).HasMaxLength(40).IsRequired(); b.Property(x => x.ClosurePackageJson).IsRequired();
            b.Property(x => x.ClosurePackageHash).HasMaxLength(64).IsRequired();
            b.HasIndex(x => new { x.ProblemReportId, x.ReportRevision, x.Sequence }).IsUnique();
            b.HasIndex(x => new { x.ProblemReportId, x.State }); b.HasIndex(x => x.ManifestHash);
            b.HasOne<ProblemReport>().WithMany().HasForeignKey(x => x.ProblemReportId).OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<CodeTraceabilityRecord>(b =>
        {
            b.ToTable("code_traceability_records"); b.HasKey(x => x.Id);
            b.Property(x => x.Disposition).HasConversion<string>().HasMaxLength(40); b.Property(x => x.RepositoryPath).HasMaxLength(300);
            b.Property(x => x.MergeRequestReference).HasMaxLength(80); b.Property(x => x.MergeRequestTitle).HasMaxLength(500);
            b.Property(x => x.MergeRequestUrl).HasMaxLength(1000); b.Property(x => x.MergeCommitSha).HasMaxLength(64);
            b.Property(x => x.NoCodeChangeRationale).HasMaxLength(4000); b.Property(x => x.RecordedBy).HasMaxLength(100).IsRequired();
            b.HasIndex(x => new { x.ReleaseId, x.RequirementRevisionId }).IsUnique(); b.HasIndex(x => new { x.ProjectId, x.ReleaseId });
            b.HasOne<ProjectRecord>().WithMany().HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<SoftwareRelease>().WithMany().HasForeignKey(x => x.ReleaseId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<RequirementArtifact>().WithMany().HasForeignKey(x => x.RequirementArtifactId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<RequirementRevision>().WithMany().HasForeignKey(x => x.RequirementRevisionId).OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<ConfigurationChangeSet>(b =>
        {
            b.ToTable("configuration_change_sets"); b.HasKey(x => x.Id); b.Property(x => x.ChangeSetNumber).HasMaxLength(80).IsRequired();
            b.Property(x => x.Title).HasMaxLength(300).IsRequired(); b.Property(x => x.Description).HasMaxLength(16000); b.Property(x => x.OwnerId).HasMaxLength(100).IsRequired();
            b.Property(x => x.State).HasConversion<string>().HasMaxLength(30);b.Property(x=>x.MergeResultJson);b.Property(x=>x.ConflictJson);b.Property(x=>x.ResolutionRationale).HasMaxLength(4000); b.Property(x => x.Version).IsConcurrencyToken(); b.HasIndex(x => new { x.ProjectId, x.ChangeSetNumber }).IsUnique();b.HasIndex(x=>new{x.ComponentId,x.State});
            b.HasOne<ProjectRecord>().WithMany().HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<ControlledArtifactCheckInEvidence>(b =>
        {
            b.ToTable("controlled_artifact_check_in_evidence"); b.HasKey(x => x.Id);
            b.Property(x => x.ArtifactType).HasMaxLength(60).IsRequired();
            b.Property(x => x.Adapter).HasMaxLength(160).IsRequired();
            b.Property(x => x.Actor).HasMaxLength(100).IsRequired();
            b.Property(x => x.BaseSnapshotHash).HasMaxLength(64).IsRequired();
            b.Property(x => x.ResultingSnapshotHash).HasMaxLength(64);
            b.Property(x => x.RevisionBefore).HasMaxLength(80);
            b.Property(x => x.RevisionAfter).HasMaxLength(80);
            b.Property(x => x.Outcome).HasConversion<string>().HasMaxLength(30);
            b.Property(x => x.Reason).HasMaxLength(4000).IsRequired();
            b.HasIndex(x => new { x.ProjectId, x.ArtifactType, x.ArtifactId, x.OccurredAt });
            b.HasIndex(x => new { x.SessionId, x.OccurredAt });
            b.HasOne<ArtifactEditSession>().WithMany().HasForeignKey(x => x.SessionId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<ArtifactDraftSnapshot>().WithMany().HasForeignKey(x => x.DraftSnapshotId).OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<ArtifactMergeConflict>(b =>
        {
            b.ToTable("artifact_merge_conflicts");b.HasKey(x=>x.Id);b.Property(x=>x.BaseJson).IsRequired();b.Property(x=>x.LocalJson).IsRequired();b.Property(x=>x.RemoteJson).IsRequired();b.Property(x=>x.ResolutionJson);b.Property(x=>x.CreatedBy).HasMaxLength(100).IsRequired();b.Property(x=>x.ResolvedBy).HasMaxLength(100);b.HasIndex(x=>new{x.ProjectId,x.ArtifactId,x.ResolvedAt});
        });
        modelBuilder.Entity<EnterpriseIntegrityCheckpoint>(b =>
        {
            b.ToTable("enterprise_integrity_checkpoints");b.HasKey(x=>x.Id);b.Property(x=>x.ManifestHash).HasMaxLength(64).IsRequired();b.Property(x=>x.State).HasConversion<string>().HasMaxLength(30);b.Property(x=>x.Detail).HasMaxLength(4000);b.Property(x=>x.CreatedBy).HasMaxLength(100).IsRequired();b.HasIndex(x=>new{x.ProjectId,x.CreatedAt});
        });
        modelBuilder.Entity<IntegrationServiceIdentity>(b =>
        {
            b.ToTable("integration_service_identities"); b.HasKey(x=>x.Id); b.Property(x=>x.Name).HasMaxLength(200).IsRequired(); b.Property(x=>x.ClientId).HasMaxLength(64).IsRequired(); b.Property(x=>x.ApiKeyHash).HasMaxLength(64).IsRequired(); b.Property(x=>x.ScopesJson).IsRequired(); b.Property(x=>x.State).HasConversion<string>().HasMaxLength(30); b.Property(x=>x.CreatedBy).HasMaxLength(100).IsRequired(); b.Property(x=>x.RevokedBy).HasMaxLength(100); b.HasIndex(x=>x.ClientId).IsUnique(); b.HasIndex(x=>new{x.ProjectId,x.State}); b.HasOne<ProjectRecord>().WithMany().HasForeignKey(x=>x.ProjectId).OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<WebhookSubscription>(b =>
        {
            b.ToTable("webhook_subscriptions"); b.HasKey(x=>x.Id); b.Property(x=>x.Name).HasMaxLength(200).IsRequired(); b.Property(x=>x.EndpointUrl).HasMaxLength(2000).IsRequired(); b.Property(x=>x.EventTypesJson).IsRequired(); b.Property(x=>x.ProtectedSecret).IsRequired(); b.Property(x=>x.CreatedBy).HasMaxLength(100).IsRequired(); b.HasIndex(x=>new{x.ProjectId,x.Name}).IsUnique(); b.HasIndex(x=>new{x.ProjectId,x.IsEnabled}); b.HasOne<ProjectRecord>().WithMany().HasForeignKey(x=>x.ProjectId).OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<IntegrationEvent>(b =>
        {
            b.ToTable("integration_events"); b.HasKey(x=>x.Id); b.Property(x=>x.EventType).HasMaxLength(160).IsRequired(); b.Property(x=>x.AggregateType).HasMaxLength(100).IsRequired(); b.Property(x=>x.PayloadJson).IsRequired(); b.Property(x=>x.Actor).HasMaxLength(120).IsRequired(); b.Property(x=>x.IdempotencyKey).HasMaxLength(160); b.Property(x=>x.State).HasConversion<string>().HasMaxLength(30); b.Property(x=>x.LastError).HasMaxLength(2000); b.HasIndex(x=>new{x.ProjectId,x.OccurredAt}); b.HasIndex(x=>new{x.ProjectId,x.IdempotencyKey}).IsUnique(); b.HasIndex(x=>new{x.State,x.OccurredAt}); b.HasOne<ProjectRecord>().WithMany().HasForeignKey(x=>x.ProjectId).OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<WebhookDelivery>(b =>
        {
            b.ToTable("webhook_deliveries"); b.HasKey(x=>x.Id); b.Property(x=>x.State).HasConversion<string>().HasMaxLength(30); b.Property(x=>x.LastError).HasMaxLength(2000); b.HasIndex(x=>new{x.State,x.NextAttemptAt}); b.HasIndex(x=>new{x.ProjectId,x.CreatedAt}); b.HasIndex(x=>new{x.IntegrationEventId,x.SubscriptionId}).IsUnique(); b.HasOne<IntegrationEvent>().WithMany().HasForeignKey(x=>x.IntegrationEventId).OnDelete(DeleteBehavior.Restrict); b.HasOne<WebhookSubscription>().WithMany().HasForeignKey(x=>x.SubscriptionId).OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<ReqIfExchangeJob>(b =>
        {
            b.ToTable("reqif_exchange_jobs"); b.HasKey(x=>x.Id); b.Property(x=>x.Direction).HasConversion<string>().HasMaxLength(20); b.Property(x=>x.State).HasConversion<string>().HasMaxLength(30); b.Property(x=>x.FileName).HasMaxLength(260).IsRequired(); b.Property(x=>x.Sha256).HasMaxLength(64).IsRequired(); b.Property(x=>x.StorageKey).HasMaxLength(500).IsRequired(); b.Property(x=>x.ManifestJson).IsRequired();b.Property(x=>x.CheckpointJson).IsRequired();b.Property(x=>x.LastError).HasMaxLength(4000); b.Property(x=>x.CreatedBy).HasMaxLength(100).IsRequired(); b.HasIndex(x=>new{x.ProjectId,x.CreatedAt}); b.HasIndex(x=>new{x.ProjectId,x.Direction,x.State}); b.HasOne<ProjectRecord>().WithMany().HasForeignKey(x=>x.ProjectId).OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<ManagedDocument>(b =>
        {
            b.ToTable("managed_documents"); b.HasKey(x => x.Id);
            b.Property(x => x.DocumentNumber).HasMaxLength(40).IsRequired(); b.Property(x => x.Acronym).HasMaxLength(12).IsRequired();
            b.Property(x => x.DocumentType).HasMaxLength(160).IsRequired(); b.Property(x => x.Title).HasMaxLength(300).IsRequired();
            b.Property(x => x.OwnerId).HasMaxLength(100).IsRequired(); b.Property(x => x.StewardId).HasMaxLength(100).IsRequired(); b.Property(x => x.CreatedBy).HasMaxLength(100).IsRequired(); b.Property(x => x.Version).IsConcurrencyToken();
            b.HasIndex(x => new { x.ProjectId, x.DocumentNumber }).IsUnique(); b.HasIndex(x => new { x.ProjectId, x.Acronym });
            b.HasIndex(x => new { x.ProjectId, x.DocumentType, x.DocumentNumber });
            b.HasIndex(x => new { x.ProjectId, x.StewardId, x.DocumentNumber });
            b.HasIndex(x => new { x.ProjectId, x.UpdatedAt, x.DocumentNumber });
            b.HasOne<ProjectRecord>().WithMany().HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<ManagedDocumentRevision>(b =>
        {
            b.ToTable("managed_document_revisions"); b.HasKey(x => x.Id);
            b.HasMany(x => x.Comments).WithOne().HasForeignKey(x => x.ManagedDocumentRevisionId).OnDelete(DeleteBehavior.Cascade);
            b.Property(x => x.State).HasConversion<string>().HasMaxLength(30); b.Property(x => x.OwnerId).HasMaxLength(100).IsRequired(); b.Property(x => x.ResponsibleOwnerId).HasMaxLength(100).IsRequired(); b.Property(x => x.InitiatedBy).HasMaxLength(100).IsRequired();
            b.Property(x => x.FormalChangeSummary).HasColumnName("ChangeSummary").HasMaxLength(4000).IsRequired(); b.Property(x => x.FormalSummaryHash).HasMaxLength(64).IsRequired(); b.Property(x => x.FormalSummaryProvenance).HasMaxLength(80).IsRequired();
            b.Property(x => x.SnapshotHash).HasMaxLength(64).IsRequired(); b.Property(x => x.SubmittedFormalSummaryHash).HasMaxLength(64).IsRequired();
            b.Property(x => x.SubmittedRelationshipManifest).IsRequired(); b.Property(x => x.SubmittedRelationshipManifestHash).HasMaxLength(64).IsRequired();
            b.Property(x => x.ParentReleasedDocxSha256).HasMaxLength(64); b.Property(x => x.TransformationProfile).HasMaxLength(120).IsRequired();
            b.Property(x => x.ReleaseManifestHash).HasMaxLength(64).IsRequired(); b.Property(x => x.ReturnReason).HasMaxLength(4000).IsRequired();
            b.Property(x => x.SubmittedBy).HasMaxLength(100); b.Property(x => x.ReleasedBy).HasMaxLength(100); b.Property(x => x.Version).IsConcurrencyToken();
            b.Ignore(x => x.CurrentReviewCycle); b.HasIndex(x => new { x.DocumentId, x.Revision }).IsUnique();
            b.HasIndex(x => new { x.DocumentId, x.State }); b.HasIndex(x => new { x.ResponsibleOwnerId, x.State, x.DocumentId }); b.HasIndex(x => x.ParentRevisionId);
            b.HasOne<ManagedDocument>().WithMany().HasForeignKey(x => x.DocumentId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<ManagedDocumentRevision>().WithMany().HasForeignKey(x => x.ParentRevisionId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<ControlledAttachment>().WithMany().HasForeignKey(x => x.ParentReleasedDocxAttachmentId).OnDelete(DeleteBehavior.Restrict);
            b.HasMany(x => x.ReviewSteps).WithOne().HasForeignKey(x => x.RevisionId).OnDelete(DeleteBehavior.Cascade);
            b.Navigation(x => x.ReviewSteps).UsePropertyAccessMode(PropertyAccessMode.Field);
        });
        modelBuilder.Entity<ManagedDocumentCheckIn>(b =>
        {
            b.ToTable("managed_document_check_ins"); b.HasKey(x => x.Id);
            b.Property(x => x.ActorId).HasMaxLength(100).IsRequired(); b.Property(x => x.Comment).HasMaxLength(4000).IsRequired();
            b.Property(x => x.BaseSha256).HasMaxLength(64); b.Property(x => x.ResultSha256).HasMaxLength(64).IsRequired(); b.Property(x => x.OperationId).HasMaxLength(160).IsRequired(); b.Property(x => x.ReturnResolutionNote).HasMaxLength(4000);
            b.HasIndex(x => new { x.RevisionId, x.WorkingVersion }).IsUnique(); b.HasIndex(x => x.WorkingAttachmentId).IsUnique();
            b.HasIndex(x => new { x.RevisionId, x.OccurredAt });
            b.HasOne<ManagedDocumentRevision>().WithMany().HasForeignKey(x => x.RevisionId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<ControlledAttachment>().WithMany().HasForeignKey(x => x.WorkingAttachmentId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<ControlledAttachment>().WithMany().HasForeignKey(x => x.BaseAttachmentId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<ControlledAttachment>().WithMany().HasForeignKey(x => x.SupersededAttachmentId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<ArtifactEditSession>().WithMany().HasForeignKey(x => x.ConnectorSessionId).OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<ManagedDocumentReviewContributor>(b =>
        {
            b.ToTable("managed_document_review_contributors"); b.HasKey(x => x.Id);
            b.Property(x => x.ContributorId).HasMaxLength(100).IsRequired(); b.Property(x => x.EvidenceHash).HasMaxLength(64).IsRequired(); b.Property(x => x.Provenance).HasMaxLength(80).IsRequired();
            b.HasIndex(x => new { x.RevisionId, x.ReviewCycle, x.ContributorId }).IsUnique();
            b.HasOne<ManagedDocumentRevision>().WithMany().HasForeignKey(x => x.RevisionId).OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<ManagedDocumentAssignment>(b =>
        {
            b.ToTable("managed_document_assignments"); b.HasKey(x => x.Id);
            b.Property(x => x.AssignmentType).HasMaxLength(40).IsRequired(); b.Property(x => x.PriorAssigneeId).HasMaxLength(100).IsRequired();
            b.Property(x => x.NewAssigneeId).HasMaxLength(100).IsRequired(); b.Property(x => x.AssignedBy).HasMaxLength(100).IsRequired(); b.Property(x => x.Reason).HasMaxLength(1000).IsRequired();
            b.HasIndex(x => new { x.DocumentId, x.EffectiveAt }); b.HasIndex(x => x.RevisionId);
            b.HasOne<ManagedDocument>().WithMany().HasForeignKey(x => x.DocumentId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<ManagedDocumentRevision>().WithMany().HasForeignKey(x => x.RevisionId).OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<ManagedDocumentReviewStep>(b =>
        {
            b.ToTable("managed_document_review_steps"); b.HasKey(x => x.Id);
            b.Property(x => x.ApproverId).HasMaxLength(100).IsRequired(); b.Property(x => x.ApproverName).HasMaxLength(200).IsRequired();
            b.Property(x => x.StageName).HasMaxLength(120).IsRequired(); b.Property(x => x.Kind).HasConversion<string>().HasMaxLength(20);
            b.Property(x => x.State).HasConversion<string>().HasMaxLength(30);
            b.Property(x => x.RequiredAuthority).HasMaxLength(80).IsRequired(); b.Property(x => x.GrantedAuthority).HasMaxLength(80).IsRequired();
            b.Property(x => x.AuthoritySource).HasMaxLength(80).IsRequired(); b.Property(x => x.WorkflowName).HasMaxLength(200).IsRequired();
            b.Property(x => x.AuthorityPolicy).HasMaxLength(80).IsRequired(); b.Property(x => x.Version).IsConcurrencyToken();
            b.Property(x => x.Rationale).HasMaxLength(4000).IsRequired(); b.HasIndex(x => new { x.RevisionId, x.Cycle, x.Position }).IsUnique();
            b.HasIndex(x => new { x.ApproverId, x.State, x.AssignedAt });
        });
        modelBuilder.Entity<ManagedDocumentOperation>(b =>
        {
            b.ToTable("managed_document_operations"); b.HasKey(x => x.Id); b.Property(x => x.OperationType).HasMaxLength(40).IsRequired();
            b.Property(x => x.OperationKey).HasMaxLength(100).IsRequired(); b.Property(x => x.PayloadHash).HasMaxLength(64).IsRequired(); b.Property(x => x.ResultJson).HasMaxLength(4000).IsRequired();
            b.HasIndex(x => new { x.RevisionId, x.OperationType, x.OperationKey }).IsUnique();
            b.HasOne<ManagedDocumentRevision>().WithMany().HasForeignKey(x => x.RevisionId).OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<ManagedDocumentStorageOperation>(b =>
        {
            b.ToTable("managed_document_storage_operations"); b.HasKey(x => x.Id);
            b.Property(x => x.OperationType).HasMaxLength(40).IsRequired(); b.Property(x => x.OperationKey).HasMaxLength(100).IsRequired();
            b.Property(x => x.PayloadHash).HasMaxLength(64).IsRequired(); b.Property(x => x.State).HasConversion<string>().HasMaxLength(30);
            b.Property(x => x.ObjectManifestJson).HasColumnType("text").IsRequired(); b.Property(x => x.ResultJson).HasColumnType("text").IsRequired();
            b.Property(x => x.Detail).HasMaxLength(4000).IsRequired(); b.Property(x => x.ActorId).HasMaxLength(100).IsRequired();
            b.HasIndex(x => new { x.ProjectId, x.OperationType, x.OperationKey }).IsUnique();
            b.HasIndex(x => new { x.ProjectId, x.State, x.UpdatedAt });
            b.HasOne<ProjectRecord>().WithMany().HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<ManagedDocumentBuildProvenance>(b =>
        {
            b.ToTable("managed_document_build_provenance"); b.HasKey(x => x.Id); b.Property(x => x.Source).HasMaxLength(80).IsRequired(); b.Property(x => x.RecordedBy).HasMaxLength(100).IsRequired();
            b.HasIndex(x => new { x.DocumentId, x.ReleaseId, x.RevisionId, x.Source }).IsUnique(); b.HasIndex(x => x.RevisionId);
            b.HasOne<ProjectRecord>().WithMany().HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<SoftwareRelease>().WithMany().HasForeignKey(x => x.ReleaseId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<ManagedDocument>().WithMany().HasForeignKey(x => x.DocumentId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<ManagedDocumentRevision>().WithMany().HasForeignKey(x => x.RevisionId).OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<ManagedDocumentLink>(b =>
        {
            b.ToTable("managed_document_links"); b.HasKey(x => x.Id); b.Property(x => x.ArtifactType).HasMaxLength(60).IsRequired();
            b.Property(x => x.DisplayNumber).HasMaxLength(80).IsRequired(); b.Property(x => x.CanonicalTitle).HasMaxLength(500).IsRequired(); b.Property(x => x.TargetState).HasMaxLength(80).IsRequired();
            b.Property(x => x.TargetReleaseVersion).HasMaxLength(80).IsRequired(); b.Property(x => x.DeepLink).HasMaxLength(1000).IsRequired(); b.Property(x => x.Relationship).HasMaxLength(80).IsRequired(); b.Property(x => x.Provenance).HasMaxLength(80).IsRequired();
            b.Property(x => x.SupersedeReason).HasMaxLength(1000).IsRequired(); b.Property(x => x.SupersededBy).HasMaxLength(100); b.Property(x => x.CreatedBy).HasMaxLength(100).IsRequired();
            b.HasIndex(x => new { x.RevisionId, x.IsCurrent }); b.HasIndex(x => new { x.RevisionId, x.CreatedAt }); b.HasIndex(x => new { x.RevisionId, x.ArtifactType, x.ArtifactId, x.Relationship });
            b.HasIndex(x => new { x.ArtifactType, x.ArtifactId });
            b.HasOne<ManagedDocumentRevision>().WithMany().HasForeignKey(x => x.RevisionId).OnDelete(DeleteBehavior.Cascade);
            b.HasOne<ProjectRecord>().WithMany().HasForeignKey(x => x.TargetProjectId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<SoftwareRelease>().WithMany().HasForeignKey(x => x.TargetReleaseId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<ManagedDocumentLink>().WithMany().HasForeignKey(x => x.SupersededByLinkId).OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<ManagedDocumentEvent>(b =>
        {
            b.ToTable("managed_document_events"); b.HasKey(x => x.Id); b.Property(x => x.EventType).HasMaxLength(100).IsRequired();
            b.Property(x => x.ActorId).HasMaxLength(100).IsRequired(); b.Property(x => x.Detail).HasMaxLength(2000).IsRequired();
            b.HasIndex(x => new { x.DocumentId, x.OccurredAt }); b.HasOne<ManagedDocument>().WithMany().HasForeignKey(x => x.DocumentId).OnDelete(DeleteBehavior.Cascade);
        });
        modelBuilder.Entity<DocumentConnectorGrant>(b =>
        {
            b.ToTable("document_connector_grants"); b.HasKey(x => x.Id); b.Property(x => x.UserName).HasMaxLength(100).IsRequired(); b.Property(x => x.Mode).HasMaxLength(16).IsRequired();
            b.Property(x => x.LaunchTokenHash).HasMaxLength(64).IsRequired(); b.Property(x => x.AccessTokenHash).HasMaxLength(64); b.Property(x => x.RedeemedAt).IsConcurrencyToken(); b.Property(x => x.RevokedAt).IsConcurrencyToken();
            b.Property(x => x.DeploymentId).HasMaxLength(100); b.Property(x => x.Origin).HasMaxLength(500); b.Property(x => x.KeyId).HasMaxLength(100);
            b.Property(x => x.SourceSha256).HasMaxLength(64); b.Property(x => x.DocumentNumber).HasMaxLength(100); b.Property(x => x.RevisionNumber).HasMaxLength(100);
            b.HasIndex(x => x.LaunchTokenHash).IsUnique(); b.HasIndex(x => x.AccessTokenHash).IsUnique(); b.HasIndex(x => x.EditSessionId);
            b.HasIndex(x => new { x.DeploymentId, x.SourceAttachmentId });
            b.HasIndex(x => x.RecoveryWorkspaceId).IsUnique().HasFilter("\"RevokedAt\" IS NULL AND \"RecoveryWorkspaceId\" IS NOT NULL");
            b.HasOne<ManagedDocument>().WithMany().HasForeignKey(x => x.DocumentId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<ManagedDocumentRevision>().WithMany().HasForeignKey(x => x.RevisionId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<ArtifactEditSession>().WithMany().HasForeignKey(x => x.EditSessionId).OnDelete(DeleteBehavior.Restrict);
        });
    }

    public override async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        // Aggregate children use application-assigned GUIDs. EF interprets newly discovered
        // children with set keys as existing unless their append-only state is made explicit.
        foreach (var entry in ChangeTracker.Entries<AuditEvent>().Where(x => x.State == EntityState.Modified)) entry.State = EntityState.Added;
        // A requirement change asks the question the blanket rule was approximating: is this row actually new?
        //
        // The rule above exists because a child discovered with an application-assigned key looks Modified to
        // EF whether it is new or not, and treating a new one as an update loses it. Flipping every modified
        // requirement change to an insert answers that correctly for additions and wrongly for genuine edits,
        // which is why rebasing one onto a later revision failed on its own primary key.
        //
        // Asking the database costs one round trip per modified requirement change, and a requirement change
        // is written far more often than it is edited. The alternative -- replacing the row -- gives it a new
        // identity, and both a reviewer's comment and a verification impact item anchor to the old one, so a
        // rebase would quietly detach an objection from the requirement it was made about.
        foreach (var entry in ChangeTracker.Entries<RequirementChange>().Where(x => x.State == EntityState.Modified))
        {
            var stored = await entry.GetDatabaseValuesAsync(cancellationToken);
            if (stored is null) entry.State = EntityState.Added;
        }
        foreach (var entry in ChangeTracker.Entries<ArtifactFieldDefinition>().Where(x => x.State == EntityState.Modified)) entry.State = EntityState.Added;
        foreach (var cycle in ChangeTracker.Entries<ReviewCycle>().Where(x => x.State == EntityState.Modified && x.Entity.CompletedAt is null && x.Entity.Steps.All(s => s.State != ApprovalStepState.Approved)))
        {
            cycle.State = EntityState.Added;
            foreach (var step in cycle.Entity.Steps) Entry(step).State = EntityState.Added;
        }
        foreach (var entry in ChangeTracker.Entries<BaselineChangeRequestSelection>().Where(x => x.State == EntityState.Modified)) entry.State = EntityState.Added;
        foreach (var entry in ChangeTracker.Entries<BaselineEvent>().Where(x => x.State == EntityState.Modified)) entry.State = EntityState.Added;
        foreach (var entry in ChangeTracker.Entries<ReleaseCampaignEvent>().Where(x => x.State == EntityState.Modified)) entry.State = EntityState.Added;
        foreach (var entry in ChangeTracker.Entries<SystemChangeRequest>())
        {
            if (entry.State == EntityState.Added) entry.Property(x => x.Version).CurrentValue = 1;
            if (entry.State == EntityState.Modified)
                entry.Property(x => x.Version).CurrentValue = entry.Property(x => x.Version).OriginalValue + 1;
        }
        foreach (var entry in ChangeTracker.Entries<RequirementSpecification>())
        {
            if (entry.State == EntityState.Added) entry.Property(x => x.Version).CurrentValue = 1;
            if (entry.State == EntityState.Modified) entry.Property(x => x.Version).CurrentValue = entry.Property(x => x.Version).OriginalValue + 1;
        }
        foreach (var entry in ChangeTracker.Entries<TestProcedure>())
        {
            if (entry.State == EntityState.Added) entry.Property(x => x.Version).CurrentValue = 1;
            if (entry.State == EntityState.Modified) entry.Property(x => x.Version).CurrentValue = entry.Property(x => x.Version).OriginalValue + 1;
        }
        foreach (var entry in ChangeTracker.Entries<RequirementTraceLink>())
        {
            if (entry.State == EntityState.Added) entry.Property(x => x.Version).CurrentValue = 1;
            if (entry.State == EntityState.Modified) entry.Property(x => x.Version).CurrentValue = entry.Property(x => x.Version).OriginalValue + 1;
        }
        foreach (var entry in ChangeTracker.Entries<CandidateBaseline>())
        {
            if (entry.State == EntityState.Added) entry.Property(x => x.Version).CurrentValue = 1;
            if (entry.State == EntityState.Modified) entry.Property(x => x.Version).CurrentValue = entry.Property(x => x.Version).OriginalValue + 1;
        }
        // A comment taken out of its review is a deleted comment, not an ownerless one. Both owner keys are
        // nullable so that exactly one of them can be set, which leaves both relationships optional, and EF's
        // answer to removing an optional child from its collection is to null the key rather than delete the
        // row. The surviving row then fails CK_review_comments_one_owner, so "remove my draft" answers 500.
        // The constraint already says a comment owned by nothing cannot exist; this makes that true instead
        // of fatal, and it holds for both owners rather than for whichever endpoint remembered to say so.
        foreach (var entry in ChangeTracker.Entries<ReviewComment>().Where(x => x.State == EntityState.Modified))
        {
            if (entry.Property(x => x.ReviewCycleId).CurrentValue is null
                && entry.Property(x => x.ManagedDocumentRevisionId).CurrentValue is null)
                entry.State = EntityState.Deleted;
        }
        await RefuseCrossLevelCoverageAsync(cancellationToken);
        await AddLifecycleEventsAsync(cancellationToken);
        await QueueNotificationDeliveriesAsync(cancellationToken);
        return await base.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Refuses a coverage link between a procedure and a requirement at a different level.
    ///
    /// An HLR test procedure exists because it verifies one or more HLRs. Linking one to a System requirement
    /// is not a thing a verification engineer means to do, and the product allowed it — which is how a System
    /// change request came to raise work in the HLR queue: retiring the System requirement stranded the HLR
    /// procedure, and the orphan was routed by the procedure's level onto a System change request. Forbidding
    /// the link removes that whole class of problem rather than routing around it, because a retirement can
    /// then only ever strand procedures of its own level.
    ///
    /// Enforced here rather than in the constructor because this is the one place every write passes through.
    /// The rule was already true of all 1,251 links in the demo database, so this fixes nothing retroactively —
    /// it stops the next one, including from code not yet written.
    /// </summary>
    private async Task RefuseCrossLevelCoverageAsync(CancellationToken ct)
    {
        var added = ChangeTracker.Entries<TestRequirementCoverage>()
            .Where(x => x.State == EntityState.Added)
            .Select(x => x.Entity)
            .ToList();
        if (added.Count == 0) return;

        var procedureRevisionIds = added.Select(x => x.ProcedureRevisionId).Distinct().ToList();
        var requirementRevisionIds = added.Select(x => x.RequirementRevisionId).Distinct().ToList();

        var procedureLevels = await (from revision in TestProcedureRevisions.AsNoTracking()
                                     join procedure in TestProcedures.AsNoTracking()
                                         on revision.ProcedureId equals procedure.Id
                                     where procedureRevisionIds.Contains(revision.Id)
                                     select new { revision.Id, procedure.Level, procedure.BaseNumber })
            .ToDictionaryAsync(x => x.Id, ct);
        var requirementLevels = await (from revision in RequirementRevisions.AsNoTracking()
                                       join artifact in Requirements.AsNoTracking()
                                           on revision.ArtifactId equals artifact.Id
                                       where requirementRevisionIds.Contains(revision.Id)
                                       select new { revision.Id, artifact.Level, artifact.BaseNumber })
            .ToDictionaryAsync(x => x.Id, ct);

        foreach (var link in added)
        {
            // A revision not yet in the database is one being written in this same unit of work. Its level is
            // not knowable here, and refusing on that basis would block legitimate writes; the authoring paths
            // check before proposing.
            if (!procedureLevels.TryGetValue(link.ProcedureRevisionId, out var procedure)) continue;
            if (!requirementLevels.TryGetValue(link.RequirementRevisionId, out var requirement)) continue;
            if (SameLevel(procedure.Level, requirement.Level)) continue;
            throw new DomainException(
                $"{procedure.BaseNumber} is a {procedure.Level} test procedure and cannot verify {requirement.BaseNumber}, which is a {requirement.Level} requirement. A test procedure covers requirements at its own level.");
        }
    }

    /// <summary>The one true correspondence between a procedure's level and a requirement's.</summary>
    private static bool SameLevel(TestProcedureLevel procedure, RequirementLevel requirement) => (procedure, requirement) switch
    {
        (TestProcedureLevel.System, RequirementLevel.System) => true,
        (TestProcedureLevel.HighLevel, RequirementLevel.HighLevel) => true,
        (TestProcedureLevel.LowLevel, RequirementLevel.LowLevel) => true,
        _ => false,
    };

    /// <summary>
    /// Queues an outbound delivery for every notification being written, in the same unit of work.
    ///
    /// This lives here rather than at each of the endpoints that raise a notification, and that is the
    /// whole point: there are several such endpoints today, someone will add another, and a notification
    /// that quietly reaches nobody is indistinguishable from one that was never raised. Attaching to the
    /// save means the delivery cannot be forgotten, and cannot survive a rollback of the work it announces.
    /// </summary>
    private async Task QueueNotificationDeliveriesAsync(CancellationToken ct)
    {
        var raised = ChangeTracker.Entries<UserNotification>()
            .Where(x => x.State == EntityState.Added)
            .Select(x => x.Entity)
            .ToList();
        if (raised.Count == 0) return;
        await new Notifications.NotificationOutbox(this).QueueEmailAsync(raised, DateTimeOffset.UtcNow, ct);
    }

    private async Task AddLifecycleEventsAsync(CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var pending = new List<(Guid ProjectId,string EventType,string AggregateType,Guid AggregateId,object Payload,string Actor)>();
        foreach(var entry in ChangeTracker.Entries<SystemChangeRequest>().Where(x=>x.State is EntityState.Added or EntityState.Modified))
            pending.Add((entry.Entity.ProjectId,"aerolink.change-request.changed","ChangeRequest",entry.Entity.Id,new{entry.Entity.DisplayNumber,state=entry.Entity.State.ToString(),entry.Entity.Version,entry.Entity.TargetReleaseId},entry.Entity.AuditEvents.OrderByDescending(x=>x.OccurredAt).FirstOrDefault()?.ActorId??entry.Entity.AuthorId));
        foreach(var entry in ChangeTracker.Entries<CandidateBaseline>().Where(x=>x.State is EntityState.Added or EntityState.Modified))
            pending.Add((entry.Entity.ProjectId,"aerolink.baseline.changed","CandidateBaseline",entry.Entity.Id,new{entry.Entity.DisplayNumber,state=entry.Entity.State.ToString(),entry.Entity.ReleaseId,entry.Entity.ContentHash},entry.Entity.Events.OrderByDescending(x=>x.OccurredAt).FirstOrDefault()?.ActorId??"aerolink.lifecycle"));
        foreach(var entry in ChangeTracker.Entries<RequirementRevision>().Where(x=>x.State==EntityState.Added))
        {
            var projectId=ChangeTracker.Entries<RequirementArtifact>().FirstOrDefault(x=>x.Entity.Id==entry.Entity.ArtifactId)?.Entity.ProjectId;
            projectId??=await Requirements.AsNoTracking().Where(x=>x.Id==entry.Entity.ArtifactId).Select(x=>(Guid?)x.ProjectId).SingleOrDefaultAsync(ct);
            if(projectId is Guid id)pending.Add((id,"aerolink.requirement.revision-created","RequirementRevision",entry.Entity.Id,new{entry.Entity.ArtifactId,entry.Entity.Revision,state=entry.Entity.State.ToString(),entry.Entity.SourceChangeRequestId,entry.Entity.EffectiveBaselineId},"aerolink.lifecycle"));
        }
        foreach(var entry in ChangeTracker.Entries<ReleaseCampaign>().Where(x=>x.State is EntityState.Added or EntityState.Modified))
            pending.Add((entry.Entity.ProjectId,"aerolink.release-campaign.changed","ReleaseCampaign",entry.Entity.Id,new{state=entry.Entity.State.ToString(),entry.Entity.ReleaseId,entry.Entity.BaselineId,entry.Entity.SoftwareBuildId,entry.Entity.ReleaseHash},entry.Entity.Events.OrderByDescending(x=>x.OccurredAt).FirstOrDefault()?.ActorId??entry.Entity.OwnerId));
        foreach(var entry in ChangeTracker.Entries<SoftwareBuild>().Where(x=>x.State is EntityState.Added or EntityState.Modified))
            pending.Add((entry.Entity.ProjectId,entry.State==EntityState.Added?"aerolink.software-build.recorded":"aerolink.software-build.changed","SoftwareBuild",entry.Entity.Id,new{entry.Entity.BuildNumber,state=entry.Entity.State.ToString(),entry.Entity.ReleaseId,entry.Entity.BaselineId},entry.Entity.RecordedBy));
        foreach(var entry in ChangeTracker.Entries<TestExecution>().Where(x=>x.State==EntityState.Added))
            pending.Add((entry.Entity.ProjectId,"aerolink.test-execution.recorded","TestExecution",entry.Entity.Id,new{outcome=entry.Entity.Outcome.ToString(),entry.Entity.ReleaseId,entry.Entity.ProcedureRevisionId,entry.Entity.SoftwareBuildId,entry.Entity.RetestOfExecutionId,entry.Entity.ExecutedAt},entry.Entity.ExecutedBy));
        if(pending.Count==0)return;
        var projectIds=pending.Select(x=>x.ProjectId).Distinct().ToList();
        var subscriptions=await WebhookSubscriptions.AsNoTracking().Where(x=>projectIds.Contains(x.ProjectId)&&x.IsEnabled).ToListAsync(ct);
        foreach(var item in pending)
        {
            if(ChangeTracker.Entries<IntegrationEvent>().Any(x=>x.State==EntityState.Added&&x.Entity.AggregateId==item.AggregateId&&x.Entity.EventType==item.EventType))continue;
            var payload=JsonSerializer.Serialize(item.Payload,new JsonSerializerOptions(JsonSerializerDefaults.Web));
            var integrationEvent=new IntegrationEvent(item.ProjectId,item.EventType,item.AggregateType,item.AggregateId,payload,item.Actor,now);
            IntegrationEvents.Add(integrationEvent);
            foreach(var subscription in subscriptions.Where(x=>x.ProjectId==item.ProjectId))
            {
                var types=JsonSerializer.Deserialize<string[]>(subscription.EventTypesJson)??[];
                if(types.Any(x=>x=="*"||x.Equals(item.EventType,StringComparison.OrdinalIgnoreCase)))WebhookDeliveries.Add(new WebhookDelivery(item.ProjectId,integrationEvent.Id,subscription.Id,now));
            }
            integrationEvent.MarkDispatched(now);
        }
    }
}
