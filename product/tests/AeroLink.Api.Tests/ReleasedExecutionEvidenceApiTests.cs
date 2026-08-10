using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using AeroLink.Domain.Baselines;
using AeroLink.Domain.Identity;
using AeroLink.Domain.Programs;
using AeroLink.Domain.Releases;
using AeroLink.Domain.Verification;
using AeroLink.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AeroLink.Api.Tests;

/// <summary>
/// #423 — linking evidence changes the controlled record of an execution. Once the execution belongs to a
/// released configuration, the ordinary relationship endpoint must be read-only even when a caller omits the
/// optional build-context header.
/// </summary>
public sealed class ReleasedExecutionEvidenceApiTests
{
    private const string UserName = "released.evidence.tester";

    [Fact]
    public async Task Headerless_direct_api_cannot_append_evidence_to_a_released_execution()
    {
        using var root = new AeroLinkApiFactory();
        using var factory = GuardedFactory(root);
        using var client = factory.CreateClient();
        var scenario = await SeedAsync(factory.Services);
        await LoginAsync(client);

        var before = await LinkIdsAsync(factory.Services, scenario.ReleasedExecutionId);
        using var response = await client.PostAsync(
            $"/api/test-executions/{scenario.ReleasedExecutionId}/evidence/{scenario.ReleasedCandidateEvidenceId}",
            null);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("released_build_read_only", await CodeAsync(response));
        Assert.Equal(before, await LinkIdsAsync(factory.Services, scenario.ReleasedExecutionId));

        using var history = await client.GetAsync(
            $"/api/test-executions?projectId={scenario.ProjectId}&releaseId={scenario.ReleasedReleaseId}&buildId={scenario.ReleasedBuildId}");
        Assert.Equal(HttpStatusCode.OK, history.StatusCode);
        var rows = await history.Content.ReadFromJsonAsync<JsonElement>();
        var execution = Assert.Single(rows.EnumerateArray());
        var evidence = execution.GetProperty("evidence").EnumerateArray().ToList();
        Assert.Single(evidence);
        Assert.Equal("released-existing.txt", evidence[0].GetProperty("originalFileName").GetString());
    }

    [Fact]
    public async Task Released_workspace_header_refuses_the_same_evidence_mutation_as_defense_in_depth()
    {
        using var root = new AeroLinkApiFactory();
        using var factory = GuardedFactory(root);
        using var client = factory.CreateClient();
        var scenario = await SeedAsync(factory.Services);
        await LoginAsync(client);
        client.DefaultRequestHeaders.Add("X-AeroLink-Build-Context", scenario.ReleasedReleaseId.ToString());

        var before = await LinkIdsAsync(factory.Services, scenario.ReleasedExecutionId);
        using var response = await client.PostAsync(
            $"/api/test-executions/{scenario.ReleasedExecutionId}/evidence/{scenario.ReleasedCandidateEvidenceId}",
            null);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("released_build_read_only", await CodeAsync(response));
        Assert.Equal(before, await LinkIdsAsync(factory.Services, scenario.ReleasedExecutionId));
    }

    [Fact]
    public async Task Authorized_test_engineer_can_attach_evidence_to_an_in_work_execution()
    {
        using var root = new AeroLinkApiFactory();
        using var factory = GuardedFactory(root);
        using var client = factory.CreateClient();
        var scenario = await SeedAsync(factory.Services);
        await LoginAsync(client);

        var before = await LinkIdsAsync(factory.Services, scenario.InWorkExecutionId);
        Assert.Empty(before);
        using var response = await client.PostAsync(
            $"/api/test-executions/{scenario.InWorkExecutionId}/evidence/{scenario.InWorkEvidenceId}",
            null);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(new[] { scenario.InWorkEvidenceId }, await LinkIdsAsync(factory.Services, scenario.InWorkExecutionId));

        using var history = await client.GetAsync(
            $"/api/test-executions?projectId={scenario.ProjectId}&releaseId={scenario.InWorkReleaseId}&buildId={scenario.InWorkBuildId}");
        Assert.Equal(HttpStatusCode.OK, history.StatusCode);
        var rows = await history.Content.ReadFromJsonAsync<JsonElement>();
        var execution = Assert.Single(rows.EnumerateArray());
        Assert.Equal("in-work-evidence.txt",
            execution.GetProperty("evidence")[0].GetProperty("originalFileName").GetString());
    }

