using AeroLink.Domain.Identity;
using AeroLink.Domain.Programs;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Npgsql;

namespace AeroLink.Infrastructure.Tests;

public sealed partial class ProjectSetupPostgresQualificationTests
{
    [DisposablePostgresFact]
    public async Task Inception_baseline_identity_migration_backfills_existing_drafts_before_unique_constraint()
    {
        var rawConnection = Environment.GetEnvironmentVariable("AEROLINK_MIGRATIONS_CONNECTION");
        if (string.IsNullOrWhiteSpace(rawConnection))
            throw new InvalidOperationException("Required project-setup PostgreSQL qualification needs an explicit disposable connection.");
        var server = ValidateServer(rawConnection);
        var databaseName = $"aerolink_1039_inception_upgrade_{Guid.NewGuid():N}";
        await CreateDatabaseAsync(server, databaseName);
        try
        {
            var connection = new NpgsqlConnectionStringBuilder(server) { Database = databaseName }.ConnectionString;
            var options = new DbContextOptionsBuilder<AeroLinkDbContext>().UseNpgsql(connection).Options;
            const string predecessor = "20260913231524_AddProjectSetupAndRepositoryVerificationFacts";
            await using (var migrate = new AeroLinkDbContext(options))
            {
                await migrate.Database.GetService<IMigrator>().MigrateAsync(predecessor);

                var now = DateTimeOffset.UtcNow;
                var firstAccount = new UserAccount("migration-owner-a", "Migration Owner A", "migration-owner-a@example.test",
                    "fixture-hash", now);
                var secondAccount = new UserAccount("migration-owner-b", "Migration Owner B", "migration-owner-b@example.test",
                    "fixture-hash", now);
                migrate.AddRange(firstAccount, secondAccount);
                await migrate.SaveChangesAsync();

                var first = new ProjectSetupDraft(firstAccount.Id, firstAccount.UserName, "Existing draft A");
                var second = new ProjectSetupDraft(secondAccount.Id, secondAccount.UserName, "Existing draft B");
                await InsertPreInceptionDraftAsync(migrate, first, now);
                await InsertPreInceptionDraftAsync(migrate, second, now);
            }

            await using (var upgrade = new AeroLinkDbContext(options))
            {
                // This is intentionally an upgrade with two pre-existing rows, not an empty-database migration.
                await upgrade.Database.MigrateAsync();
                await upgrade.Database.MigrateAsync();
                var drafts = await upgrade.ProjectSetupDrafts.AsNoTracking()
                    .OrderBy(x => x.ProjectName).ToListAsync();
                Assert.Equal(2, drafts.Count);
                Assert.Equal(2, drafts.Select(x => x.InceptionBaselineId).Distinct().Count());
                Assert.All(drafts, x => Assert.Equal(x.Id, x.InceptionBaselineId));
                Assert.Equal(new[] { "Existing draft A", "Existing draft B" }, drafts.Select(x => x.ProjectName));
            }
        }
        finally
        {
            await DropDatabaseAsync(server, databaseName);
        }
    }

    private static Task InsertPreInceptionDraftAsync(AeroLinkDbContext db, ProjectSetupDraft draft,
        DateTimeOffset now) => db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "project_setup_drafts" (
                "Id", "CreatorUserId", "CreatorUserName", "InternalProgramId", "InternalProgramName",
                "InternalProgramCode", "ProjectId", "InitialReleaseId", "State", "CurrentStep", "ProjectName",
                "SoftwareProduct", "StartKind", "SourceBaselineId", "SourceImportId", "InitialReleaseVersion",
                "InitialReleaseCanonicalIdentity", "SelectedCategoriesJson", "LadderJson", "ReviewRulesJson",
                "RepositoryJson", "MappingJson", "ReviewRulesAccepted", "ReviewRulesAcceptanceHash", "Version",
                "CreatedAt", "UpdatedAt", "LastSavedAt", "FinalizationStartedAt", "CompletedAt",
                "FinalizationOperationKey", "FinalizationResultJson", "CompletedProgramId", "CompletedProjectId",
                "CompletedReleaseId")
            VALUES ({draft.Id}, {draft.CreatorUserId}, {draft.CreatorUserName}, {draft.InternalProgramId},
                {draft.InternalProgramName}, {draft.InternalProgramCode}, {draft.ProjectId}, {draft.InitialReleaseId},
                {draft.State.ToString()}, {draft.CurrentStep.ToString()}, {draft.ProjectName}, {draft.SoftwareProduct},
                {null}, {null}, {null}, {""}, {""}, {draft.SelectedCategoriesJson}, {draft.LadderJson},
                {draft.ReviewRulesJson}, {draft.RepositoryJson}, {draft.MappingJson}, {false}, {null}, {draft.Version},
                {now}, {now}, {now}, {null}, {null}, {null}, {null}, {null}, {null}, {null});
            """);
}
