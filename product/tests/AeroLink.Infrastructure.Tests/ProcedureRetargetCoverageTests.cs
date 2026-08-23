using AeroLink.Domain.Baselines;
using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Common;
using AeroLink.Domain.Hierarchy;
using AeroLink.Domain.Programs;
using AeroLink.Domain.Requirements;
using AeroLink.Domain.Verification;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AeroLink.Infrastructure.Tests;

/// <summary>
/// A verification-impact decision is evidence about controlled content, not an alternate authoring route.
/// Exact parent additions therefore require a controlled successor; only an existing #709 suspect link may
/// be confirmed in place.
/// </summary>
public sealed class ProcedureRetargetCoverageTests
{
    /// <summary>
    /// A real requirement to move onto, because the coverage table has a foreign key to it. A loose
    /// identifier would fail for the wrong reason and prove nothing about the decision under test.
    /// </summary>
    private sealed record Fixture(AeroLinkDbContext Db, Guid ProjectId, Guid OriginalRevisionId,
        Guid TargetRevisionId, Guid BaselineId);

    private static async Task<Fixture> DatabaseAsync()
    {
        var options = new DbContextOptionsBuilder<AeroLinkDbContext>().UseSqlite("Data Source=:memory:").Options;
        var db = new AeroLinkDbContext(options);
        await db.Database.OpenConnectionAsync();
        await db.Database.EnsureCreatedAsync();
        var now = DateTimeOffset.UtcNow;

        var program = new ProgramRecord("Retarget Program", "RTG");
        var project = new ProjectRecord(program.Id, "Flight Software", "Retarget Software");
        var release = new SoftwareRelease(project.Id, "1.0", false);
        var scr = new SystemChangeRequest("SRCR-00050", 0, project.Id, release.Id, "Move", "P", "A", "S", "author", now);
        scr.AddRequirementChange("author", "SYSR-00000150", 0, RequirementLevel.System,
            RequirementChangeKind.Introduce, "The system shall preserve the stranded behavior.",
            "The retarget fixture needs an effective requirement baseline.", "Test", now);
        scr.SubmitForReview("author", [new ApproverSelection("reviewer", "Reviewer")], now);
        scr.ApproveActiveStage("reviewer", now);
        var baseline = new CandidateBaseline("SW-50.00", 0, project.Id, release.Id, null, "Candidate", "cm", now);
        baseline.Select(scr, "cm", now);
        baseline.Freeze("cm", now);
        baseline.MarkRequirementsMaterialized("cm", new string('a', 64), 2, now);
        var originalArtifact = new RequirementArtifact(project.Id, "SYSR-00000150", RequirementLevel.System, now);
        var original = new RequirementRevision(originalArtifact.Id, 0, "The FMS shall sequence oceanic waypoints.",
            "Original behaviour.", "Test", RequirementRevisionState.Retired, scr.Id, baseline.Id, now);
        var artifact = new RequirementArtifact(project.Id, "SYSR-00000151", RequirementLevel.System, now);
        var revision = new RequirementRevision(artifact.Id, 0, "The FMS shall sequence oceanic waypoints.",
            "Moved behaviour.", "Test", RequirementRevisionState.Active, scr.Id, baseline.Id, now);
        db.AddRange(program, project, release, scr, baseline, originalArtifact, original, artifact, revision,
            new BaselineRequirementSelection(baseline.Id, originalArtifact.Id, original.Id),
            new BaselineRequirementSelection(baseline.Id, artifact.Id, revision.Id),
            LegacyDefaultProjectLadderFactory.Create(project.Id, now));
        await db.SaveChangesAsync();
        return new(db, project.Id, original.Id, revision.Id, baseline.Id);
    }

    private static VerificationImpactItem Resolved(Guid projectId, Guid releaseId, Guid procedureId, Guid target)
    {
        var item = VerificationImpactItem.ForOrphanedProcedure(projectId, releaseId, Guid.NewGuid(),
            Guid.NewGuid(), procedureId, "SYSTP-000042", DateTimeOffset.UtcNow);
        item.Resolve("test.engineer", VerificationImpactOutcome.ProcedureRetargeted,
            "The behaviour moved; this procedure still exercises it.", DateTimeOffset.UtcNow,
            retargetedRequirementRevisionId: target);
        return item;
    }

