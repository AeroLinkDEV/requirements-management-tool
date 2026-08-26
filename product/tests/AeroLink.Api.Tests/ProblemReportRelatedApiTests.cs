using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AeroLink.Domain.Programs;
using AeroLink.Domain.Releases;
using AeroLink.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace AeroLink.Api.Tests;

/// <summary>
/// Problem Reports that belong together.
///
/// The relationship is symmetric and written on both records, because the reason to record it at all is
/// that somebody looking at either report should find the other. These cover that reciprocity, the guards
/// that keep it meaningful, and the fact that it is a controlled relationship rather than free-form
/// context anybody can forge through the generic links endpoint.
/// </summary>
public sealed class ProblemReportRelatedApiTests
{
    private static async Task<(Guid ProjectId, Guid ReleaseId)> SeedAsync(AeroLinkApiFactory factory, string prefix)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        var program = new ProgramRecord($"{prefix} Program", $"{prefix}{Guid.NewGuid():N}"[..12]);
        var project = new ProjectRecord(program.Id, "Flight Management Product", "Flight Management System");
        var release = new SoftwareRelease(project.Id, "1.6", false);
        db.AddRange(program, project, release);
        await db.SaveChangesAsync();
        return (project.Id, release.Id);
    }

    private static async Task<(Guid Id, long Version, string DisplayNumber)> RaiseAsync(
        HttpClient client, Guid projectId, Guid releaseId, string title)
    {
        using var created = await client.PostAsJsonAsync("/api/problem-reports", new
        {
            projectId, releaseId, category = "CodeFunctional", title,
            problem = "The tone follows the disconnect.",
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var body = await created.Content.ReadFromJsonAsync<JsonElement>();
        return (body.GetProperty("id").GetGuid(), body.GetProperty("version").GetInt64(),
            body.GetProperty("displayNumber").GetString()!);
    }

    private static async Task<JsonElement> RelatedAsync(HttpClient client, Guid id) =>
        (await client.GetFromJsonAsync<JsonElement>($"/api/problem-reports/{id}")).GetProperty("relatedReports");

    [Fact]
    public async Task Relating_two_reports_is_recorded_on_both_of_them()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        await ProblemReportApiTests.BootstrapAndLoginAsync(client);
        var (projectId, releaseId) = await SeedAsync(factory, "PRRA");
        var first = await RaiseAsync(client, projectId, releaseId, "Disconnect tone is late");
        var second = await RaiseAsync(client, projectId, releaseId, "Missed-approach sequencing loses hold entry");

        using var linked = await client.PostAsJsonAsync($"/api/problem-reports/{first.Id}/related",
            new { relatedProblemReportId = second.Id, expectedVersion = first.Version });
        Assert.Equal(HttpStatusCode.Created, linked.StatusCode);

        // Both sides. A relationship only one record knows about is one the other's reader never finds.
        var fromFirst = Assert.Single((await RelatedAsync(client, first.Id)).EnumerateArray());
        Assert.Equal(second.DisplayNumber, fromFirst.GetProperty("displayNumber").GetString());
        Assert.Equal("Missed-approach sequencing loses hold entry", fromFirst.GetProperty("title").GetString());
        Assert.Equal("Draft", fromFirst.GetProperty("state").GetString());
        Assert.Equal("1.6", fromFirst.GetProperty("targetBuild").GetString());

        var fromSecond = Assert.Single((await RelatedAsync(client, second.Id)).EnumerateArray());
        Assert.Equal(first.DisplayNumber, fromSecond.GetProperty("displayNumber").GetString());
    }

    [Fact]
    public async Task Unlinking_removes_the_relationship_from_both_records()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        await ProblemReportApiTests.BootstrapAndLoginAsync(client);
        var (projectId, releaseId) = await SeedAsync(factory, "PRRU");
        var first = await RaiseAsync(client, projectId, releaseId, "First report");
        var second = await RaiseAsync(client, projectId, releaseId, "Second report");

        using var linked = await client.PostAsJsonAsync($"/api/problem-reports/{first.Id}/related",
            new { relatedProblemReportId = second.Id });
        Assert.Equal(HttpStatusCode.Created, linked.StatusCode);

        // Removed from the other side, which is where it was not asked for — a one-sided removal would
        // leave the second report asserting a relationship that no longer exists.
        using var removed = await client.DeleteAsync($"/api/problem-reports/{second.Id}/related/{first.Id}");
        Assert.Equal(HttpStatusCode.OK, removed.StatusCode);

        Assert.Empty((await RelatedAsync(client, first.Id)).EnumerateArray());
        Assert.Empty((await RelatedAsync(client, second.Id)).EnumerateArray());
    }

    [Fact]
    public async Task A_report_cannot_be_related_to_itself()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        await ProblemReportApiTests.BootstrapAndLoginAsync(client);
        var (projectId, releaseId) = await SeedAsync(factory, "PRRS");
        var report = await RaiseAsync(client, projectId, releaseId, "Only report");

        using var refused = await client.PostAsJsonAsync($"/api/problem-reports/{report.Id}/related",
            new { relatedProblemReportId = report.Id });

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Equal("pr_related_self",
            (await refused.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
    }

    /// <summary>
    /// Same Project, like the duplicate disposition beside it. A relationship reaching across Projects
    /// would be visible to people who cannot open half of it.
    /// </summary>
    [Fact]
    public async Task A_report_in_another_Project_cannot_be_related()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        await ProblemReportApiTests.BootstrapAndLoginAsync(client);
        var (projectId, releaseId) = await SeedAsync(factory, "PRRP");
        var (otherProjectId, otherReleaseId) = await SeedAsync(factory, "PRRQ");
        var here = await RaiseAsync(client, projectId, releaseId, "Report in this Project");
        var elsewhere = await RaiseAsync(client, otherProjectId, otherReleaseId, "Report in another Project");

        using var refused = await client.PostAsJsonAsync($"/api/problem-reports/{here.Id}/related",
            new { relatedProblemReportId = elsewhere.Id });

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Equal("pr_related_not_in_project",
            (await refused.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
    }

    [Fact]
    public async Task The_same_pair_cannot_be_related_twice()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        await ProblemReportApiTests.BootstrapAndLoginAsync(client);
        var (projectId, releaseId) = await SeedAsync(factory, "PRRD");
        var first = await RaiseAsync(client, projectId, releaseId, "First report");
        var second = await RaiseAsync(client, projectId, releaseId, "Second report");

        using var linked = await client.PostAsJsonAsync($"/api/problem-reports/{first.Id}/related",
            new { relatedProblemReportId = second.Id });
        Assert.Equal(HttpStatusCode.Created, linked.StatusCode);

        using var again = await client.PostAsJsonAsync($"/api/problem-reports/{first.Id}/related",
            new { relatedProblemReportId = second.Id });
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);

        // And not from the other side either, which already holds the reciprocal half.
        using var reverse = await client.PostAsJsonAsync($"/api/problem-reports/{second.Id}/related",
            new { relatedProblemReportId = first.Id });
        Assert.Equal(HttpStatusCode.Conflict, reverse.StatusCode);

        Assert.Single((await RelatedAsync(client, first.Id)).EnumerateArray());
    }

    /// <summary>
    /// A controlled relationship, not free-form context. The generic links endpoint is limited to
    /// explicitly neutral relationships, and must not be able to forge this one.
    /// </summary>
    [Fact]
    public async Task The_generic_links_endpoint_cannot_forge_the_relationship()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        await ProblemReportApiTests.BootstrapAndLoginAsync(client);
        var (projectId, releaseId) = await SeedAsync(factory, "PRRF");
        var first = await RaiseAsync(client, projectId, releaseId, "First report");
        var second = await RaiseAsync(client, projectId, releaseId, "Second report");

        using var forged = await client.PostAsJsonAsync($"/api/problem-reports/{first.Id}/links", new
        {
            expectedVersion = first.Version,
            artifactType = "ProblemReport",
            artifactId = second.Id,
            relationship = "RelatedProblemReport",
        });

        Assert.Equal(HttpStatusCode.BadRequest, forged.StatusCode);
        Assert.Equal("problem_report_relationship_not_generic",
            (await forged.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        Assert.Empty((await RelatedAsync(client, first.Id)).EnumerateArray());
    }

    [Fact]
    public async Task Both_records_carry_the_relationship_in_their_immutable_history()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        await ProblemReportApiTests.BootstrapAndLoginAsync(client);
        var (projectId, releaseId) = await SeedAsync(factory, "PRRH");
        var first = await RaiseAsync(client, projectId, releaseId, "First report");
        var second = await RaiseAsync(client, projectId, releaseId, "Second report");

        using var linked = await client.PostAsJsonAsync($"/api/problem-reports/{first.Id}/related",
            new { relatedProblemReportId = second.Id });
        Assert.Equal(HttpStatusCode.Created, linked.StatusCode);

        foreach (var (id, other) in new[] { (first.Id, second.DisplayNumber), (second.Id, first.DisplayNumber) })
        {
            var revisions = (await client.GetFromJsonAsync<JsonElement>($"/api/problem-reports/{id}"))
                .GetProperty("revisions").EnumerateArray()
                .Where(revision => revision.GetProperty("eventType").GetString() == "RelatedProblemReportLinked")
                .ToList();
            var recorded = Assert.Single(revisions);
            Assert.Contains(other, recorded.GetProperty("detail").GetString());
        }
    }
}
