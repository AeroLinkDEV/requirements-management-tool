using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using AeroLink.Domain.Programs;
using AeroLink.Domain.Releases;
using AeroLink.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace AeroLink.Api.Tests;

/// <summary>
/// A Problem Report is edited under the same exclusive server lease as every other controlled record.
///
/// It used to be the exception: a form of its own posted the whole record with an expected version and hoped
/// nobody else was doing the same, and the edit policy still named lifecycle states the MVP no longer
/// produces — so in practice only a Draft could be checked out at all. These cover the contract that replaced
/// it: which states may be checked out, what a check-in writes, what a discard restores, and what stays
/// immutable throughout.
/// </summary>
public sealed class ProblemReportCheckoutApiTests
{
    /// <summary>
    /// Checks a report out, applies <paramref name="edit"/> to the working copy the server handed back, and
    /// checks it in. Shared with the lifecycle tests so they exercise the real editing path rather than a
    /// test-only shortcut. Returns the report's new controlled version.
    /// </summary>
    public static async Task<long> EditUnderCheckoutAsync(HttpClient client, Guid reportId, Action<JsonObject> edit)
    {
        using var checkout = await client.PostAsJsonAsync("/api/controlled-editing/checkout",
            new { artifactType = "ProblemReport", artifactId = reportId, leaseMinutes = 15 });
        Assert.True(checkout.StatusCode is HttpStatusCode.Created or HttpStatusCode.OK,
            $"Checkout returned {checkout.StatusCode}: {await checkout.Content.ReadAsStringAsync()}");
        var session = await checkout.Content.ReadFromJsonAsync<JsonElement>();
        var sessionId = session.GetProperty("id").GetGuid();
        var sessionVersion = session.GetProperty("version").GetInt64();

        var draft = JsonNode.Parse(session.GetProperty("draftJson").GetString()!)!.AsObject();
        edit(draft);
        using var autosave = await client.PutAsJsonAsync($"/api/controlled-editing/sessions/{sessionId}/autosave",
            new { expectedVersion = sessionVersion, draftJson = draft.ToJsonString(), leaseMinutes = 15 });
        Assert.Equal(HttpStatusCode.OK, autosave.StatusCode);
        sessionVersion = (await autosave.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("version").GetInt64();

        using var checkIn = await client.PostAsJsonAsync($"/api/controlled-editing/sessions/{sessionId}/check-in",
            new { expectedVersion = sessionVersion });
        Assert.True(checkIn.IsSuccessStatusCode, $"Check-in returned {checkIn.StatusCode}: {await checkIn.Content.ReadAsStringAsync()}");

        return (await client.GetFromJsonAsync<JsonElement>($"/api/problem-reports/{reportId}")).GetProperty("version").GetInt64();
    }

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

    private static async Task<Guid> RaiseAsync(HttpClient client, Guid projectId, Guid releaseId)
    {
        using var created = await client.PostAsJsonAsync("/api/problem-reports", new
        {
            projectId, releaseId, title = "Autopilot disconnect tone is late",
            problem = "The tone follows the disconnect by roughly a second.",
            problemRich = "{\"blocks\":[]}", additionalInformation = "Reported by two crews.",
            additionalInformationRich = "{\"blocks\":[]}", systemAircraftImpact = "Crew may not associate the tone with the disconnect.",
            impactAssessmentJson = "{\"SystemRequirements\":\"Yes\",\"Hlr\":\"Unknown\",\"Llr\":\"Unknown\",\"Code\":\"Yes\",\"Tests\":\"Yes\",\"Documents\":\"No\",\"SystemAircraft\":\"Yes\",\"Safety\":\"Unknown\"}"
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        return (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    /// <summary>Walks the report from Draft to Open so the checkout under test is not a Draft one.</summary>
    private static async Task OpenAsync(HttpClient client, Guid id)
    {
        var version = (await client.GetFromJsonAsync<JsonElement>($"/api/problem-reports/{id}")).GetProperty("version").GetInt64();
        using var ready = await client.PostAsJsonAsync($"/api/problem-reports/{id}/ready-for-sccb", new { expectedVersion = version });
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
        version = (await ready.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("version").GetInt64();
        using var opened = await client.PostAsJsonAsync($"/api/problem-reports/{id}/sccb/open", new { expectedVersion = version });
        Assert.Equal(HttpStatusCode.OK, opened.StatusCode);
    }

    [Fact]
    public async Task An_open_report_is_checked_out_edited_and_checked_in_like_every_other_controlled_record()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        await ProblemReportApiTests.BootstrapAndLoginAsync(client);
        var (projectId, releaseId) = await SeedAsync(factory, "PRCO");
        var id = await RaiseAsync(client, projectId, releaseId);
        await OpenAsync(client, id);

        var status = await client.GetFromJsonAsync<JsonElement>($"/api/controlled-editing/status?artifactType=ProblemReport&artifactId={id}");
        Assert.Equal("Open", status.GetProperty("state").GetString());
        // The state that used to refuse a checkout outright.
        Assert.True(status.GetProperty("editable").GetBoolean());
        Assert.False(status.GetProperty("locked").GetBoolean());

        await EditUnderCheckoutAsync(client, id, draft =>
        {
            draft["title"] = "Autopilot disconnect tone lags the disconnect";
            draft["rootCause"] = "The tone is queued behind the mode-annunciation refresh.";
            draft["severity"] = "High";
            draft["priority"] = "Urgent";
            draft["impactAssessmentJson"] = "{\"SystemRequirements\":\"Yes\",\"Hlr\":\"Yes\",\"Llr\":\"Yes\",\"Code\":\"Yes\",\"Tests\":\"Yes\",\"Documents\":\"No\",\"SystemAircraft\":\"Yes\",\"Safety\":\"No\"}";
        });

        var detail = await client.GetFromJsonAsync<JsonElement>($"/api/problem-reports/{id}");
        Assert.Equal("Autopilot disconnect tone lags the disconnect", detail.GetProperty("title").GetString());
        Assert.Equal("The tone is queued behind the mode-annunciation refresh.", detail.GetProperty("rootCause").GetString());
        Assert.Equal("High", detail.GetProperty("severity").GetString());
        Assert.Equal("Urgent", detail.GetProperty("priority").GetString());
        // Editing did not move the record's lifecycle, and a field the edit did not name is not reverted.
        Assert.Equal("Open", detail.GetProperty("state").GetString());
        Assert.Equal("Reported by two crews.", detail.GetProperty("additionalInformation").GetString());
        // And the change is in the report's own History, with who made it and when.
        var entry = Assert.Single(detail.GetProperty("revisions").EnumerateArray(),
            revision => revision.GetProperty("eventType").GetString() == "DetailsCheckedIn");
        Assert.Equal("admin", entry.GetProperty("actor").GetString());
        Assert.True(entry.GetProperty("occurredAt").GetDateTimeOffset() > DateTimeOffset.UtcNow.AddMinutes(-5));

        // The lease is released by the check-in, so the record is immediately checkable-out again.
        var after = await client.GetFromJsonAsync<JsonElement>($"/api/controlled-editing/status?artifactType=ProblemReport&artifactId={id}");
        Assert.False(after.GetProperty("locked").GetBoolean());
    }

    [Fact]
    public async Task Discarding_a_checkout_restores_the_last_checked_in_content()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        await ProblemReportApiTests.BootstrapAndLoginAsync(client);
        var (projectId, releaseId) = await SeedAsync(factory, "PRDI");
        var id = await RaiseAsync(client, projectId, releaseId);
        await OpenAsync(client, id);
        var before = await client.GetFromJsonAsync<JsonElement>($"/api/problem-reports/{id}");

        using var checkout = await client.PostAsJsonAsync("/api/controlled-editing/checkout",
            new { artifactType = "ProblemReport", artifactId = id, leaseMinutes = 15 });
        var session = await checkout.Content.ReadFromJsonAsync<JsonElement>();
        var sessionId = session.GetProperty("id").GetGuid();
        var sessionVersion = session.GetProperty("version").GetInt64();
        var draft = JsonNode.Parse(session.GetProperty("draftJson").GetString()!)!.AsObject();
        draft["title"] = "A title nobody decided to keep";
        using var autosave = await client.PutAsJsonAsync($"/api/controlled-editing/sessions/{sessionId}/autosave",
            new { expectedVersion = sessionVersion, draftJson = draft.ToJsonString(), leaseMinutes = 15 });
        Assert.Equal(HttpStatusCode.OK, autosave.StatusCode);
        sessionVersion = (await autosave.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("version").GetInt64();

        using var discard = await client.PostAsJsonAsync($"/api/controlled-editing/sessions/{sessionId}/discard",
            new { expectedVersion = sessionVersion, reason = "The correction was not needed after all." });
        Assert.True(discard.IsSuccessStatusCode, await discard.Content.ReadAsStringAsync());

        // An autosave is a recovery snapshot, not a save: nothing it held reached the controlled record.
        var after = await client.GetFromJsonAsync<JsonElement>($"/api/problem-reports/{id}");
        Assert.Equal(before.GetProperty("title").GetString(), after.GetProperty("title").GetString());
        Assert.Equal(before.GetProperty("version").GetInt64(), after.GetProperty("version").GetInt64());
        Assert.DoesNotContain(after.GetProperty("revisions").EnumerateArray(),
            revision => revision.GetProperty("eventType").GetString() == "DetailsCheckedIn");
        Assert.False((await client.GetFromJsonAsync<JsonElement>(
            $"/api/controlled-editing/status?artifactType=ProblemReport&artifactId={id}")).GetProperty("locked").GetBoolean());
    }

    [Fact]
    public async Task A_closed_report_refuses_the_checkout_and_reopening_is_the_route_back()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        await ProblemReportApiTests.BootstrapAndLoginAsync(client);
        var (projectId, releaseId) = await SeedAsync(factory, "PRCL");
        var id = await RaiseAsync(client, projectId, releaseId);
        await OpenAsync(client, id);

        var version = (await client.GetFromJsonAsync<JsonElement>($"/api/problem-reports/{id}")).GetProperty("version").GetInt64();
        using var dispositioned = await client.PostAsJsonAsync($"/api/problem-reports/{id}/disposition",
            new { expectedVersion = version, disposition = "Rejected", rationale = "The behaviour is as specified." });
        Assert.Equal(HttpStatusCode.OK, dispositioned.StatusCode);

        var closed = await client.GetFromJsonAsync<JsonElement>($"/api/controlled-editing/status?artifactType=ProblemReport&artifactId={id}");
        Assert.False(closed.GetProperty("editable").GetBoolean());
        using var refused = await client.PostAsJsonAsync("/api/controlled-editing/checkout",
            new { artifactType = "ProblemReport", artifactId = id, leaseMinutes = 15 });
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal("artifact_not_editable", (await refused.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());

        // Reopening is the route back to editable, and it keeps its own rationale requirement.
        version = (await client.GetFromJsonAsync<JsonElement>($"/api/problem-reports/{id}")).GetProperty("version").GetInt64();
        using var reopened = await client.PostAsJsonAsync($"/api/problem-reports/{id}/reopen",
            new { expectedVersion = version, rationale = "A second crew report shows the tone really is late." });
        Assert.Equal(HttpStatusCode.OK, reopened.StatusCode);
        Assert.True((await client.GetFromJsonAsync<JsonElement>(
            $"/api/controlled-editing/status?artifactType=ProblemReport&artifactId={id}")).GetProperty("editable").GetBoolean());
    }

    [Fact]
    public async Task Identity_fields_stay_immutable_while_the_report_is_checked_out()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        await ProblemReportApiTests.BootstrapAndLoginAsync(client);
        var (projectId, releaseId) = await SeedAsync(factory, "PRIM");
        var id = await RaiseAsync(client, projectId, releaseId);
        await OpenAsync(client, id);
        var before = await client.GetFromJsonAsync<JsonElement>($"/api/problem-reports/{id}");

        foreach (var (field, value) in new (string, string)[]
                 { ("reportNumber", "PR-99999"), ("reportedBy", "somebody.else"), ("responsibleEngineerId", "somebody.else") })
        {
            using var checkout = await client.PostAsJsonAsync("/api/controlled-editing/checkout",
                new { artifactType = "ProblemReport", artifactId = id, leaseMinutes = 15 });
            var session = await checkout.Content.ReadFromJsonAsync<JsonElement>();
            var sessionId = session.GetProperty("id").GetGuid();
            var sessionVersion = session.GetProperty("version").GetInt64();
            var draft = JsonNode.Parse(session.GetProperty("draftJson").GetString()!)!.AsObject();
            draft[field] = value;
            using var autosave = await client.PutAsJsonAsync($"/api/controlled-editing/sessions/{sessionId}/autosave",
                new { expectedVersion = sessionVersion, draftJson = draft.ToJsonString(), leaseMinutes = 15 });
            sessionVersion = (await autosave.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("version").GetInt64();

            using var checkIn = await client.PostAsJsonAsync($"/api/controlled-editing/sessions/{sessionId}/check-in",
                new { expectedVersion = sessionVersion });
            Assert.False(checkIn.IsSuccessStatusCode, $"Check-in accepted a changed {field}.");

            using var abandon = await client.PostAsJsonAsync($"/api/controlled-editing/sessions/{sessionId}/discard",
                new { expectedVersion = sessionVersion, reason = "Test cleanup." });
            Assert.True(abandon.IsSuccessStatusCode);
        }

        var after = await client.GetFromJsonAsync<JsonElement>($"/api/problem-reports/{id}");
        Assert.Equal(before.GetProperty("reportNumber").GetString(), after.GetProperty("reportNumber").GetString());
        Assert.Equal(before.GetProperty("reportedBy").GetString(), after.GetProperty("reportedBy").GetString());
        Assert.Equal(before.GetProperty("responsibleEngineerId").GetString(), after.GetProperty("responsibleEngineerId").GetString());
        Assert.Equal(before.GetProperty("createdAt").GetDateTimeOffset(), after.GetProperty("createdAt").GetDateTimeOffset());
    }
}
