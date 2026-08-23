using AeroLink.Domain.Common;
using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Hierarchy;
using AeroLink.Domain.Verification;

namespace AeroLink.Domain.Tests;

public sealed class ExactParentSelectionTests
{
    [Fact]
    public void Allocated_requires_distinct_nonempty_exact_parent_revisions()
    {
        var first = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var second = Guid.Parse("22222222-2222-2222-2222-222222222222");

        ExactParentSelectionPolicy.Validate(ExactParentClassification.Allocated, [first], null);
        ExactParentSelectionPolicy.Validate(ExactParentClassification.Allocated, [second, first], null);

        Assert.Throws<DomainException>(() =>
            ExactParentSelectionPolicy.Validate(ExactParentClassification.Allocated, [], null));
        Assert.Throws<DomainException>(() =>
            ExactParentSelectionPolicy.Validate(ExactParentClassification.Allocated, [first, first], null));
        Assert.Throws<DomainException>(() =>
            ExactParentSelectionPolicy.Validate(ExactParentClassification.Allocated, [Guid.Empty], null));
    }

    [Fact]
    public void Derived_requires_zero_parents_and_a_nonblank_engineering_rationale()
    {
        ExactParentSelectionPolicy.Validate(ExactParentClassification.Derived, [], "Independent engineering function.");
        ExactParentSelectionPolicy.Validate(ExactParentClassification.Derived, [], "  Independent engineering function.  ");

        var parent = Guid.Parse("33333333-3333-3333-3333-333333333333");
        Assert.Throws<DomainException>(() =>
            ExactParentSelectionPolicy.Validate(ExactParentClassification.Derived, [parent], "Independent."));
        Assert.Throws<DomainException>(() =>
            ExactParentSelectionPolicy.Validate(ExactParentClassification.Derived, [], ""));
        Assert.Throws<DomainException>(() =>
            ExactParentSelectionPolicy.Validate(ExactParentClassification.Derived, [], "   "));
        Assert.Throws<DomainException>(() =>
            ExactParentSelectionPolicy.Validate(ExactParentClassification.Unspecified, [], "Independent."));
    }

    [Fact]
    public void Normalization_is_canonical_and_the_verification_compatibility_wrapper_uses_the_shared_policy()
    {
        var first = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var second = Guid.Parse("22222222-2222-2222-2222-222222222222");
        Assert.Equal([first, second], ExactParentSelectionPolicy.NormalizeIds([second, first]));

        VerificationProcedureParentPolicy.Validate(VerificationProcedureParentKind.Allocated, [first], null);
        VerificationProcedureParentPolicy.Validate(VerificationProcedureParentKind.Derived, [], "Standalone by design.");
        Assert.Throws<DomainException>(() => VerificationProcedureParentPolicy.Validate(
            VerificationProcedureParentKind.Derived, [first], "Standalone by design."));
    }

    [Fact]
    public void The_legacy_ladder_resolves_root_and_each_nonroot_parent_without_treating_unknown_as_root()
    {
        var policy = LegacyLadderPolicy.Instance;
        Assert.Empty(policy.ParentLevels(RequirementLevel.System));
        Assert.Equal([RequirementLevel.System], policy.ParentLevels(RequirementLevel.HighLevel));
        Assert.Equal([RequirementLevel.HighLevel], policy.ParentLevels(RequirementLevel.LowLevel));
        Assert.ThrowsAny<Exception>(() => policy.Definition((RequirementLevel)999));
    }
}
