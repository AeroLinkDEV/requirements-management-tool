using AeroLink.Domain.Baselines;
using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Common;
using AeroLink.Domain.Programs;
using AeroLink.Domain.Requirements;
using AeroLink.Domain.Verification;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace AeroLink.Infrastructure.Tests;

/// <summary>
/// PostgreSQL-only qualification for #724. The predecessor is deliberately migrated first and its small
/// legacy fixture is inserted through the predecessor schema, because the current EF model contains the additive
/// columns this test is proving are absent before the migration runs.
/// </summary>
[CollectionDefinition("Issue724Postgres", DisableParallelization = true)]
public sealed class Issue724PostgresCollection : ICollectionFixture<object>;

[Trait("Category", "PostgresQualification")]
[Collection("Issue724Postgres")]
public sealed class DormantSoftwareProcedurePostgresQualificationTests
{
    private const string Predecessor = "20260822180000_RenameTestChangeRequestPrefixes";
    private const string DatabaseName = "aerolink_724_qualify";

    [DisposablePostgresFact]
    public async Task Exact_predecessor_upgrade_preserves_legacy_rows_and_qualifies_dormant_procedures()
    {
        await using var database = await DisposablePostgresDatabase.CreateAsync(DatabaseName);
        var connection = database.ConnectionString;
        await using var db = await MigrateToPredecessorAsync(connection);
        var fixture = await SeedLegacyFixtureAsync(db);

        await db.Database.GetService<IMigrator>().MigrateAsync();
        db.ChangeTracker.Clear();

        Assert.Equal(3, await db.TestProcedures.CountAsync());
        Assert.Equal(4, await db.TestProcedureRevisions.CountAsync());
        Assert.Equal(3, await db.BaselineTestProcedures.CountAsync());
        Assert.Equal(1, await db.TestProcedures.CountAsync(x => x.ArtifactKind == VerificationArtifactKind.Procedure));
        Assert.Equal(2, await db.TestProcedures.CountAsync(x => x.ArtifactKind == VerificationArtifactKind.Case));

        var oldHigh = await db.TestProcedureRevisions.AsNoTracking().SingleAsync(x => x.Id == fixture.HighRevisionId);
        var oldHighHistory = await db.TestProcedureRevisions.AsNoTracking()
            .Where(x => x.ProcedureId == fixture.HighCaseId).OrderBy(x => x.Revision).ToListAsync();
        Assert.Equal("legacy high objective", oldHigh.Objective);
        Assert.Equal("legacy high steps", oldHigh.Steps);
        Assert.Equal(fixture.BaselineId, oldHigh.EffectiveBaselineId);
        Assert.Equal(new[] { 0, 1 }, oldHighHistory.Select(x => x.Revision));
        Assert.All(oldHighHistory, revision =>
        {
            Assert.Equal(VerificationProcedureParentKind.Unspecified, revision.ParentKind);
            Assert.All(new[] { revision.EnvironmentSetup, revision.TestData, revision.OrderedSteps,
                revision.ExpectedObservations, revision.Cleanup, revision.ToolingAutomation }, Assert.Empty);
        });

        var legacyWatermarks = await db.IdentifierSequences.AsNoTracking()
            .Where(x => x.Scope == "HLRTP" || x.Scope == "LLRTP")
            .OrderBy(x => x.Scope).Select(x => new { x.Scope, x.NextValue }).ToListAsync();
        Assert.Equal(new[] { "HLRTP", "LLRTP" }, legacyWatermarks.Select(x => x.Scope));
        Assert.Equal(new long[] { 91, 53 }, legacyWatermarks.Select(x => x.NextValue));

        var service = new VerificationProcedureAuthoringService(db);
        Assert.Equal(fixture.BaselineId, await db.TestProcedureRevisions.AsNoTracking()
            .Where(x => x.Id == fixture.HighRevisionId || x.Id == fixture.HighRevision2Id)
            .Select(x => x.EffectiveBaselineId).Distinct().SingleAsync());
        var content = new VerificationProcedureContent(
            "Bench power and load the qualification image.",
            "Use the retained HLR fixture and deterministic input set.",
            "1. Start the harness.\n2. Execute the ordered check.",
            "The expected observation is recorded for every ordered step.",
            "Stop the harness and remove the qualification data.",
            "Run with the checked-in harness; capture the command and result.");
        // A new dormant Procedure names its Case's latest active revision (#1035): revision 1 supersedes 0.
        var (highProcedure, highProcedureRevision) = await service.CreateAsync(
            fixture.ProjectId, TestProcedureLevel.HighLevel, "Dormant HLR Procedure", "qualification", content,
            VerificationProcedureParentKind.Allocated, [fixture.HighRevision2Id], null,
            DateTimeOffset.UtcNow, CancellationToken.None);
        var (lowProcedure, lowProcedureRevision) = await service.CreateAsync(
            fixture.ProjectId, TestProcedureLevel.LowLevel, "Dormant LLR Procedure", "qualification", content,
            VerificationProcedureParentKind.Allocated, [fixture.LowRevisionId], null,
            DateTimeOffset.UtcNow, CancellationToken.None);

        Assert.Equal("HLRTP-000091", highProcedure.BaseNumber);
        Assert.Equal("LLRTP-000053", lowProcedure.BaseNumber);
        Assert.Equal(new[] { fixture.BaselineId, fixture.BaselineId }, await db.TestProcedureRevisions.AsNoTracking()
            .Where(x => x.Id == fixture.HighRevisionId || x.Id == fixture.HighRevision2Id)
            .OrderBy(x => x.Revision).Select(x => x.EffectiveBaselineId!.Value).ToArrayAsync());
        db.ChangeTracker.Clear();
        // A Procedure revision may verify more than one Case. Each new link names its Case's latest active revision
        // (#1035), so the second parent is a second HLR Case rather than an older revision of the first.
        var secondCase = new TestProcedure(fixture.ProjectId, "HLRTC-000042", "Second HLR Case", "qualification",
            DateTimeOffset.UtcNow, TestProcedureLevel.HighLevel);
        var secondCaseRevision = new TestProcedureRevision(secondCase.Id, 0, "second objective", "second setup",
            "second steps", "second expected", TestProcedureState.Approved, "qualification", DateTimeOffset.UtcNow,
            effectiveBaselineId: fixture.BaselineId, parentKind: VerificationProcedureParentKind.Derived,
            derivedRationale: "Second Case parent for the cardinality check.");
        await using (var cardinalityDb = new AeroLinkDbContext(Options(connection)))
        {
            cardinalityDb.AddRange(secondCase, secondCaseRevision);
            cardinalityDb.TestCaseProcedureLinks.Add(new TestCaseProcedureLink(secondCaseRevision.Id, highProcedureRevision.Id));
            await cardinalityDb.SaveChangesAsync();
            Assert.Equal(2, await cardinalityDb.TestCaseProcedureLinks.CountAsync(x => x.ProcedureRevisionId == highProcedureRevision.Id));
            Assert.Single(await cardinalityDb.TestCaseProcedureLinks.Where(x => x.ProcedureRevisionId == lowProcedureRevision.Id).ToListAsync());
        }
        Assert.Equal(3, await db.TestProcedures.CountAsync(x => x.ArtifactKind == VerificationArtifactKind.Procedure));
        Assert.Equal(0, await db.TestProcedureRevisions.AsNoTracking().CountAsync(x => x.ParentKind == VerificationProcedureParentKind.Unspecified
            && x.ProcedureId == highProcedure.Id));

        var secondBaseline = new CandidateBaseline("BL-000002", 0, fixture.ProjectId, fixture.ReleaseId, null,
            "Different build scope", "qualification", DateTimeOffset.UtcNow);
        db.CandidateBaselines.Add(secondBaseline);
        await db.SaveChangesAsync();
        var crossBaselineCaseRevision = new TestProcedureRevision(
            fixture.HighCaseId, 2, "other build case", "setup", "steps", "expected", TestProcedureState.Approved,
            "qualification", DateTimeOffset.UtcNow, effectiveBaselineId: secondBaseline.Id);
        db.TestProcedureRevisions.Add(crossBaselineCaseRevision);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        await using (var invalidScopeDb = new AeroLinkDbContext(Options(connection)))
        {
            var invalidService = new VerificationProcedureAuthoringService(invalidScopeDb);
            // Both parents are their Case's latest active revision, so only the mixed governed baselines refuse.
            var mixed = await Assert.ThrowsAsync<DomainException>(() => invalidService.CreateAsync(
                fixture.ProjectId, TestProcedureLevel.HighLevel, "Mixed baseline Procedure", "qualification", content,
                VerificationProcedureParentKind.Allocated, [secondCaseRevision.Id, crossBaselineCaseRevision.Id], null,
                DateTimeOffset.UtcNow, CancellationToken.None));
            Assert.Contains("must share one exact baseline", mixed.Message, StringComparison.Ordinal);
        }

        await using (var invalidLevelDb = new AeroLinkDbContext(Options(connection)))
        {
            invalidLevelDb.TestCaseProcedureLinks.Add(new TestCaseProcedureLink(fixture.LowRevisionId, highProcedureRevision.Id));
            await Assert.ThrowsAsync<DomainException>(() => invalidLevelDb.SaveChangesAsync());
        }

        var source = new SystemChangeRequest("SRCR-00002", 0, fixture.ProjectId, fixture.ReleaseId,
            "Qualification requirement", "Problem", "Analysis", "Solution", "qualification", DateTimeOffset.UtcNow);
        var requirement = new RequirementArtifact(fixture.ProjectId, "SYSR-00002", RequirementLevel.System, DateTimeOffset.UtcNow);
        var requirementRevision = new RequirementRevision(requirement.Id, 0, "The system shall retain old coverage behavior.",
            "qualification", "manual", RequirementRevisionState.Active, source.Id, fixture.BaselineId, DateTimeOffset.UtcNow);
        db.AddRange(source, requirement, requirementRevision);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        await using (var directCoverageDb = new AeroLinkDbContext(Options(connection)))
        {
            directCoverageDb.TestCoverage.Add(new TestRequirementCoverage(highProcedureRevision.Id, requirementRevision.Id));
            await Assert.ThrowsAsync<DomainException>(() => directCoverageDb.SaveChangesAsync());
        }

        var beforeIdempotentCounts = await SnapshotCountsAsync(db);
        await db.Database.GetService<IMigrator>().MigrateAsync();
        Assert.Equal(beforeIdempotentCounts, await SnapshotCountsAsync(db));
    }

