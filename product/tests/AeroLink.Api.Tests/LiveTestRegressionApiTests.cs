using System.Net;
using System.Net.Http.Json;
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

public sealed class LiveTestRegressionApiTests
{
    private static async Task LoginAsync(HttpClient client, string user)
    {
        using var response = await client.PostAsJsonAsync("/api/auth/login",
            new { userName = user, password = AeroLinkApiFactory.MemberPassword });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static UserAccount Account(string user, DateTimeOffset now) => new(user, user,
        $"{user}@example.test", IdentityService.HashPassword(AeroLinkApiFactory.MemberPassword), now);

    [Fact]
    public async Task Released_build_uses_its_effective_procedure_revision_and_exact_history_link()
    {
        using var factory = new AeroLinkApiFactory();
        Guid projectId, releaseId, baselineId, procedureId, releasedRevisionId, laterRevisionId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var now = DateTimeOffset.UtcNow;
            var program = new ProgramRecord("Revision Scope", "RVS");
            var project = new ProjectRecord(program.Id, "FMS", "Revision Scope FMS");
            var release = new SoftwareRelease(project.Id, "1.5", true);
            var scr = new SystemChangeRequest("SRCR-90001", 0, project.Id, release.Id, "Released source",
                "Problem", "Analysis", "Solution", "author", now);
            var baseline = new CandidateBaseline("SW-01.50", 0, project.Id, release.Id, null,
                "Released baseline", "cm", now);
            var requirement = new RequirementArtifact(project.Id, "SYSR-900001", RequirementLevel.System, now);
            var requirementRevision = new RequirementRevision(requirement.Id, 0, "The FMS shall retain build context.",
                "Controlled scope", "Test", RequirementRevisionState.Active, scr.Id, baseline.Id, now);
            var procedure = new TestProcedure(project.Id, "SYSTP-900001", "Verify build context", "tester", now,
                TestProcedureLevel.System);
            var releasedRevision = new TestProcedureRevision(procedure.Id, 0, "Released objective", "Released setup",
                "Released steps", "Released expected result", TestProcedureState.Approved, "tester", now);
            var laterRevision = new TestProcedureRevision(procedure.Id, 1, "Later objective", "Later setup",
                "Later steps", "Later expected result", TestProcedureState.Draft, "tester", now.AddDays(1));
            var member = Account("scope.reader", now);
            db.AddRange(program, project, release, scr, baseline, requirement, requirementRevision, procedure,
                releasedRevision, laterRevision, member,
                new ProgramMembership(member.Id, program.Id, ProgramRole.Engineer, "setup", now),
                new BaselineRequirementSelection(baseline.Id, requirement.Id, requirementRevision.Id),
                new TestRequirementCoverage(releasedRevision.Id, requirementRevision.Id));
            await db.SaveChangesAsync();
            await db.CandidateBaselines.Where(x => x.Id == baseline.Id)
                .ExecuteUpdateAsync(update => update.SetProperty(x => x.RequirementsMaterializedAt, now));
            projectId = project.Id; releaseId = release.Id; baselineId = baseline.Id; procedureId = procedure.Id;
            releasedRevisionId = releasedRevision.Id; laterRevisionId = laterRevision.Id;
        }

        using var client = factory.CreateClient();
        await LoginAsync(client, "scope.reader");
        var page = await client.GetFromJsonAsync<JsonElement>(
            $"/api/test-procedures?projectId={projectId}&releaseId={releaseId}&scope=System&page=1&pageSize=25");
        var row = page.GetProperty("items")[0];
        Assert.Equal("SYSTP-900001.00", row.GetProperty("displayNumber").GetString());
        Assert.Equal("Approved", row.GetProperty("state").GetString());
        Assert.Equal(1, row.GetProperty("requirementCount").GetInt32());

        var coverage = await client.GetFromJsonAsync<JsonElement>(
            $"/api/verification-coverage?projectId={projectId}&baselineId={baselineId}");
        Assert.Equal(1, coverage.GetProperty("covered").GetInt32());
        Assert.Equal(0, coverage.GetProperty("suspect").GetInt32());
        Assert.Equal("Covered", coverage.GetProperty("items")[0].GetProperty("disposition").GetString());

        using var exact = await client.GetAsync(
            $"/api/test-procedures/{procedureId}/history?releaseId={releaseId}&revisionId={releasedRevisionId}");
        Assert.Equal(HttpStatusCode.OK, exact.StatusCode);
        var history = await exact.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(releasedRevisionId, history.GetProperty("selectedRevisionId").GetGuid());
        Assert.Equal("Released steps", history.GetProperty("revisions").EnumerateArray()
            .Single(x => x.GetProperty("selected").GetBoolean()).GetProperty("steps").GetString());

        using var crossBuild = await client.GetAsync(
            $"/api/test-procedures/{procedureId}/history?releaseId={releaseId}&revisionId={laterRevisionId}");
        Assert.Equal(HttpStatusCode.NotFound, crossBuild.StatusCode);
    }

