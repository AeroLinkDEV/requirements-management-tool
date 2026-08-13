using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AeroLink.ConnectorProtocol;

namespace AeroLink.DocumentConnector;

internal static class ConnectorLocalRuntime
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("AeroLink.DocumentConnector.workspace.v2");
    public static string WorkingRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AeroLink", "DocumentConnector", "working");
    public static ConnectorWorkspaceStore Store() => new(WorkingRoot,
        value => ProtectedData.Protect(value, Entropy, DataProtectionScope.CurrentUser),
        value => ProtectedData.Unprotect(value, Entropy, DataProtectionScope.CurrentUser));

    public static async Task<string> HashFileAsync(string path)
    {
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexString(await SHA256.HashDataAsync(input)).ToLowerInvariant();
    }

    public static string RecoveryUrl(ConnectorWorkspaceMetadata value) =>
        $"{value.Origin}/programs/{value.ProgramId}/projects/{value.ProjectId}/documentation-center/{value.DocumentId}?recoveryWorkspaceId={value.WorkspaceId}&recoveryRevisionId={value.RevisionId}";

    public static async Task VerifyCompletionAsync(ConnectorWorkspaceMetadata metadata, string workspacePath, string evidenceJson)
    {
        using var evidence = JsonDocument.Parse(evidenceJson, new JsonDocumentOptions { MaxDepth = 8 });
        if (metadata.Mode == "edit")
        {
            var expected = evidence.RootElement.TryGetProperty("sha256", out var canonical)
                ? canonical.GetString() : evidence.RootElement.GetProperty("Sha256").GetString();
            if (metadata.AcceptedAttachmentId is Guid acceptedId && evidence.RootElement.GetProperty("attachmentId").GetGuid() != acceptedId)
                throw new ConnectorProtocolException("connector_cleanup_evidence_mismatch", "The server completion attachment ID does not match this retained workspace. The workspace was preserved.");
            var actual = await HashFileAsync(Path.Combine(workspacePath, metadata.WorkingFileName));
            if (!string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
                throw new ConnectorProtocolException("connector_cleanup_evidence_mismatch", "The server completion hash does not match the retained local Word file. The workspace was preserved.");
            return;
        }
        if (string.IsNullOrWhiteSpace(metadata.CandidateDirectoryName))
            throw new ConnectorProtocolException("connector_cleanup_evidence_mismatch", "The retained release-candidate pair is incomplete. The workspace was preserved.");
        var candidateRoot = Path.Combine(workspacePath, metadata.CandidateDirectoryName);
        var docx = Directory.EnumerateFiles(candidateRoot, "*.docx", SearchOption.TopDirectoryOnly).Single();
        var pdf = Directory.EnumerateFiles(candidateRoot, "*.pdf", SearchOption.TopDirectoryOnly).Single();
        var expectedDocx = evidence.RootElement.GetProperty("docxSha256").GetString();
        var expectedPdf = evidence.RootElement.GetProperty("pdfSha256").GetString();
        if ((metadata.CandidateDocxAttachmentId is Guid docxId && evidence.RootElement.GetProperty("docxAttachmentId").GetGuid() != docxId)
            || (metadata.CandidatePdfAttachmentId is Guid pdfId && evidence.RootElement.GetProperty("pdfAttachmentId").GetGuid() != pdfId)
            || !string.Equals(expectedDocx, await HashFileAsync(docx), StringComparison.OrdinalIgnoreCase)
            || !string.Equals(expectedPdf, await HashFileAsync(pdf), StringComparison.OrdinalIgnoreCase))
            throw new ConnectorProtocolException("connector_cleanup_evidence_mismatch", "The server completion hashes do not match the retained release-candidate pair. The workspace was preserved.");
    }
}

