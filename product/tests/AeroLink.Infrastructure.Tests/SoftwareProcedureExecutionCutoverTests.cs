using AeroLink.Domain.Baselines;
using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Hierarchy;
using AeroLink.Domain.Identity;
using AeroLink.Domain.Programs;
using AeroLink.Domain.Releases;
using AeroLink.Domain.Verification;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AeroLink.Infrastructure.Tests;

/// <summary>
/// #726 governed cutover: sealed Case-only software projects upgrade to [Case, Procedure], one exact
/// deterministic migration-generated Procedure is created per Case revision with honest provenance, and
/// executions, test-set entries and baseline selections rebind to the new executable revisions. Reruns are
/// idempotent; a missing typed Procedure-capable execution consumer refuses the ENTIRE cutover with no
/// partial state; unsealed Case-only projects remain untouched.
/// </summary>
public sealed class SoftwareProcedureExecutionCutoverTests
{
    internal static (IReadOnlyList<ILadderConsumerRegistration> Legacy,
        IReadOnlyList<IVerificationArtifactConsumerRegistration> Typed) FullRegistrations()
    {
        var keys = new[]
        {
            new VerificationArtifactKey(VerificationDiscipline.System, VerificationArtifactKind.Procedure),
            new VerificationArtifactKey(VerificationDiscipline.HighLevelSoftware, VerificationArtifactKind.Case),
            new VerificationArtifactKey(VerificationDiscipline.LowLevelSoftware, VerificationArtifactKind.Case),
            new VerificationArtifactKey(VerificationDiscipline.HighLevelSoftware, VerificationArtifactKind.Procedure),
            new VerificationArtifactKey(VerificationDiscipline.LowLevelSoftware, VerificationArtifactKind.Procedure),
        };
        // Production routes every stable required consumer; the test mirrors that inventory so readiness
        // measures typed artifact coverage, not a missing legacy route.
        var legacy = LadderConsumerManifestCatalog.RequiredConsumerIds
            .Select(id => new LadderConsumerRegistration(id, id))
            .Cast<ILadderConsumerRegistration>()
            .ToArray();
        var typed = new IVerificationArtifactConsumerRegistration[]
        {
            new VerificationArtifactConsumerRegistration("release.readiness", "Release readiness policy gates", keys,
                VerificationArtifactCapability.Execution | VerificationArtifactCapability.Coverage),
            new VerificationArtifactConsumerRegistration("build.test-sets", "Build verification test-set derivation", keys,
                VerificationArtifactCapability.Execution),
            new VerificationArtifactConsumerRegistration("release.reconciliation", "Release trace reconciliation", keys,
                VerificationArtifactCapability.Execution | VerificationArtifactCapability.Coverage),
            new VerificationArtifactConsumerRegistration("verification.execution", "Execution creation and latest-result", keys,
                VerificationArtifactCapability.Execution),
            new VerificationArtifactConsumerRegistration("baseline.executable-materialization", "Baseline executable selection", keys,
                VerificationArtifactCapability.Execution),
            new VerificationArtifactConsumerRegistration("navigation.primary", "Navigation and workspace projections", keys,
                VerificationArtifactCapability.Identity | VerificationArtifactCapability.Header
                | VerificationArtifactCapability.Revision | VerificationArtifactCapability.Lifecycle
                | VerificationArtifactCapability.Execution),
            new VerificationArtifactConsumerRegistration("verification.procedure-level", "Verification artifact level mapping", keys,
                VerificationArtifactCapability.Identity | VerificationArtifactCapability.Header
                | VerificationArtifactCapability.Revision | VerificationArtifactCapability.Lifecycle),
            new VerificationArtifactConsumerRegistration("verification.coverage", "Same-level coverage mutation", keys,
                VerificationArtifactCapability.Coverage),
            new VerificationArtifactConsumerRegistration("baseline.controlled-documents", "Baseline controlled documents", keys,
                VerificationArtifactCapability.ControlledDocument),
            new VerificationArtifactConsumerRegistration("change-request.downstream-impact", "Downstream assessment", keys,
                VerificationArtifactCapability.ChangeReview),
            new VerificationArtifactConsumerRegistration("verification.test-change-workflow", "Test-change workflow", keys,
                VerificationArtifactCapability.ChangeReview),
        };
        return (legacy, typed);
    }

    private sealed record Seed(AeroLinkDbContext Db, Guid ProjectId, Guid ReleaseId, Guid BaselineId,
        Guid CaseProcedureId, Guid CaseRevisionId, Guid ExecutionId, Guid TestSetEntryId, Guid BaselineSelectionId);

