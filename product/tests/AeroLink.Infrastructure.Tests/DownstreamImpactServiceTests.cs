using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Programs;
using AeroLink.Domain.Releases;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AeroLink.Infrastructure.Tests;

public sealed class DownstreamImpactServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 31, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Approved_system_and_hlr_changes_raise_the_correct_consuming_discipline_once()
    {
        var path = Path.Combine(Path.GetTempPath(), $"aerolink-downstream-{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<AeroLinkDbContext>().UseSqlite($"Data Source={path};Pooling=False").Options;
        try
        {
            await using var db = new AeroLinkDbContext(options);
            await db.Database.EnsureCreatedAsync();
            var program = new ProgramRecord("Downstream Program", "DSP");
            var project = new ProjectRecord(program.Id, "FMS", "FMS");
            var release = new SoftwareRelease(project.Id, "1.6", false);
            var system = Approved(project.Id, release.Id, "SCR-00031", RequirementLevel.System, "SYSR-000151");
            var software = Approved(project.Id, release.Id, "SWCR-00076", RequirementLevel.HighLevel, "HLR-000401", ChangeRequestType.Software);
            db.AddRange(program, project, release, system, software); await db.SaveChangesAsync();

            var service = new DownstreamImpactService(db);
            Assert.Equal(1, await service.RaiseForApprovedChangeRequestAsync(system, Now, default));
            Assert.Equal(1, await service.RaiseForApprovedChangeRequestAsync(software, Now, default));
            await db.SaveChangesAsync();
            Assert.Equal(0, await service.RaiseForApprovedChangeRequestAsync(system, Now, default));

            var assessments = await db.DownstreamChangeAssessments.AsNoTracking().OrderBy(x => x.SourceChangeRequestNumber).ToListAsync();
            Assert.Collection(assessments,
                x => Assert.Equal(RequirementLevel.HighLevel, x.TargetLevel),
                x => Assert.Equal(RequirementLevel.LowLevel, x.TargetLevel));
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public async Task Replacement_source_revision_marks_earlier_assessment_out_of_date_and_raises_a_fresh_one()
    {
        var path = Path.Combine(Path.GetTempPath(), $"aerolink-downstream-supersede-{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<AeroLinkDbContext>().UseSqlite($"Data Source={path};Pooling=False").Options;
        try
        {
            await using var db = new AeroLinkDbContext(options); await db.Database.EnsureCreatedAsync();
            var program = new ProgramRecord("Downstream Program", "DSR"); var project = new ProjectRecord(program.Id, "FMS", "FMS");
            var release = new SoftwareRelease(project.Id, "1.6", false);
            var original = Approved(project.Id, release.Id, "SCR-00032", RequirementLevel.System, "SYSR-000075");
            db.AddRange(program, project, release, original); await db.SaveChangesAsync();
            var service = new DownstreamImpactService(db); await service.RaiseForApprovedChangeRequestAsync(original, Now, default); await db.SaveChangesAsync();

            var replacement = Approved(project.Id, release.Id, "SCR-00032", RequirementLevel.System, "SYSR-000075", revision: 1);
            db.Add(replacement); await db.SaveChangesAsync();
            await service.RaiseForApprovedChangeRequestAsync(replacement, Now.AddHours(1), default); await db.SaveChangesAsync();

            var rows = (await db.DownstreamChangeAssessments.AsNoTracking().ToListAsync()).OrderBy(x => x.CreatedAt).ToList();
            Assert.Equal(DownstreamAssessmentState.Superseded, rows[0].State);
            Assert.Equal(rows[1].Id, rows[0].SupersededByAssessmentId);
            Assert.Contains("Reassess", rows[0].SupersededReason);
            Assert.Equal(DownstreamAssessmentState.Open, rows[1].State);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); if (File.Exists(path)) File.Delete(path); }
    }

    private static SystemChangeRequest Approved(Guid projectId, Guid releaseId, string number,
        RequirementLevel level, string requirement, ChangeRequestType type = ChangeRequestType.System, int revision = 0)
    {
        var request = new SystemChangeRequest(number, revision, projectId, releaseId, "Approved change", "P", "A", "S", "author", Now, type);
        request.AddRequirementChange("author", requirement, revision, level, RequirementChangeKind.Modify,
            "The requirement shall contain revised controlled behavior.", "Approved revision", "Test", Now);
        request.SubmitForReview("author", [new("reviewer", "Reviewer")], Now);
        request.ApproveActiveStage("reviewer", Now);
        return request;
    }
}
