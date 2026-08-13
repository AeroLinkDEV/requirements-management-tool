using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AeroLink.ConnectorProtocol;
using AeroLink.Domain.Identity;
using AeroLink.Domain.Programs;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AeroLink.Api.Tests;

public sealed class ManagedDocumentRecoveryApiTests
{
    [Fact]
    public async Task Recovery_reauthenticates_exact_workspace_rotates_session_and_fails_closed_after_source_change()
    {
        using var factory = new AeroLinkApiFactory(); using var administrator = factory.CreateClient();
        await ProblemReportApiTests.BootstrapAndLoginAsync(administrator); await SecurityBoundaryTests.AuthorizeMutationsAsync(administrator);
        var seeded = await SeedAsync(factory);
        using var owner = factory.CreateClient(); await LoginAsync(owner, "recovery.author");
        using var ownerSecondTab = factory.CreateClient(); await LoginAsync(ownerSecondTab, "recovery.author");
        using var other = factory.CreateClient(); await LoginAsync(other, "recovery.other");
        using var technical = factory.CreateClient(); await LoginAsync(technical, "recovery.technical");
        using var quality = factory.CreateClient(); await LoginAsync(quality, "recovery.quality");
        using var enrollmentResponse = await owner.PostAsync($"/api/managed-documents/connector-enrollment?projectId={seeded.ProjectId}", null);
        var enrollment = await enrollmentResponse.Content.ReadFromJsonAsync<ConnectorEnrollmentManifest>(); Assert.NotNull(enrollment);
        using var created = await owner.PostAsJsonAsync("/api/managed-documents", new { projectId = seeded.ProjectId, acronym = "SDP", documentType = "Software Development Plan", title = "Recoverable plan", ownerId = "recovery.author", formalChangeSummary = "Exercise protected local recovery.", operationKey = Guid.NewGuid().ToString("N") });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode); var createdBody = await created.Content.ReadFromJsonAsync<JsonElement>();
        var documentId = createdBody.GetProperty("id").GetGuid(); var revisionId = createdBody.GetProperty("revisionId").GetGuid();

        var original = await CheckoutAsync(owner, revisionId, enrollment);
        Assert.Equal(original.SessionId, original.Envelope.EditSessionId); Assert.Null(original.Envelope.RecoveryWorkspaceId);
        using var activeRecovery = await owner.PostAsJsonAsync($"/api/managed-documents/revisions/{revisionId}/recovery", new { workspaceId = original.GrantId });
        Assert.Equal(HttpStatusCode.OK, activeRecovery.StatusCode); var activeRecoveryBody = await activeRecovery.Content.ReadFromJsonAsync<JsonElement>();
        var activeRecoveryEnvelope = VerifyLaunch(activeRecoveryBody.GetProperty("launchUri").GetString()!, enrollment);
        Assert.Equal(original.SessionId, activeRecoveryEnvelope.EditSessionId); Assert.Equal(original.GrantId, activeRecoveryEnvelope.RecoveryWorkspaceId);
        Assert.InRange(activeRecoveryBody.GetProperty("expiresAt").GetDateTimeOffset(), DateTimeOffset.UtcNow.AddMinutes(14), DateTimeOffset.UtcNow.AddMinutes(16));
        using var revokedOriginalLaunch = await owner.PostAsync($"/api/document-connector/redeem/{Uri.EscapeDataString(original.Envelope.Nonce)}", null);
        Assert.Equal(HttpStatusCode.BadRequest, revokedOriginalLaunch.StatusCode);
        using var force = await administrator.PostAsJsonAsync($"/api/managed-documents/revisions/{revisionId}/force-unlock", new { reason = "Simulate a crashed desktop connector." });
        Assert.Equal(HttpStatusCode.NoContent, force.StatusCode);

