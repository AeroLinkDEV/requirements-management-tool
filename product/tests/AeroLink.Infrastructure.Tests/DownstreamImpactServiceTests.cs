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
            var system = Approved(project.Id, release.Id, "SRCR-00031", RequirementLevel.System, "SYSR-000151");
            var software = Approved(project.Id, release.Id, "HLRCR-00076", RequirementLevel.HighLevel, "HLR-000401", ChangeRequestType.Software);
            db.AddRange(program, project, release, system, software); await db.SaveChangesAsync();

            var service = new DownstreamImpactService(db);
            Assert.Equal(1, await service.RaiseForApprovedChangeRequestAsync(system, Now, default));
            Assert.Equal(1, await service.RaiseForApprovedChangeRequestAsync(software, Now, default));
            await db.SaveChangesAsync();
            Assert.Equal(0, await service.RaiseForApprovedChangeRequestAsync(system, Now, default));

            // Asserted as a mapping rather than as a sorted list. Ordering by identifier made the test
            // depend on where the prefixes happen to fall alphabetically, which says nothing about which
            // discipline consumes which change: a System change is assessed by HLR engineering, and an HLR
            // change is assessed by LLR engineering.
            var assessments = await db.DownstreamChangeAssessments.AsNoTracking().ToListAsync();
            Assert.Equal(2, assessments.Count);
            Assert.Equal(RequirementLevel.HighLevel,
                Assert.Single(assessments, x => x.SourceChangeRequestNumber.StartsWith("SRCR-")).TargetLevel);
            Assert.Equal(RequirementLevel.LowLevel,
                Assert.Single(assessments, x => x.SourceChangeRequestNumber.StartsWith("HLRCR-")).TargetLevel);
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
            var original = Approved(project.Id, release.Id, "SRCR-00032", RequirementLevel.System, "SYSR-000075");
            db.AddRange(program, project, release, original); await db.SaveChangesAsync();
            var service = new DownstreamImpactService(db); await service.RaiseForApprovedChangeRequestAsync(original, Now, default); await db.SaveChangesAsync();

            var replacement = Approved(project.Id, release.Id, "SRCR-00032", RequirementLevel.System, "SYSR-000075", revision: 1);
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

    [Fact]
    public async Task Legacy_mismatched_change_request_does_not_raise_more_invalid_assessments()
    {
        var path = Path.Combine(Path.GetTempPath(), $"aerolink-downstream-legacy-{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<AeroLinkDbContext>().UseSqlite($"Data Source={path};Pooling=False").Options;
        try
        {
            Guid requestId;
            await using (var seed = new AeroLinkDbContext(options))
            {
                await seed.Database.EnsureCreatedAsync();
                var program = new ProgramRecord("Legacy Program", "DSL");
                var project = new ProjectRecord(program.Id, "FMS", "FMS");
                var release = new SoftwareRelease(project.Id, "1.6", false);
                var request = Approved(project.Id, release.Id, "SRCR-00032", RequirementLevel.System, "SYSR-000075");
                requestId = request.Id;
                seed.AddRange(program, project, release, request);
                await seed.SaveChangesAsync();
                await seed.Database.ExecuteSqlRawAsync(
                    "UPDATE requirement_changes SET Level = 'HighLevel' WHERE ChangeRequestId = {0}", requestId);
            }

            await using var db = new AeroLinkDbContext(options);
            var legacy = await db.SystemChangeRequests.Include(x => x.RequirementChanges).SingleAsync(x => x.Id == requestId);
            var service = new DownstreamImpactService(db);

            Assert.Equal(0, await service.RaiseForApprovedChangeRequestAsync(legacy, Now, default));
            Assert.Empty(await db.DownstreamChangeAssessments.ToListAsync());
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public async Task Correctly_classified_replacement_supersedes_matching_legacy_assessment()
    {
        var path = Path.Combine(Path.GetTempPath(), $"aerolink-downstream-remediation-{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<AeroLinkDbContext>().UseSqlite($"Data Source={path};Pooling=False").Options;
        try
        {
            Guid projectId;
            Guid releaseId;
            await using (var seed = new AeroLinkDbContext(options))
            {
                await seed.Database.EnsureCreatedAsync();
                var program = new ProgramRecord("Legacy Program", "DSX");
                var project = new ProjectRecord(program.Id, "FMS", "FMS");
                var release = new SoftwareRelease(project.Id, "1.6", false);
                var legacy = Approved(project.Id, release.Id, "SRCR-00032", RequirementLevel.System, "HLR-000075", revision: 2);
                var invalidAssessment = new DownstreamChangeAssessment(project.Id, release.Id, legacy.Id,
                    legacy.DisplayNumber, RequirementLevel.LowLevel, Now);
                projectId = project.Id;
                releaseId = release.Id;
                seed.AddRange(program, project, release, legacy, invalidAssessment);
                await seed.SaveChangesAsync();
                await seed.Database.ExecuteSqlRawAsync(
                    "UPDATE requirement_changes SET Level = 'HighLevel' WHERE ChangeRequestId = {0}", legacy.Id);
            }

            await using var db = new AeroLinkDbContext(options);
            var replacement = Approved(projectId, releaseId, "HLRCR-00104", RequirementLevel.HighLevel,
                "HLR-000075", ChangeRequestType.Software, revision: 2);
            db.Add(replacement);
            await db.SaveChangesAsync();

            var service = new DownstreamImpactService(db);
            Assert.Equal(1, await service.RaiseForApprovedChangeRequestAsync(replacement, Now.AddHours(1), default));
            await db.SaveChangesAsync();

            var rows = (await db.DownstreamChangeAssessments.AsNoTracking().ToListAsync())
                .OrderBy(x => x.CreatedAt).ToList();
            Assert.Equal(DownstreamAssessmentState.Superseded, rows[0].State);
            Assert.Equal(rows[1].Id, rows[0].SupersededByAssessmentId);
            Assert.Contains("correctly classified replacement", rows[0].SupersededReason);
            Assert.Equal(DownstreamAssessmentState.Open, rows[1].State);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); if (File.Exists(path)) File.Delete(path); }
    }

    private static SystemChangeRequest Approved(Guid projectId, Guid releaseId, string number,
        RequirementLevel level, string requirement, ChangeRequestType type = ChangeRequestType.System, int revision = 0)
    {
        var request = new SystemChangeRequest(number, revision, projectId, releaseId, "Approved change", "P", "A", "S", "author", Now, type, softwareLevel: type == ChangeRequestType.Software ? (level == RequirementLevel.LowLevel ? RequirementLevel.LowLevel : RequirementLevel.HighLevel) : null);
        request.AddRequirementChange("author", requirement, revision, level, RequirementChangeKind.Modify,
            "The requirement shall contain revised controlled behavior.", "Approved revision", "Test", Now);
        request.SubmitForReview("author", [new("reviewer", "Reviewer")], Now);
        request.ApproveActiveStage("reviewer", Now);
        return request;
    }
}
