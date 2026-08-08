using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AeroLink.Api;
using AeroLink.Domain.Baselines;
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
/// Configured TCR review workflows must be executed: staged sequential and parallel cycles, active-approver
/// enforcement, return closing the cycle, resubmission starting a fresh cycle, and attributable signatures.
/// </summary>
public sealed class TestChangeRequestReviewWorkflowTests
{
    private sealed record Fixture(Guid ProjectId, Guid ReleaseId, Guid ChangeId, Guid ReviewId, Guid ItemId,
        Guid RequirementRevisionId, Guid BaselineId);

    private static async Task<Fixture> SeedAsync(AeroLinkApiFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        var impact = scope.ServiceProvider.GetRequiredService<VerificationImpactService>();
        var now = DateTimeOffset.UtcNow;

        var program = new ProgramRecord("Workflow Program", "WF");
        var project = new ProjectRecord(program.Id, "Software", "Workflow Software");
        var release = new SoftwareRelease(project.Id, "1.6", false);
        var baseline = new CandidateBaseline("SW-01.60", 0, project.Id, release.Id, null,
            "Workflow baseline", "author", now);
        db.AddRange(program, project, release, baseline);

        var change = new SystemChangeRequest("SRCR-00960", 0, project.Id, release.Id,
            "Oceanic", "P", "A", "S", "author", now);
        change.AddRequirementChange("author", "SYSR-00000961", 0, RequirementLevel.System,
            RequirementChangeKind.Introduce, "The FMS shall sequence oceanic waypoints.",
            "New capability", "Analysis", now);
        change.SubmitForReview("author", [new("reviewer", "Reviewer")], now);
        change.ApproveActiveStage("reviewer", now);

        var requirement = new RequirementArtifact(project.Id, "SYSR-00000962", RequirementLevel.System, now);
        var revision = new RequirementRevision(requirement.Id, 0,
            "The FMS shall expose a workflow verification target.", "Rationale", "Test",
            RequirementRevisionState.Active, change.Id, baseline.Id, now);
        var procedure = new TestProcedure(project.Id, "SYSTP-000900", "Workflow procedure",
            "author", now, TestProcedureLevel.System);
        var procedureRevision = new TestProcedureRevision(procedure.Id, 0,
            "Objective", "Preconditions", "Steps", "Expected", TestProcedureState.Approved, "author", now);
        db.AddRange(change, requirement, revision, procedure, procedureRevision);

        var engineer = new UserAccount("workflow.engineer", "Workflow Engineer", "workflow.engineer@example.test",
            IdentityService.HashPassword(AeroLinkApiFactory.MemberPassword), now);
        db.Add(engineer);
        db.Add(new ProgramMembership(engineer.Id, program.Id, ProgramRole.TestEngineer, "test.setup", now));
        db.Add(new ProgramMembership(engineer.Id, program.Id, ProgramRole.Approver, "test.setup", now));
        foreach (var (user, role) in new[]
                 {
                     ("workflow.one", ProgramRole.Approver),
                     ("workflow.two", ProgramRole.Approver),
                     ("workflow.config", ProgramRole.ConfigurationManager),
                     ("workflow.outsider", ProgramRole.Engineer),
                 })
        {
            var account = new UserAccount(user, user, $"{user}@example.test",
                IdentityService.HashPassword(AeroLinkApiFactory.MemberPassword), now);
            db.Add(account);
            db.Add(new ProgramMembership(account.Id, program.Id, role, "test.setup", now));
        }
        var multi = new UserAccount("workflow.multirole", "Multi Role", "workflow.multirole@example.test",
            IdentityService.HashPassword(AeroLinkApiFactory.MemberPassword), now);
        db.Add(multi);
        db.Add(new ProgramMembership(multi.Id, program.Id, ProgramRole.TestLead, "test.setup", now));
        db.Add(new ProgramMembership(multi.Id, program.Id, ProgramRole.Approver, "test.setup", now));
        var leadOnly = new UserAccount("workflow.lead", "Lead Only", "workflow.lead@example.test",
            IdentityService.HashPassword(AeroLinkApiFactory.MemberPassword), now);
        db.Add(leadOnly);
        db.Add(new ProgramMembership(leadOnly.Id, program.Id, ProgramRole.TestLead, "test.setup", now));
        var author = new UserAccount("workflow.author", "Workflow Author", "workflow.author@example.test",
            IdentityService.HashPassword(AeroLinkApiFactory.MemberPassword), now);
        db.Add(author);
        db.Add(new ProgramMembership(author.Id, program.Id, ProgramRole.Engineer, "test.setup", now));
        await db.SaveChangesAsync();

        await impact.RaiseForApprovedChangeRequestAsync(
            await db.SystemChangeRequests.Include(x => x.RequirementChanges).SingleAsync(x => x.Id == change.Id),
            now, default);
        await db.SaveChangesAsync();

        var review = await db.TestChangeReviews.SingleAsync(x => x.ChangeRequestId == change.Id
            && x.Discipline == TestChangeReviewDiscipline.System);
        var item = await db.VerificationImpactItems.SingleAsync(x => x.ChangeRequestId == change.Id);
        return new(project.Id, release.Id, change.Id, review.Id, item.Id, revision.Id, baseline.Id);
    }

