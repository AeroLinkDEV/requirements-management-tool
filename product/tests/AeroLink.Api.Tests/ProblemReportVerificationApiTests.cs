using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using AeroLink.Domain.Baselines;
using AeroLink.Domain.Identity;
using AeroLink.Domain.Programs;
using AeroLink.Domain.Releases;
using AeroLink.Domain.Requirements;
using AeroLink.Domain.Verification;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AeroLink.Api.Tests;

public sealed class ProblemReportVerificationApiTests
{
    private sealed record Fixture(
        Guid ReportId, long ReportVersion, Guid ManualReportId, long ManualReportVersion,
        Guid ProjectId, Guid TargetBuildId, Guid TargetRevisionId, Guid OriginExecutionId,
        Guid WrongReleaseId, Guid WrongBuildId,
        Guid WrongProcedureExecutionId, Guid NoRetestExecutionId, Guid HistoricalExecutionId,
        Guid EqualCorrectionTimeExecutionId,
        Guid WrongBuildExecutionId, Guid FailedExecutionId, Guid BlockedExecutionId, Guid WrongProjectExecutionId);

    private static async Task<Fixture> SeedAsync(AeroLinkApiFactory factory,
        string reportedBy = "admin", string responsibleEngineerId = "admin")
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        var start = new DateTimeOffset(2026, 8, 10, 12, 0, 0, TimeSpan.Zero);
        var program = new ProgramRecord("PR closure chain", "PVC");
        var project = new ProjectRecord(program.Id, "FMS", "Closure-chain FMS");
        var originRelease = new SoftwareRelease(project.Id, "1.5", false);
        var targetRelease = new SoftwareRelease(project.Id, "1.6", false, originRelease.Id);
        var wrongRelease = new SoftwareRelease(project.Id, "1.7", false, targetRelease.Id);
        var originBaseline = new CandidateBaseline("SW-04.49", 0, project.Id, originRelease.Id, null, "Origin", "cm", start);
        var targetBaseline = new CandidateBaseline("SW-04.50", 0, project.Id, targetRelease.Id, originBaseline.Id, "Target", "cm", start);
        var wrongBaseline = new CandidateBaseline("SW-04.51", 0, project.Id, wrongRelease.Id, targetBaseline.Id, "Wrong", "cm", start);
        var originBuild = new SoftwareBuild(project.Id, originRelease.Id, originBaseline.Id, "SW-04.49", "Origin build", "cm", start);
        var targetBuild = new SoftwareBuild(project.Id, targetRelease.Id, targetBaseline.Id, "SW-04.50", "Corrective build", "cm", start);
        var wrongBuild = new SoftwareBuild(project.Id, wrongRelease.Id, wrongBaseline.Id, "SW-04.51", "Wrong build", "cm", start);

        var procedure = new TestProcedure(project.Id, "SYSTP-004490", "Correct source selection", "test", start, TestProcedureLevel.System);
        var failedRevision = new TestProcedureRevision(procedure.Id, 0, "Original objective", "Preconditions", "Steps", "Expected",
            TestProcedureState.Approved, "test", start, effectiveBaselineId: originBaseline.Id);
        var targetRevision = new TestProcedureRevision(procedure.Id, 1, "Corrective objective", "Preconditions", "Revised steps", "Expected",
            TestProcedureState.Approved, "test", start.AddMinutes(3), sourceTestChangeRequestId: Guid.NewGuid(), effectiveBaselineId: targetBaseline.Id);
        var otherProcedure = new TestProcedure(project.Id, "SYSTP-004491", "Unrelated function", "test", start, TestProcedureLevel.System);
        var otherRevision = new TestProcedureRevision(otherProcedure.Id, 0, "Other objective", "Preconditions", "Steps", "Expected",
            TestProcedureState.Approved, "test", start, effectiveBaselineId: targetBaseline.Id);

        var originFailure = Execution(project.Id, failedRevision.Id, originBuild.Id, null, TestOutcome.Fail, start, originRelease.Id);
        var report = Report(project.Id, targetRelease.Id, "PR-04490", start.AddMinutes(1),
            reportedBy, responsibleEngineerId);
        ProgressToVerifying(report, start.AddMinutes(5));
        var manual = Report(project.Id, targetRelease.Id, "PR-04491", start.AddMinutes(1));
        ProgressToVerifying(manual, start.AddMinutes(5));

