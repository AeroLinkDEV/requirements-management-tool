using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Programs;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AeroLink.Infrastructure.Tests;

public sealed class ConcurrencyTests
{
    [Fact]
    public async Task Concurrent_scr_updates_cannot_silently_overwrite_each_other()
    {
        var path = Path.Combine(Path.GetTempPath(), $"aerolink-{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<AeroLinkDbContext>().UseSqlite($"Data Source={path};Pooling=False").Options;
        Guid scrId;
        try
        {
            await using (var setup = new AeroLinkDbContext(options))
            {
                await setup.Database.EnsureCreatedAsync();
                var program = new ProgramRecord("Concurrency Program", "CON");
                var project = new ProjectRecord(program.Id, "Software", "Concurrent Software");
                var release = new SoftwareRelease(project.Id, "1.0", false);
                var scr = new SystemChangeRequest("SCR-00001", 0, project.Id, release.Id, "Concurrent change",
                    "Problem", "Analysis", "Solution", "author", DateTimeOffset.UtcNow);
                setup.AddRange(program, project, release, scr);
                await setup.SaveChangesAsync();
                scrId = scr.Id;
            }
            await using (var first = new AeroLinkDbContext(options))
            await using (var second = new AeroLinkDbContext(options))
            {
                var firstCopy = await first.SystemChangeRequests.Include(x => x.RequirementChanges).Include(x => x.AuditEvents).SingleAsync(x => x.Id == scrId);
                var secondCopy = await second.SystemChangeRequests.Include(x => x.RequirementChanges).Include(x => x.AuditEvents).SingleAsync(x => x.Id == scrId);
                Assert.Equal(1, firstCopy.Version);
                Assert.Equal(1, secondCopy.Version);
                firstCopy.AddRequirementChange("author", "SWR-00000001", 0, RequirementLevel.HighLevel, RequirementChangeKind.Introduce, "First update", "Test", "Test", DateTimeOffset.UtcNow);
                secondCopy.AddRequirementChange("author", "SWR-00000002", 0, RequirementLevel.HighLevel, RequirementChangeKind.Introduce, "Second update", "Test", "Test", DateTimeOffset.UtcNow);
                await first.SaveChangesAsync();
                await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => second.SaveChangesAsync());
            }
        }
        finally { File.Delete(path); }
    }
}
