using System.Net;
using System.Net.Http.Json;
using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Identity;
using AeroLink.Domain.Programs;
using AeroLink.Domain.Releases;
using AeroLink.Domain.Requirements;
using AeroLink.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace AeroLink.Api.Tests;

/// <summary>
/// A reviewer follows Monday's email on Wednesday and the cycle closed on Tuesday. They should be told,
/// rather than landing on an ordinary page with no decision controls and no explanation.
///
/// The security property matters more than the courtesy. The resolver's standing promise is that missing,
/// unauthorized and unauthenticated all end at the workspace root, so a notice saying "this is no longer in
/// review" must never be the thing that tells somebody a record exists. It is offered only to a person who
/// actually held a step, and only after the access check.
/// </summary>
public sealed class ReviewLinkOutlivedCycleTests(SharedApiHost host) : IClassFixture<SharedApiHost>
{
    private sealed record Seeded(Guid ChangeRequestId, string Author, string Reviewer, string Bystander);

    [Fact]
    public async Task A_reviewer_whose_cycle_closed_is_told_so()
    {
        var fixture = await SeedAsync(host.Factory);
        using var reviewer = host.CreateClient();
        await LoginAsync(reviewer, fixture.Reviewer);

        // While the cycle is open there is a live decision, so nothing needs explaining.
        Assert.DoesNotContain("reviewEnded", await OpenLocationAsync(fixture.Reviewer, fixture.ChangeRequestId));

        using var returned = await reviewer.PostAsJsonAsync(
            $"/api/change-requests/{fixture.ChangeRequestId}/request-changes",
            new { reason = "The budget is asserted rather than derived." });
        Assert.Equal(HttpStatusCode.OK, returned.StatusCode);

        Assert.Contains("reviewEnded=1", await OpenLocationAsync(fixture.Reviewer, fixture.ChangeRequestId));
    }

    [Fact]
    public async Task Somebody_who_never_reviewed_it_is_told_nothing()
    {
        var fixture = await SeedAsync(host.Factory);
        using var reviewer = host.CreateClient();
        await LoginAsync(reviewer, fixture.Reviewer);
        using var returned = await reviewer.PostAsJsonAsync(
            $"/api/change-requests/{fixture.ChangeRequestId}/request-changes",
            new { reason = "Rework the tolerance." });
        Assert.Equal(HttpStatusCode.OK, returned.StatusCode);

        // The author can see the record and knows perfectly well what happened — they were notified. The
        // bystander holds Program access but never held a step. Neither is owed an explanation, and the
        // bystander must not be handed evidence that the record was ever under review.
        var author = await OpenLocationAsync(fixture.Author, fixture.ChangeRequestId);
        var bystander = await OpenLocationAsync(fixture.Bystander, fixture.ChangeRequestId);

        Assert.DoesNotContain("reviewEnded", author);
        Assert.DoesNotContain("reviewEnded", bystander);
        // They still land on the record itself, exactly as before.
        Assert.Contains($"/change-requests/{fixture.ChangeRequestId}", bystander);
    }

    [Fact]
    public async Task Somebody_with_no_access_still_lands_at_the_workspace_root()
    {
        var fixture = await SeedAsync(host.Factory);
        using var reviewer = host.CreateClient();
        await LoginAsync(reviewer, fixture.Reviewer);
        using var returned = await reviewer.PostAsJsonAsync(
            $"/api/change-requests/{fixture.ChangeRequestId}/request-changes",
            new { reason = "Rework the tolerance." });
        Assert.Equal(HttpStatusCode.OK, returned.StatusCode);

        // A different deployment's user entirely: no Program membership here at all.
        using var foreignFactory = new AeroLinkApiFactory();
        using var foreign = foreignFactory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        await ProblemReportApiTests.BootstrapAndLoginAsync(foreign);
        using var attempt = await foreign.GetAsync($"/open/scr/{fixture.ChangeRequestId}");

        Assert.Equal(HttpStatusCode.Redirect, attempt.StatusCode);
        Assert.Equal("/", attempt.Headers.Location!.ToString());
    }

    private async Task<string> OpenLocationAsync(string userName, Guid changeRequestId)
    {
        using var client = host.Factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        await LoginAsync(client, userName);
        using var response = await client.GetAsync($"/open/scr/{changeRequestId}");
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        return response.Headers.Location!.ToString();
    }

    private static async Task<Seeded> SeedAsync(AeroLinkApiFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        var now = DateTimeOffset.UtcNow;
        var tag = Guid.NewGuid().ToString("N")[..8];
        var author = $"author.{tag}";
        var reviewer = $"reviewer.{tag}";
        var bystander = $"bystander.{tag}";

        var program = new ProgramRecord($"Outlived Link Program {tag}", $"OLP{tag}");
        var project = new ProjectRecord(program.Id, "Software", "Outlived Software");
        var release = new SoftwareRelease(project.Id, "1.6", false);
        db.AddRange(program, project, release);

        foreach (var (name, role) in new[]
                 { (author, ProgramRole.Engineer), (reviewer, ProgramRole.Approver), (bystander, ProgramRole.Engineer) })
        {
            var account = new UserAccount(name, name, $"{name}@example.test",
                IdentityService.HashPassword(AeroLinkApiFactory.MemberPassword), now);
            db.Add(account);
            db.Add(new ProgramMembership(account.Id, program.Id, role, "test.setup", now));
        }

        var scr = new SystemChangeRequest("SRCR-00039", 0, project.Id, release.Id,
            "Reduce flight-plan reload latency", "P", "A", "S", author, now);
        scr.AddRequirementChange(author, "SYSR-00000151", 2, RequirementLevel.System, RequirementChangeKind.Modify,
            "The FMS shall make the active flight plan available within 1.5 seconds.", "Latency", "Test", now);
        scr.SubmitForReview(author, [new(reviewer, "Marcus Hale")], now);
        db.SystemChangeRequests.Add(scr);
        await db.SaveChangesAsync();
        return new Seeded(scr.Id, author, reviewer, bystander);
    }

    private static async Task LoginAsync(HttpClient client, string userName)
    {
        var login = await client.PostAsJsonAsync("/api/auth/login", new { userName, password = AeroLinkApiFactory.MemberPassword });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        await SecurityBoundaryTests.AuthorizeMutationsAsync(client);
    }
}
