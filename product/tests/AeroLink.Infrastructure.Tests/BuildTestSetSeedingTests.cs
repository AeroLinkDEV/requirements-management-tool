using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Programs;
using AeroLink.Domain.Verification;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AeroLink.Infrastructure.Tests;

/// <summary>
/// A build's test sets are created on first use, and carry forward what the product already knew.
///
/// "Must be run before release" used to be a checkbox on individual verification decisions. Every procedure
/// that checkbox pointed at is exactly a procedure the build has to run, so replacing the checkbox without
/// carrying those forward would silently discard every decision anybody had already recorded with it.
/// </summary>
public sealed class BuildTestSetSeedingTests
{
    private sealed record Fixture(AeroLinkDbContext Db, Guid ProjectId, Guid ReleaseId, Guid ProcedureRevisionId);

    private static async Task<Fixture> DatabaseAsync(bool flagPreReleaseEvidence)
    {
        var options = new DbContextOptionsBuilder<AeroLinkDbContext>().UseSqlite("Data Source=:memory:").Options;
        var db = new AeroLinkDbContext(options);
        await db.Database.OpenConnectionAsync();
        await db.Database.EnsureCreatedAsync();
        var now = DateTimeOffset.UtcNow;

        var program = new ProgramRecord("Seed Program", "SED");
        var project = new ProjectRecord(program.Id, "Flight Software", "Seed Software");
        var release = new SoftwareRelease(project.Id, "1.6", false);
        var scr = new SystemChangeRequest("SCR-00800", 0, project.Id, release.Id, "Oceanic", "P", "A", "S", "author", now);
        var procedure = new TestProcedure(project.Id, "SYSTP-000800", "Oceanic sequencing", "test.engineer", now,
            TestProcedureLevel.System);
        var revision = new TestProcedureRevision(procedure.Id, 0, "Objective", "Pre", "Steps", "Expected",
            TestProcedureState.Approved, "test.engineer", now);
        var review = new TestChangeReview(project.Id, release.Id, scr.Id, TestChangeReviewDiscipline.System,
            "SCR-00800", now, "SYSTCR-000800");
        db.AddRange(program, project, release, scr, procedure, revision, review);
        await db.SaveChangesAsync();

        var item = VerificationImpactItem.ForIntroducedRequirement(project.Id, release.Id, scr.Id, review.Id,
            Guid.NewGuid(), "SYSR-000800", "Test", now);
        item.Resolve("verification.engineer", VerificationImpactOutcome.ProcedureCoverageConfirmed,
            "Covered by the oceanic sequencing procedure.", now, procedure.Id, revision.Id,
            TestProcedureChangeAction.CreateNew, flagPreReleaseEvidence);
        db.Add(item);
        await db.SaveChangesAsync();
        return new(db, project.Id, release.Id, revision.Id);
    }

    [Fact]
    public async Task A_build_gets_one_set_for_each_discipline()
    {
        var fixture = await DatabaseAsync(flagPreReleaseEvidence: false);
        await using var db = fixture.Db;

        var sets = await new BuildTestSetService(db).EnsureForReleaseAsync(fixture.ProjectId, fixture.ReleaseId);

        Assert.Equal(3, sets.Count);
        Assert.Equal(
            [TestChangeReviewDiscipline.System, TestChangeReviewDiscipline.HighLevelSoftware, TestChangeReviewDiscipline.LowLevelSoftware],
            sets.Select(x => x.Discipline).OrderBy(x => x).ToList());
    }

    [Fact]
    public async Task Procedures_that_needed_evidence_before_release_become_the_first_entries()
    {
        var fixture = await DatabaseAsync(flagPreReleaseEvidence: true);
        await using var db = fixture.Db;

        var sets = await new BuildTestSetService(db).EnsureForReleaseAsync(fixture.ProjectId, fixture.ReleaseId);

        var system = sets.Single(x => x.Discipline == TestChangeReviewDiscipline.System);
        var entry = system.Entries.Single();
        Assert.Equal(fixture.ProcedureRevisionId, entry.ProcedureRevisionId);
        Assert.Equal(TestSelectionReason.ChangedRequirement, entry.Reason);
        // Says where it came from, so somebody reading the set later can tell a carried-forward decision from
        // one somebody made in the new surface.
        Assert.Contains("SYSR-000800", entry.Note);
        // It lands in the discipline of the test change request that raised it, not in all three.
        Assert.All(sets.Where(x => x.Discipline != TestChangeReviewDiscipline.System), x => Assert.Empty(x.Entries));
    }

    [Fact]
    public async Task A_decision_that_never_asked_for_evidence_carries_nothing_forward()
    {
        var fixture = await DatabaseAsync(flagPreReleaseEvidence: false);
        await using var db = fixture.Db;

        var sets = await new BuildTestSetService(db).EnsureForReleaseAsync(fixture.ProjectId, fixture.ReleaseId);

        // The checkbox meant "this one specifically". Carrying every confirmed procedure forward would turn a
        // deliberately narrow list into the whole suite and change what the release is measured against.
        Assert.All(sets, x => Assert.Empty(x.Entries));
    }

    [Fact]
    public async Task Asking_twice_neither_duplicates_the_sets_nor_reseeds_them()
    {
        var fixture = await DatabaseAsync(flagPreReleaseEvidence: true);
        await using var db = fixture.Db;
        var service = new BuildTestSetService(db);

        await service.EnsureForReleaseAsync(fixture.ProjectId, fixture.ReleaseId);
        var system = (await service.EnsureForReleaseAsync(fixture.ProjectId, fixture.ReleaseId))
            .Single(x => x.Discipline == TestChangeReviewDiscipline.System);

        Assert.Equal(3, await db.BuildTestSets.CountAsync());
        // A second pass must not re-add what a lead may deliberately have removed.
        Assert.Single(system.Entries);
    }

    [Fact]
    public async Task A_procedure_a_lead_removed_stays_removed()
    {
        var fixture = await DatabaseAsync(flagPreReleaseEvidence: true);
        await using var db = fixture.Db;
        var service = new BuildTestSetService(db);
        var sets = await service.EnsureForReleaseAsync(fixture.ProjectId, fixture.ReleaseId);

        var system = sets.Single(x => x.Discipline == TestChangeReviewDiscipline.System);
        system.Exclude(fixture.ProcedureRevisionId, DateTimeOffset.UtcNow);
        await db.SaveChangesAsync();

        var again = await service.EnsureForReleaseAsync(fixture.ProjectId, fixture.ReleaseId);
        Assert.Empty(again.Single(x => x.Discipline == TestChangeReviewDiscipline.System).Entries);
    }
}
