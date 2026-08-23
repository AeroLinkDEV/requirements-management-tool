using System.Text;
using System.Text.Json;
using AeroLink.Domain.Baselines;
using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Common;
using AeroLink.Domain.Identity;
using AeroLink.Domain.Programs;
using AeroLink.Domain.Requirements;
using AeroLink.Domain.Verification;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AeroLink.Infrastructure.Tests;

/// <summary>The TCR autosave adapter must preserve an author's exact-parent decision until review validation.</summary>
public sealed class ControlledEditingExactParentTests
{
    [Fact]
    public async Task Checked_in_tcr_parent_tamper_stays_draft_and_valid_parent_selection_reaches_review()
    {
        var options = new DbContextOptionsBuilder<AeroLinkDbContext>().UseSqlite("Data Source=:memory:").Options;
        await using var db = new AeroLinkDbContext(options);
        await db.Database.OpenConnectionAsync();
        await db.Database.EnsureCreatedAsync();
        var now = DateTimeOffset.UtcNow;
        var program = new ProgramRecord("Controlled TCR Parent Program", "CTP");
        var project = new ProjectRecord(program.Id, "Controlled TCR Parent Product", "Verification parent test");
        var release = new SoftwareRelease(project.Id, "1.0", false);
        var source = new SystemChangeRequest("SRCR-00901", 0, project.Id, release.Id, "Parent decision",
            "Problem", "Analysis", "Solution", "engineer", now);
        var baseline = new CandidateBaseline("SW-09.01", 0, project.Id, release.Id, null,
            "Parent decision baseline", "engineer", now);
        var artifact = new RequirementArtifact(project.Id, "SYSR-00901", RequirementLevel.System, now);
        var revision = new RequirementRevision(artifact.Id, 0, "The system shall preserve the parent decision.",
            "The controlled-editing fixture needs one exact parent.", "Test", RequirementRevisionState.Active,
            source.Id, baseline.Id, now);
        var review = new TestChangeReview(project.Id, release.Id, source.Id, TestChangeReviewDiscipline.System,
            source.DisplayNumber, now, "SYSTPCR-000901");
        review.RecordTestChangeRequired("engineer", now);
        review.AssignControlledNumber("SYSTPCR-000901", now);
        db.AddRange(program, project, release, source, baseline, artifact, revision,
            new BaselineRequirementSelection(baseline.Id, artifact.Id, revision.Id), review);
        await db.SaveChangesAsync();

        var actor = new AuthenticatedUser(Guid.NewGuid(), "engineer", "Engineer", "engineer@example.test",
            false, [new UserProgramAccess(program.Id, [ProgramRole.Engineer.ToString()])]);
        var adapter = new TestChangeRequestControlledEditingAdapter(db);
        var engine = new ControlledEditingCheckInEngine(db, new IdentityService(db), [adapter]);

        // Checkout and autosave a malformed neither-parent proposal. The adapter intentionally allows the
        // half-written draft to be stored; the aggregate's review gate must reject it later.
        var firstSession = await OpenSessionAsync(db, adapter, review, actor, now);
        var malformed = Draft(review, parentKind: "Allocated", parentIds: "[]", driving: "[]",
            derivedRationale: "");
        await AutosaveAsync(db, firstSession, malformed, actor.UserName, now.AddMinutes(1));
        var malformedResult = await engine.CheckInAsync(firstSession.Id, firstSession.Version, actor,
            now.AddMinutes(2), default);
        Assert.True(malformedResult.Success, malformedResult.Error);
        Assert.Equal(TestChangeReviewState.Draft, (await db.TestChangeReviews.SingleAsync(x => x.Id == review.Id)).State);
        db.ChangeTracker.Clear();
        var malformedReview = await db.TestChangeReviews.Include(x => x.ProcedureChanges)
            .SingleAsync(x => x.Id == review.Id);
        var malformedException = Assert.Throws<DomainException>(() => malformedReview.Submit(
            "engineer", "reviewer", true, now.AddMinutes(3)));
        Assert.Contains("exact parent", malformedException.Message, StringComparison.OrdinalIgnoreCase);

        // A fresh autosave replaces the malformed procedure change with a complete Allocated decision. The
        // exact parent and rationale survive the same remove/recreate adapter path and can now be submitted.
        var secondSession = await OpenSessionAsync(db, adapter, review, actor, now.AddMinutes(4));
        var valid = Draft(review, parentKind: "Allocated", parentIds: JsonSerializer.Serialize(new[] { revision.Id }),
            driving: JsonSerializer.Serialize(new[] { revision.Id }), derivedRationale: "");
        await AutosaveAsync(db, secondSession, valid, actor.UserName, now.AddMinutes(5));
        var validResult = await engine.CheckInAsync(secondSession.Id, secondSession.Version, actor,
            now.AddMinutes(6), default);
        Assert.True(validResult.Success, validResult.Error);

        db.ChangeTracker.Clear();
        var submitted = await db.TestChangeReviews.Include(x => x.ProcedureChanges)
            .SingleAsync(x => x.Id == review.Id);
        var storedChange = Assert.Single(submitted.ProcedureChanges);
        Assert.Equal(VerificationProcedureParentKind.Allocated, storedChange.ParentKind);
        Assert.Equal(JsonSerializer.Serialize(new[] { revision.Id }), storedChange.ParentRevisionIdsJson);
        submitted.Submit("engineer", "reviewer", true, now.AddMinutes(7));
        Assert.Equal(TestChangeReviewState.InReview, submitted.State);

        static async Task<ArtifactEditSession> OpenSessionAsync(AeroLinkDbContext db,
            TestChangeRequestControlledEditingAdapter adapter, TestChangeReview review,
            AuthenticatedUser actor, DateTimeOffset now)
        {
            var artifact = await adapter.ResolveAsync(review.Id, default) ?? throw new InvalidOperationException();
            var snapshot = adapter.CanonicalSnapshot(artifact);
            var hash = EnterpriseRequirementsService.Hash(Encoding.UTF8.GetBytes(snapshot));
            var session = new ArtifactEditSession(review.ProjectId, "TestChangeRequest", review.Id, null,
                hash, snapshot, actor.UserName, now, true, 15);
            db.ArtifactEditSessions.Add(session);
            db.ArtifactDraftSnapshots.Add(new ArtifactDraftSnapshot(review.ProjectId, session.Id,
                "TestChangeRequest", review.Id, 1, snapshot, hash, actor.UserName, now));
            await db.SaveChangesAsync();
            return session;
        }

        static async Task AutosaveAsync(AeroLinkDbContext db, ArtifactEditSession session, string json,
            string actor, DateTimeOffset now)
        {
            session.Save(json, session.Version, now, 15);
            var hash = EnterpriseRequirementsService.Hash(Encoding.UTF8.GetBytes(json));
            db.ArtifactDraftSnapshots.Add(new ArtifactDraftSnapshot(session.ProjectId, session.Id,
                "TestChangeRequest", session.ArtifactId, session.Version, json, hash, actor, now));
            await db.SaveChangesAsync();
        }

        static string Draft(TestChangeReview review, string parentKind, string parentIds,
            string driving, string derivedRationale) => JsonSerializer.Serialize(new
            {
                title = "Controlled parent decision",
                problem = "The procedure must carry an exact parent decision.",
                analysis = "The parent is selected through the controlled TCR.",
                solution = "Submit the selected exact parent.",
                procedureChanges = new[]
                {
                    new
                    {
                        baseNumber = "SYSTP-000901", revision = 0, level = "System", kind = "Introduce",
                        title = "Verify the parent decision", objective = "Exercise the exact parent",
                        preconditions = "The build is available.", steps = "Run the verification.",
                        expectedResult = "The behavior is correct.", rationale = "The procedure is controlled.",
                        drivingRequirementRevisionIdsJson = driving,
                        removedRequirementRevisionIdsJson = "[]", coverageChangeRationale = "",
                        parentKind, parentRevisionIdsJson = parentIds, derivedRationale,
                    }
                }
            });
    }
}
