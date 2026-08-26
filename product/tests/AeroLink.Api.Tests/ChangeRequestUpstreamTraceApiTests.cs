using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Identity;
using AeroLink.Domain.Programs;
using AeroLink.Domain.Requirements;
using AeroLink.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace AeroLink.Api.Tests;

/// <summary>Phase-one upstream CR authoring at the live lease, eligibility, and review boundaries.</summary>
public sealed class ChangeRequestUpstreamTraceApiTests : IClassFixture<SharedApiHost>
{
    private readonly SharedApiHost _host;
    public ChangeRequestUpstreamTraceApiTests(SharedApiHost host) => _host = host;

    private sealed record Fixture(Guid ProjectId, Guid EarlierReleaseId, Guid CurrentReleaseId,
        Guid EarlierSourceId, Guid CurrentSourceId, Guid FutureSourceId, Guid ForeignSourceId, Guid ChildId,
        Guid? AssessmentId, Guid? AssessmentLinkId, string Author, string Approver, string Outsider);

    private static async Task<Fixture> SeedAsync(AeroLinkApiFactory factory, bool withDerivedEdge)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        var now = DateTimeOffset.UtcNow;
        var tag = Guid.NewGuid().ToString("N")[..8];
        var authorName = $"trace.author.{tag}";
        var approverName = $"trace.approver.{tag}";
        var outsiderName = $"trace.outsider.{tag}";
        var program = new ProgramRecord($"CR Trace {tag}", $"CT{tag}");
        var project = new ProjectRecord(program.Id, "Flight controls", "Trace qualification");
        var earlierRelease = new SoftwareRelease(project.Id, "1.6", true);
        var currentRelease = new SoftwareRelease(project.Id, "1.7", false, earlierRelease.Id);
        var futureRelease = new SoftwareRelease(project.Id, "1.8", false, currentRelease.Id);
        var author = new UserAccount(authorName, authorName, $"{authorName}@example.test",
            IdentityService.HashPassword(AeroLinkApiFactory.MemberPassword), now);
        var approver = new UserAccount(approverName, approverName, $"{approverName}@example.test",
            IdentityService.HashPassword(AeroLinkApiFactory.MemberPassword), now);
        var outsider = new UserAccount(outsiderName, outsiderName, $"{outsiderName}@example.test",
            IdentityService.HashPassword(AeroLinkApiFactory.MemberPassword), now);
        db.AddRange(program, project, earlierRelease, currentRelease, futureRelease, author, approver, outsider,
            new ProgramMembership(author.Id, program.Id, ProgramRole.Engineer, "test.setup", now),
            new ProgramMembership(approver.Id, program.Id, ProgramRole.Approver, "test.setup", now));

        SystemChangeRequest Source(string number, SoftwareRelease release, string title)
        {
            var source = new SystemChangeRequest(number, 0, project.Id, release.Id, title,
                "Problem", "Analysis", "Solution", authorName, now);
            source.AddRequirementChange(authorName, number.Replace("SRCR", "SYSR"), 0,
                RequirementLevel.System, RequirementChangeKind.Introduce,
                $"The system shall satisfy {title}.", "Controlled source.", "Test", now);
            source.SubmitForReview(authorName, [new ApproverSelection(approverName, "Trace Approver")], now);
            source.ApproveActiveStage(approverName, now.AddMinutes(1));
            return source;
        }

        var earlierSource = Source("SRCR-78601", earlierRelease, "Earlier-build source");
        var currentSource = Source("SRCR-78602", currentRelease, "Current-build source");
        var futureSource = Source("SRCR-78603", futureRelease, "Future-build source");
        var foreignProgram = new ProgramRecord($"Foreign CR Trace {tag}", $"FT{tag}");
        var foreignProject = new ProjectRecord(foreignProgram.Id, "Foreign controls", "Isolation boundary");
        var foreignRelease = new SoftwareRelease(foreignProject.Id, "1.7", false);
        var foreignSource = new SystemChangeRequest("SRCR-78605", 0, foreignProject.Id, foreignRelease.Id,
            "Foreign-project source", "Problem", "Analysis", "Solution", authorName, now);
        foreignSource.AddRequirementChange(authorName, "SYSR-078605", 0,
            RequirementLevel.System, RequirementChangeKind.Introduce,
            "The foreign system shall remain isolated.", "Controlled foreign source.", "Test", now);
        foreignSource.SubmitForReview(authorName, [new ApproverSelection(approverName, "Trace Approver")], now);
        foreignSource.ApproveActiveStage(approverName, now.AddMinutes(1));
        var child = new SystemChangeRequest("HLRCR-78604", 0, project.Id, currentRelease.Id,
            "Downstream controlled work", "Problem", "Analysis", "Solution", authorName, now,
            ChangeRequestType.Software, softwareLevel: RequirementLevel.HighLevel);
        child.AddRequirementChange(authorName, "HLR-078604", 0, RequirementLevel.HighLevel,
            RequirementChangeKind.Modify, "The software shall implement the controlled source.",
            "This requirement is independently derived for the trace fixture.", "Test", now,
            attributesJson: "{\"derived\":true}");
        db.AddRange(earlierSource, currentSource, futureSource, foreignProgram, foreignProject, foreignRelease,
            foreignSource, child);

