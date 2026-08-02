using System.Net;
using System.Net.Http.Json;
using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Identity;
using AeroLink.Domain.Programs;
using AeroLink.Domain.Releases;
using AeroLink.Domain.Requirements;
using AeroLink.Domain.Verification;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AeroLink.Api.Tests;

/// <summary>
/// Revising an approved change request, from the state approved change requests are actually in.
///
/// The action was gated on exactly `ScrState.Approved`, which reads correctly in the enum and was unreachable
/// in the product: allocating an approved change request to a candidate baseline moves it to
/// SelectedForBaseline, and there it stays. Across the demonstration programme's 113 change requests not one
/// was in `Approved` — 107 were SelectedForBaseline — so the button existed, was correct, and could never
/// appear on any record anybody opened.
///
/// These tests are written against the state transitions rather than the enum, because that is the difference
/// the defect lived in.
/// </summary>
public sealed class ReviseChangeRequestApiTests
{
    private static async Task<(Guid ScrId, Guid ReleaseId)> SeedAsync(AeroLinkApiFactory factory,
        bool allocate, bool releaseTheBuild)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        var now = DateTimeOffset.UtcNow;

        var program = new ProgramRecord("Revise Program", "RVP");
        var project = new ProjectRecord(program.Id, "Flight Software", "Revise Software");
        var release = new SoftwareRelease(project.Id, "1.6", false);
        db.AddRange(program, project, release);
        await db.SaveChangesAsync();

        foreach (var (name, role) in new[] { ("revise.author", ProgramRole.Engineer), ("revise.approver", ProgramRole.Approver) })
        {
            var account = new UserAccount(name, name, $"{name}@example.test",
                IdentityService.HashPassword(AeroLinkApiFactory.MemberPassword), now);
            db.Add(account);
            db.Add(new ProgramMembership(account.Id, program.Id, role, "test.setup", now));
        }
        await db.SaveChangesAsync();

        var scr = new SystemChangeRequest("SCR-00060", 0, project.Id, release.Id,
            "Oceanic waypoint sequencing", "P", "A", "S", "revise.author", now);
        scr.AddRequirementChange("revise.author", "SYSR-00000601", 0, RequirementLevel.System,
            RequirementChangeKind.Introduce, "The FMS shall sequence oceanic waypoints.", "New", "Test", now);
        scr.SubmitForReview("revise.author", [new("revise.approver", "Revise Approver")], now);
        scr.ApproveActiveStage("revise.approver", now);
        Assert.Equal(ScrState.Approved, scr.State);
        if (allocate) scr.MarkSelectedForBaseline("revise.author", now);
        db.SystemChangeRequests.Add(scr);
        var report = new ProblemReport(project.Id, "PR-00001", "Oceanic sequence anomaly",
            "The sequence skipped a waypoint.", "", "revise.author", now);
        db.ProblemReports.Add(report);
        db.ProblemReportLinks.Add(new ProblemReportLink(report.Id, "Release", release.Id,
            "BuildScope", "revise.author", now));
        db.ProblemReportLinks.Add(new ProblemReportLink(report.Id, "ChangeRequest", scr.Id,
            "ProposedCorrectiveAction", "revise.author", now));

        // Released last, so the change request reaches its state through the ordinary transitions first. A
        // release that shipped before its change requests were approved is not a state this product can be in.
        if (releaseTheBuild) release.MarkReleased(now);
        await db.SaveChangesAsync();
        return (scr.Id, release.Id);
    }

    private static async Task SignInAsync(HttpClient client)
    {
        var login = await client.PostAsJsonAsync("/api/auth/login",
            new { userName = "revise.author", password = AeroLinkApiFactory.MemberPassword });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        await SecurityBoundaryTests.AuthorizeMutationsAsync(client);
    }

    /// <summary>Both signed-for states revise, because to the person asking they are the same fact.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_signed_for_change_request_on_an_in_work_build_revises(bool allocated)
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        var (scrId, _) = await SeedAsync(factory, allocate: allocated, releaseTheBuild: false);
        await SignInAsync(client);

        using var response = await client.PostAsJsonAsync($"/api/scrs/{scrId}/next-revision", new { });
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.Created, $"{(int)response.StatusCode}: {body}");

        var next = await response.Content.ReadFromJsonAsync<ScrShape>();
        Assert.NotNull(next);
        Assert.Equal("SCR-00060.01", next.DisplayNumber);
        Assert.Equal("Draft", next.State);
        // The content comes forward, which is the whole point of revising rather than starting again.
        Assert.Single(next.RequirementChanges);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        Assert.True(await db.ProblemReportLinks.AnyAsync(x => x.ArtifactType == "ChangeRequest"
            && x.ArtifactId == next.Id && x.Relationship == "ProposedCorrectiveAction"));
    }

    /// <summary>
    /// And once the build has shipped, neither does. This is the case a UI-only gate would have let through to
    /// a 400, so it is asserted at the endpoint rather than in the browser.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_change_request_incorporated_in_a_released_build_is_refused(bool allocated)
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        var (scrId, _) = await SeedAsync(factory, allocate: allocated, releaseTheBuild: true);
        await SignInAsync(client);

        using var response = await client.PostAsJsonAsync($"/api/scrs/{scrId}/next-revision", new { });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("released build", body);
    }

    private sealed record ScrShape(Guid Id, string DisplayNumber, string State, ScrChangeShape[] RequirementChanges);
    private sealed record ScrChangeShape(string DisplayNumber);
}
