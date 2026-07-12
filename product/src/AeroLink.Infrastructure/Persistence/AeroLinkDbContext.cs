using AeroLink.Domain.Baselines;
using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Common;
using AeroLink.Domain.Programs;
using Microsoft.EntityFrameworkCore;

namespace AeroLink.Infrastructure.Persistence;

public sealed class AeroLinkDbContext(DbContextOptions<AeroLinkDbContext> options) : DbContext(options)
{
    public DbSet<ProgramRecord> Programs => Set<ProgramRecord>();
    public DbSet<ProjectRecord> Projects => Set<ProjectRecord>();
    public DbSet<SoftwareRelease> Releases => Set<SoftwareRelease>();
    public DbSet<SystemChangeRequest> SystemChangeRequests => Set<SystemChangeRequest>();
    public DbSet<RequirementChange> RequirementChanges => Set<RequirementChange>();
    public DbSet<ReviewCycle> ReviewCycles => Set<ReviewCycle>();
    public DbSet<ApprovalStep> ApprovalSteps => Set<ApprovalStep>();
    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();
    public DbSet<CandidateBaseline> CandidateBaselines => Set<CandidateBaseline>();
    public DbSet<BaselineScrSelection> BaselineSelections => Set<BaselineScrSelection>();

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
            b.Ignore(x => x.DisplayNumber); b.Ignore(x => x.ActiveReviewCycle);
            b.HasIndex(x => new { x.BaseNumber, x.Revision }).IsUnique();
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
            b.Ignore(x => x.DisplayNumber);
            b.HasIndex(x => new { x.ScrId, x.BaseNumber, x.Revision }).IsUnique();
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
            b.Ignore(x => x.DisplayNumber); b.Ignore(x => x.AuditEvents);
            b.HasIndex(x => new { x.BaseNumber, x.Revision }).IsUnique();
            b.HasMany(x => x.Selections).WithOne().HasForeignKey(x => x.BaselineId).OnDelete(DeleteBehavior.Cascade);
        });
        modelBuilder.Entity<BaselineScrSelection>(b =>
        {
            b.ToTable("baseline_scr_selections"); b.HasKey(x => x.Id);
            b.Property(x => x.ScrDisplayNumber).HasMaxLength(40).IsRequired();
            b.HasIndex(x => new { x.BaselineId, x.ScrId }).IsUnique();
        });
    }
}
