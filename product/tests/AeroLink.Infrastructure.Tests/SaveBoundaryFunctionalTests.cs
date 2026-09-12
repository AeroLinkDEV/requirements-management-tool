using System.Data.Common;
using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Common;
using AeroLink.Domain.Hierarchy;
using AeroLink.Domain.Programs;
using AeroLink.Domain.Requirements;
using AeroLink.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;

namespace AeroLink.Infrastructure.Tests;

/// <summary>
/// Behavior-level coverage for the save-boundary phase extraction. These tests intentionally exercise a
/// complete aggregate save so the state-repair component is qualified through the same path as production.
/// </summary>
public sealed class SaveBoundaryFunctionalTests
{
    [Fact]
    public async Task Editing_501_existing_requirement_changes_preserves_identity_and_uses_bounded_reads()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var probe = new SaveBoundaryCommandProbe();
        var options = SqliteOptions(connection, probe);
        var requestId = Guid.Empty;
        HashSet<Guid> originalIds;
        var now = DateTimeOffset.UtcNow;

        await using (var setup = new AeroLinkDbContext(options))
        {
            await setup.Database.EnsureCreatedAsync();
            var program = new ProgramRecord("CQ07 bounded read program", "CQ7B");
            var project = new ProjectRecord(program.Id, "CQ07 bounded read project", "Functional qualification");
            var release = new SoftwareRelease(project.Id, "1.0", false);
            setup.AddRange(program, project, release);
            await setup.SaveChangesAsync();
            setup.Add(LegacyDefaultProjectLadderFactory.Create(project.Id, now));
            await setup.SaveChangesAsync();

            var request = new SystemChangeRequest("SRCR-96701", 0, project.Id, release.Id,
                "Bounded requirement edit", "Problem", "Analysis", "Solution", "author", now);
            for (var i = 1; i <= 501; i++)
            {
                request.AddRequirementChange("author", $"SYSR-{i:D8}", 0, RequirementLevel.System,
                    RequirementChangeKind.Modify, $"Original statement {i}.", "Original rationale", "Test", now);
            }

            setup.Add(request);
            await setup.SaveChangesAsync();
            requestId = request.Id;
        }

        await using (var db = new AeroLinkDbContext(options))
        {
            var request = await db.SystemChangeRequests
                .Include(x => x.RequirementChanges)
                .Include(x => x.AuditEvents)
                .SingleAsync(x => x.Id == requestId);
            originalIds = request.RequirementChanges.Select(x => x.Id).ToHashSet();

            foreach (var child in request.RequirementChanges.ToArray())
            {
                request.RebaseRequirementChange("author", child.Id, 1,
                    $"Revised statement {child.BaseNumber}.", "Revised rationale", now.AddMinutes(1));
            }

            probe.Enabled = true;
            await db.SaveChangesAsync();
            probe.Enabled = false;
        }

        Assert.Equal(2, probe.RequirementChangeSelects);