    [DisposablePostgresFact]
    public async Task Clean_current_install_creates_the_additive_schema_without_fabricated_procedures()
    {
        await using var database = await DisposablePostgresDatabase.CreateAsync(DatabaseName);
        var connection = database.ConnectionString;
        await using var db = new AeroLinkDbContext(Options(connection));
        await db.Database.EnsureDeletedAsync();
        await db.Database.GetService<IMigrator>().MigrateAsync();

        Assert.Equal(0, await db.TestProcedures.CountAsync());
        Assert.Equal(0, await db.TestCaseProcedureLinks.CountAsync());
        Assert.Equal(0, await db.TestProcedureRevisions.CountAsync());
        Assert.Equal(1, await db.Database.SqlQueryRaw<int>(
            "SELECT COUNT(*)::int AS \"Value\" FROM \"__EFMigrationsHistory\" WHERE \"MigrationId\" = '20260823061857_AddDormantSoftwareProcedures'")
            .SingleAsync());
    }

    private static async Task<AeroLinkDbContext> MigrateToPredecessorAsync(string connection)
    {
        var db = new AeroLinkDbContext(Options(connection));
        await db.Database.EnsureDeletedAsync();
        await db.Database.GetService<IMigrator>().MigrateAsync(Predecessor);
        return db;
    }

