using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Hierarchy;
using AeroLink.Domain.Programs;
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
}
