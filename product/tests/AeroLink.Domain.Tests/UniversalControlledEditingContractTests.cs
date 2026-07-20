using AeroLink.Domain.Common;
using AeroLink.Domain.Requirements;

namespace AeroLink.Domain.Tests;

public sealed class UniversalControlledEditingContractTests
{
    [Fact]
    public void Every_policy_has_one_canonical_alias_and_safe_lease_bounds()
    {
        Assert.All(ControlledArtifactEditPolicies.All, policy =>
        {
            Assert.Contains(policy.Aliases, alias => string.Equals(alias, policy.CanonicalType, StringComparison.OrdinalIgnoreCase));
            Assert.True(policy.Exclusive);
            Assert.InRange(policy.DefaultLeaseMinutes, policy.MinimumLeaseMinutes, policy.MaximumLeaseMinutes);
        });
    }

    [Theory]
    [InlineData("ChangeRequest", "Draft", true)]
    [InlineData("ChangeRequest", "InReview", false)]
    [InlineData("RequirementProposal", "Draft", true)]
    [InlineData("SpecificationStructure", "InWork", true)]
    [InlineData("TestProcedure", "Draft", true)]
    [InlineData("TestProcedure", "Approved", false)]
    [InlineData("TraceLinkProposal", "Proposed", true)]
    [InlineData("ReleasePlanning", "Candidate", true)]
    [InlineData("ReleasePlanning", "Released", false)]
    public void Existing_family_states_are_fail_closed(string artifactType, string state, bool editable)
    {
        Assert.Equal(editable, ControlledArtifactEditPolicies.Resolve(artifactType).IsEditableState(state));
    }

    [Fact]
    public void Lease_normalization_rejects_values_outside_the_shared_contract()
    {
        foreach (var policy in ControlledArtifactEditPolicies.All)
        {
            Assert.Equal(15, policy.NormalizeLease(null));
            Assert.Throws<DomainException>(() => policy.NormalizeLease(policy.MinimumLeaseMinutes - 1));
            Assert.Throws<DomainException>(() => policy.NormalizeLease(policy.MaximumLeaseMinutes + 1));
        }
    }
}