using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Identity;
using AeroLink.Domain.Programs;
using AeroLink.Domain.Requirements;
using AeroLink.Domain.Verification;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AeroLink.Api.Tests;

/// <summary>
/// Consolidating several source changes into one manual Test Change Request must never strand their
/// verification work behind a superseded assessment. Items move with their source change, identity and
/// history preserved; unfolding returns them to a fresh actionable assessment.
/// </summary>
public sealed class TestChangeRequestConsolidationTests
{
    private sealed record Fixture(Guid ProjectId, Guid ReleaseId, Guid FirstChangeId, Guid SecondChangeId,
        Guid FirstReviewId, Guid SecondReviewId, Guid FirstItemId, Guid SecondItemId);

    private static async Task<Fixture> SeedAsync(AeroLinkApiFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        var impact = scope.ServiceProvider.GetRequiredService<VerificationImpactService>();
        var now = DateTimeOffset.UtcNow;

        var program = new ProgramRecord("Consolidation Program", "CON");
        var project = new ProjectRecord(program.Id, "Software", "Consolidation Software");
        var release = new SoftwareRelease(project.Id, "1.6", false);
        db.AddRange(program, project, release);

        SystemChangeRequest Approved(string number, string requirement)
        {
            var scr = new SystemChangeRequest(number, 0, project.Id, release.Id, "Oceanic", "P", "A", "S", "author", now);
            scr.AddRequirementChange("author", requirement, 0, RequirementLevel.System, RequirementChangeKind.Introduce,
                $"The FMS shall sequence {requirement}.", "New capability", "Analysis", now);
            scr.SubmitForReview("author", [new("reviewer", "Reviewer")], now);
            scr.ApproveActiveStage("reviewer", now);
            return scr;
        }

        var first = Approved("SRCR-00950", "SYSR-00000951");
        var second = Approved("SRCR-00951", "SYSR-00000952");
        db.AddRange(first, second);
        foreach (var (user, role) in new[]
                 {
                     ("consolidation.engineer", ProgramRole.TestEngineer),
                     ("consolidation.lead", ProgramRole.TestLead),
                     ("consolidation.reviewer", ProgramRole.Approver),
                 })
        {
            var account = new UserAccount(user, user, $"{user}@example.test",
                IdentityService.HashPassword(AeroLinkApiFactory.MemberPassword), now);
            db.Add(account);
            db.Add(new ProgramMembership(account.Id, program.Id, role, "test.setup", now));
        }
        await db.SaveChangesAsync();

        await impact.RaiseForApprovedChangeRequestAsync(
            await db.SystemChangeRequests.Include(x => x.RequirementChanges).SingleAsync(x => x.Id == first.Id),
            now, default);
        await impact.RaiseForApprovedChangeRequestAsync(
            await db.SystemChangeRequests.Include(x => x.RequirementChanges).SingleAsync(x => x.Id == second.Id),
            now, default);
        await db.SaveChangesAsync();

        var firstReview = await db.TestChangeReviews.SingleAsync(x => x.ChangeRequestId == first.Id
            && x.Discipline == TestChangeReviewDiscipline.System);
        var secondReview = await db.TestChangeReviews.SingleAsync(x => x.ChangeRequestId == second.Id
            && x.Discipline == TestChangeReviewDiscipline.System);
        var firstItem = await db.VerificationImpactItems.SingleAsync(x => x.ChangeRequestId == first.Id);
        var secondItem = await db.VerificationImpactItems.SingleAsync(x => x.ChangeRequestId == second.Id);
        return new(project.Id, release.Id, first.Id, second.Id, firstReview.Id, secondReview.Id,
            firstItem.Id, secondItem.Id);
    }

    private static async Task LoginAsync(HttpClient client, string user)
    {
        using var login = await client.PostAsJsonAsync("/api/auth/login",
            new { userName = user, password = AeroLinkApiFactory.MemberPassword });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        await SecurityBoundaryTests.AuthorizeMutationsAsync(client);
    }

    [Fact]
    public async Task Manual_consolidation_moves_verification_items_to_the_surviving_package()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        var fixture = await SeedAsync(factory);
        await LoginAsync(client, "consolidation.engineer");

