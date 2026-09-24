using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using AeroLink.Domain.Baselines;
using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Hierarchy;
using AeroLink.Domain.Identity;
using AeroLink.Domain.Imports;
using AeroLink.Domain.Programs;
using AeroLink.Domain.Requirements;
using AeroLink.Domain.Verification;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AeroLink.Api.Tests;

/// <summary>
/// API qualification for the external inception composition. The same server gates are exercised for each
/// supported exchange format, including persisted parser observations, explicit mapping, reconciliation, and
/// password-confirmed source acceptance.
/// </summary>
public sealed class ProjectSetupInceptionApiTests
{
    [Fact]
    public async Task Source_assertion_binds_official_build_and_start_kind_after_build_edit()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        await SecurityBoundaryTests.BootstrapAndLoginAdministratorAsync(client);

        using var created = await client.PostAsJsonAsync("/api/project-setups", new { projectName = "Assertion build binding" });
        using var createdBody = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        var draftId = createdBody.RootElement.GetProperty("draftId").GetGuid();
        using var details = await client.PutAsJsonAsync($"/api/project-setups/{draftId}", new
        {
            expectedVersion = 1, currentStep = "StartingPoint",
            project = new { name = "Assertion build binding", softwareProduct = "Binding product" },
            build = new { version = "1.3" }, selectedCategories = Array.Empty<string>(), ladder = new { },
            reviewRules = new { }, reviewRulesAccepted = true, repository = new { mode = "ConfigureLater" }, mapping = new { },
        });
        Assert.Equal(HttpStatusCode.OK, details.StatusCode);
        using var upload = new HttpRequestMessage(HttpMethod.Post,
            $"/api/project-setups/{draftId}/source/upload?expectedVersion=2&fileName=source.csv")
        { Content = new ByteArrayContent(CreateSource("source.csv")) };
        upload.Content.Headers.ContentType = new("application/octet-stream");
        using var uploaded = await client.SendAsync(upload);
        Assert.Equal(HttpStatusCode.OK, uploaded.StatusCode);
        using var uploadedBody = JsonDocument.Parse(await uploaded.Content.ReadAsStringAsync());
        var sourceVersion = uploadedBody.RootElement.GetProperty("draftVersion").GetInt64();
        using var observed = JsonDocument.Parse(await (await client.GetAsync($"/api/project-setups/{draftId}/source"))
            .Content.ReadAsStringAsync());
        using var configured = await client.PutAsJsonAsync($"/api/project-setups/{draftId}/source/configuration", new
        {
            expectedVersion = sourceVersion, selectedCategories = new[] { "Requirements" },
            mapping = BuildMapping(observed.RootElement, "source.csv"), metadata = new { },
        });
        Assert.Equal(HttpStatusCode.OK, configured.StatusCode);
        using var ready = JsonDocument.Parse(await (await client.GetAsync($"/api/project-setups/{draftId}/source"))
            .Content.ReadAsStringAsync());
        var oldAssertion = ready.RootElement.GetProperty("assertion");
        var oldHash = oldAssertion.GetProperty("hash").GetString();
        Assert.Contains("official build SW-01.30", oldAssertion.GetProperty("text").GetString(), StringComparison.Ordinal);
        Assert.Contains("start kind ExternalBaseline", oldAssertion.GetProperty("text").GetString(), StringComparison.Ordinal);

        using var changed = await client.PutAsJsonAsync($"/api/project-setups/{draftId}", new
        {
            expectedVersion = 4, currentStep = "Review", build = new { version = "2.0" },
        });
        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
        using var changedBody = JsonDocument.Parse(await changed.Content.ReadAsStringAsync());
        var changedVersion = changedBody.RootElement.GetProperty("version").GetInt64();
        Assert.Equal(5, changedVersion);

        using var changedSource = JsonDocument.Parse(await (await client.GetAsync($"/api/project-setups/{draftId}/source"))
            .Content.ReadAsStringAsync());
        var newAssertion = changedSource.RootElement.GetProperty("assertion");
        Assert.NotEqual(oldHash, newAssertion.GetProperty("hash").GetString());
        Assert.Contains("official build SW-02.00", newAssertion.GetProperty("text").GetString(), StringComparison.Ordinal);
        using var staleFinalize = await client.PostAsJsonAsync($"/api/project-setups/{draftId}/finalize", new
        {
            expectedVersion = changedVersion, idempotencyKey = "build-binding-stale",
            password = AeroLinkApiFactory.AdministratorPassword, sourceAssertionHash = oldHash,
            sourceAssertionAccepted = true,
        });
        Assert.Equal(HttpStatusCode.BadRequest, staleFinalize.StatusCode);

