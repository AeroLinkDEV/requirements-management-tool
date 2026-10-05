using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Common;
using AeroLink.Domain.Documents;
using AeroLink.Domain.Identity;
using AeroLink.Domain.Notifications;
using AeroLink.Domain.Requirements;
using AeroLink.Domain.Verification;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AeroLink.Api.Tests;

// Primary exact-link contract: generic /open tests cannot detect an ended original assignment
// being presented as current, or an administrator opening another recipient's original notice.
// Authoring gate: each later task/cycle/round is a real persisted positive control. Rebinding an old
// notice to current work fails these HTTP assertions; infrastructure eligibility alone misses the
// endpoint's frozen projection and deliberate navigation. Within-cycle independence remains required.
public sealed class NotificationContextApiTests
{
    [Fact]
    public async Task Original_task_context_survives_completion_without_granting_another_recipient_access()
    {
        using var factory = new AeroLinkApiFactory(); using var client = factory.CreateClient();
        await SecurityBoundaryTests.BootstrapAndLoginAdministratorAsync(client);
        using var workspace = await client.PostAsJsonAsync("/api/workspaces", new
        { programName = "Exact Notice", programCode = "EXN", projectName = "Exact Notice", softwareProduct = "Notice Product", initialRelease = "1.0" });
        Assert.Equal(HttpStatusCode.Created, workspace.StatusCode);
        var project = (await workspace.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("project").GetProperty("id").GetGuid();
        Guid id, otherId, taskId, secondId, secondTaskId, artifactId;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>(); var now = DateTimeOffset.UtcNow;
            var artifact = new RequirementArtifact(project, "SR-90001", RequirementLevel.System, now); artifactId = artifact.Id;
            UserNotification AddTask(string recipient)
            {
                var task = new ArtifactAssignment(project, "Requirement", artifact.Id, null, recipient, "Private title", "Private prose", null, "admin", now);
                var notice = new UserNotification(project, recipient, "RequirementAssignment", "Private title", "Private prose", $"requirement:{artifact.Id}", artifact.Id, now);
                notice.BindContext(NotificationContext.RequirementAssignment(notice, artifact, task));
                db.ArtifactAssignments.Add(task); db.UserNotifications.Add(notice); return notice;
            }
            var mine = AddTask("admin"); id = mine.Id; taskId = mine.Context!.SourceId; var second = AddTask("admin"); secondId = second.Id; secondTaskId = second.Context!.SourceId; otherId = AddTask("someoneelse").Id;
            db.Requirements.Add(artifact); await db.SaveChangesAsync();
        }
        using var active = await client.GetAsync($"/api/notifications/{id}/context"); Assert.Equal(HttpStatusCode.OK, active.StatusCode);
        var context = await active.Content.ReadFromJsonAsync<JsonElement>(); Assert.True(context.GetProperty("originalObligationActive").GetBoolean());
        Assert.Equal(taskId, context.GetProperty("sourceId").GetGuid()); Assert.DoesNotContain("Private prose", context.GetRawText());
        var secondBefore = await ReadContextAsync(client, secondId);
        Assert.True(secondBefore.GetProperty("originalObligationActive").GetBoolean());
        Assert.Equal(secondTaskId, secondBefore.GetProperty("sourceId").GetGuid());
        using var other = await client.GetAsync($"/api/notifications/{otherId}/context"); Assert.Equal(HttpStatusCode.NotFound, other.StatusCode);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>(); var task = await db.ArtifactAssignments.SingleAsync(x => x.Id == taskId);
            task.Complete("admin", task.Version, DateTimeOffset.UtcNow); await db.SaveChangesAsync();
        }
        using var ended = await client.GetAsync($"/api/notifications/{id}/context");
        var original = await ended.Content.ReadFromJsonAsync<JsonElement>(); Assert.False(original.GetProperty("originalObligationActive").GetBoolean());
        Assert.Equal(taskId, original.GetProperty("sourceId").GetGuid());
        using var secondActive = await client.GetAsync($"/api/notifications/{secondId}/context");
        var secondContext = await secondActive.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(secondContext.GetProperty("originalObligationActive").GetBoolean());
        Assert.Equal(secondTaskId, secondContext.GetProperty("sourceId").GetGuid()); Assert.NotEqual(taskId, secondTaskId);
        using var current = await client.GetAsync($"/api/notifications/{id}/current"); Assert.Equal(HttpStatusCode.OK, current.StatusCode);
        Assert.Equal($"/open/requirement/{artifactId}", (await current.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("path").GetString());
        using var forbidden = await client.GetAsync($"/api/notifications/{otherId}/current"); Assert.Equal(HttpStatusCode.NotFound, forbidden.StatusCode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_later_valid_review_cycle_for_the_same_person_does_not_replace_the_original_notice(bool testAssessment)
    {
        using var factory = new AeroLinkApiFactory(); using var client = factory.CreateClient();
        var project = await WorkspaceAsync(client);
        Guid originalId, laterId, recordId;
        var workflow = new ReviewWorkflowSpecification(Guid.NewGuid(), Guid.NewGuid(), "Frozen approval", 1,
            ReviewMode.Sequential, [new(0, "Approval", ProgramRole.Approver, ReviewStageKind.Approval)]);
        SystemChangeRequest source;
        TestChangeReview? assessment = null;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var releaseId = await db.Releases.Where(x => x.ProjectId == project).Select(x => x.Id).SingleAsync();
            source = new SystemChangeRequest("SRCR-90001", 0, project, releaseId, "Private change title", "P", "A", "S", "author.user", DateTimeOffset.UtcNow);
            source.AddRequirementChange("author.user", "SYSR-90001", 0, RequirementLevel.System, RequirementChangeKind.Introduce,
                "Private statement", "Private rationale", "Review", DateTimeOffset.UtcNow);
            if (testAssessment)
            {
                assessment = new TestChangeReview(project, releaseId, source.Id, TestChangeReviewDiscipline.System,
                    source.DisplayNumber, DateTimeOffset.UtcNow, authorId: "author.user");
                assessment.RecordNoTestChangeRequired("author.user", "No procedure affected.", DateTimeOffset.UtcNow);
            }
            IReadOnlyList<ApproverSelection> duplicate = [new("admin", "Admin"), new("ADMIN", "Admin again")];
            var refusal = Assert.Throws<DomainException>(() => testAssessment
                ? assessment!.SubmitForReview("author.user", duplicate, true, DateTimeOffset.UtcNow, workflow: workflow)
                : source.SubmitForReview("author.user", duplicate, DateTimeOffset.UtcNow, workflow: workflow));
            Assert.Contains("cannot appear twice", refusal.Message);
            var cycle = testAssessment
                ? assessment!.SubmitForReview("author.user", [new("admin", "Admin", ProgramRole.Approver)], true, DateTimeOffset.UtcNow, workflow: workflow)
                : source.SubmitForReview("author.user", [new("admin", "Admin", ProgramRole.Approver)], DateTimeOffset.UtcNow, workflow: workflow);
            recordId = assessment?.Id ?? source.Id;
            var notice = ReviewNotice(project, source, assessment, cycle); originalId = notice.Id;
            db.AddRange(source, notice); if (assessment is not null) db.TestChangeReviews.Add(assessment);
            await db.SaveChangesAsync();
        }
        var frozen = await ReadContextAsync(client, originalId);
        Assert.True(frozen.GetProperty("originalObligationActive").GetBoolean());
        Assert.Equal("Approval", frozen.GetProperty("stage").GetString());
        Assert.Equal(1, frozen.GetProperty("cycle").GetInt32());
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            source = await db.SystemChangeRequests.Include(x => x.RequirementChanges)
                .Include(x => x.ReviewCycles).ThenInclude(x => x.Steps).SingleAsync(x => x.Id == source.Id);
            if (testAssessment)
                assessment = await db.TestChangeReviews.Include(x => x.ReviewCycles).ThenInclude(x => x.Steps).SingleAsync(x => x.Id == recordId);
            if (assessment is not null) assessment.RequestChanges("admin", "Revise assessment.", DateTimeOffset.UtcNow);
            else source.RequestChanges("admin", "Revise change.", DateTimeOffset.UtcNow);
            var cycle = assessment is not null
                ? assessment.SubmitForReview("author.user", [new("admin", "Admin", ProgramRole.Approver)], true, DateTimeOffset.UtcNow, workflow: workflow)
                : source.SubmitForReview("author.user", [new("admin", "Admin", ProgramRole.Approver)], DateTimeOffset.UtcNow, workflow: workflow);
            var notice = ReviewNotice(project, source, assessment, cycle); laterId = notice.Id;
            db.UserNotifications.Add(notice); await db.SaveChangesAsync();
        }
        await AssertEndedAndLaterAsync(client, originalId, laterId, frozen,
            $"/open/{(testAssessment ? "test-change-request" : "scr")}/{recordId}");
    }

