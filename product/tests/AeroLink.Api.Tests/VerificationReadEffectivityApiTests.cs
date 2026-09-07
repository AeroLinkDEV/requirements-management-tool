using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AeroLink.Domain.Baselines;
using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Identity;
using AeroLink.Domain.Programs;
using AeroLink.Domain.Requirements;
using AeroLink.Domain.Verification;
using AeroLink.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace AeroLink.Api.Tests;

public sealed class VerificationReadEffectivityApiTests
{
    [Theory]
    [InlineData(TestProcedureLevel.HighLevel, "HLRTC", "HLRTP")]
    [InlineData(TestProcedureLevel.LowLevel, "LLRTC", "LLRTP")]
    public async Task Source_cases_open_from_exact_links_without_becoming_executable_selections(
        TestProcedureLevel level, string casePrefix, string procedurePrefix)
    {
        using var factory = new AeroLinkApiFactory(testLadderPolicy: ProcedureEnabledTestPolicy.Create());
        using var client = factory.CreateClient();
        Guid projectId, releaseId, nextReleaseId, caseId, case00, case01, case02, procedureId, procedure00, executionId,
            legacyExecutionId, unscopedExecutionId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var now = DateTimeOffset.UtcNow;
            var program = new ProgramRecord("Exact Case reads", "EXCASE");
            var project = new ProjectRecord(program.Id, "Verification reads", "FMS");
            var release = new SoftwareRelease(project.Id, "1.6", false);
            var next = new SoftwareRelease(project.Id, "1.7", false, release.Id);
            var user = new UserAccount("case.reader", "Case Reader", "case.reader@example.test",
                IdentityService.HashPassword(AeroLinkApiFactory.MemberPassword), now);
            db.AddRange(program, project, release, next, user,
                new ProgramMembership(user.Id, program.Id, ProgramRole.Engineer, "test.setup", now));
            var baseline = Baseline("SW-91.60", release, "SRCR-91600", null);
            var nextBaseline = Baseline("SW-91.70", next, "SRCR-91700", baseline.Id);
            var testCase = new TestProcedure(project.Id, casePrefix + "-091600", "Case", user.UserName, now, level);
            var procedure = new TestProcedure(project.Id, procedurePrefix + "-091600", "Procedure", user.UserName, now,
                level, null, VerificationArtifactKind.Procedure, VerificationProcedureParentKind.Allocated);
            var first = Revision(testCase.Id, 0, baseline.Id);
            var carriedSuccessor = Revision(testCase.Id, 1, baseline.Id);
            var future = Revision(testCase.Id, 2, nextBaseline.Id);
            var executable = Revision(procedure.Id, 0, baseline.Id, VerificationProcedureParentKind.Allocated);
            var futureExecutable = Revision(procedure.Id, 1, nextBaseline.Id, VerificationProcedureParentKind.Allocated);
            db.AddRange(testCase, procedure, first, carriedSuccessor, future, executable, futureExecutable,
                new TestCaseProcedureLink(first.Id, executable.Id),
                new TestCaseProcedureLink(carriedSuccessor.Id, executable.Id),
                new TestCaseProcedureLink(future.Id, futureExecutable.Id),
                new BaselineTestProcedureSelection(baseline.Id, procedure.Id, executable.Id),
                new BaselineTestProcedureSelection(nextBaseline.Id, procedure.Id, futureExecutable.Id));
            baseline.MarkTestProceduresMaterialized("cm", new string('b', 64), 1, now);
            nextBaseline.MarkTestProceduresMaterialized("cm", new string('c', 64), 1, now);
            var execution = new TestExecution(project.Id, executable.Id, null, null, TestOutcome.Pass,
                user.UserName, "Synthetic fixture", "Exact Procedure result", "fixture.json", now, now, release.Id);
            var build = new SoftwareBuild(project.Id, release.Id, baseline.Id, "SW-91.60", "Legacy result ownership", "cm", now);
            var legacyExecution = new TestExecution(project.Id, executable.Id, build.Id, null, TestOutcome.Pass,
                user.UserName, "Synthetic fixture", "Legacy Procedure result", "legacy.json", now, now);
            var unscopedExecution = new TestExecution(project.Id, executable.Id, null, null, TestOutcome.Pass,
                user.UserName, "Synthetic fixture", "Historical unscoped result", "unscoped.json", now, now);
            db.AddRange(execution, build, legacyExecution, unscopedExecution);
            await db.SaveChangesAsync();
            projectId = project.Id; releaseId = release.Id; nextReleaseId = next.Id;
            caseId = testCase.Id; case00 = first.Id; case01 = carriedSuccessor.Id; case02 = future.Id;
            procedureId = procedure.Id; procedure00 = executable.Id; executionId = execution.Id;
            legacyExecutionId = legacyExecution.Id; unscopedExecutionId = unscopedExecution.Id;

            var manifest = await TestProcedureEffectivity.ForBaselineAsync(db, baseline.Id, default);
            Assert.Single(manifest!.RevisionIds);
            Assert.Equal(executable.Id, manifest.RevisionIds.Single());
            Assert.DoesNotContain(first.Id, manifest.RevisionIds);

            CandidateBaseline Baseline(string number, SoftwareRelease build, string crNumber, Guid? predecessor)
            {
                var request = new SystemChangeRequest(crNumber, 0, project.Id, build.Id,
                    "Read fixture", "Problem", "Analysis", "Solution", "author", now);
                request.AddRequirementChange("author", "SYSR-091600", 0, RequirementLevel.System,
                    RequirementChangeKind.Introduce, "The system shall preserve exact reads.", "Identity", "Test", now);
                request.SubmitForReview("author", [new ApproverSelection("reviewer", "Reviewer")], now);
                request.ApproveActiveStage("reviewer", now);
                var result = new CandidateBaseline(number, 0, project.Id, build.Id, predecessor, number, "cm", now);
                result.Select(request, "cm", now); result.Freeze("cm", now);
                result.MarkRequirementsMaterialized("cm", new string('a', 64), 0, now);
                db.AddRange(request, result);
                return result;
            }
            TestProcedureRevision Revision(Guid id, int revision, Guid baselineId,
                VerificationProcedureParentKind parent = VerificationProcedureParentKind.Unspecified) =>
                new(id, revision, "Objective", "Ready", "Steps", "Expected", TestProcedureState.Approved,
                    // HOME's software Procedures were created by the governed Case-to-Procedure cutover.
                    parent == VerificationProcedureParentKind.Allocated
                        ? VerificationArtifactProfileSchema.GovernedMigrationActor : user.UserName,
                    now, effectiveBaselineId: baselineId, environmentSetup: "Ready",
                    orderedSteps: "Steps", expectedObservations: "Expected", parentKind: parent);
        }

