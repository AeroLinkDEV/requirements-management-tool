using System.Net.Http.Json;
using System.Text.Json;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AeroLink.Api.Tests;

public sealed class ProjectSetupLadderCapabilityApiTests
{
    [Theory]
    [InlineData("Interface")]
    [InlineData("HighLevel")]
    public async Task Fresh_capability_subset_has_no_inapplicable_review_workflow(string level)
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        await SecurityBoundaryTests.BootstrapAndLoginAdministratorAsync(client);
        var created = await ReadAsync(await client.PostAsJsonAsync("/api/project-setups", new { projectName = "Capability subset" }));
        var draftId = created.GetProperty("draftId").GetGuid();
        var saved = await ReadAsync(await client.PutAsJsonAsync($"/api/project-setups/{draftId}", new
        {
            expectedVersion = 1, currentStep = "Review",
            project = new { name = "Capability subset", softwareProduct = "Subset product" },
            start = new { kind = "Fresh" }, build = new { version = "1.02" },
            ladder = new { steps = new[] { new { catalogueEntry = level, position = 1,
                capabilities = 0, enabledArtifactKinds = Array.Empty<string>() } }, relationships = Array.Empty<object>() },
            reviewRules = new { rules = Array.Empty<object>() }, reviewRulesAccepted = true,
            repository = new { mode = "ConfigureLater" }, mapping = new { }, selectedCategories = Array.Empty<string>()
        }));
        Assert.Empty(saved.GetProperty("reviewRules").GetProperty("suggestedDefinition").GetProperty("rules").EnumerateArray());
        var resumed = await client.GetFromJsonAsync<JsonElement>($"/api/project-setups/{draftId}");
        Assert.Empty(resumed.GetProperty("reviewRules").GetProperty("definition").GetProperty("rules").EnumerateArray());
        var result = await ReadAsync(await client.PostAsJsonAsync($"/api/project-setups/{draftId}/finalize",
            new { expectedVersion = 2, idempotencyKey = "capability-subset" }));
        var projectId = result.GetProperty("projectId").GetGuid();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        Assert.Empty(await db.ReviewWorkflows.Where(x => x.ProjectId == projectId).ToListAsync());
        Assert.Empty(await db.TestProcedureDocuments.Where(x => x.ProjectId == projectId).ToListAsync());
        Assert.Empty(await db.Requirements.Where(x => x.ProjectId == projectId).ToListAsync());
        Assert.Empty(await db.CandidateBaselines.Where(x => x.ProjectId == projectId).ToListAsync());
    }

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage response)
    {
        using (response)
        {
            var body = await response.Content.ReadAsStringAsync();
            Assert.True(response.IsSuccessStatusCode, body);
            return JsonDocument.Parse(body).RootElement.Clone();
        }
    }
}
