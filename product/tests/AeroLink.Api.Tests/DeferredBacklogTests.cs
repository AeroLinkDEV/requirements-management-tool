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
/// The deferred backlog: everything still on the shelf, and not part of any build until somebody takes it.
///
/// A build's own list is the work it is taking and the work it raised. Deferred work from an earlier build is
/// deliberately absent from it — it is a backlog to consider, and mixing it in makes the plan for the build
/// read as though it already contained work nobody has committed to.
///
/// This reverses #320, which surfaced a predecessor's deferred work inline in the successor's list. That
/// answered the right problem with the wrong shape.
/// </summary>
public sealed class DeferredBacklogTests(SharedApiHost host) : IClassFixture<SharedApiHost>
{
    [Fact]
    public async Task Deferred_work_is_not_in_the_next_build_list_but_is_in_the_backlog()
    {
        var world = await SeedAsync(host.Factory);
        using var client = host.CreateClient();
        await LoginAsync(client, world.Author);

        // Build 1.7 does not carry it.
        var seventeen = await client.GetFromJsonAsync<JsonElement>(
            $"/api/change-requests?projectId={world.ProjectId}&releaseId={world.Seventeen}");
        Assert.DoesNotContain(seventeen.GetProperty("items").EnumerateArray(),
            x => x.GetProperty("id").GetGuid() == world.DeferredId);

        // The build that shelved it still shows it, as deferred.
        var sixteen = await client.GetFromJsonAsync<JsonElement>(
            $"/api/change-requests?projectId={world.ProjectId}&releaseId={world.Sixteen}");
        var inSixteen = Assert.Single(sixteen.GetProperty("items").EnumerateArray()
            .Where(x => x.GetProperty("id").GetGuid() == world.DeferredId).ToList());
        Assert.Equal("Deferred", inSixteen.GetProperty("state").GetString());

        // And the backlog offers it.
        var backlog = await client.GetFromJsonAsync<JsonElement>(
            $"/api/change-requests/deferred?projectId={world.ProjectId}");
        var offered = Assert.Single(backlog.GetProperty("items").EnumerateArray()
            .Where(x => x.GetProperty("id").GetGuid() == world.DeferredId).ToList());
        Assert.Equal(world.Sixteen, offered.GetProperty("shelvedFromReleaseId").GetGuid());
        Assert.Equal("Draft", offered.GetProperty("deferredFromState").GetString());
    }

    /// <summary>
    /// However long ago it was shelved. #320 looked one build back, so work deferred in 1.4 and never taken up
    /// would quietly disappear by 1.7 — which is exactly the work somebody planning 1.7 needs to see.
    /// </summary>
    [Fact]
    public async Task The_backlog_reaches_further_back_than_the_previous_build()
    {
        var world = await SeedAsync(host.Factory, alsoDeferInFourteen: true);
        using var client = host.CreateClient();
        await LoginAsync(client, world.Author);

        var backlog = await client.GetFromJsonAsync<JsonElement>(
            $"/api/change-requests/deferred?projectId={world.ProjectId}");
        var ids = backlog.GetProperty("items").EnumerateArray().Select(x => x.GetProperty("id").GetGuid()).ToList();

        Assert.Contains(world.DeferredId, ids);
        Assert.Contains(world.OldDeferredId!.Value, ids);
    }

