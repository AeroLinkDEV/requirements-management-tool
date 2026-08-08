using AeroLink.Domain.Common;
using AeroLink.Domain.Requirements;

namespace AeroLink.Domain.Tests;

public sealed class ControlledArtifactEditingTests
{
    [Fact]
    public void Registry_covers_every_AeroLink_3_controlled_draft_family()
    {
        // TestProcedure is retained as an enum value for historical records but is deliberately not a
        // controlled draft family: DEC-103 governs procedure change through a Test Change Request, so no
        // direct universal-editing policy exists for it.
        var expected = Enum.GetValues<ControlledArtifactFamily>()
            .Where(family => family != ControlledArtifactFamily.TestProcedure);

        Assert.Equal(expected.Count(), ControlledArtifactEditPolicies.All.Count);
        Assert.All(expected, family =>
            Assert.Contains(ControlledArtifactEditPolicies.All, policy => policy.Family == family));
        Assert.All(ControlledArtifactEditPolicies.All, policy => Assert.True(policy.Exclusive));
        Assert.DoesNotContain(ControlledArtifactEditPolicies.All,
            policy => policy.Family == ControlledArtifactFamily.TestProcedure);
    }

    [Theory]
    [InlineData("SCR", ControlledArtifactFamily.ChangeRequest, "ChangeRequest")]
    [InlineData("swcr", ControlledArtifactFamily.ChangeRequest, "ChangeRequest")]
    [InlineData("PR", ControlledArtifactFamily.ProblemReport, "ProblemReport")]
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

        // Test procedures and their historical aliases no longer resolve to any editing policy.
        Assert.False(ControlledArtifactEditPolicies.TryResolve("TestProcedure", out _));
        Assert.False(ControlledArtifactEditPolicies.TryResolve("Procedure", out _));
        Assert.False(ControlledArtifactEditPolicies.TryResolve("TestProcedureRevision", out _));
        Assert.Throws<DomainException>(() => ControlledArtifactEditPolicies.Resolve("TestProcedure"));
    }
}
