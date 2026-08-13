using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Identity;
using AeroLink.Domain.Programs;
using AeroLink.Domain.Releases;
using AeroLink.Domain.Requirements;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AeroLink.Api.Tests;

/// <summary>
/// A change-request review step now records why the reviewer decided and whether the package was returned to
/// the author at that step. Prior cycles stay historical: a resubmission starts the next cycle and the
/// returned step remains readable in the old one, exactly as document reviews behave.
/// </summary>
public sealed class ChangeRequestReviewRationaleApiTests
{
    [Fact]
    public async Task Approval_records_the_reviewers_rationale_on_the_step_and_signature()
    {
        using var factory = new AeroLinkApiFactory();
        var fixture = await SeedAsync(factory);
        using var client = factory.CreateClient();
        await LoginAsync(client, "reviewer.user");

        using var approved = await client.PostAsJsonAsync($"/api/change-requests/{fixture.ChangeRequestId}/approve",
            new
            {
                password = AeroLinkApiFactory.MemberPassword,
                meaning = "I approve this change request.",
                rationale = "The proposed wording matches the verified HLR behavior.",
            });
        Assert.Equal(HttpStatusCode.OK, approved.StatusCode);
        var detail = await approved.Content.ReadFromJsonAsync<JsonElement>();
        var step = detail.GetProperty("reviewCycles")[0].GetProperty("steps")[0];
        Assert.Equal("Approved", step.GetProperty("state").GetString());
        Assert.Equal("The proposed wording matches the verified HLR behavior.", step.GetProperty("rationale").GetString());

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        var signature = await db.ElectronicSignatures.AsNoTracking()
            .SingleAsync(x => x.ArtifactId == fixture.ChangeRequestId && x.Action == "Approve");
        Assert.Equal("The proposed wording matches the verified HLR behavior.", signature.Rationale);
    }

    [Fact]
    public async Task Return_records_the_active_step_and_keeps_the_cycle_after_resubmission()
    {
        using var factory = new AeroLinkApiFactory();
        var fixture = await SeedAsync(factory);
        using var client = factory.CreateClient();
        await LoginAsync(client, "reviewer.user");

        using var returned = await client.PostAsJsonAsync($"/api/change-requests/{fixture.ChangeRequestId}/request-changes",
            new { reason = "The trigger wording needs the exact verified HLR reference." });
        Assert.Equal(HttpStatusCode.OK, returned.StatusCode);
        var returnedDetail = await returned.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Draft", returnedDetail.GetProperty("state").GetString());
        var returnedStep = returnedDetail.GetProperty("reviewCycles")[0].GetProperty("steps")[0];
        Assert.Equal("Returned", returnedStep.GetProperty("state").GetString());
        Assert.Equal("The trigger wording needs the exact verified HLR reference.", returnedStep.GetProperty("rationale").GetString());
        Assert.Equal("ChangesRequested", returnedDetail.GetProperty("reviewCycles")[0].GetProperty("state").GetString());
        Assert.Equal("The trigger wording needs the exact verified HLR reference.",
            returnedDetail.GetProperty("reviewCycles")[0].GetProperty("closureReason").GetString());

        // The author reworks and resubmits. Cycle two starts fresh; cycle one stays readable with its Returned step.
        using var author = factory.CreateClient();
        await LoginAsync(author, "author.user");
        using var resubmitted = await author.PostAsJsonAsync($"/api/change-requests/{fixture.ChangeRequestId}/submit",
            new { approvers = new[] { new { userId = "reviewer.user" } } });
        Assert.Equal(HttpStatusCode.OK, resubmitted.StatusCode);
        var resubmittedDetail = await resubmitted.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(2, resubmittedDetail.GetProperty("reviewCycles").GetArrayLength());
        Assert.Equal("Returned", resubmittedDetail.GetProperty("reviewCycles")[0].GetProperty("steps")[0].GetProperty("state").GetString());
        Assert.Equal("Active", resubmittedDetail.GetProperty("reviewCycles")[1].GetProperty("steps")[0].GetProperty("state").GetString());
    }

    [Fact]
    public async Task Only_the_active_reviewer_can_return_the_package()
    {
        using var factory = new AeroLinkApiFactory();
        var fixture = await SeedAsync(factory);
        using var client = factory.CreateClient();
        await LoginAsync(client, "other.user");

        using var refused = await client.PostAsJsonAsync($"/api/change-requests/{fixture.ChangeRequestId}/request-changes",
            new { reason = "I am not the active reviewer but I want changes." });
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
    }

    private static async Task<(Guid ChangeRequestId, Guid ProjectId)> SeedAsync(AeroLinkApiFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        var now = DateTimeOffset.UtcNow;

        var program = new ProgramRecord("Review Rationale Program", "RRP");
        var project = new ProjectRecord(program.Id, "Software", "Rationale Software");
        var release = new SoftwareRelease(project.Id, "1.6", false);
        db.AddRange(program, project, release);

        foreach (var (name, role) in new[] { ("author.user", ProgramRole.Engineer), ("reviewer.user", ProgramRole.Approver), ("other.user", ProgramRole.Approver) })
        {
            var account = new UserAccount(name, name, $"{name}@example.test",
                IdentityService.HashPassword(AeroLinkApiFactory.MemberPassword), now);
            db.Add(account);
            db.Add(new ProgramMembership(account.Id, program.Id, role, "test.setup", now));
        }

        var scr = new SystemChangeRequest("SRCR-00051", 0, project.Id, release.Id, "Oceanic routing", "P", "A", "S", "author.user", now);
        scr.AddRequirementChange("author.user", "SYSR-00000502", 0, RequirementLevel.System, RequirementChangeKind.Introduce,
            "The FMS shall sequence oceanic waypoints.", "New capability", "Test", now);
        scr.SubmitForReview("author.user", [new("reviewer.user", "Reviewer")], now);
        db.SystemChangeRequests.Add(scr);
        await db.SaveChangesAsync();
        return (scr.Id, project.Id);
    }

    private static async Task LoginAsync(HttpClient client, string userName)
    {
        var login = await client.PostAsJsonAsync("/api/auth/login", new { userName, password = AeroLinkApiFactory.MemberPassword });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        await SecurityBoundaryTests.AuthorizeMutationsAsync(client);
    }
}
