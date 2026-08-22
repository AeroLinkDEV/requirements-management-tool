using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Common;
using AeroLink.Domain.Hierarchy;
using AeroLink.Domain.Traceability;

namespace AeroLink.Domain.Tests;

public sealed class ResolvedProjectLadderPolicyTests
{
    [Fact]
    public void Compiled_policy_inverts_direct_parent_edges_and_keeps_configured_capabilities()
    {
        var configuration = ProjectLadderConfiguration.CreateDraft(Guid.NewGuid(), DateTimeOffset.UtcNow);
        var system = new ProjectLadderStep(configuration.Id, configuration.ProjectId, RequirementLevel.System, 1,
            LegacyLadderPolicy.Instance.Definition(RequirementLevel.System).Capabilities, DateTimeOffset.UtcNow);
        var low = new ProjectLadderStep(configuration.Id, configuration.ProjectId, RequirementLevel.LowLevel, 2,
            LegacyLadderPolicy.Instance.Definition(RequirementLevel.LowLevel).Capabilities, DateTimeOffset.UtcNow);
        configuration.Steps.Add(system); configuration.Steps.Add(low);
        configuration.AllowedUpstream.Add(new ProjectLadderAllowedUpstream(configuration.Id, configuration.ProjectId,
            system.Id, low.Id, DateTimeOffset.UtcNow));

        var policy = new ResolvedProjectLadderPolicy(ProjectLadderResolver.Resolve(configuration));

        Assert.Equal([RequirementLevel.LowLevel], policy.DownstreamLevels(RequirementLevel.System));
        Assert.Equal([RequirementLevel.System], policy.ParentLevels(RequirementLevel.LowLevel));
        Assert.True(policy.AcceptsChangeRequest(ChangeRequestType.Software, RequirementLevel.LowLevel));
        Assert.False(policy.TryParseRequirementLevel("HighLevel", out _));
        Assert.Equal(RequirementLevel.System, policy.ParseImportedRequirementLevel(null));
        Assert.Equal(RequirementLevel.System, policy.ParseImportedRequirementLevel("retired-level"));
    }

    [Fact]
    public void Configured_trace_policy_enforces_orientation_and_legacy_stays_permissive()
    {
        var configuration = ProjectLadderConfiguration.CreateDraft(Guid.NewGuid(), DateTimeOffset.UtcNow);
        var system = new ProjectLadderStep(configuration.Id, configuration.ProjectId, RequirementLevel.System, 1,
            LegacyLadderPolicy.Instance.Definition(RequirementLevel.System).Capabilities, DateTimeOffset.UtcNow);
        var low = new ProjectLadderStep(configuration.Id, configuration.ProjectId, RequirementLevel.LowLevel, 2,
            LegacyLadderPolicy.Instance.Definition(RequirementLevel.LowLevel).Capabilities, DateTimeOffset.UtcNow);
        configuration.Steps.Add(system); configuration.Steps.Add(low);
        configuration.AllowedUpstream.Add(new ProjectLadderAllowedUpstream(configuration.Id, configuration.ProjectId,
            system.Id, low.Id, DateTimeOffset.UtcNow));
        var configured = new ResolvedProjectLadderPolicy(ProjectLadderResolver.Resolve(configuration));

        RequirementTracePolicy.Validate(configured, RequirementLevel.LowLevel, RequirementLevel.System,
            RequirementTraceType.DerivedFrom);
        RequirementTracePolicy.Validate(configured, RequirementLevel.LowLevel, RequirementLevel.System,
            RequirementTraceType.AllocatedFrom);
        Assert.Throws<DomainException>(() => RequirementTracePolicy.Validate(configured,
            RequirementLevel.System, RequirementLevel.LowLevel, RequirementTraceType.DerivedFrom));
        Assert.Throws<DomainException>(() => RequirementTracePolicy.Validate(configured,
            RequirementLevel.System, RequirementLevel.LowLevel, RequirementTraceType.AllocatedFrom));
        RequirementTracePolicy.Validate(LegacyLadderPolicy.Instance, RequirementLevel.System,
            RequirementLevel.System, RequirementTraceType.AllocatedFrom);
    }

