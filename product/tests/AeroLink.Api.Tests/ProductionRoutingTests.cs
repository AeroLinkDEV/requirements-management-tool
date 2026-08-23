using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AeroLink.Domain.Identity;
using AeroLink.Domain.Programs;
using AeroLink.Domain.Verification;
using AeroLink.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace AeroLink.Api.Tests;

/// <summary>
/// The routing contract of the production-served shape: the API serving the built client, with
/// ClientHosting's fallback active.
///
/// The API test host never registers that fallback, so it sees the framework's automatic 405 for a
/// wrong-method request to an existing path while the deployed shape answered 404 "No such endpoint" — the
/// DEC-103 contract held only in the test host. These tests enable <c>Client:StaticFiles</c> so the served
/// shape is what is asserted.
/// </summary>
public sealed class ProductionRoutingTests
{
    private const string Member = "routing.engineer";
    private const string ClientTitle = "AeroLink production-shape test client";

    [Fact]
    public async Task The_served_shape_keeps_the_DEC_103_405_contract_and_the_404_and_deep_link_contracts()
    {
        using var directory = new TemporaryClientDirectory();
        using var factory = new AeroLinkApiFactory(staticFilesRoot: directory.Path);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("Origin", "http://localhost");
        client.DefaultRequestHeaders.Add("Sec-Fetch-Site", "same-origin");

        var projectId = await SeedAsync(factory);
        await LoginAsync(client);

        // The collection is read-only at this legacy root. No browser-shaped mutation can reopen the retired
        // verb, regardless of whether the caller has a mutation token.
        client.DefaultRequestHeaders.Remove("X-AeroLink-CSRF");
        using (var withoutCsrf = new HttpRequestMessage(HttpMethod.Post, "/api/test-procedures")
        {
            Content = JsonContent.Create(new { })
        })
        {
            using var refused = await client.SendAsync(withoutCsrf);
            Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        }

        await SecurityBoundaryTests.AuthorizeMutationsAsync(client);

        // DEC-103: the collection exists to be read, and only the verb that wrote to it is gone. A 404 would
        // mean the route had been renamed rather than retired.
        using (var direct = new HttpRequestMessage(HttpMethod.Post, "/api/test-procedures")
        {
            Content = JsonContent.Create(new { })
        })
        {
            using var response = await client.SendAsync(direct);
            Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
            Assert.Contains("GET", AllowedMethods(response), StringComparison.OrdinalIgnoreCase);
        }

        // The collection remains readable in the same shape.
        using (var page = await client.GetAsync($"/api/test-procedures?projectId={projectId}&page=1&pageSize=25"))
        {
            Assert.Equal(HttpStatusCode.OK, page.StatusCode);
            var body = JsonDocument.Parse(await page.Content.ReadAsStringAsync()).RootElement;
            Assert.True(body.GetProperty("totalCount").GetInt32() >= 1);
        }

        // A genuinely nonexistent API path keeps the existing JSON 404 contract.
        using (var missing = await client.GetAsync("/api/no-such-endpoint-xyz"))
        {
            Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
            var body = JsonDocument.Parse(await missing.Content.ReadAsStringAsync()).RootElement;
            Assert.Equal("endpoint_not_found", body.GetProperty("code").GetString());
        }

        // The former per-revision approval route is absent, not a 405: the path itself no longer exists.
        using (var approval = new HttpRequestMessage(
            HttpMethod.Post,
            "/api/test-procedures/00000000-0000-0000-0000-000000000000/approve")
        {
            Content = JsonContent.Create(new { })
        })
        {
            using var response = await client.SendAsync(approval);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
            Assert.Equal("endpoint_not_found", body.GetProperty("code").GetString());
        }

        // A non-API deep link still resolves to the client entry document.
        using (var deepLink = await client.GetAsync("/programs/demo/projects/demo/releases/1.6/command-center"))
        {
            var body = await deepLink.Content.ReadAsStringAsync();
            Assert.True(deepLink.StatusCode == HttpStatusCode.OK, $"{(int)deepLink.StatusCode}: {body}");
            Assert.StartsWith("text/html", deepLink.Content.Headers.ContentType?.ToString(), StringComparison.OrdinalIgnoreCase);
            Assert.Contains(ClientTitle, body);
        }
    }

    private static string AllowedMethods(HttpResponseMessage response)
    {
        if (response.Headers.TryGetValues("Allow", out var responseAllow)) return string.Join(",", responseAllow);
        if (response.Content.Headers.TryGetValues("Allow", out var contentAllow)) return string.Join(",", contentAllow);
        return string.Empty;
    }

    private static async Task<Guid> SeedAsync(AeroLinkApiFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        var now = DateTimeOffset.UtcNow;
        var program = new ProgramRecord("Routing Program", "RTE");
        var project = new ProjectRecord(program.Id, "Software", "Routing Software");
        var procedure = new TestProcedure(project.Id, "SYSTP-00000001", "Verify routing behaviour", Member, now,
            TestProcedureLevel.System);
        var revision = new TestProcedureRevision(procedure.Id, 1, "Objective", "Preconditions", "Steps", "Expected",
            TestProcedureState.Approved, Member, now);
        var account = new UserAccount(Member, Member, $"{Member}@example.test",
            IdentityService.HashPassword(AeroLinkApiFactory.MemberPassword), now);
        db.AddRange(program, project, procedure, revision, account);
        db.Add(new ProgramMembership(account.Id, program.Id, ProgramRole.Engineer, "test.setup", now));
        await db.SaveChangesAsync();
        return project.Id;
    }

    private static async Task LoginAsync(HttpClient client)
    {
        using var login = await client.PostAsJsonAsync("/api/auth/login",
            new { userName = Member, password = AeroLinkApiFactory.MemberPassword });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        await SecurityBoundaryTests.AuthorizeMutationsAsync(client);
    }

    private sealed class TemporaryClientDirectory : IDisposable
    {
        public string Path { get; } = Directory.CreateTempSubdirectory("aerolink-production-shape").FullName;

        public TemporaryClientDirectory()
        {
            File.WriteAllText(System.IO.Path.Combine(Path, "index.html"),
                $"<!doctype html><html><head><title>{ClientTitle}</title></head><body>AeroLink</body></html>");
        }

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); }
            catch (IOException) { /* A leftover temp directory is not worth failing a test over. */ }
            catch (UnauthorizedAccessException) { }
        }
    }
}
