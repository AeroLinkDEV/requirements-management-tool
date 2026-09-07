using AeroLink.Domain.Identity;
using AeroLink.Domain.Programs;
using AeroLink.Domain.Releases;
using AeroLink.Domain.ChangeControl;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using System.Net;

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
/// PostgreSQL qualification of the #849 Finding 4 composite binding on the real provider, through the real
/// migration set: a clean install; an upgrade from the immediately preceding migration carrying valid
/// existing history, which the upgrade must preserve with unchanged row identities and controlled field
/// values; and an upgrade whose inherited history is incompatible with the new binding, which must fail
/// closed on the foreign-key violation itself and leave that history — including its incompatible target
/// identity — in place unchanged. Migration tooling must never rewrite or drop controlled rows to satisfy a
/// new constraint.
///
/// Each test skips (never silently passes) unless AEROLINK_MIGRATIONS_CONNECTION points at a disposable
/// PostgreSQL server; a skipped run is not provider evidence. Each run creates and drops its own
/// aerolink_849_target_* database; the connection must be loopback and must never name the persistent
/// developer port 54329.
/// </summary>
public sealed class ChangeRequestTargetReleasePostgresQualificationTests
{
    private const string ConnectionVariable = "AEROLINK_MIGRATIONS_CONNECTION";
    private const string PredecessorMigration = "20260831033526_AddControlledAttachmentStorageOperations";
    private const string ThisMigration = "20260905222930_AddChangeRequestTargetReleaseProjectBinding";
    private const string ProtectedPort = "54329";

    [Issue849PostgresFact]
    public async Task A_clean_install_enforces_the_composite_binding()
    {
        var server = ResolveServerConnection();
        var database = $"aerolink_849_target_{Guid.NewGuid():N}";
        try
        {
            var connection = await CreateDisposableDatabaseAsync(server, database);
            var options = Options(connection);
            await using var db = new AeroLinkDbContext(options);
            await db.Database.MigrateAsync();

            var seed = await SeedProjectsAndReleasesAsync(db);

            db.SystemChangeRequests.Add(Scr("SRCR-00001", seed.ProjectA, seed.ForeignRelease));
            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
            db.ChangeTracker.Clear();

            db.SystemChangeRequests.Add(Scr("SRCR-00002", seed.ProjectA, Guid.NewGuid()));
            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
            db.ChangeTracker.Clear();

            db.SystemChangeRequests.Add(Scr("SRCR-00003", seed.ProjectA, seed.HonestRelease));
            await db.SaveChangesAsync();
            Assert.Equal(1, await db.SystemChangeRequests.CountAsync());
        }
        finally
        {
            await DropDatabaseAsync(server, database);
        }
    }

    [Issue849PostgresFact]
    public async Task An_upgrade_from_the_predecessor_preserves_valid_history_and_then_enforces_the_binding()
    {
        var server = ResolveServerConnection();
        var database = $"aerolink_849_target_{Guid.NewGuid():N}";
        try
        {
            var connection = await CreateDisposableDatabaseAsync(server, database);
            var options = Options(connection);

            // At the predecessor schema the pair is unbound, so valid history seeds cleanly. The snapshot is
            // what "preserved" is measured against after the upgrade.
            HistoryRow[] snapshot;
            await using (var db = new AeroLinkDbContext(options))
            {
                await db.Database.GetService<IMigrator>().MigrateAsync(PredecessorMigration);
                var seed = await SeedValidHistoryAsync(db);
                db.SystemChangeRequests.Add(Scr("SRCR-00002", seed.ProjectA, seed.NextRelease));
                await db.SaveChangesAsync();
                snapshot = await SnapshotHistoryAsync(db);
                Assert.Equal(2, snapshot.Length);
            }

            // The upgrade applies the new binding over the populated database.
            await using (var db = new AeroLinkDbContext(options))
            {
                await db.Database.MigrateAsync();
            }

            // Every historical row survived with unchanged identity and controlled values, and the binding
            // is live: a foreign pair is refused while a new honest row persists beside the carried history.
            await using (var db = new AeroLinkDbContext(options))
            {
                Assert.Equal(snapshot, await SnapshotHistoryAsync(db));

                var projectA = await db.Projects.SingleAsync(x => x.Name == "Project A");
                var foreignRelease = await db.Releases.SingleAsync(x => x.Version == "9.9");
                db.SystemChangeRequests.Add(Scr("SRCR-00003", projectA.Id, foreignRelease.Id));
                await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
                db.ChangeTracker.Clear();

                var nextRelease = await db.Releases.SingleAsync(x => x.Version == "2.0");
                db.SystemChangeRequests.Add(Scr("SRCR-00003", projectA.Id, nextRelease.Id));
                await db.SaveChangesAsync();
                Assert.Equal(3, await db.SystemChangeRequests.CountAsync());
            }
        }
        finally
        {
            await DropDatabaseAsync(server, database);
        }
    }

