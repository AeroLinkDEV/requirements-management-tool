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
/// Saved worklists over the test procedure library.
///
/// The verification twin of the requirements saved view, and held to the same rule: what reaches storage is
/// validated at the boundary, because a saved view is a worklist somebody else opens. A field the Explorer
/// cannot apply must be refused when it is written, not silently ignored when it is read.
/// </summary>
public sealed class ProcedureSavedViewApiTests
{
    private const string Member = "view.author";
    private const string Other = "view.reader";

    private sealed record Seeded(Guid ProjectId);

    private static async Task<Seeded> SeedAsync(AeroLinkApiFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        var now = DateTimeOffset.UtcNow;
        var program = new ProgramRecord("View Program", "VIEWP");
        var project = new ProjectRecord(program.Id, "Views", "View Software");
        db.AddRange(program, project);
        foreach (var name in new[] { Member, Other })
        {
            var account = new UserAccount(name, name, $"{name}@example.test",
                IdentityService.HashPassword(AeroLinkApiFactory.MemberPassword), now);
            db.Add(account);
            db.Add(new ProgramMembership(account.Id, program.Id, ProgramRole.TestEngineer, "test.setup", now));
        }
        await db.SaveChangesAsync();
        return new(project.Id);
    }

    private static async Task<HttpClient> SignInAsync(AeroLinkApiFactory factory, string userName)
    {
        var client = factory.CreateClient();
        var login = await client.PostAsJsonAsync("/api/auth/login",
            new { userName, password = AeroLinkApiFactory.MemberPassword });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        await SecurityBoundaryTests.AuthorizeMutationsAsync(client);
        return client;
    }

    private static object Create(Guid projectId, string name, string queryJson, bool shared = false,
        string columnsJson = """["identifier","level","state"]""")
        => new { projectId, name, queryJson, columnsJson, isShared = shared };

    [Fact]
    public async Task A_saved_view_round_trips_on_the_procedure_list()
    {
        await using var factory = new AeroLinkApiFactory();
        var seeded = await SeedAsync(factory);
        using var client = await SignInAsync(factory, Member);

        var created = await client.PostAsJsonAsync("/api/test-procedures/views",
            Create(seeded.ProjectId, "Blocked runs", """{"level":"Software","outcome":"Blocked","state":"Approved"}"""));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        // Carried on the list response, exactly as the requirements workspace carries its own.
        var listed = await client.GetFromJsonAsync<JsonElement>(
            $"/api/test-procedures?projectId={seeded.ProjectId}&pageSize=10");
        var views = listed.GetProperty("views").EnumerateArray().ToList();
        var view = Assert.Single(views);
        Assert.Equal("Blocked runs", view.GetProperty("name").GetString());
        Assert.True(view.GetProperty("owned").GetBoolean());
        Assert.Contains("\"level\":\"Software\"", view.GetProperty("queryJson").GetString());
        Assert.Contains("\"outcome\":\"Blocked\"", view.GetProperty("queryJson").GetString());
    }

    [Theory]
    // A requirements field on a procedure view would be saved and then quietly do nothing.
    [InlineData("""{"verification":"Test"}""", """["identifier"]""")]
    [InlineData("""{"tag":"safety"}""", """["identifier"]""")]
    [InlineData("""{"specificationId":"x"}""", """["identifier"]""")]
    // Vocabularies the Explorer cannot apply.
    [InlineData("""{"outcome":"Passed"}""", """["identifier"]""")]
    [InlineData("""{"state":"Cancelled"}""", """["identifier"]""")]
    [InlineData("""{"level":"Component"}""", """["identifier"]""")]
    // An id that is not one filters to nothing while looking like a worklist.
    [InlineData("""{"documentId":"not-a-guid"}""", """["identifier"]""")]
    // A column this Explorer does not show.
    [InlineData("{}", """["statement"]""")]
    public async Task A_view_the_explorer_cannot_apply_is_refused(string queryJson, string columnsJson)
    {
        await using var factory = new AeroLinkApiFactory();
        var seeded = await SeedAsync(factory);
        using var client = await SignInAsync(factory, Member);

        var response = await client.PostAsJsonAsync("/api/test-procedures/views",
            Create(seeded.ProjectId, "Rejected", queryJson, columnsJson: columnsJson));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("saved_view_contract_invalid", body.GetProperty("code").GetString());
    }

