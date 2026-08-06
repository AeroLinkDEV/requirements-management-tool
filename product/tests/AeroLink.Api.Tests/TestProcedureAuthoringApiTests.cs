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
/// Authoring the procedure decisions a test change request carries.
///
/// The test-side counterpart of authoring requirement changes inside a change request, and the surface the
/// workspace reads and writes.
/// </summary>
public sealed class TestProcedureAuthoringApiTests
{
    private sealed record Fixture(Guid ProjectId, Guid ReleaseId, Guid TcrId, Guid ReleasedTcrId);

    private static async Task<Fixture> SeedAsync(AeroLinkApiFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        var impact = scope.ServiceProvider.GetRequiredService<VerificationImpactService>();
        var now = DateTimeOffset.UtcNow;

        var program = new ProgramRecord("Procedure Program", "PRO");
        var project = new ProjectRecord(program.Id, "Software", "Procedure Software");
        var inWork = new SoftwareRelease(project.Id, "1.6", false);
        // Constructed unreleased and released afterwards: the constructor refuses a release that is born closed.
        var closed = new SoftwareRelease(project.Id, "1.5", false);
        db.AddRange(program, project, inWork, closed);

        SystemChangeRequest Approved(string number, string requirement, Guid releaseId)
        {
            var scr = new SystemChangeRequest(number, 0, project.Id, releaseId, "Oceanic", "P", "A", "S", "author", now);
            scr.AddRequirementChange("author", requirement, 0, RequirementLevel.System,
                RequirementChangeKind.Introduce, "The FMS shall sequence oceanic waypoints.", "New capability",
                "Test", now);
            scr.SubmitForReview("author", [new ApproverSelection("reviewer", "Reviewer")], now);
            scr.ApproveActiveStage("reviewer", now);
            return scr;
        }

        var open = Approved("SRCR-00920", "SYSR-00000921", inWork.Id);
        var shipped = Approved("SRCR-00921", "SYSR-00000922", closed.Id);
        db.AddRange(open, shipped);

        foreach (var (user, role) in new[]
                 {
                     ("procedure.engineer", ProgramRole.TestEngineer),
                     ("procedure.approver", ProgramRole.Approver),
                     ("procedure.outsider", ProgramRole.Engineer),
                 })
        {
            var account = new UserAccount(user, user, $"{user}@example.test",
                IdentityService.HashPassword(AeroLinkApiFactory.MemberPassword), now);
            db.Add(account);
            db.Add(new ProgramMembership(account.Id, program.Id, role, "test.setup", now));
        }
        await db.SaveChangesAsync();

        foreach (var id in new[] { open.Id, shipped.Id })
        {
            var tracked = await db.SystemChangeRequests.Include(x => x.RequirementChanges).SingleAsync(x => x.Id == id);
            await impact.RaiseForApprovedChangeRequestAsync(tracked, now, default);
        }
        await db.SaveChangesAsync();

        closed.MarkReleased(now);
        await db.SaveChangesAsync();

        var tcrId = await db.TestChangeReviews.Where(x => x.ChangeRequestId == open.Id).Select(x => x.Id).SingleAsync();
        var releasedTcrId = await db.TestChangeReviews.Where(x => x.ChangeRequestId == shipped.Id).Select(x => x.Id).SingleAsync();
        return new(project.Id, inWork.Id, tcrId, releasedTcrId);
    }

    private static async Task LoginAsync(HttpClient client, string user)
    {
        using var login = await client.PostAsJsonAsync("/api/auth/login",
            new { userName = user, password = AeroLinkApiFactory.MemberPassword });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        await SecurityBoundaryTests.AuthorizeMutationsAsync(client);
    }

