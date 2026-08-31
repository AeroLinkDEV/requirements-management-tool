using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AeroLink.Domain.Identity;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

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
        var programId = summary.GetProperty("programId").GetGuid();
        var projectId = summary.GetProperty("projectId").GetGuid();
        var activeReleaseId = summary.GetProperty("activeReleaseId").GetGuid();
        var overview = await client.GetFromJsonAsync<JsonElement>(
            $"/api/showcase/overview?projectId={projectId}&releaseId={activeReleaseId}");
        Assert.Equal(13, overview.GetProperty("activeRequests").GetInt32());

        // The endpoint is the operator-facing retry boundary. A second request must reuse the durable
        // ownership rows and preserve the exact controlled summary on the same disposable database. Remove
        // the SQA row first to prove this existing-FMS path does not recreate authority as a side effect.
        Guid sqaId;
        int scenarioRows;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            sqaId = await db.UserAccounts.Where(x => x.UserName == "quality.analyst").Select(x => x.Id).SingleAsync();
            scenarioRows = await db.ShowcaseUpgradeSteps.CountAsync(x => x.ProgramId == programId);
            db.ProgramMemberships.RemoveRange(await db.ProgramMemberships.Where(x => x.UserId == sqaId
                && x.ProgramId == programId && x.Role == ProgramRole.SoftwareQualityAnalyst).ToListAsync());
            await db.SaveChangesAsync();
        }
        using var retry = await client.PostAsync("/api/showcase/seed", content: null);
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        Assert.Equal(summary.GetProperty("projectId").GetGuid(),
            (await retry.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("projectId").GetGuid());
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            Assert.Equal(scenarioRows, await db.ShowcaseUpgradeSteps.CountAsync(x => x.ProgramId == programId));
            Assert.Empty(await db.ProgramMemberships.Where(x => x.UserId == sqaId
                && x.ProgramId == programId && x.Role == ProgramRole.SoftwareQualityAnalyst).ToListAsync());
        }
    }
}