    [Fact]
    public async Task Project_evidence_upload_remains_available_after_an_execution_release()
    {
        using var root = new AeroLinkApiFactory();
        using var factory = GuardedFactory(root);
        using var client = factory.CreateClient();
        var scenario = await SeedAsync(factory.Services);
        await LoginAsync(client);

        var beforeLinks = await AllLinkIdsAsync(factory.Services);
        var beforeEvidence = await EvidenceIdsAsync(factory.Services);
        using var multipart = new MultipartFormDataContent();
        using var file = new ByteArrayContent(Encoding.UTF8.GetBytes("late release observation"));
        file.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
        multipart.Add(file, "file", "late-release-observation.txt");
        multipart.Add(new StringContent(scenario.ProjectId.ToString()), "projectId");

        using var response = await client.PostAsync("/api/evidence", multipart);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var uploaded = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains(uploaded.GetProperty("id").GetGuid(), await EvidenceIdsAsync(factory.Services));
        Assert.Equal(beforeEvidence.Count + 1, (await EvidenceIdsAsync(factory.Services)).Count);
        Assert.Equal(beforeLinks, await AllLinkIdsAsync(factory.Services));
    }

    [Fact]
    public async Task In_review_release_package_freeze_still_precedes_evidence_persistence()
    {
        using var root = new AeroLinkApiFactory();
        using var factory = GuardedFactory(root);
        using var client = factory.CreateClient();
        var scenario = await SeedAsync(factory.Services);
        await LoginAsync(client);

        var before = await LinkIdsAsync(factory.Services, scenario.InReviewExecutionId);
        using var response = await client.PostAsync(
            $"/api/test-executions/{scenario.InReviewExecutionId}/evidence/{scenario.InReviewEvidenceId}",
            null);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("release_package_frozen", await CodeAsync(response));
        Assert.Equal(before, await LinkIdsAsync(factory.Services, scenario.InReviewExecutionId));
    }

    [Fact]
    public async Task A_new_execution_and_its_evidence_link_cannot_bypass_release_in_one_save()
    {
        using var root = new AeroLinkApiFactory();
        using var factory = GuardedFactory(root);
        var scenario = await SeedAsync(factory.Services);
        Guid executionId;
        Guid linkId;

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var revisionId = await db.TestExecutions.AsNoTracking()
                .Where(x => x.Id == scenario.ReleasedExecutionId)
                .Select(x => x.ProcedureRevisionId)
                .SingleAsync();
            var execution = Execution(scenario.ProjectId, scenario.ReleasedReleaseId, scenario.ReleasedBuildId,
                revisionId, "Same-save bypass attempt", DateTimeOffset.UtcNow);
            var link = new TestExecutionEvidence(execution.Id, scenario.ReleasedCandidateEvidenceId);
            executionId = execution.Id;
            linkId = link.Id;
            db.AddRange(execution, link);

            var exception = await Assert.ThrowsAsync<ReleasedBuildReadOnlyException>(() => db.SaveChangesAsync());
            Assert.Contains("released build", exception.Message, StringComparison.OrdinalIgnoreCase);
        }

