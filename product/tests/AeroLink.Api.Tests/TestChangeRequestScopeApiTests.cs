using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AeroLink.Domain.Baselines;
using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Identity;
using AeroLink.Domain.Programs;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AeroLink.Api.Tests;

/// <summary>
/// What a test change request answers for, over HTTP.
///
/// The default is one change request to one test change request. A verification engineer may fold a second
/// change in when the two are sensibly tested as one package, and may take it back out. A change already
/// claimed elsewhere is refused by name, so the engineer is told which package holds it rather than being
/// told to try again.
/// </summary>
public sealed class TestChangeRequestScopeApiTests
{
    private sealed record Fixture(Guid ProjectId, Guid ReleaseId, Guid FirstReviewId, Guid SecondReviewId,
        Guid FirstChangeId, Guid SecondChangeId, Guid OtherBuildChangeId);

    private static async Task<Fixture> SeedAsync(AeroLinkApiFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        var impact = scope.ServiceProvider.GetRequiredService<VerificationImpactService>();
        var now = DateTimeOffset.UtcNow;

        var program = new ProgramRecord("Scope Program", "SCP");
        var project = new ProjectRecord(program.Id, "Software", "Scope Software");
        var release = new SoftwareRelease(project.Id, "1.6", false);
        var otherBuild = new SoftwareRelease(project.Id, "1.7", false);
        db.AddRange(program, project, release, otherBuild);

        SystemChangeRequest Approved(string number, string requirement, Guid releaseId)
        {
            var scr = new SystemChangeRequest(number, 0, project.Id, releaseId, "Oceanic", "P", "A", "S", "author", now);
            scr.AddRequirementChange("author", requirement, 0, RequirementLevel.System, RequirementChangeKind.Introduce,
                "The FMS shall sequence oceanic waypoints.", "New capability", "Analysis", now);
            scr.SubmitForReview("author", [new("reviewer", "Reviewer")], now);
            scr.ApproveActiveStage("reviewer", now);
            return scr;
        }

        var first = Approved("SRCR-00900", "SYSR-00000901", release.Id);
        var second = Approved("SRCR-00901", "SYSR-00000902", release.Id);
        var elsewhere = Approved("SRCR-00902", "SYSR-00000903", otherBuild.Id);
        db.AddRange(first, second, elsewhere);

        foreach (var (user, role) in new[]
                 {
                     ("scope.engineer", ProgramRole.TestEngineer),
                     ("scope.outsider", ProgramRole.Engineer),
                 })
        {
            var account = new UserAccount(user, user, $"{user}@example.test",
                IdentityService.HashPassword(AeroLinkApiFactory.MemberPassword), now);
            db.Add(account);
            db.Add(new ProgramMembership(account.Id, program.Id, role, "test.setup", now));
        }
        await db.SaveChangesAsync();

        // Raised exactly as change-request approval raises it, so the packages under test are the real ones.
        foreach (var id in new[] { first.Id, second.Id, elsewhere.Id })
        {
            var tracked = await db.SystemChangeRequests.Include(x => x.RequirementChanges).SingleAsync(x => x.Id == id);
            await impact.RaiseForApprovedChangeRequestAsync(tracked, now, default);
        }
        await db.SaveChangesAsync();

        var firstReview = await db.TestChangeReviews.AsNoTracking().SingleAsync(x => x.ChangeRequestId == first.Id);
        var secondReview = await db.TestChangeReviews.AsNoTracking().SingleAsync(x => x.ChangeRequestId == second.Id);
        return new(project.Id, release.Id, firstReview.Id, secondReview.Id, first.Id, second.Id, elsewhere.Id);
    }

    private static async Task LoginAsync(HttpClient client, string user)
    {
        using var login = await client.PostAsJsonAsync("/api/auth/login",
            new { userName = user, password = AeroLinkApiFactory.MemberPassword });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        await SecurityBoundaryTests.AuthorizeMutationsAsync(client);
    }

    [Fact]
    public async Task An_approved_change_is_raised_to_be_assessed_and_numbered_only_once_it_needs_test_work()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        var fixture = await SeedAsync(factory);
        await LoginAsync(client, "scope.engineer");

        using var response = await client.GetAsync($"/api/releases/{fixture.ReleaseId}/test-change-reviews");
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {body}");
        var raised = JsonSerializer.Deserialize<JsonElement>(body).GetProperty("items").EnumerateArray().ToList();

        // Raised as questions, not as controlled records. Each one shows the change it is asking about until
        // somebody answers, exactly as a downstream requirement assessment does.
        Assert.All(raised, review => Assert.Equal("Pending", review.GetProperty("outcome").GetString()));
        Assert.All(raised, review => Assert.Matches(@"^SRCR-\d{5}\.\d{2}$", review.GetProperty("displayNumber").GetString()!));

        var first = raised[0].GetProperty("id").GetGuid();
        using var concluded = await client.PostAsJsonAsync($"/api/test-change-reviews/{first}/conclusion",
            new { testChangeRequired = true });
        Assert.True(concluded.IsSuccessStatusCode, await concluded.Content.ReadAsStringAsync());

