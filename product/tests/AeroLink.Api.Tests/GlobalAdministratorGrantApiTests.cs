using System.Net;
using System.Net.Http.Json;
using AeroLink.Domain.Identity;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AeroLink.Api.Tests;

/// <summary>
/// #1639: global administration is a persisted grant an administrator gives and takes away, not the account
/// named <c>admin</c>. The grant must reach the session's authority, only an administrator may make it, the
/// installation must never be left without an active administrator, and each change is in the security audit.
/// </summary>
public sealed class GlobalAdministratorGrantApiTests
{
    private const string MaintainerUserName = "host.maintainer";

    [Fact]
    public async Task Granted_administrator_reaches_admin_routes_until_revoked_and_both_changes_are_audited()
    {
        using var factory = new AeroLinkApiFactory();
        using var administrator = factory.CreateClient();
        await SecurityBoundaryTests.BootstrapAndLoginAdministratorAsync(administrator);
        var maintainerId = await AddMaintainerAsync(factory);
        using var maintainer = await LoginMaintainerAsync(factory);
        Assert.Equal(HttpStatusCode.Forbidden, (await maintainer.GetAsync("/api/admin/users")).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await administrator.PostAsync($"/api/admin/users/{maintainerId}/global-administrator", null)).StatusCode);
        // The maintainer's existing session picks the grant up on its next request.
        Assert.Equal(HttpStatusCode.OK, (await maintainer.GetAsync("/api/admin/users")).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await administrator.DeleteAsync($"/api/admin/users/{maintainerId}/global-administrator")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await maintainer.GetAsync("/api/admin/users")).StatusCode);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        var audit = await db.SecurityAuditEvents.AsNoTracking()
            .Where(x => x.Target == MaintainerUserName && x.EventType.StartsWith("GlobalAdministrator"))
            .Select(x => new { x.EventType, x.ActorId, x.Outcome }).ToListAsync();
        Assert.Equal(
            [("GlobalAdministratorGranted", "admin", "Success"), ("GlobalAdministratorRevoked", "admin", "Success")],
            audit.Select(x => (x.EventType, x.ActorId, x.Outcome)).Order().ToList());
    }

    [Fact]
    public async Task A_person_who_is_not_an_administrator_cannot_grant_global_administrator()
    {
        using var factory = new AeroLinkApiFactory();
        using var administrator = factory.CreateClient();
        await SecurityBoundaryTests.BootstrapAndLoginAdministratorAsync(administrator);
        var maintainerId = await AddMaintainerAsync(factory);
        using var maintainer = await LoginMaintainerAsync(factory);

        Assert.Equal(HttpStatusCode.Forbidden, (await maintainer.PostAsync($"/api/admin/users/{maintainerId}/global-administrator", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await maintainer.GetAsync("/api/admin/users")).StatusCode);
    }

    [Fact]
    public async Task The_last_active_administrator_cannot_be_revoked_even_by_themselves()
    {
        using var factory = new AeroLinkApiFactory();
        using var administrator = factory.CreateClient();
        await SecurityBoundaryTests.BootstrapAndLoginAdministratorAsync(administrator);
        var adminId = await UserIdAsync(factory, "admin");

        Assert.Equal(HttpStatusCode.Conflict, (await administrator.DeleteAsync($"/api/admin/users/{adminId}/global-administrator")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await administrator.GetAsync("/api/admin/users")).StatusCode);

        // With a second administrator in place, the first may step down; the second is then the last.
        var maintainerId = await AddMaintainerAsync(factory);
        using var maintainer = await LoginMaintainerAsync(factory);
        Assert.Equal(HttpStatusCode.NoContent, (await administrator.PostAsync($"/api/admin/users/{maintainerId}/global-administrator", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await administrator.DeleteAsync($"/api/admin/users/{adminId}/global-administrator")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await administrator.GetAsync("/api/admin/users")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await maintainer.DeleteAsync($"/api/admin/users/{maintainerId}/global-administrator")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await maintainer.GetAsync("/api/admin/users")).StatusCode);
    }

    private static async Task<Guid> AddMaintainerAsync(AeroLinkApiFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        var account = new UserAccount(MaintainerUserName, "Host Maintainer", "host.maintainer@example.test",
            IdentityService.HashPassword(AeroLinkApiFactory.MemberPassword), DateTimeOffset.UtcNow);
        db.UserAccounts.Add(account);
        await db.SaveChangesAsync();
        return account.Id;
    }

    private static async Task<HttpClient> LoginMaintainerAsync(AeroLinkApiFactory factory)
    {
        var client = factory.CreateClient();
        using var login = await client.PostAsJsonAsync("/api/auth/login", new { userName = MaintainerUserName, password = AeroLinkApiFactory.MemberPassword });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        await SecurityBoundaryTests.AuthorizeMutationsAsync(client);
        return client;
    }

    private static async Task<Guid> UserIdAsync(AeroLinkApiFactory factory, string userName)
    {
        using var scope = factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>().UserAccounts.AsNoTracking()
            .Where(x => x.UserName == userName).Select(x => x.Id).SingleAsync();
    }
}
