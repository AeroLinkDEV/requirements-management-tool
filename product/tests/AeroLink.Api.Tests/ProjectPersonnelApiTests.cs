using System.Net;
using System.Net.Http.Json;
using AeroLink.Domain.Identity;
using AeroLink.Domain.Programs;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AeroLink.Api.Tests;

/// <summary>
/// A project's own view of who is on it.
///
/// Membership administration existed only as a global console organised by user, gated on the single `admin`
/// account, so a Program Manager could not see their own team. These drive the project-scoped routes, and
/// three rules that only exist because of them: a position one person holds refuses a second holder, ending
/// somebody keeps the record rather than deleting it, and a standing backup carries the holder's authority
/// with no interval to expire.
/// </summary>
public sealed class ProjectPersonnelApiTests : IClassFixture<SharedApiHost>
{
    private readonly SharedApiHost _host;

    public ProjectPersonnelApiTests(SharedApiHost host)
    {
        _host = host;
    }

    private sealed record Seeded(Guid ProjectId, Guid ProgramId, Guid ManagerId, Guid EngineerId, Guid DeputyId, Guid OutsiderId,
        string ManagerName, string EngineerName, string DeputyName, string OutsiderName);

    private static async Task<Seeded> SeedAsync(AeroLinkApiFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        var now = DateTimeOffset.UtcNow;
        // Unique per test: user accounts and Program codes are globally unique-constrained, so a shared
        // host/database requires per-test identities.
        var tag = Guid.NewGuid().ToString("N")[..8];
        var managerName = $"personnel.manager.{tag}";
        var engineerName = $"personnel.engineer.{tag}";
        var deputyName = $"personnel.deputy.{tag}";
        var outsiderName = $"personnel.outsider.{tag}";
        var program = new ProgramRecord($"Personnel Program {tag}", $"PSN{tag}");
        var project = new ProjectRecord(program.Id, "Flight Software", "Personnel Software");
        db.AddRange(program, project);

        UserAccount Account(string name) =>
            new(name, name, $"{name}@example.test", IdentityService.HashPassword(AeroLinkApiFactory.MemberPassword), now);

        var manager = Account(managerName);
        var engineer = Account(engineerName);
        var deputy = Account(deputyName);
        var outsider = Account(outsiderName);
        db.AddRange(manager, engineer, deputy, outsider);
        db.AddRange(
            new ProgramMembership(manager.Id, program.Id, ProgramRole.ProgramManager, "test.setup", now),
            // Both rows deliberately: the retired SystemEngineeringLead membership is what a database
            // upgraded from before #816 still holds, and the roster endpoints must keep managing it as
            // history. The base role beside it is the position's eligibility.
            new ProgramMembership(engineer.Id, program.Id, ProgramRole.SystemEngineeringLead, "test.setup", now),
            new ProgramMembership(engineer.Id, program.Id, ProgramRole.SystemEngineer, "test.setup", now),
            new ProgramMembership(deputy.Id, program.Id, ProgramRole.SystemEngineer, "test.setup", now));
        // Roster stewardship is the Program Manager *position*, not the role that makes somebody eligible
        // for it, so the manager is elevated rather than merely granted. The engineer holds the System
        // Engineering Lead position for the same reason: the retired role name confers nothing on its own.
        db.AddRange(
            new ProjectLeadershipAssignment(program.Id, ProjectLeadershipPosition.ProgramManager, manager.Id, "test.setup", now),
            new ProjectLeadershipAssignment(program.Id, ProjectLeadershipPosition.SystemEngineeringLead, engineer.Id, "test.setup", now));
        await db.SaveChangesAsync();
        return new(project.Id, program.Id, manager.Id, engineer.Id, deputy.Id, outsider.Id,
            managerName, engineerName, deputyName, outsiderName);
    }

    private static async Task SignInAsync(HttpClient client, string userName)
    {
        var login = await client.PostAsJsonAsync("/api/auth/login",
            new { userName, password = AeroLinkApiFactory.MemberPassword });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        await SecurityBoundaryTests.AuthorizeMutationsAsync(client);
    }