    private static async Task<LegacyFixture> SeedLegacyFixtureAsync(AeroLinkDbContext db)
    {
        var now = "2026-08-23 12:00:00+00";
        var programId = Guid.NewGuid();
        var projectId = Guid.NewGuid();
        var releaseId = Guid.NewGuid();
        var baselineId = Guid.NewGuid();
        var systemId = Guid.NewGuid();
        var highCaseId = Guid.NewGuid();
        var lowCaseId = Guid.NewGuid();
        var systemRevisionId = Guid.NewGuid();
        var highRevisionId = Guid.NewGuid();
        var highRevision2Id = Guid.NewGuid();
        var lowRevisionId = Guid.NewGuid();
        var baselineSelectionSystemId = Guid.NewGuid();
        var baselineSelectionHighId = Guid.NewGuid();
        var baselineSelectionLowId = Guid.NewGuid();

        await Sql(db, $"INSERT INTO \"programs\" (\"Id\", \"Name\", \"Code\") VALUES ('{programId}', 'Issue 724 qualification', 'Q724');");
        await Sql(db, $"INSERT INTO \"projects\" (\"Id\", \"ProgramId\", \"Name\", \"SoftwareProduct\") VALUES ('{projectId}', '{programId}', 'Issue 724 project', 'Qualification product');");
        await Sql(db, $"INSERT INTO \"software_releases\" (\"Id\", \"ProjectId\", \"Version\", \"IsReleased\") VALUES ('{releaseId}', '{projectId}', '1.0', TRUE);");
        await Sql(db, $"INSERT INTO \"candidate_baselines\" (\"Id\", \"BaseNumber\", \"Revision\", \"ProjectId\", \"ReleaseId\", \"PredecessorBaselineId\", \"Name\", \"CreatedAt\", \"State\", \"UpdatedAt\", \"Version\") VALUES ('{baselineId}', 'BL-724-001', 0, '{projectId}', '{releaseId}', NULL, 'Retained pre-feature baseline', '{now}', 'Frozen', '{now}', 1);");
        await ProcedureSql(db, systemId, projectId, "SYSTP-000009", "Legacy System Procedure", "System", "System", "Procedure", now);
        await ProcedureSql(db, highCaseId, projectId, "HLRTC-000041", "Legacy HLR Case", "HighLevel", "HighLevelSoftware", "Case", now);
        await ProcedureSql(db, lowCaseId, projectId, "LLRTC-000017", "Legacy LLR Case", "LowLevel", "LowLevelSoftware", "Case", now);
        await RevisionSql(db, systemRevisionId, systemId, 0, "legacy system objective", "legacy system setup", "legacy system steps", "legacy system expected", baselineId, now);
        await RevisionSql(db, highRevisionId, highCaseId, 0, "legacy high objective", "legacy high setup", "legacy high steps", "legacy high expected", baselineId, now);
        await RevisionSql(db, highRevision2Id, highCaseId, 1, "legacy high revised objective", "legacy high setup", "legacy high revised steps", "legacy high revised expected", baselineId, now);
        await RevisionSql(db, lowRevisionId, lowCaseId, 0, "legacy low objective", "legacy low setup", "legacy low steps", "legacy low expected", baselineId, now);
        await Sql(db, $"INSERT INTO \"baseline_test_procedures\" (\"Id\", \"BaselineId\", \"ProcedureId\", \"RevisionId\") VALUES ('{baselineSelectionSystemId}', '{baselineId}', '{systemId}', '{systemRevisionId}'), ('{baselineSelectionHighId}', '{baselineId}', '{highCaseId}', '{highRevisionId}'), ('{baselineSelectionLowId}', '{baselineId}', '{lowCaseId}', '{lowRevisionId}');");
        await Sql(db, "INSERT INTO \"identifier_sequences\" (\"Id\", \"Scope\", \"NextValue\", \"ConcurrencyStamp\") VALUES "
            + $"('{Guid.NewGuid()}', 'HLRTP', 91, 0), ('{Guid.NewGuid()}', 'LLRTP', 53, 0) "
            + "ON CONFLICT (\"Scope\") DO UPDATE SET \"NextValue\" = EXCLUDED.\"NextValue\", \"ConcurrencyStamp\" = 0;");
        return new LegacyFixture(projectId, releaseId, baselineId, systemId, highCaseId, highRevisionId, highRevision2Id, lowRevisionId);
    }