internal static class WordDocumentStateProbe
{
    private const int MkUnavailable = unchecked((int)0x800401E3);
    public static ConnectorWordDocumentState Inspect(string path)
    {
        object? application = null;
        try
        {
            if (CLSIDFromProgID("Word.Application", out var clsid) != 0) return FileState(path);
            var result = GetActiveObject(ref clsid, IntPtr.Zero, out application);
            if (result == MkUnavailable) return FileState(path);
            if (result != 0 || application is null) return ConnectorWordDocumentState.Unknown;
            dynamic word = application;
            foreach (dynamic document in word.Documents)
            {
                try
                {
                    var fullName = (string)document.FullName;
                    if (string.Equals(Path.GetFullPath(fullName), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase))
                        return (bool)document.Saved ? ConnectorWordDocumentState.OpenSaved : ConnectorWordDocumentState.OpenUnsaved;
                }
                finally { Marshal.FinalReleaseComObject(document); }
            }
            return FileState(path);
        }
        catch (COMException) { return ConnectorWordDocumentState.Unknown; }
        finally { if (application is not null) Marshal.FinalReleaseComObject(application); }
    }

    private static ConnectorWordDocumentState FileState(string path)
    {
        if (!File.Exists(path)) return ConnectorWordDocumentState.Closed;
        try { using var _ = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None); return ConnectorWordDocumentState.Closed; }
        catch (IOException) { return ConnectorWordDocumentState.Locked; }
        catch (UnauthorizedAccessException) { return ConnectorWordDocumentState.Unknown; }
    }

    [DllImport("ole32.dll", CharSet = CharSet.Unicode)] private static extern int CLSIDFromProgID(string progId, out Guid clsid);
    [DllImport("oleaut32.dll", PreserveSig = true)] private static extern int GetActiveObject(ref Guid clsid, IntPtr reserved, [MarshalAs(UnmanagedType.IUnknown)] out object? value);
}

internal sealed class RecoveryCenterForm : Form
{
    private readonly ListBox _workspaces = new() { Dock = DockStyle.Fill, DisplayMember = nameof(WorkspaceItem.Label) };
    private readonly IReadOnlyList<WorkspaceItem> _items;

