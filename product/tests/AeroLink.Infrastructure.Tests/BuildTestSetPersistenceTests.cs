using AeroLink.Domain.Programs;
using AeroLink.Domain.Verification;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AeroLink.Infrastructure.Tests;

/// <summary>
/// The rules a build's test set needs the database to hold.
///
/// One set per build per discipline, and one entry per procedure revision within it. Both are constraints
/// rather than conventions because two people select procedures at the same time and neither aggregate can
/// see the other.
/// </summary>
public sealed class BuildTestSetPersistenceTests
{
    private sealed record Fixture(AeroLinkDbContext Db, Guid ProjectId, Guid ReleaseId, Guid FirstRevision, Guid SecondRevision);

    private static async Task<Fixture> DatabaseAsync()
    {
        var options = new DbContextOptionsBuilder<AeroLinkDbContext>().UseSqlite("Data Source=:memory:").Options;
        var db = new AeroLinkDbContext(options);
        await db.Database.OpenConnectionAsync();
        await db.Database.EnsureCreatedAsync();
        var now = DateTimeOffset.UtcNow;

        var program = new ProgramRecord("Test Set Program", "TSP");
        var project = new ProjectRecord(program.Id, "Flight Software", "Test Set Software");
        var release = new SoftwareRelease(project.Id, "1.6", false);
        var procedure = new TestProcedure(project.Id, "SYSTP-000700", "Oceanic sequencing", "test.engineer", now,
            TestProcedureLevel.System);
        var first = new TestProcedureRevision(procedure.Id, 0, "Objective", "Pre", "Steps", "Expected",
            TestProcedureState.Approved, "test.engineer", now);
        var second = new TestProcedureRevision(procedure.Id, 1, "Objective", "Pre", "Steps", "Expected",
            TestProcedureState.Approved, "test.engineer", now);
        db.AddRange(program, project, release, procedure, first, second);
        await db.SaveChangesAsync();
        return new(db, project.Id, release.Id, first.Id, second.Id);
    }

    [Fact]
    public async Task A_build_has_one_set_per_discipline_and_no_more()
    {
        var fixture = await DatabaseAsync();
        await using var db = fixture.Db;
        var now = DateTimeOffset.UtcNow;

        db.Add(new BuildTestSet(fixture.ProjectId, fixture.ReleaseId, TestChangeReviewDiscipline.System, now));
        db.Add(new BuildTestSet(fixture.ProjectId, fixture.ReleaseId, TestChangeReviewDiscipline.HighLevelSoftware, now));
        await db.SaveChangesAsync();

        // A second System set would mean two answers to "what is this build running", and the release gate
        // would have to guess which one it is measured against.
        db.Add(new BuildTestSet(fixture.ProjectId, fixture.ReleaseId, TestChangeReviewDiscipline.System, now));
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task Entries_added_to_a_stored_set_are_inserted_rather_than_read_as_updates()
    {
        var fixture = await DatabaseAsync();
        await using var db = fixture.Db;
        var now = DateTimeOffset.UtcNow;

        var set = new BuildTestSet(fixture.ProjectId, fixture.ReleaseId, TestChangeReviewDiscipline.System, now);
        db.Add(set);
        await db.SaveChangesAsync();

        // The failure this guards is silent and total: an entry reaching EF through the navigation with its
        // key already set is read as an existing row, and updating a row that does not exist saves nothing.
        set.Include("test.lead", fixture.FirstRevision, TestSelectionReason.ChangedRequirement, "SYSR-000151", now);
        set.Include("test.lead", fixture.SecondRevision, TestSelectionReason.CoverageArea, "Integrity", now);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var read = await db.BuildTestSets.Include(x => x.Entries).SingleAsync(x => x.Id == set.Id);
        Assert.Equal(2, read.Entries.Count);
        Assert.Equal("SYSR-000151", read.Entries.Single(x => x.ProcedureRevisionId == fixture.FirstRevision).Note);
        Assert.Equal(TestSelectionReason.CoverageArea,
            read.Entries.Single(x => x.ProcedureRevisionId == fixture.SecondRevision).Reason);
    }

    [Fact]
    public async Task Removing_an_entry_takes_it_out_of_the_stored_set()
    {
        var fixture = await DatabaseAsync();
        await using var db = fixture.Db;
        var now = DateTimeOffset.UtcNow;

        var set = new BuildTestSet(fixture.ProjectId, fixture.ReleaseId, TestChangeReviewDiscipline.System, now);
        db.Add(set);
        await db.SaveChangesAsync();
        set.Include("test.lead", fixture.FirstRevision, TestSelectionReason.Chosen, "", now);
        await db.SaveChangesAsync();

        set.Exclude(fixture.FirstRevision, now);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        Assert.Empty(await db.BuildTestSetEntries.AsNoTracking().Where(x => x.BuildTestSetId == set.Id).ToListAsync());
    }
}