    [Fact]
    public async Task Bringing_one_in_puts_it_in_that_build_as_a_draft_and_takes_it_off_the_backlog()
    {
        var world = await SeedAsync(host.Factory);
        using var client = host.CreateClient();
        await LoginAsync(client, world.Author);

        using var brought = await client.PostAsJsonAsync(
            $"/api/change-requests/{world.DeferredId}/reinstate", new { intoReleaseId = world.Seventeen });
        Assert.Equal(HttpStatusCode.OK, brought.StatusCode);

        var seventeen = await client.GetFromJsonAsync<JsonElement>(
            $"/api/change-requests?projectId={world.ProjectId}&releaseId={world.Seventeen}");
        var landed = Assert.Single(seventeen.GetProperty("items").EnumerateArray()
            .Where(x => x.GetProperty("id").GetGuid() == world.DeferredId).ToList());
        Assert.Equal("Draft", landed.GetProperty("state").GetString());

        var backlog = await client.GetFromJsonAsync<JsonElement>(
            $"/api/change-requests/deferred?projectId={world.ProjectId}");
        Assert.DoesNotContain(backlog.GetProperty("items").EnumerateArray(),
            x => x.GetProperty("id").GetGuid() == world.DeferredId);

        // Still visible in the build that raised it, because that is where the work began.
        var sixteen = await client.GetFromJsonAsync<JsonElement>(
            $"/api/change-requests?projectId={world.ProjectId}&releaseId={world.Sixteen}");
        Assert.Contains(sixteen.GetProperty("items").EnumerateArray(),
            x => x.GetProperty("id").GetGuid() == world.DeferredId);
    }

    [Fact]
    public async Task The_backlog_is_scoped_to_the_register_it_is_read_from()
    {
        var world = await SeedAsync(host.Factory);
        using var client = host.CreateClient();
        await LoginAsync(client, world.Author);

        var system = await client.GetFromJsonAsync<JsonElement>(
            $"/api/change-requests/deferred?projectId={world.ProjectId}&type=System");
        Assert.Contains(system.GetProperty("items").EnumerateArray(),
            x => x.GetProperty("id").GetGuid() == world.DeferredId);

        var software = await client.GetFromJsonAsync<JsonElement>(
            $"/api/change-requests/deferred?projectId={world.ProjectId}&type=Software");
        Assert.DoesNotContain(software.GetProperty("items").EnumerateArray(),
            x => x.GetProperty("id").GetGuid() == world.DeferredId);
    }

    private sealed record World(Guid ProjectId, Guid Sixteen, Guid Seventeen, Guid DeferredId,
        Guid? OldDeferredId, string Author);

    private static async Task LoginAsync(HttpClient client, string userName)
    {
        using var response = await client.PostAsJsonAsync("/api/auth/login",
            new { userName, password = AeroLinkApiFactory.MemberPassword });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static async Task<World> SeedAsync(AeroLinkApiFactory factory, bool alsoDeferInFourteen = false)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        var now = DateTimeOffset.UtcNow;
        var tag = Guid.NewGuid().ToString("N")[..8];
        var author = $"backlog.author.{tag}";

        var program = new ProgramRecord($"Backlog Program {tag}", $"BKL{tag}");
        var project = new ProjectRecord(program.Id, "Software", "Backlog Software");
        var fourteen = new SoftwareRelease(project.Id, "1.4", false);
        var sixteen = new SoftwareRelease(project.Id, "1.6", false);
        var seventeen = new SoftwareRelease(project.Id, "1.7", false);
        db.AddRange(program, project, fourteen, sixteen, seventeen);

        var account = new UserAccount(author, author, $"{author}@example.test",
            IdentityService.HashPassword(AeroLinkApiFactory.MemberPassword), now);
        db.Add(account);
        db.Add(new ProgramMembership(account.Id, program.Id, ProgramRole.Engineer, "test.setup", now));

        var deferred = Shelved("SRCR-00951", sixteen.Id);
        db.SystemChangeRequests.Add(deferred);

        Guid? older = null;
        if (alsoDeferInFourteen)
        {
            var ancient = Shelved("SRCR-00952", fourteen.Id);
            db.SystemChangeRequests.Add(ancient);
            older = ancient.Id;
        }

        await db.SaveChangesAsync();
        return new World(project.Id, sixteen.Id, seventeen.Id, deferred.Id, older, author);

        SystemChangeRequest Shelved(string number, Guid releaseId)
        {
            var scr = new SystemChangeRequest(number, 0, project.Id, releaseId,
                "Shelved work", "P", "A", "S", author, now);
            scr.AddRequirementChange(author, "SYSR-00191", 2, RequirementLevel.System,
                RequirementChangeKind.Modify, "The system shall respond within 1.5 seconds.", "Latency", "Test", now);
            scr.Defer(author, "Not shipping in this build.", now);
            return scr;
        }
    }
}