    [Fact]
    public void Configured_parent_lookup_is_direct_and_supports_multi_parent_shared_children()
    {
        var configuration = ProjectLadderConfiguration.CreateDraft(Guid.NewGuid(), DateTimeOffset.UtcNow);
        var system = new ProjectLadderStep(configuration.Id, configuration.ProjectId, RequirementLevel.System, 1,
            LegacyLadderPolicy.Instance.Definition(RequirementLevel.System).Capabilities, DateTimeOffset.UtcNow);
        var high = new ProjectLadderStep(configuration.Id, configuration.ProjectId, RequirementLevel.HighLevel, 2,
            LegacyLadderPolicy.Instance.Definition(RequirementLevel.HighLevel).Capabilities, DateTimeOffset.UtcNow);
        var low = new ProjectLadderStep(configuration.Id, configuration.ProjectId, RequirementLevel.LowLevel, 3,
            LegacyLadderPolicy.Instance.Definition(RequirementLevel.LowLevel).Capabilities, DateTimeOffset.UtcNow);
        configuration.Steps.Add(system); configuration.Steps.Add(high); configuration.Steps.Add(low);
        configuration.AllowedUpstream.Add(new(configuration.Id, configuration.ProjectId, system.Id, low.Id, DateTimeOffset.UtcNow));
        configuration.AllowedUpstream.Add(new(configuration.Id, configuration.ProjectId, high.Id, low.Id, DateTimeOffset.UtcNow));

        var policy = new ResolvedProjectLadderPolicy(ProjectLadderResolver.Resolve(configuration));

        Assert.Equal([RequirementLevel.System, RequirementLevel.HighLevel], policy.ParentLevels(RequirementLevel.LowLevel));
        Assert.Equal([RequirementLevel.LowLevel], policy.DownstreamLevels(RequirementLevel.System));
        Assert.Equal([RequirementLevel.LowLevel], policy.DownstreamLevels(RequirementLevel.HighLevel));
    }

    [Fact]
    public void Configured_interface_above_system_allows_system_trace_to_interface_and_direct_downstream_fan_out()
    {
        var configuration = ProjectLadderConfiguration.CreateDraft(Guid.NewGuid(), DateTimeOffset.UtcNow);
        var interfaceStep = new ProjectLadderStep(configuration.Id, configuration.ProjectId, RequirementLevel.Interface, 1,
            LegacyLadderPolicy.Instance.Definition(RequirementLevel.Interface).Capabilities, DateTimeOffset.UtcNow);
        var system = new ProjectLadderStep(configuration.Id, configuration.ProjectId, RequirementLevel.System, 2,
            LegacyLadderPolicy.Instance.Definition(RequirementLevel.System).Capabilities, DateTimeOffset.UtcNow);
        var high = new ProjectLadderStep(configuration.Id, configuration.ProjectId, RequirementLevel.HighLevel, 3,
            LegacyLadderPolicy.Instance.Definition(RequirementLevel.HighLevel).Capabilities, DateTimeOffset.UtcNow);
        configuration.Steps.Add(interfaceStep); configuration.Steps.Add(system); configuration.Steps.Add(high);
        configuration.AllowedUpstream.Add(new(configuration.Id, configuration.ProjectId,
            interfaceStep.Id, system.Id, DateTimeOffset.UtcNow));
        configuration.AllowedUpstream.Add(new(configuration.Id, configuration.ProjectId,
            interfaceStep.Id, high.Id, DateTimeOffset.UtcNow));

        var policy = new ResolvedProjectLadderPolicy(ProjectLadderResolver.Resolve(configuration));

        Assert.Equal([RequirementLevel.System, RequirementLevel.HighLevel], policy.DownstreamLevels(RequirementLevel.Interface));
        Assert.Equal([RequirementLevel.Interface], policy.ParentLevels(RequirementLevel.System));
        RequirementTracePolicy.Validate(policy, RequirementLevel.System, RequirementLevel.Interface,
            RequirementTraceType.AllocatedFrom);
        Assert.True(policy.AcceptsChangeRequest(ChangeRequestType.Interface, null, RequirementLevel.Interface));
        Assert.Equal("ICDCR", policy.ChangeRequestPrefix(ChangeRequestType.Interface, null));
    }
}
