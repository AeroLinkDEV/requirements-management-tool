using AeroLink.Domain.Identity;
using AeroLink.Domain.Programs;
using AeroLink.Domain.Releases;
using AeroLink.Domain.ChangeControl;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace AeroLink.Infrastructure.Tests;

/// <summary>
/// The change request's project and its target release are stored as independent identities, so #849
/// Finding 4 binds them referentially as well as through the shared server-side guard: a change request can
/// never persist a (ProjectId, TargetReleaseId) pair whose release belongs to another project or does not
/// exist, even through a future persistence path that never saw the guard.
/// </summary>
public sealed class ChangeRequestTargetReleaseBindingTests
{
    [Fact]
    public async Task The_composite_project_binding_is_enforced_at_the_persistence_layer()
    {
        var database = Path.Combine(Path.GetTempPath(), $"aerolink-849-binding-{Guid.NewGuid():N}.db");
        try
        {
            var options = new DbContextOptionsBuilder<AeroLinkDbContext>().UseSqlite($"Data Source={database};Pooling=False").Options;
            await using var db = new AeroLinkDbContext(options);
            await db.Database.EnsureCreatedAsync();
            var now = DateTimeOffset.UtcNow;
            var program = new ProgramRecord("Binding Program", "BN2");
            var projectA = new ProjectRecord(program.Id, "Project A", "A");
            var projectB = new ProjectRecord(program.Id, "Project B", "B");
            var releaseA = new SoftwareRelease(projectA.Id, "1.0", false);
            var releaseB = new SoftwareRelease(projectB.Id, "9.9", false);
            db.AddRange(program, projectA, projectB, releaseA, releaseB);
            await db.SaveChangesAsync();

            db.SystemChangeRequests.Add(new SystemChangeRequest("SRCR-00001", 0, projectA.Id, releaseB.Id,
                "Foreign target", "P", "A", "S", "author", now));
            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
            db.ChangeTracker.Clear();

            db.SystemChangeRequests.Add(new SystemChangeRequest("SRCR-00001", 0, projectA.Id, releaseA.Id,
                "Honest target", "P", "A", "S", "author", now));
            await db.SaveChangesAsync();
            Assert.Single(await db.SystemChangeRequests.ToListAsync());
        }
        finally
        {
            if (File.Exists(database)) File.Delete(database);
        }
    }
}

/// <summary>
/// PostgreSQL qualification of the same composite binding on the real provider, through the real migration
/// set (#849 Finding 4). Skipped unless AEROLINK_MIGRATIONS_CONNECTION points at a disposable PostgreSQL
/// server; the disposable database is created and dropped per test and 54329 is never touched.
/// </summary>
public sealed class ChangeRequestTargetReleasePostgresQualificationTests
{
    private const string ConnectionVariable = "AEROLINK_MIGRATIONS_CONNECTION";
    private const string RequiredVariable = "AEROLINK_REQUIRE_POSTGRES_QUALIFICATION";

    [Fact]
    public async Task The_composite_project_binding_holds_on_postgresql()
    {
        var serverConnectionString = Environment.GetEnvironmentVariable(ConnectionVariable);
        if (string.IsNullOrWhiteSpace(serverConnectionString))
        {
            var required = Environment.GetEnvironmentVariable(RequiredVariable);
            if (!string.IsNullOrWhiteSpace(required) && !required.Equals("false", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    $"{RequiredVariable} is set, so this qualification must actually run, but {ConnectionVariable} names no disposable PostgreSQL server.");
            return; // Developer machine without PostgreSQL: nothing this test can prove that SQLite has not.
        }

        var database = $"aerolink_849_target_{Guid.NewGuid():N}";
        try
        {
            var connection = await CreateDisposableDatabaseAsync(serverConnectionString, database);
            var options = new DbContextOptionsBuilder<AeroLinkDbContext>().UseNpgsql(connection).Options;
            await using var db = new AeroLinkDbContext(options);
            await db.Database.MigrateAsync();
            var now = DateTimeOffset.UtcNow;

            var program = new ProgramRecord("Binding Program", "BN3");
            var projectA = new ProjectRecord(program.Id, "Project A", "A");
            var projectB = new ProjectRecord(program.Id, "Project B", "B");
            var releaseA = new SoftwareRelease(projectA.Id, "1.0", false);
            var releaseB = new SoftwareRelease(projectB.Id, "9.9", false);
            db.AddRange(program, projectA, projectB, releaseA, releaseB);
            await db.SaveChangesAsync();

            db.SystemChangeRequests.Add(new SystemChangeRequest("SRCR-00001", 0, projectA.Id, releaseB.Id,
                "Foreign target", "P", "A", "S", "author", now));
            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
            db.ChangeTracker.Clear();

            db.SystemChangeRequests.Add(new SystemChangeRequest("SRCR-00001", 0, projectA.Id, releaseA.Id,
                "Honest target", "P", "A", "S", "author", now));
            await db.SaveChangesAsync();
            Assert.Single(await db.SystemChangeRequests.ToListAsync());
        }
        finally
        {
            await DropDatabaseAsync(serverConnectionString, database);
        }
    }

    private static async Task<string> CreateDisposableDatabaseAsync(string serverConnectionString, string database)
    {
        await using var admin = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(serverConnectionString)
        { Database = "postgres" }.ConnectionString);
        await admin.OpenAsync();
        await using var command = admin.CreateCommand();
        command.CommandText = $"CREATE DATABASE \"{database}\"";
        await command.ExecuteNonQueryAsync();
        return new NpgsqlConnectionStringBuilder(serverConnectionString) { Database = database }.ConnectionString;
    }

    private static async Task DropDatabaseAsync(string serverConnectionString, string database)
    {
        if (string.IsNullOrWhiteSpace(database)) return;
        await using var admin = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(serverConnectionString)
        { Database = "postgres" }.ConnectionString);
        await admin.OpenAsync();
        await using var command = admin.CreateCommand();
        command.CommandText = $"DROP DATABASE IF EXISTS \"{database}\" WITH (FORCE)";
        await command.ExecuteNonQueryAsync();
    }
}
