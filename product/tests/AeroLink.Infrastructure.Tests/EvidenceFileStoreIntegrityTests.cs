using System.Text;
using System.IO.Compression;
using AeroLink.Domain.Documents;
using AeroLink.Domain.Programs;
using AeroLink.Domain.Requirements;
using AeroLink.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace AeroLink.Infrastructure.Tests;

public sealed class EvidenceFileStoreIntegrityTests
{
    [Fact]
    public async Task Verified_read_returns_only_the_exact_recorded_object()
    {
        await WithStoreAsync(async (store, root) =>
        {
            var content = Encoding.UTF8.GetBytes("immutable controlled document");
            var stored = await store.StoreAsync(new MemoryStream(content), "plan.docx", "application/test", default);

            await using var verified = await store.OpenVerifiedReadAsync(stored.StorageKey, stored.Size, stored.Sha256, default);
            using var copy = new MemoryStream(); await verified.CopyToAsync(copy);

            Assert.Equal(content, copy.ToArray());
            Assert.Equal(Path.GetFullPath(root), store.RootPath);
        });
    }

    [Fact]
    public async Task Same_size_byte_replacement_is_rejected_before_a_stream_is_returned()
    {
        await WithStoreAsync(async (store, _) =>
        {
            var stored = await StoreAsync(store, "original-evidence");
            await File.WriteAllBytesAsync(PathFor(store, stored.StorageKey), Encoding.UTF8.GetBytes("substitute-bytes!"));

            var failure = await Assert.ThrowsAsync<EvidenceIntegrityException>(() =>
                store.OpenVerifiedReadAsync(stored.StorageKey, stored.Size, stored.Sha256, default));

            Assert.Equal("hash_mismatch", failure.Code);
        });
    }

    [Theory]
    [InlineData("short")]
    [InlineData("original-evidence-extended")]
    public async Task Truncation_or_extension_is_rejected(string replacement)
    {
        await WithStoreAsync(async (store, _) =>
        {
            var stored = await StoreAsync(store, "original-evidence");
            await File.WriteAllBytesAsync(PathFor(store, stored.StorageKey), Encoding.UTF8.GetBytes(replacement));

            var failure = await Assert.ThrowsAsync<EvidenceIntegrityException>(() =>
                store.OpenVerifiedReadAsync(stored.StorageKey, stored.Size, stored.Sha256, default));

            Assert.Equal("size_mismatch", failure.Code);
        });
    }

    [Fact]
    public async Task Missing_and_escaping_objects_have_controlled_failure_codes()
    {
        await WithStoreAsync(async (store, _) =>
        {
            var stored = await StoreAsync(store, "original-evidence");
            File.Delete(PathFor(store, stored.StorageKey));
            var missing = await Assert.ThrowsAsync<EvidenceIntegrityException>(() =>
                store.OpenVerifiedReadAsync(stored.StorageKey, stored.Size, stored.Sha256, default));
            Assert.Equal("missing", missing.Code);

            var unsafePath = await Assert.ThrowsAsync<EvidenceIntegrityException>(() =>
                store.OpenVerifiedReadAsync("../outside.docx", stored.Size, stored.Sha256, default));
            Assert.Equal("unsafe_path", unsafePath.Code);
        });
    }

    [Fact]
    public async Task Exact_hash_recovery_quarantines_altered_bytes_without_rewriting_metadata()
    {
        await WithStoreAsync(async (store, _) =>
        {
            var original = Encoding.UTF8.GetBytes("original-evidence");
            var stored = await store.StoreAsync(new MemoryStream(original), "plan.docx", "application/test", default);
            await File.WriteAllBytesAsync(PathFor(store, stored.StorageKey), Encoding.UTF8.GetBytes("substitute-bytes!"));

            var restored = await store.RestoreExactAsync(new MemoryStream(original), stored.StorageKey, stored.Size, stored.Sha256, default);

            Assert.NotNull(restored.QuarantineKey);
            Assert.True(File.Exists(PathFor(store, restored.QuarantineKey!)));
            await using var verified = await store.OpenVerifiedReadAsync(stored.StorageKey, stored.Size, stored.Sha256, default);
            Assert.Equal(stored.Size, verified.Length);
        });
    }

