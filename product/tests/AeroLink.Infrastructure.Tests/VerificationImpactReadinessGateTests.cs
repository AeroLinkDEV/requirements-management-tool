using AeroLink.Domain.Baselines;
using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Programs;
using AeroLink.Domain.Releases;
using AeroLink.Domain.Verification;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AeroLink.Infrastructure.Tests;

/// <summary>
/// The verification queue holds back release approval, not the baseline freeze.
///
/// The gate was first written against the freeze endpoint, which deadlocked the workflow: freezing and then
/// materializing is what creates the requirement revisions a test engineer needs before a procedure can be
/// written at all, so blocking the freeze withheld the test team's own inputs. It also had no test, and the
/// journey that caught it only failed once the browser suite could run outside Windows.
/// </summary>
public sealed class VerificationImpactReadinessGateTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    private static async Task<(DbContextOptions<AeroLinkDbContext> Options, Guid CampaignId, Guid ReleaseId, Guid ProjectId, Guid ScrId, string Path)> SeedAsync()
    {
        var path = Path.Combine(Path.GetTempPath(), $"aerolink-vgate-{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<AeroLinkDbContext>().UseSqlite($"Data Source={path};Pooling=False").Options;
        await using var setup = new AeroLinkDbContext(options);
        await setup.Database.EnsureCreatedAsync();
        var program = new ProgramRecord("Gate Program", "GTP");
        var project = new ProjectRecord(program.Id, "Software", "Gate Software");
        var release = new SoftwareRelease(project.Id, "1.6", false);
        var baseline = new CandidateBaseline("BL-00000001", 0, project.Id, release.Id, null, "Gate baseline", "cm", Now);
        var campaign = new ReleaseCampaign(project.Id, release.Id, baseline.Id, "1.6", "program.manager", Now);
        // Impact items carry a real foreign key to the change request that raised them.
        var scr = new SystemChangeRequest("SCR-00000010", 0, project.Id, release.Id, "Oceanic routing", "P", "A", "S", "author", Now);
        scr.AddRequirementChange("author", "SYSR-00000101", 0, RequirementLevel.System, RequirementChangeKind.Introduce,
            "The FMS shall sequence oceanic waypoints.", "New capability", "Test", Now);
        setup.AddRange(program, project, release, baseline, campaign, scr);
        await setup.SaveChangesAsync();
        return (options, campaign.Id, release.Id, project.Id, scr.Id, path);
    }

    private static async Task<ReadinessGate> GateAsync(DbContextOptions<AeroLinkDbContext> options, Guid campaignId)
    {
        await using var db = new AeroLinkDbContext(options);
        var readiness = await new ReleaseReadinessService(db).CalculateAsync(campaignId, default);
        return readiness.Gates.Single(x => x.Code == "verification_impact");
    }

    [Fact]
    public async Task A_release_that_changed_no_requirements_has_nothing_to_decide()
    {
        var seed = await SeedAsync();
        try
        {
            var gate = await GateAsync(seed.Options, seed.CampaignId);
            Assert.True(gate.Complete);
            Assert.Equal(0, gate.Total);
        }
        finally { File.Delete(seed.Path); }
    }

    [Fact]
    public async Task An_undecided_changed_requirement_holds_the_gate_and_names_itself()
    {
        var seed = await SeedAsync();
        try
        {
            await using (var arrange = new AeroLinkDbContext(seed.Options))
            {
                arrange.VerificationImpactItems.Add(VerificationImpactItem.ForIntroducedRequirement(
                    seed.ProjectId, seed.ReleaseId, seed.ScrId, Guid.NewGuid(), "SYSR-00000101.00", "Test", Now));
                await arrange.SaveChangesAsync();
            }

            var gate = await GateAsync(seed.Options, seed.CampaignId);
            Assert.False(gate.Complete);
            Assert.Equal(0, gate.Completed);
            Assert.Equal(1, gate.Total);
            Assert.Contains("SYSR-00000101.00", gate.Detail);
        }
        finally { File.Delete(seed.Path); }
    }

    [Fact]
    public async Task Recording_that_no_test_is_required_satisfies_the_gate()
    {
        var seed = await SeedAsync();
        try
        {
            Guid itemId;
            await using (var arrange = new AeroLinkDbContext(seed.Options))
            {
                // A requirement the author declared verifiable by analysis still needs the verification side
                // to confirm that no test is owed. That confirmation is a decision, so it clears the gate.
                var item = VerificationImpactItem.ForIntroducedRequirement(
                    seed.ProjectId, seed.ReleaseId, seed.ScrId, Guid.NewGuid(), "SYSR-00000104.00", "Analysis", Now);
                arrange.VerificationImpactItems.Add(item);
                await arrange.SaveChangesAsync();
                itemId = item.Id;
            }

            await using (var act = new AeroLinkDbContext(seed.Options))
            {
                var item = await act.VerificationImpactItems.SingleAsync(x => x.Id == itemId);
                item.AssignToEngineer("test.lead", "test.engineer", Now);
                item.Resolve("test.engineer", VerificationImpactOutcome.NoTestRequired,
                    "Verified by analysis of the routing model; no procedure is owed.", Now);
                await act.SaveChangesAsync();
            }

            var gate = await GateAsync(seed.Options, seed.CampaignId);
            Assert.True(gate.Complete);
            Assert.Equal(1, gate.Completed);
            Assert.Equal(1, gate.Total);
        }
        finally { File.Delete(seed.Path); }
    }

    [Fact]
    public async Task Freezing_a_baseline_is_not_held_back_by_an_undecided_item()
    {
        var seed = await SeedAsync();
        try
        {
            await using (var arrange = new AeroLinkDbContext(seed.Options))
            {
                arrange.VerificationImpactItems.Add(VerificationImpactItem.ForIntroducedRequirement(
                    seed.ProjectId, seed.ReleaseId, seed.ScrId, Guid.NewGuid(), "SYSR-00000101.00", "Test", Now));
                await arrange.SaveChangesAsync();
            }

            // Submission and approval are separate units of work, exactly as the endpoints perform them.
            // Doing both against one context would leave the review cycle inserted and approved in the same
            // save, which the change tracker cannot reconcile.
            await using (var submit = new AeroLinkDbContext(seed.Options))
            {
                var scr = await submit.SystemChangeRequests.Include(x => x.RequirementChanges).Include(x => x.ReviewCycles).Include(x => x.AuditEvents).SingleAsync();
                scr.SubmitForReview("author", [new("reviewer", "Reviewer")], Now);
                await submit.SaveChangesAsync();
            }

            await using (var act = new AeroLinkDbContext(seed.Options))
            {
                // Freezing has its own precondition: an approved change must be selected in.
                var scr = await act.SystemChangeRequests.Include(x => x.RequirementChanges).Include(x => x.ReviewCycles).ThenInclude(x => x.Steps).Include(x => x.AuditEvents).SingleAsync();
                scr.ApproveActiveStage("reviewer", Now);
                var baseline = await act.CandidateBaselines.Include(x => x.Events).Include(x => x.Selections).SingleAsync();
                baseline.Select(scr, "cm", Now);

                // The domain owns freezing, and it has no opinion about verification decisions — by design,
                // so that materialization can produce the revisions the test team needs.
                baseline.Freeze("cm", Now);
                await act.SaveChangesAsync();
                Assert.Equal(CandidateBaselineState.Frozen, baseline.State);
            }

            await using (var assert = new AeroLinkDbContext(seed.Options))
            {
                // The same item still holds release approval, which is the gate's job and nothing else's.
                var service = new VerificationImpactService(assert);
                Assert.Single(await service.OutstandingForReleaseAsync(seed.ReleaseId, default));
                var gate = await GateAsync(seed.Options, seed.CampaignId);
                Assert.False(gate.Complete);
            }
        }
        finally { File.Delete(seed.Path); }
    }
}
