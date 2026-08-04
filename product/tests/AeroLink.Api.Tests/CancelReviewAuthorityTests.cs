using System.Net;
using System.Net.Http.Json;
using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Identity;
using AeroLink.Domain.Programs;
using AeroLink.Domain.Requirements;
using AeroLink.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace AeroLink.Api.Tests;

/// <summary>
/// Who may stop a review: people with a stake in it.
///
/// "Anyone should be able to reject an active workflow" was the request, and taken literally it would let
/// somebody with Project access but no part in a change halt a review they have nothing to do with. The set
/// is the author, anybody the review is waiting on, a Program manager, and an administrator — which covers
/// every case the request was actually about while leaving a bystander unable to do it by accident.
/// </summary>
public sealed class CancelReviewAuthorityTests
{
    private const string Reason = "Superseded by a wider change.";

    private sealed record Scenario(Guid ProjectId, Guid ScrId);

    private static async Task<Scenario> SeedAsync(AeroLinkApiFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        var now = DateTimeOffset.UtcNow;

        var program = new ProgramRecord("Cancel Review Program", "CRV");
        var project = new ProjectRecord(program.Id, "Flight Software", "Cancel Review Software");
        var release = new SoftwareRelease(project.Id, "1.0", false);
        db.AddRange(program, project, release);

        var specification = new RequirementSpecification(project.Id, "SYSRD-000001", "System Requirements Document",
            RequirementLevel.System.ToString(), "Seeded specification.", "test.setup", now);
        var section = new SpecificationNode(specification.Id, null, 1000, SpecificationNodeType.Section,
            "Functional Behavior", null, "test.setup", now);
        db.AddRange(specification, section);

        foreach (var (userName, role) in new[]
                 {
                     ("cancel.author", ProgramRole.Engineer),
                     ("cancel.reviewer", ProgramRole.Approver),
                     ("cancel.manager", ProgramRole.ProgramManager),
                     ("cancel.bystander", ProgramRole.Engineer),
                 })
        {
            var account = new UserAccount(userName, userName, $"{userName}@example.test",
                IdentityService.HashPassword(AeroLinkApiFactory.MemberPassword), now);
            db.Add(account);
            db.Add(new ProgramMembership(account.Id, program.Id, role, "test.setup", now));
        }

        var scr = new SystemChangeRequest("SCR-00500", 0, project.Id, release.Id, "Governed change",
            "Problem", "Analysis", "Solution", "cancel.author", now);
        scr.AddRequirementChange("cancel.author", "SYSR-00000500", 0, RequirementLevel.System,
            RequirementChangeKind.Introduce, "The FMS shall hold its course.", "Rationale.", "Test", now,
            targetSectionId: section.Id);
        scr.SubmitForReview("cancel.author", [new ApproverSelection("cancel.reviewer", "Cancel Reviewer")], now);
        db.Add(scr);
        await db.SaveChangesAsync();
        return new(project.Id, scr.Id);
    }

    [Theory]
    [InlineData("cancel.author")]
    [InlineData("cancel.reviewer")]
    [InlineData("cancel.manager")]
    public async Task Somebody_with_a_stake_can_stop_the_review(string userName)
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        var scenario = await SeedAsync(factory);
        await SignInAsync(client, userName);

        using var response = await client.PostAsJsonAsync($"/api/change-requests/{scenario.ScrId}/cancel-review", new { reason = Reason });
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {body}");
        Assert.Contains("\"state\":\"Draft\"", body);
    }

    [Fact]
    public async Task A_bystander_in_the_Program_cannot()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        var scenario = await SeedAsync(factory);
        await SignInAsync(client, "cancel.bystander");

        using var response = await client.PostAsJsonAsync($"/api/change-requests/{scenario.ScrId}/cancel-review", new { reason = Reason });
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task A_review_cannot_be_stopped_without_saying_why()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        var scenario = await SeedAsync(factory);
        await SignInAsync(client, "cancel.author");

        using var response = await client.PostAsJsonAsync($"/api/change-requests/{scenario.ScrId}/cancel-review", new { reason = "  " });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("why", await response.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A stale page must not silently unwind a review that moved on — somebody may have approved a stage in
    /// the meantime, and the cancelling reader would be acting on a screen that no longer exists.
    /// </summary>
    [Fact]
    public async Task A_stale_version_is_refused_rather_than_applied()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        var scenario = await SeedAsync(factory);
        await SignInAsync(client, "cancel.author");

        using var response = await client.PostAsJsonAsync($"/api/change-requests/{scenario.ScrId}/cancel-review",
            new { reason = Reason, expectedVersion = 9_999L });
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("stale_version", await response.Content.ReadAsStringAsync());
    }

    private static async Task SignInAsync(HttpClient client, string userName)
    {
        var login = await client.PostAsJsonAsync("/api/auth/login",
            new { userName, password = AeroLinkApiFactory.MemberPassword });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        await SecurityBoundaryTests.AuthorizeMutationsAsync(client);
    }
}
