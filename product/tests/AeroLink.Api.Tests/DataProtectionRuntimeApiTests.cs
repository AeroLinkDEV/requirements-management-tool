using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Net.Security;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;

namespace AeroLink.Api.Tests;

/// <summary>
/// Primary owner for the loopback-only HOME Data Protection runtime capture.
///
/// This is an actual Kestrel process because TestServer has no remote socket address. The fixture ring is
/// synthetic and disposable; the test never reads HOME state, a certificate store, a service, or 54329.
/// </summary>
public sealed class DataProtectionRuntimeApiTests
{
    [Fact]
    public async Task Capture_reports_effective_ring_and_refuses_non_loopback_without_mutating_the_ring()
    {
        using var fixture = SyntheticApiFixture.Create();
        var before = SnapshotFiles(fixture.KeyRing);
        using var api = new OwnedApiProcess(fixture);
        await api.WaitForReadyAsync();

        using var loopback = NewClient();
        using var first = await loopback.GetAsync(api.LoopbackUri("/health/identity/data-protection"));
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        using var firstJson = JsonDocument.Parse(await first.Content.ReadAsStringAsync());
        Assert.Equal("captured", firstJson.RootElement.GetProperty("status").GetString());
        var firstDataProtection = firstJson.RootElement.GetProperty("dataProtection");
        Assert.Equal(fixture.ApplicationName, firstDataProtection.GetProperty("applicationDiscriminator").GetString());
        Assert.Equal(Path.GetFullPath(fixture.KeyRing),
            firstDataProtection.GetProperty("repositoryPath").GetString(), StringComparer.OrdinalIgnoreCase);
        Assert.Equal("actual XmlRepository.Directory", firstDataProtection.GetProperty("repositoryPathSource").GetString());
        Assert.True(firstDataProtection.GetProperty("keyCount").GetInt32() > 0);
        Assert.Equal(0, firstDataProtection.GetProperty("revokedKeyCount").GetInt32());
        Assert.Matches("^[0-9A-F]{64}$", firstDataProtection.GetProperty("keyMetadataSha256").GetString());
        var firstText = await first.Content.ReadAsStringAsync();
        Assert.DoesNotContain("synthetic-canary", firstText);
        Assert.DoesNotContain("encryptedSecret", firstText);
        var pid = firstJson.RootElement.GetProperty("process").GetProperty("pid").GetInt32();
        Assert.Equal(api.ProcessId, pid);
        Assert.True(firstJson.RootElement.GetProperty("process").GetProperty("startedAtUtc").GetDateTimeOffset() > DateTimeOffset.MinValue);

        // The raw socket is still loopback; a forged forwarding header must not turn it into a remote request.
        using var forged = new HttpRequestMessage(HttpMethod.Get, api.LoopbackUri("/health/identity/data-protection"));
        forged.Headers.TryAddWithoutValidation("X-Forwarded-For", "203.0.113.77");
        using var forgedResponse = await loopback.SendAsync(forged);
        Assert.Equal(HttpStatusCode.OK, forgedResponse.StatusCode);
        using var forgedJson = JsonDocument.Parse(await forgedResponse.Content.ReadAsStringAsync());
        Assert.Equal(pid, forgedJson.RootElement.GetProperty("process").GetProperty("pid").GetInt32());

        var nonLoopbackAddress = FindNonLoopbackIpv4();
        using var remote = NewClient();
        using var refused = await api.GetNonLoopbackAsync(remote, nonLoopbackAddress, "/health/identity/data-protection");
        Assert.Equal(HttpStatusCode.NotFound, refused.StatusCode);

        using var publicIdentity = await api.GetNonLoopbackAsync(remote, nonLoopbackAddress, "/health/identity");
        publicIdentity.EnsureSuccessStatusCode();
        using var publicIdentityJson = JsonDocument.Parse(await publicIdentity.Content.ReadAsStringAsync());
        Assert.Equal(JsonValueKind.Null, publicIdentityJson.RootElement.GetProperty("sourceSha").ValueKind);

        var second = SnapshotFiles(fixture.KeyRing);
        Assert.Equal(before.Count, second.Count);
        foreach (var item in before)
            Assert.True(second.TryGetValue(item.Key, out var hash) && hash == item.Value, item.Key);
        Assert.Equal("AeroLink HOME synthetic canary", fixture.UnprotectCanary());
    }

    private static HttpClient NewClient() => new(new SocketsHttpHandler
    {
        UseProxy = false,
        SslOptions = new SslClientAuthenticationOptions
        {
            RemoteCertificateValidationCallback = (_, _, _, _) => true,
        },
    })
    {
        Timeout = TimeSpan.FromSeconds(10),
    };

