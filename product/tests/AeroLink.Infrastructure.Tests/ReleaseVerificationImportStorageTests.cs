using System.Data.Common;
using System.Text.Json;
using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Hierarchy;
using AeroLink.Domain.Programs;
using AeroLink.Domain.Releases;
using AeroLink.Domain.Requirements;
using AeroLink.Domain.Verification;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace AeroLink.Infrastructure.Tests;

[Collection(ShowcaseCollection.Name)]
public sealed class ReleaseVerificationImportStorageTests(ShowcaseDatabaseFixture showcaseFixture)
{
    [Fact]
    public async Task Staged_import_is_cleaned_after_known_failure_between_save_and_commit()
    {
        using var showcase = showcaseFixture.Create();
        var evidenceRoot = Path.Combine(Path.GetTempPath(), $"aerolink-import-cleanup-{Guid.NewGuid():N}");
        var now = DateTimeOffset.UtcNow;
        try
        {
            await using var db = showcase.Context();
            var summary = showcaseFixture.Summary;
            var campaign = await db.ReleaseCampaigns.Include(x => x.Events)
                .SingleAsync(x => x.ProjectId == summary.ProjectId && x.ReleaseId == summary.ActiveReleaseId);
            var baseline = await db.CandidateBaselines.SingleAsync(x => x.Id == campaign.BaselineId);
            var sourceRequirement = await (from member in db.BaselineRequirements
                join artifact in db.Requirements on member.ArtifactId equals artifact.Id
                where member.BaselineId == summary.ReleasedBaselineId && artifact.Level == RequirementLevel.LowLevel
                select new { member.ArtifactId, member.RevisionId }).FirstAsync();
            db.BaselineRequirements.Add(new BaselineRequirementSelection(baseline.Id, sourceRequirement.ArtifactId,
                sourceRequirement.RevisionId));
            var requirementRevisionId = sourceRequirement.RevisionId;
            var procedure = new TestProcedure(summary.ProjectId, $"LLRTC-{Random.Shared.Next(0, 1_000_000):D6}",
                "Verification import cleanup qualification", "test.lead", now, TestProcedureLevel.LowLevel);
            var procedureRevision = new TestProcedureRevision(procedure.Id, 0, "Objective", "Preconditions",
                "Steps", "Expected result", TestProcedureState.Approved, "test.lead", now);
            db.AddRange(procedure, procedureRevision, new TestRequirementCoverage(procedureRevision.Id, requirementRevisionId));
            var build = new SoftwareBuild(summary.ProjectId, summary.ActiveReleaseId, baseline.Id,
                $"IMPORT-{Guid.NewGuid():N}"[..15], "Import cleanup qualification build", "test.lead", now);
            campaign.SelectVerificationBuild(build.Id, "test.lead", now);
            db.SoftwareBuilds.Add(build);
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            var store = new EvidenceFileStore(evidenceRoot);
            var service = new ReleaseExecutionService(db, store);
            var template = JsonSerializer.Deserialize<List<VerificationManifestRow>>(
                await service.CreateVerificationTemplateAsync(campaign.Id, default))!;
            Assert.NotEmpty(template);
            var completed = template.Select(x => x with
            {
                Outcome = "Pass",
                ExecutedAt = now,
                ExecutedBy = "test.team",
                Configuration = build.BuildNumber,
                Determination = "The exact build result was reviewed for cleanup qualification."
            }).ToList();
            var executionCount = await db.TestExecutions.CountAsync();
            var evidenceCount = await db.EvidenceRecords.CountAsync();

            await using var stagedInput = new MemoryStream("known rollback evidence"u8.ToArray());
            var staged = await service.StageVerificationEvidenceAsync(stagedInput, "rollback.zip", "application/zip", default);
            Assert.Single(store.EnumerateStagedKeys());

            await using var scope = await ProjectControlledWriteScope.AcquireAsync(db, summary.ProjectId);
            await using var manifest = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(completed));
            await service.ImportVerificationAsync(campaign.Id, manifest, staged, "test.lead", now, default, scope);
            Assert.True(store.Exists(staged.StorageKey));
            Assert.Empty(store.EnumerateStagedKeys());

            // This models a deterministic failure after SaveChanges but before the caller sends COMMIT. The caller
            // can establish rollback, so it may remove the promoted object and its staged alias safely.
            try { throw new InvalidOperationException("Injected failure before commit."); }
            catch (InvalidOperationException)
            {
                await scope.RollbackAsync();
                service.DeleteStagedEvidence(staged);
            }

            await using var verify = showcase.Context();
            Assert.Equal(executionCount, await verify.TestExecutions.CountAsync());
            Assert.Equal(evidenceCount, await verify.EvidenceRecords.CountAsync());
            Assert.False(store.Exists(staged.StorageKey));
            Assert.Empty(store.EnumerateStagedKeys());
            Assert.Empty(Directory.EnumerateFiles(evidenceRoot, "*", SearchOption.AllDirectories));
        }
        finally
        {
            if (Directory.Exists(evidenceRoot)) Directory.Delete(evidenceRoot, true);
        }
    }

    [Fact]
    public async Task Staged_import_is_retained_when_commit_attempt_fails()
    {
        using var showcase = showcaseFixture.Create();
        var evidenceRoot = Path.Combine(Path.GetTempPath(), $"aerolink-import-ambiguous-{Guid.NewGuid():N}");
        var now = DateTimeOffset.UtcNow;
        try
        {
            var campaignId = await SeedImportScenarioAsync(showcase, showcaseFixture.Summary, now);
            var options = new DbContextOptionsBuilder<AeroLinkDbContext>()
                .UseSqlite($"Data Source={showcase.Path};Pooling=False")
                .AddInterceptors(new CommitFailureInterceptor()).Options;
            await using var db = new AeroLinkDbContext(options);
            var campaign = await db.ReleaseCampaigns.AsNoTracking().SingleAsync(x => x.Id == campaignId);
            var store = new EvidenceFileStore(evidenceRoot);
            var service = new ReleaseExecutionService(db, store);
            var template = JsonSerializer.Deserialize<List<VerificationManifestRow>>(
                await service.CreateVerificationTemplateAsync(campaign.Id, default))!;
            var completed = template.Select(x => x with
            {
                Outcome = "Pass",
                ExecutedAt = now,
                ExecutedBy = "test.team",
                Configuration = "ambiguous-commit",
                Determination = "Retain the promoted object when commit outcome is unknown."
            }).ToList();
            await using var stagedInput = new MemoryStream("ambiguous commit evidence"u8.ToArray());
            var staged = await service.StageVerificationEvidenceAsync(stagedInput, "ambiguous.zip", "application/zip", default);
            await using var scope = await ProjectControlledWriteScope.AcquireAsync(db, campaign.ProjectId);
            await using var manifest = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(completed));
            await service.ImportVerificationAsync(campaign.Id, manifest, staged, "test.lead", now, default, scope);
            await Assert.ThrowsAsync<InvalidOperationException>(() => scope.CommitAsync());
            await scope.DisposeAsync();

            Assert.True(store.Exists(staged.StorageKey));
            Assert.Empty(store.EnumerateStagedKeys());
        }
        finally
        {
            if (Directory.Exists(evidenceRoot)) Directory.Delete(evidenceRoot, true);
        }
    }

    private static async Task<Guid> SeedImportScenarioAsync(ShowcaseDatabase showcase, FmsShowcaseSummary summary,
        DateTimeOffset now)
    {
        await using var db = showcase.Context();
        var campaign = await db.ReleaseCampaigns.Include(x => x.Events)
            .SingleAsync(x => x.ProjectId == summary.ProjectId && x.ReleaseId == summary.ActiveReleaseId);
        var baseline = await db.CandidateBaselines.SingleAsync(x => x.Id == campaign.BaselineId);
        var sourceRequirement = await (from member in db.BaselineRequirements
            join artifact in db.Requirements on member.ArtifactId equals artifact.Id
            where member.BaselineId == summary.ReleasedBaselineId && artifact.Level == RequirementLevel.LowLevel
            select new { member.ArtifactId, member.RevisionId }).FirstAsync();
        db.BaselineRequirements.Add(new BaselineRequirementSelection(baseline.Id, sourceRequirement.ArtifactId,
            sourceRequirement.RevisionId));
        var procedure = new TestProcedure(summary.ProjectId, $"LLRTC-{Random.Shared.Next(0, 1_000_000):D6}",
            "Verification import cleanup qualification", "test.lead", now, TestProcedureLevel.LowLevel);
        var procedureRevision = new TestProcedureRevision(procedure.Id, 0, "Objective", "Preconditions",
            "Steps", "Expected result", TestProcedureState.Approved, "test.lead", now);
        db.AddRange(procedure, procedureRevision, new TestRequirementCoverage(procedureRevision.Id,
            sourceRequirement.RevisionId));
        var build = new SoftwareBuild(summary.ProjectId, summary.ActiveReleaseId, baseline.Id,
            $"IMPORT-{Guid.NewGuid():N}"[..15], "Import cleanup qualification build", "test.lead", now);
        campaign.SelectVerificationBuild(build.Id, "test.lead", now);
        db.SoftwareBuilds.Add(build);
        await db.SaveChangesAsync();
        return campaign.Id;
    }

    private sealed class CommitFailureInterceptor : DbTransactionInterceptor
    {
        public override ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction,
            TransactionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Injected commit failure; outcome is deliberately ambiguous.");
    }
}
