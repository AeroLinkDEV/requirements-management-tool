using AeroLink.Domain.Identity;
using AeroLink.Domain.Programs;
using AeroLink.Infrastructure.Persistence;
using AeroLink.Infrastructure.Persistence.Maintenance;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace AeroLink.Infrastructure.Tests;

/// <summary>
/// PostgreSQL qualification for the upgrade analyzer and the controlled conflict resolver (#881).
///
/// Two incidents drive these. In #747 and again in #816 an old but perfectly valid database could not be
/// started by current code, and the operator learned that only after dependencies, a client build, an API
/// start and a readiness timeout — with a .NET stack trace as the answer. Everything needed to say so was
/// knowable the moment PostgreSQL accepted a connection.
///
/// On 2026-08-31 the #816 repair then had to be performed with hand-written SQL against a live database,
/// because no supported path existed while the API was down. These prove the supported path: it analyzes
/// without writing, it never chooses between granting authority and retiring a designation, and it refuses
/// to write against state that moved after the operator reviewed it.
///
/// Skipped unless AEROLINK_MIGRATIONS_CONNECTION points at a disposable PostgreSQL server. The disposable
/// database is created and dropped per test; the persistent developer database on 54329 is never touched.
/// </summary>
public sealed class AeroLinkMaintenanceQualificationTests
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
        var database = $"aerolink_881_maint_{Guid.NewGuid():N}";
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

    private static AeroLinkUpgradeAnalyzer Analyzer(AeroLinkDbContext db) =>
        new(db, new ProjectLeadershipReconciliationAuthority(db));

    /// <summary>
    /// A migrated database with nothing pending reports current, and the analysis is honest that it wrote
    /// nothing.
    /// </summary>
    [Fact]
    public async Task A_current_database_reports_current_and_requires_no_upgrade()
    {
        if (!ServerConfigured(out var server)) return;
        string? database = null;
        var connection = await CreateDisposableDatabaseAsync(server);
        try
        {
            database = new NpgsqlConnectionStringBuilder(connection).Database;
            await using (var migrate = new AeroLinkDbContext(Options(connection))) await migrate.Database.MigrateAsync();
            await using (var upgrade = new AeroLinkDbContext(Options(connection)))
            {
                await new ProjectLeadershipMigrationAuthority(upgrade).EnsureCompletedAsync();
                await new ProjectLeadershipReconciliationAuthority(upgrade).EnsureCompletedAsync();
            }
            // The remaining authorities need a renderer and an evidence store to RUN; the analyzer only reads
            // the completion markers they write, so a database on which they already ran is modelled by those
            // markers. That is exactly the read the analyzer performs against a real installation.
            await MarkCompletedAsync(connection,
                SoftwareVerificationCaseMigrationAuthority.MigrationMarker,
                TestChangeRequestPrefixMigrationAuthority.MigrationMarker,
                SoftwareProcedureExecutionCutoverAuthority.MigrationMarker);

            await using var db = new AeroLinkDbContext(Options(connection));
            var analysis = await Analyzer(db).AnalyzeAsync();

            Assert.True(analysis.DatabaseReachable);
            Assert.Empty(analysis.PendingEfMigrations);
            Assert.Empty(analysis.Conflicts);
            Assert.Empty(analysis.PendingSemanticUpgrades);
            Assert.Equal("current", analysis.Status);
            Assert.False(analysis.UpgradeRequired);
            Assert.False(analysis.DatabaseModified);
            Assert.Equal(database, analysis.DatabaseName);
        }
        finally { await DropDatabaseAsync(server, database); }
    }

    /// <summary>
    /// A database the schema has moved past reports every pending migration by name, before any web server
    /// starts — which is the whole difference between two seconds and a readiness timeout.
    /// </summary>
    [Fact]
    public async Task Pending_schema_migrations_are_reported_by_name_without_starting_anything()
    {
        if (!ServerConfigured(out var server)) return;
        string? database = null;
        var connection = await CreateDisposableDatabaseAsync(server);
        try
        {
            database = new NpgsqlConnectionStringBuilder(connection).Database;
            // Deliberately NOT migrated: an empty database is every migration behind.
            await using var db = new AeroLinkDbContext(Options(connection));
            var analysis = await Analyzer(db).AnalyzeAsync();

            Assert.True(analysis.DatabaseReachable);
            Assert.NotEmpty(analysis.PendingEfMigrations);
            Assert.True(analysis.UpgradeRequired);
            Assert.Equal("upgrade-required", analysis.Status);
            Assert.False(analysis.DatabaseModified);

            // No conflicts are claimed against a schema this build has not migrated: the tables the semantic
            // markers live in may not exist, so the honest answer is "not yet knowable", assessed on the
            // isolated copy after it is migrated. Asking anyway used to fail with a PostgreSQL error rather
            // than an answer, which is the failure this assertion pins.
            Assert.Empty(analysis.Conflicts);

            var rendered = string.Join("\n", AeroLinkUpgradeAnalyzer.Render(analysis));
            Assert.Contains("DATABASE UPGRADE REQUIRED", rendered);
            Assert.Contains(analysis.PendingEfMigrations[0], rendered);
            Assert.Contains("isolated validated copy", rendered);
            Assert.Contains("No persistent data has been changed", rendered);
        }
        finally { await DropDatabaseAsync(server, database); }
    }

    /// <summary>
    /// A pending semantic upgrade with nothing ambiguous about it is a deterministic upgrade, not a conflict.
    /// </summary>
    [Fact]
    public async Task A_pending_semantic_upgrade_with_no_ambiguity_is_deterministic()
    {
        if (!ServerConfigured(out var server)) return;
        string? database = null;
        var connection = await CreateDisposableDatabaseAsync(server);
        try
        {
            database = new NpgsqlConnectionStringBuilder(connection).Database;
            await using (var migrate = new AeroLinkDbContext(Options(connection))) await migrate.Database.MigrateAsync();

            var now = DateTimeOffset.UtcNow;
            var program = new ProgramRecord("Deterministic", $"DET{Guid.NewGuid():N}"[..12]);
            var lead = Account("det.lead", now);
            await using (var seed = new AeroLinkDbContext(Options(connection)))
            {
                seed.AddRange(program, lead);
                seed.AddRange(
                    new ProgramMembership(lead.Id, program.Id, ProgramRole.SystemEngineeringLead, "legacy", now),
                    new ProgramMembership(lead.Id, program.Id, ProgramRole.SystemEngineer, "legacy", now));
                await seed.SaveChangesAsync();
            }

            await using var db = new AeroLinkDbContext(Options(connection));
            var analysis = await Analyzer(db).AnalyzeAsync();

            Assert.Empty(analysis.PendingEfMigrations);
            Assert.NotEmpty(analysis.PendingSemanticUpgrades);
            // Nothing ambiguous here: the v1 backfill has not run, and the legacy lead membership it is
            // about to turn into an assignment is ordinary work, not a conflict. Reporting v2's view of a
            // database v1 has not touched would raise a false alarm on the most common upgrade path there is.
            Assert.Empty(analysis.Conflicts);
            Assert.True(analysis.DeterministicUpgrade);
            Assert.Equal("upgrade-required", analysis.Status);
        }
        finally { await DropDatabaseAsync(server, database); }
    }

    /// <summary>
    /// The exact 2026-08-31 work-laptop conflict, reported as a record rather than raised as an exception:
    /// a legacy SoftwareEngineeringLead standing backup whose holder holds Engineer, not the required
    /// SoftwareEngineer. And it must be visible WITHOUT the analysis having written anything.
    /// </summary>
    [Fact]
    public async Task The_816_ineligible_legacy_backup_is_reported_as_a_structured_conflict_and_nothing_is_written()
    {
        if (!ServerConfigured(out var server)) return;
        string? database = null;
        var connection = await CreateDisposableDatabaseAsync(server);
        try
        {
            database = new NpgsqlConnectionStringBuilder(connection).Database;
            await using (var migrate = new AeroLinkDbContext(Options(connection))) await migrate.Database.MigrateAsync();

            var now = DateTimeOffset.UtcNow;
            var program = new ProgramRecord("Flight Management System", $"FMS{Guid.NewGuid():N}"[..12]);
            var avery = Account("software.engineer.070", now);
            var rina = Account("rina.shah", now);
            await using (var seed = new AeroLinkDbContext(Options(connection)))
            {
                seed.AddRange(program, avery, rina);
                seed.AddRange(
                    // Rina holds the position; Avery is the legacy standing backup and holds only Engineer,
                    // which was sufficient under the old authority rule and is not under #816.
                    new ProgramMembership(rina.Id, program.Id, ProgramRole.SoftwareEngineer, "legacy", now),
                    new ProgramMembership(avery.Id, program.Id, ProgramRole.Engineer, "legacy", now),
                    new ProjectLeadershipAssignment(program.Id, ProjectLeadershipPosition.SoftwareEngineeringLead, rina.Id, "operator", now),
                    new ProjectRoleBackup(program.Id, ProgramRole.SoftwareEngineeringLead, avery.Id, "legacy", now));
                await seed.SaveChangesAsync();
            }
            // v1 has run; v2 is what refuses.
            await using (var v1 = new AeroLinkDbContext(Options(connection)))
                await new ProjectLeadershipMigrationAuthority(v1).EnsureCompletedAsync();

            long rowsBefore;
            await using (var before = new AeroLinkDbContext(Options(connection)))
                rowsBefore = await before.ProjectRoleBackups.AsNoTracking().LongCountAsync()
                    + await before.ProjectLeadershipBackups.AsNoTracking().LongCountAsync()
                    + await before.ProgramMemberships.AsNoTracking().LongCountAsync()
                    + await before.SecurityAuditEvents.AsNoTracking().LongCountAsync();

            await using var db = new AeroLinkDbContext(Options(connection));
            var analysis = await Analyzer(db).AnalyzeAsync();

            Assert.Equal("conflict", analysis.Status);
            var conflict = Assert.Single(analysis.Conflicts,
                x => x.Code == AeroLinkUpgradeConflict.LegacyBackupIneligibleCode);
            Assert.Equal("Flight Management System", conflict.Subject["program"]);
            Assert.Equal("SoftwareEngineeringLead", conflict.Subject["position"]);
            Assert.Equal(avery.Id.ToString(), conflict.Subject["personId"]);
            Assert.Equal("SoftwareEngineer", conflict.Subject["requiredBaseRole"]);
            Assert.Equal("Engineer", conflict.Subject["heldBaseRoles"]);
            Assert.Equal(rina.Id.ToString(), conflict.Subject["currentPrimaryId"]);

            // Both decisions offered; exactly one grants authority nobody has today, and it is flagged.
            Assert.Equal(2, conflict.Choices.Count);
            Assert.True(conflict.Choices.Single(x => x.Key == AeroLinkUpgradeConflict.ChoiceGrantAndKeep).GrantsNewAuthority);
            Assert.False(conflict.Choices.Single(x => x.Key == AeroLinkUpgradeConflict.ChoiceRetireBackup).GrantsNewAuthority);

            var rendered = string.Join("\n", AeroLinkUpgradeAnalyzer.Render(analysis));
            Assert.Contains("DATABASE ATTENTION REQUIRED", rendered);
            Assert.Contains("AeroLink made NO authority decision automatically", rendered);
            Assert.Contains("No persistent data was changed", rendered);

            await using var after = new AeroLinkDbContext(Options(connection));
            var rowsAfter = await after.ProjectRoleBackups.AsNoTracking().LongCountAsync()
                + await after.ProjectLeadershipBackups.AsNoTracking().LongCountAsync()
                + await after.ProgramMemberships.AsNoTracking().LongCountAsync()
                + await after.SecurityAuditEvents.AsNoTracking().LongCountAsync();
            Assert.Equal(rowsBefore, rowsAfter);
            Assert.False(analysis.DatabaseModified);
        }
        finally { await DropDatabaseAsync(server, database); }
    }

    /// <summary>
    /// Several conflicts in one database are all reported by ONE analysis. Discovering them one restart at a
    /// time is the operator experience #881 exists to end.
    /// </summary>
    [Fact]
    public async Task Multiple_conflicts_are_all_reported_in_one_analysis()
    {
        if (!ServerConfigured(out var server)) return;
        string? database = null;
        var connection = await CreateDisposableDatabaseAsync(server);
        try
        {
            database = new NpgsqlConnectionStringBuilder(connection).Database;
            await using (var migrate = new AeroLinkDbContext(Options(connection))) await migrate.Database.MigrateAsync();

            var now = DateTimeOffset.UtcNow;
            var first = new ProgramRecord("First Program", $"ONE{Guid.NewGuid():N}"[..12]);
            var second = new ProgramRecord("Second Program", $"TWO{Guid.NewGuid():N}"[..12]);
            var ineligible = Account("multi.ineligible", now);
            var primary = Account("multi.primary", now);
            var left = Account("multi.left", now);
            var right = Account("multi.right", now);
            await using (var seed = new AeroLinkDbContext(Options(connection)))
            {
                seed.AddRange(first, second, ineligible, primary, left, right);
                seed.AddRange(
                    // First: ineligible legacy backup (the #816 shape).
                    new ProgramMembership(primary.Id, first.Id, ProgramRole.SoftwareEngineer, "legacy", now),
                    new ProgramMembership(ineligible.Id, first.Id, ProgramRole.Engineer, "legacy", now),
                    new ProjectLeadershipAssignment(first.Id, ProjectLeadershipPosition.SoftwareEngineeringLead, primary.Id, "operator", now),
                    new ProjectRoleBackup(first.Id, ProgramRole.SoftwareEngineeringLead, ineligible.Id, "legacy", now),
                    // Second: two legacy backups mapping to one position, naming different people.
                    new ProgramMembership(left.Id, second.Id, ProgramRole.ProjectEngineer, "legacy", now),
                    new ProgramMembership(right.Id, second.Id, ProgramRole.ProjectEngineer, "legacy", now),
                    new ProjectRoleBackup(second.Id, ProgramRole.ProjectEngineer, left.Id, "legacy", now),
                    new ProjectRoleBackup(second.Id, ProgramRole.ProjectEngineeringLead, right.Id, "legacy", now));
                await seed.SaveChangesAsync();
            }
            await using (var v1 = new AeroLinkDbContext(Options(connection)))
                await new ProjectLeadershipMigrationAuthority(v1).EnsureCompletedAsync();

            await using var db = new AeroLinkDbContext(Options(connection));
            var analysis = await Analyzer(db).AnalyzeAsync();

            Assert.True(analysis.Conflicts.Count >= 2,
                $"Expected every conflict in one analysis; got {analysis.Conflicts.Count}.");
            Assert.Contains(analysis.Conflicts, x => x.Code == AeroLinkUpgradeConflict.LegacyBackupIneligibleCode);
            Assert.Contains(analysis.Conflicts, x => x.Code == AeroLinkUpgradeConflict.LegacyBackupAmbiguousCode);
        }
        finally { await DropDatabaseAsync(server, database); }
    }

    /// <summary>
    /// The resolver, on the #816 conflict. Dry run writes nothing; retiring the legacy designation ends it
    /// with attribution rather than deleting it; the analysis is clean afterwards.
    /// </summary>
    [Fact]
    public async Task Retiring_the_legacy_backup_preserves_history_and_clears_the_conflict()
    {
        if (!ServerConfigured(out var server)) return;
        string? database = null;
        var connection = await CreateDisposableDatabaseAsync(server);
        try
        {
            database = new NpgsqlConnectionStringBuilder(connection).Database;
            var fixture = await SeedIneligibleBackupAsync(connection);

            // Dry run first, exactly as an operator would.
            await using (var dryRun = new AeroLinkDbContext(Options(connection)))
            {
                var preview = await new ProjectLeadershipMaintenanceResolver(dryRun).ResolveLegacyBackupAsync(
                    fixture.ProgramId, fixture.LegacyBackupId, ProjectLeadershipPosition.SoftwareEngineeringLead,
                    fixture.PersonId, AeroLinkUpgradeConflict.ChoiceRetireBackup, fixture.PrimaryId,
                    "Sean, issue #816", apply: false);
                Assert.False(preview.Applied);
                Assert.Equal(AeroLinkResolutionResult.DryRunOutcome, preview.Outcome);
                Assert.NotEmpty(preview.Changes);
            }
            await using (var unchanged = new AeroLinkDbContext(Options(connection)))
            {
                Assert.True(await unchanged.ProjectRoleBackups.AsNoTracking()
                    .AnyAsync(x => x.Id == fixture.LegacyBackupId && x.RemovedAt == null));
                Assert.Empty(await unchanged.SecurityAuditEvents.AsNoTracking()
                    .Where(x => x.EventType == AeroLinkMaintenanceAttribution.DecisionEvent).ToListAsync());
            }

            await using (var apply = new AeroLinkDbContext(Options(connection)))
            {
                // The conflict code is passed explicitly, and a NON-default one, because several conflicts
                // share this resolution path: the audit must record the conflict the operator reviewed
                // rather than whichever code the resolver happens to default to.
                var applied = await new ProjectLeadershipMaintenanceResolver(apply).ResolveLegacyBackupAsync(
                    fixture.ProgramId, fixture.LegacyBackupId, ProjectLeadershipPosition.SoftwareEngineeringLead,
                    fixture.PersonId, AeroLinkUpgradeConflict.ChoiceRetireBackup, fixture.PrimaryId,
                    "Sean, issue #816", apply: true,
                    conflictCode: AeroLinkUpgradeConflict.LegacyBackupSupersededCode);
                Assert.True(applied.Applied);
            }

            await using (var check = new AeroLinkDbContext(Options(connection)))
            {
                // Ended, not deleted: "who was standing cover in March" stays answerable.
                var legacy = await check.ProjectRoleBackups.AsNoTracking().SingleAsync(x => x.Id == fixture.LegacyBackupId);
                Assert.NotNull(legacy.RemovedAt);
                Assert.Equal(AeroLinkMaintenanceAttribution.Actor, legacy.RemovedBy);
                Assert.Equal("legacy", legacy.NamedBy);

                // No authority was granted to make the upgrade pass.
                Assert.False(await check.ProgramMemberships.AsNoTracking().AnyAsync(x =>
                    x.UserId == fixture.PersonId && x.ProgramId == fixture.ProgramId
                    && x.Role == ProgramRole.SoftwareEngineer && x.EndedAt == null));

                var audit = await check.SecurityAuditEvents.AsNoTracking()
                    .SingleAsync(x => x.EventType == AeroLinkMaintenanceAttribution.DecisionEvent);
                Assert.Equal(AeroLinkMaintenanceAttribution.Actor, audit.ActorId);
                Assert.Equal(AeroLinkMaintenanceAttribution.Source, audit.IpAddress);
                Assert.Contains("Sean, issue #816", audit.Detail);
                Assert.Contains(AeroLinkUpgradeConflict.ChoiceRetireBackup, audit.Detail);
                Assert.Contains(AeroLinkUpgradeConflict.LegacyBackupSupersededCode, audit.Detail);
                Assert.DoesNotContain(AeroLinkUpgradeConflict.LegacyBackupIneligibleCode, audit.Detail);
            }

            await using (var reanalyze = new AeroLinkDbContext(Options(connection)))
                Assert.Empty((await Analyzer(reanalyze).AnalyzeAsync()).Conflicts);
        }
        finally { await DropDatabaseAsync(server, database); }
    }

    /// <summary>
    /// The other decision, which grants authority. It is only ever taken because the operator named it, and
    /// taking it leaves the person genuinely eligible rather than merely unblocking startup.
    /// </summary>
    [Fact]
    public async Task Granting_the_required_role_is_an_explicit_choice_that_leaves_the_backup_eligible()
    {
        if (!ServerConfigured(out var server)) return;
        string? database = null;
        var connection = await CreateDisposableDatabaseAsync(server);
        try
        {
            database = new NpgsqlConnectionStringBuilder(connection).Database;
            var fixture = await SeedIneligibleBackupAsync(connection);

            await using (var apply = new AeroLinkDbContext(Options(connection)))
            {
                var applied = await new ProjectLeadershipMaintenanceResolver(apply).ResolveLegacyBackupAsync(
                    fixture.ProgramId, fixture.LegacyBackupId, ProjectLeadershipPosition.SoftwareEngineeringLead,
                    fixture.PersonId, AeroLinkUpgradeConflict.ChoiceGrantAndKeep, fixture.PrimaryId,
                    "Sean, issue #816", apply: true);
                Assert.True(applied.Applied);
            }

            await using var check = new AeroLinkDbContext(Options(connection));
            Assert.True(await check.ProgramMemberships.AsNoTracking().AnyAsync(x =>
                x.UserId == fixture.PersonId && x.ProgramId == fixture.ProgramId
                && x.Role == ProgramRole.SoftwareEngineer && x.EndedAt == null));
            Assert.True(await check.ProjectLeadershipBackups.AsNoTracking().AnyAsync(x =>
                x.ProgramId == fixture.ProgramId && x.Position == ProjectLeadershipPosition.SoftwareEngineeringLead
                && x.BackupUserId == fixture.PersonId && x.RemovedAt == null));
            var legacy = await check.ProjectRoleBackups.AsNoTracking().SingleAsync(x => x.Id == fixture.LegacyBackupId);
            Assert.NotNull(legacy.RemovedAt);

            await using var reanalyze = new AeroLinkDbContext(Options(connection));
            Assert.Empty((await Analyzer(reanalyze).AnalyzeAsync()).Conflicts);
        }
        finally { await DropDatabaseAsync(server, database); }
    }

    /// <summary>
    /// State moved between the operator reviewing the conflict and acting on it. The write must refuse:
    /// they reviewed a different situation, and applying their decision to this one is a guess.
    /// </summary>
    [Fact]
    public async Task A_precondition_that_moved_after_analysis_refuses_and_writes_nothing()
    {
        if (!ServerConfigured(out var server)) return;
        string? database = null;
        var connection = await CreateDisposableDatabaseAsync(server);
        try
        {
            database = new NpgsqlConnectionStringBuilder(connection).Database;
            var fixture = await SeedIneligibleBackupAsync(connection);

            // The primary is replaced after the operator read the analysis.
            var replacement = Account("multi.replacement", DateTimeOffset.UtcNow);
            await using (var move = new AeroLinkDbContext(Options(connection)))
            {
                move.Add(replacement);
                var assignment = await move.ProjectLeadershipAssignments.SingleAsync(x =>
                    x.ProgramId == fixture.ProgramId
                    && x.Position == ProjectLeadershipPosition.SoftwareEngineeringLead && x.EndedAt == null);
                var later = DateTimeOffset.UtcNow;
                assignment.End("operator", later);
                move.Add(new ProgramMembership(replacement.Id, fixture.ProgramId, ProgramRole.SoftwareEngineer, "operator", later));
                move.Add(new ProjectLeadershipAssignment(fixture.ProgramId, ProjectLeadershipPosition.SoftwareEngineeringLead, replacement.Id, "operator", later));
                await move.SaveChangesAsync();
            }

            await using (var stale = new AeroLinkDbContext(Options(connection)))
            {
                var refused = await new ProjectLeadershipMaintenanceResolver(stale).ResolveLegacyBackupAsync(
                    fixture.ProgramId, fixture.LegacyBackupId, ProjectLeadershipPosition.SoftwareEngineeringLead,
                    fixture.PersonId, AeroLinkUpgradeConflict.ChoiceRetireBackup,
                    fixture.PrimaryId, // the primary the operator reviewed, who is no longer the primary
                    "Sean, issue #816", apply: true);
                Assert.False(refused.Applied);
                Assert.Equal(AeroLinkResolutionResult.PreconditionFailedOutcome, refused.Outcome);
                Assert.Contains("changed after the conflict was analyzed", refused.Detail);
            }

            await using var check = new AeroLinkDbContext(Options(connection));
            Assert.True(await check.ProjectRoleBackups.AsNoTracking()
                .AnyAsync(x => x.Id == fixture.LegacyBackupId && x.RemovedAt == null));
            Assert.Empty(await check.SecurityAuditEvents.AsNoTracking()
                .Where(x => x.EventType == AeroLinkMaintenanceAttribution.DecisionEvent).ToListAsync());
            // The refusal itself is evidence, so a decision that could not be applied leaves a record.
            Assert.NotEmpty(await check.SecurityAuditEvents.AsNoTracking()
                .Where(x => x.EventType == AeroLinkMaintenanceAttribution.RefusedEvent).ToListAsync());
        }
        finally { await DropDatabaseAsync(server, database); }
    }

    /// <summary>
    /// A row that has already been retired, or that belongs to another program, is not the row the operator
    /// reviewed, and no decision may be applied to it.
    /// </summary>
    [Fact]
    public async Task A_legacy_row_that_is_no_longer_the_analyzed_row_refuses()
    {
        if (!ServerConfigured(out var server)) return;
        string? database = null;
        var connection = await CreateDisposableDatabaseAsync(server);
        try
        {
            database = new NpgsqlConnectionStringBuilder(connection).Database;
            var fixture = await SeedIneligibleBackupAsync(connection);

            await using (var first = new AeroLinkDbContext(Options(connection)))
                Assert.True((await new ProjectLeadershipMaintenanceResolver(first).ResolveLegacyBackupAsync(
                    fixture.ProgramId, fixture.LegacyBackupId, ProjectLeadershipPosition.SoftwareEngineeringLead,
                    fixture.PersonId, AeroLinkUpgradeConflict.ChoiceRetireBackup, fixture.PrimaryId,
                    "Sean", apply: true)).Applied);

            // The same decision, replayed. It must not apply twice.
            await using (var replay = new AeroLinkDbContext(Options(connection)))
            {
                var refused = await new ProjectLeadershipMaintenanceResolver(replay).ResolveLegacyBackupAsync(
                    fixture.ProgramId, fixture.LegacyBackupId, ProjectLeadershipPosition.SoftwareEngineeringLead,
                    fixture.PersonId, AeroLinkUpgradeConflict.ChoiceRetireBackup, fixture.PrimaryId,
                    "Sean", apply: true);
                Assert.False(refused.Applied);
                Assert.Equal(AeroLinkResolutionResult.PreconditionFailedOutcome, refused.Outcome);
            }

            // An unsupported choice is refused before anything is read or written.
            await using (var wrongChoice = new AeroLinkDbContext(Options(connection)))
            {
                var refused = await new ProjectLeadershipMaintenanceResolver(wrongChoice).ResolveLegacyBackupAsync(
                    fixture.ProgramId, fixture.LegacyBackupId, ProjectLeadershipPosition.SoftwareEngineeringLead,
                    fixture.PersonId, "delete-the-row", fixture.PrimaryId, "Sean", apply: true);
                Assert.False(refused.Applied);
                Assert.Equal(AeroLinkResolutionResult.ChoiceRefusedOutcome, refused.Outcome);
            }
        }
        finally { await DropDatabaseAsync(server, database); }
    }

    /// <summary>
    /// A maintenance decision must be attributable to a person who asked for it, not only to a process.
    /// </summary>
    [Fact]
    public async Task A_decision_without_an_operator_reference_is_rejected()
    {
        if (!ServerConfigured(out var server)) return;
        string? database = null;
        var connection = await CreateDisposableDatabaseAsync(server);
        try
        {
            database = new NpgsqlConnectionStringBuilder(connection).Database;
            var fixture = await SeedIneligibleBackupAsync(connection);
            await using var db = new AeroLinkDbContext(Options(connection));
            await Assert.ThrowsAsync<ArgumentException>(() =>
                new ProjectLeadershipMaintenanceResolver(db).ResolveLegacyBackupAsync(
                    fixture.ProgramId, fixture.LegacyBackupId, ProjectLeadershipPosition.SoftwareEngineeringLead,
                    fixture.PersonId, AeroLinkUpgradeConflict.ChoiceRetireBackup, fixture.PrimaryId,
                    "   ", apply: true));
        }
        finally { await DropDatabaseAsync(server, database); }
    }

    /// <summary>
    /// The analyzer's authority list and the startup sequence in Program.cs must name the same authorities.
    ///
    /// They are two lists in two files, and the failure mode when they drift is silent under-reporting: an
    /// authority that startup runs but the analyzer does not know about is a pending upgrade the operator is
    /// never told is pending, and a conflict they meet as a stack trace instead. Source-derived rather than
    /// hand-maintained, so adding one in Program.cs fails here rather than shipping.
    /// </summary>
    [Fact]
    public void The_analyzer_knows_every_semantic_authority_startup_runs()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        string? programPath = null;
        while (directory is not null && programPath is null)
        {
            var candidate = Path.Combine(directory.FullName, "product", "src", "AeroLink.Api", "Program.cs");
            if (File.Exists(candidate)) programPath = candidate;
            directory = directory.Parent;
        }
        Assert.True(programPath is not null, "Program.cs was not found above the test assembly.");

        var program = File.ReadAllText(programPath!);
        // Every authority resolved in the startup scope, by the exact type name Program.cs asks for.
        var startupAuthorities = System.Text.RegularExpressions.Regex
            .Matches(program, @"GetRequiredService<(\w+(?:Migration|Reconciliation|Cutover)Authority)>")
            .Select(x => x.Groups[1].Value)
            .Distinct()
            .OrderBy(x => x)
            .ToList();
        Assert.NotEmpty(startupAuthorities);

        var analyzerAuthorities = AeroLinkUpgradeAnalyzer.SemanticAuthorities
            .Select(x => x.Marker switch
            {
                var m when m == SoftwareVerificationCaseMigrationAuthority.MigrationMarker => nameof(SoftwareVerificationCaseMigrationAuthority),
                var m when m == ProjectLeadershipMigrationAuthority.MigrationMarker => nameof(ProjectLeadershipMigrationAuthority),
                var m when m == ProjectLeadershipReconciliationAuthority.MigrationMarker => nameof(ProjectLeadershipReconciliationAuthority),
                var m when m == TestChangeRequestPrefixMigrationAuthority.MigrationMarker => nameof(TestChangeRequestPrefixMigrationAuthority),
                var m when m == SoftwareProcedureExecutionCutoverAuthority.MigrationMarker => nameof(SoftwareProcedureExecutionCutoverAuthority),
                _ => x.Marker,
            })
            .OrderBy(x => x)
            .ToList();

        Assert.Equal(startupAuthorities, analyzerAuthorities);
    }

    /// <summary>
    /// Records the completion markers named authorities write, for a database that has already run them.
    /// The audit target must match what the authority itself records, because that is what the analyzer
    /// matches on — a marker with the wrong target would read as still pending.
    /// </summary>
    private static async Task MarkCompletedAsync(string connection, params string[] markers)
    {
        await using var db = new AeroLinkDbContext(Options(connection));
        foreach (var marker in markers)
        {
            var target = AeroLinkUpgradeAnalyzer.SemanticAuthorities.Single(x => x.Marker == marker).Target;
            db.SecurityAuditEvents.Add(new SecurityAuditEvent(marker + ".Completed", "aerolink-migration",
                target, "Success", "Recorded by qualification for an installation that already ran this.",
                "local", DateTimeOffset.UtcNow));
        }
        await db.SaveChangesAsync();
    }

    private sealed record IneligibleBackupFixture(Guid ProgramId, Guid PersonId, Guid PrimaryId, Guid LegacyBackupId);

    /// <summary>The 2026-08-31 work-laptop shape: Rina holds the position, Avery is the ineligible backup.</summary>
    private static async Task<IneligibleBackupFixture> SeedIneligibleBackupAsync(string connection)
    {
        await using (var migrate = new AeroLinkDbContext(Options(connection))) await migrate.Database.MigrateAsync();

        var now = DateTimeOffset.UtcNow;
        var program = new ProgramRecord("Flight Management System", $"FMS{Guid.NewGuid():N}"[..12]);
        var avery = Account($"software.engineer.070.{Guid.NewGuid():N}"[..24], now);
        var rina = Account($"rina.shah.{Guid.NewGuid():N}"[..24], now);
        var legacyBackup = new ProjectRoleBackup(program.Id, ProgramRole.SoftwareEngineeringLead, avery.Id, "legacy", now);
        await using (var seed = new AeroLinkDbContext(Options(connection)))
        {
            seed.AddRange(program, avery, rina);
            seed.AddRange(
                new ProgramMembership(rina.Id, program.Id, ProgramRole.SoftwareEngineer, "legacy", now),
                new ProgramMembership(avery.Id, program.Id, ProgramRole.Engineer, "legacy", now),
                new ProjectLeadershipAssignment(program.Id, ProjectLeadershipPosition.SoftwareEngineeringLead, rina.Id, "operator", now),
                legacyBackup);
            await seed.SaveChangesAsync();
        }
        await using (var v1 = new AeroLinkDbContext(Options(connection)))
            await new ProjectLeadershipMigrationAuthority(v1).EnsureCompletedAsync();

        return new IneligibleBackupFixture(program.Id, avery.Id, rina.Id, legacyBackup.Id);
    }
}
