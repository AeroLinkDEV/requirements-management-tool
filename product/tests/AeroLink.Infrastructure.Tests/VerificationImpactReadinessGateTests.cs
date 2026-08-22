using AeroLink.Domain.Baselines;
using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Programs;
using AeroLink.Domain.Requirements;
using AeroLink.Domain.Releases;
using AeroLink.Domain.Traceability;
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

    private static async Task<(DbContextOptions<AeroLinkDbContext> Options, Guid CampaignId, Guid ReleaseId, Guid ProjectId, Guid ChangeRequestId, string Path)> SeedAsync()
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
        var scr = new SystemChangeRequest("SRCR-00010", 0, project.Id, release.Id, "Oceanic routing", "P", "A", "S", "author", Now);
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

    private static async Task<ReadinessGate> TraceGateAsync(DbContextOptions<AeroLinkDbContext> options, Guid campaignId)
    {
        await using var db = new AeroLinkDbContext(options);
        var readiness = await new ReleaseReadinessService(db).CalculateAsync(campaignId, default);
        return readiness.Gates.Single(x => x.Code == "traceability");
    }

    private static VerificationImpactItem AddIntroduced(
        AeroLinkDbContext db,
        (DbContextOptions<AeroLinkDbContext> Options, Guid CampaignId, Guid ReleaseId, Guid ProjectId, Guid ChangeRequestId, string Path) seed,
        string subject, string method)
    {
        var review = new TestChangeReview(seed.ProjectId, seed.ReleaseId, seed.ChangeRequestId,
            TestChangeReviewDiscipline.System, "SRCR-10.00", Now);
        var item = VerificationImpactItem.ForIntroducedRequirement(
            seed.ProjectId, seed.ReleaseId, seed.ChangeRequestId, review.Id, Guid.NewGuid(), subject, method, Now);
        db.AddRange(review, item);
        return item;
    }

    [Fact]
    public async Task Downstream_assurance_gates_wait_for_the_materialized_baseline_prerequisite()
    {
        var seed = await SeedAsync();
        try
        {
            await using var db = new AeroLinkDbContext(seed.Options);
            var readiness = await new ReleaseReadinessService(db).CalculateAsync(seed.CampaignId, default);
            var downstream = readiness.Gates.Where(x =>
                x.Code is "traceability" or "coverage" or "verification" or "evidence").ToList();

            Assert.Equal(4, downstream.Count);
            Assert.All(downstream, gate =>
            {
                Assert.False(gate.Complete);
                Assert.Equal("WaitingForPrerequisite", gate.EvaluationState);
                Assert.Equal("baseline", gate.PrerequisiteCode);
                Assert.Equal(0, gate.Completed);
                Assert.Equal(0, gate.Total);
                Assert.Contains("Waiting for a materialized baseline", gate.Detail);
                Assert.Contains("Requirement baseline materialized", gate.Action);
            });
        }
        finally { File.Delete(seed.Path); }
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
    public async Task Release_readiness_counts_only_hlr_and_llr_trace_obligations_but_all_baseline_revisions_for_coverage()
    {
        var seed = await SeedAsync();
        try
        {
            await using (var arrange = new AeroLinkDbContext(seed.Options))
            {
                var system = new RequirementArtifact(seed.ProjectId, "SYSR-00000101", RequirementLevel.System, Now);
                var high = new RequirementArtifact(seed.ProjectId, "HLR-00000102", RequirementLevel.HighLevel, Now);
                var low = new RequirementArtifact(seed.ProjectId, "LLR-00000103", RequirementLevel.LowLevel, Now);
                var systemRevision = new RequirementRevision(system.Id, 0, "The system shall route safely.", "R", "Test",
                    RequirementRevisionState.Active, seed.ChangeRequestId, (await arrange.CandidateBaselines.SingleAsync()).Id, Now);
                var highRevision = new RequirementRevision(high.Id, 0, "The software shall route safely.", "R", "Test",
                    RequirementRevisionState.Active, seed.ChangeRequestId, (await arrange.CandidateBaselines.SingleAsync()).Id, Now);
                var lowRevision = new RequirementRevision(low.Id, 0, "The implementation shall route safely.", "R", "Test",
                    RequirementRevisionState.Active, seed.ChangeRequestId, (await arrange.CandidateBaselines.SingleAsync()).Id, Now);
                var systemProcedure = new TestProcedure(seed.ProjectId, "SYSTP-00000101", "Verify system routing", "test.engineer", Now,
                    TestProcedureLevel.System);
                var highProcedure = new TestProcedure(seed.ProjectId, "HLRTP-00000102", "Verify HLR routing", "test.engineer", Now,
                    TestProcedureLevel.HighLevel);
                var lowProcedure = new TestProcedure(seed.ProjectId, "LLRTP-00000103", "Verify LLR routing", "test.engineer", Now,
                    TestProcedureLevel.LowLevel);
                var systemProcedureRevision = new TestProcedureRevision(systemProcedure.Id, 0, "Purpose", "Configuration",
                    "Steps", "Expected", TestProcedureState.Approved, "test.engineer", Now);
                var highProcedureRevision = new TestProcedureRevision(highProcedure.Id, 0, "Purpose", "Configuration",
                    "Steps", "Expected", TestProcedureState.Approved, "test.engineer", Now);
                var lowProcedureRevision = new TestProcedureRevision(lowProcedure.Id, 0, "Purpose", "Configuration",
                    "Steps", "Expected", TestProcedureState.Approved, "test.engineer", Now);
                var baseline = await arrange.CandidateBaselines.SingleAsync();
                arrange.AddRange(system, high, low, systemRevision, highRevision, lowRevision,
                    systemProcedure, highProcedure, lowProcedure,
                    systemProcedureRevision, highProcedureRevision, lowProcedureRevision);
                arrange.BaselineRequirements.AddRange(
                    new BaselineRequirementSelection(baseline.Id, system.Id, systemRevision.Id),
                    new BaselineRequirementSelection(baseline.Id, high.Id, highRevision.Id),
                    new BaselineRequirementSelection(baseline.Id, low.Id, lowRevision.Id));
                arrange.RequirementTraces.AddRange(
                    new RequirementTraceLink(seed.ProjectId, highRevision.Id, systemRevision.Id,
                        RequirementTraceType.DerivedFrom, "HLR derives from System.", Now),
                    new RequirementTraceLink(seed.ProjectId, lowRevision.Id, highRevision.Id,
                        RequirementTraceType.AllocatedFrom, "LLR is allocated from HLR.", Now));
                arrange.TestCoverage.AddRange(
                    new TestRequirementCoverage(systemProcedureRevision.Id, systemRevision.Id),
                    new TestRequirementCoverage(highProcedureRevision.Id, highRevision.Id),
                    new TestRequirementCoverage(lowProcedureRevision.Id, lowRevision.Id));
                await arrange.SaveChangesAsync();
                await arrange.CandidateBaselines.Where(x => x.Id == baseline.Id).ExecuteUpdateAsync(update => update
                    .SetProperty(x => x.State, CandidateBaselineState.Frozen)
                    .SetProperty(x => x.RequirementsMaterializedAt, Now));
            }

            await using var assertDb = new AeroLinkDbContext(seed.Options);
            var readiness = await new ReleaseReadinessService(assertDb).CalculateAsync(seed.CampaignId, default);
            var traceability = readiness.Gates.Single(x => x.Code == "traceability");
            var coverage = readiness.Gates.Single(x => x.Code == "coverage");
            Assert.True(traceability.Complete);
            Assert.Equal(2, traceability.Completed);
            Assert.Equal(2, traceability.Total);
            Assert.True(coverage.Complete);
            Assert.Equal(3, coverage.Completed);
            Assert.Equal(3, coverage.Total);
        }
        finally { File.Delete(seed.Path); }
    }

    [Fact]
    public async Task Suspect_trace_blocks_its_exact_baseline_until_discharged_and_does_not_backpatch_another_baseline()
    {
        var seed = await SeedAsync();
        try
        {
            Guid secondCampaignId, traceId;
            await using (var arrange = new AeroLinkDbContext(seed.Options))
            {
                var baseline = await arrange.CandidateBaselines.SingleAsync();
                var source = new RequirementArtifact(seed.ProjectId, "HLR-00000101", RequirementLevel.HighLevel, Now);
                var target = new RequirementArtifact(seed.ProjectId, "SYSR-00000101", RequirementLevel.System, Now);
                var sourceRevision = new RequirementRevision(source.Id, 0, "The software shall navigate.", "R", "Test",
                    RequirementRevisionState.Active, seed.ChangeRequestId, baseline.Id, Now);
                var targetRevision = new RequirementRevision(target.Id, 0, "The system shall navigate.", "R", "Test",
                    RequirementRevisionState.Active, seed.ChangeRequestId, baseline.Id, Now);
                var link = new RequirementTraceLink(seed.ProjectId, sourceRevision.Id, targetRevision.Id,
                    RequirementTraceType.DerivedFrom, "The software derives from the system.", Now);
                var lifecycle = ExactLinkSuspectLifecycle.Raise(seed.ProjectId, ExactLinkKind.RequirementTrace, link.Id,
                    ExactLinkLifecycleCauseKind.InternalRequirementRevision, targetRevision.Id, null,
                    "author", "The exact upstream revision changed.", Now);
                link.AttachExactLinkLifecycle(lifecycle.Id);
                traceId = link.Id;
                arrange.AddRange(source, target, sourceRevision, targetRevision, link);
                arrange.BaselineRequirements.AddRange(
                    new BaselineRequirementSelection(baseline.Id, source.Id, sourceRevision.Id),
                    new BaselineRequirementSelection(baseline.Id, target.Id, targetRevision.Id));
                arrange.ExactLinkSuspectLifecycles.Add(lifecycle);
                arrange.ExactLinkSuspectEvents.AddRange(lifecycle.Events);

                // Campaigns are unique per project/release; a second release gives the isolation check a
                // genuinely separate candidate while retaining the same project-level trace history.
                var isolatedRelease = new SoftwareRelease(seed.ProjectId, "1.7", false);
                var isolated = new CandidateBaseline("BL-00000002", 0, seed.ProjectId, isolatedRelease.Id, null,
                    "Isolated baseline", "cm", Now);
                var isolatedCampaign = new ReleaseCampaign(seed.ProjectId, isolatedRelease.Id, isolated.Id,
                    "1.6 isolated", "program.manager", Now);
                arrange.AddRange(isolatedRelease, isolated, isolatedCampaign);
                arrange.BaselineRequirements.Add(new BaselineRequirementSelection(isolated.Id, target.Id, targetRevision.Id));
                await arrange.SaveChangesAsync();
                await arrange.CandidateBaselines.Where(x => x.Id == baseline.Id || x.Id == isolated.Id)
                    .ExecuteUpdateAsync(update => update
                        .SetProperty(x => x.State, CandidateBaselineState.Frozen)
                        .SetProperty(x => x.RequirementsMaterializedAt, Now)
                        .SetProperty(x => x.RequirementsHash, "test"));
                Assert.Equal(ExactLinkLifecycleState.Suspect,
                    await arrange.ExactLinkSuspectLifecycles.Select(x => x.State).SingleAsync());
                secondCampaignId = isolatedCampaign.Id;
            }

            var blocked = await TraceGateAsync(seed.Options, seed.CampaignId);
            Assert.False(blocked.Complete);
            Assert.Equal(1, blocked.Completed);
            Assert.Equal(2, blocked.Total);
            Assert.Contains("suspect", blocked.Detail, StringComparison.OrdinalIgnoreCase);

            await using (var acknowledge = new AeroLinkDbContext(seed.Options))
                await new ExactLinkLifecycleService(acknowledge).AcknowledgeAsync(traceId, "reviewer",
                    "The downstream impact assessment is in progress.", Now.AddSeconds(1), default);
            var acknowledged = await TraceGateAsync(seed.Options, seed.CampaignId);
            Assert.False(acknowledged.Complete);
            Assert.Equal(2, acknowledged.Total);
            Assert.Contains("acknowledged", acknowledged.Detail, StringComparison.OrdinalIgnoreCase);

            var isolatedGate = await TraceGateAsync(seed.Options, secondCampaignId);
            Assert.True(isolatedGate.Complete);
            Assert.Equal(0, isolatedGate.Total);

            await using (var resolve = new AeroLinkDbContext(seed.Options))
            {
                var lifecycle = await resolve.ExactLinkSuspectLifecycles.Include(x => x.Events).SingleAsync();
                lifecycle.RecordResolution(ExactLinkResolutionOutcome.ExistingDownstreamRevisionRemainsValid,
                    "reviewer", "The existing downstream revision remains valid.", Now.AddMinutes(1));
                resolve.ExactLinkSuspectEvents.Add(lifecycle.Events.Last());
                await resolve.SaveChangesAsync();
            }

            var discharged = await TraceGateAsync(seed.Options, seed.CampaignId);
            Assert.True(discharged.Complete);
            Assert.Equal(1, discharged.Completed);
            Assert.Equal(1, discharged.Total);
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
                AddIntroduced(arrange, seed, "SYSR-00000101.00", "Test");
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
                var item = AddIntroduced(arrange, seed, "SYSR-00000104.00", "Analysis");
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
                AddIntroduced(arrange, seed, "SYSR-00000101.00", "Test");
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

    [Fact]
    public async Task Latest_same_release_no_build_execution_controls_readiness_over_an_older_pass()
    {
        var seed = await SeedAsync();
        try
        {
            Guid procedureRevisionId;
            await using (var arrange = new AeroLinkDbContext(seed.Options))
            {
                var baseline = await arrange.CandidateBaselines.SingleAsync();
                var procedure = new TestProcedure(seed.ProjectId, "SYSTP-00000101", "Verify oceanic routing",
                    "test.engineer", Now, TestProcedureLevel.System);
                var revision = new TestProcedureRevision(procedure.Id, 0, "Verify routing", "On ground",
                    "Run the routing sequence", "The sequence is accepted", TestProcedureState.Approved,
                    "test.engineer", Now);
                var set = new BuildTestSet(seed.ProjectId, seed.ReleaseId, TestChangeReviewDiscipline.System, Now);
                set.Include("test.lead", revision.Id, TestSelectionReason.ChangedRequirement,
                    "The selected release must exercise oceanic routing.", Now);
                arrange.AddRange(procedure, revision, set);
                arrange.TestExecutions.AddRange(
                    new TestExecution(seed.ProjectId, revision.Id, null, null, TestOutcome.Pass, "test.engineer",
                        "rig", "Older same-release result.", "evidence/older-pass.json", Now.AddMinutes(-5), Now.AddMinutes(-5), seed.ReleaseId),
                    new TestExecution(seed.ProjectId, revision.Id, null, null, TestOutcome.Fail, "test.engineer",
                        "rig", "Newer same-release result.", "evidence/newer-fail.json", Now.AddMinutes(-1), Now.AddMinutes(-1), seed.ReleaseId));
                await arrange.SaveChangesAsync();
                await arrange.CandidateBaselines.Where(x => x.Id == baseline.Id).ExecuteUpdateAsync(update => update
                    .SetProperty(x => x.State, CandidateBaselineState.Frozen)
                    .SetProperty(x => x.RequirementsMaterializedAt, Now));
                procedureRevisionId = revision.Id;
            }

            await using var assertDb = new AeroLinkDbContext(seed.Options);
            var latest = await ExecutionScope.LatestByProcedureAsync(assertDb, [procedureRevisionId], seed.ReleaseId,
                null, default);
            var selected = Assert.Single(latest).Value;
            Assert.Equal(TestOutcome.Fail, selected.Outcome);
            Assert.Equal("Newer same-release result.", selected.Determination);

            var readiness = await new ReleaseReadinessService(assertDb).CalculateAsync(seed.CampaignId, default);
            var verification = readiness.Gates.Single(x => x.Code == "verification");
            Assert.False(verification.Complete);
            Assert.Equal(0, verification.Completed);
            Assert.Equal(1, verification.Total);
        }
        finally { File.Delete(seed.Path); }
    }

    private static async Task<ReadinessGate> CoverageGateAsync(DbContextOptions<AeroLinkDbContext> options, Guid campaignId)
    {
        await using var db = new AeroLinkDbContext(options);
        var readiness = await new ReleaseReadinessService(db).CalculateAsync(campaignId, default);
        return readiness.Gates.Single(x => x.Code == "coverage");
    }

    /// <summary>
    /// A procedure under change has to be modified, reviewed and approved before anything relying on it counts
    /// as approved. Coverage naming a draft procedure revision used to count as covered, and a procedure with a
    /// revision in flight still counted on the strength of its superseded revision.
    /// </summary>
    [Fact]
    public async Task Coverage_does_not_count_while_the_procedure_it_names_is_being_changed()
    {
        var seed = await SeedAsync();
        try
        {
            Guid procedureId, approvedRevisionId;
            await using (var arrange = new AeroLinkDbContext(seed.Options))
            {
                var baseline = await arrange.CandidateBaselines.SingleAsync();
                var artifact = new RequirementArtifact(seed.ProjectId, "SYSR-00000101", RequirementLevel.System, Now);
                var revision = new RequirementRevision(artifact.Id, 0, "The FMS shall sequence oceanic waypoints.",
                    "R", "Test", RequirementRevisionState.Active, seed.ChangeRequestId, baseline.Id, Now);
                var procedure = new TestProcedure(seed.ProjectId, "TP-00000001", "Oceanic sequencing", "test.lead", Now);
                var approved = new TestProcedureRevision(procedure.Id, 0, "Verify sequencing", "On ground",
                    "Sequence", "Sequenced", TestProcedureState.Approved, "test.engineer", Now);
                arrange.AddRange(artifact, revision, procedure, approved);
                arrange.BaselineRequirements.Add(new BaselineRequirementSelection(baseline.Id, artifact.Id, revision.Id));
                arrange.TestCoverage.Add(new TestRequirementCoverage(approved.Id, revision.Id));
                await arrange.SaveChangesAsync();
                await arrange.CandidateBaselines.Where(x => x.Id == baseline.Id).ExecuteUpdateAsync(update => update
                    .SetProperty(x => x.State, CandidateBaselineState.Frozen)
                    .SetProperty(x => x.RequirementsMaterializedAt, Now));
                procedureId = procedure.Id; approvedRevisionId = approved.Id;
            }

            var settled = await CoverageGateAsync(seed.Options, seed.CampaignId);
            Assert.Equal(1, settled.Completed);

            // Someone starts revising the procedure. Nothing about the requirement or the link changed.
            await using (var revise = new AeroLinkDbContext(seed.Options))
            {
                revise.TestProcedureRevisions.Add(new TestProcedureRevision(procedureId, 1, "Verify sequencing",
                    "On ground", "Sequence more carefully", "Sequenced", TestProcedureState.Draft, "test.engineer", Now));
                await revise.SaveChangesAsync();
            }

            var inFlight = await CoverageGateAsync(seed.Options, seed.CampaignId);
            Assert.Equal(0, inFlight.Completed);
            Assert.False(inFlight.Complete);

            // The revision becoming Approved settles it again. In the product that happens at materialisation,
            // on the authority of the test change request that carried the change — there is no separate
            // signature on the revision — so the fixture states the outcome rather than performing a step
            // that no longer exists. What this test is about is the gate, not how the state was reached.
            await using (var approve = new AeroLinkDbContext(seed.Options))
            {
                await approve.TestProcedureRevisions
                    .Where(x => x.ProcedureId == procedureId && x.Revision == 1)
                    .ExecuteUpdateAsync(update => update.SetProperty(x => x.State, TestProcedureState.Approved));
            }

            var reapproved = await CoverageGateAsync(seed.Options, seed.CampaignId);
            Assert.Equal(1, reapproved.Completed);
            Assert.NotEqual(Guid.Empty, approvedRevisionId);
        }
        finally { File.Delete(seed.Path); }
    }
}
