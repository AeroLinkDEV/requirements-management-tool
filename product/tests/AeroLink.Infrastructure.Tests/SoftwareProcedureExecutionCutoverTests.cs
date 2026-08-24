using AeroLink.Domain.Baselines;
using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Hierarchy;
using AeroLink.Domain.Identity;
using AeroLink.Domain.Programs;
using AeroLink.Domain.Releases;
using AeroLink.Domain.Requirements;
using AeroLink.Domain.Verification;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

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

    [Fact]
    public async Task Sealed_authored_draft_is_never_made_effective_by_the_cutover()
    {
        var options = new DbContextOptionsBuilder<AeroLinkDbContext>().UseSqlite("Data Source=:memory:").Options;
        var db = new AeroLinkDbContext(options);
        await db.Database.OpenConnectionAsync();
        await db.Database.EnsureCreatedAsync();
        var projectId = await MatrixProjectAsync(db, [VerificationArtifactKind.Case]);
        var configuration = await db.ProjectLadderConfigurations.AsNoTracking()
            .SingleAsync(x => x.ProjectId == projectId);
        Assert.Equal(ProjectLadderConfigurationState.Draft, configuration.State);
        var (legacy, typed) = FullRegistrations();
        var authority = new SoftwareProcedureExecutionCutoverAuthority(db, legacy, typed,
            allowSqliteExecution: true);
        var result = await authority.EnsureCompletedAsync();
        Assert.Equal(0, result.ProjectsUpgraded);
        Assert.Equal(ProjectLadderConfigurationState.Draft, (await db.ProjectLadderConfigurations
            .AsNoTracking().SingleAsync(x => x.ProjectId == projectId)).State);
        Assert.Equal(0, await db.TestProcedures.AsNoTracking()
            .CountAsync(x => x.ProjectId == projectId
                && x.ArtifactKind == VerificationArtifactKind.Procedure));
    }

    [Fact]
    public async Task Deliberate_active_case_only_profile_is_preserved()
    {
        var options = new DbContextOptionsBuilder<AeroLinkDbContext>().UseSqlite("Data Source=:memory:").Options;
        var db = new AeroLinkDbContext(options);
        await db.Database.OpenConnectionAsync();
        await db.Database.EnsureCreatedAsync();
        var projectId = await MatrixProjectAsync(db, [VerificationArtifactKind.Case], activate: true);
        var (legacy, typed) = FullRegistrations();
        var authority = new SoftwareProcedureExecutionCutoverAuthority(db, legacy, typed,
            allowSqliteExecution: true);
        var result = await authority.EnsureCompletedAsync();
        Assert.Equal(0, result.ProjectsUpgraded);
        var active = await db.ProjectLadderConfigurations.AsNoTracking()
            .SingleAsync(x => x.ProjectId == projectId);
        Assert.Equal(ProjectLadderConfigurationState.Active, active.State);
        Assert.Equal("project.owner", active.ActivatedBy);
        Assert.Equal(0, await db.TestProcedures.AsNoTracking()
            .CountAsync(x => x.ProjectId == projectId
                && x.ArtifactKind == VerificationArtifactKind.Procedure));
    }

    [Fact]
    public async Task Retired_configuration_history_remains_untouched()
    {
        var options = new DbContextOptionsBuilder<AeroLinkDbContext>().UseSqlite("Data Source=:memory:").Options;
        var db = new AeroLinkDbContext(options);
        await db.Database.OpenConnectionAsync();
        await db.Database.EnsureCreatedAsync();
        var program = new ProgramRecord("Retired Program", $"RTP{Guid.NewGuid():N}"[..10]);
        var project = new ProjectRecord(program.Id, "Retired Software", "Retired Product");
        db.AddRange(program, project);
        var now = DateTimeOffset.UtcNow;
        var retired = (ProjectLadderConfiguration)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(
            typeof(ProjectLadderConfiguration));
        SetPrivate(retired, nameof(ProjectLadderConfiguration.Id), Guid.NewGuid());
        SetPrivate(retired, nameof(ProjectLadderConfiguration.ProjectId), project.Id);
        SetPrivate(retired, nameof(ProjectLadderConfiguration.Classification),
            ProjectLadderConfigurationClassification.NonDefault);
        SetPrivate(retired, nameof(ProjectLadderConfiguration.State),
            ProjectLadderConfigurationState.Retired);
        SetPrivate(retired, nameof(ProjectLadderConfiguration.IsSealed), true);
        SetPrivate(retired, nameof(ProjectLadderConfiguration.CreatedAt), now);
        SetPrivate(retired, nameof(ProjectLadderConfiguration.UpdatedAt), now);
        SetPrivate(retired, nameof(ProjectLadderConfiguration.Version), 1L);
        SetPrivate(retired, nameof(ProjectLadderConfiguration.VerificationProfileSchemaVersion), 2);
        SetPrivate(retired, nameof(ProjectLadderConfiguration.ActivatedAt), now);
        SetPrivate(retired, nameof(ProjectLadderConfiguration.ActivatedBy), "project.owner");
        SetPrivate(retired, nameof(ProjectLadderConfiguration.RetiredAt), now);
        SetPrivate(retired, nameof(ProjectLadderConfiguration.RetiredBy), "project.owner");
        SetPrivate(retired, nameof(ProjectLadderConfiguration.SealedAt), now);
        SetPrivate(retired, nameof(ProjectLadderConfiguration.SealedBy), "project.owner");
        SetPrivate(retired, nameof(ProjectLadderConfiguration.SealedContentKind), "test-case");
        SetPrivate(retired, nameof(ProjectLadderConfiguration.SealedContentIdentity), "retired-case");
        SetPrivate(retired, nameof(ProjectLadderConfiguration.ActivationManifestVersion),
            LadderConsumerManifestCatalog.VersionV2);
        SetPrivate(retired, nameof(ProjectLadderConfiguration.ActivationManifestHash), new string('0', 64));
        db.ProjectLadderConfigurations.Add(retired);
        await db.SaveChangesAsync();

        var (legacy, typed) = FullRegistrations();
        var authority = new SoftwareProcedureExecutionCutoverAuthority(db, legacy, typed,
            allowSqliteExecution: true);
        var result = await authority.EnsureCompletedAsync();
        Assert.Equal(0, result.ProjectsUpgraded);
        var persisted = await db.ProjectLadderConfigurations.AsNoTracking()
            .SingleAsync(x => x.ProjectId == project.Id);
        Assert.Equal(ProjectLadderConfigurationState.Retired, persisted.State);
        Assert.Equal("project.owner", persisted.RetiredBy);
        Assert.False(await db.SecurityAuditEvents.AsNoTracking()
            .AnyAsync(x => x.EventType.StartsWith("VerificationExecutionCutover")
                && x.Target == $"Project:{project.Id}"));
    }

    private static async Task<Guid> MatrixProjectAsync(AeroLinkDbContext db,
        IReadOnlyList<VerificationArtifactKind> softwareKinds, bool activate = false)
    {
        var now = DateTimeOffset.UtcNow;
        var tag = Guid.NewGuid().ToString("N")[..8];
        var program = new ProgramRecord("Matrix Program", $"MTX{tag}");
        var project = new ProjectRecord(program.Id, "Matrix Software", "Matrix Product");
        db.AddRange(program, project);
        var configuration = ProjectLadderConfiguration.CreateDraft(project.Id, now);
        var steps = new List<ProjectLadderStep>();
        foreach (var (level, position) in LegacyLadderPolicy.Instance.OrderedLevels.Select((x, i) => (x, i + 1)))
        {
            var kinds = level == RequirementLevel.System
                ? new[] { VerificationArtifactKind.Procedure }
                : softwareKinds;
            var step = new ProjectLadderStep(configuration.Id, project.Id, level, position,
                LegacyLadderPolicy.Instance.Definition(level).Capabilities, now, kinds);
            configuration.Steps.Add(step);
            steps.Add(step);
        }
        configuration.AllowedUpstream.Add(new ProjectLadderAllowedUpstream(
            configuration.Id, project.Id, steps[0].Id, steps[1].Id, now));
        configuration.AllowedUpstream.Add(new ProjectLadderAllowedUpstream(
            configuration.Id, project.Id, steps[1].Id, steps[2].Id, now));
        db.ProjectLadderConfigurations.Add(configuration);
        await db.SaveChangesAsync();
        var contentKind = LadderBoundContentCatalog.Current.First().Id;
        var seal = await new ProjectLadderSealAuthority(db).SealAsync(project.Id, contentKind,
            "matrix-content", "test.sealer", now);
        Assert.Equal(ProjectLadderSealResultKind.Sealed, seal.Kind);
        if (activate)
        {
            configuration.Activate("project.owner", now, LadderConsumerManifestCatalog.VersionV2,
                new string('0', 64));
            await db.SaveChangesAsync();
        }
        return project.Id;
    }

    private static void SetPrivate(ProjectLadderConfiguration configuration, string propertyName, object? value) =>
        typeof(ProjectLadderConfiguration).GetProperty(propertyName)!
            .GetSetMethod(nonPublic: true)!
            .Invoke(configuration, [value]);

    [Fact]
    public async Task New_project_default_is_case_plus_procedure_even_after_the_cutover_completed()
    {
        var seed = await SeedAsync();
        var (legacy, typed) = FullRegistrations();
        var authority = new SoftwareProcedureExecutionCutoverAuthority(seed.Db, legacy, typed,
            allowSqliteExecution: true);
        await authority.EnsureCompletedAsync();
        Assert.True(await seed.Db.SecurityAuditEvents.AsNoTracking()
            .AnyAsync(x => x.EventType == "VerificationExecutionCutover.SoftwareProcedures.v1.Completed"));

        var freshProjectId = Guid.NewGuid();
        var newConfiguration = NewProjectLadderFactory.Create(freshProjectId, DateTimeOffset.UtcNow);
        var highLevel = newConfiguration.Steps.Single(x => x.CatalogueEntry == nameof(RequirementLevel.HighLevel));
        var lowLevel = newConfiguration.Steps.Single(x => x.CatalogueEntry == nameof(RequirementLevel.LowLevel));
        Assert.Equal([VerificationArtifactKind.Case, VerificationArtifactKind.Procedure],
            highLevel.EnabledArtifactKinds);
        Assert.Equal([VerificationArtifactKind.Case, VerificationArtifactKind.Procedure],
            lowLevel.EnabledArtifactKinds);
        Assert.Equal(ProjectLadderConfigurationState.Draft, newConfiguration.State);
        Assert.Equal(ProjectLadderConfigurationClassification.NonDefault, newConfiguration.Classification);
    }

    [Fact]
    public async Task Multiple_case_revisions_map_to_one_coherent_procedure_artifact_with_exact_revisions()
    {
        var seed = await SeedTwoRevisionAsync();
        var db = seed.Db;
        var beforeCounts = new
        {
            Executions = await db.TestExecutions.AsNoTracking().CountAsync(),
            Entries = await db.BuildTestSetEntries.AsNoTracking().CountAsync(),
            Selections = await db.BaselineTestProcedures.AsNoTracking().CountAsync(),
            Impacts = await db.VerificationImpactItems.AsNoTracking().CountAsync(),
            CaseRevisions = await db.TestProcedureRevisions.AsNoTracking().CountAsync(),
        };

        var (legacy, typed) = FullRegistrations();
        var authority = new SoftwareProcedureExecutionCutoverAuthority(db, legacy, typed,
            allowSqliteExecution: true);
        var result = await authority.EnsureCompletedAsync();
        Assert.Equal(1, result.ProjectsUpgraded);
        Assert.Equal(2, result.ProceduresGenerated);
        Assert.Equal(2, result.ExecutionsRebound);
        Assert.Equal(2, result.TestSetEntriesRebound);
        Assert.Equal(2, result.BaselineSelectionsRebound);
        Assert.Equal(2, result.ImpactItemsRebound);

        Assert.Equal(1, await db.TestProcedures.AsNoTracking()
            .CountAsync(x => x.ProjectId == seed.ProjectId
                && x.ArtifactKind == VerificationArtifactKind.Procedure));
        var procedure = await db.TestProcedures.AsNoTracking()
            .SingleAsync(x => x.ProjectId == seed.ProjectId
                && x.ArtifactKind == VerificationArtifactKind.Procedure);
        var revisions = await db.TestProcedureRevisions.AsNoTracking()
            .Where(x => x.ProcedureId == procedure.Id).OrderBy(x => x.Revision).ToListAsync();
        Assert.Equal(2, revisions.Count);
        Assert.Equal([0, 1], revisions.Select(x => x.Revision).ToArray());
        Assert.All(revisions, revision => Assert.Equal("aerolink-migration", revision.AuthorId));
        var revisionByNumber = revisions.ToDictionary(x => x.Revision, x => x);
        Assert.Equal(seed.FirstBaselineId, revisionByNumber[0].EffectiveBaselineId);
        Assert.Equal(seed.SecondBaselineId, revisionByNumber[1].EffectiveBaselineId);

        var links = await db.TestCaseProcedureLinks.AsNoTracking().ToListAsync();
        Assert.Equal(2, links.Count);
        Assert.All(links, link => Assert.Equal(procedure.Id,
            revisions.Single(r => r.Id == link.ProcedureRevisionId).ProcedureId));
        var linkByCaseRevision = links.ToDictionary(x => x.CaseRevisionId, x => x.ProcedureRevisionId);
        Assert.Equal(revisionByNumber[0].Id, linkByCaseRevision[seed.FirstCaseRevisionId]);
        Assert.Equal(revisionByNumber[1].Id, linkByCaseRevision[seed.SecondCaseRevisionId]);

        // Exact baseline membership: both selections rebind to the exact Procedure artifact/revision pair;
        // no row may still reference the Case artifact or revision.
        var selections = await db.BaselineTestProcedures.AsNoTracking().ToListAsync();
        Assert.Equal(beforeCounts.Selections, selections.Count);
        Assert.All(selections, selection => Assert.Equal(procedure.Id, selection.ProcedureId));
        Assert.All(selections, selection => Assert.Contains(selection.RevisionId, revisions.Select(x => x.Id)));
        Assert.DoesNotContain(selections, x => x.ProcedureId == seed.CaseProcedureId
            || x.RevisionId == seed.FirstCaseRevisionId || x.RevisionId == seed.SecondCaseRevisionId);
        Assert.Contains(selections, x => x.BaselineId == seed.FirstBaselineId && x.RevisionId == revisionByNumber[0].Id);
        Assert.Contains(selections, x => x.BaselineId == seed.SecondBaselineId && x.RevisionId == revisionByNumber[1].Id);

        var executions = await db.TestExecutions.AsNoTracking()
            .Where(x => x.ProjectId == seed.ProjectId).ToListAsync();
        Assert.Equal(beforeCounts.Executions, executions.Count);
        Assert.Contains(executions, x => x.ProcedureRevisionId == revisionByNumber[0].Id && x.Outcome == TestOutcome.Pass);
        Assert.Contains(executions, x => x.ProcedureRevisionId == revisionByNumber[1].Id && x.Outcome == TestOutcome.Fail);

        var entries = await db.BuildTestSetEntries.AsNoTracking().ToListAsync();
        Assert.Equal(beforeCounts.Entries, entries.Count);
        Assert.Contains(entries, x => x.ProcedureRevisionId == revisionByNumber[0].Id);
        Assert.Contains(entries, x => x.ProcedureRevisionId == revisionByNumber[1].Id);

        var impacts = await db.VerificationImpactItems.AsNoTracking().ToListAsync();
        Assert.Equal(2, impacts.Count);
        var orphaned = impacts.Single(x => x.Trigger == VerificationImpactTrigger.ProcedureOrphaned);
        Assert.Equal(procedure.Id, orphaned.ProcedureId);
        var confirmed = impacts.Single(x => x.Trigger == VerificationImpactTrigger.RequirementIntroduced);
        Assert.Equal(procedure.Id, confirmed.ResolvedProcedureId);
        Assert.Equal(revisionByNumber[1].Id, confirmed.ResolvedProcedureRevisionId);

        // Counts and lineage survive the cutover exactly; no Case execution/entry/selection remains.
        Assert.Equal(beforeCounts.CaseRevisions,
            await db.TestProcedureRevisions.AsNoTracking().CountAsync(x => x.ProcedureId == seed.CaseProcedureId));
        Assert.Equal(0, await db.TestExecutions.AsNoTracking()
            .CountAsync(x => x.ProcedureRevisionId == seed.FirstCaseRevisionId
                || x.ProcedureRevisionId == seed.SecondCaseRevisionId));
        Assert.Equal(0, await db.BuildTestSetEntries.AsNoTracking()
            .CountAsync(x => x.ProcedureRevisionId == seed.FirstCaseRevisionId
                || x.ProcedureRevisionId == seed.SecondCaseRevisionId));

        // Rerun is idempotent and never duplicates a generated revision, link, or reference.
        var rerun = await authority.EnsureCompletedAsync();
        Assert.Equal(0, rerun.ProceduresGenerated);
        Assert.Equal(1, await db.TestProcedures.AsNoTracking()
            .CountAsync(x => x.ProjectId == seed.ProjectId
                && x.ArtifactKind == VerificationArtifactKind.Procedure));
        Assert.Equal(2, await db.TestProcedureRevisions.AsNoTracking().CountAsync(x => x.ProcedureId == procedure.Id));
        Assert.Equal(2, await db.TestCaseProcedureLinks.AsNoTracking().CountAsync());
        Assert.Equal(beforeCounts.Selections, await db.BaselineTestProcedures.AsNoTracking().CountAsync());
        Assert.Equal(beforeCounts.Executions, await db.TestExecutions.AsNoTracking().CountAsync());
        Assert.Equal(beforeCounts.Entries, await db.BuildTestSetEntries.AsNoTracking().CountAsync());
        Assert.Equal(2, await db.VerificationImpactItems.AsNoTracking().CountAsync());
        Assert.Equal(1, await db.SecurityAuditEvents.AsNoTracking()
            .CountAsync(x => x.EventType == "VerificationExecutionCutover.SoftwareProcedures.v1.ProjectUpgraded"
                && x.Target == $"Project:{seed.ProjectId}"));
    }

    private sealed record TwoRevisionSeed(AeroLinkDbContext Db, Guid ProjectId, Guid ReleaseId,
        Guid FirstBaselineId, Guid SecondBaselineId, Guid CaseProcedureId,
        Guid FirstCaseRevisionId, Guid SecondCaseRevisionId);

    private static async Task<TwoRevisionSeed> SeedTwoRevisionAsync()
    {
        var options = new DbContextOptionsBuilder<AeroLinkDbContext>().UseSqlite("Data Source=:memory:").Options;
        var db = new AeroLinkDbContext(options);
        await db.Database.OpenConnectionAsync();
        await db.Database.EnsureCreatedAsync();
        var now = DateTimeOffset.UtcNow;

        var program = new ProgramRecord("Two Revision Program", "2REV");
        var project = new ProjectRecord(program.Id, "Two Revision Software", "Two Revision Product");
        var release = new SoftwareRelease(project.Id, "2.0", false);
        var firstBaseline = new CandidateBaseline("SW-02.00", 0, project.Id, release.Id, null,
            "First candidate", "cm.test", now);
        var secondBaseline = new CandidateBaseline("SW-02.01", 0, project.Id, release.Id, firstBaseline.Id,
            "Second candidate", "cm.test", now.AddDays(1));
        db.AddRange(program, project, release, firstBaseline, secondBaseline);

        var configuration = LegacyDefaultProjectLadderFactory.Create(project.Id, now);
        db.ProjectLadderConfigurations.Add(configuration);
        await db.SaveChangesAsync();
        var contentKind = LadderBoundContentCatalog.Current.First().Id;
        var seal = await new ProjectLadderSealAuthority(db).SealAsync(project.Id, contentKind,
            "two-revision-content", "test.sealer", now);
        Assert.Equal(ProjectLadderSealResultKind.Sealed, seal.Kind);

        var scr = new SystemChangeRequest("HLRCR-00200", 0, project.Id, release.Id,
            "Sequencing change", "Problem", "Analysis", "Solution", "author", now,
            type: ChangeRequestType.Software, softwareLevel: RequirementLevel.HighLevel);
        var requirementChange = scr.AddRequirementChange("author", "HLR-00000201", 0,
            RequirementLevel.HighLevel, RequirementChangeKind.Introduce,
            "The system shall sequence oceanic waypoints.", "New capability", "Test", now);
        var review = new TestChangeReview(project.Id, release.Id, scr.Id,
            TestChangeReviewDiscipline.HighLevelSoftware, "HLRCR-00200", now);
        var caseArtifact = new TestProcedure(project.Id, "HLRTC-000201", "Two revision sequencing case",
            "test.engineer", now, TestProcedureLevel.HighLevel);
        var firstRevision = new TestProcedureRevision(caseArtifact.Id, 0,
            "Verify sequencing v1", "Logical preconditions", "Scenario steps v1", "Pass criteria v1",
            TestProcedureState.Approved, "test.engineer", now, effectiveBaselineId: firstBaseline.Id);
        var secondRevision = new TestProcedureRevision(caseArtifact.Id, 1,
            "Verify sequencing v2", "Logical preconditions v2", "Scenario steps v2", "Pass criteria v2",
            TestProcedureState.Approved, "test.engineer", now.AddDays(1), effectiveBaselineId: secondBaseline.Id);
        var firstExecution = new TestExecution(project.Id, firstRevision.Id, null, null, TestOutcome.Pass,
            "test.engineer", "Rig A", "Human determination", "evidence/a.json", now, now, release.Id);
        var secondExecution = new TestExecution(project.Id, secondRevision.Id, null, null, TestOutcome.Fail,
            "test.engineer", "Rig B", "Human determination", "evidence/b.json", now.AddDays(2),
            now.AddDays(2), release.Id);
        var set = new BuildTestSet(project.Id, release.Id, TestChangeReviewDiscipline.HighLevelSoftware, now);
        set.Include("test.lead", firstRevision.Id, TestSelectionReason.Chosen, "", now);
        set.Include("test.lead", secondRevision.Id, TestSelectionReason.Chosen, "", now);
        var firstSelection = new BaselineTestProcedureSelection(firstBaseline.Id, caseArtifact.Id, firstRevision.Id);
        var secondSelection = new BaselineTestProcedureSelection(secondBaseline.Id, caseArtifact.Id, secondRevision.Id);
        var orphanedImpact = VerificationImpactItem.ForOrphanedProcedure(project.Id, release.Id,
            scr.Id, review.Id, caseArtifact.Id, "HLRTC-000201", now);
        var confirmedImpact = VerificationImpactItem.ForIntroducedRequirement(project.Id, release.Id,
            scr.Id, review.Id, requirementChange.Id, "HLRTC-000201", "Test", now);
        confirmedImpact.Resolve("test.engineer", VerificationImpactOutcome.ProcedureCoverageConfirmed,
            "The procedure covers the sequencing behaviour introduced by this change.", now,
            procedureId: caseArtifact.Id, procedureRevisionId: secondRevision.Id);
        db.AddRange(scr, review, caseArtifact, firstRevision, secondRevision, firstExecution, secondExecution, set,
            firstSelection, secondSelection, orphanedImpact, confirmedImpact);
        await db.SaveChangesAsync();
        return new TwoRevisionSeed(db, project.Id, release.Id, firstBaseline.Id, secondBaseline.Id,
            caseArtifact.Id, firstRevision.Id, secondRevision.Id);
    }

    [Fact]
    public async Task Cutover_produces_one_coherent_execution_manifest_used_by_readiness_and_evidence_gates()
    {
        var options = new DbContextOptionsBuilder<AeroLinkDbContext>().UseSqlite("Data Source=:memory:").Options;
        var db = new AeroLinkDbContext(options);
        await db.Database.OpenConnectionAsync();
        await db.Database.EnsureCreatedAsync();
        var now = DateTimeOffset.UtcNow;

        var program = new ProgramRecord("End To End Program", "E2E");
        var project = new ProjectRecord(program.Id, "End To End Software", "E2E Product");
        var release = new SoftwareRelease(project.Id, "5.0", false);
        db.AddRange(program, project, release);
        var configuration = LegacyDefaultProjectLadderFactory.Create(project.Id, now);
        db.ProjectLadderConfigurations.Add(configuration);
        await db.SaveChangesAsync();
        var contentKind = LadderBoundContentCatalog.Current.First().Id;
        var seal = await new ProjectLadderSealAuthority(db).SealAsync(project.Id, contentKind,
            "e2e-content", "test.sealer", now);
        Assert.Equal(ProjectLadderSealResultKind.Sealed, seal.Kind);

        var predecessorScr = new SystemChangeRequest("HLRCR-00499", 0, project.Id, release.Id,
            "Predecessor change", "Problem", "Analysis", "Solution", "author", now.AddDays(-2),
            type: ChangeRequestType.Software, softwareLevel: RequirementLevel.HighLevel);
        predecessorScr.AddRequirementChange("author", "HLR-00000499", 0, RequirementLevel.HighLevel,
            RequirementChangeKind.Introduce, "The predecessor shall sequence.", "Rationale", "Test",
            now.AddDays(-2), attributesJson: "{\"derived\":true}");
        predecessorScr.SubmitForReview("author", [new ApproverSelection("reviewer", "Reviewer")],
            now.AddDays(-2));
        predecessorScr.ApproveActiveStage("reviewer", now.AddDays(-2));
        var predecessor = new CandidateBaseline("SW-04.99", 0, project.Id, release.Id, null,
            "Predecessor", "cm.test", now.AddDays(-1));
        predecessor.Select(predecessorScr, "cm.test", now.AddDays(-1));
        predecessor.Freeze("cm.test", now.AddDays(-1));
        predecessor.MarkRequirementsMaterialized("cm.test", new string('d', 64), 0, now.AddDays(-1));
        var baseline = new CandidateBaseline("SW-05.00", 0, project.Id, release.Id, predecessor.Id,
            "Candidate", "cm.test", now);
        var build = new SoftwareBuild(project.Id, release.Id, baseline.Id, "B-0500",
            "Candidate build", "cm.test", now);
        var campaign = new ReleaseCampaign(project.Id, release.Id, baseline.Id, "5.0",
            "program.manager", now);
        campaign.SelectVerificationBuild(build.Id, "program.manager", now);
        db.AddRange(predecessorScr, predecessor, baseline, build, campaign);

        var scr = new SystemChangeRequest("HLRCR-00500", 0, project.Id, release.Id,
            "Sequencing change", "Problem", "Analysis", "Solution", "author", now,
            type: ChangeRequestType.Software, softwareLevel: RequirementLevel.HighLevel);
        scr.AddRequirementChange("author", "HLR-00000501", 0, RequirementLevel.HighLevel,
            RequirementChangeKind.Introduce, "The system shall sequence.", "Rationale", "Test", now,
            attributesJson: "{\"derived\":true}");
        scr.SubmitForReview("author", [new ApproverSelection("reviewer", "Reviewer")], now);
        scr.ApproveActiveStage("reviewer", now);

        var requirementArtifact = new RequirementArtifact(project.Id, "HLR-00000501",
            RequirementLevel.HighLevel, now);
        var requirementRevision = new RequirementRevision(requirementArtifact.Id, 0,
            "The system shall sequence.", "Rationale", "Test", RequirementRevisionState.Active,
            scr.Id, baseline.Id, now, parentKind: RequirementParentKind.Derived,
            derivedRationale: "The sequencing requirement is derived from the navigation concept.");
        var caseArtifact = new TestProcedure(project.Id, "HLRTC-000501", "Sequencing case",
            "test.engineer", now, TestProcedureLevel.HighLevel);
        var caseRevision = new TestProcedureRevision(caseArtifact.Id, 0,
            "Verify sequencing", "Logical preconditions", "Scenario steps", "Pass criteria",
            TestProcedureState.Approved, "test.engineer", now, effectiveBaselineId: baseline.Id);
        var coverage = new TestRequirementCoverage(caseRevision.Id, requirementRevision.Id);
        var selection = new BaselineTestProcedureSelection(baseline.Id, caseArtifact.Id, caseRevision.Id);
        var requirementSelection = new BaselineRequirementSelection(baseline.Id,
            requirementArtifact.Id, requirementRevision.Id);
        var set = new BuildTestSet(project.Id, release.Id, TestChangeReviewDiscipline.HighLevelSoftware, now);
        set.Include("test.lead", caseRevision.Id, TestSelectionReason.Chosen, "", now);
        var execution = new TestExecution(project.Id, caseRevision.Id, build.Id, null, TestOutcome.Pass,
            "test.engineer", "Rig A", "Human determination", "evidence/e2e.json", now, now, release.Id);
        var evidenceRecord = new EvidenceRecord(project.Id, "e2e.json", "application/json", 32,
            new string('f', 64), "storage/e2e.json", "test.engineer", now);
        var evidence = new TestExecutionEvidence(execution.Id, evidenceRecord.Id);
        db.AddRange(scr, requirementArtifact, requirementRevision, caseArtifact, caseRevision,
            coverage, selection, requirementSelection, set, execution, evidenceRecord, evidence);
        baseline.Select(scr, "cm.test", now);
        baseline.Freeze("cm.test", now);
        baseline.MarkRequirementsMaterialized("cm.test", new string('e', 64), 0, now);
        baseline.MarkTestProceduresMaterialized("cm.test", new string('f', 64), 1, now);
        using (db.UseLegacyHistoricalSeed()) await db.SaveChangesAsync();

        var (legacy, typed) = FullRegistrations();
        var authority = new SoftwareProcedureExecutionCutoverAuthority(db, legacy, typed,
            allowSqliteExecution: true);
        var result = await authority.EnsureCompletedAsync();
        Assert.Equal(1, result.ProjectsUpgraded);
        Assert.Equal(1, result.ProceduresGenerated);
        Assert.Equal(1, result.ExecutionsRebound);
        Assert.Equal(1, result.TestSetEntriesRebound);
        Assert.Equal(1, result.BaselineSelectionsRebound);

        var procedure = await db.TestProcedures.AsNoTracking()
            .SingleAsync(x => x.ProjectId == project.Id
                && x.ArtifactKind == VerificationArtifactKind.Procedure);
        var procedureRevision = await db.TestProcedureRevisions.AsNoTracking()
            .SingleAsync(x => x.ProcedureId == procedure.Id);
        var link = await db.TestCaseProcedureLinks.AsNoTracking().SingleAsync();
        Assert.Equal(caseRevision.Id, link.CaseRevisionId);
        Assert.Equal(procedureRevision.Id, link.ProcedureRevisionId);

        // One typed membership contract: the baseline manifest, BuildTestSet, execution, and evidence all
        // reference the exact same generated Procedure revision.
        var membership = await BaselineExecutableMembership.ForBaselineAsync(db, baseline.Id, default);
        var member = Assert.Single(membership);
        Assert.Equal(procedure.Id, member.ProcedureId);
        Assert.Equal(procedureRevision.Id, member.RevisionId);
        Assert.Equal(VerificationArtifactKind.Procedure, member.Kind);
        var effectivity = await TestProcedureEffectivity.ForBaselineAsync(db, baseline.Id, default);
        Assert.NotNull(effectivity);
        Assert.Equal(procedureRevision.Id, Assert.Single(effectivity.RevisionIds));

        var entry = await db.BuildTestSetEntries.AsNoTracking().SingleAsync();
        Assert.Equal(procedureRevision.Id, entry.ProcedureRevisionId);
        var executionAfter = await db.TestExecutions.AsNoTracking().SingleAsync();
        Assert.Equal(procedureRevision.Id, executionAfter.ProcedureRevisionId);
        var evidenceAfter = await db.TestExecutionEvidence.AsNoTracking().SingleAsync();
        Assert.Equal(executionAfter.Id, evidenceAfter.TestExecutionId);

        // Requirement coverage stays on the exact Case revision; the cutover never rewrites coverage.
        var coverageAfter = await db.TestCoverage.AsNoTracking().SingleAsync();
        Assert.Equal(caseRevision.Id, coverageAfter.ProcedureRevisionId);
        Assert.Equal(requirementRevision.Id, coverageAfter.RequirementRevisionId);

        var policyResolver = new EffectiveProjectLadderPolicyResolver(db);
        var obligations = await CaseProcedureSatisfaction.ForBaselineAsync(db, baseline.Id, release.Id,
            build.Id, new HashSet<TestProcedureLevel> { TestProcedureLevel.HighLevel }, default);
        Assert.True(Assert.Single(obligations).Satisfied);

        var readiness = await new ReleaseReadinessService(db, policyResolver: policyResolver)
            .CalculateAsync(campaign.Id, default);
        var coverageGate = readiness.Gates.Single(x => x.Code == "coverage");
        Assert.True(coverageGate.Complete);
        Assert.Equal(1, coverageGate.Completed);
        Assert.Equal(1, coverageGate.Total);
        var verificationGate = readiness.Gates.Single(x => x.Code == "verification");
        Assert.True(verificationGate.Complete);
        Assert.Equal(1, verificationGate.Completed);
        Assert.Equal(1, verificationGate.Total);
        var evidenceGate = readiness.Gates.Single(x => x.Code == "evidence");
        Assert.True(evidenceGate.Complete);
        Assert.Equal(1, evidenceGate.Completed);
        Assert.Equal(1, evidenceGate.Total);

        var evidenceRoot = Path.Combine(Path.GetTempPath(), $"aerolink-e2e-{Guid.NewGuid():N}");
        var store = new EvidenceFileStore(evidenceRoot);
        var executionService = new ReleaseExecutionService(db, store, policyResolver);
        var template = await executionService.CreateVerificationTemplateAsync(campaign.Id, default);
        var rows = JsonSerializer.Deserialize<List<VerificationManifestRow>>(template);
        Assert.NotNull(rows);
        var manifestRow = Assert.Single(rows);
        Assert.Equal(procedureRevision.Id, manifestRow.ProcedureRevisionId);
        var reconciliation = await executionService.ReconcileAsync(campaign.Id, "test.lead", now, default);
        Assert.Equal(0, reconciliation.UnsatisfiedCaseObligations);
        try { if (Directory.Exists(evidenceRoot)) Directory.Delete(evidenceRoot, recursive: true); }
        catch (IOException) { }
    }
}
