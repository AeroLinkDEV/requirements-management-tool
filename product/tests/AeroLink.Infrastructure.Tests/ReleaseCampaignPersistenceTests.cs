using System.IO.Compression;
using System.Text.Json;
using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Programs;
using AeroLink.Domain.Releases;
using AeroLink.Domain.Verification;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AeroLink.Infrastructure.Tests;

public sealed class ReleaseCampaignPersistenceTests
{
    [Fact]
    public async Task Showcase_campaign_has_real_gates_impacts_outputs_and_checksummed_evidence()
    {
        var path = Path.Combine(Path.GetTempPath(), $"aerolink-campaign-{Guid.NewGuid():N}.db"); var evidenceRoot = Path.Combine(Path.GetTempPath(), $"aerolink-evidence-{Guid.NewGuid():N}");
        var options = new DbContextOptionsBuilder<AeroLinkDbContext>().UseSqlite($"Data Source={path};Pooling=False").Options;
        try
        {
            await using var db = new AeroLinkDbContext(options); await db.Database.EnsureCreatedAsync(); var summary = await new FmsShowcaseSeeder(db).EnsureSeededAsync(); db.ChangeTracker.Clear();
            var campaign = await db.ReleaseCampaigns.SingleAsync(x => x.ProjectId == summary.ProjectId); Assert.Equal(ReleaseCampaignState.Verification, campaign.State);
            Assert.Equal(32, await db.ImpactDispositions.CountAsync(x => x.CampaignId == campaign.Id)); Assert.Equal(8, await db.ImpactDispositions.CountAsync(x => x.CampaignId == campaign.Id && x.State == ImpactDispositionState.Addressed));
            var readiness = await new ReleaseReadinessService(db).CalculateAsync(campaign.Id, default); Assert.False(readiness.ReadyForRelease); Assert.Contains(readiness.Gates, x => x.Code == "change_control" && x.Completed == 2 && x.Total == 7);
            var documentId = await db.ControlledDocuments.Where(x => x.BaselineId == summary.ReleasedBaselineId).Select(x => x.Id).FirstAsync(); var generator = new ControlledOutputGenerator(db);
            var docx = await generator.GenerateAsync(documentId, "docx", default); var pdf = await generator.GenerateAsync(documentId, "pdf", default); Assert.NotNull(docx); Assert.NotNull(pdf); Assert.StartsWith("%PDF-1.4", System.Text.Encoding.ASCII.GetString(pdf!.Content, 0, 8));
            using (var archive = new ZipArchive(new MemoryStream(docx!.Content), ZipArchiveMode.Read)) Assert.NotNull(archive.GetEntry("word/document.xml"));
            var store = new EvidenceFileStore(evidenceRoot);
            var stored = await store.StoreAsync(new MemoryStream("evidence payload"u8.ToArray()), "run.json", "application/json", default); Assert.Equal(64, stored.Sha256.Length); await using var opened = store.OpenRead(stored.StorageKey); Assert.Equal(stored.Size, opened.Length);
        }
        finally { File.Delete(path); if (Directory.Exists(evidenceRoot)) Directory.Delete(evidenceRoot, true); }
    }

