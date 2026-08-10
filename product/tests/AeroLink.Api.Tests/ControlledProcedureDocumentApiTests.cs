using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AeroLink.Domain.Baselines;
using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Identity;
using AeroLink.Domain.Programs;
using AeroLink.Domain.Requirements;
using AeroLink.Domain.Traceability;
using AeroLink.Domain.Verification;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AeroLink.Api.Tests;

/// <summary>
/// #419 — a controlled test-procedure document is one exact immutable procedure manifest, never a
/// compatibility projection that can silently change after materialization.
/// </summary>
public sealed class ControlledProcedureDocumentApiTests
{
    private sealed record Fixture(Guid ProjectId, Guid ReleaseId, Guid BaselineId, Guid TcrId);

    private static async Task<Fixture> SeedAsync(AeroLinkApiFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        var now = DateTimeOffset.UtcNow;

        var program = new ProgramRecord("Controlled Procedure Documents", "CPD");
        var project = new ProjectRecord(program.Id, "Software", "Controlled Procedure Document Software");
        var release = new SoftwareRelease(project.Id, "1.6", false);
        db.AddRange(program, project, release);

        var scr = new SystemChangeRequest("SRCR-00940", 0, project.Id, release.Id, "Oceanic", "P", "A", "S",
            "author", now);
        scr.AddRequirementChange("author", "SYSR-00000941", 0, RequirementLevel.System,
            RequirementChangeKind.Introduce, "The FMS shall sequence oceanic waypoints.", "New capability",
            "Test", now);
        scr.SubmitForReview("author", [new ApproverSelection("reviewer", "Reviewer")], now);
        scr.ApproveActiveStage("reviewer", now);
        var baseline = new CandidateBaseline("SW-00.10", 0, project.Id, release.Id, null,
            "Controlled document baseline", "cm", now);
        baseline.Select(scr, "cm", now);
        baseline.Freeze("cm", now);
        db.AddRange(scr, baseline);

        var carrying = new TestChangeReview(project.Id, release.Id, scr.Id,
            TestChangeReviewDiscipline.System, scr.DisplayNumber, now);
        carrying.RecordTestChangeRequired("verification.engineer", now);
        carrying.AssignControlledNumber("SYSTCR-000941", now);
        db.Add(carrying);

        foreach (var (user, role) in new[]
                 {
                     ("baseline.cm", ProgramRole.ConfigurationManager),
                     ("baseline.verifier", ProgramRole.TestEngineer),
                 })
        {
            var account = new UserAccount(user, user, $"{user}@example.test",
                IdentityService.HashPassword(AeroLinkApiFactory.MemberPassword), now);
            db.Add(account);
            db.Add(new ProgramMembership(account.Id, program.Id, role, "test.setup", now));
        }
        await db.SaveChangesAsync();
        return new(project.Id, release.Id, baseline.Id, carrying.Id);
    }

    private static async Task LoginAsync(HttpClient client, string user)
    {
        using var login = await client.PostAsJsonAsync("/api/auth/login",
            new { userName = user, password = AeroLinkApiFactory.MemberPassword });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        await SecurityBoundaryTests.AuthorizeMutationsAsync(client);
    }

    private static async Task MaterializeRequirementsAsync(HttpClient client, Guid baselineId)
    {
        using var response = await client.PostAsJsonAsync($"/api/baselines/{baselineId}/materialize-requirements", new { });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static async Task PrepareCarryingPackageAsync(AeroLinkApiFactory factory, Fixture fixture)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        var now = DateTimeOffset.UtcNow;
        var review = await db.TestChangeReviews.Include(x => x.ProcedureChanges)
            .SingleAsync(x => x.Id == fixture.TcrId);
        var request = await db.SystemChangeRequests.Include(x => x.RequirementChanges)
            .SingleAsync(x => x.Id == review.ChangeRequestId);
        var revision = await db.RequirementRevisions.SingleAsync(x => x.SourceChangeRequestId == request.Id);
        var item = VerificationImpactItem.ForIntroducedRequirement(fixture.ProjectId, fixture.ReleaseId,
            request.Id, review.Id, request.RequirementChanges.Single().Id,
            request.RequirementChanges.Single().DisplayNumber, "Test", now);
        item.LinkRequirementRevision(revision.Id, now);
        review.AddProcedureChange("verification.engineer", new TestProcedureChangeDraft("SYSTP-000941", 0,
            TestProcedureLevel.System, TestProcedureChangeKind.Introduce, "Oceanic waypoint sequencing",
            "Verify oceanic sequencing.", "Cruise.", "1. Load. 2. Read.", "Sequenced.",
            "Nothing covers oceanic sequencing.", JsonSerializer.Serialize(new[] { revision.Id })), now);
        review.WriteCase("verification.engineer", "Verification case", "Problem", "Analysis", "Solution", now);
        review.Submit("verification.engineer", "test.lead", true, now);
        await db.SaveChangesAsync();
        review.ApproveActiveStage("test.lead", "Reviewed.", now);
        db.Add(item);
        await db.SaveChangesAsync();
    }

