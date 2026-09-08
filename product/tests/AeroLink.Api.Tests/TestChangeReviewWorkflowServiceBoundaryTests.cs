using AeroLink.Api;
using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Identity;
using AeroLink.Domain.Hierarchy;
using AeroLink.Domain.Programs;
using AeroLink.Domain.Requirements;
using AeroLink.Domain.Verification;
using AeroLink.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace AeroLink.Api.Tests;

public sealed class TestChangeReviewWorkflowServiceBoundaryTests
{
    private const string Password = "Workflow-boundary-password-971!";
    [Fact]
    public async Task Submit_boundary_returns_the_structured_case_refusal_without_starting_a_cycle()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AeroLinkDbContext>()
            .UseSqlite(connection).Options;
        await using var db = new AeroLinkDbContext(options);
        var review = new TestChangeReview(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            TestChangeReviewDiscipline.System, "SYSCR-00001", DateTimeOffset.UtcNow, authorId: "author");
        review.RecordTestChangeRequired("author", DateTimeOffset.UtcNow);
        var service = new TestChangeReviewWorkflowService(db, new IdentityService(db),
            new VerificationImpactService(db), new WorkflowAuthorityService(db));
        var actor = new AuthenticatedUser(Guid.NewGuid(), "author", "Author", "author@example.test", false, []);

        var refusal = await Assert.ThrowsAsync<TestChangeReviewWorkflowException>(() => service.SubmitAsync(review,
            new SubmitTestChangeReviewCommand("approver", []), actor, LegacyLadderPolicy.Instance,
            CancellationToken.None));

