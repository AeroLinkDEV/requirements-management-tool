using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AeroLink.Domain.Baselines;
using AeroLink.Domain.Identity;
using AeroLink.Domain.Programs;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AeroLink.Api.Tests;

/// <summary>
/// Discard for an unfinished saved setup. Discard is logical abandonment of draft work, never deletion of a
/// Project: it refuses anything whose meaning abandonment would hide, keeps every staged source and shared
/// evidence row, and leaves an attributable record of who discarded what.
/// </summary>
public sealed class ProjectSetupDiscardApiTests
{
    [Fact]
    public async Task Creator_discards_an_unfinished_setup_and_a_stale_page_cannot_save_or_finalize_it()
    {
        using var factory = new AeroLinkApiFactory();
        using var admin = factory.CreateClient();
        await SecurityBoundaryTests.BootstrapAndLoginAdministratorAsync(admin);
        var (draftId, _) = await CreateSavedDraftAsync(admin, "Discarded observation");

        using var listed = JsonDocument.Parse(await admin.GetStringAsync("/api/project-setups"));
        Assert.Contains(listed.RootElement.EnumerateArray(), x => x.GetProperty("draftId").GetGuid() == draftId);

        using var discarded = await admin.PostAsJsonAsync($"/api/project-setups/{draftId}/discard",
            new { expectedVersion = 2 });
        Assert.True(discarded.IsSuccessStatusCode, await discarded.Content.ReadAsStringAsync());
        using var discardedBody = JsonDocument.Parse(await discarded.Content.ReadAsStringAsync());
        Assert.Equal("Abandoned", discardedBody.RootElement.GetProperty("state").GetString());
        // The draft keeps its answers and comes back with a new version, so an open page can be told what
        // happened and a second request for the same outcome is not a second, competing write.
        Assert.Equal("Discarded observation", discardedBody.RootElement.GetProperty("project").GetProperty("name").GetString());
        Assert.Equal(3, discardedBody.RootElement.GetProperty("version").GetInt64());

        // Discovery is the product's statement of what is still active work.
        using var after = JsonDocument.Parse(await admin.GetStringAsync("/api/project-setups"));
        Assert.DoesNotContain(after.RootElement.EnumerateArray(), x => x.GetProperty("draftId").GetGuid() == draftId);

        // An open page that still names the discarded setup is told the recorded state rather than left with
        // an editable walkthrough that can only fail.
        using var reopened = await admin.GetAsync($"/api/project-setups/{draftId}");
        Assert.Equal(HttpStatusCode.OK, reopened.StatusCode);
        using var reopenedBody = JsonDocument.Parse(await reopened.Content.ReadAsStringAsync());
        Assert.Equal("Abandoned", reopenedBody.RootElement.GetProperty("state").GetString());

        using var staleSave = await admin.PutAsJsonAsync($"/api/project-setups/{draftId}", new
        {
            expectedVersion = 2,
            currentStep = "Details",
            project = new { name = "Resurrected by a stale page" },
        });
        Assert.Equal(HttpStatusCode.BadRequest, staleSave.StatusCode);
        Assert.Contains("discarded", await staleSave.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);

        using var staleFinalize = await admin.PostAsJsonAsync($"/api/project-setups/{draftId}/finalize",
            new { expectedVersion = 2, idempotencyKey = "discarded-setup-must-not-create" });
        Assert.Equal(HttpStatusCode.BadRequest, staleFinalize.StatusCode);
        Assert.Contains("discarded", await staleFinalize.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);

        // Repeating the request reaches the state the caller asked for instead of inventing a conflict.
        using var repeated = await admin.PostAsJsonAsync($"/api/project-setups/{draftId}/discard",
            new { expectedVersion = 2 });
        Assert.True(repeated.IsSuccessStatusCode, await repeated.Content.ReadAsStringAsync());
        using var repeatedBody = JsonDocument.Parse(await repeated.Content.ReadAsStringAsync());
        Assert.Equal("Abandoned", repeatedBody.RootElement.GetProperty("state").GetString());

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        var audit = Assert.Single(await db.SecurityAuditEvents.AsNoTracking()
            .Where(x => x.EventType == "ProjectSetupDiscarded" && x.Target == draftId.ToString("D")).ToListAsync());
        Assert.Equal("Success", audit.Outcome);
        Assert.Contains("Discarded observation", audit.Detail, StringComparison.Ordinal);
        Assert.False(await db.Projects.AnyAsync(x => x.Id == draftId), "discard must never create a Project");
    }

