using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Identity;
using AeroLink.Domain.Programs;
using AeroLink.Domain.Requirements;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AeroLink.Api.Tests;

/// <summary>
/// A build shows the work it was raised in as well as the work it is taking.
///
/// Deferring and then reinstating a change request into a later build moves its target. It is then neither
/// deferred nor targeting the build that raised it, so without the origin it would disappear from that build
/// entirely — a reader planning 1.6 would see work that simply vanished, with the move recorded only in audit
/// text a listing cannot be driven from.
/// </summary>
public sealed class BuildListingSpansOriginTests(SharedApiHost host) : IClassFixture<SharedApiHost>
{
    [Fact]
    public async Task A_change_request_moved_to_a_later_build_stays_visible_in_the_one_that_raised_it()
    {
        var world = await SeedAsync(host.Factory);

        // Raised in 1.6, shelved, then taken into 1.7.
        await using (var scope = host.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var scr = await db.SystemChangeRequests.Include(x => x.RequirementChanges)
                .SingleAsync(x => x.Id == world.ChangeRequestId);
            var now = DateTimeOffset.UtcNow;
            scr.Defer(world.Author, "Not shipping in 1.6.", now);
            scr.Reinstate(world.Author, now);
            scr.Retarget(world.Author, world.NextRelease, "Taken into 1.7.", now);
            await db.SaveChangesAsync();

            Assert.Equal(world.NextRelease, scr.TargetReleaseId);
            Assert.Equal(world.FirstRelease, scr.OriginReleaseId);
            Assert.Equal(ChangeRequestState.Draft, scr.State);
        }

        using var client = host.CreateClient();
        await LoginAsync(client, world.Author);

        foreach (var (release, which) in new[] { (world.FirstRelease, "the build that raised it"), (world.NextRelease, "the build taking it") })
        {
            using var response = await client.GetAsync(
                $"/api/history/change-requests?projectId={world.ProjectId}&releaseId={release}&page=1&pageSize=200");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            var items = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("items").EnumerateArray().ToList();
            Assert.True(items.Any(x => x.GetProperty("id").GetGuid() == world.ChangeRequestId),
                $"The change request should be listed by {which}.");
        }
    }

    [Fact]
    public async Task A_change_request_that_never_moved_is_listed_once_by_its_own_build()
    {
        var world = await SeedAsync(host.Factory);
        using var client = host.CreateClient();
        await LoginAsync(client, world.Author);

        using var mine = await client.GetAsync(
            $"/api/history/change-requests?projectId={world.ProjectId}&releaseId={world.FirstRelease}&page=1&pageSize=200");
        var listed = (await mine.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("items").EnumerateArray()
            .Count(x => x.GetProperty("id").GetGuid() == world.ChangeRequestId);
        Assert.Equal(1, listed);

        using var other = await client.GetAsync(
            $"/api/history/change-requests?projectId={world.ProjectId}&releaseId={world.NextRelease}&page=1&pageSize=200");
        Assert.DoesNotContain((await other.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("items").EnumerateArray(),
            x => x.GetProperty("id").GetGuid() == world.ChangeRequestId);
    }

    private sealed record World(Guid ProjectId, Guid FirstRelease, Guid NextRelease, Guid ChangeRequestId, string Author);

    private static async Task LoginAsync(HttpClient client, string userName)
    {
        using var response = await client.PostAsJsonAsync("/api/auth/login",
            new { userName, password = AeroLinkApiFactory.MemberPassword });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static async Task<World> SeedAsync(AeroLinkApiFactory factory)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        var now = DateTimeOffset.UtcNow;
        var tag = Guid.NewGuid().ToString("N")[..8];
        var author = $"origin.author.{tag}";

        var program = new ProgramRecord($"Origin Program {tag}", $"ORG{tag}");
        var project = new ProjectRecord(program.Id, "Software", "Origin Software");
        var sixteen = new SoftwareRelease(project.Id, "1.6", false);
        var seventeen = new SoftwareRelease(project.Id, "1.7", false);
        db.AddRange(program, project, sixteen, seventeen);

        var account = new UserAccount(author, author, $"{author}@example.test",
            IdentityService.HashPassword(AeroLinkApiFactory.MemberPassword), now);
        db.Add(account);
        db.Add(new ProgramMembership(account.Id, program.Id, ProgramRole.Engineer, "test.setup", now));

        var scr = new SystemChangeRequest("SRCR-00941", 0, project.Id, sixteen.Id,
            "Raised in 1.6", "P", "A", "S", author, now);
        scr.AddRequirementChange(author, "SYSR-00181", 2, RequirementLevel.System,
            RequirementChangeKind.Modify, "The system shall respond within 1.5 seconds.", "Latency", "Test", now);
        db.SystemChangeRequests.Add(scr);
        await db.SaveChangesAsync();

        return new World(project.Id, sixteen.Id, seventeen.Id, scr.Id, author);
    }
}
