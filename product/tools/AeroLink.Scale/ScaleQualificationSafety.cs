using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Npgsql;

namespace AeroLink.Scale;

/// <summary>
/// The exact controlled scope and stable dataset identity used by one qualification run.
/// The IDs are intentionally caller supplied for an existing dataset; the harness never discovers a
/// replacement project or release when one is missing.
/// </summary>
public sealed record ScaleQualificationScope(
    Guid ProgramId,
    Guid ProjectId,
    Guid ReleaseId,
    Guid BaselineId,
    string DatasetSeed,
    string DatasetHash);

/// <summary>Identity read from the target database after the connection safety check.</summary>
public sealed record ScaleDatasetIdentity(
    Guid ProgramId,
    string ProgramName,
    string ProgramCode,
    Guid ProjectId,
    string ProjectName,
    string SoftwareProduct,
    Guid ReleaseId,
    string ReleaseVersion,
    Guid BaselineId,
    string BaselineNumber,
    int RequirementCount,
    string DatasetSeed,
    string RequirementContentHash = "",
    string BaselineRequirementsHash = "");

public sealed record ScaleQualificationManifest(
    string Schema,
    string Status,
    string Command,
    string Commit,
    string DatasetSeed,
    string DatasetHash,
    Guid ProgramId,
    Guid ProjectId,
    Guid ReleaseId,
    Guid BaselineId,
    string Topology,
    string DatabaseHost,
    int DatabasePort,
    string DatabaseName,
    string EvidenceRoot,
    bool QualificationOptIn,
    bool WriteOptIn,
    DateTimeOffset CreatedAt,
    bool SourceDirty,
    IReadOnlyDictionary<string, object?>? Results = null);

/// <summary>
/// Fail-closed safety checks shared by every scale-tool command that can create schema/data, provision
/// accounts, or run a qualification workload. This class deliberately does not open a database connection.
/// </summary>
public static class ScaleQualificationSafety
{
    public const string ManifestSchema = "aerolink.scale-qualification.v1";
    public const string ProtectedPort = "54329";

