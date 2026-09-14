using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AeroLink.Domain.Identity;
using AeroLink.Domain.Programs;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AeroLink.Api.Tests;

/// <summary>AR09 coverage for the split between draft management and project creation authority.</summary>
public sealed class ProjectSetupAuthorizationApiTests
{
    [Fact]
    public async Task Former_creator_can_resume_and_save_but_current_administrator_must_finalize()
    {
        using var factory = new AeroLinkApiFactory();
        using var administrator = factory.CreateClient();
        await SecurityBoundaryTests.BootstrapAndLoginAdministratorAsync(administrator);

        const string creatorName = "setup.former.creator";
        var creatorPassword = AeroLinkApiFactory.MemberPassword;
        Guid draftId;
        using (var seed = factory.Services.CreateScope())
        {
            var db = seed.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var creator = new UserAccount(creatorName, "Setup Former Creator", "setup.former.creator@example.test",
                IdentityService.HashPassword(creatorPassword), DateTimeOffset.UtcNow);
            var draft = new ProjectSetupDraft(creator.Id, creator.UserName, "Administrator handoff");
            db.AddRange(creator, draft);
            await db.SaveChangesAsync();
            draftId = draft.Id;
        }

        using var creatorClient = factory.CreateClient();
        using var login = await creatorClient.PostAsJsonAsync("/api/auth/login",
            new { userName = creatorName, password = creatorPassword });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        await SecurityBoundaryTests.AuthorizeMutationsAsync(creatorClient);

        using var resumed = await creatorClient.GetAsync($"/api/project-setups/{draftId}");
        Assert.Equal(HttpStatusCode.OK, resumed.StatusCode);

        using var saved = await creatorClient.PutAsJsonAsync($"/api/project-setups/{draftId}", new
        {
            expectedVersion = 1,
            currentStep = "Review",
            project = new { name = "Administrator handoff", softwareProduct = "Handoff product" },
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

        using var creatorFinalize = await creatorClient.PostAsJsonAsync($"/api/project-setups/{draftId}/finalize", new
        {
            expectedVersion = 2,
            idempotencyKey = "former-creator-must-not-create",
        });
        Assert.Equal(HttpStatusCode.Forbidden, creatorFinalize.StatusCode);

        using var adminFinalize = await administrator.PostAsJsonAsync($"/api/project-setups/{draftId}/finalize", new
        {
            expectedVersion = 2,
            idempotencyKey = "current-admin-creates",
        });
        Assert.Equal(HttpStatusCode.OK, adminFinalize.StatusCode);
        using var result = JsonDocument.Parse(await adminFinalize.Content.ReadAsStringAsync());
        var programId = result.RootElement.GetProperty("programId").GetGuid();
        var projectId = result.RootElement.GetProperty("projectId").GetGuid();

        using var verify = factory.Services.CreateScope();
        var verificationDb = verify.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        var management = await verificationDb.ProgramMemberships.AsNoTracking().SingleAsync(x => x.ProgramId == programId);
        Assert.Equal(creatorName, (await verificationDb.UserAccounts.AsNoTracking().SingleAsync(x => x.Id == management.UserId)).UserName);
        Assert.Equal("admin", management.GrantedBy);
        var completion = await verificationDb.SecurityAuditEvents.AsNoTracking().SingleAsync(x =>
            x.EventType == "ProjectSetupCompleted" && x.Target == draftId.ToString("D"));
        Assert.Equal("admin", completion.ActorId);
        Assert.Contains(projectId.ToString("D"), completion.Detail, StringComparison.Ordinal);
    }
}