        using var response = await client.PostAsJsonAsync($"/api/releases/{fixture.ReleaseId}/test-change-requests",
            new
            {
                discipline = "System",
                changeRequestIds = new[] { fixture.FirstChangeId, fixture.SecondChangeId },
                title = "Consolidated package",
                problem = "P", analysis = "A", solution = "S"
            });
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.Created, $"{(int)response.StatusCode}: {body}");
        var created = JsonSerializer.Deserialize<JsonElement>(body);
        var packageId = created.GetProperty("id").GetGuid();

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            // The first requested change is the base: its pending automatic review becomes the package.
            Assert.Equal(fixture.FirstReviewId, packageId);
            var second = await db.TestChangeReviews.SingleAsync(x => x.Id == fixture.SecondReviewId);
            Assert.Equal(TestChangeReviewState.Superseded, second.State);
            Assert.Equal(packageId, second.SupersededByTestChangeRequestId);

            // Every item — including the folded source's — now belongs to the surviving package.
            var items = await db.VerificationImpactItems
                .Where(x => x.Id == fixture.FirstItemId || x.Id == fixture.SecondItemId).ToListAsync();
            Assert.All(items, item => Assert.Equal(packageId, item.TestChangeReviewId));
            Assert.Empty(await db.VerificationImpactItems
                .Where(x => x.TestChangeReviewId == fixture.SecondReviewId).ToListAsync());
        }

        // The API-visible queue shows the folded source's work under the surviving package.
        var visible = await client.GetFromJsonAsync<JsonElement>(
            $"/api/releases/{fixture.ReleaseId}/verification-impact");
        var visibleItems = visible.EnumerateArray().ToList();
        Assert.Equal(2, visibleItems.Count);
        Assert.All(visibleItems, item => Assert.Equal(packageId, item.GetProperty("testChangeReviewId").GetGuid()));

        // The moved item can be resolved from the surviving package's authority.
        using var resolved = await client.PostAsJsonAsync($"/api/verification-impact/{fixture.SecondItemId}/resolve",
            new { outcome = "NewProcedureRequired", rationale = "A procedure must be written for the second change." });
        Assert.True(resolved.IsSuccessStatusCode, await resolved.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Manual_consolidation_preserves_assignment_decision_and_history()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        var fixture = await SeedAsync(factory);
        await LoginAsync(client, "consolidation.lead");

        using var assigned = await client.PostAsJsonAsync($"/api/verification-impact/{fixture.SecondItemId}/assign",
            new { engineerId = "consolidation.engineer" });
        Assert.True(assigned.IsSuccessStatusCode, await assigned.Content.ReadAsStringAsync());
        await LoginAsync(client, "consolidation.engineer");
        using var decided = await client.PostAsJsonAsync($"/api/verification-impact/{fixture.FirstItemId}/resolve",
            new { outcome = "NewProcedureRequired", rationale = "The first change needs a new procedure." });
        Assert.True(decided.IsSuccessStatusCode, await decided.Content.ReadAsStringAsync());

        await LoginAsync(client, "consolidation.engineer");
        using var created = await client.PostAsJsonAsync($"/api/releases/{fixture.ReleaseId}/test-change-requests",
            new
            {
                discipline = "System",
                changeRequestIds = new[] { fixture.FirstChangeId, fixture.SecondChangeId },
                title = "Consolidated with history",
                problem = "P", analysis = "A", solution = "S"
            });
        var body = await created.Content.ReadAsStringAsync();
        Assert.True(created.StatusCode == HttpStatusCode.Created, $"{(int)created.StatusCode}: {body}");

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var assignedItem = await db.VerificationImpactItems.SingleAsync(x => x.Id == fixture.SecondItemId);
            Assert.Equal("consolidation.engineer", assignedItem.AssignedEngineerId);
            Assert.Equal("consolidation.lead", assignedItem.AssignedByLeadId);

            var decidedItem = await db.VerificationImpactItems.SingleAsync(x => x.Id == fixture.FirstItemId);
            Assert.Equal("consolidation.engineer", decidedItem.ResolvedBy);
            Assert.Equal("The first change needs a new procedure.", decidedItem.ResolutionRationale);
            Assert.Equal(VerificationImpactState.Resolved, decidedItem.State);
            var history = await db.VerificationImpactDecisionHistory
                .Where(x => x.VerificationImpactItemId == fixture.FirstItemId).ToListAsync();
            Assert.Single(history);
            Assert.Equal(VerificationImpactHistoryAction.Resolved, history[0].Action);
        }
    }

    [Fact]
    public async Task Fold_moves_items_and_unfold_returns_them_to_a_fresh_assessment()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        var fixture = await SeedAsync(factory);
        await LoginAsync(client, "consolidation.engineer");

        using var created = await client.PostAsJsonAsync($"/api/releases/{fixture.ReleaseId}/test-change-requests",
            new
            {
                discipline = "System",
                changeRequestIds = new[] { fixture.FirstChangeId },
                title = "Base package",
                problem = "P", analysis = "A", solution = "S"
            });
        Assert.True(created.StatusCode == HttpStatusCode.Created, await created.Content.ReadAsStringAsync());

        using var folded = await client.PostAsJsonAsync(
            $"/api/test-change-reviews/{fixture.FirstReviewId}/change-requests",
            new { changeRequestId = fixture.SecondChangeId });
        Assert.True(folded.IsSuccessStatusCode, await folded.Content.ReadAsStringAsync());

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var secondItem = await db.VerificationImpactItems.SingleAsync(x => x.Id == fixture.SecondItemId);
            Assert.Equal(fixture.FirstReviewId, secondItem.TestChangeReviewId);
            var secondReview = await db.TestChangeReviews.SingleAsync(x => x.Id == fixture.SecondReviewId);
            Assert.Equal(TestChangeReviewState.Superseded, secondReview.State);
        }

        using var unfolded = await client.DeleteAsync(
            $"/api/test-change-reviews/{fixture.FirstReviewId}/change-requests/{fixture.SecondChangeId}");
        Assert.True(unfolded.IsSuccessStatusCode, await unfolded.Content.ReadAsStringAsync());

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var secondItem = await db.VerificationImpactItems.SingleAsync(x => x.Id == fixture.SecondItemId);
            Assert.NotEqual(fixture.FirstReviewId, secondItem.TestChangeReviewId);
            var fresh = await db.TestChangeReviews.SingleAsync(x => x.Id == secondItem.TestChangeReviewId);
            Assert.Equal(TestChangeReviewDiscipline.System, fresh.Discipline);
            Assert.Equal(TestChangeReviewOutcome.Pending, fresh.Outcome);
            Assert.Equal(TestChangeReviewState.Open, fresh.State);
            Assert.Equal(1, fresh.Revision);
            // The fresh assessment is actionable, not a copy of the superseded history.
            Assert.Equal("", fresh.BaseNumber);
        }
    }

    [Fact]
    public async Task A_concluded_assessment_cannot_be_folded_in()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        var fixture = await SeedAsync(factory);
        await LoginAsync(client, "consolidation.engineer");

        using var created = await client.PostAsJsonAsync($"/api/releases/{fixture.ReleaseId}/test-change-requests",
            new
            {
                discipline = "System",
                changeRequestIds = new[] { fixture.FirstChangeId },
                title = "Base package",
                problem = "P", analysis = "A", solution = "S"
            });
        Assert.True(created.StatusCode == HttpStatusCode.Created, await created.Content.ReadAsStringAsync());

        using var concluded = await client.PostAsJsonAsync(
            $"/api/test-change-reviews/{fixture.SecondReviewId}/conclusion",
            new { testChangeRequired = true });
        Assert.True(concluded.IsSuccessStatusCode, await concluded.Content.ReadAsStringAsync());

        using var folded = await client.PostAsJsonAsync(
            $"/api/test-change-reviews/{fixture.FirstReviewId}/change-requests",
            new { changeRequestId = fixture.SecondChangeId });
        Assert.Equal(HttpStatusCode.Conflict, folded.StatusCode);
        Assert.Contains("already has a System test assessment", await folded.Content.ReadAsStringAsync());
    }
}
