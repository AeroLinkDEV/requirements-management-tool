using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AeroLink.Domain.Identity;
using AeroLink.Domain.Programs;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AeroLink.Api.Tests;

/// <summary>
/// #816 Slice 3 P1 finding: holding the Project Engineer BASE ROLE must not grant roster mutation
/// authority. Only the Project Engineer leadership primary, standing backup, ProgramManager base role,
/// legacy ProjectEngineeringLead, program-scoped Administrator, or global administrator may change the
/// roster. This is the authority separation that #816 exists to enforce.
/// </summary>
public sealed class RosterAuthoritySeparationTests : IClassFixture<SharedApiHost>
{
    private readonly SharedApiHost _host;

    public RosterAuthoritySeparationTests(SharedApiHost host) => _host = host;

    private sealed record Seeded(Guid ProjectId, Guid ProgramId, Guid PeBaseOnly, Guid PePrimary,
        Guid PeBackup, Guid ProgramManagerId, Guid OutsiderId, string PeBaseOnlyName,
        string PePrimaryName, string PeBackupName, string ProgramManagerName, string OutsiderName);

    private static async Task<Seeded> SeedAsync(AeroLinkApiFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        var now = DateTimeOffset.UtcNow;
        var tag = Guid.NewGuid().ToString("N")[..8];
        var program = new ProgramRecord($"RosterAuth Program {tag}", $"RAU{tag}");
        var project = new ProjectRecord(program.Id, "Flight Software", "RosterAuth Software");
        db.AddRange(program, project);

        UserAccount Account(string name) =>
            new(name, name, $"{name}@example.test", IdentityService.HashPassword(AeroLinkApiFactory.MemberPassword), now);

        var peBaseOnly = Account($"rauth.pe.base.{tag}");
        var pePrimary = Account($"rauth.pe.primary.{tag}");
        var peBackup = Account($"rauth.pe.backup.{tag}");
        var programManager = Account($"rauth.pm.{tag}");
        var outsider = Account($"rauth.outsider.{tag}");
        db.AddRange(peBaseOnly, pePrimary, peBackup, programManager, outsider);
        db.AddRange(
            // The PE primary has the base role AND the leadership assignment.
            new ProgramMembership(pePrimary.Id, program.Id, ProgramRole.ProjectEngineer, "test.setup", now),
            new ProgramMembership(peBaseOnly.Id, program.Id, ProgramRole.ProjectEngineer, "test.setup", now),
            new ProgramMembership(peBackup.Id, program.Id, ProgramRole.ProjectEngineer, "test.setup", now),
            new ProgramMembership(programManager.Id, program.Id, ProgramRole.ProgramManager, "test.setup", now),
            // The PE leadership primary assignment (Slice 2 domain).
            new ProjectLeadershipAssignment(program.Id, ProjectLeadershipPosition.ProjectEngineer, pePrimary.Id, "test.setup", now),
            // The standing backup designation.
            new ProjectLeadershipBackup(program.Id, ProjectLeadershipPosition.ProjectEngineer, peBackup.Id, "test.setup", now),
            // The PM leadership primary assignment: the PM base role alone is eligibility, not authority.
            new ProjectLeadershipAssignment(program.Id, ProjectLeadershipPosition.ProgramManager, programManager.Id, "test.setup", now));
        await db.SaveChangesAsync();
        return new(project.Id, program.Id, peBaseOnly.Id, pePrimary.Id, peBackup.Id, programManager.Id, outsider.Id,
            peBaseOnly.UserName, pePrimary.UserName, peBackup.UserName, programManager.UserName, outsider.UserName);
    }

    private static async Task SignInAsync(HttpClient client, string userName)
    {
        var login = await client.PostAsJsonAsync("/api/auth/login",
            new { userName, password = AeroLinkApiFactory.MemberPassword });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        await SecurityBoundaryTests.AuthorizeMutationsAsync(client);
    }

    [Fact]
    public async Task Base_project_engineer_without_leadership_cannot_mutate_the_roster()
    {
        var seeded = await SeedAsync(_host.Factory);
        using var client = _host.CreateClient();
        await SignInAsync(client, seeded.PeBaseOnlyName);

        // Reading is fine; mutating is not.
        var read = await client.GetAsync($"/api/projects/{seeded.ProjectId}/personnel");
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);

