using AeroLink.Domain.Baselines;
using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Common;
using AeroLink.Domain.Programs;
using AeroLink.Domain.Releases;
using AeroLink.Domain.Requirements;
using AeroLink.Domain.Traceability;
using Microsoft.EntityFrameworkCore;
using AeroLink.Infrastructure.Persistence;

namespace AeroLink.Infrastructure.Tests;

public sealed class ExactLinkLifecyclePersistenceTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 22, 4, 30, 0, TimeSpan.Zero);

    [Fact]
    public async Task Lifecycle_projection_and_events_persist_and_mutate_through_the_service()
    {
        var path = Path.Combine(Path.GetTempPath(), $"aerolink-exact-link-{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<AeroLinkDbContext>().UseSqlite($"Data Source={path};Pooling=False").Options;
        try
        {
            Guid linkId;
            await using (var setup = new AeroLinkDbContext(options))
            {
                await setup.Database.EnsureCreatedAsync();
                var program = new ProgramRecord("Exact link program", "ELP");
                var project = new ProjectRecord(program.Id, "Exact link project", "Exact link project");
                var release = new SoftwareRelease(project.Id, "1.0", false);
                var baseline = new CandidateBaseline("BL-00000001", 0, project.Id, release.Id, null, "Baseline", "author", Now);
                var request = new SystemChangeRequest("SRCR-00001", 0, project.Id, release.Id, "Trace cause", "P", "A", "S", "author", Now);
                var sourceArtifact = new RequirementArtifact(project.Id, "HLR-000001", RequirementLevel.HighLevel, Now);
                var targetArtifact = new RequirementArtifact(project.Id, "SYSR-000002", RequirementLevel.System, Now);
                var source = new RequirementRevision(sourceArtifact.Id, 0, "Child", "R", "Test", RequirementRevisionState.Active, request.Id, baseline.Id, Now,
                    parentKind: RequirementParentKind.Derived, derivedRationale: "This lifecycle fixture has no upstream requirement allocation.");
                var target = new RequirementRevision(targetArtifact.Id, 1, "Parent changed", "R", "Test", RequirementRevisionState.Active, request.Id, baseline.Id, Now);
                var link = new RequirementTraceLink(project.Id, source.Id, target.Id, RequirementTraceType.DerivedFrom, "Child derives from parent.", Now);
                var lifecycle = ExactLinkSuspectLifecycle.Raise(project.Id, ExactLinkKind.RequirementTrace, link.Id,
                    ExactLinkLifecycleCauseKind.InternalRequirementRevision, target.Id, null, "author", "Parent wording changed.", Now);
                link.AttachExactLinkLifecycle(lifecycle.Id);
                setup.AddRange(program, project, release, baseline, request, sourceArtifact, targetArtifact, source, target, link);
                setup.BaselineRequirements.AddRange(new BaselineRequirementSelection(baseline.Id, sourceArtifact.Id, source.Id), new BaselineRequirementSelection(baseline.Id, targetArtifact.Id, target.Id));
                setup.ExactLinkSuspectLifecycles.Add(lifecycle); setup.ExactLinkSuspectEvents.AddRange(lifecycle.Events);
                await setup.SaveChangesAsync();
                linkId = link.Id;
            }

            await using (var mutate = new AeroLinkDbContext(options))
            {
                var lifecycle = await new ExactLinkLifecycleService(mutate).AcknowledgeAsync(linkId, "reviewer", "Assessment started.", Now.AddMinutes(1), default);
                Assert.Equal(ExactLinkLifecycleState.Acknowledged, lifecycle.State);
            }
            await using (var assert = new AeroLinkDbContext(options))
            {
                var lifecycle = await assert.ExactLinkSuspectLifecycles.AsNoTracking().SingleAsync();
                var persistedLink = await assert.RequirementTraces.AsNoTracking().SingleAsync();
                var events = (await assert.ExactLinkSuspectEvents.AsNoTracking().ToListAsync()).OrderBy(x => x.OccurredAt).ToList();
                Assert.Equal(ExactLinkLifecycleState.Acknowledged, lifecycle.State);
                Assert.Equal(lifecycle.Id, persistedLink.ExactLinkSuspectLifecycleId);
                Assert.Equal("reviewer", lifecycle.AcknowledgedBy);
                Assert.Equal([ExactLinkLifecycleEventType.Raised, ExactLinkLifecycleEventType.Acknowledged], events.Select(x => x.EventType));
            }
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task Lifecycle_mutation_is_refused_when_both_exact_endpoints_are_in_review()
    {
        var path = Path.Combine(Path.GetTempPath(), $"aerolink-exact-link-freeze-{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<AeroLinkDbContext>().UseSqlite($"Data Source={path};Pooling=False").Options;
        try
        {
            Guid linkId;
            await using (var setup = new AeroLinkDbContext(options))
            {
                await setup.Database.EnsureCreatedAsync();
                var program = new ProgramRecord("Frozen exact link program", "FEL");
                var project = new ProjectRecord(program.Id, "Frozen exact link project", "Frozen exact link project");
                var release = new SoftwareRelease(project.Id, "1.0", false);
                var baseline = new CandidateBaseline("BL-00000001", 0, project.Id, release.Id, null, "Baseline", "author", Now);
                var request = new SystemChangeRequest("SRCR-00001", 0, project.Id, release.Id, "Trace cause", "P", "A", "S", "author", Now);
                var sourceArtifact = new RequirementArtifact(project.Id, "HLR-000001", RequirementLevel.HighLevel, Now);
                var targetArtifact = new RequirementArtifact(project.Id, "SYSR-000002", RequirementLevel.System, Now);
                var source = new RequirementRevision(sourceArtifact.Id, 0, "Child", "R", "Test", RequirementRevisionState.Active, request.Id, baseline.Id, Now,
                    parentKind: RequirementParentKind.Derived, derivedRationale: "This lifecycle fixture has no upstream requirement allocation.");
                var target = new RequirementRevision(targetArtifact.Id, 1, "Parent changed", "R", "Test", RequirementRevisionState.Active, request.Id, baseline.Id, Now);
                var link = new RequirementTraceLink(project.Id, source.Id, target.Id, RequirementTraceType.DerivedFrom, "Child derives from parent.", Now);
                var lifecycle = ExactLinkSuspectLifecycle.Raise(project.Id, ExactLinkKind.RequirementTrace, link.Id,
                    ExactLinkLifecycleCauseKind.InternalRequirementRevision, target.Id, null, "author", "Parent wording changed.", Now);
                link.AttachExactLinkLifecycle(lifecycle.Id);
                var campaign = new ReleaseCampaign(project.Id, release.Id, baseline.Id, "Campaign", "author", Now);
                campaign.StartVerification("author", Now.AddMinutes(1));
                campaign.BeginReleaseReview("author", [("reviewer", "Reviewer")], new string('a', 64), Now.AddMinutes(2));
                setup.AddRange(program, project, release, baseline, request, sourceArtifact, targetArtifact, source, target, link, campaign);
                setup.BaselineRequirements.AddRange(new BaselineRequirementSelection(baseline.Id, sourceArtifact.Id, source.Id), new BaselineRequirementSelection(baseline.Id, targetArtifact.Id, target.Id));
                setup.ExactLinkSuspectLifecycles.Add(lifecycle); setup.ExactLinkSuspectEvents.AddRange(lifecycle.Events);
                await setup.SaveChangesAsync(); linkId = link.Id;
            }
            await using var mutate = new AeroLinkDbContext(options);
            await Assert.ThrowsAsync<DomainException>(() => new ExactLinkLifecycleService(mutate)
                .AcknowledgeAsync(linkId, "reviewer", "Assessment started.", Now.AddMinutes(3), default));
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task Review_manifest_hash_includes_link_lifecycle_projection_and_events()
    {
        var path = Path.Combine(Path.GetTempPath(), $"aerolink-exact-link-manifest-{Guid.NewGuid():N}.db");
        var evidencePath = Path.Combine(Path.GetTempPath(), $"aerolink-exact-link-evidence-{Guid.NewGuid():N}");
        var options = new DbContextOptionsBuilder<AeroLinkDbContext>().UseSqlite($"Data Source={path};Pooling=False").Options;
        try
        {
            Guid campaignId, linkId;
            await using (var setup = new AeroLinkDbContext(options))
            {
                await setup.Database.EnsureCreatedAsync();
                var program = new ProgramRecord("Manifest exact link program", "MEL");
                var project = new ProjectRecord(program.Id, "Manifest exact link project", "Manifest exact link project");
                var release = new SoftwareRelease(project.Id, "1.0", false);
                var baseline = new CandidateBaseline("BL-00000001", 0, project.Id, release.Id, null, "Baseline", "author", Now);
                var request = new SystemChangeRequest("SRCR-00001", 0, project.Id, release.Id, "Trace cause", "P", "A", "S", "author", Now);
                var sourceArtifact = new RequirementArtifact(project.Id, "HLR-000001", RequirementLevel.HighLevel, Now);
                var targetArtifact = new RequirementArtifact(project.Id, "SYSR-000002", RequirementLevel.System, Now);
                var source = new RequirementRevision(sourceArtifact.Id, 0, "Child", "R", "Test", RequirementRevisionState.Active, request.Id, baseline.Id, Now,
                    parentKind: RequirementParentKind.Derived, derivedRationale: "This lifecycle fixture has no upstream requirement allocation.");
                var target = new RequirementRevision(targetArtifact.Id, 0, "Parent", "R", "Test", RequirementRevisionState.Active, request.Id, baseline.Id, Now);
                var link = new RequirementTraceLink(project.Id, source.Id, target.Id, RequirementTraceType.DerivedFrom, "Child derives from parent.", Now);
                var lifecycle = ExactLinkSuspectLifecycle.Raise(project.Id, ExactLinkKind.RequirementTrace, link.Id,
                    ExactLinkLifecycleCauseKind.InternalRequirementRevision, target.Id, null, "author", "Parent wording changed.", Now);
                link.AttachExactLinkLifecycle(lifecycle.Id);
                var campaign = new ReleaseCampaign(project.Id, release.Id, baseline.Id, "Campaign", "author", Now);
                var build = new SoftwareBuild(project.Id, release.Id, baseline.Id, "MEL-1", "Build", "author", Now);
                setup.AddRange(program, project, release, baseline, request, sourceArtifact, targetArtifact, source, target, link, campaign, build);
                setup.BaselineRequirements.AddRange(new BaselineRequirementSelection(baseline.Id, sourceArtifact.Id, source.Id), new BaselineRequirementSelection(baseline.Id, targetArtifact.Id, target.Id));
                setup.ExactLinkSuspectLifecycles.Add(lifecycle); setup.ExactLinkSuspectEvents.AddRange(lifecycle.Events);
                await setup.SaveChangesAsync();
                campaign.SelectVerificationBuild(build.Id, "author", Now);
                await setup.SaveChangesAsync();
                await setup.CandidateBaselines.Where(x => x.Id == baseline.Id).ExecuteUpdateAsync(update => update
                    .SetProperty(x => x.State, CandidateBaselineState.Frozen)
                    .SetProperty(x => x.RequirementsMaterializedAt, Now)
                    .SetProperty(x => x.RequirementsHash, "test"));
                campaignId = campaign.Id; linkId = link.Id;
            }

            string before;
            await using (var read = new AeroLinkDbContext(options))
                before = await new ReleaseExecutionService(read, new EvidenceFileStore(evidencePath)).ComputeReviewManifestHashAsync(campaignId, default);
            await using (var acknowledge = new AeroLinkDbContext(options))
                await new ExactLinkLifecycleService(acknowledge).AcknowledgeAsync(linkId, "reviewer", "Assessment started.", Now.AddMinutes(1), default);
            await using var afterRead = new AeroLinkDbContext(options);
            var after = await new ReleaseExecutionService(afterRead, new EvidenceFileStore(evidencePath)).ComputeReviewManifestHashAsync(campaignId, default);
            Assert.NotEqual(before, after);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (File.Exists(path)) File.Delete(path);
            if (Directory.Exists(evidencePath)) Directory.Delete(evidencePath, true);
        }
    }

    [Fact]
    public async Task Dematerializing_a_candidate_removes_its_transient_trace_lifecycle_aggregate()
    {
        var path = Path.Combine(Path.GetTempPath(), $"aerolink-exact-link-dematerialize-{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<AeroLinkDbContext>().UseSqlite($"Data Source={path};Pooling=False").Options;
        try
        {
            await using var db = new AeroLinkDbContext(options);
            await db.Database.EnsureCreatedAsync();
            var program = new ProgramRecord("Dematerialize exact link program", "DEL");
            var project = new ProjectRecord(program.Id, "Dematerialize exact link project", "Dematerialize exact link project");
            var release = new SoftwareRelease(project.Id, "1.0", false);
            var baseline = new CandidateBaseline("BL-00000001", 0, project.Id, release.Id, null, "Candidate", "author", Now);
            var request = new SystemChangeRequest("SRCR-00001", 0, project.Id, release.Id, "Transient trace", "P", "A", "S", "author", Now);
            var sourceArtifact = new RequirementArtifact(project.Id, "HLR-000001", RequirementLevel.HighLevel, Now);
            var targetArtifact = new RequirementArtifact(project.Id, "SYSR-000002", RequirementLevel.System, Now);
            var source = new RequirementRevision(sourceArtifact.Id, 0, "Child", "R", "Test", RequirementRevisionState.Active, request.Id, baseline.Id, Now,
                parentKind: RequirementParentKind.Derived, derivedRationale: "This lifecycle fixture has no upstream requirement allocation.");
            var target = new RequirementRevision(targetArtifact.Id, 0, "Parent", "R", "Test", RequirementRevisionState.Active, request.Id, baseline.Id, Now);
            var link = new RequirementTraceLink(project.Id, source.Id, target.Id, RequirementTraceType.DerivedFrom, "Transient link.", Now);
            var lifecycle = ExactLinkSuspectLifecycle.Raise(project.Id, ExactLinkKind.RequirementTrace, link.Id,
                ExactLinkLifecycleCauseKind.InternalRequirementRevision, target.Id, null, "author", "Parent changed.", Now);
            link.AttachExactLinkLifecycle(lifecycle.Id);
            db.AddRange(program, project, release, baseline, request, sourceArtifact, targetArtifact, source, target, link);
            db.BaselineRequirements.AddRange(new BaselineRequirementSelection(baseline.Id, sourceArtifact.Id, source.Id), new BaselineRequirementSelection(baseline.Id, targetArtifact.Id, target.Id));
            db.ExactLinkSuspectLifecycles.Add(lifecycle); db.ExactLinkSuspectEvents.AddRange(lifecycle.Events);
            await db.SaveChangesAsync();

            await new RequirementBaselineDematerializer(db, new VerificationImpactService(db))
                .DematerializeAsync(baseline.Id, "author", baseline.DisplayNumber, Now.AddMinutes(1), default);
            await db.SaveChangesAsync();
            Assert.Empty(await db.RequirementTraces.AsNoTracking().ToListAsync());
            Assert.Empty(await db.ExactLinkSuspectLifecycles.AsNoTracking().ToListAsync());
            Assert.Empty(await db.ExactLinkSuspectEvents.AsNoTracking().ToListAsync());
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
