using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AeroLink.Domain.Baselines;
using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Identity;
using AeroLink.Domain.Programs;
using AeroLink.Domain.Verification;
using AeroLink.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace AeroLink.Api.Tests;

public sealed class BuildScopedWorkspaceApiTests
{
    private static async Task<(Guid ProjectId, Guid ReleasedId, Guid InWorkId, Guid ReleasedScrId, Guid InWorkScrId, Guid ProcedureRevisionId)>
        SeedAsync(AeroLinkApiFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        var now = DateTimeOffset.UtcNow;
        var program = new ProgramRecord("Build Context Program", "BCP");
        var project = new ProjectRecord(program.Id, "FMS Product Development", "Flight Management System");
        var released = new SoftwareRelease(project.Id, "1.5", true);
        var inWork = new SoftwareRelease(project.Id, "1.6", false, released.Id);
        var releasedScr = new SystemChangeRequest("SCR-15001", 0, project.Id, released.Id,
            "BUILD-ONE-FIVE-ONLY stability evidence", "P", "A", "S", "build.user", now,
            ChangeRequestType.System);
        var inWorkScr = new SystemChangeRequest("SCR-16001", 0, project.Id, inWork.Id,
            "BUILD-ONE-SIX-ONLY development work", "P", "A", "S", "build.user", now,
            ChangeRequestType.System);
        var user = new UserAccount("build.user", "Build User", "build.user@example.test",
            IdentityService.HashPassword(AeroLinkApiFactory.MemberPassword), now);
        var procedure = new TestProcedure(project.Id, "SYSTP-160001", "Build-scoped execution",
            user.UserName, now, TestProcedureLevel.System);
        var procedureRevision = new TestProcedureRevision(procedure.Id, 0, "Verify the selected build.",
            "Load the selected build.", "Exercise the controlled behavior.", "The behavior is observed.",
            TestProcedureState.Approved, user.UserName, now);
        db.AddRange(program, project, released, inWork, releasedScr, inWorkScr, user, procedure, procedureRevision);
        db.ProgramMemberships.Add(new ProgramMembership(user.Id, program.Id, ProgramRole.Engineer, "test.setup", now));
        db.ProgramMemberships.Add(new ProgramMembership(user.Id, program.Id, ProgramRole.TestEngineer, "test.setup", now));
        await db.SaveChangesAsync();
        return (project.Id, released.Id, inWork.Id, releasedScr.Id, inWorkScr.Id, procedureRevision.Id);
    }

    [Fact]
    public async Task Build_context_scopes_search_and_rejects_conflicting_queries_and_resources()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        var seeded = await SeedAsync(factory);
        await SignInAsync(client);

        client.DefaultRequestHeaders.Add("X-AeroLink-Build-Context", seeded.ReleasedId.ToString());
        using var releasedSearch = await client.GetAsync(
            $"/api/search?projectId={seeded.ProjectId}&releaseId={seeded.ReleasedId}&query=BUILD-ONE-FIVE-ONLY");
        Assert.Equal(HttpStatusCode.OK, releasedSearch.StatusCode);
        var releasedBody = await releasedSearch.Content.ReadAsStringAsync();
        Assert.Contains("BUILD-ONE-FIVE-ONLY", releasedBody);
        Assert.DoesNotContain("BUILD-ONE-SIX-ONLY", releasedBody);

        using var conflictingQuery = await client.GetAsync(
            $"/api/scrs?projectId={seeded.ProjectId}&releaseId={seeded.InWorkId}");
        Assert.Equal(HttpStatusCode.Conflict, conflictingQuery.StatusCode);
        Assert.Contains("build_context_mismatch", await conflictingQuery.Content.ReadAsStringAsync());

        client.DefaultRequestHeaders.Remove("X-AeroLink-Build-Context");
        client.DefaultRequestHeaders.Add("X-AeroLink-Build-Context", seeded.InWorkId.ToString());
        using var crossBuildRecord = await client.GetAsync($"/api/scrs/{seeded.ReleasedScrId}");
        Assert.Equal(HttpStatusCode.Conflict, crossBuildRecord.StatusCode);
        Assert.Contains("cross_build_resource", await crossBuildRecord.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Released_context_rejects_mutations_before_endpoint_execution_while_in_work_context_does_not()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        var seeded = await SeedAsync(factory);
        await SignInAsync(client);

        client.DefaultRequestHeaders.Add("X-AeroLink-Build-Context", seeded.ReleasedId.ToString());
        using var released = await client.PostAsJsonAsync("/api/scr-drafts", new { });
        Assert.Equal(HttpStatusCode.Conflict, released.StatusCode);
        Assert.Contains("released_build_read_only", await released.Content.ReadAsStringAsync());

        client.DefaultRequestHeaders.Remove("X-AeroLink-Build-Context");
        client.DefaultRequestHeaders.Add("X-AeroLink-Build-Context", seeded.InWorkId.ToString());
        using var inWork = await client.PostAsJsonAsync("/api/scr-drafts", new { });
        Assert.NotEqual(HttpStatusCode.Conflict, inWork.StatusCode);
    }

