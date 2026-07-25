using AeroLink.Domain.Baselines;
using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Programs;
using AeroLink.Domain.Requirements;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AeroLink.Infrastructure.Tests;

public sealed class RequirementMaterializationTests
{
    [Fact]
    public async Task Introduce_modify_and_retire_preserve_revision_history_and_exact_membership()
    {
        var path = Path.Combine(Path.GetTempPath(), $"aerolink-req-{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<AeroLinkDbContext>().UseSqlite($"Data Source={path};Pooling=False").Options;
        try
        {
            await using var db = new AeroLinkDbContext(options); await db.Database.EnsureCreatedAsync(); var now = DateTimeOffset.UtcNow;
            var program = new ProgramRecord("FMS", "FMSR"); var project = new ProjectRecord(program.Id, "Software", "FMS Software"); var release = new SoftwareRelease(project.Id, "3.3", false);
            db.AddRange(program, project, release); await db.SaveChangesAsync();

            var firstScr = ApprovedScr("SCR-00000001", "SWR-00002375", 0, RequirementChangeKind.Introduce, "Initial round robin requirement", project.Id, release.Id, now);
            var first = FrozenBaseline("SWBL-00000001", project.Id, release.Id, null, firstScr, now); db.AddRange(firstScr, first); await db.SaveChangesAsync();
            await new RequirementBaselineMaterializer(db, new VerificationImpactService(db)).MaterializeAsync(first.Id, "cm", now, default);

            var modifyScr = ApprovedScr("SCR-00000002", "SWR-00002375", 1, RequirementChangeKind.Modify, "Clarified round robin requirement", project.Id, release.Id, now);
            var second = FrozenBaseline("SWBL-00000002", project.Id, release.Id, first.Id, modifyScr, now); db.AddRange(modifyScr, second); await db.SaveChangesAsync();
            await new RequirementBaselineMaterializer(db, new VerificationImpactService(db)).MaterializeAsync(second.Id, "cm", now, default);

            var retireScr = ApprovedScr("SCR-00000003", "SWR-00002375", 2, RequirementChangeKind.Retire, "", project.Id, release.Id, now);
            var third = FrozenBaseline("SWBL-00000003", project.Id, release.Id, second.Id, retireScr, now); db.AddRange(retireScr, third); await db.SaveChangesAsync();
            await new RequirementBaselineMaterializer(db, new VerificationImpactService(db)).MaterializeAsync(third.Id, "cm", now, default);

            var artifact = await db.Requirements.SingleAsync(); var history = await db.RequirementRevisions.Where(x => x.ArtifactId == artifact.Id).OrderBy(x => x.Revision).ToListAsync();
            Assert.Equal([0, 1, 2], history.Select(x => x.Revision)); Assert.Equal(RequirementRevisionState.Retired, history[^1].State);
            Assert.Single(await db.BaselineRequirements.Where(x => x.BaselineId == first.Id).ToListAsync());
            var secondMember = await db.BaselineRequirements.SingleAsync(x => x.BaselineId == second.Id); Assert.Equal(history[1].Id, secondMember.RevisionId);
            Assert.Empty(await db.BaselineRequirements.Where(x => x.BaselineId == third.Id).ToListAsync());
        }
        finally { File.Delete(path); }
    }

    private static SystemChangeRequest ApprovedScr(string scrNumber, string requirementNumber, int revision, RequirementChangeKind kind, string statement, Guid projectId, Guid releaseId, DateTimeOffset now)
    {
        var scr = new SystemChangeRequest(scrNumber, 0, projectId, releaseId, kind.ToString(), "P", "A", "S", "author", now);
        scr.AddRequirementChange("author", requirementNumber, revision, RequirementLevel.HighLevel, kind, statement, "Rationale", "Test", now);
        scr.SubmitForReview("author", [new("reviewer", "Reviewer")], now); scr.ApproveActiveStage("reviewer", now); return scr;
    }
    private static CandidateBaseline FrozenBaseline(string number, Guid projectId, Guid releaseId, Guid? predecessor, SystemChangeRequest scr, DateTimeOffset now)
    {
        var baseline = new CandidateBaseline(number, 0, projectId, releaseId, predecessor, number, "cm", now); baseline.Select(scr, "cm", now); baseline.Freeze("cm", now); return baseline;
    }
}
