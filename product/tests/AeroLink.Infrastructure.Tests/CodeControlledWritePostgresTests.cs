using System.Diagnostics;
using System.Net;
using AeroLink.Domain.Baselines;
using AeroLink.Domain.Common;
using AeroLink.Domain.Integrations;
using AeroLink.Domain.Programs;
using AeroLink.Domain.Releases;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace AeroLink.Infrastructure.Tests;

/// <summary>
/// Provider qualification for the project-controlled write boundary. These tests use independent contexts and
/// observe PostgreSQL lock waits instead of relying on timing guesses. The database is always a random database on
/// the explicitly supplied disposable loopback server; the persistent developer database is rejected.
/// </summary>
public sealed class CodeControlledWritePostgresTests
{
    [DisposablePostgresFact]
    public async Task Same_project_scopes_serialize_while_different_projects_progress()
    {
        await WithDatabaseAsync(async (options, connectionString) =>
        {
            var fixture = await SeedAsync(options);
            await using var holder = new AeroLinkDbContext(options);
            await using var heldScope = await ProjectControlledWriteScope.AcquireAsync(holder, fixture.ProjectId);

            var sameStarted = NewCompletion<int>();
            var sameAcquired = NewCompletion();
            var sameRelease = NewCompletion();
            var same = HoldScopeAsync(options, fixture.ProjectId, sameStarted, sameAcquired, sameRelease);
            var samePid = await sameStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await WaitForLockWaitAsync(connectionString, samePid);

            var differentStarted = NewCompletion<int>();
            var differentAcquired = NewCompletion();
            var different = CommitScopeAsync(options, fixture.OtherProjectId, differentStarted, differentAcquired);
            await differentAcquired.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await different.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(different.IsCompletedSuccessfully);
            Assert.False(sameAcquired.Task.IsCompleted);

            sameRelease.TrySetResult();
            await heldScope.CommitAsync();
            await sameAcquired.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await Task.WhenAll(same, different);
        });
    }

    [DisposablePostgresFact]
    public async Task Rollback_after_source_rows_are_saved_leaves_no_selection_event_or_pointer()
    {
        await WithDatabaseAsync(async (options, _) =>
        {
            var fixture = await SeedAsync(options);
            await using (var db = new AeroLinkDbContext(options))
            {
                await using var scope = await ProjectControlledWriteScope.AcquireAsync(db, fixture.ProjectId);
                var configuration = await ReadConfigurationAsync(db, fixture.ProjectId);
                var selection = await SelectAsync(db, scope, fixture, configuration, expectedVersion: 0);
                Assert.NotEqual(Guid.Empty, selection.Id);
                await db.SaveChangesAsync();
                // Deliberately omit CommitAsync. Dispose rolls the saved rows back.
            }

            await using var verify = new AeroLinkDbContext(options);
            Assert.Empty(await verify.GitLabSourceSnapshots.AsNoTracking().ToListAsync());
            Assert.Empty(await verify.GitLabSourceSelectionEvents.AsNoTracking().ToListAsync());
            Assert.Empty(await verify.GitLabCurrentSourceSelections.AsNoTracking().ToListAsync());
        });
    }

    [DisposablePostgresFact]
    public async Task Expected_source_version_zero_has_one_winner_and_an_explicit_stale_refusal()
    {
        await WithDatabaseAsync(async (options, connectionString) =>
        {
            var fixture = await SeedAsync(options);
            await using var winnerDb = new AeroLinkDbContext(options);
            await using var winnerScope = await ProjectControlledWriteScope.AcquireAsync(winnerDb, fixture.ProjectId);
            var winnerConfiguration = await ReadConfigurationAsync(winnerDb, fixture.ProjectId);

            var loserStarted = NewCompletion<int>();
            var loserAcquired = NewCompletion();
            var loser = AttemptSelectionAsync(options, fixture, winnerConfiguration, loserStarted, loserAcquired);
            var loserPid = await loserStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await WaitForLockWaitAsync(connectionString, loserPid);

            await SelectAsync(winnerDb, winnerScope, fixture, winnerConfiguration, expectedVersion: 0);
            await winnerDb.SaveChangesAsync();
            await winnerScope.CommitAsync();

            await loserAcquired.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var loserFailure = await loser.WaitAsync(TimeSpan.FromSeconds(10));
            var stale = Assert.IsType<DomainException>(loserFailure);
            Assert.Contains("Another source selection was saved", stale.Message);

            await using var verify = new AeroLinkDbContext(options);
            Assert.Equal(1, await verify.GitLabSourceSelectionEvents.CountAsync());
            Assert.Equal(1, await verify.GitLabSourceSnapshots.CountAsync());
            var pointer = await verify.GitLabCurrentSourceSelections.SingleAsync();
            Assert.Equal(1, pointer.Version);
        });
    }

