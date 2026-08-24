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
/// #726 governed cutover on disposable PostgreSQL (never 54329): clean current install, deterministic
/// migration-generated Procedures, reference rebinding, idempotent rerun, fail-closed rollback, and
/// concurrent-writer behavior.
/// </summary>
[CollectionDefinition("Issue726Postgres", DisableParallelization = true)]
public sealed class Issue726PostgresCollection : ICollectionFixture<object>;

[Collection("Issue726Postgres")]
public sealed class SoftwareProcedureExecutionCutoverPostgresQualificationTests
{
    private const string DatabaseName = "aerolink_726_qualify";
    private const string ServerDatabase = "postgres";

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
            var debugCases = await db.TestProcedures.AsNoTracking()
                .CountAsync(x => x.ProjectId == seed.ProjectId
                    && x.ArtifactKind == VerificationArtifactKind.Case);
            var debugEvents = await db.SecurityAuditEvents.AsNoTracking()
                .CountAsync(x => x.EventType.StartsWith("VerificationExecutionCutover"));
            Assert.True(result.ProceduresGenerated == 1,
                $"projects={result.ProjectsUpgraded} generated={result.ProceduresGenerated} "
                + $"cases={debugCases} completed={debugEvents}");
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
            await SeedAsync(first);
            var (legacy, typed) = CutoverRegistrations();
            // The loser may observe the Completed marker (returns zero) or lose the optimistic-version
            // race (throws). Either way exactly one cutover may generate Procedures.
            var parallel = await Task.WhenAll(
                Task.Run(() => CatchAsync(() =>
                    new SoftwareProcedureExecutionCutoverAuthority(first, legacy, typed)
                        .EnsureCompletedAsync())),
                Task.Run(() => CatchAsync(() =>
                    new SoftwareProcedureExecutionCutoverAuthority(second, legacy, typed)
                        .EnsureCompletedAsync())));
            Assert.Equal(1, parallel.Sum(x => x.ProceduresGenerated));
            Assert.Equal(1, parallel.Count(x => x.ProceduresGenerated > 0));
            await using var check = await DatabaseAsync(connection);
            var projectId = await check.Projects.AsNoTracking().Select(x => x.Id).FirstAsync();
            Assert.Equal(1, await check.TestProcedures.AsNoTracking()
                .CountAsync(x => x.ProjectId == projectId
                    && x.ArtifactKind == VerificationArtifactKind.Procedure));
        }
    }

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

    private static async Task<AeroLinkDbContext> DatabaseAsync(string connection)
    {
        var options = new DbContextOptionsBuilder<AeroLinkDbContext>().UseNpgsql(connection).Options;
        var db = new AeroLinkDbContext(options);
        await db.Database.MigrateAsync();
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
        catch (InvalidOperationException) { return new(0, 0, 0, 0, 0, 0); }
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
