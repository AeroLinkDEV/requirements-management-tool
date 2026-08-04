using AeroLink.Domain.Baselines;
using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Programs;
using AeroLink.Domain.Releases;
using AeroLink.Domain.Requirements;
using AeroLink.Domain.Verification;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AeroLink.Infrastructure.Tests;

/// <summary>
/// Deciding that a procedure must be written is an answer, and it is not verification.
///
/// This is the rule the whole outcome depends on. If it were wrong, an engineer could clear every verification
/// item by saying "a procedure is needed" and the release would report itself ready with nothing testing the
/// requirement — which is worse than the gap the outcome was introduced to close, because it would look
/// finished.
///
/// So two things are asserted together, and they pull in opposite directions on purpose: the item counts as
/// decided for the verification-impact gate, and it does not count as coverage for the coverage gate.
/// </summary>
public sealed class NewProcedureRequiredGateTests
{
    private sealed record Fixture(AeroLinkDbContext Db, Guid ProjectId, Guid ReleaseId, Guid CampaignId,
        Guid RequirementRevisionId, VerificationImpactItem Item);

    private static async Task<Fixture> DatabaseAsync()
    {
        var options = new DbContextOptionsBuilder<AeroLinkDbContext>().UseSqlite("Data Source=:memory:").Options;
        var db = new AeroLinkDbContext(options);
        await db.Database.OpenConnectionAsync();
        await db.Database.EnsureCreatedAsync();
        var now = DateTimeOffset.UtcNow;

        var program = new ProgramRecord("Gate Program", "GATE");
        var project = new ProjectRecord(program.Id, "Flight Software", "Gate Software");
        var release = new SoftwareRelease(project.Id, "1.6", false);
        var change = new SystemChangeRequest("SRCR-95001", 0, project.Id, release.Id,
            "Introduce oceanic sequencing", "P", "A", "S", "author", now);
        change.AddRequirementChange("author", "SYSR-950001", 0, RequirementLevel.System, RequirementChangeKind.Introduce,
            "The system shall sequence oceanic waypoints.", "New capability.", "Test", now);
        change.SubmitForReview("author", [new ApproverSelection("reviewer", "Reviewer")], now);
        change.ApproveActiveStage("reviewer", now);

        var baseline = new CandidateBaseline("SW-01.60", 0, project.Id, release.Id, null, "Build 1.6", "cm", now);
        baseline.Select(change, "cm", now);
        baseline.Freeze("cm", now);
        baseline.MarkRequirementsMaterialized("cm", new string('d', 64), 1, now);
        var campaign = new ReleaseCampaign(project.Id, release.Id, baseline.Id, "Build 1.6 release", "release.manager", now);

        var artifact = new RequirementArtifact(project.Id, "SYSR-950001", RequirementLevel.System, now);
        var revision = new RequirementRevision(artifact.Id, 0, "The system shall sequence oceanic waypoints.",
            "New capability.", "Test", RequirementRevisionState.Active, change.Id, baseline.Id, now);

        var review = new TestChangeReview(project.Id, release.Id, change.Id,
            TestChangeReviewDiscipline.System, change.BaseNumber, now, "SYSTCR-950001");
        var item = VerificationImpactItem.ForIntroducedRequirement(project.Id, release.Id, change.Id, review.Id,
            revision.Id, "SYSR-950001.00", "Test", now);

        db.AddRange(program, project, release, change, baseline, campaign, artifact, revision, review, item);
        db.Add(new BaselineRequirementSelection(baseline.Id, artifact.Id, revision.Id));
        await db.SaveChangesAsync();
        return new(db, project.Id, release.Id, campaign.Id, revision.Id, item);
    }

    [Fact]
    public async Task The_decision_counts_as_decided_but_never_as_coverage()
    {
        var fixture = await DatabaseAsync();
        await using var db = fixture.Db;
        var now = DateTimeOffset.UtcNow;
        var service = new VerificationImpactService(db);

        fixture.Item.AssignToEngineer("test.lead", "test.engineer", now);
        fixture.Item.Resolve("test.engineer", VerificationImpactOutcome.NewProcedureRequired,
            "No procedure exists for oceanic sequencing; one must be written.", now);
        // The same call the endpoint makes. It must decline to write a coverage link for this outcome.
        Assert.False(await service.ApplyResolvedCoverageAsync(fixture.Item, now, default));
        await db.SaveChangesAsync();

        Assert.Empty(await db.TestCoverage.Where(x => x.RequirementRevisionId == fixture.RequirementRevisionId).ToListAsync());

        var readiness = await new ReleaseReadinessService(db).CalculateAsync(fixture.CampaignId, default);

        // Decided: the item is no longer work nobody has looked at.
        var decided = Assert.Single(readiness.Gates, x => x.Code == "verification_impact");
        Assert.True(decided.Complete);
        Assert.Equal(1, decided.Completed);

        // But not verified: the requirement still has no settled coverage, so the release keeps waiting.
        var coverage = Assert.Single(readiness.Gates, x => x.Code == "coverage");
        Assert.False(coverage.Complete);
        Assert.Equal(0, coverage.Completed);
        Assert.Equal(1, coverage.Total);
        Assert.False(readiness.ReadyForRelease);
    }

    [Fact]
    public async Task Coverage_only_arrives_once_the_requested_procedure_is_approved()
    {
        var fixture = await DatabaseAsync();
        await using var db = fixture.Db;
        var now = DateTimeOffset.UtcNow;
        var service = new VerificationImpactService(db);

        fixture.Item.AssignToEngineer("test.lead", "test.engineer", now);
        fixture.Item.Resolve("test.engineer", VerificationImpactOutcome.NewProcedureRequired, "One must be written.", now);
        await db.SaveChangesAsync();

        // The procedure the decision asked for is written, approved, and linked to the exact revision.
        var procedure = new TestProcedure(fixture.ProjectId, "SYSTP-950001", "Verify oceanic sequencing",
            "test.engineer", now, TestProcedureLevel.System);
        var revision = new TestProcedureRevision(procedure.Id, 0, "Verify sequencing.", "Load the build.",
            "Exercise the transition.", "Sequencing holds.", TestProcedureState.Approved, "test.engineer", now);
        db.AddRange(procedure, revision, new TestRequirementCoverage(revision.Id, fixture.RequirementRevisionId));
        Assert.True(fixture.Item.SettleWithApprovedProcedure(procedure.Id, revision.Id, now));
        await db.SaveChangesAsync();

        var readiness = await new ReleaseReadinessService(db).CalculateAsync(fixture.CampaignId, default);
        var coverage = Assert.Single(readiness.Gates, x => x.Code == "coverage");
        Assert.True(coverage.Complete);
        Assert.Equal(1, coverage.Completed);
    }
}
