using System.Security.Cryptography;
using System.Text.Json;
using System.Data.Common;
using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Releases;
using AeroLink.Domain.Hierarchy;
using AeroLink.Domain.Requirements;
using AeroLink.Domain.Traceability;
using AeroLink.Domain.Verification;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace AeroLink.Infrastructure.Tests;

[Collection(ShowcaseCollection.Name)]
public sealed class FmsShowcaseActiveBuildVerificationTests(ShowcaseDatabaseFixture showcase)
{
    [Theory]
    [InlineData(false, true, false, false, null)]
    [InlineData(true, true, false, false, null)]
    [InlineData(false, false, false, false, null)]
    [InlineData(false, true, true, false, null)]
    [InlineData(true, true, true, false, null)]
    [InlineData(false, true, false, true, null)]
    [InlineData(false, true, false, false, "save")]
    [InlineData(false, true, false, false, "cancel")]
    [InlineData(false, true, false, false, "commit")]
    [InlineData(false, true, false, false, "promote")]
    public async Task Materialized_enrichment_preserves_existing_results_and_names_the_exact_waiting_cases(bool existingFailure, bool configureStore, bool sharedProcedure, bool rejectWorkflow, string? failureMode)
    {
        using var database = showcase.Create();
        var failure = new EvidenceSaveFailure();
        await using var db = new AeroLinkDbContext(new DbContextOptionsBuilder<AeroLinkDbContext>(database.Options)
            .AddInterceptors(failure, new EvidenceCommitFailure(failure)).Options);
        var configuration = await db.ProjectLadderConfigurations.AsNoTracking().Include(x => x.Steps)
            .Include(x => x.AllowedUpstream).SingleAsync(x => x.ProjectId == showcase.Summary.ProjectId);
        var resolved = ProjectLadderResolver.Resolve(configuration, LegacyLadderPolicy.Instance);
        var policy = new ResolvedProjectLadderPolicy(resolved with
        {
            Steps = resolved.Steps.Select(x => x.Level == RequirementLevel.HighLevel
                ? x with { EnabledArtifactKinds = [VerificationArtifactKind.Case, VerificationArtifactKind.Procedure] } : x).ToArray()
        }, LegacyLadderPolicy.Instance);
        var baseline = await db.CandidateBaselines.SingleAsync(x => x.ReleaseId == showcase.Summary.ActiveReleaseId);
        Assert.False(await FmsShowcaseSeeder.ActiveBuildVerificationMustResumeAsync(db, showcase.Summary.ProgramId));
        var now = DateTimeOffset.UtcNow;
        db.Entry(baseline).Property(x => x.RequirementsMaterializedAt).CurrentValue = now;
        db.Entry(baseline).Property(x => x.TestProceduresMaterializedAt).CurrentValue = now;
        var sources = await (from member in db.BaselineTestProcedures
            join artifact in db.TestProcedures on member.ProcedureId equals artifact.Id
            where member.BaselineId == showcase.Summary.ReleasedBaselineId && artifact.Level == TestProcedureLevel.HighLevel
            orderby artifact.BaseNumber select member).Take(4).ToListAsync();
        var procedures = new List<TestProcedureRevision>();
        var requirementIds = new HashSet<Guid>();
        foreach (var source in sources)
        {
            var actor = VerificationArtifactProfileSchema.GovernedMigrationActor;
            var artifact = new TestProcedure(showcase.Summary.ProjectId, $"HLRTP-{990101 + procedures.Count:D6}",
                "Exact active fixture", actor, now, TestProcedureLevel.HighLevel, policy,
                VerificationArtifactKind.Procedure, VerificationProcedureParentKind.Allocated);
            var revision = new TestProcedureRevision(artifact.Id, 0, "Objective", "Setup", "Steps", "Expected",
                TestProcedureState.Approved, actor, now, effectiveBaselineId: showcase.Summary.ReleasedBaselineId,
                environmentSetup: "Setup", testData: "Data", orderedSteps: "Steps", expectedObservations: "Expected",
                cleanup: "Cleanup", toolingAutomation: "Tooling", parentKind: VerificationProcedureParentKind.Allocated);
            db.AddRange(artifact, revision, new TestCaseProcedureLink(source.RevisionId, revision.Id),
                new TestProcedureMigrationSource(showcase.Summary.ProjectId, source.RevisionId, artifact.Id, revision.Id),
                new BaselineTestProcedureSelection(showcase.Summary.ReleasedBaselineId, artifact.Id, revision.Id),
                new BaselineTestProcedureSelection(baseline.Id, artifact.Id, revision.Id));
            procedures.Add(revision);
            var requirement = await (from coverage in db.TestCoverage
                join member in db.BaselineRequirements on coverage.RequirementRevisionId equals member.RevisionId
                where coverage.ProcedureRevisionId == source.RevisionId && member.BaselineId == showcase.Summary.ReleasedBaselineId
                select member).FirstAsync();
            if (requirementIds.Add(requirement.RevisionId))
                db.BaselineRequirements.Add(new BaselineRequirementSelection(baseline.Id, requirement.ArtifactId, requirement.RevisionId));
        }
        if (sharedProcedure)
            db.TestCaseProcedureLinks.Add(new TestCaseProcedureLink(sources[3].RevisionId, procedures[0].Id));
        var trace = await db.RequirementTraces.FirstAsync(x => requirementIds.Contains(x.SourceRevisionId));
        var parent = await db.BaselineRequirements.SingleAsync(x => x.BaselineId == showcase.Summary.ReleasedBaselineId
            && x.RevisionId == trace.TargetRevisionId);
        db.BaselineRequirements.Add(new BaselineRequirementSelection(baseline.Id, parent.ArtifactId, parent.RevisionId));
        var original = new TestExecution(showcase.Summary.ProjectId, procedures[0].Id, null, null,
            existingFailure ? TestOutcome.Fail : TestOutcome.Pass, "test.engineer", "Existing operator fixture",
            "Existing determination must remain byte-for-byte intact.", "existing-evidence", now.AddMinutes(-1), now.AddMinutes(-1), showcase.Summary.ActiveReleaseId);
        db.TestExecutions.Add(original);
        await db.SaveChangesAsync();
        Assert.True(await FmsShowcaseSeeder.ActiveBuildVerificationMustResumeAsync(db, showcase.Summary.ProgramId));
        var originalJson = JsonSerializer.Serialize(original);
        var root = Path.Combine(Path.GetTempPath(), "aerolink-913-evidence-" + Guid.NewGuid().ToString("N"));
        var store = new EvidenceFileStore(root);
        failure.Root = root;
        failure.Mode = failureMode;
        try
        {
            var seeder = new FmsShowcaseSeeder(db, new FixedProjectLadderPolicyResolver(policy), configureStore ? store : null);
            if (failureMode is "save" or "cancel" or "commit")
            {
                var executionCount = await db.TestExecutions.CountAsync();
                var evidenceCount = await db.EvidenceRecords.CountAsync();
                for (var attempt = 0; attempt < 2; attempt++)
                {
                    using var cancellation = new CancellationTokenSource();
                    failure.Cancellation = cancellation;
                    if (failureMode == "cancel")
                        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => seeder.UpgradeAsync(showcase.Summary.ProgramId, cancellation.Token));
                    else
                        await Assert.ThrowsAsync<InvalidOperationException>(() => seeder.UpgradeAsync(showcase.Summary.ProgramId, cancellation.Token));
                    db.ChangeTracker.Clear();
                    Assert.Equal(executionCount, await db.TestExecutions.CountAsync());
                    Assert.Equal(evidenceCount, await db.EvidenceRecords.CountAsync());
                    Assert.Empty(Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories));
                }
                Assert.Equal(2, failure.Interruptions);
                return;
            }
            if (failureMode == "promote")
            {
                await Assert.ThrowsAnyAsync<IOException>(() => seeder.UpgradeAsync(showcase.Summary.ProgramId));
                db.ChangeTracker.Clear();
                Assert.Single(store.EnumerateStagedKeys());
                Assert.Single(await db.ShowcaseUpgradeSteps.Where(x => x.ProgramId == showcase.Summary.ProgramId
                    && x.StepKey.StartsWith("active-verification-913/storage/")).ToListAsync());
                var pending = await seeder.ActiveTraceInventoryAsync(showcase.Summary.ProgramId);
                Assert.False(pending.Holds);
                Assert.Contains(pending.Problems, x => x.Contains("Owned synthetic evidence") && x.Contains("storage recovery"));
                File.Delete(failure.BlockedDirectory!);
                failure.Mode = null;
                // The database is already committed. The supported retry promotes its durable staged
                // object and must neither duplicate results nor reapply a showcase step.
                Assert.Empty(await seeder.UpgradeAsync(showcase.Summary.ProgramId));
                Assert.Empty(store.EnumerateStagedKeys());
            }
            if (rejectWorkflow)
            {
                // Missing completion with surviving owned scenarios must fail closed. In the old
                // order this rejection happened after evidence promotion and left an orphan file.
                db.ShowcaseUpgradeSteps.Remove(await db.ShowcaseUpgradeSteps.SingleAsync(x =>
                    x.ProgramId == showcase.Summary.ProgramId && x.StepKey == "workflow-holder-scenarios"));
                await db.SaveChangesAsync();
                var executionCount = await db.TestExecutions.CountAsync();
                var evidenceCount = await db.EvidenceRecords.CountAsync();
                for (var attempt = 0; attempt < 2; attempt++)
                {
                    await Assert.ThrowsAsync<InvalidOperationException>(() => seeder.UpgradeAsync(showcase.Summary.ProgramId));
                    db.ChangeTracker.Clear();
                    Assert.Equal(executionCount, await db.TestExecutions.CountAsync());
                    Assert.Equal(evidenceCount, await db.EvidenceRecords.CountAsync());
                    Assert.Empty(Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories));
                }
                return;
            }
            if (!configureStore)
            {
                var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => seeder.UpgradeAsync(showcase.Summary.ProgramId));
                Assert.Contains("explicitly configured evidence store", exception.Message);
                Assert.Equal(originalJson, JsonSerializer.Serialize(await db.TestExecutions.AsNoTracking().SingleAsync(x => x.Id == original.Id)));
                Assert.Empty(Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories));
                return;
            }
            await seeder.UpgradeAsync(showcase.Summary.ProgramId);
            Assert.False(await FmsShowcaseSeeder.ActiveBuildVerificationMustResumeAsync(db, showcase.Summary.ProgramId));
            Assert.Equal(originalJson, JsonSerializer.Serialize(await db.TestExecutions.AsNoTracking().SingleAsync(x => x.Id == original.Id)));
            var created = await db.TestExecutions.AsNoTracking().Where(x => x.ReleaseId == showcase.Summary.ActiveReleaseId && x.Id != original.Id).ToListAsync();
            Assert.Equal(sharedProcedure ? 3 : 2, created.Count);
            Assert.All(created, x => Assert.Contains("Synthetic demonstration", x.Determination));
            var evidenceIds = await db.TestExecutionEvidence.Where(x => created.Select(e => e.Id).Contains(x.TestExecutionId)).Select(x => x.EvidenceId).Distinct().ToListAsync();
            var evidence = Assert.Single(await db.EvidenceRecords.Where(x => evidenceIds.Contains(x.Id)).ToListAsync());
            var bytes = await File.ReadAllBytesAsync(Path.Combine(root, evidence.StorageKey));
            Assert.Equal(evidence.Sha256, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
            Assert.True(JsonDocument.Parse(bytes).RootElement.GetProperty("IsDemonstration").GetBoolean());
            var selected = await db.BuildTestSetEntries.AsNoTracking()
                .Where(x => procedures.Select(p => p.Id).Contains(x.ProcedureRevisionId)).ToListAsync();
            Assert.Equal(4, selected.Count);
            Assert.All(selected, x => Assert.Equal("program.manager", x.AddedBy));
            Assert.Empty(await seeder.UpgradeAsync(showcase.Summary.ProgramId));
            var inventory = await seeder.ActiveTraceInventoryAsync(showcase.Summary.ProgramId);
            var population = Assert.Single(inventory.Populations, x => x.Family == "Exact software Case-to-Procedure obligations");
            Assert.Equal(4, population.Total);
            Assert.Equal(sharedProcedure ? (existingFailure ? 2 : 0) : (existingFailure ? 2 : 1), population.Gaps.Count);
            Assert.Equal(sharedProcedure ? 0 : 1, population.Gaps.Count(x => x.NamedNegative));
            Assert.Equal(existingFailure ? (sharedProcedure ? 2 : 1) : 0, population.Gaps.Count(x => !x.NamedNegative));
            if (existingFailure) Assert.Contains(inventory.Problems, x => x.Contains("Case-to-Procedure") && x.Contains("HLRTC-000001"));
            var lifecycle = ExactLinkSuspectLifecycle.Raise(showcase.Summary.ProjectId, ExactLinkKind.RequirementTrace,
                trace.Id, ExactLinkLifecycleCauseKind.InternalRequirementRevision, trace.TargetRevisionId, null,
                "systems.author", "Deliberate open upstream-link regression.", DateTimeOffset.UtcNow);
            db.ExactLinkSuspectLifecycles.Add(lifecycle);
            await db.SaveChangesAsync();
            var suspectInventory = await seeder.ActiveTraceInventoryAsync(showcase.Summary.ProgramId);
            Assert.Contains(suspectInventory.Populations.Single(x => x.Family == "Exact active-baseline requirements").Gaps,
                x => x.Id == trace.SourceRevisionId && x.Warnings.Contains("SuspectUpstream") && !x.NamedNegative);
            lifecycle.RecordResolution(ExactLinkResolutionOutcome.NoDownstreamChangeRequired,
                "systems.author", "Close the deliberate regression.", DateTimeOffset.UtcNow);
            db.ExactLinkSuspectEvents.Add(lifecycle.Events.Last());
            await db.SaveChangesAsync();
            Assert.DoesNotContain((await seeder.ActiveTraceInventoryAsync(showcase.Summary.ProgramId)).Populations
                .Single(x => x.Family == "Exact active-baseline requirements").Gaps,
                x => x.Warnings.Contains("SuspectUpstream"));
            var evidenceLink = await db.TestExecutionEvidence.SingleAsync(x => x.TestExecutionId == created[0].Id);
            db.TestExecutionEvidence.Remove(evidenceLink);
            await db.SaveChangesAsync();
            Assert.Contains((await seeder.ActiveTraceInventoryAsync(showcase.Summary.ProgramId)).Problems,
                x => x.Contains("Owned synthetic result or its evidence link"));
            db.TestExecutionEvidence.Add(evidenceLink);
            await db.SaveChangesAsync();

            var waitingSet = await db.BuildTestSets.Include(x => x.Entries)
                .SingleAsync(x => x.ReleaseId == showcase.Summary.ActiveReleaseId
                    && x.Discipline == TestChangeReviewDiscipline.HighLevelSoftware);
            Assert.True(waitingSet.Exclude(procedures[3].Id, DateTimeOffset.UtcNow));
            await db.SaveChangesAsync();
            Assert.Contains((await seeder.ActiveTraceInventoryAsync(showcase.Summary.ProgramId)).Problems,
                x => x.Contains("Unplanned") && x.Contains("HLRTC-000004"));
            waitingSet.Include("program.manager", procedures[3].Id, TestSelectionReason.Chosen,
                "Restore the fixture after the deliberate selection-drift check.", DateTimeOffset.UtcNow);
            await db.SaveChangesAsync();
            if (!sharedProcedure)
            {
                var waitingMarkers = await db.ShowcaseUpgradeSteps.Where(x => x.ProgramId == showcase.Summary.ProgramId && x.StepKey.StartsWith("active-verification-913/waiting/")).ToListAsync();
                db.ShowcaseUpgradeSteps.RemoveRange(waitingMarkers);
                await db.SaveChangesAsync();
                Assert.Contains((await seeder.ActiveTraceInventoryAsync(showcase.Summary.ProgramId)).Problems,
                    x => x.Contains("Case-to-Procedure") && x.Contains("HLRTC-000004"));
                db.ShowcaseUpgradeSteps.AddRange(waitingMarkers);
                await db.SaveChangesAsync();
                foreach (var outcome in new[] { TestOutcome.Fail, TestOutcome.Blocked })
                {
                    var recordedAt = DateTimeOffset.UtcNow;
                    var operatorResult = new TestExecution(showcase.Summary.ProjectId, procedures[3].Id, null, null,
                        outcome, "test.engineer", "Operator result on a previously waiting Procedure",
                        "An actual unsuccessful result is not intentional waiting work.", "operator-evidence",
                        recordedAt, recordedAt, showcase.Summary.ActiveReleaseId);
                    db.TestExecutions.Add(operatorResult);
                    await db.SaveChangesAsync();
                    var failedInventory = await seeder.ActiveTraceInventoryAsync(showcase.Summary.ProgramId);
                    var gap = Assert.Single(failedInventory.Populations.Single(x => x.Family == "Exact software Case-to-Procedure obligations").Gaps,
                        x => x.Id == sources[3].RevisionId);
                    Assert.False(gap.NamedNegative);
                    Assert.Contains(failedInventory.Problems, x => x.Contains("Unplanned") && x.Contains("HLRTC-000004"));
                    Assert.Empty(await seeder.UpgradeAsync(showcase.Summary.ProgramId));
                    Assert.Equal(outcome, (await db.TestExecutions.AsNoTracking().SingleAsync(x => x.Id == operatorResult.Id)).Outcome);
                }
            }
        }
        finally
        {
            if (!Path.GetFullPath(root).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Unexpected temporary evidence root.");
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class EvidenceSaveFailure : SaveChangesInterceptor
    {
        public string? Mode { get; set; }
        public string Root { get; set; } = "";
        public string? BlockedDirectory { get; private set; }
        public CancellationTokenSource? Cancellation { get; set; }
        public int Interruptions { get; set; }
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            var evidence = eventData.Context!.ChangeTracker.Entries<EvidenceRecord>()
                .FirstOrDefault(x => x.State == EntityState.Added)?.Entity;
            if (Mode is null || evidence is null) return ValueTask.FromResult(result);
            var files = Directory.GetFiles(Root, "*", SearchOption.AllDirectories);
            Assert.NotEmpty(files);
            Assert.All(files, file => Assert.EndsWith(".stage", file));
            if (Mode == "promote")
            {
                BlockedDirectory = Path.Combine(Root, evidence.StorageKey.Split('/')[0]);
                File.WriteAllText(BlockedDirectory, "Owned fixture blocking only the final promotion directory.");
            }
            if (Mode == "cancel")
            {
                Interruptions++;
                Cancellation!.Cancel();
                cancellationToken.ThrowIfCancellationRequested();
            }
            if (Mode == "save") { Interruptions++; throw new InvalidOperationException("Injected evidence save failure."); }
            return ValueTask.FromResult(result);
        }
    }

    private sealed class EvidenceCommitFailure(EvidenceSaveFailure failure) : DbTransactionInterceptor
    {
        public override ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction,
            TransactionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
        {
            if (failure.Mode == "commit")
            {
                var files = Directory.GetFiles(failure.Root, "*", SearchOption.AllDirectories);
                Assert.NotEmpty(files);
                Assert.All(files, file => Assert.EndsWith(".stage", file));
                failure.Interruptions++;
                throw new InvalidOperationException("Injected database commit failure.");
            }
            return ValueTask.FromResult(result);
        }
    }
}
