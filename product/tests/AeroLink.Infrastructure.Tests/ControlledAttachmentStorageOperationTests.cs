using AeroLink.Domain.Programs;
using AeroLink.Domain.Requirements;
using AeroLink.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace AeroLink.Infrastructure.Tests;

public sealed class ControlledAttachmentStorageOperationTests
{
    [Fact]
    public async Task A_promoted_staged_image_is_adopted_only_through_the_durable_operation()
    {
        var root = Path.Combine(Path.GetTempPath(), $"aerolink-inline-operation-{Guid.NewGuid():N}");
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        try
        {
            var options = new DbContextOptionsBuilder<AeroLinkDbContext>().UseSqlite(connection).Options;
            await using var db = new AeroLinkDbContext(options);
            await db.Database.EnsureCreatedAsync();
            var program = new ProgramRecord("Inline operation program", "INL");
            var project = new ProjectRecord(program.Id, "Inline operation project", "Controlled image recovery");
            db.AddRange(program, project);
            await db.SaveChangesAsync();

            var store = new EvidenceFileStore(root);
            var operationId = Guid.NewGuid();
            var payload = new byte[] { 1, 2, 3, 4 };
            var staged = await store.StageAsync(new MemoryStream(payload), operationId, "inline-image", "diagram.png",
                "image/png", default);
            var operation = new ControlledAttachmentStorageOperation(operationId, project.Id, "InlineImageDraft",
                project.Id, null, Guid.NewGuid(), 1, "Diagram", staged.OriginalFileName, staged.ContentType,
                staged.Size, staged.Sha256, staged.StagingKey, staged.StorageKey, "author", DateTimeOffset.UtcNow);
            db.ControlledAttachmentStorageOperations.Add(operation);
            await db.SaveChangesAsync();
            await store.PromoteAsync(staged, default);

            var recovered = await new ControlledAttachmentStorageCoordinator(db, store)
                .ReconcileAsync(operation, "system.integrity", DateTimeOffset.UtcNow, default);

            Assert.NotNull(recovered);
            Assert.Equal(ControlledAttachmentStorageOperationState.Available, operation.State);
            Assert.Equal(recovered!.Id, operation.AttachmentId);
            Assert.Equal("InlineImageDraft", recovered.ArtifactType);
            Assert.Equal(staged.Sha256, recovered.Sha256);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Failed_operation_is_quarantined_and_remains_auditable()
    {
        var root = Path.Combine(Path.GetTempPath(), $"aerolink-inline-rollback-{Guid.NewGuid():N}");
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        try
        {
            var options = new DbContextOptionsBuilder<AeroLinkDbContext>().UseSqlite(connection).Options;
            await using var db = new AeroLinkDbContext(options);
            await db.Database.EnsureCreatedAsync();
            var program = new ProgramRecord("Rollback program", "RBK");
            var project = new ProjectRecord(program.Id, "Rollback project", "Controlled image recovery");
            db.AddRange(program, project);
            await db.SaveChangesAsync();
            var store = new EvidenceFileStore(root);
            var operationId = Guid.NewGuid();
            var staged = await store.StageAsync(new MemoryStream([1, 2, 3]), operationId, "inline-image", "x.png",
                "image/png", default);
            var operation = new ControlledAttachmentStorageOperation(operationId, project.Id, "InlineImageDraft",
                project.Id, null, Guid.NewGuid(), 1, "X", staged.OriginalFileName, staged.ContentType, staged.Size,
                staged.Sha256, staged.StagingKey, staged.StorageKey, "author", DateTimeOffset.UtcNow);
            db.Add(operation);
            await db.SaveChangesAsync();

            await new ControlledAttachmentStorageCoordinator(db, store).RollBackAsync(operation, "request failed",
                DateTimeOffset.UtcNow, default);

            Assert.Equal(ControlledAttachmentStorageOperationState.RolledBack, operation.State);
            Assert.False(store.Exists(staged.StagingKey));
            Assert.False(store.Exists(staged.StorageKey));
            Assert.NotEmpty(Directory.EnumerateFiles(Path.Combine(root, "_quarantine"), "*", SearchOption.AllDirectories));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Expired_recovery_cleanup_is_replayed_from_its_durable_journal()
    {
        var root = Path.Combine(Path.GetTempPath(), $"aerolink-inline-cleanup-{Guid.NewGuid():N}");
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        try
        {
            var options = new DbContextOptionsBuilder<AeroLinkDbContext>().UseSqlite(connection).Options;
            await using var db = new AeroLinkDbContext(options);
            await db.Database.EnsureCreatedAsync();
            var program = new ProgramRecord("Cleanup program", "CLN");
            var project = new ProjectRecord(program.Id, "Cleanup project", "Controlled image recovery");
            db.AddRange(program, project);
            await db.SaveChangesAsync();

            var store = new EvidenceFileStore(root);
            var operationId = Guid.NewGuid();
            var staged = await store.StageAsync(new MemoryStream([7, 8, 9]), operationId, "inline-image", "expired.png",
                "image/png", default);
            await store.PromoteAsync(staged, default);
            var operation = new ControlledAttachmentStorageOperation(operationId, project.Id, "InlineImageDraftCleanup",
                Guid.NewGuid(), null, Guid.NewGuid(), 1, "Expired image", staged.OriginalFileName, staged.ContentType,
                staged.Size, staged.Sha256, staged.StorageKey, staged.StorageKey, "system.cleanup", DateTimeOffset.UtcNow);
            db.Add(operation);
            await db.SaveChangesAsync();

            var recovered = await new ControlledAttachmentStorageCoordinator(db, store)
                .ReconcileAsync(operation, "system.integrity", DateTimeOffset.UtcNow, default);

            Assert.Null(recovered);
            Assert.Equal(ControlledAttachmentStorageOperationState.CleanedUp, operation.State);
            Assert.False(store.Exists(staged.StorageKey));
            Assert.Null(await db.ControlledAttachments.SingleOrDefaultAsync(x => x.Id == operation.ArtifactId));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
}