    [Issue849PostgresFact]
    public async Task An_upgrade_over_incompatible_history_fails_closed_without_rewriting_it()
    {
        var server = ResolveServerConnection();
        var database = $"aerolink_849_target_{Guid.NewGuid():N}";
        try
        {
            var connection = await CreateDisposableDatabaseAsync(server, database);
            var options = Options(connection);

            // At the predecessor schema a change request pointing at a release that never existed is
            // recordable. Exactly this row is what the new binding must refuse to carry forward.
            HistoryRow[] historyBefore;
            HistoryRow incompatibleRow;
            await using (var db = new AeroLinkDbContext(options))
            {
                await db.Database.GetService<IMigrator>().MigrateAsync(PredecessorMigration);
                var seed = await SeedValidHistoryAsync(db);
                db.SystemChangeRequests.Add(Scr("SRCR-00009", seed.ProjectA, Guid.NewGuid()));
                await db.SaveChangesAsync();
                historyBefore = await SnapshotHistoryAsync(db);
                incompatibleRow = historyBefore.Single(x => x.Title == "Incompatible history");
            }

            // The upgrade fails closed on the foreign-key violation itself.
            await using (var db = new AeroLinkDbContext(options))
            {
                var failure = await Assert.ThrowsAsync<PostgresException>(() => db.Database.MigrateAsync());
                Assert.Equal("23503", failure.SqlState);
            }

            // The history is untouched by the failed upgrade — same row identities, same controlled values,
            // incompatible target identity included — and the schema sits between migrations: an operator
            // must decide, never the upgrade path.
            await using (var db = new AeroLinkDbContext(options))
            {
                Assert.Equal(historyBefore, await SnapshotHistoryAsync(db));
                Assert.Contains(incompatibleRow, await SnapshotHistoryAsync(db));
                var applied = await db.Database.GetAppliedMigrationsAsync();
                Assert.Contains(PredecessorMigration, applied);
                Assert.DoesNotContain(ThisMigration, applied);
            }
        }
        finally
        {
            await DropDatabaseAsync(server, database);
        }
    }

    /// <summary>The controlled identity and values of one change-request row, for before/after comparison.</summary>
    private sealed record HistoryRow(Guid Id, Guid ProjectId, Guid TargetReleaseId, string BaseNumber, int Revision,
        string Title, string AuthorId, DateTimeOffset CreatedAt);

    private static async Task<HistoryRow[]> SnapshotHistoryAsync(AeroLinkDbContext db) =>
        await db.SystemChangeRequests.AsNoTracking()
            .OrderBy(x => x.Title)
            .Select(x => new HistoryRow(x.Id, x.ProjectId, x.TargetReleaseId, x.BaseNumber, x.Revision,
                x.Title, x.AuthorId, x.CreatedAt))
            .ToArrayAsync();

    private static async Task<(HistoryRow[] Valid, HistoryRow Incompatible)> SnapshotHistoryIncludingIncompatibleAsync(
        AeroLinkDbContext db) =>
        (await db.SystemChangeRequests.AsNoTracking().Where(x => x.Title != "Incompatible history")
                .OrderBy(x => x.Title)
                .Select(x => new HistoryRow(x.Id, x.ProjectId, x.TargetReleaseId, x.BaseNumber, x.Revision,
                    x.Title, x.AuthorId, x.CreatedAt))
                .ToArrayAsync(),
            await db.SystemChangeRequests.AsNoTracking().Where(x => x.Title == "Incompatible history")
                .Select(x => new HistoryRow(x.Id, x.ProjectId, x.TargetReleaseId, x.BaseNumber, x.Revision,
                    x.Title, x.AuthorId, x.CreatedAt))
                .SingleAsync());

