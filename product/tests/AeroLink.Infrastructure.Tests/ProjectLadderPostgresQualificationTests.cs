using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Hierarchy;
using AeroLink.Domain.Programs;
using AeroLink.Domain.Verification;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace AeroLink.Infrastructure.Tests;

[CollectionDefinition("Issue707Postgres", DisableParallelization = true)]
public sealed class Issue707PostgresCollection : ICollectionFixture<object>;

[Collection("Issue707Postgres")]
public sealed class ProjectLadderPostgresQualificationTests
{
    private const string PreFeatureMigration = "20260821171548_SoftRetireRequirementSpecifications";
    private const string LegacySnapshot =
        "steps[1:System:7;2:HighLevel:7;3:LowLevel:15]|edges[HighLevel>LowLevel;System>HighLevel]";
    private const string LegacySnapshotHash = "6fc44a4303eee5204f376a377bf139da11c421ca35e3d64b9b15cadcdb502fb7";

    [DisposablePostgresFact]
    public async Task Clean_install_seals_first_content_atomically_on_postgresql()
    {
        var connection = QualificationConnectionOrSkip();
        await using var db = await ResetAtLatestAsync(connection);
        var now = DateTimeOffset.UtcNow;
        var program = new ProgramRecord("PG clean program", "PGC");
        var project = new ProjectRecord(program.Id, "PG clean project", "PG clean software");
        db.AddRange(program, project);
        await db.SaveChangesAsync();

        db.ProjectLadderConfigurations.Add(LegacyDefaultProjectLadderFactory.Create(project.Id, now));
        var release = new SoftwareRelease(project.Id, "1.0", true);
        var request = new SystemChangeRequest("SRCR-70720", 0, project.Id, release.Id,
            "PG clean first content", "Problem", "Analysis", "Solution", "pg.test", now);
        request.AddRequirementChange("pg.test", "SYSR-00000020", 0, RequirementLevel.System,
            RequirementChangeKind.Introduce, "PG first content", "PG first content", "Review", now);
        db.AddRange(release, request);
        await db.SaveChangesAsync();

        var configuration = await db.ProjectLadderConfigurations.SingleAsync(x => x.ProjectId == project.Id);
        Assert.True(configuration.IsSealed);
        Assert.Equal("draft-requirement-change", configuration.SealedContentKind);
        Assert.Single(await db.ProjectLadderConfigurationHistories.Where(x => x.ProjectId == project.Id).ToListAsync());
        var history = await db.ProjectLadderConfigurationHistories.AsNoTracking()
            .SingleAsync(x => x.ProjectId == project.Id);
        const string expectedCanonical =
            "schema[2]|steps[1:System:7:Procedure;2:HighLevel:7:Case;3:LowLevel:15:Case]|edges[HighLevel>LowLevel;System>HighLevel]";
        Assert.Equal(expectedCanonical, history.CanonicalSnapshot);
        Assert.Equal(ProjectLadderSnapshot.Hash(expectedCanonical), history.SnapshotHash);
        Assert.Equal(2, history.SnapshotSchemaVersion);
    }

    [DisposablePostgresFact]
    public async Task Issue729_clean_install_has_dormant_profile_and_neutral_identity_schema_without_evidence()
    {
        var connection = QualificationConnectionOrSkip();
        await using var db = await ResetAtLatestAsync(connection);
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = "SELECT table_name, column_name FROM information_schema.columns WHERE table_schema = 'public' AND table_name IN ('project_ladder_steps','project_ladder_configurations','project_ladder_configuration_history','test_procedures')";
        await db.Database.OpenConnectionAsync();
        var columns = new HashSet<string>(StringComparer.Ordinal);
        await using (var reader = await command.ExecuteReaderAsync())
            while (await reader.ReadAsync()) columns.Add($"{reader.GetString(0)}.{reader.GetString(1)}");
        Assert.Contains("project_ladder_steps.EnabledArtifactKindsValue", columns);
        Assert.Contains("project_ladder_configurations.VerificationProfileSchemaVersion", columns);
        Assert.Contains("project_ladder_configuration_history.SnapshotSchemaVersion", columns);
        Assert.Contains("test_procedures.ArtifactDiscipline", columns);
        Assert.Contains("test_procedures.ArtifactKind", columns);
        Assert.Empty(await db.ProjectLadderConfigurations.ToListAsync());
        Assert.Empty(await db.ProjectLadderConfigurationHistories.ToListAsync());
    }