        using var forbidden = await other.PostAsJsonAsync($"/api/managed-documents/revisions/{revisionId}/recovery", new { workspaceId = original.GrantId });
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        var concurrentRecovery = await Task.WhenAll(
            owner.PostAsJsonAsync($"/api/managed-documents/revisions/{revisionId}/recovery", new { workspaceId = original.GrantId }),
            ownerSecondTab.PostAsJsonAsync($"/api/managed-documents/revisions/{revisionId}/recovery", new { workspaceId = original.GrantId }));
        Assert.Single(concurrentRecovery, response => response.StatusCode == HttpStatusCode.OK);
        Assert.Single(concurrentRecovery, response => response.StatusCode == HttpStatusCode.Conflict);
        using var recovered = concurrentRecovery.Single(response => response.StatusCode == HttpStatusCode.OK);
        using var recoveryConflict = concurrentRecovery.Single(response => response.StatusCode == HttpStatusCode.Conflict);
        Assert.Contains((await recoveryConflict.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString(),
            new[] { "document_recovery_already_issued", "document_recovery_lock_conflict" });
        var recoveredBody = await recovered.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("recoverable", recoveredBody.GetProperty("status").GetString());
        var recoveredEnvelope = VerifyLaunch(recoveredBody.GetProperty("launchUri").GetString()!, enrollment);
        Assert.Equal(original.GrantId, recoveredEnvelope.RecoveryWorkspaceId); Assert.NotEqual(original.SessionId, recoveredEnvelope.EditSessionId);
        Assert.InRange(recoveredBody.GetProperty("expiresAt").GetDateTimeOffset(), DateTimeOffset.UtcNow.AddMinutes(14), DateTimeOffset.UtcNow.AddMinutes(16));

        using var discard = await owner.PostAsJsonAsync($"/api/managed-documents/revisions/{revisionId}/recovery/discard", new { workspaceId = original.GrantId });
        Assert.Equal(HttpStatusCode.OK, discard.StatusCode); var discardBody = await discard.Content.ReadFromJsonAsync<JsonElement>();
        var discardEnvelope = VerifyLaunch(discardBody.GetProperty("launchUri").GetString()!, enrollment);
        Assert.Equal("discard", discardEnvelope.Mode); Assert.Equal(original.GrantId, discardEnvelope.RecoveryWorkspaceId);

        var stranded = await CheckoutAsync(owner, revisionId, enrollment);
        using var strandedRedeemed = await owner.PostAsync($"/api/document-connector/redeem/{Uri.EscapeDataString(stranded.Envelope.Nonce)}", null);
        var strandedGrant = await strandedRedeemed.Content.ReadFromJsonAsync<JsonElement>();
        var baseBytes = await DownloadAsync(owner, strandedGrant);
        using var forceStranded = await administrator.PostAsJsonAsync($"/api/managed-documents/revisions/{revisionId}/force-unlock", new { reason = "Allow a competing checked-in version." });
        Assert.Equal(HttpStatusCode.NoContent, forceStranded.StatusCode);

        var winner = await CheckoutAsync(owner, revisionId, enrollment);
        using var winnerRedeemed = await owner.PostAsync($"/api/document-connector/redeem/{Uri.EscapeDataString(winner.Envelope.Nonce)}", null);
        var winnerGrant = await winnerRedeemed.Content.ReadFromJsonAsync<JsonElement>();
        using var checkedIn = await CheckInAsync(owner, winnerGrant, baseBytes); Assert.Equal(HttpStatusCode.OK, checkedIn.StatusCode);
        using var completedRecovery = await owner.PostAsJsonAsync($"/api/managed-documents/revisions/{revisionId}/recovery", new { workspaceId = winner.GrantId });
        Assert.Equal(HttpStatusCode.OK, completedRecovery.StatusCode); var completedBody = await completedRecovery.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("completed", completedBody.GetProperty("status").GetString()); var cleanupEnvelope = VerifyLaunch(completedBody.GetProperty("launchUri").GetString()!, enrollment);
        Assert.Equal("cleanup", cleanupEnvelope.Mode); Assert.Equal(winner.GrantId, cleanupEnvelope.RecoveryWorkspaceId); Assert.Contains("sha256", cleanupEnvelope.CompletionEvidenceJson);

        using var staleRecovery = await owner.PostAsJsonAsync($"/api/managed-documents/revisions/{revisionId}/recovery", new { workspaceId = stranded.GrantId });
        Assert.Equal(HttpStatusCode.Conflict, staleRecovery.StatusCode); var staleBody = await staleRecovery.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("document_recovery_source_changed", staleBody.GetProperty("code").GetString());
        using var foreign = await owner.PostAsJsonAsync($"/api/managed-documents/revisions/{revisionId}/recovery", new { workspaceId = Guid.NewGuid() });
        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);

