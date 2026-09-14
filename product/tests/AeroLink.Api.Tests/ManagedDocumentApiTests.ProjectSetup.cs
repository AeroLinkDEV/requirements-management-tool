using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AeroLink.Domain.Documents;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AeroLink.Api.Tests;

public sealed partial class ManagedDocumentApiTests
{
    [Fact]
    public async Task Fresh_setup_project_supports_local_connector_check_in_review_and_exact_controlled_release()
    {
        using var factory = new AeroLinkApiFactory();
        var project = await ProjectSetupServiceQualificationTests.QualifyAsync(factory);
        using var admin = factory.CreateClient();
        await SignIn("admin", AeroLinkApiFactory.AdministratorPassword, admin);
        Guid reviewerId, qualityId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            reviewerId = await db.UserAccounts.Where(x => x.UserName == "fresh.reviewer").Select(x => x.Id).SingleAsync();
            qualityId = await db.UserAccounts.Where(x => x.UserName == "fresh.backup").Select(x => x.Id).SingleAsync();
        }
        using (var leadership = await admin.PostAsJsonAsync($"/api/projects/{project.ProjectId}/leadership/SystemEngineeringLead/primary",
            new { holderUserId = reviewerId })) await RequireSuccess(leadership);
        using (var qualityRole = await admin.PostAsJsonAsync($"/api/projects/{project.ProjectId}/personnel",
            new { userId = qualityId, roles = new[] { "SoftwareQualityAnalyst" } })) await RequireSuccess(qualityRole);
        using var owner = factory.CreateClient();
        using var technical = factory.CreateClient();
        using var quality = factory.CreateClient();
        await SignIn("fresh.author", AeroLinkApiFactory.MemberPassword, owner);
        await SignIn("fresh.reviewer", AeroLinkApiFactory.MemberPassword, technical);
        await SignIn("fresh.backup", AeroLinkApiFactory.MemberPassword, quality);

