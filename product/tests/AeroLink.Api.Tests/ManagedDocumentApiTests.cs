using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AeroLink.ConnectorProtocol;
using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Documents;
using AeroLink.Domain.Identity;
using AeroLink.Domain.Programs;
using AeroLink.Domain.Requirements;
using AeroLink.Domain.Verification;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace AeroLink.Api.Tests;

public sealed class ManagedDocumentApiTests
{
    [Fact]
    public async Task Relationships_are_canonical_authorized_project_scoped_review_bound_and_hashed()
    {
        using var factory = new AeroLinkApiFactory(); using var administrator = factory.CreateClient(); await ProblemReportApiTests.BootstrapAndLoginAsync(administrator);
        var scope = await SeedProjectAsync(factory); Guid releasedChangeId, activeChangeId, foreignChangeId, reportId, testChangeId;
        using (var serviceScope = factory.Services.CreateScope())
        {
            var db = serviceScope.ServiceProvider.GetRequiredService<AeroLinkDbContext>(); var now = DateTimeOffset.UtcNow;
            var releasedChange = new SystemChangeRequest("SRCR-04940", 0, scope.ProjectId, scope.ReleasedId, "Released-build canonical change", "P", "A", "S", "software.author", now);
            var activeChange = new SystemChangeRequest("HLRCR-04941", 0, scope.ProjectId, scope.ActiveReleaseId, "Active-build canonical change", "P", "A", "S", "software.author", now, ChangeRequestType.Software, softwareLevel: RequirementLevel.HighLevel);
            var report = new ProblemReport(scope.ProjectId, "PR-04940", "Canonical anomaly", "Problem", "Analysis", "software.author", now, targetReleaseId: scope.ActiveReleaseId);
            var testChange = new TestChangeReview(scope.ProjectId, scope.ReleasedId, releasedChange.Id, TestChangeReviewDiscipline.System, releasedChange.DisplayNumber, now, "SYSTPCR-04940");
            var otherProgram = new ProgramRecord("Other documents program", $"OD{Guid.NewGuid():N}"[..12]); var otherProject = new ProjectRecord(otherProgram.Id, "Other project", "Isolation"); var otherRelease = new SoftwareRelease(otherProject.Id, "9.9", false);
            var foreignChange = new SystemChangeRequest("SRCR-04942", 0, otherProject.Id, otherRelease.Id, "Foreign change", "P", "A", "S", "outsider", now);
            var configuration = new UserAccount("configuration.manager", "Casey Morgan", "configuration@example.test", IdentityService.HashPassword(AeroLinkApiFactory.MemberPassword), now);
            var delegated = new UserAccount("delegated.configuration", "Devon Reed", "delegated@example.test", IdentityService.HashPassword(AeroLinkApiFactory.MemberPassword), now);
            var engineer = new UserAccount("plain.engineer", "Parker Gray", "engineer@example.test", IdentityService.HashPassword(AeroLinkApiFactory.MemberPassword), now);
            db.AddRange(releasedChange, activeChange, report, testChange, otherProgram, otherProject, otherRelease, foreignChange, configuration, delegated, engineer,
                new ProgramMembership(configuration.Id, scope.ProgramId, ProgramRole.ConfigurationManager, "admin", now),
                new ProgramMembership(delegated.Id, scope.ProgramId, ProgramRole.SoftwareEngineer, "admin", now),
                new ProgramMembership(engineer.Id, scope.ProgramId, ProgramRole.SoftwareEngineer, "admin", now),
                new ProjectLeadershipAssignment(scope.ProgramId, ProjectLeadershipPosition.ConfigurationManager,
                    configuration.Id, "admin", now),
                new RoleDelegation(scope.ProgramId, configuration.Id, delegated.Id, ProgramRole.ConfigurationManager, now.AddMinutes(-1), now.AddDays(1), "Relationship control coverage.", "admin", now)); await db.SaveChangesAsync();
            releasedChangeId = releasedChange.Id; activeChangeId = activeChange.Id; foreignChangeId = foreignChange.Id; reportId = report.Id; testChangeId = testChange.Id;
        }
        var targetPage = await administrator.GetFromJsonAsync<JsonElement>($"/api/managed-documents/link-options?projectId={scope.ProjectId}&artifactType=ChangeRequest&pageSize=1");
        Assert.Single(targetPage.GetProperty("items").EnumerateArray()); Assert.True(targetPage.GetProperty("hasMore").GetBoolean());
        var targetPageTwo = await administrator.GetFromJsonAsync<JsonElement>($"/api/managed-documents/link-options?projectId={scope.ProjectId}&artifactType=ChangeRequest&pageSize=1&cursor={Uri.EscapeDataString(targetPage.GetProperty("nextCursor").GetString()!)}");
        var targetIds = targetPage.GetProperty("items").EnumerateArray().Concat(targetPageTwo.GetProperty("items").EnumerateArray()).Select(x => x.GetProperty("id").GetGuid()).ToList();
        Assert.Contains(releasedChangeId, targetIds); Assert.Contains(activeChangeId, targetIds); Assert.DoesNotContain(foreignChangeId, targetIds);
        using var created = await administrator.PostAsJsonAsync("/api/managed-documents", new { projectId = scope.ProjectId, acronym = "SCMP", documentType = "Software Configuration Management Plan", title = "Relationship-controlled plan", ownerId = "software.author", formalChangeSummary = "Control lifecycle relationships.", operationKey = Guid.NewGuid().ToString("N") });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode); var createdBody = await created.Content.ReadFromJsonAsync<JsonElement>(); var documentId = createdBody.GetProperty("id").GetGuid(); var revisionId = createdBody.GetProperty("revisionId").GetGuid();
        var initial = await administrator.GetFromJsonAsync<JsonElement>($"/api/managed-documents/{documentId}"); var initialVersion = initial.GetProperty("revisions")[0].GetProperty("version").GetInt64();

        using var adminBypass = await administrator.PostAsJsonAsync($"/api/managed-documents/{documentId}/links", new { revisionId, artifactType = "ChangeRequest", artifactId = releasedChangeId, displayNumber = "SRCR-99999.99", relationship = "MotivatedBy", expectedVersion = initialVersion });
        Assert.Equal(HttpStatusCode.Forbidden, adminBypass.StatusCode);
        using var engineerClient = factory.CreateClient(); using (var engineerLogin = await engineerClient.PostAsJsonAsync("/api/auth/login", new { userName = "plain.engineer", password = AeroLinkApiFactory.MemberPassword })) Assert.Equal(HttpStatusCode.OK, engineerLogin.StatusCode); await SecurityBoundaryTests.AuthorizeMutationsAsync(engineerClient);
        using var engineerBypass = await engineerClient.PostAsJsonAsync($"/api/managed-documents/{documentId}/links", new { revisionId, artifactType = "ChangeRequest", artifactId = releasedChangeId, relationship = "MotivatedBy", expectedVersion = initialVersion }); Assert.Equal(HttpStatusCode.Forbidden, engineerBypass.StatusCode);

        using var owner = factory.CreateClient(); using (var login = await owner.PostAsJsonAsync("/api/auth/login", new { userName = "software.author", password = AeroLinkApiFactory.MemberPassword })) Assert.Equal(HttpStatusCode.OK, login.StatusCode); await SecurityBoundaryTests.AuthorizeMutationsAsync(owner);
        using var invalidMeaning = await owner.PostAsJsonAsync($"/api/managed-documents/{documentId}/links", new { revisionId, artifactType = "ChangeRequest", artifactId = releasedChangeId, relationship = "Governing input", expectedVersion = initialVersion });
        Assert.Equal(HttpStatusCode.BadRequest, invalidMeaning.StatusCode);
        using var foreign = await owner.PostAsJsonAsync($"/api/managed-documents/{documentId}/links", new { revisionId, artifactType = "ChangeRequest", artifactId = foreignChangeId, relationship = "MotivatedBy", expectedVersion = initialVersion });
        Assert.Equal(HttpStatusCode.BadRequest, foreign.StatusCode);

