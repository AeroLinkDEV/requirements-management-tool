using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AeroLink.Domain.Baselines;
using AeroLink.Domain.Identity;
using AeroLink.Domain.Programs;
using AeroLink.Domain.Verification;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AeroLink.Api.Tests;

/// <summary>
/// #422 — a test execution is configuration evidence: the exact procedure revision executed must be the
/// revision the selected build's exact procedure manifest carries, not merely an Approved same-Project
/// revision.
/// </summary>
public sealed class TestExecutionEffectivityApiTests
{
    private sealed record Fixture(
        Guid ProjectId,
        Guid Release16Id,
        Guid Build16Id,
        Guid Release17Id,
        Guid Build17Id,
        Guid Release18Id,
        Guid Build18Id,
        Guid BareBuildId,
        Guid BareReleaseId,
        Guid OtherReleaseId,
        Guid ProcedureId,
        Guid Revision00Id,
        Guid Revision01Id,
        Guid UncarriedRevisionId);

    private static async Task<Fixture> SeedAsync(AeroLinkApiFactory factory)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        var now = new DateTimeOffset(2026, 8, 9, 12, 0, 0, TimeSpan.Zero);

        var program = new ProgramRecord("Execution Effectivity", "EXE");
        var project = new ProjectRecord(program.Id, "FMS", "Execution Effectivity FMS");
        var release16 = new SoftwareRelease(project.Id, "1.6", false);
        var release17 = new SoftwareRelease(project.Id, "1.7", false, release16.Id);
        var baseline16 = new CandidateBaseline("SW-01.60", 0, project.Id, release16.Id, null,
            "Build 1.6 baseline", "cm", now);
        var baseline17 = new CandidateBaseline("SW-01.70", 0, project.Id, release17.Id, baseline16.Id,
            "Build 1.7 baseline", "cm", now);
        var release18 = new SoftwareRelease(project.Id, "1.8", false, release17.Id);
        var baseline18 = new CandidateBaseline("SW-01.80", 0, project.Id, release18.Id, baseline17.Id,
            "Build 1.8 baseline", "cm", now);
        var build16 = new SoftwareBuild(project.Id, release16.Id, baseline16.Id, "SW-01.60",
            "Build 1.6 configuration", "cm", now);
        var build17 = new SoftwareBuild(project.Id, release17.Id, baseline17.Id, "SW-01.70",
            "Build 1.7 configuration", "cm", now);
        var build18 = new SoftwareBuild(project.Id, release18.Id, baseline18.Id, "SW-01.80",
            "Build 1.8 configuration", "cm", now);
        var bareBaseline = new CandidateBaseline("SW-01.90", 0, project.Id, release17.Id, null,
            "No manifest baseline", "cm", now);
        var bareBuild = new SoftwareBuild(project.Id, release17.Id, bareBaseline.Id, "SW-01.91",
            "No manifest build", "cm", now);
        // A release chain with no materialized baseline anywhere: scoped execution must fail closed.
        var bareRelease = new SoftwareRelease(project.Id, "9.1", false);
        var otherProgram = new ProgramRecord("Execution Other", "EXO");
        var otherProject = new ProjectRecord(otherProgram.Id, "Other", "Other FMS");
        var otherRelease = new SoftwareRelease(otherProject.Id, "9.0", false);

        var procedure = new TestProcedure(project.Id, "SYSTP-000123", "Verify route sequencing",
            "test.author", now, TestProcedureLevel.System);
        var revision00 = new TestProcedureRevision(procedure.Id, 0, "Verify legacy route sequencing",
            "Preconditions", "Steps", "Expected", TestProcedureState.Approved, "test.author", now,
            effectiveBaselineId: baseline16.Id);
        var revision01 = new TestProcedureRevision(procedure.Id, 1, "Verify route sequencing and discontinuities",
            "Preconditions", "Steps", "Expected", TestProcedureState.Approved, "test.author", now,
            sourceTestChangeRequestId: Guid.NewGuid(), effectiveBaselineId: baseline17.Id);
        var uncarriedProcedure = new TestProcedure(project.Id, "SYSTP-000999", "Approved but not carried",
            "test.author", now, TestProcedureLevel.System);
        var uncarriedRevision = new TestProcedureRevision(uncarriedProcedure.Id, 0, "Not carried anywhere",
            "Preconditions", "Steps", "Expected", TestProcedureState.Approved, "test.author", now,
            effectiveBaselineId: baseline16.Id);

