using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AeroLink.Domain.Baselines;
using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Identity;
using AeroLink.Domain.Programs;
using AeroLink.Domain.Releases;
using AeroLink.Domain.Requirements;
using AeroLink.Domain.Verification;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AeroLink.Api.Tests;

/// <summary>
/// What reopening a build disturbs, and how it says so before it does it.
///
/// Reopening is where option C puts the consequences the issue describes. Withdrawal cannot reach a
/// materialized revision at all -- it is refused while the build is frozen -- so nothing downstream can be
/// stranded by it. Reopening is the act that takes the revisions back, and therefore the act that strands
/// whatever was written against them.
/// </summary>
public sealed class ReopenBaselineConsequencesApiTests
{
    private sealed record Fixture(Guid ProjectId, Guid ReleaseId, Guid EarlierBaselineId, Guid BaselineId,
        Guid StrandedDraftId, Guid StrandedReviewId, Guid CarriedProcedureRevisionId,
        Guid OrphanedProcedureRevisionId, Guid OrphanedProcedureId, Guid ReboundProcedureRevisionId);

    private const string Author = "reopen.author";

    /// <summary>
    /// Two builds. The first introduces SYSR-00000005 and is where SYSTP-00000801 was written. The second
    /// modifies it and introduces SYSR-00000006, and is the one that gets reopened.
    ///
    /// Everything that makes the reopen interesting hangs off the second build: a procedure written against
    /// its wording, a draft numbered onto it, and a review in flight against a requirement it introduced.
    /// </summary>
    private static async Task<Fixture> SeedAsync(AeroLinkApiFactory factory, HttpClient client)
    {
        Guid projectId, earlierId, baselineId, releaseId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var now = DateTimeOffset.UtcNow;
            var program = new ProgramRecord("Reopen Program", "ROP");
            var project = new ProjectRecord(program.Id, "Software", "Reopen Software");
            var first = new SoftwareRelease(project.Id, "1.6", false);
            var second = new SoftwareRelease(project.Id, "1.7", false, first.Id);

            // An active schema means materialization writes an authoring profile for every revision it
            // creates, which is what a real project looks like.
            var schema = new ArtifactSchemaDefinition(project.Id, "reopen-schema", "Reopen schema", "System", "", "test.setup", now);

            var introduce = new SystemChangeRequest("SRCR-00110", 0, project.Id, first.Id,
                "Oceanic sequencing", "P", "A", "S", Author, now);
            introduce.AddRequirementChange(Author, "SYSR-00000005", 1, RequirementLevel.System,
                RequirementChangeKind.Introduce, "The FMS shall sequence oceanic waypoints.", "New", "Test", now);
            Approve(introduce, now);

            var earlier = new CandidateBaseline("SW-16.00", 0, project.Id, first.Id, null, "Build 1.6", Author, now);
            earlier.Select(introduce, Author, now);

            var modify = new SystemChangeRequest("SRCR-00120", 0, project.Id, second.Id,
                "Tighter sequencing", "P", "A", "S", Author, now);
            modify.AddRequirementChange(Author, "SYSR-00000005", 2, RequirementLevel.System,
                RequirementChangeKind.Modify, "The FMS shall sequence oceanic waypoints within 2 seconds.", "Tighter", "Test", now);
            modify.AddRequirementChange(Author, "SYSR-00000006", 1, RequirementLevel.System,
                RequirementChangeKind.Introduce, "The FMS shall annunciate a sequencing failure.", "New", "Test", now);
            Approve(modify, now);

            var baseline = new CandidateBaseline("SW-17.00", 0, project.Id, second.Id, earlier.Id, "Build 1.7", Author, now);
            baseline.Select(modify, Author, now);

            db.AddRange(program, project, first, second, schema, introduce, earlier, modify, baseline);

            var account = new UserAccount(Author, "Reopen Author", $"{Author}@example.test",
                IdentityService.HashPassword(AeroLinkApiFactory.MemberPassword), now);
            db.Add(account);
            foreach (var role in new[] { ProgramRole.Engineer, ProgramRole.ConfigurationManager })
                db.Add(new ProgramMembership(account.Id, program.Id, role, "test.setup", now));
            var reviewer = new UserAccount("reviewer", "Reviewer", "reviewer@example.test",
                IdentityService.HashPassword(AeroLinkApiFactory.MemberPassword), now);
            db.Add(reviewer);
            db.Add(new ProgramMembership(reviewer.Id, program.Id, ProgramRole.Approver, "test.setup", now));
            await db.SaveChangesAsync();
            projectId = project.Id; earlierId = earlier.Id; baselineId = baseline.Id; releaseId = second.Id;
        }

