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
/// Raising a test change request deliberately, alongside the ones raised automatically.
///
/// One appears whenever a change request is approved, so nothing goes unnoticed. That is not the only way
/// the work arrives: a verification engineer may decide a set of changes is best tested as a single package
/// of their own making, and the only way to express that was previously to let the automatic packages appear
/// and then fold them together.
/// </summary>
public sealed class ManualTestChangeRequestApiTests
{
    private sealed record Fixture(Guid ProjectId, Guid ReleaseId, Guid FirstChangeId, Guid SecondChangeId,
        Guid AutoRaisedChangeId, Guid AutoTcrId, Guid OtherBuildChangeId, Guid ProblemReportId,
        Guid OtherBuildProblemReportId);

    private static async Task<Fixture> SeedAsync(AeroLinkApiFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        var impact = scope.ServiceProvider.GetRequiredService<VerificationImpactService>();
        var now = DateTimeOffset.UtcNow;

        var program = new ProgramRecord("Manual Program", "MAN");
        var project = new ProjectRecord(program.Id, "Software", "Manual Software");
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

        var first = Approved("SCR-00910", "SYSR-00000911", release.Id);
        var second = Approved("SCR-00911", "SYSR-00000912", release.Id);
        var autoRaised = Approved("SCR-00912", "SYSR-00000913", release.Id);
        var elsewhere = Approved("SCR-00913", "SYSR-00000914", otherBuild.Id);
        var report = new ProblemReport(project.Id, "PR-00910", "Route sequencing disagreement",
            "The observed route differs from the approved plan.", "", "quality", now);
        var otherReport = new ProblemReport(project.Id, "PR-00911", "Future-build problem",
            "This problem belongs to another build.", "", "quality", now);
        db.AddRange(first, second, autoRaised, elsewhere, report, otherReport,
            new ProblemReportLink(report.Id, "Release", release.Id, "BuildScope", "quality", now),
            new ProblemReportLink(otherReport.Id, "Release", otherBuild.Id, "BuildScope", "quality", now));

        foreach (var (user, role) in new[]
                 {
                     ("manual.engineer", ProgramRole.TestEngineer),
                     ("manual.outsider", ProgramRole.Engineer),
                 })
        {
            var account = new UserAccount(user, user, $"{user}@example.test",
                IdentityService.HashPassword(AeroLinkApiFactory.MemberPassword), now);
            db.Add(account);
            db.Add(new ProgramMembership(account.Id, program.Id, role, "test.setup", now));
        }
        await db.SaveChangesAsync();

        // Only this one gets an automatic package, so the others are genuinely unclaimed.
        var tracked = await db.SystemChangeRequests.Include(x => x.RequirementChanges).SingleAsync(x => x.Id == autoRaised.Id);
        await impact.RaiseForApprovedChangeRequestAsync(tracked, now, default);
        await db.SaveChangesAsync();
        var autoTcrId = await db.TestChangeReviews.Where(x => x.ChangeRequestId == autoRaised.Id)
            .Select(x => x.Id).SingleAsync();

        return new(project.Id, release.Id, first.Id, second.Id, autoRaised.Id, autoTcrId, elsewhere.Id,
            report.Id, otherReport.Id);
    }

    private static async Task LoginAsync(HttpClient client, string user)
    {
        using var login = await client.PostAsJsonAsync("/api/auth/login",
            new { userName = user, password = AeroLinkApiFactory.MemberPassword });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        await SecurityBoundaryTests.AuthorizeMutationsAsync(client);
    }

    [Fact]
    public async Task An_engineer_raises_a_package_covering_two_changes_at_once()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        var fixture = await SeedAsync(factory);
        await LoginAsync(client, "manual.engineer");

