using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AeroLink.Domain.Baselines;
using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Hierarchy;
using AeroLink.Domain.Identity;
using AeroLink.Domain.Integrations;
using AeroLink.Domain.Programs;
using AeroLink.Domain.Releases;
using AeroLink.Domain.Requirements;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AeroLink.Api.Tests;

public sealed class CodeReviewLifecycleApiTests
{
    [Fact]
    public async Task Reopened_and_rematerialized_baseline_preserves_signed_v1_history_and_reviews_new_source_as_v2()
    {
        using var factory = new AeroLinkApiFactory();
        using var author = factory.CreateClient();
        using var approver = factory.CreateClient();
        Guid projectId, releaseId, baselineId, campaignId;
        const string owner = "code.lifecycle.cm", reviewer = "code.lifecycle.approver";
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var now = DateTimeOffset.UtcNow;
            var program = new ProgramRecord("Code lifecycle", "CLIFE");
            var project = new ProjectRecord(program.Id, "Code lifecycle", "Synthetic controlled history");
            var release = new SoftwareRelease(project.Id, "1.0", false);
            var ladder = ProjectLadderConfiguration.CreateDraft(project.Id, now);
            ladder.Steps.Add(new ProjectLadderStep(ladder.Id, project.Id, RequirementLevel.System, 1,
                LevelCapabilities.HasChangeControl, now, []));
            db.AddRange(program, project, release, ladder);
            await db.SaveChangesAsync();
            var activated = await scope.ServiceProvider.GetRequiredService<ProjectLadderAuthoringService>()
                .ActivateAsync(project.Id, new(ladder.Version, "Qualify supported System-only lifecycle."), owner, now, default);
            Assert.Equal(ProjectLadderActivationResultKind.Success, activated.Kind);
            var change = new SystemChangeRequest("SRCR-00110", 0, project.Id, release.Id,
                "Controlled history", "Problem", "Analysis", "Solution", owner, now);
            change.AddRequirementChange(owner, "SYSR-00000005", 1, RequirementLevel.System,
                RequirementChangeKind.Introduce, "The system shall retain controlled history.", "New", "Test", now);
            change.SubmitForReview(owner, [new(reviewer, "Lifecycle Approver")], now);
            change.ApproveActiveStage(reviewer, now);
            var baseline = new CandidateBaseline("SW-01.00", 0, project.Id, release.Id, null, "Lifecycle", owner, now);
            baseline.Select(change, owner, now);
            var build = new SoftwareBuild(project.Id, release.Id, baseline.Id, "SW-01.00", "Lifecycle", owner, now);
            var campaign = new ReleaseCampaign(project.Id, release.Id, baseline.Id, "Lifecycle", owner, now);
            campaign.StartVerification(owner, now);
            campaign.SelectVerificationBuild(build.Id, owner, now);
            var impact = new ChangeImpactDisposition(campaign.Id, change.Id, ImpactKind.Requirement, "SYSR-00000005", "Review lifecycle fixture");
            impact.Disposition(ImpactDispositionState.Addressed, "Exact baseline requirement selected.", owner, now);
            var cm = new UserAccount(owner, "Lifecycle CM", owner + "@example.test", IdentityService.HashPassword(AeroLinkApiFactory.MemberPassword), now);
            var reviewerAccount = new UserAccount(reviewer, "Lifecycle Approver", reviewer + "@example.test", IdentityService.HashPassword(AeroLinkApiFactory.MemberPassword), now);
            db.AddRange(change, baseline, build, campaign, impact, cm, reviewerAccount,
                new ProgramMembership(cm.Id, program.Id, ProgramRole.Engineer, "setup", now),
                new ProgramMembership(cm.Id, program.Id, ProgramRole.ConfigurationManager, "setup", now),
                new ProjectLeadershipAssignment(program.Id, ProjectLeadershipPosition.ConfigurationManager, cm.Id, "setup", now),
                new ProgramMembership(reviewerAccount.Id, program.Id, ProgramRole.Approver, "setup", now));
            await db.SaveChangesAsync();
            projectId = project.Id; releaseId = release.Id; baselineId = baseline.Id; campaignId = campaign.Id;
        }
        await Ok(author, "/api/auth/login", new { userName = owner, password = AeroLinkApiFactory.MemberPassword });
        await Ok(approver, "/api/auth/login", new { userName = reviewer, password = AeroLinkApiFactory.MemberPassword });
        async Task Seal()
        {
            await Ok(author, $"/api/baselines/{baselineId}/freeze", new { });
            await Ok(author, $"/api/baselines/{baselineId}/materialize-requirements", new { });
        }
        async Task<string> Review()
        {
            using (var scope = factory.Services.CreateScope())
            {
                var readiness = await scope.ServiceProvider.GetRequiredService<ReleaseReadinessService>().CalculateAsync(campaignId, default);
                Assert.All(readiness.Gates.Where(x => x.Code != "release_approval"), gate => Assert.True(gate.Complete, gate.Name));
            }
            return (await Ok(author, $"/api/release-campaigns/{campaignId}/review", new { approvers = new[] { new { userId = reviewer } } }))
                .GetProperty("manifestHash").GetString()!;
        }
        await Seal();
        var firstHash = await Review();
        await Ok(approver, $"/api/release-campaigns/{campaignId}/approve", new
        { password = AeroLinkApiFactory.MemberPassword, meaning = "Approve exact legacy package.", expectedManifestHash = firstHash });
        await Ok(author, $"/api/release-campaigns/{campaignId}/review/cancel", new { reason = "Reconfirm source after rematerialization." });
        await Ok(author, $"/api/baselines/{baselineId}/reopen", new { reason = "Exercise supported rematerialization." });
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            Assert.False(await db.RequirementRevisions.AnyAsync(x => x.EffectiveBaselineId == baselineId));
            var now = DateTimeOffset.UtcNow;
            var repository = new ProjectRepositoryConfiguration(projectId, ProjectRepositorySetupMode.ConnectNow, "GitLab", "https://gitlab.example/demo/source", owner, now);
            var snapshot = new GitLabSourceSnapshot(projectId, repository.Id, "https://gitlab.example", 17, "demo/source", new string('b', 40), "main", owner, now, repository.Version);
            var selection = new GitLabSourceSelectionEvent(projectId, releaseId, snapshot.Id, 0, owner, now);
            db.AddRange(repository, snapshot, selection, new GitLabCurrentSourceSelection(projectId, releaseId, snapshot.Id, selection.Id, owner, now));
            await db.SaveChangesAsync();
        }
        await Seal();
        var secondHash = await Review();
        Assert.NotEqual(firstHash, secondHash);
        using var stale = await approver.PostAsJsonAsync($"/api/release-campaigns/{campaignId}/approve", new
        { password = AeroLinkApiFactory.MemberPassword, meaning = "Approve stale package.", expectedManifestHash = firstHash });
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        using var verification = factory.Services.CreateScope();
        var finalDb = verification.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        var formats = await finalDb.CodeReviewCycleManifestIdentities.Where(x => x.ReleaseCampaignId == campaignId).OrderBy(x => x.ApprovalCycle).ToListAsync();
        Assert.Equal(new[] { 1, 2 }, formats.Select(x => x.FormatVersion));
        Assert.Equal(new[] { firstHash, secondHash }, formats.Select(x => x.ManifestHash));
        var signature = await finalDb.ElectronicSignatures.SingleAsync(x => x.ArtifactId == campaignId);
        Assert.Equal(firstHash, signature.ContentHash);
        Assert.Equal(1, signature.ReviewCycle);
        Assert.Equal(2, await finalDb.ReleaseApprovals.Where(x => x.CampaignId == campaignId).CountAsync());
        Assert.Equal(ReleaseApprovalState.Cancelled, (await finalDb.ReleaseApprovals.SingleAsync(x => x.CampaignId == campaignId && x.Cycle == 1)).State);
        Assert.Equal(2, await finalDb.ReleaseCampaignEvents.CountAsync(x => x.CampaignId == campaignId && x.EventType == "ReleaseReviewStarted"));
        Assert.Equal(1, await finalDb.ReleaseCampaignEvents.CountAsync(x => x.CampaignId == campaignId && x.EventType == "ReleaseReviewCancelled"));
    }

    private static async Task<JsonElement> Ok(HttpClient client, string url, object body)
    {
        using var response = await client.PostAsJsonAsync(url, body);
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{url}: {response.StatusCode} {text}");
        return JsonSerializer.Deserialize<JsonElement>(text);
    }
}
