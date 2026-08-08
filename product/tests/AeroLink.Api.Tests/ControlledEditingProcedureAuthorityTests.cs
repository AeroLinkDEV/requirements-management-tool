using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AeroLink.Domain.Identity;
using AeroLink.Domain.Programs;
using AeroLink.Domain.Requirements;
using AeroLink.Domain.Verification;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AeroLink.Api.Tests;

/// <summary>
/// DEC-103 authority boundary for test procedures at the universal controlled-editing API.
///
/// A test procedure is introduced, modified or retired only through a Test Change Request. The generic
/// /api/controlled-editing surface must therefore refuse TestProcedure (and its historical aliases) for
/// checkout, autosave and check-in, while historical Draft revisions stay readable and non-executable.
/// TCR-based authoring and materialization are exercised by their own suites.
/// </summary>
public sealed class ControlledEditingProcedureAuthorityTests
{
    [Theory]
    [InlineData("TestProcedure")]
    [InlineData("Procedure")]
    [InlineData("TestProcedureRevision")]
    public async Task Test_procedure_and_its_aliases_cannot_be_checked_out(string artifactType)
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("Origin", "http://localhost");
        client.DefaultRequestHeaders.Add("Sec-Fetch-Site", "same-origin");
        var seed = await SeedAsync(factory);
        await LoginAsync(client);