        using var login = await client.PostAsJsonAsync("/api/auth/login",
            new { userName = "case.reader", password = AeroLinkApiFactory.MemberPassword });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        foreach (var revision in new[] { case00, case01 })
        {
            var artifact = await Read($"/api/artifacts/test-case/{caseId}?releaseId={releaseId}&revisionId={revision}");
            Assert.Equal(revision, artifact.GetProperty("details").GetProperty("revisionId").GetGuid());
            var history = await Read($"/api/test-cases/{caseId}/history?releaseId={releaseId}&revisionId={revision}");
            Assert.Equal(revision, history.GetProperty("selectedRevisionId").GetGuid());
            using var trace = await client.GetAsync($"/api/test-cases/{caseId}/trace?releaseId={releaseId}&revisionId={revision}");
            Assert.True(trace.IsSuccessStatusCode, await trace.Content.ReadAsStringAsync());
        }
        foreach (var path in new[] { $"/api/artifacts/test-case/{caseId}", $"/api/test-cases/{caseId}/history", $"/api/test-cases/{caseId}/trace" })
        {
            using var rejected = await client.GetAsync($"{path}?releaseId={releaseId}&revisionId={case02}");
            Assert.Equal(HttpStatusCode.NotFound, rejected.StatusCode);
            using var wrongArtifact = await client.GetAsync($"{path}?releaseId={releaseId}&revisionId={procedure00}");
            Assert.Equal(HttpStatusCode.NotFound, wrongArtifact.StatusCode);
        }
        var current = await Read($"/api/artifacts/test-case/{caseId}?releaseId={releaseId}");
        Assert.Equal(case01, current.GetProperty("details").GetProperty("revisionId").GetGuid());
        var futureArtifact = await Read($"/api/artifacts/test-case/{caseId}?releaseId={nextReleaseId}&revisionId={case02}");
        Assert.Equal(case02, futureArtifact.GetProperty("details").GetProperty("revisionId").GetGuid());
        var list = await Read($"/api/test-cases?projectId={projectId}&releaseId={releaseId}");
        var item = Assert.Single(list.GetProperty("items").EnumerateArray());
        Assert.Equal(case01, item.GetProperty("revisionId").GetGuid());
        var resultArtifact = await Read($"/api/artifacts/test-execution/{executionId}?releaseId={releaseId}");
        var related = Assert.Single(resultArtifact.GetProperty("related").EnumerateArray());
        Assert.Equal("test-procedure", related.GetProperty("kind").GetString());
        Assert.Equal(procedureId, related.GetProperty("id").GetGuid());
        Assert.Equal(procedure00, related.GetProperty("revisionId").GetGuid());
        await Read($"/api/artifacts/test-execution/{legacyExecutionId}?releaseId={releaseId}");
        foreach (var resultId in new[] { executionId, legacyExecutionId, unscopedExecutionId })
        {
            using var crossRelease = await client.GetAsync($"/api/artifacts/test-execution/{resultId}?releaseId={nextReleaseId}");
            Assert.Equal(HttpStatusCode.NotFound, crossRelease.StatusCode);
        }
        using var noInventedRelease = await client.GetAsync($"/api/artifacts/test-execution/{unscopedExecutionId}?releaseId={releaseId}");
        Assert.Equal(HttpStatusCode.NotFound, noInventedRelease.StatusCode);
        await Read($"/api/artifacts/test-execution/{unscopedExecutionId}");

        async Task<JsonElement> Read(string path)
        {
            using var response = await client.GetAsync(path);
            Assert.True(response.IsSuccessStatusCode, $"{path}: {await response.Content.ReadAsStringAsync()}");
            return await response.Content.ReadFromJsonAsync<JsonElement>();
        }
    }
}
