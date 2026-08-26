using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AeroLink.Domain.Identity;
using AeroLink.Domain.Programs;
using AeroLink.Domain.Releases;
using AeroLink.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace AeroLink.Api.Tests;

/// <summary>
/// The category vocabulary at the API seam: what it serves, what it refuses, and the one gate that makes
/// the field mean something — a report cannot reach the SCCB without a category.
/// </summary>
public sealed class ProblemReportCategoryApiTests
{
    private static async Task<(Guid ProjectId, Guid ReleaseId)> SeedAsync(AeroLinkApiFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        var program = new ProgramRecord("Category Program", $"CAT{Guid.NewGuid():N}"[..12]);
        var project = new ProjectRecord(program.Id, "Flight Management Product", "Flight Management System");
        var release = new SoftwareRelease(project.Id, "1.6", false);
        db.AddRange(program, project, release);
        await db.SaveChangesAsync();
        return (project.Id, release.Id);
    }

    private static async Task<(Guid Id, long Version)> RaiseAsync(HttpClient client, Guid projectId, Guid releaseId,
        string? category)
    {
        using var created = await client.PostAsJsonAsync("/api/problem-reports", new
        {
            projectId, releaseId, category,
            title = "Disconnect tone is late",
            problem = "The tone follows the disconnect by roughly a second.",
            problemRich = "{\"blocks\":[]}",
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var body = await created.Content.ReadFromJsonAsync<JsonElement>();
        return (body.GetProperty("id").GetGuid(), body.GetProperty("version").GetInt64());
    }

    [Fact]
    public async Task The_vocabulary_is_served_whole_so_the_browser_never_spells_a_meaning_of_its_own()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        await ProblemReportApiTests.BootstrapAndLoginAsync(client);

        var body = await client.GetFromJsonAsync<JsonElement>("/api/problem-reports/categories");
        var categories = body.GetProperty("categories").EnumerateArray().ToList();

        Assert.Equal(9, categories.Count);
        Assert.All(categories, category =>
        {
            Assert.Matches("^[1-9][0-9]$", category.GetProperty("code").GetString());
            Assert.False(string.IsNullOrWhiteSpace(category.GetProperty("label").GetString()));
            Assert.False(string.IsNullOrWhiteSpace(category.GetProperty("meaning").GetString()));
            Assert.False(string.IsNullOrWhiteSpace(category.GetProperty("family").GetString()));
        });
        Assert.Equal(6, body.GetProperty("families").EnumerateArray().Count());
        // The two distinctions the retired four-kind vocabulary could not express.
        Assert.Contains(categories, category => category.GetProperty("code").GetString() == "31");
        Assert.Contains(categories, category => category.GetProperty("code").GetString() == "32");
    }

    [Fact]
    public async Task A_raised_report_carries_its_category_resolved_and_marked_as_chosen()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        await ProblemReportApiTests.BootstrapAndLoginAsync(client);
        var (projectId, releaseId) = await SeedAsync(factory);

        var (id, _) = await RaiseAsync(client, projectId, releaseId, "CodeNonFunctional");

        var category = (await client.GetFromJsonAsync<JsonElement>($"/api/problem-reports/{id}")).GetProperty("category");
        Assert.Equal("CodeNonFunctional", category.GetProperty("value").GetString());
        Assert.Equal("32", category.GetProperty("code").GetString());
        Assert.Equal("Code", category.GetProperty("family").GetString());
        Assert.Equal("Selected", category.GetProperty("provenance").GetString());
    }

    /// <summary>
    /// A Draft may be unclassified, and says so rather than defaulting to a category that would read as an
    /// answer somebody gave. Leaving Draft is where it becomes mandatory.
    /// </summary>
    [Fact]
    public async Task An_unclassified_Draft_is_refused_at_the_SCCB_with_a_reason_that_names_the_field()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        await ProblemReportApiTests.BootstrapAndLoginAsync(client);
        var (projectId, releaseId) = await SeedAsync(factory);

        var (id, version) = await RaiseAsync(client, projectId, releaseId, category: null);
        Assert.Equal(JsonValueKind.Null,
            (await client.GetFromJsonAsync<JsonElement>($"/api/problem-reports/{id}")).GetProperty("category").ValueKind);

        using var refused = await client.PostAsJsonAsync($"/api/problem-reports/{id}/ready-for-sccb",
            new { expectedVersion = version });
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        var error = (await refused.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString();
        Assert.Contains("category", error, StringComparison.OrdinalIgnoreCase);

        var stillDraft = await client.GetFromJsonAsync<JsonElement>($"/api/problem-reports/{id}");
        Assert.Equal("Draft", stillDraft.GetProperty("state").GetString());
    }

    [Fact]
    public async Task A_classified_Draft_reaches_the_SCCB()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        await ProblemReportApiTests.BootstrapAndLoginAsync(client);
        var (projectId, releaseId) = await SeedAsync(factory);

        var (id, version) = await RaiseAsync(client, projectId, releaseId, "TestBlocking");

        using var ready = await client.PostAsJsonAsync($"/api/problem-reports/{id}/ready-for-sccb",
            new { expectedVersion = version });
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
        Assert.Equal("ReadyForSccb",
            (await client.GetFromJsonAsync<JsonElement>($"/api/problem-reports/{id}")).GetProperty("state").GetString());
    }

    [Fact]
    public async Task The_queue_filters_by_a_category_and_by_its_whole_family()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        await ProblemReportApiTests.BootstrapAndLoginAsync(client);
        var (projectId, releaseId) = await SeedAsync(factory);

        await RaiseAsync(client, projectId, releaseId, "CodeFunctional");
        await RaiseAsync(client, projectId, releaseId, "CodeNonFunctional");
        await RaiseAsync(client, projectId, releaseId, "TestBlocking");

        var exact = await client.GetFromJsonAsync<JsonElement>(
            $"/api/problem-reports?projectId={projectId}&category=CodeFunctional");
        Assert.Equal(1, exact.GetProperty("totalCount").GetInt32());

        // One click for "every code defect", which is the question people actually ask.
        var family = await client.GetFromJsonAsync<JsonElement>(
            $"/api/problem-reports?projectId={projectId}&categoryFamily=Code");
        Assert.Equal(2, family.GetProperty("totalCount").GetInt32());

        using var unknown = await client.GetAsync($"/api/problem-reports?projectId={projectId}&categoryFamily=Nonsense");
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
        Assert.Equal("pr_category_family_unknown",
            (await unknown.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
    }

    /// <summary>
    /// The retired kinds must not resolve. A stored filter or an import still carrying "Code" is stale, and
    /// answering it with the nearest category would silently narrow a queue to the wrong set.
    /// </summary>
    [Fact]
    public async Task A_retired_kind_is_refused_rather_than_guessed_at()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        await ProblemReportApiTests.BootstrapAndLoginAsync(client);
        var (projectId, _) = await SeedAsync(factory);

        using var response = await client.GetAsync($"/api/problem-reports?projectId={projectId}&category=Code");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
