using System.Text.Json;
using AeroLink.Domain.Baselines;
using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Programs;
using AeroLink.Domain.Releases;
using AeroLink.Domain.Requirements;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AeroLink.Infrastructure.Tests;

/// <summary>
/// Requirements without a structured profile are given one automatically. That profile was written as an
/// escaped <c>&lt;p&gt;</c> element into the field the product describes as holding structure and never
/// markup — so the content editor and the controlled preview showed literal tags around existing
/// requirements, and an author checking one back in would have published them.
/// </summary>
public sealed class RequirementProfileContentTests
{
    [Fact]
    public async Task Synthesized_requirement_content_is_structured_rather_than_markup()
    {
        var path = Path.Combine(Path.GetTempPath(), $"aerolink-profile-{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<AeroLinkDbContext>().UseSqlite($"Data Source={path};Pooling=False").Options;
        var now = DateTimeOffset.UtcNow;
        try
        {
            Guid projectId, revisionId;
            await using (var setup = new AeroLinkDbContext(options))
            {
                await setup.Database.EnsureCreatedAsync();
                var program = new ProgramRecord("Content Program", "CTP");
                var project = new ProjectRecord(program.Id, "Software", "Content Software");
                var release = new SoftwareRelease(project.Id, "1.0", false);
                var scr = new SystemChangeRequest("SCR-00000700", 0, project.Id, release.Id, "Content", "P", "A", "S", "author", now);
                var baseline = new CandidateBaseline("SWBL-00000700", 0, project.Id, release.Id, null, "Candidate", "cm", now);
                var artifact = new RequirementArtifact(project.Id, "SYSR-00000700", RequirementLevel.System, now);
                // An apostrophe and an angle bracket, because the old path escaped them into entities that
                // then had to be read back as literal text.
                var revision = new RequirementRevision(artifact.Id, 1,
                    "The FMS shall reject a track angle > 90 degrees and hold the crew's selected mode.",
                    "Rationale", "Test", RequirementRevisionState.Active, scr.Id, baseline.Id, now);
                setup.AddRange(program, project, release, scr, baseline, artifact, revision);
                await setup.SaveChangesAsync();
                projectId = project.Id; revisionId = revision.Id;
            }

            await using (var act = new AeroLinkDbContext(options))
            {
                await new EnterpriseRequirementsService(act).SynchronizeProjectAsync(projectId, "sync.actor");
                await act.SaveChangesAsync();
            }

            await using var assert = new AeroLinkDbContext(options);
            var profile = await assert.RequirementRevisionProfiles.AsNoTracking().SingleAsync(x => x.RevisionId == revisionId);

            Assert.DoesNotContain("<p>", profile.RichText);
            Assert.DoesNotContain("&lt;", profile.RichText);

            // The canonical model, and the statement carried through it unaltered.
            using var parsed = JsonDocument.Parse(profile.RichText);
            var block = Assert.Single(parsed.RootElement.GetProperty("blocks").EnumerateArray().ToList());
            Assert.Equal("paragraph", block.GetProperty("type").GetString());
            Assert.Equal("The FMS shall reject a track angle > 90 degrees and hold the crew's selected mode.",
                block.GetProperty("text").GetString());
        }
        finally { File.Delete(path); }
    }
}
