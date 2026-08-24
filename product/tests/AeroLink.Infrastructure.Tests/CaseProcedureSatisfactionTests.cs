using AeroLink.Domain.Baselines;
using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Hierarchy;
using AeroLink.Domain.Programs;
using AeroLink.Domain.Releases;
using AeroLink.Domain.Traceability;
using AeroLink.Domain.Verification;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AeroLink.Infrastructure.Tests;

/// <summary>
/// #726 blocker 3: the authoritative Case→Procedure satisfaction projection. Every effective exact software
/// Case revision in a Procedure-enabled baseline is unsatisfied with zero links, and every required exact
/// linked Procedure counts only when it is effective in that baseline, selected in the matching discipline
/// BuildTestSet for the same release, and its latest execution under the existing release/build
/// ExecutionScope is Pass. Suspect links, Failed/Blocked/missing results, cross-release, cross-build,
/// cross-discipline, non-effective, and Derived work never count; checksummed evidence remains a separate
/// release gate.
/// </summary>
public sealed class CaseProcedureSatisfactionTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    private sealed record Seed(AeroLinkDbContext Db, Guid ProjectId, Guid ReleaseId, Guid BaselineId,
        Guid OtherBaselineId, Guid OtherReleaseId, Guid SoftwareBuildId, Guid CaseRevisionId,
        Guid CaseProcedureId, Guid ProcedureRevisionId, Guid LinkId, Guid ExecutionId, Guid TestSetId);

    private sealed record SeedOptions(
        bool WithLink = true,
        bool DerivedProcedure = false,
        bool ProcedureSelectedInOtherBaseline = false,
        bool ProcedureSelected = true,
        bool ProcedureExecuted = true,
        TestChangeReviewDiscipline Discipline = TestChangeReviewDiscipline.HighLevelSoftware,
        TestOutcome Outcome = TestOutcome.Pass,
        // Guid.Empty means "the seed's other release/build", null means "the seed's primary release / no build".
        Guid? ExecutionReleaseId = null,
        Guid? ExecutionSoftwareBuildId = null);

    [Fact]
    public async Task Zero_links_is_unsatisfied_even_when_a_derived_procedure_executes()
    {
        var seed = await BuildAsync(new SeedOptions(WithLink: false, DerivedProcedure: true));
        var obligations = await EvaluateAsync(seed);
        var obligation = Assert.Single(obligations);
        Assert.Equal(seed.CaseRevisionId, obligation.CaseRevisionId);
        Assert.Empty(obligation.RequiredProcedureRevisionIds);
        Assert.False(obligation.Satisfied);
    }

    [Fact]
    public async Task One_exact_linked_procedure_with_latest_build_scoped_pass_is_satisfied()
    {
        var seed = await BuildAsync();
        var obligations = await EvaluateAsync(seed);
        var obligation = Assert.Single(obligations);
        Assert.Equal([seed.ProcedureRevisionId], obligation.RequiredProcedureRevisionIds);
        Assert.Equal([seed.ProcedureRevisionId], obligation.SatisfiedProcedureRevisionIds);
        Assert.Empty(obligation.UnsatisfiedProcedureRevisionIds);
        Assert.True(obligation.Satisfied);
    }

    [Fact]
    public async Task Multiple_required_procedures_are_satisfied_only_when_every_link_is_met()
    {
        var seed = await BuildAsync();
        var second = await AddLinkedProcedureAsync(seed, "HLRTP-000402", satisfied: true);
        var obligations = await EvaluateAsync(seed);
        var obligation = Assert.Single(obligations);
        Assert.Equal(new[] { seed.ProcedureRevisionId, second.ProcedureRevisionId }.OrderBy(x => x).ToArray(),
            obligation.RequiredProcedureRevisionIds.OrderBy(x => x).ToArray());
        Assert.Equal(new[] { seed.ProcedureRevisionId, second.ProcedureRevisionId }.OrderBy(x => x).ToArray(),
            obligation.SatisfiedProcedureRevisionIds.OrderBy(x => x).ToArray());
        Assert.True(obligation.Satisfied);
    }

    [Fact]
    public async Task One_missing_linked_procedure_among_multiple_is_unsatisfied()
    {
        var seed = await BuildAsync();
        var second = await AddLinkedProcedureAsync(seed, "HLRTP-000403", satisfied: false);
        var obligations = await EvaluateAsync(seed);
        var obligation = Assert.Single(obligations);
        Assert.Equal([second.ProcedureRevisionId], obligation.UnsatisfiedProcedureRevisionIds);
        Assert.False(obligation.Satisfied);
    }

    [Fact]
    public async Task Wrong_discipline_build_test_set_does_not_satisfy()
    {
        var seed = await BuildAsync(new SeedOptions(
            Discipline: TestChangeReviewDiscipline.LowLevelSoftware));
        var obligations = await EvaluateAsync(seed);
        var obligation = Assert.Single(obligations);
        Assert.Equal([seed.ProcedureRevisionId], obligation.UnsatisfiedProcedureRevisionIds);
        Assert.False(obligation.Satisfied);
    }

    [Fact]
    public async Task Cross_release_execution_does_not_satisfy()
    {
        var seed = await BuildAsync(new SeedOptions(ExecutionReleaseId: Guid.Empty));
        var obligations = await EvaluateAsync(seed);
        Assert.False(Assert.Single(obligations).Satisfied);
    }

    [Fact]
    public async Task Build_attributed_execution_is_out_of_scope_without_a_campaign_build()
    {
        var seed = await BuildAsync(new SeedOptions(ExecutionSoftwareBuildId: Guid.Empty));
        var obligations = await EvaluateAsync(seed);
        Assert.False(Assert.Single(obligations).Satisfied);
    }

    [Fact]
    public async Task Linked_procedure_selected_only_in_another_baseline_is_unsatisfied()
    {
        var seed = await BuildAsync(new SeedOptions(ProcedureSelectedInOtherBaseline: true));
        var obligations = await EvaluateAsync(seed);
        var obligation = Assert.Single(obligations);
        Assert.Equal(seed.CaseRevisionId, obligation.CaseRevisionId);
        Assert.Equal([seed.ProcedureRevisionId], obligation.UnsatisfiedProcedureRevisionIds);
        Assert.False(obligation.Satisfied);
    }

    [Fact]
    public async Task Suspect_link_never_satisfies()
    {
        var seed = await BuildAsync();
        var successorCase = new TestProcedureRevision(seed.CaseProcedureId, 1,
            "Verify sequencing v2", "Logical preconditions v2", "Scenario steps v2", "Pass criteria v2",
            TestProcedureState.Approved, "test.engineer", Now.AddMinutes(5),
            effectiveBaselineId: seed.BaselineId);
        var carriedLink = new TestCaseProcedureLink(successorCase.Id, seed.ProcedureRevisionId);
        var lifecycle = ExactLinkSuspectLifecycle.Raise(seed.ProjectId, ExactLinkKind.CaseProcedure,
            carriedLink.Id, ExactLinkLifecycleCauseKind.InternalVerificationRevision, null, null,
            "test.lead", "The exact Case changed; the carried Procedure relation must be reconfirmed.",
            Now.AddMinutes(6), causeVerificationRevisionId: successorCase.Id);
        carriedLink.AttachExactLinkLifecycle(lifecycle.Id);
        seed.Db.AddRange(successorCase, carriedLink, lifecycle);
        await seed.Db.SaveChangesAsync();

        var obligations = await EvaluateAsync(seed);
        var clean = obligations.Single(x => x.CaseRevisionId == seed.CaseRevisionId);
        Assert.True(clean.Satisfied);
        var suspect = obligations.Single(x => x.CaseRevisionId == successorCase.Id);
        Assert.True(suspect.HasSuspectLink);
        Assert.Empty(suspect.SatisfiedProcedureRevisionIds);
        Assert.False(suspect.Satisfied);
    }

    [Theory]
    [InlineData(TestOutcome.Fail)]
    [InlineData(TestOutcome.Blocked)]
    public async Task Latest_non_pass_result_never_satisfies(TestOutcome outcome)
    {
        var seed = await BuildAsync(new SeedOptions(Outcome: outcome));
        var obligations = await EvaluateAsync(seed);
        var obligation = Assert.Single(obligations);
        Assert.Equal([seed.ProcedureRevisionId], obligation.UnsatisfiedProcedureRevisionIds);
        Assert.False(obligation.Satisfied);
    }

    [Fact]
    public async Task Older_pass_does_not_override_a_later_failure()
    {
        var seed = await BuildAsync();
        seed.Db.Add(new TestExecution(seed.ProjectId, seed.ProcedureRevisionId, null, null, TestOutcome.Fail,
            "test.engineer", "Rig B", "Human determination", "evidence/late-fail.json",
            Now.AddDays(1), Now.AddDays(1), seed.ReleaseId));
        await seed.Db.SaveChangesAsync();
        Assert.False(Assert.Single(await EvaluateAsync(seed)).Satisfied);
    }

    [Fact]
    public async Task Latest_pass_overrides_an_older_failure()
    {
        var seed = await BuildAsync(new SeedOptions(Outcome: TestOutcome.Fail));
        seed.Db.Add(new TestExecution(seed.ProjectId, seed.ProcedureRevisionId, null, null, TestOutcome.Pass,
            "test.engineer", "Rig B", "Human determination", "evidence/late-pass.json",
            Now.AddDays(1), Now.AddDays(1), seed.ReleaseId));
        await seed.Db.SaveChangesAsync();
        Assert.True(Assert.Single(await EvaluateAsync(seed)).Satisfied);
    }

    [Fact]
    public async Task Checksummed_evidence_remains_a_separate_gate_from_satisfaction()
    {
        var seed = await BuildAsync();
        Assert.Equal(0, await seed.Db.TestExecutionEvidence.AsNoTracking().CountAsync());
        Assert.True(Assert.Single(await EvaluateAsync(seed)).Satisfied);
        var evidenceFile = new EvidenceRecord(seed.ProjectId, "evidence.bin", "application/octet-stream",
            64, new string('a', 64), "storage/evidence.bin", "test.engineer", Now);
        seed.Db.Add(evidenceFile);
        seed.Db.Add(new TestExecutionEvidence(seed.ExecutionId, evidenceFile.Id));
        await seed.Db.SaveChangesAsync();
        Assert.Equal(1, await seed.Db.TestExecutionEvidence.AsNoTracking().CountAsync());
        Assert.True(Assert.Single(await EvaluateAsync(seed)).Satisfied);
    }

    [Fact]
    public async Task Many_to_many_case_parents_are_all_preserved_and_each_case_gets_its_own_obligation()
    {
        var seed = await BuildAsync();
        var policy = ProcedureEnabledPolicy();
        var secondCase = new TestProcedure(seed.ProjectId, "HLRTC-000404",
            "Second sequencing case", "test.engineer", Now, TestProcedureLevel.HighLevel,
            policy, VerificationArtifactKind.Case);
        var secondCaseRevision = new TestProcedureRevision(secondCase.Id, 0,
            "Verify the second sequencing case", "Logical preconditions", "Scenario steps",
            "Pass criteria", TestProcedureState.Approved, "test.engineer", Now,
            effectiveBaselineId: seed.BaselineId);
        var secondLink = new TestCaseProcedureLink(secondCaseRevision.Id, seed.ProcedureRevisionId);
        seed.Db.AddRange(secondCase, secondCaseRevision, secondLink);
        await seed.Db.SaveChangesAsync();

        var selections = await BaselineExecutableMembership.ForBaselineAsync(seed.Db, seed.BaselineId, default);
        var sourceCaseRevisionIds = await BaselineExecutableMembership.SourceCaseRevisionIdsAsync(
            seed.Db, selections, default);
        Assert.Contains(seed.CaseRevisionId, sourceCaseRevisionIds);
        Assert.Contains(secondCaseRevision.Id, sourceCaseRevisionIds);

        var obligations = await EvaluateAsync(seed);
        Assert.Equal(2, obligations.Count);
        var first = obligations.Single(x => x.CaseRevisionId == seed.CaseRevisionId);
        Assert.True(first.Satisfied);
        var second = obligations.Single(x => x.CaseRevisionId == secondCaseRevision.Id);
        Assert.True(second.Satisfied);
    }

    private static async Task<IReadOnlyList<CaseProcedureObligation>> EvaluateAsync(Seed seed)
    {
        var obligations = await CaseProcedureSatisfaction.ForBaselineAsync(seed.Db, seed.BaselineId,
            seed.ReleaseId, null, new HashSet<TestProcedureLevel> { TestProcedureLevel.HighLevel }, default);
        return obligations;
    }

    private static ILadderPolicy ProcedureEnabledPolicy()
    {
        var projectId = Guid.NewGuid();
        var configuration = ProjectLadderConfiguration.CreateDraft(projectId, Now);
        var steps = new List<ProjectLadderStep>();
        foreach (var (level, position) in LegacyLadderPolicy.Instance.OrderedLevels.Select((x, i) => (x, i + 1)))
        {
            var kinds = level == RequirementLevel.System
                ? new[] { VerificationArtifactKind.Procedure }
                : new[] { VerificationArtifactKind.Case, VerificationArtifactKind.Procedure };
            var step = new ProjectLadderStep(configuration.Id, projectId, level, position,
                LegacyLadderPolicy.Instance.Definition(level).Capabilities, Now, kinds);
            configuration.Steps.Add(step);
            steps.Add(step);
        }
        configuration.AllowedUpstream.Add(new ProjectLadderAllowedUpstream(
            configuration.Id, projectId, steps[0].Id, steps[1].Id, Now));
        configuration.AllowedUpstream.Add(new ProjectLadderAllowedUpstream(
            configuration.Id, projectId, steps[1].Id, steps[2].Id, Now));
        return new ResolvedProjectLadderPolicy(ProjectLadderResolver.Resolve(configuration));
    }

    private static async Task<Seed> BuildAsync(SeedOptions? options = null)
    {
        options ??= new SeedOptions();
        var dbOptions = new DbContextOptionsBuilder<AeroLinkDbContext>().UseSqlite("Data Source=:memory:").Options;
        var db = new AeroLinkDbContext(dbOptions);
        await db.Database.OpenConnectionAsync();
        await db.Database.EnsureCreatedAsync();
        var policy = ProcedureEnabledPolicy();

        var program = new ProgramRecord("Satisfaction Program", "SAT");
        var project = new ProjectRecord(program.Id, "Satisfaction Software", "Satisfaction Product");
        var release = new SoftwareRelease(project.Id, "3.1", false);
        var otherRelease = new SoftwareRelease(project.Id, "3.2", false);
        var baseline = new CandidateBaseline("SW-03.10", 0, project.Id, release.Id, null,
            "Candidate", "cm.test", Now);
        var otherBaseline = new CandidateBaseline("SW-03.11", 0, project.Id, release.Id, baseline.Id,
            "Other candidate", "cm.test", Now.AddMinutes(1));
        var softwareBuild = new SoftwareBuild(project.Id, release.Id, baseline.Id, "B-0301",
            "Candidate build", "cm.test", Now);
        db.AddRange(program, project, release, otherRelease, baseline, otherBaseline, softwareBuild);

        var caseArtifact = new TestProcedure(project.Id, "HLRTC-000401", "Sequencing case",
            "test.engineer", Now, TestProcedureLevel.HighLevel, policy, VerificationArtifactKind.Case);
        var caseRevision = new TestProcedureRevision(caseArtifact.Id, 0,
            "Verify oceanic sequencing", "Logical preconditions", "Scenario steps", "Pass criteria",
            TestProcedureState.Approved, "test.engineer", Now, effectiveBaselineId: baseline.Id);

        var procedure = new TestProcedure(project.Id, "HLRTP-000401", "Sequencing procedure",
            "test.engineer", Now, TestProcedureLevel.HighLevel, policy, VerificationArtifactKind.Procedure,
            options.DerivedProcedure
                ? VerificationProcedureParentKind.Derived
                : VerificationProcedureParentKind.Allocated);
        var procedureRevision = new TestProcedureRevision(procedure.Id, 0,
            "Execute sequencing", "Procedure setup", "Procedure steps", "Expected observation",
            TestProcedureState.Draft, "test.engineer", Now,
            environmentSetup: "Procedure setup",
            testData: "Controlled test data",
            orderedSteps: "Procedure steps",
            expectedObservations: "Expected observation",
            cleanup: "Restore the fixture",
            toolingAutomation: "Qualified runner",
            parentKind: options.DerivedProcedure
                ? VerificationProcedureParentKind.Derived
                : VerificationProcedureParentKind.Allocated,
            derivedRationale: options.DerivedProcedure ? "The standalone Procedure mirrors no Case." : null);

        var link = options.WithLink ? new TestCaseProcedureLink(caseRevision.Id, procedureRevision.Id) : null;

        var selectionBaseline = options.ProcedureSelectedInOtherBaseline ? otherBaseline.Id : baseline.Id;
        var selection = options.ProcedureSelected
            ? new BaselineTestProcedureSelection(selectionBaseline, procedure.Id, procedureRevision.Id)
            : null;
        var set = new BuildTestSet(project.Id, release.Id, options.Discipline, Now);
        set.Include("test.lead", procedureRevision.Id, TestSelectionReason.Chosen, "", Now);
        db.AddRange(caseArtifact, caseRevision, procedure, procedureRevision);
        if (link is not null) db.Add(link);
        if (selection is not null) db.Add(selection);
        db.Add(set);
        using (db.UseSaveBoundaryPolicy(policy)) await db.SaveChangesAsync();
        db.Entry(procedureRevision).Property(x => x.State).CurrentValue = TestProcedureState.Approved;
        using (db.UseSaveBoundaryPolicy(policy)) await db.SaveChangesAsync();
        var execution = options.ProcedureExecuted
            ? new TestExecution(project.Id, procedureRevision.Id,
                options.ExecutionSoftwareBuildId == Guid.Empty ? softwareBuild.Id : options.ExecutionSoftwareBuildId,
                null,
                options.Outcome, "test.engineer", "Rig A", "Human determination",
                options.Outcome == TestOutcome.Blocked ? "" : "evidence/a.json",
                Now, Now,
                options.ExecutionReleaseId == Guid.Empty ? otherRelease.Id
                    : options.ExecutionReleaseId ?? release.Id)
            : null;
        if (execution is not null)
        {
            db.Add(execution);
            await db.SaveChangesAsync();
        }
        return new Seed(db, project.Id, release.Id, baseline.Id, otherBaseline.Id, otherRelease.Id,
            softwareBuild.Id, caseRevision.Id, caseArtifact.Id, procedureRevision.Id, link?.Id ?? Guid.Empty,
            execution?.Id ?? Guid.Empty, set.Id);
    }

    private static async Task<(Guid ProcedureRevisionId, Guid LinkId)> AddLinkedProcedureAsync(
        Seed seed, string baseNumber, bool satisfied)
    {
        var policy = ProcedureEnabledPolicy();
        var procedure = new TestProcedure(seed.ProjectId, baseNumber, "Additional sequencing procedure",
            "test.engineer", Now, TestProcedureLevel.HighLevel, policy, VerificationArtifactKind.Procedure,
            VerificationProcedureParentKind.Allocated);
        var revision = new TestProcedureRevision(procedure.Id, 0,
            "Execute additional sequencing", "Procedure setup", "Procedure steps", "Expected observation",
            TestProcedureState.Draft, "test.engineer", Now,
            environmentSetup: "Procedure setup",
            testData: "Controlled test data",
            orderedSteps: "Procedure steps",
            expectedObservations: "Expected observation",
            cleanup: "Restore the fixture",
            toolingAutomation: "Qualified runner",
            parentKind: VerificationProcedureParentKind.Allocated);
        var link = new TestCaseProcedureLink(seed.CaseRevisionId, revision.Id);
        seed.Db.AddRange(procedure, revision, link);
        if (satisfied)
        {
            var selection = new BaselineTestProcedureSelection(seed.BaselineId, procedure.Id, revision.Id);
            var set = seed.Db.BuildTestSets.Single(x => x.Id == seed.TestSetId);
            set.Include("test.lead", revision.Id, TestSelectionReason.Chosen, "", Now);
            seed.Db.Add(selection);
        }
        using (seed.Db.UseSaveBoundaryPolicy(policy)) await seed.Db.SaveChangesAsync();
        seed.Db.Entry(revision).Property(x => x.State).CurrentValue = TestProcedureState.Approved;
        using (seed.Db.UseSaveBoundaryPolicy(policy)) await seed.Db.SaveChangesAsync();
        if (satisfied)
        {
            var execution = new TestExecution(seed.ProjectId, revision.Id, null, null, TestOutcome.Pass,
                "test.engineer", "Rig A", "Human determination", "evidence/extra.json",
                Now, Now, seed.ReleaseId);
            seed.Db.Add(execution);
            await seed.Db.SaveChangesAsync();
        }
        return (revision.Id, link.Id);
    }
}
