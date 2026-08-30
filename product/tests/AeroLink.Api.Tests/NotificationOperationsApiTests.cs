using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AeroLink.Domain.Identity;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AeroLink.Api.Tests;

public sealed class NotificationOperationsApiTests
{
    [Fact]
    public async Task Authenticated_project_member_cannot_inspect_global_notification_operations()
    {
        using var factory = new AeroLinkApiFactory();
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            db.UserAccounts.Add(new UserAccount("notification.member", "Notification Member", "member@example.test",
                IdentityService.HashPassword(AeroLinkApiFactory.MemberPassword), DateTimeOffset.UtcNow));
            await db.SaveChangesAsync();
        }
        using var member = factory.CreateClient();
        using (var login = await member.PostAsJsonAsync("/api/auth/login", new
        {
            userName = "notification.member", password = AeroLinkApiFactory.MemberPassword
        })) Assert.Equal(HttpStatusCode.OK, login.StatusCode);

        using var rejected = await member.GetAsync("/api/operations/notifications");
        Assert.Equal(HttpStatusCode.Forbidden, rejected.StatusCode);
        using var rejectedTransportTest = await member.PostAsJsonAsync(
            "/api/operations/notifications/transport-test", new { projectId = Guid.NewGuid() });
        Assert.Equal(HttpStatusCode.Forbidden, rejectedTransportTest.StatusCode);
    }

    [Fact]
    public async Task Notification_operations_are_global_administrator_only_and_never_return_secrets_or_bodies()
    {
        using var factory = new AeroLinkApiFactory();
        using (var anonymous = factory.CreateClient())
        {
            using var rejected = await anonymous.GetAsync("/api/operations/notifications");
            Assert.Equal(HttpStatusCode.Unauthorized, rejected.StatusCode);
        }

        using var administrator = factory.CreateClient();
        await SecurityBoundaryTests.BootstrapAndLoginAdministratorAsync(administrator);
        using var workspace = await administrator.PostAsJsonAsync("/api/workspaces", new
        {
            programName = "Notification Operations Program", programCode = "NOP",
            projectName = "Notification Operations Project", softwareProduct = "Notification Operations Product",
            initialRelease = "1.0"
        });
        Assert.Equal(HttpStatusCode.Created, workspace.StatusCode);
        var projectId = (await workspace.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("project").GetProperty("id").GetGuid();

        using var overview = await administrator.GetAsync("/api/operations/notifications");
        Assert.Equal(HttpStatusCode.OK, overview.StatusCode);
        var text = await overview.Content.ReadAsStringAsync();
        Assert.DoesNotContain("Password", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("PlainTextBody", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("HtmlBody", text, StringComparison.OrdinalIgnoreCase);

        using var queued = await administrator.PostAsJsonAsync("/api/operations/notifications/transport-test", new { projectId });
        Assert.Equal(HttpStatusCode.Accepted, queued.StatusCode);
        Assert.Equal("/api/operations/notifications", queued.Headers.Location?.OriginalString);
        var result = await queued.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Pending", result.GetProperty("state").GetString());

        using var afterQueue = await administrator.GetAsync("/api/operations/notifications");
        var masked = await afterQueue.Content.ReadAsStringAsync();
        Assert.DoesNotContain("admin@example.test", masked, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("a***@example.test", masked, StringComparison.OrdinalIgnoreCase);
    }
}
