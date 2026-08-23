using AeroLink.Domain.Baselines;
using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Common;
using AeroLink.Domain.Hierarchy;
using AeroLink.Domain.Programs;
using AeroLink.Domain.Requirements;
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
            var high = new TestProcedure(project.Id, "HLRTC-000001", "High-level case", "tester", now, TestProcedureLevel.HighLevel);
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
                $"UPDATE test_procedures SET BaseNumber = 'HLRTP-000001' WHERE Id = {high.Id}"));
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
            var procedure = new TestProcedure(project.Id, "HLRTC-000001", "Verify both behaviors", "tester", now);
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

    [Fact]
    public async Task Dormant_procedure_rejects_case_parents_with_mixed_baseline_provenance()
    {
        var options = new DbContextOptionsBuilder<AeroLinkDbContext>().UseSqlite("Data Source=:memory:").Options;
        await using var db = new AeroLinkDbContext(options);
        await db.Database.OpenConnectionAsync();
        await db.Database.EnsureCreatedAsync();
        var now = DateTimeOffset.UtcNow;
        var program = new ProgramRecord("Procedure parent provenance", "PRP");
        var project = new ProjectRecord(program.Id, "Software", "Procedure parent provenance project");
        var release = new SoftwareRelease(project.Id, "1.0", false);
        var firstBaseline = new CandidateBaseline("SW-01.00", 0, project.Id, release.Id, null, "First", "tester", now);
        var secondBaseline = new CandidateBaseline("SW-01.01", 0, project.Id, release.Id, firstBaseline.Id, "Second", "tester", now);
        var firstCase = new TestProcedure(project.Id, "HLRTC-000001", "First Case", "tester", now, TestProcedureLevel.HighLevel);
        var secondCase = new TestProcedure(project.Id, "HLRTC-000002", "Second Case", "tester", now, TestProcedureLevel.HighLevel);
        var firstCaseRevision = new TestProcedureRevision(firstCase.Id, 0, "Objective", "Pre", "Steps", "Expected",
            TestProcedureState.Approved, "tester", now, effectiveBaselineId: firstBaseline.Id);
        var secondCaseRevision = new TestProcedureRevision(secondCase.Id, 0, "Objective", "Pre", "Steps", "Expected",
            TestProcedureState.Approved, "tester", now, effectiveBaselineId: secondBaseline.Id);
        var procedure = new TestProcedure(project.Id, "HLRTP-000001", "Dormant Procedure", "tester", now,
            TestProcedureLevel.HighLevel, artifactKind: VerificationArtifactKind.Procedure,
            parentKind: VerificationProcedureParentKind.Allocated);
        var procedureRevision = new TestProcedureRevision(procedure.Id, 0, "Procedure objective", "Procedure preconditions",
            "Procedure summary", "Procedure result", TestProcedureState.Draft, "tester", now,
            environmentSetup: "Bench", testData: "Known data", orderedSteps: "1. Execute",
            expectedObservations: "Expected", cleanup: "Restore", toolingAutomation: "Runner",
            parentKind: VerificationProcedureParentKind.Allocated);
        db.AddRange(program, project, release, firstBaseline, secondBaseline, firstCase, secondCase,
            firstCaseRevision, secondCaseRevision, procedure, procedureRevision,
            new TestCaseProcedureLink(firstCaseRevision.Id, procedureRevision.Id),
            new TestCaseProcedureLink(secondCaseRevision.Id, procedureRevision.Id));

        await Assert.ThrowsAsync<DomainException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task Parent_classification_is_enforced_when_existing_links_are_added_or_removed_on_both_save_paths()
    {
        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AeroLinkDbContext>().UseSqlite(connection).Options;
        await using var db = new AeroLinkDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var now = DateTimeOffset.UtcNow;
        var program = new ProgramRecord("Parent mutation", "PMUT");
        var project = new ProjectRecord(program.Id, "Software", "Parent mutation project");
        var release = new SoftwareRelease(project.Id, "1.0", false);
        var configuration = LegacyDefaultProjectLadderFactory.Create(project.Id, now);
        var @case = new TestProcedure(project.Id, "HLRTC-100001", "Case", "tester", now);
        var caseRevision = new TestProcedureRevision(@case.Id, 0, "Case objective", "Case preconditions",
            "Case steps", "Case result", TestProcedureState.Draft, "tester", now);
        var derived = new TestProcedure(project.Id, "HLRTP-100001", "Derived Procedure", "tester", now,
            TestProcedureLevel.HighLevel, artifactKind: VerificationArtifactKind.Procedure,
            parentKind: VerificationProcedureParentKind.Derived);
        var derivedRevision = new TestProcedureRevision(derived.Id, 0, "Procedure objective", "Procedure preconditions",
            "Procedure summary", "Procedure result", TestProcedureState.Draft, "tester", now,
            environmentSetup: "Bench", testData: "Known data", orderedSteps: "1. Execute",
            expectedObservations: "Expected", cleanup: "Restore", toolingAutomation: "Runner",
            parentKind: VerificationProcedureParentKind.Derived, derivedRationale: "Standalone while dormant.");
        db.AddRange(program, project, release, configuration, @case, caseRevision, derived, derivedRevision);
        await db.SaveChangesAsync();

        var asyncAdded = new TestCaseProcedureLink(caseRevision.Id, derivedRevision.Id);
        db.Add(asyncAdded);
        await Assert.ThrowsAsync<DomainException>(() => db.SaveChangesAsync());
        db.Entry(asyncAdded).State = EntityState.Detached;

        var syncAdded = new TestCaseProcedureLink(caseRevision.Id, derivedRevision.Id);
        db.Add(syncAdded);
        Assert.Throws<DomainException>(() => db.SaveChanges());
        db.Entry(syncAdded).State = EntityState.Detached;

        var allocated = new TestProcedure(project.Id, "HLRTP-100002", "Allocated Procedure", "tester", now,
            TestProcedureLevel.HighLevel, artifactKind: VerificationArtifactKind.Procedure,
            parentKind: VerificationProcedureParentKind.Allocated);
        var allocatedRevision = new TestProcedureRevision(allocated.Id, 0, "Procedure objective", "Procedure preconditions",
            "Procedure summary", "Procedure result", TestProcedureState.Draft, "tester", now,
            environmentSetup: "Bench", testData: "Known data", orderedSteps: "1. Execute",
            expectedObservations: "Expected", cleanup: "Restore", toolingAutomation: "Runner",
            parentKind: VerificationProcedureParentKind.Allocated);
        var allocatedLink = new TestCaseProcedureLink(caseRevision.Id, allocatedRevision.Id);
        db.AddRange(allocated, allocatedRevision, allocatedLink);
        await db.SaveChangesAsync();
        db.Remove(allocatedLink);
        await Assert.ThrowsAsync<DomainException>(() => db.SaveChangesAsync());
        db.Entry(allocatedLink).State = EntityState.Detached;

        var syncAllocated = new TestProcedure(project.Id, "HLRTP-100003", "Sync Allocated Procedure", "tester", now,
            TestProcedureLevel.HighLevel, artifactKind: VerificationArtifactKind.Procedure,
            parentKind: VerificationProcedureParentKind.Allocated);
        var syncAllocatedRevision = new TestProcedureRevision(syncAllocated.Id, 0, "Procedure objective", "Procedure preconditions",
            "Procedure summary", "Procedure result", TestProcedureState.Draft, "tester", now,
            environmentSetup: "Bench", testData: "Known data", orderedSteps: "1. Execute",
            expectedObservations: "Expected", cleanup: "Restore", toolingAutomation: "Runner",
            parentKind: VerificationProcedureParentKind.Allocated);
        var syncAllocatedLink = new TestCaseProcedureLink(caseRevision.Id, syncAllocatedRevision.Id);
        db.AddRange(syncAllocated, syncAllocatedRevision, syncAllocatedLink);
        await db.SaveChangesAsync();
        db.Remove(syncAllocatedLink);
        Assert.Throws<DomainException>(() => db.SaveChanges());
    }

    [Fact]
    public async Task Direct_software_procedure_coverage_is_refused_for_new_rows_on_both_save_paths()
    {
        static async Task AssertRejectedAsync(bool synchronous)
        {
            await using var connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<AeroLinkDbContext>().UseSqlite(connection).Options;
            await using var db = new AeroLinkDbContext(options);
            await db.Database.EnsureCreatedAsync();
            var now = DateTimeOffset.UtcNow;
            var program = new ProgramRecord("Direct coverage", "DCOV");
            var project = new ProjectRecord(program.Id, "Software", "Direct coverage project");
            var release = new SoftwareRelease(project.Id, "1.0", false);
            var configuration = LegacyDefaultProjectLadderFactory.Create(project.Id, now);
            var source = new SystemChangeRequest("SRCR-100001", 0, project.Id, release.Id,
                "Coverage", "Problem", "Analysis", "Solution", "tester", now);
            var baseline = new CandidateBaseline("SW-10.00", 0, project.Id, release.Id, null,
                "Coverage", "tester", now);
            var requirement = new RequirementArtifact(project.Id, "HLR-100001", RequirementLevel.HighLevel, now);
            var requirementRevision = new RequirementRevision(requirement.Id, 0, "A software obligation.",
                "Coverage", "Test", RequirementRevisionState.Active, source.Id, baseline.Id, now);
            var procedure = new TestProcedure(project.Id, "HLRTP-100004", "Procedure", "tester", now,
                TestProcedureLevel.HighLevel, artifactKind: VerificationArtifactKind.Procedure,
                parentKind: VerificationProcedureParentKind.Derived);
            var procedureRevision = new TestProcedureRevision(procedure.Id, 0, "Procedure objective",
                "Procedure preconditions", "Procedure summary", "Procedure result", TestProcedureState.Draft,
                "tester", now, environmentSetup: "Bench", testData: "Known data", orderedSteps: "1. Execute",
                expectedObservations: "Expected", cleanup: "Restore", toolingAutomation: "Runner",
                parentKind: VerificationProcedureParentKind.Derived, derivedRationale: "Standalone while dormant.");
            db.AddRange(program, project, release, configuration, source, baseline, requirement,
                requirementRevision, procedure, procedureRevision,
                new TestRequirementCoverage(procedureRevision.Id, requirementRevision.Id));
            if (synchronous)
                Assert.Throws<DomainException>(() => db.SaveChanges());
            else
                await Assert.ThrowsAsync<DomainException>(() => db.SaveChangesAsync());
        }

        await AssertRejectedAsync(synchronous: false);
        await AssertRejectedAsync(synchronous: true);
    }

    [Fact]
    public async Task Added_software_procedure_headers_require_same_unit_initial_revisions_on_both_save_paths()
    {
        static async Task AssertMissingInitialRevisionAsync(bool synchronous)
        {
            await using var connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<AeroLinkDbContext>().UseSqlite(connection).Options;
            await using var db = new AeroLinkDbContext(options);
            await db.Database.EnsureCreatedAsync();
            var now = DateTimeOffset.UtcNow;
            var program = new ProgramRecord("Procedure header integrity", $"PHI{Guid.NewGuid():N}"[..7]);
            var project = new ProjectRecord(program.Id, "Software", "Procedure header integrity project");
            var procedure = new TestProcedure(project.Id, "HLRTP-100006", "Procedure without revision", "tester", now,
                TestProcedureLevel.HighLevel, artifactKind: VerificationArtifactKind.Procedure,
                parentKind: VerificationProcedureParentKind.Derived);
            db.AddRange(program, project, procedure);
            if (synchronous)
                Assert.Throws<DomainException>(() => db.SaveChanges());
            else
                await Assert.ThrowsAsync<DomainException>(() => db.SaveChangesAsync());
        }

        static async Task AssertUnclassifiedInitialRevisionAsync(bool synchronous)
        {
            await using var connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<AeroLinkDbContext>().UseSqlite(connection).Options;
            await using var db = new AeroLinkDbContext(options);
            await db.Database.EnsureCreatedAsync();
            var now = DateTimeOffset.UtcNow;
            var program = new ProgramRecord("Procedure revision integrity", $"PRI{Guid.NewGuid():N}"[..7]);
            var project = new ProjectRecord(program.Id, "Software", "Procedure revision integrity project");
            var procedure = new TestProcedure(project.Id, "HLRTP-100007", "Unclassified Procedure", "tester", now,
                TestProcedureLevel.HighLevel, artifactKind: VerificationArtifactKind.Procedure,
                parentKind: VerificationProcedureParentKind.Derived);
            var revision = new TestProcedureRevision(procedure.Id, 0, "Objective", "Preconditions", "1. Execute",
                "Expected", TestProcedureState.Draft, "tester", now, environmentSetup: "Bench",
                testData: "Known data", orderedSteps: "1. Execute", expectedObservations: "Observed",
                cleanup: "Restore", toolingAutomation: "Runner");
            db.AddRange(program, project, procedure, revision);
            if (synchronous)
                Assert.Throws<DomainException>(() => db.SaveChanges());
            else
                await Assert.ThrowsAsync<DomainException>(() => db.SaveChangesAsync());
        }

        await AssertMissingInitialRevisionAsync(synchronous: false);
        await AssertMissingInitialRevisionAsync(synchronous: true);

        static async Task AssertPseudoInitialRevisionAsync(bool synchronous, int revisionNumber, TestProcedureState state)
        {
            await using var connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<AeroLinkDbContext>().UseSqlite(connection).Options;
            await using var db = new AeroLinkDbContext(options);
            await db.Database.EnsureCreatedAsync();
            var now = DateTimeOffset.UtcNow;
            var program = new ProgramRecord("Procedure pseudo initial", $"PPI{Guid.NewGuid():N}"[..7]);
            var project = new ProjectRecord(program.Id, "Software", "Procedure pseudo initial project");
            var procedure = new TestProcedure(project.Id, "HLRTP-100009", "Pseudo-initial Procedure", "tester", now,
                TestProcedureLevel.HighLevel, artifactKind: VerificationArtifactKind.Procedure,
                parentKind: VerificationProcedureParentKind.Derived);
            var revision = new TestProcedureRevision(procedure.Id, revisionNumber, "Objective", "Preconditions",
                "1. Execute", "Expected", state, "tester", now, environmentSetup: "Bench",
                testData: "Known data", orderedSteps: "1. Execute", expectedObservations: "Observed",
                cleanup: "Restore", toolingAutomation: "Runner", parentKind: VerificationProcedureParentKind.Derived,
                derivedRationale: "Standalone while dormant.");
            db.AddRange(program, project, procedure, revision);
            if (synchronous)
                Assert.Throws<DomainException>(() => db.SaveChanges());
            else
                await Assert.ThrowsAsync<DomainException>(() => db.SaveChangesAsync());
        }

        await AssertPseudoInitialRevisionAsync(synchronous: false, revisionNumber: 5, TestProcedureState.Draft);
        await AssertPseudoInitialRevisionAsync(synchronous: true, revisionNumber: 5, TestProcedureState.Draft);
        await AssertPseudoInitialRevisionAsync(synchronous: false, revisionNumber: 0, TestProcedureState.Retired);
        await AssertPseudoInitialRevisionAsync(synchronous: true, revisionNumber: 0, TestProcedureState.Retired);
        await AssertUnclassifiedInitialRevisionAsync(synchronous: false);
        await AssertUnclassifiedInitialRevisionAsync(synchronous: true);

        await using var validConnection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=:memory:");
        await validConnection.OpenAsync();
        var validOptions = new DbContextOptionsBuilder<AeroLinkDbContext>().UseSqlite(validConnection).Options;
        await using var validDb = new AeroLinkDbContext(validOptions);
        await validDb.Database.EnsureCreatedAsync();
        var validNow = DateTimeOffset.UtcNow;
        var validProgram = new ProgramRecord("Valid Procedure authoring", "VPA");
        var validProject = new ProjectRecord(validProgram.Id, "Software", "Valid Procedure authoring project");
        var validProcedure = new TestProcedure(validProject.Id, "HLRTP-100008", "Valid Procedure", "tester", validNow,
            TestProcedureLevel.HighLevel, artifactKind: VerificationArtifactKind.Procedure,
            parentKind: VerificationProcedureParentKind.Derived);
        var validRevision = new TestProcedureRevision(validProcedure.Id, 0, "Objective", "Preconditions", "1. Execute",
            "Expected", TestProcedureState.Draft, "tester", validNow, environmentSetup: "Bench",
            testData: "Known data", orderedSteps: "1. Execute", expectedObservations: "Observed",
            cleanup: "Restore", toolingAutomation: "Runner", parentKind: VerificationProcedureParentKind.Derived,
            derivedRationale: "Standalone while dormant.");
        validDb.AddRange(validProgram, validProject, validProcedure, validRevision);
        await validDb.SaveChangesAsync();
        Assert.Equal(1, await validDb.TestProcedureRevisions.CountAsync(x => x.ProcedureId == validProcedure.Id));
    }

    [Fact]
    public async Task Authoring_service_reports_stale_revise_and_retire_intents_explicitly()
    {
        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AeroLinkDbContext>().UseSqlite(connection).Options;
        await using var db = new AeroLinkDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var now = DateTimeOffset.UtcNow;
        var program = new ProgramRecord("Service concurrency", "SCON");
        var project = new ProjectRecord(program.Id, "Software", "Service concurrency project");
        var release = new SoftwareRelease(project.Id, "1.0", false);
        var configuration = LegacyDefaultProjectLadderFactory.Create(project.Id, now);
        var procedure = new TestProcedure(project.Id, "HLRTP-100005", "Procedure", "tester", now,
            TestProcedureLevel.HighLevel, artifactKind: VerificationArtifactKind.Procedure,
            parentKind: VerificationProcedureParentKind.Derived);
        var revision = new TestProcedureRevision(procedure.Id, 0, "Procedure objective", "Procedure preconditions",
            "Procedure summary", "Procedure result", TestProcedureState.Draft, "tester", now,
            environmentSetup: "Bench", testData: "Known data", orderedSteps: "1. Execute",
            expectedObservations: "Expected", cleanup: "Restore", toolingAutomation: "Runner",
            parentKind: VerificationProcedureParentKind.Derived, derivedRationale: "Standalone while dormant.");
        db.AddRange(program, project, release, configuration, procedure, revision);
        await db.SaveChangesAsync();

        var service = new VerificationProcedureAuthoringService(db);
        var content = new VerificationProcedureContent("Bench", "Known data", "1. Execute", "Expected", "Restore", "Runner");
        await Assert.ThrowsAsync<VerificationProcedureConcurrencyException>(() => service.ReviseAsync(
            procedure.Id, "tester", content, VerificationProcedureParentKind.Derived, null,
            "Still standalone.", now, CancellationToken.None, expectedVersion: 0));
        await Assert.ThrowsAsync<VerificationProcedureConcurrencyException>(() => service.RetireAsync(
            procedure.Id, "tester", "Stale intent.", now, CancellationToken.None, expectedVersion: 0));
    }
}
