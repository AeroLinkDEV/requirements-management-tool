using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AeroLink.Domain.Baselines;
using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Hierarchy;
using AeroLink.Domain.Identity;
using AeroLink.Domain.Programs;
using AeroLink.Domain.Releases;
using AeroLink.Domain.Verification;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AeroLink.Api.Tests;

/// <summary>
/// #726 blocker 4: POST /api/test-executions resolves the effective executable kind for EVERY submission.
/// Case-only software accepts Cases and rejects software Procedures; the full Case+Procedure profile accepts
/// Procedures and rejects Cases; System accepts System Procedures.
/// </summary>
public sealed class EffectiveExecutionAuthorityApiTests
{
    [Fact]
    public async Task Case_only_profile_accepts_cases_and_rejects_software_procedures()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        var seed = await SeedAsync(factory);
        await LoginAsync(client, "execution.author");

        var caseAccepted = await PostExecutionAsync(client, seed.ProjectId, seed.BuildId,
            seed.CaseRevisionId);
        Assert.Equal(HttpStatusCode.Created, caseAccepted.StatusCode);
        var systemAccepted = await PostExecutionAsync(client, seed.ProjectId, seed.BuildId,
            seed.SystemRevisionId);
        Assert.Equal(HttpStatusCode.Created, systemAccepted.StatusCode);
        var procedureRejected = await PostExecutionAsync(client, seed.ProjectId, seed.BuildId,
            seed.SoftwareProcedureRevisionId);
        Assert.Equal(HttpStatusCode.BadRequest, procedureRejected.StatusCode);
        Assert.Contains("not_effective_executable", await procedureRejected.Content.ReadAsStringAsync(),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Full_profile_accepts_software_procedures_and_rejects_cases()
    {
        using var factory = new AeroLinkApiFactory(testLadderPolicy: ProcedureEnabledTestPolicy.Create());
        using var client = factory.CreateClient();
        var seed = await SeedAsync(factory);
        await LoginAsync(client, "execution.author");

        var procedureAccepted = await PostExecutionAsync(client, seed.ProjectId, seed.BuildId,
            seed.SoftwareProcedureRevisionId);
        Assert.Equal(HttpStatusCode.Created, procedureAccepted.StatusCode);
        var systemAccepted = await PostExecutionAsync(client, seed.ProjectId, seed.BuildId,
            seed.SystemRevisionId);
        Assert.Equal(HttpStatusCode.Created, systemAccepted.StatusCode);
        var caseRejected = await PostExecutionAsync(client, seed.ProjectId, seed.BuildId,
            seed.CaseRevisionId);
        Assert.Equal(HttpStatusCode.BadRequest, caseRejected.StatusCode);
        Assert.Contains("not_effective_executable", await caseRejected.Content.ReadAsStringAsync(),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task System_candidate_inventory_accepts_the_authoritative_procedure_kind()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        var seed = await SeedAsync(factory);
        await LoginAsync(client, "execution.author");

        using var response = await client.GetAsync(
            $"/api/test-procedures?projectId={seed.ProjectId}&releaseId={seed.ReleaseId}" +
            "&scope=System&state=Approved&artifactKind=Procedure&page=1&pageSize=25");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var item = Assert.Single(body.GetProperty("items").EnumerateArray());
        Assert.Equal(seed.SystemRevisionId, item.GetProperty("revisionId").GetGuid());
        Assert.Equal("Procedure", item.GetProperty("artifactKind").GetString());
    }

    private static async Task<HttpResponseMessage> PostExecutionAsync(HttpClient client, Guid projectId,
        Guid buildId, Guid revisionId) =>
        await client.PostAsJsonAsync("/api/test-executions", new
        {
            projectId,
            artifactRevisionId = revisionId,
            softwareBuildId = buildId,
            retestOfExecutionId = (Guid?)null,
            outcome = "Pass",
            configuration = "Controlled rig",
            determination = "The observed result satisfies the expected result.",
            evidenceReference = "evidence/execution-authority.json",
            executedAt = DateTimeOffset.UtcNow,
        });

    private static async Task LoginAsync(HttpClient client, string userName)
    {
        var response = await client.PostAsJsonAsync("/api/auth/login", new
        {
            userName,
            password = AeroLinkApiFactory.MemberPassword,
        });
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
    }

    private sealed record Seed(Guid ProjectId, Guid ReleaseId, Guid BuildId, Guid CaseRevisionId,
        Guid SystemRevisionId, Guid SoftwareProcedureRevisionId);

    private static async Task<Seed> SeedAsync(AeroLinkApiFactory factory)
    {
        var now = DateTimeOffset.UtcNow;
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        var program = new ProgramRecord("Execution Authority Program", "EXA");
        var project = new ProjectRecord(program.Id, "Execution Authority Software", "Execution Authority Product");
        var release = new SoftwareRelease(project.Id, "1.0", false);
        var baseline = new CandidateBaseline("SW-01.00", 0, project.Id, release.Id, null,
            "Candidate", "cm.test", now);
        var account = new UserAccount("execution.author", "Execution Author", "execution.author@example.test",
            IdentityService.HashPassword(AeroLinkApiFactory.MemberPassword), now);
        db.AddRange(program, project, release, baseline, account);
        db.Add(new ProgramMembership(account.Id, program.Id, ProgramRole.TestEngineer, "execution-test", now));
        db.ProjectLadderConfigurations.Add(LegacyDefaultProjectLadderFactory.Create(project.Id, now));
        var scr = new SystemChangeRequest("SRCR-00901", 0, project.Id, release.Id,
            "Execution baseline authority", "P", "A", "S", "author", now);
        scr.AddRequirementChange("author", "SYSR-000901", 0, RequirementLevel.System,
            RequirementChangeKind.Introduce, "The system shall retain the controlled execution fixture.",
            "Baseline fixture authority.", "Analysis", now);
        scr.SubmitForReview("author", [new ApproverSelection("reviewer", "Reviewer")], now);
        scr.ApproveActiveStage("reviewer", now);
        db.Add(scr);

        var caseArtifact = new TestProcedure(project.Id, "HLRTC-000901", "Execution case",
            "test.engineer", now, TestProcedureLevel.HighLevel);
        var caseRevision = new TestProcedureRevision(caseArtifact.Id, 0,
            "Verify execution", "Preconditions", "Steps", "Expected",
            TestProcedureState.Approved, "test.engineer", now);
        var systemArtifact = new TestProcedure(project.Id, "SYSTP-000901", "System procedure",
            "test.engineer", now, TestProcedureLevel.System);
        var systemRevision = new TestProcedureRevision(systemArtifact.Id, 0,
            "Verify system execution", "Preconditions", "Steps", "Expected",
            TestProcedureState.Approved, "test.engineer", now);
        // A software Procedure needs the Draft-0 header path, then approval, with the full Procedure
        // vocabulary.
        var softwareProcedure = new TestProcedure(project.Id, "HLRTP-000901", "Software procedure",
            "test.engineer", now, TestProcedureLevel.HighLevel,
            artifactKind: VerificationArtifactKind.Procedure,
            parentKind: VerificationProcedureParentKind.Derived);
        var softwareRevision = new TestProcedureRevision(softwareProcedure.Id, 0,
            "Execute software procedure", "Procedure setup", "Procedure steps", "Expected observation",
            TestProcedureState.Draft, "test.engineer", now,
            environmentSetup: "Procedure setup", testData: "Controlled data",
            orderedSteps: "Procedure steps", expectedObservations: "Expected observation",
            cleanup: "Restore fixture", toolingAutomation: "Qualified runner",
            parentKind: VerificationProcedureParentKind.Derived,
            derivedRationale: "Standalone fixture procedure for execution-authority tests.");
        db.AddRange(caseArtifact, caseRevision, systemArtifact, systemRevision,
            softwareProcedure, softwareRevision,
            new BaselineTestProcedureSelection(baseline.Id, caseArtifact.Id, caseRevision.Id),
            new BaselineTestProcedureSelection(baseline.Id, systemArtifact.Id, systemRevision.Id),
            new BaselineTestProcedureSelection(baseline.Id, softwareProcedure.Id, softwareRevision.Id));
        await db.SaveChangesAsync();
        db.Entry(softwareRevision).Property(x => x.State).CurrentValue = TestProcedureState.Approved;
        await db.SaveChangesAsync();
        baseline.Select(scr, "cm.test", now);
        baseline.Freeze("cm.test", now);
        baseline.MarkRequirementsMaterialized("cm.test", new string('a', 64), 0, now);
        baseline.MarkTestProceduresMaterialized("cm.test", new string('b', 64), 3, now);
        await db.SaveChangesAsync();

        var build = new SoftwareBuild(project.Id, release.Id, baseline.Id, "B-EXA",
            "Execution authority build", "cm.test", now);
        db.Add(build);
        await db.SaveChangesAsync();
        return new Seed(project.Id, release.Id, build.Id, caseRevision.Id, systemRevision.Id,
            softwareRevision.Id);
    }
}