    private static async Task<Seed> SeedAsync(bool sealedLadder = true, bool verificationContent = true)
    {
        var options = new DbContextOptionsBuilder<AeroLinkDbContext>().UseSqlite("Data Source=:memory:").Options;
        var db = new AeroLinkDbContext(options);
        await db.Database.OpenConnectionAsync();
        await db.Database.EnsureCreatedAsync();
        var now = DateTimeOffset.UtcNow;

        var program = new ProgramRecord("Cutover Program", "CUT");
        var project = new ProjectRecord(program.Id, "Flight Software", "Cutover Software");
        var release = new SoftwareRelease(project.Id, "1.6", false);
        var baseline = new CandidateBaseline("SW-01.60", 0, project.Id, release.Id, null,
            "Candidate", "cm.test", now);
        db.AddRange(program, project, release, baseline);

        var configuration = LegacyDefaultProjectLadderFactory.Create(project.Id, now);
        db.ProjectLadderConfigurations.Add(configuration);
        await db.SaveChangesAsync();
        if (sealedLadder)
        {
            var contentKind = LadderBoundContentCatalog.Current.First().Id;
            var seal = await new ProjectLadderSealAuthority(db).SealAsync(project.Id, contentKind,
                "test-content", "test.sealer", now);
            Assert.Equal(ProjectLadderSealResultKind.Sealed, seal.Kind);
        }

        if (!verificationContent) return new Seed(db, project.Id, release.Id, baseline.Id, Guid.Empty,
            Guid.Empty, Guid.Empty, Guid.Empty, Guid.Empty);

        var caseArtifact = new TestProcedure(project.Id, "HLRTC-000101", "Oceanic sequencing case",
            "test.engineer", now, TestProcedureLevel.HighLevel);
        var caseRevision = new TestProcedureRevision(caseArtifact.Id, 0,
            "Verify oceanic sequencing", "Logical preconditions", "Scenario steps", "Pass criteria",
            TestProcedureState.Approved, "test.engineer", now);
        db.AddRange(caseArtifact, caseRevision);
        await db.SaveChangesAsync();

        var execution = new TestExecution(project.Id, caseRevision.Id, null, null, TestOutcome.Pass,
            "test.engineer", "Rig A", "Human determination", "evidence/a.json", now, now, release.Id);
        var set = new BuildTestSet(project.Id, release.Id, TestChangeReviewDiscipline.HighLevelSoftware, now);
        set.Include("test.lead", caseRevision.Id, TestSelectionReason.Chosen, "", now);
        var selection = new BaselineTestProcedureSelection(baseline.Id, caseArtifact.Id, caseRevision.Id);
        db.AddRange(execution, set, selection);
        await db.SaveChangesAsync();
        return new Seed(db, project.Id, release.Id, baseline.Id, caseArtifact.Id, caseRevision.Id,
            execution.Id, set.Entries.Single().Id, selection.Id);
    }