    /// <summary>
    /// Skips — visibly, as a skipped result rather than a silent pass — when no disposable PostgreSQL server
    /// is configured. A skipped run is not provider evidence.
    /// </summary>
    private sealed class Issue849PostgresFactAttribute : FactAttribute
    {
        public Issue849PostgresFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(ConnectionVariable)))
                Skip = "#849 target-release qualification NOT EXECUTED: AEROLINK_MIGRATIONS_CONNECTION names no disposable PostgreSQL server.";
        }
    }

    private static string ResolveServerConnection()
    {
        var raw = Environment.GetEnvironmentVariable(ConnectionVariable);
        if (string.IsNullOrWhiteSpace(raw))
            throw new InvalidOperationException(
                "The skip gate should have prevented an unqualified run, but the connection variable is empty.");
        var builder = new NpgsqlConnectionStringBuilder(raw);
        var host = (builder.Host ?? string.Empty).Trim().Trim('[', ']');
        var loopback = string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
            || (IPAddress.TryParse(host, out var address) && IPAddress.IsLoopback(address));
        if (!loopback)
            throw new InvalidOperationException("#849 qualification requires a loopback disposable PostgreSQL host.");
        if (builder.Port.ToString() == ProtectedPort)
            throw new InvalidOperationException("#849 qualification refuses the protected developer port 54329.");
        return raw;
    }

    private sealed record SeedSeed(Guid ProgramId, Guid ProjectA, Guid ProjectB, Guid HonestRelease, Guid NextRelease, Guid ForeignRelease);

    private static async Task<SeedSeed> SeedValidHistoryAsync(AeroLinkDbContext db)
    {
        var now = DateTimeOffset.UtcNow;
        var program = new ProgramRecord("Binding Program", "BN3");
        var projectA = new ProjectRecord(program.Id, "Project A", "A");
        var projectB = new ProjectRecord(program.Id, "Project B", "B");
        var honest = new SoftwareRelease(projectA.Id, "1.0", false);
        var next = new SoftwareRelease(projectA.Id, "2.0", false);
        var foreign = new SoftwareRelease(projectB.Id, "9.9", false);
        var carried = new SystemChangeRequest("SRCR-00001", 0, projectA.Id, honest.Id,
            "Carried history", "P", "A", "S", "author", now);
        db.AddRange(program, projectA, projectB, honest, next, foreign, carried);
        await db.SaveChangesAsync();
        return new SeedSeed(program.Id, projectA.Id, projectB.Id, honest.Id, next.Id, foreign.Id);
    }

    private static async Task<SeedSeed> SeedProjectsAndReleasesAsync(AeroLinkDbContext db)
    {
        var now = DateTimeOffset.UtcNow;
        var program = new ProgramRecord("Binding Program", "BN4");
        var projectA = new ProjectRecord(program.Id, "Project A", "A");
        var projectB = new ProjectRecord(program.Id, "Project B", "B");
        var honest = new SoftwareRelease(projectA.Id, "1.0", false);
        var foreign = new SoftwareRelease(projectB.Id, "9.9", false);
        db.AddRange(program, projectA, projectB, honest, foreign);
        await db.SaveChangesAsync();
        return new SeedSeed(program.Id, projectA.Id, projectB.Id, honest.Id, Guid.Empty, foreign.Id);
    }

    private static SystemChangeRequest Scr(string number, Guid projectId, Guid targetReleaseId) =>
        new(number, 0, projectId, targetReleaseId, TitleFor(number), "P", "A", "S", "author", DateTimeOffset.UtcNow);

    private static string TitleFor(string number) =>
        number switch
        {
            "SRCR-00001" => "Carried history",
            "SRCR-00009" => "Incompatible history",
            _ => $"Qualified {number}",
        };

    private static DbContextOptions<AeroLinkDbContext> Options(string connectionString) =>
        new DbContextOptionsBuilder<AeroLinkDbContext>().UseNpgsql(connectionString).Options;

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