    [Fact]
    public async Task Exact_hash_legacy_docx_is_still_blocked_when_the_current_safe_profile_rejects_it()
    {
        var root = Path.Combine(Path.GetTempPath(), $"aerolink-integrity-ooxml-{Guid.NewGuid():N}");
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AeroLinkDbContext>().UseSqlite(connection).Options;
        try
        {
            await using var db = new AeroLinkDbContext(options);
            await db.Database.EnsureCreatedAsync();
            var program = new ProgramRecord("Integrity Program", "INTEGRITY");
            var project = new ProjectRecord(program.Id, "Integrity Project", "Controlled documents");
            var document = new ManagedDocument(project.Id, "SDP-000001", "SDP", "Software Development Plan",
                "Integrity plan", "legacy.author", DateTimeOffset.UtcNow);
            var revision = new ManagedDocumentRevision(document.Id, 0, "legacy.author", "Initial scope.", DateTimeOffset.UtcNow);
            db.AddRange(program, project, document, revision);
            var store = new EvidenceFileStore(root);
            var unsafeDocx = UnsafeExternalRelationshipDocx();
            var stored = await store.StoreAsync(new MemoryStream(unsafeDocx), "legacy.docx",
                ManagedDocumentFileService.DocxContentType, default);
            var attachment = new ControlledAttachment(project.Id, "ManagedDocument", document.Id, revision.Id,
                Guid.NewGuid(), 1, "Legacy working DOCX", "Predates the safe profile.", stored.OriginalFileName,
                stored.ContentType, stored.Size, stored.Sha256, stored.StorageKey, null, "legacy.author", DateTimeOffset.UtcNow);
            db.ControlledAttachments.Add(attachment);
            await db.SaveChangesAsync();
            var service = new ManagedDocumentIntegrityService(db, store);

            var failure = await Assert.ThrowsAsync<ManagedDocumentIntegrityFailure>(() =>
                service.OpenVerifiedAsync(attachment, "quality.analyst", default));

            Assert.Equal("ooxml_relationship_external", failure.Code);
            Assert.Contains(await db.OperationalAlerts.ToListAsync(), item =>
                item.Signal == $"managed-document-integrity:{attachment.Id:N}");
            Assert.Contains(await db.ManagedDocumentEvents.ToListAsync(), item =>
                item.EventType == "DocumentIntegrityBlocked");
            Assert.Contains(await db.SecurityAuditEvents.ToListAsync(), item =>
                item.EventType == "ManagedDocumentIntegrityFailure");

            var recovery = await Assert.ThrowsAsync<ManagedDocumentIntegrityFailure>(() => service.RestoreAsync(
                attachment, new MemoryStream(unsafeDocx), "quality.analyst", "Exact historical recovery attempt.", default));
            Assert.Equal("ooxml_relationship_external", recovery.Code);
            Assert.Contains(await db.OperationalAlerts.ToListAsync(), item =>
                item.Signal == $"managed-document-integrity:{attachment.Id:N}" && item.State != OperationalAlertState.Resolved);
            Assert.Contains(await db.SecurityAuditEvents.ToListAsync(), item =>
                item.EventType == "ManagedDocumentIntegrityRecoveryRejected");
            Assert.Contains(await db.ManagedDocumentEvents.ToListAsync(), item =>
                item.EventType == "DocumentIntegrityRecoveryRejected");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private static async Task<StoredEvidence> StoreAsync(EvidenceFileStore store, string value) =>
        await store.StoreAsync(new MemoryStream(Encoding.UTF8.GetBytes(value)), "plan.docx", "application/test", default);

    private static string PathFor(EvidenceFileStore store, string storageKey) =>
        Path.Combine(store.RootPath, storageKey.Replace('/', Path.DirectorySeparatorChar));

    private static async Task WithStoreAsync(Func<EvidenceFileStore, string, Task> test)
    {
        var root = Path.Combine(Path.GetTempPath(), $"aerolink-integrity-{Guid.NewGuid():N}");
        try { await test(new EvidenceFileStore(root), root); }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private static byte[] UnsafeExternalRelationshipDocx()
    {
        const string contentTypes = """
            <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
              <Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>
              <Default Extension="xml" ContentType="application/xml"/>
              <Override PartName="/word/document.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml"/>
            </Types>
            """;
        const string relationships = """
            <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
              <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="word/document.xml"/>
              <Relationship Id="rId2" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/image" Target="https://example.test/tracker.png" TargetMode="External"/>
            </Relationships>
            """;
        const string document = "<w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"><w:body/></w:document>";
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, true))
        {
            foreach (var (name, value) in new[] { ("[Content_Types].xml", contentTypes), ("_rels/.rels", relationships), ("word/document.xml", document) })
            {
                var entry = archive.CreateEntry(name);
                using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
                writer.Write(value);
            }
        }
        return output.ToArray();
    }
}
