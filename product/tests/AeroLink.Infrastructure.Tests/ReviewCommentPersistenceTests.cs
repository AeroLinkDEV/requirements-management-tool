using System.Text;
using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Programs;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AeroLink.Infrastructure.Tests;

/// <summary>
/// Comments survive a round trip, and — the part that matters — never reach anything controlled. They are
/// excluded from the published DOCX and PDF by never being loaded into the generator, which is a guarantee
/// that holds only until somebody adds an Include. That is precisely why it is asserted here.
/// </summary>
public sealed class ReviewCommentPersistenceTests
{
    private const string Secret = "The reload budget is asserted rather than derived.";

    [Fact]
    public async Task A_published_comment_reaches_neither_the_document_nor_the_pdf()
    {
        var path = Path.Combine(Path.GetTempPath(), $"aerolink-comment-{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<AeroLinkDbContext>().UseSqlite($"Data Source={path};Pooling=False").Options;
        try
        {
            Guid scrId;
            await using (var setup = new AeroLinkDbContext(options))
            {
                await setup.Database.EnsureCreatedAsync();
                var (program, project, release, scr) = Seed();
                scr.AddReviewComment("reviewer", ReviewCommentAnchor.ChangeCase, null, Secret, DateTimeOffset.UtcNow);
                scr.ApproveActiveStage("reviewer", DateTimeOffset.UtcNow);
                setup.AddRange(program, project, release, scr);
                await setup.SaveChangesAsync();
                scrId = scr.Id;
            }

            await using var verify = new AeroLinkDbContext(options);
            // The comment is stored and published — so if it were going to leak, it would.
            var stored = await verify.ReviewComments.AsNoTracking().SingleAsync();
            Assert.Equal(ReviewCommentState.Published, stored.State);
            Assert.True(stored.DecisionRecorded);

            var generator = new ChangeRequestOutputGenerator(verify);
            foreach (var format in new[] { "docx", "pdf" })
            {
                var output = await generator.GenerateAsync(scrId, format, default);
                Assert.NotNull(output);
                // Searching the raw bytes rather than a parsed document: a leak through any path — body,
                // metadata, an embedded part — is still a leak, and parsing would only look where expected.
                Assert.DoesNotContain(Encoding.UTF8.GetBytes(Secret), output!.Content);
            }
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Closing_a_cycle_publishes_the_drafts_that_were_actually_loaded()
    {
        var path = Path.Combine(Path.GetTempPath(), $"aerolink-comment-{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<AeroLinkDbContext>().UseSqlite($"Data Source={path};Pooling=False").Options;
        try
        {
            Guid scrId;
            await using (var setup = new AeroLinkDbContext(options))
            {
                await setup.Database.EnsureCreatedAsync();
                var (program, project, release, scr) = Seed();
                scr.AddReviewComment("reviewer", ReviewCommentAnchor.ChangeCase, null, Secret, DateTimeOffset.UtcNow);
                setup.AddRange(program, project, release, scr);
                await setup.SaveChangesAsync();
                scrId = scr.Id;
            }

            // Reloading through the repository is the path the endpoints take. If it does not bring the
            // comments back with the cycle, the publish loop below runs over an empty collection and the
            // draft is silently lost — no error, no trace, just a comment the author never sees.
            await using (var act = new AeroLinkDbContext(options))
            {
                var scr = await new ChangeRequestRepository(act).GetAsync(scrId, default);
                scr!.RequestChanges("reviewer", "Settle the tolerance first.", DateTimeOffset.UtcNow);
                await act.SaveChangesAsync();
            }

            await using var verify = new AeroLinkDbContext(options);
            var stored = await verify.ReviewComments.AsNoTracking().SingleAsync();
            Assert.Equal(ReviewCommentState.Published, stored.State);
            Assert.True(stored.DecisionRecorded);
            Assert.Equal(Secret, stored.Body);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task A_comment_can_be_added_to_an_aggregate_that_was_loaded_from_the_database()
    {
        var path = Path.Combine(Path.GetTempPath(), $"aerolink-comment-{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<AeroLinkDbContext>().UseSqlite($"Data Source={path};Pooling=False").Options;
        try
        {
            Guid scrId;
            await using (var setup = new AeroLinkDbContext(options))
            {
                await setup.Database.EnsureCreatedAsync();
                var (program, project, release, scr) = Seed();
                setup.AddRange(program, project, release, scr);
                await setup.SaveChangesAsync();
                scrId = scr.Id;
            }

            // This is the endpoint's path exactly: load, add, save. Nothing else in the aggregate changes.
            await using (var act = new AeroLinkDbContext(options))
            {
                var scr = await new ChangeRequestRepository(act).GetAsync(scrId, default);
                var comment = scr!.AddReviewComment("reviewer", ReviewCommentAnchor.ChangeCase, null, Secret, DateTimeOffset.UtcNow);
                act.ReviewComments.Add(comment);
                await act.SaveChangesAsync();
            }

            await using var verify = new AeroLinkDbContext(options);
            Assert.Equal(Secret, (await verify.ReviewComments.AsNoTracking().SingleAsync()).Body);
            // The cycle it hangs off must still be the one that was there, not a second copy.
            Assert.Single(await verify.ReviewCycles.AsNoTracking().ToListAsync());
        }
        finally { File.Delete(path); }
    }

    private static (ProgramRecord Program, ProjectRecord Project, SoftwareRelease Release, SystemChangeRequest Scr) Seed()
    {
        var now = DateTimeOffset.UtcNow;
        var program = new ProgramRecord("Comment Program", "CMP");
        var project = new ProjectRecord(program.Id, "Software", "Comment Software");
        var release = new SoftwareRelease(project.Id, "1.0", false);
        var scr = new SystemChangeRequest("SRCR-00039", 0, project.Id, release.Id,
            "Reduce flight-plan reload latency", "Problem", "Analysis", "Solution", "author", now);
        scr.AddRequirementChange("author", "SYSR-000151", 2, RequirementLevel.System,
            RequirementChangeKind.Modify, "Available within 1.5 seconds.", "Rationale", "Test", now);
        scr.SubmitForReview("author", [new("reviewer", "Marcus Hale")], now);
        return (program, project, release, scr);
    }
}
