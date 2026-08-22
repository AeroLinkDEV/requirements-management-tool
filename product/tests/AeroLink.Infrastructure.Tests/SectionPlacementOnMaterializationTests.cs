using AeroLink.Domain.Baselines;
using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Common;
using AeroLink.Domain.Programs;
using AeroLink.Domain.Requirements;
using AeroLink.Domain.Traceability;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace AeroLink.Infrastructure.Tests;

/// <summary>
/// Where a requirement goes in the document, as its author said.
///
/// Section membership is a `SpecificationNode` row and nothing on the authoring path created one: an introduced
/// requirement was placed by a backfill that derives a section from a hash of its number, and a modification could
/// not move one at all. So a change request could say what a requirement means and not where it goes, which is
/// half of what an author is deciding — and the read side had section filtering that nothing could ever aim.
///
/// Placement happens at materialization, because that is where the requirement first exists to be placed.
/// </summary>
public sealed class SectionPlacementOnMaterializationTests
{
    [Fact]
    public async Task An_introduced_requirement_lands_in_the_section_its_author_chose()
    {
        var path = Path.Combine(Path.GetTempPath(), $"aerolink-section-{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<AeroLinkDbContext>().UseSqlite($"Data Source={path};Pooling=False").Options;
        try
        {
            await using var db = new AeroLinkDbContext(options);
            await db.Database.EnsureCreatedAsync();
            var now = DateTimeOffset.UtcNow;
            var (projectId, releaseId, navigation, performance) = await SeedAsync(db, now);

            var scr = ApprovedScr("HLRCR-00001", "SWR-00002375", 0, RequirementChangeKind.Introduce,
                "The software shall sequence oceanic waypoints.", projectId, releaseId, now, navigation);
            var baseline = FrozenBaseline("SW-00.10", projectId, releaseId, null, scr, now);
            db.AddRange(scr, baseline);
            await db.SaveChangesAsync();

            await new RequirementBaselineMaterializer(db, new VerificationImpactService(db))
                .MaterializeAsync(baseline.Id, "cm", now, default);

            var artifact = await db.Requirements.SingleAsync();
            var placement = await db.SpecificationNodes.SingleAsync(x => x.RequirementArtifactId == artifact.Id);
            Assert.Equal(navigation, placement.ParentId);
            Assert.Equal(SpecificationNodeType.Requirement, placement.Type);
            // Not the other section, which is the assertion that would still pass if the chosen one were ignored
            // and a placement happened to be created anyway.
            Assert.NotEqual(performance, placement.ParentId);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task A_modification_moves_the_requirement_rather_than_placing_it_twice()
    {
        var path = Path.Combine(Path.GetTempPath(), $"aerolink-section-move-{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<AeroLinkDbContext>().UseSqlite($"Data Source={path};Pooling=False").Options;
        try
        {
            await using var db = new AeroLinkDbContext(options);
            await db.Database.EnsureCreatedAsync();
            var now = DateTimeOffset.UtcNow;
            var (projectId, releaseId, navigation, performance) = await SeedAsync(db, now);

            var introduce = ApprovedScr("HLRCR-00001", "SWR-00002375", 0, RequirementChangeKind.Introduce,
                "The software shall sequence oceanic waypoints.", projectId, releaseId, now, navigation);
            var first = FrozenBaseline("SW-00.10", projectId, releaseId, null, introduce, now);
            db.AddRange(introduce, first);
            await db.SaveChangesAsync();
            await new RequirementBaselineMaterializer(db, new VerificationImpactService(db))
                .MaterializeAsync(first.Id, "cm", now, default);

            var modify = ApprovedScr("HLRCR-00002", "SWR-00002375", 1, RequirementChangeKind.Modify,
                "The software shall sequence oceanic waypoints deterministically.", projectId, releaseId, now, performance);
            var second = FrozenBaseline("SW-00.20", projectId, releaseId, first.Id, modify, now);
            db.AddRange(modify, second);
            await db.SaveChangesAsync();
            await new RequirementBaselineMaterializer(db, new VerificationImpactService(db))
                .MaterializeAsync(second.Id, "cm", now, default);

            var artifact = await db.Requirements.SingleAsync();
            // One placement, moved. Two would put the requirement in the document twice, which is the failure a
            // careless "add a node" would produce and which nothing downstream would notice.
            var placement = await db.SpecificationNodes.SingleAsync(x => x.RequirementArtifactId == artifact.Id);
            Assert.Equal(performance, placement.ParentId);
        }
        finally { File.Delete(path); }
    }

    /// <summary>
    /// No chosen section changes nothing, so this is additive: a proposal that does not name one is still valid,
    /// and the existing placement rule is left to decide. That matters because a proposal is worth saving before
    /// every field is settled.
    /// </summary>
    [Fact]
    public async Task A_change_with_no_chosen_section_creates_no_placement_of_its_own()
    {
        var path = Path.Combine(Path.GetTempPath(), $"aerolink-section-none-{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<AeroLinkDbContext>().UseSqlite($"Data Source={path};Pooling=False").Options;
        try
        {
            await using var db = new AeroLinkDbContext(options);
            await db.Database.EnsureCreatedAsync();
            var now = DateTimeOffset.UtcNow;
            var (projectId, releaseId, _, _) = await SeedAsync(db, now);

            var scr = ApprovedScr("HLRCR-00001", "SWR-00002375", 0, RequirementChangeKind.Introduce,
                "The software shall sequence oceanic waypoints.", projectId, releaseId, now, targetSectionId: null);
            var baseline = FrozenBaseline("SW-00.10", projectId, releaseId, null, scr, now);
            db.AddRange(scr, baseline);
            await db.SaveChangesAsync();

            await new RequirementBaselineMaterializer(db, new VerificationImpactService(db))
                .MaterializeAsync(baseline.Id, "cm", now, default);

            var artifact = await db.Requirements.SingleAsync();
            Assert.Empty(await db.SpecificationNodes.Where(x => x.RequirementArtifactId == artifact.Id).ToListAsync());
        }
        finally { File.Delete(path); }
    }

    /// <summary>
    /// A section belonging to another project is ignored rather than acted on. A stale identifier carried in a
    /// copied draft would otherwise file a requirement into a document it has nothing to do with — and it would
    /// look deliberate afterwards.
    /// </summary>
    [Fact]
    public async Task A_section_from_another_project_is_rejected_without_partial_materialization()
    {
        var path = Path.Combine(Path.GetTempPath(), $"aerolink-section-foreign-{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<AeroLinkDbContext>().UseSqlite($"Data Source={path};Pooling=False").Options;
        try
        {
            await using var db = new AeroLinkDbContext(options);
            await db.Database.EnsureCreatedAsync();
            var now = DateTimeOffset.UtcNow;
            var (projectId, releaseId, _, _) = await SeedAsync(db, now);
            var (_, _, foreignSection, _) = await SeedAsync(db, now, "Other", "OTH", "SWRD-000002");

            var scr = ApprovedScr("HLRCR-00001", "SWR-00002375", 0, RequirementChangeKind.Introduce,
                "The software shall sequence oceanic waypoints.", projectId, releaseId, now, foreignSection);
            var baseline = FrozenBaseline("SW-00.10", projectId, releaseId, null, scr, now);
            db.AddRange(scr, baseline);
            await db.SaveChangesAsync();

            var error = await Assert.ThrowsAsync<DomainException>(() =>
                new RequirementBaselineMaterializer(db, new VerificationImpactService(db))
                    .MaterializeAsync(baseline.Id, "cm", now, default));
            Assert.Contains("section that is no longer available", error.Message);
            db.ChangeTracker.Clear();
            Assert.Empty(await db.Requirements.Where(x => x.ProjectId == projectId).ToListAsync());
            Assert.Null((await db.CandidateBaselines.SingleAsync(x => x.Id == baseline.Id)).RequirementsMaterializedAt);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task A_late_placement_failure_rolls_back_trace_lifecycle_and_retry_is_single_shot()
    {
        var path = Path.Combine(Path.GetTempPath(), $"aerolink-section-trace-rollback-{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<AeroLinkDbContext>().UseSqlite($"Data Source={path};Pooling=False").Options;
        try
        {
            await using var db = new AeroLinkDbContext(options);
            await db.Database.EnsureCreatedAsync();
            var now = DateTimeOffset.UtcNow;
            var (projectId, releaseId, navigation, _) = await SeedAsync(db, now);
            var (_, _, foreignSection, _) = await SeedAsync(db, now, "Other", "OTH", "SWRD-000002");

            var introduceTarget = ApprovedScr("HLRCR-00001", "SWR-00002375", 0, RequirementChangeKind.Introduce,
                "The software shall sequence oceanic waypoints.", projectId, releaseId, now, navigation);
            var first = FrozenBaseline("SW-00.10", projectId, releaseId, null, introduceTarget, now);
            db.AddRange(introduceTarget, first);
            await db.SaveChangesAsync();
            await new RequirementBaselineMaterializer(db, new VerificationImpactService(db))
                .MaterializeAsync(first.Id, "cm", now, default);

            var targetRevision = await (from artifact in db.Requirements
                                        where artifact.ProjectId == projectId && artifact.BaseNumber == "SWR-00002375"
                                        join revision in db.RequirementRevisions on artifact.Id equals revision.ArtifactId
                                        where revision.Revision == 0
                                        select revision).SingleAsync();
            var introduceSource = ApprovedScr("HLRCR-00002", "SWR-00002376", 0, RequirementChangeKind.Introduce,
                "The software shall retain the selected waypoint.", projectId, releaseId, now, navigation,
                JsonSerializer.Serialize(new[] { targetRevision.Id }));
            var second = FrozenBaseline("SW-00.20", projectId, releaseId, first.Id, introduceSource, now);
            db.AddRange(introduceSource, second);
            await db.SaveChangesAsync();
            await new RequirementBaselineMaterializer(db, new VerificationImpactService(db))
                .MaterializeAsync(second.Id, "cm", now, default);

            var reviseTarget = ApprovedScr("HLRCR-00003", "SWR-00002375", 1, RequirementChangeKind.Modify,
                "The software shall sequence the selected waypoint.", projectId, releaseId, now, foreignSection);
            var third = FrozenBaseline("SW-00.30", projectId, releaseId, second.Id, reviseTarget, now);
            db.AddRange(reviseTarget, third);
            await db.SaveChangesAsync();

            var error = await Assert.ThrowsAsync<DomainException>(() =>
                new RequirementBaselineMaterializer(db, new VerificationImpactService(db))
                    .MaterializeAsync(third.Id, "cm", now, default));
            Assert.Contains("section that is no longer available", error.Message);

            db.ChangeTracker.Clear();
            Assert.Null((await db.CandidateBaselines.SingleAsync(x => x.Id == third.Id)).RequirementsMaterializedAt);
            Assert.Equal(1, await db.RequirementRevisions.CountAsync(x => x.ArtifactId == targetRevision.ArtifactId));
            Assert.Equal(1, await db.RequirementTraces.CountAsync());
            Assert.Empty(await db.ExactLinkSuspectLifecycles.ToListAsync());
            Assert.Empty(await db.ExactLinkSuspectEvents.ToListAsync());

            var corrected = await db.RequirementChanges
                .Where(x => x.ChangeRequestId == reviseTarget.Id)
                .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.TargetSectionId, navigation));
            Assert.Equal(1, corrected);

            await new RequirementBaselineMaterializer(db, new VerificationImpactService(db))
                .MaterializeAsync(third.Id, "cm", now, default);

            var currentTarget = await (from artifact in db.Requirements
                                       where artifact.ProjectId == projectId && artifact.BaseNumber == "SWR-00002375"
                                       join revision in db.RequirementRevisions on artifact.Id equals revision.ArtifactId
                                       where revision.Revision == 1
                                       select revision).SingleAsync();
            var carried = await db.RequirementTraces.SingleAsync(x => x.TargetRevisionId == currentTarget.Id);
            var lifecycle = await db.ExactLinkSuspectLifecycles.SingleAsync();
            Assert.Equal(lifecycle.Id, carried.ExactLinkSuspectLifecycleId);
            Assert.Equal(ExactLinkLifecycleState.Suspect, lifecycle.State);
            Assert.Equal(ExactLinkLifecycleCauseKind.InternalRequirementRevision, lifecycle.CauseKind);
            Assert.Equal(currentTarget.Id, lifecycle.CauseRequirementRevisionId);
            Assert.Single(await db.ExactLinkSuspectEvents.ToListAsync());
            Assert.Equal(2, await db.RequirementTraces.CountAsync());

            var retryError = await Assert.ThrowsAsync<DomainException>(() =>
                new RequirementBaselineMaterializer(db, new VerificationImpactService(db))
                    .MaterializeAsync(third.Id, "cm", now, default));
            Assert.Contains("already materialized", retryError.Message);
            Assert.Single(await db.ExactLinkSuspectLifecycles.ToListAsync());
            Assert.Single(await db.ExactLinkSuspectEvents.ToListAsync());
            Assert.Equal(2, await db.RequirementTraces.CountAsync());
        }
        finally { File.Delete(path); }
    }

    private static async Task<(Guid ProjectId, Guid ReleaseId, Guid Navigation, Guid Performance)> SeedAsync(
        AeroLinkDbContext db, DateTimeOffset now, string programName = "FMS", string programCode = "FMSR",
        string specificationNumber = "HLRD-000001")
    {
        var program = new ProgramRecord(programName, programCode);
        var project = new ProjectRecord(program.Id, "Software", $"{programName} Software");
        var release = new SoftwareRelease(project.Id, "3.3", false);
        db.AddRange(program, project, release);
        await db.SaveChangesAsync();

        // A specification for the level these requirements are written at, with two sections to choose between.
        var specification = new RequirementSpecification(project.Id, specificationNumber, "Software Requirements",
            RequirementLevel.HighLevel.ToString(), "Authoritative structured software requirements.", "cm", now);
        db.RequirementSpecifications.Add(specification);
        var navigation = new SpecificationNode(specification.Id, null, 1000, SpecificationNodeType.Section, "1. Navigation", null, "cm", now);
        var performance = new SpecificationNode(specification.Id, null, 2000, SpecificationNodeType.Section, "2. Performance", null, "cm", now);
        db.SpecificationNodes.AddRange(navigation, performance);
        await db.SaveChangesAsync();
        return (project.Id, release.Id, navigation.Id, performance.Id);
    }

    private static SystemChangeRequest ApprovedScr(string scrNumber, string requirementNumber, int revision,
        RequirementChangeKind kind, string statement, Guid projectId, Guid releaseId, DateTimeOffset now,
        Guid? targetSectionId, string proposedUpstreamRevisionIdsJson = "[]")
    {
        var scr = new SystemChangeRequest(scrNumber, 0, projectId, releaseId, kind.ToString(), "P", "A", "S", "author", now, ChangeRequestType.Software, softwareLevel: RequirementLevel.HighLevel);
        scr.AddRequirementChange("author", requirementNumber, revision, RequirementLevel.HighLevel, kind, statement,
            "Rationale", "Test", now, targetSectionId: targetSectionId,
            proposedUpstreamRevisionIdsJson: proposedUpstreamRevisionIdsJson);
        scr.SubmitForReview("author", [new("reviewer", "Reviewer")], now);
        scr.ApproveActiveStage("reviewer", now);
        return scr;
    }

    private static CandidateBaseline FrozenBaseline(string number, Guid projectId, Guid releaseId, Guid? predecessor,
        SystemChangeRequest scr, DateTimeOffset now)
    {
        var baseline = new CandidateBaseline(number, 0, projectId, releaseId, predecessor, number, "cm", now);
        baseline.Select(scr, "cm", now);
        baseline.Freeze("cm", now);
        return baseline;
    }
}
