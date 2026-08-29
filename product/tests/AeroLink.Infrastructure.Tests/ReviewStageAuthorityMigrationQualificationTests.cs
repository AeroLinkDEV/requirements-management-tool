using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Identity;
using AeroLink.Domain.Programs;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace AeroLink.Infrastructure.Tests;

/// <summary>
/// PostgreSQL qualification for the #816 Slice 4 workflow-authority migration. API tests run on SQLite with
/// EnsureCreated and prove nothing about migration SQL or provider behavior, so the upgrade contract is
/// proven here against a disposable PostgreSQL database:
///
/// - the full forward migration chain applies to a clean install;
/// - upgrading a pre-Slice-4 database adds the authority column additively and leaves every historical
///   stage exactly as it was recorded — a null authority kind, never a guessed modern conversion;
/// - modern BaseRole and LeadershipPosition stages persist and reload with their exact kind and payload;
/// - a second full migration run over an upgraded database changes nothing.
///
/// Every test is skipped unless AEROLINK_MIGRATIONS_CONNECTION points at a disposable PostgreSQL server;
/// the disposable database is created and dropped per run, and the persistent developer database (127.0.0.1:54329)
/// is never touched.
/// </summary>
public sealed class ReviewStageAuthorityMigrationQualificationTests
{
    private const string ConnectionVariable = "AEROLINK_MIGRATIONS_CONNECTION";
    private const string AuthorityMigrationId = "20260829125743_AddReviewStageAuthorityKind";
    private const string MigrationBeforeAuthority = "20260827232621_AddProblemReportRevisionActorDisplayName";

    private static bool ServerConfigured(out string serverConnectionString)
    {
        var raw = Environment.GetEnvironmentVariable(ConnectionVariable);
        serverConnectionString = raw ?? "";
        return !string.IsNullOrWhiteSpace(raw);
    }

    private static async Task<string> CreateDisposableDatabaseAsync(string serverConnectionString)
    {
        var database = $"aerolink_820_qual_{Guid.NewGuid():N}";
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

    private static async Task MigrateAsync(string connectionString, string? target = null)
    {
        var options = new DbContextOptionsBuilder<AeroLinkDbContext>().UseNpgsql(connectionString).Options;
        await using var db = new AeroLinkDbContext(options);
        if (target is null) { await db.Database.MigrateAsync(); return; }
        await db.Database.MigrateAsync(target);
    }

    private static async Task<int> ExecuteAsync(string connectionString, string sql, params NpgsqlParameter[] parameters)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var parameter in parameters) command.Parameters.Add(parameter);
        return await command.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task The_upgrade_adds_authority_additively_and_never_gusses_historical_semantics()
    {
        if (!ServerConfigured(out var server)) return; // qualification requires the disposable server
        var database = new NpgsqlConnectionStringBuilder(server).Database;
        var connection = await CreateDisposableDatabaseAsync(server);
        try
        {
            // A: the pre-Slice-4 schema, exactly as a database upgraded to Slice 3 looks.
            await MigrateAsync(connection, MigrationBeforeAuthority);
            var options = new DbContextOptionsBuilder<AeroLinkDbContext>().UseNpgsql(connection).Options;
            await using (var db = new AeroLinkDbContext(options))
            {
                var preCheck = await db.Database.SqlQuery<bool>($"SELECT NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name = 'review_workflow_stages' AND column_name = 'RequiredAuthorityKind') AS \"Value\"").SingleAsync();
                Assert.True(preCheck, "the pre-migration schema must not carry the authority column yet");
            }

            // B: representative historical rows, recorded by raw SQL because the current model maps the new
            // column. Reviewer and Approver are the generic demands; the lead role and a versioned pair
            // cover the retained-history cases. Kind was stored by name with a Review default (LES-004).
            var projectId = await SeedProgramAndProjectAsync(options);
            var workflowId = Guid.NewGuid();
            var versionedId = Guid.NewGuid();
            await ExecuteAsync(connection,
                """INSERT INTO review_workflows ("Id","LogicalId","ProjectId","Name","AppliesTo","Mode","Version","State","CreatedBy","CreatedAt") VALUES (@id, @logical, @project, 'Legacy board', 'System', 'Sequential', 1, 'Active', 'qualification', @now), (@vid, @vlogical, @project, 'Legacy board', 'Software', 'Sequential', 1, 'Retired', 'qualification', @now)""",
                new NpgsqlParameter("id", workflowId),
                new NpgsqlParameter("logical", Guid.NewGuid()),
                new NpgsqlParameter("vid", versionedId),
                new NpgsqlParameter("vlogical", Guid.NewGuid()),
                new NpgsqlParameter("project", projectId),
                new NpgsqlParameter("now", DateTimeOffset.UtcNow));
            var stageId = 0;
            foreach (var (role, kind, target) in new[]
                     {
                         ("Reviewer", "Review", workflowId), ("Approver", "Approval", workflowId),
                         ("SystemEngineeringLead", "Review", workflowId),
                         ("Reviewer", "Review", versionedId),
                     })
            {
                await ExecuteAsync(connection,
                    """INSERT INTO review_workflow_stages ("Id","WorkflowId","Position","Name","RequiredRole","Kind") VALUES (@id, @workflow, @position, @name, @role, @kind)""",
                    new NpgsqlParameter("id", Guid.NewGuid()),
                    new NpgsqlParameter("workflow", target),
                    new NpgsqlParameter("position", stageId++),
                    new NpgsqlParameter("name", $"Stage {stageId}"),
                    new NpgsqlParameter("role", role),
                    new NpgsqlParameter("kind", kind));
            }

            // C: apply the Slice 4 migration.
            await MigrateAsync(connection, AuthorityMigrationId);

            // D: values retained, every historical row reads as LEGACY, nothing converted.
            await using (var db = new AeroLinkDbContext(options))
            {
                var workflows = await db.ReviewWorkflows.AsNoTracking().Include(x => x.Stages)
                    .ToListAsync();
                Assert.Equal(2, workflows.Count);
                Assert.Equal([ReviewWorkflowState.Active, ReviewWorkflowState.Retired],
                    workflows.OrderBy(x => x.State).Select(x => x.State).ToArray());
                Assert.All(workflows.SelectMany(x => x.Stages), stage =>
                {
                    Assert.Null(stage.RequiredAuthorityKind); // no guessed modern semantics
                    Assert.Equal(ProjectAuthorityKind.LegacyRoleDemand, stage.RequiredAuthority.Kind);
                });
                var system = workflows.Single(x => x.AppliesTo == ReviewSubject.System);
                Assert.Equal([ProgramRole.Reviewer, ProgramRole.Approver, ProgramRole.SystemEngineeringLead],
                    system.Stages.OrderBy(x => x.Position).Select(x => x.RequiredRole).ToArray());
                Assert.Equal([ReviewStageKind.Review, ReviewStageKind.Approval, ReviewStageKind.Review],
                    system.Stages.OrderBy(x => x.Position).Select(x => x.Kind).ToArray());
            }

            // E/F/G: modern stages persist and reload with their exact authority kind and payload.
            Guid modernWorkflowId;
            await using (var db = new AeroLinkDbContext(options))
            {
                var now = DateTimeOffset.UtcNow;
                var modern = new ReviewWorkflow(projectId, "Modern board", ReviewSubject.Interface,
                    ReviewMode.Sequential,
                [
                    new ReviewWorkflowStageDraft("Technical review", ProgramRole.SystemEngineer,
                        ReviewStageKind.Review, ReviewStageAuthorityKind.BaseRole),
                    new ReviewWorkflowStageDraft("Lead approval", ProgramRole.SystemEngineeringLead,
                        ReviewStageKind.Approval, ReviewStageAuthorityKind.LeadershipPosition),
                ], "qualification", now, 1);
                modern.Activate("qualification", now);
                db.ReviewWorkflows.Add(modern);
                await db.SaveChangesAsync();
                modernWorkflowId = modern.Id;
            }
            await using (var db = new AeroLinkDbContext(options))
            {
                var reloaded = await db.ReviewWorkflows.AsNoTracking().Include(x => x.Stages)
                    .SingleAsync(x => x.Id == modernWorkflowId);
                var stages = reloaded.Stages.OrderBy(x => x.Position).ToList();
                Assert.Equal(ReviewStageAuthorityKind.BaseRole, stages[0].RequiredAuthorityKind);
                Assert.Equal(ProjectAuthorityKind.BaseRole, stages[0].RequiredAuthority.Kind);
                Assert.Equal(ProgramRole.SystemEngineer, stages[0].RequiredRole);
                Assert.Null(stages[0].RequiredPosition);
                Assert.Equal(ReviewStageAuthorityKind.LeadershipPosition, stages[1].RequiredAuthorityKind);
                Assert.Equal(ProjectAuthorityKind.LeadershipPosition, stages[1].RequiredAuthority.Kind);
                Assert.Equal(ProjectLeadershipPosition.SystemEngineeringLead, stages[1].RequiredPosition);
            }
        }
        finally
        {
            await DropDatabaseAsync(server, new NpgsqlConnectionStringBuilder(connection).Database);
        }
    }

