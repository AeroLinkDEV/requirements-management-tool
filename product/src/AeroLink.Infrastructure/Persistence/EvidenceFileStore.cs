using System.Security.Cryptography;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AeroLink.Infrastructure.Persistence;

public sealed record StoredEvidence(string OriginalFileName, string ContentType, long Size, string Sha256, string StorageKey);
public sealed record RestoredEvidence(string? QuarantineKey);
public sealed record StagedEvidence(string OriginalFileName, string ContentType, long Size, string Sha256,
    string StagingKey, string StorageKey);

public sealed class EvidenceIntegrityException(string code, string message, Exception? innerException = null)
    : IOException(message, innerException)
{
    public string Code { get; } = code;
}

public sealed class EvidenceFileStore
{
    private readonly string _root;
    [ActivatorUtilitiesConstructor]
    public EvidenceFileStore(IConfiguration configuration)
        : this(configuration["Evidence:Root"] ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AeroLink", "evidence")) { }
    public EvidenceFileStore(string root)
    {
        _root = Path.GetFullPath(root);
        Directory.CreateDirectory(_root);
    }
    public string RootPath => _root;
    public async Task<StoredEvidence> StoreAsync(Stream source, string originalFileName, string contentType, CancellationToken ct)
    {
        var staged = await StageAsync(source, Guid.NewGuid(), "object", originalFileName, contentType, ct);
        try
        {
            await PromoteAsync(staged, ct);
            return new(staged.OriginalFileName, staged.ContentType, staged.Size, staged.Sha256, staged.StorageKey);
        }
        catch { Delete(staged.StagingKey); throw; }
    }

    public async Task<StagedEvidence> StageAsync(Stream source, Guid operationId, string slot,
        string originalFileName, string contentType, CancellationToken ct)
    {
        var safeName = Path.GetFileName(originalFileName); if (string.IsNullOrWhiteSpace(safeName)) throw new InvalidOperationException("A valid evidence filename is required.");
        var safeSlot = new string((slot ?? "object").Where(char.IsLetterOrDigit).ToArray());
        if (safeSlot.Length == 0) safeSlot = "object";
        EnsureNoReparsePoints(_root);
        var stagingKey = $"_staging/{operationId:N}/{safeSlot}-{Guid.NewGuid():N}.stage";
        var stagingPath = Resolve(stagingKey); Directory.CreateDirectory(Path.GetDirectoryName(stagingPath)!);
        long size = 0; string hash;
        try
        {
            await using (var output = new FileStream(stagingPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920,
                FileOptions.Asynchronous | FileOptions.SequentialScan | FileOptions.WriteThrough))
            using (var sha = SHA256.Create())
            {
                var buffer = new byte[81920]; int read;
                while ((read = await source.ReadAsync(buffer, ct)) > 0)
                {
                    size += read; if (size > 100 * 1024 * 1024) throw new InvalidOperationException("Evidence files are limited to 100 MB.");
                    sha.TransformBlock(buffer, 0, read, null, 0); await output.WriteAsync(buffer.AsMemory(0, read), ct);
                }
                await output.FlushAsync(ct); sha.TransformFinalBlock([], 0, 0); hash = Convert.ToHexString(sha.Hash!).ToLowerInvariant();
            }
            if (size == 0) throw new InvalidOperationException("Evidence files cannot be empty.");
            var finalKey = $"{hash[..2]}/{hash}-{Guid.NewGuid():N}{Path.GetExtension(safeName).ToLowerInvariant()}";
            return new(safeName, contentType ?? "application/octet-stream", size, hash, stagingKey, finalKey);
        }
        catch { if (File.Exists(stagingPath)) File.Delete(stagingPath); throw; }
    }

    public async Task PromoteAsync(StagedEvidence staged, CancellationToken ct)
    {
        var source = Resolve(staged.StagingKey); var destination = Resolve(staged.StorageKey);
        EnsureNoReparsePoints(source); EnsureNoReparsePoints(destination); ct.ThrowIfCancellationRequested();
        if (File.Exists(destination))
        {
            await using var verified = await OpenVerifiedReadAsync(staged.StorageKey, staged.Size, staged.Sha256, ct);
            if (File.Exists(source)) File.Delete(source); return;
        }
        if (!File.Exists(source)) throw new EvidenceIntegrityException("staged_missing", "The staged evidence object is missing.");
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!); File.Move(source, destination);
        await using var promoted = await OpenVerifiedReadAsync(staged.StorageKey, staged.Size, staged.Sha256, ct);
    }

