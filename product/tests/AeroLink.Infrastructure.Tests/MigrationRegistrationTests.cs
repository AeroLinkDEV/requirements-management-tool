using System.Reflection;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace AeroLink.Infrastructure.Tests;

/// <summary>
/// Guards the migration contract itself. Entity Framework discovers migrations by attribute, not by file,
/// so a migration authored without them is silently skipped by <c>Database.Migrate()</c> and its tables are
/// never created in production. These tests fail the build instead.
/// </summary>
public sealed class MigrationRegistrationTests
{
    [Fact]
    public void Every_migration_in_the_assembly_is_discoverable_by_entity_framework()
    {
        var migrations = typeof(AeroLinkDbContext).Assembly.GetTypes()
            .Where(x => x.IsSubclassOf(typeof(Migration)) && !x.IsAbstract)
            .ToList();
        Assert.NotEmpty(migrations);

        var undiscoverable = migrations
            .Where(x => x.GetCustomAttribute<MigrationAttribute>() is null
                        || x.GetCustomAttribute<DbContextAttribute>()?.ContextType != typeof(AeroLinkDbContext))
            .Select(x => x.Name)
            .OrderBy(x => x)
            .ToList();

        Assert.True(undiscoverable.Count == 0,
            "These migrations are missing [Migration] and/or [DbContext(typeof(AeroLinkDbContext))] and will never be "
            + $"applied by Database.Migrate(): {string.Join(", ", undiscoverable)}. Generate migrations with "
            + "'dotnet ef migrations add' rather than authoring the class by hand.");
    }

    [Fact]
    public void Model_has_no_change_that_is_missing_a_migration()
    {
        // The model is built against the production provider because the migration snapshot is written for it.
        // No connection is opened: this compares the context model with the recorded snapshot only.
        var options = new DbContextOptionsBuilder<AeroLinkDbContext>()
            .UseNpgsql("Host=migration-check.invalid;Database=aerolink;Username=none;Password=none")
            .Options;
        using var db = new AeroLinkDbContext(options);

        Assert.False(db.Database.HasPendingModelChanges(),
            "The EF model no longer matches the latest migration snapshot. Run 'dotnet ef migrations add <Name> "
            + "--project src/AeroLink.Infrastructure --startup-project src/AeroLink.Api --output-dir Persistence/Migrations'.");
    }
}
