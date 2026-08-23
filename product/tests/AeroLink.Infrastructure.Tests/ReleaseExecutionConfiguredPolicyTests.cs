using System.Text.Json;
using AeroLink.Domain.Baselines;
using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Hierarchy;
using AeroLink.Domain.Programs;
using AeroLink.Domain.Releases;
using AeroLink.Domain.Requirements;
using AeroLink.Domain.Verification;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AeroLink.Infrastructure.Tests;

public sealed class ReleaseExecutionConfiguredPolicyTests
{
    [Fact]
    public async Task Active_system_and_low_level_subset_excludes_retained_high_level_release_evidence()
    {
        var options = new DbContextOptionsBuilder<AeroLinkDbContext>()
            .UseSqlite("Data Source=:memory:").Options;
        await using var db = new AeroLinkDbContext(options);
        await db.Database.OpenConnectionAsync();
        await db.Database.EnsureCreatedAsync();
        var now = new DateTimeOffset(2026, 8, 21, 14, 0, 0, TimeSpan.Zero);

        var program = new ProgramRecord("Configured release program", "CRP");
        var project = new ProjectRecord(program.Id, "Configured release project", "Configured release software");
        var release = new SoftwareRelease(project.Id, "2.0", false);
        var predecessor = new CandidateBaseline("BL-00020", 0, project.Id, release.Id, null,
            "Predecessor", "release.test", now);
        var baseline = new CandidateBaseline("BL-00021", 0, project.Id, release.Id, predecessor.Id,
            "Subset release", "release.test", now);
        var campaign = new ReleaseCampaign(project.Id, release.Id, baseline.Id,
            "Configured subset campaign", "release.manager", now);
        var scr = new SystemChangeRequest("SRCR-00020", 0, project.Id, release.Id,
            "Subset release authority", "Problem", "Analysis", "Solution", "author", now);
        scr.AddRequirementChange("author", "SYSR-00020", 0, RequirementLevel.System,
            RequirementChangeKind.Introduce, "The configured release behavior shall be controlled.",
            "The subset release test needs one selected authority.", "Test", now);
        scr.SubmitForReview("author", [new ApproverSelection("approver", "Approver")], now);
        scr.ApproveActiveStage("approver", now);
        baseline.Select(scr, "release.test", now);
        baseline.Freeze("release.test", now);
        baseline.MarkRequirementsMaterialized("release.test", new string('a', 64), 3, now);
        campaign.StartVerification("release.manager", now);

        var system = new RequirementArtifact(project.Id, "SYSR-00020", RequirementLevel.System, now);
        var high = new RequirementArtifact(project.Id, "HLR-00021", RequirementLevel.HighLevel, now);
        var low = new RequirementArtifact(project.Id, "LLR-00022", RequirementLevel.LowLevel, now);
        var predecessorSystem = new RequirementRevision(system.Id, 0, "Predecessor system wording", "R", "Test",
            RequirementRevisionState.Active, scr.Id, predecessor.Id, now);
        var predecessorHigh = new RequirementRevision(high.Id, 0, "Predecessor high-level wording", "R", "Test",
            RequirementRevisionState.Active, scr.Id, predecessor.Id, now,
            parentKind: RequirementParentKind.Derived, derivedRationale: "This release execution fixture has no authored upstream selection.");
        var predecessorLow = new RequirementRevision(low.Id, 0, "Predecessor low-level wording", "R", "Test",
            RequirementRevisionState.Active, scr.Id, predecessor.Id, now,
            parentKind: RequirementParentKind.Derived, derivedRationale: "This release execution fixture has no authored upstream selection.");
        var currentSystem = new RequirementRevision(system.Id, 1, "Current system wording", "R", "Test",
            RequirementRevisionState.Active, scr.Id, baseline.Id, now);
        var currentHigh = new RequirementRevision(high.Id, 1, "Current retained high-level wording", "R", "Test",
            RequirementRevisionState.Active, scr.Id, baseline.Id, now,
            parentKind: RequirementParentKind.Derived, derivedRationale: "This release execution fixture has no authored upstream selection.");
        var currentLow = new RequirementRevision(low.Id, 1, "Current low-level wording", "R", "Test",
            RequirementRevisionState.Active, scr.Id, baseline.Id, now,
            parentKind: RequirementParentKind.Derived, derivedRationale: "This release execution fixture has no authored upstream selection.");

        var systemProcedure = new TestProcedure(project.Id, "SYSTP-00020", "Configured system procedure",
            "verification", now, TestProcedureLevel.System);
        var highProcedure = new TestProcedure(project.Id, "HLRTC-00021", "Retained high-level procedure",
            "verification", now, TestProcedureLevel.HighLevel);
        var lowProcedure = new TestProcedure(project.Id, "LLRTC-00022", "Configured low-level procedure",
            "verification", now, TestProcedureLevel.LowLevel);
        var systemProcedureRevision = new TestProcedureRevision(systemProcedure.Id, 0, "System objective", "Pre",
            "System steps", "System result", TestProcedureState.Approved, "verification", now,
            effectiveBaselineId: baseline.Id);
        var highProcedureRevision = new TestProcedureRevision(highProcedure.Id, 0, "High objective", "Pre",
            "High steps", "High result", TestProcedureState.Approved, "verification", now,
            effectiveBaselineId: baseline.Id);
        var lowProcedureRevision = new TestProcedureRevision(lowProcedure.Id, 0, "Low objective", "Pre",
            "Low steps", "Low result", TestProcedureState.Approved, "verification", now,
            effectiveBaselineId: baseline.Id);

        db.AddRange(program, project, release, predecessor, baseline, campaign, scr,
            system, high, low, predecessorSystem, predecessorHigh, predecessorLow,
            currentSystem, currentHigh, currentLow,
            systemProcedure, highProcedure, lowProcedure,
            systemProcedureRevision, highProcedureRevision, lowProcedureRevision,
            new TestRequirementCoverage(systemProcedureRevision.Id, currentSystem.Id),
            new TestRequirementCoverage(highProcedureRevision.Id, currentHigh.Id),
            new TestRequirementCoverage(lowProcedureRevision.Id, currentLow.Id),
            // The context's persistence guard needs a project ladder. The service under test receives the
            // active subset resolver below, while the stored legacy row keeps same-level fixture writes valid.
            LegacyDefaultProjectLadderFactory.Create(project.Id, now));
        db.BaselineRequirements.AddRange(
            new BaselineRequirementSelection(predecessor.Id, system.Id, predecessorSystem.Id),
            new BaselineRequirementSelection(predecessor.Id, high.Id, predecessorHigh.Id),
            new BaselineRequirementSelection(predecessor.Id, low.Id, predecessorLow.Id),
            new BaselineRequirementSelection(baseline.Id, system.Id, currentSystem.Id),
            new BaselineRequirementSelection(baseline.Id, high.Id, currentHigh.Id),
            new BaselineRequirementSelection(baseline.Id, low.Id, currentLow.Id));
        await db.SaveChangesAsync();

        var build = new SoftwareBuild(project.Id, release.Id, baseline.Id, "SW-02.00",
            "Configured subset build", "build.engineer", now);
        db.SoftwareBuilds.Add(build);
        campaign.SelectVerificationBuild(build.Id, "release.manager", now);
        await db.SaveChangesAsync();

        var policy = SubsetPolicy(project.Id, now);
        var evidenceRoot = Path.Combine(Path.GetTempPath(), $"aerolink-configured-release-{Guid.NewGuid():N}");
        try
        {
            var execution = new ReleaseExecutionService(db, new EvidenceFileStore(evidenceRoot),
                new FixedProjectLadderPolicyResolver(policy));

            var template = JsonSerializer.Deserialize<List<VerificationManifestRow>>(
                await execution.CreateVerificationTemplateAsync(campaign.Id, default))!;
            Assert.Equal(2, template.Count);
            Assert.Contains(template, x => x.ProcedureRevisionId == systemProcedureRevision.Id);
            Assert.Contains(template, x => x.ProcedureRevisionId == lowProcedureRevision.Id);
            Assert.DoesNotContain(template, x => x.ProcedureRevisionId == highProcedureRevision.Id);

            var reconciliation = await execution.ReconcileAsync(campaign.Id, "release.assurance", now, default);
            Assert.Equal(0, reconciliation.UncoveredRequirements);
            Assert.Equal(0, reconciliation.SuspectCoverage);

            var readiness = await new ReleaseReadinessService(db,
                policyResolver: new FixedProjectLadderPolicyResolver(policy))
                .CalculateAsync(campaign.Id, default);
            var coverageGate = readiness.Gates.Single(x => x.Code == "coverage");
            Assert.True(coverageGate.Complete);
            Assert.Equal(2, coverageGate.Completed);
            Assert.Equal(2, coverageGate.Total);

            var initialHash = await execution.ComputeReviewManifestHashAsync(campaign.Id, default);

            var laterHighProcedure = new TestProcedure(project.Id, "HLRTC-00023",
                "Retained high-level procedure added later", "verification", now, TestProcedureLevel.HighLevel);
            var laterHighRevision = new TestProcedureRevision(laterHighProcedure.Id, 0, "Later high objective", "Pre",
                "Later high steps", "Later high result", TestProcedureState.Approved, "verification", now,
                effectiveBaselineId: baseline.Id);
            db.AddRange(laterHighProcedure, laterHighRevision,
                new TestRequirementCoverage(laterHighRevision.Id, currentHigh.Id));
            await db.SaveChangesAsync();
            Assert.Equal(initialHash, await execution.ComputeReviewManifestHashAsync(campaign.Id, default));

            var laterLowProcedure = new TestProcedure(project.Id, "LLRTC-00024",
                "Configured low-level procedure added later", "verification", now, TestProcedureLevel.LowLevel);
            var laterLowRevision = new TestProcedureRevision(laterLowProcedure.Id, 0, "Later low objective", "Pre",
                "Later low steps", "Later low result", TestProcedureState.Approved, "verification", now,
                effectiveBaselineId: baseline.Id);
            db.AddRange(laterLowProcedure, laterLowRevision,
                new TestRequirementCoverage(laterLowRevision.Id, currentLow.Id));
            await db.SaveChangesAsync();
            Assert.NotEqual(initialHash, await execution.ComputeReviewManifestHashAsync(campaign.Id, default));
        }
        finally
        {
            if (Directory.Exists(evidenceRoot)) Directory.Delete(evidenceRoot, true);
        }
    }