        using var created = await owner.PostAsJsonAsync("/api/managed-documents", new
        {
            projectId = project.ProjectId, acronym = "SDP", documentType = "Software Development Plan",
            title = "Independent project plan", ownerId = "fresh.author", formalChangeSummary = "Initial isolated plan.",
            operationKey = "fresh-plan",
        });
        await RequireSuccess(created);
        var body = await created.Content.ReadFromJsonAsync<JsonElement>();
        var documentId = body.GetProperty("id").GetGuid();
        var revisionId = body.GetProperty("revisionId").GetGuid();
        using var checkout = await owner.PostAsync($"/api/managed-documents/revisions/{revisionId}/checkout", null);
        await RequireSuccess(checkout);
        var launch = LaunchEnvelope(new Uri((await checkout.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("launchUri").GetString()!));
        Assert.Equal(project.ProjectId, launch.ProjectId);
        Assert.Equal(revisionId, launch.RevisionId);
        using var redeem = await owner.PostAsync($"/api/document-connector/redeem/{Uri.EscapeDataString(launch.Nonce)}", null);
        await RequireSuccess(redeem);
        var grant = await redeem.Content.ReadFromJsonAsync<JsonElement>();
        var grantId = grant.GetProperty("id").GetGuid();
        var token = grant.GetProperty("accessToken").GetString()!;
        using var downloadRequest = new HttpRequestMessage(HttpMethod.Get, $"/api/document-connector/{grantId}/download");
        downloadRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var download = await owner.SendAsync(downloadRequest);
        await RequireSuccess(download);
        var workingBytes = await download.Content.ReadAsByteArrayAsync();
        ManagedDocumentFileService.ValidateDocx(workingBytes, true);
        using var checkIn = await SendCheckInAsync(owner, grantId, token, grant.GetProperty("sessionVersion").GetInt64(),
            "Checked the initial plan through the local connector protocol.", workingBytes);
        await RequireSuccess(checkIn);
        using var submitted = await SubmitAsync(owner, documentId, revisionId, "fresh.reviewer", "fresh.backup");
        await RequireSuccess(submitted);
        using var reviewed = await DecideAsync(technical, documentId, revisionId, "approve",
            "I confirm the technical review.", "The isolated plan and exact working source were reviewed.");
        await RequireSuccess(reviewed);
        using var missingCandidate = await DecideAsync(quality, documentId, revisionId, "approve",
            "I authorize release.", "A release pair has not yet been supplied.");
        Assert.Equal(HttpStatusCode.BadRequest, missingCandidate.StatusCode);
        Assert.Contains("Prepare the exact DOCX and PDF release candidate", await missingCandidate.Content.ReadAsStringAsync());

        using var prepare = await quality.PostAsync($"/api/managed-documents/revisions/{revisionId}/release-preparation", null);
        await RequireSuccess(prepare);
        var releaseLaunch = LaunchEnvelope(new Uri((await prepare.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("launchUri").GetString()!));
        using var releaseRedeem = await quality.PostAsync($"/api/document-connector/redeem/{Uri.EscapeDataString(releaseLaunch.Nonce)}", null);
        await RequireSuccess(releaseRedeem);
        var releaseGrant = await releaseRedeem.Content.ReadFromJsonAsync<JsonElement>();
        var releaseGrantId = releaseGrant.GetProperty("id").GetGuid();
        var releaseToken = releaseGrant.GetProperty("accessToken").GetString()!;
        // This is explicit local protocol/rendering qualification, not a claim that Microsoft Word ran.
        var docxBytes = ManagedDocumentFileService.ApplyReleaseMarking(workingBytes);
        var publication = new ProfessionalPublication("AeroLink", "Independent services", "Independent services",
            "Software Development Plan", "Independent project plan", "Controlled Project document", "SDP-000001", "00",
            "Released", "Project-wide", "All software builds", "fresh.author", DateTimeOffset.UtcNow,
            ManagedDocumentFileService.Sha256(workingBytes), [("Formal revision scope", "Initial isolated plan.")], [], [],
            [new("Purpose", "Scope", [new("1", "Plan", "Purpose", "Isolated protocol qualification.", [])])]);
        var pdf = ProfessionalPublicationRenderer.Render(publication, "pdf", "SDP-000001.00");
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(releaseGrant.GetProperty("sessionVersion").GetInt64().ToString()), "expectedVersion");
        var docx = new ByteArrayContent(docxBytes); docx.Headers.ContentType = new(ManagedDocumentFileService.DocxContentType);
        form.Add(docx, "docx", "SDP-000001.00.docx");
        var pdfContent = new ByteArrayContent(pdf.Content); pdfContent.Headers.ContentType = new(pdf.ContentType);
        form.Add(pdfContent, "pdf", pdf.FileName);
        using var candidateRequest = new HttpRequestMessage(HttpMethod.Post, $"/api/document-connector/{releaseGrantId}/release-candidate") { Content = form };
        candidateRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", releaseToken);
        using var candidate = await quality.SendAsync(candidateRequest);
        await RequireSuccess(candidate);
        using var released = await DecideAsync(quality, documentId, revisionId, "approve",
            "I authorize this exact controlled release.", "The local DOCX/PDF pair and formal scope were checked.");
        await RequireSuccess(released);
        using var verification = factory.Services.CreateScope();
        var verifyDb = verification.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        var revision = await verifyDb.ManagedDocumentRevisions.SingleAsync(x => x.Id == revisionId);
        Assert.Equal(ManagedDocumentState.Released, revision.State);
        Assert.Equal("fresh.backup", revision.ReleasedBy);
        var pair = await verifyDb.ControlledAttachments.Where(x => x.Id == revision.ReleasedDocxAttachmentId || x.Id == revision.ReleasedPdfAttachmentId).ToListAsync();
        Assert.Equal(2, pair.Count);
        Assert.All(pair, x => Assert.Equal(project.ProjectId, x.ProjectId));
        Assert.Contains(pair, x => x.Sha256 == ManagedDocumentFileService.Sha256(docxBytes));
        Assert.Contains(pair, x => x.Sha256 == ManagedDocumentFileService.Sha256(pdf.Content));
        var signatures = await verifyDb.ElectronicSignatures.Where(x => x.ArtifactId == documentId || x.ArtifactId == revisionId).ToListAsync();
        Assert.Contains(signatures, x => x.UserName == "fresh.reviewer");
        Assert.Contains(signatures, x => x.UserName == "fresh.backup");
        Assert.All(signatures, x => Assert.Equal(project.ProgramId, x.ProgramId));

        static async Task RequireSuccess(HttpResponseMessage response) =>
            Assert.True(response.IsSuccessStatusCode, $"{response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        static async Task SignIn(string name, string password, HttpClient client)
        {
            using var response = await client.PostAsJsonAsync("/api/auth/login", new { userName = name, password });
            await RequireSuccess(response);
            await SecurityBoundaryTests.AuthorizeMutationsAsync(client);
        }
    }
}
