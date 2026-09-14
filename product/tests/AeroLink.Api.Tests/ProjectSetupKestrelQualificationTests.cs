using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AeroLink.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace AeroLink.Api.Tests;

public sealed class ProjectSetupKestrelQualificationTests
{
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
            using var response = await client.SendAsync(request);
            var body = await response.Content.ReadAsStringAsync();
            if (size <= 50 * 1024 * 1024)
            {
                Assert.True(response.IsSuccessStatusCode, $"{size}, chunked={chunked}: {response.StatusCode} {body}");
                using var receipt = JsonDocument.Parse(body);
                Assert.Equal(size, receipt.RootElement.GetProperty("sizeBytes").GetInt64());
                Assert.Equal(Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
                    receipt.RootElement.GetProperty("sha256").GetString());
            }
            else
            {
                Assert.True(response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.RequestEntityTooLarge,
                    $"{size}, chunked={chunked}: {response.StatusCode} {body}");
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
