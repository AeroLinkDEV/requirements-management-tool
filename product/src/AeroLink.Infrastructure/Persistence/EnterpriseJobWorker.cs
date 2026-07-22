using AeroLink.Domain.Requirements;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using System.Text;
using System.IO.Compression;

namespace AeroLink.Infrastructure.Persistence;

public sealed class EnterpriseJobWorker(IServiceScopeFactory scopes, ILogger<EnterpriseJobWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await ProcessNext(stoppingToken); }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested) { logger.LogError(ex, "Enterprise background job polling failed."); }
            await Task.Delay(2000, stoppingToken);
        }
    }

    private async Task ProcessNext(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        var job = (await db.EnterpriseOperationJobs.Where(x => x.State == EnterpriseJobState.Preview && x.JobType.StartsWith("Background")).ToListAsync(ct)).OrderBy(x => x.CreatedAt).FirstOrDefault();
        if (job is null) return;
        try
        {
            job.Start(DateTimeOffset.UtcNow); await db.SaveChangesAsync(ct);
            job.ReportProgress(25, DateTimeOffset.UtcNow); await db.SaveChangesAsync(ct);
            if(job.JobType=="BackgroundControlledPublication")
            {
                var request=JsonSerializer.Deserialize<PublicationJobPayload>(job.RequestJson)??throw new InvalidOperationException("Publication request is invalid.");
                var document=await db.ControlledDocuments.AsNoTracking().SingleOrDefaultAsync(x=>x.Id==request.DocumentId&&x.ProjectId==job.ProjectId,ct)??throw new InvalidOperationException("The controlled document no longer exists.");
                var output=await scope.ServiceProvider.GetRequiredService<ControlledOutputGenerator>().GenerateAsync(request.DocumentId,request.Format,ct)??throw new InvalidOperationException("The controlled publication could not be rendered.");
                job.ReportProgress(75,DateTimeOffset.UtcNow);await db.SaveChangesAsync(ct);await using var content=new MemoryStream(output.Content);var stored=await scope.ServiceProvider.GetRequiredService<EvidenceFileStore>().StoreAsync(content,output.FileName,output.ContentType,ct);
                var publicationResult=new{document.Id,document.DocumentNumber,document.Revision,document.ContentHash,document.GeneratedAt,format=request.Format,templateId=request.TemplateId,previousDocumentId=request.PreviousDocumentId,renderer="AeroLink professional publication renderer",reproducible=true,stored.StorageKey,stored.OriginalFileName,stored.ContentType,stored.Size,stored.Sha256};
                job.Complete(1,0,JsonSerializer.Serialize(publicationResult),DateTimeOffset.UtcNow);await db.SaveChangesAsync(ct);return;
            }
            if(job.JobType=="BackgroundVariantPublication")
            {
                var request=JsonSerializer.Deserialize<ConfigurationPublicationJobPayload>(job.RequestJson)??throw new InvalidOperationException("Configuration publication request is invalid.");var publication=await scope.ServiceProvider.GetRequiredService<VariantPublicationGenerator>().GenerateAsync(request.VariantBaselineId,request.TemplateRevisionId,request.PreviousVariantBaselineId,request.Format,ct)??throw new InvalidOperationException("The immutable configuration publication could not be rendered.");job.ReportProgress(70,DateTimeOffset.UtcNow);await db.SaveChangesAsync(ct);await using var content=new MemoryStream(publication.Output.Content);var stored=await scope.ServiceProvider.GetRequiredService<EvidenceFileStore>().StoreAsync(content,publication.Output.FileName,publication.Output.ContentType,ct);var variantResult=new{request.VariantBaselineId,request.TemplateRevisionId,request.PreviousVariantBaselineId,publication.SourceManifestHash,publication.TemplateManifestHash,publication.PreviousManifestHash,publication.RequirementCount,publication.TraceCount,publication.TestCount,format=request.Format,renderer="AeroLink configuration publication renderer",reproducible=true,stored.StorageKey,stored.OriginalFileName,stored.ContentType,stored.Size,stored.Sha256};job.Complete(1,0,JsonSerializer.Serialize(variantResult),DateTimeOffset.UtcNow);await db.SaveChangesAsync(ct);return;
            }
            if(job.JobType=="BackgroundReleaseEvidencePackage")
            {
                var request=JsonSerializer.Deserialize<ReleaseEvidencePackageJobPayload>(job.RequestJson)??throw new InvalidOperationException("Release evidence package request is invalid.");var members=await db.EnterpriseOperationJobs.AsNoTracking().Where(x=>request.PublicationJobIds.Contains(x.Id)&&x.ProjectId==job.ProjectId&&x.State==EnterpriseJobState.Completed).OrderBy(x=>x.Id).ToListAsync(ct);if(members.Count!=request.PublicationJobIds.Count)throw new InvalidOperationException("A publication package member is missing or incomplete.");var store=scope.ServiceProvider.GetRequiredService<EvidenceFileStore>();var entries=new List<object>();var archiveTime=new DateTimeOffset(2000,1,1,0,0,0,TimeSpan.Zero);using var package=new MemoryStream();using(var zip=new ZipArchive(package,ZipArchiveMode.Create,true)){foreach(var member in members){using var memberResult=JsonDocument.Parse(member.ResultJson);var root=memberResult.RootElement;var storage=Property(root,"StorageKey");var name=Property(root,"OriginalFileName");var sha=Property(root,"Sha256");var entry=zip.CreateEntry(name,CompressionLevel.Optimal);entry.LastWriteTime=archiveTime;await using var source=store.OpenRead(storage);await using(var target=entry.Open())await source.CopyToAsync(target,ct);entries.Add(new{jobId=member.Id,fileName=name,sha256=sha,sourceManifestHash=Optional(root,"SourceManifestHash"),templateManifestHash=Optional(root,"TemplateManifestHash"),variantBaselineId=Optional(root,"VariantBaselineId")});}var manifestJson=JsonSerializer.Serialize(new{format="AeroLink release-evidence-package/v1",projectId=job.ProjectId,createdAt=job.CreatedAt,members=entries});var manifest=zip.CreateEntry("manifest.json",CompressionLevel.Optimal);manifest.LastWriteTime=archiveTime;await using var writer=new StreamWriter(manifest.Open(),new UTF8Encoding(false));await writer.WriteAsync(manifestJson);await writer.FlushAsync(ct);}package.Position=0;var stored=await store.StoreAsync(package,$"aerolink-release-evidence-{job.Id:N}.zip","application/zip",ct);var manifestMaterial=JsonSerializer.Serialize(entries);job.Complete(members.Count,0,JsonSerializer.Serialize(new{memberCount=members.Count,manifestHash=VariantConfigurationProjectionService.Hash(manifestMaterial),stored.StorageKey,stored.OriginalFileName,stored.ContentType,stored.Size,stored.Sha256}),DateTimeOffset.UtcNow);await db.SaveChangesAsync(ct);return;
            }
            var requirements = await db.Requirements.AsNoTracking().CountAsync(x => x.ProjectId == job.ProjectId, ct);
            var revisions = await (from revision in db.RequirementRevisions.AsNoTracking() join artifact in db.Requirements.AsNoTracking().Where(x => x.ProjectId == job.ProjectId) on revision.ArtifactId equals artifact.Id select revision.Id).CountAsync(ct);
            var attachments = await db.ControlledAttachments.AsNoTracking().CountAsync(x => x.ProjectId == job.ProjectId, ct);
            job.ReportProgress(75, DateTimeOffset.UtcNow); await db.SaveChangesAsync(ct);
            object result;
            if (job.JobType.Contains("Export", StringComparison.OrdinalIgnoreCase))
            {
                var rows = await (from artifact in db.Requirements.AsNoTracking().Where(x => x.ProjectId == job.ProjectId) join revision in db.RequirementRevisions.AsNoTracking() on artifact.Id equals revision.ArtifactId select new { artifact.BaseNumber, Level = artifact.Level.ToString(), revision.Revision, revision.Statement, revision.VerificationMethod, State = revision.State.ToString() }).ToListAsync(ct);
                var current = rows.GroupBy(x => x.BaseNumber).Select(x => x.OrderByDescending(r => r.Revision).First()).OrderBy(x => x.BaseNumber).ToList();
                static string Csv(string value) => "\"" + value.Replace("\"", "\"\"") + "\"";
                var text = new StringBuilder("Identifier,Revision,Level,Statement,Verification,State\r\n"); foreach (var row in current) text.AppendLine($"{Csv(row.BaseNumber)},{row.Revision},{Csv(row.Level)},{Csv(row.Statement)},{Csv(row.VerificationMethod)},{Csv(row.State)}");
                await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(text.ToString())); var stored = await scope.ServiceProvider.GetRequiredService<EvidenceFileStore>().StoreAsync(stream, $"aerolink-requirements-{job.Id:N}.csv", "text/csv", ct);
                result = new { requirements, revisions, attachments, generatedAt = DateTimeOffset.UtcNow, format = "controlled-csv", stored.StorageKey, stored.OriginalFileName, stored.ContentType, stored.Size, stored.Sha256 };
            }
            else result = new { requirements, revisions, attachments, generatedAt = DateTimeOffset.UtcNow, format = "integrity-manifest" };
            job.Complete(job.ItemCount, 0, JsonSerializer.Serialize(result), DateTimeOffset.UtcNow);
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            job.Fail(ex.Message, DateTimeOffset.UtcNow);
            await db.SaveChangesAsync(CancellationToken.None);
        }
    }
    private static string Property(JsonElement root,string name)=>root.TryGetProperty(name,out var p)||root.TryGetProperty(char.ToLowerInvariant(name[0])+name[1..],out p)?p.GetString()??throw new InvalidOperationException($"Publication result {name} is empty."):throw new InvalidOperationException($"Publication result has no {name}.");
    private static string? Optional(JsonElement root,string name)=>root.TryGetProperty(name,out var p)||root.TryGetProperty(char.ToLowerInvariant(name[0])+name[1..],out p)?p.ToString():null;
}

public sealed record PublicationJobPayload(Guid DocumentId,string Format,Guid? PreviousDocumentId,Guid? TemplateId);
public sealed record ConfigurationPublicationJobPayload(Guid VariantBaselineId,string Format,Guid TemplateRevisionId,Guid? PreviousVariantBaselineId);
public sealed record ReleaseEvidencePackageJobPayload(IReadOnlyList<Guid> PublicationJobIds);
