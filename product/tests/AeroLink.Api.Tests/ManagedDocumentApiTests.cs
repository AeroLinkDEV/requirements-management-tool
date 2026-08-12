using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AeroLink.Domain.Documents;
using AeroLink.Domain.Identity;
using AeroLink.Domain.Programs;
using AeroLink.Domain.Requirements;
using AeroLink.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace AeroLink.Api.Tests;

public sealed class ManagedDocumentApiTests
{
    [Fact]
    public async Task Project_document_can_create_checkout_and_check_in_without_build_context()
    {
        using var factory = new AeroLinkApiFactory(); using var client = factory.CreateClient(); await ProblemReportApiTests.BootstrapAndLoginAsync(client);
        var scope = await SeedProjectAsync(factory);
        using var created = await client.PostAsJsonAsync("/api/managed-documents", new { projectId = scope.ProjectId, acronym = "SDP", documentType = "Software Development Plan", title = "Navigation Software Development Plan", changeSummary = "Initial controlled draft." });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode); var createdBody = await created.Content.ReadFromJsonAsync<JsonElement>(); var documentId = createdBody.GetProperty("id").GetGuid(); var revisionId = createdBody.GetProperty("revisionId").GetGuid();

        using var checkout = await client.PostAsync($"/api/managed-documents/revisions/{revisionId}/checkout", null); Assert.Equal(HttpStatusCode.OK, checkout.StatusCode); var checkoutBody = await checkout.Content.ReadFromJsonAsync<JsonElement>();
        using var secondCheckout = await client.PostAsync($"/api/managed-documents/revisions/{revisionId}/checkout", null); Assert.Equal(HttpStatusCode.Conflict, secondCheckout.StatusCode);
        var ticket = Query(new Uri(checkoutBody.GetProperty("launchUri").GetString()!))["ticket"];
        using var redeemed = await client.PostAsync($"/api/document-connector/redeem/{Uri.EscapeDataString(ticket)}", null); Assert.Equal(HttpStatusCode.OK, redeemed.StatusCode); var grant = await redeemed.Content.ReadFromJsonAsync<JsonElement>();
        var grantId = grant.GetProperty("id").GetGuid(); var token = grant.GetProperty("accessToken").GetString()!; var version = grant.GetProperty("sessionVersion").GetInt64();
        using var download = new HttpRequestMessage(HttpMethod.Get, $"/api/document-connector/{grantId}/download"); download.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token); using var downloaded = await client.SendAsync(download); Assert.Equal(HttpStatusCode.OK, downloaded.StatusCode); var word = await downloaded.Content.ReadAsByteArrayAsync(); ManagedDocumentFileService.ValidateDocx(word, true);
        using var form = new MultipartFormDataContent(); form.Add(new StringContent("Added build and code-traceability responsibilities."), "comment"); form.Add(new StringContent(version.ToString()), "expectedVersion"); var file = new ByteArrayContent(word); file.Headers.ContentType = new(ManagedDocumentFileService.DocxContentType); form.Add(file, "file", "SDP-000001.00.docx");
        using var checkin = new HttpRequestMessage(HttpMethod.Post, $"/api/document-connector/{grantId}/check-in") { Content = form }; checkin.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token); using var checkedIn = await client.SendAsync(checkin); Assert.Equal(HttpStatusCode.OK, checkedIn.StatusCode);

        var detail = await client.GetFromJsonAsync<JsonElement>($"/api/managed-documents/{documentId}"); var attachments = detail.GetProperty("revisions")[0].GetProperty("attachments").EnumerateArray().ToList(); Assert.Equal(2, attachments.Count); Assert.Contains(attachments, item => item.GetProperty("version").GetInt32() == 2 && item.GetProperty("state").GetString() == "Active");
        using var submitted = await client.PostAsJsonAsync($"/api/managed-documents/revisions/{revisionId}/submit", new { technicalReviewerId = "software.lead", finalApproverId = "quality.analyst" });
        Assert.Equal(HttpStatusCode.OK, submitted.StatusCode);
        using var verificationScope = factory.Services.CreateScope(); var verificationDb = verificationScope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        Assert.Equal(2, await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.CountAsync(verificationDb.ManagedDocumentReviewSteps.Where(x => x.RevisionId == revisionId)));
    }

    [Fact]
    public async Task Released_build_context_cannot_make_a_project_document_read_only_or_change_inventory()
    {
        using var factory = new AeroLinkApiFactory(); using var client = factory.CreateClient(); await ProblemReportApiTests.BootstrapAndLoginAsync(client); var scope = await SeedProjectAsync(factory);
        client.DefaultRequestHeaders.Add("X-AeroLink-Build-Context", scope.ReleasedId.ToString());
        using var created = await client.PostAsJsonAsync("/api/managed-documents", new { projectId = scope.ProjectId, acronym = "SVP", documentType = "Software Verification Plan", title = "Project verification plan", changeSummary = "Initial Project-wide issue." }); Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var canonical = await client.GetFromJsonAsync<JsonElement>($"/api/managed-documents?projectId={scope.ProjectId}");
        var legacyReleased = await client.GetFromJsonAsync<JsonElement>($"/api/managed-documents?projectId={scope.ProjectId}&releaseId={scope.ReleasedId}");
        var legacyActive = await client.GetFromJsonAsync<JsonElement>($"/api/managed-documents?projectId={scope.ProjectId}&releaseId={scope.ActiveReleaseId}");
        Assert.Equal(1, canonical.GetProperty("totalCount").GetInt32());
        Assert.Equal(canonical.GetProperty("totalCount").GetInt32(), legacyReleased.GetProperty("totalCount").GetInt32());
        Assert.Equal(canonical.GetProperty("totalCount").GetInt32(), legacyActive.GetProperty("totalCount").GetInt32());
    }

    [Fact]
    public async Task Project_without_software_builds_can_use_documentation_center()
    {
        using var factory = new AeroLinkApiFactory(); using var client = factory.CreateClient(); await ProblemReportApiTests.BootstrapAndLoginAsync(client);
        var projectId = await SeedProjectWithoutBuildsAsync(factory);
        using var created = await client.PostAsJsonAsync("/api/managed-documents", new { projectId, acronym = "PSAC", documentType = "Plan for Software Aspects of Certification", title = "Project PSAC", changeSummary = "Initial formal revision." });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var inventory = await client.GetFromJsonAsync<JsonElement>($"/api/managed-documents?projectId={projectId}");
        Assert.Equal(1, inventory.GetProperty("totalCount").GetInt32());
    }

    [Fact]
    public async Task Successor_is_bound_to_the_verified_released_docx_and_missing_parent_fails_closed()
    {
        using var factory = new AeroLinkApiFactory(); using var client = factory.CreateClient(); await ProblemReportApiTests.BootstrapAndLoginAsync(client);
        var scope = await SeedProjectAsync(factory); var seeded = await SeedReleasedDocumentAsync(factory, scope.ProjectId);

        using var started = await client.PostAsJsonAsync($"/api/managed-documents/{seeded.DocumentId}/revisions", new { changeSummary = "Update the Project plan." });
        Assert.True(started.StatusCode == HttpStatusCode.Created, await started.Content.ReadAsStringAsync());
        var detail = await client.GetFromJsonAsync<JsonElement>($"/api/managed-documents/{seeded.DocumentId}");
        var successor = detail.GetProperty("revisions").EnumerateArray().Single(x => x.GetProperty("revision").GetInt32() == 1);
        Assert.Equal(seeded.RevisionId, successor.GetProperty("parentRevisionId").GetGuid());
        Assert.Equal(seeded.ReleasedDocxId, successor.GetProperty("parentReleasedDocxAttachmentId").GetGuid());
        Assert.Equal(seeded.ReleasedDocxSha256, successor.GetProperty("parentReleasedDocxSha256").GetString());
        Assert.Equal(ManagedDocumentFileService.SuccessorTransformationProfile, successor.GetProperty("transformationProfile").GetString());

        var releasedSuccessor = await ReleaseSuccessorForTestAsync(factory, seeded.DocumentId, successor.GetProperty("id").GetGuid());
        using var startedAgain = await client.PostAsJsonAsync($"/api/managed-documents/{seeded.DocumentId}/revisions", new { changeSummary = "Second Project update." });
        Assert.Equal(HttpStatusCode.Created, startedAgain.StatusCode);
        var sequential = await client.GetFromJsonAsync<JsonElement>($"/api/managed-documents/{seeded.DocumentId}");
        var revision02 = sequential.GetProperty("revisions").EnumerateArray().Single(x => x.GetProperty("revision").GetInt32() == 2);
        Assert.Equal(releasedSuccessor.RevisionId, revision02.GetProperty("parentRevisionId").GetGuid());
        Assert.Equal(releasedSuccessor.DocxId, revision02.GetProperty("parentReleasedDocxAttachmentId").GetGuid());
        Assert.Equal(releasedSuccessor.Sha256, revision02.GetProperty("parentReleasedDocxSha256").GetString());

        var second = await SeedReleasedDocumentAsync(factory, scope.ProjectId, "SQAP");
        using (var serviceScope = factory.Services.CreateScope()) serviceScope.ServiceProvider.GetRequiredService<EvidenceFileStore>().Delete(second.StorageKey);
        using var refused = await client.PostAsJsonAsync($"/api/managed-documents/{second.DocumentId}/revisions", new { changeSummary = "Must not persist." });
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        using var verifyScope = factory.Services.CreateScope(); var db = verifyScope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        Assert.Equal(1, await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.CountAsync(db.ManagedDocumentRevisions.Where(x => x.DocumentId == second.DocumentId)));
    }

    [Fact]
    public async Task Concurrent_successor_starts_produce_one_project_revision()
    {
        using var factory = new AeroLinkApiFactory(); using var firstClient = factory.CreateClient(); using var secondClient = factory.CreateClient();
        await ProblemReportApiTests.BootstrapAndLoginAsync(firstClient);
        using (var login = await secondClient.PostAsJsonAsync("/api/auth/login", new { userName = "admin", password = AeroLinkApiFactory.AdministratorPassword })) Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        await SecurityBoundaryTests.AuthorizeMutationsAsync(secondClient);
        var scope = await SeedProjectAsync(factory); var seeded = await SeedReleasedDocumentAsync(factory, scope.ProjectId);
        var requests = await Task.WhenAll(
            firstClient.PostAsJsonAsync($"/api/managed-documents/{seeded.DocumentId}/revisions", new { changeSummary = "Concurrent successor A." }),
            secondClient.PostAsJsonAsync($"/api/managed-documents/{seeded.DocumentId}/revisions", new { changeSummary = "Concurrent successor B." }));
        Assert.Single(requests, x => x.StatusCode == HttpStatusCode.Created);
        Assert.Single(requests, x => x.StatusCode == HttpStatusCode.Conflict);
        using var verificationScope = factory.Services.CreateScope(); var db = verificationScope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        Assert.Equal(2, await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.CountAsync(db.ManagedDocumentRevisions.Where(x => x.DocumentId == seeded.DocumentId)));
    }

    private static async Task<Guid> SeedProjectWithoutBuildsAsync(AeroLinkApiFactory factory)
    {
        using var scope = factory.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        var program = new ProgramRecord("Document Program", $"DZ{Guid.NewGuid():N}"[..12]); var project = new ProjectRecord(program.Id, "Build-free Product", "Project Documentation");
        db.AddRange(program, project); await db.SaveChangesAsync(); return project.Id;
    }

    private static async Task<(Guid DocumentId, Guid RevisionId, Guid ReleasedDocxId, string ReleasedDocxSha256, string StorageKey)> SeedReleasedDocumentAsync(AeroLinkApiFactory factory, Guid projectId, string acronym = "SCMP")
    {
        using var scope = factory.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>(); var files = scope.ServiceProvider.GetRequiredService<ManagedDocumentFileService>();
        var now = DateTimeOffset.UtcNow; var document = new ManagedDocument(projectId, $"{acronym}-000001", acronym, "Project Plan", $"{acronym} Project Plan", "admin", now); var revision = new ManagedDocumentRevision(document.Id, 0, "admin", "Initial controlled Project issue.", now);
        var publication = new ProfessionalPublication("AeroLink", "Program", "Project", "Project Plan", document.Title, "Controlled Project document", document.DocumentNumber, "00", "Draft", "Project-wide", "All software builds", "admin", now, new string('a', 64), [], [], [], [new("Purpose", "Scope", [new("1", "Plan", "Purpose", "Controlled content.", [])])]) { Watermark = "DRAFT" };
        var draft = ProfessionalPublicationRenderer.Render(publication, "docx", $"{document.DocumentNumber}.00"); var draftAttachment = await files.StoreAsync(projectId, document.Id, revision.Id, revision.Id, 1, "Working Word document", "Initial draft.", draft.FileName, draft.ContentType, draft.Content, null, "admin", now, default); revision.RecordCheckIn(draftAttachment.Id, "admin", revision.ChangeSummary, now);
        var cycle = revision.SubmitForReview("admin", draftAttachment.Sha256, [new("software.lead", "Rina Shah", "Technical"), new("quality.analyst", "Maya Patel", "Final")], now); revision.Approve("software.lead", "Complete.", now);
        var releasedPublication = publication with { Status = "Released", Watermark = null }; var docx = ProfessionalPublicationRenderer.Render(releasedPublication, "docx", $"{document.DocumentNumber}.00"); var pdf = ProfessionalPublicationRenderer.Render(releasedPublication, "pdf", $"{document.DocumentNumber}.00");
        var releasedDocx = await files.StoreAsync(projectId, document.Id, revision.Id, Guid.NewGuid(), 1, "Released DOCX", "Immutable source.", docx.FileName, docx.ContentType, docx.Content, null, "quality.analyst", now, default); var releasedPdf = await files.StoreAsync(projectId, document.Id, revision.Id, Guid.NewGuid(), 1, "Released PDF", "Immutable rendition.", pdf.FileName, pdf.ContentType, pdf.Content, null, "quality.analyst", now, default);
        revision.RecordReleaseCandidate(releasedDocx.Id, releasedPdf.Id, ManagedDocumentFileService.Sha256(System.Text.Encoding.UTF8.GetBytes(releasedDocx.Sha256 + ":" + releasedPdf.Sha256)), "quality.analyst", now); revision.Approve("quality.analyst", "Release.", now);
        db.AddRange(document, revision, draftAttachment, releasedDocx, releasedPdf); db.ManagedDocumentReviewSteps.AddRange(revision.ReviewSteps.Where(x => x.Cycle == cycle)); await db.SaveChangesAsync();
        return (document.Id, revision.Id, releasedDocx.Id, releasedDocx.Sha256, releasedDocx.StorageKey);
    }

    private static async Task<(Guid RevisionId, Guid DocxId, string Sha256)> ReleaseSuccessorForTestAsync(AeroLinkApiFactory factory, Guid documentId, Guid revisionId)
    {
        using var scope = factory.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>(); var files = scope.ServiceProvider.GetRequiredService<ManagedDocumentFileService>();
        var document = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.SingleAsync(db.ManagedDocuments.Where(x => x.Id == documentId)); var revision = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.SingleAsync(db.ManagedDocumentRevisions.Where(x => x.Id == revisionId)); var working = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.SingleAsync(db.ControlledAttachments.Where(x => x.Id == revision.CurrentWorkingAttachmentId)); var now = DateTimeOffset.UtcNow;
        var cycle = revision.SubmitForReview("admin", working.Sha256, [new("software.lead", "Rina Shah", "Technical"), new("quality.analyst", "Maya Patel", "Final")], now); revision.Approve("software.lead", "Complete.", now);
        var publication = new ProfessionalPublication("AeroLink", "Program", "Project", "Project Plan", document.Title, "Controlled Project document", document.DocumentNumber, revision.Revision.ToString("D2"), "Released", "Project-wide", "All software builds", "admin", now, new string('b', 64), [], [], [], [new("Purpose", "Scope", [new("1", "Plan", "Purpose", "Controlled content.", [])])]);
        var docx = ProfessionalPublicationRenderer.Render(publication, "docx", $"{document.DocumentNumber}.{revision.Revision:D2}"); var pdf = ProfessionalPublicationRenderer.Render(publication, "pdf", $"{document.DocumentNumber}.{revision.Revision:D2}");
        var releasedDocx = await files.StoreAsync(document.ProjectId, document.Id, revision.Id, Guid.NewGuid(), 1, "Released DOCX", "Immutable source.", docx.FileName, docx.ContentType, docx.Content, null, "quality.analyst", now, default); var releasedPdf = await files.StoreAsync(document.ProjectId, document.Id, revision.Id, Guid.NewGuid(), 1, "Released PDF", "Immutable rendition.", pdf.FileName, pdf.ContentType, pdf.Content, null, "quality.analyst", now, default);
        revision.RecordReleaseCandidate(releasedDocx.Id, releasedPdf.Id, ManagedDocumentFileService.Sha256(System.Text.Encoding.UTF8.GetBytes(releasedDocx.Sha256 + ":" + releasedPdf.Sha256)), "quality.analyst", now); revision.Approve("quality.analyst", "Release.", now);
        var oldHead = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.SingleAsync(db.ManagedDocumentRevisions.Where(x => x.DocumentId == documentId && x.Id != revisionId && x.State == ManagedDocumentState.Released)); oldHead.Supersede(now);
        db.AddRange(releasedDocx, releasedPdf); db.ManagedDocumentReviewSteps.AddRange(revision.ReviewSteps.Where(x => x.Cycle == cycle)); await db.SaveChangesAsync(); return (revision.Id, releasedDocx.Id, releasedDocx.Sha256);
    }

    private static async Task<(Guid ProjectId, Guid ReleasedId, Guid ActiveReleaseId)> SeedProjectAsync(AeroLinkApiFactory factory)
    {
        using var scope = factory.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>(); var program = new ProgramRecord("Document Program", $"DC{Guid.NewGuid():N}"[..12]); var project = new ProjectRecord(program.Id, "Navigation Product", "Navigation Software"); var released = new SoftwareRelease(project.Id, "1.5", true); var active = new SoftwareRelease(project.Id, "1.6", false, released.Id); var now = DateTimeOffset.UtcNow;
        var technical = new UserAccount("software.lead", "Rina Shah", "software.lead@example.test", IdentityService.HashPassword(AeroLinkApiFactory.MemberPassword), now); var quality = new UserAccount("quality.analyst", "Maya Patel", "quality.analyst@example.test", IdentityService.HashPassword(AeroLinkApiFactory.MemberPassword), now);
        db.AddRange(program, project, released, active, technical, quality, new ProgramMembership(technical.Id, program.Id, ProgramRole.SoftwareEngineeringLead, "admin", now), new ProgramMembership(quality.Id, program.Id, ProgramRole.SoftwareQualityAnalyst, "admin", now)); await db.SaveChangesAsync(); return (project.Id, released.Id, active.Id);
    }
    private static Dictionary<string,string> Query(Uri uri) => uri.Query.TrimStart('?').Split('&').Select(part => part.Split('=', 2)).ToDictionary(pair => pair[0], pair => Uri.UnescapeDataString(pair[1]));
}
