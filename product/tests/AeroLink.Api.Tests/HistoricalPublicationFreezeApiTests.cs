using System.Net;
using System.Net.Http.Json;
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
/// A controlled publication is frozen bytes, not a recipe. A document generated through
/// /api/baselines/{id}/generate-documents renders each format once at creation and stores the exact artifact;
/// later trace edits, coverage additions or metadata activity cannot rewrite the download. Records created
/// before artifact freezing fall back to on-demand regeneration and are explicitly reported as legacy rather
/// than being claimed deterministic.
/// </summary>
public sealed class HistoricalPublicationFreezeApiTests
{
    [Fact]
    public async Task Frozen_documents_are_byte_identical_after_live_state_changes_and_legacy_regeneration_is_labeled()
    {
        using var factory = new AeroLinkApiFactory();
        var seed = await SeedAsync(factory);
        var client = factory.CreateClient();
        using var login = await client.PostAsJsonAsync("/api/auth/login",
            new { userName = "config.manager", password = AeroLinkApiFactory.MemberPassword });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        await SecurityBoundaryTests.AuthorizeMutationsAsync(client);

        using var generated = await client.PostAsJsonAsync($"/api/baselines/{seed.BaselineId}/generate-documents", new { });
        Assert.Equal(HttpStatusCode.OK, generated.StatusCode);
        var generatedBody = await generated.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(3, generatedBody.GetProperty("generated").GetInt32());
        Assert.Equal(6, generatedBody.GetProperty("artifacts").GetInt32());

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            seed.SysrdId = await db.ControlledDocuments.Where(x => x.BaselineId == seed.BaselineId
                && x.Type == ControlledDocumentType.Sysrd).Select(x => x.Id).SingleAsync();
        }

        using var firstDocx = await client.GetAsync($"/api/documents/{seed.SysrdId}/download?format=docx");
        Assert.Equal(HttpStatusCode.OK, firstDocx.StatusCode);
        var docxBefore = await firstDocx.Content.ReadAsByteArrayAsync();
        using var firstPdf = await client.GetAsync($"/api/documents/{seed.SysrdId}/download?format=pdf");
        Assert.Equal(HttpStatusCode.OK, firstPdf.StatusCode);
        var pdfBefore = await firstPdf.Content.ReadAsByteArrayAsync();

        var manifest = await client.GetFromJsonAsync<JsonElement>($"/api/documents/{seed.SysrdId}/manifest");
        Assert.True(manifest.GetProperty("reproducibility").GetProperty("deterministic").GetBoolean());
        Assert.Equal("frozen artifact", manifest.GetProperty("reproducibility").GetProperty("basis").GetString());
        Assert.Equal(2, manifest.GetProperty("artifacts").GetArrayLength());

