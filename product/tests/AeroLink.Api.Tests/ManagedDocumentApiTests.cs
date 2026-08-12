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
        using var created = await client.PostAsJsonAsync("/api/managed-documents", new { projectId = scope.ProjectId, acronym = "SDP", documentType = "Software Development Plan", title = "Navigation Software Development Plan", ownerId = "software.author", changeSummary = "Initial controlled draft." });
        Assert.True(created.StatusCode == HttpStatusCode.Created, await created.Content.ReadAsStringAsync()); var createdBody = await created.Content.ReadFromJsonAsync<JsonElement>(); var documentId = createdBody.GetProperty("id").GetGuid(); var revisionId = createdBody.GetProperty("revisionId").GetGuid();

        using var checkout = await client.PostAsync($"/api/managed-documents/revisions/{revisionId}/checkout", null); Assert.Equal(HttpStatusCode.OK, checkout.StatusCode); var checkoutBody = await checkout.Content.ReadFromJsonAsync<JsonElement>();
        using var secondCheckout = await client.PostAsync($"/api/managed-documents/revisions/{revisionId}/checkout", null); Assert.Equal(HttpStatusCode.Conflict, secondCheckout.StatusCode);
        var ticket = Query(new Uri(checkoutBody.GetProperty("launchUri").GetString()!))["ticket"];
        using var redeemed = await client.PostAsync($"/api/document-connector/redeem/{Uri.EscapeDataString(ticket)}", null); Assert.Equal(HttpStatusCode.OK, redeemed.StatusCode); var grant = await redeemed.Content.ReadFromJsonAsync<JsonElement>();
        var grantId = grant.GetProperty("id").GetGuid(); var token = grant.GetProperty("accessToken").GetString()!; var version = grant.GetProperty("sessionVersion").GetInt64();
        using var download = new HttpRequestMessage(HttpMethod.Get, $"/api/document-connector/{grantId}/download"); download.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token); using var downloaded = await client.SendAsync(download); Assert.Equal(HttpStatusCode.OK, downloaded.StatusCode); var word = await downloaded.Content.ReadAsByteArrayAsync(); ManagedDocumentFileService.ValidateDocx(word, true);
        using (var blankForm = new MultipartFormDataContent()) { blankForm.Add(new StringContent(" "), "comment"); blankForm.Add(new StringContent(version.ToString()), "expectedVersion"); var blankFile = new ByteArrayContent(word); blankFile.Headers.ContentType = new(ManagedDocumentFileService.DocxContentType); blankForm.Add(blankFile, "file", "SDP-000001.00.docx"); using var blankRequest = new HttpRequestMessage(HttpMethod.Post, $"/api/document-connector/{grantId}/check-in") { Content = blankForm }; blankRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token); using var blank = await client.SendAsync(blankRequest); Assert.Equal(HttpStatusCode.BadRequest, blank.StatusCode); }
        using (var oversizedForm = new MultipartFormDataContent()) { oversizedForm.Add(new StringContent(new string('x', 4001)), "comment"); oversizedForm.Add(new StringContent(version.ToString()), "expectedVersion"); var oversizedFile = new ByteArrayContent(word); oversizedFile.Headers.ContentType = new(ManagedDocumentFileService.DocxContentType); oversizedForm.Add(oversizedFile, "file", "SDP-000001.00.docx"); using var oversizedRequest = new HttpRequestMessage(HttpMethod.Post, $"/api/document-connector/{grantId}/check-in") { Content = oversizedForm }; oversizedRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token); using var oversized = await client.SendAsync(oversizedRequest); Assert.Equal(HttpStatusCode.BadRequest, oversized.StatusCode); }
        using var form = new MultipartFormDataContent(); form.Add(new StringContent("Added build and code-traceability responsibilities."), "comment"); form.Add(new StringContent(version.ToString()), "expectedVersion"); var file = new ByteArrayContent(word); file.Headers.ContentType = new(ManagedDocumentFileService.DocxContentType); form.Add(file, "file", "SDP-000001.00.docx");
        using var checkin = new HttpRequestMessage(HttpMethod.Post, $"/api/document-connector/{grantId}/check-in") { Content = form }; checkin.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token); using var checkedIn = await client.SendAsync(checkin); Assert.Equal(HttpStatusCode.OK, checkedIn.StatusCode);

        using var detailResponse = await client.GetAsync($"/api/managed-documents/{documentId}"); Assert.True(detailResponse.IsSuccessStatusCode, await detailResponse.Content.ReadAsStringAsync()); var detail = await detailResponse.Content.ReadFromJsonAsync<JsonElement>(); var revision = detail.GetProperty("revisions")[0]; var attachments = revision.GetProperty("attachments").EnumerateArray().ToList(); Assert.Equal(2, attachments.Count); Assert.Contains(attachments, item => item.GetProperty("version").GetInt32() == 2 && item.GetProperty("state").GetString() == "Active");
        Assert.Equal("Initial controlled draft.", revision.GetProperty("formalChangeSummary").GetString());
        Assert.Equal("software.author", revision.GetProperty("responsibleOwnerId").GetString());
        var checkIns = revision.GetProperty("checkIns").EnumerateArray().ToList();
        Assert.Equal(2, checkIns.Count);
        Assert.Equal("Created the initial controlled Word template.", checkIns[0].GetProperty("comment").GetString());
        Assert.Equal("Added build and code-traceability responsibilities.", checkIns[1].GetProperty("comment").GetString());
        Assert.Equal(attachments.Single(x => x.GetProperty("version").GetInt32() == 2).GetProperty("sha256").GetString(), checkIns[1].GetProperty("resultSha256").GetString());
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
        using var created = await client.PostAsJsonAsync("/api/managed-documents", new { projectId = scope.ProjectId, acronym = "SVP", documentType = "Software Verification Plan", title = "Project verification plan", ownerId = "software.author", changeSummary = "Initial Project-wide issue." }); Assert.Equal(HttpStatusCode.Created, created.StatusCode); var createdBody = await created.Content.ReadFromJsonAsync<JsonElement>();
        var canonical = await client.GetFromJsonAsync<JsonElement>($"/api/managed-documents?projectId={scope.ProjectId}");
        var legacyReleased = await client.GetFromJsonAsync<JsonElement>($"/api/managed-documents?projectId={scope.ProjectId}&releaseId={scope.ReleasedId}");
        var legacyActive = await client.GetFromJsonAsync<JsonElement>($"/api/managed-documents?projectId={scope.ProjectId}&releaseId={scope.ActiveReleaseId}");
        Assert.Equal(1, canonical.GetProperty("totalCount").GetInt32());
        Assert.Equal(canonical.GetProperty("totalCount").GetInt32(), legacyReleased.GetProperty("totalCount").GetInt32());
        Assert.Equal(canonical.GetProperty("totalCount").GetInt32(), legacyActive.GetProperty("totalCount").GetInt32());
        var detail = await client.GetFromJsonAsync<JsonElement>($"/api/managed-documents/{createdBody.GetProperty("id").GetGuid()}"); var revision = detail.GetProperty("revisions")[0];
        using var corrected = await client.PatchAsJsonAsync($"/api/managed-documents/revisions/{createdBody.GetProperty("revisionId").GetGuid()}/formal-summary", new { formalChangeSummary = "Project-wide scope corrected while a released build is selected.", reason = "Prove build state is not a document-edit boundary.", expectedVersion = revision.GetProperty("version").GetInt64() });
        Assert.Equal(HttpStatusCode.OK, corrected.StatusCode);
    }

    [Fact]
    public async Task Project_without_software_builds_can_use_documentation_center()
    {
        using var factory = new AeroLinkApiFactory(); using var client = factory.CreateClient(); await ProblemReportApiTests.BootstrapAndLoginAsync(client);
        var projectId = await SeedProjectWithoutBuildsAsync(factory);
        using var created = await client.PostAsJsonAsync("/api/managed-documents", new { projectId, acronym = "PSAC", documentType = "Plan for Software Aspects of Certification", title = "Project PSAC", ownerId = "buildfree.author", changeSummary = "Initial formal revision." });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var inventory = await client.GetFromJsonAsync<JsonElement>($"/api/managed-documents?projectId={projectId}");
        Assert.Equal(1, inventory.GetProperty("totalCount").GetInt32());
    }

    [Fact]
    public async Task Formal_scope_correction_is_audited_versioned_and_bound_to_review_snapshot()
    {
        using var factory = new AeroLinkApiFactory(); using var client = factory.CreateClient(); await ProblemReportApiTests.BootstrapAndLoginAsync(client);
        var scope = await SeedProjectAsync(factory);
        using var created = await client.PostAsJsonAsync("/api/managed-documents", new { projectId = scope.ProjectId, acronym = "SAS", documentType = "Software Accomplishment Summary", title = "Project accomplishment summary", ownerId = "software.author", changeSummary = "Initial formal scope." });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode); var createdBody = await created.Content.ReadFromJsonAsync<JsonElement>(); var documentId = createdBody.GetProperty("id").GetGuid(); var revisionId = createdBody.GetProperty("revisionId").GetGuid();
        using var beforeResponse = await client.GetAsync($"/api/managed-documents/{documentId}"); Assert.True(beforeResponse.IsSuccessStatusCode, await beforeResponse.Content.ReadAsStringAsync()); var before = await beforeResponse.Content.ReadFromJsonAsync<JsonElement>(); var version = before.GetProperty("revisions")[0].GetProperty("version").GetInt64();

        using var corrected = await client.PatchAsJsonAsync($"/api/managed-documents/revisions/{revisionId}/formal-summary", new { formalChangeSummary = "Reconcile the exact approved lifecycle evidence.", reason = "   Corrected the formal revision scope before review.   ", expectedVersion = version });
        Assert.Equal(HttpStatusCode.OK, corrected.StatusCode); var correction = await corrected.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(2, correction.GetProperty("formalSummaryVersion").GetInt64());
        using var stale = await client.PatchAsJsonAsync($"/api/managed-documents/revisions/{revisionId}/formal-summary", new { formalChangeSummary = "Stale overwrite.", reason = "Stale tab.", expectedVersion = version });
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        var search = await client.GetFromJsonAsync<JsonElement>($"/api/search?projectId={scope.ProjectId}&releaseId={scope.ReleasedId}&query=approved%20lifecycle%20evidence");
        Assert.Contains(search.GetProperty("items").EnumerateArray(), item => item.GetProperty("kind").GetString() == "managed-document" && item.GetProperty("title").GetString()!.Contains("Reconcile the exact approved lifecycle evidence."));
        using var authorClient = factory.CreateClient(); using (var authorLogin = await authorClient.PostAsJsonAsync("/api/auth/login", new { userName = "software.author", password = AeroLinkApiFactory.MemberPassword })) Assert.Equal(HttpStatusCode.OK, authorLogin.StatusCode);
        var myWork = await authorClient.GetFromJsonAsync<JsonElement>($"/api/my-work?projectId={scope.ProjectId}&releaseId={scope.ReleasedId}");
        Assert.Contains(myWork.GetProperty("tasks").EnumerateArray(), item => item.GetProperty("route").GetString() == "managedDocuments" && item.GetProperty("title").GetString() == "Reconcile the exact approved lifecycle evidence.");

        using var submitted = await client.PostAsJsonAsync($"/api/managed-documents/revisions/{revisionId}/submit", new { technicalReviewerId = "software.lead", finalApproverId = "quality.analyst" });
        Assert.Equal(HttpStatusCode.OK, submitted.StatusCode);
        using var blocked = await client.PatchAsJsonAsync($"/api/managed-documents/revisions/{revisionId}/formal-summary", new { formalChangeSummary = "Review bypass.", reason = "Must be refused.", expectedVersion = correction.GetProperty("version").GetInt64() + 1 });
        Assert.Equal(HttpStatusCode.Conflict, blocked.StatusCode);

        var detail = await client.GetFromJsonAsync<JsonElement>($"/api/managed-documents/{documentId}"); var revision = detail.GetProperty("revisions")[0];
        Assert.Equal("Reconcile the exact approved lifecycle evidence.", revision.GetProperty("formalChangeSummary").GetString());
        Assert.Equal(revision.GetProperty("formalSummaryHash").GetString(), revision.GetProperty("submittedFormalSummaryHash").GetString());
        Assert.Equal(2, revision.GetProperty("submittedFormalSummaryVersion").GetInt64());
        Assert.Contains(detail.GetProperty("audit").EnumerateArray(), item => item.GetProperty("eventType").GetString() == "DocumentFormalSummaryRevised" && item.GetProperty("detail").GetString()!.EndsWith("Reason: Corrected the formal revision scope before review."));
    }

    [Fact]
    public async Task Formal_scope_capability_is_server_derived_and_an_ended_owner_cannot_bypass_project_access()
    {
        using var factory = new AeroLinkApiFactory(); using var administrator = factory.CreateClient(); await ProblemReportApiTests.BootstrapAndLoginAsync(administrator);
        var scope = await SeedProjectAsync(factory);
        using var created = await administrator.PostAsJsonAsync("/api/managed-documents", new { projectId = scope.ProjectId, acronym = "ICD", documentType = "Interface Control Document", title = "Project interface control", ownerId = "software.lead", formalChangeSummary = "Control the Project interfaces." });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode); var createdBody = await created.Content.ReadFromJsonAsync<JsonElement>(); var documentId = createdBody.GetProperty("id").GetGuid(); var revisionId = createdBody.GetProperty("revisionId").GetGuid();
        var detail = await administrator.GetFromJsonAsync<JsonElement>($"/api/managed-documents/{documentId}"); var revision = detail.GetProperty("revisions")[0];
        Assert.Equal("software.lead", revision.GetProperty("ownerId").GetString());
        Assert.True(revision.GetProperty("canReviseFormalSummary").GetBoolean());
        var version = revision.GetProperty("version").GetInt64();

        using (var serviceScope = factory.Services.CreateScope())
        {
            var db = serviceScope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var account = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.SingleAsync(db.UserAccounts.Where(x => x.UserName == "software.lead"));
            var membership = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.SingleAsync(db.ProgramMemberships.Where(x => x.UserId == account.Id && x.EndedAt == null));
            membership.End("admin", DateTimeOffset.UtcNow); await db.SaveChangesAsync();
        }

        using var formerOwner = factory.CreateClient(); using (var login = await formerOwner.PostAsJsonAsync("/api/auth/login", new { userName = "software.lead", password = AeroLinkApiFactory.MemberPassword })) Assert.Equal(HttpStatusCode.OK, login.StatusCode); await SecurityBoundaryTests.AuthorizeMutationsAsync(formerOwner);
        using var refused = await formerOwner.PatchAsJsonAsync($"/api/managed-documents/revisions/{revisionId}/formal-summary", new { formalChangeSummary = "Unauthorized correction.", reason = "Membership ended.", expectedVersion = version });
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        var recovery = await administrator.GetFromJsonAsync<JsonElement>($"/api/my-work?projectId={scope.ProjectId}");
        Assert.Contains(recovery.GetProperty("tasks").EnumerateArray(), item => item.GetProperty("type").GetString() == "Project document owner recovery" && item.GetProperty("id").GetGuid() == documentId);
    }

    [Fact]
    public async Task Assignments_are_validated_and_stewardship_and_revision_responsibility_transfer_independently()
    {
        using var factory = new AeroLinkApiFactory(); using var administrator = factory.CreateClient(); await ProblemReportApiTests.BootstrapAndLoginAsync(administrator); var scope = await SeedProjectAsync(factory);
        using (var serviceScope = factory.Services.CreateScope())
        {
            var db = serviceScope.ServiceProvider.GetRequiredService<AeroLinkDbContext>(); var now = DateTimeOffset.UtcNow;
            var inactive = new UserAccount("inactive.author", "Inactive Author", "inactive@example.test", IdentityService.HashPassword(AeroLinkApiFactory.MemberPassword), now); inactive.Disable(now);
            var outsider = new UserAccount("other.author", "Other Program Author", "other@example.test", IdentityService.HashPassword(AeroLinkApiFactory.MemberPassword), now);
            var delegated = new UserAccount("delegated.author", "Delegated Author", "delegate@example.test", IdentityService.HashPassword(AeroLinkApiFactory.MemberPassword), now);
            var otherProgram = new ProgramRecord("Other Program", $"OT{Guid.NewGuid():N}"[..12]);
            var lead = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.SingleAsync(db.UserAccounts.Where(x => x.UserName == "software.lead"));
            db.AddRange(inactive, outsider, delegated, otherProgram,
                new ProgramMembership(inactive.Id, scope.ProgramId, ProgramRole.Engineer, "admin", now),
                new ProgramMembership(outsider.Id, otherProgram.Id, ProgramRole.Engineer, "admin", now),
                new RoleDelegation(scope.ProgramId, lead.Id, delegated.Id, ProgramRole.Engineer, now.AddMinutes(-1), now.AddDays(1), "Temporary document authoring coverage.", "admin", now));
            await db.SaveChangesAsync();
        }
        foreach (var invalidOwner in new[] { "missing.person", "inactive.author", "other.author", "admin" })
        {
            using var invalid = await administrator.PostAsJsonAsync("/api/managed-documents", new { projectId = scope.ProjectId, acronym = "SQAP", documentType = "Software Quality Assurance Plan", title = "Invalid assignment", ownerId = invalidOwner, formalChangeSummary = "Must not persist." });
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        }
        var emptyInventory = await administrator.GetFromJsonAsync<JsonElement>($"/api/managed-documents?projectId={scope.ProjectId}"); Assert.Equal(0, emptyInventory.GetProperty("totalCount").GetInt32());

        using var delegatedCreate = await administrator.PostAsJsonAsync("/api/managed-documents", new { projectId = scope.ProjectId, acronym = "PSAC", documentType = "Plan for Software Aspects of Certification", title = "Delegated plan", ownerId = "delegated.author", formalChangeSummary = "Delegated authoring authority is explicit." });
        Assert.Equal(HttpStatusCode.Created, delegatedCreate.StatusCode);

        using var created = await administrator.PostAsJsonAsync("/api/managed-documents", new { projectId = scope.ProjectId, acronym = "SQAP", documentType = "Software Quality Assurance Plan", title = "Controlled quality plan", ownerId = "software.lead", formalChangeSummary = "Establish Project quality controls." });
        Assert.True(created.StatusCode == HttpStatusCode.Created, await created.Content.ReadAsStringAsync()); var body = await created.Content.ReadFromJsonAsync<JsonElement>(); var documentId = body.GetProperty("id").GetGuid(); var revisionId = body.GetProperty("revisionId").GetGuid();
        var detail = await administrator.GetFromJsonAsync<JsonElement>($"/api/managed-documents/{documentId}"); var revision = detail.GetProperty("revisions")[0];
        Assert.Equal("software.lead", detail.GetProperty("stewardId").GetString()); Assert.Equal("admin", detail.GetProperty("createdBy").GetString());
        Assert.Equal("software.lead", revision.GetProperty("responsibleOwnerId").GetString()); Assert.Equal("admin", revision.GetProperty("initiatedBy").GetString());
        Assert.Equal("admin", revision.GetProperty("checkIns")[0].GetProperty("actorId").GetString());

        using var steward = await administrator.PatchAsJsonAsync($"/api/managed-documents/{documentId}/steward", new { assigneeId = "software.author", reason = "Transfer long-term plan accountability.", expectedVersion = detail.GetProperty("version").GetInt64() });
        Assert.Equal(HttpStatusCode.OK, steward.StatusCode);
        using var owner = await administrator.PatchAsJsonAsync($"/api/managed-documents/revisions/{revisionId}/responsible-owner", new { assigneeId = "software.author", reason = "Assign the active revision to its author.", expectedVersion = revision.GetProperty("version").GetInt64() });
        Assert.Equal(HttpStatusCode.OK, owner.StatusCode);
        using var stale = await administrator.PatchAsJsonAsync($"/api/managed-documents/revisions/{revisionId}/responsible-owner", new { assigneeId = "software.lead", reason = "Stale overwrite.", expectedVersion = revision.GetProperty("version").GetInt64() });
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);

        detail = await administrator.GetFromJsonAsync<JsonElement>($"/api/managed-documents/{documentId}"); revision = detail.GetProperty("revisions")[0];
        Assert.Equal("software.author", detail.GetProperty("stewardId").GetString()); Assert.Equal("software.author", revision.GetProperty("responsibleOwnerId").GetString());
        Assert.Equal("admin", revision.GetProperty("initiatedBy").GetString()); Assert.Equal("admin", revision.GetProperty("checkIns")[0].GetProperty("actorId").GetString());
        Assert.Equal(2, detail.GetProperty("assignments").GetArrayLength());
        Assert.Contains(detail.GetProperty("audit").EnumerateArray(), item => item.GetProperty("eventType").GetString() == "DocumentStewardReassigned");
        Assert.Contains(detail.GetProperty("audit").EnumerateArray(), item => item.GetProperty("eventType").GetString() == "DocumentRevisionOwnerReassigned");
        using var auditScope = factory.Services.CreateScope(); var auditDb = auditScope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        Assert.Equal(2, await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.CountAsync(auditDb.SecurityAuditEvents.Where(x => x.Target.Contains("SQAP-000001") && x.EventType.StartsWith("ManagedDocument"))));
    }

    [Fact]
    public async Task Reviewer_independence_uses_the_exact_immutable_contributor_set()
    {
        using var factory = new AeroLinkApiFactory(); using var administrator = factory.CreateClient(); await ProblemReportApiTests.BootstrapAndLoginAsync(administrator); var scope = await SeedProjectAsync(factory);
        using var created = await administrator.PostAsJsonAsync("/api/managed-documents", new { projectId = scope.ProjectId, acronym = "PSAC", documentType = "Plan for Software Aspects of Certification", title = "Certification plan", ownerId = "software.author", formalChangeSummary = "Update certification lifecycle evidence." });
        var body = await created.Content.ReadFromJsonAsync<JsonElement>(); var documentId = body.GetProperty("id").GetGuid(); var revisionId = body.GetProperty("revisionId").GetGuid();
        long revisionVersion;
        using (var serviceScope = factory.Services.CreateScope())
        {
            var db = serviceScope.ServiceProvider.GetRequiredService<AeroLinkDbContext>(); var files = serviceScope.ServiceProvider.GetRequiredService<ManagedDocumentFileService>(); var store = serviceScope.ServiceProvider.GetRequiredService<EvidenceFileStore>();
            var revision = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.SingleAsync(db.ManagedDocumentRevisions.Where(x => x.Id == revisionId)); var current = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.SingleAsync(db.ControlledAttachments.Where(x => x.Id == revision.CurrentWorkingAttachmentId));
            await using var source = store.OpenRead(current.StorageKey); using var buffer = new MemoryStream(); await source.CopyToAsync(buffer);
            var now = DateTimeOffset.UtcNow; var next = await files.StoreAsync(scope.ProjectId, documentId, revisionId, revisionId, 2, "Working Word document", "Technical contribution by the assigned author.", current.OriginalFileName, current.ContentType, buffer.ToArray(), current.Id, "software.author", now, default);
            current.Supersede(); db.ControlledAttachments.Add(next); revision.RecordCheckIn(next.Id, now); db.ManagedDocumentCheckIns.Add(new(revisionId, next.Id, 2, "software.author", "Technical contribution by the assigned author.", current.Id, current.Sha256, next.Sha256, current.Id, null, "test-author-contribution", now)); await db.SaveChangesAsync(); revisionVersion = revision.Version;
        }
        using var transfer = await administrator.PatchAsJsonAsync($"/api/managed-documents/revisions/{revisionId}/responsible-owner", new { assigneeId = "software.lead", reason = "Transfer completion responsibility without erasing the contributor.", expectedVersion = revisionVersion }); Assert.Equal(HttpStatusCode.OK, transfer.StatusCode);
        using var lead = factory.CreateClient(); using (var login = await lead.PostAsJsonAsync("/api/auth/login", new { userName = "software.lead", password = AeroLinkApiFactory.MemberPassword })) Assert.Equal(HttpStatusCode.OK, login.StatusCode); await SecurityBoundaryTests.AuthorizeMutationsAsync(lead);
        using var refused = await lead.PostAsJsonAsync($"/api/managed-documents/revisions/{revisionId}/submit", new { technicalReviewerId = "software.author", finalApproverId = "quality.analyst" });
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        using var accepted = await lead.PostAsJsonAsync($"/api/managed-documents/revisions/{revisionId}/submit", new { technicalReviewerId = "system.reviewer", finalApproverId = "quality.analyst" });
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        var detail = await lead.GetFromJsonAsync<JsonElement>($"/api/managed-documents/{documentId}"); var contributors = detail.GetProperty("revisions")[0].GetProperty("reviewContributors").EnumerateArray().ToList();
        Assert.Contains(contributors, item => item.GetProperty("contributorId").GetString() == "software.author" && item.GetProperty("provenance").GetString() == "AuthoritativeSubmissionSnapshot");
    }

    [Fact]
    public async Task Technical_and_release_signatures_bind_formal_summary_and_released_file_metadata()
    {
        using var factory = new AeroLinkApiFactory(); using var owner = factory.CreateClient(); await ProblemReportApiTests.BootstrapAndLoginAsync(owner); var scope = await SeedProjectAsync(factory);
        using var created = await owner.PostAsJsonAsync("/api/managed-documents", new { projectId = scope.ProjectId, acronym = "SDP", documentType = "Software Development Plan", title = "Signed project plan", ownerId = "software.author", changeSummary = "Authorize the exact formal lifecycle scope." });
        var createdBody = await created.Content.ReadFromJsonAsync<JsonElement>(); var documentId = createdBody.GetProperty("id").GetGuid(); var revisionId = createdBody.GetProperty("revisionId").GetGuid();
        using var submitted = await owner.PostAsJsonAsync($"/api/managed-documents/revisions/{revisionId}/submit", new { technicalReviewerId = "software.lead", finalApproverId = "quality.analyst" }); Assert.Equal(HttpStatusCode.OK, submitted.StatusCode);

        using var technical = factory.CreateClient(); using (var login = await technical.PostAsJsonAsync("/api/auth/login", new { userName = "software.lead", password = AeroLinkApiFactory.MemberPassword })) Assert.Equal(HttpStatusCode.OK, login.StatusCode); await SecurityBoundaryTests.AuthorizeMutationsAsync(technical);
        using var technicalApproval = await technical.PostAsJsonAsync($"/api/managed-documents/revisions/{revisionId}/review/approve", new { password = AeroLinkApiFactory.MemberPassword, meaning = "I confirm the technical review is complete.", rationale = "Formal scope and exact working snapshot are technically complete." }); Assert.Equal(HttpStatusCode.OK, technicalApproval.StatusCode);
        var afterTechnical = await owner.GetFromJsonAsync<JsonElement>($"/api/managed-documents/{documentId}"); var technicalVersion = afterTechnical.GetProperty("revisions")[0].GetProperty("version").GetInt64();
        using var technicalEdit = await owner.PatchAsJsonAsync($"/api/managed-documents/revisions/{revisionId}/formal-summary", new { formalChangeSummary = "Forbidden after approval.", reason = "Must fail.", expectedVersion = technicalVersion }); Assert.Equal(HttpStatusCode.Conflict, technicalEdit.StatusCode);

        using var quality = factory.CreateClient(); using (var login = await quality.PostAsJsonAsync("/api/auth/login", new { userName = "quality.analyst", password = AeroLinkApiFactory.MemberPassword })) Assert.Equal(HttpStatusCode.OK, login.StatusCode); await SecurityBoundaryTests.AuthorizeMutationsAsync(quality);
        using var preparation = await quality.PostAsync($"/api/managed-documents/revisions/{revisionId}/release-preparation", null); Assert.Equal(HttpStatusCode.OK, preparation.StatusCode); var preparationBody = await preparation.Content.ReadFromJsonAsync<JsonElement>(); var ticket = Query(new Uri(preparationBody.GetProperty("launchUri").GetString()!))["ticket"];
        using var redeemed = await quality.PostAsync($"/api/document-connector/redeem/{Uri.EscapeDataString(ticket)}", null); var grant = await redeemed.Content.ReadFromJsonAsync<JsonElement>(); var grantId = grant.GetProperty("id").GetGuid(); var token = grant.GetProperty("accessToken").GetString()!; var sessionVersion = grant.GetProperty("sessionVersion").GetInt64();
        var now = DateTimeOffset.UtcNow; var publication = new ProfessionalPublication("AeroLink", "Program", "Project", "Software Development Plan", "Signed project plan", "Controlled Project document", "SDP-000001", "00", "Released", "Project-wide", "All software builds", "admin", now, new string('c', 64), [("Formal revision scope", "Authorize the exact formal lifecycle scope.")], [], [], [new("Purpose", "Scope", [new("1", "Plan", "Purpose", "Controlled content.", [])])]);
        var docx = ProfessionalPublicationRenderer.Render(publication, "docx", "SDP-000001.00"); var pdf = ProfessionalPublicationRenderer.Render(publication, "pdf", "SDP-000001.00");
        using var candidateForm = new MultipartFormDataContent(); candidateForm.Add(new StringContent(sessionVersion.ToString()), "expectedVersion"); var docxPart = new ByteArrayContent(docx.Content); docxPart.Headers.ContentType = new(docx.ContentType); candidateForm.Add(docxPart, "docx", docx.FileName); var pdfPart = new ByteArrayContent(pdf.Content); pdfPart.Headers.ContentType = new(pdf.ContentType); candidateForm.Add(pdfPart, "pdf", pdf.FileName);
        using var candidateRequest = new HttpRequestMessage(HttpMethod.Post, $"/api/document-connector/{grantId}/release-candidate") { Content = candidateForm }; candidateRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token); using var candidate = await quality.SendAsync(candidateRequest); Assert.Equal(HttpStatusCode.OK, candidate.StatusCode); var candidateBody = await candidate.Content.ReadFromJsonAsync<JsonElement>();
        using var release = await quality.PostAsJsonAsync($"/api/managed-documents/revisions/{revisionId}/review/approve", new { password = AeroLinkApiFactory.MemberPassword, meaning = "I authorize this exact controlled release.", rationale = "Exact DOCX/PDF candidate and formal scope are conforming." }); Assert.Equal(HttpStatusCode.OK, release.StatusCode);

        var detail = await owner.GetFromJsonAsync<JsonElement>($"/api/managed-documents/{documentId}"); var revision = detail.GetProperty("revisions")[0]; var expectedManifest = ManagedDocumentFileService.Sha256(System.Text.Encoding.UTF8.GetBytes($"{candidateBody.GetProperty("docxSha256").GetString()}:{candidateBody.GetProperty("pdfSha256").GetString()}:{revision.GetProperty("formalSummaryHash").GetString()}:{revision.GetProperty("formalSummaryVersion").GetInt64()}"));
        Assert.Equal(expectedManifest, revision.GetProperty("releaseManifestHash").GetString());
        Assert.All(revision.GetProperty("attachments").EnumerateArray().Where(item => item.GetProperty("id").GetGuid() == revision.GetProperty("releasedDocxAttachmentId").GetGuid() || item.GetProperty("id").GetGuid() == revision.GetProperty("releasedPdfAttachmentId").GetGuid()), item => Assert.Contains("Formal revision scope v1", item.GetProperty("description").GetString()));
        Assert.Contains(detail.GetProperty("signatures").EnumerateArray(), item => item.GetProperty("action").GetString() == "Approve" && item.GetProperty("contentHash").GetString() == revision.GetProperty("snapshotHash").GetString());
        Assert.Contains(detail.GetProperty("signatures").EnumerateArray(), item => item.GetProperty("action").GetString() == "Release" && item.GetProperty("contentHash").GetString() == expectedManifest);
        using var releasedEdit = await owner.PatchAsJsonAsync($"/api/managed-documents/revisions/{revisionId}/formal-summary", new { formalChangeSummary = "Forbidden after release.", reason = "Must fail.", expectedVersion = revision.GetProperty("version").GetInt64() }); Assert.Equal(HttpStatusCode.Conflict, releasedEdit.StatusCode);
    }

    [Fact]
    public async Task Successor_is_bound_to_the_verified_released_docx_and_missing_parent_fails_closed()
    {
        using var factory = new AeroLinkApiFactory(); using var client = factory.CreateClient(); await ProblemReportApiTests.BootstrapAndLoginAsync(client);
        var scope = await SeedProjectAsync(factory); var seeded = await SeedReleasedDocumentAsync(factory, scope.ProjectId);

        using var started = await client.PostAsJsonAsync($"/api/managed-documents/{seeded.DocumentId}/revisions", new { ownerId = "software.author", changeSummary = "Update the Project plan." });
        Assert.True(started.StatusCode == HttpStatusCode.Created, await started.Content.ReadAsStringAsync());
        var detail = await client.GetFromJsonAsync<JsonElement>($"/api/managed-documents/{seeded.DocumentId}");
        var successor = detail.GetProperty("revisions").EnumerateArray().Single(x => x.GetProperty("revision").GetInt32() == 1);
        Assert.Equal(seeded.RevisionId, successor.GetProperty("parentRevisionId").GetGuid());
        Assert.Equal(seeded.ReleasedDocxId, successor.GetProperty("parentReleasedDocxAttachmentId").GetGuid());
        Assert.Equal(seeded.ReleasedDocxSha256, successor.GetProperty("parentReleasedDocxSha256").GetString());
        Assert.Equal(ManagedDocumentFileService.SuccessorTransformationProfile, successor.GetProperty("transformationProfile").GetString());

        var releasedSuccessor = await ReleaseSuccessorForTestAsync(factory, seeded.DocumentId, successor.GetProperty("id").GetGuid());
        using var startedAgain = await client.PostAsJsonAsync($"/api/managed-documents/{seeded.DocumentId}/revisions", new { ownerId = "software.author", changeSummary = "Second Project update." });
        Assert.Equal(HttpStatusCode.Created, startedAgain.StatusCode);
        var sequential = await client.GetFromJsonAsync<JsonElement>($"/api/managed-documents/{seeded.DocumentId}");
        var revision02 = sequential.GetProperty("revisions").EnumerateArray().Single(x => x.GetProperty("revision").GetInt32() == 2);
        Assert.Equal(releasedSuccessor.RevisionId, revision02.GetProperty("parentRevisionId").GetGuid());
        Assert.Equal(releasedSuccessor.DocxId, revision02.GetProperty("parentReleasedDocxAttachmentId").GetGuid());
        Assert.Equal(releasedSuccessor.Sha256, revision02.GetProperty("parentReleasedDocxSha256").GetString());

        var second = await SeedReleasedDocumentAsync(factory, scope.ProjectId, "SQAP");
        using (var serviceScope = factory.Services.CreateScope()) serviceScope.ServiceProvider.GetRequiredService<EvidenceFileStore>().Delete(second.StorageKey);
        using var refused = await client.PostAsJsonAsync($"/api/managed-documents/{second.DocumentId}/revisions", new { ownerId = "software.author", changeSummary = "Must not persist." });
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
            firstClient.PostAsJsonAsync($"/api/managed-documents/{seeded.DocumentId}/revisions", new { ownerId = "software.author", changeSummary = "Concurrent successor A." }),
            secondClient.PostAsJsonAsync($"/api/managed-documents/{seeded.DocumentId}/revisions", new { ownerId = "software.author", changeSummary = "Concurrent successor B." }));
        Assert.Single(requests, x => x.StatusCode == HttpStatusCode.Created);
        Assert.Single(requests, x => x.StatusCode == HttpStatusCode.Conflict);
        using var verificationScope = factory.Services.CreateScope(); var db = verificationScope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        Assert.Equal(2, await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.CountAsync(db.ManagedDocumentRevisions.Where(x => x.DocumentId == seeded.DocumentId)));
    }

    private static async Task<Guid> SeedProjectWithoutBuildsAsync(AeroLinkApiFactory factory)
    {
        using var scope = factory.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        var program = new ProgramRecord("Document Program", $"DZ{Guid.NewGuid():N}"[..12]); var project = new ProjectRecord(program.Id, "Build-free Product", "Project Documentation"); var now = DateTimeOffset.UtcNow;
        var author = new UserAccount("buildfree.author", "Build-free Author", "buildfree@example.test", IdentityService.HashPassword(AeroLinkApiFactory.MemberPassword), now);
        db.AddRange(program, project, author, new ProgramMembership(author.Id, program.Id, ProgramRole.Engineer, "admin", now)); await db.SaveChangesAsync(); return project.Id;
    }

    private static async Task<(Guid DocumentId, Guid RevisionId, Guid ReleasedDocxId, string ReleasedDocxSha256, string StorageKey)> SeedReleasedDocumentAsync(AeroLinkApiFactory factory, Guid projectId, string acronym = "SCMP")
    {
        using var scope = factory.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>(); var files = scope.ServiceProvider.GetRequiredService<ManagedDocumentFileService>();
        var now = DateTimeOffset.UtcNow; var document = new ManagedDocument(projectId, $"{acronym}-000001", acronym, "Project Plan", $"{acronym} Project Plan", "admin", now); var revision = new ManagedDocumentRevision(document.Id, 0, "admin", "Initial controlled Project issue.", now);
        var publication = new ProfessionalPublication("AeroLink", "Program", "Project", "Project Plan", document.Title, "Controlled Project document", document.DocumentNumber, "00", "Draft", "Project-wide", "All software builds", "admin", now, new string('a', 64), [], [], [], [new("Purpose", "Scope", [new("1", "Plan", "Purpose", "Controlled content.", [])])]) { Watermark = "DRAFT" };
        var draft = ProfessionalPublicationRenderer.Render(publication, "docx", $"{document.DocumentNumber}.00"); var draftAttachment = await files.StoreAsync(projectId, document.Id, revision.Id, revision.Id, 1, "Working Word document", "Initial draft.", draft.FileName, draft.ContentType, draft.Content, null, "admin", now, default); revision.RecordCheckIn(draftAttachment.Id, now);
        var cycle = revision.SubmitForReview("admin", draftAttachment.Sha256, [new("software.lead", "Rina Shah", "Technical"), new("quality.analyst", "Maya Patel", "Final")], now); revision.Approve("software.lead", "Complete.", now);
        var releasedPublication = publication with { Status = "Released", Watermark = null }; var docx = ProfessionalPublicationRenderer.Render(releasedPublication, "docx", $"{document.DocumentNumber}.00"); var pdf = ProfessionalPublicationRenderer.Render(releasedPublication, "pdf", $"{document.DocumentNumber}.00");
        var releasedDocx = await files.StoreAsync(projectId, document.Id, revision.Id, Guid.NewGuid(), 1, "Released DOCX", "Immutable source.", docx.FileName, docx.ContentType, docx.Content, null, "quality.analyst", now, default); var releasedPdf = await files.StoreAsync(projectId, document.Id, revision.Id, Guid.NewGuid(), 1, "Released PDF", "Immutable rendition.", pdf.FileName, pdf.ContentType, pdf.Content, null, "quality.analyst", now, default);
        revision.RecordReleaseCandidate(releasedDocx.Id, releasedPdf.Id, ManagedDocumentFileService.Sha256(System.Text.Encoding.UTF8.GetBytes($"{releasedDocx.Sha256}:{releasedPdf.Sha256}:{revision.FormalSummaryHash}:{revision.FormalSummaryVersion}")), "quality.analyst", now); revision.Approve("quality.analyst", "Release.", now);
        db.AddRange(document, revision, draftAttachment, releasedDocx, releasedPdf);
        db.ManagedDocumentCheckIns.Add(new(revision.Id, draftAttachment.Id, 1, "admin", "Initial draft.", null, null, draftAttachment.Sha256, null, null, $"test-seed:{revision.Id:N}", now));
        db.ManagedDocumentReviewSteps.AddRange(revision.ReviewSteps.Where(x => x.Cycle == cycle)); await db.SaveChangesAsync();
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
        revision.RecordReleaseCandidate(releasedDocx.Id, releasedPdf.Id, ManagedDocumentFileService.Sha256(System.Text.Encoding.UTF8.GetBytes($"{releasedDocx.Sha256}:{releasedPdf.Sha256}:{revision.FormalSummaryHash}:{revision.FormalSummaryVersion}")), "quality.analyst", now); revision.Approve("quality.analyst", "Release.", now);
        var oldHead = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.SingleAsync(db.ManagedDocumentRevisions.Where(x => x.DocumentId == documentId && x.Id != revisionId && x.State == ManagedDocumentState.Released)); oldHead.Supersede(now);
        db.AddRange(releasedDocx, releasedPdf); db.ManagedDocumentReviewSteps.AddRange(revision.ReviewSteps.Where(x => x.Cycle == cycle)); await db.SaveChangesAsync(); return (revision.Id, releasedDocx.Id, releasedDocx.Sha256);
    }

    private static async Task<(Guid ProgramId, Guid ProjectId, Guid ReleasedId, Guid ActiveReleaseId)> SeedProjectAsync(AeroLinkApiFactory factory)
    {
        using var scope = factory.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>(); var program = new ProgramRecord("Document Program", $"DC{Guid.NewGuid():N}"[..12]); var project = new ProjectRecord(program.Id, "Navigation Product", "Navigation Software"); var released = new SoftwareRelease(project.Id, "1.5", true); var active = new SoftwareRelease(project.Id, "1.6", false, released.Id); var now = DateTimeOffset.UtcNow;
        var technical = new UserAccount("software.lead", "Rina Shah", "software.lead@example.test", IdentityService.HashPassword(AeroLinkApiFactory.MemberPassword), now); var quality = new UserAccount("quality.analyst", "Maya Patel", "quality.analyst@example.test", IdentityService.HashPassword(AeroLinkApiFactory.MemberPassword), now); var author = new UserAccount("software.author", "Ethan Brooks", "software.author@example.test", IdentityService.HashPassword(AeroLinkApiFactory.MemberPassword), now); var reviewer = new UserAccount("system.reviewer", "Olivia Chen", "system.reviewer@example.test", IdentityService.HashPassword(AeroLinkApiFactory.MemberPassword), now);
        db.AddRange(program, project, released, active, technical, quality, author, reviewer, new ProgramMembership(technical.Id, program.Id, ProgramRole.SoftwareEngineeringLead, "admin", now), new ProgramMembership(quality.Id, program.Id, ProgramRole.SoftwareQualityAnalyst, "admin", now), new ProgramMembership(author.Id, program.Id, ProgramRole.SoftwareEngineer, "admin", now), new ProgramMembership(author.Id, program.Id, ProgramRole.Reviewer, "admin", now), new ProgramMembership(reviewer.Id, program.Id, ProgramRole.Reviewer, "admin", now)); await db.SaveChangesAsync(); return (program.Id, project.Id, released.Id, active.Id);
    }
    private static Dictionary<string,string> Query(Uri uri) => uri.Query.TrimStart('?').Split('&').Select(part => part.Split('=', 2)).ToDictionary(pair => pair[0], pair => Uri.UnescapeDataString(pair[1]));
}
