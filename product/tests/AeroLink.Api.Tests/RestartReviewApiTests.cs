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
/// A review sent to the wrong approver used to be unrecoverable through the product: the only route onward
/// was for that approver to act, which is precisely what cannot happen when they are the wrong person. The
/// domain has always supported cancelling and restarting; nothing exposed it.
/// </summary>
public sealed class RestartReviewApiTests : IClassFixture<SharedApiHost>
{
    private readonly SharedApiHost _host;

    public RestartReviewApiTests(SharedApiHost host)
    {
        _host = host;
    }

    private sealed record Seeded(Guid ChangeRequestId, Guid ProjectId, string AuthorName, string WrongName, string RightName);

    private static async Task<Seeded> SeedAsync(AeroLinkApiFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        var now = DateTimeOffset.UtcNow;
        var tag = Guid.NewGuid().ToString("N")[..8];
        var authorName = $"author.user.{tag}";
        var wrongName = $"wrong.user.{tag}";
        var rightName = $"right.user.{tag}";

        var program = new ProgramRecord($"Restart Program {tag}", $"RSP{tag}");
        var project = new ProjectRecord(program.Id, "Software", "Restart Software");
        var release = new SoftwareRelease(project.Id, "1.6", false);
        db.AddRange(program, project, release);
        await db.SaveChangesAsync();

        foreach (var (name, role) in new[] { (authorName, ProgramRole.Engineer), (wrongName, ProgramRole.Approver), (rightName, ProgramRole.Approver) })
        {
            var account = new UserAccount(name, name, $"{name}@example.test",
                IdentityService.HashPassword(AeroLinkApiFactory.MemberPassword), now);
            db.Add(account);
            db.Add(new ProgramMembership(account.Id, program.Id, role, "test.setup", now));
        }
        await db.SaveChangesAsync();

        var scr = new SystemChangeRequest("SRCR-00050", 0, project.Id, release.Id, "Oceanic routing", "P", "A", "S", authorName, now);
        scr.AddRequirementChange(authorName, "SYSR-00000501", 0, RequirementLevel.System, RequirementChangeKind.Introduce,
            "The FMS shall sequence oceanic waypoints.", "New capability", "Test", now);
        scr.SubmitForReview(authorName, [new(wrongName, "Wrong Approver")], now);
        db.SystemChangeRequests.Add(scr);
        await db.SaveChangesAsync();
        return new(scr.Id, project.Id, authorName, wrongName, rightName);
    }

    private static async Task LoginAsync(HttpClient client, string userName)
    {
        var login = await client.PostAsJsonAsync("/api/auth/login", new { userName, password = AeroLinkApiFactory.MemberPassword });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        await SecurityBoundaryTests.AuthorizeMutationsAsync(client);
    }

    [Fact]
    public async Task The_author_cancels_a_misrouted_review_and_restarts_it_with_the_right_approver()
    {
        using var client = _host.CreateClient();
        var fixture = await SeedAsync(_host.Factory);
        await LoginAsync(client, fixture.AuthorName);

        using var response = await client.PostAsJsonAsync($"/api/change-requests/{fixture.ChangeRequestId}/restart-review",
            new { reason = "Routed to the wrong discipline approver.", approvers = new[] { new { userId = fixture.RightName } } });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var detail = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("InReview", detail.GetProperty("state").GetString());

        using var scope = _host.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        var scr = await db.SystemChangeRequests.AsNoTracking()
            .Include(x => x.ReviewCycles).ThenInclude(x => x.Steps)
            .Include(x => x.AuditEvents)
            .SingleAsync(x => x.Id == fixture.ChangeRequestId);

        // The misrouted cycle is retained and cancelled rather than erased, and the new cycle names the
        // corrected approver. History is the product's whole claim, so nothing may be rewritten.
        Assert.Equal(2, scr.ReviewCycles.Count);
        var active = scr.ReviewCycles.Single(x => x.CompletedAt is null);
        Assert.Equal(fixture.RightName, active.Steps.Single().ApproverId);
        Assert.Contains(scr.AuditEvents, x => x.EventType == "ReviewCancelledAndRestarted");
    }

    [Fact]
    public async Task Someone_who_did_not_author_the_change_cannot_restart_its_review()
    {
        using var client = _host.CreateClient();
        var fixture = await SeedAsync(_host.Factory);
        await LoginAsync(client, fixture.WrongName);

        using var response = await client.PostAsJsonAsync($"/api/change-requests/{fixture.ChangeRequestId}/restart-review",
            new { reason = "I would rather someone else reviewed this.", approvers = new[] { new { userId = fixture.RightName } } });
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task A_restart_requires_a_recorded_reason_and_active_approvers()
    {
        using var client = _host.CreateClient();
        var fixture = await SeedAsync(_host.Factory);
        await LoginAsync(client, fixture.AuthorName);

        using var noReason = await client.PostAsJsonAsync($"/api/change-requests/{fixture.ChangeRequestId}/restart-review",
            new { reason = "  ", approvers = new[] { new { userId = fixture.RightName } } });
        Assert.Equal(HttpStatusCode.BadRequest, noReason.StatusCode);

        using var unknownApprover = await client.PostAsJsonAsync($"/api/change-requests/{fixture.ChangeRequestId}/restart-review",
            new { reason = "Routed to the wrong discipline approver.", approvers = new[] { new { userId = "nobody.here" } } });
        Assert.Equal(HttpStatusCode.BadRequest, unknownApprover.StatusCode);
    }
}
