using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AeroLink.Domain.Identity;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AeroLink.Api.Tests;

[Collection(ShowcaseApiCollection.Name)]
public sealed class ShowcaseUpgradeApiTests(ShowcaseApiFixture showcase)
{
    [Fact]
    public async Task Upgrade_can_add_a_pending_interface_scenario_with_current_authority_timestamps()
    {
        using var factory = showcase.CreateFactory();
        using var client = factory.CreateClient();
        using var login = await client.PostAsJsonAsync("/api/auth/login",
            new { userName = IdentityService.SystemAdministratorUserName, password = IdentitySeeder.DemoPassword });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        await SecurityBoundaryTests.AuthorizeMutationsAsync(client);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var current = DateTimeOffset.UtcNow.AddMinutes(-1);
            var authorityNames = new[] { "quality.analyst", "systems.author", "software.author", "systems.reviewer",
                "assurance.reviewer", "lead.reviewer", "manager.reviewer", "cm.fms" };
            var authorityIds = await db.UserAccounts.Where(x => authorityNames.Contains(x.UserName))
                .Select(x => x.Id).ToListAsync();
            foreach (var membership in await db.ProgramMemberships.Where(x => x.ProgramId == showcase.Summary.ProgramId
                    && authorityIds.Contains(x.UserId) && x.EndedAt == null).ToListAsync())
                db.Entry(membership).Property(x => x.GrantedAt).CurrentValue = current;
            db.ShowcaseUpgradeSteps.Remove(await db.ShowcaseUpgradeSteps.SingleAsync(x => x.ProgramId == showcase.Summary.ProgramId
                && x.StepKey == "scenario-richness/interface/01"));
            db.ShowcaseUpgradeSteps.Remove(await db.ShowcaseUpgradeSteps.SingleAsync(x => x.ProgramId == showcase.Summary.ProgramId
                && x.StepKey == "scenario-richness"));
            await db.SaveChangesAsync();
        }

        using var response = await client.PostAsync("/api/showcase/upgrade", content: null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("healthy").GetBoolean());
        Assert.Contains(body.GetProperty("applied").EnumerateArray(), item =>
            item.GetString()!.StartsWith("scenario-richness", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Upgrade_returns_conflict_without_active_sqa_identity_or_current_authority()
    {
        using var factory = showcase.CreateFactory();
        using var client = factory.CreateClient();
        using var login = await client.PostAsJsonAsync("/api/auth/login",
            new { userName = IdentityService.SystemAdministratorUserName, password = IdentitySeeder.DemoPassword });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        await SecurityBoundaryTests.AuthorizeMutationsAsync(client);

        Guid sqaId;
        int stepCount;
        int closureCount;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var account = await db.UserAccounts.SingleAsync(x => x.UserName == "quality.analyst");
            sqaId = account.Id;
            account.Disable(DateTimeOffset.UtcNow);
            stepCount = await db.ShowcaseUpgradeSteps.CountAsync(x => x.ProgramId == showcase.Summary.ProgramId);
            closureCount = await db.ProblemReportRevisions.CountAsync(x => x.EventType == "ClosureApproved");
            await db.SaveChangesAsync();
        }

        using var disabled = await client.PostAsync("/api/showcase/upgrade", content: null);
        Assert.Equal(HttpStatusCode.Conflict, disabled.StatusCode);
        var disabledBody = await disabled.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(disabledBody.GetProperty("healthy").GetBoolean());
        Assert.Equal("quality_analyst_account_inactive", disabledBody.GetProperty("code").GetString());

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var account = await db.UserAccounts.SingleAsync(x => x.Id == sqaId);
            account.Enable();
            var membership = await db.ProgramMemberships.SingleAsync(x => x.UserId == sqaId
                && x.ProgramId == showcase.Summary.ProgramId && x.Role == ProgramRole.SoftwareQualityAnalyst
                && x.EndedAt == null);
            membership.End("admin", DateTimeOffset.UtcNow);
            await db.SaveChangesAsync();
        }

        using var ended = await client.PostAsync("/api/showcase/upgrade", content: null);
        Assert.Equal(HttpStatusCode.Conflict, ended.StatusCode);
        var endedBody = await ended.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(endedBody.GetProperty("healthy").GetBoolean());
        Assert.Equal("quality_analyst_membership_inactive", endedBody.GetProperty("code").GetString());

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            Assert.Equal(stepCount, await db.ShowcaseUpgradeSteps.CountAsync(x => x.ProgramId == showcase.Summary.ProgramId));
            Assert.Equal(closureCount, await db.ProblemReportRevisions.CountAsync(x => x.EventType == "ClosureApproved"));
            Assert.Equal(1, await db.ProgramMemberships.CountAsync(x => x.UserId == sqaId
                && x.ProgramId == showcase.Summary.ProgramId && x.Role == ProgramRole.SoftwareQualityAnalyst));
        }
    }
}
