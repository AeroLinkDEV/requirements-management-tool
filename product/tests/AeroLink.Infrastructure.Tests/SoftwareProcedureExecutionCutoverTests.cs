using AeroLink.Domain.Baselines;
using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Common;
using AeroLink.Domain.Hierarchy;
using AeroLink.Domain.Identity;
using AeroLink.Domain.Programs;
using AeroLink.Domain.Releases;
using AeroLink.Domain.Requirements;
using AeroLink.Domain.Traceability;
using AeroLink.Domain.Verification;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using System.IO.Compression;
using System.Text.Json;
using System.Text;

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

    private static async Task<Seed> SeedIntoAsync(AeroLinkDbContext db, string tag)
    {
        var now = DateTimeOffset.UtcNow;
        var program = new ProgramRecord($"{tag} Program", $"CUT{tag[^3..]}");
        var project = new ProjectRecord(program.Id, $"{tag} Software", $"{tag} Product");
        var release = new SoftwareRelease(project.Id, "1.6", false);
        var baseline = new CandidateBaseline($"SW-01.{tag[^2..]}", 0, project.Id, release.Id, null,
            "Candidate", "cm.test", now);
        db.AddRange(program, project, release, baseline);
        var configuration = LegacyDefaultProjectLadderFactory.Create(project.Id, now);
        db.ProjectLadderConfigurations.Add(configuration);
        await db.SaveChangesAsync();
        var seal = await new ProjectLadderSealAuthority(db).SealAsync(project.Id,
            LadderBoundContentCatalog.Current.First().Id, $"{tag}-content", "test.sealer", now);
        Assert.Equal(ProjectLadderSealResultKind.Sealed, seal.Kind);
        var caseArtifact = new TestProcedure(project.Id, $"HLRTC-{tag[^6..]}", $"{tag} sequencing case",
            "test.engineer", now, TestProcedureLevel.HighLevel);
        var caseRevision = new TestProcedureRevision(caseArtifact.Id, 0,
            "Verify sequencing", "Logical preconditions", "Scenario steps", "Pass criteria",
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

    private static SystemChangeRequest ApprovedBaselineScr(Guid projectId, Guid releaseId, string number,
        DateTimeOffset now)
    {
        var scr = new SystemChangeRequest(number, 0, projectId, releaseId,
            "Baseline authority", "Problem", "Analysis", "Solution", "author", now);
        scr.AddRequirementChange("author", $"SYSR-{number[^5..]}", 0, RequirementLevel.System,
            RequirementChangeKind.Introduce, "The system shall retain the controlled fixture behavior.",
            "Baseline fixture authority.", "Analysis", now);
        scr.SubmitForReview("author", [new ApproverSelection("reviewer", "Reviewer")], now);
        scr.ApproveActiveStage("reviewer", now);
        return scr;
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
        // A completed cutover is idempotent: rerun performs no writes and reports the same honest totals
        // recovered from persisted governed evidence rather than a misleading zero.
        Assert.Equal(result, rerun);
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
        Assert.Equal(result, rerun);
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

    [Fact]
    public async Task Retired_case_revision_history_migrates_without_an_active_claim()
    {
        var options = new DbContextOptionsBuilder<AeroLinkDbContext>().UseSqlite("Data Source=:memory:").Options;
        var db = new AeroLinkDbContext(options);
        await db.Database.OpenConnectionAsync();
        await db.Database.EnsureCreatedAsync();
        var now = DateTimeOffset.UtcNow;
        var program = new ProgramRecord("Retired Case Program", "RCP");
        var project = new ProjectRecord(program.Id, "Retired Case Software", "Retired Case Product");
        var release = new SoftwareRelease(project.Id, "1.0", false);
        var baseline = new CandidateBaseline("SW-01.00", 0, project.Id, release.Id, null,
            "Candidate", "cm.test", now);
        db.AddRange(program, project, release, baseline);
        var configuration = LegacyDefaultProjectLadderFactory.Create(project.Id, now);
        db.ProjectLadderConfigurations.Add(configuration);
        await db.SaveChangesAsync();
        var seal = await new ProjectLadderSealAuthority(db).SealAsync(project.Id,
            LadderBoundContentCatalog.Current.First().Id, "retired-case-content", "test.sealer", now);
        Assert.Equal(ProjectLadderSealResultKind.Sealed, seal.Kind);

        var caseArtifact = new TestProcedure(project.Id, "HLRTC-000601", "Retired sequencing case",
            "test.engineer", now, TestProcedureLevel.HighLevel);
        // A retired Case revision has no body; it is history, not an active claim.
        var retiredRevision = new TestProcedureRevision(caseArtifact.Id, 0, "", "", "", "",
            TestProcedureState.Retired, "test.engineer", now);
        var activeRevision = new TestProcedureRevision(caseArtifact.Id, 1,
            "Verify sequencing v2", "Logical preconditions", "Scenario steps v2", "Pass criteria v2",
            TestProcedureState.Approved, "test.engineer", now.AddDays(1),
            effectiveBaselineId: baseline.Id);
        var selection = new BaselineTestProcedureSelection(baseline.Id, caseArtifact.Id, activeRevision.Id);
        db.AddRange(caseArtifact, retiredRevision, activeRevision, selection);
        var scr = ApprovedBaselineScr(project.Id, release.Id, "SRCR-00726", now);
        baseline.Select(scr, "cm.test", now);
        baseline.Freeze("cm.test", now);
        baseline.MarkRequirementsMaterialized("cm.test", new string('a', 64), 0, now);
        baseline.MarkTestProceduresMaterialized("cm.test", new string('b', 64), 1, now);
        await db.SaveChangesAsync();

        var (legacy, typed) = FullRegistrations();
        var authority = new SoftwareProcedureExecutionCutoverAuthority(db, legacy, typed,
            allowSqliteExecution: true);
        var result = await authority.EnsureCompletedAsync();
        Assert.Equal(1, result.ProjectsUpgraded);
        Assert.Equal(2, result.ProceduresGenerated);
        var procedure = await db.TestProcedures.AsNoTracking()
            .SingleAsync(x => x.ProjectId == project.Id
                && x.ArtifactKind == VerificationArtifactKind.Procedure);
        var revisions = await db.TestProcedureRevisions.AsNoTracking()
            .Where(x => x.ProcedureId == procedure.Id).OrderBy(x => x.Revision).ToListAsync();
        Assert.Equal([0, 1], revisions.Select(x => x.Revision).ToArray());
        Assert.Equal(TestProcedureState.Retired, revisions[0].State);
        Assert.Equal(TestProcedureState.Approved, revisions[1].State);
        Assert.All(revisions, revision => Assert.Equal("aerolink-migration", revision.AuthorId));
        // Only the active Case revision gains an exact parent link; the retired history is mirrored without
        // manufacturing an executable claim.
        var links = await db.TestCaseProcedureLinks.AsNoTracking().ToListAsync();
        var link = Assert.Single(links);
        Assert.Equal(activeRevision.Id, link.CaseRevisionId);
        Assert.Equal(revisions[1].Id, link.ProcedureRevisionId);
        var rerun = await authority.EnsureCompletedAsync();
        Assert.Equal(result, rerun);
        Assert.Equal(2, await db.TestProcedureRevisions.AsNoTracking()
            .CountAsync(x => x.ProcedureId == procedure.Id));
    }

    [Fact]
    public async Task Configuration_only_upgrade_counts_honestly_and_records_audit()
    {
        var options = new DbContextOptionsBuilder<AeroLinkDbContext>().UseSqlite("Data Source=:memory:").Options;
        var db = new AeroLinkDbContext(options);
        await db.Database.OpenConnectionAsync();
        await db.Database.EnsureCreatedAsync();
        var now = DateTimeOffset.UtcNow;
        var program = new ProgramRecord("Config Only Program", "CFG");
        var project = new ProjectRecord(program.Id, "Config Only Software", "Config Only Product");
        db.AddRange(program, project);
        var configuration = LegacyDefaultProjectLadderFactory.Create(project.Id, now);
        db.ProjectLadderConfigurations.Add(configuration);
        await db.SaveChangesAsync();
        var seal = await new ProjectLadderSealAuthority(db).SealAsync(project.Id,
            LadderBoundContentCatalog.Current.First().Id, "config-only-content", "test.sealer", now);
        Assert.Equal(ProjectLadderSealResultKind.Sealed, seal.Kind);
        await db.SaveChangesAsync();

        var (legacy, typed) = FullRegistrations();
        var authority = new SoftwareProcedureExecutionCutoverAuthority(db, legacy, typed,
            allowSqliteExecution: true);
        var result = await authority.EnsureCompletedAsync();
        Assert.Equal(1, result.ProjectsUpgraded);
        Assert.Equal(0, result.ProceduresGenerated);
        var upgraded = await db.ProjectLadderConfigurations.AsNoTracking()
            .SingleAsync(x => x.ProjectId == project.Id);
        Assert.Equal(ProjectLadderConfigurationState.Active, upgraded.State);
        Assert.True(await db.SecurityAuditEvents.AsNoTracking()
            .AnyAsync(x => x.EventType == "VerificationExecutionCutover.SoftwareProcedures.v1.ProjectUpgraded"
                && x.Target == $"Project:{project.Id}"));
    }

    [Fact]
    public async Task Crash_before_completed_marker_recovers_honest_totals_on_rerun()
    {
        var options = new DbContextOptionsBuilder<AeroLinkDbContext>().UseSqlite("Data Source=:memory:").Options;
        var db = new AeroLinkDbContext(options);
        await db.Database.OpenConnectionAsync();
        await db.Database.EnsureCreatedAsync();
        await SeedIntoAsync(db, "000001");
        await SeedIntoAsync(db, "000002");
        var (legacy, typed) = FullRegistrations();
        var firstRun = await new SoftwareProcedureExecutionCutoverAuthority(
            db, legacy, typed, allowSqliteExecution: true).EnsureCompletedAsync();
        Assert.Equal(2, firstRun.ProjectsUpgraded);

        // Simulate a crash between the per-project work and the global Completed marker: neither the
        // completion claim nor its audit event was persisted.
        var completed = await db.SecurityAuditEvents
            .Where(x => x.EventType == "VerificationExecutionCutover.SoftwareProcedures.v1.Completed")
            .ToListAsync();
        db.SecurityAuditEvents.RemoveRange(completed);
        var completionClaims = await db.GovernedMigrationCompletions.ToListAsync();
        db.GovernedMigrationCompletions.RemoveRange(completionClaims);
        await db.SaveChangesAsync();
        var recovery = await new SoftwareProcedureExecutionCutoverAuthority(
            db, legacy, typed, allowSqliteExecution: true).EnsureCompletedAsync();
        Assert.Equal(2, recovery.ProjectsUpgraded);
        Assert.Equal(firstRun.ProceduresGenerated, recovery.ProceduresGenerated);
        Assert.Equal(firstRun.ExecutionsRebound, recovery.ExecutionsRebound);
        Assert.Equal(firstRun.TestSetEntriesRebound, recovery.TestSetEntriesRebound);
        Assert.Equal(firstRun.BaselineSelectionsRebound, recovery.BaselineSelectionsRebound);
        Assert.Equal(firstRun.ImpactItemsRebound, recovery.ImpactItemsRebound);
        Assert.True(await db.SecurityAuditEvents.AsNoTracking()
            .AnyAsync(x => x.EventType == "VerificationExecutionCutover.SoftwareProcedures.v1.Completed"));
    }

    [Fact]
    public async Task Materialized_baseline_manifest_and_controlled_documents_are_recomputed_with_signature_supersession()
    {
        var options = new DbContextOptionsBuilder<AeroLinkDbContext>().UseSqlite("Data Source=:memory:").Options;
        var db = new AeroLinkDbContext(options);
        await db.Database.OpenConnectionAsync();
        await db.Database.EnsureCreatedAsync();
        var now = DateTimeOffset.UtcNow;
        var program = new ProgramRecord("Document Cutover Program", "DOC");
        var project = new ProjectRecord(program.Id, "Document Cutover Software", "Document Cutover Product");
        var release = new SoftwareRelease(project.Id, "1.0", false);
        var baseline = new CandidateBaseline("SW-01.00", 0, project.Id, release.Id, null,
            "Candidate", "cm.test", now);
        db.AddRange(program, project, release, baseline);
        var configuration = LegacyDefaultProjectLadderFactory.Create(project.Id, now);
        db.ProjectLadderConfigurations.Add(configuration);
        await db.SaveChangesAsync();
        var seal = await new ProjectLadderSealAuthority(db).SealAsync(project.Id,
            LadderBoundContentCatalog.Current.First().Id, "document-content", "test.sealer", now);
        Assert.Equal(ProjectLadderSealResultKind.Sealed, seal.Kind);

        var caseArtifact = new TestProcedure(project.Id, "HLRTC-000701", "Documented sequencing case",
            "test.engineer", now, TestProcedureLevel.HighLevel);
        var caseRevision = new TestProcedureRevision(caseArtifact.Id, 0,
            "Verify documented sequencing", "Logical preconditions", "Scenario steps", "Pass criteria",
            TestProcedureState.Approved, "test.engineer", now, effectiveBaselineId: baseline.Id);
        var selection = new BaselineTestProcedureSelection(baseline.Id, caseArtifact.Id, caseRevision.Id);
        db.AddRange(caseArtifact, caseRevision, selection);
        var scr = ApprovedBaselineScr(project.Id, release.Id, "SRCR-00727", now);
        baseline.Select(scr, "cm.test", now);
        baseline.Freeze("cm.test", now);
        baseline.MarkRequirementsMaterialized("cm.test", new string('a', 64), 0, now);
        baseline.MarkTestProceduresMaterialized("cm.test", new string('b', 64), 1, now);
        baseline.MarkReleased("cm.test", now.AddMinutes(1));
        await db.SaveChangesAsync();

        var oldManifestHash = await db.CandidateBaselines.AsNoTracking()
            .Where(x => x.Id == baseline.Id).Select(x => x.TestProceduresHash).SingleAsync();
        var evidenceRoot = Path.Combine(Path.GetTempPath(), $"aerolink-726-doc-{Guid.NewGuid():N}");
        var files = new EvidenceFileStore(evidenceRoot);
        var oldBytes = Encoding.UTF8.GetBytes("pre-cutover controlled case document bytes");
        var stored = await files.StoreAsync(new MemoryStream(oldBytes), "cases.docx",
            "application/vnd.openxmlformats-officedocument.wordprocessingml.document", default);
        var document = new ControlledDocument(project.Id, release.Id, baseline.Id,
            ControlledDocumentType.HighLevelTestCases, "HLRTD-000726", "HLR Test Cases", 0,
            new string('c', 64), 1, now);
        var artifact = new ControlledDocumentArtifact(document.Id, "docx", stored.StorageKey,
            stored.OriginalFileName, stored.ContentType, stored.Size, stored.Sha256, now);
        var artifactSignature = new ElectronicSignature(Guid.NewGuid(), "reviewer", "Reviewer", program.Id,
            "ControlledDocumentArtifact", artifact.Id, "HLRTD-000726.00/docx", "Approve",
            "old output", artifact.Sha256, "127.0.0.1", now);
        var documentSignature = new ElectronicSignature(Guid.NewGuid(), "reviewer", "Reviewer", program.Id,
            "ControlledDocument", document.Id, "HLRTD-000726.00", "Approve",
            "old document", document.ContentHash, "127.0.0.1", now);
        db.AddRange(document, artifact, artifactSignature, documentSignature);
        await db.SaveChangesAsync();

        var (legacy, typed) = FullRegistrations();
        var authority = new SoftwareProcedureExecutionCutoverAuthority(db, legacy, typed,
            allowSqliteExecution: true, generator: new ControlledOutputGenerator(db,
                new RichContentPublisher(db, files),
                policyResolver: new EffectiveProjectLadderPolicyResolver(db)), files: files);
        var result = await authority.EnsureCompletedAsync();
        Assert.Equal(1, result.ProjectsUpgraded);

        var updatedBaseline = await db.CandidateBaselines.AsNoTracking().SingleAsync(x => x.Id == baseline.Id);
        Assert.Equal(CandidateBaselineState.Released, updatedBaseline.State);
        Assert.NotEqual(oldManifestHash, updatedBaseline.TestProceduresHash);
        var procedure = await db.TestProcedures.AsNoTracking()
            .SingleAsync(x => x.ProjectId == project.Id
                && x.ArtifactKind == VerificationArtifactKind.Procedure);
        var procedureRevision = await db.TestProcedureRevisions.AsNoTracking()
            .SingleAsync(x => x.ProcedureId == procedure.Id);
        var dbEntries = await (from member in db.BaselineTestProcedures.AsNoTracking()
                               where member.BaselineId == baseline.Id
                               join revision in db.TestProcedureRevisions.AsNoTracking()
                                   on member.RevisionId equals revision.Id
                               join p in db.TestProcedures.AsNoTracking()
                                   on member.ProcedureId equals p.Id
                               select new TestProcedureManifestEntry(p.Id, revision.Id,
                                   p.BaseNumber, revision.Revision)).ToListAsync();
        Assert.Equal(TestProcedureManifest.Hash(dbEntries), updatedBaseline.TestProceduresHash);
        var manifestEvent = await db.BaselineEvents.AsNoTracking()
            .SingleAsync(x => x.BaselineId == baseline.Id
                && x.EventType == "ExecutionCutoverManifestMigrated");
        Assert.Contains("Case-to-Procedure provenance", manifestEvent.Detail, StringComparison.Ordinal);
        Assert.Contains("executable membership changed", manifestEvent.Detail, StringComparison.Ordinal);
        // Finding 5/Blocker 2: the event detail carries only a BOUNDED summary (mappings/chunks/canonical
        // hash) — never project-wide counts — while the exact per-baseline Case→Procedure identities live in
        // durable, sequence-numbered provenance rows that stay under the 4,000-character detail limit.
        Assert.Contains("mappings=", manifestEvent.Detail, StringComparison.Ordinal);
        Assert.True(manifestEvent.Detail.Length < 4000);
        Assert.DoesNotContain("generatedProcedureRevisions=", manifestEvent.Detail, StringComparison.Ordinal);
        var provenanceRows = await db.BaselineExecutionCutoverProvenances.AsNoTracking()
            .Where(x => x.BaselineId == baseline.Id)
            .OrderBy(x => x.Sequence).ToListAsync();
        Assert.NotEmpty(provenanceRows);
        var provenanceEntries = provenanceRows.SelectMany(x => x.Content.Split(';')).ToList();
        Assert.All(provenanceEntries, entry => Assert.StartsWith("case:", entry));
        Assert.Equal(provenanceRows.Sum(x => x.EntryCount), provenanceEntries.Count);
        Assert.Equal(provenanceEntries.Count, provenanceEntries.Distinct().Count());
        // Every provenance entry must resolve to a typed migration-source record: exact Case revision → the
        // generated Procedure artifact/revision it names.
        foreach (var entry in provenanceEntries)
        {
            var caseId = Guid.Parse(entry.Split("->procedure:")[0]["case:".Length..]);
            var generated = entry.Split("->procedure:")[1].Split(':');
            Assert.True(await db.TestProcedureMigrationSources.AsNoTracking().AnyAsync(x =>
                x.SourceCaseRevisionId == caseId
                && x.GeneratedProcedureArtifactId == Guid.Parse(generated[0])
                && x.GeneratedProcedureRevisionId == Guid.Parse(generated[1])));
        }
        // The cutover event must explicitly disclaim the #722 identity-only claim: bodies were generated
        // and Case executable membership was replaced, so the detail says so instead of asserting it.
        Assert.Contains("not an identity-only migration", manifestEvent.Detail, StringComparison.Ordinal);
        Assert.False(await db.BaselineEvents.AsNoTracking()
            .AnyAsync(x => x.BaselineId == baseline.Id
                && x.EventType == "VerificationIdentityManifestMigrated"));

        var updatedDocument = await db.ControlledDocuments.AsNoTracking()
            .SingleAsync(x => x.Id == document.Id);
        Assert.NotEqual(document.ContentHash, updatedDocument.ContentHash);
        Assert.NotEqual(new string('c', 64), updatedDocument.ContentHash);
        var updatedArtifact = await db.ControlledDocumentArtifacts.AsNoTracking()
            .SingleAsync(x => x.Id == artifact.Id);
        Assert.NotEqual(artifact.Sha256, updatedArtifact.Sha256);
        // Prior rendition bytes are preserved, never deleted: the old storage key still resolves.
        Assert.True(files.Exists(stored.StorageKey));
        await using (var oldStream = files.OpenRead(stored.StorageKey))
        using (var oldBuffer = new MemoryStream())
        {
            await oldStream.CopyToAsync(oldBuffer);
            Assert.Equal(oldBytes, oldBuffer.ToArray());
        }
        Assert.True(files.Exists(updatedArtifact.StorageKey));

        Assert.True(await db.SecurityAuditEvents.AsNoTracking()
            .AnyAsync(x => x.EventType == "VerificationExecutionCutover.SoftwareProcedures.v1.DocumentContentBasisRewritten"
                && x.Target == $"ControlledDocument:{document.Id}"));
        Assert.True(await db.SecurityAuditEvents.AsNoTracking()
            .AnyAsync(x => x.EventType == "VerificationExecutionCutover.SoftwareProcedures.v1.DocumentRenditionRewritten"
                && x.Target == $"ControlledDocumentArtifact:{artifact.Id}"));
        foreach (var signature in new[] { artifactSignature, documentSignature })
        {
            Assert.True(await db.SecurityAuditEvents.AsNoTracking()
                .AnyAsync(x => x.EventType == "VerificationExecutionCutover.SoftwareProcedures.v1.SignatureSuperseded"
                    && x.Target == $"ElectronicSignature:{signature.Id}"));
            Assert.True(await db.SecurityAuditEvents.AsNoTracking()
                .AnyAsync(x => x.EventType == "VerificationExecutionCutover.SoftwareProcedures.v1.SignatureSupersessionCompleted"
                    && x.Target == $"ElectronicSignature:{signature.Id}"));
            // The original human signature row is never rewritten.
            var persisted = await db.ElectronicSignatures.AsNoTracking().SingleAsync(x => x.Id == signature.Id);
            Assert.Equal(signature.ContentHash, persisted.ContentHash);
        }

        var rerun = await authority.EnsureCompletedAsync();
        Assert.Equal(result, rerun);
        Assert.Equal(2, await db.SecurityAuditEvents.AsNoTracking()
            .CountAsync(x => x.EventType == "VerificationExecutionCutover.SoftwareProcedures.v1.SignatureSuperseded"));
        try { Directory.Delete(evidenceRoot, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public async Task Release_review_package_serializes_rebound_procedure_executions_evidence_and_test_set()
    {
        var options = new DbContextOptionsBuilder<AeroLinkDbContext>().UseSqlite("Data Source=:memory:").Options;
        var db = new AeroLinkDbContext(options);
        await db.Database.OpenConnectionAsync();
        await db.Database.EnsureCreatedAsync();
        var now = DateTimeOffset.UtcNow;
        var program = new ProgramRecord("Review Package Program", "PKG");
        var project = new ProjectRecord(program.Id, "Review Package Software", "Review Package Product");
        var release = new SoftwareRelease(project.Id, "1.0", false);
        var baseline = new CandidateBaseline("SW-01.00", 0, project.Id, release.Id, null,
            "Candidate", "cm.test", now);
        var build = new SoftwareBuild(project.Id, release.Id, baseline.Id, "B-100",
            "Review build", "cm.test", now);
        var campaign = new ReleaseCampaign(project.Id, release.Id, baseline.Id, "Review Campaign",
            "program.manager", now);
        campaign.SelectVerificationBuild(build.Id, "program.manager", now);
        db.AddRange(program, project, release, baseline, build, campaign);
        var configuration = LegacyDefaultProjectLadderFactory.Create(project.Id, now);
        db.ProjectLadderConfigurations.Add(configuration);
        await db.SaveChangesAsync();
        var seal = await new ProjectLadderSealAuthority(db).SealAsync(project.Id,
            LadderBoundContentCatalog.Current.First().Id, "review-content", "test.sealer", now);
        Assert.Equal(ProjectLadderSealResultKind.Sealed, seal.Kind);

        var scr = ApprovedBaselineScr(project.Id, release.Id, "SRCR-00728", now);
        var requirement = new RequirementArtifact(project.Id, "HLR-00000728",
            RequirementLevel.HighLevel, now);
        var requirementRevision = new RequirementRevision(requirement.Id, 0,
            "The software shall sequence.", "Rationale", "Test", RequirementRevisionState.Active,
            scr.Id, baseline.Id, now, parentKind: RequirementParentKind.Derived,
            derivedRationale: "Derived for the review package fixture.");
        var caseArtifact = new TestProcedure(project.Id, "HLRTC-000728", "Review sequencing case",
            "test.engineer", now, TestProcedureLevel.HighLevel);
        var caseRevision = new TestProcedureRevision(caseArtifact.Id, 0,
            "Verify sequencing", "Logical preconditions", "Scenario steps", "Pass criteria",
            TestProcedureState.Approved, "test.engineer", now, effectiveBaselineId: baseline.Id);
        var coverage = new TestRequirementCoverage(caseRevision.Id, requirementRevision.Id);
        var selection = new BaselineTestProcedureSelection(baseline.Id, caseArtifact.Id, caseRevision.Id);
        var requirementSelection = new BaselineRequirementSelection(baseline.Id,
            requirement.Id, requirementRevision.Id);
        var set = new BuildTestSet(project.Id, release.Id, TestChangeReviewDiscipline.HighLevelSoftware, now);
        set.Include("test.lead", caseRevision.Id, TestSelectionReason.Chosen, "", now);
        var execution = new TestExecution(project.Id, caseRevision.Id, build.Id, null, TestOutcome.Pass,
            "test.engineer", "Rig A", "Human determination", "evidence/review.json",
            now, now, release.Id);
        var evidenceRecord = new EvidenceRecord(project.Id, "review.json", "application/json", 32,
            new string('f', 64), "storage/review.json", "test.engineer", now);
        var evidence = new TestExecutionEvidence(execution.Id, evidenceRecord.Id);
        db.AddRange(scr, requirement, requirementRevision, caseArtifact, caseRevision, coverage,
            selection, requirementSelection, set, execution, evidenceRecord, evidence);
        baseline.Select(scr, "cm.test", now);
        baseline.Freeze("cm.test", now);
        baseline.MarkRequirementsMaterialized("cm.test", new string('a', 64), 1, now);
        baseline.MarkTestProceduresMaterialized("cm.test", new string('b', 64), 1, now);
        using (db.UseLegacyHistoricalSeed()) await db.SaveChangesAsync();

        var (legacy, typed) = FullRegistrations();
        var authority = new SoftwareProcedureExecutionCutoverAuthority(db, legacy, typed,
            allowSqliteExecution: true);
        var result = await authority.EnsureCompletedAsync();
        Assert.Equal(1, result.ProjectsUpgraded);
        var procedureRevision = await db.TestProcedureRevisions.AsNoTracking()
            .SingleAsync(x => x.ProcedureId != caseArtifact.Id
                && x.AuthorId == "aerolink-migration");
        var executionService = new ReleaseExecutionService(db,
            new EvidenceFileStore(Path.Combine(Path.GetTempPath(), $"aerolink-review-{Guid.NewGuid():N}")),
            new EffectiveProjectLadderPolicyResolver(db));
        var withExecution = await executionService.ComputeReviewManifestHashAsync(campaign.Id, default);

        // Finding 6: stale coverage pointing at a Case revision outside the effective coverage population
        // must NOT alter the review-manifest hash; effective selected Case coverage MUST.
        var otherBaseline = new CandidateBaseline("SW-01.01", 0, project.Id, release.Id, null,
            "Other candidate", "cm.test", now);
        var staleCase = new TestProcedure(project.Id, "HLRTC-000729", "Stale predecessor case",
            "test.engineer", now, TestProcedureLevel.HighLevel);
        var staleRevision = new TestProcedureRevision(staleCase.Id, 0,
            "Stale coverage", "Preconditions", "Steps", "Expected",
            TestProcedureState.Approved, "test.engineer", now, effectiveBaselineId: otherBaseline.Id);
        db.AddRange(otherBaseline, staleCase, staleRevision,
            new TestRequirementCoverage(staleRevision.Id, requirementRevision.Id));
        await db.SaveChangesAsync();
        var withStaleCoverage = await executionService.ComputeReviewManifestHashAsync(campaign.Id, default);
        Assert.Equal(withExecution, withStaleCoverage);

        var effectiveCoverage = await db.TestCoverage.SingleAsync(x =>
            x.ProcedureRevisionId == caseRevision.Id
            && x.RequirementRevisionId == requirementRevision.Id);
        // The save-boundary guard intentionally forbids removing an exact parent from an existing approved
        // Case revision outside a controlled successor, so this test mutates the storage layer directly to
        // prove the review-manifest filter itself fails closed on an unselected/stale coverage row. The
        // guard is separately exercised by the exact-parent validation tests.
        await db.Database.ExecuteSqlRawAsync(
            "DELETE FROM test_requirement_coverage WHERE Id = {0}", effectiveCoverage.Id);
        var withoutEffectiveCoverage = await executionService.ComputeReviewManifestHashAsync(
            campaign.Id, default);
        Assert.NotEqual(withExecution, withoutEffectiveCoverage);
        await db.Database.ExecuteSqlRawAsync(
            "INSERT INTO test_requirement_coverage (Id, ProcedureRevisionId, RequirementRevisionId, " +
            "IsSuspect, SuspectReason, SuspectSince, ConfirmedBy, ConfirmedAt) VALUES ({0}, {1}, {2}, 0, '', NULL, NULL, NULL)",
            Guid.NewGuid(), caseRevision.Id, requirementRevision.Id);
        var restoredCoverage = await executionService.ComputeReviewManifestHashAsync(campaign.Id, default);
        Assert.Equal(withExecution, restoredCoverage);

        // Remove the rebound Procedure execution: the release-review hash must change, proving the package
        // serializes the Procedure execution (and would fail if it silently disappeared).
        var reboundExecution = await db.TestExecutions.AsNoTracking()
            .SingleAsync(x => x.ProcedureRevisionId == procedureRevision.Id);
        var reboundEvidenceLinks = await db.TestExecutionEvidence
            .Where(x => x.TestExecutionId == reboundExecution.Id).ToListAsync();
        db.TestExecutionEvidence.RemoveRange(reboundEvidenceLinks);
        db.TestExecutions.Remove(await db.TestExecutions.SingleAsync(x => x.Id == reboundExecution.Id));
        await db.SaveChangesAsync();
        var withoutExecution = await executionService.ComputeReviewManifestHashAsync(campaign.Id, default);
        Assert.NotEqual(withExecution, withoutExecution);

        // Remove the Procedure from the test set: the hash must change, proving test-set membership is part
        // of the package.
        var entry = await db.BuildTestSetEntries.AsNoTracking()
            .SingleAsync(x => x.ProcedureRevisionId == procedureRevision.Id);
        db.BuildTestSetEntries.Remove(await db.BuildTestSetEntries.SingleAsync(x => x.Id == entry.Id));
        await db.SaveChangesAsync();
        var withoutTestSet = await executionService.ComputeReviewManifestHashAsync(campaign.Id, default);
        Assert.NotEqual(withExecution, withoutTestSet);
        Assert.NotEqual(withoutExecution, withoutTestSet);

        // Evidence disappears with the execution in this fixture; separately prove evidence participates by
        // restoring an execution without evidence and comparing against a fully-evidenced package.
        var restored = new TestExecution(project.Id, procedureRevision.Id, build.Id, null, TestOutcome.Pass,
            "test.engineer", "Rig A", "Human determination", "evidence/review.json",
            now, now, release.Id);
        db.TestExecutions.Add(restored);
        await db.SaveChangesAsync();
        var withoutEvidence = await executionService.ComputeReviewManifestHashAsync(campaign.Id, default);
        Assert.NotEqual(withExecution, withoutEvidence);
    }

    [Fact]
    public async Task Controlled_procedure_document_snapshot_recovers_exact_case_parents_after_cutover()
    {
        var options = new DbContextOptionsBuilder<AeroLinkDbContext>().UseSqlite("Data Source=:memory:").Options;
        var db = new AeroLinkDbContext(options);
        await db.Database.OpenConnectionAsync();
        await db.Database.EnsureCreatedAsync();
        var now = DateTimeOffset.UtcNow;
        var program = new ProgramRecord("Document Parent Program", "DPP");
        var project = new ProjectRecord(program.Id, "Document Parent Software", "Document Parent Product");
        var release = new SoftwareRelease(project.Id, "1.0", false);
        var baseline = new CandidateBaseline("SW-01.00", 0, project.Id, release.Id, null,
            "Candidate", "cm.test", now);
        db.AddRange(program, project, release, baseline);
        var configuration = LegacyDefaultProjectLadderFactory.Create(project.Id, now);
        db.ProjectLadderConfigurations.Add(configuration);
        await db.SaveChangesAsync();
        var seal = await new ProjectLadderSealAuthority(db).SealAsync(project.Id,
            LadderBoundContentCatalog.Current.First().Id, "parent-content", "test.sealer", now);
        Assert.Equal(ProjectLadderSealResultKind.Sealed, seal.Kind);

        var caseA = new TestProcedure(project.Id, "HLRTC-000801", "First parent case",
            "test.engineer", now, TestProcedureLevel.HighLevel);
        var caseARevision = new TestProcedureRevision(caseA.Id, 0,
            "Verify first parent case", "Preconditions", "Steps", "Expected",
            TestProcedureState.Approved, "test.engineer", now, effectiveBaselineId: baseline.Id);
        var caseB = new TestProcedure(project.Id, "HLRTC-000802", "Second parent case",
            "test.engineer", now, TestProcedureLevel.HighLevel);
        var caseBRevision = new TestProcedureRevision(caseB.Id, 0,
            "Verify second parent case", "Preconditions", "Steps", "Expected",
            TestProcedureState.Approved, "test.engineer", now, effectiveBaselineId: baseline.Id);
        var selection = new BaselineTestProcedureSelection(baseline.Id, caseA.Id, caseARevision.Id);
        var selectionB = new BaselineTestProcedureSelection(baseline.Id, caseB.Id, caseBRevision.Id);
        db.AddRange(caseA, caseARevision, caseB, caseBRevision, selection, selectionB);
        var scr = ApprovedBaselineScr(project.Id, release.Id, "SRCR-00730", now);
        baseline.Select(scr, "cm.test", now);
        baseline.Freeze("cm.test", now);
        baseline.MarkRequirementsMaterialized("cm.test", new string('a', 64), 0, now);
        baseline.MarkTestProceduresMaterialized("cm.test", new string('b', 64), 1, now);
        await db.SaveChangesAsync();

        var (legacy, typed) = FullRegistrations();
        var authority = new SoftwareProcedureExecutionCutoverAuthority(db, legacy, typed,
            allowSqliteExecution: true);
        await authority.EnsureCompletedAsync();
        Assert.Equal(2, await db.TestProcedures.AsNoTracking().CountAsync(
            x => x.ProjectId == project.Id
                && x.ArtifactKind == VerificationArtifactKind.Procedure
                && x.BaseNumber.StartsWith("HLRTP-")));
        var generatedRevision = await (from link in db.TestCaseProcedureLinks.AsNoTracking()
                                       where link.CaseRevisionId == caseARevision.Id
                                       join revision in db.TestProcedureRevisions.AsNoTracking()
                                           on link.ProcedureRevisionId equals revision.Id
                                       select revision).SingleAsync();

        // An authored software Procedure with TWO exact Case parents must keep both in the controlled
        // document snapshot — never collapse to First().
        var policy = new EffectiveProjectLadderPolicyResolver(db);
        var authored = new TestProcedure(project.Id, "HLRTP-000803", "Authored two-parent procedure",
            "test.engineer", now, TestProcedureLevel.HighLevel, await policy.ResolveAsync(project.Id),
            VerificationArtifactKind.Procedure, VerificationProcedureParentKind.Allocated);
        var authoredRevision = new TestProcedureRevision(authored.Id, 0,
            "Execute both parent cases", "Procedure setup", "Procedure steps", "Expected observation",
            TestProcedureState.Draft, "test.engineer", now,
            environmentSetup: "Procedure setup", testData: "Controlled data",
            orderedSteps: "Procedure steps", expectedObservations: "Expected observation",
            cleanup: "Restore fixture", toolingAutomation: "Qualified runner",
            parentKind: VerificationProcedureParentKind.Allocated);
        db.AddRange(authored, authoredRevision,
            new TestCaseProcedureLink(caseARevision.Id, authoredRevision.Id),
            new TestCaseProcedureLink(caseBRevision.Id, authoredRevision.Id),
            new BaselineTestProcedureSelection(baseline.Id, authored.Id, authoredRevision.Id));
        using (db.UseSaveBoundaryPolicy(await policy.ResolveAsync(project.Id)))
            await db.SaveChangesAsync();
        db.Entry(authoredRevision).Property(x => x.State).CurrentValue = TestProcedureState.Approved;
        using (db.UseSaveBoundaryPolicy(await policy.ResolveAsync(project.Id)))
            await db.SaveChangesAsync();

        var snapshot = await ControlledProcedureDocumentSnapshotProjection.ForDocumentAsync(db, baseline.Id,
            new VerificationArtifactKey(VerificationDiscipline.HighLevelSoftware,
                VerificationArtifactKind.Procedure), now.AddMinutes(2), default);
        Assert.True(snapshot.IsExactManifest);
        var generatedRow = snapshot.Rows.Single(x => x.RevisionId == generatedRevision.Id);
        Assert.Equal([caseARevision.Id], generatedRow.ParentRevisionIds);
        var authoredRow = snapshot.Rows.Single(x => x.RevisionId == authoredRevision.Id);
        Assert.Equal(2, authoredRow.ParentRevisionIds!.Count);
        Assert.Contains(caseARevision.Id, authoredRow.ParentRevisionIds);
        Assert.Contains(caseBRevision.Id, authoredRow.ParentRevisionIds);
    }

    [Fact]
    public async Task Requirements_coverage_side_uses_effective_case_revisions_after_cutover()
    {
        var options = new DbContextOptionsBuilder<AeroLinkDbContext>().UseSqlite("Data Source=:memory:").Options;
        var db = new AeroLinkDbContext(options);
        await db.Database.OpenConnectionAsync();
        await db.Database.EnsureCreatedAsync();
        var now = DateTimeOffset.UtcNow;
        var program = new ProgramRecord("Coverage Side Program", "CSD");
        var project = new ProjectRecord(program.Id, "Coverage Side Software", "Coverage Side Product");
        var release = new SoftwareRelease(project.Id, "1.0", false);
        var baseline = new CandidateBaseline("SW-01.00", 0, project.Id, release.Id, null,
            "Candidate", "cm.test", now);
        db.AddRange(program, project, release, baseline);
        var configuration = LegacyDefaultProjectLadderFactory.Create(project.Id, now);
        db.ProjectLadderConfigurations.Add(configuration);
        await db.SaveChangesAsync();
        var seal = await new ProjectLadderSealAuthority(db).SealAsync(project.Id,
            LadderBoundContentCatalog.Current.First().Id, "coverage-content", "test.sealer", now);
        Assert.Equal(ProjectLadderSealResultKind.Sealed, seal.Kind);

        var scr = ApprovedBaselineScr(project.Id, release.Id, "SRCR-00731", now);
        var requirement = new RequirementArtifact(project.Id, "HLR-00000731",
            RequirementLevel.HighLevel, now);
        var requirementRevision = new RequirementRevision(requirement.Id, 0,
            "The software shall sequence.", "Rationale", "Test", RequirementRevisionState.Active,
            scr.Id, baseline.Id, now, parentKind: RequirementParentKind.Derived,
            derivedRationale: "Derived for the coverage-side fixture.");
        var caseArtifact = new TestProcedure(project.Id, "HLRTC-000731", "Coverage sequencing case",
            "test.engineer", now, TestProcedureLevel.HighLevel);
        var caseRevision = new TestProcedureRevision(caseArtifact.Id, 0,
            "Verify sequencing", "Logical preconditions", "Scenario steps", "Pass criteria",
            TestProcedureState.Approved, "test.engineer", now, effectiveBaselineId: baseline.Id);
        db.AddRange(scr, requirement, requirementRevision, caseArtifact, caseRevision,
            new TestRequirementCoverage(caseRevision.Id, requirementRevision.Id),
            new BaselineRequirementSelection(baseline.Id, requirement.Id, requirementRevision.Id),
            new BaselineTestProcedureSelection(baseline.Id, caseArtifact.Id, caseRevision.Id));
        baseline.Select(scr, "cm.test", now);
        baseline.Freeze("cm.test", now);
        baseline.MarkRequirementsMaterialized("cm.test", new string('a', 64), 1, now);
        baseline.MarkTestProceduresMaterialized("cm.test", new string('b', 64), 1, now);
        using (db.UseLegacyHistoricalSeed()) await db.SaveChangesAsync();

        var (legacy, typed) = FullRegistrations();
        await new SoftwareProcedureExecutionCutoverAuthority(db, legacy, typed,
            allowSqliteExecution: true).EnsureCompletedAsync();
        var population = await BaselineExecutableMembership.ForPopulationAsync(db, baseline.Id,
            new HashSet<TestProcedureLevel> { TestProcedureLevel.HighLevel }, default);
        // The executable side is the Procedure; the coverage side is the exact Case revision.
        Assert.DoesNotContain(caseRevision.Id, population.ExecutableRevisionIds);
        var coverageStates = await VerificationCoverageProjection.StatesAsync(db,
            [requirementRevision.Id], default, population.CoverageRevisionIds, buildScoped: false);
        Assert.Equal(RequirementCoverageState.Covered, coverageStates[requirementRevision.Id]);
        var wrongStates = await VerificationCoverageProjection.StatesAsync(db,
            [requirementRevision.Id], default, population.ExecutableRevisionIds, buildScoped: false);
        Assert.Equal(RequirementCoverageState.Uncovered, wrongStates[requirementRevision.Id]);
    }

    [Fact]
    public async Task Verification_impact_coverage_work_resolves_the_source_case_after_cutover()
    {
        var options = new DbContextOptionsBuilder<AeroLinkDbContext>().UseSqlite("Data Source=:memory:").Options;
        var db = new AeroLinkDbContext(options);
        await db.Database.OpenConnectionAsync();
        await db.Database.EnsureCreatedAsync();
        var now = DateTimeOffset.UtcNow;
        var program = new ProgramRecord("Impact Semantics Program", "ISP");
        var project = new ProjectRecord(program.Id, "Impact Semantics Software", "Impact Semantics Product");
        var release = new SoftwareRelease(project.Id, "1.0", false);
        var baseline = new CandidateBaseline("SW-01.00", 0, project.Id, release.Id, null,
            "Candidate", "cm.test", now);
        db.AddRange(program, project, release, baseline);
        var configuration = LegacyDefaultProjectLadderFactory.Create(project.Id, now);
        db.ProjectLadderConfigurations.Add(configuration);
        await db.SaveChangesAsync();
        var seal = await new ProjectLadderSealAuthority(db).SealAsync(project.Id,
            LadderBoundContentCatalog.Current.First().Id, "impact-content", "test.sealer", now);
        Assert.Equal(ProjectLadderSealResultKind.Sealed, seal.Kind);

        var scr = ApprovedBaselineScr(project.Id, release.Id, "SRCR-00732", now);
        var requirement = new RequirementArtifact(project.Id, "HLR-00000732",
            RequirementLevel.HighLevel, now);
        var requirementRevision = new RequirementRevision(requirement.Id, 0,
            "The software shall sequence.", "Rationale", "Test", RequirementRevisionState.Active,
            scr.Id, baseline.Id, now, parentKind: RequirementParentKind.Derived,
            derivedRationale: "Derived for the impact fixture.");
        var caseArtifact = new TestProcedure(project.Id, "HLRTC-000732", "Impact sequencing case",
            "test.engineer", now, TestProcedureLevel.HighLevel);
        var caseRevision = new TestProcedureRevision(caseArtifact.Id, 0,
            "Verify sequencing", "Logical preconditions", "Scenario steps", "Pass criteria",
            TestProcedureState.Approved, "test.engineer", now, effectiveBaselineId: baseline.Id);
        db.AddRange(scr, requirement, requirementRevision, caseArtifact, caseRevision,
            new TestRequirementCoverage(caseRevision.Id, requirementRevision.Id),
            new BaselineRequirementSelection(baseline.Id, requirement.Id, requirementRevision.Id),
            new BaselineTestProcedureSelection(baseline.Id, caseArtifact.Id, caseRevision.Id));
        baseline.Select(scr, "cm.test", now);
        baseline.Freeze("cm.test", now);
        baseline.MarkRequirementsMaterialized("cm.test", new string('a', 64), 1, now);
        baseline.MarkTestProceduresMaterialized("cm.test", new string('b', 64), 1, now);
        using (db.UseLegacyHistoricalSeed()) await db.SaveChangesAsync();

        var (legacy, typed) = FullRegistrations();
        await new SoftwareProcedureExecutionCutoverAuthority(db, legacy, typed,
            allowSqliteExecution: true).EnsureCompletedAsync();
        var procedure = await db.TestProcedures.AsNoTracking()
            .SingleAsync(x => x.ProjectId == project.Id
                && x.ArtifactKind == VerificationArtifactKind.Procedure);
        var procedureRevision = await db.TestProcedureRevisions.AsNoTracking()
            .SingleAsync(x => x.ProcedureId == procedure.Id);

        var review = new TestChangeReview(project.Id, release.Id, scr.Id,
            TestChangeReviewDiscipline.HighLevelSoftware, "SRCR-00732.00", now);
        db.Add(review);
        var item = VerificationImpactItem.ForIntroducedRequirement(project.Id, release.Id,
            scr.Id, review.Id, Guid.NewGuid(), "HLR-00000732", "Test", now);
        SetPrivate(item, nameof(VerificationImpactItem.RequirementRevisionId), requirementRevision.Id);
        item.Resolve("test.engineer", VerificationImpactOutcome.ProcedureCoverageConfirmed,
            "The Procedure covers the sequencing requirement through its exact source Case.", now,
            procedureId: procedure.Id, procedureRevisionId: procedureRevision.Id);
        db.Add(item);
        await db.SaveChangesAsync();

        var applied = await new VerificationImpactService(db).ApplyResolvedCoverageAsync(item, now, default);
        Assert.True(applied);
        // Coverage work stays on the exact source Case revision — never a TestCoverage row against a
        // software Procedure revision.
        Assert.True(await db.TestCoverage.AsNoTracking().AnyAsync(x =>
            x.ProcedureRevisionId == caseRevision.Id
            && x.RequirementRevisionId == requirementRevision.Id));
        Assert.False(await db.TestCoverage.AsNoTracking().AnyAsync(x =>
            x.ProcedureRevisionId == procedureRevision.Id));
    }

    private static void SetPrivate(object target, string propertyName, object? value) =>
        target.GetType().GetProperty(propertyName)!
            .GetSetMethod(nonPublic: true)!
            .Invoke(target, [value]);

    private sealed record SignedDocumentSeed(AeroLinkDbContext Db, Guid ProjectId, Guid ReleaseId,
        Guid BaselineId, Guid DocumentId, Guid ArtifactId, Guid ArtifactSignatureId,
        Guid DocumentSignatureId, DateTimeOffset GeneratedAt);

    private static async Task<SignedDocumentSeed> SeedSignedDocumentProjectAsync(
        AeroLinkDbContext db, EvidenceFileStore files, string tag, bool materialized = true)
    {
        var now = DateTimeOffset.UtcNow;
        var program = new ProgramRecord($"{tag} Program", $"SDP{tag[^3..]}");
        var project = new ProjectRecord(program.Id, $"{tag} Software", $"{tag} Product");
        var release = new SoftwareRelease(project.Id, "1.0", false);
        var baseline = new CandidateBaseline($"SW-{tag[^2..]}.00", 0, project.Id, release.Id, null,
            "Candidate", "cm.test", now);
        db.AddRange(program, project, release, baseline);
        var configuration = LegacyDefaultProjectLadderFactory.Create(project.Id, now);
        db.ProjectLadderConfigurations.Add(configuration);
        await db.SaveChangesAsync();
        var seal = await new ProjectLadderSealAuthority(db).SealAsync(project.Id,
            LadderBoundContentCatalog.Current.First().Id, $"{tag}-content", "test.sealer", now);
        Assert.Equal(ProjectLadderSealResultKind.Sealed, seal.Kind);
        var caseArtifact = new TestProcedure(project.Id, $"HLRTC-{tag[^6..]}", $"{tag} case",
            "test.engineer", now, TestProcedureLevel.HighLevel);
        var caseRevision = new TestProcedureRevision(caseArtifact.Id, 0,
            "Verify", "Preconditions", "Steps", "Expected",
            TestProcedureState.Approved, "test.engineer", now, effectiveBaselineId: baseline.Id);
        var scr = ApprovedBaselineScr(project.Id, release.Id, $"SRCR-{tag[^5..]}", now);
        db.AddRange(scr, caseArtifact, caseRevision,
            new BaselineTestProcedureSelection(baseline.Id, caseArtifact.Id, caseRevision.Id));
        baseline.Select(scr, "cm.test", now);
        baseline.Freeze("cm.test", now);
        baseline.MarkRequirementsMaterialized("cm.test", new string('a', 64), 0, now);
        if (materialized) baseline.MarkTestProceduresMaterialized("cm.test", new string('b', 64), 1, now);
        await db.SaveChangesAsync();
        var oldBytes = Encoding.UTF8.GetBytes($"pre-cutover {tag} controlled bytes");
        var stored = await files.StoreAsync(new MemoryStream(oldBytes), $"{tag}.docx",
            "application/vnd.openxmlformats-officedocument.wordprocessingml.document", default);
        var document = new ControlledDocument(project.Id, release.Id, baseline.Id,
            ControlledDocumentType.HighLevelTestCases, $"HLRTD-{tag[^6..]}", $"{tag} cases", 0,
            new string('c', 64), 1, now);
        var artifact = new ControlledDocumentArtifact(document.Id, "docx", stored.StorageKey,
            stored.OriginalFileName, stored.ContentType, stored.Size, stored.Sha256, now);
        var artifactSignature = new ElectronicSignature(Guid.NewGuid(), "reviewer", "Reviewer", program.Id,
            "ControlledDocumentArtifact", artifact.Id, $"{document.DocumentNumber}.00/docx", "Approve",
            "old output", artifact.Sha256, "127.0.0.1", now);
        var documentSignature = new ElectronicSignature(Guid.NewGuid(), "reviewer", "Reviewer", program.Id,
            "ControlledDocument", document.Id, $"{document.DocumentNumber}.00", "Approve",
            "old document", document.ContentHash, "127.0.0.1", now);
        db.AddRange(document, artifact, artifactSignature, documentSignature);
        await db.SaveChangesAsync();
        return new SignedDocumentSeed(db, project.Id, release.Id, baseline.Id, document.Id, artifact.Id,
            artifactSignature.Id, documentSignature.Id, now);
    }

    [Fact]
    public async Task Two_projects_complete_with_scoped_signature_supersession()
    {
        var options = new DbContextOptionsBuilder<AeroLinkDbContext>().UseSqlite("Data Source=:memory:").Options;
        var db = new AeroLinkDbContext(options);
        await db.Database.OpenConnectionAsync();
        await db.Database.EnsureCreatedAsync();
        var evidenceRoot = Path.Combine(Path.GetTempPath(), $"aerolink-726-two-{Guid.NewGuid():N}");
        var files = new EvidenceFileStore(evidenceRoot);
        var first = await SeedSignedDocumentProjectAsync(db, files, "200001");
        var second = await SeedSignedDocumentProjectAsync(db, files, "200002");
        var (legacy, typed) = FullRegistrations();
        var generator = new ControlledOutputGenerator(db, new RichContentPublisher(db, files),
            policyResolver: new EffectiveProjectLadderPolicyResolver(db));
        var authority = new SoftwareProcedureExecutionCutoverAuthority(db, legacy, typed,
            allowSqliteExecution: true, generator: generator, files: files);
        var result = await authority.EnsureCompletedAsync();
        Assert.Equal(2, result.ProjectsUpgraded);
        foreach (var seed in new[] { first, second })
        {
            foreach (var signatureId in new[] { seed.ArtifactSignatureId, seed.DocumentSignatureId })
            {
                var target = $"ElectronicSignature:{signatureId}";
                Assert.Equal(1, await db.SecurityAuditEvents.AsNoTracking().CountAsync(
                    x => x.EventType == "VerificationExecutionCutover.SoftwareProcedures.v1.SignatureSuperseded"
                        && x.Target == target));
                Assert.Equal(1, await db.SecurityAuditEvents.AsNoTracking().CountAsync(
                    x => x.EventType == "VerificationExecutionCutover.SoftwareProcedures.v1.SignatureSupersessionCompleted"
                        && x.Target == target));
            }
        }
        try { Directory.Delete(evidenceRoot, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public async Task Legacy_unmaterialized_document_cutover_preserves_historical_snapshot()
    {
        var options = new DbContextOptionsBuilder<AeroLinkDbContext>().UseSqlite("Data Source=:memory:").Options;
        var db = new AeroLinkDbContext(options);
        await db.Database.OpenConnectionAsync();
        await db.Database.EnsureCreatedAsync();
        var evidenceRoot = Path.Combine(Path.GetTempPath(), $"aerolink-726-legacy-{Guid.NewGuid():N}");
        var files = new EvidenceFileStore(evidenceRoot);
        var seed = await SeedSignedDocumentProjectAsync(db, files, "300003", materialized: false);
        var now = DateTimeOffset.UtcNow;
        var caseArtifact = await db.TestProcedures.AsNoTracking()
            .SingleAsync(x => x.ProjectId == seed.ProjectId
                && x.ArtifactKind == VerificationArtifactKind.Case);
        // A revision approved AFTER the document's GeneratedAt must never enter the regenerated historical
        // compatibility output.
        db.Add(new TestProcedureRevision(caseArtifact.Id, 1, "Later revision", "P", "S", "E",
            TestProcedureState.Approved, "test.engineer", now.AddDays(10)));
        await db.SaveChangesAsync();

        var (legacy, typed) = FullRegistrations();
        var generator = new ControlledOutputGenerator(db, new RichContentPublisher(db, files),
            policyResolver: new EffectiveProjectLadderPolicyResolver(db));
        var authority = new SoftwareProcedureExecutionCutoverAuthority(db, legacy, typed,
            allowSqliteExecution: true, generator: generator, files: files);
        var result = await authority.EnsureCompletedAsync();
        Assert.Equal(1, result.ProjectsUpgraded);
        var baseline = await db.CandidateBaselines.AsNoTracking().SingleAsync(x => x.Id == seed.BaselineId);
        Assert.Null(baseline.TestProceduresMaterializedAt);
        Assert.Null(baseline.TestProceduresHash);
        Assert.False(await db.BaselineEvents.AsNoTracking().AnyAsync(x =>
            x.BaselineId == seed.BaselineId
            && (x.EventType == "ExecutionCutoverManifestMigrated"
                || x.EventType == "VerificationIdentityManifestMigrated")));
        var document = await db.ControlledDocuments.AsNoTracking().SingleAsync(x => x.Id == seed.DocumentId);
        Assert.Equal(seed.GeneratedAt, document.GeneratedAt);
        Assert.NotEqual(new string('c', 64), document.ContentHash);
        var artifact = await db.ControlledDocumentArtifacts.AsNoTracking().SingleAsync(x => x.Id == seed.ArtifactId);
        Assert.True(files.Exists(artifact.StorageKey));
        var snapshot = await ControlledProcedureDocumentSnapshotProjection.ForDocumentAsync(db,
            seed.BaselineId, new VerificationArtifactKey(VerificationDiscipline.HighLevelSoftware,
                VerificationArtifactKind.Case), document.GeneratedAt, default);
        var row = Assert.Single(snapshot.Rows);
        Assert.Equal(0, row.Revision);
        Assert.True(await db.SecurityAuditEvents.AsNoTracking()
            .AnyAsync(x => x.EventType == "VerificationExecutionCutover.SoftwareProcedures.v1.SignatureSupersessionCompleted"));
        try { Directory.Delete(evidenceRoot, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public async Task Recovered_totals_are_project_scoped()
    {
        var options = new DbContextOptionsBuilder<AeroLinkDbContext>().UseSqlite("Data Source=:memory:").Options;
        var db = new AeroLinkDbContext(options);
        await db.Database.OpenConnectionAsync();
        await db.Database.EnsureCreatedAsync();
        await SeedIntoAsync(db, "000001");
        await SeedIntoAsync(db, "000002");
        var (legacy, typed) = FullRegistrations();
        await new SoftwareProcedureExecutionCutoverAuthority(db, legacy, typed,
            allowSqliteExecution: true).EnsureCompletedAsync();
        var completedAudits = await db.SecurityAuditEvents
            .Where(x => x.EventType == "VerificationExecutionCutover.SoftwareProcedures.v1.Completed")
            .ToListAsync();
        db.SecurityAuditEvents.RemoveRange(completedAudits);
        var completionClaims = await db.GovernedMigrationCompletions.ToListAsync();
        db.GovernedMigrationCompletions.RemoveRange(completionClaims);
        await db.SaveChangesAsync();

        // A still-pending project (sealed LegacyDefault Stored with software Cases) is added AFTER the
        // completed projects; it must be upgraded on rerun and excluded from the recovered totals.
        var pending = await SeedIntoAsync(db, "000003");
        // An unrelated NonDefault Active Case-only project with a fabricated migration-authored Procedure
        // revision must NEVER leak into the recovered totals.
        var now = DateTimeOffset.UtcNow;
        var unrelatedProgram = new ProgramRecord("Unrelated Program", "UNR");
        var unrelatedProject = new ProjectRecord(unrelatedProgram.Id, "Unrelated Software", "Unrelated Product");
        db.AddRange(unrelatedProgram, unrelatedProject);
        var unrelatedLadder = ProjectLadderConfiguration.CreateDraft(unrelatedProject.Id, now);
        var steps = new List<ProjectLadderStep>();
        foreach (var (level, position) in LegacyLadderPolicy.Instance.OrderedLevels.Select((x, i) => (x, i + 1)))
        {
            var kinds = level == RequirementLevel.System
                ? new[] { VerificationArtifactKind.Procedure }
                : new[] { VerificationArtifactKind.Case };
            var step = new ProjectLadderStep(unrelatedLadder.Id, unrelatedProject.Id, level, position,
                LegacyLadderPolicy.Instance.Definition(level).Capabilities, now, kinds);
            unrelatedLadder.Steps.Add(step);
            steps.Add(step);
        }
        unrelatedLadder.AllowedUpstream.Add(new ProjectLadderAllowedUpstream(unrelatedLadder.Id,
            unrelatedProject.Id, steps[0].Id, steps[1].Id, now));
        unrelatedLadder.AllowedUpstream.Add(new ProjectLadderAllowedUpstream(unrelatedLadder.Id,
            unrelatedProject.Id, steps[1].Id, steps[2].Id, now));
        unrelatedLadder.Activate("project.owner", now, LadderConsumerManifestCatalog.VersionV2,
            new string('0', 64));
        db.ProjectLadderConfigurations.Add(unrelatedLadder);
        var fabricatedCase = new TestProcedure(unrelatedProject.Id, "HLRTC-000999", "Fabricated case",
            "test.engineer", now, TestProcedureLevel.HighLevel);
        var fabricatedCaseRevision = new TestProcedureRevision(fabricatedCase.Id, 0,
            "Fabricated", "P", "S", "E", TestProcedureState.Approved, "test.engineer", now);
        var fabricatedProcedure = new TestProcedure(unrelatedProject.Id, "HLRTP-000999",
            "Fabricated procedure", "test.engineer", now, TestProcedureLevel.HighLevel,
            artifactKind: VerificationArtifactKind.Procedure,
            parentKind: VerificationProcedureParentKind.Allocated);
        var fabricatedRevision = new TestProcedureRevision(fabricatedProcedure.Id, 0,
            "Fabricated", "Procedure setup", "Procedure steps", "Expected",
            TestProcedureState.Draft, "aerolink-migration", now,
            environmentSetup: "Setup", testData: "Data", orderedSteps: "Steps",
            expectedObservations: "Expected", cleanup: "Cleanup", toolingAutomation: "Tooling",
            parentKind: VerificationProcedureParentKind.Allocated);
        db.AddRange(fabricatedCase, fabricatedCaseRevision, fabricatedProcedure, fabricatedRevision,
            new TestCaseProcedureLink(fabricatedCaseRevision.Id, fabricatedRevision.Id));
        await db.SaveChangesAsync();
        db.Entry(fabricatedRevision).Property(x => x.State).CurrentValue = TestProcedureState.Approved;
        await db.SaveChangesAsync();

        var recovery = await new SoftwareProcedureExecutionCutoverAuthority(db, legacy, typed,
            allowSqliteExecution: true).EnsureCompletedAsync();
        // 3 upgraded (two recovered + the still-pending one); the unrelated project's fabricated
        // migration-looking revision must not be counted.
        Assert.Equal(3, recovery.ProjectsUpgraded);
        Assert.Equal(3, recovery.ProceduresGenerated);
        Assert.True(await db.GovernedMigrationCompletions.AsNoTracking()
            .AnyAsync(x => x.Marker == "VerificationExecutionCutover.SoftwareProcedures.v1"));
    }

    [Fact]
    public async Task Impact_coverage_work_uses_all_parent_semantics()
    {
        var seed = await SeedAllParentFixtureAsync(reversedLinks: true);
        await ExerciseAllParentCycleAsync(seed);
    }

    [Fact]
    public async Task Impact_all_parent_semantics_are_insertion_order_independent()
    {
        // The selected Cases must never depend on provider or insertion order: run the full
        // apply/confirm/reopen/retarget cycle with links inserted A→B and B→A.
        foreach (var reversed in new[] { false, true })
        {
            var seed = await SeedAllParentFixtureAsync(reversedLinks: reversed);
            await ExerciseAllParentCycleAsync(seed);
            seed.Db.Dispose();
        }
    }

    [Fact]
    public async Task Open_suspect_case_procedure_link_is_not_an_effective_parent_for_impact_work()
    {
        var seed = await SeedAllParentFixtureAsync(reversedLinks: true, openSuspectLinkOnB: true);

        var service = new VerificationImpactService(seed.Db,
            policyResolver: new EffectiveProjectLadderPolicyResolver(seed.Db));
        var item = await seed.Db.VerificationImpactItems.SingleAsync(x => x.Id == seed.ItemId);
        // Reopen first so the confirmation step is observable: apply must confirm the effective parent and
        // leave the open-suspect-link parent untouched.
        Assert.True(await service.ReopenResolvedCoverageAsync(item, "Reopen effective parent.", seed.Now, default));
        await seed.Db.SaveChangesAsync();
        Assert.True(await service.ApplyResolvedCoverageAsync(item, seed.Now, default));
        await seed.Db.SaveChangesAsync();
        var aRow = await seed.Db.TestCoverage.AsNoTracking().SingleAsync(x =>
            x.ProcedureRevisionId == seed.CaseARevisionId
            && x.RequirementRevisionId == seed.RequirementRevisionId);
        var bRow = await seed.Db.TestCoverage.AsNoTracking().SingleAsync(x =>
            x.ProcedureRevisionId == seed.CaseBRevisionId
            && x.RequirementRevisionId == seed.RequirementRevisionId);
        Assert.Equal("test.engineer", aRow.ConfirmedBy);
        Assert.False(aRow.IsSuspect);
        Assert.False(bRow.IsSuspect);
        Assert.Null(bRow.ConfirmedBy);

        // Reopen also operates on the effective set only: A becomes suspect, B is untouched.
        Assert.True(await service.ReopenResolvedCoverageAsync(item, "Reopen effective parent.", seed.Now, default));
        await seed.Db.SaveChangesAsync();
        Assert.True((await seed.Db.TestCoverage.AsNoTracking().SingleAsync(x => x.Id == aRow.Id)).IsSuspect);
        Assert.False((await seed.Db.TestCoverage.AsNoTracking().SingleAsync(x => x.Id == bRow.Id)).IsSuspect);
        Assert.Null((await seed.Db.TestCoverage.AsNoTracking().SingleAsync(x => x.Id == bRow.Id)).ConfirmedBy);

        // Retarget follows the same effective all-parent set: only A is an obligation.
        var retargetItem = VerificationImpactItem.ForOrphanedProcedure(seed.ProjectId, seed.ReleaseId,
            seed.ScrId, seed.ReviewId, seed.ProcedureId, "HLRTP-000735", seed.Now);
        SetPrivate(retargetItem, nameof(VerificationImpactItem.RetargetedRequirementRevisionId),
            seed.RequirementRevisionId);
        retargetItem.Resolve("test.engineer", VerificationImpactOutcome.ProcedureRetargeted,
            "Retarget the effective parent.", seed.Now,
            retargetedRequirementRevisionId: seed.RequirementRevisionId);
        seed.Db.Add(retargetItem);
        await seed.Db.SaveChangesAsync();
        Assert.True(await service.HasEffectiveRetargetTargetAsync(seed.ProjectId, seed.ReleaseId,
            seed.ProcedureId, seed.RequirementRevisionId, default));
        Assert.True(await service.ApplyRetargetedCoverageAsync(retargetItem, seed.Now, default));
        await seed.Db.SaveChangesAsync();
        Assert.Equal("test.engineer", (await seed.Db.TestCoverage.AsNoTracking()
            .SingleAsync(x => x.Id == aRow.Id)).ConfirmedBy);
        Assert.Null((await seed.Db.TestCoverage.AsNoTracking()
            .SingleAsync(x => x.Id == bRow.Id)).ConfirmedBy);
    }

    [Fact]
    public async Task Missing_parent_coverage_defers_apply_and_retarget_refuses_without_partial_mutation()
    {
        var seed = await SeedAllParentFixtureAsync(reversedLinks: false, coverageForBoth: false);
        var service = new VerificationImpactService(seed.Db,
            policyResolver: new EffectiveProjectLadderPolicyResolver(seed.Db));
        var item = await seed.Db.VerificationImpactItems.SingleAsync(x => x.Id == seed.ItemId);

        // Reopen makes the existing row suspect, then apply confirms the parent that has a row and
        // deterministically defers the missing parent to a controlled successor — the outcome does not
        // depend on which parent is missing or insertion order.
        Assert.True(await service.ReopenResolvedCoverageAsync(item, "Reopen before apply.", seed.Now, default));
        await seed.Db.SaveChangesAsync();
        Assert.True(await service.ApplyResolvedCoverageAsync(item, seed.Now, default));
        await seed.Db.SaveChangesAsync();
        var aRow = await seed.Db.TestCoverage.AsNoTracking().SingleAsync(x =>
            x.ProcedureRevisionId == seed.CaseARevisionId
            && x.RequirementRevisionId == seed.RequirementRevisionId);
        Assert.Equal("test.engineer", aRow.ConfirmedBy);
        Assert.Equal(0, await seed.Db.TestCoverage.AsNoTracking().CountAsync(x =>
            x.ProcedureRevisionId == seed.CaseBRevisionId
            && x.RequirementRevisionId == seed.RequirementRevisionId));

        // Retarget's all-parent precondition fails (B has no row), so it must refuse with NO partial
        // mutation — A's confirmation evidence stays byte-for-byte unchanged.
        var confirmedAt = aRow.ConfirmedAt;
        var retargetItem = VerificationImpactItem.ForOrphanedProcedure(seed.ProjectId, seed.ReleaseId,
            seed.ScrId, seed.ReviewId, seed.ProcedureId, "HLRTP-000735", seed.Now);
        SetPrivate(retargetItem, nameof(VerificationImpactItem.RetargetedRequirementRevisionId),
            seed.RequirementRevisionId);
        retargetItem.Resolve("test.engineer", VerificationImpactOutcome.ProcedureRetargeted,
            "Retarget both source Cases.", seed.Now,
            retargetedRequirementRevisionId: seed.RequirementRevisionId);
        seed.Db.Add(retargetItem);
        await seed.Db.SaveChangesAsync();
        Assert.False(await service.HasEffectiveRetargetTargetAsync(seed.ProjectId, seed.ReleaseId,
            seed.ProcedureId, seed.RequirementRevisionId, default));
        Assert.False(await service.ApplyRetargetedCoverageAsync(retargetItem, seed.Now, default));
        await seed.Db.SaveChangesAsync();
        var after = await seed.Db.TestCoverage.AsNoTracking().SingleAsync(x => x.Id == aRow.Id);
        Assert.Equal(confirmedAt, after.ConfirmedAt);
        Assert.Equal("test.engineer", after.ConfirmedBy);
        Assert.Equal(0, await seed.Db.TestCoverage.AsNoTracking().CountAsync(x =>
            x.ProcedureRevisionId == seed.CaseBRevisionId
            && x.RequirementRevisionId == seed.RequirementRevisionId));
    }

    private static async Task ExerciseAllParentCycleAsync(AllParentSeed seed)
    {
        var service = new VerificationImpactService(seed.Db,
            policyResolver: new EffectiveProjectLadderPolicyResolver(seed.Db));
        var item = await seed.Db.VerificationImpactItems.SingleAsync(x => x.Id == seed.ItemId);
        Assert.True(await service.ApplyResolvedCoverageAsync(item, seed.Now, default));
        await seed.Db.SaveChangesAsync();
        Assert.True(await seed.Db.TestCoverage.AsNoTracking().AnyAsync(x =>
            x.ProcedureRevisionId == seed.CaseARevisionId
            && x.RequirementRevisionId == seed.RequirementRevisionId && !x.IsSuspect));
        Assert.True(await seed.Db.TestCoverage.AsNoTracking().AnyAsync(x =>
            x.ProcedureRevisionId == seed.CaseBRevisionId
            && x.RequirementRevisionId == seed.RequirementRevisionId && !x.IsSuspect));

        Assert.True(await service.ReopenResolvedCoverageAsync(item, "Reopen both parents.", seed.Now, default));
        await seed.Db.SaveChangesAsync();
        Assert.Equal(2, await seed.Db.TestCoverage.AsNoTracking().CountAsync(x =>
            x.RequirementRevisionId == seed.RequirementRevisionId && x.IsSuspect));
        Assert.True(await service.ApplyResolvedCoverageAsync(item, seed.Now, default));
        await seed.Db.SaveChangesAsync();
        Assert.Equal(0, await seed.Db.TestCoverage.AsNoTracking().CountAsync(x =>
            x.RequirementRevisionId == seed.RequirementRevisionId && x.IsSuspect));

        var change = new MaterializedRequirementChange(seed.ScrId, Guid.NewGuid(),
            RequirementChangeKind.Introduce, null, seed.RequirementRevisionId, "HLR-00000733.00");
        var itemByChange = new Dictionary<Guid, VerificationImpactItem> { [change.RequirementChangeId] = item };
        Assert.Equal(2, await service.ConfirmDecidedCoverageAsync([change], itemByChange,
            new List<TestRequirementCoverage>(), seed.Now, default));
        await seed.Db.SaveChangesAsync();

        var retargetItem = VerificationImpactItem.ForOrphanedProcedure(seed.ProjectId, seed.ReleaseId,
            seed.ScrId, seed.ReviewId, seed.ProcedureId, "HLRTP-000735", seed.Now);
        SetPrivate(retargetItem, nameof(VerificationImpactItem.RetargetedRequirementRevisionId),
            seed.RequirementRevisionId);
        retargetItem.Resolve("test.engineer", VerificationImpactOutcome.ProcedureRetargeted,
            "Retarget both source Cases.", seed.Now,
            retargetedRequirementRevisionId: seed.RequirementRevisionId);
        seed.Db.Add(retargetItem);
        await seed.Db.SaveChangesAsync();
        Assert.True(await service.HasEffectiveRetargetTargetAsync(seed.ProjectId, seed.ReleaseId,
            seed.ProcedureId, seed.RequirementRevisionId, default));
        Assert.True(await service.ApplyRetargetedCoverageAsync(retargetItem, seed.Now, default));
        await seed.Db.SaveChangesAsync();
    }

    private sealed record AllParentSeed(AeroLinkDbContext Db, Guid ProjectId, Guid ReleaseId, Guid BaselineId,
        Guid RequirementRevisionId, Guid CaseARevisionId, Guid CaseBRevisionId, Guid ProcedureId,
        Guid ProcedureRevisionId, Guid ItemId, Guid ScrId, Guid ReviewId, DateTimeOffset Now);

    private static async Task<AllParentSeed> SeedAllParentFixtureAsync(bool reversedLinks,
        bool coverageForBoth = true, bool openSuspectLinkOnB = false)
    {
        var options = new DbContextOptionsBuilder<AeroLinkDbContext>().UseSqlite("Data Source=:memory:").Options;
        var db = new AeroLinkDbContext(options);
        await db.Database.OpenConnectionAsync();
        await db.Database.EnsureCreatedAsync();
        var now = DateTimeOffset.UtcNow;
        var program = new ProgramRecord("All Parent Program", "APP");
        var project = new ProjectRecord(program.Id, "All Parent Software", "All Parent Product");
        var release = new SoftwareRelease(project.Id, "1.0", false);
        var baseline = new CandidateBaseline("SW-01.00", 0, project.Id, release.Id, null,
            "Candidate", "cm.test", now);
        db.AddRange(program, project, release, baseline);
        var configuration = LegacyDefaultProjectLadderFactory.Create(project.Id, now);
        db.ProjectLadderConfigurations.Add(configuration);
        await db.SaveChangesAsync();
        var seal = await new ProjectLadderSealAuthority(db).SealAsync(project.Id,
            LadderBoundContentCatalog.Current.First().Id, "all-parent-content", "test.sealer", now);
        Assert.Equal(ProjectLadderSealResultKind.Sealed, seal.Kind);
        var scr = ApprovedBaselineScr(project.Id, release.Id, "SRCR-00733", now);
        var requirement = new RequirementArtifact(project.Id, "HLR-00000733",
            RequirementLevel.HighLevel, now);
        var requirementRevision = new RequirementRevision(requirement.Id, 0,
            "The software shall sequence.", "Rationale", "Test", RequirementRevisionState.Active,
            scr.Id, baseline.Id, now, parentKind: RequirementParentKind.Derived,
            derivedRationale: "Derived for the all-parent fixture.");
        var caseA = new TestProcedure(project.Id, "HLRTC-000733", "Parent case A",
            "test.engineer", now, TestProcedureLevel.HighLevel);
        var caseARevision = new TestProcedureRevision(caseA.Id, 0,
            "Verify A", "Preconditions", "Steps", "Expected",
            TestProcedureState.Approved, "test.engineer", now, effectiveBaselineId: baseline.Id,
            parentKind: VerificationProcedureParentKind.Allocated);
        var caseB = new TestProcedure(project.Id, "HLRTC-000734", "Parent case B",
            "test.engineer", now, TestProcedureLevel.HighLevel);
        var caseBRevision = new TestProcedureRevision(caseB.Id, 0,
            "Verify B", "Preconditions", "Steps", "Expected",
            TestProcedureState.Approved, "test.engineer", now, effectiveBaselineId: baseline.Id,
            parentKind: coverageForBoth
                ? VerificationProcedureParentKind.Allocated
                : VerificationProcedureParentKind.Derived,
            derivedRationale: coverageForBoth ? null :
                "Standalone fixture Case without coverage; the missing-parent scenario never links it.");
        db.AddRange(scr, requirement, requirementRevision, caseA, caseARevision, caseB, caseBRevision,
            new TestRequirementCoverage(caseARevision.Id, requirementRevision.Id),
            new BaselineRequirementSelection(baseline.Id, requirement.Id, requirementRevision.Id),
            new BaselineTestProcedureSelection(baseline.Id, caseA.Id, caseARevision.Id),
            new BaselineTestProcedureSelection(baseline.Id, caseB.Id, caseBRevision.Id));
        if (coverageForBoth)
            db.Add(new TestRequirementCoverage(caseBRevision.Id, requirementRevision.Id));
        baseline.Select(scr, "cm.test", now);
        baseline.Freeze("cm.test", now);
        baseline.MarkRequirementsMaterialized("cm.test", new string('a', 64), 1, now);
        baseline.MarkTestProceduresMaterialized("cm.test", new string('b', 64), 2, now);
        using (db.UseLegacyHistoricalSeed()) await db.SaveChangesAsync();
        var (legacy, typed) = FullRegistrations();
        await new SoftwareProcedureExecutionCutoverAuthority(db, legacy, typed,
            allowSqliteExecution: true).EnsureCompletedAsync();

        var policy = new EffectiveProjectLadderPolicyResolver(db);
        var ladderPolicy = await policy.ResolveAsync(project.Id);
        var authored = new TestProcedure(project.Id, "HLRTP-000735", "Two-parent procedure",
            "test.engineer", now, TestProcedureLevel.HighLevel, ladderPolicy,
            VerificationArtifactKind.Procedure, VerificationProcedureParentKind.Allocated);
        var authoredRevision = new TestProcedureRevision(authored.Id, 0,
            "Execute both parents", "Procedure setup", "Procedure steps", "Expected observation",
            TestProcedureState.Draft, "test.engineer", now,
            environmentSetup: "Setup", testData: "Data", orderedSteps: "Steps",
            expectedObservations: "Expected", cleanup: "Cleanup", toolingAutomation: "Tooling",
            parentKind: VerificationProcedureParentKind.Allocated);
        var linkCandidates = reversedLinks
            ? new[] { new TestCaseProcedureLink(caseBRevision.Id, authoredRevision.Id),
                      new TestCaseProcedureLink(caseARevision.Id, authoredRevision.Id) }
            : new[] { new TestCaseProcedureLink(caseARevision.Id, authoredRevision.Id),
                      new TestCaseProcedureLink(caseBRevision.Id, authoredRevision.Id) };
        var links = linkCandidates.ToList();
        if (openSuspectLinkOnB)
        {
            // An OPEN suspect lifecycle must be attached when the link is first created; the association is
            // immutable afterwards. B is then historical revalidation evidence, never an effective parent.
            var linkB = links.Single(x => x.CaseRevisionId == caseBRevision.Id);
            var lifecycle = ExactLinkSuspectLifecycle.Raise(project.Id, ExactLinkKind.CaseProcedure,
                linkB.Id, ExactLinkLifecycleCauseKind.InternalVerificationRevision, null, null,
                "test.engineer", "Open revalidation evidence, not an effective parent.", now,
                causeVerificationRevisionId: caseBRevision.Id);
            linkB.AttachExactLinkLifecycle(lifecycle.Id);
            db.Add(lifecycle);
        }
        db.AddRange(authored, authoredRevision);
        db.AddRange(links);
        db.Add(new BaselineTestProcedureSelection(baseline.Id, authored.Id, authoredRevision.Id));
        using (db.UseSaveBoundaryPolicy(ladderPolicy)) await db.SaveChangesAsync();
        db.Entry(authoredRevision).Property(x => x.State).CurrentValue = TestProcedureState.Approved;
        using (db.UseSaveBoundaryPolicy(ladderPolicy)) await db.SaveChangesAsync();

        var review = new TestChangeReview(project.Id, release.Id, scr.Id,
            TestChangeReviewDiscipline.HighLevelSoftware, "SRCR-00733.00", now);
        db.Add(review);
        var item = VerificationImpactItem.ForIntroducedRequirement(project.Id, release.Id,
            scr.Id, review.Id, Guid.NewGuid(), "HLR-00000733", "Test", now);
        SetPrivate(item, nameof(VerificationImpactItem.RequirementRevisionId), requirementRevision.Id);
        item.Resolve("test.engineer", VerificationImpactOutcome.ProcedureCoverageConfirmed,
            "The Procedure covers the requirement through its exact source Cases.", now,
            procedureId: authored.Id, procedureRevisionId: authoredRevision.Id);
        db.Add(item);
        await db.SaveChangesAsync();
        return new AllParentSeed(db, project.Id, release.Id, baseline.Id, requirementRevision.Id,
            caseARevision.Id, caseBRevision.Id, authored.Id, authoredRevision.Id, item.Id, scr.Id,
            review.Id, now);
    }

    [Fact]
    public async Task Document_generated_before_later_baseline_materialization_uses_legacy_basis_and_excludes_later_revisions()
    {
        var options = new DbContextOptionsBuilder<AeroLinkDbContext>().UseSqlite("Data Source=:memory:").Options;
        var db = new AeroLinkDbContext(options);
        await db.Database.OpenConnectionAsync();
        await db.Database.EnsureCreatedAsync();
        var evidenceRoot = Path.Combine(Path.GetTempPath(), $"aerolink-726-temporal-{Guid.NewGuid():N}");
        var files = new EvidenceFileStore(evidenceRoot);
        try
        {
            var seed = await SeedSignedDocumentProjectAsync(db, files, "500001", materialized: false);
            var now = DateTimeOffset.UtcNow;
            var originalStorageKey = await db.ControlledDocumentArtifacts.AsNoTracking()
                .Where(x => x.Id == seed.ArtifactId).Select(x => x.StorageKey).SingleAsync();
            var caseArtifact = await db.TestProcedures.AsNoTracking()
                .SingleAsync(x => x.ProjectId == seed.ProjectId
                    && x.ArtifactKind == VerificationArtifactKind.Case);
            var caseRevisionId = await db.TestProcedureRevisions.AsNoTracking()
                .Where(x => x.ProcedureId == caseArtifact.Id && x.Revision == 0)
                .Select(x => x.Id).SingleAsync();
            // Case .01 is approved AFTER the document and AFTER later baseline materialization. The legacy
            // document must stay on .00 forever.
            var laterRevision = new TestProcedureRevision(caseArtifact.Id, 1, "Later revision", "P", "S", "E",
                TestProcedureState.Approved, "test.engineer", now.AddDays(1),
                effectiveBaselineId: seed.BaselineId);
            db.Add(laterRevision);
            var baseline = await db.CandidateBaselines.SingleAsync(x => x.Id == seed.BaselineId);
            var materializedAt = seed.GeneratedAt.AddMinutes(1);
            baseline.MarkTestProceduresMaterialized("cm.test", new string('b', 64), 2, materializedAt);
            // Current exact membership moves to .01 (one selection per procedure per baseline), while the
            // legacy document stays on .00 forever.
            var selection = await db.BaselineTestProcedures.SingleAsync(
                x => x.BaselineId == baseline.Id && x.ProcedureId == caseArtifact.Id);
            selection.RebindMigrationExecutable(caseArtifact.Id, laterRevision.Id);
            await db.SaveChangesAsync();

            var (legacy, typed) = FullRegistrations();
            var generator = new ControlledOutputGenerator(db, new RichContentPublisher(db, files),
                policyResolver: new EffectiveProjectLadderPolicyResolver(db));
            var authority = new SoftwareProcedureExecutionCutoverAuthority(db, legacy, typed,
                allowSqliteExecution: true, generator: generator, files: files);
            var result = await authority.EnsureCompletedAsync();
            Assert.Equal(1, result.ProjectsUpgraded);

            var updatedBaseline = await db.CandidateBaselines.AsNoTracking()
                .SingleAsync(x => x.Id == seed.BaselineId);
            Assert.Equal(materializedAt, updatedBaseline.TestProceduresMaterializedAt);
            Assert.NotEqual(new string('b', 64), updatedBaseline.TestProceduresHash);

            var document = await db.ControlledDocuments.AsNoTracking().SingleAsync(x => x.Id == seed.DocumentId);
            Assert.Equal(seed.GeneratedAt, document.GeneratedAt);
            Assert.NotEqual(new string('c', 64), document.ContentHash);
            var basisEvent = await db.SecurityAuditEvents.AsNoTracking().SingleAsync(x =>
                x.EventType == "VerificationExecutionCutover.SoftwareProcedures.v1.LegacyDocumentBasisReconstructed"
                && x.Target == $"ControlledDocument:{seed.DocumentId}");
            using (var detail = JsonDocument.Parse(basisEvent.Detail))
            {
                Assert.Equal(seed.DocumentId, detail.RootElement.GetProperty("documentId").GetGuid());
                Assert.Equal(seed.BaselineId, detail.RootElement.GetProperty("baselineId").GetGuid());
                Assert.Equal(seed.GeneratedAt, detail.RootElement.GetProperty("generatedAt").GetDateTimeOffset());
                Assert.Equal(1, detail.RootElement.GetProperty("artifactCount").GetInt32());
                Assert.Equal(materializedAt,
                    detail.RootElement.GetProperty("baselineMaterializedAt").GetDateTimeOffset());
                Assert.False(detail.RootElement.GetProperty("baselineWasMaterializedWhenDocumentGenerated").GetBoolean());
                Assert.True(detail.RootElement.GetProperty("baselineManifestStatePreserved").GetBoolean());
                Assert.True(detail.RootElement.GetProperty("documentGeneratedAtPreserved").GetBoolean());
                Assert.Equal(64, detail.RootElement.GetProperty("compatibilitySnapshotHash").GetString()!.Length);
            }

            // The persisted content basis is the canonical manifest hash of the historical .00 population,
            // not the later baseline's current membership.
            var historicalEntry = new TestProcedureManifestEntry(
                caseArtifact.Id, caseRevisionId, caseArtifact.BaseNumber, 0);
            var expectedBasis = $"{TestProcedureManifest.Hash([historicalEntry])}|" +
                $"{document.Type}|{document.ArtifactCount}|aerolink-migration";
            var expectedContentHash = Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(expectedBasis)))
                .ToLowerInvariant();
            Assert.Equal(expectedContentHash, document.ContentHash);

            var artifact = await db.ControlledDocumentArtifacts.AsNoTracking()
                .SingleAsync(x => x.Id == seed.ArtifactId);
            Assert.NotEqual(originalStorageKey, artifact.StorageKey);
            Assert.True(files.Exists(originalStorageKey));
            await using var generated = await files.OpenVerifiedReadAsync(
                artifact.StorageKey, artifact.Size, artifact.Sha256, default);
            using var copy = new MemoryStream();
            await generated.CopyToAsync(copy);
            using var archive = new ZipArchive(new MemoryStream(copy.ToArray()), ZipArchiveMode.Read);
            using var reader = new StreamReader(archive.GetEntry("word/document.xml")!.Open());
            var xml = await reader.ReadToEndAsync();
            Assert.Contains(caseArtifact.BaseNumber, xml);
            Assert.DoesNotContain($"{caseArtifact.BaseNumber}.01", xml);

            // Blocker 3: the superseded .00 mirror is preserved in the honest Retired state with typed
            // source provenance and NO executable link; the current .01 mirror is Approved and linked.
            var procedure = await db.TestProcedures.AsNoTracking()
                .SingleAsync(x => x.ProjectId == seed.ProjectId
                    && x.ArtifactKind == VerificationArtifactKind.Procedure);
            var mirroredRevisions = await db.TestProcedureRevisions.AsNoTracking()
                .Where(x => x.ProcedureId == procedure.Id).OrderBy(x => x.Revision).ToListAsync();
            Assert.Equal([0, 1], mirroredRevisions.Select(x => x.Revision).ToArray());
            Assert.Equal(TestProcedureState.Retired, mirroredRevisions[0].State);
            Assert.Equal(TestProcedureState.Approved, mirroredRevisions[1].State);
            Assert.Equal(0, await db.TestCaseProcedureLinks.AsNoTracking().CountAsync(x =>
                x.ProcedureRevisionId == mirroredRevisions[0].Id));
            Assert.Equal(1, await db.TestCaseProcedureLinks.AsNoTracking().CountAsync(x =>
                x.ProcedureRevisionId == mirroredRevisions[1].Id));
            Assert.Equal(2, await db.TestProcedureMigrationSources.AsNoTracking().CountAsync(x =>
                x.ProjectId == seed.ProjectId));
            Assert.True(await db.TestProcedureMigrationSources.AsNoTracking().AnyAsync(x =>
                x.SourceCaseRevisionId == caseRevisionId
                && x.GeneratedProcedureArtifactId == procedure.Id
                && x.GeneratedProcedureRevisionId == mirroredRevisions[0].Id));
            var impactService = new VerificationImpactService(db,
                policyResolver: new EffectiveProjectLadderPolicyResolver(db));
            Assert.NotNull(await impactService.FindApprovedProcedureAsync(
                seed.ProjectId, procedure.Id, default));
            Assert.Equal(mirroredRevisions[1].Id, (await impactService.FindApprovedProcedureAsync(
                seed.ProjectId, procedure.Id, default))!.RevisionId);
        }
        finally
        {
            try { Directory.Delete(evidenceRoot, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task Fully_dormant_migration_mirror_is_retired_typed_and_never_approved()
    {
        var options = new DbContextOptionsBuilder<AeroLinkDbContext>().UseSqlite("Data Source=:memory:").Options;
        var db = new AeroLinkDbContext(options);
        await db.Database.OpenConnectionAsync();
        await db.Database.EnsureCreatedAsync();
        var now = DateTimeOffset.UtcNow;
        var program = new ProgramRecord("Dormant Program", "DRM");
        var project = new ProjectRecord(program.Id, "Dormant Software", "Dormant Product");
        var release = new SoftwareRelease(project.Id, "1.0", false);
        var baseline = new CandidateBaseline("SW-01.00", 0, project.Id, release.Id, null,
            "Candidate", "cm.test", now);
        db.AddRange(program, project, release, baseline);
        var configuration = LegacyDefaultProjectLadderFactory.Create(project.Id, now);
        db.ProjectLadderConfigurations.Add(configuration);
        await db.SaveChangesAsync();
        var seal = await new ProjectLadderSealAuthority(db).SealAsync(project.Id,
            LadderBoundContentCatalog.Current.First().Id, "dormant-content", "test.sealer", now);
        Assert.Equal(ProjectLadderSealResultKind.Sealed, seal.Kind);

        var caseArtifact = new TestProcedure(project.Id, "HLRTC-000800", "Historical dormant case",
            "test.engineer", now, TestProcedureLevel.HighLevel);
        var caseRevision = new TestProcedureRevision(caseArtifact.Id, 0,
            "Verify historical work", "Preconditions", "Steps", "Expected",
            TestProcedureState.Approved, "test.engineer", now, effectiveBaselineId: baseline.Id,
            parentKind: VerificationProcedureParentKind.Derived,
            derivedRationale: "Historical dormant fixture Case with no current requirement coverage.");
        // The Case retains its historical EffectiveBaselineId but is selected in NO current baseline: it is
        // fully dormant at cutover time.
        db.AddRange(caseArtifact, caseRevision);
        await db.SaveChangesAsync();

        var (legacy, typed) = FullRegistrations();
        var result = await new SoftwareProcedureExecutionCutoverAuthority(db, legacy, typed,
            allowSqliteExecution: true).EnsureCompletedAsync();
        Assert.Equal(1, result.ProceduresGenerated);
        var procedure = await db.TestProcedures.AsNoTracking()
            .SingleAsync(x => x.ProjectId == project.Id
                && x.ArtifactKind == VerificationArtifactKind.Procedure);
        var mirror = await db.TestProcedureRevisions.AsNoTracking()
            .SingleAsync(x => x.ProcedureId == procedure.Id);
        Assert.Equal(TestProcedureState.Retired, mirror.State);
        Assert.Null(mirror.EffectiveBaselineId);
        Assert.Equal(0, await db.TestCaseProcedureLinks.AsNoTracking().CountAsync());
        var source = await db.TestProcedureMigrationSources.AsNoTracking().SingleAsync(x =>
            x.ProjectId == project.Id);
        Assert.Equal(caseRevision.Id, source.SourceCaseRevisionId);
        Assert.Equal(procedure.Id, source.GeneratedProcedureArtifactId);
        Assert.Equal(mirror.Id, source.GeneratedProcedureRevisionId);

        // No runtime selection seam can see an approved executable revision on this artifact.
        var service = new VerificationImpactService(db,
            policyResolver: new EffectiveProjectLadderPolicyResolver(db));
        Assert.Null(await service.FindApprovedProcedureAsync(project.Id, procedure.Id, default));
        var effectivity = await TestProcedureEffectivity.ForReleaseAsync(db, project.Id, release.Id, default);
        Assert.False(effectivity?.RevisionByProcedure.ContainsKey(procedure.Id) == true);
    }

    [Fact]
    public async Task Completed_totals_are_immutable_after_ordinary_post_cutover_activity()
    {
        var seed = await SeedAsync();
        var (legacy, typed) = FullRegistrations();
        var authority = new SoftwareProcedureExecutionCutoverAuthority(seed.Db, legacy, typed,
            allowSqliteExecution: true);
        var completed = await authority.EnsureCompletedAsync();
        var auditCountBefore = await seed.Db.SecurityAuditEvents.AsNoTracking()
            .CountAsync(x => x.EventType.StartsWith("VerificationExecutionCutover"));

        // Ordinary post-cutover activity against the generated Procedure must NEVER change the reported
        // completion totals: the stored marker is the immutable answer.
        var procedure = await seed.Db.TestProcedures.AsNoTracking()
            .SingleAsync(x => x.ProjectId == seed.ProjectId
                && x.ArtifactKind == VerificationArtifactKind.Procedure);
        var procedureRevision = await seed.Db.TestProcedureRevisions.AsNoTracking()
            .SingleAsync(x => x.ProcedureId == procedure.Id);
        seed.Db.TestExecutions.Add(new TestExecution(seed.ProjectId, procedureRevision.Id, null, null,
            TestOutcome.Pass, "test.engineer", "Rig A", "Human determination", "evidence/post.json",
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, seed.ReleaseId));
        var set = await seed.Db.BuildTestSets.SingleAsync(x => x.ReleaseId == seed.ReleaseId);
        set.Include("test.lead", procedureRevision.Id, TestSelectionReason.Chosen,
            "post-cutover", DateTimeOffset.UtcNow);
        var now = DateTimeOffset.UtcNow;
        var scr = ApprovedBaselineScr(seed.ProjectId, seed.ReleaseId, "SRCR-00999", now);
        var requirement = new RequirementArtifact(seed.ProjectId, "HLR-00000999",
            RequirementLevel.HighLevel, now);
        var requirementRevision = new RequirementRevision(requirement.Id, 0,
            "Post-cutover retarget target.", "Rationale", "Test", RequirementRevisionState.Active,
            scr.Id, seed.BaselineId, now, parentKind: RequirementParentKind.Derived,
            derivedRationale: "Post-cutover retarget target.");
        var review = new TestChangeReview(seed.ProjectId, seed.ReleaseId, scr.Id,
            TestChangeReviewDiscipline.HighLevelSoftware, scr.DisplayNumber, now);
        var impact = VerificationImpactItem.ForOrphanedProcedure(seed.ProjectId, seed.ReleaseId,
            scr.Id, review.Id, procedure.Id, procedure.BaseNumber, now);
        SetPrivate(impact, nameof(VerificationImpactItem.RetargetedRequirementRevisionId),
            requirementRevision.Id);
        impact.Resolve("test.engineer", VerificationImpactOutcome.ProcedureRetargeted,
            "Post-cutover decision.", now,
            retargetedRequirementRevisionId: requirementRevision.Id);
        seed.Db.AddRange(scr, requirement, requirementRevision, review, impact);
        await seed.Db.SaveChangesAsync();

        var rerun = await authority.EnsureCompletedAsync();
        Assert.Equal(completed, rerun);
        Assert.Equal(auditCountBefore, await seed.Db.SecurityAuditEvents.AsNoTracking()
            .CountAsync(x => x.EventType.StartsWith("VerificationExecutionCutover")));
    }

    [Fact]
    public async Task Completed_marker_without_audit_recovers_and_returns_stored_totals()
    {
        var seed = await SeedAsync();
        var (legacy, typed) = FullRegistrations();
        var authority = new SoftwareProcedureExecutionCutoverAuthority(seed.Db, legacy, typed,
            allowSqliteExecution: true);
        var completed = await authority.EnsureCompletedAsync();

        // Simulate the only historically possible inconsistent state: the marker committed but its Completed
        // audit evidence did not (the pre-atomic build saved the two rows separately).
        var completionAudits = await seed.Db.SecurityAuditEvents
            .Where(x => x.EventType == "VerificationExecutionCutover.SoftwareProcedures.v1.Completed")
            .ToListAsync();
        seed.Db.SecurityAuditEvents.RemoveRange(completionAudits);
        await seed.Db.SaveChangesAsync();

        var recovered = await authority.EnsureCompletedAsync();
        Assert.Equal(completed, recovered);
        Assert.Equal(1, await seed.Db.SecurityAuditEvents.AsNoTracking().CountAsync(x =>
            x.EventType == "VerificationExecutionCutover.SoftwareProcedures.v1.Completed"));
        var second = await authority.EnsureCompletedAsync();
        Assert.Equal(completed, second);
        Assert.Equal(1, await seed.Db.SecurityAuditEvents.AsNoTracking().CountAsync(x =>
            x.EventType == "VerificationExecutionCutover.SoftwareProcedures.v1.Completed"));
    }

    [Fact]
    public async Task Forced_claim_persistence_failure_leaves_no_partial_claim_and_propagates()
    {
        // One shared in-memory SQLite connection keeps the same physical database across the failing and
        // recovery contexts.
        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AeroLinkDbContext>().UseSqlite(connection)
            .AddInterceptors(new ClaimSaveFailingInterceptor())
            .Options;
        var db = new AeroLinkDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var seed = await SeedIntoAsync(db, "600001");
        var (legacy, typed) = FullRegistrations();
        var authority = new SoftwareProcedureExecutionCutoverAuthority(db, legacy, typed,
            allowSqliteExecution: true);

        // A non-unique persistence failure during the claim save is a real defect: it must propagate and
        // leave NO marker and NO completion audit (the claim and its audit commit atomically).
        await Assert.ThrowsAsync<DbUpdateException>(() => authority.EnsureCompletedAsync());
        Assert.Equal(0, await db.GovernedMigrationCompletions.AsNoTracking().CountAsync());
        Assert.Equal(0, await db.SecurityAuditEvents.AsNoTracking().CountAsync(x =>
            x.EventType == "VerificationExecutionCutover.SoftwareProcedures.v1.Completed"));
        Assert.Equal(1, await db.ProjectLadderConfigurations.AsNoTracking().CountAsync(x =>
            x.ProjectId == seed.ProjectId && x.State == ProjectLadderConfigurationState.Active));

        // A fresh startup (no interceptor) recovers the committed per-project work and completes cleanly.
        var cleanOptions = new DbContextOptionsBuilder<AeroLinkDbContext>().UseSqlite(connection).Options;
        var cleanDb = new AeroLinkDbContext(cleanOptions);
        var recovery = await new SoftwareProcedureExecutionCutoverAuthority(cleanDb, legacy, typed,
            allowSqliteExecution: true).EnsureCompletedAsync();
        Assert.Equal(1, recovery.ProjectsUpgraded);
        Assert.Equal(1, await cleanDb.GovernedMigrationCompletions.AsNoTracking().CountAsync());
        Assert.Equal(1, await cleanDb.SecurityAuditEvents.AsNoTracking().CountAsync(x =>
            x.EventType == "VerificationExecutionCutover.SoftwareProcedures.v1.Completed"));
    }

    [Fact]
    public async Task Rollback_after_stored_rendition_removes_only_orphan_bytes_and_preserves_original_evidence()
    {
        // One shared in-memory SQLite connection keeps the same physical database across the failing and
        // recovery contexts.
        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var seedOptions = new DbContextOptionsBuilder<AeroLinkDbContext>().UseSqlite(connection).Options;
        var db = new AeroLinkDbContext(seedOptions);
        await db.Database.EnsureCreatedAsync();
        var evidenceRoot = Path.Combine(Path.GetTempPath(), $"aerolink-726-rollback-{Guid.NewGuid():N}");
        var files = new EvidenceFileStore(evidenceRoot);
        try
        {
            var seed = await SeedSignedDocumentProjectAsync(db, files, "700001", materialized: true);
            // A second stored rendition makes the rollback observable after the first artifact's new bytes
            // have already been written.
            var secondBytes = Encoding.UTF8.GetBytes("second pre-cutover rendition");
            var secondStored = await files.StoreAsync(new MemoryStream(secondBytes), "700001.pdf",
                "application/pdf", default);
            db.Add(new ControlledDocumentArtifact(seed.DocumentId, "pdf", secondStored.StorageKey,
                secondStored.OriginalFileName, secondStored.ContentType, secondStored.Size,
                secondStored.Sha256, seed.GeneratedAt));
            await db.SaveChangesAsync();
            var originalKeys = Directory.EnumerateFiles(evidenceRoot, "*", SearchOption.AllDirectories)
                .Select(Path.GetFileName).ToHashSet();

            var (legacy, typed) = FullRegistrations();
            var failingOptions = new DbContextOptionsBuilder<AeroLinkDbContext>().UseSqlite(connection)
                .AddInterceptors(new RenditionSaveFailingInterceptor())
                .Options;
            var failingDb = new AeroLinkDbContext(failingOptions);
            var generator = new ControlledOutputGenerator(failingDb, new RichContentPublisher(failingDb, files),
                policyResolver: new EffectiveProjectLadderPolicyResolver(failingDb));
            var failingAuthority = new SoftwareProcedureExecutionCutoverAuthority(failingDb, legacy, typed,
                allowSqliteExecution: true, generator: generator, files: files);
            await Assert.ThrowsAsync<InvalidOperationException>(() => failingAuthority.EnsureCompletedAsync());
            failingDb.Dispose();

            // The database transaction rolled back: artifacts still reference the ORIGINAL keys, every
            // original file remains, and no orphaned new bytes survive the cleanup.
            var artifacts = await db.ControlledDocumentArtifacts.AsNoTracking().ToListAsync();
            Assert.Equal(2, artifacts.Count);
            foreach (var artifact in artifacts)
            {
                Assert.Contains(Path.GetFileName(artifact.StorageKey), originalKeys);
                Assert.True(files.Exists(artifact.StorageKey));
            }
            var remainingKeys = Directory.EnumerateFiles(evidenceRoot, "*", SearchOption.AllDirectories)
                .Select(Path.GetFileName).ToHashSet();
            Assert.Equal(originalKeys.Count, remainingKeys.Count);
            Assert.Empty(remainingKeys.Except(originalKeys));

            // Recovery: a fresh startup regenerates from the preserved originals and commits; old bytes are
            // still retained alongside the new referenced renditions.
            var cleanDb = new AeroLinkDbContext(
                new DbContextOptionsBuilder<AeroLinkDbContext>().UseSqlite(connection).Options);
            var cleanGenerator = new ControlledOutputGenerator(cleanDb, new RichContentPublisher(cleanDb, files),
                policyResolver: new EffectiveProjectLadderPolicyResolver(cleanDb));
            var recovery = await new SoftwareProcedureExecutionCutoverAuthority(cleanDb, legacy, typed,
                allowSqliteExecution: true, generator: cleanGenerator, files: files).EnsureCompletedAsync();
            Assert.Equal(1, recovery.ProjectsUpgraded);
            var recoveredArtifacts = await cleanDb.ControlledDocumentArtifacts.AsNoTracking().ToListAsync();
            Assert.Equal(2, recoveredArtifacts.Count);
            var finalKeys = Directory.EnumerateFiles(evidenceRoot, "*", SearchOption.AllDirectories)
                .Select(Path.GetFileName).ToHashSet();
            foreach (var artifact in recoveredArtifacts)
            {
                Assert.True(files.Exists(artifact.StorageKey));
                Assert.Contains(Path.GetFileName(artifact.StorageKey), finalKeys);
            }
            Assert.True(finalKeys.Count >= originalKeys.Count + 2);
        }
        finally
        {
            try { Directory.Delete(evidenceRoot, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task Rollback_cleanup_failure_evidence_is_isolated_bounded_and_never_resaves_cutover_work()
    {
        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var seedOptions = new DbContextOptionsBuilder<AeroLinkDbContext>().UseSqlite(connection).Options;
        var db = new AeroLinkDbContext(seedOptions);
        await db.Database.EnsureCreatedAsync();
        var evidenceRoot = Path.Combine(Path.GetTempPath(), $"aerolink-726-cleanup-evidence-{Guid.NewGuid():N}");
        var files = new EvidenceFileStore(evidenceRoot);
        try
        {
            var seed = await SeedSignedDocumentProjectAsync(db, files, "800001", materialized: true);
            var secondBytes = Encoding.UTF8.GetBytes("second pre-cutover rendition");
            var secondStored = await files.StoreAsync(new MemoryStream(secondBytes), "800001.pdf",
                "application/pdf", default);
            db.Add(new ControlledDocumentArtifact(seed.DocumentId, "pdf", secondStored.StorageKey,
                secondStored.OriginalFileName, secondStored.ContentType, secondStored.Size,
                secondStored.Sha256, seed.GeneratedAt));
            await db.SaveChangesAsync();

            var originalConfiguration = await db.ProjectLadderConfigurations.AsNoTracking()
                .SingleAsync(x => x.ProjectId == seed.ProjectId);
            var originalDocumentHash = await db.ControlledDocuments.AsNoTracking()
                .Where(x => x.Id == seed.DocumentId).Select(x => x.ContentHash).SingleAsync();
            var originalArtifacts = await db.ControlledDocumentArtifacts.AsNoTracking()
                .Where(x => x.DocumentId == seed.DocumentId)
                .Select(x => new { x.Id, x.StorageKey, x.Sha256 }).ToListAsync();
            var originalSelection = await db.BaselineTestProcedures.AsNoTracking()
                .SingleAsync(x => x.BaselineId == seed.BaselineId);
            var originalAuditCount = await db.SecurityAuditEvents.AsNoTracking().CountAsync();

            var (legacy, typed) = FullRegistrations();
            var failingOptions = new DbContextOptionsBuilder<AeroLinkDbContext>().UseSqlite(connection)
                .AddInterceptors(new RenditionSaveFailingInterceptor())
                .Options;
            var failingDb = new AeroLinkDbContext(failingOptions);
            var generator = new ControlledOutputGenerator(failingDb, new RichContentPublisher(failingDb, files),
                policyResolver: new EffectiveProjectLadderPolicyResolver(failingDb));
            // Every newly written rendition's deletion fails, forcing the cleanup-evidence path.
            var failingAuthority = new SoftwareProcedureExecutionCutoverAuthority(failingDb, legacy, typed,
                allowSqliteExecution: true, generator: generator, files: files,
                cleanupDeleteFailure: _ => true);
            await Assert.ThrowsAsync<InvalidOperationException>(() => failingAuthority.EnsureCompletedAsync());
            failingDb.Dispose();

            // NOTHING from the failed cutover committed: no ladder activation, generated Procedure, source,
            // link, rebind, document replacement, signature supersession, marker, or Completed audit.
            var configuration = await db.ProjectLadderConfigurations.AsNoTracking()
                .SingleAsync(x => x.ProjectId == seed.ProjectId);
            Assert.Equal(ProjectLadderConfigurationState.Stored, configuration.State);
            Assert.True(configuration.IsSealed);
            Assert.Null(configuration.LastUpgradeBy);
            Assert.Equal(0, await db.TestProcedures.AsNoTracking().CountAsync(x =>
                x.ProjectId == seed.ProjectId && x.ArtifactKind == VerificationArtifactKind.Procedure));
            Assert.Equal(0, await db.TestProcedureMigrationSources.AsNoTracking().CountAsync());
            Assert.Equal(0, await db.TestCaseProcedureLinks.AsNoTracking().CountAsync());
            Assert.Equal(0, await db.GovernedMigrationCompletions.AsNoTracking().CountAsync());
            Assert.Equal(0, await db.SecurityAuditEvents.AsNoTracking().CountAsync(x =>
                x.EventType == SoftwareProcedureExecutionCutoverAuthority.MigrationMarker + ".Completed"));
            Assert.Equal(0, await db.SecurityAuditEvents.AsNoTracking().CountAsync(x =>
                x.EventType.StartsWith(SoftwareProcedureExecutionCutoverAuthority.MigrationMarker)
                && x.EventType != SoftwareProcedureExecutionCutoverAuthority.MigrationMarker
                    + ".OrphanedRenditionCleanupFailed"));
            Assert.Equal(originalDocumentHash, await db.ControlledDocuments.AsNoTracking()
                .Where(x => x.Id == seed.DocumentId).Select(x => x.ContentHash).SingleAsync());
            foreach (var original in originalArtifacts)
            {
                var current = await db.ControlledDocumentArtifacts.AsNoTracking()
                    .SingleAsync(x => x.Id == original.Id);
                Assert.Equal(original.StorageKey, current.StorageKey);
                Assert.Equal(original.Sha256, current.Sha256);
                Assert.True(files.Exists(original.StorageKey));
            }
            var selection = await db.BaselineTestProcedures.AsNoTracking()
                .SingleAsync(x => x.BaselineId == seed.BaselineId);
            Assert.Equal(originalSelection.ProcedureId, selection.ProcedureId);
            Assert.Equal(originalSelection.RevisionId, selection.RevisionId);

            // The ONLY new database state is the bounded cleanup-failure evidence.
            Assert.Equal(originalAuditCount + 1, await db.SecurityAuditEvents.AsNoTracking().CountAsync());
            var evidenceRows = await db.RollbackCleanupFailureEvidences.AsNoTracking().ToListAsync();
            var evidenceRow = Assert.Single(evidenceRows);
            var failedKeys = evidenceRow.Content.Split(';');
            Assert.Equal(2, failedKeys.Length);
            Assert.Equal(2, evidenceRow.TotalKeys);
            Assert.Equal(2, evidenceRow.EntryCount);
            Assert.True(evidenceRow.Content.Length <= 1500);
            // Every failed key remains recoverable: the orphaned bytes still exist and are named exactly.
            Assert.All(failedKeys, key => Assert.True(files.Exists(key), $"orphaned key {key} must be recoverable"));
            var expectedHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                Encoding.UTF8.GetBytes(string.Join(";", failedKeys.OrderBy(x => x, StringComparer.Ordinal)))))
                .ToLowerInvariant();
            Assert.Equal(expectedHash, evidenceRow.CanonicalAggregateHash);
            var summary = await db.SecurityAuditEvents.AsNoTracking().SingleAsync(x =>
                x.EventType == SoftwareProcedureExecutionCutoverAuthority.MigrationMarker
                    + ".OrphanedRenditionCleanupFailed");
            Assert.Contains("\"keys\":2", summary.Detail);
            Assert.Contains("\"chunks\":1", summary.Detail);
            Assert.True(summary.Detail.Length < 4000);

            // Retry of the SAME operation cannot duplicate cleanup evidence.
            var retryContext = new AeroLinkDbContext(
                new DbContextOptionsBuilder<AeroLinkDbContext>().UseSqlite(connection).Options);
            var retryAuthority = new SoftwareProcedureExecutionCutoverAuthority(retryContext, legacy, typed,
                allowSqliteExecution: true);
            await retryAuthority.RecordCleanupFailureEvidenceAsync(evidenceRow.OperationId, failedKeys, default);
            Assert.Equal(1, await retryContext.RollbackCleanupFailureEvidences.AsNoTracking().CountAsync());
            Assert.Equal(1, await retryContext.SecurityAuditEvents.AsNoTracking().CountAsync(x =>
                x.EventType == SoftwareProcedureExecutionCutoverAuthority.MigrationMarker
                    + ".OrphanedRenditionCleanupFailed"));

            // A fresh successful startup still completes coherently and keeps the evidence intact.
            var cleanDb = new AeroLinkDbContext(
                new DbContextOptionsBuilder<AeroLinkDbContext>().UseSqlite(connection).Options);
            var cleanGenerator = new ControlledOutputGenerator(cleanDb, new RichContentPublisher(cleanDb, files),
                policyResolver: new EffectiveProjectLadderPolicyResolver(cleanDb));
            var recovery = await new SoftwareProcedureExecutionCutoverAuthority(cleanDb, legacy, typed,
                allowSqliteExecution: true, generator: cleanGenerator, files: files).EnsureCompletedAsync();
            Assert.Equal(1, recovery.ProjectsUpgraded);
            Assert.Equal(1, recovery.ProceduresGenerated);
            Assert.Equal(1, await cleanDb.GovernedMigrationCompletions.AsNoTracking().CountAsync());
            Assert.Equal(1, await cleanDb.SecurityAuditEvents.AsNoTracking().CountAsync(x =>
                x.EventType == SoftwareProcedureExecutionCutoverAuthority.MigrationMarker + ".Completed"));
            Assert.Equal(1, await cleanDb.TestProcedureMigrationSources.AsNoTracking().CountAsync());
            Assert.Equal(1, await cleanDb.RollbackCleanupFailureEvidences.AsNoTracking().CountAsync());
            Assert.Equal(1, await cleanDb.SecurityAuditEvents.AsNoTracking().CountAsync(x =>
                x.EventType == SoftwareProcedureExecutionCutoverAuthority.MigrationMarker
                    + ".OrphanedRenditionCleanupFailed"));
        }
        finally
        {
            try { Directory.Delete(evidenceRoot, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task Provenance_sources_and_cleanup_evidence_are_immutable_at_the_sqlite_save_boundary()
    {
        var options = new DbContextOptionsBuilder<AeroLinkDbContext>().UseSqlite("Data Source=:memory:").Options;
        var db = new AeroLinkDbContext(options);
        await db.Database.OpenConnectionAsync();
        await db.Database.EnsureCreatedAsync();
        var evidenceRoot = Path.Combine(Path.GetTempPath(), $"aerolink-726-sqlite-immutable-{Guid.NewGuid():N}");
        var files = new EvidenceFileStore(evidenceRoot);
        try
        {
            var seed = await SeedSignedDocumentProjectAsync(db, files, "810001", materialized: true);
            var now = DateTimeOffset.UtcNow;
            var (legacy, typed) = FullRegistrations();
            var generator = new ControlledOutputGenerator(db, new RichContentPublisher(db, files),
                policyResolver: new EffectiveProjectLadderPolicyResolver(db));
            await new SoftwareProcedureExecutionCutoverAuthority(db, legacy, typed,
                allowSqliteExecution: true, generator: generator, files: files).EnsureCompletedAsync();

            // An unused but otherwise valid same-project Procedure pair, so the source-retarget attack is
            // completely valid at the reference level and must be refused only by immutability.
            var unusedProcedure = new TestProcedure(seed.ProjectId, "HLRTP-000899",
                "Unused procedure", "test.engineer", now, TestProcedureLevel.HighLevel,
                artifactKind: VerificationArtifactKind.Procedure,
                parentKind: VerificationProcedureParentKind.Derived);
            var unusedRevision = new TestProcedureRevision(unusedProcedure.Id, 0,
                "Unused", "PS", "SS", "E", TestProcedureState.Draft, "test.engineer", now,
                environmentSetup: "Setup", testData: "Data", orderedSteps: "Steps",
                expectedObservations: "Expected", cleanup: "Cleanup", toolingAutomation: "Tooling",
                parentKind: VerificationProcedureParentKind.Derived,
                derivedRationale: "Unused fixture procedure.");
            db.AddRange(unusedProcedure, unusedRevision);
            await db.SaveChangesAsync();
            db.Entry(unusedRevision).Property(x => x.State).CurrentValue = TestProcedureState.Approved;
            await db.SaveChangesAsync();

            // Cleanup evidence to mutate/delete.
            var cleanupContext = new AeroLinkDbContext(
                new DbContextOptionsBuilder<AeroLinkDbContext>().UseSqlite(db.Database.GetDbConnection()).Options);
            var evidenceAuthority = new SoftwareProcedureExecutionCutoverAuthority(cleanupContext, legacy, typed,
                allowSqliteExecution: true);
            var operationId = Guid.NewGuid();
            var evidenceKeys = new[] { "aa/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa-key1.bin",
                "bb/bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb-key2.bin" };
            await evidenceAuthority.RecordCleanupFailureEvidenceAsync(operationId, evidenceKeys, default);

            var otherEventId = await db.BaselineEvents.AsNoTracking()
                .Where(x => x.BaselineId == seed.BaselineId && x.EventType == "CandidateBaselineCreated")
                .Select(x => x.Id).SingleAsync();

            async Task RefuseAsync(Func<Task> action, string message) =>
                Assert.Contains(message, (await Assert.ThrowsAsync<DomainException>(action)).Message);

            // Provenance: content/totals rewrite, retarget to another valid same-baseline event, delete.
            db.ChangeTracker.Clear();
            var provenance = await db.BaselineExecutionCutoverProvenances.SingleAsync();
            SetPrivate(provenance, nameof(BaselineExecutionCutoverProvenance.TotalMappings), 99);
            await RefuseAsync(async () => await db.SaveChangesAsync(), "immutable historical evidence");
            db.ChangeTracker.Clear();
            provenance = await db.BaselineExecutionCutoverProvenances.SingleAsync();
            SetPrivate(provenance, nameof(BaselineExecutionCutoverProvenance.EventId), otherEventId);
            await RefuseAsync(async () => await db.SaveChangesAsync(), "immutable historical evidence");
            db.ChangeTracker.Clear();
            provenance = await db.BaselineExecutionCutoverProvenances.SingleAsync();
            db.BaselineExecutionCutoverProvenances.Remove(provenance);
            await RefuseAsync(async () => await db.SaveChangesAsync(), "immutable historical evidence");

            // Migration source: retarget to another completely valid same-project pair, then delete.
            db.ChangeTracker.Clear();
            var source = await db.TestProcedureMigrationSources.SingleAsync();
            SetPrivate(source, nameof(TestProcedureMigrationSource.GeneratedProcedureArtifactId),
                unusedProcedure.Id);
            SetPrivate(source, nameof(TestProcedureMigrationSource.GeneratedProcedureRevisionId),
                unusedRevision.Id);
            await RefuseAsync(async () => await db.SaveChangesAsync(), "immutable historical evidence");
            db.ChangeTracker.Clear();
            source = await db.TestProcedureMigrationSources.SingleAsync();
            db.TestProcedureMigrationSources.Remove(source);
            await RefuseAsync(async () => await db.SaveChangesAsync(), "immutable historical evidence");

            // Cleanup evidence: rewrite and delete are refused even though a later startup succeeded.
            db.ChangeTracker.Clear();
            var evidence = await db.RollbackCleanupFailureEvidences.SingleAsync();
            SetPrivate(evidence, nameof(RollbackCleanupFailureEvidence.TotalKeys), 99);
            await RefuseAsync(async () => await db.SaveChangesAsync(), "immutable historical evidence");
            db.ChangeTracker.Clear();
            evidence = await db.RollbackCleanupFailureEvidences.SingleAsync();
            db.RollbackCleanupFailureEvidences.Remove(evidence);
            await RefuseAsync(async () => await db.SaveChangesAsync(), "immutable historical evidence");

            // The controlled records remain exactly as committed.
            Assert.Equal(1, await db.BaselineExecutionCutoverProvenances.AsNoTracking().CountAsync());
            Assert.Equal(1, await db.TestProcedureMigrationSources.AsNoTracking().CountAsync());
            Assert.Equal(1, await db.RollbackCleanupFailureEvidences.AsNoTracking().CountAsync());
        }
        finally
        {
            try { Directory.Delete(evidenceRoot, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task Rollback_cleanup_failure_evidence_multi_chunk_is_bounded_complete_idempotent_and_immutable()
    {
        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var db = new AeroLinkDbContext(
            new DbContextOptionsBuilder<AeroLinkDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        var (legacy, typed) = FullRegistrations();
        var authority = new SoftwareProcedureExecutionCutoverAuthority(db, legacy, typed,
            allowSqliteExecution: true);
        var keys = Enumerable.Range(0, 25)
            .Select(_ => $"{Guid.NewGuid():N}"[..2] + "/" + Guid.NewGuid().ToString("N")
                + "-" + Guid.NewGuid().ToString("N") + ".bin")
            .ToArray();
        var operationId = Guid.NewGuid();
        await authority.RecordCleanupFailureEvidenceAsync(operationId, keys, default);

        var rows = await db.RollbackCleanupFailureEvidences.AsNoTracking()
            .OrderBy(x => x.Sequence).ToListAsync();
        Assert.Equal(3, rows.Count);
        Assert.Equal([0, 1, 2], rows.Select(x => x.Sequence).ToArray());
        Assert.All(rows, row => Assert.True(row.Content.Length <= 1500));
        Assert.All(rows, row => Assert.Equal(25, row.TotalKeys));
        Assert.Equal(25, rows.Sum(row => row.EntryCount));
        var recovered = rows.SelectMany(row => row.Content.Split(';')).ToList();
        Assert.Equal(25, recovered.Count);
        Assert.Equal(25, recovered.Distinct().Count());
        var expectedKeys = keys.OrderBy(x => x, StringComparer.Ordinal).ToArray();
        var sortedRecovered = recovered.OrderBy(x => x, StringComparer.Ordinal).ToArray();
        Assert.Equal(expectedKeys, sortedRecovered);
        var expectedHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            Encoding.UTF8.GetBytes(string.Join(";", expectedKeys)))).ToLowerInvariant();
        Assert.All(rows, row => Assert.Equal(expectedHash, row.CanonicalAggregateHash));
        var summary = await db.SecurityAuditEvents.AsNoTracking().SingleAsync(x =>
            x.EventType == SoftwareProcedureExecutionCutoverAuthority.MigrationMarker
                + ".OrphanedRenditionCleanupFailed");
        Assert.Contains("\"keys\":25", summary.Detail);
        Assert.Contains("\"chunks\":3", summary.Detail);
        Assert.Contains(expectedHash, summary.Detail);
        Assert.True(summary.Detail.Length < 4000);

        // Retry of the same operation adds no rows and no summary event.
        await authority.RecordCleanupFailureEvidenceAsync(operationId, keys, default);
        Assert.Equal(3, await db.RollbackCleanupFailureEvidences.AsNoTracking().CountAsync());
        Assert.Equal(1, await db.SecurityAuditEvents.AsNoTracking().CountAsync(x =>
            x.EventType == SoftwareProcedureExecutionCutoverAuthority.MigrationMarker
                + ".OrphanedRenditionCleanupFailed"));

        // Application immutability protects the committed chunks.
        db.ChangeTracker.Clear();
        var row = await db.RollbackCleanupFailureEvidences.SingleAsync(x => x.Sequence == 0);
        SetPrivate(row, nameof(RollbackCleanupFailureEvidence.EntryCount), 1);
        await Assert.ThrowsAsync<DomainException>(() => db.SaveChangesAsync());
        db.ChangeTracker.Clear();
        row = await db.RollbackCleanupFailureEvidences.SingleAsync(x => x.Sequence == 0);
        db.RollbackCleanupFailureEvidences.Remove(row);
        await Assert.ThrowsAsync<DomainException>(() => db.SaveChangesAsync());
        Assert.Equal(3, await db.RollbackCleanupFailureEvidences.AsNoTracking().CountAsync());
    }

    private sealed class ClaimSaveFailingInterceptor : SaveChangesInterceptor
    {
        public override InterceptionResult<int> SavingChanges(DbContextEventData eventData,
            InterceptionResult<int> result)
        {
            if (eventData.Context?.ChangeTracker.Entries<GovernedMigrationCompletion>().Any() == true)
                throw new DbUpdateException("Forced non-unique persistence failure.", (Exception?)null);
            return base.SavingChanges(eventData, result);
        }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (eventData.Context?.ChangeTracker.Entries<GovernedMigrationCompletion>().Any() == true)
                throw new DbUpdateException("Forced non-unique persistence failure.", (Exception?)null);
            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }

    private sealed class RenditionSaveFailingInterceptor : SaveChangesInterceptor
    {
        public override InterceptionResult<int> SavingChanges(DbContextEventData eventData,
            InterceptionResult<int> result)
        {
            if (eventData.Context?.ChangeTracker.Entries<ControlledDocumentArtifact>()
                .Any(entry => entry.State == EntityState.Modified) == true)
                throw new InvalidOperationException("Forced rollback after new rendition bytes were stored.");
            return base.SavingChanges(eventData, result);
        }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (eventData.Context?.ChangeTracker.Entries<ControlledDocumentArtifact>()
                .Any(entry => entry.State == EntityState.Modified) == true)
                throw new InvalidOperationException("Forced rollback after new rendition bytes were stored.");
            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }
}
