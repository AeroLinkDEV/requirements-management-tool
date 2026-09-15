using AeroLink.Infrastructure.Persistence;
using Xunit;

namespace AeroLink.Infrastructure.Tests;

public sealed class TemporaryExportTests
{
    [Fact]
    public async Task Expiry_removes_only_owned_temporary_exports_and_keeps_controlled_evidence()
    {
        var root = Path.Combine(Path.GetTempPath(), $"aerolink-export-retention-{Guid.NewGuid():N}");
        try
        {
            var files = new EvidenceFileStore(root);
            var now = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            using var controlledContent = new MemoryStream("controlled"u8.ToArray());
            var controlled = await files.StoreAsync(controlledContent, "controlled.csv", "text/csv", default);
            using var exportContent = new MemoryStream("temporary"u8.ToArray());
            var export = await files.StoreTemporaryExportAsync(exportContent, "export.csv", "text/csv", now, default);
            using var newerContent = new MemoryStream("newer"u8.ToArray());
            var newer = await files.StoreTemporaryExportAsync(newerContent, "newer.csv", "text/csv", now.AddDays(1), default);
            Assert.Equal(now.AddDays(7), export.ExpiresAt);
            Assert.Equal(0, files.RemoveExpiredTemporaryExports(now.AddDays(6)));
            Assert.Equal(1, files.RemoveExpiredTemporaryExports(now.AddDays(7)));
            Assert.False(File.Exists(Path.Combine(root, export.File.StorageKey)));
            using var retained = files.OpenRead(controlled.StorageKey);
            using var unexpired = files.OpenRead(newer.File.StorageKey);
            Assert.True(retained.Length > 0);
            Assert.True(unexpired.Length > 0);
            Assert.Equal(0, files.RemoveExpiredTemporaryExports(now.AddDays(7)));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