    private static async Task LoginAsync(HttpClient client, string user)
    {
        using var login = await client.PostAsJsonAsync("/api/auth/login",
            new { userName = user, password = AeroLinkApiFactory.MemberPassword });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        await SecurityBoundaryTests.AuthorizeMutationsAsync(client);
    }

    private static async Task<Guid> CreateWorkflowAsync(HttpClient client, Guid projectId, string mode,
        params (string Name, string Role)[] stages)
        => await CreateWorkflowAsync(client, projectId, mode, "SystemTest", stages);

    private static async Task<Guid> CreateWorkflowAsync(HttpClient client, Guid projectId, string mode,
        string appliesTo, params (string Name, string Role)[] stages)
    {
        await LoginAsync(client, "workflow.config");
        using var created = await client.PostAsJsonAsync("/api/review-workflows", new
        {
            projectId,
            name = $"TCR {mode}",
            appliesTo,
            mode,
            stages = stages.Select(x => new { name = x.Name, requiredRole = x.Role }).ToArray()
        });
        var body = await created.Content.ReadAsStringAsync();
        Assert.True(created.IsSuccessStatusCode, $"{(int)created.StatusCode}: {body}");
        var id = JsonSerializer.Deserialize<JsonElement>(body).GetProperty("id").GetGuid();
        using var activated = await client.PostAsJsonAsync($"/api/review-workflows/{id}/activate", new { });
        Assert.True(activated.IsSuccessStatusCode, await activated.Content.ReadAsStringAsync());
        return id;
    }

    private static async Task<JsonElement> ReadItemAsync(HttpClient client, Guid releaseId, Guid reviewId)
    {
        var list = await client.GetFromJsonAsync<JsonElement>(
            $"/api/releases/{releaseId}/test-change-reviews");
        return list.GetProperty("items").EnumerateArray()
            .Single(x => x.GetProperty("id").GetGuid() == reviewId);
    }