    [Fact]
    public async Task A_repeat_name_is_refused_rather_than_creating_a_second_view_nobody_can_tell_apart()
    {
        await using var factory = new AeroLinkApiFactory();
        var seeded = await SeedAsync(factory);
        using var client = await SignInAsync(factory, Member);

        var first = await client.PostAsJsonAsync("/api/test-procedures/views",
            Create(seeded.ProjectId, "My worklist", """{"state":"Draft"}"""));
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var second = await client.PostAsJsonAsync("/api/test-procedures/views",
            Create(seeded.ProjectId, "My worklist", """{"state":"Approved"}"""));

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        var body = await second.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("saved_view_duplicate_name", body.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Only_the_owner_may_rename_share_or_delete_a_view()
    {
        await using var factory = new AeroLinkApiFactory();
        var seeded = await SeedAsync(factory);
        using var owner = await SignInAsync(factory, Member);
        using var other = await SignInAsync(factory, Other);

        var created = await owner.PostAsJsonAsync("/api/test-cases/views",
            Create(seeded.ProjectId, "Shared worklist", """{"state":"Draft"}""", shared: true));
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        // A shared view is readable by the project...
        var listed = await other.GetFromJsonAsync<JsonElement>(
            $"/api/test-cases?projectId={seeded.ProjectId}&pageSize=10");
        var seen = Assert.Single(listed.GetProperty("views").EnumerateArray().ToList());
        Assert.False(seen.GetProperty("owned").GetBoolean());

        // ...and still belongs to whoever saved it. Not Found rather than Forbidden: confirming that this id
        // exists but is not yours is more than a reader of a shared list needs to know.
        var renamed = await other.PutAsJsonAsync($"/api/test-cases/views/{id}", new { name = "Mine now" });
        Assert.Equal(HttpStatusCode.NotFound, renamed.StatusCode);
        using var deleted = await other.DeleteAsync($"/api/test-procedures/views/{id}");
        Assert.Equal(HttpStatusCode.NotFound, deleted.StatusCode);

        var byOwner = await owner.PutAsJsonAsync($"/api/test-cases/views/{id}", new { name = "Renamed" });
        Assert.Equal(HttpStatusCode.OK, byOwner.StatusCode);
        Assert.Equal("Renamed", (await byOwner.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("name").GetString());

        using var removedByOwner = await owner.DeleteAsync($"/api/test-cases/views/{id}");
        Assert.Equal(HttpStatusCode.NoContent, removedByOwner.StatusCode);
    }

    /// <summary>An update is held to the same contract as a create, or the boundary has a hole in it.</summary>
    [Fact]
    public async Task Replacing_a_views_query_is_validated_too()
    {
        await using var factory = new AeroLinkApiFactory();
        var seeded = await SeedAsync(factory);
        using var client = await SignInAsync(factory, Member);

        var created = await client.PostAsJsonAsync("/api/test-procedures/views",
            Create(seeded.ProjectId, "Worklist", """{"state":"Draft"}"""));
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        var response = await client.PutAsJsonAsync($"/api/test-procedures/views/{id}",
            new { queryJson = """{"tag":"safety"}""" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("saved_view_contract_invalid", body.GetProperty("code").GetString());
    }

    [Fact]
    public async Task A_view_is_not_visible_to_somebody_outside_the_project()
    {
        await using var factory = new AeroLinkApiFactory();
        var seeded = await SeedAsync(factory);
        using var client = await SignInAsync(factory, Member);

        var outsideProject = Guid.NewGuid();
        var response = await client.PostAsJsonAsync("/api/test-procedures/views",
            Create(outsideProject, "Elsewhere", """{"state":"Draft"}"""));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }
}
