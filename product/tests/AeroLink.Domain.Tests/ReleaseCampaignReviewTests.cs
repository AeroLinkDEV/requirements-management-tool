using AeroLink.Domain.Baselines;
using AeroLink.Domain.Programs;
using AeroLink.Domain.Releases;

namespace AeroLink.Domain.Tests;

public sealed class ReleaseCampaignReviewTests
{
    [Fact]
    public void Review_can_restart_after_cancellation_with_history_retained()
    {
        var now = DateTimeOffset.UtcNow;
        var program = new ProgramRecord("Restart Program", $"RP{Guid.NewGuid():N}"[..12]);
        var project = new ProjectRecord(program.Id, "Restart Product", "Restart System");
        var release = new SoftwareRelease(project.Id, "1.0", false);
        var baseline = new CandidateBaseline("SW-01.00", 0, project.Id, release.Id, null, "Restart baseline", "cm", now);
        var build = new SoftwareBuild(project.Id, release.Id, baseline.Id, "SW-01.00", "Restart build", "cm", now);
        var campaign = new ReleaseCampaign(project.Id, release.Id, baseline.Id, "Restart campaign", "cm", now);

        campaign.StartVerification("cm", now.AddMinutes(1));
        campaign.SelectVerificationBuild(build.Id, "cm", now.AddMinutes(2));
        campaign.BeginReleaseReview("cm", [("release.approver", "Release Approver")], new string('a', 64), now.AddMinutes(3));
        campaign.Approve("release.approver", now.AddMinutes(4));
        campaign.CancelReleaseReview("cm", "Package changed.", now.AddMinutes(5));
        campaign.BeginReleaseReview("cm", [("release.approver", "Release Approver"), ("release.approver2", "Second Approver")], new string('b', 64), now.AddMinutes(6));

        Assert.Equal(ReleaseCampaignState.InReview, campaign.State);
        Assert.Equal(new string('b', 64), campaign.ReleaseHash);
        Assert.Equal(3, campaign.Approvals.Count);
        Assert.Equal(ReleaseApprovalState.Cancelled, campaign.Approvals.First().State);
        Assert.Equal(1, campaign.Approvals.First().Cycle);
        Assert.Equal(ReleaseApprovalState.Active, campaign.Approvals.Skip(1).First().State);
        Assert.Equal(ReleaseApprovalState.Pending, campaign.Approvals.Last().State);
        Assert.Equal(2, campaign.Approvals.Skip(1).First().Cycle);
        Assert.Equal(2, campaign.Approvals.Last().Cycle);
        Assert.True(campaign.Version > 1);
    }
}