        var wrongProcedure = Execution(project.Id, otherRevision.Id, targetBuild.Id, originFailure.Id, TestOutcome.Pass, start.AddMinutes(10), targetRelease.Id);
        var noRetest = Execution(project.Id, targetRevision.Id, targetBuild.Id, null, TestOutcome.Pass, start.AddMinutes(10), targetRelease.Id);
        // Simulates an Awaiting-SQA record produced by the historical weak endpoint. Closure must revalidate
        // it instead of trusting state alone.
        manual.RecordResolutionVerification("admin", noRetest.Id, start.AddMinutes(11));
        var historical = Execution(project.Id, targetRevision.Id, targetBuild.Id, originFailure.Id, TestOutcome.Pass, start.AddMinutes(4), targetRelease.Id);
        var equalCorrectionTime = Execution(project.Id, targetRevision.Id, targetBuild.Id, originFailure.Id, TestOutcome.Pass, start.AddMinutes(5), targetRelease.Id);
        var wrongBuildExecution = Execution(project.Id, targetRevision.Id, wrongBuild.Id, originFailure.Id, TestOutcome.Pass, start.AddMinutes(10), wrongRelease.Id);
        var failed = Execution(project.Id, targetRevision.Id, targetBuild.Id, originFailure.Id, TestOutcome.Fail, start.AddMinutes(10), targetRelease.Id);
        var blocked = Execution(project.Id, targetRevision.Id, targetBuild.Id, originFailure.Id, TestOutcome.Blocked, start.AddMinutes(10), targetRelease.Id);
        var otherProject = new ProjectRecord(program.Id, "Other", "Other Project");
        var quality = new UserAccount("closure.quality", "Closure Quality", "closure.quality@example.test",
            IdentityService.HashPassword(AeroLinkApiFactory.MemberPassword), start);
        var engineer = new UserAccount("closure.engineer", "Closure Engineer", "closure.engineer@example.test",
            IdentityService.HashPassword(AeroLinkApiFactory.MemberPassword), start);
        var approver = new UserAccount("closure.approver", "Closure Approver", "closure.approver@example.test",
            IdentityService.HashPassword(AeroLinkApiFactory.MemberPassword), start);
        var configurationManager = new UserAccount("closure.cm", "Closure Configuration Manager", "closure.cm@example.test",
            IdentityService.HashPassword(AeroLinkApiFactory.MemberPassword), start);
        var programManager = new UserAccount("closure.manager", "Closure Program Manager", "closure.manager@example.test",
            IdentityService.HashPassword(AeroLinkApiFactory.MemberPassword), start);
        var wrongProject = Execution(otherProject.Id, targetRevision.Id, targetBuild.Id, originFailure.Id, TestOutcome.Pass, start.AddMinutes(10), targetRelease.Id);

