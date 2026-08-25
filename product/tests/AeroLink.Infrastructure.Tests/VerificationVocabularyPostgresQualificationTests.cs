using System.Net;
using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Hierarchy;
using AeroLink.Domain.Programs;
using AeroLink.Domain.Requirements;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace AeroLink.Infrastructure.Tests;

[CollectionDefinition("Issue701Postgres", DisableParallelization = true)]
public sealed class Issue701PostgresCollection : ICollectionFixture<object>;

/// <summary>
/// PostgreSQL qualification for the #701 verification-method vocabulary: clean install, the exact upgrade
/// from the pre-feature schema, and the two things the backfill must never do — invent vocabulary from the
/// values it finds, or rewrite a stored verification method.
///
/// SQLite API tests are created with EnsureCreated and so never exercise a migration; the raw SQL backfill
/// below is invisible to them. It runs here against a disposable PostgreSQL database, never the persistent
/// demo instance.
/// </summary>
[Collection("Issue701Postgres")]
public sealed class VerificationVocabularyPostgresQualificationTests
{
    /// <summary>The head of origin/main at the time #701 was cut: the exact schema an upgrade starts from.</summary>
    private const string PreFeatureMigration = "20260824043125_ExtendCaseProcedureSuspectLifecycle";
    private const string FeatureMigration = "20260824144129_AddProjectVerificationVocabulary";
    private const string DatabaseName = "aerolink_701_qualify";
    private static readonly string[] Founding = ["Test", "Analysis", "Inspection", "Demonstration"];

    [DisposablePostgresFact]
    public async Task Clean_install_creates_the_vocabulary_schema_with_no_configuration_evidence()
    {
        var connection = QualificationConnectionOrSkip();
        await using var db = await ResetAtLatestAsync(connection);

        // Pinned by identity: the upgrade path below starts from the migration immediately before this one,
        // and a rebased or regenerated migration must not silently change what that means. On the integrated
        // post-#701/#747 main, later #726 migrations sort after the vocabulary migration, so the vocabulary
        // migration must be APPLIED with its pre-feature predecessor immediately before it, and the #726
        // execution-cutover schema must sort last.
        var applied = await db.Database.GetAppliedMigrationsAsync();
        var appliedList = applied.ToList();
        var featureIndex = appliedList.IndexOf(FeatureMigration);
        Assert.True(featureIndex > 0, "The vocabulary migration must be applied.");
        Assert.Equal(PreFeatureMigration, appliedList[featureIndex - 1]);
        Assert.Equal("20260825114510_AddExecutionCutoverSchema", appliedList.Last());

        var columns = await ColumnsAsync(db);
        Assert.Contains("project_verification_vocabularies.ProjectId", columns);
        Assert.Contains("project_verification_vocabularies.Version", columns);
        Assert.Contains("project_verification_methods.DisplayValue", columns);
        Assert.Contains("project_verification_methods.NormalizedValue", columns);
        Assert.Contains("project_verification_methods.Position", columns);
        // A clean install has no projects, so it has no vocabularies: the backfill founds what exists, it
        // does not manufacture configuration for a database that has none.
        Assert.Empty(await db.ProjectVerificationVocabularies.AsNoTracking().ToListAsync());
        Assert.Empty(await db.ProjectVerificationMethods.AsNoTracking().ToListAsync());
    }

    [DisposablePostgresFact]
    public async Task A_project_created_on_a_clean_install_carries_a_persisted_vocabulary()
    {
        var connection = QualificationConnectionOrSkip();
        await using var db = await ResetAtLatestAsync(connection);
        var program = new ProgramRecord("PG founding program", "PGF");
        var project = new ProjectRecord(program.Id, "PG founding project", "PG founding software");
        db.AddRange(program, project, ProjectVerificationVocabulary.Founding(project.Id, DateTimeOffset.UtcNow));
        await db.SaveChangesAsync();

        var vocabulary = await db.ProjectVerificationVocabularies.AsNoTracking().Include(x => x.Methods)
            .SingleAsync(x => x.ProjectId == project.Id);
        Assert.Equal(Founding, vocabulary.OrderedValues);
        Assert.Equal([1, 2, 3, 4], vocabulary.Methods.OrderBy(x => x.Position).Select(x => x.Position));
        Assert.All(vocabulary.Methods, method => Assert.Equal(project.Id, method.ProjectId));
        Assert.All(vocabulary.Methods, method => Assert.Equal(vocabulary.Id, method.VocabularyId));
    }