        using var checkout = await client.PostAsJsonAsync("/api/controlled-editing/checkout", new
        {
            artifactType,
            artifactId = seed.RevisionId,
        });
        var body = await checkout.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadRequest, checkout.StatusCode);
        Assert.Equal("unsupported_artifact_type", JsonDocument.Parse(body).RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task A_fabricated_test_procedure_session_cannot_autosave_or_check_in()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("Origin", "http://localhost");
        client.DefaultRequestHeaders.Add("Sec-Fetch-Site", "same-origin");
        var seed = await SeedAsync(factory);
        await LoginAsync(client);

        var sessionId = await SeedFabricatedSessionAsync(factory, seed);
        var draftJson = JsonSerializer.Serialize(new
        {
            procedureId = seed.ProcedureId,
            baseNumber = "SYSTP-000001",
            title = "MUST NOT APPLY",
            ownerId = seed.Engineer,
            level = "System",
            version = 0L,
            revisionId = seed.RevisionId,
            revision = 0,
            objective = "MUST NOT APPLY",
            preconditions = "P",
            steps = "MUST NOT APPLY",
            expectedResult = "E",
            state = "Draft",
            authorId = seed.Engineer,
        });

        using var autosave = await client.PutAsJsonAsync(
            $"/api/controlled-editing/sessions/{sessionId}/autosave",
            new { expectedVersion = 1L, draftJson });
        var autosaveBody = await autosave.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.Conflict, autosave.StatusCode);
        Assert.Equal("policy_missing", JsonDocument.Parse(autosaveBody).RootElement.GetProperty("code").GetString());

        using var checkIn = await client.PostAsJsonAsync(
            $"/api/controlled-editing/sessions/{sessionId}/check-in",
            new { expectedVersion = 1L });
        var checkInBody = await checkIn.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.Conflict, checkIn.StatusCode);
        Assert.Equal("check_in_adapter_missing", JsonDocument.Parse(checkInBody).RootElement.GetProperty("code").GetString());

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        db.ChangeTracker.Clear();
        var revision = await db.TestProcedureRevisions.SingleAsync(x => x.Id == seed.RevisionId);
        Assert.Equal("Original objective", revision.Objective);
        Assert.Equal("Original steps", revision.Steps);
        Assert.Equal("Original procedure", (await db.TestProcedures.SingleAsync(x => x.Id == seed.ProcedureId)).Title);
    }

    [Fact]
    public async Task Historical_draft_revisions_remain_readable()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("Origin", "http://localhost");
        client.DefaultRequestHeaders.Add("Sec-Fetch-Site", "same-origin");
        var seed = await SeedAsync(factory);
        await LoginAsync(client);

        using var history = await client.GetAsync(
            $"/api/test-procedures/{seed.ProcedureId}/history?releaseId={seed.ReleaseId}");
        Assert.Equal(HttpStatusCode.OK, history.StatusCode);
        var historyJson = JsonDocument.Parse(await history.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("SYSTP-000001", historyJson.GetProperty("baseNumber").GetString());
        Assert.Equal("Original objective",
            historyJson.GetProperty("revisions")[0].GetProperty("objective").GetString());

        using var page = await client.GetAsync(
            $"/api/test-procedures?projectId={seed.ProjectId}&scope=System&search=SYSTP-000001&page=1&pageSize=10");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        var pageJson = JsonDocument.Parse(await page.Content.ReadAsStringAsync()).RootElement;
        Assert.True(pageJson.GetProperty("totalCount").GetInt32() >= 1);
        Assert.Equal("Draft", pageJson.GetProperty("items")[0].GetProperty("state").GetString());
    }

    [Fact]
    public async Task Historical_draft_revisions_remain_non_executable()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("Origin", "http://localhost");
        client.DefaultRequestHeaders.Add("Sec-Fetch-Site", "same-origin");
        var seed = await SeedAsync(factory);
        await LoginAsync(client);

        using var execution = await client.PostAsJsonAsync("/api/test-executions", new
        {
            projectId = seed.ProjectId,
            procedureRevisionId = seed.RevisionId,
            outcome = "Pass",
            configuration = "Rig",
            determination = "The Draft must not be executable.",
            evidenceReference = "evidence/none",
            executedAt = DateTimeOffset.UtcNow,
        });
        var body = await execution.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadRequest, execution.StatusCode);
        Assert.Contains("Only an approved test procedure revision can be executed", body);
    }

    private static async Task<Seed> SeedAsync(AeroLinkApiFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        var now = DateTimeOffset.UtcNow;
        var program = new ProgramRecord("Authority Boundary Program", "ABP");
        var project = new ProjectRecord(program.Id, "Software", "Authority Boundary Software");
        var release = new SoftwareRelease(project.Id, "1.0", false);
        var procedure = new TestProcedure(project.Id, "SYSTP-000001", "Original procedure",
            "test.engineer", now, TestProcedureLevel.System);
        var revision = new TestProcedureRevision(procedure.Id, 0, "Original objective", "Original preconditions",
            "Original steps", "Original expected", TestProcedureState.Draft, "test.engineer", now);
        var account = new UserAccount("test.engineer", "Test Engineer", "test.engineer@example.test",
            IdentityService.HashPassword(AeroLinkApiFactory.MemberPassword), now);
        db.AddRange(program, project, release, procedure, revision, account);
        db.Add(new ProgramMembership(account.Id, program.Id, ProgramRole.TestEngineer, "test.setup", now));
        await db.SaveChangesAsync();
        return new Seed(project.Id, release.Id, procedure.Id, revision.Id, "test.engineer");
    }

    private static async Task<Guid> SeedFabricatedSessionAsync(AeroLinkApiFactory factory, Seed seed)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        var now = DateTimeOffset.UtcNow;
        var snapshot = "{}";
        var hash = AeroLink.Infrastructure.Persistence.EnterpriseRequirementsService.Hash(
            System.Text.Encoding.UTF8.GetBytes(snapshot));
        var session = new ArtifactEditSession(
            seed.ProjectId, "TestProcedure", seed.RevisionId, seed.RevisionId, hash, snapshot,
            seed.Engineer, now, true, 15);
        db.ArtifactEditSessions.Add(session);
        await db.SaveChangesAsync();
        return session.Id;
    }

    private static async Task LoginAsync(HttpClient client)
    {
        using var login = await client.PostAsJsonAsync("/api/auth/login",
            new { userName = "test.engineer", password = AeroLinkApiFactory.MemberPassword });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        await SecurityBoundaryTests.AuthorizeMutationsAsync(client);
    }

    private sealed record Seed(Guid ProjectId, Guid ReleaseId, Guid ProcedureId, Guid RevisionId, string Engineer);
}
