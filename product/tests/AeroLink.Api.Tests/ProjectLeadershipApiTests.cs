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
/// The Project Leadership API proves the #816 authority model end to end: eligibility is enforced, a
/// replacement is atomic, a standing backup answers the same demands as the primary and loses them the
/// moment the designation is removed, and the retired ProjectEngineeringLead cannot be newly granted.
///
/// The authority assertions resolve IdentityService from the hosted application, so they exercise the
/// resolver the real gates run — never a test double of it.
/// </summary>
public sealed class ProjectLeadershipApiTests : IClassFixture<SharedApiHost>
{
    private readonly SharedApiHost _host;

    public ProjectLeadershipApiTests(SharedApiHost host) => _host = host;

    private sealed record Seeded(Guid ProjectId, Guid ProgramId, Guid ManagerId, string ManagerName,
        Guid FirstId, Guid SecondId, Guid BackupId, Guid IneligibleId, string FirstEngineerName,
        string SecondEngineerName, string BackupEngineerName, string TesterName);

    private static async Task<Seeded> SeedAsync(AeroLinkApiFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        var now = DateTimeOffset.UtcNow;
        // Unique per test: user accounts and Program codes are globally unique-constrained, so a shared
        // host/database requires per-test identities.
        var tag = Guid.NewGuid().ToString("N")[..8];
        var managerName = $"leadership.manager.{tag}";
        var firstEngineer = $"leadership.first.{tag}";
        var secondEngineer = $"leadership.second.{tag}";
        var backupEngineer = $"leadership.backup.{tag}";
        var tester = $"leadership.tester.{tag}";
        var program = new ProgramRecord($"Leadership Program {tag}", $"LSP{tag}");
        var project = new ProjectRecord(program.Id, "Flight Software", "Leadership Software");
        db.AddRange(program, project);

        UserAccount Account(string name) =>
            new(name, name, $"{name}@example.test", IdentityService.HashPassword(AeroLinkApiFactory.MemberPassword), now);

        var manager = Account(managerName);
        var first = Account(firstEngineer);
        var second = Account(secondEngineer);
        var backup = Account(backupEngineer);
        var testerAccount = Account(tester);
        db.AddRange(manager, first, second, backup, testerAccount);
        // The manager is eligible through the ProgramManager role; the three engineers
        // carry the base role the System Engineering Lead position requires; the tester deliberately does
        // not, which is what the eligibility refusal proves.
        db.AddRange(
            new ProgramMembership(manager.Id, program.Id, ProgramRole.ProgramManager, "test.setup", now),
            new ProgramMembership(first.Id, program.Id, ProgramRole.SystemEngineer, "test.setup", now),
            new ProgramMembership(second.Id, program.Id, ProgramRole.SystemEngineer, "test.setup", now),
            new ProgramMembership(backup.Id, program.Id, ProgramRole.SystemEngineer, "test.setup", now),
            new ProgramMembership(testerAccount.Id, program.Id, ProgramRole.SystemTestEngineer, "test.setup", now));
        // Managing leadership is the Program Manager position's, not the eligibility role's, so the manager
        // is elevated into the post rather than merely granted the role that qualifies them for it.
        db.Add(new ProjectLeadershipAssignment(
            program.Id, ProjectLeadershipPosition.ProgramManager, manager.Id, "test.setup", now));
        await db.SaveChangesAsync();
        return new(project.Id, program.Id, manager.Id, managerName,
            first.Id, second.Id, backup.Id, testerAccount.Id,
            firstEngineer, secondEngineer, backupEngineer, tester);
    }

    private static async Task SignInAsync(HttpClient client, string userName)
    {
        var login = await client.PostAsJsonAsync("/api/auth/login",
            new { userName, password = AeroLinkApiFactory.MemberPassword });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        await SecurityBoundaryTests.AuthorizeMutationsAsync(client);
    }

