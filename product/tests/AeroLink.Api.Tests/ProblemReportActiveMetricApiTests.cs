using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using AeroLink.Domain.Identity;
using AeroLink.Domain.Programs;
using AeroLink.Domain.Requirements;
using AeroLink.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace AeroLink.Api.Tests;

public sealed class ProblemReportActiveMetricApiTests
{
    [Fact]
    public async Task Dashboard_portfolio_contract_and_export_share_the_complete_active_work_classification()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        await BootstrapAsync(client);
        Guid projectId;
        Guid rejectedId;
        string sqaUserName;

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var now = DateTimeOffset.UtcNow;
            var program = new ProgramRecord("Problem Report metrics", "PRM");
            var project = new ProjectRecord(program.Id, "Metric Project", "FMS");
            sqaUserName = $"prm.sqa.{Guid.NewGuid():N}";
            var sqa = new UserAccount(sqaUserName, "Problem Report SQA",
                $"{sqaUserName}@example.test",
                IdentityService.HashPassword(AeroLinkApiFactory.MemberPassword), now);
            db.AddRange(program, project, sqa,
                new ProgramMembership(sqa.Id, program.Id, ProgramRole.SoftwareQualityAnalyst,
                    "test.setup", now));
            projectId = project.Id;

            var stateProperty = typeof(ProblemReport).GetProperty(nameof(ProblemReport.State),
                BindingFlags.Instance | BindingFlags.Public)!;
            var sequence = 0;
            rejectedId = Guid.Empty;
            foreach (var state in Enum.GetValues<ProblemReportState>())
            {
                sequence++;
                var report = new ProblemReport(project.Id, $"PR-{sequence:D5}", $"State {state}",
                    "Explicit lifecycle classification fixture.", "", "admin", now.AddMinutes(sequence));
                stateProperty.SetValue(report, state);
                db.ProblemReports.Add(report);
                if (state == ProblemReportState.Rejected) rejectedId = report.Id;
            }
            await db.SaveChangesAsync();
        }

        var expected = Enum.GetValues<ProblemReportState>().Count(ProblemReportLifecycle.IsActiveWork);
        var before = await ReadCountsAsync(client, projectId);
        Assert.Equal(expected, before.Dashboard);
        Assert.Equal(expected, before.Portfolio);
        Assert.Equal(expected, before.Contract);
        Assert.Equal(ProblemReportLifecycle.ActiveWorkDefinition, before.Definition);

        using var exportResponse = await client.PostAsJsonAsync("/api/quality/exports", new
        {
            projectId,
            idempotencyKey = $"problem-report-active-metric:{projectId:N}",
        });
        Assert.Equal(HttpStatusCode.Created, exportResponse.StatusCode);
        var export = await exportResponse.Content.ReadFromJsonAsync<JsonElement>();
        using var download = await client.GetAsync(export.GetProperty("downloadUrl").GetString());
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        var exportedPortfolio = JsonDocument.Parse(await download.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(expected, exportedPortfolio.GetProperty("summary").GetProperty("openProblemReports").GetInt32());

        using var sqaLogin = await client.PostAsJsonAsync("/api/auth/login", new
        {
            userName = sqaUserName,
            password = AeroLinkApiFactory.MemberPassword,
        });
        Assert.Equal(HttpStatusCode.OK, sqaLogin.StatusCode);
        await SecurityBoundaryTests.AuthorizeMutationsAsync(client);
        using var reopen = await client.PostAsJsonAsync($"/api/problem-reports/{rejectedId}/reopen", new
        {
            expectedVersion = 1,
            rationale = "The rejected conclusion is withdrawn after new evidence.",
        });
        Assert.Equal(HttpStatusCode.OK, reopen.StatusCode);
        Assert.Equal("Draft", (await reopen.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("state").GetString());

        var after = await ReadCountsAsync(client, projectId);
        Assert.Equal(expected + 1, after.Dashboard);
        Assert.Equal(expected + 1, after.Portfolio);
        Assert.Equal(expected + 1, after.Contract);
    }

    private static async Task<(int Dashboard, int Portfolio, int Contract, string Definition)> ReadCountsAsync(
        HttpClient client, Guid projectId)
    {
        var dashboard = await client.GetFromJsonAsync<JsonElement>(
            $"/api/problem-reports/dashboard?projectId={projectId}");
        var portfolio = await client.GetFromJsonAsync<JsonElement>($"/api/quality/portfolio?projectId={projectId}");
        var contracts = await client.GetFromJsonAsync<JsonElement>(
            $"/api/quality/metric-contracts?projectId={projectId}");
        var contract = contracts.GetProperty("contracts").EnumerateArray()
            .Single(item => item.GetProperty("key").GetString() == "open_problem_reports");
        return (
            dashboard.GetProperty("summary").GetProperty("active").GetInt32(),
            portfolio.GetProperty("summary").GetProperty("openProblemReports").GetInt32(),
            contract.GetProperty("value").GetInt32(),
            contract.GetProperty("definition").GetString()!);
    }

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