        using var verifyScope = factory.Services.CreateScope();
        var verify = verifyScope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        Assert.False(await verify.TestExecutions.AnyAsync(x => x.Id == executionId));
        Assert.False(await verify.TestExecutionEvidence.AnyAsync(x => x.Id == linkId));
    }

    [Fact]
    public async Task A_stale_tracked_in_work_release_cannot_bypass_a_concurrent_release()
    {
        using var root = new AeroLinkApiFactory();
        using var factory = GuardedFactory(root);
        var scenario = await SeedAsync(factory.Services);
        Guid linkId;

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var staleRelease = await db.Releases.SingleAsync(x => x.Id == scenario.InWorkReleaseId);
            Assert.False(staleRelease.IsReleased);

            // Change the authoritative database row without refreshing the tracked entity. An interceptor that
            // blindly trusts EntityState.Unchanged would still see false and append evidence after release.
            await db.Releases.Where(x => x.Id == scenario.InWorkReleaseId)
                .ExecuteUpdateAsync(update => update
                    .SetProperty(x => x.IsReleased, true)
                    .SetProperty(x => x.ReleasedAt, DateTimeOffset.UtcNow));
            Assert.False(staleRelease.IsReleased);

            var link = new TestExecutionEvidence(scenario.InWorkExecutionId, scenario.InWorkEvidenceId);
            linkId = link.Id;
            db.Add(link);

            var exception = await Assert.ThrowsAsync<ReleasedBuildReadOnlyException>(() => db.SaveChangesAsync());
            Assert.Contains("released build", exception.Message, StringComparison.OrdinalIgnoreCase);
        }

        using var verifyScope = factory.Services.CreateScope();
        var verify = verifyScope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        Assert.False(await verify.TestExecutionEvidence.AnyAsync(x => x.Id == linkId));
        Assert.False(await verify.TestExecutionEvidence.AnyAsync(x => x.TestExecutionId == scenario.InWorkExecutionId
            && x.EvidenceId == scenario.InWorkEvidenceId));
    }

    private static WebApplicationFactory<Program> GuardedFactory(AeroLinkApiFactory root) =>
        root.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<AeroLinkDbContext>();
            services.RemoveAll<DbContextOptions<AeroLinkDbContext>>();
            services.RemoveAll<IDbContextOptionsConfiguration<AeroLinkDbContext>>();
            services.AddDbContext<AeroLinkDbContext>(options => options
                .UseSqlite(root.ConnectionString)
                .AddInterceptors(new ReleasedExecutionEvidenceInterceptor()));
        }));

    private static async Task LoginAsync(HttpClient client)
    {
        using var login = await client.PostAsJsonAsync("/api/auth/login",
            new { userName = UserName, password = AeroLinkApiFactory.MemberPassword });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        await SecurityBoundaryTests.AuthorizeMutationsAsync(client);
    }

    private static async Task<string?> CodeAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        using var json = JsonDocument.Parse(body);
        return json.RootElement.TryGetProperty("code", out var code) ? code.GetString() : null;
    }

    private static async Task<List<Guid>> LinkIdsAsync(IServiceProvider services, Guid executionId)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        return await db.TestExecutionEvidence.AsNoTracking()
            .Where(x => x.TestExecutionId == executionId)
            .OrderBy(x => x.EvidenceId)
            .Select(x => x.EvidenceId)
            .ToListAsync();
    }

    private static async Task<List<Guid>> AllLinkIdsAsync(IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        return await db.TestExecutionEvidence.AsNoTracking()
            .OrderBy(x => x.Id)
            .Select(x => x.Id)
            .ToListAsync();
    }

    private static async Task<List<Guid>> EvidenceIdsAsync(IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        return await db.EvidenceRecords.AsNoTracking()
            .OrderBy(x => x.Id)
            .Select(x => x.Id)
            .ToListAsync();
    }

    private static async Task<Scenario> SeedAsync(IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        var now = new DateTimeOffset(2026, 8, 9, 12, 0, 0, TimeSpan.Zero);

        var program = new ProgramRecord("Released execution evidence", "REE");
        var project = new ProjectRecord(program.Id, "Evidence project", "Evidence product");

        var releasedRelease = new SoftwareRelease(project.Id, "1.0", false);
        var inWorkRelease = new SoftwareRelease(project.Id, "1.1", false, releasedRelease.Id);
        var inReviewRelease = new SoftwareRelease(project.Id, "1.2", false, inWorkRelease.Id);
        var releasedBaseline = new CandidateBaseline("SW-10.00", 0, project.Id, releasedRelease.Id, null,
            "Released baseline", "cm", now);
        var inWorkBaseline = new CandidateBaseline("SW-11.00", 0, project.Id, inWorkRelease.Id,
            releasedBaseline.Id, "In-work baseline", "cm", now);
        var inReviewBaseline = new CandidateBaseline("SW-12.00", 0, project.Id, inReviewRelease.Id,
            inWorkBaseline.Id, "In-review baseline", "cm", now);
        var releasedBuild = new SoftwareBuild(project.Id, releasedRelease.Id, releasedBaseline.Id,
            "SW-10.00", "Released software build", "cm", now);
        var inWorkBuild = new SoftwareBuild(project.Id, inWorkRelease.Id, inWorkBaseline.Id,
            "SW-11.00", "In-work software build", "cm", now);
        var inReviewBuild = new SoftwareBuild(project.Id, inReviewRelease.Id, inReviewBaseline.Id,
            "SW-12.00", "In-review software build", "cm", now);

        var procedure = new TestProcedure(project.Id, "SYSTP-423001",
            "Verify released execution evidence", UserName, now, TestProcedureLevel.System);
        var revision = new TestProcedureRevision(procedure.Id, 0, "Verify evidence lifecycle.",
            "A controlled build exists.", "1. Execute the procedure.", "The expected behavior is observed.",
            TestProcedureState.Approved, UserName, now);

        var releasedExecution = Execution(project.Id, releasedRelease.Id, releasedBuild.Id, revision.Id,
            "Released execution", now);
        var inWorkExecution = Execution(project.Id, inWorkRelease.Id, inWorkBuild.Id, revision.Id,
            "In-work execution", now);
        var inReviewExecution = Execution(project.Id, inReviewRelease.Id, inReviewBuild.Id, revision.Id,
            "In-review execution", now);

        var existingEvidence = Evidence(project.Id, "released-existing.txt", 'a', now);
        var releasedCandidate = Evidence(project.Id, "released-late.txt", 'b', now);
        var inWorkEvidence = Evidence(project.Id, "in-work-evidence.txt", 'c', now);
        var inReviewEvidence = Evidence(project.Id, "in-review-evidence.txt", 'd', now);

        var user = new UserAccount(UserName, "Released Evidence Tester", "released.evidence@example.test",
            IdentityService.HashPassword(AeroLinkApiFactory.MemberPassword), now);
        var campaign = new ReleaseCampaign(project.Id, inReviewRelease.Id, inReviewBaseline.Id,
            "In-review evidence package", "cm", now);
        campaign.StartVerification("cm", now.AddMinutes(1));
        campaign.SelectVerificationBuild(inReviewBuild.Id, "cm", now.AddMinutes(2));
        campaign.BeginReleaseReview("cm", new List<(string Id, string Name)> { ("release.approver", "Release Approver") },
            new string('f', 64), now.AddMinutes(3));

        // Historical evidence is linked while the configuration is still in work. Release is a second,
        // explicit lifecycle transition; the guard must preserve the old link and refuse only later additions.
        db.AddRange(
            program, project,
            releasedRelease, inWorkRelease, inReviewRelease,
            releasedBaseline, inWorkBaseline, inReviewBaseline,
            releasedBuild, inWorkBuild, inReviewBuild,
            procedure, revision,
            releasedExecution, inWorkExecution, inReviewExecution,
            existingEvidence, releasedCandidate, inWorkEvidence, inReviewEvidence,
            new TestExecutionEvidence(releasedExecution.Id, existingEvidence.Id),
            campaign,
            user,
            new ProgramMembership(user.Id, program.Id, ProgramRole.TestEngineer, "test.setup", now));
        await db.SaveChangesAsync();
        releasedRelease.MarkReleased(now.AddHours(1));
        await db.SaveChangesAsync();

        return new(
            project.Id,
            releasedRelease.Id, releasedBuild.Id, releasedExecution.Id, releasedCandidate.Id,
            inWorkRelease.Id, inWorkBuild.Id, inWorkExecution.Id, inWorkEvidence.Id,
            inReviewExecution.Id, inReviewEvidence.Id);
    }

    private static TestExecution Execution(
        Guid projectId,
        Guid releaseId,
        Guid buildId,
        Guid revisionId,
        string determination,
        DateTimeOffset now) =>
        new(projectId, revisionId, buildId, null, TestOutcome.Pass, UserName,
            "Controlled test rig", determination, "controlled://evidence/reference", now, now, releaseId);

    private static EvidenceRecord Evidence(Guid projectId, string fileName, char hashCharacter,
        DateTimeOffset now) =>
        new(projectId, fileName, "text/plain", 1, new string(hashCharacter, 64),
            $"seed/{fileName}", UserName, now);

    private sealed record Scenario(
        Guid ProjectId,
        Guid ReleasedReleaseId,
        Guid ReleasedBuildId,
        Guid ReleasedExecutionId,
        Guid ReleasedCandidateEvidenceId,
        Guid InWorkReleaseId,
        Guid InWorkBuildId,
        Guid InWorkExecutionId,
        Guid InWorkEvidenceId,
        Guid InReviewExecutionId,
        Guid InReviewEvidenceId);
}
