using System.Text;
using AeroLink.Infrastructure.Persistence;

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
}
