using System.Security.Cryptography;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AeroLink.Infrastructure.Persistence;

public sealed record StoredEvidence(string OriginalFileName, string ContentType, long Size, string Sha256, string StorageKey);

public sealed class EvidenceFileStore
{
    private readonly string _root;
    [ActivatorUtilitiesConstructor]
    public EvidenceFileStore(IConfiguration configuration)
        : this(configuration["Evidence:Root"] ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AeroLink", "evidence")) { }
    public EvidenceFileStore(string root)
    {
        _root = root;
        Directory.CreateDirectory(_root);
    }
    public async Task<StoredEvidence> StoreAsync(Stream source, string originalFileName, string contentType, CancellationToken ct)
    {
        var safeName = Path.GetFileName(originalFileName); if (string.IsNullOrWhiteSpace(safeName)) throw new InvalidOperationException("A valid evidence filename is required.");
        var temp = Path.Combine(_root, $"upload-{Guid.NewGuid():N}.tmp"); long size = 0; string hash;
        try
        {
            await using (var output = File.Create(temp))
            using (var sha = SHA256.Create())
            {
                var buffer = new byte[81920]; int read;
                while ((read = await source.ReadAsync(buffer, ct)) > 0) { size += read; if (size > 100 * 1024 * 1024) throw new InvalidOperationException("Evidence files are limited to 100 MB."); sha.TransformBlock(buffer, 0, read, null, 0); await output.WriteAsync(buffer.AsMemory(0, read), ct); }
                sha.TransformFinalBlock([], 0, 0); hash = Convert.ToHexString(sha.Hash!).ToLowerInvariant();
            }
            var key = $"{hash[..2]}/{hash}-{Guid.NewGuid():N}{Path.GetExtension(safeName).ToLowerInvariant()}"; var destination = Resolve(key); Directory.CreateDirectory(Path.GetDirectoryName(destination)!); File.Move(temp, destination);
            return new(safeName, contentType ?? "application/octet-stream", size, hash, key);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    public Stream OpenRead(string storageKey) => File.OpenRead(Resolve(storageKey));
    private string Resolve(string storageKey)
    {
        var full = Path.GetFullPath(Path.Combine(_root, storageKey.Replace('/', Path.DirectorySeparatorChar))); var root = Path.GetFullPath(_root) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Invalid evidence storage key."); return full;
    }
}
