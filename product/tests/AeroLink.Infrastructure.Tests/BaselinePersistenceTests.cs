using AeroLink.Domain.Baselines;
using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Programs;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AeroLink.Infrastructure.Tests;

public sealed class BaselinePersistenceTests
{
    [Fact]
    public async Task Frozen_manifest_retains_exact_selection_hash_and_events()
    {
        var path = Path.Combine(Path.GetTempPath(), $"aerolink-baseline-{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<AeroLinkDbContext>().UseSqlite($"Data Source={path};Pooling=False").Options;
        try
        {
            Guid baselineId;
            await using (var setup = new AeroLinkDbContext(options))
            {
                await setup.Database.EnsureCreatedAsync();
                var program = new ProgramRecord("Baseline Program", "BLP");
                var project = new ProjectRecord(program.Id, "Software", "Baseline Software");
                var release = new SoftwareRelease(project.Id, "1.0", false);
                var scr = new SystemChangeRequest("HLRCR-00001", 0, project.Id, release.Id, "Approved input", "P", "A", "S", "author", DateTimeOffset.UtcNow, ChangeRequestType.Software, softwareLevel: RequirementLevel.HighLevel);
                scr.AddRequirementChange("author", "SWR-00000001", 0, RequirementLevel.HighLevel, RequirementChangeKind.Introduce, "Statement", "Rationale", "Test", DateTimeOffset.UtcNow);
                scr.SubmitForReview("author", [new("reviewer", "Reviewer")], DateTimeOffset.UtcNow);
                scr.ApproveActiveStage("reviewer", DateTimeOffset.UtcNow);
                var baseline = new CandidateBaseline("SW-00.10", 0, project.Id, release.Id, null, "Candidate", "cm", DateTimeOffset.UtcNow);
                baseline.Select(scr, "cm", DateTimeOffset.UtcNow);
                setup.AddRange(program, project, release, scr, baseline); await setup.SaveChangesAsync(); baselineId = baseline.Id;
            }
            await using (var freeze = new AeroLinkDbContext(options))
            {
                var baseline = await freeze.CandidateBaselines.Include(x => x.Selections).Include(x => x.Events).SingleAsync(x => x.Id == baselineId);
                baseline.Freeze("cm", DateTimeOffset.UtcNow); await freeze.SaveChangesAsync();
            }
            await using (var verify = new AeroLinkDbContext(options))
            {
                var baseline = await verify.CandidateBaselines.Include(x => x.Selections).Include(x => x.Events).SingleAsync(x => x.Id == baselineId);
                Assert.Equal(CandidateBaselineState.Frozen, baseline.State);
                Assert.Equal(64, baseline.ContentHash!.Length);
                Assert.Single(baseline.Selections);
                Assert.Equal(3, baseline.Events.Count);
            }
        }
        finally { File.Delete(path); }
    }
}
