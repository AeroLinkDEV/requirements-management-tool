using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AeroLink.Domain.Identity;
using AeroLink.Domain.Programs;
using AeroLink.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace AeroLink.Api.Tests;

public sealed class ProblemReportDispositionApiTests
{
    [Fact]
    public async Task Legacy_disposition_route_accepts_only_rejected_with_rationale_and_respects_terminal_state()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        await ProblemReportApiTests.BootstrapAndLoginAsync(client);
        var (projectId, releaseId) = await SeedProjectAsync(factory);

        var blank = await CreateDraftAsync(client, projectId, releaseId, "Blank rationale disposition");
        using (var response = await DispositionAsync(client, blank, "Rejected", "   "))
        {
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Contains("rationale", await ErrorAsync(response), StringComparison.OrdinalIgnoreCase);
        }

        var fixedReport = await CreateDraftAsync(client, projectId, releaseId, "Generic Fixed disposition");
        using (var response = await DispositionAsync(client, fixedReport, "Fixed", "A generic fixed conclusion is forbidden."))
        {
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Contains("Rejected", await ErrorAsync(response), StringComparison.OrdinalIgnoreCase);
        }

        var staleReport = await CreateDraftAsync(client, projectId, releaseId, "Stale disposition");
        using (var response = await client.PostAsJsonAsync($"/api/problem-reports/{staleReport.Id}/disposition", new
        {
            expectedVersion = staleReport.Version - 1,
            disposition = "Rejected",
            rationale = "This stale decision must not overwrite the current record.",
        }))
        {
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            var body = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("stale_version", body.GetProperty("code").GetString());
        }

        var terminal = await CreateDraftAsync(client, projectId, releaseId, "Terminal disposition");
        using (var accepted = await DispositionAsync(client, terminal, "Rejected", "Investigation established no product fault."))
        {
            accepted.EnsureSuccessStatusCode();
            terminal = terminal with { Version = (await accepted.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("version").GetInt64() };
        }
        using (var response = await DispositionAsync(client, terminal, "Rejected", "A terminal record cannot be dispositioned twice."))
        {
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Contains("dispositioned", await ErrorAsync(response), StringComparison.OrdinalIgnoreCase);
        }
        var detail = await client.GetFromJsonAsync<JsonElement>($"/api/problem-reports/{terminal.Id}");
        Assert.Equal("Rejected", detail.GetProperty("state").GetString());
        Assert.Equal("Investigation established no product fault.", detail.GetProperty("dispositionRationale").GetString());
        Assert.Single(detail.GetProperty("revisions").EnumerateArray(), revision =>
            revision.GetProperty("eventType").GetString() == "DispositionRecorded");
    }

    private static async Task<ReportRef> CreateDraftAsync(HttpClient client, Guid projectId, Guid releaseId, string title)
    {
        using var created = await client.PostAsJsonAsync("/api/problem-reports", new
        {
            projectId, releaseId, title, problem = "A controlled disposition is required."
        });
        created.EnsureSuccessStatusCode();
        var body = await created.Content.ReadFromJsonAsync<JsonElement>();
        var id = body.GetProperty("id").GetGuid();
        var version = body.GetProperty("version").GetInt64();
        return new(id, version);
    }

    private static async Task<(Guid ProjectId, Guid ReleaseId)> SeedProjectAsync(AeroLinkApiFactory factory)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        var program = new ProgramRecord("Disposition workflow Program", $"DW{Guid.NewGuid():N}"[..12]);
        var project = new ProjectRecord(program.Id, "Flight Management Product", "Flight Management System");
        var release = new SoftwareRelease(project.Id, "1.6", false);
        var admin = db.UserAccounts.Single(account => account.UserName == "admin");
        db.AddRange(program, project, release,
            new ProgramMembership(admin.Id, program.Id, ProgramRole.ProjectEngineer, "test.setup", DateTimeOffset.UtcNow));
        await db.SaveChangesAsync();
        return (project.Id, release.Id);
    }

    private static Task<HttpResponseMessage> DispositionAsync(HttpClient client, ReportRef report,
        string disposition, string rationale) =>
        client.PostAsJsonAsync($"/api/problem-reports/{report.Id}/disposition", new
        {
            expectedVersion = report.Version, disposition, rationale,
        });

    private static async Task<string> ErrorAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString() ?? "";

    private sealed record ReportRef(Guid Id, long Version);
}
