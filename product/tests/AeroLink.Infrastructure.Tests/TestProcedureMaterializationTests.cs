using AeroLink.Domain.Baselines;
using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Common;
using AeroLink.Domain.Programs;
using AeroLink.Domain.Requirements;
using AeroLink.Domain.Verification;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace AeroLink.Infrastructure.Tests;

/// <summary>
/// The procedure twin of <see cref="RequirementMaterializationTests"/>. Same lifecycle, same assertions, because
/// a test procedure is built and handled the way a requirement is.
/// </summary>
public sealed class TestProcedureMaterializationTests
{
    [Fact]
    public async Task Introduce_modify_and_retire_preserve_revision_history_and_exact_membership()
    {
        var path = Path.Combine(Path.GetTempPath(), $"aerolink-tp-{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<AeroLinkDbContext>().UseSqlite($"Data Source={path};Pooling=False").Options;
        try
        {
            await using var db = new AeroLinkDbContext(options);
            await db.Database.EnsureCreatedAsync();
            var now = DateTimeOffset.UtcNow;
            var (project, release) = await SeedProjectAsync(db, "FMSP");

            var first = await MaterializeAsync(db, project.Id, release.Id, "SW-00.10", null, now,
                Change("SYSTP-000001", 0, TestProcedureChangeKind.Introduce, "Oceanic sequencing"));
            var second = await MaterializeAsync(db, project.Id, release.Id, "SW-00.20", first.Id, now,
                Change("SYSTP-000001", 1, TestProcedureChangeKind.Modify, "Oceanic sequencing, clarified"));
            var third = await MaterializeAsync(db, project.Id, release.Id, "SW-00.30", second.Id, now,
                Change("SYSTP-000001", 2, TestProcedureChangeKind.Retire, ""));

            var procedure = await db.TestProcedures.SingleAsync();
            var history = await db.TestProcedureRevisions.Where(x => x.ProcedureId == procedure.Id)
                .OrderBy(x => x.Revision).ToListAsync();

            Assert.Equal([0, 1, 2], history.Select(x => x.Revision));
            Assert.Equal(TestProcedureState.Retired, history[^1].State);
            Assert.Single(await db.BaselineTestProcedures.Where(x => x.BaselineId == first.Id).ToListAsync());
            var secondMember = await db.BaselineTestProcedures.SingleAsync(x => x.BaselineId == second.Id);
            Assert.Equal(history[1].Id, secondMember.RevisionId);
            // Retired means gone from the build, not gone from history — the same as a retired requirement.
            Assert.Empty(await db.BaselineTestProcedures.Where(x => x.BaselineId == third.Id).ToListAsync());
            Assert.Equal("Oceanic sequencing, clarified", (await db.TestProcedures.SingleAsync()).Title);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Every_revision_names_the_test_change_request_and_baseline_that_produced_it()
    {
        var path = Path.Combine(Path.GetTempPath(), $"aerolink-tp-attr-{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<AeroLinkDbContext>().UseSqlite($"Data Source={path};Pooling=False").Options;
        try
        {
            await using var db = new AeroLinkDbContext(options);
            await db.Database.EnsureCreatedAsync();
            var now = DateTimeOffset.UtcNow;
            var (project, release) = await SeedProjectAsync(db, "FMSA");

            var baseline = await MaterializeAsync(db, project.Id, release.Id, "SW-00.10", null, now,
                Change("SYSTP-000001", 0, TestProcedureChangeKind.Introduce, "Oceanic sequencing"));

            var revision = await db.TestProcedureRevisions.SingleAsync();
            var tcr = await db.TestChangeReviews.SingleAsync();
            Assert.Equal(tcr.Id, revision.SourceTestChangeRequestId);
            Assert.Equal(baseline.Id, revision.EffectiveBaselineId);
            // Credited to the engineer who authored the package, not to whoever ran the materialization.
            Assert.Equal("verification.engineer", revision.AuthorId);

            var reloaded = await db.CandidateBaselines.SingleAsync(x => x.Id == baseline.Id);
            Assert.Equal(64, reloaded.TestProceduresHash!.Length);
            Assert.NotNull(reloaded.TestProceduresMaterializedAt);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task A_driving_requirement_becomes_real_coverage_only_once_the_procedure_revision_exists()
    {
        var path = Path.Combine(Path.GetTempPath(), $"aerolink-tp-cov-{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<AeroLinkDbContext>().UseSqlite($"Data Source={path};Pooling=False").Options;
        try
        {
            await using var db = new AeroLinkDbContext(options);
            await db.Database.EnsureCreatedAsync();
            var now = DateTimeOffset.UtcNow;
            var (project, release) = await SeedProjectAsync(db, "FMSC");

            var requirementRevisionId = await MaterializeRequirementAsync(db, project.Id, release.Id, now);
            var stale = Guid.NewGuid();

            // The proposal named one real revision and one that no longer exists. A stale identifier from a
            // draft is not an instruction to link to nothing.
            Assert.Empty(await db.TestCoverage.ToListAsync());
            await MaterializeAsync(db, project.Id, release.Id, "SW-00.20", null, now,
                Change("SYSTP-000001", 0, TestProcedureChangeKind.Introduce, "Oceanic sequencing",
                    JsonSerializer.Serialize(new[] { requirementRevisionId, stale })));

            var coverage = await db.TestCoverage.SingleAsync();
            Assert.Equal(requirementRevisionId, coverage.RequirementRevisionId);
            Assert.Equal((await db.TestProcedureRevisions.SingleAsync()).Id, coverage.ProcedureRevisionId);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task A_modification_of_a_procedure_no_build_carries_is_refused()
    {
        var path = Path.Combine(Path.GetTempPath(), $"aerolink-tp-orphan-{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<AeroLinkDbContext>().UseSqlite($"Data Source={path};Pooling=False").Options;
        try
        {
            await using var db = new AeroLinkDbContext(options);
            await db.Database.EnsureCreatedAsync();
            var now = DateTimeOffset.UtcNow;
            var (project, release) = await SeedProjectAsync(db, "FMSO");

            var error = await Assert.ThrowsAsync<DomainException>(() =>
                MaterializeAsync(db, project.Id, release.Id, "SW-00.10", null, now,
                    Change("SYSTP-000009", 1, TestProcedureChangeKind.Modify, "Nothing to modify")));
            Assert.Contains("SYSTP-000009", error.Message);

            // And a revision that does not advance is refused, so history cannot be overwritten in place.
            var first = await MaterializeAsync(db, project.Id, release.Id, "SW-00.20", null, now,
                Change("SYSTP-000001", 0, TestProcedureChangeKind.Introduce, "Oceanic sequencing"));
            await Assert.ThrowsAsync<DomainException>(() =>
                MaterializeAsync(db, project.Id, release.Id, "SW-00.30", first.Id, now,
                    Change("SYSTP-000001", 0, TestProcedureChangeKind.Modify, "Same revision again")));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task A_predecessor_with_no_procedure_manifest_starts_the_successor_empty_rather_than_failing()
    {
        var path = Path.Combine(Path.GetTempPath(), $"aerolink-tp-legacy-{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<AeroLinkDbContext>().UseSqlite($"Data Source={path};Pooling=False").Options;
        try
        {
            await using var db = new AeroLinkDbContext(options);
            await db.Database.EnsureCreatedAsync();
            var now = DateTimeOffset.UtcNow;
            var (project, release) = await SeedProjectAsync(db, "FMSL");

            // Every build that exists today is this: frozen and materialized for requirements, with no procedure
            // manifest at all. Its successor has to be able to start.
            var legacy = new CandidateBaseline("SW-00.10", 0, project.Id, release.Id, null, "Legacy", "cm", now);
            legacy.Select(ApprovedChangeRequest(project.Id, release.Id, "SRCR-00099", now), "cm", now);
            legacy.Freeze("cm", now);
            legacy.MarkRequirementsMaterialized("cm", new string('a', 64), 0, now);
            db.Add(legacy);
            await db.SaveChangesAsync();

            var successor = await MaterializeAsync(db, project.Id, release.Id, "SW-00.20", legacy.Id, now,
                Change("SYSTP-000001", 0, TestProcedureChangeKind.Introduce, "Oceanic sequencing"));

            Assert.Single(await db.BaselineTestProcedures.Where(x => x.BaselineId == successor.Id).ToListAsync());
        }
        finally { File.Delete(path); }
    }

    private static async Task<(ProjectRecord, SoftwareRelease)> SeedProjectAsync(AeroLinkDbContext db, string prefix)
    {
        var program = new ProgramRecord("FMS", prefix);
        var project = new ProjectRecord(program.Id, "Software", "FMS Software");
        var release = new SoftwareRelease(project.Id, "3.3", false);
        db.AddRange(program, project, release);
        await db.SaveChangesAsync();
        return (project, release);
    }

    private static TestProcedureChangeDraft Change(string baseNumber, int revision, TestProcedureChangeKind kind,
        string title, string drivingRequirementRevisionIdsJson = "[]") =>
        new(baseNumber, revision, TestProcedureLevel.System, kind, title,
            kind == TestProcedureChangeKind.Retire ? "" : "Verify oceanic waypoint sequencing.",
            kind == TestProcedureChangeKind.Retire ? "" : "The aircraft is in cruise on an oceanic plan.",
            kind == TestProcedureChangeKind.Retire ? "" : "1. Load the plan. 2. Read the sequencer.",
            kind == TestProcedureChangeKind.Retire ? "" : "The next eligible waypoint is sequenced.",
            "The approved change altered oceanic sequencing.", drivingRequirementRevisionIdsJson);

    /// <summary>Frozen, requirements materialized, one approved test change request carried, then materialized.</summary>
    private static async Task<CandidateBaseline> MaterializeAsync(AeroLinkDbContext db, Guid projectId,
        Guid releaseId, string number, Guid? predecessor, DateTimeOffset now, TestProcedureChangeDraft draft)
    {
        var scr = ApprovedChangeRequest(projectId, releaseId, $"SRCR-{Math.Abs(number.GetHashCode()) % 100000:D5}", now);
        var baseline = new CandidateBaseline(number, 0, projectId, releaseId, predecessor, number, "cm", now);
        baseline.Select(scr, "cm", now);
        baseline.Freeze("cm", now);
        baseline.MarkRequirementsMaterialized("cm", new string('b', 64), 0, now);

        var tcr = new TestChangeReview(projectId, releaseId, scr.Id, TestChangeReviewDiscipline.System,
            scr.DisplayNumber, now);
        tcr.RecordTestChangeRequired("verification.engineer", now);
        tcr.AssignControlledNumber($"SYSTCR-{Math.Abs(number.GetHashCode()) % 1000000:D6}", now);
        tcr.AddProcedureChange("verification.engineer", draft, now);
        tcr.Submit("verification.engineer", "test.lead", true, now);
        tcr.Approve("test.lead", "Procedure decisions are complete.", now);
        db.AddRange(scr, tcr, baseline);
        await db.SaveChangesAsync();

        baseline.SelectTestChangeRequest(tcr, "verification.lead", now);
        await db.SaveChangesAsync();

        await new TestProcedureBaselineMaterializer(db).MaterializeAsync(baseline.Id, "cm", now, default);
        return baseline;
    }

    private static async Task<Guid> MaterializeRequirementAsync(AeroLinkDbContext db, Guid projectId,
        Guid releaseId, DateTimeOffset now)
    {
        var scr = new SystemChangeRequest("SRCR-00001", 0, projectId, releaseId, "Oceanic", "P", "A", "S", "author", now);
        scr.AddRequirementChange("author", "SYSR-000001", 0, RequirementLevel.System,
            RequirementChangeKind.Introduce, "The FMS shall sequence oceanic waypoints.", "Needed.", "Test", now);
        scr.SubmitForReview("author", [new ApproverSelection("reviewer", "Reviewer")], now);
        scr.ApproveActiveStage("reviewer", now);
        var baseline = new CandidateBaseline("SW-00.05", 0, projectId, releaseId, null, "Requirements", "cm", now);
        baseline.Select(scr, "cm", now);
        baseline.Freeze("cm", now);
        db.AddRange(scr, baseline);
        await db.SaveChangesAsync();
        await new RequirementBaselineMaterializer(db, new VerificationImpactService(db))
            .MaterializeAsync(baseline.Id, "cm", now, default);
        return (await db.RequirementRevisions.SingleAsync()).Id;
    }

    private static SystemChangeRequest ApprovedChangeRequest(Guid projectId, Guid releaseId, string number,
        DateTimeOffset now)
    {
        var scr = new SystemChangeRequest(number, 0, projectId, releaseId, "Oceanic sequencing", "P", "A", "S", "author", now);
        scr.AddRequirementChange("author", $"SYSR-{Math.Abs(number.GetHashCode()) % 1000000:D6}", 0,
            RequirementLevel.System, RequirementChangeKind.Introduce,
            "The FMS shall sequence oceanic waypoints.", "Needed.", "Test", now);
        scr.SubmitForReview("author", [new ApproverSelection("reviewer", "Reviewer")], now);
        scr.ApproveActiveStage("reviewer", now);
        return scr;
    }
}
