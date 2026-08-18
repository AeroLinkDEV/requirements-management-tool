using System.Net;
using System.Net.Http.Json;
using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Documents;
using AeroLink.Domain.Identity;
using AeroLink.Domain.Programs;
using AeroLink.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace AeroLink.Api.Tests;

/// <summary>
/// A document reviewer following their email after the review closed, asserted separately from the change
/// request case because the two hang off different aggregates: a document keeps its steps on the revision
/// with an integer round, so "still open" is the revision's own state rather than a cycle's.
///
/// The security property is the same and matters more than the courtesy: the resolver promises that missing,
/// unauthorized and unauthenticated all end at the workspace root, so this notice must never be what tells
/// somebody a record exists.
/// </summary>
public sealed class DocumentReviewLinkOutlivedTests(SharedApiHost host) : IClassFixture<SharedApiHost>
{
    private sealed record Seeded(Guid DocumentId, Guid RevisionId, string Owner, string Reviewer, string Bystander);

    [Fact]
    public async Task A_document_reviewer_whose_review_closed_is_told_so()
    {
        var fixture = await SeedAsync(host.Factory);

        // While the review is open there is a live decision, so nothing needs explaining.
        Assert.DoesNotContain("reviewEnded", await OpenLocationAsync(fixture.Reviewer, fixture.DocumentId));

        await ReturnAsync(host.Factory, fixture.RevisionId, fixture.Reviewer);

        Assert.Contains("reviewEnded=1", await OpenLocationAsync(fixture.Reviewer, fixture.DocumentId));
    }

    [Fact]
    public async Task Following_the_revision_identifier_answers_the_same_way()
    {
        var fixture = await SeedAsync(host.Factory);
        await ReturnAsync(host.Factory, fixture.RevisionId, fixture.Reviewer);

        // Both spellings of the link resolve to the same document, so both must answer alike.
        Assert.Contains("reviewEnded=1", await OpenLocationAsync(fixture.Reviewer, fixture.RevisionId));
    }

    [Fact]
    public async Task The_owner_and_a_bystander_are_told_nothing()
    {
        var fixture = await SeedAsync(host.Factory);
        await ReturnAsync(host.Factory, fixture.RevisionId, fixture.Reviewer);

        // The owner was notified through the ordinary route and knows. The bystander holds Program access
        // but never held a step, and must not be handed evidence the document was ever under review.
        var owner = await OpenLocationAsync(fixture.Owner, fixture.DocumentId);
        var bystander = await OpenLocationAsync(fixture.Bystander, fixture.DocumentId);

        Assert.DoesNotContain("reviewEnded", owner);
        Assert.DoesNotContain("reviewEnded", bystander);
        Assert.Contains($"/documentation-center/{fixture.DocumentId}", bystander);
    }

    [Fact]
    public async Task Somebody_with_no_access_still_lands_at_the_workspace_root()
    {
        var fixture = await SeedAsync(host.Factory);
        await ReturnAsync(host.Factory, fixture.RevisionId, fixture.Reviewer);

        using var foreignFactory = new AeroLinkApiFactory();
        using var foreign = foreignFactory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        await ProblemReportApiTests.BootstrapAndLoginAsync(foreign);
        using var attempt = await foreign.GetAsync($"/open/managed-document/{fixture.DocumentId}");

        Assert.Equal(HttpStatusCode.Redirect, attempt.StatusCode);
        Assert.Equal("/", attempt.Headers.Location!.ToString());
    }

    private static async Task ReturnAsync(AeroLinkApiFactory factory, Guid revisionId, string actor)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        var revision = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.SingleAsync(
            Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.Include(
                Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.Include(
                    db.ManagedDocumentRevisions, x => x.ReviewSteps), x => x.Comments),
            x => x.Id == revisionId);
        revision.Return(actor, "Settle 3.2.4 first.", DateTimeOffset.UtcNow);
        await db.SaveChangesAsync();
    }

    private async Task<string> OpenLocationAsync(string userName, Guid id)
    {
        using var client = host.Factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var login = await client.PostAsJsonAsync("/api/auth/login", new { userName, password = AeroLinkApiFactory.MemberPassword });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        using var response = await client.GetAsync($"/open/managed-document/{id}");
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        return response.Headers.Location!.ToString();
    }

    private static async Task<Seeded> SeedAsync(AeroLinkApiFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        var now = DateTimeOffset.UtcNow;
        var tag = Guid.NewGuid().ToString("N")[..8];
        var owner = $"owner.{tag}";
        var reviewer = $"reviewer.{tag}";
        var bystander = $"bystander.{tag}";

        var program = new ProgramRecord($"Doc Link Program {tag}", $"DLP{tag}");
        var project = new ProjectRecord(program.Id, "Software", "Doc Link Software");
        db.AddRange(program, project);
        foreach (var (name, role) in new[]
                 { (owner, ProgramRole.Engineer), (reviewer, ProgramRole.Approver), (bystander, ProgramRole.Engineer) })
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
            new(reviewer, "Reviewer", "Independent technical review"),
            new($"second.{tag}", "Second", "Quality release", Kind: ReviewStageKind.Approval),
        ], now);
        db.AddRange(document, revision);

        var second = new UserAccount($"second.{tag}", $"second.{tag}", $"second.{tag}@example.test",
            IdentityService.HashPassword(AeroLinkApiFactory.MemberPassword), now);
        db.Add(second);
        db.Add(new ProgramMembership(second.Id, program.Id, ProgramRole.Approver, "test.setup", now));

        await db.SaveChangesAsync();
        return new Seeded(document.Id, revision.Id, owner, reviewer, bystander);
    }
}
