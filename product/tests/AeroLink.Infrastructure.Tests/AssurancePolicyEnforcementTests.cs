using AeroLink.Domain.Assurance;
using AeroLink.Domain.Baselines;
using AeroLink.Domain.Hierarchy;
using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Programs;
using AeroLink.Domain.Releases;
using AeroLink.Domain.Requirements;
using AeroLink.Domain.Verification;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AeroLink.Infrastructure.Tests;

/// <summary>
/// Every shipped assurance lever, proved at the enforcement point it names.
///
/// #711 refuses to ship a lever nothing consumes. The way to keep that honest is a test per lever that sets
/// the project's selection and watches the gate change — one seed that fails four gates on the AeroLink
/// recommendations, then the same seed under a recorded policy.
/// </summary>
public sealed class AssurancePolicyEnforcementTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    private sealed record Seed(DbContextOptions<AeroLinkDbContext> Options, Guid ProjectId, Guid CampaignId, string Path);

    /// <summary>
    /// A release that would fail coverage, evidence, impact disposition and — but for its waiver —
    /// problem-report blockers. Every lever's effect is then a single visible change against this fixture.
    /// </summary>
    private static async Task<Seed> SeedAsync(
        IReadOnlyDictionary<AssurancePolicyLever, AssuranceLeverValue>? selections = null)
    {
        var path = Path.Combine(Path.GetTempPath(), $"aerolink-assurance-{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<AeroLinkDbContext>().UseSqlite($"Data Source={path};Pooling=False").Options;
        await using var db = new AeroLinkDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var program = new ProgramRecord("Assurance Program", "ASP");
        var project = new ProjectRecord(program.Id, "Assurance", "Assurance Software");
        var release = new SoftwareRelease(project.Id, "2.0", false);
        var baseline = new CandidateBaseline("BL-00000001", 0, project.Id, release.Id, null, "Assurance baseline", "cm", Now);
        db.AddRange(program, project, release, baseline,
            // A real stored ladder, so the structural-separation assertion has something to be true about.
            LegacyDefaultProjectLadderFactory.Create(project.Id, Now));
        await db.SaveChangesAsync();

        Guid? policyVersionId = null;
        if (selections is not null)
        {
            var version = ProjectAssurancePolicy.Record(project.Id, 1, AssuranceLevel.LevelC, selections,
                "Record the project's declared posture.", "cm", Now);
            db.ProjectAssurancePolicies.Add(version);
            await db.SaveChangesAsync();
            policyVersionId = version.Id;
        }

        var campaign = new ReleaseCampaign(project.Id, release.Id, baseline.Id, "2.0", "program.manager", Now, policyVersionId);
        db.ReleaseCampaigns.Add(campaign);

        // An approved change with an impact finding nobody dispositioned.
        var scr = new SystemChangeRequest("SRCR-00020", 0, project.Id, release.Id, "Assurance change", "P", "A", "S", "author", Now);
        scr.AddRequirementChange("author", "SYSR-00000201", 0, RequirementLevel.System, RequirementChangeKind.Introduce,
            "The system shall hold the declared assurance posture.", "New capability", "Test", Now);
        db.Add(scr);
        db.ImpactDispositions.Add(new ChangeImpactDisposition(campaign.Id, scr.Id, ImpactKind.Requirement,
            scr.DisplayNumber, "Disposition the requirement impact."));

        // A materialized baseline carrying one requirement revision with no verification coverage at all.
        var requirement = new RequirementArtifact(project.Id, "SYSR-00000201", RequirementLevel.System, Now);
        var revision = new RequirementRevision(requirement.Id, 0, "The system shall hold the declared assurance posture.",
            "R", "Test", RequirementRevisionState.Active, scr.Id, baseline.Id, Now);
        db.AddRange(requirement, revision);
        db.BaselineRequirements.Add(new BaselineRequirementSelection(baseline.Id, requirement.Id, revision.Id));

        // A planned test set whose one selected procedure has no determination and therefore no evidence.
        var review = new TestChangeReview(project.Id, release.Id, scr.Id, TestChangeReviewDiscipline.System,
            scr.DisplayNumber, Now);
        var procedure = new TestProcedure(project.Id, "SYSTP-00000201", "Verify the assurance posture", "test.engineer",
            Now, TestProcedureLevel.System);
        var procedureRevision = new TestProcedureRevision(procedure.Id, 0, "Purpose", "Configuration", "Steps",
            "Expected", TestProcedureState.Approved, "test.engineer", Now);
        var testSet = new BuildTestSet(project.Id, release.Id, TestChangeReviewDiscipline.System, Now);
        db.AddRange(review, procedure, procedureRevision, testSet);
        db.BuildTestSetEntries.Add(new BuildTestSetEntry(testSet.Id, procedureRevision.Id,
            TestSelectionReason.ChangedRequirement, "Covers the changed requirement.", "test.lead", Now));

        // A release-blocking problem report suppressed by an attributable, in-force waiver.
        var report = new ProblemReport(project.Id, "PR-00201", "Posture drift", "The posture drifted.", "Analysis",
            "reporter", Now, responsibleEngineerId: "responsible.engineer");
        report.SetReleaseBlocker("responsible.engineer", true, Now);
        db.Add(report);
        await db.SaveChangesAsync();
        db.ReadinessWaivers.Add(new ReadinessWaiver(project.Id, "ProblemReportReleaseBlocker", report.Id,
            report.Revision, report.ReleaseBlockerVersion, "Accepted for this build with a documented workaround.",
            Guid.NewGuid(), "sqa.approver", "SoftwareQualityAnalyst", "Approved", Now.AddDays(30), "cm", Now));

        await db.SaveChangesAsync();
        await db.CandidateBaselines.Where(x => x.Id == baseline.Id).ExecuteUpdateAsync(update => update
            .SetProperty(x => x.State, CandidateBaselineState.Frozen)
            .SetProperty(x => x.RequirementsMaterializedAt, Now));

        return new(options, project.Id, campaign.Id, path);
    }

    private static async Task<IReadOnlyList<ReadinessGate>> GatesAsync(Seed seed)
    {
        await using var db = new AeroLinkDbContext(seed.Options);
        var readiness = await new ReleaseReadinessService(db,
            assuranceResolver: new EffectiveProjectAssurancePolicyResolver(db)).CalculateAsync(seed.CampaignId, default);
        return readiness.Gates;
    }

    private static ReadinessGate Gate(IReadOnlyList<ReadinessGate> gates, string code) =>
        gates.Single(x => x.Code == code);

    private static Dictionary<AssurancePolicyLever, AssuranceLeverValue> With(
        AssurancePolicyLever lever, AssuranceLeverValue value)
    {
        var selections = new Dictionary<AssurancePolicyLever, AssuranceLeverValue>(AssurancePolicyCatalogue.Recommended)
        {
            [lever] = value,
        };
        return selections;
    }

    [Fact]
    public async Task On_the_AeroLink_recommendations_the_fixture_fails_coverage_evidence_and_impact_disposition()
    {
        var seed = await SeedAsync();
        try
        {
            var gates = await GatesAsync(seed);
            foreach (var code in new[] { "coverage", "evidence", "impact_disposition" })
            {
                Assert.False(Gate(gates, code).Complete);
                Assert.Equal("Evaluated", Gate(gates, code).EvaluationState);
            }
            // The waiver is accepted by default, so the blocker does not stop this release.
            Assert.True(Gate(gates, "problem_reports").Complete);
        }
        finally { File.Delete(seed.Path); }
    }

    [Fact]
    public async Task A_project_that_has_never_recorded_a_policy_is_judged_exactly_as_before()
    {
        var seed = await SeedAsync();
        try
        {
            await using var db = new AeroLinkDbContext(seed.Options);
            // No assurance resolver at all — the pre-feature construction — must agree gate for gate with the
            // resolver reading a project that has recorded nothing.
            var without = await new ReleaseReadinessService(db).CalculateAsync(seed.CampaignId, default);
            var with = await new ReleaseReadinessService(db,
                assuranceResolver: new EffectiveProjectAssurancePolicyResolver(db)).CalculateAsync(seed.CampaignId, default);
            Assert.Equal(without.Percent, with.Percent);
            Assert.Equal(without.Gates.Select(x => (x.Code, x.Complete, x.Completed, x.Total, x.Detail, x.EvaluationState)),
                with.Gates.Select(x => (x.Code, x.Complete, x.Completed, x.Total, x.Detail, x.EvaluationState)));
        }
        finally { File.Delete(seed.Path); }
    }

    [Fact]
    public async Task Coverage_not_required_relaxes_only_the_coverage_gate()
    {
        var seed = await SeedAsync(With(AssurancePolicyLever.RequirementCoverageBeforeRelease, AssuranceLeverValue.NotRequired));
        try
        {
            var gates = await GatesAsync(seed);
            var coverage = Gate(gates, "coverage");
            Assert.True(coverage.Complete);
            Assert.Equal("RelaxedByPolicy", coverage.EvaluationState);
            Assert.Contains("Requirement coverage before release", coverage.Detail);
            Assert.Contains("no settled coverage", coverage.Detail);
            // The neighbouring obligations are untouched: this is one lever, not a blanket.
            Assert.False(Gate(gates, "evidence").Complete);
            Assert.False(Gate(gates, "impact_disposition").Complete);
        }
        finally { File.Delete(seed.Path); }
    }

    [Fact]
    public async Task Evidence_not_required_relaxes_only_the_evidence_gate()
    {
        var seed = await SeedAsync(With(AssurancePolicyLever.TestEvidenceBeforeRelease, AssuranceLeverValue.NotRequired));
        try
        {
            var gates = await GatesAsync(seed);
            var evidence = Gate(gates, "evidence");
            Assert.True(evidence.Complete);
            Assert.Equal("RelaxedByPolicy", evidence.EvaluationState);
            Assert.Contains("checksummed evidence", evidence.Detail);
            // A determination is still owed; only the evidence attached to it was relaxed.
            Assert.False(Gate(gates, "verification").Complete);
            Assert.False(Gate(gates, "coverage").Complete);
        }
        finally { File.Delete(seed.Path); }
    }

    [Fact]
    public async Task Impact_disposition_not_required_relaxes_only_the_impact_gate()
    {
        var seed = await SeedAsync(With(AssurancePolicyLever.ChangeImpactDispositionBeforeRelease, AssuranceLeverValue.NotRequired));
        try
        {
            var gates = await GatesAsync(seed);
            var impact = Gate(gates, "impact_disposition");
            Assert.True(impact.Complete);
            Assert.Equal("RelaxedByPolicy", impact.EvaluationState);
            Assert.Contains("Change impact dispositioned before release", impact.Detail);
            Assert.False(Gate(gates, "coverage").Complete);
        }
        finally { File.Delete(seed.Path); }
    }

    [Fact]
    public async Task Refusing_waivers_is_stricter_than_the_recommendation_and_is_enforced_without_a_deviation()
    {
        var waivers = AssurancePolicyCatalogue.Definition(AssurancePolicyLever.ProblemReportWaiverAcceptance);
        Assert.False(waivers.IsRelaxation(AssuranceLeverValue.WaiversRefused));

        var seed = await SeedAsync(With(AssurancePolicyLever.ProblemReportWaiverAcceptance, AssuranceLeverValue.WaiversRefused));
        try
        {
            var blockers = Gate(await GatesAsync(seed), "problem_reports");
            Assert.False(blockers.Complete);
            Assert.Equal(1, blockers.Total);
            Assert.Contains("PR-00201", blockers.Detail);
            Assert.Contains("refuses readiness waivers", blockers.Detail);

            await using var db = new AeroLinkDbContext(seed.Options);
            // The waiver itself is untouched. It remains the attributable decision somebody took; the policy
            // decides what it is allowed to suppress, and never rewrites the record.
            var waiver = await db.ReadinessWaivers.AsNoTracking().SingleAsync();
            Assert.Null(waiver.RevokedAt);
            Assert.True(waiver.IsActive(Now));
        }
        finally { File.Delete(seed.Path); }
    }

    [Fact]
    public async Task A_later_policy_change_does_not_reinterpret_a_campaign_that_began_under_an_earlier_snapshot()
    {
        var seed = await SeedAsync(AssurancePolicyCatalogue.Recommended.ToDictionary(x => x.Key, x => x.Value));
        try
        {
            Assert.False(Gate(await GatesAsync(seed), "coverage").Complete);

            Guid laterCampaignId;
            await using (var db = new AeroLinkDbContext(seed.Options))
            {
                var effective = await db.ProjectAssurancePolicies.SingleAsync(x => x.SupersededAt == null);
                effective.Supersede("cm", Now.AddDays(1));
                var relaxed = ProjectAssurancePolicy.Record(seed.ProjectId, 2, AssuranceLevel.LevelC,
                    With(AssurancePolicyLever.RequirementCoverageBeforeRelease, AssuranceLeverValue.NotRequired),
                    "Relax coverage for the follow-on build.", "cm", Now.AddDays(1));
                db.ProjectAssurancePolicies.Add(relaxed);

                // A second campaign, created after the change, takes the new snapshot.
                var release = new SoftwareRelease(seed.ProjectId, "2.1", false);
                var baseline = new CandidateBaseline("BL-00000002", 0, seed.ProjectId, release.Id, null,
                    "Follow-on baseline", "cm", Now.AddDays(1));
                var later = new ReleaseCampaign(seed.ProjectId, release.Id, baseline.Id, "2.1", "program.manager",
                    Now.AddDays(1), relaxed.Id);
                laterCampaignId = later.Id;
                db.AddRange(release, baseline, later);
                await db.SaveChangesAsync();
            }

            // The original campaign still answers to the policy it began under.
            Assert.False(Gate(await GatesAsync(seed), "coverage").Complete);

            await using var assertDb = new AeroLinkDbContext(seed.Options);
            var laterGates = (await new ReleaseReadinessService(assertDb,
                assuranceResolver: new EffectiveProjectAssurancePolicyResolver(assertDb))
                .CalculateAsync(laterCampaignId, default)).Gates;
            Assert.Equal("RelaxedByPolicy", Gate(laterGates, "coverage").EvaluationState);
        }
        finally { File.Delete(seed.Path); }
    }

    [Fact]
    public async Task Assurance_policy_never_alters_the_ladders_structural_verification_capability()
    {
        var seed = await SeedAsync(With(AssurancePolicyLever.RequirementCoverageBeforeRelease, AssuranceLeverValue.NotRequired));
        try
        {
            await using var db = new AeroLinkDbContext(seed.Options);
            var resolved = await new EffectiveProjectAssurancePolicyResolver(db).ResolveAsync(seed.ProjectId, default);
            Assert.True(resolved.IsRelaxed(AssurancePolicyLever.RequirementCoverageBeforeRelease));

            // The policy relaxed what the coverage gate demands. What the ladder can express is untouched:
            // the stored configuration is at the revision the seed wrote, it is sealed by its first
            // controlled content, and every step that carried verification still carries it.
            var configuration = await db.ProjectLadderConfigurations.AsNoTracking().Include(x => x.Steps)
                .SingleAsync(x => x.ProjectId == seed.ProjectId);
            Assert.True(configuration.IsSealed);
            var ladder = await new EffectiveProjectLadderPolicyResolver(db).ResolveAsync(seed.ProjectId, default);
            foreach (var level in ladder.OrderedLevels)
                Assert.Equal(LegacyLadderPolicy.Instance.Definition(level).Verification is not null,
                    ladder.Definition(level).Verification is not null);
            Assert.All(configuration.Steps,
                step => Assert.True(step.Capabilities.HasFlag(LevelCapabilities.HasVerification)));
        }
        finally { File.Delete(seed.Path); }
    }
}