    [Fact]
    public async Task Readiness_does_not_count_a_removed_level_procedure_as_current_coverage()
    {
        var options = new DbContextOptionsBuilder<AeroLinkDbContext>()
            .UseSqlite("Data Source=:memory:").Options;
        await using var db = new AeroLinkDbContext(options);
        await db.Database.OpenConnectionAsync();
        await db.Database.EnsureCreatedAsync();
        var now = new DateTimeOffset(2026, 8, 21, 15, 0, 0, TimeSpan.Zero);
        var program = new ProgramRecord("Readiness policy program", "RPP");
        var project = new ProjectRecord(program.Id, "Readiness policy project", "Readiness policy software");
        var release = new SoftwareRelease(project.Id, "3.0", false);
        var baseline = new CandidateBaseline("BL-00030", 0, project.Id, release.Id, null,
            "Readiness policy baseline", "release.test", now);
        var campaign = new ReleaseCampaign(project.Id, release.Id, baseline.Id,
            "Readiness policy campaign", "release.manager", now);
        var changeRequest = new SystemChangeRequest("SRCR-00030", 0, project.Id, release.Id,
            "Readiness policy change", "Problem", "Analysis", "Solution", "author", now);
        changeRequest.AddRequirementChange("author", "SYSR-00030", 0, RequirementLevel.System, RequirementChangeKind.Introduce,
            "The system requirement remains current.", "Rationale", "Test", now);
        changeRequest.SubmitForReview("author", [new ApproverSelection("approver", "Approver")], now);
        changeRequest.ApproveActiveStage("approver", now);
        baseline.Select(changeRequest, "release.test", now);
        baseline.Freeze("release.test", now);
        baseline.MarkRequirementsMaterialized("release.test", new string('b', 64), 1, now);
        var system = new RequirementArtifact(project.Id, "SYSR-00030", RequirementLevel.System, now);
        var systemRevision = new RequirementRevision(system.Id, 0, "The system requirement remains current.",
            "Rationale", "Test", RequirementRevisionState.Active, changeRequest.Id, baseline.Id, now);
        var removedProcedure = new TestProcedure(project.Id, "HLRTC-00030", "Removed HLR procedure",
            "verification", now, TestProcedureLevel.HighLevel);
        var removedRevision = new TestProcedureRevision(removedProcedure.Id, 0, "Removed objective", "Pre",
            "Removed steps", "Removed result", TestProcedureState.Approved, "verification", now,
            effectiveBaselineId: baseline.Id);
        db.AddRange(program, project, release, baseline, campaign, changeRequest, system, systemRevision,
            removedProcedure, removedRevision,
            new BaselineRequirementSelection(baseline.Id, system.Id, systemRevision.Id),
            LegacyDefaultProjectLadderFactory.Create(project.Id, now));
        await db.SaveChangesAsync();

        var readiness = await new ReleaseReadinessService(db,
            policyResolver: new FixedProjectLadderPolicyResolver(SystemOnlyPolicy(project.Id, now)))
            .CalculateAsync(campaign.Id, default);
        var coverage = readiness.Gates.Single(x => x.Code == "coverage");
        Assert.False(coverage.Complete);
        Assert.Equal(0, coverage.Completed);
        Assert.Equal(1, coverage.Total);
    }

