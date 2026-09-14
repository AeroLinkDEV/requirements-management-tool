using AeroLink.Domain.Common;
using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Hierarchy;
using AeroLink.Domain.Programs;
using AeroLink.Domain.Requirements;
using AeroLink.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace AeroLink.Infrastructure.Tests;

public sealed class ProjectLadderSealAuthorityEffectiveStateTests
{
    [Fact]
    public async Task First_content_against_a_draft_is_refused_before_history_or_content_is_saved()
    {
        await using var db = CreateContext();
        await db.Database.EnsureCreatedAsync();
        var (project, ladder) = await AddProjectAsync(db, NewProjectLadderFactory.Create);
        var content = new RequirementArtifact(project.Id, "SYSR-103700", RequirementLevel.System,
            DateTimeOffset.UtcNow);
        db.Requirements.Add(content);

        await Assert.ThrowsAsync<DomainException>(() => db.SaveChangesAsync());

        db.ChangeTracker.Clear();
        Assert.False(await db.Requirements.AnyAsync(x => x.Id == content.Id));
        var persisted = await db.ProjectLadderConfigurations.AsNoTracking()
            .SingleAsync(x => x.Id == ladder.Id);
        Assert.False(persisted.IsSealed);
        Assert.Equal(ProjectLadderConfigurationState.Draft, persisted.State);
        Assert.Empty(await db.ProjectLadderConfigurationHistories.AsNoTracking()
            .Where(x => x.ConfigurationId == ladder.Id).ToListAsync());
    }

    [Fact]
    public async Task Active_and_legacy_stored_ladders_can_be_sealed()
    {
        await using var db = CreateContext();
        await db.Database.EnsureCreatedAsync();

        var (activeProject, activeLadder) = await AddProjectAsync(db, NewProjectLadderFactory.Create);
        activeLadder.Activate("project.owner", DateTimeOffset.UtcNow,
            LadderConsumerManifestCatalog.VersionV2, new string('0', 64));
        await db.SaveChangesAsync();
        var activeResult = await new ProjectLadderSealAuthority(db).SealAsync(activeProject.Id,
            LadderBoundContentCatalog.Current[0].Id, "active-content", "project.owner", DateTimeOffset.UtcNow);

        var (legacyProject, legacyLadder) = await AddProjectAsync(db, LegacyDefaultProjectLadderFactory.Create);
        var legacyResult = await new ProjectLadderSealAuthority(db).SealAsync(legacyProject.Id,
            LadderBoundContentCatalog.Current[0].Id, "legacy-content", "migration", DateTimeOffset.UtcNow);
        await db.SaveChangesAsync();

        Assert.Equal(ProjectLadderSealResultKind.Sealed, activeResult.Kind);
        Assert.Equal(ProjectLadderSealResultKind.Sealed, legacyResult.Kind);
        Assert.True(await db.ProjectLadderConfigurations.AsNoTracking()
            .Where(x => x.Id == activeLadder.Id || x.Id == legacyLadder.Id).AllAsync(x => x.IsSealed));
    }

    [Fact]
    public async Task Unsealed_retired_ladder_is_refused_without_mutating_the_historical_row()
    {
        await using var db = CreateContext();
        await db.Database.EnsureCreatedAsync();
        var program = new ProgramRecord("Retired ladder program", $"RET{Guid.NewGuid():N}"[..10]);
        var project = new ProjectRecord(program.Id, "Retired ladder", "Retired ladder product");
        db.AddRange(program, project);
        await db.SaveChangesAsync();

        var now = DateTimeOffset.UtcNow;
        var retired = (ProjectLadderConfiguration)System.Runtime.CompilerServices.RuntimeHelpers
            .GetUninitializedObject(typeof(ProjectLadderConfiguration));
        SetPrivate(retired, nameof(ProjectLadderConfiguration.Id), Guid.NewGuid());
        SetPrivate(retired, nameof(ProjectLadderConfiguration.ProjectId), project.Id);
        SetPrivate(retired, nameof(ProjectLadderConfiguration.Classification),
            ProjectLadderConfigurationClassification.NonDefault);
        SetPrivate(retired, nameof(ProjectLadderConfiguration.State), ProjectLadderConfigurationState.Retired);
        SetPrivate(retired, nameof(ProjectLadderConfiguration.CreatedAt), now);
        SetPrivate(retired, nameof(ProjectLadderConfiguration.UpdatedAt), now);
        SetPrivate(retired, nameof(ProjectLadderConfiguration.Version), 3L);
        db.ProjectLadderConfigurations.Add(retired);

        await Assert.ThrowsAsync<DomainException>(() => new ProjectLadderSealAuthority(db).SealAsync(
            project.Id, LadderBoundContentCatalog.Current[0].Id, "retired-content", "project.owner", now));

        Assert.False(retired.IsSealed);
        Assert.Empty(db.ProjectLadderConfigurationHistories.Local);
    }

    private static AeroLinkDbContext CreateContext()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        return new AeroLinkDbContext(new DbContextOptionsBuilder<AeroLinkDbContext>()
            .UseSqlite(connection).Options);
    }

    private static async Task<(ProjectRecord Project, ProjectLadderConfiguration Ladder)> AddProjectAsync(
        AeroLinkDbContext db, Func<Guid, DateTimeOffset, ProjectLadderConfiguration> ladderFactory)
    {
        var program = new ProgramRecord("Effective ladder program", $"ELP{Guid.NewGuid():N}"[..10]);
        var project = new ProjectRecord(program.Id, "Effective ladder project", "Effective ladder product");
        var ladder = ladderFactory(project.Id, DateTimeOffset.UtcNow);
        db.AddRange(program, project, ladder);
        await db.SaveChangesAsync();
        return (project, ladder);
    }

    private static void SetPrivate(ProjectLadderConfiguration configuration, string propertyName, object? value) =>
        typeof(ProjectLadderConfiguration).GetProperty(propertyName)!.GetSetMethod(nonPublic: true)!
            .Invoke(configuration, [value]);
}
