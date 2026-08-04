using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Identity;
using AeroLink.Domain.Programs;
using AeroLink.Domain.Requirements;
using AeroLink.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace AeroLink.Api.Tests;

/// <summary>
/// The shelf, and the listing that stopped showing superseded revisions in the reader's way.
///
/// `Defer` had existed in the domain since the allocation states were reworked and nothing exposed it: the
/// dashboard counted deferred change requests, the history explorer offered a filter for them, and the only way
/// one could come into being was for the demonstration seeder to create it. The shelf was visible and
/// unreachable — which is the same class of defect as a Revise button gated on a state nothing rests in.
/// </summary>
public sealed class DeferralAndRevisionListingTests
{
    private static async Task<(Guid ProjectId, Guid ReleaseId, Guid ScrId)> SeedAsync(AeroLinkApiFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        var now = DateTimeOffset.UtcNow;

        var program = new ProgramRecord("Shelf Program", "SHP");
        var project = new ProjectRecord(program.Id, "Flight Software", "Shelf Software");
        var release = new SoftwareRelease(project.Id, "1.6", false);
        db.AddRange(program, project, release);

        foreach (var (name, role) in new[] { ("shelf.author", ProgramRole.Engineer), ("shelf.approver", ProgramRole.Approver) })
        {
            var account = new UserAccount(name, name, $"{name}@example.test",
                IdentityService.HashPassword(AeroLinkApiFactory.MemberPassword), now);
            db.Add(account);
            db.Add(new ProgramMembership(account.Id, program.Id, role, "test.setup", now));
        }
        await db.SaveChangesAsync();

        var scr = new SystemChangeRequest("SRCR-00070", 0, project.Id, release.Id,
            "Oceanic waypoint sequencing", "P", "A", "S", "shelf.author", now);
        scr.AddRequirementChange("shelf.author", "SYSR-00000701", 0, RequirementLevel.System,
            RequirementChangeKind.Introduce, "The FMS shall sequence oceanic waypoints.", "New", "Test", now);
        scr.SubmitForReview("shelf.author", [new("shelf.approver", "Shelf Approver")], now);
        scr.ApproveActiveStage("shelf.approver", now);
        db.SystemChangeRequests.Add(scr);
        await db.SaveChangesAsync();
        return (project.Id, release.Id, scr.Id);
    }

    private static async Task SignInAsync(HttpClient client, string userName = "shelf.author")
    {
        var login = await client.PostAsJsonAsync("/api/auth/login",
            new { userName, password = AeroLinkApiFactory.MemberPassword });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        await SecurityBoundaryTests.AuthorizeMutationsAsync(client);
    }

    [Fact]
    public async Task An_approved_change_request_goes_on_the_shelf_and_comes_back_where_it_was()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        var (_, _, scrId) = await SeedAsync(factory);
        await SignInAsync(client);

        using var deferred = await client.PostAsJsonAsync($"/api/change-requests/{scrId}/defer",
            new { reason = "Correct, but not shipping in 1.6." });
        var body = await deferred.Content.ReadAsStringAsync();
        Assert.True(deferred.StatusCode == HttpStatusCode.OK, $"{(int)deferred.StatusCode}: {body}");
        // Both facts survive the round trip: where it sits, and how far it got.
        Assert.Contains("\"state\":\"Deferred\"", body);
        Assert.Contains("\"deferredFromState\":\"Approved\"", body);

        using var reinstated = await client.PostAsync($"/api/change-requests/{scrId}/reinstate", null);
        var back = await reinstated.Content.ReadAsStringAsync();
        Assert.True(reinstated.StatusCode == HttpStatusCode.OK, $"{(int)reinstated.StatusCode}: {back}");
        Assert.Contains("\"state\":\"Approved\"", back);
        Assert.Contains("\"deferredFromState\":null", back);
    }

    [Fact]
    public async Task Deferring_without_a_reason_is_refused()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        var (_, _, scrId) = await SeedAsync(factory);
        await SignInAsync(client);

        // A shelf whose entries do not say why they are on it is a shelf nobody can plan from.
        using var response = await client.PostAsJsonAsync($"/api/change-requests/{scrId}/defer", new { reason = "   " });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("reason", await response.Content.ReadAsStringAsync());
    }

    /// <summary>
    /// One row per change request, not per revision — and the earlier revisions still reachable.
    ///
    /// A superseded revision is the same piece of work read at an earlier moment. Listed beside its successor it
    /// puts a stale copy in the reader's way, and on a page of fifty it is not obvious which of the two is
    /// current. Collapsing without a way to expand would be hiding the record, so the row carries its revision
    /// count and `baseNumber` asks for the history behind it.
    /// </summary>
    [Fact]
    public async Task The_listing_collapses_to_the_newest_revision_and_expands_on_request()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        var (projectId, _, scrId) = await SeedAsync(factory);
        await SignInAsync(client);

        using var revised = await client.PostAsJsonAsync($"/api/change-requests/{scrId}/next-revision", new { });
        Assert.Equal(HttpStatusCode.Created, revised.StatusCode);

        using var collapsed = await client.GetAsync($"/api/history/change-requests?projectId={projectId}&page=1&pageSize=50");
        using var page = JsonDocument.Parse(await collapsed.Content.ReadAsStringAsync());
        var rows = page.RootElement.GetProperty("items").EnumerateArray().ToList();
        var row = Assert.Single(rows);
        Assert.Equal("SRCR-00070.01", row.GetProperty("displayNumber").GetString());
        // The count is what lets the row offer its history without a request per row.
        Assert.Equal(2, row.GetProperty("revisionCount").GetInt32());
        Assert.Equal(1, page.RootElement.GetProperty("totalCount").GetInt32());

        using var expanded = await client.GetAsync(
            $"/api/history/change-requests?projectId={projectId}&baseNumber=SRCR-00070&page=1&pageSize=50");
        using var all = JsonDocument.Parse(await expanded.Content.ReadAsStringAsync());
        var numbers = all.RootElement.GetProperty("items").EnumerateArray()
            .Select(x => x.GetProperty("displayNumber").GetString()).OrderBy(x => x).ToList();
        Assert.Equal(["SRCR-00070.00", "SRCR-00070.01"], numbers);
    }
}