    [Fact]
    public async Task Downstream_queue_projects_claim_capability_for_the_current_user()
    {
        using var factory = new AeroLinkApiFactory();
        Guid projectId, releaseId, assessmentId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var now = DateTimeOffset.UtcNow;
            var program = new ProgramRecord("Assessment Authority", "ASA");
            var project = new ProjectRecord(program.Id, "FMS", "Assessment FMS");
            var release = new SoftwareRelease(project.Id, "1.6", false);
            var source = new SystemChangeRequest("SRCR-90002", 0, project.Id, release.Id, "Upstream change",
                "Problem", "Analysis", "Solution", "author", now);
            var assessment = new DownstreamChangeAssessment(project.Id, release.Id, source.Id,
                source.DisplayNumber, RequirementLevel.HighLevel, now);
            var reviewer = Account("reviewer.only", now);
            var engineer = Account("software.engineer", now);
            db.AddRange(program, project, release, source, assessment, reviewer, engineer,
                new ProgramMembership(reviewer.Id, program.Id, ProgramRole.Approver, "setup", now),
                new ProgramMembership(engineer.Id, program.Id, ProgramRole.Engineer, "setup", now));
            await db.SaveChangesAsync();
            projectId = project.Id; releaseId = release.Id; assessmentId = assessment.Id;
        }

        using (var reviewer = factory.CreateClient())
        {
            await LoginAsync(reviewer, "reviewer.only");
            var rows = await reviewer.GetFromJsonAsync<JsonElement>(
                $"/api/downstream-assessments?projectId={projectId}&releaseId={releaseId}");
            Assert.False(rows[0].GetProperty("capabilities").GetProperty("canAssign").GetBoolean());
        }
        using (var engineer = factory.CreateClient())
        {
            await LoginAsync(engineer, "software.engineer");
            var rows = await engineer.GetFromJsonAsync<JsonElement>(
                $"/api/downstream-assessments?projectId={projectId}&releaseId={releaseId}");
            Assert.True(rows[0].GetProperty("capabilities").GetProperty("canAssign").GetBoolean());
            Assert.Equal("Problem", rows[0].GetProperty("sourceProblem").GetString());
            await SecurityBoundaryTests.AuthorizeMutationsAsync(engineer);
            Assert.Equal(HttpStatusCode.OK, (await engineer.PostAsJsonAsync(
                $"/api/downstream-assessments/{assessmentId}/assign", new { engineerId = "software.engineer" })).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await engineer.PostAsync(
                $"/api/downstream-assessments/{assessmentId}/change-required", null)).StatusCode);
            rows = await engineer.GetFromJsonAsync<JsonElement>(
                $"/api/downstream-assessments?projectId={projectId}&releaseId={releaseId}");
            Assert.Equal("ChangeRequired", rows[0].GetProperty("outcome").GetString());
            Assert.False(rows[0].GetProperty("capabilities").GetProperty("canSubmit").GetBoolean());
        }
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            await db.Releases.Where(x => x.Id == releaseId)
                .ExecuteUpdateAsync(update => update.SetProperty(x => x.IsReleased, true));
        }
        using (var historicalReader = factory.CreateClient())
        {
            await LoginAsync(historicalReader, "software.engineer");
            var rows = await historicalReader.GetFromJsonAsync<JsonElement>(
                $"/api/downstream-assessments?projectId={projectId}&releaseId={releaseId}");
            Assert.False(rows[0].GetProperty("capabilities").GetProperty("canEdit").GetBoolean());
            await SecurityBoundaryTests.AuthorizeMutationsAsync(historicalReader);
            using var mutation = await historicalReader.PostAsync(
                $"/api/downstream-assessments/{assessmentId}/change-required", null);
            Assert.Equal(HttpStatusCode.Conflict, mutation.StatusCode);
        }
    }

    [Fact]
    public async Task Change_request_audit_projection_normalizes_legacy_requirement_padding()
    {
        using var factory = new AeroLinkApiFactory();
        Guid changeRequestId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var now = DateTimeOffset.UtcNow;
            var program = new ProgramRecord("Audit Format", "AUF");
            var project = new ProjectRecord(program.Id, "FMS", "Audit FMS");
            var release = new SoftwareRelease(project.Id, "1.6", false);
            var scr = new SystemChangeRequest("SRCR-90003", 0, project.Id, release.Id, "Audit formatting",
                "Problem", "Analysis", "Solution", "audit.reader", now);
            scr.AddRequirementChange("audit.reader", "SYSR-00000001", 0, RequirementLevel.System,
                RequirementChangeKind.Introduce, "Statement", "Rationale", "Test", now);
            var member = Account("audit.reader", now);
            db.AddRange(program, project, release, scr, member,
                new ProgramMembership(member.Id, program.Id, ProgramRole.Engineer, "setup", now));
            await db.SaveChangesAsync();
            changeRequestId = scr.Id;
        }

        using var client = factory.CreateClient();
        await LoginAsync(client, "audit.reader");
        var detail = await client.GetFromJsonAsync<JsonElement>($"/api/change-requests/{changeRequestId}");
        var added = detail.GetProperty("audit").EnumerateArray()
            .Single(x => x.GetProperty("eventType").GetString() == "RequirementChangeAdded");
        Assert.Contains("SYSR-000001.00", added.GetProperty("detail").GetString());
        Assert.DoesNotContain("SYSR-00000001.00", added.GetProperty("detail").GetString());
    }
}