    public string? Quarantine(string storageKey, Guid operationId, string reason)
    {
        var source = Resolve(storageKey); if (!File.Exists(source)) return null;
        var safeReason = new string((reason ?? "reconcile").Where(char.IsLetterOrDigit).Take(30).ToArray());
        var key = $"_quarantine/storage-{operationId:N}-{safeReason}-{Guid.NewGuid():N}-{Path.GetFileName(source)}";
        var destination = Resolve(key); Directory.CreateDirectory(Path.GetDirectoryName(destination)!); File.Move(source, destination); return key;
    }

    public IReadOnlyList<string> EnumerateStagedKeys() => Directory.Exists(Resolve("_staging"))
        ? Directory.EnumerateFiles(Resolve("_staging"), "*.stage", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(_root, path).Replace(Path.DirectorySeparatorChar, '/')).ToList()
        : [];
    public Stream OpenRead(string storageKey) => File.OpenRead(Resolve(storageKey));
    public async Task<FileStream> OpenVerifiedReadAsync(string storageKey, long expectedSize, string expectedSha256, CancellationToken ct)
    {
        if (expectedSize <= 0 || string.IsNullOrWhiteSpace(expectedSha256))
            throw new EvidenceIntegrityException("invalid_metadata", "The controlled evidence metadata is incomplete.");

        FileStream? stream = null;
        try
        {
            var path = Resolve(storageKey);
            EnsureNoReparsePoints(path);
            stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (stream.Length != expectedSize)
                throw new EvidenceIntegrityException("size_mismatch", $"The controlled evidence size is {stream.Length}, expected {expectedSize}.");

            using var sha = SHA256.Create();
            var actualHash = Convert.ToHexString(await sha.ComputeHashAsync(stream, ct)).ToLowerInvariant();
            if (stream.Length != expectedSize)
                throw new EvidenceIntegrityException("size_changed", "The controlled evidence size changed while it was being verified.");
            if (!CryptographicOperations.FixedTimeEquals(
                    Convert.FromHexString(actualHash), Convert.FromHexString(expectedSha256)))
                throw new EvidenceIntegrityException("hash_mismatch", "The controlled evidence SHA-256 does not match its immutable metadata.");

            stream.Position = 0;
            return stream;
        }
        catch (OperationCanceledException)
        {
            stream?.Dispose();
            throw;
        }
        catch (EvidenceIntegrityException)
        {
            stream?.Dispose();
            throw;
        }
        catch (FileNotFoundException ex)
        {
            stream?.Dispose();
            throw new EvidenceIntegrityException("missing", "The controlled evidence object is missing.", ex);
        }
        catch (DirectoryNotFoundException ex)
        {
            stream?.Dispose();
            throw new EvidenceIntegrityException("missing", "The controlled evidence object is missing.", ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            stream?.Dispose();
            throw new EvidenceIntegrityException("unreadable", "The controlled evidence object cannot be read with the service identity.", ex);
        }
        catch (IOException ex)
        {
            stream?.Dispose();
            throw new EvidenceIntegrityException("unreadable", "The controlled evidence object could not be verified.", ex);
        }
        catch (FormatException ex)
        {
            stream?.Dispose();
            throw new EvidenceIntegrityException("invalid_metadata", "The controlled evidence SHA-256 metadata is invalid.", ex);
        }
    }
    public async Task<RestoredEvidence> RestoreExactAsync(Stream source, string storageKey, long expectedSize, string expectedSha256, CancellationToken ct)
    {
        var destination = Resolve(storageKey);
        EnsureNoReparsePoints(destination);
        var stage = Path.Combine(_root, $"restore-{Guid.NewGuid():N}.tmp");
        string? quarantineKey = null;
        try
        {
            long size = 0;
            string hash;
            await using (var output = new FileStream(stage, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            using (var sha = SHA256.Create())
            {
                var buffer = new byte[81920];
                int read;
                while ((read = await source.ReadAsync(buffer, ct)) > 0)
                {
                    size += read;
                    if (size > expectedSize) throw new EvidenceIntegrityException("restore_size_mismatch", "The recovery object is larger than the immutable attachment metadata.");
                    sha.TransformBlock(buffer, 0, read, null, 0);
                    await output.WriteAsync(buffer.AsMemory(0, read), ct);
                }
                sha.TransformFinalBlock([], 0, 0);
                hash = Convert.ToHexString(sha.Hash!).ToLowerInvariant();
            }
            if (size != expectedSize) throw new EvidenceIntegrityException("restore_size_mismatch", "The recovery object size does not match the immutable attachment metadata.");
            if (!CryptographicOperations.FixedTimeEquals(Convert.FromHexString(hash), Convert.FromHexString(expectedSha256)))
                throw new EvidenceIntegrityException("restore_hash_mismatch", "The recovery object SHA-256 does not match the immutable attachment metadata.");

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            if (File.Exists(destination))
            {
                quarantineKey = $"_quarantine/{DateTimeOffset.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid():N}-{Path.GetFileName(destination)}";
                var quarantinePath = Resolve(quarantineKey);
                Directory.CreateDirectory(Path.GetDirectoryName(quarantinePath)!);
                File.Move(destination, quarantinePath);
                try { File.Move(stage, destination); }
                catch { File.Move(quarantinePath, destination); quarantineKey = null; throw; }
            }
            else File.Move(stage, destination);
            return new(quarantineKey);
        }
        finally { if (File.Exists(stage)) File.Delete(stage); }
    }
    public bool Exists(string storageKey) => File.Exists(Resolve(storageKey));
    public long GetSize(string storageKey) => new FileInfo(Resolve(storageKey)).Length;
    public async Task<string> ComputeSha256Async(string storageKey, CancellationToken ct)
    {
        await using var stream = File.OpenRead(Resolve(storageKey));
        using var sha = SHA256.Create();
        var hash = await sha.ComputeHashAsync(stream, ct);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
    public void Delete(string storageKey) { var path = Resolve(storageKey); if (File.Exists(path)) File.Delete(path); }
    private string Resolve(string storageKey)
    {
        if (string.IsNullOrWhiteSpace(storageKey) || Path.IsPathRooted(storageKey))
            throw new EvidenceIntegrityException("unsafe_path", "Invalid evidence storage key.");
        var full = Path.GetFullPath(Path.Combine(_root, storageKey.Replace('/', Path.DirectorySeparatorChar))); var root = Path.GetFullPath(_root) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new EvidenceIntegrityException("unsafe_path", "Invalid evidence storage key."); return full;
    }

    private void EnsureNoReparsePoints(string path)
    {
        var rootAttributes = File.GetAttributes(_root);
        if ((rootAttributes & FileAttributes.ReparsePoint) != 0)
            throw new EvidenceIntegrityException("unsafe_path", "The configured evidence root cannot be a reparse point or symbolic link.");

        var relative = Path.GetRelativePath(_root, path);
        var current = _root;
        foreach (var part in relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            if (!File.Exists(current) && !Directory.Exists(current)) continue;
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new EvidenceIntegrityException("unsafe_path", "Controlled evidence cannot be read through a reparse point or symbolic link.");
        }
    }
}