    [Fact]
    public async Task Cutover_upgrades_sealed_projects_and_rebinds_executable_references()
    {
        var seed = await SeedAsync();
        var raw = await seed.Db.ProjectLadderConfigurations.AsNoTracking()
            .Include(x => x.Steps).Include(x => x.AllowedUpstream)
            .SingleAsync(x => x.ProjectId == seed.ProjectId);
        var resolved = ProjectLadderResolver.Resolve(raw);
        var detail = string.Join("; ", resolved.Steps.Select(step =>
            $"{step.Level}:[{string.Join(",", step.EnabledArtifactKinds ?? [])}]"));
        Assert.True(resolved.AgreesWithLegacyDefault(), $"Legacy agreement failed for {detail}");
        var (legacy, typed) = FullRegistrations();
        var authority = new SoftwareProcedureExecutionCutoverAuthority(seed.Db, legacy, typed, allowSqliteExecution: true);
        var result = await authority.EnsureCompletedAsync();
        Assert.Equal(1, result.ProjectsUpgraded);
        Assert.Equal(1, result.ProceduresGenerated);
        Assert.Equal(1, result.ExecutionsRebound);
        Assert.Equal(1, result.TestSetEntriesRebound);
        Assert.Equal(1, result.BaselineSelectionsRebound);

        var configuration = await seed.Db.ProjectLadderConfigurations.AsNoTracking()
            .Include(x => x.Steps).SingleAsync(x => x.ProjectId == seed.ProjectId);
        var highLevelStep = configuration.Steps.Single(x => x.CatalogueEntry == nameof(RequirementLevel.HighLevel));
        Assert.Contains(VerificationArtifactKind.Procedure, highLevelStep.EnabledArtifactKinds);
        Assert.Contains(VerificationArtifactKind.Case, highLevelStep.EnabledArtifactKinds);
        Assert.Equal("aerolink-migration", configuration.LastUpgradeBy);

        var procedure = await seed.Db.TestProcedures.AsNoTracking()
            .SingleAsync(x => x.ProjectId == seed.ProjectId && x.ArtifactKind == VerificationArtifactKind.Procedure);
        var procedureRevision = await seed.Db.TestProcedureRevisions.AsNoTracking()
            .SingleAsync(x => x.ProcedureId == procedure.Id);
        Assert.Equal(VerificationProcedureParentKind.Allocated, procedureRevision.ParentKind);
        Assert.Equal("aerolink-migration", procedureRevision.AuthorId);
        Assert.Null(procedureRevision.SourceTestChangeRequestId);
        Assert.Equal("Scenario steps", procedureRevision.OrderedSteps);
        Assert.Equal("Pass criteria", procedureRevision.ExpectedObservations);
        Assert.Equal("Logical preconditions", procedureRevision.EnvironmentSetup);
        var link = await seed.Db.TestCaseProcedureLinks.AsNoTracking()
            .SingleAsync(x => x.CaseRevisionId == seed.CaseRevisionId);
        Assert.Equal(procedureRevision.Id, link.ProcedureRevisionId);

        Assert.Equal(procedureRevision.Id, (await seed.Db.TestExecutions.AsNoTracking()
            .SingleAsync(x => x.Id == seed.ExecutionId)).ProcedureRevisionId);
        Assert.Equal(procedureRevision.Id, (await seed.Db.BuildTestSetEntries.AsNoTracking()
            .SingleAsync(x => x.Id == seed.TestSetEntryId)).ProcedureRevisionId);
        var reboundSelection = await seed.Db.BaselineTestProcedures.AsNoTracking()
            .SingleAsync(x => x.Id == seed.BaselineSelectionId);
        Assert.Equal(procedure.Id, reboundSelection.ProcedureId);
        Assert.Equal(procedureRevision.Id, reboundSelection.RevisionId);

        Assert.True(await seed.Db.SecurityAuditEvents.AsNoTracking()
            .AnyAsync(x => x.EventType == "VerificationExecutionCutover.SoftwareProcedures.v1.ProcedureGenerated"));

        var rerun = await authority.EnsureCompletedAsync();
        Assert.Equal(0, rerun.ProceduresGenerated);
        Assert.Equal(1, await seed.Db.TestProcedures.AsNoTracking()
            .CountAsync(x => x.ProjectId == seed.ProjectId && x.ArtifactKind == VerificationArtifactKind.Procedure));
        Assert.Equal(1, await seed.Db.TestCaseProcedureLinks.AsNoTracking().CountAsync());
    }

    [Fact]
    public async Task Cutover_is_refused_when_a_procedure_capable_execution_consumer_is_absent()
    {
        var seed = await SeedAsync();
        var (legacy, _) = FullRegistrations();
        // Omit every typed registration: the v2 manifest has no Procedure-capable execution consumer at all.
        var authority = new SoftwareProcedureExecutionCutoverAuthority(seed.Db, legacy,
            Array.Empty<IVerificationArtifactConsumerRegistration>(), allowSqliteExecution: true);
        await Assert.ThrowsAsync<InvalidOperationException>(() => authority.EnsureCompletedAsync());
        Assert.Equal(0, await seed.Db.TestProcedures.AsNoTracking()
            .CountAsync(x => x.ProjectId == seed.ProjectId && x.ArtifactKind == VerificationArtifactKind.Procedure));
        Assert.Equal(0, await seed.Db.TestCaseProcedureLinks.AsNoTracking().CountAsync());
        Assert.False(await seed.Db.SecurityAuditEvents.AsNoTracking()
            .AnyAsync(x => x.EventType.StartsWith("VerificationExecutionCutover")));
        Assert.Equal(seed.CaseRevisionId, (await seed.Db.TestExecutions.AsNoTracking()
            .SingleAsync(x => x.Id == seed.ExecutionId)).ProcedureRevisionId);
    }

    [Fact]
    public async Task Unsealed_case_only_project_remains_untouched()
    {
        // A ladder is sealed by the product when its first verification content is created, so an unsealed
        // project is one with no verification content yet. The cutover must leave it alone.
        var seed = await SeedAsync(sealedLadder: false, verificationContent: false);
        var unsealed = await seed.Db.ProjectLadderConfigurations.AsNoTracking()
            .SingleAsync(x => x.ProjectId == seed.ProjectId);
        Assert.False(unsealed.IsSealed);
        var (legacy, typed) = FullRegistrations();
        var authority = new SoftwareProcedureExecutionCutoverAuthority(seed.Db, legacy, typed, allowSqliteExecution: true);
        var result = await authority.EnsureCompletedAsync();
        Assert.Equal(0, result.ProjectsUpgraded);
        Assert.Equal(0, await seed.Db.TestProcedures.AsNoTracking()
            .CountAsync(x => x.ProjectId == seed.ProjectId && x.ArtifactKind == VerificationArtifactKind.Procedure));
        Assert.False((await seed.Db.ProjectLadderConfigurations.AsNoTracking()
            .SingleAsync(x => x.ProjectId == seed.ProjectId)).IsSealed);
    }
}
