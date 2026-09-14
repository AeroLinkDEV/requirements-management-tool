using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace AeroLink.Api.Tests;

/// <summary>Regression coverage for setup provider validation and malformed release input.</summary>
public sealed class ProjectSetupBoundaryRegressionApiTests : IClassFixture<SharedApiHost>
{
    private readonly SharedApiHost _host;

    public ProjectSetupBoundaryRegressionApiTests(SharedApiHost host) => _host = host;

    [Fact]
    public async Task Setup_rejects_unsupported_repository_provider_but_accepts_gitlab_without_network_call()
    {
        using var client = _host.CreateClient();
        await SecurityBoundaryTests.BootstrapAndLoginAdministratorAsync(client);

        using var unsupportedCreated = await client.PostAsJsonAsync("/api/project-setups", new
        {
            projectName = "Unsupported Repository Provider"
        });
        Assert.Equal(HttpStatusCode.Created, unsupportedCreated.StatusCode);
        using var unsupportedBody = JsonDocument.Parse(await unsupportedCreated.Content.ReadAsStringAsync());
        var unsupportedDraftId = unsupportedBody.RootElement.GetProperty("draftId").GetGuid();

        using var rejected = await client.PutAsJsonAsync($"/api/project-setups/{unsupportedDraftId}", new
        {
            expectedVersion = 1,
            currentStep = "Services",
            repository = new
            {
                mode = "ConnectNow",
                provider = "GitHub",
                endpoint = "https://github.example.test/group/repository"
            }
        });
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);

        using var gitlabCreated = await client.PostAsJsonAsync("/api/project-setups", new
        {
            projectName = "Supported GitLab Repository Provider"
        });
        Assert.Equal(HttpStatusCode.Created, gitlabCreated.StatusCode);
        using var gitlabBody = JsonDocument.Parse(await gitlabCreated.Content.ReadAsStringAsync());
        var gitlabDraftId = gitlabBody.RootElement.GetProperty("draftId").GetGuid();

        // Configuration is retained as Configured-unverified; verification is a separate server-side probe.
        using var accepted = await client.PutAsJsonAsync($"/api/project-setups/{gitlabDraftId}", new
        {
            expectedVersion = 1,
            currentStep = "Services",
            repository = new
            {
                mode = "ConnectNow",
                provider = "GitLab",
                endpoint = "https://gitlab.example.test/group/repository"
            }
        });
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        using var acceptedBody = JsonDocument.Parse(await accepted.Content.ReadAsStringAsync());
        var repository = acceptedBody.RootElement.GetProperty("repository");
        Assert.Equal("ConnectNow", repository.GetProperty("mode").GetString());
        Assert.Equal("GitLab", repository.GetProperty("provider").GetString());
        Assert.Equal("https://gitlab.example.test/group/repository", repository.GetProperty("endpoint").GetString());

        await LaterReleaseCreationRejectsNullVersion(client);
    }

    private static async Task LaterReleaseCreationRejectsNullVersion(HttpClient client)
    {
        var suffix = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        using var workspace = await client.PostAsJsonAsync("/api/workspaces", new
        {
            programName = "Null Release Version Program " + suffix,
            programCode = "NRV" + suffix,
            projectName = "Null Release Version Project " + suffix,
            softwareProduct = "Null Release Version Product",
            initialRelease = "1.0",
            initialReleaseIsReleased = true
        });
        Assert.Equal(HttpStatusCode.Created, workspace.StatusCode);
        using var workspaceBody = JsonDocument.Parse(await workspace.Content.ReadAsStringAsync());
        var projectId = workspaceBody.RootElement.GetProperty("project").GetProperty("id").GetGuid();

        using var rejected = await client.PostAsJsonAsync("/api/releases", new
        {
            projectId,
            version = (string?)null,
            predecessorReleaseId = (Guid?)null
        });
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        Assert.Contains("version", await rejected.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
    }
}