        // Answering that test work is required is what brings the SYSTPCR into being.
        var detail = await concluded.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("ChangeRequired", detail.GetProperty("outcome").GetString());
        Assert.Matches(@"^SYSTPCR-\d{6}\.\d{2}$", detail.GetProperty("displayNumber").GetString()!);
    }

    [Fact]
    public async Task Concluding_that_no_test_work_is_needed_raises_no_test_change_request()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        var fixture = await SeedAsync(factory);
        await LoginAsync(client, "scope.engineer");

        var raised = JsonSerializer.Deserialize<JsonElement>(
            await client.GetStringAsync($"/api/releases/{fixture.ReleaseId}/test-change-reviews"))
            .GetProperty("items").EnumerateArray().First().GetProperty("id").GetGuid();

        using var refused = await client.PostAsJsonAsync($"/api/test-change-reviews/{raised}/conclusion",
            new { testChangeRequired = false, rationale = "" });
        // The conclusion that produces nothing is the one that has to say why.
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);

        using var concluded = await client.PostAsJsonAsync($"/api/test-change-reviews/{raised}/conclusion",
            new { testChangeRequired = false, rationale = "The existing procedures already exercise this wording." });
        Assert.True(concluded.IsSuccessStatusCode, await concluded.Content.ReadAsStringAsync());
        var detail = await concluded.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("NoChangeRequired", detail.GetProperty("outcome").GetString());
        // No number, because no test change request exists — that is the content of the conclusion.
        Assert.Equal("", detail.GetProperty("baseNumber").GetString());
    }

    [Fact]
    public async Task A_second_change_request_can_be_folded_in_and_taken_back_out()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        var fixture = await SeedAsync(factory);
        await LoginAsync(client, "scope.engineer");

        using var included = await client.PostAsJsonAsync(
            $"/api/test-change-reviews/{fixture.FirstReviewId}/change-requests", new { changeRequestId = fixture.SecondChangeId });
        var body = await included.Content.ReadAsStringAsync();
        Assert.True(included.IsSuccessStatusCode, $"{(int)included.StatusCode}: {body}");

        using var listed = await client.GetAsync($"/api/releases/{fixture.ReleaseId}/test-change-reviews");
        var reviews = JsonSerializer.Deserialize<JsonElement>(await listed.Content.ReadAsStringAsync());
        var package = reviews.GetProperty("items").EnumerateArray().Single(x => x.GetProperty("id").GetGuid() == fixture.FirstReviewId);
        var covered = package.GetProperty("coveredChangeRequests").EnumerateArray().ToList();
        Assert.Equal(2, covered.Count);
        // The change it was raised from is distinguishable from the one folded in: a reader needs to know
        // which change actually created the package.
        Assert.Single(covered, x => x.GetProperty("originating").GetBoolean());

        using var removed = await client.DeleteAsync(
            $"/api/test-change-reviews/{fixture.FirstReviewId}/change-requests/{fixture.SecondChangeId}");
        Assert.True(removed.IsSuccessStatusCode, await removed.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_change_already_covered_elsewhere_is_refused_by_name()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        var fixture = await SeedAsync(factory);
        await LoginAsync(client, "scope.engineer");

        // Assessed first, so the package doing the covering is a controlled test change request and the
        // refusal can name it as one.
        using var assessed = await client.PostAsJsonAsync(
            $"/api/test-change-reviews/{fixture.FirstReviewId}/conclusion", new { testChangeRequired = true });
        Assert.True(assessed.IsSuccessStatusCode, await assessed.Content.ReadAsStringAsync());

        using var first = await client.PostAsJsonAsync(
            $"/api/test-change-reviews/{fixture.FirstReviewId}/change-requests", new { changeRequestId = fixture.SecondChangeId });
        Assert.True(first.IsSuccessStatusCode, await first.Content.ReadAsStringAsync());

        using var second = await client.PostAsJsonAsync(
            $"/api/test-change-reviews/{fixture.SecondReviewId}/change-requests", new { changeRequestId = fixture.SecondChangeId });
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        var body = await second.Content.ReadAsStringAsync();
        // Named, so the engineer knows where the change went rather than being told to try again.
        Assert.Contains("SRCR-00901", body);
        Assert.Contains("SYSTPCR-", body);
    }

    /// <summary>
    /// A package governs one build's test work. Folding in a change allocated elsewhere would put its
    /// procedures behind the wrong release gate.
    /// </summary>
    [Fact]
    public async Task A_change_allocated_to_another_build_is_refused()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        var fixture = await SeedAsync(factory);
        await LoginAsync(client, "scope.engineer");

        using var response = await client.PostAsJsonAsync(
            $"/api/test-change-reviews/{fixture.FirstReviewId}/change-requests", new { changeRequestId = fixture.OtherBuildChangeId });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("different build", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Deciding_what_a_package_covers_takes_verification_authority()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        var fixture = await SeedAsync(factory);
        await LoginAsync(client, "scope.outsider");

        using var response = await client.PostAsJsonAsync(
            $"/api/test-change-reviews/{fixture.FirstReviewId}/change-requests", new { changeRequestId = fixture.SecondChangeId });
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }
}