        db.AddRange(program, project, otherProject, originRelease, targetRelease, wrongRelease, quality, engineer,
            approver, configurationManager, programManager,
            new ProgramMembership(quality.Id, program.Id, ProgramRole.SoftwareQualityAnalyst, "test.setup", start),
            new ProgramMembership(quality.Id, program.Id, ProgramRole.Reviewer, "test.setup", start),
            new ProgramMembership(engineer.Id, program.Id, ProgramRole.Engineer, "test.setup", start),
            new ProgramMembership(approver.Id, program.Id, ProgramRole.Approver, "test.setup", start),
            new ProgramMembership(configurationManager.Id, program.Id, ProgramRole.ConfigurationManager, "test.setup", start),
            new ProgramMembership(programManager.Id, program.Id, ProgramRole.ProgramManager, "test.setup", start),
            originBaseline, targetBaseline, wrongBaseline, originBuild, targetBuild, wrongBuild,
            procedure, failedRevision, targetRevision, otherProcedure, otherRevision,
            originFailure, report, manual, wrongProcedure, noRetest, historical, equalCorrectionTime, wrongBuildExecution, failed, blocked, wrongProject,
            new BaselineTestProcedureSelection(originBaseline.Id, procedure.Id, failedRevision.Id),
            new BaselineTestProcedureSelection(targetBaseline.Id, procedure.Id, targetRevision.Id),
            new BaselineTestProcedureSelection(targetBaseline.Id, otherProcedure.Id, otherRevision.Id),
            new BaselineTestProcedureSelection(wrongBaseline.Id, procedure.Id, targetRevision.Id),
            ProblemReportRelationshipPolicy.CreateControlled(report.Id, "TestExecution", originFailure.Id,
                ProblemReportRelationshipPolicy.OriginatingFailure, ProblemReportRelationshipProducer.FailureCreationWorkflow, "admin", start),
            ProblemReportRelationshipPolicy.CreateControlled(report.Id, "Release", targetRelease.Id,
                ProblemReportRelationshipPolicy.BuildScope, ProblemReportRelationshipProducer.TargetBuildWorkflow, "admin", start));
        await db.SaveChangesAsync();
        var baselineIds = new[] { originBaseline.Id, targetBaseline.Id, wrongBaseline.Id };
        await db.CandidateBaselines.Where(item => baselineIds.Contains(item.Id)).ExecuteUpdateAsync(update => update
            .SetProperty(item => item.RequirementsMaterializedAt, start)
            .SetProperty(item => item.TestProceduresMaterializedAt, start)
            .SetProperty(item => item.TestProceduresHash, "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"));
        return new(report.Id, report.Version, manual.Id, manual.Version, project.Id, targetBuild.Id,
            targetRevision.Id, originFailure.Id, wrongRelease.Id, wrongBuild.Id, wrongProcedure.Id, noRetest.Id, historical.Id,
            equalCorrectionTime.Id, wrongBuildExecution.Id, failed.Id, blocked.Id, wrongProject.Id);
    }

    [Fact]
    public async Task Verify_rejects_unrelated_stale_wrong_build_and_unscoped_evidence_then_accepts_the_effective_retest_once()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        await ProblemReportApiTests.BootstrapAndLoginAsync(client);
        var fixture = await SeedAsync(factory);

        await RejectAsync(client, fixture.ReportId, fixture.ReportVersion, fixture.WrongProjectExecutionId, "pr_verification_wrong_project");
        await RejectAsync(client, fixture.ReportId, fixture.ReportVersion, fixture.WrongProcedureExecutionId, "pr_verification_wrong_procedure");
        await RejectAsync(client, fixture.ReportId, fixture.ReportVersion, fixture.NoRetestExecutionId, "pr_verification_not_successor");
        await RejectAsync(client, fixture.ReportId, fixture.ReportVersion, fixture.HistoricalExecutionId, "pr_verification_not_successor");
        await RejectAsync(client, fixture.ReportId, fixture.ReportVersion, fixture.EqualCorrectionTimeExecutionId, "pr_verification_not_successor");
        await RejectAsync(client, fixture.ReportId, fixture.ReportVersion, fixture.WrongBuildExecutionId, "pr_verification_wrong_build");
        await RejectAsync(client, fixture.ReportId, fixture.ReportVersion, fixture.FailedExecutionId, "pr_verification_not_pass");
        await RejectAsync(client, fixture.ReportId, fixture.ReportVersion, fixture.BlockedExecutionId, "pr_verification_not_pass");
        await RejectAsync(client, fixture.ManualReportId, fixture.ManualReportVersion, fixture.NoRetestExecutionId, "pr_verification_scope_unknown");
        using var quality = factory.CreateClient();
        await LoginAsync(quality, "closure.quality");
        using (var closure = await quality.PostAsJsonAsync($"/api/problem-reports/{fixture.ManualReportId}/closure/approve", new
        {
            expectedVersion = fixture.ManualReportVersion
        }))
        {
            Assert.Equal(HttpStatusCode.Conflict, closure.StatusCode);
            Assert.Equal("pr_verification_scope_unknown",
                (await closure.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        }

        var corrective = await client.GetFromJsonAsync<JsonElement>($"/api/problem-reports/{fixture.ReportId}/corrective-action");
        Assert.True(corrective.GetProperty("available").GetBoolean());
        Assert.Equal(fixture.TargetRevisionId, corrective.GetProperty("procedureRevisionId").GetGuid());
        Assert.Equal(fixture.OriginExecutionId, corrective.GetProperty("executionId").GetGuid());

        using (var notLater = await client.PostAsJsonAsync("/api/test-executions", new
        {
            projectId = fixture.ProjectId,
            procedureRevisionId = fixture.TargetRevisionId,
            softwareBuildId = fixture.TargetBuildId,
            retestOfExecutionId = fixture.OriginExecutionId,
            outcome = "Pass",
            configuration = "Corrective rig",
            determination = "A simultaneous result is not a successor.",
            evidenceReference = "controlled://pr-449/not-later",
            executedAt = new DateTimeOffset(2026, 8, 10, 12, 0, 0, TimeSpan.Zero),
        }))
        {
            Assert.Equal(HttpStatusCode.BadRequest, notLater.StatusCode);
            Assert.Equal("retest_not_successor",
                (await notLater.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        }

        // The target build carries revision 1 while the failure executed revision 0. Recording the retest
        // through the public API proves stable procedure lineage is accepted without reviving the obsolete
        // exact-revision restriction.
        using var recorded = await client.PostAsJsonAsync("/api/test-executions", new
        {
            projectId = fixture.ProjectId,
            procedureRevisionId = fixture.TargetRevisionId,
            softwareBuildId = fixture.TargetBuildId,
            retestOfExecutionId = fixture.OriginExecutionId,
            outcome = "Pass",
            configuration = "Corrective rig",
            determination = "The corrected behavior satisfies the effective successor procedure.",
            evidenceReference = "controlled://pr-449/successor",
            executedAt = new DateTimeOffset(2026, 8, 10, 12, 10, 0, TimeSpan.Zero),
        });
        Assert.Equal(HttpStatusCode.Created, recorded.StatusCode);
        var executionId = (await recorded.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        using var accepted = await client.PostAsJsonAsync($"/api/problem-reports/{fixture.ReportId}/verify", new
        {
            expectedVersion = fixture.ReportVersion, testExecutionId = executionId
        });
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        Assert.Equal("AwaitingSqaClosure", (await accepted.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("state").GetString());

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        var links = await db.ProblemReportLinks.AsNoTracking().Where(item => item.ProblemReportId == fixture.ReportId
            && item.Relationship == ProblemReportRelationshipPolicy.ResolutionVerification).ToListAsync();
        var link = Assert.Single(links);
        Assert.Equal(executionId, link.ArtifactId);
        Assert.Equal("admin", link.AddedBy);
    }

    [Fact]
    public async Task Controlled_edit_invalidates_the_exact_candidate_and_reverification_closes_only_the_new_cycle()
    {
        using var factory = new AeroLinkApiFactory();
        using var engineer = factory.CreateClient();
        await ProblemReportApiTests.BootstrapAndLoginAsync(engineer);
        var fixture = await SeedAsync(factory);
        var first = await SelectCandidateAsync(engineer, fixture, fixture.TargetBuildId, targetReleaseId: null);
        var selected = await engineer.GetFromJsonAsync<JsonElement>($"/api/problem-reports/{fixture.ReportId}");
        var firstCandidate = Assert.Single(selected.GetProperty("closureCandidates").EnumerateArray());
        Assert.Equal("Pending", firstCandidate.GetProperty("state").GetString());
        Assert.Equal(first.ExecutionId, firstCandidate.GetProperty("verificationExecutionId").GetGuid());
        Assert.Equal(selected.GetProperty("version").GetInt64(), firstCandidate.GetProperty("reportVersion").GetInt64());

        await ProblemReportCheckoutApiTests.EditUnderCheckoutAsync(engineer, fixture.ReportId, draft =>
        {
            draft["correctiveAction"] = "A materially revised correction that requires new verification.";
            draft["rootCause"] = "The revised analysis found a different scheduling fault.";
            draft["systemAircraftImpact"] = "The revised impact includes degraded guidance availability.";
        });
        var invalidated = await engineer.GetFromJsonAsync<JsonElement>($"/api/problem-reports/{fixture.ReportId}");
        Assert.Equal("Verifying", invalidated.GetProperty("state").GetString());
        Assert.Equal(JsonValueKind.Null, invalidated.GetProperty("resolutionVerificationExecutionId").ValueKind);
        Assert.Equal(0, invalidated.GetProperty("testEvidence").GetArrayLength());
        Assert.Contains(invalidated.GetProperty("links").EnumerateArray(), link =>
            link.GetProperty("relationship").GetString() == ProblemReportRelationshipPolicy.ResolutionVerification
            && link.GetProperty("artifactId").GetGuid() == first.ExecutionId);
        var invalidatedCandidate = Assert.Single(invalidated.GetProperty("closureCandidates").EnumerateArray());
        Assert.Equal("Invalidated", invalidatedCandidate.GetProperty("state").GetString());
        Assert.Equal("DetailsCheckedIn", invalidatedCandidate.GetProperty("invalidationReason").GetString());
        Assert.Contains(invalidated.GetProperty("revisions").EnumerateArray(), revision =>
            revision.GetProperty("eventType").GetString() == "ClosureVerificationInvalidatedByChange");

        using var quality = factory.CreateClient();
        await LoginAsync(quality, "closure.quality");
        using (var staleClosure = await quality.PostAsJsonAsync($"/api/problem-reports/{fixture.ReportId}/closure/approve",
            new { expectedVersion = first.ReportVersion }))
        {
            Assert.Equal(HttpStatusCode.Conflict, staleClosure.StatusCode);
            Assert.Equal("pr_closure_candidate_stale",
                (await staleClosure.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        }

        var second = await SelectCandidateAsync(engineer, fixture, fixture.TargetBuildId, targetReleaseId: null);
        using var closed = await quality.PostAsJsonAsync($"/api/problem-reports/{fixture.ReportId}/closure/approve",
            new { expectedVersion = second.ReportVersion });
        Assert.Equal(HttpStatusCode.OK, closed.StatusCode);
        var final = await engineer.GetFromJsonAsync<JsonElement>($"/api/problem-reports/{fixture.ReportId}");
        Assert.Equal("Closed", final.GetProperty("state").GetString());
        var cycles = final.GetProperty("closureCandidates").EnumerateArray().ToList();
        Assert.Equal(2, cycles.Count);
        Assert.Equal("Approved", cycles[0].GetProperty("state").GetString());
        Assert.Equal("Invalidated", cycles[1].GetProperty("state").GetString());
        Assert.NotEqual(cycles[0].GetProperty("manifestHash").GetString(), cycles[1].GetProperty("manifestHash").GetString());

        var approvedCandidateId = cycles[0].GetProperty("id").GetGuid();
        var package = await engineer.GetFromJsonAsync<JsonElement>($"/api/problem-reports/{fixture.ReportId}/closure-package");
        Assert.Equal(approvedCandidateId, package.GetProperty("snapshot").GetProperty("id").GetGuid());
        Assert.Equal("FrozenAtApproval", package.GetProperty("snapshot").GetProperty("packageProvenance").GetString());
        Assert.Equal("SoftwareQualityAnalyst", package.GetProperty("snapshot").GetProperty("approvalAuthority").GetString());
        var firstPackageHash = package.GetProperty("snapshot").GetProperty("closurePackageHash").GetString();
        Assert.Equal("closure.quality", package.GetProperty("package").GetProperty("closure").GetProperty("approvedBy").GetString());
        Assert.Equal("SoftwareQualityAnalyst", package.GetProperty("package").GetProperty("closure").GetProperty("authority").GetString());
        Assert.Contains(package.GetProperty("package").GetProperty("history").EnumerateArray(), revision =>
            revision.GetProperty("eventType").GetString() == "ClosureApproved");
        var frozenPackage = package.GetProperty("package").GetRawText();

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var persistedFirst = await db.ProblemReportClosureCandidates.AsNoTracking()
                .SingleAsync(item => item.Id == firstCandidate.GetProperty("id").GetGuid());
            Assert.Contains("Correct and retest.", persistedFirst.ReportSnapshotJson);
            Assert.DoesNotContain("materially revised correction", persistedFirst.ReportSnapshotJson);
            var persistedApproved = await db.ProblemReportClosureCandidates.AsNoTracking()
                .SingleAsync(item => item.Id == approvedCandidateId);
            Assert.False(string.IsNullOrWhiteSpace(persistedApproved.ClosurePackageJson));
            db.ProblemReports.Add(new ProblemReport(fixture.ProjectId, $"PR-UNRELATED-{Guid.NewGuid():N}",
                "Unrelated activity", "Must not alter a frozen package.", "", "admin", DateTimeOffset.UtcNow));
            await db.SaveChangesAsync();
        }
        var repeated = await engineer.GetFromJsonAsync<JsonElement>(
            $"/api/problem-reports/{fixture.ReportId}/closure-package?candidateId={approvedCandidateId}");
        Assert.Equal(firstPackageHash, repeated.GetProperty("snapshot").GetProperty("closurePackageHash").GetString());
        Assert.Equal(frozenPackage, repeated.GetProperty("package").GetRawText());

        using var reopened = await engineer.PostAsJsonAsync($"/api/problem-reports/{fixture.ReportId}/reopen",
            new { expectedVersion = final.GetProperty("version").GetInt64(), rationale = "A field report requires a second controlled closure cycle." });
        Assert.Equal(HttpStatusCode.OK, reopened.StatusCode);
        var reopenVersion = (await reopened.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("version").GetInt64();
        using var implementing = await engineer.PostAsJsonAsync($"/api/problem-reports/{fixture.ReportId}/implementation",
            new { expectedVersion = reopenVersion });
        Assert.Equal(HttpStatusCode.OK, implementing.StatusCode);
        var implementingVersion = (await implementing.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("version").GetInt64();
        using var resolution = await engineer.PostAsJsonAsync($"/api/problem-reports/{fixture.ReportId}/resolution",
            new { expectedVersion = implementingVersion, correctiveAction = "Apply and verify the follow-on correction." });
        Assert.Equal(HttpStatusCode.OK, resolution.StatusCode);
        var third = await SelectCandidateAsync(engineer, fixture, fixture.TargetBuildId, targetReleaseId: null);
        using var reclosed = await quality.PostAsJsonAsync($"/api/problem-reports/{fixture.ReportId}/closure/approve",
            new { expectedVersion = third.ReportVersion });
        Assert.Equal(HttpStatusCode.OK, reclosed.StatusCode);
        var latestPackage = await engineer.GetFromJsonAsync<JsonElement>($"/api/problem-reports/{fixture.ReportId}/closure-package");
        Assert.Equal(1, latestPackage.GetProperty("snapshot").GetProperty("reportRevision").GetInt32());
        Assert.NotEqual(firstPackageHash, latestPackage.GetProperty("snapshot").GetProperty("closurePackageHash").GetString());
        var priorPackage = await engineer.GetFromJsonAsync<JsonElement>(
            $"/api/problem-reports/{fixture.ReportId}/closure-package?candidateId={approvedCandidateId}");
        Assert.Equal(firstPackageHash, priorPackage.GetProperty("snapshot").GetProperty("closurePackageHash").GetString());
        Assert.Equal(frozenPackage, priorPackage.GetProperty("package").GetRawText());
    }

    [Fact]
    public async Task Retarget_and_reassignment_each_invalidate_current_evidence_without_deleting_history()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        await ProblemReportApiTests.BootstrapAndLoginAsync(client);
        var fixture = await SeedAsync(factory);
        var first = await SelectCandidateAsync(client, fixture, fixture.TargetBuildId, targetReleaseId: null);

        using var retarget = await client.PostAsJsonAsync($"/api/problem-reports/{fixture.ReportId}/target-build",
            new { expectedVersion = first.ReportVersion, targetReleaseId = fixture.WrongReleaseId });
        Assert.Equal(HttpStatusCode.OK, retarget.StatusCode);
        Assert.Equal("Verifying", (await retarget.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("state").GetString());

        var second = await SelectCandidateAsync(client, fixture, fixture.WrongBuildId, fixture.WrongReleaseId);
        using var reassign = await client.PostAsJsonAsync($"/api/problem-reports/{fixture.ReportId}/owner",
            new { expectedVersion = second.ReportVersion, responsibleEngineerId = "closure.engineer" });
        Assert.Equal(HttpStatusCode.OK, reassign.StatusCode);
        var detail = await client.GetFromJsonAsync<JsonElement>($"/api/problem-reports/{fixture.ReportId}");
        Assert.Equal("Verifying", detail.GetProperty("state").GetString());
        Assert.Equal("closure.engineer", detail.GetProperty("responsibleEngineerId").GetString());
        Assert.Equal(0, detail.GetProperty("testEvidence").GetArrayLength());
        Assert.Equal(2, detail.GetProperty("links").EnumerateArray().Count(link =>
            link.GetProperty("relationship").GetString() == ProblemReportRelationshipPolicy.ResolutionVerification));
        Assert.Equal(2, detail.GetProperty("closureCandidates").EnumerateArray().Count(candidate =>
            candidate.GetProperty("state").GetString() == "Invalidated"));
        Assert.Equal(2, detail.GetProperty("revisions").EnumerateArray().Count(revision =>
            revision.GetProperty("eventType").GetString() == "ClosureVerificationInvalidatedByChange"));
    }

    [Fact]
    public async Task Concurrent_SQA_approval_and_controlled_check_in_allow_exactly_one_outcome()
    {
        using var factory = new AeroLinkApiFactory();
        using var engineer = factory.CreateClient();
        await ProblemReportApiTests.BootstrapAndLoginAsync(engineer);
        var fixture = await SeedAsync(factory);
        var candidate = await SelectCandidateAsync(engineer, fixture, fixture.TargetBuildId, targetReleaseId: null);
        using var quality = factory.CreateClient();
        await LoginAsync(quality, "closure.quality");

        using var checkout = await engineer.PostAsJsonAsync("/api/controlled-editing/checkout",
            new { artifactType = "ProblemReport", artifactId = fixture.ReportId, leaseMinutes = 15 });
        Assert.True(checkout.IsSuccessStatusCode, await checkout.Content.ReadAsStringAsync());
        var session = await checkout.Content.ReadFromJsonAsync<JsonElement>();
        var sessionId = session.GetProperty("id").GetGuid();
        var draft = JsonNode.Parse(session.GetProperty("draftJson").GetString()!)!.AsObject();
        draft["correctiveAction"] = "A concurrent correction that must not close under old evidence.";
        using var autosave = await engineer.PutAsJsonAsync($"/api/controlled-editing/sessions/{sessionId}/autosave",
            new { expectedVersion = session.GetProperty("version").GetInt64(), draftJson = draft.ToJsonString(), leaseMinutes = 15 });
        Assert.Equal(HttpStatusCode.OK, autosave.StatusCode);
        var sessionVersion = (await autosave.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("version").GetInt64();

        var closureTask = quality.PostAsJsonAsync($"/api/problem-reports/{fixture.ReportId}/closure/approve",
            new { expectedVersion = candidate.ReportVersion });
        var checkInTask = engineer.PostAsJsonAsync($"/api/controlled-editing/sessions/{sessionId}/check-in",
            new { expectedVersion = sessionVersion });
        await Task.WhenAll(closureTask, checkInTask);
        using var closure = await closureTask;
        using var checkIn = await checkInTask;
        Assert.Equal(1, new[] { closure.IsSuccessStatusCode, checkIn.IsSuccessStatusCode }.Count(success => success));

        var detail = await engineer.GetFromJsonAsync<JsonElement>($"/api/problem-reports/{fixture.ReportId}");
        var persistedCandidate = Assert.Single(detail.GetProperty("closureCandidates").EnumerateArray());
        if (closure.IsSuccessStatusCode)
        {
            Assert.Equal("Closed", detail.GetProperty("state").GetString());
            Assert.Equal("Approved", persistedCandidate.GetProperty("state").GetString());
        }
        else
        {
            Assert.Equal("Verifying", detail.GetProperty("state").GetString());
            Assert.Equal("Invalidated", persistedCandidate.GetProperty("state").GetString());
        }
    }

    [Fact]
    public async Task Concurrent_closure_approvals_freeze_exactly_one_package()
    {
        using var factory = new AeroLinkApiFactory();
        using var engineer = factory.CreateClient();
        await ProblemReportApiTests.BootstrapAndLoginAsync(engineer);
        var fixture = await SeedAsync(factory);
        var candidate = await SelectCandidateAsync(engineer, fixture, fixture.TargetBuildId, targetReleaseId: null);
        using var qualityOne = factory.CreateClient();
        using var qualityTwo = factory.CreateClient();
        await LoginAsync(qualityOne, "closure.quality");
        await LoginAsync(qualityTwo, "closure.quality");

        var firstTask = qualityOne.PostAsJsonAsync($"/api/problem-reports/{fixture.ReportId}/closure/approve",
            new { expectedVersion = candidate.ReportVersion });
        var secondTask = qualityTwo.PostAsJsonAsync($"/api/problem-reports/{fixture.ReportId}/closure/approve",
            new { expectedVersion = candidate.ReportVersion });
        await Task.WhenAll(firstTask, secondTask);
        using var first = await firstTask;
        using var second = await secondTask;
        Assert.Equal(1, new[] { first.IsSuccessStatusCode, second.IsSuccessStatusCode }.Count(success => success));

        var detail = await engineer.GetFromJsonAsync<JsonElement>($"/api/problem-reports/{fixture.ReportId}");
        var frozen = Assert.Single(detail.GetProperty("closureCandidates").EnumerateArray());
        Assert.Equal("Approved", frozen.GetProperty("state").GetString());
        Assert.Equal("FrozenAtApproval", frozen.GetProperty("packageProvenance").GetString());
        Assert.Equal(64, frozen.GetProperty("closurePackageHash").GetString()!.Length);
    }

    [Fact]
    public async Task Closure_requires_live_Software_Quality_authority_and_never_uses_generic_or_administrator_power()
    {
        using var factory = new AeroLinkApiFactory();
        using var administrator = factory.CreateClient();
        await ProblemReportApiTests.BootstrapAndLoginAsync(administrator);
        var fixture = await SeedAsync(factory);
        var candidate = await SelectCandidateAsync(administrator, fixture, fixture.TargetBuildId, targetReleaseId: null);

        foreach (var userName in new[] { "closure.approver", "closure.cm", "closure.manager", "closure.engineer" })
        {
            using var unauthorized = factory.CreateClient();
            await LoginAsync(unauthorized, userName);
            var detail = await unauthorized.GetFromJsonAsync<JsonElement>($"/api/problem-reports/{fixture.ReportId}");
            Assert.False(detail.GetProperty("capabilities").GetProperty("canApproveSqaClosure").GetBoolean());
            using var response = await unauthorized.PostAsJsonAsync($"/api/problem-reports/{fixture.ReportId}/closure/approve",
                new { expectedVersion = candidate.ReportVersion });
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }

        using (var administratorAttempt = await administrator.PostAsJsonAsync(
            $"/api/problem-reports/{fixture.ReportId}/closure/approve",
            new { expectedVersion = candidate.ReportVersion }))
            Assert.Equal(HttpStatusCode.Forbidden, administratorAttempt.StatusCode);

        using var quality = factory.CreateClient();
        await LoginAsync(quality, "closure.quality");
        var authorized = await quality.GetFromJsonAsync<JsonElement>($"/api/problem-reports/{fixture.ReportId}");
        Assert.True(authorized.GetProperty("capabilities").GetProperty("canApproveSqaClosure").GetBoolean());

        Guid qualityId;
        Guid programId;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            qualityId = await db.UserAccounts.Where(item => item.UserName == "closure.quality").Select(item => item.Id).SingleAsync();
            var delegatorId = await db.UserAccounts.Where(item => item.UserName == "closure.manager").Select(item => item.Id).SingleAsync();
            programId = await db.Projects.Where(item => item.Id == fixture.ProjectId).Select(item => item.ProgramId).SingleAsync();
            await db.ProgramMemberships.Where(item => item.UserId == qualityId && item.ProgramId == programId
                && item.Role == ProgramRole.SoftwareQualityAnalyst).ExecuteDeleteAsync();
            var now = DateTimeOffset.UtcNow;
            db.RoleDelegations.Add(new RoleDelegation(programId, delegatorId, qualityId,
                ProgramRole.SoftwareQualityAnalyst, now.AddHours(-2), now.AddHours(-1),
                "Expired quality coverage retained for audit.", "test.setup", now.AddHours(-3)));
            await db.SaveChangesAsync();
        }

        var revoked = await quality.GetFromJsonAsync<JsonElement>($"/api/problem-reports/{fixture.ReportId}");
        Assert.False(revoked.GetProperty("capabilities").GetProperty("canApproveSqaClosure").GetBoolean());
        using (var revokedAttempt = await quality.PostAsJsonAsync($"/api/problem-reports/{fixture.ReportId}/closure/approve",
            new { expectedVersion = candidate.ReportVersion }))
            Assert.Equal(HttpStatusCode.Forbidden, revokedAttempt.StatusCode);

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            db.ProgramMemberships.Add(new ProgramMembership(qualityId, programId,
                ProgramRole.SoftwareQualityAnalyst, "test.restore", DateTimeOffset.UtcNow));
            await db.SaveChangesAsync();
        }

        var restored = await quality.GetFromJsonAsync<JsonElement>($"/api/problem-reports/{fixture.ReportId}");
        Assert.True(restored.GetProperty("capabilities").GetProperty("canApproveSqaClosure").GetBoolean());
        using var closed = await quality.PostAsJsonAsync($"/api/problem-reports/{fixture.ReportId}/closure/approve",
            new { expectedVersion = candidate.ReportVersion });
        Assert.Equal(HttpStatusCode.OK, closed.StatusCode);
        var package = await quality.GetFromJsonAsync<JsonElement>($"/api/problem-reports/{fixture.ReportId}/closure-package");
        Assert.Equal("SoftwareQualityAnalyst", package.GetProperty("snapshot").GetProperty("approvalAuthority").GetString());
        Assert.Equal("SoftwareQualityAnalyst", package.GetProperty("package").GetProperty("closure").GetProperty("authority").GetString());
        Assert.Equal("IndependentSqaClosure", package.GetProperty("package").GetProperty("closure").GetProperty("authorityMeaning").GetString());
    }

    [Theory]
    [InlineData("closure.quality", "admin")]
    [InlineData("admin", "closure.quality")]
    public async Task Software_Quality_closure_remains_independent_from_reporter_and_responsible_engineer(
        string reportedBy, string responsibleEngineerId)
    {
        using var factory = new AeroLinkApiFactory();
        using var administrator = factory.CreateClient();
        await ProblemReportApiTests.BootstrapAndLoginAsync(administrator);
        var fixture = await SeedAsync(factory, reportedBy, responsibleEngineerId);
        using var quality = factory.CreateClient();
        await LoginAsync(quality, "closure.quality");
        var candidate = await SelectCandidateAsync(administrator, fixture, fixture.TargetBuildId,
            targetReleaseId: null, verificationClient: responsibleEngineerId == "closure.quality" ? quality : administrator);

        var detail = await quality.GetFromJsonAsync<JsonElement>($"/api/problem-reports/{fixture.ReportId}");
        Assert.False(detail.GetProperty("capabilities").GetProperty("canApproveSqaClosure").GetBoolean());
        using var response = await quality.PostAsJsonAsync($"/api/problem-reports/{fixture.ReportId}/closure/approve",
            new { expectedVersion = candidate.ReportVersion });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private static async Task<(Guid ExecutionId, long ReportVersion)> SelectCandidateAsync(HttpClient client,
        Fixture fixture, Guid buildId, Guid? targetReleaseId, HttpClient? verificationClient = null)
    {
        verificationClient ??= client;
        var releaseId = targetReleaseId ?? (await client.GetFromJsonAsync<JsonElement>(
            $"/api/problem-reports/{fixture.ReportId}")).GetProperty("targetReleaseId").GetGuid();
        using var recorded = await client.PostAsJsonAsync("/api/test-executions", new
        {
            projectId = fixture.ProjectId,
            procedureRevisionId = fixture.TargetRevisionId,
            softwareBuildId = buildId,
            retestOfExecutionId = fixture.OriginExecutionId,
            outcome = "Pass",
            configuration = "Closure candidate rig",
            determination = "The current corrective candidate satisfies the effective procedure.",
            evidenceReference = $"controlled://pr-461/{Guid.NewGuid():N}",
            executedAt = DateTimeOffset.UtcNow.AddMinutes(1),
            releaseId,
        });
        Assert.Equal(HttpStatusCode.Created, recorded.StatusCode);
        var executionId = (await recorded.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        var version = (await verificationClient.GetFromJsonAsync<JsonElement>($"/api/problem-reports/{fixture.ReportId}"))
            .GetProperty("version").GetInt64();
        using var verified = await verificationClient.PostAsJsonAsync($"/api/problem-reports/{fixture.ReportId}/verify",
            new { expectedVersion = version, testExecutionId = executionId });
        Assert.Equal(HttpStatusCode.OK, verified.StatusCode);
        return (executionId, (await verified.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("version").GetInt64());
    }

    private static async Task LoginAsync(HttpClient client, string userName)
    {
        using var response = await client.PostAsJsonAsync("/api/auth/login",
            new { userName, password = AeroLinkApiFactory.MemberPassword });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static async Task RejectAsync(HttpClient client, Guid reportId, long version, Guid executionId, string code)
    {
        using var response = await client.PostAsJsonAsync($"/api/problem-reports/{reportId}/verify", new
        {
            expectedVersion = version, testExecutionId = executionId
        });
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(code, (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
    }

    private static ProblemReport Report(Guid projectId, Guid releaseId, string number, DateTimeOffset now,
        string reportedBy = "admin", string responsibleEngineerId = "admin") =>
        new(projectId, number, "Verification chain", "Closure must use corrective evidence.", "", reportedBy, now,
            targetReleaseId: releaseId, responsibleEngineerId: responsibleEngineerId);

    private static void ProgressToVerifying(ProblemReport report, DateTimeOffset now)
    {
        var owner = report.ResponsibleEngineerId;
        report.ReadyForSccb(owner, now.AddMinutes(-3));
        report.OpenBySccb("sccb", now.AddMinutes(-2));
        report.BeginInvestigation(owner, "Root cause", "Cause", "Effect", "", now.AddMinutes(-1));
        report.ProposeResolution(owner, "Correct and retest.", now);
    }

    private static TestExecution Execution(Guid projectId, Guid revisionId, Guid? buildId, Guid? retestOf,
        TestOutcome outcome, DateTimeOffset executedAt, Guid releaseId) =>
        new(projectId, revisionId, buildId, retestOf, outcome, "tester", "Rig", "Determination",
            outcome == TestOutcome.Blocked ? "" : "controlled://evidence", executedAt, executedAt, releaseId);
}