        // Live engineering state changes after the document was created: the trace meaning is edited, and a
        // new verification coverage link is added. Regeneration from live tables would change the annexes;
        // the frozen artifact must not.
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var link = await db.RequirementTraces.SingleAsync(x => x.Id == seed.TraceId);
            link.UpdateProposal(RequirementTraceType.DerivedFrom,
                "The parent trace meaning changed after the document was generated.", seed.Now.AddDays(1));
            db.TestCoverage.Add(TestRequirementCoverage.CarriedForward(seed.ProcedureRevisionId,
                seed.CoverageRequirementRevisionId,
                "Coverage was carried while the frozen publication remained unchanged.",
                seed.Now.AddDays(1)));
            await db.SaveChangesAsync();
        }

        using var secondDocx = await client.GetAsync($"/api/documents/{seed.SysrdId}/download?format=docx");
        Assert.Equal(HttpStatusCode.OK, secondDocx.StatusCode);
        Assert.Equal(docxBefore, await secondDocx.Content.ReadAsByteArrayAsync());
        using var secondPdf = await client.GetAsync($"/api/documents/{seed.SysrdId}/download?format=pdf");
        Assert.Equal(HttpStatusCode.OK, secondPdf.StatusCode);
        Assert.Equal(pdfBefore, await secondPdf.Content.ReadAsByteArrayAsync());

        // The background publication job ships the same frozen bytes instead of re-evaluating live state.
        var frozenDocxSha = manifest.GetProperty("artifacts").EnumerateArray()
            .Single(x => x.GetProperty("format").GetString() == "docx").GetProperty("sha256").GetString();
        using var frozenJob = await client.PostAsJsonAsync("/api/publications/jobs",
            new { documentId = seed.SysrdId, format = "docx" });
        Assert.Equal(HttpStatusCode.Accepted, frozenJob.StatusCode);
        var frozenJobBody = await frozenJob.Content.ReadFromJsonAsync<JsonElement>();
        var frozenJobCompleted = await CompletedAsync(client, frozenJobBody.GetProperty("id").GetGuid());
        using var frozenJobResult = JsonDocument.Parse(frozenJobCompleted.GetProperty("resultJson").GetString()!);
        Assert.True(frozenJobResult.RootElement.TryGetProperty("reproducible", out _), frozenJobResult.RootElement.GetRawText());
        Assert.True(frozenJobResult.RootElement.GetProperty("reproducible").GetBoolean());
        Assert.True(frozenJobResult.RootElement.TryGetProperty("basis", out var basis), frozenJobResult.RootElement.GetRawText());
        Assert.Equal("frozen artifact", basis.GetString());
        Assert.Equal(frozenDocxSha, frozenJobResult.RootElement.GetProperty("Sha256").GetString());

        // A record created before artifact freezing cannot claim frozen bytes. It regenerates from live state,
        // which is exactly the historical-drift path; the manifest must say so instead of claiming
        // deterministic=true. The test then proves that drift is real for that legacy path.
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var legacy = new ControlledDocument(seed.ProjectId, seed.ReleaseId, seed.BaselineId,
                ControlledDocumentType.Sysrd, "SYSRD-999999", "Legacy regeneration record", 0,
                new string('f', 64), 1, seed.Now);
            db.ControlledDocuments.Add(legacy);
            await db.SaveChangesAsync();
            seed.LegacyId = legacy.Id;
        }

        using var legacyBefore = await client.GetAsync($"/api/documents/{seed.LegacyId}/download?format=docx");
        Assert.Equal(HttpStatusCode.OK, legacyBefore.StatusCode);
        var legacyBeforeBytes = await legacyBefore.Content.ReadAsByteArrayAsync();
        var legacyManifest = await client.GetFromJsonAsync<JsonElement>($"/api/documents/{seed.LegacyId}/manifest");
        Assert.False(legacyManifest.GetProperty("reproducibility").GetProperty("deterministic").GetBoolean());
        Assert.Contains("legacy", legacyManifest.GetProperty("reproducibility").GetProperty("basis").GetString(), StringComparison.OrdinalIgnoreCase);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var link = await db.RequirementTraces.SingleAsync(x => x.Id == seed.TraceId);
            link.UpdateProposal(RequirementTraceType.DerivedFrom,
                "The trace meaning moved again; legacy regeneration follows live state.", seed.Now.AddDays(2));
            db.RequirementTraces.Add(new RequirementTraceLink(seed.ProjectId, seed.RequirementRevisionId,
                seed.CoverageRequirementRevisionId, RequirementTraceType.DerivedFrom,
                "A second upward relationship was added after the legacy document was created.",
                seed.Now));
            var procedure = await db.TestProcedures.SingleAsync(x => x.Id == seed.ProcedureId);
            procedure.UpdateDraft("Frozen coverage procedure (retitled after publication)", "author", seed.Now.AddDays(2));
            await db.SaveChangesAsync();
        }

        using var legacyAfter = await client.GetAsync($"/api/documents/{seed.LegacyId}/download?format=docx");
        Assert.Equal(HttpStatusCode.OK, legacyAfter.StatusCode);
        var legacyAfterBytes = await legacyAfter.Content.ReadAsByteArrayAsync();
        Assert.NotEqual(legacyBeforeBytes, legacyAfterBytes);

        // A legacy publication job is honestly labelled: it regenerates and does not claim frozen bytes.
        using var legacyJob = await client.PostAsJsonAsync("/api/publications/jobs",
            new { documentId = seed.LegacyId, format = "docx" });
        Assert.Equal(HttpStatusCode.Accepted, legacyJob.StatusCode);
        var legacyJobBody = await legacyJob.Content.ReadFromJsonAsync<JsonElement>();
        var legacyJobCompleted = await CompletedAsync(client, legacyJobBody.GetProperty("id").GetGuid());
        using var legacyJobResult = JsonDocument.Parse(legacyJobCompleted.GetProperty("resultJson").GetString()!);
        Assert.False(legacyJobResult.RootElement.GetProperty("reproducible").GetBoolean());
        Assert.Contains("legacy", legacyJobResult.RootElement.GetProperty("basis").GetString(), StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<JsonElement> CompletedAsync(HttpClient client, Guid id)
    {
        JsonElement last = default;
        for (var attempt = 0; attempt < 80; attempt++)
        {
            var job = await client.GetFromJsonAsync<JsonElement>($"/api/publications/jobs/{id}");
            last = job;
            var state = job.GetProperty("state").GetString();
            if (state == "Completed") return job;
            if (state == "Failed") Assert.Fail(job.GetProperty("lastError").GetString());
            await Task.Delay(250);
        }
        throw new TimeoutException($"Publication job {id} did not complete. Last seen: {last}");
    }

    private static async Task<Fixture> SeedAsync(AeroLinkApiFactory factory)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        var now = new DateTimeOffset(2026, 8, 13, 10, 0, 0, TimeSpan.Zero);

        var program = new ProgramRecord("Frozen Publication Program", "FPP");
        var project = new ProjectRecord(program.Id, "Frozen Product", "Frozen System");
        var release = new SoftwareRelease(project.Id, "2.0", false);
        var scr = new SystemChangeRequest("SRCR-04500", 0, project.Id, release.Id,
            "Frozen publication", "Problem", "Analysis", "Solution", "author", now);
        scr.AddRequirementChange("author", "SYSR-004500", 0, RequirementLevel.System,
            RequirementChangeKind.Introduce, "The system shall freeze controlled publications.",
            "Exact bytes must be retained.", "Test", now);
        scr.SubmitForReview("author", [new ApproverSelection("reviewer", "Reviewer")], now);
        scr.ApproveActiveStage("reviewer", now);

        var baseline = new CandidateBaseline("SW-02.00", 0, project.Id, release.Id, null,
            "Frozen baseline", "cm", now);
        baseline.Select(scr, "cm", now);
        baseline.Freeze("cm", now);
        baseline.MarkRequirementsMaterialized("cm", new string('a', 64), 1, now);

        var requirement = new RequirementArtifact(project.Id, "SYSR-004500", RequirementLevel.System, now);
        var revision = new RequirementRevision(requirement.Id, 0,
            "The system shall freeze controlled publications.", "Rationale", "Test",
            RequirementRevisionState.Active, scr.Id, baseline.Id, now);
        var target = new RequirementArtifact(project.Id, "SYSR-004501", RequirementLevel.System, now);
        var targetRevision = new RequirementRevision(target.Id, 0,
            "Parent system requirement.", "Rationale", "Test",
            RequirementRevisionState.Active, scr.Id, baseline.Id, now);
        var coverageRequirement = new RequirementArtifact(project.Id, "SYSR-004502", RequirementLevel.System, now);
        var coverageRevision = new RequirementRevision(coverageRequirement.Id, 0,
            "A requirement carried into a later controlled publication.", "Rationale", "Test",
            RequirementRevisionState.Active, scr.Id, baseline.Id, now);
        var trace = new RequirementTraceLink(project.Id, revision.Id, targetRevision.Id,
            RequirementTraceType.DerivedFrom, "Original trace rationale.", now);
        var procedure = new TestProcedure(project.Id, "SYSTP-004500", "Frozen coverage procedure",
            "author", now, TestProcedureLevel.System);
        var procedureRevision = new TestProcedureRevision(procedure.Id, 0, "Objective",
            "Preconditions", "Steps", "Expected", TestProcedureState.Approved, "author", now,
            effectiveBaselineId: baseline.Id, parentKind: VerificationProcedureParentKind.Allocated);
        var config = new UserAccount("config.manager", "Config Manager", "config@example.test",
            IdentityService.HashPassword(AeroLinkApiFactory.MemberPassword), now);

        db.AddRange(program, project, release, scr, baseline, requirement, revision, target, targetRevision,
            coverageRequirement, coverageRevision,
            trace, procedure, procedureRevision, config,
            new ProgramMembership(config.Id, program.Id, ProgramRole.ConfigurationManager, "test.setup", now));
        db.BaselineRequirements.Add(new BaselineRequirementSelection(baseline.Id, requirement.Id, revision.Id));
        db.BaselineRequirements.Add(new BaselineRequirementSelection(baseline.Id, coverageRequirement.Id, coverageRevision.Id));
        db.TestCoverage.Add(new TestRequirementCoverage(procedureRevision.Id, revision.Id));
        await db.SaveChangesAsync();

        return new Fixture(project.Id, release.Id, baseline.Id, trace.Id, revision.Id, coverageRevision.Id,
            procedureRevision.Id, procedure.Id, now);
    }

    private sealed class Fixture(Guid projectId, Guid releaseId, Guid baselineId, Guid traceId,
        Guid requirementRevisionId, Guid coverageRequirementRevisionId, Guid procedureRevisionId,
        Guid procedureId, DateTimeOffset now)
    {
        public Guid ProjectId { get; } = projectId;
        public Guid ReleaseId { get; } = releaseId;
        public Guid BaselineId { get; } = baselineId;
        public Guid TraceId { get; } = traceId;
        public Guid RequirementRevisionId { get; } = requirementRevisionId;
        public Guid CoverageRequirementRevisionId { get; } = coverageRequirementRevisionId;
        public Guid ProcedureId { get; } = procedureId;
        public Guid ProcedureRevisionId { get; } = procedureRevisionId;
        public DateTimeOffset Now { get; } = now;
        public Guid SysrdId { get; set; }
        public Guid LegacyId { get; set; }
    }
}
