using System.Net;
using System.Net.Http.Json;
using AeroLink.Domain.Identity;
using AeroLink.Domain.Programs;
using AeroLink.Domain.Requirements;
using AeroLink.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace AeroLink.Api.Tests;

/// <summary>
/// A released build takes no new change requests.
///
/// `retarget` has always refused to *move* a change request onto a released build. Nothing stopped one being
/// *created* there, so the product offered an action whose result was a change request with no future: it could
/// not reach a baseline, be incorporated, or be revised, because all three are things a build that has already
/// shipped will never do again.
///
/// It is also the likely mechanism behind a report of a draft that saved and then could not be found. Created
/// while the released build was selected it was allocated to that build, and the change request list the author
/// then looked at was filtered to the in-work one — so the record existed, was correct, and was nowhere the
/// author was looking.
///
/// Both creation endpoints are covered. They are separate code paths and the guard has to be on each.
/// </summary>
public sealed class ClosedReleaseAuthoringTests
{
    private static async Task<(Guid ProjectId, Guid ReleasedId, Guid InWorkId)> SeedAsync(AeroLinkApiFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        var now = DateTimeOffset.UtcNow;

        var program = new ProgramRecord("Closed Release Program", "CRP");
        var project = new ProjectRecord(program.Id, "Flight Software", "Closed Release Software");
        var released = new SoftwareRelease(project.Id, "1.5", true);
        var inWork = new SoftwareRelease(project.Id, "1.6", false, released.Id);
        db.AddRange(program, project, released, inWork);

        var account = new UserAccount("closed.author", "closed.author", "closed.author@example.test",
            IdentityService.HashPassword(AeroLinkApiFactory.MemberPassword), now);
        db.Add(account);
        db.Add(new ProgramMembership(account.Id, program.Id, ProgramRole.Engineer, "test.setup", now));
        await db.SaveChangesAsync();
        return (project.Id, released.Id, inWork.Id);
    }

    private static object Body(Guid projectId, Guid releaseId) => new
    {
        projectId,
        targetReleaseId = releaseId,
        title = "Oceanic waypoint sequencing",
        problem = "P",
        analysis = "A",
        solution = "S",
        type = "System",
        requirementChanges = new[]
        {
            new
            {
                baseNumber = "",
                revision = 0,
                level = "System",
                kind = "Introduce",
                statement = "The FMS shall sequence oceanic waypoints.",
                rationale = "New",
                verificationMethod = "Test",
            },
        },
    };

    [Theory]
    [InlineData("/api/scr-drafts")]
    [InlineData("/api/scrs")]
    public async Task A_released_build_refuses_a_new_change_request(string endpoint)
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        var (projectId, releasedId, _) = await SeedAsync(factory);
        await SignInAsync(client);

        using var response = await client.PostAsJsonAsync(endpoint, Body(projectId, releasedId));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        // Names the build and says where to go instead. "Bad request" alone leaves the author guessing which of
        // a dozen fields was wrong, when nothing they typed was.
        Assert.Contains("1.5 has been released", body);
        Assert.Contains("in-work build", body);
        Assert.Contains("release_is_closed", body);
    }

    /// <summary>The same request against the in-work build, to prove the guard refuses the build and not the payload.</summary>
    [Theory]
    [InlineData("/api/scr-drafts")]
    [InlineData("/api/scrs")]
    public async Task The_in_work_build_accepts_the_same_change_request(string endpoint)
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        var (projectId, _, inWorkId) = await SeedAsync(factory);
        await SignInAsync(client);

        using var response = await client.PostAsJsonAsync(endpoint, Body(projectId, inWorkId));
        var body = await response.Content.ReadAsStringAsync();
        // Asserted against the body rather than the status alone: when this refuses, the reason is in the body
        // and a bare status comparison throws it away.
        Assert.True(response.StatusCode == HttpStatusCode.Created, $"{(int)response.StatusCode}: {body}");
        Assert.Contains("\"state\":\"Draft\"", body);
    }

    private static async Task SignInAsync(HttpClient client)
    {
        var login = await client.PostAsJsonAsync("/api/auth/login",
            new { userName = "closed.author", password = AeroLinkApiFactory.MemberPassword });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        await SecurityBoundaryTests.AuthorizeMutationsAsync(client);
    }
}
