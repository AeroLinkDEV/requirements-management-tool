using System.Data;
using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Integrations;
using AeroLink.Domain.Programs;
using AeroLink.Domain.Requirements;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace AeroLink.Infrastructure.Tests;

public sealed partial class ProjectSetupPostgresQualificationTests
{
    [DisposablePostgresFact]
    public async Task Repository_verification_migration_preserves_legacy_null_facts_and_new_identity_snapshots()
    {
        var rawConnection = Environment.GetEnvironmentVariable("AEROLINK_MIGRATIONS_CONNECTION");
        if (string.IsNullOrWhiteSpace(rawConnection))
            throw new InvalidOperationException("Required project-setup PostgreSQL qualification needs an explicit disposable connection.");
        var server = ValidateServer(rawConnection);
        var databaseName = $"aerolink_1039_repository_upgrade_{Guid.NewGuid():N}";
        await CreateDatabaseAsync(server, databaseName);
        try
        {
            var connection = new NpgsqlConnectionStringBuilder(server) { Database = databaseName }.ConnectionString;
            var options = new DbContextOptionsBuilder<AeroLinkDbContext>().UseNpgsql(connection).Options;
            const string predecessor = "20260914012557_AddProjectInceptionSourceStaging";
            var programId = Guid.NewGuid();
            var projectId = Guid.NewGuid();
            var releaseId = Guid.NewGuid();
            var baselineId = Guid.NewGuid();
            var changeId = Guid.NewGuid();
            var artifactId = Guid.NewGuid();
            var revisionId = Guid.NewGuid();
            var legacyRecordId = Guid.NewGuid();
            var now = DateTimeOffset.UtcNow;

            await using (var migrate = new AeroLinkDbContext(options))
            {
                await migrate.Database.GetService<IMigrator>().MigrateAsync(predecessor);
                await using var sql = new NpgsqlConnection(connection);
                await sql.OpenAsync();
                await InsertLegacyGraphAsync(sql, programId, projectId, releaseId, baselineId, changeId,
                    artifactId, revisionId, legacyRecordId, now);
            }

            await using (var upgrade = new AeroLinkDbContext(options))
            {
                // This is deliberately an upgrade from a populated pre-RS03 schema, not an empty-database run.
                await upgrade.Database.MigrateAsync();
                await upgrade.Database.MigrateAsync();

                var legacy = await upgrade.CodeTraceabilityRecords.AsNoTracking()
                    .SingleAsync(x => x.Id == legacyRecordId);
                Assert.Null(legacy.VerifiedRemoteProjectId);
                Assert.Null(legacy.VerifiedRepositoryEndpoint);
                Assert.Null(legacy.VerifiedRepositoryPath);
                Assert.Null(legacy.RepositoryConfigurationVersion);
                Assert.Null(legacy.RepositoryVerifiedAt);
                Assert.Null(legacy.RepositoryVerifiedBy);

                var release = new SoftwareRelease(projectId, "1.1", false);
                var repository = new ProjectRepositoryConfiguration(projectId, ProjectRepositorySetupMode.ConnectNow,
                    "GitLab", "https://git.example.test/group/project", "admin", now);
                repository.RecordVerification("admin", now, 123, "group/project");
                var mapped = new CodeTraceabilityRecord(projectId, release.Id, artifactId, revisionId,
                    CodeTraceDisposition.GitLabMerge, "group/project", "!1", "Mapped source identity",
                    "https://git.example.test/group/project/-/merge_requests/1", new string('a', 40), now, "", false,
                    "admin", now, repository);
                upgrade.AddRange(release, repository, mapped);
                await upgrade.SaveChangesAsync();

                var snapshot = await upgrade.CodeTraceabilityRecords.AsNoTracking()
                    .SingleAsync(x => x.Id == mapped.Id);
                Assert.Equal(123, snapshot.VerifiedRemoteProjectId);
                Assert.Equal("https://git.example.test/group/project", snapshot.VerifiedRepositoryEndpoint);
                Assert.Equal("group/project", snapshot.VerifiedRepositoryPath);
                Assert.Equal(repository.Version, snapshot.RepositoryConfigurationVersion);
                Assert.Equal("admin", snapshot.RepositoryVerifiedBy);
                Assert.NotNull(snapshot.RepositoryVerifiedAt);
            }
        }
        finally
        {
            await DropDatabaseAsync(server, databaseName);
        }
    }

