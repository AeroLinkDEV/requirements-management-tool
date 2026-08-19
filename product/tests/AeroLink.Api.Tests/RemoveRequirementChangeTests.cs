using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Common;
using AeroLink.Domain.Identity;
using AeroLink.Domain.Programs;
using AeroLink.Domain.Requirements;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AeroLink.Api.Tests;

/// <summary>
/// Taking a requirement change back off a draft.
///
/// The domain could always do this — UpdateDraft replaces the whole set — but nothing reached it once the
/// route that did was retired, so an author who added the wrong requirement had to abandon the change request
/// and start again, losing the problem statement, the analysis, and any review comments written against it.
///
/// It is also the remedy for a submission refused because another change request holds the requirement, which
/// until now had no remedy but waiting.
/// </summary>
public sealed class RemoveRequirementChangeTests(SharedApiHost host) : IClassFixture<SharedApiHost>
{
    [Fact]
    public async Task An_author_can_take_a_requirement_back_off_their_draft()
    {
        var world = await SeedAsync(host.Factory);
        using var client = host.CreateClient();
        await LoginAsync(client, world.Author);

        using var removed = await client.DeleteAsync(
            $"/api/change-requests/{world.ChangeRequestId}/requirements/{world.SecondChangeId}");
        Assert.Equal(HttpStatusCode.OK, removed.StatusCode);

        var detail = await removed.Content.ReadFromJsonAsync<JsonElement>();
        var remaining = detail.GetProperty("requirementChanges").EnumerateArray().ToList();
        Assert.Single(remaining);
        Assert.Equal(world.FirstRequirement, remaining[0].GetProperty("baseNumber").GetString());
    }

    [Fact]
    public async Task Removing_the_last_requirement_is_allowed_but_submitting_with_none_is_not()
    {
        var world = await SeedAsync(host.Factory);
        using var client = host.CreateClient();
        await LoginAsync(client, world.Author);

        foreach (var id in new[] { world.FirstChangeId, world.SecondChangeId })
            Assert.Equal(HttpStatusCode.OK,
                (await client.DeleteAsync($"/api/change-requests/{world.ChangeRequestId}/requirements/{id}")).StatusCode);

        await using var scope = host.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        var scr = await db.SystemChangeRequests.Include(x => x.RequirementChanges)
            .SingleAsync(x => x.Id == world.ChangeRequestId);

        Assert.Empty(scr.RequirementChanges);
        Assert.Throws<DomainException>(() =>
            scr.SubmitForReview(world.Author, [new(world.Reviewer, "Reviewer")], DateTimeOffset.UtcNow));
    }

    [Fact]
    public async Task A_package_in_front_of_reviewers_does_not_change_under_them()
    {
        var world = await SeedAsync(host.Factory, submit: true);
        using var client = host.CreateClient();
        await LoginAsync(client, world.Author);

        using var refused = await client.DeleteAsync(
            $"/api/change-requests/{world.ChangeRequestId}/requirements/{world.SecondChangeId}");
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
    }

    [Fact]
    public async Task Somebody_who_is_not_the_author_cannot_remove_from_it()
    {
        var world = await SeedAsync(host.Factory);
        using var stranger = host.CreateClient();
        await LoginAsync(stranger, world.Reviewer);

        using var refused = await stranger.DeleteAsync(
            $"/api/change-requests/{world.ChangeRequestId}/requirements/{world.SecondChangeId}");
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
    }

