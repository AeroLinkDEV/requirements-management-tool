using AeroLink.Domain.Baselines;
using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Common;
using AeroLink.Domain.Programs;
using AeroLink.Domain.Requirements;
using AeroLink.Domain.Verification;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AeroLink.Infrastructure.Tests;

/// <summary>
/// Closes the loop that materialisation opens.
///
/// Verification impact items are raised when a change request is approved, but at that moment the requirement
/// revisions they concern do not exist — only a baseline freeze and materialisation creates them. Everything
/// here is therefore only observable after materialisation, and every one of these behaviours previously had
/// domain methods written for it that nothing ever called.
/// </summary>
public sealed class VerificationImpactMaterializationTests
{
    private sealed record Fixture(DbContextOptions<AeroLinkDbContext> Options, Guid ProjectId, Guid ReleaseId, string Path);

    private static async Task<Fixture> SeedAsync()
    {
        var path = Path.Combine(Path.GetTempPath(), $"aerolink-vmat-{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<AeroLinkDbContext>().UseSqlite($"Data Source={path};Pooling=False").Options;
        await using var db = new AeroLinkDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var program = new ProgramRecord("FMS", "FMSV");
        var project = new ProjectRecord(program.Id, "Software", "FMS Software");
        var release = new SoftwareRelease(project.Id, "1.6", false);
        db.AddRange(program, project, release);
        await db.SaveChangesAsync();
        return new Fixture(options, project.Id, release.Id, path);
    }

    private static SystemChangeRequest ApprovedScr(string scrNumber, string requirementNumber, int revision,
        RequirementChangeKind kind, string statement, Guid projectId, Guid releaseId, DateTimeOffset now,
        string verificationMethod = "Test")
    {
        var scr = new SystemChangeRequest(scrNumber, 0, projectId, releaseId, kind.ToString(), "P", "A", "S", "author", now, ChangeRequestType.Software, softwareLevel: RequirementLevel.HighLevel);
        scr.AddRequirementChange("author", requirementNumber, revision, RequirementLevel.HighLevel, kind,
            statement, "Rationale", verificationMethod, now, attributesJson: "{\"derived\":true}");
        scr.SubmitForReview("author", [new("reviewer", "Reviewer")], now);
        scr.ApproveActiveStage("reviewer", now);
        return scr;
    }

    private static CandidateBaseline FrozenBaseline(string number, Guid projectId, Guid releaseId,
        Guid? predecessor, SystemChangeRequest scr, DateTimeOffset now)
    {
        var baseline = new CandidateBaseline(number, 0, projectId, releaseId, predecessor, number, "cm", now);
        baseline.Select(scr, "cm", now);
        baseline.Freeze("cm", now);
        return baseline;
    }

    private static async Task MaterializeAsync(AeroLinkDbContext db, Guid baselineId, DateTimeOffset now) =>
        await new RequirementBaselineMaterializer(db, new VerificationImpactService(db))
            .MaterializeAsync(baselineId, "cm", now, default);

    [Fact]
    public async Task An_item_binds_to_the_exact_revision_its_change_produced()
    {
        var seed = await SeedAsync();
        try
        {
            var now = DateTimeOffset.UtcNow;
            await using var db = new AeroLinkDbContext(seed.Options);
            var scr = ApprovedScr("HLRCR-00001", "SWR-00002375", 0, RequirementChangeKind.Introduce,
                "The FMS shall sequence oceanic waypoints.", seed.ProjectId, seed.ReleaseId, now);
            var baseline = FrozenBaseline("SW-00.10", seed.ProjectId, seed.ReleaseId, null, scr, now);
            db.AddRange(scr, baseline);
            await new VerificationImpactService(db).RaiseForApprovedChangeRequestAsync(scr, now, default);
            await db.SaveChangesAsync();

            var raised = await db.VerificationImpactItems.SingleAsync();
            Assert.Null(raised.RequirementRevisionId);

            await MaterializeAsync(db, baseline.Id, now);

            var revision = await db.RequirementRevisions.SingleAsync();
            var bound = await db.VerificationImpactItems.AsNoTracking().SingleAsync();
            Assert.Equal(revision.Id, bound.RequirementRevisionId);
            Assert.Equal(scr.RequirementChanges.Single().Id, bound.RequirementChangeId);
        }
        finally { File.Delete(seed.Path); }
    }

    [Fact]
    public async Task Selected_coverage_for_an_introduced_requirement_becomes_mandatory_test_scope()
    {
        var seed = await SeedAsync();
        try
        {
            var now = DateTimeOffset.UtcNow;
            await using var db = new AeroLinkDbContext(seed.Options);
            var scr = ApprovedScr("HLRCR-00001", "SWR-00002375", 0, RequirementChangeKind.Introduce,
                "The FMS shall sequence oceanic waypoints.", seed.ProjectId, seed.ReleaseId, now);
            var baseline = FrozenBaseline("SW-00.10", seed.ProjectId, seed.ReleaseId, null, scr, now);
            db.AddRange(scr, baseline);
            await new VerificationImpactService(db).RaiseForApprovedChangeRequestAsync(scr, now, default);
            await db.SaveChangesAsync();

            // The controlled requirement revision must exist before a newly authored Case can record its
            // exact Allocated parent. The impact decision remains open while that baseline is materialized.
            await MaterializeAsync(db, baseline.Id, now);
            var revision = await db.RequirementRevisions.AsNoTracking().SingleAsync();
            var procedure = new TestProcedure(seed.ProjectId, "HLRTC-00000001", "Oceanic sequencing", "test.lead", now);
            var procedureRevision = new TestProcedureRevision(procedure.Id, 0, "Verify oceanic sequencing",
                "Aircraft on ground", "Load the plan and sequence", "Waypoints sequence in order",
                TestProcedureState.Approved, "test.engineer", now, effectiveBaselineId: baseline.Id,
                parentKind: VerificationProcedureParentKind.Allocated);
            db.AddRange(procedure, procedureRevision,
                new TestRequirementCoverage(procedureRevision.Id, revision.Id));
            await db.SaveChangesAsync();

            var item = await db.VerificationImpactItems.SingleAsync();
            item.AssignToEngineer("test.lead", "test.engineer", now);
            item.Resolve("test.engineer", VerificationImpactOutcome.ProcedureCoverageConfirmed,
                "This approved procedure verifies the introduced requirement.", now,
                procedure.Id, procedureRevision.Id);
            var change = scr.RequirementChanges.Single();
            await new VerificationImpactService(db).ApplyMaterializationAsync(seed.ProjectId, seed.ReleaseId,
                [new MaterializedRequirementChange(scr.Id, change.Id, change.Kind, null, revision.Id, change.DisplayNumber)],
                "test.engineer", now, default);
            await db.SaveChangesAsync();
            Assert.Equal(procedureRevision.Id, (await db.TestCoverage.AsNoTracking()
                .SingleAsync(x => x.RequirementRevisionId == revision.Id)).ProcedureRevisionId);
            var mandatorySet = await db.BuildTestSets.Include(x => x.Entries)
                .SingleAsync(x => x.ReleaseId == seed.ReleaseId
                    && x.Discipline == TestChangeReviewDiscipline.HighLevelSoftware);
            var mandatory = Assert.Single(mandatorySet.Entries);
            Assert.Equal(procedureRevision.Id, mandatory.ProcedureRevisionId);
            Assert.Equal(TestSelectionReason.ChangedRequirement, mandatory.Reason);
        }
        finally { File.Delete(seed.Path); }
    }

    [Fact]
    public async Task Pre_materialisation_confirmation_without_a_predecessor_defers_until_a_controlled_successor()
    {
        var seed = await SeedAsync();
        try
        {
            var now = DateTimeOffset.UtcNow;
            await using var db = new AeroLinkDbContext(seed.Options);

            // Establish an existing approved Case/System Procedure revision and one exact parent.
            var firstScr = ApprovedScr("HLRCR-00001", "SWR-00002375", 0, RequirementChangeKind.Introduce,
                "The FMS shall sequence oceanic waypoints.", seed.ProjectId, seed.ReleaseId, now);
            var first = FrozenBaseline("SW-00.10", seed.ProjectId, seed.ReleaseId, null, firstScr, now);
            db.AddRange(firstScr, first);
            await db.SaveChangesAsync();
            await MaterializeAsync(db, first.Id, now);
            var firstRevision = await db.RequirementRevisions.SingleAsync();
            var procedure = new TestProcedure(seed.ProjectId, "HLRTC-00000001", "Oceanic sequencing", "test.lead", now);
            var procedureRevision = new TestProcedureRevision(procedure.Id, 0, "Verify oceanic sequencing",
                "Aircraft on ground", "Load the plan and sequence", "Waypoints sequence in order",
                TestProcedureState.Approved, "test.engineer", now, effectiveBaselineId: first.Id,
                parentKind: VerificationProcedureParentKind.Allocated);
            db.AddRange(procedure, procedureRevision,
                new TestRequirementCoverage(procedureRevision.Id, firstRevision.Id));
            await db.SaveChangesAsync();

            // This is a new requirement: its impact item has no exact revision yet. Naming the existing
            // procedure is useful evidence, but it cannot mutate that approved revision in place.
            var secondScr = ApprovedScr("HLRCR-00002", "SWR-00002376", 0, RequirementChangeKind.Introduce,
                "The FMS shall annunciate sequencing failures.", seed.ProjectId, seed.ReleaseId, now);
            var second = FrozenBaseline("SW-00.20", seed.ProjectId, seed.ReleaseId, first.Id, secondScr, now);
            db.Add(secondScr);
            var impactService = new VerificationImpactService(db);
            await impactService.RaiseForApprovedChangeRequestAsync(secondScr, now, default);
            db.Add(second);
            await db.SaveChangesAsync();

            var item = await db.VerificationImpactItems.SingleAsync(x => x.ChangeRequestId == secondScr.Id);
            item.Resolve("test.engineer", VerificationImpactOutcome.ProcedureCoverageConfirmed,
                "The existing procedure is the intended verifier once the exact requirement exists.", now,
                procedure.Id, procedureRevision.Id);
            await db.SaveChangesAsync();
            var review = await db.TestChangeReviews.SingleAsync(x => x.Id == item.TestChangeReviewId);

            var submitException = await Assert.ThrowsAsync<DomainException>(() =>
                TestChangeReviewRequirementScope.ValidateRetargetPlansForSubmissionAsync(db, review, default));
            Assert.Contains("exact requirement revision", submitException.Message, StringComparison.OrdinalIgnoreCase);

            await MaterializeAsync(db, second.Id, now);

            var exactLinks = await db.TestCoverage.AsNoTracking()
                .Where(x => x.ProcedureRevisionId == procedureRevision.Id)
                .ToListAsync();
            Assert.Single(exactLinks);
            Assert.Equal(firstRevision.Id, exactLinks[0].RequirementRevisionId);
            Assert.DoesNotContain(await db.RequirementRevisions.AsNoTracking()
                .Where(x => x.ArtifactId != firstRevision.ArtifactId)
                .Select(x => x.Id).ToListAsync(), id => exactLinks.Any(link => link.RequirementRevisionId == id));
        }
        finally { File.Delete(seed.Path); }
    }

    [Fact]
    public async Task Coverage_on_a_modified_requirement_carries_forward_marked_suspect()
    {
        var seed = await SeedAsync();
        try
        {
            var now = DateTimeOffset.UtcNow;
            await using var db = new AeroLinkDbContext(seed.Options);

            var introduce = ApprovedScr("HLRCR-00001", "SWR-00002375", 0, RequirementChangeKind.Introduce,
                "The FMS shall sequence oceanic waypoints.", seed.ProjectId, seed.ReleaseId, now);
            var first = FrozenBaseline("SW-00.10", seed.ProjectId, seed.ReleaseId, null, introduce, now);
            db.AddRange(introduce, first);
            await db.SaveChangesAsync();
            await MaterializeAsync(db, first.Id, now);

            // A procedure verifies revision 00.
            var firstRevision = await db.RequirementRevisions.SingleAsync();
            var procedure = new TestProcedure(seed.ProjectId, "HLRTC-00000001", "Oceanic sequencing", "test.lead", now);
            var procedureRevision = new TestProcedureRevision(procedure.Id, 0, "Verify oceanic sequencing",
                "Aircraft on ground", "Load the plan and sequence", "Waypoints sequence in order",
                TestProcedureState.Approved, "test.engineer", now, effectiveBaselineId: first.Id,
                parentKind: VerificationProcedureParentKind.Allocated);
            db.AddRange(procedure, procedureRevision);
            db.TestCoverage.Add(new TestRequirementCoverage(procedureRevision.Id, firstRevision.Id));
            await db.SaveChangesAsync();

            // The requirement is then modified underneath it.
            var modify = ApprovedScr("HLRCR-00002", "SWR-00002375", 1, RequirementChangeKind.Modify,
                "The FMS shall sequence oceanic waypoints within two seconds.", seed.ProjectId, seed.ReleaseId, now);
            var second = FrozenBaseline("SW-00.20", seed.ProjectId, seed.ReleaseId, first.Id, modify, now);
            db.AddRange(modify, second);
            await new VerificationImpactService(db).RaiseForApprovedChangeRequestAsync(modify, now, default);
            await db.SaveChangesAsync();
            await MaterializeAsync(db, second.Id, now);

            var secondRevision = await db.RequirementRevisions.AsNoTracking().SingleAsync(x => x.Revision == 1);
            var carried = await db.TestCoverage.AsNoTracking().SingleAsync(x => x.RequirementRevisionId == secondRevision.Id);
            Assert.Equal(procedureRevision.Id, carried.ProcedureRevisionId);
            Assert.True(carried.IsSuspect);
            Assert.Contains("SWR-00002375", carried.SuspectReason);
            var mandatorySet = await db.BuildTestSets.Include(x => x.Entries)
                .SingleAsync(x => x.ReleaseId == seed.ReleaseId
                    && x.Discipline == TestChangeReviewDiscipline.HighLevelSoftware);
            var mandatory = Assert.Single(mandatorySet.Entries);
            Assert.Equal(procedureRevision.Id, mandatory.ProcedureRevisionId);
            Assert.Equal(TestSelectionReason.ChangedRequirement, mandatory.Reason);

            // The original link is untouched: history stays exactly as it was approved.
            var original = await db.TestCoverage.AsNoTracking().SingleAsync(x => x.RequirementRevisionId == firstRevision.Id);
            Assert.False(original.IsSuspect);
        }
        finally { File.Delete(seed.Path); }
    }

    [Fact]
    public async Task Confirming_a_procedure_clears_the_suspect_flag_rather_than_duplicating_the_link()
    {
        var seed = await SeedAsync();
        try
        {
            var now = DateTimeOffset.UtcNow;
            await using var db = new AeroLinkDbContext(seed.Options);

            var introduce = ApprovedScr("HLRCR-00001", "SWR-00002375", 0, RequirementChangeKind.Introduce,
                "The FMS shall sequence oceanic waypoints.", seed.ProjectId, seed.ReleaseId, now);
            var first = FrozenBaseline("SW-00.10", seed.ProjectId, seed.ReleaseId, null, introduce, now);
            db.AddRange(introduce, first);
            await db.SaveChangesAsync();
            await MaterializeAsync(db, first.Id, now);

            var firstRevision = await db.RequirementRevisions.SingleAsync();
            var procedure = new TestProcedure(seed.ProjectId, "HLRTC-00000001", "Oceanic sequencing", "test.lead", now);
            var procedureRevision = new TestProcedureRevision(procedure.Id, 0, "Verify oceanic sequencing",
                "Aircraft on ground", "Load the plan and sequence", "Waypoints sequence in order",
                TestProcedureState.Approved, "test.engineer", now, effectiveBaselineId: first.Id,
                parentKind: VerificationProcedureParentKind.Allocated);
            db.AddRange(procedure, procedureRevision);
            db.TestCoverage.Add(new TestRequirementCoverage(procedureRevision.Id, firstRevision.Id));
            await db.SaveChangesAsync();

            var modify = ApprovedScr("HLRCR-00002", "SWR-00002375", 1, RequirementChangeKind.Modify,
                "The FMS shall sequence oceanic waypoints within two seconds.", seed.ProjectId, seed.ReleaseId, now);
            var second = FrozenBaseline("SW-00.20", seed.ProjectId, seed.ReleaseId, first.Id, modify, now);
            db.AddRange(modify, second);
            await new VerificationImpactService(db).RaiseForApprovedChangeRequestAsync(modify, now, default);
            await db.SaveChangesAsync();

            // The verification engineer decides before the baseline materialises, naming the procedure.
            var item = await db.VerificationImpactItems.SingleAsync();
            item.AssignToEngineer("test.lead", "test.engineer", now);
            item.Resolve("test.engineer", VerificationImpactOutcome.ProcedureCoverageConfirmed,
                "The existing procedure still exercises the two-second bound.", now,
                procedure.Id, procedureRevision.Id);
            Assert.True(item.PreReleaseEvidenceRequired);
            await db.SaveChangesAsync();

            await MaterializeAsync(db, second.Id, now);

            var secondRevision = await db.RequirementRevisions.AsNoTracking().SingleAsync(x => x.Revision == 1);
            var links = await db.TestCoverage.AsNoTracking()
                .Where(x => x.RequirementRevisionId == secondRevision.Id).ToListAsync();
            Assert.Single(links);
            Assert.False(links[0].IsSuspect);
            Assert.Equal("test.engineer", links[0].ConfirmedBy);
        }
        finally { File.Delete(seed.Path); }
    }

    [Fact]
    public async Task Confirming_a_procedure_after_materialisation_clears_the_existing_suspect_link()
    {
        var seed = await SeedAsync();
        try
        {
            var now = DateTimeOffset.UtcNow;
            await using var db = new AeroLinkDbContext(seed.Options);

            var introduce = ApprovedScr("HLRCR-00001", "SWR-00002375", 0, RequirementChangeKind.Introduce,
                "The FMS shall sequence oceanic waypoints.", seed.ProjectId, seed.ReleaseId, now);
            var first = FrozenBaseline("SW-00.10", seed.ProjectId, seed.ReleaseId, null, introduce, now);
            db.AddRange(introduce, first);
            await db.SaveChangesAsync();
            await MaterializeAsync(db, first.Id, now);

            var firstRevision = await db.RequirementRevisions.SingleAsync();
            var procedure = new TestProcedure(seed.ProjectId, "HLRTC-00000001", "Oceanic sequencing", "test.lead", now);
            var procedureRevision = new TestProcedureRevision(procedure.Id, 0, "Verify oceanic sequencing",
                "Aircraft on ground", "Load the plan and sequence", "Waypoints sequence in order",
                TestProcedureState.Approved, "test.engineer", now, effectiveBaselineId: first.Id,
                parentKind: VerificationProcedureParentKind.Allocated);
            db.AddRange(procedure, procedureRevision);
            db.TestCoverage.Add(new TestRequirementCoverage(procedureRevision.Id, firstRevision.Id));
            await db.SaveChangesAsync();

            var modify = ApprovedScr("HLRCR-00002", "SWR-00002375", 1, RequirementChangeKind.Modify,
                "The FMS shall sequence oceanic waypoints within two seconds.", seed.ProjectId, seed.ReleaseId, now);
            var second = FrozenBaseline("SW-00.20", seed.ProjectId, seed.ReleaseId, first.Id, modify, now);
            db.AddRange(modify, second);
            var service = new VerificationImpactService(db);
            await service.RaiseForApprovedChangeRequestAsync(modify, now, default);
            await db.SaveChangesAsync();
            await MaterializeAsync(db, second.Id, now);

            var item = await db.VerificationImpactItems.SingleAsync();
            Assert.NotNull(item.RequirementRevisionId);
            Assert.True((await db.TestCoverage.SingleAsync(
                x => x.RequirementRevisionId == item.RequirementRevisionId)).IsSuspect);

            item.Resolve("test.engineer", VerificationImpactOutcome.ProcedureCoverageConfirmed,
                "The procedure still exercises the exact changed requirement.", now.AddHours(1),
                procedure.Id, procedureRevision.Id);
            Assert.True(await service.ApplyResolvedCoverageAsync(item, now.AddHours(1), default));
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            var links = await db.TestCoverage.AsNoTracking()
                .Where(x => x.RequirementRevisionId == item.RequirementRevisionId).ToListAsync();
            Assert.Single(links);
            Assert.False(links[0].IsSuspect);
            Assert.Equal("test.engineer", links[0].ConfirmedBy);
        }
        finally { File.Delete(seed.Path); }
    }

    [Fact]
    public async Task Retiring_the_last_requirement_a_procedure_covers_raises_an_orphan_item()
    {
        var seed = await SeedAsync();
        try
        {
            var now = DateTimeOffset.UtcNow;
            await using var db = new AeroLinkDbContext(seed.Options);

            var introduce = ApprovedScr("HLRCR-00001", "SWR-00002375", 0, RequirementChangeKind.Introduce,
                "The FMS shall sequence oceanic waypoints.", seed.ProjectId, seed.ReleaseId, now);
            var first = FrozenBaseline("SW-00.10", seed.ProjectId, seed.ReleaseId, null, introduce, now);
            db.AddRange(introduce, first);
            await db.SaveChangesAsync();
            await MaterializeAsync(db, first.Id, now);

            var firstRevision = await db.RequirementRevisions.SingleAsync();
            var procedure = new TestProcedure(seed.ProjectId, "HLRTC-00000001", "Oceanic sequencing", "test.lead", now);
            var procedureRevision = new TestProcedureRevision(procedure.Id, 0, "Verify oceanic sequencing",
                "Aircraft on ground", "Load the plan and sequence", "Waypoints sequence in order",
                TestProcedureState.Approved, "test.engineer", now, effectiveBaselineId: first.Id,
                parentKind: VerificationProcedureParentKind.Allocated);
            db.AddRange(procedure, procedureRevision);
            db.TestCoverage.Add(new TestRequirementCoverage(procedureRevision.Id, firstRevision.Id));
            await db.SaveChangesAsync();

            var retire = ApprovedScr("HLRCR-00002", "SWR-00002375", 1, RequirementChangeKind.Retire,
                "", seed.ProjectId, seed.ReleaseId, now);
            var second = FrozenBaseline("SW-00.20", seed.ProjectId, seed.ReleaseId, first.Id, retire, now);
            db.AddRange(retire, second);
            await new VerificationImpactService(db).RaiseForApprovedChangeRequestAsync(retire, now, default);
            await db.SaveChangesAsync();

            // Retirement raises nothing on its own — only stranding a procedure does.
            Assert.Empty(await db.VerificationImpactItems.AsNoTracking().ToListAsync());

            await MaterializeAsync(db, second.Id, now);

            var orphan = await db.VerificationImpactItems.AsNoTracking().SingleAsync();
            Assert.Equal(VerificationImpactTrigger.ProcedureOrphaned, orphan.Trigger);
            Assert.Equal(procedure.Id, orphan.ProcedureId);
            Assert.Equal("HLRTC-00000001", orphan.SubjectDisplayNumber);
            Assert.True(orphan.BlocksBaselineApproval);
        }
        finally { File.Delete(seed.Path); }
    }

    [Fact]
    public async Task A_procedure_that_still_covers_something_else_stays_quiet()
    {
        var seed = await SeedAsync();
        try
        {
            var now = DateTimeOffset.UtcNow;
            await using var db = new AeroLinkDbContext(seed.Options);

            // Two requirements, one procedure covering both.
            var introduce = new SystemChangeRequest("HLRCR-00001", 0, seed.ProjectId, seed.ReleaseId,
                "Introduce", "P", "A", "S", "author", now, ChangeRequestType.Software, softwareLevel: RequirementLevel.HighLevel);
            introduce.AddRequirementChange("author", "SWR-00002375", 0, RequirementLevel.HighLevel,
                RequirementChangeKind.Introduce, "The FMS shall sequence oceanic waypoints.", "R", "Test", now,
                attributesJson: "{\"derived\":true}");
            introduce.AddRequirementChange("author", "SWR-00002376", 0, RequirementLevel.HighLevel,
                RequirementChangeKind.Introduce, "The FMS shall annunciate sequencing failures.", "R", "Test", now,
                attributesJson: "{\"derived\":true}");
            introduce.SubmitForReview("author", [new("reviewer", "Reviewer")], now);
            introduce.ApproveActiveStage("reviewer", now);
            var first = FrozenBaseline("SW-00.10", seed.ProjectId, seed.ReleaseId, null, introduce, now);
            db.AddRange(introduce, first);
            await db.SaveChangesAsync();
            await MaterializeAsync(db, first.Id, now);

            var revisions = await db.RequirementRevisions.ToListAsync();
            var procedure = new TestProcedure(seed.ProjectId, "HLRTC-00000001", "Oceanic sequencing", "test.lead", now);
            var procedureRevision = new TestProcedureRevision(procedure.Id, 0, "Verify sequencing",
                "Aircraft on ground", "Sequence and fail", "Both behaviours observed",
                TestProcedureState.Approved, "test.engineer", now, effectiveBaselineId: first.Id,
                parentKind: VerificationProcedureParentKind.Allocated);
            db.AddRange(procedure, procedureRevision);
            foreach (var revision in revisions) db.TestCoverage.Add(new TestRequirementCoverage(procedureRevision.Id, revision.Id));
            await db.SaveChangesAsync();

            // Retire only one of them.
            var retire = ApprovedScr("HLRCR-00002", "SWR-00002375", 1, RequirementChangeKind.Retire,
                "", seed.ProjectId, seed.ReleaseId, now);
            var second = FrozenBaseline("SW-00.20", seed.ProjectId, seed.ReleaseId, first.Id, retire, now);
            db.AddRange(retire, second);
            await db.SaveChangesAsync();
            await MaterializeAsync(db, second.Id, now);

            Assert.Empty(await db.VerificationImpactItems.AsNoTracking()
                .Where(x => x.Trigger == VerificationImpactTrigger.ProcedureOrphaned).ToListAsync());
        }
        finally { File.Delete(seed.Path); }
    }
}
