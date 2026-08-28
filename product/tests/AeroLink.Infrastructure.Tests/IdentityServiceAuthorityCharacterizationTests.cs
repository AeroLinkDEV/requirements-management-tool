using AeroLink.Domain.Identity;
using AeroLink.Domain.Programs;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AeroLink.Infrastructure.Tests;

/// <summary>
/// Characterizes the two live `IdentityService.HasRoleAsync` paths — the session overload and the userId
/// overload — so that the #816 Slice 2 authority-model change surfaces as a deliberate delta at this seam
/// rather than as a divergence that happens to pass on one path and not the other.
///
/// The code itself carries the warning that the two copies of the check are both reached from live
/// authorization paths and "a rule applied to only one of them is a rule that holds by luck". These tests
/// hold them to the same contract for the mechanisms the Project Leadership work will move: satisfying
/// memberships, standing backups, exact-role delegations and ended memberships.
/// </summary>
public sealed class IdentityServiceAuthorityCharacterizationTests : IDisposable
{
    private const string Password = "StrongPass!2026";
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"aerolink-authchar-{Guid.NewGuid():N}.db");
    private readonly AeroLinkDbContext _db;
    private readonly IdentityService _identity;
    private readonly DateTimeOffset _now = DateTimeOffset.UtcNow;

    private readonly ProgramRecord _program;
    private readonly UserAccount _systemEngineer;
    private readonly UserAccount _lead;
    private readonly UserAccount _backup;
    private readonly UserAccount _outsider;

    public IdentityServiceAuthorityCharacterizationTests()
    {
        var options = new DbContextOptionsBuilder<AeroLinkDbContext>().UseSqlite($"Data Source={_path};Pooling=False").Options;
        _db = new AeroLinkDbContext(options);
        _db.Database.OpenConnection();
        _db.Database.EnsureCreated();
        _identity = new IdentityService(_db);

        _program = new ProgramRecord("Authority Characterization", $"AC{Guid.NewGuid():N}"[..12]);
        _systemEngineer = NewAccount("char.system.engineer");
        _lead = NewAccount("char.system.lead");
        _backup = NewAccount("char.backup");
        _outsider = NewAccount("char.outsider");
        _db.AddRange(_program, _systemEngineer, _lead, _backup, _outsider);
        _db.SaveChanges();
        // The backup deliberately holds only the discipline membership: the strongest form of the backup
        // rule, where the backup carries a lead's authority with no lead membership of their own.
        _db.AddRange(
            new ProgramMembership(_systemEngineer.Id, _program.Id, ProgramRole.SystemEngineer, "admin", _now),
            new ProgramMembership(_lead.Id, _program.Id, ProgramRole.SystemEngineeringLead, "admin", _now),
            new ProgramMembership(_backup.Id, _program.Id, ProgramRole.SystemEngineer, "admin", _now));
        _db.SaveChanges();
    }

    private static UserAccount NewAccount(string name) =>
        new(name, $"Characterization {name}", $"{name}@example.test", IdentityService.HashPassword(Password), DateTimeOffset.UtcNow);

    /// <summary>Logs the account in and returns the live session user the way a real request would carry.</summary>
    private async Task<(AuthenticatedUser User, string Token)> SessionForAsync(UserAccount account)
    {
        var login = await _identity.LoginAsync(account.UserName, Password, "127.0.0.1", "test", _now, default);
        Assert.NotNull(login);
        return (login!.User, login.Token);
    }

    [Fact]
    public async Task Both_paths_accept_a_satisfying_membership()
    {
        var (user, _) = await SessionForAsync(_systemEngineer);
        Assert.True(await _identity.HasRoleAsync(user, _program.Id, ProgramRole.Engineer, _now, default));
        Assert.True(await _identity.HasRoleAsync(_systemEngineer.Id, _program.Id, ProgramRole.Engineer, _now, default));
    }

    [Fact]
    public async Task A_legacy_position_keyed_backup_confers_nothing_on_both_paths()
    {
        // The backup is named for the lead role while holding only the discipline membership.
        _db.ProjectRoleBackups.Add(new ProjectRoleBackup(_program.Id, ProgramRole.SystemEngineeringLead, _backup.Id, "admin", _now));
        await _db.SaveChangesAsync();

        var (backupUser, _) = await SessionForAsync(_backup);
        Assert.False(await _identity.HasRoleAsync(backupUser, _program.Id, ProgramRole.SystemEngineeringLead, _now, default));
        Assert.False(await _identity.HasRoleAsync(_backup.Id, _program.Id, ProgramRole.SystemEngineeringLead, _now, default));
    }

    [Fact]
    public async Task A_backup_without_project_membership_fails_closed_on_both_paths()
    {
        var unmembered = NewAccount("char.unmembered.backup");
        _db.Add(unmembered); await _db.SaveChangesAsync();
        _db.ProjectRoleBackups.Add(new ProjectRoleBackup(_program.Id, ProgramRole.SystemEngineeringLead, unmembered.Id, "admin", _now));
        await _db.SaveChangesAsync();

        var (unmemberedUser, _) = await SessionForAsync(unmembered);
        Assert.False(await _identity.HasRoleAsync(unmemberedUser, _program.Id, ProgramRole.SystemEngineeringLead, _now, default));
        Assert.False(await _identity.HasRoleAsync(unmembered.Id, _program.Id, ProgramRole.SystemEngineeringLead, _now, default));
    }

    /// <summary>
    /// A delegation is an exact-role grant on both paths, and — unlike a standing backup — it does not
    /// even require the delegatee to be a current member. A ProjectEngineeringLead delegation therefore
    /// satisfies a ProjectEngineeringLead demand and nothing else: the satisfying set of the delegated
    /// role does not travel with the delegation, so it does not carry the engineer's authority either.
    /// The #816 migration moves this seam; the pin records which way it behaved before the move.
    /// </summary>
    [Fact]
    public async Task A_delegation_is_an_exact_role_grant_on_both_paths()
    {
        var delegatee = _outsider; // no memberships at all: nothing but the delegation can answer
        _db.RoleDelegations.Add(new RoleDelegation(_program.Id, _lead.Id, delegatee.Id,
            ProgramRole.ProjectEngineeringLead, _now.AddMinutes(-1), _now.AddDays(1), "Lead away.", "admin", _now));
        await _db.SaveChangesAsync();

        var (delegateeUser, _) = await SessionForAsync(delegatee);
        Assert.True(await _identity.HasRoleAsync(delegateeUser, _program.Id, ProgramRole.ProjectEngineeringLead, _now, default));
        Assert.True(await _identity.HasRoleAsync(delegatee.Id, _program.Id, ProgramRole.ProjectEngineeringLead, _now, default));
        Assert.False(await _identity.HasRoleAsync(delegateeUser, _program.Id, ProgramRole.Engineer, _now, default));
        Assert.False(await _identity.HasRoleAsync(delegatee.Id, _program.Id, ProgramRole.Engineer, _now, default));
    }

    [Fact]
    public async Task An_ended_membership_removes_authority_on_both_paths()
    {
        var membership = await _db.ProgramMemberships.SingleAsync(x => x.UserId == _systemEngineer.Id, default);
        membership.End("admin", _now);
        await _db.SaveChangesAsync();

        var (user, _) = await SessionForAsync(_systemEngineer);
        Assert.False(await _identity.HasRoleAsync(user, _program.Id, ProgramRole.Engineer, _now, default));
        Assert.False(await _identity.HasRoleAsync(_systemEngineer.Id, _program.Id, ProgramRole.Engineer, _now, default));
    }

    [Fact]
    public async Task A_disabled_account_fails_the_userId_path_and_its_session_stops_resolving()
    {
        var (user, token) = await SessionForAsync(_systemEngineer);
        var account = await _db.UserAccounts.SingleAsync(x => x.Id == _systemEngineer.Id, default);
        account.Disable(_now);
        await _db.SaveChangesAsync();

        Assert.False(await _identity.HasRoleAsync(_systemEngineer.Id, _program.Id, ProgramRole.Engineer, _now, default));
        Assert.Null(await _identity.ResolveAsync(token, _now, default));
    }

    public void Dispose()
    {
        _db.Database.CloseConnection();
        _db.Dispose();
        try { File.Delete(_path); } catch { /* temp cleanup best effort */ }
    }
}
