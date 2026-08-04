using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Programs;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AeroLink.Infrastructure.Tests;

public sealed class ArtifactScopingTests
{
    [Fact]
    public async Task Separate_projects_can_start_with_the_same_artifact_number()
    {
        var path = Path.Combine(Path.GetTempPath(), $"aerolink-scope-{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<AeroLinkDbContext>().UseSqlite($"Data Source={path};Pooling=False").Options;
        try
        {
            await using var db = new AeroLinkDbContext(options); await db.Database.EnsureCreatedAsync();
            var p1 = new ProgramRecord("Program One", "ONE"); var p2 = new ProgramRecord("Program Two", "TWO");
            var project1 = new ProjectRecord(p1.Id, "Software", "Product One"); var project2 = new ProjectRecord(p2.Id, "Software", "Product Two");
            var release1 = new SoftwareRelease(project1.Id, "1.0", false); var release2 = new SoftwareRelease(project2.Id, "1.0", false);
            db.AddRange(p1, p2, project1, project2, release1, release2,
                new SystemChangeRequest("SRCR-00001", 0, project1.Id, release1.Id, "One", "P", "A", "S", "a", DateTimeOffset.UtcNow),
                new SystemChangeRequest("SRCR-00001", 0, project2.Id, release2.Id, "Two", "P", "A", "S", "a", DateTimeOffset.UtcNow));
            await db.SaveChangesAsync();
            Assert.Equal(2, await db.SystemChangeRequests.CountAsync());
        }
        finally { File.Delete(path); }
    }
}
