using AeroLink.Domain.Baselines;
using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Programs;
using AeroLink.Domain.Requirements;
using AeroLink.Infrastructure.Persistence;
using AeroLink.Domain.Traceability;
using AeroLink.Domain.Hierarchy;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

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

            var firstScr = ApprovedScr("HLRCR-00001", "SWR-00002375", 0, RequirementChangeKind.Introduce, "Initial round robin requirement", project.Id, release.Id, now);
            var first = FrozenBaseline("SW-00.10", project.Id, release.Id, null, firstScr, now); db.AddRange(firstScr, first); await db.SaveChangesAsync();
            await new RequirementBaselineMaterializer(db, new VerificationImpactService(db)).MaterializeAsync(first.Id, "cm", now, default);

            var modifyScr = ApprovedScr("HLRCR-00002", "SWR-00002375", 1, RequirementChangeKind.Modify, "Clarified round robin requirement", project.Id, release.Id, now);
            var second = FrozenBaseline("SW-00.20", project.Id, release.Id, first.Id, modifyScr, now); db.AddRange(modifyScr, second); await db.SaveChangesAsync();
            await new RequirementBaselineMaterializer(db, new VerificationImpactService(db)).MaterializeAsync(second.Id, "cm", now, default);

            var retireScr = ApprovedScr("HLRCR-00003", "SWR-00002375", 2, RequirementChangeKind.Retire, "", project.Id, release.Id, now);
            var third = FrozenBaseline("SW-00.30", project.Id, release.Id, second.Id, retireScr, now); db.AddRange(retireScr, third); await db.SaveChangesAsync();
            await new RequirementBaselineMaterializer(db, new VerificationImpactService(db)).MaterializeAsync(third.Id, "cm", now, default);

            var artifact = await db.Requirements.SingleAsync(); var history = await db.RequirementRevisions.Where(x => x.ArtifactId == artifact.Id).OrderBy(x => x.Revision).ToListAsync();
            Assert.Equal([0, 1, 2], history.Select(x => x.Revision)); Assert.Equal(RequirementRevisionState.Retired, history[^1].State);
            Assert.Single(await db.BaselineRequirements.Where(x => x.BaselineId == first.Id).ToListAsync());
            var secondMember = await db.BaselineRequirements.SingleAsync(x => x.BaselineId == second.Id); Assert.Equal(history[1].Id, secondMember.RevisionId);
            Assert.Empty(await db.BaselineRequirements.Where(x => x.BaselineId == third.Id).ToListAsync());
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Proposed_one_and_many_parent_allocations_materialize_as_exact_links_across_supersession()
    {
        var path = Path.Combine(Path.GetTempPath(), $"aerolink-upstream-{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<AeroLinkDbContext>().UseSqlite($"Data Source={path};Pooling=False").Options;
        try
        {
            await using var db = new AeroLinkDbContext(options); await db.Database.EnsureCreatedAsync(); var now = DateTimeOffset.UtcNow;
            var program = new ProgramRecord("FMS", "FMST"); var project = new ProjectRecord(program.Id, "Software", "FMS Software"); var release = new SoftwareRelease(project.Id, "1.6", false);
            db.AddRange(program, project, release); await db.SaveChangesAsync();

            var system = new SystemChangeRequest("SRCR-00001", 0, project.Id, release.Id, "System parents", "P", "A", "S", "author", now);
            system.AddRequirementChange("author", "SYSR-000001", 0, RequirementLevel.System, RequirementChangeKind.Introduce, "The system shall navigate.", "Parent one.", "Test", now);
            system.AddRequirementChange("author", "SYSR-000002", 0, RequirementLevel.System, RequirementChangeKind.Introduce, "The system shall monitor position.", "Parent two.", "Test", now);
            system.SubmitForReview("author", [new("reviewer", "Reviewer")], now); system.ApproveActiveStage("reviewer", now);
            var first = FrozenBaseline("SYSBL-000001", project.Id, release.Id, null, system, now); db.AddRange(system, first); await db.SaveChangesAsync();
            await new RequirementBaselineMaterializer(db, new VerificationImpactService(db)).MaterializeAsync(first.Id, "cm", now, default);
            var parents = await (from artifact in db.Requirements.Where(x => x.Level == RequirementLevel.System)
                                 join revision in db.RequirementRevisions on artifact.Id equals revision.ArtifactId
                                 orderby artifact.BaseNumber select revision.Id).ToListAsync();

            var software = new SystemChangeRequest("HLRCR-00001", 0, project.Id, release.Id, "Software allocations", "P", "A", "S", "author", now, ChangeRequestType.Software, softwareLevel: RequirementLevel.HighLevel);
            software.AddRequirementChange("author", "HLR-000001", 0, RequirementLevel.HighLevel, RequirementChangeKind.Introduce, "The software shall navigate.", "One-to-one allocation.", "Test", now,
                proposedUpstreamRevisionIdsJson: JsonSerializer.Serialize(new[] { parents[0] }));
            software.AddRequirementChange("author", "HLR-000002", 0, RequirementLevel.HighLevel, RequirementChangeKind.Introduce, "The software shall monitor navigation integrity.", "Many-to-one allocation.", "Test", now,
                proposedUpstreamRevisionIdsJson: JsonSerializer.Serialize(parents));
            software.SubmitForReview("author", [new("reviewer", "Reviewer")], now); software.ApproveActiveStage("reviewer", now);
            var second = FrozenBaseline("SYSBL-000002", project.Id, release.Id, first.Id, software, now); db.AddRange(software, second); await db.SaveChangesAsync();
            await new RequirementBaselineMaterializer(db, new VerificationImpactService(db)).MaterializeAsync(second.Id, "cm", now, default);

            var links = await db.RequirementTraces.AsNoTracking().Where(x => x.Type == RequirementTraceType.AllocatedFrom).ToListAsync();
            Assert.Equal(3, links.Count);
            Assert.Equal(parents.Order(), links.Select(x => x.TargetRevisionId).Distinct().Order());
            var firstHlrRevision = await (from artifact in db.Requirements.Where(x => x.BaseNumber == "HLR-000001")
                                          join revision in db.RequirementRevisions on artifact.Id equals revision.ArtifactId
                                          select revision).SingleAsync();
            Assert.Contains(links, x => x.SourceRevisionId == firstHlrRevision.Id && x.TargetRevisionId == parents[0]);

            var revise = new SystemChangeRequest("HLRCR-00002", 0, project.Id, release.Id, "Supersede allocated HLR", "P", "A", "S", "author", now, ChangeRequestType.Software, softwareLevel: RequirementLevel.HighLevel);
            revise.AddRequirementChange("author", "HLR-000001", 1, RequirementLevel.HighLevel, RequirementChangeKind.Modify, "The software shall navigate with integrity monitoring.", "Reassessed allocation.", "Test", now,
                proposedUpstreamRevisionIdsJson: JsonSerializer.Serialize(new[] { parents[0] }));
            revise.SubmitForReview("author", [new("reviewer", "Reviewer")], now); revise.ApproveActiveStage("reviewer", now);
            var third = FrozenBaseline("SYSBL-000003", project.Id, release.Id, second.Id, revise, now); db.AddRange(revise, third); await db.SaveChangesAsync();
            await new RequirementBaselineMaterializer(db, new VerificationImpactService(db)).MaterializeAsync(third.Id, "cm", now, default);

            var hlrHistory = await db.RequirementRevisions.Where(x => x.ArtifactId == firstHlrRevision.ArtifactId).OrderBy(x => x.Revision).ToListAsync();
            Assert.Equal(new[] { 0, 1 }, hlrHistory.Select(x => x.Revision));
            Assert.Contains(await db.RequirementTraces.AsNoTracking().ToListAsync(), x => x.SourceRevisionId == hlrHistory[0].Id && x.TargetRevisionId == parents[0]);
            Assert.Contains(await db.RequirementTraces.AsNoTracking().ToListAsync(), x => x.SourceRevisionId == hlrHistory[1].Id && x.TargetRevisionId == parents[0]);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Configured_child_to_parent_allocated_trace_materializes_successfully()
    {
        var path = Path.Combine(Path.GetTempPath(), $"aerolink-configured-materialization-{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<AeroLinkDbContext>().UseSqlite($"Data Source={path};Pooling=False").Options;
        try
        {
            await using var db = new AeroLinkDbContext(options); await db.Database.EnsureCreatedAsync(); var now = DateTimeOffset.UtcNow;
            var program = new ProgramRecord("Configured", "CFG");
            var projectRecord = new ProjectRecord(program.Id, "Configured Project", "Configured Software");
            var release = new SoftwareRelease(projectRecord.Id, "1.0", false);
            db.AddRange(program, projectRecord, release); await db.SaveChangesAsync();
            var policy = ConfiguredSystemLowPolicy();

            var system = new SystemChangeRequest("SRCR-00001", 0, projectRecord.Id, release.Id, "System", "P", "A", "S", "author", now,
                ladderPolicy: policy);
            system.AddRequirementChange("author", "SYSR-000001", 0, RequirementLevel.System, RequirementChangeKind.Introduce,
                "The system shall navigate.", "Rationale", "Test", now, ladderPolicy: policy);
            system.SubmitForReview("author", [new("reviewer", "Reviewer")], now); system.ApproveActiveStage("reviewer", now);
            var first = FrozenBaseline("CFG-000001", projectRecord.Id, release.Id, null, system, now);
            db.AddRange(system, first); await db.SaveChangesAsync();
            await new RequirementBaselineMaterializer(db, new VerificationImpactService(db), policy: policy)
                .MaterializeAsync(first.Id, "cm", now, default);
            var parentRevision = await (from artifact in db.Requirements where artifact.ProjectId == projectRecord.Id && artifact.Level == RequirementLevel.System
                                        join revision in db.RequirementRevisions on artifact.Id equals revision.ArtifactId
                                        select revision.Id).SingleAsync();

            var low = new SystemChangeRequest("LLRCR-00001", 0, projectRecord.Id, release.Id, "Low", "P", "A", "S", "author", now,
                ChangeRequestType.Software, softwareLevel: RequirementLevel.LowLevel, ladderPolicy: policy);
            low.AddRequirementChange("author", "LLR-000001", 0, RequirementLevel.LowLevel, RequirementChangeKind.Introduce,
                "The implementation shall navigate.", "Rationale", "Test", now,
                proposedUpstreamRevisionIdsJson: JsonSerializer.Serialize(new[] { parentRevision }), ladderPolicy: policy);
            low.SubmitForReview("author", [new("reviewer", "Reviewer")], now); low.ApproveActiveStage("reviewer", now);
            var second = FrozenBaseline("CFG-000002", projectRecord.Id, release.Id, first.Id, low, now);
            db.AddRange(low, second); await db.SaveChangesAsync();
            await new RequirementBaselineMaterializer(db, new VerificationImpactService(db), policy: policy)
                .MaterializeAsync(second.Id, "cm", now, default);

            var link = await db.RequirementTraces.SingleAsync(x => x.Type == RequirementTraceType.AllocatedFrom);
            var childArtifact = await db.Requirements.SingleAsync(x => x.BaseNumber == "LLR-000001");
            var childRevision = await db.RequirementRevisions.SingleAsync(x => x.ArtifactId == childArtifact.Id);
            Assert.Equal(childRevision.Id, link.SourceRevisionId);
            Assert.Equal(parentRevision, link.TargetRevisionId);
        }
        finally { File.Delete(path); }
    }

    private static SystemChangeRequest ApprovedScr(string scrNumber, string requirementNumber, int revision, RequirementChangeKind kind, string statement, Guid projectId, Guid releaseId, DateTimeOffset now)
    {
        var scr = new SystemChangeRequest(scrNumber, 0, projectId, releaseId, kind.ToString(), "P", "A", "S", "author", now, ChangeRequestType.Software, softwareLevel: RequirementLevel.HighLevel);
        scr.AddRequirementChange("author", requirementNumber, revision, RequirementLevel.HighLevel, kind, statement, "Rationale", "Test", now);
        scr.SubmitForReview("author", [new("reviewer", "Reviewer")], now); scr.ApproveActiveStage("reviewer", now); return scr;
    }
    private static CandidateBaseline FrozenBaseline(string number, Guid projectId, Guid releaseId, Guid? predecessor, SystemChangeRequest scr, DateTimeOffset now)
    {
        var baseline = new CandidateBaseline(number, 0, projectId, releaseId, predecessor, number, "cm", now); baseline.Select(scr, "cm", now); baseline.Freeze("cm", now); return baseline;
    }

    private static ILadderPolicy ConfiguredSystemLowPolicy()
    {
        var configuration = ProjectLadderConfiguration.CreateDraft(Guid.NewGuid(), DateTimeOffset.UtcNow);
        var now = DateTimeOffset.UtcNow;
        var system = new ProjectLadderStep(configuration.Id, configuration.ProjectId, RequirementLevel.System, 1,
            LegacyLadderPolicy.Instance.Definition(RequirementLevel.System).Capabilities, now);
        var low = new ProjectLadderStep(configuration.Id, configuration.ProjectId, RequirementLevel.LowLevel, 2,
            LegacyLadderPolicy.Instance.Definition(RequirementLevel.LowLevel).Capabilities, now);
        configuration.Steps.Add(system); configuration.Steps.Add(low);
        configuration.AllowedUpstream.Add(new ProjectLadderAllowedUpstream(configuration.Id, configuration.ProjectId,
            system.Id, low.Id, now));
        return new ResolvedProjectLadderPolicy(ProjectLadderResolver.Resolve(configuration));
    }
}