    private static async Task ConcludeTestWorkRequiredAsync(HttpClient client, Guid tcrId)
    {
        using var response = await client.PostAsJsonAsync($"/api/test-change-reviews/{tcrId}/conclusion",
            new { testChangeRequired = true });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Introducing_a_procedure_allocates_its_number_rather_than_letting_the_caller_choose()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        var fixture = await SeedAsync(factory);
        await LoginAsync(client, "procedure.engineer");
        await ConcludeTestWorkRequiredAsync(client, fixture.TcrId);

        using var response = await client.PostAsJsonAsync(
            $"/api/test-change-reviews/{fixture.TcrId}/procedure-changes",
            new
            {
                kind = "Introduce", revision = 0, title = "Oceanic waypoint sequencing",
                objective = "Verify oceanic waypoints sequence in flight-plan order.",
                preconditions = "The aircraft is in cruise on an oceanic plan.",
                steps = "1. Load the plan. 2. Read the sequencer.",
                expectedResult = "The next eligible waypoint is sequenced.",
                rationale = "No procedure exercises oceanic sequencing after the approved change."
            });
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{(int)response.StatusCode}: {body}");

        // Allocated centrally, so two engineers proposing a new procedure at once cannot claim one number.
        var created = JsonSerializer.Deserialize<JsonElement>(body);
        Assert.Matches(@"^SYSTP-\d{6}\.00$", created.GetProperty("displayNumber").GetString()!);
        // The discipline fixes the level; the caller does not get to disagree with it.
        Assert.Equal("System", created.GetProperty("level").GetString());
    }

    [Fact]
    public async Task Nothing_can_be_proposed_until_the_assessment_has_asked_for_test_work()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        var fixture = await SeedAsync(factory);
        await LoginAsync(client, "procedure.engineer");

        using var response = await client.PostAsJsonAsync(
            $"/api/test-change-reviews/{fixture.TcrId}/procedure-changes",
            new { kind = "Introduce", revision = 0, title = "Premature", objective = "o", steps = "s", expectedResult = "e", rationale = "r" });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task A_modification_has_to_name_the_procedure_it_acts_on()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        var fixture = await SeedAsync(factory);
        await LoginAsync(client, "procedure.engineer");
        await ConcludeTestWorkRequiredAsync(client, fixture.TcrId);

