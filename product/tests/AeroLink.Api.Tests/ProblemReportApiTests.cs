using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AeroLink.Domain.Programs;
using AeroLink.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace AeroLink.Api.Tests;

public sealed class ProblemReportApiTests
{
    [Fact]
    public async Task Problem_report_is_server_numbered_and_cannot_be_closed_by_its_owner()
    {
        using var factory = new AeroLinkApiFactory(); using var client = factory.CreateClient(); await BootstrapAndLoginAsync(client);
        var projectId = await SeedProjectAsync(factory);

        using var created = await client.PostAsJsonAsync("/api/problem-reports", new { projectId, title = "Unexpected reset", problem = "The unit resets during a route update.", analysis = "", classification = "Verification failure", severity = "High", priority = "Urgent", origin = "Test execution", affectedConfiguration = "Build 1.6.0" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode); var report = await created.Content.ReadFromJsonAsync<JsonElement>();
        var id = report.GetProperty("id").GetGuid();

        using var opened = await client.GetAsync($"/api/problem-reports/{id}"); var openedText = await opened.Content.ReadAsStringAsync(); Assert.True(opened.IsSuccessStatusCode, openedText); var openedBody = JsonDocument.Parse(openedText).RootElement;
        Assert.Equal("PR-00001", openedBody.GetProperty("reportNumber").GetString());
        var version = openedBody.GetProperty("version").GetInt64();
        using var investigation = await client.PostAsJsonAsync($"/api/problem-reports/{id}/investigation", new { expectedVersion = version, analysis = "Reproduced during integration test.", rootCause = "Timeout race", effects = "Navigation reset", containment = "Disable retry" });
        Assert.Equal(HttpStatusCode.OK, investigation.StatusCode); version = (await investigation.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("version").GetInt64();
        using var proposal = await client.PostAsJsonAsync($"/api/problem-reports/{id}/resolution", new { expectedVersion = version, correctiveAction = "Serialize reset commands." });
        Assert.Equal(HttpStatusCode.OK, proposal.StatusCode);

        using var close = await client.PostAsJsonAsync($"/api/problem-reports/{id}/closure/approve", new { expectedVersion = version + 1 });
        Assert.Equal(HttpStatusCode.BadRequest, close.StatusCode);
        var detail = await client.GetFromJsonAsync<JsonElement>($"/api/problem-reports/{id}");
        Assert.Equal("ResolutionProposed", detail.GetProperty("state").GetString());
        Assert.True(detail.GetProperty("revisions").GetArrayLength() >= 3);
    }

    [Fact]
    public async Task Dashboard_exposes_unwaived_release_blockers_as_exact_records()
    {
        using var factory = new AeroLinkApiFactory(); using var client = factory.CreateClient(); await BootstrapAndLoginAsync(client);
        var projectId = await SeedProjectAsync(factory);
        using var created = await client.PostAsJsonAsync("/api/problem-reports", new { projectId, title = "Critical data loss", problem = "Unexpected data loss observed.", classification = "Software anomaly", severity = "Critical", priority = "Urgent", origin = "Manual report" });
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        using var detailResponse = await client.GetAsync($"/api/problem-reports/{id}"); var detailText = await detailResponse.Content.ReadAsStringAsync(); Assert.True(detailResponse.IsSuccessStatusCode, detailText); var detail = JsonDocument.Parse(detailText).RootElement; var version = detail.GetProperty("version").GetInt64();
        using var blocked = await client.PostAsJsonAsync($"/api/problem-reports/{id}/blocker", new { expectedVersion = version, isReleaseBlocker = true, waiverRationale = "" });
        Assert.Equal(HttpStatusCode.OK, blocked.StatusCode);
        var dashboard = await client.GetFromJsonAsync<JsonElement>($"/api/problem-reports/dashboard?projectId={projectId}");
        Assert.Equal(1, dashboard.GetProperty("summary").GetProperty("releaseBlockers").GetInt32());
        Assert.Contains(dashboard.GetProperty("attention").EnumerateArray(), x => x.GetProperty("id").GetGuid() == id);
    }

    private static async Task<Guid> SeedProjectAsync(AeroLinkApiFactory factory)
    {
        using var scope = factory.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        var program = new ProgramRecord("Problem Report Program", $"PR{Guid.NewGuid():N}"[..12]); var project = new ProjectRecord(program.Id, "Flight Management Product", "Flight Management System");
        db.AddRange(program, project); await db.SaveChangesAsync(); return project.Id;
    }

    private static async Task BootstrapAndLoginAsync(HttpClient client)
    {
        using var bootstrap = new HttpRequestMessage(HttpMethod.Post, "/api/setup/bootstrap") { Content = JsonContent.Create(new { displayName = "AeroLink Administrator", email = "admin@example.test", password = AeroLinkApiFactory.AdministratorPassword }) };
        bootstrap.Headers.Add("X-AeroLink-Bootstrap-Secret", AeroLinkApiFactory.BootstrapSecret); Assert.Equal(HttpStatusCode.Created, (await client.SendAsync(bootstrap)).StatusCode);
        using var login = await client.PostAsJsonAsync("/api/auth/login", new { userName = "admin", password = AeroLinkApiFactory.AdministratorPassword }); Assert.Equal(HttpStatusCode.OK, login.StatusCode); await SecurityBoundaryTests.AuthorizeMutationsAsync(client);
    }
}
