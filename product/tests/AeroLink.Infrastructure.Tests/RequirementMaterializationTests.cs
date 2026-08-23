using System.IO.Compression;
using AeroLink.Domain.Baselines;
using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Common;
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
    public async Task Materialized_requirement_classification_is_persisted_and_published_deterministically()
    {
        var path = Path.Combine(Path.GetTempPath(), $"aerolink-req-output-{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<AeroLinkDbContext>().UseSqlite($"Data Source={path};Pooling=False").Options;
        var evidencePath = Path.Combine(Path.GetTempPath(), $"aerolink-req-output-evidence-{Guid.NewGuid():N}");
        try
        {
            await using var db = new AeroLinkDbContext(options); await db.Database.EnsureCreatedAsync();
            var now = DateTimeOffset.UtcNow;
            var program = new ProgramRecord("Requirement publication", "RPD");
            var project = new ProjectRecord(program.Id, "Requirement publication project", "Qualification product");
            var release = new SoftwareRelease(project.Id, "9.0", false);
            var scr = ApprovedScr("HLRCR-73800", "HLR-738000", 0, RequirementChangeKind.Introduce,
                "The software shall publish its exact parent decision.", project.Id, release.Id, now);
            var baseline = FrozenBaseline("SW-90.00", project.Id, release.Id, null, scr, now);
            db.AddRange(program, project, release, scr, baseline);
            await db.SaveChangesAsync();
            await new RequirementBaselineMaterializer(db, new VerificationImpactService(db))
                .MaterializeAsync(baseline.Id, "cm", now, default);

            var revision = await db.RequirementRevisions.SingleAsync();
            Assert.Equal(RequirementParentKind.Derived, revision.ParentKind);
            Assert.Equal("Rationale", revision.DerivedRationale);
            Assert.Equal("[]", revision.ParentRevisionIdsJson);

            var document = new ControlledDocument(project.Id, release.Id, baseline.Id,
                ControlledDocumentType.SwrdHighLevel, "HLRD-738000", "High-Level Requirements", 0,
                new string('a', 64), 1, now);
            db.ControlledDocuments.Add(document);
            await db.SaveChangesAsync();
            var generator = new ControlledOutputGenerator(db,
                new RichContentPublisher(db, new EvidenceFileStore(evidencePath)));
            var first = Assert.IsType<GeneratedOutput>(await generator.GenerateAsync(document.Id, "docx", default));
            var second = Assert.IsType<GeneratedOutput>(await generator.GenerateAsync(document.Id, "docx", default));
            Assert.Equal(first.Content, second.Content);
            using var archive = new ZipArchive(new MemoryStream(first.Content), ZipArchiveMode.Read);
            using var reader = new StreamReader(archive.GetEntry("word/document.xml")!.Open());
            var xml = await reader.ReadToEndAsync();
            Assert.Contains("Parent classification", xml);
            Assert.Contains("Derived", xml);
            Assert.Contains("Derived rationale", xml);
            Assert.Contains("Rationale", xml);
        }
        finally
        {
            File.Delete(path);
            if (Directory.Exists(evidencePath)) Directory.Delete(evidencePath, recursive: true);
        }
    }

    [Fact]
    public async Task Ordinary_materialization_refuses_an_unmaterialized_v1_non_root_but_preserves_materialized_v1_history()
    {
        var path = Path.Combine(Path.GetTempPath(), $"aerolink-v1-materialization-{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<AeroLinkDbContext>().UseSqlite($"Data Source={path};Pooling=False").Options;
        try
        {
            await using var db = new AeroLinkDbContext(options); await db.Database.EnsureCreatedAsync();
            var now = DateTimeOffset.UtcNow;
            var program = new ProgramRecord("Legacy v1 materialization", "LVM");
            var project = new ProjectRecord(program.Id, "Legacy v1 project", "Legacy v1 software");
            var release = new SoftwareRelease(project.Id, "1.0", false);
            db.AddRange(program, project, release); await db.SaveChangesAsync();

            var historical = LegacyHighLevelScr("HLRCR-00901", "HLR-00901", project.Id, release.Id, now);
            var historicalBaseline = FrozenBaseline("LVM-000001", project.Id, release.Id, null, historical, now);
            db.AddRange(historical, historicalBaseline); await db.SaveChangesAsync();
            await new RequirementBaselineMaterializer(db, new VerificationImpactService(db))
                .MaterializeLegacyHistoricalSeedAsync(historicalBaseline.Id, "cm", now, default);
            var historicalRevision = await db.RequirementRevisions.AsNoTracking().SingleAsync();
            var historicalHash = historical.ReviewCycles.Single().SnapshotHash;
            Assert.Equal(RequirementParentKind.Unspecified, historicalRevision.ParentKind);
            Assert.Equal("[]", historicalRevision.ParentRevisionIdsJson);

            // This is the forward path after migration: an approved v1 row selected into a fresh baseline
            // must not use the historical Unspecified exception to create a new active non-root revision.
            var pending = LegacyHighLevelScr("HLRCR-00902", "HLR-00902", project.Id, release.Id, now);
            var pendingBaseline = FrozenBaseline("LVM-000002", project.Id, release.Id,
                historicalBaseline.Id, pending, now);
            db.AddRange(pending, pendingBaseline); await db.SaveChangesAsync();
            var exception = await Assert.ThrowsAsync<DomainException>(() =>
                new RequirementBaselineMaterializer(db, new VerificationImpactService(db))
                    .MaterializeAsync(pendingBaseline.Id, "cm", now, default));
            Assert.Contains("parent", exception.Message, StringComparison.OrdinalIgnoreCase);

            db.ChangeTracker.Clear();
            var unchanged = await db.RequirementRevisions.AsNoTracking().SingleAsync(x => x.Id == historicalRevision.Id);
            Assert.Equal(RequirementParentKind.Unspecified, unchanged.ParentKind);
            Assert.Equal("[]", unchanged.ParentRevisionIdsJson);
            Assert.Equal(historicalHash, (await db.SystemChangeRequests.AsNoTracking().Include(x => x.ReviewCycles)
                .SingleAsync(x => x.Id == historical.Id)).ReviewCycles.Single().SnapshotHash);
        }
        finally { File.Delete(path); }

        static SystemChangeRequest LegacyHighLevelScr(string scrNumber, string requirementNumber,
            Guid projectId, Guid releaseId, DateTimeOffset now)
        {
            var scr = new SystemChangeRequest(scrNumber, 0, projectId, releaseId, "Legacy requirement",
                "Problem", "Analysis", "Solution", "author", now, ChangeRequestType.Software,
                softwareLevel: RequirementLevel.HighLevel);
            scr.AddRequirementChange("author", requirementNumber, 0, RequirementLevel.HighLevel,
                RequirementChangeKind.Introduce, "The software shall retain historical wording.",
                "Historical rationale", "Test", now);
            scr.MarkAsLegacyHistoricalPackage("author", now);
            scr.SubmitForReview("author", [new("reviewer", "Reviewer")], now);
            scr.ApproveActiveStage("reviewer", now);
            return scr;
        }
    }

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

            // A new upstream revision carries each direct predecessor relationship to the exact new target
            // and raises evidence only for the new exact link. The released predecessor link stays immutable.
            var parentRevision = new SystemChangeRequest("SRCR-00002", 0, project.Id, release.Id,
                "Supersede system parent", "P", "A", "S", "author", now);
            parentRevision.AddRequirementChange("author", "SYSR-000001", 1, RequirementLevel.System,
                RequirementChangeKind.Modify, "The system shall navigate with integrity monitoring.",
                "Updated upstream wording.", "Test", now);
            parentRevision.SubmitForReview("author", [new("reviewer", "Reviewer")], now);
            parentRevision.ApproveActiveStage("reviewer", now);
            var fourth = FrozenBaseline("SYSBL-000004", project.Id, release.Id, third.Id, parentRevision, now);
            db.AddRange(parentRevision, fourth); await db.SaveChangesAsync();
            await new RequirementBaselineMaterializer(db, new VerificationImpactService(db))
                .MaterializeAsync(fourth.Id, "cm", now, default);

            var currentParent = await (from artifact in db.Requirements
                                       where artifact.BaseNumber == "SYSR-000001"
                                       join revision in db.RequirementRevisions on artifact.Id equals revision.ArtifactId
                                       where revision.Revision == 1
                                       select revision).SingleAsync();
            var carried = await db.RequirementTraces.AsNoTracking()
                .Where(x => x.TargetRevisionId == currentParent.Id).ToListAsync();
            Assert.NotEmpty(carried);
            Assert.All(carried, x => Assert.NotEqual(Guid.Empty, x.ExactLinkSuspectLifecycleId));
            var carriedLifecycles = await db.ExactLinkSuspectLifecycles.AsNoTracking().ToListAsync();
            Assert.Equal(carried.Count, carriedLifecycles.Count);
            Assert.All(carriedLifecycles, lifecycle =>
            {
                Assert.Equal(ExactLinkLifecycleState.Suspect, lifecycle.State);
                Assert.Equal(ExactLinkLifecycleCauseKind.InternalRequirementRevision, lifecycle.CauseKind);
                Assert.Equal(currentParent.Id, lifecycle.CauseRequirementRevisionId);
            });
            Assert.Equal(carried.Count, await db.ExactLinkSuspectEvents.AsNoTracking().CountAsync());
            Assert.Contains(await db.RequirementTraces.AsNoTracking().ToListAsync(),
                x => x.SourceRevisionId == hlrHistory[1].Id && x.TargetRevisionId == parents[0]
                    && x.ExactLinkSuspectLifecycleId == null);
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

    [Fact]
    public async Task Configured_low_level_can_allocate_to_the_high_level_alternative_and_persist_exact_selection()
    {
        var path = Path.Combine(Path.GetTempPath(), $"aerolink-configured-alternative-{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<AeroLinkDbContext>().UseSqlite($"Data Source={path};Pooling=False").Options;
        try
        {
            await using var db = new AeroLinkDbContext(options); await db.Database.EnsureCreatedAsync();
            var now = DateTimeOffset.UtcNow;
            var program = new ProgramRecord("Configured alternatives", "CFA");
            var project = new ProjectRecord(program.Id, "Configured alternatives project", "Configured software");
            var release = new SoftwareRelease(project.Id, "1.0", false);
            db.AddRange(program, project, release); await db.SaveChangesAsync();
            var policy = ConfiguredSystemHighLowPolicy(project.Id);

            var system = new SystemChangeRequest("SRCR-00001", 0, project.Id, release.Id, "System", "P", "A", "S", "author", now,
                ladderPolicy: policy);
            system.AddRequirementChange("author", "SYSR-000001", 0, RequirementLevel.System, RequirementChangeKind.Introduce,
                "The system shall navigate.", "Rationale", "Test", now, ladderPolicy: policy);
            system.SubmitForReview("author", [new("reviewer", "Reviewer")], now); system.ApproveActiveStage("reviewer", now);
            var first = FrozenBaseline("CFA-000001", project.Id, release.Id, null, system, now);
            db.AddRange(system, first); await db.SaveChangesAsync();
            await new RequirementBaselineMaterializer(db, new VerificationImpactService(db), policy: policy)
                .MaterializeAsync(first.Id, "cm", now, default);
            var systemRevision = await (from artifact in db.Requirements
                                        where artifact.ProjectId == project.Id && artifact.Level == RequirementLevel.System
                                        join revision in db.RequirementRevisions on artifact.Id equals revision.ArtifactId
                                        select revision).SingleAsync();

            var high = new SystemChangeRequest("HLRCR-00001", 0, project.Id, release.Id, "High level", "P", "A", "S", "author", now,
                ChangeRequestType.Software, softwareLevel: RequirementLevel.HighLevel, ladderPolicy: policy);
            high.AddRequirementChange("author", "HLR-000001", 0, RequirementLevel.HighLevel, RequirementChangeKind.Introduce,
                "The software shall navigate.", "Rationale", "Test", now,
                proposedUpstreamRevisionIdsJson: JsonSerializer.Serialize(new[] { systemRevision.Id }), ladderPolicy: policy);
            high.SubmitForReview("author", [new("reviewer", "Reviewer")], now); high.ApproveActiveStage("reviewer", now);
            var second = FrozenBaseline("CFA-000002", project.Id, release.Id, first.Id, high, now);
            db.AddRange(high, second); await db.SaveChangesAsync();
            await new RequirementBaselineMaterializer(db, new VerificationImpactService(db), policy: policy)
                .MaterializeAsync(second.Id, "cm", now, default);
            var highRevision = await (from artifact in db.Requirements
                                      where artifact.BaseNumber == "HLR-000001"
                                      join revision in db.RequirementRevisions on artifact.Id equals revision.ArtifactId
                                      select revision).SingleAsync();

            // The configured LowLevel topology permits System or HighLevel. Exercise the HighLevel
            // alternative through the real materializer and save-boundary trace creation.
            var low = new SystemChangeRequest("LLRCR-00001", 0, project.Id, release.Id, "Low level", "P", "A", "S", "author", now,
                ChangeRequestType.Software, softwareLevel: RequirementLevel.LowLevel, ladderPolicy: policy);
            low.AddRequirementChange("author", "LLR-000001", 0, RequirementLevel.LowLevel, RequirementChangeKind.Introduce,
                "The implementation shall navigate.", "Rationale", "Test", now,
                proposedUpstreamRevisionIdsJson: JsonSerializer.Serialize(new[] { highRevision.Id }), ladderPolicy: policy);
            low.SubmitForReview("author", [new("reviewer", "Reviewer")], now); low.ApproveActiveStage("reviewer", now);
            var third = FrozenBaseline("CFA-000003", project.Id, release.Id, second.Id, low, now);
            db.AddRange(low, third); await db.SaveChangesAsync();
            await new RequirementBaselineMaterializer(db, new VerificationImpactService(db), policy: policy)
                .MaterializeAsync(third.Id, "cm", now, default);

            var lowRevision = await (from artifact in db.Requirements
                                     where artifact.BaseNumber == "LLR-000001"
                                     join revision in db.RequirementRevisions on artifact.Id equals revision.ArtifactId
                                     select revision).SingleAsync();
            var link = await db.RequirementTraces.AsNoTracking()
                .SingleAsync(x => x.Type == RequirementTraceType.AllocatedFrom && x.SourceRevisionId == lowRevision.Id);
            Assert.Equal(highRevision.Id, link.TargetRevisionId);
            Assert.Equal(RequirementParentKind.Allocated, lowRevision.ParentKind);
            Assert.Equal(JsonSerializer.Serialize(new[] { highRevision.Id }), lowRevision.ParentRevisionIdsJson);
        }
        finally { File.Delete(path); }
    }

    private static SystemChangeRequest ApprovedScr(string scrNumber, string requirementNumber, int revision, RequirementChangeKind kind, string statement, Guid projectId, Guid releaseId, DateTimeOffset now)
    {
        var scr = new SystemChangeRequest(scrNumber, 0, projectId, releaseId, kind.ToString(), "P", "A", "S", "author", now, ChangeRequestType.Software, softwareLevel: RequirementLevel.HighLevel);
        scr.AddRequirementChange("author", requirementNumber, revision, RequirementLevel.HighLevel, kind, statement, "Rationale", "Test", now,
            attributesJson: "{\"derived\":true}");
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

    private static ILadderPolicy ConfiguredSystemHighLowPolicy(Guid projectId)
    {
        var now = DateTimeOffset.UtcNow;
        var configuration = ProjectLadderConfiguration.CreateDraft(projectId, now);
        var system = new ProjectLadderStep(configuration.Id, projectId, RequirementLevel.System, 1,
            LegacyLadderPolicy.Instance.Definition(RequirementLevel.System).Capabilities, now);
        var high = new ProjectLadderStep(configuration.Id, projectId, RequirementLevel.HighLevel, 2,
            LegacyLadderPolicy.Instance.Definition(RequirementLevel.HighLevel).Capabilities, now);
        var low = new ProjectLadderStep(configuration.Id, projectId, RequirementLevel.LowLevel, 3,
            LegacyLadderPolicy.Instance.Definition(RequirementLevel.LowLevel).Capabilities, now);
        configuration.Steps.Add(system); configuration.Steps.Add(high); configuration.Steps.Add(low);
        configuration.AllowedUpstream.Add(new ProjectLadderAllowedUpstream(configuration.Id, projectId,
            system.Id, high.Id, now));
        configuration.AllowedUpstream.Add(new ProjectLadderAllowedUpstream(configuration.Id, projectId,
            system.Id, low.Id, now));
        configuration.AllowedUpstream.Add(new ProjectLadderAllowedUpstream(configuration.Id, projectId,
            high.Id, low.Id, now));
        return new ResolvedProjectLadderPolicy(ProjectLadderResolver.Resolve(configuration));
    }
}
