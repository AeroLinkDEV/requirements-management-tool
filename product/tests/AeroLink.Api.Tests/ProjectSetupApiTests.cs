using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Identity;
using AeroLink.Domain.Integrations;
using AeroLink.Domain.Programs;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AeroLink.Api.Tests;

/// <summary>API qualification for the durable Fresh project setup boundary.</summary>
public sealed class ProjectSetupApiTests : IClassFixture<SharedApiHost>
{
    private readonly SharedApiHost _host;

    public ProjectSetupApiTests(SharedApiHost host) => _host = host;

    [Fact]
    public async Task Administrator_can_resume_finalize_and_replay_fresh_setup_without_inheriting_content()
    {
        using var anonymous = _host.CreateClient();
        using var denied = await anonymous.PostAsJsonAsync("/api/project-setups", new { projectName = "Denied" });
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);

        using var client = _host.CreateClient();
        await SecurityBoundaryTests.BootstrapAndLoginAdministratorAsync(client);

        using var created = await client.PostAsJsonAsync("/api/project-setups", new { projectName = "Fresh Recovery" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        using var createdBody = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        var draftId = createdBody.RootElement.GetProperty("draftId").GetGuid();
        Assert.Equal("Draft", createdBody.RootElement.GetProperty("state").GetString());
        Assert.Equal(1, createdBody.RootElement.GetProperty("version").GetInt64());

        using var saved = await client.PutAsJsonAsync($"/api/project-setups/{draftId}", new
        {
            expectedVersion = 1,
            currentStep = "Review",
            project = new { name = "Fresh Recovery", softwareProduct = "Recovery Software" },
            start = new { kind = "Fresh" },
            build = new { version = "1.3" },
            selectedCategories = Array.Empty<string>(),
            ladder = new { },
            reviewRules = new { },
            reviewRulesAccepted = true,
            repository = new { mode = "ConfigureLater" },
            mapping = new { },
        });
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        using var savedBody = JsonDocument.Parse(await saved.Content.ReadAsStringAsync());
        Assert.Equal(2, savedBody.RootElement.GetProperty("version").GetInt64());
        Assert.Equal("SW-01.30", savedBody.RootElement.GetProperty("build").GetProperty("officialName").GetString());
        Assert.True(savedBody.RootElement.GetProperty("reviewRules").GetProperty("accepted").GetBoolean());
        Assert.NotEqual(JsonValueKind.Null, savedBody.RootElement.GetProperty("reviewRules").GetProperty("acceptanceHash").ValueKind);

        using var finalized = await client.PostAsJsonAsync($"/api/project-setups/{draftId}/finalize", new
        {
            expectedVersion = 2,
            idempotencyKey = "fresh-recovery-request-1",
        });
        Assert.True(finalized.IsSuccessStatusCode, $"{finalized.StatusCode}: {await finalized.Content.ReadAsStringAsync()}");
        using var finalizedBody = JsonDocument.Parse(await finalized.Content.ReadAsStringAsync());
        Assert.Equal("Completed", finalizedBody.RootElement.GetProperty("state").GetString());
        Assert.False(finalizedBody.RootElement.GetProperty("alreadyCompleted").GetBoolean());
        Assert.Equal("1.3", finalizedBody.RootElement.GetProperty("version").GetString());
        Assert.Equal("SW-01.30", finalizedBody.RootElement.GetProperty("officialBuildName").GetString());
        var projectId = finalizedBody.RootElement.GetProperty("projectId").GetGuid();
        var programId = finalizedBody.RootElement.GetProperty("programId").GetGuid();
        var releaseId = finalizedBody.RootElement.GetProperty("releaseId").GetGuid();

        // A client may lose the success response and retry with the last version it knew. Completed drafts
        // return the committed result rather than creating a second project.
        using var replay = await client.PostAsJsonAsync($"/api/project-setups/{draftId}/finalize", new
        {
            expectedVersion = 2,
            idempotencyKey = "fresh-recovery-request-1-retry",
        });
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        using var replayBody = JsonDocument.Parse(await replay.Content.ReadAsStringAsync());
        Assert.True(replayBody.RootElement.GetProperty("alreadyCompleted").GetBoolean());
        Assert.Equal(projectId, replayBody.RootElement.GetProperty("projectId").GetGuid());
        Assert.Equal(programId, replayBody.RootElement.GetProperty("programId").GetGuid());
        Assert.Equal(releaseId, replayBody.RootElement.GetProperty("releaseId").GetGuid());

        using var scope = _host.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        var draft = await db.ProjectSetupDrafts.AsNoTracking().SingleAsync(x => x.Id == draftId);
        Assert.Equal(ProjectSetupState.Completed, draft.State);
        Assert.Equal(projectId, draft.CompletedProjectId);
        Assert.Equal(1, await db.Programs.CountAsync(x => x.Id == programId));
        Assert.Equal(1, await db.Projects.CountAsync(x => x.Id == projectId));
        Assert.Equal(1, await db.Releases.CountAsync(x => x.Id == releaseId && x.CanonicalIdentity == "SW-01.30"));
        Assert.Equal(0, await db.Requirements.CountAsync(x => x.ProjectId == projectId));
        Assert.Equal(0, await db.TestProcedures.CountAsync(x => x.ProjectId == projectId));
        Assert.True(await db.TestProcedureDocuments.AnyAsync(x => x.ProjectId == projectId));
        Assert.Equal(1, await db.ProgramMemberships.CountAsync(x => x.ProgramId == programId));
        Assert.True(await db.ReviewWorkflows.AnyAsync(x => x.ProjectId == projectId && x.State == ReviewWorkflowState.Active));
        var repository = await db.ProjectRepositoryConfigurations.SingleAsync(x => x.ProjectId == projectId);
        Assert.Equal(ProjectRepositorySetupStatus.Pending, repository.Status);
        Assert.Null(repository.RemoteProjectId);
        Assert.Null(repository.RemotePathWithNamespace);
    }
}
