using System.Net;
using AeroLink.Domain.Programs;
using AeroLink.Domain.Verification;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;

namespace AeroLink.Infrastructure.Tests;

/// <summary>
/// Provider qualification for CQ-05. The two contexts are forced to read the missing disciplines before the
/// first writer is allowed to commit, so PostgreSQL's actual ReleaseId/Discipline unique constraint arbitrates
/// the race. The test is skipped unless the caller points it at the dedicated disposable server database.
/// </summary>
public sealed class BuildTestSetSeedingPostgresQualificationTests
{
    [DisposablePostgresFact]
    public async Task Concurrent_initializers_converge_and_the_loser_keeps_unrelated_tracked_work()
    {
        var server = ValidateServer(Environment.GetEnvironmentVariable("AEROLINK_MIGRATIONS_CONNECTION")!);
        var databaseName = $"aerolink_965_buildsets_{Guid.NewGuid():N}";
        await CreateDatabaseAsync(server, databaseName);
        try
        {
            var connection = new NpgsqlConnectionStringBuilder(server) { Database = databaseName }.ConnectionString;
            var options = new DbContextOptionsBuilder<AeroLinkDbContext>().UseNpgsql(connection).Options;
            await using (var seed = new AeroLinkDbContext(options))
            {
                await seed.Database.MigrateAsync();
                var program = new ProgramRecord("CQ-05 PG Program", "CQ05PG");
                var project = new ProjectRecord(program.Id, "CQ-05 PG Project", "CQ-05 PG Product");
                var release = new SoftwareRelease(project.Id, "9.5", false);
                seed.AddRange(program, project, release);
                await seed.SaveChangesAsync();
            }

            var barrier = new FirstWriterBarrier();
            await using var first = new AeroLinkDbContext(
                new DbContextOptionsBuilder<AeroLinkDbContext>().UseNpgsql(connection)
                    .AddInterceptors(barrier).Options);
            await using var second = new AeroLinkDbContext(
                new DbContextOptionsBuilder<AeroLinkDbContext>().UseNpgsql(connection)
                    .AddInterceptors(barrier).Options);
            var projectId = await first.Projects.AsNoTracking().Select(x => x.Id).SingleAsync();
            var releaseId = await first.Releases.AsNoTracking().Select(x => x.Id).SingleAsync();
            var unrelated = new ProgramRecord("CQ-05 loser pending", "CQ05LOSER");
            second.Programs.Add(unrelated);

            var firstTask = new BuildTestSetService(first).EnsureForReleaseAsync(projectId, releaseId);
            await barrier.FirstEntered.Task.WaitAsync(TimeSpan.FromSeconds(30));
            var secondTask = new BuildTestSetService(second).EnsureForReleaseAsync(projectId, releaseId);
            var results = await Task.WhenAll(firstTask, secondTask);

            Assert.All(results, sets => Assert.Equal(3, sets.Count));
            Assert.Equal(3, await first.BuildTestSets.AsNoTracking().CountAsync());
            Assert.Equal(EntityState.Added, second.Entry(unrelated).State);
            Assert.DoesNotContain(second.ChangeTracker.Entries<BuildTestSet>(), x => x.State == EntityState.Added);

            // The losing initializer did not discard the caller's unrelated mutation. It can be committed by
            // the caller after handling the successful convergence result.
            await second.SaveChangesAsync();
            await using var check = new AeroLinkDbContext(options);
            Assert.True(await check.Programs.AsNoTracking().AnyAsync(x => x.Id == unrelated.Id));
            Assert.Equal(3, await check.BuildTestSets.AsNoTracking().CountAsync(x => x.ReleaseId == releaseId));
        }
        finally
        {
            await DropDatabaseAsync(server, databaseName);
        }
    }

    private static string ValidateServer(string raw)
    {
        var builder = new NpgsqlConnectionStringBuilder(raw);
        var host = (builder.Host ?? string.Empty).Trim().Trim('[', ']');
        var loopback = string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
            || IPAddress.TryParse(host, out var address) && IPAddress.IsLoopback(address);
        if (!loopback) throw new InvalidOperationException("CQ-05 PostgreSQL qualification requires a loopback host.");
        if (builder.Port == 54329) throw new InvalidOperationException("CQ-05 qualification refuses port 54329.");
        return raw;
    }

    private static async Task CreateDatabaseAsync(string server, string database)
    {
        await using var connection = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(server)
        { Database = "postgres" }.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"CREATE DATABASE \"{database}\"";
        await command.ExecuteNonQueryAsync();
    }

    private static async Task DropDatabaseAsync(string server, string database)
    {
        await using var connection = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(server)
        { Database = "postgres" }.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"DROP DATABASE IF EXISTS \"{database}\" WITH (FORCE)";
        await command.ExecuteNonQueryAsync();
    }

    private sealed class FirstWriterBarrier : SaveChangesInterceptor
    {
        private readonly TaskCompletionSource<bool> _secondEntered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _firstCommitted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _first;
        private AeroLinkDbContext? _firstContext;

        public TaskCompletionSource<bool> FirstEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (eventData.Context is not AeroLinkDbContext context
                || !context.ChangeTracker.Entries<BuildTestSet>().Any(x => x.State == EntityState.Added))
                return result;

            if (Interlocked.CompareExchange(ref _first, 1, 0) == 0)
            {
                _firstContext = context;
                FirstEntered.TrySetResult(true);
                await _secondEntered.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
                return result;
            }

            _secondEntered.TrySetResult(true);
            await _firstCommitted.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
            return result;
        }

        public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result,
            CancellationToken cancellationToken = default)
        {
            if (ReferenceEquals(eventData.Context, _firstContext)) _firstCommitted.TrySetResult(true);
            return ValueTask.FromResult(result);
        }
    }

    private sealed class DisposablePostgresFactAttribute : FactAttribute
    {
        public DisposablePostgresFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("AEROLINK_MIGRATIONS_CONNECTION")))
                Skip = "CQ-05 PostgreSQL qualification skipped: set AEROLINK_MIGRATIONS_CONNECTION to the dedicated disposable database.";
        }
    }
}
