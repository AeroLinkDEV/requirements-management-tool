using AeroLink.Domain.Identity;
using AeroLink.Domain.Programs;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace AeroLink.Infrastructure.Tests;

/// <summary>
/// PostgreSQL qualification for the #816 Slice 2 authority migration. API tests run on SQLite with
/// EnsureCreated and prove nothing about migration SQL or provider behavior, so the upgrade contract is
/// proven here against a disposable PostgreSQL database:
///
/// - the full forward migration chain applies to a clean install;
/// - the migration authority backfills the eight leadership positions from legacy singular memberships,
///   deriving the base roles the named rules require;
/// - conflicting active Project Engineer / Project Engineering Lead memberships held by different people
///   fail closed with a report instead of choosing a winner;
/// - eligible role-keyed backups migrate to the position;
/// - a second run is a no-op (idempotent).
///
/// Every test is skipped unless AEROLINK_MIGRATIONS_CONNECTION points at a disposable PostgreSQL server;
/// the disposable database is created and dropped per run, and the persistent developer database is never
/// touched.
/// </summary>
public sealed class ProjectLeadershipMigrationQualificationTests
{
    private const string ConnectionVariable = "AEROLINK_MIGRATIONS_CONNECTION";

    private static bool ServerConfigured(out string serverConnectionString)
    {
        var raw = Environment.GetEnvironmentVariable(ConnectionVariable);
        serverConnectionString = raw ?? "";
        return !string.IsNullOrWhiteSpace(serverConnectionString);
    }

