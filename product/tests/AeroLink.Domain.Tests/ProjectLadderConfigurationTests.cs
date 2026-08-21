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

    [Fact]
    public void Canonical_snapshot_hash_is_stable_and_manifest_names_every_unrouted_consumer()
    {
        var steps = new[]
        {
            new LadderStepDraft("LowLevel", 2, LevelCapabilities.HasChangeControl),
            new LadderStepDraft("System", 1, LevelCapabilities.HasChangeControl),
        };
        var edges = new[] { new LadderRelationshipDraft("System", "LowLevel") };

        var first = ProjectLadderSnapshot.Canonicalize(steps, edges);
        var second = ProjectLadderSnapshot.Canonicalize(steps.Reverse(), edges);

        Assert.Equal(first, second);
        Assert.Equal(ProjectLadderSnapshot.Hash(first), ProjectLadderSnapshot.Hash(second));
        Assert.False(LadderConsumerManifestCatalog.Current.IsReady);
        Assert.Contains(LadderConsumerManifestCatalog.Current.MissingOrUnrouted,
            x => x.Id == "release.readiness");
        Assert.NotEmpty(LadderConsumerManifestCatalog.Current.Hash);
    }

    [Fact]
    public void Authored_graph_allows_non_adjacent_upstream_but_refuses_authored_or_persisted_backward_edges()
    {
        var projectId = Guid.NewGuid();
        var policy = LegacyLadderPolicy.Instance;
        var valid = ProjectLadderDraftValidator.Validate(
            [new("System", 1, policy.Definition(RequirementLevel.System).Capabilities), new("LowLevel", 2, policy.Definition(RequirementLevel.LowLevel).Capabilities)],
            [new("System", "LowLevel")], policy);
        Assert.Single(valid.Relationships);
        var reverseError = Assert.Throws<DomainException>(() => ProjectLadderDraftValidator.Validate(
            [new("System", 1, policy.Definition(RequirementLevel.System).Capabilities), new("LowLevel", 2, policy.Definition(RequirementLevel.LowLevel).Capabilities)],
            [new("LowLevel", "System")], policy));
        Assert.Contains("earlier position", reverseError.Message);

        var persistedReverse = ProjectLadderConfiguration.CreateDraft(projectId, DateTimeOffset.UtcNow);
        var persistedSteps = AddSteps(persistedReverse, [RequirementLevel.System, RequirementLevel.LowLevel]);
        persistedReverse.AllowedUpstream.Add(new ProjectLadderAllowedUpstream(persistedReverse.Id, projectId,
            persistedSteps[1].Id, persistedSteps[0].Id, DateTimeOffset.UtcNow));
        var persistedReverseError = Assert.Throws<DomainException>(() => ProjectLadderResolver.Resolve(persistedReverse));
        Assert.Contains("earlier position", persistedReverseError.Message);

    }

    [Fact]
    public void Manifest_inventory_is_exactly_tied_to_the_matrix_and_unknown_routes_fail_closed()
    {
        var matrix = FindMatrix();
        Assert.Contains($"`{LadderConsumerManifestCatalog.Version}`", matrix);
        var ids = LadderConsumerManifestCatalog.Current.Consumers.Select(x => x.Id).ToArray();
        Assert.Equal(ids.Length, ids.Distinct(StringComparer.Ordinal).Count());
        const string startMarker = "<!-- ladder-consumer-ids:start -->";
        const string endMarker = "<!-- ladder-consumer-ids:end -->";
        var start = matrix.IndexOf(startMarker, StringComparison.Ordinal);
        var end = matrix.IndexOf(endMarker, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start, "The matrix must contain the machine-readable manifest ID block.");
        var matrixIds = matrix[(start + startMarker.Length)..end].Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(x => x.Trim()).Where(x => !x.StartsWith("<!--", StringComparison.Ordinal)).ToArray();
        Assert.Equal(ids.OrderBy(x => x, StringComparer.Ordinal), matrixIds.OrderBy(x => x, StringComparer.Ordinal));
        Assert.Equal(matrixIds.Length, matrixIds.Distinct(StringComparer.Ordinal).Count());

        var unknown = LadderConsumerManifestCatalog.BuildForTests(
            [new LadderConsumerRegistration("future.unregistered", "Not a current consumer")]);
        Assert.False(unknown.IsReady);
        Assert.Contains(unknown.UnknownRegistrations, x => x.Id == "future.unregistered");
    }

    [Fact]
    public void Ladder_history_rejects_a_snapshot_hash_that_does_not_match_its_evidence()
    {
        var snapshot = "steps[1:System:7]|edges[]";
        var error = Assert.Throws<DomainException>(() => new ProjectLadderConfigurationHistory(
            Guid.NewGuid(), Guid.NewGuid(), 1, "manager", DateTimeOffset.UtcNow, "reason", snapshot, "not-the-hash"));
        Assert.Contains("does not match", error.Message);
    }

    private static string FindMatrix()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var direct = Path.Combine(directory.FullName, "docs", "REQUIREMENT_HIERARCHY_POLICY_MATRIX.md");
            var nested = Path.Combine(directory.FullName, "product", "docs", "REQUIREMENT_HIERARCHY_POLICY_MATRIX.md");
            if (File.Exists(direct)) return File.ReadAllText(direct);
            if (File.Exists(nested)) return File.ReadAllText(nested);
        }

        throw new FileNotFoundException("The policy matrix is required for the activation-manifest source contract.");
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
