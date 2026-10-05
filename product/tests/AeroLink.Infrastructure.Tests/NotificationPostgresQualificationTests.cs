using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Notifications;
using AeroLink.Domain.Programs;
using AeroLink.Domain.Requirements;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace AeroLink.Infrastructure.Tests;

// Primary provider owner: EnsureCreated/SQLite cannot prove upgrade preservation, triggers or unique claims.
[Trait("Category", "PostgresQualification")]
public sealed class NotificationPostgresQualificationTests
{
    [DisposablePostgresFact]
    public async Task Upgrade_preserves_legacy_duplicates_and_never_invents_source_or_admission()
    {
        await using var database = await DisposablePostgresDatabase.CreateAsync("notification_upgrade");
        await using var db = Database(database.ConnectionString);
        await db.Database.GetService<IMigrator>().MigrateAsync("20261003032322_AddProblemReportImportRecovery");
        var now = DateTimeOffset.UtcNow;
        var notice = new UserNotification(Guid.NewGuid(), "reviewer", "ReviewActivated", "Historical", "Historical", "", null, now);
        var first = new NotificationDelivery(notice.Id, NotificationChannel.Email, "reviewer", "old@example.test", now);
        var duplicate = new NotificationDelivery(notice.Id, NotificationChannel.Email, "reviewer", "old@example.test", now);
        var accepted = new NotificationDelivery(notice.Id, NotificationChannel.Email, "reviewer", "old@example.test", now); accepted.MarkSent(now);
        db.AddRange(notice, first, duplicate, accepted);
        await PredecessorSchemaRows.InsertTrackedAsync(db); db.ChangeTracker.Clear();
        await db.Database.MigrateAsync();
        var rows = await db.NotificationDeliveries.AsNoTracking().OrderBy(x => x.Id).ToListAsync();
        Assert.Equal(3, rows.Count); Assert.Equal(2, rows.Count(x => x.State == NotificationDeliveryState.LegacyUnbound));
        Assert.Equal(NotificationDeliveryState.Sent, rows.Single(x => x.Id == accepted.Id).State);
        Assert.All(rows, x => { Assert.Null(x.BoundNotificationId); Assert.Null(x.AdmissionEpochId); Assert.Equal("old@example.test", x.Address); });
        Assert.Empty(await db.NotificationContexts.ToListAsync()); Assert.Empty(await db.NotificationDeliveryGenerations.ToListAsync());
    }

    [DisposablePostgresFact]
    public async Task Provider_rejects_cross_source_context_and_changes_to_committed_receipts()
    {
        await using var database = await DisposablePostgresDatabase.CreateAsync("notification_integrity");
        await using var db = Database(database.ConnectionString); await db.Database.MigrateAsync();
        var now = DateTimeOffset.UtcNow; var program = new ProgramRecord("Notification qualification", "NQ");
        var project = new ProjectRecord(program.Id, "Notification qualification", "Qualification");
        var artifact = new RequirementArtifact(project.Id, "SR-99001", RequirementLevel.System, now);
        var task = new ArtifactAssignment(project.Id, "Requirement", artifact.Id, null, "reviewer", "Task", "", null, "author", now);
        var notice = new UserNotification(project.Id, "reviewer", "RequirementAssignment", "Task", "", "", artifact.Id, now);
        notice.BindContext(NotificationContext.RequirementAssignment(notice, artifact, task));
        db.AddRange(program, project, artifact, task, notice); await db.SaveChangesAsync();
        var original = notice.Context!;
        var wrongNotice = new UserNotification(project.Id, "other", "RequirementAssignment", "Task", "", "", artifact.Id, now);
        db.UserNotifications.Add(wrongNotice); await db.SaveChangesAsync();
        // Copy a structurally valid family with a different recipient. Individual GUID foreign keys
        // all remain valid; only the provider's source/notice ownership boundary can refuse it.
        var mismatch = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO notification_contexts ("NotificationId", "ProjectId", "Recipient", "EventType", "SourceFamily", "SourceId", "SourceSchemaVersion", "RecordId", "ChangeRequestId", "TestChangeReviewId", "ReviewCycleId", "ApprovalStepId", "AuditEventId", "DocumentId", "DocumentRevisionId", "DocumentStepId", "DocumentEventId", "DocumentAssignmentId", "ArtifactAssignmentId", "OperationId", "Cycle", "Revision", "SourceVersion", "Identifier", "Stage", "SnapshotHash") SELECT {wrongNotice.Id}, "ProjectId", {"other"}, "EventType", "SourceFamily", "SourceId", "SourceSchemaVersion", "RecordId", "ChangeRequestId", "TestChangeReviewId", "ReviewCycleId", "ApprovalStepId", "AuditEventId", "DocumentId", "DocumentRevisionId", "DocumentStepId", "DocumentEventId", "DocumentAssignmentId", "ArtifactAssignmentId", "OperationId", "Cycle", "Revision", "SourceVersion", "Identifier", "Stage", "SnapshotHash"
            FROM notification_contexts WHERE "NotificationId"={original.NotificationId}
            """));
        Assert.Contains("source ownership mismatch", mismatch.MessageText);
        var operation = new NotificationOperation(Guid.NewGuid().ToString("D"), "admin", "Settings", Guid.NewGuid(), "hash", now);
        operation.Record("{\"state\":\"Saved\"}"); db.NotificationOperations.Add(operation); await db.SaveChangesAsync();
        var immutable = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE notification_operations SET \"ResultJson\"={"altered"} WHERE \"Id\"={operation.Id}"));
        Assert.Contains("immutable", immutable.MessageText);
        var duplicate = new NotificationOperation(operation.InstallationId, operation.Actor, operation.Family, operation.OperationKey, "different", now); duplicate.Record("{}");
        db.NotificationOperations.Add(duplicate); var conflict = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        Assert.Equal(PostgresErrorCodes.UniqueViolation, Assert.IsType<PostgresException>(conflict.InnerException).SqlState);
    }
    private static AeroLinkDbContext Database(string connection) => new(new DbContextOptionsBuilder<AeroLinkDbContext>().UseNpgsql(connection).Options);
}
