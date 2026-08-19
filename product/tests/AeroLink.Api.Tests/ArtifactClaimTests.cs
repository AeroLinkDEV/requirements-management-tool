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
/// Only one change request may have a requirement in front of reviewers at a time.
///
/// The rule is not "one change request per requirement" — two authors may write against the same requirement
/// and neither is stopped. It is that the first to *submit* takes it, so the approved wording of a controlled
/// requirement never depends on which approval happened to land first.
///
/// Decided by a query at submission rather than by a unique index over a claim row. An index would settle a
/// tie between two submissions in the same instant, which is not a case worth designing for, and it cannot be
/// applied to data that already holds two approved change requests over one requirement — which this
/// project's own database does.
/// </summary>
public sealed class ArtifactClaimTests(SharedApiHost host) : IClassFixture<SharedApiHost>
{
    [Fact]
    public async Task A_change_request_in_review_blocks_the_next_one_and_the_refusal_says_which()
    {
        var world = await SeedAsync(host.Factory);
        var second = await AddChangeRequestAsync(host.Factory, world, "SRCR-00902");

        await using var scope = Scope(host.Factory, out var db);
        var blocking = (await ArtifactClaims.ContendersAsync(db, world.ProjectId, [world.Requirement], second, default))
            .Where(x => x.Holds).ToList();

        var only = Assert.Single(blocking);
        Assert.Equal(ChangeRequestState.InReview, only.State);
        var refusal = ArtifactClaims.Refusal(blocking);
        Assert.Contains(world.Requirement, refusal);
        Assert.Contains(only.DisplayNumber, refusal);
        // Removal exists now (#685) so it is offered. Rebasing still does not, so it is still not named.
        Assert.Contains("Remove the contested", refusal);
        Assert.DoesNotContain("rebase", refusal, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task An_author_is_warned_differently_depending_on_what_the_other_change_request_is_doing()
    {
        foreach (var (state, blocking, fragment) in new[]
                 {
                     (ChangeRequestState.Draft, false, "also drafting"),
                     (ChangeRequestState.InReview, true, "cannot go to review"),
                     (ChangeRequestState.Approved, true, "cannot go to review"),
                     (ChangeRequestState.Deferred, false, "it is deferred"),
                 })
        {
            var world = await SeedAsync(host.Factory, submitFirst: false);
            await MoveToAsync(host.Factory, world, state);
            var second = await AddChangeRequestAsync(host.Factory, world, "SRCR-00903");

            await using var scope = Scope(host.Factory, out var db);
            var contenders = await ArtifactClaims.ContendersAsync(db, world.ProjectId, [world.Requirement], second, default);
            var notice = Assert.Single(contenders.Select(ArtifactClaims.Notice).ToList());
            var json = System.Text.Json.JsonSerializer.Serialize(notice);

            Assert.Contains(fragment, json);
            Assert.Contains($"\"blocking\":{blocking.ToString().ToLowerInvariant()}", json);
            Assert.Contains(world.Requirement, json);
        }
    }

    [Fact]
    public async Task A_holder_that_lets_go_stops_blocking()
    {
        foreach (var release in new[] { ChangeRequestState.Draft, ChangeRequestState.Deferred })
        {
            var world = await SeedAsync(host.Factory);
            var second = await AddChangeRequestAsync(host.Factory, world, "SRCR-00904");

            await ActOnHolderAsync(host.Factory, world, (scr, now) => scr.CancelReview(world.Author, "Superseded.", now));
            if (release == ChangeRequestState.Deferred)
                await ActOnHolderAsync(host.Factory, world, (scr, now) => scr.Defer(world.Author, "Next build.", now));

            await using var scope = Scope(host.Factory, out var db);
            Assert.Empty((await ArtifactClaims.ContendersAsync(db, world.ProjectId, [world.Requirement], second, default))
                .Where(x => x.Holds));
        }
    }

    [Fact]
    public async Task An_approved_change_request_still_blocks_because_its_change_is_pending()
    {
        var world = await SeedAsync(host.Factory, submitFirst: false);
        await MoveToAsync(host.Factory, world, ChangeRequestState.Approved);
        var second = await AddChangeRequestAsync(host.Factory, world, "SRCR-00905");

        await using var scope = Scope(host.Factory, out var db);
        var blocking = (await ArtifactClaims.ContendersAsync(db, world.ProjectId, [world.Requirement], second, default))
            .Where(x => x.Holds).ToList();
        Assert.Equal(ChangeRequestState.Approved, Assert.Single(blocking).State);
    }

    [Fact]
    public async Task Introducing_a_requirement_contends_with_nobody()
    {
        var world = await SeedAsync(host.Factory, kind: RequirementChangeKind.Introduce);

        await using var scope = Scope(host.Factory, out var db);
        var scr = await db.SystemChangeRequests.Include(x => x.RequirementChanges)
            .SingleAsync(x => x.Id == world.FirstId);
        Assert.Empty(await ArtifactClaims.NoticesAsync(db, scr, default));
    }

    [Fact]
    public async Task Contention_is_over_a_requirement_not_over_a_change_request()
    {
        var world = await SeedAsync(host.Factory);

        await using var scope = Scope(host.Factory, out var db);
        var unrelated = new SystemChangeRequest("SRCR-00906", 0, world.ProjectId, world.ReleaseId,
            "Unrelated change", "P", "A", "S", world.Author, DateTimeOffset.UtcNow);
        unrelated.AddRequirementChange(world.Author, "SYSR-00152", 1, RequirementLevel.System,
            RequirementChangeKind.Modify, "The system shall log the reload.", "Traceability", "Test", DateTimeOffset.UtcNow);

        Assert.Empty(await ArtifactClaims.NoticesAsync(db, unrelated, default));
    }

    [Fact]
    public async Task A_test_change_request_contends_for_the_procedures_it_changes()
    {
        var world = await SeedAsync(host.Factory);
        var holder = await AddTestChangeReviewAsync(host.Factory, world, "TCR-00001", submit: true);
        var otherScr = await AddChangeRequestAsync(host.Factory, world, "SRCR-00910");
        var second = await AddTestChangeReviewAsync(host.Factory, world, "TCR-00002", submit: false, sourceChangeRequestId: otherScr);

        await using var scope = Scope(host.Factory, out var db);
        var blocking = (await ArtifactClaims.ProcedureContendersAsync(db, world.ProjectId, [Procedure], second, default))
            .Where(x => x.Holds).ToList();

        var only = Assert.Single(blocking);
        Assert.Equal(holder, only.ChangeRequestId);
        Assert.Equal(ChangeRequestState.InReview, only.State);
        var refusal = ArtifactClaims.Refusal(blocking, "procedures");
        Assert.Contains(Procedure, refusal);
        Assert.Contains("procedures", refusal);
    }

    [Fact]
    public async Task A_drafting_test_change_request_warns_without_blocking()
    {
        var world = await SeedAsync(host.Factory);
        await AddTestChangeReviewAsync(host.Factory, world, "TCR-00003", submit: false);
        var otherScr = await AddChangeRequestAsync(host.Factory, world, "SRCR-00911");
        var second = await AddTestChangeReviewAsync(host.Factory, world, "TCR-00004", submit: false, sourceChangeRequestId: otherScr);

        await using var scope = Scope(host.Factory, out var db);
        var contenders = await ArtifactClaims.ProcedureContendersAsync(db, world.ProjectId, [Procedure], second, default);
        var only = Assert.Single(contenders);
        Assert.False(only.Holds);
        Assert.Contains("also drafting", System.Text.Json.JsonSerializer.Serialize(ArtifactClaims.Notice(only)));
    }

    [Fact]
    public async Task Introducing_a_procedure_contends_with_nobody()
    {
        var world = await SeedAsync(host.Factory);
        // Not submitted: an introduce must name the requirement revisions it verifies before review, and
        // this is about what contends, not about what may be submitted.
        await AddTestChangeReviewAsync(host.Factory, world, "TCR-00005", submit: false,
            kind: TestProcedureChangeKind.Introduce);
        var otherScr = await AddChangeRequestAsync(host.Factory, world, "SRCR-00912");
        var second = await AddTestChangeReviewAsync(host.Factory, world, "TCR-00006", submit: false,
            kind: TestProcedureChangeKind.Introduce, sourceChangeRequestId: otherScr);

        await using var scope = Scope(host.Factory, out var db);
        Assert.Empty(await ArtifactClaims.ProcedureContendersAsync(db, world.ProjectId, [Procedure], second, default));
    }

    private const string Procedure = "TP-00042";

    private static async Task<Guid> AddTestChangeReviewAsync(AeroLinkApiFactory factory, World world, string number,
        bool submit, TestProcedureChangeKind kind = TestProcedureChangeKind.Modify, Guid? sourceChangeRequestId = null)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        var now = DateTimeOffset.UtcNow;
        var review = new TestChangeReview(world.ProjectId, world.ReleaseId, sourceChangeRequestId ?? world.FirstId,
            TestChangeReviewDiscipline.HighLevelSoftware, "SRCR-00901.00", now, number, 0, authorId: world.Author);
        // A procedure change is only proposable once the review has concluded test work is needed.
        review.RecordTestChangeRequired(world.Author, now);
        review.AddProcedureChange(world.Author, new TestProcedureChangeDraft(Procedure, 1,
            TestProcedureLevel.HighLevel, kind, "Reload timing", "Verify the reload budget",
            "FMS powered", "Trigger a reload", "Under 1.5 seconds", "Latency"), now);
        if (submit)
        {
            review.WriteCase(world.Author, "Reload timing", "P", "A", "S", now);
            review.SubmitForReview(world.Author, [new(world.Reviewer, "Reviewer")], true, now);
        }
        db.TestChangeReviews.Add(review);
        await db.SaveChangesAsync();
        return review.Id;
    }

    private sealed record World(Guid ProjectId, Guid ReleaseId, Guid FirstId, string Requirement, string Author, string Reviewer);

    private static AsyncServiceScope Scope(AeroLinkApiFactory factory, out AeroLinkDbContext db)
    {
        var scope = factory.Services.CreateAsyncScope();
        db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        return scope;
    }

    /// <summary>Saves between transitions, because every transition is its own request in the product.</summary>
    private static async Task MoveToAsync(AeroLinkApiFactory factory, World world, ChangeRequestState target)
    {
        if (target == ChangeRequestState.Draft) return;
        await ActOnHolderAsync(factory, world, (scr, now) => scr.SubmitForReview(world.Author, [new(world.Reviewer, "Reviewer")], now));
        if (target is ChangeRequestState.Approved)
            await ActOnHolderAsync(factory, world, (scr, now) => scr.ApproveActiveStage(world.Reviewer, now));
        if (target is ChangeRequestState.Deferred)
        {
            await ActOnHolderAsync(factory, world, (scr, now) => scr.CancelReview(world.Author, "Shelved.", now));
            await ActOnHolderAsync(factory, world, (scr, now) => scr.Defer(world.Author, "Next build.", now));
        }
    }

    private static async Task ActOnHolderAsync(AeroLinkApiFactory factory, World world,
        Action<SystemChangeRequest, DateTimeOffset> act)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        var scr = await db.SystemChangeRequests
            .Include(x => x.RequirementChanges).Include(x => x.ReviewCycles).ThenInclude(x => x.Steps)
            .SingleAsync(x => x.Id == world.FirstId);
        act(scr, DateTimeOffset.UtcNow);
        await db.SaveChangesAsync();
    }