        await SignInAsync(client);
        await SealAsync(client, earlierId);

        // SYSTP-00000801 is written against SYSR-00000005.01, before the build that changes it. Materializing the
        // second build carries its coverage forward onto .02 as suspect and leaves the .01 link alone.
        Guid carriedRevisionId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var now = DateTimeOffset.UtcNow;
            var first = await db.RequirementRevisions.AsNoTracking()
                .SingleAsync(x => x.EffectiveBaselineId == earlierId);
            var procedure = new TestProcedure(projectId, "SYSTP-00000801", "Oceanic sequencing", "test.author", now, TestProcedureLevel.System);
            var revision = new TestProcedureRevision(procedure.Id, 1, "Objective", "Preconditions",
                "Steps", "Expected", TestProcedureState.Approved, "test.author", now);
            db.AddRange(procedure, revision);
            db.TestCoverage.Add(new TestRequirementCoverage(revision.Id, first.Id));
            await db.SaveChangesAsync();
            carriedRevisionId = revision.Id;
        }

        await SealAsync(client, baselineId);

        // Two procedures written afterwards, against wording the second build created. Nothing carried either
        // of them there, so nothing takes them back: these are the two the reopen has to say something about,
        // and they are the two different somethings. SYSTP-00000802 covers a requirement the reopen removes
        // altogether and is left covering nothing; SYSTP-00000803 covers one that returns to earlier wording.
        Guid orphanedRevisionId, orphanedProcedureId, reboundRevisionId, strandedDraftId, strandedReviewId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var now = DateTimeOffset.UtcNow;
            var introduced = await db.RequirementRevisions.AsNoTracking()
                .SingleAsync(x => x.EffectiveBaselineId == baselineId && x.Revision == 1);
            var modified = await db.RequirementRevisions.AsNoTracking()
                .SingleAsync(x => x.EffectiveBaselineId == baselineId && x.Revision == 2);

            var orphaned = new TestProcedure(projectId, "SYSTP-00000802", "Failure annunciation", "test.author", now, TestProcedureLevel.System);
            var orphanedRevision = new TestProcedureRevision(orphaned.Id, 1, "Objective", "Preconditions",
                "Steps", "Expected", TestProcedureState.Approved, "test.author", now);
            var rebound = new TestProcedure(projectId, "SYSTP-00000803", "Two second sequencing", "test.author", now, TestProcedureLevel.System);
            var reboundRevision = new TestProcedureRevision(rebound.Id, 1, "Objective", "Preconditions",
                "Steps", "Expected", TestProcedureState.Approved, "test.author", now);
            db.AddRange(orphaned, orphanedRevision, rebound, reboundRevision);
            db.TestCoverage.Add(new TestRequirementCoverage(orphanedRevision.Id, introduced.Id));
            db.TestCoverage.Add(new TestRequirementCoverage(reboundRevision.Id, modified.Id));

            // Numbered onto revision 02, which the reopen takes back.
            var draft = new SystemChangeRequest("SRCR-00130", 0, projectId, releaseId,
                "Tighter still", "P", "A", "S", Author, now);
            draft.AddRequirementChange(Author, "SYSR-00000005", 3, RequirementLevel.System,
                RequirementChangeKind.Modify, "The FMS shall sequence oceanic waypoints within 1 second.", "Tighter", "Test", now);

            // In front of approvers, against a requirement the reopen removes altogether.
            var review = new SystemChangeRequest("SRCR-00140", 0, projectId, releaseId,
                "Annunciation wording", "P", "A", "S", Author, now);
            review.AddRequirementChange(Author, "SYSR-00000006", 2, RequirementLevel.System,
                RequirementChangeKind.Modify, "The FMS shall annunciate a sequencing failure within 1 second.", "Clearer", "Test", now);
            review.SubmitForReview(Author, [new("reviewer", "Reviewer")], now);
            // A reviewer part-way through reading it. Cancelling a cycle publishes whatever draft remarks were
            // outstanding, so this is what proves the reopen does not quietly discard somebody's writing.
            review.AddReviewComment("reviewer", ReviewCommentAnchor.ChangeCase, null,
                "Half a second is tighter than the alerting budget allows.", now);

            db.AddRange(draft, review);
            await db.SaveChangesAsync();
            orphanedRevisionId = orphanedRevision.Id; orphanedProcedureId = orphaned.Id; reboundRevisionId = reboundRevision.Id;
            strandedDraftId = draft.Id; strandedReviewId = review.Id;
        }

        return new Fixture(projectId, releaseId, earlierId, baselineId, strandedDraftId, strandedReviewId,
            carriedRevisionId, orphanedRevisionId, orphanedProcedureId, reboundRevisionId);
    }

    /// <summary>
    /// The reproduction. A build whose requirements carry authoring profiles and test coverage is what every
    /// real build looks like, and reopening one has to take those back with the revisions rather than leaving
    /// the database to refuse the delete.
    /// </summary>
    [Fact]
    public async Task Reopening_takes_back_the_profiles_and_coverage_that_materializing_created()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        var fixture = await SeedAsync(factory, client);

        using var reopened = await client.PostAsJsonAsync($"/api/baselines/{fixture.BaselineId}/reopen",
            new { reason = "SRCR-00120 was wrong and 1.7 has not shipped." });
        Assert.True(reopened.StatusCode == HttpStatusCode.OK, await reopened.Content.ReadAsStringAsync());

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        Assert.False(await db.RequirementRevisions.AnyAsync(x => x.EffectiveBaselineId == fixture.BaselineId));

        // The first build revision is untouched, and so is the coverage written against it.
        var surviving = await db.RequirementRevisions.AsNoTracking()
            .SingleAsync(x => x.EffectiveBaselineId == fixture.EarlierBaselineId);
        Assert.True(await db.TestCoverage.AnyAsync(
            x => x.ProcedureRevisionId == fixture.CarriedProcedureRevisionId && x.RequirementRevisionId == surviving.Id));

        // Nothing is left pointing at a revision that no longer exists.
        var revisionIds = await db.RequirementRevisions.AsNoTracking().Select(x => x.Id).ToListAsync();
        Assert.All(await db.TestCoverage.AsNoTracking().ToListAsync(),
            x => Assert.Contains(x.RequirementRevisionId, revisionIds));
        Assert.All(await db.RequirementRevisionProfiles.AsNoTracking().ToListAsync(),
            x => Assert.Contains(x.RevisionId, revisionIds));
    }

    /// <summary>
    /// A procedure written against wording the reopen takes back covers the earlier wording again, and says so
    /// by being suspect. Silently dropping the link would read as "nothing to verify" rather than
    /// "verification needs rechecking", which is the distinction the suspect flag exists to make.
    ///
    /// The procedure whose coverage this build carried forward is the control: it is left exactly as it was
    /// before the build, unsuspected, because the link it was copied from was never touched.
    /// </summary>
    [Fact]
    public async Task A_procedure_written_against_taken_back_wording_returns_to_the_earlier_revision_as_suspect()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        var fixture = await SeedAsync(factory, client);

        using var reopened = await client.PostAsJsonAsync($"/api/baselines/{fixture.BaselineId}/reopen",
            new { reason = "SRCR-00120 was wrong and 1.7 has not shipped." });
        Assert.True(reopened.StatusCode == HttpStatusCode.OK, await reopened.Content.ReadAsStringAsync());

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        var restored = await db.RequirementRevisions.AsNoTracking()
            .SingleAsync(x => x.EffectiveBaselineId == fixture.EarlierBaselineId);

        var rebound = await db.TestCoverage.AsNoTracking()
            .SingleAsync(x => x.ProcedureRevisionId == fixture.ReboundProcedureRevisionId);
        Assert.Equal(restored.Id, rebound.RequirementRevisionId);
        Assert.True(rebound.IsSuspect);
        Assert.Contains("SW-17.00", rebound.SuspectReason);
        Assert.Contains("SYSR-00000005.01", rebound.SuspectReason);

        // The carried-forward copy is gone and the original is back to being the only link, exactly as it was
        // before the build that was reopened.
        var carried = await db.TestCoverage.AsNoTracking()
            .SingleAsync(x => x.ProcedureRevisionId == fixture.CarriedProcedureRevisionId);
        Assert.Equal(restored.Id, carried.RequirementRevisionId);
        Assert.False(carried.IsSuspect);

        // And the one whose requirement ceased to exist has nothing left to point at.
        Assert.False(await db.TestCoverage.AnyAsync(x => x.ProcedureRevisionId == fixture.OrphanedProcedureRevisionId));
    }

    /// <summary>
    /// The two dependents. A draft numbered onto a revision that is going is flagged and left for its author,
    /// because re-pointing it would assert they wrote their words against text they never read. One in front
    /// of approvers has its review cancelled as well: they were asked about a change against a revision that
    /// no longer exists.
    /// </summary>
    [Fact]
    public async Task Dependents_are_flagged_and_a_dependent_review_is_cancelled_back_to_draft()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        var fixture = await SeedAsync(factory, client);

        using var reopened = await client.PostAsJsonAsync($"/api/baselines/{fixture.BaselineId}/reopen",
            new { reason = "SRCR-00120 was wrong and 1.7 has not shipped." });
        Assert.True(reopened.StatusCode == HttpStatusCode.OK, await reopened.Content.ReadAsStringAsync());

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();

        var draft = await db.SystemChangeRequests.AsNoTracking().SingleAsync(x => x.Id == fixture.StrandedDraftId);
        Assert.Equal(ChangeRequestState.Draft, draft.State);
        Assert.NotNull(draft.RebaseRequiredReason);
        Assert.Contains("SW-17.00", draft.RebaseRequiredReason);
        Assert.Contains("SYSR-00000005", draft.RebaseRequiredReason);

        var review = await db.SystemChangeRequests.AsNoTracking()
            .Include(x => x.ReviewCycles).SingleAsync(x => x.Id == fixture.StrandedReviewId);
        Assert.Equal(ChangeRequestState.Draft, review.State);
        Assert.NotNull(review.RebaseRequiredReason);
        Assert.DoesNotContain(review.ReviewCycles, x => x.State == ReviewCycleState.Active);

        // A cancelled review still produced reading. The remark its reviewer had in draft is published rather
        // than lost, which only happens if the reopen loaded the cycle deeply enough to see it.
        var remark = Assert.Single(await db.ReviewComments.AsNoTracking()
            .Where(x => x.AuthorId == "reviewer").ToListAsync());
        Assert.Equal(ReviewCommentState.Published, remark.State);

        // The change request that was in the reopened build is not a dependent of it. It is selected into the
        // build rather than written against its result, and flagging it would be telling its author their own
        // work stranded them.
        var inTheBuild = await db.SystemChangeRequests.AsNoTracking().SingleAsync(x => x.BaseNumber == "SRCR-00120");
        Assert.Null(inTheBuild.RebaseRequiredReason);
    }

    /// <summary>
    /// The preview and the act are the same computation, so this asserts they produce the same words rather
    /// than merely agreeing about the counts. A preview assembled by its own query drifts the first time
    /// either side changes, and nothing fails while it does.
    /// </summary>
    [Fact]
    public async Task The_preview_says_exactly_what_the_reopen_then_does()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        var fixture = await SeedAsync(factory, client);

        var preview = await client.GetFromJsonAsync<JsonElement>($"/api/baselines/{fixture.BaselineId}/reopen-preview");
        Assert.True(preview.GetProperty("available").GetBoolean());
        var predicted = preview.GetProperty("consequences");

        // Everything the issue asks the confirmation to state, before anything has happened.
        Assert.Equal(["SYSR-00000005.02", "SYSR-00000006.01"],
            predicted.GetProperty("revisionsTakenBack").EnumerateArray().Select(x => x.GetString()!).ToArray());
        Assert.Equal(["SYSR-00000006"],
            predicted.GetProperty("requirementsRemoved").EnumerateArray().Select(x => x.GetString()!).ToArray());
        var stranded = predicted.GetProperty("strandedChangeRequests").EnumerateArray().ToList();
        Assert.Equal(["SRCR-00130.00", "SRCR-00140.00"],
            stranded.Select(x => x.GetProperty("displayNumber").GetString()!).Order().ToArray());
        Assert.Contains(stranded, x => x.GetProperty("displayNumber").GetString() == "SRCR-00140.00"
            && x.GetProperty("reviewWillBeCancelled").GetBoolean());
        Assert.Equal(2, predicted.GetProperty("disturbedCoverage").GetArrayLength());

        using var reopened = await client.PostAsJsonAsync($"/api/baselines/{fixture.BaselineId}/reopen",
            new { reason = "SRCR-00120 was wrong and 1.7 has not shipped." });
        Assert.True(reopened.StatusCode == HttpStatusCode.OK, await reopened.Content.ReadAsStringAsync());
        var performed = (await reopened.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("consequences");

        Assert.Equal(predicted.GetRawText(), performed.GetRawText());

        // And once it has happened there is nothing left to preview, which is the same answer read twice.
        var after = await client.GetFromJsonAsync<JsonElement>($"/api/baselines/{fixture.BaselineId}/reopen-preview");
        Assert.False(after.GetProperty("available").GetBoolean());
        Assert.Equal("not_frozen", after.GetProperty("code").GetString());
    }

    /// <summary>
    /// The point of #694. A procedure the reopen leaves covering nothing becomes work in somebody's queue,
    /// not a sentence in a dialog that closes.
    ///
    /// It is the same finding a retirement produces and it goes to the same queue by the same route, but it
    /// carries the baseline that caused it: a reopen is somebody deciding about the build, not the change
    /// request deciding anything, and the change request it names has itself been taken back.
    /// </summary>
    [Fact]
    public async Task A_procedure_left_covering_nothing_becomes_work_that_names_the_reopened_build()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        var fixture = await SeedAsync(factory, client);

        using var reopened = await client.PostAsJsonAsync($"/api/baselines/{fixture.BaselineId}/reopen",
            new { reason = "SRCR-00120 was wrong and 1.7 has not shipped." });
        Assert.True(reopened.StatusCode == HttpStatusCode.OK, await reopened.Content.ReadAsStringAsync());

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        var orphan = Assert.Single(await db.VerificationImpactItems.AsNoTracking()
            .Where(x => x.Trigger == VerificationImpactTrigger.ProcedureOrphaned).ToListAsync());

        // The procedure whose requirement ceased to exist, and only that one.
        Assert.Equal("SYSTP-00000802", orphan.SubjectDisplayNumber);
        Assert.Equal(fixture.OrphanedProcedureId, orphan.ProcedureId);
        Assert.Equal(fixture.BaselineId, orphan.CausingBaselineId);
        Assert.Equal(VerificationImpactState.Open, orphan.State);

        // Routed to the discipline that answers for a System procedure.
        var review = await db.TestChangeReviews.AsNoTracking().SingleAsync(x => x.Id == orphan.TestChangeReviewId);
        Assert.Equal(TestChangeReviewDiscipline.System, review.Discipline);

        // The two that still verify something are not in the queue. One kept its earlier link untouched; the
        // other was moved back onto earlier wording and is suspect, which is a different finding.
        Assert.DoesNotContain(await db.VerificationImpactItems.AsNoTracking()
            .Where(x => x.Trigger == VerificationImpactTrigger.ProcedureOrphaned)
            .Select(x => x.SubjectDisplayNumber).ToListAsync(),
            x => x is "SYSTP-00000801" or "SYSTP-00000803");
    }

    /// <summary>
    /// A procedure already waiting on somebody is not handed to them twice. Whatever raised the first item,
    /// the second reopen has nothing new to say about it.
    /// </summary>
    [Fact]
    public async Task A_procedure_already_waiting_on_somebody_is_not_raised_a_second_time()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        var fixture = await SeedAsync(factory, client);

        Guid existing;
        using (var before = factory.Services.CreateScope())
        {
            var db = before.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var now = DateTimeOffset.UtcNow;
            var scr = await db.SystemChangeRequests.AsNoTracking().SingleAsync(x => x.BaseNumber == "SRCR-00120");
            var review = new TestChangeReview(fixture.ProjectId, fixture.ReleaseId, scr.Id,
                TestChangeReviewDiscipline.System, "SRCR-00120.00", now);
            db.Add(review);
            var item = VerificationImpactItem.ForOrphanedProcedure(fixture.ProjectId, fixture.ReleaseId, scr.Id,
                review.Id, fixture.OrphanedProcedureId, "SYSTP-00000802", now);
            db.Add(item);
            await db.SaveChangesAsync();
            existing = item.Id;
        }

        using var reopened = await client.PostAsJsonAsync($"/api/baselines/{fixture.BaselineId}/reopen",
            new { reason = "SRCR-00120 was wrong and 1.7 has not shipped." });
        Assert.True(reopened.StatusCode == HttpStatusCode.OK, await reopened.Content.ReadAsStringAsync());

        using var scope = factory.Services.CreateScope();
        var db2 = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        var item2 = Assert.Single(await db2.VerificationImpactItems.AsNoTracking()
            .Where(x => x.Trigger == VerificationImpactTrigger.ProcedureOrphaned).ToListAsync());
        Assert.Equal(existing, item2.Id);
        // Still the one that was already there, so it was left alone rather than replaced.
        Assert.Null(item2.CausingBaselineId);
    }

    private static void Approve(SystemChangeRequest scr, DateTimeOffset now)
    {
        scr.SubmitForReview(Author, [new("reviewer", "Reviewer")], now);
        scr.ApproveActiveStage("reviewer", now);
    }

    private static async Task SealAsync(HttpClient client, Guid baselineId)
    {
        using var frozen = await client.PostAsJsonAsync($"/api/baselines/{baselineId}/freeze", new { });
        Assert.True(frozen.StatusCode == HttpStatusCode.OK, await frozen.Content.ReadAsStringAsync());
        using var materialized = await client.PostAsJsonAsync($"/api/baselines/{baselineId}/materialize-requirements", new { });
        Assert.True(materialized.StatusCode == HttpStatusCode.OK, await materialized.Content.ReadAsStringAsync());
    }

    private static async Task SignInAsync(HttpClient client)
    {
        using var response = await client.PostAsJsonAsync("/api/auth/login",
            new { userName = Author, password = AeroLinkApiFactory.MemberPassword });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