        using var submitted = await SubmitAsync(owner, documentId, revisionId); Assert.Equal(HttpStatusCode.OK, submitted.StatusCode);
        using var advancedRecovery = await owner.PostAsJsonAsync($"/api/managed-documents/revisions/{revisionId}/recovery", new { workspaceId = stranded.GrantId });
        Assert.Equal(HttpStatusCode.Conflict, advancedRecovery.StatusCode);
        Assert.Equal("document_recovery_revision_advanced", (await advancedRecovery.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        using var technicalApproval = await DecideAsync(technical, documentId, revisionId); Assert.Equal(HttpStatusCode.OK, technicalApproval.StatusCode);
        using var preparation = await quality.PostAsync($"/api/managed-documents/revisions/{revisionId}/release-preparation", null);
        Assert.Equal(HttpStatusCode.OK, preparation.StatusCode); var preparationBody = await preparation.Content.ReadFromJsonAsync<JsonElement>();
        var releaseGrantId = preparationBody.GetProperty("grantId").GetGuid(); var releaseSessionId = preparationBody.GetProperty("sessionId").GetGuid();
        using var forceRelease = await administrator.PostAsJsonAsync($"/api/managed-documents/revisions/{revisionId}/force-unlock", new { reason = "Simulate a release-rendering workstation crash." });
        Assert.Equal(HttpStatusCode.NoContent, forceRelease.StatusCode);
        using var ownerCannotRecoverRelease = await owner.PostAsJsonAsync($"/api/managed-documents/revisions/{revisionId}/recovery", new { workspaceId = releaseGrantId });
        Assert.Equal(HttpStatusCode.Forbidden, ownerCannotRecoverRelease.StatusCode);
        using var recoveredRelease = await quality.PostAsJsonAsync($"/api/managed-documents/revisions/{revisionId}/recovery", new { workspaceId = releaseGrantId });
        Assert.Equal(HttpStatusCode.OK, recoveredRelease.StatusCode); var recoveredReleaseBody = await recoveredRelease.Content.ReadFromJsonAsync<JsonElement>();
        var releaseEnvelope = VerifyLaunch(recoveredReleaseBody.GetProperty("launchUri").GetString()!, enrollment);
        Assert.Equal("release", releaseEnvelope.Mode); Assert.Equal(releaseGrantId, releaseEnvelope.RecoveryWorkspaceId);
        Assert.NotEqual(releaseSessionId, releaseEnvelope.EditSessionId);

        using var scope = factory.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        Assert.Contains(await db.ManagedDocumentEvents.Where(x => x.DocumentId == documentId).ToListAsync(), x => x.EventType == "DocumentCheckoutRecovered");
        Assert.Contains(await db.ManagedDocumentEvents.Where(x => x.DocumentId == documentId).ToListAsync(), x => x.EventType == "DocumentRecoveryDiscarded");
    }

    private static async Task<(Guid GrantId, Guid SessionId, ConnectorLaunchEnvelope Envelope)> CheckoutAsync(HttpClient client, Guid revisionId, ConnectorEnrollmentManifest enrollment)
    {
        using var response = await client.PostAsync($"/api/managed-documents/revisions/{revisionId}/checkout", null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode); var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return (body.GetProperty("grantId").GetGuid(), body.GetProperty("sessionId").GetGuid(), VerifyLaunch(body.GetProperty("launchUri").GetString()!, enrollment));
    }

    private static ConnectorLaunchEnvelope VerifyLaunch(string launchUri, ConnectorEnrollmentManifest enrollment)
    {
        var uri = new Uri(launchUri); var value = Uri.UnescapeDataString(uri.Query.TrimStart('?').Split('=', 2)[1]);
        return ConnectorLaunchProtocol.Verify(value, enrollment.PublicKey, DateTimeOffset.UtcNow);
    }

    private static async Task<byte[]> DownloadAsync(HttpClient client, JsonElement grant)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/document-connector/{grant.GetProperty("id").GetGuid()}/download");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", grant.GetProperty("accessToken").GetString());
        using var response = await client.SendAsync(request); Assert.Equal(HttpStatusCode.OK, response.StatusCode); return await response.Content.ReadAsByteArrayAsync();
    }

    private static async Task<HttpResponseMessage> CheckInAsync(HttpClient client, JsonElement grant, byte[] content)
    {
        var form = new MultipartFormDataContent(); form.Add(new StringContent("Recovered competitor check-in."), "comment");
        form.Add(new StringContent(grant.GetProperty("sessionVersion").GetInt64().ToString()), "expectedVersion");
        var file = new ByteArrayContent(content); file.Headers.ContentType = new("application/vnd.openxmlformats-officedocument.wordprocessingml.document"); form.Add(file, "file", "SDP-000001.00.docx");
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/document-connector/{grant.GetProperty("id").GetGuid()}/check-in") { Content = form };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", grant.GetProperty("accessToken").GetString()); return await client.SendAsync(request);
    }

    private static async Task<HttpResponseMessage> SubmitAsync(HttpClient client, Guid documentId, Guid revisionId)
    {
        var detail = await client.GetFromJsonAsync<JsonElement>($"/api/managed-documents/{documentId}");
        var revision = detail.GetProperty("revisions").EnumerateArray().Single(x => x.GetProperty("id").GetGuid() == revisionId);
        var workingId = revision.GetProperty("currentWorkingAttachmentId").GetGuid();
        var working = revision.GetProperty("attachments").EnumerateArray().Single(x => x.GetProperty("id").GetGuid() == workingId);
        return await client.PostAsJsonAsync($"/api/managed-documents/revisions/{revisionId}/submit", new
        {
            technicalReviewerId = "recovery.technical", finalApproverId = "recovery.quality",
            expectedVersion = revision.GetProperty("version").GetInt64(), expectedWorkingAttachmentId = workingId,
            expectedWorkingSha256 = working.GetProperty("sha256").GetString(),
            expectedFormalSummaryVersion = revision.GetProperty("formalSummaryVersion").GetInt64(),
            expectedFormalSummaryHash = revision.GetProperty("formalSummaryHash").GetString(),
            expectedRelationshipManifestHash = revision.GetProperty("currentRelationshipManifestHash").GetString(),
            operationKey = Guid.NewGuid().ToString("N")
        });
    }

    private static async Task<HttpResponseMessage> DecideAsync(HttpClient client, Guid documentId, Guid revisionId)
    {
        var detail = await client.GetFromJsonAsync<JsonElement>($"/api/managed-documents/{documentId}");
        var revision = detail.GetProperty("revisions").EnumerateArray().Single(x => x.GetProperty("id").GetGuid() == revisionId);
        var step = revision.GetProperty("reviewSteps").EnumerateArray().Single(x => x.GetProperty("state").GetString() == "Active");
        return await client.PostAsJsonAsync($"/api/managed-documents/revisions/{revisionId}/review/approve", new
        {
            password = AeroLinkApiFactory.MemberPassword, meaning = "Technical recovery qualification",
            rationale = "The exact submitted working file and controlled scope are acceptable.",
            expectedVersion = revision.GetProperty("version").GetInt64(),
            expectedCycle = revision.GetProperty("currentReviewCycle").GetInt32(),
            expectedStepId = step.GetProperty("id").GetGuid(), expectedStepVersion = step.GetProperty("version").GetInt64(),
            expectedSnapshotHash = revision.GetProperty("snapshotHash").GetString(),
            expectedCandidateDocxAttachmentId = (Guid?)null, expectedCandidatePdfAttachmentId = (Guid?)null,
            expectedCandidateManifestHash = (string?)null, operationKey = Guid.NewGuid().ToString("N")
        });
    }

    private static async Task LoginAsync(HttpClient client, string userName)
    {
        using var login = await client.PostAsJsonAsync("/api/auth/login", new { userName, password = AeroLinkApiFactory.MemberPassword });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode); await SecurityBoundaryTests.AuthorizeMutationsAsync(client);
    }

    private static async Task<(Guid ProgramId, Guid ProjectId)> SeedAsync(AeroLinkApiFactory factory)
    {
        using var scope = factory.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>(); var now = DateTimeOffset.UtcNow;
        var program = new ProgramRecord("Recovery Program", $"RC{Guid.NewGuid():N}"[..12]); var project = new ProjectRecord(program.Id, "Recovery Project", "Project-wide documents");
        var owner = new UserAccount("recovery.author", "Recovery Author", "recovery.author@example.test", IdentityService.HashPassword(AeroLinkApiFactory.MemberPassword), now);
        var other = new UserAccount("recovery.other", "Other Engineer", "recovery.other@example.test", IdentityService.HashPassword(AeroLinkApiFactory.MemberPassword), now);
        var technical = new UserAccount("recovery.technical", "Recovery Technical Reviewer", "recovery.technical@example.test", IdentityService.HashPassword(AeroLinkApiFactory.MemberPassword), now);
        var quality = new UserAccount("recovery.quality", "Recovery Quality Approver", "recovery.quality@example.test", IdentityService.HashPassword(AeroLinkApiFactory.MemberPassword), now);
        db.AddRange(program, project, owner, other, technical, quality,
            new ProgramMembership(owner.Id, program.Id, ProgramRole.Engineer, "admin", now),
            new ProgramMembership(other.Id, program.Id, ProgramRole.Engineer, "admin", now),
            new ProgramMembership(technical.Id, program.Id, ProgramRole.SoftwareEngineeringLead, "admin", now),
            new ProgramMembership(quality.Id, program.Id, ProgramRole.SoftwareQualityAnalyst, "admin", now));
        await db.SaveChangesAsync(); return (program.Id, project.Id);
    }
}
