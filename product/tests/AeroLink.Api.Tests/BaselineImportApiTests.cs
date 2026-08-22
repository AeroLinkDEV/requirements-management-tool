using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AeroLink.Domain.Baselines;
using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Hierarchy;
using AeroLink.Domain.Identity;
using AeroLink.Domain.Imports;
using AeroLink.Domain.Programs;
using AeroLink.Domain.Requirements;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AeroLink.Api.Tests;

/// <summary>
/// Walking a program in from another tool, through the API.
///
/// The gates exist so that nothing is committed blind, and the acceptance exists so that a named person owns
/// the assertion. What must never happen is the thing these cover most closely: an imported baseline that
/// cannot be told apart from one this product built.
/// </summary>
public sealed class BaselineImportApiTests
{
    private const string Digest = "9f2c4b1e7a0d3c5589ab41e2f7c60d9b8e35a1470c2df6b849e0d17ac3d07a38";

    private static async Task<Guid> SeedProjectAsync(AeroLinkApiFactory factory, string prefix)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        var program = new ProgramRecord($"{prefix} Program", $"{prefix}{Guid.NewGuid():N}"[..12]);
        var project = new ProjectRecord(program.Id, "Flight Management Product", "Flight Management System");
        db.AddRange(program, project);
        await db.SaveChangesAsync();
        return project.Id;
    }

    private static object StartBody(Guid projectId, string[]? carries = null) => new
    {
        projectId,
        sourceSystem = "IBM Rational DOORS",
        sourceSystemVersion = "9.6.1.13",
        sourceBaselineName = "FMS Sys Req v4.2",
        sourceBaselineDate = new DateTimeOffset(2026, 6, 30, 0, 0, 0, TimeSpan.Zero),
        extractFileName = "FMS_SYSTEM_REQUIREMENTS_2026-07-14.reqifz",
        extractSha256 = Digest,
        extractSizeBytes = 43_842_112L,
        carries = carries ?? ["Requirements"],
        extractedBy = "m.chen",
        extractedAt = new DateTimeOffset(2026, 7, 14, 9, 12, 0, TimeSpan.Zero)
    };

    private static async Task<Guid> StartAsync(HttpClient client, Guid projectId)
    {
        using var created = await client.PostAsJsonAsync("/api/baseline-imports", StartBody(projectId));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        return (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    private static object SourceRecord(int number, bool inImportedBaseline = true, object[]? history = null) => new
    {
        sourceModule = "FMS_System_Requirements",
        sourceObjectKey = number.ToString(),
        sourceIdentifier = $"SYS-{number:00000}",
        inImportedBaseline,
        history
    };

    private static Task<HttpResponseMessage> RecordSourceRecordsAsync(HttpClient client, Guid id, params object[] records) =>
        client.PostAsJsonAsync($"/api/baseline-imports/{id}/source-records", new { records });

    [Fact]
    public async Task External_package_routes_fail_closed_for_missing_records()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        await ProblemReportApiTests.BootstrapAndLoginAsync(client);
        var missing = Guid.NewGuid();
        Assert.Equal(HttpStatusCode.NotFound,
            (await client.PostAsJsonAsync($"/api/baseline-imports/{missing}/customer-items", new { items = Array.Empty<object>() })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await client.PostAsJsonAsync($"/api/baselines/{missing}/external-packages", new { baselineImportId = Guid.NewGuid() })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await client.DeleteAsync($"/api/baselines/{missing}/external-packages/{Guid.NewGuid()}")).StatusCode);
    }

    [Fact]
    public async Task Reconciled_package_selection_accepts_and_binds_to_existing_draft_release()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        await ProblemReportApiTests.BootstrapAndLoginAsync(client);
        var projectId = await SeedProjectAsync(factory, "PKG");
        using (var configure = await client.PutAsJsonAsync($"/api/projects/{projectId}/configuration", new
        {
            expectedVersion = 1, reason = "Enable external Customer requirements",
            steps = new[]
            {
                new { catalogueEntry = "Customer", position = 1, capabilities = 0 },
                new { catalogueEntry = "System", position = 2, capabilities = 7 },
                new { catalogueEntry = "HighLevel", position = 3, capabilities = 7 },
                new { catalogueEntry = "LowLevel", position = 4, capabilities = 15 },
            },
            relationships = new[]
            {
                new { parent = "Customer", child = "System" },
                new { parent = "System", child = "HighLevel" },
                new { parent = "HighLevel", child = "LowLevel" },
            },
        })) { Assert.True(configure.IsSuccessStatusCode, await configure.Content.ReadAsStringAsync()); }
        using (var activate = await client.PostAsJsonAsync($"/api/projects/{projectId}/configuration/activate",
            new { expectedVersion = 2, reason = "Activate external Customer requirements" }))
            Assert.True(activate.IsSuccessStatusCode, await activate.Content.ReadAsStringAsync());
        Guid baselineId, importId, releaseId, identityId;

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var now = DateTimeOffset.UtcNow;
            var release = new SoftwareRelease(projectId, "9.9", isReleased: false);
            var import = new BaselineImport(projectId, "DOORS", "1", "Customer v1", now,
                "customer.reqif", Digest, 1, ImportedArtifactKinds.Requirements, "source", now, "cm", now);
            import.RecordAnalysis(now); import.RecordMapping("{}", now);
            import.NoteSourceRecordsAccountedFor(1, now); import.RecordReconciliation("{}", now);
            var identity = new SourceIdentity(projectId, import.Id, "DOORS", "Requirements", "42", "REQ-42", now);
            var membership = new BaselineImportSourceIdentityMembership(import.Id, identity.Id, true, now);
            var item = new BaselineImportPackageItem(projectId, import.Id, identity.Id, "CUSR-000001", 0,
                "The customer system shall provide navigation.", "Customer rationale", "REQ-42", now);
            var baseline = new CandidateBaseline("SW-09.90", 0, projectId, release.Id, null,
                "Customer candidate", "cm", now);
            db.AddRange(release, import, identity, membership, item, baseline);
            await db.SaveChangesAsync();
            baselineId = baseline.Id; importId = import.Id; releaseId = release.Id; identityId = identity.Id;
        }

        using var legacyAccept = await client.PostAsJsonAsync($"/api/baseline-imports/{importId}/accept", new { version = "9.9" });
        Assert.Equal(HttpStatusCode.BadRequest, legacyAccept.StatusCode);

        using var selected = await client.PostAsJsonAsync($"/api/baselines/{baselineId}/external-packages",
            new { baselineImportId = importId });
        Assert.Equal(HttpStatusCode.OK, selected.StatusCode);

        using var verifyScope = factory.Services.CreateScope();
        var verify = verifyScope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        var accepted = await verify.BaselineImports.AsNoTracking().SingleAsync(x => x.Id == importId);
        Assert.Equal(BaselineImportState.Accepted, accepted.State);
        Assert.Equal(releaseId, accepted.ReleaseId);
        Assert.Equal(baselineId, accepted.BoundCandidateBaselineId);
        Assert.Single(await verify.BaselineExternalPackageSelections.AsNoTracking()
            .Where(x => x.BaselineId == baselineId).ToListAsync());
        Assert.False(await verify.Releases.Where(x => x.Id == releaseId).Select(x => x.IsReleased).SingleAsync());

        using var lateStage = await client.PostAsJsonAsync($"/api/baseline-imports/{importId}/customer-items",
            new { items = new[] { new { sourceIdentityId = identityId, baseNumber = "CUSR-000002", revision = 0,
                statement = "A late mutation must be refused.", rationale = "", sourceIdentifier = "REQ-42" } } });
        Assert.Equal(HttpStatusCode.BadRequest, lateStage.StatusCode);
    }

    [Fact]
    public async Task External_package_binding_refuses_active_ladder_without_customer()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        await ProblemReportApiTests.BootstrapAndLoginAsync(client);
        var projectId = await SeedProjectAsync(factory, "NOC");
        using (var configure = await client.PutAsJsonAsync($"/api/projects/{projectId}/configuration", new
        {
            expectedVersion = 1, reason = "Use legacy levels only",
            steps = new[]
            {
                new { catalogueEntry = "System", position = 1, capabilities = 7 },
                new { catalogueEntry = "HighLevel", position = 2, capabilities = 7 },
                new { catalogueEntry = "LowLevel", position = 3, capabilities = 15 },
            },
            relationships = new[]
            {
                new { parent = "System", child = "HighLevel" },
                new { parent = "HighLevel", child = "LowLevel" },
            },
        })) { Assert.True(configure.IsSuccessStatusCode, await configure.Content.ReadAsStringAsync()); }
        using (var activate = await client.PostAsJsonAsync($"/api/projects/{projectId}/configuration/activate",
            new { expectedVersion = 2, reason = "Activate legacy-only ladder" }))
            Assert.True(activate.IsSuccessStatusCode, await activate.Content.ReadAsStringAsync());

        Guid baselineId, importId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var now = DateTimeOffset.UtcNow;
            var release = new SoftwareRelease(projectId, "8.8", isReleased: false);
            var import = new BaselineImport(projectId, "DOORS", "1", "Customer v1", now,
                "customer.reqif", Digest, 1, ImportedArtifactKinds.Requirements, "source", now, "cm", now);
            import.RecordAnalysis(now); import.RecordMapping("{}", now);
            import.NoteSourceRecordsAccountedFor(1, now); import.RecordReconciliation("{}", now);
            var identity = new SourceIdentity(projectId, import.Id, "DOORS", "Requirements", "43", "REQ-43", now);
            var membership = new BaselineImportSourceIdentityMembership(import.Id, identity.Id, true, now);
            var item = new BaselineImportPackageItem(projectId, import.Id, identity.Id, "CUSR-000003", 0,
                "The customer system shall refuse an unconfigured ladder.", "Customer rationale", "REQ-43", now);
            var baseline = new CandidateBaseline("SW-08.80", 0, projectId, release.Id, null,
                "Refused customer candidate", "cm", now);
            db.AddRange(release, import, identity, membership, item, baseline);
            await db.SaveChangesAsync();
            baselineId = baseline.Id; importId = import.Id;
        }

        using var refused = await client.PostAsJsonAsync($"/api/baselines/{baselineId}/external-packages",
            new { baselineImportId = importId });
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        using var verifyScope = factory.Services.CreateScope();
        var verify = verifyScope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        var importAfter = await verify.BaselineImports.AsNoTracking().SingleAsync(x => x.Id == importId);
        Assert.Equal(BaselineImportState.Reconciled, importAfter.State);
        Assert.Null(importAfter.BoundCandidateBaselineId);
        Assert.Empty(await verify.BaselineExternalPackageSelections.AsNoTracking()
            .Where(x => x.BaselineId == baselineId).ToListAsync());
    }

    [Fact]
    public async Task Materialized_external_customer_is_visible_in_requirement_baseline_and_history_reads()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        await ProblemReportApiTests.BootstrapAndLoginAsync(client);
        var projectId = await SeedProjectAsync(factory, "RDO");
        using (var configure = await client.PutAsJsonAsync($"/api/projects/{projectId}/configuration", new
        {
            expectedVersion = 1, reason = "Enable external Customer requirements",
            steps = new[]
            {
                new { catalogueEntry = "Customer", position = 1, capabilities = 0 },
                new { catalogueEntry = "System", position = 2, capabilities = 7 },
                new { catalogueEntry = "HighLevel", position = 3, capabilities = 7 },
                new { catalogueEntry = "LowLevel", position = 4, capabilities = 15 },
            },
            relationships = new[]
            {
                new { parent = "Customer", child = "System" },
                new { parent = "System", child = "HighLevel" },
                new { parent = "HighLevel", child = "LowLevel" },
            },
        })) { Assert.True(configure.IsSuccessStatusCode, await configure.Content.ReadAsStringAsync()); }
        using (var activate = await client.PostAsJsonAsync($"/api/projects/{projectId}/configuration/activate",
            new { expectedVersion = 2, reason = "Activate external Customer requirements" }))
            Assert.True(activate.IsSuccessStatusCode, await activate.Content.ReadAsStringAsync());

        Guid baselineId, importId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var now = DateTimeOffset.UtcNow;
            var release = new SoftwareRelease(projectId, "10.0", isReleased: false);
            var import = new BaselineImport(projectId, "DOORS", "1", "Customer v1", now,
                "customer.reqif", Digest, 1, ImportedArtifactKinds.Requirements, "source", now, "cm", now);
            import.RecordAnalysis(now); import.RecordMapping("{}", now); import.NoteSourceRecordsAccountedFor(1, now);
            import.RecordReconciliation("{}", now);
            var identity = new SourceIdentity(projectId, import.Id, "DOORS", "Requirements", "44", "REQ-44", now);
            var membership = new BaselineImportSourceIdentityMembership(import.Id, identity.Id, true, now);
            var item = new BaselineImportPackageItem(projectId, import.Id, identity.Id, "CUSR-000004", 0,
                "The customer system shall remain visible.", "Customer rationale", "REQ-44", now);
            var baseline = new CandidateBaseline("SW-10.00", 0, projectId, release.Id, null,
                "Customer read surface", "cm", now);
            db.AddRange(release, import, identity, membership, item, baseline);
            await db.SaveChangesAsync();
            baselineId = baseline.Id; importId = import.Id;
        }

        using var selected = await client.PostAsJsonAsync($"/api/baselines/{baselineId}/external-packages",
            new { baselineImportId = importId });
        Assert.Equal(HttpStatusCode.OK, selected.StatusCode);
        using var frozen = await client.PostAsJsonAsync($"/api/baselines/{baselineId}/freeze", new { });
        Assert.True(frozen.IsSuccessStatusCode, await frozen.Content.ReadAsStringAsync());
        using var materialized = await client.PostAsJsonAsync($"/api/baselines/{baselineId}/materialize-requirements", new { });
        Assert.Equal(HttpStatusCode.OK, materialized.StatusCode);

        Guid artifactId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            artifactId = await db.Requirements.Where(x => x.BaseNumber == "CUSR-000004").Select(x => x.Id).SingleAsync();
        }

        var listed = await client.GetFromJsonAsync<JsonElement>(
            $"/api/requirements?projectId={projectId}&baselineId={baselineId}&page=1&pageSize=50");
        var listedRow = Assert.Single(listed.GetProperty("items").EnumerateArray());
        Assert.Equal("CUSR-000004", listedRow.GetProperty("baseNumber").GetString());
        Assert.Equal("ExternalSourcePackage", listedRow.GetProperty("originKind").GetString());
        Assert.Equal(importId, listedRow.GetProperty("sourceBaselineImportId").GetGuid());
        Assert.Equal(JsonValueKind.Null, listedRow.GetProperty("sourceChangeRequestId").ValueKind);

        var history = await client.GetFromJsonAsync<JsonElement>($"/api/requirements/{artifactId}/history");
        var historyRow = Assert.Single(history.GetProperty("revisions").EnumerateArray());
        Assert.Equal("ExternalSourcePackage", historyRow.GetProperty("originKind").GetString());
        Assert.Equal(importId, historyRow.GetProperty("sourceBaselineImportId").GetGuid());
        Assert.Equal(JsonValueKind.Null, historyRow.GetProperty("sourceChangeRequestId").ValueKind);

        var detail = await client.GetFromJsonAsync<JsonElement>($"/api/enterprise-requirements/{artifactId}");
        var detailRow = Assert.Single(detail.GetProperty("history").EnumerateArray());
        Assert.Equal("ExternalSourcePackage", detailRow.GetProperty("originKind").GetString());
        Assert.Equal(importId, detailRow.GetProperty("sourceBaselineImportId").GetGuid());
        Assert.Equal(JsonValueKind.Null, detailRow.GetProperty("sourceChangeRequestId").ValueKind);

        var swrd = await client.GetFromJsonAsync<JsonElement>($"/api/baselines/{baselineId}/swrd");
        var swrdRow = Assert.Single(swrd.GetProperty("requirements").EnumerateArray());
        Assert.Equal("CUSR-000004", swrdRow.GetProperty("baseNumber").GetString());
        Assert.Equal("ExternalSourcePackage", swrdRow.GetProperty("originKind").GetString());
        Assert.Equal(importId, swrdRow.GetProperty("sourceBaselineImportId").GetGuid());
        Assert.Equal(JsonValueKind.Null, swrdRow.GetProperty("sourceChangeRequestId").ValueKind);
    }

    [Fact]
    public async Task Customer_package_rejects_source_identifier_mismatch_without_staging_a_row()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        await ProblemReportApiTests.BootstrapAndLoginAsync(client);
        var projectId = await SeedProjectAsync(factory, "BAD");
        var importId = await StartAsync(client, projectId);
        await client.PostAsync($"/api/baseline-imports/{importId}/analysis", null);
        await client.PostAsJsonAsync($"/api/baseline-imports/{importId}/mapping", new { mappingJson = "{}" });
        Assert.Equal(HttpStatusCode.OK, (await RecordSourceRecordsAsync(client, importId, SourceRecord(451))).StatusCode);
        await client.PostAsJsonAsync($"/api/baseline-imports/{importId}/reconciliation", new { reconciliationJson = "{\"objectsIn\":1}" });
        var sourceRecords = await client.GetFromJsonAsync<JsonElement>($"/api/baseline-imports/{importId}/source-records");
        var identityId = sourceRecords.GetProperty("records").EnumerateArray().Single().GetProperty("id").GetGuid();

        using var refused = await client.PostAsJsonAsync($"/api/baseline-imports/{importId}/customer-items", new
        {
            items = new[] { new { sourceIdentityId = identityId, baseNumber = "CUSR-000005", revision = 0,
                statement = "Malformed identifier must not stage.", rationale = "", sourceIdentifier = "REQ-WRONG" } }
        });
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        Assert.Empty(await db.BaselineImportPackageItems.AsNoTracking().Where(x => x.BaselineImportId == importId).ToListAsync());
    }

    [Fact]
    public async Task Source_identity_read_uses_latest_link_and_retains_later_package_provenance()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        await ProblemReportApiTests.BootstrapAndLoginAsync(client);
        var projectId = await SeedProjectAsync(factory, "LIN");
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var now = DateTimeOffset.UtcNow;
            var firstImport = new BaselineImport(projectId, "DOORS", "1", "v1", now, "v1.reqif", Digest, 1,
                ImportedArtifactKinds.Requirements, "source", now, "cm", now);
            var secondImport = new BaselineImport(projectId, "DOORS", "1", "v2", now.AddMinutes(1), "v2.reqif",
                "8f2c4b1e7a0d3c5589ab41e2f7c60d9b8e35a1470c2df6b849e0d17ac3d07a38", 1,
                ImportedArtifactKinds.Requirements, "source", now.AddMinutes(1), "cm", now.AddMinutes(1));
            var identity = new SourceIdentity(projectId, firstImport.Id, "DOORS", "Requirements", "88", "REQ-88", now);
            var firstBaseline = new CandidateBaseline("SW-03.00", 0, projectId, Guid.NewGuid(), null, "v1", "cm", now);
            var secondBaseline = new CandidateBaseline("SW-03.01", 0, projectId, Guid.NewGuid(), firstBaseline.Id, "v2", "cm", now.AddMinutes(1));
            var artifact = new RequirementArtifact(projectId, "CUSR-000011", RequirementLevel.Customer, now);
            var firstRevision = RequirementRevision.FromExternalSourcePackage(artifact.Id, 0, "First", "", RequirementRevisionState.Active,
                firstImport.Id, firstBaseline.Id, now);
            var secondRevision = RequirementRevision.FromExternalSourcePackage(artifact.Id, 1, "Second", "", RequirementRevisionState.Active,
                secondImport.Id, secondBaseline.Id, now.AddMinutes(1));
            db.AddRange(firstImport, secondImport, identity, firstBaseline, secondBaseline, artifact, firstRevision, secondRevision,
                new SourceIdentityLink(projectId, firstRevision.Id, identity.Id, firstImport.Id, now),
                new SourceIdentityLink(projectId, secondRevision.Id, identity.Id, secondImport.Id, now.AddMinutes(1)));
            await db.SaveChangesAsync();
        }

        var response = await client.GetFromJsonAsync<JsonElement>($"/api/source-identities?projectId={projectId}&search=REQ-88");
        var match = Assert.Single(response.GetProperty("matches").EnumerateArray());
        using var verifyScope = factory.Services.CreateScope();
        var verify = verifyScope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        var secondRevisionId = await verify.RequirementRevisions.Where(x => x.Revision == 1).Select(x => x.Id).SingleAsync();
        Assert.Equal(secondRevisionId, match.GetProperty("requirementRevisionId").GetGuid());
        var provenance = match.GetProperty("provenance").EnumerateArray().ToList();
        Assert.Equal(2, provenance.Count);
        Assert.Equal(secondRevisionId, provenance[0].GetProperty("requirementRevisionId").GetGuid());
    }

    /// <summary>Walks an import to Reconciled, which now means it has really been told what the extract held.</summary>
    private static async Task WalkToReconciledAsync(HttpClient client, Guid id, string mapping = "{}")
    {
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync($"/api/baseline-imports/{id}/analysis", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync($"/api/baseline-imports/{id}/mapping",
            new { mappingJson = mapping })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await RecordSourceRecordsAsync(client, id, SourceRecord(1234), SourceRecord(1235))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync($"/api/baseline-imports/{id}/reconciliation",
            new { reconciliationJson = """{"objectsIn":2,"requirementsOut":2}""" })).StatusCode);
    }

    [Fact]
    public async Task An_import_walks_its_five_gates_and_becomes_a_released_build()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        await ProblemReportApiTests.BootstrapAndLoginAsync(client);
        var projectId = await SeedProjectAsync(factory, "IMP");

        var id = await StartAsync(client, projectId);

        var started = await client.GetFromJsonAsync<JsonElement>($"/api/baseline-imports/{id}");
        Assert.Equal("Draft", started.GetProperty("state").GetString());
        Assert.Equal(Digest, started.GetProperty("extractSha256").GetString());
        // The assertion is stated by the record itself rather than left for a reader to infer.
        Assert.Contains("were not", started.GetProperty("doesNotAssert").GetString());

        await WalkToReconciledAsync(client, id, """{"modules":{"FMS_System_Requirements":"System"}}""");

        using var accepted = await client.PostAsJsonAsync($"/api/baseline-imports/{id}/accept", new { version = "1.0" });
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        var detail = await accepted.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Accepted", detail.GetProperty("state").GetString());
        Assert.Equal("admin", detail.GetProperty("acceptedBy").GetString());

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        var releaseId = detail.GetProperty("releaseId").GetGuid();
        var release = await db.Releases.AsNoTracking().SingleAsync(x => x.Id == releaseId);

        // Released on arrival: readiness gates evaluate a build before release, and this one is already past
        // that. Its prior decisions belong to the source's own release, not to anything done here.
        Assert.Equal("1.0", release.Version);
        Assert.True(release.IsReleased);
        Assert.NotNull(release.ReleasedAt);

        // And the build is externally sourced because an accepted import points at it — the fact is derived
        // from the provenance rather than duplicated into a flag that could drift away from it.
        Assert.True(await db.BaselineImports.AsNoTracking()
            .AnyAsync(x => x.ReleaseId == releaseId && x.State == BaselineImportState.Accepted));
    }

    [Fact]
    public async Task No_gate_can_be_skipped()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        await ProblemReportApiTests.BootstrapAndLoginAsync(client);
        var projectId = await SeedProjectAsync(factory, "GAT");
        var id = await StartAsync(client, projectId);

        // Straight to accept, from Draft.
        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.PostAsJsonAsync($"/api/baseline-imports/{id}/accept", new { version = "1.0" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(
            $"/api/baseline-imports/{id}/reconciliation", new { reconciliationJson = "{}" })).StatusCode);

        await client.PostAsync($"/api/baseline-imports/{id}/analysis", null);
        // Analysed, but nothing has been mapped, so there is nothing to reconcile.
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(
            $"/api/baseline-imports/{id}/reconciliation", new { reconciliationJson = "{}" })).StatusCode);

        await client.PostAsJsonAsync($"/api/baseline-imports/{id}/mapping", new { mappingJson = "{}" });
        // Mapped, but the import has not been told what the extract held. Reconciling "every object is
        // accounted for" against no objects is vacuously true, and would produce an empty build asserting a
        // program was brought in from elsewhere.
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(
            $"/api/baseline-imports/{id}/reconciliation", new { reconciliationJson = """{"objectsIn":0}""" })).StatusCode);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        // Nothing partial was written along the way.
        Assert.Empty(await db.Releases.AsNoTracking().Where(x => x.ProjectId == projectId).ToListAsync());
    }

    [Fact]
    public async Task Changing_the_mapping_makes_the_import_unacceptable_again()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        await ProblemReportApiTests.BootstrapAndLoginAsync(client);
        var projectId = await SeedProjectAsync(factory, "MAP");
        var id = await StartAsync(client, projectId);

        await WalkToReconciledAsync(client, id, """{"v":1}""");

        // Remapping discards the reconciliation, because those counts described the old mapping. Accepting
        // against them would be accepting something other than what the import would now do.
        await client.PostAsJsonAsync($"/api/baseline-imports/{id}/mapping", new { mappingJson = """{"v":2}""" });

        var afterRemap = await client.GetFromJsonAsync<JsonElement>($"/api/baseline-imports/{id}");
        Assert.Equal("Mapped", afterRemap.GetProperty("state").GetString());
        Assert.Equal("", afterRemap.GetProperty("reconciliationJson").GetString());
        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.PostAsJsonAsync($"/api/baseline-imports/{id}/accept", new { version = "1.0" })).StatusCode);
    }

    [Fact]
    public async Task Provenance_that_could_not_be_checked_later_is_refused_at_the_door()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        await ProblemReportApiTests.BootstrapAndLoginAsync(client);
        var projectId = await SeedProjectAsync(factory, "PRV");

        using var noHash = await client.PostAsJsonAsync("/api/baseline-imports", new
        {
            projectId, sourceSystem = "DOORS", sourceSystemVersion = "9.6", sourceBaselineName = "v4.2",
            sourceBaselineDate = DateTimeOffset.UtcNow, extractFileName = "x.reqifz",
            extractSha256 = "not-a-digest", extractSizeBytes = 10L, carries = new[] { "Requirements" },
            extractedBy = "m.chen", extractedAt = DateTimeOffset.UtcNow
        });
        Assert.Equal(HttpStatusCode.BadRequest, noHash.StatusCode);

        using var noKind = await client.PostAsJsonAsync("/api/baseline-imports", StartBody(projectId, []));
        Assert.Equal(HttpStatusCode.BadRequest, noKind.StatusCode);

        using var unknownKind = await client.PostAsJsonAsync("/api/baseline-imports", StartBody(projectId, ["Drawings"]));
        Assert.Equal(HttpStatusCode.BadRequest, unknownKind.StatusCode);
    }

    [Fact]
    public async Task An_import_declares_carrying_requirements_and_test_procedures_separately()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        await ProblemReportApiTests.BootstrapAndLoginAsync(client);
        var projectId = await SeedProjectAsync(factory, "KND");

        using var created = await client.PostAsJsonAsync("/api/baseline-imports",
            StartBody(projectId, ["Requirements", "TestProcedures"]));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var carries = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("carries").GetString();
        Assert.Contains("Requirements", carries);
        Assert.Contains("TestProcedures", carries);
    }

    [Fact]
    public async Task Accepting_twice_or_onto_an_existing_build_is_refused()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        await ProblemReportApiTests.BootstrapAndLoginAsync(client);
        var projectId = await SeedProjectAsync(factory, "TWC");
        var id = await StartAsync(client, projectId);
        await WalkToReconciledAsync(client, id);
        Assert.Equal(HttpStatusCode.OK,
            (await client.PostAsJsonAsync($"/api/baseline-imports/{id}/accept", new { version = "1.0" })).StatusCode);

        // An accepted import is immutable: its baseline exists.
        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.PostAsJsonAsync($"/api/baseline-imports/{id}/accept", new { version = "1.1" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync($"/api/baseline-imports/{id}/abandon", null)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await RecordSourceRecordsAsync(client, id, SourceRecord(1236))).StatusCode);

        var second = await StartAsync(client, projectId);
        await WalkToReconciledAsync(client, second);
        using var collision = await client.PostAsJsonAsync($"/api/baseline-imports/{second}/accept", new { version = "1.0" });
        Assert.Equal(HttpStatusCode.Conflict, collision.StatusCode);
    }

    [Fact]
    public async Task An_import_records_what_the_extract_held_and_a_re_extract_is_a_delta()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        await ProblemReportApiTests.BootstrapAndLoginAsync(client);
        var projectId = await SeedProjectAsync(factory, "DLT");
        var id = await StartAsync(client, projectId);
        await client.PostAsync($"/api/baseline-imports/{id}/analysis", null);
        await client.PostAsJsonAsync($"/api/baseline-imports/{id}/mapping", new { mappingJson = "{}" });

        using var first = await RecordSourceRecordsAsync(client, id,
            SourceRecord(1233, inImportedBaseline: false), SourceRecord(1234), SourceRecord(1235));
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var firstBody = await first.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(3, firstBody.GetProperty("recorded").GetInt32());
        Assert.Equal(0, firstBody.GetProperty("seenAgain").GetInt32());

        // A later extract of the same program. The same objects, not a second set of them — that is what the
        // source's own stable key is for, and it holds even when the identifier text was edited in between.
        using var again = await RecordSourceRecordsAsync(client, id, SourceRecord(1234), SourceRecord(1235), SourceRecord(1236));
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        var againBody = await again.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, againBody.GetProperty("recorded").GetInt32());
        Assert.Equal(2, againBody.GetProperty("seenAgain").GetInt32());
        // Everything in the payload was accounted for, whether new here or already known — which is what the
        // Reconcile gate needs. Counting rows this import created would have said one.
        Assert.Equal(3, againBody.GetProperty("accountedFor").GetInt32());

        var detail = await client.GetFromJsonAsync<JsonElement>($"/api/baseline-imports/{id}");
        Assert.Equal(3, detail.GetProperty("sourceRecordCount").GetInt32());
        // Four identities exist: three in the baseline and one the source retired before it.
        Assert.Equal(4, detail.GetProperty("sourceIdentityCount").GetInt32());
        Assert.Equal(3, detail.GetProperty("sourceRecords").GetProperty("inImportedBaseline").GetInt32());
        Assert.Equal(1, detail.GetProperty("sourceRecords").GetProperty("historyOnly").GetInt32());
    }

    [Fact]
    public async Task Two_objects_claiming_the_same_source_identity_are_refused()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        await ProblemReportApiTests.BootstrapAndLoginAsync(client);
        var projectId = await SeedProjectAsync(factory, "DUP");
        var id = await StartAsync(client, projectId);
        await client.PostAsync($"/api/baseline-imports/{id}/analysis", null);
        await client.PostAsJsonAsync($"/api/baseline-imports/{id}/mapping", new { mappingJson = "{}" });

        // Refused outright rather than reported at Reconcile as a gap somebody could accept: there is no
        // mapping decision that makes two objects with one key safe, because a later extract cannot tell
        // them apart, and the delta rule would silently merge them.
        using var refused = await RecordSourceRecordsAsync(client, id, SourceRecord(1234), SourceRecord(1234));
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Contains("cannot be told apart", await refused.Content.ReadAsStringAsync());

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        // Nothing was half-written: a refused payload leaves the import exactly as it was.
        Assert.Empty(await db.SourceIdentities.AsNoTracking().Where(x => x.BaselineImportId == id).ToListAsync());
    }

    [Fact]
    public async Task Source_history_is_recorded_as_reported_and_never_becomes_a_revision()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        await ProblemReportApiTests.BootstrapAndLoginAsync(client);
        var projectId = await SeedProjectAsync(factory, "HST");
        var id = await StartAsync(client, projectId);
        await client.PostAsync($"/api/baseline-imports/{id}/analysis", null);
        await client.PostAsJsonAsync($"/api/baseline-imports/{id}/mapping", new { mappingJson = "{}" });

        Assert.Equal(HttpStatusCode.OK, (await RecordSourceRecordsAsync(client, id, SourceRecord(1234, history:
        [
            new { sourceBaselineName = "V0.8", statement = "", changedBy = "", changedAt = (DateTimeOffset?)null, sourceChangeReference = "" },
            new
            {
                sourceBaselineName = "V0.9",
                statement = "The FMS shall annunciate a navigation source disagreement.",
                changedBy = "a.okafor",
                changedAt = (DateTimeOffset?)new DateTimeOffset(2025, 1, 22, 0, 0, 0, TimeSpan.Zero),
                sourceChangeReference = "DOORS CR-1402"
            }
        ]))).StatusCode);

        var records = await client.GetFromJsonAsync<JsonElement>($"/api/baseline-imports/{id}/source-records");
        // The total is reported alongside the page: a capped list that does not say so reads as the whole
        // set, which on this endpoint means reading a partial import as a complete one.
        Assert.Equal(1, records.GetProperty("total").GetInt32());
        Assert.Equal(1, records.GetProperty("returned").GetInt32());
        var record = Assert.Single(records.GetProperty("records").EnumerateArray());
        var history = record.GetProperty("sourceHistory").EnumerateArray().ToList();
        Assert.Equal(2, history.Count);
        // A source that recorded no author, date or statement is described as it was found. Nothing
        // downstream reasons over any of it, which is exactly what makes recording it honestly safe.
        Assert.Equal("V0.8", history[0].GetProperty("sourceBaselineName").GetString());
        Assert.Equal("", history[0].GetProperty("changedBy").GetString());
        Assert.Equal(JsonValueKind.Null, history[0].GetProperty("changedAt").ValueKind);
        Assert.Equal("DOORS CR-1402", history[1].GetProperty("sourceChangeReference").GetString());

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        // History is held against the source identity, never as requirement revisions. A revision here binds
        // a change request and a materialized baseline; importing V0.8 as one would mean fabricating both.
        Assert.Equal(2, await db.SourceHistoryEntries.AsNoTracking().CountAsync(x => x.BaselineImportId == id));
        Assert.Empty(await db.RequirementRevisions.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task Recording_more_of_the_extract_makes_the_import_unacceptable_again()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        await ProblemReportApiTests.BootstrapAndLoginAsync(client);
        var projectId = await SeedProjectAsync(factory, "AGN");
        var id = await StartAsync(client, projectId);
        await WalkToReconciledAsync(client, id);

        Assert.Equal(HttpStatusCode.OK, (await RecordSourceRecordsAsync(client, id, SourceRecord(9001))).StatusCode);

        // The reconciliation described a different set of objects. Accepting against it would be accepting
        // counts that no longer say what this import would do.
        var afterMore = await client.GetFromJsonAsync<JsonElement>($"/api/baseline-imports/{id}");
        Assert.Equal("Mapped", afterMore.GetProperty("state").GetString());
        Assert.Equal("", afterMore.GetProperty("reconciliationJson").GetString());
        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.PostAsJsonAsync($"/api/baseline-imports/{id}/accept", new { version = "1.0" })).StatusCode);
    }

    [Fact]
    public async Task Abandoning_an_import_leaves_nothing_behind_for_the_next_attempt()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        await ProblemReportApiTests.BootstrapAndLoginAsync(client);
        var projectId = await SeedProjectAsync(factory, "RTY");

        // Getting a program in usually takes more than one attempt: import, find the mapping wrong, abandon,
        // re-extract, try again. Only the last one is ever accepted.
        var first = await StartAsync(client, projectId);
        await client.PostAsync($"/api/baseline-imports/{first}/analysis", null);
        await client.PostAsJsonAsync($"/api/baseline-imports/{first}/mapping", new { mappingJson = """{"wrong":true}""" });
        Assert.Equal(HttpStatusCode.OK, (await RecordSourceRecordsAsync(client, first,
            SourceRecord(1234, history: [new { sourceBaselineName = "V0.9", statement = "", changedBy = "", changedAt = (DateTimeOffset?)null, sourceChangeReference = "" }]),
            SourceRecord(1235))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync($"/api/baseline-imports/{first}/abandon", null)).StatusCode);

        var second = await StartAsync(client, projectId);
        await client.PostAsync($"/api/baseline-imports/{second}/analysis", null);
        await client.PostAsJsonAsync($"/api/baseline-imports/{second}/mapping", new { mappingJson = """{"right":true}""" });
        using var again = await RecordSourceRecordsAsync(client, second, SourceRecord(1234), SourceRecord(1235));
        var body = await again.Content.ReadFromJsonAsync<JsonElement>();

        // An abandoned import committed nothing, so the retry records these objects rather than finding them
        // already taken. Otherwise the accepted import would own no source records at all, and every count
        // and listing on its page would describe the attempt that was thrown away.
        Assert.Equal(2, body.GetProperty("recorded").GetInt32());
        Assert.Equal(0, body.GetProperty("seenAgain").GetInt32());

        await client.PostAsJsonAsync($"/api/baseline-imports/{second}/reconciliation",
            new { reconciliationJson = """{"objectsIn":2}""" });
        Assert.Equal(HttpStatusCode.OK,
            (await client.PostAsJsonAsync($"/api/baseline-imports/{second}/accept", new { version = "1.0" })).StatusCode);

        var detail = await client.GetFromJsonAsync<JsonElement>($"/api/baseline-imports/{second}");
        Assert.Equal(2, detail.GetProperty("sourceIdentityCount").GetInt32());
        Assert.Equal(2, detail.GetProperty("sourceRecords").GetProperty("inImportedBaseline").GetInt32());

        var records = await client.GetFromJsonAsync<JsonElement>($"/api/baseline-imports/{second}/source-records");
        Assert.Equal(2, records.GetProperty("total").GetInt32());

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        // The abandoned attempt's history went with it. It described an extract nobody accepted.
        Assert.Empty(await db.SourceIdentities.AsNoTracking().Where(x => x.BaselineImportId == first).ToListAsync());
        Assert.Empty(await db.SourceHistoryEntries.AsNoTracking().Where(x => x.BaselineImportId == first).ToListAsync());
    }

    [Fact]
    public async Task Abandoning_first_import_does_not_delete_identity_observed_by_later_import()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        await ProblemReportApiTests.BootstrapAndLoginAsync(client);
        var projectId = await SeedProjectAsync(factory, "MEM");
        var first = await StartAsync(client, projectId);
        await client.PostAsync($"/api/baseline-imports/{first}/analysis", null);
        await client.PostAsJsonAsync($"/api/baseline-imports/{first}/mapping", new { mappingJson = "{}" });
        Assert.Equal(HttpStatusCode.OK, (await RecordSourceRecordsAsync(client, first, SourceRecord(7890))).StatusCode);

        var second = await StartAsync(client, projectId);
        await client.PostAsync($"/api/baseline-imports/{second}/analysis", null);
        await client.PostAsJsonAsync($"/api/baseline-imports/{second}/mapping", new { mappingJson = "{}" });
        var secondRecords = await RecordSourceRecordsAsync(client, second, SourceRecord(7890));
        var secondBody = await secondRecords.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(HttpStatusCode.OK, secondRecords.StatusCode);
        Assert.Equal(1, secondBody.GetProperty("seenAgain").GetInt32());
        await client.PostAsJsonAsync($"/api/baseline-imports/{second}/reconciliation", new { reconciliationJson = "{\"objectsIn\":1}" });
        Assert.Equal(HttpStatusCode.OK,
            (await client.PostAsJsonAsync($"/api/baseline-imports/{second}/accept", new { version = "2.0" })).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync($"/api/baseline-imports/{first}/abandon", null)).StatusCode);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        var identity = await db.SourceIdentities.AsNoTracking().SingleAsync(x => x.SourceObjectKey == "7890");
        Assert.Equal(first, identity.BaselineImportId);
        Assert.Empty(await db.BaselineImportSourceIdentityMemberships.AsNoTracking()
            .Where(x => x.BaselineImportId == first).ToListAsync());
        Assert.True(await db.BaselineImportSourceIdentityMemberships.AsNoTracking()
            .AnyAsync(x => x.BaselineImportId == second && x.SourceIdentityId == identity.Id));
    }

    [Fact]
    public async Task A_released_build_in_the_workspace_does_not_refuse_an_import()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        await ProblemReportApiTests.BootstrapAndLoginAsync(client);
        var projectId = await SeedProjectAsync(factory, "RLS");
        Guid releasedId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var released = new SoftwareRelease(projectId, "1.5", isReleased: false);
            released.MarkReleased(DateTimeOffset.UtcNow);
            db.Releases.Add(released);
            await db.SaveChangesAsync();
            releasedId = released.Id;
        }

        client.DefaultRequestHeaders.Add("X-AeroLink-Build-Context", releasedId.ToString());

        // The released-build refusal stops a released build being edited. An import creates a new build from
        // a source that is already released, so refusing it would answer a question nobody asked — and it
        // would, because "/api/baseline" is loose enough to catch "/api/baselines" and so catches
        // "/api/baseline-imports" with it.
        var id = await StartAsync(client, projectId);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync($"/api/baseline-imports/{id}/analysis", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await RecordSourceRecordsAsync(client, id, SourceRecord(1234))).StatusCode);

        // The refusal still holds for everything it was written for.
        using var refused = await client.PostAsJsonAsync($"/api/baselines?projectId={projectId}&releaseId={releasedId}",
            new { name = "Attempted baseline" });
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Contains("released_build_read_only", await refused.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Porting_a_program_in_takes_Program_authority_not_engineering_authority()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        var now = DateTimeOffset.UtcNow;
        Guid projectId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var program = new ProgramRecord("Import Authority Program", "IAU");
            var project = new ProjectRecord(program.Id, "Flight Software", "Import Authority Software");
            db.AddRange(program, project);
            foreach (var (userName, role) in new[]
                     {
                         ("import.engineer", ProgramRole.Engineer),
                         ("import.cm", ProgramRole.ConfigurationManager),
                     })
            {
                var account = new UserAccount(userName, userName, $"{userName}@example.test",
                    IdentityService.HashPassword(AeroLinkApiFactory.MemberPassword), now);
                db.Add(account);
                db.Add(new ProgramMembership(account.Id, program.Id, role, "test.setup", now));
            }
            await db.SaveChangesAsync();
            projectId = project.Id;
        }

        // An engineer has every right to work inside this Program. Declaring that a whole baseline arrived
        // from somewhere else, already released, is not that kind of act — it is Program setup, so it takes
        // the authority that establishes a Project.
        await SignInAsync(client, "import.engineer");
        using var refused = await client.PostAsJsonAsync("/api/baseline-imports", StartBody(projectId));
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);

        await SignInAsync(client, "import.cm");
        using var allowed = await client.PostAsJsonAsync("/api/baseline-imports", StartBody(projectId));
        Assert.Equal(HttpStatusCode.Created, allowed.StatusCode);
        var id = (await allowed.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        // The same holds for every later gate, not only for starting one.
        await SignInAsync(client, "import.engineer");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsync($"/api/baseline-imports/{id}/analysis", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await RecordSourceRecordsAsync(client, id, SourceRecord(1234))).StatusCode);
        // Reading is not the same as asserting: anyone in the Program can see where a requirement came from.
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/baseline-imports/{id}")).StatusCode);
    }

    private static async Task SignInAsync(HttpClient client, string userName)
    {
        using var login = await client.PostAsJsonAsync("/api/auth/login",
            new { userName, password = AeroLinkApiFactory.MemberPassword });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        await SecurityBoundaryTests.AuthorizeMutationsAsync(client);
    }

    [Fact]
    public async Task A_source_identifier_retired_before_the_imported_baseline_still_answers()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        await ProblemReportApiTests.BootstrapAndLoginAsync(client);
        var projectId = await SeedProjectAsync(factory, "SRC");
        var id = await StartAsync(client, projectId);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var now = DateTimeOffset.UtcNow;
            var current = new SourceIdentity(projectId, id, "IBM Rational DOORS",
                "FMS_System_Requirements", "1234", "SYS-01234", now);
            var retired = SourceIdentity.FromHistoryOnly(projectId, id, "IBM Rational DOORS",
                "FMS_System_Requirements", "1233", "SYS-01233", now);
            db.AddRange(current, retired);
            db.Add(new SourceHistoryEntry(projectId, current.Id, id, "V0.9",
                "The FMS shall annunciate a navigation source disagreement.", "a.okafor",
                new DateTimeOffset(2025, 1, 22, 0, 0, 0, TimeSpan.Zero), "DOORS CR-1402"));
            await db.SaveChangesAsync();
        }

        // Somebody holding a drawing that cites a retired identifier gets an answer, not an empty result.
        var retiredHit = await client.GetFromJsonAsync<JsonElement>(
            $"/api/source-identities?projectId={projectId}&search=SYS-01233");
        Assert.Equal(1, retiredHit.GetProperty("total").GetInt32());
        var row = Assert.Single(retiredHit.GetProperty("matches").EnumerateArray());
        Assert.Equal("SYS-01233", row.GetProperty("sourceIdentifier").GetString());
        // It joins nothing: history is narrative, not nodes, so it can never be a dangling reference.
        Assert.False(row.GetProperty("inImportedBaseline").GetBoolean());
        Assert.Equal(JsonValueKind.Null, row.GetProperty("requirementRevisionId").ValueKind);

        var currentHit = await client.GetFromJsonAsync<JsonElement>(
            $"/api/source-identities?projectId={projectId}&search=SYS-01234");
        var live = Assert.Single(currentHit.GetProperty("matches").EnumerateArray());
        Assert.True(live.GetProperty("inImportedBaseline").GetBoolean());
        // Source history is reported as found, attributed to the source system, and claimed by nobody here.
        var history = Assert.Single(live.GetProperty("sourceHistory").EnumerateArray());
        Assert.Equal("V0.9", history.GetProperty("sourceBaselineName").GetString());
        Assert.Equal("DOORS CR-1402", history.GetProperty("sourceChangeReference").GetString());
    }
}