    private static async Task InsertLegacyGraphAsync(NpgsqlConnection connection, Guid programId, Guid projectId,
        Guid releaseId, Guid baselineId, Guid changeId, Guid artifactId, Guid revisionId, Guid codeRecordId,
        DateTimeOffset now)
    {
        await ExecuteAsync(connection, """
            INSERT INTO "programs" ("Id", "Name", "Code") VALUES (@programId, 'Legacy repository program', 'LEGACY-REPO');
            INSERT INTO "projects" ("Id", "ProgramId", "Name", "SoftwareProduct")
                VALUES (@projectId, @programId, 'Legacy repository project', 'Legacy repository product');
            INSERT INTO "software_releases" ("Id", "ProjectId", "Version", "IsReleased", "CanonicalIdentity")
                VALUES (@releaseId, @projectId, '1.0', FALSE, NULL);
            INSERT INTO "system_change_requests" (
                "Id", "BaseNumber", "Revision", "ProjectId", "TargetReleaseId", "Title", "Problem", "Analysis",
                "Solution", "AuthorId", "State", "CreatedAt", "UpdatedAt", "AnalysisRich", "ProblemRich",
                "SolutionRich", "OriginReleaseId", "Type", "SnapshotContractVersion", "UpstreamAnswerAffirmed")
                VALUES (@changeId, 'LEGACY-SCR-1', 0, @projectId, @releaseId, 'Legacy source change', 'Problem',
                    'Analysis', 'Solution', 'legacy.author', 'Approved', @now, @now, 'Analysis', 'Problem', 'Solution',
                    @releaseId, 'Software', 1, FALSE);
            INSERT INTO "candidate_baselines" (
                "Id", "BaseNumber", "Revision", "ProjectId", "ReleaseId", "PredecessorBaselineId", "Name",
                "CreatedAt", "UpdatedAt", "State", "Version", "FrozenAt")
                VALUES (@baselineId, 'SW-99.01', 0, @projectId, @releaseId, NULL, 'Legacy source baseline', @now, @now,
                    'Frozen', 1, @now);
            INSERT INTO "requirements" ("Id", "ProjectId", "BaseNumber", "Level", "CreatedAt")
                VALUES (@artifactId, @projectId, 'SYSR-990001', 'System', @now);
            INSERT INTO "requirement_revisions" (
                "Id", "ArtifactId", "Revision", "Statement", "Rationale", "VerificationMethod", "State",
                "SourceChangeRequestId", "EffectiveBaselineId", "OriginKind", "ParentKind", "ParentRevisionIdsJson",
                "DerivedRationale", "CreatedAt")
                VALUES (@revisionId, @artifactId, 0, 'Legacy exact statement', 'Legacy rationale', 'Analysis', 'Active',
                    @changeId, @baselineId, 'ChangeRequest', 'Primary', '[]', '', @now);
            INSERT INTO "code_traceability_records" (
                "Id", "ProjectId", "ReleaseId", "RequirementArtifactId", "RequirementRevisionId", "Disposition",
                "RepositoryPath", "MergeRequestReference", "MergeRequestTitle", "MergeRequestUrl", "MergeCommitSha",
                "MergedAt", "NoCodeChangeRationale", "IsDemonstration", "RecordedBy", "RecordedAt")
                VALUES (@codeRecordId, @projectId, @releaseId, @artifactId, @revisionId, 'GitLabMerge', 'group/project',
                    '!legacy', 'Legacy exact merge', 'https://gitlab.example.test/group/project/-/merge_requests/1',
                    repeat('b', 40), @now, '', FALSE, 'legacy.admin', @now);
            """, ("programId", programId), ("projectId", projectId), ("releaseId", releaseId),
            ("changeId", changeId), ("baselineId", baselineId), ("artifactId", artifactId),
            ("revisionId", revisionId), ("codeRecordId", codeRecordId), ("now", now));
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql,
        params (string Name, object Value)[] values)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in values) command.Parameters.AddWithValue(name, value);
        await command.ExecuteNonQueryAsync();
    }
}
