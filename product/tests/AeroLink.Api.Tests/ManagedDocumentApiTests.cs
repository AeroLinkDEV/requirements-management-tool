using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AeroLink.Domain.Identity;
using AeroLink.Domain.Programs;
using AeroLink.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace AeroLink.Api.Tests;

public sealed class ManagedDocumentApiTests
{
    [Fact]
    public async Task Active_build_can_create_checkout_and_check_in_a_watermarked_word_document()
    {
        using var factory = new AeroLinkApiFactory(); using var client = factory.CreateClient(); await ProblemReportApiTests.BootstrapAndLoginAsync(client);
        var scope = await SeedProjectAsync(factory); client.DefaultRequestHeaders.Add("X-AeroLink-Build-Context", scope.ActiveReleaseId.ToString());
        using var created = await client.PostAsJsonAsync("/api/managed-documents", new { projectId = scope.ProjectId, targetReleaseId = scope.ActiveReleaseId, acronym = "SDP", documentType = "Software Development Plan", title = "Navigation Software Development Plan", changeSummary = "Initial controlled draft." });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode); var createdBody = await created.Content.ReadFromJsonAsync<JsonElement>(); var documentId = createdBody.GetProperty("id").GetGuid(); var revisionId = createdBody.GetProperty("revisionId").GetGuid();

        using var checkout = await client.PostAsync($"/api/managed-documents/revisions/{revisionId}/checkout", null); Assert.Equal(HttpStatusCode.OK, checkout.StatusCode); var checkoutBody = await checkout.Content.ReadFromJsonAsync<JsonElement>();
        using var secondCheckout = await client.PostAsync($"/api/managed-documents/revisions/{revisionId}/checkout", null); Assert.Equal(HttpStatusCode.Conflict, secondCheckout.StatusCode);
        var ticket = Query(new Uri(checkoutBody.GetProperty("launchUri").GetString()!))["ticket"];
        using var redeemed = await client.PostAsync($"/api/document-connector/redeem/{Uri.EscapeDataString(ticket)}", null); Assert.Equal(HttpStatusCode.OK, redeemed.StatusCode); var grant = await redeemed.Content.ReadFromJsonAsync<JsonElement>();
        var grantId = grant.GetProperty("id").GetGuid(); var token = grant.GetProperty("accessToken").GetString()!; var version = grant.GetProperty("sessionVersion").GetInt64();
        using var download = new HttpRequestMessage(HttpMethod.Get, $"/api/document-connector/{grantId}/download"); download.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token); using var downloaded = await client.SendAsync(download); Assert.Equal(HttpStatusCode.OK, downloaded.StatusCode); var word = await downloaded.Content.ReadAsByteArrayAsync(); ManagedDocumentFileService.ValidateDocx(word, true);
        using var form = new MultipartFormDataContent(); form.Add(new StringContent("Added build and code-traceability responsibilities."), "comment"); form.Add(new StringContent(version.ToString()), "expectedVersion"); var file = new ByteArrayContent(word); file.Headers.ContentType = new(ManagedDocumentFileService.DocxContentType); form.Add(file, "file", "SDP-000001.00.docx");
        using var checkin = new HttpRequestMessage(HttpMethod.Post, $"/api/document-connector/{grantId}/check-in") { Content = form }; checkin.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token); using var checkedIn = await client.SendAsync(checkin); Assert.Equal(HttpStatusCode.OK, checkedIn.StatusCode);

        var detail = await client.GetFromJsonAsync<JsonElement>($"/api/managed-documents/{documentId}?releaseId={scope.ActiveReleaseId}"); var attachments = detail.GetProperty("revisions")[0].GetProperty("attachments").EnumerateArray().ToList(); Assert.Equal(2, attachments.Count); Assert.Contains(attachments, item => item.GetProperty("version").GetInt32() == 2 && item.GetProperty("state").GetString() == "Active");
        using var submitted = await client.PostAsJsonAsync($"/api/managed-documents/revisions/{revisionId}/submit", new { technicalReviewerId = "software.lead", finalApproverId = "quality.analyst" });
        Assert.Equal(HttpStatusCode.OK, submitted.StatusCode);
        using var verificationScope = factory.Services.CreateScope(); var verificationDb = verificationScope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        Assert.Equal(2, await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.CountAsync(verificationDb.ManagedDocumentReviewSteps.Where(x => x.RevisionId == revisionId)));
    }

    [Fact]
    public async Task Released_build_is_read_only_and_does_not_project_a_successor_build_draft()
    {
        using var factory = new AeroLinkApiFactory(); using var client = factory.CreateClient(); await ProblemReportApiTests.BootstrapAndLoginAsync(client); var scope = await SeedProjectAsync(factory);
        client.DefaultRequestHeaders.Add("X-AeroLink-Build-Context", scope.ReleasedId.ToString());
        using var refused = await client.PostAsJsonAsync("/api/managed-documents", new { projectId = scope.ProjectId, targetReleaseId = scope.ReleasedId, acronym = "SVP", documentType = "Software Verification Plan", title = "Historical edit", changeSummary = "Must fail." }); Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        var list = await client.GetFromJsonAsync<JsonElement>($"/api/managed-documents?projectId={scope.ProjectId}&releaseId={scope.ReleasedId}"); Assert.Equal(0, list.GetProperty("totalCount").GetInt32());
    }

    private static async Task<(Guid ProjectId, Guid ReleasedId, Guid ActiveReleaseId)> SeedProjectAsync(AeroLinkApiFactory factory)
    {
        using var scope = factory.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>(); var program = new ProgramRecord("Document Program", $"DC{Guid.NewGuid():N}"[..12]); var project = new ProjectRecord(program.Id, "Navigation Product", "Navigation Software"); var released = new SoftwareRelease(project.Id, "1.5", true); var active = new SoftwareRelease(project.Id, "1.6", false, released.Id); var now = DateTimeOffset.UtcNow;
        var technical = new UserAccount("software.lead", "Rina Shah", "software.lead@example.test", IdentityService.HashPassword(AeroLinkApiFactory.MemberPassword), now); var quality = new UserAccount("quality.analyst", "Maya Patel", "quality.analyst@example.test", IdentityService.HashPassword(AeroLinkApiFactory.MemberPassword), now);
        db.AddRange(program, project, released, active, technical, quality, new ProgramMembership(technical.Id, program.Id, ProgramRole.SoftwareEngineeringLead, "admin", now), new ProgramMembership(quality.Id, program.Id, ProgramRole.SoftwareQualityAnalyst, "admin", now)); await db.SaveChangesAsync(); return (project.Id, released.Id, active.Id);
    }
    private static Dictionary<string,string> Query(Uri uri) => uri.Query.TrimStart('?').Split('&').Select(part => part.Split('=', 2)).ToDictionary(pair => pair[0], pair => Uri.UnescapeDataString(pair[1]));
}
