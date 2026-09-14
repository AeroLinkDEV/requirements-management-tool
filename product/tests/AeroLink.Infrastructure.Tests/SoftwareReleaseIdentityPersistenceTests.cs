using AeroLink.Domain.Common;
using AeroLink.Domain.Programs;
using AeroLink.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace AeroLink.Infrastructure.Tests;

public sealed class SoftwareReleaseIdentityPersistenceTests
{
    [Fact]
    public async Task Save_boundary_rejects_canonical_collision_with_historical_null_identity()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AeroLinkDbContext>().UseSqlite(connection).Options;
        await using var db = new AeroLinkDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var program = new ProgramRecord("Historical identity test", "HIT");
        var project = new ProjectRecord(program.Id, "Historical project", "Historical software");
        var historical = new SoftwareRelease(project.Id, "1.3", false);
        db.AddRange(program, project, historical);
        await db.SaveChangesAsync();

        // Model a pre-canonical historical row without touching any persistent developer/demo state.
        db.ChangeTracker.Clear();
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE software_releases SET \"CanonicalIdentity\" = NULL WHERE \"Id\" = {historical.Id}");

        db.Releases.Add(new SoftwareRelease(project.Id, "1.30", false));
        var error = await Assert.ThrowsAsync<DomainException>(() => db.SaveChangesAsync());

        Assert.Contains("canonical identity", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("1.3", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Save_boundary_rejects_invalid_historical_raw_version_before_new_release()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AeroLinkDbContext>().UseSqlite(connection).Options;
        await using var db = new AeroLinkDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var program = new ProgramRecord("Invalid identity test", "IIT");
        var project = new ProjectRecord(program.Id, "Invalid project", "Invalid software");
        var historical = new SoftwareRelease(project.Id, "1.4", false);
        db.AddRange(program, project, historical);
        await db.SaveChangesAsync();

        db.ChangeTracker.Clear();
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE software_releases SET \"Version\" = 'legacy-raw' WHERE \"Id\" = {historical.Id}");
        db.Releases.Add(new SoftwareRelease(project.Id, "1.5", false));

        var error = await Assert.ThrowsAsync<DomainException>(() => db.SaveChangesAsync());
        Assert.Contains("existing raw version", error.Message, StringComparison.OrdinalIgnoreCase);
    }
}