    [Fact]
    public async Task My_work_orders_drafts_within_their_priority_tier_by_earliest_due_date()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        var seeded = await SeedAsync(factory);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            db.SystemChangeRequests.Add(new SystemChangeRequest("SCR-16002", 0, seeded.ProjectId,
                seeded.InWorkId, "Earlier due Draft", "P", "A", "S", "build.user",
                DateTimeOffset.UtcNow.AddDays(-30), ChangeRequestType.System));
            await db.SaveChangesAsync();
        }

        await SignInAsync(client);
        var body = await client.GetFromJsonAsync<JsonElement>(
            $"/api/my-work?projectId={seeded.ProjectId}&releaseId={seeded.InWorkId}");
        var drafts = body.GetProperty("tasks").EnumerateArray()
            .Where(task => task.GetProperty("type").GetString() == "Draft to complete").ToList();

        Assert.Equal(2, drafts.Count);
        Assert.Equal("Earlier due Draft", drafts[0].GetProperty("title").GetString());
        Assert.True(drafts[0].GetProperty("dueAt").GetDateTimeOffset()
            < drafts[1].GetProperty("dueAt").GetDateTimeOffset());
        Assert.Equal(2, body.GetProperty("summary").GetProperty("drafts").GetInt32());
    }

    [Fact]
    public async Task Problem_reports_are_explicitly_owned_by_one_build_and_cross_build_detail_is_rejected()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        var seeded = await SeedAsync(factory);
        await SignInAsync(client);

        using var created = await client.PostAsJsonAsync("/api/problem-reports", new
        {
            projectId = seeded.ProjectId,
            releaseId = seeded.InWorkId,
            title = "BUILD-ONE-SIX-ONLY anomaly",
            problem = "An anomaly isolated to the in-work build."
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var body = await created.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        var reportId = body.GetProperty("id").GetGuid();

        client.DefaultRequestHeaders.Add("X-AeroLink-Build-Context", seeded.InWorkId.ToString());
        using var inWorkList = await client.GetAsync(
            $"/api/problem-reports?projectId={seeded.ProjectId}&releaseId={seeded.InWorkId}");
        Assert.Equal(HttpStatusCode.OK, inWorkList.StatusCode);
        Assert.Contains("BUILD-ONE-SIX-ONLY", await inWorkList.Content.ReadAsStringAsync());

        client.DefaultRequestHeaders.Remove("X-AeroLink-Build-Context");
        client.DefaultRequestHeaders.Add("X-AeroLink-Build-Context", seeded.ReleasedId.ToString());
        using var releasedList = await client.GetAsync(
            $"/api/problem-reports?projectId={seeded.ProjectId}&releaseId={seeded.ReleasedId}");
        Assert.Equal(HttpStatusCode.OK, releasedList.StatusCode);
        Assert.DoesNotContain("BUILD-ONE-SIX-ONLY", await releasedList.Content.ReadAsStringAsync());

        using var crossBuildDetail = await client.GetAsync($"/api/problem-reports/{reportId}");
        Assert.Equal(HttpStatusCode.Conflict, crossBuildDetail.StatusCode);
        Assert.Contains("cross_build_resource", await crossBuildDetail.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task In_work_execution_is_owned_by_route_build_even_without_immutable_software_build()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        var seeded = await SeedAsync(factory);
        await SignInAsync(client);

        client.DefaultRequestHeaders.Add("X-AeroLink-Build-Context", seeded.InWorkId.ToString());
        using var created = await client.PostAsJsonAsync("/api/test-executions", new
        {
            projectId = seeded.ProjectId,
            procedureRevisionId = seeded.ProcedureRevisionId,
            outcome = "Pass",
            configuration = "FMS 1.6 integration rig",
            determination = "The in-work behavior was observed.",
            evidenceReference = "evidence/build-1.6.json",
            executedAt = DateTimeOffset.UtcNow
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var createdBody = await created.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        var executionId = createdBody.GetProperty("id").GetGuid();

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var execution = await db.TestExecutions.FindAsync(executionId);
            Assert.Equal(seeded.InWorkId, execution!.ReleaseId);
            Assert.Null(execution.SoftwareBuildId);
        }

        using var inWorkList = await client.GetAsync(
            $"/api/test-executions?projectId={seeded.ProjectId}&releaseId={seeded.InWorkId}");
        Assert.Equal(HttpStatusCode.OK, inWorkList.StatusCode);
        Assert.Contains(executionId.ToString(), await inWorkList.Content.ReadAsStringAsync());

        client.DefaultRequestHeaders.Remove("X-AeroLink-Build-Context");
        client.DefaultRequestHeaders.Add("X-AeroLink-Build-Context", seeded.ReleasedId.ToString());
        using var releasedList = await client.GetAsync(
            $"/api/test-executions?projectId={seeded.ProjectId}&releaseId={seeded.ReleasedId}");
        Assert.Equal(HttpStatusCode.OK, releasedList.StatusCode);
        Assert.DoesNotContain(executionId.ToString(), await releasedList.Content.ReadAsStringAsync());

        using var crossBuildDetail = await client.GetAsync($"/api/test-executions/{executionId}");
        Assert.Equal(HttpStatusCode.Conflict, crossBuildDetail.StatusCode);
        Assert.Contains("cross_build_resource", await crossBuildDetail.Content.ReadAsStringAsync());
    }

    /// <summary>
    /// A released build is read-only whether or not the caller behaves like the browser.
    ///
    /// The workspace middleware refuses this, but only when the build-context header is supplied — which is a
    /// browser guarantee rather than a product one. A service account, integration or script that omitted the
    /// header reached the endpoint's final validation with the released boundary never checked, and a
    /// well-formed request would have written an immutable determination against a released build.
    ///
    /// The request below is otherwise entirely acceptable: approved procedure revision, correct project, no
    /// retest reference, no frozen campaign. Nothing but the released check can refuse it, so this cannot pass
    /// because some unrelated rule happened to reject the request.
    /// </summary>
    [Fact]
    public async Task Released_build_execution_is_refused_without_the_build_context_header()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        var seeded = await SeedAsync(factory);
        var releasedBuildId = await RecordReleasedSoftwareBuildAsync(factory, seeded.ProjectId, seeded.ReleasedId);
        await SignInAsync(client);

        Assert.False(client.DefaultRequestHeaders.Contains("X-AeroLink-Build-Context"));
        using var refused = await client.PostAsJsonAsync("/api/test-executions", new
        {
            projectId = seeded.ProjectId,
            procedureRevisionId = seeded.ProcedureRevisionId,
            softwareBuildId = releasedBuildId,
            outcome = "Pass",
            configuration = "Released rig",
            determination = "This determination must never reach a released build.",
            evidenceReference = "evidence/should-not-exist.json",
            executedAt = DateTimeOffset.UtcNow
        });

        var body = await refused.Content.ReadAsStringAsync();
        Assert.True(refused.StatusCode == HttpStatusCode.Conflict, $"Expected Conflict, got {(int)refused.StatusCode}: {body}");
        Assert.Contains("released_build_read_only", body);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            Assert.Empty(db.TestExecutions.Where(x => x.SoftwareBuildId == releasedBuildId));
        }

        // The same headerless caller must still be able to record against work in progress, or the refusal
        // would be protecting the released build by breaking the active one.
        using var accepted = await client.PostAsJsonAsync("/api/test-executions", new
        {
            projectId = seeded.ProjectId,
            procedureRevisionId = seeded.ProcedureRevisionId,
            outcome = "Pass",
            configuration = "FMS 1.6 integration rig",
            determination = "The in-work behavior was observed.",
            evidenceReference = "evidence/build-1.6.json",
            executedAt = DateTimeOffset.UtcNow
        });
        Assert.Equal(HttpStatusCode.Created, accepted.StatusCode);
    }

    private static async Task<Guid> RecordReleasedSoftwareBuildAsync(AeroLinkApiFactory factory, Guid projectId, Guid releasedId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        var now = DateTimeOffset.UtcNow;
        var baseline = new CandidateBaseline("SW-01.50", 0, projectId, releasedId, null, "Released software build", "build.user", now);
        var build = new SoftwareBuild(projectId, releasedId, baseline.Id, "SW-01.50", "Released configuration", "build.user", now);
        db.AddRange(baseline, build);
        await db.SaveChangesAsync();
        return build.Id;
    }

    private static async Task SignInAsync(HttpClient client)
    {
        using var response = await client.PostAsJsonAsync("/api/auth/login",
            new { userName = "build.user", password = AeroLinkApiFactory.MemberPassword });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
