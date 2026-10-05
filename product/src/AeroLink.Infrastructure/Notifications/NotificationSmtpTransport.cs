using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using AeroLink.Domain.Notifications;
using MailKit;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;

namespace AeroLink.Infrastructure.Notifications;

public sealed record NotificationTransportResult(NotificationAttemptOutcome Outcome, string Phase, int? Status,
    string SafeCode, bool TransportDisposed, bool CleanupWarning = false);

/// <summary>One protected generation, one recipient and one physical connection. No raw protocol logging.</summary>
public sealed class NotificationSmtpTransport
{
    public async Task<NotificationTransportResult> SendAsync(ResolvedNotificationSettings settings,
        MimeMessage message, CancellationToken ct, Func<NotificationTransportResult, Task>? persistAcceptance = null)
    {
        if (!settings.CanSend) return new(NotificationAttemptOutcome.ConfigBlocked, "Prepared", null, "SettingsBlocked", true);
        if (message.To.Count != 1 || message.Cc.Count != 0 || message.Bcc.Count != 0 || message.Attachments.Any())
            return new(NotificationAttemptOutcome.ConfigBlocked, "Prepared", null, "MessagePolicyBlocked", true);
        var accepted = false; var submitted = false; var phase = "Connect"; var cleanupWarning = false;
        NotificationTransportResult? result = null;
        using var tcp = new TcpClient();
        using var observer = new SubmissionDeadlineObserver();
        using var client = new InstallationSmtpClient(settings.Policy?.TrustAnchorsPem, observer);
        Task acceptancePersistence = Task.CompletedTask;
        NotificationSmtpDeadlineStream? deadlines = null;
        // This supported event is raised on the final accepted DATA/BDAT response, before QUIT.
        client.MessageSent += (_, _) =>
        {
            accepted = true;
            // Latch and begin durable acceptance before any QUIT, cancellation or socket cleanup.
            acceptancePersistence = persistAcceptance?.Invoke(new(AcceptedOutcome(settings.Mode), "Accepted", 250, "", false))
                ?? Task.CompletedTask;
        };
        try
        {
            using (var connect = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                connect.CancelAfter(TimeSpan.FromSeconds(30));
                await tcp.ConnectAsync(settings.Host, settings.Port, connect.Token);
            }
            deadlines = new NotificationSmtpDeadlineStream(tcp.GetStream());
            observer.Stream = deadlines;
            // Connect includes greeting/EHLO/STARTTLS. MailKit does not expose separate trustworthy command
            // callbacks, so the common setup allowance is five minutes; authentication has its own bound.
            client.Timeout = 300_000;
            using (var setup = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                setup.CancelAfter(TimeSpan.FromMinutes(5)); phase = "GreetingTls";
                await client.ConnectAsync(deadlines, settings.Host, settings.Port,
                    settings.Mode == NotificationMode.Capture ? SecureSocketOptions.None : SecureSocketOptions.StartTls, setup.Token);
            }
            if (settings.UserName.Length > 0)
            {
                phase = "Authentication";
                using var auth = CancellationTokenSource.CreateLinkedTokenSource(ct); auth.CancelAfter(TimeSpan.FromMinutes(2));
                await client.AuthenticateAsync(settings.UserName, settings.Credential, auth.Token);
            }
            // A per-read deadline permits a ten-minute final DATA reply. There is no short whole-send cap.
            // The wrapper enforces async cancellation, because NetworkStream.ReadTimeout alone does not.
            client.Timeout = 600_000;
            client.Capabilities &= ~(SmtpCapabilities.Chunking | SmtpCapabilities.BinaryMime | SmtpCapabilities.EightBitMime);
            phase = "Submission"; submitted = true;
            await client.SendAsync(message, ct);
            accepted = true;
            phase = "Accepted";
        }
        catch (Exception ex)
        {
            if (accepted) result = new(AcceptedOutcome(settings.Mode), "Accepted", 250, "", true, true);
            else if (ex is SmtpCommandException refusal && (int)refusal.StatusCode is >= 400 and <= 599)
            {
                var status = (int)refusal.StatusCode;
                result = new(status < 500 ? NotificationAttemptOutcome.TransientRefused : NotificationAttemptOutcome.PermanentRefused,
                    observer.Phase ?? phase, status, status < 500 ? "RelayTransientRefusal" : "RelayPermanentRefusal", true);
            }
            else if (ex is SslHandshakeException or MailKit.Security.AuthenticationException or System.Security.Authentication.AuthenticationException or NotSupportedException)
                result = new(NotificationAttemptOutcome.ConfigBlocked, phase, null,
                    phase == "Authentication" ? "AuthenticationBlocked" : "TlsTrustBlocked", true);
            else result = new(submitted ? NotificationAttemptOutcome.AcceptanceUnknown : NotificationAttemptOutcome.TransientRefused,
                observer.Phase ?? phase, null, submitted ? "AcceptanceUnproven" : "ConnectionUnavailable", true);
            // Exception message/inner exception/raw SMTP response are deliberately never retained.
        }
        finally
        {
            if (accepted) await acceptancePersistence;
            if (client.IsConnected)
            {
                try
                {
                    using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                    await client.DisconnectAsync(accepted, cleanup.Token);
                    if (accepted && observer.QuitStarted && !observer.QuitAcknowledged) cleanupWarning = true;
                }
                catch { cleanupWarning = true; }
            }
            client.Dispose(); deadlines?.Dispose(); tcp.Dispose();
        }
        return accepted ? new(AcceptedOutcome(settings.Mode), "Accepted", 250, "", true, cleanupWarning || result?.CleanupWarning == true)
            : result ?? new(NotificationAttemptOutcome.AcceptanceUnknown, phase, null, "AcceptanceUnproven", true);
    }
    private static NotificationAttemptOutcome AcceptedOutcome(NotificationMode mode) => mode switch
    {
        NotificationMode.Capture => NotificationAttemptOutcome.Captured,
        NotificationMode.ControlledTest => NotificationAttemptOutcome.TestAccepted,
        _ => NotificationAttemptOutcome.SmtpAccepted,
    };
    /// <summary>Observe the library's supported protocol boundary only to bound waits; retain no bytes.
    /// MailKit owns the SMTP state machine, parsing, command ordering and acceptance.</summary>
    private sealed class SubmissionDeadlineObserver : IProtocolLogger
    {
        public IAuthenticationSecretDetector? AuthenticationSecretDetector { get; set; }
        public NotificationSmtpDeadlineStream? Stream { get; set; }
        public string? Phase { get; private set; }
        public bool QuitStarted { get; private set; }
        public bool QuitAcknowledged { get; private set; }
        private bool dataStarted;
        private int replyDigits, replyCode;
        public void LogConnect(Uri uri) { }
        public void LogClient(byte[] buffer, int offset, int count)
        {
            var bytes = buffer.AsSpan(offset, count);
            if (bytes.StartsWith("QUIT\r\n"u8)) { QuitStarted = true; return; }
            if (dataStarted || Stream is null) return;
            if (bytes.StartsWith("MAIL FROM:"u8)) { Phase = "MailFrom"; Stream.ReadTimeout = 300_000; }
            else if (bytes.StartsWith("RCPT TO:"u8)) { Phase = "Recipient"; Stream.ReadTimeout = 300_000; }
            else if (bytes.StartsWith("DATA\r\n"u8)) { Phase = "DataCommand"; Stream.ReadTimeout = 120_000; dataStarted = true; }
        }
        public void LogServer(byte[] buffer, int offset, int count)
        {
            // Keep only the numeric status prefix across split reads, never response text or recipients.
            for (var index = offset; index < offset + count; index++)
            {
                var value = buffer[index];
                if (value == 10) { replyDigits = 0; replyCode = 0; }
                else if (replyDigits < 3)
                {
                    if (value is >= 48 and <= 57) { replyCode = replyCode * 10 + value - 48; replyDigits++; }
                    else replyDigits = 3;
                    if (replyDigits == 3 && replyCode == 354 && dataStarted && Stream is not null)
                    { Phase = "DataReply"; Stream.ReadTimeout = 600_000; }
                    if (replyDigits == 3 && replyCode == 221 && QuitStarted) QuitAcknowledged = true;
                }
            }
        }
        public void Dispose() { }
    }

