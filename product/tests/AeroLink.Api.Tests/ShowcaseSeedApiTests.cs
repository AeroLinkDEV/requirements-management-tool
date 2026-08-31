using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AeroLink.Domain.Identity;
using AeroLink.Infrastructure.Persistence;

namespace AeroLink.Api.Tests;

public sealed class ShowcaseSeedApiTests
{
    [Fact]
    public async Task Showcase_seed_endpoint_builds_the_controlled_dataset_on_disposable_sqlite()
    {
        using var factory = new AeroLinkApiFactory(seedDemoAccounts: true, allowDemoAccounts: true);
        using var client = factory.CreateClient();
        client.Timeout = TimeSpan.FromMinutes(10);
        using var login = await client.PostAsJsonAsync("/api/auth/login",
            new { userName = IdentityService.SystemAdministratorUserName, password = IdentitySeeder.DemoPassword });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        await SecurityBoundaryTests.AuthorizeMutationsAsync(client);

        using var response = await client.PostAsync("/api/showcase/seed", content: null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var summary = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(150, summary.GetProperty("systemRequirements").GetInt32());
        Assert.Equal(400, summary.GetProperty("highLevelRequirements").GetInt32());
        Assert.Equal(700, summary.GetProperty("lowLevelRequirements").GetInt32());
        Assert.Equal(520, summary.GetProperty("testExecutions").GetInt32());

        // The endpoint is the operator-facing retry boundary. A second request must reuse the durable
        // ownership rows and preserve the exact controlled summary on the same disposable database.
        using var retry = await client.PostAsync("/api/showcase/seed", content: null);
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        Assert.Equal(summary.GetProperty("projectId").GetGuid(),
            (await retry.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("projectId").GetGuid());
    }
}
