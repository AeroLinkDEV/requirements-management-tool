using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AeroLink.Domain.Identity;
using AeroLink.Domain.Programs;
using AeroLink.Domain.Verification;
using AeroLink.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace AeroLink.Api.Tests;

/// <summary>
/// Choosing what a build has to run, over HTTP.
///
/// A build is rarely worth its whole test suite, and which procedures it needs is a planning judgement. These
/// endpoints are that decision; what was actually run against it is recorded elsewhere.
/// </summary>
public sealed class BuildTestSetApiTests
{
    private sealed record Fixture(Guid ProjectId, Guid ReleaseId, Guid ApprovedRevisionId, Guid DraftRevisionId, Guid OtherProjectRevisionId);

    private static async Task<Fixture> SeedAsync(AeroLinkApiFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        var now = DateTimeOffset.UtcNow;

        var program = new ProgramRecord("Plan Program", "PLN");
        var project = new ProjectRecord(program.Id, "Software", "Plan Software");
        var release = new SoftwareRelease(project.Id, "1.6", false);
        var elsewhere = new ProjectRecord(program.Id, "Other Software", "Other Plan Software");
        db.AddRange(program, project, elsewhere, release);

        (TestProcedure Procedure, TestProcedureRevision Revision) Procedure(Guid projectId, string number, TestProcedureState state)
        {
            var procedure = new TestProcedure(projectId, number, "Oceanic sequencing", "plan.engineer", now, TestProcedureLevel.System);
            var revision = new TestProcedureRevision(procedure.Id, 0, "Objective", "Pre", "Steps", "Expected", state, "plan.engineer", now);
            db.AddRange(procedure, revision);
            return (procedure, revision);
        }

        var approved = Procedure(project.Id, "SYSTP-000901", TestProcedureState.Approved);
        var draft = Procedure(project.Id, "SYSTP-000902", TestProcedureState.Draft);
        var other = Procedure(elsewhere.Id, "SYSTP-000903", TestProcedureState.Approved);

        foreach (var (user, role) in new[]
                 {
                     ("plan.lead", ProgramRole.TestLead),
                     ("plan.engineer", ProgramRole.TestEngineer),
                 })
        {
            var account = new UserAccount(user, user, $"{user}@example.test",
                IdentityService.HashPassword(AeroLinkApiFactory.MemberPassword), now);
            db.Add(account);
            db.Add(new ProgramMembership(account.Id, program.Id, role, "test.setup", now));
        }
        await db.SaveChangesAsync();
        return new(project.Id, release.Id, approved.Revision.Id, draft.Revision.Id, other.Revision.Id);
    }

    private static async Task LoginAsync(HttpClient client, string user)
    {
        using var login = await client.PostAsJsonAsync("/api/auth/login",
            new { userName = user, password = AeroLinkApiFactory.MemberPassword });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        await SecurityBoundaryTests.AuthorizeMutationsAsync(client);
    }

    [Fact]
    public async Task A_build_reports_a_set_for_each_discipline_starting_empty()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        var fixture = await SeedAsync(factory);
        await LoginAsync(client, "plan.lead");

        using var response = await client.GetAsync($"/api/releases/{fixture.ReleaseId}/test-sets");
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {body}");
        var sets = JsonSerializer.Deserialize<JsonElement>(body).EnumerateArray().ToList();