    [Fact]
    public async Task Retargeting_refuses_a_new_parent_and_leaves_the_effective_revision_unchanged()
    {
        var fixture = await DatabaseAsync();
        await using var db = fixture.Db;
        var now = DateTimeOffset.UtcNow;
        var projectId = fixture.ProjectId;
        var original = fixture.OriginalRevisionId;
        var target = fixture.TargetRevisionId;

        var procedure = new TestProcedure(projectId, "SYSTP-000042", "Oceanic sequencing", "test.engineer", now,
            TestProcedureLevel.System);
        var first = new TestProcedureRevision(procedure.Id, 0, "Objective", "Pre", "Steps", "Expected",
            TestProcedureState.Approved, "test.engineer", now, effectiveBaselineId: fixture.BaselineId,
            parentKind: VerificationProcedureParentKind.Allocated);
        var second = new TestProcedureRevision(procedure.Id, 1, "Objective", "Pre", "Steps", "Expected",
            TestProcedureState.Approved, "test.engineer", now, effectiveBaselineId: fixture.BaselineId,
            parentKind: VerificationProcedureParentKind.Allocated);
        db.AddRange(procedure, first, second,
            new TestRequirementCoverage(first.Id, original), new TestRequirementCoverage(second.Id, original));
        await db.SaveChangesAsync();

        var before = await db.TestCoverage.AsNoTracking()
            .Where(x => x.ProcedureRevisionId == second.Id)
            .Select(x => new { x.RequirementRevisionId, x.IsSuspect, x.SuspectReason, x.ConfirmedBy, x.ConfirmedAt })
            .ToListAsync();
        var applied = await new VerificationImpactService(db)
            .ApplyRetargetedCoverageAsync(Resolved(projectId, await db.Releases.Select(x => x.Id).SingleAsync(), procedure.Id, target), now, default);
        Assert.False(applied);
        var after = await db.TestCoverage.AsNoTracking()
            .Where(x => x.ProcedureRevisionId == second.Id)
            .Select(x => new { x.RequirementRevisionId, x.IsSuspect, x.SuspectReason, x.ConfirmedBy, x.ConfirmedAt })
            .ToListAsync();
        Assert.Equal(before, after);
        Assert.Empty(await db.TestCoverage.AsNoTracking()
            .Where(x => x.ProcedureRevisionId == first.Id && x.RequirementRevisionId == target).ToListAsync());
        Assert.True(await db.TestCoverage.AsNoTracking().AnyAsync(x =>
            x.ProcedureRevisionId == first.Id && x.RequirementRevisionId == original));
    }

