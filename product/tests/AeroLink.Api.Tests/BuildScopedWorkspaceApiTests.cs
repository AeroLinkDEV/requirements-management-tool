using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AeroLink.Domain.Baselines;
using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Identity;
using AeroLink.Domain.Programs;
using AeroLink.Domain.Verification;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
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
        var releasedScr = new SystemChangeRequest("SRCR-15001", 0, project.Id, released.Id,
            "BUILD-ONE-FIVE-ONLY stability evidence", "P", "A", "S", "build.user", now,
            ChangeRequestType.System);
        var inWorkScr = new SystemChangeRequest("SRCR-16001", 0, project.Id, inWork.Id,
            "BUILD-ONE-SIX-ONLY development work", "P", "A", "S", "build.user", now,
            ChangeRequestType.System);
        var user = new UserAccount("build.user", "Build User", "build.user@example.test",
            IdentityService.HashPassword(AeroLinkApiFactory.MemberPassword), now);
        var procedure = new TestProcedure(project.Id, "SYSTP-160001", "Build-scoped execution",
            user.UserName, now, TestProcedureLevel.System);
        var procedureRevision = new TestProcedureRevision(procedure.Id, 0, "Verify the selected build.",
            "Load the selected build.", "Exercise the controlled behavior.", "The behavior is observed.",
            TestProcedureState.Approved, user.UserName, now);
        var inWorkBaseline = new CandidateBaseline("SW-01.60", 0, project.Id, inWork.Id, null,
            "In-work controlled baseline", "build.user", now);
        db.AddRange(program, project, released, inWork, releasedScr, inWorkScr, user, procedure,
            procedureRevision, inWorkBaseline,
            new BaselineTestProcedureSelection(inWorkBaseline.Id, procedure.Id, procedureRevision.Id));
        db.ProgramMemberships.Add(new ProgramMembership(user.Id, program.Id, ProgramRole.Engineer, "test.setup", now));
        db.ProgramMemberships.Add(new ProgramMembership(user.Id, program.Id, ProgramRole.TestEngineer, "test.setup", now));
        await db.SaveChangesAsync();
        await db.CandidateBaselines.Where(x => x.Id == inWorkBaseline.Id)
            .ExecuteUpdateAsync(update => update
                .SetProperty(x => x.RequirementsMaterializedAt, now)
                .SetProperty(x => x.TestProceduresMaterializedAt, now)
                .SetProperty(x => x.TestProceduresHash, "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"));
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
            $"/api/change-requests?projectId={seeded.ProjectId}&releaseId={seeded.InWorkId}");
        Assert.Equal(HttpStatusCode.Conflict, conflictingQuery.StatusCode);
        Assert.Contains("build_context_mismatch", await conflictingQuery.Content.ReadAsStringAsync());

        client.DefaultRequestHeaders.Remove("X-AeroLink-Build-Context");
        client.DefaultRequestHeaders.Add("X-AeroLink-Build-Context", seeded.InWorkId.ToString());
        using var crossBuildRecord = await client.GetAsync($"/api/change-requests/{seeded.ReleasedScrId}");
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
        using var released = await client.PostAsJsonAsync("/api/change-request-drafts", new { });
        Assert.Equal(HttpStatusCode.Conflict, released.StatusCode);
        Assert.Contains("released_build_read_only", await released.Content.ReadAsStringAsync());

        client.DefaultRequestHeaders.Remove("X-AeroLink-Build-Context");
        client.DefaultRequestHeaders.Add("X-AeroLink-Build-Context", seeded.InWorkId.ToString());
        using var inWork = await client.PostAsJsonAsync("/api/change-request-drafts", new { });
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
            db.SystemChangeRequests.Add(new SystemChangeRequest("SRCR-16002", 0, seeded.ProjectId,
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

    /// <summary>
    /// One Problem Report database, read the same from any build.
    ///
    /// This test previously asserted the opposite — that a report was owned by one build, invisible from the
    /// others, and refused as a cross-build resource. That was a reasonable reading of consistency with
    /// requirements and change requests, and it was wrong: a report *names* a target build, but the database
    /// of what is open and in work is a Project-level record set. Filtering it by whichever workspace the
    /// reader stands in does not produce a different view of one database; it produces what looks like a
    /// different database. See DEC-089.
    ///
    /// The target build survives as an attribute and as an explicit filter, which is what the last block checks.
    /// </summary>
    [Fact]
    public async Task Problem_reports_are_one_project_database_visible_from_every_build()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        var seeded = await SeedAsync(factory);
        await SignInAsync(client);

        using var created = await client.PostAsJsonAsync("/api/problem-reports", new
        { category = "CodeFunctional",
            projectId = seeded.ProjectId,
            releaseId = seeded.InWorkId,
            title = "RAISED-AGAINST-ONE-SIX anomaly",
            problem = "An anomaly raised while Build 1.6 was the active workspace."
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var createdReport = await created.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        var reportId = createdReport.GetProperty("id").GetGuid();
        var reportVersion = createdReport.GetProperty("version").GetInt64();
        using var unassignedCreated = await client.PostAsJsonAsync("/api/problem-reports", new
        { category = "CodeFunctional",
            projectId = seeded.ProjectId,
            title = "UNASSIGNED-PROJECT anomaly",
            problem = "An anomaly whose target build has not been assigned."
        });
        Assert.Equal(HttpStatusCode.Created, unassignedCreated.StatusCode);

        client.DefaultRequestHeaders.Add("X-AeroLink-Build-Context", seeded.InWorkId.ToString());
        using var fromInWork = await client.GetAsync($"/api/problem-reports?projectId={seeded.ProjectId}");
        Assert.Equal(HttpStatusCode.OK, fromInWork.StatusCode);
        Assert.Contains("RAISED-AGAINST-ONE-SIX", await fromInWork.Content.ReadAsStringAsync());
        using var detailFromInWork = await client.GetAsync($"/api/problem-reports/{reportId}");
        Assert.Equal(HttpStatusCode.OK, detailFromInWork.StatusCode);

        // The same database, from the other build. Both the list and the record itself.
        client.DefaultRequestHeaders.Remove("X-AeroLink-Build-Context");
        client.DefaultRequestHeaders.Add("X-AeroLink-Build-Context", seeded.ReleasedId.ToString());
        using var fromReleased = await client.GetAsync($"/api/problem-reports?projectId={seeded.ProjectId}");
        Assert.Equal(HttpStatusCode.OK, fromReleased.StatusCode);
        Assert.Contains("RAISED-AGAINST-ONE-SIX", await fromReleased.Content.ReadAsStringAsync());
        using var detailFromReleased = await client.GetAsync($"/api/problem-reports/{reportId}");
        Assert.Equal(HttpStatusCode.OK, detailFromReleased.StatusCode);
        using var mutateFromReleased = await client.PostAsJsonAsync(
            $"/api/problem-reports/{reportId}/ready-for-sccb", new { expectedVersion = reportVersion });
        Assert.Equal(HttpStatusCode.OK, mutateFromReleased.StatusCode);
        using var checkoutFromReleased = await client.PostAsJsonAsync("/api/controlled-editing/checkout", new
        {
            artifactType = "ProblemReport",
            artifactId = reportId,
            leaseMinutes = 15
        });
        Assert.Equal(HttpStatusCode.Created, checkoutFromReleased.StatusCode);
        var checkout = await checkoutFromReleased.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        using var discardFromReleased = await client.PostAsJsonAsync(
            $"/api/controlled-editing/sessions/{checkout.GetProperty("id").GetGuid()}/discard",
            new { expectedVersion = checkout.GetProperty("version").GetInt64(), reason = "Released-context contract proof." });
        Assert.Equal(HttpStatusCode.NoContent, discardFromReleased.StatusCode);
        using var refusedBuildOwnedCheckout = await client.PostAsJsonAsync("/api/controlled-editing/checkout", new
        {
            artifactType = "DocumentTemplate",
            artifactId = Guid.NewGuid(),
            leaseMinutes = 15
        });
        Assert.Equal(HttpStatusCode.Conflict, refusedBuildOwnedCheckout.StatusCode);

        // Target build is still recorded, and still filters when somebody asks for it deliberately.
        using var targeted = await client.GetAsync(
            $"/api/problem-reports?projectId={seeded.ProjectId}&targetReleaseId={seeded.InWorkId}");
        Assert.Contains("RAISED-AGAINST-ONE-SIX", await targeted.Content.ReadAsStringAsync());
        using var otherTarget = await client.GetAsync(
            $"/api/problem-reports?projectId={seeded.ProjectId}&targetReleaseId={seeded.ReleasedId}");
        Assert.DoesNotContain("RAISED-AGAINST-ONE-SIX", await otherTarget.Content.ReadAsStringAsync());
        using var unassigned = await client.GetAsync(
            $"/api/problem-reports?projectId={seeded.ProjectId}&targetUnassigned=true");
        var unassignedBody = await unassigned.Content.ReadAsStringAsync();
        Assert.Contains("UNASSIGNED-PROJECT", unassignedBody);
        Assert.DoesNotContain("RAISED-AGAINST-ONE-SIX", unassignedBody);
        var targetDashboard = await client.GetFromJsonAsync<System.Text.Json.JsonElement>(
            $"/api/problem-reports/dashboard?projectId={seeded.ProjectId}&targetReleaseId={seeded.InWorkId}");
        var unassignedDashboard = await client.GetFromJsonAsync<System.Text.Json.JsonElement>(
            $"/api/problem-reports/dashboard?projectId={seeded.ProjectId}&targetUnassigned=true");
        Assert.Equal(1, targetDashboard.GetProperty("summary").GetProperty("total").GetInt32());
        Assert.Equal(1, unassignedDashboard.GetProperty("summary").GetProperty("total").GetInt32());
        using var conflictingTarget = await client.GetAsync(
            $"/api/problem-reports?projectId={seeded.ProjectId}&targetReleaseId={seeded.InWorkId}&targetUnassigned=true");
        Assert.Equal(HttpStatusCode.BadRequest, conflictingTarget.StatusCode);
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
