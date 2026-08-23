using AeroLink.Domain.Baselines;
using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Common;
using AeroLink.Domain.Programs;
using AeroLink.Domain.Requirements;
using AeroLink.Domain.Traceability;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
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
        Guid projectId;
        Guid releaseId;
        Guid targetArtifactId;
        Guid thirdBaselineId;
        DateTimeOffset now = DateTimeOffset.UtcNow;
        try
        {
            Guid navigation;
            using (var setupDb = new AeroLinkDbContext(options))
            {
                await setupDb.Database.EnsureCreatedAsync();
                (projectId, releaseId, navigation, _) = await SeedAsync(setupDb, now);

                var introduceTarget = new SystemChangeRequest("HLRCR-00001", 0, projectId, releaseId,
                    "Introduce", "P", "A", "S", "author", now, ChangeRequestType.Software,
                    softwareLevel: RequirementLevel.HighLevel);
                var first = new CandidateBaseline("SW-00.10", 0, projectId, releaseId, null, "SW-00.10", "cm", now);
                // Both HLR introductions in this lifecycle scenario are allocated to this exact System
                // revision. A sibling HLR would exercise the wrong-level refusal rather than the rollback.
                var systemArtifact = new RequirementArtifact(projectId, "SYSR-00002374", RequirementLevel.System, now);
                var systemRevision = new RequirementRevision(systemArtifact.Id, 0, "The system shall retain selected waypoints.",
                    "System capability.", "Test", RequirementRevisionState.Active, introduceTarget.Id, first.Id, now);
                introduceTarget.AddRequirementChange("author", "SWR-00002375", 0, RequirementLevel.HighLevel,
                    RequirementChangeKind.Introduce, "The software shall sequence oceanic waypoints.", "Rationale", "Test", now,
                    targetSectionId: navigation, attributesJson: "{\"derived\":true}",
                    proposedUpstreamRevisionIdsJson: "[]");
                introduceTarget.SubmitForReview("author", [new("reviewer", "Reviewer")], now);
                introduceTarget.ApproveActiveStage("reviewer", now);
                first.Select(introduceTarget, "cm", now);
                first.Freeze("cm", now);
                setupDb.AddRange(introduceTarget, first, systemArtifact, systemRevision,
                    new BaselineRequirementSelection(first.Id, systemArtifact.Id, systemRevision.Id));
                await setupDb.SaveChangesAsync();
                await new RequirementBaselineMaterializer(setupDb, new VerificationImpactService(setupDb))
                    .MaterializeAsync(first.Id, "cm", now, default);

                var targetRevision = await (from artifact in setupDb.Requirements
                                            where artifact.ProjectId == projectId && artifact.BaseNumber == "SWR-00002375"
                                            join revision in setupDb.RequirementRevisions on artifact.Id equals revision.ArtifactId
                                            where revision.Revision == 0
                                            select revision).SingleAsync();
                targetArtifactId = targetRevision.ArtifactId;
                var introduceSource = ApprovedScr("HLRCR-00002", "SWR-00002376", 0, RequirementChangeKind.Introduce,
                    "The software shall retain the selected waypoint.", projectId, releaseId, now, navigation);
                var second = FrozenBaseline("SW-00.20", projectId, releaseId, first.Id, introduceSource, now);
                setupDb.AddRange(introduceSource, second);
                await setupDb.SaveChangesAsync();
                await new RequirementBaselineMaterializer(setupDb, new VerificationImpactService(setupDb))
                    .MaterializeAsync(second.Id, "cm", now, default);

                var sourceRevision = await (from artifact in setupDb.Requirements
                                            where artifact.ProjectId == projectId && artifact.BaseNumber == "SWR-00002376"
                                            join revision in setupDb.RequirementRevisions on artifact.Id equals revision.ArtifactId
                                            where revision.Revision == 0
                                            select revision).SingleAsync();
                // This is a non-parent provenance relation. It is deliberately not used for the XOR parent
                // decision, but its carried exact identity must still receive #709 suspect attribution when
                // the referenced requirement gets a successor.
                setupDb.RequirementTraces.Add(new RequirementTraceLink(projectId, sourceRevision.Id, targetRevision.Id,
                    RequirementTraceType.DerivedFrom, "Source wording derives from the earlier requirement.", now));
                await setupDb.SaveChangesAsync();

                var reviseTarget = ApprovedScr("HLRCR-00003", "SWR-00002375", 1, RequirementChangeKind.Modify,
                    "The software shall sequence the selected waypoint.", projectId, releaseId, now, navigation,
                    JsonSerializer.Serialize(new[] { systemRevision.Id }));
                var third = FrozenBaseline("SW-00.30", projectId, releaseId, second.Id, reviseTarget, now);
                thirdBaselineId = third.Id;
                setupDb.AddRange(reviseTarget, third);
                await setupDb.SaveChangesAsync();
            }

            var interceptor = new ThrowAfterLifecycleSaveChangesInterceptor();
            var throwingOptions = new DbContextOptionsBuilder<AeroLinkDbContext>()
                .UseSqlite($"Data Source={path};Pooling=False")
                .AddInterceptors(interceptor)
                .Options;
            await using (var failedDb = new AeroLinkDbContext(throwingOptions))
            {
                var error = await Assert.ThrowsAsync<DomainException>(() =>
                    new RequirementBaselineMaterializer(failedDb, new VerificationImpactService(failedDb))
                        .MaterializeAsync(thirdBaselineId, "cm", now, default));
                Assert.Contains("induced after lifecycle save", error.Message);
            }

            await using (var verificationDb = new AeroLinkDbContext(options))
            {
                Assert.Null((await verificationDb.CandidateBaselines.SingleAsync(x => x.Id == thirdBaselineId)).RequirementsMaterializedAt);
                Assert.Empty(await verificationDb.BaselineRequirements.Where(x => x.BaselineId == thirdBaselineId).ToListAsync());
                Assert.Equal(1, await verificationDb.RequirementRevisions.CountAsync(x => x.ArtifactId == targetArtifactId));
                Assert.Equal(1, await verificationDb.RequirementTraces.CountAsync());
                Assert.Empty(await verificationDb.ExactLinkSuspectLifecycles.ToListAsync());
                Assert.Empty(await verificationDb.ExactLinkSuspectEvents.ToListAsync());
            }

            await using (var retryDb = new AeroLinkDbContext(options))
            {
                await new RequirementBaselineMaterializer(retryDb, new VerificationImpactService(retryDb))
                    .MaterializeAsync(thirdBaselineId, "cm", now, default);

                var currentTarget = await (from artifact in retryDb.Requirements
                                           where artifact.ProjectId == projectId && artifact.BaseNumber == "SWR-00002375"
                                           join revision in retryDb.RequirementRevisions on artifact.Id equals revision.ArtifactId
                                           where revision.Revision == 1
                                           select revision).SingleAsync();
                var carried = await retryDb.RequirementTraces.SingleAsync(x => x.TargetRevisionId == currentTarget.Id);
                var lifecycle = await retryDb.ExactLinkSuspectLifecycles.SingleAsync();
                Assert.Equal(lifecycle.Id, carried.ExactLinkSuspectLifecycleId);
                Assert.Equal(ExactLinkLifecycleState.Suspect, lifecycle.State);
                Assert.Equal(ExactLinkLifecycleCauseKind.InternalRequirementRevision, lifecycle.CauseKind);
                Assert.Equal(currentTarget.Id, lifecycle.CauseRequirementRevisionId);
                Assert.Single(await retryDb.ExactLinkSuspectEvents.ToListAsync());
                // The successor also carries its newly authored AllocatedFrom parent; the original
                // DerivedFrom row and its carried suspect row remain immutable alongside it.
                Assert.Equal(3, await retryDb.RequirementTraces.CountAsync());
            }

            await using (var idempotenceDb = new AeroLinkDbContext(options))
            {
                var retryError = await Assert.ThrowsAsync<DomainException>(() =>
                    new RequirementBaselineMaterializer(idempotenceDb, new VerificationImpactService(idempotenceDb))
                        .MaterializeAsync(thirdBaselineId, "cm", now, default));
                Assert.Contains("already materialized", retryError.Message);
                Assert.Single(await idempotenceDb.ExactLinkSuspectLifecycles.ToListAsync());
                Assert.Single(await idempotenceDb.ExactLinkSuspectEvents.ToListAsync());
                Assert.Equal(3, await idempotenceDb.RequirementTraces.CountAsync());
            }
        }
        finally { File.Delete(path); }
    }

    private sealed class ThrowAfterLifecycleSaveChangesInterceptor : SaveChangesInterceptor
    {
        private bool _armed;
        private bool _thrown;

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (!_armed && eventData.Context is not null
                && eventData.Context.ChangeTracker.Entries<ExactLinkSuspectLifecycle>()
                    .Any(entry => entry.State == EntityState.Added)
                && eventData.Context.ChangeTracker.Entries<ExactLinkSuspectEvent>()
                    .Any(entry => entry.State == EntityState.Added))
                _armed = true;
            return ValueTask.FromResult(result);
        }

        public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result,
            CancellationToken cancellationToken = default)
        {
            if (_armed && !_thrown)
            {
                _thrown = true;
                throw new DomainException("Materialization failure induced after lifecycle save.");
            }
            return ValueTask.FromResult(result);
        }
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
            attributesJson: proposedUpstreamRevisionIdsJson == "[]" ? "{\"derived\":true}" : "{}",
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
