using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AeroLink.Domain.Common;
using AeroLink.Domain.Identity;
using AeroLink.Domain.Programs;
using AeroLink.Domain.Requirements;
using AeroLink.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace AeroLink.Api.Tests;

/// <summary>#1113 S1: a project's switched-on features.</summary>
public sealed class ProjectFeatureApiTests
{
    [Fact]
    public async Task Features_default_on_follow_dependencies_switch_off_only_while_empty_and_gate_new_records()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        await BootstrapAsync(client);
        Guid projectId, otherProjectId;
        var member = $"pf.member.{Guid.NewGuid():N}";
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var now = DateTimeOffset.UtcNow;
            var program = new ProgramRecord("Feature program", "PFP");
            var project = new ProjectRecord(program.Id, "Feature Project", "FMS");
            var other = new ProjectRecord(program.Id, "Other Project", "FMS");
            var account = new UserAccount(member, member, $"{member}@example.test",
                IdentityService.HashPassword(AeroLinkApiFactory.MemberPassword), now);
            db.AddRange(program, project, other, account,
                new ProgramMembership(account.Id, program.Id, ProgramRole.SoftwareEngineer, "test.setup", now));
            db.ProblemReports.Add(new ProblemReport(other.Id, "PR-00001", "Existing", "Existing report.", "", "admin", now));
            await db.SaveChangesAsync();
            projectId = project.Id; otherProjectId = other.Id;
        }
        string Url(Guid id) => $"/api/projects/{id}/features";
        Task<HttpResponseMessage> PutAsync(Guid id, long version, params string[] enabled) =>
            client.PutAsJsonAsync(Url(id), new { expectedVersion = version, reason = "Problem Reports trial project", enabled });

        // No row: every feature, nothing persisted.
        var initial = await client.GetFromJsonAsync<JsonElement>(Url(projectId));
        Assert.False(initial.GetProperty("persisted").GetBoolean());
        Assert.Equal(7, initial.GetProperty("enabled").GetArrayLength());

        // Dependencies are refused before anything is stored.
        using (var codeAlone = await PutAsync(projectId, 0, "Code", "ProblemReports"))
            Assert.Equal(HttpStatusCode.BadRequest, codeAlone.StatusCode);
        using (var verificationAlone = await PutAsync(projectId, 0, "Verification", "ProblemReports"))
            Assert.Equal(HttpStatusCode.BadRequest, verificationAlone.StatusCode);
        using (var unknown = await PutAsync(projectId, 0, "Bogus"))
            Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
        using (var noReason = await client.PutAsJsonAsync(Url(projectId), new { expectedVersion = 0, enabled = new[] { "ProblemReports" } }))
            Assert.Equal(HttpStatusCode.BadRequest, noReason.StatusCode);

        // A feature holding records cannot be switched off.
        using (var withRecords = await PutAsync(otherProjectId, 0, "TeamWork", "Requirements", "Verification", "Code", "DocumentationCenter", "Release"))
        {
            Assert.Equal(HttpStatusCode.Conflict, withRecords.StatusCode);
            Assert.Contains("Problem Reports already holds records", await withRecords.Content.ReadAsStringAsync());
        }

        // The owner's target shape on an empty project: Team Work and Problem Reports only.
        using (var changed = await PutAsync(projectId, 0, "TeamWork", "ProblemReports"))
        {
            Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
            var body = await changed.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(1, body.GetProperty("version").GetInt64());
            Assert.Equal(["TeamWork", "ProblemReports"], body.GetProperty("enabled").EnumerateArray().Select(x => x.GetString()));
            var entry = Assert.Single(body.GetProperty("history").EnumerateArray());
            Assert.Equal("admin", entry.GetProperty("actor").GetString());
            Assert.Equal(64, entry.GetProperty("snapshotHash").GetString()!.Length);
        }
        using (var stale = await PutAsync(projectId, 0, "ProblemReports"))
            Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);

        // The save boundary refuses new records for a feature that is off, whatever the path.
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            db.ManagedDocuments.Add(ManagedDocumentFixture(projectId));
            var refused = await Assert.ThrowsAsync<DomainException>(() => db.SaveChangesAsync());
            Assert.Equal("Documentation Center is not enabled for this project.", refused.Message);
        }
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            db.ProblemReports.Add(new ProblemReport(projectId, "PR-00002", "Allowed", "Problem Reports stay on.", "", "admin", DateTimeOffset.UtcNow));
            await db.SaveChangesAsync();
        }

        // Switching on is always allowed; Team Work refuses its projection once off.
        using (var teamWorkOff = await PutAsync(projectId, 1, "ProblemReports"))
            Assert.Equal(HttpStatusCode.OK, teamWorkOff.StatusCode);
        using (var projection = await client.GetAsync($"/api/team-work?projectId={projectId}"))
        {
            Assert.Equal(HttpStatusCode.Conflict, projection.StatusCode);
            Assert.Contains("feature_disabled", await projection.Content.ReadAsStringAsync());
        }
        using (var allOn = await PutAsync(projectId, 2, "TeamWork", "Requirements", "Verification", "Code", "DocumentationCenter", "ProblemReports", "Release"))
            Assert.Equal(HttpStatusCode.OK, allOn.StatusCode);

        // Members read the features; only Configuration Manager, Program Manager or Administrator change them.
        using var login = await client.PostAsJsonAsync("/api/auth/login", new { userName = member, password = AeroLinkApiFactory.MemberPassword });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        await SecurityBoundaryTests.AuthorizeMutationsAsync(client);
        var asMember = await client.GetFromJsonAsync<JsonElement>(Url(projectId));
        Assert.False(asMember.GetProperty("canManage").GetBoolean());
        using (var forbidden = await PutAsync(projectId, 3, "ProblemReports"))
            Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
    }

    private static AeroLink.Domain.Documents.ManagedDocument ManagedDocumentFixture(Guid projectId) =>
        new(projectId, "SYSRD-00001", "SYSRD", "System Requirements", "Gated document", "admin", DateTimeOffset.UtcNow);

    private static async Task BootstrapAsync(HttpClient client)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/setup/bootstrap")
        {
            Content = JsonContent.Create(new
            {
                displayName = "Administrator",
                email = "admin@example.test",
                password = AeroLinkApiFactory.AdministratorPassword,
            }),
        };
        request.Headers.Add("X-AeroLink-Bootstrap-Secret", AeroLinkApiFactory.BootstrapSecret);
        using var created = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        using var login = await client.PostAsJsonAsync("/api/auth/login", new
        {
            userName = "admin",
            password = AeroLinkApiFactory.AdministratorPassword,
        });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        await SecurityBoundaryTests.AuthorizeMutationsAsync(client);
    }
}
