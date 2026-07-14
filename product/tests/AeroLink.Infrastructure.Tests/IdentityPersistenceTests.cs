using AeroLink.Domain.Identity;
using AeroLink.Domain.Programs;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AeroLink.Infrastructure.Tests;

public sealed class IdentityPersistenceTests
{
    [Fact]
    public async Task Password_session_role_and_signature_form_an_accountable_chain()
    {
        var path = Path.Combine(Path.GetTempPath(), $"aerolink-identity-{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<AeroLinkDbContext>().UseSqlite($"Data Source={path};Pooling=False").Options;
        try
        {
            await using var db = new AeroLinkDbContext(options); await db.Database.EnsureCreatedAsync(); var now = DateTimeOffset.UtcNow;
            var program = new ProgramRecord("Identity Program", "IDP"); var user = new UserAccount("reviewer.one", "Reviewer One", "reviewer@example.test", IdentityService.HashPassword("StrongPass!2026"), now);
            db.AddRange(program, user); await db.SaveChangesAsync(); db.ProgramMemberships.Add(new(user.Id, program.Id, ProgramRole.Approver, "admin", now)); await db.SaveChangesAsync();
            var identity = new IdentityService(db); var login = await identity.LoginAsync("REVIEWER.ONE", "StrongPass!2026", "127.0.0.1", "test", now, default);
            Assert.NotNull(login); Assert.True(await identity.HasRoleAsync(login!.User, program.Id, ProgramRole.Approver, now, default));
            var resolved = await identity.ResolveAsync(login.Token, now.AddMinutes(1), default); Assert.Equal("reviewer.one", resolved!.UserName);
            db.ElectronicSignatures.Add(new(user.Id, user.UserName, user.DisplayName, program.Id, "SCR", Guid.NewGuid(), "SCR-00000001.00", "Approve", "Reviewed and approved.", new string('a',64), "127.0.0.1", now)); await db.SaveChangesAsync();
            var signature = await db.ElectronicSignatures.AsNoTracking().SingleAsync(); Assert.Equal("Reviewer One", signature.DisplayName); Assert.Equal(64, signature.ContentHash.Length);
            await identity.LogoutAsync(login.Token, "127.0.0.1", now.AddMinutes(2), default); Assert.Null(await identity.ResolveAsync(login.Token, now.AddMinutes(3), default));
            Assert.Contains(await db.SecurityAuditEvents.AsNoTracking().ToListAsync(), x => x.EventType == "Login" && x.Outcome == "Success");
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Eight_failed_logins_lock_the_account()
    {
        var options = new DbContextOptionsBuilder<AeroLinkDbContext>().UseSqlite("Data Source=:memory:").Options;
        await using var db = new AeroLinkDbContext(options); await db.Database.OpenConnectionAsync(); await db.Database.EnsureCreatedAsync();
        var user = new UserAccount("locked.user", "Locked User", "", IdentityService.HashPassword("StrongPass!2026"), DateTimeOffset.UtcNow); db.Add(user); await db.SaveChangesAsync(); var identity = new IdentityService(db);
        for (var i=0;i<8;i++) Assert.Null(await identity.LoginAsync(user.UserName, "incorrect-password", "local", "test", DateTimeOffset.UtcNow, default));
        Assert.Equal(AccountState.Locked, (await db.UserAccounts.AsNoTracking().SingleAsync()).State);
    }

    [Fact]
    public async Task Program_administrator_remains_scoped_and_role_checks_are_exact()
    {
        var options = new DbContextOptionsBuilder<AeroLinkDbContext>().UseSqlite("Data Source=:memory:").Options;
        await using var db = new AeroLinkDbContext(options); await db.Database.OpenConnectionAsync(); await db.Database.EnsureCreatedAsync();
        var now = DateTimeOffset.UtcNow; var program = new ProgramRecord("Scoped Program", "SCP");
        var scopedAdministrator = new UserAccount("program.admin", "Program Administrator", "program.admin@example.test", IdentityService.HashPassword("StrongPass!2026"), now);
        var systemAdministrator = new UserAccount(IdentityService.SystemAdministratorUserName, "System Administrator", "admin@example.test", IdentityService.HashPassword("StrongPass!2026"), now);
        var delegateUser = new UserAccount("delegate.user", "Delegate User", "delegate@example.test", IdentityService.HashPassword("StrongPass!2026"), now);
        db.AddRange(program, scopedAdministrator, systemAdministrator, delegateUser); await db.SaveChangesAsync();
        db.ProgramMemberships.AddRange(
            new(scopedAdministrator.Id, program.Id, ProgramRole.Administrator, systemAdministrator.UserName, now),
            new(delegateUser.Id, program.Id, ProgramRole.Engineer, systemAdministrator.UserName, now));
        await db.SaveChangesAsync();

        var identity = new IdentityService(db);
        var scopedLogin = await identity.LoginAsync(scopedAdministrator.UserName, "StrongPass!2026", "local", "test", now, default);
        var systemLogin = await identity.LoginAsync(systemAdministrator.UserName, "StrongPass!2026", "local", "test", now, default);
        Assert.NotNull(scopedLogin); Assert.NotNull(systemLogin);
        Assert.False(scopedLogin!.User.IsAdministrator); Assert.True(systemLogin!.User.IsAdministrator);
        Assert.True(await identity.HasRoleAsync(scopedAdministrator.Id, program.Id, ProgramRole.Administrator, now, default));
        Assert.False(await identity.HasRoleAsync(scopedAdministrator.Id, program.Id, ProgramRole.ProgramManager, now, default));

        db.RoleDelegations.Add(new(program.Id, scopedAdministrator.Id, delegateUser.Id, ProgramRole.Administrator, now.AddMinutes(-1), now.AddHours(1), "Temporary scoped administration.", scopedAdministrator.UserName, now));
        await db.SaveChangesAsync();
        Assert.True(await identity.HasRoleAsync(delegateUser.Id, program.Id, ProgramRole.Administrator, now, default));
        Assert.False(await identity.HasRoleAsync(delegateUser.Id, program.Id, ProgramRole.ProgramManager, now, default));
    }
}
