using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Programs;
using AeroLink.Domain.Releases;
using AeroLink.Domain.Requirements;
using AeroLink.Domain.Verification;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AeroLink.Infrastructure.Tests;

public sealed class ProblemReportLinkServiceTests
{
    [Fact]
    public async Task A_build_scoped_pr_flows_from_proposed_change_to_tcr_and_approved_corrective_action()
    {
        var path = Path.Combine(Path.GetTempPath(), $"aerolink-pr-links-{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<AeroLinkDbContext>()
            .UseSqlite($"Data Source={path};Pooling=False").Options;
        try
        {
            await using var db = new AeroLinkDbContext(options);
            await db.Database.EnsureCreatedAsync();
            var now = DateTimeOffset.UtcNow;
            var program = new ProgramRecord("PR traceability", "PRTR");
            var project = new ProjectRecord(program.Id, "FMS", "FMS");
            var release = new SoftwareRelease(project.Id, "1.6", false);
            var otherRelease = new SoftwareRelease(project.Id, "1.7", false);
            var report = new ProblemReport(project.Id, "PR-00001", "Position disagreement",
                "Sources disagree during approach.", "", "quality.engineer", now);
            var scr = new SystemChangeRequest("SWCR-00001", 0, project.Id, release.Id,
                "Correct source selection", "P", "A", "S", "software.engineer", now,
                ChangeRequestType.Software);
            scr.AddRequirementChange("software.engineer", "LLR-000001", 1, RequirementLevel.LowLevel,
                RequirementChangeKind.Modify, "The software shall reject a stale position source.",
                "Correct the reported disagreement.", "Test", now);
            var tcr = new TestChangeReview(project.Id, release.Id, scr.Id,
                TestChangeReviewDiscipline.LowLevelSoftware, scr.DisplayNumber, now, "LLRTCR-000001");
            db.AddRange(program, project, release, otherRelease, report, scr, tcr);
            db.ProblemReportLinks.Add(new ProblemReportLink(report.Id, "Release", release.Id,
                "BuildScope", "quality.engineer", now));
            await db.SaveChangesAsync();

            var service = new ProblemReportLinkService(db);
            Assert.Null(await service.ValidateSelectionAsync(project.Id, release.Id, [report.Id], default));
            Assert.NotNull(await service.ValidateSelectionAsync(project.Id, otherRelease.Id, [report.Id], default));
            await service.LinkChangeRequestAsync(scr.Id, [report.Id], "software.engineer", now, default);
            await db.SaveChangesAsync();
            await service.PropagateToTestChangeRequestAsync(scr.Id, tcr.Id, "test.engineer", now, default);
            scr.SubmitForReview("software.engineer", [new("reviewer", "Reviewer")], now);
            await db.SaveChangesAsync();
            scr.ApproveActiveStage("reviewer", now);
            await service.RecordApprovedCorrectiveActionsAsync(scr, "reviewer", now, default);
            await db.SaveChangesAsync();

            var links = await db.ProblemReportLinks.AsNoTracking()
                .Where(x => x.ProblemReportId == report.Id).ToListAsync();
            Assert.Contains(links, x => x.ArtifactType == "ChangeRequest"
                && x.ArtifactId == scr.Id && x.Relationship == "ProposedCorrectiveAction");
            Assert.Contains(links, x => x.ArtifactType == "ChangeRequest"
                && x.ArtifactId == scr.Id && x.Relationship == "ApprovedCorrectiveAction");
            Assert.Contains(links, x => x.ArtifactType == "TestChangeRequest"
                && x.ArtifactId == tcr.Id && x.Relationship == "VerificationForProblem");
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
