using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Common;
using AeroLink.Domain.Hierarchy;

namespace AeroLink.Domain.Tests;

public sealed class ProjectLadderConfigurationTests
{
    private static readonly ILadderPolicy Policy = LegacyLadderPolicy.Instance;

    [Fact]
    public void A_legacy_default_resolves_to_the_complete_catalogue_and_linear_upstream_graph()
    {
        var projectId = Guid.NewGuid();
        var configuration = new ProjectLadderConfiguration(projectId, DateTimeOffset.UtcNow);
        var steps = AddSteps(configuration, Policy.OrderedLevels);
        configuration.AllowedUpstream.Add(new ProjectLadderAllowedUpstream(
            configuration.Id, projectId, steps[0].Id, steps[1].Id, DateTimeOffset.UtcNow));
        configuration.AllowedUpstream.Add(new ProjectLadderAllowedUpstream(
            configuration.Id, projectId, steps[1].Id, steps[2].Id, DateTimeOffset.UtcNow));

        var resolved = ProjectLadderResolver.Resolve(configuration);

        Assert.True(resolved.AgreesWithLegacyDefault());
        Assert.Equal(Policy.OrderedLevels, resolved.Steps.Select(x => x.Level));
        Assert.Equal(
            new[]
            {
                new ResolvedProjectLadderRelationship(RequirementLevel.System, RequirementLevel.HighLevel),
                new ResolvedProjectLadderRelationship(RequirementLevel.HighLevel, RequirementLevel.LowLevel)
            },
            resolved.AllowedUpstream);
    }

    [Fact]
    public void Shared_legacy_default_factory_builds_the_exact_storage_graph_used_by_creation_paths()
    {
        var configuration = LegacyDefaultProjectLadderFactory.Create(Guid.NewGuid(), DateTimeOffset.UtcNow);

        var resolved = ProjectLadderResolver.Resolve(configuration);

        Assert.True(resolved.AgreesWithLegacyDefault());
        Assert.Equal(ProjectLadderConfigurationClassification.LegacyDefault, configuration.Classification);
        Assert.Equal(ProjectLadderConfigurationState.Stored, configuration.State);
    }

    [Fact]
    public void A_non_default_ladder_may_be_a_nonempty_contiguous_subset_with_supported_capabilities()
    {
        var projectId = Guid.NewGuid();
        var configuration = ProjectLadderConfiguration.CreateDraft(projectId, DateTimeOffset.UtcNow);
        AddSteps(configuration, [RequirementLevel.System, RequirementLevel.LowLevel]);

        var resolved = ProjectLadderResolver.Resolve(configuration);

        Assert.Equal(ProjectLadderConfigurationClassification.NonDefault, resolved.Classification);
        Assert.Equal(ProjectLadderConfigurationState.Draft, resolved.State);
        Assert.Equal([RequirementLevel.System, RequirementLevel.LowLevel], resolved.Steps.Select(x => x.Level));
        Assert.False(resolved.AgreesWithLegacyDefault());
    }

    [Fact]
    public void A_ladder_rejects_non_contiguous_positions_and_capabilities_disabled_by_the_catalogue()
    {
        var projectId = Guid.NewGuid();
        var configuration = ProjectLadderConfiguration.CreateDraft(projectId, DateTimeOffset.UtcNow);
        configuration.Steps.Add(new ProjectLadderStep(configuration.Id, projectId, RequirementLevel.System, 2,
            Policy.Definition(RequirementLevel.System).Capabilities, DateTimeOffset.UtcNow));
        configuration.Steps.Add(new ProjectLadderStep(configuration.Id, projectId, RequirementLevel.LowLevel, 3,
            Policy.Definition(RequirementLevel.LowLevel).Capabilities, DateTimeOffset.UtcNow));

        var positionError = Assert.Throws<DomainException>(() => ProjectLadderResolver.Resolve(configuration));
        Assert.Contains("contiguous", positionError.Message);

        var invalid = ProjectLadderConfiguration.CreateDraft(Guid.NewGuid(), DateTimeOffset.UtcNow);
        invalid.Steps.Add(new ProjectLadderStep(invalid.Id, invalid.ProjectId, RequirementLevel.System, 1,
            LevelCapabilities.HasChangeControl | LevelCapabilities.HasVerification
            | LevelCapabilities.HasRequirementsDocument | LevelCapabilities.HasCodeTraceability,
            DateTimeOffset.UtcNow));

        var capabilityError = Assert.Throws<DomainException>(() => ProjectLadderResolver.Resolve(invalid));
        Assert.Contains("exceed", capabilityError.Message);
    }

    [Fact]
    public void Draft_storage_is_not_a_public_active_configuration_path_and_relationships_reject_self_edges()
    {
        var configuration = ProjectLadderConfiguration.CreateDraft(Guid.NewGuid(), DateTimeOffset.UtcNow);
        Assert.Equal(ProjectLadderConfigurationState.Draft, configuration.State);

        var step = new ProjectLadderStep(configuration.Id, configuration.ProjectId, RequirementLevel.System, 1,
            Policy.Definition(RequirementLevel.System).Capabilities, DateTimeOffset.UtcNow);
        var error = Assert.Throws<DomainException>(() => new ProjectLadderAllowedUpstream(
            configuration.Id, configuration.ProjectId, step.Id, step.Id, DateTimeOffset.UtcNow));
        Assert.Contains("itself", error.Message);
    }

    private static List<ProjectLadderStep> AddSteps(ProjectLadderConfiguration configuration,
        IReadOnlyList<RequirementLevel> levels)
    {
        var steps = new List<ProjectLadderStep>();
        for (var i = 0; i < levels.Count; i++)
        {
            var level = levels[i];
            var step = new ProjectLadderStep(configuration.Id, configuration.ProjectId, level, i + 1,
                Policy.Definition(level).Capabilities, DateTimeOffset.UtcNow);
            configuration.Steps.Add(step);
            steps.Add(step);
        }

        return steps;
    }
}
