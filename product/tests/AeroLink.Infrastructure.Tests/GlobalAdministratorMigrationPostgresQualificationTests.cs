using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace AeroLink.Infrastructure.Tests;

/// <summary>
/// #1639 upgrade path on PostgreSQL. Global administration used to be the account named <c>admin</c>; an
/// existing installation must come out of the migration with that account still an administrator, attributed
/// to the migration and recorded in the security audit, and with nobody else promoted by it.
/// </summary>
[Trait("Category", "PostgresQualification")]
public sealed class GlobalAdministratorMigrationPostgresQualificationTests
{
    private const string DatabaseName = "aerolink_1639_qualify";
    private const string PreGrantMigration = "20261005112545_NativeNotificationProcessIdentity";

    [DisposablePostgresFact]
    public async Task Upgrade_makes_the_existing_admin_account_an_attributed_global_administrator_and_nobody_else()
    {
        await using var database = await DisposablePostgresDatabase.CreateAsync(DatabaseName);
        var options = new DbContextOptionsBuilder<AeroLinkDbContext>().UseNpgsql(database.ConnectionString).Options;
        var adminId = Guid.NewGuid();
        var engineerId = Guid.NewGuid();
        await using (var pre = new AeroLinkDbContext(options))
        {
            await pre.Database.GetService<IMigrator>().MigrateAsync(PreGrantMigration);
            // Written as SQL: the model already carries the grant columns this schema does not have yet.
            foreach (var (id, userName) in new[] { (adminId, "admin"), (engineerId, "existing.engineer") })
                await pre.Database.ExecuteSqlRawAsync(
                    "INSERT INTO user_accounts (\"Id\",\"UserName\",\"DisplayName\",\"Email\",\"PasswordHash\",\"State\","
                    + "\"MustChangePassword\",\"FailedLoginCount\",\"CreatedAt\") "
                    + $"VALUES ('{id}','{userName}','{userName}','{userName}@example.test','hash','Active',false,0,now());");
        }

        await using var db = new AeroLinkDbContext(options);
        await db.Database.MigrateAsync();

        var admin = await db.UserAccounts.AsNoTracking().SingleAsync(x => x.Id == adminId);
        Assert.True(admin.IsGlobalAdministrator);
        Assert.True(admin.IsBuiltInAdministrator);
        Assert.Equal("system.migration", admin.GlobalAdministratorGrantedBy);
        Assert.NotNull(admin.GlobalAdministratorGrantedAt);
        var engineer = await db.UserAccounts.AsNoTracking().SingleAsync(x => x.Id == engineerId);
        Assert.False(engineer.IsGlobalAdministrator);
        Assert.False(engineer.IsBuiltInAdministrator);
        var audit = await db.SecurityAuditEvents.AsNoTracking()
            .Where(x => x.EventType == "GlobalAdministratorGranted").ToListAsync();
        Assert.Equal(("system.migration", "admin"), (Assert.Single(audit).ActorId, audit[0].Target));
    }
}