    [Fact]
    public async Task A_requirement_that_belongs_to_another_change_request_is_refused()
    {
        var world = await SeedAsync(host.Factory);
        using var client = host.CreateClient();
        await LoginAsync(client, world.Author);

        using var refused = await client.DeleteAsync(
            $"/api/change-requests/{world.ChangeRequestId}/requirements/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Contains("not part of this change request",
            (await refused.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
    }

    /// <summary>
    /// The reason this issue existed. A change request refused because somebody else holds one of its
    /// requirements can now drop that one and send the rest, instead of waiting for them to finish.
    /// </summary>
    [Fact]
    public async Task A_change_request_blocked_on_a_contested_requirement_can_drop_it_and_submit()
    {
        var world = await SeedAsync(host.Factory);

        await using (var scope = host.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var holder = new SystemChangeRequest("SRCR-00921", 0, world.ProjectId, world.ReleaseId,
                "Holder", "P", "A", "S", world.Author, DateTimeOffset.UtcNow);
            holder.AddRequirementChange(world.Author, world.SecondRequirement, 2, RequirementLevel.System,
                RequirementChangeKind.Modify, "Held.", "R", "Test", DateTimeOffset.UtcNow);
            holder.SubmitForReview(world.Author, [new(world.Reviewer, "Reviewer")], DateTimeOffset.UtcNow);
            db.SystemChangeRequests.Add(holder);
            await db.SaveChangesAsync();
        }

        await using (var scope = host.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var blocking = (await ArtifactClaims.ContendersAsync(db, world.ProjectId,
                [world.SecondRequirement], world.ChangeRequestId, default)).Where(x => x.Holds).ToList();
            Assert.NotEmpty(blocking);
            Assert.Contains("Remove the contested", ArtifactClaims.Refusal(blocking));
        }

        using var client = host.CreateClient();
        await LoginAsync(client, world.Author);
        Assert.Equal(HttpStatusCode.OK, (await client.DeleteAsync(
            $"/api/change-requests/{world.ChangeRequestId}/requirements/{world.SecondChangeId}")).StatusCode);

        await using (var scope = host.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var scr = await db.SystemChangeRequests.Include(x => x.RequirementChanges)
                .SingleAsync(x => x.Id == world.ChangeRequestId);

            // Nothing it still carries is contended, so it goes.
            Assert.Empty(await ArtifactClaims.NoticesAsync(db, scr, default));
            scr.SubmitForReview(world.Author, [new(world.Reviewer, "Reviewer")], DateTimeOffset.UtcNow);
            await db.SaveChangesAsync();
            Assert.Equal(ChangeRequestState.InReview, scr.State);
        }
    }

    private sealed record World(Guid ProjectId, Guid ReleaseId, Guid ChangeRequestId, Guid FirstChangeId,
        Guid SecondChangeId, string FirstRequirement, string SecondRequirement, string Author, string Reviewer);

    private static async Task LoginAsync(HttpClient client, string userName)
    {
        using var response = await client.PostAsJsonAsync("/api/auth/login",
            new { userName, password = AeroLinkApiFactory.MemberPassword });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static async Task<World> SeedAsync(AeroLinkApiFactory factory, bool submit = false)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        var now = DateTimeOffset.UtcNow;
        var tag = Guid.NewGuid().ToString("N")[..8];
        var author = $"remove.author.{tag}";
        var reviewer = $"remove.reviewer.{tag}";

        var program = new ProgramRecord($"Remove Program {tag}", $"RMV{tag}");
        var project = new ProjectRecord(program.Id, "Software", "Remove Software");
        var release = new SoftwareRelease(project.Id, "1.6", false);
        db.AddRange(program, project, release);
        foreach (var (name, role) in new[] { (author, ProgramRole.Engineer), (reviewer, ProgramRole.Approver) })
        {
            var account = new UserAccount(name, name, $"{name}@example.test",
                IdentityService.HashPassword(AeroLinkApiFactory.MemberPassword), now);
            db.Add(account);
            db.Add(new ProgramMembership(account.Id, program.Id, role, "test.setup", now));
        }

        var scr = new SystemChangeRequest("SRCR-00920", 0, project.Id, release.Id,
            "Two requirements", "P", "A", "S", author, now);
        var first = scr.AddRequirementChange(author, "SYSR-00161", 2, RequirementLevel.System,
            RequirementChangeKind.Modify, "The system shall respond within 1.5 seconds.", "Latency", "Test", now);
        var second = scr.AddRequirementChange(author, "SYSR-00162", 2, RequirementLevel.System,
            RequirementChangeKind.Modify, "The system shall log the reload.", "Traceability", "Test", now);
        if (submit) scr.SubmitForReview(author, [new(reviewer, "Reviewer")], now);
        db.SystemChangeRequests.Add(scr);
        await db.SaveChangesAsync();

        return new World(project.Id, release.Id, scr.Id, first.Id, second.Id,
            "SYSR-00161", "SYSR-00162", author, reviewer);
    }
}
