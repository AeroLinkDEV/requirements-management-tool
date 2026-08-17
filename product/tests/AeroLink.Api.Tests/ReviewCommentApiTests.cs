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
/// A reviewer's comment is working communication, not evidence, and who may read it is not who may read the
/// package. What is asserted here is the split over HTTP: a draft reaches nobody, deciding hands it to the
/// author at once, and a reviewer who has not decided is shown less than the author is.
///
/// Shares one API host through <see cref="SharedApiHost"/>; each test seeds uniquely named users and a
/// uniquely coded Program so the shared database never collides.
/// </summary>
public sealed class ReviewCommentApiTests(SharedApiHost host) : IClassFixture<SharedApiHost>
{
    private sealed record Seeded(Guid ChangeRequestId, Guid RequirementChangeId, string Author,
        string FirstReviewer, string SecondReviewer);

    [Fact]
    public async Task A_draft_comment_is_visible_to_its_author_and_to_nobody_else()
    {
        var fixture = await SeedAsync(host.Factory);
        using var reviewer = host.CreateClient();
        await LoginAsync(reviewer, fixture.FirstReviewer);

        using var created = await reviewer.PostAsJsonAsync(
            $"/api/change-requests/{fixture.ChangeRequestId}/review-comments",
            new { anchor = "RequirementRevision", requirementChangeId = fixture.RequirementChangeId, body = "1.5s is asserted, not derived." });
        Assert.True(created.StatusCode == HttpStatusCode.Created, await created.Content.ReadAsStringAsync());
        var comment = await created.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Draft", comment.GetProperty("state").GetString());
        Assert.True(comment.GetProperty("isMine").GetBoolean());

        Assert.Single(await CommentsFor(reviewer, fixture.ChangeRequestId));

        using var author = host.CreateClient();
        await LoginAsync(author, fixture.Author);
        Assert.Empty(await CommentsFor(author, fixture.ChangeRequestId));
    }

    [Fact]
    public async Task Deciding_hands_the_comment_to_the_author_without_waiting_for_the_cycle()
    {
        var fixture = await SeedAsync(host.Factory);
        using var reviewer = host.CreateClient();
        await LoginAsync(reviewer, fixture.FirstReviewer);
        await PostCommentAsync(reviewer, fixture.ChangeRequestId, "Tolerance is not stated.");

        using var approved = await reviewer.PostAsJsonAsync($"/api/change-requests/{fixture.ChangeRequestId}/approve",
            new { password = AeroLinkApiFactory.MemberPassword, meaning = "I approve this change request." });
        Assert.Equal(HttpStatusCode.OK, approved.StatusCode);

        using var author = host.CreateClient();
        await LoginAsync(author, fixture.Author);
        var visible = await CommentsFor(author, fixture.ChangeRequestId);
        var only = Assert.Single(visible);
        Assert.Equal("Published", only.GetProperty("state").GetString());
        Assert.True(only.GetProperty("decisionRecorded").GetBoolean());
        Assert.False(only.GetProperty("isMine").GetBoolean());
    }

    [Fact]
    public async Task A_reviewer_who_has_not_decided_is_shown_less_than_the_author_is()
    {
        var fixture = await SeedAsync(host.Factory, ReviewMode.Parallel);
        using var first = host.CreateClient();
        await LoginAsync(first, fixture.FirstReviewer);
        await PostCommentAsync(first, fixture.ChangeRequestId, "Signed with reservations.");
        using var approved = await first.PostAsJsonAsync($"/api/change-requests/{fixture.ChangeRequestId}/approve",
            new { password = AeroLinkApiFactory.MemberPassword, meaning = "I approve this change request." });
        Assert.Equal(HttpStatusCode.OK, approved.StatusCode);

        // The author has it already.
        using var author = host.CreateClient();
        await LoginAsync(author, fixture.Author);
        Assert.Single(await CommentsFor(author, fixture.ChangeRequestId));

        // The second reviewer is still deciding, so reading it would weaken the signature they are about to
        // give. They see nothing until they have decided themselves.
        using var second = host.CreateClient();
        await LoginAsync(second, fixture.SecondReviewer);
        Assert.Empty(await CommentsFor(second, fixture.ChangeRequestId));

        using var alsoApproved = await second.PostAsJsonAsync($"/api/change-requests/{fixture.ChangeRequestId}/approve",
            new { password = AeroLinkApiFactory.MemberPassword, meaning = "I approve this change request." });
        Assert.Equal(HttpStatusCode.OK, alsoApproved.StatusCode);
        Assert.Single(await CommentsFor(second, fixture.ChangeRequestId));
    }

    [Fact]
    public async Task Only_the_author_of_a_comment_can_change_it()
    {
        var fixture = await SeedAsync(host.Factory, ReviewMode.Parallel);
        using var first = host.CreateClient();
        await LoginAsync(first, fixture.FirstReviewer);
        var commentId = await PostCommentAsync(first, fixture.ChangeRequestId, "Mine to edit.");

        using var second = host.CreateClient();
        await LoginAsync(second, fixture.SecondReviewer);
        using var refused = await second.PutAsJsonAsync(
            $"/api/change-requests/{fixture.ChangeRequestId}/review-comments/{commentId}",
            new { anchor = "ChangeCase", body = "Not yours." });
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);

