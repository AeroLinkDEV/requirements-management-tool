using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AeroLink.Domain.Baselines;
using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Hierarchy;
using AeroLink.Domain.Identity;
using AeroLink.Domain.Programs;
using AeroLink.Domain.Requirements;
using AeroLink.Domain.Traceability;
using AeroLink.Domain.Verification;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AeroLink.Api.Tests;

/// <summary>#728 exact-key qualification through the same controlled document endpoint and renderer as Case.</summary>
public sealed class ProcedureControlledDocumentApiTests
{
    [Fact]
    public async Task Exact_key_documents_have_distinct_identity_content_hash_and_frozen_bytes()
    {
        var policy = ProcedurePolicy();
        using var factory = new AeroLinkApiFactory(testLadderPolicy: policy);
        using var client = factory.CreateClient();
        var fixture = await SeedAsync(factory, policy);
        await LoginAsync(client, "document.cm");

        var procedureRegisters = await client.GetFromJsonAsync<JsonElement>(
            $"/api/projects/{fixture.ProjectId}/test-procedure-documents?scope=Software");
        var procedureRegisterRows = procedureRegisters.EnumerateArray().ToArray();
        Assert.Equal(2, procedureRegisterRows.Length);
        Assert.All(procedureRegisterRows, row =>
            Assert.Equal("Procedure", row.GetProperty("artifactKind").GetString()));
        Assert.Contains(procedureRegisterRows, row =>
            row.GetProperty("documentNumber").GetString()!.StartsWith("HLRTPD-", StringComparison.Ordinal));
        Assert.Contains(procedureRegisterRows, row =>
            row.GetProperty("documentNumber").GetString()!.StartsWith("LLRTPD-", StringComparison.Ordinal));
        var caseRegisters = await client.GetFromJsonAsync<JsonElement>(
            $"/api/projects/{fixture.ProjectId}/test-case-documents?scope=Software");
        Assert.All(caseRegisters.EnumerateArray(), row =>
            Assert.Equal("Case", row.GetProperty("artifactKind").GetString()));

        using var generated = await client.PostAsJsonAsync(
            $"/api/baselines/{fixture.BaselineId}/generate-documents", new { });
        var generatedBody = await generated.Content.ReadAsStringAsync();
        Assert.True(generated.IsSuccessStatusCode, generatedBody);
        using (var json = JsonDocument.Parse(generatedBody))
        {
            Assert.Equal(8, json.RootElement.GetProperty("generated").GetInt32());
            Assert.Equal(16, json.RootElement.GetProperty("artifacts").GetInt32());
            Assert.Empty(json.RootElement.GetProperty("skipped").EnumerateArray());
        }

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        var documents = await db.ControlledDocuments.AsNoTracking()
            .Where(x => x.BaselineId == fixture.BaselineId).ToListAsync();
        Assert.Equal(8, documents.Count);
        var highCase = documents.Single(x => x.Type == ControlledDocumentType.HighLevelTestCases);
        var highProcedure = documents.Single(x => x.Type == ControlledDocumentType.HighLevelTestProcedures);
        var lowCase = documents.Single(x => x.Type == ControlledDocumentType.LowLevelTestCases);
        var lowProcedure = documents.Single(x => x.Type == ControlledDocumentType.LowLevelTestProcedures);
        Assert.StartsWith("HLRTD-", highCase.DocumentNumber, StringComparison.Ordinal);
        Assert.StartsWith("HLRTPD-", highProcedure.DocumentNumber, StringComparison.Ordinal);
        Assert.StartsWith("LLRTD-", lowCase.DocumentNumber, StringComparison.Ordinal);
        Assert.StartsWith("LLRTPD-", lowProcedure.DocumentNumber, StringComparison.Ordinal);
        Assert.All(new[] { highCase, highProcedure, lowCase, lowProcedure }, x => Assert.Equal(1, x.ArtifactCount));
        Assert.NotEqual(highCase.ContentHash, highProcedure.ContentHash);
        Assert.NotEqual(lowCase.ContentHash, lowProcedure.ContentHash);

        var highCaseXml = await DocumentXmlAsync(client, highCase.Id);
        var highProcedureXml = await DocumentXmlAsync(client, highProcedure.Id);
        Assert.Contains("HLRTC-728001.00", highCaseXml);
        Assert.Contains("Case-only objective", highCaseXml);
        Assert.Contains("Case steps", highCaseXml);
        Assert.DoesNotContain("HLRTP-728001.00", highCaseXml);
        Assert.DoesNotContain("Procedure-only setup", highCaseXml);
        Assert.DoesNotContain("Environment / setup", highCaseXml);

        Assert.Contains("HLRTP-728001.00", highProcedureXml);
        Assert.Equal(1, Occurrences(highProcedureXml, "Compatibility objective"));
        foreach (var value in new[]
                 {
                     "Procedure-only setup", "Procedure-only data", "Procedure-only ordered steps",
                     "Procedure-only observations", "Procedure-only cleanup", "Procedure-only tooling"
                 })
            Assert.Equal(1, Occurrences(highProcedureXml, value));
        Assert.DoesNotContain("HLRTC-728001.00", highProcedureXml);
        Assert.DoesNotContain("Case-only objective", highProcedureXml);
        Assert.DoesNotContain("Case steps", highProcedureXml);

        var highProcedureDraftXml = await DraftDocumentXmlAsync(client, fixture.ReleaseId,
            ControlledDocumentType.HighLevelTestProcedures);
        Assert.Contains("HLRTP-728001.00", highProcedureDraftXml);
        Assert.Equal(1, Occurrences(highProcedureDraftXml, "Compatibility objective"));
        foreach (var value in new[]
                 {
                     "Procedure-only setup", "Procedure-only data", "Procedure-only ordered steps",
                     "Procedure-only observations", "Procedure-only cleanup", "Procedure-only tooling"
                 })
            Assert.Equal(1, Occurrences(highProcedureDraftXml, value));
        Assert.DoesNotContain("HLRTC-728001.00", highProcedureDraftXml);
        Assert.DoesNotContain("Case-only objective", highProcedureDraftXml);
        Assert.DoesNotContain("Case steps", highProcedureDraftXml);

        var system = documents.Single(x => x.Type == ControlledDocumentType.SystemTestProcedures);
        var systemXml = await DocumentXmlAsync(client, system.Id);
        Assert.Contains("SYSTP-728001.00", systemXml);
        Assert.Contains("System objective unchanged", systemXml);
        Assert.Contains("Procedure steps", systemXml);
        Assert.DoesNotContain("Environment / setup", systemXml);

        foreach (var document in new[] { highCase, highProcedure, lowCase, lowProcedure, system })
        {
            var artifact = await db.ControlledDocumentArtifacts.AsNoTracking()
                .SingleAsync(x => x.DocumentId == document.Id && x.Format == "docx");
            using var download = await client.GetAsync($"/api/documents/{document.Id}/download?format=docx");
            var bytes = await download.Content.ReadAsByteArrayAsync();
            Assert.Equal(artifact.Sha256,
                Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
        }
    }

    private sealed record Fixture(Guid ProjectId, Guid ReleaseId, Guid BaselineId);

    private static async Task<Fixture> SeedAsync(AeroLinkApiFactory factory, ILadderPolicy policy)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        var now = DateTimeOffset.UtcNow;
        var program = new ProgramRecord("Procedure Document Program", "PDP");
        var project = new ProjectRecord(program.Id, "Software", "Procedure Document Software");
        var release = new SoftwareRelease(project.Id, "7.28", false);
        var change = new SystemChangeRequest("SRCR-728001", 0, project.Id, release.Id,
            "Document qualification authority", "P", "A", "S", "document.author", now);
        change.AddRequirementChange("document.author", "SYSR-728001", 0, RequirementLevel.System,
            RequirementChangeKind.Introduce, "Qualification requirement", "Controlled output qualification",
            "Test", now);
        change.SubmitForReview("document.author",
            [new ApproverSelection("document.reviewer", "Document Reviewer")], now);
        change.ApproveActiveStage("document.reviewer", now);
        var baseline = new CandidateBaseline("SW-07.28", 0, project.Id, release.Id, null,
            "Exact-key controlled documents", "document.cm", now);
        baseline.Select(change, "document.cm", now);
        baseline.Freeze("document.cm", now);
        var user = new UserAccount("document.cm", "Document CM", "document.cm@example.test",
            IdentityService.HashPassword(AeroLinkApiFactory.MemberPassword), now);
        db.AddRange(program, project, release, change, baseline, user,
            new ProgramMembership(user.Id, program.Id, ProgramRole.ConfigurationManager, "test.setup", now),
            new ProjectLeadershipAssignment(program.Id, ProjectLeadershipPosition.ConfigurationManager,
                user.Id, "test.setup", now));
        await db.SaveChangesAsync();

        // Requirements are already represented by their own exact manifest. The controlled-output endpoint
        // only needs that materialized boundary; this test is about typed verification membership.
        baseline.MarkRequirementsMaterialized("document.cm", new string('a', 64), 0, now);

        var artifacts = new[]
        {
            NewArtifact(project.Id, "SYSTP-728001", TestProcedureLevel.System,
                VerificationArtifactKind.Procedure, policy, "System"),
            NewArtifact(project.Id, "HLRTC-728001", TestProcedureLevel.HighLevel,
                VerificationArtifactKind.Case, policy, "High Case"),
            NewArtifact(project.Id, "HLRTP-728001", TestProcedureLevel.HighLevel,
                VerificationArtifactKind.Procedure, policy, "High Procedure"),
            NewArtifact(project.Id, "LLRTC-728001", TestProcedureLevel.LowLevel,
                VerificationArtifactKind.Case, policy, "Low Case"),
            NewArtifact(project.Id, "LLRTP-728001", TestProcedureLevel.LowLevel,
                VerificationArtifactKind.Procedure, policy, "Low Procedure"),
        };
        foreach (var (artifact, revision) in artifacts)
            db.AddRange(artifact, revision);
        await db.SaveChangesAsync();

        // The dormant authoring seam creates software Procedure revision 0 as Draft. This fixture represents
        // the future approved membership boundary without activating #726: promote only those disposable test
        // rows, then freeze their exact IDs into the baseline manifest.
        var procedureRevisionIds = artifacts
            .Where(x => x.Artifact.Level != TestProcedureLevel.System
                && x.Artifact.ArtifactKind == VerificationArtifactKind.Procedure)
            .Select(x => x.Revision.Id).ToArray();
        await db.TestProcedureRevisions.Where(x => procedureRevisionIds.Contains(x.Id))
            .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.State, TestProcedureState.Approved));
        foreach (var (artifact, revision) in artifacts)
            db.Add(new BaselineTestProcedureSelection(baseline.Id, artifact.Id, revision.Id));
        baseline.MarkTestProceduresMaterialized("document.cm", new string('b', 64), artifacts.Length, now);
        await db.SaveChangesAsync();
        await new TestProcedureDocumentBootstrap(db, policy).EnsureForProjectAsync(project.Id);
        await db.SaveChangesAsync();
        return new(project.Id, release.Id, baseline.Id);
    }

    private static (TestProcedure Artifact, TestProcedureRevision Revision) NewArtifact(Guid projectId,
        string number, TestProcedureLevel level, VerificationArtifactKind kind, ILadderPolicy policy, string title)
    {
        var now = DateTimeOffset.UtcNow;
        var parentKind = level == TestProcedureLevel.System || kind == VerificationArtifactKind.Case
            ? VerificationProcedureParentKind.Derived
            : VerificationProcedureParentKind.Derived;
        var artifact = new TestProcedure(projectId, number, title, "test.engineer", now, level, policy, kind,
            parentKind);
        var system = level == TestProcedureLevel.System;
        var @case = kind == VerificationArtifactKind.Case;
        var revision = new TestProcedureRevision(artifact.Id, 0,
            @case ? "Case-only objective" : system ? "System objective unchanged" : "Compatibility objective",
            @case ? "Case-only preconditions" : system ? "System preconditions unchanged" : "Procedure-only setup",
            @case ? "Case-only steps" : system ? "System steps unchanged" : "Procedure-only ordered steps",
            @case ? "Case-only expected result" : system ? "System expected result unchanged" : "Procedure-only observations",
            !system && kind == VerificationArtifactKind.Procedure
                ? TestProcedureState.Draft
                : TestProcedureState.Approved, "test.engineer", now,
            effectiveBaselineId: null,
            environmentSetup: system || @case ? "" : "Procedure-only setup",
            testData: system || @case ? "" : "Procedure-only data",
            orderedSteps: system || @case ? "" : "Procedure-only ordered steps",
            expectedObservations: system || @case ? "" : "Procedure-only observations",
            cleanup: system || @case ? "" : "Procedure-only cleanup",
            toolingAutomation: system || @case ? "" : "Procedure-only tooling",
            parentKind: parentKind,
            derivedRationale: "Standalone exact-key controlled document qualification.");
        return (artifact, revision);
    }

    private static async Task<string> DocumentXmlAsync(HttpClient client, Guid documentId)
    {
        using var download = await client.GetAsync($"/api/documents/{documentId}/download?format=docx");
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        var bytes = await download.Content.ReadAsByteArrayAsync();
        using var archive = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
        using var reader = new StreamReader(archive.GetEntry("word/document.xml")!.Open());
        return await reader.ReadToEndAsync();
    }

    private static async Task<string> DraftDocumentXmlAsync(HttpClient client, Guid releaseId,
        ControlledDocumentType type)
    {
        using var download = await client.GetAsync(
            $"/api/releases/{releaseId}/draft-document?type={type}&format=docx");
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        var bytes = await download.Content.ReadAsByteArrayAsync();
        using var archive = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
        using var reader = new StreamReader(archive.GetEntry("word/document.xml")!.Open());
        return await reader.ReadToEndAsync();
    }

    private static int Occurrences(string value, string expected) =>
        value.Split(expected, StringSplitOptions.None).Length - 1;

    private static async Task LoginAsync(HttpClient client, string user)
    {
        using var login = await client.PostAsJsonAsync("/api/auth/login", new
        {
            userName = user,
            password = AeroLinkApiFactory.MemberPassword,
        });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        await SecurityBoundaryTests.AuthorizeMutationsAsync(client);
    }

    private static ILadderPolicy ProcedurePolicy()
    {
        var projectId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var configuration = ProjectLadderConfiguration.CreateDraft(projectId, now);
        var steps = LegacyLadderPolicy.Instance.OrderedLevels.Select((level, index) =>
        {
            var kinds = level == RequirementLevel.System
                ? new[] { VerificationArtifactKind.Procedure }
                : new[] { VerificationArtifactKind.Case, VerificationArtifactKind.Procedure };
            var step = new ProjectLadderStep(configuration.Id, projectId, level, index + 1,
                LegacyLadderPolicy.Instance.Definition(level).Capabilities, now, kinds);
            configuration.Steps.Add(step);
            return step;
        }).ToArray();
        configuration.AllowedUpstream.Add(new ProjectLadderAllowedUpstream(
            configuration.Id, projectId, steps[0].Id, steps[1].Id, now));
        configuration.AllowedUpstream.Add(new ProjectLadderAllowedUpstream(
            configuration.Id, projectId, steps[1].Id, steps[2].Id, now));
        return new ResolvedProjectLadderPolicy(ProjectLadderResolver.Resolve(configuration));
    }
}