    private static async Task<string> CreateDisposableDatabaseAsync(string serverConnectionString)
    {
        var database = $"aerolink_816_qual_{Guid.NewGuid():N}";
        await using var admin = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(serverConnectionString)
        { Database = "postgres" }.ConnectionString);
        await admin.OpenAsync();
        await using var command = admin.CreateCommand();
        command.CommandText = $"CREATE DATABASE \"{database}\"";
        await command.ExecuteNonQueryAsync();
        return new NpgsqlConnectionStringBuilder(serverConnectionString) { Database = database }.ConnectionString;
    }

    private static async Task DropDatabaseAsync(string serverConnectionString, string database)
    {
        await using var admin = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(serverConnectionString)
        { Database = "postgres" }.ConnectionString);
        await admin.OpenAsync();
        await using var command = admin.CreateCommand();
        command.CommandText = $"DROP DATABASE IF EXISTS \"{database}\" WITH (FORCE)";
        await command.ExecuteNonQueryAsync();
    }

    private static async Task MigrateAsync(string connectionString, string? stopBefore = null)
    {
        var options = new DbContextOptionsBuilder<AeroLinkDbContext>().UseNpgsql(connectionString).Options;
        await using var db = new AeroLinkDbContext(options);
        if (stopBefore is null) { await db.Database.MigrateAsync(); return; }
        await db.Database.MigrateAsync(stopBefore);
    }

    [Fact]
    public async Task The_upgrade_backfills_leadership_from_legacy_memberships_and_is_idempotent()
    {
        if (!ServerConfigured(out var server)) return; // qualification requires the disposable server
        var serverDatabase = new NpgsqlConnectionStringBuilder(server).Database;
        var connection = await CreateDisposableDatabaseAsync(server);
        try
        {
            // Forward migration over the full chain on a clean install.
            await MigrateAsync(connection);

            var options = new DbContextOptionsBuilder<AeroLinkDbContext>().UseNpgsql(connection).Options;
            await using var db = new AeroLinkDbContext(options);
            var now = DateTimeOffset.UtcNow;
            var program = new ProgramRecord("Leadership Qualification", $"LPQ{Guid.NewGuid():N}"[..12]);
            UserAccount Account(string name) => new(name, name, $"{name}@example.test",
                IdentityService.HashPassword("StrongPass!2026"), now);
            var lead = Account("qual.system.lead");
            var softwareLead = Account("qual.software.lead");
            var configurationManager = Account("qual.cm");
            var backup = Account("qual.backup");
            var retiredProjectLead = Account("qual.project.lead");
            db.AddRange(program, lead, softwareLead, configurationManager, backup, retiredProjectLead);
            // Legacy memberships, deliberately incomplete: the discipline leads lack the base roles their
            // positions require, which the migration derives; the retiring PEL has no Project Engineer
            // co-holder, so the one-directional derivation applies.
            db.AddRange(
                new ProgramMembership(lead.Id, program.Id, ProgramRole.SystemEngineeringLead, "legacy", now),
                new ProgramMembership(softwareLead.Id, program.Id, ProgramRole.SoftwareEngineeringLead, "legacy", now),
                new ProgramMembership(configurationManager.Id, program.Id, ProgramRole.ConfigurationManager, "legacy", now),
                new ProgramMembership(retiredProjectLead.Id, program.Id, ProgramRole.ProjectEngineeringLead, "legacy", now),
                // The named backup holds the base role, so their role-keyed backup migrates to the position.
                new ProgramMembership(backup.Id, program.Id, ProgramRole.SystemEngineer, "legacy", now),
                new ProjectRoleBackup(program.Id, ProgramRole.SystemEngineeringLead, backup.Id, "legacy", now));
            await db.SaveChangesAsync();

            await using (var fresh = new AeroLinkDbContext(options))
            {
                var authority = new ProjectLeadershipMigrationAuthority(fresh);
                await authority.EnsureCompletedAsync();
                await authority.EnsureCompletedAsync(); // second run: idempotent no-op
            }

            var assignments = await db.ProjectLeadershipAssignments.AsNoTracking()
                .Where(x => x.ProgramId == program.Id).ToListAsync();
            Assert.Equal(4, assignments.Count);
            Assert.All(assignments, x => Assert.Equal("aerolink-migration", x.AssignedBy));
            Assert.Contains(assignments, x => x.Position == ProjectLeadershipPosition.SystemEngineeringLead && x.HolderUserId == lead.Id);
            Assert.Contains(assignments, x => x.Position == ProjectLeadershipPosition.SoftwareEngineeringLead && x.HolderUserId == softwareLead.Id);
            Assert.Contains(assignments, x => x.Position == ProjectLeadershipPosition.ConfigurationManager && x.HolderUserId == configurationManager.Id);
            Assert.Contains(assignments, x => x.Position == ProjectLeadershipPosition.ProjectEngineer && x.HolderUserId == retiredProjectLead.Id);
            // The derived base roles the named migration rules require.
            Assert.Equal(2, await db.ProgramMemberships.CountAsync(x => x.ProgramId == program.Id
                && x.Role == ProgramRole.SystemEngineer && x.EndedAt == null));
            Assert.Equal(1, await db.ProgramMemberships.CountAsync(x => x.ProgramId == program.Id
                && x.Role == ProgramRole.ProjectEngineer && x.EndedAt == null && x.GrantedBy == "aerolink-migration"));
            // The eligible lead-role backup migrated to the position.
            Assert.Equal(1, await db.ProjectLeadershipBackups.CountAsync(x => x.ProgramId == program.Id
                && x.Position == ProjectLeadershipPosition.SystemEngineeringLead && x.RemovedAt == null));

            // The migrated authority is live: the backfilled holder answers the retired role's demands.
            await using (var fresh = new AeroLinkDbContext(options))
            {
                var identity = new IdentityService(fresh);
                Assert.True(await identity.HasRoleAsync(retiredProjectLead.Id, program.Id,
                    ProgramRole.ProjectEngineeringLead, now, default));
                Assert.True(await identity.HasRoleAsync(retiredProjectLead.Id, program.Id, ProgramRole.Approver, now, default));
            }
        }
        finally
        {
            var database = new NpgsqlConnectionStringBuilder(connection).Database;
            if (database != serverDatabase) await DropDatabaseAsync(server, database);
        }
    }

    [Fact]
    public async Task Conflicting_project_engineer_and_project_engineering_lead_holders_fail_closed()
    {
        if (!ServerConfigured(out var server)) return; // qualification requires the disposable server
        var serverDatabase = new NpgsqlConnectionStringBuilder(server).Database;
        var connection = await CreateDisposableDatabaseAsync(server);
        try
        {
            await MigrateAsync(connection);
            var options = new DbContextOptionsBuilder<AeroLinkDbContext>().UseNpgsql(connection).Options;
            await using var db = new AeroLinkDbContext(options);
            var now = DateTimeOffset.UtcNow;
            var program = new ProgramRecord("Conflicting Authority", $"LPC{Guid.NewGuid():N}"[..12]);
            UserAccount Account(string name) => new(name, name, $"{name}@example.test",
                IdentityService.HashPassword("StrongPass!2026"), now);
            var projectEngineer = Account("qual.pe.holder");
            var engineeringLead = Account("qual.pel.holder");
            db.AddRange(program, projectEngineer, engineeringLead);
            db.AddRange(
                new ProgramMembership(projectEngineer.Id, program.Id, ProgramRole.ProjectEngineer, "legacy", now),
                new ProgramMembership(engineeringLead.Id, program.Id, ProgramRole.ProjectEngineeringLead, "legacy", now));
            await db.SaveChangesAsync();

            // Two different people hold the accountability the retired role and the base role both claim.
            // The upgrade refuses with no partial state rather than picking a winner.
            await using (var fresh = new AeroLinkDbContext(options))
            {
                var authority = new ProjectLeadershipMigrationAuthority(fresh);
                await Assert.ThrowsAsync<InvalidOperationException>(() => authority.EnsureCompletedAsync());
            }
            Assert.Empty(await db.ProjectLeadershipAssignments.AsNoTracking().ToListAsync());
        }
        finally
        {
            var database = new NpgsqlConnectionStringBuilder(connection).Database;
            if (database != serverDatabase) await DropDatabaseAsync(server, database);
        }
    }
}