    [Fact]
    public async Task Retargeting_refuses_an_active_sibling_revision_not_selected_in_the_target_build()
    {
        var fixture = await DatabaseAsync();
        await using var db = fixture.Db;
        var now = DateTimeOffset.UtcNow;
        var siblingArtifact = new RequirementArtifact(fixture.ProjectId, "SYSR-00000152",
            RequirementLevel.System, now);
        var sibling = new RequirementRevision(siblingArtifact.Id, 0, "A sibling-build requirement",
            "This revision is deliberately outside the target manifest.", "Test",
            RequirementRevisionState.Active, (await db.SystemChangeRequests.SingleAsync()).Id,
            fixture.BaselineId, now);
        db.AddRange(siblingArtifact, sibling);
        await db.SaveChangesAsync();

        var procedure = new TestProcedure(fixture.ProjectId, "SYSTP-000042", "Oceanic sequencing",
            "test.engineer", now, TestProcedureLevel.System);
        var revision = new TestProcedureRevision(procedure.Id, 0, "Objective", "Pre", "Steps", "Expected",
            TestProcedureState.Approved, "test.engineer", now, effectiveBaselineId: fixture.BaselineId,
            parentKind: VerificationProcedureParentKind.Allocated);
        db.AddRange(procedure, revision, new TestRequirementCoverage(revision.Id, fixture.OriginalRevisionId));
        await db.SaveChangesAsync();

        var releaseId = await db.Releases.Where(x => x.ProjectId == fixture.ProjectId)
            .Select(x => x.Id).SingleAsync();
        var service = new VerificationImpactService(db);
        Assert.False(await service.IsExactRetargetTargetInBuildAsync(fixture.ProjectId, releaseId,
            procedure.Id, sibling.Id, default));
        Assert.False(await service.ApplyRetargetedCoverageAsync(
            Resolved(fixture.ProjectId, releaseId, procedure.Id, sibling.Id), now, default));
        Assert.DoesNotContain(await db.TestCoverage.AsNoTracking().ToListAsync(),
            x => x.ProcedureRevisionId == revision.Id && x.RequirementRevisionId == sibling.Id);

        var source = await db.SystemChangeRequests.SingleAsync();
        var review = new TestChangeReview(fixture.ProjectId, releaseId, source.Id,
            TestChangeReviewDiscipline.System, source.DisplayNumber, now);
        review.RecordTestChangeRequired("test.engineer", now);
        review.AssignControlledNumber("SYSTPCR-000152", now);
        var item = VerificationImpactItem.ForOrphanedProcedure(fixture.ProjectId, releaseId, source.Id,
            review.Id, procedure.Id, procedure.BaseNumber, now);
        item.Resolve("test.engineer", VerificationImpactOutcome.ProcedureRetargeted,
            "The stale sibling must be refused at submission.", now, procedureChangeAction: TestProcedureChangeAction.ModifyExisting,
            retargetedRequirementRevisionId: sibling.Id);
        db.AddRange(review, item);
        await db.SaveChangesAsync();
        var submitException = await Assert.ThrowsAsync<DomainException>(() =>
            TestChangeReviewRequirementScope.ValidateRetargetPlansForSubmissionAsync(db, review, default));
        Assert.Contains("target build", submitException.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Confirming_an_existing_suspect_target_clears_it_without_adding_a_parent()
    {
        var fixture = await DatabaseAsync();
        await using var db = fixture.Db;
        var now = DateTimeOffset.UtcNow;
        var projectId = fixture.ProjectId;
        var releaseId = await db.Releases.Where(x => x.ProjectId == projectId).Select(x => x.Id).SingleAsync();
        var target = fixture.TargetRevisionId;
        var original = fixture.OriginalRevisionId;

        var procedure = new TestProcedure(projectId, "SYSTP-000042", "Oceanic sequencing", "test.engineer", now,
            TestProcedureLevel.System);
        var revision = new TestProcedureRevision(procedure.Id, 0, "Objective", "Pre", "Steps", "Expected",
            TestProcedureState.Approved, "test.engineer", now, effectiveBaselineId: fixture.BaselineId,
            parentKind: VerificationProcedureParentKind.Allocated);
        db.AddRange(procedure, revision,
            new TestRequirementCoverage(revision.Id, original),
            TestRequirementCoverage.CarriedForward(revision.Id, target, "Retarget awaits confirmation.", now));
        await db.SaveChangesAsync();

        var service = new VerificationImpactService(db);
        await service.ApplyRetargetedCoverageAsync(Resolved(projectId, releaseId, procedure.Id, target), now, default);
        await db.SaveChangesAsync();
        await service.ApplyRetargetedCoverageAsync(Resolved(projectId, releaseId, procedure.Id, target), now, default);
        await db.SaveChangesAsync();
        var confirmed = await db.TestCoverage.AsNoTracking()
            .SingleAsync(x => x.ProcedureRevisionId == revision.Id && x.RequirementRevisionId == target);

        Assert.False(confirmed.IsSuspect);
        Assert.Equal("test.engineer", confirmed.ConfirmedBy);
        Assert.Single(await db.TestCoverage.AsNoTracking().Where(x => x.RequirementRevisionId == target).ToListAsync());
    }

    [Fact]
    public async Task Direct_save_cannot_add_a_non_suspect_parent_to_an_existing_approved_revision()
    {
        var fixture = await DatabaseAsync();
        await using var db = fixture.Db;
        var now = DateTimeOffset.UtcNow;
        var procedure = new TestProcedure(fixture.ProjectId, "SYSTP-000042", "Oceanic sequencing", "test.engineer", now,
            TestProcedureLevel.System);
        var revision = new TestProcedureRevision(procedure.Id, 0, "Objective", "Pre", "Steps", "Expected",
            TestProcedureState.Approved, "test.engineer", now, effectiveBaselineId: fixture.BaselineId,
            parentKind: VerificationProcedureParentKind.Allocated);
        db.AddRange(procedure, revision, new TestRequirementCoverage(revision.Id, fixture.OriginalRevisionId));
        await db.SaveChangesAsync();

        db.TestCoverage.Add(new TestRequirementCoverage(revision.Id, fixture.TargetRevisionId));
        var exception = await Assert.ThrowsAsync<DomainException>(() => db.SaveChangesAsync());
        Assert.Contains("controlled successor", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(await db.TestCoverage.AsNoTracking()
            .Where(x => x.ProcedureRevisionId == revision.Id && x.RequirementRevisionId == fixture.TargetRevisionId)
            .ToListAsync());
    }

    [Fact]
    public async Task Direct_save_cannot_remove_a_non_suspect_parent_from_an_existing_approved_revision()
    {
        var fixture = await DatabaseAsync();
        await using var db = fixture.Db;
        var now = DateTimeOffset.UtcNow;
        var procedure = new TestProcedure(fixture.ProjectId, "SYSTP-000042", "Oceanic sequencing", "test.engineer", now,
            TestProcedureLevel.System);
        var revision = new TestProcedureRevision(procedure.Id, 0, "Objective", "Pre", "Steps", "Expected",
            TestProcedureState.Approved, "test.engineer", now, effectiveBaselineId: fixture.BaselineId,
            parentKind: VerificationProcedureParentKind.Allocated);
        var original = new TestRequirementCoverage(revision.Id, fixture.OriginalRevisionId);
        db.AddRange(procedure, revision, original);
        await db.SaveChangesAsync();

        db.TestCoverage.Remove(original);
        var exception = await Assert.ThrowsAsync<DomainException>(() => db.SaveChangesAsync());
        Assert.Contains("controlled successor", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Coverage_confirmation_defers_a_missing_parent_to_a_controlled_successor()
    {
        var fixture = await DatabaseAsync();
        await using var db = fixture.Db;
        var now = DateTimeOffset.UtcNow;
        var procedure = new TestProcedure(fixture.ProjectId, "SYSTP-000042", "Oceanic sequencing", "test.engineer", now,
            TestProcedureLevel.System);
        var revision = new TestProcedureRevision(procedure.Id, 0, "Objective", "Pre", "Steps", "Expected",
            TestProcedureState.Approved, "test.engineer", now, effectiveBaselineId: fixture.BaselineId,
            parentKind: VerificationProcedureParentKind.Allocated);
        db.AddRange(procedure, revision, new TestRequirementCoverage(revision.Id, fixture.OriginalRevisionId));
        await db.SaveChangesAsync();

        var releaseId = await db.Releases.Where(x => x.ProjectId == fixture.ProjectId).Select(x => x.Id).SingleAsync();
        var item = VerificationImpactItem.ForModifiedRequirement(fixture.ProjectId, releaseId, Guid.NewGuid(), Guid.NewGuid(),
            Guid.NewGuid(), "SYSR-00000151", "Test", now);
        item.LinkRequirementRevision(fixture.TargetRevisionId, now);
        item.Resolve("test.engineer", VerificationImpactOutcome.ProcedureCoverageConfirmed,
            "The existing procedure remains the intended verifier.", now, procedure.Id, revision.Id);

        Assert.False(await new VerificationImpactService(db).ApplyResolvedCoverageAsync(item, now, default));
        Assert.Empty(await db.TestCoverage.AsNoTracking()
            .Where(x => x.ProcedureRevisionId == revision.Id && x.RequirementRevisionId == fixture.TargetRevisionId)
            .ToListAsync());
    }

    [Fact]
    public async Task Confirmation_only_reviews_validate_a_missing_parent_instead_of_returning_early()
    {
        var fixture = await DatabaseAsync();
        await using var db = fixture.Db;
        var now = DateTimeOffset.UtcNow;
        var source = await db.SystemChangeRequests.SingleAsync();
        var releaseId = await db.Releases.Select(x => x.Id).SingleAsync();
        var procedure = new TestProcedure(fixture.ProjectId, "SYSTP-000044", "Confirmation-only procedure",
            "test.engineer", now, TestProcedureLevel.System);
        var revision = new TestProcedureRevision(procedure.Id, 0, "Objective", "Pre", "Steps", "Expected",
            TestProcedureState.Approved, "test.engineer", now, effectiveBaselineId: fixture.BaselineId,
            parentKind: VerificationProcedureParentKind.Allocated);
        db.AddRange(procedure, revision, new TestRequirementCoverage(revision.Id, fixture.OriginalRevisionId));
        await db.SaveChangesAsync();

        var review = new TestChangeReview(fixture.ProjectId, releaseId, source.Id,
            TestChangeReviewDiscipline.System, source.DisplayNumber, now);
        review.RecordTestChangeRequired("test.engineer", now);
        review.AssignControlledNumber("SYSTPCR-000044", now);
        var item = VerificationImpactItem.ForModifiedRequirement(fixture.ProjectId, releaseId, source.Id,
            review.Id, Guid.NewGuid(), "SYSR-00000151", "Test", now);
        item.LinkRequirementRevision(fixture.TargetRevisionId, now);
        item.Resolve("test.engineer", VerificationImpactOutcome.ProcedureCoverageConfirmed,
            "A controlled successor must carry this missing exact parent.", now, procedure.Id, revision.Id);
        db.AddRange(review, item);
        await db.SaveChangesAsync();

        var exception = await Assert.ThrowsAsync<DomainException>(() =>
            TestChangeReviewRequirementScope.ValidateRetargetPlansForSubmissionAsync(db, review, default));
        Assert.Contains("ModifyExisting", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_missing_retarget_link_cannot_submit_as_link_existing_without_a_successor()
    {
        var fixture = await DatabaseAsync();
        await using var db = fixture.Db;
        var now = DateTimeOffset.UtcNow;
        var source = await db.SystemChangeRequests.SingleAsync();
        var procedure = new TestProcedure(fixture.ProjectId, "SYSTP-000043", "Retargeted procedure",
            "test.engineer", now, TestProcedureLevel.System);
        var revision = new TestProcedureRevision(procedure.Id, 0, "Objective", "Pre", "Steps", "Expected",
            TestProcedureState.Approved, "test.engineer", now, effectiveBaselineId: fixture.BaselineId,
            parentKind: VerificationProcedureParentKind.Allocated);
        db.AddRange(procedure, revision,
            new BaselineTestProcedureSelection(fixture.BaselineId, procedure.Id, revision.Id),
            new TestRequirementCoverage(revision.Id, fixture.OriginalRevisionId));
        await db.SaveChangesAsync();

        var review = new TestChangeReview(fixture.ProjectId,
            await db.Releases.Select(x => x.Id).SingleAsync(), source.Id,
            TestChangeReviewDiscipline.System, source.DisplayNumber, now);
        review.RecordTestChangeRequired("test.engineer", now);
        review.AssignControlledNumber("SYSTPCR-000043", now);
        var item = VerificationImpactItem.ForOrphanedProcedure(fixture.ProjectId, review.ReleaseId, source.Id,
            review.Id, procedure.Id, procedure.BaseNumber, now);
        item.Resolve("test.engineer", VerificationImpactOutcome.ProcedureRetargeted,
            "A new controlled successor is required.", now, retargetedRequirementRevisionId: fixture.TargetRevisionId);
        db.AddRange(review, item);
        await db.SaveChangesAsync();

        var exception = await Assert.ThrowsAsync<DomainException>(() =>
            TestChangeReviewRequirementScope.ValidateRetargetPlansForSubmissionAsync(db, review, default));
        Assert.Contains("ModifyExisting", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A decision that is not a move must not quietly create coverage.</summary>
    [Fact]
    public async Task A_retired_procedure_creates_no_new_coverage()
    {
        var fixture = await DatabaseAsync();
        await using var db = fixture.Db;
        var now = DateTimeOffset.UtcNow;
        var item = VerificationImpactItem.ForOrphanedProcedure(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            Guid.NewGuid(), Guid.NewGuid(), "SYSTP-000042", now);
        item.Resolve("test.engineer", VerificationImpactOutcome.ProcedureRetired, "Withdrawn.", now);

        Assert.False(await new VerificationImpactService(db).ApplyRetargetedCoverageAsync(item, now, default));
        Assert.Empty(await db.TestCoverage.AsNoTracking().ToListAsync());
    }
}
