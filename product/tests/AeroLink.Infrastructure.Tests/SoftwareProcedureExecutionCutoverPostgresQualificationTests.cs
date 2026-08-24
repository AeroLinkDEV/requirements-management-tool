using System.Net;
using AeroLink.Domain.Baselines;
using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Hierarchy;
using AeroLink.Domain.Identity;
using AeroLink.Domain.Programs;
using AeroLink.Domain.Releases;
using AeroLink.Domain.Traceability;
using AeroLink.Domain.Verification;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using System.IO.Compression;
using System.Text;
using System.Text.Json;

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
            Assert.Equal(result, rerun);
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
            Assert.Equal(1, await check.GovernedMigrationCompletions.AsNoTracking()
                .CountAsync(x => x.Marker == "VerificationExecutionCutover.SoftwareProcedures.v1"));
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
        Assert.Equal(result, rerun);
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

    [DisposablePostgresFact]
    public async Task Retired_case_revisions_migrate_on_postgres_without_an_active_claim()
    {
        var server = ValidateQualificationConnection(
            Environment.GetEnvironmentVariable("AEROLINK_MIGRATIONS_CONNECTION")!);
        var databaseName = DatabaseName + "_retiredcases";
        await EnsureDatabaseAsync(server, databaseName);
        var connection = new NpgsqlConnectionStringBuilder(server) { Database = databaseName }.ConnectionString;
        await using var db = await DatabaseAsync(connection);
        var now = DateTimeOffset.UtcNow;
        var tag = Guid.NewGuid().ToString("N")[..8];
        var program = new ProgramRecord("Retired PG Program", $"RPG{tag}");
        var project = new ProjectRecord(program.Id, "Retired Case Software", "Retired Case PG Product");
        var release = new SoftwareRelease(project.Id, "1.0", false);
        var baseline = new CandidateBaseline("SW-01.00", 0, project.Id, release.Id, null,
            "Candidate", "cm.test", now);
        db.AddRange(program, project, release, baseline);
        var configuration = LegacyDefaultProjectLadderFactory.Create(project.Id, now);
        db.ProjectLadderConfigurations.Add(configuration);
        await db.SaveChangesAsync();
        var seal = await new ProjectLadderSealAuthority(db).SealAsync(project.Id,
            LadderBoundContentCatalog.Current.First().Id, "retired-content", "test.sealer", now);
        Assert.Equal(ProjectLadderSealResultKind.Sealed, seal.Kind);
        await db.SaveChangesAsync();

        var caseArtifact = new TestProcedure(project.Id, $"HLRTC-{Random.Shared.Next(100000, 999999)}",
            "Retired sequencing case", "test.engineer", now, TestProcedureLevel.HighLevel);
        var retiredRevision = new TestProcedureRevision(caseArtifact.Id, 0, "", "", "", "",
            TestProcedureState.Retired, "test.engineer", now);
        var activeRevision = new TestProcedureRevision(caseArtifact.Id, 1,
            "Verify sequencing v2", "Logical preconditions", "Scenario steps v2", "Pass criteria v2",
            TestProcedureState.Approved, "test.engineer", now.AddDays(1),
            effectiveBaselineId: baseline.Id);
        var scr = new SystemChangeRequest("SRCR-00729", 0, project.Id, release.Id,
            "Baseline authority", "P", "A", "S", "author", now);
        scr.AddRequirementChange("author", "SYSR-000729", 0, RequirementLevel.System,
            RequirementChangeKind.Introduce, "The system shall retain the controlled fixture behavior.",
            "Baseline fixture authority.", "Analysis", now);
        scr.SubmitForReview("author", [new ApproverSelection("reviewer", "Reviewer")], now);
        scr.ApproveActiveStage("reviewer", now);
        db.AddRange(scr, caseArtifact, retiredRevision, activeRevision,
            new BaselineTestProcedureSelection(baseline.Id, caseArtifact.Id, activeRevision.Id));
        baseline.Select(scr, "cm.test", now);
        baseline.Freeze("cm.test", now);
        baseline.MarkRequirementsMaterialized("cm.test", new string('a', 64), 0, now);
        baseline.MarkTestProceduresMaterialized("cm.test", new string('b', 64), 1, now);
        await db.SaveChangesAsync();

        var (legacy, typed) = CutoverRegistrations();
        var result = await new SoftwareProcedureExecutionCutoverAuthority(db, legacy, typed)
            .EnsureCompletedAsync();
        Assert.Equal(1, result.ProjectsUpgraded);
        Assert.Equal(2, result.ProceduresGenerated);
        var procedure = await db.TestProcedures.AsNoTracking()
            .SingleAsync(x => x.ProjectId == project.Id
                && x.ArtifactKind == VerificationArtifactKind.Procedure);
        var revisions = await db.TestProcedureRevisions.AsNoTracking()
            .Where(x => x.ProcedureId == procedure.Id).OrderBy(x => x.Revision).ToListAsync();
        Assert.Equal([0, 1], revisions.Select(x => x.Revision).ToArray());
        Assert.Equal(TestProcedureState.Retired, revisions[0].State);
        Assert.Equal(TestProcedureState.Approved, revisions[1].State);
        Assert.All(revisions, revision => Assert.Equal("aerolink-migration", revision.AuthorId));
        var link = Assert.Single(await db.TestCaseProcedureLinks.AsNoTracking().ToListAsync());
        Assert.Equal(activeRevision.Id, link.CaseRevisionId);
        Assert.Equal(revisions[1].Id, link.ProcedureRevisionId);
        var rerun = await new SoftwareProcedureExecutionCutoverAuthority(db, legacy, typed)
            .EnsureCompletedAsync();
        Assert.Equal(result, rerun);
        Assert.Equal(2, await db.TestProcedureRevisions.AsNoTracking()
            .CountAsync(x => x.ProcedureId == procedure.Id));
    }

    [DisposablePostgresFact]
    public async Task Concurrent_recovery_claim_after_persisted_project_work_keeps_one_completion()
    {
        // Exact crash-recovery state from the Codex finding: per-project cutover work is already persisted,
        // the global Completed marker is absent, and two startup instances race. The unique marker row must
        // make the claim atomic so exactly one completion remains and totals stay consistent.
        var server = ValidateQualificationConnection(
            Environment.GetEnvironmentVariable("AEROLINK_MIGRATIONS_CONNECTION")!);
        var databaseName = DatabaseName + "_recoveryclaim";
        await EnsureDatabaseAsync(server, databaseName);
        var connection = new NpgsqlConnectionStringBuilder(server) { Database = databaseName }.ConnectionString;
        var (legacy, typed) = CutoverRegistrations();

        await using (var seedContext = await DatabaseAsync(connection))
        {
            var seed = await SeedAsync(seedContext);
            var first = await new SoftwareProcedureExecutionCutoverAuthority(seedContext, legacy, typed)
                .EnsureCompletedAsync();
            Assert.Equal(1, first.ProceduresGenerated);
            Assert.Equal(1, await seedContext.GovernedMigrationCompletions.AsNoTracking()
                .CountAsync(x => x.Marker == "VerificationExecutionCutover.SoftwareProcedures.v1"));

            // Simulate the crash boundary: work committed, marker and its completion audit lost.
            var completionAudits = await seedContext.SecurityAuditEvents
                .Where(x => x.EventType == "VerificationExecutionCutover.SoftwareProcedures.v1.Completed")
                .ToListAsync();
            seedContext.SecurityAuditEvents.RemoveRange(completionAudits);
            seedContext.GovernedMigrationCompletions.RemoveRange(
                await seedContext.GovernedMigrationCompletions.ToListAsync());
            await seedContext.SaveChangesAsync();
        }

        await using (var first = await DatabaseAsync(connection))
        await using (var second = await DatabaseAsync(connection))
        {
            var parallel = await Task.WhenAll(
                Task.Run(() => CatchAsync(() =>
                    new SoftwareProcedureExecutionCutoverAuthority(first, legacy, typed)
                        .EnsureCompletedAsync())),
                Task.Run(() => CatchAsync(() =>
                    new SoftwareProcedureExecutionCutoverAuthority(second, legacy, typed)
                        .EnsureCompletedAsync())));
            var expected = new SoftwareProcedureCutoverResult(1, 1, 1, 1, 1, 0);
            Assert.All(parallel, result => Assert.Equal(expected, result));

            await using var check = await DatabaseAsync(connection);
            Assert.Equal(1, await check.GovernedMigrationCompletions.AsNoTracking()
                .CountAsync(x => x.Marker == "VerificationExecutionCutover.SoftwareProcedures.v1"));
            Assert.Equal(1, await check.SecurityAuditEvents.AsNoTracking()
                .CountAsync(x => x.EventType == "VerificationExecutionCutover.SoftwareProcedures.v1.Completed"));
            // The completion audit must be the SAME evidence as the marker's immutable totals.
            var marker = await check.GovernedMigrationCompletions.AsNoTracking()
                .SingleAsync(x => x.Marker == "VerificationExecutionCutover.SoftwareProcedures.v1");
            var completedAudit = await check.SecurityAuditEvents.AsNoTracking()
                .SingleAsync(x => x.EventType == "VerificationExecutionCutover.SoftwareProcedures.v1.Completed");
            using (var markerJson = JsonDocument.Parse(marker.TotalsJson))
            using (var auditJson = JsonDocument.Parse(completedAudit.Detail))
            {
                Assert.Equal(markerJson.RootElement.GetProperty("ProjectsUpgraded").GetInt32(),
                    auditJson.RootElement.GetProperty("projectsUpgraded").GetInt32());
                Assert.Equal(markerJson.RootElement.GetProperty("ProceduresGenerated").GetInt32(),
                    auditJson.RootElement.GetProperty("proceduresGenerated").GetInt32());
                Assert.Equal(markerJson.RootElement.GetProperty("ExecutionsRebound").GetInt32(),
                    auditJson.RootElement.GetProperty("executionsRebound").GetInt32());
                Assert.Equal(markerJson.RootElement.GetProperty("TestSetEntriesRebound").GetInt32(),
                    auditJson.RootElement.GetProperty("testSetEntriesRebound").GetInt32());
                Assert.Equal(markerJson.RootElement.GetProperty("BaselineSelectionsRebound").GetInt32(),
                    auditJson.RootElement.GetProperty("baselineSelectionsRebound").GetInt32());
                Assert.Equal(markerJson.RootElement.GetProperty("ImpactItemsRebound").GetInt32(),
                    auditJson.RootElement.GetProperty("impactItemsRebound").GetInt32());
            }
            var projectId = await check.Projects.AsNoTracking().Select(x => x.Id).SingleAsync();
            Assert.Equal(1, await check.TestProcedures.AsNoTracking()
                .CountAsync(x => x.ProjectId == projectId
                    && x.ArtifactKind == VerificationArtifactKind.Procedure));
            Assert.Equal(1, await check.TestCaseProcedureLinks.AsNoTracking().CountAsync());
            Assert.Equal(1, await check.TestExecutions.AsNoTracking()
                .CountAsync(x => x.ProjectId == projectId));
            Assert.Equal(1, await check.BuildTestSetEntries.AsNoTracking().CountAsync());
            Assert.Equal(1, await check.BaselineTestProcedures.AsNoTracking().CountAsync());
            Assert.Equal(1, await check.SecurityAuditEvents.AsNoTracking()
                .CountAsync(x => x.EventType == "VerificationExecutionCutover.SoftwareProcedures.v1.ProjectUpgraded"
                    && x.Target == $"Project:{projectId}"));
        }
    }

    [DisposablePostgresFact]
    public async Task Two_projects_complete_with_scoped_signature_supersession_on_postgres()
    {
        var server = ValidateQualificationConnection(
            Environment.GetEnvironmentVariable("AEROLINK_MIGRATIONS_CONNECTION")!);
        var databaseName = DatabaseName + "_twosig";
        await EnsureDatabaseAsync(server, databaseName);
        var connection = new NpgsqlConnectionStringBuilder(server) { Database = databaseName }.ConnectionString;
        var evidenceRoot = Path.Combine(Path.GetTempPath(), $"aerolink-726-pg-two-{Guid.NewGuid():N}");
        var files = new EvidenceFileStore(evidenceRoot);
        try
        {
            await using var db = await DatabaseAsync(connection);
            var first = await SeedSignedDocumentProjectAsync(db, files, "400001");
            var second = await SeedSignedDocumentProjectAsync(db, files, "400002");
            var (legacy, typed) = CutoverRegistrations();
            var generator = new ControlledOutputGenerator(db, new RichContentPublisher(db, files),
                policyResolver: new EffectiveProjectLadderPolicyResolver(db));
            var authority = new SoftwareProcedureExecutionCutoverAuthority(db, legacy, typed,
                generator: generator, files: files);

            var firstRun = await authority.EnsureCompletedAsync();
            Assert.Equal(2, firstRun.ProjectsUpgraded);
            foreach (var seed in new[] { first, second })
            {
                foreach (var signatureId in new[] { seed.ArtifactSignatureId, seed.DocumentSignatureId })
                {
                    var target = $"ElectronicSignature:{signatureId}";
                    Assert.Equal(1, await db.SecurityAuditEvents.AsNoTracking().CountAsync(
                        x => x.EventType == "VerificationExecutionCutover.SoftwareProcedures.v1.SignatureSuperseded"
                            && x.Target == target));
                    Assert.Equal(1, await db.SecurityAuditEvents.AsNoTracking().CountAsync(
                        x => x.EventType == "VerificationExecutionCutover.SoftwareProcedures.v1.SignatureSupersessionCompleted"
                            && x.Target == target));
                }
            }

            // Rerun must not reprocess immutable SignatureSuperseded events that already carry completion
            // evidence: every target stays at exactly one superseded + one completion event.
            var rerun = await authority.EnsureCompletedAsync();
            Assert.Equal(firstRun, rerun);
            foreach (var seed in new[] { first, second })
            {
                foreach (var signatureId in new[] { seed.ArtifactSignatureId, seed.DocumentSignatureId })
                {
                    var target = $"ElectronicSignature:{signatureId}";
                    Assert.Equal(1, await db.SecurityAuditEvents.AsNoTracking().CountAsync(
                        x => x.EventType == "VerificationExecutionCutover.SoftwareProcedures.v1.SignatureSuperseded"
                            && x.Target == target));
                    Assert.Equal(1, await db.SecurityAuditEvents.AsNoTracking().CountAsync(
                        x => x.EventType == "VerificationExecutionCutover.SoftwareProcedures.v1.SignatureSupersessionCompleted"
                            && x.Target == target));
                }
            }
        }
        finally
        {
            try { Directory.Delete(evidenceRoot, recursive: true); } catch (IOException) { }
        }
    }

    [DisposablePostgresFact]
    public async Task Legacy_unmaterialized_document_cutover_preserves_historical_snapshot_on_postgres()
    {
        var server = ValidateQualificationConnection(
            Environment.GetEnvironmentVariable("AEROLINK_MIGRATIONS_CONNECTION")!);
        var databaseName = DatabaseName + "_legacydoc";
        await EnsureDatabaseAsync(server, databaseName);
        var connection = new NpgsqlConnectionStringBuilder(server) { Database = databaseName }.ConnectionString;
        var evidenceRoot = Path.Combine(Path.GetTempPath(), $"aerolink-726-pg-legacy-{Guid.NewGuid():N}");
        var files = new EvidenceFileStore(evidenceRoot);
        try
        {
            await using var db = await DatabaseAsync(connection);
            var seed = await SeedSignedDocumentProjectAsync(db, files, "400003", materialized: false);
            var now = DateTimeOffset.UtcNow;
            var caseArtifact = await db.TestProcedures.AsNoTracking()
                .SingleAsync(x => x.ProjectId == seed.ProjectId
                    && x.ArtifactKind == VerificationArtifactKind.Case);
            // A revision approved AFTER the document's GeneratedAt must never enter the regenerated
            // historical compatibility output.
            db.Add(new TestProcedureRevision(caseArtifact.Id, 1, "Later revision", "P", "S", "E",
                TestProcedureState.Approved, "test.engineer", now.AddDays(10)));
            await db.SaveChangesAsync();

            var (legacy, typed) = CutoverRegistrations();
            var generator = new ControlledOutputGenerator(db, new RichContentPublisher(db, files),
                policyResolver: new EffectiveProjectLadderPolicyResolver(db));
            var authority = new SoftwareProcedureExecutionCutoverAuthority(db, legacy, typed,
                generator: generator, files: files);
            var result = await authority.EnsureCompletedAsync();
            Assert.Equal(1, result.ProjectsUpgraded);

            var baseline = await db.CandidateBaselines.AsNoTracking().SingleAsync(x => x.Id == seed.BaselineId);
            Assert.Null(baseline.TestProceduresMaterializedAt);
            Assert.Null(baseline.TestProceduresHash);
            Assert.False(await db.BaselineEvents.AsNoTracking().AnyAsync(x =>
                x.BaselineId == seed.BaselineId
                && (x.EventType == "ExecutionCutoverManifestMigrated"
                    || x.EventType == "VerificationIdentityManifestMigrated")));
            var document = await db.ControlledDocuments.AsNoTracking().SingleAsync(x => x.Id == seed.DocumentId);
            // PostgreSQL timestamptz stores microsecond precision, so the round-trip may differ from the
            // in-memory DateTimeOffset by less than one microsecond. The cutover must preserve the original
            // GeneratedAt to database precision — never replace it with the cutover time.
            Assert.True(Math.Abs((document.GeneratedAt - seed.GeneratedAt).TotalMicroseconds) < 1,
                $"GeneratedAt changed: {seed.GeneratedAt} -> {document.GeneratedAt}");
            Assert.NotEqual(new string('c', 64), document.ContentHash);
            var artifact = await db.ControlledDocumentArtifacts.AsNoTracking()
                .SingleAsync(x => x.Id == seed.ArtifactId);
            Assert.True(files.Exists(artifact.StorageKey));
            var snapshot = await ControlledProcedureDocumentSnapshotProjection.ForDocumentAsync(db,
                seed.BaselineId, new VerificationArtifactKey(VerificationDiscipline.HighLevelSoftware,
                    VerificationArtifactKind.Case), document.GeneratedAt, default);
            var row = Assert.Single(snapshot.Rows);
            Assert.Equal(0, row.Revision);
            Assert.True(await db.SecurityAuditEvents.AsNoTracking().AnyAsync(x =>
                x.EventType == "VerificationExecutionCutover.SoftwareProcedures.v1.SignatureSupersessionCompleted"));
        }
        finally
        {
            try { Directory.Delete(evidenceRoot, recursive: true); } catch (IOException) { }
        }
    }

    [DisposablePostgresFact]
    public async Task Document_generated_before_later_baseline_materialization_still_uses_legacy_basis_on_postgres()
    {
        var server = ValidateQualificationConnection(
            Environment.GetEnvironmentVariable("AEROLINK_MIGRATIONS_CONNECTION")!);
        var databaseName = DatabaseName + "_temporal";
        await EnsureDatabaseAsync(server, databaseName);
        var connection = new NpgsqlConnectionStringBuilder(server) { Database = databaseName }.ConnectionString;
        var evidenceRoot = Path.Combine(Path.GetTempPath(), $"aerolink-726-pg-temporal-{Guid.NewGuid():N}");
        var files = new EvidenceFileStore(evidenceRoot);
        try
        {
            await using var db = await DatabaseAsync(connection);
            var seed = await SeedSignedDocumentProjectAsync(db, files, "500002", materialized: false);
            var now = DateTimeOffset.UtcNow;
            var originalStorageKey = await db.ControlledDocumentArtifacts.AsNoTracking()
                .Where(x => x.Id == seed.ArtifactId).Select(x => x.StorageKey).SingleAsync();
            var caseArtifact = await db.TestProcedures.AsNoTracking()
                .SingleAsync(x => x.ProjectId == seed.ProjectId
                    && x.ArtifactKind == VerificationArtifactKind.Case);
            var caseRevisionId = await db.TestProcedureRevisions.AsNoTracking()
                .Where(x => x.ProcedureId == caseArtifact.Id && x.Revision == 0)
                .Select(x => x.Id).SingleAsync();
            var laterRevision = new TestProcedureRevision(caseArtifact.Id, 1, "Later revision", "P", "S", "E",
                TestProcedureState.Approved, "test.engineer", now.AddDays(1),
                effectiveBaselineId: seed.BaselineId);
            db.Add(laterRevision);
            var baseline = await db.CandidateBaselines.SingleAsync(x => x.Id == seed.BaselineId);
            var materializedAt = seed.GeneratedAt.AddMinutes(1);
            baseline.MarkTestProceduresMaterialized("cm.test", new string('b', 64), 2, materializedAt);
            var selection = await db.BaselineTestProcedures.SingleAsync(
                x => x.BaselineId == baseline.Id && x.ProcedureId == caseArtifact.Id);
            selection.RebindMigrationExecutable(caseArtifact.Id, laterRevision.Id);
            await db.SaveChangesAsync();

            var (legacy, typed) = CutoverRegistrations();
            var generator = new ControlledOutputGenerator(db, new RichContentPublisher(db, files),
                policyResolver: new EffectiveProjectLadderPolicyResolver(db));
            var authority = new SoftwareProcedureExecutionCutoverAuthority(db, legacy, typed,
                generator: generator, files: files);
            var result = await authority.EnsureCompletedAsync();
            Assert.Equal(1, result.ProjectsUpgraded);
            db.ChangeTracker.Clear();

            var updatedBaseline = await db.CandidateBaselines.AsNoTracking()
                .SingleAsync(x => x.Id == seed.BaselineId);
            Assert.True(Math.Abs((updatedBaseline.TestProceduresMaterializedAt!.Value - materializedAt)
                .TotalMicroseconds) < 1);
            Assert.NotEqual(new string('b', 64), updatedBaseline.TestProceduresHash);

            var document = await db.ControlledDocuments.AsNoTracking().SingleAsync(x => x.Id == seed.DocumentId);
            Assert.True(Math.Abs((document.GeneratedAt - seed.GeneratedAt).TotalMicroseconds) < 1);
            Assert.NotEqual(new string('c', 64), document.ContentHash);
            var basisEvent = await db.SecurityAuditEvents.AsNoTracking().SingleAsync(x =>
                x.EventType == "VerificationExecutionCutover.SoftwareProcedures.v1.LegacyDocumentBasisReconstructed"
                && x.Target == $"ControlledDocument:{seed.DocumentId}");
            using (var detail = JsonDocument.Parse(basisEvent.Detail))
            {
                Assert.Equal(seed.DocumentId, detail.RootElement.GetProperty("documentId").GetGuid());
                Assert.Equal(seed.BaselineId, detail.RootElement.GetProperty("baselineId").GetGuid());
                Assert.Equal(1, detail.RootElement.GetProperty("artifactCount").GetInt32());
                Assert.False(detail.RootElement.GetProperty("baselineWasMaterializedWhenDocumentGenerated").GetBoolean());
                Assert.True(detail.RootElement.GetProperty("baselineManifestStatePreserved").GetBoolean());
                Assert.True(detail.RootElement.GetProperty("documentGeneratedAtPreserved").GetBoolean());
                Assert.Equal(64, detail.RootElement.GetProperty("compatibilitySnapshotHash").GetString()!.Length);
            }

            var artifact = await db.ControlledDocumentArtifacts.AsNoTracking()
                .SingleAsync(x => x.Id == seed.ArtifactId);
            Assert.NotEqual(originalStorageKey, artifact.StorageKey);
            Assert.True(files.Exists(originalStorageKey));
            await using var generated = await files.OpenVerifiedReadAsync(
                artifact.StorageKey, artifact.Size, artifact.Sha256, default);
            using var copy = new MemoryStream();
            await generated.CopyToAsync(copy);
            using var archive = new ZipArchive(new MemoryStream(copy.ToArray()), ZipArchiveMode.Read);
            using var reader = new StreamReader(archive.GetEntry("word/document.xml")!.Open());
            var xml = await reader.ReadToEndAsync();
            Assert.Contains(caseArtifact.BaseNumber, xml);
            Assert.DoesNotContain($"{caseArtifact.BaseNumber}.01", xml);
        }
        finally
        {
            try { Directory.Delete(evidenceRoot, recursive: true); } catch (IOException) { }
        }
    }

    [DisposablePostgresFact]
    public async Task Concurrent_missing_audit_repair_keeps_exactly_one_audit_on_postgres()
    {
        var server = ValidateQualificationConnection(
            Environment.GetEnvironmentVariable("AEROLINK_MIGRATIONS_CONNECTION")!);
        var databaseName = DatabaseName + "_repair";
        await EnsureDatabaseAsync(server, databaseName);
        var connection = new NpgsqlConnectionStringBuilder(server) { Database = databaseName }.ConnectionString;
        var (legacy, typed) = CutoverRegistrations();

        await using (var seedContext = await DatabaseAsync(connection))
        {
            await SeedAsync(seedContext);
            var completed = await new SoftwareProcedureExecutionCutoverAuthority(seedContext, legacy, typed)
                .EnsureCompletedAsync();
            Assert.Equal(1, completed.ProceduresGenerated);
            // Reproduce the historical "marker exists, audit missing" state the pre-atomic build could leave.
            var audits = await seedContext.SecurityAuditEvents
                .Where(x => x.EventType == "VerificationExecutionCutover.SoftwareProcedures.v1.Completed")
                .ToListAsync();
            seedContext.SecurityAuditEvents.RemoveRange(audits);
            await seedContext.SaveChangesAsync();
            Assert.Equal(0, await seedContext.SecurityAuditEvents.AsNoTracking().CountAsync(x =>
                x.EventType == "VerificationExecutionCutover.SoftwareProcedures.v1.Completed"));
        }

        await using (var first = await DatabaseAsync(connection))
        await using (var second = await DatabaseAsync(connection))
        {
            var parallel = await Task.WhenAll(
                Task.Run(() => new SoftwareProcedureExecutionCutoverAuthority(first, legacy, typed)
                    .EnsureCompletedAsync()),
                Task.Run(() => new SoftwareProcedureExecutionCutoverAuthority(second, legacy, typed)
                    .EnsureCompletedAsync()));
            var expected = new SoftwareProcedureCutoverResult(1, 1, 1, 1, 1, 0);
            Assert.All(parallel, result => Assert.Equal(expected, result));

            await using var check = await DatabaseAsync(connection);
            Assert.Equal(1, await check.GovernedMigrationCompletions.AsNoTracking().CountAsync(x =>
                x.Marker == "VerificationExecutionCutover.SoftwareProcedures.v1"));
            Assert.Equal(1, await check.SecurityAuditEvents.AsNoTracking().CountAsync(x =>
                x.EventType == "VerificationExecutionCutover.SoftwareProcedures.v1.Completed"));
            var marker = await check.GovernedMigrationCompletions.AsNoTracking()
                .SingleAsync(x => x.Marker == "VerificationExecutionCutover.SoftwareProcedures.v1");
            var audit = await check.SecurityAuditEvents.AsNoTracking()
                .SingleAsync(x => x.EventType == "VerificationExecutionCutover.SoftwareProcedures.v1.Completed");
            using (var markerJson = JsonDocument.Parse(marker.TotalsJson))
            using (var auditJson = JsonDocument.Parse(audit.Detail))
            {
                Assert.Equal(markerJson.RootElement.GetProperty("ProceduresGenerated").GetInt32(),
                    auditJson.RootElement.GetProperty("proceduresGenerated").GetInt32());
                Assert.Equal(markerJson.RootElement.GetProperty("ProjectsUpgraded").GetInt32(),
                    auditJson.RootElement.GetProperty("projectsUpgraded").GetInt32());
            }
            Assert.Equal(1, await check.SecurityAuditEvents.AsNoTracking().CountAsync(x =>
                x.EventType == "VerificationExecutionCutover.SoftwareProcedures.v1.Completed"));
            var rerun = await new SoftwareProcedureExecutionCutoverAuthority(check, legacy, typed)
                .EnsureCompletedAsync();
            Assert.Equal(expected, rerun);
            Assert.Equal(1, await check.SecurityAuditEvents.AsNoTracking().CountAsync(x =>
                x.EventType == "VerificationExecutionCutover.SoftwareProcedures.v1.Completed"));
        }
    }

    [DisposablePostgresFact]
    public async Task Large_baseline_provenance_is_chunked_complete_deterministic_and_recoverable_on_postgres()
    {
        var server = ValidateQualificationConnection(
            Environment.GetEnvironmentVariable("AEROLINK_MIGRATIONS_CONNECTION")!);
        var databaseName = DatabaseName + "_largeprov";
        await EnsureDatabaseAsync(server, databaseName);
        var connection = new NpgsqlConnectionStringBuilder(server) { Database = databaseName }.ConnectionString;
        await using var db = await DatabaseAsync(connection);
        var seed = await SeedLargeBaselineAsync(db, caseCount: 60);
        var (legacy, typed) = CutoverRegistrations();
        var result = await new SoftwareProcedureExecutionCutoverAuthority(db, legacy, typed)
            .EnsureCompletedAsync();
        Assert.Equal(1, result.ProjectsUpgraded);
        Assert.Equal(60, result.ProceduresGenerated);
        Assert.Equal(60, result.BaselineSelectionsRebound);

        var manifestEvent = await db.BaselineEvents.AsNoTracking()
            .SingleAsync(x => x.BaselineId == seed.BaselineId
                && x.EventType == "ExecutionCutoverManifestMigrated");
        Assert.True(manifestEvent.Detail.Length < 4000,
            $"BaselineEvent.Detail must stay under 4,000 characters; got {manifestEvent.Detail.Length}.");
        Assert.Contains("mappings=60", manifestEvent.Detail, StringComparison.Ordinal);
        Assert.Contains("chunks=6", manifestEvent.Detail, StringComparison.Ordinal);

        var provenanceRows = await db.BaselineExecutionCutoverProvenances.AsNoTracking()
            .Where(x => x.BaselineId == seed.BaselineId)
            .OrderBy(x => x.Sequence).ToListAsync();
        Assert.Equal(6, provenanceRows.Count);
        Assert.All(provenanceRows, row =>
        {
            Assert.True(row.Content.Length <= 2000);
            Assert.Equal(60, row.TotalMappings);
            Assert.Equal(manifestEvent.Id, row.EventId);
        });
        var entries = provenanceRows.SelectMany(row => row.Content.Split(';')).ToList();
        Assert.Equal(60, entries.Count);
        Assert.Equal(60, entries.Distinct().Count());
        Assert.Equal(60, provenanceRows.Sum(row => row.EntryCount));

        // Deterministic canonical aggregate hash: recompute over the sequence-ordered entries.
        var canonicalInput = string.Join(";", provenanceRows.OrderBy(x => x.Sequence)
            .SelectMany(x => x.Content.Split(';')));
        var expectedHash = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(canonicalInput)))
            .ToLowerInvariant();
        Assert.All(provenanceRows, row => Assert.Equal(expectedHash, row.CanonicalAggregateHash));

        // Every exact mapping resolves to a typed migration-source record; no mapping is lost or invented.
        var sources = await db.TestProcedureMigrationSources.AsNoTracking().ToListAsync();
        Assert.Equal(60, sources.Count);
        foreach (var entry in entries)
        {
            var caseId = Guid.Parse(entry.Split("->procedure:")[0]["case:".Length..]);
            var generated = entry.Split("->procedure:")[1].Split(':');
            Assert.Contains(sources, source =>
                source.SourceCaseRevisionId == caseId
                && source.GeneratedProcedureArtifactId == Guid.Parse(generated[0])
                && source.GeneratedProcedureRevisionId == Guid.Parse(generated[1]));
        }
        Assert.Equal(sources.Count, sources.Select(x => x.SourceCaseRevisionId).Distinct().Count());

        // Rerun adds no provenance and no additional manifest events.
        var rerun = await new SoftwareProcedureExecutionCutoverAuthority(db, legacy, typed)
            .EnsureCompletedAsync();
        Assert.Equal(result, rerun);
        Assert.Equal(6, await db.BaselineExecutionCutoverProvenances.AsNoTracking().CountAsync(x =>
            x.BaselineId == seed.BaselineId));
        Assert.Equal(1, await db.BaselineEvents.AsNoTracking().CountAsync(x =>
            x.BaselineId == seed.BaselineId && x.EventType == "ExecutionCutoverManifestMigrated"));

        // Crash recovery keeps the same honest totals without duplicate provenance.
        db.ChangeTracker.Clear();
        var completionAudits = await db.SecurityAuditEvents
            .Where(x => x.EventType == "VerificationExecutionCutover.SoftwareProcedures.v1.Completed")
            .ToListAsync();
        db.SecurityAuditEvents.RemoveRange(completionAudits);
        db.GovernedMigrationCompletions.RemoveRange(
            await db.GovernedMigrationCompletions.ToListAsync());
        await db.SaveChangesAsync();
        var recovered = await new SoftwareProcedureExecutionCutoverAuthority(db, legacy, typed)
            .EnsureCompletedAsync();
        Assert.Equal(1, recovered.ProjectsUpgraded);
        Assert.Equal(60, recovered.ProceduresGenerated);
        Assert.Equal(60, recovered.BaselineSelectionsRebound);
        Assert.Equal(6, await db.BaselineExecutionCutoverProvenances.AsNoTracking().CountAsync(x =>
            x.BaselineId == seed.BaselineId));
        Assert.Equal(1, await db.BaselineEvents.AsNoTracking().CountAsync(x =>
            x.BaselineId == seed.BaselineId && x.EventType == "ExecutionCutoverManifestMigrated"));
    }

    [DisposablePostgresFact]
    public async Task Dormant_historical_revision_is_retired_typed_and_not_selectable_on_postgres()
    {
        var server = ValidateQualificationConnection(
            Environment.GetEnvironmentVariable("AEROLINK_MIGRATIONS_CONNECTION")!);
        var databaseName = DatabaseName + "_dormant";
        await EnsureDatabaseAsync(server, databaseName);
        var connection = new NpgsqlConnectionStringBuilder(server) { Database = databaseName }.ConnectionString;
        await using var db = await DatabaseAsync(connection);
        var now = DateTimeOffset.UtcNow;
        var tag = Guid.NewGuid().ToString("N")[..8];
        var program = new ProgramRecord("Dormant PG Program", $"DRP{tag}");
        var project = new ProjectRecord(program.Id, "Dormant PG Software", "Dormant PG Product");
        var release = new SoftwareRelease(project.Id, "1.0", false);
        var baseline = new CandidateBaseline("SW-01.00", 0, project.Id, release.Id, null,
            "Candidate", "cm.test", now);
        db.AddRange(program, project, release, baseline);
        var configuration = LegacyDefaultProjectLadderFactory.Create(project.Id, now);
        db.ProjectLadderConfigurations.Add(configuration);
        await db.SaveChangesAsync();
        var seal = await new ProjectLadderSealAuthority(db).SealAsync(project.Id,
            LadderBoundContentCatalog.Current.First().Id, "dormant-content", "test.sealer", now);
        Assert.Equal(ProjectLadderSealResultKind.Sealed, seal.Kind);
        var caseArtifact = new TestProcedure(project.Id, $"HLRTC-{Random.Shared.Next(100000, 999999)}",
            "Historical dormant case", "test.engineer", now, TestProcedureLevel.HighLevel);
        var caseRevision = new TestProcedureRevision(caseArtifact.Id, 0,
            "Verify historical work", "Preconditions", "Steps", "Expected",
            TestProcedureState.Approved, "test.engineer", now, effectiveBaselineId: baseline.Id,
            parentKind: VerificationProcedureParentKind.Derived,
            derivedRationale: "Historical dormant fixture Case with no current requirement coverage.");
        db.AddRange(caseArtifact, caseRevision);
        await db.SaveChangesAsync();

        var (legacy, typed) = CutoverRegistrations();
        var result = await new SoftwareProcedureExecutionCutoverAuthority(db, legacy, typed)
            .EnsureCompletedAsync();
        Assert.Equal(1, result.ProceduresGenerated);
        var procedure = await db.TestProcedures.AsNoTracking()
            .SingleAsync(x => x.ProjectId == project.Id
                && x.ArtifactKind == VerificationArtifactKind.Procedure);
        var mirror = await db.TestProcedureRevisions.AsNoTracking()
            .SingleAsync(x => x.ProcedureId == procedure.Id);
        Assert.Equal(TestProcedureState.Retired, mirror.State);
        Assert.Null(mirror.EffectiveBaselineId);
        Assert.Equal(0, await db.TestCaseProcedureLinks.AsNoTracking().CountAsync());
        var source = await db.TestProcedureMigrationSources.AsNoTracking().SingleAsync(x =>
            x.ProjectId == project.Id);
        Assert.Equal(caseRevision.Id, source.SourceCaseRevisionId);
        Assert.Equal(procedure.Id, source.GeneratedProcedureArtifactId);
        Assert.Equal(mirror.Id, source.GeneratedProcedureRevisionId);
        var service = new VerificationImpactService(db,
            policyResolver: new EffectiveProjectLadderPolicyResolver(db));
        Assert.Null(await service.FindApprovedProcedureAsync(project.Id, procedure.Id, default));
    }

    private sealed record LargeBaselineSeed(AeroLinkDbContext Db, Guid ProjectId, Guid ReleaseId,
        Guid BaselineId);

    private static async Task<LargeBaselineSeed> SeedLargeBaselineAsync(AeroLinkDbContext db, int caseCount)
    {
        var now = DateTimeOffset.UtcNow;
        var tag = Guid.NewGuid().ToString("N")[..8];
        var program = new ProgramRecord("Large Provenance Program", $"LPP{tag}");
        var project = new ProjectRecord(program.Id, "Large Provenance Software", "Large Provenance Product");
        var release = new SoftwareRelease(project.Id, "1.0", false);
        var baseline = new CandidateBaseline("SW-01.00", 0, project.Id, release.Id, null,
            "Candidate", "cm.test", now);
        db.AddRange(program, project, release, baseline);
        var configuration = LegacyDefaultProjectLadderFactory.Create(project.Id, now);
        db.ProjectLadderConfigurations.Add(configuration);
        await db.SaveChangesAsync();
        var seal = await new ProjectLadderSealAuthority(db).SealAsync(project.Id,
            LadderBoundContentCatalog.Current.First().Id, "large-prov-content", "test.sealer", now);
        Assert.Equal(ProjectLadderSealResultKind.Sealed, seal.Kind);
        var scr = ApprovedBaselineScr(project.Id, release.Id, "SRCR-80000", now);
        db.Add(scr);
        for (var index = 0; index < caseCount; index++)
        {
            var caseArtifact = new TestProcedure(project.Id, $"HLRTC-{800000 + index}",
                $"Large provenance case {index}", "test.engineer", now, TestProcedureLevel.HighLevel);
            var caseRevision = new TestProcedureRevision(caseArtifact.Id, 0,
                $"Verify case {index}", "Preconditions", "Steps", "Expected",
                TestProcedureState.Approved, "test.engineer", now, effectiveBaselineId: baseline.Id,
                parentKind: VerificationProcedureParentKind.Derived,
                derivedRationale: $"Large provenance fixture case {index}.");
            db.AddRange(caseArtifact, caseRevision,
                new BaselineTestProcedureSelection(baseline.Id, caseArtifact.Id, caseRevision.Id));
        }
        baseline.Select(scr, "cm.test", now);
        baseline.Freeze("cm.test", now);
        baseline.MarkRequirementsMaterialized("cm.test", new string('a', 64), 0, now);
        baseline.MarkTestProceduresMaterialized("cm.test", new string('b', 64), caseCount, now);
        await db.SaveChangesAsync();
        return new LargeBaselineSeed(db, project.Id, release.Id, baseline.Id);
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

    private sealed record SignedDocumentSeed(AeroLinkDbContext Db, Guid ProjectId, Guid ReleaseId,
        Guid BaselineId, Guid DocumentId, Guid ArtifactId, Guid ArtifactSignatureId,
        Guid DocumentSignatureId, DateTimeOffset GeneratedAt);

    private static async Task<SignedDocumentSeed> SeedSignedDocumentProjectAsync(
        AeroLinkDbContext db, EvidenceFileStore files, string tag, bool materialized = true)
    {
        var now = DateTimeOffset.UtcNow;
        var program = new ProgramRecord($"{tag} Program", $"SDP{tag[^3..]}");
        var project = new ProjectRecord(program.Id, $"{tag} Software", $"{tag} Product");
        var release = new SoftwareRelease(project.Id, "1.0", false);
        var baseline = new CandidateBaseline($"SW-{tag[^2..]}.00", 0, project.Id, release.Id, null,
            "Candidate", "cm.test", now);
        db.AddRange(program, project, release, baseline);
        var configuration = LegacyDefaultProjectLadderFactory.Create(project.Id, now);
        db.ProjectLadderConfigurations.Add(configuration);
        await db.SaveChangesAsync();
        var seal = await new ProjectLadderSealAuthority(db).SealAsync(project.Id,
            LadderBoundContentCatalog.Current.First().Id, $"{tag}-content", "test.sealer", now);
        Assert.Equal(ProjectLadderSealResultKind.Sealed, seal.Kind);
        var caseArtifact = new TestProcedure(project.Id, $"HLRTC-{tag[^6..]}", $"{tag} case",
            "test.engineer", now, TestProcedureLevel.HighLevel);
        var caseRevision = new TestProcedureRevision(caseArtifact.Id, 0,
            "Verify", "Preconditions", "Steps", "Expected",
            TestProcedureState.Approved, "test.engineer", now, effectiveBaselineId: baseline.Id);
        var scr = ApprovedBaselineScr(project.Id, release.Id, $"SRCR-{tag[^5..]}", now);
        db.AddRange(scr, caseArtifact, caseRevision,
            new BaselineTestProcedureSelection(baseline.Id, caseArtifact.Id, caseRevision.Id));
        baseline.Select(scr, "cm.test", now);
        baseline.Freeze("cm.test", now);
        baseline.MarkRequirementsMaterialized("cm.test", new string('a', 64), 0, now);
        if (materialized) baseline.MarkTestProceduresMaterialized("cm.test", new string('b', 64), 1, now);
        await db.SaveChangesAsync();
        var oldBytes = Encoding.UTF8.GetBytes($"pre-cutover {tag} controlled bytes");
        var stored = await files.StoreAsync(new MemoryStream(oldBytes), $"{tag}.docx",
            "application/vnd.openxmlformats-officedocument.wordprocessingml.document", default);
        var document = new ControlledDocument(project.Id, release.Id, baseline.Id,
            ControlledDocumentType.HighLevelTestCases, $"HLRTD-{tag[^6..]}", $"{tag} cases", 0,
            new string('c', 64), 1, now);
        var artifact = new ControlledDocumentArtifact(document.Id, "docx", stored.StorageKey,
            stored.OriginalFileName, stored.ContentType, stored.Size, stored.Sha256, now);
        var artifactSignature = new ElectronicSignature(Guid.NewGuid(), "reviewer", "Reviewer", program.Id,
            "ControlledDocumentArtifact", artifact.Id, $"{document.DocumentNumber}.00/docx", "Approve",
            "old output", artifact.Sha256, "127.0.0.1", now);
        var documentSignature = new ElectronicSignature(Guid.NewGuid(), "reviewer", "Reviewer", program.Id,
            "ControlledDocument", document.Id, $"{document.DocumentNumber}.00", "Approve",
            "old document", document.ContentHash, "127.0.0.1", now);
        db.AddRange(document, artifact, artifactSignature, documentSignature);
        await db.SaveChangesAsync();
        return new SignedDocumentSeed(db, project.Id, release.Id, baseline.Id, document.Id, artifact.Id,
            artifactSignature.Id, documentSignature.Id, now);
    }

    private static SystemChangeRequest ApprovedBaselineScr(Guid projectId, Guid releaseId, string number,
        DateTimeOffset now)
    {
        var scr = new SystemChangeRequest(number, 0, projectId, releaseId,
            "Baseline authority", "Problem", "Analysis", "Solution", "author", now);
        scr.AddRequirementChange("author", $"SYSR-{number[^5..]}", 0, RequirementLevel.System,
            RequirementChangeKind.Introduce, "The system shall retain the controlled fixture behavior.",
            "Baseline fixture authority.", "Analysis", now);
        scr.SubmitForReview("author", [new ApproverSelection("reviewer", "Reviewer")], now);
        scr.ApproveActiveStage("reviewer", now);
        return scr;
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
