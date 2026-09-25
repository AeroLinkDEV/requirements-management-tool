using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Collections.Concurrent;
using AeroLink.Domain.Identity;
using AeroLink.Infrastructure.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace AeroLink.Api.Tests;

public sealed class ProjectSetupKestrelQualificationTests
{
    [Fact]
    public async Task Real_kestrel_denies_anonymous_and_outsider_chunked_uploads_without_raising_the_body_limit()
    {
        using var factory = new AeroLinkApiFactory();
        var limits = new UploadLimitObserver();
        using var configured = factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            services.AddSingleton<IStartupFilter>(limits)));
        configured.UseKestrel(0);
        using var administrator = configured.CreateClient();
        await SecurityBoundaryTests.BootstrapAndLoginAdministratorAsync(administrator);
        using var created = await administrator.PostAsJsonAsync("/api/project-setups", new { projectName = "Access transport fixture" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var draftId = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("draftId").GetGuid();
        using (var scope = configured.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            db.UserAccounts.Add(new UserAccount("upload.outsider", "Upload Outsider", "upload.outsider@example.test",
                IdentityService.HashPassword(AeroLinkApiFactory.MemberPassword), DateTimeOffset.UtcNow));
            await db.SaveChangesAsync();
        }
        var globalLimit = configured.Services.GetRequiredService<IOptions<KestrelServerOptions>>().Value.Limits.MaxRequestBodySize;
        Assert.True(globalLimit is < 32 * 1024 * 1024);
        foreach (var authenticated in new[] { false, true })
        {
            using var caller = configured.CreateClient();
            if (authenticated)
            {
                using var login = await caller.PostAsJsonAsync("/api/auth/login", new
                { userName = "upload.outsider", password = AeroLinkApiFactory.MemberPassword });
                Assert.Equal(HttpStatusCode.OK, login.StatusCode);
            }
            var probe = Guid.NewGuid().ToString("N");
            using var request = new HttpRequestMessage(HttpMethod.Post,
                $"/api/project-setups/{draftId}/source/upload?expectedVersion=1&fileName=denied.csv&probe={probe}")
            {
                Version = HttpVersion.Version11, VersionPolicy = HttpVersionPolicy.RequestVersionExact,
                Content = new ChunkedSource(new byte[32 * 1024 * 1024])
            };
            request.Headers.ExpectContinue = true;
            using var response = await caller.SendAsync(request);
            Assert.Equal(authenticated ? HttpStatusCode.Forbidden : HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.True(limits.Observed.TryGetValue(probe, out var observedLimit));
            Assert.Equal(globalLimit, observedLimit);
        }
        using var verification = configured.Services.CreateScope();
        Assert.False(await verification.ServiceProvider.GetRequiredService<AeroLinkDbContext>()
            .ProjectSetupSourcePackages.AnyAsync(x => x.DraftId == draftId));
    }

    private sealed class UploadLimitObserver : IStartupFilter
    {
        public ConcurrentDictionary<string, long?> Observed { get; } = new();
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (context, continuation) =>
            {
                await continuation();
                if (context.Request.Query.TryGetValue("probe", out var probe))
                    Observed[probe.ToString()] = context.Features.Get<IHttpMaxRequestBodySizeFeature>()?.MaxRequestBodySize;
            });
            next(app);
        };
    }

    [Fact]
    public async Task Real_kestrel_accepts_large_and_chunked_sources_and_rejects_over_limit_without_staging()
    {
        using var factory = new AeroLinkApiFactory();
        factory.UseKestrel(0);
        using var client = factory.CreateClient();
        client.Timeout = TimeSpan.FromMinutes(3);
        Assert.Contains("Kestrel", factory.Services.GetRequiredService<IServer>().GetType().FullName);
        Assert.True(factory.Services.GetRequiredService<IOptions<KestrelServerOptions>>().Value.Limits.MaxRequestBodySize
            is < 32 * 1024 * 1024, "The socket test must retain Kestrel's lower global limit and exercise the endpoint override.");
        await SecurityBoundaryTests.BootstrapAndLoginAdministratorAsync(client);

        foreach (var (size, chunked) in new[] { (32 * 1024 * 1024, true), (50 * 1024 * 1024, false),
                     (50 * 1024 * 1024 + 1, false), (50 * 1024 * 1024 + 1, true) })
        {
            using var created = await client.PostAsJsonAsync("/api/project-setups", new { projectName = "Transport fixture" });
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            var draft = await created.Content.ReadFromJsonAsync<JsonElement>();
            var draftId = draft.GetProperty("draftId").GetGuid();
            var bytes = new byte[size];
            Array.Fill(bytes, (byte)' ');
            Encoding.UTF8.GetBytes("Identifier,Statement\nSOURCE-1,An isolated transport requirement.\n").CopyTo(bytes, 0);
            using var request = new HttpRequestMessage(HttpMethod.Post,
                $"/api/project-setups/{draftId}/source/upload?expectedVersion=1&fileName=transport.csv")
            {
                Version = HttpVersion.Version11, VersionPolicy = HttpVersionPolicy.RequestVersionExact,
                Content = chunked ? new ChunkedSource(bytes) : new ByteArrayContent(bytes)
            };
            request.Content.Headers.ContentType = new("application/octet-stream");
            if (size <= 50 * 1024 * 1024)
            {
                using var response = await client.SendAsync(request);
                var body = await response.Content.ReadAsStringAsync();
                Assert.True(response.IsSuccessStatusCode, $"{size}, chunked={chunked}: {response.StatusCode} {body}");
                using var receipt = JsonDocument.Parse(body);
                Assert.Equal(size, receipt.RootElement.GetProperty("sizeBytes").GetInt64());
                Assert.Equal(Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
                    receipt.RootElement.GetProperty("sha256").GetString());
            }
            else
            {
                // Kestrel rejects an over-limit body and may close the connection while the client is still
                // writing it (#1117). Whether the client then reads the 400/413 or sees the reset is a timing
                // race HTTP permits, so either is a refusal; what must hold every time is that nothing was staged.
                try
                {
                    using var response = await client.SendAsync(request);
                    var body = await response.Content.ReadAsStringAsync();
                    Assert.True(response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.RequestEntityTooLarge,
                        $"{size}, chunked={chunked}: {response.StatusCode} {body}");
                }
                catch (HttpRequestException reset) when (reset.InnerException is IOException)
                {
                    // The server closed the connection on the over-limit body.
                }
                using var scope = factory.Services.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
                Assert.False(await db.ProjectSetupSourcePackages.AnyAsync(x => x.DraftId == draftId));
            }
        }
    }

    private sealed class ChunkedSource(byte[] bytes) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => stream.WriteAsync(bytes).AsTask();
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
    }
}