        using var deleted = await second.DeleteAsync(
            $"/api/change-requests/{fixture.ChangeRequestId}/review-comments/{commentId}");
        Assert.Equal(HttpStatusCode.BadRequest, deleted.StatusCode);

        using var revised = await first.PutAsJsonAsync(
            $"/api/change-requests/{fixture.ChangeRequestId}/review-comments/{commentId}",
            new { anchor = "ChangeCase", body = "Second thought." });
        Assert.Equal(HttpStatusCode.OK, revised.StatusCode);
    }

    [Fact]
    public async Task My_work_tells_the_author_there_are_comments_without_anything_being_sent()
    {
        var fixture = await SeedAsync(host.Factory);
        using var reviewer = host.CreateClient();
        await LoginAsync(reviewer, fixture.FirstReviewer);
        await PostCommentAsync(reviewer, fixture.ChangeRequestId, "Tolerance is not stated.");

        using var author = host.CreateClient();
        await LoginAsync(author, fixture.Author);

        // A draft is not the author's to know about, so nothing appears yet.
        Assert.Empty(await MyWorkCommentsAsync(author));

        using var approved = await reviewer.PostAsJsonAsync($"/api/change-requests/{fixture.ChangeRequestId}/approve",
            new { password = AeroLinkApiFactory.MemberPassword, meaning = "I approve this change request." });
        Assert.Equal(HttpStatusCode.OK, approved.StatusCode);

        var waiting = Assert.Single(await MyWorkCommentsAsync(author));
        Assert.Equal("A reviewer commented on your package", waiting.GetProperty("title").GetString());

        // Nothing was sent. The package is still locked, so a message now would be noise followed minutes
        // later by the one that actually matters.
        using var scope = host.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        Assert.Empty(await db.UserNotifications.AsNoTracking()
            .Where(x => x.ArtifactId == fixture.ChangeRequestId && x.Recipient == fixture.Author).ToListAsync());
    }

    private static async Task<List<JsonElement>> MyWorkCommentsAsync(HttpClient client)
    {
        using var response = await client.GetAsync("/api/my-work");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        return payload.GetProperty("tasks").EnumerateArray()
            .Where(task => task.GetProperty("type").GetString() == "Reviewer comments")
            .ToList();
    }

    [Fact]
    public async Task A_comment_cannot_name_a_revision_from_a_different_package()
    {
        var fixture = await SeedAsync(host.Factory);
        using var reviewer = host.CreateClient();
        await LoginAsync(reviewer, fixture.FirstReviewer);

        using var refused = await reviewer.PostAsJsonAsync(
            $"/api/change-requests/{fixture.ChangeRequestId}/review-comments",
            new { anchor = "RequirementRevision", requirementChangeId = Guid.NewGuid(), body = "About something else." });
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
    }

    private static async Task<Guid> PostCommentAsync(HttpClient client, Guid changeRequestId, string body)
    {
        using var created = await client.PostAsJsonAsync($"/api/change-requests/{changeRequestId}/review-comments",
            new { anchor = "ChangeCase", body });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var comment = await created.Content.ReadFromJsonAsync<JsonElement>();
        return comment.GetProperty("id").GetGuid();
    }

    private static async Task<List<JsonElement>> CommentsFor(HttpClient client, Guid changeRequestId)
    {
        using var response = await client.GetAsync($"/api/change-requests/{changeRequestId}/review-comments");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        return payload.GetProperty("cycles").EnumerateArray()
            .SelectMany(cycle => cycle.GetProperty("comments").EnumerateArray())
            .ToList();
    }

    private static async Task<Seeded> SeedAsync(AeroLinkApiFactory factory, ReviewMode mode = ReviewMode.Sequential)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        var now = DateTimeOffset.UtcNow;
        var tag = Guid.NewGuid().ToString("N")[..8];
        var author = $"author.{tag}";
        var first = $"first.{tag}";
        var second = $"second.{tag}";

        var program = new ProgramRecord($"Review Comment Program {tag}", $"RCP{tag}");
        var project = new ProjectRecord(program.Id, "Software", "Comment Software");
        var release = new SoftwareRelease(project.Id, "1.6", false);
        db.AddRange(program, project, release);

        foreach (var (name, role) in new[]
                 { (author, ProgramRole.Engineer), (first, ProgramRole.Approver), (second, ProgramRole.Approver) })
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
        scr.SubmitForReview(author, [new(first, "First Reviewer"), new(second, "Second Reviewer")], now, mode);
        db.SystemChangeRequests.Add(scr);
        await db.SaveChangesAsync();
        return new Seeded(scr.Id, scr.RequirementChanges.Single().Id, author, first, second);
    }

    private static async Task LoginAsync(HttpClient client, string userName)
    {
        var login = await client.PostAsJsonAsync("/api/auth/login", new { userName, password = AeroLinkApiFactory.MemberPassword });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        await SecurityBoundaryTests.AuthorizeMutationsAsync(client);
    }
}