    [DisposablePostgresFact]
    public async Task Freeze_wins_after_wait_and_source_selection_refuses_the_frozen_release()
    {
        await WithDatabaseAsync(async (options, connectionString) =>
        {
            var fixture = await SeedAsync(options);
            await using var freezeDb = new AeroLinkDbContext(options);
            await using var freezeScope = await ProjectControlledWriteScope.AcquireAsync(freezeDb, fixture.ProjectId);
            var configuration = await ReadConfigurationAsync(freezeDb, fixture.ProjectId);

            var selectionStarted = NewCompletion<int>();
            var selectionAcquired = NewCompletion();
            var selection = AttemptSelectionAsync(options, fixture, configuration, selectionStarted, selectionAcquired);
            var selectionPid = await selectionStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await WaitForLockWaitAsync(connectionString, selectionPid);

            await BeginReviewAsync(freezeDb, fixture, "freeze-winner");
            await freezeDb.SaveChangesAsync();
            await freezeScope.CommitAsync();

            await selectionAcquired.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var failure = Assert.IsType<DomainException>(await selection.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Contains("frozen or released", failure.Message, StringComparison.OrdinalIgnoreCase);

            await using var verify = new AeroLinkDbContext(options);
            Assert.Empty(await verify.GitLabSourceSelectionEvents.ToListAsync());
            Assert.Equal(ReleaseCampaignState.InReview,
                await verify.ReleaseCampaigns.Where(x => x.Id == fixture.CampaignId).Select(x => x.State).SingleAsync());
        });
    }

    [DisposablePostgresFact]
    public async Task Source_wins_before_wait_and_freeze_observes_the_committed_exact_selection()
    {
        await WithDatabaseAsync(async (options, connectionString) =>
        {
            var fixture = await SeedAsync(options);
            await using var sourceDb = new AeroLinkDbContext(options);
            await using var sourceScope = await ProjectControlledWriteScope.AcquireAsync(sourceDb, fixture.ProjectId);
            var configuration = await ReadConfigurationAsync(sourceDb, fixture.ProjectId);
            var expectedSnapshot = await SelectAsync(sourceDb, sourceScope, fixture, configuration, expectedVersion: 0);
            await sourceDb.SaveChangesAsync();

            var freezeStarted = NewCompletion<int>();
            var freezeAcquired = NewCompletion();
            var freeze = ObserveSelectionAndFreezeAsync(options, fixture, freezeStarted, freezeAcquired);
            var freezePid = await freezeStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await WaitForLockWaitAsync(connectionString, freezePid);

            await sourceScope.CommitAsync();
            var observedSnapshot = await freeze.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(expectedSnapshot.SourceSnapshotId, observedSnapshot);
            Assert.True(freezeAcquired.Task.IsCompletedSuccessfully);

            await using var verify = new AeroLinkDbContext(options);
            Assert.Equal(ReleaseCampaignState.InReview,
                await verify.ReleaseCampaigns.Where(x => x.Id == fixture.CampaignId).Select(x => x.State).SingleAsync());
            var pointer = await verify.GitLabCurrentSourceSelections.SingleAsync();
            Assert.Equal(expectedSnapshot.SourceSnapshotId, pointer.SourceSnapshotId);
        });
    }

    private static async Task<Exception?> AttemptSelectionAsync(
        DbContextOptions<AeroLinkDbContext> options, Fixture fixture,
        ProjectRepositoryConfiguration observedConfiguration,
        TaskCompletionSource<int> started, TaskCompletionSource acquired)
    {
        await using var db = new AeroLinkDbContext(options);
        try
        {
            await db.Database.OpenConnectionAsync();
            started.TrySetResult(((NpgsqlConnection)db.Database.GetDbConnection()).ProcessID);
            await using var scope = await ProjectControlledWriteScope.AcquireAsync(db, fixture.ProjectId);
            acquired.TrySetResult();
            await new GitLabSourceSelectionService(db).SelectAsync(scope, fixture.ReleaseId, observedConfiguration,
                fixture.InstanceBaseUrl, Observation(fixture.ProjectId), Observation(fixture.ProjectId).Sha, 0,
                "source-contender", fixture.Now, CancellationToken.None);
            await db.SaveChangesAsync();
            await scope.CommitAsync();
            return null;
        }
        catch (Exception ex)
        {
            acquired.TrySetException(ex);
            return ex;
        }
    }

    private static async Task HoldScopeAsync(DbContextOptions<AeroLinkDbContext> options, Guid projectId,
        TaskCompletionSource<int> started, TaskCompletionSource acquired, TaskCompletionSource release)
    {
        await using var db = new AeroLinkDbContext(options);
        await db.Database.OpenConnectionAsync();
        started.TrySetResult(((NpgsqlConnection)db.Database.GetDbConnection()).ProcessID);
        await using var scope = await ProjectControlledWriteScope.AcquireAsync(db, projectId);
        acquired.TrySetResult();
        await release.Task.WaitAsync(TimeSpan.FromSeconds(15));
        await scope.CommitAsync();
    }

    private static async Task CommitScopeAsync(DbContextOptions<AeroLinkDbContext> options, Guid projectId,
        TaskCompletionSource<int> started, TaskCompletionSource acquired)
    {
        await using var db = new AeroLinkDbContext(options);
        await db.Database.OpenConnectionAsync();
        started.TrySetResult(((NpgsqlConnection)db.Database.GetDbConnection()).ProcessID);
        await using var scope = await ProjectControlledWriteScope.AcquireAsync(db, projectId);
        acquired.TrySetResult();
        await scope.CommitAsync();
    }

    private static async Task<Guid> ObserveSelectionAndFreezeAsync(
        DbContextOptions<AeroLinkDbContext> options, Fixture fixture,
        TaskCompletionSource<int> started, TaskCompletionSource acquired)
    {
        await using var db = new AeroLinkDbContext(options);
        await db.Database.OpenConnectionAsync();
        started.TrySetResult(((NpgsqlConnection)db.Database.GetDbConnection()).ProcessID);
        await using var scope = await ProjectControlledWriteScope.AcquireAsync(db, fixture.ProjectId);
        acquired.TrySetResult();
        var pointer = await db.GitLabCurrentSourceSelections.SingleAsync(x => x.ProjectId == fixture.ProjectId && x.ReleaseId == fixture.ReleaseId);
        await BeginReviewAsync(db, fixture, "freeze-observer");
        await db.SaveChangesAsync();
        await scope.CommitAsync();
        return pointer.SourceSnapshotId;
    }

    private static async Task<GitLabSourceSelectionEvent> SelectAsync(
        AeroLinkDbContext db, ProjectControlledWriteScope scope, Fixture fixture,
        ProjectRepositoryConfiguration observedConfiguration, long expectedVersion)
    {
        var observation = Observation(fixture.ProjectId);
        return await new GitLabSourceSelectionService(db).SelectAsync(scope, fixture.ReleaseId,
            observedConfiguration, fixture.InstanceBaseUrl, observation, observation.Sha, expectedVersion,
            "source-owner", fixture.Now, CancellationToken.None);
    }

    private static async Task<ProjectRepositoryConfiguration> ReadConfigurationAsync(
        AeroLinkDbContext db, Guid projectId) =>
        await db.ProjectRepositoryConfigurations.AsNoTracking().SingleAsync(x => x.ProjectId == projectId);

    private static async Task BeginReviewAsync(AeroLinkDbContext db, Fixture fixture, string actor)
    {
        var campaign = await db.ReleaseCampaigns.Include(x => x.Approvals).SingleAsync(x => x.Id == fixture.CampaignId);
        campaign.BeginReleaseReview(actor, [("approver", "Provider qualification approver")], new string('f', 64), fixture.Now);
        var persisted = await db.ReleaseApprovals.Where(x => x.CampaignId == campaign.Id).Select(x => x.Id).ToListAsync();
        foreach (var approval in campaign.Approvals.Where(x => !persisted.Contains(x.Id))) db.ReleaseApprovals.Add(approval);
    }

    private static GitLabCommitReference Observation(Guid _) =>
        new(17, "main", GitLabReferenceKind.Branch, new string('a', 40));

    private sealed record Fixture(Guid ProjectId, Guid OtherProjectId, Guid ReleaseId, Guid CampaignId,
        string InstanceBaseUrl, DateTimeOffset Now);

    private static TaskCompletionSource<T> NewCompletion<T>() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static TaskCompletionSource NewCompletion() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static async Task<Fixture> SeedAsync(DbContextOptions<AeroLinkDbContext> options)
    {
        var now = DateTimeOffset.UtcNow;
        await using var db = new AeroLinkDbContext(options);
        var program = new ProgramRecord("Provider race qualification", $"PRQ{Guid.NewGuid():N}"[..10]);
        var otherProgram = new ProgramRecord("Other provider race qualification", $"ORQ{Guid.NewGuid():N}"[..10]);
        var project = new ProjectRecord(program.Id, "Provider race project", "Synthetic code");
        var otherProject = new ProjectRecord(otherProgram.Id, "Other provider race project", "Synthetic code");
        var release = new SoftwareRelease(project.Id, "1.0", false);
        var baseline = new CandidateBaseline("SW-01.00", 0, project.Id, release.Id, null, "Provider race baseline", "qualification", now);
        var campaign = new ReleaseCampaign(project.Id, release.Id, baseline.Id, "Provider race campaign", "qualification", now);
        campaign.StartVerification("qualification", now);
        var repository = new ProjectRepositoryConfiguration(project.Id, ProjectRepositorySetupMode.ConnectNow,
            "GitLab", "https://gitlab.example/demo/code", "qualification", now);
        repository.RecordVerification("qualification", now, 17, "demo/code");
        db.AddRange(program, otherProgram, project, otherProject, release, baseline, campaign, repository);
        await db.SaveChangesAsync();
        return new(project.Id, otherProject.Id, release.Id, campaign.Id, "https://gitlab.example", now);
    }

    private static async Task WaitForLockWaitAsync(string connectionString, int pid)
    {
        await using var observer = new NpgsqlConnection(connectionString);
        await observer.OpenAsync();
        var deadline = Stopwatch.GetTimestamp() + Stopwatch.Frequency * 10;
        while (Stopwatch.GetTimestamp() < deadline)
        {
            await using var command = new NpgsqlCommand(
                "SELECT wait_event_type FROM pg_stat_activity WHERE pid = @pid", observer);
            command.Parameters.AddWithValue("pid", pid);
            var waitEvent = await command.ExecuteScalarAsync();
            if (string.Equals(waitEvent as string, "Lock", StringComparison.Ordinal)) return;
            await Task.Delay(25);
        }

        throw new TimeoutException($"PostgreSQL backend {pid} did not enter a lock wait within ten seconds.");
    }

    private static async Task WithDatabaseAsync(Func<DbContextOptions<AeroLinkDbContext>, string, Task> test)
    {
        var raw = Environment.GetEnvironmentVariable("AEROLINK_MIGRATIONS_CONNECTION")
            ?? throw new InvalidOperationException("Code qualification requires an explicit disposable PostgreSQL connection.");
        var server = new NpgsqlConnectionStringBuilder(raw);
        var host = (server.Host ?? string.Empty).Trim('[', ']');
        if (!(host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
              || IPAddress.TryParse(host, out var address) && IPAddress.IsLoopback(address))
            || server.Port != 55423 || string.IsNullOrWhiteSpace(server.Username))
            throw new InvalidOperationException("Provider qualification requires loopback PostgreSQL on disposable port 55423.");

        var database = $"aerolink_1023_scope_{Guid.NewGuid():N}";
        server.Database = "postgres";
        await using var administrator = new NpgsqlConnection(server.ConnectionString);
        await administrator.OpenAsync();
        var created = false;
        try
        {
            await using (var create = new NpgsqlCommand($"CREATE DATABASE \"{database}\"", administrator))
                await create.ExecuteNonQueryAsync();
            created = true;
            server.Database = database;
            var options = new DbContextOptionsBuilder<AeroLinkDbContext>().UseNpgsql(server.ConnectionString).Options;
            await using (var migrate = new AeroLinkDbContext(options))
            {
                await migrate.Database.MigrateAsync();
                await migrate.Database.MigrateAsync();
            }
            await test(options, server.ConnectionString);
        }
        finally
        {
            if (created)
            {
                server.Database = "postgres";
                await using var drop = new NpgsqlCommand($"DROP DATABASE \"{database}\" WITH (FORCE)", administrator);
                await drop.ExecuteNonQueryAsync();
            }
        }
    }

    private sealed class DisposablePostgresFactAttribute : FactAttribute
    {
        public DisposablePostgresFactAttribute()
        {
            var required = Environment.GetEnvironmentVariable("AEROLINK_REQUIRE_POSTGRES_QUALIFICATION");
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("AEROLINK_MIGRATIONS_CONNECTION"))
                && (string.IsNullOrWhiteSpace(required) || required.Equals("false", StringComparison.OrdinalIgnoreCase)))
                Skip = "Set AEROLINK_MIGRATIONS_CONNECTION to an owned loopback PostgreSQL server on port 55423.";
        }
    }
}
