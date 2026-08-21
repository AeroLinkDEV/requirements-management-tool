using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Hierarchy;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AeroLink.Infrastructure.Tests;

public sealed class ImportPracticeSeederTests
{
    [Fact]
    public async Task Import_practice_creation_bootstraps_the_exact_legacy_default_ladder()
    {
        var path = Path.Combine(Path.GetTempPath(), $"aerolink-import-practice-{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<AeroLinkDbContext>()
            .UseSqlite($"Data Source={path};Pooling=False")
            .Options;
        try
        {
            await using var db = new AeroLinkDbContext(options);
            await db.Database.EnsureCreatedAsync();
            var projectId = await new ImportPracticeSeeder(db).EnsureSeededAsync();

            var ladder = await db.ProjectLadderConfigurations
                .Include(x => x.Steps).Include(x => x.AllowedUpstream)
                .SingleAsync(x => x.ProjectId == projectId);
            var resolved = ProjectLadderResolver.Resolve(ladder);

            Assert.True(resolved.AgreesWithLegacyDefault());
            Assert.Equal(ProjectLadderConfigurationClassification.LegacyDefault, ladder.Classification);
            Assert.Equal(ProjectLadderConfigurationState.Stored, ladder.State);
            Assert.Equal([RequirementLevel.System, RequirementLevel.HighLevel, RequirementLevel.LowLevel],
                resolved.Steps.Select(x => x.Level));
            Assert.Equal([7, 7, 15], resolved.Steps.Select(x => (int)x.Capabilities));
            Assert.Equal(2, ladder.AllowedUpstream.Count);
            Assert.DoesNotContain(await db.ProjectLadderConfigurations.AsNoTracking()
                .Where(x => x.ProjectId == projectId)
                .Select(x => new { x.Classification, x.State }).ToListAsync(),
                x => x.Classification != ProjectLadderConfigurationClassification.LegacyDefault
                    || x.State != ProjectLadderConfigurationState.Stored);
        }
        finally
        {
            try { if (File.Exists(path)) File.Delete(path); } catch (IOException) { }
        }
    }
}
