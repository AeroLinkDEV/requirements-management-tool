using System.IO.Compression;
using AeroLink.Domain.Baselines;
using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Hierarchy;
using AeroLink.Domain.Programs;
using AeroLink.Domain.Requirements;
using AeroLink.Domain.Traceability;
using AeroLink.Domain.Verification;
using AeroLink.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace AeroLink.Infrastructure.Tests;

public sealed class LegacyControlledProcedureDocumentSnapshotTests
{
    [Fact]
    public async Task An_exact_Procedure_document_excludes_a_link_to_a_predecessor_Case_revision_not_in_its_baseline()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AeroLinkDbContext>().UseSqlite(connection).Options;
        await using var db = new AeroLinkDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var now = new DateTimeOffset(2026, 8, 24, 4, 0, 0, TimeSpan.Zero);
        var program = new ProgramRecord("Procedure parent manifest", "PPM");
        var project = new ProjectRecord(program.Id, "Procedure parent project", "Procedure parent product");
        var release = new SoftwareRelease(project.Id, "7.2", false);
        var baseline = new CandidateBaseline("SW-72.08", 0, project.Id, release.Id, null,
            "Exact Procedure baseline", "cm", now);
        var policy = ProcedurePolicy();
        var @case = new TestProcedure(project.Id, "HLRTC-728100", "HLR Case", "case.owner", now,
            TestProcedureLevel.HighLevel, policy, VerificationArtifactKind.Case);
        var predecessorCase = new TestProcedureRevision(@case.Id, 0, "Old Case objective", "Old preconditions",
            "Old Case steps", "Old expected result", TestProcedureState.Approved, "case.owner", now);
        var procedure = new TestProcedure(project.Id, "HLRTP-728100", "HLR Procedure", "procedure.owner", now,
            TestProcedureLevel.HighLevel, policy, VerificationArtifactKind.Procedure,
            VerificationProcedureParentKind.Allocated);
        var procedureRevision = new TestProcedureRevision(procedure.Id, 0, "Procedure objective", "Setup",
            "Ordered steps", "Expected observations", TestProcedureState.Draft, "procedure.owner", now,
            parentKind: VerificationProcedureParentKind.Allocated,
            environmentSetup: "Setup", testData: "Test data", orderedSteps: "Ordered steps",
            expectedObservations: "Expected observations", cleanup: "Cleanup",
            toolingAutomation: "Tooling");
        db.AddRange(program, project, release, baseline, @case, predecessorCase, procedure, procedureRevision,
            new TestCaseProcedureLink(predecessorCase.Id, procedureRevision.Id));
        await db.SaveChangesAsync();
        await db.TestProcedureRevisions.Where(x => x.Id == procedureRevision.Id)
            .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.State, TestProcedureState.Approved));

        // The link remains valid historical data, but revision 1 is the Case selected for this exact baseline.
        // A regenerated Procedure document must not silently describe its predecessor as current effectivity.
        var currentCase = new TestProcedureRevision(@case.Id, 1, "Current Case objective", "Current preconditions",
            "Current Case steps", "Current expected result", TestProcedureState.Approved, "case.owner", now.AddMinutes(1));
        var carried = new TestCaseProcedureLink(currentCase.Id, procedureRevision.Id);
        var lifecycle = ExactLinkSuspectLifecycle.Raise(project.Id, ExactLinkKind.CaseProcedure, carried.Id,
            ExactLinkLifecycleCauseKind.InternalVerificationRevision, null, null, "cm",
            "The exact Case successor requires Procedure reassessment.", now.AddMinutes(1), currentCase.Id);
        carried.AttachExactLinkLifecycle(lifecycle.Id);
        db.AddRange(currentCase,
            new BaselineTestProcedureSelection(baseline.Id, @case.Id, currentCase.Id),
            new BaselineTestProcedureSelection(baseline.Id, procedure.Id, procedureRevision.Id),
            carried, lifecycle);
        await db.SaveChangesAsync();
        await db.CandidateBaselines.Where(x => x.Id == baseline.Id).ExecuteUpdateAsync(setters => setters
            .SetProperty(x => x.TestProceduresMaterializedAt, now.AddMinutes(2))
            .SetProperty(x => x.TestProceduresHash, new string('7', 64)));

        async Task<IReadOnlyList<Guid>> SnapshotParentsAsync(DateTimeOffset generatedAt)
        {
            var snapshot = await ControlledProcedureDocumentSnapshotProjection.ForDocumentAsync(db, baseline.Id,
                new VerificationArtifactKey(VerificationDiscipline.HighLevelSoftware,
                    VerificationArtifactKind.Procedure), generatedAt, default);
            var row = Assert.Single(snapshot.Rows);
            Assert.Equal(procedureRevision.Id, row.RevisionId);
            Assert.DoesNotContain(predecessorCase.Id, row.ParentRevisionIds ?? Array.Empty<Guid>());
            return row.ParentRevisionIds ?? Array.Empty<Guid>();
        }

        Assert.Empty(await SnapshotParentsAsync(now.AddMinutes(3)));
        var lifecycleService = new ExactLinkLifecycleService(db);
        await lifecycleService.AcknowledgeAsync(ExactLinkKind.CaseProcedure, carried.Id,
            "test.lead", "The exact relation is under review.", now.AddMinutes(4), default);
        Assert.Empty(await SnapshotParentsAsync(now.AddMinutes(5)));
        await lifecycleService.ResolveAsync(ExactLinkKind.CaseProcedure, carried.Id,
            ExactLinkResolutionOutcome.DownstreamChangeRequiredNotYetApproved, "test.lead",
            "The Procedure needs controlled work before this relation can be effective.", now.AddMinutes(6), default);
        Assert.Empty(await SnapshotParentsAsync(now.AddMinutes(7)));
        await lifecycleService.ResolveAsync(ExactLinkKind.CaseProcedure, carried.Id,
            ExactLinkResolutionOutcome.ExistingDownstreamRevisionRemainsValid, "test.lead",
            "The approved Procedure remains valid after completing the reassessment.", now.AddMinutes(8), default);
        Assert.Equal([currentCase.Id], await SnapshotParentsAsync(now.AddMinutes(9)));
    }

    [Fact]
    public async Task A_released_predecessor_document_does_not_leak_successor_coverage_from_a_carried_revision()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AeroLinkDbContext>().UseSqlite(connection).Options;
        await using var db = new AeroLinkDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var now = new DateTimeOffset(2026, 8, 23, 6, 0, 0, TimeSpan.Zero);
        var program = new ProgramRecord("Released snapshot scope", "RSS");
        var project = new ProjectRecord(program.Id, "Released snapshot project", "Released snapshot product");
        var release = new SoftwareRelease(project.Id, "7.0", false);
        var source0 = new SystemChangeRequest("SRCR-73800", 0, project.Id, release.Id,
            "Initial system obligation", "Problem", "Analysis", "Solution", "author", now);
        source0.AddRequirementChange("author", "SYSR-738000", 0, RequirementLevel.System,
            RequirementChangeKind.Introduce, "The system shall retain the released behavior.", "Initial", "Test", now);
        source0.SubmitForReview("author", [new ApproverSelection("reviewer", "Reviewer")], now);
        source0.ApproveActiveStage("reviewer", now);
        var predecessor = new CandidateBaseline("SW-73.00", 0, project.Id, release.Id, null,
            "Released predecessor", "cm", now);
        predecessor.Select(source0, "cm", now); predecessor.Freeze("cm", now);
        db.AddRange(program, project, release, source0, predecessor);
        await db.SaveChangesAsync();
        await new RequirementBaselineMaterializer(db, new VerificationImpactService(db))
            .MaterializeAsync(predecessor.Id, "cm", now, default);
        var originalRequirement = await db.RequirementRevisions.SingleAsync();

        var procedure = new TestProcedure(project.Id, "SYSTP-738000", "Released system check", "test", now,
            TestProcedureLevel.System);
        var procedureRevision = new TestProcedureRevision(procedure.Id, 0, "Verify released behavior",
            "Preconditions", "Steps", "Expected", TestProcedureState.Approved, "test", now,
            effectiveBaselineId: predecessor.Id, parentKind: VerificationProcedureParentKind.Allocated);
        db.AddRange(procedure, procedureRevision,
            new BaselineTestProcedureSelection(predecessor.Id, procedure.Id, procedureRevision.Id),
            new TestRequirementCoverage(procedureRevision.Id, originalRequirement.Id));
        await db.SaveChangesAsync();
        await db.CandidateBaselines.Where(x => x.Id == predecessor.Id).ExecuteUpdateAsync(set => set
            .SetProperty(x => x.TestProceduresMaterializedAt, now)
            .SetProperty(x => x.TestProceduresHash, new string('a', 64)));

        var source1 = new SystemChangeRequest("SRCR-73801", 0, project.Id, release.Id,
            "Successor system obligation", "Problem", "Analysis", "Solution", "author", now.AddMinutes(1));
        source1.AddRequirementChange("author", "SYSR-738000", 1, RequirementLevel.System,
            RequirementChangeKind.Modify, "The system shall retain the corrected behavior.", "Correction", "Test", now.AddMinutes(1));
        source1.SubmitForReview("author", [new ApproverSelection("reviewer", "Reviewer")], now.AddMinutes(1));
        source1.ApproveActiveStage("reviewer", now.AddMinutes(1));
        var successor = new CandidateBaseline("SW-73.01", 0, project.Id, release.Id, predecessor.Id,
            "Successor baseline", "cm", now.AddMinutes(1));
        successor.Select(source1, "cm", now.AddMinutes(1)); successor.Freeze("cm", now.AddMinutes(1));
        db.AddRange(source1, successor);
        await db.SaveChangesAsync();
        await new RequirementBaselineMaterializer(db, new VerificationImpactService(db))
            .MaterializeAsync(successor.Id, "cm", now.AddMinutes(2), default);
        var successorRequirement = await db.RequirementRevisions.SingleAsync(x => x.Revision == 1);
        // The carried endpoint is present for #709 lifecycle review but is not an approved exact parent.
        if (!await db.TestCoverage.AnyAsync(x => x.ProcedureRevisionId == procedureRevision.Id
            && x.RequirementRevisionId == successorRequirement.Id))
            db.TestCoverage.Add(TestRequirementCoverage.CarriedForward(
                procedureRevision.Id, successorRequirement.Id, "successor wording requires confirmation", now.AddMinutes(2)));
        db.BaselineTestProcedures.Add(new BaselineTestProcedureSelection(successor.Id, procedure.Id, procedureRevision.Id));
        await db.SaveChangesAsync();
        await db.CandidateBaselines.Where(x => x.Id == successor.Id).ExecuteUpdateAsync(set => set
            .SetProperty(x => x.TestProceduresMaterializedAt, now.AddMinutes(2))
            .SetProperty(x => x.TestProceduresHash, new string('b', 64)));

        var predecessorSnapshot = await ControlledProcedureDocumentSnapshotProjection.ForDocumentAsync(
            db, predecessor.Id, TestProcedureLevel.System, now.AddMinutes(3), default);
        var successorSnapshot = await ControlledProcedureDocumentSnapshotProjection.ForDocumentAsync(
            db, successor.Id, TestProcedureLevel.System, now.AddMinutes(3), default);
        var oldRow = Assert.Single(predecessorSnapshot.Rows);
        var newRow = Assert.Single(successorSnapshot.Rows);
        Assert.Equal([originalRequirement.Id], oldRow.ParentRevisionIds);
        Assert.Empty(newRow.ParentRevisionIds ?? Array.Empty<Guid>());
    }

    [Fact]
    public async Task A_pre_manifest_document_keeps_its_generation_time_revision_after_later_activity()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AeroLinkDbContext>().UseSqlite(connection).Options;
        await using var db = new AeroLinkDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var t0 = new DateTimeOffset(2026, 8, 10, 6, 0, 0, TimeSpan.Zero);
        var generatedAt = t0.AddHours(1);
        var program = new ProgramRecord("Legacy document snapshot", "LDS");
        var project = new ProjectRecord(program.Id, "Legacy document project", "Legacy document product");
        var release = new SoftwareRelease(project.Id, "4.0", false);
        var baseline = new CandidateBaseline("SW-40.00", 0, project.Id, release.Id, null,
            "Pre-manifest baseline", "cm", t0);

        var source00 = new SystemChangeRequest("SRCR-419900", 0, project.Id, release.Id,
            "Generation-time source", "Problem", "Analysis", "Solution", "author", t0);
        var procedure = new TestProcedure(project.Id, "SYSTP-419900", "Current catalog title",
            "verification.engineer", t0, TestProcedureLevel.System);
        var tcr00 = Review(project.Id, release.Id, source00.Id, source00.DisplayNumber,
            "SYSTPCR-419900", 0, TestProcedureChangeKind.Introduce, "Generation-time title", t0);
        var revision00 = new TestProcedureRevision(procedure.Id, 0, "Generation-time objective",
            "Generation-time preconditions", "Generation-time steps", "Generation-time expected result",
            TestProcedureState.Approved, "verification.engineer", t0,
            sourceTestChangeRequestId: tcr00.Id,
            parentKind: VerificationProcedureParentKind.Derived,
            derivedRationale: "This pre-manifest document fixture has no upstream coverage.");
        var document = new ControlledDocument(project.Id, release.Id, baseline.Id,
            ControlledDocumentType.SystemTestProcedures, "SYSTD-419900",
            "Legacy System Test Procedures", 0, new string('a', 64), 1, generatedAt);
        db.AddRange(program, project, release, baseline, source00, procedure, tcr00, revision00, document);
        await db.SaveChangesAsync();

        var root = Path.Combine(Path.GetTempPath(), $"aerolink-legacy-doc-{Guid.NewGuid():N}");
        try
        {
            var generator = new ControlledOutputGenerator(db,
                new RichContentPublisher(db, new EvidenceFileStore(root)));
            var first = await generator.GenerateAsync(document.Id, "docx", CancellationToken.None);
            Assert.NotNull(first);
            var firstXml = DocumentXml(first!);
            Assert.Contains("SYSTP-419900.00", firstXml);
            Assert.Contains("Generation-time title", firstXml);
            Assert.Contains("Generation-time objective", firstXml);
            Assert.Contains("Legacy generation-time compatibility snapshot", firstXml);
            Assert.Contains("Not materialized when this document was generated", firstXml);

            // Later activity that used to rewrite the old document: a new approved revision appears and the
            // stable catalog title changes. The old document must remain bound to what existed at GeneratedAt.
            var t2 = generatedAt.AddHours(1);
            var source01 = new SystemChangeRequest("SRCR-419901", 0, project.Id, release.Id,
                "Later source", "Problem", "Analysis", "Solution", "author", t2);
            var tcr01 = Review(project.Id, release.Id, source01.Id, source01.DisplayNumber,
                "SYSTPCR-419901", 1, TestProcedureChangeKind.Modify, "Later title", t2);
            var revision01 = new TestProcedureRevision(procedure.Id, 1, "Later objective",
                "Later preconditions", "Later steps", "Later expected result",
                TestProcedureState.Approved, "verification.engineer", t2,
                sourceTestChangeRequestId: tcr01.Id,
                parentKind: VerificationProcedureParentKind.Derived,
                derivedRationale: "This pre-manifest document fixture has no upstream coverage.");
            procedure.UpdateDraft("Later title", procedure.OwnerId, t2);
            db.AddRange(source01, tcr01, revision01);
            await db.SaveChangesAsync();

            var second = await generator.GenerateAsync(document.Id, "docx", CancellationToken.None);
            Assert.NotNull(second);
            var secondXml = DocumentXml(second!);
            Assert.Equal(firstXml, secondXml);
            Assert.DoesNotContain("SYSTP-419900.01", secondXml);
            Assert.DoesNotContain("Later title", secondXml);
            Assert.DoesNotContain("Later objective", secondXml);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task A_pre_manifest_snapshot_ignores_a_newer_draft_but_respects_retirement()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AeroLinkDbContext>().UseSqlite(connection).Options;
        await using var db = new AeroLinkDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var t0 = new DateTimeOffset(2026, 8, 10, 10, 0, 0, TimeSpan.Zero);
        var generatedAt = t0.AddHours(1);
        var program = new ProgramRecord("Legacy draft precedence", "LDP");
        var project = new ProjectRecord(program.Id, "Legacy draft project", "Legacy draft product");
        var release = new SoftwareRelease(project.Id, "4.1", false);
        var baseline = new CandidateBaseline("SW-41.00", 0, project.Id, release.Id, null,
            "Pre-manifest baseline", "cm", t0);

        var activeProcedure = new TestProcedure(project.Id, "SYSTP-419910", "Still effective",
            "verification.engineer", t0, TestProcedureLevel.System);
        var activeApproved = new TestProcedureRevision(activeProcedure.Id, 0, "Approved objective",
            "Approved preconditions", "Approved steps", "Approved expected result",
            TestProcedureState.Approved, "verification.engineer", t0);
        var activeDraft = new TestProcedureRevision(activeProcedure.Id, 1, "Draft objective",
            "Draft preconditions", "Draft steps", "Draft expected result",
            TestProcedureState.Draft, "verification.engineer", t0.AddMinutes(30));

        var retiredProcedure = new TestProcedure(project.Id, "SYSTP-419911", "Retired before generation",
            "verification.engineer", t0, TestProcedureLevel.System);
        var retiredApproved = new TestProcedureRevision(retiredProcedure.Id, 0, "Retired objective",
            "Retired preconditions", "Retired steps", "Retired expected result",
            TestProcedureState.Approved, "verification.engineer", t0);
        var retirement = new TestProcedureRevision(retiredProcedure.Id, 1, "", "", "", "",
            TestProcedureState.Retired, "verification.engineer", t0.AddMinutes(30));

        db.AddRange(program, project, release, baseline, activeProcedure, activeApproved, activeDraft,
            retiredProcedure, retiredApproved, retirement);
        await db.SaveChangesAsync();

        var snapshot = await ControlledProcedureDocumentSnapshotProjection.ForDocumentAsync(
            db, baseline.Id, TestProcedureLevel.System, generatedAt, CancellationToken.None);

        Assert.False(snapshot.IsExactManifest);
        var row = Assert.Single(snapshot.Rows);
        Assert.Equal(activeApproved.Id, row.RevisionId);
        Assert.Equal("SYSTP-419910", row.BaseNumber);
        Assert.Equal(0, row.Revision);
        Assert.Equal(TestProcedureState.Approved, row.State);
        Assert.Equal("Legacy procedure SYSTP-419910.00 — exact historical title was not recorded", row.Title);
        Assert.DoesNotContain(snapshot.Rows, x => x.BaseNumber == "SYSTP-419911");
    }

    [Fact]
    public async Task A_pre_manifest_unattributed_title_and_manifest_metadata_do_not_drift_after_later_materialization()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AeroLinkDbContext>().UseSqlite(connection).Options;
        await using var db = new AeroLinkDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var t0 = new DateTimeOffset(2026, 8, 10, 12, 0, 0, TimeSpan.Zero);
        var generatedAt = t0.AddHours(1);
        var later = generatedAt.AddHours(1);
        var program = new ProgramRecord("Legacy metadata immutability", "LMI");
        var project = new ProjectRecord(program.Id, "Legacy metadata project", "Legacy metadata product");
        var release = new SoftwareRelease(project.Id, "4.2", false);
        var baseline = new CandidateBaseline("SW-42.00", 0, project.Id, release.Id, null,
            "Pre-manifest baseline", "cm", t0);
        var procedure = new TestProcedure(project.Id, "SYSTP-419920", "Catalog title at generation",
            "verification.engineer", t0, TestProcedureLevel.System);
        var revision = new TestProcedureRevision(procedure.Id, 0, "Legacy objective",
            "Legacy preconditions", "Legacy steps", "Legacy expected result",
            TestProcedureState.Approved, "verification.engineer", t0);
        var document = new ControlledDocument(project.Id, release.Id, baseline.Id,
            ControlledDocumentType.SystemTestProcedures, "SYSTD-419920",
            "Legacy Unattributed Procedures", 0, new string('c', 64), 1, generatedAt);
        db.AddRange(program, project, release, baseline, procedure, revision, document);
        await db.SaveChangesAsync();

        var root = Path.Combine(Path.GetTempPath(), $"aerolink-legacy-metadata-{Guid.NewGuid():N}");
        try
        {
            var generator = new ControlledOutputGenerator(db,
                new RichContentPublisher(db, new EvidenceFileStore(root)));
            var first = Assert.IsType<GeneratedOutput>(
                await generator.GenerateAsync(document.Id, "docx", CancellationToken.None));
            var firstXml = DocumentXml(first);
            Assert.Contains("Legacy procedure SYSTP-419920.00 — exact historical title was not recorded", firstXml);
            Assert.Contains("Not materialized when this document was generated", firstXml);

            procedure.UpdateDraft("Later mutable catalog title", procedure.OwnerId, later);
            await db.SaveChangesAsync();
            var laterHash = new string('f', 64);
            await db.CandidateBaselines.Where(x => x.Id == baseline.Id)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(x => x.TestProceduresMaterializedAt, (DateTimeOffset?)later)
                    .SetProperty(x => x.TestProceduresHash, laterHash));

            var second = Assert.IsType<GeneratedOutput>(
                await generator.GenerateAsync(document.Id, "docx", CancellationToken.None));
            var secondXml = DocumentXml(second);
            Assert.Equal(firstXml, secondXml);
            Assert.DoesNotContain("Later mutable catalog title", secondXml);
            Assert.DoesNotContain(laterHash, secondXml);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static TestChangeReview Review(Guid projectId, Guid releaseId, Guid sourceChangeRequestId,
        string sourceChangeRequestNumber, string number, int revision,
        TestProcedureChangeKind kind, string title, DateTimeOffset now)
    {
        var review = new TestChangeReview(projectId, releaseId, sourceChangeRequestId,
            TestChangeReviewDiscipline.System, sourceChangeRequestNumber, now,
            number, revision);
        review.RecordTestChangeRequired("verification.engineer", now);
        review.AddProcedureChange("verification.engineer", new TestProcedureChangeDraft(
            "SYSTP-419900", revision, TestProcedureLevel.System, kind, title,
            "Objective", "Preconditions", "Steps", "Expected result", "Rationale", "[]"), now);
        return review;
    }

    private static ILadderPolicy ProcedurePolicy()
    {
        var projectId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var configuration = ProjectLadderConfiguration.CreateDraft(projectId, now);
        var steps = LegacyLadderPolicy.Instance.OrderedLevels.Select((level, index) =>
        {
            var kinds = level == RequirementLevel.System
                ? new[] { VerificationArtifactKind.Procedure }
                : new[] { VerificationArtifactKind.Case, VerificationArtifactKind.Procedure };
            var step = new ProjectLadderStep(configuration.Id, projectId, level, index + 1,
                LegacyLadderPolicy.Instance.Definition(level).Capabilities, now, kinds);
            configuration.Steps.Add(step);
            return step;
        }).ToArray();
        configuration.AllowedUpstream.Add(new ProjectLadderAllowedUpstream(
            configuration.Id, projectId, steps[0].Id, steps[1].Id, now));
        configuration.AllowedUpstream.Add(new ProjectLadderAllowedUpstream(
            configuration.Id, projectId, steps[1].Id, steps[2].Id, now));
        return new ResolvedProjectLadderPolicy(ProjectLadderResolver.Resolve(configuration));
    }

    private static string DocumentXml(GeneratedOutput output)
    {
        using var archive = new ZipArchive(new MemoryStream(output.Content), ZipArchiveMode.Read);
        var entry = archive.GetEntry("word/document.xml");
        Assert.NotNull(entry);
        using var reader = new StreamReader(entry!.Open());
        return reader.ReadToEnd();
    }
}
