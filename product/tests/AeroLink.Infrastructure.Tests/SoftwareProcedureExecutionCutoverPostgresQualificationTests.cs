using System.Net;
using AeroLink.Domain.Baselines;
using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Hierarchy;
using AeroLink.Domain.Identity;
using AeroLink.Domain.Programs;
using AeroLink.Domain.Releases;
using AeroLink.Domain.Verification;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace AeroLink.Infrastructure.Tests;

/// <summary>
/// #726 governed cutover on disposable PostgreSQL (never 54329). The suite qualifies a clean current
/// install, an exact pre-feature-shaped database upgrade, deterministic migration-generated Procedures,
/// reference rebinding, multiple Case revisions, the Stored/Draft/Active/Retired state matrix, idempotent
/// rerun, fail-closed rollback, and concurrent-writer behavior.
/// </summary>
[CollectionDefinition("Issue726Postgres", DisableParallelization = true)]
public sealed class Issue726PostgresCollection : ICollectionFixture<object>;

[Collection("Issue726Postgres")]
public sealed class SoftwareProcedureExecutionCutoverPostgresQualificationTests
{
    private const string DatabaseName = "aerolink_726_qualify";
    private const string ServerDatabase = "postgres";
    private const string PreFeatureMigration = "20260824043125_ExtendCaseProcedureSuspectLifecycle";

    [DisposablePostgresFact]
    public async Task Clean_install_cutover_is_idempotent_rolls_back_and_serializes_concurrent_writers()
    {
        var server = ValidateQualificationConnection(
            Environment.GetEnvironmentVariable("AEROLINK_MIGRATIONS_CONNECTION")!);
        await EnsureDatabaseAsync(server, DatabaseName);
        var connection = new NpgsqlConnectionStringBuilder(server) { Database = DatabaseName }.ConnectionString;

        await using (var db = await DatabaseAsync(connection))
        {
            var seed = await SeedAsync(db);
            var (legacy, typed) = CutoverRegistrations();
            // Fail-closed refusal BEFORE any cutover: a missing Procedure-capable execution consumer refuses
            // with no partial Procedures, links, rebindings, or activation events.
            var refusal = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                new SoftwareProcedureExecutionCutoverAuthority(db, legacy,
                    Array.Empty<IVerificationArtifactConsumerRegistration>()).EnsureCompletedAsync());
            Assert.Contains("refused", refusal.Message);
            Assert.Equal(0, await db.TestProcedures.AsNoTracking()
                .CountAsync(x => x.ProjectId == seed.ProjectId
                    && x.ArtifactKind == VerificationArtifactKind.Procedure));
            Assert.False(await db.SecurityAuditEvents.AsNoTracking()
                .AnyAsync(x => x.EventType.StartsWith("VerificationExecutionCutover")));

            var authority = new SoftwareProcedureExecutionCutoverAuthority(db, legacy, typed);
            var result = await authority.EnsureCompletedAsync();
            Assert.Equal(1, result.ProjectsUpgraded);
            Assert.Equal(1, result.ProceduresGenerated);
            Assert.Equal(1, result.ExecutionsRebound);
            Assert.Equal(1, result.TestSetEntriesRebound);
            Assert.Equal(1, result.BaselineSelectionsRebound);

            var procedure = await db.TestProcedures.AsNoTracking()
                .SingleAsync(x => x.ProjectId == seed.ProjectId
                    && x.ArtifactKind == VerificationArtifactKind.Procedure);
            var procedureRevision = await db.TestProcedureRevisions.AsNoTracking()
                .SingleAsync(x => x.ProcedureId == procedure.Id);
            Assert.Equal(VerificationProcedureParentKind.Allocated, procedureRevision.ParentKind);
            Assert.Equal("aerolink-migration", procedureRevision.AuthorId);
            Assert.Equal("Scenario steps", procedureRevision.OrderedSteps);
            Assert.Equal(procedureRevision.Id, (await db.TestExecutions.AsNoTracking()
                .SingleAsync(x => x.Id == seed.ExecutionId)).ProcedureRevisionId);
            Assert.Equal(procedureRevision.Id, (await db.BuildTestSetEntries.AsNoTracking()
                .SingleAsync(x => x.Id == seed.TestSetEntryId)).ProcedureRevisionId);
            Assert.Equal(procedure.Id, (await db.BaselineTestProcedures.AsNoTracking()
                .SingleAsync(x => x.Id == seed.BaselineSelectionId)).ProcedureId);

            var rerun = await authority.EnsureCompletedAsync();
            Assert.Equal(0, rerun.ProceduresGenerated);
            Assert.Equal(1, await db.TestProcedures.AsNoTracking()
                .CountAsync(x => x.ProjectId == seed.ProjectId
                    && x.ArtifactKind == VerificationArtifactKind.Procedure));
            Assert.Equal(1, await db.TestCaseProcedureLinks.AsNoTracking().CountAsync());
        }

