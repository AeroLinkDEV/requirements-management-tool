using System.Net;
using System.Net.Sockets;
using System.Text;
using AeroLink.Domain.Integrations;
using AeroLink.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace AeroLink.Infrastructure.Tests;

/// <summary>
/// #849 Finding 1: an outbound webhook connection may only reach an address that was resolved, classified,
/// and approved for that request. All network answers here come from deterministic seams; no test touches
/// live DNS or a public destination. The only sockets are the loopback listener each test starts itself.
/// </summary>
public sealed class WebhookDestinationPolicyTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    private sealed class FakeResolver(IReadOnlyList<IPAddress> answers) : IWebhookDnsResolver
    {
        public List<string> Requests { get; } = [];
        public Func<string, IReadOnlyList<IPAddress>>? AnswerFor { get; set; }

        public Task<IReadOnlyList<IPAddress>> ResolveAsync(string host, CancellationToken cancellationToken)
        {
            Requests.Add(host);
            return Task.FromResult(AnswerFor?.Invoke(host) ?? answers);
        }
    }

    private static WebhookDestinationPolicy Policy(FakeResolver resolver) => new(resolver);

    private static async Task<ApprovedWebhookDestination> Validate(string url, bool allowInsecure = false, bool allowPrivate = false, FakeResolver? resolver = null)
    {
        resolver ??= new FakeResolver([]);
        return await Policy(resolver).ValidateAsync(new Uri(url), allowInsecure, allowPrivate, CancellationToken.None);
    }

    [Theory]
    [InlineData("10.1.2.3")]
    [InlineData("172.16.0.1")]
    [InlineData("172.31.255.255")]
    [InlineData("192.168.1.1")]
    [InlineData("127.0.0.1")]
    [InlineData("127.255.0.1")]
    [InlineData("0.0.0.0")]
    [InlineData("169.254.169.254")]
    [InlineData("100.64.0.1")]
    [InlineData("100.127.255.254")]
    [InlineData("192.0.0.5")]
    [InlineData("192.0.2.1")]
    [InlineData("198.51.100.7")]
    [InlineData("203.0.113.9")]
    [InlineData("198.18.0.1")]
    [InlineData("198.19.255.255")]
    [InlineData("224.0.0.1")]
    [InlineData("239.255.255.255")]
    [InlineData("240.0.0.1")]
    [InlineData("255.255.255.255")]
    [InlineData("::1")]
    [InlineData("::")]
    [InlineData("::ffff:10.0.0.5")]
    [InlineData("::ffff:127.0.0.1")]
    [InlineData("::ffff:169.254.169.254")]
    [InlineData("fe80::1")]
    [InlineData("fec0::5")]
    [InlineData("fc00::1")]
    [InlineData("fd12:3456::1")]
    [InlineData("ff02::1")]
    [InlineData("2001:db8::10")]
    [InlineData("2001::5")]
    [InlineData("2002:7f00:1::")]
    [InlineData("64:ff9b::a00:1")]
    [InlineData("::a00:1")]
    public void Prohibited_address_classes_fail_closed(string candidate) =>
        Assert.True(WebhookDestinationPolicy.IsProhibitedOutboundAddress(IPAddress.Parse(candidate)), candidate);

    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("1.1.1.1")]
    [InlineData("93.184.216.34")]
    [InlineData("2606:4700::1111")]
    [InlineData("2001:4860:4860::8888")]
    [InlineData("2606:2800:220:1:248:1893:25c8:1946")]
    public void Globally_routable_addresses_are_permitted(string candidate) =>
        Assert.False(WebhookDestinationPolicy.IsProhibitedOutboundAddress(IPAddress.Parse(candidate)), candidate);

    [Fact]
    public async Task Valid_public_literal_ipv4_is_accepted_without_consulting_dns()
    {
        var resolver = new FakeResolver([]);
        var approved = await Validate("https://93.184.216.34/hook", resolver: resolver);
        Assert.Equal([IPAddress.Parse("93.184.216.34")], approved.Addresses);
        Assert.Empty(resolver.Requests);
    }

    [Fact]
    public async Task Prohibited_literal_ipv4_is_refused() =>
        await Assert.ThrowsAsync<WebhookDestinationPolicyException>(() => Validate("https://10.0.0.5/hook"));

    [Fact]
    public async Task Prohibited_literal_ipv6_is_refused() =>
        await Assert.ThrowsAsync<WebhookDestinationPolicyException>(() => Validate("https://[fe80::1]/hook"));

    [Fact]
    public async Task Prohibited_mapped_literal_is_refused() =>
        await Assert.ThrowsAsync<WebhookDestinationPolicyException>(() => Validate("https://[::ffff:10.0.0.5]/hook"));

    [Fact]
    public async Task Hostname_resolving_entirely_to_permitted_addresses_is_accepted()
    {
        var resolver = new FakeResolver([IPAddress.Parse("93.184.216.34"), IPAddress.Parse("2606:4700::1111")]);
        var approved = await Validate("https://hooks.example.test/hook", resolver: resolver);
        Assert.Equal([IPAddress.Parse("93.184.216.34"), IPAddress.Parse("2606:4700::1111")], approved.Addresses);
        Assert.Equal(["hooks.example.test"], resolver.Requests);
    }

    [Fact]
    public async Task Hostname_resolving_to_a_prohibited_address_is_refused()
    {
        var resolver = new FakeResolver([IPAddress.Parse("10.0.0.7")]);
        await Assert.ThrowsAsync<WebhookDestinationPolicyException>(() => Validate("https://hooks.example.test/hook", resolver: resolver));
        Assert.Equal(["hooks.example.test"], resolver.Requests);
    }

    [Fact]
    public async Task Hostname_returning_mixed_answers_is_refused()
    {
        var resolver = new FakeResolver([IPAddress.Parse("93.184.216.34"), IPAddress.Parse("fd00::7")]);
        await Assert.ThrowsAsync<WebhookDestinationPolicyException>(() => Validate("https://hooks.example.test/hook", resolver: resolver));
    }

    [Fact]
    public async Task Empty_dns_result_fails_closed()
    {
        var resolver = new FakeResolver([]);
        await Assert.ThrowsAsync<WebhookDestinationPolicyException>(() => Validate("https://hooks.example.test/hook", resolver: resolver));
    }

    [Fact]
    public async Task Insecure_scheme_requires_the_explicit_override()
    {
        await Assert.ThrowsAsync<WebhookDestinationPolicyException>(() => Validate("http://93.184.216.34/hook"));
        var approved = await Validate("http://93.184.216.34/hook", allowInsecure: true);
        Assert.Equal([IPAddress.Parse("93.184.216.34")], approved.Addresses);
    }

    [Fact]
    public async Task Development_private_override_permits_only_its_intended_exception()
    {
        var privateResolver = new FakeResolver([IPAddress.Parse("10.0.0.5")]);
        var approved = await Validate("https://internal.example.test/hook", allowPrivate: true, resolver: privateResolver);
        Assert.Equal([IPAddress.Parse("10.0.0.5")], approved.Addresses);

        await Assert.ThrowsAsync<WebhookDestinationPolicyException>(() =>
            Policy(privateResolver).ValidateAsync(new Uri("http://10.0.0.5/hook"), allowInsecure: false, allowPrivate: true, CancellationToken.None));

        await Assert.ThrowsAsync<WebhookDestinationPolicyException>(() =>
            Policy(new FakeResolver([])).ValidateAsync(new Uri("https://internal.example.test/hook"), allowInsecure: true, allowPrivate: true, CancellationToken.None));
    }

    [Fact]
    public void Redirects_are_disabled_on_the_webhook_transport() =>
        Assert.False(WebhookConnectionTransport.CreateHandler(TimeSpan.FromSeconds(5)).AllowAutoRedirect);

    [Fact]
    public async Task Valid_delivery_reaches_only_the_approved_address_and_completes()
    {
        await using var responder = await LoopbackWebhookResponder.StartAsync("HTTP/1.1 200 OK\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
        using var handler = WebhookConnectionTransport.CreateHandler(TimeSpan.FromSeconds(5));
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
        using var request = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{responder.Port}/hook") { Content = new StringContent("{\"a\":1}", Encoding.UTF8, "application/json") };
        request.Options.Set(WebhookConnectionTransport.ApprovedAddressesOption, [IPAddress.Loopback]);
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, responder.ConnectionCount);
        Assert.Contains("POST /hook", responder.FirstRequest);
        Assert.Contains("{\"a\":1}", responder.FirstRequest);
    }

    [Fact]
    public async Task Changed_dns_answers_cannot_steer_the_pinned_connection()
    {
        await using var responder = await LoopbackWebhookResponder.StartAsync("HTTP/1.1 200 OK\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
        using var handler = WebhookConnectionTransport.CreateHandler(TimeSpan.FromSeconds(5));
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
        using var request = new HttpRequestMessage(HttpMethod.Post, $"http://hooks.example.test:{responder.Port}/hook") { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
        request.Options.Set(WebhookConnectionTransport.ApprovedAddressesOption, [IPAddress.Loopback]);
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, responder.ConnectionCount);
        Assert.Contains("hooks.example.test", responder.FirstRequest);
    }

    [Fact]
    public async Task Redirect_response_is_returned_and_never_followed_to_a_second_target()
    {
        await using var responder = await LoopbackWebhookResponder.StartAsync("HTTP/1.1 302 Found\r\nLocation: http://10.9.8.7/hook\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
        using var handler = WebhookConnectionTransport.CreateHandler(TimeSpan.FromSeconds(5));
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
        using var request = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{responder.Port}/hook") { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
        request.Options.Set(WebhookConnectionTransport.ApprovedAddressesOption, [IPAddress.Loopback]);
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.Equal(1, responder.ConnectionCount);
    }

    [Fact]
    public async Task Request_without_an_approved_destination_fails_closed()
    {
        await using var responder = await LoopbackWebhookResponder.StartAsync("HTTP/1.1 200 OK\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
        using var handler = WebhookConnectionTransport.CreateHandler(TimeSpan.FromSeconds(5));
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
        using var request = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{responder.Port}/hook") { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
        await Assert.ThrowsAsync<HttpRequestException>(() => client.SendAsync(request));
        Assert.Equal(0, responder.ConnectionCount);
    }

    [Fact]
    public async Task Worker_delivery_completes_through_the_pinned_transport()
    {
        await using var responder = await LoopbackWebhookResponder.StartAsync("HTTP/1.1 200 OK\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection("DataSource=:memory:");
        connection.Open();
        var services = new ServiceCollection();
        services.AddDbContext<AeroLinkDbContext>(options => options.UseSqlite(connection));
        services.AddDataProtection();
        services.AddScoped<IntegrationSecurityService>();
        services.AddSingleton<IWebhookDnsResolver, SystemWebhookDnsResolver>();
        services.AddSingleton<WebhookDestinationPolicy>();
        services.AddHttpClient("AeroLinkWebhooks", client => client.Timeout = TimeSpan.FromSeconds(10))
            .ConfigurePrimaryHttpMessageHandler(() => WebhookConnectionTransport.CreateHandler(TimeSpan.FromSeconds(5)));
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Integrations:AllowInsecureWebhookTargets"] = "true",
            ["Integrations:AllowPrivateWebhookTargets"] = "true",
        }).Build();
        services.AddSingleton<IConfiguration>(configuration);
        await using var provider = services.BuildServiceProvider();
        using (var scope = provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            await db.Database.EnsureCreatedAsync();
            var security = scope.ServiceProvider.GetRequiredService<IntegrationSecurityService>();
            var project = new AeroLink.Domain.Programs.ProjectRecord(Guid.NewGuid(), "Webhook policy test project", "AeroLink");
            db.Projects.Add(project);
            var projectId = project.Id;
            var subscription = new WebhookSubscription(projectId, "Pinned loopback", $"http://127.0.0.1:{responder.Port}/hook", "[\"requirement.updated\"]", security.ProtectWebhookSecret("whsec_pin_test"), "tester", Now);
            var integrationEvent = new IntegrationEvent(projectId, "requirement.updated", "Requirement", Guid.NewGuid(), "{\"a\":1}", "tester", Now);
            db.WebhookSubscriptions.Add(subscription);
            db.IntegrationEvents.Add(integrationEvent);
            db.WebhookDeliveries.Add(new WebhookDelivery(projectId, integrationEvent.Id, subscription.Id, Now));
            await db.SaveChangesAsync();
            var worker = new WebhookDeliveryWorker(
                scope.ServiceProvider.GetRequiredService<IServiceScopeFactory>(),
                configuration,
                NullLogger<WebhookDeliveryWorker>.Instance);
            await worker.DeliverBatchAsync(CancellationToken.None);
            var saved = await db.WebhookDeliveries.AsNoTracking().SingleAsync();
            Assert.Equal(WebhookDeliveryState.Delivered, saved.State);
            Assert.Equal(200, saved.ResponseStatusCode);
        }
        Assert.Equal(1, responder.ConnectionCount);
        Assert.Contains("X-AeroLink-Signature: v1=", responder.FirstRequest);
        Assert.Contains("X-AeroLink-Delivery:", responder.FirstRequest);
    }

    private sealed class LoopbackWebhookResponder(string response) : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private CancellationTokenSource? _cancellation;

        public int Port { get; private set; }
        public int ConnectionCount { get; private set; }
        public string FirstRequest { get; private set; } = "";

        public static async Task<LoopbackWebhookResponder> StartAsync(string response)
        {
            var responder = new LoopbackWebhookResponder(response);
            responder._listener.Start();
            responder.Port = ((IPEndPoint)responder._listener.LocalEndpoint).Port;
            responder._cancellation = new CancellationTokenSource();
            _ = responder.AcceptLoopAsync(responder._cancellation.Token);
            await Task.Yield();
            return responder;
        }

        private async Task AcceptLoopAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                TcpClient client;
                try { client = await _listener.AcceptTcpClientAsync(cancellationToken); }
                catch (OperationCanceledException) { return; }
                ConnectionCount++;
                _ = ServeAsync(client, cancellationToken);
            }
        }

        private async Task ServeAsync(TcpClient client, CancellationToken cancellationToken)
        {
            await using var stream = client.GetStream();
            var buffer = new byte[8192];
            var received = new MemoryStream();
            int headerEnd;
            while (!Encoding.ASCII.GetString(received.ToArray()).Contains("\r\n\r\n"))
            {
                var read = await stream.ReadAsync(buffer.AsMemory(), cancellationToken);
                if (read == 0) return;
                received.Write(buffer, 0, read);
            }
            headerEnd = received.ToArray().AsSpan().IndexOf("\r\n\r\n"u8);
            var headersText = Encoding.ASCII.GetString(received.ToArray()[..headerEnd]);
            var contentLength = 0;
            foreach (var line in headersText.Split("\r\n"))
            {
                var separator = line.IndexOf(':');
                if (separator > 0 && line[..separator].Trim().Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                    contentLength = int.Parse(line[(separator + 1)..].Trim());
            }
            var body = received.ToArray()[(headerEnd + 4)..];
            while (body.Length < contentLength)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(), cancellationToken);
                if (read == 0) break;
                received.Write(buffer, 0, read);
                body = received.ToArray()[(headerEnd + 4)..];
            }
            var requestText = Encoding.UTF8.GetString(received.ToArray());
            await stream.WriteAsync(Encoding.ASCII.GetBytes(response), cancellationToken);
            await stream.FlushAsync(cancellationToken);
            if (string.IsNullOrEmpty(requestText)) return;
            PublishFirst(requestText);
        }

        private static readonly object Gate = new();
        private void PublishFirst(string requestText)
        {
            lock (Gate) { if (string.IsNullOrEmpty(FirstRequest)) FirstRequest = requestText; }
        }

        public async ValueTask DisposeAsync()
        {
            _cancellation?.Cancel();
            _listener.Stop();
            GC.SuppressFinalize(this);
            await Task.CompletedTask;
        }
    }
}