        using var response = await client.PostAsJsonAsync($"/api/releases/{fixture.ReleaseId}/test-change-requests",
            new { discipline = "System", changeRequestIds = new[] { fixture.FirstChangeId, fixture.SecondChangeId },
                problemReportIds = new[] { fixture.ProblemReportId } });
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.Created, $"{(int)response.StatusCode}: {body}");

        var created = JsonSerializer.Deserialize<JsonElement>(body);
        // Numbered like any other controlled package, not marked out as hand-made.
        Assert.Matches(@"^SYSTCR-\d{6}\.\d{2}$", created.GetProperty("displayNumber").GetString()!);
        Assert.Equal(2, created.GetProperty("covered").EnumerateArray().Count());
        var list = await client.GetFromJsonAsync<JsonElement>($"/api/releases/{fixture.ReleaseId}/test-change-reviews");
        var package = Assert.Single(list.EnumerateArray(), x => x.GetProperty("id").GetGuid() == created.GetProperty("id").GetGuid());
        Assert.Equal("PR-00910.00", Assert.Single(package.GetProperty("problemReports").EnumerateArray()).GetProperty("displayNumber").GetString());
    }

    [Fact]
    public async Task A_package_has_to_answer_for_something()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        var fixture = await SeedAsync(factory);
        await LoginAsync(client, "manual.engineer");

        // A package covering nothing has nothing to decide and would sit in the queue looking like work.
        using var response = await client.PostAsJsonAsync($"/api/releases/{fixture.ReleaseId}/test-change-requests",
            new { discipline = "System", changeRequestIds = Array.Empty<Guid>() });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    /// <summary>
    /// The automatic package already covers it. Two packages answering for one change could be approved with
    /// contradictory procedure decisions, and nothing would notice.
    /// </summary>
    [Fact]
    public async Task A_change_already_covered_is_refused_by_name()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        var fixture = await SeedAsync(factory);
        await LoginAsync(client, "manual.engineer");

        using var response = await client.PostAsJsonAsync($"/api/releases/{fixture.ReleaseId}/test-change-requests",
            new { discipline = "System", changeRequestIds = new[] { fixture.AutoRaisedChangeId } });
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("SCR-00912", body);
        Assert.Contains("SYSTCR-", body);
    }

    [Fact]
    public async Task A_change_allocated_to_another_build_cannot_be_covered_here()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        var fixture = await SeedAsync(factory);
        await LoginAsync(client, "manual.engineer");

        using var response = await client.PostAsJsonAsync($"/api/releases/{fixture.ReleaseId}/test-change-requests",
            new { discipline = "System", changeRequestIds = new[] { fixture.OtherBuildChangeId } });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("change_request_not_selectable", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_problem_report_allocated_to_another_build_cannot_be_linked()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        var fixture = await SeedAsync(factory);
        await LoginAsync(client, "manual.engineer");

        using var response = await client.PostAsJsonAsync($"/api/releases/{fixture.ReleaseId}/test-change-requests",
            new { discipline = "System", changeRequestIds = new[] { fixture.FirstChangeId },
                problemReportIds = new[] { fixture.OtherBuildProblemReportId } });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("target build", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Problem_report_links_are_editable_only_while_the_test_change_request_is_open()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        var fixture = await SeedAsync(factory);
        await LoginAsync(client, "manual.engineer");

        using var open = await client.PostAsJsonAsync(
            $"/api/test-change-reviews/{fixture.AutoTcrId}/problem-reports",
            new { problemReportIds = new[] { fixture.ProblemReportId } });
        Assert.Equal(HttpStatusCode.OK, open.StatusCode);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var review = await db.TestChangeReviews.SingleAsync(x => x.Id == fixture.AutoTcrId);
            review.Submit("manual.engineer", "independent.reviewer", true, DateTimeOffset.UtcNow);
            await db.SaveChangesAsync();
        }

        using var inReview = await client.PostAsJsonAsync(
            $"/api/test-change-reviews/{fixture.AutoTcrId}/problem-reports",
            new { problemReportIds = new[] { fixture.ProblemReportId } });
        Assert.Equal(HttpStatusCode.Conflict, inReview.StatusCode);
        Assert.Contains("only while", await inReview.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Raising_one_takes_verification_authority()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        var fixture = await SeedAsync(factory);
        await LoginAsync(client, "manual.outsider");

        using var response = await client.PostAsJsonAsync($"/api/releases/{fixture.ReleaseId}/test-change-requests",
            new { discipline = "System", changeRequestIds = new[] { fixture.FirstChangeId } });
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }
}