    private static async Task PreparePackageAsync(HttpClient client, Fixture fixture)
    {
        await LoginAsync(client, "workflow.engineer");
        using var resolved = await client.PostAsJsonAsync($"/api/verification-impact/{fixture.ItemId}/resolve",
            new { outcome = "NewProcedureRequired", rationale = "A procedure must be written for the new requirement." });
        Assert.True(resolved.IsSuccessStatusCode, await resolved.Content.ReadAsStringAsync());
        using var concluded = await client.PostAsJsonAsync(
            $"/api/test-change-reviews/{fixture.ReviewId}/conclusion",
            new { testChangeRequired = true });
        Assert.True(concluded.IsSuccessStatusCode, await concluded.Content.ReadAsStringAsync());
        using var proposed = await client.PostAsJsonAsync(
            $"/api/test-change-reviews/{fixture.ReviewId}/procedure-changes",
            new
            {
                kind = "Introduce",
                revision = 0,
                title = "Workflow procedure",
                objective = "Verify the workflow target behavior.",
                preconditions = "The workflow target is available.",
                steps = "Exercise the target.",
                expectedResult = "The expected behavior is observed.",
                rationale = "Nothing covers the new requirement.",
                drivingRequirementRevisionIds = new[] { fixture.RequirementRevisionId }
            });
        Assert.True(proposed.IsSuccessStatusCode, await proposed.Content.ReadAsStringAsync());
    }

    private static async Task<JsonElement> SubmitAsync(HttpClient client, Guid reviewId, object body)
    {
        using var response = await client.PostAsJsonAsync($"/api/test-change-reviews/{reviewId}/submit", body);
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{(int)response.StatusCode}: {text}");
        return JsonSerializer.Deserialize<JsonElement>(text);
    }

