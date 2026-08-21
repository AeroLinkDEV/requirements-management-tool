using AeroLink.Domain.Baselines;
using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Hierarchy;
using AeroLink.Domain.Programs;
using AeroLink.Domain.Requirements;
using AeroLink.Domain.Verification;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AeroLink.Infrastructure.Tests;

/// <summary>
/// Moving a stranded procedure is only real if the coverage link moves with it.
///
/// The decision itself is a record of a judgement. What makes the requirement covered again is the link, and
/// a decision that recorded the judgement without creating it would leave the requirement reading as
/// untested while the workspace showed the item resolved.
/// </summary>
public sealed class ProcedureRetargetCoverageTests
{
    /// <summary>
    /// A real requirement to move onto, because the coverage table has a foreign key to it. A loose
    /// identifier would fail for the wrong reason and prove nothing about the decision under test.
    /// </summary>
    private sealed record Fixture(AeroLinkDbContext Db, Guid ProjectId, Guid TargetRevisionId);

    private static async Task<Fixture> DatabaseAsync()
    {
        var options = new DbContextOptionsBuilder<AeroLinkDbContext>().UseSqlite("Data Source=:memory:").Options;
        var db = new AeroLinkDbContext(options);
        await db.Database.OpenConnectionAsync();
        await db.Database.EnsureCreatedAsync();
        var now = DateTimeOffset.UtcNow;

        var program = new ProgramRecord("Retarget Program", "RTG");
        var project = new ProjectRecord(program.Id, "Flight Software", "Retarget Software");
        var release = new SoftwareRelease(project.Id, "1.0", false);
        var scr = new SystemChangeRequest("SRCR-00050", 0, project.Id, release.Id, "Move", "P", "A", "S", "author", now);
        var baseline = new CandidateBaseline("SW-50.00", 0, project.Id, release.Id, null, "Candidate", "cm", now);
        var artifact = new RequirementArtifact(project.Id, "SYSR-00000151", RequirementLevel.System, now);
        var revision = new RequirementRevision(artifact.Id, 0, "The FMS shall sequence oceanic waypoints.",
            "Moved behaviour.", "Test", RequirementRevisionState.Active, scr.Id, baseline.Id, now);
        db.AddRange(program, project, release, scr, baseline, artifact, revision,
            LegacyDefaultProjectLadderFactory.Create(project.Id, now));
        await db.SaveChangesAsync();
        return new(db, project.Id, revision.Id);
    }

    private static VerificationImpactItem Resolved(Guid projectId, Guid procedureId, Guid target)
    {
        var item = VerificationImpactItem.ForOrphanedProcedure(projectId, Guid.NewGuid(), Guid.NewGuid(),
            Guid.NewGuid(), procedureId, "SYSTP-000042", DateTimeOffset.UtcNow);
        item.Resolve("test.engineer", VerificationImpactOutcome.ProcedureRetargeted,
            "The behaviour moved; this procedure still exercises it.", DateTimeOffset.UtcNow,
            retargetedRequirementRevisionId: target);
        return item;
    }

    [Fact]
    public async Task Every_revision_of_the_moved_procedure_covers_the_new_requirement()
    {
        var fixture = await DatabaseAsync();
        await using var db = fixture.Db;
        var now = DateTimeOffset.UtcNow;
        var projectId = fixture.ProjectId;
        var target = fixture.TargetRevisionId;

        var procedure = new TestProcedure(projectId, "SYSTP-000042", "Oceanic sequencing", "test.engineer", now,
            TestProcedureLevel.System);
        var first = new TestProcedureRevision(procedure.Id, 0, "Objective", "Pre", "Steps", "Expected",
            TestProcedureState.Approved, "test.engineer", now);
        var second = new TestProcedureRevision(procedure.Id, 1, "Objective", "Pre", "Steps", "Expected",
            TestProcedureState.Approved, "test.engineer", now);
        db.AddRange(procedure, first, second);
        await db.SaveChangesAsync();

        var applied = await new VerificationImpactService(db)
            .ApplyRetargetedCoverageAsync(Resolved(projectId, procedure.Id, target), now, default);
        await db.SaveChangesAsync();

        Assert.True(applied);
        // Both revisions, so "what covers this requirement" does not depend on which revision a reader opens.
        var covered = await db.TestCoverage.AsNoTracking()
            .Where(x => x.RequirementRevisionId == target).Select(x => x.ProcedureRevisionId).ToListAsync();
        Assert.Equal([first.Id, second.Id], covered.OrderBy(x => x == second.Id).ToList());
        Assert.Equal(2, covered.Count);
    }

    [Fact]
    public async Task Applying_it_twice_confirms_the_link_rather_than_duplicating_it()
    {
        var fixture = await DatabaseAsync();
        await using var db = fixture.Db;
        var now = DateTimeOffset.UtcNow;
        var projectId = fixture.ProjectId;
        var target = fixture.TargetRevisionId;

        var procedure = new TestProcedure(projectId, "SYSTP-000042", "Oceanic sequencing", "test.engineer", now,
            TestProcedureLevel.System);
        var revision = new TestProcedureRevision(procedure.Id, 0, "Objective", "Pre", "Steps", "Expected",
            TestProcedureState.Approved, "test.engineer", now);
        db.AddRange(procedure, revision);
        await db.SaveChangesAsync();

        var service = new VerificationImpactService(db);
        await service.ApplyRetargetedCoverageAsync(Resolved(projectId, procedure.Id, target), now, default);
        await db.SaveChangesAsync();
        await service.ApplyRetargetedCoverageAsync(Resolved(projectId, procedure.Id, target), now, default);
        await db.SaveChangesAsync();

        Assert.Single(await db.TestCoverage.AsNoTracking().Where(x => x.RequirementRevisionId == target).ToListAsync());
    }

    /// <summary>A decision that is not a move must not quietly create coverage.</summary>
    [Fact]
    public async Task A_retired_procedure_creates_no_new_coverage()
    {
        var fixture = await DatabaseAsync();
        await using var db = fixture.Db;
        var now = DateTimeOffset.UtcNow;
        var item = VerificationImpactItem.ForOrphanedProcedure(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            Guid.NewGuid(), Guid.NewGuid(), "SYSTP-000042", now);
        item.Resolve("test.engineer", VerificationImpactOutcome.ProcedureRetired, "Withdrawn.", now);

        Assert.False(await new VerificationImpactService(db).ApplyRetargetedCoverageAsync(item, now, default));
        Assert.Empty(await db.TestCoverage.AsNoTracking().ToListAsync());
    }
}
