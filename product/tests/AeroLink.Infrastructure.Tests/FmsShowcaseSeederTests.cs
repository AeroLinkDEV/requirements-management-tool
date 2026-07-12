using AeroLink.Domain.ChangeControl;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AeroLink.Infrastructure.Tests;

public sealed class FmsShowcaseSeederTests
{
    [Fact]
    public async Task Generates_exact_released_15_baseline_and_active_16_work_without_duplicates()
    {
        var path = Path.Combine(Path.GetTempPath(), $"aerolink-showcase-{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<AeroLinkDbContext>().UseSqlite($"Data Source={path};Pooling=False").Options;
        try
        {
            await using var db = new AeroLinkDbContext(options); await db.Database.EnsureCreatedAsync();
            var seeder = new FmsShowcaseSeeder(db); var first = await seeder.EnsureSeededAsync(); var second = await seeder.EnsureSeededAsync();
            Assert.Equal(150, first.SystemRequirements); Assert.Equal(400, first.HighLevelRequirements); Assert.Equal(700, first.LowLevelRequirements);
            Assert.Equal(30, first.HistoricalScrs); Assert.Equal(75, first.HistoricalSwcrs); Assert.Equal(1100, first.TraceLinks);
            Assert.Equal(515, first.TestProcedures); Assert.Equal(520, first.TestExecutions); Assert.Equal(6, first.Documents); Assert.Equal(first, second);
            Assert.Equal(1250, await db.BaselineRequirements.CountAsync(x => x.BaselineId == first.ReleasedBaselineId));
            Assert.Equal(1250, await db.TestCoverage.Select(x => x.RequirementRevisionId).Distinct().CountAsync());
            Assert.True(await db.RequirementRevisions.GroupBy(x => x.ArtifactId).AllAsync(x => x.Count() >= 1));
            var active = db.SystemChangeRequests.Where(x => x.TargetReleaseId == first.ActiveReleaseId);
            Assert.Equal(8, await active.CountAsync()); Assert.Equal(2, await active.CountAsync(x => x.State == ScrState.SelectedForBaseline));
            Assert.Equal(2, await active.CountAsync(x => x.State == ScrState.InReview)); Assert.Equal(3, await active.CountAsync(x => x.State == ScrState.Draft)); Assert.Equal(1, await active.CountAsync(x => x.State == ScrState.Deferred));
        }
        finally { File.Delete(path); }
    }
}