    [DisposablePostgresFact]
    public async Task Upgrading_the_exact_pre_feature_schema_founds_every_project_and_rewrites_nothing()
    {
        var connection = QualificationConnectionOrSkip();
        await using var db = await MigrateToPreFeatureAsync(connection);
        var seeded = await SeedPreFeatureAsync(db);

        await db.Database.GetService<IMigrator>().MigrateAsync();

        // Every project the database held gets exactly one vocabulary, whatever its requirements say.
        var vocabularies = await db.ProjectVerificationVocabularies.AsNoTracking().Include(x => x.Methods)
            .OrderBy(x => x.CreatedAt).ToListAsync();
        Assert.Equal(2, vocabularies.Count);
        Assert.Equal(new[] { seeded.FragmentedProjectId, seeded.CleanProjectId }.Order(),
            vocabularies.Select(x => x.ProjectId).Order());
        Assert.All(vocabularies, vocabulary =>
        {
            Assert.Equal(Founding, vocabulary.OrderedValues);
            Assert.Equal([1, 2, 3, 4], vocabulary.Methods.OrderBy(x => x.Position).Select(x => x.Position));
            Assert.Equal(["test", "analysis", "inspection", "demonstration"],
                vocabulary.Methods.OrderBy(x => x.Position).Select(x => x.NormalizedValue));
            Assert.All(vocabulary.Methods, method => Assert.Equal(vocabulary.ProjectId, method.ProjectId));
            Assert.All(vocabulary.Methods, method => Assert.Equal(vocabulary.Id, method.VocabularyId));
            Assert.Equal(1, vocabulary.Version);
        });

        // The fragmented project held "Test", "test" and "Testing". None of those three became configured
        // vocabulary -- blessing the variants would make the report empty by defining the defect as correct --
        // and none of the stored values moved.
        Assert.DoesNotContain("test", vocabularies.SelectMany(x => x.OrderedValues));
        Assert.DoesNotContain("Testing", vocabularies.SelectMany(x => x.OrderedValues));
        Assert.Equal(["Test", "Testing", "test"],
            (await db.RequirementChanges.AsNoTracking().Select(x => x.VerificationMethod).ToListAsync())
                .Order(StringComparer.Ordinal));
        Assert.Equal(["Test", "Testing"],
            (await db.RequirementRevisions.AsNoTracking().Select(x => x.VerificationMethod).ToListAsync())
                .Order(StringComparer.Ordinal));
    }

    [DisposablePostgresFact]
    public async Task The_backfill_is_idempotent_and_survives_a_down_and_reapply()
    {
        var connection = QualificationConnectionOrSkip();
        await using var db = await MigrateToPreFeatureAsync(connection);
        var seeded = await SeedPreFeatureAsync(db);
        await db.Database.GetService<IMigrator>().MigrateAsync();

        var before = await VocabularyFingerprintAsync(db);
        var storedBefore = await StoredMethodsFingerprintAsync(db);

        // Re-running the latest migration is the startup path on an already-upgraded database.
        await db.Database.GetService<IMigrator>().MigrateAsync();
        Assert.Equal(before, await VocabularyFingerprintAsync(db));

        // Down removes the configuration and nothing else; reapplying founds it again from the same rule.
        await db.Database.GetService<IMigrator>().MigrateAsync(PreFeatureMigration);
        Assert.Equal(storedBefore, await StoredMethodsFingerprintAsync(db));
        await db.Database.GetService<IMigrator>().MigrateAsync();

        var after = await db.ProjectVerificationVocabularies.AsNoTracking().Include(x => x.Methods).ToListAsync();
        Assert.Equal(2, after.Count);
        Assert.All(after, vocabulary => Assert.Equal(Founding, vocabulary.OrderedValues));
        Assert.Equal(new[] { seeded.FragmentedProjectId, seeded.CleanProjectId }.Order(),
            after.Select(x => x.ProjectId).Order());
        Assert.Equal(storedBefore, await StoredMethodsFingerprintAsync(db));
    }