    [Fact]
    public async Task Discard_refuses_a_stale_version_and_setups_that_are_finalizing_or_completed()
    {
        using var factory = new AeroLinkApiFactory();
        using var admin = factory.CreateClient();
        await SecurityBoundaryTests.BootstrapAndLoginAdministratorAsync(admin);

        // A page that was opened before somebody saved a newer answer must not silently discard that work.
        var (staleDraftId, _) = await CreateSavedDraftAsync(admin, "Stale discard page");
        using var stale = await admin.PostAsJsonAsync($"/api/project-setups/{staleDraftId}/discard",
            new { expectedVersion = 1 });
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Contains("draft_conflict", await stale.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        using var stillDraft = JsonDocument.Parse(await admin.GetStringAsync($"/api/project-setups/{staleDraftId}"));
        Assert.Equal("Draft", stillDraft.RootElement.GetProperty("state").GetString());
        Assert.Equal(2, stillDraft.RootElement.GetProperty("version").GetInt64());

        // A finalization in flight is not discardable: the outcome has not been established yet.
        var (finalizingDraftId, finalizingVersion) = await CreateSavedDraftAsync(admin, "Finalizing discard page");
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var finalizing = await db.ProjectSetupDrafts.SingleAsync(x => x.Id == finalizingDraftId);
            finalizing.BeginFinalization(finalizingVersion, "in-flight-discard-probe", DateTimeOffset.UtcNow);
            await db.SaveChangesAsync();
        }
        using var finalizingRefusal = await admin.PostAsJsonAsync($"/api/project-setups/{finalizingDraftId}/discard",
            new { expectedVersion = finalizingVersion });
        Assert.Equal(HttpStatusCode.BadRequest, finalizingRefusal.StatusCode);
        Assert.Contains("being finalized", await finalizingRefusal.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        // A created Project is never reached through discard, and its controlled state stays answerable.
        var (completedDraftId, completedVersion) = await CreateSavedDraftAsync(admin, "Completed discard page");
        using var finalized = await admin.PostAsJsonAsync($"/api/project-setups/{completedDraftId}/finalize",
            new { expectedVersion = completedVersion, idempotencyKey = "completed-before-discard" });
        Assert.True(finalized.IsSuccessStatusCode, await finalized.Content.ReadAsStringAsync());
        using var finalizedBody = JsonDocument.Parse(await finalized.Content.ReadAsStringAsync());
        var projectId = finalizedBody.RootElement.GetProperty("projectId").GetGuid();
        var releaseId = finalizedBody.RootElement.GetProperty("releaseId").GetGuid();
        using var completedRead = JsonDocument.Parse(await admin.GetStringAsync($"/api/project-setups/{completedDraftId}"));
        Assert.Equal("Completed", completedRead.RootElement.GetProperty("state").GetString());
        using var completedRefusal = await admin.PostAsJsonAsync($"/api/project-setups/{completedDraftId}/discard",
            new { expectedVersion = completedRead.RootElement.GetProperty("version").GetInt64() });
        Assert.Equal(HttpStatusCode.BadRequest, completedRefusal.StatusCode);
        Assert.Contains("completed setup", await completedRefusal.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
        using var scopeAfter = factory.Services.CreateScope();
        var after = scopeAfter.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        Assert.True(await after.Projects.AnyAsync(x => x.Id == projectId));
        Assert.True(await after.Releases.AnyAsync(x => x.Id == releaseId));
        Assert.Equal(ProjectSetupState.Completed,
            await after.ProjectSetupDrafts.Where(x => x.Id == completedDraftId).Select(x => x.State).SingleAsync());
    }

    [Fact]
    public async Task An_unrelated_account_cannot_discard_another_creators_setup()
    {
        using var factory = new AeroLinkApiFactory();
        using var admin = factory.CreateClient();
        await SecurityBoundaryTests.BootstrapAndLoginAdministratorAsync(admin);
        var tag = Guid.NewGuid().ToString("N");
        var now = DateTimeOffset.UtcNow;
        UserAccount Account(string role) => new($"discard.{role}.{tag}", role, $"{role}.{tag}@example.test",
            IdentityService.HashPassword(AeroLinkApiFactory.MemberPassword), now);
        var creator = Account("creator");
        var outsider = Account("outsider");
        var draft = new ProjectSetupDraft(creator.Id, creator.UserName, "Somebody else's setup");
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            db.AddRange(creator, outsider, draft);
            await db.SaveChangesAsync();
        }

        using var outsiderClient = factory.CreateClient();
        using (var login = await outsiderClient.PostAsJsonAsync("/api/auth/login",
            new { userName = outsider.UserName, password = AeroLinkApiFactory.MemberPassword }))
            Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        await SecurityBoundaryTests.AuthorizeMutationsAsync(outsiderClient);
        using var refused = await outsiderClient.PostAsJsonAsync($"/api/project-setups/{draft.Id}/discard",
            new { expectedVersion = 1 });
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        using var hidden = JsonDocument.Parse(await outsiderClient.GetStringAsync("/api/project-setups"));
        Assert.DoesNotContain(hidden.RootElement.EnumerateArray(), x => x.GetProperty("draftId").GetGuid() == draft.Id);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            Assert.Equal(ProjectSetupState.Draft,
                await db.ProjectSetupDrafts.Where(x => x.Id == draft.Id).Select(x => x.State).SingleAsync());
        }

        // The creator may discard their own setup, and the administrator boundary is not weakened by this.
        using var creatorClient = factory.CreateClient();
        using (var login = await creatorClient.PostAsJsonAsync("/api/auth/login",
            new { userName = creator.UserName, password = AeroLinkApiFactory.MemberPassword }))
            Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        await SecurityBoundaryTests.AuthorizeMutationsAsync(creatorClient);
        using var discarded = await creatorClient.PostAsJsonAsync($"/api/project-setups/{draft.Id}/discard",
            new { expectedVersion = 1 });
        Assert.True(discarded.IsSuccessStatusCode, await discarded.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Discarding_an_unfinished_setup_preserves_its_staged_native_source_and_the_source_baseline()
    {
        using var factory = new AeroLinkApiFactory();
        using var admin = factory.CreateClient();
        await SecurityBoundaryTests.BootstrapAndLoginAdministratorAsync(admin);

        var now = DateTimeOffset.UtcNow;
        Guid sourceBaselineId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var program = new ProgramRecord("Discard source program", "DSP");
            var project = new ProjectRecord(program.Id, "Discard source", "Discard product");
            var release = new SoftwareRelease(project.Id, "1.0", false);
            var baseline = new CandidateBaseline("SW-93.01", 0, project.Id, release.Id, null,
                "Discard source", "source.manager", now);
            baseline.FreezeForInception("source.manager", now);
            baseline.MarkRequirementsMaterialized("source.manager", new string('e', 64), 0, now);
            db.AddRange(program, project, release, baseline);
            await db.SaveChangesAsync();
            sourceBaselineId = baseline.Id;
        }

        using var created = await admin.PostAsJsonAsync("/api/project-setups",
            new { projectName = "Discarded source start" });
        using var createdBody = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        var draftId = createdBody.RootElement.GetProperty("draftId").GetGuid();
        using var details = await admin.PutAsJsonAsync($"/api/project-setups/{draftId}", new
        {
            expectedVersion = 1,
            currentStep = "StartingPoint",
            project = new { name = "Discarded source start", softwareProduct = "Discarded source product" },
            build = new { version = "1.4" },
            selectedCategories = Array.Empty<string>(),
            ladder = new { }, reviewRules = new { }, reviewRulesAccepted = true,
            repository = new { mode = "ConfigureLater" }, mapping = new { },
        });
        Assert.Equal(HttpStatusCode.OK, details.StatusCode);
        using var capture = await admin.PostAsJsonAsync($"/api/project-setups/{draftId}/source/native",
            new { expectedVersion = 2, baselineId = sourceBaselineId });
        Assert.Equal(HttpStatusCode.OK, capture.StatusCode);
        using var captureBody = JsonDocument.Parse(await capture.Content.ReadAsStringAsync());
        var capturedVersion = captureBody.RootElement.GetProperty("draftVersion").GetInt64();
        var stagedSourceId = captureBody.RootElement.GetProperty("id").GetGuid();
        var stagedSha = captureBody.RootElement.GetProperty("sha256").GetString();

        using var discarded = await admin.PostAsJsonAsync($"/api/project-setups/{draftId}/discard",
            new { expectedVersion = capturedVersion });
        Assert.True(discarded.IsSuccessStatusCode, await discarded.Content.ReadAsStringAsync());

        using var scopeAfter = factory.Services.CreateScope();
        var after = scopeAfter.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        var package = await after.ProjectSetupSourcePackages.AsNoTracking().SingleAsync(x => x.Id == stagedSourceId);
        Assert.Equal(stagedSha, package.Sha256);
        Assert.Equal(draftId, package.DraftId);
        // A native source is an immutable reference to AeroLink's own facts, so it keeps its exact baseline,
        // its captured snapshot and its recorded analysis while carrying no separate payload bytes.
        Assert.Equal(sourceBaselineId, package.SourceBaselineId);
        Assert.Equal(0, package.SizeBytes);
        Assert.Empty(package.Payload);
        Assert.Equal("Frozen", package.SourceState);
        Assert.NotNull(package.SourceProjectId);
        Assert.Equal(ProjectSetupSourceStage.Analysed, package.Stage);
        Assert.NotEqual("{}", package.MetadataJson);
        Assert.NotEqual("{}", package.AnalysisJson);
        Assert.Equal("Frozen", (await after.CandidateBaselines.AsNoTracking()
            .Where(x => x.Id == sourceBaselineId).Select(x => x.State).SingleAsync()).ToString());
        Assert.True(await after.CandidateBaselines.AnyAsync(x => x.Id == sourceBaselineId));
    }

    private static async Task<(Guid DraftId, long Version)> CreateSavedDraftAsync(
        HttpClient client, string name)
    {
        using var created = await client.PostAsJsonAsync("/api/project-setups", new { projectName = name });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        using var createdBody = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        var draftId = createdBody.RootElement.GetProperty("draftId").GetGuid();
        using var saved = await client.PutAsJsonAsync($"/api/project-setups/{draftId}", new
        {
            expectedVersion = 1,
            currentStep = "Review",
            project = new { name, softwareProduct = $"{name} software" },
            start = new { kind = "Fresh" },
            build = new { version = "1.3" },
            selectedCategories = Array.Empty<string>(),
            ladder = new { }, reviewRules = new { }, reviewRulesAccepted = true,
            repository = new { mode = "ConfigureLater" }, mapping = new { },
        });
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        using var savedBody = JsonDocument.Parse(await saved.Content.ReadAsStringAsync());
        return (draftId, savedBody.RootElement.GetProperty("version").GetInt64());
    }
}
