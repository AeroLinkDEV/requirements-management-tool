using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AeroLink.Api.Tests;

[Collection(ShowcaseApiCollection.Name)]
public sealed class ConfigurationPublicationApiTests(ShowcaseApiFixture showcase)
{
    [Fact]
    public async Task Configuration_publications_are_resumable_integrity_checked_and_packaged_with_verifiable_manifest()
    {
        using var factory=showcase.CreateFactory();using var client=factory.CreateClient();await BootstrapAsync(client);Guid projectId,activeBaselineId,releasedBaselineId,templateRevisionId;
        using(var scope=factory.Services.CreateScope()){var db=scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();var summary=showcase.Summary;projectId=summary.ProjectId;var variants=await db.ProductVariants.AsNoTracking().Where(x=>x.ProjectId==projectId).ToListAsync();var active=variants.Single(x=>x.VariantKey=="FMS-1.6");var released=variants.Single(x=>x.VariantKey=="FMS-1.5");activeBaselineId=await db.ProductVariantBaselines.Where(x=>x.VariantId==active.Id).OrderByDescending(x=>x.Revision).Select(x=>x.Id).FirstAsync();releasedBaselineId=await db.ProductVariantBaselines.Where(x=>x.VariantId==released.Id).OrderByDescending(x=>x.Revision).Select(x=>x.Id).FirstAsync();templateRevisionId=await db.DocumentTemplateRevisions.Select(x=>x.Id).SingleAsync();}
        var libraries=await client.GetFromJsonAsync<JsonElement>($"/api/product-line/libraries?projectId={projectId}");var library=Assert.Single(libraries.EnumerateArray());Assert.Equal(2,library.GetProperty("decisionHistory").GetArrayLength());Assert.Equal(2,library.GetProperty("reuses").GetArrayLength());
        var pdf=await Post(client,"/api/publications/configuration-jobs",new{variantBaselineId=activeBaselineId,format="pdf",templateRevisionId,previousVariantBaselineId=releasedBaselineId,idempotencyKey="acceptance-pdf"});var pdfId=pdf.GetProperty("id").GetGuid();
        var resumed=await Post(client,"/api/publications/configuration-jobs",new{variantBaselineId=activeBaselineId,format="pdf",templateRevisionId,previousVariantBaselineId=releasedBaselineId,idempotencyKey="acceptance-pdf"});Assert.Equal(pdfId,resumed.GetProperty("id").GetGuid());Assert.True(resumed.GetProperty("reused").GetBoolean());
        var docx=await Post(client,"/api/publications/configuration-jobs",new{variantBaselineId=activeBaselineId,format="docx",templateRevisionId,previousVariantBaselineId=releasedBaselineId,idempotencyKey="acceptance-docx"});var docxId=docx.GetProperty("id").GetGuid();
        await Completed(client,pdfId);await Completed(client,docxId);
        foreach(var id in new[]{pdfId,docxId}){var verification=await client.GetFromJsonAsync<JsonElement>($"/api/publications/jobs/{id}/verify");Assert.True(verification.GetProperty("valid").GetBoolean());Assert.Equal(64,verification.GetProperty("sourceManifestHash").GetString()!.Length);Assert.Equal(64,verification.GetProperty("templateManifestHash").GetString()!.Length);}
        var package=await Post(client,"/api/publications/release-packages",new{projectId,publicationJobIds=new[]{pdfId,docxId},idempotencyKey="acceptance-package"});var packageId=package.GetProperty("id").GetGuid();await Completed(client,packageId);var resumedPackage=await Post(client,"/api/publications/release-packages",new{projectId,publicationJobIds=new[]{pdfId,docxId},idempotencyKey="acceptance-package"});Assert.Equal(packageId,resumedPackage.GetProperty("id").GetGuid());Assert.True(resumedPackage.GetProperty("reused").GetBoolean());
        using var response=await client.GetAsync($"/api/enterprise-hardening/jobs/{packageId}/download");Assert.Equal(HttpStatusCode.OK,response.StatusCode);Assert.Equal("application/zip",response.Content.Headers.ContentType?.MediaType);await using var stream=await response.Content.ReadAsStreamAsync();using var archive=new ZipArchive(stream,ZipArchiveMode.Read);var manifestEntry=archive.GetEntry("manifest.json");Assert.NotNull(manifestEntry);using var manifest=JsonDocument.Parse(await ReadBytes(manifestEntry!));var members=manifest.RootElement.GetProperty("members").EnumerateArray().ToList();Assert.Equal(2,members.Count);foreach(var member in members){var entry=archive.GetEntry(member.GetProperty("fileName").GetString()!);Assert.NotNull(entry);Assert.Equal(member.GetProperty("sha256").GetString(),Convert.ToHexString(SHA256.HashData(await ReadBytes(entry!))).ToLowerInvariant());Assert.Equal(activeBaselineId.ToString(),member.GetProperty("variantBaselineId").GetString(),ignoreCase:true);}
    }

    private static async Task<JsonElement> Completed(HttpClient client,Guid id){for(var i=0;i<80;i++){var job=await client.GetFromJsonAsync<JsonElement>($"/api/publications/jobs/{id}");var state=job.GetProperty("state").GetString();if(state=="Completed")return job;if(state=="Failed")Assert.Fail(job.GetProperty("lastError").GetString());await Task.Delay(250);}throw new TimeoutException($"Job {id} did not complete.");}
    private static async Task<byte[]> ReadBytes(ZipArchiveEntry entry){await using var source=entry.Open();using var output=new MemoryStream();await source.CopyToAsync(output);return output.ToArray();}
    private static async Task BootstrapAsync(HttpClient client){using var request=new HttpRequestMessage(HttpMethod.Post,"/api/setup/bootstrap"){Content=JsonContent.Create(new{displayName="Administrator",email="admin@example.test",password=AeroLinkApiFactory.AdministratorPassword})};request.Headers.Add("X-AeroLink-Bootstrap-Secret",AeroLinkApiFactory.BootstrapSecret);using var created=await client.SendAsync(request);Assert.Equal(HttpStatusCode.Created,created.StatusCode);using var login=await client.PostAsJsonAsync("/api/auth/login",new{userName="admin",password=AeroLinkApiFactory.AdministratorPassword});Assert.Equal(HttpStatusCode.OK,login.StatusCode);await SecurityBoundaryTests.AuthorizeMutationsAsync(client);}
    private static async Task<JsonElement> Post(HttpClient client,string url,object body){using var response=await client.PostAsJsonAsync(url,body);var text=await response.Content.ReadAsStringAsync();Assert.True(response.IsSuccessStatusCode,$"{url} returned {(int)response.StatusCode}: {text}");return JsonDocument.Parse(text).RootElement.Clone();}
}
