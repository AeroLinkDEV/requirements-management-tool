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
/// The release approval contract: an electronic release signature must bind exactly the package the
/// approver reviewed. Approvals of a changed package are refused, retries are idempotent, concurrent
/// stale approvals conflict on the campaign version, and the manifest covers every mutable package input.
/// </summary>
public sealed class ReleaseCampaignExactIntentApiTests
{
    private sealed record Scenario(
        Guid ProgramId, Guid ProjectId, Guid CampaignId, Guid ReleaseId, Guid BaselineId, Guid BuildId,
        string ManifestHash, Guid ChangeRequestId);

    [Fact]
    public async Task Approval_after_a_package_change_is_refused_and_only_the_current_manifest_is_signed()
    {
        using var factory = new AeroLinkApiFactory();
        var scenario = await SeedAsync(factory, twoApprovers: true);
        var first = await ApproverClientAsync(factory, "release.approver");
        var second = await ApproverClientAsync(factory, "release.approver2");

        using var firstApproval = await first.PostAsJsonAsync($"/api/release-campaigns/{scenario.CampaignId}/approve",
            new { password = AeroLinkApiFactory.MemberPassword, meaning = "I approve this exact release decision package.", expectedManifestHash = scenario.ManifestHash });
        Assert.Equal(HttpStatusCode.OK, firstApproval.StatusCode);
        Assert.Equal(scenario.ManifestHash, (await firstApproval.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("manifestHash").GetString());

        // The package changes after the first approval while the second approver still holds a stale tab.
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            db.ImpactDispositions.Add(new ChangeImpactDisposition(scenario.CampaignId, scenario.ChangeRequestId,
                ImpactKind.Requirement, "SYSR-000001", "Late impact discovered during review."));
            await db.SaveChangesAsync();
        }

        // A second approval bound to the reviewed hash must be refused: the manifest is now different.
        using var stale = await second.PostAsJsonAsync($"/api/release-campaigns/{scenario.CampaignId}/approve",
            new { password = AeroLinkApiFactory.MemberPassword, meaning = "I approve this exact release decision package.", expectedManifestHash = scenario.ManifestHash });
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        var staleBody = await stale.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("release_manifest_changed", staleBody.GetProperty("code").GetString());
        Assert.NotEqual(scenario.ManifestHash, staleBody.GetProperty("currentManifestHash").GetString());

        // A fabricated expected hash never matches the reviewed manifest.
        using var forged = await second.PostAsJsonAsync($"/api/release-campaigns/{scenario.CampaignId}/approve",
            new { password = AeroLinkApiFactory.MemberPassword, meaning = "I approve this exact release decision package.", expectedManifestHash = new string('0', 64) });
        Assert.Equal(HttpStatusCode.Conflict, forged.StatusCode);
        Assert.Equal("stale_release_package", (await forged.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());

        // The changed package must be re-frozen and re-reviewed: cancel the old review and bind the new
        // manifest, then every approver signs the new package from scratch.
        var current = staleBody.GetProperty("currentManifestHash").GetString()!;
        using (var restartScope = factory.Services.CreateScope())
        {
            var restartDb = restartScope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var restartCampaign = await restartDb.ReleaseCampaigns.Include(x => x.Approvals).SingleAsync(x => x.Id == scenario.CampaignId);
            restartCampaign.CancelReleaseReview("admin", "The package changed after review began.", DateTimeOffset.UtcNow);
            await restartDb.SaveChangesAsync();
        }
        using (var restartScope = factory.Services.CreateScope())
        {
            var restartDb = restartScope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var restartCampaign = await restartDb.ReleaseCampaigns.Include(x => x.Approvals).SingleAsync(x => x.Id == scenario.CampaignId);
            var existingApprovalIds = restartDb.ChangeTracker.Entries<ReleaseApproval>().Select(e => e.Entity.Id).ToHashSet();
            restartCampaign.BeginReleaseReview("admin",
                [("release.approver", "Release Approver"), ("release.approver2", "Second Approver")], current, DateTimeOffset.UtcNow);
            foreach (var approval in restartCampaign.Approvals.Where(x => !existingApprovalIds.Contains(x.Id)))
                restartDb.ReleaseApprovals.Add(approval);
            await restartDb.SaveChangesAsync();
        }
        using var firstAgain = await first.PostAsJsonAsync($"/api/release-campaigns/{scenario.CampaignId}/approve",
            new { password = AeroLinkApiFactory.MemberPassword, meaning = "I approve this exact release decision package.", expectedManifestHash = current });
        Assert.Equal(HttpStatusCode.OK, firstAgain.StatusCode);
        using var accepted = await second.PostAsJsonAsync($"/api/release-campaigns/{scenario.CampaignId}/approve",
            new { password = AeroLinkApiFactory.MemberPassword, meaning = "I approve this exact release decision package.", expectedManifestHash = current });
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var signatures = await db.ElectronicSignatures.AsNoTracking()
                .Where(x => x.ArtifactId == scenario.CampaignId && x.Action == "ApproveRelease").ToListAsync();
            Assert.Equal(3, signatures.Count);
            Assert.Contains(signatures, x => x.UserName == "release.approver" && x.ContentHash == scenario.ManifestHash && x.ReviewStepPosition == 0 && x.ReviewCycle == 1);
            Assert.Contains(signatures, x => x.UserName == "release.approver" && x.ContentHash == current && x.ReviewStepPosition == 0 && x.ReviewCycle == 2);
            Assert.Contains(signatures, x => x.UserName == "release.approver2" && x.ContentHash == current && x.ReviewStepPosition == 1 && x.ReviewCycle == 2);
        }
    }

