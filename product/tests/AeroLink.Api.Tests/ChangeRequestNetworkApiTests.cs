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
/// The build-scoped change network. Membership is by exact target build rather than by reachability from a
/// root, which is the whole difference from the rooted trace: an isolated change still belongs to its build.
/// </summary>
public sealed class ChangeRequestNetworkApiTests : IClassFixture<SharedApiHost>
{
    private readonly SharedApiHost _host;
    public ChangeRequestNetworkApiTests(SharedApiHost host) => _host = host;

    private sealed record Fixture(Guid ProjectId, Guid CurrentReleaseId, Guid OtherReleaseId,
        Guid ParentId, Guid ChildId, Guid IsolatedId, Guid OtherBuildId, Guid ProblemReportId,
        string Member, string Outsider);

    private static async Task<Fixture> SeedAsync(AeroLinkApiFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        var now = DateTimeOffset.UtcNow;
        var tag = Guid.NewGuid().ToString("N")[..8];
        var memberName = $"network.member.{tag}";
        var outsiderName = $"network.outsider.{tag}";
        var program = new ProgramRecord($"CR Network {tag}", $"CN{tag}");
        var project = new ProjectRecord(program.Id, "Flight controls", "Network qualification");
        var currentRelease = new SoftwareRelease(project.Id, "2.1", false);
        var otherRelease = new SoftwareRelease(project.Id, "2.2", false, currentRelease.Id);
        var member = new UserAccount(memberName, memberName, $"{memberName}@example.test",
            IdentityService.HashPassword(AeroLinkApiFactory.MemberPassword), now);
        var outsider = new UserAccount(outsiderName, outsiderName, $"{outsiderName}@example.test",
            IdentityService.HashPassword(AeroLinkApiFactory.MemberPassword), now);
        db.AddRange(program, project, currentRelease, otherRelease, member, outsider,
            new ProgramMembership(member.Id, program.Id, ProgramRole.Engineer, "test.setup", now));

        SystemChangeRequest Change(string number, SoftwareRelease release, string title)
        {
            var change = new SystemChangeRequest(number, 0, project.Id, release.Id, title,
                "Problem", "Analysis", "Solution", memberName, now);
            db.Add(change);
            return change;
        }

        var parent = Change("SRCR-90001", currentRelease, "Parent change");
        var child = Change("SRCR-90002", currentRelease, "Child change");
        var isolated = Change("SRCR-90003", currentRelease, "Isolated change");
        var otherBuild = Change("SRCR-90004", otherRelease, "Different build change");

        db.Add(new ChangeRequestUpstreamLink(child.Id, parent.Id, "SRCR-90001.00",
            currentRelease.Id, "2.1", "Derived from the parent.", memberName, now));

        var report = new ProblemReport(project.Id, "PR-90001", "Sequencing defect", "Observed problem",
            "Analysis", memberName, now, targetReleaseId: currentRelease.Id);
        db.Add(report);
        db.Add(new ProblemReportLink(report.Id, "ChangeRequest", parent.Id, "ResolvedBy", memberName, now));

        await db.SaveChangesAsync();
        return new(project.Id, currentRelease.Id, otherRelease.Id, parent.Id, child.Id, isolated.Id,
            otherBuild.Id, report.Id, memberName, outsiderName);
    }

    private static async Task SignInAsync(HttpClient client, string userName)
    {
        using var login = await client.PostAsJsonAsync("/api/auth/login",
            new { userName, password = AeroLinkApiFactory.MemberPassword });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
    }

    private static async Task<JsonElement> NetworkAsync(HttpClient client, Fixture fixture, int? maxNodes = null)
    {
        var query = $"/api/change-requests/network?projectId={fixture.ProjectId}&releaseId={fixture.CurrentReleaseId}"
            + (maxNodes is null ? "" : $"&maxNodes={maxNodes}");
        using var response = await client.GetAsync(query);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
    }

    private static IReadOnlyList<string> NodeIds(JsonElement body) =>
        body.GetProperty("nodes").EnumerateArray().Select(x => x.GetProperty("id").GetString()!).ToList();

    [Fact]
    public async Task Network_carries_every_change_in_the_build_including_one_with_no_relations()
    {
        var fixture = await SeedAsync(_host.Factory);
        using var client = _host.CreateClient();
        await SignInAsync(client, fixture.Member);

        var body = await NetworkAsync(client, fixture);
        var ids = NodeIds(body);

        Assert.Contains(fixture.ParentId.ToString(), ids);
        Assert.Contains(fixture.ChildId.ToString(), ids);
        // Membership, not reachability. The rooted trace would never surface this one.
        Assert.Contains(fixture.IsolatedId.ToString(), ids);
        Assert.False(body.GetProperty("truncated").GetBoolean());
    }

