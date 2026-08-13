using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AeroLink.ConnectorProtocol;

public enum ConnectorWorkspaceState
{
    Downloading, Connected, Retrying, LeaseAtRisk, Expired, ForceUnlocked, SourceConflict,
    Finalizing, CleanupPending, Completed, Discarded, Abandoned
}

public enum ConnectorWordDocumentState { Closed, OpenSaved, OpenUnsaved, Locked, Unknown }

public sealed record ConnectorWorkspaceMetadata(
    int Version,
    Guid WorkspaceId,
    string DeploymentId,
    string Origin,
    Guid ProgramId,
    Guid ProjectId,
    Guid DocumentId,
    string DocumentNumber,
    Guid RevisionId,
    string RevisionNumber,
    Guid EditSessionId,
    Guid ActiveGrantId,
    string Mode,
    Guid BaseAttachmentId,
    string BaseSha256,
    string WorkingFileName,
    ConnectorWorkspaceState State,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string? LocalSha256 = null,
    int HeartbeatFailures = 0,
    DateTimeOffset? LeaseExpiresAt = null,
    string? CandidateDirectoryName = null,
    string? CandidateDocxSha256 = null,
    string? CandidatePdfSha256 = null,
    DateTimeOffset? RetainUntil = null,
    Guid? AcceptedAttachmentId = null,
    Guid? CandidateDocxAttachmentId = null,
    Guid? CandidatePdfAttachmentId = null);

public sealed class ConnectorWorkspaceStore(
    string rootPath,
    Func<byte[], byte[]> protect,
    Func<byte[], byte[]> unprotect)
{
    public const string MetadataFileName = ".workspace.aerolink";
    private const int MaximumMetadataBytes = 64 * 1024;
    private readonly string _root = Path.GetFullPath(rootPath);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public void Save(string workspacePath, ConnectorWorkspaceMetadata metadata)
    {
        Validate(metadata); var directory = ControlledDirectory(workspacePath); Directory.CreateDirectory(directory);
        var plain = JsonSerializer.SerializeToUtf8Bytes(metadata, Json); var cipher = protect(plain);
        if (cipher.Length is 0 or > MaximumMetadataBytes) throw new ConnectorProtocolException("connector_workspace_invalid", "The protected connector metadata is empty or oversized.");
        var path = Path.Combine(directory, MetadataFileName); var temporary = path + $".{Guid.NewGuid():N}.tmp";
        File.WriteAllBytes(temporary, cipher); File.Move(temporary, path, true);
    }

    public ConnectorWorkspaceMetadata Load(string workspacePath)
    {
        var path = Path.Combine(ControlledDirectory(workspacePath), MetadataFileName);
        if (!File.Exists(path) || new FileInfo(path).Length is 0 or > MaximumMetadataBytes)
            throw new ConnectorProtocolException("connector_workspace_invalid", "The protected connector workspace metadata is missing or oversized.");
        try
        {
            var value = JsonSerializer.Deserialize<ConnectorWorkspaceMetadata>(unprotect(File.ReadAllBytes(path)), Json)
                ?? throw new JsonException(); Validate(value); return value;
        }
        catch (ConnectorProtocolException) { throw; }
        catch (Exception ex) when (ex is CryptographicException or JsonException or IOException or UnauthorizedAccessException)
        { throw new ConnectorProtocolException("connector_workspace_invalid", "The connector workspace metadata could not be authenticated.", ex); }
    }

    public IReadOnlyList<(string Path, ConnectorWorkspaceMetadata Metadata)> Scan()
    {
        if (!Directory.Exists(_root)) return [];
        var results = new List<(string Path, ConnectorWorkspaceMetadata Metadata)>();
        foreach (var path in Directory.EnumerateFiles(_root, MetadataFileName, SearchOption.AllDirectories).Take(1000))
        {
            var directory = Path.GetDirectoryName(path)!;
            try { results.Add((directory, Load(directory))); }
            catch (ConnectorProtocolException) { /* The recovery UI preserves an unreadable workspace as export-only. */ }
        }
        return results;
    }

    public void Delete(string workspacePath)
    {
        var directory = ControlledDirectory(workspacePath);
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }

    private string ControlledDirectory(string value)
    {
        var path = Path.GetFullPath(value);
        if (!path.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new ConnectorProtocolException("connector_workspace_invalid", "The connector workspace escaped its controlled root.");
        return path;
    }

    private static void Validate(ConnectorWorkspaceMetadata value)
    {
        if (value.Version != 2 || value.WorkspaceId == Guid.Empty || value.ProgramId == Guid.Empty || value.ProjectId == Guid.Empty
            || value.DocumentId == Guid.Empty || value.RevisionId == Guid.Empty || value.EditSessionId == Guid.Empty
            || value.ActiveGrantId == Guid.Empty || value.BaseAttachmentId == Guid.Empty || value.BaseSha256.Length != 64
            || !value.BaseSha256.All(Uri.IsHexDigit) || string.IsNullOrWhiteSpace(value.WorkingFileName)
            || Path.GetFileName(value.WorkingFileName) != value.WorkingFileName || value.HeartbeatFailures < 0
            || !ValidOptionalHash(value.LocalSha256) || !ValidOptionalHash(value.CandidateDocxSha256)
            || !ValidOptionalHash(value.CandidatePdfSha256) || !ValidRelativeDirectory(value.CandidateDirectoryName)
            || value.AcceptedAttachmentId == Guid.Empty || value.CandidateDocxAttachmentId == Guid.Empty
            || value.CandidatePdfAttachmentId == Guid.Empty)
            throw new ConnectorProtocolException("connector_workspace_invalid", "The connector workspace metadata is incomplete or unsafe.");
        _ = ConnectorLaunchProtocol.NormalizeOrigin(value.Origin, allowInsecureLoopback: true);
    }

    private static bool ValidOptionalHash(string? value) => value is null || (value.Length == 64 && value.All(Uri.IsHexDigit));

    private static bool ValidRelativeDirectory(string? value)
    {
        if (value is null) return true;
        if (string.IsNullOrWhiteSpace(value) || Path.IsPathRooted(value)) return false;
        var normalized = value.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
        return normalized.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries)
            .All(segment => segment is not "." and not "..");
    }
}

