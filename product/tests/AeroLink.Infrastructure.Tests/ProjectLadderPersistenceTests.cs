using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Hierarchy;
using AeroLink.Domain.Programs;
using AeroLink.Domain.Traceability;
using AeroLink.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace AeroLink.Infrastructure.Tests;

public sealed class ProjectLadderPersistenceTests : IAsyncLifetime
{
    private string _path = string.Empty;
    private DbContextOptions<AeroLinkDbContext> _options = null!;
    private Guid _fmsProjectId;
    private Guid _otherProjectId;

    public async Task InitializeAsync()
    {
        _path = Path.Combine(Path.GetTempPath(), $"aerolink-project-ladder-{Guid.NewGuid():N}.db");
        _options = new DbContextOptionsBuilder<AeroLinkDbContext>()
            .UseSqlite($"Data Source={_path};Pooling=False;Foreign Keys=True")
            .Options;

        await using var db = Context();
        await db.Database.EnsureCreatedAsync();
        var fmsProgram = new ProgramRecord("FMS program", "FMS");
        var otherProgram = new ProgramRecord("Other program", "OTH");
        var fms = new ProjectRecord(fmsProgram.Id, "FMS", "Flight Management System");
        var other = new ProjectRecord(otherProgram.Id, "Other", "Other software");
        db.AddRange(fmsProgram, otherProgram, fms, other);
        await db.SaveChangesAsync();
        _fmsProjectId = fms.Id;
        _otherProjectId = other.Id;

        await SeedLegacyAsync(fms.Id);
        await SeedLegacyAsync(other.Id);
    }

    public Task DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        try { if (File.Exists(_path)) File.Delete(_path); } catch (IOException) { }
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Each_seeded_project_shape_resolves_to_the_exact_legacy_default_including_FMS()
    {
        await using var db = Context();
        var configurations = await db.ProjectLadderConfigurations
            .Include(x => x.Steps)
            .Include(x => x.AllowedUpstream)
            .AsNoTracking()
            .OrderBy(x => x.ProjectId)
            .ToListAsync();

        Assert.Equal(2, configurations.Count);
        Assert.All(configurations, configuration =>
        {
            var resolved = ProjectLadderResolver.Resolve(configuration);
            Assert.True(resolved.AgreesWithLegacyDefault());
            Assert.Equal(ProjectLadderConfigurationClassification.LegacyDefault, configuration.Classification);
            Assert.Equal(ProjectLadderConfigurationState.Stored, configuration.State);
            Assert.Equal([RequirementLevel.System, RequirementLevel.HighLevel, RequirementLevel.LowLevel],
                resolved.Steps.Select(x => x.Level));
            Assert.Equal([7, 7, 15], resolved.Steps.Select(x => (int)x.Capabilities));
        });

        var fms = configurations.Single(x => x.ProjectId == _fmsProjectId);
        Assert.Equal("FMS", (await db.Projects.SingleAsync(x => x.Id == fms.ProjectId)).Name);
        Assert.Equal(2, fms.AllowedUpstream.Count);
    }

    [Fact]
    public async Task Effective_project_resolver_reads_the_stored_shape_and_preserves_legacy_trace_compatibility()
    {
        await using var db = Context();
        var resolver = new EffectiveProjectLadderPolicyResolver(db);

        var policy = await resolver.ResolveAsync(_fmsProjectId);

        Assert.IsAssignableFrom<ILegacyLadderCompatibilityPolicy>(policy);
        Assert.Equal([RequirementLevel.HighLevel], policy.DownstreamLevels(RequirementLevel.System));
        RequirementTracePolicy.Validate(policy, RequirementLevel.System, RequirementLevel.System,
            RequirementTraceType.AllocatedFrom);
    }