        var user = new UserAccount("execution.tester", "Execution Tester", "execution.tester@example.test",
            IdentityService.HashPassword(AeroLinkApiFactory.MemberPassword), now);
        db.AddRange(
            program, project, release16, release17, release18, baseline16, baseline17, baseline18,
            build16, build17, build18, bareBaseline, bareBuild, bareRelease,
            otherProgram, otherProject, otherRelease,
            procedure, revision00, revision01, uncarriedProcedure, uncarriedRevision,
            new BaselineTestProcedureSelection(baseline16.Id, procedure.Id, revision00.Id),
            new BaselineTestProcedureSelection(baseline17.Id, procedure.Id, revision01.Id),
            new BaselineTestProcedureSelection(baseline18.Id, procedure.Id, revision01.Id),
            user,
            new ProgramMembership(user.Id, program.Id, ProgramRole.TestEngineer, "test.setup", now),
            // Access to the other program lets the workspace middleware pass so the endpoint's own
            // header-release-vs-request-project check is the thing under test.
            new ProgramMembership(user.Id, otherProgram.Id, ProgramRole.TestEngineer, "test.setup", now));
        await db.SaveChangesAsync();
        await db.CandidateBaselines.Where(x => x.Id == baseline16.Id || x.Id == baseline17.Id || x.Id == baseline18.Id)
            .ExecuteUpdateAsync(update => update
                .SetProperty(x => x.RequirementsMaterializedAt, now)
                .SetProperty(x => x.TestProceduresMaterializedAt, now)
                .SetProperty(x => x.TestProceduresHash, "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"));
        return new(project.Id, release16.Id, build16.Id, release17.Id, build17.Id,
            release18.Id, build18.Id, bareBuild.Id, bareRelease.Id, otherRelease.Id,
            procedure.Id, revision00.Id, revision01.Id, uncarriedRevision.Id);
    }

    private static async Task LoginAsync(HttpClient client)
    {
        using var login = await client.PostAsJsonAsync("/api/auth/login",
            new { userName = "execution.tester", password = AeroLinkApiFactory.MemberPassword });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        await SecurityBoundaryTests.AuthorizeMutationsAsync(client);
    }

    private static object ExecutionBody(Guid projectId, Guid revisionId, Guid? buildId) => new
    {
        projectId,
        procedureRevisionId = revisionId,
        softwareBuildId = buildId,
        retestOfExecutionId = (Guid?)null,
        outcome = "Pass",
        configuration = "Test rig 1",
        determination = "The expected behavior was observed.",
        evidenceReference = "controlled://execution-effectivity/result",
        executedAt = DateTimeOffset.UtcNow,
    };

    [Fact]
    public async Task Cross_build_procedure_revisions_are_refused_and_persist_nothing()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        var fixture = await SeedAsync(factory);
        await LoginAsync(client);

        // Build 1.7 carries .01; executing the predecessor .00 as Build 1.7 must be refused.
        using var wrongRevision = await client.PostAsJsonAsync("/api/test-executions",
            ExecutionBody(fixture.ProjectId, fixture.Revision00Id, fixture.Build17Id));
        var wrongBody = await wrongRevision.Content.ReadAsStringAsync();
        Assert.True(wrongRevision.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Conflict,
            $"{(int)wrongRevision.StatusCode}: {wrongBody}");
        Assert.Contains("procedure_revision_not_carried_by_build", wrongBody);

        // An Approved same-Project procedure the build manifest does not carry is also refused.
        using var uncarried = await client.PostAsJsonAsync("/api/test-executions",
            ExecutionBody(fixture.ProjectId, fixture.UncarriedRevisionId, fixture.Build16Id));
        var uncarriedBody = await uncarried.Content.ReadAsStringAsync();
        Assert.True(uncarried.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Conflict,
            $"{(int)uncarried.StatusCode}: {uncarriedBody}");
        Assert.Contains("procedure_revision_not_carried_by_build", uncarriedBody);

        // A successor revision cannot be executed against the earlier build.
        using var successorOnEarlier = await client.PostAsJsonAsync("/api/test-executions",
            ExecutionBody(fixture.ProjectId, fixture.Revision01Id, fixture.Build16Id));
        var successorBody = await successorOnEarlier.Content.ReadAsStringAsync();
        Assert.True(successorOnEarlier.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Conflict,
            $"{(int)successorOnEarlier.StatusCode}: {successorBody}");
        Assert.Contains("procedure_revision_not_carried_by_build", successorBody);

        // Refused writes persist no TestExecution row and no evidence relationship.
        using var verifyScope = factory.Services.CreateScope();
        var db = verifyScope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        Assert.Equal(0, await db.TestExecutions.CountAsync());
    }

    [Fact]
    public async Task Exact_carried_revisions_are_executable_against_their_build()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        var fixture = await SeedAsync(factory);
        await LoginAsync(client);

        using var on16 = await client.PostAsJsonAsync("/api/test-executions",
            ExecutionBody(fixture.ProjectId, fixture.Revision00Id, fixture.Build16Id));
        Assert.Equal(HttpStatusCode.Created, on16.StatusCode);

        using var on17 = await client.PostAsJsonAsync("/api/test-executions",
            ExecutionBody(fixture.ProjectId, fixture.Revision01Id, fixture.Build17Id));
        Assert.Equal(HttpStatusCode.Created, on17.StatusCode);

        using var verifyScope = factory.Services.CreateScope();
        var db = verifyScope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        Assert.Equal(2, await db.TestExecutions.CountAsync());
        Assert.True(await db.TestExecutions.AnyAsync(x => x.ProcedureRevisionId == fixture.Revision00Id
            && x.SoftwareBuildId == fixture.Build16Id));
        Assert.True(await db.TestExecutions.AnyAsync(x => x.ProcedureRevisionId == fixture.Revision01Id
            && x.SoftwareBuildId == fixture.Build17Id));
    }

    [Fact]
    public async Task Release_scoped_executions_without_a_software_build_use_the_effective_revision()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        var fixture = await SeedAsync(factory);
        await LoginAsync(client);

        // Release-scoped through the workspace header only: Build 1.7 effective revision is .01.
        client.DefaultRequestHeaders.Add("X-AeroLink-Build-Context", fixture.Release17Id.ToString());
        using var wrongRevision = await client.PostAsJsonAsync("/api/test-executions",
            ExecutionBody(fixture.ProjectId, fixture.Revision00Id, null));
        var wrongBody = await wrongRevision.Content.ReadAsStringAsync();
        Assert.True(wrongRevision.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Conflict,
            $"{(int)wrongRevision.StatusCode}: {wrongBody}");
        Assert.Contains("procedure_revision_not_carried_by_build", wrongBody);

        using var exactRevision = await client.PostAsJsonAsync("/api/test-executions",
            ExecutionBody(fixture.ProjectId, fixture.Revision01Id, null));
        Assert.Equal(HttpStatusCode.Created, exactRevision.StatusCode);

        client.DefaultRequestHeaders.Remove("X-AeroLink-Build-Context");
        client.DefaultRequestHeaders.Add("X-AeroLink-Build-Context", fixture.Release16Id.ToString());
        using var successorOnEarlier = await client.PostAsJsonAsync("/api/test-executions",
            ExecutionBody(fixture.ProjectId, fixture.Revision01Id, null));
        var successorBody = await successorOnEarlier.Content.ReadAsStringAsync();
        Assert.True(successorOnEarlier.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Conflict,
            $"{(int)successorOnEarlier.StatusCode}: {successorBody}");
        Assert.Contains("procedure_revision_not_carried_by_build", successorBody);
    }

    [Fact]
    public async Task Inherited_unchanged_revision_is_executable_through_predecessor_traversal()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        var fixture = await SeedAsync(factory);
        await LoginAsync(client);

        // Build 1.8 carries .01 unchanged from Build 1.7; the exact carried revision is accepted.
        using var on18 = await client.PostAsJsonAsync("/api/test-executions",
            ExecutionBody(fixture.ProjectId, fixture.Revision01Id, fixture.Build18Id));
        Assert.Equal(HttpStatusCode.Created, on18.StatusCode);

        // Release-header-only traversal over the predecessor chain accepts the effective .01 and refuses .00.
        client.DefaultRequestHeaders.Add("X-AeroLink-Build-Context", fixture.Release18Id.ToString());
        using var exact18 = await client.PostAsJsonAsync("/api/test-executions",
            ExecutionBody(fixture.ProjectId, fixture.Revision01Id, null));
        Assert.Equal(HttpStatusCode.Created, exact18.StatusCode);

        using var predecessorOn18 = await client.PostAsJsonAsync("/api/test-executions",
            ExecutionBody(fixture.ProjectId, fixture.Revision00Id, null));
        var predecessorBody = await predecessorOn18.Content.ReadAsStringAsync();
        Assert.True(predecessorOn18.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Conflict,
            $"{(int)predecessorOn18.StatusCode}: {predecessorBody}");
        Assert.Contains("procedure_revision_not_carried_by_build", predecessorBody);
    }

    [Fact]
    public async Task A_scoped_execution_without_a_controlled_manifest_is_refused()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        var fixture = await SeedAsync(factory);
        await LoginAsync(client);

        using var buildScoped = await client.PostAsJsonAsync("/api/test-executions",
            ExecutionBody(fixture.ProjectId, fixture.Revision00Id, fixture.BareBuildId));
        var buildBody = await buildScoped.Content.ReadAsStringAsync();
        Assert.True(buildScoped.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Conflict,
            $"{(int)buildScoped.StatusCode}: {buildBody}");
        Assert.Contains("procedure_manifest_unavailable", buildBody);

        client.DefaultRequestHeaders.Add("X-AeroLink-Build-Context", fixture.BareReleaseId.ToString());
        using var releaseScoped = await client.PostAsJsonAsync("/api/test-executions",
            ExecutionBody(fixture.ProjectId, fixture.Revision00Id, null));
        var releaseBody = await releaseScoped.Content.ReadAsStringAsync();
        Assert.True(releaseScoped.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Conflict,
            $"{(int)releaseScoped.StatusCode}: {releaseBody}");
        Assert.Contains("procedure_manifest_unavailable", releaseBody);

        using var verifyScope = factory.Services.CreateScope();
        var db = verifyScope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        Assert.Equal(0, await db.TestExecutions.CountAsync());
    }

    [Fact]
    public async Task A_cross_project_release_header_is_refused()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        var fixture = await SeedAsync(factory);
        await LoginAsync(client);

        client.DefaultRequestHeaders.Add("X-AeroLink-Build-Context", fixture.OtherReleaseId.ToString());
        using var refused = await client.PostAsJsonAsync("/api/test-executions",
            ExecutionBody(fixture.ProjectId, fixture.Revision00Id, null));
        var body = await refused.Content.ReadAsStringAsync();
        Assert.True(refused.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Conflict,
            $"{(int)refused.StatusCode}: {body}");
        Assert.Contains("cross_project_release", body);

        using var verifyScope = factory.Services.CreateScope();
        var db = verifyScope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        Assert.Equal(0, await db.TestExecutions.CountAsync());
    }
}
