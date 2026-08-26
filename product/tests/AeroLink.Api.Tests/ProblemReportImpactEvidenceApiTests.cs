using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AeroLink.Domain.Identity;
using AeroLink.Domain.Programs;
using AeroLink.Domain.Releases;
using AeroLink.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace AeroLink.Api.Tests;

/// <summary>
/// The evidence that arrives under each impact answer.
///
/// This is a projection over links other workflows write, never a stored list, and these cover the two
/// properties that makes it worth having: it changes when the linked artifact changes without anybody
/// touching the report, and it never hides a link to make an answer look right.
/// </summary>
public sealed class ProblemReportImpactEvidenceApiTests
{
    private static async Task<(Guid ProjectId, Guid ReleaseId)> SeedAsync(AeroLinkApiFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        var program = new ProgramRecord("Impact Program", $"IMP{Guid.NewGuid():N}"[..12]);
        var project = new ProjectRecord(program.Id, "Flight Management Product", "Flight Management System");
        var release = new SoftwareRelease(project.Id, "1.6", false);
        db.AddRange(program, project, release);
        await db.SaveChangesAsync();
        return (project.Id, release.Id);
    }

    private static async Task<Guid> RaiseAsync(HttpClient client, Guid projectId, Guid releaseId, string impacts)
    {
        using var created = await client.PostAsJsonAsync("/api/problem-reports", new
        {
            projectId, releaseId, category = "CodeFunctional",
            title = $"Impact evidence {Guid.NewGuid():N}"[..28],
            problem = "The tone follows the disconnect.",
            impactAssessmentJson = impacts,
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        return (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    private static JsonElement Area(JsonElement detail, string key) =>
        detail.GetProperty("impactAreas").EnumerateArray()
            .Single(area => area.GetProperty("key").GetString() == key);

    [Fact]
    public async Task Every_assessed_area_is_reported_and_only_some_of_them_can_carry_artifacts()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        await ProblemReportApiTests.BootstrapAndLoginAsync(client);
        var (projectId, releaseId) = await SeedAsync(factory);
        var id = await RaiseAsync(client, projectId, releaseId, "{}");

        var detail = await client.GetFromJsonAsync<JsonElement>($"/api/problem-reports/{id}");
        var areas = detail.GetProperty("impactAreas").EnumerateArray().ToList();

        Assert.Equal(8, areas.Count);
        // System/aircraft and Airworthiness have no controlled artifact type: the narrative is the record,
        // and an empty evidence slot there would imply something is missing.
        Assert.False(Area(detail, "SystemAircraft").GetProperty("hasArtifactSlot").GetBoolean());
        Assert.False(Area(detail, "Airworthiness").GetProperty("hasArtifactSlot").GetBoolean());
        foreach (var key in new[] { "SystemRequirements", "Hlr", "Llr", "Code", "Tests", "Documents" })
            Assert.True(Area(detail, key).GetProperty("hasArtifactSlot").GetBoolean(), key);
    }

    [Fact]
    public async Task An_unanswered_area_reads_as_unknown_rather_than_as_not_impacted()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        await ProblemReportApiTests.BootstrapAndLoginAsync(client);
        var (projectId, releaseId) = await SeedAsync(factory);
        var id = await RaiseAsync(client, projectId, releaseId, "{\"SystemRequirements\":\"Yes\",\"Hlr\":\"No\"}");

        var detail = await client.GetFromJsonAsync<JsonElement>($"/api/problem-reports/{id}");

        Assert.Equal("Yes", Area(detail, "SystemRequirements").GetProperty("assessment").GetString());
        Assert.Equal("No", Area(detail, "Hlr").GetProperty("assessment").GetString());
        Assert.Equal("Unknown", Area(detail, "Llr").GetProperty("assessment").GetString());
    }

    /// <summary>
    /// The answer a change request produces is the point of the whole panel: an SRCR raised against this
    /// report appears under System requirements, carrying its live state and target build, without the
    /// report being edited at all.
    /// </summary>
    [Fact]
    public async Task A_change_request_naming_the_report_appears_under_the_area_it_changes()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        await ProblemReportApiTests.BootstrapAndLoginAsync(client);
        var (projectId, releaseId) = await SeedAsync(factory);
        var id = await RaiseAsync(client, projectId, releaseId, "{\"SystemRequirements\":\"Yes\"}");

        var before = await client.GetFromJsonAsync<JsonElement>($"/api/problem-reports/{id}");
        Assert.Empty(Area(before, "SystemRequirements").GetProperty("artifacts").EnumerateArray());
        var version = before.GetProperty("version").GetInt64();

        using var draft = await client.PostAsJsonAsync("/api/change-request-drafts", new
        {
            projectId, targetReleaseId = releaseId, title = "Advance the disconnect tone",
            problem = "The tone follows the disconnect.", analysis = "Reorder the annunciator queue.",
            solution = "Queue the tone ahead of the annunciator.",
            requirementChanges = Array.Empty<object>(), problemReportIds = new[] { id },
        });
        Assert.True(draft.IsSuccessStatusCode, await draft.Content.ReadAsStringAsync());

        // Nothing edited the Problem Report — its controlled version is unchanged — and yet it now says
        // something different, because the panel is derived rather than stored.
        var after = await client.GetFromJsonAsync<JsonElement>($"/api/problem-reports/{id}");
        Assert.Equal(version, after.GetProperty("version").GetInt64());
        var artifact = Assert.Single(Area(after, "SystemRequirements").GetProperty("artifacts").EnumerateArray());
        Assert.StartsWith("SRCR-", artifact.GetProperty("identifier").GetString());
        Assert.Equal("Advance the disconnect tone", artifact.GetProperty("title").GetString());
        Assert.Equal("Draft", artifact.GetProperty("state").GetString());
        Assert.Equal("1.6", artifact.GetProperty("targetBuild").GetString());
    }

    /// <summary>
    /// The rule this panel exists to obey. Suppressing a link because the answer says "No" would make the
    /// record assert something untrue; changing the answer would put words in an engineer's mouth. Both
    /// are reported, and the disagreement is named.
    /// </summary>
    [Fact]
    public async Task Evidence_under_a_not_impacted_answer_is_shown_and_the_disagreement_is_named()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        await ProblemReportApiTests.BootstrapAndLoginAsync(client);
        var (projectId, releaseId) = await SeedAsync(factory);
        var id = await RaiseAsync(client, projectId, releaseId, "{\"SystemRequirements\":\"No\"}");

        using var draft = await client.PostAsJsonAsync("/api/change-request-drafts", new
        {
            projectId, targetReleaseId = releaseId, title = "Correction raised anyway",
            problem = "The tone follows the disconnect.", analysis = "Reorder the queue.",
            solution = "Queue the tone first.",
            requirementChanges = Array.Empty<object>(), problemReportIds = new[] { id },
        });
        Assert.True(draft.IsSuccessStatusCode, await draft.Content.ReadAsStringAsync());

        var area = Area(await client.GetFromJsonAsync<JsonElement>($"/api/problem-reports/{id}"), "SystemRequirements");

        Assert.Equal("No", area.GetProperty("assessment").GetString());
        // The link is still reported. It is not hidden to make the answer look right.
        Assert.Single(area.GetProperty("artifacts").EnumerateArray());
        var mismatch = area.GetProperty("mismatch").GetString();
        Assert.False(string.IsNullOrWhiteSpace(mismatch));
        Assert.Contains("not impacted", mismatch, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task An_area_whose_answer_and_evidence_agree_carries_no_advisory()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        await ProblemReportApiTests.BootstrapAndLoginAsync(client);
        var (projectId, releaseId) = await SeedAsync(factory);
        var id = await RaiseAsync(client, projectId, releaseId, "{\"SystemRequirements\":\"Yes\",\"Hlr\":\"No\"}");

        using var draft = await client.PostAsJsonAsync("/api/change-request-drafts", new
        {
            projectId, targetReleaseId = releaseId, title = "Expected correction",
            problem = "The tone follows the disconnect.", analysis = "Reorder the queue.",
            solution = "Queue the tone first.",
            requirementChanges = Array.Empty<object>(), problemReportIds = new[] { id },
        });
        Assert.True(draft.IsSuccessStatusCode, await draft.Content.ReadAsStringAsync());

        var detail = await client.GetFromJsonAsync<JsonElement>($"/api/problem-reports/{id}");

        // Impacted, with evidence: nothing to advise about.
        Assert.Equal(JsonValueKind.Null, Area(detail, "SystemRequirements").GetProperty("mismatch").ValueKind);
        // Not impacted, and nothing linked: also nothing to advise about.
        Assert.Equal(JsonValueKind.Null, Area(detail, "Hlr").GetProperty("mismatch").ValueKind);
    }

    /// <summary>
    /// The panel is a read-time projection, so a state change on the linked artifact has to reach the
    /// report with no controlled action on the report itself.
    /// </summary>
    [Fact]
    public async Task A_linked_change_request_moving_state_changes_what_the_report_says()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        await ProblemReportApiTests.BootstrapAndLoginAsync(client);
        var (projectId, releaseId) = await SeedAsync(factory);
        var id = await RaiseAsync(client, projectId, releaseId, "{\"SystemRequirements\":\"Yes\"}");

        using var draft = await client.PostAsJsonAsync("/api/change-request-drafts", new
        {
            projectId, targetReleaseId = releaseId, title = "Moves through review",
            problem = "The tone follows the disconnect.", analysis = "Reorder the queue.",
            solution = "Queue the tone first.",
            requirementChanges = Array.Empty<object>(), problemReportIds = new[] { id },
        });
        Assert.True(draft.IsSuccessStatusCode, await draft.Content.ReadAsStringAsync());
        var changeRequestId = (await draft.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        Assert.Equal("Draft", Assert.Single(Area(await client.GetFromJsonAsync<JsonElement>(
            $"/api/problem-reports/{id}"), "SystemRequirements").GetProperty("artifacts").EnumerateArray())
            .GetProperty("state").GetString());

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        var request = db.SystemChangeRequests.Single(item => item.Id == changeRequestId);
        request.Withdraw("admin", "Superseded by a different correction.", DateTimeOffset.UtcNow);
        await db.SaveChangesAsync();

        var artifact = Assert.Single(Area(await client.GetFromJsonAsync<JsonElement>(
            $"/api/problem-reports/{id}"), "SystemRequirements").GetProperty("artifacts").EnumerateArray());
        Assert.Equal("Withdrawn", artifact.GetProperty("state").GetString());
    }
}
