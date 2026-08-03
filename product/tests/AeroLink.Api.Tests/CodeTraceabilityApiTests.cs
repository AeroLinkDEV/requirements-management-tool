using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;

namespace AeroLink.Api.Tests;

[Collection(ShowcaseApiCollection.Name)]
public sealed class CodeTraceabilityApiTests(ShowcaseApiFixture showcase)
{
    [Fact]
    public async Task Code_gate_is_build_scoped_and_accepts_a_justified_no_code_decision_for_active_work()
    {
        using var factory = showcase.CreateFactory();
        using var client = factory.CreateClient();
        await BootstrapAsync(client);
        var summary = showcase.Summary;

        var active = await client.GetFromJsonAsync<JsonElement>($"/api/code-traceability?projectId={summary.ProjectId}&releaseId={summary.ActiveReleaseId}");
        Assert.False(active.GetProperty("build").GetProperty("readOnly").GetBoolean());
        Assert.True(active.GetProperty("demonstrationScope").GetBoolean());
        Assert.Equal(5, active.GetProperty("summary").GetProperty("required").GetInt32());
        Assert.Equal(4, active.GetProperty("summary").GetProperty("mapped").GetInt32());
        Assert.False(active.GetProperty("summary").GetProperty("gateComplete").GetBoolean());
        Assert.Contains("GitLab is the source of truth", active.GetProperty("sourceOfTruth").GetString());

        var missing = active.GetProperty("requirements").EnumerateArray().Single(x => x.GetProperty("mapping").ValueKind == JsonValueKind.Null);
        using var created = await client.PostAsJsonAsync("/api/code-traceability", new
        {
            projectId = summary.ProjectId,
            releaseId = summary.ActiveReleaseId,
            requirementArtifactId = missing.GetProperty("artifactId").GetGuid(),
            requirementRevisionId = missing.GetProperty("revisionId").GetGuid(),
            disposition = "NoCodeChangeRequired",
            noCodeChangeRationale = "The approved LLR clarifies existing behavior and requires no executable change.",
        });
        var createdBody = await created.Content.ReadAsStringAsync();
        Assert.True(created.StatusCode == HttpStatusCode.Created, $"Expected Created, got {(int)created.StatusCode}: {createdBody}");

        var completed = await client.GetFromJsonAsync<JsonElement>($"/api/code-traceability?projectId={summary.ProjectId}&releaseId={summary.ActiveReleaseId}");
        Assert.Equal(5, completed.GetProperty("summary").GetProperty("mapped").GetInt32());
        Assert.True(completed.GetProperty("summary").GetProperty("gateComplete").GetBoolean());

        using var scope = factory.Services.CreateScope();
        var releasedId = scope.ServiceProvider.GetRequiredService<AeroLink.Infrastructure.Persistence.AeroLinkDbContext>()
            .Releases.Single(x => x.ProjectId == summary.ProjectId && x.IsReleased).Id;
        var released = await client.GetFromJsonAsync<JsonElement>($"/api/code-traceability?projectId={summary.ProjectId}&releaseId={releasedId}");
        Assert.True(released.GetProperty("build").GetProperty("readOnly").GetBoolean());
        Assert.True(released.GetProperty("summary").GetProperty("gateComplete").GetBoolean());

        using var refused = await client.PostAsJsonAsync("/api/code-traceability", new
        {
            projectId = summary.ProjectId,
            releaseId = releasedId,
            requirementArtifactId = missing.GetProperty("artifactId").GetGuid(),
            requirementRevisionId = missing.GetProperty("revisionId").GetGuid(),
            disposition = "NoCodeChangeRequired",
            noCodeChangeRationale = "Must remain historical.",
        });
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
    }

    [Fact]
    public async Task Digital_thread_returns_one_exact_SYSR_to_build_path()
    {
        using var factory = showcase.CreateFactory();
        using var client = factory.CreateClient();
        await BootstrapAsync(client);
        var summary = showcase.Summary;
        var path = await client.GetFromJsonAsync<JsonElement>($"/api/traceability/path?projectId={summary.ProjectId}&baselineId={summary.ReleasedBaselineId}");

        Assert.Equal(["System", "HighLevel", "LowLevel"], path.GetProperty("nodes").EnumerateArray().Select(x => x.GetProperty("level").GetString()!).ToArray());
        Assert.StartsWith("LLRTP-", path.GetProperty("procedure").GetProperty("displayNumber").GetString());
        Assert.Equal("Pass", path.GetProperty("execution").GetProperty("outcome").GetString());
        Assert.False(string.IsNullOrWhiteSpace(path.GetProperty("execution").GetProperty("evidenceReference").GetString()));
        Assert.Contains("1.5", path.GetProperty("build").GetProperty("buildNumber").GetString());
    }

    private static async Task BootstrapAsync(HttpClient client)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/setup/bootstrap")
        {
            Content = JsonContent.Create(new { displayName = "Administrator", email = "admin@example.test", password = AeroLinkApiFactory.AdministratorPassword }),
        };
        request.Headers.Add("X-AeroLink-Bootstrap-Secret", AeroLinkApiFactory.BootstrapSecret);
        using var created = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        using var login = await client.PostAsJsonAsync("/api/auth/login", new { userName = "admin", password = AeroLinkApiFactory.AdministratorPassword });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        await SecurityBoundaryTests.AuthorizeMutationsAsync(client);
    }
}
