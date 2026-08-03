using AeroLink.Domain.Programs;
using AeroLink.Domain.Verification;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AeroLink.Infrastructure.Tests;

/// <summary>
/// Which determinations belong to the build being decided about.
///
/// The rule these tests hold is that a released build's evidence can never stand in for work in progress.
/// It used to: the scope predicate relaxed to "any execution at all" whenever the active build had not yet
/// recorded an immutable software build, so a Build 1.5 determination satisfied Build 1.6's gates and showed
/// as Build 1.6's latest result. Each test therefore plants a historical run that is *newer* than the active
/// one, because the failure mode was ordering by time across releases rather than scoping to one first.
/// </summary>
public sealed class ExecutionScopeTests
{
    private sealed record Fixture(
        AeroLinkDbContext Db, Guid ProjectId, Guid ReleasedId, Guid InWorkId, Guid ReleasedBuildId, Guid RevisionId);

    private static async Task<Fixture> DatabaseAsync()
    {
        var options = new DbContextOptionsBuilder<AeroLinkDbContext>().UseSqlite("Data Source=:memory:").Options;
        var db = new AeroLinkDbContext(options);
        await db.Database.OpenConnectionAsync();
        await db.Database.EnsureCreatedAsync();
        var now = DateTimeOffset.UtcNow;

        var program = new ProgramRecord("Execution Scope Program", "ESP");
        var project = new ProjectRecord(program.Id, "Flight Software", "Execution Scope Software");
        var released = new SoftwareRelease(project.Id, "1.5", true);
        var inWork = new SoftwareRelease(project.Id, "1.6", false, released.Id);
        var baseline = new AeroLink.Domain.Baselines.CandidateBaseline("SW-01.50", 0, project.Id, released.Id, null,
            "Released software build", "cm.test", now);
        var releasedBuild = new SoftwareBuild(project.Id, released.Id, baseline.Id, "SW-01.50",
            "Released configuration", "cm.test", now);
        var procedure = new TestProcedure(project.Id, "SYSTP-000800", "Oceanic sequencing", "test.engineer", now,
            TestProcedureLevel.System);
        var revision = new TestProcedureRevision(procedure.Id, 0, "Objective", "Pre", "Steps", "Expected",
            TestProcedureState.Approved, "test.engineer", now);
        db.AddRange(program, project, released, inWork, baseline, releasedBuild, procedure, revision);
        await db.SaveChangesAsync();
        return new(db, project.Id, released.Id, inWork.Id, releasedBuild.Id, revision.Id);
    }

    private static TestExecution Run(Fixture fixture, Guid? buildId, Guid? releaseId, DateTimeOffset at,
        TestOutcome outcome, string determination) =>
        new(fixture.ProjectId, fixture.RevisionId, buildId, null, outcome, "test.engineer",
            "rig", determination, "evidence/run.json", at, at, releaseId);

    [Fact]
    public async Task A_released_builds_result_never_counts_for_a_build_that_has_no_software_build_yet()
    {
        var fixture = await DatabaseAsync();
        await using var db = fixture.Db;
        var now = DateTimeOffset.UtcNow;

        // The historical run is deliberately the newer of the two. Ordering alone would pick it.
        db.Add(Run(fixture, fixture.ReleasedBuildId, fixture.ReleasedId, now, TestOutcome.Pass,
            "Historical Build 1.5 evidence."));
        db.Add(Run(fixture, null, fixture.InWorkId, now.AddDays(-2), TestOutcome.Fail,
            "Active Build 1.6 determination."));
        await db.SaveChangesAsync();

        var latest = await ExecutionScope.LatestByProcedureAsync(
            db, [fixture.RevisionId], fixture.InWorkId, null, default);

        var run = Assert.Single(latest).Value;
        Assert.Equal("Active Build 1.6 determination.", run.Determination);
        Assert.Equal(TestOutcome.Fail, run.Outcome);
        Assert.Equal(fixture.InWorkId, run.ReleaseId);
    }

    [Fact]
    public async Task A_build_with_no_software_build_still_counts_its_own_release_scoped_result()
    {
        var fixture = await DatabaseAsync();
        await using var db = fixture.Db;
        var now = DateTimeOffset.UtcNow;

        db.Add(Run(fixture, null, fixture.InWorkId, now, TestOutcome.Pass, "Active Build 1.6 determination."));
        await db.SaveChangesAsync();

        var latest = await ExecutionScope.LatestByProcedureAsync(
            db, [fixture.RevisionId], fixture.InWorkId, null, default);

        Assert.Equal("Active Build 1.6 determination.", Assert.Single(latest).Value.Determination);
    }

    [Fact]
    public async Task Once_a_software_build_is_recorded_only_that_exact_configuration_counts()
    {
        var fixture = await DatabaseAsync();
        await using var db = fixture.Db;
        var now = DateTimeOffset.UtcNow;

        // Both belong to the release, but only one names the configuration the release is being decided about.
        db.Add(Run(fixture, null, fixture.ReleasedId, now, TestOutcome.Fail, "Unattributed release-scoped run."));
        db.Add(Run(fixture, fixture.ReleasedBuildId, fixture.ReleasedId, now.AddDays(-2), TestOutcome.Pass,
            "Exact software-build evidence."));
        await db.SaveChangesAsync();

        var latest = await ExecutionScope.LatestByProcedureAsync(
            db, [fixture.RevisionId], fixture.ReleasedId, fixture.ReleasedBuildId, default);

        var run = Assert.Single(latest).Value;
        Assert.Equal("Exact software-build evidence.", run.Determination);
        Assert.Equal(fixture.ReleasedBuildId, run.SoftwareBuildId);
    }

    [Fact]
    public async Task The_predicate_and_the_query_agree_on_every_case()
    {
        var fixture = await DatabaseAsync();
        await using var db = fixture.Db;
        var now = DateTimeOffset.UtcNow;

        var historical = Run(fixture, fixture.ReleasedBuildId, fixture.ReleasedId, now, TestOutcome.Pass, "Historical.");
        var active = Run(fixture, null, fixture.InWorkId, now, TestOutcome.Pass, "Active.");
        var foreign = Run(fixture, null, fixture.ReleasedId, now, TestOutcome.Pass, "Other release, no build.");
        db.AddRange(historical, active, foreign);
        await db.SaveChangesAsync();

        Assert.False(ExecutionScope.Belongs(historical, fixture.InWorkId, null));
        Assert.True(ExecutionScope.Belongs(active, fixture.InWorkId, null));
        Assert.False(ExecutionScope.Belongs(foreign, fixture.InWorkId, null));
        Assert.True(ExecutionScope.Belongs(historical, fixture.ReleasedId, fixture.ReleasedBuildId));
        Assert.False(ExecutionScope.Belongs(active, fixture.ReleasedId, fixture.ReleasedBuildId));
    }
}
