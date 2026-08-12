using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Win32;

namespace AeroLink.DocumentConnector;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        if (args.Contains("--install", StringComparer.OrdinalIgnoreCase)) { InstallProtocol(); MessageBox.Show("The AeroLink desktop connector is installed for this Windows account.", "AeroLink connector", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
        if (args.Length != 1 || !Uri.TryCreate(args[0], UriKind.Absolute, out var uri) || uri.Scheme != "aerolink") { MessageBox.Show("Open a controlled document from AeroLink Documentation Center.", "AeroLink connector"); return; }
        try { Application.Run(ConnectorForm.CreateAsync(uri).GetAwaiter().GetResult()); } catch (Exception ex) { MessageBox.Show(ex.Message, "AeroLink connector", MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }

    private static void InstallProtocol()
    {
        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("The connector executable path is unavailable.");
        using var root = Registry.CurrentUser.CreateSubKey(@"Software\Classes\aerolink"); root.SetValue(null, "URL:AeroLink controlled document protocol"); root.SetValue("URL Protocol", "");
        using var icon = root.CreateSubKey("DefaultIcon"); icon.SetValue(null, $"\"{executable}\",0");
        using var command = root.CreateSubKey(@"shell\open\command"); command.SetValue(null, $"\"{executable}\" \"%1\"");
    }
}

internal sealed record Redemption(Guid Id, string AccessToken, string Mode, string DocumentNumber, string Title, DateTimeOffset ExpiresAt, long SessionVersion, Guid SourceAttachmentId, long SourceSize, string SourceSha256);
internal sealed record Heartbeat(long Version, DateTimeOffset ExpiresAt);

internal sealed class ConnectorForm : Form
{
    private readonly HttpClient _client; private readonly Redemption _grant; private readonly string _workingFile;
    private readonly Label _status = new() { AutoSize = true, ForeColor = Color.FromArgb(82, 98, 116) };
    private readonly TextBox _comment = new() { Multiline = true, Height = 78, PlaceholderText = "Describe what changed in this check-in…" };
    private readonly Button _primary = new() { Height = 40, BackColor = Color.FromArgb(22, 133, 120), ForeColor = Color.White, FlatStyle = FlatStyle.Flat };
    private readonly Button _discard = new() { Height = 36, Text = "Discard checkout", FlatStyle = FlatStyle.Flat };
    private readonly System.Windows.Forms.Timer _heartbeat = new() { Interval = 4 * 60 * 1000 };
    private long _sessionVersion; private bool _completed;

    private ConnectorForm(HttpClient client, Redemption grant, string workingFile)
    {
        _client = client; _grant = grant; _workingFile = workingFile; _sessionVersion = grant.SessionVersion;
        Text = grant.Mode == "release" ? "AeroLink — Prepare document release" : "AeroLink — Controlled Word checkout"; Width = 620; Height = 480; MinimumSize = new(540, 440); StartPosition = FormStartPosition.CenterScreen; BackColor = Color.White; Font = new("Segoe UI", 10);
        var title = new Label { AutoSize = true, MaximumSize = new(530, 0), Font = new("Segoe UI Semibold", 18), Text = grant.Title };
        var number = new Label { AutoSize = true, ForeColor = Color.FromArgb(46, 116, 181), Font = new("Segoe UI Semibold", 10), Text = grant.DocumentNumber };
        var guidance = new Label { AutoSize = true, MaximumSize = new(530, 0), Text = grant.Mode == "release" ? "Word is opening the exact reviewed Draft. Confirm it, then prepare a clean DOCX and PDF. AeroLink hashes both before final signature." : "Edit this exclusive working copy in Microsoft Word, save normally, then return here to check it in. The faint DRAFT watermark must remain." };
        _primary.Text = grant.Mode == "release" ? "Prepare DOCX + PDF release candidate" : "Check in saved Word document"; _status.Text = $"Checkout active until {grant.ExpiresAt.LocalDateTime:g}";
        var file = new LinkLabel { AutoSize = true, Text = workingFile }; file.LinkClicked += (_,_) => OpenWord();
        var panel = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(26), AutoScroll = true };
        panel.Controls.Add(number); panel.Controls.Add(title); panel.Controls.Add(guidance); panel.Controls.Add(new Label { AutoSize = true, Text = "Working copy", Font = new("Segoe UI Semibold", 9), Margin = new Padding(0, 18, 0, 3) }); panel.Controls.Add(file);
        if (grant.Mode == "edit") { panel.Controls.Add(new Label { AutoSize = true, Text = "Required check-in comment", Font = new("Segoe UI Semibold", 9), Margin = new Padding(0, 18, 0, 3) }); _comment.Width = 530; panel.Controls.Add(_comment); }
        _primary.Width = 530; _primary.Margin = new Padding(0, 18, 0, 5); _discard.Width = 530; panel.Controls.Add(_primary); panel.Controls.Add(_discard); _status.Margin = new Padding(0, 10, 0, 0); panel.Controls.Add(_status); Controls.Add(panel);
        _primary.Click += async (_,_) => await CompleteAsync(); _discard.Click += async (_,_) => await DiscardAsync(); _heartbeat.Tick += async (_,_) => await HeartbeatAsync(); FormClosing += OnClosing; _heartbeat.Start(); Shown += (_,_) => OpenWord();
    }

    public static async Task<ConnectorForm> CreateAsync(Uri launch)
    {
        var query = ParseQuery(launch.Query); if (!query.TryGetValue("server", out var server) || !query.TryGetValue("ticket", out var ticket)) throw new InvalidOperationException("The AeroLink handoff is incomplete. Return to Documentation Center and try again.");
        var origin = new Uri(server); if (origin.Scheme != Uri.UriSchemeHttps && !origin.IsLoopback) throw new InvalidOperationException("The connector requires HTTPS except when AeroLink is running on this computer.");
        var client = new HttpClient { BaseAddress = origin, Timeout = TimeSpan.FromMinutes(3) }; using var response = await client.PostAsync($"/api/document-connector/redeem/{Uri.EscapeDataString(ticket)}", null); await EnsureAsync(response);
        var grant = await response.Content.ReadFromJsonAsync<Redemption>(JsonOptions) ?? throw new InvalidOperationException("AeroLink returned an incomplete connector grant."); client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", grant.AccessToken);
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AeroLink", "DocumentConnector", "working"); Directory.CreateDirectory(root); var path = Path.Combine(root, SafeFileName(grant.DocumentNumber) + ".docx");
        var temporaryPath = path + $".{Guid.NewGuid():N}.download";
        try
        {
            await using (var output = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                using var download = await client.GetAsync($"/api/document-connector/{grant.Id}/download", HttpCompletionOption.ResponseHeadersRead); await EnsureAsync(download);
                await download.Content.CopyToAsync(output);
            }
            var info = new FileInfo(temporaryPath);
            if (info.Length != grant.SourceSize) throw new InvalidOperationException("The downloaded controlled document size does not match the connector grant. Word was not opened.");
            await using (var input = new FileStream(temporaryPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                var actualHash = Convert.ToHexString(await SHA256.HashDataAsync(input)).ToLowerInvariant();
                if (!CryptographicOperations.FixedTimeEquals(Convert.FromHexString(actualHash), Convert.FromHexString(grant.SourceSha256)))
                    throw new InvalidOperationException("The downloaded controlled document hash does not match the connector grant. Word was not opened.");
            }
            File.Move(temporaryPath, path, true);
        }
        finally { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); }
        return new ConnectorForm(client, grant, path);
    }

    private void OpenWord() { try { Process.Start(new ProcessStartInfo(_workingFile) { UseShellExecute = true }); _status.Text = "Microsoft Word opened the controlled working copy."; } catch { _status.Text = "Open the working copy from the link above in Microsoft Word."; } }
    private async Task CompleteAsync()
    {
        if (_grant.Mode == "edit" && string.IsNullOrWhiteSpace(_comment.Text)) { MessageBox.Show("Enter a check-in comment describing what changed.", "AeroLink connector"); return; }
        Toggle(false); try { if (_grant.Mode == "release") await UploadReleaseAsync(); else await UploadDraftAsync(); _completed = true; _heartbeat.Stop(); MessageBox.Show(_grant.Mode == "release" ? "The exact DOCX and PDF candidate is ready for final AeroLink signature." : "The Word document was checked in. Its prior working version remains in History.", "AeroLink connector", MessageBoxButtons.OK, MessageBoxIcon.Information); Close(); } catch (Exception ex) { _status.Text = ex.Message; Toggle(true); }
    }
    private async Task UploadDraftAsync()
    { using var form = new MultipartFormDataContent(); form.Add(new StringContent(_comment.Text.Trim()), "comment"); form.Add(new StringContent(_sessionVersion.ToString()), "expectedVersion"); var file = new StreamContent(File.OpenRead(_workingFile)); file.Headers.ContentType = new("application/vnd.openxmlformats-officedocument.wordprocessingml.document"); form.Add(file, "file", Path.GetFileName(_workingFile)); using var response = await _client.PostAsync($"/api/document-connector/{_grant.Id}/check-in", form); await EnsureAsync(response); }
    private async Task UploadReleaseAsync()
    {
        var outputRoot = Path.Combine(Path.GetDirectoryName(_workingFile)!, "release"); Directory.CreateDirectory(outputRoot); var docx = Path.Combine(outputRoot, SafeFileName(_grant.DocumentNumber) + "-RELEASE-CANDIDATE.docx"); var pdf = Path.Combine(outputRoot, SafeFileName(_grant.DocumentNumber) + "-RELEASE-CANDIDATE.pdf"); WordReleaseRenderer.Create(_workingFile, docx, pdf);
        using var form = new MultipartFormDataContent(); form.Add(new StringContent(_sessionVersion.ToString()), "expectedVersion"); var docxFile = new StreamContent(File.OpenRead(docx)); docxFile.Headers.ContentType = new("application/vnd.openxmlformats-officedocument.wordprocessingml.document"); form.Add(docxFile, "docx", Path.GetFileName(docx)); var pdfFile = new StreamContent(File.OpenRead(pdf)); pdfFile.Headers.ContentType = new("application/pdf"); form.Add(pdfFile, "pdf", Path.GetFileName(pdf)); using var response = await _client.PostAsync($"/api/document-connector/{_grant.Id}/release-candidate", form); await EnsureAsync(response);
    }
    private async Task HeartbeatAsync()
    { try { using var response = await _client.PostAsJsonAsync($"/api/document-connector/{_grant.Id}/heartbeat", new { expectedVersion = _sessionVersion }); await EnsureAsync(response); var heartbeat = await response.Content.ReadFromJsonAsync<Heartbeat>(JsonOptions); if (heartbeat is not null) { _sessionVersion = heartbeat.Version; _status.Text = $"Checkout active until {heartbeat.ExpiresAt.LocalDateTime:g}"; } } catch (Exception ex) { _heartbeat.Stop(); _status.Text = "Checkout heartbeat stopped: " + ex.Message; } }
    private async Task DiscardAsync()
    { if (MessageBox.Show("Discard this checkout? Saved local changes will not be uploaded.", "AeroLink connector", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return; Toggle(false); try { using var response = await _client.PostAsJsonAsync($"/api/document-connector/{_grant.Id}/discard", new { expectedVersion = _sessionVersion, reason = "User discarded the desktop checkout." }); await EnsureAsync(response); _completed = true; _heartbeat.Stop(); Close(); } catch (Exception ex) { _status.Text = ex.Message; Toggle(true); } }
    private void OnClosing(object? sender, FormClosingEventArgs e) { if (!_completed && MessageBox.Show("Close and leave the checkout active until its lease expires?", "AeroLink connector", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) e.Cancel = true; }
    private void Toggle(bool enabled) { _primary.Enabled = enabled; _discard.Enabled = enabled; _comment.Enabled = enabled; }
    private static Dictionary<string,string> ParseQuery(string value) => value.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries).Select(part => part.Split('=', 2)).Where(pair => pair.Length == 2).ToDictionary(pair => Uri.UnescapeDataString(pair[0]), pair => Uri.UnescapeDataString(pair[1]), StringComparer.OrdinalIgnoreCase);
    private static string SafeFileName(string value) => string.Concat(value.Select(character => Path.GetInvalidFileNameChars().Contains(character) ? '_' : character));
    private static async Task EnsureAsync(HttpResponseMessage response) { if (response.IsSuccessStatusCode) return; var message = "AeroLink did not complete the connector operation."; try { using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync()); if (json.RootElement.TryGetProperty("error", out var error)) message = error.GetString() ?? message; } catch { } throw new InvalidOperationException(message); }
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
}

internal static class WordReleaseRenderer
{
    public static void Create(string source, string docx, string pdf)
    {
        var wordType = Type.GetTypeFromProgID("Word.Application") ?? throw new InvalidOperationException("Microsoft Word desktop is required to prepare the approved PDF rendition."); dynamic? word = null, document = null;
        try { word = Activator.CreateInstance(wordType) ?? throw new InvalidOperationException("Microsoft Word could not be started."); word.Visible = false; word.DisplayAlerts = 0; document = word.Documents.Open(source, ReadOnly: false); foreach (dynamic section in document.Sections) foreach (dynamic header in section.Headers) { for (var index = header.Shapes.Count; index >= 1; index--) { dynamic shape = header.Shapes[index]; var name = (string)shape.Name; var text = ""; try { text = (string)shape.TextEffect.Text; } catch { } if (string.Equals(name, "AeroLinkWatermark", StringComparison.OrdinalIgnoreCase) || text.Contains("DRAFT", StringComparison.OrdinalIgnoreCase)) shape.Delete(); Marshal.FinalReleaseComObject(shape); } Marshal.FinalReleaseComObject(header); } RelabelDraftStories(document); document.SaveAs2(docx, 16); document.ExportAsFixedFormat(pdf, 17, OpenAfterExport: false, OptimizeFor: 0, Range: 0, Item: 0, IncludeDocProps: true, KeepIRM: true, CreateBookmarks: 1, DocStructureTags: true, BitmapMissingFonts: true, UseISO19005_1: false); }
        catch (COMException ex) { throw new InvalidOperationException("Microsoft Word could not prepare the release rendition. Close any prompt in Word, save the working copy, and try again.", ex); }
        finally { if (document is not null) { try { document.Close(false); } catch { } Marshal.FinalReleaseComObject(document); } if (word is not null) { try { word.Quit(); } catch { } Marshal.FinalReleaseComObject(word); } }
    }

    private static void RelabelDraftStories(dynamic document)
    {
        foreach (dynamic firstRange in document.StoryRanges)
        {
            dynamic? range = firstRange;
            while (range is not null)
            {
                dynamic find = range.Find;
                find.ClearFormatting();
                find.Replacement.ClearFormatting();
                find.Execute(FindText: "DRAFT", MatchCase: true, MatchWholeWord: true, ReplaceWith: "RELEASE CANDIDATE", Replace: 2);
                find.Execute(FindText: "Draft", MatchCase: true, MatchWholeWord: true, ReplaceWith: "Release Candidate", Replace: 2);
                dynamic? next = range.NextStoryRange;
                Marshal.FinalReleaseComObject(find);
                Marshal.FinalReleaseComObject(range);
                range = next;
            }
        }
    }
}