    [Fact]
    public async Task A_program_manager_may_read_and_change_their_own_project_roster()
    {
        var seeded = await SeedAsync(_host.Factory);
        using var client = _host.CreateClient();
        await SignInAsync(client, seeded.ManagerName);

        var response = await client.GetAsync($"/api/projects/{seeded.ProjectId}/personnel");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<PersonnelResponse>();
        Assert.NotNull(body);
        Assert.True(body!.CanManage);
        Assert.Equal(3, body.Members.Count(member => member.IsCurrent));
    }

    /// <summary>
    /// Somebody on the project who does not lead it sees the roster and cannot change it. Knowing who the
    /// Configuration Manager is should not require privilege; appointing one should.
    /// </summary>
    [Fact]
    public async Task An_ordinary_member_reads_the_roster_but_cannot_change_it()
    {
        var seeded = await SeedAsync(_host.Factory);
        using var client = _host.CreateClient();
        await SignInAsync(client, seeded.DeputyName);

        var read = await client.GetAsync($"/api/projects/{seeded.ProjectId}/personnel");
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        Assert.False((await read.Content.ReadFromJsonAsync<PersonnelResponse>())!.CanManage);

        var attempt = await client.PostAsJsonAsync($"/api/projects/{seeded.ProjectId}/personnel",
            new { userId = seeded.OutsiderId, role = nameof(ProgramRole.SystemEngineer) });
        Assert.Equal(HttpStatusCode.Forbidden, attempt.StatusCode);
    }