    [Fact]
    public async Task Two_stage_sequential_review_advances_stage_by_stage_and_approves_only_at_the_end()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        var fixture = await SeedAsync(factory);
        await CreateWorkflowAsync(client, fixture.ProjectId, "Sequential",
            ("First", nameof(ProgramRole.Approver)), ("Second", nameof(ProgramRole.Approver)));
        await PreparePackageAsync(client, fixture);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var spec = await WorkflowEndpoints.ActiveSpecificationAsync(db, fixture.ProjectId,
                TestChangeReviewDiscipline.System, default);
            Assert.NotNull(spec);
        }

        var submitted = await SubmitAsync(client, fixture.ReviewId, new
        {
            approvers = new[] { new { userId = "workflow.one" }, new { userId = "workflow.two" } }
        });
        Assert.Equal(1, submitted.GetProperty("sequence").GetInt32());
        Assert.Equal(2, submitted.GetProperty("stageCount").GetInt32());
        Assert.Equal("InReview", submitted.GetProperty("state").GetString());

        Guid cycleId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var cycle = await db.ReviewCycles.Include(x => x.Steps).SingleAsync(x => x.TestChangeReviewId == fixture.ReviewId);
            cycleId = cycle.Id;
            Assert.NotNull(cycle.WorkflowId);
            Assert.Equal("TCR Sequential", cycle.WorkflowName);
            Assert.Equal(1, cycle.WorkflowVersion);
            Assert.Equal(ReviewMode.Sequential, cycle.Mode);
            Assert.Equal(2, cycle.Steps.Count);
            Assert.Equal(ApprovalStepState.Active, cycle.Steps.Single(x => x.Position == 0).State);
            Assert.Equal(ApprovalStepState.Pending, cycle.Steps.Single(x => x.Position == 1).State);
        }

        await LoginAsync(client, "workflow.one");
        using var stageOne = await client.PostAsJsonAsync(
            $"/api/test-change-reviews/{fixture.ReviewId}/approve",
            new { rationale = "Stage one is sound." });
        var stageOneBody = await stageOne.Content.ReadAsStringAsync();
        Assert.True(stageOne.StatusCode == HttpStatusCode.OK, $"{(int)stageOne.StatusCode}: {stageOneBody}");
        Assert.Equal("InReview", JsonSerializer.Deserialize<JsonElement>(stageOneBody).GetProperty("state").GetString());

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var cycle = await db.ReviewCycles.Include(x => x.Steps).SingleAsync(x => x.Id == cycleId);
            Assert.Equal(ReviewCycleState.Active, cycle.State);
            Assert.Equal(ApprovalStepState.Approved, cycle.Steps.Single(x => x.Position == 0).State);
            Assert.Equal(ApprovalStepState.Active, cycle.Steps.Single(x => x.Position == 1).State);
            Assert.True(await db.UserNotifications.AnyAsync(x =>
                x.Recipient == "workflow.two" && x.Type == "ReviewActivated" && x.ArtifactId == fixture.ReviewId));
        }

        await LoginAsync(client, "workflow.two");
        using var stageTwo = await client.PostAsJsonAsync(
            $"/api/test-change-reviews/{fixture.ReviewId}/approve",
            new { rationale = "Stage two is sound." });
        var stageTwoBody = await stageTwo.Content.ReadAsStringAsync();
        Assert.True(stageTwo.StatusCode == HttpStatusCode.OK, $"{(int)stageTwo.StatusCode}: {stageTwoBody}");
        Assert.Equal("Approved", JsonSerializer.Deserialize<JsonElement>(stageTwoBody).GetProperty("state").GetString());

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var review = await db.TestChangeReviews.SingleAsync(x => x.Id == fixture.ReviewId);
            var cycle = await db.ReviewCycles.Include(x => x.Steps).SingleAsync(x => x.Id == cycleId);
            Assert.Equal(TestChangeReviewState.Approved, review.State);
            Assert.Equal(ReviewCycleState.Approved, cycle.State);
            var signatures = await db.ElectronicSignatures
                .Where(x => x.ArtifactId == fixture.ReviewId).ToListAsync();
            Assert.Equal(2, signatures.Count);
            Assert.All(signatures, signature => Assert.Equal(cycle.SnapshotHash, signature.ContentHash));
            Assert.Equal(new[] { "workflow.one", "workflow.two" },
                signatures.OrderBy(x => x.SignedAt).Select(x => x.UserName).ToArray());
        }
    }

    [Fact]
    public async Task Parallel_review_activates_all_stages_and_approves_on_the_final_approval()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        var fixture = await SeedAsync(factory);
        await CreateWorkflowAsync(client, fixture.ProjectId, "Parallel",
            ("First", nameof(ProgramRole.Approver)), ("Second", nameof(ProgramRole.Approver)));
        await PreparePackageAsync(client, fixture);

        var submitted = await SubmitAsync(client, fixture.ReviewId, new
        {
            approvers = new[] { new { userId = "workflow.one" }, new { userId = "workflow.two" } }
        });
        Assert.Equal(2, submitted.GetProperty("stageCount").GetInt32());

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var cycle = await db.ReviewCycles.Include(x => x.Steps).SingleAsync(x => x.TestChangeReviewId == fixture.ReviewId);
            Assert.Equal(ReviewMode.Parallel, cycle.Mode);
            Assert.All(cycle.Steps, step => Assert.Equal(ApprovalStepState.Active, step.State));
        }

        await LoginAsync(client, "workflow.one");
        using var first = await client.PostAsJsonAsync(
            $"/api/test-change-reviews/{fixture.ReviewId}/approve", new { rationale = "One." });
        Assert.True(first.IsSuccessStatusCode, await first.Content.ReadAsStringAsync());
        Assert.Equal("InReview", (await first.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("state").GetString());

        await LoginAsync(client, "workflow.two");
        using var second = await client.PostAsJsonAsync(
            $"/api/test-change-reviews/{fixture.ReviewId}/approve", new { rationale = "Two." });
        Assert.True(second.IsSuccessStatusCode, await second.Content.ReadAsStringAsync());
        Assert.Equal("Approved", (await second.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("state").GetString());
    }

    [Fact]
    public async Task A_stage_approver_who_lacks_the_required_role_is_refused_at_submission()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        var fixture = await SeedAsync(factory);
        await CreateWorkflowAsync(client, fixture.ProjectId, "Sequential",
            ("First", nameof(ProgramRole.Approver)), ("Second", nameof(ProgramRole.Approver)));
        await PreparePackageAsync(client, fixture);

        // workflow.outsider is an Engineer, not an Approver, and cannot sign the Approver stage.
        await LoginAsync(client, "workflow.engineer");
        using var response = await client.PostAsJsonAsync(
            $"/api/test-change-reviews/{fixture.ReviewId}/submit",
            new { approvers = new[] { new { userId = "workflow.outsider" }, new { userId = "workflow.two" } } });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(body.Contains("has no recorded authority") || body.Contains("must be signed by"), body);
    }

    [Fact]
    public async Task An_inactive_stage_approver_cannot_approve_before_their_turn()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        var fixture = await SeedAsync(factory);
        await CreateWorkflowAsync(client, fixture.ProjectId, "Sequential",
            ("First", nameof(ProgramRole.Approver)), ("Second", nameof(ProgramRole.Approver)));
        await PreparePackageAsync(client, fixture);

        await SubmitAsync(client, fixture.ReviewId, new
        {
            approvers = new[] { new { userId = "workflow.one" }, new { userId = "workflow.two" } }
        });
        await LoginAsync(client, "workflow.two");
        using var response = await client.PostAsJsonAsync(
            $"/api/test-change-reviews/{fixture.ReviewId}/approve", new { rationale = "Too early." });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("Only the active approver", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Self_approval_is_refused_at_submission()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        var fixture = await SeedAsync(factory);
        await CreateWorkflowAsync(client, fixture.ProjectId, "Sequential",
            ("First", nameof(ProgramRole.Approver)), ("Second", nameof(ProgramRole.Approver)));
        await PreparePackageAsync(client, fixture);

        // workflow.engineer holds both TestEngineer and Approver authority, so the stage role check passes
        // and the independence guard is what refuses the submission.
        await LoginAsync(client, "workflow.engineer");
        using var response = await client.PostAsJsonAsync(
            $"/api/test-change-reviews/{fixture.ReviewId}/submit",
            new { approvers = new[] { new { userId = "workflow.engineer" }, new { userId = "workflow.two" } } });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("independent", body);
    }

    [Fact]
    public async Task Return_closes_the_cycle_and_resubmission_starts_a_fresh_cycle()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        var fixture = await SeedAsync(factory);
        await CreateWorkflowAsync(client, fixture.ProjectId, "Sequential",
            ("First", nameof(ProgramRole.Approver)), ("Second", nameof(ProgramRole.Approver)));
        await PreparePackageAsync(client, fixture);

        var first = await SubmitAsync(client, fixture.ReviewId, new
        {
            approvers = new[] { new { userId = "workflow.one" }, new { userId = "workflow.two" } }
        });
        Guid firstCycleId = first.GetProperty("cycleId").GetGuid();
        string firstHash;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            firstHash = (await db.ReviewCycles.SingleAsync(x => x.Id == firstCycleId)).SnapshotHash;
        }

        await LoginAsync(client, "workflow.one");
        using var returned = await client.PostAsJsonAsync(
            $"/api/test-change-reviews/{fixture.ReviewId}/return",
            new { rationale = "The procedure steps need rework." });
        var returnBody = await returned.Content.ReadAsStringAsync();
        Assert.True(returned.StatusCode == HttpStatusCode.OK, $"{(int)returned.StatusCode}: {returnBody}");
        Assert.Equal("Open", JsonSerializer.Deserialize<JsonElement>(returnBody).GetProperty("state").GetString());

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var review = await db.TestChangeReviews.SingleAsync(x => x.Id == fixture.ReviewId);
            Assert.Equal(TestChangeReviewState.Open, review.State);
            var cycle = await db.ReviewCycles.SingleAsync(x => x.Id == firstCycleId);
            Assert.Equal(ReviewCycleState.ChangesRequested, cycle.State);
            Assert.Equal("The procedure steps need rework.", cycle.ClosureReason);
        }

        // The case can be corrected after return.
        await LoginAsync(client, "workflow.engineer");
        using var caseEdit = await client.PostAsJsonAsync(
            $"/api/test-change-reviews/{fixture.ReviewId}/case",
            new { title = "Workflow package", problem = "P", analysis = "A", solution = "S" });
        Assert.True(caseEdit.IsSuccessStatusCode, await caseEdit.Content.ReadAsStringAsync());

        var second = await SubmitAsync(client, fixture.ReviewId, new
        {
            approvers = new[] { new { userId = "workflow.one" }, new { userId = "workflow.two" } }
        });
        Assert.Equal(2, second.GetProperty("sequence").GetInt32());
        Guid secondCycleId = second.GetProperty("cycleId").GetGuid();
        Assert.NotEqual(firstCycleId, secondCycleId);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var secondCycle = await db.ReviewCycles.Include(x => x.Steps).SingleAsync(x => x.Id == secondCycleId);
            Assert.NotEqual(firstHash, secondCycle.SnapshotHash);
            Assert.Equal(ApprovalStepState.Active, secondCycle.Steps.Single(x => x.Position == 0).State);
            Assert.Equal(ReviewCycleState.Active, secondCycle.State);
        }

        await LoginAsync(client, "workflow.one");
        using var approveOne = await client.PostAsJsonAsync(
            $"/api/test-change-reviews/{fixture.ReviewId}/approve", new { rationale = "Rework is sound." });
        Assert.True(approveOne.IsSuccessStatusCode, await approveOne.Content.ReadAsStringAsync());
        await LoginAsync(client, "workflow.two");
        using var approveTwo = await client.PostAsJsonAsync(
            $"/api/test-change-reviews/{fixture.ReviewId}/approve", new { rationale = "Approved." });
        Assert.True(approveTwo.IsSuccessStatusCode, await approveTwo.Content.ReadAsStringAsync());

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var review = await db.TestChangeReviews.SingleAsync(x => x.Id == fixture.ReviewId);
            Assert.Equal(TestChangeReviewState.Approved, review.State);
            var firstCycle = await db.ReviewCycles.SingleAsync(x => x.Id == firstCycleId);
            Assert.Equal(ReviewCycleState.ChangesRequested, firstCycle.State);
        }
    }

    [Fact]
    public async Task No_configured_workflow_keeps_the_single_independent_approver_fallback()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        var fixture = await SeedAsync(factory);
        await PreparePackageAsync(client, fixture);

        var submitted = await SubmitAsync(client, fixture.ReviewId, new { approverId = "workflow.one" });
        Assert.Equal(1, submitted.GetProperty("stageCount").GetInt32());

        await LoginAsync(client, "workflow.one");
        using var approved = await client.PostAsJsonAsync(
            $"/api/test-change-reviews/{fixture.ReviewId}/approve", new { rationale = "Approved." });
        var body = await approved.Content.ReadAsStringAsync();
        Assert.True(approved.StatusCode == HttpStatusCode.OK, $"{(int)approved.StatusCode}: {body}");
        Assert.Equal("Approved", JsonSerializer.Deserialize<JsonElement>(body).GetProperty("state").GetString());

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var cycle = await db.ReviewCycles.Include(x => x.Steps).SingleAsync(x => x.TestChangeReviewId == fixture.ReviewId);
            Assert.Equal(ReviewCycleState.Approved, cycle.State);
            Assert.Single(cycle.Steps);
            Assert.Single(await db.ElectronicSignatures.Where(x => x.ArtifactId == fixture.ReviewId).ToListAsync());
        }
    }

    [Fact]
    public async Task Read_model_exposes_the_active_stage_to_the_correct_approver()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        var fixture = await SeedAsync(factory);
        await CreateWorkflowAsync(client, fixture.ProjectId, "Sequential",
            ("First", nameof(ProgramRole.Approver)), ("Second", nameof(ProgramRole.Approver)));
        await PreparePackageAsync(client, fixture);

        await SubmitAsync(client, fixture.ReviewId, new
        {
            approvers = new[] { new { userId = "workflow.one" }, new { userId = "workflow.two" } }
        });

        // Stage one is the active step; stage two is not yet actionable through the read model.
        await LoginAsync(client, "workflow.one");
        var asOne = await ReadItemAsync(client, fixture.ReleaseId, fixture.ReviewId);
        Assert.True(asOne.GetProperty("capabilities").GetProperty("canApprove").GetBoolean());
        Assert.True(asOne.GetProperty("capabilities").GetProperty("canReturn").GetBoolean());
        var cycle = asOne.GetProperty("reviewCycle");
        Assert.Equal("TCR Sequential", cycle.GetProperty("workflowName").GetString());
        Assert.Equal(1, cycle.GetProperty("workflowVersion").GetInt32());
        Assert.Equal(1, cycle.GetProperty("sequence").GetInt32());
        var steps = cycle.GetProperty("steps").EnumerateArray().ToList();
        Assert.Equal("Active", steps[0].GetProperty("state").GetString());
        Assert.Equal("Pending", steps[1].GetProperty("state").GetString());

        await LoginAsync(client, "workflow.two");
        var asTwo = await ReadItemAsync(client, fixture.ReleaseId, fixture.ReviewId);
        Assert.False(asTwo.GetProperty("capabilities").GetProperty("canApprove").GetBoolean());

        await LoginAsync(client, "workflow.one");
        using var approved = await client.PostAsJsonAsync(
            $"/api/test-change-reviews/{fixture.ReviewId}/approve", new { rationale = "Stage one." });
        Assert.True(approved.IsSuccessStatusCode, await approved.Content.ReadAsStringAsync());

        // After stage one, the read model moves the actionable step to stage two without a direct API call.
        await LoginAsync(client, "workflow.two");
        var afterOne = await ReadItemAsync(client, fixture.ReleaseId, fixture.ReviewId);
        Assert.True(afterOne.GetProperty("capabilities").GetProperty("canApprove").GetBoolean());
        Assert.True(afterOne.GetProperty("capabilities").GetProperty("canReturn").GetBoolean());
        var updatedSteps = afterOne.GetProperty("reviewCycle").GetProperty("steps").EnumerateArray().ToList();
        Assert.Equal("Approved", updatedSteps[0].GetProperty("state").GetString());
        Assert.Equal("Active", updatedSteps[1].GetProperty("state").GetString());

        await LoginAsync(client, "workflow.engineer");
        var asEngineer = await ReadItemAsync(client, fixture.ReleaseId, fixture.ReviewId);
        Assert.False(asEngineer.GetProperty("capabilities").GetProperty("canApprove").GetBoolean());
    }

    [Fact]
    public async Task Read_model_parallel_mode_offers_every_active_approver()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        var fixture = await SeedAsync(factory);
        await CreateWorkflowAsync(client, fixture.ProjectId, "Parallel",
            ("First", nameof(ProgramRole.Approver)), ("Second", nameof(ProgramRole.Approver)));
        await PreparePackageAsync(client, fixture);

        await SubmitAsync(client, fixture.ReviewId, new
        {
            approvers = new[] { new { userId = "workflow.one" }, new { userId = "workflow.two" } }
        });

        foreach (var user in new[] { "workflow.one", "workflow.two" })
        {
            await LoginAsync(client, user);
            var item = await ReadItemAsync(client, fixture.ReleaseId, fixture.ReviewId);
            Assert.True(item.GetProperty("capabilities").GetProperty("canApprove").GetBoolean(), user);
            Assert.True(item.GetProperty("capabilities").GetProperty("canReturn").GetBoolean(), user);
        }
    }

    [Fact]
    public async Task A_stage_requiring_test_lead_is_actionable_without_generic_approver_role()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        var fixture = await SeedAsync(factory);
        await CreateWorkflowAsync(client, fixture.ProjectId, "Sequential",
            ("Lead review", nameof(ProgramRole.TestLead)), ("Assurance", nameof(ProgramRole.Approver)));
        await PreparePackageAsync(client, fixture);

        // workflow.lead holds TestLead only — no generic Approver role.
        await SubmitAsync(client, fixture.ReviewId, new
        {
            approvers = new[] { new { userId = "workflow.lead" }, new { userId = "workflow.two" } }
        });
        await LoginAsync(client, "workflow.lead");
        var item = await ReadItemAsync(client, fixture.ReleaseId, fixture.ReviewId);
        Assert.True(item.GetProperty("capabilities").GetProperty("canApprove").GetBoolean());
        Assert.True(item.GetProperty("capabilities").GetProperty("canReturn").GetBoolean());
    }

    [Fact]
    public async Task A_multi_role_user_signs_the_stage_they_hold()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        var fixture = await SeedAsync(factory);
        await CreateWorkflowAsync(client, fixture.ProjectId, "Sequential",
            ("Lead review", nameof(ProgramRole.TestLead)), ("Assurance", nameof(ProgramRole.Approver)));
        await PreparePackageAsync(client, fixture);

        // workflow.multirole holds both TestLead and Approver; the strongest role must not be forced on a
        // stage that asks for TestLead.
        await SubmitAsync(client, fixture.ReviewId, new
        {
            approvers = new[] { new { userId = "workflow.multirole" }, new { userId = "workflow.two" } }
        });
        await LoginAsync(client, "workflow.multirole");
        using var approved = await client.PostAsJsonAsync(
            $"/api/test-change-reviews/{fixture.ReviewId}/approve", new { rationale = "Signed as Test Lead." });
        Assert.True(approved.IsSuccessStatusCode, await approved.Content.ReadAsStringAsync());

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var cycle = await db.ReviewCycles.Include(x => x.Steps)
                .SingleAsync(x => x.TestChangeReviewId == fixture.ReviewId);
            var step = cycle.Steps.Single(x => x.Position == 0);
            Assert.Equal("TestLead", step.Authority);
            Assert.Equal("workflow.multirole", step.ApproverId);
        }
    }

    [Fact]
    public async Task System_change_request_workflow_respects_multi_role_stage_authority()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        var fixture = await SeedAsync(factory);
        await CreateWorkflowAsync(client, fixture.ProjectId, "Sequential", "System",
            ("Lead review", nameof(ProgramRole.TestLead)));

        Guid draftId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var now = DateTimeOffset.UtcNow;
            var draft = new SystemChangeRequest("SRCR-00970", 0, fixture.ProjectId, fixture.ReleaseId,
                "SRCR multi-role", "P", "A", "S", "workflow.author", now);
            draft.AddRequirementChange("workflow.author", "SYSR-00000980", 0, RequirementLevel.System,
                RequirementChangeKind.Introduce, "A statement.", "Rationale", "Test", now);
            db.Add(draft);
            await db.SaveChangesAsync();
            draftId = draft.Id;
        }

        await LoginAsync(client, "workflow.author");
        using var submitted = await client.PostAsJsonAsync($"/api/change-requests/{draftId}/submit",
            new { approvers = new[] { new { userId = "workflow.multirole" } }, mode = "Sequential" });
        var body = await submitted.Content.ReadAsStringAsync();
        Assert.True(submitted.StatusCode == HttpStatusCode.OK, $"{(int)submitted.StatusCode}: {body}");
    }

    [Fact]
    public async Task A_test_lead_only_user_gets_create_decide_submit_capabilities()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        var fixture = await SeedAsync(factory);
        await LoginAsync(client, "workflow.lead");

        var list = await client.GetFromJsonAsync<JsonElement>(
            $"/api/releases/{fixture.ReleaseId}/test-change-reviews");
        Assert.True(list.GetProperty("canCreate").GetBoolean());
        var pending = list.GetProperty("items").EnumerateArray().Single(x =>
            x.GetProperty("id").GetGuid() == fixture.ReviewId);
        Assert.True(pending.GetProperty("capabilities").GetProperty("canDecide").GetBoolean());
        Assert.True(pending.GetProperty("capabilities").GetProperty("canSubmit").GetBoolean());
    }
}
