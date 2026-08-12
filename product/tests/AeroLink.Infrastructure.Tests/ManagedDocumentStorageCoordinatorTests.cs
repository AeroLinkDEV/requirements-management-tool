using System.Text;
using AeroLink.Domain.Documents;
using AeroLink.Domain.Programs;
using AeroLink.Domain.Requirements;
using AeroLink.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace AeroLink.Infrastructure.Tests;

public sealed class ManagedDocumentStorageCoordinatorTests
{
    [Fact]
    public async Task Operation_key_retry_rejects_different_content_intent()
    {
        var root = Path.Combine(Path.GetTempPath(), $"aerolink-storage-key-conflict-{Guid.NewGuid():N}");
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AeroLinkDbContext>().UseSqlite(connection).Options;
        try
        {
            await using var db = new AeroLinkDbContext(options);
            await db.Database.EnsureCreatedAsync();
            var now = DateTimeOffset.UtcNow;
            var program = new ProgramRecord("Storage Program", "STORAGE");
            var project = new ProjectRecord(program.Id, "Storage Project", "Controlled documents");
            db.AddRange(program, project);
            await db.SaveChangesAsync();
            var store = new EvidenceFileStore(root);
            var coordinator = new ManagedDocumentStorageCoordinator(db, store,
                new ManagedDocumentIntegrityService(db, store), new NoManagedDocumentStorageFaultInjector());
            await coordinator.BeginAsync(project.Id, Guid.NewGuid(), Guid.NewGuid(), "CheckIn", "same-key",
                new string('a', 64), "author", now, default);

            var conflict = await Assert.ThrowsAsync<ManagedDocumentStorageConflictException>(() =>
                coordinator.BeginAsync(project.Id, Guid.NewGuid(), Guid.NewGuid(), "CheckIn", "same-key",
                    new string('b', 64), "author", now, default));

            Assert.Equal("operation_key_reused", conflict.Code);
            Assert.Single(await db.ManagedDocumentStorageOperations.ToListAsync());
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Reconciler_quarantines_an_operation_stage_that_precedes_its_manifest()
    {
        var root = Path.Combine(Path.GetTempPath(), $"aerolink-storage-unmanifested-{Guid.NewGuid():N}");
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AeroLinkDbContext>().UseSqlite(connection).Options;
        try
        {
            await using var db = new AeroLinkDbContext(options);
            await db.Database.EnsureCreatedAsync();
            var now = DateTimeOffset.UtcNow;
            var program = new ProgramRecord("Storage Program", "STORAGE");
            var project = new ProjectRecord(program.Id, "Storage Project", "Controlled documents");
            db.AddRange(program, project);
            await db.SaveChangesAsync();
            var store = new EvidenceFileStore(root);
            var coordinator = new ManagedDocumentStorageCoordinator(db, store,
                new ManagedDocumentIntegrityService(db, store), new NoManagedDocumentStorageFaultInjector());
            var operation = (await coordinator.BeginAsync(project.Id, Guid.NewGuid(), Guid.NewGuid(),
                "Candidate", "candidate-before-manifest", new string('c', 64), "author", now, default)).Operation;
            var staged = await store.StageAsync(new MemoryStream(Encoding.UTF8.GetBytes("candidate-docx")),
                operation.Id, "docx", "candidate.docx", "application/test", default);

            var result = await coordinator.ReconcileProjectAsync(project.Id, "configuration.manager",
                now.AddMinutes(1), default);

            Assert.Equal(1, result.RolledBack);
            Assert.Equal(ManagedDocumentStorageOperationState.RolledBack, operation.State);
            Assert.False(File.Exists(Path.Combine(root, staged.StagingKey.Replace('/', Path.DirectorySeparatorChar))));
            var quarantined = Assert.Single(result.QuarantinedKeys);
            Assert.True(File.Exists(Path.Combine(root, quarantined.Replace('/', Path.DirectorySeparatorChar))));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Project_reconciliation_does_not_quarantine_another_projects_pending_stage()
    {
        var root = Path.Combine(Path.GetTempPath(), $"aerolink-storage-cross-project-{Guid.NewGuid():N}");
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AeroLinkDbContext>().UseSqlite(connection).Options;
        try
        {
            await using var db = new AeroLinkDbContext(options);
            await db.Database.EnsureCreatedAsync();
            var now = DateTimeOffset.UtcNow;
            var program = new ProgramRecord("Storage Program", "STORAGE");
            var projectA = new ProjectRecord(program.Id, "Project A", "Controlled documents A");
            var projectB = new ProjectRecord(program.Id, "Project B", "Controlled documents B");
            db.AddRange(program, projectA, projectB);
            await db.SaveChangesAsync();
            var store = new EvidenceFileStore(root);
            var coordinator = new ManagedDocumentStorageCoordinator(db, store,
                new ManagedDocumentIntegrityService(db, store), new NoManagedDocumentStorageFaultInjector());
            var operationB = (await coordinator.BeginAsync(projectB.Id, Guid.NewGuid(), Guid.NewGuid(),
                "CheckIn", "project-b-pending", new string('d', 64), "author", now, default)).Operation;
            var stagedB = await store.StageAsync(new MemoryStream(Encoding.UTF8.GetBytes("project-b-working")),
                operationB.Id, "working", "working.docx", "application/test", default);

            var result = await coordinator.ReconcileProjectAsync(projectA.Id, "configuration.manager",
                now.AddMinutes(1), default);

            Assert.Empty(result.QuarantinedKeys);
            Assert.Equal(ManagedDocumentStorageOperationState.Pending, operationB.State);
            Assert.True(File.Exists(Path.Combine(root, stagedB.StagingKey.Replace('/', Path.DirectorySeparatorChar))));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Reconciler_quarantines_unreferenced_objects_and_blocks_partial_candidate_sets()
    {
        var root = Path.Combine(Path.GetTempPath(), $"aerolink-storage-reconcile-{Guid.NewGuid():N}");
        await using var connection = new SqliteConnection("Data Source=:memory:"); await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AeroLinkDbContext>().UseSqlite(connection).Options;
        try
        {
            await using var db = new AeroLinkDbContext(options); await db.Database.EnsureCreatedAsync();
            var now = DateTimeOffset.UtcNow; var program = new ProgramRecord("Storage Program", "STORAGE");
            var project = new ProjectRecord(program.Id, "Storage Project", "Controlled documents");
            var document = new ManagedDocument(project.Id, "SDP-000001", "SDP", "Software Development Plan", "Storage plan", "author", now);
            var revision = new ManagedDocumentRevision(document.Id, 0, "author", "Initial scope.", now);
            db.AddRange(program, project, document, revision); await db.SaveChangesAsync();
            var store = new EvidenceFileStore(root); var integrity = new ManagedDocumentIntegrityService(db, store);
            var coordinator = new ManagedDocumentStorageCoordinator(db, store, integrity, new NoManagedDocumentStorageFaultInjector());

            var partial = (await coordinator.BeginAsync(project.Id, document.Id, revision.Id, "Candidate", "candidate-1",
                new string('a', 64), "author", now, default)).Operation;
            var docx = await store.StageAsync(new MemoryStream(Encoding.UTF8.GetBytes("candidate-docx")), partial.Id, "docx", "candidate.docx", "application/test", default);
            var pdf = await store.StageAsync(new MemoryStream(Encoding.UTF8.GetBytes("candidate-pdf")), partial.Id, "pdf", "candidate.pdf", "application/test", default);
            var docxAttachment = Attachment(project.Id, document.Id, revision.Id, docx, "Candidate DOCX", now);
            var pdfAttachment = Attachment(project.Id, document.Id, revision.Id, pdf, "Candidate PDF", now);
            await coordinator.RecordPlanAsync(partial,
                [Object("docx", docxAttachment, docx), Object("pdf", pdfAttachment, pdf)], "{\"candidate\":true}", now, default);
            await coordinator.PromoteAsync(partial, [docx, pdf], default); db.ControlledAttachments.Add(docxAttachment); await db.SaveChangesAsync();

            var orphan = (await coordinator.BeginAsync(project.Id, document.Id, revision.Id, "CheckIn", "orphan-1",
                new string('b', 64), "author", now, default)).Operation;
            var orphanBytes = await store.StageAsync(new MemoryStream(Encoding.UTF8.GetBytes("orphan-working-copy")), orphan.Id, "working", "working.docx", "application/test", default);
            var orphanAttachment = Attachment(project.Id, document.Id, revision.Id, orphanBytes, "Working", now);
            await coordinator.RecordPlanAsync(orphan, [Object("working", orphanAttachment, orphanBytes)], "{\"working\":true}", now, default);
            await coordinator.PromoteAsync(orphan, [orphanBytes], default);

            var result = await coordinator.ReconcileProjectAsync(project.Id, "configuration.manager", now.AddMinutes(1), default);

            Assert.True(result.RepairRequired >= 1); Assert.Equal(1, result.RolledBack);
            Assert.Equal(ManagedDocumentStorageOperationState.RepairRequired, partial.State);
            Assert.Equal(ManagedDocumentStorageOperationState.RolledBack, orphan.State);
            Assert.Contains(result.Objects, item => item.OperationId == partial.Id && item.Slot == "docx"
                && item.Size == docx.Size && item.Sha256 == docx.Sha256
                && item.State == ManagedDocumentStorageOperationState.RepairRequired);
            Assert.Contains(result.Objects, item => item.OperationId == orphan.Id && item.Slot == "working"
                && item.Size == orphanBytes.Size && item.Sha256 == orphanBytes.Sha256
                && item.State == ManagedDocumentStorageOperationState.RolledBack);
            Assert.Contains(result.QuarantinedKeys, key => key.Contains("orphanobject", StringComparison.Ordinal));
            Assert.All(result.QuarantinedKeys, key => Assert.True(File.Exists(Path.Combine(root, key.Replace('/', Path.DirectorySeparatorChar)))));
            Assert.Contains(await db.OperationalAlerts.ToListAsync(), alert => alert.Signal == $"managed-document-storage:{partial.Id:N}" && alert.State == OperationalAlertState.Open);
            Assert.Contains(await db.ManagedDocumentEvents.ToListAsync(), item => item.EventType == "ManagedDocumentStorageRepairRequired");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private static ControlledAttachment Attachment(Guid projectId, Guid documentId, Guid revisionId,
        StagedEvidence staged, string label, DateTimeOffset now) => new(projectId, "ManagedDocument", documentId,
        revisionId, Guid.NewGuid(), 1, label, "Test evidence", staged.OriginalFileName, staged.ContentType,
        staged.Size, staged.Sha256, staged.StorageKey, null, "author", now);
    private static ManagedDocumentStagedObject Object(string slot, ControlledAttachment attachment, StagedEvidence staged) =>
        new(slot, attachment.Id, staged.StagingKey, staged.StorageKey, staged.Size, staged.Sha256);
}