    private static async Task<string> ExpectedContentHashAsync(AeroLinkApiFactory factory, string manifestHash,
        ControlledDocumentType type, int count, string user)
    {
        var content = $"{manifestHash}|{type}|{count}|{user}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant();
    }

    private static async Task<string> DocumentXmlAsync(HttpClient client, Guid documentId)
    {
        using var download = await client.GetAsync($"/api/documents/{documentId}/download?format=docx");
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        var bytes = await download.Content.ReadAsByteArrayAsync();
        using var archive = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
        var part = archive.GetEntry("word/document.xml");
        Assert.NotNull(part);
        using var reader = new StreamReader(part!.Open());
        return await reader.ReadToEndAsync();
    }

    [Fact]
    public async Task Procedure_documents_cannot_be_generated_before_the_exact_procedure_manifest_exists()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        var fixture = await SeedAsync(factory);
        await LoginAsync(client, "baseline.cm");
        await MaterializeRequirementsAsync(client, fixture.BaselineId);

        using var generated = await client.PostAsJsonAsync($"/api/baselines/{fixture.BaselineId}/generate-documents", new { });
        Assert.Equal(HttpStatusCode.OK, generated.StatusCode);
        var body = JsonSerializer.Deserialize<JsonElement>(await generated.Content.ReadAsStringAsync());
        Assert.Equal(3, body.GetProperty("generated").GetInt32());
        var skipped = body.GetProperty("skipped").EnumerateArray()
            .Select(x => x.GetString()).ToList();
        Assert.Contains("SystemTestProcedures", skipped);
        Assert.Contains("HighLevelTestProcedures", skipped);
        Assert.Contains("LowLevelTestProcedures", skipped);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        var documents = await db.ControlledDocuments.AsNoTracking()
            .Where(x => x.BaselineId == fixture.BaselineId).ToListAsync();
        Assert.Equal(3, documents.Count);
        Assert.DoesNotContain(documents, x => x.Type == ControlledDocumentType.SystemTestProcedures);
        Assert.DoesNotContain(documents, x => x.Type == ControlledDocumentType.HighLevelTestProcedures);
        Assert.DoesNotContain(documents, x => x.Type == ControlledDocumentType.LowLevelTestProcedures);
    }

    [Fact]
    public async Task Procedure_documents_are_bound_to_the_exact_manifest_after_materialization()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        var fixture = await SeedAsync(factory);
        await LoginAsync(client, "baseline.cm");
        await MaterializeRequirementsAsync(client, fixture.BaselineId);
        await PrepareCarryingPackageAsync(factory, fixture);

        using var selected = await client.PostAsJsonAsync($"/api/baselines/{fixture.BaselineId}/test-change-requests",
            new { testChangeRequestId = fixture.TcrId });
        Assert.True(selected.StatusCode == HttpStatusCode.OK, await selected.Content.ReadAsStringAsync());
        using var materialized = await client.PostAsJsonAsync(
            $"/api/baselines/{fixture.BaselineId}/materialize-test-procedures", new { });
        Assert.Equal(HttpStatusCode.OK, materialized.StatusCode);
        var materialization = JsonSerializer.Deserialize<JsonElement>(await materialized.Content.ReadAsStringAsync());
        var proceduresHash = materialization.GetProperty("proceduresHash").GetString()!;

        using var generated = await client.PostAsJsonAsync($"/api/baselines/{fixture.BaselineId}/generate-documents", new { });
        Assert.Equal(HttpStatusCode.OK, generated.StatusCode);
        var body = JsonSerializer.Deserialize<JsonElement>(await generated.Content.ReadAsStringAsync());
        Assert.Equal(6, body.GetProperty("generated").GetInt32());
        Assert.Empty(body.GetProperty("skipped").EnumerateArray().ToList());

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        var systemDocument = await db.ControlledDocuments.AsNoTracking()
            .SingleAsync(x => x.BaselineId == fixture.BaselineId
                              && x.Type == ControlledDocumentType.SystemTestProcedures);
        Assert.Equal(1, systemDocument.ArtifactCount);
        Assert.Equal(await ExpectedContentHashAsync(factory, proceduresHash,
            ControlledDocumentType.SystemTestProcedures, 1, "baseline.cm"), systemDocument.ContentHash);
        Assert.NotEqual((await db.CandidateBaselines.AsNoTracking()
            .SingleAsync(x => x.Id == fixture.BaselineId)).RequirementsHash, systemDocument.ContentHash);
    }

    [Fact]
    public async Task A_procedure_document_renders_the_same_records_after_unrelated_later_activity()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        var fixture = await SeedAsync(factory);
        await LoginAsync(client, "baseline.cm");
        await MaterializeRequirementsAsync(client, fixture.BaselineId);
        await PrepareCarryingPackageAsync(factory, fixture);
        using var selected = await client.PostAsJsonAsync($"/api/baselines/{fixture.BaselineId}/test-change-requests",
            new { testChangeRequestId = fixture.TcrId });
        Assert.True(selected.StatusCode == HttpStatusCode.OK, await selected.Content.ReadAsStringAsync());
        using var materialized = await client.PostAsJsonAsync(
            $"/api/baselines/{fixture.BaselineId}/materialize-test-procedures", new { });
        Assert.Equal(HttpStatusCode.OK, materialized.StatusCode);
        using var generated = await client.PostAsJsonAsync($"/api/baselines/{fixture.BaselineId}/generate-documents", new { });
        Assert.Equal(HttpStatusCode.OK, generated.StatusCode);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        var systemDocument = await db.ControlledDocuments.AsNoTracking()
            .SingleAsync(x => x.BaselineId == fixture.BaselineId
                              && x.Type == ControlledDocumentType.SystemTestProcedures);

        var firstXml = await DocumentXmlAsync(client, systemDocument.Id);
        Assert.Contains("SYSTP-000941.00", firstXml);

        // Unrelated later activity: an approved procedure revision that is NOT in this baseline's manifest.
        var now = DateTimeOffset.UtcNow;
        var unrelated = new TestProcedure(fixture.ProjectId, "SYSTP-009999", "Approved but uncarried",
            "verification.engineer", now, TestProcedureLevel.System);
        db.TestProcedures.Add(unrelated);
        db.TestProcedureRevisions.Add(new TestProcedureRevision(unrelated.Id, 0, "Uncarried objective",
            "Preconditions", "Steps", "Expected", TestProcedureState.Approved, "verification.engineer", now,
            effectiveBaselineId: fixture.BaselineId));
        await db.SaveChangesAsync();

        var secondXml = await DocumentXmlAsync(client, systemDocument.Id);
        Assert.Equal(firstXml, secondXml);
        Assert.DoesNotContain("SYSTP-009999", secondXml);
    }

    private static TestProcedureChangeDraft Draft(string baseNumber, int revision,
        TestProcedureChangeKind kind, string title, Guid? drivingRequirementRevisionId = null)
    {
        var drivingJson = drivingRequirementRevisionId is null
            ? "[]"
            : JsonSerializer.Serialize(new[] { drivingRequirementRevisionId.Value });
        return new TestProcedureChangeDraft(baseNumber, revision, TestProcedureLevel.System, kind, title,
            "Verify the exact behavior.", "The configuration is available.", "1. Load. 2. Exercise.",
            "The expected behavior is observed.", "Controlled procedure work.", drivingJson);
    }

    [Fact]
    public async Task Introduce_modify_and_retire_procedure_documents_publish_only_exact_carried_revisions()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        var fixture = await SeedAsync(factory);
        await LoginAsync(client, "baseline.cm");
        await MaterializeRequirementsAsync(client, fixture.BaselineId);

        // Baseline 1 carries two introduced procedures (.00 each) from one approved package.
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var now = DateTimeOffset.UtcNow;
            var review = await db.TestChangeReviews.Include(x => x.ProcedureChanges)
                .SingleAsync(x => x.Id == fixture.TcrId);
            var request = await db.SystemChangeRequests.Include(x => x.RequirementChanges)
                .SingleAsync(x => x.Id == review.ChangeRequestId);
            var revision = await db.RequirementRevisions.SingleAsync(x => x.SourceChangeRequestId == request.Id);
            var item = VerificationImpactItem.ForIntroducedRequirement(fixture.ProjectId, fixture.ReleaseId,
                request.Id, review.Id, request.RequirementChanges.Single().Id,
                request.RequirementChanges.Single().DisplayNumber, "Test", now);
            item.LinkRequirementRevision(revision.Id, now);
            review.AddProcedureChange("verification.engineer",
                Draft("SYSTP-000941", 0, TestProcedureChangeKind.Introduce, "Oceanic waypoint sequencing",
                    revision.Id), now);
            review.AddProcedureChange("verification.engineer",
                Draft("SYSTP-000942", 0, TestProcedureChangeKind.Introduce, "Oceanic plan display",
                    revision.Id), now);
            review.WriteCase("verification.engineer", "Two procedures", "Problem", "Analysis", "Solution", now);
            review.Submit("verification.engineer", "test.lead", true, now);
            await db.SaveChangesAsync();
            review.ApproveActiveStage("test.lead", "Reviewed.", now);
            db.Add(item);
            await db.SaveChangesAsync();
        }
        using (var selected = await client.PostAsJsonAsync($"/api/baselines/{fixture.BaselineId}/test-change-requests",
                   new { testChangeRequestId = fixture.TcrId }))
        {
            Assert.True(selected.StatusCode == HttpStatusCode.OK, await selected.Content.ReadAsStringAsync());
        }
        using (var materialized = await client.PostAsJsonAsync(
                   $"/api/baselines/{fixture.BaselineId}/materialize-test-procedures", new { }))
        {
            Assert.Equal(HttpStatusCode.OK, materialized.StatusCode);
        }
        using (var generated = await client.PostAsJsonAsync(
                   $"/api/baselines/{fixture.BaselineId}/generate-documents", new { }))
        {
            Assert.Equal(HttpStatusCode.OK, generated.StatusCode);
        }

        Guid firstDocumentId;
        (string ContentHash, int ArtifactCount, DateTimeOffset GeneratedAt) firstRecord;
        string firstXml;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var record = await db.ControlledDocuments.AsNoTracking()
                .Where(x => x.BaselineId == fixture.BaselineId
                            && x.Type == ControlledDocumentType.SystemTestProcedures)
                .SingleAsync();
        firstDocumentId = record.Id;
        firstRecord = (record.ContentHash, record.ArtifactCount, record.GeneratedAt);
        // #420: requirement documents keep their existing source-change provenance and change-authority role.
        var requirementDocumentId = await db.ControlledDocuments.AsNoTracking()
            .Where(x => x.BaselineId == fixture.BaselineId && x.Type == ControlledDocumentType.Sysrd)
            .Select(x => x.Id).SingleAsync();
        var requirementXml = await DocumentXmlAsync(client, requirementDocumentId);
        Assert.Contains("Source change request", requirementXml);
        Assert.Contains("Change Authority", requirementXml);
        }
        firstXml = await DocumentXmlAsync(client, firstDocumentId);
        Assert.Contains("SYSTP-000941.00", firstXml);
        Assert.Contains("SYSTP-000942.00", firstXml);
        // #420: the exact TCR that authorized the materialized procedure revisions is printed as the source
        // test change request, and TCR signatures are the approval authority (DEC-103).
        Assert.Contains("SYSTCR-000941.00", firstXml);
        Assert.Contains("Source test change request", firstXml);
        Assert.Contains("Test Change Authority", firstXml);
        Assert.Contains("test.lead", firstXml);
        Assert.DoesNotContain(">Change Authority<", firstXml);

        // Successor baseline 2 modifies SYSTP-000941 to .01 and retires SYSTP-000942.
        Guid secondBaselineId, secondTcrId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var now = DateTimeOffset.UtcNow;
            var release17 = new SoftwareRelease(fixture.ProjectId, "1.7", false, fixture.ReleaseId);
            var scr2 = new SystemChangeRequest("SRCR-00942", 0, fixture.ProjectId, release17.Id,
                "Oceanic plan", "P", "A", "S", "author", now);
            scr2.AddRequirementChange("author", "SYSR-00000942", 0, RequirementLevel.System,
                RequirementChangeKind.Introduce, "The FMS shall state the active plan.", "Wording", "Test", now);
            scr2.SubmitForReview("author", [new ApproverSelection("reviewer", "Reviewer")], now);
            scr2.ApproveActiveStage("reviewer", now);
            var second = new CandidateBaseline("SW-00.20", 0, fixture.ProjectId, release17.Id,
                fixture.BaselineId, "Successor baseline", "cm", now);
            second.Select(scr2, "cm", now);
            second.Freeze("cm", now);
            var tcr2 = new TestChangeReview(fixture.ProjectId, release17.Id, scr2.Id,
                TestChangeReviewDiscipline.System, scr2.DisplayNumber, now);
            tcr2.RecordTestChangeRequired("verification.engineer", now);
            tcr2.AssignControlledNumber("SYSTCR-000942", now);
            tcr2.AddProcedureChange("verification.engineer",
                Draft("SYSTP-000941", 1, TestProcedureChangeKind.Modify,
                    "Oceanic waypoint sequencing, clarified"), now);
            tcr2.AddProcedureChange("verification.engineer",
                Draft("SYSTP-000942", 1, TestProcedureChangeKind.Retire, ""), now);
            tcr2.WriteCase("verification.engineer", "Successor package", "Problem", "Analysis", "Solution", now);
            tcr2.Submit("verification.engineer", "test.lead", true, now);
            await db.SaveChangesAsync();
            tcr2.ApproveActiveStage("test.lead", "Reviewed.", now);
            db.AddRange(release17, scr2, second, tcr2);
            await db.SaveChangesAsync();
            secondBaselineId = second.Id;
            secondTcrId = tcr2.Id;
        }
        using (var requirements = await client.PostAsJsonAsync(
                   $"/api/baselines/{secondBaselineId}/materialize-requirements", new { }))
        {
            Assert.True(requirements.StatusCode == HttpStatusCode.OK,
                $"{(int)requirements.StatusCode}: {await requirements.Content.ReadAsStringAsync()}");
        }
        using (var selected = await client.PostAsJsonAsync($"/api/baselines/{secondBaselineId}/test-change-requests",
                   new { testChangeRequestId = secondTcrId }))
        {
            Assert.True(selected.StatusCode == HttpStatusCode.OK, await selected.Content.ReadAsStringAsync());
        }
        using (var materialized = await client.PostAsJsonAsync(
                   $"/api/baselines/{secondBaselineId}/materialize-test-procedures", new { }))
        {
            Assert.Equal(HttpStatusCode.OK, materialized.StatusCode);
        }
        using (var generated = await client.PostAsJsonAsync(
                   $"/api/baselines/{secondBaselineId}/generate-documents", new { }))
        {
            Assert.Equal(HttpStatusCode.OK, generated.StatusCode);
        }

        Guid secondDocumentId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            secondDocumentId = await db.ControlledDocuments.AsNoTracking()
                .Where(x => x.BaselineId == secondBaselineId
                            && x.Type == ControlledDocumentType.SystemTestProcedures)
                .Select(x => x.Id).SingleAsync();
        }
        var secondXml = await DocumentXmlAsync(client, secondDocumentId);
        Assert.Contains("SYSTP-000941.01", secondXml);
        Assert.DoesNotContain("SYSTP-000941.00", secondXml);
        Assert.DoesNotContain("SYSTP-000942.00", secondXml);
        Assert.DoesNotContain("SYSTP-000942.01", secondXml);
        Assert.Contains("SYSTCR-000942.00", secondXml);
        Assert.Contains("Test Change Authority", secondXml);
        Assert.DoesNotContain(">Change Authority<", secondXml);

        // The first baseline's document remains the controlled snapshot: the same two .00 identities, no
        // successor or retired revision leaked in, and the document record metadata is unchanged. Title text
        // mutation across revisions is a separate tracked defect (#421) and is deliberately not byte-asserted
        // here; #419 owns membership/body identity binding.
        var refreshedFirstXml = await DocumentXmlAsync(client, firstDocumentId);
        Assert.Contains("SYSTP-000941.00", refreshedFirstXml);
        Assert.Contains("SYSTP-000942.00", refreshedFirstXml);
        Assert.DoesNotContain("SYSTP-000941.01", refreshedFirstXml);
        Assert.DoesNotContain("SYSTP-000942.01", refreshedFirstXml);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var record = await db.ControlledDocuments.AsNoTracking()
                .SingleAsync(x => x.Id == firstDocumentId);
            Assert.Equal(firstRecord.ContentHash, record.ContentHash);
            Assert.Equal(firstRecord.ArtifactCount, record.ArtifactCount);
            Assert.Equal(firstRecord.GeneratedAt, record.GeneratedAt);
        }
    }
}
