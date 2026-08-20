using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Identity;
using AeroLink.Domain.Programs;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AeroLink.Api.Tests;

/// <summary>
/// Work shelved by one build comes with the build that follows it.
///
/// Deferring means "put away for another day with the state it had reached remembered", and the next build is
/// exactly the day it should come back and be considered. Listing strictly by target build meant a change
/// request deferred in 1.6 vanished the moment 1.7 opened, and the only route back to it was to navigate into
/// the build that shelved it — so the shelf was where work went to be forgotten rather than to wait.
/// </summary>
public sealed class DeferredCarryForwardApiTests
{
    private sealed record Seeded(Guid ProjectId, Guid ReleasedId, Guid CurrentId, Guid SuccessorId, Guid DeferredId, Guid ActiveId);

    private static async Task<Seeded> SeedAsync(AeroLinkApiFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        var now = DateTimeOffset.UtcNow;
        var program = new ProgramRecord("Deferral Program", "DEF");
        var project = new ProjectRecord(program.Id, "FMS Product Development", "Flight Management System");
        var released = new SoftwareRelease(project.Id, "1.5", true);
        var current = new SoftwareRelease(project.Id, "1.6", false, released.Id);
        var successor = new SoftwareRelease(project.Id, "1.7", false, current.Id);

        var deferred = new SystemChangeRequest("SRCR-60001", 0, project.Id, current.Id,
            "SHELVED-IN-ONE-SIX oceanic sequencing", "P", "A", "S", "defer.user", now, ChangeRequestType.System);
        deferred.Defer("defer.user", "Shelved for a later build.", now, true);
        var active = new SystemChangeRequest("SRCR-60002", 0, project.Id, current.Id,
            "STILL-IN-ONE-SIX active work", "P", "A", "S", "defer.user", now, ChangeRequestType.System);

        var user = new UserAccount("defer.user", "Deferral User", "defer.user@example.test",
            IdentityService.HashPassword(AeroLinkApiFactory.MemberPassword), now);
        db.AddRange(program, project, released, current, successor, deferred, active, user);
        db.ProgramMemberships.Add(new ProgramMembership(user.Id, program.Id, ProgramRole.Engineer, "test.setup", now));
        db.ProgramMemberships.Add(new ProgramMembership(user.Id, program.Id, ProgramRole.ConfigurationManager, "test.setup", now));
        await db.SaveChangesAsync();
        return new(project.Id, released.Id, current.Id, successor.Id, deferred.Id, active.Id);
    }

    /// <summary>
    /// Shelved work is offered to the successor as a backlog, not as part of its plan.
    ///
    /// This test asserted the opposite: that a deferred change request appeared inline in the successor's own
    /// list. That answered the right problem — shelved work was unreachable from the build that followed — with
    /// the wrong shape, because a build's list is then read as though it already contained work nobody has
    /// committed to. The backlog is its own listing, and bringing one in is the explicit act that moves it.
    /// </summary>
    [Fact]
    public async Task A_change_request_deferred_in_one_build_is_offered_to_its_successor()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        var seeded = await SeedAsync(factory);
        await SignInAsync(client);

        var successor = await client.GetFromJsonAsync<JsonElement>(
            $"/api/change-requests?projectId={seeded.ProjectId}&releaseId={seeded.SuccessorId}");
        var listed = successor.GetProperty("items").EnumerateArray().ToList();

        // Neither the shelved work nor the live work of another build belongs to this one.
        Assert.DoesNotContain(listed, x => x.GetProperty("title").GetString() == "SHELVED-IN-ONE-SIX oceanic sequencing");
        Assert.DoesNotContain(listed, x => x.GetProperty("title").GetString() == "STILL-IN-ONE-SIX active work");

        // The backlog offers it, saying which build shelved it and how far it had got, so a reader deciding
        // whether to take it on is not guessing.
        var backlog = await client.GetFromJsonAsync<JsonElement>(
            $"/api/change-requests/deferred?projectId={seeded.ProjectId}");
        var offered = Assert.Single(backlog.GetProperty("items").EnumerateArray()
            .Where(x => x.GetProperty("title").GetString() == "SHELVED-IN-ONE-SIX oceanic sequencing").ToList());
        Assert.Equal(seeded.CurrentId, offered.GetProperty("shelvedFromReleaseId").GetGuid());

