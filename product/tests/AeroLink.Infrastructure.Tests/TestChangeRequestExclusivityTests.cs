using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Programs;
using AeroLink.Domain.Verification;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AeroLink.Infrastructure.Tests;

/// <summary>
/// A change request belongs to at most one test change request, and the database is what says so.
///
/// The aggregate refuses to fold the same change in twice, but two engineers working on two packages hold
/// two aggregates and neither can see the other. Only a unique index decides which of them wins, and it has
/// to: "is this change's test work covered?" cannot have two answers, or two packages could be approved with
/// contradictory procedure decisions for the same requirement and nothing would notice.
/// </summary>
public sealed class TestChangeRequestExclusivityTests
{
    /// <summary>
    /// A real Project, build and change requests, because the rule under test is a database constraint and
    /// the table it lives on has foreign keys to all three. Loose identifiers would fail for the wrong reason.
    ///
    /// Two originating change requests, because a package is unique per change request and discipline, so two
    /// packages cannot be raised from the same change. The third is the one they compete for.
    /// </summary>
    private sealed record Fixture(AeroLinkDbContext Db, Guid ProjectId, Guid ReleaseId,
        Guid FirstOrigin, Guid SecondOrigin, Guid Contested);

    private static async Task<Fixture> DatabaseAsync()
    {
        var options = new DbContextOptionsBuilder<AeroLinkDbContext>().UseSqlite("Data Source=:memory:").Options;
        var db = new AeroLinkDbContext(options);
        await db.Database.OpenConnectionAsync();
        await db.Database.EnsureCreatedAsync();
        var now = DateTimeOffset.UtcNow;

        var program = new ProgramRecord("Claim Program", "CLM");
        var project = new ProjectRecord(program.Id, "Flight Software", "Claim Software");
        var release = new SoftwareRelease(project.Id, "1.0", false);
        SystemChangeRequest Change(string number, string title) =>
            new(number, 0, project.Id, release.Id, title, "Problem", "Analysis", "Solution", "change.author", now);
        var first = Change("SCR-00031", "First origin");
        var second = Change("SCR-00032", "Second origin");
        var contested = Change("SCR-00040", "Contested");
        db.AddRange(program, project, release, first, second, contested);
        await db.SaveChangesAsync();
        return new(db, project.Id, release.Id, first.Id, second.Id, contested.Id);
    }

    private static TestChangeReview Package(Fixture fixture, Guid raisedFrom, string number) =>
        new(fixture.ProjectId, fixture.ReleaseId, raisedFrom, TestChangeReviewDiscipline.System,
            "SCR-00031", DateTimeOffset.UtcNow, number);

    [Fact]
    public async Task Two_packages_cannot_both_claim_the_same_change_request()
    {
        var fixture = await DatabaseAsync();
        await using var db = fixture.Db;

        var first = Package(fixture, fixture.FirstOrigin, "SYSTCR-000001");
        var second = Package(fixture, fixture.SecondOrigin, "SYSTCR-000002");
        db.AddRange(first, second);
        await db.SaveChangesAsync();

        first.IncludeChangeRequest("first.engineer", fixture.Contested, "SCR-00040", DateTimeOffset.UtcNow);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        second = await db.TestChangeReviews.Include(x => x.AdditionalSources).SingleAsync(x => x.Id == second.Id);

        // The second engineer's aggregate has no way of knowing. The index does.
        second.IncludeChangeRequest("second.engineer", fixture.Contested, "SCR-00040", DateTimeOffset.UtcNow);
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task Releasing_a_claim_lets_another_package_take_it()
    {
        var fixture = await DatabaseAsync();
        await using var db = fixture.Db;

        var first = Package(fixture, fixture.FirstOrigin, "SYSTCR-000001");
        var second = Package(fixture, fixture.SecondOrigin, "SYSTCR-000002");
        db.AddRange(first, second);
        await db.SaveChangesAsync();

        first.IncludeChangeRequest("first.engineer", fixture.Contested, "SCR-00040", DateTimeOffset.UtcNow);
        await db.SaveChangesAsync();
        first.ExcludeChangeRequest(fixture.Contested, DateTimeOffset.UtcNow);
        await db.SaveChangesAsync();

        // Exclusivity would be a trap rather than a rule if a change could never be handed on.
        second.IncludeChangeRequest("second.engineer", fixture.Contested, "SCR-00040", DateTimeOffset.UtcNow);
        await db.SaveChangesAsync();

        var claims = await db.TestChangeRequestClaims.AsNoTracking()
            .Where(x => x.ChangeRequestId == fixture.Contested).ToListAsync();
        Assert.Equal(second.Id, claims.Single().TestChangeReviewId);
    }

    [Fact]
    public async Task A_package_and_its_claims_survive_a_round_trip()
    {
        var fixture = await DatabaseAsync();
        await using var db = fixture.Db;

        var package = Package(fixture, fixture.FirstOrigin, "HLRTCR-000007");
        db.Add(package);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        package = await db.TestChangeReviews.Include(x => x.AdditionalSources).SingleAsync(x => x.Id == package.Id);
        package.IncludeChangeRequest("test.engineer", fixture.Contested, "SCR-00040", DateTimeOffset.UtcNow);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var read = await db.TestChangeReviews.Include(x => x.AdditionalSources).SingleAsync(x => x.Id == package.Id);
        Assert.Equal("HLRTCR-000007", read.BaseNumber);
        Assert.Equal("HLRTCR-000007.00", read.DisplayNumber);
        Assert.Equal("SCR-00040", read.AdditionalSources.Single().ChangeRequestNumber);
        Assert.Equal(2, read.CoveredChangeRequestIds.Count());
    }
}
