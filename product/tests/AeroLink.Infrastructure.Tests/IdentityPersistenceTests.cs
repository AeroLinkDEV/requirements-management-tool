using AeroLink.Domain.Identity;
using AeroLink.Domain.Programs;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AeroLink.Infrastructure.Tests;

public sealed class IdentityPersistenceTests
{
    [Fact]
    public async Task Demo_identity_leadership_bootstrap_runs_on_sqlite()
    {
        var options = new DbContextOptionsBuilder<AeroLinkDbContext>().UseSqlite("Data Source=:memory:").Options;
        await using var db = new AeroLinkDbContext(options);
        await db.Database.OpenConnectionAsync();
        await db.Database.EnsureCreatedAsync();
        var program = new ProgramRecord("Seeded Program", "SEED");
        db.Programs.Add(program);
        await db.SaveChangesAsync();

        await new IdentitySeeder(db).EnsureSeededAsync();

        var assignments = await db.ProjectLeadershipAssignments.AsNoTracking()
            .Where(x => x.ProgramId == program.Id).ToListAsync();
        Assert.Equal(
            [
                ProjectLeadershipPosition.ProjectEngineer,
                ProjectLeadershipPosition.ProgramManager,
                ProjectLeadershipPosition.EngineeringManager,
                ProjectLeadershipPosition.ConfigurationManager,
                ProjectLeadershipPosition.SystemEngineeringLead,
                ProjectLeadershipPosition.SoftwareEngineeringLead,
            ],
            assignments.Select(x => x.Position).Order().ToArray());
        var programManager = assignments
            .Single(x => x.Position == ProjectLeadershipPosition.ProgramManager);
        var holder = await db.UserAccounts.AsNoTracking().SingleAsync(x => x.Id == programManager.HolderUserId);
        Assert.Equal("engineering.manager", holder.UserName);

        await new IdentitySeeder(db).EnsureSeededAsync();

        var reseededAssignments = await db.ProjectLeadershipAssignments.AsNoTracking()
            .Where(x => x.ProgramId == program.Id).ToListAsync();
        Assert.Equal(assignments.Count, reseededAssignments.Count);
        Assert.Equal(
            assignments.OrderBy(x => x.Position).Select(x => (x.Position, x.HolderUserId)),
            reseededAssignments.OrderBy(x => x.Position).Select(x => (x.Position, x.HolderUserId)));
    }

    [Fact]
    public async Task Demo_identity_leadership_bootstrap_never_assigns_a_disabled_account()
    {
        var options = new DbContextOptionsBuilder<AeroLinkDbContext>().UseSqlite("Data Source=:memory:").Options;
        await using var db = new AeroLinkDbContext(options);
        await db.Database.OpenConnectionAsync();
        await db.Database.EnsureCreatedAsync();
        var now = DateTimeOffset.UtcNow;
        var program = new ProgramRecord("Disabled Seed Holder Program", "DSH");
        var disabled = new UserAccount("engineering.manager", "Engineering Manager",
            "engineering.manager@aerolink.local", IdentityService.HashPassword(IdentitySeeder.DemoPassword), now);
        disabled.Disable(now);
        db.AddRange(program, disabled);
        await db.SaveChangesAsync();

        await new IdentitySeeder(db).EnsureSeededAsync();

        var assignments = await db.ProjectLeadershipAssignments.AsNoTracking()
            .Where(x => x.ProgramId == program.Id).ToListAsync();
        Assert.DoesNotContain(assignments, x => x.HolderUserId == disabled.Id);
        var activeHolders = (await db.UserAccounts.AsNoTracking()
            .Where(x => x.State == AccountState.Active)
            .Select(x => x.Id).ToListAsync()).ToHashSet();
        Assert.All(assignments, x => Assert.Contains(x.HolderUserId, activeHolders));
    }

    [Fact]
    public void Mfa_secrets_are_standard_base32_and_totp_matches_rfc_6238()
    {
        var generated = IdentityService.CreateMfaSecret();
        Assert.Equal(32, generated.Length);
        Assert.All(generated, value => Assert.Contains(value, "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567"));
        Assert.True(IdentityService.VerifyTotp("GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ", "287082", DateTimeOffset.FromUnixTimeSeconds(59)));
        Assert.False(IdentityService.VerifyTotp("GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ", "287083", DateTimeOffset.FromUnixTimeSeconds(59)));
    }

    [Fact]
    public async Task Mfa_secret_is_protected_at_rest_and_can_be_used_after_reload()
    {
        var options = new DbContextOptionsBuilder<AeroLinkDbContext>().UseSqlite("Data Source=:memory:").Options;
        await using var db = new AeroLinkDbContext(options); await db.Database.OpenConnectionAsync(); await db.Database.EnsureCreatedAsync();
        var identity = new IdentityService(db); var secret = IdentityService.CreateMfaSecret(); var protectedSecret = identity.ProtectMfaSecret(secret);
        Assert.StartsWith("dp:v1:", protectedSecret); Assert.DoesNotContain(secret, protectedSecret);
        Assert.Equal(secret, identity.RevealMfaSecret(protectedSecret));
    }

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
            db.ElectronicSignatures.Add(new(user.Id, user.UserName, user.DisplayName, program.Id, "SCR", Guid.NewGuid(), "SRCR-00001.00", "Approve", "Reviewed and approved.", new string('a',64), "127.0.0.1", now)); await db.SaveChangesAsync();
            var signature = await db.ElectronicSignatures.AsNoTracking().SingleAsync(); Assert.Equal("Reviewer One", signature.DisplayName); Assert.Equal(64, signature.ContentHash.Length); Assert.Equal("", signature.Authority);
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