        var attempt = await client.PostAsJsonAsync($"/api/projects/{seeded.ProjectId}/personnel",
            new { userId = seeded.OutsiderId, role = nameof(ProgramRole.SystemEngineer) });
        Assert.Equal(HttpStatusCode.Forbidden, attempt.StatusCode);
    }

    [Fact]
    public async Task Project_engineer_leadership_primary_can_mutate_the_roster()
    {
        var seeded = await SeedAsync(_host.Factory);
        using var client = _host.CreateClient();
        await SignInAsync(client, seeded.PePrimaryName);

        var attempt = await client.PostAsJsonAsync($"/api/projects/{seeded.ProjectId}/personnel",
            new { userId = seeded.OutsiderId, role = nameof(ProgramRole.SystemEngineer) });
        Assert.True(attempt.IsSuccessStatusCode || attempt.StatusCode == HttpStatusCode.Conflict,
            $"Expected success or conflict (already exists), got {attempt.StatusCode}");
    }

    [Fact]
    public async Task Project_engineer_standing_backup_can_mutate_the_roster()
    {
        var seeded = await SeedAsync(_host.Factory);
        using var client = _host.CreateClient();
        await SignInAsync(client, seeded.PeBackupName);

        var attempt = await client.PostAsJsonAsync($"/api/projects/{seeded.ProjectId}/personnel",
            new { userId = seeded.OutsiderId, role = nameof(ProgramRole.SystemEngineer) });
        Assert.True(attempt.IsSuccessStatusCode || attempt.StatusCode == HttpStatusCode.Conflict,
            $"Expected success or conflict, got {attempt.StatusCode}");
    }

    [Fact]
    public async Task An_unrelated_project_member_cannot_mutate_the_roster()
    {
        var seeded = await SeedAsync(_host.Factory);
        using var client = _host.CreateClient();
        await SignInAsync(client, seeded.OutsiderName);

        var attempt = await client.PostAsJsonAsync($"/api/projects/{seeded.ProjectId}/personnel",
            new { userId = seeded.OutsiderId, role = nameof(ProgramRole.SystemEngineer) });
        Assert.Equal(HttpStatusCode.Forbidden, attempt.StatusCode);
    }

    /// <summary>
    /// A Program Manager leadership primary can mutate the roster. A person with only the ProgramManager
    /// base role (eligibility without elevation) cannot — that is the #816 base-role vs leadership
    /// separation.
    /// </summary>
    [Fact]
    public async Task Program_manager_leadership_primary_can_mutate_the_roster()
    {
        var seeded = await SeedAsync(_host.Factory);
        using var client = _host.CreateClient();
        await SignInAsync(client, seeded.ProgramManagerName);

        var attempt = await client.PostAsJsonAsync($"/api/projects/{seeded.ProjectId}/personnel",
            new { userId = seeded.OutsiderId, role = nameof(ProgramRole.SystemEngineer) });
        Assert.True(attempt.IsSuccessStatusCode || attempt.StatusCode == HttpStatusCode.Conflict,
            $"Expected success or conflict, got {attempt.StatusCode}");
    }

    [Fact]
    public async Task Program_manager_base_eligibility_without_leadership_cannot_mutate_the_roster()
    {
        var seeded = await SeedAsync(_host.Factory);
        using var client = _host.CreateClient();

        // Create a member with the ProgramManager BASE ROLE only (no leadership assignment).
        var baseOnly = $"rauth.pm.base.only.{Guid.NewGuid():N}"[..8];
        using (var scope = _host.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var account = new UserAccount(baseOnly, baseOnly, $"{baseOnly}@example.test",
                IdentityService.HashPassword(AeroLinkApiFactory.MemberPassword), DateTimeOffset.UtcNow);
            db.Add(account);
            db.Add(new ProgramMembership(account.Id, seeded.ProgramId, ProgramRole.ProgramManager, "test.setup", DateTimeOffset.UtcNow));
            await db.SaveChangesAsync();
        }

        await SignInAsync(client, baseOnly);
        var attempt = await client.PostAsJsonAsync($"/api/projects/{seeded.ProjectId}/personnel",
            new { userId = seeded.OutsiderId, role = nameof(ProgramRole.SystemEngineer) });
        Assert.Equal(HttpStatusCode.Forbidden, attempt.StatusCode);
    }

    [Fact]
    public async Task Ending_the_project_engineer_leadership_assignment_removes_roster_authority()
    {
        var seeded = await SeedAsync(_host.Factory);

        // The PE primary initially has authority.
        using var primaryClient = _host.CreateClient();
        await SignInAsync(primaryClient, seeded.PePrimaryName);
        var before = await primaryClient.GetAsync($"/api/projects/{seeded.ProjectId}/personnel");
        var beforeBody = await before.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(beforeBody.GetProperty("canManage").GetBoolean(),
            "PE leadership primary should have roster authority before the assignment is ended.");

        // End the PE leadership assignment.
        using (var scope = _host.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var assignment = await db.ProjectLeadershipAssignments
                .SingleAsync(x => x.ProgramId == seeded.ProgramId
                    && x.Position == ProjectLeadershipPosition.ProjectEngineer && x.EndedAt == null);
            assignment.End("admin", DateTimeOffset.UtcNow);
            await db.SaveChangesAsync();
        }

        // The authority is gone immediately: canManage flips to false.
        using var afterClient = _host.CreateClient();
        await SignInAsync(afterClient, seeded.PePrimaryName);
        var after = await afterClient.GetAsync($"/api/projects/{seeded.ProjectId}/personnel");
        var afterBody = await after.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(afterBody.GetProperty("canManage").GetBoolean(),
            "PE leadership primary must lose roster authority when the leadership assignment ends.");
    }
}
