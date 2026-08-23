using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Common;
using AeroLink.Domain.Baselines;
using AeroLink.Domain.Hierarchy;
using AeroLink.Domain.Identity;
using AeroLink.Domain.Programs;
using AeroLink.Domain.Requirements;
using AeroLink.Domain.Verification;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AeroLink.Infrastructure.Tests;

/// <summary>
/// A test procedure covers requirements at its own level, and nothing else.
///
/// An HLR test procedure exists because it verifies one or more HLRs. The product used to allow it to be
/// linked to a System requirement, and that is what let a System change request raise work in the HLR queue:
/// retiring the System requirement stranded the HLR procedure, and the orphan was routed by the procedure's
/// level onto a change request of a different discipline. These are about that link never forming.
/// </summary>
public sealed class CoverageLevelDisciplineTests
{
    private sealed record World(AeroLinkDbContext Db, Guid ProjectId, Guid ReleaseId, Guid ChangeRequestId, Guid BaselineId);

    [Fact]
    public async Task A_procedure_cannot_verify_a_requirement_from_another_level()
    {
        var path = Path.Combine(Path.GetTempPath(), $"aerolink-level-{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<AeroLinkDbContext>().UseSqlite($"Data Source={path};Pooling=False").Options;
        try
        {
            await using var db = new AeroLinkDbContext(options);
            var world = await SeedAsync(db, "LVL");

            var (_, systemRequirement) = await RequirementAsync(db, world, "SYSR-000001", RequirementLevel.System);
            var (_, highLevelProcedure) = await ProcedureAsync(db, world, "HLRTC-000001", TestProcedureLevel.HighLevel);

            db.TestCoverage.Add(new TestRequirementCoverage(highLevelProcedure.Id, systemRequirement.Id));
            var error = await Assert.ThrowsAsync<DomainException>(() => db.SaveChangesAsync());

            // The message names both artifacts and both levels, because "invalid link" would leave the reader
            // to work out which of the two is in the wrong place.
            Assert.Contains("HLRTC-000001", error.Message);
            Assert.Contains("SYSR-000001", error.Message);
            Assert.Contains("its own level", error.Message);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task A_procedure_covers_as_many_requirements_at_its_own_level_as_it_needs_to()
    {
        var path = Path.Combine(Path.GetTempPath(), $"aerolink-level-ok-{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<AeroLinkDbContext>().UseSqlite($"Data Source={path};Pooling=False").Options;
        try
        {
            await using var db = new AeroLinkDbContext(options);
            var world = await SeedAsync(db, "LVO");

            var (_, first) = await RequirementAsync(db, world, "SYSR-000001", RequirementLevel.System);
            var (_, second) = await RequirementAsync(db, world, "SYSR-000002", RequirementLevel.System);
            var (_, procedure) = await ProcedureAsync(db, world, "SYSTP-000001", TestProcedureLevel.System);

            // One System procedure answering for two System requirements is ordinary, and is exactly why a
            // retirement does not always strand it.
            db.TestCoverage.Add(new TestRequirementCoverage(procedure.Id, first.Id));
            db.TestCoverage.Add(new TestRequirementCoverage(procedure.Id, second.Id));
            await db.SaveChangesAsync();

            Assert.Equal(2, await db.TestCoverage.CountAsync());
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Every_level_pairing_is_stated_rather_than_assumed()
    {
        var path = Path.Combine(Path.GetTempPath(), $"aerolink-level-matrix-{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<AeroLinkDbContext>().UseSqlite($"Data Source={path};Pooling=False").Options;
        try
        {
            await using var db = new AeroLinkDbContext(options);
            var world = await SeedAsync(db, "LVM");

            var requirements = new Dictionary<RequirementLevel, Guid>();
            var procedures = new Dictionary<TestProcedureLevel, Guid>();
            var levels = new[]
            {
                (RequirementLevel.System, TestProcedureLevel.System, "SYSR", "SYSTP"),
                (RequirementLevel.HighLevel, TestProcedureLevel.HighLevel, "HLR", "HLRTC"),
                (RequirementLevel.LowLevel, TestProcedureLevel.LowLevel, "LLR", "LLRTC"),
            };
            var index = 1;
            foreach (var (requirementLevel, procedureLevel, requirementPrefix, procedurePrefix) in levels)
            {
                var (_, requirement) = await RequirementAsync(db, world, $"{requirementPrefix}-{index:D6}", requirementLevel);
                var (_, procedure) = await ProcedureAsync(db, world, $"{procedurePrefix}-{index:D6}", procedureLevel);
                requirements[requirementLevel] = requirement.Id;
                procedures[procedureLevel] = procedure.Id;
                index++;
            }

            foreach (var (requirementLevel, requirementRevisionId) in requirements)
            foreach (var (procedureLevel, procedureRevisionId) in procedures)
            {
                db.TestCoverage.Add(new TestRequirementCoverage(procedureRevisionId, requirementRevisionId));
                var matches = requirementLevel.ToString() == procedureLevel.ToString();
                if (matches) await db.SaveChangesAsync();
                else
                {
                    await Assert.ThrowsAsync<DomainException>(() => db.SaveChangesAsync());
                    // The rejected entity stays tracked, so drop it before the next pairing.
                    db.ChangeTracker.Clear();
                }
            }

            // Three of the nine pairings are legitimate; the six that cross a level are not.
            Assert.Equal(3, await db.TestCoverage.CountAsync());
        }
        finally { File.Delete(path); }
    }

    private static async Task<World> SeedAsync(AeroLinkDbContext db, string prefix)
    {
        await db.Database.EnsureCreatedAsync();
        var program = new ProgramRecord("Level Program", prefix);
        var project = new ProjectRecord(program.Id, "Software", "Level Software");
        var release = new SoftwareRelease(project.Id, "1.6", false);
        var now = DateTimeOffset.UtcNow;
        db.AddRange(program, project, release, LegacyDefaultProjectLadderFactory.Create(project.Id, now));
        await db.SaveChangesAsync();

        // A revision names the change request that produced it and the baseline it became effective in, and
        // both are real foreign keys — so the fixture supplies real ones rather than plausible-looking GUIDs.
        var scr = new SystemChangeRequest("SRCR-00001", 0, project.Id, release.Id, "Levels", "P", "A", "S", "author", now);
        scr.AddRequirementChange("author", "SYSR-000999", 0, RequirementLevel.System,
            RequirementChangeKind.Introduce, "The FMS shall hold a level.", "Needed.", "Test", now);
        scr.SubmitForReview("author", [new ApproverSelection("reviewer", "Reviewer")], now);
        scr.ApproveActiveStage("reviewer", now);
        var baseline = new CandidateBaseline("SW-00.10", 0, project.Id, release.Id, null, "Levels", "cm", now);
        baseline.Select(scr, "cm", now);
        baseline.Freeze("cm", now);
        db.AddRange(scr, baseline);
        await db.SaveChangesAsync();

        return new World(db, project.Id, release.Id, scr.Id, baseline.Id);
    }

    private static async Task<(RequirementArtifact, RequirementRevision)> RequirementAsync(AeroLinkDbContext db,
        World world, string baseNumber, RequirementLevel level)
    {
        var now = DateTimeOffset.UtcNow;
        var artifact = new RequirementArtifact(world.ProjectId, baseNumber, level, now);
        var revision = new RequirementRevision(artifact.Id, 0, "The system shall do the thing.", "Because.",
            "Test", RequirementRevisionState.Active, world.ChangeRequestId, world.BaselineId, now);
        db.AddRange(artifact, revision);
        await db.SaveChangesAsync();
        return (artifact, revision);
    }

    private static async Task<(TestProcedure, TestProcedureRevision)> ProcedureAsync(AeroLinkDbContext db,
        World world, string baseNumber, TestProcedureLevel level)
    {
        var now = DateTimeOffset.UtcNow;
        var procedure = new TestProcedure(world.ProjectId, baseNumber, "A procedure", "owner", now, level);
        var revision = new TestProcedureRevision(procedure.Id, 0, "Objective", "Preconditions", "Steps",
            "Expected", TestProcedureState.Approved, "author", now);
        db.AddRange(procedure, revision);
        await db.SaveChangesAsync();
        return (procedure, revision);
    }
}
