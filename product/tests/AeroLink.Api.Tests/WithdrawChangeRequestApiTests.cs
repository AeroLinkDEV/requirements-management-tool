using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AeroLink.Domain.Baselines;
using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Identity;
using AeroLink.Domain.Programs;
using AeroLink.Domain.Releases;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AeroLink.Api.Tests;

/// <summary>
/// Taking work back out of an open build, and unsealing a build so that becomes possible.
///
/// Two verbs, split on whether anybody was ever asked. A draft nobody reviewed is deleted, because there is no
/// decision to be accountable for. Anything that reached a reviewer is withdrawn: the record, its review
/// history and its signatures stay readable, and only its effect on the build goes.
///
/// Its effect on the build is nothing at all until the build is frozen and materialized, which is why nothing
/// here unwinds a requirement revision. The revisions belong to the freeze, so the way to take them back is to
/// reopen the build — a deliberate act with a name and a reason on it, rather than something that happens
/// quietly because an author changed their mind.
/// </summary>
public sealed class WithdrawChangeRequestApiTests
{
    private sealed record Fixture(Guid ProjectId, Guid ReleaseId, Guid BaselineId, Guid ApprovedId, Guid DraftId);

    private const string Author = "withdraw.author";
    private const string Manager = "withdraw.cm";

    /// <summary>
    /// One approved change request selected into a candidate baseline, and one draft nobody has ever submitted.
    /// The author is also the configuration manager, so a single sign-in drives both halves.
    /// </summary>
    private static async Task<Fixture> SeedAsync(AeroLinkApiFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        var now = DateTimeOffset.UtcNow;
        var program = new ProgramRecord("Withdrawal Program", "WDR");
        var project = new ProjectRecord(program.Id, "Software", "Withdrawal Software");
        var release = new SoftwareRelease(project.Id, "1.6", false);

        var approved = new SystemChangeRequest("SRCR-00110", 0, project.Id, release.Id,
            "Oceanic sequencing", "P", "A", "S", Author, now);
        approved.AddRequirementChange(Author, "SYSR-00000005", 3, RequirementLevel.System,
            RequirementChangeKind.Introduce, "The FMS shall sequence oceanic waypoints.", "New capability", "Analysis", now);
        approved.SubmitForReview(Author, [new("reviewer", "Reviewer")], now);
        approved.ApproveActiveStage("reviewer", now);

        var draft = new SystemChangeRequest("SRCR-00111", 0, project.Id, release.Id,
            "Never submitted", "P", "A", "S", Author, now);

        var baseline = new CandidateBaseline("SW-16.00", 0, project.Id, release.Id, null, "Build 1.6", Manager, now);
        baseline.Select(approved, Manager, now);
        db.AddRange(program, project, release, approved, draft, baseline);

        // One person wearing both hats: the author raises and withdraws, the configuration manager freezes and
        // reopens, and a single sign-in drives the whole sequence.
        var account = new UserAccount(Author, "Withdrawal Author", $"{Author}@example.test",
            IdentityService.HashPassword(AeroLinkApiFactory.MemberPassword), now);
        db.Add(account);
        foreach (var role in new[] { ProgramRole.Engineer, ProgramRole.ConfigurationManager })
            db.Add(new ProgramMembership(account.Id, program.Id, role, "test.setup", now));

        // Submitting through the API checks its approvers are real people, so the reviewer the domain seeds
        // name needs an account of its own.
        var reviewer = new UserAccount("reviewer", "Reviewer", "reviewer@example.test",
            IdentityService.HashPassword(AeroLinkApiFactory.MemberPassword), now);
        db.Add(reviewer);
        db.Add(new ProgramMembership(reviewer.Id, program.Id, ProgramRole.Approver, "test.setup", now));
        await db.SaveChangesAsync();
        return new Fixture(project.Id, release.Id, baseline.Id, approved.Id, draft.Id);
    }

    /// <summary>Nothing was decided about it, so nothing is owed an explanation. The record goes.</summary>
    [Fact]
    public async Task A_draft_nobody_ever_submitted_is_deleted_outright()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        var fixture = await SeedAsync(factory);
        await SignInAsync(client);

