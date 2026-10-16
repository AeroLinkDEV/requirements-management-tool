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
        // With `admin` not the last administrator, only the route's own guard stops a revoke by an outsider.
        var secondId = await AddAccountAsync(factory, "second.admin");
        Assert.Equal(HttpStatusCode.NoContent, (await administrator.PostAsync($"/api/admin/users/{secondId}/global-administrator", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await maintainer.DeleteAsync($"/api/admin/users/{secondId}/global-administrator")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await maintainer.GetAsync("/api/admin/users")).StatusCode);
        using var scope = factory.Services.CreateScope();
        Assert.True(await scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>().UserAccounts.AsNoTracking()
            .Where(x => x.Id == secondId).Select(x => x.IsGlobalAdministrator).SingleAsync());
    }

    [Fact]
    public async Task The_built_in_administrator_cannot_be_revoked_or_disabled_by_another_administrator()
    {
        using var factory = new AeroLinkApiFactory();
        using var administrator = factory.CreateClient();
        await SecurityBoundaryTests.BootstrapAndLoginAdministratorAsync(administrator);
        var adminId = await UserIdAsync(factory, "admin");
        var maintainerId = await AddMaintainerAsync(factory);
        using var maintainer = await LoginMaintainerAsync(factory);
        Assert.Equal(HttpStatusCode.NoContent, (await administrator.PostAsync($"/api/admin/users/{maintainerId}/global-administrator", null)).StatusCode);

        using var revoke = await maintainer.DeleteAsync($"/api/admin/users/{adminId}/global-administrator");
        Assert.Equal(HttpStatusCode.Conflict, revoke.StatusCode);
        Assert.Contains("built_in_administrator_protected", await revoke.Content.ReadAsStringAsync());
        using var disable = await maintainer.PostAsJsonAsync($"/api/admin/users/{adminId}/state", new { enabled = false });
        Assert.Equal(HttpStatusCode.Conflict, disable.StatusCode);
        Assert.Contains("built_in_administrator_protected", await disable.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, (await administrator.GetAsync("/api/admin/users")).StatusCode);

        // The built-in account may disable itself once another active administrator exists.
        using var selfDisable = await administrator.PostAsJsonAsync($"/api/admin/users/{adminId}/state", new { enabled = false });
        Assert.Equal(HttpStatusCode.NoContent, selfDisable.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await administrator.GetAsync("/api/admin/users")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await maintainer.GetAsync("/api/admin/users")).StatusCode);
    }

    [Fact]
    public async Task A_granted_administrator_gets_system_administration_but_not_the_built_in_authority_override()
    {
        using var factory = new AeroLinkApiFactory();
        using var administrator = factory.CreateClient();
        await SecurityBoundaryTests.BootstrapAndLoginAdministratorAsync(administrator);
        var maintainerId = await AddMaintainerAsync(factory);
        using var maintainer = await LoginMaintainerAsync(factory);
        Assert.Equal(HttpStatusCode.NoContent, (await administrator.PostAsync($"/api/admin/users/{maintainerId}/global-administrator", null)).StatusCode);
        Guid projectId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var program = new AeroLink.Domain.Programs.ProgramRecord("Override Program", "OVR");
            var project = new AeroLink.Domain.Programs.ProjectRecord(program.Id, "Override Project", "Override Product");
            db.AddRange(program, project);
            await db.SaveChangesAsync();
            projectId = project.Id;
        }

        // Project access without membership is the override, which stays with the built-in account.
        var configuration = $"/api/projects/{projectId}/approval-configuration";
        Assert.Equal(HttpStatusCode.Forbidden, (await maintainer.GetAsync(configuration)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await administrator.GetAsync(configuration)).StatusCode);

        var me = await maintainer.GetFromJsonAsync<System.Text.Json.JsonElement>("/api/auth/me");
        Assert.True(me.GetProperty("isSystemAdministrator").GetBoolean());
        Assert.False(me.GetProperty("isAdministrator").GetBoolean());
    }

    /// <summary>
    /// Owner decision (15 Oct, #1639): project creation and setup acceptance, artifact schemas and assurance
    /// policy are system powers. Both people below are ordinary Engineer members of the Program, as every
    /// Windows-provisioned account is; only the grant separates them.
    /// </summary>
    [Theory]
    [InlineData("workspace")]
    [InlineData("project-setup")]
    [InlineData("artifact-schema")]
    [InlineData("assurance-policy")]
    public async Task A_granted_administrator_may_use_each_moved_system_power_and_an_engineer_may_not(string area)
    {
        using var factory = new AeroLinkApiFactory();
        using var administrator = factory.CreateClient();
        await SecurityBoundaryTests.BootstrapAndLoginAdministratorAsync(administrator);
        using var workspace = await administrator.PostAsJsonAsync("/api/workspaces", new
        { programName = "System Powers", programCode = "SYP", projectName = "System Powers", softwareProduct = "Powers Product", initialRelease = "1.0" });
        Assert.Equal(HttpStatusCode.Created, workspace.StatusCode);
        var created = await workspace.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        var projectId = created.GetProperty("project").GetProperty("id").GetGuid();
        var maintainerId = await AddMaintainerAsync(factory);
        var engineerId = await AddAccountAsync(factory, "plain.engineer");
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var programId = await db.Projects.Where(x => x.Id == projectId).Select(x => x.ProgramId).SingleAsync();
            foreach (var id in new[] { maintainerId, engineerId })
                db.ProgramMemberships.Add(new ProgramMembership(id, programId, ProgramRole.Engineer, "test.setup", DateTimeOffset.UtcNow));
            await db.SaveChangesAsync();
        }
        Assert.Equal(HttpStatusCode.NoContent, (await administrator.PostAsync($"/api/admin/users/{maintainerId}/global-administrator", null)).StatusCode);
        Guid schemaId = Guid.Empty;
        if (area == "artifact-schema")
        {
            using var schema = await administrator.PostAsJsonAsync("/api/enterprise-requirements/schemas",
                new { projectId, key = "base", name = "Base", appliesTo = "System", description = "Base schema" });
            Assert.Equal(HttpStatusCode.Created, schema.StatusCode);
            schemaId = (await schema.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>()).GetProperty("id").GetGuid();
        }

        async Task<HttpStatusCode> Attempt(HttpClient client, string suffix)
        {
            using var response = area switch
            {
                "workspace" => await client.PostAsJsonAsync("/api/workspaces", new
                { programName = $"Created {suffix}", programCode = $"CR{suffix}", projectName = $"Created {suffix}", softwareProduct = "Product", initialRelease = "1.0" }),
                "project-setup" => await client.PostAsJsonAsync("/api/project-setups", new { projectName = $"Setup {suffix}" }),
                "artifact-schema" => await client.PostAsJsonAsync($"/api/enterprise-requirements/schemas/{schemaId}/fields",
                    new { key = $"field{suffix}", label = $"Field {suffix}", type = "ShortText", isRequired = false, sortOrder = 1, optionsJson = "[]" }),
                _ => await client.PutAsJsonAsync($"/api/projects/{projectId}/assurance-policy", new
                {
                    expectedVersion = (await client.GetFromJsonAsync<System.Text.Json.JsonElement>($"/api/projects/{projectId}/assurance-policy")).GetProperty("version").GetInt32(),
                    declaredLevel = "LevelC", reason = $"Declare as {suffix}.", selections = Array.Empty<object>(), deviations = Array.Empty<object>(),
                }),
            };
            return response.StatusCode;
        }

        using var engineer = await LoginAsync(factory, "plain.engineer");
        Assert.Equal(HttpStatusCode.Forbidden, await Attempt(engineer, "E"));
        using var maintainer = await LoginMaintainerAsync(factory);
        var allowed = area switch { "artifact-schema" => HttpStatusCode.NoContent, "assurance-policy" => HttpStatusCode.OK, _ => HttpStatusCode.Created };
        Assert.Equal(allowed, await Attempt(maintainer, "M"));
    }

    [Fact]
    public async Task The_last_active_administrator_cannot_be_revoked_or_disabled_even_by_themselves()
    {
        using var factory = new AeroLinkApiFactory();
        using var administrator = factory.CreateClient();
        await SecurityBoundaryTests.BootstrapAndLoginAdministratorAsync(administrator);
        var adminId = await UserIdAsync(factory, "admin");

        using var selfDisable = await administrator.PostAsJsonAsync($"/api/admin/users/{adminId}/state", new { enabled = false });
        Assert.Equal(HttpStatusCode.Conflict, selfDisable.StatusCode);
        Assert.Contains("last_global_administrator", await selfDisable.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, (await administrator.GetAsync("/api/admin/users")).StatusCode);
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

    private static Task<Guid> AddMaintainerAsync(AeroLinkApiFactory factory) => AddAccountAsync(factory, MaintainerUserName);

    private static async Task<Guid> AddAccountAsync(AeroLinkApiFactory factory, string userName)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        var account = new UserAccount(userName, userName, $"{userName}@example.test",
            IdentityService.HashPassword(AeroLinkApiFactory.MemberPassword), DateTimeOffset.UtcNow);
        db.UserAccounts.Add(account);
        await db.SaveChangesAsync();
        return account.Id;
    }

    private static Task<HttpClient> LoginMaintainerAsync(AeroLinkApiFactory factory) => LoginAsync(factory, MaintainerUserName);

    private static async Task<HttpClient> LoginAsync(AeroLinkApiFactory factory, string userName)
    {
        var client = factory.CreateClient();
        using var login = await client.PostAsJsonAsync("/api/auth/login", new { userName, password = AeroLinkApiFactory.MemberPassword });
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
