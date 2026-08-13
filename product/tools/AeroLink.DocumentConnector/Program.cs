using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AeroLink.ConnectorProtocol;
using AeroLink.DocumentSecurity;
using Microsoft.Win32;

namespace AeroLink.DocumentConnector;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        if (args.Contains("--install", StringComparer.OrdinalIgnoreCase)) { InstallProtocol(); MessageBox.Show("The AeroLink desktop connector is installed for this Windows account. Enroll each trusted AeroLink deployment before opening documents.", "AeroLink connector", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
        if (args.Length == 2 && args[0].Equals("--enroll", StringComparison.OrdinalIgnoreCase)) { Enroll(args[1]); return; }
        if (args.Length == 3 && args[0].Equals("--revoke", StringComparison.OrdinalIgnoreCase)) { TrustStore().Revoke(args[1], args[2], Environment.UserName, DateTimeOffset.UtcNow); MessageBox.Show("The connector deployment key was revoked for this Windows account.", "AeroLink connector", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
        if (args.Length == 0) { Application.Run(new RecoveryCenterForm()); return; }
        if (args.Length != 1 || !Uri.TryCreate(args[0], UriKind.Absolute, out var uri) || uri.Scheme != "aerolink") { MessageBox.Show("Open a controlled document from AeroLink Documentation Center, or run the connector without arguments to recover retained work.", "AeroLink connector"); return; }
        try { var form = ConnectorForm.CreateAsync(uri, TrustStore()).GetAwaiter().GetResult(); if (form is not null) Application.Run(form); }
        catch (Exception ex) { LogRejected(ex); MessageBox.Show(ex.Message, "AeroLink connector", MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }

    private static void InstallProtocol()
    {
        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("The connector executable path is unavailable.");
        using var root = Registry.CurrentUser.CreateSubKey(@"Software\Classes\aerolink"); root.SetValue(null, "URL:AeroLink controlled document protocol"); root.SetValue("URL Protocol", "");
        using var icon = root.CreateSubKey("DefaultIcon"); icon.SetValue(null, $"\"{executable}\",0");
        using var command = root.CreateSubKey(@"shell\open\command"); command.SetValue(null, $"\"{executable}\" \"%1\"");
    }

    private static void Enroll(string path)
    {
        path = Path.GetFullPath(path);
        if (!File.Exists(path) || new FileInfo(path).Length > 1024 * 1024) throw new InvalidOperationException("Select a bounded AeroLink connector enrollment manifest.");
        var manifest = JsonSerializer.Deserialize<ConnectorEnrollmentManifest>(File.ReadAllBytes(path), new JsonSerializerOptions(JsonSerializerDefaults.Web))
            ?? throw new InvalidOperationException("The connector enrollment manifest is empty.");
        var message = $"Trust deployment {manifest.DeploymentId}?\n\nOrigin: {manifest.Origin}\nKey fingerprint: {manifest.PublicKeyFingerprint}\n\nEnrolling a new key retires the prior active key for this deployment.";
        if (MessageBox.Show(message, "Enroll AeroLink deployment", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
        TrustStore().Enroll(manifest, Environment.UserName, DateTimeOffset.UtcNow);
        MessageBox.Show("The AeroLink deployment and exact origin are now trusted for this Windows account.", "AeroLink connector", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private static ConnectorTrustStore TrustStore() => new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AeroLink", "DocumentConnector", "trust"));
    private static void LogRejected(Exception ex)
    {
        try
        {
            var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AeroLink", "DocumentConnector", "logs"); Directory.CreateDirectory(root);
            var code = ex is ConnectorProtocolException protocol ? protocol.Code : ex.GetType().Name;
            File.AppendAllLines(Path.Combine(root, "rejected-launches.log"), [$"{DateTimeOffset.UtcNow:O}\t{code}\t{ex.Message.Replace('\r', ' ').Replace('\n', ' ')}"]);
        }
        catch { }
    }
}

internal sealed record Redemption(Guid Id, string AccessToken, string Mode, string DeploymentId, string Origin, Guid ProgramId, Guid ProjectId,
    Guid DocumentId, string DocumentNumber, string Title, Guid RevisionId, string RevisionNumber, DateTimeOffset ExpiresAt,
    long SessionVersion, Guid SourceAttachmentId, long SourceSize, string SourceSha256, Guid EditSessionId, Guid? RecoveryWorkspaceId);
internal sealed record Heartbeat(long Version, DateTimeOffset ExpiresAt);

internal sealed class ConnectorForm : Form
{
    private readonly HttpClient _client; private readonly Redemption _grant; private readonly string _workingFile;
    private readonly ConnectorWorkspaceStore _workspaceStore; private readonly string _workspacePath; private ConnectorWorkspaceMetadata _metadata;
    private readonly Label _status = new() { AutoSize = true, ForeColor = Color.FromArgb(82, 98, 116) };
    private readonly TextBox _comment = new() { Multiline = true, Height = 78, PlaceholderText = "Describe what changed in this check-in…" };
    private readonly Button _primary = new() { Height = 40, BackColor = Color.FromArgb(22, 133, 120), ForeColor = Color.White, FlatStyle = FlatStyle.Flat };
    private readonly Button _discard = new() { Height = 36, Text = "Discard checkout", FlatStyle = FlatStyle.Flat };
    private readonly System.Windows.Forms.Timer _heartbeat = new() { Interval = 4 * 60 * 1000 };
    private long _sessionVersion; private bool _completed; private bool _finalizing; private bool _heartbeatBusy;

    private ConnectorForm(HttpClient client, Redemption grant, string workingFile, ConnectorWorkspaceStore workspaceStore,
        string workspacePath, ConnectorWorkspaceMetadata metadata)
    {
        _client = client; _grant = grant; _workingFile = workingFile; _workspaceStore = workspaceStore; _workspacePath = workspacePath;
        _metadata = metadata; _sessionVersion = grant.SessionVersion;
        Text = grant.Mode == "release" ? "AeroLink — Prepare document release" : "AeroLink — Controlled Word checkout"; Width = 620; Height = 480; MinimumSize = new(540, 440); StartPosition = FormStartPosition.CenterScreen; BackColor = Color.White; Font = new("Segoe UI", 10);
        var title = new Label { AutoSize = true, MaximumSize = new(530, 0), Font = new("Segoe UI Semibold", 18), Text = grant.Title };
        var number = new Label { AutoSize = true, ForeColor = Color.FromArgb(46, 116, 181), Font = new("Segoe UI Semibold", 10), Text = grant.RevisionNumber };
        var verifiedContext = new Label { AutoSize = true, MaximumSize = new(530, 0), ForeColor = Color.FromArgb(45, 75, 70), Text = $"Verified deployment: {grant.DeploymentId}\nOrigin: {grant.Origin}\nProject: {grant.ProjectId}\nDocument/revision: {grant.DocumentId} / {grant.RevisionId}\nMode: {grant.Mode}" };
        var guidance = new Label { AutoSize = true, MaximumSize = new(530, 0), Text = grant.Mode == "release" ? "Word is opening the exact reviewed Draft. Only the named AeroLink status controls change to Released and the named DRAFT watermark is removed; all other reviewed content is preserved. AeroLink verifies the exact transformation and hashes both files before final signature." : "Edit this exclusive working copy in Microsoft Word, save normally, then return here to check it in. The faint DRAFT watermark must remain." };
        _primary.Text = grant.Mode == "release" ? "Prepare DOCX + PDF release candidate" : "Check in saved Word document"; _status.Text = $"Checkout active until {grant.ExpiresAt.LocalDateTime:g}";
        var file = new LinkLabel { AutoSize = true, Text = workingFile }; file.LinkClicked += (_,_) => OpenWord();
        var panel = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(26), AutoScroll = true };
        panel.Controls.Add(number); panel.Controls.Add(title); panel.Controls.Add(verifiedContext); panel.Controls.Add(guidance); panel.Controls.Add(new Label { AutoSize = true, Text = "Working copy", Font = new("Segoe UI Semibold", 9), Margin = new Padding(0, 18, 0, 3) }); panel.Controls.Add(file);
        if (grant.Mode == "edit") { panel.Controls.Add(new Label { AutoSize = true, Text = "Required check-in comment", Font = new("Segoe UI Semibold", 9), Margin = new Padding(0, 18, 0, 3) }); _comment.Width = 530; panel.Controls.Add(_comment); }
        _primary.Width = 530; _primary.Margin = new Padding(0, 18, 0, 5); _discard.Width = 530; panel.Controls.Add(_primary); panel.Controls.Add(_discard); _status.Margin = new Padding(0, 10, 0, 0); panel.Controls.Add(_status); Controls.Add(panel);
        _primary.Click += async (_,_) => await CompleteAsync(); _discard.Click += async (_,_) => await DiscardAsync(); _heartbeat.Tick += async (_,_) => await HeartbeatAsync(); FormClosing += OnClosing; _heartbeat.Start(); Shown += (_,_) => OpenWord();
    }

    public static async Task<ConnectorForm?> CreateAsync(Uri launch, ConnectorTrustStore trust)
    {
        if (!launch.Scheme.Equals("aerolink", StringComparison.OrdinalIgnoreCase) || !launch.Host.Equals("document", StringComparison.OrdinalIgnoreCase)) throw new ConnectorProtocolException("connector_launch_invalid", "The AeroLink launch target is invalid.");
        var query = ParseQuery(launch.Query); if (query.Count != 1 || !query.TryGetValue("envelope", out var compact)) throw new ConnectorProtocolException("connector_envelope_invalid", "The AeroLink handoff must contain only one signed launch envelope.");
        var identity = ConnectorLaunchProtocol.ReadUnverifiedIdentity(compact); var enrolled = trust.Require(identity.DeploymentId, identity.KeyId);
        var envelope = ConnectorLaunchProtocol.Verify(compact, enrolled.PublicKey, DateTimeOffset.UtcNow); trust.Require(envelope);
        trust.ConsumeNonce(envelope.DeploymentId, envelope.Nonce, envelope.ExpiresAt, DateTimeOffset.UtcNow);
        var store = ConnectorLocalRuntime.Store();
        if (envelope.Mode is "discard" or "cleanup")
        {
            var workspace = ConnectorWorkspaceLayout.ResolveExisting(ConnectorLocalRuntime.WorkingRoot, envelope, envelope.RecoveryWorkspaceId!.Value);
            var commandMetadata = store.Load(workspace); ValidateWorkspace(envelope, commandMetadata);
            var working = Path.Combine(workspace, commandMetadata.WorkingFileName); var wordState = WordDocumentStateProbe.Inspect(working);
            if (!ConnectorWorkspaceLifecycle.CanCleanup(wordState)) throw new ConnectorProtocolException("connector_workspace_in_use", "Close Microsoft Word before discarding or cleaning this retained workspace.");
            if (envelope.Mode == "cleanup") await ConnectorLocalRuntime.VerifyCompletionAsync(commandMetadata, workspace, envelope.CompletionEvidenceJson!);
            store.Delete(workspace); MessageBox.Show(envelope.Mode == "cleanup" ? "AeroLink verified the server completion evidence and cleaned the retained workspace." : "The authenticated discard was recorded and the local workspace was removed.", "AeroLink connector", MessageBoxButtons.OK, MessageBoxIcon.Information); return null;
        }
        var origin = new Uri(envelope.Origin, UriKind.Absolute);
        var client = ConnectorHttpPolicy.CreateClient(origin, TimeSpan.FromMinutes(3));
        using var response = await client.PostAsync($"/api/document-connector/redeem/{Uri.EscapeDataString(envelope.Nonce)}", null); await EnsureAsync(response, client);
        var grant = await response.Content.ReadFromJsonAsync<Redemption>(JsonOptions) ?? throw new InvalidOperationException("AeroLink returned an incomplete connector grant."); client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", grant.AccessToken);
        ValidateRedemption(envelope, grant);
        var root = envelope.RecoveryWorkspaceId is Guid recoveryId
            ? ConnectorWorkspaceLayout.ResolveExisting(ConnectorLocalRuntime.WorkingRoot, envelope, recoveryId)
            : ConnectorWorkspaceLayout.CreateNew(ConnectorLocalRuntime.WorkingRoot, envelope, grant.Id);
        ConnectorWorkspaceMetadata metadata; string path;
        if (envelope.RecoveryWorkspaceId is not null)
        {
            metadata = store.Load(root); ValidateWorkspace(envelope, metadata); path = Path.Combine(root, metadata.WorkingFileName);
            metadata = metadata with { EditSessionId = grant.EditSessionId, ActiveGrantId = grant.Id, State = ConnectorWorkspaceState.Connected,
                UpdatedAt = DateTimeOffset.UtcNow, HeartbeatFailures = 0, LeaseExpiresAt = grant.ExpiresAt }; store.Save(root, metadata);
            if (File.Exists(path)) AeroLinkOoxmlProfile.ValidateFile(path);
            else { await DownloadAsync(client, grant, envelope, path); metadata=metadata with{LocalSha256=envelope.SourceSha256,UpdatedAt=DateTimeOffset.UtcNow};store.Save(root,metadata); }
        }
        else
        {
            var now = DateTimeOffset.UtcNow; var fileName = ConnectorWorkspaceLayout.SafeDocumentFileName(envelope.RevisionNumber); path = Path.Combine(root, fileName);
            metadata = new(2, grant.Id, envelope.DeploymentId, envelope.Origin, grant.ProgramId, envelope.ProjectId,
                envelope.DocumentId, envelope.DocumentNumber, envelope.RevisionId, envelope.RevisionNumber, grant.EditSessionId,
                grant.Id, envelope.Mode, envelope.SourceAttachmentId, envelope.SourceSha256, fileName,
                ConnectorWorkspaceState.Downloading, now, now, LeaseExpiresAt: grant.ExpiresAt); store.Save(root, metadata);
            await DownloadAsync(client, grant, envelope, path); metadata = metadata with { State = ConnectorWorkspaceState.Connected,
                UpdatedAt = DateTimeOffset.UtcNow, LocalSha256 = envelope.SourceSha256 }; store.Save(root, metadata);
        }
        return new ConnectorForm(client, grant, path, store, root, metadata);
    }

    private static async Task DownloadAsync(HttpClient client, Redemption grant, ConnectorLaunchEnvelope envelope, string path)
    {
        var temporaryPath = Path.Combine(Path.GetDirectoryName(path)!, $"download-{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var output = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                using var download = await client.GetAsync($"/api/document-connector/{grant.Id}/download", HttpCompletionOption.ResponseHeadersRead); await EnsureAsync(download, client);
                if (download.Content.Headers.ContentLength is long declared && declared != envelope.SourceSize) throw new ConnectorProtocolException("connector_download_size_mismatch", "The controlled download length does not match the signed envelope.");
                await using var source = await download.Content.ReadAsStreamAsync(); await ConnectorLaunchProtocol.CopyExactlyAsync(source, output, envelope.SourceSize);
            }
            var actualHash = await ConnectorLocalRuntime.HashFileAsync(temporaryPath);
            if (!CryptographicOperations.FixedTimeEquals(Convert.FromHexString(actualHash), Convert.FromHexString(envelope.SourceSha256)))
                throw new ConnectorProtocolException("connector_download_hash_mismatch", "The controlled download hash does not match the signed launch envelope. Word was not opened.");
            AeroLinkOoxmlProfile.ValidateFile(temporaryPath, envelope.SourceSize, envelope.SourceSha256);
            File.Move(temporaryPath, path, false); ApplyWindowsAttachmentPolicy(path, envelope.Origin);
        }
        finally { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); }
    }

    private static void ValidateWorkspace(ConnectorLaunchEnvelope envelope, ConnectorWorkspaceMetadata metadata)
    {
        if (metadata.WorkspaceId != envelope.RecoveryWorkspaceId || metadata.DeploymentId != envelope.DeploymentId
            || metadata.ProjectId != envelope.ProjectId || metadata.DocumentId != envelope.DocumentId
            || metadata.RevisionId != envelope.RevisionId || metadata.BaseAttachmentId != envelope.SourceAttachmentId
            || !string.Equals(metadata.BaseSha256, envelope.SourceSha256, StringComparison.OrdinalIgnoreCase))
            throw new ConnectorProtocolException("connector_workspace_mismatch", "The signed recovery command does not match this protected local workspace.");
    }

    private void OpenWord() { try { Process.Start(new ProcessStartInfo(_workingFile) { UseShellExecute = true }); _status.Text = "Microsoft Word opened the controlled working copy."; } catch { _status.Text = "Open the working copy from the link above in Microsoft Word."; } }
    private async Task CompleteAsync()
    {
        if (_grant.Mode == "edit" && string.IsNullOrWhiteSpace(_comment.Text)) { MessageBox.Show("Enter a check-in comment describing what changed.", "AeroLink connector"); return; }
        var wordState = WordDocumentStateProbe.Inspect(_workingFile);
        if (!ConnectorWorkspaceLifecycle.CanUpload(wordState)) { MessageBox.Show(wordState == ConnectorWordDocumentState.OpenUnsaved ? "Save the open Word document before checking it in." : "AeroLink cannot prove the Word file is saved and readable. Close Word and try again.", "AeroLink connector", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
        Toggle(false); _finalizing = true; _heartbeat.Stop();
        _metadata = _metadata with { State = ConnectorWorkspaceState.Finalizing, UpdatedAt = DateTimeOffset.UtcNow }; _workspaceStore.Save(_workspacePath, _metadata);
        try
        {
            if (_grant.Mode == "release") await UploadReleaseAsync(); else await UploadDraftAsync();
            _completed = true; _metadata = _metadata with { State = ConnectorWorkspaceState.CleanupPending, UpdatedAt = DateTimeOffset.UtcNow }; _workspaceStore.Save(_workspacePath, _metadata);
            if (ConnectorWorkspaceLifecycle.CanCleanup(WordDocumentStateProbe.Inspect(_workingFile))) _workspaceStore.Delete(_workspacePath);
            MessageBox.Show(_grant.Mode == "release" ? "The exact DOCX and PDF candidate is ready for final AeroLink signature. Server hashes were verified." : "The Word document was checked in and its returned attachment hash was verified.", "AeroLink connector", MessageBoxButtons.OK, MessageBoxIcon.Information); Close();
        }
        catch (Exception ex)
        {
            _finalizing = false;var code=(ex as ConnectorHttpException)?.Code;var terminal=code is "document_snapshot_conflict" or "document_revision_not_editable" or "release_stage_changed";var nextState=code=="document_snapshot_conflict"?ConnectorWorkspaceState.SourceConflict:terminal?ConnectorWorkspaceState.Abandoned:ConnectorWorkspaceState.Connected; _metadata = _metadata with { State = nextState, UpdatedAt = DateTimeOffset.UtcNow,RetainUntil=ConnectorWorkspaceLifecycle.RetainUntil(nextState,DateTimeOffset.UtcNow) }; _workspaceStore.Save(_workspacePath, _metadata);
            if(!terminal)_heartbeat.Start(); _status.Text = ex.Message; Toggle(!terminal);
        }
    }
    private async Task UploadDraftAsync()
    { AeroLinkOoxmlProfile.ValidateFile(_workingFile); var localHash = await ConnectorLocalRuntime.HashFileAsync(_workingFile); using var form = new MultipartFormDataContent(); form.Add(new StringContent(_comment.Text.Trim()), "comment"); form.Add(new StringContent(_sessionVersion.ToString()), "expectedVersion"); var file = new StreamContent(File.OpenRead(_workingFile)); file.Headers.ContentType = new("application/vnd.openxmlformats-officedocument.wordprocessingml.document"); form.Add(file, "file", Path.GetFileName(_workingFile)); using var response = await _client.PostAsync($"/api/document-connector/{_grant.Id}/check-in", form); await EnsureAsync(response, _client); using var result=JsonDocument.Parse(await response.Content.ReadAsStringAsync()); if(result.RootElement.GetProperty("attachmentId").GetGuid()==Guid.Empty||!string.Equals(result.RootElement.GetProperty("sha256").GetString(),localHash,StringComparison.OrdinalIgnoreCase))throw new ConnectorProtocolException("connector_completion_mismatch","AeroLink did not return the exact attachment ID and SHA-256 for this local Word file. The workspace was preserved."); _metadata=_metadata with{LocalSha256=localHash}; _workspaceStore.Save(_workspacePath,_metadata); }
    private async Task UploadReleaseAsync()
    {
        var outputRoot = ConnectorWorkspaceLifecycle.CreateCandidateSet(_workspacePath); var relativeCandidate = Path.GetRelativePath(_workspacePath, outputRoot); var docx = Path.Combine(outputRoot, SafeFileName(_grant.DocumentNumber) + "-RELEASE-CANDIDATE.docx"); var pdf = Path.Combine(outputRoot, SafeFileName(_grant.DocumentNumber) + "-RELEASE-CANDIDATE.pdf"); _metadata=_metadata with{CandidateDirectoryName=relativeCandidate,UpdatedAt=DateTimeOffset.UtcNow};_workspaceStore.Save(_workspacePath,_metadata); WordReleaseRenderer.Create(_workingFile, docx, pdf);
        AeroLinkOoxmlProfile.ValidateFile(docx);
        var docxHash=await ConnectorLocalRuntime.HashFileAsync(docx);var pdfHash=await ConnectorLocalRuntime.HashFileAsync(pdf);using var form = new MultipartFormDataContent(); form.Add(new StringContent(_sessionVersion.ToString()), "expectedVersion"); var docxFile = new StreamContent(File.OpenRead(docx)); docxFile.Headers.ContentType = new("application/vnd.openxmlformats-officedocument.wordprocessingml.document"); form.Add(docxFile, "docx", Path.GetFileName(docx)); var pdfFile = new StreamContent(File.OpenRead(pdf)); pdfFile.Headers.ContentType = new("application/pdf"); form.Add(pdfFile, "pdf", Path.GetFileName(pdf)); using var response = await _client.PostAsync($"/api/document-connector/{_grant.Id}/release-candidate", form); await EnsureAsync(response, _client);using var result=JsonDocument.Parse(await response.Content.ReadAsStringAsync());if(!string.Equals(result.RootElement.GetProperty("docxSha256").GetString(),docxHash,StringComparison.OrdinalIgnoreCase)||!string.Equals(result.RootElement.GetProperty("pdfSha256").GetString(),pdfHash,StringComparison.OrdinalIgnoreCase))throw new ConnectorProtocolException("connector_completion_mismatch","AeroLink did not return the exact hashes for this local release-candidate pair. The workspace was preserved.");_metadata=_metadata with{CandidateDocxSha256=docxHash,CandidatePdfSha256=pdfHash};_workspaceStore.Save(_workspacePath,_metadata);
    }
    private async Task HeartbeatAsync()
    {
        if (_heartbeatBusy || _finalizing) return; _heartbeatBusy = true; _heartbeat.Stop();
        try
        {
            using var response = await _client.PostAsJsonAsync($"/api/document-connector/{_grant.Id}/heartbeat", new { expectedVersion = _sessionVersion }); await EnsureAsync(response, _client); var heartbeat = await response.Content.ReadFromJsonAsync<Heartbeat>(JsonOptions);
            if (heartbeat is not null) { _sessionVersion = heartbeat.Version; var decision=ConnectorHeartbeatPolicy.Success(); _metadata=_metadata with{State=decision.State,HeartbeatFailures=0,LeaseExpiresAt=heartbeat.ExpiresAt,UpdatedAt=DateTimeOffset.UtcNow};_workspaceStore.Save(_workspacePath,_metadata);_heartbeat.Interval=(int)decision.NextDelay.TotalMilliseconds;_heartbeat.Start();_status.Text=$"Checkout connected until {heartbeat.ExpiresAt.LocalDateTime:g}"; }
        }
        catch (Exception ex)
        {
            var code=(ex as ConnectorHttpException)?.Code;var lease=_metadata.LeaseExpiresAt??DateTimeOffset.UtcNow;var decision=ConnectorHeartbeatPolicy.Failure(_metadata.HeartbeatFailures,DateTimeOffset.UtcNow,lease,code);_metadata=_metadata with{State=decision.State,HeartbeatFailures=decision.Failures,UpdatedAt=DateTimeOffset.UtcNow,RetainUntil=ConnectorWorkspaceLifecycle.RetainUntil(decision.State,DateTimeOffset.UtcNow)};_workspaceStore.Save(_workspacePath,_metadata);_status.Text=decision.Terminal?$"Checkout {decision.State}: {ex.Message}":$"Checkout {decision.State}; retrying in {decision.NextDelay.TotalSeconds:0}s: {ex.Message}";if(!decision.Terminal){_heartbeat.Interval=(int)decision.NextDelay.TotalMilliseconds;_heartbeat.Start();}
        }
        finally { _heartbeatBusy=false; }
    }
    private async Task DiscardAsync()
    { if (MessageBox.Show("Discard this checkout? Saved local changes will not be uploaded.", "AeroLink connector", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;if(!ConnectorWorkspaceLifecycle.CanCleanup(WordDocumentStateProbe.Inspect(_workingFile))){MessageBox.Show("Close Microsoft Word before discarding this workspace.","AeroLink connector",MessageBoxButtons.OK,MessageBoxIcon.Warning);return;} Toggle(false); try { using var response = await _client.PostAsJsonAsync($"/api/document-connector/{_grant.Id}/discard", new { expectedVersion = _sessionVersion, reason = "User discarded the desktop checkout." }); await EnsureAsync(response, _client); _completed = true; _heartbeat.Stop();_metadata=_metadata with{State=ConnectorWorkspaceState.Discarded,UpdatedAt=DateTimeOffset.UtcNow};_workspaceStore.Save(_workspacePath,_metadata);_workspaceStore.Delete(_workspacePath); Close(); } catch (Exception ex) { _status.Text = ex.Message; Toggle(true); } }
    private void OnClosing(object? sender, FormClosingEventArgs e)
    {
        if (_completed) return;
        if (MessageBox.Show("Close and retain this local workspace for browser-authenticated recovery? The server checkout remains active only until its lease expires.", "AeroLink connector", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) { e.Cancel = true; return; }
        _heartbeat.Stop(); var now = DateTimeOffset.UtcNow;
        _metadata = _metadata with { State = ConnectorWorkspaceState.Abandoned, UpdatedAt = now,
            RetainUntil = ConnectorWorkspaceLifecycle.RetainUntil(ConnectorWorkspaceState.Abandoned, now) };
        _workspaceStore.Save(_workspacePath, _metadata);
    }
    private void Toggle(bool enabled) { _primary.Enabled = enabled; _discard.Enabled = enabled; _comment.Enabled = enabled; }
    private static void ValidateRedemption(ConnectorLaunchEnvelope envelope, Redemption grant)
    {
        if (grant.Id == Guid.Empty || string.IsNullOrWhiteSpace(grant.AccessToken)) throw new ConnectorProtocolException("connector_redemption_mismatch", "The server redemption response is incomplete.");
        ConnectorLaunchProtocol.ValidateRedemption(envelope, new(grant.Mode, grant.DeploymentId, grant.Origin,
            grant.ProjectId, grant.DocumentId, grant.DocumentNumber, grant.RevisionId, grant.RevisionNumber,
            grant.SourceAttachmentId, grant.SourceSize, grant.SourceSha256, grant.EditSessionId, grant.RecoveryWorkspaceId));
    }

    private static void ApplyWindowsAttachmentPolicy(string path, string origin)
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            // Zone 2 deliberately asks Office to apply its trusted/intranet attachment policy. The file has
            // already passed exact size/hash and safe-OOXML validation; organizational Office policy remains
            // authoritative about whether editing opens directly or through Protected View.
            File.WriteAllText(path + ":Zone.Identifier", $"[ZoneTransfer]\r\nZoneId=2\r\nHostUrl={origin}\r\n", Encoding.ASCII);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            throw new ConnectorProtocolException("connector_attachment_policy_failed", "Windows attachment policy could not be applied, so Word was not opened.", ex);
        }
    }

    private static Dictionary<string,string> ParseQuery(string value)
    {
        try { return value.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries).Select(part => part.Split('=', 2)).Where(pair => pair.Length == 2).ToDictionary(pair => Uri.UnescapeDataString(pair[0]), pair => Uri.UnescapeDataString(pair[1]), StringComparer.OrdinalIgnoreCase); }
        catch (ArgumentException ex) { throw new ConnectorProtocolException("connector_envelope_invalid", "The connector launch query contains duplicate fields.", ex); }
    }
    private static string SafeFileName(string value) => string.Concat(value.Select(character => Path.GetInvalidFileNameChars().Contains(character) ? '_' : character));
    private static async Task EnsureAsync(HttpResponseMessage response, HttpClient client) { ConnectorHttpPolicy.ValidateResponse(response, client.BaseAddress!); if (response.IsSuccessStatusCode) return; var message = "AeroLink did not complete the connector operation.";string? code=null; try { using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync()); if (json.RootElement.TryGetProperty("error", out var error)) message = error.GetString() ?? message;if(json.RootElement.TryGetProperty("code",out var errorCode))code=errorCode.GetString(); } catch { } throw new ConnectorHttpException(code,message); }
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
}

internal sealed class ConnectorHttpException(string? code,string message):Exception(message){public string? Code{get;}=code;}

internal static class WordReleaseRenderer
{
    // These tags must stay identical to WordDocumentStructure.StatusTag / WatermarkTag in
    // AeroLink.Infrastructure. The connector intentionally does not reference that assembly
    // (it would drag the ASP.NET/EF stack into the desktop tool), so the server validates
    // whatever the connector produced and fails closed if the two ever drift.
    private const string StatusTag = "AeroLink.Status";
    private const string WatermarkTag = "AeroLink.Watermark";

    public static void Create(string source, string docx, string pdf)
    {
        var wordType = Type.GetTypeFromProgID("Word.Application") ?? throw new InvalidOperationException("Microsoft Word desktop is required to prepare the approved PDF rendition."); dynamic? word = null, document = null;
        try { word = Activator.CreateInstance(wordType) ?? throw new InvalidOperationException("Microsoft Word could not be started."); word.Visible = false; word.DisplayAlerts = 0; document = word.Documents.Open(source, ReadOnly: true); var content = document.Content; ApplyStory(content); Marshal.FinalReleaseComObject(content); foreach (dynamic section in document.Sections) { foreach (dynamic header in section.Headers) { var headerRange = header.Range; ApplyStory(headerRange); Marshal.FinalReleaseComObject(headerRange); Marshal.FinalReleaseComObject(header); } foreach (dynamic footer in section.Footers) { var footerRange = footer.Range; ApplyStory(footerRange); Marshal.FinalReleaseComObject(footerRange); Marshal.FinalReleaseComObject(footer); } Marshal.FinalReleaseComObject(section); } document.SaveAs2(docx, 16); document.ExportAsFixedFormat(pdf, 17, OpenAfterExport: false, OptimizeFor: 0, Range: 0, Item: 0, IncludeDocProps: true, KeepIRM: true, CreateBookmarks: 1, DocStructureTags: true, BitmapMissingFonts: true, UseISO19005_1: false); }
        catch (COMException ex) { throw new InvalidOperationException("Microsoft Word could not prepare the release rendition. Close any prompt in Word, save the working copy, and try again.", ex); }
        finally { if (document is not null) { try { document.Close(false); } catch { } Marshal.FinalReleaseComObject(document); } if (word is not null) { try { word.Quit(); } catch { } Marshal.FinalReleaseComObject(word); } }
    }

    /// <summary>
    /// Changes only the named AeroLink content controls: every AeroLink.Status control becomes Released and
    /// every AeroLink.Watermark control is removed with its contents. Ordinary text, shapes and drawings are
    /// never searched, relabelled or deleted.
    /// </summary>
    private static void ApplyStory(dynamic range)
    {
        var controls = new List<dynamic>();
        try
        {
            foreach (dynamic control in range.ContentControls)
                if (control is not null) controls.Add(control);
        }
        catch { return; }
        foreach (dynamic control in controls)
        {
            string? tag = null;
            try { tag = (string)control.Tag; } catch { }
            try
            {
                if (tag == WatermarkTag) control.Delete(DeleteContents: true);
                else if (tag == StatusTag) control.Range.Text = "Released";
            }
            catch { }
            Marshal.FinalReleaseComObject(control);
        }
    }
}
