using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Identity;
using AeroLink.Domain.Programs;
using AeroLink.Domain.Requirements;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AeroLink.Api.Tests;

/// <summary>
/// Only one change request may have a requirement in front of reviewers at a time.
///
/// The rule these prove is not "one change request per requirement" — two authors may write against the same
/// requirement, and neither is stopped. It is that the first to *submit* takes it, so the approved wording of
/// a controlled requirement never depends on which approval happened to land first.
/// </summary>
public sealed class ArtifactClaimTests(SharedApiHost host) : IClassFixture<SharedApiHost>
{
    [Fact]
    public async Task Submitting_takes_the_claim_and_a_second_change_request_is_refused()
    {
        var world = await SeedAsync(host.Factory);

        await using (var scope = Scope(host.Factory, out var db))
        {
            var claims = await db.ArtifactClaims.Where(x => x.ProjectId == world.ProjectId).ToListAsync();
            var only = Assert.Single(claims);
            Assert.Equal(ArtifactClaimKey.ForRequirement(world.Requirement), only.ArtifactKey);
            Assert.Equal(world.FirstId, only.ChangeRequestId);
        }

        // The second change request is written against the same requirement. Writing is allowed.
        var second = await AddChangeRequestAsync(host.Factory, world, "SRCR-00902", submit: false);

        await using (var scope = Scope(host.Factory, out var db))
        {
            var contenders = await ArtifactClaims.ContendersAsync(db, world.ProjectId, [world.Requirement], second, default);
            var blocking = contenders.Where(x => x.Holds).ToList();
            Assert.Single(blocking);
            Assert.Equal(ChangeRequestState.InReview, blocking[0].State);
            Assert.Contains(world.Requirement, ArtifactClaims.Refusal(blocking));
            Assert.Contains(blocking[0].DisplayNumber, ArtifactClaims.Refusal(blocking));
        }
    }

    [Fact]
    public async Task A_returned_review_releases_the_claim_so_the_other_change_request_can_go()
    {
        var world = await SeedAsync(host.Factory);

        await using (var scope = Scope(host.Factory, out var db))
        {
            var scr = await db.SystemChangeRequests.Include(x => x.RequirementChanges)
                .Include(x => x.ReviewCycles).ThenInclude(x => x.Steps)
                .SingleAsync(x => x.Id == world.FirstId);
            scr.RequestChanges(world.Reviewer, "Latency is asserted, not derived.", DateTimeOffset.UtcNow);
            await db.SaveChangesAsync();
        }

        await using (var scope = Scope(host.Factory, out var db))
            Assert.Empty(await db.ArtifactClaims.Where(x => x.ProjectId == world.ProjectId).ToListAsync());
    }

    [Fact]
    public async Task Deferring_releases_the_claim_because_shelved_work_blocks_nobody()
    {
        var world = await SeedAsync(host.Factory);

        await using (var scope = Scope(host.Factory, out var db))
        {
            var scr = await db.SystemChangeRequests.Include(x => x.RequirementChanges)
                .Include(x => x.ReviewCycles).ThenInclude(x => x.Steps)
                .SingleAsync(x => x.Id == world.FirstId);
            scr.CancelReview(world.Author, "Superseded.", DateTimeOffset.UtcNow);
            scr.Defer(world.Author, "Moved to the next build.", DateTimeOffset.UtcNow);
            await db.SaveChangesAsync();
        }

        await using (var scope = Scope(host.Factory, out var db))
            Assert.Empty(await db.ArtifactClaims.Where(x => x.ProjectId == world.ProjectId).ToListAsync());
    }

    [Fact]
    public async Task An_approved_change_request_keeps_its_claim()
    {
        var world = await SeedAsync(host.Factory);

        await using (var scope = Scope(host.Factory, out var db))
        {
            var scr = await db.SystemChangeRequests
                .Include(x => x.RequirementChanges).Include(x => x.ReviewCycles).ThenInclude(x => x.Steps)
                .SingleAsync(x => x.Id == world.FirstId);
            scr.ApproveActiveStage(world.Reviewer, DateTimeOffset.UtcNow);
            await db.SaveChangesAsync();
            Assert.Equal(ChangeRequestState.Approved, scr.State);
        }

        await using (var scope = Scope(host.Factory, out var db))
            Assert.Single(await db.ArtifactClaims.Where(x => x.ProjectId == world.ProjectId).ToListAsync());
    }