    [Fact]
    public async Task Assignment_requires_the_base_role_and_activation_grants_the_leadership_authority()
    {
        var seeded = await SeedAsync(_host.Factory);
        using var client = _host.CreateClient();
        await SignInAsync(client, seeded.ManagerName);

        // An ineligible person is refused with the reason, and the refusal grants nothing.
        var position = "SystemEngineeringLead";
        var refused = await client.PostAsJsonAsync($"/api/projects/{seeded.ProjectId}/leadership/{position}/primary",
            new { holderUserId = seeded.IneligibleId });
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);

        var assigned = await client.PostAsJsonAsync($"/api/projects/{seeded.ProjectId}/leadership/{position}/primary",
            new { holderUserId = seeded.FirstId });
        Assert.True(assigned.IsSuccessStatusCode);

        using var scope = _host.Factory.Services.CreateScope();
        var identity = scope.ServiceProvider.GetRequiredService<IdentityService>();
        Assert.True(await identity.HasRoleAsync(seeded.FirstId, seeded.ProgramId, ProgramRole.SystemEngineeringLead, DateTimeOffset.UtcNow, default));
        Assert.False(await identity.HasRoleAsync(seeded.IneligibleId, seeded.ProgramId, ProgramRole.SystemEngineeringLead, DateTimeOffset.UtcNow, default));
    }

    [Fact]
    public async Task A_replacement_is_atomic_and_the_previous_holder_loses_the_authority()
    {
        var seeded = await SeedAsync(_host.Factory);
        using var client = _host.CreateClient();
        await SignInAsync(client, seeded.ManagerName);

        var position = "SystemEngineeringLead";
        Assert.True((await client.PostAsJsonAsync($"/api/projects/{seeded.ProjectId}/leadership/{position}/primary",
            new { holderUserId = seeded.FirstId })).IsSuccessStatusCode);

        var replaced = await client.PostAsJsonAsync($"/api/projects/{seeded.ProjectId}/leadership/{position}/primary",
            new { holderUserId = seeded.SecondId });
        Assert.True(replaced.IsSuccessStatusCode);
        var body = await replaced.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(seeded.FirstId, body.GetProperty("replaced").GetGuid());

        using var scope = _host.Factory.Services.CreateScope();
        var identity = scope.ServiceProvider.GetRequiredService<IdentityService>();
        Assert.False(await identity.HasRoleAsync(seeded.FirstId, seeded.ProgramId, ProgramRole.SystemEngineeringLead, DateTimeOffset.UtcNow, default));
        Assert.True(await identity.HasRoleAsync(seeded.SecondId, seeded.ProgramId, ProgramRole.SystemEngineeringLead, DateTimeOffset.UtcNow, default));
    }

    [Fact]
    public async Task A_standing_backup_answers_the_same_demands_until_the_designation_is_removed()
    {
        var seeded = await SeedAsync(_host.Factory);
        using var client = _host.CreateClient();
        await SignInAsync(client, seeded.ManagerName);

        var position = "SystemEngineeringLead";
        Assert.True((await client.PostAsJsonAsync($"/api/projects/{seeded.ProjectId}/leadership/{position}/primary",
            new { holderUserId = seeded.FirstId })).IsSuccessStatusCode);
        Assert.True((await client.PostAsJsonAsync($"/api/projects/{seeded.ProjectId}/leadership/{position}/backup",
            new { backupUserId = seeded.BackupId })).IsSuccessStatusCode);

        using var scope = _host.Factory.Services.CreateScope();
        var identity = scope.ServiceProvider.GetRequiredService<IdentityService>();
        Assert.True(await identity.HasRoleAsync(seeded.BackupId, seeded.ProgramId, ProgramRole.SystemEngineeringLead, DateTimeOffset.UtcNow, default));

        // A person who is the current primary cannot also be that position's backup.
        var selfBackup = await client.PostAsJsonAsync($"/api/projects/{seeded.ProjectId}/leadership/{position}/backup",
            new { backupUserId = seeded.FirstId });
        Assert.True(selfBackup.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.BadRequest, selfBackup.StatusCode.ToString());

        // Changing the backup is one atomic operation: the second engineer becomes the standing backup and
        // the authority moves with the designation in the same transaction.
        var changed = await client.PutAsJsonAsync($"/api/projects/{seeded.ProjectId}/leadership/{position}/backup",
            new { backupUserId = seeded.SecondId });
        Assert.Equal(HttpStatusCode.NoContent, changed.StatusCode);
        Assert.True(await identity.HasRoleAsync(seeded.SecondId, seeded.ProgramId, ProgramRole.SystemEngineeringLead, DateTimeOffset.UtcNow, default));
        Assert.False(await identity.HasRoleAsync(seeded.BackupId, seeded.ProgramId, ProgramRole.SystemEngineeringLead, DateTimeOffset.UtcNow, default));

        // Removing the designation ends the authority immediately, with the row retained as history.
        var removed = await client.DeleteAsync($"/api/projects/{seeded.ProjectId}/leadership/{position}/backup");
        Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
        Assert.False(await identity.HasRoleAsync(seeded.SecondId, seeded.ProgramId, ProgramRole.SystemEngineeringLead, DateTimeOffset.UtcNow, default));
    }

    [Fact]
    public async Task Promoting_the_backup_ends_the_designation_atomically()
    {
        var seeded = await SeedAsync(_host.Factory);
        using var client = _host.CreateClient();
        await SignInAsync(client, seeded.ManagerName);

        var position = "SystemEngineeringLead";
        Assert.True((await client.PostAsJsonAsync($"/api/projects/{seeded.ProjectId}/leadership/{position}/primary",
            new { holderUserId = seeded.FirstId })).IsSuccessStatusCode);
        Assert.True((await client.PostAsJsonAsync($"/api/projects/{seeded.ProjectId}/leadership/{position}/backup",
            new { backupUserId = seeded.BackupId })).IsSuccessStatusCode);

        // The current backup is promoted to primary: the designation ends in the same operation, so the
        // same person is never simultaneously primary and backup.
        var promoted = await client.PostAsJsonAsync($"/api/projects/{seeded.ProjectId}/leadership/{position}/primary",
            new { holderUserId = seeded.BackupId });
        Assert.True(promoted.IsSuccessStatusCode);

        using var scope = _host.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        Assert.Equal(0, await db.ProjectLeadershipBackups.CountAsync(x => x.Position == ProjectLeadershipPosition.SystemEngineeringLead && x.RemovedAt == null));
        Assert.Equal(1, await db.ProjectLeadershipBackups.CountAsync(x => x.Position == ProjectLeadershipPosition.SystemEngineeringLead && x.RemovedAt != null));
    }

    [Theory]
    [InlineData(ProgramRole.SystemEngineeringLead)]
    [InlineData(ProgramRole.SoftwareEngineeringLead)]
    [InlineData(ProgramRole.SystemTestLead)]
    [InlineData(ProgramRole.SoftwareTestLead)]
    [InlineData(ProgramRole.ProjectEngineeringLead)]
    public async Task A_retired_position_role_cannot_be_newly_granted_through_the_project_roster(
        ProgramRole retiredRole)
    {
        var seeded = await SeedAsync(_host.Factory);
        using var client = _host.CreateClient();
        await SignInAsync(client, seeded.ManagerName);

        var granted = await client.PostAsJsonAsync($"/api/projects/{seeded.ProjectId}/personnel",
            new { userId = seeded.FirstId, role = retiredRole.ToString() });
        Assert.Equal(HttpStatusCode.Conflict, granted.StatusCode);
        Assert.Contains("retired", await granted.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData(ProgramRole.SystemEngineeringLead)]
    [InlineData(ProgramRole.SoftwareEngineeringLead)]
    [InlineData(ProgramRole.SystemTestLead)]
    [InlineData(ProgramRole.SoftwareTestLead)]
    [InlineData(ProgramRole.ProjectEngineeringLead)]
    public async Task A_retired_position_role_cannot_be_newly_granted_through_global_administration(
        ProgramRole retiredRole)
    {
        using var factory = new AeroLinkApiFactory();
        using var admin = factory.CreateClient();
        await SecurityBoundaryTests.BootstrapAndLoginAdministratorAsync(admin);

        Guid userId;
        Guid programId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var now = DateTimeOffset.UtcNow;
            var program = new ProgramRecord($"Retired role {retiredRole}", $"RR{Guid.NewGuid():N}"[..12]);
            var user = new UserAccount($"retired.{Guid.NewGuid():N}"[..40], "Retired role target",
                $"retired.{Guid.NewGuid():N}@example.test", IdentityService.HashPassword(AeroLinkApiFactory.MemberPassword), now);
            db.AddRange(program, user);
            await db.SaveChangesAsync();
            userId = user.Id;
            programId = program.Id;
        }

        var adminGrant = await admin.PostAsJsonAsync($"/api/admin/users/{userId}/memberships",
            new { programId, role = retiredRole.ToString() });
        Assert.Equal(HttpStatusCode.Conflict, adminGrant.StatusCode);
        Assert.Contains("retired", await adminGrant.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task The_project_engineer_position_carries_the_retired_leads_review_authority()
    {
        var seeded = await SeedAsync(_host.Factory);
        using var client = _host.CreateClient();
        await SignInAsync(client, seeded.ManagerName);

        // Only the Project Engineer base role is required for eligibility, and the position is granted to
        // exactly one of the two people who hold it.
        var holder = new UserAccount($"leadership.pe.holder.{Guid.NewGuid():N}"[..30], "PE Position Holder",
            "pe.holder@example.test", IdentityService.HashPassword(AeroLinkApiFactory.MemberPassword), DateTimeOffset.UtcNow);
        var second = new UserAccount($"leadership.second.pe.{Guid.NewGuid():N}"[..30], "Second Project Engineer",
            "second.pe@example.test", IdentityService.HashPassword(AeroLinkApiFactory.MemberPassword), DateTimeOffset.UtcNow);
        using (var dbScope = _host.Factory.Services.CreateScope())
        {
            var db = dbScope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            db.AddRange(holder, second);
            db.AddRange(
                new ProgramMembership(holder.Id, seeded.ProgramId, ProgramRole.ProjectEngineer, "test.setup", DateTimeOffset.UtcNow),
                new ProgramMembership(second.Id, seeded.ProgramId, ProgramRole.ProjectEngineer, "test.setup", DateTimeOffset.UtcNow));
            await db.SaveChangesAsync();
        }
        var position = "ProjectEngineer";
        Assert.True((await client.PostAsJsonAsync($"/api/projects/{seeded.ProjectId}/leadership/{position}/primary",
            new { holderUserId = holder.Id })).IsSuccessStatusCode);

        using var scope = _host.Factory.Services.CreateScope();
        var identity = scope.ServiceProvider.GetRequiredService<IdentityService>();
        // The position answers the demands the retired ProjectEngineeringLead role used to answer...
        Assert.True(await identity.HasRoleAsync(holder.Id, seeded.ProgramId, ProgramRole.ProjectEngineeringLead, DateTimeOffset.UtcNow, default));
        Assert.True(await identity.HasRoleAsync(holder.Id, seeded.ProgramId, ProgramRole.Approver, DateTimeOffset.UtcNow, default));
        // ...without those authorities existing anywhere else: a second holder of the base Project
        // Engineer role gains none of it.
        Assert.False(await identity.HasRoleAsync(second.Id, seeded.ProgramId, ProgramRole.ProjectEngineeringLead, DateTimeOffset.UtcNow, default));
        Assert.False(await identity.HasRoleAsync(second.Id, seeded.ProgramId, ProgramRole.Approver, DateTimeOffset.UtcNow, default));
    }
}
