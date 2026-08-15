// #563 phase-2 pilot isolation proof: two tests sharing one SharedApiHost must own unique logical data and
// never see each other's data or act in each other's projects.
//
// xUnit runs the tests of a class serially, so both tests execute against the SAME host and database file.
// Each test seeds its own uniquely tagged Program, users, and projects; the assertions below fail if a
// seed collides with an earlier test (uniqueness constraints) or if project/program scoping leaks across
// the shared database.

using System.Net;
using System.Net.Http.Json;
using AeroLink.Domain.Identity;
using AeroLink.Domain.Programs;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AeroLink.Api.Tests;

public sealed class SharedHostIsolationTests : IClassFixture<SharedApiHost>
{
    private readonly SharedApiHost _host;

    // Cross-test reuse proof: xUnit runs a class's tests serially but does not guarantee their order, so
    // every fact records the fixture instance it saw. Once more than one fact has run, ALL observed
    // instance IDs must be equal: if SharedApiHost were silently recreated per test, the IDs would differ
    // and the last fact to run fails. A filtered single-test run observes one ID and cannot prove reuse,
    // but the two-client fact still proves session/project isolation within that one run.
    private static readonly List<Guid> ObservedInstanceIds = [];

    private void RecordObservedInstance()
    {
        lock (ObservedInstanceIds)
        {
            if (!ObservedInstanceIds.Contains(_host.InstanceId)) ObservedInstanceIds.Add(_host.InstanceId);
            if (ObservedInstanceIds.Count > 1)
            {
                Assert.Single(ObservedInstanceIds.Distinct());
            }
        }
    }

    public SharedHostIsolationTests(SharedApiHost host)
    {
        _host = host;
    }

    private sealed record Seeded(Guid HomeProjectId, Guid HomeProgramId, Guid ForeignProjectId, Guid MemberId,
        string MemberName, string OutsiderName);

    private static async Task<Seeded> SeedAsync(AeroLinkApiFactory factory, string tag)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        var now = DateTimeOffset.UtcNow;
        var memberName = $"iso.member.{tag}";
        var outsiderName = $"iso.outsider.{tag}";
        var homeProgram = new ProgramRecord($"Isolation Home {tag}", $"ISH{tag}");
        var foreignProgram = new ProgramRecord($"Isolation Foreign {tag}", $"ISF{tag}");
        var homeProject = new ProjectRecord(homeProgram.Id, "Home Software", "Isolation Home Software");
        var foreignProject = new ProjectRecord(foreignProgram.Id, "Foreign Software", "Isolation Foreign Software");

        UserAccount Account(string name) =>
            new(name, name, $"{name}@example.test", IdentityService.HashPassword(AeroLinkApiFactory.MemberPassword), now);
        var member = Account(memberName);
        var outsider = Account(outsiderName);

        db.AddRange(homeProgram, foreignProgram, homeProject, foreignProject, member, outsider,
            new ProgramMembership(member.Id, homeProgram.Id, ProgramRole.Engineer, "test.setup", now),
            new ProgramMembership(outsider.Id, foreignProgram.Id, ProgramRole.Engineer, "test.setup", now));
        await db.SaveChangesAsync();
        return new Seeded(homeProject.Id, homeProgram.Id, foreignProject.Id, member.Id, memberName, outsiderName);
    }

    private static async Task SignInAsync(HttpClient client, string userName)
    {
        using var login = await client.PostAsJsonAsync("/api/auth/login",
            new { userName, password = AeroLinkApiFactory.MemberPassword });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        await SecurityBoundaryTests.AuthorizeMutationsAsync(client);
    }

    [Fact]
    public async Task A_shared_host_test_seeds_unique_data_and_cannot_cross_a_program_boundary()
    {
        RecordObservedInstance();

        var tag = Guid.NewGuid().ToString("N")[..8];
        var seeded = await SeedAsync(_host.Factory, tag);

        using (var scope = _host.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            // Uniqueness constraints would have rejected a colliding seed; these counts prove the shared
            // database holds exactly one record for this test's identities, not a duplicate from an earlier test.
            Assert.Equal(1, await db.Programs.AsNoTracking().CountAsync(x => x.Code == $"ISH{tag}".ToUpperInvariant()));
            Assert.Equal(1, await db.UserAccounts.AsNoTracking().CountAsync(x => x.UserName == seeded.MemberName));
            Assert.Equal(1, await db.ProgramMemberships.AsNoTracking()
                .CountAsync(x => x.UserId == seeded.MemberId && x.ProgramId == seeded.HomeProgramId));
        }

        using var member = _host.CreateClient();
        await SignInAsync(member, seeded.MemberName);
        using var home = await member.GetAsync($"/api/projects/{seeded.HomeProjectId}/personnel");
        Assert.Equal(HttpStatusCode.OK, home.StatusCode);
        using var foreign = await member.GetAsync($"/api/projects/{seeded.ForeignProjectId}/personnel");
        Assert.Equal(HttpStatusCode.Forbidden, foreign.StatusCode);
    }

    [Fact]
    public async Task A_second_shared_host_test_owns_its_own_data_and_sessions()
    {
        RecordObservedInstance();

        var tag = Guid.NewGuid().ToString("N")[..8];
        var seeded = await SeedAsync(_host.Factory, tag);

        using (var scope = _host.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            Assert.Equal(1, await db.Programs.AsNoTracking().CountAsync(x => x.Code == $"ISH{tag}".ToUpperInvariant()));
            Assert.Equal(1, await db.UserAccounts.AsNoTracking().CountAsync(x => x.UserName == seeded.MemberName));
        }

        // A fresh client is a fresh session: the previous test's cookie container cannot carry over.
        using var outsider = _host.CreateClient();
        await SignInAsync(outsider, seeded.OutsiderName);
        using var refused = await outsider.GetAsync($"/api/projects/{seeded.HomeProjectId}/personnel");
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
    }

    [Fact]
    public async Task Two_clients_on_one_shared_host_do_not_leak_sessions_or_projects()
    {
        RecordObservedInstance();

        // One test, two logical scenarios and two clients on the SAME shared host: each login must stay in
        // its own client's cookie container, and each client must be refused the other's project.
        var tag = Guid.NewGuid().ToString("N")[..8];
        var seeded = await SeedAsync(_host.Factory, tag);

        using var memberClient = _host.CreateClient();
        await SignInAsync(memberClient, seeded.MemberName);
        using var memberHome = await memberClient.GetAsync($"/api/projects/{seeded.HomeProjectId}/personnel");
        Assert.Equal(HttpStatusCode.OK, memberHome.StatusCode);
        using var memberForeign = await memberClient.GetAsync($"/api/projects/{seeded.ForeignProjectId}/personnel");
        Assert.Equal(HttpStatusCode.Forbidden, memberForeign.StatusCode);

        using var outsiderClient = _host.CreateClient();
        using var unauthenticated = await outsiderClient.GetAsync($"/api/auth/me");
        Assert.Equal(HttpStatusCode.Unauthorized, unauthenticated.StatusCode);
        await SignInAsync(outsiderClient, seeded.OutsiderName);
        using var outsiderRefused = await outsiderClient.GetAsync($"/api/projects/{seeded.HomeProjectId}/personnel");
        Assert.Equal(HttpStatusCode.Forbidden, outsiderRefused.StatusCode);
        using var outsiderForeign = await outsiderClient.GetAsync($"/api/projects/{seeded.ForeignProjectId}/personnel");
        Assert.Equal(HttpStatusCode.OK, outsiderForeign.StatusCode);
    }
}
