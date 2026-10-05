using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Common;
using AeroLink.Domain.Documents;
using AeroLink.Domain.Notifications;
using AeroLink.Domain.Requirements;
using AeroLink.Domain.Verification;
using Microsoft.EntityFrameworkCore;

namespace AeroLink.Infrastructure.Persistence;

internal static class NotificationModelConfiguration
{
    internal static void Apply(ModelBuilder model)
    {
        model.Entity<NotificationContext>(b =>
        {
            b.ToTable("notification_contexts", table => table.HasCheckConstraint("CK_notification_contexts_typed_source", TypedSourceConstraint())); b.HasKey(x => x.NotificationId);
            b.Property(x => x.SourceFamily).HasConversion<string>().HasMaxLength(40);
            b.Property(x => x.Recipient).HasMaxLength(100); b.Property(x => x.EventType).HasMaxLength(60);
            b.Property(x => x.Identifier).HasMaxLength(100); b.Property(x => x.Stage).HasMaxLength(120);
            b.Property(x => x.SnapshotHash).HasMaxLength(64);
            b.HasIndex(x => new { x.ProjectId, x.SourceFamily, x.SourceId, x.EventType, x.Recipient }).IsUnique();
            b.HasOne<UserNotification>().WithOne(x => x.Context).HasForeignKey<NotificationContext>(x => x.NotificationId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<SystemChangeRequest>().WithMany().HasForeignKey(x => x.ChangeRequestId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<TestChangeReview>().WithMany().HasForeignKey(x => x.TestChangeReviewId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<ReviewCycle>().WithMany().HasForeignKey(x => x.ReviewCycleId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<ApprovalStep>().WithMany().HasForeignKey(x => new { x.ApprovalStepId, x.ReviewCycleId }).HasPrincipalKey(x => new { x.Id, x.ReviewCycleId }).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<AuditEvent>().WithMany().HasForeignKey(x => x.AuditEventId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<ManagedDocument>().WithMany().HasForeignKey(x => x.DocumentId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<ManagedDocumentRevision>().WithMany().HasForeignKey(x => x.DocumentRevisionId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<ManagedDocumentReviewStep>().WithMany().HasForeignKey(x => new { x.DocumentStepId, x.DocumentRevisionId }).HasPrincipalKey(x => new { x.Id, x.RevisionId }).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<ManagedDocumentEvent>().WithMany().HasForeignKey(x => new { x.DocumentEventId, x.DocumentId }).HasPrincipalKey(x => new { x.Id, x.DocumentId }).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<ManagedDocumentAssignment>().WithMany().HasForeignKey(x => new { x.DocumentAssignmentId, x.DocumentId }).HasPrincipalKey(x => new { x.Id, x.DocumentId }).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<ArtifactAssignment>().WithMany().HasForeignKey(x => x.ArtifactAssignmentId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<NotificationOperation>().WithMany().HasForeignKey(x => x.OperationId).OnDelete(DeleteBehavior.Restrict);
        });
        model.Entity<NotificationSettingsRevision>(b =>
        {
            b.ToTable("notification_settings_revisions"); b.HasKey(x => x.Id);
            b.Property(x => x.InstallationId).HasMaxLength(100); b.Property(x => x.Mode).HasConversion<string>().HasMaxLength(30);
            b.HasIndex(x => new { x.InstallationId, x.Version }).IsUnique();
        });
        model.Entity<NotificationInstallationState>(b =>
        {
            b.ToTable("notification_installation_states"); b.HasKey(x => x.InstallationId);
            b.Property(x => x.InstallationId).HasMaxLength(100); b.Property(x => x.Version).IsConcurrencyToken();
            b.HasOne<NotificationSettingsRevision>().WithMany().HasForeignKey(x => x.SettingsRevisionId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<NotificationAdmissionEpoch>().WithMany().HasForeignKey(x => x.AdmissionEpochId).OnDelete(DeleteBehavior.Restrict);
        });
        model.Entity<NotificationAdmissionEpoch>(b =>
        {
            b.ToTable("notification_admission_epochs"); b.HasKey(x => x.Id);
            b.Property(x => x.InstallationId).HasMaxLength(100); b.Property(x => x.Mode).HasConversion<string>().HasMaxLength(30);
            b.HasOne<NotificationSettingsRevision>().WithMany().HasForeignKey(x => x.SettingsRevisionId).OnDelete(DeleteBehavior.Restrict);
        });
        model.Entity<NotificationDeliveryGeneration>(b =>
        {
            b.ToTable("notification_delivery_generations"); b.HasKey(x => x.Id);
            b.Property(x => x.State).HasConversion<string>().HasMaxLength(40); b.Property(x => x.Mode).HasConversion<string>().HasMaxLength(30);
            b.Property(x => x.Version).IsConcurrencyToken(); b.Property(x => x.SafeCode).HasMaxLength(60);
            b.HasIndex(x => x.MessageId).IsUnique(); b.HasIndex(x => new { x.State, x.DueTicks }); b.HasIndex(x => x.DeliveryId);
            b.HasIndex(x => x.InitialDeliveryId).IsUnique();
            b.HasOne<NotificationDelivery>().WithMany().HasForeignKey(x => x.DeliveryId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<NotificationAdmissionEpoch>().WithMany().HasForeignKey(x => x.AdmissionEpochId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<NotificationAdmissionEpoch>().WithMany().HasForeignKey(x => x.OriginalAdmissionEpochId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<NotificationSettingsRevision>().WithMany().HasForeignKey(x => x.SettingsRevisionId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<NotificationDeliveryGeneration>().WithMany().HasForeignKey(x => x.PredecessorId).OnDelete(DeleteBehavior.Restrict);
        });
        model.Entity<NotificationPhysicalAttempt>(b =>
        {
            b.ToTable("notification_physical_attempts"); b.HasKey(x => x.Id);
            b.Property(x => x.Outcome).HasConversion<string>().HasMaxLength(40); b.Property(x => x.SafeCode).HasMaxLength(60);
            b.HasIndex(x => x.ClaimToken).IsUnique(); b.HasIndex(x => x.GenerationId);
            b.HasOne<NotificationDeliveryGeneration>().WithMany().HasForeignKey(x => x.GenerationId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<NotificationSettingsRevision>().WithMany().HasForeignKey(x => x.SettingsRevisionId).OnDelete(DeleteBehavior.Restrict);
        });
        model.Entity<NotificationOperation>(b =>
        {
            b.ToTable("notification_operations"); b.HasKey(x => x.Id);
            b.Property(x => x.InstallationId).HasMaxLength(100); b.Property(x => x.Actor).HasMaxLength(100);
            b.Property(x => x.Family).HasMaxLength(40); b.Property(x => x.PayloadHash).HasMaxLength(1024);
            b.HasIndex(x => new { x.InstallationId, x.Actor, x.Family, x.OperationKey }).IsUnique();
        });
        model.Entity<NotificationPreferenceConfirmation>(b =>
        {
            b.ToTable("notification_preference_confirmations"); b.HasKey(x => x.ChallengeHash);
            b.Property(x => x.ChallengeHash).HasMaxLength(64);
        });
        model.Entity<NotificationDelivery>().HasIndex(x => new { x.BoundNotificationId, x.Channel }).IsUnique();
        model.Entity<ManagedDocument>().HasOne<ManagedDocumentAssignment>().WithMany().HasForeignKey(x => x.CurrentStewardAssignmentId).OnDelete(DeleteBehavior.Restrict);
        model.Entity<ManagedDocumentRevision>().HasOne<ManagedDocumentAssignment>().WithMany().HasForeignKey(x => x.CurrentResponsibleAssignmentId).OnDelete(DeleteBehavior.Restrict);
    }
    private static string TypedSourceConstraint()
    {
        var all = new[] { "ChangeRequestId", "TestChangeReviewId", "ReviewCycleId", "ApprovalStepId", "AuditEventId", "DocumentId", "DocumentRevisionId", "DocumentStepId", "DocumentEventId", "DocumentAssignmentId", "ArtifactAssignmentId", "OperationId" };
        var variants = new (string Family, string Source, string[] Required, string[] Optional)[]
        {
            ("CRStep", "ApprovalStepId", ["ChangeRequestId", "ReviewCycleId", "ApprovalStepId"], []),
            ("TCRStep", "ApprovalStepId", ["TestChangeReviewId", "ReviewCycleId", "ApprovalStepId"], []),
            ("DocStep", "DocumentStepId", ["DocumentId", "DocumentRevisionId", "DocumentStepId"], []),
            ("CRAudit", "AuditEventId", ["ChangeRequestId", "ReviewCycleId", "ApprovalStepId", "AuditEventId"], []),
            ("DocEvent", "DocumentEventId", ["DocumentId", "DocumentRevisionId", "DocumentStepId", "DocumentEventId"], []),
            ("RequirementAssignment", "ArtifactAssignmentId", ["ArtifactAssignmentId"], []),
            ("DocumentAssignment", "DocumentAssignmentId", ["DocumentId", "DocumentAssignmentId"], ["DocumentRevisionId"]),
            ("DiagnosticOperation", "OperationId", ["OperationId"], []),
        };
        return "\"SourceSchemaVersion\" = 1 AND (" + string.Join(" OR ", variants.Select(v =>
            "(\"SourceFamily\" = '" + v.Family + "' AND \"SourceId\" = \"" + v.Source + "\" AND "
            + string.Join(" AND ", all.Where(x => !v.Optional.Contains(x)).Select(x => "\"" + x + "\" IS " + (v.Required.Contains(x) ? "NOT NULL" : "NULL"))) + ")")) + ")";
    }

}
