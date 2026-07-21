using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Programs;
using AeroLink.Domain.Requirements;
using AeroLink.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;

namespace AeroLink.Api.Tests;

public sealed class ControlledEditingCheckInApiTests
{
    [Fact]
    public async Task Universal_check_in_returns_resulting_version_hash_and_released_lease()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        await BootstrapAndLoginAsync(client);
        var seeded = await SeedAsync(factory, stale: false);

        using var response = await client.PostAsJsonAsync(
            $"/api/controlled-editing/sessions/{seeded.SessionId}/check-in", new { expectedVersion = 1 });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("success").GetBoolean());
        Assert.Equal(2, body.GetProperty("resultingArtifactVersion").GetInt64());
        Assert.Equal(64, body.GetProperty("resultingHash").GetString()!.Length);
        Assert.True(body.GetProperty("sessionClosed").GetBoolean());
        Assert.True(body.GetProperty("leaseReleased").GetBoolean());
        Assert.NotEqual(Guid.Empty, body.GetProperty("evidenceId").GetGuid());
    }

    [Fact]
    public async Task Authoritative_change_after_checkout_returns_409_stale_artifact_version()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        await BootstrapAndLoginAsync(client);
        var seeded = await SeedAsync(factory, stale: true);

        using var response = await client.PostAsJsonAsync(
            $"/api/controlled-editing/sessions/{seeded.SessionId}/check-in", new { expectedVersion = 1 });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("stale_artifact_version", body.GetProperty("code").GetString());
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        var session = await db.ArtifactEditSessions.FindAsync(seeded.SessionId);
        var artifact = await db.SystemChangeRequests.FindAsync(seeded.ArtifactId);
        Assert.Equal(EditSessionState.Active, session!.State);
        Assert.NotNull(session.LockKey);
        Assert.Equal("Authoritative concurrent update", artifact!.Title);
    }

    [Theory]
    [InlineData("DocumentTemplate", "TPL-00001")]
    [InlineData("ProblemReport", "PR-00001")]
    [InlineData("ConfigurationChangeSet", "CCS-00001")]
    public async Task Remaining_controlled_families_complete_the_public_checkout_to_evidence_lifecycle(string artifactType, string number)
    {
        using var factory = new AeroLinkApiFactory(); using var client = factory.CreateClient();
        await BootstrapAndLoginAsync(client); var projectId = await SeedProjectAsync(factory);
        using var created = await client.PostAsJsonAsync("/api/controlled-editing/artifacts", new
        { artifactType, projectId, number, title = $"{artifactType} controlled draft", content = "Initial controlled content", analysis = "Initial engineering analysis" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var artifactId = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        using var checkout = await client.PostAsJsonAsync("/api/controlled-editing/checkout", new { artifactType, artifactId });
        Assert.True(checkout.IsSuccessStatusCode); var session = await checkout.Content.ReadFromJsonAsync<JsonElement>();
        var sessionId = session.GetProperty("id").GetGuid(); var draft = session.GetProperty("draftJson").GetString()!;
        using var autosave = await client.PutAsJsonAsync($"/api/controlled-editing/sessions/{sessionId}/autosave", new { expectedVersion = 1, draftJson = draft });
        Assert.Equal(HttpStatusCode.OK, autosave.StatusCode); var version = (await autosave.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("version").GetInt64();
        using var checkIn = await client.PostAsJsonAsync($"/api/controlled-editing/sessions/{sessionId}/check-in", new { expectedVersion = version });
        Assert.Equal(HttpStatusCode.OK, checkIn.StatusCode); Assert.True((await checkIn.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("leaseReleased").GetBoolean());
        using var scope = factory.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        Assert.Contains(await db.ControlledArtifactCheckInEvidence.ToListAsync(), x => x.ArtifactType == artifactType && x.ArtifactId == artifactId && x.Outcome == ControlledCheckInOutcome.Succeeded);
    }

    private static async Task<Guid> SeedProjectAsync(AeroLinkApiFactory factory)
    {
        using var scope = factory.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        var program = new ProgramRecord("Universal Family Program", $"UF{Guid.NewGuid():N}"[..12]);
        var project = new ProjectRecord(program.Id, "Universal Controlled Product", "Flight Management System");
        db.AddRange(program, project); await db.SaveChangesAsync(); return project.Id;
    }

    private static async Task<(Guid SessionId, Guid ArtifactId)> SeedAsync(AeroLinkApiFactory factory, bool stale)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        var now = DateTimeOffset.UtcNow;
        var program = new ProgramRecord("Check-In API Program", $"CI{Guid.NewGuid():N}"[..12]);
        var project = new ProjectRecord(program.Id, "Controlled Product", "Flight Management System");
        var release = new SoftwareRelease(project.Id, "1.0", false);
        var scr = new SystemChangeRequest("SCR-00000001", 0, project.Id, release.Id, "Original title",
            "Original problem", "Original analysis", "Original solution", "admin", now);
        db.AddRange(program, project, release, scr);
        await db.SaveChangesAsync();
        var adapter = new SystemChangeRequestControlledEditingAdapter(db);
        var artifact = await adapter.ResolveAsync(scr.Id, default) ?? throw new InvalidOperationException();
        var snapshot = adapter.CanonicalSnapshot(artifact);
        var hash = EnterpriseRequirementsService.Hash(Encoding.UTF8.GetBytes(snapshot));
        var session = new ArtifactEditSession(project.Id, "ChangeRequest", scr.Id, null, hash,
            snapshot, "admin", now, true, 15);
        db.AddRange(session, new ArtifactDraftSnapshot(project.Id, session.Id, "ChangeRequest", scr.Id,
            1, snapshot, hash, "admin", now));
        await db.SaveChangesAsync();
        if (stale)
        {
            scr.UpdateDraft("admin", "Authoritative concurrent update", "Problem", "Analysis", "Solution", [], now.AddSeconds(1));
            await db.SaveChangesAsync();
        }
        return (session.Id, scr.Id);
    }

    private static async Task BootstrapAndLoginAsync(HttpClient client)
    {
        using var bootstrap = new HttpRequestMessage(HttpMethod.Post, "/api/setup/bootstrap")
        {
            Content = JsonContent.Create(new { displayName = "AeroLink Administrator",
                email = "admin@example.test", password = AeroLinkApiFactory.AdministratorPassword })
        };
        bootstrap.Headers.Add("X-AeroLink-Bootstrap-Secret", AeroLinkApiFactory.BootstrapSecret);
        using var bootstrapResponse = await client.SendAsync(bootstrap);
        Assert.Equal(HttpStatusCode.Created, bootstrapResponse.StatusCode);
        using var login = await client.PostAsJsonAsync("/api/auth/login",
            new { userName = "admin", password = AeroLinkApiFactory.AdministratorPassword });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        await SecurityBoundaryTests.AuthorizeMutationsAsync(client);
    }
}
