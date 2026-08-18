using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Documents;
using AeroLink.Domain.Identity;
using AeroLink.Domain.Programs;
using AeroLink.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace AeroLink.Api.Tests;

/// <summary>
/// The same visibility split as a change request review, over a document revision. Asserted separately
/// rather than assumed to carry across, because the two reviews are different aggregates with different
/// state machines and only the rules are shared.
/// </summary>
public sealed class DocumentReviewCommentApiTests(SharedApiHost host) : IClassFixture<SharedApiHost>
{
    private sealed record Seeded(Guid RevisionId, string Owner, string FirstReviewer, string SecondReviewer);

    [Fact]
    public async Task A_draft_reaches_nobody_and_deciding_hands_it_to_the_owner()
    {
        var fixture = await SeedAsync(host.Factory);
        using var reviewer = host.CreateClient();
        await LoginAsync(reviewer, fixture.FirstReviewer);

        using var created = await reviewer.PostAsJsonAsync(
            $"/api/managed-documents/revisions/{fixture.RevisionId}/review-comments",
            new { body = "3.2.4 still cites the retired full-reload statement.", sectionLabel = "3.2 Flight plan synchronisation" });
        Assert.True(created.StatusCode == HttpStatusCode.Created, await created.Content.ReadAsStringAsync());
        var comment = await created.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Draft", comment.GetProperty("state").GetString());
        Assert.Equal("DocumentRevision", comment.GetProperty("anchor").GetString());

        using var owner = host.CreateClient();
        await LoginAsync(owner, fixture.Owner);
        Assert.Empty(await CommentsFor(owner, fixture.RevisionId));

        // The second reviewer has not decided either, so they see nothing of it.
        using var second = host.CreateClient();
        await LoginAsync(second, fixture.SecondReviewer);
        Assert.Empty(await CommentsFor(second, fixture.RevisionId));

        await ApproveAsync(host.Factory, fixture.RevisionId, fixture.FirstReviewer);

        var visible = Assert.Single(await CommentsFor(owner, fixture.RevisionId));
        Assert.Equal("Published", visible.GetProperty("state").GetString());
        Assert.True(visible.GetProperty("decisionRecorded").GetBoolean());
        Assert.Equal("3.2 Flight plan synchronisation", visible.GetProperty("sectionLabel").GetString());
    }

    [Fact]
    public async Task Only_a_reviewer_on_the_document_can_comment()
    {
        var fixture = await SeedAsync(host.Factory);
        using var owner = host.CreateClient();
        await LoginAsync(owner, fixture.Owner);

        using var refused = await owner.PostAsJsonAsync(
            $"/api/managed-documents/revisions/{fixture.RevisionId}/review-comments",
            new { body = "The owner's own note." });
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
    }

    private static async Task ApproveAsync(AeroLinkApiFactory factory, Guid revisionId, string actor)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        var revision = await LoadAsync(db, revisionId);
        revision.Approve(actor, "Reads correctly.", DateTimeOffset.UtcNow);
        await db.SaveChangesAsync();
    }

    private static async Task<ManagedDocumentRevision> LoadAsync(AeroLinkDbContext db, Guid revisionId) =>
        await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.SingleAsync(
            Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.Include(
                Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.Include(
                    db.ManagedDocumentRevisions, x => x.ReviewSteps), x => x.Comments),
            x => x.Id == revisionId);

    private static async Task<List<JsonElement>> CommentsFor(HttpClient client, Guid revisionId)
    {
        using var response = await client.GetAsync($"/api/managed-documents/revisions/{revisionId}/review-comments");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        return payload.GetProperty("comments").EnumerateArray().ToList();
    }

    private static async Task<Seeded> SeedAsync(AeroLinkApiFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        var now = DateTimeOffset.UtcNow;
        var tag = Guid.NewGuid().ToString("N")[..8];
        var owner = $"owner.{tag}";
        var first = $"first.{tag}";
        var second = $"second.{tag}";

        var program = new ProgramRecord($"Doc Comment Program {tag}", $"DCP{tag}");
        var project = new ProjectRecord(program.Id, "Software", "Doc Comment Software");
        db.AddRange(program, project);
        foreach (var (name, role) in new[]
                 { (owner, ProgramRole.Engineer), (first, ProgramRole.Approver), (second, ProgramRole.Approver) })
        {
            var account = new UserAccount(name, name, $"{name}@example.test",
                IdentityService.HashPassword(AeroLinkApiFactory.MemberPassword), now);
            db.Add(account);
            db.Add(new ProgramMembership(account.Id, program.Id, role, "test.setup", now));
        }

        var document = new ManagedDocument(project.Id, "SYSRD-00001", "SYSRD", "System Requirements",
            "System Requirements Document", owner, now);
        var revision = new ManagedDocumentRevision(document.Id, 0, owner, "Initial controlled draft.", now);
        revision.RecordCheckIn(Guid.NewGuid(), now);
        revision.SubmitForReview(owner, new string('b', 64),
        [
            new(first, "First Reviewer", "Independent technical review"),
            new(second, "Second Reviewer", "Quality release", Kind: ReviewStageKind.Approval),
        ], now);
        db.AddRange(document, revision);
        await db.SaveChangesAsync();
        return new Seeded(revision.Id, owner, first, second);
    }

    private static async Task LoginAsync(HttpClient client, string userName)
    {
        var login = await client.PostAsJsonAsync("/api/auth/login", new { userName, password = AeroLinkApiFactory.MemberPassword });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        await SecurityBoundaryTests.AuthorizeMutationsAsync(client);
    }
}