    /// <summary>
    /// The reason the claim is a row with a unique index rather than a query over state.
    ///
    /// Both of these read no holder and both proceed to save. Checking the outcome sequentially would prove
    /// nothing — the point is that the database, not the read, decides which one took the requirement.
    /// </summary>
    [Fact]
    public async Task Two_simultaneous_submissions_for_one_requirement_produce_exactly_one_winner()
    {
        var world = await SeedAsync(host.Factory, submitFirst: false);
        var second = await AddChangeRequestAsync(host.Factory, world, "SRCR-00903", submit: false);

        async Task<bool> SubmitAsync(Guid id)
        {
            try
            {
                await using var scope = Scope(host.Factory, out var db);
                var scr = await db.SystemChangeRequests.Include(x => x.RequirementChanges)
                    .SingleAsync(x => x.Id == id);
                scr.SubmitForReview(world.Author, [new(world.Reviewer, "Reviewer")], DateTimeOffset.UtcNow);
                await db.SaveChangesAsync();
                return true;
            }
            catch (DbUpdateException) { return false; }
        }

        var outcomes = await Task.WhenAll(SubmitAsync(world.FirstId), SubmitAsync(second));

        Assert.Equal(1, outcomes.Count(won => won));
        await using (var scope = Scope(host.Factory, out var db))
        {
            var claims = await db.ArtifactClaims.Where(x => x.ProjectId == world.ProjectId).ToListAsync();
            Assert.Single(claims);
        }
    }

    [Fact]
    public async Task Introducing_a_requirement_claims_nothing_because_nobody_else_can_hold_it()
    {
        var world = await SeedAsync(host.Factory, submitFirst: false, kind: RequirementChangeKind.Introduce);

        await using (var scope = Scope(host.Factory, out var db))
        {
            var scr = await db.SystemChangeRequests.Include(x => x.RequirementChanges)
                .SingleAsync(x => x.Id == world.FirstId);
            scr.SubmitForReview(world.Author, [new(world.Reviewer, "Reviewer")], DateTimeOffset.UtcNow);
            await db.SaveChangesAsync();
        }

        await using (var scope = Scope(host.Factory, out var db))
            Assert.Empty(await db.ArtifactClaims.Where(x => x.ProjectId == world.ProjectId).ToListAsync());
    }

    [Fact]
    public async Task An_author_writing_against_a_contended_requirement_is_warned_in_every_state()
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
            var second = await AddChangeRequestAsync(host.Factory, world, "SRCR-00904", submit: false);

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
    public async Task A_claim_covers_only_the_requirement_it_is_over()
    {
        var world = await SeedAsync(host.Factory);

        // A second change request against a different requirement is not contended at all, and takes its own
        // claim. The claim is over a requirement, not over the change request that holds one.
        await using (var scope = Scope(host.Factory, out var db))
        {
            var scr = new SystemChangeRequest("SRCR-00905", 0, world.ProjectId, world.ReleaseId,
                "Unrelated change", "P", "A", "S", world.Author, DateTimeOffset.UtcNow);
            scr.AddRequirementChange(world.Author, "SYSR-00152", 1, RequirementLevel.System,
                RequirementChangeKind.Modify, "The system shall log the reload.", "Traceability", "Test", DateTimeOffset.UtcNow);
            Assert.Empty((await ArtifactClaims.NoticesAsync(db, scr, default)));
            scr.SubmitForReview(world.Author, [new(world.Reviewer, "Reviewer")], DateTimeOffset.UtcNow);
            db.SystemChangeRequests.Add(scr);
            await db.SaveChangesAsync();
        }

        await using (var scope = Scope(host.Factory, out var db))
        {
            var claims = await db.ArtifactClaims.Where(x => x.ProjectId == world.ProjectId).ToListAsync();
            Assert.Equal(2, claims.Count);
            Assert.Equal(2, claims.Select(x => x.ArtifactKey).Distinct().Count());
        }
    }

