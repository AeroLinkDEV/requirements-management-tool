using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Hierarchy;
using AeroLink.Domain.Verification;

namespace AeroLink.Domain.Tests;

/// <summary>
/// The one authoritative effective-executable rule from #720/#726: System executes its Procedure; software
/// Case-only executes the Case; software Case+Procedure executes the Procedure. Every execution, test-set,
/// readiness, workspace and materialization consumer routes through this answer instead of guessing.
/// </summary>
public sealed class EffectiveExecutableArtifactTests
{
    private static ILadderPolicy Policy(RequirementLevel target, params VerificationArtifactKind[] kinds)
    {
        var catalogue = LegacyLadderPolicy.Instance;
        var steps = catalogue.OrderedLevels.Select((level, index) => new ResolvedProjectLadderStep(
            level, index + 1, catalogue.Definition(level).Capabilities,
            level == target
                ? kinds
                : catalogue.Definition(level).VerificationProfile?.EnabledKinds)).ToArray();
        var relationships = catalogue.ParentRelationships
            .Select(edge => new ResolvedProjectLadderRelationship(edge.Parent, edge.Child)).ToArray();
        return new ResolvedProjectLadderPolicy(new ResolvedProjectLadder(
            Guid.NewGuid(), ProjectLadderConfigurationClassification.NonDefault,
            ProjectLadderConfigurationState.Active, steps, relationships), catalogue);
    }

    [Fact]
    public void System_executes_its_procedure()
    {
        Assert.Equal(VerificationArtifactKind.Procedure,
            EffectiveExecutableArtifact.KindFor(LegacyLadderPolicy.Instance, RequirementLevel.System));
    }

    [Fact]
    public void Case_only_software_executes_the_case()
    {
        var policy = Policy(RequirementLevel.HighLevel, VerificationArtifactKind.Case);
        Assert.Equal(VerificationArtifactKind.Case,
            EffectiveExecutableArtifact.KindFor(policy, RequirementLevel.HighLevel));
        Assert.True(EffectiveExecutableArtifact.IsExecutable(
            policy, TestProcedureLevel.HighLevel, VerificationArtifactKind.Case));
        Assert.False(EffectiveExecutableArtifact.IsExecutable(
            policy, TestProcedureLevel.HighLevel, VerificationArtifactKind.Procedure));
    }

    [Fact]
    public void Full_software_profile_executes_the_procedure()
    {
        var policy = Policy(RequirementLevel.LowLevel,
            VerificationArtifactKind.Case, VerificationArtifactKind.Procedure);
        Assert.Equal(VerificationArtifactKind.Procedure,
            EffectiveExecutableArtifact.KindFor(policy, RequirementLevel.LowLevel));
        Assert.True(EffectiveExecutableArtifact.IsExecutable(
            policy, TestProcedureLevel.LowLevel, VerificationArtifactKind.Procedure));
        Assert.False(EffectiveExecutableArtifact.IsExecutable(
            policy, TestProcedureLevel.LowLevel, VerificationArtifactKind.Case));
    }

    [Fact]
    public void Enabled_bindings_include_both_kinds_under_the_full_profile()
    {
        var policy = Policy(RequirementLevel.HighLevel,
            VerificationArtifactKind.Case, VerificationArtifactKind.Procedure);
        var bindings = EffectiveExecutableArtifact.EnabledBindings(policy);
        Assert.Contains(bindings, binding => binding.Level == TestProcedureLevel.HighLevel
            && binding.Kind == VerificationArtifactKind.Case);
        Assert.Contains(bindings, binding => binding.Level == TestProcedureLevel.HighLevel
            && binding.Kind == VerificationArtifactKind.Procedure);
        Assert.Equal(VerificationArtifactKind.Procedure,
            EffectiveExecutableArtifact.Bindings(policy)
                .Single(binding => binding.Level == TestProcedureLevel.HighLevel).Kind);
    }
}