        using var first = await owner.PostAsJsonAsync($"/api/managed-documents/{documentId}/links", new { revisionId, artifactType = "srcr", artifactId = releasedChangeId, displayNumber = "SRCR-99999.99", relationship = "MotivatedBy", expectedVersion = initialVersion });
        Assert.Equal(HttpStatusCode.Created, first.StatusCode); var firstBody = await first.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("SRCR-04940.00", firstBody.GetProperty("displayNumber").GetString()); Assert.Equal("Released-build canonical change", firstBody.GetProperty("canonicalTitle").GetString()); Assert.Equal(scope.ReleasedId, firstBody.GetProperty("targetReleaseId").GetGuid());
        var versionAfterFirst = (await owner.GetFromJsonAsync<JsonElement>($"/api/managed-documents/{documentId}")).GetProperty("revisions")[0].GetProperty("version").GetInt64();
        using var delegatedClient = factory.CreateClient(); using (var delegatedLogin = await delegatedClient.PostAsJsonAsync("/api/auth/login", new { userName = "delegated.configuration", password = AeroLinkApiFactory.MemberPassword })) Assert.Equal(HttpStatusCode.OK, delegatedLogin.StatusCode); await SecurityBoundaryTests.AuthorizeMutationsAsync(delegatedClient);
        using var second = await delegatedClient.PostAsJsonAsync($"/api/managed-documents/{documentId}/links", new { revisionId, artifactType = "ChangeRequest", artifactId = activeChangeId, relationship = "ImplementsChange", expectedVersion = versionAfterFirst });
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);

        var linked = await owner.GetFromJsonAsync<JsonElement>($"/api/managed-documents/{documentId}"); var linkedRevision = linked.GetProperty("revisions")[0]; var links = linkedRevision.GetProperty("links").EnumerateArray().ToList();
        Assert.Equal(2, links.Count(x => x.GetProperty("isCurrent").GetBoolean()));
        Assert.Contains(links, x => x.GetProperty("targetReleaseId").GetGuid() == scope.ReleasedId && x.GetProperty("targetReleaseVersion").GetString() == "1.5");
        Assert.Contains(links, x => x.GetProperty("targetReleaseId").GetGuid() == scope.ActiveReleaseId && x.GetProperty("targetReleaseVersion").GetString() == "1.6");

        using var configurationClient = factory.CreateClient(); using (var configurationLogin = await configurationClient.PostAsJsonAsync("/api/auth/login", new { userName = "configuration.manager", password = AeroLinkApiFactory.MemberPassword })) Assert.Equal(HttpStatusCode.OK, configurationLogin.StatusCode); await SecurityBoundaryTests.AuthorizeMutationsAsync(configurationClient);
        using var corrected = await configurationClient.PatchAsJsonAsync($"/api/managed-documents/{documentId}/links/{firstBody.GetProperty("id").GetGuid()}", new { artifactType = "ChangeRequest", artifactId = releasedChangeId, relationship = "ImplementsChange", reason = "Correct the typed direction before review.", expectedVersion = linkedRevision.GetProperty("version").GetInt64() });
        Assert.Equal(HttpStatusCode.OK, corrected.StatusCode);
        linked = await owner.GetFromJsonAsync<JsonElement>($"/api/managed-documents/{documentId}"); linkedRevision = linked.GetProperty("revisions")[0]; links = linkedRevision.GetProperty("links").EnumerateArray().ToList();
        Assert.Equal(2, links.Count(x => x.GetProperty("isCurrent").GetBoolean())); Assert.Single(links, x => !x.GetProperty("isCurrent").GetBoolean() && x.GetProperty("supersedeReason").GetString() == "Correct the typed direction before review.");

        foreach (var relationship in new[]
        {
            new { Type = "ProblemReport", Id = reportId, Meaning = "AddressesProblem" },
            new { Type = "TestChangeRequest", Id = testChangeId, Meaning = "VerificationImpact" },
            new { Type = "Release", Id = scope.ActiveReleaseId, Meaning = "RelatedBuild" }
        })
        {
            using var added = await owner.PostAsJsonAsync($"/api/managed-documents/{documentId}/links", new { revisionId, artifactType = relationship.Type, artifactId = relationship.Id, relationship = relationship.Meaning, expectedVersion = linkedRevision.GetProperty("version").GetInt64() });
            Assert.Equal(HttpStatusCode.Created, added.StatusCode); linked = await owner.GetFromJsonAsync<JsonElement>($"/api/managed-documents/{documentId}"); linkedRevision = linked.GetProperty("revisions")[0];
        }
        links = linkedRevision.GetProperty("links").EnumerateArray().ToList(); Assert.Equal(5, links.Count(x => x.GetProperty("isCurrent").GetBoolean()));
        Assert.Contains(links, x => x.GetProperty("artifactType").GetString() == "ProblemReport" && x.GetProperty("deepLink").GetString()!.EndsWith($"/problem-reports/{reportId}"));
        Assert.Contains(links, x => x.GetProperty("artifactType").GetString() == "TestChangeRequest" && x.GetProperty("deepLink").GetString()!.EndsWith($"/coverage/{testChangeId}"));

        using var submitted = await SubmitAsync(owner, documentId, revisionId, "software.lead", "quality.analyst"); Assert.Equal(HttpStatusCode.OK, submitted.StatusCode);
        var reviewed = await owner.GetFromJsonAsync<JsonElement>($"/api/managed-documents/{documentId}"); var reviewedRevision = reviewed.GetProperty("revisions")[0];
        Assert.Equal(64, reviewedRevision.GetProperty("submittedRelationshipManifestHash").GetString()!.Length); Assert.Contains("SRCR-04940.00", reviewedRevision.GetProperty("submittedRelationshipManifest").GetString()); var firstSnapshotHash = reviewedRevision.GetProperty("snapshotHash").GetString();
        using var postReview = await owner.PostAsJsonAsync($"/api/managed-documents/{documentId}/links", new { revisionId, artifactType = "Release", artifactId = scope.ActiveReleaseId, relationship = "RelatedBuild", expectedVersion = reviewedRevision.GetProperty("version").GetInt64() });
        Assert.Equal(HttpStatusCode.Conflict, postReview.StatusCode);
        var afterBlocked = await owner.GetFromJsonAsync<JsonElement>($"/api/managed-documents/{documentId}"); Assert.Equal(5, afterBlocked.GetProperty("revisions")[0].GetProperty("links").EnumerateArray().Count(x => x.GetProperty("isCurrent").GetBoolean()));

        using var technical = factory.CreateClient(); using (var technicalLogin = await technical.PostAsJsonAsync("/api/auth/login", new { userName = "software.lead", password = AeroLinkApiFactory.MemberPassword })) Assert.Equal(HttpStatusCode.OK, technicalLogin.StatusCode); await SecurityBoundaryTests.AuthorizeMutationsAsync(technical);
        using var returned = await DecideAsync(technical, documentId, revisionId, "return", "Relationship evidence requires correction.", "Add the milestone meaning before resubmission."); Assert.Equal(HttpStatusCode.OK, returned.StatusCode);
        var returnedDetail = await owner.GetFromJsonAsync<JsonElement>($"/api/managed-documents/{documentId}"); var returnedRevision = returnedDetail.GetProperty("revisions")[0];
        using var correctedAfterReturn = await owner.PostAsJsonAsync($"/api/managed-documents/{documentId}/links", new { revisionId, artifactType = "Release", artifactId = scope.ReleasedId, relationship = "AppliesToMilestone", expectedVersion = returnedRevision.GetProperty("version").GetInt64() }); Assert.Equal(HttpStatusCode.Created, correctedAfterReturn.StatusCode);
        var changed = await owner.GetFromJsonAsync<JsonElement>($"/api/managed-documents/{documentId}"); Assert.Equal("", changed.GetProperty("revisions")[0].GetProperty("snapshotHash").GetString());
        using var resubmitted = await SubmitAsync(owner, documentId, revisionId, "software.lead", "quality.analyst"); Assert.Equal(HttpStatusCode.OK, resubmitted.StatusCode);
        var resubmittedDetail = await owner.GetFromJsonAsync<JsonElement>($"/api/managed-documents/{documentId}"); Assert.NotEqual(firstSnapshotHash, resubmittedDetail.GetProperty("revisions")[0].GetProperty("snapshotHash").GetString());

        using (var serviceScope = factory.Services.CreateScope())
        {
            var db = serviceScope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var account = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.SingleAsync(db.UserAccounts.Where(x => x.UserName == "software.author"));
            var memberships = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.ToListAsync(db.ProgramMemberships.Where(x => x.UserId == account.Id && x.ProgramId == scope.ProgramId && x.EndedAt == null));
            foreach (var membership in memberships) membership.End("admin", DateTimeOffset.UtcNow);
            await db.SaveChangesAsync();
        }
        var endedOwnerDetail = await administrator.GetFromJsonAsync<JsonElement>($"/api/managed-documents/{documentId}");
        Assert.False(endedOwnerDetail.GetProperty("revisions")[0].GetProperty("canManageRelationships").GetBoolean());
        using var endedOwnerBypass = await owner.PostAsJsonAsync($"/api/managed-documents/{documentId}/links", new { revisionId, artifactType = "Release", artifactId = scope.ActiveReleaseId, relationship = "RelatedBuild", expectedVersion = resubmittedDetail.GetProperty("revisions")[0].GetProperty("version").GetInt64() });
        Assert.Equal(HttpStatusCode.Forbidden, endedOwnerBypass.StatusCode);
    }

    [Fact]
    public async Task Project_document_can_create_checkout_and_check_in_without_build_context()
    {
        using var factory = new AeroLinkApiFactory(); using var client = factory.CreateClient(); await ProblemReportApiTests.BootstrapAndLoginAsync(client);
        var scope = await SeedProjectAsync(factory);
        using var enrollmentResponse = await client.PostAsync($"/api/managed-documents/connector-enrollment?projectId={scope.ProjectId}", null);
        Assert.Equal(HttpStatusCode.OK, enrollmentResponse.StatusCode);
        var enrollment = await enrollmentResponse.Content.ReadFromJsonAsync<ConnectorEnrollmentManifest>();
        Assert.NotNull(enrollment); Assert.Equal("aerolink-api-tests", enrollment.DeploymentId);
        using var created = await client.PostAsJsonAsync("/api/managed-documents", new { projectId = scope.ProjectId, acronym = "SDP", documentType = "Software Development Plan", title = "Navigation Software Development Plan", ownerId = "software.author", changeSummary = "Initial controlled draft.", operationKey = Guid.NewGuid().ToString("N") });
        Assert.True(created.StatusCode == HttpStatusCode.Created, await created.Content.ReadAsStringAsync()); var createdBody = await created.Content.ReadFromJsonAsync<JsonElement>(); var documentId = createdBody.GetProperty("id").GetGuid(); var revisionId = createdBody.GetProperty("revisionId").GetGuid();

        using var checkout = await client.PostAsync($"/api/managed-documents/revisions/{revisionId}/checkout", null); Assert.Equal(HttpStatusCode.OK, checkout.StatusCode); var checkoutBody = await checkout.Content.ReadFromJsonAsync<JsonElement>();
        using var secondCheckout = await client.PostAsync($"/api/managed-documents/revisions/{revisionId}/checkout", null); Assert.Equal(HttpStatusCode.Conflict, secondCheckout.StatusCode);
        var launch = new Uri(checkoutBody.GetProperty("launchUri").GetString()!); var launchQuery = Query(launch); Assert.Single(launchQuery); Assert.True(launchQuery.ContainsKey("envelope"));
        var signedEnvelope = ConnectorLaunchProtocol.Verify(launchQuery["envelope"], enrollment.PublicKey, DateTimeOffset.UtcNow);
        Assert.Equal(scope.ProjectId, signedEnvelope.ProjectId); Assert.Equal(documentId, signedEnvelope.DocumentId); Assert.Equal(revisionId, signedEnvelope.RevisionId);
        Assert.Equal("edit", signedEnvelope.Mode); Assert.Equal(enrollment.DeploymentId, signedEnvelope.DeploymentId); Assert.Equal(enrollment.Origin, signedEnvelope.Origin);
        var ticket = signedEnvelope.Nonce;
        var redemptionAttempts = await Task.WhenAll(
            client.PostAsync($"/api/document-connector/redeem/{Uri.EscapeDataString(ticket)}", null),
            client.PostAsync($"/api/document-connector/redeem/{Uri.EscapeDataString(ticket)}", null));
        using var redeemed = redemptionAttempts.Single(response => response.StatusCode == HttpStatusCode.OK);
        using var concurrentReplay = redemptionAttempts.Single(response => response.StatusCode != HttpStatusCode.OK);
        Assert.True(concurrentReplay.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Conflict);
        var grant = await redeemed.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(signedEnvelope.ProjectId, grant.GetProperty("projectId").GetGuid()); Assert.Equal(signedEnvelope.DocumentId, grant.GetProperty("documentId").GetGuid());
        Assert.Equal(signedEnvelope.RevisionId, grant.GetProperty("revisionId").GetGuid()); Assert.Equal(signedEnvelope.SourceAttachmentId, grant.GetProperty("sourceAttachmentId").GetGuid());
        Assert.Equal(signedEnvelope.SourceSize, grant.GetProperty("sourceSize").GetInt64()); Assert.Equal(signedEnvelope.SourceSha256, grant.GetProperty("sourceSha256").GetString());
        using var replayedRedemption = await client.PostAsync($"/api/document-connector/redeem/{Uri.EscapeDataString(ticket)}", null); Assert.Equal(HttpStatusCode.BadRequest, replayedRedemption.StatusCode);
        var grantId = grant.GetProperty("id").GetGuid(); var token = grant.GetProperty("accessToken").GetString()!; var version = grant.GetProperty("sessionVersion").GetInt64();
        using var download = new HttpRequestMessage(HttpMethod.Get, $"/api/document-connector/{grantId}/download"); download.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token); using var downloaded = await client.SendAsync(download); Assert.Equal(HttpStatusCode.OK, downloaded.StatusCode); var word = await downloaded.Content.ReadAsByteArrayAsync(); ManagedDocumentFileService.ValidateDocx(word, true);
        using (var heartbeatRequest = new HttpRequestMessage(HttpMethod.Post, $"/api/document-connector/{grantId}/heartbeat") { Content = JsonContent.Create(new { expectedVersion = version }) }) { heartbeatRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token); using var heartbeat = await client.SendAsync(heartbeatRequest); Assert.Equal(HttpStatusCode.OK, heartbeat.StatusCode); var heartbeatBody = await heartbeat.Content.ReadFromJsonAsync<JsonElement>(); Assert.Equal(version, heartbeatBody.GetProperty("version").GetInt64()); }
        using (var blankForm = new MultipartFormDataContent()) { blankForm.Add(new StringContent(" "), "comment"); blankForm.Add(new StringContent(version.ToString()), "expectedVersion"); var blankFile = new ByteArrayContent(word); blankFile.Headers.ContentType = new(ManagedDocumentFileService.DocxContentType); blankForm.Add(blankFile, "file", "SDP-000001.00.docx"); using var blankRequest = new HttpRequestMessage(HttpMethod.Post, $"/api/document-connector/{grantId}/check-in") { Content = blankForm }; blankRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token); using var blank = await client.SendAsync(blankRequest); Assert.Equal(HttpStatusCode.BadRequest, blank.StatusCode); }
        using (var oversizedForm = new MultipartFormDataContent()) { oversizedForm.Add(new StringContent(new string('x', 4001)), "comment"); oversizedForm.Add(new StringContent(version.ToString()), "expectedVersion"); var oversizedFile = new ByteArrayContent(word); oversizedFile.Headers.ContentType = new(ManagedDocumentFileService.DocxContentType); oversizedForm.Add(oversizedFile, "file", "SDP-000001.00.docx"); using var oversizedRequest = new HttpRequestMessage(HttpMethod.Post, $"/api/document-connector/{grantId}/check-in") { Content = oversizedForm }; oversizedRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token); using var oversized = await client.SendAsync(oversizedRequest); Assert.Equal(HttpStatusCode.BadRequest, oversized.StatusCode); }
        using (var unsafeCheckIn = await SendCheckInAsync(client, grantId, token, version,
            "Attempted direct API bypass with external package content.", AddExternalImageRelationship(word)))
        {
            Assert.Equal(HttpStatusCode.BadRequest, unsafeCheckIn.StatusCode);
            Assert.Contains("external target is prohibited", await unsafeCheckIn.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
        }
        using var checkedIn = await SendCheckInAsync(client, grantId, token, version, "Added build and code-traceability responsibilities.", word); Assert.Equal(HttpStatusCode.OK, checkedIn.StatusCode); var checkedInJson = await checkedIn.Content.ReadAsStringAsync();
        using var retriedCheckIn = await SendCheckInAsync(client, grantId, token, version, "Added build and code-traceability responsibilities.", word); Assert.Equal(HttpStatusCode.OK, retriedCheckIn.StatusCode); Assert.Equal(checkedInJson, await retriedCheckIn.Content.ReadAsStringAsync());
        using var conflictingCheckIn = await SendCheckInAsync(client, grantId, token, version, "Different comment under the completed connector grant.", word); Assert.Equal(HttpStatusCode.Conflict, conflictingCheckIn.StatusCode);

        using var detailResponse = await client.GetAsync($"/api/managed-documents/{documentId}"); Assert.True(detailResponse.IsSuccessStatusCode, await detailResponse.Content.ReadAsStringAsync()); var detail = await detailResponse.Content.ReadFromJsonAsync<JsonElement>(); var revision = detail.GetProperty("revisions")[0]; var attachments = revision.GetProperty("attachments").EnumerateArray().ToList(); Assert.Equal(2, attachments.Count); Assert.Contains(attachments, item => item.GetProperty("version").GetInt32() == 2 && item.GetProperty("state").GetString() == "Active"); Assert.All(attachments, item => { Assert.Equal("aerolink-ooxml-safe-v1", item.GetProperty("validationProfile").GetString()); Assert.Equal("accepted", item.GetProperty("validationResult").GetString()); });
        Assert.Equal("Initial controlled draft.", revision.GetProperty("formalChangeSummary").GetString());
        Assert.Equal("software.author", revision.GetProperty("responsibleOwnerId").GetString());
        var checkIns = revision.GetProperty("checkIns").EnumerateArray().ToList();
        Assert.Equal(2, checkIns.Count);
        Assert.Equal("Created the initial controlled Word template.", checkIns[0].GetProperty("comment").GetString());
        Assert.Equal("Added build and code-traceability responsibilities.", checkIns[1].GetProperty("comment").GetString());
        Assert.Equal(attachments.Single(x => x.GetProperty("version").GetInt32() == 2).GetProperty("sha256").GetString(), checkIns[1].GetProperty("resultSha256").GetString());
        using var submitted = await SubmitAsync(client, documentId, revisionId, "software.lead", "quality.analyst");
        Assert.Equal(HttpStatusCode.OK, submitted.StatusCode);
        using var verificationScope = factory.Services.CreateScope(); var verificationDb = verificationScope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        Assert.Equal(2, await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.CountAsync(verificationDb.ManagedDocumentReviewSteps.Where(x => x.RevisionId == revisionId)));
        var storageOperations = await verificationDb.ManagedDocumentStorageOperations.Where(x => x.DocumentId == documentId).ToListAsync();
        Assert.Equal(2, storageOperations.Count); Assert.All(storageOperations, item => Assert.Equal(ManagedDocumentStorageOperationState.Available, item.State));
        Assert.Empty(verificationScope.ServiceProvider.GetRequiredService<EvidenceFileStore>().EnumerateStagedKeys());
    }

    [Fact]
    public async Task Controlled_reads_fail_closed_deduplicate_incidents_and_require_exact_hash_recovery()
    {
        using var factory = new AeroLinkApiFactory(); using var administrator = factory.CreateClient();
        await ProblemReportApiTests.BootstrapAndLoginAsync(administrator); var scope = await SeedProjectAsync(factory);
        using var owner = factory.CreateClient();
        using (var login = await owner.PostAsJsonAsync("/api/auth/login", new { userName = "software.author", password = AeroLinkApiFactory.MemberPassword })) Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        await SecurityBoundaryTests.AuthorizeMutationsAsync(owner);
        using var quality = factory.CreateClient();
        using (var login = await quality.PostAsJsonAsync("/api/auth/login", new { userName = "quality.analyst", password = AeroLinkApiFactory.MemberPassword })) Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        await SecurityBoundaryTests.AuthorizeMutationsAsync(quality);

        using var created = await owner.PostAsJsonAsync("/api/managed-documents", new { projectId = scope.ProjectId, acronym = "SDP", documentType = "Software Development Plan", title = "Integrity-controlled plan", ownerId = "software.author", formalChangeSummary = "Prove exact retained bytes.", operationKey = Guid.NewGuid().ToString("N") });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode); var createdBody = await created.Content.ReadFromJsonAsync<JsonElement>();
        var documentId = createdBody.GetProperty("id").GetGuid(); var revisionId = createdBody.GetProperty("revisionId").GetGuid();
        var detail = await owner.GetFromJsonAsync<JsonElement>($"/api/managed-documents/{documentId}"); var revision = detail.GetProperty("revisions")[0]; var attachmentId = revision.GetProperty("currentWorkingAttachmentId").GetGuid();

        using var checkout = await owner.PostAsync($"/api/managed-documents/revisions/{revisionId}/checkout", null); Assert.Equal(HttpStatusCode.OK, checkout.StatusCode);
        var ticket = LaunchEnvelope(new Uri((await checkout.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("launchUri").GetString()!)).Nonce;
        using var redeemed = await owner.PostAsync($"/api/document-connector/redeem/{Uri.EscapeDataString(ticket)}", null); Assert.Equal(HttpStatusCode.OK, redeemed.StatusCode);
        var grant = await redeemed.Content.ReadFromJsonAsync<JsonElement>(); var grantId = grant.GetProperty("id").GetGuid(); var accessToken = grant.GetProperty("accessToken").GetString()!;
        Assert.Equal(attachmentId, grant.GetProperty("sourceAttachmentId").GetGuid());

        byte[] original; string storageKey; string expectedHash; long expectedSize;
        using (var serviceScope = factory.Services.CreateScope())
        {
            var db = serviceScope.ServiceProvider.GetRequiredService<AeroLinkDbContext>(); var store = serviceScope.ServiceProvider.GetRequiredService<EvidenceFileStore>();
            var attachment = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.SingleAsync(db.ControlledAttachments.Where(x => x.Id == attachmentId));
            storageKey = attachment.StorageKey; expectedHash = attachment.Sha256; expectedSize = attachment.Size;
            original = await File.ReadAllBytesAsync(Path.Combine(store.RootPath, storageKey.Replace('/', Path.DirectorySeparatorChar)));
            var altered = original.ToArray(); altered[^1] ^= 0x01;
            await File.WriteAllBytesAsync(Path.Combine(store.RootPath, storageKey.Replace('/', Path.DirectorySeparatorChar)), altered);
        }
        Assert.Equal(expectedSize, original.LongLength); Assert.Equal(expectedHash, ManagedDocumentFileService.Sha256(original));
        Assert.Equal(expectedHash, grant.GetProperty("sourceSha256").GetString()); Assert.Equal(expectedSize, grant.GetProperty("sourceSize").GetInt64());

        using (var connectorRequest = new HttpRequestMessage(HttpMethod.Get, $"/api/document-connector/{grantId}/download"))
        {
            connectorRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken); connectorRequest.Headers.Range = new RangeHeaderValue(0, 31);
            using var connectorDownload = await owner.SendAsync(connectorRequest); Assert.Equal(HttpStatusCode.Conflict, connectorDownload.StatusCode);
            Assert.Equal("document_integrity_blocked", (await connectorDownload.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        }
        using (var rangeRequest = new HttpRequestMessage(HttpMethod.Get, $"/api/managed-documents/attachments/{attachmentId}"))
        {
            rangeRequest.Headers.Range = new RangeHeaderValue(0, 31); using var rangeDownload = await owner.SendAsync(rangeRequest);
            Assert.Equal(HttpStatusCode.Conflict, rangeDownload.StatusCode);
        }
        using (var discard = new HttpRequestMessage(HttpMethod.Post, $"/api/document-connector/{grantId}/discard") { Content = JsonContent.Create(new { expectedVersion = grant.GetProperty("sessionVersion").GetInt64(), reason = "Continue integrity coverage without an active checkout." }) })
        { discard.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken); using var discarded = await owner.SendAsync(discard); Assert.Equal(HttpStatusCode.NoContent, discarded.StatusCode); }
        using var refusedSubmission = await SubmitAsync(owner, documentId, revisionId, "software.lead", "quality.analyst"); Assert.Equal(HttpStatusCode.Conflict, refusedSubmission.StatusCode);

        using var scan = await quality.PostAsync($"/api/managed-documents/projects/{scope.ProjectId}/integrity/scan", null); Assert.Equal(HttpStatusCode.OK, scan.StatusCode);
        var scanBody = await scan.Content.ReadFromJsonAsync<JsonElement>(); Assert.Equal(1, scanBody.GetProperty("failed").GetInt32()); Assert.Contains(attachmentId, scanBody.GetProperty("failedAttachmentIds").EnumerateArray().Select(x => x.GetGuid()));
        detail = await owner.GetFromJsonAsync<JsonElement>($"/api/managed-documents/{documentId}"); Assert.True(detail.GetProperty("revisions")[0].GetProperty("integrityBlocked").GetBoolean());
        using (var serviceScope = factory.Services.CreateScope())
        {
            var db = serviceScope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            Assert.Single(await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.ToListAsync(db.OperationalAlerts.Where(x => x.Signal == $"managed-document-integrity:{attachmentId:N}" && x.State != OperationalAlertState.Resolved)));
            Assert.Single(await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.ToListAsync(db.ManagedDocumentEvents.Where(x => x.DocumentId == documentId && x.EventType == "DocumentIntegrityBlocked")));
        }

        using (var recoveryForm = new MultipartFormDataContent())
        {
            recoveryForm.Add(new StringContent("Recovered from the independently verified backup object."), "reason"); var file = new ByteArrayContent(original); file.Headers.ContentType = new(ManagedDocumentFileService.DocxContentType); recoveryForm.Add(file, "file", "SDP-000001.00.docx");
            using var recovered = await quality.PostAsync($"/api/managed-documents/attachments/{attachmentId}/restore", recoveryForm); Assert.Equal(HttpStatusCode.OK, recovered.StatusCode);
        }
        using var recoveredCheckout = await owner.PostAsync($"/api/managed-documents/revisions/{revisionId}/checkout", null); Assert.Equal(HttpStatusCode.OK, recoveredCheckout.StatusCode);
        var recoveredTicket = LaunchEnvelope(new Uri((await recoveredCheckout.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("launchUri").GetString()!)).Nonce;
        using var recoveredRedemption = await owner.PostAsync($"/api/document-connector/redeem/{Uri.EscapeDataString(recoveredTicket)}", null); var recoveredGrant = await recoveredRedemption.Content.ReadFromJsonAsync<JsonElement>();
        var recoveredGrantId = recoveredGrant.GetProperty("id").GetGuid(); var recoveredToken = recoveredGrant.GetProperty("accessToken").GetString()!;
        using (var connectorRequest = new HttpRequestMessage(HttpMethod.Get, $"/api/document-connector/{recoveredGrantId}/download"))
        {
            connectorRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", recoveredToken); using var connectorDownload = await owner.SendAsync(connectorRequest); Assert.Equal(HttpStatusCode.OK, connectorDownload.StatusCode);
            Assert.Equal(expectedHash, ManagedDocumentFileService.Sha256(await connectorDownload.Content.ReadAsByteArrayAsync()));
        }
        using (var discard = new HttpRequestMessage(HttpMethod.Post, $"/api/document-connector/{recoveredGrantId}/discard") { Content = JsonContent.Create(new { expectedVersion = recoveredGrant.GetProperty("sessionVersion").GetInt64(), reason = "Integrity coverage complete." }) })
        { discard.Headers.Authorization = new AuthenticationHeaderValue("Bearer", recoveredToken); using var discarded = await owner.SendAsync(discard); Assert.Equal(HttpStatusCode.NoContent, discarded.StatusCode); }
        using var acceptedSubmission = await SubmitAsync(owner, documentId, revisionId, "software.lead", "quality.analyst"); Assert.Equal(HttpStatusCode.OK, acceptedSubmission.StatusCode);
    }

    [Fact]
    public async Task Released_build_context_cannot_make_a_project_document_read_only_or_change_inventory()
    {
        using var factory = new AeroLinkApiFactory(); using var client = factory.CreateClient(); await ProblemReportApiTests.BootstrapAndLoginAsync(client); var scope = await SeedProjectAsync(factory);
        client.DefaultRequestHeaders.Add("X-AeroLink-Build-Context", scope.ReleasedId.ToString());
        using var created = await client.PostAsJsonAsync("/api/managed-documents", new { projectId = scope.ProjectId, acronym = "SVP", documentType = "Software Verification Plan", title = "Project verification plan", ownerId = "software.author", changeSummary = "Initial Project-wide issue.", operationKey = Guid.NewGuid().ToString("N") }); Assert.Equal(HttpStatusCode.Created, created.StatusCode); var createdBody = await created.Content.ReadFromJsonAsync<JsonElement>();
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
        using var created = await client.PostAsJsonAsync("/api/managed-documents", new { projectId, acronym = "PSAC", documentType = "Plan for Software Aspects of Certification", title = "Project PSAC", ownerId = "buildfree.author", changeSummary = "Initial formal revision.", operationKey = Guid.NewGuid().ToString("N") });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var inventory = await client.GetFromJsonAsync<JsonElement>($"/api/managed-documents?projectId={projectId}");
        Assert.Equal(1, inventory.GetProperty("totalCount").GetInt32());
    }

    [Fact]
    public async Task Formal_scope_correction_is_audited_versioned_and_bound_to_review_snapshot()
    {
        using var factory = new AeroLinkApiFactory(); using var client = factory.CreateClient(); await ProblemReportApiTests.BootstrapAndLoginAsync(client);
        var scope = await SeedProjectAsync(factory);
        using var created = await client.PostAsJsonAsync("/api/managed-documents", new { projectId = scope.ProjectId, acronym = "SAS", documentType = "Software Accomplishment Summary", title = "Project accomplishment summary", ownerId = "software.author", changeSummary = "Initial formal scope.", operationKey = Guid.NewGuid().ToString("N") });
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

        using var submitted = await SubmitAsync(client, documentId, revisionId, "software.lead", "quality.analyst");
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
        using var created = await administrator.PostAsJsonAsync("/api/managed-documents", new { projectId = scope.ProjectId, acronym = "ICD", documentType = "Interface Control Document", title = "Project interface control", ownerId = "software.lead", formalChangeSummary = "Control the Project interfaces.", operationKey = Guid.NewGuid().ToString("N") });
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
    public async Task Managed_document_control_routes_require_position_authority_and_preserve_primary_backup_delegation_and_admin()
    {
        using var factory = new AeroLinkApiFactory();
        using var administrator = factory.CreateClient();
        await ProblemReportApiTests.BootstrapAndLoginAsync(administrator);
        var scope = await SeedProjectAsync(factory);

        using (var serviceScope = factory.Services.CreateScope())
        {
            var db = serviceScope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var now = DateTimeOffset.UtcNow;
            var baseConfiguration = new UserAccount("base.configuration", "Base Configuration", "base.configuration@example.test", IdentityService.HashPassword(AeroLinkApiFactory.MemberPassword), now);
            var baseProgramManager = new UserAccount("base.program.manager", "Base Program Manager", "base.program.manager@example.test", IdentityService.HashPassword(AeroLinkApiFactory.MemberPassword), now);
            var primaryAccount = new UserAccount("configuration.primary", "Configuration Primary", "configuration.primary@example.test", IdentityService.HashPassword(AeroLinkApiFactory.MemberPassword), now);
            var backupAccount = new UserAccount("configuration.backup", "Configuration Backup", "configuration.backup@example.test", IdentityService.HashPassword(AeroLinkApiFactory.MemberPassword), now);
            var delegatedAccount = new UserAccount("configuration.delegated", "Configuration Delegate", "configuration.delegated@example.test", IdentityService.HashPassword(AeroLinkApiFactory.MemberPassword), now);
            db.AddRange(baseConfiguration, baseProgramManager, primaryAccount, backupAccount, delegatedAccount,
                new ProgramMembership(baseConfiguration.Id, scope.ProgramId, ProgramRole.ConfigurationManager, "admin", now),
                new ProgramMembership(baseProgramManager.Id, scope.ProgramId, ProgramRole.ProgramManager, "admin", now),
                new ProgramMembership(primaryAccount.Id, scope.ProgramId, ProgramRole.ConfigurationManager, "admin", now),
                new ProgramMembership(backupAccount.Id, scope.ProgramId, ProgramRole.ConfigurationManager, "admin", now),
                new ProgramMembership(delegatedAccount.Id, scope.ProgramId, ProgramRole.SoftwareEngineer, "admin", now),
                new ProjectLeadershipAssignment(scope.ProgramId, ProjectLeadershipPosition.ConfigurationManager, primaryAccount.Id, "admin", now),
                new ProjectLeadershipBackup(scope.ProgramId, ProjectLeadershipPosition.ConfigurationManager, backupAccount.Id, "admin", now),
                new RoleDelegation(scope.ProgramId, primaryAccount.Id, delegatedAccount.Id, ProgramRole.ConfigurationManager,
                    now.AddMinutes(-1), now.AddDays(1), "Controlled-document configuration coverage.", "admin", now));
            await db.SaveChangesAsync();
        }

        using var created = await administrator.PostAsJsonAsync("/api/managed-documents", new
        {
            projectId = scope.ProjectId,
            acronym = "CMP",
            documentType = "Configuration Management Plan",
            title = "Position-controlled configuration plan",
            ownerId = "software.author",
            formalChangeSummary = "Prove managed-document authority follows Project Leadership.",
            operationKey = Guid.NewGuid().ToString("N")
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var createdBody = await created.Content.ReadFromJsonAsync<JsonElement>();
        var documentId = createdBody.GetProperty("id").GetGuid();
        var revisionId = createdBody.GetProperty("revisionId").GetGuid();
        var administratorDetail = await administrator.GetFromJsonAsync<JsonElement>($"/api/managed-documents/{documentId}");
        Assert.True(administratorDetail.GetProperty("canReassignSteward").GetBoolean());
        Assert.True(administratorDetail.GetProperty("revisions")[0].GetProperty("canReviseFormalSummary").GetBoolean());

        foreach (var userName in new[] { "base.configuration", "base.program.manager" })
        {
            using var baseOnly = await LoginAsync(factory, userName);
            var detail = await baseOnly.GetFromJsonAsync<JsonElement>($"/api/managed-documents/{documentId}");
            var revision = detail.GetProperty("revisions")[0];
            Assert.False(detail.GetProperty("canReassignSteward").GetBoolean());
            Assert.False(revision.GetProperty("canReviseFormalSummary").GetBoolean());
            Assert.False(revision.GetProperty("canReassignResponsibleOwner").GetBoolean());

            using var createDenied = await baseOnly.PostAsJsonAsync("/api/managed-documents", new { projectId = scope.ProjectId, acronym = "DENY", documentType = "Plan", title = "Denied", ownerId = "software.author", formalChangeSummary = "Must not persist.", operationKey = Guid.NewGuid().ToString("N") });
            Assert.Equal(HttpStatusCode.Forbidden, createDenied.StatusCode);
            using var successorDenied = await baseOnly.PostAsJsonAsync($"/api/managed-documents/{documentId}/revisions", new { ownerId = "software.author", changeSummary = "Must not start." });
            Assert.Equal(HttpStatusCode.Forbidden, successorDenied.StatusCode);
            using var formalDenied = await baseOnly.PatchAsJsonAsync($"/api/managed-documents/revisions/{revisionId}/formal-summary", new { formalChangeSummary = "Must not change.", reason = "Base eligibility is not authority.", expectedVersion = revision.GetProperty("version").GetInt64() });
            Assert.Equal(HttpStatusCode.Forbidden, formalDenied.StatusCode);
            using var stewardDenied = await baseOnly.PatchAsJsonAsync($"/api/managed-documents/{documentId}/steward", new { assigneeId = "software.lead", reason = "Must not transfer.", expectedVersion = detail.GetProperty("version").GetInt64() });
            Assert.Equal(HttpStatusCode.Forbidden, stewardDenied.StatusCode);
            using var ownerDenied = await baseOnly.PatchAsJsonAsync($"/api/managed-documents/revisions/{revisionId}/responsible-owner", new { assigneeId = "software.lead", reason = "Must not transfer.", expectedVersion = revision.GetProperty("version").GetInt64() });
            Assert.Equal(HttpStatusCode.Forbidden, ownerDenied.StatusCode);
            using var checkoutDenied = await baseOnly.PostAsync($"/api/managed-documents/revisions/{revisionId}/checkout", null);
            Assert.Equal(HttpStatusCode.Forbidden, checkoutDenied.StatusCode);
            using var unlockDenied = await baseOnly.PostAsJsonAsync($"/api/managed-documents/revisions/{revisionId}/force-unlock", new { reason = "Must not unlock." });
            Assert.Equal(HttpStatusCode.Forbidden, unlockDenied.StatusCode);
            using var withdrawDenied = await baseOnly.PostAsJsonAsync($"/api/managed-documents/revisions/{revisionId}/withdraw", new { reason = "Must not withdraw.", expectedVersion = revision.GetProperty("version").GetInt64() });
            Assert.Equal(HttpStatusCode.Forbidden, withdrawDenied.StatusCode);
            using var scanDenied = await baseOnly.PostAsync($"/api/managed-documents/projects/{scope.ProjectId}/integrity/scan", null);
            Assert.Equal(HttpStatusCode.Forbidden, scanDenied.StatusCode);
            using var reconcileDenied = await baseOnly.PostAsync($"/api/managed-documents/projects/{scope.ProjectId}/storage/reconcile", null);
            Assert.Equal(HttpStatusCode.Forbidden, reconcileDenied.StatusCode);
            using var restoreForm = new MultipartFormDataContent();
            using var restoreDenied = await baseOnly.PostAsync($"/api/managed-documents/attachments/{revision.GetProperty("currentWorkingAttachmentId").GetGuid()}/restore", restoreForm);
            Assert.Equal(HttpStatusCode.Forbidden, restoreDenied.StatusCode);
        }

        using var primary = await LoginAsync(factory, "configuration.primary");
        using var backup = await LoginAsync(factory, "configuration.backup");
        using var delegated = await LoginAsync(factory, "configuration.delegated");
        foreach (var authorized in new[] { primary, backup, delegated })
        {
            var detail = await authorized.GetFromJsonAsync<JsonElement>($"/api/managed-documents/{documentId}");
            Assert.True(detail.GetProperty("canReassignSteward").GetBoolean());
            Assert.True(detail.GetProperty("revisions")[0].GetProperty("canReviseFormalSummary").GetBoolean());
            Assert.True(detail.GetProperty("revisions")[0].GetProperty("canReassignResponsibleOwner").GetBoolean());
        }

        var beforePrimary = await primary.GetFromJsonAsync<JsonElement>($"/api/managed-documents/{documentId}");
        using var formal = await primary.PatchAsJsonAsync($"/api/managed-documents/revisions/{revisionId}/formal-summary", new { formalChangeSummary = "The Configuration Manager controls the formal scope.", reason = "Exercise primary authority.", expectedVersion = beforePrimary.GetProperty("revisions")[0].GetProperty("version").GetInt64() });
        Assert.Equal(HttpStatusCode.OK, formal.StatusCode);
        var beforeBackup = await backup.GetFromJsonAsync<JsonElement>($"/api/managed-documents/{documentId}");
        using var steward = await backup.PatchAsJsonAsync($"/api/managed-documents/{documentId}/steward", new { assigneeId = "software.lead", reason = "Exercise standing-backup authority.", expectedVersion = beforeBackup.GetProperty("version").GetInt64() });
        Assert.Equal(HttpStatusCode.OK, steward.StatusCode);
        var beforeDelegate = await delegated.GetFromJsonAsync<JsonElement>($"/api/managed-documents/{documentId}");
        using var owner = await delegated.PatchAsJsonAsync($"/api/managed-documents/revisions/{revisionId}/responsible-owner", new { assigneeId = "software.lead", reason = "Exercise exact delegated authority.", expectedVersion = beforeDelegate.GetProperty("revisions")[0].GetProperty("version").GetInt64() });
        Assert.Equal(HttpStatusCode.OK, owner.StatusCode);

        using var scan = await primary.PostAsync($"/api/managed-documents/projects/{scope.ProjectId}/integrity/scan", null);
        Assert.Equal(HttpStatusCode.OK, scan.StatusCode);
        using var reconcile = await backup.PostAsync($"/api/managed-documents/projects/{scope.ProjectId}/storage/reconcile", null);
        Assert.Equal(HttpStatusCode.OK, reconcile.StatusCode);
        using var missingSession = await delegated.PostAsJsonAsync($"/api/managed-documents/revisions/{revisionId}/force-unlock", new { reason = "No active session exists." });
        Assert.Equal(HttpStatusCode.NotFound, missingSession.StatusCode);
        using var activeConflict = await primary.PostAsJsonAsync($"/api/managed-documents/{documentId}/revisions", new { ownerId = "software.author", changeSummary = "Cannot start beside the active revision." });
        Assert.Equal(HttpStatusCode.Conflict, activeConflict.StatusCode);
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
        var delegatedDirectory = await administrator.GetFromJsonAsync<JsonElement>($"/api/directory?projectId={scope.ProjectId}&authority=ManagedDocumentAuthor&search=delegated.author");
        Assert.Contains(delegatedDirectory.EnumerateArray(), person => person.GetProperty("userName").GetString() == "delegated.author");
        foreach (var invalidOwner in new[] { "missing.person", "inactive.author", "other.author", "admin" })
        {
            using var invalid = await administrator.PostAsJsonAsync("/api/managed-documents", new { projectId = scope.ProjectId, acronym = "SQAP", documentType = "Software Quality Assurance Plan", title = "Invalid assignment", ownerId = invalidOwner, formalChangeSummary = "Must not persist.", operationKey = Guid.NewGuid().ToString("N") });
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        }
        var emptyInventory = await administrator.GetFromJsonAsync<JsonElement>($"/api/managed-documents?projectId={scope.ProjectId}"); Assert.Equal(0, emptyInventory.GetProperty("totalCount").GetInt32());

        using var delegatedCreate = await administrator.PostAsJsonAsync("/api/managed-documents", new { projectId = scope.ProjectId, acronym = "PSAC", documentType = "Plan for Software Aspects of Certification", title = "Delegated plan", ownerId = "delegated.author", formalChangeSummary = "Delegated authoring authority is explicit.", operationKey = Guid.NewGuid().ToString("N") });
        Assert.Equal(HttpStatusCode.Created, delegatedCreate.StatusCode);

        using var created = await administrator.PostAsJsonAsync("/api/managed-documents", new { projectId = scope.ProjectId, acronym = "SQAP", documentType = "Software Quality Assurance Plan", title = "Controlled quality plan", ownerId = "software.lead", formalChangeSummary = "Establish Project quality controls.", operationKey = Guid.NewGuid().ToString("N") });
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
        Assert.Equal(2, await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.CountAsync(auditDb.UserNotifications.Where(x => x.ProjectId == scope.ProjectId && x.Recipient == "software.author" && x.ArtifactId == documentId)));
        using var authorClient = factory.CreateClient(); using (var login = await authorClient.PostAsJsonAsync("/api/auth/login", new { userName = "software.author", password = AeroLinkApiFactory.MemberPassword })) Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var authorWork = await authorClient.GetFromJsonAsync<JsonElement>($"/api/my-work?projectId={scope.ProjectId}");
        Assert.Contains(authorWork.GetProperty("tasks").EnumerateArray(), item => item.GetProperty("id").GetGuid() == documentId && item.GetProperty("type").GetString() == "Project document to complete");
        using var formerClient = factory.CreateClient(); using (var login = await formerClient.PostAsJsonAsync("/api/auth/login", new { userName = "software.lead", password = AeroLinkApiFactory.MemberPassword })) Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var formerWork = await formerClient.GetFromJsonAsync<JsonElement>($"/api/my-work?projectId={scope.ProjectId}");
        Assert.DoesNotContain(formerWork.GetProperty("tasks").EnumerateArray(), item => item.GetProperty("id").GetGuid() == documentId && item.GetProperty("type").GetString() == "Project document to complete");
    }

    [Fact]
    public async Task Reviewer_independence_uses_the_exact_immutable_contributor_set()
    {
        using var factory = new AeroLinkApiFactory(); using var administrator = factory.CreateClient(); await ProblemReportApiTests.BootstrapAndLoginAsync(administrator); var scope = await SeedProjectAsync(factory);
        using var created = await administrator.PostAsJsonAsync("/api/managed-documents", new { projectId = scope.ProjectId, acronym = "PSAC", documentType = "Plan for Software Aspects of Certification", title = "Certification plan", ownerId = "software.author", formalChangeSummary = "Update certification lifecycle evidence.", operationKey = Guid.NewGuid().ToString("N") });
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
        using var refused = await SubmitAsync(lead, documentId, revisionId, "software.author", "quality.analyst");
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        using var accepted = await SubmitAsync(lead, documentId, revisionId, "system.reviewer", "quality.analyst");
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        var detail = await lead.GetFromJsonAsync<JsonElement>($"/api/managed-documents/{documentId}"); var contributors = detail.GetProperty("revisions")[0].GetProperty("reviewContributors").EnumerateArray().ToList();
        Assert.Contains(contributors, item => item.GetProperty("contributorId").GetString() == "software.author" && item.GetProperty("provenance").GetString() == "AuthoritativeSubmissionSnapshot");
        using var inReviewTransfer = await administrator.PatchAsJsonAsync($"/api/managed-documents/revisions/{revisionId}/responsible-owner", new { assigneeId = "software.author", reason = "Must fail after submission.", expectedVersion = detail.GetProperty("revisions")[0].GetProperty("version").GetInt64() });
        Assert.Equal(HttpStatusCode.Conflict, inReviewTransfer.StatusCode);
    }

    [Fact]
    public async Task Technical_and_release_signatures_bind_formal_summary_and_released_file_metadata()
    {
        using var factory = new AeroLinkApiFactory(); using var owner = factory.CreateClient(); await ProblemReportApiTests.BootstrapAndLoginAsync(owner); var scope = await SeedProjectAsync(factory);
        using var created = await owner.PostAsJsonAsync("/api/managed-documents", new { projectId = scope.ProjectId, acronym = "SDP", documentType = "Software Development Plan", title = "Signed project plan", ownerId = "software.author", changeSummary = "Authorize the exact formal lifecycle scope.", operationKey = Guid.NewGuid().ToString("N") });
        var createdBody = await created.Content.ReadFromJsonAsync<JsonElement>(); var documentId = createdBody.GetProperty("id").GetGuid(); var revisionId = createdBody.GetProperty("revisionId").GetGuid();
        using var submitted = await SubmitAsync(owner, documentId, revisionId, "software.lead", "quality.analyst"); Assert.Equal(HttpStatusCode.OK, submitted.StatusCode);

        using var technical = factory.CreateClient(); using (var login = await technical.PostAsJsonAsync("/api/auth/login", new { userName = "software.lead", password = AeroLinkApiFactory.MemberPassword })) Assert.Equal(HttpStatusCode.OK, login.StatusCode); await SecurityBoundaryTests.AuthorizeMutationsAsync(technical);
        using var technicalApproval = await DecideAsync(technical, documentId, revisionId, "approve", "I confirm the technical review is complete.", "Formal scope and exact working snapshot are technically complete."); Assert.Equal(HttpStatusCode.OK, technicalApproval.StatusCode);
        using (var notificationScope = factory.Services.CreateScope())
        {
            var notificationDb = notificationScope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var approvalNotification = await notificationDb.UserNotifications.SingleAsync(x =>
                x.ArtifactId == documentId && x.Recipient == "quality.analyst");
            Assert.Equal("DocumentApprovalActivated", approvalNotification.Type);
            Assert.Equal("Approve SDP-000001.00", approvalNotification.Title);
            Assert.Contains("ready for your approval", approvalNotification.Detail);
        }
        var afterTechnical = await owner.GetFromJsonAsync<JsonElement>($"/api/managed-documents/{documentId}"); var technicalVersion = afterTechnical.GetProperty("revisions")[0].GetProperty("version").GetInt64();
        using var technicalEdit = await owner.PatchAsJsonAsync($"/api/managed-documents/revisions/{revisionId}/formal-summary", new { formalChangeSummary = "Forbidden after approval.", reason = "Must fail.", expectedVersion = technicalVersion }); Assert.Equal(HttpStatusCode.Conflict, technicalEdit.StatusCode);

        using var quality = factory.CreateClient(); using (var login = await quality.PostAsJsonAsync("/api/auth/login", new { userName = "quality.analyst", password = AeroLinkApiFactory.MemberPassword })) Assert.Equal(HttpStatusCode.OK, login.StatusCode); await SecurityBoundaryTests.AuthorizeMutationsAsync(quality);
        using var preparation = await quality.PostAsync($"/api/managed-documents/revisions/{revisionId}/release-preparation", null); Assert.Equal(HttpStatusCode.OK, preparation.StatusCode); var preparationBody = await preparation.Content.ReadFromJsonAsync<JsonElement>(); var ticket = LaunchEnvelope(new Uri(preparationBody.GetProperty("launchUri").GetString()!)).Nonce;
        using var redeemed = await quality.PostAsync($"/api/document-connector/redeem/{Uri.EscapeDataString(ticket)}", null); var grant = await redeemed.Content.ReadFromJsonAsync<JsonElement>(); var grantId = grant.GetProperty("id").GetGuid(); var token = grant.GetProperty("accessToken").GetString()!; var sessionVersion = grant.GetProperty("sessionVersion").GetInt64();
        byte[] workingBytes; string reviewedSha256;
        using (var serviceScope = factory.Services.CreateScope())
        {
            var serviceDb = serviceScope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var store = serviceScope.ServiceProvider.GetRequiredService<EvidenceFileStore>();
            var storedRevision = await serviceDb.ManagedDocumentRevisions.SingleAsync(x => x.Id == revisionId);
            var workingAttachment = await serviceDb.ControlledAttachments.SingleAsync(x => x.Id == storedRevision.CurrentWorkingAttachmentId);
            var workingPath = Path.Combine(store.RootPath, workingAttachment.StorageKey.Replace('/', Path.DirectorySeparatorChar));
            workingBytes = await File.ReadAllBytesAsync(workingPath);
            reviewedSha256 = workingAttachment.Sha256;
        }
        var docxBytes = ManagedDocumentFileService.ApplyReleaseMarking(workingBytes);
        var pdfPublication = new ProfessionalPublication("AeroLink", "Program", "Project", "Software Development Plan", "Signed project plan", "Controlled Project document", "SDP-000001", "00", "Released", "Project-wide", "All software builds", "admin", DateTimeOffset.UtcNow, new string('c', 64), [("Formal revision scope", "Authorize the exact formal lifecycle scope.")], [], [], [new("Purpose", "Scope", [new("1", "Plan", "Purpose", "Controlled content.", [])])]);
        var pdf = ProfessionalPublicationRenderer.Render(pdfPublication, "pdf", "SDP-000001.00");
        using var candidateForm = new MultipartFormDataContent(); candidateForm.Add(new StringContent(sessionVersion.ToString()), "expectedVersion"); var docxPart = new ByteArrayContent(docxBytes); docxPart.Headers.ContentType = new(ManagedDocumentFileService.DocxContentType); candidateForm.Add(docxPart, "docx", "SDP-000001.00.docx"); var pdfPart = new ByteArrayContent(pdf.Content); pdfPart.Headers.ContentType = new(pdf.ContentType); candidateForm.Add(pdfPart, "pdf", pdf.FileName);
        using var candidateRequest = new HttpRequestMessage(HttpMethod.Post, $"/api/document-connector/{grantId}/release-candidate") { Content = candidateForm }; candidateRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token); using var candidate = await quality.SendAsync(candidateRequest); Assert.Equal(HttpStatusCode.OK, candidate.StatusCode); var candidateBody = await candidate.Content.ReadFromJsonAsync<JsonElement>();
        using var candidateRetryForm = new MultipartFormDataContent(); candidateRetryForm.Add(new StringContent(sessionVersion.ToString()), "expectedVersion"); var retryDocxPart = new ByteArrayContent(docxBytes); retryDocxPart.Headers.ContentType = new(ManagedDocumentFileService.DocxContentType); candidateRetryForm.Add(retryDocxPart, "docx", "SDP-000001.00.docx"); var retryPdfPart = new ByteArrayContent(pdf.Content); retryPdfPart.Headers.ContentType = new(pdf.ContentType); candidateRetryForm.Add(retryPdfPart, "pdf", pdf.FileName);
        using var candidateRetryRequest = new HttpRequestMessage(HttpMethod.Post, $"/api/document-connector/{grantId}/release-candidate") { Content = candidateRetryForm }; candidateRetryRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token); using var candidateRetry = await quality.SendAsync(candidateRetryRequest); Assert.Equal(HttpStatusCode.OK, candidateRetry.StatusCode); var candidateRetryBody = await candidateRetry.Content.ReadFromJsonAsync<JsonElement>(); Assert.Equal(candidateBody.GetProperty("manifestHash").GetString(), candidateRetryBody.GetProperty("manifestHash").GetString());
        using (var candidateScope = factory.Services.CreateScope())
        {
            var candidateDb = candidateScope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var operation = await candidateDb.ManagedDocumentStorageOperations.SingleAsync(x => x.RevisionId == revisionId && x.OperationType == "ConnectorReleaseCandidate");
            Assert.Equal(ManagedDocumentStorageOperationState.Available, operation.State);
            Assert.Equal(2, JsonSerializer.Deserialize<List<ManagedDocumentStagedObject>>(operation.ObjectManifestJson)!.Count);
            Assert.Equal(2, await candidateDb.ControlledAttachments.CountAsync(x => x.RevisionId == revisionId && (x.Label == "Released DOCX" || x.Label == "Released PDF")));
        }
        var candidateDetail = await owner.GetFromJsonAsync<JsonElement>($"/api/managed-documents/{documentId}"); var candidateRevision = candidateDetail.GetProperty("revisions")[0]; var candidatePdfId = candidateRevision.GetProperty("releaseCandidatePdfAttachmentId").GetGuid();
        using (var serviceScope = factory.Services.CreateScope())
        {
            var db = serviceScope.ServiceProvider.GetRequiredService<AeroLinkDbContext>(); var store = serviceScope.ServiceProvider.GetRequiredService<EvidenceFileStore>(); var attachment = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.SingleAsync(db.ControlledAttachments.Where(x => x.Id == candidatePdfId));
            var path = Path.Combine(store.RootPath, attachment.StorageKey.Replace('/', Path.DirectorySeparatorChar)); var altered = await File.ReadAllBytesAsync(path); altered[^1] ^= 0x01; await File.WriteAllBytesAsync(path, altered);
        }
        using var blockedRelease = await DecideAsync(quality, documentId, revisionId, "approve", "I authorize this exact controlled release.", "The altered candidate must never be signed."); Assert.Equal(HttpStatusCode.Conflict, blockedRelease.StatusCode);
        using (var recoveryForm = new MultipartFormDataContent())
        {
            recoveryForm.Add(new StringContent("Recover the exact independently retained candidate PDF before signature."), "reason"); var recoveryFile = new ByteArrayContent(pdf.Content); recoveryFile.Headers.ContentType = new(pdf.ContentType); recoveryForm.Add(recoveryFile, "file", pdf.FileName);
            using var recovery = await quality.PostAsync($"/api/managed-documents/attachments/{candidatePdfId}/restore", recoveryForm); Assert.Equal(HttpStatusCode.OK, recovery.StatusCode);
        }
        using var release = await DecideAsync(quality, documentId, revisionId, "approve", "I authorize this exact controlled release.", "Exact DOCX/PDF candidate and formal scope are conforming."); Assert.Equal(HttpStatusCode.OK, release.StatusCode);

        var detail = await owner.GetFromJsonAsync<JsonElement>($"/api/managed-documents/{documentId}"); var revision = detail.GetProperty("revisions")[0]; var expectedManifest = ManagedDocumentFileService.Sha256(System.Text.Encoding.UTF8.GetBytes($"managed-document-release-v3:{reviewedSha256}:{ManagedDocumentFileService.ReleaseTransformationProfile}:{ManagedDocumentFileService.ReleaseTransformationVersion}:{candidateBody.GetProperty("docxSha256").GetString()}:{candidateBody.GetProperty("pdfSha256").GetString()}:{revision.GetProperty("formalSummaryHash").GetString()}:{revision.GetProperty("formalSummaryVersion").GetInt64()}:{revision.GetProperty("submittedRelationshipManifestHash").GetString()}"));
        Assert.Equal(expectedManifest, revision.GetProperty("releaseManifestHash").GetString());
        Assert.All(revision.GetProperty("attachments").EnumerateArray().Where(item => item.GetProperty("id").GetGuid() == revision.GetProperty("releasedDocxAttachmentId").GetGuid() || item.GetProperty("id").GetGuid() == revision.GetProperty("releasedPdfAttachmentId").GetGuid()), item => Assert.Contains("Formal revision scope v1", item.GetProperty("description").GetString()));
        Assert.Contains(detail.GetProperty("signatures").EnumerateArray(), item => item.GetProperty("action").GetString() == "Approve" && item.GetProperty("contentHash").GetString() == revision.GetProperty("snapshotHash").GetString());
        Assert.Contains(detail.GetProperty("signatures").EnumerateArray(), item => item.GetProperty("action").GetString() == "Release" && item.GetProperty("contentHash").GetString() == expectedManifest);
        foreach (var releasedFile in revision.GetProperty("attachments").EnumerateArray().Where(item => item.GetProperty("id").GetGuid() == revision.GetProperty("releasedDocxAttachmentId").GetGuid() || item.GetProperty("id").GetGuid() == revision.GetProperty("releasedPdfAttachmentId").GetGuid()))
        {
            var downloaded = await owner.GetByteArrayAsync(releasedFile.GetProperty("downloadUrl").GetString()!);
            Assert.Equal(releasedFile.GetProperty("sha256").GetString(), ManagedDocumentFileService.Sha256(downloaded));
        }
        using var releasedEdit = await owner.PatchAsJsonAsync($"/api/managed-documents/revisions/{revisionId}/formal-summary", new { formalChangeSummary = "Forbidden after release.", reason = "Must fail.", expectedVersion = revision.GetProperty("version").GetInt64() }); Assert.Equal(HttpStatusCode.Conflict, releasedEdit.StatusCode);
    }

    [Fact]
    public async Task Release_candidate_rejects_unrelated_docx_bad_pdf_and_misleading_names_without_persisting_any_state()
    {
        using var factory = new AeroLinkApiFactory(); using var owner = factory.CreateClient(); await ProblemReportApiTests.BootstrapAndLoginAsync(owner); var scope = await SeedProjectAsync(factory);
        using var created = await owner.PostAsJsonAsync("/api/managed-documents", new { projectId = scope.ProjectId, acronym = "SDA", documentType = "Software Development Plan", title = "Assurance plan", ownerId = "software.author", changeSummary = "Exact reviewed assurance scope.", operationKey = Guid.NewGuid().ToString("N") });
        var createdBody = await created.Content.ReadFromJsonAsync<JsonElement>(); var documentId = createdBody.GetProperty("id").GetGuid(); var revisionId = createdBody.GetProperty("revisionId").GetGuid();
        using var submitted = await SubmitAsync(owner, documentId, revisionId, "software.lead", "quality.analyst"); Assert.Equal(HttpStatusCode.OK, submitted.StatusCode);

        using var technical = factory.CreateClient(); using (var login = await technical.PostAsJsonAsync("/api/auth/login", new { userName = "software.lead", password = AeroLinkApiFactory.MemberPassword })) Assert.Equal(HttpStatusCode.OK, login.StatusCode); await SecurityBoundaryTests.AuthorizeMutationsAsync(technical);
        using var technicalApproval = await DecideAsync(technical, documentId, revisionId, "approve", "I confirm the technical review is complete.", "The exact snapshot is technically complete."); Assert.Equal(HttpStatusCode.OK, technicalApproval.StatusCode);

        using var quality = factory.CreateClient(); using (var login = await quality.PostAsJsonAsync("/api/auth/login", new { userName = "quality.analyst", password = AeroLinkApiFactory.MemberPassword })) Assert.Equal(HttpStatusCode.OK, login.StatusCode); await SecurityBoundaryTests.AuthorizeMutationsAsync(quality);
        using var preparation = await quality.PostAsync($"/api/managed-documents/revisions/{revisionId}/release-preparation", null); Assert.Equal(HttpStatusCode.OK, preparation.StatusCode); var preparationBody = await preparation.Content.ReadFromJsonAsync<JsonElement>();
        var ticket = LaunchEnvelope(new Uri(preparationBody.GetProperty("launchUri").GetString()!)).Nonce;
        using var redeemed = await quality.PostAsync($"/api/document-connector/redeem/{Uri.EscapeDataString(ticket)}", null); var grant = await redeemed.Content.ReadFromJsonAsync<JsonElement>();
        var grantId = grant.GetProperty("id").GetGuid(); var token = grant.GetProperty("accessToken").GetString()!; var sessionVersion = grant.GetProperty("sessionVersion").GetInt64();

        byte[] workingBytes;
        using (var serviceScope = factory.Services.CreateScope())
        {
            var db = serviceScope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var store = serviceScope.ServiceProvider.GetRequiredService<EvidenceFileStore>();
            var revision = await db.ManagedDocumentRevisions.SingleAsync(x => x.Id == revisionId);
            var working = await db.ControlledAttachments.SingleAsync(x => x.Id == revision.CurrentWorkingAttachmentId);
            workingBytes = await File.ReadAllBytesAsync(Path.Combine(store.RootPath, working.StorageKey.Replace('/', Path.DirectorySeparatorChar)));
        }
        var correctDocx = ManagedDocumentFileService.ApplyReleaseMarking(workingBytes);
        var pdfPublication = new ProfessionalPublication("AeroLink", "Program", "Project", "Software Development Plan", "Assurance plan", "Controlled Project document", "SDA-000001", "00", "Released", "Project-wide", "All software builds", "admin", DateTimeOffset.UtcNow, new string('c', 64), [], [], [], [new("Purpose", "Scope", [new("1", "Plan", "Purpose", "Controlled content.", [])])]);
        var validPdf = ProfessionalPublicationRenderer.Render(pdfPublication, "pdf", "SDA-000001.00").Content;
        var unrelatedDraft = new ProfessionalPublication("AeroLink", "Program", "Project", "Software Development Plan", "Assurance plan", "Controlled Project document", "SDA-000001", "00", "Draft", "Project-wide", "All software builds", "admin", DateTimeOffset.UtcNow, new string('c', 64), [], [], [], [new("Purpose", "Scope", [new("1", "Plan", "Purpose", "Different unreviewed content.", [])])]) { Watermark = "DRAFT", ControlledStatusControls = true };
        var unrelatedDocx = ManagedDocumentFileService.ApplyReleaseMarking(ProfessionalPublicationRenderer.Render(unrelatedDraft, "docx", "SDA-000001.00").Content);

        static MultipartFormDataContent CandidateForm(long version, byte[] docx, string docxName, byte[] pdf, string pdfName)
        {
            var form = new MultipartFormDataContent { { new StringContent(version.ToString()), "expectedVersion" } };
            var docxPart = new ByteArrayContent(docx); docxPart.Headers.ContentType = new(ManagedDocumentFileService.DocxContentType); form.Add(docxPart, "docx", docxName);
            var pdfPart = new ByteArrayContent(pdf); pdfPart.Headers.ContentType = new(ManagedDocumentFileService.PdfContentType); form.Add(pdfPart, "pdf", pdfName);
            return form;
        }
        async Task<(HttpStatusCode Status, string Code)> PostCandidate(byte[] docx, string docxName, byte[] pdf, string pdfName)
        {
            using var form = CandidateForm(sessionVersion, docx, docxName, pdf, pdfName);
            using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/document-connector/{grantId}/release-candidate") { Content = form };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await quality.SendAsync(request);
            var json = await response.Content.ReadFromJsonAsync<JsonElement>();
            return (response.StatusCode, json.TryGetProperty("code", out var code) ? code.GetString() ?? "" : "");
        }

        var unrelated = await PostCandidate(unrelatedDocx, "SDA-000001.00.docx", validPdf, "SDA-000001.00.pdf");
        Assert.Equal(HttpStatusCode.Conflict, unrelated.Status);
        Assert.Equal("candidate_source_mismatch", unrelated.Code);

        var fakePdf = await PostCandidate(correctDocx, "SDA-000001.00.docx", System.Text.Encoding.ASCII.GetBytes("%PDF-not-a-pdf"), "SDA-000001.00.pdf");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, fakePdf.Status);
        Assert.Equal("pdf_structure_invalid", fakePdf.Code);

        var misleadingName = await PostCandidate(correctDocx, "SDA-000001.00.docx", validPdf, "approved-document.exe");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, misleadingName.Status);
        Assert.Equal("invalid_pdf_filename", misleadingName.Code);

        using (var serviceScope = factory.Services.CreateScope())
        {
            var db = serviceScope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var revision = await db.ManagedDocumentRevisions.SingleAsync(x => x.Id == revisionId);
            Assert.Null(revision.ReleaseCandidateDocxAttachmentId);
            Assert.Null(revision.ReleaseCandidatePdfAttachmentId);
            Assert.Equal("", revision.ReleaseManifestHash);
            Assert.Equal(ManagedDocumentState.InReview, revision.State);
            Assert.False(await db.ControlledAttachments.AnyAsync(x => x.RevisionId == revisionId && (x.Label == "Released DOCX" || x.Label == "Released PDF")));
            Assert.False(await db.ManagedDocumentStorageOperations.AnyAsync(x => x.RevisionId == revisionId && x.OperationType == "ConnectorReleaseCandidate"));
        }

        var accepted = await PostCandidate(correctDocx, "SDA-000001.00.docx", validPdf, "SDA-000001.00.pdf");
        Assert.Equal(HttpStatusCode.OK, accepted.Status);
        var approved = await DecideAsync(quality, documentId, revisionId, "approve", "I authorize this exact controlled release.", "The exact controlled pair is conforming."); Assert.Equal(HttpStatusCode.OK, approved.StatusCode);
    }

    [Fact]
    public async Task Repreparing_a_candidate_supersedes_the_prior_pair_and_releases_only_the_latest()
    {
        using var factory = new AeroLinkApiFactory(); using var owner = factory.CreateClient(); await ProblemReportApiTests.BootstrapAndLoginAsync(owner); var scope = await SeedProjectAsync(factory);
        using var created = await owner.PostAsJsonAsync("/api/managed-documents", new { projectId = scope.ProjectId, acronym = "RSP", documentType = "Re-preparation Plan", title = "Supersession plan", ownerId = "software.author", changeSummary = "Prove candidate supersession.", operationKey = Guid.NewGuid().ToString("N") });
        var createdBody = await created.Content.ReadFromJsonAsync<JsonElement>(); var documentId = createdBody.GetProperty("id").GetGuid(); var revisionId = createdBody.GetProperty("revisionId").GetGuid();
        using var submitted = await SubmitAsync(owner, documentId, revisionId, "software.lead", "quality.analyst"); Assert.Equal(HttpStatusCode.OK, submitted.StatusCode);

        using var technical = factory.CreateClient(); using (var login = await technical.PostAsJsonAsync("/api/auth/login", new { userName = "software.lead", password = AeroLinkApiFactory.MemberPassword })) Assert.Equal(HttpStatusCode.OK, login.StatusCode); await SecurityBoundaryTests.AuthorizeMutationsAsync(technical);
        using var technicalApproval = await DecideAsync(technical, documentId, revisionId, "approve", "I confirm the technical review is complete.", "The snapshot is technically complete."); Assert.Equal(HttpStatusCode.OK, technicalApproval.StatusCode);

        using var quality = factory.CreateClient(); using (var login = await quality.PostAsJsonAsync("/api/auth/login", new { userName = "quality.analyst", password = AeroLinkApiFactory.MemberPassword })) Assert.Equal(HttpStatusCode.OK, login.StatusCode); await SecurityBoundaryTests.AuthorizeMutationsAsync(quality);

        byte[] workingBytes;
        using (var serviceScope = factory.Services.CreateScope())
        {
            var db = serviceScope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var store = serviceScope.ServiceProvider.GetRequiredService<EvidenceFileStore>();
            var revision = await db.ManagedDocumentRevisions.SingleAsync(x => x.Id == revisionId);
            var working = await db.ControlledAttachments.SingleAsync(x => x.Id == revision.CurrentWorkingAttachmentId);
            workingBytes = await File.ReadAllBytesAsync(Path.Combine(store.RootPath, working.StorageKey.Replace('/', Path.DirectorySeparatorChar)));
        }
        var candidateDocx = ManagedDocumentFileService.ApplyReleaseMarking(workingBytes);
        static byte[] Pdf(string title) => ProfessionalPublicationRenderer.Render(new ProfessionalPublication("AeroLink", "Program", "Project", "Re-preparation Plan", title, "Controlled Project document", "RSP-000001", "00", "Released", "Project-wide", "All software builds", "admin", DateTimeOffset.UtcNow, new string('c', 64), [], [], [], [new("Purpose", "Scope", [new("1", "Plan", "Purpose", "Controlled content.", [])])]), "pdf", "RSP-000001.00").Content;
        var firstPdf = Pdf("First candidate");
        var secondPdf = Pdf("Second candidate");

        async Task<(Guid GrantId, string Token, long Version)> OpenPreparationAsync()
        {
            using var preparation = await quality.PostAsync($"/api/managed-documents/revisions/{revisionId}/release-preparation", null); Assert.Equal(HttpStatusCode.OK, preparation.StatusCode);
            var body = await preparation.Content.ReadFromJsonAsync<JsonElement>();
            var ticket = LaunchEnvelope(new Uri(body.GetProperty("launchUri").GetString()!)).Nonce;
            using var redeemed = await quality.PostAsync($"/api/document-connector/redeem/{Uri.EscapeDataString(ticket)}", null);
            var grant = await redeemed.Content.ReadFromJsonAsync<JsonElement>();
            return (grant.GetProperty("id").GetGuid(), grant.GetProperty("accessToken").GetString()!, grant.GetProperty("sessionVersion").GetInt64());
        }
        async Task<HttpStatusCode> PostAsync(Guid grantId, string token, long version, byte[] pdf)
        {
            using var form = new MultipartFormDataContent { { new StringContent(version.ToString()), "expectedVersion" } };
            var docxPart = new ByteArrayContent(candidateDocx); docxPart.Headers.ContentType = new(ManagedDocumentFileService.DocxContentType); form.Add(docxPart, "docx", "RSP-000001.00.docx");
            var pdfPart = new ByteArrayContent(pdf); pdfPart.Headers.ContentType = new(ManagedDocumentFileService.PdfContentType); form.Add(pdfPart, "pdf", "RSP-000001.00.pdf");
            using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/document-connector/{grantId}/release-candidate") { Content = form };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await quality.SendAsync(request);
            return response.StatusCode;
        }

        var (firstGrant, firstToken, firstVersion) = await OpenPreparationAsync();
        Assert.Equal(HttpStatusCode.OK, await PostAsync(firstGrant, firstToken, firstVersion, firstPdf));
        var firstDetail = await owner.GetFromJsonAsync<JsonElement>($"/api/managed-documents/{documentId}");
        var firstRevision = firstDetail.GetProperty("revisions")[0];
        var firstDocxId = firstRevision.GetProperty("releaseCandidateDocxAttachmentId").GetGuid();
        var firstPdfId = firstRevision.GetProperty("releaseCandidatePdfAttachmentId").GetGuid();

        var (secondGrant, secondToken, secondVersion) = await OpenPreparationAsync();
        Assert.Equal(HttpStatusCode.OK, await PostAsync(secondGrant, secondToken, secondVersion, secondPdf));
        var secondDetail = await owner.GetFromJsonAsync<JsonElement>($"/api/managed-documents/{documentId}");
        var secondRevision = secondDetail.GetProperty("revisions")[0];
        var secondDocxId = secondRevision.GetProperty("releaseCandidateDocxAttachmentId").GetGuid();
        var secondPdfId = secondRevision.GetProperty("releaseCandidatePdfAttachmentId").GetGuid();
        Assert.NotEqual(firstPdfId, secondPdfId);

        using (var serviceScope = factory.Services.CreateScope())
        {
            var db = serviceScope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            Assert.Equal(ControlledAttachmentState.Superseded, (await db.ControlledAttachments.SingleAsync(x => x.Id == firstDocxId)).State);
            Assert.Equal(ControlledAttachmentState.Superseded, (await db.ControlledAttachments.SingleAsync(x => x.Id == firstPdfId)).State);
            Assert.Equal(ControlledAttachmentState.Active, (await db.ControlledAttachments.SingleAsync(x => x.Id == secondDocxId)).State);
            Assert.Equal(ControlledAttachmentState.Active, (await db.ControlledAttachments.SingleAsync(x => x.Id == secondPdfId)).State);
        }

        var approved = await DecideAsync(quality, documentId, revisionId, "approve", "I authorize this exact controlled release.", "Only the latest candidate is the controlled release."); Assert.Equal(HttpStatusCode.OK, approved.StatusCode);
        var releasedDetail = await owner.GetFromJsonAsync<JsonElement>($"/api/managed-documents/{documentId}");
        var releasedRevision = releasedDetail.GetProperty("revisions")[0];
        Assert.Equal(secondDocxId, releasedRevision.GetProperty("releasedDocxAttachmentId").GetGuid());
        Assert.Equal(secondPdfId, releasedRevision.GetProperty("releasedPdfAttachmentId").GetGuid());
    }

    [Fact]
    public async Task Draft_and_returned_withdrawal_closes_checkouts_but_reviewed_and_released_revisions_fail_closed()
    {
        using var factory = new AeroLinkApiFactory(); using var administrator = factory.CreateClient();
        await ProblemReportApiTests.BootstrapAndLoginAsync(administrator); var scope = await SeedProjectAsync(factory);
        using var owner = factory.CreateClient();
        using (var login = await owner.PostAsJsonAsync("/api/auth/login", new { userName = "software.author", password = AeroLinkApiFactory.MemberPassword })) Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        await SecurityBoundaryTests.AuthorizeMutationsAsync(owner);

        using var created = await owner.PostAsJsonAsync("/api/managed-documents", new { projectId = scope.ProjectId, acronym = "SVP", documentType = "Software Verification Plan", title = "Withdrawable plan", ownerId = "software.author", formalChangeSummary = "Initial controlled scope.", operationKey = "withdraw-draft" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode); var body = await created.Content.ReadFromJsonAsync<JsonElement>();
        var documentId = body.GetProperty("id").GetGuid(); var revisionId = body.GetProperty("revisionId").GetGuid();
        using var checkout = await owner.PostAsync($"/api/managed-documents/revisions/{revisionId}/checkout", null); Assert.Equal(HttpStatusCode.OK, checkout.StatusCode);
        var detail = await owner.GetFromJsonAsync<JsonElement>($"/api/managed-documents/{documentId}"); var version = detail.GetProperty("revisions")[0].GetProperty("version").GetInt64();
        using var withdrawn = await owner.PostAsJsonAsync($"/api/managed-documents/revisions/{revisionId}/withdraw", new { reason = "The Project no longer needs this initial issue.", expectedVersion = version });
        Assert.Equal(HttpStatusCode.OK, withdrawn.StatusCode);
        using (var scopeCheck = factory.Services.CreateScope())
        {
            var db = scopeCheck.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            Assert.Equal(ManagedDocumentState.Withdrawn, (await db.ManagedDocumentRevisions.SingleAsync(x => x.Id == revisionId)).State);
            Assert.All(await db.ControlledAttachments.Where(x => x.RevisionId == revisionId).ToListAsync(), item => Assert.Equal(ControlledAttachmentState.Withdrawn, item.State));
            Assert.All(await db.ArtifactEditSessions.Where(x => x.RevisionId == revisionId).ToListAsync(), item => Assert.Equal(EditSessionState.ForceUnlocked, item.State));
            Assert.Contains(await db.ManagedDocumentEvents.Where(x => x.DocumentId == documentId).ToListAsync(), item => item.EventType == "DocumentRevisionWithdrawn");
        }

        using var reviewedCreate = await owner.PostAsJsonAsync("/api/managed-documents", new { projectId = scope.ProjectId, acronym = "SQAP", documentType = "Software Quality Assurance Plan", title = "Reviewed plan", ownerId = "software.author", formalChangeSummary = "Review this exact scope.", operationKey = "withdraw-review" });
        var reviewedBody = await reviewedCreate.Content.ReadFromJsonAsync<JsonElement>(); var reviewedDocumentId = reviewedBody.GetProperty("id").GetGuid(); var reviewedRevisionId = reviewedBody.GetProperty("revisionId").GetGuid();
        using var submitted = await SubmitAsync(owner, reviewedDocumentId, reviewedRevisionId, "software.lead", "quality.analyst"); Assert.Equal(HttpStatusCode.OK, submitted.StatusCode);
        var reviewedDetail = await owner.GetFromJsonAsync<JsonElement>($"/api/managed-documents/{reviewedDocumentId}");
        using var blockedReview = await owner.PostAsJsonAsync($"/api/managed-documents/revisions/{reviewedRevisionId}/withdraw", new { reason = "Must not bypass review.", expectedVersion = reviewedDetail.GetProperty("revisions")[0].GetProperty("version").GetInt64() });
        Assert.Equal(HttpStatusCode.BadRequest, blockedReview.StatusCode);

        using var technical = factory.CreateClient(); using (var login = await technical.PostAsJsonAsync("/api/auth/login", new { userName = "software.lead", password = AeroLinkApiFactory.MemberPassword })) Assert.Equal(HttpStatusCode.OK, login.StatusCode); await SecurityBoundaryTests.AuthorizeMutationsAsync(technical);
        using var returned = await DecideAsync(technical, reviewedDocumentId, reviewedRevisionId, "return", "Return this revision.", "The controlled revision should be abandoned rather than corrected."); Assert.Equal(HttpStatusCode.OK, returned.StatusCode);
        reviewedDetail = await owner.GetFromJsonAsync<JsonElement>($"/api/managed-documents/{reviewedDocumentId}");
        using var returnedWithdraw = await owner.PostAsJsonAsync($"/api/managed-documents/revisions/{reviewedRevisionId}/withdraw", new { reason = "Withdraw the returned successor with history retained.", expectedVersion = reviewedDetail.GetProperty("revisions")[0].GetProperty("version").GetInt64() });
        Assert.Equal(HttpStatusCode.OK, returnedWithdraw.StatusCode);

        var released = await SeedReleasedDocumentAsync(factory, scope.ProjectId, "SAS");
        long releasedVersion; using (var releasedScope = factory.Services.CreateScope()) releasedVersion = (await releasedScope.ServiceProvider.GetRequiredService<AeroLinkDbContext>().ManagedDocumentRevisions.AsNoTracking().SingleAsync(x => x.Id == released.RevisionId)).Version;
        using var blockedReleased = await administrator.PostAsJsonAsync($"/api/managed-documents/revisions/{released.RevisionId}/withdraw", new { reason = "Released evidence is immutable.", expectedVersion = releasedVersion });
        Assert.Equal(HttpStatusCode.BadRequest, blockedReleased.StatusCode);
    }

    [Theory]
    [InlineData("pending-recorded")]
    [InlineData("object-staged-1")]
    [InlineData("manifest-recorded")]
    [InlineData("before-promote")]
    [InlineData("object-promoted-1")]
    [InlineData("metadata-saved")]
    public async Task Create_faults_leave_no_visible_document_and_reconcile_to_reported_rollback(string phase)
    {
        var injector = new OneShotStorageFaultInjector("DocumentCreate", phase);
        using var factory = new AeroLinkApiFactory(storageFaultInjector: injector); using var client = factory.CreateClient();
        await ProblemReportApiTests.BootstrapAndLoginAsync(client); var scope = await SeedProjectAsync(factory);
        using var failed = await client.PostAsJsonAsync("/api/managed-documents", new { projectId = scope.ProjectId,
            acronym = "ICD", documentType = "Interface Control Document", title = "Fault-injected document",
            ownerId = "software.author", formalChangeSummary = "Atomic failure coverage.", operationKey = $"fault-{phase}" });
        Assert.Equal(HttpStatusCode.InternalServerError, failed.StatusCode);

        using var reconciled = await client.PostAsync($"/api/managed-documents/projects/{scope.ProjectId}/storage/reconcile", null);
        Assert.Equal(HttpStatusCode.OK, reconciled.StatusCode);
        using var verification = factory.Services.CreateScope(); var db = verification.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        Assert.Empty(await db.ManagedDocuments.Where(x => x.ProjectId == scope.ProjectId).ToListAsync());
        Assert.Empty(await db.ControlledAttachments.Where(x => x.ProjectId == scope.ProjectId && x.ArtifactType == "ManagedDocument").ToListAsync());
        var operation = await db.ManagedDocumentStorageOperations.SingleAsync(x => x.OperationKey == $"fault-{phase}");
        Assert.Equal(ManagedDocumentStorageOperationState.RolledBack, operation.State);
        Assert.Empty(verification.ServiceProvider.GetRequiredService<EvidenceFileStore>().EnumerateStagedKeys());
    }

    [Fact]
    public async Task Create_requires_a_caller_operation_key_and_distinct_keys_do_not_collapse_equal_intent()
    {
        using var factory = new AeroLinkApiFactory(); using var client = factory.CreateClient();
        await ProblemReportApiTests.BootstrapAndLoginAsync(client); var scope = await SeedProjectAsync(factory);
        var withoutKey = new { projectId = scope.ProjectId, acronym = "ICD", documentType = "Interface Control Document",
            title = "Repeated controlled intent", ownerId = "software.author", formalChangeSummary = "Same formal scope." };

        using var refused = await client.PostAsJsonAsync("/api/managed-documents", withoutKey);
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        var refusal = await refused.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("operation_key_required", refusal.GetProperty("code").GetString());

        using var first = await client.PostAsJsonAsync("/api/managed-documents", new { withoutKey.projectId,
            withoutKey.acronym, withoutKey.documentType, withoutKey.title, withoutKey.ownerId,
            withoutKey.formalChangeSummary, operationKey = "equal-intent-1" });
        using var second = await client.PostAsJsonAsync("/api/managed-documents", new { withoutKey.projectId,
            withoutKey.acronym, withoutKey.documentType, withoutKey.title, withoutKey.ownerId,
            withoutKey.formalChangeSummary, operationKey = "equal-intent-2" });

        Assert.Equal(HttpStatusCode.Created, first.StatusCode); Assert.Equal(HttpStatusCode.Created, second.StatusCode);
        var firstBody = await first.Content.ReadFromJsonAsync<JsonElement>(); var secondBody = await second.Content.ReadFromJsonAsync<JsonElement>();
        Assert.NotEqual(firstBody.GetProperty("id").GetGuid(), secondBody.GetProperty("id").GetGuid());
        Assert.Equal("ICD-000001", firstBody.GetProperty("documentNumber").GetString());
        Assert.Equal("ICD-000002", secondBody.GetProperty("documentNumber").GetString());
    }

    [Fact]
    public async Task Create_fault_after_metadata_commit_reconciles_to_available_and_retry_returns_the_same_result()
    {
        const string operationKey = "fault-after-metadata-commit";
        var injector = new OneShotStorageFaultInjector("DocumentCreate", "before-available-recorded");
        using var factory = new AeroLinkApiFactory(storageFaultInjector: injector);
        using var client = factory.CreateClient();
        await ProblemReportApiTests.BootstrapAndLoginAsync(client);
        var scope = await SeedProjectAsync(factory);
        var request = new { projectId = scope.ProjectId, acronym = "ICD", documentType = "Interface Control Document",
            title = "Committed fault-injected document", ownerId = "software.author",
            formalChangeSummary = "Post-commit recovery coverage.", operationKey };

        using var failed = await client.PostAsJsonAsync("/api/managed-documents", request);
        Assert.Equal(HttpStatusCode.InternalServerError, failed.StatusCode);
        using var retried = await client.PostAsJsonAsync("/api/managed-documents", request);
        Assert.Equal(HttpStatusCode.Created, retried.StatusCode);
        var retriedResult = await retried.Content.ReadFromJsonAsync<JsonElement>();

        using var verification = factory.Services.CreateScope();
        var db = verification.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        var document = Assert.Single(await db.ManagedDocuments.Where(x => x.ProjectId == scope.ProjectId).ToListAsync());
        var operation = await db.ManagedDocumentStorageOperations.SingleAsync(x => x.OperationKey == operationKey);
        Assert.Equal(ManagedDocumentStorageOperationState.Available, operation.State);
        Assert.Equal(document.Id, retriedResult.GetProperty("id").GetGuid());
        Assert.Single(await db.ControlledAttachments.Where(x => x.ProjectId == scope.ProjectId && x.ArtifactType == "ManagedDocument").ToListAsync());
        Assert.Empty(verification.ServiceProvider.GetRequiredService<EvidenceFileStore>().EnumerateStagedKeys());
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

    [Fact]
    public async Task Review_decisions_bind_exact_intent_and_retry_idempotently()
    {
        using var factory = new AeroLinkApiFactory(); using var owner = factory.CreateClient();
        await ProblemReportApiTests.BootstrapAndLoginAsync(owner); var scope = await SeedProjectAsync(factory);
        using var created = await owner.PostAsJsonAsync("/api/managed-documents", new { projectId = scope.ProjectId, acronym = "SVP", documentType = "Software Verification Plan", title = "Exact review intent", ownerId = "software.author", formalChangeSummary = "Qualify exact review intent.", operationKey = Guid.NewGuid().ToString("N") });
        var createdBody = await created.Content.ReadFromJsonAsync<JsonElement>(); var documentId = createdBody.GetProperty("id").GetGuid(); var revisionId = createdBody.GetProperty("revisionId").GetGuid();
        using var submitted = await SubmitAsync(owner, documentId, revisionId, "software.lead", "quality.analyst"); Assert.Equal(HttpStatusCode.OK, submitted.StatusCode);
        var submittedDetail = await owner.GetFromJsonAsync<JsonElement>($"/api/managed-documents/{documentId}"); var submittedRevision = submittedDetail.GetProperty("revisions")[0];
        var technicalStep = submittedRevision.GetProperty("reviewSteps").EnumerateArray().Single(x => x.GetProperty("state").GetString() == "Active");
        Assert.Equal("TechnicalDocumentReview", technicalStep.GetProperty("requiredAuthority").GetString());
        Assert.Equal("SoftwareEngineeringLead", technicalStep.GetProperty("grantedAuthority").GetString());
        // The signature records that the technical reviewer signed as the holder of the Software Engineering
        // Lead position, which is what they are. Recording it as direct membership — as this did before #816
        // separated the two — misdescribes who was accountable on a controlled signature.
        Assert.Equal("ProjectLeadershipPrimary", technicalStep.GetProperty("authoritySource").GetString());
        Assert.NotEqual(Guid.Empty, technicalStep.GetProperty("authoritySourceId").GetGuid());
        Assert.Equal(Guid.Parse("89d7b639-96f1-4fd4-970a-8a0db066c493"), technicalStep.GetProperty("workflowId").GetGuid());
        Assert.Equal(2, technicalStep.GetProperty("workflowVersion").GetInt32());
        Assert.Equal("FrozenAtAssignment;ActiveAccountAtSigning", technicalStep.GetProperty("authorityPolicy").GetString());

        using var technical = factory.CreateClient(); using (var login = await technical.PostAsJsonAsync("/api/auth/login", new { userName = "software.lead", password = AeroLinkApiFactory.MemberPassword })) Assert.Equal(HttpStatusCode.OK, login.StatusCode); await SecurityBoundaryTests.AuthorizeMutationsAsync(technical);
        using var concurrentTechnical = factory.CreateClient(); using (var login = await concurrentTechnical.PostAsJsonAsync("/api/auth/login", new { userName = "software.lead", password = AeroLinkApiFactory.MemberPassword })) Assert.Equal(HttpStatusCode.OK, login.StatusCode); await SecurityBoundaryTests.AuthorizeMutationsAsync(concurrentTechnical);
        var operationKey = Guid.NewGuid().ToString("N"); var decision = DecisionPayload(submittedRevision, technicalStep, operationKey, "Technical approval", "The exact submitted hashes and formal scope are acceptable.");
        var concurrent = await Task.WhenAll(technical.PostAsJsonAsync($"/api/managed-documents/revisions/{revisionId}/review/approve", decision), concurrentTechnical.PostAsJsonAsync($"/api/managed-documents/revisions/{revisionId}/review/approve", decision));
        Assert.All(concurrent, response => Assert.Equal(HttpStatusCode.OK, response.StatusCode)); var firstJson = await concurrent[0].Content.ReadAsStringAsync(); Assert.Equal(firstJson, await concurrent[1].Content.ReadAsStringAsync());
        foreach (var response in concurrent) response.Dispose();
        using var retry = await technical.PostAsJsonAsync($"/api/managed-documents/revisions/{revisionId}/review/approve", decision); Assert.Equal(HttpStatusCode.OK, retry.StatusCode); Assert.Equal(firstJson, await retry.Content.ReadAsStringAsync());
        decision["rationale"] = "Different intent under a reused key.";
        using var conflictingRetry = await technical.PostAsJsonAsync($"/api/managed-documents/revisions/{revisionId}/review/approve", decision); Assert.Equal(HttpStatusCode.Conflict, conflictingRetry.StatusCode);
        decision["operationKey"] = Guid.NewGuid().ToString("N"); decision["rationale"] = "The exact submitted hashes and formal scope are acceptable.";
        using var stale = await technical.PostAsJsonAsync($"/api/managed-documents/revisions/{revisionId}/review/approve", decision); Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        var finalDetail = await owner.GetFromJsonAsync<JsonElement>($"/api/managed-documents/{documentId}");
        var signatures = finalDetail.GetProperty("signatures").EnumerateArray().Where(x => x.GetProperty("action").GetString() == "Approve").ToList(); Assert.Single(signatures);
        Assert.Equal(technicalStep.GetProperty("authoritySourceId").GetGuid(), signatures[0].GetProperty("authoritySourceId").GetGuid());
        Assert.Equal("Technical approval", signatures[0].GetProperty("meaning").GetString()); Assert.Equal("The exact submitted hashes and formal scope are acceptable.", signatures[0].GetProperty("rationale").GetString());
    }

    [Fact]
    public async Task Author_can_submit_and_reroute_an_ordered_named_review_and_approval_chain()
    {
        using var factory = new AeroLinkApiFactory(); using var owner = factory.CreateClient();
        await ProblemReportApiTests.BootstrapAndLoginAsync(owner); var scope = await SeedProjectAsync(factory);
        using (var serviceScope = factory.Services.CreateScope())
        {
            var db = serviceScope.ServiceProvider.GetRequiredService<AeroLinkDbContext>(); var now = DateTimeOffset.UtcNow;
            var otherProgram = new ProgramRecord("Other review Program", $"OR{Guid.NewGuid():N}"[..12]);
            var outsider = new UserAccount("other.program.reviewer", "Other Program Reviewer", "other.review@example.test", IdentityService.HashPassword(AeroLinkApiFactory.MemberPassword), now);
            db.AddRange(otherProgram, outsider, new ProgramMembership(outsider.Id, otherProgram.Id, ProgramRole.Reviewer, "admin", now));
            await db.SaveChangesAsync();
        }
        using var created = await owner.PostAsJsonAsync("/api/managed-documents", new { projectId = scope.ProjectId, acronym = "RTP", documentType = "Review Test Plan", title = "Configurable review route", ownerId = "software.author", formalChangeSummary = "Prove ordered per-cycle routing.", operationKey = Guid.NewGuid().ToString("N") });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var createdBody = await created.Content.ReadFromJsonAsync<JsonElement>(); var documentId = createdBody.GetProperty("id").GetGuid(); var revisionId = createdBody.GetProperty("revisionId").GetGuid();

        using var tooShort = await SubmitRouteAsync(owner, documentId, revisionId,
            [("quality.analyst", "Release authorization", "Approval")]);
        Assert.Equal(HttpStatusCode.BadRequest, tooShort.StatusCode);
        using var duplicate = await SubmitRouteAsync(owner, documentId, revisionId,
            [("system.reviewer", "Peer review", "Review"), ("system.reviewer", "Release authorization", "Approval")]);
        Assert.Equal(HttpStatusCode.BadRequest, duplicate.StatusCode);
        using var ownerInRoute = await SubmitRouteAsync(owner, documentId, revisionId,
            [("software.author", "Self review", "Review"), ("quality.analyst", "Release authorization", "Approval")]);
        Assert.Equal(HttpStatusCode.BadRequest, ownerInRoute.StatusCode);
        using var crossProgram = await SubmitRouteAsync(owner, documentId, revisionId,
            [("other.program.reviewer", "Foreign Program review", "Review"), ("quality.analyst", "Release authorization", "Approval")]);
        Assert.Equal(HttpStatusCode.BadRequest, crossProgram.StatusCode);
        using var wrongOrder = await SubmitRouteAsync(owner, documentId, revisionId,
            [("system.reviewer", "Premature approval", "Approval"), ("software.lead", "Late review", "Review"), ("quality.analyst", "Release authorization", "Approval")]);
        Assert.Equal(HttpStatusCode.BadRequest, wrongOrder.StatusCode);
        using var invalidKind = await SubmitRouteAsync(owner, documentId, revisionId,
            [("system.reviewer", "Unclassified stage", (object)999), ("quality.analyst", "Release authorization", "Approval")]);
        Assert.Equal(HttpStatusCode.BadRequest, invalidKind.StatusCode);

        using var submitted = await SubmitRouteAsync(owner, documentId, revisionId,
        [
            ("system.reviewer", "Peer architecture review", "Review"),
            ("software.lead", "Software discipline review", "Review"),
            ("quality.analyst", "Independent release approval", "Approval")
        ]);
        Assert.Equal(HttpStatusCode.OK, submitted.StatusCode);
        var detail = await owner.GetFromJsonAsync<JsonElement>($"/api/managed-documents/{documentId}"); var revision = detail.GetProperty("revisions")[0];
        var firstCycle = revision.GetProperty("reviewSteps").EnumerateArray().Where(x => x.GetProperty("cycle").GetInt32() == 1).OrderBy(x => x.GetProperty("position").GetInt32()).ToList();
        Assert.Equal(["Peer architecture review", "Software discipline review", "Independent release approval"], firstCycle.Select(x => x.GetProperty("stageName").GetString()));
        Assert.Equal(["Review", "Review", "Approval"], firstCycle.Select(x => x.GetProperty("kind").GetString()));
        Assert.Equal(["Active", "Pending", "Pending"], firstCycle.Select(x => x.GetProperty("state").GetString()));

        using var reviewer = factory.CreateClient();
        using (var login = await reviewer.PostAsJsonAsync("/api/auth/login", new { userName = "system.reviewer", password = AeroLinkApiFactory.MemberPassword })) Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        await SecurityBoundaryTests.AuthorizeMutationsAsync(reviewer);
        using var returned = await reviewer.PostAsJsonAsync($"/api/managed-documents/revisions/{revisionId}/review/return",
            DecisionPayload(revision, firstCycle[0], Guid.NewGuid().ToString("N"), "Return for correction", "Route this revision through the revised discipline chain."));
        Assert.Equal(HttpStatusCode.OK, returned.StatusCode);

        using var rerouted = await SubmitRouteAsync(owner, documentId, revisionId,
        [
            ("software.lead", "Corrected software review", "Review"),
            ("quality.analyst", "Corrected release approval", "Approval")
        ]);
        Assert.Equal(HttpStatusCode.OK, rerouted.StatusCode);
        var reroutedDetail = await owner.GetFromJsonAsync<JsonElement>($"/api/managed-documents/{documentId}"); var reroutedRevision = reroutedDetail.GetProperty("revisions")[0];
        var secondCycle = reroutedRevision.GetProperty("reviewSteps").EnumerateArray().Where(x => x.GetProperty("cycle").GetInt32() == 2).OrderBy(x => x.GetProperty("position").GetInt32()).ToList();
        Assert.Equal(["Corrected software review", "Corrected release approval"], secondCycle.Select(x => x.GetProperty("stageName").GetString()));
        Assert.Equal(["Review", "Approval"], secondCycle.Select(x => x.GetProperty("kind").GetString()));
        Assert.Contains(reroutedRevision.GetProperty("reviewSteps").EnumerateArray(), x => x.GetProperty("cycle").GetInt32() == 1 && x.GetProperty("state").GetString() == "Returned");
    }

    [Fact]
    public async Task Delegated_authority_is_frozen_at_assignment_but_disabled_signers_fail_closed()
    {
        using var factory = new AeroLinkApiFactory(); using var owner = factory.CreateClient();
        await ProblemReportApiTests.BootstrapAndLoginAsync(owner); var scope = await SeedProjectAsync(factory); Guid delegationId; Guid delegateAccountId;
        using (var serviceScope = factory.Services.CreateScope())
        {
            var db = serviceScope.ServiceProvider.GetRequiredService<AeroLinkDbContext>(); var now = DateTimeOffset.UtcNow;
            var account = new UserAccount("delegated.reviewer", "Delegated Reviewer", "delegated.review@example.test", IdentityService.HashPassword(AeroLinkApiFactory.MemberPassword), now);
            var membership = new ProgramMembership(account.Id, scope.ProgramId, ProgramRole.SoftwareEngineer, "admin", now);
            var principal = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.SingleAsync(db.UserAccounts.Where(x => x.UserName == "software.lead"));
            var delegation = new RoleDelegation(scope.ProgramId, principal.Id, account.Id, ProgramRole.Reviewer, now.AddMinutes(-1), now.AddHours(1), "Controlled document review delegation.", "admin", now);
            db.AddRange(account, membership, delegation); await db.SaveChangesAsync(); delegationId = delegation.Id; delegateAccountId = account.Id;
        }
        using var created = await owner.PostAsJsonAsync("/api/managed-documents", new { projectId = scope.ProjectId, acronym = "ICD", documentType = "Interface Control Document", title = "Delegated review", ownerId = "software.author", formalChangeSummary = "Exercise delegated authority.", operationKey = Guid.NewGuid().ToString("N") });
        var createdBody = await created.Content.ReadFromJsonAsync<JsonElement>(); var documentId = createdBody.GetProperty("id").GetGuid(); var revisionId = createdBody.GetProperty("revisionId").GetGuid();
        using var submitted = await SubmitAsync(owner, documentId, revisionId, "delegated.reviewer", "quality.analyst"); Assert.Equal(HttpStatusCode.OK, submitted.StatusCode);
        var detail = await owner.GetFromJsonAsync<JsonElement>($"/api/managed-documents/{documentId}"); var revision = detail.GetProperty("revisions")[0]; var step = revision.GetProperty("reviewSteps").EnumerateArray().Single(x => x.GetProperty("state").GetString() == "Active");
        Assert.Equal("ActiveDelegation", step.GetProperty("authoritySource").GetString()); Assert.Equal(delegationId, step.GetProperty("authoritySourceId").GetGuid());
        using (var serviceScope = factory.Services.CreateScope()) { var db = serviceScope.ServiceProvider.GetRequiredService<AeroLinkDbContext>(); var delegation = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.SingleAsync(db.RoleDelegations.Where(x => x.Id == delegationId)); delegation.Revoke(DateTimeOffset.UtcNow); await db.SaveChangesAsync(); }
        using var reviewer = factory.CreateClient(); using (var login = await reviewer.PostAsJsonAsync("/api/auth/login", new { userName = "delegated.reviewer", password = AeroLinkApiFactory.MemberPassword })) Assert.Equal(HttpStatusCode.OK, login.StatusCode); await SecurityBoundaryTests.AuthorizeMutationsAsync(reviewer);
        using var approved = await reviewer.PostAsJsonAsync($"/api/managed-documents/revisions/{revisionId}/review/approve", DecisionPayload(revision, step, Guid.NewGuid().ToString("N"), "Delegated technical approval", "Authority was valid and frozen when this review step was assigned.")); Assert.Equal(HttpStatusCode.OK, approved.StatusCode);

        using var secondCreated = await owner.PostAsJsonAsync("/api/managed-documents", new { projectId = scope.ProjectId, acronym = "SAS", documentType = "Software Accomplishment Summary", title = "Disabled signer", ownerId = "software.author", formalChangeSummary = "Fail closed for disabled signer.", operationKey = Guid.NewGuid().ToString("N") });
        var secondBody = await secondCreated.Content.ReadFromJsonAsync<JsonElement>(); var secondDocumentId = secondBody.GetProperty("id").GetGuid(); var secondRevisionId = secondBody.GetProperty("revisionId").GetGuid();
        using (var serviceScope = factory.Services.CreateScope()) { var db = serviceScope.ServiceProvider.GetRequiredService<AeroLinkDbContext>(); var account = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.SingleAsync(db.UserAccounts.Where(x => x.Id == delegateAccountId)); var principal = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.SingleAsync(db.UserAccounts.Where(x => x.UserName == "software.lead")); db.RoleDelegations.Add(new(scope.ProgramId, principal.Id, account.Id, ProgramRole.Reviewer, DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1), "Second controlled assignment.", "admin", DateTimeOffset.UtcNow)); await db.SaveChangesAsync(); }
        using var secondSubmitted = await SubmitAsync(owner, secondDocumentId, secondRevisionId, "delegated.reviewer", "quality.analyst"); Assert.Equal(HttpStatusCode.OK, secondSubmitted.StatusCode);
        var secondDetail = await owner.GetFromJsonAsync<JsonElement>($"/api/managed-documents/{secondDocumentId}"); var secondRevision = secondDetail.GetProperty("revisions")[0]; var secondStep = secondRevision.GetProperty("reviewSteps").EnumerateArray().Single(x => x.GetProperty("state").GetString() == "Active");
        using (var serviceScope = factory.Services.CreateScope()) { var db = serviceScope.ServiceProvider.GetRequiredService<AeroLinkDbContext>(); var account = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.SingleAsync(db.UserAccounts.Where(x => x.Id == delegateAccountId)); account.Disable(DateTimeOffset.UtcNow); await db.SaveChangesAsync(); }
        using var disabled = await reviewer.PostAsJsonAsync($"/api/managed-documents/revisions/{secondRevisionId}/review/approve", DecisionPayload(secondRevision, secondStep, Guid.NewGuid().ToString("N"), "Must not sign", "Disabled accounts cannot exercise even frozen authority.")); Assert.Equal(HttpStatusCode.Unauthorized, disabled.StatusCode);
    }

    [Fact]
    public async Task Project_inventory_and_histories_are_bounded_filtered_and_cursor_scoped()
    {
        var commands = new ProblemReportPagingCommandInterceptor();
        using var factory = new AeroLinkApiFactory(commandInterceptor: commands); using var client = factory.CreateClient();
        await ProblemReportApiTests.BootstrapAndLoginAsync(client); var scope = await SeedProjectAsync(factory);
        Guid historyDocumentId;
        using (var serviceScope = factory.Services.CreateScope())
        {
            var db = serviceScope.ServiceProvider.GetRequiredService<AeroLinkDbContext>(); var now = DateTimeOffset.UtcNow.AddMinutes(-10);
            var documents = new List<ManagedDocument>(); var revisions = new List<ManagedDocumentRevision>(); var events = new List<ManagedDocumentEvent>();
            for (var index = 1; index <= 123; index++)
            {
                var document = new ManagedDocument(scope.ProjectId, $"DOC-{index:D6}", "DOC", index % 2 == 0 ? "Project Plan" : "Assurance Plan",
                    $"Scale document {index:D3}", index % 3 == 0 ? "software.lead" : "software.author", now.AddMilliseconds(index), "admin");
                documents.Add(document); revisions.Add(new ManagedDocumentRevision(document.Id, 0, index % 3 == 0 ? "software.lead" : "software.author", "Scale qualification.", now.AddMilliseconds(index), initiatedBy: "admin"));
                if (index == 1) for (var eventIndex = 1; eventIndex <= 27; eventIndex++)
                    events.Add(new ManagedDocumentEvent(document.Id, "ScaleEvent", "admin", $"Bounded history event {eventIndex:D2}", now.AddSeconds(eventIndex)));
            }
            historyDocumentId = documents[0].Id; db.AddRange(documents); db.AddRange(revisions); db.AddRange(events); await db.SaveChangesAsync();
        }

        commands.Clear();
        var first = await client.GetFromJsonAsync<JsonElement>($"/api/managed-documents?projectId={scope.ProjectId}&pageSize=25");
        Assert.Equal(123, first.GetProperty("totalCount").GetInt32()); Assert.Equal(25, first.GetProperty("items").GetArrayLength()); Assert.True(first.GetProperty("hasMore").GetBoolean());
        Assert.InRange(commands.Commands.Count, 1, 8); Assert.InRange(JsonSerializer.SerializeToUtf8Bytes(first).Length, 1, 256 * 1024);
        var inventorySql = commands.Commands.Where(command => command.Contains("managed_documents", StringComparison.OrdinalIgnoreCase)).ToArray();
        Assert.Contains(inventorySql, command => command.Contains("COUNT", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(inventorySql, command => command.Contains("LIMIT", StringComparison.OrdinalIgnoreCase));
        using (var serviceScope = factory.Services.CreateScope())
        {
            var db = serviceScope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var addedAfterSnapshot = new ManagedDocument(scope.ProjectId, "DOC-000000", "DOC", "Project Plan", "Added while paging", "software.author", DateTimeOffset.UtcNow.AddMinutes(1), "admin");
            db.AddRange(addedAfterSnapshot, new ManagedDocumentRevision(addedAfterSnapshot.Id, 0, "software.author", "Concurrent addition.", DateTimeOffset.UtcNow.AddMinutes(1), initiatedBy: "admin"));
            await db.SaveChangesAsync();
        }
        var numbers = first.GetProperty("items").EnumerateArray().Select(x => x.GetProperty("documentNumber").GetString()!).ToList();
        var cursor = first.GetProperty("nextCursor").GetString();
        while (!string.IsNullOrEmpty(cursor))
        {
            var page = await client.GetFromJsonAsync<JsonElement>($"/api/managed-documents?projectId={scope.ProjectId}&pageSize=25&cursor={Uri.EscapeDataString(cursor)}");
            numbers.AddRange(page.GetProperty("items").EnumerateArray().Select(x => x.GetProperty("documentNumber").GetString()!));
            cursor = page.GetProperty("nextCursor").ValueKind == JsonValueKind.Null ? null : page.GetProperty("nextCursor").GetString();
        }
        Assert.Equal(123, numbers.Count); Assert.Equal(123, numbers.Distinct().Count()); Assert.Equal(numbers.Order(), numbers); Assert.DoesNotContain("DOC-000000", numbers);

        var filtered = await client.GetFromJsonAsync<JsonElement>($"/api/managed-documents?projectId={scope.ProjectId}&documentType=Project%20Plan&owner=software.lead&pageSize=100");
        Assert.All(filtered.GetProperty("items").EnumerateArray(), item => Assert.Equal("Project Plan", item.GetProperty("documentType").GetString()));
        var dashboard = await client.GetFromJsonAsync<JsonElement>($"/api/managed-documents/dashboard?projectId={scope.ProjectId}&documentType=Project%20Plan&owner=software.lead");
        Assert.Equal(filtered.GetProperty("totalCount").GetInt32(), dashboard.GetProperty("total").GetInt32());
        commands.Clear(); var completeDashboard = await client.GetFromJsonAsync<JsonElement>($"/api/managed-documents/dashboard?projectId={scope.ProjectId}");
        Assert.Equal(124, completeDashboard.GetProperty("total").GetInt32()); Assert.Equal(124, completeDashboard.GetProperty("inWork").GetInt32()); Assert.Equal(0, completeDashboard.GetProperty("released").GetInt32());
        Assert.InRange(commands.Commands.Count, 1, 8);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync($"/api/managed-documents?projectId={scope.ProjectId}&pageSize=101")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync($"/api/managed-documents?projectId={scope.ProjectId}&cursor=not-a-cursor")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync($"/api/managed-documents?projectId={scope.ProjectId}&search=changed&cursor={Uri.EscapeDataString(first.GetProperty("nextCursor").GetString()!)}")).StatusCode);

        commands.Clear(); var auditFirst = await client.GetFromJsonAsync<JsonElement>($"/api/managed-documents/{historyDocumentId}/history/audit?pageSize=10");
        Assert.Equal(10, auditFirst.GetProperty("items").GetArrayLength()); Assert.True(auditFirst.GetProperty("hasMore").GetBoolean());
        Assert.InRange(commands.Commands.Count, 1, 8); Assert.InRange(JsonSerializer.SerializeToUtf8Bytes(auditFirst).Length, 1, 128 * 1024);
        var auditSecond = await client.GetFromJsonAsync<JsonElement>($"/api/managed-documents/{historyDocumentId}/history/audit?pageSize=10&cursor={Uri.EscapeDataString(auditFirst.GetProperty("nextCursor").GetString()!)}");
        Assert.Equal(10, auditSecond.GetProperty("items").GetArrayLength());
        Assert.Empty(auditFirst.GetProperty("items").EnumerateArray().Select(x => x.GetProperty("id").GetGuid()).Intersect(auditSecond.GetProperty("items").EnumerateArray().Select(x => x.GetProperty("id").GetGuid())));

        foreach (var surface in new[] { "revisions", "check-ins", "reviews", "signatures", "relationships", "contributors", "assignments" })
        {
            using var response = await client.GetAsync($"/api/managed-documents/{historyDocumentId}/history/{surface}?pageSize=10");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var page = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.InRange(page.GetProperty("items").GetArrayLength(), 0, 10);
        }
    }

    private static async Task<HttpResponseMessage> SubmitAsync(HttpClient client, Guid documentId, Guid revisionId, string technicalReviewerId, string finalApproverId)
        => await SubmitRouteAsync(client, documentId, revisionId,
            [(technicalReviewerId, "Technical review", "Review"), (finalApproverId, "SQA / configuration release authorization", "Approval")]);

    private static async Task<HttpResponseMessage> SubmitRouteAsync(HttpClient client, Guid documentId, Guid revisionId,
        IReadOnlyList<(string UserId, string StageName, object Kind)> reviewers)
    {
        var detail = await client.GetFromJsonAsync<JsonElement>($"/api/managed-documents/{documentId}");
        var revision = detail.GetProperty("revisions").EnumerateArray().Single(x => x.GetProperty("id").GetGuid() == revisionId);
        var workingId = revision.GetProperty("currentWorkingAttachmentId").GetGuid();
        var working = revision.GetProperty("attachments").EnumerateArray().Single(x => x.GetProperty("id").GetGuid() == workingId);
        return await client.PostAsJsonAsync($"/api/managed-documents/revisions/{revisionId}/submit", new
        {
            reviewers = reviewers.Select(x => new { userId = x.UserId, stageName = x.StageName, kind = x.Kind }).ToArray(),
            expectedVersion = revision.GetProperty("version").GetInt64(),
            expectedWorkingAttachmentId = workingId,
            expectedWorkingSha256 = working.GetProperty("sha256").GetString(),
            expectedFormalSummaryVersion = revision.GetProperty("formalSummaryVersion").GetInt64(),
            expectedFormalSummaryHash = revision.GetProperty("formalSummaryHash").GetString(),
            expectedRelationshipManifestHash = revision.GetProperty("currentRelationshipManifestHash").GetString(),
            operationKey = Guid.NewGuid().ToString("N")
        });
    }

    private static async Task<HttpResponseMessage> SendCheckInAsync(HttpClient client, Guid grantId, string token, long expectedVersion, string comment, byte[] content)
    {
        var form = new MultipartFormDataContent(); form.Add(new StringContent(comment), "comment"); form.Add(new StringContent(expectedVersion.ToString()), "expectedVersion");
        var file = new ByteArrayContent(content); file.Headers.ContentType = new(ManagedDocumentFileService.DocxContentType); form.Add(file, "file", "SDP-000001.00.docx");
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/document-connector/{grantId}/check-in") { Content = form }; request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await client.SendAsync(request);
    }

    private static async Task<HttpResponseMessage> DecideAsync(HttpClient client, Guid documentId, Guid revisionId, string action, string meaning, string rationale)
    {
        var detail = await client.GetFromJsonAsync<JsonElement>($"/api/managed-documents/{documentId}");
        var revision = detail.GetProperty("revisions").EnumerateArray().Single(x => x.GetProperty("id").GetGuid() == revisionId);
        var step = revision.GetProperty("reviewSteps").EnumerateArray().Single(x => x.GetProperty("state").GetString() == "Active");
        Guid? NullableGuid(string property) => revision.GetProperty(property).ValueKind == JsonValueKind.Null ? null : revision.GetProperty(property).GetGuid();
        return await client.PostAsJsonAsync($"/api/managed-documents/revisions/{revisionId}/review/{action}", new
        {
            password = AeroLinkApiFactory.MemberPassword,
            meaning,
            rationale,
            expectedVersion = revision.GetProperty("version").GetInt64(),
            expectedCycle = revision.GetProperty("currentReviewCycle").GetInt32(),
            expectedStepId = step.GetProperty("id").GetGuid(),
            expectedStepVersion = step.GetProperty("version").GetInt64(),
            expectedSnapshotHash = revision.GetProperty("snapshotHash").GetString(),
            expectedCandidateDocxAttachmentId = NullableGuid("releaseCandidateDocxAttachmentId"),
            expectedCandidatePdfAttachmentId = NullableGuid("releaseCandidatePdfAttachmentId"),
            expectedCandidateManifestHash = revision.GetProperty("releaseManifestHash").GetString(),
            operationKey = Guid.NewGuid().ToString("N")
        });
    }

    private static Dictionary<string, object?> DecisionPayload(JsonElement revision, JsonElement step, string operationKey, string meaning, string rationale)
    {
        Guid? NullableGuid(string property) => revision.GetProperty(property).ValueKind == JsonValueKind.Null ? null : revision.GetProperty(property).GetGuid();
        return new()
        {
            ["password"] = AeroLinkApiFactory.MemberPassword, ["meaning"] = meaning, ["rationale"] = rationale,
            ["expectedVersion"] = revision.GetProperty("version").GetInt64(), ["expectedCycle"] = revision.GetProperty("currentReviewCycle").GetInt32(),
            ["expectedStepId"] = step.GetProperty("id").GetGuid(), ["expectedStepVersion"] = step.GetProperty("version").GetInt64(),
            ["expectedSnapshotHash"] = revision.GetProperty("snapshotHash").GetString(), ["expectedCandidateDocxAttachmentId"] = NullableGuid("releaseCandidateDocxAttachmentId"),
            ["expectedCandidatePdfAttachmentId"] = NullableGuid("releaseCandidatePdfAttachmentId"), ["expectedCandidateManifestHash"] = revision.GetProperty("releaseManifestHash").GetString(),
            ["operationKey"] = operationKey
        };
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
        var cycle = revision.SubmitForReview("admin", draftAttachment.Sha256, [new("software.lead", "Rina Shah", "Technical"), new("quality.analyst", "Maya Patel", "Final", Kind: ReviewStageKind.Approval)], now); revision.Approve("software.lead", "Complete.", now);
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
        var cycle = revision.SubmitForReview("admin", working.Sha256, [new("software.lead", "Rina Shah", "Technical"), new("quality.analyst", "Maya Patel", "Final", Kind: ReviewStageKind.Approval)], now); revision.Approve("software.lead", "Complete.", now);
        var publication = new ProfessionalPublication("AeroLink", "Program", "Project", "Project Plan", document.Title, "Controlled Project document", document.DocumentNumber, revision.Revision.ToString("D2"), "Released", "Project-wide", "All software builds", "admin", now, new string('b', 64), [], [], [], [new("Purpose", "Scope", [new("1", "Plan", "Purpose", "Controlled content.", [])])]);
        var docx = ProfessionalPublicationRenderer.Render(publication, "docx", $"{document.DocumentNumber}.{revision.Revision:D2}"); var pdf = ProfessionalPublicationRenderer.Render(publication, "pdf", $"{document.DocumentNumber}.{revision.Revision:D2}");
        var releasedDocx = await files.StoreAsync(document.ProjectId, document.Id, revision.Id, Guid.NewGuid(), 1, "Released DOCX", "Immutable source.", docx.FileName, docx.ContentType, docx.Content, null, "quality.analyst", now, default); var releasedPdf = await files.StoreAsync(document.ProjectId, document.Id, revision.Id, Guid.NewGuid(), 1, "Released PDF", "Immutable rendition.", pdf.FileName, pdf.ContentType, pdf.Content, null, "quality.analyst", now, default);
        revision.RecordReleaseCandidate(releasedDocx.Id, releasedPdf.Id, ManagedDocumentFileService.Sha256(System.Text.Encoding.UTF8.GetBytes($"{releasedDocx.Sha256}:{releasedPdf.Sha256}:{revision.FormalSummaryHash}:{revision.FormalSummaryVersion}")), "quality.analyst", now); revision.Approve("quality.analyst", "Release.", now);
        var oldHead = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.SingleAsync(db.ManagedDocumentRevisions.Where(x => x.DocumentId == documentId && x.Id != revisionId && x.State == ManagedDocumentState.Released)); oldHead.Supersede(now);
        db.AddRange(releasedDocx, releasedPdf); db.ManagedDocumentReviewSteps.AddRange(revision.ReviewSteps.Where(x => x.Cycle == cycle)); await db.SaveChangesAsync(); return (revision.Id, releasedDocx.Id, releasedDocx.Sha256);
    }

    [Fact]
    public async Task Open_managed_document_resolver_lands_on_the_project_wide_record_without_guessing_a_build()
    {
        using var factory = new AeroLinkApiFactory(); using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false }); await ProblemReportApiTests.BootstrapAndLoginAsync(client);
        var scope = await SeedProjectAsync(factory);
        using var created = await client.PostAsJsonAsync("/api/managed-documents", new { projectId = scope.ProjectId, acronym = "SVP", documentType = "Software Verification Plan", title = "Project verification plan", ownerId = "software.author", formalChangeSummary = "Control Project-wide verification planning.", operationKey = Guid.NewGuid().ToString("N") });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var body = await created.Content.ReadFromJsonAsync<JsonElement>();
        var documentId = body.GetProperty("id").GetGuid();
        var revisionId = body.GetProperty("revisionId").GetGuid();
        var canonical = $"/programs/{scope.ProgramId}/projects/{scope.ProjectId}/documentation-center/{documentId}";

        using var byDocument = await client.GetAsync($"/open/managed-document/{documentId}");
        Assert.Equal(HttpStatusCode.Redirect, byDocument.StatusCode);
        Assert.Equal(canonical, byDocument.Headers.Location!.ToString());
        Assert.DoesNotContain("/releases/", byDocument.Headers.Location!.ToString());

        using var byRevision = await client.GetAsync($"/open/managed-document/{revisionId}");
        Assert.Equal(HttpStatusCode.Redirect, byRevision.StatusCode);
        Assert.Equal(canonical, byRevision.Headers.Location!.ToString());

        using var anonymous = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        using var anonymousAttempt = await anonymous.GetAsync($"/open/managed-document/{documentId}");
        Assert.Equal(HttpStatusCode.Redirect, anonymousAttempt.StatusCode);
        Assert.Equal("/", anonymousAttempt.Headers.Location!.ToString());

        using var foreignFactory = new AeroLinkApiFactory(); using var foreignClient = foreignFactory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false }); await ProblemReportApiTests.BootstrapAndLoginAsync(foreignClient);
        using var foreignAttempt = await foreignClient.GetAsync($"/open/managed-document/{documentId}");
        Assert.Equal(HttpStatusCode.Redirect, foreignAttempt.StatusCode);
        Assert.Equal("/", foreignAttempt.Headers.Location!.ToString());
    }

    [Fact]
    public async Task Search_finds_managed_documents_by_acronym_type_owner_and_state_and_denies_other_projects()
    {
        using var factory = new AeroLinkApiFactory(); using var client = factory.CreateClient(); await ProblemReportApiTests.BootstrapAndLoginAsync(client);
        var scope = await SeedProjectAsync(factory);
        using var created = await client.PostAsJsonAsync("/api/managed-documents", new { projectId = scope.ProjectId, acronym = "SVP", documentType = "Software Verification Plan", title = "Project verification plan", ownerId = "software.author", formalChangeSummary = "Control Project-wide verification planning.", operationKey = Guid.NewGuid().ToString("N") });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        foreach (var query in new[] { "SVP", "Software Verification Plan", "software.author", "draft", "SVP-000001.00" })
        {
            var results = await client.GetFromJsonAsync<JsonElement>($"/api/search?projectId={scope.ProjectId}&query={Uri.EscapeDataString(query)}");
            Assert.Contains(results.GetProperty("items").EnumerateArray(), item => item.GetProperty("kind").GetString() == "managed-document" && item.GetProperty("identifier").GetString()!.StartsWith("SVP-000001."));
        }
        var exact = await client.GetFromJsonAsync<JsonElement>($"/api/search?projectId={scope.ProjectId}&query=SVP-000001.00");
        var exactItems = exact.GetProperty("items").EnumerateArray().Where(item => item.GetProperty("kind").GetString() == "managed-document").ToList();
        Assert.Single(exactItems);
        Assert.Equal("SVP-000001.00", exactItems[0].GetProperty("identifier").GetString());

        using var foreignFactory = new AeroLinkApiFactory();
        _ = await SeedProjectAsync(foreignFactory);
        using var foreignMember = foreignFactory.CreateClient();
        using (var login = await foreignMember.PostAsJsonAsync("/api/auth/login", new { userName = "software.author", password = AeroLinkApiFactory.MemberPassword })) Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        using var denied = await foreignMember.GetAsync($"/api/search?projectId={scope.ProjectId}&query=SVP");
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
    }

    [Fact]
    public async Task My_work_surfaces_the_active_desktop_checkout_for_its_holder_only()
    {
        using var factory = new AeroLinkApiFactory(); using var administrator = factory.CreateClient(); await ProblemReportApiTests.BootstrapAndLoginAsync(administrator);
        var scope = await SeedProjectAsync(factory);
        using var created = await administrator.PostAsJsonAsync("/api/managed-documents", new { projectId = scope.ProjectId, acronym = "SVP", documentType = "Software Verification Plan", title = "Project verification plan", ownerId = "software.author", formalChangeSummary = "Control Project-wide verification planning.", operationKey = Guid.NewGuid().ToString("N") });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var body = await created.Content.ReadFromJsonAsync<JsonElement>();
        var documentId = body.GetProperty("id").GetGuid();
        var revisionId = body.GetProperty("revisionId").GetGuid();

        using var author = factory.CreateClient();
        using (var login = await author.PostAsJsonAsync("/api/auth/login", new { userName = "software.author", password = AeroLinkApiFactory.MemberPassword })) Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        await SecurityBoundaryTests.AuthorizeMutationsAsync(author);
        using var checkout = await author.PostAsync($"/api/managed-documents/revisions/{revisionId}/checkout", null);
        Assert.Equal(HttpStatusCode.OK, checkout.StatusCode);

        var authorWork = await author.GetFromJsonAsync<JsonElement>($"/api/my-work?projectId={scope.ProjectId}");
        Assert.Contains(authorWork.GetProperty("tasks").EnumerateArray(), item => item.GetProperty("id").GetGuid() == documentId && item.GetProperty("type").GetString() == "Project document checkout" && item.GetProperty("route").GetString() == "managedDocuments");

        using var lead = factory.CreateClient();
        using (var login = await lead.PostAsJsonAsync("/api/auth/login", new { userName = "software.lead", password = AeroLinkApiFactory.MemberPassword })) Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var leadWork = await lead.GetFromJsonAsync<JsonElement>($"/api/my-work?projectId={scope.ProjectId}");
        Assert.DoesNotContain(leadWork.GetProperty("tasks").EnumerateArray(), item => item.GetProperty("type").GetString() == "Project document checkout" && item.GetProperty("id").GetGuid() == documentId);
    }

    private static async Task<(Guid ProgramId, Guid ProjectId, Guid ReleasedId, Guid ActiveReleaseId)> SeedProjectAsync(AeroLinkApiFactory factory)
    {
        using var scope = factory.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>(); var program = new ProgramRecord("Document Program", $"DC{Guid.NewGuid():N}"[..12]); var project = new ProjectRecord(program.Id, "Navigation Product", "Navigation Software"); var released = new SoftwareRelease(project.Id, "1.5", true); var active = new SoftwareRelease(project.Id, "1.6", false, released.Id); var now = DateTimeOffset.UtcNow;
        var technical = new UserAccount("software.lead", "Rina Shah", "software.lead@example.test", IdentityService.HashPassword(AeroLinkApiFactory.MemberPassword), now); var quality = new UserAccount("quality.analyst", "Maya Patel", "quality.analyst@example.test", IdentityService.HashPassword(AeroLinkApiFactory.MemberPassword), now); var author = new UserAccount("software.author", "Ethan Brooks", "software.author@example.test", IdentityService.HashPassword(AeroLinkApiFactory.MemberPassword), now); var reviewer = new UserAccount("system.reviewer", "Olivia Chen", "system.reviewer@example.test", IdentityService.HashPassword(AeroLinkApiFactory.MemberPassword), now);
        // Technical document review is the Software Engineering Lead position's authority since #816, so the
        // reviewer holds the base role that makes them eligible and is elevated into the post. The retired
        // role name on its own no longer signs anything.
        db.AddRange(program, project, released, active, technical, quality, author, reviewer, new ProgramMembership(technical.Id, program.Id, ProgramRole.SoftwareEngineer, "admin", now), new ProjectLeadershipAssignment(program.Id, ProjectLeadershipPosition.SoftwareEngineeringLead, technical.Id, "admin", now), new ProgramMembership(quality.Id, program.Id, ProgramRole.SoftwareQualityAnalyst, "admin", now), new ProgramMembership(author.Id, program.Id, ProgramRole.SoftwareEngineer, "admin", now), new ProgramMembership(author.Id, program.Id, ProgramRole.Reviewer, "admin", now), new ProgramMembership(reviewer.Id, program.Id, ProgramRole.Reviewer, "admin", now)); await db.SaveChangesAsync(); return (program.Id, project.Id, released.Id, active.Id);
    }
    private static async Task<HttpClient> LoginAsync(AeroLinkApiFactory factory, string userName)
    {
        var client = factory.CreateClient();
        using var login = await client.PostAsJsonAsync("/api/auth/login", new { userName, password = AeroLinkApiFactory.MemberPassword });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        await SecurityBoundaryTests.AuthorizeMutationsAsync(client);
        return client;
    }
    private static Dictionary<string,string> Query(Uri uri) => uri.Query.TrimStart('?').Split('&').Select(part => part.Split('=', 2)).ToDictionary(pair => pair[0], pair => Uri.UnescapeDataString(pair[1]));
    private static ConnectorLaunchEnvelope LaunchEnvelope(Uri uri)
    {
        var compact = Query(uri)["envelope"]; var encoded = compact.Split('.')[0].Replace('-', '+').Replace('_', '/');
        encoded += (encoded.Length % 4) switch { 2 => "==", 3 => "=", 0 => "", _ => throw new InvalidOperationException("Invalid connector envelope encoding.") };
        return JsonSerializer.Deserialize<ConnectorLaunchEnvelope>(Convert.FromBase64String(encoded), new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
    }

    private static byte[] AddExternalImageRelationship(byte[] source)
    {
        using var output = new MemoryStream(); output.Write(source); output.Position = 0;
        using (var archive = new System.IO.Compression.ZipArchive(output, System.IO.Compression.ZipArchiveMode.Update, true))
        {
            var entry = archive.GetEntry("word/_rels/document.xml.rels")!;
            System.Xml.Linq.XDocument document;
            using (var reader = new StreamReader(entry.Open())) document = System.Xml.Linq.XDocument.Parse(reader.ReadToEnd());
            entry.Delete();
            var ns = (System.Xml.Linq.XNamespace)"http://schemas.openxmlformats.org/package/2006/relationships";
            document.Root!.Add(new System.Xml.Linq.XElement(ns + "Relationship",
                new System.Xml.Linq.XAttribute("Id", "rIdUnsafeExternalImage"),
                new System.Xml.Linq.XAttribute("Type", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/image"),
                new System.Xml.Linq.XAttribute("Target", "https://example.test/tracker.png"),
                new System.Xml.Linq.XAttribute("TargetMode", "External")));
            var replacement = archive.CreateEntry("word/_rels/document.xml.rels");
            using var writer = new StreamWriter(replacement.Open()); writer.Write(document.ToString(System.Xml.Linq.SaveOptions.DisableFormatting));
        }
        return output.ToArray();
    }
}

internal sealed class OneShotStorageFaultInjector(string operationType, string phase) : IManagedDocumentStorageFaultInjector
{
    private int _triggered;
    public Task CheckpointAsync(ManagedDocumentStorageOperation operation, string checkpoint, CancellationToken ct)
    {
        if (operation.OperationType == operationType && checkpoint == phase && Interlocked.Exchange(ref _triggered, 1) == 0)
            throw new IOException($"Injected managed-document storage failure at {operationType}/{phase}.");
        return Task.CompletedTask;
    }
}