    [Fact]
    public async Task Somebody_outside_the_project_cannot_read_its_roster()
    {
        var seeded = await SeedAsync(_host.Factory);
        using var client = _host.CreateClient();
        await SignInAsync(client, seeded.OutsiderName);

        var response = await client.GetAsync($"/api/projects/{seeded.ProjectId}/personnel");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    /// <summary>
    /// The rule that makes a lead a position rather than a label. Without it a project could record four
    /// System Engineering Leads and no answer to which of them a review stage means.
    /// </summary>
    [Fact]
    public async Task A_position_one_person_holds_refuses_a_second_holder()
    {
        var seeded = await SeedAsync(_host.Factory);
        using var client = _host.CreateClient();
        await SignInAsync(client, seeded.ManagerName);

        var response = await client.PostAsJsonAsync($"/api/projects/{seeded.ProjectId}/personnel",
            new { userId = seeded.DeputyId, role = nameof(ProgramRole.SystemEngineeringLead) });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("System Engineering Lead", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_discipline_accepts_as_many_members_as_it_needs()
    {
        var seeded = await SeedAsync(_host.Factory);
        using var client = _host.CreateClient();
        await SignInAsync(client, seeded.ManagerName);

        var response = await client.PostAsJsonAsync($"/api/projects/{seeded.ProjectId}/personnel",
            new { userId = seeded.OutsiderId, role = nameof(ProgramRole.SystemEngineer) });

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    /// <summary>
    /// Ending a position keeps the row. The roster still has to answer what it was during a period that has
    /// already closed, which a deleted membership cannot.
    /// </summary>
    [Fact]
    public async Task Ending_a_position_retains_the_record_and_removes_the_authority()
    {
        var seeded = await SeedAsync(_host.Factory);
        using var client = _host.CreateClient();
        await SignInAsync(client, seeded.ManagerName);

        var ended = await client.DeleteAsync(
            $"/api/projects/{seeded.ProjectId}/personnel/{seeded.EngineerId}/roles/{nameof(ProgramRole.SystemEngineeringLead)}");
        Assert.Equal(HttpStatusCode.NoContent, ended.StatusCode);

        using var scope = _host.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        var membership = await db.ProgramMemberships.AsNoTracking()
            .SingleAsync(x => x.UserId == seeded.EngineerId && x.Role == ProgramRole.SystemEngineeringLead);
        Assert.NotNull(membership.EndedAt);
        Assert.Equal(seeded.ManagerName, membership.EndedBy);

        var identity = scope.ServiceProvider.GetRequiredService<IdentityService>();
        // The authority does NOT go with the legacy membership, because since #816 it never came from it.
        // The engineer still holds the System Engineering Lead position, and that is what answers the demand.
        Assert.True(await identity.HasRoleAsync(seeded.EngineerId, seeded.ProgramId,
            ProgramRole.SystemEngineeringLead, DateTimeOffset.UtcNow, default));

        // Ending the assignment is what removes it. Proving both halves here is the point: an upgrade that
        // ended the membership and left the assignment would look like a revocation and not be one, and the
        // reverse — the shape #824 shipped — left a replaced holder still signing.
        var assignment = await db.ProjectLeadershipAssignments
            .SingleAsync(x => x.ProgramId == seeded.ProgramId && x.HolderUserId == seeded.EngineerId
                              && x.Position == ProjectLeadershipPosition.SystemEngineeringLead && x.EndedAt == null);
        assignment.End("test.setup", DateTimeOffset.UtcNow);
        await db.SaveChangesAsync();
        Assert.False(await identity.HasRoleAsync(seeded.EngineerId, seeded.ProgramId,
            ProgramRole.SystemEngineeringLead, DateTimeOffset.UtcNow, default));
    }

    /// <summary>
    /// The position becomes available once its holder's role has ended — the singular rule bites on current
    /// holders, not on everybody who ever held it.
    /// </summary>
    [Fact]
    public async Task A_position_can_be_reassigned_once_its_holder_has_been_ended()
    {
        var seeded = await SeedAsync(_host.Factory);
        using var client = _host.CreateClient();
        await SignInAsync(client, seeded.ManagerName);

        await client.DeleteAsync($"/api/projects/{seeded.ProjectId}/personnel/{seeded.EngineerId}/roles/{nameof(ProgramRole.SystemEngineeringLead)}");
        var reassigned = await client.PostAsJsonAsync($"/api/projects/{seeded.ProjectId}/personnel",
            new { userId = seeded.DeputyId, role = nameof(ProgramRole.SystemEngineeringLead) });

        Assert.Equal(HttpStatusCode.NoContent, reassigned.StatusCode);
    }

    /// <summary>
    /// Somebody who left and came back is not blocked by their own history. The uniqueness that keeps a role
    /// from being granted twice is scoped to memberships that have not ended.
    /// </summary>
    [Fact]
    public async Task Somebody_who_left_can_be_added_again()
    {
        var seeded = await SeedAsync(_host.Factory);
        using var client = _host.CreateClient();
        await SignInAsync(client, seeded.ManagerName);

        await client.DeleteAsync($"/api/projects/{seeded.ProjectId}/personnel/{seeded.DeputyId}/roles/{nameof(ProgramRole.SystemEngineer)}");
        var rejoined = await client.PostAsJsonAsync($"/api/projects/{seeded.ProjectId}/personnel",
            new { userId = seeded.DeputyId, role = nameof(ProgramRole.SystemEngineer) });

        Assert.Equal(HttpStatusCode.NoContent, rejoined.StatusCode);
    }

    /// <summary>
    /// The standing backup, which is the whole point of the feature: authority with no interval, usable
    /// whether or not the holder is away.
    /// </summary>
    [Fact]
    public async Task A_standing_backup_carries_the_holders_authority_with_no_end_date()
    {
        var seeded = await SeedAsync(_host.Factory);
        using var client = _host.CreateClient();
        await SignInAsync(client, seeded.ManagerName);

        using (var before = _host.Factory.Services.CreateScope())
        {
            var identity = before.ServiceProvider.GetRequiredService<IdentityService>();
            Assert.False(await identity.HasRoleAsync(seeded.DeputyId, seeded.ProgramId,
                ProgramRole.SystemEngineeringLead, DateTimeOffset.UtcNow, default));
        }

        var named = await client.PostAsJsonAsync($"/api/projects/{seeded.ProjectId}/personnel/backups",
            new { backupUserId = seeded.DeputyId, role = nameof(ProgramRole.SystemEngineeringLead) });
        Assert.Equal(HttpStatusCode.NoContent, named.StatusCode);

        using var after = _host.Factory.Services.CreateScope();
        var service = after.ServiceProvider.GetRequiredService<IdentityService>();
        Assert.True(await service.HasRoleAsync(seeded.DeputyId, seeded.ProgramId,
            ProgramRole.SystemEngineeringLead, DateTimeOffset.UtcNow, default));
        // Ten years out, because the point of a standing backup is that nothing expires it.
        Assert.True(await service.HasRoleAsync(seeded.DeputyId, seeded.ProgramId,
            ProgramRole.SystemEngineeringLead, DateTimeOffset.UtcNow.AddYears(10), default));
    }

    [Fact]
    public async Task A_backup_has_to_be_on_the_project()
    {
        var seeded = await SeedAsync(_host.Factory);
        using var client = _host.CreateClient();
        await SignInAsync(client, seeded.ManagerName);

        var response = await client.PostAsJsonAsync($"/api/projects/{seeded.ProjectId}/personnel/backups",
            new { backupUserId = seeded.OutsiderId, role = nameof(ProgramRole.SystemEngineeringLead) });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    /// <summary>
    /// Naming the holder as their own cover would report cover that does not exist.
    /// </summary>
    [Fact]
    public async Task The_holder_cannot_be_their_own_backup()
    {
        var seeded = await SeedAsync(_host.Factory);
        using var client = _host.CreateClient();
        await SignInAsync(client, seeded.ManagerName);

        var response = await client.PostAsJsonAsync($"/api/projects/{seeded.ProjectId}/personnel/backups",
            new { backupUserId = seeded.EngineerId, role = nameof(ProgramRole.SystemEngineeringLead) });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    /// <summary>
    /// Leaving the project stands the backup down. The authority was already refused — a backup must be a
    /// current member — but a departed name left standing on the position reports cover nobody is providing.
    /// </summary>
    [Fact]
    public async Task Leaving_the_project_stands_a_backup_down()
    {
        var seeded = await SeedAsync(_host.Factory);
        using var client = _host.CreateClient();
        await SignInAsync(client, seeded.ManagerName);

        await client.PostAsJsonAsync($"/api/projects/{seeded.ProjectId}/personnel/backups",
            new { backupUserId = seeded.DeputyId, role = nameof(ProgramRole.SystemEngineeringLead) });
        await client.DeleteAsync($"/api/projects/{seeded.ProjectId}/personnel/{seeded.DeputyId}/roles/{nameof(ProgramRole.SystemEngineer)}");

        using var scope = _host.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        var backup = await db.ProjectRoleBackups.AsNoTracking().SingleAsync(x => x.BackupUserId == seeded.DeputyId);
        Assert.NotNull(backup.RemovedAt);

        var identity = scope.ServiceProvider.GetRequiredService<IdentityService>();
        Assert.False(await identity.HasRoleAsync(seeded.DeputyId, seeded.ProgramId,
            ProgramRole.SystemEngineeringLead, DateTimeOffset.UtcNow, default));
    }

    /// <summary>
    /// Project leadership staffs its own project but does not mint project administrators. Otherwise the
    /// authority to manage a roster is also the authority to escape being managed.
    /// </summary>
    [Fact]
    public async Task Project_leadership_cannot_grant_the_administrator_role()
    {
        var seeded = await SeedAsync(_host.Factory);
        using var client = _host.CreateClient();
        await SignInAsync(client, seeded.ManagerName);

        var response = await client.PostAsJsonAsync($"/api/projects/{seeded.ProjectId}/personnel",
            new { userId = seeded.DeputyId, role = nameof(ProgramRole.Administrator) });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    private sealed record PersonnelResponse(Guid ProjectId, bool CanManage, PositionRow[] Positions, MemberRow[] Members);
    private sealed record PositionRow(string Role, PersonRow? Holder, PersonRow? Backup);
    private sealed record PersonRow(Guid UserId, string UserName, string DisplayName);
    private sealed record MemberRow(Guid UserId, string UserName, string DisplayName, string[] Roles, string[] BacksUp, bool IsCurrent);
}
