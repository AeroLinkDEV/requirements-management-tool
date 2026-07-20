using AeroLink.Domain.Common;
using AeroLink.Domain.Requirements;

namespace AeroLink.Domain.Tests;

public sealed class ControlledArtifactEditingTests
{
    [Fact]
    public void Registry_covers_every_AeroLink_3_controlled_draft_family()
    {
        var expected = Enum.GetValues<ControlledArtifactFamily>();

        Assert.Equal(expected.Length, ControlledArtifactEditPolicies.All.Count);
        Assert.All(expected, family =>
            Assert.Contains(ControlledArtifactEditPolicies.All, policy => policy.Family == family));
        Assert.All(ControlledArtifactEditPolicies.All, policy => Assert.True(policy.Exclusive));
    }

    [Theory]
    [InlineData("SCR", ControlledArtifactFamily.ChangeRequest, "ChangeRequest")]
    [InlineData("swcr", ControlledArtifactFamily.ChangeRequest, "ChangeRequest")]
    [InlineData("PR", ControlledArtifactFamily.ProblemReport, "ProblemReport")]
    [InlineData("TestProcedureRevision", ControlledArtifactFamily.TestProcedure, "TestProcedure")]
    [InlineData("CandidateBaseline", ControlledArtifactFamily.ReleasePlanning, "ReleasePlanning")]
    [InlineData("SpecificationNode", ControlledArtifactFamily.SpecificationStructure, "SpecificationStructure")]
    public void Aliases_resolve_to_one_canonical_policy(
        string alias,
        ControlledArtifactFamily expectedFamily,
        string expectedCanonicalType)
    {
        var policy = ControlledArtifactEditPolicies.Resolve(alias);

        Assert.Equal(expectedFamily, policy.Family);
        Assert.Equal(expectedCanonicalType, policy.CanonicalType);
        Assert.Equal(expectedCanonicalType, ControlledArtifactEditPolicies.Canonicalize(alias));
    }

    [Fact]
    public void Lease_policy_preserves_the_existing_safe_bounds()
    {
        var policy = ControlledArtifactEditPolicies.Resolve("SCR");

        Assert.Equal(15, policy.NormalizeLease(null));
        Assert.Equal(2, policy.NormalizeLease(2));
        Assert.Equal(120, policy.NormalizeLease(120));
        Assert.Throws<DomainException>(() => policy.NormalizeLease(1));
        Assert.Throws<DomainException>(() => policy.NormalizeLease(121));
    }

    [Theory]
    [InlineData("SCR", "Draft", true)]
    [InlineData("SCR", "Approved", false)]
    [InlineData("TestProcedure", "ChangesRequested", true)]
    [InlineData("TestProcedure", "Approved", false)]
    [InlineData("ProblemReport", "Investigating", true)]
    [InlineData("ConfigurationChangeSet", "Conflict", true)]
    public void Lifecycle_state_eligibility_is_explicit(
        string artifactType,
        string state,
        bool expected)
    {
        Assert.Equal(expected, ControlledArtifactEditPolicies.Resolve(artifactType).IsEditableState(state));
    }

    [Fact]
    public void Unsupported_artifact_types_fail_closed()
    {
        Assert.False(ControlledArtifactEditPolicies.TryResolve("UnknownArtifact", out _));
        Assert.Throws<DomainException>(() => ControlledArtifactEditPolicies.Resolve("UnknownArtifact"));
    }
}
