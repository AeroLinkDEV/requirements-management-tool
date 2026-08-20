using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Identity;
using AeroLink.Domain.Programs;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace AeroLink.Infrastructure.Tests;

public sealed class ReviewPersistenceTests
{
    [Fact]
    public void Active_workflow_uniqueness_migration_is_discoverable()
    {
        var migration = typeof(AeroLinkDbContext).Assembly.GetType(
            "AeroLink.Infrastructure.Persistence.Migrations.EnforceOneActiveReviewWorkflow");

        Assert.NotNull(migration);
        Assert.Equal("20260820142622_EnforceOneActiveReviewWorkflow",
            migration!.GetCustomAttributes(typeof(MigrationAttribute), inherit: false)
                .Cast<MigrationAttribute>().Single().Id);
        Assert.NotEmpty(migration.GetCustomAttributes(typeof(DbContextAttribute), inherit: false));
    }

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
                var scr = new SystemChangeRequest("HLRCR-00001", 0, project.Id, release.Id, "Review ordering", "P", "A", "S", "author", DateTimeOffset.UtcNow, ChangeRequestType.Software, softwareLevel: RequirementLevel.HighLevel);
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

    [Fact]
    public async Task Sqlite_allows_history_but_rejects_two_active_workflows_for_one_subject()
    {
        var path = Path.Combine(Path.GetTempPath(), $"aerolink-workflow-index-{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<AeroLinkDbContext>().UseSqlite($"Data Source={path};Pooling=False").Options;
        try
        {
            await using var db = new AeroLinkDbContext(options);
            await db.Database.EnsureCreatedAsync();
            var projectId = Guid.NewGuid();
            var first = new ReviewWorkflow(projectId, "First", ReviewSubject.System, ReviewMode.Sequential,
                [new("Review", ProgramRole.Reviewer)], "test", DateTimeOffset.UtcNow);
            first.Activate("test", DateTimeOffset.UtcNow);
            db.ReviewWorkflows.Add(first);
            await db.SaveChangesAsync();

            var retired = first.Revise("Retired", ReviewMode.Sequential,
                [new("Review", ProgramRole.Reviewer)], "test", DateTimeOffset.UtcNow.AddMinutes(1));
            // The aggregate revision is Draft by design; retiring it is not required for this assertion. The
            // unique index must permit historical non-active rows while refusing a second active row.
            db.ReviewWorkflows.Add(retired);
            await db.SaveChangesAsync();

            var competing = new ReviewWorkflow(projectId, "Competing", ReviewSubject.System, ReviewMode.Sequential,
                [new("Review", ProgramRole.Reviewer)], "race", DateTimeOffset.UtcNow.AddMinutes(2));
            competing.Activate("race", DateTimeOffset.UtcNow.AddMinutes(2));
            db.ReviewWorkflows.Add(competing);

            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
            Assert.Equal(1, await db.ReviewWorkflows.CountAsync(x => x.ProjectId == projectId && x.State == ReviewWorkflowState.Active));
        }
        finally { File.Delete(path); }
    }

    private static Task<SystemChangeRequest> Load(AeroLinkDbContext db, Guid id) => db.SystemChangeRequests
        .Include(x => x.RequirementChanges).Include(x => x.AuditEvents)
        .Include(x => x.ReviewCycles).ThenInclude(x => x.Steps).SingleAsync(x => x.Id == id);
}
