using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Notifications;
using AeroLink.Domain.Requirements;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AeroLink.Api.Tests;

// Primary exact-link contract: generic /open tests cannot detect an ended original assignment
// being presented as current, or an administrator opening another recipient's original notice.
public sealed class NotificationContextApiTests
{
    [Fact]
    public async Task Original_task_context_survives_completion_without_granting_another_recipient_access()
    {
        using var factory = new AeroLinkApiFactory(); using var client = factory.CreateClient();
        await SecurityBoundaryTests.BootstrapAndLoginAdministratorAsync(client);
        using var workspace = await client.PostAsJsonAsync("/api/workspaces", new
        { programName = "Exact Notice", programCode = "EXN", projectName = "Exact Notice", softwareProduct = "Notice Product", initialRelease = "1.0" });
        Assert.Equal(HttpStatusCode.Created, workspace.StatusCode);
        var project = (await workspace.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("project").GetProperty("id").GetGuid();
        Guid id, otherId, taskId, secondId, secondTaskId, artifactId;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>(); var now = DateTimeOffset.UtcNow;
            var artifact = new RequirementArtifact(project, "SR-90001", RequirementLevel.System, now); artifactId = artifact.Id;
            UserNotification AddTask(string recipient)
            {
                var task = new ArtifactAssignment(project, "Requirement", artifact.Id, null, recipient, "Private title", "Private prose", null, "admin", now);
                var notice = new UserNotification(project, recipient, "RequirementAssignment", "Private title", "Private prose", $"requirement:{artifact.Id}", artifact.Id, now);
                notice.BindContext(NotificationContext.RequirementAssignment(notice, artifact, task));
                db.ArtifactAssignments.Add(task); db.UserNotifications.Add(notice); return notice;
            }
            var mine = AddTask("admin"); id = mine.Id; taskId = mine.Context!.SourceId; var second = AddTask("admin"); secondId = second.Id; secondTaskId = second.Context!.SourceId; otherId = AddTask("someoneelse").Id;
            db.Requirements.Add(artifact); await db.SaveChangesAsync();
        }
        using var active = await client.GetAsync($"/api/notifications/{id}/context"); Assert.Equal(HttpStatusCode.OK, active.StatusCode);
        var context = await active.Content.ReadFromJsonAsync<JsonElement>(); Assert.True(context.GetProperty("originalObligationActive").GetBoolean());
        Assert.Equal(taskId, context.GetProperty("sourceId").GetGuid()); Assert.DoesNotContain("Private prose", context.GetRawText());
        using var other = await client.GetAsync($"/api/notifications/{otherId}/context"); Assert.Equal(HttpStatusCode.NotFound, other.StatusCode);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>(); var task = await db.ArtifactAssignments.SingleAsync(x => x.Id == taskId);
            task.Complete("admin", task.Version, DateTimeOffset.UtcNow); await db.SaveChangesAsync();
        }
        using var ended = await client.GetAsync($"/api/notifications/{id}/context");
        var original = await ended.Content.ReadFromJsonAsync<JsonElement>(); Assert.False(original.GetProperty("originalObligationActive").GetBoolean());
        Assert.Equal(taskId, original.GetProperty("sourceId").GetGuid());
        using var secondActive = await client.GetAsync($"/api/notifications/{secondId}/context");
        var secondContext = await secondActive.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(secondContext.GetProperty("originalObligationActive").GetBoolean());
        Assert.Equal(secondTaskId, secondContext.GetProperty("sourceId").GetGuid()); Assert.NotEqual(taskId, secondTaskId);
        using var current = await client.GetAsync($"/api/notifications/{id}/current"); Assert.Equal(HttpStatusCode.OK, current.StatusCode);
        Assert.Equal($"/open/requirement/{artifactId}", (await current.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("path").GetString());
        using var forbidden = await client.GetAsync($"/api/notifications/{otherId}/current"); Assert.Equal(HttpStatusCode.NotFound, forbidden.StatusCode);
    }
}