    [DisposablePostgresFact]
    public async Task Pre_feature_database_backfill_seals_with_truthful_immutable_evidence_on_postgresql()
    {
        var connection = QualificationConnectionOrSkip();
        await using var db = await MigrateToPreFeatureAsync(connection);
        var now = DateTimeOffset.UtcNow;
        var programId = Guid.NewGuid();
        var projectId = Guid.NewGuid();
        var configurationId = Guid.NewGuid();
        var systemStepId = Guid.NewGuid();
        var highStepId = Guid.NewGuid();
        var lowStepId = Guid.NewGuid();
        var requirementId = Guid.NewGuid();

        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "programs" ("Id", "Name", "Code") VALUES ({programId}, {"PG pre-feature program"}, {"PGP"});
            INSERT INTO "projects" ("Id", "ProgramId", "Name", "SoftwareProduct") VALUES ({projectId}, {programId}, {"PG pre-feature project"}, {"PG pre-feature software"});
            INSERT INTO "project_ladder_configurations"
                ("Id", "ProjectId", "Classification", "State", "CreatedAt", "UpdatedAt", "Version")
                VALUES ({configurationId}, {projectId}, {"LegacyDefault"}, {"Stored"}, {now}, {now}, {1L});
            INSERT INTO "project_ladder_steps"
                ("Id", "ConfigurationId", "ProjectId", "CatalogueEntry", "Position", "Capabilities", "CreatedAt", "UpdatedAt", "Version")
                VALUES ({systemStepId}, {configurationId}, {projectId}, {"System"}, {1}, {7}, {now}, {now}, {1L}),
                       ({highStepId}, {configurationId}, {projectId}, {"HighLevel"}, {2}, {7}, {now}, {now}, {1L}),
                       ({lowStepId}, {configurationId}, {projectId}, {"LowLevel"}, {3}, {15}, {now}, {now}, {1L});
            INSERT INTO "project_ladder_allowed_upstreams"
                ("Id", "ConfigurationId", "ProjectId", "ParentStepId", "ChildStepId", "CreatedAt", "UpdatedAt", "Version")
                VALUES ({Guid.NewGuid()}, {configurationId}, {projectId}, {systemStepId}, {highStepId}, {now}, {now}, {1L}),
                       ({Guid.NewGuid()}, {configurationId}, {projectId}, {highStepId}, {lowStepId}, {now}, {now}, {1L});
            INSERT INTO "requirements" ("Id", "ProjectId", "BaseNumber", "Level", "CreatedAt")
                VALUES ({requirementId}, {projectId}, {"SYS-00020"}, {"System"}, {now});
            """);

        await db.Database.GetService<IMigrator>().MigrateAsync();

        var configuration = await db.ProjectLadderConfigurations.AsNoTracking()
            .SingleAsync(x => x.Id == configurationId);
        var history = await db.ProjectLadderConfigurationHistories.AsNoTracking()
            .SingleAsync(x => x.ConfigurationId == configurationId);
        Assert.True(configuration.IsSealed);
        Assert.Equal(2, configuration.Version);
        Assert.Equal(configuration.Version, history.Revision);
        Assert.Equal(configuration.SealedAt, history.OccurredAt);
        Assert.Equal("migration.backfill", configuration.SealedBy);
        Assert.Equal("migration-backfill", configuration.SealedContentKind);
        Assert.Equal(LegacySnapshot, history.CanonicalSnapshot);
        Assert.Equal(LegacySnapshotHash, history.SnapshotHash);
        Assert.Equal(2, configuration.VerificationProfileSchemaVersion);
        Assert.Equal(1, history.SnapshotSchemaVersion);
        Assert.Equal(["Procedure", "Case", "Case"], await db.ProjectLadderSteps.AsNoTracking()
            .Where(x => x.ConfigurationId == configurationId).OrderBy(x => x.Position)
            .Select(x => x.EnabledArtifactKindsValue).ToListAsync());
        Assert.Null(configuration.ActivationManifestVersion);
        Assert.Null(configuration.ActivationManifestHash);
        Assert.Contains("historical first content is not inferred", history.Reason, StringComparison.Ordinal);
    }

    [DisposablePostgresFact]
    public async Task Issue729_pre_feature_migration_leaves_no_verification_step_empty_and_is_idempotent()
    {
        var connection = QualificationConnectionOrSkip();
        await using var db = await MigrateToPreFeatureAsync(connection);
        var now = DateTimeOffset.UtcNow;
        var programId = Guid.NewGuid(); var projectId = Guid.NewGuid(); var configurationId = Guid.NewGuid(); var stepId = Guid.NewGuid();
        var systemProcedureId = Guid.NewGuid(); var highLevelProcedureId = Guid.NewGuid();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "programs" ("Id", "Name", "Code") VALUES ({programId}, {"PG no-verification program"}, {"PGN"});
            INSERT INTO "projects" ("Id", "ProgramId", "Name", "SoftwareProduct") VALUES ({projectId}, {programId}, {"PG no-verification project"}, {"PG no-verification software"});
            INSERT INTO "project_ladder_configurations" ("Id", "ProjectId", "Classification", "State", "CreatedAt", "UpdatedAt", "Version")
                VALUES ({configurationId}, {projectId}, {"LegacyDefault"}, {"Stored"}, {now}, {now}, {1L});
            INSERT INTO "project_ladder_steps" ("Id", "ConfigurationId", "ProjectId", "CatalogueEntry", "Position", "Capabilities", "CreatedAt", "UpdatedAt", "Version")
                VALUES ({stepId}, {configurationId}, {projectId}, {"HighLevel"}, {1}, {1}, {now}, {now}, {1L});
            INSERT INTO "test_procedures" ("Id", "ProjectId", "BaseNumber", "Title", "OwnerId", "Level", "CreatedAt", "UpdatedAt", "Version")
                VALUES ({systemProcedureId}, {projectId}, {"SYSTP-72901"}, {"Legacy system procedure"}, {"migration.test"}, {"System"}, {now}, {now}, {1L}),
                       ({highLevelProcedureId}, {projectId}, {"HLRTP-72901"}, {"Legacy high-level case"}, {"migration.test"}, {"HighLevel"}, {now}, {now}, {1L});
            """);
        await db.Database.GetService<IMigrator>().MigrateAsync();
        var step = await db.ProjectLadderSteps.AsNoTracking().SingleAsync(x => x.Id == stepId);
        Assert.Equal(string.Empty, step.EnabledArtifactKindsValue);
        var migratedArtifacts = await db.TestProcedures.AsNoTracking().OrderBy(x => x.Level).ToListAsync();
        Assert.Equal(VerificationDiscipline.System, migratedArtifacts.Single(x => x.Level == TestProcedureLevel.System).ArtifactDiscipline);
        Assert.Equal(VerificationArtifactKind.Procedure, migratedArtifacts.Single(x => x.Level == TestProcedureLevel.System).ArtifactKind);
        Assert.Equal(VerificationDiscipline.HighLevelSoftware, migratedArtifacts.Single(x => x.Level == TestProcedureLevel.HighLevel).ArtifactDiscipline);
        Assert.Equal(VerificationArtifactKind.Case, migratedArtifacts.Single(x => x.Level == TestProcedureLevel.HighLevel).ArtifactKind);
        var before = await db.ProjectLadderConfigurations.AsNoTracking().SingleAsync(x => x.Id == configurationId);
        Assert.Null(before.ActivationManifestVersion);
        var beforeHistory = await db.ProjectLadderConfigurationHistories.AsNoTracking()
            .Where(x => x.ConfigurationId == configurationId).OrderBy(x => x.Revision).ToListAsync();
        await db.Database.GetService<IMigrator>().MigrateAsync();
        var after = await db.ProjectLadderSteps.AsNoTracking().SingleAsync(x => x.Id == stepId);
        Assert.Equal(string.Empty, after.EnabledArtifactKindsValue);
        Assert.Equal(before.VerificationProfileSchemaVersion, (await db.ProjectLadderConfigurations.AsNoTracking().SingleAsync(x => x.Id == configurationId)).VerificationProfileSchemaVersion);

