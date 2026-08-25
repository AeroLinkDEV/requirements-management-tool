using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using AeroLink.Domain.Identity;
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

    private static async Task<(Guid ProjectId, Guid ReleaseId, string SccbUserName)> SeedAsync(AeroLinkApiFactory factory, string prefix)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        var program = new ProgramRecord($"{prefix} Program", $"{prefix}{Guid.NewGuid():N}"[..12]);
        var project = new ProjectRecord(program.Id, "Flight Management Product", "Flight Management System");
        var release = new SoftwareRelease(project.Id, "1.6", false);
        var admin = db.UserAccounts.Single(account => account.UserName == "admin");
        var sccbUserName = $"{prefix.ToLowerInvariant()}.sccb.{Guid.NewGuid():N}";
        var sccb = new UserAccount(sccbUserName, "SCCB Project Engineer",
            $"{sccbUserName}@example.test", IdentityService.HashPassword(AeroLinkApiFactory.MemberPassword), DateTimeOffset.UtcNow);
        db.AddRange(program, project, release,
            sccb, new ProgramMembership(sccb.Id, program.Id, ProgramRole.ProjectEngineer, "test.setup", DateTimeOffset.UtcNow),
            new ProgramMembership(sccb.Id, program.Id, ProgramRole.SoftwareQualityAnalyst, "test.setup", DateTimeOffset.UtcNow));
        await db.SaveChangesAsync();
        return (project.Id, release.Id, sccbUserName);
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
    private static async Task OpenAsync(HttpClient client, Guid id, string sccbUserName)
    {
        var version = (await client.GetFromJsonAsync<JsonElement>($"/api/problem-reports/{id}")).GetProperty("version").GetInt64();
        using var ready = await client.PostAsJsonAsync($"/api/problem-reports/{id}/ready-for-sccb", new { expectedVersion = version });
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
        version = (await ready.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("version").GetInt64();
        using var login = await client.PostAsJsonAsync("/api/auth/login",
            new { userName = sccbUserName, password = AeroLinkApiFactory.MemberPassword });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        await SecurityBoundaryTests.AuthorizeMutationsAsync(client);
        using var opened = await client.PostAsJsonAsync($"/api/problem-reports/{id}/sccb/open", new { expectedVersion = version });
        Assert.Equal(HttpStatusCode.OK, opened.StatusCode);
        using var restore = await client.PostAsJsonAsync("/api/auth/login",
            new { userName = "admin", password = AeroLinkApiFactory.AdministratorPassword });
        Assert.Equal(HttpStatusCode.OK, restore.StatusCode);
        await SecurityBoundaryTests.AuthorizeMutationsAsync(client);
    }

    [Fact]
    public async Task An_open_report_is_checked_out_edited_and_checked_in_like_every_other_controlled_record()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        await ProblemReportApiTests.BootstrapAndLoginAsync(client);
        var (projectId, releaseId, sccbUserName) = await SeedAsync(factory, "PRCO");
        var id = await RaiseAsync(client, projectId, releaseId);
        await OpenAsync(client, id, sccbUserName);

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
        var (projectId, releaseId, sccbUserName) = await SeedAsync(factory, "PRDI");
        var id = await RaiseAsync(client, projectId, releaseId);
        await OpenAsync(client, id, sccbUserName);
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
        var (projectId, releaseId, sccbUserName) = await SeedAsync(factory, "PRCL");
        var id = await RaiseAsync(client, projectId, releaseId);
        await OpenAsync(client, id, sccbUserName);

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
        using var qualityLogin = await client.PostAsJsonAsync("/api/auth/login",
            new { userName = sccbUserName, password = AeroLinkApiFactory.MemberPassword });
        Assert.Equal(HttpStatusCode.OK, qualityLogin.StatusCode);
        await SecurityBoundaryTests.AuthorizeMutationsAsync(client);
        using var reopened = await client.PostAsJsonAsync($"/api/problem-reports/{id}/reopen",
            new { expectedVersion = version, rationale = "A second crew report shows the tone really is late." });
        Assert.Equal(HttpStatusCode.OK, reopened.StatusCode);
        var reopenedDetail = await client.GetFromJsonAsync<JsonElement>($"/api/problem-reports/{id}");
        Assert.Equal("Draft", reopenedDetail.GetProperty("state").GetString());
        using var adminLogin = await client.PostAsJsonAsync("/api/auth/login",
            new { userName = "admin", password = AeroLinkApiFactory.AdministratorPassword });
        Assert.Equal(HttpStatusCode.OK, adminLogin.StatusCode);
        await SecurityBoundaryTests.AuthorizeMutationsAsync(client);
        Assert.True((await client.GetFromJsonAsync<JsonElement>(
            $"/api/controlled-editing/status?artifactType=ProblemReport&artifactId={id}")).GetProperty("editable").GetBoolean());
    }

    [Fact]
    public async Task Identity_fields_stay_immutable_while_the_report_is_checked_out()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        await ProblemReportApiTests.BootstrapAndLoginAsync(client);
        var (projectId, releaseId, sccbUserName) = await SeedAsync(factory, "PRIM");
        var id = await RaiseAsync(client, projectId, releaseId);
        await OpenAsync(client, id, sccbUserName);
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

    /// <summary>
    /// Seeds a Program member who has access to the Project and nothing else — no engineering role, and
    /// not the report's responsible engineer. Reviewer is deliberate: it is a membership, so Project
    /// access is satisfied, and it is absent from both the engineering-authority set and the four roles
    /// the shared checkout endpoint demands.
    /// </summary>
    private static async Task<string> SeedBystanderAsync(AeroLinkApiFactory factory, Guid projectId, string prefix)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        var programId = db.Projects.Single(project => project.Id == projectId).ProgramId;
        var userName = $"{prefix.ToLowerInvariant()}.bystander.{Guid.NewGuid():N}";
        var account = new UserAccount(userName, "Bystander Reviewer", $"{userName}@example.test",
            IdentityService.HashPassword(AeroLinkApiFactory.MemberPassword), DateTimeOffset.UtcNow);
        db.AddRange(account,
            new ProgramMembership(account.Id, programId, ProgramRole.Reviewer, "test.setup", DateTimeOffset.UtcNow));
        await db.SaveChangesAsync();
        return userName;
    }

    private static async Task LoginAsync(HttpClient client, string userName, string password)
    {
        using var login = await client.PostAsJsonAsync("/api/auth/login", new { userName, password });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        await SecurityBoundaryTests.AuthorizeMutationsAsync(client);
    }

    /// <summary>Walks an Open report to Verifying, which needs nothing but Project access.</summary>
    private static async Task VerifyingAsync(HttpClient client, Guid id)
    {
        foreach (var target in new[] { "Implementing", "Verifying" })
        {
            var version = (await client.GetFromJsonAsync<JsonElement>($"/api/problem-reports/{id}")).GetProperty("version").GetInt64();
            using var moved = await client.PostAsJsonAsync($"/api/problem-reports/{id}/transition",
                new { expectedVersion = version, targetState = target });
            Assert.Equal(HttpStatusCode.OK, moved.StatusCode);
        }
    }

    /// <summary>
    /// The reported defect: a report in Verifying offered no way in to anybody but its responsible
    /// engineer, so the tester who found the mistake in it could only raise a second report saying so.
    ///
    /// Every other test in this class runs as the administrator, which was always exempt from the
    /// governing-author comparison — which is exactly why the whole suite stayed green while the rule it
    /// was meant to cover was wrong. This one runs as an ordinary member holding no engineering role.
    /// </summary>
    [Fact]
    public async Task A_project_member_who_does_not_own_the_report_can_correct_it_in_Verifying()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        await ProblemReportApiTests.BootstrapAndLoginAsync(client);
        var (projectId, releaseId, sccbUserName) = await SeedAsync(factory, "PRBY");
        var id = await RaiseAsync(client, projectId, releaseId);
        await OpenAsync(client, id, sccbUserName);
        await VerifyingAsync(client, id);
        var bystander = await SeedBystanderAsync(factory, projectId, "PRBY");

        var owner = (await client.GetFromJsonAsync<JsonElement>($"/api/problem-reports/{id}"))
            .GetProperty("responsibleEngineerId").GetString();
        Assert.NotEqual(bystander, owner);

        await LoginAsync(client, bystander, AeroLinkApiFactory.MemberPassword);
        var status = await client.GetFromJsonAsync<JsonElement>(
            $"/api/controlled-editing/status?artifactType=ProblemReport&artifactId={id}");
        Assert.Equal("Verifying", status.GetProperty("state").GetString());
        Assert.True(status.GetProperty("editable").GetBoolean(),
            "A Verifying report must offer a checkout to any member of its Project.");

        await EditUnderCheckoutAsync(client, id, draft => draft["rootCause"] = "The tone is queued behind the annunciator.");

        var detail = await client.GetFromJsonAsync<JsonElement>($"/api/problem-reports/{id}");
        Assert.Equal("The tone is queued behind the annunciator.", detail.GetProperty("rootCause").GetString());
        // Ownership is a separate decision from correctness, and correcting the report does not take it.
        Assert.Equal(owner, detail.GetProperty("responsibleEngineerId").GetString());
        // The correction is credited to whoever made it, not to whoever the report is assigned to.
        var checkIn = detail.GetProperty("revisions").EnumerateArray()
            .First(revision => revision.GetProperty("eventType").GetString() == "DetailsCheckedIn");
        Assert.Equal(bystander, checkIn.GetProperty("actor").GetString());
    }

    /// <summary>
    /// The lease is what makes shared editing safe, so widening who may check out must not widen how many
    /// may hold it at once.
    /// </summary>
    [Fact]
    public async Task A_second_member_is_refused_while_somebody_else_holds_the_lease()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        await ProblemReportApiTests.BootstrapAndLoginAsync(client);
        var (projectId, releaseId, sccbUserName) = await SeedAsync(factory, "PRLS");
        var id = await RaiseAsync(client, projectId, releaseId);
        await OpenAsync(client, id, sccbUserName);
        var first = await SeedBystanderAsync(factory, projectId, "PRLS");
        var second = await SeedBystanderAsync(factory, projectId, "PRLT");

        await LoginAsync(client, first, AeroLinkApiFactory.MemberPassword);
        using var held = await client.PostAsJsonAsync("/api/controlled-editing/checkout",
            new { artifactType = "ProblemReport", artifactId = id, leaseMinutes = 15 });
        Assert.True(held.IsSuccessStatusCode, await held.Content.ReadAsStringAsync());

        await LoginAsync(client, second, AeroLinkApiFactory.MemberPassword);
        using var refused = await client.PostAsJsonAsync("/api/controlled-editing/checkout",
            new { artifactType = "ProblemReport", artifactId = id, leaseMinutes = 15 });
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        var body = await refused.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("exclusive_lock", body.GetProperty("code").GetString());
        Assert.Equal(first, body.GetProperty("holder").GetString());

        var status = await client.GetFromJsonAsync<JsonElement>(
            $"/api/controlled-editing/status?artifactType=ProblemReport&artifactId={id}");
        Assert.True(status.GetProperty("locked").GetBoolean());
        Assert.False(status.GetProperty("mine").GetBoolean());
    }

    /// <summary>
    /// A finished report is revived, never edited in place. The capability reports whether the reopen edge
    /// is available to this actor, and it is the same SQA-only edge the transition policy already governs.
    /// </summary>
    [Fact]
    public async Task Only_Software_Quality_is_offered_the_revive_on_a_rejected_report()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        await ProblemReportApiTests.BootstrapAndLoginAsync(client);
        var (projectId, releaseId, sccbUserName) = await SeedAsync(factory, "PRRV");
        var id = await RaiseAsync(client, projectId, releaseId);
        await OpenAsync(client, id, sccbUserName);
        var bystander = await SeedBystanderAsync(factory, projectId, "PRRV");

        var version = (await client.GetFromJsonAsync<JsonElement>($"/api/problem-reports/{id}")).GetProperty("version").GetInt64();
        using var rejected = await client.PostAsJsonAsync($"/api/problem-reports/{id}/disposition",
            new { expectedVersion = version, disposition = "Rejected", rationale = "The behaviour is as specified." });
        Assert.Equal(HttpStatusCode.OK, rejected.StatusCode);

        await LoginAsync(client, bystander, AeroLinkApiFactory.MemberPassword);
        var toMember = await client.GetFromJsonAsync<JsonElement>($"/api/problem-reports/{id}");
        Assert.False(toMember.GetProperty("capabilities").GetProperty("canRevive").GetBoolean());
        Assert.False((await client.GetFromJsonAsync<JsonElement>(
            $"/api/controlled-editing/status?artifactType=ProblemReport&artifactId={id}")).GetProperty("editable").GetBoolean());

        await LoginAsync(client, sccbUserName, AeroLinkApiFactory.MemberPassword);
        var toQuality = await client.GetFromJsonAsync<JsonElement>($"/api/problem-reports/{id}");
        var capabilities = toQuality.GetProperty("capabilities");
        Assert.True(capabilities.GetProperty("canRevive").GetBoolean());
        Assert.Equal("Draft", capabilities.GetProperty("reviveTargetState").GetString());

        // Reviving is the reopen it always was: the rationale is mandatory and the revision advances.
        var beforeRevision = toQuality.GetProperty("revision").GetInt32();
        version = toQuality.GetProperty("version").GetInt64();
        using var revived = await client.PostAsJsonAsync($"/api/problem-reports/{id}/transition",
            new { expectedVersion = version, targetState = "Draft", rationale = "A second crew report shows the tone really is late." });
        Assert.Equal(HttpStatusCode.OK, revived.StatusCode);
        var after = await client.GetFromJsonAsync<JsonElement>($"/api/problem-reports/{id}");
        Assert.Equal("Draft", after.GetProperty("state").GetString());
        Assert.Equal(beforeRevision + 1, after.GetProperty("revision").GetInt32());
        Assert.True((await client.GetFromJsonAsync<JsonElement>(
            $"/api/controlled-editing/status?artifactType=ProblemReport&artifactId={id}")).GetProperty("editable").GetBoolean());
    }
}
