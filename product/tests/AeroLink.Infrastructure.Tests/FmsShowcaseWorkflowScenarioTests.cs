using System.Text.Json;
using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Requirements;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AeroLink.Infrastructure.Tests;

[Collection(ShowcaseCollection.Name)]
public sealed class FmsShowcaseWorkflowScenarioTests(ShowcaseDatabaseFixture showcase)
{
    [Fact]
    public async Task Editorial_examples_have_a_native_approval_holder_and_an_independent_assessment_approver()
    {
        using var database = showcase.Create();
        await using var db = database.Context();
        var markers = await db.ShowcaseUpgradeSteps.Where(x => x.ProgramId == showcase.Summary.ProgramId
            && x.StepKey.StartsWith(FmsShowcaseSeeder.WorkflowScenarioPrefix)).ToListAsync();
        Assert.Equal(2, markers.Count);
        foreach (var marker in markers)
        {
            using var identity = JsonDocument.Parse(marker.Detail);
            var id = identity.RootElement.GetProperty("RequestId").GetGuid();
            var request = await db.SystemChangeRequests.Include(x => x.RequirementChanges)
                .Include(x => x.ReviewCycles).ThenInclude(x => x.Steps).SingleAsync(x => x.Id == id);
            var originalId = identity.RootElement.GetProperty("RequirementRevisionId").GetGuid();
            var original = await db.RequirementRevisions.SingleAsync(x => x.Id == originalId);
            Assert.Equal(original.Statement, Assert.Single(request.RequirementChanges).Statement);
            var cycle = Assert.Single(request.ReviewCycles);
            Assert.NotNull(cycle.WorkflowId);
            var notifications = await db.UserNotifications.Where(x => x.ArtifactId == id).ToListAsync();
            Assert.Equal(3, notifications.Count);
            Assert.Equal(cycle.Steps.Select(x => x.ApproverId).Order(), notifications.Select(x => x.Recipient).Order());
            var signatures = await db.ElectronicSignatures.Where(x => x.ArtifactId == id).ToListAsync();
            Assert.All(signatures, signature =>
            {
                Assert.StartsWith("Synthetic showcase fixture", signature.Meaning);
                Assert.Equal(cycle.SnapshotHash, signature.ContentHash);
                Assert.NotNull(signature.AuthoritySourceId);
                Assert.True(signature.SignedAt <= DateTimeOffset.UtcNow);
            });
            if (marker.StepKey.EndsWith("awaiting-approval", StringComparison.Ordinal))
            {
                Assert.Equal(ChangeRequestState.InReview, request.State);
                var active = Assert.Single(cycle.Steps, x => x.State == ApprovalStepState.Active);
                Assert.Equal(ReviewStageKind.Approval, active.StageKind);
                Assert.Equal("software.lead", active.ApproverId);
                Assert.Equal(2, signatures.Count);
                Assert.DoesNotContain(signatures, x => x.ReviewStepId == active.Id);
            }
            else
            {
                Assert.Equal(ChangeRequestState.Approved, request.State);
                Assert.Equal(3, signatures.Count);
                var assessment = await db.DownstreamChangeAssessments.SingleAsync(x =>
                    x.Id == identity.RootElement.GetProperty("AssessmentId").GetGuid());
                Assert.Equal(DownstreamAssessmentState.InReview, assessment.State);
                Assert.Equal(DownstreamAssessmentOutcome.NoChangeRequired, assessment.Outcome);
                Assert.Equal(RequirementLevel.HighLevel, assessment.TargetLevel);
                Assert.Equal("software.author", assessment.SubmittedBy);
                Assert.Equal("software.lead", assessment.SelectedApproverId);
                Assert.Null(assessment.ApprovedAt);
            }
        }
        var seeder = new FmsShowcaseSeeder(db);
        var trace = await seeder.ActiveTraceInventoryAsync(showcase.Summary.ProgramId);
        Assert.True(trace.Holds, string.Join(" ", trace.Problems));
        Assert.InRange(trace.IncompletePercent, 5, 10);
        Assert.Empty(await seeder.UpgradeAsync(showcase.Summary.ProgramId));
        Assert.Equal(2, await db.ShowcaseUpgradeSteps.CountAsync(x => x.ProgramId == showcase.Summary.ProgramId
            && x.StepKey.StartsWith(FmsShowcaseSeeder.WorkflowScenarioPrefix)));
    }

    [Theory]
    [InlineData("selected-approver")]
    [InlineData("signature-hash")]
    [InlineData("missing-marker")]
    public async Task The_diagnostic_exposes_workflow_drift_and_the_upgrade_does_not_adopt_it(string drift)
    {
        using var database = showcase.Create();
        await using var db = database.Context();
        var marker = await db.ShowcaseUpgradeSteps.SingleAsync(x => x.ProgramId == showcase.Summary.ProgramId
            && x.StepKey == FmsShowcaseSeeder.WorkflowScenarioPrefix + "assessment-approval");
        using var identity = JsonDocument.Parse(marker.Detail);
        var requestId = identity.RootElement.GetProperty("RequestId").GetGuid();
        if (drift == "missing-marker") db.ShowcaseUpgradeSteps.Remove(marker);
        else if (drift == "signature-hash")
        {
            var signature = await db.ElectronicSignatures.FirstAsync(x => x.ArtifactId == requestId);
            db.Entry(signature).Property(x => x.ContentHash).CurrentValue = "invalid fixture hash";
        }
        else
        {
            var assessment = await db.DownstreamChangeAssessments.SingleAsync(x => x.SourceChangeRequestId == requestId);
            db.Entry(assessment).Property(x => x.SelectedApproverId).CurrentValue = assessment.AssignedEngineerId;
        }
        await db.SaveChangesAsync();
        var seeder = new FmsShowcaseSeeder(db);
        Assert.False((await seeder.ActiveTraceInventoryAsync(showcase.Summary.ProgramId)).Holds);
        Assert.Empty(await seeder.UpgradeAsync(showcase.Summary.ProgramId));
        Assert.False((await seeder.ActiveTraceInventoryAsync(showcase.Summary.ProgramId)).Holds);
    }
}
