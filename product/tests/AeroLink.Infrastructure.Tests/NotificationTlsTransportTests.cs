using System.Diagnostics;
using System.Text.Json;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using AeroLink.Domain.Notifications;
using AeroLink.Infrastructure.Notifications;
using MimeKit;

namespace AeroLink.Infrastructure.Tests;

// Primary transport owner: real STARTTLS, normal hostname/chain/EKU validation and actual relay replies.
// Fake IEmailSender fixtures cannot prove DATA acceptance versus QUIT failure or a lost final response.
// This installation-owned self-signed CA/server anchor avoids touching any machine trust store. It is
// not corporate trust; the signed-leaf cases below separately exercise actual Online CRL retrieval.
public sealed class NotificationTlsTransportTests
{
    [Fact]
    public async Task Trusted_tls_acceptance_is_latched_before_quit_and_wire_identity_is_stable()
    {
        using var certificate = Certificate("localhost"); var message = Message();
        using var chain = new X509Chain(); chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.RevocationMode = X509RevocationMode.Online; chain.ChainPolicy.RevocationFlag = X509RevocationFlag.ExcludeRoot;
        chain.ChainPolicy.CustomTrustStore.Add(certificate); chain.ChainPolicy.ApplicationPolicy.Add(new Oid("1.3.6.1.5.5.7.3.1"));
        Assert.True(chain.Build(certificate), string.Join(",", chain.ChainStatus.Select(x => x.Status.ToString())));
        string? firstWire = null, firstEnvelope = null;
        for (var index = 0; index < 2; index++)
        {
            await using var relay = new TlsRelay(certificate, Reply.AcceptThenDropQuit);
            var persisted = false; var quitObserved = false;
            relay.OnQuit = () => { Assert.True(persisted); quitObserved = true; }; // A real QUIT cannot precede the durable receipt callback.
            var result = await new NotificationSmtpTransport().SendAsync(Settings(relay.Port, certificate), message, default,
                receipt => { Assert.False(receipt.TransportDisposed); persisted = true; return Task.CompletedTask; });
            await relay.Completion;
            Assert.True(quitObserved); Assert.True(result.CleanupWarning);
            Assert.True(persisted, $"{result.Outcome} {result.Phase} {result.SafeCode}; TLS={relay.TlsStarted}, DATA={relay.DataCount}, envelope={relay.Envelope}"); Assert.Equal(NotificationAttemptOutcome.TestAccepted, result.Outcome);
            Assert.True(result.TransportDisposed); Assert.Equal(1, relay.DataCount);
            if (index == 0) { firstWire = relay.Wire; firstEnvelope = relay.Envelope; }
            else { Assert.Equal(firstWire, relay.Wire); Assert.Equal(firstEnvelope, relay.Envelope); }
        }
    }

    [Theory]
    [InlineData(Reply.LoseFinalReply, NotificationAttemptOutcome.AcceptanceUnknown, 1)]
    [InlineData(Reply.Refuse451, NotificationAttemptOutcome.TransientRefused, 0)]
    [InlineData(Reply.Refuse550, NotificationAttemptOutcome.PermanentRefused, 0)]
    public async Task Actual_relay_reply_controls_the_submission_outcome(Reply reply, NotificationAttemptOutcome expected, int data)
    {
        using var certificate = Certificate("localhost");
        await using var relay = new TlsRelay(certificate, reply);
        var result = await new NotificationSmtpTransport().SendAsync(Settings(relay.Port, certificate), Message(), default);
        await relay.Completion;
        Assert.Equal(expected, result.Outcome); Assert.Equal(data, relay.DataCount); Assert.True(relay.TlsStarted);
    }

