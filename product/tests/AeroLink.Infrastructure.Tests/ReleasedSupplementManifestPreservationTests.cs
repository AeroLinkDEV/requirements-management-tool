using System.Text.Json;
using AeroLink.Domain.Baselines;
using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Identity;
using AeroLink.Domain.Integrations;
using AeroLink.Domain.Programs;
using AeroLink.Domain.Releases;
using AeroLink.Domain.Requirements;
using AeroLink.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AeroLink.Infrastructure.Tests;

public sealed class ReleasedSupplementManifestPreservationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Dated_supplement_preserves_recomputed_recorded_manifest_and_original_release(bool noCodeV2)
    {
        var releasedAt = DateTimeOffset.Parse("2026-01-02T03:04:05Z");
        var supplementedAt = releasedAt.AddMonths(8);
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new AeroLinkDbContext(new DbContextOptionsBuilder<AeroLinkDbContext>()
            .UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        var program = new ProgramRecord("Synthetic preservation fixture", "FMSLIVE");
        var project = new ProjectRecord(program.Id, "FMS Product Development", "Flight Management System");
        var release = new SoftwareRelease(project.Id, "1.5", false);
        var baseline = new CandidateBaseline("SW-01.50", 0, project.Id, release.Id, null,
            "Synthetic released baseline", "tester", releasedAt);
        var predecessor = new CandidateBaseline("SW-01.40", 0, project.Id, release.Id, null,
            "Synthetic inherited source", "tester", releasedAt);
        var artifact = new RequirementArtifact(project.Id, "LLR-000001", RequirementLevel.LowLevel, releasedAt);
        var revision = RequirementRevision.FromAeroLinkBaseline(artifact.Id, 0,
            "Retain the validated state on rejected input.", "Preservation fixture", RequirementRevisionState.Active,
            predecessor.Id, baseline.Id, releasedAt, "LLR-000001.00");
        typeof(RequirementRevision).GetProperty(nameof(revision.ParentKind))!.SetValue(revision, RequirementParentKind.Derived);
        typeof(RequirementRevision).GetProperty(nameof(revision.DerivedRationale))!.SetValue(revision, "Isolated fixture.");
        var build = new SoftwareBuild(project.Id, release.Id, baseline.Id, "FMS-1.5.0",
            "Synthetic release", "tester", releasedAt);
        var campaign = new ReleaseCampaign(project.Id, release.Id, baseline.Id, "Synthetic review", "tester", releasedAt);
        campaign.SelectVerificationBuild(build.Id, "tester", releasedAt);
        var repository = new ProjectRepositoryConfiguration(project.Id, ProjectRepositorySetupMode.ConnectNow,
            "GitLab", "https://gitlab.example/synthetic/fms", "tester", releasedAt);
        repository.RecordVerification("tester", releasedAt, 17, "synthetic/fms");
        var legacy = new CodeTraceabilityRecord(project.Id, release.Id, artifact.Id, revision.Id,
            CodeTraceDisposition.NoCodeChangeRequired, "", "", "", "", "", null,
            "Existing released decision.", true, "tester", releasedAt);
        db.AddRange(program, project, release, baseline, predecessor, artifact, revision, build, campaign,
            repository, legacy, new BaselineRequirementSelection(baseline.Id, artifact.Id, revision.Id),
            new ShowcaseUpgradeStep(program.Id, "released-campaign", "Synthetic fixture marker.", releasedAt));
        if (noCodeV2)
        {
            var noCode = new CodeEvidenceDispositionSet(project.Id, release.Id, artifact.Id, revision.Id,
                CodeEvidenceDisposition.NoCodeChangeRequired, "Explicit source-less NoCode decision.",
                null, null, legacy.Id, "tester", releasedAt);
            db.AddRange(noCode, new CodeEvidenceCurrentSelector(project.Id, release.Id, artifact.Id, revision.Id,
                noCode.Id, "tester", releasedAt));
        }
        await db.SaveChangesAsync();
        baseline.FreezeForInception("tester", releasedAt);
        baseline.MarkRequirementsMaterialized("tester", new string('c', 64), 1, releasedAt);
        baseline.MarkReleased("tester", releasedAt);
        release.MarkReleased(releasedAt);
        build.MarkReleased(releasedAt);
        await db.SaveChangesAsync();

        var execution = new ReleaseExecutionService(db,
            new EvidenceFileStore(Path.Combine(Path.GetTempPath(), "aerolink-supplement-unused")));
        PreparedCodeReviewManifest prepared;
        await using (var scope = await ProjectControlledWriteScope.AcquireAsync(db, project.Id))
        {
            prepared = await execution.PrepareCodeReviewManifestAsync(campaign, scope, default);
            Assert.Equal(noCodeV2 ? 2 : 1, prepared.FormatVersion);
            Assert.Null(prepared.SourceSnapshotId);
            Assert.Null(prepared.SourceSelectionEventId);
            var previousEvents = campaign.Events.ToHashSet();
            campaign.StartVerification("tester", releasedAt);
            campaign.BeginReleaseReview("tester", [("synthetic.reviewer", "Synthetic reviewer")], prepared.Hash, releasedAt);
            execution.RecordCodeReviewManifest(campaign, prepared, scope, "tester", releasedAt);
            campaign.Approve("synthetic.reviewer", releasedAt);
            campaign.Release(build.Id, prepared.Hash, "tester", releasedAt);
            db.AddRange(campaign.Approvals);
            db.AddRange(campaign.Events.Where(item => !previousEvents.Contains(item)));
            db.ElectronicSignatures.Add(new ElectronicSignature(Guid.NewGuid(), "synthetic.reviewer",
                "Synthetic test reviewer", program.Id, "ReleaseCampaign", campaign.Id, "1", "Approve",
                "Automated isolated fixture signature; not a real human approval.", prepared.Hash,
                "127.0.0.1", releasedAt));
            await db.SaveChangesAsync();
            await scope.CommitAsync();
        }
        db.ChangeTracker.Clear();
        Assert.Equal(prepared.Hash, await execution.ComputeRecordedReviewManifestHashAsync(campaign.Id, default));
        var legacyHashBefore = await execution.ComputeReviewManifestHashAsync(campaign.Id, default);
        var historyBefore = await HistoricalRowsAsync(db);
        var evidenceBefore = JsonSerializer.Serialize(await CurrentCodeEvidenceProjection.ForReleaseAsync(db, project.Id, release.Id, default));
        var readinessBefore = JsonSerializer.Serialize(await new ReleaseReadinessService(db).CalculateAsync(campaign.Id, default));
        var options = new ProjectGitLabOptions
        {
            BaseUrl = "https://gitlab.example",
            ReleasedSyntheticSourceSupplementScope = new()
            {
                ProgramId = program.Id.ToString(), ProjectId = project.Id.ToString(), ReleaseId = release.Id.ToString(),
                BaselineId = baseline.Id.ToString(), CampaignId = campaign.Id.ToString(),
            },
        };
        var manifest = new ReleasedSyntheticSourceSupplementManifest(1, project.Id, release.Id, campaign.Id,
            baseline.Id, repository.Id, repository.Version, 0, options.BaseUrl, 17, "synthetic/fms", new string('a', 40),
            new string('a', 40), GitLabReferenceKind.Commit, Guid.NewGuid(), "DEC-131",
            "issue-1023-owner-approved-source-only", "Present-day synthetic supplement; original package unchanged.");
        var service = new ReleasedSyntheticSourceSupplementService(db,
            new GitLabMetadataReader(new HttpClient(new NoNetworkHandler()), Options.Create(options)), Options.Create(options));
        var observation = new GitLabCommitReference(17, manifest.RequestedReference, GitLabReferenceKind.Commit, manifest.CommitSha);
        await using (var scope = await ProjectControlledWriteScope.AcquireAsync(db, project.Id))
        {
            await service.ApplyAsync(scope, manifest,
                new(manifest.Digest(), false, null, observation, repository.Id, repository.Version),
                "supplement.operator", supplementedAt);
            await db.SaveChangesAsync();
            await scope.CommitAsync();
        }
        db.ChangeTracker.Clear();

        // Recompute through the recorded format, including the v2 NoCode/null-source regression case.
        Assert.Equal(prepared.Hash, await execution.ComputeRecordedReviewManifestHashAsync(campaign.Id, default));
        Assert.Equal(legacyHashBefore, await execution.ComputeReviewManifestHashAsync(campaign.Id, default));
        Assert.Equal(historyBefore, await HistoricalRowsAsync(db));
        Assert.Equal(evidenceBefore, JsonSerializer.Serialize(await CurrentCodeEvidenceProjection.ForReleaseAsync(db, project.Id, release.Id, default)));
        Assert.Equal(readinessBefore, JsonSerializer.Serialize(await new ReleaseReadinessService(db).CalculateAsync(campaign.Id, default)));
        Assert.Empty(await db.GitLabCurrentSourceSelections.ToListAsync());
        Assert.Empty(await db.GitLabSourceSelectionEvents.ToListAsync());
        Assert.Empty(await db.GitLabMergeRequestRelationships.ToListAsync());
        Assert.Empty(await db.GitLabFileRelationships.ToListAsync());
        var supplement = await db.ReleasedSyntheticSourceSupplements.SingleAsync();
        Assert.Equal(supplementedAt, supplement.RecordedAt);
        Assert.Equal("supplement.operator", supplement.RecordedBy);
        Assert.Equal(1, await db.GitLabSourceSnapshots.CountAsync());
    }

    private static async Task<string> HistoricalRowsAsync(AeroLinkDbContext db) => JsonSerializer.Serialize(new
    {
        Builds = await db.SoftwareBuilds.AsNoTracking().OrderBy(x => x.Id).ToListAsync(),
        EvidenceSets = await db.CodeEvidenceDispositionSets.AsNoTracking().OrderBy(x => x.Id).ToListAsync(),
        Contributions = await db.CodeEvidenceContributions.AsNoTracking().OrderBy(x => x.Id).ToListAsync(),
        Selectors = await db.CodeEvidenceCurrentSelectors.AsNoTracking().OrderBy(x => x.Id).ToListAsync(),
        Invalidations = await db.CodeEvidenceInvalidations.AsNoTracking().OrderBy(x => x.Id).ToListAsync(),
        Releases = await db.Releases.AsNoTracking().OrderBy(x => x.Id).ToListAsync(),
        Baselines = await db.CandidateBaselines.AsNoTracking().OrderBy(x => x.Id).ToListAsync(),
        Members = await db.BaselineRequirements.AsNoTracking().OrderBy(x => x.Id).ToListAsync(),
        Campaigns = await db.ReleaseCampaigns.AsNoTracking().Include(x => x.Approvals).Include(x => x.Events).OrderBy(x => x.Id).ToListAsync(),
        Signatures = await db.ElectronicSignatures.AsNoTracking().OrderBy(x => x.Id).ToListAsync(),
        LegacyCode = await db.CodeTraceabilityRecords.AsNoTracking().OrderBy(x => x.Id).ToListAsync(),
        Formats = await db.CodeReviewCycleManifestIdentities.AsNoTracking().OrderBy(x => x.Id).ToListAsync(),
    });

    private sealed class NoNetworkHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new InvalidOperationException("Local append must not perform network requests.");
    }
}