    [Fact]
    public async Task Approve_retry_is_idempotent_and_a_changed_meaning_conflicts()
    {
        using var factory = new AeroLinkApiFactory();
        var scenario = await SeedAsync(factory, twoApprovers: false);
        var client = await ApproverClientAsync(factory, "release.approver");
        var payload = new { password = AeroLinkApiFactory.MemberPassword, meaning = "I approve this exact release decision package.", expectedManifestHash = scenario.ManifestHash };

        using var first = await client.PostAsJsonAsync($"/api/release-campaigns/{scenario.CampaignId}/approve", payload);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        // A lost response is retried with the identical intent: no second signature, no second event.
        using var retry = await client.PostAsJsonAsync($"/api/release-campaigns/{scenario.CampaignId}/approve", payload);
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        Assert.Equal(scenario.ManifestHash, (await retry.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("manifestHash").GetString());

        using var changedMeaning = await client.PostAsJsonAsync($"/api/release-campaigns/{scenario.CampaignId}/approve",
            new { password = AeroLinkApiFactory.MemberPassword, meaning = "A different meaning on retry.", expectedManifestHash = scenario.ManifestHash });
        Assert.Equal(HttpStatusCode.Conflict, changedMeaning.StatusCode);
        Assert.Equal("decision_already_recorded", (await changedMeaning.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            Assert.Equal(1, await db.ElectronicSignatures.CountAsync(x => x.ArtifactId == scenario.CampaignId && x.Action == "ApproveRelease"));
            Assert.Equal(1, await db.ReleaseCampaignEvents.CountAsync(x => x.CampaignId == scenario.CampaignId && x.EventType == "ReleaseApprovalRecorded"));
        }
    }

    [Fact]
    public async Task Cancel_review_preserves_history_and_an_old_tab_cannot_sign_the_next_cycle()
    {
        using var factory = new AeroLinkApiFactory();
        using var administrator = factory.CreateClient();
        await ProblemReportApiTests.BootstrapAndLoginAsync(administrator);
        var scenario = await SeedAsync(factory, twoApprovers: false);
        var client = await ApproverClientAsync(factory, "release.approver");

        using var approved = await client.PostAsJsonAsync($"/api/release-campaigns/{scenario.CampaignId}/approve",
            new { password = AeroLinkApiFactory.MemberPassword, meaning = "I approve this exact release decision package.", expectedManifestHash = scenario.ManifestHash });
        Assert.Equal(HttpStatusCode.OK, approved.StatusCode);

        using var cancelled = await administrator.PostAsJsonAsync($"/api/release-campaigns/{scenario.CampaignId}/review/cancel",
            new { reason = "The package changed after review began." });
        Assert.Equal(HttpStatusCode.OK, cancelled.StatusCode);
        Assert.Equal("Verification", (await cancelled.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("state").GetString());

        using var staleTab = await client.PostAsJsonAsync($"/api/release-campaigns/{scenario.CampaignId}/approve",
            new { password = AeroLinkApiFactory.MemberPassword, meaning = "I approve this exact release decision package.", expectedManifestHash = scenario.ManifestHash });
        Assert.Equal(HttpStatusCode.Conflict, staleTab.StatusCode);
        Assert.Equal("release_manifest_missing", (await staleTab.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var campaign = await db.ReleaseCampaigns.Include(x => x.Approvals).Include(x => x.Events).SingleAsync(x => x.Id == scenario.CampaignId);
            Assert.Equal(ReleaseCampaignState.Verification, campaign.State);
            Assert.Null(campaign.ReleaseHash);
            Assert.All(campaign.Approvals, approval => Assert.Equal(ReleaseApprovalState.Cancelled, approval.State));
            Assert.Equal(1, await db.ElectronicSignatures.CountAsync(x => x.ArtifactId == scenario.CampaignId && x.Action == "ApproveRelease"));
        Assert.Contains(campaign.Events, x => x.EventType == "ReleaseReviewCancelled");
        Assert.Contains(campaign.Events, x => x.EventType == "ReleaseApprovalRecorded");
        }
    }

    [Fact]
    public async Task Concurrent_stale_approval_writes_conflict_on_the_campaign_version()
    {
        using var factory = new AeroLinkApiFactory();
        var scenario = await SeedAsync(factory, twoApprovers: false);
        var now = DateTimeOffset.UtcNow;

        await using var staleScope = factory.Services.CreateAsyncScope();
        var staleDb = staleScope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        var staleCampaign = await staleDb.ReleaseCampaigns.Include(x => x.Approvals).SingleAsync(x => x.Id == scenario.CampaignId);
        staleCampaign.Approve("release.approver", now);

        await using var winnerScope = factory.Services.CreateAsyncScope();
        var winnerDb = winnerScope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        var winnerCampaign = await winnerDb.ReleaseCampaigns.Include(x => x.Approvals).SingleAsync(x => x.Id == scenario.CampaignId);
        winnerCampaign.Approve("release.approver", now);
        winnerDb.ElectronicSignatures.Add(new(await winnerDb.UserAccounts.Where(x => x.UserName == "release.approver").Select(x => x.Id).SingleAsync(), "release.approver", "Release Approver",
            scenario.ProgramId, "ReleaseCampaign", scenario.CampaignId, "Campaign", "ApproveRelease", "Meaning", scenario.ManifestHash, "test", now, reviewStepPosition: 0));
        await winnerDb.SaveChangesAsync();

        staleDb.ElectronicSignatures.Add(new(await staleDb.UserAccounts.Where(x => x.UserName == "release.approver").Select(x => x.Id).SingleAsync(), "release.approver", "Release Approver",
            scenario.ProgramId, "ReleaseCampaign", scenario.CampaignId, "Campaign", "ApproveRelease", "Meaning", scenario.ManifestHash, "test", now, reviewStepPosition: 0));
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => staleDb.SaveChangesAsync());
    }

    [Fact]
    public async Task Release_manifest_hash_covers_the_selected_test_set_and_tcr_state()
    {
        using var factory = new AeroLinkApiFactory();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        var execution = scope.ServiceProvider.GetRequiredService<ReleaseExecutionService>();
        var scenario = await SeedAsync(factory, twoApprovers: false);
        var baselineHash = await execution.ComputeReviewManifestHashAsync(scenario.CampaignId, default);

        var procedure = new TestProcedure(scenario.ProjectId, "SYSTP-990001", "Late procedure", "test", DateTimeOffset.UtcNow, TestProcedureLevel.System);
        var revision = new TestProcedureRevision(procedure.Id, 0, "Late objective", "Preconditions", "Steps", "Expected",
            TestProcedureState.Approved, "test", DateTimeOffset.UtcNow);
        var testSet = new BuildTestSet(scenario.ProjectId, scenario.ReleaseId, TestChangeReviewDiscipline.System, DateTimeOffset.UtcNow);
        db.AddRange(procedure, revision, testSet, new BuildTestSetEntry(testSet.Id, revision.Id, TestSelectionReason.CoverageArea, "Late set", "test", DateTimeOffset.UtcNow));
        await db.SaveChangesAsync();
        var afterTestSet = await execution.ComputeReviewManifestHashAsync(scenario.CampaignId, default);
        Assert.NotEqual(baselineHash, afterTestSet);

        var change = await db.SystemChangeRequests.SingleAsync(x => x.Id == scenario.ChangeRequestId);
        db.TestChangeReviews.Add(new TestChangeReview(scenario.ProjectId, scenario.ReleaseId, change.Id,
            TestChangeReviewDiscipline.System, "SRCR-09600", DateTimeOffset.UtcNow));
        await db.SaveChangesAsync();
        var afterTcr = await execution.ComputeReviewManifestHashAsync(scenario.CampaignId, default);
        Assert.NotEqual(afterTestSet, afterTcr);
    }

    [Fact]
    public async Task Release_campaign_control_requires_leadership_and_accepts_the_standing_backup()
    {
        using var factory = new AeroLinkApiFactory();
        Guid projectId;
        Guid releaseId;
        Guid baselineId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var now = DateTimeOffset.UtcNow;
            var program = new ProgramRecord("Release Authority Program", "RCA");
            var project = new ProjectRecord(program.Id, "Release Authority", "Release Authority");
            var release = new SoftwareRelease(project.Id, "1.0", false);
            var baseline = new CandidateBaseline("SW-01.00", 0, project.Id, release.Id, null,
                "Release authority baseline", "test.setup", now);
            var baseOnly = new UserAccount("release.cm.base", "Base Configuration Manager",
                "release.cm.base@example.test", IdentityService.HashPassword(AeroLinkApiFactory.MemberPassword), now);
            var backup = new UserAccount("release.cm.backup", "Backup Configuration Manager",
                "release.cm.backup@example.test", IdentityService.HashPassword(AeroLinkApiFactory.MemberPassword), now);
            db.AddRange(program, project, release, baseline, baseOnly, backup,
                new ProgramMembership(baseOnly.Id, program.Id, ProgramRole.ConfigurationManager, "test.setup", now),
                new ProgramMembership(backup.Id, program.Id, ProgramRole.ConfigurationManager, "test.setup", now),
                new ProjectLeadershipBackup(program.Id, ProjectLeadershipPosition.ConfigurationManager,
                    backup.Id, "test.setup", now));
            await db.SaveChangesAsync();
            projectId = project.Id;
            releaseId = release.Id;
            baselineId = baseline.Id;
        }

        using var baseClient = await ApproverClientAsync(factory, "release.cm.base");
        using var refused = await baseClient.PostAsJsonAsync("/api/release-campaigns",
            new { projectId, releaseId, baselineId, name = "Base-only attempt" });
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);

        using var backupClient = await ApproverClientAsync(factory, "release.cm.backup");
        using var accepted = await backupClient.PostAsJsonAsync("/api/release-campaigns",
            new { projectId, releaseId, baselineId, name = "Backup-authorized campaign" });
        Assert.Equal(HttpStatusCode.Created, accepted.StatusCode);
    }

    private static async Task<HttpClient> ApproverClientAsync(AeroLinkApiFactory factory, string userName)
    {
        var client = factory.CreateClient();
        using var login = await client.PostAsJsonAsync("/api/auth/login", new { userName, password = AeroLinkApiFactory.MemberPassword });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        await SecurityBoundaryTests.AuthorizeMutationsAsync(client);
        return client;
    }

    private static async Task<Scenario> SeedAsync(AeroLinkApiFactory factory, bool twoApprovers)
    {
        var now = DateTimeOffset.UtcNow;
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        var program = new ProgramRecord("Release exact intent", $"RE{Guid.NewGuid():N}"[..12]);
        var project = new ProjectRecord(program.Id, "Exact Product", "Exact System");
        var release = new SoftwareRelease(project.Id, "1.6", false);
        var baseline = new CandidateBaseline("SW-09.60", 0, project.Id, release.Id, null, "Exact baseline", "cm", now);
        var build = new SoftwareBuild(project.Id, release.Id, baseline.Id, "SW-09.60", "Exact build", "cm", now);
        var change = new SystemChangeRequest("SRCR-09600", 0, project.Id, release.Id,
            "Exact behavior", "Problem", "Analysis", "Solution", "engineer", now);
        var approver = new UserAccount("release.approver", "Release Approver", "release.approver@example.test",
            IdentityService.HashPassword(AeroLinkApiFactory.MemberPassword), now);
        var secondApprover = new UserAccount("release.approver2", "Second Approver", "release.approver2@example.test",
            IdentityService.HashPassword(AeroLinkApiFactory.MemberPassword), now);
        var campaign = new ReleaseCampaign(project.Id, release.Id, baseline.Id, "Exact release campaign", "cm", now);
        campaign.StartVerification("cm", now.AddMinutes(1));
        campaign.SelectVerificationBuild(build.Id, "cm", now.AddMinutes(2));
        db.AddRange(program, project, release, baseline, build, change, approver, secondApprover,
            new ProgramMembership(approver.Id, program.Id, ProgramRole.Approver, "test.setup", now),
            new ProgramMembership(secondApprover.Id, program.Id, ProgramRole.Approver, "test.setup", now),
            campaign);
        await db.SaveChangesAsync();
        var campaignId = campaign.Id;
        await using var reviewScope = factory.Services.CreateAsyncScope();
        var reviewDb = reviewScope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        var execution = reviewScope.ServiceProvider.GetRequiredService<ReleaseExecutionService>();
        var manifest = await execution.ComputeReviewManifestHashAsync(campaignId, default);
        var approvers = twoApprovers
            ? new List<(string Id, string Name)> { ("release.approver", "Release Approver"), ("release.approver2", "Second Approver") }
            : new List<(string Id, string Name)> { ("release.approver", "Release Approver") };
        var reviewCampaign = await reviewDb.ReleaseCampaigns.Include(x => x.Approvals).SingleAsync(x => x.Id == campaignId);
        reviewCampaign.BeginReleaseReview("cm", approvers, manifest, now.AddMinutes(3));
        reviewDb.ReleaseApprovals.AddRange(reviewCampaign.Approvals);
        await reviewDb.SaveChangesAsync();
        return new(program.Id, project.Id, campaignId, release.Id, baseline.Id, build.Id, manifest, change.Id);
    }
}
