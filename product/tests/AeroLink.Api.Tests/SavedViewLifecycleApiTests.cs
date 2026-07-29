using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AeroLink.Domain.Identity;
using AeroLink.Domain.Programs;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AeroLink.Api.Tests;

/// <summary>
/// Saved views could be created and linked to, and nothing else. Their contracts were stored exactly as the
/// browser sent them, so a field the workspace cannot apply or a column it cannot show was persisted and then
/// read by everyone the view was shared with; and repeated use left duplicates that no path in the product
/// could rename or remove.
/// </summary>
public sealed class SavedViewLifecycleApiTests
{
    private const string Owner = "view.owner";
    private const string Other = "view.other";

    private static async Task<Guid> SeedAsync(AeroLinkApiFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        var now = DateTimeOffset.UtcNow;
        var program = new ProgramRecord("View Program", "VWP");
        var project = new ProjectRecord(program.Id, "Software", "View Software");
        db.AddRange(program, project);
        foreach (var user in new[] { Owner, Other })
        {
            var account = new UserAccount(user, user, $"{user}@example.test",
                IdentityService.HashPassword(AeroLinkApiFactory.MemberPassword), now);
            db.Add(account);
            db.Add(new ProgramMembership(account.Id, program.Id, ProgramRole.Engineer, "test.setup", now));
        }
        await db.SaveChangesAsync();
        return project.Id;
    }

    private static async Task SignInAsync(HttpClient client, string user)
    {
        using var login = await client.PostAsJsonAsync("/api/auth/login",
            new { userName = user, password = AeroLinkApiFactory.MemberPassword });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        await SecurityBoundaryTests.AuthorizeMutationsAsync(client);
    }

    private static Task<HttpResponseMessage> CreateAsync(HttpClient client, Guid projectId, string name,
        string query = "{\"search\":\"oceanic\",\"sort\":\"identifier\"}", string columns = "[\"identifier\",\"statement\"]",
        bool shared = true) =>
        client.PostAsJsonAsync("/api/enterprise-requirements/views",
            new { projectId, name, queryJson = query, columnsJson = columns, isShared = shared });

    [Fact]
    public async Task A_view_contract_is_validated_before_it_is_stored()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        var projectId = await SeedAsync(factory);
        await SignInAsync(client, Owner);

        // A field the workspace cannot apply, a sort it cannot perform, and a column it cannot show.
        foreach (var (query, columns) in new[]
                 {
                     ("{\"deleteEverything\":true}", "[\"identifier\"]"),
                     ("{\"sort\":\"by-vibes\"}", "[\"identifier\"]"),
                     ("{\"search\":\"x\"}", "[\"social-security-number\"]"),
                     ("not json at all", "[\"identifier\"]"),
                 })
        {
            using var rejected = await CreateAsync(client, projectId, $"Rejected {Guid.NewGuid():N}", query, columns);
            Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        }

        using var accepted = await CreateAsync(client, projectId, "Accepted view");
        Assert.Equal(HttpStatusCode.Created, accepted.StatusCode);

        // Stored normalized, and stamped with the version of the contract it was written against.
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        var stored = await db.SavedRequirementViews.AsNoTracking().SingleAsync();
        Assert.Equal(1, JsonDocument.Parse(stored.QueryJson).RootElement.GetProperty("version").GetInt32());
    }

    [Fact]
    public async Task A_repeated_name_is_refused_and_says_so_rather_than_creating_a_second_view()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        var projectId = await SeedAsync(factory);
        await SignInAsync(client, Owner);

        using (var first = await CreateAsync(client, projectId, "Oceanic worklist"))
            Assert.Equal(HttpStatusCode.Created, first.StatusCode);

        using var duplicate = await CreateAsync(client, projectId, "Oceanic worklist");
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        var body = JsonDocument.Parse(await duplicate.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("saved_view_duplicate_name", body.GetProperty("code").GetString());
        Assert.Contains("Oceanic worklist", body.GetProperty("error").GetString());

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        Assert.Equal(1, await db.SavedRequirementViews.CountAsync());
    }

    [Fact]
    public async Task An_owner_can_rename_reshare_and_delete_and_nobody_else_can()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        var projectId = await SeedAsync(factory);
        await SignInAsync(client, Owner);

        using var created = await CreateAsync(client, projectId, "Original name");
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var id = JsonDocument.Parse(await created.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();

        using (var renamed = await client.PutAsJsonAsync($"/api/enterprise-requirements/views/{id}", new { name = "Renamed view" }))
        {
            Assert.Equal(HttpStatusCode.OK, renamed.StatusCode);
            Assert.Equal("Renamed view", JsonDocument.Parse(await renamed.Content.ReadAsStringAsync()).RootElement.GetProperty("name").GetString());
        }

        using (var unshared = await client.PutAsJsonAsync($"/api/enterprise-requirements/views/{id}", new { isShared = false }))
        {
            Assert.Equal(HttpStatusCode.OK, unshared.StatusCode);
            Assert.False(JsonDocument.Parse(await unshared.Content.ReadAsStringAsync()).RootElement.GetProperty("isShared").GetBoolean());
        }

        // Reshared, so somebody else can see it — and still not alter it.
        using (var reshared = await client.PutAsJsonAsync($"/api/enterprise-requirements/views/{id}", new { isShared = true }))
            Assert.Equal(HttpStatusCode.OK, reshared.StatusCode);

        using var otherClient = factory.CreateClient();
        await SignInAsync(otherClient, Other);
        using (var stolen = await otherClient.PutAsJsonAsync($"/api/enterprise-requirements/views/{id}", new { name = "Not yours" }))
            Assert.Equal(HttpStatusCode.NotFound, stolen.StatusCode);
        using (var removed = await otherClient.DeleteAsync($"/api/enterprise-requirements/views/{id}"))
            Assert.Equal(HttpStatusCode.NotFound, removed.StatusCode);

        using (var deleted = await client.DeleteAsync($"/api/enterprise-requirements/views/{id}"))
            Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        Assert.Empty(await db.SavedRequirementViews.AsNoTracking().ToListAsync());
    }

    /// <summary>
    /// Rows written before the contract existed carry no version and no normalization. They must still open,
    /// because refusing to show a saved view whose storage format predates the reader is a defect in the
    /// reader.
    /// </summary>
    [Fact]
    public async Task A_view_stored_before_the_contract_existed_is_still_readable()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        var projectId = await SeedAsync(factory);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var ownerId = await db.UserAccounts.Where(x => x.UserName == Owner).Select(x => x.Id).SingleAsync();
            db.SavedRequirementViews.Add(new(projectId, ownerId, "Legacy view", "{\"search\":\"legacy\"}", "[\"identifier\"]", true, DateTimeOffset.UtcNow));
            await db.SaveChangesAsync();
        }

        await SignInAsync(client, Owner);
        using var response = await client.GetAsync($"/api/enterprise-requirements/workspace?projectId={projectId}&page=1&pageSize=5");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var views = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("views");
        Assert.Equal("Legacy view", views.EnumerateArray().Single().GetProperty("name").GetString());
    }
}