        // Concurrent writers on a separate, fresh database: no Completed marker exists yet, so the two
        // authorities race; exactly one may generate Procedures.
        var concurrentName = DatabaseName + "_concurrent";
        await EnsureDatabaseAsync(server, concurrentName);
        var concurrentConnection = new NpgsqlConnectionStringBuilder(connection)
        {
            Database = concurrentName
        }.ConnectionString;
        await using (var first = await DatabaseAsync(concurrentConnection))
        await using (var second = await DatabaseAsync(concurrentConnection))
        {
            var concurrentSeed = await SeedAsync(first);
            var (legacy, typed) = CutoverRegistrations();
            var parallel = await Task.WhenAll(
                Task.Run(() => CatchAsync(() =>
                    new SoftwareProcedureExecutionCutoverAuthority(first, legacy, typed)
                        .EnsureCompletedAsync())),
                Task.Run(() => CatchAsync(() =>
                    new SoftwareProcedureExecutionCutoverAuthority(second, legacy, typed)
                        .EnsureCompletedAsync())));
            Assert.Equal(1, parallel.Sum(x => x.ProceduresGenerated));
            Assert.Equal(1, parallel.Count(x => x.ProceduresGenerated > 0));

            // The loser must leave NO partial state: the persisted database has exactly one winner outcome.
            await using var check = await DatabaseAsync(concurrentConnection);
            var projectId = await check.Projects.AsNoTracking()
                .Select(x => x.Id).FirstAsync();
            var procedureCount = await check.TestProcedures.AsNoTracking().CountAsync(
                x => x.ProjectId == projectId && x.ArtifactKind == VerificationArtifactKind.Procedure);
            Assert.Equal(1, procedureCount);
            var procedureId = await check.TestProcedures.AsNoTracking()
                .Where(x => x.ProjectId == projectId
                    && x.ArtifactKind == VerificationArtifactKind.Procedure)
                .Select(x => x.Id).SingleAsync();
            Assert.Equal(1, await check.TestProcedureRevisions.AsNoTracking()
                .CountAsync(x => x.ProcedureId == procedureId));
            Assert.Equal(1, await check.TestCaseProcedureLinks.AsNoTracking().CountAsync());
            Assert.Equal(1, await check.TestExecutions.AsNoTracking()
                .CountAsync(x => x.ProjectId == projectId
                    && x.ProcedureRevisionId != Guid.Empty));
            Assert.Equal(1, await check.BuildTestSetEntries.AsNoTracking()
                .CountAsync(x => x.ProcedureRevisionId != Guid.Empty));
            Assert.Equal(1, await check.BaselineTestProcedures.AsNoTracking()
                .CountAsync(x => x.ProcedureId == procedureId));
            var procedureRevisionId = await check.TestProcedureRevisions.AsNoTracking()
                .Where(x => x.ProcedureId == procedureId).Select(x => x.Id).SingleAsync();
            Assert.Equal(procedureRevisionId, (await check.TestExecutions.AsNoTracking()
                .SingleAsync(x => x.ProjectId == projectId)).ProcedureRevisionId);
            Assert.Equal(procedureRevisionId, (await check.BuildTestSetEntries.AsNoTracking()
                .SingleAsync()).ProcedureRevisionId);
            Assert.Equal(1, await check.SecurityAuditEvents.AsNoTracking()
                .CountAsync(x => x.EventType == "VerificationExecutionCutover.SoftwareProcedures.v1.ProjectUpgraded"
                    && x.Target == $"Project:{projectId}"));
            Assert.Equal(1, await check.SecurityAuditEvents.AsNoTracking()
                .CountAsync(x => x.EventType == "VerificationExecutionCutover.SoftwareProcedures.v1.Completed"));
            Assert.Equal(0, await check.SecurityAuditEvents.AsNoTracking()
                .CountAsync(x => x.EventType == "VerificationExecutionCutover.SoftwareProcedures.v1.ProcedureGenerated"
                    && !x.Target.StartsWith("TestCaseProcedureLink:")));
            Assert.Equal(concurrentSeed.CaseRevisionId, (await check.TestCaseProcedureLinks.AsNoTracking()
                .SingleAsync()).CaseRevisionId);
        }
    }

    [DisposablePostgresFact]
    public async Task Exact_pre_feature_shaped_database_upgrade_runs_the_governed_cutover()
    {
        var server = ValidateQualificationConnection(
            Environment.GetEnvironmentVariable("AEROLINK_MIGRATIONS_CONNECTION")!);
        var databaseName = DatabaseName + "_prefeature";
        await EnsureDatabaseAsync(server, databaseName);
        var connection = new NpgsqlConnectionStringBuilder(server) { Database = databaseName }.ConnectionString;

        // #726 adds no EF migration: the "upgrade" is the governed runtime authority. Qualify the exact
        // pre-feature shape by migrating explicitly to the last pre-#726 migration, seeding the legacy
        // Case-only state, then completing migrations (a no-op) and running the platform cutover.
        await using (var db = await DatabaseAsync(connection, PreFeatureMigration))
        {
            var seed = await SeedAsync(db);
            await db.Database.MigrateAsync();
            var (legacy, typed) = CutoverRegistrations();
            var result = await new SoftwareProcedureExecutionCutoverAuthority(db, legacy, typed)
                .EnsureCompletedAsync();
            Assert.Equal(1, result.ProjectsUpgraded);
            Assert.Equal(1, result.ProceduresGenerated);
            Assert.Equal(1, await db.TestProcedures.AsNoTracking()
                .CountAsync(x => x.ProjectId == seed.ProjectId
                    && x.ArtifactKind == VerificationArtifactKind.Procedure));
            Assert.Equal(1, await db.TestCaseProcedureLinks.AsNoTracking().CountAsync());
            var configuration = await db.ProjectLadderConfigurations.AsNoTracking()
                .SingleAsync(x => x.ProjectId == seed.ProjectId);
            Assert.Equal(ProjectLadderConfigurationClassification.NonDefault, configuration.Classification);
            Assert.Equal(ProjectLadderConfigurationState.Active, configuration.State);
            Assert.True(configuration.IsSealed);
            Assert.Equal("aerolink-migration", configuration.ActivatedBy);
        }
    }

    [DisposablePostgresFact]
    public async Task Multiple_case_revisions_migrate_to_one_exact_procedure_artifact_on_postgres()
    {
        var server = ValidateQualificationConnection(
            Environment.GetEnvironmentVariable("AEROLINK_MIGRATIONS_CONNECTION")!);
        var databaseName = DatabaseName + "_multirev";
        await EnsureDatabaseAsync(server, databaseName);
        var connection = new NpgsqlConnectionStringBuilder(server) { Database = databaseName }.ConnectionString;
        await using var db = await DatabaseAsync(connection);
        var seed = await SeedTwoRevisionAsync(db);
        var (legacy, typed) = CutoverRegistrations();
        var result = await new SoftwareProcedureExecutionCutoverAuthority(db, legacy, typed)
            .EnsureCompletedAsync();
        Assert.Equal(2, result.ProceduresGenerated);
        Assert.Equal(2, result.ExecutionsRebound);
        Assert.Equal(2, result.TestSetEntriesRebound);
        Assert.Equal(2, result.BaselineSelectionsRebound);
        Assert.Equal(1, await db.TestProcedures.AsNoTracking()
            .CountAsync(x => x.ProjectId == seed.ProjectId
                && x.ArtifactKind == VerificationArtifactKind.Procedure));
        var procedureId = await db.TestProcedures.AsNoTracking()
            .Where(x => x.ProjectId == seed.ProjectId
                && x.ArtifactKind == VerificationArtifactKind.Procedure)
            .Select(x => x.Id).SingleAsync();
        var revisions = await db.TestProcedureRevisions.AsNoTracking()
            .Where(x => x.ProcedureId == procedureId).OrderBy(x => x.Revision).ToListAsync();
        Assert.Equal(2, revisions.Count);
        Assert.Equal([0, 1], revisions.Select(x => x.Revision).ToArray());
        Assert.Equal(seed.FirstBaselineId, revisions[0].EffectiveBaselineId);
        Assert.Equal(seed.SecondBaselineId, revisions[1].EffectiveBaselineId);
        var links = await db.TestCaseProcedureLinks.AsNoTracking().ToListAsync();
        Assert.Equal(2, links.Count);
        Assert.Equal(seed.FirstCaseRevisionId, links.Single(x => x.ProcedureRevisionId == revisions[0].Id).CaseRevisionId);
        Assert.Equal(seed.SecondCaseRevisionId, links.Single(x => x.ProcedureRevisionId == revisions[1].Id).CaseRevisionId);
        Assert.Equal(2, await db.BaselineTestProcedures.AsNoTracking()
            .CountAsync(x => x.ProcedureId == procedureId));
        Assert.Equal(2, await db.TestExecutions.AsNoTracking()
            .CountAsync(x => x.ProjectId == seed.ProjectId
                && x.ProcedureRevisionId == revisions[0].Id
                || x.ProcedureRevisionId == revisions[1].Id));
        var rerun = await new SoftwareProcedureExecutionCutoverAuthority(db, legacy, typed)
            .EnsureCompletedAsync();
        Assert.Equal(0, rerun.ProceduresGenerated);
        Assert.Equal(2, await db.TestCaseProcedureLinks.AsNoTracking().CountAsync());
        Assert.Equal(2, await db.TestProcedureRevisions.AsNoTracking()
            .CountAsync(x => x.ProcedureId == procedureId));
    }

    [DisposablePostgresFact]
    public async Task Stored_draft_active_and_retired_configuration_matrix_follows_the_state_contract()
    {
        var server = ValidateQualificationConnection(
            Environment.GetEnvironmentVariable("AEROLINK_MIGRATIONS_CONNECTION")!);
        var databaseName = DatabaseName + "_matrix";
        await EnsureDatabaseAsync(server, databaseName);
        var connection = new NpgsqlConnectionStringBuilder(server) { Database = databaseName }.ConnectionString;
        await using var db = await DatabaseAsync(connection);
        var now = DateTimeOffset.UtcNow;

        var program = new ProgramRecord("Matrix PG Program", "MPG");
        db.Add(program);
        var storedProject = await MatrixProjectAsync(db, program, "Stored", now,
            state: MatrixState.StoredLegacyDefault);
        var draftProject = await MatrixProjectAsync(db, program, "Draft", now,
            state: MatrixState.SealedAuthoredDraft);
        var activeProject = await MatrixProjectAsync(db, program, "Active", now,
            state: MatrixState.NonDefaultActiveCaseOnly);
        var retiredProject = await MatrixProjectAsync(db, program, "Retired", now,
            state: MatrixState.Retired);
        await db.SaveChangesAsync();

        var (legacy, typed) = CutoverRegistrations();
        var result = await new SoftwareProcedureExecutionCutoverAuthority(db, legacy, typed)
            .EnsureCompletedAsync();
        Assert.Equal(1, result.ProjectsUpgraded);

        var upgraded = await db.ProjectLadderConfigurations.AsNoTracking()
            .SingleAsync(x => x.ProjectId == storedProject);
        Assert.Equal(ProjectLadderConfigurationState.Active, upgraded.State);
        Assert.Equal(ProjectLadderConfigurationClassification.NonDefault, upgraded.Classification);
        Assert.True(upgraded.IsSealed);
        Assert.Equal("aerolink-migration", upgraded.ActivatedBy);

        var draft = await db.ProjectLadderConfigurations.AsNoTracking()
            .SingleAsync(x => x.ProjectId == draftProject);
        Assert.Equal(ProjectLadderConfigurationState.Draft, draft.State);
        Assert.True(draft.IsSealed);
        Assert.Equal(0, await db.TestProcedures.AsNoTracking()
            .CountAsync(x => x.ProjectId == draftProject
                && x.ArtifactKind == VerificationArtifactKind.Procedure));

        var active = await db.ProjectLadderConfigurations.AsNoTracking()
            .SingleAsync(x => x.ProjectId == activeProject);
        Assert.Equal(ProjectLadderConfigurationState.Active, active.State);
        Assert.Equal("project.owner", active.ActivatedBy);
        Assert.Equal(0, await db.TestProcedures.AsNoTracking()
            .CountAsync(x => x.ProjectId == activeProject
                && x.ArtifactKind == VerificationArtifactKind.Procedure));

        var retired = await db.ProjectLadderConfigurations.AsNoTracking()
            .SingleAsync(x => x.ProjectId == retiredProject);
        Assert.Equal(ProjectLadderConfigurationState.Retired, retired.State);
        Assert.Equal("project.owner", retired.RetiredBy);
        Assert.False(await db.SecurityAuditEvents.AsNoTracking()
            .AnyAsync(x => x.EventType.StartsWith("VerificationExecutionCutover")
                && x.Target == $"Project:{retiredProject}"));
    }

    private enum MatrixState { StoredLegacyDefault, SealedAuthoredDraft, NonDefaultActiveCaseOnly, Retired }

    private static async Task<Guid> MatrixProjectAsync(AeroLinkDbContext db, ProgramRecord program,
        string name, DateTimeOffset now, MatrixState state)
    {
        var project = new ProjectRecord(program.Id, name, name + " Product");
        db.Add(project);
        if (state == MatrixState.StoredLegacyDefault)
        {
            var storedConfiguration = LegacyDefaultProjectLadderFactory.Create(project.Id, now);
            db.ProjectLadderConfigurations.Add(storedConfiguration);
            await db.SaveChangesAsync();
            var seal = await new ProjectLadderSealAuthority(db).SealAsync(project.Id,
                LadderBoundContentCatalog.Current.First().Id, $"{name}-content", "test.sealer", now);
            Assert.Equal(ProjectLadderSealResultKind.Sealed, seal.Kind);
            await db.SaveChangesAsync();
            return project.Id;
        }
        if (state == MatrixState.Retired)
        {
            var retired = (ProjectLadderConfiguration)System.Runtime.CompilerServices.RuntimeHelpers
                .GetUninitializedObject(typeof(ProjectLadderConfiguration));
            SetPrivate(retired, nameof(ProjectLadderConfiguration.Id), Guid.NewGuid());
            SetPrivate(retired, nameof(ProjectLadderConfiguration.ProjectId), project.Id);
            SetPrivate(retired, nameof(ProjectLadderConfiguration.Classification),
                ProjectLadderConfigurationClassification.NonDefault);
            SetPrivate(retired, nameof(ProjectLadderConfiguration.State),
                ProjectLadderConfigurationState.Retired);
            SetPrivate(retired, nameof(ProjectLadderConfiguration.IsSealed), true);
            SetPrivate(retired, nameof(ProjectLadderConfiguration.CreatedAt), now);
            SetPrivate(retired, nameof(ProjectLadderConfiguration.UpdatedAt), now);
            SetPrivate(retired, nameof(ProjectLadderConfiguration.Version), 1L);
            SetPrivate(retired, nameof(ProjectLadderConfiguration.VerificationProfileSchemaVersion), 2);
            SetPrivate(retired, nameof(ProjectLadderConfiguration.ActivatedAt), now);
            SetPrivate(retired, nameof(ProjectLadderConfiguration.ActivatedBy), "project.owner");
            SetPrivate(retired, nameof(ProjectLadderConfiguration.RetiredAt), now);
            SetPrivate(retired, nameof(ProjectLadderConfiguration.RetiredBy), "project.owner");
            SetPrivate(retired, nameof(ProjectLadderConfiguration.SealedAt), now);
            SetPrivate(retired, nameof(ProjectLadderConfiguration.SealedBy), "project.owner");
            SetPrivate(retired, nameof(ProjectLadderConfiguration.SealedContentKind), "test-case");
            SetPrivate(retired, nameof(ProjectLadderConfiguration.SealedContentIdentity), "retired-case");
            SetPrivate(retired, nameof(ProjectLadderConfiguration.ActivationManifestVersion),
                LadderConsumerManifestCatalog.VersionV2);
            SetPrivate(retired, nameof(ProjectLadderConfiguration.ActivationManifestHash), new string('0', 64));
            db.ProjectLadderConfigurations.Add(retired);
            await db.SaveChangesAsync();
            return project.Id;
        }

        var configuration = ProjectLadderConfiguration.CreateDraft(project.Id, now);
        var steps = new List<ProjectLadderStep>();
        foreach (var (level, position) in LegacyLadderPolicy.Instance.OrderedLevels.Select((x, i) => (x, i + 1)))
        {
            var kinds = level == RequirementLevel.System
                ? new[] { VerificationArtifactKind.Procedure }
                : new[] { VerificationArtifactKind.Case };
            var step = new ProjectLadderStep(configuration.Id, project.Id, level, position,
                LegacyLadderPolicy.Instance.Definition(level).Capabilities, now, kinds);
            configuration.Steps.Add(step);
            steps.Add(step);
        }
        configuration.AllowedUpstream.Add(new ProjectLadderAllowedUpstream(
            configuration.Id, project.Id, steps[0].Id, steps[1].Id, now));
        configuration.AllowedUpstream.Add(new ProjectLadderAllowedUpstream(
            configuration.Id, project.Id, steps[1].Id, steps[2].Id, now));
        db.ProjectLadderConfigurations.Add(configuration);
        await db.SaveChangesAsync();
        var sealResult = await new ProjectLadderSealAuthority(db).SealAsync(project.Id,
            LadderBoundContentCatalog.Current.First().Id, $"{name}-content", "test.sealer", now);
        Assert.Equal(ProjectLadderSealResultKind.Sealed, sealResult.Kind);
        if (state == MatrixState.NonDefaultActiveCaseOnly)
        {
            configuration.Activate("project.owner", now, LadderConsumerManifestCatalog.VersionV2,
                new string('0', 64));
        }
        await db.SaveChangesAsync();
        return project.Id;
    }

    private static void SetPrivate(ProjectLadderConfiguration configuration, string propertyName, object? value) =>
        typeof(ProjectLadderConfiguration).GetProperty(propertyName)!
            .GetSetMethod(nonPublic: true)!
            .Invoke(configuration, [value]);

    private static async Task EnsureDatabaseAsync(string connection, string databaseName)
    {
        var builder = new NpgsqlConnectionStringBuilder(connection) { Database = ServerDatabase };
        await using var server = new NpgsqlConnection(builder.ConnectionString);
        await server.OpenAsync();
        await using var command = server.CreateCommand();
        // The qualification database is disposable by definition: start every run from a clean database so
        // leftover rows from a previous attempt cannot collide with this run's fixture.
        command.CommandText = $"DROP DATABASE IF EXISTS \"{databaseName}\" WITH (FORCE)";
        await command.ExecuteNonQueryAsync();
        command.CommandText = $"CREATE DATABASE \"{databaseName}\"";
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<AeroLinkDbContext> DatabaseAsync(string connection,
        string? targetMigration = null)
    {
        var options = new DbContextOptionsBuilder<AeroLinkDbContext>().UseNpgsql(connection).Options;
        var db = new AeroLinkDbContext(options);
        if (targetMigration is null) await db.Database.MigrateAsync();
        else await db.Database.MigrateAsync(targetMigration);
        return db;
    }

    private sealed record Seed(AeroLinkDbContext Db, Guid ProjectId, Guid ReleaseId, Guid BaselineId,
        Guid CaseRevisionId, Guid ExecutionId, Guid TestSetEntryId, Guid BaselineSelectionId);

    private static async Task<Seed> SeedAsync(AeroLinkDbContext db)
    {
        var now = DateTimeOffset.UtcNow;
        var tag = Guid.NewGuid().ToString("N")[..8];
        var program = new ProgramRecord("Cutover PG Program", $"CUP{tag}");
        var project = new ProjectRecord(program.Id, "Flight Software", "Cutover PG Software");
        var release = new SoftwareRelease(project.Id, "1.6", false);
        var baseline = new CandidateBaseline("SW-01.60", 0, project.Id, release.Id, null,
            "Candidate", "cm.test", now);
        db.AddRange(program, project, release, baseline);
        var configuration = LegacyDefaultProjectLadderFactory.Create(project.Id, now);
        db.ProjectLadderConfigurations.Add(configuration);
        await db.SaveChangesAsync();
        var contentKind = LadderBoundContentCatalog.Current.First().Id;
        var seal = await new ProjectLadderSealAuthority(db).SealAsync(project.Id, contentKind,
            "test-content", "test.sealer", now);
        Assert.Equal(ProjectLadderSealResultKind.Sealed, seal.Kind);
        await db.SaveChangesAsync();

        var caseArtifact = new TestProcedure(project.Id, $"HLRTC-{Random.Shared.Next(100000, 999999)}",
            "Oceanic sequencing case",
            "test.engineer", now, TestProcedureLevel.HighLevel);
        var caseRevision = new TestProcedureRevision(caseArtifact.Id, 0,
            "Verify oceanic sequencing", "Logical preconditions", "Scenario steps", "Pass criteria",
            TestProcedureState.Approved, "test.engineer", now);
        db.AddRange(caseArtifact, caseRevision);
        await db.SaveChangesAsync();
        var execution = new TestExecution(project.Id, caseRevision.Id, null, null, TestOutcome.Pass,
            "test.engineer", "Rig A", "Human determination", "evidence/a.json", now, now, release.Id);
        var set = new BuildTestSet(project.Id, release.Id, TestChangeReviewDiscipline.HighLevelSoftware, now);
        set.Include("test.lead", caseRevision.Id, TestSelectionReason.Chosen, "", now);
        var selection = new BaselineTestProcedureSelection(baseline.Id, caseArtifact.Id, caseRevision.Id);
        db.AddRange(execution, set, selection);
        await db.SaveChangesAsync();
        return new Seed(db, project.Id, release.Id, baseline.Id, caseRevision.Id,
            execution.Id, set.Entries.Single().Id, selection.Id);
    }

    private sealed record TwoRevisionSeed(AeroLinkDbContext Db, Guid ProjectId, Guid ReleaseId,
        Guid FirstBaselineId, Guid SecondBaselineId, Guid FirstCaseRevisionId, Guid SecondCaseRevisionId);

    private static async Task<TwoRevisionSeed> SeedTwoRevisionAsync(AeroLinkDbContext db)
    {
        var now = DateTimeOffset.UtcNow;
        var tag = Guid.NewGuid().ToString("N")[..8];
        var program = new ProgramRecord("Two Revision PG Program", $"TRP{tag}");
        var project = new ProjectRecord(program.Id, "Two Revision Software", "Two Revision PG Product");
        var release = new SoftwareRelease(project.Id, "2.0", false);
        var firstBaseline = new CandidateBaseline("SW-02.00", 0, project.Id, release.Id, null,
            "First candidate", "cm.test", now);
        var secondBaseline = new CandidateBaseline("SW-02.01", 0, project.Id, release.Id, firstBaseline.Id,
            "Second candidate", "cm.test", now.AddDays(1));
        db.AddRange(program, project, release, firstBaseline, secondBaseline);
        var configuration = LegacyDefaultProjectLadderFactory.Create(project.Id, now);
        db.ProjectLadderConfigurations.Add(configuration);
        await db.SaveChangesAsync();
        var seal = await new ProjectLadderSealAuthority(db).SealAsync(project.Id,
            LadderBoundContentCatalog.Current.First().Id, "two-revision-content", "test.sealer", now);
        Assert.Equal(ProjectLadderSealResultKind.Sealed, seal.Kind);
        await db.SaveChangesAsync();

        var caseArtifact = new TestProcedure(project.Id, $"HLRTC-{Random.Shared.Next(100000, 999999)}",
            "Two revision sequencing case", "test.engineer", now, TestProcedureLevel.HighLevel);
        var firstRevision = new TestProcedureRevision(caseArtifact.Id, 0,
            "Verify sequencing v1", "Logical preconditions", "Scenario steps v1", "Pass criteria v1",
            TestProcedureState.Approved, "test.engineer", now, effectiveBaselineId: firstBaseline.Id);
        var secondRevision = new TestProcedureRevision(caseArtifact.Id, 1,
            "Verify sequencing v2", "Logical preconditions v2", "Scenario steps v2", "Pass criteria v2",
            TestProcedureState.Approved, "test.engineer", now.AddDays(1),
            effectiveBaselineId: secondBaseline.Id);
        var firstExecution = new TestExecution(project.Id, firstRevision.Id, null, null, TestOutcome.Pass,
            "test.engineer", "Rig A", "Human determination", "evidence/a.json", now, now, release.Id);
        var secondExecution = new TestExecution(project.Id, secondRevision.Id, null, null, TestOutcome.Pass,
            "test.engineer", "Rig B", "Human determination", "evidence/b.json", now.AddDays(2),
            now.AddDays(2), release.Id);
        var set = new BuildTestSet(project.Id, release.Id, TestChangeReviewDiscipline.HighLevelSoftware, now);
        set.Include("test.lead", firstRevision.Id, TestSelectionReason.Chosen, "", now);
        set.Include("test.lead", secondRevision.Id, TestSelectionReason.Chosen, "", now);
        var firstSelection = new BaselineTestProcedureSelection(firstBaseline.Id, caseArtifact.Id, firstRevision.Id);
        var secondSelection = new BaselineTestProcedureSelection(secondBaseline.Id, caseArtifact.Id, secondRevision.Id);
        db.AddRange(caseArtifact, firstRevision, secondRevision, firstExecution, secondExecution, set,
            firstSelection, secondSelection);
        await db.SaveChangesAsync();
        return new TwoRevisionSeed(db, project.Id, release.Id, firstBaseline.Id, secondBaseline.Id,
            firstRevision.Id, secondRevision.Id);
    }

    private static (IReadOnlyList<ILadderConsumerRegistration> Legacy,
        IReadOnlyList<IVerificationArtifactConsumerRegistration> Typed) CutoverRegistrations() =>
        SoftwareProcedureExecutionCutoverTests.FullRegistrations();

    private static string ValidateQualificationConnection(string connection)
    {
        var builder = new NpgsqlConnectionStringBuilder(connection);
        var host = (builder.Host ?? string.Empty).Trim().Trim('[', ']');
        var loopback = string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
            || (IPAddress.TryParse(host, out var address) && IPAddress.IsLoopback(address));
        if (!loopback) throw new InvalidOperationException("#726 PostgreSQL qualification requires a loopback host.");
        if (builder.Port == 54329) throw new InvalidOperationException("#726 qualification refuses port 54329.");
        return connection;
    }

    private static async Task<SoftwareProcedureCutoverResult> CatchAsync(
        Func<Task<SoftwareProcedureCutoverResult>> operation)
    {
        try { return await operation(); }
        catch (InvalidOperationException ex)
            when (ex.Message.Contains(
                "Another ladder edit, seal, or platform upgrade was saved during the Procedure cutover",
                StringComparison.Ordinal))
        {
            // The documented concurrency-loser outcome: the winner committed its optimistic-version write
            // first. Any OTHER exception is a real defect and must surface.
            return new(0, 0, 0, 0, 0, 0);
        }
    }

    private sealed class DisposablePostgresFactAttribute : FactAttribute
    {
        public DisposablePostgresFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("AEROLINK_MIGRATIONS_CONNECTION")))
                Skip = "Issue #726 PostgreSQL qualification skipped: set AEROLINK_MIGRATIONS_CONNECTION to the dedicated disposable database.";
        }
    }
}
