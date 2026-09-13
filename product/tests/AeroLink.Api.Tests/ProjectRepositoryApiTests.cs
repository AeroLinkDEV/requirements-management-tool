using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AeroLink.Domain.Identity;
using AeroLink.Domain.Programs;
using AeroLink.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace AeroLink.Api.Tests;

public sealed class ProjectRepositoryApiTests : IClassFixture<SharedApiHost>
{
    private readonly SharedApiHost host;
    public ProjectRepositoryApiTests(SharedApiHost host) => this.host = host;

    [Fact]
    public async Task DeferredSetupCanBeConfiguredLaterWithTruthfulUnconfiguredServiceAndStaleEditRefusal()
    {
        var data = await SeedAsync();
        using var client = host.CreateClient();
        await SignIn(client, data.Manager);
        var path = $"/api/projects/{data.Project}/repository";
        var deferred = await client.PutAsJsonAsync(path, new { expectedVersion = 0, mode = "ConfigureLater" });
        Assert.True(deferred.IsSuccessStatusCode, await deferred.Content.ReadAsStringAsync());
        using var pending = JsonDocument.Parse(await deferred.Content.ReadAsStringAsync());
        Assert.Equal("Pending", pending.RootElement.GetProperty("status").GetString());
        var configured = await client.PutAsJsonAsync(path, new
        { expectedVersion = 1, mode = "ConnectNow", provider = "GitLab", endpoint = "https://gitlab.example/group/project" });
        Assert.True(configured.IsSuccessStatusCode, await configured.Content.ReadAsStringAsync());
        var verify = await client.PostAsJsonAsync(path + "/verify", new { expectedVersion = 2 });
        Assert.True(verify.IsSuccessStatusCode, await verify.Content.ReadAsStringAsync());
        using var observed = JsonDocument.Parse(await verify.Content.ReadAsStringAsync());
        Assert.Equal("service_unconfigured", observed.RootElement.GetProperty("observation").GetProperty("code").GetString());
        Assert.Equal("ConfiguredUnverified", observed.RootElement.GetProperty("repository").GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, observed.RootElement.GetProperty("repository").GetProperty("remoteProjectId").ValueKind);
        var stale = await client.PutAsJsonAsync(path, new { expectedVersion = 2, mode = "ConfigureLater" });
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        using var readback = JsonDocument.Parse(await client.GetStringAsync(path));
        Assert.Equal(3, readback.RootElement.GetProperty("repository").GetProperty("version").GetInt64());
    }

    [Fact]
    public async Task RepositoryAuthorityIsProjectScopedAndOrdinaryMembershipCannotConfigureOrProbe()
    {
        var data = await SeedAsync();
        using var client = host.CreateClient();
        await SignIn(client, data.Member);
        var path = $"/api/projects/{data.Project}/repository";
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(path)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PutAsJsonAsync(path, new { expectedVersion = 0, mode = "ConfigureLater" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync(path + "/verify", new { expectedVersion = 1 })).StatusCode);
        using var manager = host.CreateClient();
        await SignIn(manager, data.Manager);
        var other = $"/api/projects/{data.OtherProject}/repository";
        Assert.Equal(HttpStatusCode.Forbidden, (await manager.GetAsync(other)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await manager.PutAsJsonAsync(other, new { expectedVersion = 0, mode = "ConfigureLater" })).StatusCode);
    }

    private async Task<(Guid Project, Guid OtherProject, string Manager, string Member)> SeedAsync()
    {
        using var scope = host.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        var tag = Guid.NewGuid().ToString("N")[..8];
        var program = new ProgramRecord("Repository test " + tag, "RP" + tag);
        var otherProgram = new ProgramRecord("Other repository " + tag, "RO" + tag);
        var project = new ProjectRecord(program.Id, "Repository project", "Software");
        var other = new ProjectRecord(otherProgram.Id, "Independent project", "Software");
        var now = DateTimeOffset.UtcNow;
        UserAccount User(string role) => new($"repo.{role}.{tag}", role, $"{role}.{tag}@example.test",
            IdentityService.HashPassword(AeroLinkApiFactory.MemberPassword), now);
        var manager = User("manager"); var member = User("member");
        db.AddRange(program, otherProgram, project, other, manager, member,
            new ProgramMembership(manager.Id, program.Id, ProgramRole.Administrator, "test.setup", now),
            new ProgramMembership(member.Id, program.Id, ProgramRole.Engineer, "test.setup", now));
        await db.SaveChangesAsync();
        return (project.Id, other.Id, manager.UserName, member.UserName);
    }

    private static async Task SignIn(HttpClient client, string name)
    {
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/auth/login",
            new { userName = name, password = AeroLinkApiFactory.MemberPassword })).StatusCode);
        await SecurityBoundaryTests.AuthorizeMutationsAsync(client);
    }
}