    [Theory]
    [InlineData("wrong-host")]
    [InlineData("expired")]
    [InlineData("future")]
    [InlineData("wrong-eku")]
    [InlineData("untrusted")]
    public async Task Tls_negative_controls_reach_tls_and_never_submit_data(string kind)
    {
        // An independent trusted positive control above is mandatory; a suite of refusals alone is vacuous.
        using var certificate = Certificate(kind == "wrong-host" ? "other.example.test" : "localhost",
            expired: kind == "expired", future: kind == "future", clientOnly: kind == "wrong-eku");
        using var unrelated = Certificate("localhost");
        await using var relay = new TlsRelay(certificate, Reply.AcceptThenDropQuit);
        var result = await new NotificationSmtpTransport().SendAsync(Settings(relay.Port, kind == "untrusted" ? unrelated : certificate), Message(), default);
        await relay.Completion;
        Assert.Equal(NotificationAttemptOutcome.ConfigBlocked, result.Outcome); Assert.True(relay.TlsStarted);
        Assert.Equal(0, relay.DataCount); Assert.Equal("", relay.Envelope);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Signed_server_leaf_uses_actual_online_loopback_crl_before_submission(bool revoked)
    {
        // A unique issuer and URL prevent a cached negative from masquerading as a positive control.
        // Normal OS retrieval caching is permitted; no user/machine trust store is changed.
        await using var crl = new CrlServer();
        using var issuerKey = RSA.Create(2048);
        var issuerRequest = new CertificateRequest("CN=Owned issuer " + Guid.NewGuid().ToString("N"), issuerKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        issuerRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        issuerRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        issuerRequest.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(issuerRequest.PublicKey, false));
        var now = DateTimeOffset.UtcNow; using var issuer = issuerRequest.CreateSelfSigned(now.AddDays(-2), now.AddDays(2));
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder(); san.AddDnsName("localhost"); request.CertificateExtensions.Add(san.Build());
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.1") }, true));
        request.CertificateExtensions.Add(CertificateRevocationListBuilder.BuildCrlDistributionPointExtension([crl.Url]));
        using var publicLeaf = request.Create(issuer, now.AddDays(-1), now.AddDays(1), RandomNumberGenerator.GetBytes(16));
        using var leaf = publicLeaf.CopyWithPrivateKey(key);
        var list = new CertificateRevocationListBuilder(); if (revoked) list.AddEntry(leaf, now.AddMinutes(-1), X509RevocationReason.KeyCompromise);
        crl.Content = list.Build(issuer, 1, now.AddHours(1), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1, now.AddMinutes(-2));
        await using var relay = new TlsRelay(leaf, Reply.AcceptThenDropQuit);
        var result = await new NotificationSmtpTransport().SendAsync(Settings(relay.Port, issuer), Message(), default);
        await relay.Completion;
        Assert.True(crl.Requests > 0); Assert.True(relay.TlsStarted);
        Assert.Equal(revoked ? NotificationAttemptOutcome.ConfigBlocked : NotificationAttemptOutcome.TestAccepted, result.Outcome);
        Assert.Equal(revoked ? 0 : 1, relay.DataCount);
    }

    private sealed class CrlServer : IAsyncDisposable
    {
        private readonly TcpListener listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource stop = new();
        private readonly Task loop;
        internal byte[] Content = [];
        internal int Requests;
        internal string Url { get; }
        internal CrlServer()
        { listener.Start(); Url = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/{Guid.NewGuid():N}.crl"; loop = RunAsync(); }
        private async Task RunAsync()
        {
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    using var socket = await listener.AcceptTcpClientAsync(stop.Token); using var stream = socket.GetStream();
                    var buffer = new byte[8192]; var length = 0;
                    while (length < buffer.Length)
                    {
                        var count = await stream.ReadAsync(buffer.AsMemory(length), stop.Token); if (count == 0) break; length += count;
                        if (Encoding.ASCII.GetString(buffer, 0, length).Contains("\r\n\r\n", StringComparison.Ordinal)) break;
                    }
                    Assert.StartsWith("GET " + new Uri(Url).AbsolutePath + " ", Encoding.ASCII.GetString(buffer, 0, length));
                    Interlocked.Increment(ref Requests);
                    await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: application/pkix-crl\r\nContent-Length: {Content.Length}\r\nConnection: close\r\n\r\n"), stop.Token);
                    await stream.WriteAsync(Content, stop.Token);
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        }
        public async ValueTask DisposeAsync() { stop.Cancel(); listener.Stop(); await loop; stop.Dispose(); }
    }

    private static ResolvedNotificationSettings Settings(int port, X509Certificate2 anchor)
    {
        var policy = new NotificationInstallationPolicy(Guid.NewGuid().ToString("D"), Environment.MachineName, Guid.NewGuid(), Guid.NewGuid(),
            NotificationMode.ControlledTest, ["localhost"], ["sender@example.test"], [], [], "diagnostic@example.test", "https://fixture.invalid",
            TrustAnchorsPem: [anchor.ExportCertificatePem()]);
        return new(policy.InstallationId, 1, Guid.NewGuid(), NotificationMode.ControlledTest, "localhost", port, "sender@example.test", "AeroLink",
            policy.BaseUrl, "", "", policy, "fixture-policy", "", true, true, true, true) { AdmissionEnabled = true };
    }
    private static MimeMessage Message()
    {
        var result = new MimeMessage { MessageId = "immutable-fixture@notifications.aerolink.invalid", Date = new DateTimeOffset(2026, 10, 4, 12, 0, 0, TimeSpan.Zero), Subject = "Synthetic diagnostic" };
        result.From.Add(MailboxAddress.Parse("sender@example.test")); result.To.Add(MailboxAddress.Parse("diagnostic@example.test"));
        result.Body = new BodyBuilder { TextBody = "Synthetic diagnostic only.", HtmlBody = "<p>Synthetic diagnostic only.</p>" }.ToMessageBody();
        result.Prepare(EncodingConstraint.SevenBit); return result;
    }
    private static X509Certificate2 Certificate(string host, bool expired = false, bool future = false, bool clientOnly = false)
    {
        using var key = RSA.Create(2048); var request = new CertificateRequest("CN=" + host, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder(); san.AddDnsName(host); request.CertificateExtensions.Add(san.Build());
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new(clientOnly ? "1.3.6.1.5.5.7.3.2" : "1.3.6.1.5.5.7.3.1") }, true));
        var now = DateTimeOffset.UtcNow;
        return request.CreateSelfSigned(future ? now.AddDays(1) : now.AddDays(-2), expired ? now.AddDays(-1) : now.AddDays(2));
    }

