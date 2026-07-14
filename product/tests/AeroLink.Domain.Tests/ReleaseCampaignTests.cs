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
    public void Impact_disposition_requires_rationale()
    {
        var impact = new ChangeImpactDisposition(Guid.NewGuid(), Guid.NewGuid(), ImpactKind.Traceability, "HLR-00000001.00", "Review trace impact");
        Assert.ThrowsAny<Exception>(() => impact.Disposition(ImpactDispositionState.Addressed, "", "engineer", DateTimeOffset.UtcNow));
        impact.Disposition(ImpactDispositionState.NotApplicable, "No downstream allocation changes.", "engineer", DateTimeOffset.UtcNow);
        Assert.Equal(ImpactDispositionState.NotApplicable, impact.State);
    }
}