    private static ILadderPolicy SubsetPolicy(Guid projectId, DateTimeOffset now)
    {
        var configuration = ProjectLadderConfiguration.CreateDraft(projectId, now);
        var system = new ProjectLadderStep(configuration.Id, projectId, RequirementLevel.System, 1,
            LegacyLadderPolicy.Instance.Definition(RequirementLevel.System).Capabilities, now);
        var low = new ProjectLadderStep(configuration.Id, projectId, RequirementLevel.LowLevel, 2,
            LegacyLadderPolicy.Instance.Definition(RequirementLevel.LowLevel).Capabilities, now);
        configuration.Steps.Add(system);
        configuration.Steps.Add(low);
        configuration.AllowedUpstream.Add(new ProjectLadderAllowedUpstream(
            configuration.Id, projectId, system.Id, low.Id, now));
        return new ResolvedProjectLadderPolicy(ProjectLadderResolver.Resolve(configuration));
    }

    private static ILadderPolicy SystemOnlyPolicy(Guid projectId, DateTimeOffset now)
    {
        var configuration = ProjectLadderConfiguration.CreateDraft(projectId, now);
        configuration.Steps.Add(new ProjectLadderStep(configuration.Id, projectId, RequirementLevel.System, 1,
            LegacyLadderPolicy.Instance.Definition(RequirementLevel.System).Capabilities, now));
        return new ResolvedProjectLadderPolicy(ProjectLadderResolver.Resolve(configuration));
    }
}
