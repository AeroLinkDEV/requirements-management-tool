using AeroLink.Domain.Baselines;
using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Common;
using AeroLink.Domain.Programs;
using AeroLink.Domain.Requirements;
using AeroLink.Domain.Verification;
using AeroLink.Domain.Traceability;
using AeroLink.Domain.Releases;
using AeroLink.Domain.Identity;
using Microsoft.EntityFrameworkCore;

namespace AeroLink.Infrastructure.Persistence;

public sealed class AeroLinkDbContext(DbContextOptions<AeroLinkDbContext> options) : DbContext(options)
{
    public DbSet<ProgramRecord> Programs => Set<ProgramRecord>();
    public DbSet<ProjectRecord> Projects => Set<ProjectRecord>();
    public DbSet<SoftwareRelease> Releases => Set<SoftwareRelease>();
    public DbSet<SoftwareBuild> SoftwareBuilds => Set<SoftwareBuild>();
    public DbSet<SystemChangeRequest> SystemChangeRequests => Set<SystemChangeRequest>();
    public DbSet<RequirementChange> RequirementChanges => Set<RequirementChange>();
    public DbSet<ReviewCycle> ReviewCycles => Set<ReviewCycle>();
    public DbSet<ApprovalStep> ApprovalSteps => Set<ApprovalStep>();
    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();
    public DbSet<CandidateBaseline> CandidateBaselines => Set<CandidateBaseline>();
    public DbSet<BaselineScrSelection> BaselineSelections => Set<BaselineScrSelection>();
    public DbSet<BaselineEvent> BaselineEvents => Set<BaselineEvent>();
    public DbSet<RequirementArtifact> Requirements => Set<RequirementArtifact>();
    public DbSet<RequirementRevision> RequirementRevisions => Set<RequirementRevision>();
    public DbSet<BaselineRequirementSelection> BaselineRequirements => Set<BaselineRequirementSelection>();
    public DbSet<TestProcedure> TestProcedures => Set<TestProcedure>();
    public DbSet<TestProcedureRevision> TestProcedureRevisions => Set<TestProcedureRevision>();
    public DbSet<TestRequirementCoverage> TestCoverage => Set<TestRequirementCoverage>();
    public DbSet<TestExecution> TestExecutions => Set<TestExecution>();
    public DbSet<RequirementTraceLink> RequirementTraces => Set<RequirementTraceLink>();
    public DbSet<ControlledDocument> ControlledDocuments => Set<ControlledDocument>();
    public DbSet<ReleaseCampaign> ReleaseCampaigns => Set<ReleaseCampaign>();
    public DbSet<ReleaseApproval> ReleaseApprovals => Set<ReleaseApproval>();
    public DbSet<ReleaseCampaignEvent> ReleaseCampaignEvents => Set<ReleaseCampaignEvent>();
    public DbSet<ChangeImpactDisposition> ImpactDispositions => Set<ChangeImpactDisposition>();
    public DbSet<EvidenceRecord> EvidenceRecords => Set<EvidenceRecord>();
    public DbSet<TestExecutionEvidence> TestExecutionEvidence => Set<TestExecutionEvidence>();
    public DbSet<UserAccount> UserAccounts => Set<UserAccount>();
    public DbSet<ProgramMembership> ProgramMemberships => Set<ProgramMembership>();
    public DbSet<UserSession> UserSessions => Set<UserSession>();
    public DbSet<RoleDelegation> RoleDelegations => Set<RoleDelegation>();
    public DbSet<ElectronicSignature> ElectronicSignatures => Set<ElectronicSignature>();
    public DbSet<SecurityAuditEvent> SecurityAuditEvents => Set<SecurityAuditEvent>();
    public DbSet<ArtifactSchemaDefinition> ArtifactSchemas => Set<ArtifactSchemaDefinition>();
    public DbSet<ArtifactFieldDefinition> ArtifactFieldDefinitions => Set<ArtifactFieldDefinition>();
    public DbSet<RequirementSpecification> RequirementSpecifications => Set<RequirementSpecification>();
    public DbSet<SpecificationNode> SpecificationNodes => Set<SpecificationNode>();
    public DbSet<RequirementRevisionProfile> RequirementRevisionProfiles => Set<RequirementRevisionProfile>();
    public DbSet<ArtifactComment> ArtifactComments => Set<ArtifactComment>();
    public DbSet<SavedRequirementView> SavedRequirementViews => Set<SavedRequirementView>();
    public DbSet<EnterpriseOperationJob> EnterpriseOperationJobs => Set<EnterpriseOperationJob>();
    public DbSet<RequirementInterchangeJob> RequirementInterchangeJobs => Set<RequirementInterchangeJob>();
    public DbSet<ArtifactWatch> ArtifactWatches => Set<ArtifactWatch>();
    public DbSet<ArtifactAssignment> ArtifactAssignments => Set<ArtifactAssignment>();
    public DbSet<UserNotification> UserNotifications => Set<UserNotification>();
    public DbSet<RequirementImportMapping> RequirementImportMappings => Set<RequirementImportMapping>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
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
        modelBuilder.Entity<SoftwareRelease>(b =>
        {
            b.ToTable("software_releases"); b.HasKey(x => x.Id);
            b.Property(x => x.Version).HasMaxLength(40).IsRequired();
            b.HasIndex(x => new { x.ProjectId, x.Version }).IsUnique();
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
            b.Property(x => x.AuthorId).HasMaxLength(100).IsRequired();
            b.Property(x => x.State).HasConversion<string>().HasMaxLength(40);
            b.Property(x => x.Type).HasConversion<string>().HasMaxLength(30);
            b.Property(x => x.Version).IsConcurrencyToken();
            b.Ignore(x => x.DisplayNumber); b.Ignore(x => x.ActiveReviewCycle);
            b.HasIndex(x => new { x.ProjectId, x.BaseNumber, x.Revision }).IsUnique();
            b.HasIndex(x => new { x.ProjectId, x.UpdatedAt });
            b.HasIndex(x => new { x.ProjectId, x.State });
            b.HasMany(x => x.RequirementChanges).WithOne().HasForeignKey(x => x.ScrId).OnDelete(DeleteBehavior.Cascade);
            b.HasMany(x => x.ReviewCycles).WithOne().HasForeignKey(x => x.ScrId).OnDelete(DeleteBehavior.Cascade);
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
            b.Property(x => x.RichText).HasMaxLength(32000);
            b.Property(x => x.AttributesJson).IsRequired();
            b.Property(x => x.ImpactDispositionJson).IsRequired();
            b.Ignore(x => x.DisplayNumber);
            b.HasIndex(x => new { x.ScrId, x.BaseNumber, x.Revision }).IsUnique();
            b.HasIndex(x => x.BaseNumber);
        });
        modelBuilder.Entity<ReviewCycle>(b =>
        {
            b.ToTable("review_cycles"); b.HasKey(x => x.Id);
            b.Property(x => x.SnapshotHash).HasMaxLength(64).IsRequired();
            b.Property(x => x.State).HasConversion<string>().HasMaxLength(40);
            b.Property(x => x.ClosureReason).HasMaxLength(2000);
            b.Ignore(x => x.ActivePosition);
            b.HasIndex(x => new { x.ScrId, x.Sequence }).IsUnique();
            b.HasMany(x => x.Steps).WithOne().HasForeignKey(x => x.ReviewCycleId).OnDelete(DeleteBehavior.Cascade);
        });
        modelBuilder.Entity<ApprovalStep>(b =>
        {
            b.ToTable("approval_steps"); b.HasKey(x => x.Id);
            b.Property(x => x.ApproverId).HasMaxLength(100).IsRequired();
            b.Property(x => x.ApproverName).HasMaxLength(200).IsRequired();
            b.Property(x => x.State).HasConversion<string>().HasMaxLength(30);
            b.HasIndex(x => new { x.ReviewCycleId, x.Position }).IsUnique();
        });
        modelBuilder.Entity<AuditEvent>(b =>
        {
            b.ToTable("audit_events"); b.HasKey(x => x.Id);
            b.Property(x => x.EventType).HasMaxLength(100).IsRequired();
            b.Property(x => x.ActorId).HasMaxLength(100).IsRequired();
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
            b.Ignore(x => x.DisplayNumber);
            b.HasIndex(x => new { x.ProjectId, x.BaseNumber, x.Revision }).IsUnique();
            b.HasIndex(x => new { x.ProjectId, x.ReleaseId, x.State });
            b.HasMany(x => x.Selections).WithOne().HasForeignKey(x => x.BaselineId).OnDelete(DeleteBehavior.Cascade);
            b.HasMany(x => x.Events).WithOne().HasForeignKey(x => x.BaselineId).OnDelete(DeleteBehavior.Cascade);
        });
        modelBuilder.Entity<BaselineScrSelection>(b =>
        {
            b.ToTable("baseline_scr_selections"); b.HasKey(x => x.Id);
            b.Property(x => x.ScrDisplayNumber).HasMaxLength(40).IsRequired();
            b.HasIndex(x => new { x.BaselineId, x.ScrId }).IsUnique();
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
            b.HasIndex(x => x.SourceScrId); b.HasIndex(x => x.EffectiveBaselineId);
            b.HasOne<RequirementArtifact>().WithMany().HasForeignKey(x => x.ArtifactId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<SystemChangeRequest>().WithMany().HasForeignKey(x => x.SourceScrId).OnDelete(DeleteBehavior.Restrict);
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
            b.HasIndex(x => new { x.ProjectId, x.BaseNumber }).IsUnique();
            b.HasOne<ProjectRecord>().WithMany().HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<TestProcedureRevision>(b =>
        {
            b.ToTable("test_procedure_revisions"); b.HasKey(x => x.Id); b.Property(x => x.Objective).HasMaxLength(4000).IsRequired();
            b.Property(x => x.Preconditions).HasMaxLength(8000); b.Property(x => x.Steps).HasMaxLength(16000).IsRequired();
            b.Property(x => x.ExpectedResult).HasMaxLength(8000).IsRequired(); b.Property(x => x.State).HasConversion<string>().HasMaxLength(30);
            b.Property(x => x.AuthorId).HasMaxLength(100).IsRequired(); b.HasIndex(x => new { x.ProcedureId, x.Revision }).IsUnique();
            b.HasOne<TestProcedure>().WithMany().HasForeignKey(x => x.ProcedureId).OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<TestRequirementCoverage>(b =>
        {
            b.ToTable("test_requirement_coverage"); b.HasKey(x => x.Id);
            b.HasIndex(x => new { x.ProcedureRevisionId, x.RequirementRevisionId }).IsUnique(); b.HasIndex(x => x.RequirementRevisionId);
            b.HasOne<TestProcedureRevision>().WithMany().HasForeignKey(x => x.ProcedureRevisionId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<RequirementRevision>().WithMany().HasForeignKey(x => x.RequirementRevisionId).OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<TestExecution>(b =>
        {
            b.ToTable("test_executions"); b.HasKey(x => x.Id); b.Property(x => x.Outcome).HasConversion<string>().HasMaxLength(30);
            b.Property(x => x.ExecutedBy).HasMaxLength(100).IsRequired(); b.Property(x => x.Configuration).HasMaxLength(4000);
            b.Property(x => x.Determination).HasMaxLength(8000).IsRequired(); b.Property(x => x.EvidenceReference).HasMaxLength(2000);
            b.HasIndex(x => new { x.ProjectId, x.ExecutedAt }); b.HasIndex(x => x.ProcedureRevisionId); b.HasIndex(x => x.SoftwareBuildId);
            b.HasOne<ProjectRecord>().WithMany().HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<TestProcedureRevision>().WithMany().HasForeignKey(x => x.ProcedureRevisionId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<SoftwareBuild>().WithMany().HasForeignKey(x => x.SoftwareBuildId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<TestExecution>().WithMany().HasForeignKey(x => x.RetestOfExecutionId).OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<RequirementTraceLink>(b =>
        {
            b.ToTable("requirement_trace_links"); b.HasKey(x => x.Id); b.Property(x => x.Type).HasConversion<string>().HasMaxLength(30);
            b.Property(x => x.Rationale).HasMaxLength(2000); b.HasIndex(x => new { x.SourceRevisionId, x.TargetRevisionId, x.Type }).IsUnique();
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
        modelBuilder.Entity<ReleaseCampaign>(b =>
        {
            b.ToTable("release_campaigns"); b.HasKey(x => x.Id); b.Property(x => x.Name).HasMaxLength(300).IsRequired(); b.Property(x => x.OwnerId).HasMaxLength(100).IsRequired();
            b.Property(x => x.State).HasConversion<string>().HasMaxLength(30); b.Property(x => x.ReleaseHash).HasMaxLength(64); b.HasIndex(x => new { x.ProjectId, x.ReleaseId }).IsUnique();
            b.HasOne<ProjectRecord>().WithMany().HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Restrict); b.HasOne<SoftwareRelease>().WithMany().HasForeignKey(x => x.ReleaseId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<CandidateBaseline>().WithMany().HasForeignKey(x => x.BaselineId).OnDelete(DeleteBehavior.Restrict); b.HasOne<SoftwareBuild>().WithMany().HasForeignKey(x => x.SoftwareBuildId).OnDelete(DeleteBehavior.Restrict);
            b.HasMany(x => x.Approvals).WithOne().HasForeignKey(x => x.CampaignId).OnDelete(DeleteBehavior.Restrict); b.HasMany(x => x.Events).WithOne().HasForeignKey(x => x.CampaignId).OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<ReleaseApproval>(b =>
        {
            b.ToTable("release_approvals"); b.HasKey(x => x.Id); b.Property(x => x.ApproverId).HasMaxLength(100).IsRequired(); b.Property(x => x.ApproverName).HasMaxLength(200).IsRequired(); b.Property(x => x.State).HasConversion<string>().HasMaxLength(30); b.HasIndex(x => new { x.CampaignId, x.Position }).IsUnique();
        });
        modelBuilder.Entity<ReleaseCampaignEvent>(b =>
        {
            b.ToTable("release_campaign_events"); b.HasKey(x => x.Id); b.Property(x => x.EventType).HasMaxLength(100).IsRequired(); b.Property(x => x.ActorId).HasMaxLength(100).IsRequired(); b.Property(x => x.Detail).HasMaxLength(4000).IsRequired(); b.HasIndex(x => new { x.CampaignId, x.OccurredAt });
        });
        modelBuilder.Entity<ChangeImpactDisposition>(b =>
        {
            b.ToTable("change_impact_dispositions"); b.HasKey(x => x.Id); b.Property(x => x.Kind).HasConversion<string>().HasMaxLength(30); b.Property(x => x.State).HasConversion<string>().HasMaxLength(30);
            b.Property(x => x.ArtifactReference).HasMaxLength(100).IsRequired(); b.Property(x => x.Description).HasMaxLength(2000).IsRequired(); b.Property(x => x.Rationale).HasMaxLength(2000); b.Property(x => x.DispositionedBy).HasMaxLength(100);
            b.HasIndex(x => new { x.CampaignId, x.ScrId, x.Kind, x.ArtifactReference }).IsUnique(); b.HasOne<ReleaseCampaign>().WithMany().HasForeignKey(x => x.CampaignId).OnDelete(DeleteBehavior.Restrict); b.HasOne<SystemChangeRequest>().WithMany().HasForeignKey(x => x.ScrId).OnDelete(DeleteBehavior.Restrict);
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
            b.ToTable("program_memberships"); b.HasKey(x => x.Id); b.Property(x => x.Role).HasConversion<string>().HasMaxLength(40); b.Property(x => x.GrantedBy).HasMaxLength(100).IsRequired(); b.HasIndex(x => new { x.UserId, x.ProgramId, x.Role }).IsUnique();
            b.HasOne<UserAccount>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict); b.HasOne<ProgramRecord>().WithMany().HasForeignKey(x => x.ProgramId).OnDelete(DeleteBehavior.Restrict);
        });
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
            b.ToTable("electronic_signatures"); b.HasKey(x => x.Id); b.Property(x => x.UserName).HasMaxLength(100).IsRequired(); b.Property(x => x.DisplayName).HasMaxLength(200).IsRequired(); b.Property(x => x.ArtifactType).HasMaxLength(60).IsRequired(); b.Property(x => x.ArtifactRevision).HasMaxLength(80).IsRequired(); b.Property(x => x.Action).HasMaxLength(80).IsRequired(); b.Property(x => x.Meaning).HasMaxLength(1000).IsRequired(); b.Property(x => x.ContentHash).HasMaxLength(64).IsRequired(); b.Property(x => x.IpAddress).HasMaxLength(100); b.HasIndex(x => new { x.ArtifactType, x.ArtifactId, x.SignedAt }); b.HasIndex(x => new { x.UserId, x.SignedAt });
        });
        modelBuilder.Entity<SecurityAuditEvent>(b =>
        {
            b.ToTable("security_audit_events"); b.HasKey(x => x.Id); b.Property(x => x.EventType).HasMaxLength(100).IsRequired(); b.Property(x => x.ActorId).HasMaxLength(100).IsRequired(); b.Property(x => x.Target).HasMaxLength(300).IsRequired(); b.Property(x => x.Outcome).HasMaxLength(30).IsRequired(); b.Property(x => x.Detail).HasMaxLength(4000).IsRequired(); b.Property(x => x.IpAddress).HasMaxLength(100); b.HasIndex(x => x.OccurredAt); b.HasIndex(x => new { x.ActorId, x.OccurredAt });
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
            b.ToTable("requirement_specifications"); b.HasKey(x=>x.Id); b.Property(x=>x.DocumentNumber).HasMaxLength(40).IsRequired(); b.Property(x=>x.Title).HasMaxLength(300).IsRequired(); b.Property(x=>x.Level).HasMaxLength(40).IsRequired(); b.Property(x=>x.Description).HasMaxLength(4000); b.Property(x=>x.CreatedBy).HasMaxLength(100).IsRequired(); b.HasIndex(x=>new{x.ProjectId,x.DocumentNumber}).IsUnique(); b.HasOne<ProjectRecord>().WithMany().HasForeignKey(x=>x.ProjectId).OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<SpecificationNode>(b =>
        {
            b.ToTable("specification_nodes"); b.HasKey(x=>x.Id); b.Property(x=>x.Type).HasConversion<string>().HasMaxLength(30); b.Property(x=>x.Heading).HasMaxLength(500); b.Property(x=>x.CreatedBy).HasMaxLength(100).IsRequired(); b.HasIndex(x=>new{x.SpecificationId,x.ParentId,x.Position}).IsUnique(); b.HasIndex(x=>x.RequirementArtifactId); b.HasOne<RequirementSpecification>().WithMany().HasForeignKey(x=>x.SpecificationId).OnDelete(DeleteBehavior.Cascade); b.HasOne<SpecificationNode>().WithMany().HasForeignKey(x=>x.ParentId).OnDelete(DeleteBehavior.Restrict); b.HasOne<RequirementArtifact>().WithMany().HasForeignKey(x=>x.RequirementArtifactId).OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<RequirementRevisionProfile>(b =>
        {
            b.ToTable("requirement_revision_profiles"); b.HasKey(x=>x.Id); b.Property(x=>x.RichText); b.Property(x=>x.AttributesJson).IsRequired(); b.Property(x=>x.TagsJson).IsRequired(); b.Property(x=>x.UpdatedBy).HasMaxLength(100).IsRequired(); b.HasIndex(x=>x.RevisionId).IsUnique(); b.HasOne<RequirementRevision>().WithMany().HasForeignKey(x=>x.RevisionId).OnDelete(DeleteBehavior.Restrict); b.HasOne<ArtifactSchemaDefinition>().WithMany().HasForeignKey(x=>x.SchemaId).OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<ArtifactComment>(b =>
        {
            b.ToTable("artifact_comments"); b.HasKey(x=>x.Id); b.Property(x=>x.ArtifactType).HasMaxLength(60).IsRequired(); b.Property(x=>x.Body).HasMaxLength(8000).IsRequired(); b.Property(x=>x.MentionsJson).IsRequired(); b.Property(x=>x.State).HasConversion<string>().HasMaxLength(30); b.Property(x=>x.CreatedBy).HasMaxLength(100).IsRequired(); b.Property(x=>x.ResolvedBy).HasMaxLength(100); b.Property(x=>x.Disposition).HasMaxLength(4000); b.HasIndex(x=>new{x.ProjectId,x.ArtifactType,x.ArtifactId,x.CreatedAt}); b.HasOne<ArtifactComment>().WithMany().HasForeignKey(x=>x.ParentCommentId).OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<SavedRequirementView>(b =>
        {
            b.ToTable("saved_requirement_views"); b.HasKey(x=>x.Id); b.Property(x=>x.Name).HasMaxLength(200).IsRequired(); b.Property(x=>x.QueryJson).IsRequired(); b.Property(x=>x.ColumnsJson).IsRequired(); b.HasIndex(x=>new{x.ProjectId,x.OwnerId,x.Name}).IsUnique(); b.HasOne<UserAccount>().WithMany().HasForeignKey(x=>x.OwnerId).OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<EnterpriseOperationJob>(b =>
        {
            b.ToTable("enterprise_operation_jobs"); b.HasKey(x=>x.Id); b.Property(x=>x.JobType).HasMaxLength(80).IsRequired(); b.Property(x=>x.RequestJson).IsRequired(); b.Property(x=>x.ResultJson).IsRequired(); b.Property(x=>x.State).HasConversion<string>().HasMaxLength(30); b.Property(x=>x.CreatedBy).HasMaxLength(100).IsRequired(); b.HasIndex(x=>new{x.ProjectId,x.CreatedAt});
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
        modelBuilder.Entity<RequirementImportMapping>(b =>
        {
            b.ToTable("requirement_import_mappings");b.HasKey(x=>x.Id);b.Property(x=>x.Name).HasMaxLength(200).IsRequired();b.Property(x=>x.MappingJson).IsRequired();b.Property(x=>x.CreatedBy).HasMaxLength(100).IsRequired();b.HasIndex(x=>new{x.ProjectId,x.Name}).IsUnique();
        });
    }

    public override async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        // Aggregate children use application-assigned GUIDs. EF interprets newly discovered
        // children with set keys as existing unless their append-only state is made explicit.
        foreach (var entry in ChangeTracker.Entries<AuditEvent>().Where(x => x.State == EntityState.Modified)) entry.State = EntityState.Added;
        foreach (var entry in ChangeTracker.Entries<RequirementChange>().Where(x => x.State == EntityState.Modified)) entry.State = EntityState.Added;
        foreach (var cycle in ChangeTracker.Entries<ReviewCycle>().Where(x => x.State == EntityState.Modified && x.Entity.CompletedAt is null && x.Entity.Steps.All(s => s.State != ApprovalStepState.Approved)))
        {
            cycle.State = EntityState.Added;
            foreach (var step in cycle.Entity.Steps) Entry(step).State = EntityState.Added;
        }
        foreach (var entry in ChangeTracker.Entries<BaselineScrSelection>().Where(x => x.State == EntityState.Modified)) entry.State = EntityState.Added;
        foreach (var entry in ChangeTracker.Entries<BaselineEvent>().Where(x => x.State == EntityState.Modified)) entry.State = EntityState.Added;
        foreach (var entry in ChangeTracker.Entries<ReleaseCampaignEvent>().Where(x => x.State == EntityState.Modified)) entry.State = EntityState.Added;
        foreach (var entry in ChangeTracker.Entries<SystemChangeRequest>())
        {
            if (entry.State == EntityState.Added) entry.Property(x => x.Version).CurrentValue = 1;
            if (entry.State == EntityState.Modified)
                entry.Property(x => x.Version).CurrentValue = entry.Property(x => x.Version).OriginalValue + 1;
        }
        return await base.SaveChangesAsync(cancellationToken);
    }
}
