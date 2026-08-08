using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using AeroLink.Domain.Baselines;
using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Identity;
using AeroLink.Domain.Programs;
using AeroLink.Domain.Releases;
using AeroLink.Domain.Requirements;
using AeroLink.Domain.Verification;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AeroLink.Api.Tests;

/// <summary>
/// The verification read surface carries controlled requirements, execution determinations, evidence metadata,
/// and the evidence bytes themselves. Every route has to prove the caller belongs to the owning Program; a
/// project identifier supplied by the browser is never authority to read it.
/// </summary>
public sealed class VerificationProgramIsolationApiTests
{
    private const string ProgramAUser = "verification.program-a";
    private const string ProgramBUser = "verification.program-b";
    private const string EvidenceFileName = "program-a-flight-test.json";
    private const string EvidenceContent = "{\"program\":\"A\",\"result\":\"pass\"}";

    [Fact]
    public async Task Program_b_browser_session_cannot_read_any_program_a_verification_route()
    {
        using var factory = new AeroLinkApiFactory();
        var scenario = await SeedAsync(factory);
        using var client = factory.CreateClient();
        await LoginAsync(client, ProgramBUser);

        var requests = new[]
        {
            $"/api/traceability?projectId={scenario.ProjectAId}&baselineId={scenario.BaselineAId}&page=1&pageSize=25",
            $"/api/test-executions?projectId={scenario.ProjectAId}&releaseId={scenario.ReleaseAId}&buildId={scenario.BuildAId}",
            $"/api/verification-coverage?projectId={scenario.ProjectAId}&baselineId={scenario.BaselineAId}&buildId={scenario.BuildAId}",
            $"/api/evidence/{scenario.EvidenceAId}",
            $"/api/traceability/{scenario.BaselineAId}/download?format=pdf",
            $"/api/traceability/path?projectId={scenario.ProjectAId}&baselineId={scenario.BaselineAId}",
            $"/api/test-procedures?projectId={scenario.ProjectAId}&scope=System&page=1&pageSize=25",
            $"/api/test-procedures/{scenario.ProcedureAId}/history?revisionId={scenario.ProcedureRevisionAId}",
            $"/api/test-procedures/{scenario.ProcedureAId}/comments",
        };

        foreach (var request in requests)
        {
            using var response = await client.GetAsync(request);
            var body = await response.Content.ReadAsStringAsync();
            Assert.True(response.StatusCode == HttpStatusCode.Forbidden,
                $"Expected Program refusal for {request}, got {(int)response.StatusCode}: {body}");
            Assert.DoesNotContain(EvidenceFileName, body, StringComparison.Ordinal);
            Assert.DoesNotContain(scenario.EvidenceSha256, body, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(scenario.RequirementAId.ToString(), body, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(scenario.ExecutionAId.ToString(), body, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Program A controlled requirement", body, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Program_a_browser_session_retains_traceability_coverage_execution_and_evidence_reads()
    {
        using var factory = new AeroLinkApiFactory();
        var scenario = await SeedAsync(factory);
        using var client = factory.CreateClient();
        await LoginAsync(client, ProgramAUser);

        using var traceability = await client.GetAsync(
            $"/api/traceability?projectId={scenario.ProjectAId}&baselineId={scenario.BaselineAId}&page=1&pageSize=25");
        Assert.Equal(HttpStatusCode.OK, traceability.StatusCode);
        var trace = await traceability.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, trace.GetProperty("totalCount").GetInt32());
        Assert.Equal(scenario.RequirementAId,
            trace.GetProperty("items")[0].GetProperty("revisionId").GetGuid());
        Assert.Equal(EvidenceFileName, trace.GetProperty("items")[0].GetProperty("tests")[0]
            .GetProperty("executions")[0].GetProperty("evidence")[0].GetProperty("originalFileName").GetString());

        using var executions = await client.GetAsync(
            $"/api/test-executions?projectId={scenario.ProjectAId}&releaseId={scenario.ReleaseAId}&buildId={scenario.BuildAId}");
        Assert.Equal(HttpStatusCode.OK, executions.StatusCode);
        var executionRows = await executions.Content.ReadFromJsonAsync<JsonElement>();
        var execution = Assert.Single(executionRows.EnumerateArray());
        Assert.Equal(scenario.ExecutionAId, execution.GetProperty("id").GetGuid());
        Assert.Equal(EvidenceFileName, execution.GetProperty("evidence")[0].GetProperty("originalFileName").GetString());

        using var coverage = await client.GetAsync(
            $"/api/verification-coverage?projectId={scenario.ProjectAId}&baselineId={scenario.BaselineAId}&buildId={scenario.BuildAId}");
        Assert.Equal(HttpStatusCode.OK, coverage.StatusCode);
        var coverageBody = await coverage.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, coverageBody.GetProperty("total").GetInt32());
        Assert.Equal(1, coverageBody.GetProperty("covered").GetInt32());
        Assert.Equal(1, coverageBody.GetProperty("verified").GetInt32());

        using var evidence = await client.GetAsync($"/api/evidence/{scenario.EvidenceAId}");
        Assert.Equal(HttpStatusCode.OK, evidence.StatusCode);
        Assert.Equal(EvidenceFileName, evidence.Content.Headers.ContentDisposition?.FileNameStar
            ?? evidence.Content.Headers.ContentDisposition?.FileName?.Trim('"'));
        Assert.Equal(EvidenceContent, await evidence.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Cross_project_baseline_release_and_build_parameters_are_refused_deterministically()
    {
        using var factory = new AeroLinkApiFactory();
        var scenario = await SeedAsync(factory);
        using var client = factory.CreateClient();
        await LoginAsync(client, ProgramBUser);

        await AssertBadRequestsAsync(client,
        [
            ($"/api/traceability?projectId={scenario.ProjectBId}&baselineId={scenario.BaselineAId}&page=1&pageSize=25", "baseline_project_mismatch"),
            ($"/api/verification-coverage?projectId={scenario.ProjectBId}&baselineId={scenario.BaselineAId}", "baseline_project_mismatch"),
            ($"/api/verification-coverage?projectId={scenario.ProjectBId}&buildId={scenario.BuildAId}", "build_project_mismatch"),
            ($"/api/verification-coverage?projectId={scenario.ProjectBId}&baselineId={scenario.BaselineAId}&buildId={scenario.BuildBId}", "baseline_build_mismatch"),
            ($"/api/test-executions?projectId={scenario.ProjectBId}&releaseId={scenario.ReleaseAId}", "release_project_mismatch"),
            ($"/api/test-executions?projectId={scenario.ProjectBId}&buildId={scenario.BuildAId}", "build_project_mismatch"),
            ($"/api/test-executions?projectId={scenario.ProjectBId}&releaseId={scenario.ReleaseAId}&buildId={scenario.BuildBId}", "release_project_mismatch"),
        ]);
    }

    [Fact]
    public async Task Program_b_service_identity_cannot_cross_into_program_a_or_browser_verification_routes()
    {
        using var factory = new AeroLinkApiFactory();
        var scenario = await SeedAsync(factory);
        string apiKey;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var security = scope.ServiceProvider.GetRequiredService<IntegrationSecurityService>();
            var issued = await security.CreateIdentityAsync(scenario.ProjectBId, "Program B verification integration",
                ["requirements:read"], "test.setup", DateTimeOffset.UtcNow, CancellationToken.None);
            apiKey = issued.ApiKey;
        }

        using var service = factory.CreateClient();
        service.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        using var list = await service.GetAsync($"/api/v1/requirements?projectId={scenario.ProjectAId}");
        Assert.Equal(HttpStatusCode.Forbidden, list.StatusCode);
        Assert.DoesNotContain("Program A controlled requirement", await list.Content.ReadAsStringAsync(),
            StringComparison.Ordinal);

        using var detail = await service.GetAsync($"/api/v1/requirements/{scenario.RequirementArtifactAId}");
        Assert.Equal(HttpStatusCode.NotFound, detail.StatusCode);

        using var browserRoute = await service.GetAsync($"/api/evidence/{scenario.EvidenceAId}");
        Assert.Equal(HttpStatusCode.Unauthorized, browserRoute.StatusCode);
        var body = await browserRoute.Content.ReadAsStringAsync();
        Assert.DoesNotContain(EvidenceFileName, body, StringComparison.Ordinal);
        Assert.DoesNotContain(scenario.EvidenceSha256, body, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task AssertBadRequestsAsync(HttpClient client,
        IReadOnlyCollection<(string Request, string ExpectedCode)> cases)
    {
        var failures = new List<string>();
        foreach (var (request, expectedCode) in cases)
        {
            using var response = await client.GetAsync(request);
            var body = await response.Content.ReadAsStringAsync();
            string? actualCode = null;
            if (!string.IsNullOrWhiteSpace(body))
            {
                using var json = JsonDocument.Parse(body);
                if (json.RootElement.ValueKind == JsonValueKind.Object
                    && json.RootElement.TryGetProperty("code", out var code)) actualCode = code.GetString();
            }
            if (response.StatusCode != HttpStatusCode.BadRequest || actualCode != expectedCode)
                failures.Add($"{request} => {(int)response.StatusCode}, code={actualCode ?? "<none>"}, body={body}");
        }
        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    private static async Task LoginAsync(HttpClient client, string userName)
    {
        using var response = await client.PostAsJsonAsync("/api/auth/login",
            new { userName, password = AeroLinkApiFactory.MemberPassword });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static async Task<Scenario> SeedAsync(AeroLinkApiFactory factory)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        var evidenceStore = scope.ServiceProvider.GetRequiredService<EvidenceFileStore>();
        var now = DateTimeOffset.UtcNow;

        var programA = new ProgramRecord("Verification Program A", "VPA");
        var projectA = new ProjectRecord(programA.Id, "Program A Flight Controls", "Program A Flight Controls");
        var releaseA = new SoftwareRelease(projectA.Id, "1.0", false);
        var baselineA = new CandidateBaseline("SW-10.00", 0, projectA.Id, releaseA.Id, null,
            "Program A controlled baseline", "program-a.cm", now);
        var buildA = new SoftwareBuild(projectA.Id, releaseA.Id, baselineA.Id, "SW-10.00",
            "Program A controlled build", "program-a.cm", now);
        var sourceA = new SystemChangeRequest("SRCR-10001", 0, projectA.Id, releaseA.Id,
            "Program A source change", "Problem", "Analysis", "Solution", "program-a.author", now);
        var requirementArtifactA = new RequirementArtifact(projectA.Id, "SYSR-100001",
            RequirementLevel.System, now);
        var requirementA = new RequirementRevision(requirementArtifactA.Id, 0,
            "Program A controlled requirement", "Program A rationale", "Test",
            RequirementRevisionState.Active, sourceA.Id, baselineA.Id, now);
        var procedureA = new TestProcedure(projectA.Id, "SYSTP-100001", "Program A verification procedure",
            "program-a.tester", now, TestProcedureLevel.System);
        var procedureRevisionA = new TestProcedureRevision(procedureA.Id, 0, "Verify Program A behavior",
            "Program A test rig", "Execute Program A scenario", "Program A behavior passes",
            TestProcedureState.Approved, "program-a.tester", now);
        var executionA = new TestExecution(projectA.Id, procedureRevisionA.Id, buildA.Id, null, TestOutcome.Pass,
            "program-a.tester", "Program A rig", "Program A determination", "controlled://program-a/result",
            now, now, releaseA.Id);

        await using var evidenceInput = new MemoryStream(Encoding.UTF8.GetBytes(EvidenceContent));
        var storedEvidence = await evidenceStore.StoreAsync(evidenceInput, EvidenceFileName,
            "application/json", CancellationToken.None);
        var evidenceA = new EvidenceRecord(projectA.Id, storedEvidence.OriginalFileName,
            storedEvidence.ContentType, storedEvidence.Size, storedEvidence.Sha256, storedEvidence.StorageKey,
            "program-a.tester", now);

        var programB = new ProgramRecord("Verification Program B", "VPB");
        var projectB = new ProjectRecord(programB.Id, "Program B Navigation", "Program B Navigation");
        var releaseB = new SoftwareRelease(projectB.Id, "2.0", false);
        var baselineB = new CandidateBaseline("SW-20.00", 0, projectB.Id, releaseB.Id, null,
            "Program B controlled baseline", "program-b.cm", now);
        var buildB = new SoftwareBuild(projectB.Id, releaseB.Id, baselineB.Id, "SW-20.00",
            "Program B controlled build", "program-b.cm", now);

        var programAUser = Account(ProgramAUser, now);
        var programBUser = Account(ProgramBUser, now);
        db.AddRange(
            programA, projectA, releaseA, baselineA, buildA, sourceA, requirementArtifactA, requirementA,
            procedureA, procedureRevisionA,
            new BaselineRequirementSelection(baselineA.Id, requirementArtifactA.Id, requirementA.Id),
            new BaselineTestProcedureSelection(baselineA.Id, procedureA.Id, procedureRevisionA.Id),
            new TestRequirementCoverage(procedureRevisionA.Id, requirementA.Id), executionA, evidenceA,
            new TestExecutionEvidence(executionA.Id, evidenceA.Id),
            programB, projectB, releaseB, baselineB, buildB,
            programAUser, programBUser,
            new ProgramMembership(programAUser.Id, programA.Id, ProgramRole.Engineer, "test.setup", now),
            new ProgramMembership(programBUser.Id, programB.Id, ProgramRole.Engineer, "test.setup", now));
        await db.SaveChangesAsync();
        await db.CandidateBaselines.Where(x => x.Id == baselineA.Id)
            .ExecuteUpdateAsync(update => update.SetProperty(x => x.RequirementsMaterializedAt, now));

        return new(projectA.Id, releaseA.Id, baselineA.Id, buildA.Id, requirementArtifactA.Id, requirementA.Id,
            procedureA.Id, procedureRevisionA.Id, executionA.Id, evidenceA.Id, evidenceA.Sha256,
            projectB.Id, releaseB.Id, baselineB.Id, buildB.Id);
    }

    private static UserAccount Account(string userName, DateTimeOffset now) => new(userName, userName,
        $"{userName}@example.test", IdentityService.HashPassword(AeroLinkApiFactory.MemberPassword), now);

    private sealed record Scenario(
        Guid ProjectAId,
        Guid ReleaseAId,
        Guid BaselineAId,
        Guid BuildAId,
        Guid RequirementArtifactAId,
        Guid RequirementAId,
        Guid ProcedureAId,
        Guid ProcedureRevisionAId,
        Guid ExecutionAId,
        Guid EvidenceAId,
        string EvidenceSha256,
        Guid ProjectBId,
        Guid ReleaseBId,
        Guid BaselineBId,
        Guid BuildBId);
}