        // And the build that shelved it still lists it, unchanged.
        var origin = await client.GetFromJsonAsync<JsonElement>(
            $"/api/change-requests?projectId={seeded.ProjectId}&releaseId={seeded.CurrentId}");
        Assert.Contains(origin.GetProperty("items").EnumerateArray(),
            x => x.GetProperty("title").GetString() == "SHELVED-IN-ONE-SIX oceanic sequencing");
    }

    [Fact]
    public async Task Reinstating_from_the_successor_moves_the_change_request_into_that_build()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        var seeded = await SeedAsync(factory);
        await SignInAsync(client);

        using var reinstated = await client.PostAsJsonAsync(
            $"/api/change-requests/{seeded.DeferredId}/reinstate", new { intoReleaseId = seeded.SuccessorId });
        var body = await reinstated.Content.ReadAsStringAsync();
        Assert.True(reinstated.StatusCode == HttpStatusCode.OK, $"Expected OK, got {(int)reinstated.StatusCode}: {body}");

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        var record = await db.SystemChangeRequests.AsNoTracking().SingleAsync(x => x.Id == seeded.DeferredId);
        Assert.Equal(seeded.SuccessorId, record.TargetReleaseId);
        Assert.Equal(ChangeRequestState.Draft, record.State);
        Assert.Null(record.DeferredFromState);

        // Still one record, not a copy — but the build that raised it keeps listing it, now as work that has
        // moved on rather than as work in flight. #320 asserted the opposite, on the reasoning that the record
        // had moved and belonged to the successor. It does belong there, and a reader planning 1.6 still needs
        // to know the work existed and where it went; without this it simply vanished from their build.
        var origin = await client.GetFromJsonAsync<JsonElement>(
            $"/api/change-requests?projectId={seeded.ProjectId}&releaseId={seeded.CurrentId}");
        var listedByOrigin = origin.GetProperty("items").EnumerateArray()
            .Where(x => x.GetProperty("title").GetString() == "SHELVED-IN-ONE-SIX oceanic sequencing").ToList();
        Assert.Single(listedByOrigin);
        Assert.Equal(seeded.SuccessorId, listedByOrigin[0].GetProperty("targetReleaseId").GetGuid());
    }

    [Fact]
    public async Task Reinstating_into_a_released_build_is_refused()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        var seeded = await SeedAsync(factory);
        await SignInAsync(client);

        using var refused = await client.PostAsJsonAsync(
            $"/api/change-requests/{seeded.DeferredId}/reinstate", new { intoReleaseId = seeded.ReleasedId });

        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Contains("released_build_read_only", await refused.Content.ReadAsStringAsync());

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        var record = await db.SystemChangeRequests.AsNoTracking().SingleAsync(x => x.Id == seeded.DeferredId);
        Assert.Equal(ChangeRequestState.Deferred, record.State);
        Assert.Equal(seeded.CurrentId, record.TargetReleaseId);
    }

    /// Reinstating without naming a build keeps the old behaviour: back into the build that shelved it.
    [Fact]
    public async Task Reinstating_without_a_target_returns_it_to_the_build_that_shelved_it()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        var seeded = await SeedAsync(factory);
        await SignInAsync(client);

        using var reinstated = await client.PostAsJsonAsync($"/api/change-requests/{seeded.DeferredId}/reinstate", new { });
        Assert.Equal(HttpStatusCode.OK, reinstated.StatusCode);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        var record = await db.SystemChangeRequests.AsNoTracking().SingleAsync(x => x.Id == seeded.DeferredId);
        Assert.Equal(seeded.CurrentId, record.TargetReleaseId);
        Assert.Equal(ChangeRequestState.Draft, record.State);
    }

    private static async Task SignInAsync(HttpClient client)
    {
        using var response = await client.PostAsJsonAsync("/api/auth/login",
            new { userName = "defer.user", password = AeroLinkApiFactory.MemberPassword });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