        await using var verification = new AeroLinkDbContext(options);
        var saved = await verification.RequirementChanges.AsNoTracking()
            .Where(x => x.ChangeRequestId == requestId)
            .ToListAsync();
        Assert.Equal(501, saved.Count);
        Assert.True(OriginalIdsArePreserved(originalIds, saved.Select(x => x.Id)));
        // Rebasing onto approved .01 proposes .02; the child identities and bounded reads stay unchanged.
        Assert.All(saved, change => Assert.Equal(2, change.Revision));
    }

    [Fact]
    public async Task Existing_upstream_link_is_immutable_when_directly_marked_modified()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = SqliteOptions(connection);
        var now = DateTimeOffset.UtcNow;
        Guid linkId;

        await using (var setup = new AeroLinkDbContext(options))
        {
            await setup.Database.EnsureCreatedAsync();
            var program = new ProgramRecord("CQ07 immutable program", "CQ7I");
            var project = new ProjectRecord(program.Id, "CQ07 immutable project", "Functional qualification");
            var release = new SoftwareRelease(project.Id, "1.0", false);
            var source = new SystemChangeRequest("SRCR-96702", 0, project.Id, release.Id,
                "Upstream source", "Problem", "Analysis", "Solution", "author", now);
            var child = new SystemChangeRequest("HLRCR-96703", 0, project.Id, release.Id,
                "Upstream child", "Problem", "Analysis", "Solution", "author", now,
                ChangeRequestType.Software, softwareLevel: RequirementLevel.HighLevel);
            child.AddUpstreamLink("author", source.Id, source.DisplayNumber, release.Id, release.Version,
                "The source controls this child.", now);
            linkId = child.UpstreamLinks.Single().Id;
            setup.AddRange(program, project, release, source, child);
            await setup.SaveChangesAsync();
        }

        await using (var db = new AeroLinkDbContext(options))
        {
            var link = await db.ChangeRequestUpstreamLinks.SingleAsync(x => x.Id == linkId);
            db.Entry(link).Property(x => x.Rationale).CurrentValue = "Unauthorized direct edit";
            var error = await Assert.ThrowsAsync<DomainException>(() => db.SaveChangesAsync());
            Assert.Contains("immutable", error.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(EntityState.Modified, db.Entry(link).State);
        }

        await using var verification = new AeroLinkDbContext(options);
        var stored = await verification.ChangeRequestUpstreamLinks.AsNoTracking()
            .SingleAsync(x => x.Id == linkId);
        Assert.Equal("The source controls this child.", stored.Rationale);
    }

    [Fact]
    public async Task Explicit_transaction_rollback_leaves_no_rows_and_allows_a_clean_retry()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = SqliteOptions(connection);

        await using var db = new AeroLinkDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var first = new ProgramRecord("CQ07 rollback program", "CQ7R");
        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            db.Add(first);
            await db.SaveChangesAsync();
            Assert.Equal(1, await db.Programs.CountAsync());
            await transaction.RollbackAsync();
        }

        Assert.Equal(EntityState.Unchanged, db.Entry(first).State);
        Assert.Empty(await db.Programs.AsNoTracking().ToListAsync());

        db.ChangeTracker.Clear();
        var retry = new ProgramRecord("CQ07 rollback retry", "CQ7S");
        db.Add(retry);
        await db.SaveChangesAsync();
        Assert.Equal(1, await db.Programs.CountAsync());
        Assert.Equal(retry.Id, (await db.Programs.AsNoTracking().SingleAsync()).Id);
    }

    private static DbContextOptions<AeroLinkDbContext> SqliteOptions(SqliteConnection connection,
        SaveBoundaryCommandProbe? probe = null)
    {
        var builder = new DbContextOptionsBuilder<AeroLinkDbContext>().UseSqlite(connection);
        if (probe is not null) builder.AddInterceptors(probe);
        return builder.Options;
    }

    private static bool OriginalIdsArePreserved(IEnumerable<Guid> originalIds, IEnumerable<Guid> savedIds) =>
        new HashSet<Guid>(originalIds).SetEquals(savedIds);

    private sealed class SaveBoundaryCommandProbe : DbCommandInterceptor
    {
        public bool Enabled { get; set; }
        public int RequirementChangeSelects { get; private set; }

        private void Record(DbCommand command)
        {
            if (!Enabled || !command.CommandText.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase))
                return;
            if (command.CommandText.Contains("requirement_changes", StringComparison.OrdinalIgnoreCase))
                RequirementChangeSelects++;
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Record(command);
            return ValueTask.FromResult(result);
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            Record(command);
            return ValueTask.FromResult(result);
        }

        public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<object> result,
            CancellationToken cancellationToken = default)
        {
            Record(command);
            return ValueTask.FromResult(result);
        }
    }
}

[CollectionDefinition("CQ07Postgres", DisableParallelization = true)]
public sealed class Cq07PostgresCollection;

/// <summary>Runs the bounded existing-child query against PostgreSQL in a unique disposable database.</summary>
[Collection("CQ07Postgres")]
public sealed class SaveBoundaryPostgresQualificationTests
{
    private const string ConnectionVariable = "AEROLINK_MIGRATIONS_CONNECTION";
    private const int QualificationPort = 55465;