    [Fact]
    public async Task Release_execution_reconciles_versioned_links_and_imports_an_exact_build_manifest()
    {
        var path = Path.Combine(Path.GetTempPath(), $"aerolink-execution-{Guid.NewGuid():N}.db"); var evidenceRoot = Path.Combine(Path.GetTempPath(), $"aerolink-execution-evidence-{Guid.NewGuid():N}");
        var options = new DbContextOptionsBuilder<AeroLinkDbContext>().UseSqlite($"Data Source={path};Pooling=False").Options;
        try
        {
            await using var db = new AeroLinkDbContext(options); await db.Database.EnsureCreatedAsync(); var summary = await new FmsShowcaseSeeder(db).EnsureSeededAsync(); db.ChangeTracker.Clear();
            var campaign = await db.ReleaseCampaigns.Include(x => x.Events).SingleAsync(x => x.ProjectId == summary.ProjectId);
            var baseline = await db.CandidateBaselines.Include(x => x.Selections).Include(x => x.Events).SingleAsync(x => x.Id == campaign.BaselineId);
            var requests = await db.SystemChangeRequests.Include(x => x.RequirementChanges).Include(x => x.ReviewCycles).ThenInclude(x => x.Steps).Where(x => x.TargetReleaseId == campaign.ReleaseId).ToListAsync();
            var now = new DateTimeOffset(2025, 1, 10, 14, 0, 0, TimeSpan.Zero);
            foreach (var request in requests.Where(x => x.State != ScrState.Deferred && x.State != ScrState.SelectedForBaseline))
            {
                if (request.State == ScrState.Draft) { request.SubmitForReview(request.AuthorId, [new ApproverSelection("release.reviewer", "Release Reviewer")], now); await db.SaveChangesAsync(); }
                while (request.State == ScrState.InReview) { request.ApproveActiveStage(request.ActiveReviewCycle!.Steps.Single(x => x.State == ApprovalStepState.Active).ApproverId, now); await db.SaveChangesAsync(); }
                baseline.Select(request, "cm.test", now); await db.SaveChangesAsync();
            }
            baseline.Freeze("cm.test", now); await db.SaveChangesAsync();
            var materialized = await new RequirementBaselineMaterializer(db).MaterializeAsync(baseline.Id, "cm.test", now, default); Assert.Equal(1251, materialized.ActiveRequirementCount);
            var service = new ReleaseExecutionService(db, new EvidenceFileStore(evidenceRoot)); var reconciled = await service.ReconcileAsync(campaign.Id, "assurance.test", now, default);
            Assert.True(reconciled.TraceLinksCreated > 0); Assert.True(reconciled.CoverageLinksCreated > 0); Assert.Equal(1, reconciled.UncoveredRequirements);

            var introducedRevision = await (from member in db.BaselineRequirements where member.BaselineId == baseline.Id join artifact in db.Requirements on member.ArtifactId equals artifact.Id where artifact.BaseNumber == "SYSR-00000151" select member.RevisionId).SingleAsync();
            var procedure = new TestProcedure(summary.ProjectId, "SYSTP-00000076", "Verify round-robin waypoint sequencing", "test.author", now, TestProcedureLevel.System);
            var revision = new TestProcedureRevision(procedure.Id, 0, "Verify round-robin sequencing.", "Load the release candidate.", "Exercise every eligible sequencing transition.", "Every transition follows the approved round-robin requirement.", TestProcedureState.Approved, "test.author", now);
            db.AddRange(procedure, revision, new TestRequirementCoverage(revision.Id, introducedRevision));
            var build = new SoftwareBuild(summary.ProjectId, campaign.ReleaseId, baseline.Id, "FMS-1.6-RC1", "Release-candidate verification build.", "build.engineer", now); db.SoftwareBuilds.Add(build);
            campaign.SelectVerificationBuild(build.Id, "release.manager", now); await db.SaveChangesAsync();

            var template = JsonSerializer.Deserialize<List<VerificationManifestRow>>(await service.CreateVerificationTemplateAsync(campaign.Id, default))!; Assert.Equal(516, template.Count);
            var completed = template.Select(x => x with { Outcome = "Pass", ExecutedAt = now, ExecutedBy = "test.team", Configuration = build.BuildNumber, Determination = "Observed results satisfy the approved expected result." }).ToList();
            await using var manifest = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(completed)); await using var evidence = new MemoryStream("signed verification campaign evidence"u8.ToArray());
            var imported = await service.ImportVerificationAsync(campaign.Id, manifest, evidence, "FMS-1.6-RC1-verification.zip", "application/zip", "test.lead", now, default);
            Assert.Equal(516, imported.ExecutionsRecorded); Assert.Equal(516, imported.Passed); Assert.Equal(516, await db.TestExecutionEvidence.CountAsync(x => x.EvidenceId == imported.EvidenceId));
        }
        finally { File.Delete(path); if (Directory.Exists(evidenceRoot)) Directory.Delete(evidenceRoot, true); }
    }
}