    [Fact]
    public async Task Draft_uses_prior_legacy_runtime_and_active_uses_the_persisted_subset()
    {
        var projectId = Guid.Empty;
        await using (var db = Context())
        {
            var program = new ProgramRecord("Draft authority program", $"DRA{Guid.NewGuid():N}"[..12]);
            var project = new ProjectRecord(program.Id, "Draft authority", "Draft authority software");
            projectId = project.Id;
            db.AddRange(program, project);
            await db.SaveChangesAsync();

            var now = DateTimeOffset.UtcNow;
            var configuration = ProjectLadderConfiguration.CreateDraft(project.Id, now);
            var system = new ProjectLadderStep(configuration.Id, project.Id, RequirementLevel.System, 1,
                LegacyLadderPolicy.Instance.Definition(RequirementLevel.System).Capabilities, now);
            var low = new ProjectLadderStep(configuration.Id, project.Id, RequirementLevel.LowLevel, 2,
                LegacyLadderPolicy.Instance.Definition(RequirementLevel.LowLevel).Capabilities, now);
            configuration.Steps.Add(system);
            configuration.Steps.Add(low);
            configuration.AllowedUpstream.Add(new(configuration.Id, project.Id, system.Id, low.Id, now));
            db.ProjectLadderConfigurations.Add(configuration);
            await db.SaveChangesAsync();
        }

        await using (var draft = Context())
        {
            var policy = await new EffectiveProjectLadderPolicyResolver(draft).ResolveAsync(projectId);
            Assert.Same(LegacyLadderPolicy.Instance, policy);
            Assert.Equal([RequirementLevel.System, RequirementLevel.HighLevel, RequirementLevel.LowLevel], policy.OrderedLevels);
        }

        await using (var activate = Context())
        {
            var configuration = await activate.ProjectLadderConfigurations
                .SingleAsync(x => x.ProjectId == projectId);
            await activate.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE project_ladder_configurations
                SET Classification = 'NonDefault', State = 'Active', Version = 2,
                    ActivatedAt = CURRENT_TIMESTAMP, ActivatedBy = 'test.manager',
                    ActivationManifestVersion = 'test-manifest-v1',
                    ActivationManifestHash = {'a'.ToString().PadLeft(64, 'a')}
                WHERE Id = {configuration.Id}
                """);
        }

        await using (var active = Context())
        {
            var policy = await new EffectiveProjectLadderPolicyResolver(active).ResolveAsync(projectId);
            Assert.IsType<ResolvedProjectLadderPolicy>(policy);
            Assert.Equal([RequirementLevel.System, RequirementLevel.LowLevel], policy.OrderedLevels);
            Assert.Equal([RequirementLevel.LowLevel], policy.DownstreamLevels(RequirementLevel.System));
            RequirementTracePolicy.Validate(policy, RequirementLevel.LowLevel, RequirementLevel.System,
                RequirementTraceType.DerivedFrom);
            Assert.Throws<Domain.Common.DomainException>(() => RequirementTracePolicy.Validate(policy,
                RequirementLevel.System, RequirementLevel.LowLevel, RequirementTraceType.DerivedFrom));
        }
    }

    [Fact]
    public async Task Database_rejects_duplicate_step_position_or_catalogue_entry()
    {
        await using var db = Context();
        var configuration = await db.ProjectLadderConfigurations
            .Include(x => x.Steps)
            .SingleAsync(x => x.ProjectId == _fmsProjectId);
        var duplicate = new ProjectLadderStep(configuration.Id, configuration.ProjectId, RequirementLevel.System, 1,
            LegacyLadderPolicy.Instance.Definition(RequirementLevel.System).Capabilities, DateTimeOffset.UtcNow);
        db.ProjectLadderSteps.Add(duplicate);

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task Composite_foreign_keys_reject_an_upstream_edge_that_crosses_projects()
    {
        await using var db = Context();
        var first = await db.ProjectLadderConfigurations.Include(x => x.Steps)
            .SingleAsync(x => x.ProjectId == _fmsProjectId);
        var second = await db.ProjectLadderConfigurations.Include(x => x.Steps)
            .SingleAsync(x => x.ProjectId == _otherProjectId);
        var firstSystem = first.Steps.Single(x => x.CatalogueEntry == nameof(RequirementLevel.System));
        var foreignHigh = second.Steps.Single(x => x.CatalogueEntry == nameof(RequirementLevel.HighLevel));
        db.ProjectLadderAllowedUpstreams.Add(new ProjectLadderAllowedUpstream(
            first.Id, first.ProjectId, firstSystem.Id, foreignHigh.Id, DateTimeOffset.UtcNow));

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task Configuration_state_and_activation_evidence_are_checked_as_a_pair_by_the_database()
    {
        await using var db = Context();
        var configuration = await db.ProjectLadderConfigurations
            .SingleAsync(x => x.ProjectId == _fmsProjectId);

        FormattableString sql = $"UPDATE project_ladder_configurations SET Classification = 'NonDefault', State = 'Active', ActivatedAt = CURRENT_TIMESTAMP, ActivatedBy = NULL WHERE Id = {configuration.Id}";
        Assert.Throws<SqliteException>(() => db.Database.ExecuteSqlInterpolated(sql));
    }

    [Fact]
    public async Task Database_rejects_manifest_evidence_on_stored_and_incomplete_active_or_retired_rows()
    {
        await using var read = Context();
        var configuration = await read.ProjectLadderConfigurations.AsNoTracking()
            .SingleAsync(x => x.ProjectId == _fmsProjectId);

        await AssertInvalidUpdateAsync($"UPDATE project_ladder_configurations SET ActivationManifestVersion = 'v1' WHERE Id = {configuration.Id}");
        await AssertInvalidUpdateAsync($"UPDATE project_ladder_configurations SET Classification = 'NonDefault', State = 'Active', ActivatedAt = CURRENT_TIMESTAMP, ActivatedBy = 'manager', ActivationManifestVersion = 'v1', ActivationManifestHash = NULL WHERE Id = {configuration.Id}");
        await AssertInvalidUpdateAsync($"UPDATE project_ladder_configurations SET Classification = 'NonDefault', State = 'Retired', ActivatedAt = CURRENT_TIMESTAMP, ActivatedBy = 'manager', RetiredAt = CURRENT_TIMESTAMP, RetiredBy = 'manager', ActivationManifestVersion = NULL, ActivationManifestHash = NULL WHERE Id = {configuration.Id}");
    }

    [Fact]
    public async Task Resolver_fails_closed_when_persisted_catalogue_data_is_unknown()
    {
        await using (var db = Context())
        {
            var configuration = await db.ProjectLadderConfigurations
                .SingleAsync(x => x.ProjectId == _fmsProjectId);
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE project_ladder_steps SET CatalogueEntry = 'Unknown' WHERE ConfigurationId = {configuration.Id} AND Position = 1");
        }

        await using var check = Context();
        var malformed = await check.ProjectLadderConfigurations
            .Include(x => x.Steps).Include(x => x.AllowedUpstream)
            .SingleAsync(x => x.ProjectId == _fmsProjectId);
        var error = Assert.Throws<AeroLink.Domain.Common.DomainException>(() => ProjectLadderResolver.Resolve(malformed));
        Assert.Contains("Unknown persisted", error.Message);
    }

    [Fact]
    public async Task Version_is_optimistic_concurrency_protection_for_a_persisted_ladder()
    {
        await using var first = Context();
        await using var second = Context();
        var firstConfiguration = await first.ProjectLadderConfigurations.SingleAsync(x => x.ProjectId == _fmsProjectId);
        var secondConfiguration = await second.ProjectLadderConfigurations.SingleAsync(x => x.ProjectId == _fmsProjectId);
        var updatedAt = DateTimeOffset.UtcNow;
        first.Entry(firstConfiguration).Property(nameof(ProjectLadderConfiguration.UpdatedAt)).CurrentValue = updatedAt;
        first.Entry(firstConfiguration).Property(nameof(ProjectLadderConfiguration.Version)).CurrentValue = 2L;
        second.Entry(secondConfiguration).Property(nameof(ProjectLadderConfiguration.UpdatedAt)).CurrentValue = updatedAt.AddSeconds(1);
        second.Entry(secondConfiguration).Property(nameof(ProjectLadderConfiguration.Version)).CurrentValue = 2L;

        await first.SaveChangesAsync();
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => second.SaveChangesAsync());
    }

    private AeroLinkDbContext Context() => new(_options);

    private async Task SeedLegacyAsync(Guid projectId)
    {
        await using var db = Context();
        var now = DateTimeOffset.UtcNow;
        var configuration = LegacyDefaultProjectLadderFactory.Create(projectId, now);
        db.ProjectLadderConfigurations.Add(configuration);
        await db.SaveChangesAsync();
    }

    private async Task AssertInvalidUpdateAsync(FormattableString sql)
    {
        await using var db = Context();
        Assert.Throws<SqliteException>(() => db.Database.ExecuteSqlInterpolated(sql));
    }
}