    [Cq07PostgresFact]
    public async Task Existing_501_requirement_changes_are_repaired_in_two_postgres_batches()
    {
        var serverConnection = QualificationServerConnectionOrThrow();
        var database = $"aerolink_967_{Guid.NewGuid():N}";
        var connection = await CreateDisposableDatabaseAsync(serverConnection, database);
        try
        {
            var probe = new SaveBoundaryPostgresCommandProbe();
            var options = new DbContextOptionsBuilder<AeroLinkDbContext>()
                .UseNpgsql(connection).AddInterceptors(probe).Options;
            var now = DateTimeOffset.UtcNow;
            Guid requestId;
            HashSet<Guid> originalIds;

            await using (var setup = new AeroLinkDbContext(options))
            {
                await setup.Database.MigrateAsync();
                var program = new ProgramRecord("CQ07 PostgreSQL bounded program", "CQ7P");
                var project = new ProjectRecord(program.Id, "CQ07 PostgreSQL project", "Functional qualification");
                var release = new SoftwareRelease(project.Id, "1.0", false);
                setup.AddRange(program, project, release);
                await setup.SaveChangesAsync();
                setup.Add(LegacyDefaultProjectLadderFactory.Create(project.Id, now));
                await setup.SaveChangesAsync();

                var request = new SystemChangeRequest("SRCR-96704", 0, project.Id, release.Id,
                    "PostgreSQL bounded requirement edit", "Problem", "Analysis", "Solution", "author", now);
                for (var i = 1; i <= 501; i++)
                {
                    request.AddRequirementChange("author", $"SYSR-{i:D8}", 0, RequirementLevel.System,
                        RequirementChangeKind.Modify, $"Original statement {i}.", "Original rationale", "Test", now);
                }

                setup.Add(request);
                await setup.SaveChangesAsync();
                requestId = request.Id;
                originalIds = request.RequirementChanges.Select(x => x.Id).ToHashSet();
            }

            await using (var db = new AeroLinkDbContext(options))
            {
                var request = await db.SystemChangeRequests
                    .Include(x => x.RequirementChanges)
                    .Include(x => x.AuditEvents)
                    .SingleAsync(x => x.Id == requestId);
                foreach (var child in request.RequirementChanges.ToArray())
                {
                    request.RebaseRequirementChange("author", child.Id, 1,
                        $"Revised statement {child.BaseNumber}.", "Revised rationale", now.AddMinutes(1));
                }

                probe.Enabled = true;
                await db.SaveChangesAsync();
                probe.Enabled = false;
            }

            Assert.Equal(2, probe.RequirementChangeSelects);
            await using var verification = new AeroLinkDbContext(options);
            var saved = await verification.RequirementChanges.AsNoTracking()
                .Where(x => x.ChangeRequestId == requestId).ToListAsync();
            Assert.Equal(501, saved.Count);
            Assert.True(originalIds.SetEquals(saved.Select(x => x.Id)));
            Assert.All(saved, x => Assert.Equal(2, x.Revision));
        }
        finally
        {
            await DropDisposableDatabaseAsync(serverConnection, database);
        }
    }

    private static string QualificationServerConnectionOrThrow()
    {
        var raw = Environment.GetEnvironmentVariable(ConnectionVariable);
        if (string.IsNullOrWhiteSpace(raw))
            throw new InvalidOperationException(
                "CQ07 PostgreSQL qualification requires AEROLINK_MIGRATIONS_CONNECTION.");
        var builder = new NpgsqlConnectionStringBuilder(raw);
        var host = (builder.Host ?? string.Empty).Trim().Trim('[', ']');
        if (!string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(host, "127.0.0.1", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("CQ07 PostgreSQL qualification requires a loopback host.");
        if (builder.Port is < 55438 or > 55499)
            throw new InvalidOperationException(
                "CQ07 PostgreSQL qualification requires a disposable port in 55438-55499 and refuses 54329.");
        return raw;
    }

    private static async Task<string> CreateDisposableDatabaseAsync(string serverConnection, string database)
    {
        await using var admin = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(serverConnection)
        { Database = "postgres" }.ConnectionString);
        await admin.OpenAsync();
        await using var command = admin.CreateCommand();
        command.CommandText = $"CREATE DATABASE \"{database}\"";
        await command.ExecuteNonQueryAsync();
        return new NpgsqlConnectionStringBuilder(serverConnection) { Database = database }.ConnectionString;
    }

    private static async Task DropDisposableDatabaseAsync(string serverConnection, string database)
    {
        await using var admin = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(serverConnection)
        { Database = "postgres" }.ConnectionString);
        await admin.OpenAsync();
        await using var command = admin.CreateCommand();
        command.CommandText = $"DROP DATABASE IF EXISTS \"{database}\" WITH (FORCE)";
        await command.ExecuteNonQueryAsync();
    }

    private sealed class Cq07PostgresFactAttribute : FactAttribute
    {
        public Cq07PostgresFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(ConnectionVariable)))
                Skip = "CQ07 PostgreSQL qualification skipped: set AEROLINK_MIGRATIONS_CONNECTION to a disposable port in 55438-55499.";
        }
    }

    private sealed class SaveBoundaryPostgresCommandProbe : DbCommandInterceptor
    {
        public bool Enabled { get; set; }
        public int RequirementChangeSelects { get; private set; }

        private void Record(DbCommand command)
        {
            if (!Enabled || !command.CommandText.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase))
                return;
            if (command.CommandText.Contains("requirement_changes", StringComparison.OrdinalIgnoreCase))
                RequirementChangeSelects++;
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Record(command);
            return ValueTask.FromResult(result);
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            Record(command);
            return ValueTask.FromResult(result);
        }

        public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<object> result,
            CancellationToken cancellationToken = default)
        {
            Record(command);
            return ValueTask.FromResult(result);
        }
    }
}
