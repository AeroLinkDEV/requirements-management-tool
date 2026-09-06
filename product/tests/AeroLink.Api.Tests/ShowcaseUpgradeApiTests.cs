using System.Net;
using System.Net.Http.Json;
using System.IO;
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
    public async Task Upgrade_can_add_a_pending_problem_report_scenario_with_current_authority_timestamps()
    {
        using var factory = showcase.CreateFactory();
        using var client = factory.CreateClient();
        client.Timeout = TimeSpan.FromMinutes(10);
        using var login = await client.PostAsJsonAsync("/api/auth/login",
            new { userName = IdentityService.SystemAdministratorUserName, password = IdentitySeeder.DemoPassword });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        await SecurityBoundaryTests.AuthorizeMutationsAsync(client);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var current = DateTimeOffset.UtcNow.AddMinutes(-1);
            var authorityNames = new[] { "quality.analyst", "systems.author", "software.author", "test.engineer",
                "engineer.demo", "test.author", "project.lead" };
            var authorityIds = await db.UserAccounts.Where(x => authorityNames.Contains(x.UserName))
                .Select(x => x.Id).ToListAsync();
            foreach (var membership in await db.ProgramMemberships.Where(x => x.ProgramId == showcase.Summary.ProgramId
                    && authorityIds.Contains(x.UserId) && x.EndedAt == null).ToListAsync())
                db.Entry(membership).Property(x => x.GrantedAt).CurrentValue = current;
            db.ShowcaseUpgradeSteps.Remove(await db.ShowcaseUpgradeSteps.SingleAsync(x => x.ProgramId == showcase.Summary.ProgramId
                && x.StepKey == "scenario-richness/problem-report/01"));
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
    public async Task Upgrade_state_reports_the_work_distribution_diagnostic()
    {
        using var factory = showcase.CreateFactory();
        using var client = factory.CreateClient();
        using var login = await client.PostAsJsonAsync("/api/auth/login",
            new { userName = IdentityService.SystemAdministratorUserName, password = IdentitySeeder.DemoPassword });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);

        using var response = await client.GetAsync("/api/showcase/upgrade-state");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("seeded").GetBoolean());
        var distribution = body.GetProperty("distribution");

        // #913: the deterministic showcase must disperse current work across several synthetic
        // people, with shared-holder and zero-work contrast, and no single holder dominating.
        Assert.True(distribution.GetProperty("peopleHoldingWork").GetInt32() >= 5,
            "expected at least five distinct people holding work");
        Assert.True(distribution.GetProperty("multiHolderItems").GetInt32() >= 1,
            "expected at least one shared/multi-holder item");
        foreach (var check in distribution.GetProperty("checks").EnumerateObject())
            Assert.True(check.Value.GetBoolean(), $"distribution check {check.Name} failed");

        // The holder roster itself: each entry names the person and their holding count.
        var holders = distribution.GetProperty("holders").EnumerateArray().ToList();
        Assert.True(holders.Count >= 5);
        Assert.All(holders, holder =>
        {
            Assert.False(string.IsNullOrWhiteSpace(holder.GetProperty("userName").GetString()));
            Assert.True(holder.GetProperty("holds").GetInt32() > 0);
        });

        // By-basis counts: every held item is accounted on exactly one holder basis, several
        // bases are exercised, and at least one legitimate no-current-holder item exists.
        var basisCounts = distribution.GetProperty("holderBasisCounts");
        var basisEntries = basisCounts.EnumerateObject().ToList();
        Assert.True(basisEntries.Count >= 3, "expected at least three distinct holder bases");
        var countedHeldItems = 0;
        foreach (var basis in basisEntries)
        {
            Assert.True(basis.Value.GetInt32() > 0, $"holder basis {basis.Name} carries no items");
            countedHeldItems += basis.Value.GetInt32();
        }
        Assert.Equal(
            distribution.GetProperty("items").GetInt32() - distribution.GetProperty("unheld").GetInt32(),
            countedHeldItems);
        Assert.True(distribution.GetProperty("unheld").GetInt32() >= 1,
            "expected at least one legitimate no-current-holder item");
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