        // Exercise the neutral migration's Down and reapply path as well as an idempotent latest-Migrate call.
        await db.Database.GetService<IMigrator>().MigrateAsync("20260822045540_ExactLinkSuspectLifecycle");
        await db.Database.GetService<IMigrator>().MigrateAsync();
        var reapplied = await db.ProjectLadderSteps.AsNoTracking().SingleAsync(x => x.Id == stepId);
        Assert.Equal(string.Empty, reapplied.EnabledArtifactKindsValue);
        var reappliedArtifacts = await db.TestProcedures.AsNoTracking().ToListAsync();
        Assert.Equal(VerificationArtifactKind.Procedure,
            reappliedArtifacts.Single(x => x.Level == TestProcedureLevel.System).ArtifactKind);
        Assert.Equal(VerificationArtifactKind.Case,
            reappliedArtifacts.Single(x => x.Level == TestProcedureLevel.HighLevel).ArtifactKind);
        var reappliedHistory = await db.ProjectLadderConfigurationHistories.AsNoTracking()
            .Where(x => x.ConfigurationId == configurationId).OrderBy(x => x.Revision).ToListAsync();
        Assert.Equal(beforeHistory.Count, reappliedHistory.Count);
        Assert.Equal(beforeHistory.Select(x => (x.Revision, x.CanonicalSnapshot, x.SnapshotHash, x.SnapshotSchemaVersion)),
            reappliedHistory.Select(x => (x.Revision, x.CanonicalSnapshot, x.SnapshotHash, x.SnapshotSchemaVersion)));
    }

    [DisposablePostgresFact]
    public async Task Issue729_postgresql_profile_constraint_rejects_customer_interface_and_unknown_shapes()
    {
        var connection = QualificationConnectionOrSkip();
        await using var db = await ResetAtLatestAsync(connection);
        var now = DateTimeOffset.UtcNow;
        var program = new ProgramRecord("PG invalid profile program", "PGI"); var project = new ProjectRecord(program.Id, "PG invalid profile", "PG invalid profile software");
        db.AddRange(program, project); await db.SaveChangesAsync();
        var configuration = LegacyDefaultProjectLadderFactory.Create(project.Id, now); db.Add(configuration); await db.SaveChangesAsync();
        var stored = await db.ProjectLadderConfigurations.AsNoTracking().SingleAsync(x => x.ProjectId == project.Id);
        await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE \"project_ladder_steps\" SET \"CatalogueEntry\" = 'Customer', \"Capabilities\" = 2, \"EnabledArtifactKindsValue\" = 'Case' WHERE \"ConfigurationId\" = {stored.Id} AND \"Position\" = 1"));
        await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE \"project_ladder_steps\" SET \"CatalogueEntry\" = 'Interface', \"Capabilities\" = 2, \"EnabledArtifactKindsValue\" = 'Case' WHERE \"ConfigurationId\" = {stored.Id} AND \"Position\" = 1"));
        await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE \"project_ladder_steps\" SET \"CatalogueEntry\" = 'Unknown', \"Capabilities\" = 1, \"EnabledArtifactKindsValue\" = '' WHERE \"ConfigurationId\" = {stored.Id} AND \"Position\" = 1"));
    }

    [DisposablePostgresFact]
    public async Task Concurrent_postgresql_edit_and_first_content_have_one_atomic_winner()
    {
        var connection = QualificationConnectionOrSkip();
        await using (var setup = await ResetAtLatestAsync(connection))
        {
            var program = new ProgramRecord("PG race program", "PGR");
            var project = new ProjectRecord(program.Id, "PG race project", "PG race software");
            setup.AddRange(program, project);
            await setup.SaveChangesAsync();
            setup.ProjectLadderConfigurations.Add(LegacyDefaultProjectLadderFactory.Create(project.Id, DateTimeOffset.UtcNow));
            var release = new SoftwareRelease(project.Id, "1.0", true);
            setup.Add(release);
            await setup.SaveChangesAsync();

            await RunEditVsContentRaceAsync(project.Id, release.Id, connection);
        }
    }

    [Theory]
    [InlineData("Host=example.test;Port=55437;Database=aerolink_707_qualify")]
    [InlineData("Host=127.0.0.1;Port=55437;Database=unrelated_database")]
    [InlineData("Host=127.0.0.1;Port=54329;Database=aerolink_707_qualify")]
    public void Qualification_connection_rejects_non_disposable_targets_before_database_access(string connection)
    {
        var error = Assert.Throws<InvalidOperationException>(() => ValidateQualificationConnection(connection));
        Assert.Contains("Issue #707", error.Message, StringComparison.Ordinal);
    }

    private static async Task RunEditVsContentRaceAsync(Guid projectId, Guid releaseId, string connectionString)
    {
        var options = Options(connectionString);
        await using var edit = new AeroLinkDbContext(options);
        await using var content = new AeroLinkDbContext(options);
        var editConfiguration = await edit.ProjectLadderConfigurations
            .Include(x => x.Steps).Include(x => x.AllowedUpstream).SingleAsync(x => x.ProjectId == projectId);
        _ = await content.ProjectLadderConfigurations
            .Include(x => x.Steps).Include(x => x.AllowedUpstream).SingleAsync(x => x.ProjectId == projectId);
        editConfiguration.BeginDraftEdit(DateTimeOffset.UtcNow);
        var now = DateTimeOffset.UtcNow;
        var request = new SystemChangeRequest("SRCR-70721", 0, projectId, releaseId,
            "PG race content", "Problem", "Analysis", "Solution", "pg.race", now);
        request.AddRequirementChange("pg.race", "SYSR-00000021", 0, RequirementLevel.System,
            RequirementChangeKind.Introduce, "PG race content", "PG race content", "Review", now);
        content.Add(request);

        using var barrier = new Barrier(2);
        async Task<string> SaveEditAsync()
        {
            barrier.SignalAndWait();
            try { await edit.SaveChangesAsync(); return "edit"; }
            catch (DbUpdateConcurrencyException) { return "edit-lost"; }
        }
        async Task<string> SaveContentAsync()
        {
            barrier.SignalAndWait();
            try { await content.SaveChangesAsync(); return "content"; }
            catch (ProjectLadderSealConcurrencyException) { return "content-lost"; }
        }

        var outcomes = await Task.WhenAll(Task.Run(SaveEditAsync), Task.Run(SaveContentAsync));
        Assert.Single(outcomes, x => x is "edit" or "content");
        Assert.Contains(outcomes, x => x is "edit-lost" or "content-lost");

        await using var check = new AeroLinkDbContext(options);
        var configuration = await check.ProjectLadderConfigurations.SingleAsync(x => x.ProjectId == projectId);
        var contentWon = outcomes.Contains("content");
        Assert.Equal(contentWon, configuration.IsSealed);
        Assert.Equal(contentWon ? 1 : 0,
            await check.ProjectLadderConfigurationHistories.CountAsync(x => x.ProjectId == projectId));
        Assert.Equal(contentWon ? 1 : 0,
            await check.SystemChangeRequests.CountAsync(x => x.ProjectId == projectId));
        Assert.Equal(contentWon ? 1 : 0, await check.RequirementChanges.CountAsync());
    }

    private static async Task<AeroLinkDbContext> ResetAtLatestAsync(string connectionString)
    {
        var db = new AeroLinkDbContext(Options(connectionString));
        await db.Database.EnsureDeletedAsync();
        await db.Database.MigrateAsync();
        return db;
    }

    private static async Task<AeroLinkDbContext> MigrateToPreFeatureAsync(string connectionString)
    {
        var db = new AeroLinkDbContext(Options(connectionString));
        await db.Database.EnsureDeletedAsync();
        await db.Database.GetService<IMigrator>().MigrateAsync(PreFeatureMigration);
        return db;
    }

    private static DbContextOptions<AeroLinkDbContext> Options(string connectionString)
        => new DbContextOptionsBuilder<AeroLinkDbContext>().UseNpgsql(connectionString).Options;

    private static string QualificationConnectionOrSkip()
    {
        var connection = Environment.GetEnvironmentVariable("AEROLINK_MIGRATIONS_CONNECTION");
        return ValidateQualificationConnection(connection);
    }

    private static string ValidateQualificationConnection(string? connection)
    {
        if (string.IsNullOrWhiteSpace(connection))
        {
            throw new InvalidOperationException(
                "Issue #707 PostgreSQL qualification requires AEROLINK_MIGRATIONS_CONNECTION; the test should have been skipped during discovery.");
        }

        var builder = new NpgsqlConnectionStringBuilder(connection);
        var host = (builder.Host ?? string.Empty).Trim().Trim('[', ']');
        if (!string.Equals(host, "127.0.0.1", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Issue #707 PostgreSQL qualification requires a loopback host (localhost or 127.0.0.1).");
        }

        if (builder.Port == 54329)
            throw new InvalidOperationException("Issue #707 qualification refuses the protected PostgreSQL port 54329.");

        if (!string.Equals(builder.Database, "aerolink_707_qualify", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Issue #707 PostgreSQL qualification requires the dedicated database aerolink_707_qualify.");
        }

        return connection;
    }

    private sealed class DisposablePostgresFactAttribute : FactAttribute
    {
        public DisposablePostgresFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("AEROLINK_MIGRATIONS_CONNECTION")))
            {
                Skip =
                    "Issue #707 PostgreSQL qualification skipped: set AEROLINK_MIGRATIONS_CONNECTION to the dedicated disposable database.";
            }
        }
    }
}
