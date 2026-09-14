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
        var import = await db.BaselineImports.SingleAsync(x => x.ProjectId == projectId);
        Assert.Equal(BaselineImportState.Accepted, import.State);
        Assert.Equal(sourceBytes.LongLength, import.ExtractSizeBytes);
        Assert.NotEqual(string.Empty, import.ExtractSha256);
        var revision = await db.RequirementRevisions.SingleAsync(x => db.Requirements.Any(a => a.Id == x.ArtifactId && a.ProjectId == projectId));
        Assert.Equal(RequirementRevisionOriginKind.ExternalSourcePackage, revision.OriginKind);
        Assert.Equal(1, await db.ProjectInceptionSourceRecords.CountAsync(x => x.ProjectId == projectId));
        Assert.Single(await db.ElectronicSignatures.Where(x => x.ProgramId == (Guid)finalizedBody.RootElement.GetProperty("programId").GetGuid()
            && x.ArtifactType == "ProjectInceptionSourceAssertion").ToListAsync());
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
