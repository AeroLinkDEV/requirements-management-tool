using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace AeroLink.Api.Tests;

/// <summary>
/// The setup wizard's Features step (#1113, DEC-136): a new project can start with only the features it uses,
/// and the choice is recorded as the first entry of its feature history.
/// </summary>
public sealed class ProjectSetupFeaturesApiTests : IClassFixture<SharedApiHost>
{
    private static readonly string[] AllFeatures =
        ["TeamWork", "Requirements", "Verification", "Code", "DocumentationCenter", "ProblemReports", "Release"];

    private readonly SharedApiHost _host;

    public ProjectSetupFeaturesApiTests(SharedApiHost host) => _host = host;

    [Fact]
    public async Task A_fresh_setup_starts_with_the_chosen_features_and_records_the_choice()
    {
        using var client = _host.CreateClient();
        await SecurityBoundaryTests.BootstrapAndLoginAdministratorAsync(client);
        var draftId = await CreateDraftAsync(client, "Problem Reports Only Setup");

        // Until the creator chooses, the draft starts with every feature, like a project with no stored set.
        using (var unchosen = await client.GetAsync($"/api/project-setups/{draftId}"))
        using (var body = JsonDocument.Parse(await unchosen.Content.ReadAsStringAsync()))
        {
            Assert.False(body.RootElement.GetProperty("features").GetProperty("chosen").GetBoolean());
            Assert.Equal(AllFeatures, Names(body.RootElement.GetProperty("features").GetProperty("enabled")));
        }

        // The server decides: an unknown name, and each combination DEC-136 refuses, leave the draft unchanged.
        foreach (var (names, error) in new[]
                 {
                     (new[] { "TeamWork", "Nope" }, "'Nope' is not a project feature."),
                     (new[] { "Code" }, "Code needs Requirements"),
                 })
        {
            using var refused = await client.PutAsJsonAsync($"/api/project-setups/{draftId}",
                new { expectedVersion = 1, currentStep = "Features", features = names });
            Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
            Assert.Contains(error, await refused.Content.ReadAsStringAsync());
        }

        string[] chosen = ["TeamWork", "ProblemReports", "Release"];
        using (var saved = await client.PutAsJsonAsync($"/api/project-setups/{draftId}", Answers(1, chosen)))
        {
            Assert.True(saved.IsSuccessStatusCode, await saved.Content.ReadAsStringAsync());
            using var body = JsonDocument.Parse(await saved.Content.ReadAsStringAsync());
            Assert.Equal(2, body.RootElement.GetProperty("version").GetInt64());
            Assert.True(body.RootElement.GetProperty("features").GetProperty("chosen").GetBoolean());
            Assert.Equal(chosen, Names(body.RootElement.GetProperty("features").GetProperty("enabled")));
        }

        // Omitting features on a later save keeps the choice.
        using (var later = await client.PutAsJsonAsync($"/api/project-setups/{draftId}", new { expectedVersion = 2, currentStep = "Review" }))
        using (var body = JsonDocument.Parse(await later.Content.ReadAsStringAsync()))
            Assert.Equal(chosen, Names(body.RootElement.GetProperty("features").GetProperty("enabled")));

        var projectId = await FinalizeAsync(client, draftId, 3, "problem-reports-only-setup-1");

        using var features = await client.GetAsync($"/api/projects/{projectId}/features");
        Assert.Equal(HttpStatusCode.OK, features.StatusCode);
        using var featuresBody = JsonDocument.Parse(await features.Content.ReadAsStringAsync());
        var root = featuresBody.RootElement;
        Assert.True(root.GetProperty("persisted").GetBoolean());
        Assert.Equal(1, root.GetProperty("version").GetInt64());
        Assert.Equal(chosen, Names(root.GetProperty("enabled")));
        var history = Assert.Single(root.GetProperty("history").EnumerateArray());
        Assert.Equal("Chosen during project setup.", history.GetProperty("reason").GetString());
        Assert.Equal(AllFeatures, Names(history.GetProperty("previous")));
        Assert.Equal(chosen, Names(history.GetProperty("enabled")));
        Assert.Equal("admin", history.GetProperty("actor").GetString());
        Assert.Matches("^[0-9a-f]{64}$", history.GetProperty("snapshotHash").GetString());

        // Choosing every feature is the default, so it stores no feature set and no history, exactly like a
        // project created before the step existed.
        var everyDraftId = await CreateDraftAsync(client, "Every Feature Setup");
        using (var saved = await client.PutAsJsonAsync($"/api/project-setups/{everyDraftId}", Answers(1, AllFeatures)))
            Assert.True(saved.IsSuccessStatusCode, await saved.Content.ReadAsStringAsync());
        var everyProjectId = await FinalizeAsync(client, everyDraftId, 2, "every-feature-setup-1");
        using var every = await client.GetAsync($"/api/projects/{everyProjectId}/features");
        using var everyBody = JsonDocument.Parse(await every.Content.ReadAsStringAsync());
        Assert.False(everyBody.RootElement.GetProperty("persisted").GetBoolean());
        Assert.Equal(0, everyBody.RootElement.GetProperty("version").GetInt64());
        Assert.Equal(AllFeatures, Names(everyBody.RootElement.GetProperty("enabled")));
        Assert.Empty(everyBody.RootElement.GetProperty("history").EnumerateArray());
    }

    private static object Answers(long expectedVersion, string[] features) => new
    {
        expectedVersion,
        currentStep = "Review",
        project = new { name = "Features Project", softwareProduct = "Features Software" },
        start = new { kind = "Fresh" },
        build = new { version = "1.3" },
        selectedCategories = Array.Empty<string>(),
        ladder = new { },
        reviewRules = new { },
        reviewRulesAccepted = true,
        repository = new { mode = "ConfigureLater" },
        mapping = new { },
        features,
    };

    private static async Task<Guid> CreateDraftAsync(HttpClient client, string name)
    {
        using var created = await client.PostAsJsonAsync("/api/project-setups", new { projectName = name });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        using var body = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        return body.RootElement.GetProperty("draftId").GetGuid();
    }

    private static async Task<Guid> FinalizeAsync(HttpClient client, Guid draftId, long expectedVersion, string key)
    {
        using var finalized = await client.PostAsJsonAsync($"/api/project-setups/{draftId}/finalize",
            new { expectedVersion, idempotencyKey = key });
        Assert.True(finalized.IsSuccessStatusCode, $"{finalized.StatusCode}: {await finalized.Content.ReadAsStringAsync()}");
        using var body = JsonDocument.Parse(await finalized.Content.ReadAsStringAsync());
        return body.RootElement.GetProperty("projectId").GetGuid();
    }

    private static string[] Names(JsonElement array) => array.EnumerateArray().Select(x => x.GetString()!).ToArray();
}