    [DisposablePostgresFact]
    public async Task Uniqueness_foreign_keys_and_the_position_check_are_enforced_by_postgresql()
    {
        var connection = QualificationConnectionOrSkip();
        await using var db = await ResetAtLatestAsync(connection);
        var now = DateTimeOffset.UtcNow;
        var program = new ProgramRecord("PG constraint program", "PGK");
        var project = new ProjectRecord(program.Id, "PG constraint project", "PG constraint software");
        var vocabulary = ProjectVerificationVocabulary.Founding(project.Id, now);
        db.AddRange(program, project, vocabulary);
        await db.SaveChangesAsync();

        // Two spellings of one method cannot coexist, however they are written.
        await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "project_verification_methods"
                ("Id","VocabularyId","ProjectId","Position","DisplayValue","NormalizedValue","CreatedAt","UpdatedAt","Version")
            VALUES ({Guid.NewGuid()},{vocabulary.Id},{project.Id},{5},{"test"},{"test"},{now},{now},{1L});
            """));
        // One vocabulary per project.
        await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "project_verification_vocabularies" ("Id","ProjectId","CreatedAt","UpdatedAt","Version")
            VALUES ({Guid.NewGuid()},{project.Id},{now},{now},{1L});
            """));
        // A member cannot belong to a vocabulary that does not exist.
        await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "project_verification_methods"
                ("Id","VocabularyId","ProjectId","Position","DisplayValue","NormalizedValue","CreatedAt","UpdatedAt","Version")
            VALUES ({Guid.NewGuid()},{Guid.NewGuid()},{project.Id},{6},{"Similarity"},{"similarity"},{now},{now},{1L});
            """));
        // Positions are one-based configured order.
        await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "project_verification_methods"
                ("Id","VocabularyId","ProjectId","Position","DisplayValue","NormalizedValue","CreatedAt","UpdatedAt","Version")
            VALUES ({Guid.NewGuid()},{vocabulary.Id},{project.Id},{0},{"Similarity"},{"similarity"},{now},{now},{1L});
            """));
    }

    [DisposablePostgresFact]
    public async Task An_optimistic_conflict_is_refused_and_the_configuration_is_left_intact()
    {
        var connection = QualificationConnectionOrSkip();
        var options = Options(connection);
        await using (var setup = await ResetAtLatestAsync(connection))
        {
            var program = new ProgramRecord("PG concurrency program", "PGQ");
            var project = new ProjectRecord(program.Id, "PG concurrency project", "PG concurrency software");
            setup.AddRange(program, project, ProjectVerificationVocabulary.Founding(project.Id, DateTimeOffset.UtcNow));
            await setup.SaveChangesAsync();

            await using var first = new AeroLinkDbContext(options);
            await using var second = new AeroLinkDbContext(options);
            var resolver = new FixedLadderPolicyResolver(LegacyLadderPolicy.Instance);
            var winner = await new ProjectVerificationVocabularyService(first, resolver).ReplaceAsync(project.Id,
                ["Test", "Analysis", "Inspection", "Demonstration", "Similarity"], 1,
                "Add similarity", "pg.first", "127.0.0.1", DateTimeOffset.UtcNow);
            Assert.Equal(VerificationVocabularyEditResultKind.Success, winner.Kind);

            var loser = await new ProjectVerificationVocabularyService(second, resolver).ReplaceAsync(project.Id,
                ["Test"], 1, "Narrow to test", "pg.second", "127.0.0.1", DateTimeOffset.UtcNow);
            Assert.Equal(VerificationVocabularyEditResultKind.Conflict, loser.Kind);

            await using var check = new AeroLinkDbContext(options);
            var stored = await check.ProjectVerificationVocabularies.AsNoTracking().Include(x => x.Methods)
                .SingleAsync(x => x.ProjectId == project.Id);
            Assert.Equal(["Test", "Analysis", "Inspection", "Demonstration", "Similarity"], stored.OrderedValues);
            Assert.Equal(2, stored.Version);
        }
    }

    [DisposablePostgresFact]
    public async Task A_refused_edit_rolls_back_leaving_no_partial_configuration()
    {
        var connection = QualificationConnectionOrSkip();
        await using var db = await ResetAtLatestAsync(connection);
        var now = DateTimeOffset.UtcNow;
        var program = new ProgramRecord("PG rollback program", "PGB");
        var project = new ProjectRecord(program.Id, "PG rollback project", "PG rollback software");
        db.AddRange(program, project, ProjectVerificationVocabulary.Founding(project.Id, now));
        await db.SaveChangesAsync();

        var service = new ProjectVerificationVocabularyService(db,
            new FixedLadderPolicyResolver(LegacyLadderPolicy.Instance));
        var refused = await service.ReplaceAsync(project.Id, ["Test", "Similarity", "similarity"], 1,
            "Two spellings of one method", "pg.actor", "127.0.0.1", now);
        Assert.Equal(VerificationVocabularyEditResultKind.Invalid, refused.Kind);

        await using var check = new AeroLinkDbContext(Options(connection));
        var stored = await check.ProjectVerificationVocabularies.AsNoTracking().Include(x => x.Methods)
            .SingleAsync(x => x.ProjectId == project.Id);
        Assert.Equal(Founding, stored.OrderedValues);
        Assert.Equal(1, stored.Version);
        Assert.Empty(await check.SecurityAuditEvents.AsNoTracking()
            .Where(x => x.EventType == "VerificationVocabularyConfigured").ToListAsync());
    }

    [Theory]
    [InlineData("Host=example.test;Port=54701;Database=aerolink_701_qualify")]
    [InlineData("Host=127.0.0.1;Port=54701;Database=aerolink")]
    [InlineData("Host=127.0.0.1;Port=54329;Database=aerolink_701_qualify")]
    public void Qualification_connection_rejects_non_disposable_targets_before_database_access(string connection)
    {
        var error = Assert.Throws<InvalidOperationException>(() => ValidateQualificationConnection(connection));
        Assert.Contains("Issue #701", error.Message, StringComparison.Ordinal);
    }

    private sealed record PreFeatureSeed(Guid FragmentedProjectId, Guid CleanProjectId);

    /// <summary>
    /// A pre-feature database holding exactly the fragmentation #701 exists to correct: one project whose
    /// requirements say "Test", "test" and "Testing", and one that is already consistent.
    /// </summary>
    private static async Task<PreFeatureSeed> SeedPreFeatureAsync(AeroLinkDbContext db)
    {
        var now = DateTimeOffset.UtcNow;
        var programId = Guid.NewGuid();
        var fragmented = Guid.NewGuid();
        var clean = Guid.NewGuid();
        var releaseId = Guid.NewGuid();
        var requestId = Guid.NewGuid();
        var artifactId = Guid.NewGuid();
        var baselineId = Guid.NewGuid();

        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "programs" ("Id","Name","Code") VALUES ({programId},{"PG 701 program"},{"PG701"});
            INSERT INTO "projects" ("Id","ProgramId","Name","SoftwareProduct")
                VALUES ({fragmented},{programId},{"PG fragmented project"},{"PG fragmented software"}),
                       ({clean},{programId},{"PG clean project"},{"PG clean software"});
            INSERT INTO "software_releases" ("Id","ProjectId","Version","IsReleased")
                VALUES ({releaseId},{fragmented},{"1.0"},{false});
            INSERT INTO "candidate_baselines"
                ("Id","BaseNumber","Revision","ProjectId","ReleaseId","Name","CreatedAt","State","UpdatedAt","Version")
                VALUES ({baselineId},{"SW-01.00"},{0},{fragmented},{releaseId},{"Historical baseline"},{now},{"Released"},{now},{1L});
            INSERT INTO "system_change_requests"
                ("Id","BaseNumber","Revision","ProjectId","TargetReleaseId","OriginReleaseId","Title","Problem","Analysis","Solution",
                 "ProblemRich","AnalysisRich","SolutionRich","AuthorId","Type","State","CreatedAt","UpdatedAt","Version","SnapshotContractVersion")
                VALUES ({requestId},{"SRCR-70190"},{0},{fragmented},{releaseId},{releaseId},{"Historical fragmentation"},
                        {"P"},{"A"},{"S"},{"{}"},{"{}"},{"{}"},{"pg.author"},{"System"},{"Draft"},{now},{now},{1L},{2});
            INSERT INTO "requirement_changes"
                ("Id","ChangeRequestId","BaseNumber","Revision","Level","Kind","Statement","Rationale","VerificationMethod",
                 "RichText","AttributesJson","ImpactDispositionJson","ProposedUpstreamRevisionIdsJson")
                VALUES ({Guid.NewGuid()},{requestId},{"SYSR-701901"},{0},{"System"},{"Introduce"},{"S1"},{"R1"},{"Test"},{"{}"},{"{}"},{"{}"},{"[]"}),
                       ({Guid.NewGuid()},{requestId},{"SYSR-701902"},{0},{"System"},{"Introduce"},{"S2"},{"R2"},{"test"},{"{}"},{"{}"},{"{}"},{"[]"}),
                       ({Guid.NewGuid()},{requestId},{"SYSR-701903"},{0},{"System"},{"Introduce"},{"S3"},{"R3"},{"Testing"},{"{}"},{"{}"},{"{}"},{"[]"});
            INSERT INTO "requirements" ("Id","ProjectId","BaseNumber","Level","CreatedAt")
                VALUES ({artifactId},{fragmented},{"SYSR-701901"},{"System"},{now});
            INSERT INTO "requirement_revisions"
                ("Id","ArtifactId","Revision","Statement","Rationale","VerificationMethod","State","OriginKind",
                 "SourceChangeRequestId","EffectiveBaselineId","CreatedAt","ParentKind","DerivedRationale","ParentRevisionIdsJson")
                VALUES ({Guid.NewGuid()},{artifactId},{0},{"S1"},{"R1"},{"Test"},{"Active"},{"ChangeRequest"},{requestId},{baselineId},{now},{"Unspecified"},{""},{"[]"}),
                       ({Guid.NewGuid()},{artifactId},{1},{"S1"},{"R1"},{"Testing"},{"Active"},{"ChangeRequest"},{requestId},{baselineId},{now},{"Unspecified"},{""},{"[]"});
            """);
        return new(fragmented, clean);
    }

    private static async Task<string> VocabularyFingerprintAsync(AeroLinkDbContext db)
    {
        var rows = await db.ProjectVerificationMethods.AsNoTracking()
            .OrderBy(x => x.ProjectId).ThenBy(x => x.Position)
            .Select(x => new { x.Id, x.ProjectId, x.VocabularyId, x.Position, x.DisplayValue, x.NormalizedValue })
            .ToListAsync();
        return string.Join("|", rows.Select(x =>
            $"{x.ProjectId:D}:{x.VocabularyId:D}:{x.Position}:{x.DisplayValue}:{x.NormalizedValue}"));
    }

    private static async Task<string> StoredMethodsFingerprintAsync(AeroLinkDbContext db)
    {
        var changes = (await db.RequirementChanges.AsNoTracking()
            .Select(x => x.BaseNumber + "=" + x.VerificationMethod).ToListAsync()).Order(StringComparer.Ordinal);
        var revisions = (await db.RequirementRevisions.AsNoTracking()
            .Select(x => x.Revision + "=" + x.VerificationMethod).ToListAsync()).Order(StringComparer.Ordinal);
        return string.Join("|", changes) + "||" + string.Join("|", revisions);
    }

    private static async Task<HashSet<string>> ColumnsAsync(AeroLinkDbContext db)
    {
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = """
            SELECT table_name, column_name FROM information_schema.columns
            WHERE table_schema = 'public'
              AND table_name IN ('project_verification_vocabularies','project_verification_methods')
            """;
        await db.Database.OpenConnectionAsync();
        var columns = new HashSet<string>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) columns.Add($"{reader.GetString(0)}.{reader.GetString(1)}");
        return columns;
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

    private static DbContextOptions<AeroLinkDbContext> Options(string connectionString) =>
        new DbContextOptionsBuilder<AeroLinkDbContext>().UseNpgsql(connectionString).Options;

    private static string QualificationConnectionOrSkip() =>
        ValidateQualificationConnection(Environment.GetEnvironmentVariable("AEROLINK_MIGRATIONS_CONNECTION"));

    /// <summary>
    /// The guard that keeps this qualification off the persistent demo database. Loopback only, never the
    /// protected 54329, and only the dedicated disposable database this feature owns.
    /// </summary>
    private static string ValidateQualificationConnection(string? connection)
    {
        if (string.IsNullOrWhiteSpace(connection))
            throw new InvalidOperationException(
                "Issue #701 PostgreSQL qualification requires AEROLINK_MIGRATIONS_CONNECTION; the test should have been skipped during discovery.");
        var builder = new NpgsqlConnectionStringBuilder(connection);
        var host = (builder.Host ?? "").Trim().Trim('[', ']');
        var loopback = string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
            || (IPAddress.TryParse(host, out var address) && IPAddress.IsLoopback(address));
        if (!loopback)
            throw new InvalidOperationException("Issue #701 PostgreSQL qualification requires a loopback host.");
        if (builder.Port == 54329)
            throw new InvalidOperationException("Issue #701 qualification refuses the protected PostgreSQL port 54329.");
        if (!string.Equals(builder.Database, DatabaseName, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Issue #701 qualification requires the dedicated database {DatabaseName}.");
        return connection;
    }

    private sealed class FixedLadderPolicyResolver(ILadderPolicy policy) : IProjectLadderPolicyResolver
    {
        public Task<ILadderPolicy> ResolveAsync(Guid projectId, CancellationToken ct = default) =>
            Task.FromResult(policy);
    }

    private sealed class DisposablePostgresFactAttribute : FactAttribute
    {
        public DisposablePostgresFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("AEROLINK_MIGRATIONS_CONNECTION")))
                Skip = "Issue #701 PostgreSQL qualification skipped: set AEROLINK_MIGRATIONS_CONNECTION to the dedicated disposable database.";
        }
    }

}