        Guid? assessmentId = null; Guid? assessmentLinkId = null;
        if (withDerivedEdge)
        {
            var assessment = new DownstreamChangeAssessment(project.Id, currentRelease.Id, currentSource.Id,
                currentSource.DisplayNumber, RequirementLevel.HighLevel, now);
            assessment.Assign(authorName, authorName, now);
            assessment.RecordChangeRequired(authorName, now);
            assessment.LinkChangeRequest(authorName, child.Id, child.DisplayNumber, now);
            db.Add(assessment);
            assessmentId = assessment.Id;
            assessmentLinkId = assessment.ChangeRequestLinks.Single().Id;
        }
        await db.SaveChangesAsync();
        return new(project.Id, earlierRelease.Id, currentRelease.Id, earlierSource.Id, currentSource.Id,
            futureSource.Id, foreignSource.Id, child.Id, assessmentId, assessmentLinkId, authorName,
            approverName, outsiderName);
    }

    private static async Task SignInAsync(HttpClient client, string userName)
    {
        using var login = await client.PostAsJsonAsync("/api/auth/login",
            new { userName, password = AeroLinkApiFactory.MemberPassword });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        await SecurityBoundaryTests.AuthorizeMutationsAsync(client);
    }

    private static async Task<(Guid SessionId, long Version, JsonObject Draft)> CheckoutAsync(
        HttpClient client, Guid changeRequestId)
    {
        using var response = await client.PostAsJsonAsync("/api/controlled-editing/checkout",
            new { artifactType = "SCR", artifactId = changeRequestId, leaseMinutes = 15 });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
        return (body.GetProperty("id").GetGuid(), body.GetProperty("version").GetInt64(),
            JsonNode.Parse(body.GetProperty("draftJson").GetString()!)!.AsObject());
    }

    private static async Task<long> AutosaveAsync(HttpClient client, Guid sessionId, long expectedVersion,
        JsonObject draft)
    {
        using var response = await client.PutAsJsonAsync($"/api/controlled-editing/sessions/{sessionId}/autosave",
            new { expectedVersion, draftJson = draft.ToJsonString() });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync())
            .GetProperty("version").GetInt64();
    }

    [Fact]
    public async Task Candidate_search_and_check_in_hold_exact_build_and_review_snapshot_contracts()
    {
        var fixture = await SeedAsync(_host.Factory, withDerivedEdge: false);
        using var client = _host.CreateClient();
        await SignInAsync(client, fixture.Author);

        var normal = await client.GetFromJsonAsync<JsonElement>(
            $"/api/change-requests/{fixture.ChildId}/upstream-candidates?limit=25");
        var normalIds = normal.GetProperty("candidates").EnumerateArray()
            .Select(x => x.GetProperty("id").GetGuid()).ToHashSet();
        Assert.Contains(fixture.CurrentSourceId, normalIds);
        Assert.DoesNotContain(fixture.EarlierSourceId, normalIds);
        Assert.DoesNotContain(fixture.FutureSourceId, normalIds);

        var expanded = await client.GetFromJsonAsync<JsonElement>(
            $"/api/change-requests/{fixture.ChildId}/upstream-candidates?includeEarlierBuilds=true&search=earlier");
        var earlier = Assert.Single(expanded.GetProperty("candidates").EnumerateArray());
        Assert.Equal(fixture.EarlierSourceId, earlier.GetProperty("id").GetGuid());
        Assert.True(earlier.GetProperty("earlierBuild").GetBoolean());
        Assert.Equal("1.6", earlier.GetProperty("build").GetString());

        var checkout = await CheckoutAsync(client, fixture.ChildId);
        checkout.Draft["upstreamLinks"] = new JsonArray(new JsonObject
        {
            ["upstreamChangeRequestId"] = fixture.EarlierSourceId,
            ["rationale"] = "The signed 1.6 system decision remains the controlling source for 1.7."
        });
        checkout.Draft["noUpstreamRationale"] = null;
        var sessionVersion = await AutosaveAsync(client, checkout.SessionId, checkout.Version, checkout.Draft);
        using var checkedIn = await client.PostAsJsonAsync(
            $"/api/controlled-editing/sessions/{checkout.SessionId}/check-in",
            new { expectedVersion = sessionVersion });
        var checkedInBody = await checkedIn.Content.ReadAsStringAsync();
        Assert.True(checkedIn.StatusCode == HttpStatusCode.OK, checkedInBody);
        var artifactVersion = JsonSerializer.Deserialize<JsonElement>(checkedInBody)
            .GetProperty("resultingArtifactVersion").GetInt64();

        var rationaleCheckout = await CheckoutAsync(client, fixture.ChildId);
        var persistedLink = Assert.Single(rationaleCheckout.Draft["upstreamLinks"]!.AsArray())!.AsObject();
        persistedLink["rationale"] =
            "The signed 1.6 system decision remains controlling after the 1.7 rationale review.";
        var rationaleSessionVersion = await AutosaveAsync(client, rationaleCheckout.SessionId,
            rationaleCheckout.Version, rationaleCheckout.Draft);
        using var rationaleCheckedIn = await client.PostAsJsonAsync(
            $"/api/controlled-editing/sessions/{rationaleCheckout.SessionId}/check-in",
            new { expectedVersion = rationaleSessionVersion });
        var rationaleBody = await rationaleCheckedIn.Content.ReadAsStringAsync();
        Assert.True(rationaleCheckedIn.StatusCode == HttpStatusCode.OK, rationaleBody);
        artifactVersion = JsonSerializer.Deserialize<JsonElement>(rationaleBody)
            .GetProperty("resultingArtifactVersion").GetInt64();

        using var submit = await client.PostAsJsonAsync($"/api/change-requests/{fixture.ChildId}/submit",
            new { expectedVersion = artifactVersion, mode = "Sequential",
                approvers = new[] { new { userId = fixture.Approver, name = "Trace Approver" } } });
        var submitBody = await submit.Content.ReadAsStringAsync();
        Assert.True(submit.StatusCode == HttpStatusCode.OK, submitBody);
        var detail = JsonSerializer.Deserialize<JsonElement>(submitBody);
        var stored = Assert.Single(detail.GetProperty("upstream").EnumerateArray());
        Assert.Equal(fixture.EarlierSourceId, stored.GetProperty("upstreamChangeRequestId").GetGuid());
        Assert.Equal(fixture.EarlierReleaseId, stored.GetProperty("upstreamBuildId").GetGuid());
        Assert.Equal("1.6", stored.GetProperty("upstreamBuildVersion").GetString());
        Assert.Equal(fixture.Author, stored.GetProperty("actor").GetString());
        Assert.Contains("after the 1.7 rationale review", stored.GetProperty("rationale").GetString(),
            StringComparison.Ordinal);
        var changed = Assert.Single(detail.GetProperty("upstreamHistory").EnumerateArray(),
            item => item.GetProperty("action").GetString() == "Changed");
        Assert.NotEqual(Guid.Empty, changed.GetProperty("upstreamLinkId").GetGuid());
        var cycle = Assert.Single(detail.GetProperty("reviewCycles").EnumerateArray());
        Assert.Equal(3, cycle.GetProperty("snapshotContractVersion").GetInt32());
        Assert.Contains(fixture.EarlierSourceId.ToString(), cycle.GetProperty("snapshotJson").GetString(),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Derived_edge_rejects_authored_duplicate_and_frozen_evidence_survives_reopening()
    {
        var fixture = await SeedAsync(_host.Factory, withDerivedEdge: true);
        using var client = _host.CreateClient();
        await SignInAsync(client, fixture.Author);

        var candidates = await client.GetFromJsonAsync<JsonElement>(
            $"/api/change-requests/{fixture.ChildId}/upstream-candidates?limit=25");
        var derived = Assert.Single(candidates.GetProperty("derivedEdges").EnumerateArray());
        Assert.Equal(fixture.CurrentSourceId, derived.GetProperty("upstreamChangeRequestId").GetGuid());
        Assert.Equal(fixture.AssessmentId, derived.GetProperty("assessmentId").GetGuid());
        Assert.Equal(fixture.AssessmentLinkId, derived.GetProperty("assessmentLinkId").GetGuid());

        var checkout = await CheckoutAsync(client, fixture.ChildId);
        checkout.Draft["upstreamLinks"] = new JsonArray(new JsonObject
        {
            ["upstreamChangeRequestId"] = fixture.CurrentSourceId,
            ["rationale"] = "This deliberately duplicates the assessment-owned edge."
        });
        var sessionVersion = await AutosaveAsync(client, checkout.SessionId, checkout.Version, checkout.Draft);
        using var refused = await client.PostAsJsonAsync(
            $"/api/controlled-editing/sessions/{checkout.SessionId}/check-in",
            new { expectedVersion = sessionVersion });
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Contains("assessment-derived", await refused.Content.ReadAsStringAsync(),
            StringComparison.OrdinalIgnoreCase);
        using var discarded = await client.PostAsJsonAsync(
            $"/api/controlled-editing/sessions/{checkout.SessionId}/discard",
            new { expectedVersion = sessionVersion, reason = "Discard the deliberately invalid duplicate." });
        Assert.Equal(HttpStatusCode.NoContent, discarded.StatusCode);

        using var submit = await client.PostAsJsonAsync($"/api/change-requests/{fixture.ChildId}/submit",
            new { expectedVersion = 1L, mode = "Sequential",
                approvers = new[] { new { userId = fixture.Approver, name = "Trace Approver" } } });
        var submitBody = await submit.Content.ReadAsStringAsync();
        Assert.True(submit.StatusCode == HttpStatusCode.OK, submitBody);
        var submitted = JsonSerializer.Deserialize<JsonElement>(submitBody);
        var cycle = Assert.Single(submitted.GetProperty("reviewCycles").EnumerateArray());
        var frozenJson = cycle.GetProperty("snapshotJson").GetString()!;
        Assert.Contains(fixture.AssessmentId!.Value.ToString(), frozenJson, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(fixture.AssessmentLinkId!.Value.ToString(), frozenJson, StringComparison.OrdinalIgnoreCase);

        using var reopened = await client.PostAsJsonAsync(
            $"/api/downstream-assessments/{fixture.AssessmentId}/reopen",
            new { reason = "The engineering assessment is being corrected." });
        var reopenBody = await reopened.Content.ReadAsStringAsync();
        Assert.True(reopened.StatusCode == HttpStatusCode.OK, reopenBody);
        var after = await client.GetFromJsonAsync<JsonElement>(
            $"/api/change-requests/{fixture.ChildId}/upstream-candidates?limit=25");
        Assert.Empty(after.GetProperty("derivedEdges").EnumerateArray());
        using var detailResponse = await client.GetAsync($"/api/change-requests/{fixture.ChildId}");
        var detail = JsonSerializer.Deserialize<JsonElement>(await detailResponse.Content.ReadAsStringAsync());
        var preserved = Assert.Single(detail.GetProperty("reviewCycles").EnumerateArray())
            .GetProperty("snapshotJson").GetString()!;
        Assert.Contains(fixture.AssessmentLinkId.Value.ToString(), preserved, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Detail_and_candidate_search_refuse_an_authenticated_non_member()
    {
        var fixture = await SeedAsync(_host.Factory, withDerivedEdge: false);
        using var client = _host.CreateClient();
        await SignInAsync(client, fixture.Outsider);

        using var detail = await client.GetAsync($"/api/change-requests/{fixture.ChildId}");
        Assert.Equal(HttpStatusCode.Forbidden, detail.StatusCode);

        using var candidates = await client.GetAsync(
            $"/api/change-requests/{fixture.ChildId}/upstream-candidates?limit=25");
        Assert.Equal(HttpStatusCode.Forbidden, candidates.StatusCode);
    }

    [Fact]
    public async Task Check_in_revalidates_future_build_and_cross_project_ids_from_the_browser()
    {
        var fixture = await SeedAsync(_host.Factory, withDerivedEdge: false);
        using var client = _host.CreateClient();
        await SignInAsync(client, fixture.Author);

        async Task AssertRefusedAsync(Guid upstreamId, string expected)
        {
            var checkout = await CheckoutAsync(client, fixture.ChildId);
            checkout.Draft["upstreamLinks"] = new JsonArray(new JsonObject
            {
                ["upstreamChangeRequestId"] = upstreamId,
                ["rationale"] = "A crafted browser payload must not bypass server eligibility."
            });
            checkout.Draft["noUpstreamRationale"] = null;
            var sessionVersion = await AutosaveAsync(client, checkout.SessionId, checkout.Version, checkout.Draft);
            using var refused = await client.PostAsJsonAsync(
                $"/api/controlled-editing/sessions/{checkout.SessionId}/check-in",
                new { expectedVersion = sessionVersion });
            Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
            Assert.Contains(expected, await refused.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
            using var discarded = await client.PostAsJsonAsync(
                $"/api/controlled-editing/sessions/{checkout.SessionId}/discard",
                new { expectedVersion = sessionVersion, reason = "Discard the deliberately invalid trace answer." });
            Assert.Equal(HttpStatusCode.NoContent, discarded.StatusCode);
        }

        await AssertRefusedAsync(fixture.FutureSourceId, "earlier-build");
        await AssertRefusedAsync(fixture.ForeignSourceId, "outside this Project");
    }
}