        // Allocating a fresh number for a modification would silently turn it into a different procedure.
        using var response = await client.PostAsJsonAsync(
            $"/api/test-change-reviews/{fixture.TcrId}/procedure-changes",
            new { kind = "Modify", revision = 1, title = "Nameless", objective = "o", steps = "s", expectedResult = "e", rationale = "r" });
        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("name the procedure", body);
    }

    [Fact]
    public async Task The_workspace_reads_back_what_was_proposed_and_can_withdraw_it()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        var fixture = await SeedAsync(factory);
        await LoginAsync(client, "procedure.engineer");
        await ConcludeTestWorkRequiredAsync(client, fixture.TcrId);

        using var created = await client.PostAsJsonAsync(
            $"/api/test-change-reviews/{fixture.TcrId}/procedure-changes",
            new
            {
                kind = "Introduce", revision = 0, title = "Oceanic waypoint sequencing",
                objective = "Verify oceanic sequencing.", preconditions = "Cruise.",
                steps = "1. Load. 2. Read.", expectedResult = "Sequenced.", rationale = "Nothing covers it."
            });
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        var changeId = JsonSerializer.Deserialize<JsonElement>(await created.Content.ReadAsStringAsync())
            .GetProperty("id").GetGuid();

        var package = await client.GetFromJsonAsync<JsonElement>(
            $"/api/test-change-reviews/{fixture.TcrId}/procedure-changes");
        Assert.Equal("System", package.GetProperty("procedureLevel").GetString());
        Assert.Equal("ChangeRequired", package.GetProperty("outcome").GetString());
        var change = Assert.Single(package.GetProperty("procedureChanges").EnumerateArray());
        Assert.Equal("Oceanic waypoint sequencing", change.GetProperty("title").GetString());
        Assert.Equal("Introduce", change.GetProperty("kind").GetString());

        using var removed = await client.DeleteAsync(
            $"/api/test-change-reviews/{fixture.TcrId}/procedure-changes/{changeId}");
        Assert.Equal(HttpStatusCode.OK, removed.StatusCode);
        var after = await client.GetFromJsonAsync<JsonElement>(
            $"/api/test-change-reviews/{fixture.TcrId}/procedure-changes");
        Assert.Empty(after.GetProperty("procedureChanges").EnumerateArray());
    }

    [Fact]
    public async Task Someone_without_test_authority_cannot_propose_procedure_work()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        var fixture = await SeedAsync(factory);
        await LoginAsync(client, "procedure.outsider");

        using var response = await client.PostAsJsonAsync(
            $"/api/test-change-reviews/{fixture.TcrId}/procedure-changes",
            new { kind = "Introduce", revision = 0, title = "T", objective = "o", steps = "s", expectedResult = "e", rationale = "r" });
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task A_released_build_refuses_procedure_work()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        var fixture = await SeedAsync(factory);
        await LoginAsync(client, "procedure.engineer");

        using var propose = await client.PostAsJsonAsync(
            $"/api/test-change-reviews/{fixture.ReleasedTcrId}/procedure-changes",
            new { kind = "Introduce", revision = 0, title = "T", objective = "o", steps = "s", expectedResult = "e", rationale = "r" });
        Assert.Equal(HttpStatusCode.Conflict, propose.StatusCode);

        // Revising it is refused on the earlier ground that it was never approved. The released-build refusal
        // sits behind that check, and is covered where an approved package meets a closed build in
        // TestChangeReviewTests.
        using var revise = await client.PostAsJsonAsync(
            $"/api/test-change-reviews/{fixture.ReleasedTcrId}/revise", new { });
        Assert.Equal(HttpStatusCode.BadRequest, revise.StatusCode);
        Assert.Contains("Only an approved test change request", await revise.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task An_approved_package_advances_to_its_next_revision_carrying_its_work()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        var fixture = await SeedAsync(factory);
        await LoginAsync(client, "procedure.engineer");
        await ConcludeTestWorkRequiredAsync(client, fixture.TcrId);

        using var created = await client.PostAsJsonAsync(
            $"/api/test-change-reviews/{fixture.TcrId}/procedure-changes",
            new
            {
                kind = "Introduce", revision = 0, title = "Oceanic waypoint sequencing",
                objective = "Verify oceanic sequencing.", preconditions = "Cruise.",
                steps = "1. Load. 2. Read.", expectedResult = "Sequenced.", rationale = "Nothing covers it."
            });
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);

        // Only an approved package can be revised, so the unapproved one is refused first.
        using var tooEarly = await client.PostAsJsonAsync($"/api/test-change-reviews/{fixture.TcrId}/revise", new { });
        Assert.Equal(HttpStatusCode.BadRequest, tooEarly.StatusCode);

        // A package cannot be submitted while its assessment items are still open, so answer them first.
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            foreach (var item in await db.VerificationImpactItems.Where(x => x.TestChangeReviewId == fixture.TcrId).ToListAsync())
                item.Resolve("procedure.engineer", VerificationImpactOutcome.NewProcedureRequired,
                    "The procedure proposed in this package will cover it.", DateTimeOffset.UtcNow);
            await db.SaveChangesAsync();
        }

        using var submit = await client.PostAsJsonAsync($"/api/test-change-reviews/{fixture.TcrId}/submit",
            new { approverId = "procedure.approver" });
        var submitBody = await submit.Content.ReadAsStringAsync();
        Assert.True(submit.StatusCode == HttpStatusCode.OK, $"{(int)submit.StatusCode}: {submitBody}");
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var review = await db.TestChangeReviews.SingleAsync(x => x.Id == fixture.TcrId);
            review.Approve("procedure.approver", "Procedure decisions are complete.", DateTimeOffset.UtcNow);
            await db.SaveChangesAsync();
        }

        using var revise = await client.PostAsJsonAsync($"/api/test-change-reviews/{fixture.TcrId}/revise", new { });
        var body = await revise.Content.ReadAsStringAsync();
        Assert.True(revise.StatusCode == HttpStatusCode.OK, $"{(int)revise.StatusCode}: {body}");

        var next = JsonSerializer.Deserialize<JsonElement>(body);
        Assert.Equal(1, next.GetProperty("revision").GetInt32());
        Assert.EndsWith(".01", next.GetProperty("displayNumber").GetString()!);
        // Carries the work forward, and stays concluded that test work is required: reopening approved work to
        // correct it is not a reason to re-ask whether any was needed.
        Assert.Equal(1, next.GetProperty("procedureChanges").GetInt32());
        Assert.Equal("ChangeRequired", next.GetProperty("outcome").GetString());
    }
}
