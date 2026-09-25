using AeroLink.Domain.Common;
using AeroLink.Domain.Releases;

namespace AeroLink.Domain.Tests;

public sealed class ReleaseCampaignTests
{
    [Fact]
    public void Ordered_release_approval_is_unanimous_and_release_is_immutable()
    {
        var now = DateTimeOffset.UtcNow; var campaign = new ReleaseCampaign(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "1.6", "owner", now);
        var buildId = Guid.NewGuid(); var manifestHash = new string('a', 64);
        campaign.StartVerification("owner", now); campaign.SelectVerificationBuild(buildId, "owner", now);
        campaign.BeginReleaseReview("owner", [("systems", "Systems"), ("manager", "Manager")], manifestHash, now);
        Assert.Equal(manifestHash, campaign.ReleaseHash);
        Assert.ThrowsAny<Exception>(() => campaign.SelectVerificationBuild(Guid.NewGuid(), "owner", now));
        Assert.False(campaign.Approve("systems", now)); Assert.True(campaign.Approve("manager", now));
        Assert.ThrowsAny<Exception>(() => campaign.Release(buildId, new string('b', 64), "manager", now));
        campaign.Release(buildId, manifestHash, "manager", now);
        Assert.Equal(ReleaseCampaignState.Released, campaign.State); Assert.ThrowsAny<Exception>(() => campaign.StartVerification("owner", now));
    }

    [Fact]
    public void Only_the_active_approver_can_approve_so_approval_order_is_enforced()
    {
        var (campaign, _, _, now) = CampaignInReview();

        var error = Assert.Throws<DomainException>(() => campaign.Approve("manager", now));
        Assert.Equal("Only the active release approver can approve.", error.Message);

        Assert.False(campaign.Approve("systems", now));
        Assert.True(campaign.Approve("manager", now));
    }

    [Fact]
    public void A_release_before_every_approver_has_approved_is_refused()
    {
        var (campaign, buildId, manifestHash, now) = CampaignInReview();
        Assert.False(campaign.Approve("systems", now));

        var error = Assert.Throws<DomainException>(() => campaign.Release(buildId, manifestHash, "manager", now));
        Assert.Equal("Every configured release approver must approve before release.", error.Message);
        Assert.Equal(ReleaseCampaignState.InReview, campaign.State);
    }

    private static (ReleaseCampaign Campaign, Guid BuildId, string ManifestHash, DateTimeOffset Now) CampaignInReview()
    {
        var now = DateTimeOffset.UtcNow;
        var campaign = new ReleaseCampaign(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "1.6", "owner", now);
        var buildId = Guid.NewGuid(); var manifestHash = new string('a', 64);
        campaign.StartVerification("owner", now); campaign.SelectVerificationBuild(buildId, "owner", now);
        campaign.BeginReleaseReview("owner", [("systems", "Systems"), ("manager", "Manager")], manifestHash, now);
        return (campaign, buildId, manifestHash, now);
    }

    [Fact]
    public void Impact_disposition_requires_rationale()
    {
        var impact = new ChangeImpactDisposition(Guid.NewGuid(), Guid.NewGuid(), ImpactKind.Traceability, "HLR-00000001.00", "Review trace impact");
        Assert.ThrowsAny<Exception>(() => impact.Disposition(ImpactDispositionState.Addressed, "", "engineer", DateTimeOffset.UtcNow));
        impact.Disposition(ImpactDispositionState.NotApplicable, "No downstream allocation changes.", "engineer", DateTimeOffset.UtcNow);
        Assert.Equal(ImpactDispositionState.NotApplicable, impact.State);
    }
}