    private static IPAddress FindNonLoopbackIpv4()
    {
        var address = NetworkInterface.GetAllNetworkInterfaces()
            .Where(network => network.OperationalStatus == OperationalStatus.Up)
            .SelectMany(network => network.GetIPProperties().UnicastAddresses)
            .Select(unicast => unicast.Address)
            .FirstOrDefault(candidate => candidate.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
                && !IPAddress.IsLoopback(candidate));
        return address ?? throw new InvalidOperationException("The focused runtime capture test needs an owned non-loopback IPv4 address.");
    }

    private static Dictionary<string, string> SnapshotFiles(string directory) =>
        Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(path => Path.GetFileName(path)!, path => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))), StringComparer.OrdinalIgnoreCase);

    private sealed class SyntheticApiFixture : IDisposable
    {
        private readonly IDataProtectionProvider provider;
        private readonly IDataProtector protector;
        private readonly string protectedCanary;
        private readonly string root;

        private SyntheticApiFixture(string root, string keyRing, string database, string evidence,
            string authorityRoot, string applicationName, string publicHost, string certificatePath,
            string certificateKeyPath, IDataProtectionProvider provider,
            IDataProtector protector, string protectedCanary, int port)
        {
            this.root = root;
            KeyRing = keyRing;
            Database = database;
            Evidence = evidence;
            AuthorityRoot = authorityRoot;
            ApplicationName = applicationName;
            PublicHost = publicHost;
            CertificatePath = certificatePath;
            CertificateKeyPath = certificateKeyPath;
            this.provider = provider;
            this.protector = protector;
            this.protectedCanary = protectedCanary;
            Port = port;
        }

        public string KeyRing { get; }
        public string Database { get; }
        public string Evidence { get; }
        public string AuthorityRoot { get; }
        public string ApplicationName { get; }
        public string PublicHost { get; }
        public string CertificatePath { get; }
        public string CertificateKeyPath { get; }
        public int Port { get; }

        public static SyntheticApiFixture Create()
        {
            var root = Path.Combine(Path.GetTempPath(), "aerolink-home-dp-capture-" + Guid.NewGuid().ToString("N"));
            var keyRing = Path.Combine(root, "keys");
            var database = Path.Combine(root, "fixture.db");
            var evidence = Path.Combine(root, "evidence");
            var authorityRoot = Path.Combine(root, "authority");
            var publicHost = "aerolink-home-synthetic.example.test";
            Directory.CreateDirectory(root);
            Directory.CreateDirectory(keyRing);
            Directory.CreateDirectory(evidence);
            Directory.CreateDirectory(authorityRoot);
            var certificatePath = Path.Combine(root, "site.crt");
            var certificateKeyPath = Path.Combine(root, "site.key");
            using (var key = RSA.Create(2048))
            {
                var request = new CertificateRequest($"CN={publicHost}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                var san = new SubjectAlternativeNameBuilder();
                san.AddDnsName(publicHost);
                request.CertificateExtensions.Add(san.Build());
                using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
                File.WriteAllText(certificatePath, certificate.ExportCertificatePem());
                File.WriteAllText(certificateKeyPath, key.ExportPkcs8PrivateKeyPem());
            }
            var applicationName = "AeroLink.Home.Synthetic." + Guid.NewGuid().ToString("N");
            var provider = DataProtectionProvider.Create(new DirectoryInfo(keyRing), builder =>
                builder.SetApplicationName(applicationName));
            var protector = provider.CreateProtector("AeroLink.Home.Continuity.Synthetic.v1");
            var protectedCanary = protector.Protect("AeroLink HOME synthetic canary");
            (provider as IDisposable)?.Dispose();
            using var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            var retainedProvider = DataProtectionProvider.Create(new DirectoryInfo(keyRing), builder =>
                builder.SetApplicationName(applicationName));
            return new SyntheticApiFixture(root, keyRing, database, evidence, authorityRoot, applicationName,
                publicHost, certificatePath, certificateKeyPath,
                retainedProvider, retainedProvider.CreateProtector("AeroLink.Home.Continuity.Synthetic.v1"),
                protectedCanary, port);
        }

        public string UnprotectCanary() => protector.Unprotect(protectedCanary);

        public void Dispose()
        {
            (provider as IDisposable)?.Dispose();
            try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); } catch { }
        }
    }

    private sealed class OwnedApiProcess : IDisposable
    {
        private readonly SyntheticApiFixture fixture;
        private readonly Process process;
        private readonly Task<string> standardOutput;
        private readonly Task<string> standardError;

        public OwnedApiProcess(SyntheticApiFixture fixture)
        {
            this.fixture = fixture;
            var apiAssembly = typeof(Program).Assembly.Location;
            if (!File.Exists(apiAssembly)) throw new FileNotFoundException("The built API assembly is unavailable.", apiAssembly);
            var start = new ProcessStartInfo("dotnet")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(apiAssembly)!,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            start.ArgumentList.Add(apiAssembly);
            start.Environment["ASPNETCORE_ENVIRONMENT"] = "Production";
            start.Environment["DOTNET_ENVIRONMENT"] = "Production";
            start.Environment["SiteServer__Enabled"] = "true";
            start.Environment["SiteServer__PublicHost"] = fixture.PublicHost;
            start.Environment["SiteServer__BindAddress"] = "0.0.0.0";
            start.Environment["SiteServer__Port"] = fixture.Port.ToString();
            start.Environment["SiteServer__Http1Only"] = "true";
            start.Environment["SiteServer__Certificate__Path"] = fixture.CertificatePath;
            start.Environment["SiteServer__Certificate__KeyPath"] = fixture.CertificateKeyPath;
            start.Environment["Database__Provider"] = "Sqlite";
            start.Environment["ConnectionStrings__AeroLink"] = "Data Source=" + fixture.Database;
            start.Environment["Evidence__Root"] = fixture.Evidence;
            start.Environment["AEROLINK_NOTIFICATION_AUTHORITY_ROOT"] = fixture.AuthorityRoot;
            start.Environment["Connector__DeploymentId"] = "synthetic-home-capture";
            start.Environment["Connector__SigningKeyPath"] = Path.Combine(fixture.Evidence, "connector.pem");
            start.Environment["DemoData__Enabled"] = "false";
            start.Environment["Identity__SeedDemoAccounts"] = "false";
            start.Environment["Identity__AllowDemoAccounts"] = "false";
            start.Environment["Identity__CookieSecure"] = "true";
            start.Environment["Identity__BootstrapSecret"] = "synthetic-bootstrap-not-a-secret-0123456789";
            start.Environment["Notifications__BaseUrl"] = $"https://{fixture.PublicHost}:{fixture.Port}";
            start.Environment["Connector__PublicOrigin"] = $"https://{fixture.PublicHost}:{fixture.Port}";
            start.Environment["Instance__InstanceId"] = Guid.NewGuid().ToString("D");
            start.Environment["Instance__Label"] = "Synthetic HOME capture";
            start.Environment["Instance__Classification"] = "HomeCanonical";
            start.Environment["Runtime__Mode"] = "HOME-SYNTHETIC";
            start.Environment["Runtime__SourceSha"] = "synthetic-source";
            start.Environment["Runtime__SourceIdentity"] = "synthetic-source-identity";
            start.Environment["DataProtection__KeyRingPath"] = fixture.KeyRing;
            start.Environment["DataProtection__ApplicationName"] = fixture.ApplicationName;
            start.Environment["Logging__LogLevel__Default"] = "Warning";
            process = Process.Start(start) ?? throw new InvalidOperationException("The disposable API process did not start.");
            ProcessId = process.Id;
            standardOutput = process.StandardOutput.ReadToEndAsync();
            standardError = process.StandardError.ReadToEndAsync();
        }

        public int ProcessId { get; }
        public Uri LoopbackUri(string path) => new($"https://127.0.0.1:{fixture.Port}{path}");
        public async Task<HttpResponseMessage> GetNonLoopbackAsync(HttpClient client, IPAddress address, string path)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri($"https://{address}:{fixture.Port}{path}"));
            request.Headers.Host = $"{fixture.PublicHost}:{fixture.Port}";
            return await client.SendAsync(request);
        }

        public async Task WaitForReadyAsync()
        {
            using var client = NewClient();
            Exception? last = null;
            for (var attempt = 0; attempt < 240; attempt++)
            {
                if (process.HasExited)
                    throw new InvalidOperationException("The disposable API process exited before readiness. " + (await standardError));
                try
                {
                    using var response = await client.GetAsync(LoopbackUri("/health/live"));
                    if (response.IsSuccessStatusCode) return;
                    last = new InvalidOperationException($"Readiness returned {(int)response.StatusCode}.");
                }
                catch (Exception problem) { last = problem; }
                await Task.Delay(250);
            }
            throw new TimeoutException("The disposable API process did not become ready: " + last + " " + (await standardError));
        }

        public void Dispose()
        {
            try
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                process.WaitForExit(10000);
            }
            finally
            {
                process.Dispose();
            }
        }
    }
}
