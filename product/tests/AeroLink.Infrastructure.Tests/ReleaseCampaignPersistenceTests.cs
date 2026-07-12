using System.IO.Compression;
using AeroLink.Domain.Releases;
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
            await using var db = new AeroLinkDbContext(options); await db.Database.EnsureCreatedAsync(); var summary = await new FmsShowcaseSeeder(db).EnsureSeededAsync();
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
}