    public static string RequireSafeConnection(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            throw new InvalidOperationException("AEROLINK_SCALE_CONNECTION must point to an explicit disposable qualification database.");

        NpgsqlConnectionStringBuilder builder;
        try
        {
            builder = new NpgsqlConnectionStringBuilder(raw.Trim());
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException)
        {
            throw new InvalidOperationException("AEROLINK_SCALE_CONNECTION is not a valid PostgreSQL connection string.");
        }

        var host = (builder.Host ?? string.Empty).Trim().Trim('[', ']');
        if (!host.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase)
            && !host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            && !host.Equals("::1", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Scale qualification requires a loopback PostgreSQL host.");

        if (builder.Port == 54329)
            throw new InvalidOperationException("Scale qualification refuses the protected PostgreSQL port 54329.");

        var database = (builder.Database ?? string.Empty).Trim();
        if (!IsDedicatedDatabase(database))
            throw new InvalidOperationException("Scale qualification requires a dedicated database named aerolink_scale or aerolink_*_qualify.");

        // Return the caller's value unchanged. In particular, do not replace an unsafe target with a
        // convenient database name and then perform destructive operations there.
        return raw.Trim();
    }

    public static (string Host, int Port, string Database) SafeConnectionIdentity(string raw)
    {
        var builder = new NpgsqlConnectionStringBuilder(RequireSafeConnection(raw));
        return ((builder.Host ?? string.Empty).Trim().Trim('[', ']'), builder.Port, (builder.Database ?? string.Empty).Trim());
    }

    public static void RequireOptIns(bool qualificationEnabled, bool writeOptIn, string operation)
    {
        if (!qualificationEnabled)
            throw new InvalidOperationException($"{operation} requires explicit qualification opt-in (--qualification-enabled).");
        if (!writeOptIn)
            throw new InvalidOperationException($"{operation} requires separate write opt-in (--allow-write-load).");
    }

    public static void RequireDatasetWriteOptIn(bool qualificationEnabled, bool datasetWriteOptIn)
    {
        if (!qualificationEnabled)
            throw new InvalidOperationException("Dataset preparation requires explicit qualification opt-in (--qualification-enabled).");
        if (!datasetWriteOptIn)
            throw new InvalidOperationException("Dataset preparation requires separate dataset-write opt-in (--allow-dataset-write).");
    }

    public static string RequireEvidenceRoot(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new InvalidOperationException("Qualification requires an explicit evidence root (--evidence-root).");

        string full;
        try { full = Path.GetFullPath(path.Trim()); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        { throw new InvalidOperationException("Qualification evidence root is not a valid path."); }

        var persistentStore = ResolveExistingPath(PersistentProductStorePath());
        var resolvedEvidenceRoot = ResolveExistingPath(full);
        if (IsUnder(resolvedEvidenceRoot, persistentStore) || IsUnder(persistentStore, resolvedEvidenceRoot))
            throw new InvalidOperationException("Qualification evidence must be outside the persistent product/.local store.");

        return full;
    }

    public static string RequireExplicitSecret(string environmentVariable)
    {
        var value = Environment.GetEnvironmentVariable(environmentVariable);
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidOperationException($"Qualification requires the explicit {environmentVariable} environment variable.");
        return value;
    }

    public static string RequireExplicitSetting(string environmentVariable)
    {
        var value = Environment.GetEnvironmentVariable(environmentVariable);
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidOperationException($"Qualification requires the explicit {environmentVariable} environment variable.");
        return value.Trim();
    }

    public static ScaleQualificationScope RequireScope(
        string? programId,
        string? projectId,
        string? releaseId,
        string? baselineId,
        string? datasetSeed,
        string? datasetHash)
    {
        return new ScaleQualificationScope(
            ParseRequiredGuid(programId, "program"),
            ParseRequiredGuid(projectId, "project"),
            ParseRequiredGuid(releaseId, "release"),
            ParseRequiredGuid(baselineId, "baseline"),
            RequiredValue(datasetSeed, "dataset seed"),
            RequiredHash(datasetHash));
    }

    public static void ValidateExactDataset(ScaleQualificationScope expected, ScaleDatasetIdentity actual)
    {
        if (expected.ProgramId != actual.ProgramId
            || expected.ProjectId != actual.ProjectId
            || expected.ReleaseId != actual.ReleaseId
            || expected.BaselineId != actual.BaselineId)
            throw new InvalidOperationException("The qualification dataset scope does not match the requested Program, Project, release, and baseline.");

        if (!string.Equals(expected.DatasetSeed, actual.DatasetSeed, StringComparison.Ordinal))
            throw new InvalidOperationException("The qualification dataset seed does not match the requested dataset.");

        var actualHash = ComputeDatasetHash(actual);
        if (!actualHash.Equals(expected.DatasetHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The qualification dataset manifest hash does not match the requested dataset.");
    }

    public static string ComputeDatasetHash(ScaleDatasetIdentity identity)
    {
        var canonical = string.Join('|',
            identity.ProgramId.ToString("D"), identity.ProgramCode.Trim().ToUpperInvariant(), identity.ProgramName.Trim(),
            identity.ProjectId.ToString("D"), identity.ProjectName.Trim(), identity.SoftwareProduct.Trim(),
            identity.ReleaseId.ToString("D"), identity.ReleaseVersion.Trim(),
            identity.BaselineId.ToString("D"), identity.BaselineNumber.Trim(),
            identity.RequirementCount.ToString(System.Globalization.CultureInfo.InvariantCulture), identity.DatasetSeed.Trim(),
            identity.RequirementContentHash.Trim().ToLowerInvariant(), identity.BaselineRequirementsHash.Trim().ToLowerInvariant());
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    public static ScaleQualificationManifest CreateManifest(
        string status,
        string command,
        string commit,
        ScaleDatasetIdentity identity,
        string topology,
        string connection,
        string evidenceRoot,
        bool qualificationOptIn,
        bool writeOptIn,
        bool sourceDirty = false,
        IReadOnlyDictionary<string, object?>? results = null)
    {
        var safeConnection = SafeConnectionIdentity(connection);
        return new ScaleQualificationManifest(
            ManifestSchema,
            status,
            command,
            RequiredValue(commit, "commit"),
            identity.DatasetSeed,
            ComputeDatasetHash(identity),
            identity.ProgramId,
            identity.ProjectId,
            identity.ReleaseId,
            identity.BaselineId,
            topology,
            safeConnection.Host,
            safeConnection.Port,
            safeConnection.Database,
            RequireEvidenceRoot(evidenceRoot),
            qualificationOptIn,
            writeOptIn,
            DateTimeOffset.UtcNow,
            sourceDirty,
            results);
    }

    public static string WriteImmutableManifest(string path, ScaleQualificationManifest manifest)
    {
        var full = Path.GetFullPath(path);
        var evidenceRoot = RequireEvidenceRoot(Path.GetDirectoryName(full));
        Directory.CreateDirectory(evidenceRoot);
        var options = new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(manifest, options);
        try
        {
            using var stream = new FileStream(full, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            stream.Write(bytes);
        }
        catch (IOException)
        {
            throw new InvalidOperationException("The qualification manifest already exists or cannot be created immutably.");
        }
        return full;
    }

    public static string RequireManifestPath(string? path, string evidenceRoot)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new InvalidOperationException("Qualification requires an explicit manifest path (--manifest).");
        var full = Path.GetFullPath(path.Trim());
        var root = RequireEvidenceRoot(evidenceRoot);
        if (!IsUnder(full, root))
            throw new InvalidOperationException("The qualification manifest must be stored under the separate evidence root.");
        if (File.Exists(full))
            throw new InvalidOperationException("The qualification manifest already exists; immutable evidence cannot be overwritten.");
        return full;
    }

    public static bool IsDedicatedDatabase(string? database) =>
        !string.IsNullOrWhiteSpace(database)
        && (database.Trim().Equals("aerolink_scale", StringComparison.OrdinalIgnoreCase)
            || (database.Trim().StartsWith("aerolink_", StringComparison.OrdinalIgnoreCase)
                && database.Trim().EndsWith("_qualify", StringComparison.OrdinalIgnoreCase)));

    public static string PersistentProductStorePath()
    {
        var current = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (current is not null)
        {
            var candidate = Path.Combine(current.FullName, "product", ".local");
            if (Directory.Exists(Path.Combine(current.FullName, "product"))) return Path.GetFullPath(candidate);
            current = current.Parent;
        }
        return Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "product", ".local"));
    }

    private static bool IsUnder(string path, string root)
    {
        var relative = Path.GetRelativePath(root, path);
        return relative.Equals(".", StringComparison.Ordinal)
            || (!relative.StartsWith("..", StringComparison.Ordinal)
                && !Path.IsPathRooted(relative));
    }

    private static string ResolveExistingPath(string path)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full) ?? throw new InvalidOperationException("Qualification evidence path has no resolvable root.");
        var segments = full[root.Length..].Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries);
        var current = new DirectoryInfo(root);
        try
        {
            for (var i = 0; i < segments.Length; i++)
            {
                var candidate = new DirectoryInfo(Path.Combine(current.FullName, segments[i]));
                if (!candidate.Exists)
                {
                    for (var remaining = i; remaining < segments.Length; remaining++)
                        current = new DirectoryInfo(Path.Combine(current.FullName, segments[remaining]));
                    return current.FullName;
                }
                var resolved = candidate.ResolveLinkTarget(returnFinalTarget: true);
                current = resolved as DirectoryInfo ?? candidate;
            }
            return current.FullName;
        }
        catch (IOException ex)
        {
            throw new InvalidOperationException("Qualification evidence path could not be resolved safely.", ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new InvalidOperationException("Qualification evidence path could not be resolved safely.", ex);
        }
    }

    private static Guid ParseRequiredGuid(string? value, string label) =>
        Guid.TryParse(value, out var parsed) && parsed != Guid.Empty
            ? parsed
            : throw new InvalidOperationException($"Qualification requires an exact non-empty {label} ID.");

    private static string RequiredValue(string? value, string label) =>
        string.IsNullOrWhiteSpace(value) ? throw new InvalidOperationException($"Qualification requires an explicit {label}.") : value.Trim();

    private static string RequiredHash(string? value)
    {
        var hash = RequiredValue(value, "dataset manifest hash");
        if (hash.Length != 64 || hash.Any(c => !Uri.IsHexDigit(c)))
            throw new InvalidOperationException("Qualification requires a SHA-256 dataset manifest hash.");
        return hash.ToLowerInvariant();
    }
}