        Assert.Equal("test_change_request_case_incomplete", refusal.Code);
        Assert.NotNull(refusal.Fields);
        Assert.NotEmpty(refusal.Fields!);
        Assert.Empty(review.ReviewCycles);
    }

    [Fact]
    public async Task Submit_and_approve_persist_the_frozen_workflow_contract_without_an_api_host()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AeroLinkDbContext>()
            .UseSqlite(connection).Options;
        await using var db = new AeroLinkDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var now = DateTimeOffset.UtcNow;
        var program = new ProgramRecord("Boundary workflow program", "TCRB");
        var project = new ProjectRecord(program.Id, "Boundary workflow project", "Boundary workflow product");
        var release = new SoftwareRelease(project.Id, "9.71", false);
        var author = new UserAccount("boundary.author", "Boundary Author", "boundary.author@example.test",
            IdentityService.HashPassword(Password), now);
        var approver = new UserAccount("boundary.approver", "Boundary Approver", "boundary.approver@example.test",
            IdentityService.HashPassword(Password), now);
        var membership = new ProgramMembership(approver.Id, program.Id, ProgramRole.Approver,
            "boundary-fixture", now);
        var source = new SystemChangeRequest("SRCR-971001", 0, project.Id, release.Id,
            "Boundary source change", "A controlled change needs verification.",
            "The changed behavior is covered by a new procedure.", "Introduce and approve the procedure.",
            author.UserName, now);
        var requirementChange = source.AddRequirementChange(author.UserName, "SYSR-971001", 0,
            RequirementLevel.System, RequirementChangeKind.Introduce,
            "The system shall preserve the controlled boundary.", "New controlled behavior", "Test", now);
        var review = new TestChangeReview(project.Id, release.Id, source.Id,
            TestChangeReviewDiscipline.System, source.DisplayNumber, now,
            baseNumber: "SYSTCCR-971001", authorId: author.UserName);
        review.RecordTestChangeRequired(author.UserName, now);
        review.WriteCase(author.UserName, "Boundary test change package",
            "The source change alters controlled behavior.",
            "A new system procedure is the selected verification response.",
            "Approve the exact proposal and its verification decision.", now);
        review.AddProcedureChange(author.UserName, new TestProcedureChangeDraft(
            "SYS-971001", 0, TestProcedureLevel.System, TestProcedureChangeKind.Introduce,
            "Boundary system procedure", "Verify the controlled boundary.", "Configured boundary fixture.",
            "Exercise the changed behavior.", "The controlled behavior is observed.",
            "The approved source change requires this new procedure.",
            ParentKind: VerificationProcedureParentKind.Derived,
            DerivedRationale: "The procedure is intentionally derived for this boundary fixture."), now);
        var impact = VerificationImpactItem.ForIntroducedRequirement(project.Id, release.Id, source.Id,
            review.Id, requirementChange.Id, requirementChange.DisplayNumber, "Test", now);
        impact.Resolve(author.UserName, VerificationImpactOutcome.NewProcedureRequired,
            "A new system procedure is required and is proposed by this package.", now);

        db.AddRange(program, project, release, author, approver, membership, source, review, impact);
        await db.SaveChangesAsync();

        var service = new TestChangeReviewWorkflowService(db, new IdentityService(db),
            new VerificationImpactService(db), new WorkflowAuthorityService(db));
        var authorActor = new AuthenticatedUser(author.Id, author.UserName, author.DisplayName,
            author.Email, false, []);
        var approverActor = new AuthenticatedUser(approver.Id, approver.UserName, approver.DisplayName,
            approver.Email, false, [new UserProgramAccess(program.Id, [ProgramRole.Approver.ToString()])]);

        var submitted = await service.SubmitAsync(review,
            new SubmitTestChangeReviewCommand(approver.UserName, []), authorActor,
            LegacyLadderPolicy.Instance, CancellationToken.None);

        Assert.Equal(review.Id, submitted.ReviewId);
        Assert.Equal(TestChangeReviewState.InReview, submitted.State);
        db.ChangeTracker.Clear();
        review = await db.TestChangeReviews
            .Include(x => x.ProcedureChanges)
            .Include(x => x.ReviewCycles).ThenInclude(x => x.Steps)
            .SingleAsync(x => x.Id == review.Id);
        var cycle = Assert.Single(review.ReviewCycles);
        var step = Assert.Single(cycle.Steps);
        Assert.Equal(1, cycle.Sequence);
        Assert.Equal(ReviewCycleState.Active, cycle.State);
        Assert.Equal(approver.UserName, step.ApproverId);
        Assert.Equal(ProgramRole.Approver.ToString(), step.Authority);
        Assert.Equal(ProjectAuthoritySource.DirectBaseRole, step.AuthoritySource);
        Assert.Equal(membership.Id, step.AuthoritySourceId);
        var expectedSnapshotHash = review.ComputeSnapshotHashForIdentityMigration([],
            [new VerificationImpactSnapshot(impact.Id, impact.ChangeRequestId, impact.Trigger,
                impact.RequirementChangeId, impact.RequirementRevisionId, impact.ProcedureId,
                impact.SubjectDisplayNumber, impact.Outcome, impact.ProcedureChangeAction,
                impact.ResolutionRationale, impact.ResolvedProcedureId, impact.ResolvedProcedureRevisionId,
                impact.RetargetedRequirementRevisionId, impact.PreReleaseEvidenceRequired)]);
        Assert.Equal(expectedSnapshotHash, cycle.SnapshotHash);
        Assert.Equal(64, cycle.SnapshotHash.Length);

        var submittedNotification = await db.UserNotifications.SingleAsync(x => x.ArtifactId == review.Id);
        Assert.Equal(approver.UserName, submittedNotification.Recipient);
        Assert.Equal("ReviewActivated", submittedNotification.Type);
        Assert.Contains(author.DisplayName, submittedNotification.Detail);

        var approved = await service.ApproveAsync(review,
            new ApproveTestChangeReviewCommand("The frozen package is correct.",
                Password, "I approve this controlled package."),
            approverActor, "192.0.2.71", CancellationToken.None);

        Assert.Equal(review.Id, approved.ReviewId);
        Assert.Equal(TestChangeReviewState.Approved, approved.State);
        Assert.Equal(ReviewCycleState.Approved, cycle.State);
        Assert.All(cycle.Steps, x => Assert.Equal(ApprovalStepState.Approved, x.State));

        var signature = await db.ElectronicSignatures.SingleAsync(x => x.ArtifactId == review.Id);
        Assert.Equal(approver.Id, signature.UserId);
        Assert.Equal(approver.UserName, signature.UserName);
        Assert.Equal(approver.DisplayName, signature.DisplayName);
        Assert.Equal("I approve this controlled package.", signature.Meaning);
        Assert.Equal("TestChangeRequest", signature.ArtifactType);
        Assert.Equal(review.DisplayNumber, signature.ArtifactRevision);
        Assert.Equal(step.Id, signature.ReviewStepId);
        Assert.Equal(cycle.Sequence, signature.ReviewCycle);
        Assert.Equal(step.Position, signature.ReviewStepPosition);
        Assert.Equal(step.Authority, signature.Authority);
        Assert.Equal(step.AuthoritySource.ToString(), signature.AuthoritySource);
        Assert.Equal(step.AuthoritySourceId, signature.AuthoritySourceId);
        Assert.Equal(cycle.SnapshotHash, signature.ContentHash);
        Assert.Equal("192.0.2.71", signature.IpAddress);
        Assert.Equal("The frozen package is correct.", signature.Rationale);

        db.ChangeTracker.Clear();
        var persisted = await db.TestChangeReviews
            .Include(x => x.ProcedureChanges)
            .Include(x => x.ReviewCycles).ThenInclude(x => x.Steps)
            .SingleAsync(x => x.Id == review.Id);
        Assert.Equal(TestChangeReviewState.Approved, persisted.State);
        Assert.Equal(ReviewCycleState.Approved, Assert.Single(persisted.ReviewCycles).State);
    }
}