    // SslStream retains its target host, default rejection callback and server-authentication
    // checks. An installation-owned root policy replaces only the chain trust store.
    private sealed class InstallationSmtpClient(string[]? roots, IProtocolLogger observer) : SmtpClient(observer)
    {
        protected override SslClientAuthenticationOptions GetSslClientAuthenticationOptions(string host,
            RemoteCertificateValidationCallback remoteCertificateValidationCallback)
        {
            var options = base.GetSslClientAuthenticationOptions(host, remoteCertificateValidationCallback);
            if (roots is { Length: > 0 })
            {
                var policy = new X509ChainPolicy
                {
                    TrustMode = X509ChainTrustMode.CustomRootTrust,
                    RevocationMode = X509RevocationMode.Online,
                    RevocationFlag = X509RevocationFlag.ExcludeRoot,
                    VerificationFlags = X509VerificationFlags.NoFlag,
                    UrlRetrievalTimeout = TimeSpan.FromSeconds(10),
                };
                policy.ApplicationPolicy.Add(new Oid("1.3.6.1.5.5.7.3.1"));
                foreach (var pem in roots) policy.CustomTrustStore.Add(X509Certificate2.CreateFromPem(pem));
                options.CertificateChainPolicy = policy;
            }
            return options;
        }
    }
}

/// <summary>Supported Stream boundary: deadlines apply to actual asynchronous network reads/writes.</summary>
internal sealed class NotificationSmtpDeadlineStream(Stream inner) : Stream
{
    private int readTimeout = 600_000, writeTimeout = 180_000;
    public override int ReadTimeout { get => readTimeout; set => readTimeout = Math.Clamp(value, 1, 600_000); }
    public override int WriteTimeout { get => writeTimeout; set => writeTimeout = Math.Clamp(value, 1, 180_000); }
    public override bool CanRead => inner.CanRead;
    public override bool CanWrite => inner.CanWrite;
    public override bool CanSeek => false;
    public override bool CanTimeout => true;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
    public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
    public override void Flush() => inner.Flush();
    public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);
    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); timeout.CancelAfter(ReadTimeout);
        return await inner.ReadAsync(buffer.AsMemory(offset, count), timeout.Token);
    }
    public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); timeout.CancelAfter(WriteTimeout);
        await inner.WriteAsync(buffer.AsMemory(offset, count), timeout.Token);
    }
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); timeout.CancelAfter(ReadTimeout);
        return await inner.ReadAsync(buffer, timeout.Token);
    }
    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); timeout.CancelAfter(WriteTimeout);
        await inner.WriteAsync(buffer, timeout.Token);
    }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
}