        using var changedLadder = await client.PutAsJsonAsync($"/api/project-setups/{draftId}", new
        {
            expectedVersion = changedVersion, currentStep = "Review",
            ladder = new
            {
                steps = new[] { new { catalogueEntry = "Customer", position = 1, capabilities = 0,
                    enabledArtifactKinds = Array.Empty<string>() } },
                relationships = Array.Empty<object>(),
            },
        });
        Assert.Equal(HttpStatusCode.OK, changedLadder.StatusCode);
        using var invalidatedSource = await client.GetAsync($"/api/project-setups/{draftId}/source");
        Assert.Equal("Analysed", (await invalidatedSource.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("stage").GetString());
        using var invalidatedDraft = await client.GetAsync($"/api/project-setups/{draftId}");
        Assert.False((await invalidatedDraft.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("reviewRules").GetProperty("accepted").GetBoolean());
    }

    [Fact]
    public async Task Native_source_rechecks_current_membership_after_capture_and_allows_admin_resume()
    {
        using var factory = new AeroLinkApiFactory();
        using var administrator = factory.CreateClient();
        await SecurityBoundaryTests.BootstrapAndLoginAdministratorAsync(administrator);
        var now = DateTimeOffset.UtcNow.AddMinutes(-2);
        Guid sourceBaselineId;
        Guid sourceProgramId;
        Guid memberId;
        Guid draftId;
        using (var seed = factory.Services.CreateScope())
        {
            var db = seed.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var member = new UserAccount("native.revoked.member", "Native Revoked Member", "native.revoked@example.test",
                IdentityService.HashPassword(AeroLinkApiFactory.MemberPassword), now);
            var program = new ProgramRecord("Revocation source program", "RSP");
            var project = new ProjectRecord(program.Id, "Revocation source project", "Revocation product");
            var release = new SoftwareRelease(project.Id, "1.0", false);
            var baseline = new CandidateBaseline("SW-91.01", 0, project.Id, release.Id, null,
                "Revocation source baseline", "source.manager", now);
            var sourceChange = new SystemChangeRequest("SRCR-910011", 0, project.Id, release.Id,
                "Source requirement", "Problem", "Analysis", "Solution", "source.author", now);
            var requirement = new RequirementArtifact(project.Id, "SYSR-910011", RequirementLevel.System, now);
            var revision = new RequirementRevision(requirement.Id, 0, "The source requirement shall remain attributable.",
                "Source rationale", "", RequirementRevisionState.Active, sourceChange.Id, baseline.Id, now);
            baseline.FreezeForInception("source.manager", now);
            baseline.MarkRequirementsMaterialized("source.manager", new string('a', 64), 1, now);
            var draft = new ProjectSetupDraft(member.Id, member.UserName, "Native revocation destination");
            db.AddRange(member, program, project, release, baseline, sourceChange, requirement, revision,
                new BaselineRequirementSelection(baseline.Id, requirement.Id, revision.Id),
                new ProgramMembership(member.Id, program.Id, ProgramRole.Engineer, "admin", now), draft);
            await db.SaveChangesAsync();
            sourceBaselineId = baseline.Id;
            sourceProgramId = program.Id;
            memberId = member.Id;
            draftId = draft.Id;
        }

        using var memberClient = factory.CreateClient();
        using var login = await memberClient.PostAsJsonAsync("/api/auth/login", new
        { userName = "native.revoked.member", password = AeroLinkApiFactory.MemberPassword });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        await SecurityBoundaryTests.AuthorizeMutationsAsync(memberClient);
        using var details = await memberClient.PutAsJsonAsync($"/api/project-setups/{draftId}", new
        {
            expectedVersion = 1, currentStep = "StartingPoint",
            project = new { name = "Native revocation destination", softwareProduct = "Native revocation product" },
            build = new { version = "1.3" }, selectedCategories = Array.Empty<string>(), ladder = new { },
            reviewRules = new { }, reviewRulesAccepted = true, repository = new { mode = "ConfigureLater" }, mapping = new { },
        });
        Assert.Equal(HttpStatusCode.OK, details.StatusCode);
        using var capture = await memberClient.PostAsJsonAsync($"/api/project-setups/{draftId}/source/native", new
        { expectedVersion = 2, baselineId = sourceBaselineId });
        Assert.Equal(HttpStatusCode.OK, capture.StatusCode);
        using var captureBody = JsonDocument.Parse(await capture.Content.ReadAsStringAsync());
        var capturedVersion = captureBody.RootElement.GetProperty("draftVersion").GetInt64();

        // Switching away and returning must retain the exact captured package, even if the source baseline
        // has since advanced from Frozen to Released. It must not create a second package for one selection.
        using var initialSourceResponse = await memberClient.GetAsync($"/api/project-setups/{draftId}/source");
        var initialSource = await initialSourceResponse.Content.ReadFromJsonAsync<JsonElement>();
        using var initialConfiguration = await memberClient.PutAsJsonAsync($"/api/project-setups/{draftId}/source/configuration", new
        {
            expectedVersion = capturedVersion, selectedCategories = new[] { "Requirements" },
            mapping = BuildNativeMapping(initialSource), metadata = new { },
        });
        Assert.Equal(HttpStatusCode.OK, initialConfiguration.StatusCode);
        var configuredBeforeBacktracking = (await initialConfiguration.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("draftVersion").GetInt64();
        using var freshSelection = await memberClient.PutAsJsonAsync($"/api/project-setups/{draftId}", new
        {
            expectedVersion = configuredBeforeBacktracking, currentStep = "StartingPoint", start = new { kind = "Fresh" },
            selectedCategories = Array.Empty<string>(), mapping = new { },
        });
        Assert.Equal(HttpStatusCode.OK, freshSelection.StatusCode);
        var freshVersion = (await freshSelection.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("version").GetInt64();
        using (var sourceAdvance = factory.Services.CreateScope())
        {
            var sourceDb = sourceAdvance.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            (await sourceDb.CandidateBaselines.SingleAsync(x => x.Id == sourceBaselineId))
                .MarkReleased("source.manager", DateTimeOffset.UtcNow);
            await sourceDb.SaveChangesAsync();
        }
        using var reselected = await memberClient.PostAsJsonAsync($"/api/project-setups/{draftId}/source/native", new
        { expectedVersion = freshVersion, baselineId = sourceBaselineId });
        Assert.Equal(HttpStatusCode.OK, reselected.StatusCode);
        var reselectedBody = await reselected.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(captureBody.RootElement.GetProperty("id").GetGuid(), reselectedBody.GetProperty("id").GetGuid());
        Assert.Equal(captureBody.RootElement.GetProperty("sha256").GetString(), reselectedBody.GetProperty("sha256").GetString());
        capturedVersion = reselectedBody.GetProperty("draftVersion").GetInt64();
        using var restoredSourceResponse = await memberClient.GetAsync($"/api/project-setups/{draftId}/source");
        Assert.Equal(HttpStatusCode.OK, restoredSourceResponse.StatusCode);
        var restoredSource = await restoredSourceResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Reconciled", restoredSource.GetProperty("stage").GetString());
        Assert.Equal("Requirements", Assert.Single(restoredSource.GetProperty("selectedCategories").EnumerateArray()).GetString());
        capturedVersion = await AssertReselectionRevalidatesLadderAsync(memberClient, draftId, capturedVersion,
            async version =>
            {
                using var response = await memberClient.PostAsJsonAsync($"/api/project-setups/{draftId}/source/native",
                    new { expectedVersion = version, baselineId = sourceBaselineId });
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                return await response.Content.ReadFromJsonAsync<JsonElement>();
            });

        // This token was issued while the member still held the source-program role. The persisted role is
        // ended after capture, so every source boundary must reject the stale in-memory Programs snapshot.
        using (var revoke = factory.Services.CreateScope())
        {
            var db = revoke.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var membership = await db.ProgramMemberships.SingleAsync(x => x.UserId == memberId
                && x.ProgramId == sourceProgramId && x.EndedAt == null);
            membership.End("admin", DateTimeOffset.UtcNow);
            await db.SaveChangesAsync();
        }

        using var revokedRead = await memberClient.GetAsync($"/api/project-setups/{draftId}/source");
        Assert.Equal(HttpStatusCode.Forbidden, revokedRead.StatusCode);
        using var revokedConfig = await memberClient.PutAsJsonAsync($"/api/project-setups/{draftId}/source/configuration", new
        {
            expectedVersion = capturedVersion, selectedCategories = new[] { "Requirements" },
            mapping = new { }, metadata = new { },
        });
        Assert.Equal(HttpStatusCode.Forbidden, revokedConfig.StatusCode);
        using var revokedReconcile = await memberClient.PostAsJsonAsync($"/api/project-setups/{draftId}/source/reconcile", new
        { expectedVersion = capturedVersion });
        Assert.Equal(HttpStatusCode.Forbidden, revokedReconcile.StatusCode);
        using var revokedFinalize = await memberClient.PostAsJsonAsync($"/api/project-setups/{draftId}/finalize", new
        {
            expectedVersion = capturedVersion, idempotencyKey = "revoked-source-member",
            password = AeroLinkApiFactory.MemberPassword,
        });
        Assert.Equal(HttpStatusCode.Forbidden, revokedFinalize.StatusCode);

        using var adminRead = await administrator.GetAsync($"/api/project-setups/{draftId}/source");
        Assert.Equal(HttpStatusCode.OK, adminRead.StatusCode);
        using var source = JsonDocument.Parse(await adminRead.Content.ReadAsStringAsync());
        using var configured = await administrator.PutAsJsonAsync($"/api/project-setups/{draftId}/source/configuration", new
        {
            expectedVersion = capturedVersion, selectedCategories = new[] { "Requirements" },
            mapping = BuildNativeMapping(source.RootElement), metadata = new { },
        });
        Assert.Equal(HttpStatusCode.OK, configured.StatusCode);
        using var configuredBody = JsonDocument.Parse(await configured.Content.ReadAsStringAsync());
        var configuredVersion = configuredBody.RootElement.GetProperty("draftVersion").GetInt64();
        using var ready = JsonDocument.Parse(await (await administrator.GetAsync($"/api/project-setups/{draftId}/source"))
            .Content.ReadAsStringAsync());
        var assertionHash = ready.RootElement.GetProperty("assertion").GetProperty("hash").GetString();
        using var finalized = await administrator.PostAsJsonAsync($"/api/project-setups/{draftId}/finalize", new
        {
            expectedVersion = configuredVersion, idempotencyKey = "revoked-source-admin-resume",
            password = AeroLinkApiFactory.AdministratorPassword, sourceAssertionHash = assertionHash,
            sourceAssertionAccepted = true,
        });
        Assert.Equal(HttpStatusCode.OK, finalized.StatusCode);
        var outcome = await finalized.Content.ReadFromJsonAsync<JsonElement>();
        var destinationId = outcome.GetProperty("projectId").GetGuid();
        await AssertInheritedDocumentsAsync(administrator, outcome.GetProperty("releaseId").GetGuid(),
            "Native revocation destination", "The source requirement shall remain attributable.");
        await AssertIndependentDocumentAsync(administrator);
        using var provenanceResponse = await administrator.GetAsync($"/api/projects/{destinationId}/inception-source");
        Assert.Equal(HttpStatusCode.OK, provenanceResponse.StatusCode);
        var provenance = await provenanceResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("admin", provenance.GetProperty("acceptance").GetProperty("userName").GetString());
        using var completedScope = factory.Services.CreateScope();
        var completedDb = completedScope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        var destinationProgramId = outcome.GetProperty("programId").GetGuid();
        var management = await completedDb.ProgramMemberships.SingleAsync(x => x.ProgramId == destinationProgramId);
        Assert.Equal(memberId, management.UserId);
        Assert.Equal("admin", management.GrantedBy);
        var completionAudit = await completedDb.SecurityAuditEvents.SingleAsync(x => x.EventType == "ProjectSetupCompleted"
            && x.Target == draftId.ToString("D"));
        Assert.Equal("admin", completionAudit.ActorId);
        Assert.Contains("AeroLinkBaseline", completionAudit.Detail, StringComparison.Ordinal);
        Assert.Contains(sourceBaselineId.ToString("D"), completionAudit.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("no engineering content was inherited", completionAudit.Detail, StringComparison.Ordinal);
    }

    private static async Task<long> AssertReselectionRevalidatesLadderAsync(HttpClient client, Guid draftId,
        long version, Func<long, Task<JsonElement>> reselect)
    {
        using var fresh = await client.PutAsJsonAsync($"/api/project-setups/{draftId}", new
        { expectedVersion = version, currentStep = "StartingPoint", start = new { kind = "Fresh" } });
        Assert.Equal(HttpStatusCode.OK, fresh.StatusCode);
        version = (await fresh.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("version").GetInt64();
        using var changed = await client.PutAsJsonAsync($"/api/project-setups/{draftId}", new
        {
            expectedVersion = version, currentStep = "StartingPoint",
            ladder = new
            {
                steps = new[] { new { catalogueEntry = "Customer", position = 1, capabilities = 0,
                    enabledArtifactKinds = Array.Empty<string>() } },
                relationships = Array.Empty<object>(),
            },
        });
        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
        version = (await changed.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("version").GetInt64();
        var selected = await reselect(version);
        Assert.Equal("Analysed", selected.GetProperty("stage").GetString());
        using var source = await client.GetAsync($"/api/project-setups/{draftId}/source");
        var sourceBody = await source.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Requirements", Assert.Single(sourceBody.GetProperty("selectedCategories").EnumerateArray()).GetString());
        Assert.Equal(JsonValueKind.Null, sourceBody.GetProperty("assertion").ValueKind);
        using var restored = await client.PutAsJsonAsync($"/api/project-setups/{draftId}", new
        {
            expectedVersion = selected.GetProperty("draftVersion").GetInt64(), currentStep = "Review",
            ladder = new { }, reviewRulesAccepted = true,
        });
        Assert.Equal(HttpStatusCode.OK, restored.StatusCode);
        version = (await restored.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("version").GetInt64();
        using var reconciled = await client.PostAsJsonAsync($"/api/project-setups/{draftId}/source/reconcile",
            new { expectedVersion = version });
        Assert.Equal(HttpStatusCode.OK, reconciled.StatusCode);
        var result = await reconciled.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(result.GetProperty("reconciliation").GetProperty("ready").GetBoolean());
        return result.GetProperty("draftVersion").GetInt64();
    }

    private static async Task AssertInheritedDocumentsAsync(HttpClient client, Guid releaseId,
        string projectName, string statement)
    {
        foreach (var format in new[] { "docx", "pdf" })
        {
            using var document = await client.GetAsync($"/api/releases/{releaseId}/draft-document?type=Sysrd&format={format}");
            var bytes = await document.Content.ReadAsByteArrayAsync();
            Assert.True(document.IsSuccessStatusCode, Encoding.UTF8.GetString(bytes));
            Assert.Equal(format == "pdf" ? "application/pdf"
                : "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
                document.Content.Headers.ContentType?.MediaType);
            string text;
            if (format == "docx")
            {
                using var archive = new ZipArchive(new MemoryStream(bytes));
                using var reader = new StreamReader(archive.GetEntry("word/document.xml")!.Open());
                text = await reader.ReadToEndAsync();
            }
            else
            {
                text = Encoding.Latin1.GetString(bytes);
                Assert.StartsWith("%PDF", text);
            }
            Assert.Contains("SW-01.30", text);
            Assert.Contains(projectName, text);
            Assert.Contains(statement, text);
            Assert.Contains("Accepted source manifest", text);
            Assert.Contains("Initial materialized baseline", text, StringComparison.OrdinalIgnoreCase);
            // PDF wraps its cover description into separate text operators; the DOCX keeps the full sentence.
            if (format == "docx") Assert.Contains("Source acceptance is not a new engineering approval", text);
            else
            {
                Assert.Contains("Source acceptance", text);
                Assert.Contains("engineering", text);
                Assert.Contains("approval", text);
            }
            Assert.DoesNotContain("backing scope", text);
        }
    }

    private static async Task AssertIndependentDocumentAsync(HttpClient client)
    {
        using var created = await client.PostAsJsonAsync("/api/project-setups", new { projectName = "Independent empty peer" });
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("draftId").GetGuid();
        using var saved = await client.PutAsJsonAsync($"/api/project-setups/{id}", new
        {
            expectedVersion = 1, currentStep = "Review", start = new { kind = "Fresh" },
            project = new { name = "Independent empty peer", softwareProduct = "Independent peer product" },
            build = new { version = "1.3" }, ladder = new { }, reviewRules = new { }, reviewRulesAccepted = true,
            repository = new { mode = "ConfigureLater" },
        });
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        using var completed = await client.PostAsJsonAsync($"/api/project-setups/{id}/finalize",
            new { expectedVersion = 2, idempotencyKey = "independent-empty-peer" });
        Assert.Equal(HttpStatusCode.OK, completed.StatusCode);
        var releaseId = (await completed.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("releaseId").GetGuid();
        using var document = await client.GetAsync($"/api/releases/{releaseId}/draft-document?type=Sysrd&format=pdf");
        Assert.Equal(HttpStatusCode.OK, document.StatusCode);
        var pdf = Encoding.Latin1.GetString(await document.Content.ReadAsByteArrayAsync());
        Assert.Contains("Independent empty peer", pdf);
        Assert.DoesNotContain("The source requirement shall remain attributable", pdf);
        Assert.DoesNotContain("Accepted source manifest", pdf);
        Assert.DoesNotContain("Initial materialized baseline", pdf, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Source_upload_retries_are_idempotent_and_stale_assertions_cannot_finalize()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        await SecurityBoundaryTests.BootstrapAndLoginAdministratorAsync(client);
        using var created = await client.PostAsJsonAsync("/api/project-setups", new { projectName = "Source retry" });
        using var createdBody = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        var draftId = createdBody.RootElement.GetProperty("draftId").GetGuid();
        using var details = await client.PutAsJsonAsync($"/api/project-setups/{draftId}", new
        {
            expectedVersion = 1, currentStep = "StartingPoint",
            project = new { name = "Source retry", softwareProduct = "Source retry product" },
            build = new { version = "1.3" }, selectedCategories = Array.Empty<string>(), ladder = new { },
            reviewRules = new { }, reviewRulesAccepted = true, repository = new { mode = "ConfigureLater" }, mapping = new { },
        });
        Assert.Equal(HttpStatusCode.OK, details.StatusCode);
        var bytes = CreateSource("source.csv");
        async Task<JsonElement> UploadAsync(long expectedVersion)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post,
                $"/api/project-setups/{draftId}/source/upload?expectedVersion={expectedVersion}&fileName=source.csv")
            { Content = new ByteArrayContent(bytes) };
            request.Content.Headers.ContentType = new("application/octet-stream");
            using var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
        }
        var firstUpload = await UploadAsync(2);
        var retryUpload = await UploadAsync(2);
        Assert.Equal(firstUpload.GetProperty("id").GetGuid(), retryUpload.GetProperty("id").GetGuid());
        Assert.Equal(firstUpload.GetProperty("draftVersion").GetInt64(), retryUpload.GetProperty("draftVersion").GetInt64());

        using var observedResponse = await client.GetAsync($"/api/project-setups/{draftId}/source");
        using var observed = JsonDocument.Parse(await observedResponse.Content.ReadAsStringAsync());
        var firstMapping = BuildMapping(observed.RootElement, "source.csv");
        using var firstConfiguration = await client.PutAsJsonAsync($"/api/project-setups/{draftId}/source/configuration", new
        {
            expectedVersion = 3, selectedCategories = new[] { "Requirements" }, mapping = firstMapping, metadata = new { },
        });
        Assert.Equal(HttpStatusCode.OK, firstConfiguration.StatusCode);
        using var firstReadyResponse = await client.GetAsync($"/api/project-setups/{draftId}/source");
        using var firstReady = JsonDocument.Parse(await firstReadyResponse.Content.ReadAsStringAsync());
        var staleHash = firstReady.RootElement.GetProperty("assertion").GetProperty("hash").GetString();

        using var changedConfiguration = await client.PutAsJsonAsync($"/api/project-setups/{draftId}/source/configuration", new
        {
            expectedVersion = 4, selectedCategories = new[] { "Requirements" },
            mapping = BuildMapping(observed.RootElement, "source.csv", "A different explicit source-fact reason."), metadata = new { },
        });
        Assert.Equal(HttpStatusCode.OK, changedConfiguration.StatusCode);
        using var changedBody = JsonDocument.Parse(await changedConfiguration.Content.ReadAsStringAsync());
        var currentVersion = changedBody.RootElement.GetProperty("draftVersion").GetInt64();
        using var staleFinalize = await client.PostAsJsonAsync($"/api/project-setups/{draftId}/finalize", new
        {
            expectedVersion = currentVersion, idempotencyKey = "stale-source-assertion", password = AeroLinkApiFactory.AdministratorPassword,
            sourceAssertionHash = staleHash, sourceAssertionAccepted = true,
        });
        Assert.Equal(HttpStatusCode.BadRequest, staleFinalize.StatusCode);

        using var forgedMetadata = await client.PutAsJsonAsync($"/api/project-setups/{draftId}/source/configuration", new
        {
            expectedVersion = currentVersion, selectedCategories = new[] { "Requirements" },
            mapping = BuildMapping(observed.RootElement, "source.csv"), metadata = new { sourceTool = "forged" },
        });
        Assert.Equal(HttpStatusCode.BadRequest, forgedMetadata.StatusCode);
    }

    [Fact]
    public async Task Source_upload_rejects_content_over_the_50_mib_bound()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        await SecurityBoundaryTests.BootstrapAndLoginAdministratorAsync(client);
        using var created = await client.PostAsJsonAsync("/api/project-setups", new { projectName = "Oversize source" });
        using var createdBody = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        var draftId = createdBody.RootElement.GetProperty("draftId").GetGuid();
        using var oversized = new ByteArrayContent(new byte[50 * 1024 * 1024 + 1]);
        using var response = await client.PostAsync(
            $"/api/project-setups/{draftId}/source/upload?expectedVersion=1&fileName=source.csv", oversized);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Source_reconcile_route_revalidates_the_durable_token_and_replays_only_current_source()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        await SecurityBoundaryTests.BootstrapAndLoginAdministratorAsync(client);
        using var created = await client.PostAsJsonAsync("/api/project-setups", new { projectName = "Route source" });
        using var createdBody = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        var draftId = createdBody.RootElement.GetProperty("draftId").GetGuid();
        using var details = await client.PutAsJsonAsync($"/api/project-setups/{draftId}", new
        {
            expectedVersion = 1, currentStep = "StartingPoint",
            project = new { name = "Route source", softwareProduct = "Route source product" },
            build = new { version = "1.3" }, selectedCategories = Array.Empty<string>(), ladder = new { },
            reviewRules = new { }, reviewRulesAccepted = true, repository = new { mode = "ConfigureLater" }, mapping = new { },
        });
        Assert.Equal(HttpStatusCode.OK, details.StatusCode);
        using var upload = new HttpRequestMessage(HttpMethod.Post,
            $"/api/project-setups/{draftId}/source/upload?expectedVersion=2&fileName=source.csv")
        { Content = new ByteArrayContent(CreateSource("source.csv")) };
        upload.Content.Headers.ContentType = new("application/octet-stream");
        using var uploaded = await client.SendAsync(upload);
        Assert.Equal(HttpStatusCode.OK, uploaded.StatusCode);
        using var uploadedBody = JsonDocument.Parse(await uploaded.Content.ReadAsStringAsync());
        var uploadVersion = uploadedBody.RootElement.GetProperty("draftVersion").GetInt64();

        using var sourceResponse = await client.GetAsync($"/api/project-setups/{draftId}/source");
        using var source = JsonDocument.Parse(await sourceResponse.Content.ReadAsStringAsync());
        using var configured = await client.PutAsJsonAsync($"/api/project-setups/{draftId}/source/configuration", new
        {
            expectedVersion = uploadVersion, selectedCategories = new[] { "Requirements" },
            mapping = BuildMapping(source.RootElement, "source.csv"), metadata = new { },
        });
        Assert.Equal(HttpStatusCode.OK, configured.StatusCode);
        using var configuredBody = JsonDocument.Parse(await configured.Content.ReadAsStringAsync());
        var configuredVersion = configuredBody.RootElement.GetProperty("draftVersion").GetInt64();

        // Exercise the actual POST route after configuration has already persisted a valid mapping. The route
        // must advance the same durable draft token and return the server reconciliation, not trust browser JSON.
        using var reconciled = await client.PostAsJsonAsync($"/api/project-setups/{draftId}/source/reconcile", new
        { expectedVersion = configuredVersion });
        Assert.Equal(HttpStatusCode.OK, reconciled.StatusCode);
        using var reconciledBody = JsonDocument.Parse(await reconciled.Content.ReadAsStringAsync());
        Assert.Equal(configuredVersion + 1, reconciledBody.RootElement.GetProperty("draftVersion").GetInt64());
        Assert.True(reconciledBody.RootElement.GetProperty("reconciliation").GetProperty("ready").GetBoolean());

        // A stale route request cannot replay or overwrite a newer reconciliation.
        using var stale = await client.PostAsJsonAsync($"/api/project-setups/{draftId}/source/reconcile", new
        { expectedVersion = configuredVersion });
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        using var staleBody = JsonDocument.Parse(await stale.Content.ReadAsStringAsync());
        Assert.Equal("draft_conflict", staleBody.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Native_capture_reconcile_and_materialize_preserves_source_authorship_without_staffing_target()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        await SecurityBoundaryTests.BootstrapAndLoginAdministratorAsync(client);

        var now = DateTimeOffset.UtcNow;
        Guid sourceProjectId;
        Guid sourceBaselineId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var program = new ProgramRecord("Native source program", "NSP");
            var project = new ProjectRecord(program.Id, "Native source project", "Native source product");
            var release = new SoftwareRelease(project.Id, "1.0", false);
            var baseline = new CandidateBaseline("SW-91.01", 0, project.Id, release.Id, null,
                "Native frozen source", "source.manager", now);
            var sourceChange = new SystemChangeRequest("SRCR-910001", 0, project.Id, release.Id,
                "Native source requirement", "Problem", "Analysis", "Solution", "source.author", now);
            var requirement = new RequirementArtifact(project.Id, "SYSR-910001", RequirementLevel.System, now);
            var requirementRevision = new RequirementRevision(requirement.Id, 0, "The source requirement shall remain attributable.",
                "Source rationale", "", RequirementRevisionState.Active, sourceChange.Id, baseline.Id, now);
            var procedure = new TestProcedure(project.Id, "SYSTP-910001", "Native source procedure", "source.owner", now,
                TestProcedureLevel.System);
            var procedureRevision = new TestProcedureRevision(procedure.Id, 2, "Source objective", "Source preconditions",
                "Source steps", "Source expected result", TestProcedureState.Approved, "source.author", now);
            baseline.FreezeForInception("source.manager", now);
            baseline.MarkRequirementsMaterialized("source.manager", new string('a', 64), 1, now);
            baseline.MarkTestProceduresMaterialized("source.manager", new string('b', 64), 1, now);
            db.AddRange(program, project, release, baseline, sourceChange, requirement, requirementRevision,
                new BaselineRequirementSelection(baseline.Id, requirement.Id, requirementRevision.Id), procedure,
                procedureRevision, new BaselineTestProcedureSelection(baseline.Id, procedure.Id, procedureRevision.Id));
            await db.SaveChangesAsync();
            sourceProjectId = project.Id;
            sourceBaselineId = baseline.Id;
        }

        using var created = await client.PostAsJsonAsync("/api/project-setups", new { projectName = "Native destination" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        using var createdBody = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        var draftId = createdBody.RootElement.GetProperty("draftId").GetGuid();
        using var details = await client.PutAsJsonAsync($"/api/project-setups/{draftId}", new
        {
            expectedVersion = 1,
            currentStep = "StartingPoint",
            project = new { name = "Native destination", softwareProduct = "Native destination product" },
            build = new { version = "1.3" },
            selectedCategories = Array.Empty<string>(),
            ladder = new { }, reviewRules = new { }, reviewRulesAccepted = true,
            repository = new { mode = "ConfigureLater" }, mapping = new { },
        });
        Assert.Equal(HttpStatusCode.OK, details.StatusCode);

        using var capture = await client.PostAsJsonAsync($"/api/project-setups/{draftId}/source/native", new
        {
            expectedVersion = 2, baselineId = sourceBaselineId,
        });
        Assert.Equal(HttpStatusCode.OK, capture.StatusCode);
        using var captureBody = JsonDocument.Parse(await capture.Content.ReadAsStringAsync());
        var capturedVersion = captureBody.RootElement.GetProperty("draftVersion").GetInt64();

        using var observedResponse = await client.GetAsync($"/api/project-setups/{draftId}/source");
        Assert.Equal(HttpStatusCode.OK, observedResponse.StatusCode);
        using var observed = JsonDocument.Parse(await observedResponse.Content.ReadAsStringAsync());
        Assert.Equal(sourceProjectId, observed.RootElement.GetProperty("sourceProjectId").GetGuid());
        var nativeObjects = observed.RootElement.GetProperty("modules").EnumerateArray()
            .SelectMany(x => x.GetProperty("objects").EnumerateArray()).ToArray();
        var nativeProcedure = Assert.Single(nativeObjects, x => x.GetProperty("kind").GetString() == "Procedure");
        var nativeAttributes = nativeProcedure.GetProperty("attributes");
        Assert.Equal("source.owner", nativeAttributes.GetProperty("SourceOwnerId").GetString());
        Assert.Equal("source.author", nativeAttributes.GetProperty("SourceAuthorId").GetString());

        using var configured = await client.PutAsJsonAsync($"/api/project-setups/{draftId}/source/configuration", new
        {
            expectedVersion = capturedVersion,
            selectedCategories = new[] { "Requirements", "Procedures" },
            mapping = BuildNativeMapping(observed.RootElement), metadata = new { },
        });
        Assert.Equal(HttpStatusCode.OK, configured.StatusCode);
        using var configuredBody = JsonDocument.Parse(await configured.Content.ReadAsStringAsync());
        Assert.Equal("Reconciled", configuredBody.RootElement.GetProperty("stage").GetString());
        var configuredVersion = configuredBody.RootElement.GetProperty("draftVersion").GetInt64();
        using var readyResponse = await client.GetAsync($"/api/project-setups/{draftId}/source");
        using var ready = JsonDocument.Parse(await readyResponse.Content.ReadAsStringAsync());
        var assertionHash = ready.RootElement.GetProperty("assertion").GetProperty("hash").GetString();

        using var finalized = await client.PostAsJsonAsync($"/api/project-setups/{draftId}/finalize", new
        {
            expectedVersion = configuredVersion, idempotencyKey = "native-authorship-1",
            password = AeroLinkApiFactory.AdministratorPassword,
            sourceAssertionHash = assertionHash, sourceAssertionAccepted = true,
        });
        Assert.True(finalized.IsSuccessStatusCode, $"{finalized.StatusCode}: {await finalized.Content.ReadAsStringAsync()}");
        using var finalizedBody = JsonDocument.Parse(await finalized.Content.ReadAsStringAsync());
        var projectId = finalizedBody.RootElement.GetProperty("projectId").GetGuid();
        using var scopeAfter = factory.Services.CreateScope();
        var after = scopeAfter.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        var targetProcedure = await after.TestProcedures.SingleAsync(x => x.ProjectId == projectId);
        Assert.Equal("", targetProcedure.OwnerId);
        var targetRevision = await after.TestProcedureRevisions.SingleAsync(x => x.ProcedureId == targetProcedure.Id);
        Assert.Equal("", targetRevision.AuthorId);
        Assert.Equal(TestProcedureState.Draft, targetRevision.State);
        var sourceRecord = await after.ProjectInceptionSourceRecords.SingleAsync(x => x.ProjectId == projectId
            && x.TargetKind == "TestProcedure");
        Assert.Contains("source.owner", sourceRecord.SourceSnapshotJson, StringComparison.Ordinal);
        Assert.Contains("source.author", sourceRecord.SourceSnapshotJson, StringComparison.Ordinal);
        Assert.Equal(CandidateBaselineState.Frozen,
            await after.CandidateBaselines.Where(x => x.Id == sourceBaselineId).Select(x => x.State).SingleAsync());
    }

    /// <summary>
    /// I09 for the native path: a real AeroLink baseline is captured, reconciled and accepted, then the
    /// creator repairs a capability profile on the draft. That change must invalidate the accepted
    /// reconciliation while keeping the exact staged source and every unrelated answer, and the supported
    /// flow must reconcile, re-accept and materialize the repaired configuration.
    /// </summary>
    [Fact]
    public async Task Native_source_repair_invalidates_reconciliation_and_reaccepts_the_repaired_profile()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        await SecurityBoundaryTests.BootstrapAndLoginAdministratorAsync(client);

        var now = DateTimeOffset.UtcNow;
        Guid sourceBaselineId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var program = new ProgramRecord("Native repair program", "NRP");
            var project = new ProjectRecord(program.Id, "Native repair source", "Native repair product");
            var release = new SoftwareRelease(project.Id, "1.0", false);
            var baseline = new CandidateBaseline("SW-92.01", 0, project.Id, release.Id, null,
                "Native repair source", "source.manager", now);
            var sourceChange = new SystemChangeRequest("SRCR-920001", 0, project.Id, release.Id,
                "Native repair requirement", "Problem", "Analysis", "Solution", "source.author", now);
            var requirement = new RequirementArtifact(project.Id, "SYSR-920001", RequirementLevel.System, now);
            var requirementRevision = new RequirementRevision(requirement.Id, 0,
                "The repaired source requirement shall remain attributable.", "Source rationale", "",
                RequirementRevisionState.Active, sourceChange.Id, baseline.Id, now);
            var procedure = new TestProcedure(project.Id, "SYSTP-920001", "Native repair procedure",
                "source.owner", now, TestProcedureLevel.System);
            var procedureRevision = new TestProcedureRevision(procedure.Id, 2, "Source objective",
                "Source preconditions", "Source steps", "Source expected result", TestProcedureState.Approved,
                "source.author", now);
            baseline.FreezeForInception("source.manager", now);
            baseline.MarkRequirementsMaterialized("source.manager", new string('c', 64), 1, now);
            baseline.MarkTestProceduresMaterialized("source.manager", new string('d', 64), 1, now);
            db.AddRange(program, project, release, baseline, sourceChange, requirement, requirementRevision,
                new BaselineRequirementSelection(baseline.Id, requirement.Id, requirementRevision.Id), procedure,
                procedureRevision, new BaselineTestProcedureSelection(baseline.Id, procedure.Id, procedureRevision.Id));
            await db.SaveChangesAsync();
            sourceBaselineId = baseline.Id;
        }

        using var created = await client.PostAsJsonAsync("/api/project-setups", new { projectName = "Native repair destination" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        using var createdBody = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        var draftId = createdBody.RootElement.GetProperty("draftId").GetGuid();
        using var details = await client.PutAsJsonAsync($"/api/project-setups/{draftId}", new
        {
            expectedVersion = 1,
            currentStep = "StartingPoint",
            project = new { name = "Native repair destination", softwareProduct = "Native repair destination product" },
            build = new { version = "1.4" },
            selectedCategories = Array.Empty<string>(),
            ladder = new { }, reviewRules = new { }, reviewRulesAccepted = true,
            repository = new { mode = "ConfigureLater" }, mapping = new { },
        });
        Assert.Equal(HttpStatusCode.OK, details.StatusCode);

        using var capture = await client.PostAsJsonAsync($"/api/project-setups/{draftId}/source/native", new
        {
            expectedVersion = 2, baselineId = sourceBaselineId,
        });
        Assert.Equal(HttpStatusCode.OK, capture.StatusCode);
        using var captureBody = JsonDocument.Parse(await capture.Content.ReadAsStringAsync());
        var capturedVersion = captureBody.RootElement.GetProperty("draftVersion").GetInt64();

        using var observedResponse = await client.GetAsync($"/api/project-setups/{draftId}/source");
        Assert.Equal(HttpStatusCode.OK, observedResponse.StatusCode);
        using var observed = JsonDocument.Parse(await observedResponse.Content.ReadAsStringAsync());
        var stagedSourceId = observed.RootElement.GetProperty("id").GetGuid();
        var stagedSourceSha = observed.RootElement.GetProperty("sha256").GetString();
        var stagedObjectKeys = observed.RootElement.GetProperty("modules").EnumerateArray()
            .SelectMany(x => x.GetProperty("objects").EnumerateArray())
            .Select(x => x.GetProperty("key").GetString()).OrderBy(x => x, StringComparer.Ordinal).ToArray();

        using var configured = await client.PutAsJsonAsync($"/api/project-setups/{draftId}/source/configuration", new
        {
            expectedVersion = capturedVersion,
            selectedCategories = new[] { "Requirements", "Procedures" },
            mapping = BuildNativeMapping(observed.RootElement), metadata = new { },
        });
        Assert.Equal(HttpStatusCode.OK, configured.StatusCode);
        using var configuredBody = JsonDocument.Parse(await configured.Content.ReadAsStringAsync());
        Assert.Equal("Reconciled", configuredBody.RootElement.GetProperty("stage").GetString());
        var configuredVersion = configuredBody.RootElement.GetProperty("draftVersion").GetInt64();
        using var readyResponse = await client.GetAsync($"/api/project-setups/{draftId}/source");
        using var ready = JsonDocument.Parse(await readyResponse.Content.ReadAsStringAsync());
        var acceptedAssertionHash = ready.RootElement.GetProperty("assertion").GetProperty("hash").GetString();
        Assert.False(string.IsNullOrWhiteSpace(acceptedAssertionHash));

        // The creator's repair: an explicit ladder with a Case-only software profile. The accepted
        // reconciliation was written for the previous ladder, so it can no longer stand.
        using var repaired = await client.PutAsJsonAsync($"/api/project-setups/{draftId}", new
        {
            expectedVersion = configuredVersion,
            currentStep = "Ladder",
            project = new { name = "Native repair destination", softwareProduct = "Native repair destination product" },
            build = new { version = "1.4" },
            ladder = new
            {
                steps = new object[]
                {
                    new { catalogueEntry = "System", position = 1, capabilities = 7,
                        enabledArtifactKinds = new[] { "Procedure" } },
                    new { catalogueEntry = "HighLevel", position = 2, capabilities = 7,
                        enabledArtifactKinds = new[] { "Case" } },
                    new { catalogueEntry = "LowLevel", position = 3, capabilities = 15,
                        enabledArtifactKinds = new[] { "Case" } },
                },
                relationships = new object[]
                {
                    new { parent = "System", child = "HighLevel" },
                    new { parent = "HighLevel", child = "LowLevel" },
                },
            },
            reviewRules = new { }, reviewRulesAccepted = true,
            repository = new { mode = "ConfigureLater" },
        });
        Assert.Equal(HttpStatusCode.OK, repaired.StatusCode);
        using var repairedBody = JsonDocument.Parse(await repaired.Content.ReadAsStringAsync());
        var repairedVersion = repairedBody.RootElement.GetProperty("version").GetInt64();
        // The readiness verdict is scoped to ladder/profile and rule compatibility, so it can still describe a
        // compatible ladder; the source acceptance is the fact the repair invalidates — and the final gate must
        // refuse the stale assertion until the source is reconciled and accepted again.
        using var refusedBeforeReconcile = await client.PostAsJsonAsync($"/api/project-setups/{draftId}/finalize", new
        {
            expectedVersion = repairedVersion, idempotencyKey = "native-repair-before-reconcile",
            password = AeroLinkApiFactory.AdministratorPassword,
            sourceAssertionHash = acceptedAssertionHash, sourceAssertionAccepted = true,
        });
        Assert.Equal(HttpStatusCode.BadRequest, refusedBeforeReconcile.StatusCode);
        using var refusal = JsonDocument.Parse(await refusedBeforeReconcile.Content.ReadAsStringAsync());
        Assert.Equal("cannot_finalize", refusal.RootElement.GetProperty("code").GetString());

        // The exact staged source and the unrelated answers survive the repair.
        using var afterRepairResponse = await client.GetAsync($"/api/project-setups/{draftId}/source");
        using var afterRepair = JsonDocument.Parse(await afterRepairResponse.Content.ReadAsStringAsync());
        Assert.Equal(stagedSourceId, afterRepair.RootElement.GetProperty("id").GetGuid());
        Assert.Equal(stagedSourceSha, afterRepair.RootElement.GetProperty("sha256").GetString());
        Assert.Equal(JsonValueKind.Null, afterRepair.RootElement.GetProperty("reconciliation").ValueKind);
        Assert.Equal(JsonValueKind.Null, afterRepair.RootElement.GetProperty("assertion").ValueKind);
        Assert.Equal(stagedObjectKeys, afterRepair.RootElement.GetProperty("modules").EnumerateArray()
            .SelectMany(x => x.GetProperty("objects").EnumerateArray())
            .Select(x => x.GetProperty("key").GetString()).OrderBy(x => x, StringComparer.Ordinal).ToArray());
        using var draftAfterRepairResponse = await client.GetAsync($"/api/project-setups/{draftId}");
        using var draftAfterRepair = JsonDocument.Parse(await draftAfterRepairResponse.Content.ReadAsStringAsync());
        Assert.Equal("Native repair destination",
            draftAfterRepair.RootElement.GetProperty("project").GetProperty("name").GetString());
        Assert.Equal("1.4", draftAfterRepair.RootElement.GetProperty("build").GetProperty("version").GetString());
        Assert.Equal("AeroLinkBaseline", draftAfterRepair.RootElement.GetProperty("start").GetProperty("kind").GetString());
        Assert.Equal(sourceBaselineId,
            draftAfterRepair.RootElement.GetProperty("start").GetProperty("sourceBaselineId").GetGuid());
        Assert.Equal(new[] { "Procedures", "Requirements" },
            draftAfterRepair.RootElement.GetProperty("selectedCategories").EnumerateArray()
                .Select(x => x.GetString() ?? "").OrderBy(x => x, StringComparer.Ordinal).ToArray());

        // Reconcile and accept again through the supported flow, then materialize the repaired configuration.
        using var reconciled = await client.PutAsJsonAsync($"/api/project-setups/{draftId}/source/configuration", new
        {
            expectedVersion = repairedVersion,
            selectedCategories = new[] { "Requirements", "Procedures" },
            mapping = BuildNativeMapping(afterRepair.RootElement), metadata = new { },
        });
        Assert.Equal(HttpStatusCode.OK, reconciled.StatusCode);
        using var reconciledBody = JsonDocument.Parse(await reconciled.Content.ReadAsStringAsync());
        Assert.Equal("Reconciled", reconciledBody.RootElement.GetProperty("stage").GetString());
        var reacceptedVersion = reconciledBody.RootElement.GetProperty("draftVersion").GetInt64();
        using var reacceptedResponse = await client.GetAsync($"/api/project-setups/{draftId}/source");
        using var reaccepted = JsonDocument.Parse(await reacceptedResponse.Content.ReadAsStringAsync());
        var reacceptedAssertionHash = reaccepted.RootElement.GetProperty("assertion").GetProperty("hash").GetString();
        Assert.False(string.IsNullOrWhiteSpace(reacceptedAssertionHash));
        // Re-reconciliation is a new committed configuration step, whether or not this particular content
        // produces a different manifest hash (the System-only source is unaffected by a software profile).
        Assert.True(reacceptedVersion > repairedVersion, "re-reconciliation advanced the draft");

        using var finalized = await client.PostAsJsonAsync($"/api/project-setups/{draftId}/finalize", new
        {
            expectedVersion = reacceptedVersion, idempotencyKey = "native-repair-1",
            password = AeroLinkApiFactory.AdministratorPassword,
            sourceAssertionHash = reacceptedAssertionHash, sourceAssertionAccepted = true,
        });
        Assert.True(finalized.IsSuccessStatusCode,
            $"{finalized.StatusCode}: {await finalized.Content.ReadAsStringAsync()}");
        using var finalizedBody = JsonDocument.Parse(await finalized.Content.ReadAsStringAsync());
        var projectId = finalizedBody.RootElement.GetProperty("projectId").GetGuid();

        // The created project carries the repaired profile and the materialized native content, and the source
        // baseline is untouched evidence rather than consumed input.
        using var scopeAfter = factory.Services.CreateScope();
        var after = scopeAfter.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        var steps = await after.ProjectLadderSteps.AsNoTracking().Where(x => x.ProjectId == projectId)
            .OrderBy(x => x.Position).ToListAsync();
        Assert.Equal(new[] { VerificationArtifactKind.Case },
            steps.Single(x => x.CatalogueEntry == "HighLevel").EnabledArtifactKinds);
        Assert.Equal(new[] { VerificationArtifactKind.Case },
            steps.Single(x => x.CatalogueEntry == "LowLevel").EnabledArtifactKinds);
        var materializedRequirement = await after.Requirements.AsNoTracking()
            .SingleAsync(x => x.ProjectId == projectId);
        var materializedRevision = await after.RequirementRevisions.AsNoTracking()
            .SingleAsync(x => x.ArtifactId == materializedRequirement.Id);
        Assert.Contains("repaired source requirement", materializedRevision.Statement,
            StringComparison.OrdinalIgnoreCase);
        var sourceRecordForRequirement = await after.ProjectInceptionSourceRecords.AsNoTracking()
            .SingleAsync(x => x.ProjectId == projectId && x.TargetKind == "Requirement");
        Assert.Contains("SYSR-920001", sourceRecordForRequirement.SourceSnapshotJson, StringComparison.Ordinal);
        var targetProcedure = await after.TestProcedures.SingleAsync(x => x.ProjectId == projectId);
        Assert.Equal("", targetProcedure.OwnerId);
        Assert.Equal(CandidateBaselineState.Frozen,
            await after.CandidateBaselines.Where(x => x.Id == sourceBaselineId).Select(x => x.State).SingleAsync());
    }

    [Theory]
    [InlineData("source.csv")]
    [InlineData("source.xlsx")]
    [InlineData("source.reqif")]
    public async Task Administrator_can_reconcile_and_accept_each_supported_external_format(string fileName)
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        await SecurityBoundaryTests.BootstrapAndLoginAdministratorAsync(client);

        using var created = await client.PostAsJsonAsync("/api/project-setups", new { projectName = $"Inception {fileName}" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        using var createdBody = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        var draftId = createdBody.RootElement.GetProperty("draftId").GetGuid();

        // Set project identity, build identity, and the explicit maintained review standard before selecting a
        // source. Upload then owns the source selection and advances the same durable draft token.
        using var details = await client.PutAsJsonAsync($"/api/project-setups/{draftId}", new
        {
            expectedVersion = 1,
            currentStep = "StartingPoint",
            project = new { name = $"Inception {fileName}", softwareProduct = "Imported Product" },
            build = new { version = "1.3" },
            selectedCategories = Array.Empty<string>(),
            ladder = new { },
            reviewRules = new { },
            reviewRulesAccepted = true,
            repository = new { mode = "ConfigureLater" },
            mapping = new { },
        });
        Assert.Equal(HttpStatusCode.OK, details.StatusCode);
        using var detailsBody = JsonDocument.Parse(await details.Content.ReadAsStringAsync());
        Assert.Equal(2, detailsBody.RootElement.GetProperty("version").GetInt64());

        var sourceBytes = CreateSource(fileName);
        using var upload = new HttpRequestMessage(HttpMethod.Post,
            $"/api/project-setups/{draftId}/source/upload?expectedVersion=2&fileName={fileName}")
        {
            Content = new ByteArrayContent(sourceBytes),
        };
        upload.Content.Headers.ContentType = new("application/octet-stream");
        using var uploaded = await client.SendAsync(upload);
        Assert.Equal(HttpStatusCode.OK, uploaded.StatusCode);
        using var uploadedBody = JsonDocument.Parse(await uploaded.Content.ReadAsStringAsync());
        var sourceVersion = uploadedBody.RootElement.GetProperty("draftVersion").GetInt64();

        using var observedResponse = await client.GetAsync($"/api/project-setups/{draftId}/source");
        Assert.Equal(HttpStatusCode.OK, observedResponse.StatusCode);
        using var observed = JsonDocument.Parse(await observedResponse.Content.ReadAsStringAsync());
        var observedRoot = observed.RootElement;
        Assert.Equal("ExternalBaseline", observedRoot.GetProperty("kind").GetString());
        Assert.Equal(Path.GetExtension(fileName).TrimStart('.').ToUpperInvariant(), observedRoot.GetProperty("format").GetString());
        var observedObjects = observedRoot.GetProperty("modules").EnumerateArray()
            .SelectMany(x => x.GetProperty("objects").EnumerateArray()).ToArray();
        Assert.Single(observedObjects);
        Assert.Equal("FOREIGN-1", observedObjects[0].GetProperty("sourceIdentifier").GetString());
        Assert.DoesNotContain(observedObjects[0].GetProperty("attributes").EnumerateObject(),
            x => x.Name.Equals("StorageKey", StringComparison.OrdinalIgnoreCase));

        var mapping = BuildMapping(observedRoot, fileName);
        using var configured = await client.PutAsJsonAsync($"/api/project-setups/{draftId}/source/configuration", new
        {
            expectedVersion = sourceVersion,
            selectedCategories = new[] { "Requirements" },
            mapping,
            metadata = new { },
        });
        Assert.Equal(HttpStatusCode.OK, configured.StatusCode);
        using var configuredBody = JsonDocument.Parse(await configured.Content.ReadAsStringAsync());
        Assert.Equal("Reconciled", configuredBody.RootElement.GetProperty("stage").GetString());
        var configuredVersion = configuredBody.RootElement.GetProperty("draftVersion").GetInt64();
        Assert.NotEqual(JsonValueKind.Null, configuredBody.RootElement.GetProperty("manifestHash").ValueKind);

        using var savedUnchangedLadder = await client.PutAsJsonAsync($"/api/project-setups/{draftId}", new
        { expectedVersion = configuredVersion, currentStep = "Review", ladder = new { } });
        Assert.Equal(HttpStatusCode.OK, savedUnchangedLadder.StatusCode);
        configuredVersion = (await savedUnchangedLadder.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("version").GetInt64();
        using var sourceAfterSave = await client.GetAsync($"/api/project-setups/{draftId}/source");
        Assert.Equal("Reconciled", (await sourceAfterSave.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("stage").GetString());

        // Backtracking without resending source fields clears inherited answers, while returning to the
        // exact upload restores the server-owned configuration for every supported external format.
        using var switchedFresh = await client.PutAsJsonAsync($"/api/project-setups/{draftId}", new
        { expectedVersion = configuredVersion, currentStep = "StartingPoint", start = new { kind = "Fresh" } });
        Assert.Equal(HttpStatusCode.OK, switchedFresh.StatusCode);
        var switchedDraft = await switchedFresh.Content.ReadFromJsonAsync<JsonElement>();
        var switchedVersion = switchedDraft.GetProperty("version").GetInt64();
        using var reupload = new HttpRequestMessage(HttpMethod.Post,
            $"/api/project-setups/{draftId}/source/upload?expectedVersion={switchedVersion}&fileName={fileName}")
        { Content = new ByteArrayContent(sourceBytes) };
        reupload.Content.Headers.ContentType = new("application/octet-stream");
        using var reuploaded = await client.SendAsync(reupload);
        Assert.Equal(HttpStatusCode.OK, reuploaded.StatusCode);
        var restored = await reuploaded.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(uploadedBody.RootElement.GetProperty("id").GetGuid(), restored.GetProperty("id").GetGuid());
        Assert.Equal("Reconciled", restored.GetProperty("stage").GetString());
        configuredVersion = restored.GetProperty("draftVersion").GetInt64();
        configuredVersion = await AssertReselectionRevalidatesLadderAsync(client, draftId, configuredVersion,
            async version =>
            {
                using var request = new HttpRequestMessage(HttpMethod.Post,
                    $"/api/project-setups/{draftId}/source/upload?expectedVersion={version}&fileName={fileName}")
                { Content = new ByteArrayContent(sourceBytes) };
                request.Content.Headers.ContentType = new("application/octet-stream");
                using var response = await client.SendAsync(request);
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                return await response.Content.ReadFromJsonAsync<JsonElement>();
            });

        using var readyResponse = await client.GetAsync($"/api/project-setups/{draftId}/source");
        Assert.Equal(HttpStatusCode.OK, readyResponse.StatusCode);
        using var ready = JsonDocument.Parse(await readyResponse.Content.ReadAsStringAsync());
        var assertion = ready.RootElement.GetProperty("assertion");
        var assertionHash = assertion.GetProperty("hash").GetString();
        Assert.False(string.IsNullOrWhiteSpace(assertionHash));

        using var finalized = await client.PostAsJsonAsync($"/api/project-setups/{draftId}/finalize", new
        {
            expectedVersion = configuredVersion,
            idempotencyKey = $"inception-{fileName}",
            password = AeroLinkApiFactory.AdministratorPassword,
            sourceAssertionHash = assertionHash,
            sourceAssertionAccepted = true,
        });
        Assert.True(finalized.IsSuccessStatusCode, $"{finalized.StatusCode}: {await finalized.Content.ReadAsStringAsync()}");
        using var finalizedBody = JsonDocument.Parse(await finalized.Content.ReadAsStringAsync());
        var projectId = finalizedBody.RootElement.GetProperty("projectId").GetGuid();
        Assert.Equal("Completed", finalizedBody.RootElement.GetProperty("state").GetString());
        Assert.Equal("SW-01.30", finalizedBody.RootElement.GetProperty("officialBuildName").GetString());
        await AssertInheritedDocumentsAsync(client, finalizedBody.RootElement.GetProperty("releaseId").GetGuid(),
            $"Inception {fileName}", "Imported exact wording");

        using var provenanceResponse = await client.GetAsync($"/api/projects/{projectId}/inception-source");
        Assert.Equal(HttpStatusCode.OK, provenanceResponse.StatusCode);
        using var provenance = JsonDocument.Parse(await provenanceResponse.Content.ReadAsStringAsync());
        var provenanceRoot = provenance.RootElement;
        Assert.Equal(projectId, provenanceRoot.GetProperty("projectId").GetGuid());
        Assert.Equal(sourceBytes.Length, provenanceRoot.GetProperty("package").GetProperty("sizeBytes").GetInt64());
        Assert.Equal(assertionHash, provenanceRoot.GetProperty("package").GetProperty("assertionHash").GetString());
        Assert.Equal("admin", provenanceRoot.GetProperty("acceptance").GetProperty("userName").GetString());
        Assert.Contains("Accepted source", provenanceRoot.GetProperty("acceptance").GetProperty("meaning").GetString(), StringComparison.Ordinal);
        var provenanceRecord = Assert.Single(provenanceRoot.GetProperty("records").EnumerateArray());
        Assert.DoesNotContain("StorageKey", provenanceRecord.GetProperty("sourceSnapshot").GetRawText(), StringComparison.OrdinalIgnoreCase);

        // Provenance is project-scoped source evidence. A user outside the program and a member whose
        // source-program access has ended must both be denied, while an active ordinary member may inspect it.
        var programId = finalizedBody.RootElement.GetProperty("programId").GetGuid();
        var baselineId = provenanceRecord.GetProperty("baselineId").GetGuid();
        var suffix = fileName.Replace('.', '-');
        var memberName = $"inception-provenance-member-{suffix}";
        var outsiderName = $"inception-provenance-outsider-{suffix}";
        Guid memberId;
        using (var identityScope = factory.Services.CreateScope())
        {
            var identityDb = identityScope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var member = new UserAccount(memberName, "Inception Provenance Member", $"{memberName}@example.test",
                IdentityService.HashPassword(AeroLinkApiFactory.MemberPassword), DateTimeOffset.UtcNow);
            var outsider = new UserAccount(outsiderName, "Inception Provenance Outsider", $"{outsiderName}@example.test",
                IdentityService.HashPassword(AeroLinkApiFactory.MemberPassword), DateTimeOffset.UtcNow);
            identityDb.AddRange(member, outsider,
                new ProgramMembership(member.Id, programId, ProgramRole.Engineer, "test.setup", DateTimeOffset.UtcNow));
            await identityDb.SaveChangesAsync();
            memberId = member.Id;
        }
        using var memberClient = factory.CreateClient();
        using var memberLogin = await memberClient.PostAsJsonAsync("/api/auth/login", new
        { userName = memberName, password = AeroLinkApiFactory.MemberPassword });
        Assert.Equal(HttpStatusCode.OK, memberLogin.StatusCode);
        using var memberProvenance = await memberClient.GetAsync($"/api/projects/{projectId}/inception-source");
        Assert.Equal(HttpStatusCode.OK, memberProvenance.StatusCode);
        var requirementsUrl = $"/api/requirements?projectId={projectId}&baselineId={baselineId}&page=1&pageSize=50";
        using var memberRequirements = await memberClient.GetAsync(requirementsUrl);
        Assert.Equal(HttpStatusCode.OK, memberRequirements.StatusCode);
        Assert.Contains("Imported exact wording", await memberRequirements.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        using (var revokeScope = factory.Services.CreateScope())
        {
            var revokeDb = revokeScope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var membership = await revokeDb.ProgramMemberships.SingleAsync(x => x.UserId == memberId
                && x.ProgramId == programId && x.EndedAt == null);
            membership.End("test.setup", DateTimeOffset.UtcNow);
            await revokeDb.SaveChangesAsync();
        }
        using var endedProvenance = await memberClient.GetAsync($"/api/projects/{projectId}/inception-source");
        Assert.Equal(HttpStatusCode.Forbidden, endedProvenance.StatusCode);
        using var endedRequirements = await memberClient.GetAsync(requirementsUrl);
        Assert.Equal(HttpStatusCode.Forbidden, endedRequirements.StatusCode);

        using var outsiderClient = factory.CreateClient();
        using var outsiderLogin = await outsiderClient.PostAsJsonAsync("/api/auth/login", new
        { userName = outsiderName, password = AeroLinkApiFactory.MemberPassword });
        Assert.Equal(HttpStatusCode.OK, outsiderLogin.StatusCode);
        using var outsiderProvenance = await outsiderClient.GetAsync($"/api/projects/{projectId}/inception-source");
        Assert.Equal(HttpStatusCode.Forbidden, outsiderProvenance.StatusCode);
        // Project access is required even when the caller omits the browser's optional build header.
        Assert.False(outsiderClient.DefaultRequestHeaders.Contains("X-AeroLink-Build-Context"));
        using var outsiderRequirements = await outsiderClient.GetAsync(requirementsUrl);
        Assert.Equal(HttpStatusCode.Forbidden, outsiderRequirements.StatusCode);
        Assert.DoesNotContain("Imported exact wording", await outsiderRequirements.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        // A newer signature with a different assertion hash is not the source acceptance represented by this
        // package. The route must continue to return the exact hash-bound acceptance fact.
        using (var signatureScope = factory.Services.CreateScope())
        {
            var signatureDb = signatureScope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            signatureDb.ElectronicSignatures.Add(new ElectronicSignature(Guid.NewGuid(), "later.actor",
                "Later Actor", programId, "ProjectInceptionSourceAssertion", baselineId, "1", "AcceptSource",
                "Later assertion", new string('f', 64), "local", DateTimeOffset.UtcNow.AddMinutes(1),
                authority: "Administrator"));
            await signatureDb.SaveChangesAsync();
        }
        using var exactProvenance = await client.GetAsync($"/api/projects/{projectId}/inception-source");
        Assert.Equal(HttpStatusCode.OK, exactProvenance.StatusCode);
        using var exactBody = JsonDocument.Parse(await exactProvenance.Content.ReadAsStringAsync());
        Assert.Equal(assertionHash, exactBody.RootElement.GetProperty("acceptance").GetProperty("contentHash").GetString());

        // Replay with the last client-known token proves the committed finalization is recoverable after a lost
        // response. No second project or source import may be created.
        using var replay = await client.PostAsJsonAsync($"/api/project-setups/{draftId}/finalize", new
        {
            expectedVersion = configuredVersion,
            idempotencyKey = $"inception-{fileName}-retry",
        });
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        using var replayBody = JsonDocument.Parse(await replay.Content.ReadAsStringAsync());
        Assert.True(replayBody.RootElement.GetProperty("alreadyCompleted").GetBoolean());
        Assert.Equal(projectId, replayBody.RootElement.GetProperty("projectId").GetGuid());

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        Assert.Single(await db.Projects.Where(x => x.Id == projectId).ToListAsync());
        Assert.Single(await db.BaselineImports.Where(x => x.ProjectId == projectId).ToListAsync());
        var completionAudit = await db.SecurityAuditEvents.SingleAsync(x => x.EventType == "ProjectSetupCompleted"
            && x.Target == draftId.ToString("D"));
        Assert.Contains($"exact source package {uploadedBody.RootElement.GetProperty("id").GetGuid():D}", completionAudit.Detail);
        var import = await db.BaselineImports.SingleAsync(x => x.ProjectId == projectId);
        Assert.Equal(BaselineImportState.Accepted, import.State);
        Assert.Equal(sourceBytes.LongLength, import.ExtractSizeBytes);
        Assert.NotEqual(string.Empty, import.ExtractSha256);
        var revision = await db.RequirementRevisions.SingleAsync(x => db.Requirements.Any(a => a.Id == x.ArtifactId && a.ProjectId == projectId));
        Assert.Equal(RequirementRevisionOriginKind.ExternalSourcePackage, revision.OriginKind);
        Assert.Equal(1, await db.ProjectInceptionSourceRecords.CountAsync(x => x.ProjectId == projectId));
        Assert.Equal(2, await db.ElectronicSignatures.CountAsync(x => x.ProgramId == (Guid)finalizedBody.RootElement.GetProperty("programId").GetGuid()
            && x.ArtifactType == "ProjectInceptionSourceAssertion"));
        Assert.Empty(await db.TestExecutions.Where(x => x.SoftwareBuildId != null && db.SoftwareBuilds.Any(b => b.Id == x.SoftwareBuildId && b.ProjectId == projectId)).ToListAsync());
    }

    private static object BuildMapping(JsonElement source, string fileName,
        string sourceOnlyReason = "Retain exact source attribute.")
    {
        var objects = source.GetProperty("modules").EnumerateArray()
            .SelectMany(x => x.GetProperty("objects").EnumerateArray())
            .Select(item =>
            {
                var attributes = item.GetProperty("attributes").EnumerateObject()
                    .Select(attribute => new
                    {
                        sourceAttribute = attribute.Name,
                        destination = Destination(attribute.Name),
                        reason = Destination(attribute.Name) == "SourceOnly" ? sourceOnlyReason : null,
                    }).ToArray();
                var level = item.GetProperty("attributes").EnumerateObject()
                    .FirstOrDefault(x => IsLevel(x.Name)).Value.ValueKind == JsonValueKind.String
                    ? item.GetProperty("attributes").EnumerateObject().First(x => IsLevel(x.Name)).Value.GetString()
                    : "System";
                return new { sourceKey = item.GetProperty("key").GetString(), include = true, level, attributes };
            }).ToArray();
        return new
        {
            sourceSha256 = source.GetProperty("sha256").GetString(),
            objects,
            relations = Array.Empty<object>(),
            findingResolutions = new Dictionary<string, string>(),
        };
    }

    private static object BuildNativeMapping(JsonElement source)
    {
        var objects = source.GetProperty("modules").EnumerateArray()
            .SelectMany(x => x.GetProperty("objects").EnumerateArray())
            .Select(item =>
            {
                var kind = item.GetProperty("kind").GetString() ?? "";
                var attributes = item.GetProperty("attributes").EnumerateObject()
                    .Select(attribute => new
                    {
                        sourceAttribute = attribute.Name,
                        destination = NativeDestination(kind, attribute.Name),
                        reason = NativeDestination(kind, attribute.Name) == "SourceOnly" ? "Retain exact source fact." : null,
                    }).ToArray();
                var level = item.GetProperty("attributes").EnumerateObject()
                    .FirstOrDefault(x => IsLevel(x.Name)).Value.ValueKind == JsonValueKind.String
                    ? item.GetProperty("attributes").EnumerateObject().First(x => IsLevel(x.Name)).Value.GetString()
                    : "System";
                return new { sourceKey = item.GetProperty("key").GetString(), include = true, level, attributes };
            }).ToArray();
        return new
        {
            sourceSha256 = source.GetProperty("sha256").GetString(), objects,
            relations = Array.Empty<object>(), findingResolutions = new Dictionary<string, string>(),
        };
    }

    private static string NativeDestination(string kind, string key)
    {
        if (kind.Equals("Requirement", StringComparison.OrdinalIgnoreCase))
        {
            if (key.Equals("Statement", StringComparison.OrdinalIgnoreCase)) return "Statement";
            if (key.Equals("Rationale", StringComparison.OrdinalIgnoreCase)) return "Rationale";
            if (key.Equals("VerificationMethod", StringComparison.OrdinalIgnoreCase)) return "VerificationMethod";
        }
        else if (key.Equals("Title", StringComparison.OrdinalIgnoreCase)) return "Title";
        else if (key.Equals("Objective", StringComparison.OrdinalIgnoreCase)) return "Objective";
        else if (key.Equals("Preconditions", StringComparison.OrdinalIgnoreCase)) return "Preconditions";
        else if (key.Equals("Steps", StringComparison.OrdinalIgnoreCase)) return "Steps";
        else if (key.Equals("ExpectedResult", StringComparison.OrdinalIgnoreCase)) return "ExpectedResult";
        return "SourceOnly";
    }

    private static string Destination(string key)
    {
        if (key.Equals("Identifier", StringComparison.OrdinalIgnoreCase)
            || key.Equals("ID", StringComparison.OrdinalIgnoreCase)
            || key.EndsWith(":id", StringComparison.OrdinalIgnoreCase)
            || key.EndsWith(":identifier", StringComparison.OrdinalIgnoreCase)) return "SourceIdentifier";
        if (key.Equals("Statement", StringComparison.OrdinalIgnoreCase)
            || key.EndsWith(":statement", StringComparison.OrdinalIgnoreCase)) return "Statement";
        return "SourceOnly";
    }

    private static bool IsLevel(string key) => key.Equals("Level", StringComparison.OrdinalIgnoreCase)
        || key.EndsWith(":level", StringComparison.OrdinalIgnoreCase);

    private static byte[] CreateSource(string fileName) => Path.GetExtension(fileName).ToLowerInvariant() switch
    {
        ".csv" => Encoding.UTF8.GetBytes("Identifier,Level,Statement\r\nFOREIGN-1,System,Imported exact wording\r\n"),
        ".xlsx" => CreateWorkbook(),
        ".reqif" => Encoding.UTF8.GetBytes("""
            <REQ-IF>
              <REQ-IF-HEADER><SOURCE-TOOL-ID>External Tool</SOURCE-TOOL-ID></REQ-IF-HEADER>
              <SPEC-TYPES><SPEC-OBJECT-TYPE IDENTIFIER="REQ">
                <ATTRIBUTE-DEFINITION-STRING IDENTIFIER="id" LONG-NAME="Identifier"/>
                <ATTRIBUTE-DEFINITION-STRING IDENTIFIER="level" LONG-NAME="Level"/>
                <ATTRIBUTE-DEFINITION-STRING IDENTIFIER="statement" LONG-NAME="Statement"/>
              </SPEC-OBJECT-TYPE></SPEC-TYPES>
              <SPEC-OBJECTS><SPEC-OBJECT IDENTIFIER="foreign-1">
                <TYPE><SPEC-OBJECT-TYPE-REF>REQ</SPEC-OBJECT-TYPE-REF></TYPE><VALUES>
                  <ATTRIBUTE-VALUE-STRING THE-VALUE="FOREIGN-1"><DEFINITION><ATTRIBUTE-DEFINITION-STRING-REF>id</ATTRIBUTE-DEFINITION-STRING-REF></DEFINITION></ATTRIBUTE-VALUE-STRING>
                  <ATTRIBUTE-VALUE-STRING THE-VALUE="System"><DEFINITION><ATTRIBUTE-DEFINITION-STRING-REF>level</ATTRIBUTE-DEFINITION-STRING-REF></DEFINITION></ATTRIBUTE-VALUE-STRING>
                  <ATTRIBUTE-VALUE-STRING THE-VALUE="Imported exact wording"><DEFINITION><ATTRIBUTE-DEFINITION-STRING-REF>statement</ATTRIBUTE-DEFINITION-STRING-REF></DEFINITION></ATTRIBUTE-VALUE-STRING>
                </VALUES>
              </SPEC-OBJECT></SPEC-OBJECTS>
            </REQ-IF>
            """),
        _ => throw new ArgumentException($"Unsupported test source: {fileName}"),
    };

    private static byte[] CreateWorkbook()
    {
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, true))
        {
            Write(archive, "xl/workbook.xml", "<workbook xmlns='http://schemas.openxmlformats.org/spreadsheetml/2006/main' xmlns:r='http://schemas.openxmlformats.org/officeDocument/2006/relationships'><sheets><sheet name='Requirements' sheetId='1' r:id='rId1'/></sheets></workbook>");
            Write(archive, "xl/_rels/workbook.xml.rels", "<Relationships xmlns='http://schemas.openxmlformats.org/package/2006/relationships'><Relationship Id='rId1' Type='http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet' Target='worksheets/sheet1.xml'/></Relationships>");
            Write(archive, "xl/worksheets/sheet1.xml", "<worksheet xmlns='http://schemas.openxmlformats.org/spreadsheetml/2006/main'><sheetData><row r='1'><c r='A1' t='inlineStr'><is><t>Identifier</t></is></c><c r='B1' t='inlineStr'><is><t>Level</t></is></c><c r='C1' t='inlineStr'><is><t>Statement</t></is></c></row><row r='2'><c r='A2' t='inlineStr'><is><t>FOREIGN-1</t></is></c><c r='B2' t='inlineStr'><is><t>System</t></is></c><c r='C2' t='inlineStr'><is><t>Imported exact wording</t></is></c></row></sheetData></worksheet>");
        }
        return output.ToArray();
    }

    private static void Write(ZipArchive archive, string name, string content)
    {
        using var writer = new StreamWriter(archive.CreateEntry(name).Open(), new UTF8Encoding(false));
        writer.Write(content);
    }
}