    [Fact]
    public async Task A_later_document_round_for_the_same_reviewer_preserves_the_original_round_context()
    {
        using var factory = new AeroLinkApiFactory(); using var client = factory.CreateClient();
        var project = await WorkspaceAsync(client);
        Guid originalId, laterId, documentId, revisionId;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var document = new ManagedDocument(project, "SDP-900001", "SDP", "Plan", "Private plan", "author.user", DateTimeOffset.UtcNow);
            var revision = new ManagedDocumentRevision(document.Id, 0, "author.user", "Initial.", DateTimeOffset.UtcNow);
            documentId = document.Id; revisionId = revision.Id; revision.RecordCheckIn(Guid.NewGuid(), DateTimeOffset.UtcNow);
            var refusal = Assert.Throws<DomainException>(() => revision.SubmitForReview("author.user", new string('a', 64),
                [new("admin", "Admin", "Technical"), new("ADMIN", "Admin again", "Approval", Kind: ReviewStageKind.Approval)], DateTimeOffset.UtcNow));
            Assert.Contains("cannot appear twice", refusal.Message);
            revision.SubmitForReview("author.user", new string('a', 64),
                [new("admin", "Admin", "Technical"), new("final.user", "Final", "Approval", Kind: ReviewStageKind.Approval)], DateTimeOffset.UtcNow);
            var notice = DocumentNotice(project, document, revision); originalId = notice.Id;
            db.AddRange(document, revision, notice); await db.SaveChangesAsync();
        }
        var frozen = await ReadContextAsync(client, originalId);
        Assert.True(frozen.GetProperty("originalObligationActive").GetBoolean());
        Assert.Equal(1, frozen.GetProperty("cycle").GetInt32());
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var document = await db.ManagedDocuments.SingleAsync(x => x.Id == documentId);
            var revision = await db.ManagedDocumentRevisions.Include(x => x.ReviewSteps).SingleAsync(x => x.Id == revisionId);
            revision.Return("admin", "Revise plan.", DateTimeOffset.UtcNow); revision.RecordCheckIn(Guid.NewGuid(), DateTimeOffset.UtcNow);
            revision.SubmitForReview("author.user", new string('b', 64),
                [new("admin", "Admin", "Technical"), new("final.user", "Final", "Approval", Kind: ReviewStageKind.Approval)], DateTimeOffset.UtcNow);
            db.ManagedDocumentReviewSteps.AddRange(revision.ReviewSteps.Where(x => x.Cycle == revision.CurrentReviewCycle));
            var notice = DocumentNotice(project, document, revision); laterId = notice.Id;
            db.UserNotifications.Add(notice); await db.SaveChangesAsync();
        }
        await AssertEndedAndLaterAsync(client, originalId, laterId, frozen, $"/open/managed-document/{documentId}");
    }

    private static async Task<Guid> WorkspaceAsync(HttpClient client)
    {
        await SecurityBoundaryTests.BootstrapAndLoginAdministratorAsync(client);
        using var response = await client.PostAsJsonAsync("/api/workspaces", new
        { programName = "Exact Review", programCode = "EXR", projectName = "Exact Review", softwareProduct = "Review Product", initialRelease = "1.0" });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("project").GetProperty("id").GetGuid();
    }
    private static UserNotification ReviewNotice(Guid project, SystemChangeRequest source, TestChangeReview? assessment, ReviewCycle cycle)
    {
        var step = Assert.Single(cycle.Steps, x => x.State == ApprovalStepState.Active);
        var id = assessment?.Id ?? source.Id;
        var notice = new UserNotification(project, "admin", "ApprovalActivated", "Private title", "Private prose",
            $"{(assessment is null ? "scr" : "test-change-request")}:{id}", id, DateTimeOffset.UtcNow);
        notice.BindContext(assessment is null ? NotificationContext.ChangeRequestStep(notice, source, cycle, step)
            : NotificationContext.TestChangeStep(notice, assessment, cycle, step));
        return notice;
    }
    private static UserNotification DocumentNotice(Guid project, ManagedDocument document, ManagedDocumentRevision revision)
    {
        var step = Assert.Single(revision.ReviewSteps, x => x.State == ManagedDocumentReviewStepState.Active);
        var notice = new UserNotification(project, "admin", "DocumentReviewActivated", "Private title", "Private prose",
            $"managed-document:{document.Id}", document.Id, DateTimeOffset.UtcNow);
        notice.BindContext(NotificationContext.DocumentStep(notice, document, revision, step)); return notice;
    }
    private static async Task<JsonElement> ReadContextAsync(HttpClient client, Guid id)
    {
        using var response = await client.GetAsync($"/api/notifications/{id}/context");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var context = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.DoesNotContain("Private", context.GetRawText()); return context;
    }
    private static async Task AssertEndedAndLaterAsync(HttpClient client, Guid originalId, Guid laterId, JsonElement frozen, string currentPath)
    {
        var original = await ReadContextAsync(client, originalId); var later = await ReadContextAsync(client, laterId);
        Assert.False(original.GetProperty("originalObligationActive").GetBoolean());
        Assert.Contains("does not authorize a later cycle", original.GetProperty("explanation").GetString());
        foreach (var field in new[] { "identifier", "eventType", "stage", "cycle", "revision", "sourceId", "sourceFamily", "snapshotHash" })
            Assert.Equal(frozen.GetProperty(field).GetRawText(), original.GetProperty(field).GetRawText());
        Assert.True(later.GetProperty("originalObligationActive").GetBoolean());
        Assert.Equal(2, later.GetProperty("cycle").GetInt32());
        Assert.NotEqual(original.GetProperty("sourceId").GetGuid(), later.GetProperty("sourceId").GetGuid());
        Assert.Equal(original.GetProperty("identifier").GetString(), later.GetProperty("identifier").GetString());
        using var current = await client.GetAsync($"/api/notifications/{originalId}/current");
        Assert.Equal(HttpStatusCode.OK, current.StatusCode);
        Assert.Equal(currentPath, (await current.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("path").GetString());
    }
}
