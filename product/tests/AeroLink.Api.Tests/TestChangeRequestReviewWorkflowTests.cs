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
        baseline.Select(change, "author", now);
        baseline.Freeze("author", now);
        baseline.MarkRequirementsMaterialized("author", new string('a', 64), 1, now);
        var procedure = new TestProcedure(project.Id, "SYSTP-000900", "Workflow procedure",
            "author", now, TestProcedureLevel.System);
        var procedureRevision = new TestProcedureRevision(procedure.Id, 0,
            "Objective", "Preconditions", "Steps", "Expected", TestProcedureState.Approved, "author", now);
        db.AddRange(change, requirement, revision, procedure, procedureRevision,
            new BaselineRequirementSelection(baseline.Id, requirement.Id, revision.Id));

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
                     ("workflow.reviewer", ProgramRole.Reviewer),
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
        var other = new UserAccount("workflow.other", "Other Engineer", "workflow.other@example.test",
            IdentityService.HashPassword(AeroLinkApiFactory.MemberPassword), now);
        db.Add(other);
        db.Add(new ProgramMembership(other.Id, program.Id, ProgramRole.TestEngineer, "test.setup", now));
        var workflowAdmin = new UserAccount("workflow.admin", "Workflow Admin", "workflow.admin@example.test",
            IdentityService.HashPassword(AeroLinkApiFactory.MemberPassword), now);
        db.Add(workflowAdmin);
        db.Add(new ProgramMembership(workflowAdmin.Id, program.Id, ProgramRole.Administrator, "test.setup", now));
        db.Add(new ProgramMembership(workflowAdmin.Id, program.Id, ProgramRole.TestEngineer, "test.setup", now));
        db.Add(new ProgramMembership(workflowAdmin.Id, program.Id, ProgramRole.TestLead, "test.setup", now));
        await db.SaveChangesAsync();

        await impact.RaiseForApprovedChangeRequestAsync(
            await db.SystemChangeRequests.Include(x => x.RequirementChanges).SingleAsync(x => x.Id == change.Id),
            now, default);
        await db.SaveChangesAsync();

        var review = await db.TestChangeReviews.SingleAsync(x => x.ChangeRequestId == change.Id
            && x.Discipline == TestChangeReviewDiscipline.System);
        var item = await db.VerificationImpactItems.SingleAsync(x => x.ChangeRequestId == change.Id);
        item.LinkRequirementRevision(revision.Id, now);
        await db.SaveChangesAsync();
        return new(project.Id, release.Id, change.Id, review.Id, item.Id, revision.Id, baseline.Id);
    }

    private static async Task LoginAsync(HttpClient client, string user, string password = AeroLinkApiFactory.MemberPassword)
    {
        using var login = await client.PostAsJsonAsync("/api/auth/login",
            new { userName = user, password });
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
            new { rationale = "Stage one is sound.", password = AeroLinkApiFactory.MemberPassword, meaning = "I approve the first review stage." });
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
            new { rationale = "Stage two is sound.", password = AeroLinkApiFactory.MemberPassword, meaning = "I approve the final review stage." });
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
            $"/api/test-change-reviews/{fixture.ReviewId}/approve", new { rationale = "One.", password = AeroLinkApiFactory.MemberPassword, meaning = "I approve stage one." });
        Assert.True(first.IsSuccessStatusCode, await first.Content.ReadAsStringAsync());
        Assert.Equal("InReview", (await first.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("state").GetString());

        await LoginAsync(client, "workflow.two");
        using var second = await client.PostAsJsonAsync(
            $"/api/test-change-reviews/{fixture.ReviewId}/approve", new { rationale = "Two.", password = AeroLinkApiFactory.MemberPassword, meaning = "I approve stage two." });
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
            $"/api/test-change-reviews/{fixture.ReviewId}/approve", new { rationale = "Too early.", password = AeroLinkApiFactory.MemberPassword, meaning = "I approve this stage." });
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
            $"/api/test-change-reviews/{fixture.ReviewId}/approve", new { rationale = "Rework is sound.", password = AeroLinkApiFactory.MemberPassword, meaning = "I approve the reworked stage." });
        Assert.True(approveOne.IsSuccessStatusCode, await approveOne.Content.ReadAsStringAsync());
        await LoginAsync(client, "workflow.two");
        using var approveTwo = await client.PostAsJsonAsync(
            $"/api/test-change-reviews/{fixture.ReviewId}/approve", new { rationale = "Approved.", password = AeroLinkApiFactory.MemberPassword, meaning = "I approve the final reworked stage." });
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
            $"/api/test-change-reviews/{fixture.ReviewId}/approve", new { rationale = "Approved.", password = AeroLinkApiFactory.MemberPassword, meaning = "I approve this exact package." });
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

    [Theory]
    [InlineData(null)]
    [InlineData("not-the-current-password")]
    public async Task Missing_or_incorrect_password_refuses_signature_without_any_partial_transition(string? password)
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        var fixture = await SeedAsync(factory);
        await PreparePackageAsync(client, fixture);
        await SubmitAsync(client, fixture.ReviewId, new { approverId = "workflow.one" });
        int notificationsBefore;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            notificationsBefore = await db.UserNotifications.CountAsync();
        }

        await LoginAsync(client, "workflow.one");
        using var refused = await client.PostAsJsonAsync($"/api/test-change-reviews/{fixture.ReviewId}/approve",
            new { rationale = "The engineering case is sound.", password, meaning = "I approve this exact package." });
        Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
        Assert.Equal("electronic_signature_confirmation_failed",
            (await refused.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());

        using var verificationScope = factory.Services.CreateScope();
        var verificationDb = verificationScope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        var review = await verificationDb.TestChangeReviews.SingleAsync(x => x.Id == fixture.ReviewId);
        var cycle = await verificationDb.ReviewCycles.Include(x => x.Steps)
            .SingleAsync(x => x.TestChangeReviewId == fixture.ReviewId);
        Assert.Equal(TestChangeReviewState.InReview, review.State);
        Assert.Equal(ReviewCycleState.Active, cycle.State);
        Assert.All(cycle.Steps, step => Assert.Null(step.DecidedAt));
        Assert.False(await verificationDb.ElectronicSignatures.AnyAsync(x => x.ArtifactId == fixture.ReviewId));
        Assert.Equal(notificationsBefore, await verificationDb.UserNotifications.CountAsync());
    }

    [Fact]
    public async Task Missing_signature_meaning_is_refused_without_advancing_the_active_stage()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        var fixture = await SeedAsync(factory);
        await PreparePackageAsync(client, fixture);
        await SubmitAsync(client, fixture.ReviewId, new { approverId = "workflow.one" });
        await LoginAsync(client, "workflow.one");

        using var refused = await client.PostAsJsonAsync($"/api/test-change-reviews/{fixture.ReviewId}/approve",
            new { rationale = "The engineering case is sound.", password = AeroLinkApiFactory.MemberPassword, meaning = "" });
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Equal("signature_meaning_required",
            (await refused.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        Assert.Equal(TestChangeReviewState.InReview,
            (await db.TestChangeReviews.SingleAsync(x => x.Id == fixture.ReviewId)).State);
        Assert.False(await db.ElectronicSignatures.AnyAsync(x => x.ArtifactId == fixture.ReviewId));
    }

    [Theory]
    [InlineData("workflow.lead", ProgramRole.TestLead)]
    [InlineData("workflow.config", ProgramRole.ConfigurationManager)]
    public async Task Configured_stage_signs_with_its_frozen_authority_without_generic_Approver(
        string signer, ProgramRole authority)
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        var fixture = await SeedAsync(factory);
        await CreateWorkflowAsync(client, fixture.ProjectId, "Sequential", ("Governed stage", authority.ToString()));
        await PreparePackageAsync(client, fixture);
        await SubmitAsync(client, fixture.ReviewId, new { approvers = new[] { new { userId = signer } } });
        await LoginAsync(client, signer);

        const string rationale = "The engineering case and controlled procedure decision are acceptable.";
        var meaning = $"I approve this exact package in my frozen {authority} authority.";
        using var approved = await client.PostAsJsonAsync($"/api/test-change-reviews/{fixture.ReviewId}/approve",
            new { rationale, password = AeroLinkApiFactory.MemberPassword, meaning });
        Assert.True(approved.IsSuccessStatusCode, await approved.Content.ReadAsStringAsync());

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        var review = await db.TestChangeReviews.SingleAsync(x => x.Id == fixture.ReviewId);
        var cycle = await db.ReviewCycles.Include(x => x.Steps)
            .SingleAsync(x => x.TestChangeReviewId == fixture.ReviewId);
        var signature = await db.ElectronicSignatures.SingleAsync(x => x.ArtifactId == fixture.ReviewId);
        Assert.Equal(rationale, review.ApprovalRationale);
        Assert.Equal(meaning, signature.Meaning);
        Assert.NotEqual(rationale, signature.Meaning);
        Assert.Equal(authority.ToString(), signature.Authority);
        Assert.Equal(cycle.SnapshotHash, signature.ContentHash);
        Assert.Equal(signer, signature.UserName);
        Assert.Equal(cycle.Steps.Single().DecidedAt, signature.SignedAt);
    }

    [Fact]
    public async Task No_workflow_fallback_accepts_current_delegated_Approver_authority()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        var fixture = await SeedAsync(factory);
        await PreparePackageAsync(client, fixture);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var delegator = await db.UserAccounts.SingleAsync(x => x.UserName == "workflow.one");
            var reviewer = await db.UserAccounts.SingleAsync(x => x.UserName == "workflow.reviewer");
            var programId = await db.Projects.Where(x => x.Id == fixture.ProjectId).Select(x => x.ProgramId).SingleAsync();
            var now = DateTimeOffset.UtcNow;
            db.RoleDelegations.Add(new(programId, delegator.Id, reviewer.Id, ProgramRole.Approver,
                now.AddMinutes(-1), now.AddHours(1), "Temporary approval coverage.", "test.setup", now));
            await db.SaveChangesAsync();
        }
        await LoginAsync(client, "workflow.engineer");
        await SubmitAsync(client, fixture.ReviewId, new { approverId = "workflow.reviewer" });
        await LoginAsync(client, "workflow.reviewer");

        using var approved = await client.PostAsJsonAsync($"/api/test-change-reviews/{fixture.ReviewId}/approve",
            new { rationale = "Approved under current delegation.", password = AeroLinkApiFactory.MemberPassword, meaning = "I approve as the current delegated Approver." });
        Assert.True(approved.IsSuccessStatusCode, await approved.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Revoked_no_workflow_delegation_cannot_sign_and_changes_nothing()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        var fixture = await SeedAsync(factory);
        await PreparePackageAsync(client, fixture);
        Guid delegationId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var delegator = await db.UserAccounts.SingleAsync(x => x.UserName == "workflow.one");
            var reviewer = await db.UserAccounts.SingleAsync(x => x.UserName == "workflow.reviewer");
            var programId = await db.Projects.Where(x => x.Id == fixture.ProjectId).Select(x => x.ProgramId).SingleAsync();
            var now = DateTimeOffset.UtcNow;
            var delegation = new RoleDelegation(programId, delegator.Id, reviewer.Id, ProgramRole.Approver,
                now.AddMinutes(-1), now.AddHours(1), "Temporary approval coverage.", "test.setup", now);
            delegationId = delegation.Id;
            db.RoleDelegations.Add(delegation);
            await db.SaveChangesAsync();
        }
        await LoginAsync(client, "workflow.engineer");
        await SubmitAsync(client, fixture.ReviewId, new { approverId = "workflow.reviewer" });
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            (await db.RoleDelegations.SingleAsync(x => x.Id == delegationId)).Revoke(DateTimeOffset.UtcNow);
            await db.SaveChangesAsync();
        }
        await LoginAsync(client, "workflow.reviewer");

        using var refused = await client.PostAsJsonAsync($"/api/test-change-reviews/{fixture.ReviewId}/approve",
            new { rationale = "Should not apply.", password = AeroLinkApiFactory.MemberPassword, meaning = "Should not be recorded." });
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        using var verificationScope = factory.Services.CreateScope();
        var verificationDb = verificationScope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        Assert.Equal(TestChangeReviewState.InReview,
            (await verificationDb.TestChangeReviews.SingleAsync(x => x.Id == fixture.ReviewId)).State);
        Assert.False(await verificationDb.ElectronicSignatures.AnyAsync(x => x.ArtifactId == fixture.ReviewId));
    }

    [Fact]
    public async Task Expired_no_workflow_delegation_cannot_complete_a_legacy_cycle()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        var fixture = await SeedAsync(factory);
        await PreparePackageAsync(client, fixture);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var review = await db.TestChangeReviews.Include(x => x.ReviewCycles)
                .SingleAsync(x => x.Id == fixture.ReviewId);
            var delegator = await db.UserAccounts.SingleAsync(x => x.UserName == "workflow.one");
            var reviewer = await db.UserAccounts.SingleAsync(x => x.UserName == "workflow.reviewer");
            var programId = await db.Projects.Where(x => x.Id == fixture.ProjectId).Select(x => x.ProgramId).SingleAsync();
            var now = DateTimeOffset.UtcNow;
            db.RoleDelegations.Add(new(programId, delegator.Id, reviewer.Id, ProgramRole.Approver,
                now.AddHours(-2), now.AddHours(-1), "Expired approval coverage.", "test.setup", now.AddHours(-3)));
            // This represents a historical no-workflow cycle selected before the defensive signing check.
            review.SubmitForReview("workflow.engineer",
                [new ApproverSelection("workflow.reviewer", "workflow.reviewer", ProgramRole.Approver)], true, now);
            await db.SaveChangesAsync();
        }
        await LoginAsync(client, "workflow.reviewer");

        using var refused = await client.PostAsJsonAsync($"/api/test-change-reviews/{fixture.ReviewId}/approve",
            new { rationale = "Should not apply.", password = AeroLinkApiFactory.MemberPassword, meaning = "Should not be recorded." });
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        using var verificationScope = factory.Services.CreateScope();
        var verificationDb = verificationScope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        Assert.Equal(TestChangeReviewState.InReview,
            (await verificationDb.TestChangeReviews.SingleAsync(x => x.Id == fixture.ReviewId)).State);
        Assert.False(await verificationDb.ElectronicSignatures.AnyAsync(x => x.ArtifactId == fixture.ReviewId));
    }

    [Fact]
    public async Task No_workflow_fallback_preserves_administrator_substitution()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        await SecurityBoundaryTests.BootstrapAndLoginAdministratorAsync(client);
        var fixture = await SeedAsync(factory);
        await PreparePackageAsync(client, fixture);
        await LoginAsync(client, "workflow.engineer");
        await SubmitAsync(client, fixture.ReviewId, new { approverId = "admin" });
        await LoginAsync(client, "admin", AeroLinkApiFactory.AdministratorPassword);

        using var approved = await client.PostAsJsonAsync($"/api/test-change-reviews/{fixture.ReviewId}/approve",
            new { rationale = "Administrator substitution is required.", password = AeroLinkApiFactory.AdministratorPassword, meaning = "I approve under administrator substitution." });
        Assert.True(approved.IsSuccessStatusCode, await approved.Content.ReadAsStringAsync());
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
            $"/api/test-change-reviews/{fixture.ReviewId}/approve", new { rationale = "Stage one.", password = AeroLinkApiFactory.MemberPassword, meaning = "I approve stage one." });
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
            $"/api/test-change-reviews/{fixture.ReviewId}/approve", new { rationale = "Signed as Test Lead.", password = AeroLinkApiFactory.MemberPassword, meaning = "I approve in the frozen Test Lead authority." });
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

    private static async Task<Guid> CreateSystemDraftAsync(AeroLinkApiFactory factory, Fixture fixture)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        var now = DateTimeOffset.UtcNow;
        var draft = new SystemChangeRequest("SRCR-00980", 0, fixture.ProjectId, fixture.ReleaseId,
            "SRCR approval path", "P", "A", "S", "workflow.author", now);
        draft.AddRequirementChange("workflow.author", "SYSR-00000990", 0, RequirementLevel.System,
            RequirementChangeKind.Introduce, "A statement.", "Rationale", "Test", now);
        db.Add(draft);
        await db.SaveChangesAsync();
        return draft.Id;
    }

    [Fact]
    public async Task A_test_lead_only_stage_approves_a_system_change_request_through_the_real_route()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        var fixture = await SeedAsync(factory);
        await CreateWorkflowAsync(client, fixture.ProjectId, "Sequential", "System",
            ("Lead review", nameof(ProgramRole.TestLead)));
        var draftId = await CreateSystemDraftAsync(factory, fixture);

        await LoginAsync(client, "workflow.author");
        using var submitted = await client.PostAsJsonAsync($"/api/change-requests/{draftId}/submit",
            new { approvers = new[] { new { userId = "workflow.lead" } }, mode = "Sequential" });
        Assert.True(submitted.IsSuccessStatusCode, await submitted.Content.ReadAsStringAsync());

        await LoginAsync(client, "workflow.lead");
        using var approved = await client.PostAsJsonAsync($"/api/change-requests/{draftId}/approve",
            new { password = AeroLinkApiFactory.MemberPassword, meaning = "Signed as the Test Lead stage." });
        var body = await approved.Content.ReadAsStringAsync();
        Assert.True(approved.StatusCode == HttpStatusCode.OK, $"{(int)approved.StatusCode}: {body}");

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var step = await db.ReviewCycles.Include(x => x.Steps)
                .SelectMany(x => x.Steps).SingleAsync(x => x.ApproverId == "workflow.lead");
            Assert.Equal("TestLead", step.Authority);
            Assert.True(await db.ElectronicSignatures.AnyAsync(x =>
                x.ArtifactId == draftId && x.UserName == "workflow.lead"));
        }
    }

    [Fact]
    public async Task A_configuration_manager_only_stage_approves_a_system_change_request_through_the_real_route()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        var fixture = await SeedAsync(factory);
        await CreateWorkflowAsync(client, fixture.ProjectId, "Sequential", "System",
            ("Configuration review", nameof(ProgramRole.ConfigurationManager)));
        var draftId = await CreateSystemDraftAsync(factory, fixture);

        await LoginAsync(client, "workflow.author");
        using var submitted = await client.PostAsJsonAsync($"/api/change-requests/{draftId}/submit",
            new { approvers = new[] { new { userId = "workflow.config" } }, mode = "Sequential" });
        Assert.True(submitted.IsSuccessStatusCode, await submitted.Content.ReadAsStringAsync());

        await LoginAsync(client, "workflow.config");
        using var approved = await client.PostAsJsonAsync($"/api/change-requests/{draftId}/approve",
            new { password = AeroLinkApiFactory.MemberPassword, meaning = "Signed as the Configuration Manager stage." });
        var body = await approved.Content.ReadAsStringAsync();
        Assert.True(approved.StatusCode == HttpStatusCode.OK, $"{(int)approved.StatusCode}: {body}");

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var step = await db.ReviewCycles.Include(x => x.Steps)
                .SelectMany(x => x.Steps).SingleAsync(x => x.ApproverId == "workflow.config");
            Assert.Equal("ConfigurationManager", step.Authority);
        }
    }

    [Theory]
    [InlineData("workflow.outsider")]   // Engineer only
    [InlineData("workflow.other")]      // TestEngineer only
    [InlineData("workflow.reviewer")]   // Reviewer only
    public async Task No_workflow_srcr_refuses_a_reviewer_without_approver_authority(string reviewer)
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        var fixture = await SeedAsync(factory);
        var draftId = await CreateSystemDraftAsync(factory, fixture);

        await LoginAsync(client, "workflow.author");
        using var refused = await client.PostAsJsonAsync($"/api/change-requests/{draftId}/submit",
            new { approvers = new[] { new { userId = reviewer } }, mode = "Sequential" });
        var body = await refused.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Contains("does not hold Approver authority", body);
        Assert.Contains("no review workflow configured", body);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var scr = await db.SystemChangeRequests.Include(x => x.ReviewCycles)
                .SingleAsync(x => x.Id == draftId);
            Assert.Equal(ChangeRequestState.Draft, scr.State);
            Assert.Empty(scr.ReviewCycles);
            Assert.False(await db.ElectronicSignatures.AnyAsync(x => x.ArtifactId == draftId));
        }
    }

    [Fact]
    public async Task No_workflow_srcr_generic_approver_submits_returns_and_approves_through_the_real_route()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        var fixture = await SeedAsync(factory);
        var draftId = await CreateSystemDraftAsync(factory, fixture);

        await LoginAsync(client, "workflow.author");
        using var submitted = await client.PostAsJsonAsync($"/api/change-requests/{draftId}/submit",
            new { approvers = new[] { new { userId = "workflow.one" } }, mode = "Sequential" });
        Assert.True(submitted.IsSuccessStatusCode, await submitted.Content.ReadAsStringAsync());

        // Request-changes stays available to the same eligible reviewer under the fallback.
        await LoginAsync(client, "workflow.one");
        using var returned = await client.PostAsJsonAsync($"/api/change-requests/{draftId}/request-changes",
            new { reason = "Rework the statement." });
        var returnBody = await returned.Content.ReadAsStringAsync();
        Assert.True(returned.StatusCode == HttpStatusCode.OK, $"{(int)returned.StatusCode}: {returnBody}");
        Assert.Equal("Draft", JsonSerializer.Deserialize<JsonElement>(returnBody).GetProperty("state").GetString());

        await LoginAsync(client, "workflow.author");
        using var resubmitted = await client.PostAsJsonAsync($"/api/change-requests/{draftId}/submit",
            new { approvers = new[] { new { userId = "workflow.one" } }, mode = "Sequential" });
        Assert.True(resubmitted.IsSuccessStatusCode, await resubmitted.Content.ReadAsStringAsync());

        await LoginAsync(client, "workflow.one");
        using var approved = await client.PostAsJsonAsync($"/api/change-requests/{draftId}/approve",
            new { password = AeroLinkApiFactory.MemberPassword, meaning = "Signed as the fallback approver." });
        var body = await approved.Content.ReadAsStringAsync();
        Assert.True(approved.StatusCode == HttpStatusCode.OK, $"{(int)approved.StatusCode}: {body}");
        Assert.Equal("Approved", JsonSerializer.Deserialize<JsonElement>(body).GetProperty("state").GetString());

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            Assert.True(await db.ElectronicSignatures.AnyAsync(x =>
                x.ArtifactId == draftId && x.UserName == "workflow.one"));
        }
    }

    [Fact]
    public async Task No_workflow_srcr_system_administrator_substitution_submits_and_approves()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        await SecurityBoundaryTests.BootstrapAndLoginAdministratorAsync(client);
        var fixture = await SeedAsync(factory);
        var draftId = await CreateSystemDraftAsync(factory, fixture);

        using var submitted = await client.PostAsJsonAsync($"/api/change-requests/{draftId}/submit",
            new { approvers = new[] { new { userId = "admin" } }, mode = "Sequential" });
        Assert.True(submitted.IsSuccessStatusCode, await submitted.Content.ReadAsStringAsync());

        using var approved = await client.PostAsJsonAsync($"/api/change-requests/{draftId}/approve",
            new { password = AeroLinkApiFactory.AdministratorPassword, meaning = "Administrator substitution." });
        var body = await approved.Content.ReadAsStringAsync();
        Assert.True(approved.StatusCode == HttpStatusCode.OK, $"{(int)approved.StatusCode}: {body}");
        Assert.Equal("Approved", JsonSerializer.Deserialize<JsonElement>(body).GetProperty("state").GetString());

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            Assert.True(await db.ElectronicSignatures.AnyAsync(x =>
                x.ArtifactId == draftId && x.UserName == "admin"));
        }
    }

    [Fact]
    public async Task No_workflow_srcr_defensive_checks_refuse_a_legacy_cycle_with_an_ineligible_reviewer()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        var fixture = await SeedAsync(factory);
        var draftId = await CreateSystemDraftAsync(factory, fixture);

        // A cycle created before the submit-time rule existed: the domain accepted an Engineer as the
        // sole reviewer, which the old approval gate should never have let complete.
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var draft = await db.SystemChangeRequests.Include(x => x.RequirementChanges)
                .SingleAsync(x => x.Id == draftId);
            draft.SubmitForReview("workflow.author",
                [new ApproverSelection("workflow.outsider", "Outsider")], DateTimeOffset.UtcNow);
            await db.SaveChangesAsync();
        }

        await LoginAsync(client, "workflow.outsider");
        using var refusedApprove = await client.PostAsJsonAsync($"/api/change-requests/{draftId}/approve",
            new { password = AeroLinkApiFactory.MemberPassword, meaning = "Not entitled." });
        Assert.Equal(HttpStatusCode.Forbidden, refusedApprove.StatusCode);
        using var refusedReturn = await client.PostAsJsonAsync($"/api/change-requests/{draftId}/request-changes",
            new { reason = "Not entitled either." });
        Assert.Equal(HttpStatusCode.Forbidden, refusedReturn.StatusCode);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var scr = await db.SystemChangeRequests.Include(x => x.ReviewCycles)
                .SingleAsync(x => x.Id == draftId);
            Assert.Equal(ChangeRequestState.InReview, scr.State);
            Assert.Equal(ReviewCycleState.Active, scr.ReviewCycles.Single().State);
            Assert.False(await db.ElectronicSignatures.AnyAsync(x => x.ArtifactId == draftId));
        }
    }

    [Fact]
    public async Task An_inactive_or_wrong_system_approver_is_refused()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        var fixture = await SeedAsync(factory);
        await CreateWorkflowAsync(client, fixture.ProjectId, "Sequential", "System",
            ("Lead review", nameof(ProgramRole.TestLead)), ("Assurance", nameof(ProgramRole.Approver)));
        var draftId = await CreateSystemDraftAsync(factory, fixture);

        await LoginAsync(client, "workflow.author");
        using var submitted = await client.PostAsJsonAsync($"/api/change-requests/{draftId}/submit",
            new { approvers = new[] { new { userId = "workflow.lead" }, new { userId = "workflow.two" } }, mode = "Sequential" });
        Assert.True(submitted.IsSuccessStatusCode, await submitted.Content.ReadAsStringAsync());

        // The second stage is Pending: its approver cannot sign yet.
        await LoginAsync(client, "workflow.two");
        using var early = await client.PostAsJsonAsync($"/api/change-requests/{draftId}/approve",
            new { password = AeroLinkApiFactory.MemberPassword, meaning = "Too early." });
        Assert.Equal(HttpStatusCode.BadRequest, early.StatusCode);

        // The author is not the active approver.
        await LoginAsync(client, "workflow.author");
        using var wrong = await client.PostAsJsonAsync($"/api/change-requests/{draftId}/approve",
            new { password = AeroLinkApiFactory.MemberPassword, meaning = "Not the approver." });
        Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);
    }

    [Fact]
    public async Task Too_few_or_too_many_system_approvers_are_refused_with_stage_guidance()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        var fixture = await SeedAsync(factory);
        await CreateWorkflowAsync(client, fixture.ProjectId, "Sequential", "System",
            ("First", nameof(ProgramRole.Approver)), ("Second", nameof(ProgramRole.Approver)));
        var draftId = await CreateSystemDraftAsync(factory, fixture);
        await LoginAsync(client, "workflow.author");

        using var tooFew = await client.PostAsJsonAsync($"/api/change-requests/{draftId}/submit",
            new { approvers = new[] { new { userId = "workflow.one" } }, mode = "Sequential" });
        Assert.Equal(HttpStatusCode.BadRequest, tooFew.StatusCode);
        var tooFewBody = await tooFew.Content.ReadAsStringAsync();
        Assert.Contains("requires 2 approvers", tooFewBody);
        Assert.Contains("First", tooFewBody);
        Assert.Contains("Second", tooFewBody);

        using var tooMany = await client.PostAsJsonAsync($"/api/change-requests/{draftId}/submit",
            new { approvers = new[] { new { userId = "workflow.one" }, new { userId = "workflow.two" }, new { userId = "workflow.lead" } }, mode = "Sequential" });
        Assert.Equal(HttpStatusCode.BadRequest, tooMany.StatusCode);

        // Restart-review enforces the same count before touching the cycle.
        using var valid = await client.PostAsJsonAsync($"/api/change-requests/{draftId}/submit",
            new { approvers = new[] { new { userId = "workflow.one" }, new { userId = "workflow.two" } }, mode = "Sequential" });
        Assert.True(valid.IsSuccessStatusCode, await valid.Content.ReadAsStringAsync());
        using var restartTooMany = await client.PostAsJsonAsync($"/api/change-requests/{draftId}/restart-review",
            new { approvers = new[] { new { userId = "workflow.one" } }, reason = "Wrong count." });
        Assert.Equal(HttpStatusCode.BadRequest, restartTooMany.StatusCode);
        Assert.Contains("requires 2 approvers", await restartTooMany.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Reload_after_return_and_resubmit_shows_the_newest_cycle()
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
        await LoginAsync(client, "workflow.one");
        using var returned = await client.PostAsJsonAsync($"/api/test-change-reviews/{fixture.ReviewId}/return",
            new { rationale = "Rework." });
        Assert.True(returned.IsSuccessStatusCode, await returned.Content.ReadAsStringAsync());
        await LoginAsync(client, "workflow.engineer");
        await SubmitAsync(client, fixture.ReviewId, new
        {
            approvers = new[] { new { userId = "workflow.one" }, new { userId = "workflow.two" } }
        });

        var item = await ReadItemAsync(client, fixture.ReleaseId, fixture.ReviewId);
        Assert.Equal(2, item.GetProperty("reviewCycle").GetProperty("sequence").GetInt32());
        await LoginAsync(client, "workflow.one");
        var asOne = await ReadItemAsync(client, fixture.ReleaseId, fixture.ReviewId);
        Assert.True(asOne.GetProperty("capabilities").GetProperty("canApprove").GetBoolean());
        await LoginAsync(client, "workflow.two");
        var asTwo = await ReadItemAsync(client, fixture.ReleaseId, fixture.ReviewId);
        Assert.False(asTwo.GetProperty("capabilities").GetProperty("canApprove").GetBoolean());
    }

    [Fact]
    public async Task Assignment_authority_matrix_controls_capabilities_and_mutations()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        var fixture = await SeedAsync(factory);

        // Test Lead assigns the automatic review to the first engineer.
        await LoginAsync(client, "workflow.lead");
        using var assigned = await client.PostAsJsonAsync(
            $"/api/test-change-reviews/{fixture.ReviewId}/assign",
            new { engineerId = "workflow.engineer" });
        Assert.True(assigned.IsSuccessStatusCode, await assigned.Content.ReadAsStringAsync());

        // The unrelated engineer sees no decide/submit capability and cannot mutate the decision.
        await LoginAsync(client, "workflow.other");
        var asOther = await ReadItemAsync(client, fixture.ReleaseId, fixture.ReviewId);
        Assert.False(asOther.GetProperty("capabilities").GetProperty("canDecide").GetBoolean());
        Assert.False(asOther.GetProperty("capabilities").GetProperty("canSubmit").GetBoolean());
        using var refusedResolve = await client.PostAsJsonAsync($"/api/verification-impact/{fixture.ItemId}/resolve",
            new { outcome = "NoTestRequired", rationale = "Not mine." });
        Assert.Equal(HttpStatusCode.Forbidden, refusedResolve.StatusCode);
        using var refusedLink = await client.PostAsJsonAsync(
            $"/api/test-change-reviews/{fixture.ReviewId}/problem-reports",
            new { problemReportIds = Array.Empty<Guid>() });
        Assert.Equal(HttpStatusCode.Forbidden, refusedLink.StatusCode);

        // The holder sees and can use the capabilities.
        await LoginAsync(client, "workflow.engineer");
        var asHolder = await ReadItemAsync(client, fixture.ReleaseId, fixture.ReviewId);
        Assert.True(asHolder.GetProperty("capabilities").GetProperty("canDecide").GetBoolean());
        Assert.True(asHolder.GetProperty("capabilities").GetProperty("canSubmit").GetBoolean());
        using var allowedResolve = await client.PostAsJsonAsync($"/api/verification-impact/{fixture.ItemId}/resolve",
            new { outcome = "NoTestRequired", rationale = "Mine to decide." });
        Assert.True(allowedResolve.IsSuccessStatusCode, await allowedResolve.Content.ReadAsStringAsync());

        // The Test Lead retains supervisory decide/submit capability even though the package is held.
        await LoginAsync(client, "workflow.lead");
        var asLead = await ReadItemAsync(client, fixture.ReleaseId, fixture.ReviewId);
        Assert.True(asLead.GetProperty("capabilities").GetProperty("canDecide").GetBoolean());
        Assert.True(asLead.GetProperty("capabilities").GetProperty("canSubmit").GetBoolean());

        // Administrator can also act.
        await LoginAsync(client, "workflow.admin");
        var asAdmin = await ReadItemAsync(client, fixture.ReleaseId, fixture.ReviewId);
        Assert.True(asAdmin.GetProperty("capabilities").GetProperty("canDecide").GetBoolean());
        Assert.True(asAdmin.GetProperty("capabilities").GetProperty("canSubmit").GetBoolean());
    }
}