        using var deleted = await client.DeleteAsync($"/api/change-requests/{fixture.DraftId}");
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        Assert.False(await db.SystemChangeRequests.AnyAsync(x => x.Id == fixture.DraftId));
    }

    /// <summary>
    /// The signatures are the point. Somebody looking for SRCR-00110 should find that it was approved and then
    /// withdrawn, by whom and why, rather than finding nothing.
    /// </summary>
    [Fact]
    public async Task Work_that_reached_a_reviewer_is_withdrawn_rather_than_deleted_and_stays_readable()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        var fixture = await SeedAsync(factory);
        await SignInAsync(client);

        using var refused = await client.DeleteAsync($"/api/change-requests/{fixture.ApprovedId}");
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Contains("withdraw_instead", await refused.Content.ReadAsStringAsync());

        using var withdrawn = await client.PostAsJsonAsync($"/api/change-requests/{fixture.ApprovedId}/withdraw",
            new { reason = "Superseded by a better approach." });
        Assert.Equal(HttpStatusCode.OK, withdrawn.StatusCode);

        // Still there, still saying what happened to it and what had been signed.
        var detail = await client.GetFromJsonAsync<JsonElement>($"/api/change-requests/{fixture.ApprovedId}");
        Assert.Equal("Withdrawn", detail.GetProperty("state").GetString());
        Assert.NotEmpty(detail.GetProperty("reviewCycles").EnumerateArray());

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        var history = await db.AuditEvents.AsNoTracking()
            .Where(x => x.AggregateId == fixture.ApprovedId && x.EventType == "ChangeRequestWithdrawn")
            .ToListAsync();
        Assert.Contains("Superseded by a better approach.", Assert.Single(history).Detail);
    }

    [Fact]
    public async Task A_withdrawal_reason_is_mandatory()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        var fixture = await SeedAsync(factory);
        await SignInAsync(client);

        using var refused = await client.PostAsJsonAsync($"/api/change-requests/{fixture.ApprovedId}/withdraw",
            new { reason = "   " });
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Contains("reason", await refused.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);

        // Refused means nothing moved: still allocated to the build, still selected by it. Withdrawal takes
        // the work out of an open baseline on its way through, and a refusal must not leave that half done.
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        Assert.Equal(ChangeRequestState.SelectedForBaseline,
            (await db.SystemChangeRequests.AsNoTracking().SingleAsync(x => x.Id == fixture.ApprovedId)).State);
        var baseline = await db.CandidateBaselines.AsNoTracking().Include(x => x.Selections)
            .SingleAsync(x => x.Id == fixture.BaselineId);
        Assert.Contains(baseline.Selections, x => x.ChangeRequestId == fixture.ApprovedId);
    }

    /// <summary>
    /// The whole point of option C: withdrawal from a frozen build is refused, naming the way out; reopening is
    /// the deliberate act that takes the materialized revisions back; and only then does the withdrawal go
    /// through. The requirement introduced by the withdrawn change request ceases to exist along the way.
    /// </summary>
    [Fact]
    public async Task Withdrawing_from_a_frozen_build_is_refused_until_somebody_reopens_it()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        var fixture = await SeedAsync(factory);
        await SignInAsync(client);

        Assert.Equal(HttpStatusCode.OK,
            (await client.PostAsJsonAsync($"/api/baselines/{fixture.BaselineId}/freeze", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.OK,
            (await client.PostAsJsonAsync($"/api/baselines/{fixture.BaselineId}/materialize-requirements", new { })).StatusCode);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            Assert.True(await db.RequirementRevisions.AnyAsync(x => x.EffectiveBaselineId == fixture.BaselineId));
        }

        using var refused = await client.PostAsJsonAsync($"/api/change-requests/{fixture.ApprovedId}/withdraw",
            new { reason = "It was wrong." });
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        var refusal = await refused.Content.ReadAsStringAsync();
        Assert.Contains("baseline_frozen", refusal);
        Assert.Contains("SW-16.00", refusal);

        using var reopened = await client.PostAsJsonAsync($"/api/baselines/{fixture.BaselineId}/reopen",
            new { reason = "SRCR-00110 was wrong and 1.6 has not shipped." });
        Assert.Equal(HttpStatusCode.OK, reopened.StatusCode);
        var taken = (await reopened.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("revisionsTakenBack").GetInt32();
        Assert.Equal(1, taken);

        Assert.Equal(HttpStatusCode.OK,
            (await client.PostAsJsonAsync($"/api/change-requests/{fixture.ApprovedId}/withdraw",
                new { reason = "It was wrong." })).StatusCode);

        using var after = factory.Services.CreateScope();
        var store = after.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        Assert.False(await store.RequirementRevisions.AnyAsync(x => x.EffectiveBaselineId == fixture.BaselineId));
        var baseline = await store.CandidateBaselines.AsNoTracking().SingleAsync(x => x.Id == fixture.BaselineId);
        Assert.Equal(CandidateBaselineState.Draft, baseline.State);
        Assert.Null(baseline.RequirementsMaterializedAt);
    }

    /// <summary>
    /// A released baseline is what the world was told the build contains. The refusal says so rather than
    /// offering a reopen that will itself be refused.
    /// </summary>
    [Fact]
    public async Task Released_work_cannot_be_withdrawn_and_a_released_build_cannot_be_reopened()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        var fixture = await SeedAsync(factory);
        await SignInAsync(client);

        await client.PostAsJsonAsync($"/api/baselines/{fixture.BaselineId}/freeze", new { });
        await client.PostAsJsonAsync($"/api/baselines/{fixture.BaselineId}/materialize-requirements", new { });
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var baseline = await db.CandidateBaselines.SingleAsync(x => x.Id == fixture.BaselineId);
            baseline.MarkReleased(Manager, DateTimeOffset.UtcNow);
            await db.SaveChangesAsync();
        }

        using var withdrawal = await client.PostAsJsonAsync($"/api/change-requests/{fixture.ApprovedId}/withdraw",
            new { reason = "Too late." });
        Assert.Equal(HttpStatusCode.BadRequest, withdrawal.StatusCode);
        Assert.Contains("baseline_released", await withdrawal.Content.ReadAsStringAsync());

        using var reopen = await client.PostAsJsonAsync($"/api/baselines/{fixture.BaselineId}/reopen",
            new { reason = "Too late." });
        Assert.Equal(HttpStatusCode.BadRequest, reopen.StatusCode);
        Assert.Contains("released", await reopen.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);

        // And the revisions it published are still there.
        using var after = factory.Services.CreateScope();
        var store = after.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        Assert.True(await store.RequirementRevisions.AnyAsync(x => x.EffectiveBaselineId == fixture.BaselineId));
    }

    /// <summary>
    /// Builds are sealed in order and must be unsealed in the reverse order. A later build frozen on top of
    /// this one derives from what this one contains, so taking these revisions back underneath it would leave
    /// it sealed around requirement revisions that no longer exist.
    /// </summary>
    [Fact]
    public async Task A_build_with_a_later_build_sealed_on_top_of_it_cannot_be_reopened()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        var fixture = await SeedAsync(factory);
        await SignInAsync(client);

        await client.PostAsJsonAsync($"/api/baselines/{fixture.BaselineId}/freeze", new { });
        await client.PostAsJsonAsync($"/api/baselines/{fixture.BaselineId}/materialize-requirements", new { });

        Guid successorId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var now = DateTimeOffset.UtcNow;
            var next = new SoftwareRelease(fixture.ProjectId, "1.7", false, fixture.ReleaseId);
            var later = new SystemChangeRequest("SRCR-00120", 0, fixture.ProjectId, next.Id,
                "Work in the next build", "P", "A", "S", Author, now);
            later.AddRequirementChange(Author, "SYSR-00000006", 0, RequirementLevel.System,
                RequirementChangeKind.Introduce, "The FMS shall report oceanic position.", "New", "Analysis", now);
            later.SubmitForReview(Author, [new("reviewer", "Reviewer")], now);
            later.ApproveActiveStage("reviewer", now);
            var successor = new CandidateBaseline("SW-17.00", 0, fixture.ProjectId, next.Id,
                fixture.BaselineId, "Build 1.7", Manager, now);
            successor.Select(later, Manager, now);
            successor.Freeze(Manager, now);
            db.AddRange(next, later, successor);
            await db.SaveChangesAsync();
            successorId = successor.Id;
        }

        using var refused = await client.PostAsJsonAsync($"/api/baselines/{fixture.BaselineId}/reopen",
            new { reason = "SRCR-00110 was wrong." });
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        var body = await refused.Content.ReadAsStringAsync();
        Assert.Contains("successor_baseline", body);
        Assert.Contains("SW-17.00", body);
        Assert.NotEqual(Guid.Empty, successorId);

        // Refused means refused: nothing was taken back on the way out.
        using var after = factory.Services.CreateScope();
        var store = after.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        Assert.True(await store.RequirementRevisions.AnyAsync(x => x.EffectiveBaselineId == fixture.BaselineId));
        Assert.Equal(CandidateBaselineState.Frozen,
            (await store.CandidateBaselines.AsNoTracking().SingleAsync(x => x.Id == fixture.BaselineId)).State);
    }

    /// <summary>
    /// A withdrawn change request stops competing for the requirements it wanted. Without this it kept
    /// appearing in another author's contention notices as though it were still racing them, which is the
    /// opposite of what withdrawing it said.
    /// </summary>
    [Fact]
    public async Task A_withdrawn_change_request_stops_contending_for_its_requirements()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        var fixture = await SeedAsync(factory);
        await SignInAsync(client);

        // A holder is a change request that modifies a requirement -- introducing one contends with nobody,
        // because until it is materialized there is nothing there for anybody else to be writing against.
        var holderId = await HoldAsync(factory, fixture);

        // A second author wants the same requirement that holder holds.
        var rivalId = await CreateAsync(client, fixture, "Same requirement");
        using var added = await client.PostAsJsonAsync($"/api/change-requests/{rivalId}/requirements", SameRequirement);
        Assert.Equal(HttpStatusCode.OK, added.StatusCode);
        var contended = await added.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains(contended.GetProperty("contention").EnumerateArray(),
            x => x.GetProperty("displayNumber").GetString() == "SRCR-00112.00");

        Assert.Equal(HttpStatusCode.OK,
            (await client.PostAsJsonAsync($"/api/change-requests/{holderId}/withdraw",
                new { reason = "Superseded by the rival." })).StatusCode);

        // Contention is computed for the author writing, so a third author asking the same question is what
        // shows the withdrawn change request is no longer part of the answer.
        var third = await CreateAsync(client, fixture, "Third author, same requirement");
        using var again = await client.PostAsJsonAsync($"/api/change-requests/{third}/requirements", SameRequirement);
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        var notices = (await again.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("contention").EnumerateArray().ToList();
        Assert.DoesNotContain(notices, x => x.GetProperty("displayNumber").GetString() == "SRCR-00112.00");
        // The rival is still there and still racing, so an empty list would not have proved anything.
        Assert.Contains(notices, x => x.GetProperty("baseNumber").GetString() == "SYSR-00000009");

        // And with nothing holding the requirement, the rival can now go to review.
        using var submitted = await client.PostAsJsonAsync($"/api/change-requests/{rivalId}/submit",
            new { approvers = new[] { new { userId = "reviewer", name = "Reviewer" } } });
        Assert.True(submitted.StatusCode == HttpStatusCode.OK, await submitted.Content.ReadAsStringAsync());
    }

    private static readonly object SameRequirement = new
    {
        baseNumber = "SYSR-00000009", revision = 3, level = "System", kind = "Modify",
        statement = "The FMS shall sequence oceanic waypoints within 2 seconds.",
        rationale = "Tighter", verificationMethod = "Test",
    };

    /// <summary>An approved change request modifying SYSR-00000009, holding it against everybody else.</summary>
    private static async Task<Guid> HoldAsync(AeroLinkApiFactory factory, Fixture fixture)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        var now = DateTimeOffset.UtcNow;
        var holder = new SystemChangeRequest("SRCR-00112", 0, fixture.ProjectId, fixture.ReleaseId,
            "Holds the requirement", "P", "A", "S", Author, now);
        holder.AddRequirementChange(Author, "SYSR-00000009", 3, RequirementLevel.System,
            RequirementChangeKind.Modify, "The FMS shall sequence oceanic waypoints.", "Wording", "Test", now);
        holder.SubmitForReview(Author, [new("reviewer", "Reviewer")], now);
        holder.ApproveActiveStage("reviewer", now);
        db.Add(holder);
        await db.SaveChangesAsync();
        return holder.Id;
    }

    private static async Task<Guid> CreateAsync(HttpClient client, Fixture fixture, string title)
    {
        using var created = await client.PostAsJsonAsync("/api/change-requests", new
        {
            baseNumber = "", projectId = fixture.ProjectId, targetReleaseId = fixture.ReleaseId, title,
            problem = "P", analysis = "A", solution = "S", type = "System",
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        return (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    private static async Task SignInAsync(HttpClient client)
    {
        using var response = await client.PostAsJsonAsync("/api/auth/login",
            new { userName = Author, password = AeroLinkApiFactory.MemberPassword });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