    private static Task ProcedureSql(AeroLinkDbContext db, Guid id, Guid projectId, string number, string title,
        string level, string discipline, string kind, string now) =>
        Sql(db, $"INSERT INTO \"test_procedures\" (\"Id\", \"ProjectId\", \"BaseNumber\", \"Title\", \"OwnerId\", \"CreatedAt\", \"Level\", \"ArtifactDiscipline\", \"ArtifactKind\", \"UpdatedAt\", \"Version\") VALUES ('{id}', '{projectId}', '{number}', '{title}', 'qualification', '{now}', '{level}', '{discipline}', '{kind}', '{now}', 1);");

    private static Task RevisionSql(AeroLinkDbContext db, Guid id, Guid procedureId, int revision, string objective,
        string preconditions, string steps, string expected, Guid baselineId, string now) =>
        Sql(db, $"INSERT INTO \"test_procedure_revisions\" (\"Id\", \"ProcedureId\", \"Revision\", \"Objective\", \"Preconditions\", \"Steps\", \"ExpectedResult\", \"State\", \"AuthorId\", \"CreatedAt\", \"SelectedApproverId\", \"SourceTestChangeRequestId\", \"EffectiveBaselineId\", \"SourceChangeRequestsJson\") VALUES ('{id}', '{procedureId}', {revision}, '{objective}', '{preconditions}', '{steps}', '{expected}', 'Approved', 'qualification', '{now}', NULL, NULL, '{baselineId}', '[]');");

    private static Task Sql(AeroLinkDbContext db, string sql) => db.Database.ExecuteSqlRawAsync(sql);

    private static async Task<(int Procedures, int Revisions, int Links, int Sequences)> SnapshotCountsAsync(AeroLinkDbContext db) =>
        (await db.TestProcedures.CountAsync(), await db.TestProcedureRevisions.CountAsync(),
            await db.TestCaseProcedureLinks.CountAsync(), await db.IdentifierSequences.CountAsync());

    private static DbContextOptions<AeroLinkDbContext> Options(string connection) =>
        new DbContextOptionsBuilder<AeroLinkDbContext>().UseNpgsql(connection).Options;

    private sealed record LegacyFixture(Guid ProjectId, Guid ReleaseId, Guid BaselineId, Guid SystemId,
        Guid HighCaseId, Guid HighRevisionId, Guid HighRevision2Id, Guid LowRevisionId);
}
