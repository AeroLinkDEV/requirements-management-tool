using System.IO.Compression;
using System.Text;
using AeroLink.Domain.Requirements;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AeroLink.Infrastructure.Tests;

public sealed class ProductLinePublicationTests
{
    [Fact]
    public async Task Frozen_variants_project_exact_reuse_decisions_and_render_reproducible_rich_publications()
    {
        var path=Path.Combine(Path.GetTempPath(),$"aerolink-product-line-{Guid.NewGuid():N}.db");
        var options=new DbContextOptionsBuilder<AeroLinkDbContext>().UseSqlite($"Data Source={path};Pooling=False").Options;
        try
        {
            await using var db=new AeroLinkDbContext(options);await db.Database.EnsureCreatedAsync();var summary=await new FmsShowcaseSeeder(db).EnsureSeededAsync();db.ChangeTracker.Clear();
            var variants=await db.ProductVariants.AsNoTracking().Where(x=>x.ProjectId==summary.ProjectId).OrderBy(x=>x.VariantKey).ToListAsync();
            var released=variants.Single(x=>x.VariantKey=="FMS-1.5");var active=variants.Single(x=>x.VariantKey=="FMS-1.6");
            var releasedBaseline=await db.ProductVariantBaselines.AsNoTracking().Where(x=>x.VariantId==released.Id).OrderByDescending(x=>x.Revision).FirstAsync();
            var activeBaseline=await db.ProductVariantBaselines.AsNoTracking().Where(x=>x.VariantId==active.Id).OrderByDescending(x=>x.Revision).FirstAsync();
            var projector=new VariantConfigurationProjectionService(db);var releasedProjection=await projector.ProjectAsync(releasedBaseline.Id,default);var activeProjection=await projector.ProjectAsync(activeBaseline.Id,default);
            Assert.NotNull(releasedProjection);Assert.NotNull(activeProjection);Assert.Contains("2 seconds",Assert.Single(releasedProjection!.Requirements).Statement);Assert.Contains("1 second",Assert.Single(activeProjection!.Requirements).Statement);
            var reuse=await db.VariantLibraryReuses.AsNoTracking().OrderBy(x=>x.VariantId).ToListAsync();Assert.Contains(reuse,x=>x.VariantId==released.Id&&x.SynchronizationState==ReuseSynchronizationState.Deferred);Assert.Contains(reuse,x=>x.VariantId==active.Id&&x.SynchronizationState==ReuseSynchronizationState.Current&&x.SelectedRevisionId==x.LatestUpstreamRevisionId);Assert.Equal(2,await db.LibraryPropagationDecisions.CountAsync());
            var template=await db.DocumentTemplateRevisions.AsNoTracking().SingleAsync();var generator=new VariantPublicationGenerator(db,projector);
            var docx=await generator.GenerateAsync(activeBaseline.Id,template.Id,releasedBaseline.Id,"docx",default);await Task.Delay(2100);var repeated=await generator.GenerateAsync(activeBaseline.Id,template.Id,releasedBaseline.Id,"docx",default);var pdf=await generator.GenerateAsync(activeBaseline.Id,template.Id,releasedBaseline.Id,"pdf",default);
            Assert.NotNull(docx);Assert.NotNull(repeated);Assert.NotNull(pdf);Assert.Equal(docx!.Output.Content,repeated!.Output.Content);Assert.Equal(docx.SourceManifestHash,repeated.SourceManifestHash);Assert.NotEqual(releasedProjection.ManifestHash,activeProjection.ManifestHash);
            using(var archive=new ZipArchive(new MemoryStream(docx.Output.Content),ZipArchiveMode.Read)){Assert.NotNull(archive.GetEntry("word/media/image1.jpg"));var xml=await ReadAsync(archive,"word/document.xml");Assert.Contains("<w:tbl>",xml);Assert.Contains("Controlled symbol",xml);Assert.Contains("Navigation integrity allocation",xml);Assert.Contains("Exact Configuration Redline",xml);Assert.Contains("<w:drawing>",xml);}
            var pdfText=Encoding.ASCII.GetString(pdf!.Output.Content);Assert.Contains("/Subtype /Image",pdfText);Assert.Contains("/Im1 Do",pdfText);Assert.Contains("Exact Configuration Redline".Replace(" ",""),pdfText.Replace(" ",""),StringComparison.OrdinalIgnoreCase);

            var editable=await db.DocumentTemplates.SingleAsync();editable.BeginSuccessorRevision("cm.fms",template.ApprovedAt.AddDays(1));editable.UpdateDraft("AeroLink configured SYSRD - refreshed","{\"subtitle\":\"Updated organization format\",\"titlePrefix\":\"Configured System Requirements\"}","cm.fms",template.ApprovedAt.AddDays(1));var revision=editable.Approve("cm.fms",template.ApprovedAt.AddDays(1));var successor=new DocumentTemplateRevision(editable.Id,revision,"SYSRD","AeroLink Flight Systems",editable.Body,VariantConfigurationProjectionService.Hash(editable.Body),"cm.fms",template.ApprovedAt.AddDays(1));db.DocumentTemplateRevisions.Add(successor);await db.SaveChangesAsync();
            var later=await generator.GenerateAsync(activeBaseline.Id,successor.Id,releasedBaseline.Id,"docx",default);Assert.NotNull(later);Assert.NotEqual(docx.TemplateManifestHash,later!.TemplateManifestHash);Assert.NotEqual(docx.Output.Content,later.Output.Content);Assert.Equal(docx.Output.Content,repeated.Output.Content);
        }
        finally{File.Delete(path);}
    }

    private static async Task<string> ReadAsync(ZipArchive archive,string name){await using var stream=archive.GetEntry(name)!.Open();using var reader=new StreamReader(stream);return await reader.ReadToEndAsync();}
}
