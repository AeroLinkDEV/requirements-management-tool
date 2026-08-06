using AeroLink.Domain.Baselines;
using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Programs;
using AeroLink.Domain.Releases;
using AeroLink.Domain.Requirements;
using AeroLink.Domain.Verification;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AeroLink.Infrastructure.Tests;

public sealed class VerificationCoverageProjectionTests
{
    [Fact]
    public async Task Projection_distinguishes_uncovered_suspect_confirmed_and_mixed_requirements()
    {
        var path = Path.Combine(Path.GetTempPath(), $"aerolink-coverage-{Guid.NewGuid():N}.db");
        try
        {
            var options = new DbContextOptionsBuilder<AeroLinkDbContext>()
                .UseSqlite($"Data Source={path};Pooling=False").Options;
            await using var db = new AeroLinkDbContext(options);
            await db.Database.EnsureCreatedAsync();
            var now = DateTimeOffset.UtcNow;

            var program = new ProgramRecord("Coverage Program", "CVG");
            var project = new ProjectRecord(program.Id, "Software", "Coverage Software");
            var release = new SoftwareRelease(project.Id, "1.0", false);
            var origin = new SystemChangeRequest("SRCR-00001", 0, project.Id, release.Id,
                "Origin", "P", "A", "S", "author", now);
            var baseline = new CandidateBaseline("SW-00.10", 0, project.Id, release.Id, null,
                "Projection fixture", "cm", now);
            db.AddRange(program, project, release, origin, baseline);

            static (RequirementArtifact Artifact, RequirementRevision Revision) Requirement(
                Guid projectId, Guid originId, Guid baselineId, string number, DateTimeOffset at)
            {
                var artifact = new RequirementArtifact(projectId, number, RequirementLevel.System, at);
                var revision = new RequirementRevision(artifact.Id, 0, $"{number} statement", "Rationale", "Test",
                    RequirementRevisionState.Active, originId, baselineId, at);
                return (artifact, revision);
            }

            var uncovered = Requirement(project.Id, origin.Id, baseline.Id, "SYSR-000001", now);
            var suspectOnly = Requirement(project.Id, origin.Id, baseline.Id, "SYSR-000002", now);
            var confirmedOnly = Requirement(project.Id, origin.Id, baseline.Id, "SYSR-000003", now);
            var mixed = Requirement(project.Id, origin.Id, baseline.Id, "SYSR-000004", now);
            db.AddRange(
                uncovered.Artifact, uncovered.Revision,
                suspectOnly.Artifact, suspectOnly.Revision,
                confirmedOnly.Artifact, confirmedOnly.Revision,
                mixed.Artifact, mixed.Revision);

            var suspectProcedure = new TestProcedure(project.Id, "SYSTP-000001", "Carried procedure", "author", now, TestProcedureLevel.System);
            var suspectRevision = new TestProcedureRevision(suspectProcedure.Id, 0, "Objective", "Preconditions",
                "Steps", "Expected", TestProcedureState.Approved, "author", now);
            var confirmedProcedure = new TestProcedure(project.Id, "SYSTP-000002", "Confirmed procedure", "author", now, TestProcedureLevel.System);
            var confirmedRevision = new TestProcedureRevision(confirmedProcedure.Id, 0, "Objective", "Preconditions",
                "Steps", "Expected", TestProcedureState.Approved, "author", now);
            db.AddRange(suspectProcedure, suspectRevision, confirmedProcedure, confirmedRevision);
            db.TestCoverage.AddRange(
                TestRequirementCoverage.CarriedForward(suspectRevision.Id, suspectOnly.Revision.Id,
                    "Requirement wording changed.", now),
                new TestRequirementCoverage(confirmedRevision.Id, confirmedOnly.Revision.Id),
                TestRequirementCoverage.CarriedForward(suspectRevision.Id, mixed.Revision.Id,
                    "Requirement wording changed.", now),
                new TestRequirementCoverage(confirmedRevision.Id, mixed.Revision.Id));
            await db.SaveChangesAsync();

            var revisionIds = new[]
            {
                uncovered.Revision.Id, suspectOnly.Revision.Id, confirmedOnly.Revision.Id, mixed.Revision.Id
            };
            var projected = await VerificationCoverageProjection.ForRequirementRevisionsAsync(db, revisionIds, default);
            var byRequirement = projected.ToLookup(x => x.RequirementRevisionId);

            Assert.Empty(byRequirement[uncovered.Revision.Id]);
            Assert.Equal(["Suspect"], byRequirement[suspectOnly.Revision.Id].Select(x => x.CoverageState));
            Assert.Equal(["Confirmed"], byRequirement[confirmedOnly.Revision.Id].Select(x => x.CoverageState));
            Assert.Equal(["Confirmed", "Suspect"],
                byRequirement[mixed.Revision.Id].Select(x => x.CoverageState).Order());
            Assert.All(projected, link => Assert.Equal("Approved", link.ProcedureState));
            Assert.All(projected, link => Assert.NotEqual(Guid.Empty, link.ProcedureRevisionId));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
