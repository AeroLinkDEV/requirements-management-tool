using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AeroLink.Domain.Identity;
using AeroLink.Domain.Programs;
using AeroLink.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace AeroLink.Api.Tests;

public sealed class ProjectSetupDiscoveryApiTests : IClassFixture<SharedApiHost>
{
    private readonly SharedApiHost host;
    public ProjectSetupDiscoveryApiTests(SharedApiHost host) => this.host = host;

    [Fact]
    public async Task CreatorCanSaveExitAndResumeWhileAnUnrelatedEmployeeCannotReadOrEditTheDraft()
    {
        using var admin = host.CreateClient();
        await SecurityBoundaryTests.BootstrapAndLoginAdministratorAsync(admin);
        var tag = Guid.NewGuid().ToString("N");
        var now = DateTimeOffset.UtcNow;
        UserAccount User(string role) => new($"setup.{role}.{tag}", role, $"{role}.{tag}@example.test",
            IdentityService.HashPassword(AeroLinkApiFactory.MemberPassword), now);
        var creator = User("creator"); var outsider = User("outsider");
        var draft = new ProjectSetupDraft(creator.Id, creator.UserName, "Previously started setup");
        using (var scope = host.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            db.AddRange(creator, outsider, draft);
            await db.SaveChangesAsync();
        }
        using var client = host.CreateClient();
        await Login(client, creator.UserName);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/project-setups", new { projectName = "Unauthorized new setup" })).StatusCode);
        var saved = await client.PostAsJsonAsync($"/api/project-setups/{draft.Id}/save-and-exit", new
        {
            expectedVersion = 1, currentStep = "FirstBuild",
            project = new { name = "Saved project draft", softwareProduct = "Saved software" },
        });
        Assert.True(saved.IsSuccessStatusCode, await saved.Content.ReadAsStringAsync());
        using var other = host.CreateClient();
        await Login(other, outsider.UserName);
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/api/project-setups/{draft.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await other.PutAsJsonAsync($"/api/project-setups/{draft.Id}", new { expectedVersion = 2, currentStep = "Details" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await other.PostAsJsonAsync($"/api/project-setups/{draft.Id}/save-and-exit", new { expectedVersion = 2, currentStep = "Details" })).StatusCode);
        using var hidden = JsonDocument.Parse(await other.GetStringAsync("/api/project-setups"));
        Assert.DoesNotContain(hidden.RootElement.EnumerateArray(), x => x.GetProperty("draftId").GetGuid() == draft.Id);

        using var resumed = host.CreateClient();
        await Login(resumed, creator.UserName);
        using var listed = JsonDocument.Parse(await resumed.GetStringAsync("/api/project-setups"));
        var found = Assert.Single(listed.RootElement.EnumerateArray(), x => x.GetProperty("draftId").GetGuid() == draft.Id);
        Assert.Equal("Saved project draft", found.GetProperty("project").GetProperty("name").GetString());
        Assert.Equal("FirstBuild", found.GetProperty("currentStep").GetString());
        Assert.Equal(2, found.GetProperty("version").GetInt64());
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync($"/api/project-setups/{draft.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await resumed.PostAsJsonAsync($"/api/project-setups/{draft.Id}/save-and-exit", new { expectedVersion = 1, currentStep = "Details" })).StatusCode);
    }

    private static async Task Login(HttpClient client, string name)
    {
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/auth/login", new
        { userName = name, password = AeroLinkApiFactory.MemberPassword })).StatusCode);
        await SecurityBoundaryTests.AuthorizeMutationsAsync(client);
    }
}