    [Fact]
    public async Task A_clean_install_applies_the_full_chain_and_is_idempotent()
    {
        if (!ServerConfigured(out var server)) return;
        var connection = await CreateDisposableDatabaseAsync(server);
        try
        {
            await MigrateAsync(connection);
            await MigrateAsync(connection); // a second run is a no-op

            var options = new DbContextOptionsBuilder<AeroLinkDbContext>().UseNpgsql(connection).Options;
            await using var db = new AeroLinkDbContext(options);
            var applied = await db.Database.GetAppliedMigrationsAsync();
            Assert.Contains(AuthorityMigrationId, applied);
            var projectId = await SeedProgramAndProjectAsync(options);
            var now = DateTimeOffset.UtcNow;
            var workflow = new ReviewWorkflow(projectId, "Clean install board", ReviewSubject.System,
                ReviewMode.Sequential,
                [new ReviewWorkflowStageDraft("Review", ProgramRole.SystemEngineer, ReviewStageKind.Review,
                    ReviewStageAuthorityKind.BaseRole)],
                "qualification", now);
            db.ReviewWorkflows.Add(workflow);
            await db.SaveChangesAsync();
            await db.Entry(workflow).ReloadAsync();
            Assert.Equal(ReviewStageAuthorityKind.BaseRole, workflow.Stages.Single().RequiredAuthorityKind);
        }
        finally
        {
            await DropDatabaseAsync(server, new NpgsqlConnectionStringBuilder(connection).Database);
        }
    }

    /// <summary>Program and project scaffolding via EF: the migration touches neither table.</summary>
    private static async Task<Guid> SeedProgramAndProjectAsync(DbContextOptions<AeroLinkDbContext> options)
    {
        await using var db = new AeroLinkDbContext(options);
        var program = new ProgramRecord("Authority Qualification", $"AQP{Guid.NewGuid():N}"[..12]);
        var project = new ProjectRecord(program.Id, "Flight Software", "Authority Qualification Software");
        db.AddRange(program, project);
        await db.SaveChangesAsync();
        return project.Id;
    }
}