    public enum Reply { AcceptThenDropQuit, LoseFinalReply, Refuse451, Refuse550 }
    private sealed class TlsRelay : IAsyncDisposable
    {
        private readonly string directory = Path.Combine(Path.GetTempPath(), "aerolink-owned-tls-" + Guid.NewGuid().ToString("N"));
        private readonly Process process;
        internal readonly Task Completion;
        internal int Port { get; }
        internal bool TlsStarted;
        internal int DataCount;
        internal string Wire = "", Envelope = "";
        internal Action? OnQuit;
        internal TlsRelay(X509Certificate2 certificate, Reply reply)
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "certificate.pem"), certificate.ExportCertificatePem());
            using var key = certificate.GetRSAPrivateKey()!;
            File.WriteAllText(Path.Combine(directory, "key.pem"), key.ExportPkcs8PrivateKeyPem());
            File.Copy(Path.Combine(AppContext.BaseDirectory, "TestSupport", "NotificationTlsRelay.py"), Path.Combine(directory, "relay.py"));
            // Test-only process support uses an explicit interpreter or the normal CI Python command.
            // Absence fails the positive fixture; it never silently skips qualification.
            var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("AEROLINK_TEST_PYTHON") ?? (OperatingSystem.IsWindows() ? "python" : "python3"))
            { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            start.ArgumentList.Add(Path.Combine(directory, "relay.py")); start.ArgumentList.Add(directory); start.ArgumentList.Add(reply.ToString());
            process = Process.Start(start) ?? throw new InvalidOperationException("Owned TLS relay could not start.");
            var first = process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
            if (!int.TryParse(first, out var port)) throw new InvalidOperationException("Owned TLS relay did not publish its port.");
            Port = port; Completion = ReadCompletionAsync();
        }
        private async Task ReadCompletionAsync()
        {
            while (await process.StandardOutput.ReadLineAsync() is string line)
            {
                if (line == "QUIT") { OnQuit?.Invoke(); continue; }
                using var value = JsonDocument.Parse(line); var root = value.RootElement;
                TlsStarted = root.GetProperty("tls").GetBoolean(); DataCount = root.GetProperty("data").GetInt32();
                Wire = root.GetProperty("wire").GetString()!; Envelope = root.GetProperty("envelope").GetString()!;
            }
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(35));
            Assert.Equal(0, process.ExitCode);
        }
        public async ValueTask DisposeAsync()
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            try { await Completion; } finally { process.Dispose(); Directory.Delete(directory, recursive: true); }
        }

    }
}
