using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using AeroLink.Domain.Identity;
using AeroLink.Domain.Programs;
using AeroLink.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace AeroLink.Api.Tests;

/// <summary>#1114: Problem Reports imported from another tool's CSV export.</summary>
public sealed class ProblemReportImportApiTests
{
    [Fact]
    public async Task A_csv_export_is_reconciled_row_by_row_signed_imported_with_source_facts_and_skipped_on_reimport()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        await BootstrapAsync(client);
        var engineer = $"pri.eng.{Guid.NewGuid():N}";
        Guid projectId, buildId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var now = DateTimeOffset.UtcNow;
            var program = new ProgramRecord("Import program", "PRI");
            var project = new ProjectRecord(program.Id, "Imported reports", "Widget");
            var build = new SoftwareRelease(project.Id, "1.0", false);
            var account = new UserAccount(engineer, "Imported Engineer", $"{engineer}@example.test",
                IdentityService.HashPassword(AeroLinkApiFactory.MemberPassword), now);
            db.AddRange(program, project, build, account,
                new ProgramMembership(account.Id, program.Id, ProgramRole.SoftwareEngineer, "test.setup", now));
            await db.SaveChangesAsync();
            projectId = project.Id; buildId = build.Id;
        }
        const string csv = """
            Key,Summary,Description,Status,Severity,Category,Reporter,Assignee,Created,Version
            JIRA-1,Nav freeze,Display freezes on waypoint insert,Open,High,Code,Jane Source,Eng Source,2024-03-01,1.0
            JIRA-2,Old bug,Fixed long ago in the old tool,Closed,Minor,Code,Jane Source,,2023-01-01,1.0
            JIRA-3,Odd status,Something happened,Weird,Major,Code,,,,
            JIRA-1,Duplicate key,Repeated row,Open,High,Code,,Eng Source,,
            JIRA-4,No owner,Needs an owner,Open,High,Code,Jane Source,,,
            """;
        var mapping = new
        {
            sourceSystem = "Jira",
            columns = new Dictionary<string, string>
            {
                ["sourceKey"] = "Key", ["title"] = "Summary", ["problem"] = "Description", ["status"] = "Status",
                ["severity"] = "Severity", ["category"] = "Category", ["reportedBy"] = "Reporter",
                ["responsibleEngineer"] = "Assignee", ["createdAt"] = "Created", ["targetBuild"] = "Version",
            },
            statuses = new Dictionary<string, string> { ["Open"] = "Open", ["Closed"] = "ClosedInSource" },
            categories = new Dictionary<string, string> { ["Code"] = "CodeFunctional" },
            people = new Dictionary<string, string> { ["Eng Source"] = engineer },
            builds = new Dictionary<string, string> { ["1.0"] = buildId.ToString() },
        };
        MultipartFormDataContent Form(string? previewHash = null, string? password = null)
        {
            var form = new MultipartFormDataContent
            {
                { new StringContent(projectId.ToString()), "projectId" },
                { new StringContent(JsonSerializer.Serialize(mapping)), "mapping" },
                { new ByteArrayContent(Encoding.UTF8.GetBytes(csv)), "file", "jira-export.csv" },
            };
            if (previewHash is not null) form.Add(new StringContent(previewHash), "previewHash");
            if (password is not null) form.Add(new StringContent(password), "password");
            return form;
        }

        using var previewResponse = await client.PostAsync("/api/problem-reports/import/preview", Form());
        Assert.Equal(HttpStatusCode.OK, previewResponse.StatusCode);
        var preview = await previewResponse.Content.ReadFromJsonAsync<JsonElement>();
        var rows = preview.GetProperty("rows").EnumerateArray().ToArray();
        Assert.Equal(5, rows.Length); // Every row is accounted for.
        Assert.Equal(2, preview.GetProperty("create").GetInt32());
        Assert.Equal(["Create", "Create", "Skip", "Skip", "Skip"], rows.Select(x => x.GetProperty("action").GetString()));
        Assert.Contains("Map the source status", rows[2].GetProperty("reason").GetString());
        Assert.Contains("earlier in this file", rows[3].GetProperty("reason").GetString());
        Assert.Contains("responsible engineer", rows[4].GetProperty("reason").GetString());
        Assert.Equal("ClosedInSource", rows[1].GetProperty("landingState").GetString());
        var previewHash = preview.GetProperty("previewHash").GetString()!;

        using (var wrongPassword = await client.PostAsync("/api/problem-reports/import/commit", Form(previewHash, "not-it")))
            Assert.Equal(HttpStatusCode.Unauthorized, wrongPassword.StatusCode);
        using (var stale = await client.PostAsync("/api/problem-reports/import/commit", Form(new string('0', 64), AeroLinkApiFactory.AdministratorPassword)))
            Assert.Equal(HttpStatusCode.BadRequest, stale.StatusCode);
        using var commit = await client.PostAsync("/api/problem-reports/import/commit", Form(previewHash, AeroLinkApiFactory.AdministratorPassword));
        Assert.Equal(HttpStatusCode.OK, commit.StatusCode);
        var committed = await commit.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(2, committed.GetProperty("created").GetInt32());
        var ids = committed.GetProperty("reports").EnumerateArray()
            .ToDictionary(x => x.GetProperty("sourceKey").GetString()!, x => x.GetProperty("id").GetGuid());

        var open = await client.GetFromJsonAsync<JsonElement>($"/api/problem-reports/{ids["JIRA-1"]}");
        Assert.Equal("Open", open.GetProperty("state").GetString());
        Assert.Equal(engineer, open.GetProperty("responsibleEngineerId").GetString());
        Assert.Equal("admin", open.GetProperty("reportedBy").GetString()); // Unmapped reporter: the importer.
        Assert.Equal("Jane Source", open.GetProperty("sourceReportedBy").GetString());
        Assert.Equal("JIRA-1", open.GetProperty("sourceKey").GetString());
        Assert.Equal("Jira", open.GetProperty("sourceSystem").GetString());

        var closed = await client.GetFromJsonAsync<JsonElement>($"/api/problem-reports/{ids["JIRA-2"]}");
        Assert.Equal("Closed", closed.GetProperty("state").GetString());
        Assert.True(closed.GetProperty("closedInSource").GetBoolean());
        Assert.Equal(JsonValueKind.Null, closed.GetProperty("closureApprovedAt").ValueKind); // No fabricated SQA closure.
        using (var reopen = await client.PostAsJsonAsync($"/api/problem-reports/{ids["JIRA-2"]}/reopen", new { rationale = "Try to revive it." }))
            Assert.NotEqual(HttpStatusCode.OK, reopen.StatusCode);

        var search = await client.GetFromJsonAsync<JsonElement>($"/api/problem-reports?projectId={projectId}&search=JIRA-1");
        Assert.Contains(ids["JIRA-1"].ToString(), search.GetRawText());

        // Importing the same export again skips what is already there.
        using var again = await client.PostAsync("/api/problem-reports/import/preview", Form());
        var againRows = (await again.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("rows").EnumerateArray().ToArray();
        Assert.StartsWith("Already imported as PR-", againRows[0].GetProperty("reason").GetString());
        Assert.StartsWith("Already imported as PR-", againRows[1].GetProperty("reason").GetString());

        var batches = await client.GetFromJsonAsync<JsonElement>($"/api/problem-reports/import/batches?projectId={projectId}");
        var batch = Assert.Single(batches.EnumerateArray());
        Assert.Equal(2, batch.GetProperty("created").GetInt32());
        Assert.Equal(3, batch.GetProperty("skipped").GetInt32());
        using var check = factory.Services.CreateScope();
        var signatures = check.ServiceProvider.GetRequiredService<AeroLinkDbContext>().ElectronicSignatures
            .Where(x => x.Action == "ImportProblemReports").ToList();
        Assert.Equal(previewHash, Assert.Single(signatures).ContentHash);

        // Only project configuration authority imports.
        using var member = factory.CreateClient();
        using (var login = await member.PostAsJsonAsync("/api/auth/login", new { userName = engineer, password = AeroLinkApiFactory.MemberPassword }))
            Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        await SecurityBoundaryTests.AuthorizeMutationsAsync(member);
        using (var forbidden = await member.PostAsync("/api/problem-reports/import/preview", Form()))
            Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
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
