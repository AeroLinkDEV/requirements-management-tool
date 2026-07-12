using AeroLink.Domain.Baselines;
using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Programs;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AeroLink.Infrastructure.Tests;

public sealed class BuildProvenanceTests
{
    [Fact]
    public async Task Build_retains_exact_frozen_baseline_and_selected_scr_history()
    {
        var path = Path.Combine(Path.GetTempPath(), $"aerolink-build-{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<AeroLinkDbContext>().UseSqlite($"Data Source={path};Pooling=False").Options;
        try
        {
            Guid buildId;
            await using (var db = new AeroLinkDbContext(options))
            {
                await db.Database.EnsureCreatedAsync(); var now = DateTimeOffset.UtcNow;
                var program = new ProgramRecord("Flight Management", "FMS");
                var project = new ProjectRecord(program.Id, "FMS Software", "Flight Management Software");
                var release = new SoftwareRelease(project.Id, "3.3", false);
                var scr = new SystemChangeRequest("SCR-00000042", 1, project.Id, release.Id, "Round robin", "P", "A", "S", "author", now);
                scr.AddRequirementChange("author", "SWR-00002375", 4, RequirementLevel.HighLevel, RequirementChangeKind.Introduce, "Provide round robin routing.", "New function", "Test", now);
                scr.SubmitForReview("author", [new("reviewer", "Reviewer")], now); scr.ApproveActiveStage("reviewer", now);
                var baseline = new CandidateBaseline("SWBL-00000033", 0, project.Id, release.Id, null, "FMS 3.3", "cm", now);
                baseline.Select(scr, "cm", now); baseline.Freeze("cm", now);
                var build = new SoftwareBuild(project.Id, release.Id, baseline.Id, "FMS-3.3.0-001", "Integration build", "cm", now);
                db.AddRange(program, project, release, scr, baseline, build); await db.SaveChangesAsync(); buildId = build.Id;
            }
            await using (var verify = new AeroLinkDbContext(options))
            {
                var build = await verify.SoftwareBuilds.SingleAsync(x => x.Id == buildId);
                var baseline = await verify.CandidateBaselines.Include(x => x.Selections).SingleAsync(x => x.Id == build.BaselineId);
                var scr = await verify.SystemChangeRequests.Include(x => x.RequirementChanges).SingleAsync(x => x.Id == baseline.Selections.Single().ScrId);
                Assert.Equal("FMS-3.3.0-001", build.BuildNumber); Assert.Equal(64, baseline.ContentHash!.Length);
                Assert.Equal("SCR-00000042.01", scr.DisplayNumber); Assert.Equal("SWR-00002375.04", scr.RequirementChanges.Single().DisplayNumber);
            }
        }
        finally { File.Delete(path); }
    }
}
