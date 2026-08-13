using System.Security.Cryptography;
using System.Text;

namespace AeroLink.ConnectorProtocol;

public static class ConnectorWorkspaceLayout
{
    public static string CreateNew(string workingRoot, ConnectorLaunchEnvelope envelope, Guid grantId)
    {
        if (grantId == Guid.Empty) throw new ConnectorProtocolException("connector_workspace_invalid", "The connector workspace identity is invalid.");
        var root = Path.GetFullPath(workingRoot);
        var deployment = SafeSegment(envelope.DeploymentId);
        var path = Path.Combine(root, deployment, envelope.ProjectId.ToString("N"), envelope.DocumentId.ToString("N"),
            envelope.RevisionId.ToString("N"), grantId.ToString("N"));
        if (!Path.GetFullPath(path).StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new ConnectorProtocolException("connector_workspace_invalid", "The connector workspace path escaped its controlled root.");
        if (Directory.Exists(path)) throw new ConnectorProtocolException("connector_workspace_exists", "An unresolved connector workspace already exists. It was preserved and was not overwritten.");
        Directory.CreateDirectory(path);
        try { using var claim = new FileStream(Path.Combine(path, ".workspace-claim"), FileMode.CreateNew, FileAccess.Write, FileShare.None); }
        catch
        {
            // Never delete here: another process may own the directory or already have unsent work in it.
            throw new ConnectorProtocolException("connector_workspace_exists", "An unresolved connector workspace already exists. It was preserved and was not overwritten.");
        }
        return path;
    }

    public static string ResolveExisting(string workingRoot, ConnectorLaunchEnvelope envelope, Guid workspaceId)
    {
        var root = Path.GetFullPath(workingRoot); var path = WorkspacePath(root, envelope, workspaceId);
        if (!Directory.Exists(path)) throw new ConnectorProtocolException("connector_workspace_missing", "The signed recovery workspace was not found on this Windows account.");
        return path;
    }

    public static string SafeDocumentFileName(string revisionNumber)
    {
        var value = string.Concat(revisionNumber.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        return string.IsNullOrWhiteSpace(value) ? "controlled-document.docx" : value + ".docx";
    }

    private static string SafeSegment(string value)
    {
        var readable = string.Concat(value.Take(48).Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_'));
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..12].ToLowerInvariant();
        return $"{readable}-{hash}";
    }

    private static string WorkspacePath(string root, ConnectorLaunchEnvelope envelope, Guid workspaceId)
    {
        if (workspaceId == Guid.Empty) throw new ConnectorProtocolException("connector_workspace_invalid", "The connector workspace identity is invalid.");
        var path = Path.Combine(root, SafeSegment(envelope.DeploymentId), envelope.ProjectId.ToString("N"), envelope.DocumentId.ToString("N"),
            envelope.RevisionId.ToString("N"), workspaceId.ToString("N"));
        if (!Path.GetFullPath(path).StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new ConnectorProtocolException("connector_workspace_invalid", "The connector workspace path escaped its controlled root.");
        return path;
    }
}