    [Fact]
    public async Task Network_states_suspect_on_every_edge_so_the_client_never_reads_it_out_of_wording()
    {
        var fixture = await SeedAsync(_host.Factory);
        using var client = _host.CreateClient();
        await SignInAsync(client, fixture.Member);

        var edges = (await NetworkAsync(client, fixture)).GetProperty("edges").EnumerateArray().ToList();

        Assert.NotEmpty(edges);
        foreach (var edge in edges)
        {
            // Present on every edge, not only on suspect ones. A field that appears only when true forces the
            // reader to treat absence as a value, which is how a client ends up inferring the state instead of
            // being told it. These fixture edges are settled, so each says so explicitly.
            Assert.True(edge.TryGetProperty("isSuspect", out var isSuspect),
                "Every projected edge must state its suspect status.");
            Assert.Equal(JsonValueKind.False, isSuspect.ValueKind);
        }
    }

    [Fact]
    public async Task Network_states_the_projects_configured_ladder_so_the_client_never_orders_levels()
    {
        var fixture = await SeedAsync(_host.Factory);
        using var client = _host.CreateClient();
        await SignInAsync(client, fixture.Member);

        var levels = (await NetworkAsync(client, fixture)).GetProperty("orderedLevels")
            .EnumerateArray().Select(x => x.GetString()).ToList();

        // Layers above System are configured per Project, so their order is the ladder policy's to state and
        // not something a consumer may assume. System must derive into the software levels, in that order.
        Assert.NotEmpty(levels);
        Assert.Contains("System", levels);
        Assert.True(levels.IndexOf("System") < levels.IndexOf("HighLevel"));
        Assert.True(levels.IndexOf("HighLevel") < levels.IndexOf("LowLevel"));
    }

    [Fact]
    public async Task Network_excludes_a_change_targeting_a_different_build_in_the_same_project()
    {
        var fixture = await SeedAsync(_host.Factory);
        using var client = _host.CreateClient();
        await SignInAsync(client, fixture.Member);

        var ids = NodeIds(await NetworkAsync(client, fixture));

        Assert.DoesNotContain(fixture.OtherBuildId.ToString(), ids);
    }

    [Fact]
    public async Task Network_carries_the_typed_relation_between_two_changes_in_the_build()
    {
        var fixture = await SeedAsync(_host.Factory);
        using var client = _host.CreateClient();
        await SignInAsync(client, fixture.Member);

        var body = await NetworkAsync(client, fixture);
        var edge = body.GetProperty("edges").EnumerateArray().Single(x =>
            x.GetProperty("fromId").GetString() == fixture.ChildId.ToString()
            && x.GetProperty("toId").GetString() == fixture.ParentId.ToString());

        Assert.Equal("ChangeRequest", edge.GetProperty("fromKind").GetString());
        Assert.Equal("Upstream", edge.GetProperty("relation").GetString());
        Assert.Contains(edge.GetProperty("provenance").EnumerateArray(),
            x => x.GetProperty("kind").GetString() == "AuthorStated");
    }

    [Fact]
    public async Task Network_carries_a_problem_report_feeding_a_change_in_the_build()
    {
        var fixture = await SeedAsync(_host.Factory);
        using var client = _host.CreateClient();
        await SignInAsync(client, fixture.Member);

        var body = await NetworkAsync(client, fixture);
        var report = body.GetProperty("nodes").EnumerateArray()
            .Single(x => x.GetProperty("id").GetString() == fixture.ProblemReportId.ToString());
        Assert.Equal("ProblemReport", report.GetProperty("kind").GetString());

        var edge = body.GetProperty("edges").EnumerateArray().Single(x =>
            x.GetProperty("fromId").GetString() == fixture.ProblemReportId.ToString());
        Assert.Equal("ProblemReport", edge.GetProperty("fromKind").GetString());
        Assert.Equal(fixture.ParentId.ToString(), edge.GetProperty("toId").GetString());
        Assert.Equal("ProblemReportResolution", edge.GetProperty("relation").GetString());
    }

    [Fact]
    public async Task Network_declares_truncation_and_drops_edges_to_records_it_cut()
    {
        var fixture = await SeedAsync(_host.Factory);
        using var client = _host.CreateClient();
        await SignInAsync(client, fixture.Member);

        var body = await NetworkAsync(client, fixture, maxNodes: 1);

        Assert.True(body.GetProperty("truncated").GetBoolean());
        var ids = NodeIds(body);
        Assert.Single(ids);
        // A cut record takes its edges with it; a surviving edge would assert a relationship to something
        // the response does not contain.
        Assert.All(body.GetProperty("edges").EnumerateArray(), edge =>
        {
            Assert.Contains(edge.GetProperty("fromId").GetString(), ids);
            Assert.Contains(edge.GetProperty("toId").GetString(), ids);
        });
    }

    [Fact]
    public async Task Network_refuses_a_project_the_caller_cannot_see_and_a_release_outside_it()
    {
        var fixture = await SeedAsync(_host.Factory);

        using var outsider = _host.CreateClient();
        await SignInAsync(outsider, fixture.Outsider);
        using var forbidden = await outsider.GetAsync(
            $"/api/change-requests/network?projectId={fixture.ProjectId}&releaseId={fixture.CurrentReleaseId}");
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);

        using var client = _host.CreateClient();
        await SignInAsync(client, fixture.Member);
        using var missing = await client.GetAsync(
            $"/api/change-requests/network?projectId={fixture.ProjectId}&releaseId={Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }
}
