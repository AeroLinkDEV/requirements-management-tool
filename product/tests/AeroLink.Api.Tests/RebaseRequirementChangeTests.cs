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
/// Keeping the work when somebody else reached the requirement first.
///
/// A change request refused at submission can drop the contested requirement or wait. Neither keeps an
/// analysis that is still valid and only disagrees with the winner about text. Rebasing is the third answer:
/// the author is shown what the requirement now says, what they proposed, and re-applies their intent.
///
/// The tool does not merge. The author wrote their words against the earlier text; carrying them onto the
/// later revision unchanged would assert they wrote them against wording they never saw.
/// </summary>
public sealed class RebaseRequirementChangeTests(SharedApiHost host) : IClassFixture<SharedApiHost>
{
    [Fact]
    public async Task An_approved_result_is_offered_with_both_texts_and_the_rebase_carries_the_new_wording()
    {
        var world = await SeedAsync(host.Factory, holderState: ChangeRequestState.Approved);
        using var client = host.CreateClient();
        await LoginAsync(client, world.Author);

        var offer = await client.GetFromJsonAsync<JsonElement>(
            $"/api/change-requests/{world.MineId}/requirements/{world.MyChangeId}/rebase");
        Assert.True(offer.GetProperty("available").GetBoolean());
        Assert.Equal(4, offer.GetProperty("onto").GetProperty("revision").GetInt32());
        Assert.Equal("The system shall reload within 1.2 seconds.", offer.GetProperty("onto").GetProperty("statement").GetString());
        Assert.Equal(3, offer.GetProperty("mine").GetProperty("revision").GetInt32());
        Assert.Equal("The system shall reload within 1.5 seconds.", offer.GetProperty("mine").GetProperty("statement").GetString());

        using var rebased = await client.PostAsJsonAsync(
            $"/api/change-requests/{world.MineId}/requirements/{world.MyChangeId}/rebase",
            new { statement = "The system shall reload within 1.0 seconds." });
        Assert.Equal(HttpStatusCode.OK, rebased.StatusCode);

        await using var scope = host.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        var change = await db.RequirementChanges.AsNoTracking().SingleAsync(x => x.Id == world.MyChangeId);
        Assert.Equal(4, change.Revision);
        Assert.Equal("The system shall reload within 1.0 seconds.", change.Statement);
    }

    [Fact]
    public async Task A_rebased_change_request_can_then_go_to_review()
    {
        var world = await SeedAsync(host.Factory, holderState: ChangeRequestState.Approved);
        using var client = host.CreateClient();
        await LoginAsync(client, world.Author);

        await client.PostAsJsonAsync($"/api/change-requests/{world.MineId}/requirements/{world.MyChangeId}/rebase",
            new { statement = "The system shall reload within 1.0 seconds." });

        // The holder still holds, so this is about the rebase not having broken anything, rather than the
        // contention having gone away. Dropping or waiting remain the other answers.
        await using var scope = host.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        var scr = await db.SystemChangeRequests.Include(x => x.RequirementChanges).SingleAsync(x => x.Id == world.MineId);
        Assert.Equal(ChangeRequestState.Draft, scr.State);
        Assert.Equal(4, scr.RequirementChanges.Single().Revision);
    }

    /// <summary>
    /// A change still in review can be returned, deferred or withdrawn. Rebasing onto a result that may never
    /// exist leaves this change request baselined on nothing, which is worse than having waited.
    /// </summary>
    [Fact]
    public async Task Nothing_is_offered_while_the_holder_is_still_in_review()
    {
        var world = await SeedAsync(host.Factory, holderState: ChangeRequestState.InReview);
        using var client = host.CreateClient();
        await LoginAsync(client, world.Author);

        var offer = await client.GetFromJsonAsync<JsonElement>(
            $"/api/change-requests/{world.MineId}/requirements/{world.MyChangeId}/rebase");
        Assert.False(offer.GetProperty("available").GetBoolean());
        Assert.Contains("still in review", offer.GetProperty("reason").GetString());

        using var refused = await client.PostAsJsonAsync(
            $"/api/change-requests/{world.MineId}/requirements/{world.MyChangeId}/rebase",
            new { statement = "Anything." });
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
    }

    /// <summary>
    /// A retired requirement cannot be modified, so there is nothing to re-apply a statement against. The
    /// affordance must be absent rather than merely unused.
    /// </summary>
    [Fact]
    public async Task Nothing_is_offered_when_the_holder_retires_the_requirement()
    {
        var world = await SeedAsync(host.Factory, holderState: ChangeRequestState.Approved,
            holderKind: RequirementChangeKind.Retire);
        using var client = host.CreateClient();
        await LoginAsync(client, world.Author);

        var offer = await client.GetFromJsonAsync<JsonElement>(
            $"/api/change-requests/{world.MineId}/requirements/{world.MyChangeId}/rebase");
        Assert.False(offer.GetProperty("available").GetBoolean());
        Assert.Contains("retires", offer.GetProperty("reason").GetString());
        Assert.Contains("contest the retirement", offer.GetProperty("reason").GetString());

        using var refused = await client.PostAsJsonAsync(
            $"/api/change-requests/{world.MineId}/requirements/{world.MyChangeId}/rebase",
            new { statement = "Anything." });
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
    }

