using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Programs;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AeroLink.Infrastructure.Tests;

public sealed class ReviewPersistenceTests
{
    [Fact]
    public async Task Reloaded_review_advances_by_explicit_position_not_database_return_order()
    {
        var path = Path.Combine(Path.GetTempPath(), $"aerolink-review-{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<AeroLinkDbContext>().UseSqlite($"Data Source={path};Pooling=False").Options;
        try
        {
            Guid id;
            await using (var setup = new AeroLinkDbContext(options))
            {
                await setup.Database.EnsureCreatedAsync();
                var program = new ProgramRecord("Review Program", "REV");
                var project = new ProjectRecord(program.Id, "Software", "Review Software");
                var release = new SoftwareRelease(project.Id, "1.0", false);
                var scr = new SystemChangeRequest("SCR-00000001", 0, project.Id, release.Id, "Review ordering", "P", "A", "S", "author", DateTimeOffset.UtcNow);
                scr.AddRequirementChange("author", "SWR-00000001", 0, RequirementLevel.HighLevel, RequirementChangeKind.Introduce, "Statement", "Rationale", "Test", DateTimeOffset.UtcNow);
                scr.SubmitForReview("author", [new("r1", "One"), new("r2", "Two"), new("r3", "Three")], DateTimeOffset.UtcNow);
                setup.AddRange(program, project, release, scr);
                await setup.SaveChangesAsync(); id = scr.Id;
            }
            await using (var first = new AeroLinkDbContext(options))
            {
                var scr = await Load(first, id); scr.ApproveActiveStage("r1", DateTimeOffset.UtcNow); await first.SaveChangesAsync();
            }
            await using (var verify = new AeroLinkDbContext(options))
            {
                var scr = await Load(verify, id);
                Assert.Equal(1, scr.ActiveReviewCycle!.ActivePosition);
                Assert.Equal("r2", scr.ActiveReviewCycle.Steps.Single(x => x.State == ApprovalStepState.Active).ApproverId);
            }
        }
        finally { File.Delete(path); }
    }

    private static Task<SystemChangeRequest> Load(AeroLinkDbContext db, Guid id) => db.SystemChangeRequests
        .Include(x => x.RequirementChanges).Include(x => x.AuditEvents)
        .Include(x => x.ReviewCycles).ThenInclude(x => x.Steps).SingleAsync(x => x.Id == id);
}
