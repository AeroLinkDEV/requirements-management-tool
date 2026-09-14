using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AeroLink.Domain.Programs;
using AeroLink.Domain.Requirements;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AeroLink.Api.Tests;

public sealed partial class ManagedDocumentApiTests
{
    [Fact]
    public async Task Release_link_pages_and_quality_trends_use_canonical_numeric_order()
    {
        using var factory = new AeroLinkApiFactory();
        using var administrator = factory.CreateClient();
        await ProblemReportApiTests.BootstrapAndLoginAsync(administrator);
        var scope = await SeedProjectAsync(factory);
        using (var services = factory.Services.CreateScope())
        {
            var db = services.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var legacy = new SoftwareRelease(scope.ProjectId, "9.0", true);
            typeof(SoftwareRelease).GetProperty(nameof(SoftwareRelease.CanonicalIdentity))!.SetValue(legacy, null);
            db.AddRange(legacy, new SoftwareRelease(scope.ProjectId, "10.5", false));
            await db.SaveChangesAsync();
        }
        var names = new List<string>();
        string? cursor = null;
        for (var pageNumber = 0; pageNumber < 5; pageNumber++)
        {
            var url = $"/api/managed-documents/link-options?projectId={scope.ProjectId}&artifactType=Release&pageSize=1";
            if (cursor is not null) url += $"&cursor={Uri.EscapeDataString(cursor)}";
            var page = await administrator.GetFromJsonAsync<JsonElement>(url);
            names.Add(Assert.Single(page.GetProperty("items").EnumerateArray()).GetProperty("displayNumber").GetString()!);
            if (!page.GetProperty("hasMore").GetBoolean()) break;
            cursor = page.GetProperty("nextCursor").GetString();
        }
        Assert.Equal(["BUILD-1.5", "BUILD-1.6", "BUILD-9.0", "BUILD-10.5"], names);
        using var changedFilter = await administrator.GetAsync($"/api/managed-documents/link-options?projectId={scope.ProjectId}&artifactType=Release&pageSize=1&search=9&cursor={Uri.EscapeDataString(cursor!)}");
        Assert.Equal(HttpStatusCode.BadRequest, changedFilter.StatusCode);
        using var invalid = await administrator.GetAsync($"/api/managed-documents/link-options?projectId={scope.ProjectId}&artifactType=Release&cursor=invalid");
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        var portfolio = await administrator.GetFromJsonAsync<JsonElement>($"/api/quality/portfolio?projectId={scope.ProjectId}");
        Assert.Equal(["1.5", "1.6", "9.0", "10.5"], portfolio.GetProperty("trends").EnumerateArray().Select(x => x.GetProperty("version").GetString()));
    }

    [Fact]
    public async Task Targetless_problem_report_link_keeps_project_scope_without_inventing_a_build()
    {
        using var factory = new AeroLinkApiFactory();
        using var administrator = factory.CreateClient();
        await ProblemReportApiTests.BootstrapAndLoginAsync(administrator);
        var scope = await SeedProjectAsync(factory);
        var report = new ProblemReport(scope.ProjectId, "PR-10371", "Project-wide anomaly", "Problem", "Analysis", "software.author", DateTimeOffset.UtcNow);
        using (var services = factory.Services.CreateScope())
        {
            var db = services.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            db.Add(report);
            await db.SaveChangesAsync();
        }
        using var created = await administrator.PostAsJsonAsync("/api/managed-documents", new
        {
            projectId = scope.ProjectId, acronym = "SCMP", documentType = "Software Configuration Management Plan",
            title = "Project link fixture", ownerId = "software.author", formalChangeSummary = "Record project scope.",
            operationKey = Guid.NewGuid().ToString("N"),
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var body = await created.Content.ReadFromJsonAsync<JsonElement>();
        var documentId = body.GetProperty("id").GetGuid();
        var revisionId = body.GetProperty("revisionId").GetGuid();
        using var owner = await LoginAsync(factory, "software.author");
        var detail = await owner.GetFromJsonAsync<JsonElement>($"/api/managed-documents/{documentId}");
        using var linked = await owner.PostAsJsonAsync($"/api/managed-documents/{documentId}/links", new
        {
            revisionId, artifactType = "ProblemReport", artifactId = report.Id, relationship = "AddressesProblem",
            expectedVersion = detail.GetProperty("revisions")[0].GetProperty("version").GetInt64(),
        });
        Assert.Equal(HttpStatusCode.Created, linked.StatusCode);
        var link = await linked.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(JsonValueKind.Null, link.GetProperty("targetReleaseId").ValueKind);
        Assert.Equal("", link.GetProperty("targetReleaseVersion").GetString());
        Assert.Equal($"/projects/{scope.ProjectId}/problem-reports/{report.Id}", link.GetProperty("deepLink").GetString());
    }
}