    [Fact]
    public async Task The_holder_letting_go_lets_the_waiting_change_request_take_the_requirement()
    {
        foreach (var release in new[] { ChangeRequestState.Draft, ChangeRequestState.Deferred })
        {
            var world = await SeedAsync(host.Factory);
            var second = await AddChangeRequestAsync(host.Factory, world, "SRCR-00906", submit: false);

            // The holder is In Review from the seed, so it is genuinely holding before this releases it.
            await ActOnHolderAsync(host.Factory, world, (scr, now) => scr.CancelReview(world.Author, "Superseded.", now));
            if (release == ChangeRequestState.Deferred)
                await ActOnHolderAsync(host.Factory, world, (scr, now) => scr.Defer(world.Author, "Next build.", now));

            await using (var scope = Scope(host.Factory, out var db))
            {
                var scr = await db.SystemChangeRequests.Include(x => x.RequirementChanges).SingleAsync(x => x.Id == second);
                Assert.Empty((await ArtifactClaims.ContendersAsync(db, world.ProjectId, [world.Requirement], second, default))
                    .Where(x => x.Holds));
                scr.SubmitForReview(world.Author, [new(world.Reviewer, "Reviewer")], DateTimeOffset.UtcNow);
                await db.SaveChangesAsync();
            }

            await using (var scope = Scope(host.Factory, out var db))
            {
                var claim = Assert.Single(await db.ArtifactClaims.Where(x => x.ProjectId == world.ProjectId).ToListAsync());
                Assert.Equal(second, claim.ChangeRequestId);
            }
        }
    }

    private static async Task ActOnHolderAsync(AeroLinkApiFactory factory, World world, Action<SystemChangeRequest, DateTimeOffset> act)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        var scr = await db.SystemChangeRequests
            .Include(x => x.RequirementChanges).Include(x => x.ReviewCycles).ThenInclude(x => x.Steps)
            .SingleAsync(x => x.Id == world.FirstId);
        act(scr, DateTimeOffset.UtcNow);
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Moves the seeded change request into the state a case needs, saving between each transition.
    ///
    /// Batching them into one save is not what the product does — every transition is its own request — and
    /// EF misclassifies a cycle created and then acted on within a single save, which fails in a way that has
    /// nothing to do with what these tests are about.
    /// </summary>
    private static async Task MoveToAsync(AeroLinkApiFactory factory, World world, ChangeRequestState target)
    {
        if (target == ChangeRequestState.Draft) return;

        async Task ActAsync(Action<SystemChangeRequest, DateTimeOffset> act)
        {
            await using var scope = factory.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var scr = await db.SystemChangeRequests
                .Include(x => x.RequirementChanges).Include(x => x.ReviewCycles).ThenInclude(x => x.Steps)
                .SingleAsync(x => x.Id == world.FirstId);
            act(scr, DateTimeOffset.UtcNow);
            await db.SaveChangesAsync();
        }

        await ActAsync((scr, now) => scr.SubmitForReview(world.Author, [new(world.Reviewer, "Reviewer")], now));
        if (target is ChangeRequestState.Approved)
            await ActAsync((scr, now) => scr.ApproveActiveStage(world.Reviewer, now));
        if (target is ChangeRequestState.Deferred)
        {
            await ActAsync((scr, now) => scr.CancelReview(world.Author, "Shelved.", now));
            await ActAsync((scr, now) => scr.Defer(world.Author, "Moved to the next build.", now));
        }
    }

    private sealed record World(Guid ProjectId, Guid ReleaseId, Guid FirstId, string Requirement, string Author, string Reviewer);

    private static AsyncServiceScope Scope(AeroLinkApiFactory factory, out AeroLinkDbContext db)
    {
        var scope = factory.Services.CreateAsyncScope();
        db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        return scope;
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
        // Claims are scoped to a project and every test seeds its own, so a fixed number is unique enough
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

        var scr = new SystemChangeRequest($"SRCR-00901", 0, project.Id, release.Id,
            "First change", "P", "A", "S", author, now);
        scr.AddRequirementChange(author, requirement, 2, RequirementLevel.System, kind,
            "The system shall respond within 1.5 seconds.", "Latency", "Test", now);
        if (submitFirst) scr.SubmitForReview(author, [new(reviewer, "Reviewer")], now);
        db.SystemChangeRequests.Add(scr);
        await db.SaveChangesAsync();
        return new World(project.Id, release.Id, scr.Id, requirement, author, reviewer);
    }

    private static async Task<Guid> AddChangeRequestAsync(AeroLinkApiFactory factory, World world, string number, bool submit)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        var now = DateTimeOffset.UtcNow;
        var scr = new SystemChangeRequest(number, 0, world.ProjectId, world.ReleaseId,
            "Second change", "P", "A", "S", world.Author, now);
        scr.AddRequirementChange(world.Author, world.Requirement, 2, RequirementLevel.System,
            RequirementChangeKind.Modify, "The system shall respond within 1.2 seconds.", "Latency", "Test", now);
        if (submit) scr.SubmitForReview(world.Author, [new(world.Reviewer, "Reviewer")], now);
        db.SystemChangeRequests.Add(scr);
        await db.SaveChangesAsync();
        return scr.Id;
    }
}
