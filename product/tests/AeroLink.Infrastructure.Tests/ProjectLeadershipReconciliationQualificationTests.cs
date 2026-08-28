using AeroLink.Domain.Identity;
using AeroLink.Domain.Programs;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace AeroLink.Infrastructure.Tests;

/// <summary>
/// PostgreSQL qualification for the v2 reconciliation that retires the legacy authority rows v1 left alive.
///
/// v1 copied legacy lead memberships and role backups into the Project Leadership tables and left the
/// originals active, so a replaced leader kept answering Reviewer/Approver through the old membership and a
/// removed backup kept signing through the old designation. These prove the repair, and prove the two
/// properties that make it safe to run against a live installation: it refuses rather than guessing, and it
/// writes nothing at all when it refuses.
///
/// Skipped unless AEROLINK_MIGRATIONS_CONNECTION points at a disposable PostgreSQL server. The disposable
/// database is created and dropped per test; the persistent developer database on 54329 is never touched.
/// </summary>
public sealed class ProjectLeadershipReconciliationQualificationTests
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
        var database = $"aerolink_816_v2_{Guid.NewGuid():N}";
        await using var admin = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(serverConnectionString)
        { Database = "postgres" }.ConnectionString);
        await admin.OpenAsync();
        await using var command = admin.CreateCommand();
        command.CommandText = $"CREATE DATABASE \"{database}\"";
        await command.ExecuteNonQueryAsync();
        return new NpgsqlConnectionStringBuilder(serverConnectionString) { Database = database }.ConnectionString;
    }

    private static async Task DropDatabaseAsync(string serverConnectionString, string? database)
    {
        if (string.IsNullOrWhiteSpace(database)) return;
        await using var admin = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(serverConnectionString)
        { Database = "postgres" }.ConnectionString);
        await admin.OpenAsync();
        await using var command = admin.CreateCommand();
        command.CommandText = $"DROP DATABASE IF EXISTS \"{database}\" WITH (FORCE)";
        await command.ExecuteNonQueryAsync();
    }

    private static DbContextOptions<AeroLinkDbContext> Options(string connectionString) =>
        new DbContextOptionsBuilder<AeroLinkDbContext>().UseNpgsql(connectionString).Options;

    private static UserAccount Account(string name, DateTimeOffset now) =>
        new(name, name, $"{name}@example.test", IdentityService.HashPassword("StrongPass!2026"), now);

    /// <summary>
    /// A database that already ran v1: assignments exist, and so do the legacy rows beside them. After v2 the
    /// legacy position memberships are ended, the migrated role backup is removed, and the base eligibility
    /// memberships that keep the assignments valid are untouched.
    /// </summary>
    [Fact]
    public async Task V2_retires_the_legacy_rows_v1_left_and_preserves_base_eligibility()
    {
        if (!ServerConfigured(out var server)) return;
        string? database = null;
        var connection = await CreateDisposableDatabaseAsync(server);
        try
        {
            database = new NpgsqlConnectionStringBuilder(connection).Database;
            await using (var migrate = new AeroLinkDbContext(Options(connection))) await migrate.Database.MigrateAsync();

            var now = DateTimeOffset.UtcNow;
            var program = new ProgramRecord("V2 Reconciliation", $"V2R{Guid.NewGuid():N}"[..12]);
            var lead = Account("v2.system.lead", now);
            var backup = Account("v2.backup", now);
            var manager = Account("v2.program.manager", now);
            await using (var seed = new AeroLinkDbContext(Options(connection)))
            {
                seed.AddRange(program, lead, backup, manager);
                seed.AddRange(
                    new ProgramMembership(lead.Id, program.Id, ProgramRole.SystemEngineeringLead, "legacy", now),
                    new ProgramMembership(lead.Id, program.Id, ProgramRole.SystemEngineer, "legacy", now),
                    new ProgramMembership(backup.Id, program.Id, ProgramRole.SystemEngineer, "legacy", now),
                    new ProgramMembership(manager.Id, program.Id, ProgramRole.ProgramManager, "legacy", now),
                    new ProjectRoleBackup(program.Id, ProgramRole.SystemEngineeringLead, backup.Id, "legacy", now));
                await seed.SaveChangesAsync();
            }

            // v1 as it shipped: creates the new rows, leaves the old ones alive.
            await using (var v1 = new AeroLinkDbContext(Options(connection)))
                await new ProjectLeadershipMigrationAuthority(v1).EnsureCompletedAsync();

            await using (var v2 = new AeroLinkDbContext(Options(connection)))
            {
                await new ProjectLeadershipReconciliationAuthority(v2).EnsureCompletedAsync();
                await new ProjectLeadershipReconciliationAuthority(v2).EnsureCompletedAsync(); // idempotent
            }

            await using var check = new AeroLinkDbContext(Options(connection));
            // The legacy position membership is ended — retained as history, no longer granting.
            var legacyLead = await check.ProgramMemberships.AsNoTracking().SingleAsync(
                x => x.ProgramId == program.Id && x.UserId == lead.Id && x.Role == ProgramRole.SystemEngineeringLead);
            Assert.NotNull(legacyLead.EndedAt);

            // The base eligibility memberships survive: ending them would revoke the very authority the
            // assignment depends on.
            Assert.True(await check.ProgramMemberships.AsNoTracking().AnyAsync(
                x => x.ProgramId == program.Id && x.UserId == lead.Id
                     && x.Role == ProgramRole.SystemEngineer && x.EndedAt == null));
            Assert.True(await check.ProgramMemberships.AsNoTracking().AnyAsync(
                x => x.ProgramId == program.Id && x.UserId == manager.Id
                     && x.Role == ProgramRole.ProgramManager && x.EndedAt == null));

            // The migrated legacy backup is removed; the leadership backup is the live designation.
            Assert.False(await check.ProjectRoleBackups.AsNoTracking().AnyAsync(
                x => x.ProgramId == program.Id && x.Role == ProgramRole.SystemEngineeringLead && x.RemovedAt == null));
            Assert.True(await check.ProjectLeadershipBackups.AsNoTracking().AnyAsync(
                x => x.ProgramId == program.Id && x.Position == ProjectLeadershipPosition.SystemEngineeringLead
                     && x.BackupUserId == backup.Id && x.RemovedAt == null));

            // Exactly one completion marker after two runs.
            Assert.Equal(1, await check.SecurityAuditEvents.AsNoTracking().CountAsync(
                x => x.EventType == ProjectLeadershipReconciliationAuthority.MigrationMarker + ".Completed"));
        }
        finally { await DropDatabaseAsync(server, database); }
    }

    /// <summary>
    /// The defect the repair exists to close: after v2 a replaced leader must lose the authority, where
    /// before it survived in the legacy membership the API could not see.
    /// </summary>
    [Fact]
    public async Task After_v2_a_replaced_leader_no_longer_answers_the_demand()
    {
        if (!ServerConfigured(out var server)) return;
        string? database = null;
        var connection = await CreateDisposableDatabaseAsync(server);
        try
        {
            database = new NpgsqlConnectionStringBuilder(connection).Database;
            await using (var migrate = new AeroLinkDbContext(Options(connection))) await migrate.Database.MigrateAsync();

            var now = DateTimeOffset.UtcNow;
            var program = new ProgramRecord("V2 Replacement", $"V2P{Guid.NewGuid():N}"[..12]);
            var outgoing = Account("v2.outgoing", now);
            var incoming = Account("v2.incoming", now);
            await using (var seed = new AeroLinkDbContext(Options(connection)))
            {
                seed.AddRange(program, outgoing, incoming);
                seed.AddRange(
                    new ProgramMembership(outgoing.Id, program.Id, ProgramRole.SystemEngineeringLead, "legacy", now),
                    new ProgramMembership(outgoing.Id, program.Id, ProgramRole.SystemEngineer, "legacy", now),
                    new ProgramMembership(incoming.Id, program.Id, ProgramRole.SystemEngineer, "legacy", now));
                await seed.SaveChangesAsync();
            }

            await using (var v1 = new AeroLinkDbContext(Options(connection)))
                await new ProjectLeadershipMigrationAuthority(v1).EnsureCompletedAsync();
            await using (var v2 = new AeroLinkDbContext(Options(connection)))
                await new ProjectLeadershipReconciliationAuthority(v2).EnsureCompletedAsync();

            // Replace the leader through the model.
            await using (var replace = new AeroLinkDbContext(Options(connection)))
            {
                var assignment = await replace.ProjectLeadershipAssignments.SingleAsync(
                    x => x.ProgramId == program.Id && x.Position == ProjectLeadershipPosition.SystemEngineeringLead
                         && x.EndedAt == null);
                var later = DateTimeOffset.UtcNow;
                assignment.End("operator", later);
                replace.ProjectLeadershipAssignments.Add(new ProjectLeadershipAssignment(
                    program.Id, ProjectLeadershipPosition.SystemEngineeringLead, incoming.Id, "operator", later));
                await replace.SaveChangesAsync();
            }

            await using var check = new AeroLinkDbContext(Options(connection));
            var identity = new IdentityService(check);
            Assert.False(await identity.HasRoleAsync(outgoing.Id, program.Id, ProgramRole.Reviewer, now, default));
            Assert.True(await identity.HasRoleAsync(incoming.Id, program.Id, ProgramRole.Reviewer, now, default));
        }
        finally { await DropDatabaseAsync(server, database); }
    }

    /// <summary>
    /// Two programs, the second in conflict. The refusal must leave the FIRST program untouched — v1's
    /// per-program SaveChanges is exactly what made that untrue, and one program in the fixture could never
    /// have exposed it.
    /// </summary>
    [Fact]
    public async Task A_conflict_in_a_later_program_leaves_no_partial_repair_in_an_earlier_one()
    {
        if (!ServerConfigured(out var server)) return;
        string? database = null;
        var connection = await CreateDisposableDatabaseAsync(server);
        try
        {
            database = new NpgsqlConnectionStringBuilder(connection).Database;
            await using (var migrate = new AeroLinkDbContext(Options(connection))) await migrate.Database.MigrateAsync();

            var now = DateTimeOffset.UtcNow;
            var repairable = new ProgramRecord("V2 Repairable", $"V2A{Guid.NewGuid():N}"[..12]);
            var conflicted = new ProgramRecord("V2 Conflicted", $"V2B{Guid.NewGuid():N}"[..12]);
            var goodLead = Account("v2.good.lead", now);
            var orphanLead = Account("v2.orphan.lead", now);
            await using (var seed = new AeroLinkDbContext(Options(connection)))
            {
                seed.AddRange(repairable, conflicted, goodLead, orphanLead);
                seed.AddRange(
                    new ProgramMembership(goodLead.Id, repairable.Id, ProgramRole.SystemEngineeringLead, "legacy", now),
                    new ProgramMembership(goodLead.Id, repairable.Id, ProgramRole.SystemEngineer, "legacy", now),
                    // The conflict: a legacy position membership with no assignment to take over from it.
                    // v1 is not run for this program, so nothing was ever created for it.
                    new ProgramMembership(orphanLead.Id, conflicted.Id, ProgramRole.SoftwareEngineeringLead, "legacy", now));
                await seed.SaveChangesAsync();
            }

            // v1 for the repairable program only, so the conflicted one keeps its orphan membership.
            await using (var v1 = new AeroLinkDbContext(Options(connection)))
            {
                var authority = new ProjectLeadershipMigrationAuthority(v1);
                // Backfill would also fix the conflicted program, so create the repairable program's
                // assignment directly — the point here is v2's behaviour, not v1's.
                v1.ProjectLeadershipAssignments.Add(new ProjectLeadershipAssignment(
                    repairable.Id, ProjectLeadershipPosition.SystemEngineeringLead, goodLead.Id, "aerolink-migration", now));
                await v1.SaveChangesAsync();
                _ = authority;
            }

            await using (var v2 = new AeroLinkDbContext(Options(connection)))
            {
                var failure = await Assert.ThrowsAsync<InvalidOperationException>(
                    () => new ProjectLeadershipReconciliationAuthority(v2).EnsureCompletedAsync());
                Assert.Contains("Conflicting legacy Project Leadership authority", failure.Message);
                Assert.Contains("V2 Conflicted", failure.Message);
            }

            await using var check = new AeroLinkDbContext(Options(connection));
            // Zero committed writes for the repairable program: its legacy membership is still active.
            var untouched = await check.ProgramMemberships.AsNoTracking().SingleAsync(
                x => x.ProgramId == repairable.Id && x.UserId == goodLead.Id
                     && x.Role == ProgramRole.SystemEngineeringLead);
            Assert.Null(untouched.EndedAt);
            // And no completion marker.
            Assert.False(await check.SecurityAuditEvents.AsNoTracking().AnyAsync(
                x => x.EventType == ProjectLeadershipReconciliationAuthority.MigrationMarker + ".Completed"));

            // Resolve the conflict, rerun: everything applies, one marker, and a further run changes nothing.
            await using (var resolve = new AeroLinkDbContext(Options(connection)))
            {
                var orphan = await resolve.ProgramMemberships.SingleAsync(
                    x => x.ProgramId == conflicted.Id && x.Role == ProgramRole.SoftwareEngineeringLead);
                orphan.End("operator", now);
                await resolve.SaveChangesAsync();
            }
            await using (var rerun = new AeroLinkDbContext(Options(connection)))
                await new ProjectLeadershipReconciliationAuthority(rerun).EnsureCompletedAsync();

            await using var after = new AeroLinkDbContext(Options(connection));
            var repaired = await after.ProgramMemberships.AsNoTracking().SingleAsync(
                x => x.ProgramId == repairable.Id && x.UserId == goodLead.Id
                     && x.Role == ProgramRole.SystemEngineeringLead);
            Assert.NotNull(repaired.EndedAt);
            Assert.Equal(1, await after.SecurityAuditEvents.AsNoTracking().CountAsync(
                x => x.EventType == ProjectLeadershipReconciliationAuthority.MigrationMarker + ".Completed"));
        }
        finally { await DropDatabaseAsync(server, database); }
    }

    [Fact]
    public async Task V2_reports_a_legacy_position_membership_held_by_somebody_other_than_the_assignment_holder()
    {
        if (!ServerConfigured(out var server)) return;
        string? database = null;
        var connection = await CreateDisposableDatabaseAsync(server);
        try
        {
            database = new NpgsqlConnectionStringBuilder(connection).Database;
            await using (var migrate = new AeroLinkDbContext(Options(connection)))
                await migrate.Database.MigrateAsync();

            var now = DateTimeOffset.UtcNow;
            var program = new ProgramRecord("V2 Holder Conflict", $"V2H{Guid.NewGuid():N}"[..12]);
            var legacyHolder = Account("v2.legacy.holder", now);
            var assignedHolder = Account("v2.assigned.holder", now);
            await using (var seed = new AeroLinkDbContext(Options(connection)))
            {
                seed.AddRange(program, legacyHolder, assignedHolder);
                seed.AddRange(
                    new ProgramMembership(legacyHolder.Id, program.Id, ProgramRole.SystemEngineeringLead, "legacy", now),
                    new ProgramMembership(assignedHolder.Id, program.Id, ProgramRole.SystemEngineer, "operator", now),
                    new ProjectLeadershipAssignment(program.Id, ProjectLeadershipPosition.SystemEngineeringLead,
                        assignedHolder.Id, "operator", now));
                await seed.SaveChangesAsync();
            }

            await using var v2 = new AeroLinkDbContext(Options(connection));
            var failure = await Assert.ThrowsAsync<InvalidOperationException>(
                () => new ProjectLeadershipReconciliationAuthority(v2).EnsureCompletedAsync());
            Assert.Contains("V2 Holder Conflict", failure.Message);
            Assert.Contains("does not hold the SystemEngineeringLead position", failure.Message);
            Assert.False(await v2.SecurityAuditEvents.AsNoTracking().AnyAsync(
                x => x.EventType == ProjectLeadershipReconciliationAuthority.MigrationMarker + ".Completed"));
        }
        finally { await DropDatabaseAsync(server, database); }
    }

    [Fact]
    public async Task V2_migrates_every_legacy_position_backup_family_to_the_same_person()
    {
        if (!ServerConfigured(out var server)) return;
        string? database = null;
        var connection = await CreateDisposableDatabaseAsync(server);
        try
        {
            database = new NpgsqlConnectionStringBuilder(connection).Database;
            await using (var migrate = new AeroLinkDbContext(Options(connection)))
                await migrate.Database.MigrateAsync();

            var now = DateTimeOffset.UtcNow;
            var program = new ProgramRecord("V2 Backup Families", $"V2D{Guid.NewGuid():N}"[..12]);
            var projectEngineer = Account("v2.backup.project", now);
            var programManager = Account("v2.backup.program", now);
            var engineeringManager = Account("v2.backup.engineering", now);
            var configurationManager = Account("v2.backup.configuration", now);
            var systemEngineeringLead = Account("v2.backup.system.engineering", now);
            var softwareEngineeringLead = Account("v2.backup.software.engineering", now);
            var systemTestLead = Account("v2.backup.system.test", now);
            var softwareTestLead = Account("v2.backup.software.test", now);
            var mappings = new[]
            {
                (ProgramRole.ProjectEngineeringLead, ProjectLeadershipPosition.ProjectEngineer, ProgramRole.ProjectEngineer, projectEngineer),
                (ProgramRole.ProjectEngineer, ProjectLeadershipPosition.ProjectEngineer, ProgramRole.ProjectEngineer, projectEngineer),
                (ProgramRole.ProgramManager, ProjectLeadershipPosition.ProgramManager, ProgramRole.ProgramManager, programManager),
                (ProgramRole.EngineeringManager, ProjectLeadershipPosition.EngineeringManager, ProgramRole.EngineeringManager, engineeringManager),
                (ProgramRole.ConfigurationManager, ProjectLeadershipPosition.ConfigurationManager, ProgramRole.ConfigurationManager, configurationManager),
                (ProgramRole.SystemEngineeringLead, ProjectLeadershipPosition.SystemEngineeringLead, ProgramRole.SystemEngineer, systemEngineeringLead),
                (ProgramRole.SoftwareEngineeringLead, ProjectLeadershipPosition.SoftwareEngineeringLead, ProgramRole.SoftwareEngineer, softwareEngineeringLead),
                (ProgramRole.SystemTestLead, ProjectLeadershipPosition.SystemTestLead, ProgramRole.SystemTestEngineer, systemTestLead),
                (ProgramRole.SoftwareTestLead, ProjectLeadershipPosition.SoftwareTestLead, ProgramRole.SoftwareTestEngineer, softwareTestLead),
            };
            await using (var seed = new AeroLinkDbContext(Options(connection)))
            {
                seed.Add(program);
                seed.AddRange(mappings.Select(x => x.Item4).DistinctBy(x => x.Id));
                seed.AddRange(mappings.DistinctBy(x => new { x.Item4.Id, x.Item3 }).Select(x =>
                    new ProgramMembership(x.Item4.Id, program.Id, x.Item3, "legacy", now)));
                seed.AddRange(mappings.Select(x =>
                    new ProjectRoleBackup(program.Id, x.Item1, x.Item4.Id, "legacy", now)));
                await seed.SaveChangesAsync();
            }

            await using (var v2 = new AeroLinkDbContext(Options(connection)))
                await new ProjectLeadershipReconciliationAuthority(v2).EnsureCompletedAsync();

            await using var check = new AeroLinkDbContext(Options(connection));
            foreach (var expected in mappings.DistinctBy(x => x.Item2))
                Assert.True(await check.ProjectLeadershipBackups.AsNoTracking().AnyAsync(x =>
                    x.ProgramId == program.Id && x.Position == expected.Item2
                    && x.BackupUserId == expected.Item4.Id && x.RemovedAt == null));
            Assert.Equal(8, await check.ProjectLeadershipBackups.AsNoTracking().CountAsync(x =>
                x.ProgramId == program.Id && x.RemovedAt == null));
            Assert.False(await check.ProjectRoleBackups.AsNoTracking().AnyAsync(x =>
                x.ProgramId == program.Id && x.RemovedAt == null));
            Assert.True(await check.SecurityAuditEvents.AsNoTracking().AnyAsync(
                x => x.EventType == ProjectLeadershipReconciliationAuthority.MigrationMarker + ".Completed"));
        }
        finally { await DropDatabaseAsync(server, database); }
    }

    [Fact]
    public async Task V2_reports_a_legacy_and_leadership_backup_that_name_different_people()
    {
        if (!ServerConfigured(out var server)) return;
        string? database = null;
        var connection = await CreateDisposableDatabaseAsync(server);
        try
        {
            database = new NpgsqlConnectionStringBuilder(connection).Database;
            await using (var migrate = new AeroLinkDbContext(Options(connection)))
                await migrate.Database.MigrateAsync();

            var now = DateTimeOffset.UtcNow;
            var program = new ProgramRecord("V2 Different Backup Holders", $"V2E{Guid.NewGuid():N}"[..12]);
            var legacy = Account("v2.backup.legacy.holder", now);
            var current = Account("v2.backup.current.holder", now);
            await using (var seed = new AeroLinkDbContext(Options(connection)))
            {
                seed.AddRange(program, legacy, current,
                    new ProgramMembership(legacy.Id, program.Id, ProgramRole.SystemEngineer, "legacy", now),
                    new ProgramMembership(current.Id, program.Id, ProgramRole.SystemEngineer, "operator", now),
                    new ProjectRoleBackup(program.Id, ProgramRole.SystemEngineeringLead, legacy.Id, "legacy", now),
                    new ProjectLeadershipBackup(program.Id, ProjectLeadershipPosition.SystemEngineeringLead,
                        current.Id, "operator", now));
                await seed.SaveChangesAsync();
            }

            await using var v2 = new AeroLinkDbContext(Options(connection));
            var failure = await Assert.ThrowsAsync<InvalidOperationException>(
                () => new ProjectLeadershipReconciliationAuthority(v2).EnsureCompletedAsync());
            Assert.Contains("V2 Different Backup Holders", failure.Message);
            Assert.Contains("name different people", failure.Message);
            Assert.True(await v2.ProjectRoleBackups.AsNoTracking().AnyAsync(x =>
                x.ProgramId == program.Id && x.BackupUserId == legacy.Id && x.RemovedAt == null));
            Assert.True(await v2.ProjectLeadershipBackups.AsNoTracking().AnyAsync(x =>
                x.ProgramId == program.Id && x.BackupUserId == current.Id && x.RemovedAt == null));
            Assert.False(await v2.SecurityAuditEvents.AsNoTracking().AnyAsync(
                x => x.EventType == ProjectLeadershipReconciliationAuthority.MigrationMarker + ".Completed"));
        }
        finally { await DropDatabaseAsync(server, database); }
    }

    /// <summary>
    /// The Project Engineering Lead backup v1 deliberately left behind. Where it maps unambiguously it moves
    /// to the Project Engineer position and the legacy row is retired, so removing the new backup actually
    /// removes the authority.
    /// </summary>
    [Fact]
    public async Task A_legacy_project_engineering_lead_backup_migrates_and_stops_being_a_second_channel()
    {
        if (!ServerConfigured(out var server)) return;
        string? database = null;
        var connection = await CreateDisposableDatabaseAsync(server);
        try
        {
            database = new NpgsqlConnectionStringBuilder(connection).Database;
            await using (var migrate = new AeroLinkDbContext(Options(connection))) await migrate.Database.MigrateAsync();

            var now = DateTimeOffset.UtcNow;
            var program = new ProgramRecord("V2 PEL Backup", $"V2C{Guid.NewGuid():N}"[..12]);
            var primary = Account("v2.pe.primary", now);
            var pelBackup = Account("v2.pel.backup", now);
            await using (var seed = new AeroLinkDbContext(Options(connection)))
            {
                seed.AddRange(program, primary, pelBackup);
                // The legacy shape: one Project Engineering Lead and a role-keyed backup for that retired
                // position. The backup deliberately does not yet hold the Project Engineer role — granting it
                // here would make two different people hold PE and PEL, which v1 refuses outright.
                seed.AddRange(
                    new ProgramMembership(primary.Id, program.Id, ProgramRole.ProjectEngineeringLead, "legacy", now),
                    new ProgramMembership(pelBackup.Id, program.Id, ProgramRole.SystemEngineer, "legacy", now),
                    new ProjectRoleBackup(program.Id, ProgramRole.ProjectEngineeringLead, pelBackup.Id, "legacy", now));
                await seed.SaveChangesAsync();
            }

            await using (var v1 = new AeroLinkDbContext(Options(connection)))
                await new ProjectLeadershipMigrationAuthority(v1).EnsureCompletedAsync();

            // The operator grants the backup the eligibility the position requires. v2 never invents this:
            // without it the backup is reported as a conflict rather than migrated.
            await using (var grant = new AeroLinkDbContext(Options(connection)))
            {
                grant.ProgramMemberships.Add(new ProgramMembership(
                    pelBackup.Id, program.Id, ProgramRole.ProjectEngineer, "operator", DateTimeOffset.UtcNow));
                await grant.SaveChangesAsync();
            }

            await using (var v2 = new AeroLinkDbContext(Options(connection)))
                await new ProjectLeadershipReconciliationAuthority(v2).EnsureCompletedAsync();

            await using (var check = new AeroLinkDbContext(Options(connection)))
            {
                Assert.True(await check.ProjectLeadershipBackups.AsNoTracking().AnyAsync(
                    x => x.ProgramId == program.Id && x.Position == ProjectLeadershipPosition.ProjectEngineer
                         && x.BackupUserId == pelBackup.Id && x.RemovedAt == null));
                Assert.False(await check.ProjectRoleBackups.AsNoTracking().AnyAsync(
                    x => x.ProgramId == program.Id && x.Role == ProgramRole.ProjectEngineeringLead && x.RemovedAt == null));
            }

            // Removing the new backup removes the authority: no hidden legacy channel survives.
            await using (var remove = new AeroLinkDbContext(Options(connection)))
            {
                var row = await remove.ProjectLeadershipBackups.SingleAsync(
                    x => x.ProgramId == program.Id && x.BackupUserId == pelBackup.Id && x.RemovedAt == null);
                row.Remove("operator", DateTimeOffset.UtcNow);
                await remove.SaveChangesAsync();
            }

            await using var after = new AeroLinkDbContext(Options(connection));
            var identity = new IdentityService(after);
            Assert.False(await identity.HasRoleAsync(pelBackup.Id, program.Id, ProgramRole.Reviewer, now, default));
        }
        finally { await DropDatabaseAsync(server, database); }
    }
}