public static class ConnectorWorkspaceLifecycle
{
    public static bool CanUpload(ConnectorWordDocumentState state) => state is ConnectorWordDocumentState.Closed or ConnectorWordDocumentState.OpenSaved;
    public static bool CanCleanup(ConnectorWordDocumentState state) => state == ConnectorWordDocumentState.Closed;
    public static DateTimeOffset? RetainUntil(ConnectorWorkspaceState state, DateTimeOffset now) => state switch
    {
        ConnectorWorkspaceState.SourceConflict => now.AddDays(90),
        ConnectorWorkspaceState.Abandoned or ConnectorWorkspaceState.Expired or ConnectorWorkspaceState.ForceUnlocked => now.AddDays(30),
        _ => null
    };

    public static string CreateCandidateSet(string workspacePath)
    {
        var release = Path.Combine(Path.GetFullPath(workspacePath), "release"); Directory.CreateDirectory(release);
        var candidate = Path.Combine(release, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(candidate); return candidate;
    }
}

public sealed record ConnectorHeartbeatDecision(ConnectorWorkspaceState State, TimeSpan NextDelay, int Failures, bool Terminal);

public static class ConnectorHeartbeatPolicy
{
    public static ConnectorHeartbeatDecision Success() => new(ConnectorWorkspaceState.Connected, TimeSpan.FromMinutes(4), 0, false);

    public static ConnectorHeartbeatDecision Failure(int priorFailures, DateTimeOffset now, DateTimeOffset leaseExpiresAt, string? serverCode = null)
    {
        if (serverCode is "stale_connector_session" or "connector_session_expired")
            return new(ConnectorWorkspaceState.Expired, TimeSpan.Zero, priorFailures + 1, true);
        if (serverCode == "connector_force_unlocked")
            return new(ConnectorWorkspaceState.ForceUnlocked, TimeSpan.Zero, priorFailures + 1, true);
        if (serverCode == "document_snapshot_conflict")
            return new(ConnectorWorkspaceState.SourceConflict, TimeSpan.Zero, priorFailures + 1, true);
        if (leaseExpiresAt <= now)
            return new(ConnectorWorkspaceState.Expired, TimeSpan.Zero, priorFailures + 1, true);
        var failures = priorFailures + 1; var remaining = leaseExpiresAt - now;
        var delay = TimeSpan.FromSeconds(failures switch { 1 => 10, 2 => 30, 3 => 60, _ => 120 });
        var state = remaining <= TimeSpan.FromMinutes(5) || failures >= 3 ? ConnectorWorkspaceState.LeaseAtRisk : ConnectorWorkspaceState.Retrying;
        return new(state, delay, failures, false);
    }
}