    private static async Task<World> SeedAsync(AeroLinkApiFactory factory, bool submitFirst = true,
        RequirementChangeKind kind = RequirementChangeKind.Modify)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        var now = DateTimeOffset.UtcNow;
        var tag = Guid.NewGuid().ToString("N")[..8];
        var author = $"claim.author.{tag}";
        var reviewer = $"claim.reviewer.{tag}";
        // Contention is scoped to a project and every test seeds its own, so a fixed number is unique enough
        // and keeps the identifier in the PREFIX-00001 form the allocator requires.
        const string requirement = "SYSR-00151";

        var program = new ProgramRecord($"Claim Program {tag}", $"CLM{tag}");
        var project = new ProjectRecord(program.Id, "Software", "Claim Software");
        var release = new SoftwareRelease(project.Id, "1.6", false);
        db.AddRange(program, project, release);
        foreach (var (name, role) in new[] { (author, ProgramRole.Engineer), (reviewer, ProgramRole.Approver) })
        {
            var account = new UserAccount(name, name, $"{name}@example.test",
                IdentityService.HashPassword(AeroLinkApiFactory.MemberPassword), now);
            db.Add(account);
            db.Add(new ProgramMembership(account.Id, program.Id, role, "test.setup", now));
        }

        var scr = new SystemChangeRequest("SRCR-00901", 0, project.Id, release.Id,
            "First change", "P", "A", "S", author, now);
        scr.AddRequirementChange(author, requirement, 2, RequirementLevel.System, kind,
            "The system shall respond within 1.5 seconds.", "Latency", "Test", now);
        if (submitFirst) scr.SubmitForReview(author, [new(reviewer, "Reviewer")], now);
        db.SystemChangeRequests.Add(scr);
        await db.SaveChangesAsync();
        return new World(project.Id, release.Id, scr.Id, requirement, author, reviewer);
    }

    private static async Task<Guid> AddChangeRequestAsync(AeroLinkApiFactory factory, World world, string number)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        var now = DateTimeOffset.UtcNow;
        var scr = new SystemChangeRequest(number, 0, world.ProjectId, world.ReleaseId,
            "Second change", "P", "A", "S", world.Author, now);
        scr.AddRequirementChange(world.Author, world.Requirement, 2, RequirementLevel.System,
            RequirementChangeKind.Modify, "The system shall respond within 1.2 seconds.", "Latency", "Test", now);
        db.SystemChangeRequests.Add(scr);
        await db.SaveChangesAsync();
        return scr.Id;
    }
}
