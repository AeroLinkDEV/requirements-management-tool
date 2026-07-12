using AeroLink.Domain.Releases;

namespace AeroLink.Domain.Tests;

public sealed class ReleaseCampaignTests
{
    [Fact]
    public void Ordered_release_approval_is_unanimous_and_release_is_immutable()
    {
        var now = DateTimeOffset.UtcNow; var campaign = new ReleaseCampaign(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "1.6", "owner", now);
        campaign.StartVerification("owner", now); campaign.BeginReleaseReview("owner", [("systems", "Systems"), ("manager", "Manager")], now);
        Assert.False(campaign.Approve("systems", now)); Assert.True(campaign.Approve("manager", now)); campaign.Release(Guid.NewGuid(), new string('a', 64), "manager", now);
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
