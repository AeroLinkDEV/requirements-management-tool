using AeroLink.Domain.Baselines;
using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Programs;
using AeroLink.Domain.Verification;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AeroLink.Infrastructure.Tests;

public sealed class VerificationPersistenceTests
{
    [Fact]
    public async Task Neutral_artifact_identity_is_persisted_and_database_rejects_drift()
    {
        var path = Path.Combine(Path.GetTempPath(), $"aerolink-neutral-verification-{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<AeroLinkDbContext>().UseSqlite($"Data Source={path};Pooling=False;Foreign Keys=True").Options;
        try
        {
            await using var db = new AeroLinkDbContext(options);
            await db.Database.EnsureCreatedAsync();
            var now = DateTimeOffset.UtcNow;
            var program = new ProgramRecord("Neutral verification", "NEUT");
            var project = new ProjectRecord(program.Id, "Neutral", "Neutral verification project");
            db.AddRange(program, project);
            var system = new TestProcedure(project.Id, "SYSTP-000001", "System", "tester", now, TestProcedureLevel.System);
            var high = new TestProcedure(project.Id, "HLRTP-000001", "High-level case", "tester", now, TestProcedureLevel.HighLevel);
            db.AddRange(system, high);
            await db.SaveChangesAsync();

            var persisted = await db.TestProcedures.AsNoTracking().ToListAsync();
            var persistedSystem = persisted.Single(x => x.Level == TestProcedureLevel.System);
            var persistedHigh = persisted.Single(x => x.Level == TestProcedureLevel.HighLevel);
            Assert.Equal(VerificationDiscipline.System, persistedSystem.ArtifactDiscipline);
            Assert.Equal(VerificationArtifactKind.Procedure, persistedSystem.ArtifactKind);
            Assert.Equal(VerificationDiscipline.HighLevelSoftware, persistedHigh.ArtifactDiscipline);
            Assert.Equal(VerificationArtifactKind.Case, persistedHigh.ArtifactKind);
            await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(() => db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE test_procedures SET ArtifactKind = 'Procedure' WHERE Id = {high.Id}"));
            await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(() => db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE test_procedures SET ArtifactDiscipline = 'System' WHERE Id = {high.Id}"));
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { File.Delete(path); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task One_procedure_covers_multiple_exact_requirements_and_retest_preserves_failed_run()
    {
        var path = Path.Combine(Path.GetTempPath(), $"aerolink-verification-{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<AeroLinkDbContext>().UseSqlite($"Data Source={path};Pooling=False").Options;
        try
        {
            await using var db = new AeroLinkDbContext(options); await db.Database.EnsureCreatedAsync(); var now = DateTimeOffset.UtcNow;
            var program = new ProgramRecord("Verification", "VRFY"); var project = new ProjectRecord(program.Id, "Software", "FMS"); var release = new SoftwareRelease(project.Id, "3.3", false);
            var scr = new SystemChangeRequest("HLRCR-00010", 0, project.Id, release.Id, "Requirements", "P", "A", "S", "author", now, ChangeRequestType.Software, softwareLevel: RequirementLevel.HighLevel);
            scr.AddRequirementChange("author", "SWR-00000001", 0, RequirementLevel.HighLevel, RequirementChangeKind.Introduce, "First behavior", "R", "Test", now);
            scr.AddRequirementChange("author", "SWR-00000002", 0, RequirementLevel.HighLevel, RequirementChangeKind.Introduce, "Second behavior", "R", "Test", now);
            scr.SubmitForReview("author", [new("reviewer", "Reviewer")], now); scr.ApproveActiveStage("reviewer", now);
            var baseline = new CandidateBaseline("SW-01.00", 0, project.Id, release.Id, null, "3.3", "cm", now); baseline.Select(scr, "cm", now); baseline.Freeze("cm", now);
            db.AddRange(program, project, release, scr, baseline); await db.SaveChangesAsync(); await new RequirementBaselineMaterializer(db, new VerificationImpactService(db)).MaterializeAsync(baseline.Id, "cm", now, default);
            var requirementIds = await db.BaselineRequirements.Where(x => x.BaselineId == baseline.Id).Select(x => x.RevisionId).ToListAsync();
            var procedure = new TestProcedure(project.Id, "SWTP-00000001", "Verify both behaviors", "tester", now);
            var revision = new TestProcedureRevision(procedure.Id, 0, "Verify", "Configured", "Execute", "Both behaviors observed", TestProcedureState.Approved, "tester", now);
            db.AddRange(procedure, revision); db.TestCoverage.AddRange(requirementIds.Select(x => new TestRequirementCoverage(revision.Id, x))); await db.SaveChangesAsync();
            var failed = new TestExecution(project.Id, revision.Id, null, null, TestOutcome.Fail, "tester", "Rig A", "Second behavior was absent", "evidence/fail-001", now, now);
            var passed = new TestExecution(project.Id, revision.Id, null, failed.Id, TestOutcome.Pass, "tester", "Rig A", "Both behaviors observed after correction", "evidence/pass-002", now.AddHours(1), now.AddHours(1));
            db.AddRange(failed, passed); await db.SaveChangesAsync();
            Assert.Equal(2, await db.TestCoverage.CountAsync(x => x.ProcedureRevisionId == revision.Id));
            var runs = (await db.TestExecutions.ToListAsync()).OrderBy(x => x.ExecutedAt).ToList(); Assert.Equal([TestOutcome.Fail, TestOutcome.Pass], runs.Select(x => x.Outcome)); Assert.Equal(failed.Id, runs[1].RetestOfExecutionId);
        }
        finally { File.Delete(path); }
    }
}