    public RecoveryCenterForm()
    {
        Text = "AeroLink — Recover local document work"; Width = 760; Height = 440; StartPosition = FormStartPosition.CenterScreen;
        var store = ConnectorLocalRuntime.Store();
        try
        {
            var items = store.Scan().Select(value => new WorkspaceItem(value.Path, value.Metadata,
                Path.Combine(value.Path, value.Metadata.WorkingFileName))).ToList();
            if (Directory.Exists(ConnectorLocalRuntime.WorkingRoot))
            {
                var authenticatedPaths = items.Select(item => Path.GetFullPath(item.Path)).ToHashSet(StringComparer.OrdinalIgnoreCase);
                items.AddRange(Directory.EnumerateFiles(ConnectorLocalRuntime.WorkingRoot, ConnectorWorkspaceStore.MetadataFileName, SearchOption.AllDirectories)
                    .Select(path => Path.GetDirectoryName(path)!)
                    .Where(path => !authenticatedPaths.Contains(Path.GetFullPath(path)))
                    .SelectMany(path => Directory.EnumerateFiles(path, "*.docx", SearchOption.TopDirectoryOnly).Take(1)
                        .Select(docx => new WorkspaceItem(path, null, docx))));
                items.AddRange(Directory.EnumerateFiles(ConnectorLocalRuntime.WorkingRoot, "*.docx", SearchOption.TopDirectoryOnly)
                    .Select(path => new WorkspaceItem(Path.GetDirectoryName(path)!, null, path)));
                items.AddRange(Directory.EnumerateFiles(ConnectorLocalRuntime.WorkingRoot, "workspace.json", SearchOption.AllDirectories)
                    .Where(path => !File.Exists(Path.Combine(Path.GetDirectoryName(path)!, ConnectorWorkspaceStore.MetadataFileName)))
                    .Select(path => Path.GetDirectoryName(path)!)
                    .SelectMany(path => Directory.EnumerateFiles(path, "*.docx", SearchOption.TopDirectoryOnly).Take(1)
                        .Select(docx => new WorkspaceItem(path, null, docx))));
            }
            items = items.GroupBy(item => Path.GetFullPath(item.SourcePath), StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First()).ToList();
            _items = items.OrderByDescending(x => x.Metadata?.UpdatedAt ?? File.GetLastWriteTimeUtc(x.SourcePath)).ToList();
        }
        catch (Exception ex) { _items = []; MessageBox.Show(ex.Message, "AeroLink recovery", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        foreach (var item in _items) _workspaces.Items.Add(item);
        var recover = new Button { Text = "Authenticate in AeroLink", AutoSize = true };
        var export = new Button { Text = "Export retained copy", AutoSize = true };
        var close = new Button { Text = "Close", AutoSize = true };
        recover.Click += (_, _) => { if (_workspaces.SelectedItem is not WorkspaceItem item) return;if(item.Metadata is null){MessageBox.Show("This legacy working copy has no trustworthy server/session metadata. Export it for controlled reconciliation; AeroLink will not upload it automatically.");return;}Process.Start(new ProcessStartInfo(ConnectorLocalRuntime.RecoveryUrl(item.Metadata)) { UseShellExecute = true }); };
        export.Click += (_, _) => ExportSelected(); close.Click += (_, _) => Close();
        var actions = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 54, Padding = new Padding(10) }; actions.Controls.AddRange([recover, export, close]);
        var guidance = new Label { Dock = DockStyle.Top, Height = 72, Padding = new Padding(12), Text = "AeroLink preserved these local workspaces. Authenticate in the browser to resume or discard one. Source conflicts remain export-only and are never uploaded automatically." };
        Controls.Add(_workspaces); Controls.Add(actions); Controls.Add(guidance);
        if (_items.Count > 0) _workspaces.SelectedIndex = 0;
    }

    private void ExportSelected()
    {
        if (_workspaces.SelectedItem is not WorkspaceItem item) return;
        var source = item.SourcePath; if (!File.Exists(source)) { MessageBox.Show("This interrupted download has no complete Word file to export."); return; }
        if (item.Metadata?.CandidateDirectoryName is { Length: > 0 } candidateName)
        {
            var candidateRoot = Path.Combine(item.Path, candidateName);
            if (Directory.Exists(candidateRoot))
            {
                using var folder = new FolderBrowserDialog { Description = "Choose a folder for the retained AeroLink working copy and release-candidate pair" };
                if (folder.ShowDialog() != DialogResult.OK) return;
                var exportRoot = Path.Combine(folder.SelectedPath, $"AeroLink-{item.Metadata.RevisionNumber}-{item.Metadata.WorkspaceId:N}");
                Directory.CreateDirectory(exportRoot);
                File.Copy(source, Path.Combine(exportRoot, Path.GetFileName(source)), overwrite: false);
                foreach (var candidate in Directory.EnumerateFiles(candidateRoot, "*", SearchOption.TopDirectoryOnly))
                    File.Copy(candidate, Path.Combine(exportRoot, Path.GetFileName(candidate)), overwrite: false);
                MessageBox.Show($"The retained working copy and release candidates were exported to {exportRoot}.");
                return;
            }
        }
        using var dialog = new SaveFileDialog { FileName = Path.GetFileName(source), Filter = "Word document (*.docx)|*.docx", OverwritePrompt = true };
        if (dialog.ShowDialog() == DialogResult.OK) File.Copy(source, dialog.FileName, overwrite: true);
    }

    private sealed record WorkspaceItem(string Path, ConnectorWorkspaceMetadata? Metadata, string SourcePath)
    {
        private ConnectorWorkspaceState EffectiveState => Metadata is not null
            && Metadata.LeaseExpiresAt <= DateTimeOffset.UtcNow
            && Metadata.State is ConnectorWorkspaceState.Connected or ConnectorWorkspaceState.Retrying or ConnectorWorkspaceState.LeaseAtRisk
                ? ConnectorWorkspaceState.Expired : Metadata?.State ?? ConnectorWorkspaceState.Abandoned;
        public string Label => Metadata is null?$"Legacy retained copy — {System.IO.Path.GetFileName(SourcePath)} — export only":$"{Metadata.RevisionNumber} — {EffectiveState} — {Metadata.DeploymentId} — updated {Metadata.UpdatedAt.LocalDateTime:g}";
    }
}