        Assert.Equal(3, sets.Count);
        Assert.All(sets, set => Assert.Empty(set.GetProperty("procedures").EnumerateArray()));
    }

    [Fact]
    public async Task A_lead_chooses_procedures_and_the_set_says_who_chose_them_and_why()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        var fixture = await SeedAsync(factory);
        await LoginAsync(client, "plan.lead");

        using var response = await client.PostAsJsonAsync(
            $"/api/releases/{fixture.ReleaseId}/test-sets/System/procedures",
            new { procedureRevisionIds = new[] { fixture.ApprovedRevisionId }, reason = "CoverageArea", note = "Integrity and Monitoring" });
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {body}");

        var result = JsonSerializer.Deserialize<JsonElement>(body);
        Assert.Equal(1, result.GetProperty("added").GetInt32());
        var procedure = result.GetProperty("set").GetProperty("procedures").EnumerateArray().Single();
        Assert.Equal("SYSTP-000901.00", procedure.GetProperty("displayNumber").GetString());
        Assert.Equal("CoverageArea", procedure.GetProperty("reason").GetString());
        Assert.Equal("Integrity and Monitoring", procedure.GetProperty("note").GetString());
        Assert.Equal("plan.lead", procedure.GetProperty("addedBy").GetString());
        // Nothing has been run against it yet, and the set says so rather than leaving the field out.
        Assert.True(procedure.GetProperty("latestOutcome").ValueKind == JsonValueKind.Null);
        Assert.False(procedure.GetProperty("hasEvidence").GetBoolean());
    }

    /// <summary>
    /// Selecting from two directions at once is expected, so the answer is how many were new rather than how
    /// many were named.
    /// </summary>
    [Fact]
    public async Task Choosing_the_same_procedure_again_adds_nothing_and_says_so()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        var fixture = await SeedAsync(factory);
        await LoginAsync(client, "plan.lead");
        var payload = new { procedureRevisionIds = new[] { fixture.ApprovedRevisionId }, reason = "ChangedRequirement", note = "SYSR-000901" };

        using var first = await client.PostAsJsonAsync($"/api/releases/{fixture.ReleaseId}/test-sets/System/procedures", payload);
        Assert.True(first.IsSuccessStatusCode, await first.Content.ReadAsStringAsync());

        using var second = await client.PostAsJsonAsync($"/api/releases/{fixture.ReleaseId}/test-sets/System/procedures",
            new { procedureRevisionIds = new[] { fixture.ApprovedRevisionId }, reason = "CoverageArea", note = "Area sweep" });
        var body = await second.Content.ReadAsStringAsync();
        var result = JsonSerializer.Deserialize<JsonElement>(body);
        Assert.Equal(0, result.GetProperty("added").GetInt32());
        Assert.Equal(1, result.GetProperty("named").GetInt32());
        // The first reason stands: a later route arriving at the same procedure did not put it in the set.
        var procedure = result.GetProperty("set").GetProperty("procedures").EnumerateArray().Single();
        Assert.Equal("ChangedRequirement", procedure.GetProperty("reason").GetString());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_build_can_only_be_set_to_run_approved_procedures_from_its_own_Project(bool useDraft)
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        var fixture = await SeedAsync(factory);
        await LoginAsync(client, "plan.lead");

        var target = useDraft ? fixture.DraftRevisionId : fixture.OtherProjectRevisionId;
        using var response = await client.PostAsJsonAsync(
            $"/api/releases/{fixture.ReleaseId}/test-sets/System/procedures",
            new { procedureRevisionIds = new[] { target }, reason = "Chosen", note = "" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("procedure_not_selectable", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_procedure_can_be_taken_back_out()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        var fixture = await SeedAsync(factory);
        await LoginAsync(client, "plan.lead");
        using var added = await client.PostAsJsonAsync($"/api/releases/{fixture.ReleaseId}/test-sets/System/procedures",
            new { procedureRevisionIds = new[] { fixture.ApprovedRevisionId }, reason = "Chosen", note = "" });
        Assert.True(added.IsSuccessStatusCode, await added.Content.ReadAsStringAsync());

        using var removed = await client.DeleteAsync(
            $"/api/releases/{fixture.ReleaseId}/test-sets/System/procedures/{fixture.ApprovedRevisionId}");
        var body = await removed.Content.ReadAsStringAsync();
        Assert.True(removed.IsSuccessStatusCode, $"{(int)removed.StatusCode}: {body}");
        Assert.Empty(JsonSerializer.Deserialize<JsonElement>(body).GetProperty("procedures").EnumerateArray());

        // Removing it again is the state the caller asked for, not a fault.
        using var again = await client.DeleteAsync(
            $"/api/releases/{fixture.ReleaseId}/test-sets/System/procedures/{fixture.ApprovedRevisionId}");
        Assert.True(again.IsSuccessStatusCode, await again.Content.ReadAsStringAsync());
    }

    /// <summary>
    /// Scope and execution are different jobs even when one person does both: a test engineer records
    /// determinations against the set rather than deciding what is in it.
    /// </summary>
    [Fact]
    public async Task Choosing_what_a_build_runs_takes_lead_authority()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        var fixture = await SeedAsync(factory);
        await LoginAsync(client, "plan.engineer");

        using var response = await client.PostAsJsonAsync(
            $"/api/releases/{fixture.ReleaseId}/test-sets/System/procedures",
            new { procedureRevisionIds = new[] { fixture.ApprovedRevisionId }, reason = "Chosen", note = "" });
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        // Reading it is not restricted: everybody working the build needs to know what it is being measured against.
        using var read = await client.GetAsync($"/api/releases/{fixture.ReleaseId}/test-sets");
        Assert.True(read.IsSuccessStatusCode, await read.Content.ReadAsStringAsync());
    }
}