    [Fact]
    public async Task Rebasing_an_approved_change_request_returns_it_to_draft()
    {
        var world = await SeedAsync(host.Factory, holderState: ChangeRequestState.Approved, approveMine: true);
        using var client = host.CreateClient();
        await LoginAsync(client, world.Author);

        using var rebased = await client.PostAsJsonAsync(
            $"/api/change-requests/{world.MineId}/requirements/{world.MyChangeId}/rebase",
            new { statement = "The system shall reload within 1.0 seconds." });
        Assert.Equal(HttpStatusCode.OK, rebased.StatusCode);

        await using var scope = host.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        var scr = await db.SystemChangeRequests.AsNoTracking().SingleAsync(x => x.Id == world.MineId);
        Assert.Equal(ChangeRequestState.Draft, scr.State);
    }

    [Fact]
    public async Task The_rebase_is_recorded_naming_both_revisions_and_what_it_was_rebased_onto()
    {
        var world = await SeedAsync(host.Factory, holderState: ChangeRequestState.Approved);
        using var client = host.CreateClient();
        await LoginAsync(client, world.Author);

        await client.PostAsJsonAsync($"/api/change-requests/{world.MineId}/requirements/{world.MyChangeId}/rebase",
            new { statement = "The system shall reload within 1.0 seconds." });

        await using var scope = host.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        var scr = await db.SystemChangeRequests.Include(x => x.AuditEvents).SingleAsync(x => x.Id == world.MineId);
        var entry = Assert.Single(scr.AuditEvents.Where(x => x.EventType == "RequirementChangeRebased"));
        Assert.Contains("from revision 3 onto revision 4", entry.Detail);
        Assert.Contains("SRCR-00961.00", entry.Detail);
    }

    private sealed record World(Guid ProjectId, Guid MineId, Guid MyChangeId, string Author, string Reviewer);

    private static async Task LoginAsync(HttpClient client, string userName)
    {
        using var response = await client.PostAsJsonAsync("/api/auth/login",
            new { userName, password = AeroLinkApiFactory.MemberPassword });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static async Task<World> SeedAsync(AeroLinkApiFactory factory, ChangeRequestState holderState,
        RequirementChangeKind holderKind = RequirementChangeKind.Modify, bool approveMine = false)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        var now = DateTimeOffset.UtcNow;
        var tag = Guid.NewGuid().ToString("N")[..8];
        var author = $"rebase.author.{tag}";
        var reviewer = $"rebase.reviewer.{tag}";
        const string requirement = "SYSR-00201";

        var program = new ProgramRecord($"Rebase Program {tag}", $"RBS{tag}");
        var project = new ProjectRecord(program.Id, "Software", "Rebase Software");
        var release = new SoftwareRelease(project.Id, "1.6", false);
        db.AddRange(program, project, release);
        foreach (var (name, role) in new[] { (author, ProgramRole.Engineer), (reviewer, ProgramRole.Approver) })
        {
            var account = new UserAccount(name, name, $"{name}@example.test",
                IdentityService.HashPassword(AeroLinkApiFactory.MemberPassword), now);
            db.Add(account);
            db.Add(new ProgramMembership(account.Id, program.Id, role, "test.setup", now));
        }

        // The winner: reaches the requirement first and takes it to revision 4.
        var holder = new SystemChangeRequest("SRCR-00961", 0, project.Id, release.Id,
            "Reached it first", "P", "A", "S", author, now);
        holder.AddRequirementChange(author, requirement, 4, RequirementLevel.System, holderKind,
            holderKind == RequirementChangeKind.Retire ? "" : "The system shall reload within 1.2 seconds.",
            "Budget apportioned", "Test", now);
        holder.SubmitForReview(author, [new(reviewer, "Reviewer")], now);
        db.SystemChangeRequests.Add(holder);
        await db.SaveChangesAsync();

        if (holderState == ChangeRequestState.Approved)
        {
            var tracked = await db.SystemChangeRequests
                .Include(x => x.RequirementChanges).Include(x => x.ReviewCycles).ThenInclude(x => x.Steps)
                .SingleAsync(x => x.Id == holder.Id);
            tracked.ApproveActiveStage(reviewer, now);
            await db.SaveChangesAsync();
        }

        // Mine: written against revision 3, the text the winner has since replaced.
        var mine = new SystemChangeRequest("SRCR-00962", 0, project.Id, release.Id,
            "Written against the earlier text", "P", "A", "S", author, now);
        var change = mine.AddRequirementChange(author, requirement, 3, RequirementLevel.System,
            RequirementChangeKind.Modify, "The system shall reload within 1.5 seconds.", "Latency", "Test", now);
        db.SystemChangeRequests.Add(mine);
        await db.SaveChangesAsync();

        if (approveMine)
        {
            var tracked = await db.SystemChangeRequests
                .Include(x => x.RequirementChanges).Include(x => x.ReviewCycles).ThenInclude(x => x.Steps)
                .SingleAsync(x => x.Id == mine.Id);
            tracked.SubmitForReview(author, [new(reviewer, "Reviewer")], now);
            await db.SaveChangesAsync();
            tracked.ApproveActiveStage(reviewer, now);
            await db.SaveChangesAsync();
        }

        return new World(project.Id, mine.Id, change.Id, author, reviewer);
    }
}
